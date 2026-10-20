using System;
using System.Linq;
using System.Runtime.CompilerServices;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The rules of predicates over JSON documents, beside <see cref="JsonRules"/> in the same graph: what a filter that
    /// holds says of the paths it reads, and what the facts say of a filter. The terms and their analysis are
    /// <see cref="PredicateVocabulary"/>'s, which the rendering graph defines too; the rest is the facts graph's alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Predicates are terms, and nothing here is ever extracted.</b> Every constructor is unextractable, no rule
    /// unions two predicates or two values, and the only unions in the graph are <see cref="JsonRules"/>' path unions and
    /// the congruence they bring. What the host reads back is a value — a verdict, a fact about a path — never a term to
    /// execute. A rendering is the host's, and it is never in the graph.
    /// </para>
    /// <para>
    /// <b>Three rulesets, run as strata.</b> <see cref="Analyse"/> reads terms into atoms: the path a value reads and
    /// how, and the test made of it (<see cref="PredicateAtom"/>). <see cref="Establish"/> reads what a predicate that
    /// holds, or fails, says of its paths, and runs inside the theory's saturation and splits. <see cref="Decide"/> runs
    /// once those have settled, and states the verdicts: <see cref="AlwaysTrue"/>, <see cref="AlwaysFalse"/> and
    /// <see cref="NeverTrue"/>. The second and third are kept apart because UNKNOWN is not FALSE under <c>NOT</c>: a
    /// comparison over an absent path keeps no row, and neither does its negation.
    /// </para>
    /// <para>
    /// <b>A verdict holds in every context beneath the one it was reached in.</b> Each condition a verdict rule reads
    /// only narrows as facts are added, so what holds of every document a context admits holds of every document a
    /// narrower one admits.
    /// </para>
    /// <para>
    /// <b>No rule names an operand shape.</b> What a comparison means on a stored value is <see cref="PredicateAtom"/>'s,
    /// in one place; the rules read atoms, and what a path may hold (<see cref="Possible"/>), and nothing finer.
    /// </para>
    /// </remarks>
    internal sealed class PredicateRules
    {

        static readonly ConditionalWeakTable<JsonContexts, PredicateRules> Defined = new();

        /// <summary>
        /// Returns the rules of predicates in a graph, defining them the first time they are asked for.
        /// </summary>
        /// <remarks>
        /// Defined after the graph's root is frozen, which a definition may be, and only where something asks: the planner
        /// never does, so the graphs it reasons in never hold them.
        /// </remarks>
        /// <param name="contexts">The graph and its rules of JSON.</param>
        /// <returns>The rules.</returns>
        public static PredicateRules Of(JsonContexts contexts)
        {
            lock (Defined)
            {
                if (Defined.TryGetValue(contexts, out var rules) == false)
                {
                    rules = new PredicateRules(contexts.Graph, contexts.Rules);
                    Defined.Add(contexts, rules);
                }

                return rules;
            }
        }

        PredicateRules(EGraph graph, JsonRules json)
        {
            Graph = graph;
            Json = json;
            Ir = new PredicateVocabulary(graph);

            var local = new TableOptions { Lift = false };
            var analysis = new TableOptions { Role = TableRole.Analysis };

            Holds = graph.DefineRelation<Pred, bool>("holds", local);
            Chosen = graph.DefineRelation<Pred, bool, long>("chosen", local);
            Arms = graph.DefineFunction<Pred, bool, ValueSet<long>>("arms", options: analysis);
            Arm = graph.DefineRelation<Pred, bool, long, Pred>("arm", analysis);
            Possible = graph.DefineFunction<JPath, PathValues>("possible", PathValues.Lattice, local);
            AlwaysTrue = graph.DefineRelation<Pred>("always true");
            AlwaysFalse = graph.DefineRelation<Pred>("always false");
            NeverTrue = graph.DefineRelation<Pred>("never true");

            Establish = graph.DefineRuleset("establish");
            Decide = graph.DefineRuleset("decide");

            DefineEstablish();
            DefineSplit();
            DefineDecide();

            ScheduleWith(ByArm);
        }

        /// <summary>
        /// Schedules the rules around a split over a junction's arms: <see cref="ByArm"/>, or, in a graph a checker owns, a
        /// variant of it planted to be caught.
        /// </summary>
        /// <param name="arms">The split over arms.</param>
        internal void ScheduleWith(SplitRule arms)
        {
            var json = Json;

            // A question pays for the rules of JSON, JSON Schema and what predicates establish on every iteration, and
            // for the verdicts once, after the facts have settled. Arms split before discriminators, two deep: a
            // disjunction spelled per kind lifts the kinds its arms name, and a discriminator then opens a case only for
            // each of those, where split together it opened one for every kind the declaration has. Within an arm both
            // may split again; within a discriminator's case only a discriminator, as JsonRules.QuerySaturation does.
            var settle = Schedule.Saturate(json.Json, json.JsonSchema, Analyse, Establish);
            var inArm = Schedule.Saturate(settle, Schedule.Split(arms, settle), Schedule.Split(json.QuerySplit, settle));
            var inDiscriminator = Schedule.Saturate(settle, Schedule.Split(json.QuerySplit, settle));
            QuerySaturation = Schedule.Saturate(settle, Schedule.Split(arms, inArm), Schedule.Split(json.QuerySplit, inDiscriminator));
            Saturation = Schedule.Sequence(QuerySaturation, Schedule.Saturate(Decide));

            // A context that assumes nothing learns nothing a split could prove that its parent's split has not lifted:
            // it reads its new terms, the paths they mention, and the verdicts.
            Answer = Schedule.Sequence(Schedule.Saturate(json.Json, json.JsonSchema, Analyse), Schedule.Saturate(Decide));
        }

        public EGraph Graph { get; }

        /// <summary>Gets the rules of JSON the predicates read paths in.</summary>
        public JsonRules Json { get; }

        /// <summary>Gets the terms of a predicate and their analysis, which the rendering graph defines too.</summary>
        public PredicateVocabulary Ir { get; }

        public Constructor<Pred, Pred, Pred> And => Ir.And;
        public Constructor<Pred, Pred, Pred> Or => Ir.Or;
        public Constructor<Pred, Pred> Not => Ir.Not;
        public Relation<Pred, string> Point => Ir.Point;
        public Relation<Pred, JPath, PredicateAtom> Atom => Ir.Atom;
        public Ruleset Analyse => Ir.Analyse;

        /// <summary>
        /// The predicate takes this truth value on every row the context admits: TRUE for a conjunct the context assumes,
        /// FALSE beneath a negation that holds. Never lifted: it says what a case chose, not what every case proves.
        /// </summary>
        public Relation<Pred, bool> Holds { get; }

        /// <summary>The arm a case of <see cref="ByArm"/> chose for a junction taking a truth value.</summary>
        public Relation<Pred, bool, long> Chosen { get; }

        /// <summary>
        /// The arms of a junction that decide it at a truth value — a disjunction's at TRUE, a conjunction's at FALSE —
        /// numbered: stated by the host beside the term, flattened over its right-nesting.
        /// </summary>
        public Function<Pred, bool, ValueSet<long>> Arms { get; }

        /// <summary>One numbered arm of a junction.</summary>
        public Relation<Pred, bool, long, Pred> Arm { get; }

        /// <summary>What a path an atom reads may hold, gathered from the theory's tables into one value.</summary>
        public Function<JPath, PathValues> Possible { get; }

        /// <summary>The predicate is TRUE of every row the context admits.</summary>
        public Relation<Pred> AlwaysTrue { get; }

        /// <summary>The predicate is FALSE, never UNKNOWN, of every row the context admits.</summary>
        public Relation<Pred> AlwaysFalse { get; }

        /// <summary>The predicate is FALSE or UNKNOWN of every row the context admits: it keeps none.</summary>
        public Relation<Pred> NeverTrue { get; }

        /// <summary>What a predicate that takes a truth value says of the paths it reads.</summary>
        public Ruleset Establish { get; }

        /// <summary>What the facts say of a predicate: one stratum, run after they settle.</summary>
        public Ruleset Decide { get; }

        /// <summary>The split over the arms of a junction that takes the truth value its arms decide.</summary>
        public SplitRule ByArm { get; private set; } = null!;

        /// <summary>
        /// The rules of JSON, of JSON Schema, of analysis and of what predicates establish, saturated, with the theory's
        /// split on relevant discriminators and this split over arms, two deep.
        /// </summary>
        public Schedule QuerySaturation { get; private set; } = null!;

        /// <summary><see cref="QuerySaturation"/>, then the verdicts to saturation.</summary>
        public Schedule Saturation { get; private set; } = null!;

        /// <summary>
        /// What a context that assumes nothing runs: the rules of JSON and of analysis over its new terms, without a
        /// split, then the verdicts. A question asked of a frozen context is answered in one.
        /// </summary>
        public Schedule Answer { get; private set; } = null!;

        void DefineEstablish()
        {
            var json = Json;

            Establish.DefineRule("a conjunction that holds holds on both sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(Holds.Of(e, true), e.Is(And.Of(a, b)));
                r.Then(Holds.Of(a, true), Holds.Of(b, true));
            });
            Establish.DefineRule("a disjunction that fails fails on both sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(Holds.Of(e, false), e.Is(Or.Of(a, b)));
                r.Then(Holds.Of(a, false), Holds.Of(b, false));
            });
            Establish.DefineRule("a negation takes the other truth value of its operand", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var t = r.Var<bool>("t");
                r.When(Holds.Of(e, t), e.Is(Not.Of(a)));
                r.Then(Holds.Of(a, Compute.Of(t, x => x == false)));
            });
            Establish.DefineRule("the chosen arm takes the junction's truth value", r =>
            {
                var (o, b) = r.Vars<Pred, Pred>("o", "b");
                var (t, i) = r.Vars<bool, long>("t", "i");
                r.When(Chosen.Of(o, t, i), Arm.Of(o, t, i, b));
                r.Then(Holds.Of(b, t));
            });
            Establish.DefineRule("a predicate is not both true and false of a row", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(Holds.Of(e, true), Holds.Of(e, false));
                r.Then(Graph.Contradiction.Of());
            });

            // What an atom taking a truth value says of the path it reads: what PredicateAtom.Kept says every such row
            // holds there, and no more. A comparison's own rendering preimage is the most it states, so it never states
            // what would make itself exact.
            Establish.DefineRule("an atom that takes a truth value only where its path is there says it is there", r =>
            {
                var (e, p, a, t) = r.Vars<Pred, JPath, PredicateAtom, bool>("e", "p", "a", "t");
                r.When(Holds.Of(e, t), Atom.Of(e, p, a));
                r.Where(a, t, (atom, truth) => atom.Kept(truth).IsPresent);
                r.Then(json.Present.Of(p));
            });
            Establish.DefineRule("an atom that takes a truth value says what types its path may hold", r =>
            {
                var (e, p, a, t) = r.Vars<Pred, JPath, PredicateAtom, bool>("e", "p", "a", "t");
                r.When(Holds.Of(e, t), Atom.Of(e, p, a));
                r.Where(a, t, (atom, truth) => atom.Kept(truth).Types.Count < JsonValues.AllTypes.Count);
                r.Then(json.Types.Of(p).Is(Compute.Of(a, t, (atom, truth) => atom.Kept(truth).Types)));
            });
            Establish.DefineRule("an atom that takes a truth value says what values its path may hold", r =>
            {
                var (e, p, a, t) = r.Vars<Pred, JPath, PredicateAtom, bool>("e", "p", "a", "t");
                r.When(Holds.Of(e, t), Atom.Of(e, p, a));
                r.Where(a, t, (atom, truth) => atom.Kept(truth).Values is not null);
                r.Then(json.Dom.Of(p).Is(Compute.Of(a, t, (atom, truth) => atom.Kept(truth).Values!)));
            });
            Establish.DefineRule("an atom that takes a truth value says how small a number its path may hold", r =>
            {
                var (e, p, a, t) = r.Vars<Pred, JPath, PredicateAtom, bool>("e", "p", "a", "t");
                r.When(Holds.Of(e, t), Atom.Of(e, p, a));
                r.Where(a, t, (atom, truth) => atom.Kept(truth).Lower is not null);
                r.Then(json.Lo.Of(p).Is(Compute.Of(a, t, (atom, truth) => atom.Kept(truth).Lower!.Value)));
            });
            Establish.DefineRule("an atom that takes a truth value says how large a number its path may hold", r =>
            {
                var (e, p, a, t) = r.Vars<Pred, JPath, PredicateAtom, bool>("e", "p", "a", "t");
                r.When(Holds.Of(e, t), Atom.Of(e, p, a));
                r.Where(a, t, (atom, truth) => atom.Kept(truth).Upper is not null);
                r.Then(json.Hi.Of(p).Is(Compute.Of(a, t, (atom, truth) => atom.Kept(truth).Upper!.Value)));
            });
            Establish.DefineRule("an atom that takes a truth value says which of its points its path does not hold", r =>
            {
                var (e, p, a, t) = r.Vars<Pred, JPath, PredicateAtom, bool>("e", "p", "a", "t");
                var v = r.Var<string>("v");
                r.When(Holds.Of(e, t), Atom.Of(e, p, a), Point.Of(e, v));
                r.Where(a, t, v, (atom, truth, point) => atom.Kept(truth).Excluded.Contains(point));
                r.Then(json.Ne.Of(p, v));
            });

            // A rule of JSON the theory's own rules leave out, because nothing the planner states needed it: a domain
            // stated beside narrower types keeps its members of those types. A preimage meets a declared type here. Read
            // for the paths an atom reads, which are where a predicate states a domain, and not for every path the
            // declaration speaks of, which a rule defined after it was frozen would otherwise read again in every context.
            Establish.DefineRule("a domain an atom's path holds keeps only values of a type the path may hold", r =>
            {
                var (e, p, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "p", "a");
                var (d, t) = r.Vars<ValueSet<string>, ValueSet<string>>("d", "t");
                r.When(Atom.Of(e, p, a), d.Is(json.Dom.Of(p)), t.Is(json.Types.Of(p)));
                r.Where(d, t, (domain, types) => domain.Any(v => types.Contains(JsonValues.TypeOf(v)) == false));
                r.Then(json.Dom.Of(p).Is(Compute.Of(d, t, (domain, types) => domain.Where(v => types.Contains(JsonValues.TypeOf(v))))));
            });
        }

        /// <summary>
        /// One case per arm of a junction taking the truth value its arms decide: a disjunction that holds holds by some
        /// arm, and a conjunction that fails fails by some arm, so the cases cover the context. What every case proves
        /// is lifted, and an arm the facts contradict drops out of the lift.
        /// </summary>
        void DefineSplit()
        {
            ByArm = Graph.DefineSplit("which arm decides the junction", s =>
            {
                var (o, t, n) = s.Vars<Pred, bool, ValueSet<long>>("o", "t", "n");
                s.When(Holds.Of(o, t), n.Is(Arms.Of(o, t)));
                var i = s.Cases("i", n);
                s.Key(o, t);
                s.Assume(Chosen.Of(o, t, i));
            });
        }

        void DefineDecide()
        {
            var json = Json;

            // What a path an atom reads may hold, as one value, so that a verdict reads one row. Each fact narrows it.
            Decide.DefineRule("a path an atom reads may hold what nothing has ruled out", r =>
            {
                var (e, p, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "p", "a");
                r.When(Atom.Of(e, p, a));
                r.Then(Possible.Of(p).Is(PathValues.Anything));
            });
            Decide.DefineRule("a path that is there is not absent", r =>
            {
                var (p, x) = r.Vars<JPath, PathValues>("p", "x");
                r.When(x.Is(Possible.Of(p)), json.Present.Of(p));
                r.Then(Possible.Of(p).Is(PathValues.AnythingThere));
            });
            Decide.DefineRule("a path holds a value of a type it may hold", r =>
            {
                var (p, x, t) = r.Vars<JPath, PathValues, ValueSet<string>>("p", "x", "t");
                r.When(x.Is(Possible.Of(p)), t.Is(json.Types.Of(p)));
                r.Then(Possible.Of(p).Is(Compute.Of(t, PathValues.OfTypes)));
            });
            Decide.DefineRule("a path holds a value of its domain", r =>
            {
                var (p, x, d) = r.Vars<JPath, PathValues, ValueSet<string>>("p", "x", "d");
                r.When(x.Is(Possible.Of(p)), d.Is(json.Dom.Of(p)));
                r.Then(Possible.Of(p).Is(Compute.Of(d, PathValues.OneOf)));
            });
            Decide.DefineRule("a path does not hold a value it is unequal to", r =>
            {
                var (p, x, v) = r.Vars<JPath, PathValues, string>("p", "x", "v");
                r.When(x.Is(Possible.Of(p)), json.Ne.Of(p, v));
                r.Then(Possible.Of(p).Is(Compute.Of(v, PathValues.Not)));
            });
            Decide.DefineRule("a path holds no number below its lower bound", r =>
            {
                var (p, x, l) = r.Vars<JPath, PathValues, Bound>("p", "x", "l");
                r.When(x.Is(Possible.Of(p)), l.Is(json.Lo.Of(p)));
                r.Then(Possible.Of(p).Is(Compute.Of(l, PathValues.Above)));
            });
            Decide.DefineRule("a path holds no number above its upper bound", r =>
            {
                var (p, x, h) = r.Vars<JPath, PathValues, Bound>("p", "x", "h");
                r.When(x.Is(Possible.Of(p)), h.Is(json.Hi.Of(p)));
                r.Then(Possible.Of(p).Is(Compute.Of(h, PathValues.Below)));
            });

            // An atom's verdict, read off what its path may hold.
            Decide.DefineRule("an atom true of everything its path may hold is always true", r =>
            {
                var (e, p, a, x) = r.Vars<Pred, JPath, PredicateAtom, PathValues>("e", "p", "a", "x");
                r.When(Atom.Of(e, p, a), x.Is(Possible.Of(p)));
                r.Where(a, x, (atom, values) => atom.AlwaysTrue(values));
                r.Then(AlwaysTrue.Of(e));
            });
            Decide.DefineRule("an atom false of everything its path may hold is always false", r =>
            {
                var (e, p, a, x) = r.Vars<Pred, JPath, PredicateAtom, PathValues>("e", "p", "a", "x");
                r.When(Atom.Of(e, p, a), x.Is(Possible.Of(p)));
                r.Where(a, x, (atom, values) => atom.AlwaysFalse(values));
                r.Then(AlwaysFalse.Of(e));
            });
            Decide.DefineRule("an atom true of nothing its path may hold is never true", r =>
            {
                var (e, p, a, x) = r.Vars<Pred, JPath, PredicateAtom, PathValues>("e", "p", "a", "x");
                r.When(Atom.Of(e, p, a), x.Is(Possible.Of(p)));
                r.Where(a, x, (atom, values) => atom.NeverTrue(values));
                r.Then(NeverTrue.Of(e));
            });

            // What a context assumes it has decided, whatever its atoms can be said to mean: an opaque predicate, or an
            // equality of two numbers no atom calls equal across numeric types, holds on every row a context assuming it
            // admits. Read where the verdicts are, after the splits: what holds inside a case is the case's choice, and
            // is never lifted to be read here.
            Decide.DefineRule("a predicate that holds true of every row is always true", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(Holds.Of(e, true));
                r.Then(AlwaysTrue.Of(e));
            });
            Decide.DefineRule("a predicate that holds false of every row is always false", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(Holds.Of(e, false));
                r.Then(AlwaysFalse.Of(e));
            });

            // The connectives, in Kleene's logic. FALSE is never UNKNOWN, so what is always false is never true; the
            // converse does not hold, and nothing here reads NeverTrue beneath a NOT.
            Decide.DefineRule("what is always false is never true", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(AlwaysFalse.Of(e));
                r.Then(NeverTrue.Of(e));
            });
            Decide.DefineRule("not of what is always false is always true", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                r.When(e.Is(Not.Of(a)), AlwaysFalse.Of(a));
                r.Then(AlwaysTrue.Of(e));
            });
            Decide.DefineRule("not of what is always true is always false", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                r.When(e.Is(Not.Of(a)), AlwaysTrue.Of(a));
                r.Then(AlwaysFalse.Of(e));
            });
            Decide.DefineRule("a conjunction of truths is true", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(And.Of(a, b)), AlwaysTrue.Of(a), AlwaysTrue.Of(b));
                r.Then(AlwaysTrue.Of(e));
            });
            Decide.DefineRule("a disjunction of what is never true is never true", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(Or.Of(a, b)), NeverTrue.Of(a), NeverTrue.Of(b));
                r.Then(NeverTrue.Of(e));
            });
            Decide.DefineRule("a disjunction of falsehoods is false", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(Or.Of(a, b)), AlwaysFalse.Of(a), AlwaysFalse.Of(b));
                r.Then(AlwaysFalse.Of(e));
            });

            foreach (var left in new[] { true, false })
            {
                var side = left ? "left" : "right";
                Decide.DefineRule($"a conjunction whose {side} side is never true is never true", r =>
                {
                    var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                    r.When(e.Is(And.Of(a, b)), NeverTrue.Of(left ? a : b));
                    r.Then(NeverTrue.Of(e));
                });
                Decide.DefineRule($"a conjunction whose {side} side is always false is always false", r =>
                {
                    var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                    r.When(e.Is(And.Of(a, b)), AlwaysFalse.Of(left ? a : b));
                    r.Then(AlwaysFalse.Of(e));
                });
                Decide.DefineRule($"a disjunction whose {side} side is always true is always true", r =>
                {
                    var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                    r.When(e.Is(Or.Of(a, b)), AlwaysTrue.Of(left ? a : b));
                    r.Then(AlwaysTrue.Of(e));
                });
            }
        }

    }

}
