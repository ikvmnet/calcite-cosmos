using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using org.apache.calcite.rel.core;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// A filter as the predicate oracle reads it, asked what the correlate rule asks: whether anything a statement holds
    /// raises on a document the declaration admits (<see cref="CosmosFilterStatement.RaisesNothing"/>).
    /// </summary>
    public sealed class CosmosFilterStatementTests
    {

        /// <summary>
        /// The <c>WHERE</c> above a statement's correlate, read through the binding the correlate rule's question reads the
        /// correlate's row by, the element a traversal yields among it.
        /// </summary>
        static CosmosFilterStatement AboveTheCorrelate(string sql)
        {
            var members = RegisteredMembers.Of(sql);
            var correlate = members.OfType<Correlate>().First();
            var filter = members.OfType<Filter>().First();

            CosmosImplementor.TryBindElements(correlate, out var fields, out var readings, out var container).Should().BeTrue();
            return CosmosFilterStatement.Of(filter.getCondition(), fields, readings, container, filter.getCluster().getRexBuilder());
        }

        static string Traversal(string where, string type = "VARCHAR") => $"""
            SELECT c."id", u.t FROM products AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING {type} ARRAY)) AS u(t)
            WHERE {where}
            """;

        /// <summary>
        /// A comparison of the element a traversal yields and of the document's text raises nowhere, a cast of the element
        /// may raise on a string it does not parse, and an element of a type no row names reads nothing, so a comparison of
        /// it may raise. Asked of the statement whole, nothing assumed, whether it keeps a document or not.
        /// </summary>
        [Fact]
        public void RaisesNothingAsksWhetherAnythingTheStatementHoldsRaises()
        {
            AboveTheCorrelate(Traversal("JSON_VALUE(c.\"DOC\", '$.kind') = 'A' AND u.t = '1'")).RaisesNothing.Should().BeTrue();
            AboveTheCorrelate(Traversal("JSON_VALUE(c.\"DOC\", '$.kind') = 'A' AND (u.t = '1' OR u.t IS NULL)")).RaisesNothing.Should().BeTrue();
            AboveTheCorrelate(Traversal("u.t = '1' AND u.t = '2'")).RaisesNothing.Should().BeTrue("a statement that keeps nothing raises nothing either");

            AboveTheCorrelate(Traversal("JSON_VALUE(c.\"DOC\", '$.kind') = 'A' AND CAST(u.t AS INTEGER) = 1")).RaisesNothing.Should().BeFalse("the element may be \"x\"");
            AboveTheCorrelate(Traversal("JSON_VALUE(c.\"DOC\", '$.kind') = 'A' OR CAST(u.t AS INTEGER) = 1")).RaisesNothing.Should().BeFalse("Calcite reads both sides of an OR");
            AboveTheCorrelate(Traversal("u.t = 1", "INTEGER")).RaisesNothing.Should().BeFalse("no row names what an INTEGER traversal yields");
        }

    }

}
