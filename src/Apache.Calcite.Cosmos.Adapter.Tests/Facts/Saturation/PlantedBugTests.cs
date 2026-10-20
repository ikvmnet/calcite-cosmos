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
    /// property of a context written as a property of a term becomes under congruence. An eighth is a declaration's
    /// rather than a rule's: the characters one kind's <c>pattern</c> states, read of every kind. Five more are the
    /// characters' own: a value's read narrower than it is — its surrogates, its characters outside ASCII below them,
    /// those the service trims, or U+0000 left out — and an arm's stated of its disjunction. And two are the
    /// theory's rules of a domain: its numbers read as bounding the path above their least, and a domain of one read as
    /// one value in every document without the path's presence.
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
        /// A claim of a string's characters that one kind's branch makes, read as made of every kind: the shape of a
        /// discriminated union's pattern taken for the meet. What population D checks the characters a declared
        /// <c>pattern</c> states by.
        /// </summary>
        [Fact]
        public void AGuardedCharactersClaimReadOfEveryKindIsCaught() =>
            Caught(theory => new CosmosOracle(new JsonFactTheory(theory.Rules.Select(rule =>
                rule.Head.Claim is JsonClaim.Characters ? JsonFactRule.Unconditional(rule.Head) : rule)), null), "fact");

        /// <summary>
        /// A value's characters read by the unit's high band alone, a supplementary character's surrogates left out: what a
        /// domain of an <c>enum</c>, a <c>const</c> or a filter's <c>=</c> and <c>IN</c> says of a path, stated narrower
        /// than the strings it names.
        /// </summary>
        [Fact]
        public void AValuesCharactersReadWithoutItsSurrogatesAreCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a value's characters are its units from U+E000", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(rules.Json.Dom.Of(p)));
                r.Then(rules.Json.Chars.Of(p).Is(Compute.Of(d, domain => CharactersOf(domain).Remove(SurrogateBand))));
            })), "fact");

        /// <summary>
        /// A value's characters read without those outside ASCII below the surrogates: what a domain says of a path stated
        /// narrower than the strings it names, ß or the Kelvin sign read as ASCII, which would let a case mapping push.
        /// </summary>
        [Fact]
        public void AValuesCharactersReadAsAsciiBelowTheSurrogatesAreCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a value's characters below U+D800 are ASCII", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(rules.Json.Dom.Of(p)));
                r.Then(rules.Json.Chars.Of(p).Is(Compute.Of(d, domain => CharactersOf(domain).Remove(NonAsciiBand))));
            })), "fact");

        /// <summary>
        /// A value's characters read without those the service trims: a tab or U+00A0 in a domain left out of what it says
        /// of a path, which would let a trim push.
        /// </summary>
        [Fact]
        public void AValuesCharactersReadWithoutWhatTheServiceTrimsAreCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a value's characters are none the service trims", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(rules.Json.Dom.Of(p)));
                r.Then(rules.Json.Chars.Of(p).Is(Compute.Of(d, domain => CharactersOf(domain).Remove(TrimmedClass))));
            })), "fact");

        /// <summary>
        /// A value's characters read without U+0000: a domain holding one stated of a path as holding none, which would let
        /// a <c>REPLACE</c>, or a <c>LIKE</c> the service answers as a <c>CONTAINS</c>, push over a string they read only
        /// part of.
        /// </summary>
        [Fact]
        public void AValuesCharactersReadWithoutItsNulAreCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a value's characters hold no U+0000", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(rules.Json.Dom.Of(p)));
                r.Then(rules.Json.Chars.Of(p).Is(Compute.Of(d, domain => CharactersOf(domain).Remove(NulClass))));
            })), "fact");

        /// <summary>
        /// A call's characters read off its first operand alone: a concatenation said to hold what its left side holds, as
        /// a C# conjunction of questions that stopped at the first would, which the theory's join over every operand does
        /// not.
        /// </summary>
        [Fact]
        public void ACallsCharactersReadOffItsFirstOperandAloneAreCaught() => Caught(Planted(rules =>
            rules.Characters.DefineRule("planted: a call holds what its first operand holds", r =>
            {
                var (v, a, t, x) = r.Vars<Val, Args, Args, Args>("v", "a", "t", "x");
                var (h, f, c) = r.Vars<Val, string, ValueSet<string>>("h", "f", "c");
                r.When(rules.Walk.Of(v, 0L, a), a.Is(rules.Operand.Of(h, t)), v.Is(rules.Call.Of(f, x)), c.Is(rules.ValueChars.Of(h)));
                r.Where(f, function => function is "||" or "CONCAT" or "CASE");
                r.Then(rules.ValueChars.Of(v).Is(c));
            })), "computed");

        /// <summary>
        /// An upper-case mapping read as keeping every class where it was: Java's full mapping takes U+FB13, from U+E000,
        /// to Armenian letters below U+D800, which a string from the high band then holds.
        /// </summary>
        [Fact]
        public void AnUpperCaseMappingReadAsKeepingItsClassesIsCaught() => Caught(Planted(rules =>
            rules.Characters.DefineRule("planted: an upper-case mapping holds what it maps", r =>
            {
                var (v, a, t) = r.Vars<Val, Args, Args>("v", "a", "t");
                var (h, c) = r.Vars<Val, ValueSet<string>>("h", "c");
                r.When(v.Is(rules.Call.Of("UPPER", a)), a.Is(rules.Operand.Of(h, t)), c.Is(rules.ValueChars.Of(h)));
                r.Then(rules.ValueChars.Of(v).Is(c));
            })), "computed");

        /// <summary>
        /// A domain's numbers read as bounding the path from above their least: the rule that bounds a path by its
        /// domain, off by the least member itself, which a document holding it refutes.
        /// </summary>
        [Fact]
        public void ADomainsBoundStricterThanItsLeastIsCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a domain's numbers lie above its least", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(rules.Json.Dom.Of(p)));
                r.Where(d, x => NumbersOf(x) is not null);
                r.Then(rules.Json.Lo.Of(p).Is(Compute.Of(d, x => Bound.Above(NumbersOf(x)!.Value.Least))));
            })), "fact");

        /// <summary>
        /// A domain of one read as one value in every document, the presence left out: what a declared <c>const</c> on a
        /// property no <c>required</c> names says, read as a sort key that orders nothing and a partition key pinned.
        /// </summary>
        [Fact]
        public void AConstantWithoutItsPresenceIsCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a path of one value holds it in every document", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(rules.Json.Dom.Of(p)));
                r.Where(d, x => x.Count == 1);
                r.Then(rules.Json.Constant.Of(p, Compute.Of(d, x => x[0])));
            })), "fact");

        /// <summary>
        /// What one arm of a disjunction that holds says of a path's characters, stated of the disjunction: a filter's
        /// <c>OR</c> narrowing the characters where it should widen them.
        /// </summary>
        [Fact]
        public void AnArmsCharactersStatedOfItsDisjunctionAreCaught() => Caught(Planted(rules =>
            rules.Establish.DefineRule("planted: a disjunction that holds holds its left arm's characters", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                var (p, k) = r.Vars<JPath, PredicateAtom>("p", "k");
                r.When(rules.Holds.Of(e, true), e.Is(rules.Or.Of(a, b)), rules.Atom.Of(a, p, k));
                r.Where(k, atom => atom.Kept(true).Values is not null);
                r.Then(rules.Json.Chars.Of(p).Is(Compute.Of(k, atom => CharactersOf(atom.Kept(true).Values!))));
            })), "fact");

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
