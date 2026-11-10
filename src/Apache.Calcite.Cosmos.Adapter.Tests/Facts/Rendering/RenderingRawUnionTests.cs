using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The raw columns' union as S's rules (spec h12, G5): S derives each column's answer on a key from the registry's row,
    /// read one fact a field, and joins the columns; a raw reading's cells read the union, deciding on the cell only what a
    /// column comparing the value as one type with the literal leaves to it. The plant that drops a column is
    /// <c>RenderingGenerateMutationTests</c>'.
    /// </summary>
    public sealed class RenderingRawUnionTests
    {

        const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;

        /// <summary>
        /// The union S holds is, key by key, every measured column's answer joined, and beside it each column's own under the
        /// key of the reading through that column alone (rawtyped), each S's rules' answer for that column, and nothing else:
        /// every class a column states, an absent path its own (F3), beside each literal it is compared beside. A column no
        /// row measured (<see cref="PredicateAtom.UnionColumns"/>, a <c>CHAR</c>'s) answers each key as the union does. Both
        /// lattices keep their laws over the answers and unions S gives.
        /// </summary>
        [Fact]
        public void TheUnionIsEveryKeysColumnsJoined()
        {
            var s = ProbeRows.ProbeTheory;
            var union = ProbeRows.Columns;
            var columns = union.Entries.Where(e => PredicateAtom.ColumnOf(e.Key.Reader) is not null).ToList();
            var measured = columns.Where(e => PredicateAtom.RawColumns.Contains(PredicateAtom.ColumnOf(e.Key.Reader)!)).ToList();
            var joined = union.Entries.Where(e => PredicateAtom.ColumnOf(e.Key.Reader) is null).ToList();
            static RawKey Columnless(RawKey key) => key with { Reader = PredicateAtom.Columnless(key.Reader) };

            // Each column's reading: 8 classes, its null test and five comparisons beside each literal it is beside.
            columns.GroupBy(e => e.Key.Reader).ToDictionary(g => g.Key, g => g.Count()).Should().Equal(new System.Collections.Generic.Dictionary<string, int>
            {
                ["raw:VARIANT"] = 288, ["raw:ANY"] = 288, ["raw:VARCHAR"] = 48,
                ["cast:VARCHAR:raw:VARIANT"] = 48, ["cast:VARCHAR:raw:ANY"] = 48, ["cast:VARCHAR:raw:VARCHAR"] = 48,
                ["raw:CHAR"] = 288, ["cast:VARCHAR:raw:CHAR"] = 48,
            });
            joined.Should().HaveCount(288 + 48);
            foreach (var (key, answer) in joined)
                answer.Should().Be(measured.Where(e => Columnless(e.Key) == key).Aggregate(new RawAnswer(), (a, e) => RawAnswer.Join(a, e.Value)), key.ToString());
            foreach (var (key, answer) in measured)
                s.Theory.Get(s.RawColumn.Of(Columnless(key), PredicateAtom.ColumnOf(key.Reader)!)).Should().Be(answer, key.ToString());
            foreach (var (key, answer) in columns.Except(measured))
                answer.Should().Be(joined.Single(e => e.Key == Columnless(key)).Value, key + " reads as the union");

            RawTruths.AnswerUnion.Verify([.. union.Entries.Select(e => e.Value).Distinct(), RawAnswer.Unanswered]);
            RawTruths.Union.Verify([RawTruths.Empty, union, .. union.Entries.Take(40).Select(e => RawTruths.Of(e.Key, e.Value))]);
            GC.KeepAlive(s);
        }

        /// <summary>
        /// A raw reading's cell reads the union: a string against a string is placed on the cell where every column reads
        /// it, a number beside a string raises where the <c>VARCHAR</c> column reads it and is unequal where the others do,
        /// a <c>VARIANT</c>'s ordering raises; the cast of a number is its rendering through an <c>ANY</c>, null through a
        /// <c>VARIANT</c> and a raise through a <c>VARCHAR</c> column; a null is null through each.
        /// </summary>
        [Fact]
        public void ARawCellReadsTheUnion()
        {
            var columns = ProbeRows.Columns;
            var cast = PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader);
            Truths On(PredicateAtom atom, PathCell cell) => atom.OnCell(cell, columns, ProbeRows.ReaderRows);
            PathCell thirty = new PathCell.Point(Of(30m)), yes = new PathCell.Point(Of(true));

            On(PredicateAtom.Compare(PredicateAtom.RawReader, "=", Of("b")), new PathCell.Point(Of("b"))).Should().Be(T);
            On(PredicateAtom.Compare(PredicateAtom.RawReader, "=", Of("b")), thirty).Should().Be(F | R);
            On(PredicateAtom.Compare(PredicateAtom.RawReader, "=", Of(30m)), thirty).Should().Be(T | F);
            On(PredicateAtom.Compare(PredicateAtom.RawReader, "<", Of(31m)), thirty).Should().Be(T | R);
            On(PredicateAtom.Compare(cast, "=", Of("30")), thirty).Should().Be(T | F | U | R);
            On(PredicateAtom.Compare(cast, "=", Of("31")), thirty).Should().Be(F | U | R);
            On(PredicateAtom.Compare(cast, "=", Of("true")), new PathCell.Null()).Should().Be(U);
            On(PredicateAtom.IsNull(cast), yes).Should().Be(T | F | R);
            On(PredicateAtom.IsNull(PredicateAtom.RawReader), yes).Should().Be(F | R);
            On(PredicateAtom.IsNull(PredicateAtom.RawReader), new PathCell.Absent()).Should().Be(T);
        }

        /// <summary>
        /// A reading through one column reads that column's truths alone (rawtyped): a <c>VARIANT</c>'s ordering raises and an
        /// <c>ANY</c>'s places the number; a <c>VARCHAR</c> column raises over a number beside a string; the cast of a number
        /// is null through a <c>VARIANT</c> and its rendering through an <c>ANY</c>, so a negated affix over it is UNKNOWN
        /// through the one and TRUE through the other, where the union reads both.
        /// </summary>
        [Fact]
        public void AColumnsReadingReadsItsColumnAlone()
        {
            var columns = ProbeRows.Columns;
            Truths On(PredicateAtom atom, PathCell cell) => atom.OnCell(cell, columns, ProbeRows.ReaderRows);
            string Raw(string column) => PredicateAtom.RawIn(column);
            string Cast(string column) => PredicateAtom.CastReader("VARCHAR", Raw(column));
            PathCell thirty = new PathCell.Point(Of(30m)), b = new PathCell.Point(Of("b"));

            On(PredicateAtom.Compare(Raw("VARIANT"), "<", Of(31m)), thirty).Should().Be(R);
            On(PredicateAtom.Compare(Raw("ANY"), "<", Of(31m)), thirty).Should().Be(T);
            On(PredicateAtom.Compare(Raw("VARCHAR"), "=", Of("b")), thirty).Should().Be(R);
            On(PredicateAtom.Compare(Raw("VARCHAR"), "=", Of("b")), b).Should().Be(T);
            On(PredicateAtom.Compare(Cast("VARIANT"), "=", Of("30")), thirty).Should().Be(U);
            On(PredicateAtom.Compare(Cast("ANY"), "=", Of("30")), thirty).Should().Be(T | F);
            On(PredicateAtom.IsNull(Raw("VARIANT")), new PathCell.Null()).Should().Be(T);
            PredicateAtom.Swap(On(PredicateAtom.Affix(Cast("VARIANT"), PredicateAtom.LikeTest, "b%"), thirty)).Should().Be(U);
            PredicateAtom.Swap(On(PredicateAtom.Affix(Cast("ANY"), PredicateAtom.LikeTest, "b%"), thirty)).Should().Be(T);
            PredicateAtom.Swap(On(PredicateAtom.Affix(PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader), PredicateAtom.LikeTest, "b%"), thirty)).Should().Be(T | U | R);
        }

        /// <summary>
        /// A definition naming no column leaves the union empty, and a raw cell then reads anything and a raise, the lattice's
        /// top: no column says the value is compared at all, so the cell decides nothing.
        /// </summary>
        [Fact]
        public void NoColumnReadsAnythingAndARaise()
        {
            var theory = RenderingTheory.Define(ProbeRows.Measured with { Columns = RawColumnRows.None });
            var empty = theory.Theory.Get(theory.RawUnion.Of());
            empty.Should().Be(RawTruths.Empty);
            PredicateAtom.Compare(PredicateAtom.RawReader, "=", Of("b")).OnCell(new PathCell.Point(Of("b")), empty, ReadingTable.Empty).Should().Be(T | F | U | R);
            GC.KeepAlive(theory);
        }

    }

}
