using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
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
    /// The check run over every input (<see cref="RenderingCheckRun"/>): its discipline, its memo, its cross-checks and
    /// what each check holds asserted, the proofs held to the registry, no refutation left to fail, and its coverage
    /// reported, here and in the reports. Coverage's line lands with the typed readings.
    /// </summary>
    public sealed class RenderingCheckTests
    {

        static RenderingCheckRun Run => RenderingCheckRun.PerPullRequest.Value;

        /// <summary>
        /// Every input planned and implemented; every bundle the run opened, under the asserted set and under
        /// <c>Portable</c>, completed within its count limits, still held one node per class, derived no contradiction and
        /// declined on no engine failure: so no verdict is <c>Incomplete</c> or a <c>Conflict</c>, and none is an Unproven
        /// that S did not answer. The reports are written, and coverage is summarised.
        /// </summary>
        [Fact]
        public void EveryBundleCompletesWithNoMergeAndNoConflict()
        {
            var run = Run;
            var folder = RenderingReports.Write(run);
            var sent = run.Conjuncts.Where(c => c.Site == 2 && c.Entry is not null).ToList();
            var unproven = sent.Count(c => c.Entry!.Asserted.Kind == RenderingVerdictKind.Unproven);
            TestContext.Current.TestOutputHelper?.WriteLine($"reports in {folder}; {run.Memo.Entries.Count} checks; coverage {unproven} unproven of {sent.Count} sent conjuncts checked");

            var disciplined = new RenderingRun(RunStopReason.Completed, true, false);
            run.Failures.Should().BeEmpty();
            run.Memo.Entries.Should().NotBeEmpty();
            run.Memo.Entries.Where(e => e.AssertedRun != disciplined || e.PortableRun != disciplined || e.Failure is not null
                || new[] { e.Asserted, e.Portable }.Concat(e.Alone).Any(v => v?.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict))
                .Select(e => $"entry {e.Id}, {e.Key.Form}: {e.AssertedRun} {e.Asserted.Reason}; {e.PortableRun} {e.Portable.Reason}; {e.Failure?.Message}").Should().BeEmpty();
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
                    run.Check([Ne(Text(LabelPath), "bikes")], Bare, [Bare], claim, bundle => RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath])).Entry.Asserted.Kind;

                return (Ask(Verdict.Implies), Ask(Verdict.Equal));
            }
        }

        /// <summary>
        /// The two cross-checks: each conjunct sent is the text it was admitted as, its values inlined; and each <c>WHERE</c>
        /// built holds the conjunction of the texts its statements sent, as <c>Translate</c> writes one, reads back as
        /// itself, and any text in it no site wrote is counted as written outside the translator.
        /// </summary>
        [Fact]
        public void WhatWasAdmittedIsWhatWasSentAndTheWhereHoldsIt()
        {
            Run.Conjuncts.Where(c => c.Site == 2).Should().NotBeEmpty();
            Run.Mismatches.Should().BeEmpty();
        }

        /// <summary>
        /// What the service is asked together is checked together: two statements one <c>WHERE</c> holds are one check, and
        /// a check that holds a conjunct it cannot judge is never proven, so that neither the <c>AND</c> licence nor the
        /// range need escapes through a conjunct left out. A conjunct proven alone beside an opaque one is the plant.
        /// </summary>
        [Fact]
        public void WhatTheServiceIsAskedTogetherIsCheckedTogether()
        {
            var units = Run.Conjuncts.GroupBy(c => (c.Input, c.Name, c.Unit)).ToList();
            units.Should().Contain(u => u.Select(c => c.Statement).Distinct().Count() > 1);
            units.Where(u => u.Select(c => c.Entry).OfType<RenderingMemo.Entry>().Distinct().Count() > 1).Select(u => u.Key).Should().BeEmpty();
            units.Where(u => u.Any(c => c.Skip is not null) && u.Any(c => c.Entry?.Asserted.Kind == RenderingVerdictKind.Proven)).Select(u => u.Key).Should().BeEmpty();

            var (entry, _) = new RenderingCheckRun().Check([Ne(Text(LabelPath), "bikes"), new SqlPredicate.Opaque(-1)], All(Guarded, ServiceForm.OpaqueOf("x", ValueSet<string>.Empty)),
                [Guarded, null], Verdict.Equal, bundle => RenderingImporter.Import(bundle, Source, null, null, null, [LabelPath]));
            (entry.Asserted.Kind, entry.Portable.Kind, entry.Alone[0]!.Kind, entry.Alone[1]).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven, RenderingVerdictKind.Proven, (RenderingVerdict?)null));
        }

        /// <summary>
        /// A check is imported from its statements' root, which assumes none of their conjuncts. The node a residual
        /// conjunct would render from is the last stratum, which assumes the conjunct admitted before it: there a form
        /// written wrongly for that conjunct is proven, and from the root it is not.
        /// </summary>
        [Fact]
        public void ACheckIsImportedFromWhatAssumesNoConjunctItHolds()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = generations.Bind(Theory(Outright(P("a"), Present)));
            var (category, kind) = (Eq(Raw(CategoryPath), "bikes"), Eq(Raw(KindPath), "a"));
            var scope = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { category, kind }), (i, _) => i == 0);
            scope.StratumOf(1).Should().Be(0, "the second conjunct is residual");

            var members = new[] { ServiceForm.TestOf(CosmosServiceTest.IsDefined, SqlValue.Spell(CategoryPath)), ServiceForm.CompareOf(CosmosServiceTest.Equal, SqlValue.Spell(KindPath), JsonValues.Of("a")) };
            RenderingVerdictKind From((Scope? Context, PredicateRules? Rules) node) => new RenderingCheckRun().Check([category, kind], All(members), members, Verdict.Equal,
                bundle => RenderingImporter.Import(bundle, Source, node.Context, node.Rules, theory, [CategoryPath, KindPath])).Entry.Asserted.Kind;

            From(scope.ContextFor(OracleLicence.RenderConjunct(1))).Should().Be(RenderingVerdictKind.Proven);
            From(RenderingCheckRun.RootOf([scope])!.Value).Should().NotBe(RenderingVerdictKind.Proven);
            GC.KeepAlive(scope);
        }

        /// <summary>
        /// No push rests on nothing: no proof holds only where Calcite raises on every row, and no render licence keeps no
        /// document (#217's shape). The allowance is empty.
        /// </summary>
        [Fact]
        public void NoPushRestsOnNothing() =>
            Run.Conjuncts.Where(c => c.Entry is not null && RenderingReports.RestsOnNothing(c)).Select(c => $"{c.Input} {c.Name}: {c.Text}").Distinct().Should().BeEmpty();

        /// <summary>
        /// No refutation fails its triage (spec §5.3): each warns until the emulator reproduces it, and <c>KnownRefutations</c>
        /// is empty, so one over an open default fails here.
        /// </summary>
        [Fact]
        public void NoRefutationFails() =>
            RenderingReports.Refutations(Run).Where(r => r.Triage.StartsWith("fails", StringComparison.Ordinal))
                .Select(r => $"{r.Check.Input} {r.Check.Name}: {string.Join("; ", r.Refutation.Cells.AsEnumerable())}").Should().BeEmpty();

        /// <summary>Every row a proof names is one the registry holds, so re-measuring a row finds every check resting on it.</summary>
        [Fact]
        public void EveryProofNamesARegisteredRow()
        {
            var resting = RenderingReports.Resting(Run);
            resting.Should().NotBeEmpty();
            resting.Keys.Except(CosmosAxioms.All.Select(a => a.Id)).Should().BeEmpty();
        }

        /// <summary>Lists every check resting on the row <c>COSMOS_RENDERING_ROW</c> names, for whoever re-measures it.</summary>
        [Fact(Explicit = true)]
        public void TheStatementsRestingOnARow()
        {
            if (Environment.GetEnvironmentVariable("COSMOS_RENDERING_ROW") is not { Length: > 0 } row)
            {
                Assert.Skip("COSMOS_RENDERING_ROW names no row.");
                return;
            }

            foreach (var check in RenderingReports.Resting(Run).GetValueOrDefault(row) ?? [])
                TestContext.Current.TestOutputHelper?.WriteLine(check);
        }

    }

}
