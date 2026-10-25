using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// Admission asked once a round (spec g12): each round handed whole, the stratum a later round's atoms may be stated
    /// beneath, and S's admissions standing only where their composite is proven.
    /// </summary>
    public sealed class AdmissionRoundTests
    {

        static readonly JsonDocumentPath A = P("a");
        static readonly JsonDocumentPath B = P("b");
        static readonly JsonDocumentPath C = P("c");

        static bool Knows(JsonFactSet facts, JsonDocumentPath path, JsonClaim claim) => facts.Knows(new JsonFact(path, claim));

        /// <summary>
        /// A round is asked once, of every pending conjunct in index order and from its node, and admits as the conjunct
        /// asked alone did; with nothing of S's, nothing is asked after the last round.
        /// </summary>
        [Fact]
        public void EachRoundIsAskedOnceAndAdmitsAsEachConjunctDid()
        {
            var statement = new PredicateStatement(new[] { Eq(Text(A), "x"), IsNotNull(Text(A)) });
            var rounds = new List<AdmissionRound>();
            var whole = Oracle(Theory()).Admit(statement, round =>
            {
                rounds.Add(round);
                return new([.. round.Pending.Where(i => i == 1 || Knows(round.Node.View, A, Present))], []);
            });
            var each = Oracle(Theory()).Admit(statement, (i, facts) => i == 1 || Knows(facts, A, Present));

            whole.Strata.Should().BeEquivalentTo(each.Strata, o => o.WithStrictOrdering());
            rounds.Select(r => (r.Number, Pending: string.Join(",", r.Pending), r.Again, r.Closing)).Should().Equal((1, "0,1", false, false), (2, "0", false, false));
            rounds.Should().OnlyContain(r => r.AdmittedByS.Count == 0 && r.Pending.All(i => ReferenceEquals(r.NodeFor(i), r.Node)));
        }

        /// <summary>
        /// A later round's atoms are stated beneath the deepest node every conjunct of which it assumes is S's and read
        /// exactly: never beneath one that assumes a raw value compared with a number, which the facts graph reads coarsely,
        /// nor one that assumes a conjunct the bundle does not hold; the root otherwise. Asked while the rounds run, and
        /// after, alike.
        /// </summary>
        [Fact]
        public void StratumForStatesNothingBeneathACoarseStratum()
        {
            var (exact, other, coarse) = (Eq(Text(A), "x"), Eq(Text(B), "y"), Eq(Raw(B), 30m));

            // Rounds 1 to 3 each admit one conjunct, S's, so round k's node assumes conjuncts 0 to k - 2.
            (CosmosStatementScope Scope, AdmissionRound[] Rounds, StatementTerms.Node Stated) Three(SqlPredicate first, SqlPredicate second, Func<int, bool> inBundle)
            {
                CosmosStatementScope? scope = null;
                var rounds = new List<AdmissionRound>();
                StatementTerms.Node? stated = null;
                scope = Oracle(Theory()).Admit(new PredicateStatement(new[] { first, second, Eq(Text(C), "z") }), round =>
                {
                    rounds.Add(round);
                    if (round is { Number: 3, Closing: false })
                        stated = scope!.StratumFor(round.Node, inBundle);

                    return round.Closing ? new([], round.AdmittedByS) : new([], [round.Number - 1]);
                });

                scope.Strata.Select(s => s.Single()).Should().Equal(0, 1, 2);
                return (scope, [.. rounds.Where(r => r.Closing == false)], stated!);
            }

            var (scope, rounds, stated) = Three(exact, other, _ => true);
            var elsewhere = rounds[2].Node;
            rounds.Select(r => r.Node).Distinct().Should().HaveCount(3, "each round renders from a stratum of its own");
            stated.Should().BeSameAs(rounds[2].Node);
            scope.StratumFor(rounds[2].Node, _ => true).Should().BeSameAs(rounds[2].Node, "asked after the rounds, alike");

            // Round 3's node assumes the coarse conjunct, round 2's only the exact one.
            (scope, rounds, stated) = Three(exact, coarse, _ => true);
            stated.Should().BeSameAs(rounds[1].Node);
            scope.StratumFor(rounds[1].Node, _ => true).Should().BeSameAs(rounds[1].Node);

            // Beneath a coarse first round, nothing.
            (scope, rounds, stated) = Three(coarse, exact, _ => true);
            stated.Should().BeSameAs(rounds[0].Node, "the root");

            // A conjunct the bundle does not hold, the translator's, rests on no proof S has.
            (scope, rounds, stated) = Three(exact, other, i => i != 0);
            stated.Should().BeSameAs(rounds[0].Node, "the root");
            scope.StratumFor(rounds[0].Node, _ => false).Should().BeSameAs(rounds[0].Node, "the root qualifies always");
            scope.StratumFor(elsewhere, _ => true).Should().BeSameAs(rounds[0].Node, "a node not of the chain reads as the root");
        }

        /// <summary>
        /// S's admissions stand only where their composite is proven: asked once, after the last round, of what S admitted and
        /// what is still residual; not proven, the rounds run again with S admitting nothing, and what the translator renders
        /// is all that is admitted.
        /// </summary>
        [Fact]
        public void AnUnprovenCompositeAdmitsAgainWithSAdmittingNothing()
        {
            var statement = new PredicateStatement(new[] { Eq(Text(A), "x"), Eq(Text(B), "y"), Eq(Text(C), "z") });

            // Round 1: S proves conjunct 0 and the translator renders 1; conjunct 2 renders nowhere.
            (CosmosStatementScope Scope, List<AdmissionRound> Rounds) Admit(bool proven)
            {
                var rounds = new List<AdmissionRound>();
                var scope = Oracle(Theory()).Admit(statement, round =>
                {
                    rounds.Add(round);
                    return round switch
                    {
                        { Closing: true } => new([], proven ? round.AdmittedByS : []),
                        { Number: 1 } => new([1], [0]),
                        _ => AdmissionRound.Answer.None,
                    };
                });

                _ = scope.Strata;
                return (scope, rounds);
            }

            var (stands, asked) = Admit(proven: true);
            stands.Strata.Should().ContainSingle().Which.Should().Equal(0, 1);
            stands.Residual.Should().Equal(2);
            var closing = asked.Should().ContainSingle(r => r.Closing).Subject;
            closing.AdmittedByS.Should().Equal(0);
            closing.Pending.Should().Equal(2);
            asked.Should().NotContain(r => r.Again);

            var (voided, again) = Admit(proven: false);
            voided.Strata.Should().ContainSingle().Which.Should().Equal(new[] { 1 }, "S admits nothing on the rounds run again, whatever it says");
            voided.Residual.Should().Equal(0, 2);
            again.Select(r => (r.Number, r.Again, r.Closing, ByS: string.Join(",", r.AdmittedByS)))
                .Should().Equal((1, false, false, ""), (2, false, false, "0"), (2, false, true, "0"), (1, true, false, ""), (2, true, false, ""));
        }

    }

}
