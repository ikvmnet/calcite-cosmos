using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The facts graph's readings table (G7): what the readings vocabulary settles at the root from the registry's rows,
    /// which the atom rules bind beside an atom, so a test that asks an atom directly reads it as the graph does; and the
    /// JSON kinds it keys each type test by.
    /// </summary>
    internal static class FactsReadings
    {

        static readonly Lazy<(ReadingTable Rows, IReadOnlyList<(string Test, ValueSet<string> Kinds)> TestKinds)> Root = new(() =>
        {
            var contexts = new JsonContexts(CosmosReasoning.Rows);
            var root = contexts.Graph.Root;
            var kinds = (from test in Enum.GetValues<CosmosServiceTest>()
                         let name = ServiceForm.SpellingOf(test)
                         where name is not null && root.TryGet(contexts.Rules.TestKinds.Of(name), out _)
                         select (name, root.Get(contexts.Rules.TestKinds.Of(name)))).ToList();
            return (root.Get(contexts.Rules.Readings.ReaderRows.Of()), kinds);
        });

        /// <summary>Every reading's rows at the facts graph's root.</summary>
        public static ReadingTable Rows => Root.Value.Rows;

        /// <summary>Each type test's name beside the JSON kinds the facts graph's root holds it TRUE of.</summary>
        public static IReadOnlyList<(string Test, ValueSet<string> Kinds)> TestKinds => Root.Value.TestKinds;

    }

}
