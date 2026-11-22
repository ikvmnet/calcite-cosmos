using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Which evaluator reads a reading is its rows' (G7, <c>DESIGN.md</c>, "Which evaluator reads a reading is its rows'"): a
    /// <c>Calcite</c> row names the evaluator that reads a reading (<c>calciteevaluator</c>), the readings rules derive which
    /// reading reads each class of value from it, in S's table and the facts graph's alike, and a cell is evaluated by the
    /// evaluator the table derives for its class, or by the reading's rows alone where it derives none. No reading is read by
    /// its spelling.
    /// </summary>
    public sealed class ReadingEvaluatorTests
    {

        const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;

        /// <summary>What a reading answers where nothing says what it reads: anything and a raise.</summary>
        const Truths Unread = T | F | U | R;

        static readonly string CastOfRaw = PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader);

        /// <summary>A cell of each class a stored value may be of, and the unknown cell, which is of none.</summary>
        static readonly PathCell[] Cells =
        [
            new PathCell.Point(Of("30")), new PathCell.Point(Of("x")), new PathCell.Point(Of(30m)), new PathCell.Point(Of(30.5m)), new PathCell.Point(Of(true)),
            new PathCell.Point(Of(false)), new PathCell.Null(), new PathCell.Absent(), new PathCell.Structure(ObjectType), new PathCell.Structure(ArrayType),
            new PathCell.Unknown(),
        ];

        /// <summary>A reading's null test, and its comparisons with a number, a string and a boolean.</summary>
        static IEnumerable<PredicateAtom> Atoms(string reader) =>
        [
            PredicateAtom.IsNull(reader), PredicateAtom.Compare(reader, PredicateAtom.Equal, Of(30m)), PredicateAtom.Compare(reader, PredicateAtom.Less, Of(30m)),
            PredicateAtom.Compare(reader, PredicateAtom.Equal, Of("30")), PredicateAtom.Compare(reader, PredicateAtom.Less, Of("30")),
            PredicateAtom.Compare(reader, PredicateAtom.Equal, Of(true)), PredicateAtom.In(reader, ValueSet.Of(Of(30m), Of(31m))),
        ];

        /// <summary>Both graphs' readings tables, each beside its raw columns' union: S's, and the facts graph's root's.</summary>
        static IEnumerable<(string Graph, ReadingTable Rows, RawTruths Columns)> Tables => [("S", ProbeRows.ReaderRows, ProbeRows.Columns), ("the facts graph", FactsReadings.Rows, FactsReadings.Columns)];

        /// <summary>
        /// Every reading a row names an evaluator for, with the evaluator and the rows naming it; and each raw column's reading of
        /// those whose columns a row states, through every column a row measured and every column read as their union, with the
        /// evaluator and rows of its reading through every column, as the readings rules derive them.
        /// </summary>
        static IReadOnlyList<(string Reader, ReadingEvaluator Evaluator, ValueSet<string> Rows)> Named()
        {
            var named = CosmosAxiomRows.ReadCalciteEvaluators(CosmosAxioms.All).GroupBy(r => r.Reader)
                .Select(g => (Reader: g.Key, Evaluator: g.Select(r => r.Evaluator).Distinct().Single(), Rows: ValueSet.From(g.Select(r => r.Row)))).ToList();
            var columns = ProbeRows.Measured.Columns.Reads;
            var inColumns = named.Where(n => columns.Any(c => c.Reading == n.Reader))
                .SelectMany(n => columns.Where(c => c.Reading == n.Reader).Select(c => c.Column).Distinct().Concat(PredicateAtom.UnionColumns)
                    .Select(column => (Reader: PredicateAtom.InColumn(n.Reader, column), n.Evaluator, n.Rows)));
            return [.. named, .. inColumns];
        }

        /// <summary>
        /// Every reading a row names an evaluator for is read by itself over every class and over a cell of no one class, in
        /// both graphs, by the evaluator the row names, resting on each row naming it; and so is each raw column's reading of
        /// the raw value and its cast to text, by the evaluator of its reading through every column.
        /// </summary>
        [Fact]
        public void EveryReadingARowNamesAnEvaluatorForIsReadByItOverEveryClass()
        {
            var named = Named();
            named.Select(n => n.Reader).Should().Contain(new[] { PredicateAtom.RawIn("VARIANT"), PredicateAtom.RawIn("CHAR"), PredicateAtom.InColumn(CastOfRaw, "ANY"), PredicateAtom.InColumn(CastOfRaw, "CHAR") },
                "the raw columns' readings are derived from the raw value's and its cast's");
            foreach (var (graph, table, _) in Tables)
                foreach (var (reader, evaluator, rows) in named)
                {
                    table[reader].Evaluator.Should().Be(evaluator, $"{reader} in {graph}");
                    table[reader].Cites.AsEnumerable().Should().Contain(rows.AsEnumerable(), $"{reader} in {graph} rests on the rows naming its evaluator");
                    foreach (var kind in CosmosValueClasses.Cells.AsEnumerable())
                        table[reader].AliasOf(kind).Should().Be(reader, $"{reader} over {kind} in {graph}");
                }
        }

        /// <summary>
        /// Every reading a row names an evaluator for evaluates on every cell, in both graphs: its null test and its comparisons
        /// with a number, a string and a boolean answer on a point of each class, a null, an absent path, a structure and the
        /// unknown cell, and what a row it keeps holds is read both ways, none of it throwing. A row naming an evaluator that no
        /// evaluator binds, or a type no family holds, fails here rather than inside a rule when a plan is made.
        /// </summary>
        [Fact]
        public void EveryReadingARowNamesAnEvaluatorForEvaluatesOnEveryCell()
        {
            foreach (var (graph, table, columns) in Tables)
                foreach (var (reader, _, _) in Named())
                    foreach (var atom in Atoms(reader))
                    {
                        foreach (var cell in Cells)
                            atom.OnCell(cell, columns, table).Should().NotBe(Truths.None, $"{atom} on {cell} in {graph}");

                        foreach (var truth in new[] { true, false })
                            atom.Kept(truth, table, columns).Should().NotBeNull($"{atom} kept {truth} in {graph}");
                    }
        }

        /// <summary>
        /// Every reading the predicate loader builds (<c>Calcite.ReadingsAreNullOverAnAbsentPath</c>'s subjects) is read by the
        /// evaluator a row names for it over every class, in both graphs, but the numeric casts of a raw value, which no row
        /// measured, which read nothing, as they did before a row named any evaluator. A row naming an evaluator dropped from a
        /// reading the loader writes fails here.
        /// </summary>
        [Fact]
        public void EveryReadingTheLoaderBuildsIsReadByTheEvaluatorARowNames()
        {
            var subjects = CosmosAxioms.All.Single(a => a.Id == "Calcite.ReadingsAreNullOverAnAbsentPath").Subjects!;
            var unmeasured = new[] { "INTEGER", "BIGINT", "DOUBLE" }.Select(target => PredicateAtom.CastReader(target, PredicateAtom.RawReader)).ToList();
            subjects.Should().Contain(unmeasured);
            foreach (var (graph, table, _) in Tables)
                foreach (var reader in subjects)
                    if (unmeasured.Contains(reader))
                    {
                        table[reader].Evaluator.Should().BeNull($"no row measured {reader}, in {graph}");
                        CosmosValueClasses.Cells.AsEnumerable().Should().OnlyContain(kind => table[reader].AliasOf(kind) == null, $"{reader} in {graph}");
                    }
                    else
                    {
                        table[reader].Evaluator.Should().NotBeNull($"{reader} in {graph}");
                        CosmosValueClasses.Cells.AsEnumerable().Should().OnlyContain(kind => table[reader].AliasOf(kind) == reader, $"{reader} in {graph}");
                    }
        }

        /// <summary>
        /// <c>typed:VARCHAR</c> and <c>typed:CHAR</c>, which a row reads as the text and none names an evaluator for, are read
        /// as the text over a string, a null and an absent path, the classes both read alike, and by their rows alone over
        /// every other class and over a cell of no one class: a number compared reads anything and a raise.
        /// </summary>
        [Fact]
        public void TheTypedTextPlacesAreReadAsTheTextWhereBothReadAlikeAndByTheirRowsAloneElsewhere()
        {
            var alike = new[] { CosmosValueClass.String, CosmosValueClass.Null, CosmosValueClass.Absent };
            foreach (var (graph, table, columns) in Tables)
                foreach (var reader in new[] { PredicateAtom.TypedReader("VARCHAR"), PredicateAtom.TypedReader("CHAR") })
                {
                    table[reader].Evaluator.Should().BeNull($"{reader} in {graph}");
                    foreach (var kind in CosmosValueClasses.Cells.AsEnumerable())
                        table[reader].AliasOf(kind).Should().Be(alike.Contains(kind) ? PredicateAtom.TextReader : null, $"{reader} over {kind} in {graph}");

                    PredicateAtom.IsNull(reader).OnCell(new PathCell.Point(Of("x")), columns, table).Should().Be(F, $"{reader} in {graph}");
                    PredicateAtom.IsNull(reader).OnCell(new PathCell.Absent(), columns, table).Should().Be(T, $"{reader} in {graph}");
                    PredicateAtom.Compare(reader, PredicateAtom.Equal, Of("30")).OnCell(new PathCell.Point(Of("30")), columns, table).Should().Be(T, $"{reader} in {graph}");
                    PredicateAtom.Compare(reader, PredicateAtom.Equal, Of(30m)).OnCell(new PathCell.Point(Of(30m)), columns, table).Should().Be(Unread, $"{reader} in {graph}");
                    PredicateAtom.IsNull(reader).OnCell(new PathCell.Point(Of(true)), columns, table).Should().Be(Unread, $"{reader} in {graph}");
                }
        }

        /// <summary>
        /// A reading no row names an evaluator for, nor reads as another, is read by its rows alone, in both graphs:
        /// <c>JSON_QUERY</c>'s null test is FALSE over an object and TRUE over a string, as its rows say; a
        /// <c>RETURNING VARIANT</c>, the element a traversal yields, a numeric cast of a raw value and a cast of the text to
        /// <c>CHAR</c>, whose rows say nothing, read anything and a raise on every cell but the unknown cell, where every
        /// reading answers anything.
        /// </summary>
        [Fact]
        public void AReadingNoRowNamesAnEvaluatorForIsReadByItsRowsAlone()
        {
            foreach (var (graph, table, columns) in Tables)
            {
                table[PredicateAtom.QueryReader].Evaluator.Should().BeNull(graph);
                PredicateAtom.IsNull(PredicateAtom.QueryReader).OnCell(new PathCell.Structure(ObjectType), columns, table).Should().Be(F, graph);
                PredicateAtom.IsNull(PredicateAtom.QueryReader).OnCell(new PathCell.Point(Of("x")), columns, table).Should().Be(T, graph);

                foreach (var reader in new[] { PredicateAtom.TypedReader("VARIANT"), PredicateAtom.ElementReader, PredicateAtom.CastReader("INTEGER", PredicateAtom.RawReader),
                    PredicateAtom.CastReader("CHAR", PredicateAtom.TextReader) })
                {
                    table[reader].Evaluator.Should().BeNull($"{reader} in {graph}");
                    foreach (var atom in Atoms(reader))
                        foreach (var cell in Cells)
                            atom.OnCell(cell, columns, table).Should().Be(cell is PathCell.Unknown ? Truths.Any : Unread, $"{atom} on {cell} in {graph}");
                }
            }
        }

        /// <summary>
        /// A reading is read as its rows say, whatever it is spelled, in both graphs: with <c>typed:INTEGER</c>'s evaluator
        /// rows dropped it reads anything and a raise on every cell but the unknown cell, though its name is unchanged; and a
        /// reading spelled <c>parse-me</c>, which a row names the number cast of <c>INTEGER</c> for, answers on every cell, and
        /// keeps on every row, what <c>cast:INTEGER:text</c> does.
        /// </summary>
        [Fact]
        public void AReadingIsReadAsItsRowsSayAndNotAsItsSpelling()
        {
            var typed = PredicateAtom.TypedReader("INTEGER");
            var cast = PredicateAtom.CastReader("INTEGER", PredicateAtom.TextReader);
            IReadOnlyList<(string, ReadingEvaluator, string)> Renamed(IReadOnlyList<(string Reader, ReadingEvaluator Evaluator, string Row)> rows) =>
                [.. rows.Where(e => e.Reader != typed), ("parse-me", rows.First(e => e.Reader == cast).Evaluator, "Calcite.CastOfTheAccessorParsesTheRendering")];
            var theory = RenderingTheory.Define(ProbeRows.Measured with { CalciteEvaluators = Renamed(ProbeRows.Measured.CalciteEvaluators) });
            var contexts = new JsonContexts(CosmosReasoning.Rows with { Calcite = CosmosReasoning.Rows.Calcite with { Evaluators = Renamed(CosmosReasoning.Rows.Calcite.Evaluators) } });

            PredicateAtom.Compare(typed, PredicateAtom.Equal, Of(30m)).OnCell(new PathCell.Point(Of(30m)), ProbeRows.Columns, ProbeRows.ReaderRows).Should().NotBe(Unread, "the registry's rows name its evaluator");
            foreach (var (graph, table, columns) in new[]
            {
                ("S", theory.Theory.Get(theory.ReaderRows.Of()), theory.Theory.Get(theory.RawUnion.Of())),
                ("the facts graph", contexts.Graph.Root.Get(contexts.Rules.Readings.ReaderRows.Of()), contexts.Graph.Root.Get(contexts.Rules.Readings.RawUnion.Of())),
            })
            {
                table[typed].Evaluator.Should().BeNull(graph);
                foreach (var atom in Atoms(typed))
                    foreach (var cell in Cells)
                        atom.OnCell(cell, columns, table).Should().Be(cell is PathCell.Unknown ? Truths.Any : Unread, $"{atom} on {cell} in {graph}");

                table["parse-me"].Evaluator.Should().Be(table[cast].Evaluator, graph);
                foreach (var (named, spelled) in Atoms("parse-me").Zip(Atoms(cast)))
                {
                    foreach (var cell in Cells)
                        named.OnCell(cell, columns, table).Should().Be(spelled.OnCell(cell, columns, table), $"{named} on {cell} in {graph}");

                    foreach (var truth in new[] { true, false })
                        named.Kept(truth, table, columns).Should().Be(spelled.Kept(truth, table, columns), $"{named} kept {truth} in {graph}");
                }
            }

            GC.KeepAlive(theory);
            GC.KeepAlive(contexts);
        }

        /// <summary>
        /// What a row keeps is each class's as the reading that reads the class keeps it, not as the reading a cell of no one
        /// class is read by, in both graphs: with rows reading such a cell as itself for <c>typed:VARCHAR</c> and for the text,
        /// the alias rule reads it as the text there, while a boolean is still read by <c>typed:VARCHAR</c>'s rows alone, so
        /// what a row it keeps holds may be every value its cell may take that truth value on, a boolean among them.
        /// </summary>
        [Fact]
        public void EachClassIsKeptAsTheReadingThatReadsItKeepsIt()
        {
            var varchar = PredicateAtom.TypedReader("VARCHAR");
            (string, CosmosValueClass, string)[] noClass = [(varchar, CosmosValueClass.None, CalciteRead.Itself), (PredicateAtom.TextReader, CosmosValueClass.None, CalciteRead.Itself)];
            var theory = RenderingTheory.Define(ProbeRows.Measured with { CalciteReadings = [.. ProbeRows.Measured.CalciteReadings, .. noClass] });
            var contexts = new JsonContexts(CosmosReasoning.Rows with { Calcite = CosmosReasoning.Rows.Calcite with { Reads = [.. CosmosReasoning.Rows.Calcite.Reads, .. noClass] } });
            foreach (var (graph, table, columns) in new[]
            {
                ("S", theory.Theory.Get(theory.ReaderRows.Of()), theory.Theory.Get(theory.RawUnion.Of())),
                ("the facts graph", contexts.Graph.Root.Get(contexts.Rules.Readings.ReaderRows.Of()), contexts.Graph.Root.Get(contexts.Rules.Readings.RawUnion.Of())),
            })
            {
                table[varchar].AliasOf(CosmosValueClass.None).Should().Be(PredicateAtom.TextReader, graph);
                table[varchar].AliasOf(CosmosValueClass.True).Should().BeNull(graph);
                foreach (var atom in Atoms(varchar))
                    foreach (var (truth, value) in new[] { (true, T), (false, F) })
                    {
                        var kept = atom.Kept(truth, table, columns);
                        foreach (var cell in Cells.Where(c => c is not PathCell.Unknown && (atom.OnCell(c, columns, table) & value) != 0))
                            Holds(kept, cell).Should().BeTrue($"{atom} may be {truth} on {cell} in {graph}, so a row it keeps {truth} may hold it: {kept}");
                    }
            }

            GC.KeepAlive(theory);
            GC.KeepAlive(contexts);
        }

        /// <summary>
        /// What a row keeps is each class's as the table reads the class, whatever the table: with <c>typed:INTEGER</c>'s
        /// evaluator named over one class alone, an absent path or a string, which no rule derives, every other class is read
        /// by the reading's rows alone, which say nothing there, so what a row it keeps holds may be every value of those
        /// classes, as their cells may take either truth value; and the one class is kept as the evaluator keeps it.
        /// </summary>
        [Fact]
        public void AClassTheTableNamesNoReadingForIsKeptAsTheRowsAloneKeepIt()
        {
            var integer = PredicateAtom.TypedReader("INTEGER");
            var evaluator = ProbeRows.ReaderRows[integer].Evaluator!.Value;
            foreach (var kind in new[] { CosmosValueClass.Absent, CosmosValueClass.String })
            {
                var table = ReadingTable.EvaluatedBy(integer, kind, evaluator, "Calcite.ReturningAsserts");
                foreach (var atom in Atoms(integer))
                    foreach (var (truth, value) in new[] { (true, T), (false, F) })
                    {
                        var kept = atom.Kept(truth, table, RawTruths.Empty);
                        foreach (var cell in Cells.Where(c => c is not PathCell.Unknown && (atom.OnCell(c, RawTruths.Empty, table) & value) != 0))
                            Holds(kept, cell).Should().BeTrue($"{atom} may be {truth} on {cell} with its evaluator over {kind} alone, so a row it keeps {truth} may hold it: {kept}");
                    }
            }
        }

        /// <summary>Whether what a path holds may be the cell's value.</summary>
        static bool Holds(PathValues kept, PathCell cell) => cell switch
        {
            PathCell.Point { Value: var v } => kept.Admits(v),
            PathCell.Null => kept.Admits(NullValue),
            PathCell.Absent => kept.IsPresent == false,
            PathCell.Structure { Type: var type } => kept.Kinds.Contains(type),
            _ => true,
        };

        /// <summary>
        /// Both graphs' readings settled from the registry's rows with these evaluator rows beside them: S defined, and the
        /// facts graph's root built.
        /// </summary>
        static IEnumerable<(string Graph, Action Settle)> SettledWith(params (string Reader, ReadingEvaluator Evaluator, string Row)[] added) =>
        [
            ("S", () => RenderingTheory.Define(ProbeRows.Measured with { CalciteEvaluators = [.. ProbeRows.Measured.CalciteEvaluators, .. added] })),
            ("the facts graph", () => new JsonContexts(CosmosReasoning.Rows with { Calcite = CosmosReasoning.Rows.Calcite with { Evaluators = [.. CosmosReasoning.Rows.Calcite.Evaluators, .. added] } })),
        ];

        /// <summary>
        /// Two evaluators named for one reading fail the run, in both graphs, as two readings named for one class do: the raw
        /// value's evaluator named for the text beside its own throws, rather than choose one; and so does
        /// <c>typed:INTEGER</c>'s named again with the kinds of another type, the failure naming the two by their kinds.
        /// </summary>
        [Fact]
        public void TwoEvaluatorsForOneReadingFailTheRun()
        {
            foreach (var (graph, settle) in SettledWith((PredicateAtom.TextReader, new ReadingEvaluator(CalciteEvaluator.Raw, "", ValueSet<string>.Empty), "Calcite.TextOrderIsByCodeUnit")))
                FluentActions.Invoking(settle).Should().Throw<RuleEvaluationException>(graph).WithInnerException<InvalidOperationException>().WithMessage("*evaluated by Text and Raw.");

            var integer = PredicateAtom.TypedReader("INTEGER");
            var numbers = ProbeRows.ReaderRows[integer].Evaluator!.Value with { Kinds = ValueSet.Of(FractionType, IntegerType) };
            foreach (var (graph, settle) in SettledWith((integer, numbers, "Calcite.ReturningAsserts")))
                FluentActions.Invoking(settle).Should().Throw<RuleEvaluationException>(graph).WithInnerException<InvalidOperationException>()
                    .WithMessage("*evaluated by Typed(INTEGER){fraction,integer} and Typed(INTEGER){integer}.");
        }

        /// <summary>
        /// An evaluator named for a reading a row reads as another fails the run, in both graphs: the text's named for
        /// <c>typed:VARCHAR</c>, which a row reads as the text over a string, a null and an absent path, would read those
        /// classes as itself and as the text, and the run throws rather than choose one.
        /// </summary>
        [Fact]
        public void AnEvaluatorForAReadingARowReadsAsAnotherFailsTheRun()
        {
            foreach (var (graph, settle) in SettledWith((PredicateAtom.TypedReader("VARCHAR"), new ReadingEvaluator(CalciteEvaluator.Text, "", ValueSet<string>.Empty), "Calcite.TextPlacesCompareAsTheirStrings")))
                FluentActions.Invoking(settle).Should().Throw<RuleEvaluationException>(graph).WithInnerException<InvalidOperationException>().WithMessage("*evaluated as*");
        }

        /// <summary>
        /// Each reading rests on the rows that name its evaluator, beside its own, in both graphs: the null test's rows of the
        /// text, the raw value through a <c>VARIANT</c>, its cast to text through an <c>ANY</c>, a typed reading, <c>RETURNING
        /// DECIMAL</c>, a numeric cast and the boolean cast of the text, and <c>typed:VARCHAR</c>, the text's beside its own.
        /// </summary>
        [Fact]
        public void EachReadingRestsOnTheRowsThatNameItsEvaluator()
        {
            string[] text = ["Calcite.JsonValueReadsStructureAsNull", "Calcite.JsonValueRendersScalarsAsText", "Calcite.TextOrderIsByCodeUnit"];
            var rests = new Dictionary<string, string[]>
            {
                [PredicateAtom.TextReader] = text,
                [PredicateAtom.RawIn("VARIANT")] = ["Calcite.RawNullIsSqlNull", "Calcite.RawStringEqualityIsOrdinal"],
                [PredicateAtom.InColumn(CastOfRaw, "ANY")] = ["Calcite.CastOfARawValueToText", "Calcite.TextOrderIsByCodeUnit"],
                [PredicateAtom.TypedReader("INTEGER")] = ["Calcite.ColumnReadsWholeNumbersAsItsType", "Calcite.ReturningAsserts"],
                [PredicateAtom.TypedReader("DECIMAL")] = ["Calcite.ColumnReadsWholeNumbersAsItsType", "Calcite.JsonValueRendersScalarsAsText", "Calcite.ReturningDecimalParsesTheRendering"],
                [PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader)] = ["Calcite.CastOfTheAccessorParsesTheRendering", "Calcite.JsonValueRendersScalarsAsText"],
                [PredicateAtom.CastReader("BOOLEAN", PredicateAtom.TextReader)] = ["Calcite.CastToBooleanParses", "Calcite.JsonValueReadsStructureAsNull", "Calcite.JsonValueRendersScalarsAsText"],
                [PredicateAtom.TypedReader("VARCHAR")] = [.. text, "Calcite.TextPlacesCompareAsTheirStrings"],
            };
            foreach (var (graph, table, _) in Tables)
                foreach (var (reader, rows) in rests)
                    PredicateAtom.IsNull(reader).RestsOn(table).AsEnumerable().Should().BeEquivalentTo(rows, $"{reader} in {graph}");
        }

    }

}
