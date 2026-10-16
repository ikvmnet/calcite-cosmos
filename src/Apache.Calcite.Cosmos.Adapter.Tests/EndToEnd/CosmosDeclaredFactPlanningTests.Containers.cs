using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;

using FluentAssertions;
using Xunit;

using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A statement reading two containers: each filter is read by its own container's declaration, whatever the other
    /// declares. Every rule set sees every operator in the plan, and a conversion decided under another container's
    /// declaration once made one container's filter an empty relation, or dropped it, beside a statement over the other
    /// (<see cref="CosmosConvention.Owns"/>).
    /// </summary>
    public partial class CosmosDeclaredFactPlanningTests
    {

        static CosmosContainerMetadata Undeclared() => new("typed", new[] { "/category" });

        static CosmosContainerMetadata ThreeKinds() =>
            new CosmosContainerMetadata("kinds", new[] { "/kind" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(Planning.CosmosPushdownProbeTests.KindsSchema)));

        /// <summary>
        /// What each container's statements send, by container.
        /// </summary>
        static IReadOnlyList<string> Sent(RelNode planned, string container) =>
            Statements(planned)
                .Where(node => PlannerHarness.TableOf(node)?.Container.Name == container)
                .Select(node => PlannerHarness.Implement(node, PlannerHarness.TableOf(node)!.Container).Sql)
                .ToArray();

        static IEnumerable<RelNode> Statements(RelNode node)
        {
            if (node is CosmosRel)
            {
                yield return node;
                yield break;
            }

            for (var i = 0; i < node.getInputs().size(); i++)
                foreach (var found in Statements((RelNode)node.getInputs().get(i)))
                    yield return found;
        }

        /// <summary>
        /// A filter over a container that declares nothing, on a path another container in the statement declares, plans
        /// as it does alone: <c>kind IS NULL</c> is true of every typed document, none having a kind, and <c>kind IN ('a',
        /// 'b', 'c')</c> of none — where what the kinds declare made the first nothing, and the second every document.
        /// </summary>
        [Theory]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.kind') IS NULL")]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.kind') IN ('a', 'b', 'c')")]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.kind') = 'a' AND JSON_VALUE(c.\"DOC\", '$.label') = '30'")]
        public void AFilterIsReadByItsOwnContainerBesideAnother(string where)
        {
            var typed = Undeclared();
            var kinds = ThreeKinds();

            var alone = Planning.CosmosPushdownProbeTests.Plan($"""SELECT c."id" FROM typed AS c WHERE {where}""", typed);
            var beside = Planning.CosmosPushdownProbeTests.Plan($"""SELECT c."id" FROM typed AS c WHERE {where} UNION ALL SELECT k."id" FROM kinds AS k""", typed, kinds);

            PlanText(beside).Should().NotContain(KeepsNothing, "kinds' declaration says nothing of typed: " + PlanText(beside));
            Sent(beside, "typed").Should().Equal(Sent(alone, "typed"), "typed's filter is typed's: " + PlanText(beside));
            Sent(alone, "typed").Should().ContainSingle().Which.Should().Contain("WHERE", PlanText(alone));
        }

        /// <summary>
        /// A filter Calcite reads as never true, a null test of a predicate that is never null, keeps nothing over a
        /// container that declares nothing too, and reads it not at all: it is reduced as the oracle's contradiction is.
        /// </summary>
        [Theory]
        [InlineData("(c.\"$.category\" IS NULL) IS NULL")]
        [InlineData("(c.\"id\" IN ('huge', 'b')) IS NULL")]
        [InlineData("(NOT (c.\"id\" = 'BIKES')) IS NULL")]
        public void AFilterCalciteReadsAsNeverTrueKeepsNothing(string where)
        {
            var products = new CosmosContainerMetadata("products", new[] { "/category" });
            var planned = Planning.CosmosPushdownProbeTests.Plan($"SELECT c.\"id\" FROM products AS c WHERE {where}", products);

            PlanText(planned).Should().Contain(KeepsNothing, PlanText(planned));
        }

        /// <summary>
        /// And the other way round: a filter over the declared container plans by its declaration beside one that declares
        /// nothing, the empty relation where the declaration admits no such document.
        /// </summary>
        [Fact]
        public void ADeclaredContainersFilterIsReadByItsDeclarationBesideAnother()
        {
            var typed = Undeclared();
            var kinds = ThreeKinds();

            var beside = Planning.CosmosPushdownProbeTests.Plan($"""SELECT k."id" FROM kinds AS k WHERE JSON_VALUE(k."DOC", '$.kind') = 'd' UNION ALL SELECT c."id" FROM typed AS c""", typed, kinds);

            PlanText(beside).Should().Contain(KeepsNothing, "no kind is d: " + PlanText(beside));
            Sent(beside, "typed").Should().Equal(new[] { "SELECT VALUE { \"id\": c.id } FROM typed c" }, PlanText(beside));
        }

    }

}
