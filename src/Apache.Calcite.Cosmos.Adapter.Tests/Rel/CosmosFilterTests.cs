using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.tools;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// The <c>WHERE</c> clause a filter renders, and the predicates it refuses.
    /// </summary>
    [TestClass]
    public class CosmosFilterTests : CosmosRelNodeFixture
    {

        [TestMethod]
        public void FilterRendersAWhereClause()
        {
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("abc")));

            var implementor = Implementor();
            Sql(filter, implementor).Should().Be("SELECT VALUE c FROM products c WHERE (c.id = @p0)");
            implementor.Build().Parameters.Should().ContainSingle().Which.Value.Should().Be("abc");
        }

        [TestMethod]
        public void FilterOverADocumentPropertyRendersAPath()
        {
            var item = Doc("city");
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, item, Str("Seattle")));

            Sql(filter, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE (c.city = @p0)");
        }

        /// <remarks>
        /// Stacked filters are normally merged by the planner; conjoining defensively ensures
        /// neither predicate is silently dropped if they are not.
        /// </remarks>
        [TestMethod]
        public void StackedFiltersAreConjoined()
        {
            var inner = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("a")));
            var outer = new CosmosFilter(_cluster, Traits(), inner,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("b")));

            Sql(outer, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE ((c.id = @p0) AND (c.id = @p1))");
        }

        [TestMethod]
        public void UntranslatableFilterIsRefused()
        {
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.IS_NOT_NULL, _rex.makeCall(SqlStdOperatorTable.INITCAP, Ref(1))));

            var act = () => Sql(filter, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

    }

}
