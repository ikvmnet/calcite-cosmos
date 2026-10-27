using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The production oracle (spec g11): its key holds every value a bundle reads, an opaque term's reading among them, and
    /// no parameter's value; a decline is counted and forgotten, a run that stopped short remembered; and where S cannot be
    /// defined, or is not ready within the wait, every question declines, a remembered answer is still served, and nothing
    /// throws.
    /// </summary>
    public sealed class RenderingOracleTests
    {

        /// <summary>An oracle of its own whose questions wait for the first build however long it takes, as a busy CI host needs.</summary>
        static RenderingOracle Oracle(Func<int, int>? limit = null) => new(new RenderingGraph(() => Measured), wait: Timeout.InfiniteTimeSpan, limit: limit);

        static RenderingOracle.RenderingBundleAnswer Ask(RenderingOracle oracle, IReadOnlyList<SqlPredicate> roots, IReadOnlyDictionary<long, SqlValue>? opaques = null) =>
            oracle.Ask(RenderingCheckRun.AssertedSet, roots, opaques, Source, null, null);

        /// <summary>
        /// A statement of two rounds: <c>a = 'x'</c> admitted in the first, from <c>D0</c>, and <c>a &lt;&gt; 'y'</c> rendered
        /// in the second, from the stratum where <c>a</c> is <c>'x'</c>. Every value its bundle reads — each path's
        /// <c>possible</c> and <c>chars</c>, and the atom stated beneath its stratum — is spelled in the key, and narrowing
        /// any of them, or stating nothing beneath, keys the question apart. A node its owner let go declines.
        /// </summary>
        [Fact]
        public void TheKeyHoldsEveryValueTheImportReads()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = Theory(Outright(P("a"), Present), Outright(P("a"), Type(JsonType.String)), Outright(P("o"), Type(JsonType.Object))).In(generations);
            var (first, second) = (Eq(Text(P("a")), "x"), Ne(Text(P("a")), "y"));
            var scope = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { first, second, Eq(Text(P("o", "b")), "z") }), (i, _) => i == 0);
            var (d0, rules) = scope.ContextFor(OracleLicence.RenderConjunct(0));
            var (stratum, _) = scope.ContextFor(OracleLicence.RenderConjunct(1));
            var source = new ImportSource(scope.Statement.Digest, "declaration", 1);
            var later = source with { Licence = "render round 2", Round = 2 };
            SqlPredicate[] roots = [first, second, scope.Statement.Conjuncts[2]];
            var paths = roots.SelectMany(r => r.Paths).Distinct().ToList();

            var oracle = Oracle();
            RenderingOracle.RenderingBundleAnswer Asked(Scope? node, Scope? beneath) =>
                oracle.Ask(RenderingCheckRun.AssertedSet, roots, null, source, node, rules, beneath is null ? null : [(second, later, beneath, rules)]);

            var asked = Asked(d0, stratum);
            var key = asked.Key!;
            (asked.Declined, key.Set, key.Theory).Should().Be(((string?)null, RenderingCheckRun.AssertedSet, RenderingAnswerMemo.DigestOf(ProbeTheory)));
            key.Roots.Split('\n').Should().BeEquivalentTo(roots.Select(r => r.Digest));

            // What the oracle's bundle read, read again in a bundle of the same import, run as the oracle runs it.
            using (var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet))
            {
                var terms = roots.ToDictionary(r => r, r => bundle.Write(r));
                RenderingImporter.Import(bundle, source, d0, rules, null, paths, [(second, later, stratum, rules)]);
                foreach (var term in terms.Values)
                    bundle.Generate(term);

                bundle.RunGenerate(RenderingBundle.WidthOf(roots)).StopReason.Should().Be(RunStopReason.Completed);
                foreach (var path in paths)
                {
                    var s = JsonFactEncoding.Path(path).ToTerm(ProbeTheory.Paths);
                    key.Import.Should().Contain(RenderingImporter.Spell(path, bundle.Scope.Get(ProbeTheory.Imported.Of(s)), bundle.Scope.Get(ProbeTheory.ImportedChars.Of(s))), SqlValue.Spell(path));
                }

                var assumed = bundle.Scope.Get(ProbeTheory.Assumed.Of(terms[second]));
                assumed.Should().NotBe(bundle.Scope.Get(ProbeTheory.Imported.Of(JsonFactEncoding.Path(P("a")).ToTerm(ProbeTheory.Paths))), "the stratum narrows the import");
                key.Import.Should().Contain(JsonSerializer.Serialize(second.ToString()) + " beneath " + RenderingImporter.Spell(P("a"), assumed, ValueSet<string>.Empty));
            }

            // Narrowed otherwise, or stated nowhere, the import keys apart: each another bundle.
            var others = new[] { Asked(theory.Declaration()!.Context, stratum), Asked(d0, d0), Asked(d0, null) };
            others.Select(o => o.Key!.Import).Append(key.Import).Should().OnlyHaveUniqueItems();
            Asked(d0, stratum).Cost.Hits.Should().Be(1, "the same question again is the remembered answer");
            oracle.Counts.Should().Be((1L, 4L, 0L));

            // A node is its scope's, disposed once its owner lets it go: asked over one it never read, the oracle declines and
            // throws nothing.
            var gone = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { first, second, Eq(Text(P("o", "b")), "z") }), (i, _) => i == 0).ContextFor(OracleLicence.RenderConjunct(1)).Context!;
            gone.Dispose();
            Asked(d0, gone).Should().Match<RenderingOracle.RenderingBundleAnswer>(a => a.Declined == RenderingOracle.Engine && a.Key == null && a.Of(second).Reason!.Contains("ObjectDisposedException"));
            GC.KeepAlive(scope);
        }

        /// <summary>
        /// An opaque term is numbered within its loading, so two conjuncts the loader reads apart may share a digest:
        /// <c>UPPER(name) = 'X'</c> and <c>CONCAT_WS('-', name, sku) &lt;&gt; 'x'</c>, each alone. The value each was read
        /// as is in the key, so the second is not handed the first's presence.
        /// </summary>
        [Fact]
        public void AnOpaquesReadingIsInTheKey()
        {
            var types = new JavaTypeFactoryImpl();
            var rex = new RexBuilder(types);
            var doc = rex.makeInputRef(types.createSqlType(SqlTypeName.VARCHAR), 0);
            RexNode Read(string path) => rex.makeCall(types.createSqlType(SqlTypeName.VARCHAR), SqlStdOperatorTable.JSON_VALUE, java.util.Arrays.asList(doc, rex.makeLiteral("$." + path)));
            PredicateStatement Loaded(RexNode conjunct) => new CosmosPredicateLoader.Loading([CosmosPath.Root("c")], [CosmosReading.Typed], "c").Statement([conjunct]);
            var upper = Loaded(rex.makeCall(SqlStdOperatorTable.EQUALS, rex.makeCall(SqlStdOperatorTable.UPPER, Read("name")), rex.makeLiteral("X")));
            var unlisted = Loaded(rex.makeCall(SqlStdOperatorTable.NOT_EQUALS, rex.makeCall(SqlLibraryOperators.CONCAT_WS, rex.makeLiteral("-"), Read("name"), Read("sku")), rex.makeLiteral("x")));
            upper.Conjuncts.Single().Should().Be(unlisted.Conjuncts.Single(), "both are the loading's first opaque term");

            var oracle = Oracle();
            var (strict, none, unread) = (Ask(oracle, upper.Conjuncts, upper.Opaques), Ask(oracle, unlisted.Conjuncts, unlisted.Opaques), Ask(oracle, upper.Conjuncts));
            new[] { strict, none, unread }.Select(a => a.Key!.Roots).Should().OnlyHaveUniqueItems();
            strict.Key!.Roots.Should().Contain(upper.Opaques.Values.Single().ToString());
            (strict.Of(upper.Conjuncts[0]).Weakening, none.Of(unlisted.Conjuncts[0]).Weakening, unread.Of(upper.Conjuncts[0]).Weakening)
                .Should().Be((ServiceForm.TestOf(CosmosServiceTest.IsDefined, SqlValue.Spell(P("name"))), (ServiceForm?)null, (ServiceForm?)null));
            oracle.Counts.Bundles.Should().Be(3);
        }

        /// <summary>
        /// A parameter is spelled by its ordinal and type: one prepared statement, whatever it is executed with, is one key,
        /// and S gives it presence alone, since no template spells a parameter.
        /// </summary>
        [Fact]
        public void AParameterNeverReachesTheKey()
        {
            var oracle = Oracle();
            var conjunct = Cmp(Text(LabelPath), "<>", Param(0));
            var answer = Ask(oracle, [conjunct]);
            answer.Key!.Roots.Should().Be(conjunct.Digest).And.Contain("?0:VARCHAR");
            (answer.Of(conjunct).Kind, answer.Of(conjunct).Best, answer.Of(conjunct).Weakening)
                .Should().Be((RenderingVerdictKind.Unproven, (ServiceForm?)null, ServiceForm.TestOf(CosmosServiceTest.IsDefined, Label)));
            Ask(oracle, [conjunct]).Cost.Hits.Should().Be(1);
        }

        /// <summary>
        /// A bundle the engine failed — here a primitive that throws while the test says so — declines: counted, its roots
        /// Unproven and named, and forgotten, so the same question asked again opens a bundle that answers, and is remembered.
        /// Eight askers at once of one key share one bundle, and a waiter handed another's decline counts no hit.
        /// </summary>
        [Fact]
        public async Task ADeclineIsNotRemembered()
        {
            var failing = false;
            var oracle = new RenderingOracle(new RenderingGraph(() => Measured with
            {
                Leaf = (f, z, t, q) => Volatile.Read(ref failing) ? throw new InvalidOperationException("planted") : Measured.Leaf(f, z, t, q),
            }));
            await oracle.Ready;

            Volatile.Write(ref failing, true);
            var declined = Ask(oracle, [NotBikes]);
            Volatile.Write(ref failing, false);
            (declined.Declined, declined.Of(NotBikes).Kind, oracle.Memo.Count).Should().Be((RenderingOracle.Engine, RenderingVerdictKind.Unproven, 0));
            declined.Of(NotBikes).Reason.Should().StartWith("declined: engine: RuleEvaluationException");

            Ask(oracle, [NotBikes]).Should().Match<RenderingOracle.RenderingBundleAnswer>(a => a.Declined == null && a.Of(NotBikes).Kind == RenderingVerdictKind.Proven);
            Ask(oracle, [NotBikes]).Cost.Hits.Should().Be(1);
            oracle.Counts.Should().Be((1L, 2L, 1L));

            var shared = Oracle();
            await Together(() => Ask(shared, [Ne(Text(LabelPath), "boots")]));
            shared.Counts.Should().Be((7L, 1L, 0L));
            Volatile.Write(ref failing, true);
            await Together(() => Ask(oracle, [Ne(Text(LabelPath), "shoes")]));
            Volatile.Write(ref failing, false);
            (oracle.Counts.Hits, oracle.Counts.Declines).Should().Be((1L, 9L), "a waiter handed a decline counts it as one");
        }

        /// <summary>Eight threads of their own asking at once.</summary>
        static async Task Together(Action ask)
        {
            using var together = new Barrier(8);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
            {
                together.SignalAndWait();
                ask();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
        }

        /// <summary>
        /// A run that stops short of its limit reads <c>Incomplete</c>, which is an answer, not a decline: limits are counts,
        /// so the same question would stop alike, and the second ask opens no bundle.
        /// </summary>
        [Fact]
        public void AnIncompleteIsRemembered()
        {
            var oracle = Oracle(limit: _ => 3);
            var first = Ask(oracle, [NotBikes]);
            (first.Declined, first.Of(NotBikes).Kind).Should().Be(((string?)null, RenderingVerdictKind.Incomplete));
            first.Cost.Iterations.Should().BeInRange(1, 3);
            Ask(oracle, [NotBikes]).Should().Match<RenderingOracle.RenderingBundleAnswer>(a => a.Cost.Hits == 1 && a.Of(NotBikes).Kind == RenderingVerdictKind.Incomplete);
            oracle.Counts.Should().Be((1L, 1L, 0L));
        }

        /// <summary>
        /// S unavailable declines every question and throws nothing: a definition that throws — a row contradicting
        /// another, G1's — or whose generate mode fails its warm-up, for the rest of the process; a build not finished within
        /// the wait, until it is, each question of a plan waiting no longer than the first.
        /// </summary>
        [Fact]
        public async Task AFailedDefinitionDeclinesAndCounts()
        {
            var key = MeasuredKey.Of(CosmosServiceTest.NotEqual, CosmosValueClass.String, CosmosValueClass.Null);
            var failed = new RenderingOracle(new RenderingGraph(() => RenderingDefinition.Measured([.. Rows, new("Service.Planted", key, RenderingTarget.Azure, CosmosAnswer.False)])));
            await failed.Ready;
            foreach (var _ in Enumerable.Range(0, 2))
                Ask(failed, [NotBikes]).Should().Match<RenderingOracle.RenderingBundleAnswer>(a => a.Declined == RenderingOracle.Unavailable && a.Key == null
                    && a.Of(NotBikes).Kind == RenderingVerdictKind.Unproven && a.Of(NotBikes).Weakening == null && a.Of(NotBikes).Reason!.StartsWith("declined: unavailable"));
            (failed.Available, failed.Counts, failed.Memo.Count).Should().Be((false, (0L, 0L, 2L), 0));

            var broken = new RenderingOracle(new RenderingGraph(() => Measured with { Leaf = (_, _, _, _) => throw new InvalidOperationException("planted") }));
            await broken.Ready;
            broken.Available.Should().BeFalse("a generation whose every run fails would decline every question at a bundle's cost");

            using var gate = new ManualResetEventSlim(false);
            var waiting = new RenderingOracle(new RenderingGraph(() =>
            {
                gate.Wait();
                return Measured;
            }), wait: TimeSpan.FromMilliseconds(200));
            var plan = Stopwatch.StartNew();
            foreach (var label in new[] { "a", "b", "c", "d", "e", "f", "g", "h" })
                Ask(waiting, [Ne(Text(LabelPath), label)]).Declined.Should().Be(RenderingOracle.Unavailable, "a question waits for the build no longer than it is told");
            plan.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "eight questions wait one wait, not eight");
            gate.Set();
            await waiting.Ready;
            (Ask(waiting, [NotBikes]).Of(NotBikes).Kind, waiting.Available, waiting.Counts).Should().Be((RenderingVerdictKind.Proven, true, (0L, 1L, 8L)));
        }

        /// <summary>
        /// A remembered answer is served while no generation answers — the current ran out and its successor is building —
        /// since generations defined alike hash alike; a question not remembered waits, and declines.
        /// </summary>
        [Fact]
        public async Task ARememberedAnswerIsServedWhileNoGenerationAnswers()
        {
            using var gate = new ManualResetEventSlim(true);
            var graph = new RenderingGraph(() =>
            {
                gate.Wait();
                return Measured;
            });
            var oracle = new RenderingOracle(graph, wait: TimeSpan.FromMilliseconds(50));
            await oracle.Ready;
            Ask(oracle, [NotBikes]).Declined.Should().BeNull();

            gate.Reset();
            graph.Current().RanOut = true;
            (Ask(oracle, [NotBikes]).Cost.Hits, Ask(oracle, [Ne(Text(LabelPath), "shoes")]).Declined).Should().Be((1, RenderingOracle.Unavailable));
            gate.Set();
            SpinWait.SpinUntil(() => graph.Generations == 2, TimeSpan.FromMinutes(1)).Should().BeTrue();
            Ask(oracle, [Ne(Text(LabelPath), "shoes")]).Of(Ne(Text(LabelPath), "shoes")).Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// What a remembered answer costs, its key built from a frozen <c>D0</c> included: every container the plan digests
        /// plan over that declares something, a comparison over each path it mentions, first asked, then asked again five
        /// times, by the oracle's own clock. Reported, in microseconds, beside the first generation's build; the lever (§6.1),
        /// spelling the import once a node, is taken above 20 µs at the 90th centile. Explicit: run it in Release with
        /// <c>-showLiveOutput</c> and record the machine's load beside.
        /// </summary>
        [Fact(Explicit = true)]
        public async Task AHitCostsMicroseconds()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var oracle = Oracle();
            var built = Stopwatch.GetTimestamp();
            await oracle.Ready;
            var build = Stopwatch.GetElapsedTime(built);
            var questions = new List<Func<RenderingOracle.RenderingBundleAnswer>>();
            var scopes = new List<CosmosStatementScope>();
            foreach (var container in CosmosDifferentialTests.Containers.Concat(PlannerSchema.All).Where(c => c.Facts.MentionedPaths.Count > 0))
            {
                var theory = container.Facts.In(generations);
                var conjuncts = theory.MentionedPaths.Take(16).Select(p => Ne(Text(p), "x")).ToArray();
                var scope = new CosmosOracle(theory, generations).Admit(new PredicateStatement(conjuncts));
                var (d0, rules) = scope.ContextFor(OracleLicence.RenderConjunct(0));
                var source = new ImportSource(scope.Statement.Digest, "declaration", 1);
                scopes.Add(scope);
                questions.AddRange(conjuncts.Select(c => (Func<RenderingOracle.RenderingBundleAnswer>)(() => oracle.Ask(RenderingSet.Production, [c], null, source, d0, rules))));
            }

            foreach (var question in questions)
                question().Declined.Should().BeNull();

            var micros = new List<double>();
            for (var round = 0; round < 5; round++)
                foreach (var question in questions)
                {
                    var answer = question();
                    answer.Cost.Hits.Should().Be(1);
                    micros.Add(answer.Cost.Elapsed.TotalMicroseconds);
                }

            micros.Sort();
            TestContext.Current.TestOutputHelper?.WriteLine($"{questions.Count} questions, {micros.Count} hits: p50 {RenderingCostTests.At(micros, 0.5):F1} µs, "
                + $"p90 {RenderingCostTests.At(micros, 0.9):F1} µs, max {micros[^1]:F1} µs; {oracle.Counts}; the first generation built in {build.TotalMilliseconds:F0} ms");
            GC.KeepAlive(scopes);
        }

    }

}
