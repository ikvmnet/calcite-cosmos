using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Internal;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Translates Calcite <see cref="RexNode"/> expressions into Cosmos SQL scalar expression text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Field references are resolved through an ordinal-indexed list of <see cref="CosmosPath"/>
    /// supplied by the caller, which knows how the input row type maps onto document paths.
    /// Literals are bound as parameters rather than inlined, so the generated text never varies
    /// with the data.
    /// </para>
    /// <para>
    /// Anything without a faithful Cosmos equivalent raises <see cref="CosmosTranslationException"/>.
    /// Declining is the correct outcome — the operator is then evaluated by Calcite in-process.
    /// An operator is only supported here when its Cosmos semantics are known to match; where
    /// they differ subtly (see remarks on individual cases) it is deliberately refused rather
    /// than approximated.
    /// </para>
    /// <para>
    /// Binary operations are fully parenthesized. Cosmos accepts redundant parentheses, and
    /// relying on them removes any dependence on its operator precedence table.
    /// </para>
    /// </remarks>
    public sealed class CosmosRexTranslator
    {

        readonly IReadOnlyList<CosmosPath?> _fields;

        /// <summary>
        /// How each input field is read back, parallel to <see cref="_fields"/> and empty where the
        /// caller did not say. What it adds to the path is whether a field is a rendering — see
        /// <see cref="IsTextRendering"/>.
        /// </summary>
        readonly IReadOnlyList<CosmosReading> _readings;

        /// <summary>
        /// The correlation variable whose row is the one <see cref="_fields"/> describes, where the
        /// caller has one.
        /// </summary>
        readonly org.apache.calcite.rel.core.CorrelationId? _ownRow;

        /// <summary>
        /// Whether a scoring function is currently legal, which is only while rendering a rank clause.
        /// </summary>
        bool _scoring;
        readonly CosmosParameterList _parameters;
        readonly RexBuilder _rexBuilder;

        /// <summary>
        /// What the container declares, where the caller knows which container this is written
        /// against.
        /// </summary>
        readonly Metadata.CosmosContainerMetadata? _container;
        JsonFactSet? _facts;

        /// <summary>
        /// Gets what is known of the documents this expression is evaluated over: the facts the
        /// caller's licence admits, or where it named none, what the container declares of every
        /// document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The one place a translation asks what is true.</b> Whether a rendering is the value it
        /// renders, whether an equality over text is exact, whether an ordering over it is, whether a
        /// typed field is the stored value: each is "does the path hold this type, or null, here", and
        /// each is asked of these facts and nothing else (<see cref="IsDeclared"/>). Which facts those
        /// are is the licence's, chosen by the caller once — for a filter's conjunct, the stratum before
        /// the one that admitted it (<see cref="Metadata.CosmosFilterStatement"/>), which never holds
        /// what the conjunct establishes, so no comparison certifies itself (#117).
        /// </para>
        /// <para>
        /// The declaration is what a consumer that names no licence may read (L0): it holds of every
        /// document whatever reads it. Derived the first time it is asked, as most translations ask
        /// nothing.
        /// </para>
        /// </remarks>
        internal JsonFactSet Facts => _facts ??= _container is null ? JsonFactSet.Empty : CosmosOracle.For(_container.Facts).Declared;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rexBuilder">Used to expand <c>SEARCH</c> nodes back into comparison trees.</param>
        /// <param name="fields">Maps input field ordinals onto document paths.</param>
        /// <param name="parameters">Receives bound literal values.</param>
        /// <param name="ownRow">
        /// The correlation variable that stands for the very row <paramref name="fields"/> describes,
        /// where one is in scope. A lateral traversal is correlated on its own input, so its array
        /// expression addresses this input through such a variable; nothing else does.
        /// </param>
        /// <param name="container">
        /// What the container declares, where the caller knows it. Only the vector and the geography
        /// functions consult it — see <see cref="DeclaresVector"/> and <see cref="RequireGeographyReading"/>
        /// — and a caller with nothing to translate but a path may leave it out. Where it is <c>null</c>
        /// those functions render as they did before this was read, which is what keeps a caller that
        /// only resolves paths from having to supply one. The full text functions do not consult it:
        /// what the container declares about their path is a cost, read by the nodes, and not a
        /// legality — see <see cref="WriteFullTextPredicate"/>.
        /// </param>
        /// <param name="readings">
        /// How each field in <paramref name="fields"/> is read back, where the caller knows. A field
        /// bound to a document path and read as text is the rendering of the value there rather than
        /// the value, and the comparisons hold it to the tests they hold the accessor itself to — see
        /// <see cref="IsTextRendering"/>. Where it is <c>null</c> or shorter than the binding every
        /// field not covered is read as its declared type, which is what a scan's fields are.
        /// </param>
        /// <param name="facts">
        /// What is known of the documents this expression will be evaluated over, which a rewrite may
        /// lean on: that a path holds a string, that it is always there, that its text spells a value
        /// of some other type. Which facts a caller may pass is its licence's — a filter's conjunct
        /// those of the stratum before its own, never its own — so the decision is the caller's and not
        /// this one's. Where it is <c>null</c>, what the container declares of every document, and
        /// nothing where there is no container.
        /// </param>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        public CosmosRexTranslator(RexBuilder rexBuilder, IReadOnlyList<CosmosPath?> fields, CosmosParameterList parameters, org.apache.calcite.rel.core.CorrelationId? ownRow = null, Metadata.CosmosContainerMetadata? container = null, IReadOnlyList<CosmosReading>? readings = null, JsonFactSet? facts = null)
        {
            _rexBuilder = rexBuilder ?? throw new ArgumentNullException(nameof(rexBuilder));
            _fields = fields ?? throw new ArgumentNullException(nameof(fields));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _ownRow = ownRow;
            _container = container;
            _readings = readings ?? Array.Empty<CosmosReading>();
            _facts = facts;
        }

        /// <summary>
        /// Determines whether a correlation variable stands for this input's own row.
        /// </summary>
        /// <remarks>
        /// The distinction matters because the two cases are the same shape of expression and mean
        /// opposite things. A lateral traversal correlates an input on itself, so
        /// <c>JSON_QUERY($cor0.DOC, '$.tags')</c> under one denotes a path of the document being scanned. A join
        /// correlates it on the <em>other</em> side, and there the identical expression denotes a
        /// value of a row this statement knows nothing about — which would resolve against these
        /// bindings to a plausible, wrong document path. Where the caller has not said which variable
        /// is its own, none is.
        /// </remarks>
        bool IsOwnRow(RexCorrelVariable variable)
        {
            return _ownRow is not null && _ownRow.equals(variable.id);
        }

        /// <summary>
        /// Attempts to translate <paramref name="node"/>.
        /// </summary>
        /// <remarks>
        /// Parameters bound before a failure remain in the list. Callers that may discard the
        /// result should translate into a throwaway <see cref="CosmosParameterList"/> and merge
        /// on success.
        /// </remarks>
        /// <param name="node">The expression to translate.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the expression was translated; otherwise <c>false</c>.</returns>
        public bool TryTranslate(RexNode node, out string? expression)
        {
            try
            {
                expression = Translate(node);
                return true;
            }
            catch (CosmosTranslationException)
            {
                expression = null;
                return false;
            }
        }

        /// <summary>
        /// Determines whether an expression denotes a document path, and if so what that path is.
        /// </summary>
        /// <remarks>
        /// Some clauses require a path rather than an arbitrary expression — <c>ORDER BY</c> keys
        /// must be resolvable against the container's indexes, for instance. Only a field
        /// reference or a chain of constant <c>ITEM</c> accessors over one qualifies.
        /// </remarks>
        /// <param name="node">The expression to inspect.</param>
        /// <param name="path">On success, the resolved path.</param>
        /// <returns><c>true</c> if the expression denotes a path; otherwise <c>false</c>.</returns>
        public bool TryResolvePath(RexNode node, out CosmosPath? path)
        {
            switch (node)
            {
                // A null binding is a field with no document path — a computed projection — and does
                // not resolve. The caller declines the operator, exactly as it would for an ordinal
                // that was never bound.
                case RexInputRef inputRef when inputRef.getIndex() >= 0 && inputRef.getIndex() < _fields.Count:
                    path = _fields[inputRef.getIndex()];
                    return path is not null;

                // A field of a correlation variable standing for this input's own row addresses that
                // row, so it resolves against the same bindings as a plain field reference. This is
                // how the array expression of a lateral unnest arrives. A variable standing for
                // anything else is refused — see IsOwnRow.
                case RexFieldAccess access when access.getReferenceExpr() is RexCorrelVariable variable && IsOwnRow(variable)
                    && access.getField().getIndex() >= 0 && access.getField().getIndex() < _fields.Count:
                    path = _fields[access.getField().getIndex()];
                    return path is not null;

                case RexCall call when KindOf(call) == SqlKind.__Enum.ITEM && call.getOperands().size() == 2:
                    if (TryResolvePath(Operand(call, 0), out var basePath) == false || Operand(call, 1) is not RexLiteral accessor)
                        break;

                    object? value;
                    try
                    {
                        value = GetLiteralValue(accessor);
                    }
                    catch (CosmosTranslationException)
                    {
                        break;
                    }

                    if (value is string name)
                    {
                        path = basePath!.Property(name);
                        return true;
                    }

                    if (TryGetArrayIndex(value, out var index))
                    {
                        path = basePath!.Index(index);
                        return true;
                    }

                    break;

                // A SQL/JSON accessor addresses a document path: `JSON_VALUE(<doc>, '$.a.b')` resolves
                // to `c.a.b`, and every clause that requires a path accepts it. The document is the
                // `DOC` column, which binds to the root.
                case RexCall json when IsJsonAccessor(json) && TryResolveJsonPath(json, out path):
                    return true;

                // `StringToArray(JSON_QUERY(<doc>, '$.tags'))` is the array at that path, and the
                // composition exists because UNNEST will not take a string: `JSON_QUERY` is typed
                // VARCHAR and is refused, while `StringToArray` is typed ANY and is accepted.
                case RexCall array when IsArrayFromJson(array) && TryResolvePath((RexNode)array.getOperands().get(0), out path):
                    return true;
            }

            path = null;
            return false;
        }

        /// <summary>
        /// Drops a cast to <c>VARCHAR</c> that converts nothing.
        /// </summary>
        /// <remarks>
        /// A view over a container used to have to cast, the row model typing every path <c>ANY</c>.
        /// Through the document column it does not: <c>JSON_VALUE</c> is already <c>VARCHAR</c>. A
        /// cast written over one anyway is an identity, and dropping it is what lets the accessor
        /// underneath be rendered as itself.
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns>The expression, or the value underneath a redundant cast.</returns>
        internal static RexNode StripRedundantTextCast(RexNode node)
        {
            if (node is not RexCall call || call.getOperands().size() != 1)
                return node;

            var kind = KindOf(call);
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return node;

            var type = call.getType();
            if (type?.getSqlTypeName() != SqlTypeName.VARCHAR || type.getPrecision() != org.apache.calcite.rel.type.RelDataType.PRECISION_NOT_SPECIFIED)
                return node;

            var operand = (RexNode)call.getOperands().get(0);
            return operand is RexCall inner && IsJsonAccessor(inner) ? operand : node;
        }

        /// <summary>
        /// Determines whether a call is one of the SQL/JSON accessors this can render as a path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// By name rather than by kind, for the reason recorded on <c>CosmosOperators</c>: a call
        /// resolved through a schema carries an operator Calcite built around the declaration rather
        /// than the operator itself.
        /// </para>
        /// <para>
        /// <c>JSON_VALUE</c> and <c>JSON_QUERY</c> only. <c>JSON_EXISTS</c> is a predicate rather than
        /// an accessor and is rendered as <c>IS_DEFINED</c> where it appears; the modifying family is
        /// not an accessor at all.
        /// </para>
        /// <para>
        /// This is the single gate every consumer of a path goes through — a projection, a filter, a
        /// partition key, a traversal — so refusing a spelling here refuses it everywhere, which is why
        /// <see cref="IsArrayReturningJsonValue"/> and <see cref="IsParsingJsonValue"/> are asked at
        /// this level rather than at any one of them.
        /// </para>
        /// </remarks>
        static bool IsJsonAccessor(RexCall call)
        {
            if (call.getOperator().getName() == "JSON_VALUE")
            {
                // A behaviour clause substitutes something the path does not hold, and which documents
                // it substitutes for is Calcite's to say -- measured, not the ones the standard names.
                // See IsPlainJsonValue.
                if (IsPlainJsonValue(call) == false)
                    return false;

                // A RETURNING that parses is a conversion of what the path holds and not the value, so
                // it addresses no path in any clause either -- see IsParsingJsonValue.
                if (IsParsingJsonValue(call))
                    return false;

                // An array RETURNING on JSON_VALUE is not a construct SQL defines, so it addresses no
                // path in any clause -- see IsArrayReturningJsonValue.
                return IsArrayReturningJsonValue(call) == false;
            }

            // A wrapper or a behaviour clause substitutes something the path does not hold -- measured,
            // WITH UNCONDITIONAL ARRAY WRAPPER over the string `bikes` answers ["bikes"] and
            // EMPTY OBJECT ON ERROR answers {} -- so only the plain form is the path it addresses.
            return IsPlainJsonQuery(call);
        }

        /// <summary>
        /// Determines whether an expression is a <c>JSON_VALUE</c> whose <c>RETURNING</c> names an
        /// array type — a spelling that is refused rather than rendered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>SQL restricts <c>JSON_VALUE</c>'s <c>RETURNING</c> to a predefined scalar type</b> and
        /// gives <c>JSON_QUERY</c> for structure. So this is not a construct the standard defines and
        /// Calcite implements badly — it is a spelling Calcite happens to accept and then answers by
        /// its own lights: measured, null over an array under the default <c>NULL ON ERROR</c> and a
        /// raw cast failure over a scalar. Rendering it would invent a meaning, and invent it only
        /// here, so the same query moved off this adapter would silently answer differently.
        /// </para>
        /// <para>
        /// <b>Refused in every clause rather than in the projection alone.</b> The first version of
        /// this refusal declined the column and left the traversal pushing, which put the divergence
        /// somewhere else instead of removing it: <c>UNNEST</c> of a null array yields no rows in
        /// process, so a pushed <c>JOIN t0 IN c.tags</c> answered rows where the engine answers none.
        /// One expression must not mean two things — that is what #119 was filed about — and the only
        /// way to hold that is for the spelling to address no path at all.
        /// </para>
        /// <para>
        /// A caller wanting the array writes <c>JSON_QUERY</c>, which SQL permits the clause on and
        /// which measurement shows Calcite implements: it answers the array in process, unnests to the
        /// right rows, and renders to the same path here — so it agrees with itself pushed or not.
        /// </para>
        /// </remarks>
        internal static bool IsArrayReturningJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            var name = call.getType()?.getSqlTypeName();
            return name == SqlTypeName.ARRAY || name == SqlTypeName.MULTISET;
        }

        /// <summary>
        /// Determines whether an expression is a <c>JSON_VALUE</c> whose <c>RETURNING</c> parses the
        /// value it reads rather than asserting its type — <c>DECIMAL</c>, of any precision and scale.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every other numeric or boolean <c>RETURNING</c> asserts, and this one converts.</b>
        /// Measured against Calcite's runtime
        /// (<c>CalciteJsonValueMeasurementTests.EachNumericOrBooleanReturningAssertsItsClassButDecimal</c>):
        /// the others cast the value the JSON reader built to one Java class and raise on any other, so
        /// <c>INTEGER</c> answers a stored <c>30</c> and raises on <c>"30"</c>, on <c>30.0</c> and on a
        /// whole number past its range; <c>BIGINT</c> answers only a whole number past an
        /// <c>INTEGER</c>'s, <c>DOUBLE</c> and <c>FLOAT</c> only a number written with a point or an
        /// exponent, <c>SMALLINT</c>, <c>TINYINT</c> and <c>REAL</c> nothing at all, and <c>BOOLEAN</c>
        /// only a stored boolean. <c>DECIMAL</c> reads a stored number as its exact value and a stored
        /// string as the number its trimmed text parses as, so <c>"2"</c>, <c>" 2 "</c> and
        /// <c>"2.0"</c> are each 2 to it; it raises only on what does not parse, a boolean and
        /// <c>"x"</c> among them, and no <c>ON ERROR</c> clause catches that.
        /// </para>
        /// <para>
        /// <b>So it is not the value at the path, and the service has nothing that is.</b> Written as
        /// the path, <c>= 2</c> pushed as <c>c.body = 2</c> lost every document storing <c>"2"</c>;
        /// read as the value it was, the fact extractor took the same equality to say the path holds
        /// the number 2, and over a path the container declares a string rewrote the filter to
        /// <c>FALSE</c>. Even over numbers alone the value is not the service's: a fraction is the
        /// double's exact binary expansion, so a stored <c>0.1</c> under <c>RETURNING DECIMAL(10, 1)</c>
        /// is not equal to <c>0.1</c> and is greater than it, where the service's doubles are equal.
        /// And the literal it is compared with is cast to the comparison's scale, which against the
        /// default <c>DECIMAL(19, 0)</c> truncates it: <c>= 2.5</c> keeps a stored 2.
        /// </para>
        /// <para>
        /// <b>It is the cast of the rendering, written as a clause</b>, and is read as one:
        /// <see cref="TryNumericCastOperand(RexNode, RexBuilder)"/> takes it, so a comparison through it
        /// pushes the window it puts a stored number in and is rechecked above, exactly as
        /// <c>CAST(JSON_VALUE(…) AS INTEGER)</c> is. Refused as a path
        /// everywhere else — <see cref="IsJsonAccessor"/> — so a projection, a sort key, a grouping key,
        /// a partition key and a fact all stay where Calcite computes them.
        /// </para>
        /// </remarks>
        internal static bool IsParsingJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            return call.getType()?.getSqlTypeName() == SqlTypeName.DECIMAL;
        }

        /// <summary>
        /// Determines whether a call is <c>StringToArray</c> over a SQL/JSON accessor, which addresses
        /// an array in the document rather than parsing one out of a string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Neither call is rendered. The path already holds the array, so the service needs no
        /// conversion — and would refuse the one written down, Cosmos's <c>StringToArray</c> taking a
        /// string and being <c>undefined</c> over an array. Eliding it is therefore required for the
        /// statement to run at all, not merely cheaper.
        /// </para>
        /// <para>
        /// Restricted to an accessor operand rather than admitted over anything that resolves. A
        /// caller writing <c>StringToArray</c> over a path that genuinely holds a string means the
        /// service's function and means it to run; only the composition with an accessor names an
        /// array, and only that one is elided.
        /// </para>
        /// </remarks>
        static bool IsArrayFromJson(RexCall call)
        {
            return string.Equals(call.getOperator().getName(), "StringToArray", StringComparison.Ordinal)
                && call.getOperands().size() == 1
                && call.getOperands().get(0) is RexCall inner
                && IsJsonAccessor(inner);
        }

        /// <summary>
        /// Resolves a SQL/JSON accessor to the document path it addresses.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The path argument must be a literal, for the reason the full text functions hold their first
        /// argument to one: a path assembled at run time names a property this cannot know, so there is
        /// nothing to render and the call is declined rather than guessed at.
        /// </para>
        /// <para>
        /// Anything beyond the plain subset — a wildcard, a descent, a filter, a function — is declined
        /// too. Cosmos addresses a property or an array element and nothing else, so a path it cannot
        /// express has no rendering, and answering one differently would be worse than not answering it.
        /// </para>
        /// <para>
        /// Trailing operands are the <c>RETURNING</c>, <c>ON EMPTY</c> and <c>ON ERROR</c> flags.
        /// Nothing is done with them here: the type they declare is what the plan already says the
        /// column is. What a column makes of a document holding another type is the projection's to
        /// say, which writes a typed accessor through its type's test — see <see cref="ReadingTests"/>
        /// — and a field bound to one is the stored value only where the container declares the type —
        /// see <see cref="IsReadAsStored"/>. That holds of a clause that asserts its type, and
        /// <c>DECIMAL</c> does not: it parses a stored string, so a document storing <c>"2"</c> agrees
        /// with it in process and disagrees with the path. <see cref="IsJsonAccessor"/> refuses it
        /// before this is asked — see <see cref="IsParsingJsonValue"/>.
        /// </para>
        /// </remarks>
        bool TryResolveJsonPath(RexCall call, out CosmosPath? path)
        {
            path = null;

            // The document an accessor addresses through has to be a document. A scalar rendering is
            // text, which Calcite parses as JSON to read the path out of, and the service has the
            // value: measured over products, `JSON_VALUE(JSON_VALUE(DOC, '$.name'), '$') IS NOT NULL`
            // resolved to c.name and kept every document, where Calcite fails to parse
            // `Trail Blazer` and answers null for each. A path stored as a JSON string is the case it
            // means, and there the service's nested path is undefined where Calcite reads a value.
            // The document column and a plain JSON_QUERY are what remain, the fragment's text parsing
            // back to the fragment the nested path addresses.
            if (IsScalarRendering(Operand(call, 0)))
                return false;

            if (TryResolvePath(Operand(call, 0), out var basePath) == false || basePath is null)
                return false;

            if (Operand(call, 1) is not RexLiteral literal)
                return false;

            object? value;
            try
            {
                value = GetLiteralValue(literal);
            }
            catch (CosmosTranslationException)
            {
                return false;
            }

            return value is string text && TryExtendByJsonPath(basePath, text, out path);
        }

        /// <summary>
        /// Extends a path by a SQL/JSON path expression, where it is one Cosmos can address.
        /// </summary>
        /// <remarks>
        /// The accepted grammar is <c>$</c> followed by any number of <c>.name</c>, <c>['name']</c> and
        /// <c>[0]</c> steps. That is the whole of what a document path is; everything else is refused.
        /// </remarks>
        /// <param name="basePath">The path the document itself is at.</param>
        /// <param name="text">The SQL/JSON path expression.</param>
        /// <param name="path">On success, the resolved path.</param>
        /// <returns><c>true</c> where the expression is a plain path.</returns>
        internal static bool TryExtendByJsonPath(CosmosPath basePath, string text, out CosmosPath? path)
        {
            path = null;

            if (basePath is null || string.IsNullOrEmpty(text) || text[0] != '$')
                return false;

            var current = basePath;
            var i = 1;

            while (i < text.Length)
            {
                if (text[i] == '.')
                {
                    // A descent, '..', names every match at any depth and is not a path.
                    var start = ++i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;

                    if (i == start)
                        return false;

                    current = current.Property(text[start..i]);
                    continue;
                }

                if (text[i] != '[')
                    return false;

                var close = text.IndexOf(']', ++i);
                if (close < 0)
                    return false;

                var step = text[i..close];
                i = close + 1;

                if (step.Length >= 2 && (step[0] == '\'' && step[^1] == '\'' || step[0] == '"' && step[^1] == '"'))
                {
                    var name = step[1..^1];
                    if (name.Length == 0 || name.Contains('\'') || name.Contains('"'))
                        return false;

                    current = current.Property(name);
                    continue;
                }

                if (int.TryParse(step, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) == false)
                    return false;

                current = current.Index(index);
            }

            path = current;
            return true;
        }

        /// <summary>
        /// Translates <paramref name="node"/> as a predicate, whose truth alone is read.
        /// </summary>
        /// <remarks>
        /// What a <c>WHERE</c> is, and a join condition and a traversal's filter: a row is kept where
        /// the predicate is true, and false and unknown both discard it. That is what lets a comparison
        /// written here be false where SQL's is unknown — see <see cref="WriteComparison"/>. Anything
        /// whose value is read rather than its truth — a column, a sort key, an argument — goes through
        /// <see cref="TranslateValue"/> instead.
        /// </remarks>
        /// <param name="node">The expression to translate.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="node"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosTranslationException">The expression has no Cosmos equivalent.</exception>
        public string Translate(RexNode node) => TranslateAs(node, value: false);

        /// <summary>
        /// Translates <paramref name="node"/> as a value, whose SQL null has to survive.
        /// </summary>
        /// <remarks>
        /// A projected column, and anything else whose value is read. A predicate written here is held
        /// to more than one in a <c>WHERE</c> is: unknown and false are different values, and a
        /// comparison that answers one where SQL answers the other is declined — see
        /// <see cref="_value"/>.
        /// </remarks>
        /// <param name="node">The expression to translate.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="node"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosTranslationException">The expression has no Cosmos equivalent.</exception>
        public string TranslateValue(RexNode node) => TranslateAs(node, value: true);

        /// <inheritdoc cref="TranslateValue(RexNode)" />
        /// <param name="node">The expression to translate.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the expression was translated; otherwise <c>false</c>.</returns>
        public bool TryTranslateValue(RexNode node, out string? expression)
        {
            try
            {
                expression = TranslateValue(node);
                return true;
            }
            catch (CosmosTranslationException)
            {
                expression = null;
                return false;
            }
        }

        string TranslateAs(RexNode node, bool value)
        {
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            // Calcite aggressively rewrites comparison chains into SEARCH over a Sarg. Expanding
            // first keeps range and IN predicates pushable instead of uniformly declined.
            var expanded = RexUtil.expandSearch(_rexBuilder, null, node);

            RequireRenderingsReadAsRenderings(expanded, permitted: true);

            var saved = _value;
            _value = value;

            try
            {
                var builder = new StringBuilder();
                Write(builder, expanded);
                return builder.ToString();
            }
            finally
            {
                _value = saved;
            }
        }

        /// <summary>
        /// Refuses an expression that hands the rendering of a path to an operator as a value, where
        /// the path is not known to hold a string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A rendering is text, and the service has the value it renders.</b> <c>JSON_VALUE</c>
        /// read as text turns a stored number into <c>30</c> and a boolean into <c>true</c>; the path,
        /// guarded or not, hands the service the number and the boolean themselves. Wherever the value
        /// is used as a value — upper-cased, concatenated, measured, passed out through a
        /// <c>CASE</c> — the two answer differently: the service's string functions are undefined
        /// over a number, and a column the plan typed text comes back holding one. Measured against
        /// the emulator over the typed container, <c>UPPER(label)</c> and <c>label || '!'</c>
        /// projected null for the number and the boolean where Calcite projects <c>30</c> and
        /// <c>TRUE</c>; <c>UPPER(label) = 'TRUE'</c>, <c>UPPER(label) = '30'</c>,
        /// <c>CHAR_LENGTH(label) = 2</c> and <c>label || 'x' = '30x'</c> each lost the document
        /// Calcite keeps; and a <c>COALESCE</c> or a <c>CASE</c> passing the accessor out as a column
        /// failed the read at the boolean.
        /// </para>
        /// <para>
        /// <b>Where a rendering may be read, it is read as one.</b> A comparison reasons about which
        /// documents a rendering matches — see <see cref="WriteComparand"/> — and a null test asks only
        /// whether there is one — see <see cref="WriteIsNull"/>. <c>LIKE</c> refuses its rendering
        /// subject on its own terms. A cast that names the raw value, or that a stored boolean proves,
        /// says what it reads. The geography, full text, vector and type-test functions take the path
        /// as an address. Nothing else is told it is reading a rendering, so nothing else may, and a
        /// new operator is refused until it is.
        /// </para>
        /// <para>
        /// <b>A string is its own rendering.</b> Where what is known of the container says the path
        /// holds a string or nothing at all, the value the service hands on is the text Calcite
        /// renders, and every operator agrees over it. Not a <c>JSON_QUERY</c>, which renders the
        /// fragment and is null for a string: no fact makes its text the value.
        /// </para>
        /// <para>
        /// Asked once, of the whole expression, before anything is written, because the question is
        /// about the edge between an operator and its operand and not about either alone — and asking
        /// it in every writer is how <c>WriteLike</c> came to know it and nothing else did.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <param name="permitted">Whether what encloses the expression reads a rendering as one.</param>
        /// <exception cref="CosmosTranslationException">A rendering is read as a value.</exception>
        void RequireRenderingsReadAsRenderings(RexNode node, bool permitted)
        {
            if (IsRendering(node))
            {
                if (permitted == false && IsDeclaredString(node) == false)
                    throw new CosmosTranslationException("JSON_VALUE read as text is the rendering of the value at the path, and an operator over it reads the text: the service hands the operator the value, which renders differently wherever the path does not hold a string.");

                return;
            }

            if (node is not RexCall call)
                return;

            // An address rather than a value: an ITEM chain, an accessor with a scalar or array
            // RETURNING, an array read out of one.
            if (TryResolvePath(call, out _))
                return;

            for (var i = 0; i < call.getOperands().size(); i++)
                RequireRenderingsReadAsRenderings(Operand(call, i), ReadsRenderings(call, i, permitted));
        }

        /// <summary>
        /// Determines whether an expression is the rendering of a document value as text: a
        /// <c>JSON_VALUE</c> or a plain <c>JSON_QUERY</c> read as text, or a field bound to one.
        /// </summary>
        /// <remarks>
        /// <see cref="IsTextRendering"/> without the array <c>RETURNING</c> of <c>JSON_QUERY</c>, which
        /// is the array and not its text.
        /// </remarks>
        bool IsRendering(RexNode node)
        {
            if (IsCollectionJsonQuery(node))
                return false;

            return IsTextRendering(node);
        }


        /// <summary>
        /// Determines whether an expression renders a scalar: a <c>JSON_VALUE</c> read as text, or a
        /// field bound to one.
        /// </summary>
        /// <remarks>
        /// The renderings a fact about the stored value can make exact. A plain <c>JSON_QUERY</c> is a
        /// rendering too, of the fragment, and is null for every scalar — so a path the container
        /// declares a string is the one path it never renders, and a comparison over it never selects
        /// the documents the raw comparison does.
        /// </remarks>
        bool IsScalarRendering(RexNode node)
        {
            return IsTextJsonValue(node)
                || node is RexInputRef reference
                    && IsCharacter(reference)
                    && reference.getIndex() >= 0
                    && reference.getIndex() < _readings.Count
                    && _readings[reference.getIndex()] is CosmosReading.Text;
        }

        /// <summary>
        /// Determines whether an operator reads its operand, if that is a rendering, as one.
        /// </summary>
        /// <param name="call">The operator.</param>
        /// <param name="ordinal">The operand.</param>
        /// <param name="permitted">Whether what encloses the operator does, for an operator that hands its operand on unchanged.</param>
        bool ReadsRenderings(RexCall call, int ordinal, bool permitted)
        {
            switch (KindOf(call))
            {
                case SqlKind.__Enum.EQUALS:
                case SqlKind.__Enum.NOT_EQUALS:
                case SqlKind.__Enum.LESS_THAN:
                case SqlKind.__Enum.LESS_THAN_OR_EQUAL:
                case SqlKind.__Enum.GREATER_THAN:
                case SqlKind.__Enum.GREATER_THAN_OR_EQUAL:
                case SqlKind.__Enum.IS_NULL:
                case SqlKind.__Enum.IS_NOT_NULL:
                case SqlKind.__Enum.LIKE:
                    return true;

                case SqlKind.__Enum.CAST:
                case SqlKind.__Enum.SAFE_CAST:
                    // A marker says what it reads, and so does a cast a stored boolean proves. Any other
                    // cast hands its operand on to whatever encloses it, to be judged there: one that
                    // converts is refused by WriteCast, and one that boxes, asserts or renders hands
                    // the value through. That includes the box Calcite's coercion puts around a side
                    // of a comparison with a VARIANT, which the comparison sees through to the
                    // rendering underneath (WriteComparand), and the cast to text an equality drops,
                    // which is the rendering itself and declares no width (TryTextCastValue).
                    if (IsRawValueMarker(call) || TryStoredBoolean(call) is not null)
                        return true;

                    return permitted;
            }

            var name = call.getOperator().getName();

            return GeographyFunctions.Contains(name)
                || CosmosOperators.IsAbsenceObserving(call.getOperator())
                || name is "VECTORDISTANCE"
                    or "FULLTEXTCONTAINS" or "FULLTEXTCONTAINSALL" or "FULLTEXTCONTAINSANY" or "FULLTEXTSCORE"
                    or "CLR_FT_CONTAINS" or "CLR_FT_CONTAINS_ALL" or "CLR_FT_CONTAINS_ANY" or "CLR_FT_SCORE";
        }

        void Write(StringBuilder builder, RexNode node)
        {
            switch (node)
            {
                case RexInputRef inputRef:
                    WriteInputRef(builder, inputRef);
                    break;
                case RexLiteral literal:
                    WriteLiteral(builder, literal);
                    break;
                case org.apache.calcite.rex.RexDynamicParam parameter:
                    WriteDynamicParam(builder, parameter);
                    break;
                case RexCall call:
                    WriteCall(builder, call);
                    break;
                default:
                    throw new CosmosTranslationException($"Unsupported expression '{node.getKind().name()}' of type '{node.GetType().Name}'.");
            }
        }

        /// <summary>
        /// Writes a dynamic parameter as a bound parameter of the statement.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The plan knows the type, which is what it needs; the value belongs to the execution.</b>
        /// A prepared statement is compiled once and run many times, so <c>?</c> carries an ordinal
        /// rather than a value — and every decision made here is a decision about types. Whether a
        /// comparison is exact, whether a guard is needed, whether the path is a string: none of them
        /// asks what the value is. So a parameter is written exactly as a literal of its type would
        /// be, and the slot behind the name is filled in by
        /// <see cref="Client.CosmosQueries.Bind"/> when the statement runs.
        /// </para>
        /// <para>
        /// This is what a prepared statement is for, and declining it was expensive: a host that
        /// parameterises its filters — which Entity Framework does for every one — had the comparison
        /// weakened to a definedness test and rechecked in process, so an equality on <c>id</c> read
        /// the container whole rather than taking a point read.
        /// </para>
        /// </remarks>
        /// <param name="builder">The statement under construction.</param>
        /// <param name="parameter">The parameter to write.</param>
        void WriteDynamicParam(StringBuilder builder, org.apache.calcite.rex.RexDynamicParam parameter)
        {
            builder.Append(_parameters.Add(new CosmosDynamicValue(parameter.getIndex())));
        }

        void WriteInputRef(StringBuilder builder, RexInputRef inputRef)
        {
            var index = inputRef.getIndex();
            if (index < 0 || index >= _fields.Count)
                throw new CosmosTranslationException($"Field ordinal {index} is not bound to a document path.");

            // Unbound because the field is a computed projection. Cosmos cannot address one — a
            // projection alias is not visible to WHERE or ORDER BY — so the operator reading it is
            // declined and Calcite evaluates it in-process.
            var path = _fields[index]
                ?? throw new CosmosTranslationException($"Field ordinal {index} is a computed projection and has no document path.");

            path.WriteTo(builder);
        }

        void WriteLiteral(StringBuilder builder, RexLiteral literal)
        {
            // Bind rather than inline, so that statement text is independent of data.
            builder.Append(_parameters.Add(GetLiteralValue(literal)));
        }

        /// <summary>
        /// Converts a Calcite literal into the CLR value to bind.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Temporal literals are refused. Cosmos JSON has no date or time type — dates are ISO
        /// strings or epoch numbers by application convention, and nothing in the container
        /// declares which. Guessing an encoding would silently return wrong rows.
        /// </para>
        /// <para>
        /// So is an exact number the service would read as another whole number — see
        /// <see cref="TryGetExactNumberValue"/>.
        /// </para>
        /// </remarks>
        internal static object? GetLiteralValue(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();

            if (type == SqlTypeName.BOOLEAN)
                return ((java.lang.Boolean)literal.getValue()).booleanValue();

            if (type == SqlTypeName.CHAR || type == SqlTypeName.VARCHAR)
                return literal.getValue() is NlsString s ? s.getValue() : literal.getValue()?.ToString();

            // Calcite is not consistent about the boxed representation of numeric literals:
            // exact literals arrive as BigDecimal, while those built through makeApproxLiteral
            // arrive as java.lang.Double. Accept either rather than assuming one.
            if (type == SqlTypeName.TINYINT || type == SqlTypeName.SMALLINT || type == SqlTypeName.INTEGER || type == SqlTypeName.BIGINT)
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal bd => bd.longValueExact(),
                    java.lang.Long l => l.longValue(),
                    java.lang.Integer i => (long)i.intValue(),
                    var v => throw new CosmosTranslationException($"Unexpected representation '{v?.GetType().Name}' for an integer literal."),
                };
            }

            if (type == SqlTypeName.DECIMAL)
                return TryGetExactNumberValue((java.math.BigDecimal)literal.getValue())
                    ?? throw new CosmosTranslationException($"No value the service can be sent compares there as the number {literal.getValue()}.");

            if (type == SqlTypeName.FLOAT || type == SqlTypeName.REAL || type == SqlTypeName.DOUBLE)
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal bd => bd.doubleValue(),
                    java.lang.Double d => d.doubleValue(),
                    java.lang.Float f => (double)f.floatValue(),
                    var v => throw new CosmosTranslationException($"Unexpected representation '{v?.GetType().Name}' for an approximate literal."),
                };
            }

            // A geography the plan already holds, which is what a connection makes of a constant one:
            // its reduction evaluates CLR_ST_GEOG_GEOMFROMGEOJSON('…') while planning and leaves the
            // shape as a literal, where a bare planner leaves the call for WriteGeographyLiteral. Bound
            // as the GeoJSON object a geometry parameter is bound as (#156), so the folded constant and
            // the bound one reach the service in one spelling.
            if (type == SqlTypeName.GEOMETRY && literal.getValue() is org.locationtech.jts.geom.Geometry geometry)
                return Client.CosmosJson.ToGeoJsonValue(geometry);

            throw new CosmosTranslationException($"Unsupported literal type '{type.getName()}'.");
        }

        /// <summary>
        /// Converts an exact number into the CLR value the service compares as that number, or answers
        /// <c>null</c> where there is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A decimal reaches the service as a double.</b> The SDK writes one with a point, and the
        /// service reads a number written with one as a double. It keeps a stored integer as the integer
        /// it is, and compares a double with it exactly. Measured against the emulator over the
        /// differential corpus's <c>declared</c>, whose <c>v</c> holds 2^53, 2^53 + 1, 2^60 and 2^60 + 1:
        /// <c>RETURNING BIGINT = 1152921504606846977</c>, sent as a decimal, matched the document holding
        /// 2^60 and not the one holding the literal, and <c>= 9007199254740993</c> matched 2^53. A double
        /// that is the number is exact against either: <c>&gt; 1152921504606846976</c> keeps 2^60 + 1, and
        /// <c>= 9007199254740992</c> keeps 2^53 alone.
        /// </para>
        /// <para>
        /// <b>So a decimal stays wherever its double is right, and only the rest changes.</b> A double
        /// that is not the number lies within half a unit in its last place of it, and no stored value
        /// lies between the two but that double itself. Through a typed accessor Calcite tells that
        /// double from the number only where it is whole: <c>RETURNING DOUBLE</c> rounds the literal as
        /// the service does, and an integer is read exactly. So the decimal is wrong exactly where its
        /// double is a whole number other than the number — an integer beyond 2^53 a double does not
        /// hold, and a fraction a hair from a whole number, <c>2.9999999999999999</c>, which reads as 3.
        /// A whole one is sent as a long, which the service keeps as the integer it is. A fraction, and
        /// a whole number beyond a long, are sent as nothing, and the caller declines.
        /// </para>
        /// <para>
        /// The window a cast's comparison pushes asks this of each side, for the same reason:
        /// <see cref="Metadata.CosmosNumericWindow"/>. Loosened by one beyond 2^53, a bound sent as a
        /// double came back as the number it was loosened from, and excluded the document holding it.
        /// </para>
        /// </remarks>
        /// <param name="value">The number.</param>
        /// <returns>A <see cref="decimal"/> or a <see cref="long"/>, or <c>null</c>.</returns>
        internal static object? TryGetExactNumberValue(java.math.BigDecimal value)
        {
            var approximate = value.doubleValue();

            if (double.IsInfinity(approximate))
                return null;

            try
            {
                if (Math.Floor(approximate) != approximate || new java.math.BigDecimal(approximate).compareTo(value) == 0)
                    return BigDecimalConverter.ToDecimal(value);

                return value.longValueExact();
            }
            catch (Exception e) when (e is java.lang.ArithmeticException or OverflowException)
            {
                // A fraction or a number beyond a long, which longValueExact refuses; or one beyond a decimal.
                return null;
            }
        }

        /// <summary>
        /// Whether an accessor reached inside a larger expression carries the guard it would carry as
        /// a projection of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Set for the compound branch of <see cref="TranslateProjection"/> and nowhere else, which is
        /// exactly the scope of #131. A projection's result <em>is</em> the column, so an accessor in
        /// it has to answer what the accessor answers; a filter compares the raw value the service
        /// holds on purpose — see <see cref="WriteComparand"/> and
        /// <see cref="Rel.Convert.CosmosFilterSplitRule"/> — and is left alone.
        /// </para>
        /// <para>
        /// A field rather than a parameter because <see cref="Write"/> recurses through a dozen
        /// writers that have no business knowing about it, and the alternative is threading a flag
        /// through every one of them to be read in a single place.
        /// </para>
        /// </remarks>
        bool _guardNestedAccessors;

        /// <summary>
        /// Writes an accessor that sits inside a larger expression, with the guard its own projection
        /// would carry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The same guard, decided the same way, one level down.</b> Bare, the path hands the
        /// service's raw value to whatever encloses it, and the accessor's own meaning is lost:
        /// measured on main, <c>UPPER(JSON_VALUE(DOC, '$.a'))</c> rendered <c>UPPER(c.a)</c>, so a
        /// document holding an object at <c>$.a</c> — which the accessor answers null for — had the
        /// object handed to <c>UPPER</c> instead (#131).
        /// </para>
        /// <para>
        /// <b>The plain <c>JSON_QUERY</c> is refused rather than guarded</b>, because a guard is not
        /// what it needs. Its column is the fragment <em>as text</em>, which the reading produces by
        /// re-serialising what comes back — see <see cref="TryJsonQueryProjection"/> and
        /// <see cref="CosmosReading.JsonText"/>. Nested, there is no reading to do that: the enclosing
        /// operator would run at the service over the object itself, where Calcite runs it over the
        /// text. No rendering of the path fixes that, so the expression is declined and computed in
        /// process.
        /// </para>
        /// <para>
        /// <b>A scalar <c>RETURNING</c> is guarded by its type's test</b>, nested or alone — alone it
        /// reaches here too, nothing ahead of the compound branch of <see cref="TranslateProjection"/>
        /// taking it. It answers only where the path holds the type it names: an object and an array
        /// read null, as an absent path does, measured at Calcite's runtime
        /// (<c>CalciteJsonValueMeasurementTests</c>); a scalar of another type is the error SQL:2016
        /// answers null for under the default <c>NULL ON ERROR</c>, where Calcite raises instead
        /// (ikvmnet/calcite-dotnet#120). It used to stay bare, on the reading that a document of
        /// another type fails a column of its own either way, and for an object and an array the
        /// statement does not fail: the service sent the object, the reader refused it for the
        /// declared type, and a column, a grouping, a <c>DISTINCT</c> or a <c>COUNT</c> over such a
        /// path failed where the query answers — measured on the emulator over the differential
        /// suite's typed container.
        /// </para>
        /// <para>
        /// <b>Which guard each accessor takes is <see cref="ReadingTests"/>'s to say</b>, and is not
        /// said again here: the guard is the reading's null set, which the null test and a comparison's
        /// guard read from the same place. A date or a time is written without it, being admitted
        /// only over a declared stored form, where its guard would read null for nothing — see
        /// <see cref="RequireTheEngineCouldRead"/>. <c>DECIMAL</c> does not reach here. It parses a
        /// stored string that spells a number rather than asserting its class
        /// (<c>CalciteJsonValueMeasurementTests.EachNumericOrBooleanReturningAssertsItsClassButDecimal</c>),
        /// so no type test is where it answers, and it addresses no path — see
        /// <see cref="IsParsingJsonValue"/>.
        /// </para>
        /// </remarks>
        void WriteGuardedAccessor(StringBuilder builder, RexCall call, string rendered)
        {
            if (IsPlainJsonQuery(call) && IsCollectionJsonQuery(call) == false)
                throw new CosmosTranslationException("A JSON_QUERY inside an expression is the fragment as text, which the service has no way to produce -- the enclosing operator would run over the value instead. JSON_QUERY renders as a column of its own, where the reading writes the text.");

            // A date or a time is the one reading left bare. RequireTheEngineCouldRead admits it only
            // over a stored form the container declares, which is a string, so the path holds nothing
            // its guard would read null for -- and a guard costs a point read whose columns include one.
            if (Metadata.CosmosTemporalParse.PartsOf(call.getType()?.getSqlTypeName()) != Metadata.CosmosTemporalParts.None)
            {
                builder.Append(rendered);
                return;
            }

            if (ReadingTests(call) is { } tests)
            {
                builder.Append(Guarded(tests, rendered));
                return;
            }

            builder.Append(rendered);
        }

        /// <summary>
        /// The service's type tests, any one of which is true exactly where a reading of a path
        /// answers a value, for a reading that answers null for more than an absent path and a JSON
        /// null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What an accessor reads at a path is not what the path holds, and this is the one place
        /// that says how.</b> <c>JSON_VALUE</c> answers SQL null unless the value is a scalar — an
        /// object and an array read as null as surely as an absent path does — and a plain
        /// <c>JSON_QUERY</c> is its mirror, answering only for an object or an array. A typed
        /// <c>RETURNING</c> narrows the scalar to the type it names: SQL:2016 makes any other value
        /// an error that <c>NULL ON ERROR</c> answers null for, and Calcite raises there instead
        /// (ikvmnet/calcite-dotnet#120), so a value of another type is never one the engine answers
        /// a value for — it raises, and so does a stored integer under <c>RETURNING DOUBLE</c>,
        /// which the test admits (see <see cref="TypeTestOf"/>). A field a projection
        /// bound to an accessor reads as that accessor: its reading says which rendering, and its type
        /// which <c>RETURNING</c>.
        /// </para>
        /// <para>
        /// <b>Every writer that needs the line reads it from here</b>: the guard a projected accessor
        /// carries (<see cref="WriteGuardedAccessor"/>, <see cref="TryJsonValueProjection"/> and its
        /// siblings), the null test (<see cref="WriteIsNull"/>), and the guard a comparison takes in
        /// the position where SQL's unknown has to be restored (<see cref="WriteComparison"/>). Each
        /// of those used to state it, or not, for itself, and the null test and the negated equality
        /// were the two that did not: <c>IS NOT NULL</c> over an accessor at an object kept the
        /// document, and <c>NOT (… = 'bikes')</c> kept the object and the array.
        /// </para>
        /// <para>
        /// <c>null</c> for the raw value — a promoted column, a field typed as itself, an accessor the
        /// split rule re-typed <c>ANY</c> — which is null only where the path is absent or holds null,
        /// and which the null test and the guard already say in full.
        /// </para>
        /// </remarks>
        /// <param name="node">An expression that resolves to a path.</param>
        /// <returns>The tests, or <c>null</c> where the expression is the raw value.</returns>
        IReadOnlyList<SqlFunction>? ReadingTests(RexNode node)
        {
            if (IsCollectionJsonQuery(node))
                return ArrayReading;

            if (IsPlainJsonQuery(node))
                return FragmentReading;

            if (IsTextJsonValue(node))
                return ScalarReading;

            if (node is RexCall call && call.getOperator().getName() == "JSON_VALUE")
            {
                // Typed ANY by the split rule to name the raw value at the path rather than the
                // reading -- see IsDocumentValueType -- and the raw value is what that comparison is.
                if (IsDocumentValueType(call.getType()?.getSqlTypeName()))
                    return null;

                return TypeTestOf(call.getType()) is SqlFunction test ? new[] { test } : ScalarReading;
            }

            if (node is RexInputRef reference && reference.getIndex() >= 0 && reference.getIndex() < _readings.Count)
            {
                return _readings[reference.getIndex()] switch
                {
                    CosmosReading.Text when IsCharacter(reference) => ScalarReading,
                    CosmosReading.JsonText when IsCharacter(reference) => FragmentReading,
                    CosmosReading.Typed when TypedFieldTest(reference) is SqlFunction typed => new[] { typed },
                    _ => null,
                };
            }

            return null;
        }

        /// <summary>
        /// The type test a field read as declared answers a value under, or <c>null</c> where it is the
        /// raw value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A field a projection bound to a typed accessor is read as the type it names, and answers
        /// null where that accessor does: for every value of another type. The binding records the path
        /// and that the reading is typed, and the field's type is the accessor's — <c>RETURNING</c> is
        /// what types the call — so the test is the one <see cref="TypeTestOf"/> gives the accessor
        /// itself. Without it, <c>IS NOT NULL</c> over the field kept an object, and the guard under a
        /// <c>NOT</c> kept the object too, wherever a host left the filter above the projection rather
        /// than transposing it through; and a sort keyed by the field ordered the object the column
        /// reads null for — see <see cref="IsReadAsStored"/>, which asks it too.
        /// </para>
        /// <para>
        /// Nullable only: every other typed field bound to a path is the service's <c>id</c>,
        /// <c>_ts</c> or <c>_etag</c>, which is never null and has nothing to test, or a promoted
        /// <c>VARIANT</c> column, which is the raw value and has no test to give.
        /// </para>
        /// </remarks>
        /// <param name="reference">A field read as declared.</param>
        /// <returns>The test, or <c>null</c>.</returns>
        static SqlFunction? TypedFieldTest(RexInputRef reference)
        {
            return reference.getType()?.isNullable() == true ? TypeTestOf(reference.getType()) : null;
        }

        static readonly SqlFunction[] ScalarReading = [CosmosOperators.IsPrimitive];
        static readonly SqlFunction[] ArrayReading = [CosmosOperators.IsArray];
        static readonly SqlFunction[] FragmentReading = [CosmosOperators.IsObject, CosmosOperators.IsArray];

        /// <summary>
        /// The service's type test for the type a scalar <c>RETURNING</c> names, or <c>null</c>
        /// where it names none the service has a test for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One answer to which JSON type a typed accessor reads, for every writer that needs it</b>:
        /// the reading's tests, which the guard its column is written through, its null test and the
        /// restriction a comparison through one takes all read (<see cref="ReadingTests"/>), and
        /// whether a field bound to one is the value the service holds (<see cref="IsReadAsStored"/>).
        /// </para>
        /// <para>
        /// <c>JSON_VALUE(doc, '$.v' RETURNING INTEGER)</c> is a typed extraction, not a rendering: it
        /// participates only where the value is of that type. Calcite does not enforce that — it casts
        /// what it extracted and throws when the cast fails, outside the <c>ON ERROR</c> handling that
        /// would otherwise answer null. The type test is that restriction written where the service can
        /// apply it.
        /// </para>
        /// <para>
        /// By the call's type, not its operand count: <c>RETURNING</c> names the type of the call and
        /// adds no operand, so a bare accessor and a <c>RETURNING INTEGER</c> one differ only in what
        /// they are typed as. A character <c>RETURNING</c> is a rendering and is read as one; see
        /// <see cref="IsTextJsonValue"/>. And so by a field's type too, where a projection bound the
        /// field to such an accessor — the field is typed what the call is.
        /// </para>
        /// <para>
        /// <b>Not a conversion either.</b> <c>RETURNING DOUBLE</c> over a stored integer is a number,
        /// and <c>IS_NUMBER</c> admits it, where Calcite raises rather than widening: measured,
        /// <c>JSON_VALUE(DOC, '$.price' RETURNING DOUBLE) / 2</c> over products fails in process with
        /// an <c>Integer</c> that cannot be cast to a <c>Double</c>. The same assertion
        /// ikvmnet/calcite-dotnet#120 records for a string, met by a number of another numeric type,
        /// so over integer data such a statement has no oracle to agree with, and the pushed one
        /// answers.
        /// </para>
        /// <para>
        /// <b>Not <c>DECIMAL</c></b>, which parses rather than asserts: it answers for a stored string
        /// that spells a number too, so no type test is where it answers, and <c>IS_NUMBER</c> lost
        /// exactly those documents. It addresses no path — <see cref="IsParsingJsonValue"/> — so
        /// neither the accessor nor a field bound to one reaches here.
        /// </para>
        /// </remarks>
        static SqlFunction? TypeTestOf(org.apache.calcite.rel.type.RelDataType? type)
        {
            switch (type?.getSqlTypeName()?.getName())
            {
                case nameof(SqlTypeName.TINYINT):
                case nameof(SqlTypeName.SMALLINT):
                case nameof(SqlTypeName.INTEGER):
                case nameof(SqlTypeName.BIGINT):
                case nameof(SqlTypeName.FLOAT):
                case nameof(SqlTypeName.REAL):
                case nameof(SqlTypeName.DOUBLE):
                    return CosmosOperators.IsNumber;

                case nameof(SqlTypeName.BOOLEAN):
                    return CosmosOperators.IsBool;

                // Reached by a field and not by an accessor: a JSON_QUERY whose RETURNING names an
                // array is answered by ArrayReading first, and the same RETURNING on JSON_VALUE
                // addresses no path (IsArrayReturningJsonValue).
                case nameof(SqlTypeName.ARRAY):
                case nameof(SqlTypeName.MULTISET):
                    return CosmosOperators.IsArray;

                default:
                    return null;
            }
        }

        /// <summary>
        /// The JSON type a test <see cref="TypeTestOf"/> gives is true of, where a declaration can
        /// answer for it, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <c>null</c> for <c>IS_ARRAY</c> too: an array is a value whose order and equality at the
        /// service nobody has shown to be SQL's, so no declaration makes a field bound to one the value
        /// the service holds — see <see cref="IsReadAsStored"/>.
        /// </remarks>
        static JsonType? TypeTestedBy(SqlFunction test) =>
            test == CosmosOperators.IsNumber ? JsonType.Number
            : test == CosmosOperators.IsBool ? JsonType.Boolean
            : null;

        /// <summary>
        /// Writes the disjunction of a reading's tests over a path, bare.
        /// </summary>
        static string AnyOf(IReadOnlyList<SqlFunction> tests, string path)
        {
            var text = new StringBuilder();

            for (var i = 0; i < tests.Count; i++)
            {
                if (i > 0)
                    text.Append(" OR ");

                text.Append(tests[i].getName()).Append('(').Append(path).Append(')');
            }

            return text.ToString();
        }

        /// <summary>
        /// Writes the disjunction of a reading's tests over a path as one operand of a connective.
        /// </summary>
        static string AnyOfOperand(IReadOnlyList<SqlFunction> tests, string path)
        {
            return tests.Count == 1 ? AnyOf(tests, path) : "(" + AnyOf(tests, path) + ")";
        }

        /// <summary>
        /// Writes a path as the value a reading answers: the value where a test admits it, null
        /// everywhere else.
        /// </summary>
        static string Guarded(IReadOnlyList<SqlFunction> tests, string path)
        {
            return $"({AnyOf(tests, path)} ? {path} : null)";
        }

        /// <summary>
        /// Writes a call, holding back the nested-accessor guard where the call is one whose operand is
        /// an address rather than a value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A geography function consumes what the document holds, not what the accessor extracts.</b>
        /// A stored geography is a GeoJSON <em>object</em>, and
        /// <c>ST_DISTANCE(JSON_VALUE(DOC, '$.location'), …)</c> is how a query names it — the accessor
        /// is typed <c>VARCHAR</c> and is an addressing device, which is the same liberty
        /// <see cref="IsTextRendering"/> records for the filter side. Guarded, <c>IS_PRIMITIVE</c> is
        /// false of that object and the function would be handed null for every document that has one,
        /// so the pushdown geography exists for would answer nothing.
        /// </para>
        /// <para>
        /// This was found by a test rather than reasoned to —
        /// <c>CosmosSortTests.Implement.ASortOverADistanceRendersTheExpression</c> failed the moment the
        /// guard went in — which is the argument for the narrow rule: the guard is for operators whose
        /// Calcite meaning is computed over the accessor's own result, and these are the ones where it
        /// is not.
        /// </para>
        /// </remarks>
        void WriteCall(StringBuilder builder, RexCall call)
        {
            if (_guardNestedAccessors && GeographyFunctions.Contains(call.getOperator().getName()))
            {
                _guardNestedAccessors = false;

                try
                {
                    WriteCallCore(builder, call);
                }
                finally
                {
                    _guardNestedAccessors = true;
                }

                return;
            }

            WriteCallCore(builder, call);
        }

        /// <summary>
        /// Writes a SQL/JSON accessor whose document is a literal as the value it answers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An accessor over a literal addresses nothing, and it does not have to.</b> Both operands
        /// are constants, so the whole call is one — it answers the same value for every document and
        /// can be computed here. Without this it was refused, because <see cref="TryResolveJsonPath"/>
        /// resolves the document operand to a path and a literal is not one, and a refused operand
        /// takes its whole expression in process with it.
        /// </para>
        /// <para>
        /// <b>Which is how it reached us.</b> #130 models an array column that reads empty where the
        /// document has nothing, spelling the fallback as a <c>JSON_QUERY</c> over the literal
        /// <c>[]</c>. The accessor over the document rendered, the coalesce rendered, and the empty
        /// array was the one piece that did not.
        /// </para>
        /// <para>
        /// <b>Calcite own reducer does not close this.</b> Measured with
        /// <c>PROJECT_REDUCE_EXPRESSIONS</c> registered and an executor set, a constant
        /// <c>JSON_VALUE</c> folds to a literal and the constant <c>JSON_QUERY</c> does not, there
        /// being no <c>RexLiteral</c> for an array to fold it to. So the collection case, which is the
        /// one asked for, needs its own answer whether or not that rule is ever registered.
        /// </para>
        /// <para>
        /// <b>The accessor own shape test is applied rather than assumed</b>, and it is the guard
        /// written out: an array <c>RETURNING</c> answers the value only where it is an array, a text
        /// accessor only where it is a primitive, null otherwise — the same line
        /// <see cref="WriteGuardedAccessor"/> draws at the service. The value is bound as a parameter
        /// in plain CLR form, so the service is sent the JSON the constant is and the column own
        /// reading converts what comes back, exactly as for a path.
        /// </para>
        /// </remarks>
        bool TryWriteConstantAccessor(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() < 2
                || Operand(call, 0) is not RexLiteral document
                || Operand(call, 1) is not RexLiteral pathLiteral)
                return false;

            string? text;
            string? pathText;

            try
            {
                text = GetLiteralValue(document) as string;
                pathText = GetLiteralValue(pathLiteral) as string;
            }
            catch (CosmosTranslationException)
            {
                return false;
            }

            if (text is null || pathText is null)
                return false;

            // The same grammar a path over the document column is held to -- a wildcard, a descent or a
            // filter is refused here as it is there, rather than answered by a second implementation.
            if (TryExtendByJsonPath(CosmosPath.Root("$"), pathText, out var path) == false || path is null)
                return false;

            JsonDocument parsed;

            try
            {
                parsed = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // Not a document at all, which is an error behaviour rather than a value. Left to the
                // engine rather than guessed at.
                return false;
            }

            using (parsed)
            {
                var value = parsed.RootElement;

                foreach (var segment in path.Segments)
                {
                    if (segment.IsIndex)
                    {
                        if (value.ValueKind != JsonValueKind.Array || segment.ArrayIndex >= value.GetArrayLength())
                            return WriteJsonNull(builder);

                        value = value[segment.ArrayIndex];
                        continue;
                    }

                    if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment.Name!, out value) == false)
                        return WriteJsonNull(builder);
                }

                if (IsCollectionJsonQuery(call))
                    return value.ValueKind == JsonValueKind.Array ? WriteJsonConstant(builder, value) : WriteJsonNull(builder);

                // A plain JSON_QUERY answers the fragment as *text*, which the reading produces rather
                // than the service -- see TryJsonQueryProjection. There is no reading over a constant
                // written into an expression, so it is declined for the reason a nested one is.
                if (IsPlainJsonQuery(call))
                    return false;

                if (IsJsonPrimitive(value))
                    return WriteJsonConstant(builder, value);

                return IsTextJsonValue(call) ? WriteJsonNull(builder) : WriteJsonConstant(builder, value);
            }
        }

        static bool IsJsonPrimitive(JsonElement value)
        {
            return value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }

        static bool WriteJsonNull(StringBuilder builder)
        {
            builder.Append("null");
            return true;
        }

        bool WriteJsonConstant(StringBuilder builder, JsonElement value)
        {
            builder.Append(_parameters.Add(ToParameterValue(value)));
            return true;
        }

        /// <summary>
        /// Reads a JSON value as the plain CLR shape a query parameter is serialised from.
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>CosmosJson.GetValue</c>, which reads a value into the Java types
        /// Calcite runtime holds. Those are what a column is read <em>back</em> as; what goes
        /// <em>out</em> has to survive whichever serialiser the SDK is configured with, and lists and
        /// dictionaries of primitives do where a <c>JsonElement</c> or a <c>java.util.List</c> does not.
        /// </remarks>
        static object? ToParameterValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.TryGetInt64(out var integral) ? integral : value.GetDouble();
                case JsonValueKind.Array:
                    var items = new List<object?>(value.GetArrayLength());
                    foreach (var item in value.EnumerateArray())
                        items.Add(ToParameterValue(item));

                    return items;
                case JsonValueKind.Object:
                    var members = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var member in value.EnumerateObject())
                        members[member.Name] = ToParameterValue(member.Value);

                    return members;
                default:
                    return null;
            }
        }

        void WriteCallCore(StringBuilder builder, RexCall call)
        {
            // An accessor whose document is a literal is a constant, and is computed here rather than
            // addressed -- see TryWriteConstantAccessor. Ahead of the path branch because that one
            // resolves the document operand to a path, and a literal is not one.
            if (IsJsonAccessor(call) && TryWriteConstantAccessor(builder, call))
                return;

            // A SQL/JSON accessor over the document is the path it addresses, and is written as one:
            // the service returns the value at the path, and the RETURNING clause is what told the
            // plan its type. Handled ahead of the kind switch so that every clause reaching here —
            // a projection, a predicate, a sort key, an aggregate argument — gets it without a case
            // of its own.
            if (IsJsonAccessor(call) && TryResolveJsonPath(call, out var jsonPath) && jsonPath is not null)
            {
                RequireTheEngineCouldRead(call, jsonPath);

                if (_guardNestedAccessors)
                    WriteGuardedAccessor(builder, call, jsonPath.ToString());
                else
                    builder.Append(jsonPath.ToString());

                return;
            }

            // Said here rather than left to the function table, which would call JSON_VALUE unsupported:
            // it is the conversion the clause names that has no Cosmos form, and the bound it implies
            // still pushes -- see IsParsingJsonValue.
            if (IsParsingJsonValue(call))
                throw new CosmosTranslationException(
                    "JSON_VALUE with RETURNING DECIMAL parses what the path holds -- a stored \"2\" is 2 to it, and \"x\" raises -- where the service has the value as it stands, so it is left to the engine. A comparison through it pushes the bound it implies, as a cast to a number does.");

            // The position a NOT puts its operand in belongs to predicates, and only the connectives
            // pass it on -- see CarriesPosition. Everything else is a value, written as one: cleared
            // here, once, rather than by each writer remembering to. And an operand of anything else
            // is read for its value, so a predicate written there is in a value's place -- see _value.
            var position = _negated;
            var value = _value;

            if (CarriesPosition(KindOf(call)) == false)
            {
                _negated = false;
                _value = true;
            }

            try
            {
                WriteCallKind(builder, call);
            }
            finally
            {
                _negated = position;
                _value = value;
            }
        }

        /// <summary>
        /// Determines whether a call passes the position it is written in on to its operands, or
        /// reads it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A position is a property of a predicate, and only a connective has a predicate for an
        /// operand.</b> <c>AND</c> and <c>OR</c> pass it to both arms and <c>NOT</c> flips it, which is
        /// what lets the guard compose; a comparison and a truth test read it, and write their own
        /// operands as values. A <c>CASE</c> passes it to its arms, whose value is its value, and not
        /// to its conditions, which select an arm. SQL takes the next arm for any condition that is not
        /// true, and so does the service — the differential corpus's <c>CASE WHEN category = 'bikes'
        /// THEN 1 ELSE 0 END</c> answers 0 for the document with no category, whose condition is
        /// undefined there — so a condition has to be true exactly where SQL's is, which is the
        /// positive position whatever encloses the <c>CASE</c>.
        /// </para>
        /// <para>
        /// <b>Everything else writes a value, and a value has no position.</b> This was the rule the
        /// field's own remark stated, and only the comparison and the truth test kept it: a
        /// <c>CASE</c> condition, the operand of <c>IS NULL</c> and a function's argument were each
        /// written in the position of the <c>NOT</c> above them. Measured against the emulator,
        /// <c>NOT (CASE WHEN category &lt;&gt; 'bikes' THEN …)</c> took the then arm for a null
        /// category, its condition written unguarded as though negated, where SQL takes the else; so
        /// did the same <c>CASE</c> over <c>=</c>, guarded the wrong way; and
        /// <c>NOT COALESCE(category &lt;&gt; 'bikes', FALSE)</c> lost the null category.
        /// </para>
        /// </remarks>
        static bool CarriesPosition(SqlKind.__Enum kind)
        {
            return kind is SqlKind.__Enum.AND or SqlKind.__Enum.OR or SqlKind.__Enum.NOT or SqlKind.__Enum.CASE
                or SqlKind.__Enum.EQUALS or SqlKind.__Enum.NOT_EQUALS
                or SqlKind.__Enum.LESS_THAN or SqlKind.__Enum.LESS_THAN_OR_EQUAL
                or SqlKind.__Enum.GREATER_THAN or SqlKind.__Enum.GREATER_THAN_OR_EQUAL
                or SqlKind.__Enum.IS_TRUE or SqlKind.__Enum.IS_FALSE
                or SqlKind.__Enum.IS_NOT_TRUE or SqlKind.__Enum.IS_NOT_FALSE;
        }

        /// <summary>
        /// Writes a call by its kind, once the accessors have been taken out.
        /// </summary>
        void WriteCallKind(StringBuilder builder, RexCall call)
        {
            switch (KindOf(call))
            {
                case SqlKind.__Enum.EQUALS:
                    WriteComparison(builder, call, "=");
                    break;
                case SqlKind.__Enum.NOT_EQUALS:
                    WriteComparison(builder, call, "!=");
                    break;
                case SqlKind.__Enum.LESS_THAN:
                    WriteComparison(builder, call, "<");
                    break;
                case SqlKind.__Enum.LESS_THAN_OR_EQUAL:
                    WriteComparison(builder, call, "<=");
                    break;
                case SqlKind.__Enum.GREATER_THAN:
                    WriteComparison(builder, call, ">");
                    break;
                case SqlKind.__Enum.GREATER_THAN_OR_EQUAL:
                    WriteComparison(builder, call, ">=");
                    break;
                case SqlKind.__Enum.AND:
                    WriteChain(builder, call, "AND");
                    break;
                case SqlKind.__Enum.OR:
                    WriteChain(builder, call, "OR");
                    break;
                case SqlKind.__Enum.PLUS:
                    RequireNoFraction(call);
                    WriteBinary(builder, call, "+");
                    break;
                case SqlKind.__Enum.MINUS:
                    RequireNoFraction(call);
                    WriteBinary(builder, call, "-");
                    break;
                case SqlKind.__Enum.TIMES:
                    RequireNoFraction(call);
                    WriteBinary(builder, call, "*");
                    break;
                case SqlKind.__Enum.DIVIDE:
                    RequireApproximateQuotient(call);
                    WriteBinary(builder, call, "/");
                    break;
                case SqlKind.__Enum.MOD:
                    RequireNoFraction(call);
                    WriteBinary(builder, call, "%");
                    break;
                case SqlKind.__Enum.NOT:
                    WriteNot(builder, call);
                    break;
                case SqlKind.__Enum.MINUS_PREFIX:
                    RequireOperandCount(call, 1);
                    builder.Append("(-");
                    Write(builder, Operand(call, 0));
                    builder.Append(')');
                    break;
                case SqlKind.__Enum.IS_NULL:
                    WriteIsNull(builder, call, negated: false);
                    break;
                case SqlKind.__Enum.IS_NOT_NULL:
                    WriteIsNull(builder, call, negated: true);
                    break;
                case SqlKind.__Enum.IS_TRUE:
                    WriteTruthTest(builder, call, truth: true, negated: false);
                    break;
                case SqlKind.__Enum.IS_FALSE:
                    WriteTruthTest(builder, call, truth: false, negated: false);
                    break;
                case SqlKind.__Enum.IS_NOT_TRUE:
                    WriteTruthTest(builder, call, truth: true, negated: true);
                    break;
                case SqlKind.__Enum.IS_NOT_FALSE:
                    WriteTruthTest(builder, call, truth: false, negated: true);
                    break;
                case SqlKind.__Enum.LIKE:
                    WriteLike(builder, call);
                    break;
                case SqlKind.__Enum.ITEM:
                    WriteItem(builder, call);
                    break;
                case SqlKind.__Enum.CASE:
                    WriteCase(builder, call);
                    break;
                case SqlKind.__Enum.TRIM:
                    WriteTrim(builder, call);
                    break;
                case SqlKind.__Enum.CAST:
                case SqlKind.__Enum.SAFE_CAST:
                    WriteCast(builder, call);
                    break;
                case SqlKind.__Enum.FLOOR:
                    RequireOperandCount(call, 1);
                    WriteFunctionCall(builder, call, "FLOOR");
                    break;
                case SqlKind.__Enum.CEIL:
                    // Cosmos spells it CEILING; CEIL is rejected.
                    RequireOperandCount(call, 1);
                    WriteFunctionCall(builder, call, "CEILING");
                    break;
                default:
                    // Most scalar functions are OTHER_FUNCTION, but several carry a dedicated kind
                    // — CHAR_LENGTH and POSITION among them — so dispatch on the name for anything
                    // whose structure does not need special handling.
                    WriteNamedFunction(builder, call);
                    break;
            }
        }

        /// <summary>
        /// Refuses a division Calcite does not compute in doubles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The service divides in doubles, and SQL divides in the type of its operands.</b> Calcite
        /// types <c>INTEGER / INTEGER</c> an integer and truncates toward zero, as Java does, where
        /// the service answers the fraction. Measured against the emulator over products, whose prices
        /// are integers: <c>JSON_VALUE(DOC, '$.price' RETURNING INTEGER) / 2 = 2</c> lost the document
        /// priced 5, which Calcite divides to 2 and the service to 2.5; and the same quotient
        /// projected failed the read, <c>113.33333333333333</c> not being a whole number to read as
        /// the <c>INTEGER</c> the plan declared. A <c>DECIMAL</c> quotient has a scale of its own and
        /// a rounding the service does not perform either.
        /// </para>
        /// <para>
        /// <b>Refused rather than rendered.</b> <c>TRUNC(a / b)</c> is the obvious rendering and would
        /// have to be shown to agree on negatives, on a quotient past the precision a double carries,
        /// and on what the reader makes of the result, for every integer type; none of that has been
        /// measured, and declining costs only the division, which Calcite computes over what the
        /// service sends. <see cref="Rel.CosmosAggregate.CanImplement"/> made the same call for
        /// <c>AVG</c>, which an integer argument sends to <c>SUM</c> and <c>COUNT</c> with the division
        /// done above.
        /// </para>
        /// </remarks>
        /// <param name="call">The division.</param>
        /// <exception cref="CosmosTranslationException">Calcite types the quotient anything but approximate.</exception>
        static void RequireApproximateQuotient(RexCall call)
        {
            var type = call.getType()?.getSqlTypeName();

            if (type is null || SqlTypeName.APPROX_TYPES.contains(type) == false)
                throw new CosmosTranslationException($"A division typed '{call.getType()}' is computed in that type by Calcite -- an integer one truncates -- and in doubles by the service, so only an approximate quotient is the same at both ends.");
        }

        /// <summary>
        /// Refuses a sum, difference, product or remainder Calcite computes exactly in decimal
        /// fractions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The division's neighbours, for the division's reason.</b> Calcite types an integer
        /// times <c>1.1</c> a <c>DECIMAL</c> with a scale, and computes it exactly; the service
        /// computes it in doubles, where <c>1.1</c> is not <c>1.1</c>. Measured against the emulator
        /// over products: <c>price * 1.1 = 374</c> lost the document priced 340, the service's product
        /// being <c>374.00000000000006</c>; <c>price * 0.07 = 0.35</c> and <c>price + 0.1 + 0.2 =
        /// 10.3</c> each lost theirs; projected, <c>price * 0.7</c> answered
        /// <c>237.99999999999997</c> where Calcite answers <c>238.0</c>, a grouping key by the product
        /// came back as <c>374.00000000000006</c>, and a <c>SUM</c> over it drifted in its last place.
        /// A remainder by <c>0.3</c> lost two documents the same way.
        /// </para>
        /// <para>
        /// <b>A scale of zero is an integer, and stays.</b> Integer arithmetic is exact in doubles
        /// up to 2^53, so an integer sum is the service's sum wherever it fits — and an approximate
        /// result is the service's arithmetic outright. Where an integer one does not fit, Calcite and
        /// the service part for another reason: Calcite wraps, as Java does, where SQL raises and the
        /// service computes the value. That is recorded in <c>DESIGN.md</c> as a decision, beside
        /// out-of-domain arithmetic, rather than declined.
        /// </para>
        /// </remarks>
        /// <param name="call">The arithmetic.</param>
        /// <exception cref="CosmosTranslationException">Calcite types the result a decimal fraction.</exception>
        static void RequireNoFraction(RexCall call)
        {
            var type = call.getType();

            if (type?.getSqlTypeName() == SqlTypeName.DECIMAL && type.getScale() > 0)
                throw new CosmosTranslationException($"Arithmetic typed '{type}' is computed exactly in decimal by Calcite and in doubles by the service, which do not agree on a fraction.");
        }

        void WriteBinary(StringBuilder builder, RexCall call, string op)
        {
            RequireOperandCount(call, 2);
            WriteBinary(builder, Operand(call, 0), Operand(call, 1), op);
        }

        void WriteBinary(StringBuilder builder, RexNode left, RexNode right, string op)
        {
            builder.Append('(');
            Write(builder, left);
            builder.Append(' ').Append(op).Append(' ');
            Write(builder, right);
            builder.Append(')');
        }

        /// <summary>
        /// Recognises <c>CAST(&lt;document value&gt; AS VARCHAR) = &lt;text&gt;</c> in the one form
        /// where the cast can be dropped, and returns the value underneath.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A view over a container can only give a column a SQL type by wrapping the document access
        /// in a cast, because the row model types every path <c>ANY</c>. Dropping a cast in general
        /// changes which documents match — Calcite converts the stored value and the service compares
        /// it as it stands — so it is not done. This shape is the exception, and it is an
        /// <em>equivalence</em> rather than an approximation.
        /// </para>
        /// <para>
        /// <b>The argument.</b> Calcite renders the stored value as text and compares. A stored string
        /// renders as itself, so it matches exactly when it equals the literal. Every other JSON value
        /// renders as something recognisable — a number as digits, a boolean as <c>true</c> or
        /// <c>false</c>, an array or object with a bracket — so where the literal is none of those, no
        /// non-string value can render as it, and the documents Calcite matches are exactly the
        /// documents storing that string. <c>c.path = 'text'</c> selects exactly those at the service.
        /// Absent and null match under neither: SQL's comparison is unknown and the service's is not
        /// true.
        /// </para>
        /// <para>
        /// <b>What the conditions are for.</b> The operand must be a document value the cast renders
        /// rather than converts — see <see cref="IsRenderedDocumentValue"/> — so the cast is
        /// reinterpreting what the document holds rather than converting a value that already has a
        /// type. The literal must be text — a numeric literal is a different comparison — and must be
        /// text <see cref="IsUnambiguousText"/> admits, which is where the argument above is enforced
        /// rather than assumed. A cast carrying a format is refused by arity.
        /// </para>
        /// </remarks>
        internal static RexNode? TryTextCastOperand(RexNode node, RexNode other)
        {
            if (TryTextCastValue(node) is not RexNode operand)
                return null;

            if (other is not RexLiteral literal)
                return null;

            object? value;
            try
            {
                value = GetLiteralValue(literal);
            }
            catch (CosmosTranslationException)
            {
                return null;
            }

            return value is string text && IsUnambiguousTextFor(operand, text) ? operand : null;
        }

        /// <summary>
        /// Recognises <c>CAST(&lt;document value&gt; AS VARCHAR)</c> over a value the cast renders
        /// rather than converts, and returns the value underneath.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shape without the literal: what <see cref="TryTextCastOperand"/> admits once the text is
        /// right, what <see cref="Rel.Convert.CosmosFilterSplitRule"/> weakens when it is not, and
        /// what the oracle's loader reads a value through (<see cref="Metadata.CosmosPredicateLoader"/>),
        /// and so every fact a filter establishes and every key it pins.
        /// </para>
        /// <para>
        /// <b>Only an undecorated <c>VARCHAR</c></b>, which is what <see cref="TryRenderedTextOperand"/>
        /// and <see cref="StripTextCoercion"/> already held to. A width is a second conversion on top
        /// of the rendering: measured, <c>VARCHAR(3)</c> truncates and <c>CHAR(8)</c> pads, and a
        /// <c>CHAR</c> always carries one, a bare <c>CHAR</c> being <c>CHAR(1)</c>. Taken as an identity
        /// it was dropped from the equality — <c>CAST(label AS VARCHAR(3)) = 'bik'</c> pushed as
        /// <c>c.label = 'bik'</c> and lost the document storing <c>bikes</c>, which Calcite truncates
        /// and keeps — and the partition-key extractor routed the same statement over
        /// <c>category</c> to a partition named <c>bik</c>, which holds nothing. Unlike the width on a
        /// <c>RETURNING</c>, which the same measurement found no run time applying — see
        /// <see cref="IsRenderedDocumentValue"/>.
        /// </para>
        /// </remarks>
        internal static RexNode? TryTextCastValue(RexNode node)
        {
            if (node is not RexCall call)
                return null;

            var kind = KindOf(call);
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return null;

            if (call.getOperands().size() != 1)
                return null;

            var type = call.getType();
            if (type?.getSqlTypeName() != SqlTypeName.VARCHAR || type.getPrecision() != org.apache.calcite.rel.type.RelDataType.PRECISION_NOT_SPECIFIED)
                return null;

            var operand = Operand(call, 0);
            return IsRenderedDocumentValue(operand) ? operand : null;
        }

        /// <summary>
        /// Recognises a comparand that is the rendering of a document value — the value under a cast
        /// to text, or a bare <c>JSON_VALUE</c> read as text — and returns the value.
        /// </summary>
        /// <param name="node">The expression to inspect.</param>
        /// <param name="scalarOnly">
        /// On success, whether only a scalar can render: <c>JSON_VALUE</c> answers null for an object
        /// or an array, where the cast over <c>ANY</c> renders them with a bracket.
        /// </param>
        /// <returns>The value rendered, or <c>null</c> where this is not that shape.</returns>
        internal static RexNode? TryRenderedTextValue(RexNode node, out bool scalarOnly)
        {
            if (TryTextCastValue(node) is RexNode operand)
            {
                scalarOnly = IsTextJsonValue(operand);
                return operand;
            }

            if (IsTextJsonValue(node) && IsPlainJsonValue(node))
            {
                scalarOnly = true;
                return node;
            }

            scalarOnly = false;
            return null;
        }

        /// <summary>
        /// Determines whether text is one no JSON value other than that string renders as, for the
        /// value it is compared against.
        /// </summary>
        /// <remarks>
        /// <see cref="IsUnambiguousText"/> for a value the cast over <c>ANY</c> renders, where an
        /// array and an object render too. A <c>JSON_VALUE</c> read as text renders scalars only — an
        /// object, an array and a null all come back as SQL null and never match — so for it only a
        /// number's digits and Calcite's lowercase <c>true</c> and <c>false</c> are ambiguous.
        /// </remarks>
        internal static bool IsUnambiguousTextFor(RexNode value, string text)
        {
            return IsTextJsonValue(value) ? IsUnambiguousScalarText(text) : IsUnambiguousText(text);
        }

        /// <summary>
        /// Determines whether text is one no JSON <em>scalar</em> other than that string renders as.
        /// </summary>
        /// <remarks>
        /// The test for a rendering that answers null for an object and an array — which is
        /// <c>JSON_VALUE</c>'s, and therefore that of a field bound to one. Only a number's digits and
        /// Calcite's lowercase <c>true</c> and <c>false</c> are ambiguous.
        /// </remarks>
        static bool IsUnambiguousScalarText(string text)
        {
            return TryParseRenderedNumber(text, out _) == false && text is not ("true" or "false");
        }

        /// <summary>
        /// Parses text as the number Calcite renders one as, where it is one.
        /// </summary>
        /// <remarks>
        /// Java's <c>BigDecimal</c> grammar, which every rendering a stored number can take —
        /// <c>30</c>, <c>30.7</c>, <c>1.0E30</c>, <c>1E+30</c> — satisfies, and which admits nothing
        /// that is not a number. Read as a double because that is what the service holds; a literal
        /// the type cannot represent exactly rounds as the stored text rounds, to the same double. A
        /// value beyond the double range parses and comes back infinite, which the caller decides.
        /// <para>
        /// Text holding an ASCII character the grammar has no place for is refused before it is parsed.
        /// Most text asked about is a string literal — a discriminator, a type name — and the parse refuses
        /// it by throwing, which under IKVM captures a stack trace each time; the planner asks this of
        /// every equality it considers pushing, and the throws were a measured share of planning (#201).
        /// Only ASCII is refused early: the grammar's digits are whatever Java calls a digit, and that
        /// question is left to the parse.
        /// </para>
        /// </remarks>
        internal static bool TryParseRenderedNumber(string text, out double number)
        {
            number = 0;

            if (text.Length == 0)
                return false;

            foreach (var c in text)
                if (c < 128 && c is not ((>= '0' and <= '9') or '+' or '-' or '.' or 'e' or 'E'))
                    return false;

            try
            {
                number = new java.math.BigDecimal(text).doubleValue();
                return true;
            }
            catch (java.lang.NumberFormatException)
            {
                number = 0;
                return false;
            }
        }

        /// <summary>
        /// Determines whether what is known of the documents says the path an expression resolves to
        /// holds one JSON type or null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One question, asked of the licensed facts.</b> There were four — a string asked of the
        /// facts a filter established, the same asked of the declaration alone, any type asked of the
        /// declaration alone, and a rendering's string asked of the established facts again — and the
        /// split between "established" and "outright" existed only because the facts a filter
        /// established included the conjunct being translated: <c>JSON_VALUE(…, '$.label') = '30'</c>
        /// established <c>EqualTo "30"</c>, which entails a string, and certified itself exact while a
        /// stored number 30 renders as <c>'30'</c> and matches at Calcite and not at the service. The
        /// facts a translation is handed now never hold what the expression establishes
        /// (<see cref="Facts"/>), so the question is one.
        /// </para>
        /// <para>
        /// <c>OrNull</c>, because a JSON null and an absent path are both SQL's null, which every
        /// accessor reads as one and the service places together, below every value.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <param name="type">The type.</param>
        /// <returns><c>true</c> where the facts say the path holds that type, or null.</returns>
        bool IsDeclared(RexNode node, JsonType type)
        {
            if (TryResolvePath(node, out var path) == false || DocumentOf(path) is not JsonDocumentPath document)
                return false;

            return Facts.Knows(new JsonFact(document, new JsonClaim.OfType(type, OrNull: true)));
        }

        /// <summary>
        /// Returns the document path a statement's path reads, where it reads the container's document.
        /// </summary>
        /// <remarks>
        /// Rooted at the container only: a traversal alias addresses an element, and what is known is of
        /// the document. <see cref="Metadata.CosmosDocumentPaths.From"/> drops the alias, so asked of an
        /// element's <c>t.x</c> it answers the document's <c>x</c>. Every translator reads the declaration
        /// where its caller named no licence (<see cref="Facts"/>), the one predicate an unnest renders over
        /// its element among them.
        /// </remarks>
        /// <param name="path">The statement's path.</param>
        /// <returns>The document path, or <c>null</c>.</returns>
        static JsonDocumentPath? DocumentOf(CosmosPath? path) =>
            path is not null && string.Equals(path.Alias, CosmosImplementor.DefaultRootAlias, StringComparison.Ordinal)
                ? Metadata.CosmosDocumentPaths.From(path)
                : null;

        /// <summary>
        /// Determines whether a field, as it is read back, is the value the service holds at the path
        /// it binds to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three operators act on the stored value in the field's place, and each has to ask.</b> A
        /// field bound to a path lets an operator above it address the document — which is what the
        /// binding is for, and which says nothing about whether the stored value and the column agree.
        /// The lookup join restricts <c>path IN (@k…)</c> and matches the column; a grouping key groups
        /// the value the service holds and SQL groups the column; a sort orders by the path and SQL
        /// orders the column. Each is right exactly where the two are the same value, and that is a
        /// question about the field's <see cref="CosmosReading"/>, asked here once so that the three
        /// cannot answer it three ways.
        /// </para>
        /// <para>
        /// <b>The lookup join asks it</b>, of a key column the probe side reads as text: its
        /// restriction is the raw <c>path IN (@k…)</c>, which is an equality against a parameter once
        /// per key. See <c>CosmosLookupJoinRule.TryProbeKey</c>. So do a grouping key and a sort key.
        /// Each asks <see cref="IsDeclared"/> of the facts it was handed.
        /// </para>
        /// <para>
        /// <b>Read as declared</b>, the column is the stored value — unless a <c>RETURNING</c> asserted
        /// a type. Such a field reads null wherever the path holds anything else, an object and an
        /// array as surely as an absent path (see <see cref="WriteGuardedAccessor"/>), while the
        /// service still holds the object and sorts it after every scalar: measured on the emulator
        /// over the differential suite's typed container,
        /// <c>JSON_VALUE(DOC, '$.label' RETURNING INTEGER)</c> ordered <c>NULLS FIRST</c> under a page
        /// of three came back <c>[null, null, 30]</c> where SQL answers three nulls, the array and the
        /// object that read null having sorted last. So it is the stored value where the facts the
        /// caller was handed give the path the type its test names, or null — the test a null test and a
        /// comparison read the field by too (<see cref="TypedFieldTest"/>), and none for the service's
        /// own <c>id</c>, <c>_ts</c> and <c>_etag</c> or a promoted <c>VARIANT</c> column, each being
        /// the raw value.
        /// </para>
        /// <para>
        /// <b>Read as text</b>, it is a rendering: a stored <c>30</c> and a stored <c>"30"</c> are one
        /// value as text and two at the service, a stored <c>true</c> renders as <c>'true'</c> and
        /// sorts below every number there, and a stored number sorts numerically where its digits sort
        /// as text. The exception is <see cref="IsDeclaredString"/>'s, what is known saying the path
        /// holds a string and nothing else, where the rendering is the value. <b>Read as JSON</b> —
        /// the document column, or a <c>JSON_QUERY</c> fragment — it is a serialisation of an object or
        /// an array, which the service orders by rules of its own, measured to differ from their text's
        /// order (<c>DESIGN.md</c>, under <em>Ordering a sort key the way it reads</em>), and equates
        /// by rules nobody has measured against their text's equality.
        /// </para>
        /// <para>
        /// A caller with an exception of its own takes it before asking. A grouping by the document
        /// column groups nothing, no two documents being equal, and a grouping by a field read as
        /// declared groups what the projection wrote, which for a typed accessor is its guard and not
        /// the path — see <c>CosmosAggregate.GroupsAsTheColumnReads</c>. A lookup key read as
        /// declared is bound as a parameter of its type, which no stored value of another type equals
        /// — see <c>CosmosLookupJoinRule.IsRestrictedAsStored</c>. And a sort refuses an array however
        /// it is read, the service's order over arrays not being SQL's — see
        /// <c>CosmosSort.TryResolveSortKeys</c>.
        /// </para>
        /// </remarks>
        /// <param name="reference">The field.</param>
        /// <returns><c>true</c> where an operator on the stored value is an operator on the column.</returns>
        internal bool IsReadAsStored(RexInputRef reference)
        {
            var index = reference.getIndex();

            return (index >= 0 && index < _readings.Count ? _readings[index] : CosmosReading.Typed) switch
            {
                CosmosReading.Typed => TypedFieldTest(reference) is not SqlFunction test
                    || TypeTestedBy(test) is JsonType tested && IsDeclared(reference, tested),
                CosmosReading.Text => IsDeclaredString(reference),
                _ => false,
            };
        }

        /// <summary>
        /// Determines whether an expression is a scalar rendering of a path what is known says holds a
        /// string, where it holds anything: the rendering of a string is the string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What the refusals over a rendering rest on is that the service orders and compares raw
        /// values <em>across</em> JSON types while Calcite compares their renderings, and that a
        /// service function over a number is undefined where Calcite's over its text is not. Where the
        /// path holds a string and nothing else, there is no second type for them to disagree over, and
        /// the comparison, the equality and the function are exact rather than a weakening.
        /// </para>
        /// <para>
        /// A scalar rendering only: a <c>JSON_QUERY</c> is null for a string, so the fact that makes
        /// the accessor's text the value makes the fragment's nothing — see
        /// <see cref="IsScalarRendering"/>. <c>OrNull</c>, because a JSON null is dropped by the
        /// service and answered SQL null by the accessor alike.
        /// </para>
        /// </remarks>
        internal bool IsDeclaredString(RexNode node) => IsScalarRendering(node) && IsDeclared(node, JsonType.String);

        /// <summary>
        /// Determines whether an expression is a document value that a cast to text renders rather
        /// than converts.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two spellings say it. A promoted column is typed <c>ANY</c>, and Calcite's cast over one
        /// is the value's own rendering. <c>JSON_VALUE</c> without a <c>RETURNING</c> clause is the
        /// same value said as a path: Calcite types it <c>VARCHAR(2000)</c> and, measured against its
        /// own runtime, renders a stored number as digits, a boolean as <c>true</c> or <c>false</c>,
        /// and a string as itself — exactly what the cast over <c>ANY</c> renders — and answers null
        /// for an absent path, a null, an object or an array, which the comparison then does not keep.
        /// So the argument on <see cref="TryTextCastOperand"/> carries over unchanged to the cast a
        /// <c>DOC</c> view writes (#71). The same measurement found no width applied at run time —
        /// <c>RETURNING VARCHAR(3)</c> returns <c>'bikes'</c> whole — so a character type of any width
        /// is the same reading.
        /// </para>
        /// <para>
        /// A <c>RETURNING</c> that converts — a number, a boolean, a date — is refused: the cast then
        /// renders a converted value, and a string in the document is not it. <c>JSON_QUERY</c> is
        /// refused with it, being the JSON text of an object or an array and null for anything else,
        /// which is not what the path holds. Only the plain form — see <see cref="IsPlainJsonValue"/>:
        /// an <c>ON EMPTY</c> or <c>ON ERROR</c> clause substitutes a value where the path has none,
        /// which is a document the path itself does not match.
        /// </para>
        /// </remarks>
        internal static bool IsRenderedDocumentValue(RexNode operand)
        {
            if (IsDocumentValueType(operand.getType()?.getSqlTypeName()))
                return true;

            return IsTextJsonValue(operand) && IsPlainJsonValue(operand);
        }

        /// <summary>
        /// Determines whether a type is one a document value carries: the raw value at a path.
        /// </summary>
        /// <remarks>
        /// Two spellings reach here for one meaning. A promoted declared-path column is typed
        /// <c>VARIANT</c> — <see cref="CosmosTable.getRowType"/> — being a scalar whose concrete type is
        /// learned per row. An accessor named as its raw value rather than its rendering is typed
        /// <c>ANY</c> — <see cref="Rel.Convert.CosmosFilterSplitRule"/> re-types it there, and a field
        /// bound to one is cast there; see <see cref="WriteCast"/>. Both say "the value the service
        /// holds", and Calcite's cast over either is the value's own rendering, so both are read as the
        /// document value a cast to text reinterprets rather than converts.
        /// </remarks>
        internal static bool IsDocumentValueType(SqlTypeName? name)
        {
            return name == SqlTypeName.ANY || name == SqlTypeName.VARIANT;
        }

        /// <summary>
        /// Sees through the cast Calcite's type coercion adds to the other side of a comparison with a
        /// <c>VARIANT</c> column, returning the value underneath.
        /// </summary>
        /// <remarks>
        /// A promoted declared-path column is typed <c>VARIANT</c> — <see cref="CosmosTable.getRowType"/>
        /// — and the validator coerces the other side of a comparison with it by wrapping it in
        /// <c>CAST(… AS VARIANT)</c>. That box carries the value unchanged: the service compares the raw
        /// document value against it either way, and <see cref="WriteCast"/> renders it as the value
        /// underneath. Recognising the shape lets the point-read and fact extractors read the literal
        /// under it, exactly as they read the bare literal an <c>ANY</c>-typed column left uncoerced.
        /// Only the single-operand cast whose sole effect is the <c>VARIANT</c> retyping; anything else
        /// is returned unchanged.
        /// <para>
        /// The validator boxes the accessor too, where the promoted column is the other side, and the
        /// box hides nothing about what is inside it: a rendering boxed is a rendering. So the
        /// comparison, <c>LIKE</c> and the null test ask their questions beneath it — see
        /// <see cref="WriteComparand"/>.
        /// </para>
        /// </remarks>
        internal static RexNode StripVariantCoercion(RexNode node)
        {
            if (node is RexCall call
                && (KindOf(call) == SqlKind.__Enum.CAST || KindOf(call) == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && call.getType()?.getSqlTypeName() == SqlTypeName.VARIANT)
                return Operand(call, 0);

            return node;
        }

        /// <summary>
        /// Sees through the cast Calcite's type coercion adds when a raw <c>VARIANT</c> column stands
        /// where a string is wanted — the subject of <c>LIKE</c>, of a case fold — returning the column
        /// underneath.
        /// </summary>
        /// <remarks>
        /// An <c>ANY</c> value carried into those operators without a cast, so the subject was the bare
        /// path and rendered as one. A <c>VARIANT</c> partition-key column —
        /// <see cref="CosmosTable.getRowType"/> — is coerced to <c>VARCHAR</c> first, and this unwraps
        /// that coercion so the raw path renders as it did: the service applies the string function to
        /// the value it holds, exactly as it did over the <c>ANY</c> column, and
        /// <see cref="IsTextRendering"/> is false of it either way, so its guard is unaffected. Only over
        /// a <c>VARIANT</c> operand, which only the promoted column carries — a re-typed accessor is
        /// <c>ANY</c> and is a rendering, kept as one — and only an undecorated <c>VARCHAR</c>/<c>CHAR</c>,
        /// a width being a truncation. This is not the comparison case: there a cast to text over a
        /// document value is a rendering an equality drops only against unambiguous text, and
        /// <see cref="WriteCast"/> declines what survives; a string operator reads the value itself, so
        /// the cast is nothing but the coercion.
        /// </remarks>
        internal static RexNode StripTextCoercion(RexNode node)
        {
            if (node is RexCall call
                && (KindOf(call) == SqlKind.__Enum.CAST || KindOf(call) == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && (call.getType()?.getSqlTypeName() == SqlTypeName.VARCHAR || call.getType()?.getSqlTypeName() == SqlTypeName.CHAR)
                && call.getType()?.getPrecision() == org.apache.calcite.rel.type.RelDataType.PRECISION_NOT_SPECIFIED
                && Operand(call, 0).getType()?.getSqlTypeName() == SqlTypeName.VARIANT)
                return Operand(call, 0);

            return node;
        }

        /// <summary>
        /// Determines whether an expression is a <c>JSON_VALUE</c> read as text, which is what the
        /// accessor is without a <c>RETURNING</c> clause.
        /// </summary>
        /// <remarks>
        /// Such a call is a rendering of the value at the path and not the value itself: SQL:2016 casts
        /// the scalar to the returning type, and Calcite does — a stored number 30 arrives as the text
        /// <c>30</c>. The path at the service holds the number. Whatever compares the two therefore has
        /// to reason about which documents the rendering matches, which is the argument
        /// <see cref="TryTextCastOperand"/> makes for the cast the accessor is the spelling of.
        /// </remarks>
        internal static bool IsTextJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            var name = call.getType()?.getSqlTypeName();
            return name == SqlTypeName.VARCHAR || name == SqlTypeName.CHAR;
        }

        /// <summary>
        /// Determines whether an expression is a plain <c>JSON_VALUE</c> — one whose behaviour clauses,
        /// if it carries any, say what saying nothing says.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A behaviour clause is not a path.</b> <c>DEFAULT x ON EMPTY</c>, <c>DEFAULT x ON ERROR</c>
        /// and <c>ERROR ON …</c> each answer something for a document the path answers nothing for — a
        /// value it does not hold, or a failure — so a projection rendered as the guarded path read null
        /// where the engine reads the default, and a field bound to one carried the path up to every
        /// operator above it with nothing to say the column was not that path. The accessor was taken
        /// whatever followed its second operand, and this is the test it was missing.
        /// </para>
        /// <para>
        /// <b>Refused rather than rendered, and measurement is why.</b> <c>IS_DEFINED(p) ? … : @d</c>
        /// looks like <c>DEFAULT … ON EMPTY</c> at the service, and it is not what Calcite does with
        /// it. Measured — <c>CalciteJsonValueMeasurementTests</c> — a path written without a mode is
        /// strict there, so an absent property is an <em>error</em> and not an empty result:
        /// <c>DEFAULT 'none' ON EMPTY</c> over <c>{}</c> answers null and <c>DEFAULT 'none' ON
        /// ERROR</c> answers <c>'none'</c>; the same <c>ON EMPTY</c> over <c>'lax $.v'</c> answers
        /// <c>'none'</c>; and a JSON null, an object and an array all take the error branch, never the
        /// empty one. A rendering would have to reproduce that table and the mode the path is spelled
        /// in, and <c>ERROR ON ERROR</c> raises, which no rendering reproduces at all. Left in process,
        /// the engine answers what it answers and there is nothing to keep in step.
        /// </para>
        /// <para>
        /// <b><c>NULL ON EMPTY</c> and <c>NULL ON ERROR</c> are plain</b>, being the defaults written
        /// down — the validator keeps them as symbols, <c>JSON_VALUE($0, '$.v', FLAG(NULL),
        /// FLAG(ON EMPTY))</c>, where the call with nothing said has two operands. Each clause is its
        /// behaviour, a default value where the behaviour is <c>DEFAULT</c>, and the condition it
        /// governs; so a call is plain when what follows the path is a run of <c>NULL</c> and a
        /// condition, read by name for the reason <see cref="IsPlainJsonQuery"/> gives.
        /// </para>
        /// </remarks>
        internal static bool IsPlainJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            var count = call.getOperands().size();
            if (count < 2 || count % 2 != 0)
                return false;

            for (var i = 2; i < count; i += 2)
                if (IsFlag(call, i, "NULL") == false || (IsFlag(call, i + 1, "EMPTY") || IsFlag(call, i + 1, "ERROR")) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Determines whether an expression is a plain <c>JSON_QUERY</c> — the accessor that answers a
        /// JSON fragment rather than a scalar.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The two-operand form only.</b> <c>JSON_QUERY</c> carries a wrapper clause and behaviour
        /// clauses, and each changes what comes back: measured, <c>WITH UNCONDITIONAL ARRAY WRAPPER</c>
        /// over the string <c>bikes</c> answers <c>["bikes"]</c> where the plain form answers null, and
        /// <c>EMPTY OBJECT ON ERROR</c> answers <c>{}</c>. None of those is the path, so none is
        /// rendered as one, and only the spelling with nothing else to say is taken.
        /// </para>
        /// <para>
        /// The type is not the test it is for <see cref="IsTextJsonValue"/>: <c>JSON_QUERY</c> is
        /// <c>VARCHAR</c> whatever it is asked for. Nor is the operand count, which is where this
        /// first went wrong — <c>JSON_VALUE</c> carries its <c>RETURNING</c> in its type and has two
        /// operands, while a validated <c>JSON_QUERY</c> always has five: the wrapper and both
        /// behaviours are present as symbols whether or not they were written. Plain is what those
        /// three say — or, for a call built by hand with none of them, what their absence says.
        /// </para>
        /// <para>
        /// Read by name rather than by ordinal, for the reason the rest of this file gives: a Java
        /// enum's ordinals are not stable across versions, its names are.
        /// </para>
        /// </remarks>
        internal static bool IsPlainJsonQuery(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_QUERY")
                return false;

            // Two operands is the call with nothing said, which a caller building one by hand writes
            // and which is plain by construction -- there is no clause on it to differ from the plain
            // form. The validator always writes five, so both shapes reach here.
            if (call.getOperands().size() == 2)
                return true;

            return call.getOperands().size() == 5
                && IsFlag(call, 2, "WITHOUT_ARRAY")
                && IsFlag(call, 3, "NULL")
                && IsFlag(call, 4, "NULL");
        }

        /// <summary>
        /// Determines whether an operand is a symbol literal naming the given enum constant.
        /// </summary>
        static bool IsFlag(RexCall call, int ordinal, string name)
        {
            if ((RexNode)call.getOperands().get(ordinal) is not RexLiteral literal)
                return false;

            var value = literal.getValue();
            var text = value is java.lang.Enum symbol ? symbol.name() : value?.ToString();

            return string.Equals(text, name, StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether an expression is a plain <c>JSON_QUERY</c> whose <c>RETURNING</c> names
        /// an array type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The counterpart of <see cref="IsTextJsonValue"/>, and asked for the same reason: what the
        /// call is typed decides both how it is rendered and how the value is read back. A
        /// <c>RETURNING</c> is what changes that type — a <em>wrapper</em> clause does not, so
        /// <c>WITH UNCONDITIONAL ARRAY WRAPPER</c> is still <c>VARCHAR</c>, the JSON text of an array
        /// rather than the array, and is not this.
        /// </para>
        /// <para>
        /// <c>JSON_QUERY</c>'s alone. This asked for either accessor until the array
        /// <c>RETURNING</c> on <c>JSON_VALUE</c> was refused — see
        /// <see cref="IsArrayReturningJsonValue"/>, which says why, and note that such a call no
        /// longer reaches here at all: <see cref="IsJsonAccessor"/> declines it first.
        /// </para>
        /// </remarks>
        internal static bool IsCollectionJsonQuery(RexNode node)
        {
            if (node is not RexCall call)
                return false;

            // JSON_QUERY's alone. SQL restricts JSON_VALUE's RETURNING to a predefined scalar type,
            // so an array one is not a construct with a meaning to implement -- see
            // IsArrayReturningJsonValue, and IsJsonAccessor, which refuses it to every clause.
            if (IsPlainJsonQuery(call) == false)
                return false;

            var name = call.getType()?.getSqlTypeName();
            return name == SqlTypeName.ARRAY || name == SqlTypeName.MULTISET;
        }

        /// <summary>
        /// Determines whether an expression is the rendering of a document value as text: a
        /// <c>JSON_VALUE</c> read as text, or a field bound to one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The second spelling is the first seen from above a projection. A view projects the accessor
        /// under an alias, and an operator written over the alias arrives here as a field reference
        /// the binding resolves to the path — which is right, the document being what the operator
        /// addresses, and which said nothing about the column carrying the rendering rather than the
        /// value. A comparison over such a field was pushed raw, the very comparison the accessor's
        /// own spelling is declined for; and where a host then transposed the filter below the
        /// projection, the accessor was inlined and the statement refused at implementation (#83).
        /// The reading is what the binding carries to say it, and a field read as text is held to
        /// every test the accessor is.
        /// </para>
        /// <para>
        /// Of a character type only, in both spellings. <see cref="Rel.Convert.CosmosFilterSplitRule"/>
        /// names the raw value at the same path by typing the accessor <c>ANY</c>, and a field bound
        /// to one by casting it there — see <see cref="WriteCast"/> — so the type is what tells the
        /// value from its rendering.
        /// </para>
        /// </remarks>
        internal bool IsTextRendering(RexNode node)
        {
            if (IsTextJsonValue(node))
                return true;

            // A plain JSON_QUERY is a rendering of the fragment at the path, not the fragment, so an
            // operator written over it is held to the same tests the scalar accessor is.
            if (IsPlainJsonQuery(node))
                return true;

            return node is RexInputRef reference
                && IsCharacter(reference)
                && reference.getIndex() >= 0
                && reference.getIndex() < _readings.Count
                && _readings[reference.getIndex()] is CosmosReading.Text or CosmosReading.JsonText;
        }

        /// <summary>
        /// Recognises a case fold — <c>UPPER</c> or <c>LOWER</c> of one argument — and returns what it
        /// folds.
        /// </summary>
        /// <remarks>
        /// By name, for the reason the named functions are dispatched by name.
        /// </remarks>
        /// <param name="node">The expression to inspect.</param>
        /// <param name="upper">On success, whether the fold is to upper case.</param>
        /// <returns>The folded expression, or <c>null</c> where this is not a fold.</returns>
        internal static RexNode? TryCaseFoldOperand(RexNode node, out bool upper)
        {
            upper = false;

            if (node is not RexCall call || call.getOperands().size() != 1)
                return null;

            switch (call.getOperator().getName())
            {
                case "UPPER":
                    upper = true;
                    return (RexNode)call.getOperands().get(0);
                case "LOWER":
                    return (RexNode)call.getOperands().get(0);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Recognises <c>CAST(&lt;document value&gt; AS VARCHAR)</c> where the value can be sent as it
        /// stands and turned into text as it is read back, and returns the value underneath.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>For a projection, and nothing else.</b> A comparison has a literal to reason from, which is
        /// what <see cref="TryTextCastOperand"/> uses; a projection carries the value instead, so the
        /// question is not which documents match but what each one returns. Nothing at the service
        /// answers it — measured over one document per JSON type, neither the bare path nor
        /// <c>ToString</c> reproduces what Calcite renders, and <c>DESIGN.md</c> keeps the table. What
        /// does reproduce it is the reader: Calcite's cast over an <c>ANY</c> value is Java's rendering
        /// of the very object <see cref="Client.CosmosJson.GetNatural"/> already builds, so the service
        /// sends the value and <see cref="Client.CosmosJson.GetText"/> renders it. The rows are the
        /// same ones; what changes is that the projection pushes, and with it the statement stops
        /// carrying whole documents.
        /// </para>
        /// <para>
        /// <b>The value is rendered, so it is no longer addressable.</b> The column projects text where
        /// the document holds something else, and a filter or a sort above it would be written against
        /// the raw path and mean something different — <c>= '30'</c> is true of the rendered number and
        /// false at the service, and as text <c>10</c> sorts before <c>9</c>. Such a column therefore
        /// binds to no path, exactly as a computed one does, and the operators reading it decline.
        /// </para>
        /// <para>
        /// <b>Only an undecorated <c>VARCHAR</c>.</b> A width is a second conversion this does not
        /// perform: measured, <c>VARCHAR(3)</c> truncates and <c>CHAR(8)</c> pads, so both are refused
        /// and the cast stays in process. <c>SAFE_CAST</c> is admitted beside <c>CAST</c> because the
        /// two differ only in what happens when a conversion fails, and rendering a value as text
        /// never does.
        /// </para>
        /// <para>
        /// <b>Only a document value — typed <c>ANY</c> or <c>VARIANT</c>; see
        /// <see cref="IsDocumentValueType"/>.</b> The same cast over <c>JSON_VALUE</c> drops in a
        /// comparison — see <see cref="IsRenderedDocumentValue"/> — and does not here, because a
        /// projection has no literal to exclude the cases on. Measured, <c>JSON_VALUE</c> answers null
        /// for an object or an array where the reader renders one as <c>{x=1}</c> or <c>[x, y]</c>, so
        /// a rendered column over it would carry text for a document Calcite carries nothing for. It
        /// stays in process, and the filters around it push regardless.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression to inspect.</param>
        /// <returns>The value underneath the cast, or <c>null</c> where this is not that shape.</returns>
        internal static RexNode? TryRenderedTextOperand(RexNode node)
        {
            if (node is not RexCall call)
                return null;

            var kind = KindOf(call);
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return null;

            if (call.getOperands().size() != 1)
                return null;

            // An unspecified precision is what a bare VARCHAR carries -- Calcite's type system gives
            // the type no default -- so this is the test for a cast that declares no width.
            var type = call.getType();
            if (type?.getSqlTypeName() != SqlTypeName.VARCHAR || type.getPrecision() != org.apache.calcite.rel.type.RelDataType.PRECISION_NOT_SPECIFIED)
                return null;

            var operand = Operand(call, 0);
            if (IsDocumentValueType(operand.getType()?.getSqlTypeName()) == false)
                return null;

            return operand;
        }

        /// <summary>
        /// Translates a projected expression, rendering the one cast a projection may send without.
        /// </summary>
        /// <remarks>
        /// The single place the decision is made, so that the rule admitting a projection and the node
        /// implementing it cannot disagree about which expressions are renderable — a disagreement that
        /// shows up as a projection accepted by the planner and refused during implementation.
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="reading">
        /// On return, how the value is to be read back — as the field's declared type, or as one of
        /// the renderings a dropped cast leaves to the reader. See <see cref="TryRenderedTextOperand"/>.
        /// </param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="CosmosTranslationException">The expression has no Cosmos equivalent.</exception>
        public string TranslateProjection(RexNode node, out CosmosReading reading)
        {
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            var expression = TranslateProjectionCore(node, out reading);

            // A column the reader has no reading for must not go down. The statement would be right
            // and the row unbuildable, which is a failure at the first row -- and a read that has
            // begun has already sent a caller 200 and the headers, so what arrives is a truncated
            // body rather than an error (#149). Declining is the ordinary refusal this class is built
            // out of: the column stays in process, where Calcite computes it, and the query answers.
            //
            // Only a Typed reading asks the reader about the declared type at all. Text, Json and
            // JsonText read what the service sent whatever the plan calls it, which is why a
            // projected JSON fragment is not caught here for being an object in a VARCHAR column.
            if (reading == CosmosReading.Typed && Client.CosmosJson.CanRead(node.getType()) == false)
                throw new CosmosTranslationException(
                    $"A projection typed '{node.getType()}' has no Cosmos JSON reading, so pushing it would render a column that cannot be read back.");

            return expression;
        }

        /// <inheritdoc cref="TranslateProjection(RexNode, out CosmosReading)" />
        /// <remarks>
        /// The recognisers, without the readability gate <see cref="TranslateProjection"/> puts in
        /// front of them. Separate because every shape below returns early with a reading of its own,
        /// and one question asked once after them all is the only way to be sure none skipped it.
        /// </remarks>
        string TranslateProjectionCore(RexNode node, out CosmosReading reading)
        {
            // A cast to VARCHAR over a SQL/JSON accessor converts nothing -- the accessor is already
            // VARCHAR -- so it is dropped here rather than treated as a rendering, and what is left
            // is rendered as the accessor it is.
            node = StripRedundantTextCast(node);

            // A cast that differs from its operand only in nullability converts nothing either -- see
            // IsNullabilityAssertion -- and over an accessor its column is the accessor's, read as the
            // accessor is. The split rule hands one up as a column of its own: the asserted branch of
            // a COALESCE whose CASE stays in process. Read as the declared VARCHAR it failed at a
            // stored number, which the accessor renders as text.
            if (node is RexCall assertion && IsNullabilityAssertion(assertion) && Operand(assertion, 0) is RexCall asserted && IsJsonAccessor(asserted))
                node = asserted;

            if (TryRenderedTextOperand(node) is RexNode operand)
            {
                reading = CosmosReading.Text;
                return TranslateValue(operand);
            }

            // CLR_ST_GEOG_ASGEOJSON over a stored geography is the property itself. The document holds the
            // GeoJSON, so parsing it into a geometry and writing it back out is a round trip the service
            // never asked for. What comes back is an object where the projection is declared VARCHAR, so
            // it is read as the JSON the service sent — the same reading the DOC column takes, and for
            // the same reason.
            if (TryGeoJsonProjection(node, out var geography) && geography is not null)
            {
                reading = CosmosReading.Json;
                return geography.ToString();
            }

            // A cast to UUID over a path the container declared a UUID spelling for. The service sends
            // the stored text and the reader parses it, which is the read side of the same reasoning a
            // filter already leans on -- see TryStoredUuidProjection.
            if (TryStoredUuidProjection(node, out var stored) && stored is not null)
            {
                reading = CosmosReading.Typed;
                return stored;
            }

            // A parse of a path the container declared a shape the format reads exactly. The same
            // move as the UUID cast above and for the same reason -- the service holds the value in a
            // form the plan's type is a reading of -- and the reason to bother is the sort above it.
            // See TryStoredInstantProjection.
            if (TryStoredInstantProjection(node, out var instant) && instant is not null)
            {
                reading = CosmosReading.Typed;
                return instant;
            }

            // A stored geography projected as itself. The constructor disappears and the path goes
            // down, the reader putting it back -- the same shape the UUID cast takes, and for the same
            // reason: the service holds the value in a form the plan's type is a reading of. Guarded,
            // unlike the predicate form -- see TryStoredGeographyProjection.
            if (TryStoredGeographyProjection(node, out var shape) && shape is not null)
            {
                reading = CosmosReading.Typed;
                return shape;
            }

            if (TryJsonValueProjection(node, out var guarded) && guarded is not null)
            {
                reading = CosmosReading.Text;
                return guarded;
            }

            // A JSON_QUERY whose RETURNING names an array type asks for the array at the path, which
            // the service holds and the reader builds a list out of. Guarded rather than bare for the
            // reason the text form is -- see TryJsonArrayProjection.
            if (TryJsonArrayProjection(node, out var collection) && collection is not null)
            {
                reading = CosmosReading.Typed;
                return collection;
            }

            // The other half of SQL/JSON: the fragment at the path, as text. Its guard is the
            // complement of the scalar one -- see TryJsonQueryProjection.
            if (TryJsonQueryProjection(node, out var fragment) && fragment is not null)
            {
                reading = CosmosReading.JsonText;
                return fragment;
            }

            // An array RETURNING on JSON_VALUE is refused rather than rendered, and the refusal is the
            // whole behaviour -- see IsArrayReturningJsonValue, and IsJsonAccessor, which refuses the
            // same spelling to every other clause. Declining sends the column in process, where a
            // caller gets what the engine gives: null under the default NULL ON ERROR, and a raw
            // failure under ERROR ON ERROR. The second is the half that settles it, a pushed column
            // having no way to raise.
            //
            // Stated here as well as at the gate because a projection is the one clause that must
            // say so out loud: everywhere else a refusal is a pushdown not taken, and here it is the
            // difference between an answer and an invented one.
            if (IsArrayReturningJsonValue(node))
                throw new CosmosTranslationException(
                    "JSON_VALUE with an array RETURNING is not a construct SQL defines -- the clause names a predefined scalar type -- so it is left to the engine, which answers null. JSON_QUERY is the accessor that returns an array.");

            // Every shape above is one this recognises whole and renders with the guard its own
            // reading needs. What is left is a compound expression -- a CASE, a function call, a
            // concatenation -- and an accessor inside one rendered as the bare path, dropping the
            // guard merely by being nested (#131). The flag is what carries it down; see
            // WriteGuardedAccessor for what each accessor gets.
            reading = CosmosReading.Typed;

            _guardNestedAccessors = true;

            try
            {
                return TranslateValue(node);
            }
            finally
            {
                _guardNestedAccessors = false;
            }
        }

        /// <summary>
        /// Renders a cast to <c>UUID</c> over a path the container declared a UUID spelling for, as
        /// the path itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The read side of what the filter already does.</b> A declared stored form lets a
        /// comparison against a <c>UUID</c> become a comparison of stored strings, because the form
        /// says a value has exactly one spelling — see <see cref="Metadata.CosmosFactRewriter"/>. The
        /// same form says the stored text <em>is</em> a UUID, and that is what a projection needs: the
        /// service sends the text it holds and <see cref="Client.CosmosJson.GetValue"/> reads it back
        /// as the <c>UUID</c> the plan declared, which is the very conversion Calcite's cast performs.
        /// Nothing is computed at the service and nothing is approximated here.
        /// </para>
        /// <para>
        /// <b>Why it is worth rendering at all.</b> A cast left untranslated keeps the whole projection
        /// in process, and a projection in process is one a sort cannot be pushed through — so a query
        /// selecting an identifier and ordering by something else read the container whole and sorted
        /// it in memory, while the same query without the identifier pushed both. Every query that
        /// returns an entity selects its identifier, which is what made this the common case.
        /// </para>
        /// <para>
        /// <b>The guard is the same one, for the same reason.</b> The cast is written over
        /// <c>JSON_VALUE</c>, which answers null for an object, an array and an absent path;
        /// the bare path answers the object. <c>IS_PRIMITIVE</c> is exactly that distinction, so the
        /// guarded path means what the accessor means for every JSON type — including a document that
        /// contradicts the declaration, where both sides then agree on null. Where the stored value is
        /// a scalar that is not a UUID the two agree as well, by both failing.
        /// </para>
        /// <para>
        /// <b>Equality is all that is asked.</b> The column binds to no path afterwards, a cast
        /// resolving to none, so nothing above orders or groups by it; whether the stored order is the
        /// order Calcite compares UUIDs in is therefore never consulted, and the sortable forms are
        /// admitted here on the same terms as the others.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the projection is one of these.</returns>
        bool TryStoredUuidProjection(RexNode node, out string? expression)
        {
            expression = null;

            if (node is not RexCall call)
                return false;

            var kind = KindOf(call);
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return false;

            if (call.getType()?.getSqlTypeName() != SqlTypeName.UUID || call.getOperands().size() != 1)
                return false;

            // A view over a container may write the cast over one to text, which converts nothing.
            var operand = StripRedundantTextCast(Operand(call, 0));
            if (operand is not RexCall accessor || IsJsonAccessor(accessor) == false)
                return false;

            if (TryResolvePath(operand, out var path) == false || path is null)
                return false;

            if (DocumentOf(path) is not JsonDocumentPath document)
                return false;

            if (Facts.RepresentationOf(document) is not Metadata.CosmosRepresentation representation)
                return false;

            if (Metadata.CosmosUuidForms.IsUuid(representation) == false)
                return false;

            expression = Guarded(ScalarReading, path.ToString());
            return true;
        }

        /// <summary>
        /// Refuses an accessor the plan types as temporal over a path the engine could not have read,
        /// so that the pushed plan does not answer where the query raises.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The whole of this class is a claim that the service computes what the engine computes,
        /// and here that claim was false.</b> <c>JSON_VALUE(…, RETURNING TIMESTAMP)</c> over a path
        /// holding an ISO-8601 string <em>raises</em> in process — measured at Calcite's own runtime,
        /// for every shape including the one <c>CAST</c> accepts, because <c>RETURNING</c> asserts the
        /// extracted type rather than converting to it and the generated code wants a
        /// <c>java.lang.Long</c>. Rendered as the bare path it does not raise: the service returns the
        /// string and the reader parses it. So the column answered a value for a query that has none,
        /// and a caller could get rows out of this adapter that the engine cannot produce.
        /// </para>
        /// <para>
        /// <b>Refusing is the whole fix, and it costs what it costs.</b> The column stays in process,
        /// where it raises — which is the answer the query has. What replaces it is the parse: a
        /// caller who writes <c>PARSE_DATETIME(&lt;format&gt;, &lt;path&gt;)</c> names how the text is
        /// read, the engine can read it, and <see cref="TryStoredInstantProjection"/> pushes it. That
        /// is the spelling that works and the one <c>README.md</c> now points at.
        /// </para>
        /// <para>
        /// <b>One choke point rather than one per clause.</b> Every clause reaches an accessor through
        /// <c>WriteCallCore</c>, so asking here covers the projection, the predicate, the sort key and
        /// the aggregate argument at once — and a rewrite that legitimately drops the conversion, as
        /// <see cref="Metadata.CosmosFactRewriter"/> does for a licensed shape, has already replaced
        /// the node by the time this would see it.
        /// </para>
        /// </remarks>
        /// <param name="call">The accessor.</param>
        /// <param name="path">The path it addresses.</param>
        /// <exception cref="CosmosTranslationException">The engine could not have read the shape.</exception>
        void RequireTheEngineCouldRead(RexCall call, CosmosPath path)
        {
            var held = Metadata.CosmosTemporalParse.PartsOf(call.getType()?.getSqlTypeName());
            if (held == Metadata.CosmosTemporalParts.None)
                return;

            if (DocumentOf(path) is JsonDocumentPath document
                && Facts.RepresentationOf(document) is Metadata.CosmosRepresentation representation
                && Metadata.CosmosStoredForms.EngineReads(representation, held))
                return;

            throw new CosmosTranslationException(
                $"An accessor typed '{call.getType()}' over '{path}' has no Cosmos form: measured, Calcite reads a "
                + "stored string into that type only for a calendar date and a whole-second time of day, and raises "
                + "for every ISO-8601 instant. Sending the path down would answer a value where the query raises. "
                + "PARSE_DATETIME with a format the declared shape is read by is the spelling that pushes.");
        }

        /// <summary>
        /// Renders a parse over a path the container declared a shape the format reads exactly, as the
        /// path itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The same shape as the UUID cast, and the reason to have it is a sort rather than a
        /// column.</b> <c>PARSE_DATETIME(&lt;format&gt;, &lt;path&gt;)</c> is a computed projection, so
        /// a projection carrying one stays in process — and Calcite does not transpose a sort through
        /// a projection whose key is a function call, the way it does through a cast. So a query
        /// ordering by a parsed timestamp read the whole container and sorted it in memory, with the
        /// projection collapsing to whole documents beside it, and the key's form was never asked
        /// about. Rendering the column is what puts a <c>CosmosProject</c> under the sort for
        /// <c>CosmosImplementor.OrderingPaths</c> to record the binding on.
        /// </para>
        /// <para>
        /// <b>Two conditions, and neither is the format on its own.</b> The format has to read the
        /// declared shape exactly — <see cref="Metadata.CosmosStoredForms.ParsesExactly"/>, which is
        /// measured rather than derived, the engine's own parser being what gives the format meaning.
        /// And the stored text has to read back as the column's type without the reader inventing
        /// anything, which is <see cref="Metadata.CosmosStoredForms.ReadsBackAs"/>: the service sends
        /// the string it holds and <see cref="Client.CosmosJson"/> converts, so what the column
        /// answers is that conversion rather than the engine's parse, and the two have to agree.
        /// </para>
        /// <para>
        /// <b>The guard is the same one, for the same reason.</b> The parse reads a text accessor,
        /// which answers null for an object, an array and an absent path where the bare path answers
        /// the object; <c>IS_PRIMITIVE</c> is exactly that distinction. Over a document that
        /// contradicts the declaration the two sides agree by both failing — the engine's parse raises
        /// <c>Invalid format</c> and the reader refuses the text.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the projection is one of these.</returns>
        bool TryStoredInstantProjection(RexNode node, out string? expression)
        {
            expression = null;

            if (Metadata.CosmosTemporalParse.TryRead(node, out var text, out var format, out var held) == false || text is null)
                return false;

            // A view over a container may write the parse over a cast to text, which converts nothing.
            var operand = StripRedundantTextCast(text);
            if (operand is not RexCall accessor || IsJsonAccessor(accessor) == false)
                return false;

            if (TryResolvePath(operand, out var path) == false || path is null)
                return false;

            if (DocumentOf(path) is not JsonDocumentPath document)
                return false;

            if (Facts.RepresentationOf(document) is not Metadata.CosmosRepresentation representation)
                return false;

            if (Metadata.CosmosStoredForms.ParsesExactly(representation, format, held) == false)
                return false;

            if (Metadata.CosmosStoredForms.ReadsBackAs(representation, held) == false)
                return false;

            // And the reader has to have a reading for the type at all, which is asked here rather
            // than left to the gate in TranslateProjection so that PARSE_TIMESTAMP's
            // TIMESTAMP WITH LOCAL TIME ZONE is a shape this declines rather than one it claims and
            // then throws over.
            if (Client.CosmosJson.CanRead(node.getType()) == false)
                return false;

            expression = Guarded(ScalarReading, path.ToString());
            return true;
        }

        /// <summary>
        /// Renders <c>JSON_VALUE</c> over a document path as the value the function means.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The bare path is not it, and the difference is a failure rather than a discrepancy.</b>
        /// <c>JSON_VALUE</c> is declared <c>VARCHAR</c>, so a projection of one is read as text; the
        /// path holds whatever the document holds. Over a number the service returns a JSON number
        /// where the plan declared a string, and <c>CosmosJson.GetString</c> refuses to coerce it, so
        /// the pushed statement throws for data the in-process plan renders as <c>30</c>.
        /// </para>
        /// <para>
        /// <b>Two rules to reproduce, and the service has both.</b> A scalar renders as its text,
        /// which is what the <c>Text</c> reading does — the same equivalence
        /// <see cref="TryRenderedTextOperand"/> rests on, Java's rendering of the box the reader
        /// builds, and <c>JSON_VALUE</c>'s implicit <c>RETURNING VARCHAR</c> is that same cast. An
        /// object or an array renders as nothing: SQL/JSON answers null there rather than writing one
        /// out, which is the whole difference between <c>JSON_VALUE</c> and <c>JSON_QUERY</c>.
        /// <c>IS_PRIMITIVE</c> is exactly that distinction — true of a string, a number, a boolean and
        /// a JSON null, false of an object, an array and an absent property, false rather than
        /// undefined, measured in <c>CosmosJsonTypeMeasurementTests</c> — so
        /// <c>IIF(IS_PRIMITIVE(p), p, null)</c> means at the service what the function means here, for
        /// every JSON type.
        /// </para>
        /// <para>
        /// <c>JSON_QUERY</c> is the other half and is <em>not</em> handled: it answers the JSON text of
        /// an object or an array and null for a scalar, which is the mirror guard and a separate
        /// rendering. Until it has one it keeps the typed reading, which is wrong in the same way for
        /// the same reason — recorded in <c>TODO.md</c>.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the projection is one of these.</returns>
        bool TryJsonValueProjection(RexNode node, out string? expression)
        {
            expression = null;

            // Only the accessor read as text. A RETURNING that names another type is not a rendering
            // and the guard would be the wrong one: IS_PRIMITIVE is false of an array, so an
            // array-typed accessor rendered this way answered null for the very documents it was
            // written to read (#119), and a scalar RETURNING was read back as text into a column the
            // plan had declared a number. The type is what tells them apart, and it is the same test
            // CosmosImplementor.TryBindOutput records the reading by.
            if (IsTextJsonValue(node) == false)
                return false;

            var call = (RexCall)node;

            if (IsJsonAccessor(call) == false || TryResolveJsonPath(call, out var path) == false || path is null)
                return false;

            expression = Guarded(ReadingTests(call)!, path.ToString());
            return true;
        }

        /// <summary>
        /// Renders <c>JSON_QUERY</c> whose <c>RETURNING</c> names an array type as the array at the
        /// path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the accessor SQL gives for an array, and the only one rendered as one.</b>
        /// <c>JSON_VALUE</c>'s <c>RETURNING</c> names a predefined scalar type, so an array one is not
        /// a construct with a meaning to implement — <see cref="TranslateProjection"/> refuses it, and
        /// the comment there says why. <c>JSON_QUERY</c>'s may name an array, and measured against
        /// Calcite's own runtime it answers one: <c>JSON_QUERY(doc, '$.v' RETURNING VARCHAR ARRAY)</c>
        /// reads back <c>string[2]{a,b}</c> in process and unnests to two rows. The pushed answer and
        /// the in-process answer are therefore the same answer, which is the test a rendering has to
        /// pass and the one the array <c>JSON_VALUE</c> could never pass.
        /// </para>
        /// <para>
        /// <b>The same expression means this to a traversal.</b>
        /// <c>UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY))</c> resolves through
        /// <see cref="TryResolvePath"/> to the path the array is at and renders to a join over it;
        /// projected, this addresses the same path. <c>CosmosPlannerTests</c>'s
        /// <c>AProjectedArrayAddressesTheSamePathATraversalDoes</c> holds the two together, which is
        /// what #119 was filed about: one expression must not mean two things.
        /// </para>
        /// <para>
        /// <b>Read as the declared type, not as text.</b> <c>CosmosJson.GetValue</c> reads an
        /// <c>ARRAY</c> or a <c>MULTISET</c> as the <see cref="java.util.List"/> Calcite holds a
        /// collection in, which is the value the plan declared — so the reading stays
        /// <see cref="CosmosReading.Typed"/> and nothing renders. The plain form of the same accessor
        /// is <c>VARCHAR</c> and is read as <see cref="CosmosReading.JsonText"/> instead, the fragment
        /// as text — see <see cref="TryJsonQueryProjection"/>, which runs after this one for that
        /// reason.
        /// </para>
        /// <para>
        /// <b>The guard is <c>IS_ARRAY</c>, and it exists for the reader rather than for the engine.</b>
        /// Bare, the path would hand the reader an object where the plan declared a list, and one
        /// oddly-shaped document would fail the whole query; guarded, that document reads null for its
        /// own row and the rest of the read continues. <see cref="TryJsonQueryProjection"/> carries the
        /// complementary guard for the text form, and
        /// <see cref="Rel.Convert.CosmosFilterSplitRule"/> the same reasoning on the filter side.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the projection is one of these.</returns>
        bool TryJsonArrayProjection(RexNode node, out string? expression)
        {
            expression = null;

            if (IsCollectionJsonQuery(node) == false)
                return false;

            var call = (RexCall)node;

            if (IsJsonAccessor(call) == false || TryResolveJsonPath(call, out var path) == false || path is null)
                return false;

            expression = Guarded(ReadingTests(call)!, path.ToString());
            return true;
        }

        /// <summary>
        /// Renders a plain <c>JSON_QUERY</c> over a document path as the fragment the function means.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The mirror of the scalar accessor, and it was wrong in both directions before.</b>
        /// <c>JSON_QUERY</c> is declared <c>VARCHAR</c>, so a projection of one was read as text while
        /// the statement sent the bare path: over an object or an array — the values the function
        /// exists to return — the service answered the raw value and <c>CosmosJson.GetString</c>
        /// refused it, so the column threw; over a scalar it answered the scalar, where SQL/JSON says
        /// the function returns null. Worse than the array case, which merely came back empty.
        /// </para>
        /// <para>
        /// <b>The guard is the complement of <c>IS_PRIMITIVE</c>.</b> <c>JSON_VALUE</c> answers for a
        /// string, a number, a boolean and a JSON null and nothing else; this answers for an object and
        /// an array and nothing else. <c>IS_OBJECT(p) OR IS_ARRAY(p)</c> is that line at the service,
        /// so the rendered column carries a value for precisely the documents the function carries one
        /// for, and null — through the absent property Cosmos elides — for the rest.
        /// </para>
        /// <para>
        /// <b>Read as <see cref="CosmosReading.JsonText"/> rather than as the declared <c>VARCHAR</c>,</b>
        /// because the value that comes back is a fragment and not a string. The reading writes it out
        /// as compact JSON, which is what Calcite's own <c>JSON_QUERY</c> answers — measured, a path
        /// stored as <c>[ "a" ,   "b" ]</c> reads <c>["a","b"]</c> there, so handing over the service's
        /// own bytes would differ by whatever whitespace the document happened to carry.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the projection is one of these.</returns>
        bool TryJsonQueryProjection(RexNode node, out string? expression)
        {
            expression = null;

            // The text-typed form only. A RETURNING that names an array type is a collection column
            // and is rendered as one by TryJsonArrayProjection, which runs first -- reading it as a
            // fragment would hand a string to a column the plan typed an array.
            if (IsPlainJsonQuery(node) == false || IsCharacter(node) == false)
                return false;

            var call = (RexCall)node;

            if (IsJsonAccessor(call) == false || TryResolveJsonPath(call, out var path) == false || path is null)
                return false;

            expression = Guarded(ReadingTests(call)!, path.ToString());
            return true;
        }

        /// <summary>
        /// Determines whether a projection is <c>CLR_ST_GEOG_ASGEOJSON</c> over a stored geography.
        /// </summary>
        /// <remarks>
        /// A projection only. In a predicate the same call is declined and evaluated in process, because
        /// the column carries text where the path carries an object and a comparison against one is not a
        /// comparison against the other.
        /// </remarks>
        bool TryGeoJsonProjection(RexNode node, out CosmosPath? path)
        {
            path = null;

            return node is RexCall call
                && call.getOperator().getName() == Geography.Sql.GeographyOperatorTable.ClrStGeogAsGeoJson.getName()
                && call.getOperands().size() == 1
                && TryResolveGeography(Operand(call, 0), out path);
        }

        /// <summary>
        /// Renders a stored geography projected as itself, as the guarded path the shape lives at.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The read side of what every geography predicate already does.</b> A stored shape reaches
        /// an operator as <c>CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location'))</c>, no
        /// column being typed a geometry; pushed down the constructor disappears and the path is sent,
        /// because Cosmos reads the property as the shape. Selecting one is the same statement with
        /// nothing around it, and <see cref="Client.CosmosJson"/> puts the constructor back — which
        /// until #149 it could not, there being no reading for <c>GEOMETRY</c> at all.
        /// </para>
        /// <para>
        /// <b>Guarded, where the predicate form is bare, and the difference is not an inconsistency.</b>
        /// <see cref="WriteCall"/> holds the nested-accessor guard back for a geography function
        /// because the function consumes the object the path holds and <c>IS_PRIMITIVE</c> is false of
        /// it — guarded, <c>ST_DISTANCE</c> would be handed null for every document that has a shape.
        /// That argument is about the <em>scalar</em> guard and about an operand. Here the path is the
        /// whole column, and what it needs is the guard a <c>JSON_QUERY</c> carries, which admits the
        /// object and the array and excludes the scalar. Measured: over a scalar at the path the
        /// in-process accessor answers null and the constructor is never reached, while the bare path
        /// would send the scalar and fail the read — an error where the engine answers a value. Over an
        /// array both raise, so the guard lets through exactly what agrees.
        /// </para>
        /// <para>
        /// The literal form is not this and falls through to <see cref="WriteGeographyLiteral"/>, which
        /// writes the object out where the call stood: there is no path to guard, and the reader
        /// converts what the statement itself carried.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the projection is one of these.</returns>
        bool TryStoredGeographyProjection(RexNode node, out string? expression)
        {
            expression = null;

            if (node is not RexCall call
                || call.getOperator().getName() != Geography.Sql.GeographyOperatorTable.ClrStGeogGeomFromGeoJson.getName()
                || call.getOperands().size() != 1)
                return false;

            // A container reading its coordinates as a plane refuses a geodesic operator, and a
            // projection is no exception -- the fall-through this stands in front of asks the same
            // question of every geography name it writes.
            RequireGeographyReading(call.getOperator().getName());

            // The literal form resolves to no path, and is written by WriteGeographyLiteral instead.
            if (TryResolveGeography(node, out var path) == false || path is null)
                return false;

            var rendered = path.ToString();
            expression = $"({CosmosOperators.IsObject.getName()}({rendered}) OR {CosmosOperators.IsArray.getName()}({rendered}) ? {rendered} : null)";
            return true;
        }

        /// <inheritdoc cref="TranslateProjection(RexNode, out CosmosReading)" />
        /// <param name="node">The projected expression.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <param name="reading">On success, how the value is to be read back.</param>
        /// <returns><c>true</c> if the expression was translated; otherwise <c>false</c>.</returns>
        public bool TryTranslateProjection(RexNode node, out string? expression, out CosmosReading reading)
        {
            try
            {
                expression = TranslateProjection(node, out reading);
                return true;
            }
            catch (CosmosTranslationException)
            {
                expression = null;
                reading = CosmosReading.Typed;
                return false;
            }
        }

        /// <summary>
        /// Recognises <c>CAST(&lt;document value&gt; AS &lt;number&gt;)</c> whose conversion stays within
        /// one of the stored value, and returns the value underneath — or <c>JSON_VALUE(… RETURNING
        /// DECIMAL)</c>, which is that conversion written as a clause, and returns the rendering it
        /// converts.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not for translation.</b> Nothing renders a numeric cast: Calcite converts the stored
        /// value and the service compares it as it stands, so the two select different documents and
        /// the operator is declined. What this is for is <see cref="Rel.Convert.CosmosFilterSplitRule"/>,
        /// which pushes a restriction the predicate <em>implies</em> and rechecks the predicate itself
        /// above — a bound on the raw value is such a restriction, and the cast is what says the value
        /// is being read as a number at all.
        /// </para>
        /// <para>
        /// <b>A parsing <c>RETURNING</c> is the same question</b>, and was read as the stored value
        /// instead — see <see cref="IsParsingJsonValue"/>. It has no operand to return, the clause
        /// adding none, so the value it converts is written out: the same call typed as the bare
        /// accessor is, which is the rendering a cast to a number reads and which the translator writes
        /// as the path where the bound compares it with a number. Built rather than found, hence the
        /// builder.
        /// </para>
        /// </remarks>
        internal static RexNode? TryNumericCastOperand(RexNode node, RexBuilder rexBuilder) => TryNumericCastOperand(node, rexBuilder, out _, out _);

        /// <inheritdoc cref="TryNumericCastOperand(RexNode, RexBuilder)" />
        /// <param name="node">The expression to inspect.</param>
        /// <param name="rexBuilder">Builds the rendering a parsing <c>RETURNING</c> converts.</param>
        /// <param name="saturates">
        /// On success, the range the conversion saturates to, or <c>null</c> where it does not
        /// saturate. A comparison against one of these two values matches every stored value beyond it,
        /// which a bound must not exclude.
        /// </param>
        /// <param name="roundsFrom">
        /// On success, the magnitude from which the conversion may move a value by a whole unit, or
        /// <c>null</c> where it never does. A window around a bound that far out holds nothing.
        /// </param>
        internal static RexNode? TryNumericCastOperand(RexNode node, RexBuilder rexBuilder, out (java.math.BigDecimal Min, java.math.BigDecimal Max)? saturates, out java.math.BigDecimal? roundsFrom)
        {
            saturates = null;
            roundsFrom = null;

            if (node is not RexCall call)
                return null;

            // RETURNING DECIMAL parses what the path holds -- a stored number as its exact value, a
            // stored string as the number its text spells -- so a stored number it compares lies within
            // one of the literal as surely as under the cast: where the comparison casts the literal down
            // to the clause's type it is truncated toward zero, and the value is not (measured: `= 2.5`
            // keeps a stored 2, and `> 2` a stored 2.4). It never saturates, so no side is spared. The
            // clause applies no precision of its own -- a stored 12345 under RETURNING DECIMAL(5, 1) is
            // 12345 -- but the comparison may: a literal with more digits casts the value up instead,
            // CAST(...):DECIMAL(11, 1) against 12345, which checks the precision and which this does not
            // read through; and a literal whose whole digits the clause's type cannot hold raises when
            // cast down to it, which CosmosNumericWindow refuses to read. A behaviour clause substitutes a
            // value the path does not hold, and is not this.
            if (IsParsingJsonValue(call))
            {
                if (IsPlainJsonValue(call) == false)
                    return null;

                var types = rexBuilder.getTypeFactory();
                var text = types.createTypeWithNullability(types.createSqlType(SqlTypeName.VARCHAR, 2000), true);
                return rexBuilder.makeCall(text, call.getOperator(), call.getOperands());
            }

            var kind = KindOf(call);
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return null;

            if (call.getOperands().size() != 1)
                return null;

            // The targets whose conversion is known to land within one of the stored value, which is
            // what a caller can state a bound from. Measured against Calcite's own runtime, and four
            // plausible members are absent because measuring them is what ruled them out:
            //
            //   INTEGER, BIGINT  over a raw value, truncate toward zero and saturate at the limits --
            //                    within one, except at the limits, which the caller handles. Over
            //                    JSON_VALUE's rendering, Java's integer parse of the text: a whole number
            //                    lands exactly, and a point, an exponent or a whole number beyond the
            //                    type raises (ACastOfTheAccessorToANumberParsesTheRendering). A bound only
            //                    excludes what raises, which is the recorded divergence.
            //   DOUBLE           identity for a stored number, and the double's parse of its rendering,
            //                    below 2^52. From there half a unit in the last place reaches a half, and
            //                    the stored value and the literal each round to a double: a whole unit
            //                    apart, they can land on one, and = 2^60 keeps a stored 2^60 + 1 (the
            //                    differential corpus, over declared). The caller states nothing there.
            //   SMALLINT, TINYINT  do not saturate, they wrap: toShort(1e30) is -1 and toByte(1e30) is
            //                    255, which bear no relation to the stored value at all.
            //   FLOAT, REAL      round to float precision: float(1e30) differs from 1e30 by 1.5e22.
            //   DECIMAL          throws where the value does not fit the declared precision, and a
            //                    bound that excluded the document would turn a failing query into a
            //                    passing one.
            var target = call.getType()?.getSqlTypeName();
            if (target != SqlTypeName.INTEGER && target != SqlTypeName.BIGINT && target != SqlTypeName.DOUBLE)
                return null;

            var operand = Operand(call, 0);
            if (IsRenderedDocumentValue(operand) == false)
                return null;

            if (target == SqlTypeName.INTEGER)
                saturates = (new java.math.BigDecimal(int.MinValue), new java.math.BigDecimal(int.MaxValue));
            else if (target == SqlTypeName.BIGINT)
                saturates = (new java.math.BigDecimal(long.MinValue), new java.math.BigDecimal(long.MaxValue));
            else
                roundsFrom = new java.math.BigDecimal(Apache.Calcite.Cosmos.Facts.Saturation.Renderings.DoubleUnitFrom);

            return operand;
        }

        /// <summary>
        /// Determines whether a string is one no JSON value other than that string renders as.
        /// </summary>
        /// <remarks>
        /// Conservative on purpose, and each refusal is a value that could have come from somewhere
        /// else: anything that parses as a number, because a stored number renders as digits and which
        /// spelling Calcite produces is not something this decides; <c>true</c>, <c>false</c> and
        /// <c>null</c>; and anything opening with a bracket or a quote, which is how an array or an
        /// object would arrive. The empty string is refused as well, having nothing to distinguish.
        /// </remarks>
        internal static bool IsUnambiguousText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;

            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                return false;

            return value[0] is not ('[' or '{' or '"');
        }

        void WriteChain(StringBuilder builder, RexCall call, string op)
        {
            var operands = call.getOperands();
            if (operands.size() < 2)
                throw new CosmosTranslationException($"Operator '{op}' requires at least two operands.");

            builder.Append('(');

            for (var i = 0; i < operands.size(); i++)
            {
                if (i > 0)
                    builder.Append(' ').Append(op).Append(' ');

                Write(builder, (RexNode)operands.get(i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Whether an odd number of <c>NOT</c>s encloses what is being written.
        /// </summary>
        /// <remarks>
        /// See <see cref="WriteComparison"/>. The boolean connectives pass it through -- negating a
        /// conjunction negates the position of both its arms -- and everything else clears it, because
        /// the position of a value is not the position of a predicate. Cleared in one place,
        /// <see cref="WriteCallCore"/>, for every call <see cref="CarriesPosition"/> does not name.
        /// </remarks>
        bool _negated;

        /// <summary>
        /// Whether what is being written is read for its value, where SQL's unknown is a null that has
        /// to come back, rather than for its truth alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A predicate's truth and its value are two questions, and every guard here answers the
        /// first.</b> A comparison in a <c>WHERE</c> is written to be false where SQL's is unknown,
        /// because a row is discarded for either — see <see cref="WriteComparison"/> — and the
        /// service's own equality is false over a JSON null where SQL's is unknown, which needs no
        /// guard for the same reason. Read as a value, false and unknown are different answers.
        /// Measured against the emulator over products, whose category is null in one document and
        /// absent in another: <c>(category &lt;&gt; 'bikes') = FALSE</c>, and the same <c>IN
        /// (FALSE)</c>, kept both, the guarded inequality being false over them where SQL's is
        /// unknown; <c>(category = 'bikes') = FALSE</c> kept the null one; and <c>category =
        /// 'bikes'</c> projected, or grouped by, answered false for the null category where SQL
        /// answers null.
        /// </para>
        /// <para>
        /// <b>Where it is set.</b> At the top of <see cref="TranslateValue"/>, which a projection
        /// goes through, and for every operand of an operator that reads its operands as values —
        /// a comparison, a function, a cast, arithmetic. The connectives pass it on, as they pass the
        /// <c>NOT</c>'s position on, and a <c>CASE</c> passes it to its arms and clears it for its
        /// conditions, which select an arm by their truth. <see cref="Translate(RexNode)"/> starts
        /// without it: a <c>WHERE</c>, a join condition and a traversal's filter read truth alone.
        /// </para>
        /// <para>
        /// <b>What it declines.</b> A comparison Calcite types nullable, which is the one whose SQL
        /// value can be unknown: none of its renderings answer null exactly where SQL does, and
        /// rendering one that did would be a new thing to measure. Typed <c>NOT NULL</c> — the
        /// comparison of an <c>id</c>, a <c>_ts</c> or a literal — it is two-valued at both ends and
        /// is written as before. A truth test over a stored boolean is written in its two-valued form
        /// — see <see cref="WriteTruthTest"/>. <c>IS NULL</c> is two-valued as written, and
        /// <c>IS NULL</c> over a predicate is declined on its own account.
        /// </para>
        /// </remarks>
        bool _value;

        /// <summary>
        /// Writes <c>NOT</c>, flipping the position its operand is written in.
        /// </summary>
        void WriteNot(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 1);

            var saved = _negated;
            _negated = saved == false;

            builder.Append("(NOT ");
            Write(builder, Operand(call, 0));
            builder.Append(')');

            _negated = saved;
        }

        /// <summary>
        /// Writes a comparison, restoring what SQL says about one against <c>NULL</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// SQL's comparison against <c>NULL</c> is unknown, and a row is kept only where the predicate
        /// is true -- so an unknown discards the row in a positive position, and, because negating an
        /// unknown leaves it unknown, discards it in a negated position too. The service is two-valued
        /// over null and disagrees in both directions. Measured against a container carrying a
        /// null-valued property:
        /// </para>
        /// <list type="bullet">
        /// <item><description><c>c.category = 'bikes'</c> over a null is <b>false</b>, which discards
        /// the row -- the same as SQL, so nothing is needed.</description></item>
        /// <item><description><c>c.category != 'bikes'</c> over a null is <b>true</b>, which keeps a
        /// row SQL discards.</description></item>
        /// <item><description>Under a <c>NOT</c> the two swap: the false becomes true and keeps a row
        /// SQL discards, and the true becomes false and is right.</description></item>
        /// </list>
        /// <para>
        /// So a guard is needed in exactly one of the two positions per operator, and which one depends
        /// on both. In a positive position the comparison has to be false over a null; in a negated one
        /// it has to be true, so that the <c>NOT</c> above makes it false.
        /// </para>
        /// <para>
        /// Written that way it composes, which is why the position is tracked rather than the negation
        /// being guarded as a whole. A conjunction under a negation has both arms written negated, and
        /// <c>NOT (x = 1 AND y = 2)</c> with <c>x</c> null and <c>y</c> not 2 keeps its row -- unknown
        /// AND false is false, and its negation is true. A guard wrapped around the negation would have
        /// discarded that row, and a guard applied only under <c>NOT</c> made a double negation wrong.
        /// Both were measured before this shape was arrived at.
        /// </para>
        /// <para>
        /// An absent property needs nothing in either position. The service's comparison on one is
        /// undefined, <c>NOT undefined</c> is undefined, and a <c>WHERE</c> keeps neither -- which is
        /// what SQL says about it too. The guard names it anyway, because a term that is already
        /// unreachable costs nothing and reads as deliberate.
        /// </para>
        /// </remarks>
        void WriteComparison(StringBuilder builder, RexCall call, string op)
        {
            RequireOperandCount(call, 2);

            var negated = _negated;
            var value = _value;
            var notEquals = KindOf(call) == SqlKind.__Enum.NOT_EQUALS;

            // Read for its value, a comparison SQL can answer unknown for is declined: every form
            // written below is false somewhere SQL's is unknown, which a WHERE cannot tell apart and a
            // column, a grouping key or an enclosing comparison can. See _value.
            if (value && call.getType()?.isNullable() != false)
                throw new CosmosTranslationException("A comparison read as a value is unknown where an operand is null, and the service's comparisons answer false there -- the guard that makes them right in a WHERE is what makes them wrong as a value.");

            // Operands are values, and a value has no position. Cleared so that a comparison nested
            // inside one is not written as though the NOT above applied to it.
            _negated = false;
            _value = true;

            try
            {
                // The one operator already true over a null needs the guard in the position the others
                // do not.
                if (negated == notEquals)
                {
                    WriteComparand(builder, call, op);
                    return;
                }

                var paths = new List<(string Path, IReadOnlyList<SqlFunction>? Tests)>();
                CollectGuardPaths(call, paths);

                if (paths.Count == 0)
                {
                    WriteComparand(builder, call, op);
                    return;
                }

                // The guard says SQL's comparison is unknown exactly where a path it reads is null,
                // which holds through an operator that answers null for a null and through nothing
                // else. A CASE does not -- COALESCE(label, 'none') is 'none' where label is absent --
                // and neither does a null or type test, so over one the guard would decide rows the
                // comparison does not. Measured over the typed container, NOT (COALESCE(label,
                // 'none') = 'x') and COALESCE(label, 'none') <> 'x' each lost the null label and the
                // absent one, which SQL keeps. See ObservesAbsence.
                if (ObservesAbsence(Operand(call, 0)) || ObservesAbsence(Operand(call, 1)))
                    throw new CosmosTranslationException("A comparison over an expression that answers a value where a path it reads is absent has no guard to restore SQL's unknown in this position: the guard would test the path, and the expression does not follow it.");

                builder.Append('(');

                if (negated)
                {
                    // True wherever SQL's comparison is unknown, so that the NOT above discards the
                    // row: where an operand is null, which for an accessor is wherever its reading
                    // answers null -- see ReadingTests. Over an object the service's `c.label = 'bikes'`
                    // is false, its negation true, and the row was kept where JSON_VALUE's null keeps
                    // nothing; measured over the typed container, NOT (label = 'bikes') gained the
                    // array and the object. The same for a RETURNING, whose `=` had no type test under
                    // a NOT where its `!=` had one in the positive position.
                    WriteComparand(builder, call, op);

                    foreach (var (path, tests) in paths)
                    {
                        builder.Append(" OR IS_NULL(").Append(path).Append(") OR NOT IS_DEFINED(").Append(path).Append(')');

                        if (tests is not null)
                            builder.Append(" OR NOT ").Append(AnyOfOperand(tests, path));
                    }
                }
                else
                {
                    // False wherever SQL's comparison is unknown, which discards the row here.
                    foreach (var (path, _) in paths)
                        builder.Append("IS_DEFINED(").Append(path).Append(") AND NOT IS_NULL(").Append(path).Append(") AND ");

                    // And where the reading answers null for more than that. The one comparison that
                    // reaches here is the inequality, and through a typed accessor this is the test of
                    // the type it names. Measured against a real account: the service's ordering
                    // comparisons are *undefined* across JSON types, so `c.v > 10` already returns no
                    // string and no boolean; `!=` is the exception and answers true for every value of
                    // another type. Calcite would have thrown on those documents -- RETURNING asserts
                    // the type rather than converting it, ikvmnet/calcite-dotnet#120 -- and SQL:2016
                    // says they are an error condition answering null, which no comparison keeps.
                    // Restricting is therefore what the standard asks for, and closer to it than
                    // either the engine or the unrestricted pushdown.
                    foreach (var (path, tests) in paths)
                        if (tests is not null)
                            builder.Append(AnyOfOperand(tests, path)).Append(" AND ");

                    WriteComparand(builder, call, op);
                }

                builder.Append(')');
            }
            finally
            {
                _negated = negated;
                _value = value;
            }
        }

        /// <summary>
        /// Writes a cast, which is only ever a cast of a numeric literal to an approximate type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A cast over a document value is still refused</b>, and everything said about that
        /// elsewhere in this type stands: Calcite converts the stored value and the service compares it
        /// as it stands, so the two select different documents. This case is disjoint from that one —
        /// the operand has to be a literal, whose value is known while the statement is being written.
        /// </para>
        /// <para>
        /// What makes it worth writing is that comparing against a function returning a double puts one
        /// here. <c>VECTORDISTANCE(c.embedding, …) &lt; 0.5</c> arrives as
        /// <c>&lt;(VECTORDISTANCE(…), CAST(0.5):DOUBLE NOT NULL)</c>, because the comparison coerces the
        /// literal to the function's type — and declining the cast declines the predicate, which is the
        /// half of such a query that bounds what is read. Calcite's own constant reduction folds this
        /// where a host registers it, and this adapter does not require a host to.
        /// </para>
        /// <para>
        /// Only the approximate targets, because only they are reached this way and because widening an
        /// exact literal to a double is the conversion Calcite would itself have performed. A cast to an
        /// exact type truncates or throws depending on the value, which is a question worth answering
        /// when something asks it.
        /// </para>
        /// </remarks>
        void WriteCast(StringBuilder builder, RexCall call)
        {
            // The raw value at a path, named over its rendering. CosmosFilterSplitRule writes the
            // comparison it pushes against the value the service holds rather than the text Calcite
            // renders, and says so by typing the accessor ANY -- see IsTextRendering for why the type is
            // what decides it. A field bound to an accessor is cast to ANY instead of re-typed: a host
            // transposing the filter through the projection replaces the reference with the
            // projection's expression and the type that said it goes with it, where a cast is a call
            // of its own and survives. Rendered as the path, which is what the cast names.
            if (IsRawValueMarker(call))
            {
                Write(builder, Operand(call, 0));
                return;
            }

            // The cast Calcite's type coercion adds to the other side of a comparison with a VARIANT
            // column -- a promoted declared-path key; see CosmosTable.getRowType. The box carries the
            // value unchanged, and the service compares the raw document value against it either way, so
            // it is rendered as the value underneath rather than converted: c.category = @p0 for
            // `category = 'bikes'`, the very statement the ANY-typed column produced before the literal
            // was coerced. StripVariantCoercion is the reading side of the same shape, where the
            // point-read and fact extractors take the literal out of it.
            if (call.getOperands().size() == 1 && call.getType()?.getSqlTypeName() == SqlTypeName.VARIANT)
            {
                Write(builder, Operand(call, 0));
                return;
            }

            // A cast to BOOLEAN over the rendering of a path the facts prove holds a boolean is the
            // stored boolean, and is written as the path -- see TryStoredBoolean for why the proof is
            // the whole of it.
            if (TryStoredBoolean(call) is RexNode stored)
            {
                Write(builder, stored);
                return;
            }

            // A cast that differs from its operand only in nullability converts nothing, and refusing it
            // cost every COALESCE its pushdown (#130). The validator expands COALESCE(x, y) to
            // CASE(IS NOT NULL(x), CAST(x):T NOT NULL, y) before a RexCall exists: the accessor, the
            // null test and the CASE all render, and that one cast -- Calcite asserting what the guard
            // already proved -- failed the whole expression, so the projection lifted and DOC crossed
            // the wire. Nothing about COALESCE or about arrays was involved; a scalar one with a string
            // literal fallback de-pushed identically.
            //
            // Narrow on purpose, in two ways that a test caught rather than the reasoning did. A cast
            // that changes the type family, the precision or the scale is still a conversion and is
            // still refused -- the service would not perform it, which is what the refusal is for. And
            // the direction matters: this is a *nullable operand cast to a non-nullable target*, not
            // any cast whose types match. A cast between two identical types is a marker rather than a
            // conversion -- WriteComparand reads one to decide what an equality is comparing, and
            // CosmosFilterSplitRule writes one to say the comparison is against the raw value -- so
            // stripping those would quietly push equalities the filter side declines on purpose. See
            // CosmosRexTranslatorTests.EqualityThroughACastOverATypedColumnKeepsTheCast and
            // CosmosRexTranslatorTests.Casts.ACastToAnExactTypeIsDeclined, both of which failed to
            // the broader test.
            if (IsNullabilityAssertion(call))
            {
                Write(builder, Operand(call, 0));
                return;
            }

            // A geography bound as a parameter -- see IsGeographyParameter for why the cast converts
            // nothing and what arrives in the slot.
            if (IsGeographyParameter(call))
            {
                Write(builder, Operand(call, 0));
                return;
            }

            if (call.getOperands().size() != 1 || Operand(call, 0) is not RexLiteral literal)
                throw new CosmosTranslationException("A cast of anything but a literal has no Cosmos equivalent.");

            var target = call.getType()?.getSqlTypeName();
            if (target != SqlTypeName.DOUBLE && target != SqlTypeName.FLOAT && target != SqlTypeName.REAL)
                throw new CosmosTranslationException($"A cast to '{target?.getName()}' has no Cosmos equivalent.");

            var value = GetLiteralValue(literal) switch
            {
                long l => (double)l,
                decimal m => (double)m,
                double d => d,
                var other => throw new CosmosTranslationException($"A cast of '{other?.GetType().Name ?? "null"}' to a double has no Cosmos equivalent."),
            };

            builder.Append(_parameters.Add(value));
        }

        /// <summary>
        /// Writes the comparison itself, taking the one cast an equality against text may drop.
        /// </summary>
        /// <remarks>
        /// A bare <c>JSON_VALUE</c> read as text is that cast with nothing written, and an equality
        /// over one is held to the same test. Measured, Calcite keeps a document storing the number 30
        /// for <c>JSON_VALUE(doc, '$.x') = '30'</c>, having rendered the number; the service compares
        /// the number as it stands and does not. So the equality pushes only against text no other
        /// JSON value renders as, where the two select the same documents, and is declined otherwise —
        /// against a number-like literal, against another expression, or with a behaviour clause that
        /// substitutes a value where the path has none. The split rule then pushes what it implies.
        /// </remarks>
        void WriteComparand(StringBuilder builder, RexCall call, string op)
        {
            var left = Operand(call, 0);
            var right = Operand(call, 1);

            // An instant read out of a path, against a parameter: written in the path's own spelling
            // when the value arrives. See TryWriteStoredInstantParameter.
            if (TryInstantParameter(right) is org.apache.calcite.rex.RexDynamicParam onRight && TryWriteStoredInstantParameter(builder, left, onRight, op))
                return;

            if (TryInstantParameter(left) is org.apache.calcite.rex.RexDynamicParam onLeft && TryWriteStoredInstantParameter(builder, right, onLeft, Reverse(op)))
                return;

            // A UUID read out of a path, against a parameter: the same, in the path's UUID spelling.
            // See TryWriteStoredUuidParameter.
            if (TryUuidParameter(right) is org.apache.calcite.rex.RexDynamicParam uuidOnRight && TryWriteStoredUuidParameter(builder, left, uuidOnRight, op))
                return;

            if (TryUuidParameter(left) is org.apache.calcite.rex.RexDynamicParam uuidOnLeft && TryWriteStoredUuidParameter(builder, right, uuidOnLeft, Reverse(op)))
                return;

            // What each side is beneath the box Calcite's coercion puts around it where the other side
            // is a VARIANT. The box carries the value unchanged, and WriteCast writes it as the value
            // underneath -- so a rendering inside one is still a rendering, and the tests below are
            // asked of it. Asked of the box, they saw no rendering: measured over the typed container,
            // `label = CAST('30' AS VARIANT)` pushed as `c.label = '30'` and lost the stored number 30,
            // which Calcite renders as '30' and keeps. Which is also the shape of an accessor compared
            // with a promoted column, the validator boxing the accessor.
            var leftValue = StripVariantCoercion(left);
            var rightValue = StripVariantCoercion(right);

            if (KindOf(call) == SqlKind.__Enum.EQUALS)
            {
                if (TryTextCastOperand(left, right) is RexNode unwrappedLeft)
                    left = unwrappedLeft;
                else if (TryTextCastOperand(right, left) is RexNode unwrappedRight)
                    right = unwrappedRight;
                else if ((IsTextRendering(leftValue) && IsUnambiguousTextEquality(leftValue, rightValue) == false
                    || IsTextRendering(rightValue) && IsUnambiguousTextEquality(rightValue, leftValue) == false)
                    // Unless what is known says every rendering here holds a string and nothing else, in
                    // which case each is the stored value and the comparand need not be inspected at all
                    // -- which is what lets a parameter through. Known from the facts this conjunct is
                    // rendered from, which never hold what it establishes itself.
                    && RendersOnlyStrings(leftValue, rightValue) == false)
                    throw new CosmosTranslationException("An equality over JSON_VALUE read as text compares a rendering, and only an equality against unambiguous text selects the same documents at the service.");
            }
            else if (IsOrdering(KindOf(call))
                && (IsTextRendering(leftValue) && IsTextComparand(rightValue) || IsTextRendering(rightValue) && IsTextComparand(leftValue))
                && RendersOnlyStrings(leftValue, rightValue) == false)
            {
                // The same gap the equality has, with no exact case to carve out of it. An equality
                // against text no non-string renders as is exact; an ordering comparison never is,
                // because the two orders disagree in kind — Calcite compares renderings as text, and
                // the service compares raw values across JSON types, where a boolean sorts before a
                // number and a number before any string.
                //
                // Measured over the typed container, one document per JSON type. `label > 'bikes'`
                // lost the stored `true`, which renders as `true` and sorts after `bikes` as text
                // while sorting before every string at the service. `label >= '30'` lost the stored
                // 30 and the stored `true` the same way. `label < 'bikes'` lost the stored 30, and
                // `label <> 'bikes'` gained the array and the object, which the accessor answers
                // null for and Calcite therefore excludes.
                //
                // So it is declined here and CosmosFilterSplitRule pushes what it implies: the
                // comparison where the value is a string, and every non-string admitted for the
                // recheck above.
                throw new CosmosTranslationException("A comparison over JSON_VALUE read as text compares a rendering, and the service orders raw values across JSON types rather than their renderings.");
            }

            WriteBinary(builder, left, right, op);
        }

        /// <summary>
        /// Determines whether every side of a comparison that is a rendering is the rendering of a
        /// string, so that the comparison of the renderings is the comparison of what is stored.
        /// </summary>
        /// <remarks>
        /// Every side, not either. A literal or a parameter is no rendering, and against one a string
        /// path suffices; but two renderings are two stored values, and where only one is known to be a
        /// string the other may be a number: <c>JSON_VALUE(a) = JSON_VALUE(b)</c> keeps a document
        /// holding <c>"3"</c> and <c>3</c> in Calcite, which compares <c>'3'</c> with <c>'3'</c>, and
        /// not at the service, which compares a string with a number. Either side sufficed once, and a
        /// sibling proving one path a string pushed the raw comparison (review of the oracle in the
        /// planner).
        /// </remarks>
        bool RendersOnlyStrings(RexNode left, RexNode right) =>
            (IsTextRendering(left) == false || IsDeclaredString(left)) && (IsTextRendering(right) == false || IsDeclaredString(right));

        /// <summary>
        /// Writes a comparison between an instant read out of a path and a parameter, the parameter
        /// bound to be written in the path's stored spelling when the statement runs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The literal's lowering, with the value arriving later.</b> Against a literal,
        /// <see cref="Metadata.CosmosFactRewriter"/> writes the literal in the form the container
        /// declares for the path while the plan is made, and the comparison is one of stored strings.
        /// Against a parameter it declined, the parameter being no literal — so the comparison was
        /// weakened to a definedness test and a "changed since" refresh read every document to
        /// compare in process (#182). Nothing about the decision needs the value: whether the stored
        /// strings answer for the instants is a question about the path and the operator, and
        /// <see cref="Metadata.CosmosFactRewriter.TryStoredInstant"/> answers it for both callers.
        /// What is left is writing the value, and <see cref="Client.CosmosQueries.Bind"/> does that
        /// when it exists.
        /// </para>
        /// <para>
        /// <b>A value between two stored spellings cannot be refused, so it is rounded, and the
        /// operator says which way.</b> A literal that does not land on the form keeps its comparison
        /// in process; a parameter's statement is already written. Every stored value lands on the
        /// form, so rounding the parameter onto it in the direction the comparison is insensitive to
        /// changes nothing it keeps — see <see cref="Metadata.CosmosTemporalRounding"/>. The path is
        /// written on the left, the operator reversed where it was read the other way round, so that
        /// the direction is decided once.
        /// </para>
        /// <para>
        /// <b>Which facts.</b> The ones this translator was given, which is the same set the literal's
        /// lowering is decided from in the filter rules: the conjunct cannot prove its own path's form,
        /// a comparison against a parameter establishing nothing, so a guard proving it is a sibling
        /// pushed beside it.
        /// </para>
        /// </remarks>
        /// <param name="builder">The statement under construction.</param>
        /// <param name="temporal">The side that may read a path as an instant.</param>
        /// <param name="parameter">The parameter on the other side.</param>
        /// <param name="op">The operator, with the path on its left.</param>
        /// <returns><c>true</c> where the comparison was written.</returns>
        bool TryWriteStoredInstantParameter(StringBuilder builder, RexNode temporal, org.apache.calcite.rex.RexDynamicParam parameter, string op)
        {
            // The two sides compared as one type. Calcite casts one of them where they differ, and a
            // cast between a DATE and a TIMESTAMP converts.
            if (temporal.getType()?.getSqlTypeName() != parameter.getType()?.getSqlTypeName())
                return false;

            var ordering = op is not ("=" or "!=");

            if (Metadata.CosmosFactRewriter.TryStoredInstant(temporal, ordering, this, () => Facts, CosmosImplementor.DefaultRootAlias, out var representation) is not RexNode accessor)
                return false;

            var rounding = op switch
            {
                ">" or "<=" => Metadata.CosmosTemporalRounding.Down,
                ">=" or "<" => Metadata.CosmosTemporalRounding.Up,
                _ => Metadata.CosmosTemporalRounding.None,
            };

            builder.Append('(');
            Write(builder, accessor);
            builder.Append(' ').Append(op).Append(' ');
            builder.Append(_parameters.Add(new CosmosDynamicValue(parameter.getIndex()) { Form = representation, Rounding = rounding }));
            builder.Append(')');
            return true;
        }

        /// <summary>
        /// Writes a comparison between a UUID read out of a path and a parameter, the parameter bound
        /// to be written in the path's stored spelling when the statement runs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The literal's lowering, with the value arriving later</b>, as
        /// <see cref="TryWriteStoredInstantParameter"/> is for instants. Against a literal,
        /// <see cref="Metadata.CosmosFactRewriter"/> writes the literal in the path's declared spelling
        /// and the comparison is one of stored strings. Against a parameter it declined, so an id
        /// lookup — the shape every Entity Framework and OData key filter takes, both parameterising
        /// the value — was weakened to a definedness test and read the container whole (#207).
        /// <see cref="Metadata.CosmosFactRewriter.TryStoredUuid"/> decides whether the path licenses
        /// the comparison for both callers.
        /// </para>
        /// <para>
        /// <b>An ordering is held to every value rather than to the container's.</b> A confined form
        /// orders its stored strings as the engine orders values only within its sign class when the
        /// engine compares signed, and a literal outside the class is refused for that reason. A
        /// parameter cannot be refused once the statement is written, so an ordering pushes only where
        /// the spelling's order is the engine's for every value — see
        /// <see cref="Metadata.CosmosUuidForms.OrdersEveryValue"/>. An equality never needed the class:
        /// a value no stored string spells matches none, which is the right answer.
        /// </para>
        /// </remarks>
        /// <param name="builder">The statement under construction.</param>
        /// <param name="uuid">The side that may read a path as a UUID.</param>
        /// <param name="parameter">The parameter on the other side.</param>
        /// <param name="op">The operator, with the path on its left.</param>
        /// <returns><c>true</c> where the comparison was written.</returns>
        bool TryWriteStoredUuidParameter(StringBuilder builder, RexNode uuid, org.apache.calcite.rex.RexDynamicParam parameter, string op)
        {
            var ordering = op is not ("=" or "!=");

            if (Metadata.CosmosFactRewriter.TryStoredUuid(uuid, ordering, this, () => Facts, CosmosImplementor.DefaultRootAlias, out var representation) is not RexNode accessor)
                return false;

            if (ordering && Metadata.CosmosUuidForms.OrdersEveryValue(representation) == false)
                return false;

            builder.Append('(');
            Write(builder, accessor);
            builder.Append(' ').Append(op).Append(' ');
            builder.Append(_parameters.Add(new CosmosDynamicValue(parameter.getIndex()) { Form = representation }));
            builder.Append(')');
            return true;
        }

        /// <summary>
        /// Recognises a parameter of type <c>UUID</c>, through any casts to <c>UUID</c> over it, and
        /// returns it.
        /// </summary>
        /// <param name="node">The expression.</param>
        /// <returns>The parameter, or <c>null</c>.</returns>
        static org.apache.calcite.rex.RexDynamicParam? TryUuidParameter(RexNode node)
        {
            if (node.getType()?.getSqlTypeName() != SqlTypeName.UUID)
                return null;

            while (node is RexCall call
                && (KindOf(call) == SqlKind.__Enum.CAST || KindOf(call) == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && Operand(call, 0).getType()?.getSqlTypeName() == SqlTypeName.UUID)
                node = Operand(call, 0);

            return node as org.apache.calcite.rex.RexDynamicParam;
        }

        /// <summary>
        /// Recognises a parameter of a temporal type, through any casts that keep its value, and
        /// returns it.
        /// </summary>
        /// <remarks>
        /// <c>CAST(? AS TIMESTAMP)</c> compared with a <c>TIMESTAMP(3)</c> arrives as
        /// <c>CAST(CAST(?0):TIMESTAMP(0)):TIMESTAMP(3)</c>, and a cast between two precisions of one
        /// type changes nothing at run time — measured, the milliseconds survive the
        /// <c>TIMESTAMP(0)</c> — so the value compared is the value the parameter carries. A cast
        /// between a <c>DATE</c> and a <c>TIMESTAMP</c> converts and is not looked through.
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns>The parameter, or <c>null</c>.</returns>
        static org.apache.calcite.rex.RexDynamicParam? TryInstantParameter(RexNode node)
        {
            var type = node.getType()?.getSqlTypeName();
            if (type != SqlTypeName.TIMESTAMP && type != SqlTypeName.DATE)
                return null;

            while (node is RexCall call
                && (KindOf(call) == SqlKind.__Enum.CAST || KindOf(call) == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && Operand(call, 0).getType()?.getSqlTypeName() == type)
                node = Operand(call, 0);

            return node as org.apache.calcite.rex.RexDynamicParam;
        }

        /// <summary>
        /// Returns the operator that means the same thing with its operands the other way round.
        /// </summary>
        static string Reverse(string op) => op switch
        {
            ">" => "<",
            ">=" => "<=",
            "<" => ">",
            "<=" => ">=",
            _ => op,
        };

        /// <summary>
        /// Determines whether an expression is of a character type.
        /// </summary>
        /// <remarks>
        /// What decides that a comparison is against a rendering. <c>JSON_VALUE</c> read as text is
        /// <c>VARCHAR</c>, so a comparison against another character value is Calcite comparing two
        /// renderings — and the service comparing a raw value against one. A comparison against a
        /// <em>number</em> is not that: nothing types such a call from SQL, and the one that exists
        /// is built by <c>CosmosFilterSplitRule</c> against the raw value on purpose, which is
        /// exactly the comparison the service should make.
        /// </remarks>
        static bool IsCharacter(RexNode node)
        {
            var name = node.getType()?.getSqlTypeName();
            return name == SqlTypeName.VARCHAR || name == SqlTypeName.CHAR;
        }

        /// <summary>
        /// Determines whether the other side of an ordering comparison with a rendering makes it a
        /// comparison Calcite decides by the rendering's text.
        /// </summary>
        /// <remarks>
        /// Text, as <see cref="IsCharacter"/> says; and a document value, typed <c>VARIANT</c> or
        /// <c>ANY</c>, which the validator compares a rendering with by boxing the rendering — the
        /// text in the box, where the service has the raw value at the path. A number is neither, for
        /// the reason <see cref="IsCharacter"/> gives.
        /// </remarks>
        static bool IsTextComparand(RexNode node)
        {
            return IsCharacter(node) || IsDocumentValueType(node.getType()?.getSqlTypeName());
        }

        /// <summary>
        /// Determines whether a kind is one of the ordering comparisons, or the inequality.
        /// </summary>
        /// <remarks>
        /// The inequality belongs with them rather than with the equality it negates: an equality
        /// against unambiguous text is exact, and its negation is not, because the accessor answers
        /// null for an object or an array where the raw value compares unequal to anything.
        /// </remarks>
        static bool IsOrdering(SqlKind.__Enum kind)
        {
            return kind is SqlKind.__Enum.NOT_EQUALS
                or SqlKind.__Enum.LESS_THAN
                or SqlKind.__Enum.LESS_THAN_OR_EQUAL
                or SqlKind.__Enum.GREATER_THAN
                or SqlKind.__Enum.GREATER_THAN_OR_EQUAL;
        }

        /// <summary>
        /// Determines whether an equality over a <c>JSON_VALUE</c> read as text selects the same
        /// documents with the accessor written as the path.
        /// </summary>
        bool IsUnambiguousTextEquality(RexNode accessor, RexNode other)
        {
            // A behaviour clause substitutes a value where the path has none, which is a document
            // the path itself does not match. Visible on the accessor; a field is never bound to
            // one, because such an accessor addresses no path to bind -- see IsPlainJsonValue.
            if (accessor is RexCall call && (IsTextJsonValue(call) ? IsPlainJsonValue(call) : call.getOperands().size() == 2) == false)
                return false;

            // The fragment's text against text: the service compares the object as it stands, which
            // equals no string, and the stored string the literal spells, which JSON_QUERY answers
            // null for. No literal makes the two select the same documents.
            if (IsTextRendering(accessor) && IsScalarRendering(accessor) == false)
                return false;

            if (other is not RexLiteral literal)
                return false;

            try
            {
                return GetLiteralValue(literal) is string text && (IsTextRendering(accessor) ? IsUnambiguousScalarText(text) : IsUnambiguousText(text));
            }
            catch (CosmosTranslationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Collects the rendered paths an expression reads, the largest that resolves winning, each
        /// with the tests its reading answers a value under.
        /// </summary>
        /// <remarks>
        /// The reading is the one the expression names, so a text rendering cast to <c>ANY</c> — the
        /// raw value, named on purpose; see <see cref="WriteCast"/> — collects its path with none.
        /// </remarks>
        void CollectGuardPaths(RexNode node, List<(string Path, IReadOnlyList<SqlFunction>? Tests)> paths)
        {
            var raw = false;

            if (node is RexCall marker && IsRawValueMarker(marker))
            {
                node = Operand(marker, 0);
                raw = true;
            }

            if (TryResolvePath(node, out var path) && path is not null)
            {
                var entry = (path.ToString(), raw ? null : ReadingTests(node));
                if (paths.Contains(entry) == false)
                    paths.Add(entry);

                return;
            }

            if (node is not RexCall call)
                return;

            for (var i = 0; i < call.getOperands().size(); i++)
                CollectGuardPaths(Operand(call, i), paths);
        }

        /// <summary>
        /// Determines whether an expression can answer something other than unknown where a path it
        /// reads is absent.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two consumers, one question.</b> The split rule asks it to decide whether a branch
        /// implies the paths it reads are defined, and pushes <c>IS_DEFINED</c> of each where it
        /// does — see <see cref="Rel.Convert.CosmosFilterSplitRule"/>. The guard a comparison takes
        /// asks it to decide whether SQL's comparison is unknown exactly where those paths are null —
        /// see <see cref="WriteComparison"/>. Both are the claim that the expression follows its
        /// paths into unknown, and an expression that observes absence is the one it is false of.
        /// </para>
        /// <para>
        /// Measured against a real account, a comparison on an absent property is <em>undefined</em>
        /// rather than false, and <c>NOT undefined</c> is not true either, so neither
        /// <c>c.price &gt; 5</c> nor <c>NOT (c.price &gt; 5)</c> matches a document without a
        /// <c>price</c>. The <c>IS_*</c> family is different: each returns a real boolean for an absent
        /// path, so <c>NOT IS_DEFINED(c.price)</c> is genuinely true there. Only <c>IS_DEFINED</c> was
        /// measured; the rest are refused with it. SQL's own null and truth tests observe absence
        /// too, being true or false where their operand is unknown.
        /// </para>
        /// <para>
        /// <b>And a <c>CASE</c>, which was missing.</b> An unknown condition takes the next arm, and
        /// an arm is a value whatever the paths the others read: <c>COALESCE(x, 'none')</c> is
        /// <c>'none'</c> where <c>x</c> is absent, and <c>CASE WHEN category = 'bikes' THEN … ELSE 'z'
        /// END = 'z'</c> is true there. Measured against the emulator, the split rule weakened that
        /// predicate to <c>IS_DEFINED(c.category) AND IS_DEFINED(c.name)</c> and lost the document
        /// with no category; and a guard written over a coalesce lost the documents whose path was
        /// null or absent. <c>COALESCE</c> and <c>NULLIF</c> are named beside it for a caller that
        /// builds them unexpanded.
        /// </para>
        /// <para>
        /// <b>And a <c>SEARCH</c> that decides null</b>, which is a null test folded into a range —
        /// see <see cref="DecidesNull"/>.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns><c>true</c> where the expression can tell an absent path from an unknown.</returns>
        internal static bool ObservesAbsence(RexNode node)
        {
            if (node is not RexCall call)
                return false;

            if (CosmosOperators.IsAbsenceObserving(call.getOperator()))
                return true;

            switch (call.getKind()?.name())
            {
                case nameof(SqlKind.IS_NULL):
                case nameof(SqlKind.IS_NOT_NULL):
                case nameof(SqlKind.IS_TRUE):
                case nameof(SqlKind.IS_NOT_TRUE):
                case nameof(SqlKind.IS_FALSE):
                case nameof(SqlKind.IS_NOT_FALSE):
                case nameof(SqlKind.IS_UNKNOWN):
                case nameof(SqlKind.IS_DISTINCT_FROM):
                case nameof(SqlKind.IS_NOT_DISTINCT_FROM):
                case nameof(SqlKind.CASE):
                case nameof(SqlKind.COALESCE):
                case nameof(SqlKind.NULLIF):
                    return true;

                // A null test folded into the range: Calcite simplifies `x IS NULL OR x = 'a'` to
                // SEARCH(x, Sarg['a'; NULL AS TRUE]), which says outright what it answers over a
                // null. One that answers unknown there is the comparisons it stands for.
                case nameof(SqlKind.SEARCH) when DecidesNull(call):
                    return true;
            }

            var operands = call.getOperands();
            for (var i = 0; i < operands.size(); i++)
                if (ObservesAbsence((RexNode)operands.get(i)))
                    return true;

            return false;
        }

        /// <summary>
        /// Determines whether a <c>SEARCH</c> answers true or false where its operand is null, rather
        /// than unknown.
        /// </summary>
        /// <remarks>
        /// Asked of the node as it stands, because one consumer does not expand it. The split rule
        /// weakens the predicate the planner hands it, and over a projection Calcite's own
        /// simplification hands it the folded form: <c>INITCAP(sku) IS NULL OR INITCAP(sku) = 'B-2'</c>
        /// arrived as <c>SEARCH(INITCAP(sku), Sarg['B-2'; NULL AS TRUE])</c>, read as strict, and was
        /// weakened to <c>IS_DEFINED(c.metadata.sku)</c> — keeping one product where SQL keeps all
        /// seven.
        /// </remarks>
        static bool DecidesNull(RexCall call)
        {
            return call.getOperands().size() == 2
                && Operand(call, 1) is RexLiteral literal
                && literal.getValue() is org.apache.calcite.util.Sarg sarg
                && sarg.nullAs != org.apache.calcite.rex.RexUnknownAs.UNKNOWN;
        }

        /// <summary>
        /// Determines whether a cast differs from its operand only in nullability: a nullable operand
        /// cast to the same type not null, which is Calcite asserting what a null test has proved.
        /// </summary>
        /// <remarks>
        /// The direction matters, and so does everything else matching: see <see cref="WriteCast"/>,
        /// which writes one as its operand, for the two tests that caught a broader version.
        /// </remarks>
        static bool IsNullabilityAssertion(RexCall call)
        {
            var kind = KindOf(call);

            return (kind == SqlKind.__Enum.CAST || kind == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && call.getType()?.isNullable() == false
                && Operand(call, 0).getType()?.isNullable() == true
                && org.apache.calcite.sql.type.SqlTypeUtil.equalSansNullability(call.getType(), Operand(call, 0).getType());
        }

        /// <summary>
        /// Determines whether a call is the cast that names the raw value at a path over its rendering.
        /// </summary>
        /// <remarks>
        /// The first branch of <see cref="WriteCast"/>, asked as a question so that what reads a path
        /// through one knows it is reading the value the service holds.
        /// </remarks>
        bool IsRawValueMarker(RexCall call)
        {
            var kind = KindOf(call);

            return (kind == SqlKind.__Enum.CAST || kind == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && call.getType()?.getSqlTypeName() == SqlTypeName.ANY
                && IsTextRendering(Operand(call, 0));
        }

        /// <summary>
        /// Recognises a cast to <c>BOOLEAN</c> over the rendering of a path the facts prove holds a
        /// boolean, and returns the rendering — which is written as the path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without the proof the cast is not the service's value, and it is declined.</b> A view
        /// gives a boolean column its type the way it gives any column one, by casting the text
        /// accessor — <c>CAST(JSON_VALUE(DOC, '$.offline') AS BOOLEAN)</c> — and Calcite's cast of
        /// text to a boolean is a parse: measured, it trims the text and reads <c>true</c> and
        /// <c>false</c> in any case, so a stored string <c>"TRUE"</c> is true there and is a string at
        /// the service, where it equals no boolean. A number raises. Nothing about the path can be
        /// pushed from the cast alone.
        /// </para>
        /// <para>
        /// <b>With it the cast is the stored value, exactly.</b> Where the path holds a boolean the
        /// accessor renders it as <c>true</c> or <c>false</c> and the cast reads that back as the
        /// boolean it was; an absent path and a JSON null render as SQL null and the cast keeps them
        /// null. At the service the path is that boolean, undefined where it is absent and null where
        /// it is null — and the service's logic over a value that is not a boolean is SQL's logic over
        /// null: measured against the emulator, <c>NOT</c>, <c>AND</c> and <c>OR</c> treat a null, an
        /// absent path and any other type as unknown, and a <c>WHERE</c> keeps none of them
        /// (<c>CosmosBooleanLogicMeasurementTests</c>). So the path stands for the cast in any boolean position,
        /// which is what lets <c>NOT</c>, a comparison against a boolean and the truth tests in
        /// <see cref="WriteTruthTest"/> each be written over it with nothing added. <c>OrNull</c>
        /// because a JSON null needs nothing excluded: it is null on both sides.
        /// </para>
        /// <para>
        /// <b>Which renderings.</b> A bare <c>JSON_VALUE</c>, or a raw value typed <c>ANY</c> or
        /// <c>VARIANT</c> — <see cref="IsRenderedDocumentValue"/>. Two operands only: a behaviour clause
        /// substitutes a value where the path has none, and the cast of <c>DEFAULT 'true' ON EMPTY</c>
        /// is true over a document the path is absent from. Not a <c>JSON_QUERY</c>, which answers null
        /// for every scalar and so for every boolean. And not a field a projection bound to an
        /// accessor, although the comparisons take one: the binding records the path and that the
        /// column is text, and not the clause, so the one thing this has to refuse could not be seen.
        /// Nothing is lost by it — <c>FILTER_PROJECT_TRANSPOSE</c> takes the filter below the view,
        /// and there the cast is written over the accessor itself.
        /// </para>
        /// <para>
        /// <b>Which facts.</b> The ones this translator was given, which for a filter's conjunct are
        /// the stratum it was admitted from: the declaration closed under what the conjuncts pushed
        /// beside it establish, and never what it does — the sibling-conjunct argument
        /// <see cref="Metadata.CosmosFactRewriter"/> makes, a guard proving the type being applied
        /// beside the comparison it licenses. Rooted at the container only: a traversal alias
        /// addresses an element, and the schema describes the document.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns>The rendering the cast reads, or <c>null</c> where this is not that shape.</returns>
        internal RexNode? TryStoredBoolean(RexNode node)
        {
            if (node is not RexCall call || call.getOperands().size() != 1)
                return null;

            var kind = KindOf(call);
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return null;

            if (call.getType()?.getSqlTypeName() != SqlTypeName.BOOLEAN)
                return null;

            var operand = Operand(call, 0);

            if (IsRenderedDocumentValue(operand) == false)
                return null;

            if (TryResolvePath(operand, out var path) == false || DocumentOf(path) is not JsonDocumentPath document || document.IsRoot)
                return null;

            return Facts.Knows(new JsonFact(document, new JsonClaim.OfType(JsonType.Boolean, OrNull: true)))
                ? operand
                : null;
        }

        /// <summary>
        /// Writes <c>IS TRUE</c>, <c>IS FALSE</c> and their negations over a stored boolean.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two-valued where SQL's are, and that is the only thing that needs care.</b> SQL's truth
        /// tests never answer unknown: <c>x IS TRUE</c> is false where <c>x</c> is null. The service's
        /// <c>c.x = true</c> is false over a JSON null and <em>undefined</em> over an absent path —
        /// measured, <c>CosmosBooleanLogicMeasurementTests</c> — and a <c>NOT</c> above an undefined is
        /// undefined too, so written bare, <c>NOT (x IS TRUE)</c>
        /// would drop a document with no <c>x</c> that SQL keeps. The negative tests therefore keep the
        /// absent case by name, <c>NOT IS_DEFINED(c.x) OR NOT (c.x = true)</c>, which is true over an
        /// absent path, over a null, and over the other boolean, and false over the one it excludes.
        /// The positive tests need nothing in a positive position — undefined is not kept, as false is
        /// not — and take <c>IS_DEFINED</c> beside the comparison under a <c>NOT</c>, where the
        /// difference would show.
        /// </para>
        /// <para>
        /// <b>And read as a value, where it shows too.</b> A projected <c>x IS TRUE</c> written bare
        /// would come back null over an absent path, the undefined leaving the property out of the
        /// row, where SQL answers false. The form a <c>NOT</c> takes is two-valued by construction —
        /// false where the path is absent, and the comparison's own false over a null and over the
        /// other boolean — so it is the form a value takes as well. See <see cref="_value"/>.
        /// </para>
        /// <para>
        /// <b>Over a stored boolean and nothing else.</b> Every other operand is declined, as the whole
        /// family was before: over an arbitrary expression the service's undefined can arise in places
        /// a test like this cannot see, and the shapes that reach here have only the one, at the path.
        /// See <see cref="TryStoredBoolean"/>.
        /// </para>
        /// </remarks>
        /// <param name="builder">The statement under construction.</param>
        /// <param name="call">The test.</param>
        /// <param name="truth">The value tested for: <c>true</c> for <c>IS TRUE</c> and <c>IS NOT TRUE</c>.</param>
        /// <param name="negated">Whether the test is the negative one.</param>
        void WriteTruthTest(StringBuilder builder, RexCall call, bool truth, bool negated)
        {
            RequireOperandCount(call, 1);

            if (TryStoredBoolean(Operand(call, 0)) is not RexNode stored)
                throw new CosmosTranslationException($"'{call.getOperator().getName()}' is written only over a path the container declares a boolean: over anything else the service's undefined does not follow SQL's unknown.");

            var position = _negated;
            var value = _value;

            // The operand is a value, and a value has no position.
            _negated = false;
            _value = true;

            try
            {
                var operand = new StringBuilder();
                Write(operand, stored);
                var path = operand.ToString();

                var comparison = $"({path} = {_parameters.Add(truth)})";

                if (negated)
                    builder.Append("(NOT IS_DEFINED(").Append(path).Append(") OR (NOT ").Append(comparison).Append("))");
                else if (position || value)
                    builder.Append("(IS_DEFINED(").Append(path).Append(") AND ").Append(comparison).Append(')');
                else
                    builder.Append(comparison);
            }
            finally
            {
                _negated = position;
                _value = value;
            }
        }

        /// <summary>
        /// Writes <c>IS NULL</c> and <c>IS NOT NULL</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos distinguishes an absent property (<c>undefined</c>) from one present with a null
        /// value, whereas SQL has only <c>NULL</c>. Both Cosmos states must therefore be tested, or a
        /// filter on a property that is simply missing from a document would not match.
        /// </para>
        /// <para>
        /// <b>Over an accessor that is not the whole of it.</b> <c>JSON_VALUE</c> is null for an object
        /// and an array as well, and <c>JSON_QUERY</c> for every scalar — see
        /// <see cref="ReadingTests"/> — so over a path one of them reads, the test is the reading's
        /// null set and not the path's. Measured against the emulator before this was written,
        /// <c>JSON_VALUE(DOC, '$.metadata') IS NOT NULL</c> kept the document whose <c>metadata</c> is
        /// an object, which Calcite does not, and <c>IS NULL</c> over the mixed-type <c>label</c> lost
        /// the array and the object.
        /// </para>
        /// <para>
        /// <b>Written so that it does not matter what the service says of a type test over an absent
        /// path.</b> The record disagrees with itself there — false in one place, undefined in another
        /// — and the absent case is named by <c>IS_DEFINED</c>, which was measured, ahead of the type
        /// test in both forms: <c>NOT IS_DEFINED(p) OR …</c> is true and <c>IS_DEFINED(p) AND …</c>
        /// false whatever follows, so the test is only ever asked of a value that is there.
        /// </para>
        /// <para>
        /// Over the path itself rather than over the guarded value a projection writes for the
        /// accessor, so that the filter and the projection say it in one spelling — and because the
        /// guarded value is only right here by way of how the service answers a ternary over an
        /// undefined condition, which is another thing the record does not settle.
        /// </para>
        /// </remarks>
        void WriteIsNull(StringBuilder builder, RexCall call, bool negated)
        {
            RequireOperandCount(call, 1);

            // Beneath the VARIANT box, which is null exactly where what it boxes is, and which hid the
            // accessor's reading from the test: measured over the typed container,
            // `CAST(label AS VARIANT) IS NULL` was written over the path's null set alone and lost
            // the array and the object, which JSON_VALUE answers null for.
            var value = StripVariantCoercion(Operand(call, 0));

            if (TryResolvePath(value, out var path) && path is not null)
            {
                if (value is RexCall accessor && IsJsonAccessor(accessor))
                    RequireTheEngineCouldRead(accessor, path);

                var rendered = path.ToString();
                var tests = ReadingTests(value);

                if (negated)
                {
                    builder.Append("(IS_DEFINED(").Append(rendered).Append(") AND NOT IS_NULL(").Append(rendered).Append(')');

                    if (tests is not null)
                        builder.Append(" AND ").Append(AnyOfOperand(tests, rendered));
                }
                else
                {
                    builder.Append("(NOT IS_DEFINED(").Append(rendered).Append(") OR IS_NULL(").Append(rendered).Append(')');

                    if (tests is not null)
                        builder.Append(" OR NOT ").Append(AnyOfOperand(tests, rendered));
                }

                builder.Append(')');
                return;
            }

            // A predicate's unknown is not a value the service can test for. Written as an operand it
            // is written positively, and the positive form of a comparison is guarded to be false
            // wherever SQL's is unknown -- which is what makes it right in a WHERE and what leaves
            // nothing for IS NULL to find. Measured against the emulator,
            // `(category <> 'bikes') IS NULL` kept neither the null category nor the absent one,
            // both of which SQL keeps, and `(category = 'bikes') IS NOT NULL` kept the null one,
            // the service's equality being false there. Unguarded it would be no better: the
            // service's `!=` is true over a null. A stored boolean is a value, and is tested as one
            // -- see TryStoredBoolean.
            if (value is RexCall predicate && value.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN && TryStoredBoolean(predicate) is null)
                throw new CosmosTranslationException("IS NULL over a predicate asks where it is unknown, and the service's comparisons are two-valued over a null where SQL's are not, so there is nothing at the service to test.");

            var operand = new StringBuilder();
            Write(operand, value);
            var text = operand.ToString();

            if (negated)
                builder.Append("(IS_DEFINED(").Append(text).Append(") AND NOT IS_NULL(").Append(text).Append("))");
            else
                builder.Append("(NOT IS_DEFINED(").Append(text).Append(") OR IS_NULL(").Append(text).Append("))");
        }

        /// <summary>
        /// Writes a <c>LIKE</c> predicate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The three-operand form carries an <c>ESCAPE</c> clause, which is refused rather than
        /// dropped: silently ignoring it would change which rows match.
        /// </para>
        /// <para>
        /// The pattern must be a literal, because Cosmos <c>LIKE</c> is not SQL <c>LIKE</c>: Cosmos
        /// additionally reads <c>[…]</c> as a character range where SQL matches the brackets
        /// literally. A literal pattern is checked for brackets and declined when it has any; a
        /// computed pattern cannot be checked and is declined whole, since pushing it would change
        /// which rows match whenever a value contains one.
        /// </para>
        /// <para>
        /// A literal pattern whose only wildcard is a single trailing <c>%</c> is a prefix match,
        /// rendered as <c>STARTSWITH</c> — which the index serves, where <c>LIKE</c> is a scan.
        /// </para>
        /// <para>
        /// A case fold under such a pattern is a case-insensitive match, and the service has one:
        /// <c>UPPER(x) LIKE '%ACADIA%'</c> is <c>CONTAINS(x, 'ACADIA', true)</c>, and the prefix and
        /// suffix forms are <c>STARTSWITH</c> and <c>ENDSWITH</c> with the same flag. See
        /// <see cref="TryCaseInsensitiveMatch"/> for what the pattern has to be.
        /// </para>
        /// </remarks>
        void WriteLike(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 2)
                throw new CosmosTranslationException("LIKE with an ESCAPE clause is not supported.");

            var subject = Operand(call, 0);

            // The rendering gap the comparisons have. Measured: `label LIKE '3%'` matches the stored
            // number 30, which renders as `30`, and the service's STARTSWITH over a number is
            // undefined rather than true. Declined here, and CosmosFilterSplitRule pushes the
            // pattern where the value is a string. Through a case fold as well: `UPPER(label)` is
            // `30` in Calcite, having folded the rendering, and undefined at the service, so
            // `UPPER(label) LIKE '3%'` has the same gap and takes the same guard.
            //
            // And through the coercions the subject is unwrapped from before it is written. A
            // rendering boxed as a VARIANT and cast back to text is the same rendering, and
            // StripTextCoercion then WriteCast's VARIANT branch took it down to the bare path:
            // measured, `CAST(CAST(label AS VARIANT) AS VARCHAR) LIKE '3%'` pushed as
            // STARTSWITH(c.label, '3') and lost the stored 30.
            if (IsRenderingBeneathCoercion(subject) || TryCaseFoldOperand(subject, out _) is RexNode folded && IsRenderingBeneathCoercion(folded))
                throw new CosmosTranslationException("LIKE over JSON_VALUE read as text matches a rendering, and the service's string functions are undefined over a value that is not a string.");

            if (Operand(call, 1) is not RexLiteral patternLiteral || GetLiteralValue(patternLiteral) is not string pattern)
                throw new CosmosTranslationException("LIKE with a computed pattern is not supported: Cosmos gives '[' a meaning SQL does not, and only a literal pattern can be checked for one.");

            if (pattern.IndexOfAny(new[] { '[', ']' }) >= 0)
                throw new CosmosTranslationException("LIKE with a bracket in the pattern is not supported: Cosmos reads a character range where SQL reads the brackets literally.");

            // The raw path under a string operator, once its coercion to VARCHAR is unwrapped -- a
            // VARIANT partition-key column carries one where an ANY value did not; see StripTextCoercion.
            if (TryCaseFoldOperand(subject, out var upper) is RexNode value && TryCaseInsensitiveMatch(pattern, upper, out var function, out var text))
            {
                builder.Append(function).Append('(');
                Write(builder, StripTextCoercion(value));
                builder.Append(", ").Append(_parameters.Add(text)).Append(", true)");
                return;
            }

            if (pattern.EndsWith("%", StringComparison.Ordinal) &&
                pattern.IndexOfAny(new[] { '%', '_' }) == pattern.Length - 1)
            {
                builder.Append("STARTSWITH(");
                Write(builder, StripTextCoercion(Operand(call, 0)));
                builder.Append(", ").Append(_parameters.Add(pattern.Substring(0, pattern.Length - 1))).Append(')');
                return;
            }

            WriteBinary(builder, StripTextCoercion(Operand(call, 0)), Operand(call, 1), "LIKE");
        }

        /// <summary>
        /// Determines whether an expression is a rendering, or one boxed as a <c>VARIANT</c> and cast
        /// back to text — the two coercions a string operator's subject is unwrapped from before it is
        /// written.
        /// </summary>
        bool IsRenderingBeneathCoercion(RexNode node)
        {
            return IsTextRendering(node) || IsTextRendering(StripVariantCoercion(StripTextCoercion(node)));
        }

        /// <summary>
        /// Recognises a pattern that, matched against a case fold, is a case-insensitive prefix,
        /// suffix or substring match, and names the service function that makes it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>UPPER(x) LIKE '%ACADIA%'</c> is what an ORM writes for a case-insensitive
        /// <c>contains</c>, and what a typeahead is; rendered as written it is a scan the service
        /// folds and pattern-matches row by row, and <c>CONTAINS(x, 'ACADIA', true)</c> is the same
        /// question asked of the function that answers it. <c>STARTSWITH</c> and <c>ENDSWITH</c> take
        /// the same third argument.
        /// </para>
        /// <para>
        /// <b>What the pattern has to be, and why.</b> The wildcards may only be one leading and one
        /// trailing <c>%</c>, so that what is between them is matched literally as a unit; a <c>_</c>
        /// or an inner <c>%</c> is a pattern and stays one. The text must already be in the case the
        /// fold produces: <c>UPPER(x)</c> never contains a lowercase letter, so <c>LIKE '%acadia%'</c>
        /// matches nothing under it and is left as written, which answers the same nothing. And the
        /// text must be ASCII. The rewrite rests on the fold Calcite applies and the folding the
        /// service applies under the flag agreeing on every character, and ASCII is where that is
        /// known: Java's <c>toUpperCase</c> maps <c>ß</c> to <c>SS</c> and a ligature to its letters,
        /// which no case-insensitive comparison of the stored text can reproduce. Outside ASCII the
        /// plain form stands, folding at the service under its own rules as it did.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The literal pattern.</param>
        /// <param name="upper">Whether the fold under the pattern is to upper case.</param>
        /// <param name="function">On success, the service function.</param>
        /// <param name="text">On success, the text to match, with the wildcards removed.</param>
        /// <returns><c>true</c> if the pattern is one of the three shapes.</returns>
        static bool TryCaseInsensitiveMatch(string pattern, bool upper, out string function, out string text)
        {
            function = "";
            text = "";

            var leading = pattern.StartsWith("%", StringComparison.Ordinal);
            var trailing = pattern.EndsWith("%", StringComparison.Ordinal);
            if (leading == false && trailing == false)
                return false;

            var start = leading ? 1 : 0;
            var end = trailing ? pattern.Length - 1 : pattern.Length;
            if (end <= start)
                return false;

            var core = pattern.Substring(start, end - start);

            foreach (var c in core)
            {
                if (c is '%' or '_' || c > 0x7F)
                    return false;

                if (upper ? char.IsLower(c) : char.IsUpper(c))
                    return false;
            }

            function = leading && trailing ? "CONTAINS" : trailing ? "STARTSWITH" : "ENDSWITH";
            text = core;
            return true;
        }

        /// <summary>
        /// Writes an element access.
        /// </summary>
        /// <remarks>
        /// <c>ITEM</c> reaches into a value typed <c>ANY</c> — an array subscript, or a property of a
        /// promoted column — and must become a path extension rather than a function call. Only
        /// constant accessors can be resolved this way, since Cosmos paths are static.
        /// </remarks>
        void WriteItem(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 2);

            if (Operand(call, 1) is not RexLiteral accessor)
                throw new CosmosTranslationException("ITEM requires a constant accessor.");

            // The base must itself be a path, otherwise appending a segment would produce
            // nonsense such as "@p0.name" from a bound parameter. Calcite's own operand type
            // checking rejects many such calls before they reach here, but not all of them.
            var target = Operand(call, 0);
            if (target is not RexInputRef && (target is not RexCall inner || KindOf(inner) != SqlKind.__Enum.ITEM))
                throw new CosmosTranslationException("ITEM may only be applied to a field reference or another ITEM.");

            Write(builder, target);

            var value = GetLiteralValue(accessor);

            if (value is string name)
                CosmosSql.WritePropertyAccess(builder, name);
            else if (TryGetArrayIndex(value, out var index))
                CosmosSql.WriteIndexAccess(builder, index);
            else
                throw new CosmosTranslationException("ITEM accessor must be a string property name or an array subscript of one or more.");
        }

        /// <summary>
        /// Recognizes an array subscript and converts it to the service's origin.
        /// </summary>
        /// <remarks>
        /// <para>
        /// SQL subscripts an array from one and Cosmos from zero, so the subscript is emitted as
        /// <c>index - 1</c>. It was passed through unchanged, which read one element early:
        /// <c>JSON_QUERY(c."DOC", '$.tags')[0]</c> returned the first element where SQL returns nothing, and every
        /// subscript after it named its predecessor. Measured against the differential corpus, which
        /// carried no subscript at all until one was looked for.
        /// </para>
        /// <para>
        /// A subscript below one names no element in SQL. Rather than emit the negative subscript that
        /// shifting it produces — which the service may or may not read as counting from the end, and
        /// which has not been measured — it is refused, and Calcite answers the whole operator.
        /// </para>
        /// <para>
        /// The value may arrive as any of several numeric types depending on how the literal was
        /// built, and a non-integral one is not a subscript at all.
        /// </para>
        /// </remarks>
        static bool TryGetArrayIndex(object? value, out int index)
        {
            switch (value)
            {
                case long l when l >= 1 && l <= int.MaxValue:
                    index = (int)l - 1;
                    return true;
                case int i when i >= 1:
                    index = i - 1;
                    return true;
                case decimal m when m >= 1 && m <= int.MaxValue && decimal.Truncate(m) == m:
                    index = (int)m - 1;
                    return true;
                default:
                    index = 0;
                    return false;
            }
        }

        /// <summary>
        /// Writes a <c>CASE</c> expression as a chain of ternary conditionals.
        /// </summary>
        /// <remarks>
        /// Cosmos has no <c>CASE</c>. Its ternary operator is semantically equivalent for the
        /// searched form Calcite produces: operands alternate condition and result, with a final
        /// else branch.
        /// </remarks>
        void WriteCase(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() < 3 || operands.size() % 2 == 0)
                throw new CosmosTranslationException("CASE must have an odd number of operands of at least three.");

            var depth = 0;

            // The arms are in the position the CASE is in, and the conditions in the positive one --
            // see CarriesPosition. A condition's truth alone selects an arm, however the CASE's own
            // value is read, so it is a predicate where the arms may be values -- see _value.
            var position = _negated;
            var value = _value;

            try
            {
                for (var i = 0; i + 1 < operands.size(); i += 2)
                {
                    builder.Append('(');

                    _negated = false;
                    _value = false;
                    Write(builder, (RexNode)operands.get(i));
                    _negated = position;
                    _value = value;

                    builder.Append(" ? ");
                    Write(builder, (RexNode)operands.get(i + 1));
                    builder.Append(" : ");
                    depth++;
                }

                Write(builder, (RexNode)operands.get(operands.size() - 1));
                builder.Append(')', depth);
            }
            finally
            {
                _negated = position;
                _value = value;
            }
        }

        /// <summary>
        /// Scalar functions whose Cosmos counterpart takes the same arguments in the same order
        /// and means the same thing, mapped from the SQL name to the Cosmos name.
        /// </summary>
        /// <remarks>
        /// Verified against the service. Functions needing an argument adjustment are handled
        /// separately below; anything absent from both is declined.
        /// </remarks>
        static readonly Dictionary<string, (string Name, int MinArgs, int MaxArgs)> DirectFunctions = new(StringComparer.Ordinal)
        {
            ["UPPER"] = ("UPPER", 1, 1),
            ["LOWER"] = ("LOWER", 1, 1),
            ["LENGTH"] = ("LENGTH", 1, 1),
            ["CHAR_LENGTH"] = ("LENGTH", 1, 1),
            ["CHARACTER_LENGTH"] = ("LENGTH", 1, 1),
            ["REPLACE"] = ("REPLACE", 3, 3),
            ["CONCAT"] = ("CONCAT", 2, int.MaxValue),
            ["||"] = ("CONCAT", 2, int.MaxValue),
            ["ABS"] = ("ABS", 1, 1),
            ["EXP"] = ("EXP", 1, 1),
            ["LN"] = ("LOG", 1, 1),
            ["LOG10"] = ("LOG10", 1, 1),
            ["POWER"] = ("POWER", 2, 2),
            ["SQRT"] = ("SQRT", 1, 1),
            ["SIGN"] = ("SIGN", 1, 1),
            ["ROUND"] = ("ROUND", 1, 1),
            // Cosmos carries the whole trigonometric set under the names SQL uses, ATAN2 excepted:
            // Cosmos spells it ATN2, after T-SQL.
            ["SIN"] = ("SIN", 1, 1),
            ["COS"] = ("COS", 1, 1),
            ["TAN"] = ("TAN", 1, 1),
            ["COT"] = ("COT", 1, 1),
            // ASIN and ACOS carry a domain, and Cosmos fails the whole query for a value outside it
            // rather than yielding undefined — measured, along with SQRT(-1), which does the same and
            // which this adapter has always pushed, and LOG(0), which does too. This comment used to
            // say LOG(0) was accepted; the measurement beside it said otherwise, and still does. So
            // this is one property of the service rather than a fact about these two, and they are
            // treated as SQRT already is; see OutOfDomainArithmeticFailsTheQuery, which pins the
            // measurement (CosmosAxioms: Service.OutOfDomainArithmeticFailsTheStatement), and
            // DESIGN.md, which records that the choice to live with it is a choice.
            ["ASIN"] = ("ASIN", 1, 1),
            ["ACOS"] = ("ACOS", 1, 1),
            ["ATAN"] = ("ATAN", 1, 1),
            ["ATAN2"] = ("ATN2", 2, 2),
            ["DEGREES"] = ("DEGREES", 1, 1),
            ["RADIANS"] = ("RADIANS", 1, 1),
            // Niladic, and written PI() at both ends.
            ["PI"] = ("PI", 0, 0),
            // The two-argument SQL form — truncate to a number of decimal places — is left out
            // rather than guessed at, the arity of Cosmos's TRUNC not having been verified.
            ["TRUNCATE"] = ("TRUNC", 1, 1),
            // String functions SQL and Cosmos spell alike and mean alike. LEFT and RIGHT clamp rather
            // than fail on a count longer than the string at both ends; REVERSE is a reversal.
            ["LEFT"] = ("LEFT", 2, 2),
            ["RIGHT"] = ("RIGHT", 2, 2),
            ["REVERSE"] = ("REVERSE", 1, 1),
            // SQL repeats a string with REPEAT and Cosmos with REPLICATE. Same arguments, same order,
            // same meaning.
            ["REPEAT"] = ("REPLICATE", 2, 2),
            // The array functions, mapped from their SQL counterparts. ARRAY_SLICE differs only in
            // index origin and is adjusted like SUBSTRING, below; these three agree outright.
            ["ARRAY_CONCAT"] = ("ARRAY_CONCAT", 2, int.MaxValue),
            ["ARRAY_INTERSECT"] = ("SETINTERSECT", 2, 2),
            ["ARRAY_UNION"] = ("SETUNION", 2, 2),
            // Under its own name, and this one is not an index origin: a regular expression dialect
            // is a semantic difference a query cannot see. Cosmos documents PCRE with constructs it
            // does not support, so REGEXP_LIKE and this are two languages, not two spellings — the
            // LIKE bracket measurement is the standing argument.
            ["REGEXMATCH"] = ("REGEXMATCH", 2, 3),
            // The JSON conversions. Cosmos spells these in camel case and the service is
            // case-insensitive about function names, but they are emitted as documented.
            ["ToString"] = ("ToString", 1, 1),
            ["StringToNumber"] = ("StringToNumber", 1, 1),
            ["StringToObject"] = ("StringToObject", 1, 1),
            ["StringToArray"] = ("StringToArray", 1, 1),
            ["StringToBoolean"] = ("StringToBoolean", 1, 1),
            ["ObjectToArray"] = ("ObjectToArray", 1, 1),
            // The type tests. Their argument is an ordinary expression rather than a path, so they need
            // nothing beyond the name — which is already the Cosmos one, these operators being this
            // adapter's own rather than translations of a SQL counterpart. See CosmosOperators.
            ["IS_DEFINED"] = ("IS_DEFINED", 1, 1),
            ["IS_ARRAY"] = ("IS_ARRAY", 1, 1),
            ["IS_BOOL"] = ("IS_BOOL", 1, 1),
            ["IS_NULL"] = ("IS_NULL", 1, 1),
            ["IS_NUMBER"] = ("IS_NUMBER", 1, 1),
            ["IS_OBJECT"] = ("IS_OBJECT", 1, 1),
            ["IS_PRIMITIVE"] = ("IS_PRIMITIVE", 1, 1),
            ["IS_STRING"] = ("IS_STRING", 1, 1),
            // The geography operators, whose Cosmos spellings are the unprefixed ones. The prefix exists
            // because Calcite's own ST_* are planar and mean something else — see DESIGN.md — and it goes
            // away here because the service has only the one reading, which is the geodesic one.
            [Geography.Sql.GeographyOperatorTable.ClrStGeogDistance.getName()] = ("ST_DISTANCE", 2, 2),
            [Geography.Sql.GeographyOperatorTable.ClrStGeogWithin.getName()] = ("ST_WITHIN", 2, 2),
            [Geography.Sql.GeographyOperatorTable.ClrStGeogIntersects.getName()] = ("ST_INTERSECTS", 2, 2),
            [Geography.Sql.GeographyOperatorTable.ClrStGeogIsValid.getName()] = ("ST_ISVALID", 1, 1),
        };

        /// <summary>
        /// Every name the geography package declares, whether or not this adapter translates it.
        /// </summary>
        /// <remarks>
        /// Taken from the operator table rather than matched on the <c>CLR_ST_GEOG_</c> prefix, so that the
        /// refusal over a planar container covers the whole surface the package offers — including the
        /// names translated in process, which would otherwise be pushed past the check by not being here.
        /// The operators cannot be compared by instance: a schema-registered function is rebuilt by
        /// <c>CalciteCatalogReader</c> on every lookup, so only the name survives into the plan.
        /// </remarks>
        static readonly HashSet<string> GeographyFunctions = BuildGeographyFunctions();

        static HashSet<string> BuildGeographyFunctions()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            var operators = Geography.Sql.GeographyOperatorTable.Instance().getOperatorList();
            for (var i = 0; i < operators.size(); i++)
                names.Add(((org.apache.calcite.sql.SqlOperator)operators.get(i)).getName());

            return names;
        }

        /// <summary>
        /// Writes a function whose Cosmos form differs from SQL's only in name.
        /// </summary>
        void WriteNamedFunction(StringBuilder builder, RexCall call)
        {
            var name = call.getOperator().getName();

            if (GeographyFunctions.Contains(name))
            {
                RequireGeographyReading(name);

                // Cosmos has no ST_DWITHIN and no constructor, so both are shapes rather than renames.
                // Written as comparisons rather than switch labels because the names belong to the
                // operators and a case label has to be a constant.
                if (name == Geography.Sql.GeographyOperatorTable.ClrStGeogDWithin.getName())
                {
                    WriteGeographyDWithin(builder, call);
                    return;
                }

                if (name == Geography.Sql.GeographyOperatorTable.ClrStGeogGeomFromGeoJson.getName())
                {
                    WriteGeographyLiteral(builder, call);
                    return;
                }

                // GeoJSON records the geometry type as a member of the shape, so over a stored geography
                // this is a path rather than a function and no spatial machinery is involved.
                if (name == Geography.Sql.GeographyOperatorTable.ClrStGeogGeometryType.getName())
                {
                    WriteGeographyType(builder, call);
                    return;
                }
            }

            switch (name)
            {
                case "SUBSTRING":
                    WriteSubstring(builder, call);
                    return;
                case "ARRAY_SLICE":
                    WriteArraySlice(builder, call);
                    return;
                case "POSITION":
                    WritePosition(builder, call);
                    return;
                case "CARDINALITY":
                    WriteCardinality(builder, call);
                    return;
                case "MEMBER OF":
                    WriteMemberOf(builder, call);
                    return;
                // Rankable, but not restricted to a rank clause the way a full text score is: the
                // reference projects it. So it renders wherever any other function does, and
                // IsScoringFunction lets an ORDER BY over it reach the clause as well. It is written
                // here rather than mapped by name because one of its vectors has to be a path the
                // container declares.
                case "VECTORDISTANCE":
                    WriteVectorDistance(builder, call);
                    return;
                case "FULLTEXTCONTAINS":
                case "FULLTEXTCONTAINSALL":
                case "FULLTEXTCONTAINSANY":
                    WriteFullTextPredicate(builder, call, name);
                    return;

                // The shared vocabulary, rendered as the service's own. Every one of these is a rename
                // and nothing more -- the operand shape is the same, a searched path then keywords --
                // which is what "everything Cosmos offers is expressible" amounts to from this side.
                case "CLR_FT_CONTAINS":
                    WriteFullTextPredicate(builder, call, "FULLTEXTCONTAINS");
                    return;
                case "CLR_FT_CONTAINS_ALL":
                    WriteFullTextPredicate(builder, call, "FULLTEXTCONTAINSALL");
                    return;
                case "CLR_FT_CONTAINS_ANY":
                    WriteFullTextPredicate(builder, call, "FULLTEXTCONTAINSANY");
                    return;

                case "CLR_FT_SCORE":
                    if (_scoring == false)
                        throw new CosmosTranslationException($"'{name}' is only legal in an ORDER BY RANK clause.");

                    WriteFullTextPredicate(builder, call, "FULLTEXTSCORE");
                    return;

                case "CLR_FT_RRF":
                    if (_scoring == false)
                        throw new CosmosTranslationException($"'{name}' is only legal in an ORDER BY RANK clause.");

                    WriteRrf(builder, call);
                    return;

                // A term constructor stands where a keyword goes and says what kind of term it is.
                case "CLR_FT_PHRASE":
                    RequireOperandCount(call, 1);
                    Write(builder, Operand(call, 0));
                    return;

                case "CLR_FT_FUZZY":
                    WriteFuzzyTerm(builder, call);
                    return;

                case "CLR_FT_PREFIX":
                    throw new CosmosTranslationException("CLR_FT_PREFIX has no Cosmos form: the service matches whole analyzed terms and offers no prefix term. The call is left to the engine, which has no body for it, so the query says so rather than matching something else.");

                // Only inside RRF, where it becomes the trailing weights array -- see WriteRrf. Reached
                // here it is a weight somewhere the service has nowhere to put one.
                case "CLR_FT_WEIGHT":
                    throw new CosmosTranslationException("CLR_FT_WEIGHT is a weight on a score fused by RRF, and Cosmos carries those as RRF's trailing array. Outside an RRF there is nothing for it to weigh.");
                case "FULLTEXTSCORE":
                    if (_scoring == false)
                        throw new CosmosTranslationException($"'{name}' is only legal in an ORDER BY RANK clause.");

                    // Same shape as the predicates: a property path, then the terms.
                    WriteFullTextPredicate(builder, call, name);
                    return;

                case "RRF":
                    if (_scoring == false)
                        throw new CosmosTranslationException($"'{name}' is only legal in an ORDER BY RANK clause.");

                    WriteRrf(builder, call);
                    return;
            }

            if (DirectFunctions.TryGetValue(name, out var mapping) == false)
                throw new CosmosTranslationException($"Unsupported function '{name}'.");

            var count = call.getOperands().size();
            if (count < mapping.MinArgs || count > mapping.MaxArgs)
                throw new CosmosTranslationException($"Function '{name}' with {count} argument(s) has no Cosmos equivalent.");

            WriteFunctionCall(builder, call, mapping.Name);
        }

        /// <summary>
        /// Writes <c>CLR_ST_GEOG_DWITHIN</c> as the comparison it is defined as.
        /// </summary>
        /// <remarks>
        /// Cosmos has no <c>ST_DWITHIN</c>. It has <c>ST_DISTANCE</c>, and the reference documents a
        /// distance compared against a constant as what its spatial index answers, so the rewritten form
        /// is the one the service was going to want anyway rather than a fallback.
        /// <para>
        /// <b>The comparison is inclusive.</b> That is PostGIS's reading of <c>ST_DWithin</c>, which is
        /// what the operator is named after. Whether the package's own in-process implementation agrees
        /// at exactly the boundary is unverified, and it matters only once a pushed predicate is
        /// rechecked in process — which <c>DESIGN.md</c> holds until agreement with the service has been
        /// measured.
        /// </para>
        /// </remarks>
        void WriteGeographyDWithin(StringBuilder builder, RexCall call)
        {
            builder.Append("ST_DISTANCE(");
            Write(builder, Operand(call, 0));
            builder.Append(", ");
            Write(builder, Operand(call, 1));
            builder.Append(") <= ");
            Write(builder, Operand(call, 2));
        }

        /// <summary>
        /// Writes <c>CLR_ST_GEOG_GEOMETRYTYPE</c> as the GeoJSON member that already holds it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not a spatial pushdown. A GeoJSON shape records its type as a <c>type</c> member, so over a
        /// stored geography this is <c>c.location.type</c> — an ordinary property read, of the kind the
        /// service answers without any spatial function being involved and without a spatial index
        /// mattering.
        /// </para>
        /// <para>
        /// <b>The two vocabularies agree for anything a container can hold.</b> JTS spells the types
        /// GeoJSON spells, and one more: <c>LinearRing</c>, which GeoJSON has no member for. A stored
        /// shape therefore cannot be one, so the rendered answer and the in-process answer cannot differ
        /// over a value that came out of a document. A geometry built in the query — from WKT, say —
        /// does not resolve to a path and is declined here, which is also where that difference would
        /// otherwise have appeared.
        /// </para>
        /// </remarks>
        void WriteGeographyType(StringBuilder builder, RexCall call)
        {
            if (TryResolveGeography(Operand(call, 0), out var path) == false || path is null)
                throw new CosmosTranslationException(
                    $"'{call.getOperator().getName()}' translates over a geography that resolves to a document path.");

            builder.Append(path.Property("type").ToString());
        }

        /// <summary>
        /// Resolves the document path a geography was read from, seeing through the constructor.
        /// </summary>
        /// <remarks>
        /// A stored geography is written as <c>CLR_ST_GEOG_GEOMFROMGEOJSON</c> over the text a document path
        /// yields, because no column is typed as a geometry — so the path is one level down and the
        /// constructor has to be looked through rather than at.
        /// </remarks>
        public bool TryResolveGeography(RexNode node, out CosmosPath? path)
        {
            if (node is RexCall call
                && call.getOperator().getName() == Geography.Sql.GeographyOperatorTable.ClrStGeogGeomFromGeoJson.getName()
                && call.getOperands().size() == 1)
                node = Operand(call, 0);

            return TryResolvePath(node, out path) && path is not null;
        }

        /// <summary>
        /// Writes <c>CLR_ST_GEOG_GEOMFROMGEOJSON</c> as the thing it names — a document path, or the object.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos has no constructor to translate this into: a geography in a statement <em>is</em> a
        /// GeoJSON object, either a property of the document or a literal written out. So the constructor
        /// disappears in both cases and its argument is written where the call stood.
        /// </para>
        /// <para>
        /// <b>The path case is what makes a stored geography reachable at all.</b> An <c>CLR_ST_GEOG_*</c>
        /// operator takes a geometry and no column has that type, so a shape in a document reaches one
        /// only by being parsed out of text — <c>CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location'))</c>.
        /// In process that is exactly what happens. Pushed down it is not: the service reads the property
        /// as the shape, so the text and the parsing are a round trip it never needed, and what it wants
        /// is the path.
        /// </para>
        /// <para>
        /// Anything else is declined rather than guessed at. A constructor over a computed string would
        /// have to be evaluated to be rendered, and evaluating it is what the service is being asked to
        /// do; such a call stays in process, where the geography package answers it.
        /// </para>
        /// </remarks>
        void WriteGeographyLiteral(StringBuilder builder, RexCall call)
        {
            var argument = Operand(call, 0);

            // A stored geography. The constructor disappears and the path is written where the call stood:
            // Cosmos reads the property itself as the shape, so parsing it out and handing back text is a
            // step the service never needed. This is what JSON_QUERY(c."DOC", '$.location') collapses to.
            if (TryResolvePath(argument, out var path) && path is not null)
            {
                builder.Append(path.ToString());
                return;
            }

            // A constant. Cosmos has no constructor to translate into either — a geography in a statement
            // is the GeoJSON object — so the literal is written out as it stands.
            if (argument is RexLiteral literal && GetLiteralValue(literal) is string geoJson)
            {
                builder.Append(geoJson);
                return;
            }

            throw new CosmosTranslationException(
                "A geography translates where its GeoJSON is a literal or resolves to a document path.");
        }

        /// <summary>
        /// Determines whether an expression is a geography bound as a parameter: <c>?</c> typed
        /// <c>GEOMETRY</c>, bare or under the cast that typed it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The cast converts nothing, and it is the only thing that stood in the way</b> (#187).
        /// <c>CAST(? AS GEOMETRY)</c> is how a caller — and Entity Framework, for every spatial
        /// argument — gives a parameter a type, and the validator types the parameter from it: the
        /// plan carries <c>CAST(?0):GEOMETRY</c> over a <c>?0</c> already typed <c>GEOMETRY</c>.
        /// <see cref="WriteCast"/> rendered a cast over a literal only, so a distance to a bound point
        /// was declined whole, and every document was read and measured in process where the same
        /// distance to a literal point pushed as the filter and as the sort.
        /// </para>
        /// <para>
        /// <b>What arrives in the slot is a geometry or nothing</b>, and that is measured rather than
        /// hoped — <c>CalciteGeometryParameterMeasurementTests</c>. The ADO.NET driver converts a
        /// parameter to its declared type before execution: WKT text arrives as a JTS geometry, and
        /// GeoJSON text or a number fails there, before either plan sees it. So the value is one
        /// <see cref="Client.CosmosQueries.Bind"/> already writes as the GeoJSON object a constant
        /// inlines (#156), and the parameter is written where the cast stood, the cast falling away
        /// the way the constructor falls away over a literal — see <see cref="WriteGeographyLiteral"/>.
        /// </para>
        /// <para>
        /// Only <c>GEOMETRY</c> to <c>GEOMETRY</c>. A cast into it from anything else is a parse
        /// that has to happen somewhere, and nowhere here does it.
        /// </para>
        /// </remarks>
        internal static bool IsGeographyParameter(RexNode node)
        {
            if (node is RexCall call && KindOf(call) == SqlKind.__Enum.CAST && call.getOperands().size() == 1)
            {
                if (call.getType()?.getSqlTypeName() != SqlTypeName.GEOMETRY)
                    return false;

                node = Operand(call, 0);
            }

            return node is org.apache.calcite.rex.RexDynamicParam parameter
                && parameter.getType()?.getSqlTypeName() == SqlTypeName.GEOMETRY;
        }

        /// <summary>
        /// Writes a call as <c>NAME(arg, …)</c> over all of its operands.
        /// </summary>
        void WriteFunctionCall(StringBuilder builder, RexCall call, string name)
        {
            builder.Append(name).Append('(');

            for (var i = 0; i < call.getOperands().size(); i++)
            {
                if (i > 0)
                    builder.Append(", ");

                Write(builder, Operand(call, i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>SUBSTRING</c>, adjusting for the differing origin.
        /// </summary>
        /// <remarks>
        /// SQL positions are one-based and Cosmos's are zero-based: over <c>"Hello World"</c>,
        /// <c>SUBSTRING(s, 0, 5)</c> yields <c>"Hello"</c> where SQL's <c>FROM 1</c> does. The
        /// start is therefore emitted as <c>start - 1</c>.
        /// <para>
        /// Cosmos requires a length, so the two-argument SQL form — take the rest of the string —
        /// is declined rather than approximated with an over-long length.
        /// </para>
        /// </remarks>
        void WriteSubstring(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 3)
                throw new CosmosTranslationException("SUBSTRING without an explicit length has no Cosmos equivalent.");

            builder.Append("SUBSTRING(");
            Write(builder, Operand(call, 0));
            builder.Append(", (");
            Write(builder, Operand(call, 1));
            builder.Append(" - 1), ");
            Write(builder, Operand(call, 2));
            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>ARRAY_SLICE</c>, which needs no adjustment.
        /// </summary>
        /// <remarks>
        /// Both take <c>(array, start, length)</c> and both count from zero, so the operands are
        /// written through unchanged. This was emitted as <c>start - 1</c> on the premise that
        /// Calcite's origin is one, the way SQL's <c>SUBSTRING</c> is — measured, it is not, and the
        /// adjustment moved the window one element to the left. Over <c>["outdoor", "steel"]</c>,
        /// <c>ARRAY_SLICE(tags, 1, 1)</c> is <c>["steel"]</c> and the shifted form returned
        /// <c>["outdoor"]</c>, and <c>ARRAY_SLICE(tags, 2, 1)</c> is empty where the shifted form
        /// returned an element.
        /// <para>
        /// The differential corpus carries both of those, which is what said so. It carried them
        /// before this was written, and could not observe them while its oracle was inert.
        /// </para>
        /// <para>
        /// The two-argument form is accepted because Cosmos accepts it — the rest of the array —
        /// though the library's counterpart takes exactly three, so no SQL statement produces it
        /// today.
        /// </para>
        /// </remarks>
        void WriteArraySlice(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() is not (2 or 3))
                throw new CosmosTranslationException("ARRAY_SLICE takes an array, a start, and an optional length.");

            builder.Append("ARRAY_SLICE(");
            Write(builder, Operand(call, 0));
            builder.Append(", ");
            Write(builder, Operand(call, 1));

            if (operands.size() == 3)
            {
                builder.Append(", ");
                Write(builder, Operand(call, 2));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>POSITION</c> in terms of <c>INDEX_OF</c>.
        /// </summary>
        /// <remarks>
        /// <c>INDEX_OF</c> is zero-based and yields <c>-1</c> when absent, so adding one reproduces
        /// SQL exactly on both counts: a match at SQL position 7 and zero for no match.
        /// </remarks>
        void WritePosition(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 2)
                throw new CosmosTranslationException("POSITION with a start offset has no Cosmos equivalent.");

            // SQL is POSITION(substring IN string); INDEX_OF takes the string first.
            builder.Append("(INDEX_OF(");
            Write(builder, Operand(call, 1));
            builder.Append(", ");
            Write(builder, Operand(call, 0));
            builder.Append(") + 1)");
        }

        /// <summary>
        /// Writes <c>CARDINALITY</c> in terms of <c>ARRAY_LENGTH</c>.
        /// </summary>
        /// <remarks>
        /// SQL defines <c>CARDINALITY</c> over a collection <em>or a map</em>, and Cosmos counts only an
        /// array. Counting an object's properties has no Cosmos form — the document column is the whole document,
        /// so the question is real and simply unanswerable here — and the map case is declined rather
        /// than emitted as an array count, which would report nothing meaningful.
        /// </remarks>
        void WriteCardinality(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 1);

            var typeName = Operand(call, 0).getType().getSqlTypeName();
            if (typeName != SqlTypeName.ARRAY && typeName != SqlTypeName.MULTISET)
                throw new CosmosTranslationException($"CARDINALITY over {typeName.name()} has no Cosmos equivalent; ARRAY_LENGTH counts an array.");

            WriteFunctionCall(builder, call, "ARRAY_LENGTH");
        }

        /// <summary>
        /// Writes <c>MEMBER OF</c> in terms of <c>ARRAY_CONTAINS</c>.
        /// </summary>
        /// <remarks>
        /// The operands are the other way round: SQL is <c>&lt;value&gt; MEMBER OF &lt;multiset&gt;</c> and
        /// Cosmos takes the array first. The optional third argument of <c>ARRAY_CONTAINS</c>, which asks
        /// for a partial match of an object, is not emitted — <c>MEMBER OF</c> means full equality.
        /// </remarks>
        void WriteMemberOf(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 2);

            builder.Append("ARRAY_CONTAINS(");
            Write(builder, Operand(call, 1));
            builder.Append(", ");
            Write(builder, Operand(call, 0));
            builder.Append(')');
        }

        /// <summary>
        /// Translates a scoring function for an <c>ORDER BY RANK</c> clause.
        /// </summary>
        /// <remarks>
        /// The one context in which <c>FULLTEXTSCORE</c> and <c>RRF</c> are legal. <see cref="Translate"/>
        /// refuses them, because everywhere it is used — a predicate, a projection, an ordinary sort key
        /// — is a place the service rejects them, and a refusal costs a pushdown where a rendered
        /// statement would cost the query.
        /// </remarks>
        /// <param name="node">The scoring function.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="CosmosTranslationException">The expression is not a scoring function, or has no Cosmos equivalent.</exception>
        public string TranslateRank(RexNode node)
        {
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            if (IsScoringFunction(node) == false)
                throw new CosmosTranslationException("An ORDER BY RANK clause takes a scoring function.");

            _scoring = true;

            try
            {
                var builder = new StringBuilder();
                Write(builder, node);
                return builder.ToString();
            }
            finally
            {
                _scoring = false;
            }
        }

        /// <summary>
        /// Determines whether an expression is one of the functions <c>ORDER BY RANK</c> ranks by.
        /// </summary>
        /// <param name="node">The expression to test.</param>
        /// <returns><c>true</c> if it is a scoring function.</returns>
        public static bool IsScoringFunction(RexNode? node)
        {
            return node is RexCall call && call.getOperator().getName() switch
            {
                "FULLTEXTSCORE" or "RRF" or "VECTORDISTANCE" => true,
                "CLR_FT_SCORE" or "CLR_FT_RRF" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Determines whether an expression is a full text function — a predicate or the score — whose
        /// first argument is the path searched.
        /// </summary>
        /// <remarks>
        /// By name, for the reason everything here dispatches by name. What the container declares
        /// about that path is what the call costs, and <see cref="Rel.CosmosFilter"/> is where it is
        /// priced.
        /// </remarks>
        /// <param name="node">The expression to test.</param>
        /// <returns><c>true</c> if it is a full text function.</returns>
        public static bool IsFullTextFunction(RexNode? node)
        {
            return node is RexCall call && call.getOperator().getName() switch
            {
                "FULLTEXTCONTAINS" or "FULLTEXTCONTAINSALL" or "FULLTEXTCONTAINSANY" or "FULLTEXTSCORE" => true,
                "CLR_FT_CONTAINS" or "CLR_FT_CONTAINS_ALL" or "CLR_FT_CONTAINS_ANY" or "CLR_FT_SCORE" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Writes <c>RRF</c>, whose arguments are themselves scoring functions.
        /// </summary>
        /// <remarks>
        /// Two or more of them, optionally followed by an array of weights. Only the scoring functions
        /// are checked for here — a trailing weights array is an ordinary expression and renders as one —
        /// so an argument that is neither is refused by the recursive call rather than by a count.
        /// </remarks>
        /// <summary>
        /// Writes <c>CLR_FT_FUZZY</c> as the object form the service takes for a fuzzy term.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Cosmos spells a fuzzy term as an object where a keyword goes</b> —
        /// <c>{"term": "bycycle", "distance": 2}</c> — which is documented by the error text of
        /// <c>SC2241</c>, the refusal a keyword <em>array</em> earns. Bound as a parameter like every
        /// other keyword, so the statement text stays independent of what is searched for.
        /// </para>
        /// <para>
        /// Both operands must be literals. The distance is a count the service reads while planning the
        /// match, and a computed one is refused rather than bound: there is nothing to compute it from
        /// at the point the term is written.
        /// </para>
        /// </remarks>
        void WriteFuzzyTerm(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 2);

            if (Operand(call, 0) is not RexLiteral term || GetLiteralValue(term) is not string text)
                throw new CosmosTranslationException("The text of CLR_FT_FUZZY must be a literal: the service reads the term while planning the match.");

            // Read as whatever exact shape the literal arrived in. A SQL `2` reaches here as a DECIMAL
            // where one built with makeLiteral over an INTEGER type reaches here as a long, and the
            // distance is the same count either way.
            if (Operand(call, 1) is not RexLiteral edits)
                throw new CosmosTranslationException("The edit distance of CLR_FT_FUZZY must be an integer literal.");

            var distance = GetLiteralValue(edits) switch
            {
                long value => value,
                decimal value when decimal.Truncate(value) == value => (long)value,
                var other => throw new CosmosTranslationException($"The edit distance of CLR_FT_FUZZY must be a whole number, and '{other}' is not one."),
            };

            builder.Append(_parameters.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["term"] = text,
                ["distance"] = distance,
            }));
        }

        void WriteRrf(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() < 2)
                throw new CosmosTranslationException("RRF fuses two or more scoring functions.");

            builder.Append("RRF(");

            for (var i = 0; i < operands.size(); i++)
            {
                if (i > 0)
                    builder.Append(", ");

                Write(builder, Operand(call, i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes a full text predicate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>FULLTEXTCONTAINS(&lt;property_path&gt;, &lt;string_expr&gt;)</c> and the <c>ALL</c> and
        /// <c>ANY</c> forms, which take one or more keywords. The rendered form is the SQL form: the
        /// arguments are already in the service's order.
        /// </para>
        /// <para>
        /// The first argument is a <em>property path</em> and not an expression, so a call over anything
        /// that does not resolve to one is declined rather than rendered — the service would reject it.
        /// The keywords are ordinary expressions and bind as parameters like any other literal.
        /// </para>
        /// <para>
        /// <b>What the container declares about the path is not asked here.</b> It was, as a refusal:
        /// a predicate over a path with neither a full text policy nor a full text index had been
        /// measured as a bodyless 400, so the call was declined and the refusal named the path. That
        /// measurement does not reproduce. Measured again against three accounts and four containers
        /// (#85), the service answers the predicate and the score over an undeclared path, over a
        /// container with no policy at all, and on an account without the full text capability — so
        /// the declaration decides what the call <em>costs</em>, an index seek or a scan, and
        /// <see cref="Rel.CosmosFilter"/> and <see cref="Rel.CosmosRank"/> read it for that. Refusing
        /// a legal plan has the worse failure of the two: the query does not get slower, it fails,
        /// because the declined call is left above the scan with no in-process body.
        /// </para>
        /// </remarks>
        void WriteFullTextPredicate(StringBuilder builder, RexCall call, string name)
        {
            var operands = call.getOperands();
            if (operands.size() < 2)
                throw new CosmosTranslationException($"'{name}' expects a property path and at least one keyword.");

            if (TryResolvePath(Operand(call, 0), out var path) == false || path is null)
                throw new CosmosTranslationException($"The first argument of '{name}' must be a document path.");

            builder.Append(name).Append('(');
            path.WriteTo(builder);

            for (var i = 1; i < operands.size(); i++)
            {
                builder.Append(", ");
                Write(builder, Operand(call, i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>VECTORDISTANCE</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>VECTORDISTANCE(&lt;vector1&gt;, &lt;vector2&gt;, [&lt;brute_force&gt;], [&lt;options&gt;])</c>,
        /// rendered over all of its operands. Either vector may be a literal — searching for a
        /// neighbour of a supplied embedding is the whole point of the function — so unlike a full text
        /// predicate this does not require its first argument to be a path.
        /// </para>
        /// <para>
        /// What it does require is that <em>one</em> of the two is a path the container declares
        /// searchable. Comparing two arbitrary arrays is legal and pointless; comparing a document path
        /// the container has said nothing about is the case the gate exists for.
        /// </para>
        /// </remarks>
        void WriteVectorDistance(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() < 2 || operands.size() > 4)
                throw new CosmosTranslationException($"Function 'VECTORDISTANCE' with {operands.size()} argument(s) has no Cosmos equivalent.");

            if (_container is not null &&
                DeclaresVector(Operand(call, 0)) == false &&
                DeclaresVector(Operand(call, 1)) == false)
                throw new CosmosTranslationException("'VECTORDISTANCE' requires a vector path the container declares; " + Declared(_container.VectorPaths) + ".");

            WriteFunctionCall(builder, call, "VECTORDISTANCE");
        }

        /// <summary>
        /// Determines whether an expression is a path the container declares vector searchable.
        /// </summary>
        bool DeclaresVector(RexNode node)
        {
            return TryResolvePath(node, out var path) && path is not null && _container!.IsPathVectorSearchable(path.ToPolicyPath());
        }

        /// <summary>
        /// Refuses a geodesic call over a container that reads its coordinates as a plane.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Cosmos spelling of every one of these is the unprefixed one, so what a rendered
        /// <c>ST_DISTANCE</c> means at the service is decided by the container's <c>geospatialConfig</c>
        /// and not by the name in the query. Over a container reading <c>Geometry</c> the service would
        /// answer the planar question, in the units of the coordinate system rather than in metres, and
        /// nothing in the response would say so.
        /// </para>
        /// <para>
        /// This is unlike the full text gate above, which exists because the service returns an error.
        /// Here the service returns an answer. That is the worse failure, and it is why this refuses
        /// while planning rather than leaving it to be noticed.
        /// </para>
        /// </remarks>
        /// <param name="name">The function being written, for the message.</param>
        /// <exception cref="CosmosTranslationException">The container reads its coordinates as a plane.</exception>
        void RequireGeographyReading(string name)
        {
            if (_container is null || _container.ReadsGeography)
                return;

            throw new CosmosTranslationException(
                $"'{name}' is geodesic and the container reads its coordinates as a plane; its geospatialConfig says Geometry.");
        }

        /// <summary>
        /// Renders what a container declares, for a refusal that names it.
        /// </summary>
        static string Declared(IReadOnlyList<string> paths)
        {
            return paths.Count == 0
                ? "the container declares none"
                : "the container declares " + string.Join(", ", paths);
        }

        /// <summary>
        /// Writes <c>TRIM</c>, whose specification decides which of Cosmos's three functions applies.
        /// </summary>
        /// <remarks>
        /// Calcite carries the operands as specification, characters, string — the specification being a
        /// symbol literal of <c>BOTH</c>, <c>LEADING</c> or <c>TRAILING</c>, which pick <c>TRIM</c>,
        /// <c>LTRIM</c> and <c>RTRIM</c>.
        /// <para>
        /// Only trimming spaces is translated. Cosmos's two-argument forms have not been verified, and
        /// emitting the one-argument form for <c>TRIM(LEADING 'x' FROM s)</c> would silently trim
        /// something else.
        /// </para>
        /// </remarks>
        void WriteTrim(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 3);

            if (Operand(call, 0) is not RexLiteral specification || specification.getValue() is not SqlTrimFunction.Flag flag)
                throw new CosmosTranslationException("TRIM without a constant specification has no Cosmos equivalent.");

            if (Operand(call, 1) is not RexLiteral characters || GetLiteralValue(characters) as string != " ")
                throw new CosmosTranslationException("TRIM of any character other than a space has no Cosmos equivalent.");

            // Dispatched on the name: a Java enum's ordinals are not stable across versions and its
            // names are.
            var name = flag.name() switch
            {
                "BOTH" => "TRIM",
                "LEADING" => "LTRIM",
                "TRAILING" => "RTRIM",
                var other => throw new CosmosTranslationException($"TRIM specification '{other}' has no Cosmos equivalent."),
            };

            builder.Append(name).Append('(');
            Write(builder, Operand(call, 2));
            builder.Append(')');
        }

        /// <summary>
        /// Projects a call's <see cref="SqlKind"/> onto the CLR enumeration IKVM generates for it,
        /// so that dispatch is compile-time checked rather than string-matched.
        /// </summary>
        static SqlKind.__Enum KindOf(RexCall call) => (SqlKind.__Enum)call.getKind().ordinal();

        /// <summary>
        /// Returns an operand as a <see cref="RexNode"/>. Calcite's operand lists are raw Java
        /// collections, which surface as <see cref="object"/>.
        /// </summary>
        static RexNode Operand(RexCall call, int index) => (RexNode)call.getOperands().get(index);

        static void RequireOperandCount(RexCall call, int count)
        {
            if (call.getOperands().size() != count)
                throw new CosmosTranslationException($"Operator '{call.getOperator().getName()}' expects {count} operand(s), found {call.getOperands().size()}.");
        }

    }

}
