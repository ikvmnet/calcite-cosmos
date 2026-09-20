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
    /// What a scan selects, and what it binds each column of the row type to.
    /// </summary>
    public class CosmosTableScanTests : CosmosRelNodeFixture
    {

        [Fact]
        public void ScanSelectsTheDocument()
        {
            Sql(Scan(), Implementor()).Should().Be("SELECT VALUE c FROM products c");
        }

        [Fact]
        public void ScanBindsDocumentColumnToRootAndPromotedColumnsToProperties()
        {
            var implementor = Implementor();
            Scan().Implement(implementor);

            implementor.Fields[0]!.ToString().Should().Be("c");
            implementor.Fields[1]!.ToString().Should().Be("c.id");
            implementor.Fields[4]!.ToString().Should().Be("c.category");
        }

    }

}
