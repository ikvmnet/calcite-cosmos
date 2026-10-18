using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// The oracle's facade: the strata a statement's conjuncts are admitted in, the licence table that chooses the facts
    /// a question is answered from, redundancy, what an incomplete run is read as, and a container that declares no
    /// schema.
    /// </summary>
    public sealed class CosmosOracleTests
    {

        static readonly JsonDocumentPath A = P("a");
        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Label = P("label");
        static readonly JsonDocumentPath Id = P("id");
        static readonly JsonDocumentPath X = P("x");
        static readonly JsonDocumentPath B = P("b");

        /// <summary>The service's facts alone: the theory of every container that declares no schema.</summary>
        static readonly JsonFactTheory Undeclared = new(CosmosServiceFacts.Rules);

        static bool Knows(JsonFactSet facts, JsonDocumentPath path, JsonClaim claim) => facts.Knows(new JsonFact(path, claim));

        // The strata.

        /// <summary>
        /// A renderer that needs a fact only the conjunct itself establishes never admits it: the conjunct is rendered from
        /// the facts above it, and its own enter only below.
        /// </summary>
        [Fact]
        public void NoConjunctIsAdmittedOnItsOwnFacts()
        {
            var statement = new PredicateStatement(new[] { Eq(Text(A), "x") });
            var scope = Oracle(Theory()).Admit(statement, (i, facts) => Knows(facts, A, Present));

            scope.Strata.Should().BeEmpty();
            scope.Residual.Should().Equal(0);
        }

        [Fact]
        public void AConjunctIsAdmittedOnASiblingsFactsInTheStratumAfterIt()
        {
            // The null test renders unaided; the equality only where a's presence is known.
            var statement = new PredicateStatement(new[] { Eq(Text(A), "x"), IsNotNull(Text(A)) });
            var scope = Oracle(Theory()).Admit(statement, (i, facts) => i == 1 || Knows(facts, A, Present));

            scope.Strata.Should().HaveCount(2);
            scope.Strata[0].Should().Equal(1);
            scope.Strata[1].Should().Equal(0);
            scope.AssumedIn(OracleLicence.RenderConjunct(0)).Should().Equal(1);
            scope.AssumedIn(OracleLicence.RenderConjunct(1)).Should().BeEmpty();
            Knows(scope.Facts(OracleLicence.RenderConjunct(0)), A, Present).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.RenderConjunct(0)), A, Value("x")).Should().BeFalse("its own value is never among the facts it is rendered from");
        }

        /// <summary>
        /// #117's self-certification. An equality with text a number renders as was read as the string, the string as the
        /// path being a string, and the comparison then certified itself exact and lost the stored number. Neither step
        /// can be taken here: the preimage does not say the path is a string, and a renderer that certifies exactness
        /// from the facts it is handed is never handed the conjunct's own.
        /// </summary>
        [Fact]
        public void TheSelfCertificationOf117CannotBeDerived()
        {
            var thirty = Eq(Text(Label), "30");
            var stringOrNull = Type(JsonType.String, orNull: true);
            bool Exact(int i, JsonFactSet facts) => i != 0 || Knows(facts, Label, stringOrNull);

            // The fact the old extractor stated is not established, beside its own conjunct or anywhere below it.
            Knows(Established(Theory(), thirty), Label, stringOrNull).Should().BeFalse();

            // Alone, the comparison is never certified.
            Oracle(Theory()).Admit(new PredicateStatement(new[] { thirty }), Exact).Residual.Should().Equal(0);

            // Over a declared string, it is certified from the declaration, in the first stratum.
            Oracle(Theory(Outright(Label, Type(JsonType.String)))).Admit(new PredicateStatement(new[] { thirty }), Exact).StratumOf(0).Should().Be(1);

            // Beside a sibling that proves a stored string, it is certified from the sibling, in the stratum after it.
            var beside = Oracle(Theory()).Admit(new PredicateStatement(new[] { thirty, Eq(Raw(Label), "30") }), Exact);
            beside.StratumOf(1).Should().Be(1);
            beside.StratumOf(0).Should().Be(2);
            beside.AssumedIn(OracleLicence.RenderConjunct(0)).Should().Equal(1);
        }

        /// <summary>
        /// Random statements and renderers that read whatever the facts they are handed say: no admitted conjunct is
        /// among the conjuncts its facts came from, and the facts it was handed are the ones its render licence names.
        /// </summary>
        [Fact]
        public void NoConjunctIsEverRenderedFromItsOwnFacts()
        {
            var random = new Random(20261015);
            var paths = new[] { P("a"), P("b"), P("c") };
            var values = new JsonScalar[] { "x", "y", "30", 30m };
            var theory = Theory(Outright(P("c"), Type(JsonType.String)));

            SqlPredicate Atom()
            {
                var path = paths[random.Next(paths.Length)];
                return random.Next(5) switch
                {
                    0 => Eq(Text(path), values[random.Next(values.Length)] is var v && v.ToClrValue() is string ? v : "x"),
                    1 => Ne(Text(path), "y"),
                    2 => IsNotNull(Text(path)),
                    3 => Eq(Raw(path), values[random.Next(values.Length)]),
                    _ => In(Text(path), "x", "y"),
                };
            }

            for (var trial = 0; trial < 60; trial++)
            {
                var conjuncts = Enumerable.Range(0, random.Next(1, 5)).Select(_ => random.Next(4) == 0 ? Or(Atom(), Atom()) : Atom()).ToArray();
                var needs = conjuncts.Select(_ => paths[random.Next(paths.Length)]).ToArray();
                var unaided = conjuncts.Select(_ => random.Next(3) == 0).ToArray();
                var handed = new Dictionary<int, JsonFactSet>();

                var scope = Oracle(theory).Admit(new PredicateStatement(conjuncts), (i, facts) =>
                {
                    var renders = unaided[i] || Knows(facts, needs[i], Present);
                    if (renders)
                        handed[i] = facts;
                    return renders;
                });

                for (var i = 0; i < conjuncts.Length; i++)
                {
                    if (scope.StratumOf(i) == 0)
                        continue;

                    var assumed = scope.AssumedIn(OracleLicence.RenderConjunct(i));
                    assumed.Should().NotContain(i, $"trial {trial}: conjunct {i} is never rendered from its own facts");
                    assumed.Should().OnlyContain(j => scope.StratumOf(j) > 0 && scope.StratumOf(j) < scope.StratumOf(i));
                    scope.Facts(OracleLicence.RenderConjunct(i)).Should().BeSameAs(handed[i], "the facts it was rendered from are its licence's");
                }
            }
        }

        // The licence table.

        [Fact]
        public void TheLicenceTable()
        {
            // id and kind render unaided; the ordering on x only where kind is known; the opaque conjunct never.
            var statement = new PredicateStatement(new[]
            {
                Eq(Raw(Id), "1"),
                Eq(Text(Kind), "a"),
                Cmp(Text(X), ">", Lit("m")),
                Opaque(0),
            });
            var scope = Oracle(Theory()).Admit(statement, (i, facts) => i < 2 || i == 2 && Knows(facts, Kind, Value("a")));

            scope.Strata.Should().HaveCount(2);
            scope.Residual.Should().Equal(3);

            scope.AssumedIn(OracleLicence.RenderConjunct(0)).Should().BeEmpty();
            scope.AssumedIn(OracleLicence.RenderConjunct(2)).Should().Equal(0, 1);
            scope.AssumedIn(OracleLicence.RenderConjunct(3)).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.DeleteConjunct(1)).Should().Equal(new[] { 0, 2 }, "an admitted conjunct is deleted only for what reaches the service with it");
            scope.AssumedIn(OracleLicence.DeleteConjunct(3)).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.DeleteConjunct(1, new[] { 1, 2, 3 })).Should().Equal(2);
            scope.AssumedIn(OracleLicence.Siblings(1)).Should().Equal(0, 2, 3);
            scope.AssumedIn(OracleLicence.Siblings(3)).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.Applied(new[] { Opaque(7) })).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.PointRead(new[] { Id })).Should().Equal(0);
            scope.AssumedIn(OracleLicence.Select).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.OrderBy).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.GroupBy).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.Declaration).Should().BeEmpty();

            // And the facts each scope holds are those of the conjuncts it names.
            Knows(scope.Facts(OracleLicence.RenderConjunct(1)), Kind, Value("a")).Should().BeFalse();
            Knows(scope.Facts(OracleLicence.RenderConjunct(2)), Kind, Value("a")).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.DeleteConjunct(1)), Kind, Present).Should().BeFalse();
            Knows(scope.Facts(OracleLicence.DeleteConjunct(1)), X, Present).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.PointRead(new[] { Id })), Id, Value("1")).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.PointRead(new[] { Id })), Kind, Present).Should().BeFalse("a point read applies no predicate");
            Knows(scope.Facts(OracleLicence.Select), X, Present).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.Declaration), Kind, Present).Should().BeFalse();
        }

        /// <summary>
        /// #114: a conjunct's own facts never license its deletion.
        /// </summary>
        [Fact]
        public void ADeletionIsNeverLicensedByTheConjunctItself()
        {
            var scope = Admitted(Theory(), Eq(Text(Kind), "a"));

            scope.Decide(0, OracleLicence.Select).Should().Be(Truth.Always, "beneath itself it holds, which licenses nothing");
            scope.Decide(0, OracleLicence.DeleteConjunct(0)).Should().Be(Truth.Unknown);
            scope.Decide(0, OracleLicence.Siblings(0)).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void TwoDuplicatesDoNotDeleteEachOther()
        {
            var scope = Declared(Theory(), Eq(Text(Kind), "a"), Eq(Text(Kind), "a"));

            scope.Decide(0, OracleLicence.DeleteConjunct(0)).Should().Be(Truth.Always, "its duplicate is a sibling");
            scope.RedundantConjuncts().Should().Equal(0);
        }

        /// <summary>
        /// A predicate given is a predicate decided, whatever the atoms in it can be said to mean: an opaque one, and an
        /// equality of two numbers the oracle declines to call equal across numeric types.
        /// </summary>
        /// <remarks>
        /// The split rule's test of a weakening already beneath a filter is this question, and what it asks about is
        /// almost always opaque — a definedness test beside a type test. Unanswered, a planner reading the oracle pushes
        /// the same weakening under itself for ever, which is what <c>AlreadyBelow</c> was written against.
        /// </remarks>
        [Fact]
        public void APredicateGivenIsDecidedWhateverItsAtomsMean()
        {
            var raw = Eq(Raw(X), 30m);
            var scope = Declared(Theory(), Opaque(0), Opaque(0), raw);

            Given(Opaque(0)).Decide(Opaque(0), OracleLicence.Kept).Should().Be(Truth.Always, "what is given holds of every row");
            Given(raw).Decide(raw, OracleLicence.Kept).Should().Be(Truth.Always);
            Given(Opaque(0)).Decide(Not(Opaque(0)), OracleLicence.Kept).Should().Be(Truth.Never, "and its negation of none");
            Given(Opaque(0)).Decide(Opaque(1), OracleLicence.Kept).Should().Be(Truth.Unknown, "another opaque predicate is another predicate");
            scope.RedundantConjuncts().Should().Equal(0);
            scope.Decide(0, OracleLicence.DeleteConjunct(0)).Should().Be(Truth.Always, "its duplicate is a sibling");
            scope.Decide(2, OracleLicence.DeleteConjunct(2)).Should().Be(Truth.Unknown, "and nothing licenses a conjunct's deletion by itself");
        }

        [Fact]
        public void AConjunctItsSiblingsImplyIsRedundant()
        {
            var scope = Declared(Theory(), Eq(Text(Kind), "a"), In(Text(Kind), "a", "b"), Cmp(Text(X), ">", Lit("m")));

            scope.RedundantConjuncts().Should().Equal(1);
            Given(Eq(Text(Kind), "a")).Decide(In(Text(Kind), "a", "b"), OracleLicence.Kept).Should().Be(Truth.Always);
            Given(In(Text(Kind), "a", "b")).Decide(Eq(Text(Kind), "a"), OracleLicence.Kept).Should().NotBe(Truth.Always);
            Given(In(Text(Kind), "a", "b")).Decide(Eq(Text(Kind), "c"), OracleLicence.Kept).Should().Be(Truth.Never);
        }

        /// <summary>
        /// A conjunct the strata admit is deleted only where what the service still applies implies it: beside a sibling
        /// left above, whose weakening is all that goes down, the exact test stays.
        /// </summary>
        [Fact]
        public void AnAdmittedConjunctIsDeletedOnlyForWhatReachesTheServiceWithIt()
        {
            // The null test renders; the disequality over a rendering does not, and implies it.
            var statement = new PredicateStatement(new[] { IsNotNull(Text(X)), Ne(Text(X), "zzz") });

            Oracle(Theory()).Admit(statement, (i, _) => i == 0).RedundantConjuncts().Should().BeEmpty("the sibling that implies it is left above");
            Oracle(Theory()).Admit(statement).RedundantConjuncts().Should().Equal(new[] { 0 }, "a conjunct left above is deleted for any sibling");
            Oracle(Theory()).Admit(statement, (_, _) => true).RedundantConjuncts().Should().Equal(new[] { 0 }, "and an admitted one for an admitted sibling");
        }

        /// <summary>
        /// A weakening beside a statement is asked against what it applies: the admitted conjuncts, and what lies beneath.
        /// </summary>
        [Fact]
        public void AWeakeningIsAskedAgainstWhatTheStatementApplies()
        {
            var statement = new PredicateStatement(new[] { Eq(Text(Kind), "a"), Opaque(0) });
            var scope = Oracle(Theory()).Admit(statement, (i, _) => i == 0);

            scope.Decide(In(Text(Kind), "a", "b"), OracleLicence.Applied(Array.Empty<SqlPredicate>())).Should().Be(Truth.Always, "the admitted equality implies it");
            scope.Decide(Opaque(0), OracleLicence.Applied(Array.Empty<SqlPredicate>())).Should().NotBe(Truth.Always, "a conjunct left above is not applied");
            scope.Decide(Opaque(1), OracleLicence.Applied(new[] { Opaque(1) })).Should().Be(Truth.Always, "what lies beneath is");
        }

        static CosmosStatementScope Given(params SqlPredicate[] given) => Oracle(Theory()).Admit(new PredicateStatement(given));

        [Fact]
        public void APredicateTheStatementDoesNotHoldIsDecidedInTheScopeItsLicenceNames()
        {
            var scope = Admitted(Theory(), Eq(Text(Kind), "a"));

            scope.Decide(Ne(Text(Kind), "b"), OracleLicence.Select).Should().Be(Truth.Always);
            scope.Decide(Ne(Text(Kind), "b"), OracleLicence.Declaration).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AStatementIsAnsweredFromOneTreeWhateverTheOrderOfItsConjuncts()
        {
            var oracle = Oracle(Theory());
            var first = oracle.Admit(new PredicateStatement(new[] { Eq(Text(Kind), "a"), IsNotNull(Text(A)) }));
            var second = oracle.Admit(new PredicateStatement(new[] { IsNotNull(Text(A)), Eq(Text(Kind), "a") }));

            second.Facts(OracleLicence.Declaration).Should().BeSameAs(first.Facts(OracleLicence.Declaration));
            second.Facts(OracleLicence.DeleteConjunct(0)).Should().BeSameAs(first.Facts(OracleLicence.DeleteConjunct(1)));
        }

        // The invariants.

        /// <summary>
        /// A stratum whose run stops on a limit is read as its parent: it knows nothing more, and nothing is admitted
        /// beneath it.
        /// </summary>
        [Fact]
        public void ARunThatDoesNotCompleteKnowsNothingMore()
        {
            var oracle = new CosmosOracle(Theory(), null, new RunOptions { StopOnContradiction = true, IterationLimit = 1 });
            var scope = oracle.Admit(new PredicateStatement(new[] { Eq(Text(A), "x"), IsNotNull(Text(Kind)) }), (i, facts) => true);

            scope.Complete.Should().BeFalse();
            Knows(scope.Facts(OracleLicence.Select), A, Value("x")).Should().BeFalse();
            scope.Decide(IsNotNull(Text(A)), OracleLicence.Select).Should().Be(Truth.Unknown, "nothing is read of a run that did not finish");
            scope.Decide(Eq(Text(A), "x"), OracleLicence.Select).Should().Be(Truth.Always,
                "what a stratum assumes holds of every row it admits, read off the assumption and not the run");
        }

        /// <summary>
        /// A predicate a scope assumes, or a conjunction of what it assumes, is always true there however its run ends:
        /// the question a weakening beneath asks, whose answer must never be no for want of a run, or the same weakening
        /// is pushed under itself for ever.
        /// </summary>
        [Fact]
        public void WhatAScopeAssumesIsAlwaysTrueThereHoweverItsRunEnds()
        {
            var oracle = new CosmosOracle(Theory(), null, new RunOptions { StopOnContradiction = true, IterationLimit = 1 });
            var weakening = And(Opaque(0), Opaque(1));
            var scope = oracle.Admit(new PredicateStatement(new[] { Opaque(0), Opaque(1), Eq(Text(A), "x"), weakening }));

            scope.Decide(3, OracleLicence.Siblings(3)).Should().Be(Truth.Always);
            scope.Decide(And(Opaque(0), Opaque(2)), OracleLicence.Siblings(3)).Should().NotBe(Truth.Always, "opaque#2 is not assumed");
        }

        [Fact]
        public void EveryRunCompletesOnAnOrdinaryStatement()
        {
            var scope = Admitted(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))), In(Text(Kind), "a", "b"), Or(Eq(Text(A), "x"), Eq(Text(X), "y")));

            scope.Complete.Should().BeTrue();
            scope.Reports.Should().OnlyContain(r => r.StopReason == RunStopReason.Completed);
        }

        /// <summary>
        /// The planner's oracle reasons in the graphs its declaration compiles into, so a declaration compiles once whoever
        /// asks it; a test's reasons in graphs of its own.
        /// </summary>
        [Fact]
        public void ThePlannersOracleReasonsInThePlannersGraphs()
        {
            var theory = Theory(Outright(Kind, Present));
            CosmosOracle.For(theory).Theory.Should().BeSameAs(theory);
            CosmosOracle.For(theory).Should().BeSameAs(CosmosOracle.For(theory));
            Oracle(theory).Theory.Generations.Should().NotBeSameAs(theory.Generations);
        }

        /// <summary>
        /// Every conjunct, for a strengthening; and an arm of a <c>CASE</c>, its siblings and its conditions, never its own
        /// conjunct.
        /// </summary>
        [Fact]
        public void TheRowsAFilterKeepsAndTheArmOfACase()
        {
            var theory = Theory(Outright(Kind, Enum("a", "b")));
            var scope = Oracle(theory).Admit(new PredicateStatement(new[] { Eq(Text(Kind), "a"), Eq(Text(A), "x") }));

            scope.AssumedIn(OracleLicence.Kept).Should().Equal(0, 1);
            Knows(scope.Facts(OracleLicence.Kept), Kind, Value("a")).Should().BeTrue();

            var arm = OracleLicence.Arm(1, new[] { Eq(Text(X), "y") });
            scope.AssumedIn(arm).Should().Equal(0);
            Knows(scope.Facts(arm), Kind, Value("a")).Should().BeTrue("a sibling holds wherever the arm's conjunct is decided");
            Knows(scope.Facts(arm), X, Value("y")).Should().BeTrue("the condition holds wherever the arm is read");
            Knows(scope.Facts(arm), A, Present).Should().BeFalse("the conjunct the arm is part of is not its own licence");

            Oracle(theory).Admit(new PredicateStatement(new[] { Eq(Text(Kind), "c") })).IsContradictory(OracleLicence.Kept).Should().BeTrue();
        }

        // A container that declares no schema.

        /// <summary>
        /// What a common filter over a container that declares no schema keeps is its stratum's context: what the filter
        /// states, what the rules of JSON derive from it, and what the service guarantees, read from one context.
        /// </summary>
        [Fact]
        public void ACommonFilterOverAnUndeclaredContainerKeepsWhatItsStratumDerives()
        {
            var keeps = Oracle(Undeclared).Admit(new PredicateStatement(new[] { Eq(Raw(A), "k0"), Cmp(Cast("DOUBLE", Text(B)), ">", Lit(500m)) }))
                .Facts(OracleLicence.Kept);

            Knows(keeps, A, Type(JsonType.String)).Should().BeTrue("a value is of its own type");
            Knows(keeps, A, new JsonClaim.NotEqualTo("k1")).Should().BeTrue();
            Knows(keeps, B, new JsonClaim.LowerBound(400)).Should().BeTrue("the cast's window bounds the number it reads");
            Knows(keeps, Id, Type(JsonType.String)).Should().BeTrue("what the service states holds beneath the filter");
            Knows(keeps, A.Property("x"), Present).Should().BeFalse("a string has no properties");
            keeps.IsContradictory.Should().BeFalse();
        }

        /// <summary>
        /// A filter that meets what the service states is reasoned about with it: a property of <c>id</c> is the property
        /// of a string, which no document has.
        /// </summary>
        [Fact]
        public void APropertyOfWhatTheServiceStatesAScalarKeepsNoDocument()
        {
            Oracle(Undeclared).Admit(new PredicateStatement(new[] { new SqlPredicate.IsDefined(Raw(Id.Property("q"))) }))
                .IsContradictory(OracleLicence.Kept).Should().BeTrue();
        }

    }

}
