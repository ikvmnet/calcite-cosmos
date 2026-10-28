using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The importer: a copy of the licensed node's rows, one per path per table, refused after the run and from a second
    /// source, a differing second copy loud, and a path the node holds no row for counted as the declaration says.
    /// </summary>
    public sealed class RenderingImporterTests
    {

        /// <summary>A declared string at <c>a</c>, an object at <c>o</c>, and something present at <c>x.y</c>.</summary>
        static JsonFactTheory Declaration(JsonFactGenerations generations) => Theory(
            Outright(P("a"), Present), Outright(P("a"), Type(JsonType.String)), Outright(P("o"), Type(JsonType.Object)), Outright(P("x", "y"), Present)).In(generations);

        /// <summary>
        /// Every path a statement reads is imported as the node the licence chose holds it: round 1's node is
        /// <c>D0</c>, over every container the plan digests plan over, every path each mentions, a child of each and a
        /// path none mentions; and a later round's, a stratum, narrowed by the conjunct admitted before.
        /// </summary>
        [Fact]
        public void TheImportEqualsTheNodesPossible()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var (held, narrowed, mentioned) = (0, 0, 0);

            void Compare(CosmosStatementScope scope, int conjunct)
            {
                var (node, rules) = scope.ContextFor(OracleLicence.RenderConjunct(conjunct));
                var paths = scope.Statement.Conjuncts.SelectMany(c => c.Paths).Distinct().ToList();
                using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
                var report = RenderingImporter.Import(bundle, Source, node, rules, null, paths);
                (report.Paths, report.Contradicted).Should().Be((paths.Count, false));

                foreach (var path in paths)
                {
                    var term = JsonFactEncoding.Path(path).ToTerm(rules!.Json);
                    var s = JsonFactEncoding.Path(path).ToTerm(ProbeTheory.Paths);
                    var possible = node!.TryGet(rules.Possible.Of(term), out var p) ? p : null;
                    var chars = node.TryGet(rules.Json.Chars.Of(term), out var c) ? c : JsonValues.AllBands;
                    bundle.Scope.Get(ProbeTheory.Imported.Of(s)).Should().Be(possible ?? PathValues.Anything, SqlValue.Spell(path));
                    bundle.Scope.Get(ProbeTheory.ImportedChars.Of(s)).Should().Equal(chars, SqlValue.Spell(path));
                    held += possible is null ? 0 : 1;
                    narrowed += possible is null || possible == PathValues.Anything ? 0 : 1;
                }

                report.DefaultedOpen.Should().Be(paths.Count - paths.Count(p => node!.TryGet(rules!.Possible.Of(JsonFactEncoding.Path(p).ToTerm(rules.Json)), out _)));
                GC.KeepAlive(scope);
            }

            foreach (var container in CosmosDifferentialTests.Containers.Concat(PlannerSchema.All).Where(c => c.Facts.MentionedPaths.Count > 0))
            {
                var theory = container.Facts.In(generations);
                mentioned += theory.MentionedPaths.Count;
                var paths = theory.MentionedPaths.SelectMany(p => new[] { p, p.Property("zz") }).Append(P("unmentioned"));
                Compare(new CosmosOracle(theory, generations).Admit(new PredicateStatement(paths.Select(p => new SqlPredicate.IsDefined(Raw(p))).ToArray())), 0);
            }

            var a = Eq(Text(P("a")), "x");
            var stratum = new CosmosOracle(Declaration(generations), generations).Admit(new PredicateStatement(new[] { a, Eq(Text(P("o", "b")), "y") }), (i, _) => i == 0);
            stratum.StratumOf(1).Should().Be(0, "the second conjunct renders from the stratum the first was admitted to");
            Compare(stratum, 1);

            held.Should().Be(mentioned + 2, "D0 holds every path its declaration mentions, and the stratum both its statement reads");
            narrowed.Should().BePositive();
        }

        /// <summary>The plant "an import after <c>Run</c>": the run has read every import it will read, so a later one is refused.</summary>
        [Fact]
        public void AnImportAfterRunIsRefused()
        {
            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(Ne(Text(LabelPath), "bikes"));
            RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath]);
            bundle.Claim(root, Guarded);
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
            FluentActions.Invoking(() => RenderingImporter.Import(bundle, Source, null, null, null, [KindPath])).Should().Throw<InvalidOperationException>();
        }

        /// <summary>
        /// The plant "two imports of one path": S never meets two values of a path, so a second that differs throws; the
        /// importer reads one node alike each time, so the same value again is the same row.
        /// </summary>
        [Fact]
        public void ASecondImportOfOnePathThrows()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = Declaration(generations);
            var scope = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { Eq(Text(P("a")), "x") }));
            var (d0, rules) = scope.ContextFor(OracleLicence.RenderConjunct(0));

            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            RenderingImporter.Import(bundle, Source, d0, rules, theory, [P("a")]);
            RenderingImporter.Import(bundle, Source, d0, rules, theory, [P("a")]);
            FluentActions.Invoking(() => bundle.Import(Source with { Node = d0 }, P("a"), PathValues.AnythingThere, JsonValues.AllBands)).Should().Throw<FactConflictException>();
            bundle.Failure.Should().BeNull();
            GC.KeepAlive(scope);
        }

        /// <summary>
        /// The plant "a second source": one bundle holds one node's facts, so an import from another is refused, named
        /// otherwise or named alike: a declaration's context and its <c>D0</c> under one statement, licence and round.
        /// </summary>
        [Fact]
        public void ASecondSourceIsRefused()
        {
            using (var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet))
            {
                RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath]);
                FluentActions.Invoking(() => RenderingImporter.Import(bundle, Source with { Round = 2 }, null, null, null, [KindPath])).Should().Throw<InvalidOperationException>();
            }

            var theory = Declaration(new JsonFactGenerations(long.MaxValue));
            var declaration = theory.Declaration()!;
            var rules = PredicateRules.Of(declaration.Generation.Contexts);
            using var twice = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            RenderingImporter.Import(twice, Source, declaration.Context, rules, theory, [P("a")]);
            FluentActions.Invoking(() => RenderingImporter.Import(twice, Source, declaration.DeclaredPossible(), rules, theory, [P("o")])).Should().Throw<InvalidOperationException>();
        }

        /// <summary>
        /// A stratum is stated beside an atom only as a later round of the import's statement, of a path it imported, and
        /// admitting nothing the import does not; and an atom is stated beneath one stratum. Each plant is refused.
        /// </summary>
        [Fact]
        public void AStratumIsStatedOfALaterRoundBesideItsImportAlone()
        {
            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var atom = Eq(Text(LabelPath), "x");
            var strings = PathValues.OfKinds(ValueSet.Of(JsonValues.StringType));
            bundle.Write(atom);
            bundle.Import(Source, LabelPath, strings, JsonValues.AllBands);
            var later = Source with { Round = Source.Round + 1 };

            FluentActions.Invoking(() => bundle.Assume(Source, atom, strings)).Should().Throw<InvalidOperationException>("the root's own round is no later one");
            FluentActions.Invoking(() => bundle.Assume(later with { Statement = "another" }, atom, strings)).Should().Throw<InvalidOperationException>("another statement's");
            FluentActions.Invoking(() => bundle.Assume(later, Eq(Text(KindPath), "a"), strings)).Should().Throw<InvalidOperationException>("a path not imported");
            FluentActions.Invoking(() => bundle.Assume(later, atom, PathValues.Anything)).Should().Throw<InvalidOperationException>("more than the import admits");
            bundle.Assume(later, atom, PathValues.Meet(strings, PathValues.Not(JsonValues.Of("y"))));
            FluentActions.Invoking(() => bundle.Assume(later, atom, PathValues.Meet(strings, PathValues.Not(JsonValues.Of("z"))))).Should().Throw<InvalidOperationException>("beneath two strata");
        }

        /// <summary>
        /// Two imports that differ spell apart, though <see cref="PathValues"/> prints them alike: one string holding a
        /// comma, against two strings, would otherwise share a memo entry and its verdict.
        /// </summary>
        [Fact]
        public void TwoImportsThatDifferSpellApart()
        {
            var (one, two) = (PathValues.Anything with { Values = ValueSet.Of("string|a, string|b") }, PathValues.Anything with { Values = ValueSet.Of("string|a", "string|b") });
            one.ToString().Should().Be(two.ToString());
            RenderingImporter.Spell(LabelPath, one, JsonValues.AllBands).Should().NotBe(RenderingImporter.Spell(LabelPath, two, JsonValues.AllBands));
        }

        /// <summary>
        /// A path the node holds no row for is the top: open where the declaration mentions nothing at, within or above it,
        /// and constrained beneath a mentioned path, above one, or at one the node holds nothing of; a node that keeps no
        /// document imports nothing.
        /// </summary>
        [Fact]
        public void ADefaultedPathIsOpenOrConstrainedAsTheDeclarationSays()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = Declaration(generations);
            var paths = new[] { P("a"), P("o", "b"), P("x"), P("x", "y"), P("c"), P("c", "d") };
            var statement = new PredicateStatement(paths.Select(p => new SqlPredicate.IsDefined(Raw(p))).ToArray());

            // D0 holds a and x.y; o.b lies beneath o and x above x.y; c and c.d are spoken of nowhere.
            var scope = new CosmosOracle(theory, generations).Admit(statement);
            var (d0, rules) = scope.ContextFor(OracleLicence.RenderConjunct(0));
            Report(d0, rules).Should().Match<ImportReport>(r => r.Paths == 6 && r.DefaultedOpen == 2 && r.Constrained.Count == 2 && r.Contradicted == false);

            // The declaration's own context holds no possible row: every mentioned path's default is constrained.
            Report(theory.Declaration()!.Context, rules).Should().Match<ImportReport>(r => r.DefaultedOpen == 2 && r.Constrained.Count == 4);

            // A node that keeps no document: a declared string equal to two values.
            var nothing = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { Eq(Text(P("a")), "x"), Eq(Text(P("a")), "y") }));
            nothing.IsContradictory(OracleLicence.Kept).Should().BeTrue();
            var (none, kept) = nothing.ContextFor(OracleLicence.Kept);
            Report(none, kept).Should().Be(new ImportReport(0, 0, ValueSet<string>.Empty, "contradicted", true));
            GC.KeepAlive(scope);

            ImportReport Report(Scope? node, PredicateRules? rules)
            {
                using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
                var report = RenderingImporter.Import(bundle, Source, node, rules, theory, paths);
                bundle.Scope.CountRows(ProbeTheory.Imported).Should().Be(report.Paths);
                return report;
            }
        }

    }

}
