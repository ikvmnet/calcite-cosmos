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

        /// <summary>
        /// A row type of the given columns, named after their types.
        /// </summary>
        org.apache.calcite.rel.type.RelDataType Row(params SqlTypeName[] types)
        {
            var builder = _types.builder();

            for (var i = 0; i < types.Length; i++)
                builder = builder.add("c" + i, _types.createSqlType(types[i]));

            return builder.build();
        }

        // ── Refusing a row that cannot be read ───────────────────────────────────────

        /// <remarks>
        /// The ordinary row, and the half that says the refusal below is about the type rather than
        /// about the check being on at all.
        /// </remarks>
        [Fact]
        public void ARowOfReadableColumnsIsAccepted()
        {
            var act = () => CosmosImplementor.RequireReadableRow(Row(SqlTypeName.VARCHAR, SqlTypeName.BIGINT, SqlTypeName.UUID));

            act.Should().NotThrow();
        }

        /// <summary>
        /// A column the reader has no reading for is refused while the plan is being built.
        /// </summary>
        /// <remarks>
        /// <b>Which is the whole point of the check, and the reason it is not left to the reader.</b>
        /// Reached at the first row instead, a caller over HTTP has already had its <c>200</c> and its
        /// headers, so the failure arrives as a truncated body rather than as an error — that is what
        /// #149 cost. <c>INTERVAL DAY</c> stands in for the class: Calcite has thirteen interval types,
        /// four unsigned integer ones and three more temporal spellings that this reader does not
        /// cover, none of which anything renders today.
        /// </remarks>
        [Fact]
        public void AColumnWithNoReadingIsRefused()
        {
            var act = () => CosmosImplementor.RequireReadableRow(Row(SqlTypeName.VARCHAR, SqlTypeName.INTERVAL_DAY));

            act.Should().Throw<CosmosTranslationException>()
                .WithMessage("*'c1'*INTERVAL_DAY*no reading*");
        }

        /// <summary>
        /// A reading other than <c>Typed</c> is not asked about, because it never consults the type.
        /// </summary>
        /// <remarks>
        /// <c>Text</c>, <c>Json</c> and <c>JsonText</c> read what the service sent whatever the plan
        /// calls it — which is exactly why they exist, the <c>DOC</c> column being an object in a
        /// column declared <c>VARCHAR</c>. Refusing on the declared type there would refuse the row
        /// model itself.
        /// </remarks>
        [Fact]
        public void AColumnReadAsSomethingOtherThanItsTypeIsNotAsked()
        {
            var act = () => CosmosImplementor.RequireReadableRow(
                Row(SqlTypeName.VARCHAR, SqlTypeName.INTERVAL_DAY),
                new[] { CosmosReading.Typed, CosmosReading.Json });

            act.Should().NotThrow();
        }

        /// <remarks>
        /// A list shorter than the row leaves the rest <c>Typed</c>, which is what
        /// <c>CosmosConverters.RowBuilder</c> means by a short list and has to mean here too.
        /// </remarks>
        [Fact]
        public void AShortReadingListLeavesTheRestTyped()
        {
            var act = () => CosmosImplementor.RequireReadableRow(
                Row(SqlTypeName.VARCHAR, SqlTypeName.INTERVAL_DAY),
                new[] { CosmosReading.Json });

            act.Should().Throw<CosmosTranslationException>().WithMessage("*'c1'*");
        }

        /// <summary>
        /// A collection is refused for its element type, the failure otherwise arriving one level down.
        /// </summary>
        [Fact]
        public void ACollectionIsRefusedForItsElementType()
        {
            var element = _types.createSqlType(SqlTypeName.INTERVAL_DAY);
            var rowType = _types.builder().add("a", _types.createArrayType(element, -1)).build();

            var act = () => CosmosImplementor.RequireReadableRow(rowType);

            act.Should().Throw<CosmosTranslationException>().WithMessage("*'a'*");
        }

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
