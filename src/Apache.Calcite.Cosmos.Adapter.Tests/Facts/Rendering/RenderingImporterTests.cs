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
        static JsonFactTheory Declaration(JsonFactGenerations generations) => generations.Bind(Theory(
            Outright(P("a"), Present), Outright(P("a"), Type(JsonType.String)), Outright(P("o"), Type(JsonType.Object)), Outright(P("x", "y"), Present)));

        /// <summary>
        /// Every path a statement reads is imported as the node the licence chose holds it: round 1's node, armed, is
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
                using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
                var report = RenderingImporter.Import(bundle, Source, node, rules, null, paths);
                (report.Paths, report.Contradicted).Should().Be((paths.Count, false));

                foreach (var path in paths)
                {
                    var term = JsonFactEncoding.Path(path).ToTerm(rules!.Json);
                    var s = JsonFactEncoding.Path(path).ToTerm(ProbeTheory.Paths);
                    var possible = node!.TryGet(rules.Possible.Of(term), out var p) ? p : null;
                    var chars = possible is not null && node.TryGet(rules.Json.Chars.Of(term), out var c) ? c : JsonValues.AllBands;
                    bundle.Scope.Get(ProbeTheory.Imported.Of(s)).Should().Be(possible ?? PathValues.Anything, SqlValue.Spell(path));
                    bundle.Scope.Get(ProbeTheory.ImportedChars.Of(s)).Should().Equal(chars, SqlValue.Spell(path));
                    held += possible is null ? 0 : 1;
                    narrowed += possible is null || possible == PathValues.Anything ? 0 : 1;
                }

                report.DefaultedOpen.Should().Be(paths.Count - paths.Count(p => node.TryGet(rules!.Possible.Of(JsonFactEncoding.Path(p).ToTerm(rules.Json)), out _)));
                GC.KeepAlive(scope);
            }

            foreach (var container in CosmosDifferentialTests.Containers.Concat(PlannerSchema.All).Where(c => c.Facts.MentionedPaths.Count > 0))
            {
                var theory = generations.Bind(container.Facts);
                mentioned += theory.MentionedPaths.Count;
                var paths = theory.MentionedPaths.SelectMany(p => new[] { p, p.Property("zz") }).Append(P("unmentioned"));
                using (StatementTerms.ArmDeclaredPossible())
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
            using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
            var root = bundle.Write(Ne(Text(LabelPath), "bikes"));
            RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath]);
            bundle.Claim(root, Guarded);
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
            FluentActions.Invoking(() => RenderingImporter.Import(bundle, Source, null, null, null, [KindPath])).Should().Throw<InvalidOperationException>();
        }

        /// <summary>
        /// The plant "two imports of one path": S never meets two values of a path, so a second that differs throws, from
        /// the importer or from the bundle; the same value again is the same row.
        /// </summary>
        [Fact]
        public void ASecondImportOfOnePathThrows()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = Declaration(generations);
            using var armed = StatementTerms.ArmDeclaredPossible();
            var scope = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { Eq(Text(P("a")), "x") }));
            var (d0, rules) = scope.ContextFor(OracleLicence.RenderConjunct(0));

            using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
            RenderingImporter.Import(bundle, Source, d0, rules, theory, [P("a")]);
            RenderingImporter.Import(bundle, Source, d0, rules, theory, [P("a")]);
            FluentActions.Invoking(() => RenderingImporter.Import(bundle, Source, null, null, theory, [P("a")])).Should().Throw<FactConflictException>();
            FluentActions.Invoking(() => bundle.Import(Source, P("a"), PathValues.AnythingThere, JsonValues.AllBands)).Should().Throw<FactConflictException>();
            bundle.Failure.Should().BeNull();
            GC.KeepAlive(scope);
        }

        /// <summary>The plant "a second source": one bundle holds one node's facts, so an import from another is refused.</summary>
        [Fact]
        public void ASecondSourceIsRefused()
        {
            using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
            RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath]);
            FluentActions.Invoking(() => RenderingImporter.Import(bundle, Source with { Round = 2 }, null, null, null, [KindPath])).Should().Throw<InvalidOperationException>();
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

            // Armed, D0 holds a and x.y; o.b lies beneath o and x above x.y; c and c.d are spoken of nowhere.
            CosmosStatementScope scope;
            using (StatementTerms.ArmDeclaredPossible())
                scope = new CosmosOracle(theory, generations).Admit(statement);
            var (d0, rules) = scope.ContextFor(OracleLicence.RenderConjunct(0));
            Report(d0, rules).Should().Match<ImportReport>(r => r.Paths == 6 && r.DefaultedOpen == 2 && r.DefaultedConstrained == 2 && r.Contradicted == false);

            // Unarmed, the declaration's own context holds no possible row: every mentioned path's default is constrained.
            var (context, unarmed) = new CosmosOracle(theory, generations).Admit(statement).ContextFor(OracleLicence.RenderConjunct(0));
            Report(context, unarmed).Should().Match<ImportReport>(r => r.DefaultedOpen == 2 && r.DefaultedConstrained == 4);

            // A node that keeps no document: a declared string equal to two values.
            var nothing = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { Eq(Text(P("a")), "x"), Eq(Text(P("a")), "y") }));
            nothing.IsContradictory(OracleLicence.Kept).Should().BeTrue();
            var (none, kept) = nothing.ContextFor(OracleLicence.Kept);
            Report(none, kept).Should().Be(new ImportReport(0, 0, 0, "contradicted", true));
            GC.KeepAlive(scope);

            ImportReport Report(Scope? node, PredicateRules? rules)
            {
                using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
                var report = RenderingImporter.Import(bundle, Source, node, rules, theory, paths);
                bundle.Scope.CountRows(ProbeTheory.Imported).Should().Be(report.Paths);
                return report;
            }
        }

    }

}
