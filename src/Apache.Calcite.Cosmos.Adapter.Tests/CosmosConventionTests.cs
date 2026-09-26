using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    public class CosmosConventionTests
    {

        [Fact]
        public void ConventionIsNamedForItsContainer()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            convention.getName().Should().Be("COSMOS.products");
        }

        /// <remarks>
        /// A convention is bound to one container, so two containers yield two conventions and the
        /// planner inserts converters between them.
        /// </remarks>
        [Fact]
        public void DistinctContainersYieldDistinctConventions()
        {
            var a = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            var b = CosmosConvention.Create(new CosmosContainerMetadata("orders"));

            a.getName().Should().NotBe(b.getName());
        }

        [Fact]
        public void ContainerMetadataIsReachableFromTheConvention()
        {
            var container = new CosmosContainerMetadata("products", new[] { "/tenant" });
            CosmosConvention.Create(container).Container.Should().BeSameAs(container);
        }

        [Fact]
        public void RulesAreBoundToTheConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            CosmosRules.GetRules(convention).Should().NotBeEmpty();
        }

        /// <summary>
        /// Nothing is converted <em>into</em> the Cosmos convention that Cosmos cannot express.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Joins, set operations and values have no Cosmos equivalent, and their absence is a design
        /// commitment rather than an omission. The check is on the out-convention rather than on the
        /// rule's name, because a name says what a rule is about and the convention says what it
        /// promises the service can run.
        /// </para>
        /// <para>
        /// <c>CosmosLookupJoinRule</c> is the reason that distinction now matters. It is a join rule,
        /// and it converts into <c>ClrCursorConvention</c> — the join is still performed
        /// outside the service, and what reaches the statement is only a restriction to the keys the
        /// other side has. A rule converting a join <em>into</em> the Cosmos convention would be
        /// claiming the service can join, which it cannot.
        /// </para>
        /// </remarks>
        [Fact]
        public void NothingInexpressibleIsConvertedIntoTheConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));

            var into = CosmosRules.GetRules(convention)
                .OfType<org.apache.calcite.rel.convert.ConverterRule>()
                .Where(x => ReferenceEquals(x.getOutConvention(), convention))
                .Select(x => x.GetType().Name)
                .ToList();

            into.Should().NotBeEmpty("the convention is reached by rules, so an empty list would mean the check is vacuous");

            foreach (var forbidden in new[] { "Join", "Union", "Intersect", "Minus", "Values" })
                into.Should().NotContain(x => x.Contains(forbidden), "{0} has no Cosmos equivalent", forbidden);
        }

        /// <remarks>
        /// The lookup join leaves the convention, exactly as the converter does, and for the same
        /// reason: below it is a statement, above it are rows.
        /// </remarks>
        [Fact]
        public void TheLookupJoinConvertsOutOfTheConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));

            var rule = CosmosRules.GetRules(convention)
                .OfType<Adapter.Rel.Convert.CosmosLookupJoinRule>()
                .Should().ContainSingle().Subject;

            rule.getOutConvention().Should().BeSameAs(Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance);
        }

        [Fact]
        public void TheTableModifyConvertsIntoTheCursorConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));

            var rule = CosmosRules.GetRules(convention)
                .OfType<Adapter.Rel.Convert.CosmosTableModifyRule>()
                .Should().ContainSingle().Subject;

            rule.getOutConvention().Should().BeSameAs(Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance);
        }

        /// <remarks>
        /// <b>The adapter leads into the cursor convention and into nothing else.</b> A plan that wants
        /// rows in <c>ClrEnumerableConvention</c> or Calcite's <c>EnumerableConvention</c> gets there
        /// higher up, through converters that are not the adapter's; a rule here that led anywhere but
        /// the Cosmos convention itself or the cursor convention would be a second way out.
        /// </remarks>
        [Fact]
        public void EveryRuleLeadsIntoTheConventionOrTheCursorConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            var cursor = Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance;

            var rules = CosmosRules.GetRules(convention)
                .OfType<org.apache.calcite.rel.convert.ConverterRule>()
                .ToList();

            rules.Where(x => ReferenceEquals(x.getOutConvention(), cursor)).Select(x => x.GetType().Name)
                .Should().BeEquivalentTo(new[] { "CosmosToClrCursorConverterRule", "CosmosLookupJoinRule", "CosmosTableModifyRule" });

            rules.Should().OnlyContain(x => ReferenceEquals(x.getOutConvention(), convention) || ReferenceEquals(x.getOutConvention(), cursor),
                "no rule may lead into a convention other than the Cosmos one or the cursor one");
        }

        /// <remarks>
        /// A subtree in the convention is a statement, and one converter turns it into rows. The lookup
        /// join and the table modify also leave into the cursor convention, but they convert a logical
        /// join or modify rather than a Cosmos subtree.
        /// </remarks>
        [Fact]
        public void TheOnlyWayOutOfTheConventionIsTheCursorConverter()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));

            CosmosRules.GetRules(convention)
                .OfType<org.apache.calcite.rel.convert.ConverterRule>()
                .Where(x => ReferenceEquals(x.getInTrait(), convention) && ReferenceEquals(x.getOutConvention(), convention) == false)
                .Should().ContainSingle()
                .Which.Should().BeOfType<Adapter.Rel.Convert.CosmosToClrCursorConverterRule>();
        }

    }

}
