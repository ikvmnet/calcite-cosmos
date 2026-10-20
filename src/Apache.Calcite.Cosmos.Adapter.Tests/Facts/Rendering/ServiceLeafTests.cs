using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// One service test on each cell (<see cref="ServiceLeaf.On"/>): nothing without a row, never nothing at all, and the
    /// two string orders kept apart where a literal parts them.
    /// </summary>
    public sealed class ServiceLeafTests
    {

        static readonly string S = SqlValue.Spell(JsonDocumentPath.Root.Property("s"));

        static readonly string High = Of(char.ConvertFromUtf32(0xE000));

        static readonly string Emoji = Of("\U0001F600");

        static readonly string[] Literals = [Of("bikes"), Of(""), High, Emoji, Of(30m), Of(2.5m), Of(true), NullValue];

        static readonly ServiceTable Empty = ServiceTable.Of(ValueSet<MeasuredEntry>.Empty);

        static IEnumerable<ServiceForm> Leaves()
        {
            foreach (var test in Enum.GetValues<CosmosServiceTest>().Where(test => test <= CosmosServiceTest.IsArray))
                yield return ServiceForm.TestOf(test, S);
            foreach (var op in new[] { CosmosServiceTest.Equal, CosmosServiceTest.NotEqual, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater, CosmosServiceTest.GreaterOrEqual })
                foreach (var literal in Literals)
                    yield return ServiceForm.CompareOf(op, S, literal);

            yield return ServiceForm.InOf(S, ValueSet.Of(Of("bikes"), Of(30m)));
            yield return ServiceForm.BareOf(S);
            yield return ServiceForm.ConstantOf(true);
            yield return ServiceForm.OpaqueOf("STARTSWITH(c.s, 'b')", ValueSet.Of(S));
        }

        /// <summary>Every kind of cell: an undeclared path, its strings, and integers bounded within a double's exact range.</summary>
        static IEnumerable<CellPartition> Partitions()
        {
            var cuts = ValueSet.From(Literals.Select(l => new CutPoint(l, CutKind.Literal)));
            var integers = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Meet(PathValues.Above(Bound.AtLeast(-10)), PathValues.Below(Bound.AtMost(100))));
            foreach (var classes in new[] { AllBands, NoBand })
                foreach (var import in new[] { PathValues.Anything, PathValues.OfKinds(ValueSet.Of(StringType)), integers })
                    yield return PathCell.Split(new PathFacts(S, import, classes), cuts);
        }

        static IEnumerable<ValueSet<string>> Parted() => [ValueSet<string>.Empty, ValueSet.From(Literals.Where(IsString))];

        static MeasuredOutcome Row(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell, CosmosAnswer answer) =>
            new("Service.Planted", MeasuredKey.Of(test, operand, cell), RenderingTarget.Emulator, answer);

        static ServiceOutcomes At(OutcomeVector vector, string text) => vector.Outcomes[vector.Cells.Cells.ToList().FindIndex(c => c.Text == text)].Outcomes;

        /// <summary>No branch answers anything a row does not say: with no rows, every leaf answers everything on every cell.</summary>
        [Fact]
        public void AnEmptyTableAnswersWidestEverywhere()
        {
            var narrower = from leaf in Leaves()
                           from cells in Partitions()
                           from parted in Parted()
                           let vector = ServiceLeaf.On(leaf, cells, Empty, parted)
                           from i in Enumerable.Range(0, vector.Outcomes.Count)
                           where vector.Outcomes[i].Outcomes != ServiceOutcomes.Widest
                           select $"{leaf} answers {vector.Outcomes[i].Outcomes} on {cells.Cells[i]}";

            narrower.Should().BeEmpty();
        }

        /// <summary>Whatever the rows, a cell answers something: the probe's, and random tables cut from them and garbled.</summary>
        [Fact]
        public void NeverEmpty()
        {
            var random = new Random(20_261_020);
            var rows = ProbeRows.Rows.Append(Row(CosmosServiceTest.Order, CosmosValueClass.Number, CosmosValueClass.None, CosmosAnswer.Numeric))
                .Concat(new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }.Select(side => Row(CosmosServiceTest.Less, CosmosValueClass.Number, side, CosmosAnswer.True))).ToList();
            var answers = Enum.GetValues<CosmosAnswer>();

            var tables = new List<ServiceTable> { ProbeRows.Table, ProbeRows.TableOf(rows) };
            for (var trial = 0; trial < 40; trial++)
                tables.Add(ProbeRows.TableOf(rows.Where(_ => random.Next(4) > 0).Select(row => random.Next(3) > 0 ? row : row with { Answer = answers[random.Next(answers.Length)] })));

            var empty = from table in tables
                        from leaf in Leaves()
                        from cells in Partitions()
                        from parted in Parted()
                        let vector = ServiceLeaf.On(leaf, cells, table, parted)
                        from i in Enumerable.Range(0, vector.Outcomes.Count)
                        where vector.Outcomes[i].Outcomes == ServiceOutcomes.None
                        select $"{leaf} answers nothing on {cells.Cells[i]}";

            empty.Should().BeEmpty();
        }

        /// <summary>A key no row settles may fail the statement; one a row settles fails only where the row says so.</summary>
        [Fact]
        public void WidestIncludesFails()
        {
            ServiceOutcomes.Widest.Should().HaveFlag(ServiceOutcomes.Fails);

            var strings = PathCell.Split(new PathFacts(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands), ValueSet.Of(new CutPoint(Of("bikes"), CutKind.Literal)));
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, Of("bikes")), strings, ProbeRows.Table, ValueSet<string>.Empty).Outcomes
                .Should().OnlyContain(o => o.Outcomes == ServiceOutcomes.Widest, "the probe's rows hold no ordering");
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S, Of("bikes")), strings, ProbeRows.Table, ValueSet<string>.Empty).Outcomes
                .Should().NotContain(o => o.Outcomes.HasFlag(ServiceOutcomes.Fails));
        }

        /// <summary>
        /// U+1F600 is below U+E000 by UTF-16 unit, Calcite's order and the cells', and above it by code point, the service's.
        /// So an interval cut by unit lies to one side of U+E000 for the service only where the literal does not part the
        /// orders over the path; where it does, the interval answers both sides.
        /// </summary>
        [Fact]
        public void PartedLiteralsAnswerBothOrders()
        {
            var table = Less(CosmosAnswer.CodePoint, CosmosAnswer.True);
            var cells = PathCell.Split(new PathFacts(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands), ValueSet.Of(new CutPoint(High, CutKind.Literal), new CutPoint(Emoji, CutKind.Literal)));
            var less = ServiceForm.CompareOf(CosmosServiceTest.Less, S, High);

            var whole = ServiceLeaf.On(less, cells, table, ValueSet<string>.Empty);
            At(whole, Emoji).Should().Be(ServiceOutcomes.False, "by code point U+1F600 is above U+E000");
            At(whole, new PathCell.Strings(Emoji, High, ValueSet<string>.Empty, AllBands).Text).Should().Be(ServiceOutcomes.True, "unparted, the interval is placed by its bounds");

            var parted = ServiceLeaf.On(less, cells, table, ValueSet.Of(High));
            cells.Cells.Select((cell, i) => (cell, parted.Outcomes[i].Outcomes)).Where(c => c.cell is PathCell.Strings)
                .Should().OnlyContain(c => c.Outcomes == (ServiceOutcomes.True | ServiceOutcomes.False));
            At(parted, Emoji).Should().Be(ServiceOutcomes.False);
            At(parted, High).Should().Be(ServiceOutcomes.False);
        }

        /// <summary>An entry of two answers means both; an order of two comparators places a cell by each.</summary>
        [Fact]
        public void AMultiValuedEntryAnswersTheUnion()
        {
            var table = Less(CosmosAnswer.CodePoint, CosmosAnswer.Utf16, CosmosAnswer.True, CosmosAnswer.NotKept);
            table.TryGet(MeasuredKey.Of(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Below), out var below).Should().BeTrue();
            below!.Outcomes.Should().Be(ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined);

            var cells = PathCell.Split(new PathFacts(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands), ValueSet.Of(new CutPoint(High, CutKind.Literal), new CutPoint(Emoji, CutKind.Literal)));
            var vector = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, High), cells, table, ValueSet<string>.Empty);
            At(vector, Emoji).Should().Be(ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined, "by code point it is above, by unit below");
            At(vector, new PathCell.Strings(null, Emoji, ValueSet<string>.Empty, AllBands).Text).Should().Be(ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined);

            // What an order entry answers beside its comparators stands beside every cell it places.
            var failing = ProbeRows.TableOf(new[] { CosmosAnswer.CodePoint, CosmosAnswer.Fails }.Select(a => Row(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None, a))
                .Concat(new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }.Select(side => Row(CosmosServiceTest.Less, CosmosValueClass.String, side, CosmosAnswer.True))));
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, High), cells, failing, ValueSet<string>.Empty).Outcomes
                .Should().OnlyContain(o => o.Outcomes == (ServiceOutcomes.True | ServiceOutcomes.Fails));
        }

        /// <summary>A key the table misses is named among the rows, and answers everything where it is read.</summary>
        [Fact]
        public void MissingKeysAreNamed()
        {
            var missing = MeasuredKey.Of(CosmosServiceTest.NotEqual, CosmosValueClass.String, CosmosValueClass.Null);
            var table = ProbeRows.TableOf(ProbeRows.Rows.Where(row => row.Target != RenderingTarget.Calcite && row.Key != missing));
            var cells = PathCell.Split(new PathFacts(S, PathValues.Anything, AllBands), ValueSet.Of(new CutPoint(Of("bikes"), CutKind.Literal)));

            var vector = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S, Of("bikes")), cells, table, ValueSet<string>.Empty);
            vector.Rows.Where(row => row.StartsWith("missing:")).Should().Equal("missing:NotEqual/String/Null");
            At(vector, "null").Should().Be(ServiceOutcomes.Widest);
            At(vector, "absent").Should().Be(ServiceOutcomes.Undefined);

            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, Of("bikes")), cells, table, ValueSet<string>.Empty).Rows.Where(row => row.StartsWith("missing:"))
                .Should().BeEquivalentTo(new[] { "Below", "Equal", "Above", "Absent", "Null", "True", "False", "Number", "Object", "Array" }.Select(cell => "missing:Less/String/" + cell));
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S, Of("bikes")), cells, Empty, ValueSet<string>.Empty).Rows.Should().Contain("missing:Order/String/None");
        }

        /// <summary>A table of <c>&lt;</c> against a string, ordered by the comparators, below the literal answering the rest.</summary>
        static ServiceTable Less(params CosmosAnswer[] comparatorsThenBelow)
        {
            var comparators = comparatorsThenBelow.Where(a => a is CosmosAnswer.CodePoint or CosmosAnswer.Utf16).ToList();
            var below = comparatorsThenBelow.Except(comparators).ToList();
            return ProbeRows.TableOf(comparators.Select(c => Row(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None, c))
                .Concat(below.Select(a => Row(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Below, a)))
                .Append(Row(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Equal, CosmosAnswer.False))
                .Append(Row(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Above, CosmosAnswer.False)));
        }

    }

}
