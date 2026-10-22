using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The cost line (spec §6) and <c>D0</c>'s (§4.3), each timed, printed and held to its line. Explicit, since a clock is
    /// the machine's: run it in Release on net10.0 with <c>-showLiveOutput</c>, a fact a process, and record the machine's
    /// load beside the figures.
    /// </summary>
    public sealed class RenderingCostTests
    {

        /// <summary>
        /// Every bundle the pull request's check run opens, warm: the inputs checked once to warm the process and S, then
        /// again with a fresh memo, two statements at a time as CI plans them, each bundle timed from open to dispose under
        /// each set. The line: 1.5 ms at the median, 5 ms at the 90th centile.
        /// </summary>
        [Fact(Explicit = true)]
        public void EachBundleIsWithinTheLine()
        {
            var inputs = () => RenderingInputs.All(CosmosDifferentialTests.DefaultPartitionedPredicates, new JsonFactGenerations());
            GC.KeepAlive(RenderingCheckRun.Over(inputs()));

            var times = new ConcurrentQueue<(RenderingSet Set, double Ms)>();
            var started = Stopwatch.GetTimestamp();
            var run = RenderingCheckRun.Over(inputs(), (set, t) => times.Enqueue((set, t.TotalMilliseconds)));
            Print($"run {Stopwatch.GetElapsedTime(started).TotalSeconds:N1} s, planning included; {run.Memo.Entries.Count} entries");
            foreach (var group in times.GroupBy(t => t.Set.ToString()).Append(times.GroupBy(_ => "both").Single()))
                Print($"{group.Key}: {Centiles([.. group.Select(t => t.Ms)])}");

            var all = Sorted(times.Select(t => t.Ms));
            At(all, 0.5).Should().BeLessThanOrEqualTo(1.5);
            At(all, 0.9).Should().BeLessThanOrEqualTo(5);
        }

        /// <summary>
        /// Stage 2's projection: the corpus's distinct bundles, checked first in the process, each at that early cost and
        /// at 12 to 14 candidates an atom more, each at the marginal cost of one. The marginal is read off synthetic bundles
        /// claiming 3, 6 and 12 forms at each atom of a conjunction, warm, and scaled by what the corpus's bundles cost early
        /// against warm. The line: 0.25 s.
        /// </summary>
        [Fact(Explicit = true)]
        public void StageTwosProjectionIsWithinTheLine()
        {
            double Mean(out int entries)
            {
                var times = new ConcurrentQueue<double>();
                var run = RenderingCheckRun.Over(RenderingInputs.Corpus(new JsonFactGenerations()), (set, t) =>
                {
                    if (set == RenderingCheckRun.AssertedSet)
                        times.Enqueue(t.TotalMilliseconds);
                });
                entries = run.Memo.Entries.Count;
                Print($"corpus, {RenderingCheckRun.AssertedSet}: {Centiles([.. times])}");
                return times.Average();
            }

            var (early, warm) = (Mean(out var bundles), Mean(out _));
            var each = new[] { 3, 6, 12 }.ToDictionary(k => k, Synthetic);
            var marginal = (each[12] - each[3]) / 9 * early / warm;
            var (low, high) = (bundles * (early + 12 * marginal) / 1000, bundles * (early + 14 * marginal) / 1000);
            Print($"corpus: {bundles} distinct bundles, {early:N3} ms each early, {warm:N3} ms warm; synthetic, warm: {string.Join(", ", each.Select(e => $"k={e.Key} {e.Value:N3} ms"))}");
            Print($"marginal {marginal:N4} ms a candidate at the early cost; projected {low:N3} to {high:N3} s");
            high.Should().BeLessThanOrEqualTo(0.25);
        }

        /// <summary>A bundle claiming k forms at each of two atoms, all evaluated on their cells: its median over 64 runs, the first 16 dropped.</summary>
        static double Synthetic(int k)
        {
            var (label, kind) = (Ne(Text(LabelPath), "bikes"), Eq(Text(KindPath), "a"));
            ServiceForm[] Forms(ServiceForm compare) =>
            [
                .. new[] { CosmosServiceTest.IsPrimitive, CosmosServiceTest.IsString, CosmosServiceTest.IsDefined, CosmosServiceTest.IsNull, CosmosServiceTest.IsBool, CosmosServiceTest.IsNumber }
                    .Select(test => ServiceForm.TestOf(test, compare.Path!)).SelectMany(test => new[] { All(test, compare), All(compare, test) }).Take(k),
            ];

            var (labelled, kinded) = (Forms(Bare), Forms(ServiceForm.CompareOf(CosmosServiceTest.Equal, SqlValue.Spell(KindPath), JsonValues.Of("a"))));
            var times = new List<double>();
            for (var i = 0; i < 64; i++)
            {
                var started = Stopwatch.GetTimestamp();
                using (var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet))
                {
                    var (root, x, y) = (bundle.Write(And(label, kind)), bundle.Write(label), bundle.Write(kind));
                    bundle.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
                    bundle.Import(Source, KindPath, PathValues.Anything, JsonValues.AllBands);
                    foreach (var (atom, forms) in new[] { (x, labelled), (y, kinded) })
                        foreach (var form in forms)
                            bundle.Claim(atom, form);

                    bundle.Run(64 + 8 * k).StopReason.Should().Be(Eggnog.RunStopReason.Completed);
                    GC.KeepAlive(labelled.Select(f => bundle.Read(x, f, Verdict.Equal)).Concat(kinded.Select(f => bundle.Read(y, f, Verdict.Equal))).ToList());
                    GC.KeepAlive(root);
                }

                if (i >= 16)
                    times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return At(Sorted(times), 0.5);
        }

        /// <summary>
        /// <c>D0</c> built and run, per declaration and generation (§4.3): every container the inputs plan over, in
        /// generations of their own a round, the first dropped; a container sharing a declaration reads its <c>D0</c> built.
        /// The declaration's compile and its generation's predicate rules, which the first statement planned builds anyway,
        /// are timed apart. The line is read at each declaration's median, 5 ms: its tail is a slow build in a process that
        /// ran every round, most likely a collection, and is printed beside it.
        /// </summary>
        [Fact(Explicit = true)]
        public void TheDeclarationsPossibleRowsAreWithinTheLine()
        {
            var containers = CosmosDifferentialTests.Containers.Concat(PlannerSchema.All).ToList();
            var built = new List<(string Name, int Paths, double Compile, double Rules, double Ms)>();
            var kept = new List<object?>();
            for (var round = 0; round < 21; round++)
            {
                var (generations, seen) = (new JsonFactGenerations(), new HashSet<object>(ReferenceEqualityComparer.Instance));
                foreach (var container in containers)
                {
                    var theory = generations.Bind(container.Facts);
                    var started = Stopwatch.GetTimestamp();
                    var declaration = theory.Declaration();
                    var compile = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (declaration is null || seen.Add(declaration.Context) == false)
                        continue;

                    started = Stopwatch.GetTimestamp();
                    kept.Add(PredicateRules.Of(declaration.Generation.Contexts));
                    var rules = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    started = Stopwatch.GetTimestamp();
                    kept.Add(declaration.DeclaredPossible());
                    if (round > 0)
                        built.Add((container.Name, theory.MentionedPaths.Count, compile, rules, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                }

                kept.Add(generations);
            }

            foreach (var declaration in built.GroupBy(b => (b.Name, b.Paths)))
                Print($"{declaration.Key.Name}, {declaration.Key.Paths} paths: D0 {Centiles([.. declaration.Select(b => b.Ms)])}; compiled p50 {At(Sorted(declaration.Select(b => b.Compile)), 0.5):N2} ms, rules p50 {At(Sorted(declaration.Select(b => b.Rules)), 0.5):N2} ms");

            built.GroupBy(b => b.Name).Where(d => At(Sorted(d.Select(b => b.Ms)), 0.5) > 5).Select(d => d.Key).Should().BeEmpty();
            GC.KeepAlive(kept);
        }

        /// <summary>
        /// The corpus planned new, armed for <c>D0</c> and unarmed (§4.3): each statement planned and implemented in two
        /// harnesses of fresh generations a round, the arm planned first alternating, so that a statement's two arms are
        /// timed a moment apart; the paired difference of the round's totals. The line: 0.06 s at the median of the rounds,
        /// the first dropped. Arming adds one <c>D0</c> a declaration and generation, and the corpus plans over one.
        /// </summary>
        [Fact(Explicit = true)]
        public void ArmingAddsWithinTheLineToTheCorpusPlannedNew()
        {
            var differences = new List<double>();
            for (var round = 0; round < 11; round++)
            {
                var (armed, unarmed) = (new PlannerHarness(new JsonFactGenerations()), new PlannerHarness(new JsonFactGenerations()));
                var totals = new double[2];
                foreach (var (query, i) in PlannerQueries.All.Select((q, i) => (q, i)))
                    foreach (var arm in (round + i) % 2 == 0 ? new[] { 0, 1 } : [1, 0])
                        totals[arm] += Planned(arm == 0 ? armed : unarmed, query.Sql, arm == 0);

                Print($"round {round}: armed {totals[0] / 1000:N3} s, unarmed {totals[1] / 1000:N3} s");
                if (round > 0)
                    differences.Add((totals[0] - totals[1]) / 1000);
            }

            var sorted = Sorted(differences);
            Print($"armed - unarmed, paired: median {At(sorted, 0.5):N3} s, quartiles {At(sorted, 0.25):N3} to {At(sorted, 0.75):N3} s, range {sorted[0]:N3} to {sorted[^1]:N3} s");
            At(sorted, 0.5).Should().BeLessThanOrEqualTo(0.06);
        }

        /// <summary>Plans a corpus statement and implements its Cosmos part, as <c>CosmosPlanningTimeTests</c> does; a statement that fails fails in both arms.</summary>
        static double Planned(PlannerHarness harness, string sql, bool armed)
        {
            using var arming = armed ? StatementTerms.ArmDeclaredPossible() : null;
            var started = Stopwatch.GetTimestamp();
            try
            {
                CosmosPlanningTimeTests.Implement(harness.PlanToAsync(sql));
            }
            catch (Exception)
            {
            }

            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        static List<double> Sorted(IEnumerable<double> values) => [.. values.Order()];

        /// <summary>The nearest-rank centile.</summary>
        static double At(List<double> sorted, double q) => sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Count) - 1)];

        static string Centiles(List<double> values)
        {
            var sorted = Sorted(values);
            return $"{sorted.Count} timed, p50 {At(sorted, 0.5):N3}, p90 {At(sorted, 0.9):N3}, p99 {At(sorted, 0.99):N3}, max {sorted[^1]:N3} ms, total {sorted.Sum() / 1000:N2} s";
        }

        static void Print(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    }

}
