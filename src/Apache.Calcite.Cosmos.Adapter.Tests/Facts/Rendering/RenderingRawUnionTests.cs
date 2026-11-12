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
        /// key of the reading through that column alone (rawtyped), each the rules' answer for that column, and nothing else:
        /// every class a column states, an absent path its own (F3), beside each literal it is compared beside, and beside a
        /// parameter, a comparison's key with no literal's class, the column's answers beside every class it is beside
        /// joined. A column no row measured (<see cref="PredicateAtom.UnionColumns"/>, a <c>CHAR</c>'s) answers a literal's
        /// key as the union does and a parameter's as the column a row says it answers one as (parameteras, a
        /// <c>VARCHAR</c>'s). Both lattices keep their laws over the answers and unions S gives.
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
            static bool Parameter(RawKey key) => key.Test != CosmosServiceTest.IsNull && key.Operand == CosmosValueClass.None;

            // Each column's reading: 8 classes, its null test and five comparisons beside each literal it is beside, and
            // the five beside a parameter.
            columns.GroupBy(e => e.Key.Reader).ToDictionary(g => g.Key, g => g.Count()).Should().Equal(new System.Collections.Generic.Dictionary<string, int>
            {
                ["raw:VARIANT"] = 328, ["raw:ANY"] = 328, ["raw:VARCHAR"] = 88,
                ["cast:VARCHAR:raw:VARIANT"] = 88, ["cast:VARCHAR:raw:ANY"] = 88, ["cast:VARCHAR:raw:VARCHAR"] = 88,
                ["raw:CHAR"] = 328, ["cast:VARCHAR:raw:CHAR"] = 88,
            });
            joined.Should().HaveCount(328 + 88);
            foreach (var (key, answer) in joined)
                answer.Should().Be(measured.Where(e => Columnless(e.Key) == key).Aggregate(new RawAnswer(), (a, e) => RawAnswer.Join(a, e.Value)), key.ToString());
            foreach (var (key, answer) in measured.Where(e => Parameter(e.Key) == false))
                s.Theory.Get(s.RawColumn.Of(Columnless(key), PredicateAtom.ColumnOf(key.Reader)!)).Should().Be(answer, key.ToString());
            // A parameter's key holds no rawcolumn row: it is the join of the column's answer beside each class it is beside.
            foreach (var (key, answer) in measured.Where(e => Parameter(e.Key)))
            {
                var column = PredicateAtom.ColumnOf(key.Reader)!;
                var beside = ProbeRows.Measured.Columns.Beside.Where(b => b.Reading == PredicateAtom.Columnless(key.Reader) && b.Column == column).Select(b => b.Operand).ToList();
                beside.Should().NotBeEmpty(key.ToString());
                answer.Should().Be(beside.Aggregate(new RawAnswer(), (a, o) => RawAnswer.Join(a, s.Theory.Get(s.RawColumn.Of(Columnless(key) with { Operand = o }, column)))), key + " beside a parameter");
            }
            var parameterAs = ProbeRows.Measured.ParametersAs.ToDictionary(p => p.Column, p => p.As);
            foreach (var (key, answer) in columns.Except(measured))
                answer.Should().Be(Parameter(key) ? union[Columnless(key).In(parameterAs[PredicateAtom.ColumnOf(key.Reader)!])] : joined.Single(e => e.Key == Columnless(key)).Value,
                    key + (Parameter(key) ? " answers a parameter as its row's column" : " reads as the union"));

            RawTruths.AnswerUnion.Verify([.. union.Entries.Select(e => e.Value).Distinct(), RawAnswer.Unanswered]);
            RawTruths.Union.Verify([RawTruths.Empty, union, .. union.Entries.Take(40).Select(e => RawTruths.Of(e.Key, e.Value))]);
            GC.KeepAlive(s);
        }

        /// <summary>
        /// Beside a parameter a raw reading answers what its column answers beside every class it is compared beside: a
        /// <c>VARIANT</c>'s ordering fails to compile over every class, an absent path among them; an <c>ANY</c>'s raises
        /// over every value, across types, and is unknown over a null; a <c>VARCHAR</c> column's equality raises over
        /// anything but a string, which the cell decides; and a <c>VARIANT</c>'s and an <c>ANY</c>'s equality raise nowhere.
        /// </summary>
        [Fact]
        public void AComparisonWithAParameterReadsItsColumnBesideEveryClass()
        {
            var columns = ProbeRows.Columns;
            Truths On(string column, CosmosValueClass kind, CosmosServiceTest test) =>
                columns[RawKey.Of(PredicateAtom.RawIn(column), kind, test, CosmosValueClass.None)].On(() => T | F);
            var nulls = new[] { CosmosValueClass.Absent, CosmosValueClass.Null };

            foreach (var kind in CosmosValueClasses.Kinds.AsEnumerable())
            {
                On("VARIANT", kind, CosmosServiceTest.Less).Should().Be(R, $"a VARIANT's ordering over {kind}");
                (On("VARIANT", kind, CosmosServiceTest.Equal) & R).Should().Be(Truths.None, $"a VARIANT's equality over {kind}");
                (On("ANY", kind, CosmosServiceTest.Equal) & R).Should().Be(Truths.None, $"an ANY's equality over {kind}");
                if (nulls.Contains(kind))
                {
                    On("ANY", kind, CosmosServiceTest.Greater).Should().Be(U, $"an ANY's ordering over {kind}");
                    On("VARCHAR", kind, CosmosServiceTest.Equal).Should().Be(U, $"a VARCHAR column's equality over {kind}");
                }
                else
                {
                    (On("ANY", kind, CosmosServiceTest.Greater) & R).Should().Be(R, $"an ANY's ordering over {kind}");
                    On("VARCHAR", kind, CosmosServiceTest.Equal).Should().Be(kind == CosmosValueClass.String ? T | F : R, $"a VARCHAR column's equality over {kind}");
                }
            }
        }

        /// <summary>
        /// The facts graph's root derives the union S's does, the keys beside a parameter among it, both from one set of
        /// rows: the columns' rows and rules are the readings vocabulary's, which both graphs define. And the rows the
        /// adapter's facts graph reads (<see cref="CosmosAxiomRows.ReadCalcite"/>) name the columns S's definition does.
        /// </summary>
        [Fact]
        public void TheFactsGraphsRootJoinsTheUnionSDoes()
        {
            var shared = new JsonContexts(Adapter.Metadata.CosmosReasoning.Rows with { Calcite = ProbeRows.Measured.Calcite() });
            var production = new JsonContexts(Adapter.Metadata.CosmosReasoning.Rows);
            var union = shared.Json.Get(shared.Rules.Readings.RawUnion.Of());

            union.Should().Be(ProbeRows.Columns);
            union.Entries.Should().Contain(e => e.Key.Test != CosmosServiceTest.IsNull && e.Key.Operand == CosmosValueClass.None, "a comparison beside a parameter has its key");
            production.Json.Get(production.Rules.Readings.RawUnion.Of()).Should().Be(ProbeRows.Columns, "the registry's rows the adapter reads");
            GC.KeepAlive(shared);
            GC.KeepAlive(production);
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
