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

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The five gate faults (spec §5.4), each in a graph of its own through <see cref="RenderingDefinition"/>: a row that
    /// contradicts another, a template that drops the null guard, a service evaluation mutated beneath it, a candidate
    /// contaminated across two disjuncts of one path, and a union. Every one is caught.
    /// </summary>
    public sealed class RenderingGateFaultTests
    {

        internal const string NotBikes = "SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') <> 'bikes'";

        /// <summary>The gains preview's inequality over typed with its null guard dropped.</summary>
        internal const string Unguarded = "IS_PRIMITIVE(c.label) AND c.label != @p0";

        /// <summary>
        /// G1: a second row for one key and target that answers otherwise is refused at load, on either target, naming its
        /// axiom, without faulting the theory; a definition from the right rows answers as before.
        /// </summary>
        [Fact]
        public void G1_AContradictingRowIsRefusedAtLoad()
        {
            var key = MeasuredKey.Of(CosmosServiceTest.NotEqual, CosmosValueClass.String, CosmosValueClass.Null);
            foreach (var target in new[] { RenderingTarget.Azure, RenderingTarget.Emulator })
            {
                var refusal = FluentActions.Invoking(() => RenderingTheory.Define(RenderingDefinition.Measured(
                    [.. Rows, new("Service.Planted", key, target, CosmosAnswer.False)]))).Should().Throw<FactConflictException>().Which;
                (refusal.Table.Name, refusal.Data["axiom"], refusal.Data["faulted"]).Should().Be(("outcome", "Service.Planted", false));
            }

            ProbeTheory.Theory.Get(ProbeTheory.Final.Of(key, RenderingSet.Portable)).Should().Equal(ValueSet.Of(CosmosAnswer.True));
        }

        /// <summary>
        /// G2: a template that drops the null guard. Every form the pull request's run proved with a positive
        /// <c>NOT IS_NULL</c>, the guard dropped, is proven only where source A's documents show the guard kept nothing
        /// out, as beside an <c>IS_NUMBER</c>; and the gains preview's inequality without it is refuted at the null, which
        /// typed's document 6 holds.
        /// </summary>
        [Fact]
        public void G2_ATemplateThatDropsTheNullGuardIsRefuted()
        {
            var rechecked = RenderingCheckRun.PerPullRequest.Value.Memo.Entries.Where(e => e.Asserted.Kind == RenderingVerdictKind.Proven && e.What is { } w && Unguard(w.Form) != w.Form)
                .Select(e => (Claim: e.What!, Dropped: Unguard(e.What!.Form))).Select(x => (x.Dropped,
                    Verdict: new RenderingCheckRun().Recheck(x.Claim, x.Dropped, [.. x.Claim.Members.Select(m => m is null ? null : Unguard(m))]).Asserted.Kind,
                    Matters: RenderingDocumentSampler.Sample(x.Claim).Any(d => RenderingCheckRun.Targets.Any(t => CosmosEval.On(x.Dropped, d, t) != CosmosEval.On(x.Claim.Form, d, t)))))
                .ToList();
            rechecked.Where(x => x.Matters).Should().NotBeEmpty();
            rechecked.Where(x => x.Matters && x.Verdict == RenderingVerdictKind.Proven).Select(x => x.Dropped.Text).Should().BeEmpty();

            RenderingMutationTests.Show(RenderingRecallTests.Replay(NotBikes, [Unguarded], [RenderingRecallTests.Bikes], M8 | M9).Asserted).Should().Be("$[\"label\"]=null (U,t)");
        }

        /// <summary>
        /// G3: a service evaluation mutated to answer <c>!=</c> over a null as false. Beneath it the gate proves G2's
        /// guard-dropped inequality, a false agreement; source A finds the document with a null label, where
        /// <see cref="CosmosEval"/> says the service keeps it and Calcite does not.
        /// </summary>
        [Fact]
        public void G3_AMutatedServiceEvaluationIsFoundBySourceA()
        {
            var planted = new RenderingCheckRun(graph: new RenderingGraph(() => Measured with
            {
                Leaf = (form, cells, table, parted) => Measured.Leaf(form, cells, table, parted) is var vector && form.Test == CosmosServiceTest.NotEqual
                    ? vector with { Outcomes = [.. vector.Outcomes.Zip(cells.Cells, (o, cell) => cell is PathCell.Null ? o with { Outcomes = ServiceOutcomes.False } : o)] }
                    : vector,
            }));

            var entry = RenderingRecallTests.Replay(NotBikes, [Unguarded], [RenderingRecallTests.Bikes], M8 | M9, planted);
            entry.Asserted.Kind.Should().Be(RenderingVerdictKind.Proven, "the mutated leaf makes the gate agree");

            var tally = new RenderingSoundnessTests.Tally();
            RenderingSoundnessTests.SourceA(tally, entry, "G3");
            tally.Witnesses.Should().Contain(w => w.Contains("on {\"label\":null}: Calcite Unknown", StringComparison.Ordinal));
        }

        /// <summary>
        /// G4 (critic-check §3.2): the second disjunct's form claimed for the first, inside a root candidate headed by
        /// <c>OR</c>. Nothing agrees at the root or at the first disjunct, and under <c>DOUBLE</c> the first is refuted
        /// where the service drops a number Calcite keeps; the second disjunct's form agrees on its own row, the two
        /// disjuncts' rows over the shared <c>IS_DEFINED</c> staying apart; the root is Unproven, since a refutation of one
        /// path does not compose through a junction of that path.
        /// </summary>
        [Theory]
        [InlineData("INTEGER", null)]
        [InlineData("DOUBLE", "$[\"price\"]=fractions > 5 double >= 5.000000000000001 (T,f)")]
        public void G4_AContaminatedCandidateIsNotProven(string type, string? refuted)
        {
            var price = P("price");
            var (first, second) = (Cmp(Cast(type, Text(price)), ">", Lit(5)), Eq(Text(price), "x"));
            var spelled = SqlValue.Spell(price);
            var contaminated = All(ServiceForm.TestOf(CosmosServiceTest.IsDefined, spelled), ServiceForm.CompareOf(CosmosServiceTest.Equal, spelled, JsonValues.Of("x")));
            var root = Any(contaminated, ServiceForm.CompareOf(CosmosServiceTest.Equal, spelled, JsonValues.Of("x")));

            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var (a, b, e) = (bundle.Write(first), bundle.Write(second), bundle.Write(Or(first, second)));
            bundle.Import(Source, price, PathValues.Anything, JsonValues.AllBands);
            bundle.Claim(e, root);
            bundle.Run().Should().Be(new RenderingRun(RunStopReason.Completed, true, false));

            bundle.Read(e, root, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Unproven);
            var atFirst = bundle.Read(a, contaminated, Verdict.Equal);
            (atFirst.Kind == RenderingVerdictKind.Refuted ? RenderingMutationTests.Show(atFirst) : null).Should().Be(refuted);
            atFirst.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            bundle.Read(b, root.Members[1], Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Proven);
            bundle.Scope.Rows(ProbeTheory.Svc).Where(r => r.Keys[1].Equals(contaminated.Members[0])).Select(r => r.Keys[0]).Distinct().Should().HaveCount(2);
        }

        /// <summary>
        /// G5: a union. A test-only rule whose head merges two atoms is refused by the order test, and trips the wire in a
        /// bundle (T7); so does a union the bundle's scope is asked to make itself.
        /// </summary>
        [Fact]
        public void G5_AUnionIsRefusedAndTripsTheWire()
        {
            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "gate").DefineRule("planted: two atoms of one path are one", r =>
            {
                var (e1, e2, p) = r.Vars<Pred, Pred, JPath>("e1", "e2", "p");
                var (a1, a2) = r.Vars<PredicateAtom, PredicateAtom>("a1", "a2");
                r.When(theory.Ir.Atom.Of(e1, p, a1), theory.Ir.Atom.Of(e2, p, a2), e1.IsNot(e2));
                r.Then(e1.Is(e2));
            });
            RenderingOrderTests.Violations(theory).Should().Contain("gate/planted: two atoms of one path are one: a head merges classes");

            foreach (var (tripped, union) in new[] { (theory, false), (ProbeTheory, true) })
            {
                using var bundle = tripped.Open(RenderingCheckRun.AssertedSet);
                var claimed = bundle.Write(Ne(Text(LabelPath), "bikes"));
                var (x, y) = (bundle.Write(Eq(Text(KindPath), "a")), bundle.Write(Eq(Text(KindPath), "b")));
                if (union)
                    bundle.Scope.Union(x, y);

                bundle.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
                bundle.Claim(claimed, Bare);
                bundle.Run().OneNodePerClass.Should().BeFalse();
                bundle.Read(claimed, Bare, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Conflict);
            }
        }

        /// <summary>A form with every <c>NOT IS_NULL</c> member of a junction kept where it stands removed, under an even number of <c>NOT</c>s.</summary>
        static ServiceForm Unguard(ServiceForm form, bool positive = true) => form.Kind switch
        {
            ServiceFormKind.Not => ServiceForm.NotOf(Unguard(form.Members[0], !positive)),
            ServiceFormKind.And or ServiceFormKind.Or => form.Members.Where(m => (positive && form.Kind == ServiceFormKind.And && IsGuard(m)) == false).Select(m => Unguard(m, positive)).ToList() switch
            {
                [var one] => one,
                var rest => ServiceForm.JunctionOf(form.Test, rest),
            },
            _ => form,
        };

        static bool IsGuard(ServiceForm form) => form is { Kind: ServiceFormKind.Not, Members: [{ Kind: ServiceFormKind.Test, Test: CosmosServiceTest.IsNull }] };

    }

}
