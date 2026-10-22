using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Source A (spec §5.5): up to 64 documents from what a check imported, each path set to values its import admits —
    /// the literals and their neighbours, the text other values render as, strings holding a character of each band beside
    /// each literal, numbers either side of 2^53, and every kind — the paths' values mixed so that each value of each path
    /// stands in one document at least.
    /// </summary>
    internal static class RenderingDocumentSampler
    {

        public const int Most = 64;

        static readonly string[] Texts = ["", "30", "30.0", "true"], Marks = ["", "�", "\U0001F600"];

        static readonly decimal[] Numbers = [30m, 30.0m, 9007199254740992m, 9007199254740993m, -1m, 2.5m];

        /// <summary>The documents, each set where every path's value is one its import admits.</summary>
        public static IEnumerable<J.Obj> Sample(RenderingMemo.Claimed claim)
        {
            var literals = claim.Conjuncts.SelectMany(Literals).Concat(claim.Form.Literals).Distinct().ToList();
            var typed = claim.Conjuncts.Any(c => c.ToString()!.Contains("typed(", StringComparison.Ordinal) || c.ToString()!.Contains("cast(", StringComparison.Ordinal));
            var paths = claim.Imports.Keys.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToList();
            var values = paths.Select(p => Values(literals, typed).Where(v => PathCellTests.Admitted((p, claim.Imports[p].Values, claim.Imports[p].Chars), v)).DistinctBy(Show).ToList()).ToList();
            if (values.Any(v => v.Count == 0))
                yield break;

            // Every combination where they are few; otherwise each path's values in turn, then drawn.
            var product = values.Aggregate(1L, (n, v) => Math.Min(n * v.Count, Most + 1));
            var radixes = values.Select((_, i) => values.Take(i).Aggregate(1L, (n, v) => Math.Min(n * v.Count, Most + 1))).ToList();
            var (widest, random, seen) = (values.Max(v => v.Count), new Random(claim.Form.Text.Length * 31 + paths.Count), new HashSet<string>());
            for (var j = 0; j < Most * 4 && seen.Count < Math.Min(Most, product); j++)
            {
                var picked = values.Select((v, i) => product <= Most ? v[(int)(j / radixes[i] % v.Count)] : j < widest ? v[j % v.Count] : v[random.Next(v.Count)]).ToList();
                var document = Document(paths, picked);
                if (paths.All(p => PathCellTests.Admitted((p, claim.Imports[p].Values, claim.Imports[p].Chars), CosmosEval.At(document, p))) && seen.Add(Documents.Json(document)))
                    yield return document;
            }
        }

        static IEnumerable<J?> Values(IReadOnlyList<string> literals, bool typed)
        {
            J?[] kinds = [null, new J.Null(), new J.Bool(true), new J.Bool(false), new J.Obj(new(StringComparer.Ordinal)), new J.Arr([])];
            var strings = Texts.Concat(Marks).Concat(literals.Where(IsString).Select(StringOf).SelectMany(s =>
                new[] { s, s + "\0", s + "a" }.Concat(s.Length > 0 ? [s[..^1]] : []).Concat(Marks.Select(m => s + m))));
            var numbers = Numbers.Concat(typed ? [7m, 3_000_000_000m] : []).Concat(literals.Where(IsExactNumber).Select(NumberOf).SelectMany(c =>
                new[] { c, c - 1, c + 1, c + 0.5m, c * 1.0m }));
            return kinds.Concat(strings.Select(s => (J?)new J.Str(s))).Concat(numbers.Select(n => (J?)new J.Num(n)));
        }

        /// <summary>The literals a predicate compares with.</summary>
        static IEnumerable<string> Literals(SqlPredicate predicate) => predicate.Subterms.SelectMany(term => term switch
        {
            SqlPredicate.Compare { Right: SqlValue.Literal l } => [l.Value],
            SqlPredicate.In i => i.Points.AsEnumerable(),
            _ => [],
        });

        /// <summary>A document holding each value at its path, an absent one left out, a path beneath a scalar not set.</summary>
        internal static J.Obj Document(IReadOnlyList<string> paths, IReadOnlyList<J?> values)
        {
            var root = new J.Obj(new(StringComparer.Ordinal));
            for (var i = 0; i < paths.Count; i++)
            {
                if (values[i] is not { } value)
                    continue;

                var (names, current) = (CosmosEval.PathOf(paths[i]).Names.ToList(), root.Properties);
                foreach (var name in names.Take(names.Count - 1))
                {
                    if (current.TryGetValue(name, out var child) == false)
                        current[name] = child = new J.Obj(new(StringComparer.Ordinal));
                    if (child is not J.Obj o)
                        goto next;
                    current = o.Properties;
                }

                current[names[^1]] = value;
            next:;
            }

            return root;
        }

        static string Show(J? value) => value is null ? "absent" : Documents.Json(value);

    }

}
