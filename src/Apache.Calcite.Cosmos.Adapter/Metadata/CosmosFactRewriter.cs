using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Rewrites a predicate into the one the service can evaluate, where the container's declared
    /// facts say the two select the same documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cosmos has strings where Calcite has <c>UUID</c>.</b> A comparison against a UUID therefore
    /// has no form the service can evaluate, and the container is read whole. What closes the gap is a
    /// proof about the <em>stored</em> form: if every value at a path is the canonical lowercase
    /// spelling of its UUID, then comparing the stored strings answers exactly what comparing the
    /// values answers, and the comparison lowers to a plain string equality.
    /// </para>
    /// <para>
    /// <b>One rewrite is the whole of it.</b> Once the condition carries a string equality, everything
    /// downstream is machinery that already exists — the translator renders it, the service's index
    /// serves it, <see cref="CosmosPartitionKeyExtractor"/> recovers a partition key from it, and a
    /// point read follows where the predicate says nothing else.
    /// </para>
    /// <para>
    /// <b>The same argument reaches instants, and reaches further.</b> Cosmos has no date type either,
    /// so a path read as a <c>TIMESTAMP</c> holds a string and a comparison against one has no form
    /// the service can evaluate. Where the declared shape is one fixed ISO-8601 UTC spelling, the
    /// lexical order of the stored strings <em>is</em> the chronological order, so a range lowers as
    /// well as an equality — see <see cref="TryLowerInstant"/>.
    /// </para>
    /// <para>
    /// <b>Which of the two a form licenses is not a matter of degree.</b> An instant written at mixed
    /// precision has one spelling per value while <c>'…:56.5Z'</c> sorts before <c>'…:56Z'</c>, and an
    /// unpadded integer has one spelling while <c>'9'</c> sorts after <c>'42'</c> — see
    /// <see cref="CosmosStoredForms"/>. A form may therefore preserve equality and not order, and
    /// lowering a range on one would return the wrong rows. So the two comparisons are gated on the
    /// two properties separately, on exactly what <see cref="CosmosRepresentation"/> carries.
    /// </para>
    /// <para>
    /// <b>A UUID used to reach the equality alone, and the reason was the engine's rather than the
    /// spelling's.</b> Calcite compared UUIDs as two <em>signed</em> 64-bit halves, so the lexical
    /// order of the canonical string was not its order unless the schema also confined the first hex
    /// digit. CALCITE-7716 made the comparison unsigned in 1.43 and
    /// <see cref="CosmosUuidForms.CanonicalLower"/> preserves order with it, so
    /// <see cref="TryLowerUuid"/> takes the operator and the reversal exactly as the other two do.
    /// The gate did not move: it is still the two bits, asked separately.
    /// </para>
    /// <para>
    /// <b>Why a guard needs no extra check here.</b> A fact may be conditional — a path holds a UUID
    /// only when a discriminator property has a particular value — and the condition that proved it is
    /// a conjunct of the very predicate being rewritten. It stays there. So over a document the fact
    /// was never claimed for, the conjunct that proved the guard has already excluded the row, and the
    /// rewritten comparison decides nothing. That is the sibling-conjunct case; a sort or a point read
    /// needs the stronger statement-wide argument, and neither is rewritten here.
    /// </para>
    /// <para>
    /// <b>What is true is the oracle's; what is written is this.</b> Every fact a rewrite reads is
    /// asked of <see cref="CosmosFilterStatement"/> under the licence the rewrite has: the filter is
    /// <c>FALSE</c> where every conjunct together contradicts the declaration, a strengthening; a
    /// conjunct is deleted where its siblings imply it, one at a time; a comparison is lowered on what
    /// its siblings establish, never what it does; and inside the arm of a <c>CASE</c>, on that and the
    /// conditions the arm is read under. What is left here is the shapes, the stored spellings and the
    /// licence a form gives an operator.
    /// </para>
    /// </remarks>
    public static class CosmosFactRewriter
    {

        /// <summary>
        /// Each statement's rewrite, so that every rule asking of one condition gets one node back, and the statement read
        /// off that node is remembered too; released with the statement.
        /// </summary>
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CosmosFilterStatement, RexNode> Rewrites = new();


        /// <summary>
        /// Returns the predicate with every comparison the declared facts license lowered, or the
        /// predicate unchanged.
        /// </summary>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="container">The container, carrying whatever the model declared.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="rexBuilder">Builds the replacement nodes.</param>
        /// <param name="readings">How each field is read back, where the caller knows: a field read as text compares
        /// its rendering, and establishes what a rendering does.</param>
        /// <returns>The rewritten predicate, or <paramref name="condition"/> where nothing applied.</returns>
        public static RexNode Rewrite(RexNode? condition, IReadOnlyList<CosmosPath?>? fields, CosmosContainerMetadata? container, string rootAlias, RexBuilder rexBuilder, IReadOnlyList<CosmosReading>? readings = null)
        {
            if (condition is null || fields is null || rexBuilder is null)
                return condition!;

            // A CASE is the one shape rewritten without a fact -- see TryFlattenCase -- and where
            // there is no container it is all there is to look for.
            if (container is null)
                return ContainsCase(condition) ? Rewritten(condition, null, fields, rootAlias, rexBuilder) : condition;

            var statement = CosmosFilterStatement.Of(condition, fields, readings, container, rexBuilder, rootAlias);
            return Rewrites.GetValue(statement, s => Rewritten(condition, s, fields, rootAlias, rexBuilder));
        }

        /// <summary>
        /// Rewrites a condition conjunct by conjunct, each under the licence its rewrite has.
        /// </summary>
        static RexNode Rewritten(RexNode condition, CosmosFilterStatement? statement, IReadOnlyList<CosmosPath?> fields, string rootAlias, RexBuilder rexBuilder)
        {
            // Where the conjuncts and the container's declaration cannot all hold, no document
            // satisfies the predicate -- so the equivalent predicate is the constant, and every rule
            // that reduces one takes it from there. A strengthening, which every conjunct's own facts
            // may justify.
            if (statement is { IsContradictory: true })
                return rexBuilder.makeLiteral(false);

            var conjuncts = statement?.Conjuncts ?? CosmosFilterStatement.ConjunctsOf(condition);

            // A conjunct the rest of the filter implies decides nothing, so the equivalent predicate is
            // the one without it -- asked of its siblings and never of itself, and one at a time, so
            // that two conjuncts implying each other do not both go.
            var redundant = statement?.Redundant ?? Array.Empty<int>();
            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList());

            var operands = new java.util.ArrayList();
            var changed = redundant.Count > 0;

            for (var i = 0; i < conjuncts.Count; i++)
            {
                if (redundant.Contains(i))
                    continue;

                // An IN arrives folded into a SEARCH over a Sarg, which is one node rather than the
                // equalities it stands for, so nothing below would recognise it. Expanded, it is the
                // disjunction translation would have rendered anyway -- and the expansion is kept only
                // if something was actually lowered, so a conjunct this has no opinion about reaches
                // the planner in the shape it arrived.
                var rewritten = Apply(Expand(conjuncts[i], rexBuilder), new Rewriting(statement, i, Array.Empty<RexNode>(), translator, rexBuilder, fields, rootAlias));

                operands.add(rewritten ?? conjuncts[i]);
                changed |= rewritten is not null;
            }

            return changed ? RexUtil.composeConjunction(rexBuilder, operands) : condition;
        }

        static RexNode Expand(RexNode node, RexBuilder rexBuilder)
        {
            try
            {
                return RexUtil.expandSearch(rexBuilder, null, node);
            }
            catch (Exception)
            {
                return node;
            }
        }

        /// <summary>
        /// Where a rewrite stands: the conjunct it is inside, and the conditions of the <c>CASE</c> arms
        /// it has descended into, which together say what it may lean on.
        /// </summary>
        /// <remarks>
        /// The facts are asked the first time a lowering's shape is found, and not before: most
        /// conjuncts lower nothing.
        /// </remarks>
        sealed class Rewriting
        {

            readonly CosmosFilterStatement? _statement;
            readonly int _conjunct;
            readonly IReadOnlyList<RexNode> _conditions;
            JsonFactSet? _known;

            public Rewriting(CosmosFilterStatement? statement, int conjunct, IReadOnlyList<RexNode> conditions, CosmosRexTranslator translator, RexBuilder rexBuilder, IReadOnlyList<CosmosPath?> fields, string rootAlias)
            {
                _statement = statement;
                _conjunct = conjunct;
                _conditions = conditions;
                Translator = translator;
                RexBuilder = rexBuilder;
                Fields = fields;
                RootAlias = rootAlias;
            }

            public CosmosRexTranslator Translator { get; }

            public RexBuilder RexBuilder { get; }

            public IReadOnlyList<CosmosPath?> Fields { get; }

            public string RootAlias { get; }

            /// <summary>
            /// Gets what the rewrite may lean on: every sibling of its conjunct, and the conditions of
            /// the arms it is inside.
            /// </summary>
            public JsonFactSet Known => _known ??= _statement is null ? JsonFactSet.Empty
                : _conditions.Count == 0 ? _statement.Siblings(_conjunct)
                : _statement.Within(_conjunct, _conditions);

            /// <summary>
            /// Gets whether the arm the rewrite is inside is never read: its conditions contradict
            /// what its siblings establish.
            /// </summary>
            public bool NeverRead => _statement is not null && _conditions.Count > 0 && _statement.NeverWithin(_conjunct, _conditions);

            /// <summary>
            /// Returns where a rewrite stands inside the arm a condition guards.
            /// </summary>
            public Rewriting Within(RexNode condition) =>
                new(_statement, _conjunct, _conditions.Append(condition).ToArray(), Translator, RexBuilder, Fields, RootAlias);

        }

        /// <summary>
        /// Returns a projected expression with the condition of every <c>CASE</c> in it rewritten as
        /// <see cref="Rewrite"/> rewrites a predicate, or the expression unchanged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A condition is a predicate wherever it is written.</b> A <c>CASE</c> takes an arm where
        /// its condition is true and passes over it where the condition is false or unknown — the
        /// distinction a filter draws, and the only one <see cref="Rewrite"/> preserves. So a rewrite
        /// that keeps the rows a filter keeps keeps the arm a <c>CASE</c> takes.
        /// </para>
        /// <para>
        /// <b>What needs it is a merged join's column.</b> A left join read as one read gives each
        /// column of the other side as <c>CASE WHEN M THEN q END</c>, and <c>M</c> carries the key test
        /// <c>CAST(k AS UUID) IS NOT NULL</c>, which the service cannot evaluate until it is lowered onto
        /// the text. Unlowered, the whole projection stayed in process — and with it a sort or a page
        /// above it (#192).
        /// </para>
        /// </remarks>
        /// <param name="expression">The projected expression, over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the projected input.</param>
        /// <param name="container">The container, carrying whatever the model declared.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="rexBuilder">Builds the replacement nodes.</param>
        /// <returns>The rewritten expression, or <paramref name="expression"/> where nothing applied.</returns>
        public static RexNode RewriteConditions(RexNode expression, IReadOnlyList<CosmosPath?>? fields, CosmosContainerMetadata? container, string rootAlias, RexBuilder rexBuilder)
        {
            if (expression is null || fields is null || rexBuilder is null || ContainsCase(expression) == false)
                return expression!;

            return (RexNode)expression.accept(new Conditions(fields, container, rootAlias, rexBuilder));
        }

        /// <summary>
        /// Rewrites the conditions of every <c>CASE</c> beneath a node, innermost first.
        /// </summary>
        sealed class Conditions : RexShuttle
        {

            readonly IReadOnlyList<CosmosPath?> _fields;
            readonly CosmosContainerMetadata? _container;
            readonly string _rootAlias;
            readonly RexBuilder _rexBuilder;

            public Conditions(IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata? container, string rootAlias, RexBuilder rexBuilder)
            {
                _fields = fields;
                _container = container;
                _rootAlias = rootAlias;
                _rexBuilder = rexBuilder;
            }

            public override RexNode visitCall(RexCall call)
            {
                var visited = base.visitCall(call);
                if (visited is not RexCall @case || @case.getKind() != SqlKind.CASE)
                    return visited;

                var operands = new java.util.ArrayList();
                var changed = false;

                // Conditions sit at the even positions, every one but the last operand, which is the
                // ELSE.
                for (var i = 0; i < @case.getOperands().size(); i++)
                {
                    var operand = (RexNode)@case.getOperands().get(i);

                    if (i % 2 == 0 && i < @case.getOperands().size() - 1)
                    {
                        var rewritten = Rewrite(operand, _fields, _container, _rootAlias, _rexBuilder);
                        changed |= ReferenceEquals(rewritten, operand) == false;
                        operand = rewritten;
                    }

                    operands.add(operand);
                }

                // Typed as the CASE was: a rewritten condition decides the same arms, and the value is
                // still one of them.
                return changed ? _rexBuilder.makeCall(@case.getType(), SqlStdOperatorTable.CASE, operands) : @case;
            }

        }

        /// <summary>
        /// Rewrites what it can, returning <c>null</c> where nothing below changed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Conjunctions and disjunctions alike, and the reason it reaches into a disjunction is worth
        /// stating. A rewrite is an equivalence only under the declared fact, and a conditional fact
        /// was proven from the <em>top-level</em> conjuncts — which hold of every row the whole
        /// predicate keeps, whatever shape the rest of it has. So inside <c>A AND (B OR C)</c> the
        /// proof of <c>A</c> is as good in <c>B</c> as it is beside it: over a row where <c>A</c>
        /// fails the conjunction is false however the branch reads, and over one where it holds the
        /// fact holds too.
        /// </para>
        /// <para>
        /// Which is what makes an <c>IN</c> work. Expanded, it is a disjunction of equalities over one
        /// path, each lowered the way a lone equality is — and once the points are stored spellings
        /// rather than casts, <c>CosmosPartitionKeyExtractor</c> can recover them as a set and the
        /// batch point read becomes reachable through a typed column.
        /// </para>
        /// </remarks>
        static RexNode? Apply(RexNode node, Rewriting at)
        {
            if (node is not RexCall call)
                return null;

            // By name rather than by ordinal: a kind's position among SqlKind's 355 values is
            // not an API, and a cast from the ordinal keeps compiling when one is inserted.
            var kind = call.getKind().name();

            if (kind == nameof(SqlKind.__Enum.AND) || kind == nameof(SqlKind.__Enum.OR))
            {
                var operands = new java.util.ArrayList();
                var changed = false;

                for (var i = 0; i < call.getOperands().size(); i++)
                {
                    var operand = (RexNode)call.getOperands().get(i);
                    var rewritten = Apply(operand, at);

                    operands.add(rewritten ?? operand);
                    changed |= rewritten is not null;
                }

                if (changed == false)
                    return null;

                return kind == nameof(SqlKind.__Enum.AND)
                    ? RexUtil.composeConjunction(at.RexBuilder, operands)
                    : RexUtil.composeDisjunction(at.RexBuilder, operands);
            }

            if (kind == nameof(SqlKind.__Enum.IS_NULL) || kind == nameof(SqlKind.__Enum.IS_NOT_NULL))
                return TryLowerUuidNullTest(call, at);

            if (kind == nameof(SqlKind.__Enum.CASE))
                return TryFlattenCase(call, at);

            // A merged view's column inside a comparison, rather than the comparison itself -- see
            // TryLiftStrictCase. Before the lowering below, which reads a comparison's operands as
            // written and would find a CASE where it looks for a path.
            if (TryLiftStrictCase(call, at) is RexNode lifted)
                return lifted;

            if (call.getOperands().size() != 2)
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            // Reading the operand on the right means reading the comparison backwards, so the
            // operator is reversed with it: `<literal> > <path>` is `<path> < <literal>`, and keeping
            // the operator as written would select the complement. `=` and `<>` are their own
            // reverse, so the equalities are unaffected by passing through the same machinery.
            if (ComparisonOf(kind) is not SqlOperator comparison)
                return null;

            return TryLowerUuid(left, right, comparison, at)
                ?? TryLowerUuid(right, left, Reverse(comparison), at)
                ?? TryLowerInstant(left, right, comparison, at)
                ?? TryLowerInstant(right, left, Reverse(comparison), at)
                ?? TryLowerNumber(left, right, comparison, at)
                ?? TryLowerNumber(right, left, Reverse(comparison), at);
        }

        /// <summary>
        /// Rewrites <c>CASE WHEN p THEN q ELSE FALSE END</c> in a filter as <c>p AND q</c>, lowering
        /// both halves first, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Where it comes from.</b> A self-join merged into one read gives each view's columns as
        /// <c>CASE WHEN &lt;that view's filter&gt; THEN … END</c> — see <c>CosmosSelfJoinRule</c> — and
        /// a comparison through such a column simplifies to a <c>CASE</c> whose arms are a comparison
        /// and <c>FALSE</c>. Through one view a host's simplifier takes that apart itself; through two,
        /// <c>OR(CASE(base, CASE(type = 'park', parkId = X, false), false), …)</c> reached the filter
        /// rules whole (#183). The translator writes a <c>CASE</c> as a ternary, and it declined this
        /// one only because <c>parkId = X</c> compares a <c>UUID</c> the service has no form for —
        /// which nothing here lowered, a <c>CASE</c> not having been walked into.
        /// </para>
        /// <para>
        /// <b>Why the two select the same documents.</b> A filter keeps a row where its condition is
        /// true, and through <c>AND</c> and <c>OR</c> — the only nodes this is reached through — that
        /// depends only on where each part is true: replace a part by one true on the same rows and
        /// the whole is true on the same rows, whatever the two answer elsewhere. The <c>CASE</c> is
        /// true exactly where <c>p</c> is true and <c>q</c> is, its <c>ELSE</c> being false — or null,
        /// which is true nowhere either — and <c>p AND q</c> is true exactly there too. Where <c>p</c>
        /// is null the two differ, the <c>CASE</c> answering false and the conjunction null, and under
        /// a <c>NOT</c> that would matter; nothing here is under one.
        /// </para>
        /// <para>
        /// <b><c>q</c> is read only where <c>p</c> holds, so what <c>p</c> proves is in force there.</b>
        /// A merged view's filter is exactly the discriminator that makes its columns mean what they
        /// do, so <c>q</c> is lowered against the facts <c>p</c> establishes beside the ones the
        /// conjunct's siblings do — the sibling-conjunct argument, one level down: over a row where
        /// <c>p</c> is not true the <c>CASE</c> answers its <c>ELSE</c> however <c>q</c> reads. And where
        /// those facts cannot hold together <c>p</c> is never true, so the <c>CASE</c> is the constant.
        /// That is <see cref="OracleLicence.Arm"/>: the siblings and the arm's conditions, never the
        /// conjunct the <c>CASE</c> is part of.
        /// </para>
        /// <para>
        /// <b>Only where <c>q</c> cannot raise, which is the condition Calcite's own simplifier keeps
        /// and the reason it left this alone.</b> A <c>CASE</c> evaluates <c>q</c> only where <c>p</c>
        /// holds; a conjunction need not. Over a document of another kind the cast to <c>UUID</c> in
        /// <c>parkId = X</c> may raise, and a conjunction rechecked in process could evaluate it there.
        /// So the rewrite waits for the lowering: once <c>q</c> compares stored strings it raises
        /// nowhere, and only then is the <c>CASE</c> taken apart. A <c>q</c> still holding a conversion
        /// keeps its <c>CASE</c>, lowered inside where it could be.
        /// </para>
        /// <para>
        /// <b>And it needs no fact.</b> The equivalence holds of any <c>CASE</c> of this shape, so a
        /// container that declares nothing has one taken apart too — the one thing
        /// <see cref="Rewrite"/> walks a predicate for when nothing is declared. A <c>CASE</c> with
        /// more than one arm, or an <c>ELSE</c> that may be true, is not this shape and is left as the
        /// ternary it already rendered as.
        /// </para>
        /// </remarks>
        static RexNode? TryFlattenCase(RexCall call, Rewriting at)
        {
            if (call.getOperands().size() != 3 || (RexNode)call.getOperands().get(2) is not RexLiteral otherwise || (otherwise.isNull() || otherwise.isAlwaysFalse()) == false)
                return null;

            var condition = (RexNode)call.getOperands().get(0);
            var value = (RexNode)call.getOperands().get(1);

            var loweredCondition = Apply(condition, at) ?? condition;

            // What the arm may lean on: what the conjunct's siblings prove, and what the condition
            // proves of the rows the arm is read for.
            var within = at.Within(loweredCondition);
            if (within.NeverRead)
                return at.RexBuilder.makeLiteral(false);

            var loweredValue = Apply(value, within) ?? value;

            if (IsSafe(loweredValue))
                return RexUtil.composeConjunction(at.RexBuilder, new java.util.ArrayList { loweredCondition, loweredValue });

            if (ReferenceEquals(loweredCondition, condition) && ReferenceEquals(loweredValue, value))
                return null;

            return at.RexBuilder.makeCall(call.getType(), call.getOperator(), new java.util.ArrayList { loweredCondition, loweredValue, otherwise });
        }

        /// <summary>
        /// Rewrites a comparison over <c>CASE WHEN p THEN x END</c>, reached through operators that are
        /// null wherever an operand is, as <c>p AND CASE WHEN p THEN &lt;the comparison over x&gt;
        /// ELSE FALSE END</c>, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Where it comes from.</b> A merged view's column is <c>CASE WHEN &lt;that view's
        /// filter&gt; THEN … END</c>, and <see cref="TryFlattenCase"/> takes one apart where it is the
        /// condition. Where the column is a <em>value</em> inside one — a distance from it, then a
        /// comparison — nothing reached it (#189): the filter rules saw
        /// <c>&lt;(CLR_ST_GEOG_DISTANCE(CASE(p, location, null), ?), 50000)</c>, whose <c>p</c> carries
        /// the join's match test, <c>IS NOT NULL</c> over a <c>UUID</c> cast. That cast has no form at
        /// the service and is lowered only by being reached, so the whole filter stayed in process and
        /// every document was read to be measured.
        /// </para>
        /// <para>
        /// <b>Why the two select the same documents.</b> The <c>CASE</c> is null wherever <c>p</c> is
        /// not true, and every operator between it and the comparison is null when that operand is —
        /// measured for the distance, <c>CLR_ST_GEOG_DISTANCE(NULL, point)</c> answering null in
        /// process — so there the comparison is unknown, and a filter keeps nothing. Where <c>p</c> is
        /// true the <c>CASE</c> is <c>x</c>. So the comparison is true exactly where <c>p</c> is and
        /// the comparison over <c>x</c> is, which is the <c>CASE … ELSE FALSE</c> this builds, and
        /// <see cref="TryFlattenCase"/> takes that from there: lowers <c>p</c>, lowers the arm against
        /// what <c>p</c> proves, and takes it apart where the arm cannot raise. Through <c>AND</c> and
        /// <c>OR</c> only, as there, and for the reason given there.
        /// </para>
        /// <para>
        /// <b>Strict operators only, named rather than assumed.</b> A comparison, a cast, arithmetic,
        /// and the geodesic distance. An operator that answers something for a null — <c>COALESCE</c>,
        /// <c>IS NULL</c>, a <c>CASE</c> — would keep rows where <c>p</c> is false, and is not walked
        /// through.
        /// </para>
        /// <para>
        /// <b>Where the arm can still raise, <c>p</c> is put beside the <c>CASE</c> as well as kept
        /// inside it.</b> The arm here builds a geography from the document, and that raises over an
        /// object that is not GeoJSON — measured — so a bare <c>p AND …</c> rechecked in process could
        /// evaluate it over a document <c>p</c> excludes. Inside the <c>CASE</c> it is evaluated only
        /// where <c>p</c> holds; beside it, <c>p</c> is a conjunct the service can apply as it is,
        /// narrowing the read through its index where the ternary alone could not. The two are true on
        /// the same rows.
        /// </para>
        /// </remarks>
        static RexNode? TryLiftStrictCase(RexCall call, Rewriting at)
        {
            if (ComparisonOf(call.getKind().name()) is null)
                return null;

            if (FindStrictCase(call) is not RexCall found)
                return null;

            var condition = (RexNode)found.getOperands().get(0);
            var value = (RexNode)found.getOperands().get(1);

            var replaced = Replace(call, found, value);
            var guarded = (RexCall)at.RexBuilder.makeCall(SqlStdOperatorTable.CASE, condition, replaced, at.RexBuilder.makeLiteral(false));

            var flattened = TryFlattenCase(guarded, at) ?? guarded;

            if (flattened is RexCall still && still.getKind().name() == nameof(SqlKind.__Enum.CASE))
                return RexUtil.composeConjunction(at.RexBuilder, new java.util.ArrayList { (RexNode)still.getOperands().get(0), still });

            return flattened;
        }

        /// <summary>
        /// Finds a <c>CASE WHEN p THEN x END</c> — one arm, an <c>ELSE</c> of null — reached from a
        /// comparison through strict operators only, or returns <c>null</c>.
        /// </summary>
        static RexCall? FindStrictCase(RexCall call)
        {
            for (var i = 0; i < call.getOperands().size(); i++)
            {
                if ((RexNode)call.getOperands().get(i) is not RexCall operand)
                    continue;

                if (operand.getKind().name() == nameof(SqlKind.__Enum.CASE))
                {
                    if (operand.getOperands().size() == 3 && (RexNode)operand.getOperands().get(2) is RexLiteral otherwise && otherwise.isNull())
                        return operand;

                    continue;
                }

                if (IsStrict(operand) && FindStrictCase(operand) is RexCall found)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// Determines whether an operator is null wherever any of its operands is.
        /// </summary>
        /// <remarks>
        /// A short list rather than a general test, because a function's null behaviour is its
        /// implementation's and not its signature's. The distance is here by measurement —
        /// <c>CLR_ST_GEOG_DISTANCE</c> answers null in process for a null on either side.
        /// </remarks>
        static bool IsStrict(RexCall call) =>
            call.getKind().name() is nameof(SqlKind.__Enum.CAST)
                or nameof(SqlKind.__Enum.PLUS) or nameof(SqlKind.__Enum.MINUS)
                or nameof(SqlKind.__Enum.TIMES) or nameof(SqlKind.__Enum.DIVIDE)
            || string.Equals(call.getOperator().getName(), Apache.Calcite.Geography.Sql.GeographyOperatorTable.ClrStGeogDistance.getName(), StringComparison.Ordinal);

        /// <summary>
        /// Returns an expression with one node replaced, rebuilding only the calls above it.
        /// </summary>
        static RexNode Replace(RexNode node, RexNode target, RexNode replacement)
        {
            if (ReferenceEquals(node, target))
                return replacement;

            if (node is not RexCall call)
                return node;

            var operands = new java.util.ArrayList();
            var changed = false;

            for (var i = 0; i < call.getOperands().size(); i++)
            {
                var operand = (RexNode)call.getOperands().get(i);
                var rewritten = Replace(operand, target, replacement);

                operands.add(rewritten);
                changed |= ReferenceEquals(rewritten, operand) == false;
            }

            return changed ? call.clone(call.getType(), operands) : call;
        }

        /// <summary>
        /// Determines whether evaluating an expression can raise.
        /// </summary>
        /// <remarks>
        /// Conservative, and built from the inside: what is admitted is a short list of operators that
        /// answer for every input — the connectives, the comparisons, the null and truth tests, a
        /// <c>CASE</c>, the text accessor, which answers null where it has nothing to render, and the
        /// service's type tests, whose in-process bodies answer false for what they cannot read. A
        /// cast, an arithmetic operator and any function not named here are refused, which can only
        /// leave a <c>CASE</c> in place. It is the test Calcite's simplifier applies before taking a
        /// <c>CASE</c> apart, asked of the expression after the lowering rather than before it.
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns><c>true</c> where no input makes it raise.</returns>
        static bool IsSafe(RexNode node)
        {
            switch (node)
            {
                case RexInputRef:
                case RexLiteral:
                case RexDynamicParam:
                    return true;

                case RexCall call:
                    var safe = call.getKind().name() switch
                    {
                        nameof(SqlKind.__Enum.AND) or nameof(SqlKind.__Enum.OR) or nameof(SqlKind.__Enum.NOT)
                            or nameof(SqlKind.__Enum.EQUALS) or nameof(SqlKind.__Enum.NOT_EQUALS)
                            or nameof(SqlKind.__Enum.LESS_THAN) or nameof(SqlKind.__Enum.LESS_THAN_OR_EQUAL)
                            or nameof(SqlKind.__Enum.GREATER_THAN) or nameof(SqlKind.__Enum.GREATER_THAN_OR_EQUAL)
                            or nameof(SqlKind.__Enum.IS_NULL) or nameof(SqlKind.__Enum.IS_NOT_NULL)
                            or nameof(SqlKind.__Enum.IS_TRUE) or nameof(SqlKind.__Enum.IS_NOT_TRUE)
                            or nameof(SqlKind.__Enum.IS_FALSE) or nameof(SqlKind.__Enum.IS_NOT_FALSE)
                            or nameof(SqlKind.__Enum.CASE) or nameof(SqlKind.__Enum.SEARCH) => true,
                        _ => CosmosRexTranslator.IsTextJsonValue(call) && CosmosRexTranslator.IsPlainJsonValue(call)
                            || CosmosOperators.IsAbsenceObserving(call.getOperator()),
                    };

                    if (safe == false)
                        return false;

                    for (var i = 0; i < call.getOperands().size(); i++)
                        if (IsSafe((RexNode)call.getOperands().get(i)) == false)
                            return false;

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Determines whether a predicate holds a <c>CASE</c> anywhere.
        /// </summary>
        /// <param name="node">The predicate.</param>
        /// <returns><c>true</c> where one is found.</returns>
        static bool ContainsCase(RexNode node)
        {
            if (node is not RexCall call)
                return false;

            if (call.getKind().name() == nameof(SqlKind.__Enum.CASE))
                return true;

            for (var i = 0; i < call.getOperands().size(); i++)
                if (ContainsCase((RexNode)call.getOperands().get(i)))
                    return true;

            return false;
        }

        /// <summary>
        /// Maps a comparison kind onto the operator that rebuilds it, or <c>null</c> where the kind is
        /// not one an ordering lowers.
        /// </summary>
        /// <param name="kind">The kind name.</param>
        /// <returns>The operator, or <c>null</c>.</returns>
        static SqlOperator? ComparisonOf(string kind) => kind switch
        {
            nameof(SqlKind.__Enum.EQUALS) => SqlStdOperatorTable.EQUALS,
            nameof(SqlKind.__Enum.NOT_EQUALS) => SqlStdOperatorTable.NOT_EQUALS,
            nameof(SqlKind.__Enum.GREATER_THAN) => SqlStdOperatorTable.GREATER_THAN,
            nameof(SqlKind.__Enum.GREATER_THAN_OR_EQUAL) => SqlStdOperatorTable.GREATER_THAN_OR_EQUAL,
            nameof(SqlKind.__Enum.LESS_THAN) => SqlStdOperatorTable.LESS_THAN,
            nameof(SqlKind.__Enum.LESS_THAN_OR_EQUAL) => SqlStdOperatorTable.LESS_THAN_OR_EQUAL,
            _ => null,
        };

        /// <summary>
        /// Returns the operator that means the same thing with its operands the other way round.
        /// </summary>
        /// <param name="comparison">The operator as written.</param>
        /// <returns>Its reverse.</returns>
        static SqlOperator Reverse(SqlOperator comparison)
        {
            if (comparison == SqlStdOperatorTable.GREATER_THAN)
                return SqlStdOperatorTable.LESS_THAN;

            if (comparison == SqlStdOperatorTable.GREATER_THAN_OR_EQUAL)
                return SqlStdOperatorTable.LESS_THAN_OR_EQUAL;

            if (comparison == SqlStdOperatorTable.LESS_THAN)
                return SqlStdOperatorTable.GREATER_THAN;

            if (comparison == SqlStdOperatorTable.LESS_THAN_OR_EQUAL)
                return SqlStdOperatorTable.GREATER_THAN_OR_EQUAL;

            // `=` and `<>` are their own reverse.
            return comparison;
        }

        /// <summary>
        /// Lowers a comparison between a UUID read out of a path and a UUID literal into a comparison
        /// between the stored strings, where the path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Equality and ordering ask different things of the form, and the second only recently
        /// gets an answer.</b> Equality needs one spelling per value, which a canonical form has by
        /// being canonical. An ordering needs the lexical order of the stored strings to be the order
        /// the engine compares the values in, and until
        /// <a href="https://issues.apache.org/jira/browse/CALCITE-7716">CALCITE-7716</a> the engine
        /// compared the two 64-bit halves as signed longs, so a canonical form gave that only where
        /// the schema also confined the first hex digit. The comparison is unsigned from 1.43 and
        /// <see cref="CosmosUuidForms.CanonicalLower"/> preserves order with it — but the rows
        /// still carry the two bits separately, and this gates on them separately, because the engine
        /// keeps a switch that puts the old semantics back.
        /// </para>
        /// <para>
        /// <b>The literal is rendered into the path's own spelling</b>, which is the whole of what the
        /// UUID forms differ by; <see cref="CosmosStoredForms.RenderUuid"/> decides it, and refuses
        /// where the form stores something other than a UUID.
        /// </para>
        /// <para>
        /// <b><c>&lt;&gt;</c> comes with the range rather than with the order.</b> It asks the
        /// equality's question and is gated with it, and it lowers now only because the operator is
        /// carried through at all — the previous spelling reached this from the <c>EQUALS</c> branch
        /// alone and left the inequality in process for no reason either bit gives.
        /// </para>
        /// </remarks>
        /// <param name="castNode">The operand that may read a path as a UUID.</param>
        /// <param name="literalNode">The operand that may be the literal.</param>
        /// <param name="comparison">The operator, already reversed where the operands were read backwards.</param>
        /// <param name="translator">Resolves the path underneath.</param>
        /// <param name="known">What the container has been shown to hold.</param>
        /// <param name="rootAlias">The alias a path must be rooted at.</param>
        /// <param name="rexBuilder">Builds the lowered comparison.</param>
        /// <returns>The lowered comparison, or <c>null</c>.</returns>
        /// <summary>
        /// Lowers a null test over a <c>UUID</c> cast into the same test over the text it casts, where the
        /// path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A cast is null exactly where its operand is</b> — unless the text does not parse, where a
        /// <c>CAST</c> raises and a <c>SAFE_CAST</c> answers null. A declared UUID form says every string at
        /// the path parses, so neither happens, and the test over the cast and the test over the text are
        /// one test. The text test is one the service evaluates; the cast test is not, and alone it kept a
        /// predicate whole in process.
        /// </para>
        /// <para>
        /// <b>Where it comes from.</b> A join that equates a key reads, over the one document it pairs, as
        /// <c>k = k</c> — which is <c>k IS NOT NULL</c> — and an identifier key is a <c>UUID</c> cast. So
        /// every merged self-join on such a key carries this conjunct. #177.
        /// </para>
        /// </remarks>
        static RexNode? TryLowerUuidNullTest(RexCall call, Rewriting at)
        {
            if (call.getOperands().size() != 1 || (RexNode)call.getOperands().get(0) is not RexCall cast)
                return null;

            var kind = cast.getKind().name();
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
                return null;

            if (cast.getType()?.getSqlTypeName() != SqlTypeName.UUID || cast.getOperands().size() != 1)
                return null;

            var operand = (RexNode)cast.getOperands().get(0);

            if (at.Translator.TryResolvePath(operand, out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, at.RootAlias, StringComparison.Ordinal) == false)
                return null;

            if (CosmosDocumentPaths.From(path) is not JsonDocumentPath document)
                return null;

            if (at.Known.RepresentationOf(document) is not CosmosRepresentation representation || CosmosUuidForms.IsUuid(representation) == false)
                return null;

            return at.RexBuilder.makeCall(call.getOperator(), operand);
        }

        static RexNode? TryLowerUuid(RexNode castNode, RexNode literalNode, SqlOperator comparison, Rewriting at)
        {
            if (literalNode is not RexLiteral literal || UuidOf(literal) is not Guid value)
                return null;

            var ordering = comparison != SqlStdOperatorTable.EQUALS && comparison != SqlStdOperatorTable.NOT_EQUALS;

            if (TryStoredUuid(castNode, ordering, at.Translator, () => at.Known, at.RootAlias, out var representation) is not RexNode operand)
                return null;

            // And the form has to be one whose stored spelling this knows how to write. A
            // representation that pins some other shape is not this rewrite's business, however well
            // declared.
            if (CosmosStoredForms.RenderUuid(representation, value) is not string stored)
                return null;

            return at.RexBuilder.makeCall(comparison, operand, at.RexBuilder.makeLiteral(stored));
        }

        /// <summary>
        /// Returns the text accessor underneath a cast to <c>UUID</c>, where the path's declared form
        /// makes comparing the stored strings answer what comparing the values would — or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The license, asked apart from what the comparison is against, for the reason
        /// <see cref="TryStoredInstant"/> gives: <see cref="TryLowerUuid"/> writes a literal in the
        /// form while the plan is made, and <see cref="CosmosRexTranslator"/> binds a parameter to be
        /// written in it when the statement runs (#207). Whether the stored strings answer for the
        /// values is a question about the path and the operator, so both ask it here.
        /// </remarks>
        /// <param name="castNode">The expression that may read a path as a UUID.</param>
        /// <param name="ordering">Whether the comparison is an ordering rather than an equality or an inequality.</param>
        /// <param name="translator">Resolves the path underneath.</param>
        /// <param name="known">What the container has been shown to hold, asked only once the shape is found.</param>
        /// <param name="rootAlias">The alias a path must be rooted at.</param>
        /// <param name="representation">On success, the path's declared form.</param>
        /// <returns>The text accessor, or <c>null</c>.</returns>
        internal static RexNode? TryStoredUuid(RexNode castNode, bool ordering, CosmosRexTranslator translator, Func<JsonFactSet> known, string rootAlias, out CosmosRepresentation representation)
        {
            representation = default;

            if (castNode is not RexCall cast)
                return null;

            var kind = cast.getKind().name();
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
                return null;

            if (cast.getType()?.getSqlTypeName() != SqlTypeName.UUID || cast.getOperands().size() != 1)
                return null;

            var operand = (RexNode)cast.getOperands().get(0);

            if (translator.TryResolvePath(operand, out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            if (CosmosDocumentPaths.From(path) is not JsonDocumentPath document)
                return null;

            if (known().RepresentationOf(document) is not CosmosRepresentation declared || CosmosUuidForms.IsUuid(declared) == false)
                return null;

            if (ordering ? declared.PreservesOrder == false : declared.PreservesEquality == false)
                return null;

            representation = declared;
            return operand;
        }

        /// <summary>
        /// Lowers a comparison between an instant read out of a path and a temporal literal into a
        /// comparison between the stored strings, where the path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Equality and ordering ask different things of the form.</b> Equality needs one spelling
        /// per value, which every form here has. An ordering needs the lexical order of the stored
        /// strings to be the chronological order, which only a fixed shape gives — so the two are
        /// gated separately, on exactly the two properties
        /// <see cref="CosmosRepresentation"/> carries.
        /// </para>
        /// <para>
        /// <b>The literal is rendered into the path's own shape, and refused where it will not fit.</b>
        /// <see cref="CosmosStoredForms.RenderDateTime(CosmosRepresentation, DateTime)"/> is what decides that, and why truncating
        /// would not be sound is recorded there.
        /// </para>
        /// <para>
        /// <b>A parse is a chain rather than a cast, and carries a second condition.</b> A cast says
        /// only that the text is to be read as an instant, and how is Calcite's business. A
        /// <c>PARSE_DATE</c> or <c>PARSE_DATETIME</c> says how, in a format string, and the rewrite is
        /// an equivalence only where that format reads <em>this</em> shape faithfully — a format that
        /// reads it some other way maps the stored strings onto some other instants, and comparing the
        /// strings then answers a different question. <see cref="CosmosStoredForms.ParsesExactly"/> is
        /// where that is decided and where the measurements behind it are recorded.
        /// </para>
        /// </remarks>
        static RexNode? TryLowerInstant(RexNode temporalNode, RexNode literalNode, SqlOperator comparison, Rewriting at)
        {
            if (literalNode is not RexLiteral literal || InstantOf(literal) is not DateTime value)
                return null;

            var ordering = comparison != SqlStdOperatorTable.EQUALS && comparison != SqlStdOperatorTable.NOT_EQUALS;

            if (TryStoredInstant(temporalNode, ordering, at.Translator, () => at.Known, at.RootAlias, out var representation) is not RexNode accessor)
                return null;

            if (CosmosStoredForms.RenderDateTime(representation, value) is not string stored)
                return null;

            return at.RexBuilder.makeCall(comparison, accessor, at.RexBuilder.makeLiteral(stored));
        }

        /// <summary>
        /// Returns the text accessor underneath an expression that reads a path as an instant, where
        /// the path's declared form makes comparing the stored strings answer what comparing the
        /// instants would — or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The license, asked apart from what the comparison is against, because two things ask it.
        /// <see cref="TryLowerInstant"/> writes a literal in the form while the plan is made, and
        /// <see cref="CosmosRexTranslator"/> binds a parameter to be written in it when the statement
        /// runs. Whether the stored strings answer for the instants is a question about the path and
        /// the comparison and not about the value, so it is one question asked in one place, and the
        /// two cannot come to disagree about which paths they act on.
        /// </para>
        /// <para>
        /// What the value then has to satisfy differs between them, and is theirs to ask: a literal
        /// that does not land on the form is refused, and a parameter is rounded onto it — see
        /// <see cref="CosmosTemporalRounding"/>.
        /// </para>
        /// </remarks>
        /// <param name="temporalNode">The expression that may read a path as an instant.</param>
        /// <param name="ordering">Whether the comparison is an ordering rather than an equality or an inequality.</param>
        /// <param name="translator">Resolves the path underneath.</param>
        /// <param name="known">What the container has been shown to hold, asked only once the shape is found.</param>
        /// <param name="rootAlias">The alias a path must be rooted at.</param>
        /// <param name="representation">On success, the path's declared form.</param>
        /// <returns>The text accessor, or <c>null</c>.</returns>
        internal static RexNode? TryStoredInstant(RexNode temporalNode, bool ordering, CosmosRexTranslator translator, Func<JsonFactSet> known, string rootAlias, out CosmosRepresentation representation)
        {
            representation = default;

            if (TextAccessorOf(temporalNode, out var format, out var held) is not RexNode accessor)
                return null;

            if (translator.TryResolvePath(accessor, out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            if (CosmosDocumentPaths.From(path) is not JsonDocumentPath document)
                return null;

            if (known().RepresentationOf(document) is not CosmosRepresentation declared)
                return null;

            if (ordering ? declared.PreservesOrder == false : declared.PreservesEquality == false)
                return null;

            // A parse names how the text is read and is licensed by the format reading the shape; a
            // cast leaves it to the engine, and is licensed only where the engine can do it. Neither
            // is optional: dropping a conversion the engine would have failed at answers rows where
            // the query answers an error, which is a different query rather than a faster one.
            if (format is not null
                ? CosmosStoredForms.ParsesExactly(declared, format, held) == false
                : CosmosStoredForms.EngineReads(declared, held) == false)
                return null;

            representation = declared;
            return accessor;
        }

        /// <summary>
        /// Lowers a comparison between a number read out of a path and a numeric literal into a
        /// comparison between the stored strings, where the path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A string spelling a number is the case this exists for.</b> Cosmos has numbers, so a
        /// path holding one needs none of this; what needs it is the very common path holding a
        /// <em>string</em> that spells one — an account number, a code, a zero-padded sequence — which
        /// a caller reaches through <c>CAST(… AS INTEGER)</c> and which was read whole because the
        /// cast has no Cosmos form.
        /// </para>
        /// <para>
        /// <b>The whole question is whether the spelling is faithful, and a pattern can say so.</b>
        /// Measured, Calcite's cast reads <c>'042'</c>, <c>'+42'</c>, <c>' 42'</c> and <c>'42'</c> as
        /// the same number, so a form admitting any two of them gives one value two spellings and a
        /// string equality would miss documents. A pattern that forbids the padding, or fixes it,
        /// admits exactly one — which is what <see cref="CosmosNumericForms.IntegerUnpadded"/> and
        /// <see cref="CosmosNumericForms.IntegerFixedWidth"/> record.
        /// </para>
        /// <para>
        /// <b>Ordering asks the stronger question and only the padded form answers it.</b> A lexical
        /// comparison compares the first differing character, which is a comparison of digits at equal
        /// significance only when the strings are the same length; unpadded, <c>'9'</c> sorts after
        /// <c>'42'</c>. So the two are gated separately on the two properties the form carries, exactly
        /// as the temporal lowering gates them.
        /// </para>
        /// </remarks>
        /// <param name="numericNode">The operand that may read a path as a number.</param>
        /// <param name="literalNode">The operand that may be the literal.</param>
        /// <param name="comparison">The operator, already reversed where the operands were read backwards.</param>
        /// <param name="at">Where the rewrite stands, which says what is known.</param>
        /// <returns>The lowered comparison, or <c>null</c>.</returns>
        static RexNode? TryLowerNumber(RexNode numericNode, RexNode literalNode, SqlOperator comparison, Rewriting at)
        {
            if (literalNode is not RexLiteral literal || IntegerOf(literal) is not long value)
                return null;

            if (NumericAccessorOf(numericNode) is not RexNode accessor)
                return null;

            if (at.Translator.TryResolvePath(accessor, out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, at.RootAlias, StringComparison.Ordinal) == false)
                return null;

            if (CosmosDocumentPaths.From(path) is not JsonDocumentPath document)
                return null;

            if (at.Known.RepresentationOf(document) is not CosmosRepresentation representation)
                return null;

            var ordering = comparison != SqlStdOperatorTable.EQUALS && comparison != SqlStdOperatorTable.NOT_EQUALS;

            if (ordering ? representation.PreservesOrder == false : representation.PreservesEquality == false)
                return null;

            if (CosmosStoredForms.RenderInteger(representation, value) is not string stored)
                return null;

            return at.RexBuilder.makeCall(comparison, accessor, at.RexBuilder.makeLiteral(stored));
        }

        /// <summary>
        /// Returns the accessor answering the stored text underneath an expression that reads a path
        /// as an exact number, or <c>null</c> where the expression is not one of those.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the cast, and only over the text. <c>RETURNING INTEGER</c> is not a second spelling
        /// here the way <c>RETURNING VARCHAR</c> is for a UUID: <c>RETURNING</c> asserts the extracted
        /// type rather than converting to it, and a path holding a string never extracts as a number,
        /// so the clause fails at run time rather than naming this shape. Except <c>DECIMAL</c>, which
        /// parses a stored string as the cast does and is a second spelling in principle; it addresses
        /// no path — <see cref="CosmosRexTranslator.IsParsingJsonValue"/> — and reading it here is a
        /// recovery <c>TODO.md</c> records rather than a shape this misses.
        /// </para>
        /// <para>
        /// The approximate types are absent deliberately. A stored spelling maps to one
        /// <c>DOUBLE</c>, but a <c>DOUBLE</c> maps back to many spellings — the value is rounded
        /// before it is compared — so the literal could not be rendered into the container's shape
        /// without changing which documents match.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns>The text accessor, or <c>null</c>.</returns>
        static RexNode? NumericAccessorOf(RexNode node)
        {
            if (node is not RexCall call || call.getOperands().size() != 1)
                return null;

            var kind = call.getKind().name();
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
                return null;

            var type = call.getType()?.getSqlTypeName();
            if (type != SqlTypeName.TINYINT && type != SqlTypeName.SMALLINT && type != SqlTypeName.INTEGER
                && type != SqlTypeName.BIGINT && type != SqlTypeName.DECIMAL)
                return null;

            return (RexNode)call.getOperands().get(0);
        }

        /// <summary>
        /// Reads the whole number a numeric literal carries, or <c>null</c> where it carries none.
        /// </summary>
        /// <remarks>
        /// A value with a fractional part has no spelling in an integer form, so it is refused here
        /// rather than rounded into one — the comparison would then select different documents. A
        /// decimal literal that happens to be whole is accepted, <c>42.0</c> and <c>42</c> being the
        /// same number however the query wrote it.
        /// </remarks>
        /// <param name="literal">The literal.</param>
        /// <returns>The value, or <c>null</c>.</returns>
        static long? IntegerOf(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();
            if (type != SqlTypeName.DECIMAL && type != SqlTypeName.INTEGER && type != SqlTypeName.BIGINT
                && type != SqlTypeName.SMALLINT && type != SqlTypeName.TINYINT)
                return null;

            try
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal d => d.stripTrailingZeros().scale() <= 0 ? d.longValueExact() : null,
                    java.lang.Long l => l.longValue(),
                    java.lang.Integer i => (long)i.intValue(),
                    _ => null,
                };
            }
            catch (java.lang.ArithmeticException)
            {
                // Beyond a long, which no stored spelling this renders could match anyway.
                return null;
            }
        }

        /// <summary>
        /// Returns the accessor answering the stored text underneath an expression that reads a path
        /// as an instant, or <c>null</c> where the expression is not one of those.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two spellings reach here. <c>CAST(JSON_VALUE(…) AS TIMESTAMP)</c> carries the text
        /// accessor as its operand, so the operand is the answer — the same one the UUID lowering
        /// takes. And a <em>chain</em>: <c>PARSE_DATE</c> and <c>PARSE_DATETIME</c> carry the text as
        /// their second operand and a format as their first, and
        /// <c>CAST(… AS TIMESTAMP(3) FORMAT '…')</c> carries the same pair the other way round. The
        /// format is handed back beside the accessor because it is half of what licenses the rewrite —
        /// see <see cref="TryLowerInstant"/>. <see cref="CosmosTemporalParse.TryRead"/> tells the
        /// chains apart, which is why a cast is read here only where it has no format.
        /// </para>
        /// <para>
        /// <b><c>JSON_VALUE(…, '$.p' RETURNING TIMESTAMP)</c> was a third and has been withdrawn,
        /// because the clause does not mean what this read it as.</b> Measured at Calcite's own
        /// runtime: it raises for <em>every</em> string — including <c>2024-01-15 12:30:00</c>, the
        /// one shape the cast accepts — and answers correctly for a JSON <em>number</em> of epoch
        /// milliseconds. <c>RETURNING</c> asserts the extracted type rather than converting to it,
        /// which is the reading <see cref="NumericAccessorOf"/> already records for
        /// <c>RETURNING INTEGER</c>. So over a path storing text the engine cannot evaluate the
        /// expression at all, and lowering it onto the stored strings answered rows for a query that
        /// raises. A path storing a number has no temporal form declared for it, so nothing that was
        /// sound is lost by no longer looking.
        /// </para>
        /// <para>
        /// <c>PARSE_TIMESTAMP</c> is shaped like the parse and does not reach here, its type being
        /// <c>TIMESTAMP WITH LOCAL TIME ZONE</c>: a comparison between one of those and a zone-less
        /// literal is a question about the session's zone, and a declared stored form answers nothing
        /// about that. <c>PARSE_TIME</c> is left out by the same test, a <c>TIME</c> literal carrying
        /// a millisecond-of-day rather than the calendar <see cref="InstantOf"/> reads.
        /// </para>
        /// <para>
        /// The type test is what keeps all of them apart from an ordinary <c>VARCHAR</c> accessor,
        /// which needs no lowering and must not be given one.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <param name="format">
        /// On success, the format a parse reads the text with, or <c>null</c> where the expression
        /// names no format and the conversion is Calcite's own.
        /// </param>
        /// <param name="held">On success, the halves of an instant the expression's value holds.</param>
        /// <returns>The text accessor, or <c>null</c>.</returns>
        static RexNode? TextAccessorOf(RexNode node, out string? format, out CosmosTemporalParts held)
        {
            format = null;
            held = CosmosTemporalParts.None;

            if (node is not RexCall call)
                return null;

            var type = call.getType()?.getSqlTypeName();
            if (type != SqlTypeName.TIMESTAMP && type != SqlTypeName.DATE)
                return null;

            held = CosmosTemporalParse.PartsOf(type);

            var kind = call.getKind().name();

            if ((kind == nameof(SqlKind.__Enum.CAST) || kind == nameof(SqlKind.__Enum.SAFE_CAST)) && call.getOperands().size() == 1)
                return (RexNode)call.getOperands().get(0);

            if (CosmosTemporalParse.TryRead(call, out var text, out var written, out var parsed) && text is not null)
            {
                format = written;
                held = parsed;
                return text;
            }

            return null;
        }

        /// <summary>
        /// Reads the instant a temporal literal carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The value rather than a spelling, for the reason <see cref="UuidOf"/> gives: which spelling
        /// a conforming document stores is the path form to say, and
        /// <see cref="CosmosStoredForms.RenderDateTime(CosmosRepresentation, DateTime)"/> is what says it.
        /// </para>
        /// <para>
        /// <b>Read from the calendar rather than from its text.</b> Measured: <c>getValue</c> answers a
        /// <c>GregorianCalendar</c>, whose <c>toString</c> is the Java dump — <c>areFieldsSet</c> and
        /// all — and parses as nothing. The calendar is pinned to UTC and its epoch milliseconds are
        /// the wall clock the query wrote, read as UTC, which is exactly the reading these forms
        /// store. Anything else yields no rewrite rather than a guess.
        /// </para>
        /// </remarks>
        /// <param name="literal">The literal.</param>
        /// <returns>The value, or <c>null</c> where the literal is not one this can read.</returns>
        static DateTime? InstantOf(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();
            if (type != SqlTypeName.TIMESTAMP && type != SqlTypeName.DATE)
                return null;

            if (literal.getValue() is not java.util.Calendar calendar)
                return null;

            return DateTimeOffset.FromUnixTimeMilliseconds(calendar.getTimeInMillis()).UtcDateTime;
        }

        /// <summary>
        /// Reads the value a UUID literal carries.
        /// </summary>
        /// <remarks>
        /// The value and not a spelling: which spelling a conforming document stores is the path form
        /// to say, and <see cref="CosmosStoredForms.RenderUuid"/> is what says it. The box is
        /// <c>org.apache.calcite.util.UuidValue</c>, which is what <c>RexBuilder.makeUuidLiteral</c>
        /// wraps a <c>java.util.UUID</c> into since CALCITE-7716 — the same move that put
        /// <c>UuidValue</c> on the read path, in <see cref="Client.CosmosJson"/>. The fallback exists
        /// because the boxed representation of a literal is Calcite business rather than this adapter
        /// business, and text that will not parse yields no rewrite rather than a guess.
        /// </remarks>
        /// <param name="literal">The literal.</param>
        /// <returns>The value, or <c>null</c> where the literal is not a UUID this can read.</returns>
        static Guid? UuidOf(RexLiteral literal)
        {
            if (literal.isNull() || literal.getTypeName() != SqlTypeName.UUID)
                return null;

            var text = literal.getValue() is org.apache.calcite.util.UuidValue uuid ? uuid.toString() : literal.getValue()?.ToString();

            return text is not null && Guid.TryParse(text, out var value) ? value : null;
        }

    }

}
