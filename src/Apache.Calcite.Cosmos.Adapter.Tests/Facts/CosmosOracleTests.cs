using System;
using System.Collections.Generic;
using System.Linq;

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
    /// a question is answered from, redundancy, and what an incomplete run is read as.
    /// </summary>
    public sealed class CosmosOracleTests
    {

        static readonly JsonDocumentPath A = P("a");
        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Label = P("label");
        static readonly JsonDocumentPath Id = P("id");
        static readonly JsonDocumentPath X = P("x");

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
            scope.AssumedIn(OracleLicence.DeleteConjunct(1)).Should().Equal(0, 2, 3);
            scope.AssumedIn(OracleLicence.Redundant(3)).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.PointRead(new[] { Id })).Should().Equal(0);
            scope.AssumedIn(OracleLicence.Select).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.OrderBy).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.GroupBy).Should().Equal(0, 1, 2);
            scope.AssumedIn(OracleLicence.Residual).Should().BeEmpty();
            scope.AssumedIn(OracleLicence.Route).Should().BeEmpty();

            // And the facts each scope holds are those of the conjuncts it names.
            Knows(scope.Facts(OracleLicence.RenderConjunct(1)), Kind, Value("a")).Should().BeFalse();
            Knows(scope.Facts(OracleLicence.RenderConjunct(2)), Kind, Value("a")).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.DeleteConjunct(1)), Kind, Present).Should().BeFalse();
            Knows(scope.Facts(OracleLicence.DeleteConjunct(1)), X, Present).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.PointRead(new[] { Id })), Id, Value("1")).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.PointRead(new[] { Id })), Kind, Present).Should().BeFalse("a point read applies no predicate");
            Knows(scope.Facts(OracleLicence.Select), X, Present).Should().BeTrue();
            Knows(scope.Facts(OracleLicence.Residual), Kind, Present).Should().BeFalse();
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
            scope.Decide(0, OracleLicence.Redundant(0)).Should().Be(Truth.Unknown);
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

            scope.IsRedundant(Opaque(0), new[] { Opaque(0) }).Should().BeTrue("what is given holds of every row");
            scope.IsRedundant(raw, new[] { raw }).Should().BeTrue();
            scope.DecideGiven(Not(Opaque(0)), new[] { Opaque(0) }).Should().Be(Truth.Never, "and its negation of none");
            scope.DecideGiven(Opaque(1), new[] { Opaque(0) }).Should().Be(Truth.Unknown, "another opaque predicate is another predicate");
            scope.RedundantConjuncts().Should().Equal(0);
            scope.Decide(0, OracleLicence.DeleteConjunct(0)).Should().Be(Truth.Always, "its duplicate is a sibling");
            scope.Decide(2, OracleLicence.DeleteConjunct(2)).Should().Be(Truth.Unknown, "and nothing licenses a conjunct's deletion by itself");
        }

        [Fact]
        public void AConjunctItsSiblingsImplyIsRedundant()
        {
            var scope = Declared(Theory(), Eq(Text(Kind), "a"), In(Text(Kind), "a", "b"), Cmp(Text(X), ">", Lit("m")));

            scope.RedundantConjuncts().Should().Equal(1);
            scope.IsRedundant(In(Text(Kind), "a", "b"), new[] { Eq(Text(Kind), "a") }).Should().BeTrue();
            scope.IsRedundant(Eq(Text(Kind), "a"), new[] { In(Text(Kind), "a", "b") }).Should().BeFalse();
            scope.DecideGiven(Eq(Text(Kind), "c"), new[] { In(Text(Kind), "a", "b") }).Should().Be(Truth.Never);
        }

        [Fact]
        public void APredicateTheStatementDoesNotHoldIsDecidedInTheScopeItsLicenceNames()
        {
            var scope = Admitted(Theory(), Eq(Text(Kind), "a"));

            scope.Decide(Ne(Text(Kind), "b"), OracleLicence.Select).Should().Be(Truth.Always);
            scope.Decide(Ne(Text(Kind), "b"), OracleLicence.Residual).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AStatementIsAnsweredFromOneTreeWhateverTheOrderOfItsConjuncts()
        {
            var oracle = Oracle(Theory());
            var first = oracle.Admit(new PredicateStatement(new[] { Eq(Text(Kind), "a"), IsNotNull(Text(A)) }));
            var second = oracle.Admit(new PredicateStatement(new[] { IsNotNull(Text(A)), Eq(Text(Kind), "a") }));

            second.Facts(OracleLicence.Residual).Should().BeSameAs(first.Facts(OracleLicence.Residual));
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
            scope.Decide(Eq(Text(A), "x"), OracleLicence.Select).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void EveryRunCompletesOnAnOrdinaryStatement()
        {
            var scope = Admitted(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))), In(Text(Kind), "a", "b"), Or(Eq(Text(A), "x"), Eq(Text(X), "y")));

            scope.Complete.Should().BeTrue();
            scope.Reports.Should().OnlyContain(r => r.StopReason == RunStopReason.Completed);
        }

        [Fact]
        public void TheOracleReasonsInGraphsApartFromThePlanners()
        {
            var theory = Theory(Outright(Kind, Present));
            CosmosOracle.For(theory).Theory.Generations.Should().NotBeSameAs(theory.Generations);
            CosmosOracle.For(theory).Should().BeSameAs(CosmosOracle.For(theory));
        }

    }

}
