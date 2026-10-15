using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The registry holds only what has a measurement behind it.
    /// </summary>
    /// <remarks>
    /// The measurement is the axiom's evidence, and a row whose test has been renamed or deleted is a
    /// claim nobody is checking any more. So every row's test is looked up here, by name, and has to
    /// be a test.
    /// </remarks>
    public class CosmosAxiomsTests
    {

        const string Root = "Apache.Calcite.Cosmos.Adapter.Tests.";

        [Fact]
        public void EveryAxiomNamesAMeasurementThatExists()
        {
            CosmosAxioms.All.Should().NotBeEmpty();

            var missing = new List<string>();

            foreach (var axiom in CosmosAxioms.All)
            {
                var dot = axiom.PinnedBy.LastIndexOf('.');
                var type = dot < 0 ? null : typeof(CosmosAxiomsTests).Assembly.GetType(Root + axiom.PinnedBy[..dot]);
                var method = type?.GetMethod(axiom.PinnedBy[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance);

                // A theory is a fact to xUnit, so this asks for either.
                if (method is null || method.IsDefined(typeof(FactAttribute), inherit: true) == false)
                    missing.Add($"{axiom.Id}: {axiom.PinnedBy}");
            }

            missing.Should().BeEmpty("every axiom names the test that measures it, and the test must exist");
        }

        [Fact]
        public void EveryAxiomHasItsOwnName()
        {
            CosmosAxioms.All.Select(a => a.Id).Should().OnlyHaveUniqueItems();

            foreach (var axiom in CosmosAxioms.All)
                axiom.Id.Should().MatchRegex(@"^(Service|Calcite)\.[A-Z][A-Za-z]+$", "an id says what was measured and then what it says");
        }

        /// <summary>
        /// A <c>Service.</c> row was measured against an emulator or an account, and a <c>Calcite.</c>
        /// row against neither; each says when.
        /// </summary>
        [Fact]
        public void EveryAxiomSaysWhereAndWhen()
        {
            foreach (var axiom in CosmosAxioms.All)
            {
                if (axiom.Id.StartsWith("Calcite.", StringComparison.Ordinal))
                    axiom.MeasuredOn.Should().Be(CosmosAxiomTarget.Calcite, axiom.Id);
                else
                    (axiom.MeasuredOn & ~(CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure)).Should().Be((CosmosAxiomTarget)0, axiom.Id);

                axiom.MeasuredOn.Should().NotBe((CosmosAxiomTarget)0, axiom.Id);
                axiom.Measured.Should().BeOnOrAfter(new DateOnly(2026, 1, 1), axiom.Id);
                axiom.Measured.Should().BeOnOrBefore(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), axiom.Id);
                axiom.Statement.Should().EndWith(".", axiom.Id);
            }
        }

    }

}
