using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Population D in S (h10 to h12): each trial's declaration and declared documents as <see cref="PopulationD"/> draws
    /// them, and roots from a stream of their own (<see cref="Statements.Placed"/>): computed places, pairs, and a raw value
    /// beside a literal of each class. Source B plans none of them until h15, so each trial's roots go straight to one
    /// bundle a set and theory, imported as the declaration's node holds each path and generated. S's best is judged at
    /// <c>Equal</c> and its weakening at <c>Implies</c> on the declared documents, as source C judges them; and every cell
    /// S paired an atom with holds, on each declared document in it, what <see cref="IrEval"/> answers through every reader
    /// the atom stands for, raises included. The cells are the one place a raise is not tolerated, so they are what sees a
    /// raw column dropped from S's union: a column that adds only raises leaves every form as it was.
    /// </summary>
    public sealed class RenderingPopulationDTests
    {

        /// <summary>The trials every pull request pays, seeded from <see cref="PredicateSoundnessTests.Seed"/> as source B's are.</summary>
        public const int Trials = 30;

        static readonly RenderingSet[] Sets = [RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable];

        /// <summary>
        /// No form S proves of a place, a pair or a raw value has a witness among the declared documents, and no cell
        /// answers less than Calcite does on a declared document it holds; and something of each kind is judged.
        /// </summary>
        [Fact]
        public void NoPlaceOrPairHasAWitnessAndEveryCellHoldsCalcite()
        {
            var tally = Run(Trials, [ProbeTheory, SwitchedTheory], Sets);
            Print(tally);
            tally.Forms.Witnesses.Should().BeEmpty();
            tally.Faults.Should().BeEmpty();
            tally.Uncovered.Should().BeEmpty("a cell partition covers every document its import admits");
            tally.Unimported.Should().BeEmpty("the import admits every declared document");
            tally.Forms.Agreed.Should().BePositive();
            foreach (var kind in new[] { "place", "pair", "raw beside a string", "raw beside a number", "raw beside a boolean", "raw null test", "raw cast to text", "raw through its column" })
                tally.ProvenKinds.GetValueOrDefault(kind).Should().BePositive(kind);
            tally.Varchar.Should().BePositive("a VARCHAR column is read beside a string and in a null test");
        }

        /// <summary>
        /// h12's plant, caught by its answers: S's union with one raw column's rows dropped. Without <c>VARCHAR</c> a raw
        /// value that is no string is never a raise beside a string literal or under the null test, and a cell over a number,
        /// a boolean or a structure answers less than the <c>VARCHAR</c> column does; without <c>ANY</c> or <c>VARIANT</c>,
        /// the truths that column alone answers go.
        /// </summary>
        [Theory]
        [InlineData("VARCHAR")]
        [InlineData("ANY")]
        [InlineData("VARIANT")]
        public void ARawColumnDroppedFromTheUnionIsCaught(string column)
        {
            var planted = RenderingTheory.Define(Measured with { Columns = [.. Measured.Columns.Where(c => c.Column != column)] });
            var tally = Run(Trials, [planted], [RenderingSet.Production]);
            Print(tally);
            tally.Faults.Should().NotBeEmpty();
            tally.Faults.Where(f => ReadsRaw(f.Atom) == false).Should().BeEmpty("only a raw reading reads the union");
            if (column == "VARCHAR")
                tally.Faults.Where(f => f.Truth != Truths.Raises || f.Raw != IrEval.RawAs.Varchar || f.Value is J.Str).Should().BeEmpty("the column adds only its raises over what is no string");
        }

        static bool ReadsRaw(SqlPredicate atom) => atom switch
        {
            SqlPredicate.Compare c => c.Left is SqlValue.Raw or SqlValue.Cast { Value: SqlValue.Raw },
            SqlPredicate.In i => i.Value is SqlValue.Raw or SqlValue.Cast { Value: SqlValue.Raw },
            SqlPredicate.IsNull n => n.Value is SqlValue.Raw or SqlValue.Cast { Value: SqlValue.Raw },
            _ => false,
        };

        static void Print(Tally tally)
        {
            var output = TestContext.Current.TestOutputHelper;
            output?.WriteLine(tally.ToString());
            foreach (var line in tally.Faults.Take(20).Select(f => f.ToString()).Concat(tally.Uncovered.Take(20)).Concat(tally.Unimported.Take(20)).Concat(tally.Forms.Witnesses.Take(20))
                .Concat(tally.Declined.Take(20)))
                output?.WriteLine("  " + line);
        }

        /// <summary>
        /// Runs the trials from <see cref="PredicateSoundnessTests.Seed"/>: each trial's roots in one bundle a theory and set,
        /// imported from the declaration's node, generated; its forms and its cells judged on the declared documents.
        /// </summary>
        internal static Tally Run(int trials, IReadOnlyList<RenderingTheory> theories, IReadOnlyList<RenderingSet> sets)
        {
            var (tally, generations, clock) = (new Tally(), new JsonFactGenerations(), Stopwatch.StartNew());
            for (var trial = 0; trial < trials; trial++)
            {
                var seed = PredicateSoundnessTests.Seed + trial;
                var (_, _, _, declaration, documents) = PopulationD.Trial(seed);
                var roots = Statements.Placed(new Random(unchecked(seed * 15485863 + 7)), declaration);
                var scope = new CosmosOracle(new JsonFactTheory(declaration.Rules), generations).Admit(new PredicateStatement(roots));
                var (node, rules) = scope.ContextFor(OracleLicence.Declaration);

                // What the bundles judge on is what the declaration's node admits: a declared document outside it is a fault of
                // the import, which no cell then holds.
                foreach (var path in roots.SelectMany(r => r.Places).Distinct())
                    if (node is not null && rules is not null && node.TryGet(rules.Possible.Of(JsonFactEncoding.Path(path).ToTerm(rules.Json)), out var possible))
                        foreach (var document in documents.Where(d => PopulationD.Admits(possible, IrEval.At(d, path)) == false))
                            tally.Unimported.Add($"trial {trial} (seed {seed}): {SqlValue.Spell(path)} imported as {possible}, and {Documents.Json(document)} is declared");

                tally.Roots += roots.Count;
                foreach (var kind in roots.SelectMany(r => r.Subterms).Distinct().Select(KindOf).OfType<string>())
                    tally.Kinds[kind] = tally.Kinds.GetValueOrDefault(kind) + 1;

                for (var t = 0; t < theories.Count; t++)
                    foreach (var set in sets)
                    {
                        using var bundle = theories[t].Open(set);
                        var terms = roots.Select(r => bundle.Write(r)).ToList();
                        RenderingImporter.Import(bundle, Source, node, rules, null, roots.SelectMany(r => r.Places), computed: roots.SelectMany(r => r.Computed));
                        foreach (var term in terms)
                            bundle.Generate(term);

                        bundle.RunGenerate(RenderingBundle.WidthOf(roots));
                        tally.Bundles++;
                        if (bundle.Failure is { } failure)
                        {
                            tally.Declined.Add($"trial {trial} (seed {seed}) {set} theory {t}: {failure.GetType().Name}: {failure.Message}");
                            continue;
                        }

                        var world = new World(seed);
                        for (var i = 0; i < roots.Count; i++)
                        {
                            var answer = bundle.ReadBest(terms[i]);
                            if (answer is { Kind: RenderingVerdictKind.Proven, Best: { } best })
                            {
                                tally.Proven++;
                                foreach (var kind in roots[i].Subterms.Select(KindOf).OfType<string>().Distinct())
                                    tally.ProvenKinds[kind] = tally.ProvenKinds.GetValueOrDefault(kind) + 1;
                                foreach (var document in documents)
                                    tally.Forms.Judge(RenderingSoundnessTests.Tally.Calcite(roots[i], document, world), roots[i], best, Verdict.Equal, RenderPosition.Pos, document,
                                        Measured.Serves[set], $"trial {trial} {set} best");
                            }

                            if (answer.Weakening is { } weakening)
                            {
                                tally.Weakened++;
                                foreach (var document in documents)
                                    tally.Forms.Judge(RenderingSoundnessTests.Tally.Calcite(roots[i], document, world), roots[i], weakening, Verdict.Implies, RenderPosition.Pos, document,
                                        Measured.Serves[set], $"trial {trial} {set} weakening");
                            }
                        }

                        Cells(tally, bundle, roots, documents, $"trial {trial} (seed {seed}) {set} theory {t}");
                    }

                GC.KeepAlive(scope);
            }

            tally.Elapsed = clock.Elapsed;
            return tally;
        }

        /// <summary>
        /// Every cell S paired an atom with, each atom's cells read once a bundle: on each declared document the cell holds,
        /// under each reader the atom stands for, the truth <see cref="IrEval"/> answers is among the cell's. A reader the
        /// evaluator tosses a coin for, or cannot read, is left out, counted; a document no cell of the partition holds is
        /// recorded.
        /// </summary>
        static void Cells(Tally tally, RenderingBundle bundle, IReadOnlyList<SqlPredicate> roots, IReadOnlyList<J.Obj> documents, string where)
        {
            // A node is its term again, made by get-or-add: the class the roots already hold.
            var atoms = roots.SelectMany(r => r.Subterms).Where(n => n is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Case or SqlPredicate.Constant))
                .DistinctBy(n => n.Digest).ToDictionary(n => (object)bundle.Write(n).Id, n => n);
            var seen = new HashSet<(string, string)>();
            foreach (var row in bundle.Scope.Rows(bundle.Theory.Outcomes))
            {
                if (atoms.TryGetValue(row.Keys[0], out var atom) == false || row.Output is not CellOutcomes outcomes)
                    continue;

                var cells = outcomes.Cells.Where(c => seen.Add((atom.Digest, c.Cell.Text))).ToList();
                if (cells.Count == 0)
                    continue;

                tally.Cells += cells.Count;
                foreach (var document in documents)
                    foreach (var (typed, raw) in Modes(atom))
                    {
                        var world = new World(tally.Checked + tally.Tossed);
                        if (Value(atom, document, world, typed) is not { } value)
                            continue;

                        var holding = cells.Where(c => Holds(c.Cell, value)).ToList();
                        if (holding.Count == 0)
                        {
                            if (outcomes.Cells.Any(c => Holds(c.Cell, value)) == false)
                                tally.Uncovered.Add($"{where}: {atom} over {Documents.Json(document)}: no cell of {outcomes.Path} holds {value}");
                            continue;
                        }

                        Truths truth;
                        try
                        {
                            truth = IrEval.Truth(atom, document, world, typed, raw);
                        }
                        catch (NotSupportedException)
                        {
                            tally.Unread++;
                            continue;
                        }

                        if (world.Tossed > 0)
                        {
                            tally.Tossed++;
                            continue;
                        }

                        tally.Checked++;
                        if (raw == IrEval.RawAs.Varchar)
                            tally.Varchar++;

                        // A computed null may be either cell the service reads it in; any holding the truth answers it.
                        if (holding.All(c => (c.Class.Calcite & truth) == 0))
                            tally.Faults.Add(new(where, atom, holding[0].Cell, holding[0].Class.Calcite, truth, typed, raw, document, value.Stored));
                    }
            }
        }

        /// <summary>A kind of atom the trials count, so that each is seen: <c>null</c> for one counted under no kind.</summary>
        static string? KindOf(SqlPredicate atom) => atom switch
        {
            SqlPredicate.Compare { Left: SqlValue.Call } or SqlPredicate.In { Value: SqlValue.Call } or SqlPredicate.IsNull { Value: SqlValue.Call } => "place",
            SqlPredicate.Compare { Right: not SqlValue.Literal } => "pair",
            _ when ColumnOf(atom) is not null => "raw through its column",
            SqlPredicate.Compare { Left: SqlValue.Cast { Value: SqlValue.Raw } } or SqlPredicate.In { Value: SqlValue.Cast { Value: SqlValue.Raw } }
                or SqlPredicate.IsNull { Value: SqlValue.Cast { Value: SqlValue.Raw } } => "raw cast to text",
            SqlPredicate.IsNull { Value: SqlValue.Raw } => "raw null test",
            SqlPredicate.Compare { Left: SqlValue.Raw, Right: SqlValue.Literal l } => "raw beside a " + Class(l.Value),
            SqlPredicate.In { Value: SqlValue.Raw } i => "raw beside a " + Class(i.Points.First()),
            _ => null,
        };

        /// <summary>The column an atom's raw value is read through, where it names one (rawtyped).</summary>
        static string? ColumnOf(SqlPredicate atom) => atom switch
        {
            SqlPredicate.Compare c => ColumnOf(c.Left),
            SqlPredicate.In i => ColumnOf(i.Value),
            SqlPredicate.IsNull n => ColumnOf(n.Value),
            _ => null,
        };

        static string? ColumnOf(SqlValue value) => value switch { SqlValue.Raw r => r.Column, SqlValue.Cast { Value: SqlValue.Raw r } => r.Column, _ => null };

        static string Class(string literal) => TypeOf(literal) switch { StringType => "string", BooleanType => "boolean", _ => "number" };

        /// <summary>
        /// The readers an atom stands for, as the cells read them: a <c>VARIANT</c>, an <c>ANY</c> and, where every literal
        /// beside it is a string or there is none, a <c>VARCHAR</c> for a raw value and its cast to text; <c>RETURNING</c>
        /// and the column for a typed reading but <c>DECIMAL</c>'s, whose column was not measured; <c>RETURNING</c> otherwise.
        /// </summary>
        static IEnumerable<(IrEval.TypedAs Typed, IrEval.RawAs? Raw)> Modes(SqlPredicate atom)
        {
            var (values, literals) = atom switch
            {
                SqlPredicate.Compare c => (new[] { c.Left, c.Right }, c.Right is SqlValue.Literal l ? new[] { l.Value } : []),
                SqlPredicate.In i => (new[] { i.Value }, i.Points.ToArray()),
                SqlPredicate.IsNull n => (new[] { n.Value }, Array.Empty<string>()),
                _ => (Array.Empty<SqlValue>(), Array.Empty<string>()),
            };

            IrEval.RawAs?[] raws = ColumnOf(atom) is { } column ? [IrEval.Named(column)]
                : values.Any(v => v is SqlValue.Raw or SqlValue.Cast { Value: SqlValue.Raw })
                ? literals.All(IsString) ? [IrEval.RawAs.Variant, IrEval.RawAs.Any, IrEval.RawAs.Varchar] : [IrEval.RawAs.Variant, IrEval.RawAs.Any]
                : [null];
            IrEval.TypedAs[] typed = values.Any(v => v is SqlValue.Typed { Type: not "DECIMAL" }) ? [IrEval.TypedAs.Returning, IrEval.TypedAs.Column] : [IrEval.TypedAs.Returning];
            return from t in typed from r in raws select (t, r);
        }

        /// <summary>
        /// What an atom's place holds on a document, as a cell holds it: a path's stored value; both of a pair's and how
        /// they lie; a computed place's value under the reader, a null as either a null or nothing. <c>null</c> where the
        /// place is none of these, or its value is a raise.
        /// </summary>
        static Held? Value(SqlPredicate atom, J.Obj document, World world, IrEval.TypedAs typed)
        {
            var value = atom switch
            {
                SqlPredicate.Compare c => c.Left,
                SqlPredicate.In i => i.Value,
                SqlPredicate.IsNull n => n.Value,
                SqlPredicate.Affix a => a.Value,
                _ => null,
            };

            switch (atom, value)
            {
                case (SqlPredicate.Compare { Right: not SqlValue.Literal and { PathRead: { } right } }, { PathRead: { } left }):
                    return new(IrEval.At(document, left), [], IrEval.At(document, right));

                case (_, SqlValue.Call { Spelled: not null } call):
                    return IrEval.Read(call, document, world, typed) switch
                    {
                        SqlDatum.NullDatum => new(new J.Null(), [null, NullValue]),
                        SqlDatum.Text t => new(new J.Str(t.Value), [Of(t.Value)]),
                        SqlDatum.Exact x => new(new J.Num(x.Value), [new J.Num(x.Value).Encoded]),
                        SqlDatum.Boolean b => new(new J.Bool(b.Value), [Of(b.Value)]),
                        _ => null,
                    };

                case (_, { PathRead: { } path }) when atom.Places.Count() == 1:
                    return new(IrEval.At(document, path), [PathCellTests.Encode(IrEval.At(document, path))]);

                default:
                    return null;
            }
        }

        /// <summary>Whether a cell holds a place's value: one of its spellings, or for a pair each side and how they lie.</summary>
        static bool Holds(PathCell cell, Held value) => cell switch
        {
            PathCell.Pair p when value.Right is var right && value.Spellings.Count == 0 => p.Left.Contains(PathCellTests.Encode(value.Stored)) && p.Right.Contains(PathCellTests.Encode(right))
                && (p.Order is not int order || Order(value.Stored, right) == order),
            PathCell.Pair => false,
            _ => value.Spellings.Count > 0 && value.Spellings.Any(cell.Contains),
        };

        /// <summary>
        /// How a pair's left value lies against its right: by UTF-16 unit for two strings, by value for two numbers, and
        /// level for two alike, as a place against itself lies on every cell.
        /// </summary>
        static int? Order(J? left, J? right) => (left, right) switch
        {
            (J.Str l, J.Str r) => Math.Sign(string.CompareOrdinal(l.Value, r.Value)),
            (J.Num l, J.Num r) => l.Value.CompareTo(r.Value),
            (J.Num or J.Huge, J.Num or J.Huge) => Math.Sign(Double(left!).CompareTo(Double(right!))),
            _ when PathCellTests.Encode(left) == PathCellTests.Encode(right) => 0,
            _ => null,
        };

        static double Double(J number) => number is J.Num n ? (double)n.Value : ((J.Huge)number).Value;

        /// <summary>What a place holds: the stored or computed value, its spellings as a cell holds one, and a pair's right side.</summary>
        sealed record Held(J? Stored, IReadOnlyList<string?> Spellings, J? Right = null)
        {
            public override string ToString() => (Stored is null ? "nothing" : Documents.Json(Stored)) + (Spellings.Count == 0 ? " and " + (Right is null ? "nothing" : Documents.Json(Right)) : "");
        }

        /// <summary>A cell answering less than Calcite does on a declared document it holds.</summary>
        internal sealed record Fault(string Where, SqlPredicate Atom, PathCell Cell, Truths Answers, Truths Truth, IrEval.TypedAs Typed, IrEval.RawAs? Raw, J.Obj Document, J? Value)
        {
            public override string ToString() => $"{Where}: {Atom} is {Truth} of {Documents.Json(Document)} read {Typed} {Raw}, in {Cell}, where S answers {Answers}";
        }

        /// <summary>What the trials judged.</summary>
        internal sealed class Tally
        {

            public int Roots, Bundles, Proven, Weakened, Cells, Checked, Varchar, Tossed, Unread;

            public TimeSpan Elapsed;

            public RenderingSoundnessTests.Tally Forms { get; } = new();

            public Dictionary<string, int> Kinds { get; } = new(StringComparer.Ordinal);

            public List<Fault> Faults { get; } = [];

            public Dictionary<string, int> ProvenKinds { get; } = new(StringComparer.Ordinal);

            public List<string> Uncovered { get; } = [];

            public List<string> Unimported { get; } = [];

            public List<string> Declined { get; } = [];

            public override string ToString() => $"{Roots} roots, {Bundles} bundles ({Declined.Count} declined), {Elapsed.TotalSeconds:N0} s; atoms {string.Join(", ", Kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}; "
                + $"proven {string.Join(", ", ProvenKinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}; forms: {Proven} best proven, {Weakened} weakened, {Forms}; cells: {Cells} read, {Checked} answers checked ({Varchar} through VARCHAR), {Tossed} a coin, {Unread} unread, "
                + $"{Uncovered.Count} documents in no cell, {Unimported.Count} outside the import, {Faults.Count} faults";

        }

    }

}
