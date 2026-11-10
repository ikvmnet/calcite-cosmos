using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
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
    /// The mutation line's Stage 3 plants over <c>CASE</c>, the guarded spelling and an element's index (spec h13; line 16).
    /// A <c>CASE</c> reaches production's S only from the switch (h15), so the trusted plants are asked of a bundle of their
    /// own over the IR the loader reads, and source A judges each root's agreements on documents its import admits: M17 a
    /// condition read at the case's own position, M31 the opposite else without two-valuedness, M32 an unknown else read as
    /// the opposite constant at <c>Neg</c>, M33 the narrow else read without two-valuedness. M22, an untrusted template, is
    /// planned through production's sites over the switch's templates and refuted by the gate; M21, in the reading an
    /// element path comes from, is found by holding that reading to Calcite in process, which is what v1 does of values.
    /// </summary>
    public sealed partial class RenderingGenerateMutationTests
    {

        static readonly JsonDocumentPath XPath = P("x"), NPath = P("n"), NamePath = P("name");

        static readonly SqlPredicate TrueArm = new SqlPredicate.Constant(true), FalseArm = new SqlPredicate.Constant(false), UnknownArm = new SqlPredicate.Constant(null);

        /// <summary>UNKNOWN where the label is null or absent, and no form exact at <c>Pos</c> is exact at <c>Neg</c>.</summary>
        static readonly SqlPredicate LabelIsBikes = Eq(Text(LabelPath), "bikes");

        /// <summary>A type test: TRUE or FALSE on every document.</summary>
        static readonly SqlPredicate XIsNumber = new SqlPredicate.TypeTest("IS_NUMBER", Raw(XPath));

        static readonly SqlPredicate KindIsA = Eq(Text(KindPath), "a");

        /// <summary><c>c.x &gt; 5</c> read as a double: over numbers, null or absent the bare form is exact at both positions, yet UNKNOWN on null.</summary>
        static readonly SqlPredicate XAbove5 = Cmp(Typed(XPath, "DOUBLE"), ">", Lit(5m));

        static SqlPredicate Case(SqlPredicate condition, SqlPredicate then, SqlPredicate otherwise) => new SqlPredicate.Case(condition, then, otherwise);

        /// <summary>P7's conjunct as the loader reads it: <c>NOT (CASE WHEN category &lt;&gt; 'bikes' THEN id = '1' ELSE id = '6' END)</c>.</summary>
        static readonly SqlPredicate P7 = Not(Case(Ne(Text(CategoryPath), "bikes"), Eq(Text(P("id")), "1"), Eq(Text(P("id")), "6")));

        static J.Obj Doc(params (JsonDocumentPath Path, J? Value)[] values) =>
            RenderingDocumentSampler.Document([.. values.Select(v => SqlValue.Spell(v.Path))], [.. values.Select(v => v.Value)]);

        /// <summary>
        /// A <c>CASE</c> plant and its trigger: the roots asked under a set, each path's import, what the run states beneath a
        /// stratum, which documents the statement can hold, the documents source A must judge beside those drawn, and the
        /// document the witness is found on.
        /// </summary>
        internal sealed record CasePlant(string Name, Action<RenderingTheory> Rules, SqlPredicate Root, RenderingSet Set, IReadOnlyDictionary<JsonDocumentPath, PathValues>? Values,
            Action<RenderingBundle>? Beneath, Func<J.Obj, bool>? Holds, J.Obj Witness);

        /// <summary>What a bundle of a theory's own said of a root: its run, its answer, each agreement of the root and of a negation's operand at either position, and source A's tally over them.</summary>
        internal sealed record CaseJudged(RenderingRun Run, RenderingAnswer Answer, IReadOnlyList<(SqlPredicate Node, RenderPosition Position, ServiceForm Form)> Agreements,
            RenderingSoundnessTests.Tally Tally);

        internal static readonly IReadOnlyDictionary<string, CasePlant> CasePlants = new Dictionary<string, CasePlant>
        {
            // Beneath a NOT the case stands at Neg, the absorbing unknown else there NOT c OR a; c's Neg form is true on a
            // null label, so the planted form is false there where kind is not "a", and Calcite's case is UNKNOWN.
            ["M17 absorbing"] = new("M17", ConditionAtTheCasesPosition, Not(Case(LabelIsBikes, KindIsA, UnknownArm)), RenderingSet.Production, null, null, null,
                Doc((LabelPath, new J.Null()), (KindPath, new J.Str("b")))),
            // P7, the translator's _negated leak (soundness cause C): the ternary's condition read at Neg keeps document 6.
            ["M17 ternary"] = new("M17", ConditionAtTheCasesPosition, P7, RenderingSet.Emulator,
                new Dictionary<JsonDocumentPath, PathValues> { [P("id")] = PathValues.OfKinds(ValueSet.Of(JsonValues.StringType)) }, null, null,
                Doc((CategoryPath, new J.Null()), (P("id"), new J.Str("6")))),
            // The soundness critic's counterexample: NOT ((c.x > 5) IS TRUE) keeps a null x, which NOT (c.x > 5) does not.
            ["M31"] = new("M31", OppositeWithoutTwoValued, Not(Case(XAbove5, TrueArm, FalseArm)), RenderingSet.Production,
                new Dictionary<JsonDocumentPath, PathValues> { [XPath] = PathValues.OfTypes(ValueSet.Of(JsonValues.IntegerType, JsonValues.FractionType, JsonValues.NullType)) }, null, null,
                Doc((XPath, new J.Null()))),
            // c AND a at Neg is false where a two-valued c is FALSE, where Calcite's case is UNKNOWN.
            ["M32"] = new("M32", UnknownAsTheOppositeAtNeg, Not(Case(XIsNumber, KindIsA, UnknownArm)), RenderingSet.Production, null, null, null,
                Doc((XPath, new J.Str("s")), (KindPath, new J.Str("a")))),
            // The narrowing stated beneath [NOT c, kind = 'a'] read over a nullable c: where the label is null Calcite takes
            // the else, whose name may be null, which the bare inequality keeps.
            ["M33"] = new("M33", NarrowWithoutTwoValued, Case(LabelIsBikes, Eq(Text(NPath), "x"), Case(KindIsA, Ne(Text(NamePath), "bikes"), FalseArm)), RenderingSet.Emulator, null,
                StateNameAString, NameIsAStringWhereTheNarrowStratumHolds, Doc((LabelPath, new J.Null()), (KindPath, new J.Str("a")), (NamePath, new J.Null()))),
        };

        public static TheoryData<string> CaseFaults => [.. CasePlants.Keys];

        /// <summary>
        /// Each <c>CASE</c> plant is caught: unplanted, nothing S agrees of the trigger has a witness; planted, S agrees a form
        /// it did not, and source A finds the witness document, where the form and Calcite part.
        /// </summary>
        [Theory]
        [MemberData(nameof(CaseFaults))]
        public void ACasePlantIsCaught(string name)
        {
            var plant = CasePlants[name];
            var tip = Judge(ProbeTheory, plant);
            var theory = RenderingTheory.Define(Measured);
            plant.Rules(theory);
            var planted = Judge(theory, plant);
            var output = TestContext.Current.TestOutputHelper;
            output?.WriteLine($"{name} over {plant.Root} under {plant.Set}: tip {tip.Answer.Kind} {tip.Answer.Best?.Text}; planted {planted.Answer.Kind} {planted.Answer.Best?.Text}");
            output?.WriteLine($"  tip: {tip.Tally}; planted: {planted.Tally}");
            foreach (var w in planted.Tally.Witnesses.Take(4))
                output?.WriteLine("  witness: " + w);

            (tip.Run, planted.Run).Should().Be((Disciplined, Disciplined));
            tip.Tally.Witnesses.Should().BeEmpty("unplanted, nothing S agrees is wrong");
            planted.Agreements.Except(tip.Agreements).Should().NotBeEmpty("the plant fires");
            planted.Tally.Found.Should().Contain(f => Documents.Json(f.Document) == Documents.Json(plant.Witness), "source A finds the witness");
        }

        static readonly RenderingRun Disciplined = new(RunStopReason.Completed, true, false);

        /// <summary>
        /// Generates a plant's root in a bundle of a theory's own, and holds each agreement of the root, and of a negation's
        /// operand, at either position to Calcite on the documents drawn from the import and the witness, those the statement
        /// can hold.
        /// </summary>
        internal static CaseJudged Judge(RenderingTheory theory, CasePlant plant)
        {
            using var bundle = theory.Open(plant.Set);
            var term = bundle.Write(plant.Root);
            foreach (var path in plant.Root.Paths.Distinct())
                bundle.Import(Source, path, plant.Values?.GetValueOrDefault(path) ?? PathValues.Anything, JsonValues.AllBands);
            plant.Beneath?.Invoke(bundle);
            bundle.Generate(term);
            var run = bundle.RunGenerate(RenderingBundle.WidthOf([plant.Root]));
            var answer = bundle.ReadBest(term);
            SqlPredicate[] nodes = plant.Root is SqlPredicate.Not not ? [plant.Root, not.Operand] : [plant.Root];
            var agreements = (from node in nodes from pos in System.Enum.GetValues<RenderPosition>() from a in bundle.Agreements(bundle.Write(node), pos) where a.Claim == Verdict.Equal
                              select (node, pos, a.Form)).ToList();
            var claim = RenderingCheckRun.Claimed(bundle, [plant.Root], ServiceForm.ConstantOf(true), [null], [], null, null);
            var (tally, world, targets) = (new RenderingSoundnessTests.Tally(), new World(13), Measured.Serves[plant.Set]);
            foreach (var document in RenderingDocumentSampler.Sample(claim, 512).Append(plant.Witness).DistinctBy(Documents.Json).Where(d => plant.Holds?.Invoke(d) != false))
                foreach (var (node, pos, form) in agreements)
                    tally.Judge(RenderingSoundnessTests.Tally.Calcite(node, document, world), node, form, Verdict.Equal, pos, document, targets, $"{plant.Name} {pos}");

            return new(run, answer, agreements, tally);
        }

        /// <summary>The narrowing M33's trigger states: beneath the narrow stratum <c>[NOT c, kind = 'a']</c> the name is a string.</summary>
        static void StateNameAString(RenderingBundle bundle)
        {
            var (first, second) = (LabelIsBikes, Ne(Text(NamePath), "bikes"));
            var narrow = bundle.Strata.Single(s => s.Value.SequenceEqual([new SqlPredicate.Not(first), KindIsA])).Key;
            bundle.Assume(Source with { Stratum = narrow }, new SqlPredicate.Beneath(narrow, second), PathValues.OfKinds(ValueSet.Of(JsonValues.StringType)));
        }

        /// <summary>The documents M33's narrowing admits: where the label is a string other than "bikes" and kind is "a", the name is a string.</summary>
        static bool NameIsAStringWhereTheNarrowStratumHolds(J.Obj document) =>
            CosmosEval.At(document, SqlValue.Spell(LabelPath)) is J.Str { Value: not "bikes" } && CosmosEval.At(document, SqlValue.Spell(KindPath)) is J.Str { Value: "a" }
                ? CosmosEval.At(document, SqlValue.Spell(NamePath)) is J.Str : true;

        /// <summary>
        /// M17: a case's condition read at the position the case stands at, not at <c>Pos</c>, where it takes its then-arm:
        /// the absorbing else and the ternary each reading the condition's form at the case's own position.
        /// </summary>
        static void ConditionAtTheCasesPosition(RenderingTheory t)
        {
            var best = t.Graph.Rulesets.Single(r => r.Name == "best");
            best.DefineRule("planted M17: a case whose else absorbs is its condition's form at its own position joined with its then-arm", r =>
            {
                var (e, c, a, k) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "k");
                var kw = r.Var<Pred>("kw");
                var (pos, kv, j, ng) = r.Vars<RenderPosition, string, CosmosServiceTest, bool>("pos", "kv", "j", "ng");
                var (bc, ba) = r.Vars<Priced, Priced>("bc", "ba");
                var (cf, af, nc, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "nc", "h");
                var (pc, pa) = r.Vars<Proof, Proof>("pc", "pa");
                r.When(t.At.Of(e, pos), e.Is(t.Chosen.Of(c, a, k, kw)), k.Is(t.Ir.Truth.Of(kv)), t.AbsorbingElse.Of(pos, kv, j, ng), bc.Is(t.BestExact.Of(c, pos)), ba.Is(t.BestExact.Of(a, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bc));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.When(cf.Is(Compute.Of(bc, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), pc.Is(t.Agrees.Of(c, pos, cf, Verdict.Equal)), pa.Is(t.Agrees.Of(a, pos, af, Verdict.Equal)),
                    nc.Is(Compute.Of(ng, cf, ServiceForm.Signed)), h.Is(Compute.Of(j, nc, af, ServiceForm.Paired)));
                r.Then(t.Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(pc, pa, Proof.Join)), t.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });
            best.DefineRule("planted M17: a case's ternary reads its condition's form at the case's own position", r =>
            {
                var (e, c, a, bn) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "bn");
                var bw = r.Var<Pred>("bw");
                var pos = r.Var<RenderPosition>("pos");
                var (bc, ba, bb) = r.Vars<Priced, Priced, Priced>("bc", "ba", "bb");
                var (cf, af, bf, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "bf", "h");
                var (pc, px, py) = r.Vars<Proof, Proof, Proof>("pc", "px", "py");
                var (s, lic) = r.Vars<RenderingSet, ValueSet<CosmosServiceTest>>("s", "lic");
                r.When(t.At.Of(e, pos), e.Is(t.Chosen.Of(c, a, bn, bw)), bc.Is(t.BestExact.Of(c, pos)), ba.Is(t.BestExact.Of(a, pos)), bb.Is(t.BestExact.Of(bw, pos)),
                    t.Under.Of(s), lic.Is(t.Licensed.Of(s)));
                r.Where(Primitives.Contains<CosmosServiceTest>().Of(lic, CosmosServiceTest.Choose));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bc));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bb));
                r.When(cf.Is(Compute.Of(bc, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), bf.Is(Compute.Of(bb, Priced.FormOf)),
                    pc.Is(t.Agrees.Of(c, pos, cf, Verdict.Equal)), px.Is(t.Agrees.Of(a, pos, af, Verdict.Equal)), py.Is(t.Agrees.Of(bw, pos, bf, Verdict.Equal)),
                    h.Is(Compute.Of(cf, af, bf, ServiceForm.ChosenOf)));
                r.Then(t.Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(Compute.Of(pc, px, Proof.Join), py, Proof.Join)), t.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });
        }

        /// <summary>M31: the opposite-else rule without its <c>TwoValued</c> premise, one form still exact at both positions.</summary>
        static void OppositeWithoutTwoValued(RenderingTheory t) => Opposite(t, "planted M31: a case whose else is the opposite constant is its condition's form joined with its then-arm, two-valued or not",
            (r, pos, kv, j, ng) => r.When(t.OppositeElse.Of(pos, kv, j, ng)));

        /// <summary>M32: an unknown else read as the opposite constant at <c>Neg</c>, FALSE there, over a two-valued condition.</summary>
        static void UnknownAsTheOppositeAtNeg(RenderingTheory t) => Opposite(t, "planted M32: an unknown else at Neg is the opposite constant",
            (r, pos, kv, j, ng) =>
            {
                r.Where(pos, p => p == RenderPosition.Neg);
                r.Where(kv, v => v == "unknown");
                r.When(t.OppositeElse.Of(RenderPosition.Neg, "false", j, ng));
            }, twoValued: true);

        /// <summary>The opposite-else rule as built but for what reads the else: the condition's one form exact at both positions joined with the then-arm.</summary>
        static void Opposite(RenderingTheory t, string name, Action<RuleBuilder, Var<RenderPosition>, Var<string>, Var<CosmosServiceTest>, Var<bool>> reads, bool twoValued = false) =>
            t.Graph.Rulesets.Single(r => r.Name == "best").DefineRule(name, r =>
            {
                var (e, c, a, k) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "k");
                var kw = r.Var<Pred>("kw");
                var (pos, kv, j, ng) = r.Vars<RenderPosition, string, CosmosServiceTest, bool>("pos", "kv", "j", "ng");
                var (bp, bn, ba) = r.Vars<Priced, Priced, Priced>("bp", "bn", "ba");
                var (cf, af, nc, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "nc", "h");
                var (pc, pn, pa) = r.Vars<Proof, Proof, Proof>("pc", "pn", "pa");
                r.When(t.At.Of(e, pos), e.Is(t.Chosen.Of(c, a, k, kw)), k.Is(t.Ir.Truth.Of(kv)));
                reads(r, pos, kv, j, ng);
                if (twoValued)
                    r.When(t.TwoValued.Of(c));
                r.When(bp.Is(t.BestExact.Of(c, RenderPosition.Pos)), bn.Is(t.BestExact.Of(c, RenderPosition.Neg)), ba.Is(t.BestExact.Of(a, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bp));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.Where(RenderingPrimitives.SameForm.Of(bp, bn));
                r.When(cf.Is(Compute.Of(bp, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), pc.Is(t.Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)),
                    pn.Is(t.Agrees.Of(c, RenderPosition.Neg, cf, Verdict.Equal)), pa.Is(t.Agrees.Of(a, pos, af, Verdict.Equal)),
                    nc.Is(Compute.Of(ng, cf, ServiceForm.Signed)), h.Is(Compute.Of(j, nc, af, ServiceForm.Paired)));
                r.Then(t.Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(Compute.Of(pc, pn, Proof.Join), pa, Proof.Join)), t.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

        /// <summary>M33: the ternary's narrow else read over any condition, two-valued or not.</summary>
        static void NarrowWithoutTwoValued(RenderingTheory t) =>
            t.Graph.Rulesets.Single(r => r.Name == "best").DefineRule("planted M33: a case's ternary reads the narrow else whatever its condition", r =>
            {
                var (e, c, a, bn) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "bn");
                var bw = r.Var<Pred>("bw");
                var pos = r.Var<RenderPosition>("pos");
                var (bc, ba, bb) = r.Vars<Priced, Priced, Priced>("bc", "ba", "bb");
                var (cf, af, bf, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "bf", "h");
                var (pc, px, py) = r.Vars<Proof, Proof, Proof>("pc", "px", "py");
                var (s, lic) = r.Vars<RenderingSet, ValueSet<CosmosServiceTest>>("s", "lic");
                r.When(t.At.Of(e, pos), e.Is(t.Chosen.Of(c, a, bn, bw)), bc.Is(t.BestExact.Of(c, RenderPosition.Pos)), ba.Is(t.BestExact.Of(a, pos)), bb.Is(t.BestExact.Of(bn, pos)),
                    t.Under.Of(s), lic.Is(t.Licensed.Of(s)));
                r.Where(Primitives.Contains<CosmosServiceTest>().Of(lic, CosmosServiceTest.Choose));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bc));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bb));
                r.When(cf.Is(Compute.Of(bc, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), bf.Is(Compute.Of(bb, Priced.FormOf)),
                    pc.Is(t.Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)), px.Is(t.Agrees.Of(a, pos, af, Verdict.Equal)), py.Is(t.Agrees.Of(bn, pos, bf, Verdict.Equal)),
                    h.Is(Compute.Of(cf, af, bf, ServiceForm.ChosenOf)));
                r.Then(t.Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(Compute.Of(pc, px, Proof.Join), py, Proof.Join)), t.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

    }

}
