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
    /// Calcite on one cell (<see cref="PredicateAtom.OnCell"/>), over <see cref="PredicateAtomTests"/>' grid and
    /// <c>BIGINT</c>, on the cells of five imports cut at nothing and at the atom's literals.
    /// </summary>
    /// <remarks>
    /// A cell's answer holds of every value in it, as <see cref="IrEval"/> reads the value through each reader the reading
    /// stands for: <c>RETURNING</c> and a column for <c>typed</c>, three columns for <c>raw</c>. It is within the coarse
    /// answer on the cell's state, but for the raises the coarse readings leave out (<c>TODO.md</c>), and a sub-cell's is
    /// within its cell's.
    /// </remarks>
    public sealed class PredicateAtomCellTests
    {

        const string Bigint = "typed:BIGINT";

        static readonly JsonDocumentPath A = JsonDocumentPath.Root.Property("a");

        /// <summary>Nothing known, a declared string, a non-negative integer, a domain, and a value excluded.</summary>
        static readonly PathValues[] Imports =
        {
            PathValues.Anything,
            PathValues.OfKinds(ValueSet.Of(StringType)),
            PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m))),
            PathValues.OneOf(ValueSet.Of(Of("30"), Of("x"), Of(30m), Of(2.5m), Of(true), NullValue)),
            PathValues.Meet(PathValues.AnythingThere, PathValues.Not(Of("x"))),
        };

        /// <summary>The values a cell is asked of, beside its witness: the grid's stored values and population D's.</summary>
        static readonly J?[] Pool = PredicateAtomTests.Stored.Select(PathCellTests.Decode).Concat(Declaration.Values).ToArray();

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
            foreach (var atom in Atoms())
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
            readers.Should().BeEquivalentTo(PredicateAtomTests.Readers.Append(Bigint), "every reading is checked on some value");
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
        /// leave out there: a <c>VARCHAR</c> column's read and a raw ordering, and a typed column over a structure.
        /// </summary>
        [Fact]
        public void CellAnswersStayInsideTheCoarseAnswer()
        {
            var wider = new List<string>();
            foreach (var atom in Atoms())
            {
                var leftOut = atom.Reader == PredicateAtom.RawReader || atom.Reader.StartsWith("typed:", StringComparison.Ordinal) && atom.Reader != "typed:DECIMAL"
                    ? Truths.Raises : Truths.None;
                foreach (var cell in Partitions(atom).SelectMany(p => p.Coarse.Cells.Concat(p.Fine.Cells)))
                    if ((atom.OnCell(cell) & ~atom.On(cell.Coarse) & ~leftOut) != 0)
                        wider.Add($"{atom} answers {atom.OnCell(cell)} on {cell}, and {atom.On(cell.Coarse)} on its state");
            }

            wider.Should().BeEmpty();
        }

        /// <summary>A cell cut at the atom's literals answers within the cell it lies in, cut at none.</summary>
        [Fact]
        public void ASubCellAnswersNoWider()
        {
            var wider = new List<string>();
            foreach (var atom in Atoms())
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

        /// <summary>The grid's atoms the loader builds: each literal of the kind its reading is compared with.</summary>
        static IEnumerable<PredicateAtom> Atoms() => PredicateAtomTests.Grid(PredicateAtomTests.Readers.Append(Bigint)).Where(atom => Literals(atom).All(literal => atom.Reader switch
        {
            PredicateAtom.RawReader => true,
            PredicateAtom.TextReader or "cast:VARCHAR:raw" => IsString(literal),
            "typed:BOOLEAN" => TypeOf(literal) == BooleanType,
            _ => IsNumber(literal),
        }));

        static IEnumerable<string> Literals(PredicateAtom atom) => new[] { atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty);

        /// <summary>Each import, with either set of classes, cut at nothing and at the atom's literals.</summary>
        static IEnumerable<(CellPartition Coarse, CellPartition Fine)> Partitions(PredicateAtom atom)
        {
            var literals = ValueSet.From(Literals(atom).Select(l => new CutPoint(l, CutKind.Literal)));
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
        /// a <c>VARCHAR</c>, for <c>raw</c>.
        /// </summary>
        static IEnumerable<(IrEval.TypedAs Typed, IrEval.RawAs? Raw)> Modes(PredicateAtom atom)
        {
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
            foreach (var atom in Atoms().Where(a => a.Reader is "typed:INTEGER" or Bigint or "typed:DOUBLE" or "typed:BOOLEAN" && a.Parameter == false))
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
