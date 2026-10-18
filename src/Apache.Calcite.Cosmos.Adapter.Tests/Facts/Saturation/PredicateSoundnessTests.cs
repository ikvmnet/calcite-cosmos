using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The predicate oracle never says anything false of a declared document: <see cref="PopulationD"/>, run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A CI-sized run by default, and a long one on request.</b> <see cref="Trials"/> is what every run of the suite
    /// pays; <see cref="LongTrials"/> is the explicit test, run with <c>-explicit only</c>, before a change to the rules,
    /// the atoms or the strata merges.
    /// </para>
    /// <para>
    /// A failure prints its seed, its declaration and its conjuncts, and <c>PopulationD.Run(seed)</c> replays it alone.
    /// </para>
    /// </remarks>
    public sealed class PredicateSoundnessTests
    {

        /// <summary>The trials every run of the suite pays for.</summary>
        public const int Trials = 200;

        /// <summary>The trials of the long run.</summary>
        public const int LongTrials = 5_000;

        /// <summary>The first trial's seed; trial <c>t</c> is seeded <c>Seed + t</c>.</summary>
        public const int Seed = 20261015;

        [Fact]
        public void NothingTheOracleSaysIsFalseOfADeclaredDocument() => Run(Trials);

        [Fact(Explicit = true)]
        public void NothingTheOracleSaysIsFalseOfADeclaredDocumentOverALongRun() => Run(LongTrials);

        static void Run(int trials)
        {
            var checker = new PopulationD();
            var failed = new List<PopulationReport>();
            var reports = new List<PopulationReport>(trials);
            var clock = Stopwatch.StartNew();

            for (var trial = 0; trial < trials; trial++)
            {
                var report = checker.Run(Seed + trial);
                reports.Add(report);
                if (report.Failures.Count > 0)
                    failed.Add(report);
            }

            var elapsed = clock.Elapsed;
            var times = reports.Select(r => r.Elapsed.TotalMilliseconds).OrderBy(t => t).ToArray();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{trials:N0} trials in {elapsed.TotalSeconds:N1} s (p50 {times[times.Length / 2]:N0} ms, p95 {times[times.Length * 95 / 100]:N0} ms a trial): "
                + $"{reports.Sum(r => r.Facts):N0} facts ({reports.Sum(r => r.Characters):N0} of characters), {reports.Sum(r => r.Verdicts):N0} verdicts ({reports.Sum(r => r.Decided):N0} deciding something), {reports.Sum(r => r.Rows):N0} rows, "
                + $"{reports.Sum(r => r.Atoms):N0} atoms and {reports.Sum(r => r.Nodes):N0} e-nodes checked on {trials * (PopulationD.DocumentsPerTrial + PopulationD.BandedPerTrial):N0} documents; "
                + $"{reports.Count(r => r.Strata > 1):N0} statements admitted in two strata or more, {reports.Count(r => r.Complete == false):N0} incomplete, "
                + $"{reports.Count(r => r.Declaration.IsUnconditional):N0} declaring nothing conditional");

            if (failed.Count > 0)
                Assert.Fail($"{failed.Count} of {trials} trials found something false ("
                    + string.Join(", ", failed.SelectMany(r => r.Failures).GroupBy(f => f.Check).Select(g => $"{g.Count()} by {g.Key}")) + "):\n\n"
                    + string.Join("\n\n", failed.Take(3).Select(r => r.Describe())));
        }

    }

}
