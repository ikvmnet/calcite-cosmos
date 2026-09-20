using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using StatementPath = Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
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
    public class CosmosFactTheoryTests
    {

        static readonly CosmosDocumentPath Type = CosmosDocumentPath.Root.Property("type");
        static readonly CosmosDocumentPath ParkId = CosmosDocumentPath.Root.Property("data").Property("parkId");
        static readonly CosmosDocumentPath At = CosmosDocumentPath.Root.Property("data").Property("at");

        // Stand-ins rather than rows out of CosmosStoredForms: what a theory does with a
        // representation is carry it, so the pair is chosen to be two distinguishable values with
        // the two licences between them, and nothing here turns on which pattern yields either.
        static readonly CosmosRepresentation EqualityOnly = new("equality-only", PreservesEquality: true, PreservesOrder: false);
        static readonly CosmosRepresentation Ordered = new("ordered", PreservesEquality: true, PreservesOrder: true);

        static CosmosFact Equals(CosmosDocumentPath path, object? value) => new(path, new CosmosClaim.EqualTo(value));

        static CosmosFact Represents(CosmosDocumentPath path, CosmosRepresentation representation) => new(path, new CosmosClaim.Represents(representation));

        [Fact]
        public void AnUnconditionalFactHoldsWithNothingEstablished()
        {
            var theory = new CosmosFactTheory(new[] { CosmosFactRule.Unconditional(Represents(ParkId, EqualityOnly)) });

            theory.Derive(null).RepresentationOf(ParkId).Should().Be(EqualityOnly);
        }

        [Fact]
        public void AGuardedFactIsUnusableUntilItsGuardIsProven()
        {
            var theory = new CosmosFactTheory(new[]
            {
                new CosmosFactRule(new[] { Equals(Type, "ParkMap") }, Represents(ParkId, EqualityOnly)),
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
            var set = CosmosFactTheory.Empty.Derive(new[] { Equals(Type, "ParkMap") });

            set.Knows(new CosmosFact(Type, new CosmosClaim.OfType(CosmosJsonType.String))).Should().BeTrue();
            set.Knows(new CosmosFact(Type, new CosmosClaim.NotEqualTo("Park"))).Should().BeTrue(
                "an else branch's guard is discharged by an equality to a different value, with no closed world needed");
            set.Knows(new CosmosFact(Type, new CosmosClaim.NotEqualTo("ParkMap"))).Should().BeFalse();
            set.Knows(new CosmosFact(Type, new CosmosClaim.OneOf(new object?[] { "Park", "ParkMap" }))).Should().BeTrue();
            set.Knows(new CosmosFact(Type, new CosmosClaim.OneOf(new object?[] { "Park", "Trail" }))).Should().BeFalse();
            set.Knows(new CosmosFact(Type, new CosmosClaim.OfType(CosmosJsonType.Integer))).Should().BeFalse();
        }

        [Fact]
        public void ADomainSettlesWhatEveryMemberAgreesOn()
        {
            var set = CosmosFactTheory.Empty.Derive(new[] { new CosmosFact(Type, new CosmosClaim.OneOf(new object?[] { "Park", "ParkMap" })) });

            set.Knows(new CosmosFact(Type, new CosmosClaim.OfType(CosmosJsonType.String))).Should().BeTrue(
                "every member is a string, so the type is settled even though the value is not");
            set.Knows(new CosmosFact(Type, new CosmosClaim.NotEqualTo("Trail"))).Should().BeTrue();
            set.Knows(new CosmosFact(Type, new CosmosClaim.NotEqualTo("Park"))).Should().BeFalse();
            set.Knows(Equals(Type, "Park")).Should().BeFalse("a domain is not a value");
        }

        [Fact]
        public void AGuardIsSatisfiedThroughSubsumptionRatherThanLiterally()
        {
            var theory = new CosmosFactTheory(new[]
            {
                new CosmosFactRule(new[] { new CosmosFact(Type, new CosmosClaim.OneOf(new object?[] { "Park", "ParkMap" })) }, Represents(ParkId, EqualityOnly)),
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
            var set = CosmosFactTheory.Empty.Derive(new[]
            {
                Equals(Type, "ParkMap"),
                Represents(ParkId, EqualityOnly),
                new CosmosFact(At, new CosmosClaim.OfType(CosmosJsonType.String)),
            });

            set.Knows(new CosmosFact(Type, new CosmosClaim.Present())).Should().BeFalse();
            set.Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeFalse();
            set.Knows(new CosmosFact(At, new CosmosClaim.Present())).Should().BeFalse();

            CosmosFactTheory.Empty.Derive(new[] { new CosmosFact(ParkId, new CosmosClaim.Present()) })
                .Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeTrue("which required states outright");
        }

        [Fact]
        public void ChainingReachesAFactWhoseGuardIsItselfDerived()
        {
            // if type = 'ParkMap' then the kind is 'v2'; if the kind is 'v2' then the instant is fixed shape.
            var kind = CosmosDocumentPath.Root.Property("kind");

            var theory = new CosmosFactTheory(new[]
            {
                new CosmosFactRule(new[] { Equals(Type, "ParkMap") }, Equals(kind, "v2")),
                new CosmosFactRule(new[] { Equals(kind, "v2") }, Represents(At, Ordered)),
            });

            theory.Derive(null).RepresentationOf(At).Should().BeNull();
            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(At).Should().Be(Ordered,
                "the second rule's body is satisfied by the first rule's head, which is what chaining is for");
        }

        [Fact]
        public void EveryAtomOfAConjunctiveBodyHasToHold()
        {
            var version = CosmosDocumentPath.Root.Property("v");

            var theory = new CosmosFactTheory(new[]
            {
                new CosmosFactRule(new[] { Equals(Type, "ParkMap"), Equals(version, 2) }, Represents(At, Ordered)),
            });

            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(At).Should().BeNull();
            theory.Derive(new[] { Equals(version, 2) }).RepresentationOf(At).Should().BeNull();
            theory.Derive(new[] { Equals(Type, "ParkMap"), Equals(version, 2) }).RepresentationOf(At).Should().Be(Ordered);
        }

        [Fact]
        public void TheStrongestRepresentationWins()
        {
            var theory = new CosmosFactTheory(new[]
            {
                CosmosFactRule.Unconditional(Represents(At, new CosmosRepresentation("iso8601-loose", PreservesEquality: true, PreservesOrder: false))),
                CosmosFactRule.Unconditional(Represents(At, Ordered)),
            });

            theory.Derive(null).RepresentationOf(At).Should().Be(Ordered,
                "both were proven, and the caller wants the one that licenses the most");
        }

        [Fact]
        public void MutuallyDependentRulesTerminate()
        {
            var a = CosmosDocumentPath.Root.Property("a");
            var b = CosmosDocumentPath.Root.Property("b");

            var theory = new CosmosFactTheory(new[]
            {
                new CosmosFactRule(new[] { Equals(a, 1) }, Equals(b, 2)),
                new CosmosFactRule(new[] { Equals(b, 2) }, Equals(a, 1)),
            });

            var set = theory.Derive(new[] { Equals(a, 1) });

            set.Knows(Equals(b, 2)).Should().BeTrue();
            theory.Derive(null).Knows(Equals(a, 1)).Should().BeFalse("neither rule can start itself");
        }

        [Fact]
        public void ADocumentPathIsTheStatementsPathWithoutTheAlias()
        {
            var path = StatementPath.Root("c").Property("data").Property("parkId");

            CosmosDocumentPath.From(path).Should().Be(ParkId);
            CosmosDocumentPath.From(StatementPath.Root("x").Property("data").Property("parkId")).Should().Be(ParkId,
                "the alias is the statement's business and no part of what a schema declares");

            CosmosDocumentPath.From(StatementPath.Root("c").Property("tags").Index(0)).Should().BeNull(
                "an element is not its array, and conflating them would apply one's facts to the other");
        }

    }

}
