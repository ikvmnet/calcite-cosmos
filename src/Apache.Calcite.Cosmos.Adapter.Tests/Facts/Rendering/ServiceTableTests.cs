using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A table is equal to another by its entries: Eggnog interns a value by its equality, and the probe's table, equal by
    /// its size, would have let a bundle read another set's rows.
    /// </summary>
    public sealed class ServiceTableTests
    {

        static List<string> Faults(Func<ServiceTable, ServiceTable, bool> equal, Func<ServiceTable, int> hash)
        {
            var random = new Random(20_261_020);
            var entries = ProbeRows.Table.Entries;
            var faults = new List<string>();
            for (var trial = 0; trial < 50; trial++)
            {
                var kept = entries.Where(_ => random.Next(2) == 0).ToList();
                var changed = kept[random.Next(kept.Count)];
                var other = kept.Select(e => e == changed ? e with { Answers = e.Answers.Add(CosmosAnswer.Fails), Outcomes = e.Outcomes | ServiceOutcomes.Fails } : e).ToList();

                var (a, again, b) = (ServiceTable.Of(ValueSet.From(kept)), ServiceTable.Of(ValueSet.From(kept.ToArray())), ServiceTable.Of(ValueSet.From(other)));
                if (equal(a, again) == false || hash(a) != hash(again))
                    faults.Add($"a table of {kept.Count} entries is not equal to itself built again");
                if (equal(a, b))
                    faults.Add($"two tables of {kept.Count} entries differing at {changed.Key} are equal");
            }

            return faults;
        }

        [Fact]
        public void TablesAreEqualByContent()
        {
            Faults((a, b) => a.Equals(b), t => t.GetHashCode()).Should().BeEmpty();
            Faults((a, b) => a.Entries.Count == b.Entries.Count, t => t.Entries.Count).Should().NotBeEmpty("the plant compares tables by their size, as the probe's did");

            // Interned, two tables of one size are two values, and one built again is the same value.
            var graph = new EGraph();
            graph.Values.Register<ServiceTable>("service table");
            var table = graph.DefineFunction<string, ServiceTable>("table");
            var entries = ProbeRows.Table.Entries;
            var first = entries.First();
            using var scope = graph.Root.CreateChild();
            scope.Assume(table.Of("t").Is(ServiceTable.Of(entries)));
            scope.Assume(table.Of("t").Is(ServiceTable.Of(ValueSet.From(entries.ToArray()))));
            scope.Invoking(s => s.Assume(table.Of("t").Is(ServiceTable.Of(entries.Remove(first).Add(first with { Evidence = ValueSet.Of("Service.Planted") })))))
                .Should().Throw<FactConflictException>();
        }

    }

}
