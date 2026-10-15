using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// Bugs planted in the predicate oracle, each of which <see cref="PopulationD"/> must catch within the trials a run of
    /// the suite pays for: what the checker is worth is what it finds when something is wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each plant is a variant of a rule, in a graph of its own.</b> A test makes a <see cref="JsonFactGenerations"/>
    /// no planner reads, defines the predicate rules there, and adds the faulty rule beside the right one — which for a
    /// rule that only adds facts is the same fault as replacing it — or schedules a faulty split in place of the guarded
    /// one, or hands the renderer the wrong stratum. Nothing else the suite runs sees the plant.
    /// </para>
    /// <para>
    /// The six of Stage 1's plan, and a seventh that only C5 sees: a union of two predicates, which is the trap a
    /// property of a context written as a property of a term becomes under congruence.
    /// </para>
    /// </remarks>
    public sealed class PlantedBugTests
    {

        /// <summary>The trials a plant must be caught within: a run of the suite's.</summary>
        const int Within = PredicateSoundnessTests.Trials;

        /// <summary>
        /// A rendering's preimage read as exact: an equality with text is decided TRUE where every value its path may
        /// hold is in the preimage. A stored 30.0 is in the preimage of <c>'30'</c> as the theory spells it, and renders
        /// as <c>30.0</c>.
        /// </summary>
        [Fact]
        public void ATooWidePreimageRowIsCaught() => Caught(Planted(rules =>
            rules.Decide.DefineRule("planted: an equality with text holds where the path holds only its preimage", r =>
            {
                var (e, p, a, x) = r.Vars<Pred, JPath, PredicateAtom, PathValues>("e", "p", "a", "x");
                r.When(rules.Atom.Of(e, p, a), x.Is(rules.Possible.Of(p)));
                r.Where(a, x, (atom, values) => atom.Reader == PredicateAtom.TextReader && atom.Parameter == false && atom.Test is PredicateAtom.Equal or PredicateAtom.InTest
                    && values.IsPresent && values.Values is { Count: > 0 } held && atom.Kept(true).Values is { } preimage && held.All(preimage.Contains));
                r.Then(rules.AlwaysTrue.Of(e));
            })));

        /// <summary>
        /// A disjunction that holds read as each of its arms holding: an arm's facts established from what only weakens
        /// it.
        /// </summary>
        [Fact]
        public void EstablishingFromAWeakeningIsCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a disjunction that holds holds on both sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(rules.Holds.Of(e, true), e.Is(rules.Or.Of(a, b)));
                r.Then(rules.Holds.Of(a, true), rules.Holds.Of(b, true));
            })));

        /// <summary>
        /// Never-true read beneath a <c>NOT</c>: a comparison that is UNKNOWN on some row keeps none, and neither does its
        /// negation.
        /// </summary>
        [Fact]
        public void ReadingNeverTrueBeneathANotIsCaught() => Caught(Planted(rules =>
            rules.Decide.DefineRule("planted: not of what is never true is always true", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                r.When(e.Is(rules.Not.Of(a)), rules.NeverTrue.Of(a));
                r.Then(rules.AlwaysTrue.Of(e));
            })));

        /// <summary>
        /// The split over a junction's arms without its <c>holds</c> guard: every disjunction a context holds a term of
        /// splits, whether or not it holds, and what all its arms prove is lifted into a context that never assumed it.
        /// </summary>
        [Fact]
        public void ADisjunctSplitWithoutItsGuardIsCaught() => Caught(Planted(rules =>
        {
            var unguarded = rules.Graph.DefineSplit("planted: which arm decides any junction", s =>
            {
                var (o, t, n) = s.Vars<Pred, bool, ValueSet<long>>("o", "t", "n");
                s.When(n.Is(rules.Arms.Of(o, t)));
                var i = s.Cases("i", n);
                s.Key(o, t);
                s.Assume(rules.Chosen.Of(o, t, i));
            });
            rules.ScheduleWith(unguarded);
        }));

        /// <summary>
        /// An axiom row that contradicts the measurement: the text accessor's null test read as FALSE of an object and an
        /// array, which <c>JSON_VALUE</c> answers SQL null for — root cause A of the soundness review, as a row of the
        /// table, in both of the directions a row is read.
        /// </summary>
        [Fact]
        public void AContradictingAxiomRowIsCaught() => Caught(Planted(rules =>
        {
            rules.Establish.DefineRule("planted: a text null test that holds says the path holds no structure", r =>
            {
                var (e, p, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "p", "a");
                r.When(rules.Holds.Of(e, true), rules.Atom.Of(e, p, a));
                r.Where(a, atom => atom.Reader == PredicateAtom.TextReader && atom.Test == PredicateAtom.NullTest);
                r.Then(rules.Json.Types.Of(p).Is(ValueSet.Of(NullType)));
            });
            rules.Decide.DefineRule("planted: a text null test over a present path that is no null is false", r =>
            {
                var (e, p, a, x) = r.Vars<Pred, JPath, PredicateAtom, PathValues>("e", "p", "a", "x");
                r.When(rules.Atom.Of(e, p, a), x.Is(rules.Possible.Of(p)));
                r.Where(a, x, (atom, values) => atom.Reader == PredicateAtom.TextReader && atom.Test == PredicateAtom.NullTest
                    && values.IsPresent && values.Kinds.Contains(NullType) == false);
                r.Then(rules.AlwaysFalse.Of(e));
            });
        }));

        /// <summary>
        /// A stratum that hands a conjunct its own facts to render from: #117's self-certification, in the strata loop
        /// rather than in a rule.
        /// </summary>
        [Fact]
        public void AStratumAdmittingAConjunctWithItsOwnFactsIsCaught() =>
            Caught(theory => new CosmosOracle(theory, null) { RenderedFrom = (terms, stratum, conjunct) => terms.Child(stratum, new[] { conjunct }) });

        /// <summary>
        /// A union of two predicates: a conjunction with an arm the context decides always true made one class with its
        /// other arm. Right of every row the context keeps, and wrong of the rows it does not — a property of the context
        /// written as one of the term, which only local validation over every declared document sees.
        /// </summary>
        [Fact]
        public void AUnionOfTwoPredicatesIsCaughtByLocalValidation() => Caught(Planted(rules =>
        {
            foreach (var left in new[] { true, false })
                rules.Decide.DefineRule($"planted: a conjunction whose {(left ? "left" : "right")} side is always true is its other side", r =>
                {
                    var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                    r.When(e.Is(rules.And.Of(a, b)), rules.AlwaysTrue.Of(left ? a : b));
                    r.Then(e.Is(left ? b : a));
                });
        }), "c5");

        /// <summary>
        /// The predicate rules of a graph of the test's own, planted.
        /// </summary>
        static Func<JsonFactTheory, CosmosOracle> Planted(Action<PredicateRules> plant)
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            plant(PredicateRules.Of(generations.Current.Contexts));
            return theory => new CosmosOracle(theory, generations);
        }

        /// <summary>
        /// Runs the checker until it fails, and requires that it does within a run's trials — by the named check, where
        /// one is named.
        /// </summary>
        static void Caught(Func<JsonFactTheory, CosmosOracle> oracle, string? by = null)
        {
            var checker = new PopulationD(oracle);
            for (var trial = 0; trial < Within; trial++)
            {
                var report = checker.Run(PredicateSoundnessTests.Seed + trial);
                if (report.Failures.Count == 0 || by is not null && report.Failures.All(f => f.Check != by))
                    continue;

                var first = report.Failures.First(f => by is null || f.Check == by);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"caught in trial {trial} of {Within}, by {string.Join(", ", report.Failures.Select(f => f.Check).Distinct())}: [{first.Check}] {first.Message}");
                return;
            }

            Assert.Fail($"the plant survived {Within} trials" + (by is null ? "" : $" of the {by} check"));
        }

    }

}
