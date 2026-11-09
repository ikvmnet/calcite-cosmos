using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Source C: the IR the non-legacy loader reads from h07 to h11 written straight to a bundle, which source B cannot
    /// plan until S reads that IR at h15. Each root's places are imported, S generates, and its best is judged at
    /// <c>Equal</c> and its weakening at <c>Implies</c> by <see cref="RenderingSoundnessTests.Tally"/>, IrEval against
    /// CosmosEval on every target the set serves, under <c>Production</c>, the emulator and <c>Portable</c>, through
    /// production's theory, which asks every template since the switch (h15). Three populations: <c>CASE</c> trees over type tests, comparisons, null and
    /// presence tests, an affix and the truth constants; a numeric comparison under its <c>IS_NUMBER</c> guard through
    /// every numeric reader, at 2^53 and beside a fraction; and pairs of readings and computed places, the counts and an
    /// arithmetic operator among them, bare, negated, beneath a guard and as a condition, over strings of every band.
    /// </summary>
    public sealed class RenderingSourceCTests
    {

        static readonly JsonDocumentPath X = P("x"), N = P("n");

        static readonly RenderingSet[] Sets = [RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable];

        /// <summary>Random <c>CASE</c> trees three deep, every one holding a <c>CASE</c>, over 17 values at each of two paths.</summary>
        [Fact]
        public void RandomCasesAreHeldToCalcite()
        {
            J?[] values =
            [
                null, new J.Null(), new J.Bool(true), new J.Bool(false), new J.Num(0), new J.Num(5), new J.Num(6), new J.Num(5.5m), new J.Num(-1),
                new J.Str("a"), new J.Str("abc"), new J.Str("5"), new J.Str(""), new J.Str("b"), new J.Str("true"),
                new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal)), new J.Arr([]),
            ];
            SqlPredicate[] atoms =
            [
                new SqlPredicate.TypeTest("IS_NUMBER", Raw(X)), new SqlPredicate.TypeTest("IS_STRING", Raw(X)), new SqlPredicate.TypeTest("IS_NULL", Raw(N)),
                new SqlPredicate.TypeTest("IS_BOOL", Raw(N)), new SqlPredicate.TypeTest("IS_PRIMITIVE", Raw(X)),
                Eq(Text(X), "a"), Ne(Text(X), "a"), Cmp(Text(N), "<", Lit("5")), Cmp(Text(X), ">", Lit("a")),
                IsNull(Text(X)), IsNull(Text(N)), new SqlPredicate.IsDefined(Raw(N)), new SqlPredicate.Affix(PredicateAtom.LikeTest, Text(X), "a%"),
                Eq(Raw(X), "a"), Eq(Text(N), "5"), new SqlPredicate.Constant(true), new SqlPredicate.Constant(false), new SqlPredicate.Constant(null),
            ];

            SqlPredicate Tree(Random random, int depth) => depth == 0 || random.Next(4) == 0 ? atoms[random.Next(atoms.Length)] : random.Next(6) switch
            {
                0 => Not(Tree(random, depth - 1)),
                1 => new SqlPredicate.And([Tree(random, depth - 1), Tree(random, depth - 1)]),
                2 => new SqlPredicate.Or([Tree(random, depth - 1), Tree(random, depth - 1)]),
                _ => new SqlPredicate.Case(Tree(random, depth - 1), Tree(random, depth - 1), Tree(random, depth - 1)),
            };

            var random = new Random(1);
            var roots = Enumerable.Range(0, 400).Select(_ => Tree(random, 3)).Where(r => r.Subterms.Any(t => t is SqlPredicate.Case)).Distinct().Take(60).ToList();
            var documents = (from x in values from n in values select RenderingDocumentSampler.Document([SqlValue.Spell(X), SqlValue.Spell(N)], [x, n])).ToList();
            Judge(roots.Select(r => (r, PathValues.Anything, AllBands, documents)));
        }

        /// <summary>A comparison of a number under its guard, as C2 and C4n write it, through every numeric reader.</summary>
        [Fact]
        public void GuardedNumbersAreHeldToCalcite()
        {
            J?[] values =
            [
                null, new J.Null(), new J.Bool(true), new J.Str("30"), new J.Str(" 30"), new J.Str("abc"), new J.Str("9007199254740993"),
                new J.Num(30), new J.Num(30.0m), new J.Num(30.5m), new J.Num(30.0000000000000001m), new J.Num(29.9999999999999999m),
                new J.Num(9007199254740992), new J.Num(9007199254740993), new J.Num(9007199254740993.0m), new J.Num(1e20m), new J.Num(9223372036854775808m),
                new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal)), new J.Arr([]),
            ];
            SqlValue[] readers =
            [
                Typed(N, "DECIMAL"), Typed(N, "DOUBLE"), Typed(N, "BIGINT"), Typed(N, "INTEGER"),
                new SqlValue.Cast("INTEGER", Text(N)), new SqlValue.Cast("BIGINT", Text(N)), new SqlValue.Cast("DOUBLE", Text(N)),
            ];
            var isNumber = new SqlPredicate.TypeTest("IS_NUMBER", Raw(N));
            var roots = new List<SqlPredicate>();
            foreach (var reader in readers)
                foreach (var op in new[] { "=", "<" })
                    foreach (var literal in new JsonScalar[] { 30L, 9007199254740993L, 30.5m })
                    {
                        var compared = Cmp(reader, op, Lit(literal));
                        roots.AddRange([
                            new SqlPredicate.Case(isNumber, compared, new SqlPredicate.Constant(false)),
                            new SqlPredicate.And([isNumber, compared]),
                            Not(new SqlPredicate.Case(isNumber, compared, new SqlPredicate.Constant(false))),
                        ]);
                    }

            var documents = values.Select(v => RenderingDocumentSampler.Document([SqlValue.Spell(N)], [v])).ToList();
            Judge(roots.Select(r => (r, PathValues.Anything, AllBands, documents)));
        }

        /// <summary>
        /// Pairs of readings and computed places, the counts and an arithmetic operator among them, over strings of every
        /// band, ASCII strings and anything.
        /// </summary>
        [Fact]
        public void PairsAndPlacesAreHeldToCalcite()
        {
            J?[] values =
            [
                null, new J.Null(), new J.Bool(true), new J.Num(1), new J.Num(1.5m),
                new J.Str("a"), new J.Str("b"), new J.Str("A"), new J.Str(""), new J.Str(" a"), new J.Str("ab"), new J.Str("﷐"), new J.Str("Ａ"),
                new J.Str("😀"), new J.Str("\0"), new J.Str("ß"), new J.Str(""),
                new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal)), new J.Arr([]),
            ];

            static string? Spelling(SqlValue operand) => operand switch
            {
                SqlValue.Call call => call.Spelled,
                SqlValue.Literal literal => literal.Value,
                _ when operand.PathRead is JsonDocumentPath path => SqlValue.Spell(path),
                _ => null,
            };

            static SqlValue Place(string function, string type, params SqlValue[] operands)
            {
                var call = new SqlValue.Call(function, operands);
                return Apache.Calcite.Cosmos.Adapter.Sql.CosmosFunctionSpellings.Spell(call, Spelling) is string spelled ? call with { Type = type, Spelled = spelled }
                    : throw new InvalidOperationException($"{call} is spelled by nothing, so the loader reads no place of it.");
            }

            var atoms = new List<SqlPredicate>();
            foreach (var op in new[] { "=", "<", ">=" })
                atoms.AddRange([
                    Cmp(Text(X), op, Text(N)), Cmp(Text(X), op, Text(X)), Cmp(Typed(X, "BIGINT"), op, Typed(N, "BIGINT")),
                    Cmp(Place("UPPER", "VARCHAR", Text(X)), op, Lit("A")), Cmp(Place("REVERSE", "VARCHAR", Text(X)), op, Lit("ba")),
                    Cmp(Place("SUBSTRING", "VARCHAR", Text(X), Lit(1L), Lit(1L)), op, Lit("a")), Cmp(Place("TRIM BOTH", "VARCHAR", Lit(" "), Text(X)), op, Lit("a")),
                    Cmp(Place("CHAR_LENGTH", "INTEGER", Text(X)), op, Lit(1L)), Cmp(Place("POSITION", "INTEGER", Lit("a"), Text(X)), op, Lit(1L)),
                    Cmp(Place("+", "INTEGER", Typed(N, "INTEGER"), Lit(1L)), op, Lit(2L)),
                ]);

            var guard = new SqlPredicate.IsDefined(Raw(N));
            var roots = atoms.SelectMany(atom => new[]
            {
                atom, Not(atom), new SqlPredicate.Case(guard, atom, new SqlPredicate.Constant(false)), new SqlPredicate.Case(atom, new SqlPredicate.Constant(true), Eq(Text(N), "a")),
            }).ToList();

            static bool Ascii(J? v) => v is J.Str t && t.Value.All(c => c >= 0x20 && c < 0x7F) && t.Value.Trim() == t.Value;
            List<J.Obj> Over(Func<J?, bool> holds) =>
                [.. from x in values where holds(x) from n in values where holds(n) select RenderingDocumentSampler.Document([SqlValue.Spell(X), SqlValue.Spell(N)], [x, n])];

            var (anything, strings, ascii) = (Over(_ => true), Over(v => v is J.Str), Over(Ascii));
            Judge([
                .. roots.Select(r => (r, PathValues.Anything, AllBands, anything)),
                .. roots.Select(r => (r, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, strings)),
                .. roots.Select(r => (r, PathValues.OfKinds(ValueSet.Of(StringType)), ClassSet(JsonCharacterBand.None), ascii)),
            ]);
        }

        /// <summary>
        /// Writes each root to a bundle under every set through production's theory, imports its places as given, generates, and
        /// judges what S answers on the documents; no witness may stand, and something must be proven.
        /// </summary>
        static void Judge(IEnumerable<(SqlPredicate Root, PathValues Values, ValueSet<string> Chars, List<J.Obj> Documents)> population)
        {
            var tally = new RenderingSoundnessTests.Tally();
            var (roots, proven) = (0, 0);
            foreach (var (root, values, chars, documents) in population)
            {
                roots++;
                foreach (var set in Sets)
                {
                    using var bundle = ProbeTheory.Open(set);
                    var term = bundle.Write(root);
                    foreach (var path in root.Places)
                        bundle.Import(Source, path, values, chars);

                    bundle.Generate(term);
                    bundle.RunGenerate(RenderingBundle.WidthOf([root]));
                    var answer = bundle.ReadBest(term);
                    var world = new World(roots);
                    if (answer is { Kind: RenderingVerdictKind.Proven, Best: { } best })
                    {
                        proven++;
                        foreach (var document in documents)
                            tally.Judge(RenderingSoundnessTests.Tally.Calcite(root, document, world), root, best, Verdict.Equal, RenderPosition.Pos, document, Measured.Serves[set], $"{set} best");
                    }

                    if (answer.Weakening is { } weakening)
                        foreach (var document in documents)
                            tally.Judge(RenderingSoundnessTests.Tally.Calcite(root, document, world), root, weakening, Verdict.Implies, RenderPosition.Pos, document, Measured.Serves[set], $"{set} weakening");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{roots} roots, {proven} proven: {tally}");
            tally.Witnesses.Should().BeEmpty();
            proven.Should().BePositive("a source that proves nothing judges nothing");
        }

    }

}
