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
    /// The raw columns' union as S's rule (spec h12): S joins every column's row on a key, which the registry's row reads
    /// one row a column a key, and a raw reading's cells read the union, deciding on the cell only what a column comparing
    /// the value as its type answers. The plant that drops a column is <c>RenderingGenerateMutationTests</c>'.
    /// </summary>
    public sealed class RenderingRawUnionTests
    {

        const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;

        /// <summary>
        /// The union S holds is, key by key, every column's truths joined, and nothing else; both lattices keep their laws
        /// over the truths and unions the row gives.
        /// </summary>
        [Fact]
        public void TheUnionIsEveryKeysColumnsJoined()
        {
            var rows = CosmosAxiomRows.ReadColumns(CosmosAxioms.All);
            var union = ProbeRows.Columns;
            var keys = rows.GroupBy(r => r.Key).ToList();
            union.Count.Should().Be(keys.Count);
            foreach (var key in keys)
                union[key.Key].Should().Be(key.Aggregate(Truths.None, (t, r) => t | r.Truths), key.Key.ToString());

            RawTruths.TruthsUnion.Verify(Enumerable.Range(0, 16).Select(t => (Truths)t));
            RawTruths.Union.Verify([RawTruths.Empty, union, .. rows.Take(40).Select(r => RawTruths.Of(r.Key, r.Truths))]);
            GC.KeepAlive(ProbeRows.ProbeTheory);
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
            Truths On(PredicateAtom atom, PathCell cell) => atom.OnCell(cell, columns);
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

        /// <summary>A definition naming no column leaves the union empty, and a raw cell then reads anything and a raise.</summary>
        [Fact]
        public void NoColumnReadsAnythingAndARaise()
        {
            var theory = RenderingTheory.Define(ProbeRows.Measured with { Columns = [] });
            var empty = theory.Theory.Get(theory.RawUnion.Of());
            empty.Should().Be(RawTruths.Empty);
            PredicateAtom.Compare(PredicateAtom.RawReader, "=", Of("b")).OnCell(new PathCell.Point(Of("b")), empty).Should().Be(T | U | R);
            GC.KeepAlive(theory);
        }

    }

}
