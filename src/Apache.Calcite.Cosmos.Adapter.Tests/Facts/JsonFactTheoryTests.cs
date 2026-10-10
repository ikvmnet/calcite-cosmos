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
    /// the whole soundness argument for a container holding more than one kind of document; and that
    /// subsumption is not a rule — a known value settles presence, type and disequality without any of
    /// them being derived, which is what keeps the rule set linear in the schema.
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

        static JsonFact Equals(JsonDocumentPath path, object? value) => new(path, new JsonClaim.EqualTo(value));

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
            isNull.Entails(new JsonFact(ParkId, new JsonClaim.OneOf(new object?[] { "a", null }))).Should().BeTrue();
            isNull.Entails(new JsonFact(ParkId, new JsonClaim.OneOf(new object?[] { "a" }))).Should().BeFalse();
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
            set.Knows(new JsonFact(Type, new JsonClaim.OneOf(new object?[] { "Park", "ParkMap" }))).Should().BeTrue();
            set.Knows(new JsonFact(Type, new JsonClaim.OneOf(new object?[] { "Park", "Trail" }))).Should().BeFalse();
            set.Knows(new JsonFact(Type, new JsonClaim.OfType(JsonType.Integer))).Should().BeFalse();
        }

        [Fact]
        public void ADomainSettlesWhatEveryMemberAgreesOn()
        {
            var set = JsonFactTheory.Empty.Derive(new[] { new JsonFact(Type, new JsonClaim.OneOf(new object?[] { "Park", "ParkMap" })) });

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
                new JsonFactRule(new[] { new JsonFact(Type, new JsonClaim.OneOf(new object?[] { "Park", "ParkMap" })) }, Represents(ParkId, EqualityOnly)),
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

    }

}
