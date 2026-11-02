using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosRexLiterals;
using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosRexShapes;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Reads a Rex expression as the document path it addresses, against one binding: the path each input field is
    /// bound to, how each is read back, and the correlation variable that stands for the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The translator's path readers, moved so that a caller that only resolves paths — the predicate loader, the fact
    /// rewrite, the joins, the unnest, the implementor's binding walk — builds a binding and no translator. Every
    /// pushdown resolves its path here, so a spelling refused here is refused in every clause. See DESIGN.md, <i>The
    /// document column</i> and <i>The Rex readers, apart from the translator</i>.
    /// </para>
    /// <para>
    /// It reads shapes and nothing about the documents: no facts, no parameters, and of the container only whether its
    /// target stores a member's name escaped (<see cref="NamesAnEscape"/>).
    /// </para>
    /// </remarks>
    internal sealed class CosmosRexPaths
    {

        readonly IReadOnlyList<CosmosPath?> _fields;

        /// <summary>
        /// How each input field is read back, parallel to <see cref="_fields"/> and empty where the caller did not say.
        /// What it adds to the path is whether a field is a rendering — see <see cref="IsTextRendering"/>.
        /// </summary>
        readonly IReadOnlyList<CosmosReading> _readings;

        /// <summary>
        /// The correlation variable whose row is the one <see cref="_fields"/> describes, where the caller has one.
        /// </summary>
        readonly org.apache.calcite.rel.core.CorrelationId? _ownRow;

        /// <summary>
        /// Whether the binding's container stores a member's name holding U+0000 or U+FDD0 escaped, the emulator's
        /// (<see cref="Metadata.CosmosRendering.StoresSpelled(Metadata.CosmosServiceTarget)"/>): a path naming one is then
        /// no path. DESIGN.md, <i>What the emulator is sent is what it stores</i>.
        /// </summary>
        readonly bool _spelled;

        /// <summary>
        /// Initializes a new instance over a binding, with whether its container stores a member's name escaped, which every
        /// caller holding a container passes. A field <paramref name="readings"/> does not cover is read as its declared type.
        /// </summary>
        public CosmosRexPaths(IReadOnlyList<CosmosPath?> fields, bool spelled, IReadOnlyList<CosmosReading>? readings = null, org.apache.calcite.rel.core.CorrelationId? ownRow = null)
        {
            _fields = fields ?? throw new ArgumentNullException(nameof(fields));
            _readings = readings ?? Array.Empty<CosmosReading>();
            _ownRow = ownRow;
            _spelled = spelled;
        }

        /// <summary>
        /// Determines whether an expression denotes a document path, and if so what that path is.
        /// </summary>
        /// <remarks>
        /// A field reference, a field of this row's own correlation variable, a chain of constant <c>ITEM</c> accessors over
        /// one, a plain SQL/JSON accessor, or <c>StringToArray</c> over one. Every clause that needs a path asks this, so a
        /// spelling refused here is refused everywhere. See DESIGN.md, <i>The document column</i>.
        /// </remarks>
        /// <param name="node">The expression to inspect.</param>
        /// <param name="path">On success, the resolved path.</param>
        /// <returns><c>true</c> if the expression denotes a path; otherwise <c>false</c>.</returns>
        internal bool TryResolvePath(RexNode node, out CosmosPath? path)
        {
            // Under the emulator a member's name holding U+0000 or U+FDD0 is stored escaped, and a path naming one written
            // as spelled reads nothing: it is no path there, and what reads it is computed in process over the document.
            if (Resolve(node, out path) && (_spelled == false || NamesAnEscape(path) == false))
                return true;

            path = null;
            return false;
        }

        /// <summary>Whether a path names a member holding U+0000 or U+FDD0, which the emulator stores escaped.</summary>
        internal static bool NamesAnEscape(CosmosPath? path)
        {
            if (path is not null)
                foreach (var segment in path.Segments)
                    if (segment.IsIndex == false && EmulatorStoredText.Escapes(segment.Name!))
                        return true;

            return false;
        }

        bool Resolve(RexNode node, out CosmosPath? path)
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
        /// Determines whether a correlation variable stands for this input's own row.
        /// </summary>
        /// <remarks>
        /// A lateral traversal correlates an input on itself and a join on the other side, in the same shape of expression;
        /// resolved against these bindings the second would name a plausible, wrong document path. Where the caller has not
        /// said which variable is its own, none is. See DESIGN.md, <i>The document column</i>.
        /// </remarks>
        bool IsOwnRow(RexCorrelVariable variable)
        {
            return _ownRow is not null && _ownRow.equals(variable.id);
        }

        /// <summary>
        /// Resolves a SQL/JSON accessor to the document path it addresses.
        /// </summary>
        /// <remarks>
        /// The path argument must be a literal in the plain subset <see cref="TryExtendByJsonPath"/> accepts, and the document
        /// it reads through must be a document: a scalar rendering is text Calcite parses, where the service has the value.
        /// The trailing <c>RETURNING</c> and behaviour flags are <see cref="CosmosRexShapes.IsJsonAccessor"/>'s to refuse
        /// before this is asked. See DESIGN.md, <i>The document column</i> and <i>What an accessor reads, stated once</i>.
        /// </remarks>
        internal bool TryResolveJsonPath(RexCall call, out CosmosPath? path)
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

            return value is string text && TryExtendByJsonPath(basePath, text, out path) && (_spelled == false || NamesAnEscape(path) == false);
        }

        /// <summary>
        /// Extends a path by a SQL/JSON path expression, where it is one Cosmos can address.
        /// </summary>
        /// <remarks>
        /// The accepted grammar is <c>$</c> followed by any number of <c>.name</c>, <c>['name']</c> and <c>[0]</c> steps. That
        /// is the whole of what a document path is; everything else is refused.
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

            // The steps are gathered and appended once: appending each as it is read copies the path at every
            // step, which is quadratic in its length. The list is made only at a second step, so a path of one
            // step allocates no more than it did when each step was appended.
            var first = default(CosmosPathSegment);
            var steps = default(List<CosmosPathSegment>);
            var count = 0;
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

                    Add(CosmosPathSegment.Property(text[start..i]));
                    continue;
                }

                if (text[i] != '[')
                    return false;

                var close = text.IndexOf(']', ++i);
                if (close < 0)
                    return false;

                var step = text.AsSpan(i, close - i);
                i = close + 1;

                if (step.Length >= 2 && (step[0] == '\'' && step[^1] == '\'' || step[0] == '"' && step[^1] == '"'))
                {
                    var name = step[1..^1];
                    if (name.Length == 0 || name.IndexOfAny('\'', '"') >= 0)
                        return false;

                    Add(CosmosPathSegment.Property(name.ToString()));
                    continue;
                }

                if (int.TryParse(step, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) == false)
                    return false;

                Add(CosmosPathSegment.Index(index));
            }

            path = count switch
            {
                0 => basePath,
                1 => basePath.Append(first),
                _ => basePath.Append(steps!),
            };
            return true;

            void Add(CosmosPathSegment segment)
            {
                if (count++ == 0)
                    first = segment;
                else
                    (steps ??= new List<CosmosPathSegment> { first }).Add(segment);
            }
        }

        /// <summary>
        /// Determines whether an expression is the rendering of a document value as text: a <c>JSON_VALUE</c> read as text,
        /// a plain <c>JSON_QUERY</c>, or a field bound to one.
        /// </summary>
        /// <remarks>
        /// A field bound through a projection to an accessor read as text carries the rendering, not the value, and is held
        /// to every test the accessor is (#83). Of a character type only: the raw value at the same path is typed <c>ANY</c>.
        /// See DESIGN.md, <i>Casts over document values</i>.
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
        /// Determines whether an expression renders a scalar: a <c>JSON_VALUE</c> read as text, or a field bound to one.
        /// </summary>
        /// <remarks>
        /// The renderings a fact about the stored value can make exact. A plain <c>JSON_QUERY</c> renders the fragment and is
        /// null for every scalar, so it is not one. See DESIGN.md, <i>Casts over document values</i>.
        /// </remarks>
        internal bool IsScalarRendering(RexNode node)
        {
            return IsTextJsonValue(node)
                || node is RexInputRef reference
                    && IsCharacter(reference)
                    && reference.getIndex() >= 0
                    && reference.getIndex() < _readings.Count
                    && _readings[reference.getIndex()] is CosmosReading.Text;
        }

        /// <summary>
        /// Determines whether a call is the cast that names the raw value at a path over its rendering.
        /// </summary>
        /// <remarks>
        /// The cast to <c>ANY</c> over a rendering, which the translator writes as the path, asked as a question so that what
        /// reads a path through one knows it is reading the value the service holds. See DESIGN.md, <i>Casts over document
        /// values</i>.
        /// </remarks>
        internal bool IsRawValueMarker(RexCall call)
        {
            var kind = KindOf(call);

            return (kind == SqlKind.__Enum.CAST || kind == SqlKind.__Enum.SAFE_CAST)
                && call.getOperands().size() == 1
                && call.getType()?.getSqlTypeName() == SqlTypeName.ANY
                && IsTextRendering(Operand(call, 0));
        }

        /// <summary>
        /// Returns the document path a statement's path reads, where it reads the container's document.
        /// </summary>
        /// <remarks>
        /// Rooted at the container only: a traversal alias addresses an element, and what is known is of the document.
        /// </remarks>
        /// <param name="path">The statement's path.</param>
        /// <returns>The document path, or <c>null</c>.</returns>
        internal static JsonDocumentPath? DocumentOf(CosmosPath? path) =>
            Metadata.CosmosDocumentPaths.From(path, CosmosImplementor.DefaultRootAlias);

        /// <summary>
        /// Resolves the document path a geography was read from, seeing through the constructor.
        /// </summary>
        /// <remarks>
        /// A stored geography is written as <c>CLR_ST_GEOG_GEOMFROMGEOJSON</c> over the text a document path yields, so the
        /// path is one level down. See DESIGN.md, <i>Spatial is reachable, and nothing but the operator's name says which
        /// reading is meant</i>.
        /// </remarks>
        internal bool TryResolveGeography(RexNode node, out CosmosPath? path)
        {
            if (node is RexCall call
                && call.getOperator().getName() == Geography.Sql.GeographyOperatorTable.ClrStGeogGeomFromGeoJson.getName()
                && call.getOperands().size() == 1)
                node = Operand(call, 0);

            return TryResolvePath(node, out path) && path is not null;
        }

        /// <summary>
        /// Determines whether an expression is a geography bound as a parameter: <c>?</c> typed <c>GEOMETRY</c>, bare or
        /// under the cast that typed it.
        /// </summary>
        /// <remarks>
        /// <c>CAST(? AS GEOMETRY)</c> converts nothing (#187), and what arrives in the slot is a geometry or nothing, measured
        /// by <c>CalciteGeometryParameterMeasurementTests</c>. Only <c>GEOMETRY</c> to <c>GEOMETRY</c>. See DESIGN.md,
        /// <i>Spatial is reachable, and nothing but the operator's name says which reading is meant</i>.
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

    }

}
