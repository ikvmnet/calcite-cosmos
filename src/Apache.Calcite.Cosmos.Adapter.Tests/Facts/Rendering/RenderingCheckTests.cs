using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The check run over every input (<see cref="RenderingCheckRun"/>): its discipline, its memo and its cross-checks
    /// asserted, the proofs held to the registry, and its coverage reported, here and in the reports. Coverage's line lands
    /// with the typed readings.
    /// </summary>
    public sealed class RenderingCheckTests
    {

        static RenderingCheckRun Run => RenderingCheckRun.PerPullRequest.Value;

        /// <summary>
        /// Every bundle the run opened, under the asserted set and under <c>Portable</c>, completed within its count limits,
        /// still held one node per class, and derived no contradiction: so no verdict is <c>Incomplete</c> or a
        /// <c>Conflict</c>. The reports are written, and what they hold is summarised.
        /// </summary>
        [Fact]
        public void EveryBundleCompletesWithNoMergeAndNoConflict()
        {
            var run = Run;
            Summarise(run, RenderingReports.Write(run));
            var disciplined = new RenderingRun(RunStopReason.Completed, true, false);

            run.Memo.Entries.Should().NotBeEmpty();
            run.Memo.Entries.Where(e => e.AssertedRun != disciplined || e.PortableRun != disciplined || Undisciplined(e.Asserted) || Undisciplined(e.Portable))
                .Select(e => $"entry {e.Id}, {e.Key.Form}: {e.AssertedRun} {e.Asserted.Reason}; {e.PortableRun} {e.Portable.Reason}").Should().BeEmpty();
        }

        /// <summary>
        /// The plant "a memo keyed without the claim": a weakening's <c>Implies</c> of the bare comparison is answered first
        /// and holds, and an exact push's <c>Equal</c> of the same IR, import and form asked after is refuted, unless the
        /// memo files both under one key, when it answers the second from the first.
        /// </summary>
        [Fact]
        public void TheMemoKeysTheClaim()
        {
            Asked(new RenderingMemo()).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Refuted));
            Asked(new RenderingMemo(key => key with { Claim = Verdict.Equal })).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Proven), "the plant is what the first assertion catches");

            static (RenderingVerdictKind, RenderingVerdictKind) Asked(RenderingMemo memo)
            {
                var run = new RenderingCheckRun(memo);
                RenderingVerdictKind Ask(Verdict claim) =>
                    run.Check([Ne(Text(LabelPath), "bikes")], Bare, [Bare], claim, bundle => RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath])).Asserted.Kind;

                return (Ask(Verdict.Implies), Ask(Verdict.Equal));
            }
        }

        /// <summary>
        /// The two cross-checks: each conjunct sent is the text it was admitted as, its values inlined; and each <c>WHERE</c>
        /// built holds the conjunction of the texts its statements sent, as <c>Translate</c> writes one, any text no site
        /// wrote counted as written outside the translator.
        /// </summary>
        [Fact]
        public void WhatWasAdmittedIsWhatWasSentAndTheWhereHoldsIt()
        {
            Run.Conjuncts.Where(c => c.Site == 2).Should().NotBeEmpty();
            Run.Mismatches.Should().BeEmpty();
        }

        /// <summary>
        /// No push rests on nothing: no proof holds only where Calcite raises on every row, and no render licence keeps no
        /// document (#217's shape). The allowance is empty.
        /// </summary>
        [Fact]
        public void NoPushRestsOnNothing()
        {
            var vacuous = Run.Conjuncts.Where(c => c.Entry is { } e && (e.Import.Contradicted || e.Asserted is { Kind: RenderingVerdictKind.Proven, Vacuous: true }
                || e.Alone.Any(v => v is { Kind: RenderingVerdictKind.Proven, Vacuous: true })));

            vacuous.Select(c => $"{c.Input} {c.Name}: {c.Text}").Distinct().Should().BeEmpty();
        }

        /// <summary>Every row a proof names is one the registry holds, so re-measuring a row finds every statement resting on it.</summary>
        [Fact]
        public void EveryProofNamesARegisteredRow()
        {
            var resting = RenderingReports.Resting(Run);
            resting.Should().NotBeEmpty();
            resting.Keys.Except(CosmosAxioms.All.Select(a => a.Id)).Should().BeEmpty();
        }

        /// <summary>Lists every statement resting on the row <c>COSMOS_RENDERING_ROW</c> names, for whoever re-measures it.</summary>
        [Fact(Explicit = true)]
        public void TheStatementsRestingOnARow()
        {
            if (Environment.GetEnvironmentVariable("COSMOS_RENDERING_ROW") is not { Length: > 0 } row)
            {
                Assert.Skip("COSMOS_RENDERING_ROW names no row.");
                return;
            }

            foreach (var statement in RenderingReports.Resting(Run).GetValueOrDefault(row) ?? [])
                TestContext.Current.TestOutputHelper?.WriteLine(statement);
        }

        static bool Undisciplined(RenderingVerdict verdict) => verdict.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict;

        /// <summary>The run's counts: verdicts per conjunct under each set, the skips, what was written outside, and coverage as c13 will hold it.</summary>
        static void Summarise(RenderingCheckRun run, string folder)
        {
            var output = TestContext.Current.TestOutputHelper;
            var rows = run.Conjuncts.ToList();
            output?.WriteLine($"reports in {folder}; {run.Memo.Entries.Count} checks, {rows.Count} conjuncts admitted, {run.Failures.Count} statements failed to plan");
            foreach (var (label, sent) in new[] { ("sent", true), ("admitted", false) })
            {
                var some = rows.Where(c => (c.Site == 2) == sent).ToList();
                var checkedRows = some.Where(c => c.Entry is not null).ToList();
                string Count(Func<RenderingCheckRun.Checked, RenderingVerdict> verdict) =>
                    string.Join(", ", checkedRows.GroupBy(c => verdict(c).Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));

                var unproven = checkedRows.Count(c => c.Entry!.Asserted.Kind == RenderingVerdictKind.Unproven);
                output?.WriteLine($"{label} {some.Count}: asserted {Count(c => c.Entry!.Asserted)}; Portable {Count(c => c.Entry!.Portable)}; " +
                    $"skipped {string.Join(", ", some.Where(c => c.Skip is not null).GroupBy(c => c.Skip).Select(g => $"{g.Key} {g.Count()}"))}; " +
                    $"coverage {unproven} unproven of {checkedRows.Count} ({(checkedRows.Count == 0 ? 0 : 100.0 * unproven / checkedRows.Count):F1} %)");
            }

            output?.WriteLine($"written outside the translator {run.Outside.Count}");
        }

    }

}
