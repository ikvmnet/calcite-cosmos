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
    /// The object constructor a projection renders, and what it leaves bound above it.
    /// </summary>
    public class CosmosProjectTests : CosmosRelNodeFixture
    {

        [Fact]
        public void ProjectRendersAnObjectConstructor()
        {
            var project = ProjectOver(Scan(), new[] { ("theId", Ref(1)), ("stamp", Ref(2)) });

            Sql(project, Implementor()).Should().Be("SELECT VALUE { \"theId\": c.id, \"stamp\": c._ts } FROM products c");
        }

        [Fact]
        public void ProjectOfADocumentPropertyRendersAPath()
        {
            var project = ProjectOver(Scan(), new[] { ("city", Doc("city")) });

            Sql(project, Implementor()).Should().Be("SELECT VALUE { \"city\": (IS_PRIMITIVE(c.city) ? c.city : null) } FROM products c");
        }

        /// <remarks>
        /// Cosmos ORDER BY addresses the source document, not the projected object, so a sort above
        /// a projection must reference the underlying path. That only works because the projection
        /// rebinds the field ordinals to the paths it projected.
        /// </remarks>
        [Fact]
        public void SortAboveAPathProjectionUsesTheUnderlyingPath()
        {
            var project = ProjectOver(Scan(), new[] { ("theId", Ref(1)) });
            var sort = SortOver(project, Collation((0, RelFieldCollation.Direction.ASCENDING)));

            Sql(sort, Implementor()).Should().Be("SELECT VALUE { \"theId\": c.id } FROM products c ORDER BY c.id ASC");
        }

        /// <remarks>
        /// A computed projection has no path to rebind to, so downstream operators that need one
        /// must decline rather than address the wrong value.
        /// </remarks>
        [Fact]
        public void SortAboveAComputedProjectionIsRefused()
        {
            var computed = _rex.makeCall(SqlStdOperatorTable.PLUS, Ref(2), Num(1));
            var project = ProjectOver(Scan(), new[] { ("adjusted", computed) });
            var sort = SortOver(project, Collation((0, RelFieldCollation.Direction.ASCENDING)));

            var act = () => Sql(sort, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        [Fact]
        public void ComputedProjectionStillRenders()
        {
            var computed = _rex.makeCall(SqlStdOperatorTable.PLUS, Ref(2), Num(1));
            var project = ProjectOver(Scan(), new[] { ("adjusted", computed) });

            Sql(project, Implementor()).Should().Be("SELECT VALUE { \"adjusted\": (c._ts + @p0) } FROM products c");
        }

        /// <remarks>
        /// Cosmos evaluates WHERE against the source document, before SELECT — and the predicate is
        /// rendered against document paths rather than projected names, so filtering before or
        /// after a path-only projection admits the same documents. Once refused wholesale; what
        /// stays refused is a predicate that reads a computed column, covered next.
        /// </remarks>
        [Fact]
        public void FilterAboveAPathOnlyProjectionRendersAsAWhere()
        {
            var project = ProjectOver(Scan(), new[] { ("theId", Ref(1)) });
            var filter = new CosmosFilter(_cluster, Traits(), project,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0), Str("abc")));

            Sql(filter, Implementor()).Should().Be("SELECT VALUE { \"theId\": c.id } FROM products c WHERE (c.id = @p0)");
        }

        /// <remarks>
        /// A computed column has no path for WHERE to name — a projection alias is not visible to
        /// it — so the reference itself is refused.
        /// </remarks>
        [Fact]
        public void FilterReadingAComputedProjectionIsRefused()
        {
            var computed = _rex.makeCall(SqlStdOperatorTable.PLUS, Ref(2), Num(1));
            var project = ProjectOver(Scan(), new[] { ("adjusted", computed) });
            var filter = new CosmosFilter(_cluster, Traits(), project,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0), Num(5)));

            var act = () => Sql(filter, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*computed*");
        }

        [Fact]
        public void StackedProjectionsAreRefused()
        {
            var inner = ProjectOver(Scan(), new[] { ("theId", Ref(1)) });
            var outer = ProjectOver(inner, new[] { ("again", Ref(0)) });

            var act = () => Sql(outer, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        [Fact]
        public void ProjectionOverFilterCombinesBothClauses()
        {
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(4), Str("bikes")));
            var project = ProjectOver(filter, new[] { ("theId", Ref(1)) });

            Sql(project, Implementor()).Should().Be("SELECT VALUE { \"theId\": c.id } FROM products c WHERE (c.category = @p0)");
        }

    }

}
