using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Reads a join of a container to itself on one of its keys as one read of the container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape it answers.</b> A federation presents one container as several views, each a
    /// projection of the same documents narrowed by a discriminator, and a table-per-type mapping joins
    /// them back together on the key they share. Every view reads the same document by the same key, and
    /// each read brings the document whole — so a query over five such views read the container five times
    /// and hash-joined the copies (#177).
    /// </para>
    /// <code>
    /// Join(l.k = r.k)                          Project(P, CASE WHEN M THEN Q END)
    ///   Project(P, Filter(F, Scan(c)))    →      Filter(F, Scan(c))
    ///   Project(Q, Filter(G, Scan(c)))
    /// </code>
    /// <para>
    /// <b>Why it is sound.</b> Each input yields at most one row per document, each row a function of its
    /// document. Where the join equates the paths of a <c>UNIQUE</c> constraint, any pair it makes is one document
    /// paired with itself — and the join becomes a function of one document: <c>M</c>, the other side's
    /// filter and the join condition both evaluated over it, decides whether it pairs. An inner join keeps
    /// the document where <c>F ∧ M</c>; a left join keeps every document <c>F</c> admits and gives the
    /// right side's columns only where <c>M</c> holds. The key equality becomes <c>k IS NOT NULL</c> by
    /// that substitution, which is the join's own null semantics: a document with no key pairs with
    /// nothing.
    /// </para>
    /// <para>
    /// <b>Where uniqueness comes from, and why it is asked of the container rather than of each input.</b>
    /// <see cref="CosmosContainerMetadata.Constraints"/> answers it, each compiled by
    /// <see cref="CosmosConstraintCompiler"/>: <c>UNIQUE (pk…, id)</c> from the service,
    /// <c>UNIQUE (pk…, paths…)</c> from a unique key policy, or a constraint the model declared — one scoped by
    /// a predicate only where <em>both</em> sides' filters imply it. Asking Calcite whether the key is unique <em>on each input</em> is the wrong question: with a park
    /// link on the left and a map link on the right, a guid unique among the map links still lets one of each
    /// share it, the join pairs them, and one read of each document would not.
    /// </para>
    /// <para>
    /// <b>And the expression has to read the key faithfully.</b> A unique stored value is not a unique join
    /// key if two stored values read alike: <c>JSON_VALUE</c> renders the number <c>1</c> and the string
    /// <c>"1"</c> as the same text, and a cast to <c>UUID</c> reads <c>ABC…</c> and <c>abc…</c> as the same
    /// value. So a text accessor counts where the container's facts give the path one scalar type, and a
    /// <c>UUID</c> cast where they give it a canonical UUID form — the fact that makes the cast push.
    /// </para>
    /// <para>
    /// <b>Why it is not Calcite's.</b> <c>ProjectJoinRemoveRule</c> drops a join only where no column of
    /// one side is used, and <c>LoptMultiJoin.RemovableSelfJoin</c> needs the key to be a column of the
    /// table. Here the key is an expression over the document column, which has no ordinal for a unique key
    /// to name, and both sides contribute columns.
    /// </para>
    /// <para>
    /// <b>It closes over itself</b>: what it produces is again a projection over a filter over the scan, so
    /// the join above a merged one merges too, and a chain of five views becomes one read. A side that is
    /// a merged left join carries its key as <c>CASE WHEN M THEN k END</c>; that is either <c>k</c> or null,
    /// so equating it equates <c>k</c> wherever it pairs anything, and the key is read through it.
    /// </para>
    /// <para>
    /// <b>A side may be sorted, and paged, where the join keeps it (#192).</b> A left join keeps every row
    /// of its left side, and the key join gives each at most one partner, so the page maps one to one onto
    /// the join's rows and can be taken after it: <c>Join(LEFT, Sort(A), B)</c> is
    /// <c>Sort(Join(LEFT, A, B))</c>, and the join beneath merges. The mirror holds for a right join with
    /// the sort on its right.
    /// </para>
    /// <para>
    /// <b>It fires before anything else does, and the join it replaces is explored no further (#201).</b>
    /// A <see cref="org.apache.calcite.rel.rules.SubstitutionRule"/>: Volcano queues its matches ahead of
    /// every other rule's, and once it has fired, prunes the join, so no rule fires on that join again. As an
    /// ordinary rule it waited behind everything the joins matched — filters pushed into them, pushed
    /// back out, commuted — and each of those spellings was a set of its own that the merge then fired on
    /// again: 8,250 expressions and three minutes to plan a five-view hierarchy, for a result one read
    /// answers. Pruning is sound because what replaces the join is always as good: one read of the
    /// container in place of several, with nothing the join could do that the read cannot.
    /// </para>
    /// <para>
    /// <b>Which is also why it matches no scan.</b> A substitution rule is a transformation rule, and Volcano
    /// matches a transformation rule's operands only within one convention — a child in another is skipped.
    /// The scan is in the container's convention and the filter above it is not, so an operand naming the
    /// scan never matched. Each side is matched as a projection, or a projection over a projection, and read
    /// down to its scan by <see cref="Read"/>, which also sees through the projections a subquery or an
    /// earlier merge stacks there — without waiting for Calcite's merges to remove them first.
    /// </para>
    /// <para>
    /// <b>What it declines.</b> A full join: a document holding no key would be one row merged and two
    /// joined. A semi or anti join, which the same substitution answers and nothing has asked for yet. A
    /// sorted side under an inner join, which can drop rows of the page, or on the side an outer join
    /// does not keep. A join carrying correlation variables, and any side whose expressions are not
    /// deterministic.
    /// </para>
    /// </remarks>
    public class CosmosSelfJoinRule : RelOptRule, org.apache.calcite.rel.rules.SubstitutionRule
    {

        /// <inheritdoc />
        public bool autoPruneOld() => true;

        /// <summary>
        /// The rule's instances: one for each combination of a projection and a projection over a projection on
        /// each side, and of which side, if either, is sorted.
        /// </summary>
        /// <remarks>
        /// Static, because the rule takes no convention — it reads the container from the scans — and so
        /// registering it once per convention must register it once.
        /// </remarks>
        static readonly RelOptRule[] Instances = CreateInstances();

        static RelOptRule[] CreateInstances()
        {
            var instances = new List<RelOptRule>();
            foreach (var sorted in new[] { Paged.None, Paged.Left, Paged.Right })
                foreach (var leftStacked in new[] { true, false })
                    foreach (var rightStacked in new[] { true, false })
                        instances.Add(new CosmosSelfJoinRule(leftStacked, rightStacked, sorted));

            return instances.ToArray();
        }

        /// <summary>
        /// Which side of the join, if either, is sorted — and usually paged — between the join and its
        /// projection.
        /// </summary>
        public enum Paged
        {

            /// <summary>Neither side.</summary>
            None,

            /// <summary>The left side, which a left join preserves.</summary>
            Left,

            /// <summary>The right side, which a right join preserves.</summary>
            Right,

        }

        /// <summary>
        /// Returns the rule's instances.
        /// </summary>
        /// <returns>The rules.</returns>
        public static IEnumerable<RelOptRule> Create() => Instances;

        readonly bool _leftStacked;
        readonly bool _rightStacked;
        readonly Paged _sorted;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="leftStacked">Whether the left side is matched as a projection over a projection.</param>
        /// <param name="rightStacked">Whether the right side is.</param>
        /// <param name="sorted">Which side, if either, has a sort above its projection.</param>
        // As CosmosPointReadSplitRule: RelOptRule's operand builders are deprecated in favour of
        // RelRule.Config, whose operand supplier costs more ceremony from C# than it buys.
#pragma warning disable CS0612
        public CosmosSelfJoinRule(bool leftStacked, bool rightStacked, Paged sorted = Paged.None) :
            base(
                operand((java.lang.Class)typeof(LogicalJoin), Side(leftStacked, sorted == Paged.Left), Side(rightStacked, sorted == Paged.Right)),
                $"CosmosSelfJoinRule({Describe(leftStacked, sorted == Paged.Left)},{Describe(rightStacked, sorted == Paged.Right)})")
        {
            _leftStacked = leftStacked;
            _rightStacked = rightStacked;
            _sorted = sorted;
        }

        static RelOptRuleOperand Side(bool stacked, bool sorted)
        {
            var side = stacked
                ? operand((java.lang.Class)typeof(LogicalProject), operand((java.lang.Class)typeof(LogicalProject), any()))
                : operand((java.lang.Class)typeof(LogicalProject), any());

            return sorted ? operand((java.lang.Class)typeof(LogicalSort), side) : side;
        }
#pragma warning restore CS0612

        static string Describe(bool stacked, bool sorted) =>
            (sorted ? "Sort:" : "") + (stacked ? "Project:Project" : "Project");

        /// <summary>
        /// One input of the join, read down to the scan: its columns and the condition it keeps a document
        /// on, both over the scan's row, under an optional sort.
        /// </summary>
        readonly record struct Input(Sort? Sort, IReadOnlyList<RexNode> Projects, RexNode? Condition, TableScan Scan);

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var join = (Join)call.rel(0);
            var at = 1;

            Input? Next(bool stacked, bool sorted)
            {
                var sort = sorted ? (Sort)call.rel(at++) : null;
                var project = (Project)call.rel(at++);
                if (stacked)
                    at++;

                return Read(sort, project);
            }

            if (Next(_leftStacked, _sorted == Paged.Left) is not Input left || Next(_rightStacked, _sorted == Paged.Right) is not Input right)
                return;

            if (TryMerge(join, left, right) is RelNode merged)
                call.transformTo(merged);
        }

        /// <summary>
        /// Reads a side down to the scan beneath it, through any projections and filters in between.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A view is a projection over a filter over the scan, but what sits under the join is not always
        /// the view alone: a subquery projects the view's columns again, and a merged join is a projection
        /// the query's own projection then stands over. Each projection is substituted into the one above
        /// it and each filter's condition carried down beside it, so what is read is the side's columns and
        /// condition over the scan's row — the shape the merge needs, without waiting for Calcite's
        /// transposes and merges to produce it. Waiting was the expensive part: the merge is what makes the
        /// rest of the plan small, and every rule that fired before it explored the joins it removes (#201).
        /// </para>
        /// <para>
        /// The inputs are equivalence sets, and any member of one means what the others do, so the walk
        /// takes the first member of each kind it needs and prefers the scan. Logical nodes only: a node in
        /// another convention is an implementation of the same rows, and nothing here needs one.
        /// </para>
        /// </remarks>
        static Input? Read(Sort? sort, Project project)
        {
            var projects = new List<RexNode>(Projections(project));
            var conditions = new List<RexNode>();
            var node = project.getInput();

            // Bounded, because a registered plan is a graph: a set can reach itself through a projection
            // that renames nothing.
            for (var depth = 0; depth < 8; depth++)
            {
                var members = CosmosPlanMembers.Of(node);

                if (First<TableScan>(members) is TableScan scan)
                    return new Input(sort, projects, And(project.getCluster().getRexBuilder(), conditions.ToArray()), scan);

                if (First<LogicalFilter>(members) is Filter filter)
                {
                    conditions.Add(filter.getCondition());
                    node = filter.getInput();
                    continue;
                }

                if (First<LogicalProject>(members) is Project below)
                {
                    // Substituted, an expression is evaluated once per reference, and a condition from
                    // above it is evaluated beneath it — the same only for a function of its row alone. A
                    // window is not one: a filter moved under it changes the rows it ranges over.
                    if (IsDeterministic(Projections(below), null) == false)
                        return null;

                    for (var i = 0; i < projects.Count; i++)
                        projects[i] = RelOptUtil.pushPastProject(projects[i], below);
                    for (var i = 0; i < conditions.Count; i++)
                        conditions[i] = RelOptUtil.pushPastProject(conditions[i], below);

                    node = below.getInput();
                    continue;
                }

                return null;
            }

            return null;
        }

        static T? First<T>(IReadOnlyList<RelNode> members) where T : class
        {
            foreach (var member in members)
                if (member is T match)
                    return match;

            return null;
        }

        /// <summary>
        /// Builds the one read the join is equivalent to, or returns <c>null</c> where it is not.
        /// </summary>
        static RelNode? TryMerge(Join join, Input left, Input right)
        {
            var type = join.getJoinType();
            if (type != JoinRelType.INNER && type != JoinRelType.LEFT && type != JoinRelType.RIGHT)
                return null;

            // A sorted side is taken after the join rather than before it, which holds only where the join
            // keeps each of its rows exactly once: it has to be the side an outer join preserves, and the key
            // join below is what says the other side adds at most one row to each. An inner join can drop a
            // row of the page, and the page taken after it would reach past the row for another.
            if (left.Sort is not null && (type != JoinRelType.LEFT || right.Sort is not null))
                return null;
            if (right.Sort is not null && type != JoinRelType.RIGHT)
                return null;

            if (join.getVariablesSet().isEmpty() == false)
                return null;

            if (left.Scan.getTable()?.unwrap(typeof(CosmosTable)) is not CosmosTable table
                || right.Scan.getTable()?.unwrap(typeof(CosmosTable)) is not CosmosTable other
                || ReferenceEquals(table, other) == false)
                return null;

            // The same table read twice has the same row type, so either side's expressions are
            // expressions over the one scan the merged plan keeps.
            if (left.Scan.getRowType().Equals(right.Scan.getRowType()) == false)
                return null;

            var rexBuilder = join.getCluster().getRexBuilder();
            var p = left.Projects;
            var q = right.Projects;
            var f = left.Condition;
            var g = right.Condition;

            if (IsDeterministic(p, f) == false || IsDeterministic(q, g) == false)
                return null;

            if (IsKeyJoin(join.getCondition(), p, q, f, g, left.Scan, table, rexBuilder) == false)
                return null;

            // The join condition over the one document both sides now read.
            var condition = (RexNode)join.getCondition().accept(new Substitute(p, q));
            var simplifier = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR);

            RexNode? kept;
            var projects = new List<RexNode>();

            if (type == JoinRelType.INNER)
            {
                kept = And(rexBuilder, f, g, condition);
                projects.AddRange(p);
                projects.AddRange(q);
            }
            else if (type == JoinRelType.LEFT)
            {
                kept = f;
                var matched = simplifier.simplifyUnknownAsFalse(And(rexBuilder, g, condition));
                projects.AddRange(p);
                foreach (var column in q)
                    projects.Add(When(rexBuilder, matched, column));
            }
            else
            {
                kept = g;
                var matched = simplifier.simplifyUnknownAsFalse(And(rexBuilder, f, condition));
                foreach (var column in p)
                    projects.Add(When(rexBuilder, matched, column));
                projects.AddRange(q);
            }

            // Each column as the join typed it: a left join's right side is nullable, and a CASE over a
            // column already nullable is not typed identically to it in every case.
            var fields = join.getRowType().getFieldList();
            for (var i = 0; i < projects.Count; i++)
            {
                var wanted = ((RelDataTypeField)fields.get(i)).getType();
                if (projects[i].getType().Equals(wanted) == false)
                    projects[i] = rexBuilder.makeCast(wanted, projects[i], true, false);
            }

            RelNode input = left.Scan;

            if (kept is not null)
            {
                var simplified = simplifier.simplifyUnknownAsFalse(kept);
                if (simplified.isAlwaysFalse())
                    return null;
                if (simplified.isAlwaysTrue() == false)
                    input = LogicalFilter.create(input, simplified);
            }

            RelNode merged = LogicalProject.create(input, java.util.Collections.emptyList(), ToJava(projects), join.getRowType(), java.util.Collections.emptySet());

            // The sort over the merged read, its keys where the sorted side's columns now sit: in place for
            // the left side, after the left side's columns for the right.
            if (left.Sort is Sort leftSort)
                merged = LogicalSort.create(merged, leftSort.getCollation(), leftSort.offset, leftSort.fetch);
            else if (right.Sort is Sort rightSort)
                merged = LogicalSort.create(merged, RelCollations.shift(rightSort.getCollation(), p.Count), rightSort.offset, rightSort.fetch);

            return merged;
        }

        /// <summary>
        /// Determines whether a join condition equates a key of the container between the two sides.
        /// </summary>
        /// <remarks>
        /// Only the top-level equalities between a column of each side count, and only those whose two
        /// sides are the same expression over the document — read through the <c>CASE WHEN M THEN k
        /// END</c> a merged left join wraps a key in. The rest of the condition is not ignored: it is
        /// evaluated over the one document like everything else, and simply proves nothing about which
        /// documents pair.
        /// </remarks>
        static bool IsKeyJoin(RexNode condition, IReadOnlyList<RexNode> p, IReadOnlyList<RexNode> q, RexNode? f, RexNode? g, TableScan scan, CosmosTable table, RexBuilder rexBuilder)
        {
            if (CosmosImplementor.TryBindOutput(scan, out var fields, out _) == false)
                return false;

            var container = table.Container;
            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList(), null, container);
            var facts = container.Facts.Derive(null);
            var equated = new List<RexNode>();
            var paths = new List<JsonDocumentPath>();

            var conjuncts = RelOptUtil.conjunctions(condition);
            for (var i = 0; i < conjuncts.size(); i++)
            {
                // IS NOT DISTINCT FROM pairs a null with a null, and a key says nothing about documents
                // holding none, so only a plain equality counts.
                if ((RexNode)conjuncts.get(i) is not RexCall call || call.getKind() != SqlKind.EQUALS || call.getOperands().size() != 2)
                    continue;

                if ((RexNode)call.getOperands().get(0) is not RexInputRef a || (RexNode)call.getOperands().get(1) is not RexInputRef b)
                    continue;

                var (l, r) = a.getIndex() < p.Count ? (a.getIndex(), b.getIndex() - p.Count) : (b.getIndex(), a.getIndex() - p.Count);
                if (l < 0 || l >= p.Count || r < 0 || r >= q.Count)
                    continue;

                var lk = KeyOf(p[l]);
                if (lk.equals(KeyOf(q[r])) == false)
                    continue;

                if (equated.Contains(lk) == false)
                    equated.Add(lk);

                if (TryKeyPath(lk, translator, facts, out var path) && path is JsonDocumentPath key && paths.Contains(key) == false)
                    paths.Add(key);
            }

            if (equated.Count == 0)
                return false;

            foreach (var constraint in container.Constraints.Constraints)
            {
                if (constraint is not CosmosConstraint.Unique unique)
                    continue;

                CosmosConstraintCompiler.Compiled compiled;
                try
                {
                    compiled = CosmosConstraintCompiler.Compile(unique, table, rexBuilder);
                }
                catch (System.ArgumentException)
                {
                    // A declared constraint was compiled when the model was read, so this is one built some
                    // other way that does not compile against this container: it licenses nothing.
                    continue;
                }

                if (Covers(compiled, equated, paths, translator) == false)
                    continue;

                // Scoped by a predicate, the constraint holds among the documents it admits, and a join pairs
                // a document of one side with one of the other — so both sides have to be proved inside it.
                if (compiled.Filter is RexNode filter
                    && (CosmosConstraintCompiler.Implies(rexBuilder, f, filter) == false || CosmosConstraintCompiler.Implies(rexBuilder, g, filter) == false))
                    continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether two documents agreeing on every equated expression agree on every one of a
        /// constraint's keys, which are then the same document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A key that is a plain accessor stands for the stored value</b> at its path, so it is agreed on
        /// where the join equates a faithful reading of that path — text where the path holds one type, a
        /// <c>UUID</c> cast where it holds a canonical form. See <see cref="TryKeyPath"/>.
        /// </para>
        /// <para>
        /// <b>Any other key is agreed on where it is computed from equated expressions alone</b>: equal inputs
        /// make equal outputs. Only through operators that are null exactly where an operand is — Calcite's
        /// <see cref="Strong.Policy.ANY"/> — because a key that could be null where the join key is not would
        /// put the pair outside the constraint's claim, a null equalling nothing.
        /// </para>
        /// </remarks>
        static bool Covers(CosmosConstraintCompiler.Compiled compiled, List<RexNode> equated, List<JsonDocumentPath> paths, CosmosRexTranslator translator)
        {
            foreach (var key in compiled.Keys)
            {
                var value = CosmosRexTranslator.StripRedundantTextCast(key);

                if ((value is RexInputRef || CosmosRexTranslator.IsTextJsonValue(value)) && translator.TryResolvePath(value, out var resolved))
                {
                    if (CosmosDocumentPaths.From(resolved) is not JsonDocumentPath path || paths.Contains(path) == false)
                        return false;

                    continue;
                }

                if (IsComputedFrom(key, equated, out var used) == false || used == false)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether a call is null exactly where one of its operands is.
        /// </summary>
        /// <remarks>
        /// Calcite's own answer where it has one — <see cref="Strong.Policy.ANY"/>, which the comparison and
        /// arithmetic operators and the cast carry — and otherwise a short list of string functions that are
        /// null only for a null argument and Calcite types as such but does not classify: an ordinary function
        /// reports <see cref="Strong.Policy.AS_IS"/>, which says nothing either way. Short and named rather than
        /// inferred from a return type, because a nullable result type says a null is possible, not when.
        /// </remarks>
        static bool IsNullExactlyWhereAnOperandIs(RexCall call)
        {
            if (Strong.policy(call) == Strong.Policy.ANY)
                return true;

            return call.getOperator().getName() switch
            {
                "LOWER" or "UPPER" or "TRIM" or "INITCAP" or "SUBSTRING" or "CHAR_LENGTH" or "CHARACTER_LENGTH" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Determines whether an expression is computed from the equated expressions and literals alone,
        /// through operators null exactly where an operand is.
        /// </summary>
        static bool IsComputedFrom(RexNode node, List<RexNode> equated, out bool used)
        {
            used = false;

            foreach (var expression in equated)
            {
                if (node.equals(expression))
                {
                    used = true;
                    return true;
                }
            }

            switch (node)
            {
                case RexLiteral:
                    return true;
                case RexCall call when IsNullExactlyWhereAnOperandIs(call):
                    for (var i = 0; i < call.getOperands().size(); i++)
                    {
                        if (IsComputedFrom((RexNode)call.getOperands().get(i), equated, out var operandUsed) == false)
                            return false;

                        used |= operandUsed;
                    }

                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads through the wrapper a merged left join puts around a column: <c>CASE WHEN M THEN k END</c>
        /// is <c>k</c> or null, and equal to something only where it is <c>k</c>.
        /// </summary>
        static RexNode KeyOf(RexNode node)
        {
            while (true)
            {
                if (node is RexCall cast && cast.getKind() == SqlKind.CAST && cast.getOperands().size() == 1
                    && SqlTypeUtil.equalSansNullability(cast.getType(), ((RexNode)cast.getOperands().get(0)).getType()))
                {
                    node = (RexNode)cast.getOperands().get(0);
                    continue;
                }

                if (node is RexCall @case && @case.getKind() == SqlKind.CASE && @case.getOperands().size() == 3
                    && RexLiteral.isNullLiteral((RexNode)@case.getOperands().get(2)))
                {
                    node = (RexNode)@case.getOperands().get(1);
                    continue;
                }

                return node;
            }
        }

        /// <summary>
        /// Determines whether an expression reads the value at one document path faithfully enough that
        /// two equal readings are two equal stored values, and returns the path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two readings. The value as text — a promoted column, or <c>JSON_VALUE</c> — where the path holds
        /// one scalar type, so that text equal is value equal: without it the number <c>1</c> and the
        /// string <c>"1"</c> read alike. And a <c>UUID</c> cast over that text where the path holds a
        /// canonical UUID spelling, so that the one value has one spelling.
        /// </para>
        /// <para>
        /// The facts asked are the unconditional ones. A fact stated under a discriminator holds of the
        /// documents one side proves it for and not necessarily of the documents the other side reads.
        /// </para>
        /// </remarks>
        static bool TryKeyPath(RexNode node, CosmosRexTranslator translator, JsonFactSet facts, out JsonDocumentPath? path)
        {
            path = null;

            if (node is RexCall cast && cast.getKind() == SqlKind.CAST && cast.getOperands().size() == 1)
            {
                if (cast.getType().getSqlTypeName() != SqlTypeName.UUID)
                    return false;

                var text = CosmosRexTranslator.StripRedundantTextCast((RexNode)cast.getOperands().get(0));
                if (CosmosRexTranslator.IsTextJsonValue(text) == false || translator.TryResolvePath(text, out var resolved) == false)
                    return false;

                path = CosmosDocumentPaths.From(resolved);
                return path is JsonDocumentPath uuid && facts.RepresentationOf(uuid) is CosmosRepresentation representation && CosmosUuidForms.IsUuid(representation);
            }

            var value = CosmosRexTranslator.StripRedundantTextCast(node);
            if (value is not RexInputRef && CosmosRexTranslator.IsTextJsonValue(value) == false)
                return false;

            if (translator.TryResolvePath(value, out var accessed) == false)
                return false;

            path = CosmosDocumentPaths.From(accessed);
            return path is JsonDocumentPath scalar && IsOneScalarType(facts, scalar);
        }

        /// <summary>
        /// Determines whether the facts give a path one scalar type, which is what makes its text a
        /// faithful reading of its value.
        /// </summary>
        static bool IsOneScalarType(JsonFactSet facts, JsonDocumentPath path)
        {
            foreach (var claim in facts.ClaimsFor(path))
                if (claim is JsonClaim.OfType { Type: JsonType.String or JsonType.Number or JsonType.Integer or JsonType.Boolean })
                    return true;

            return false;
        }

        static IReadOnlyList<RexNode> Projections(Project project)
        {
            var list = new List<RexNode>();
            var projects = project.getProjects();
            for (var i = 0; i < projects.size(); i++)
                list.Add((RexNode)projects.get(i));
            return list;
        }

        static bool IsDeterministic(IReadOnlyList<RexNode> projects, RexNode? condition)
        {
            foreach (var project in projects)
                if (RexUtil.isDeterministic(project) == false || RexOver.containsOver(project))
                    return false;

            return condition is null || RexUtil.isDeterministic(condition);
        }

        static RexNode? And(RexBuilder rexBuilder, params RexNode?[] conjuncts)
        {
            var list = new java.util.ArrayList();
            foreach (var conjunct in conjuncts)
                if (conjunct is not null)
                    list.add(conjunct);

            return list.isEmpty() ? null : RexUtil.composeConjunction(rexBuilder, list);
        }

        /// <summary>
        /// A column of the side that may not pair: its value where the document pairs, and null where not.
        /// </summary>
        static RexNode When(RexBuilder rexBuilder, RexNode? matched, RexNode column)
        {
            if (matched is null || matched.isAlwaysTrue())
                return column;

            var type = rexBuilder.getTypeFactory().createTypeWithNullability(column.getType(), true);
            return rexBuilder.makeCall(type, SqlStdOperatorTable.CASE, java.util.Arrays.asList(matched, column, rexBuilder.makeNullLiteral(type)));
        }

        static java.util.List ToJava(List<RexNode> nodes)
        {
            var list = new java.util.ArrayList(nodes.Count);
            foreach (var node in nodes)
                list.add(node);
            return list;
        }

        /// <summary>
        /// Rewrites an expression over the join's row into one over the scan, each side's column replaced
        /// by the expression that side computed it with.
        /// </summary>
        sealed class Substitute : RexShuttle
        {

            readonly IReadOnlyList<RexNode> _left;
            readonly IReadOnlyList<RexNode> _right;

            public Substitute(IReadOnlyList<RexNode> left, IReadOnlyList<RexNode> right)
            {
                _left = left;
                _right = right;
            }

            public override RexNode visitInputRef(RexInputRef inputRef)
            {
                var index = inputRef.getIndex();
                return index < _left.Count ? _left[index] : _right[index - _left.Count];
            }

        }

    }

}
