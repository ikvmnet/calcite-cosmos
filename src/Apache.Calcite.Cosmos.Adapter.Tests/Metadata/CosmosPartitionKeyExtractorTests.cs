using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Recovering the partition key a predicate pins. Getting this wrong in the permissive
    /// direction loses rows, so only a conjunction of equalities against constants qualifies.
    /// </summary>
    public class CosmosPartitionKeyExtractorTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;

        public CosmosPartitionKeyExtractorTests()
        {
            _rex = new RexBuilder(_types);
        }

        static readonly CosmosPath[] Fields =
        {
            CosmosPath.Root("c"),                             // 0 — document column
            CosmosPath.Root("c").Property("category"),        // 1
            CosmosPath.Root("c").Property("tenant"),          // 2
            CosmosPath.Root("c").Property("id"),              // 3
            CosmosPath.Root("t0").Property("category"),       // 4 — element-relative
        };

        RexNode Ref(int index) => _rex.makeInputRef(_types.createTypeWithNullability(_types.createSqlType(SqlTypeName.ANY), true), index);

        RexNode Str(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        RexNode Eq(RexNode a, RexNode b) => _rex.makeCall(SqlStdOperatorTable.EQUALS, a, b);

        RexNode And(params RexNode[] operands) => _rex.makeCall(SqlStdOperatorTable.AND, operands);

        RexNode Or(params RexNode[] operands) => _rex.makeCall(SqlStdOperatorTable.OR, operands);

        static CosmosContainerMetadata Container(params string[] partitionKeyPaths) =>
            TestReasoning.Bind(new CosmosContainerMetadata("products", partitionKeyPaths));

        static bool Extract(RexNode condition, CosmosContainerMetadata container, out IReadOnlyList<object?> values) =>
            CosmosPartitionKeyExtractor.TryExtract(condition, Fields, null, container, "c", out values);

        // ── Recovered ─────────────────────────────────────────────────────────────

        [Fact]
        public void EqualityOnThePartitionKeyIsRecovered()
        {
            Extract(Eq(Ref(1), Str("bikes")), Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        // ── Through a cast to text ────────────────────────────────────────────────

        RexNode TextCast(RexNode operand) => _rex.makeAbstractCast(_types.createSqlType(SqlTypeName.VARCHAR), operand, false);

        static bool ExtractPrefix(RexNode condition, CosmosContainerMetadata container, out IReadOnlyList<object?> values) =>
            CosmosPartitionKeyExtractor.TryExtractPrefix(condition, Fields, null, container, "c", out values, out _);

        /// <summary>
        /// A view exposing the partition key gives it a SQL type, which over this row model means a
        /// cast — and losing single-partition execution to that is the largest cost there is to lose
        /// silently.
        /// </summary>
        /// <remarks>
        /// Recovered only for routing, and only for the equality against text that selects the same
        /// documents either way. Routing narrows which partitions are visited and filters nothing; the
        /// predicate is still in the statement.
        /// </remarks>
        [Fact]
        public void APartitionKeyReachedThroughACastToTextIsRoutedOn()
        {
            ExtractPrefix(Eq(TextCast(Ref(1)), Str("bikes")), Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        /// <remarks>
        /// Text a number renders as. A document storing the number 30 matches the predicate and lives in
        /// a different logical partition than one storing the string, so routing on it would skip the
        /// partition holding a matching document. Measured against the differential container, where
        /// exactly that document exists.
        /// </remarks>
        [Fact]
        public void APartitionKeyReachedThroughACastAgainstAmbiguousTextIsNotRoutedOn()
        {
            ExtractPrefix(Eq(TextCast(Ref(1)), Str("30")), Container("/category"), out _).Should().BeFalse();
        }

        /// <remarks>
        /// A cast to a number is not dropped anywhere, and least of all here.
        /// </remarks>
        [Fact]
        public void APartitionKeyReachedThroughANumericCastIsNotRoutedOn()
        {
            var cast = _rex.makeAbstractCast(_types.createSqlType(SqlTypeName.INTEGER), Ref(1), false);

            ExtractPrefix(Eq(cast, _rex.makeExactLiteral(new java.math.BigDecimal(30))), Container("/category"), out _).Should().BeFalse();
        }

        /// <summary>
        /// The operations that replace the predicate rather than route it keep the cast opaque.
        /// </summary>
        /// <remarks>
        /// A point read and a whole-partition delete apply no predicate of their own — the read returns
        /// whatever is at the key, the delete removes everything in the partition — so each needs its
        /// conjuncts accounted for in a stronger sense than routing does. Nothing about the cast form is
        /// known to fail there; it is simply not the place to find out, and a delete least of all.
        /// </remarks>
        [Fact]
        public void ACastToTextIsNotUsedWhereThePredicateWouldBeReplaced()
        {
            var condition = Eq(TextCast(Ref(1)), Str("bikes"));

            Extract(condition, Container("/category"), out _).Should().BeFalse();

            CosmosPartitionKeyExtractor.TryExtractWholePartition(condition, Fields, null, Container("/category"), "c", out _)
                .Should().BeFalse();

            CosmosPartitionKeyExtractor.TryExtractPointRead(
                And(condition, Eq(Ref(3), Str("x"))), Fields, null, Container("/category"), "c", out _, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void OperandOrderDoesNotMatter()
        {
            Extract(Eq(Str("bikes"), Ref(1)), Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        [Fact]
        public void PartitionKeyWithinAConjunctionIsRecovered()
        {
            var condition = And(Eq(Ref(3), Str("x")), Eq(Ref(1), Str("bikes")));

            Extract(condition, Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        [Fact]
        public void HierarchicalKeyIsRecoveredInDeclaredOrder()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Eq(Ref(2), Str("acme")));

            Extract(condition, Container("/tenant", "/category"), out var values).Should().BeTrue();
            values.Should().Equal("acme", "bikes");
        }

        [Fact]
        public void MapPropertyPathIsRecovered()
        {
            var item = _rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("region"));

            Extract(Eq(item, Str("emea")), Container("/region"), out var values).Should().BeTrue();
            values.Should().Equal("emea");
        }

        // ── A set of point reads ──────────────────────────────────────────────────

        static bool ExtractSet(RexNode condition, CosmosContainerMetadata container, out IReadOnlyList<object?> values, out IReadOnlyList<string> ids) =>
            CosmosPartitionKeyExtractor.TryExtractPointReadSet(condition, Fields, null, container, "c", out values, out ids);

        /// <remarks>
        /// The shape <c>pk = 'x' AND id IN ('a', 'b')</c>, after <c>IN</c> has expanded to the
        /// disjunction of equalities the planner carries.
        /// </remarks>
        [Fact]
        public void ASetOfIdsWithACompleteKeyIsRecovered()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("b"))));

            ExtractSet(condition, Container("/category"), out var values, out var ids).Should().BeTrue();
            values.Should().Equal("bikes");
            ids.Should().Equal("a", "b");
        }

        [Fact]
        public void DuplicateIdsCollapse()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("a"))));

            ExtractSet(condition, Container("/category"), out _, out var ids).Should().BeTrue();
            ids.Should().Equal("a");
        }

        /// <remarks>
        /// The batch read applies no predicate, so a residual conjunct rules it out — the same
        /// blindness the single point read answers for.
        /// </remarks>
        [Fact]
        public void AResidualConjunctIsNotRecovered()
        {
            var condition = And(
                Eq(Ref(1), Str("bikes")),
                Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("b"))),
                Eq(_rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("price")), Str("5")));

            ExtractSet(condition, Container("/category"), out _, out _).Should().BeFalse();
        }

        [Fact]
        public void ABranchThatIsNotAnIdIsNotRecovered()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Or(Eq(Ref(3), Str("a")), Eq(Ref(1), Str("shoes"))));

            ExtractSet(condition, Container("/category"), out _, out _).Should().BeFalse();
        }

        [Fact]
        public void ASetWithoutACompleteKeyIsNotRecovered()
        {
            ExtractSet(Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("b"))), Container("/category"), out _, out _).Should().BeFalse();
        }

        /// <remarks>
        /// A lone id equality is the single point read's question, asked first by the filter.
        /// </remarks>
        [Fact]
        public void ASingleIdEqualityIsLeftToThePointRead()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Eq(Ref(3), Str("a")));

            ExtractSet(condition, Container("/category"), out _, out _).Should().BeFalse();
        }

        // ── What the oracle says the conjuncts pin ────────────────────────────────

        /// <summary>
        /// A column read as text: <c>category</c>, at ordinal 1, as the accessor renders it.
        /// </summary>
        static readonly CosmosReading[] TextAtCategory = { CosmosReading.Typed, CosmosReading.Text, CosmosReading.Typed, CosmosReading.Typed, CosmosReading.Typed };

        RexNode Text(int index) => _rex.makeInputRef(_types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true), index);

        /// <summary>
        /// An equality over a key's text pins the key only where one stored value renders as the text.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A key is pinned where the facts the question's licence admits say it is there and holds one value. Text no
        /// number renders as is a string's, and only a string's, so the equality pins the string. <c>'30'</c> is the
        /// rendering of a stored <c>"30"</c> and of a stored <c>30</c>, which are two partitions: routing on either would
        /// skip a partition holding a row the predicate keeps, and a point read would answer for one document of two.
        /// </para>
        /// <para>
        /// The recovery before the oracle read the literal off the comparison and pinned <c>"30"</c> whatever the
        /// column's reading. The planner sends the weakening of both spellings in its place, which pins nothing either way.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnEqualityOverTextPinsTheKeyOnlyWhereOneValueRendersAsIt()
        {
            var container = Container("/category");

            CosmosPartitionKeyExtractor.TryExtractPrefix(Eq(Text(1), Str("bikes")), Fields, TextAtCategory, container, "c", out var values, out _)
                .Should().BeTrue("only a string renders as 'bikes'");
            values.Should().Equal("bikes");

            CosmosPartitionKeyExtractor.TryExtractPrefix(Eq(Text(1), Str("30")), Fields, TextAtCategory, container, "c", out _, out _)
                .Should().BeFalse("a stored 30 renders as '30' too, and lives in another partition");

            CosmosPartitionKeyExtractor.TryExtractPointRead(And(Eq(Text(1), Str("30")), Eq(Ref(3), Str("x"))), Fields, TextAtCategory, container, "c", out _, out _)
                .Should().BeFalse("so there is no one partition for a read to look in");
        }

        static CosmosContainerMetadata Declaring(string schema, params string[] partitionKeyPaths) =>
            TestReasoning.Bind(new CosmosContainerMetadata("products", partitionKeyPaths)).WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)!));

        /// <summary>
        /// A point read applies no predicate, so every conjunct beside its pins has to be one the pins and the declaration
        /// already make true: there, it asks nothing of the document the read returns.
        /// </summary>
        /// <remarks>
        /// Asked of the oracle in a scope that assumes the pins and nothing else, as <c>Always</c>: a <c>kind</c> the
        /// container declares required and constant is true of every document, so the read answers the query. The
        /// control is a conjunct over a path the declaration says nothing of, which a point read would ignore.
        /// </remarks>
        [Fact]
        public void APointReadAllowsAConjunctThePinsAndTheDeclarationMakeTrue()
        {
            var container = Declaring("{ \"type\": \"object\", \"required\": [\"kind\"], \"properties\": { \"kind\": { \"const\": \"bike\" } } }", "/category");
            var kind = _rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("kind"));
            var price = _rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("price"));

            CosmosPartitionKeyExtractor.TryExtractPointRead(And(Eq(Ref(1), Str("bikes")), Eq(Ref(3), Str("x")), Eq(kind, Str("bike"))), Fields, null, container, "c", out var values, out var id)
                .Should().BeTrue("every document holds kind 'bike', so the conjunct asks nothing of the one read");
            values.Should().Equal("bikes");
            id.Should().Be("x");

            CosmosPartitionKeyExtractor.TryExtractPointRead(And(Eq(Ref(1), Str("bikes")), Eq(Ref(3), Str("x")), Eq(price, Str("5"))), Fields, null, container, "c", out _, out _)
                .Should().BeFalse("nothing says what price the document holds, and the read would not ask");
        }

        /// <summary>
        /// A number pinned is sent as the client sends a literal of it: a whole one as a <see cref="long"/>, which a
        /// whole-partition delete takes, and any other as a <see cref="double"/>.
        /// </summary>
        /// <remarks>
        /// The oracle holds a number as a <see cref="decimal"/>, which no partition key component is.
        /// </remarks>
        [Fact]
        public void ANumberPinnedIsSentAsTheClientSendsOne()
        {
            var container = Container("/category");

            CosmosPartitionKeyExtractor.TryExtractWholePartition(Eq(Ref(1), _rex.makeExactLiteral(new java.math.BigDecimal(5))), Fields, null, container, "c", out var whole)
                .Should().BeTrue();
            whole.Should().Equal(5L);

            CosmosPartitionKeyExtractor.TryExtractWholePartition(Eq(Ref(1), _rex.makeExactLiteral(new java.math.BigDecimal("5.5"))), Fields, null, container, "c", out var fraction)
                .Should().BeTrue();
            fraction.Should().Equal(5.5d);
        }

        // ── Not recovered ─────────────────────────────────────────────────────────

        /// <remarks>
        /// Under a disjunction an equality does not constrain the predicate, so the query may
        /// match several partitions.
        /// </remarks>
        [Fact]
        public void EqualityUnderADisjunctionIsNotRecovered()
        {
            var condition = Or(Eq(Ref(1), Str("bikes")), Eq(Ref(1), Str("shoes")));

            Extract(condition, Container("/category"), out _).Should().BeFalse();
        }

        [Fact]
        public void RangePredicateIsNotRecovered()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Ref(1), Str("bikes"));

            Extract(condition, Container("/category"), out _).Should().BeFalse();
        }

        [Fact]
        public void PartiallyPinnedHierarchicalKeyIsNotRecovered()
        {
            Extract(Eq(Ref(1), Str("bikes")), Container("/tenant", "/category"), out _).Should().BeFalse();
        }

        [Fact]
        public void EqualityOnADifferentPropertyIsNotRecovered()
        {
            Extract(Eq(Ref(3), Str("x")), Container("/category"), out _).Should().BeFalse();
        }

        [Fact]
        public void EqualityBetweenTwoPathsIsNotRecovered()
        {
            Extract(Eq(Ref(1), Ref(2)), Container("/category"), out _).Should().BeFalse();
        }

        /// <remarks>
        /// A path rooted at an array-traversal alias addresses an element, not the document.
        /// </remarks>
        [Fact]
        public void EqualityOnAnUnnestAliasIsNotRecovered()
        {
            Extract(Eq(Ref(4), Str("bikes")), Container("/category"), out _).Should().BeFalse();
        }

        [Fact]
        public void ContainerWithNoDeclaredPartitionKeyRecoversNothing()
        {
            Extract(Eq(Ref(1), Str("bikes")), Container(), out _).Should().BeFalse();
        }

    }

}
