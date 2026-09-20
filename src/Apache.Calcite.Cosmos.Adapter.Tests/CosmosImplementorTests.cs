using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Exercises the implementor directly, without a relational tree, to pin the interaction
    /// between field bindings, expression translation, parameter accumulation, and statement
    /// assembly.
    /// </summary>
    public class CosmosImplementorTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;

        public CosmosImplementorTests()
        {
            _rex = new RexBuilder(_types);
        }

        CosmosImplementor Implementor(CosmosContainerMetadata? container = null) =>
            new(_rex, container ?? new CosmosContainerMetadata("products"));

        RexNode Ref(int index, SqlTypeName type) => _rex.makeInputRef(_types.createSqlType(type), index);

        RexNode Str(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        [Fact]
        public void StartsBoundToTheWholeDocument()
        {
            var implementor = Implementor();

            implementor.RootAlias.Should().Be("c");
            implementor.Fields.Should().ContainSingle().Which!.ToString().Should().Be("c");
        }

        [Fact]
        public void BareImplementorRendersAnIdentityQuery()
        {
            var query = Implementor().Build();

            query.Sql.Should().Be("SELECT VALUE c FROM products c");
            query.Parameters.Should().BeEmpty();
        }

        [Fact]
        public void RootAliasCanBeOverridden()
        {
            var implementor = new CosmosImplementor(_rex, new CosmosContainerMetadata("products"), "p");

            implementor.Build().Sql.Should().Be("SELECT VALUE p FROM products p");
            implementor.Root.ToString().Should().Be("p");
        }

        [Fact]
        public void TranslatedPredicateAndParametersFlowIntoTheStatement()
        {
            var implementor = Implementor();
            implementor.Fields = new[] { CosmosPath.Root("c").Property("name") };

            implementor.Query.Where = implementor.Translate(
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("bike")));

            var query = implementor.Build();

            query.Sql.Should().Be("SELECT VALUE c FROM products c WHERE (c.name = @p0)");
            query.Parameters.Should().ContainSingle().Which.Value.Should().Be("bike");
        }

        [Fact]
        public void ParametersAccumulateAcrossTranslations()
        {
            var implementor = Implementor();
            implementor.Fields = new[] { CosmosPath.Root("c").Property("name") };

            var first = implementor.Translate(_rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("a")));
            var second = implementor.Translate(_rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("b")));

            first.Should().Be("(c.name = @p0)");
            second.Should().Be("(c.name = @p1)");
            implementor.Parameters.Count.Should().Be(2);
        }

        /// <remarks>
        /// A projection rebinds the field ordinals, so a translator created afterwards must see
        /// the new paths rather than the ones captured at construction.
        /// </remarks>
        [Fact]
        public void RebindingFieldsAffectsSubsequentTranslations()
        {
            var implementor = Implementor();

            implementor.Fields = new[] { CosmosPath.Root("c").Property("a") };
            implementor.Translate(Ref(0, SqlTypeName.ANY)).Should().Be("c.a");

            implementor.Fields = new[] { CosmosPath.Root("c").Property("b") };
            implementor.Translate(Ref(0, SqlTypeName.ANY)).Should().Be("c.b");
        }

        [Fact]
        public void ContainerMetadataIsReachableForRuleGuards()
        {
            var container = new CosmosContainerMetadata("products", new[] { "/tenant" });
            Implementor(container).Container.PartitionKeyPaths.Should().Equal("/tenant");
        }

        /// <remarks>
        /// The builder's refusal of combinations Cosmos rejects must survive being driven through
        /// the implementor.
        /// </remarks>
        [Fact]
        public void IllegalClauseCombinationStillFailsAtBuild()
        {
            var implementor = Implementor();
            implementor.Query.AddGroupBy("c.category");
            implementor.Query.AddOrderBy("c.category", descending: false);

            var act = () => implementor.Build();
            act.Should().Throw<System.InvalidOperationException>();
        }

        [Fact]
        public void UntranslatableExpressionIsDeclined()
        {
            var implementor = Implementor();
            implementor.Fields = new[] { CosmosPath.Root("c").Property("name") };

            var node = _rex.makeCall(SqlStdOperatorTable.INITCAP, Ref(0, SqlTypeName.VARCHAR));
            implementor.CreateTranslator().TryTranslate(node, out _).Should().BeFalse();
        }

        // ── Page size ─────────────────────────────────────────────────────────────

        /// <remarks>
        /// A statement with no row restriction reads to the end, so there is no page size worth
        /// asking for and the SDK's own default stands.
        /// </remarks>
        [Fact]
        public void UnboundedStatementAsksForNoParticularPageSize()
        {
            Implementor().Build().MaxItemCount.Should().BeNull();
        }

        [Fact]
        public void LimitBecomesThePageSize()
        {
            var implementor = Implementor();
            implementor.Query.Fetch = 5;

            implementor.Build().MaxItemCount.Should().Be(5);
        }

        /// <remarks>
        /// The service still walks the rows the offset skips, so the page worth asking for spans both.
        /// </remarks>
        [Fact]
        public void OffsetIsCountedIntoThePageSize()
        {
            var implementor = Implementor();
            implementor.Query.Offset = 10;
            implementor.Query.Fetch = 5;

            implementor.Build().MaxItemCount.Should().Be(15);
        }

        [Fact]
        public void TopBecomesThePageSize()
        {
            var implementor = Implementor();
            implementor.Query.Top = 3;

            implementor.Build().MaxItemCount.Should().Be(3);
        }

        /// <remarks>
        /// An offset alone bounds nothing — the statement still reads to the end of the container.
        /// </remarks>
        [Fact]
        public void OffsetAloneAsksForNoParticularPageSize()
        {
            var implementor = Implementor();
            implementor.Query.Offset = 10;

            implementor.Build().MaxItemCount.Should().BeNull();
        }

    }

}
