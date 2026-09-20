using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.tools;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// The part of <c>Implement</c> that belongs to no single node: the order a plan is allowed to
    /// write its clauses in, and the refusal to visit anything outside the Cosmos convention.
    /// </summary>
    public class CosmosRelTests : CosmosRelNodeFixture
    {

        // Cosmos applies OFFSET/LIMIT last. An operator the plan places above a row restriction
        // but that would be written into an earlier clause cannot be folded into the same
        // statement: it would run before the restriction rather than after, returning different
        // rows. These are silent wrong answers, not service errors.

        CosmosSort LimitOver(RelNode input, int fetch) =>
            SortOver(input, RelCollations.EMPTY, null, _rex.makeExactLiteral(new java.math.BigDecimal(fetch)));

        [Fact]
        public void FilterAboveARowLimitIsRefused()
        {
            var filter = new CosmosFilter(_cluster, Traits(), LimitOver(Scan(), 5),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("x")));

            var act = () => Sql(filter, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*row limit*");
        }

        [Fact]
        public void AggregateAboveARowLimitIsRefused()
        {
            var aggregate = new CosmosAggregate(
                _cluster, Traits(), LimitOver(Scan(), 5),
                org.apache.calcite.util.ImmutableBitSet.of(new[] { 4 }), null, new java.util.ArrayList());

            var act = () => Sql(aggregate, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*row limit*");
        }

        [Fact]
        public void UnnestAboveARowLimitIsRefused()
        {
            var unnest = UnnestOver(LimitOver(Scan(), 5), MapItem("tags"));

            var act = () => Sql(unnest, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        /// <remarks>
        /// A traversal multiplies rows, so folding one above a sort would sort the unmultiplied
        /// set.
        /// </remarks>
        [Fact]
        public void UnnestAboveASortIsRefused()
        {
            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
            var unnest = UnnestOver(sort, MapItem("tags"));

            var act = () => Sql(unnest, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        /// <remarks>
        /// A sort without a restriction commutes with a filter, so this stays available.
        /// </remarks>
        [Fact]
        public void FilterAboveAnUnlimitedSortIsAllowed()
        {
            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
            var filter = new CosmosFilter(_cluster, Traits(), sort,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(4), Str("bikes")));

            Sql(filter, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE (c.category = @p0) ORDER BY c.id ASC");
        }

        // ── Convention boundary ───────────────────────────────────────────────────

        [Fact]
        public void VisitingANonCosmosNodeIsRefused()
        {
            var logical = org.apache.calcite.rel.logical.LogicalTableScan.create(_cluster, _table, java.util.Collections.emptyList());

            var act = () => Implementor().Visit(logical);
            act.Should().Throw<CosmosTranslationException>();
        }

    }

}
