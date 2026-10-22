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
    /// refuted, and carry both through the predicate's AND, OR and NOT.
    /// </summary>
    /// <remarks>
    /// Every fact that is derived or combined is a rule here, and every universal reads complete inputs (<c>DESIGN.md</c>,
    /// <i>The rendering graph in check mode</i>).
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
            Assumed = Graph.DefineFunction<JPath, PathValues>("assumed");
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
            RefutedAt = Graph.DefineRelation<Refutation, JPath>("refutedat");
            Conflict = Graph.DefineRelation<Pred, RenderPosition, ServiceForm, Verdict>("conflict");
            var analysis = new TableOptions { Role = TableRole.Analysis };
            Junction = Graph.DefineRelation<Pred, Pred, Pred, CosmosServiceTest>("junction", analysis);
            First = Graph.DefineRelation<Pred, Pred>("first", analysis);
            Needs = Graph.DefineFunction<ServiceForm, ValueSet<CosmosServiceTest>>("needs", ValueLattice.Union<CosmosServiceTest>());
            Neutral = Graph.DefineFunction<Pred, ServiceForm, CosmosServiceTest, Witness>("neutral", ValueLattice.Min<Witness>());
            Disjoint = Graph.DefineRelation<JPath, JPath>("disjoint");

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

            Settle = Schedule.Sequence(Schedule.Saturate(measure, allowed, refutes, expected), Schedule.Saturate(tabulate), Schedule.Saturate(matched),
                Schedule.Saturate(licence));
            Check = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, structure, descend, disjoint), Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes, gate), Schedule.Saturate(compose, conflict));

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
        // two spellings, and throw), the set, each import once and lattice-free, beside it what the stratum its earliest
        // round was rendered from says the path may hold (`assumed`, the import where none narrows it), and the root's
        // candidate with every member of its junctions as written and each conjunction's members, nested ones flattened;
        // the rules write the rest, the candidates beneath the root and the members of a junction's rest among them.
        public Function<JPath, string> Spelling { get; }
        public Relation<ServiceForm, ServiceForm> Conjunct { get; }
        public Relation<RenderingSet> Under { get; }
        public Function<JPath, PathValues> Imported { get; }
        public Function<JPath, PathValues> Assumed { get; }
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

        /// <summary>The one location a refutation on an atom's cells sets; a junction's sets two, and has none.</summary>
        public Relation<Refutation, JPath> RefutedAt { get; }
        public Relation<Pred, RenderPosition, ServiceForm, Verdict> Conflict { get; }

        /// <summary>A junction of the IR, its connective, and each arm beside the other; <see cref="First"/> names its first.</summary>
        public Relation<Pred, Pred, Pred, CosmosServiceTest> Junction { get; }
        public Relation<Pred, Pred> First { get; }
        public Function<ServiceForm, ValueSet<CosmosServiceTest>> Needs { get; }
        public Function<Pred, ServiceForm, CosmosServiceTest, Witness> Neutral { get; }
        public Relation<JPath, JPath> Disjoint { get; }

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
            values.Register<ServiceForm>("service form");
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

        // Positions and pairings are derived, never carried: a junction's first arm is proposed each member of its form and
        // its second the rest, so the gate decides which pairing holds; a negation's operand takes the form stripped of a
        // NOT at the opposite position.
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

            descend.DefineRule("a junction's first arm takes each member of its form, and its second the rest", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                r.When(Candidate.Of(e, pos, f), First.Of(e, x), Junction.Of(e, x, y, j), Member.Of(f, g));
                r.Where(RenderingPrimitives.FormHeads.Of(f, j));
                r.Then(Candidate.Of(x, pos, g), Candidate.Of(y, pos, Compute.Of(f, g, ServiceForm.Without)), Needs.Of(f).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            descend.DefineRule("a negation's operand is a candidate for the form's operand at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, g) = r.Vars<RenderPosition, RenderPosition, ServiceForm>("pos", "q", "g");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q));
                r.Then(Candidate.Of(a, q, Compute.Of(g, ServiceForm.Strip)));
            });

            // Members are counted as the relation holds them, once a text, nested ones of the junction's connective in place:
            // with three, the rest without one is a junction of the others; with two, it is the other, whose members are its own.
            descend.DefineRule("the rest of a junction's form has every member it was not stripped of", r =>
            {
                var (e, x, n) = r.Vars<Pred, Pred, long>("e", "x", "n");
                var pos = r.Var<RenderPosition>("pos");
                var (f, g, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("f", "g", "h");
                r.When(Candidate.Of(e, pos, f), First.Of(e, x), Member.Of(f, g), Member.Of(f, h), h.IsNot(g), n.Is(Compute.Of(f, w => (long)ValueSet.From(w.Flat).Count)));
                r.Where(Primitives.Greater.Of(n, 2L));
                r.Then(Member.Of(Compute.Of(f, g, ServiceForm.Without), h));
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
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), x.Is(Imported.Of(p)), k.Is(ImportedChars.Of(p)), y.Is(Assumed.Of(p)), n.Is(Spelling.Of(p)),
                    rd.Is(Compute.Of(a, w => w.Reader)), g.Is(Edges.Of(rd)));
                r.Where(Primitives.IsSubsetOf<string>().Of(Compute.Of(f, w => w.Paths), Primitives.Singleton<string>().Of(n)));
                r.Then(Partition.Of(e, f).Is(Compute.Of(Compute.Of(n, x, k, Compute.Of(a, f, g, definition.Cut), definition.Split), y, definition.Within)));
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
                var mn = r.Var<AnswerMeanings>("mn");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)), v.Is(Svc.Of(z, f)), mn.Is(Means.Of()));
                r.Then(Outcomes.Of(e, f).Is(Compute.Of(a, v, mn, definition.Pair)));
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
                r.Then(Disagrees.Of(e, pos, f, v).Is(Compute.Of(o, m, Refutation.Of)), RefutedAt.Of(Compute.Of(o, m, Refutation.Of), p));
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
            compose.DefineRule("a junction agrees where both sides agree on one verdict and its form is exactly their join", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, v) = r.Vars<RenderPosition, CosmosServiceTest, Verdict>("pos", "j", "v");
                var (f, l, m) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("f", "l", "m");
                var (pl, pr) = r.Vars<Proof, Proof>("pl", "pr");
                var (vl, vr) = r.Vars<bool, bool>("vl", "vr");
                r.When(Candidate.Of(e, pos, f), First.Of(e, x), Junction.Of(e, x, y, j), pl.Is(Agrees.Of(x, pos, l, v)), pr.Is(Agrees.Of(y, pos, m, v)),
                    vl.Is(Vacuous.Of(x, pos, l, v)), vr.Is(Vacuous.Of(y, pos, m, v)));
                r.Where(RenderingPrimitives.FormHeads.Of(f, j));
                r.Where(RenderingPrimitives.FormDecomposes.Of(f, l, m));
                Composes(r, j, f);
                r.Then(Agrees.Of(e, pos, f, v).Is(Compute.Of(pl, pr, Proof.Join)), Vacuous.Of(e, pos, f, v).Is(vl), Vacuous.Of(e, pos, f, v).Is(vr));
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

            compose.DefineRule("a junction is refuted where one side is refuted and the other is its identity on another location", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, v) = r.Vars<RenderPosition, CosmosServiceTest, Verdict>("pos", "j", "v");
                var (f, l, m) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("f", "l", "m");
                var (z, w) = r.Vars<Refutation, Witness>("z", "w");
                var (p1, p2, a2) = r.Vars<JPath, JPath, PredicateAtom>("p1", "p2", "a2");
                r.When(Candidate.Of(e, pos, f), Junction.Of(e, x, y, j), z.Is(Disagrees.Of(x, pos, l, v)), w.Is(Neutral.Of(y, m, j)),
                    RefutedAt.Of(z, p1), Ir.Atom.Of(y, p2, a2), Disjoint.Of(p1, p2));
                r.Where(RenderingPrimitives.FormHeads.Of(f, j));
                r.Where(RenderingPrimitives.FormDecomposes.Of(f, l, m));
                Composes(r, j, f);
                r.Then(Disagrees.Of(e, pos, f, v).Is(Compute.Of(z, w, Refutation.With)));
            });

            compose.DefineRule("a negation is refuted where its operand is refuted at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, v) = r.Vars<RenderPosition, RenderPosition, Verdict>("pos", "q", "v");
                var (g, h, z) = r.Vars<ServiceForm, ServiceForm, Refutation>("g", "h", "z");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), h.Is(Compute.Of(g, ServiceForm.Strip)), z.Is(Disagrees.Of(a, q, h, v)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Disagrees.Of(e, pos, g, v).Is(z));
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

                w.Assume(Means.Of().Is(definition.Means), RaiseOnly.Of().Is(ValueSet<CellClass>.Empty), Tolerated.Of().Is(definition.Tolerated));
            });

            var report = Theory.Run(Settle);
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"The rendering theory stopped {report.StopReason} after {report.Iterations} iterations.");

            Theory.Freeze();
        }

    }

}
