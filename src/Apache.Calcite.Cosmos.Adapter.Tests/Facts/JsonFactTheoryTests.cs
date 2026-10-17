using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

using StatementPath = Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// The compiled schema, asked questions the way a rule asks them.
    /// </summary>
    /// <remarks>
    /// Two things are being pinned. That a guarded fact is unusable until its guard is proven, which is
    /// the whole soundness argument for a container holding more than one kind of document; and that a
    /// known value settles type, membership and disequality — but never presence, which a claim about a
    /// value does not make.
    /// </remarks>
    public class JsonFactTheoryTests
    {

        static readonly JsonDocumentPath Type = JsonDocumentPath.Root.Property("type");
        static readonly JsonDocumentPath ParkId = JsonDocumentPath.Root.Property("data").Property("parkId");
        static readonly JsonDocumentPath At = JsonDocumentPath.Root.Property("data").Property("at");

        // Stand-ins rather than rows out of CosmosStoredForms: what a theory does with a
        // representation is carry it, so the pair is chosen to be two distinguishable values with
        // the two licences between them, and nothing here turns on which pattern yields either.
        static readonly CosmosRepresentation EqualityOnly = new("equality-only", PreservesEquality: true, PreservesOrder: false);
        static readonly CosmosRepresentation Ordered = new("ordered", PreservesEquality: true, PreservesOrder: true);

        static JsonFact Equals(JsonDocumentPath path, JsonScalar value) => new(path, new JsonClaim.EqualTo(value));

        static JsonFact Represents(JsonDocumentPath path, CosmosRepresentation representation) => new(path, new JsonClaim.Represents(representation));

        /// <summary>
        /// A null entails every claim that admits one, and nothing that does not. #175.
        /// </summary>
        [Fact]
        public void ANullEntailsEveryClaimThatAdmitsOne()
        {
            var isNull = new JsonFact(ParkId, new JsonClaim.OfType(JsonType.Null));
            var equalsNull = Equals(ParkId, null);

            foreach (var fact in new[] { isNull, equalsNull })
            {
                fact.Entails(Represents(ParkId, EqualityOnly)).Should().BeTrue("a form says how the strings are written, not that one is there");
                fact.Entails(new JsonFact(ParkId, new JsonClaim.OfType(JsonType.String, OrNull: true))).Should().BeTrue();
                fact.Entails(new JsonFact(ParkId, new JsonClaim.OfType(JsonType.String))).Should().BeFalse();
                fact.Entails(new JsonFact(ParkId, new JsonClaim.Present())).Should().BeFalse("a claim about a value says nothing about its being there");
            }

            isNull.Entails(equalsNull).Should().BeTrue();
            isNull.Entails(new JsonFact(ParkId, new JsonClaim.OneOf(new JsonScalar[] { "a", null }))).Should().BeTrue();
            isNull.Entails(new JsonFact(ParkId, new JsonClaim.OneOf(new JsonScalar[] { "a" }))).Should().BeFalse();
            isNull.Entails(new JsonFact(ParkId, new JsonClaim.NotEqualTo("a"))).Should().BeTrue();
            isNull.Entails(new JsonFact(ParkId, new JsonClaim.NotEqualTo(null))).Should().BeFalse();
        }

        [Fact]
        public void AnUnconditionalFactHoldsWithNothingEstablished()
        {
            var theory = new JsonFactTheory(new[] { JsonFactRule.Unconditional(Represents(ParkId, EqualityOnly)) });

            theory.Derive(null).RepresentationOf(ParkId).Should().Be(EqualityOnly);
        }

        [Fact]
        public void AGuardedFactIsUnusableUntilItsGuardIsProven()
        {
            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { Equals(Type, "ParkMap") }, Represents(ParkId, EqualityOnly)),
            });

            theory.Derive(null).RepresentationOf(ParkId).Should().BeNull(
                "a Park may not even carry the path, so nothing about it is known until the discriminator is");

            theory.Derive(new[] { Equals(Type, "Park") }).RepresentationOf(ParkId).Should().BeNull(
                "the wrong discriminator proves nothing");

            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(ParkId).Should().Be(EqualityOnly);
        }

        [Fact]
        public void AKnownValueSettlesPresenceTypeMembershipAndDisequality()
        {
            var set = JsonFactTheory.Empty.Derive(new[] { Equals(Type, "ParkMap") });

            set.Knows(new JsonFact(Type, new JsonClaim.OfType(JsonType.String))).Should().BeTrue();
            set.Knows(new JsonFact(Type, new JsonClaim.NotEqualTo("Park"))).Should().BeTrue(
                "an else branch's guard is discharged by an equality to a different value, with no closed world needed");
            set.Knows(new JsonFact(Type, new JsonClaim.NotEqualTo("ParkMap"))).Should().BeFalse();
            set.Knows(new JsonFact(Type, new JsonClaim.OneOf(new JsonScalar[] { "Park", "ParkMap" }))).Should().BeTrue();
            set.Knows(new JsonFact(Type, new JsonClaim.OneOf(new JsonScalar[] { "Park", "Trail" }))).Should().BeFalse();
            set.Knows(new JsonFact(Type, new JsonClaim.OfType(JsonType.Integer))).Should().BeFalse();
        }

        [Fact]
        public void ADomainSettlesWhatEveryMemberAgreesOn()
        {
            var set = JsonFactTheory.Empty.Derive(new[] { new JsonFact(Type, new JsonClaim.OneOf(new JsonScalar[] { "Park", "ParkMap" })) });

            set.Knows(new JsonFact(Type, new JsonClaim.OfType(JsonType.String))).Should().BeTrue(
                "every member is a string, so the type is settled even though the value is not");
            set.Knows(new JsonFact(Type, new JsonClaim.NotEqualTo("Trail"))).Should().BeTrue();
            set.Knows(new JsonFact(Type, new JsonClaim.NotEqualTo("Park"))).Should().BeFalse();
            set.Knows(Equals(Type, "Park")).Should().BeFalse("a domain is not a value");
        }

        [Fact]
        public void AGuardIsSatisfiedThroughSubsumptionRatherThanLiterally()
        {
            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { new JsonFact(Type, new JsonClaim.OneOf(new JsonScalar[] { "Park", "ParkMap" })) }, Represents(ParkId, EqualityOnly)),
            });

            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(ParkId).Should().Be(EqualityOnly,
                "an equality establishes membership, so the guard holds without the query having said so");
        }

        /// <summary>
        /// A claim about a value says nothing about whether the path has one.
        /// </summary>
        /// <remarks>
        /// A schema's <c>properties</c> constrains what a path holds if it holds anything; only
        /// <c>required</c> says it holds something. Reading the first as the second would claim of
        /// every document what the schema claimed of none — and a grouping key drops its
        /// <c>IS_DEFINED</c> normalisation on the strength of it.
        /// </remarks>
        [Fact]
        public void NoClaimAboutAValueImpliesThePathHasOne()
        {
            var set = JsonFactTheory.Empty.Derive(new[]
            {
                Equals(Type, "ParkMap"),
                Represents(ParkId, EqualityOnly),
                new JsonFact(At, new JsonClaim.OfType(JsonType.String)),
            });

            set.Knows(new JsonFact(Type, new JsonClaim.Present())).Should().BeFalse();
            set.Knows(new JsonFact(ParkId, new JsonClaim.Present())).Should().BeFalse();
            set.Knows(new JsonFact(At, new JsonClaim.Present())).Should().BeFalse();

            JsonFactTheory.Empty.Derive(new[] { new JsonFact(ParkId, new JsonClaim.Present()) })
                .Knows(new JsonFact(ParkId, new JsonClaim.Present())).Should().BeTrue("which required states outright");
        }

        [Fact]
        public void ChainingReachesAFactWhoseGuardIsItselfDerived()
        {
            // if type = 'ParkMap' then the kind is 'v2'; if the kind is 'v2' then the instant is fixed shape.
            var kind = JsonDocumentPath.Root.Property("kind");

            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { Equals(Type, "ParkMap") }, Equals(kind, "v2")),
                new JsonFactRule(new[] { Equals(kind, "v2") }, Represents(At, Ordered)),
            });

            theory.Derive(null).RepresentationOf(At).Should().BeNull();
            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(At).Should().Be(Ordered,
                "the second rule's body is satisfied by the first rule's head, which is what chaining is for");
        }

        [Fact]
        public void EveryAtomOfAConjunctiveBodyHasToHold()
        {
            var version = JsonDocumentPath.Root.Property("v");

            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { Equals(Type, "ParkMap"), Equals(version, 2) }, Represents(At, Ordered)),
            });

            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(At).Should().BeNull();
            theory.Derive(new[] { Equals(version, 2) }).RepresentationOf(At).Should().BeNull();
            theory.Derive(new[] { Equals(Type, "ParkMap"), Equals(version, 2) }).RepresentationOf(At).Should().Be(Ordered);
        }

        [Fact]
        public void TheStrongestRepresentationWins()
        {
            var theory = new JsonFactTheory(new[]
            {
                JsonFactRule.Unconditional(Represents(At, new CosmosRepresentation("iso8601-loose", PreservesEquality: true, PreservesOrder: false))),
                JsonFactRule.Unconditional(Represents(At, Ordered)),
            });

            theory.Derive(null).RepresentationOf(At).Should().Be(Ordered,
                "both were proven, and the caller wants the one that licenses the most");
        }

        [Fact]
        public void MutuallyDependentRulesTerminate()
        {
            var a = JsonDocumentPath.Root.Property("a");
            var b = JsonDocumentPath.Root.Property("b");

            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { Equals(a, 1) }, Equals(b, 2)),
                new JsonFactRule(new[] { Equals(b, 2) }, Equals(a, 1)),
            });

            var set = theory.Derive(new[] { Equals(a, 1) });

            set.Knows(Equals(b, 2)).Should().BeTrue();
            theory.Derive(null).Knows(Equals(a, 1)).Should().BeFalse("neither rule can start itself");
        }

        [Fact]
        public void ADocumentPathIsTheStatementsPathWithoutTheAlias()
        {
            var path = StatementPath.Root("c").Property("data").Property("parkId");

            CosmosDocumentPaths.From(path).Should().Be(ParkId);
            CosmosDocumentPaths.From(StatementPath.Root("x").Property("data").Property("parkId")).Should().Be(ParkId,
                "the alias is the statement's business and no part of what a schema declares");

            CosmosDocumentPaths.From(StatementPath.Root("c").Property("tags").Index(0)).Should().BeNull(
                "an element is not its array, and conflating them would apply one's facts to the other");
        }

        /// <summary>
        /// An integer is a number: a whole value, a domain of them, and the narrower type all establish
        /// <c>number</c>, and none of them excludes it. #217.
        /// </summary>
        [Fact]
        public void AnIntegerIsANumber()
        {
            var number = new JsonFact(At, new JsonClaim.OfType(JsonType.Number));
            var nullableNumber = new JsonFact(At, new JsonClaim.OfType(JsonType.Number, OrNull: true));

            foreach (var fact in new[]
            {
                Equals(At, 2L),
                new JsonFact(At, new JsonClaim.OneOf(new JsonScalar[] { 1L, 2L })),
                new JsonFact(At, new JsonClaim.OfType(JsonType.Integer)),
            })
            {
                fact.Entails(number).Should().BeTrue($"{fact.Claim} is a number");
                fact.Entails(nullableNumber).Should().BeTrue($"{fact.Claim} is a number, which a nullable number admits");
                JsonFactTheory.Empty.Derive(new[] { fact, Present(At), number }).IsContradictory.Should().BeFalse($"{fact.Claim} is a number, so a path declared one may hold it");
            }

            Equals(At, 1.5d).Entails(number).Should().BeTrue();
        }

        /// <summary>
        /// The lattice runs one way: a number need not be whole, and a fraction is not an integer.
        /// </summary>
        [Fact]
        public void ANumberIsNotNecessarilyAnInteger()
        {
            var integer = new JsonFact(At, new JsonClaim.OfType(JsonType.Integer));

            new JsonFact(At, new JsonClaim.OfType(JsonType.Number)).Entails(integer).Should().BeFalse();
            new JsonFact(At, new JsonClaim.OfType(JsonType.Integer, OrNull: true))
                .Entails(new JsonFact(At, new JsonClaim.OfType(JsonType.Number))).Should().BeFalse("admitting a null is weaker than not admitting one");

            Contradicts(Equals(At, 1.5d), integer).Should().BeTrue("a value with a fractional part is no integer");
            Contradicts(Equals(At, "2"), new JsonFact(At, new JsonClaim.OfType(JsonType.Number))).Should().BeTrue("a string is no number, whatever it spells");
            Contradicts(new JsonFact(At, new JsonClaim.OneOf(new JsonScalar[] { 1.5d, 2.5d })), integer).Should().BeTrue();
        }

        /// <summary>
        /// Two claims no one value satisfies say that the path is absent, and contradict each other only beside a
        /// presence. F3: a declared enum and a type outside it are a schema saying the property is never there, which
        /// a document satisfies by not having it — so no query over the container is answered as empty for it.
        /// </summary>
        [Fact]
        public void TwoClaimsNoValueSatisfiesProveAbsenceAndNotAContradiction()
        {
            var number = new JsonFact(At, new JsonClaim.OfType(JsonType.Number));
            var set = JsonFactTheory.Empty.Derive(new[] { Equals(At, "2"), number });

            set.IsContradictory.Should().BeFalse("a document without the path satisfies both");
            set.Knows(new JsonFact(At, new JsonClaim.Present())).Should().BeFalse();
            set.Knows(Equals(At, "anything")).Should().BeTrue("an absent path satisfies every claim about its value");

            var declared = new JsonFactTheory(new[]
            {
                JsonFactRule.Unconditional(new JsonFact(Type, new JsonClaim.OneOf(new JsonScalar[] { "Park", "ParkMap" }))),
                JsonFactRule.Unconditional(new JsonFact(Type, new JsonClaim.OfType(JsonType.Integer))),
            });

            declared.Derive(null).IsContradictory.Should().BeFalse("the declaration admits every document without a type");
            declared.Derive(new[] { new JsonFact(Type, new JsonClaim.Present()) }).IsContradictory.Should().BeTrue("and none with one");
        }

        /// <summary>
        /// A pinned value is read back as the scalar it is, and only where exactly one value is possible.
        /// </summary>
        [Fact]
        public void APinnedValueIsReadBackAsItsScalar()
        {
            foreach (var value in new JsonScalar[] { "ParkMap", 42L, 1.5m, true, JsonScalar.Null, 1e300 })
            {
                var set = JsonFactTheory.Empty.Derive(new[] { Equals(At, value) });

                set.TryGetValue(At, out var read).Should().BeTrue($"{value} is pinned");
                read.Should().Be(value);
            }

            JsonFactTheory.Empty.Derive(new[] { new JsonFact(At, new JsonClaim.OneOf(new JsonScalar[] { "a", "b" })) })
                .TryGetValue(At, out _).Should().BeFalse("two values are possible");
            JsonFactTheory.Empty.Derive(new[] { Present(At) }).TryGetValue(At, out _).Should().BeFalse("nothing is known of the value");
            JsonFactTheory.Empty.Derive(new[] { Present(At) }).TryGetValue(Type, out _).Should().BeFalse("nor of a path nothing mentions");
        }

        /// <summary>
        /// A number a decimal cannot hold is one value, of its own type, and no bound or domain loses it.
        /// </summary>
        [Fact]
        public void ANumberBeyondADecimalIsCarried()
        {
            var huge = JsonFactTheory.Empty.Derive(new[] { Equals(At, 1e300), Present(At) });

            huge.IsContradictory.Should().BeFalse();
            huge.Knows(new JsonFact(At, new JsonClaim.OfType(JsonType.Integer))).Should().BeTrue("1e300 is whole");
            huge.Knows(Equals(At, 1e300)).Should().BeTrue();
            huge.Knows(Equals(At, 2e300)).Should().BeFalse("another such number is another value");

            JsonFactTheory.Empty.Derive(new[] { Equals(At, 1e300), Equals(At, 2e300), Present(At) }).IsContradictory.Should().BeTrue();
        }

        /// <summary>
        /// One scalar kind is what makes a path's text a faithful reading of its value; a null beside it reads as no
        /// text, and two kinds read alike.
        /// </summary>
        [Fact]
        public void OneScalarKindIsAskedOfTheTypes()
        {
            JsonFactSet Typed(JsonType type, bool orNull = false) =>
                JsonFactTheory.Empty.Derive(new[] { new JsonFact(At, new JsonClaim.OfType(type, orNull)) });

            Typed(JsonType.String).HasOneScalarType(At).Should().BeTrue();
            Typed(JsonType.String, orNull: true).HasOneScalarType(At).Should().BeTrue("a null reads as no text at all");
            Typed(JsonType.Number).HasOneScalarType(At).Should().BeTrue("an integer and a fraction are one kind");
            Typed(JsonType.Object).HasOneScalarType(At).Should().BeFalse();
            JsonFactTheory.Empty.Derive(new[] { new JsonFact(At, new JsonClaim.OneOf(new JsonScalar[] { "1", 1L })) })
                .HasOneScalarType(At).Should().BeFalse("a string and a number read alike");
            JsonFactTheory.Empty.Derive(new[] { new JsonFact(At, new JsonClaim.OneOf(new JsonScalar[] { "a", "b" })) })
                .HasOneScalarType(At).Should().BeTrue("a domain of strings settles the kind as a declared type does");
        }

        /// <summary>
        /// An extension is a token, and states nothing beside it in JSON's terms: a geography says what the value is
        /// where there is one, and whether one is there is a presence the theory is told of, or derives, as of any path.
        /// </summary>
        [Fact]
        public void AnExtensionStatesNothingButItself()
        {
            var location = JsonDocumentPath.Root.Property("data").Property("location");
            var alone = new JsonFactTheory(new[] { JsonFactRule.Unconditional(new JsonFact(location, new CosmosGeography())) }).Derive(null);

            alone.Knows(new JsonFact(location, new CosmosGeography())).Should().BeTrue();
            alone.Knows(Present(location)).Should().BeFalse("a claim about a value says nothing about its being there");
            alone.Knows(new JsonFact(location, new JsonClaim.OfType(JsonType.Object))).Should().BeFalse("nor what type it is: that is the keyword's to say");
            alone.IsAlwaysGeography(location).Should().BeFalse();

            var required = new JsonFactTheory(new[]
            {
                JsonFactRule.Unconditional(new JsonFact(location, new CosmosGeography())),
                JsonFactRule.Unconditional(Present(location)),
            }).Derive(null);

            required.IsAlwaysGeography(location).Should().BeTrue();
            required.Knows(Present(JsonDocumentPath.Root.Property("data"))).Should().BeTrue("a present property has a present object for a parent");
            required.IsAlwaysGeography(At).Should().BeFalse();
        }

        /// <summary>
        /// A generation that has interned enough is retired, a theory compiles itself into the next, and its answers
        /// do not change; one given out by the retired generation still answers.
        /// </summary>
        [Fact]
        public void ATheoryAnswersAlikeInEveryGeneration()
        {
            var generations = new JsonFactGenerations(internedLimit: 1);
            var theory = new JsonFactTheory(new[] { new JsonFactRule(new[] { Equals(Type, "ParkMap") }, Represents(ParkId, EqualityOnly)) }, generations);

            var first = theory.Derive(new[] { Equals(Type, "ParkMap") });
            var started = generations.Started;
            var second = theory.Derive(new[] { Equals(Type, "ParkMap") });

            generations.Started.Should().BeGreaterThan(started, "the first generation interned more than its limit");
            second.Should().NotBeSameAs(first);
            second.RepresentationOf(ParkId).Should().Be(EqualityOnly);
            first.RepresentationOf(ParkId).Should().Be(EqualityOnly, "a retired generation lives as long as an answer it gave");
            theory.Derive(null).RepresentationOf(ParkId).Should().BeNull();
        }

        /// <summary>
        /// Many threads asking one theory get what one thread gets.
        /// </summary>
        [Fact]
        public void ConcurrentQuestionsAnswerAsOneThreadDoes()
        {
            var theory = new JsonFactTheory(new[] { new JsonFactRule(new[] { Equals(Type, "ParkMap") }, Represents(ParkId, EqualityOnly)) });
            var kinds = new JsonScalar[] { "ParkMap", "Park", "Trail", "Map" };

            var answers = new CosmosRepresentation?[256];
            System.Threading.Tasks.Parallel.For(0, answers.Length, i =>
                answers[i] = theory.Derive(new[] { Equals(Type, kinds[i % kinds.Length]), new JsonFact(At, new JsonClaim.OneOf(new JsonScalar[] { i % 7 })) }).RepresentationOf(ParkId));

            for (var i = 0; i < answers.Length; i++)
                answers[i].Should().Be(i % kinds.Length == 0 ? EqualityOnly : null, $"question {i}");
        }

        /// <summary>
        /// A bound binds numbers. Bounds that cross leave the path no number, which only a path known to hold a
        /// number contradicts.
        /// </summary>
        [Fact]
        public void CrossedBoundsLeaveNoNumber()
        {
            var above = new JsonFact(At, new JsonClaim.LowerBound(499, Exclusive: true));
            var below = new JsonFact(At, new JsonClaim.UpperBound(100));

            var set = JsonFactTheory.Empty.Derive(new[] { above, below, Present(At) });
            set.IsContradictory.Should().BeFalse("a string is no number for either bound to refuse");
            set.Knows(new JsonFact(At, new JsonClaim.NotEqualTo(5L))).Should().BeTrue("no number is there, 5 least of all");

            JsonFactTheory.Empty.Derive(new[] { above, below, Present(At), new JsonFact(At, new JsonClaim.OfType(JsonType.Integer)) })
                .IsContradictory.Should().BeTrue("and an integer there is a number");
        }

        /// <summary>
        /// A bound is known from a stricter one, from a domain every number of which it admits, and from a path that
        /// holds no number.
        /// </summary>
        [Fact]
        public void ABoundIsKnownFromWhatSettlesIt()
        {
            JsonFactSet With(JsonClaim claim) => JsonFactTheory.Empty.Derive(new[] { new JsonFact(At, claim) });

            With(new JsonClaim.LowerBound(5)).Knows(new JsonFact(At, new JsonClaim.LowerBound(3))).Should().BeTrue();
            With(new JsonClaim.LowerBound(5)).Knows(new JsonFact(At, new JsonClaim.LowerBound(5, Exclusive: true))).Should().BeFalse();
            With(new JsonClaim.OneOf(new JsonScalar[] { 4L, 6L, "x" })).Knows(new JsonFact(At, new JsonClaim.LowerBound(3))).Should().BeTrue("a string binds no bound");
            With(new JsonClaim.OneOf(new JsonScalar[] { 4L, 6L })).Knows(new JsonFact(At, new JsonClaim.UpperBound(5))).Should().BeFalse();
            With(new JsonClaim.OfType(JsonType.String)).Knows(new JsonFact(At, new JsonClaim.LowerBound(1000))).Should().BeTrue("a string is no number");
        }

        static JsonFact Present(JsonDocumentPath path) => new(path, new JsonClaim.Present());

        /// <summary>
        /// Whether two claims about a path that is there have no document in common.
        /// </summary>
        static bool Contradicts(JsonFact left, JsonFact right) =>
            JsonFactTheory.Empty.Derive(new[] { left, right, Present(left.Path) }).IsContradictory;


    }

}
