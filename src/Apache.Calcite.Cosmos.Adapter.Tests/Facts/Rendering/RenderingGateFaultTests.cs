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
    /// The gate faults (spec g06; line 18), each in a graph of its own: a row contradicting another, a template that drops
    /// the null guard, a service evaluation mutated beneath it, a candidate contaminated across two disjuncts of one path,
    /// a union; and in generate mode a best lifted from an implied agreement, presence written at <c>Neg</c> as at
    /// <c>Pos</c>, a negation's best from its operand's at <c>Pos</c>, the importer reading a conjunct's own stratum, a
    /// later round read beneath a stratum resting on a coarse reading, and a composite whose later arm fails where its
    /// stratum sets cells aside. Every one is caught.
    /// </summary>
    public sealed class RenderingGateFaultTests
    {

        internal const string NotBikes = "SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') <> 'bikes'";

        /// <summary>The gains preview's inequality over typed with its null guard dropped.</summary>
        internal const string Unguarded = "IS_PRIMITIVE(c.label) AND c.label != @p0";

        static readonly RenderingRun Disciplined = new(RunStopReason.Completed, true, false);

        /// <summary>The restricted form S sends for text <c>label &lt;&gt; 'bikes'</c> over an undeclared label (P1).</summary>
        static readonly ServiceForm Restricted = All(ServiceForm.TestOf(CosmosServiceTest.IsDefined, Label), Negated(ServiceForm.TestOf(CosmosServiceTest.IsNull, Label)),
            ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, Label), Bare);

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
        /// G2, over templates: a planted template proposes, beside every template's form, that form with its null guard
        /// dropped. Each check the pull request's run proved with a guard, asked again of generate mode in the planted
        /// graph, never keeps a guard-dropped form as a node's best where source A's documents show the guard kept something
        /// out, and some do show it; over typed, the gains' inequality without the guard is refuted at the null its document
        /// 6 holds, and so is the preview's text the check claims there, S's plants of M8 and M9 beneath it.
        /// </summary>
        [Fact]
        public void G2_ATemplateThatDropsTheNullGuardIsNeverBest()
        {
            var graph = Unguarding();
            var theory = graph.Current();
            var (mattering, kept) = (0, new List<string>());
            foreach (var claim in RenderingCheckRun.PerPullRequest.Value.Memo.Entries.Where(e => e.Asserted.Kind == RenderingVerdictKind.Proven && e.What is { } w && Unguard(w.Form).Equals(w.Form) == false)
                .Select(e => e.What!))
            {
                using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
                var terms = claim.Conjuncts.Select(c => bundle.Write(c)).ToList();
                RenderingCheckRun.Restate(bundle, claim);
                terms.ForEach(bundle.Generate);
                bundle.RunGenerate(RenderingBundle.WidthOf(claim.Conjuncts)).Should().Be(Disciplined);
                var candidates = bundle.Scope.Rows(theory.Candidate).Select(r => (ServiceForm)r.Keys[2]).ToHashSet();
                var documents = RenderingDocumentSampler.Sample(claim).ToList();
                var matters = candidates.Select(f => (Guarded: f, Dropped: Unguard(f))).Where(x => x.Dropped.Equals(x.Guarded) == false && candidates.Contains(x.Dropped))
                    .Where(x => documents.Any(d => RenderingCheckRun.Targets.Any(t => CosmosEval.On(x.Dropped, d, t) != CosmosEval.On(x.Guarded, d, t)))).Select(x => x.Dropped).ToHashSet();
                mattering += matters.Count;
                kept.AddRange(bundle.Scope.Rows(theory.BestExact).Select(r => ((Priced)r.Output!).Form).OfType<ServiceForm>().Where(matters.Contains).Select(f => f.Text));
                GC.KeepAlive(claim);
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{mattering} guard-dropped forms the documents show the guard matters for; {kept.Count} kept as a best");
            mattering.Should().BePositive();
            kept.Should().BeEmpty();

            Shadowed(graph, NotBikes).Bundles.SelectMany(b => b.Refutations ?? []).Where(r => r.Form.Equals(Unguard(Restricted)))
                .Select(r => RenderingGenerateMutationTests.Show(r.Refutation)).Distinct().Should().Equal("$[\"label\"]=null (U,t)");
            RenderingGenerateMutationTests.Show(RenderingRecallTests.Replay(NotBikes, [Unguarded], [RenderingRecallTests.Bikes], new RenderingCheckRun(graph: new RenderingGraph(() => ByRendering(Measured))))
                .Asserted).Should().Be("$[\"label\"]=null (U,t)");
        }

        /// <summary>
        /// G3, the key generate-mode fault: G2's planted template beneath a service evaluation mutated to answer <c>!=</c>
        /// over a null as false. The gate then agrees with the guard-dropped inequality, <c>Min</c> prefers it, and S sends
        /// it; source A finds the document with a null label, where <see cref="CosmosEval"/> says the service keeps it and
        /// Calcite does not. So it does of the preview's text the check claims beneath the mutation and S's plants of M8 and
        /// M9, which the gate agrees with. The witnesses run that document on the emulator.
        /// </summary>
        [Fact]
        public void G3_AMutatedServiceEvaluationIsFoundBySourceA()
        {
            var shadow = Shadowed(Unguarding(d => d with { Leaf = NullUnequalIsFalse(d.Leaf) }), NotBikes);
            var sent = shadow.Classes.Where(c => c.Ir == Ne(Text(LabelPath), "bikes").Digest).Select(c => c.Form).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join("; ", sent.Select(f => f?.Text)));
            sent.Should().NotBeEmpty().And.OnlyContain(f => Unguard(Restricted).Equals(f), "the mutated leaf makes the gate agree, and Min prefers it");
            SourceA(shadow).Witnesses.Should().Contain(w => w.Contains("on {\"label\":null}", StringComparison.Ordinal));

            var entry = RenderingRecallTests.Replay(NotBikes, [Unguarded], [RenderingRecallTests.Bikes], new RenderingCheckRun(graph: new RenderingGraph(() => ByRendering(Measured) is var d ? d with { Leaf = NullUnequalIsFalse(d.Leaf) } : d)));
            entry.Asserted.Kind.Should().Be(RenderingVerdictKind.Proven, "the mutated leaf makes the gate agree");
            var tally = new RenderingSoundnessTests.Tally();
            RenderingSoundnessTests.SourceA(tally, entry, "G3");
            tally.Witnesses.Should().Contain(w => w.Contains("on {\"label\":null}: Calcite Unknown", StringComparison.Ordinal));
        }

        /// <summary>
        /// G4, over templates (critic-check §3.2): the second disjunct's form offered by a planted template to the first,
        /// inside a disjunction of one path. It is never the first's best nor within the root's, and under <c>DOUBLE</c> it is
        /// refuted where the service drops a number Calcite keeps; the second disjunct's own form is exact on its own cells.
        /// </summary>
        [Theory]
        [InlineData("INTEGER", null)]
        [InlineData("DOUBLE", "$[\"price\"]=fractions > 5 double >= 5.000000000000001 (T,f)")]
        public void G4_AContaminatedCandidateIsNeverBest(string type, string? refuted)
        {
            var price = P("price");
            var (first, second) = (Cmp(Cast(type, Text(price)), ">", Lit(5)), Eq(Text(price), "x"));
            var spelled = SqlValue.Spell(price);
            var own = ServiceForm.CompareOf(CosmosServiceTest.Equal, spelled, JsonValues.Of("x"));
            var contaminated = All(ServiceForm.TestOf(CosmosServiceTest.IsDefined, spelled), own);

            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "spell").DefineRule("planted: the second disjunct's form offered to the first", r =>
            {
                var (e, p, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "p", "a");
                r.When(theory.At.Of(e, RenderPosition.Pos), theory.Ir.Atom.Of(e, p, a));
                r.Where(a, x => x.Reader.StartsWith("cast:", StringComparison.Ordinal));
                r.Then(theory.Candidate.Of(e, RenderPosition.Pos, contaminated));
            });

            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var root = Or(first, second);
            var (a, b, e) = (bundle.Write(first), bundle.Write(second), bundle.Write(root));
            bundle.Import(Source, price, PathValues.Anything, JsonValues.AllBands);
            bundle.Generate(e);
            bundle.RunGenerate(RenderingBundle.WidthOf([root])).Should().Be(Disciplined);

            bundle.Scope.Check(theory.Candidate.Of(a, RenderPosition.Pos, contaminated)).Should().BeTrue("the plant offered it");
            (bundle.Scope.TryGet(theory.Disagrees.Of(a, RenderPosition.Pos, contaminated, Verdict.Equal), out var refutation) ? RenderingGenerateMutationTests.Show(refutation) : null).Should().Be(refuted);
            Priced.FormOf(bundle.Scope.Get(theory.BestExact.Of(a, RenderPosition.Pos))).Should().NotBe(contaminated);
            bundle.ReadBest(e).Best?.Parts.Should().NotContain(contaminated);
            bundle.Scope.Get(theory.BestExact.Of(b, RenderPosition.Pos)).Form.Should().Be(own, "the second's own form is exact on its own cells");
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

        /// <summary>
        /// G6: a best lifted from an implied agreement. The raw inequality's bare form keeps a null, so it is implied and not
        /// exact, and cheaper than the guarded form the translator sends: lifted, S sends it. Source A judges a best as the
        /// Equal agreement it must be and finds the null, and the pairwise check parts it from the translator's text.
        /// </summary>
        [Fact]
        public void G6_ABestLiftedFromAnImpliedAgreementIsFoundBySourceAAndThePairwiseCheck()
        {
            var shadow = Shadowed(Planted(rules: theory => theory.Graph.Rulesets.Single(r => r.Name == "lift").DefineRule("planted: an implied agreement that may be sent is a best candidate", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var b = r.Var<ValueSet<string>>("b");
                r.When(theory.At.Of(e, pos), Fact.Exists(theory.Agrees.Of(e, pos, f, Verdict.Implies)), theory.Vacuous.Of(e, pos, f, Verdict.Implies).Is(false), b.Is(theory.PartingBands.Of()));
                r.Where(RenderingPrimitives.Sendable.Of(f, b));
                r.Then(theory.BestExact.Of(e, pos).Is(Compute.Of(f, Priced.Of)));
            })), "SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" <> 'bikes'");

            var bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, SqlValue.Spell(CategoryPath), JsonValues.Of("bikes"));
            shadow.Classes.Select(c => c.Form).Should().NotBeEmpty().And.OnlyContain(f => bare.Equals(f), "the implied bare form is the cheapest");
            SourceA(shadow).Witnesses.Should().Contain(w => w.Contains("on {\"category\":null}", StringComparison.Ordinal));
            RenderingPairwiseTests.Compare(shadow).Disagreements.Should().Contain(d => d.Contains("the translator's", StringComparison.Ordinal));
        }

        /// <summary>
        /// G7: <c>Implied</c>'s dual at <c>Neg</c> written wrongly, presence there as at <c>Pos</c>. Beneath a <c>NOT</c> a
        /// strict reading then implies its path present where the atom is FALSE, which a FALSE the service must keep there
        /// refutes: source A's Implies at <c>Neg</c> finds it.
        /// </summary>
        [Fact]
        public void G7_PresenceWrittenAtNegAsAtPosIsFoundBySourceA()
        {
            var shadow = Shadowed(Planted(rules: theory => theory.Graph.Rulesets.Single(r => r.Name == "strict").DefineRule("planted: a predicate over a strict value implies its path present at either position", r =>
            {
                var (e, pos, v, p) = r.Vars<Pred, RenderPosition, Val, JPath>("e", "pos", "v", "p");
                var n = r.Var<string>("n");
                r.When(theory.At.Of(e, pos), theory.Over.Of(e, v), theory.Strict.Of(v, p), n.Is(theory.Spelling.Of(p)));
                r.Then(theory.Implied.Of(e, pos).Is(Primitives.Singleton<ServiceForm>().Of(Compute.Of(n, path => ServiceForm.TestOf(CosmosServiceTest.IsDefined, path)))));
            })), RenderingRecallTests.Cases["P4"].Sql, RenderingRecallTests.Cases["P6"].Sql);

            SourceA(shadow).Found.Should().Contain(f => f.Position == RenderPosition.Neg && f.Form.Kind == ServiceFormKind.Test && f.Form.Test == CosmosServiceTest.IsDefined);
        }

        /// <summary>
        /// G8: a negation's best built from its operand's best at <c>Pos</c>, where the operand stands too. The bare equality
        /// negated is cheaper than the completed form stripped, so it is the negation's best, and source A at the root finds
        /// the null it keeps and Calcite does not.
        /// </summary>
        [Fact]
        public void G8_ANegationsBestFromItsOperandsPosFormIsFoundBySourceA()
        {
            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "best").DefineRule("planted: a negation's best is its operand's best at Pos, negated", r =>
            {
                var (e, a, pos) = r.Vars<Pred, Pred, RenderPosition>("e", "a", "pos");
                var (b, g, h, pa) = r.Vars<Priced, ServiceForm, ServiceForm, Proof>("b", "g", "h", "pa");
                r.When(theory.At.Of(e, pos), e.Is(theory.Ir.Not.Of(a)), b.Is(theory.BestExact.Of(a, RenderPosition.Pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(b));
                r.When(g.Is(Compute.Of(b, Priced.FormOf)), pa.Is(theory.Agrees.Of(a, RenderPosition.Pos, g, Verdict.Equal)), h.Is(Compute.Of(g, ServiceForm.Strip)));
                r.Then(theory.Agrees.Of(e, pos, h, Verdict.Equal).Is(pa), theory.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            var atom = Eq(Text(LabelPath), "bikes");
            var (best, judged) = Generated(theory, atom, Not(atom));
            best[Not(atom)].Should().Be(Negated(ServiceForm.CompareOf(CosmosServiceTest.Equal, Label, JsonValues.Of("bikes"))));
            judged.Witnesses.Should().Contain(w => w.Contains("at Pos", StringComparison.Ordinal) && w.Contains("on {\"label\":null}", StringComparison.Ordinal));
        }

        /// <summary>
        /// G11 (#117's shape): the stratum a conjunct is rendered from replaced by one holding its own facts, through the plant
        /// <see cref="CosmosOracle.RenderedFrom"/>. The importer reads the licence table's node, which assumes no conjunct it
        /// renders, so S proves the guarded form. Read beneath the node the renderer was handed, S would prove the bare one,
        /// and source B's declared documents where the conjunct's own facts do not hold refute it.
        /// </summary>
        [Fact]
        public void G11_AConjunctsOwnStratumIsNeverWhatTheImporterReads()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var declaration = generations.Bind(Theory(Outright(LabelPath, Present)));
            var conjunct = Ne(Text(LabelPath), "bikes");
            var handed = new List<(StatementTerms.Node Node, PredicateRules Rules)>();
            var oracle = new CosmosOracle(declaration, generations)
            {
                RenderedFrom = (terms, stratum, c) =>
                {
                    var own = terms.Child(stratum, [c]);
                    handed.Add((own, terms.Rules!));
                    return own;
                },
            };
            var scope = oracle.Admit(new PredicateStatement([conjunct]), (_, _) => true);
            scope.StratumOf(0).Should().Be(1);
            handed.Should().ContainSingle().Which.Node.Assumes(conjunct).Should().BeTrue("the plant hands the conjunct its own facts");
            scope.AssumedIn(OracleLicence.RenderConjunct(0)).Should().BeEmpty("the licence table renders no conjunct from its own facts");

            var root = RenderingCheckRun.RootOf([scope])!.Value;
            ServiceForm? Best(bool beneath)
            {
                using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
                var term = bundle.Write(conjunct);
                RenderingImporter.Import(bundle, new ImportSource("g11", "declaration", 1), root.Context, root.Rules, declaration, [LabelPath],
                    beneath ? [(conjunct, new ImportSource("g11", "render conjunct 0", 2), handed[0].Node.Scope, handed[0].Rules)] : null);
                bundle.Generate(term);
                bundle.RunGenerate(1).Should().Be(Disciplined);
                return bundle.ReadBest(term).Best;
            }

            var (honest, planted) = (Best(false), Best(true));
            (honest, planted).Should().Be((Restricted, Bare));

            J.Obj Document(J? label) => new(new(StringComparer.Ordinal) { ["label"] = label! });
            J.Obj[] declared = [Document(new J.Null()), Document(new J.Arr([])), Document(new J.Str("bikes")), Document(new J.Str("x")), Document(new J.Bool(true))];
            var (tallies, world) = (new Dictionary<bool, RenderingSoundnessTests.Tally> { [false] = new(), [true] = new() }, new World(11));
            foreach (var document in declared)
                foreach (var (beneath, form) in new[] { (false, honest!), (true, planted!) })
                    tallies[beneath].Judge(RenderingSoundnessTests.Tally.Calcite(conjunct, document, world), conjunct, form, Verdict.Equal, RenderPosition.Pos, document, RenderingCheckRun.Targets, "G11");

            tallies[false].Witnesses.Should().BeEmpty();
            tallies[true].Witnesses.Should().Contain(w => w.Contains("on {\"label\":null}", StringComparison.Ordinal));
            GC.KeepAlive(scope);
        }

        /// <summary>
        /// G12 (critic finding 1; <c>TODO.md</c>, a stratum resting on a coarse reading): the TODO's statement, whose second
        /// round reads the label beside kind's stratum, which the facts graph reads as the integer 5 where a column reads
        /// 5.0000000000000001 as 5 too, beside a label the declaration leaves free. S states nothing beneath that stratum, so
        /// what it sends for the statement under each set — admitted forms and weakenings — keeps the document of kind
        /// 5.0000000000000001 and label 7, as Calcite does. The plant, a stratum read as exact whatever it rests on, sends
        /// the bare label and drops it.
        /// </summary>
        [Fact]
        public void G12_WhatIsSentBeneathAStratumACoarseReadingRestsOnKeepsTheLabelSeven()
        {
            var container = new CosmosContainerMetadata("t", ["/kind"])
                .WithFacts([Outright(KindPath, Present), When(KindPath, 5m, LabelPath, Type(JsonType.String))]).WithFactGenerations(new JsonFactGenerations());
            const string Sql = "SELECT c.\"id\" FROM t AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind' RETURNING INTEGER) = 5 AND JSON_VALUE(c.\"DOC\", '$.label') = '7'";
            var document = new J.Obj(new(StringComparer.Ordinal) { ["kind"] = new J.Num(5.0000000000000001m), ["label"] = new J.Num(7m) });

            var calcite = RenderingSoundnessTests.Tally.Calcite(And(Cmp(Typed(KindPath, "INTEGER"), "=", Lit(5m)), Eq(Text(LabelPath), "7")), document, new World(12));
            calcite.Should().Contain(Truths.True, "Calcite keeps the document");

            foreach (var (planted, keeps) in new[] { (false, true), (true, false) })
            {
                var shadow = RenderingCheckRun.Over(RenderingInputs.Differential("stratum", [Sql], [container]),
                    shadow: new RenderingGenerateRun(sets: RenderingGenerateRun.AssertedSets, readExactly: planted ? _ => true : null)).Shadow!;
                foreach (var set in RenderingGenerateRun.AssertedSets)
                {
                    var sent = shadow.Classes.Where(c => c.Set == set).Select(c => c.Form).Concat(shadow.Weakenings.Where(w => w.Set == set && w.Residual).Select(w => w.Mine)).OfType<ServiceForm>().ToList();
                    var kept = Measured.Serves[set].All(target => sent.All(f => CosmosEval.On(f, document, target) == ServiceOutcomes.True));
                    TestContext.Current.TestOutputHelper?.WriteLine($"{(planted ? "planted" : "as built")} {set}: {string.Join(" AND ", sent.Select(f => f.Text))}: keeps {kept}");
                    kept.Should().Be(keeps, $"{set}, {(planted ? "planted" : "as built")}");
                }
            }
        }

        /// <summary>
        /// G14: a composite whose later round's arm answers <c>Fails</c> on the cells its stratum sets aside. A set-aside cell
        /// tolerates any answer but a failure, so that arm has no exact form, and the round's junction, the composite S
        /// would admit, has none either; as built, both are proven.
        /// </summary>
        [Fact]
        public void G14_ACompositeWhoseLaterArmFailsOnItsSetAsideCellsIsNotAdmitted()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var declaration = generations.Bind(Theory(Outright(KindPath, Present), When(KindPath, "a", LabelPath, Type(JsonType.String))));
            var (kind, label) = (Eq(Text(KindPath), "a"), Eq(Text(LabelPath), "30"));
            var scope = new CosmosOracle(declaration, generations).Admit(new PredicateStatement([kind, label]),
                (i, facts) => i == 0 || facts.Knows(new Cosmos.Facts.JsonFact(LabelPath, Type(JsonType.String))));
            scope.StratumOf(1).Should().Be(2);

            var (root, stratum) = (RenderingCheckRun.RootOf([scope])!.Value, scope.ContextFor(OracleLicence.RenderConjunct(1)));
            var through = SqlPredicate.AllOf([kind, label]);
            (ServiceForm? Arm, ServiceForm? Composite) Generate(RenderingTheory theory)
            {
                using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
                var (k, l, t) = (bundle.Write(kind), bundle.Write(label), bundle.Write(through));
                RenderingImporter.Import(bundle, new ImportSource("g14", "declaration", 1), root.Context, root.Rules, declaration, [KindPath, LabelPath],
                    [(label, new ImportSource("g14", "render conjunct 1", 2), stratum.Context, stratum.Rules)]);
                new[] { k, l, t }.ToList().ForEach(bundle.Generate);
                bundle.RunGenerate(RenderingBundle.WidthOf([kind, label, through])).Should().Be(Disciplined);
                return (bundle.ReadBest(l).Best, bundle.ReadBest(t).Best);
            }

            var failing = RenderingTheory.Define(Measured with
            {
                Leaf = (form, cells, table, parted) => Measured.Leaf(form, cells, table, parted) is var vector && cells.Outside is { Count: > 0 } outside
                    ? vector with { Outcomes = [.. vector.Outcomes.Zip(cells.Cells, (o, cell) => outside.Contains(cell) ? o with { Outcomes = ServiceOutcomes.Fails } : o)] }
                    : vector,
            });

            Generate(ProbeTheory).Should().Be((ServiceForm.CompareOf(CosmosServiceTest.Equal, Label, JsonValues.Of("30")),
                All(ServiceForm.CompareOf(CosmosServiceTest.Equal, SqlValue.Spell(KindPath), JsonValues.Of("a")), ServiceForm.CompareOf(CosmosServiceTest.Equal, Label, JsonValues.Of("30")))));
            Generate(failing).Should().Be(((ServiceForm?)null, (ServiceForm?)null));
            GC.KeepAlive(scope);
        }

        /// <summary>G2's graph: a planted template proposing, beside each template's form, that form with its null guard dropped, over a definition given.</summary>
        static RenderingGraph Unguarding(Func<RenderingDefinition, RenderingDefinition>? define = null) => Planted(define, theory =>
            theory.Graph.Rulesets.Single(r => r.Name == "spell").DefineRule("planted: a template's form with its null guard dropped", r =>
            {
                var (e, p, a, pos) = r.Vars<Pred, JPath, PredicateAtom, RenderPosition>("e", "p", "a", "pos");
                var (t, n, sh) = r.Vars<TemplateId, string, string>("t", "n", "sh");
                r.When(theory.At.Of(e, pos), theory.Ir.Atom.Of(e, p, a), n.Is(theory.Spelling.Of(p)), theory.Template.Of(t, pos), sh.Is(Compute.Of(a, RenderingTemplates.ShapeOf)), theory.Fits.Of(t, sh));
                r.Then(theory.Candidate.Of(e, pos, Compute.Of(t, pos, a, n, (id, q, x, y) => Unguard(RenderingTemplates.Spell(id, q, x, y)))));
            }));

        /// <summary>S's plants of M8 and M9: a boolean's <c>!=</c> and orderings with a string answered by its rendering.</summary>
        static RenderingDefinition ByRendering(RenderingDefinition definition) => RenderingGenerateMutationTests.Plants["M9"].Define(RenderingGenerateMutationTests.Plants["M8"].Define(definition));

        /// <summary>G3's mutation: the service's <c>!=</c> over a null answered false.</summary>
        static Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> NullUnequalIsFalse(Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> leaf) =>
            (form, cells, table, parted) => leaf(form, cells, table, parted) is var vector && form.Test == CosmosServiceTest.NotEqual
                ? vector with { Outcomes = [.. vector.Outcomes.Zip(cells.Cells, (o, cell) => cell is PathCell.Null ? o with { Outcomes = ServiceOutcomes.False } : o)] }
                : vector;

        /// <summary>A graph of a plant's own, never spent, defined from the registry's definition as changed and holding the rules given beside the right ones.</summary>
        static RenderingGraph Planted(Func<RenderingDefinition, RenderingDefinition>? define = null, Action<RenderingTheory>? rules = null)
        {
            var graph = new RenderingGraph(() => define?.Invoke(Measured) ?? Measured, _ => false);
            rules?.Invoke(graph.Current());
            return graph;
        }

        /// <summary>Statements planned as the differential plans them, shadowed through a graph under the sets the legs plan under.</summary>
        static RenderingGenerateRun Shadowed(RenderingGraph graph, params string[] statements)
        {
            var run = RenderingCheckRun.Over(RenderingInputs.Differential("gate fault", statements, RenderingGenerateMutationTests.Containers),
                shadow: new RenderingGenerateRun(graph, RenderingGenerateRun.AssertedSets));
            run.Failures.Should().BeEmpty();
            return run.Shadow!;
        }

        /// <summary>Source A over a shadow's generate bundles.</summary>
        static RenderingSoundnessTests.Tally SourceA(RenderingGenerateRun shadow)
        {
            var tally = new RenderingSoundnessTests.Tally();
            RenderingSoundnessTests.Generated(tally, shadow.Bundles, "gate fault");
            TestContext.Current.TestOutputHelper?.WriteLine($"{tally}: {string.Join(" | ", tally.Witnesses.Take(3))}");
            return tally;
        }

        /// <summary>
        /// Generate mode over roots in a bundle of a theory's own, every path imported as anything; each node's best at
        /// <c>Pos</c>, and source A over every agreement of every node at either position.
        /// </summary>
        static (Dictionary<SqlPredicate, ServiceForm?> Best, RenderingSoundnessTests.Tally Judged) Generated(RenderingTheory theory, params SqlPredicate[] roots)
        {
            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var nodes = roots.SelectMany(r => r.Subterms).Distinct().ToList();
            var terms = nodes.ToDictionary(n => n, n => bundle.Write(n));
            foreach (var path in roots.SelectMany(r => r.Paths).Distinct())
                bundle.Import(Source, path, PathValues.Anything, JsonValues.AllBands);
            foreach (var root in roots)
                bundle.Generate(terms[root]);
            bundle.RunGenerate(RenderingBundle.WidthOf(roots)).Should().Be(Disciplined);

            var forms = (from n in nodes from pos in System.Enum.GetValues<RenderPosition>() from a in bundle.Agreements(terms[n], pos) select (n, pos, a.Form, a.Claim)).ToList();
            var claim = RenderingCheckRun.Claimed(bundle, roots, ServiceForm.ConstantOf(true), [.. roots.Select(_ => (ServiceForm?)null)], [], null);
            var (tally, world) = (new RenderingSoundnessTests.Tally(), new World(8));
            foreach (var document in RenderingDocumentSampler.Sample(claim))
                foreach (var (n, pos, form, claimed) in forms)
                    tally.Judge(RenderingSoundnessTests.Tally.Calcite(n, document, world), n, form, claimed, pos, document, RenderingCheckRun.Targets, "generated");

            return (nodes.ToDictionary(n => n, n => bundle.Scope.TryGet(theory.BestExact.Of(terms[n], RenderPosition.Pos), out var b) ? b.Form : null), tally);
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
