using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the <c>IS_*</c> tests answer in process, where a plan evaluates one: their bodies (<c>CosmosFunctionBodies</c>),
    /// read over a document holding each kind of value at the path, and none.
    /// </summary>
    /// <remarks>
    /// The Calcite side of a type test. That the bodies answer what the service answers is
    /// <c>Service.TypeTestBodiesMatchTheService</c>; this holds the row S reads to each body over the outcome catalogue's
    /// witnesses (<see cref="CosmosOutcomeWitnesses"/>), a test the body answers false absent from the row as false.
    /// </remarks>
    public class CalciteTypeTestMeasurementTests
    {

        [Fact, CosmosPins("Calcite.TypeTestsAreTheirBodies")]
        public void EachTypeTestAnswersItsRowInProcess()
        {
            var tests = CosmosFunctionBodiesTests.Tests.ToDictionary(t => t.Name.Replace("IS_", "").Replace("_", ""), t => t.Body, System.StringComparer.OrdinalIgnoreCase);
            var wrong = from outcome in CosmosAxioms.Get("Calcite.TypeTestsAreTheirBodies").Outcomes!
                        from cell in outcome.Cells
                        from json in CosmosOutcomeWitnesses.Of(CosmosValueClass.None, cell.Cell)
                        let document = json is null ? "{}" : $"{{\"v\":{json}}}"
                        let answer = tests[outcome.Test.ToString()[2..]](document, "$.v") ? CosmosAnswer.True : CosmosAnswer.False
                        where answer != cell.Answer
                        select $"{outcome.Test} over {json ?? "absent"} is {answer}, the row {cell.Answer}";

            wrong.Should().BeEmpty();
            CosmosAxioms.Get("Calcite.TypeTestsAreTheirBodies").Outcomes!.Select(o => o.Test).Should().BeEquivalentTo(
                CosmosAxioms.All.Where(a => a.Id is "Service.IsPrimitive" or "Service.IsDefined" or "Service.IsNull" or "Service.KindTests").SelectMany(a => a.Outcomes!).Select(o => o.Test),
                "every test the service's rows answer has its Calcite side");
        }

    }

}
