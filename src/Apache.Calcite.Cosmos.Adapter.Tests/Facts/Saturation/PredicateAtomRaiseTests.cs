using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// Where Calcite may raise on a document holding a value of a class (<see cref="PredicateAtom.RaisesOver"/>), over the
    /// facts graph's own reading table and <c>rawunion</c> (<see cref="FactsReadings"/>), not a probe's, which gives a raw
    /// reader rows the facts graph lacks: wherever the evaluator raises on a value of the class, through any reader the
    /// reading stands for and in any world, or fails to compile the atom, the atom says it may raise; and on the atoms a
    /// reduce rests on, it says it raises nowhere.
    /// </summary>
    public sealed class PredicateAtomRaiseTests
    {

        const string Text = PredicateAtom.TextReader;
        const string Raw = PredicateAtom.RawReader;

        static readonly JsonDocumentPath A = JsonDocumentPath.Root.Property("a");

        static readonly string[] TextPlaces = [PredicateAtom.TypedReader("VARCHAR"), PredicateAtom.TypedReader("CHAR")];

        static readonly string CastOfRaw = PredicateAtom.CastReader("VARCHAR", Raw);

        static readonly string CastToDouble = PredicateAtom.CastReader("DOUBLE", Text);

        /// <summary>
        /// Every reading the vocabulary defines: the grid's, <c>BIGINT</c> and the narrow typed columns, the typed text places,
        /// and a raw value and its cast to text in each column (<c>RawIn</c>).
        /// </summary>
        static readonly string[] Readers = [.. PredicateAtomTests.Readers, PredicateAtom.TypedReader("BIGINT"),
            .. new[] { "FLOAT", "REAL", "TINYINT", "SMALLINT" }.Select(PredicateAtom.TypedReader), .. TextPlaces,
            .. from column in PredicateAtom.Columns from reader in new[] { Raw, CastOfRaw } select PredicateAtom.InColumn(reader, column)];

        /// <summary>The grid's literals, beside numbers a double holds only near, past 2^53 and whole-valued, and strings that render a number or none.</summary>
        static readonly string[] Literals = [.. PredicateAtomTests.Literals, Of(0.1m), Of(0.10000000000000001m), Of(9007199254740993m), Of(6.0000000000000001m), Of(""), Of("2.5")];

        static readonly string[] Operators = [PredicateAtom.Equal, PredicateAtom.NotEqual, PredicateAtom.Less, PredicateAtom.LessOrEqual, PredicateAtom.Greater, PredicateAtom.GreaterOrEqual];

        /// <summary><c>LIKE</c>'s patterns: whole, prefix, suffix, infix, every string, two F does not decide, and one holding U+0000.</summary>
        static readonly string[] Patterns = ["30", "3%", "%0", "%r%", "%", "", "_0", @"a\%", "a\u0000%"];

        /// <summary>Every class a path may hold, as the facts graph knows it.</summary>
        static readonly (string Name, PathValues Values)[] Classes =
        [
            ("absent", PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind))),
            ("null", PathValues.OfKinds(ValueSet.Of(NullType))),
            ("true", PathValues.Meet(PathValues.OfKinds(ValueSet.Of(BooleanType)), PathValues.OneOf(ValueSet.Of(Of(true))))),
            ("false", PathValues.Meet(PathValues.OfKinds(ValueSet.Of(BooleanType)), PathValues.OneOf(ValueSet.Of(Of(false))))),
            ("integer", PathValues.OfKinds(ValueSet.Of(IntegerType))),
            ("fraction", PathValues.OfKinds(ValueSet.Of(FractionType))),
            ("string", PathValues.OfKinds(ValueSet.Of(StringType))),
            ("object", PathValues.OfKinds(ValueSet.Of(ObjectType))),
            ("array", PathValues.OfKinds(ValueSet.Of(ArrayType))),
        ];

        /// <summary>The classes that hold a value: all but absence and null.</summary>
        static IEnumerable<(string Name, PathValues Values)> Holding() => Classes.Where(c => c.Name is not ("absent" or "null"));

        /// <summary>
        /// The values the evaluator's documents draw: population D's, the grid's stored values, numbers a double holds only
        /// near, past an integer type's range and whole-valued, and strings that parse as a number or do not; each whole
        /// number in both its spellings.
        /// </summary>
        static readonly J?[] Pool = Declaration.Values.Cast<J?>().Append(null).Concat(PredicateAtomTests.Stored.Select(PathCellTests.Decode))
            .Concat([new J.Num(0.1m), new J.Num(0.10000000000000001m), new J.Num(0.0000001m), new J.Num(6.0000000000000001m), new J.Num(9007199254740993m),
                new J.Num(2147483648m), new J.Num(127.5m), new J.Num(-129m), new J.Num(40000m),
                new J.Str("2"), new J.Str("2.5"), new J.Str("0.1"), new J.Str("a\u0000b"), new J.Str("中"), new J.Str("null"), new J.Str("1e400"), new J.Str("[1]")])
            .SelectMany(v => v is J.Num n && n.Value == decimal.Truncate(n.Value) ? new J?[] { new J.Num(NumberOf(Of(n.Value))), new J.Num(NumberOf(Of(n.Value)) + 0.0m) } : new[] { v })
            .DistinctBy(v => v is null ? "absent" : v.GetType().Name + Documents.Json(v))
            .ToArray();

        /// <summary>
        /// The worlds asked: every world population D draws in a run of the suite, each trial's seeded as its own, so that
        /// what a coin or a parameter may say where nothing was measured is asked as the population asks it.
        /// </summary>
        static readonly int[] Worlds = [.. Enumerable.Range(0, PredicateSoundnessTests.Trials).Select(t => PredicateSoundnessTests.Seed + t)];

        /// <summary>
        /// Wherever the evaluator raises on a value of a class, through any reader the atom's reading stands for and in any
        /// world, or fails to compile the atom's predicate, the atom says it may raise over the class: every reading, the
        /// raw readings in a column among them, every test, a parameter by every operator, and every class.
        /// </summary>
        [Fact]
        public void EveryRaiseTheEvaluatorFindsIsOneTheAtomMayMake()
        {
            var missed = new List<string>();
            var (atoms, raising, quiet, asked) = (0, 0, 0, 0);
            var readers = new HashSet<string>();
            foreach (var atom in Atoms())
            {
                atoms++;
                readers.Add(atom.Reader);
                var predicate = Predicate(atom);
                foreach (var (name, values) in Classes)
                {
                    if (atom.RaisesOver(values, FactsReadings.Columns, FactsReadings.Rows))
                    {
                        raising++;
                        continue;
                    }

                    quiet++;
                    if (IrEval.FailsToCompile(predicate))
                        missed.Add($"{atom} fails to compile, and says it raises nowhere over {name}");
                    else if (FirstRaise(atom, predicate, values, ref asked) is { } raise)
                        missed.Add($"{atom} raises on {raise}, and says it raises nowhere over {name}");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{atoms:N0} atoms; {raising:N0} classes may raise, {quiet:N0} raise nowhere, over which {asked:N0} answers were asked");
            missed.Should().BeEmpty("the facts graph assumes nothing of an atom that may raise");
            readers.Should().BeEquivalentTo(Readers, "every reading is asked");
        }

        /// <summary>
        /// The first value of a class, reader and world on which the evaluator raises, as it is read; <c>null</c> for none.
        /// An answer that read no parameter and tossed no coin is the same in every world, and is asked in the first alone.
        /// </summary>
        static string? FirstRaise(PredicateAtom atom, SqlPredicate predicate, PathValues values, ref int asked)
        {
            foreach (var value in Pool.Where(v => PopulationD.Admits(values, v)))
                foreach (var (typed, raw) in Modes(atom))
                    foreach (var seed in Worlds)
                    {
                        asked++;
                        var (document, world) = (Document(value), new World(seed));
                        if (IrEval.Truth(predicate, document, world, typed, raw) == Truths.Raises || IrEval.Raises(predicate, document, world, typed, raw))
                            return $"{PathCellTests.Show(value)} read {typed} {raw} in world {seed}";

                        if (atom.Parameter == false && world.Tossed == 0)
                            break;
                    }

            return null;
        }

        /// <summary>
        /// The atoms that must say they may raise: an ordering against a parameter of a raw value in a <c>VARIANT</c>, which
        /// fails to compile whatever the path holds, absent included; in an <c>ANY</c>, which raises across types wherever the
        /// path holds a value, a string among them, and the same of the bare raw value; a <c>LIKE</c> whose pattern holds
        /// U+0000, which Calcite reads as its escape; and the first design's counterexample, the cast of the text to
        /// <c>DOUBLE</c> over a path that may hold an integer or a string.
        /// </summary>
        [Fact]
        public void TheseMayRaise()
        {
            var unraised = new List<string>();
            void Raises(PredicateAtom atom, string name, PathValues values)
            {
                if (atom.RaisesOver(values, FactsReadings.Columns, FactsReadings.Rows) == false)
                    unraised.Add($"{atom} over {name}");
            }

            foreach (var op in Operators.Skip(2))
            {
                foreach (var (name, values) in Classes)
                    Raises(PredicateAtom.CompareParameter(PredicateAtom.RawIn("VARIANT"), op), name, values);

                foreach (var (name, values) in Holding())
                {
                    Raises(PredicateAtom.CompareParameter(PredicateAtom.RawIn("ANY"), op), name, values);
                    Raises(PredicateAtom.CompareParameter(Raw, op), name, values);
                }
            }

            foreach (var reader in new[] { Text, CastOfRaw, PredicateAtom.RawIn("VARCHAR"), TextPlaces[0] })
                foreach (var (name, values) in Classes)
                    Raises(PredicateAtom.Affix(reader, PredicateAtom.LikeTest, "a\u0000%"), name, values);

            foreach (var op in Operators)
                Raises(PredicateAtom.Compare(CastToDouble, op, Of(30m)), "an integer or a string", PathValues.OfKinds(ValueSet.Of(IntegerType, StringType)));

            unraised.Should().BeEmpty();
        }

        /// <summary>
        /// The atoms a reduce rests on raise nowhere: the text over every class beside a string, its null test and a
        /// parameter, and over strings a <c>LIKE</c> whose pattern holds no U+0000; a raw value in a <c>VARCHAR</c> over
        /// strings beside a string, its null test and a parameter; a <c>VARIANT</c>'s equality over every class beside a
        /// literal of any class or a parameter; and the cast of the text to <c>DOUBLE</c> over numbers beside a number, its
        /// null test and a parameter.
        /// </summary>
        [Fact]
        public void TheseRaiseNowhere()
        {
            var raised = new List<string>();
            void Quiet(PredicateAtom atom, string name, PathValues values)
            {
                if (atom.RaisesOver(values, FactsReadings.Columns, FactsReadings.Rows))
                    raised.Add($"{atom} over {name}");
            }

            IEnumerable<PredicateAtom> Beside(string reader, IEnumerable<string> literals) =>
                (from literal in literals from op in Operators select PredicateAtom.Compare(reader, op, literal))
                .Concat(literals.Select(literal => PredicateAtom.In(reader, ValueSet.Of(literal, Of("y")))))
                .Append(PredicateAtom.IsNull(reader))
                .Concat(Operators.Select(op => PredicateAtom.CompareParameter(reader, op)));

            var strings = Literals.Where(IsString).ToArray();
            foreach (var (name, values) in Classes)
                foreach (var atom in Beside(Text, strings))
                    Quiet(atom, name, values);

            var aString = PathValues.OfKinds(ValueSet.Of(StringType));
            foreach (var pattern in Patterns.Where(p => p.Contains('\u0000') == false))
                Quiet(PredicateAtom.Affix(Text, PredicateAtom.LikeTest, pattern), "a string", aString);

            foreach (var atom in Beside(PredicateAtom.RawIn("VARCHAR"), strings))
                Quiet(atom, "a string", aString);

            foreach (var (name, values) in Classes)
            {
                foreach (var literal in Literals)
                    foreach (var op in Operators.Take(2))
                        Quiet(PredicateAtom.Compare(PredicateAtom.RawIn("VARIANT"), op, literal), name, values);

                foreach (var op in Operators.Take(2))
                    Quiet(PredicateAtom.CompareParameter(PredicateAtom.RawIn("VARIANT"), op), name, values);
            }

            foreach (var (name, values) in new[] { ("an integer", PathValues.OfKinds(ValueSet.Of(IntegerType))), ("a fraction", PathValues.OfKinds(ValueSet.Of(FractionType))) })
                foreach (var atom in Beside(CastToDouble, Literals.Where(IsNumber)).Where(a => a.Test != PredicateAtom.InTest))
                    Quiet(atom, name, values);

            raised.Should().BeEmpty();
        }

        /// <summary>
        /// A definedness or type test over every reading a row names raises where its reading raises or its body does, as the
        /// rows say directly (<c>testreads</c>, <c>Calcite.TypeTestsAreTheirBodies</c>): over a raw value in a column the body
        /// raises on every class, absent included; the body reads the raw value of the text, a raw value and a
        /// <c>RETURNING</c>, so a test of the text or the raw value raises nowhere and answers its row's kinds, and a test of a
        /// <c>RETURNING</c> raises at least where the <c>RETURNING</c> does on a value of the class.
        /// </summary>
        [Fact]
        public void ATestRaisesWhereItsReadingOrItsBodyRaises()
        {
            var wrong = new List<string>();
            var bodies = new HashSet<string>();
            foreach (var reader in Readers)
            {
                if (BodyOf(reader) is not { } body)
                    continue;

                bodies.Add(body);
                foreach (var atom in Tests(reader, body))
                    foreach (var (name, values) in Classes)
                    {
                        var raises = atom.RaisesOver(values, FactsReadings.Columns, FactsReadings.Rows);
                        if (body == PredicateAtom.RaisesReader)
                        {
                            if (raises == false)
                                wrong.Add($"{atom} raises nowhere over {name}, where its body raises everywhere");

                            continue;
                        }

                        body.Should().Be(Raw, "a reading's test body reads its raw value or raises");
                        if (reader is Text or Raw)
                        {
                            if (raises)
                                wrong.Add($"{atom} may raise over {name}, where neither the text, the raw value nor the body does");

                            foreach (var state in PredicateAtom.State.Enumerate(values))
                            {
                                var kind = state switch
                                {
                                    PredicateAtom.State.Absent => PathValues.AbsentKind,
                                    PredicateAtom.State.Null => NullType,
                                    PredicateAtom.State.Scalar s => TypeOf(s.Value),
                                    PredicateAtom.State.AnyString => StringType,
                                    PredicateAtom.State.AnyNumber n => n.Integral ? IntegerType : FractionType,
                                    PredicateAtom.State.Structure s => s.Type,
                                    _ => throw new InvalidOperationException($"No class yields {state}."),
                                };
                                var expected = atom.Test == PredicateAtom.DefinedTest ? kind == PathValues.AbsentKind ? Truths.False : Truths.True
                                    : atom.Kinds!.Contains(kind) ? Truths.True : Truths.False;
                                if (atom.OnCell(PathCell.Of(state), FactsReadings.Columns, FactsReadings.Rows) != expected)
                                    wrong.Add($"{atom} answers {atom.OnCell(PathCell.Of(state), FactsReadings.Columns, FactsReadings.Rows)} over {state}, where its row says {expected}");
                            }
                        }
                        else if (raises == false && Pool.Where(v => PopulationD.Admits(values, v)).Any(v => IrEval.Read(Reading(reader), Document(v), new World(0)) is SqlDatum.Raised))
                            wrong.Add($"{atom} raises nowhere over {name}, where its RETURNING raises");
                    }
            }

            wrong.Should().BeEmpty();
            bodies.Should().BeEquivalentTo([Raw, PredicateAtom.RaisesReader], "both kinds of body are asked");
        }

        /// <summary>
        /// The undecided convention: each answer that decides nothing is exactly <see cref="Truths.Any"/>, so it counts as a
        /// raise: a <c>LIKE</c> whose rows read no string as itself, every <c>LIKE</c> over a raw reader; an unknown state or
        /// cell; a test no case covers; the text compared with a literal that is no string; and a numeric cast compared with
        /// one that is no number. The answers narrower than that drop no raise: the enumeration yields no unknown state for
        /// any class, and a pattern the units' rows leave unread is asked of the rows themselves. Any other undecided answer
        /// narrower than <see cref="Truths.Any"/> that drops a raise the evaluator finds fails
        /// <see cref="EveryRaiseTheEvaluatorFindsIsOneTheAtomMayMake"/>.
        /// </summary>
        [Fact]
        public void AnUndecidedAnswerIsEveryTruthValue()
        {
            var thirty = new PathCell.Point(Of("30"));
            var undecided = new List<(PredicateAtom Atom, PathCell Cell)>();
            foreach (var reader in PredicateAtom.Columns.SelectMany(c => new[] { PredicateAtom.RawIn(c), PredicateAtom.InColumn(CastOfRaw, c) }).Append(CastOfRaw))
                undecided.Add((PredicateAtom.Affix(reader, PredicateAtom.LikeTest, "3%"), thirty));

            undecided.Add((PredicateAtom.Compare(Text, PredicateAtom.Equal, Of("x")), new PathCell.Unknown()));
            undecided.Add((PredicateAtom.In(Text, ValueSet<string>.Empty), thirty));
            foreach (var op in Operators)
            {
                undecided.Add((PredicateAtom.Compare(Text, op, Of(30m)), thirty));
                foreach (var cast in new[] { CastToDouble, PredicateAtom.CastReader("INTEGER", Text), PredicateAtom.CastReader("BIGINT", Text) })
                    undecided.Add((PredicateAtom.Compare(cast, op, Of("x")), new PathCell.Point(Of(30m))));
            }

            foreach (var (atom, cell) in undecided)
                atom.OnCell(cell, FactsReadings.Columns, FactsReadings.Rows).Should().Be(Truths.Any, $"{atom} decides nothing on {cell}");

            PredicateAtom.Compare(Text, PredicateAtom.Equal, Of("x")).On(new PredicateAtom.State.Unknown(), FactsReadings.Rows).Should().Be(Truths.Any);
            Classes.Select(c => c.Values).Append(PathValues.Anything).SelectMany(PredicateAtom.State.Enumerate).Should().NotContain(s => s is PredicateAtom.State.Unknown,
                "a test's T and F over an unknown state drop no raise only where no class yields one");

            var unread = PredicateAtom.Affix(Text, PredicateAtom.LikeTest, "a\u0000%");
            unread.OnCell(new PathCell.Point(Of("a")), FactsReadings.Columns, FactsReadings.Rows).Should().Be(Truths.True | Truths.False, "LIKE answers an unread pattern as it would a read one");
            unread.RaisesOver(PathValues.OfKinds(ValueSet.Of(StringType)), FactsReadings.Columns, FactsReadings.Rows).Should().BeTrue("the rows leave U+0000 unread");
        }

        /// <summary>
        /// The atoms over every reading: its null test, a parameter by every operator, each literal it is compared with by
        /// every operator and in a membership, <c>LIKE</c> over the readings that read text, and each test over the readings a
        /// row names.
        /// </summary>
        static IEnumerable<PredicateAtom> Atoms() =>
            from reader in Readers
            from atom in new[] { PredicateAtom.IsNull(reader) }
                .Concat(Operators.Select(op => PredicateAtom.CompareParameter(reader, op)))
                .Concat(from literal in Literals where Compared(reader, literal) from op in Operators select PredicateAtom.Compare(reader, op, literal))
                .Concat(from literal in Literals where Compared(reader, literal) select PredicateAtom.In(reader, ValueSet.Of(literal, Beside(literal))))
                .Concat(Textual(reader) || reader == PredicateAtom.RawIn("VARCHAR") ? Patterns.Select(p => PredicateAtom.Affix(reader, PredicateAtom.LikeTest, p)) : [])
                .Concat(BodyOf(reader) is { } body ? Tests(reader, body) : [])
            select atom;

        /// <summary>Each type test its row names and the definedness test over a reading, read as its body reads it.</summary>
        static IEnumerable<PredicateAtom> Tests(string reader, string body) =>
            FactsReadings.TestKinds.Where(row => row.Test != "IS_DEFINED").Select(row => PredicateAtom.TypeTest(reader, row.Test, row.Kinds, body))
                .Append(PredicateAtom.IsDefined(reader, body));

        /// <summary>
        /// What a test's body reads over a reading, as its row says (<c>testreads</c>): the text, a raw value and every
        /// <c>RETURNING</c> by their constructors, a raw value in a column by its own reading; <c>null</c> for a cast, which no
        /// row names.
        /// </summary>
        static string? BodyOf(string reader)
        {
            var key = reader.StartsWith(PredicateAtom.TypedReaders + ":", StringComparison.Ordinal) ? PredicateAtom.TypedReaders : reader;
            return CosmosAxiomRows.ReadTestReads(CosmosAxioms.All).Where(row => row.Reader == key).Select(row => row.As).FirstOrDefault();
        }

        /// <summary>
        /// Whether the loader compares a reading with a literal: a raw value with any, but a <c>VARCHAR</c> column with a string
        /// alone; the text, a typed text place and a cast of a raw value with a string; a boolean reading with a boolean; and
        /// every other with a number.
        /// </summary>
        static bool Compared(string reader, string literal) => reader switch
        {
            _ when reader == PredicateAtom.RawIn("VARCHAR") => IsString(literal),
            _ when PredicateAtom.IsRaw(reader) => true,
            _ when Textual(reader) => IsString(literal),
            "typed:BOOLEAN" or "cast:BOOLEAN:text" => TypeOf(literal) == BooleanType,
            _ => IsNumber(literal),
        };

        static bool Textual(string reader) => PredicateAtom.Columnless(reader) is Text || PredicateAtom.Columnless(reader) == CastOfRaw || TextPlaces.Contains(reader);

        /// <summary>A second point of a literal's class, for a membership.</summary>
        static string Beside(string literal) => TypeOf(literal) switch
        {
            StringType => Of("y"),
            BooleanType => Of(false),
            _ => Of(7m),
        };

        /// <summary>
        /// The readers a reading stands for, as the evaluator names them: a raw value and its cast through its own column, a
        /// <c>CHAR</c>'s as the union reads it; unnamed, through none, a <c>VARIANT</c>, an <c>ANY</c> and, beside strings
        /// alone, a <c>VARCHAR</c>; <c>RETURNING</c> and a column for <c>typed</c>, but the columns of <c>DECIMAL</c> and the
        /// text places, which were not measured.
        /// </summary>
        static IEnumerable<(IrEval.TypedAs Typed, IrEval.RawAs? Raw)> Modes(PredicateAtom atom)
        {
            if (PredicateAtom.ColumnOf(atom.Reader) is { } column && IrEval.Named(column) is { } named)
                return [(IrEval.TypedAs.Returning, named)];

            if (PredicateAtom.IsRaw(atom.Reader) || PredicateAtom.Columnless(atom.Reader) == CastOfRaw)
                return new IrEval.RawAs?[] { null, IrEval.RawAs.Variant, IrEval.RawAs.Any, IrEval.RawAs.Varchar }
                    .Where(raw => raw != IrEval.RawAs.Varchar || new[] { atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty).All(IsString))
                    .Select(raw => (IrEval.TypedAs.Returning, raw));

            if (atom.Reader.StartsWith(PredicateAtom.TypedReaders + ":", StringComparison.Ordinal) && atom.Reader != "typed:DECIMAL" && TextPlaces.Contains(atom.Reader) == false)
                return [(IrEval.TypedAs.Returning, null), (IrEval.TypedAs.Column, null)];

            return [(IrEval.TypedAs.Returning, null)];
        }

        /// <summary>An atom as the IR's predicate over the path <c>a</c>, a parameter typed as what it is compared with.</summary>
        static SqlPredicate Predicate(PredicateAtom atom)
        {
            var value = Reading(atom.Reader);
            return atom.Test switch
            {
                _ when atom.Kinds is not null => new SqlPredicate.TypeTest(atom.Test, value),
                PredicateAtom.DefinedTest => new SqlPredicate.IsDefined(value),
                PredicateAtom.LikeTest => new SqlPredicate.Affix(atom.Test, value, StringOf(atom.Operand!)),
                PredicateAtom.NullTest => new SqlPredicate.IsNull(value),
                PredicateAtom.InTest => new SqlPredicate.In(value, atom.Points!),
                _ when atom.Parameter => new SqlPredicate.Compare(atom.Test, value, new SqlValue.Parameter(0, atom.Reader.Split(':') is [_, var type, ..] ? type : "VARCHAR")),
                _ => new SqlPredicate.Compare(atom.Test, value, new SqlValue.Literal(atom.Operand!)),
            };
        }

        static SqlValue Reading(string reader) => reader.Split(':', 3) switch
        {
            [Text] => new SqlValue.Text(A),
            [Raw] => new SqlValue.Raw(A),
            [Raw, var column] => new SqlValue.Raw(A) { Column = column },
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
