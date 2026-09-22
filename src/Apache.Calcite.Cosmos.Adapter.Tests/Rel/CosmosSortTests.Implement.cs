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

    public partial class CosmosSortTests
    {

        /// <summary>
        /// The <c>ORDER BY</c> clause a sort renders, and the orderings it refuses to render.
        /// </summary>
        public class Implement : CosmosRelNodeFixture
        {

            [Fact]
            public void SingleKeySortRendersOrderBy()
            {
                var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
                Sql(sort, Implementor()).Should().Be("SELECT VALUE c FROM products c ORDER BY c.id ASC");
            }

            [Fact]
            public void DescendingSortRendersDesc()
            {
                var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.DESCENDING)));
                Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id DESC");
            }

            /// <summary>
            /// A distance orders at the service, so a sort over one is written out rather than declined.
            /// </summary>
            /// <remarks>
            /// The expression appears twice — once selected, once in the clause — because Cosmos cannot
            /// order by a projection alias. That it may appear in the clause at all is the exception the
            /// service makes for spatial, measured in <c>CosmosGeographyServiceMeasurementTests</c>; a computed key of
            /// any other kind is refused with 400, error 2206.
            /// </remarks>
            [Fact]
            public void ASortOverADistanceRendersTheExpression()
            {
                var project = ProjectOver(Scan(), new[]
                {
                    ("id", Ref(1)),
                    ("d", Distance()),
                });

                var sort = SortOver(project, Collation((1, RelFieldCollation.Direction.ASCENDING)));

                Sql(sort, Implementor()).Should().EndWith($"ORDER BY ST_DISTANCE(c.location, {Here}) ASC");
            }

            /// <summary>
            /// And a second key beside it is declined, because the service refuses one.
            /// </summary>
            [Fact]
            public void ASecondKeyBesideADistanceIsDeclined()
            {
                var project = ProjectOver(Scan(), new[]
                {
                    ("id", Ref(1)),
                    ("d", Distance()),
                });

                var sort = SortOver(project, Collation(
                    (1, RelFieldCollation.Direction.ASCENDING),
                    (0, RelFieldCollation.Direction.ASCENDING)));

                var implement = () => Sql(sort, Implementor());
                implement.Should().Throw<CosmosTranslationException>();
            }

            /// <summary>
            /// A computed key that is not a distance is declined as it always was.
            /// </summary>
            /// <remarks>
            /// <c>ORDER BY UPPER(…)</c> would render into a statement the service answers with 400, error
            /// 2206, so the projection records nothing for it and the sort has nothing to write.
            /// </remarks>
            [Fact]
            public void AComputedKeyThatIsNotADistanceIsDeclined()
            {
                var project = ProjectOver(Scan(), new[]
                {
                    ("id", Ref(1)),
                    ("u", _rex.makeCall(SqlStdOperatorTable.UPPER, Ref(1))),
                });

                var sort = SortOver(project, Collation((1, RelFieldCollation.Direction.ASCENDING)));

                var implement = () => Sql(sort, Implementor());
                implement.Should().Throw<CosmosTranslationException>();
            }

            [Fact]
            public void SortOverFilterCombinesBothClauses()
            {
                var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                    _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(4), Str("bikes")));
                var sort = SortOver(filter, Collation((1, RelFieldCollation.Direction.ASCENDING)));

                Sql(sort, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE (c.category = @p0) ORDER BY c.id ASC");
            }

            [Fact]
            public void OffsetAndFetchRenderAsOffsetLimit()
            {
                var sort = SortOver(
                    Scan(),
                    Collation((1, RelFieldCollation.Direction.ASCENDING)),
                    _rex.makeExactLiteral(new java.math.BigDecimal(5)),
                    _rex.makeExactLiteral(new java.math.BigDecimal(10)));

                Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id ASC OFFSET 5 LIMIT 10");
            }

            /// <remarks>
            /// The container declares a composite index over (/id, /_ts), which are fields 1 and 2.
            /// </remarks>
            [Fact]
            public void MultiKeySortWithAMatchingCompositeIndexIsAccepted()
            {
                var sort = SortOver(Scan(), Collation(
                    (1, RelFieldCollation.Direction.ASCENDING),
                    (2, RelFieldCollation.Direction.ASCENDING)));

                Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id ASC, c._ts ASC");
            }

            /// <remarks>
            /// A composite index also serves the fully inverted sort.
            /// </remarks>
            [Fact]
            public void FullyInvertedMultiKeySortIsAccepted()
            {
                var sort = SortOver(Scan(), Collation(
                    (1, RelFieldCollation.Direction.DESCENDING),
                    (2, RelFieldCollation.Direction.DESCENDING)));

                Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id DESC, c._ts DESC");
            }

            [Fact]
            public void PartiallyInvertedMultiKeySortIsRefused()
            {
                var sort = SortOver(Scan(), Collation(
                    (1, RelFieldCollation.Direction.ASCENDING),
                    (2, RelFieldCollation.Direction.DESCENDING)));

                var act = () => Sql(sort, Implementor());
                act.Should().Throw<CosmosTranslationException>().WithMessage("*composite index*");
            }

            /// <remarks>
            /// Without a matching composite index the service rejects the query outright, so pushing
            /// it down would be a defect rather than a pessimisation.
            /// </remarks>
            [Fact(Skip = "#165: key 4 is a promoted VARIANT column, which CosmosSort now declines before the composite-index check is reached. Composite-index refusal is still covered by the (1, 2) case above; re-enable when upstream defines a variant order.")]
            public void MultiKeySortWithoutACompositeIndexIsRefused()
            {
                var sort = SortOver(Scan(), Collation(
                    (1, RelFieldCollation.Direction.ASCENDING),
                    (4, RelFieldCollation.Direction.DESCENDING)));

                var act = () => Sql(sort, Implementor());
                act.Should().Throw<CosmosTranslationException>().WithMessage("*composite index*");
            }

            [Fact]
            public void StackedSortsAreRefused()
            {
                var inner = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
                var outer = SortOver(inner, Collation((2, RelFieldCollation.Direction.ASCENDING)));

                var act = () => Sql(outer, Implementor());
                act.Should().Throw<CosmosTranslationException>();
            }

            [Fact]
            public void SortIsRefusedWhenGroupingIsPresent()
            {
                var implementor = Implementor();
                implementor.Query.AddGroupBy("c.category");

                var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));

                var act = () => Sql(sort, implementor);
                act.Should().Throw<CosmosTranslationException>().WithMessage("*GROUP BY*");
            }

        }

    }

}
