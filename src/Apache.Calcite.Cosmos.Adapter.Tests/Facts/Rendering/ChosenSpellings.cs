using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The spelling S chooses of each computed place a read predicate holds (<see cref="RenderingBundle.SpellingOf"/>), for a
    /// test that prints a form it built over one: the predicate written beneath the registry's S and run, nothing imported,
    /// since which spelling a call takes reads its literals alone (G6).
    /// </summary>
    internal static class ChosenSpellings
    {

        /// <summary>S's choice of each computed call <paramref name="read"/> holds, under the set given.</summary>
        public static Func<SqlValue.Call, long?> Of(SqlPredicate read, RenderingSet set)
        {
            using var bundle = ProbeRows.ProbeTheory.Open(set);
            bundle.Write(read);
            bundle.Run();
            var chosen = new Dictionary<SqlValue.Call, long?>(ReferenceEqualityComparer.Instance);
            foreach (var call in read.Computed)
                chosen.TryAdd(call, bundle.SpellingOf(call));

            return call => chosen.TryGetValue(call, out var spelling) ? spelling : null;
        }

    }

}
