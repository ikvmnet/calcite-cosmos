using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The rendering graph S: what the service answers, settled per set of targets, the agreement and the connectives each
    /// set composes through, loaded into a frozen theory; and the rules that, in a bundle beneath it, evaluate a written
    /// form beside the predicate it was written from on every cell of an atom's path, decide whether it agrees or is
    /// refuted, and carry both through the predicate's AND, OR and NOT; and, in generate mode, propose an atom's forms from
    /// templates, keep the cheapest exact one of every node and build each node's weakening.
    /// </summary>
    /// <remarks>
    /// Every fact that is derived or combined is a rule here, and every universal reads complete inputs (<c>DESIGN.md</c>,
    /// <i>The rendering graph in check mode</i> and <i>in generate mode</i>).
    /// </remarks>
    internal sealed class RenderingTheory
    {

        RenderingTheory(RenderingDefinition definition)
        {
            Graph = new EGraph();
            Paths = new PathVocabulary(Graph);
            Ir = new PredicateVocabulary(Graph);
            Register(Graph.Values);

            Outcome = Graph.DefineFunction<MeasuredKey, RenderingTarget, CosmosAnswer>("outcome");
            Cites = Graph.DefineFunction<MeasuredKey, RenderingTarget, ValueSet<string>>("cites", ValueLattice.Union<string>());
            Serves = Graph.DefineFunction<RenderingSet, ValueSet<RenderingTarget>>("serves");
            Means = Graph.DefineFunction<AnswerMeanings>("means");
            Answers = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("answers", ValueLattice.Union<CosmosAnswer>());
            Measured = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<RenderingTarget>>("measured", ValueLattice.Union<RenderingTarget>());
            Evidence = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<string>>("evidence", ValueLattice.Union<string>());
            Final = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("final");
            Table = Graph.DefineFunction<RenderingSet, ValueSet<MeasuredEntry>>("table", ValueLattice.Union<MeasuredEntry>());
            Class = Graph.DefineRelation<CellClass>("class");
            RaiseOnly = Graph.DefineFunction<ValueSet<CellClass>>("raiseonly", ValueLattice.Union<CellClass>());
            Allowing = Graph.DefineRelation<RenderPosition, Verdict, Box>("allowing");
            Refuting = Graph.DefineRelation<RenderPosition, Verdict, Box>("refuting");
            Tolerated = Graph.DefineFunction<Truths>("tolerated");
            Allowed = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<CellClass>>("allowed", ValueLattice.Union<CellClass>());
            AllowedCites = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<string>>("allowedcites", ValueLattice.Union<string>());
            Refutes = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<CellClass>>("refutes", ValueLattice.Union<CellClass>());
            Edges = Graph.DefineFunction<string, ReaderEdges>("edges");
            Parting = Graph.DefineRelation<string, string>("parting");
            Kleene = Graph.DefineFunction<MeasuredKey, RenderingTarget, CosmosAnswer>("kleene");
            Expected = Graph.DefineFunction<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>>("expected", ValueLattice.Union<MeasuredKey>());
            Matched = Graph.DefineFunction<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>>("matched", ValueLattice.Union<MeasuredKey>());
            Licensed = Graph.DefineFunction<RenderingSet, ValueSet<CosmosServiceTest>>("licensed", ValueLattice.Union<CosmosServiceTest>());
            Identity = Graph.DefineFunction<CosmosServiceTest, ValueSet<CellClass>>("identity");
            Opposite = Graph.DefineRelation<RenderPosition, RenderPosition>("opposite");

            Spelling = Graph.DefineFunction<JPath, string>("spelling");
            Conjunct = Graph.DefineRelation<ServiceForm, ServiceForm>("conjunct");
            Under = Graph.DefineRelation<RenderingSet>("under");
            Imported = Graph.DefineFunction<JPath, PathValues>("imported");
            Assumed = Graph.DefineFunction<Pred, PathValues>("assumed");
            ImportedChars = Graph.DefineFunction<JPath, ValueSet<string>>("importedchars");
            PartedLiterals = Graph.DefineFunction<JPath, ValueSet<string>>("partedliterals", ValueLattice.Union<string>());
            Candidate = Graph.DefineRelation<Pred, RenderPosition, ServiceForm>("candidate");
            Member = Graph.DefineRelation<ServiceForm, ServiceForm>("member");
            Partition = Graph.DefineFunction<Pred, ServiceForm, CellPartition>("partition");
            Need = Graph.DefineRelation<CellPartition, ServiceForm>("need");
            Svc = Graph.DefineFunction<CellPartition, ServiceForm, OutcomeVector>("svc");
            Outcomes = Graph.DefineFunction<Pred, ServiceForm, CellOutcomes>("outcomes");
            Agrees = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, Proof>("agrees", ValueLattice.Min<Proof>());
            Vacuous = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, bool>("vacuous", ValueLattice.Max<bool>());
            Disagrees = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, Refutation>("disagrees", ValueLattice.Min<Refutation>());
            RefutedAt = Graph.DefineRelation<ValueSet<string>, JPath>("refutedat");
            Conflict = Graph.DefineRelation<Pred, RenderPosition, ServiceForm, Verdict>("conflict");
            var analysis = new TableOptions { Role = TableRole.Analysis };
            Junction = Graph.DefineRelation<Pred, Pred, Pred, CosmosServiceTest>("junction", analysis);
            First = Graph.DefineRelation<Pred, Pred>("first", analysis);
            Within = Graph.DefineRelation<Pred, RenderPosition, ServiceForm>("within");
            Needs = Graph.DefineFunction<ServiceForm, ValueSet<CosmosServiceTest>>("needs", ValueLattice.Union<CosmosServiceTest>());
            Neutral = Graph.DefineFunction<Pred, ServiceForm, CosmosServiceTest, Witness>("neutral", ValueLattice.Min<Witness>());
            Disjoint = Graph.DefineRelation<JPath, JPath>("disjoint");

            At = Graph.DefineRelation<Pred, RenderPosition>("at");
            Template = Graph.DefineRelation<TemplateId, RenderPosition>("template");
            Fits = Graph.DefineRelation<TemplateId, string>("fits");
            PartingBands = Graph.DefineFunction<ValueSet<string>>("partingbands");
            RexGrammar = Graph.DefineFunction<RenderingRexGrammar.Value>("rexgrammar");
            StrictOp = Graph.DefineRelation<string>("strictop");
            StrictReader = Graph.DefineRelation<string>("strictreader");
            BestExact = Graph.DefineFunction<Pred, RenderPosition, Priced>("bestexact", ValueLattice.Min<Priced>());
            Implied = Graph.DefineFunction<Pred, RenderPosition, ValueSet<ServiceForm>>("implied", ValueLattice.Union<ServiceForm>());
            Weakening = Graph.DefineFunction<Pred, RenderPosition, ServiceForm>("weakening");
            Leaf = Graph.DefineRelation<Pred>("leaf");
            Over = Graph.DefineRelation<Pred, Val>("over");
            CallArg = Graph.DefineRelation<Val, Val>("callarg");
            Strict = Graph.DefineRelation<Val, JPath>("strict");

            var measure = Graph.DefineRuleset("measure");
            var allowed = Graph.DefineRuleset("allowed");
            var refutes = Graph.DefineRuleset("refutes");
            var tabulate = Graph.DefineRuleset("tabulate");
            var expected = Graph.DefineRuleset("expected");
            var matched = Graph.DefineRuleset("matched");
            var licence = Graph.DefineRuleset("licence");
            DefineTheory(measure, allowed, refutes, tabulate);
            DefineLicences(expected, matched, licence);

            var structure = Graph.DefineRuleset("structure");
            var descend = Graph.DefineRuleset("descend");
            var disjoint = Graph.DefineRuleset("disjoint");
            DefineDescent(structure, descend, disjoint);

            var partition = Graph.DefineRuleset("partition");
            var need = Graph.DefineRuleset("need");
            var parted = Graph.DefineRuleset("parted");
            var evaluate = Graph.DefineRuleset("evaluate");
            var classes = Graph.DefineRuleset("classes");
            var gate = Graph.DefineRuleset("gate");
            var compose = Graph.DefineRuleset("compose");
            var conflict = Graph.DefineRuleset("conflict");
            DefineCells(definition, partition, need, parted);
            DefineEvaluation(definition, evaluate);
            DefineGate(definition, classes, gate, conflict);
            DefineComposition(compose);

            var at = Graph.DefineRuleset("at");
            var spell = Graph.DefineRuleset("spell");
            var lift = Graph.DefineRuleset("lift");
            var strict = Graph.DefineRuleset("strict");
            var best = Graph.DefineRuleset("best");
            var weaken = Graph.DefineRuleset("weaken");
            DefineProposal(at, spell);
            DefineLifts(lift, strict);
            DefineChoice(best, weaken);

            Settle = Schedule.Sequence(Schedule.Saturate(measure, allowed, refutes, expected), Schedule.Saturate(tabulate), Schedule.Saturate(matched),
                Schedule.Saturate(licence));
            Check = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, structure, descend, disjoint), Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes, gate), Schedule.Saturate(compose, conflict));
            Generate = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, structure, at, spell), Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes, gate), Schedule.Saturate(lift, strict), Schedule.Saturate(best, conflict),
                Schedule.Saturate(weaken));

            Graph.Root.Freeze();
            Theory = Graph.Root.CreateChild();
            Load(definition);
        }

        public EGraph Graph { get; }

        /// <summary>The frozen scope that holds the rows, settled; every bundle is a child of it.</summary>
        public Scope Theory { get; }

        public PathVocabulary Paths { get; }

        public PredicateVocabulary Ir { get; }

        /// <summary>The theory's schedule: the rows settled per set, the classes the boxes allow and refute, and the licences.</summary>
        public Schedule Settle { get; }

        /// <summary>
        /// A bundle's schedule, in strata: terms into atoms, and the candidates down the tree; cells; the service on them;
        /// the gate on atoms; verdicts up the tree.
        /// </summary>
        public Schedule Check { get; }

        /// <summary>
        /// Generate mode's schedule: positions down the tree and the templates' forms; the cells, the service and the gate
        /// on them as in check mode; what the gate agreed lifted, and presence through calls; the bests composed up the
        /// tree; one weakening a node, from complete inputs.
        /// </summary>
        public Schedule Generate { get; }

        // The theory's tables: the rows as measured and as settled per set, and the agreement. `final` and `table` hold a
        // key once every target its set serves has measured it.
        public Function<MeasuredKey, RenderingTarget, CosmosAnswer> Outcome { get; }
        public Function<MeasuredKey, RenderingTarget, ValueSet<string>> Cites { get; }
        public Function<RenderingSet, ValueSet<RenderingTarget>> Serves { get; }
        public Function<AnswerMeanings> Means { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> Answers { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<RenderingTarget>> Measured { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<string>> Evidence { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> Final { get; }
        public Function<RenderingSet, ValueSet<MeasuredEntry>> Table { get; }
        public Relation<CellClass> Class { get; }
        public Function<ValueSet<CellClass>> RaiseOnly { get; }
        public Relation<RenderPosition, Verdict, Box> Allowing { get; }
        public Relation<RenderPosition, Verdict, Box> Refuting { get; }
        public Function<Truths> Tolerated { get; }
        public Function<RenderPosition, Verdict, ValueSet<CellClass>> Allowed { get; }
        public Function<RenderPosition, Verdict, ValueSet<string>> AllowedCites { get; }
        public Function<RenderPosition, Verdict, ValueSet<CellClass>> Refutes { get; }
        public Function<string, ReaderEdges> Edges { get; }
        public Relation<string, string> Parting { get; }

        // Composition's: Kleene's answers, the keys each set is expected and matched on, the connectives it composes
        // through, an identity's class and the position beneath a negation.
        public Function<MeasuredKey, RenderingTarget, CosmosAnswer> Kleene { get; }
        public Function<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>> Expected { get; }
        public Function<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>> Matched { get; }
        public Function<RenderingSet, ValueSet<CosmosServiceTest>> Licensed { get; }
        public Function<CosmosServiceTest, ValueSet<CellClass>> Identity { get; }
        public Relation<RenderPosition, RenderPosition> Opposite { get; }

        // A bundle's tables. The host writes a path's spelling beside its term (a merge of two paths would give one class
        // two spellings, and throw), the set, each import once and lattice-free, beside each atom what the facts its
        // conjunct was rendered from say its path may hold (`assumed`, anything where none is read), and the root's
        // candidate with every member of its junctions as written and each conjunction's members, nested ones flattened;
        // the rules write the rest: the forms each junction lies within, the candidates beneath the root, and the members
        // of a member of one connective.
        public Function<JPath, string> Spelling { get; }
        public Relation<ServiceForm, ServiceForm> Conjunct { get; }
        public Relation<RenderingSet> Under { get; }
        public Function<JPath, PathValues> Imported { get; }
        public Function<Pred, PathValues> Assumed { get; }
        public Function<JPath, ValueSet<string>> ImportedChars { get; }
        public Function<JPath, ValueSet<string>> PartedLiterals { get; }
        public Relation<Pred, RenderPosition, ServiceForm> Candidate { get; }
        public Relation<ServiceForm, ServiceForm> Member { get; }
        public Function<Pred, ServiceForm, CellPartition> Partition { get; }
        public Relation<CellPartition, ServiceForm> Need { get; }
        public Function<CellPartition, ServiceForm, OutcomeVector> Svc { get; }
        public Function<Pred, ServiceForm, CellOutcomes> Outcomes { get; }
        public Function<Pred, RenderPosition, ServiceForm, Verdict, Proof> Agrees { get; }

        /// <summary>Whether an agreement holds of nothing: by any of its proofs, Calcite raises on every row (a disjunction, by its lattice).</summary>
        public Function<Pred, RenderPosition, ServiceForm, Verdict, bool> Vacuous { get; }
        public Function<Pred, RenderPosition, ServiceForm, Verdict, Refutation> Disagrees { get; }

        /// <summary>The one location a refutation on an atom's cells sets, by its cells; a junction's sets two, and has none.</summary>
        public Relation<ValueSet<string>, JPath> RefutedAt { get; }
        public Relation<Pred, RenderPosition, ServiceForm, Verdict> Conflict { get; }

        /// <summary>A junction of the IR, its connective, and each arm beside the other; <see cref="First"/> names its first.</summary>
        public Relation<Pred, Pred, Pred, CosmosServiceTest> Junction { get; }
        public Relation<Pred, Pred> First { get; }

        /// <summary>
        /// A junction, a position and a form of its own connective it lies within: one it is a candidate for, or one its
        /// parent of that connective lies within. Its arms take the form's parts, and its own forms are built up from theirs.
        /// </summary>
        public Relation<Pred, RenderPosition, ServiceForm> Within { get; }
        public Function<ServiceForm, ValueSet<CosmosServiceTest>> Needs { get; }
        public Function<Pred, ServiceForm, CosmosServiceTest, Witness> Neutral { get; }
        public Relation<JPath, JPath> Disjoint { get; }

        // Generate mode's. The theory holds the templates asked at each position and the shapes each fits, the parting bands
        // as one set, the Rex builder's grammar, and the operators and readers Calcite answers null for over a null; the host
        // writes a root where it stands (`at`), and beside the terms each leaf, the values a comparison or membership is
        // over and a call's operands. The rules write the rest: each node's position, its cheapest exact form (`None`
        // until one is found), the forms it implies, and one weakening, lattice-free, which a second write faults.
        public Relation<Pred, RenderPosition> At { get; }
        public Relation<TemplateId, RenderPosition> Template { get; }
        public Relation<TemplateId, string> Fits { get; }
        public Function<ValueSet<string>> PartingBands { get; }
        public Function<RenderingRexGrammar.Value> RexGrammar { get; }
        public Relation<string> StrictOp { get; }
        public Relation<string> StrictReader { get; }
        public Function<Pred, RenderPosition, Priced> BestExact { get; }
        public Function<Pred, RenderPosition, ValueSet<ServiceForm>> Implied { get; }
        public Function<Pred, RenderPosition, ServiceForm> Weakening { get; }
        public Relation<Pred> Leaf { get; }
        public Relation<Pred, Val> Over { get; }
        public Relation<Val, Val> CallArg { get; }

        /// <summary>A value Calcite reads as null where a path is absent.</summary>
        public Relation<Val, JPath> Strict { get; }

        /// <summary>
        /// Builds S from a definition and loads its theory: one batch per axiom, then the host rows, settled and frozen.
        /// </summary>
        /// <exception cref="FactConflictException">
        /// A row contradicts another for one key and target. Its <c>Data["axiom"]</c> names the axiom whose batch was
        /// refused, and nothing of it was written; no S is built.
        /// </exception>
        public static RenderingTheory Define(RenderingDefinition definition) => new(definition);

        /// <summary>Opens a bundle under a set: a child of the theory, holding one statement round's terms, import and candidates.</summary>
        public RenderingBundle Open(RenderingSet set) => new(this, set);

        /// <summary>Whether a bundle found the graph out of ids or epochs, which retires its generation at once.</summary>
        internal volatile bool RanOut;

        static void Register(ValueStore values)
        {
            values.RegisterEnum<CosmosServiceTest>();
            values.RegisterEnum<CosmosValueClass>();
            values.RegisterEnum<CosmosAnswer>();
            values.RegisterEnum<RenderingTarget>();
            values.RegisterEnum<RenderingSet>();
            values.RegisterEnum<RenderPosition>();
            values.RegisterEnum<Verdict>();
            values.RegisterEnum<Truths>();
            values.RegisterEnum<ServiceOutcomes>();
            values.RegisterEnum<TemplateId>();
            values.Register<ServiceForm>("service form");
            values.Register<Priced>("priced");
            values.Register<RenderingRexGrammar.Value>("rex grammar");
            values.Register<CellClass>("cell class");
            values.Register<Box>("box");
            values.Register<Proof>("proof");
            values.Register<Refutation>("refutation");
            values.Register<MeasuredKey>("measured key");
            values.Register<MeasuredEntry>("measured entry");
            values.Register<AnswerMeanings>("answer meanings");
            values.Register<ServiceTable>("service table");
            values.Register<ReaderEdges>("reader edges");
            values.Register<CutPoint>("cut point");
            values.Register<CellPartition>("cell partition");
            values.Register<OutcomeVector>("outcome vector");
            values.Register<CellOutcomes>("cell outcomes");
            values.Register<Witness>("witness");
            values.RegisterSet<CosmosAnswer>();
            values.RegisterSet<RenderingTarget>();
            values.RegisterSet<CellClass>();
            values.RegisterSet<MeasuredEntry>();
            values.RegisterSet<CutPoint>();
            values.RegisterSet<MeasuredKey>();
            values.RegisterSet<CosmosServiceTest>();
            values.RegisterSet<ServiceForm>();
        }

        void DefineTheory(Ruleset measure, Ruleset allowed, Ruleset refutes, Ruleset tabulate)
        {
            measure.DefineRule("a measured answer is one a set's service may give", r =>
            {
                var (k, x, s, o) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "x", "s", "o");
                var (d, i) = r.Vars<ValueSet<RenderingTarget>, ValueSet<string>>("d", "i");
                r.When(o.Is(Outcome.Of(k, x)), d.Is(Serves.Of(s)), i.Is(Cites.Of(k, x)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Then(Answers.Of(k, s).Is(Primitives.Singleton<CosmosAnswer>().Of(o)), Measured.Of(k, s).Is(Primitives.Singleton<RenderingTarget>().Of(x)),
                    Evidence.Of(k, s).Is(i));
            });

            allowed.DefineRule("a class within an allowing box is allowed", r =>
            {
                var (k, b, tol) = r.Vars<CellClass, Box, Truths>("k", "b", "tol");
                var (pos, v) = r.Vars<RenderPosition, Verdict>("pos", "v");
                var (kc, bc, ks, bs) = r.Vars<Truths, Truths, ServiceOutcomes, ServiceOutcomes>("kc", "bc", "ks", "bs");
                r.When(Class.Of(k), Allowing.Of(pos, v, b), tol.Is(Tolerated.Of()), kc.Is(Compute.Of(k, tol, (x, y) => x.Calcite & ~y)),
                    bc.Is(Compute.Of(b, x => x.Calcite)), ks.Is(Compute.Of(k, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, bc));
                r.Where(RenderingPrimitives.OutcomesWithin.Of(ks, bs));
                r.Then(Allowed.Of(pos, v).Is(Primitives.Singleton<CellClass>().Of(k)), AllowedCites.Of(pos, v).Is(Compute.Of(b, x => x.Cites)));
            });

            allowed.DefineRule("a class whose every truth of Calcite's the policy sets aside is one Calcite raises on alone", r =>
            {
                var (k, tol, kc) = r.Vars<CellClass, Truths, Truths>("k", "tol", "kc");
                r.When(Class.Of(k), tol.Is(Tolerated.Of()), kc.Is(Compute.Of(k, x => x.Calcite)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, tol));
                r.Then(RaiseOnly.Of().Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            // Calcite's truths as they are: a class that may raise is no definite counterexample, whatever the box says.
            refutes.DefineRule("a class within a refuting box is a definite counterexample", r =>
            {
                var (k, b) = r.Vars<CellClass, Box>("k", "b");
                var (pos, v) = r.Vars<RenderPosition, Verdict>("pos", "v");
                var (kc, bc, ks, bs) = r.Vars<Truths, Truths, ServiceOutcomes, ServiceOutcomes>("kc", "bc", "ks", "bs");
                r.When(Class.Of(k), Refuting.Of(pos, v, b), kc.Is(Compute.Of(k, x => x.Calcite)), bc.Is(Compute.Of(b, x => x.Calcite)),
                    ks.Is(Compute.Of(k, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, Truths.Any));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, bc));
                r.Where(RenderingPrimitives.OutcomesWithin.Of(ks, bs));
                r.Then(Refutes.Of(pos, v).Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            // Measured is complete when this stratum runs, so a key is settled once and its answers are all of them.
            tabulate.DefineRule("a settled key is an entry of its set's table", r =>
            {
                var (k, s) = r.Vars<MeasuredKey, RenderingSet>("k", "s");
                var (m, d) = r.Vars<ValueSet<RenderingTarget>, ValueSet<RenderingTarget>>("m", "d");
                var (v, i, mn) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>, AnswerMeanings>("v", "i", "mn");
                r.When(m.Is(Measured.Of(k, s)), d.Is(Serves.Of(s)), v.Is(Answers.Of(k, s)), i.Is(Evidence.Of(k, s)), mn.Is(Means.Of()));
                r.Where(Primitives.IsSubsetOf<RenderingTarget>().Of(d, m));
                r.Then(Table.Of(s).Is(Primitives.Singleton<MeasuredEntry>().Of(Compute.Of(k, v, i, mn, MeasuredEntry.Of))), Final.Of(k, s).Is(v));
            });
        }

        // A connective composes in a set where every key Kleene's logic expects of the set's targets is settled there as
        // Kleene's; Calcite composes only where its own rows hold too, which every composition reads beside the set's.
        void DefineLicences(Ruleset expected, Ruleset matched, Ruleset licence)
        {
            expected.DefineRule("every Kleene cell of a connective is expected of a set that serves a target it is Kleene's for", r =>
            {
                var (k, y, s, x) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "y", "s", "x");
                var d = r.Var<ValueSet<RenderingTarget>>("d");
                r.When(x.Is(Kleene.Of(k, y)), d.Is(Serves.Of(s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, y));
                r.Then(Expected.Of(Compute.Of(k, w => w.Test), s).Is(Primitives.Singleton<MeasuredKey>().Of(k)));
            });

            // A settled answer is never empty, so one within Kleene's single answer is that answer.
            matched.DefineRule("a settled answer that is Kleene's matches", r =>
            {
                var (k, y, s, x) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "y", "s", "x");
                var (d, v) = r.Vars<ValueSet<RenderingTarget>, ValueSet<CosmosAnswer>>("d", "v");
                r.When(v.Is(Final.Of(k, s)), x.Is(Kleene.Of(k, y)), d.Is(Serves.Of(s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, y));
                r.Where(Primitives.IsSubsetOf<CosmosAnswer>().Of(v, Primitives.Singleton<CosmosAnswer>().Of(x)));
                r.Then(Matched.Of(Compute.Of(k, w => w.Test), s).Is(Primitives.Singleton<MeasuredKey>().Of(k)));
            });

            licence.DefineRule("a connective every expected cell of which matched composes in the set", r =>
            {
                var (t, s) = r.Vars<CosmosServiceTest, RenderingSet>("t", "s");
                var (m, n) = r.Vars<ValueSet<MeasuredKey>, ValueSet<MeasuredKey>>("m", "n");
                r.When(m.Is(Matched.Of(t, s)), n.Is(Expected.Of(t, s)));
                r.Where(Primitives.IsSubsetOf<MeasuredKey>().Of(n, m));
                r.Then(Licensed.Of(s).Is(Primitives.Singleton<CosmosServiceTest>().Of(t)));
            });
        }

        // Positions and pairings are derived, never carried: every arm of a junction lying within a form is proposed each
        // part of it, so the gate decides which pairing holds, and the junction's own forms are composed up from what its
        // arms agreed with (design 2, DESIGN.md); a negation's operand takes the form stripped of a NOT at the opposite
        // position.
        void DefineDescent(Ruleset structure, Ruleset descend, Ruleset disjoint)
        {
            structure.DefineRule("a conjunction's arms are its junction's sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(Ir.And.Of(a, b)));
                r.Then(Junction.Of(e, a, b, CosmosServiceTest.And), Junction.Of(e, b, a, CosmosServiceTest.And), First.Of(e, a));
            });

            structure.DefineRule("a disjunction's arms are its junction's sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(Ir.Or.Of(a, b)));
                r.Then(Junction.Of(e, a, b, CosmosServiceTest.Or), Junction.Of(e, b, a, CosmosServiceTest.Or), First.Of(e, a));
            });

            // A form of a junction's own connective is what the junction and every junction of that connective beneath it lie
            // within; each arm of them is offered each part of it, and the compose rules build the junctions' forms up from
            // the parts their arms agree with or are refuted on, never a subset of it no arm has taken.
            descend.DefineRule("a junction lies within each form of its own connective it is a candidate for", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, f) = r.Vars<RenderPosition, CosmosServiceTest, ServiceForm>("pos", "j", "f");
                r.When(Candidate.Of(e, pos, f), Junction.Of(e, x, y, j));
                r.Where(RenderingPrimitives.FormHeads.Of(f, j));
                r.Then(Within.Of(e, pos, f), Needs.Of(f).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            descend.DefineRule("an arm of a junction that is a junction of its connective lies within what the junction lies within", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (u, w) = r.Vars<Pred, Pred>("u", "w");
                var (pos, j, f) = r.Vars<RenderPosition, CosmosServiceTest, ServiceForm>("pos", "j", "f");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), Junction.Of(y, u, w, j));
                r.Then(Within.Of(y, pos, f));
            });

            descend.DefineRule("each arm of a junction takes each part of a form it lies within", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), Member.Of(f, g));
                r.Then(Candidate.Of(x, pos, g));
            });

            // A conjunct written twice, or with a member beside it that reads its path alone, is one atom's: offered the
            // junction of every part reading that path, the atom evaluates them together on its cells.
            descend.DefineRule("an atom of a junction takes the part of a form it lies within that reads its path alone", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, f) = r.Vars<RenderPosition, CosmosServiceTest, ServiceForm>("pos", "j", "f");
                var (p, a, n) = r.Vars<JPath, PredicateAtom, string>("p", "a", "n");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), Ir.Atom.Of(x, p, a), n.Is(Spelling.Of(p)));
                r.Then(Candidate.Of(x, pos, Compute.Of(f, n, ServiceForm.Reading)));
            });

            descend.DefineRule("a negation's operand is a candidate for the form's operand at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, g) = r.Vars<RenderPosition, RenderPosition, ServiceForm>("pos", "q", "g");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q));
                r.Then(Candidate.Of(a, q, Compute.Of(g, ServiceForm.Strip)));
            });

            // A member of a member of the junction's own connective is a part an arm may take, the licensed connective being
            // associative: the reader writes members as written, and this is the regrouping.
            descend.DefineRule("a member of a member of a junction's own connective is a member of the junction", r =>
            {
                var (f, g, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("f", "g", "h");
                r.When(Member.Of(f, g), Member.Of(g, h));
                r.Where(RenderingPrimitives.FormJoins.Of(f, g));
                r.Then(Member.Of(f, h));
            });

            // Never an element against an index, nor a path against one beneath it: those may be one place.
            disjoint.DefineRule("two properties of one parent with different names are disjoint locations", r =>
            {
                var (p, q, u) = r.Vars<JPath, JPath, JPath>("p", "q", "u");
                var (n, m) = r.Vars<string, string>("n", "m");
                r.When(p.Is(Paths.Prop.Of(u, n)), q.Is(Paths.Prop.Of(u, m)), n.IsNot(m));
                r.Then(Disjoint.Of(p, q));
            });
        }

        void DefineCells(RenderingDefinition definition, Ruleset partition, Ruleset need, Ruleset parted)
        {
            partition.DefineRule("an atom's import is cut into cells, at its literals, the form's and the edges its reader names, where the form reads that path alone", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, x, k, y) = r.Vars<RenderPosition, PathValues, ValueSet<string>, PathValues>("pos", "x", "k", "y");
                var (n, rd, g) = r.Vars<string, string, ReaderEdges>("n", "rd", "g");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), x.Is(Imported.Of(p)), k.Is(ImportedChars.Of(p)), y.Is(Assumed.Of(e)), n.Is(Spelling.Of(p)),
                    rd.Is(Compute.Of(a, w => w.Reader)), g.Is(Edges.Of(rd)));
                r.Where(Primitives.IsSubsetOf<string>().Of(Compute.Of(f, w => w.Paths), Primitives.Singleton<string>().Of(n)));
                var cuts = Compute.Of(a, f, g, definition.Cut);
                r.Then(Partition.Of(e, f).Is(Compute.Of(Compute.Of(n, x, k, cuts, definition.Split), y, k, cuts, definition.SetAside)));
            });

            need.DefineRule("a candidate of an atom is evaluated on the atom's cells", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z) = r.Vars<RenderPosition, CellPartition>("pos", "z");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)));
                r.Then(Need.Of(z, f), Needs.Of(f).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            // Every value a body computes is total: body calls are unordered, so a filter beside one guards nothing. A
            // leaf is its own part, and none of these derives anything from it.
            need.DefineRule("a compound that is evaluated is evaluated through its parts", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("g", "h1", "h2");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)));
                r.Then(Need.Of(z, h1), Need.Of(z, h2), Needs.Of(h1).Is(ValueSet<CosmosServiceTest>.Empty), Needs.Of(h2).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            parted.DefineRule("a string literal parts the orders over a path whose strings may hold the other band", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                var (k, b) = r.Vars<ValueSet<string>, ValueSet<string>>("k", "b");
                var (u, w, l) = r.Vars<string, string, string>("u", "w", "l");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), k.Is(ImportedChars.Of(p)), Parting.Of(u, w));
                r.Where(RenderingPrimitives.StringComparison.Of(g));
                r.When(l.Is(Compute.Of(g, x => x.Literal ?? "")), b.Is(Compute.Of(l, x => JsonValues.CharactersOf(x))));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Where(Primitives.Contains<string>().Of(b, w));
                r.Then(PartedLiterals.Of(p).Is(Primitives.Singleton<string>().Of(l)));
            });

            // The service may answer two bounds of one path as a range, not each bound, where they part (N8). What it is
            // asked is the conjunction as written, so its own orderings are read, not the IR's.
            parted.DefineRule("two string orderings of one path in a written conjunction, one of whose literals parts the orders there, need Range", r =>
            {
                var (f, h1, h2, p) = r.Vars<ServiceForm, ServiceForm, ServiceForm, JPath>("f", "h1", "h2", "p");
                var (k, b) = r.Vars<ValueSet<string>, ValueSet<string>>("k", "b");
                var (n, u, w, l) = r.Vars<string, string, string, string>("n", "u", "w", "l");
                r.When(Conjunct.Of(f, h1), Conjunct.Of(f, h2), h1.IsNot(h2), n.Is(Compute.Of(h1, x => x.Path ?? "")), n.Is(Compute.Of(h2, x => x.Path ?? "")),
                    n.Is(Spelling.Of(p)), k.Is(ImportedChars.Of(p)), Parting.Of(u, w), l.Is(Compute.Of(h1, x => x.Literal ?? "")), b.Is(Compute.Of(l, x => JsonValues.CharactersOf(x))));
                r.Where(RenderingPrimitives.StringOrdering.Of(h1));
                r.Where(RenderingPrimitives.StringOrdering.Of(h2));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Where(Primitives.Contains<string>().Of(b, w));
                r.Then(Needs.Of(f).Is(Primitives.Singleton<CosmosServiceTest>().Of(CosmosServiceTest.Range)));
            });
        }

        void DefineEvaluation(RenderingDefinition definition, Ruleset evaluate)
        {
            // Which literals part the orders is complete here, an earlier stratum's: a literal not among them is read as not.
            evaluate.DefineRule("a test is evaluated on every cell by the rows", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, s) = r.Vars<ServiceForm, ServiceForm, RenderingSet>("f", "g", "s");
                var (q, t) = r.Vars<ValueSet<string>, ValueSet<MeasuredEntry>>("q", "t");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), q.Is(PartedLiterals.Of(p)), Under.Of(s), t.Is(Table.Of(s)));
                r.Where(RenderingPrimitives.FormIsLeaf.Of(g));
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, z, Compute.Of(t, ServiceTable.Of), q, definition.Leaf)));
            });

            // A junction of three members or more is evaluated as its first and a regrouped rest rid of copies, which only
            // the set's licence makes the junction's own value; two members read their row as written.
            evaluate.DefineRule("a compound is evaluated from its parts by its connective's rows", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2, s) = r.Vars<ServiceForm, ServiceForm, ServiceForm, RenderingSet>("g", "h1", "h2", "s");
                var (v, w, t) = r.Vars<OutcomeVector, OutcomeVector, ValueSet<MeasuredEntry>>("v", "w", "t");
                var (lic, nd) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>>("lic", "nd");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)), v.Is(Svc.Of(z, h1)), w.Is(Svc.Of(z, h2)),
                    Under.Of(s), t.Is(Table.Of(s)), lic.Is(Licensed.Of(s)), nd.Is(Needs.Of(g)));
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(RenderingPrimitives.FormRegroups.Of(g), lic));
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(nd, lic));
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, v, w, Compute.Of(t, ServiceTable.Of), definition.Compound)));
            });
        }

        void DefineGate(RenderingDefinition definition, Ruleset classes, Ruleset gate, Ruleset conflict)
        {
            classes.DefineRule("Calcite's atom and the form are paired on every cell", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z, v) = r.Vars<RenderPosition, CellPartition, OutcomeVector>("pos", "z", "v");
                var (mn, tol) = r.Vars<AnswerMeanings, Truths>("mn", "tol");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)), v.Is(Svc.Of(z, f)), mn.Is(Means.Of()), tol.Is(Tolerated.Of()));
                r.Then(Outcomes.Of(e, f).Is(Compute.Of(a, v, mn, tol, definition.Pair)));
            });

            // The universal over cells: the cells' classes are complete (lattice-free), and the allowed ones the theory's.
            gate.DefineRule("a candidate agrees where every class of its cells is allowed at its position", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (o, w) = r.Vars<CellOutcomes, ValueSet<string>>("o", "w");
                var (kk, y, ro) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "ro");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Allowed.Of(pos, v)),
                    w.Is(AllowedCites.Of(pos, v)), ro.Is(RaiseOnly.Of()));
                r.Where(Primitives.IsSubsetOf<CellClass>().Of(kk, y));
                r.Then(Agrees.Of(e, pos, f, v).Is(Compute.Of(o, w, Proof.Of)), Vacuous.Of(e, pos, f, v).Is(Primitives.IsSubsetOf<CellClass>().Of(kk, ro)));
            });

            gate.DefineRule("a candidate is refuted where a class of its cells is a definite counterexample", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (o, p, a) = r.Vars<CellOutcomes, JPath, PredicateAtom>("o", "p", "a");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), Ir.Atom.Of(e, p, a), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Refutes.Of(pos, v)),
                    m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
                r.Where(RenderingPrimitives.SetNonempty.Of(m));
                r.Then(Disagrees.Of(e, pos, f, v).Is(Compute.Of(o, m, Refutation.Of)), RefutedAt.Of(Compute.Of(Compute.Of(o, m, Refutation.Of), x => x.Cells), p));
            });

            gate.DefineRule("an atom is a connective's identity on some cell", r =>
            {
                var (e, f, j) = r.Vars<Pred, ServiceForm, CosmosServiceTest>("e", "f", "j");
                var o = r.Var<CellOutcomes>("o");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                r.When(o.Is(Outcomes.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Identity.Of(j)), m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
                r.Where(RenderingPrimitives.SetNonempty.Of(m));
                r.Then(Neutral.Of(e, f, j).Is(Compute.Of(o, y, Witness.Of)));
            });

            conflict.DefineRule("an agreement and a refutation of one candidate are a contradiction", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                r.When(Fact.Exists(Agrees.Of(e, pos, f, v)), Fact.Exists(Disagrees.Of(e, pos, f, v)));
                r.Then(Conflict.Of(e, pos, f, v), Graph.Contradiction.Of());
            });
        }

        // Each step holds by the identities the licence makes Kleene's on both sides. A node may hold candidates from more
        // than one parent, so the connective is checked where the step is taken, not only where the sides were offered.
        void DefineComposition(Ruleset compose)
        {
            // The junction's form is the part of the form it lies within that its sides make, so it is built from what agreed,
            // and held to exactly their join; the need checked is the written form's, the one the service is asked.
            compose.DefineRule("a junction agrees where both sides agree on one verdict, as the part of a form it lies within that they make", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, v) = r.Vars<RenderPosition, CosmosServiceTest, Verdict>("pos", "j", "v");
                var (f, l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("f", "l", "m", "h");
                var (pl, pr) = r.Vars<Proof, Proof>("pl", "pr");
                var (vl, vr) = r.Vars<bool, bool>("vl", "vr");
                r.When(Within.Of(e, pos, f), First.Of(e, x), Junction.Of(e, x, y, j), pl.Is(Agrees.Of(x, pos, l, v)), pr.Is(Agrees.Of(y, pos, m, v)),
                    vl.Is(Vacuous.Of(x, pos, l, v)), vr.Is(Vacuous.Of(y, pos, m, v)), h.Is(Compute.Of(f, l, m, ServiceForm.Spanned)));
                r.Where(RenderingPrimitives.FormDecomposes.Of(h, l, m));
                Composes(r, j, f);
                r.Then(Agrees.Of(e, pos, h, v).Is(Compute.Of(pl, pr, Proof.Join)), Vacuous.Of(e, pos, h, v).Is(vl), Vacuous.Of(e, pos, h, v).Is(vr));
            });

            compose.DefineRule("a negation agrees where its operand agrees at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, v) = r.Vars<RenderPosition, RenderPosition, Verdict>("pos", "q", "v");
                var (g, h, p) = r.Vars<ServiceForm, ServiceForm, Proof>("g", "h", "p");
                var vp = r.Var<bool>("vp");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), h.Is(Compute.Of(g, ServiceForm.Strip)), p.Is(Agrees.Of(a, q, h, v)), vp.Is(Vacuous.Of(a, q, h, v)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Agrees.Of(e, pos, g, v).Is(p), Vacuous.Of(e, pos, g, v).Is(vp));
            });

            compose.DefineRule("a junction is refuted where one side is refuted and the other is its identity on another location, as the part of a form it lies within that they make", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, v) = r.Vars<RenderPosition, CosmosServiceTest, Verdict>("pos", "j", "v");
                var (f, l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("f", "l", "m", "h");
                var (z, w, c) = r.Vars<Refutation, Witness, ValueSet<string>>("z", "w", "c");
                var (p1, p2, a2) = r.Vars<JPath, JPath, PredicateAtom>("p1", "p2", "a2");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), z.Is(Disagrees.Of(x, pos, l, v)), w.Is(Neutral.Of(y, m, j)),
                    c.Is(Compute.Of(z, t => t.Cells)), RefutedAt.Of(c, p1), Ir.Atom.Of(y, p2, a2), Disjoint.Of(p1, p2), h.Is(Compute.Of(f, l, m, ServiceForm.Spanned)));
                r.Where(RenderingPrimitives.FormDecomposes.Of(h, l, m));
                Composes(r, j, f);
                r.Then(Disagrees.Of(e, pos, h, v).Is(Compute.Of(z, w, Refutation.With)));
            });

            compose.DefineRule("a negation is refuted where its operand is refuted at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, v) = r.Vars<RenderPosition, RenderPosition, Verdict>("pos", "q", "v");
                var (g, h, z) = r.Vars<ServiceForm, ServiceForm, Refutation>("g", "h", "z");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), h.Is(Compute.Of(g, ServiceForm.Strip)), z.Is(Disagrees.Of(a, q, h, v)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Disagrees.Of(e, pos, g, v).Is(Compute.Of(z, Refutation.Negated)));
            });
        }

        // Positions are derived down the IR, never carried; each node they reach starts with no exact form and nothing
        // implied, complete values the later strata only lower or grow. A template only proposes: the gate disposes.
        void DefineProposal(Ruleset at, Ruleset spell)
        {
            at.DefineRule("a junction's arms stand where it stands", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                r.When(At.Of(e, pos), Junction.Of(e, x, y, j));
                r.Then(At.Of(x, pos), BestExact.Of(x, pos).Is(Priced.None), Implied.Of(x, pos).Is(ValueSet<ServiceForm>.Empty));
            });

            at.DefineRule("a negation's operand stands at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q) = r.Vars<RenderPosition, RenderPosition>("pos", "q");
                r.When(At.Of(e, pos), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q));
                r.Then(At.Of(a, q), BestExact.Of(a, q).Is(Priced.None), Implied.Of(a, q).Is(ValueSet<ServiceForm>.Empty));
            });

            spell.DefineRule("a template that fits an atom proposes its form at the atom's position", r =>
            {
                var (e, p, a, pos) = r.Vars<Pred, JPath, PredicateAtom, RenderPosition>("e", "p", "a", "pos");
                var (t, n, sh) = r.Vars<TemplateId, string, string>("t", "n", "sh");
                r.When(At.Of(e, pos), Ir.Atom.Of(e, p, a), n.Is(Spelling.Of(p)), Template.Of(t, pos), sh.Is(Compute.Of(a, RenderingTemplates.ShapeOf)), Fits.Of(t, sh));
                r.Then(Candidate.Of(e, pos, Compute.Of(t, pos, a, n, RenderingTemplates.Spell)));
            });
        }

        // The gate's agreements, complete here, lifted where they rest on something and may be sent: an exact one offered at
        // its price, an implied one the Rex builder writes as a member. Presence through calls is three rules over rows.
        void DefineLifts(Ruleset lift, Ruleset strict)
        {
            lift.DefineRule("an exact agreement that rests on something and may be sent is a best candidate", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var b = r.Var<ValueSet<string>>("b");
                r.When(At.Of(e, pos), Fact.Exists(Agrees.Of(e, pos, f, Verdict.Equal)), Vacuous.Of(e, pos, f, Verdict.Equal).Is(false), b.Is(PartingBands.Of()));
                r.Where(RenderingPrimitives.Sendable.Of(f, b));
                r.Then(BestExact.Of(e, pos).Is(Compute.Of(f, Priced.Of)));
            });

            lift.DefineRule("an implied agreement that rests on something, may be sent and can be written as a Rex is implied", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var (p, a) = r.Vars<JPath, PredicateAtom>("p", "a");
                var (b, g) = r.Vars<ValueSet<string>, RenderingRexGrammar.Value>("b", "g");
                r.When(At.Of(e, pos), Fact.Exists(Agrees.Of(e, pos, f, Verdict.Implies)), Vacuous.Of(e, pos, f, Verdict.Implies).Is(false), Ir.Atom.Of(e, p, a),
                    b.Is(PartingBands.Of()), g.Is(RexGrammar.Of()));
                r.Where(RenderingPrimitives.Sendable.Of(f, b));
                r.Where(RenderingPrimitives.RexWritable.Of(f, a, g));
                r.Then(Implied.Of(e, pos).Is(Primitives.Singleton<ServiceForm>().Of(f)));
            });

            strict.DefineRule("a reading Calcite answers null over an absent path is strict in its path", r =>
            {
                var (v, p, k) = r.Vars<Val, JPath, string>("v", "p", "k");
                r.When(Ir.Reads.Of(v, p, k), StrictReader.Of(k));
                r.Then(Strict.Of(v, p));
            });

            strict.DefineRule("a call whose operator answers null for a null operand is strict in what an operand is strict in", r =>
            {
                var (c, v, p) = r.Vars<Val, Val, JPath>("c", "v", "p");
                var (name, args) = r.Vars<string, Args>("name", "args");
                r.When(c.Is(Ir.Call.Of(name, args)), StrictOp.Of(name), CallArg.Of(c, v), Strict.Of(v, p));
                r.Then(Strict.Of(c, p));
            });

            // Null where the path is absent, so neither TRUE nor FALSE there: present wherever it is kept, at either position.
            strict.DefineRule("a predicate over a strict value implies its path present", r =>
            {
                var (e, pos, v, p) = r.Vars<Pred, RenderPosition, Val, JPath>("e", "pos", "v", "p");
                var n = r.Var<string>("n");
                r.When(At.Of(e, pos), Over.Of(e, v), Strict.Of(v, p), n.Is(Spelling.Of(p)));
                r.Then(Implied.Of(e, pos).Is(Primitives.Singleton<ServiceForm>().Of(Compute.Of(n, pos, ServiceForm.Presence))));
            });
        }

        // An exact form composes up from the arms' bests under the licence, held to exactly their join; a Min value only
        // lowers and priced-is-real only turns true, so whatever it composed from is exact. The weakening reads every best
        // and implied set complete: a leaf takes its best where that is a member, or what its members make together.
        void DefineChoice(Ruleset best, Ruleset weaken)
        {
            best.DefineRule("a junction's best is the join of its arms' bests", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (bx, by) = r.Vars<Priced, Priced>("bx", "by");
                var (l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("l", "m", "h");
                var (px, py) = r.Vars<Proof, Proof>("px", "py");
                r.When(At.Of(e, pos), First.Of(e, x), Junction.Of(e, x, y, j), bx.Is(BestExact.Of(x, pos)), by.Is(BestExact.Of(y, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bx));
                r.Where(RenderingPrimitives.PricedIsReal.Of(by));
                r.When(l.Is(Compute.Of(bx, Priced.FormOf)), m.Is(Compute.Of(by, Priced.FormOf)), px.Is(Agrees.Of(x, pos, l, Verdict.Equal)), py.Is(Agrees.Of(y, pos, m, Verdict.Equal)),
                    h.Is(Compute.Of(j, l, m, ServiceForm.Joined)));
                r.Where(RenderingPrimitives.FormIsJoinOf.Of(h, l, m));
                Composes(r, j, null);
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(px, py, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            best.DefineRule("a negation's best is its operand's best at the opposite position, negated", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, bq) = r.Vars<RenderPosition, RenderPosition, Priced>("pos", "q", "bq");
                var (g, h, pa) = r.Vars<ServiceForm, ServiceForm, Proof>("g", "h", "pa");
                r.When(At.Of(e, pos), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), bq.Is(BestExact.Of(a, q)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bq));
                r.When(g.Is(Compute.Of(bq, Priced.FormOf)), pa.Is(Agrees.Of(a, q, g, Verdict.Equal)), h.Is(Compute.Of(g, ServiceForm.Strip)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(pa), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            weaken.DefineRule("a leaf whose exact form can be written as a Rex weakens to it", r =>
            {
                var (e, pos) = r.Vars<Pred, RenderPosition>("e", "pos");
                var (b, s) = r.Vars<Priced, ValueSet<ServiceForm>>("b", "s");
                r.When(At.Of(e, pos), Leaf.Of(e), b.Is(BestExact.Of(e, pos)), s.Is(Implied.Of(e, pos)));
                r.Where(RenderingPrimitives.BestIsImplied.Of(b, s));
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(b, Priced.FormOf)));
            });

            weaken.DefineRule("any other leaf weakens to what its implied forms make together", r =>
            {
                var (e, pos) = r.Vars<Pred, RenderPosition>("e", "pos");
                var (b, s) = r.Vars<Priced, ValueSet<ServiceForm>>("b", "s");
                r.When(At.Of(e, pos), Leaf.Of(e), b.Is(BestExact.Of(e, pos)), s.Is(Implied.Of(e, pos)));
                r.Where(RenderingPrimitives.BestIsNotImplied.Of(b, s));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or));
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(s, pos, RenderingWeakening.AtLeaf)));
            });

            weaken.DefineRule("a junction's weakening joins its arms' weakenings", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (wx, wy) = r.Vars<ServiceForm, ServiceForm>("wx", "wy");
                r.When(At.Of(e, pos), First.Of(e, x), Junction.Of(e, x, y, j), wx.Is(Weakening.Of(x, pos)), wy.Is(Weakening.Of(y, pos)));
                Composes(r, j, null);
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(j, wx, wy, ServiceForm.Joined)));
            });

            weaken.DefineRule("a negation's weakening is its operand's at the opposite position, negated", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, w) = r.Vars<RenderPosition, RenderPosition, ServiceForm>("pos", "q", "w");
                r.When(At.Of(e, pos), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), w.Is(Weakening.Of(a, q)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(w, ServiceForm.Negated)));
            });
        }

        /// <summary>
        /// Holds a step to a connective the set under which the bundle runs licenses and Calcite does, and a junction's
        /// form to a set that licenses whatever it needs.
        /// </summary>
        void Composes(RuleBuilder r, Term<CosmosServiceTest> connective, Var<ServiceForm>? junction)
        {
            var s = r.Var<RenderingSet>("s");
            var (lic, cal) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>>("lic", "cal");
            r.When(Under.Of(s), lic.Is(Licensed.Of(s)), cal.Is(Licensed.Of(RenderingSet.Calcite)));
            r.Where(Primitives.Contains<CosmosServiceTest>().Of(lic, connective));
            r.Where(Primitives.Contains<CosmosServiceTest>().Of(cal, connective));
            if (junction is null)
                return;

            var nd = r.Var<ValueSet<CosmosServiceTest>>("nd");
            r.When(nd.Is(Needs.Of(junction)));
            r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(nd, lic));
        }

        /// <summary>
        /// Holds a step to every one of the connectives, which the set and Calcite both license: <see cref="Composes"/>'s
        /// reads under names of its own, so the two may stand in one rule.
        /// </summary>
        void ComposesAll(RuleBuilder r, ValueSet<CosmosServiceTest> connectives)
        {
            var sa = r.Var<RenderingSet>("sa");
            var (lica, cala) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>>("lica", "cala");
            r.When(Under.Of(sa), lica.Is(Licensed.Of(sa)), cala.Is(Licensed.Of(RenderingSet.Calcite)));
            r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(connectives, lica));
            r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(connectives, cala));
        }

        /// <summary>
        /// Writes each axiom's rows as one batch, so a contradiction refuses that axiom whole and names it, then the host
        /// rows; settles them, and freezes the theory.
        /// </summary>
        void Load(RenderingDefinition definition)
        {
            foreach (var axiom in definition.Rows.GroupBy(row => row.AxiomId))
            {
                try
                {
                    Theory.Write(w =>
                    {
                        foreach (var row in axiom)
                            w.Assume(Outcome.Of(row.Key, row.Target).Is(row.Answer), Cites.Of(row.Key, row.Target).Is(ValueSet.Of(axiom.Key)));
                    });
                }
                catch (FactConflictException e)
                {
                    e.Data["axiom"] = axiom.Key;
                    e.Data["faulted"] = Theory.IsFaulted;
                    throw;
                }
            }

            Theory.Write(w =>
            {
                // Every set's licence starts empty, so a rule may read a set that licenses nothing.
                foreach (var (set, targets) in definition.Serves)
                    w.Assume(Serves.Of(set).Is(targets), Licensed.Of(set).Is(ValueSet<CosmosServiceTest>.Empty));

                foreach (var k in AgreementRows.Classes)
                    w.Assume(Class.Of(k));

                foreach (var (pos, v, b) in definition.Allowing)
                    w.Assume(Allowing.Of(pos, v, b));

                foreach (var (pos, v, b) in definition.Refuting)
                    w.Assume(Refuting.Of(pos, v, b));

                foreach (var (reader, edges) in definition.Edges)
                    w.Assume(Edges.Of(reader).Is(edges));

                foreach (var (u, v) in definition.Parting)
                    w.Assume(Parting.Of(u, v));

                foreach (var (target, key, answer) in definition.Kleene)
                    w.Assume(Kleene.Of(key, target).Is(answer));

                foreach (var (connective, classes) in definition.Identity)
                    w.Assume(Identity.Of(connective).Is(classes));

                foreach (var (pos, q) in KleeneTables.Opposite)
                    w.Assume(Opposite.Of(pos, q));

                foreach (var (id, pos) in definition.Templates)
                    w.Assume(Template.Of(id, pos));

                foreach (var (id, shape) in definition.Fits)
                    w.Assume(Fits.Of(id, shape));

                foreach (var name in definition.StrictOperators)
                    w.Assume(StrictOp.Of(name));

                foreach (var reader in definition.StrictReaders)
                    w.Assume(StrictReader.Of(reader));

                w.Assume(Means.Of().Is(definition.Means), RaiseOnly.Of().Is(ValueSet<CellClass>.Empty), Tolerated.Of().Is(definition.Tolerated),
                    PartingBands.Of().Is(ValueSet.From(definition.Parting.SelectMany(p => new[] { p.Item1, p.Item2 }))), RexGrammar.Of().Is(RenderingRexGrammar.Written));
            });

            var report = Theory.Run(Settle);
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"The rendering theory stopped {report.StopReason} after {report.Iterations} iterations.");

            Theory.Freeze();
        }

    }

}
