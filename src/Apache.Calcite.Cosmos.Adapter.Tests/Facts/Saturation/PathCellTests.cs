using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The cells an import is cut into (<see cref="PathCell.Split"/>): every value the import admits lies in exactly one,
    /// and every cell holds one the import admits.
    /// </summary>
    /// <remarks>
    /// Imports are drawn from population D's values and edges, with classes of characters, and cut at its literals and at
    /// strings where the two orders part. A cell is asked of population D's values, the strings of every band, and what
    /// lies either side of each cut.
    /// </remarks>
    public sealed class PathCellTests
    {

        const int Trials = 1000;

        static readonly string[] Scalars = Declaration.Values.Where(v => v.IsScalar).Select(v => v.Encoded).Concat(Declaration.BandedStrings.Select(Of)).ToArray();

        static readonly decimal[] Edges = { -1m, 0m, 2.5m, 7m, 10m, 30m };

        static readonly string[] Cuts =
        {
            Of(""), Of("30"), Of("bikes"), Of("x"), Of("true"), Of(""), Of("😀"), Of("�"),
            Of(-1m), Of(0m), Of(2.5m), Of(7m), Of(30m), Of(1e30), Of(true), NullValue,
        };

        /// <summary>Every value an import admits lies in exactly one of its cells, and a value it refuses in none.</summary>
        [Fact]
        public void CellsCoverTheImportAndDoNotOverlap()
        {
            var faults = new List<string>();
            foreach (var (facts, cuts) in Cases())
            {
                var cells = PathCell.Split(facts, cuts).Cells;
                foreach (var value in Values(cuts))
                {
                    var holding = cells.Where(c => c.Contains(Encode(value))).ToList();
                    if (holding.Count != (Admitted(facts, value) ? 1 : 0))
                        faults.Add($"{Show(value)} lies in [{string.Join("; ", holding)}] of {Show(facts, cuts)}");
                }
            }

            faults.Should().BeEmpty();
        }

        /// <summary>A split keeps a cell for every value the import admits; the plant drops the absent path's.</summary>
        [Fact]
        public void SplitDropsNothingTheImportAdmits()
        {
            static List<string> Dropped(Func<PathFacts, ValueSet<CutPoint>, CellPartition> split) => Cases().SelectMany(c =>
            {
                var cells = split(c.Facts, c.Cuts).Cells;
                return Values(c.Cuts).Where(v => Admitted(c.Facts, v) && cells.Any(cell => cell.Contains(Encode(v))) == false).Select(v => $"{Show(v)} of {Show(c.Facts, c.Cuts)}");
            }).ToList();

            Dropped(PathCell.Split).Should().BeEmpty();
            Dropped((facts, cuts) =>
            {
                var planted = PathCell.Split(facts, cuts);
                return planted with { Cells = planted.Cells.Where(c => c is not PathCell.Absent) };
            }).Should().NotBeEmpty("the plant drops the absent path's cell");
        }

        /// <summary>Every cell's witness lies in it, and the import admits it; the plant keeps the strings below <c>""</c>.</summary>
        [Fact]
        public void EveryCellIsInhabited()
        {
            static List<string> Uninhabited(Func<PathFacts, ValueSet<CutPoint>, CellPartition> split) => Cases()
                .SelectMany(c => split(c.Facts, c.Cuts).Cells.Where(cell => cell.Contains(cell.Witness()) == false || Admitted(c.Facts, Decode(cell.Witness())) == false)
                    .Select(cell => $"{cell}'s witness {cell.Witness() ?? "absent"}, of {Show(c.Facts, c.Cuts)}"))
                .ToList();

            Uninhabited(PathCell.Split).Should().BeEmpty();
            Uninhabited((facts, cuts) =>
            {
                var planted = PathCell.Split(facts, cuts);
                return facts.Values.Kinds.Contains(StringType) && facts.Values.Values is null
                    ? planted with { Cells = planted.Cells.Add(new PathCell.Strings(null, Of(""), ValueSet<string>.Empty, facts.Classes)) }
                    : planted;
            }).Should().NotBeEmpty("the plant keeps the interval below the empty string");
        }

        /// <summary>Imports, classes and cuts, drawn as population D draws a declaration: from its values and edges.</summary>
        static IEnumerable<(PathFacts Facts, ValueSet<CutPoint> Cuts)> Cases()
        {
            var random = new Random(20261020);
            T[] Some<T>(T[] from, int about) => from.Where(_ => random.Next(from.Length) < about).ToArray();

            for (var i = 0; i < Trials; i++)
            {
                var x = PathValues.OfKinds(ValueSet.From(PathValues.AllKinds.Where(_ => random.Next(4) > 0)));
                if (random.Next(4) == 0)
                    x = PathValues.Meet(x, PathValues.OneOf(ValueSet.From(Some(Scalars, 3))));
                if (random.Next(2) == 0)
                    x = PathValues.Meet(x, PathValues.Not(Scalars[random.Next(Scalars.Length)]));
                if (random.Next(3) == 0)
                    x = PathValues.Meet(x, PathValues.Above(new Bound(Edges[random.Next(Edges.Length)], random.Next(2) == 0)));
                if (random.Next(3) == 0)
                    x = PathValues.Meet(x, PathValues.Below(new Bound(Edges[random.Next(Edges.Length)], random.Next(2) == 0)));

                var classes = ValueSet.From(AllBands.Where(_ => random.Next(2) == 0));
                yield return (new PathFacts("$[\"a\"]", x, classes), ValueSet.From(Some(Cuts, 4).Select(c => new CutPoint(c, CutKind.Literal))));
            }
        }

        /// <summary>
        /// The values a case's cells are asked of: an absent path, population D's, the strings of every band, and each
        /// cut with what lies beside it — a unit more or less, U+0000 or a letter appended, a number one or a half away.
        /// </summary>
        static IEnumerable<J?> Values(ValueSet<CutPoint> cuts)
        {
            var values = new List<J?> { null };
            values.AddRange(Declaration.Values);
            values.AddRange(Declaration.BandedStrings.Select(s => new J.Str(s)));
            foreach (var cut in cuts.Select(c => c.Value))
            {
                if (IsString(cut) && StringOf(cut) is var t)
                    values.AddRange(new[] { t, t + "\0", t + "a" }.Concat(t.Length == 0 ? Array.Empty<string>() : new[] { t[..^1], t[..^1] + (char)(t[^1] + 1), t[..^1] + (char)(t[^1] - 1) })
                        .Select(s => new J.Str(s)));

                if (TryNumberOf(cut, out var c))
                    values.AddRange(new[] { c, c - 1, c + 1, c - 0.5m, c + 0.5m, c + 0.0m }.Select(n => new J.Num(n)));
            }

            return values;
        }

        /// <summary>A value as a cell holds it: in <c>JsonValues</c>' spelling, a structure as its type, absent as null.</summary>
        internal static string? Encode(J? value) => value switch
        {
            null => null,
            J.Obj => ObjectType + "|",
            J.Arr => ArrayType + "|",
            _ => value.Encoded,
        };

        /// <summary>A value back from a cell's spelling, a structure empty.</summary>
        internal static J? Decode(string? value) => value is null ? null : TypeOf(value) switch
        {
            ObjectType => new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal)),
            ArrayType => new J.Arr(new List<J>()),
            NullType => new J.Null(),
            BooleanType => new J.Bool(value == Of(true)),
            StringType => new J.Str(StringOf(value)),
            _ => TryNumberOf(value, out var x) ? new J.Num(x) : new J.Huge(ApproximateOf(value)!.Value),
        };

        /// <summary>Whether an import admits a value: one its path may hold, and a string whose characters its classes allow.</summary>
        internal static bool Admitted(PathFacts facts, J? value) =>
            PopulationD.Admits(facts.Values, value) && (value is not J.Str s || BandsOfText(s.Value).IsSubsetOf(facts.Classes));

        internal static string Show(J? value) => value is null ? "absent" : value.ToString();

        static string Show(PathFacts facts, ValueSet<CutPoint> cuts) =>
            $"{facts.Values} in [{string.Join(",", facts.Classes.AsEnumerable())}] cut at {string.Join(", ", cuts.Select(c => c.Value))}";

    }

}
