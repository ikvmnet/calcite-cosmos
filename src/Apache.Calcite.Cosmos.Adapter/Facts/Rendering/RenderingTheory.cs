using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The rendering graph S: what the service answers, settled per set of targets, and the agreement, loaded into a frozen
    /// theory; and the rules that, in a bundle beneath it, evaluate a written form beside the predicate it was written
    /// from on every cell of its path and decide whether it agrees or is refuted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every fact that is derived or combined is a rule here.</b> C# reads rows in and evaluates one atom, one test or
    /// one connective on concrete cells (decision D1); which cuts, which literals part the orders, which subforms are
    /// evaluated, which classes are allowed or refute, the universal over cells and the verdicts are rules.
    /// </para>
    /// <para>
    /// <b>A universal reads complete inputs.</b> Each stratum's universals (<c>set-subset</c>) read tables that are
    /// lattice-free, the host's, the theory's, or written only in an earlier stratum, so a verdict never fires over a set
    /// still growing; the order test reads every rule and holds them to it. A table the evaluation fills is lattice-free,
    /// so a primitive that answers one key two ways faults the bundle rather than meeting silently.
    /// </para>
    /// </remarks>
    internal sealed class RenderingTheory
    {

        readonly Function<string> _version;

        RenderingTheory(RenderingDefinition definition)
        {
            Graph = new EGraph();
            Paths = new PathVocabulary(Graph);
            Ir = new PredicateVocabulary(Graph, Paths);
            Register(Graph.Values);

            Outcome = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingTarget, CosmosAnswer>("outcome");
            Cites = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingTarget, ValueSet<string>>("cites", ValueLattice.Union<string>());
            Serves = Graph.DefineFunction<RenderingSet, ValueSet<RenderingTarget>>("serves");
            Means = Graph.DefineFunction<AnswerMeanings>("means");
            Answers = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<CosmosAnswer>>("answers", ValueLattice.Union<CosmosAnswer>());
            Measured = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<RenderingTarget>>("measured", ValueLattice.Union<RenderingTarget>());
            Evidence = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<string>>("evidence", ValueLattice.Union<string>());
            Final = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<CosmosAnswer>>("final");
            Table = Graph.DefineFunction<RenderingSet, ValueSet<MeasuredEntry>>("table", ValueLattice.Union<MeasuredEntry>());
            Class = Graph.DefineRelation<CellClass>("class");
            RaiseOnly = Graph.DefineFunction<ValueSet<CellClass>>("raiseonly");
            Allowing = Graph.DefineRelation<RenderPosition, Verdict, Box>("allowing");
            Refuting = Graph.DefineRelation<RenderPosition, Verdict, Box>("refuting");
            Tolerated = Graph.DefineFunction<Truths>("tolerated");
            Allowed = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<CellClass>>("allowed", ValueLattice.Union<CellClass>());
            AllowedCites = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<string>>("allowedcites", ValueLattice.Union<string>());
            Refutes = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<CellClass>>("refutes", ValueLattice.Union<CellClass>());
            Edges = Graph.DefineFunction<string, ReaderEdges>("edges");
            Parting = Graph.DefineRelation<string, string>("parting");
            _version = Graph.DefineFunction<string>("version");

            Spelling = Graph.DefineFunction<JPath, string>("spelling");
            Arm = Graph.DefineRelation<Pred, Pred>("arm");
            Under = Graph.DefineRelation<RenderingSet>("under");
            Imported = Graph.DefineFunction<JPath, PathValues>("imported");
            ImportedChars = Graph.DefineFunction<JPath, ValueSet<string>>("importedchars");
            PartedLiterals = Graph.DefineFunction<JPath, ValueSet<string>>("partedliterals", ValueLattice.Union<string>());
            Candidate = Graph.DefineRelation<Pred, RenderPosition, ServiceForm>("candidate");
            Member = Graph.DefineRelation<ServiceForm, ServiceForm>("member");
            Cuts = Graph.DefineFunction<Pred, ServiceForm, ValueSet<CutPoint>>("cuts");
            Partition = Graph.DefineFunction<Pred, ServiceForm, CellPartition>("partition");
            Need = Graph.DefineRelation<CellPartition, ServiceForm>("need");
            Svc = Graph.DefineFunction<CellPartition, ServiceForm, OutcomeVector>("svc");
            Outcomes = Graph.DefineFunction<Pred, ServiceForm, CellOutcomes>("outcomes");
            Agrees = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, Proof>("agrees", ValueLattice.Min<Proof>());
            Disagrees = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, Refutation>("disagrees", ValueLattice.Min<Refutation>());
            Conflict = Graph.DefineRelation<Pred, RenderPosition, ServiceForm, Verdict>("conflict");

            var measure = Graph.DefineRuleset("measure");
            var allowed = Graph.DefineRuleset("allowed");
            var refutes = Graph.DefineRuleset("refutes");
            var tabulate = Graph.DefineRuleset("tabulate");
            DefineTheory(measure, allowed, refutes, tabulate);

            var cuts = Graph.DefineRuleset("cuts");
            var partition = Graph.DefineRuleset("partition");
            var need = Graph.DefineRuleset("need");
            var parted = Graph.DefineRuleset("parted");
            var evaluate = Graph.DefineRuleset("evaluate");
            var classes = Graph.DefineRuleset("classes");
            var gate = Graph.DefineRuleset("gate");
            var conflict = Graph.DefineRuleset("conflict");
            DefineCells(definition, cuts, partition, need, parted);
            DefineEvaluation(definition, evaluate);
            DefineGate(definition, classes, gate, conflict);

            Settle = Schedule.Sequence(Schedule.Saturate(measure, allowed, refutes), Schedule.Saturate(tabulate));
            Check = Schedule.Sequence(Schedule.Saturate(Ir.Analyse), Schedule.Saturate(cuts, partition, need, parted), Schedule.Saturate(evaluate),
                Schedule.Saturate(classes, gate, conflict));

            Version = Digest(definition);
            Graph.Root.Freeze();
            Theory = Graph.Root.CreateChild();
            Load(definition);
        }

        public EGraph Graph { get; }

        /// <summary>The frozen scope that holds the rows, settled; every bundle is a child of it.</summary>
        public Scope Theory { get; }

        public PathVocabulary Paths { get; }

        public PredicateVocabulary Ir { get; }

        /// <summary>A digest of every host row, so that an answer remembered under one theory is not read under another.</summary>
        public string Version { get; }

        /// <summary>The theory's schedule: the rows settled per set, and the classes the boxes allow and refute.</summary>
        public Schedule Settle { get; }

        /// <summary>A bundle's schedule, in strata: terms into atoms; cells; the service on them; the gate.</summary>
        public Schedule Check { get; }

        // The theory's tables: the rows as measured and as settled per set, and the agreement. `final` and `table` hold a
        // key once every target its set serves has measured it.
        public Function<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingTarget, CosmosAnswer> Outcome { get; }
        public Function<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingTarget, ValueSet<string>> Cites { get; }
        public Function<RenderingSet, ValueSet<RenderingTarget>> Serves { get; }
        public Function<AnswerMeanings> Means { get; }
        public Function<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<CosmosAnswer>> Answers { get; }
        public Function<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<RenderingTarget>> Measured { get; }
        public Function<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<string>> Evidence { get; }
        public Function<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet, ValueSet<CosmosAnswer>> Final { get; }
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

        // A bundle's tables. The host writes a path's spelling beside its term (a merge of two paths would give one class
        // two spellings, and throw), each arm of a junction, the set, each import once and lattice-free, and the candidates
        // with every member of their junctions as written; the rules write the rest.
        public Function<JPath, string> Spelling { get; }
        public Relation<Pred, Pred> Arm { get; }
        public Relation<RenderingSet> Under { get; }
        public Function<JPath, PathValues> Imported { get; }
        public Function<JPath, ValueSet<string>> ImportedChars { get; }
        public Function<JPath, ValueSet<string>> PartedLiterals { get; }
        public Relation<Pred, RenderPosition, ServiceForm> Candidate { get; }
        public Relation<ServiceForm, ServiceForm> Member { get; }
        public Function<Pred, ServiceForm, ValueSet<CutPoint>> Cuts { get; }
        public Function<Pred, ServiceForm, CellPartition> Partition { get; }
        public Relation<CellPartition, ServiceForm> Need { get; }
        public Function<CellPartition, ServiceForm, OutcomeVector> Svc { get; }
        public Function<Pred, ServiceForm, CellOutcomes> Outcomes { get; }
        public Function<Pred, RenderPosition, ServiceForm, Verdict, Proof> Agrees { get; }
        public Function<Pred, RenderPosition, ServiceForm, Verdict, Refutation> Disagrees { get; }
        public Relation<Pred, RenderPosition, ServiceForm, Verdict> Conflict { get; }

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
            values.Register<PathFacts>("path facts");
            values.Register<CellPartition>("cell partition");
            values.Register<OutcomeVector>("outcome vector");
            values.Register<CellOutcomes>("cell outcomes");
            values.RegisterSet<CosmosAnswer>();
            values.RegisterSet<RenderingTarget>();
            values.RegisterSet<CellClass>();
            values.RegisterSet<MeasuredEntry>();
            values.RegisterSet<CutPoint>();
        }

        void DefineTheory(Ruleset measure, Ruleset allowed, Ruleset refutes, Ruleset tabulate)
        {
            measure.DefineRule("a measured answer is one a set's service may give", r =>
            {
                var (t, a, c) = r.Vars<CosmosServiceTest, CosmosValueClass, CosmosValueClass>("t", "a", "c");
                var (x, s, o) = r.Vars<RenderingTarget, RenderingSet, CosmosAnswer>("x", "s", "o");
                var (d, i) = r.Vars<ValueSet<RenderingTarget>, ValueSet<string>>("d", "i");
                r.When(o.Is(Outcome.Of(t, a, c, x)), d.Is(Serves.Of(s)), i.Is(Cites.Of(t, a, c, x)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Then(Answers.Of(t, a, c, s).Is(Primitives.Singleton<CosmosAnswer>().Of(o)),
                    Measured.Of(t, a, c, s).Is(Primitives.Singleton<RenderingTarget>().Of(x)), Evidence.Of(t, a, c, s).Is(i));
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
                var (t, a, c, s) = r.Vars<CosmosServiceTest, CosmosValueClass, CosmosValueClass, RenderingSet>("t", "a", "c", "s");
                var (m, d) = r.Vars<ValueSet<RenderingTarget>, ValueSet<RenderingTarget>>("m", "d");
                var (v, i, mn) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>, AnswerMeanings>("v", "i", "mn");
                r.When(m.Is(Measured.Of(t, a, c, s)), d.Is(Serves.Of(s)), v.Is(Answers.Of(t, a, c, s)), i.Is(Evidence.Of(t, a, c, s)), mn.Is(Means.Of()));
                r.Where(Primitives.IsSubsetOf<RenderingTarget>().Of(d, m));
                r.Then(Table.Of(s).Is(Primitives.Singleton<MeasuredEntry>().Of(Compute.Of(Compute.Of(t, a, c, MeasuredKey.Of), v, i, mn, MeasuredEntry.Of))),
                    Final.Of(t, a, c, s).Is(v));
            });
        }

        void DefineCells(RenderingDefinition definition, Ruleset cuts, Ruleset partition, Ruleset need, Ruleset parted)
        {
            cuts.DefineRule("an atom's path is cut at its literals, the form's, and the edges its reader names", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, k, g) = r.Vars<RenderPosition, string, ReaderEdges>("pos", "k", "g");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), k.Is(Compute.Of(a, x => x.Reader)), g.Is(Edges.Of(k)));
                r.Then(Cuts.Of(e, f).Is(Compute.Of(a, f, g, definition.Cut)));
            });

            partition.DefineRule("an atom's import is cut into cells where the form reads that path alone", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, x, k) = r.Vars<RenderPosition, PathValues, ValueSet<string>>("pos", "x", "k");
                var (n, c) = r.Vars<string, ValueSet<CutPoint>>("n", "c");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), x.Is(Imported.Of(p)), k.Is(ImportedChars.Of(p)), n.Is(Spelling.Of(p)), c.Is(Cuts.Of(e, f)));
                r.Where(Primitives.IsSubsetOf<string>().Of(Compute.Of(f, y => y.Paths), Primitives.Singleton<string>().Of(n)));
                r.Then(Partition.Of(e, f).Is(Compute.Of(Compute.Of(n, x, k, (q, y, z) => new PathFacts(q, y, z)), c, definition.Split)));
            });

            need.DefineRule("a candidate of an atom is evaluated on the atom's cells", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z) = r.Vars<RenderPosition, CellPartition>("pos", "z");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)));
                r.Then(Need.Of(z, f));
            });

            // The filter is placed before the parts are taken: a leaf has none, and taking them throws.
            need.DefineRule("a compound that is evaluated is evaluated through its parts", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("g", "h1", "h2");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)));
                r.Then(Need.Of(z, h1), Need.Of(z, h2));
            });

            parted.DefineRule("a string literal parts the orders over a path whose strings may hold the other band", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                var (k, b) = r.Vars<ValueSet<string>, ValueSet<string>>("k", "b");
                var (u, w, l) = r.Vars<string, string, string>("u", "w", "l");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), k.Is(ImportedChars.Of(p)), Parting.Of(u, w));
                r.Where(RenderingPrimitives.StringOrdering.Of(g));
                r.When(l.Is(Compute.Of(g, x => x.Literal!)), b.Is(Compute.Of(l, x => JsonValues.CharactersOf(x))));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Where(Primitives.Contains<string>().Of(b, w));
                r.Then(PartedLiterals.Of(p).Is(Primitives.Singleton<string>().Of(l)));
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

            evaluate.DefineRule("a compound is evaluated from its parts by its connective's rows", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2, s) = r.Vars<ServiceForm, ServiceForm, ServiceForm, RenderingSet>("g", "h1", "h2", "s");
                var (v, w, t) = r.Vars<OutcomeVector, OutcomeVector, ValueSet<MeasuredEntry>>("v", "w", "t");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)), v.Is(Svc.Of(z, h1)), w.Is(Svc.Of(z, h2)),
                    Under.Of(s), t.Is(Table.Of(s)));
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, v, w, Compute.Of(t, ServiceTable.Of), definition.Compound)));
            });
        }

        void DefineGate(RenderingDefinition definition, Ruleset classes, Ruleset gate, Ruleset conflict)
        {
            classes.DefineRule("Calcite's atom and the form are paired on every cell", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z, v) = r.Vars<RenderPosition, CellPartition, OutcomeVector>("pos", "z", "v");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)), v.Is(Svc.Of(z, f)));
                r.Then(Outcomes.Of(e, f).Is(Compute.Of(a, v, definition.Pair)));
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
                r.Then(Agrees.Of(e, pos, f, v).Is(Compute.Of(o, w, Primitives.IsSubsetOf<CellClass>().Of(kk, ro), Proof.Of)));
            });

            gate.DefineRule("a candidate is refuted where a class of its cells is a definite counterexample", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var o = r.Var<CellOutcomes>("o");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Refutes.Of(pos, v)),
                    m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
                r.Where(RenderingPrimitives.SetNonempty.Of(m));
                r.Then(Disagrees.Of(e, pos, f, v).Is(Compute.Of(o, m, Refutation.Of)));
            });

            conflict.DefineRule("an agreement and a refutation of one candidate are a contradiction", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                r.When(Fact.Exists(Agrees.Of(e, pos, f, v)), Fact.Exists(Disagrees.Of(e, pos, f, v)));
                r.Then(Conflict.Of(e, pos, f, v), Graph.Contradiction.Of());
            });
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
                        foreach (var (_, (t, a, c), x, answer) in axiom)
                            w.Assume(Outcome.Of(t, a, c, x).Is(answer), Cites.Of(t, a, c, x).Is(ValueSet.Of(axiom.Key)));
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
                foreach (var (set, targets) in definition.Serves)
                    w.Assume(Serves.Of(set).Is(targets));

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

                w.Assume(Means.Of().Is(definition.Means), RaiseOnly.Of().Is(AgreementRows.RaiseOnly), Tolerated.Of().Is(definition.Tolerated), _version.Of().Is(Version));
            });

            var report = Theory.Run(Settle);
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"The rendering theory stopped {report.StopReason} after {report.Iterations} iterations.");

            Theory.Freeze();
        }

        static string Digest(RenderingDefinition d)
        {
            static string Set<T>(IEnumerable<T> items) => string.Join(",", items);
            var rows = d.Rows.Select(r => $"outcome {r.AxiomId} {r.Key} {r.Target} {r.Answer}")
                .Concat(d.Serves.Select(s => $"serves {s.Key} {Set(s.Value)}"))
                .Concat(d.Allowing.Select(a => $"allowing {a.Item1} {a.Item2} {a.Item3.Calcite} {a.Item3.Service} {Set(a.Item3.Cites)}"))
                .Concat(d.Refuting.Select(a => $"refuting {a.Item1} {a.Item2} {a.Item3.Calcite} {a.Item3.Service} {Set(a.Item3.Cites)}"))
                .Concat(d.Edges.Select(e => $"edges {e.Key} {Set(e.Value.Edges)} {Set(e.Value.Boundaries)}"))
                .Concat(d.Parting.Select(p => $"parting {p.Item1} {p.Item2}"))
                .Concat(AgreementRows.Classes.Select(k => $"class {k}"))
                .Append($"means {d.Means}").Append($"tolerated {d.Tolerated}")
                .OrderBy(row => row, StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
        }

    }

}
