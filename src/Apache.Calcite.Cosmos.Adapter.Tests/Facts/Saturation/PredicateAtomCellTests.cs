using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// Calcite on one cell (<see cref="PredicateAtom.OnCell"/>), over <see cref="PredicateAtomTests"/>' grid, <c>BIGINT</c>
    /// and the casts it leaves out, and literals at the rounding boundaries, on the cells of five imports cut at nothing and
    /// where the reader's edges row cuts them.
    /// </summary>
    /// <remarks>
    /// A cell's answer holds of every value in it, as <see cref="IrEval"/> reads the value through each reader the reading
    /// stands for: <c>RETURNING</c> and a column for <c>typed</c>, three columns for <c>raw</c> and its cast to text. It is
    /// within the coarse answer on the cell's state, but for what the coarse readings leave out (<c>TODO.md</c>), and a
    /// sub-cell's is within its cell's.
    /// </remarks>
    public sealed class PredicateAtomCellTests
    {

        const string Bigint = "typed:BIGINT";

        static readonly string[] Readers = [.. PredicateAtomTests.Readers, Bigint, "cast:INTEGER:raw", "cast:DOUBLE:raw", "cast:BIGINT:text"];

        /// <summary>Literals where a number's double, its parse and a type's limits part cells.</summary>
        static readonly string[] Boundaries = [Of(9007199254740993m), Of(9007199254740994m), Of(0.1m), Of(2m), Of(2147483648m)];

        /// <summary>What an <c>ANY</c> renders the pool's numbers as, a long's digits and a double's spelling, beside the cast of a raw value to text.</summary>
        static readonly string[] Rendered = [Of("9007199254740993"), Of("9007199254740992"), Of("2147483648"), Of("0.1"), Of("9.007199254740992E15"), Of("9.007199254740994E15"), Of("2.147483648E9"), Of("1.0E30")];

        static readonly JsonDocumentPath A = JsonDocumentPath.Root.Property("a");

        /// <summary>
        /// Nothing known, a declared string, a non-negative integer, a domain, a value excluded, and a fraction a decimal's
        /// cast to a double misses.
        /// </summary>
        static readonly PathValues[] Imports =
        {
            PathValues.Anything,
            PathValues.OfKinds(ValueSet.Of(StringType)),
            PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m))),
            PathValues.OneOf(ValueSet.Of(Of("30"), Of("x"), Of(30m), Of(2.5m), Of(true), NullValue)),
            PathValues.Meet(PathValues.AnythingThere, PathValues.Not(Of("x"))),
            PathValues.OneOf(ValueSet.Of(Of(9007199254740994.4m))),
        };

        /// <summary>The values a cell is asked of, beside its witness: the grid's stored values and population D's.</summary>
        static readonly J?[] Pool = PredicateAtomTests.Stored.Select(PathCellTests.Decode).Concat(Declaration.Values)
            .Concat([new J.Num(9007199254740993m), new J.Num(9007199254740994.4m), new J.Num(0.1m), new J.Num(0.10000000000000001m), new J.Num(2m), new J.Num(2147483648m), new J.Str("2"), new J.Str("2.5")])
            .ToArray();

        /// <summary>
        /// Every value in a cell, read through every reader the atom's reading stands for, takes a truth value the cell's
        /// answer holds. A coin the evaluator tosses, where nothing was measured, leaves the case out, counted.
        /// </summary>
        [Fact]
        public void EveryCellAnswerHoldsOnItsValues()
        {
            var faults = new List<string>();
            var (asked, tossed) = (0, 0);
            var readers = new HashSet<string>();
            foreach (var atom in Atoms(boundaries: true))
            {
                var predicate = Predicate(atom);
                foreach (var cell in Partitions(atom).SelectMany(p => p.Coarse.Cells.Concat(p.Fine.Cells)))
                {
                    var answers = atom.OnCell(cell);
                    foreach (var value in Inside(cell))
                        foreach (var (typed, raw) in Modes(atom))
                        {
                            var world = new World(asked + tossed);
                            var truth = IrEval.Truth(predicate, Document(value), world, typed, raw);
                            if (world.Tossed > 0)
                            {
                                tossed++;
                                continue;
                            }

                            asked++;
                            readers.Add(atom.Reader);
                            if ((answers & truth) == 0)
                                faults.Add($"{predicate} is {truth} of {PathCellTests.Show(value)} read {typed} {raw}, in {cell}, where the atom answers {answers}");
                        }
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{asked:N0} answers checked; {tossed:N0} left out, a coin tossed");
            faults.Should().BeEmpty();
            readers.Should().BeEquivalentTo(Readers, "every reading is checked on some value");
        }

        /// <summary>
        /// Where the column a typed reading stands for reads a value of a cell, the cell answers TRUE or FALSE beside any
        /// raise: <c>RETURNING</c> raising there is no leave to answer nothing. The plant answers a raise alone on an
        /// int-sized <c>BIGINT</c>, which <c>RETURNING</c> raises on and the column reads.
        /// </summary>
        [Fact]
        public void TypedCellsKeepTheValueBesideTheRaise()
        {
            Unkept((atom, cell) => atom.OnCell(cell)).Should().BeEmpty();
            Unkept((atom, cell) => atom.Reader == Bigint && cell is PathCell.Point { Value: var v } && TryNumberOf(v, out var x) && x >= int.MinValue && x <= int.MaxValue
                    ? Truths.Raises : atom.OnCell(cell))
                .Should().NotBeEmpty("the plant answers a raise alone where the column reads an int-sized BIGINT");
        }

        /// <summary>
        /// A cell answers within what the coarse reading answers on its state, but for the raises a raw and a typed reading
        /// leave out there, a <c>VARCHAR</c> column's read and a raw ordering, and a typed column, <c>DECIMAL</c>'s among them,
        /// over a structure; where a reading compares a number's double, which the coarse reading compares by value; and the
        /// cast of a raw value to text, which the coarse reading reads as an <c>ANY</c> renders it alone (<c>TODO.md</c>). A
        /// reading the precision table calls exact is read so by neither (<see cref="EveryExactReadingAnswersAsItsCell"/>).
        /// </summary>
        [Fact]
        public void CellAnswersStayInsideTheCoarseAnswer()
        {
            var wider = new List<string>();
            foreach (var atom in Atoms(boundaries: true).Where(a => a.Reader != "cast:VARCHAR:raw"))
            {
                var leftOut = atom.Reader == PredicateAtom.RawReader || atom.Reader.StartsWith("typed:", StringComparison.Ordinal) ? Truths.Raises : Truths.None;
                foreach (var cell in Partitions(atom).SelectMany(p => p.Coarse.Cells.Concat(p.Fine.Cells)))
                    if ((atom.OnCell(cell) & ~atom.On(cell.Coarse) & ~leftOut & ~(Rounded(atom, cell) ? Truths.True | Truths.False | Truths.Unknown : 0)) != 0)
                        wider.Add($"{atom} answers {atom.OnCell(cell)} on {cell}, and {atom.On(cell.Coarse)} on its state");
            }

            wider.Should().BeEmpty();
        }

        /// <summary>
        /// Every reading <see cref="ReadingPrecisionTable"/> calls exact, beside each literal it is exact beside, is read by
        /// the facts graph as its cells answer: the coarse answer on a cell's state holds every truth the cell gives, and
        /// what a row of each truth can be (<see cref="PredicateAtom.Kept"/>) admits every value Calcite gives that truth on,
        /// through every reader the reading stands for. So a stratum that assumes such an atom leaves out no document Calcite
        /// keeps. Coins are left out, counted; the table calls some reading coarse that this would hold.
        /// </summary>
        [Fact]
        public void EveryExactReadingAnswersAsItsCell()
        {
            var (inexact, (asked, tossed)) = (new List<string>(), (0, 0));
            var readers = new HashSet<string>();
            foreach (var atom in Atoms(boundaries: true).Where(a => RenderingFamily.ReadExactly(Predicate(a))))
            {
                readers.Add(atom.Reader);
                foreach (var cell in Partitions(atom).SelectMany(p => p.Coarse.Cells.Concat(p.Fine.Cells)))
                {
                    if ((atom.OnCell(cell) & ~atom.On(cell.Coarse) & (Truths.True | Truths.False)) != 0)
                        inexact.Add($"{atom} answers {atom.OnCell(cell)} on {cell}, and {atom.On(cell.Coarse)} on its state");

                    foreach (var value in Inside(cell))
                        foreach (var (typed, raw) in Modes(atom))
                        {
                            var world = new World(asked + tossed);
                            var truth = IrEval.Truth(Predicate(atom), Document(value), world, typed, raw);
                            if (world.Tossed > 0)
                            {
                                tossed++;
                                continue;
                            }

                            asked++;
                            if (truth is Truths.True or Truths.False && PopulationD.Admits(atom.Kept(truth == Truths.True), value) == false)
                                inexact.Add($"{atom} is {truth} of {PathCellTests.Show(value)} read {typed} {raw}, which a row it keeps {truth} cannot be");
                        }
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{asked:N0} answers checked; {tossed:N0} left out, a coin tossed");
            inexact.Should().BeEmpty("a stratum resting on a reading called exact leaves out no document Calcite keeps");
            readers.Should().BeEquivalentTo(ReadingPrecisionTable.Rows.Keys.Where(Readers.Contains), "every row the grid reads is held");
        }

        static bool Fractional(PathCell cell) => cell.Coarse is PredicateAtom.State.AnyNumber { Integral: false } || cell.Coarse is PredicateAtom.State.Scalar s && TypeOf(s.Value) == FractionType;

        /// <summary>
        /// Whether the coarse reading compares a number's value where Calcite compares its double: an integer type's column
        /// and <c>DECIMAL</c>'s clause, which read a number written with a point through it, a double, and the cast to a
        /// double, whose decimal the coarse reading converts by a cast that misses at rounding boundaries
        /// (<c>Renderings.DoubleOf</c>); and the raw value, whose box holds a number not written as a long as its double.
        /// </summary>
        static bool Rounded(PredicateAtom atom, PathCell cell) => (cell.Coarse is PredicateAtom.State.AnyNumber || cell.Coarse is PredicateAtom.State.Scalar { Value: var v } && IsNumber(v))
            && atom.Reader is "typed:INTEGER" or Bigint or "typed:DOUBLE" or "typed:DECIMAL" or "cast:DOUBLE:raw" or "cast:DOUBLE:text" or PredicateAtom.RawReader;

        /// <summary>
        /// The raw reading's raises (<c>Calcite.RawNullIsSqlNull</c>): an ordering places a string by its units and a number
        /// in its box beside a <c>VARIANT</c>'s raise, and raises alone across types; an equality with a number is FALSE of
        /// another type, and TRUE or FALSE of a number a box may equal, the <c>VARIANT</c>'s being the literal's.
        /// </summary>
        [Fact]
        public void ARawReadingPlacesItsTypeAndRaisesAcrossTypes()
        {
            const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;
            var (below, around, thirty) = (PredicateAtom.Compare(PredicateAtom.RawReader, "<", Of("b")), PredicateAtom.Compare(PredicateAtom.RawReader, "<", Of(9007199254740993m)),
                PredicateAtom.Compare(PredicateAtom.RawReader, "=", Of(30m)));

            new[] { new PathCell.Point(Of("a")), new PathCell.Strings(Of("b"), null, ValueSet<string>.Empty, AllBands), new PathCell.Point(Of(7m)), (PathCell)new PathCell.Null() }
                .Select(below.OnCell).Should().Equal(T | R, F | R, R, U | R);
            new[] { new PathCell.Point(Of(9007199254740992m)), new PathCell.Point(Of(9007199254740993m)), new PathCell.Point(Of(true)) }.Select(around.OnCell).Should().Equal(T | R, T | F | R, R);
            new[] { new PathCell.Point(Of("30")), new PathCell.Point(Of(30m)), new PathCell.Point(Of(31m)), (PathCell)new PathCell.Null() }.Select(thirty.OnCell).Should().Equal(F, T | F, F, U);
        }

        /// <summary>A cell cut at the atom's literals answers within the cell it lies in, cut at none.</summary>
        [Fact]
        public void ASubCellAnswersNoWider()
        {
            var wider = new List<string>();
            foreach (var atom in Atoms(boundaries: true))
                foreach (var (coarse, fine) in Partitions(atom))
                    foreach (var cell in fine.Cells)
                    {
                        var within = coarse.Cells.Single(c => c.Contains(cell.Witness()));
                        if ((atom.OnCell(cell) & ~atom.OnCell(within)) != 0)
                            wider.Add($"{atom} answers {atom.OnCell(cell)} on {cell}, within {within}, which answers {atom.OnCell(within)}");
                    }

            wider.Should().BeEmpty();
        }

        /// <summary>Every row a reading's cells rest on is the registry's.</summary>
        [Fact]
        public void EveryRowACellRestsOnIsRegistered()
        {
            var rests = Atoms().SelectMany(a => a.Rests).Distinct().ToList();

            rests.Should().NotBeEmpty();
            rests.Should().BeSubsetOf(CosmosAxioms.All.Select(r => r.Id));
        }

        /// <summary>
        /// The grid's atoms the loader builds, each literal of the kind its reading is compared with; and beside the numeric
        /// readings, the boundaries, which the coarse readings compare exactly where a double does not.
        /// </summary>
        static IEnumerable<PredicateAtom> Atoms(bool boundaries = false) => PredicateAtomTests.Grid(Readers)
            .Concat(boundaries ? from reader in Readers from literal in reader == "cast:VARCHAR:raw" ? Rendered : Boundaries from op in new[] { "=", "<", ">=" } select PredicateAtom.Compare(reader, op, literal) : [])
            .Where(atom => Literals(atom).All(literal => atom.Reader switch
        {
            PredicateAtom.RawReader => true,
            PredicateAtom.TextReader or "cast:VARCHAR:raw" => IsString(literal),
            "typed:BOOLEAN" => TypeOf(literal) == BooleanType,
            _ => IsNumber(literal),
        }));

        static IEnumerable<string> Literals(PredicateAtom atom) => new[] { atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty);

        /// <summary>Each import, with either set of classes, cut at nothing and where the reader's edges cut it (<see cref="CutPoints"/>).</summary>
        static IEnumerable<(CellPartition Coarse, CellPartition Fine)> Partitions(PredicateAtom atom)
        {
            var literals = CutPoints.Of(atom, ServiceForm.BareOf(SqlValue.Spell(A)), ReaderEdgesTable.Rows.GetValueOrDefault(atom.Reader) ?? new(ValueSet<CutKind>.Empty, ValueSet<string>.Empty));
            foreach (var values in Imports)
                foreach (var classes in new[] { AllBands, NoBand })
                    yield return (PathCell.Split(SqlValue.Spell(A), values, classes, ValueSet<CutPoint>.Empty), PathCell.Split(SqlValue.Spell(A), values, classes, literals));
        }

        /// <summary>The values a cell holds of its witness and the pool, each whole number in both its spellings.</summary>
        static IEnumerable<J?> Inside(PathCell cell) => Pool.Append(PathCellTests.Decode(cell.Witness()))
            .SelectMany(v => v is J.Num n && n.Value == decimal.Truncate(n.Value) ? new J?[] { new J.Num(NumberOf(Of(n.Value))), new J.Num(NumberOf(Of(n.Value)) + 0.0m) } : new[] { v })
            .Where(v => cell.Contains(PathCellTests.Encode(v)));

        /// <summary>
        /// The readers a reading stands for, as the evaluator names them: <c>RETURNING</c> and a column for <c>typed</c>,
        /// but <c>DECIMAL</c>'s column, which was not measured; a <c>VARIANT</c>, an <c>ANY</c>, and beside strings alone
        /// a <c>VARCHAR</c>, for <c>raw</c> and its cast to text.
        /// </summary>
        static IEnumerable<(IrEval.TypedAs Typed, IrEval.RawAs? Raw)> Modes(PredicateAtom atom)
        {
            if (atom.Reader == "cast:VARCHAR:raw")
                return IrEval.RawModes(Predicate(atom)).Select(raw => (IrEval.TypedAs.Returning, raw));

            if (atom.Reader == PredicateAtom.RawReader)
                return new[] { IrEval.RawAs.Variant, IrEval.RawAs.Any, IrEval.RawAs.Varchar }
                    .Where(raw => raw != IrEval.RawAs.Varchar || Literals(atom).All(IsString))
                    .Select(raw => (IrEval.TypedAs.Returning, (IrEval.RawAs?)raw));

            if (atom.Reader.StartsWith("typed:", StringComparison.Ordinal) && atom.Reader != "typed:DECIMAL")
                return new[] { (IrEval.TypedAs.Returning, (IrEval.RawAs?)null), (IrEval.TypedAs.Column, null) };

            return new[] { (IrEval.TypedAs.Returning, (IrEval.RawAs?)null) };
        }

        /// <summary>
        /// The cells of a typed atom on which its column reads a value of the cell but the answer holds neither TRUE nor
        /// FALSE.
        /// </summary>
        static List<string> Unkept(Func<PredicateAtom, PathCell, Truths> onCell)
        {
            var unkept = new List<string>();
            foreach (var atom in Atoms(boundaries: true).Where(a => a.Reader is "typed:INTEGER" or Bigint or "typed:DOUBLE" or "typed:BOOLEAN" && a.Parameter == false))
            {
                var column = new SqlValue.Typed(A, atom.Reader.Substring("typed:".Length));
                foreach (var cell in Partitions(atom).SelectMany(p => p.Coarse.Cells.Concat(p.Fine.Cells)))
                    if (Inside(cell).Any(v => IrEval.Read(column, Document(v), new World(0), IrEval.TypedAs.Column) is not (SqlDatum.Raised or SqlDatum.NullDatum))
                        && (onCell(atom, cell) & (Truths.True | Truths.False)) == 0)
                        unkept.Add($"{atom} answers {onCell(atom, cell)} on {cell}");
            }

            return unkept;
        }

        /// <summary>An atom as the IR's predicate over the path <c>a</c>.</summary>
        static SqlPredicate Predicate(PredicateAtom atom)
        {
            var value = Reading(atom.Reader);
            return atom.Test switch
            {
                PredicateAtom.NullTest => new SqlPredicate.IsNull(value),
                PredicateAtom.InTest => new SqlPredicate.In(value, atom.Points!),
                _ when atom.Parameter => new SqlPredicate.Compare(atom.Test, value, new SqlValue.Parameter(0, atom.Reader.Split(':') is [_, var type, ..] ? type : "VARCHAR")),
                _ => new SqlPredicate.Compare(atom.Test, value, new SqlValue.Literal(atom.Operand!)),
            };
        }

        static SqlValue Reading(string reader) => reader.Split(':', 3) switch
        {
            [PredicateAtom.TextReader] => new SqlValue.Text(A),
            [PredicateAtom.RawReader] => new SqlValue.Raw(A),
            ["typed", var type] => new SqlValue.Typed(A, type),
            [_, var target, var inner] => new SqlValue.Cast(target, Reading(inner)),
            _ => throw new NotSupportedException(reader),
        };

        static J.Obj Document(J? value)
        {
            var properties = new SortedDictionary<string, J>(StringComparer.Ordinal);
            if (value is not null)
                properties["a"] = value;

            return new J.Obj(properties);
        }

    }

}
