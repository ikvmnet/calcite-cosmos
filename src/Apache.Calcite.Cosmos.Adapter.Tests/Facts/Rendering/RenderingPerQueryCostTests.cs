using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;
using FluentAssertions.Execution;

using org.apache.calcite.rel;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The per-query cost lines (spec §6.3, §6.4; lines 23–30), with S writing as the switch will, through the
    /// registry's S — the oracle of the reasoning the harnesses share (<see cref="TestReasoning.Shared"/>), or for a
    /// first plan one over a warm graph that remembers nothing: S's own time in a query's plan — every question its
    /// plan asked, from the lookup to the roots' answers, the wait for a generation among it — and the bundles it
    /// opened, by the query's shape, first and asked again; the merged-view hierarchy; the first plan in a process,
    /// paired with S off; and what a bundle interns. Explicit: run each fact in a process of its own, Release
    /// <c>net10.0</c>, with <c>-showLiveOutput</c>. The machine's load is sampled beside every reading, and no fact
    /// waits for quiet; S's thread time (<see cref="ThreadCpu"/>) is read beside its wall time, which the lines hold,
    /// so that a reading a host's contention stretched says so.
    /// </summary>
    public sealed class RenderingPerQueryCostTests
    {

        /// <summary>How many times a statement is planned again, the median kept.</summary>
        const int Again = 5;

        /// <summary>The variables the first plan's child reads: S on or off, and the file it writes its figures to.</summary>
        const string FirstPlanMode = "COSMOS_FIRST_PLAN", FirstPlanOut = "COSMOS_FIRST_PLAN_OUT";

        /// <summary>The statement each child plans first: two conditions, one a pin.</summary>
        const string FirstPlanSql = """SELECT * FROM products AS c WHERE c."$.category" = 'bikes' AND c."id" = 'x'""";

        /// <summary>
        /// Lines 23–28: each corpus statement, then the synthetic families, planned and implemented first through an oracle
        /// whose memory holds nothing, over the warm graph, then five times more, the median kept; by shape, read off what S
        /// was asked, and "no filter" off the plan, the count, median, 90th centile and greatest of S's time first and again,
        /// the plan's own time beside, the bundles, candidates and bytes; held to the pass lines, four conditions to five to
        /// seven's, since a first plan opens as many bundles for either.
        /// </summary>
        [Fact(Explicit = true)]
        public async Task EachShapeIsWithinItsLine()
        {
            using var load = new CpuLoad();
            Print(ThreadCpu.Describe());
            var graph = await Warmed();

            // A plan over a container of its own warms the process.
            var harness = new PlannerHarness();
            using (CosmosRenderingShadow.Arm(new RenderingShadowCounter(new RenderingOracle(graph), []), writes: true))
                CosmosPlanningTimeTests.Implement(harness.PlanToAsync("""SELECT c."id" FROM customers AS c WHERE c."id" = 'warm'"""));

            var queries = new List<Query>();
            foreach (var query in PlannerQueries.All)
                queries.Add(Measure(graph, query.Name, () => harness.PlanToAsync(query.Sql)));
            foreach (var (name, sql) in Synthetic())
                queries.Add(Measure(graph, name, () => Differential(sql)));

            Print($"{queries.Count} queries ({PlannerQueries.All.Count} of the corpus); {load}");
            foreach (var shape in queries.GroupBy(q => q.Shape).OrderBy(g => Array.IndexOf(Shapes, g.Key)))
            {
                var (first, again, plan, cpu) = (Sorted(shape.Select(q => q.First)), Sorted(shape.Select(q => q.Again)), Sorted(shape.Select(q => q.Plan)), Sorted(shape.Select(q => q.FirstThread)));
                Print($"{shape.Key}: {shape.Count()} queries; S first p50 {At(first, 0.5):F2} p90 {At(first, 0.9):F2} max {first[^1]:F1} ms, its thread time p50 {At(cpu, 0.5):F2} p90 {At(cpu, 0.9):F2} max {cpu[^1]:F1} ms; again p50 {At(again, 0.5):F3} p90 {At(again, 0.9):F3} max {again[^1]:F3} ms; "
                    + $"plan first p50 {At(plan, 0.5):F0} p90 {At(plan, 0.9):F0} ms; bundles first median {At(Sorted(shape.Select(q => (double)q.Bundles)), 0.5)} max {shape.Max(q => q.Bundles)}, again max {shape.Max(q => q.AgainBundles)}; "
                    + $"candidates {shape.Sum(q => q.Candidates)}; {shape.Sum(q => q.Bytes) / 1048576.0:F1} MB allocated in S; S {Share(shape.Sum(q => q.First), shape.Sum(q => q.Plan))} of the first plans' time");
            }

            foreach (var q in queries.Where(q => q.Failure is not null || q.Shape == "rare"))
                Print($"  {q.Name} ({q.Shape}): S first {q.First:F1} ms, {q.Bundles} bundles{(q.First > 1000 ? ", a warning: above 1 s" : "")}{(q.Failure is { } f ? ", " + f : "")}");
            Print($"{queries.Count(q => q.Shape == NotAsked)} queries filtered with S asked nothing; {load}");

            // Every line judged, so that one over its line does not hide another's reading.
            using var scope = new AssertionScope();
            queries.Where(q => q.Failure is not null).Select(q => $"{q.Name}: {q.Failure}").Should().BeEmpty("no plan fails on S (line 28)");
            queries.Where(q => q.Shape == "no filter").Select(q => $"{q.Name}: {q.Bundles}").Should().NotBeEmpty().And.OnlyContain(q => q.EndsWith(": 0", StringComparison.Ordinal), "line 27");
            queries.Where(q => q.AgainBundles > 0).Select(q => q.Name).Should().BeEmpty("line 26: again opens no bundle");
            Within(queries, "1-3 conditions", 30, 75);
            Within(queries, "4 conditions", 120, 160);
            Within(queries, "5-7 conditions", 120, 160);
            Within(queries, "a list of 5-19", 120, 160);
            var repeated = Sorted(queries.Where(q => q.Shape != "no filter").Select(q => q.Again));
            (At(repeated, 0.5), At(repeated, 0.9)).Should().Match<(double Median, double P90)>(a => a.Median <= 1 && a.P90 <= 3, $"line 26, again: {load}");
        }

        /// <summary>
        /// Line 30: the neutral merged-view hierarchy (<see cref="MergedViewHierarchy"/>), each statement planned first over
        /// containers built for it, S's own time ≤ 1.0 s; then five times over fresh containers and five over kept ones,
        /// each ≤ 10 ms, opening no bundle. Every reading is printed, S's share of the plan's time beside, before any is
        /// judged.
        /// </summary>
        [Fact(Explicit = true)]
        public async Task TheHierarchyIsWithinItsLine()
        {
            using var load = new CpuLoad();
            using var writing = CosmosRenderingShadow.Arm(null, writes: true);
            Print(ThreadCpu.Describe());
            TestReasoning.Shared.Rendering.Warm();
            await TestReasoning.Shared.Rendering.Ready;

            using var scope = new AssertionScope();
            foreach (var (name, sql, parameters) in MergedViewHierarchy.Statements)
            {
                Planned Plan(System.Data.Common.DbConnection connection)
                {
                    using var cost = CosmosRenderingCost.Measure(out var samples, ThreadCpu.Clock);
                    var (clock, collections) = (Stopwatch.StartNew(), GC.CollectionCount(0));
                    MergedViewHierarchy.Explain(connection, sql, parameters);
                    return new(samples.Sum(s => s.Cost.Elapsed.TotalMilliseconds), samples.Sum(s => s.Cost.Bundles), samples.Count, clock.Elapsed.TotalMilliseconds,
                        samples.Select(s => s.Cost.Elapsed.TotalMilliseconds).DefaultIfEmpty(0).Max(), GC.CollectionCount(0) - collections, ThreadTime(samples));
                }

                Planned first;
                using (var connection = MergedViewHierarchy.Open())
                    first = Plan(connection);
                Print($"{name}, first: S {first.S:F1} ms, its thread time {first.Thread:F1} ms, over {first.Questions} questions, {first.Bundles} bundles; the plan {first.Wall:F0} ms, S {Share(first.S, first.Wall)} of it; {load}");

                var fresh = Enumerable.Range(0, Again).Select(_ =>
                {
                    using var connection = MergedViewHierarchy.Open();
                    return Plan(connection);
                }).ToList();
                List<Planned> kept;
                using (var connection = MergedViewHierarchy.Open())
                    kept = [.. Enumerable.Range(0, Again + 1).Select(_ => Plan(connection)).Skip(1)];

                foreach (var (what, later) in new[] { ("fresh containers", fresh), ("kept containers", kept) })
                    Print($"{name}, later over {what}: S p50 {At(Sorted(later.Select(l => l.S)), 0.5):F2} max {later.Max(l => l.S):F2} ms, its thread time p50 {At(Sorted(later.Select(l => l.Thread)), 0.5):F2} max {later.Max(l => l.Thread):F2} ms, over {later.Max(l => l.Questions)} questions, "
                        + $"the slowest question {later.Max(l => l.Slowest):F2} ms, {later.Sum(l => l.Collections)} collections in {later.Count} plans; bundles {later.Sum(l => l.Bundles)}; "
                        + $"the plan p50 {At(Sorted(later.Select(l => l.Wall)), 0.5):F0} ms, S {Share(later.Sum(l => l.S), later.Sum(l => l.Wall))} of it; {load}");

                first.S.Should().BeLessThanOrEqualTo(1000, $"{name}'s first plan, line 30");
                foreach (var (what, later) in new[] { ("fresh containers", fresh), ("kept containers", kept) })
                    later.Should().OnlyContain(l => l.S <= 10 && l.Bundles == 0, $"{name} planned again over {what}, line 30");
            }
        }

        /// <summary>
        /// Line 29: ten fresh processes, alternately with S writing and without, each planning one corpus statement as its
        /// first plan — S's build started first, as the switch starts it at the first table — and the difference in wall
        /// time, paired, ≤ 0.5 s at the median, killed above 1.0 s. Each child's process CPU, and where S's first plan
        /// spends its time — the build, Warm to Ready, its parts, and the plan's own wait — are printed beside.
        /// </summary>
        [Fact(Explicit = true)]
        public void TheFirstPlanInAProcessIsWithinItsLine()
        {
            using var load = new CpuLoad();
            var runs = Enumerable.Range(0, 10).Select(i => Child(i % 2 == 0 ? "on" : "off")).ToList();
            var parts = Child("parts");
            foreach (var run in runs)
                Print(string.Join(", ", run.Select(f => $"{f.Key} {f.Value}")));
            Print("the build's parts, first in a fresh process: " + string.Join(", ", parts.Select(f => $"{f.Key} {f.Value}")));

            var (on, off) = (runs.Where(r => r["mode"] == "on").ToList(), runs.Where(r => r["mode"] == "off").ToList());
            var paired = on.Zip(off, (a, b) => double.Parse(a["wall ms"], CultureInfo.InvariantCulture) - double.Parse(b["wall ms"], CultureInfo.InvariantCulture)).Order().ToList();
            Print($"paired, S on less S off: {string.Join(", ", paired.Select(d => d.ToString("F0", CultureInfo.InvariantCulture)))} ms; median {At(paired, 0.5):F0} ms; {load}");
            At(paired, 0.5).Should().BeLessThanOrEqualTo(500, "line 29: the first plan in a process, paired");
        }

        /// <summary>
        /// The child <see cref="TheFirstPlanInAProcessIsWithinItsLine"/> runs, a fact a fresh process: with S on, writing
        /// armed and its build started; off, nothing armed; and in <c>parts</c>, the build's parts timed as the process's
        /// first act. Explicit, and skipped where its parent did not start it.
        /// </summary>
        [Fact(Explicit = true)]
        public async Task FirstPlanInThisProcess()
        {
            var path = Environment.GetEnvironmentVariable(FirstPlanOut);
            if (path is null)
                Assert.Skip("Run by TheFirstPlanInAProcessIsWithinItsLine.");

            var mode = Environment.GetEnvironmentVariable(FirstPlanMode);
            var figures = new Dictionary<string, string> { ["mode"] = mode ?? "" };
            static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
            var clock = Stopwatch.StartNew();
            if (mode == "parts")
            {
                var definition = CosmosRendering.Definition;
                figures["registry read ms"] = Ms(clock.Elapsed);
                var theory = RenderingTheory.Define(definition);
                figures["define and settle ms"] = Ms(clock.Elapsed);
                _ = RenderingAnswerMemo.DigestOf(theory);
                figures["digest ms"] = Ms(clock.Elapsed);
                var answer = new RenderingOracle(new RenderingGraph(() => definition), wait: System.Threading.Timeout.InfiniteTimeSpan).Ask(RenderingSet.Production,
                    [ProbeRows.NotBikes], null, ProbeRows.Source, null, null);
                figures["a second graph's build and one question ms"] = Ms(clock.Elapsed);
                figures["answer"] = answer.Of(ProbeRows.NotBikes).Kind.ToString();
            }
            else
            {
                if (mode == "on")
                {
                    CosmosRenderingShadow.ArmEverywhere(null);
                    TestReasoning.Shared.Rendering.Warm();
                }

                IReadOnlyList<RenderingCostSample> samples;
                using (CosmosRenderingCost.Measure(out samples))
                {
                    var harness = new PlannerHarness();
                    CosmosPlanningTimeTests.Implement(harness.PlanToAsync(FirstPlanSql));
                }

                figures["wall ms"] = Ms(clock.Elapsed);
                figures["process cpu ms"] = Ms(Process.GetCurrentProcess().TotalProcessorTime);
                if (mode == "on")
                {
                    figures["S ms in the plan"] = samples.Sum(s => s.Cost.Elapsed.TotalMilliseconds).ToString("F0", CultureInfo.InvariantCulture);
                    figures["S's first question ms (the wait)"] = samples.Count > 0 ? Ms(samples[0].Cost.Elapsed) : "none";
                    figures["declines"] = samples.Sum(s => s.Cost.Declines).ToString(CultureInfo.InvariantCulture);
                    await TestReasoning.Shared.Rendering.Ready;
                    figures["Warm to Ready ms"] = Ms(clock.Elapsed);
                }
            }

            File.WriteAllLines(path, figures.Select(f => $"{f.Key}\t{f.Value}"));
        }

        /// <summary>
        /// What a bundle interns and spends of its generation, over the corpus planned with S writing through an oracle that
        /// remembers nothing before each statement: the values interned and the share of ids or epochs used, before and after
        /// each bundle, and so how many bundles a generation answers before its successor is built (0.4, or 800,000 values)
        /// and before it retires (0.5, or 1,000,000).
        /// </summary>
        [Fact(Explicit = true)]
        public async Task InterningPerBundleIsCounted()
        {
            using var load = new CpuLoad();
            var graph = await Warmed();

            var harness = new PlannerHarness();
            var bundles = new List<RenderingCostSample>();
            foreach (var query in PlannerQueries.All)
            {
                using (CosmosRenderingShadow.Arm(new RenderingShadowCounter(new RenderingOracle(graph), []), writes: true))
                using (CosmosRenderingCost.Measure(out var samples))
                {
                    try
                    {
                        CosmosPlanningTimeTests.Implement(harness.PlanToAsync(query.Sql));
                    }
                    catch (Exception)
                    {
                        // A statement that does not plan is no bundle's.
                    }

                    bundles.AddRange(samples.Where(s => s.Cost.Bundles == 1 && s.Before is not null && s.After is not null));
                }
            }

            var interned = Sorted(bundles.Select(b => (double)(b.After!.Value.InternedValues - b.Before!.Value.InternedValues)));
            var used = Sorted(bundles.Select(b => b.After!.Value.FractionUsed - b.Before!.Value.FractionUsed));
            Print($"{bundles.Count} bundles: interned a bundle p50 {At(interned, 0.5):F0} p90 {At(interned, 0.9):F0} max {interned[^1]:F0}; "
                + $"ids or epochs a bundle p50 {At(used, 0.5):E2} p90 {At(used, 0.9):E2}; {load}");
            var (successor, retired) = (Math.Min(RenderingGraph.SuccessorInterned / Math.Max(1, At(interned, 0.5)), RenderingGraph.SuccessorUsed / Math.Max(1e-12, At(used, 0.5))),
                Math.Min(Cosmos.Facts.JsonFactGenerations.DefaultInternedLimit / Math.Max(1, At(interned, 0.5)), Cosmos.Facts.JsonFactGenerations.DefaultUsedLimit / Math.Max(1e-12, At(used, 0.5))));
            Print($"bundles a generation at the median: {successor:N0} before its successor is built, {retired:N0} before it retires");

            // What an answer remembered holds (§6.1): the corpus planned once more over containers of its own, its answers kept,
            // the heap compared; an upper bound, since the new containers' contexts grow beside them.
            var (oracle, again, before) = (new RenderingOracle(graph), new PlannerHarness(), GC.GetTotalMemory(forceFullCollection: true));
            using (CosmosRenderingShadow.Arm(new RenderingShadowCounter(oracle, []), writes: true))
                foreach (var query in PlannerQueries.All)
                    try
                    {
                        CosmosPlanningTimeTests.Implement(again.PlanToAsync(query.Sql));
                    }
                    catch (Exception)
                    {
                        // As above.
                    }

            var (entries, grown) = (oracle.Memo.Count, GC.GetTotalMemory(forceFullCollection: true) - before);
            Print($"{entries} answers remembered, the heap {grown / 1024.0:N0} KB larger: about {grown / 1024.0 / Math.Max(1, entries):N1} KB an answer, "
                + $"{grown / 1048576.0 / Math.Max(1, entries) * 4096:N1} MB at 4,096; {load}");
            bundles.Should().NotBeEmpty();
            interned[^1].Should().BePositive("a bundle interns what it writes");
        }

        /// <summary>A query measured: its shape, S's time and the plan's first, S's again (the median of five), bundles, candidates and bytes, why it failed, if it did, and S's thread time first.</summary>
        sealed record Query(string Name, string Shape, double First, double Plan, int Bundles, long Candidates, long Bytes, double Again, int AgainBundles, string? Failure, double FirstThread);

        /// <summary>One plan of the hierarchy: S's time, bundles and questions, the plan's wall time, the slowest question, collections and S's thread time.</summary>
        sealed record Planned(double S, int Bundles, int Questions, double Wall, double Slowest, int Collections, double Thread);

        /// <summary>A query whose plan holds a filter and asked S nothing: every conjunct the translator's, or none read.</summary>
        const string NotAsked = "a filter, S asked nothing";

        static readonly string[] Shapes = ["no filter", NotAsked, "1-3 conditions", "4 conditions", "5-7 conditions", "a list of 5-19", "rare", "failed"];

        /// <summary>The registry's S on a graph of its own, built and warmed: what each first plan's oracle, which remembers nothing, asks.</summary>
        static async Task<RenderingGraph> Warmed()
        {
            var graph = new RenderingGraph(() => CosmosRendering.Definition);
            var oracle = new RenderingOracle(graph);
            oracle.Warm();
            await oracle.Ready;
            return graph;
        }

        /// <summary>
        /// A statement planned and implemented first through an oracle over the warm graph that remembers nothing, then five
        /// times more through it, each in a flow measured, with S writing.
        /// </summary>
        static Query Measure(RenderingGraph graph, string name, Func<RelNode> plan)
        {
            using var writing = CosmosRenderingShadow.Arm(new RenderingShadowCounter(new RenderingOracle(graph), []), writes: true);
            (double S, double Wall, IReadOnlyList<RenderingCostSample> Samples, string? Failure, RelNode? Planned) Once()
            {
                using var cost = CosmosRenderingCost.Measure(out var samples, ThreadCpu.Clock);
                var clock = Stopwatch.StartNew();
                RelNode? planned = null;
                try
                {
                    CosmosPlanningTimeTests.Implement(planned = plan());
                }
                catch (Exception e)
                {
                    return (samples.Sum(s => s.Cost.Elapsed.TotalMilliseconds), clock.Elapsed.TotalMilliseconds, samples, PlanDigests.Failure(e), planned);
                }

                return (samples.Sum(s => s.Cost.Elapsed.TotalMilliseconds), clock.Elapsed.TotalMilliseconds, samples, null, planned);
            }

            var first = Once();
            var again = Enumerable.Range(0, Again).Select(_ => Once()).ToList();
            var shape = first.Planned is null ? "failed" : PlanDigests.Filters(first.Planned) == false ? "no filter" : first.Samples.Count == 0 ? NotAsked
                : RenderingGenerateRun.ShapeOf(first.Samples.SelectMany(s => s.Roots));
            return new(name, shape, first.S, first.Wall, first.Samples.Sum(s => s.Cost.Bundles), first.Samples.Sum(s => s.Candidates), first.Samples.Sum(s => s.Bytes),
                again.Select(a => a.S).Order().ElementAt(Again / 2), again.Max(a => a.Samples.Sum(s => s.Cost.Bundles)), first.Failure ?? again.Select(a => a.Failure).FirstOrDefault(f => f is not null),
                ThreadTime(first.Samples));
        }

        /// <summary>S's thread time over a plan's questions; a hit handed again, which reads no clock, at its wall time, a few microseconds.</summary>
        static double ThreadTime(IEnumerable<RenderingCostSample> samples) => samples.Sum(s => (s.Thread ?? s.Cost.Elapsed).TotalMilliseconds);

        /// <summary>
        /// The synthetic families (§6.4): over <c>products</c>, a membership of k ids alone and beside a category, and an
        /// <c>OR</c> of k equalities, k from 2 to 19; conjunctions of 5, 6 and 7 over as many paths and over one path, with and
        /// without a residual; over <c>declared</c>, conjunctions of 5 to 7 over its declared paths; and rare, reported: 8,
        /// 10, 12, 16 and 20 conditions, and lists of 20 and 32, each an <c>OR</c>, since Calcite plans an <c>IN</c> of 20 or
        /// more as a join.
        /// </summary>
        static IEnumerable<(string Name, string Sql)> Synthetic()
        {
            static string Ids(int k) => string.Join(", ", Enumerable.Range(0, k).Select(i => $"'a{i}'"));
            static string Over(int k, Func<int, string> each, string join = " AND ") => string.Join(join, Enumerable.Range(0, k).Select(each));
            const string From = "SELECT c.\"id\" FROM products AS c WHERE ", Residual = " AND SOUNDEX(JSON_VALUE(c.\"DOC\", '$.name')) = 'A120'";
            foreach (var k in new[] { 2, 3, 4, 5, 8, 12, 16, 19 })
            {
                yield return ($"IN {k}", From + $"c.\"id\" IN ({Ids(k)})");
                yield return ($"IN {k} beside a category", From + $"c.\"$.category\" = 'bikes' AND c.\"id\" IN ({Ids(k)})");
                yield return ($"OR of {k}", From + Over(k, i => $"c.\"id\" = 'a{i}'", " OR "));
            }

            foreach (var k in new[] { 5, 6, 7 })
            {
                yield return ($"AND of {k} over {k} paths", From + Over(k, i => $"JSON_VALUE(c.\"DOC\", '$.p{i}') = 'x{i}'"));
                yield return ($"AND of {k} over one path", From + Over(k, i => $"JSON_VALUE(c.\"DOC\", '$.label') <> 'x{i}'"));
                yield return ($"AND of {k} over {k} paths and a residual", From + Over(k - 1, i => $"JSON_VALUE(c.\"DOC\", '$.p{i}') = 'x{i}'") + Residual);
                yield return ($"AND of {k} over one path and a residual", From + Over(k - 1, i => $"JSON_VALUE(c.\"DOC\", '$.label') <> 'x{i}'") + Residual);
                yield return ($"AND of {k} over declared", "SELECT c.\"id\" FROM declared AS c WHERE " + Over(k, i => (i % 3) switch
                {
                    0 => $"JSON_VALUE(c.\"DOC\", '$.body') <> 'b{i}'",
                    1 => $"JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) > {i}",
                    _ => $"JSON_VALUE(c.\"DOC\", '$.v' RETURNING INTEGER) <> {i}",
                }));
            }

            foreach (var k in new[] { 8, 10, 12, 16, 20 })
                yield return ($"rare: {k} conditions", From + Over(k, i => $"JSON_VALUE(c.\"DOC\", '$.p{i % 6}') <> 'x{i}'"));
            // Calcite plans an IN of 20 or more as a join with its values, which no rule here pushes; an OR is the list.
            foreach (var k in new[] { 20, 32 })
                yield return ($"rare: a list of {k}", From + Over(k, i => $"c.\"id\" = 'a{i}'", " OR "));
        }

        /// <summary>A statement over the differential's containers, as the check run plans one.</summary>
        static RelNode Differential(string sql) => RenderingInputs.Differential("cost", [sql], [.. CosmosDifferentialTests.Containers]).Single().Plan();

        /// <summary>Runs <see cref="FirstPlanInThisProcess"/> in a process of its own, and reads back its figures.</summary>
        static Dictionary<string, string> Child(string mode)
        {
            var output = Path.Combine(Path.GetTempPath(), $"first-plan-{Guid.NewGuid():N}.tsv");
            var start = new ProcessStartInfo(Environment.ProcessPath!, $"-method {typeof(RenderingPerQueryCostTests).FullName}.{nameof(FirstPlanInThisProcess)} -explicit only")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment[FirstPlanMode] = mode;
            start.Environment[FirstPlanOut] = output;
            start.Environment.Remove(RenderingRehearsal.Variable);
            using var process = Process.Start(start)!;
            var log = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            File.Exists(output).Should().BeTrue($"the {mode} child wrote its figures: {log.Result}");
            var figures = File.ReadAllLines(output).Select(l => l.Split('\t')).ToDictionary(f => f[0], f => f[1]);
            File.Delete(output);
            return figures;
        }

        static void Within(List<Query> queries, string shape, double median, double p90)
        {
            var first = Sorted(queries.Where(q => q.Shape == shape).Select(q => q.First));
            if (first.Count == 0)
                return;

            (At(first, 0.5), At(first, 0.9)).Should().Match<(double Median, double P90)>(f => f.Median <= median && f.P90 <= p90, $"{shape}: ≤ {median} / ≤ {p90} ms");
        }

        static List<double> Sorted(IEnumerable<double> values) => [.. values.Order()];

        /// <summary>S's time as a share of the plans' wall time: what a host's contention, which stretches both, leaves alike.</summary>
        static string Share(double s, double wall) => wall > 0 ? (s / wall).ToString("P2", CultureInfo.InvariantCulture) : "-";

        static double At(List<double> sorted, double centile) => RenderingCostTests.At(sorted, centile);

        static void Print(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    }

}
