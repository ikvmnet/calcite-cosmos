using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Filter implemented in the <see cref="CosmosConvention"/> calling convention, rendered as a
    /// <c>WHERE</c> clause.
    /// </summary>
    public class CosmosFilter : Filter, CosmosRel
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="condition">The filter condition.</param>
        public CosmosFilter(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RexNode condition) :
            base(cluster, traitSet, input, condition)
        {

        }

        /// <inheritdoc />
        public override Filter copy(RelTraitSet traitSet, RelNode input, RexNode condition)
        {
            return new CosmosFilter(getCluster(), traitSet, input, condition);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// A predicate pinning a complete partition key and an <c>id</c> addresses one document, and
        /// saying so is the difference between the planner comparing mechanisms and comparing invented
        /// row counts. Calcite's default is a fixed selectivity per conjunct, which for a by-id lookup
        /// over a container of any size produces a number unrelated to the single document there is:
        /// the nodes around the lookup — a converter, a filter finishing above it — are then costed on
        /// that number, and their difference swamps the difference between a read and a query.
        /// </para>
        /// <para>
        /// It is <em>at most</em> one, and the estimate is one rather than a fraction because a row
        /// count of zero prices a plan at nothing and makes everything containing it look free.
        /// </para>
        /// <para>
        /// <b>This is not the path the planner takes, and on its own it does nothing.</b>
        /// <c>RelMdRowCount</c> has a handler for <c>Filter</c> and dispatches to it on the node's
        /// class, so the planner never asks a <c>Filter</c> subclass for its own estimate.
        /// <see cref="CosmosRelMetadataQuery"/> is what actually answers, and it is installed where a
        /// Cosmos table reaches a plan. This override is kept because it is the correct answer to the
        /// question it is asked, for any caller that does ask it directly.
        /// </para>
        /// </remarks>
        public override double estimateRowCount(RelMetadataQuery mq)
        {
            if (getConvention() is CosmosConvention convention &&
                CosmosImplementor.TryBindOutput(getInput(), out var fields, out _) &&
                CosmosPartitionKeyExtractor.PinsAtMostOneDocument(getCondition(), fields, convention.Container, CosmosImplementor.DefaultRootAlias))
                return 1d;

            return base.estimateRowCount(mq);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Two properties of the predicate dominate what a Cosmos query costs, and neither is
        /// visible in the shape of the plan:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// Naming the partition key confines execution to one physical partition instead of
        /// fanning out across every one and merging.
        /// </description></item>
        /// <item><description>
        /// Filtering on a path the container does not index forces a scan of it.
        /// </description></item>
        /// </list>
        /// <para>
        /// Both are reflected here so the planner prefers the predicate that is genuinely cheaper
        /// rather than treating all pushed-down filters alike.
        /// </para>
        /// </remarks>
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var cost = base.computeSelfCost(planner, mq);
            if (cost is null || getConvention() is not CosmosConvention convention)
                return cost;

            var container = convention.Container;

            // Cost only, so an input whose binding cannot be derived costs as the base does rather than
            // declining anything — but it is the walked binding, so the discount is decided on the paths
            // the statement will actually address.
            if (CosmosImplementor.TryBindOutput(getInput(), out var fields, out _) == false)
                return cost.multiplyBy(CosmosConvention.CostMultiplier);

            var multiplier = CosmosConvention.CostMultiplier;

            // How much pinning the key is worth is a fact about the container, not a constant. A
            // cross-partition query costs roughly one single-partition query per physical partition, so
            // on a two-partition container this saves little and on a two-hundred-partition container it
            // saves nearly everything. Where the service was not asked, the old constant stands.
            if (CosmosPartitionKeyExtractor.TryExtractPrefix(getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, out var prefix, out var complete))
                multiplier *= PartitionDiscount(container, prefix.Count, complete);

            if (ReferencesUnindexedPath(getCondition(), fields, container))
                multiplier *= UnindexedPathPenalty;

            // A full text function over a path the container declares nothing about is a scan of the
            // same kind, and priced as one. It was refused instead, and the refusal is what #85
            // measured out of existence.
            if (ReferencesUndeclaredFullTextPath(getCondition(), fields, container))
                multiplier *= UnindexedPathPenalty;

            // What the route costs, added rather than multiplied. A multiplier scales with the rows and
            // the difference between a read and a query does not: it is a per-statement constant of
            // about two request units, which against the single row a by-id lookup returns a multiplier
            // rounds away entirely. This is the axis CosmosPointReadSplitRule offers a choice on.
            return cost.multiplyBy(multiplier)
                .plus(planner.getCostFactory().makeCost(RequestUnits(container, mq), 0d, 0d));
        }

        /// <summary>
        /// Returns what this filter's route is estimated to cost in request units.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A predicate that says nothing beyond a complete <c>id</c> and partition key is answered by a
        /// point read rather than by the query engine, and the two are not priced alike:
        /// <see cref="CosmosRequestUnitModel"/> carries the measured coefficients, and the short version
        /// is that a read skips the query floor but pays for the document body at about six times a
        /// query's rate. So the read wins on a container of records and loses on one of large bodies,
        /// and which container this is decides it rather than a rule asserting that one shape is better.
        /// </para>
        /// <para>
        /// <b>Added to the cost rather than multiplied into it, which is a commitment worth naming.</b>
        /// Calcite costs in abstract units and Cosmos charges in request units, and adding one to the
        /// other says a request unit is worth about as much as moving a row in process. That conversion
        /// is a judgement and nobody has measured it. It is made because the alternative is worse: the
        /// difference between a read and a query is a per-statement constant, so multiplied into the
        /// one-row cost of a by-id lookup it rounds the whole distinction away — and the planner's bar is
        /// ordering rather than accuracy. This orders the routes the way the measured charges do, which
        /// a multiplier demonstrably does not.
        /// </para>
        /// <para>
        /// The fan-out is left to <see cref="PartitionDiscount"/>, which already prices it, so the model
        /// is asked about one partition here and the two do not double-count.
        /// </para>
        /// <para>
        /// Where the service was never asked for statistics the size is zero, which prices a read at its
        /// floor and makes it look best. That is the right way round: a container nobody has measured is
        /// far likelier to hold records than blobs, and being wrong costs the query floor while being
        /// right saves without bound.
        /// </para>
        /// </remarks>
        double RequestUnits(CosmosContainerMetadata container, RelMetadataQuery mq)
        {
            var size = CosmosRequestUnitModel.AverageDocumentSizeInBytes(container);

            if (CosmosImplementor.TryBindOutput(getInput(), out var fields, out _) &&
                CosmosPartitionKeyExtractor.TryExtractPointRead(getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, out _, out _))
                return CosmosRequestUnitModel.PointRead(size);

            return CosmosRequestUnitModel.Query(mq.getRowCount(this).doubleValue(), size);
        }

        /// <summary>
        /// Applied when the predicate confines the query to one logical partition and the container's
        /// spread is unknown.
        /// </summary>
        public const double SinglePartitionDiscount = .25d;

        /// <summary>
        /// Returns the discount for confining a query to part of the container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A complete key reaches one partition out of however many there are, so the work is divided by
        /// that count. A prefix of a hierarchical key reaches a subset, and how large a subset is not
        /// knowable from anything declared — it depends on how the values are distributed. Half the
        /// benefit of a complete key is a guess, and it is a guess about <em>cost</em>: it can make the
        /// planner prefer the wrong plan and cannot make it return the wrong rows.
        /// </para>
        /// <para>
        /// Bounded below so that an enormous container does not drive a filter's cost to nothing and
        /// make every plan containing one look free.
        /// </para>
        /// </remarks>
        static double PartitionDiscount(CosmosContainerMetadata container, int pinned, bool complete)
        {
            if (container.Statistics is not CosmosContainerStatistics statistics || statistics.PartitionCount <= 1)
                return complete ? SinglePartitionDiscount : 1d;

            var whole = 1d / statistics.PartitionCount;

            return System.Math.Max(complete ? whole : System.Math.Sqrt(whole), MinimumPartitionDiscount);
        }

        /// <summary>
        /// The floor on <see cref="PartitionDiscount"/>.
        /// </summary>
        public const double MinimumPartitionDiscount = .001d;

        /// <summary>
        /// Applied when the predicate touches a path the container does not index.
        /// </summary>
        public const double UnindexedPathPenalty = 4d;

        /// <summary>
        /// Determines whether a predicate references any path outside the container's index.
        /// </summary>
        static bool ReferencesUnindexedPath(RexNode condition, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container)
        {
            var translator = new CosmosRexTranslator(RexBuilderHolder.Value, fields, new CosmosParameterList());
            var unindexed = false;

            void Walk(RexNode node)
            {
                if (unindexed)
                    return;

                if (translator.TryResolvePath(node, out var path) && path is not null)
                {
                    // Only container-rooted paths correspond to index paths; an element-relative
                    // one is reached through its array, which is covered separately.
                    if (string.Equals(path.Alias, CosmosImplementor.DefaultRootAlias, StringComparison.Ordinal) &&
                        container.IsPathIndexed(path.ToPolicyPath()) == false)
                        unindexed = true;

                    return;
                }

                if (node is RexCall call)
                    for (var i = 0; i < call.getOperands().size(); i++)
                        Walk((RexNode)call.getOperands().get(i));
            }

            Walk(condition);
            return unindexed;
        }

        /// <summary>
        /// Determines whether an expression applies a full text function to a path the container
        /// declares nothing about — neither in its full text policy nor in a full text index.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A cost input, and until #85 it was a refusal: a predicate over such a path had been
        /// measured as a bodyless 400, so the translator declined the call. Measured again against
        /// three accounts and four containers, the service answers every form — the predicates and
        /// the score, over an undeclared path, over a container with no policy, and on an account
        /// without the full text capability — so what the declaration decides is whether the call is
        /// served by the index or by a scan. <see cref="UnindexedPathPenalty"/> is the price of a scan
        /// the ordinary index does not serve, and this is the same scan reached another way.
        /// </para>
        /// <para>
        /// Shared with <see cref="CosmosRank"/>, whose score is the same function in the other
        /// clause. The path is compared in policy form, without its alias, which is the form the
        /// container declares it in.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression to inspect.</param>
        /// <param name="fields">The binding the expression's field references resolve against.</param>
        /// <param name="container">What the container declares.</param>
        /// <returns><c>true</c> where a full text function reads an undeclared path.</returns>
        internal static bool ReferencesUndeclaredFullTextPath(RexNode node, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container)
        {
            var translator = new CosmosRexTranslator(RexBuilderHolder.Value, fields, new CosmosParameterList());
            var undeclared = false;

            void Walk(RexNode current)
            {
                if (undeclared || current is not RexCall call)
                    return;

                if (CosmosRexTranslator.IsFullTextFunction(call)
                    && call.getOperands().size() > 0
                    && translator.TryResolvePath((RexNode)call.getOperands().get(0), out var path)
                    && path is not null
                    && container.IsPathFullTextSearchable(path.ToPolicyPath()) == false)
                {
                    undeclared = true;
                    return;
                }

                for (var i = 0; i < call.getOperands().size(); i++)
                    Walk((RexNode)call.getOperands().get(i));
            }

            Walk(node);
            return undeclared;
        }

        static class RexBuilderHolder
        {

            internal static readonly RexBuilder Value = new(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());

        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            // Cosmos evaluates WHERE against the source document, before SELECT — which is not a
            // reason to refuse a filter above a pushed-down projection, because the translation
            // below renders the predicate against document paths rather than projected names, and
            // filtering before or after a path-only projection admits the same documents. What
            // cannot be expressed is a predicate reading a computed column, which has no path for
            // WHERE to name; the translation refuses that reference itself. Filters above a
            // projection were once refused wholesale here, which contradicted the per-ordinal
            // binding CosmosProject records, and cost the WHERE that FILTER_AGGREGATE_TRANSPOSE
            // recovers from a HAVING on a grouping key.

            // WHERE is evaluated before OFFSET/LIMIT, so folding a filter that the plan places
            // above a row restriction would filter the whole set and then restrict it, rather
            // than restricting first.
            if (implementor.Query.HasRowLimit)
                throw new CosmosTranslationException("A filter cannot be applied above a pushed-down row limit.");

            // Recovered before translation, from the same bindings, so that a query naming its
            // partition key can be executed against one partition instead of fanned out.
            if (implementor.PartitionKeyValues is null &&
                Metadata.CosmosPartitionKeyExtractor.TryExtractPrefix(getCondition(), implementor.Fields, implementor.Container, implementor.RootAlias, out var partitionKey, out var complete))
            {
                implementor.PartitionKeyValues = partitionKey;
                implementor.PartitionKeyIsComplete = complete;
            }

            // And whether the predicate is only that, plus an id — the shape a point read answers for
            // about 1 RU rather than the 2.3 a query costs at best. Offered here; whether it is taken
            // depends on what the rest of the statement asks for, which the implementor decides.
            if (implementor.PointReadCandidate is null &&
                Metadata.CosmosPartitionKeyExtractor.TryExtractPointRead(getCondition(), implementor.Fields, implementor.Container, implementor.RootAlias, out _, out var pointReadId))
                implementor.PointReadCandidate = pointReadId;

            // Or a set of ids — the same shape for several documents, which ReadManyItemsAsync
            // answers charged as point reads. Asked second: a single id is the single read's.
            else if (implementor.PointReadCandidate is null && implementor.PointReadSetCandidate is null &&
                Metadata.CosmosPartitionKeyExtractor.TryExtractPointReadSet(getCondition(), implementor.Fields, implementor.Container, implementor.RootAlias, out _, out var pointReadIds))
                implementor.PointReadSetCandidate = pointReadIds;

            var condition = implementor.Translate(getCondition());

            // Stacked filters are normally merged by the planner, but conjoin defensively rather
            // than silently discarding one.
            implementor.Query.Where = string.IsNullOrEmpty(implementor.Query.Where)
                ? condition
                : $"({implementor.Query.Where} AND {condition})";
        }

    }

}
