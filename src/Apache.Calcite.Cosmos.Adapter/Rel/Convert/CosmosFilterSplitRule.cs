using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Splits a filter whose condition is partly renderable into the part Cosmos can evaluate and the
    /// part it cannot, so that the service does the work it is able to instead of none of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WHERE a AND b</c>, where <c>a</c> renders and <c>b</c> does not, becomes <c>WHERE b</c> over
    /// <c>WHERE a</c>. The inner filter converts to a <see cref="CosmosFilter"/> and reaches the service;
    /// the outer stays in Calcite and rechecks what is left. Without this the whole predicate is
    /// declined and every document in the container crosses the wire.
    /// </para>
    /// <para>
    /// <b>Why it is sound.</b> The pushed condition is a conjunction of a subset of the original's
    /// conjuncts, so the original <em>implies</em> it. A weaker filter can only discard rows the original
    /// would have discarded too, and the outer filter — the remaining conjuncts, applied to what
    /// survives — restores exactly the original result. The rule never needs to reason about what a
    /// predicate <em>means</em>, only that dropping conjuncts weakens.
    /// </para>
    /// <para>
    /// <b>Why only conjunctions.</b> Weakening is sound in a positive position and nowhere else.
    /// Dropping a disjunct from <c>a OR b</c> <em>strengthens</em> the predicate and loses every row that
    /// satisfied only the dropped side, so an <c>OR</c> is pushable only when every branch is, which is
    /// what the existing all-or-nothing rule already decides. Under a <c>NOT</c> the polarity flips and
    /// a weaker inner predicate makes a stronger outer one. Top-level conjuncts are the case where
    /// polarity needs no analysis: each one is positive by construction.
    /// </para>
    /// <para>
    /// The split terminates. It fires only when both parts are non-empty, and neither of the two filters
    /// it produces has that property — the inner is wholly renderable, the outer wholly not.
    /// </para>
    /// <para>
    /// <b>What the container declares is lowered first.</b> A comparison with no Cosmos form may have
    /// one once the declared stored form is taken into account, and the fact that says so is usually
    /// conditional on a sibling conjunct — so the whole condition is rewritten before it is taken apart,
    /// while the conjunct that proves the condition is still beside the one it licenses. See
    /// <see cref="Metadata.CosmosFactRewriter"/>.
    /// </para>
    /// <para>
    /// <b>Which conjuncts push is the oracle's strata</b> (<see cref="Metadata.CosmosFilterStatement"/>):
    /// a conjunct is pushed where it renders from what the conjuncts already pushed establish, round
    /// after round, and its own facts enter only the round after it. So a guard pushed beside what it
    /// licenses is the only kind of help a conjunct gets, to any depth, and none is its own. A
    /// weakening of a conjunct left above is rendered from everything pushed, and is pushed beside it.
    /// </para>
    /// </remarks>
    public class CosmosFilterSplitRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosFilterSplitRule Create(CosmosConvention convention)
        {
            return new CosmosFilterSplitRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        // RelOptRule's operand builders are deprecated in favour of RelRule.Config, whose operand
        // supplier is a chain of Calcite functional interfaces that costs more ceremony from C# than it
        // buys. The deprecation is "to be removed before 2.0" and the API is unchanged; this is not a
        // rule shape worth expressing twice.
#pragma warning disable CS0612
        public CosmosFilterSplitRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Filter), any()),
                "CosmosFilterSplitRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <summary>
        /// Returns the container a subtree reads, or <c>null</c> where it does not read one.
        /// </summary>
        /// <remarks>
        /// Walked rather than read off a fixed operand position, because the filter may sit above a
        /// projection or a traversal as easily as above the scan — the split is the same argument
        /// either way, and <see cref="Split"/> already binds through them.
        /// </remarks>
        internal static CosmosTable? FindTable(RelNode? node)
        {
            return FindTable(node, CosmosPlanMembers.NewSeen());
        }

        /// <inheritdoc cref="FindTable(RelNode?)" />
        /// <remarks>
        /// Every member is asked, not one representative: which container a subtree reads is a
        /// question about its <em>shape</em>, and a member whose type none of the cases mention
        /// answers "none" for a plan that reads one. See <see cref="CosmosPlanMembers"/>.
        /// </remarks>
        /// <param name="node">The subtree.</param>
        /// <param name="seen">The expressions already asked, which keeps a graph finite.</param>
        static CosmosTable? FindTable(RelNode? node, System.Collections.Generic.HashSet<RelNode> seen)
        {
            foreach (var member in CosmosPlanMembers.Of(node))
            {
                if (seen.Add(member) == false)
                    continue;

                var found = member switch
                {
                    TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) as CosmosTable,
                    Filter filter => FindTable(filter.getInput(), seen),
                    Project project => FindTable(project.getInput(), seen),
                    Correlate correlate => FindTable(correlate.getLeft(), seen),
                    _ => null,
                };

                if (found is not null)
                    return found;
            }

            return null;
        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            // Scoped to this convention's own container. A rule set is registered per container, and a
            // filter over somebody else's table is not this rule's business.
            if (FindTable(((Filter)call.rel(0)).getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            var (pushable, residual) = Split((Filter)call.rel(0), _convention.Container);

            return pushable.Count > 0 && residual.Count > 0;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            var (pushable, residual) = Split(filter, _convention.Container);
            if (pushable.Count == 0 || residual.Count == 0)
                return;

            var rexBuilder = filter.getCluster().getRexBuilder();

            var inner = filter.copy(filter.getTraitSet(), filter.getInput(), Compose(rexBuilder, pushable));
            var outer = filter.copy(filter.getTraitSet(), inner, Compose(rexBuilder, residual));

            call.transformTo(outer);
        }

        /// <summary>
        /// Partitions a filter's top-level conjuncts by whether each renders as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Each conjunct is tested on its own, against the same binding <see cref="CosmosFilterRule"/>
        /// uses, so a conjunct this puts in the pushable half is one that rule would accept whole. An
        /// input whose binding cannot be derived splits into nothing, and the rule does not fire.
        /// </remarks>
        /// <param name="filter">The filter.</param>
        /// <param name="container">The container it reads.</param>
        /// <param name="projecting">Whether the residual projection rule asks (site R), which fires only where nothing pushes.</param>
        internal static (List<RexNode> Pushable, List<RexNode> Residual) Split(Filter filter, Metadata.CosmosContainerMetadata container, bool projecting = false)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _) == false)
                return (new List<RexNode>(), new List<RexNode>());

            var rexBuilder = filter.getCluster().getRexBuilder();

            // No weakening over a projection. FILTER_PROJECT_TRANSPOSE gives every such filter an
            // equivalent beneath the projection, where its weakenings are derived against the filters
            // already applied. Derived here they are blind to those: a filter left over a pushed
            // projection is a residual whose weakenings are already below it, and pushing them again
            // put a redundant definedness test in the statement, or one a declared presence had
            // deleted (#210).
            var weakens = OverProjection(filter.getInput()) == false;

            // Lowered before it is split, and that order is the point: a fact is usually conditional,
            // and the conjunct that proves the condition is a sibling of the one being lowered. Split
            // first and each conjunct would be weighed on its own, with nothing left to prove the
            // guard from.
            var condition = Metadata.CosmosFactRewriter.Rewrite(
                filter.getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);

            // Admitted stratum by stratum: a conjunct pushes where it renders from what the conjuncts
            // pushed before it establish. A fact licenses a comparison only where the conjunct that
            // proved it is applied beside it -- a guard left above in the residual would leave the
            // service deciding rows the guard was meant to have excluded -- and only a conjunct pushed
            // beside it is in a stratum it is rendered from.
            var statement = Metadata.CosmosFilterStatement.Of(condition, fields, readings, container, rexBuilder);

            var pushable = new List<RexNode>();
            var residual = new List<RexNode>();

            // Site W: where a flow armed the shadow, S's weakening of each residual beside J's; writing, S's in its place.
            var arm = Metadata.CosmosRenderingShadow.Armed ? Metadata.CosmosRenderingShadow.Current : null;

            // What lies beneath a weakening, beside the conjuncts the statement admits: every conjunct the
            // filters beneath apply, and the weakenings pushed before it. A weakening they, the admitted
            // conjuncts and the declaration already imply restricts nothing -- one beneath, applied by a
            // filter the residual was split from, would be pushed under itself for ever -- and is not pushed.
            List<RexNode>? beneath = null;

            for (var i = 0; i < statement.Conjuncts.Count; i++)
            {
                var conjunct = statement.Conjuncts[i];

                if (statement.IsAdmitted(i))
                {
                    pushable.Add(conjunct);
                    continue;
                }

                // Kept whatever happens: a weakening is a restriction the service can apply, not a
                // replacement for the predicate, so the original is still rechecked above.
                residual.Add(conjunct);

                if (weakens == false)
                    continue;

                // Rendered, and its guards deleted, from what every pushed conjunct establishes: the
                // stratum a conjunct no round admitted is read in, which is never its own facts.
                var parameters = new CosmosParameterList();
                var translator = statement.TranslatorFor(i, parameters);

                if ((arm is { Writes: true } ? statement.WeakeningOf(i).Rex : TryWeakenConjunct(conjunct, translator, rexBuilder, CosmosImplementor.DefaultRootAlias)) is not RexNode weakened)
                    continue;

                beneath ??= AlreadyApplied(filter.getInput()).ToList();

                if (statement.IsApplied(weakened, beneath) == false && translator.TryTranslate(weakened, out var text))
                {
                    pushable.Add(weakened);
                    beneath.Add(weakened);

                    // The rendering check's third site, inert unless a test arms it.
                    if (Metadata.CosmosRenderingCapture.Armed)
                        Metadata.CosmosRenderingCapture.Observer?.Weakened(statement, i, text!, parameters);
                }
            }

            if (arm is { Writes: false, Observer: { } observer } && Shadowed(statement, filter, weakens, observer) is var pushes && projecting)
                observer.Placed(statement, pushable.Count == 0 && residual.Count > 0, pushes == 0 && residual.Count > 0);

            return (pushable, residual);
        }

        /// <summary>
        /// The split S would make of a statement, under each set the shadow asked (sites W and R): what it admitted, and each
        /// residual's weakening built, judged against what lies beneath and written as the translator writes it, told the
        /// shadow; what it pushes under the statement's own set.
        /// </summary>
        static int Shadowed(Metadata.CosmosFilterStatement statement, Filter filter, bool weakens, Metadata.ICosmosRenderingShadowObserver observer)
        {
            var pushes = new List<int>();
            foreach (var set in statement.RenderedSets)
            {
                if (statement.RenderingFor(set) is not { Resolution: var resolution })
                    continue;

                var (pushed, beneath) = (resolution.Forms.Count + Enumerable.Range(0, statement.Conjuncts.Count).Count(i => statement.IsAdmitted(i) && resolution.Forms.ContainsKey(i) == false
                    && resolution.Residual.Contains(i) == false), AlreadyApplied(filter.getInput()).ToList());
                foreach (var i in weakens ? resolution.Residual : [])
                    if (statement.WeakeningOf(i, set) is ({ } form, var rex))
                    {
                        string? text = null;
                        var applied = rex is not null && statement.IsApplied(rex, beneath);
                        if (rex is not null && applied == false && statement.TranslatorFor(i, new CosmosParameterList()).TryTranslate(rex, out text))
                        {
                            beneath.Add(rex);
                            pushed++;
                        }

                        observer.Weakened(statement, i, set, form, text, applied);
                    }

                pushes.Add(pushed);
            }

            return pushes.FirstOrDefault();
        }

        /// <summary>
        /// Assembles a weakened conjunct, injecting only the guards the container has not already
        /// ruled out the need for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A guard exists to admit what a fact would have excluded, so a fact deletes it.</b> The
        /// type test admits a value of some other kind, for the recheck above to decide; a container
        /// that says the path holds that kind has no such value, and the test admits nothing that
        /// exists. The definedness test is there only because of the type test — the disjunction it
        /// guards is <em>true</em> for an absent path, <c>IS_STRING</c> of nothing being false — so
        /// dropping the type test drops the need for it, and a declared presence drops it anyway.
        /// </para>
        /// <para>
        /// What it buys is not a shorter statement. Each guard admits documents the service then
        /// returns for the recheck to throw away, so deleting one is fewer documents over the wire;
        /// and a conjunct that needed none of them is exact, which is what lets a row limit travel
        /// with it.
        /// </para>
        /// </remarks>
        /// <param name="value">The raw document value the comparison reads.</param>
        /// <param name="comparison">The comparison to push, already written against that value.</param>
        /// <param name="typeTest">The service predicate that answers whether the value is of the admitted kind.</param>
        /// <param name="admits">The claims that would make the type test admit nothing that exists; any one of them suffices.</param>
        /// <param name="translator">Resolves the path, and carries what the container knows.</param>
        /// <param name="rexBuilder">Builds the guard nodes.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The weakened conjunct.</returns>
        static RexNode Guarded(
            RexNode value,
            RexNode comparison,
            SqlOperator typeTest,
            IReadOnlyList<JsonClaim> admits,
            CosmosRexTranslator translator,
            RexBuilder rexBuilder,
            string rootAlias)
        {
            var known = Known(value, translator, rootAlias);

            var typed = known is JsonDocumentPath path && admits.Any(claim => translator.Facts.Knows(new JsonFact(path, claim)));
            if (typed)
                return comparison;

            var present = known is JsonDocumentPath held && translator.Facts.Knows(new JsonFact(held, new JsonClaim.Present()));

            var admitted = RexUtil.composeDisjunction(rexBuilder, new java.util.ArrayList
            {
                rexBuilder.makeCall(SqlStdOperatorTable.NOT, rexBuilder.makeCall(typeTest, new[] { value })),
                comparison,
            });

            if (present)
                return admitted;

            return RexUtil.composeConjunction(rexBuilder, new java.util.ArrayList
            {
                rexBuilder.makeCall(CosmosOperators.IsDefined, new[] { value }),
                admitted,
            });
        }

        /// <summary>
        /// Returns the document path a value reads, or <c>null</c> where it reads none this knows
        /// facts about.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="translator">Resolves the path.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The path, or <c>null</c>.</returns>
        static JsonDocumentPath? Known(RexNode value, CosmosRexTranslator translator, string rootAlias)
        {
            if (translator.TryResolvePath(value, out var path) == false || path is null)
                return null;

            return Metadata.CosmosDocumentPaths.From(path, rootAlias);
        }

        /// <summary>
        /// Weakens a comparison over a text accessor to the case where the two agree, or returns
        /// <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>JSON_VALUE</c> read as text renders the value at the path, and Calcite compares that
        /// rendering; the service compares the raw value. The two agree on exactly one kind of
        /// document — one storing a <em>string</em>, whose rendering is itself. Everywhere else the
        /// orders differ in kind: a boolean sorts before a number and a number before any string at
        /// the service, while as text <c>true</c> sorts after <c>bikes</c> and <c>30</c> before it.
        /// </para>
        /// <para>
        /// So the comparison is pushed where the value is a string, and every other document is
        /// admitted for the recheck above:
        /// </para>
        /// <code>
        /// IS_DEFINED(c.label) AND (NOT IS_STRING(c.label) OR c.label &gt; 'bikes')
        /// </code>
        /// <para>
        /// Which is a superset, and that is the whole of what the split rule needs. A document
        /// storing a string is selected by the pushed comparison exactly when Calcite selects it,
        /// because the rendering is the value. Every other document reaches the recheck, where
        /// Calcite decides on the rendering as it would have anyway — including the object and the
        /// array, which the accessor answers null for and which no comparison selects.
        /// </para>
        /// <para>
        /// The <c>IS_DEFINED</c> is the one document the escape hatch need not admit: the accessor
        /// answers SQL null over an absent path, every one of these operators answers null against
        /// null, and Calcite discards the row. It is the guard
        /// <see cref="TryBoundNumericCast"/> carries for the same reason.
        /// </para>
        /// <para>
        /// The accessor may be a field a projection bound to one — a view's column, which is the
        /// spelling every caller through a view writes — and under <c>LIKE</c> it may sit beneath a
        /// case fold: <c>UPPER(label) LIKE '3%'</c> folds the rendering where the service folds the
        /// value, and has the gap for the same reason. Both take the same guard, the fold rebuilt
        /// over the raw value so that the translator renders it as the case-insensitive match it is.
        /// </para>
        /// </remarks>
        static RexNode? TryTextComparisonWeakening(RexNode node, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (node is not RexCall call || call.getOperands().size() != 2)
                return null;

            var kind = (SqlKind.__Enum)call.getKind().ordinal();
            if (kind is not (SqlKind.__Enum.NOT_EQUALS
                or SqlKind.__Enum.LESS_THAN or SqlKind.__Enum.LESS_THAN_OR_EQUAL
                or SqlKind.__Enum.GREATER_THAN or SqlKind.__Enum.GREATER_THAN_OR_EQUAL
                or SqlKind.__Enum.LIKE))
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            // Against a character comparand only, for the reason CosmosRexTranslator.IsCharacter
            // gives: a comparison against a number is the raw one the bound rule builds on purpose.
            RexNode subject;
            RexNode? accessor;

            if (IsCharacter(right) && TryRenderedAccessor(left, translator, throughCaseFold: kind == SqlKind.__Enum.LIKE) is RexNode onLeft)
            {
                subject = left;
                accessor = onLeft;
            }
            else if (kind != SqlKind.__Enum.LIKE && IsCharacter(left) && TryRenderedAccessor(right, translator, throughCaseFold: false) is RexNode onRight)
            {
                subject = right;
                accessor = onRight;
            }
            else
            {
                return null;
            }

            // The path the accessor addresses, which is what the service compares.
            if (translator.TryResolvePath(accessor, out var path) == false || path is null || path.ToString() == rootAlias)
                return null;

            var raw = RenderingRexBuilder.RawValue(rexBuilder, accessor);

            var rawSubject = ReferenceEquals(subject, accessor)
                ? raw
                : rexBuilder.makeCall(((RexCall)subject).getOperator(), raw);

            var comparison = ReferenceEquals(subject, left)
                ? rexBuilder.makeCall(call.getOperator(), rawSubject, right)
                : rexBuilder.makeCall(call.getOperator(), left, rawSubject);

            // The string branch is an order the service computes, and keeps exactly the strings Calcite's does only
            // where the strings on both sides order alike by code point and by UTF-16 code unit: asked as every such
            // order is (CosmosServiceOrder.Compare), of the facts the weakening is decided under. A parameter may hold
            // any character, and a literal under a charset wider than the default may hold one from U+E000; where it
            // is not proved, the comparison stays above whole and only the presence the generic weakening finds is
            // pushed.
            if (kind is not (SqlKind.__Enum.NOT_EQUALS or SqlKind.__Enum.LIKE)
                && translator.CompareOrder(ReferenceEquals(subject, left) ? rawSubject : left, ReferenceEquals(subject, left) ? right : rawSubject, CosmosRexTranslator.IsInclusive(kind)) is { IsProved: false })
                return null;

            return Guarded(
                raw,
                comparison,
                CosmosOperators.IsString,
                new[] { new JsonClaim.OfType(JsonType.String, OrNull: true) },
                translator,
                rexBuilder,
                rootAlias);
        }

        /// <summary>
        /// Returns the rendered accessor a comparison's side reads, or <c>null</c>.
        /// </summary>
        /// <param name="side">The side of the comparison.</param>
        /// <param name="translator">The translator bound to the filter's input, which knows a field bound to an accessor.</param>
        /// <param name="throughCaseFold">Whether to look beneath <c>UPPER</c> or <c>LOWER</c>, which is the <c>LIKE</c> case.</param>
        static RexNode? TryRenderedAccessor(RexNode side, CosmosRexTranslator translator, bool throughCaseFold)
        {
            if (translator.IsTextRendering(side))
                return side;

            if (throughCaseFold && CosmosRexTranslator.TryCaseFoldOperand(side, out _) is RexNode folded && translator.IsTextRendering(folded))
                return folded;

            return null;
        }

        /// <summary>
        /// Determines whether an expression is of a character type.
        /// </summary>
        static bool IsCharacter(RexNode node)
        {
            var name = node.getType()?.getSqlTypeName();
            return name == org.apache.calcite.sql.type.SqlTypeName.VARCHAR || name == org.apache.calcite.sql.type.SqlTypeName.CHAR;
        }

        /// <summary>
        /// Determines whether an expression can tell an absent property from a present one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what decides whether a branch implies the paths it reads are defined. Measured
        /// against a real account: a comparison on an absent property is <em>undefined</em> rather than
        /// false, and <c>NOT undefined</c> is not true either — so neither <c>c.price &gt; 5</c> nor
        /// <c>NOT (c.price &gt; 5)</c> matches a document without a <c>price</c>, and both therefore
        /// imply the path is defined. Polarity does not come into it for a single comparison, which
        /// was the guess and was wrong. It does come into it for a negated <em>conjunction</em>: see
        /// <see cref="CollectPaths"/>, where a sibling that is merely false makes the negation true
        /// and the paths on the other side imply nothing at all.
        /// </para>
        /// <para>
        /// The <c>IS_*</c> family is different: each returns a real boolean for an absent path, so
        /// <c>NOT IS_DEFINED(c.price)</c> is genuinely true there and implies the opposite of what is
        /// wanted. Only <c>IS_DEFINED</c> was measured; the rest are refused with it, because they
        /// answer the same kind of question and a branch using one is rare enough that the caution
        /// costs nothing.
        /// </para>
        /// <para>
        /// SQL's own three-valued tests observe absence too, and they are not in the Cosmos family
        /// because they are not Cosmos functions. <c>x IS NULL</c> is true where the path is missing,
        /// and the <c>IS_TRUE</c> family collapses unknown to a definite boolean, which is the same
        /// thing wearing a different hat. Missing these was a live soundness hole. So was missing a
        /// <c>CASE</c>, whose else arm answers where its condition's path is absent: measured,
        /// <c>CASE WHEN category = 'bikes' THEN INITCAP(name) ELSE 'z' END = 'z'</c> was weakened to
        /// <c>IS_DEFINED(c.category) AND IS_DEFINED(c.name)</c> and lost the document with no
        /// category.
        /// </para>
        /// <para>
        /// The translator asks the same question of the guard it writes, so the answer lives there —
        /// see <see cref="CosmosRexTranslator.ObservesAbsence"/> — and this reads it.
        /// </para>
        /// </remarks>
        static bool ObservesAbsence(RexNode node) => CosmosRexTranslator.ObservesAbsence(node);

        /// <summary>
        /// What <see cref="CollectPaths"/> asks of an expression: the paths it cannot do without where
        /// it is true, where it is false, or where it is anything but null.
        /// </summary>
        enum Reading
        {

            /// <summary>Where it is true: a predicate in its own position.</summary>
            Truth,

            /// <summary>Where it is false: a predicate under a <c>NOT</c>.</summary>
            Falsity,

            /// <summary>Where it is not null: an operand of anything but a connective.</summary>
            Value,

        }

        /// <summary>
        /// The reading of a predicate's operand under a <c>NOT</c>: true where it is false, false where
        /// it is true, and null exactly where it is.
        /// </summary>
        static Reading Negated(Reading reading) => reading switch
        {
            Reading.Truth => Reading.Falsity,
            Reading.Falsity => Reading.Truth,
            _ => Reading.Value,
        };

        /// <summary>
        /// Collects the document paths an expression cannot do without: those that must be defined
        /// wherever it is true, wherever it is false, or, read as a value, wherever it is not null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The largest sub-expression that resolves wins, so <c>c.a.b</c> is collected rather than
        /// <c>c.a</c> — the deeper path being defined is the stronger statement and is the one implied.
        /// The root is skipped: every document is defined, so <c>IS_DEFINED(c)</c> restricts nothing.
        /// </para>
        /// <para>
        /// <b>An operator implies the paths of every operand only where every operand has to answer.</b>
        /// A comparison, arithmetic and most functions answer null for a null, so each needs all of its
        /// operands — but not every function does, and one that answers a value over a null implies
        /// nothing: see <see cref="IsNullWhereAnOperandIs"/>, which says which may be read through. A
        /// conjunction needs all of its conjuncts to be true. A connective whose
        /// answer one operand can settle alone does not: a disjunction is true where one branch is,
        /// whatever the others read, and a conjunction read as a value is false where one conjunct
        /// is. Each of those implies only what every one of its operands implies. This used to
        /// descend into both as though they were strict, and it was a wrong answer each way:
        /// </para>
        /// <list type="bullet">
        /// <item><description>A disjunction <see cref="TryWeakenDisjunction"/> gave up on reached here
        /// whole. <c>price &gt; 100 OR NOT (category = 'bikes' AND INITCAP(name) = 'Sprint')</c>
        /// pushed <c>IS_DEFINED(c.price)</c>, the negated branch contributing nothing, and lost the
        /// shoe with no price for which that branch is true. Calcite writes the shape unasked:
        /// <c>SqlToRelConverter</c> pushes a <c>NOT</c> down through the connectives above an
        /// <c>IN</c>, so <c>NOT ((tags IN (…) OR sku &lt;&gt; 'SPRINT') AND (price - 3 = 5 AND
        /// name || '!' &gt; 'bikes'))</c> arrives as a disjunction of the first operand's negation
        /// and <c>NOT (… AND …)</c>, and pushed <c>IS_DEFINED(c.tags) AND IS_DEFINED(c.metadata.sku)</c>
        /// against a negation true of all seven documents. Ternary logic partitioning found it
        /// twice.</description></item>
        /// <item><description>A compound inside a comparison is read as a value. <c>(category =
        /// 'bikes' AND price &gt; 100) &lt;&gt; (id = '5')</c> pushed <c>IS_DEFINED</c> of all
        /// three paths, and the shoe with no price, whose conjunction is false, was lost.</description></item>
        /// </list>
        /// <para>
        /// <b>Falsity is the third reading, and it is the dual of truth.</b> A conjunction is false
        /// where one conjunct is, so false it needs only what every conjunct needs to be false; a
        /// disjunction is false where every branch is, so false it needs what any branch does. A
        /// <c>NOT</c> swaps the two, and a predicate compared with <c>TRUE</c> or <c>FALSE</c> is that
        /// predicate read for its truth or its falsity rather than as a value. Before, a negated
        /// compound needed nothing and a compared one only what its operands needed as values, which
        /// declined sound restrictions — measured over products, <c>price &gt; 100 OR NOT ((category =
        /// 'shoes' OR INITCAP(sku) = 'B-2') &lt;&gt; FALSE)</c>, whose second branch is true only where
        /// both disjuncts are false, pushed nothing, where the category and the sku are both needed.
        /// Every reading needs at least what the value does, so this only ever adds what is sound.
        /// </para>
        /// <para>
        /// A disjunction's common paths are still implied, so <c>x = 1 OR x &gt; 5</c> under a
        /// connective keeps <c>IS_DEFINED(x)</c>; where a branch implies nothing, nor does the
        /// disjunction.
        /// </para>
        /// <para>
        /// The absence-observing operators — the null and truth tests, <c>CASE</c>, the <c>IS_*</c>
        /// family, a <c>SEARCH</c> that decides null — are not read here at all:
        /// <see cref="TryWeaken"/> asks <see cref="ObservesAbsence"/> of the whole expression first,
        /// and weakens none that has one anywhere.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <param name="reading">Whether it is read for its truth, its falsity or its value.</param>
        /// <param name="translator">Resolves the paths.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The paths, each once, in the order they are first read: each as the path it
        /// resolves to, by which two are the same, and as the first expression read at it.</returns>
        static List<(string Path, RexNode Node)> CollectPaths(RexNode node, Reading reading, CosmosRexTranslator translator, string rootAlias)
        {
            if (translator.TryResolvePath(node, out var path) && path is not null)
            {
                var text = path.ToString();
                return text == rootAlias ? new() : new() { (text, node) };
            }

            if (node is not RexCall call)
                return new();

            var operands = call.getOperands();

            switch (KindOf(call))
            {
                // Every conjunct is true where the conjunction is, and every branch false where the
                // disjunction is.
                case SqlKind.__Enum.AND when reading == Reading.Truth:
                case SqlKind.__Enum.OR when reading == Reading.Falsity:
                    return Union(operands, reading, translator, rootAlias);

                // One operand settles it: a branch true or a conjunct false, and read as a value,
                // either.
                case SqlKind.__Enum.AND:
                case SqlKind.__Enum.OR:
                    return Common(operands, reading, translator, rootAlias);

                // A negation is true where its operand is false, and that is all a negated compound
                // implies. `NOT (a > 1)` implies the path is defined -- an absent property makes the
                // comparison undefined and `NOT undefined` is not true either, which is the measurement
                // above. `NOT (a = 1 AND b > 1)` implies neither path: where `a = 1` is false the
                // conjunction is false whatever `b` does, so the negation is *true* and a document with
                // no `b` belongs in the result. Pushing `IS_DEFINED(b)` deletes it. A conjunction's
                // falsity is what every conjunct's falsity needs, which here is nothing.
                //
                // Measured as a wrong answer rather than reasoned into: the differential oracle caught
                // `NOT (c.category = 'bikes' AND CAST(JSON_VALUE(c."DOC", '$.price') AS DOUBLE) > 50)`
                // returning three rows where Calcite returns five.
                //
                // A disjunction under the negation does need every branch, each having to be false, and
                // its falsity says so. Read as a value the NOT is strict, and its operand is read as one
                // too.
                case SqlKind.__Enum.NOT when operands.size() == 1:
                    return CollectPaths((RexNode)operands.get(0), Negated(reading), translator, rootAlias);

                // A predicate compared with TRUE or FALSE is true where the predicate is, or where it is
                // false, and null exactly where it is null -- so it is read as the predicate, for its
                // truth or its falsity. Read as a value instead it needed only what its operands need as
                // values, which for a compound is what every operand needs, and declined the rest.
                case SqlKind.__Enum.EQUALS or SqlKind.__Enum.NOT_EQUALS when TryComparedWithBoolean(call, out var predicate, out var same):
                    return CollectPaths(predicate, same ? reading : Negated(reading), translator, rootAlias);

                // Anything else needs every operand, each as a value, only where it is null for a null
                // one. One that answers a value over a null -- CONCAT_WS skipping it, an ARRAY holding
                // it -- is true or not null with the path absent, and implies nothing.
                default:
                    return IsNullWhereAnOperandIs(call)
                        ? Union(operands, Reading.Value, translator, rootAlias)
                        : new();
            }
        }

        /// <summary>
        /// Determines whether a call is null wherever one of its operands is null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The premise every path <see cref="CollectPaths"/> finds rests on.</b> An operand's path is
        /// implied only where an absent path makes the operand null and a null operand makes the call
        /// null, all the way up to the conjunct. A comparison, <c>LIKE</c> and arithmetic are like that.
        /// Not every function is, and the walk used to assume they all were: measured against the
        /// emulator over products, <c>CONCAT_WS('-', name, sku) &lt;&gt; 'Road Runner-B-2'</c> pushed
        /// <c>IS_DEFINED(c.name) AND IS_DEFINED(c.metadata.sku)</c> and kept none of the six documents it
        /// is true of, <c>CONCAT_WS</c> skipping the absent sku; and <c>CARDINALITY(ARRAY[INITCAP(sku)])
        /// = 1</c> pushed <c>IS_DEFINED(c.metadata.sku)</c> and kept one of seven, an array holding a null
        /// not being null. <c>ARRAY_CONTAINS(ARRAY[INITCAP(sku), 'x'], 'x')</c> did the same.
        /// </para>
        /// <para>
        /// <b>So the walk descends only where this says it may, and a call it does not know implies
        /// nothing.</b> A list of what is safe rather than of what is not, because the unknown function
        /// is the one a library adds next, and declining a weakening loses no rows. Calcite's own answer
        /// comes first — <see cref="Strong.Policy.ANY"/>, which the comparisons, <c>LIKE</c>, the
        /// arithmetic, the cast, <c>NOT</c>, <c>ITEM</c>, <c>TRIM</c>, <c>FLOOR</c>, <c>CEIL</c> and
        /// <c>EXTRACT</c> carry. An ordinary function reports <see cref="Strong.Policy.AS_IS"/>, which
        /// says nothing either way, so the functions named below are the ones Calcite was measured to
        /// answer null for over a path every document lacks, each read in process against the emulator.
        /// A <c>SEARCH</c> that answers unknown over a null is the comparisons it stands for; one that
        /// decides null observes absence and never reaches the walk. A plain accessor that addresses no
        /// path of its own is null where the value it parses is, so <c>JSON_VALUE(JSON_VALUE(DOC,
        /// '$.name'), '$')</c> needs the name it reads.
        /// </para>
        /// <para>
        /// <see cref="CosmosSelfJoinRule"/> asks a stronger question of a key, null <em>exactly</em> where
        /// an operand is, because a key that could be null where its inputs are not falls outside the
        /// constraint it is proved under. This one is only the half a weakening needs, which admits an
        /// accessor and a cast that is null for more than a null.
        /// </para>
        /// </remarks>
        static bool IsNullWhereAnOperandIs(RexCall call)
        {
            if (Strong.policy(call) == Strong.Policy.ANY)
                return true;

            if (KindOf(call) == SqlKind.__Enum.SEARCH)
                return call.getOperands().size() == 2
                    && call.getOperands().get(1) is RexLiteral literal
                    && literal.getValue() is org.apache.calcite.util.Sarg sarg
                    && sarg.nullAs == RexUnknownAs.UNKNOWN;

            if (CosmosRexTranslator.IsPlainJsonValue(call) || CosmosRexTranslator.IsPlainJsonQuery(call))
                return true;

            return call.getOperator().getName() switch
            {
                "||" or "UPPER" or "LOWER" or "INITCAP" or "SUBSTRING" or "CHAR_LENGTH" or "CHARACTER_LENGTH" or
                "POSITION" or "REPLACE" or "LEFT" or "RIGHT" or "REVERSE" or "REPEAT" or
                "MOD" or "ABS" or "ROUND" or "POWER" or "SAFE_CAST" => true,
                _ => false,
            };
        }

        /// <summary>
        /// The paths any of the operands needs, which is what an operator needing all of them needs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A path is told apart by the path it resolves to, never by the expression that reads it.</b>
        /// One path has several spellings — the promoted column <c>c."$.category"</c> and
        /// <c>JSON_VALUE(c."DOC", '$.category')</c>, a typed accessor and a text one — and each is a
        /// different expression with the same thing to say about the document. Told apart by the
        /// expression, a path read two ways was implied twice here, and found in neither branch by
        /// <see cref="Common"/>, which then declined a sound restriction. Either spelling renders the
        /// same <c>IS_DEFINED</c>, and the first read is the one kept.
        /// </para>
        /// </remarks>
        static List<(string Path, RexNode Node)> Union(java.util.List operands, Reading reading, CosmosRexTranslator translator, string rootAlias)
        {
            var paths = new List<(string Path, RexNode Node)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < operands.size(); i++)
                foreach (var path in CollectPaths((RexNode)operands.get(i), reading, translator, rootAlias))
                    if (seen.Add(path.Path))
                        paths.Add(path);

            return paths;
        }

        /// <summary>
        /// The paths every one of the operands needs, which is what an operator any one of them can
        /// settle needs.
        /// </summary>
        /// <remarks>
        /// By the path, as <see cref="Union"/> says: <c>c."$.category" = 'bikes' OR
        /// JSON_VALUE(c."DOC", '$.category') = 'shoes'</c> needs the category in both branches, and
        /// matched by expression it needed nothing.
        /// </remarks>
        static List<(string Path, RexNode Node)> Common(java.util.List operands, Reading reading, CosmosRexTranslator translator, string rootAlias)
        {
            if (operands.size() == 0)
                return new();

            var common = CollectPaths((RexNode)operands.get(0), reading, translator, rootAlias);

            for (var i = 1; i < operands.size() && common.Count > 0; i++)
            {
                var needed = new HashSet<string>(CollectPaths((RexNode)operands.get(i), reading, translator, rootAlias).Select(p => p.Path), StringComparer.Ordinal);
                common = common.Where(p => needed.Contains(p.Path)).ToList();
            }

            return common;
        }

        /// <summary>
        /// Determines whether a comparison is a predicate compared with <c>TRUE</c> or <c>FALSE</c>,
        /// and whether it is true where that predicate is or where it is false.
        /// </summary>
        /// <param name="call">An <c>=</c> or a <c>&lt;&gt;</c>.</param>
        /// <param name="predicate">The side that is not the literal.</param>
        /// <param name="same">Whether the comparison is true where the predicate is: <c>= TRUE</c> and
        /// <c>&lt;&gt; FALSE</c>.</param>
        static bool TryComparedWithBoolean(RexCall call, out RexNode predicate, out bool same)
        {
            predicate = null!;
            same = false;

            if (call.getOperands().size() != 2)
                return false;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            java.lang.Boolean? literal;
            if (right is RexLiteral r && r.getValue() is java.lang.Boolean onRight && left is not RexLiteral)
            {
                literal = onRight;
                predicate = left;
            }
            else if (left is RexLiteral l && l.getValue() is java.lang.Boolean onLeft && right is not RexLiteral)
            {
                literal = onLeft;
                predicate = right;
            }
            else
            {
                return false;
            }

            same = (KindOf(call) == SqlKind.__Enum.EQUALS) == literal.booleanValue();
            return true;
        }

        /// <summary>
        /// The kind of a call, by name, for the reason the translator dispatches on names.
        /// </summary>
        static SqlKind.__Enum KindOf(RexCall call)
        {
            return (SqlKind.__Enum)call.getKind().ordinal();
        }

        /// <summary>
        /// Returns something <paramref name="node"/> implies and Cosmos can evaluate, or <c>null</c>.
        /// </summary>
        static RexNode? TryWeaken(RexNode node, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (translator.TryTranslate(node, out _))
                return node;

            if (ObservesAbsence(node))
                return null;

            if (TryBoundNumericCast(node, translator, rexBuilder, rootAlias) is RexNode bounded)
                return bounded;

            if (TryTextEqualityAlternatives(node, translator, rexBuilder, rootAlias) is RexNode alternatives)
                return alternatives;

            // A guarded comparison the facts do not let render is no weakening, and the presence the comparison implies
            // still is: a case fold under LIKE over a string nothing proves ASCII is refused with the fold
            // (CosmosStringFunctions), where it used to push the case-insensitive match and lose the rows it folds
            // otherwise than Calcite.
            if (TryTextComparisonWeakening(node, translator, rexBuilder, rootAlias) is RexNode weakened && translator.TryTranslate(weakened, out _))
                return weakened;

            var paths = CollectPaths(node, Reading.Truth, translator, rootAlias);

            if (paths.Count == 0)
                return null;

            var terms = new java.util.ArrayList();
            foreach (var (_, path) in paths)
                terms.add(rexBuilder.makeCall(CosmosOperators.IsDefined, new[] { path }));

            return RexUtil.composeConjunction(rexBuilder, terms);
        }

        /// <summary>
        /// Bounds the raw value a comparison through a numeric cast reads, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>CAST(JSON_VALUE(c."DOC", '$.price') AS INTEGER) &gt; 10</c> has no Cosmos form — Calcite converts the
        /// stored value and the service compares it as it stands, so the two select different documents
        /// — and it is therefore declined and the container read whole. It still <em>implies</em>
        /// something the service can apply, and that is what is pushed:
        /// </para>
        /// <code>
        /// IS_DEFINED(c.price) AND (NOT IS_NUMBER(c.price) OR c.price &gt; 9)
        /// </code>
        /// <para>
        /// The window, and why each side holds, is <see cref="Metadata.CosmosNumericWindow"/>'s, which the
        /// fact extractor reads too. <c>JSON_VALUE(… RETURNING DECIMAL)</c> takes it as well, being the same
        /// conversion written as a clause — see <see cref="CosmosRexTranslator.IsParsingJsonValue"/>.
        /// </para>
        /// <para>
        /// <b>Why the type test is inverted.</b> Calcite converts a stored <em>string</em> too —
        /// measured, <c>= 30</c> matches a document storing <c>"30"</c> — so a filter that kept only
        /// numbers would lose it. Anything that is not a number passes the test untouched and is
        /// rechecked above, where Calcite decides it. In a container whose field really is numeric,
        /// which is the case a typed view describes, that branch matches nothing and the service does
        /// all the work.
        /// </para>
        /// <para>
        /// <c>IS_DEFINED</c> is implied separately: a comparison against an absent property is unknown
        /// in SQL, so no document without one is ever kept.
        /// </para>
        /// </remarks>
        static RexNode? TryBoundNumericCast(RexNode node, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (Metadata.CosmosNumericWindow.TryRead(node, rexBuilder, out var window) == false)
                return null;

            var value = window.Value;

            // Rooted at the container rather than at an array-traversal alias, and addressable at all.
            if (translator.TryResolvePath(value, out var path) == false || path is null ||
                string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            var terms = new java.util.ArrayList();

            if (window.Above is java.math.BigDecimal above)
                terms.add(rexBuilder.makeCall(SqlStdOperatorTable.GREATER_THAN, value, rexBuilder.makeExactLiteral(above)));

            if (window.Below is java.math.BigDecimal below)
                terms.add(rexBuilder.makeCall(SqlStdOperatorTable.LESS_THAN, value, rexBuilder.makeExactLiteral(below)));

            // Either numeric claim serves: the service's IS_NUMBER is true of both, JSON drawing no
            // line between them and `integer` being a number with no fractional part rather than a
            // type of its own.
            return Guarded(
                value,
                RexUtil.composeConjunction(rexBuilder, terms),
                CosmosOperators.IsNumber,
                new[]
                {
                    new JsonClaim.OfType(JsonType.Number, OrNull: true),
                    new JsonClaim.OfType(JsonType.Integer, OrNull: true),
                },
                translator,
                rexBuilder,
                rootAlias);
        }

        /// <summary>
        /// Pushes what an equality against text a stored value could render as implies, or returns
        /// <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>JSON_VALUE(c."DOC", '$.x') = '30'</c> and <c>CAST(c."$.x" AS VARCHAR) = '30'</c> are
        /// declined as translations — Calcite renders the stored number 30 as <c>30</c> and keeps the
        /// document, where <c>c.x = '30'</c> at the service does not. Each still implies something
        /// tighter: the value is that string, or it is a value that renders as it. So the rule pushes
        /// </para>
        /// <code>
        /// c.x = '30' OR c.x = 30
        /// </code>
        /// <para>
        /// and the comparison itself stays above, where Calcite makes it.
        /// </para>
        /// <para>
        /// <b>Why it is implied, and why it is not exact.</b> A stored string matches exactly when it is
        /// the literal. A stored number matches when Java's rendering of it is the literal, and every
        /// such number has the literal's numeric value — so <c>c.x = 30</c> keeps all of them. It also
        /// keeps a stored 30.0, which Calcite renders as <c>30.0</c> and does not match: the two are one
        /// double at the service and two spellings in the JSON text Calcite reads. That is the whole of
        /// the looseness, and the recheck above is what pays for it. A boolean renders as Calcite's
        /// lowercase <c>true</c> or <c>false</c>, so exactly that text takes the boolean beside the
        /// string; any other casing is the string alone. A number the double cannot hold takes
        /// <c>IS_NUMBER</c> instead of a value.
        /// </para>
        /// <para>
        /// <b>Past 2^53 a whole number is sent as the whole number it is</b>, as the translator sends a
        /// literal of one (<see cref="CosmosRexTranslator.TryGetExactNumberValue"/>). The service keeps a
        /// stored integer exactly, and Java renders it as its digits, so <c>'9007199254740993'</c> is the
        /// rendering of the integer 9007199254740993 — which the double the text rounds to, 2^53, is not
        /// equal to at the service. Measured on the emulator over the declared container, the double
        /// lost the document storing it.
        /// </para>
        /// <para>
        /// <b>Which values render at all depends on the spelling.</b> The cast over <c>ANY</c> renders an
        /// array as <c>[x, y]</c> and an object as <c>{x=1}</c>, so a literal opening with that bracket
        /// takes <c>IS_ARRAY</c> or <c>IS_OBJECT</c> beside the string. <c>JSON_VALUE</c> answers null
        /// for both and for a JSON null, so under it only a number and a boolean ever join the string —
        /// and where nothing does, the translator pushes the equality exactly and this is never reached.
        /// </para>
        /// <para>
        /// The branches are written against the value with its type discarded, so that the translator
        /// renders each as the path and does not apply the test it applies to the conjunct: what is
        /// pushed here is never evaluated by Calcite, only sent. It is still simplified by Calcite
        /// wherever a host reduces expressions, so the number and the boolean read the value in their
        /// own type rather than beside the string in one — see <see cref="EqualsOtherThanText"/>.
        /// </para>
        /// </remarks>
        static RexNode? TryTextEqualityAlternatives(RexNode node, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (node is not RexCall call || (SqlKind.__Enum)call.getKind().ordinal() != SqlKind.__Enum.EQUALS || call.getOperands().size() != 2)
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            bool scalarOnly;
            RexNode other;

            if (CosmosRexTranslator.TryRenderedTextValue(left, out scalarOnly) is RexNode value)
            {
                other = right;
            }
            else if (CosmosRexTranslator.TryRenderedTextValue(right, out scalarOnly) is RexNode mirrored)
            {
                value = mirrored;
                other = left;
            }
            // A field a projection bound to the accessor, which renders what the accessor renders.
            else if (translator.IsTextRendering(left))
            {
                value = left;
                scalarOnly = true;
                other = right;
            }
            else if (translator.IsTextRendering(right))
            {
                value = right;
                scalarOnly = true;
                other = left;
            }
            else
            {
                return null;
            }

            if (other is not RexLiteral literal)
                return null;

            string text;
            try
            {
                if (CosmosRexTranslator.GetLiteralValue(literal) is not string s)
                    return null;

                text = s;
            }
            catch (CosmosTranslationException)
            {
                return null;
            }

            // Rooted at the container rather than at an array-traversal alias, and addressable at all.
            if (translator.TryResolvePath(value, out var path) == false || path is null ||
                string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            // The accessor without the type that says it is text. The translator would otherwise hold
            // the string branch to the test the conjunct failed, and rightly: as a translation it is
            // not exact. As a branch of what the conjunct implies it is, and this is that.
            if (translator.IsTextRendering(value))
                value = RenderingRexBuilder.RawValue(rexBuilder, value);

            var branches = new java.util.ArrayList();
            branches.add(rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, value, literal));

            if (CosmosRexTranslator.TryParseRenderedNumber(text, out var number) && CosmosTranslatorFaults.Has(CosmosTranslatorFault.M11) == false)
                branches.add(double.IsFinite(number)
                    ? EqualsOtherThanText(rexBuilder, value, NumberRenderedAs(rexBuilder, text, number))
                    : rexBuilder.makeCall(CosmosOperators.IsNumber, new[] { value }));

            if (text is "true" or "false")
                branches.add(EqualsOtherThanText(rexBuilder, value, rexBuilder.makeLiteral(text == "true")));

            if (scalarOnly == false && text.Length > 0 && text[0] == '[')
                branches.add(rexBuilder.makeCall(CosmosOperators.IsArray, new[] { value }));

            if (scalarOnly == false && text.Length > 0 && text[0] == '{')
                branches.add(rexBuilder.makeCall(CosmosOperators.IsObject, new[] { value }));

            return RexUtil.composeDisjunction(rexBuilder, branches);
        }

        /// <summary>
        /// Returns the number a stored value renders as text as: the whole number it spells where a
        /// double cannot hold it, as a <c>BIGINT</c>, and the double otherwise.
        /// </summary>
        static RexLiteral NumberRenderedAs(RexBuilder rexBuilder, string text, double number)
        {
            var exact = new java.math.BigDecimal(text);

            return CosmosRexTranslator.TryGetExactNumberValue(exact) is long whole
                ? (RexLiteral)rexBuilder.makeExactLiteral(java.math.BigDecimal.valueOf(whole), rexBuilder.getTypeFactory().createSqlType(org.apache.calcite.sql.type.SqlTypeName.BIGINT))
                : (RexLiteral)rexBuilder.makeApproxLiteral(new java.math.BigDecimal(number));
        }

        /// <summary>
        /// Writes an alternative that compares the raw value with a number or a boolean, as a
        /// comparison Calcite's own rules can read beside the one with the string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One expression, one kind of literal.</b> Calcite's simplifier takes every literal an
        /// expression is compared with in one condition to be of one class: it gathers the comparisons
        /// on an expression into one range set, and it reads each branch of a disjunction under the
        /// negation of the branches before it, and both compare the literals with each other. The
        /// alternatives written over one raw value compared a string with a double, or with a boolean,
        /// and the comparison threw — so <c>JSON_VALUE(DOC, '$.label') = '30'</c> and <c>= 'true'</c>
        /// failed to plan wherever <c>FILTER_REDUCE_EXPRESSIONS</c> is registered, which a connection
        /// does by default: Calcite's <c>RelOptUtil.registerDefaultRules</c> adds it last. The bare
        /// planner this adapter's tests mostly use never simplified the condition, so nothing saw it.
        /// </para>
        /// <para>
        /// <b>So the value is read in the type the comparison needs.</b> An accessor is re-typed to the
        /// literal's type, which is the accessor with a <c>RETURNING</c> of that type: another
        /// expression to the simplifier, and to the translator the typed read of the path, which a
        /// positive equality writes as the path — <c>c.label = 30</c>, as before. Under a host that
        /// reduces, the boolean's equality folds to the read itself and is written <c>c.label</c> or
        /// <c>NOT c.label</c>, which the service holds true of that boolean and of nothing else.
        /// </para>
        /// <para>
        /// A field has no call to re-type — a view's column bound to the accessor, or a document-value
        /// column under a cast to text — and a cast of one to a number is the conversion the translator
        /// refuses, rightly. Its literal is boxed as <c>VARIANT</c> instead, the box Calcite's own
        /// coercion puts around a literal compared with a raw value: the translator writes it as the
        /// literal, and the simplifier does not read it as one. The box costs what Calcite charges for
        /// every comparison with a <c>VARIANT</c>: the simplifier hands the cast to the executor, which
        /// compiles it, each time it simplifies the filter — nine compilations and between one and a
        /// half and three seconds of planning for <c>CAST(c."$.category" AS VARCHAR) = '30'</c>,
        /// measured through a connection. The accessor is the spelling nearly every caller writes and
        /// costs nothing, which is why the two are not one.
        /// </para>
        /// </remarks>
        /// <param name="rexBuilder">Builds the comparison.</param>
        /// <param name="value">The raw value: an accessor typed <c>ANY</c>, or a field.</param>
        /// <param name="literal">A number or a boolean.</param>
        static RexNode EqualsOtherThanText(RexBuilder rexBuilder, RexNode value, RexLiteral literal)
        {
            var types = rexBuilder.getTypeFactory();

            if (value is RexCall accessor && accessor.getOperator().getName() == "JSON_VALUE")
                return rexBuilder.makeCall(
                    SqlStdOperatorTable.EQUALS,
                    rexBuilder.makeCall(types.createTypeWithNullability(literal.getType(), true), accessor.getOperator(), accessor.getOperands()),
                    literal);

            var variant = types.createSqlType(org.apache.calcite.sql.type.SqlTypeName.VARIANT);
            return rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, value, rexBuilder.makeAbstractCast(variant, literal, false));
        }

        /// <summary>
        /// Returns something a conjunct implies and Cosmos can evaluate, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Only disjunctions were weakened before, which left the commonest untranslatable shape of all
        /// — a single comparison the service has no form for — pushing nothing whatever. A conjunct is
        /// positive by construction, which is the whole of the polarity argument, so the same weakening
        /// applies to it directly.
        /// <para>
        /// A weakening identical to the conjunct is no weakening; it is refused rather than pushed,
        /// which is what the disjunction form has always done for the same reason.
        /// </para>
        /// </remarks>
        static RexNode? TryWeakenConjunct(RexNode conjunct, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (TryWeakenDisjunction(conjunct, translator, rexBuilder, rootAlias) is RexNode disjunctive)
                return disjunctive;

            if (TryWeaken(conjunct, translator, rexBuilder, rootAlias) is not RexNode weakened)
                return null;

            return weakened.toString() == conjunct.toString() ? null : weakened;
        }

        /// <summary>
        /// Weakens a disjunction into something the service can apply, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Dropping a disjunct strengthens, so an <c>OR</c> is pushable only when every branch is. It
        /// is still pushable when every branch can be <em>weakened</em>: each branch implies its own
        /// weakening, so the disjunction implies the disjunction of them, and a restriction the plan
        /// implies discards nothing the plan would have kept.
        /// </para>
        /// <para>
        /// One branch with no weakening abandons the whole thing, since a disjunction is only as
        /// restrictive as its loosest branch.
        /// </para>
        /// </remarks>
        static RexNode? TryWeakenDisjunction(RexNode conjunct, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            var branches = org.apache.calcite.plan.RelOptUtil.disjunctions(conjunct);
            if (branches.size() < 2)
                return null;

            var weakened = new java.util.ArrayList();

            for (var i = 0; i < branches.size(); i++)
            {
                if (TryWeaken((RexNode)branches.get(i), translator, rexBuilder, rootAlias) is not RexNode branch)
                    return null;

                weakened.add(branch);
            }

            var result = RexUtil.composeDisjunction(rexBuilder, weakened);

            // Nothing gained where every branch already rendered, since the conjunct was pushable then.
            return result.toString() == conjunct.toString() ? null : result;
        }

        /// <summary>
        /// Returns every conjunct any equivalent form of a subtree already applies, once each.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A weakening is derived from a conjunct that stays in the residual, and the residual is what
        /// the rule matches on next time round — so the weakening is asked whether these already apply
        /// it (<see cref="Metadata.CosmosFilterStatement.IsApplied"/>), or the same weakening would be
        /// pushed under itself for ever: measured, as a stack overflow with TryBindOutput recursing
        /// through the filters it had built. A weakening of more than one term lands as several
        /// conjuncts of the filter beneath, and is applied where all of them are.
        /// </para>
        /// <para>
        /// Every equivalent form, not the one the set happens to name: a Volcano input is a set of
        /// equivalent expressions, and which of them <c>getOriginal</c> or <c>getBest</c> returns
        /// depends on how far the planner has got. Reading the whole set is stable, because once a
        /// weakening has been pushed the filter carrying it stays in the set.
        /// </para>
        /// </remarks>
        static IReadOnlyList<RexNode> AlreadyApplied(RelNode? node)
        {
            // Once each by Calcite's equality, which compares types where the digest prints none.
            var applied = new List<RexNode>();
            var once = new HashSet<RexNode>();
            var seen = Rel.CosmosPlanMembers.NewSeen();

            // Through a stack of filters as well as across a set's members: a filter keeps its input's
            // row type, so a conjunct applied anywhere down the stack is applied to this relation in
            // the same ordinals. Read from the top filter alone, a weakening pushed under a filter the
            // residual was later split from was pushed again above it — and a redundant filter, which
            // halves the estimated rows, was then the plan chosen (#210).
            void Collect(RelNode? candidate)
            {
                if (candidate is not Filter filter || seen.Add(filter) == false)
                    return;

                foreach (var conjunct in Metadata.CosmosFilterStatement.ConjunctsOf(filter.getCondition()))
                    if (once.Add(conjunct))
                        applied.Add(conjunct);

                foreach (var member in Rel.CosmosPlanMembers.Of(filter.getInput()))
                    Collect(member);
            }

            // Every expression the node stands for, which this did by hand before CosmosPlanMembers
            // existed and is the pattern that class was named after: a conjunct applied in any member
            // is applied to the relation, and missing one re-pushes a weakening under itself.
            foreach (var member in Rel.CosmosPlanMembers.Of(node))
                Collect(member);

            return applied;
        }

        /// <summary>
        /// Determines whether any equivalent form of a subtree is a projection.
        /// </summary>
        static bool OverProjection(RelNode? node)
        {
            foreach (var member in Rel.CosmosPlanMembers.Of(node))
                if (member is Project)
                    return true;

            return false;
        }

        static RexNode Compose(RexBuilder rexBuilder, List<RexNode> conjuncts)
        {
            var list = new java.util.ArrayList();
            foreach (var conjunct in conjuncts)
                list.add(conjunct);

            return RexUtil.composeConjunction(rexBuilder, list);
        }

    }

}
