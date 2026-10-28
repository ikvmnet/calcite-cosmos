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
    /// what each check holds asserted, the proofs held to the registry, no refutation left to fail, and its coverage within
    /// the line.
    /// </summary>
    public sealed class RenderingCheckTests
    {

        static RenderingCheckRun Run => RenderingCheckRun.PerPullRequest.Value;

        /// <summary>
        /// Every input planned and implemented; every bundle the run opened, under the asserted set and the set beside it,
        /// completed within its count limits, still held one node per class, derived no contradiction and
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
            run.Memo.Entries.Where(e => e.AssertedRun != disciplined || e.BesideRun != disciplined || e.Failure is not null
                || new[] { e.Asserted, e.Beside }.Concat(e.Alone).Any(v => v?.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict))
                .Select(e => $"entry {e.Id}, {e.Key.Form}: {e.AssertedRun} {e.Asserted.Reason}; {e.BesideRun} {e.Beside.Reason}; {e.Failure?.Message}").Should().BeEmpty();
        }

        /// <summary>
        /// The coverage report (spec §6), under the asserted set, counted as <see cref="RenderingReports.Coverage"/> says: S's
        /// share and the translator's. Reported, not a line: since the switch the check reads S's forms, written under each
        /// container's own set, Azure's, and a form proven there alone reads Unproven under <c>Portable</c>, so the 5 % line,
        /// which spec §7 retires as a kill line, cannot tell those from an Unproven <c>WHERE</c> S's composite proved
        /// (<c>TODO.md</c>, <i>Line 12 reads a sent <c>WHERE</c> under <c>Portable</c></i>). The translator's share is empty
        /// on these inputs. Asserted only that the check reads what S wrote.
        /// </summary>
        [Fact]
        public void CoverageIsReported()
        {
            var (coverage, mine) = (RenderingReports.Coverage(Run), RenderingReports.Coverage(Run, owned: true));
            var (unproven, counted) = (coverage.Values.Sum(c => c.Unproven), coverage.Values.Sum(c => c.Counted));
            var (sUnproven, sCounted) = (mine.Values.Sum(c => c.Unproven), mine.Values.Sum(c => c.Counted));
            TestContext.Current.TestOutputHelper?.WriteLine($"the translator's: {unproven} unproven of {counted}; S's: {sUnproven} unproven of {sCounted} "
                + $"({100.0 * sUnproven / Math.Max(1, sCounted):N1} %): " + string.Join(", ", mine.Select(c => $"{c.Key} {c.Value.Unproven}/{c.Value.Counted}")));
            sCounted.Should().BePositive("the check reads what S wrote");
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
        /// Implementation never admits again (spec §5.2): every statement object the run's plans admitted, counted across
        /// planning and implementation, admitted once, and one holding a conjunct S owns in planning, since implementation
        /// finds admission's statement by its condition node and binding. One admitted afresh would ask S again, and a run
        /// that then stopped short would make <c>Translate</c> throw for a plan Volcano chose. The unnest's element filters,
        /// asked in planning under a placeholder alias, are admitted at implementation, and own nothing.
        /// </summary>
        [Fact]
        public void ImplementationNeverAdmitsAgain()
        {
            var (statements, unowned) = Run.Admissions;
            TestContext.Current.TestOutputHelper?.WriteLine($"{statements} statements admitted, {Run.Readmitted.Count} again; {unowned} first while implementing, owning nothing");
            statements.Should().BePositive();
            Run.Readmitted.Select(r => $"{r.Input} {r.Name}: {r.Why}").Should().BeEmpty();
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
            (entry.Asserted.Kind, entry.Beside.Kind, entry.Alone[0]!.Kind, entry.Alone[1]).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven, RenderingVerdictKind.Proven, (RenderingVerdict?)null));
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
            var theory = Theory(Outright(P("a"), Present)).In(generations);
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
        /// A later round's conjunct is read beside the facts it was rendered from. Where kind is "a" the label is a string,
        /// so the bare equality a second round writes for text <c>label = '30'</c> is exact: a cell of the label's atom the
        /// first round's conjunct rules out, the number 30 among them, is read as the policy reads a raise, which the
        /// service's answer there refines but for a failure. From the root alone it is Unproven. The plant: kind's own atom
        /// read beneath the stratum that assumes it, which proves a form that keeps every kind, and which the check run
        /// never states, since an atom is read beneath its own conjunct's stratum alone.
        /// </summary>
        [Fact]
        public void ALaterRoundIsReadBesideTheFactsItWasRenderedFrom()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = Theory(Outright(KindPath, Present), When(KindPath, "a", LabelPath, Type(JsonType.String))).In(generations);
            var (kind, label) = (Eq(Text(KindPath), "a"), Eq(Text(LabelPath), "30"));
            var scope = new CosmosOracle(theory, generations).Admit(new PredicateStatement(new[] { kind, label }),
                (i, facts) => i == 0 || facts.Knows(new Cosmos.Facts.JsonFact(LabelPath, Type(JsonType.String))));
            scope.StratumOf(1).Should().Be(2);

            var (root, stratum) = (RenderingCheckRun.RootOf([scope])!.Value, scope.ContextFor(OracleLicence.RenderConjunct(1)));
            var rendered = new ImportSource(Source.Statement, "render conjunct 1", 2);
            var (named, labelled) = (ServiceForm.CompareOf(CosmosServiceTest.Equal, SqlValue.Spell(KindPath), JsonValues.Of("a")), ServiceForm.CompareOf(CosmosServiceTest.Equal, Label, JsonValues.Of("30")));
            RenderingVerdictKind Checked(ServiceForm first, params SqlPredicate[] beneath) => new RenderingCheckRun().Check([kind, label], All(first, labelled), [first, labelled], Verdict.Equal,
                bundle => RenderingImporter.Import(bundle, Source, root.Context, root.Rules, theory, [KindPath, LabelPath], beneath.Select(a => (a, rendered, stratum.Context, stratum.Rules)))).Entry.Asserted.Kind;

            (Checked(named), Checked(named, label)).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Proven));
            Checked(ServiceForm.TestOf(CosmosServiceTest.IsDefined, SqlValue.Spell(KindPath)), label, kind).Should().Be(RenderingVerdictKind.Proven, "the plant is what reading an atom beneath its own conjunct's stratum alone refuses");
            GC.KeepAlive(scope);
        }

        /// <summary>
        /// A stratum is read beside a later round only where the facts graph reads every conjunct it assumes as Calcite does.
        /// Where kind is 5 the label is a string, and a second round writes text <c>label = '7'</c> bare; but the facts graph
        /// reads a typed <c>INTEGER</c> = 5 as kind holding the integer 5 alone, where a column reads 5.0000000000000001 as 5
        /// too, beside a label the declaration leaves free. Read beneath that stratum the bare form would be exact and
        /// wrong. S states no round beneath it (<c>StratumFor</c> falls back to the root), where the label is not proven,
        /// so S sends the kind alone and the label's presence beneath it, where the translator sent the bare label from
        /// that stratum: a regression O2 lists, its reason a stratum read at the root.
        /// </summary>
        [Fact]
        public void AStratumAConjunctReadCoarselyRestsOnIsNotRead()
        {
            var container = new Adapter.Metadata.CosmosContainerMetadata("t", ["/kind"])
                .WithFacts([Outright(KindPath, Present), When(KindPath, 5m, LabelPath, Type(JsonType.String))]).WithReasoning(new Adapter.Metadata.CosmosReasoning());
            var run = RenderingCheckRun.Over(RenderingInputs.Differential("stratum", [
                "SELECT c.\"id\" FROM t AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind' RETURNING INTEGER) = 5 AND JSON_VALUE(c.\"DOC\", '$.label') = '7'"], [container]));
            var checks = run.Conjuncts.Where(c => c.Site == 2 && c.Entry is not null).ToList();
            foreach (var c in checks)
                TestContext.Current.TestOutputHelper?.WriteLine($"{c.Text}: later {c.LaterRound}, {c.Entry!.Asserted.Kind}; {c.Import!.Digest}");

            RenderingFamily.ReadExactly(Cmp(Typed(KindPath, "INTEGER"), "=", SqlValue.Of(5m))).Should().BeFalse();
            checks.Select(c => c.Text).Should().BeEquivalentTo(["(c.kind = @p0)", "IS_DEFINED(c.label)"], "the kind is sent, and the label's weakening beneath it");
            checks.Should().OnlyContain(c => c.LaterRound == false && c.Import!.Digest.Contains(" beneath ") == false, "nothing is read beneath the coarse stratum");
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
