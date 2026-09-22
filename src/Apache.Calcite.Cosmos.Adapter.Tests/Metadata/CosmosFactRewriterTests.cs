using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using Xunit;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The one rewrite the declared facts drive: a comparison against a <c>UUID</c>, lowered to the
    /// string comparison the service can evaluate.
    /// </summary>
    /// <remarks>
    /// What is being pinned is not the rendering but the <em>condition</em> on it. The same predicate
    /// lowers or does not depending on what the container declared and on what the query proved, and
    /// both directions have to hold or the feature is either useless or wrong.
    /// </remarks>
    public class CosmosFactRewriterTests
    {

        const string Canonical = "123e4567-e89b-12d3-a456-426614174000";

        const string Discriminated = """
        {
          "oneOf": [
            { "properties": { "kind": { "const": "A" } } },
            { "properties": { "kind": { "const": "B" },
                              "ref": { "type": "string",
                                       "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
          ]
        }
        """;

        const string Unconditional = """
        { "properties": { "ref": { "type": "string",
                                   "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
        """;

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;

        public CosmosFactRewriterTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath?>
            {
                CosmosPath.Root("c").Property("ref"),    // 0
                CosmosPath.Root("c").Property("kind"),   // 1
            };
        }

        static CosmosContainerMetadata Container(string? schema) =>
            schema is null
                ? new CosmosContainerMetadata("items", new[] { "/ref" })
                : new CosmosContainerMetadata("items", new[] { "/ref" })
                    .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

        RexNode Ref(int index, SqlTypeName type) => _rex.makeInputRef(_types.createSqlType(type), index);

        RexNode Uuid(string value) =>
            _rex.makeLiteral(java.util.UUID.fromString(value), _types.createSqlType(SqlTypeName.UUID), false);

        RexNode Str(string value) => _rex.makeLiteral(value);

        /// <summary><c>CAST(&lt;ref&gt; AS UUID)</c>, the shape a view over a typed column produces.</summary>
        RexNode AsUuid() => _rex.makeCast(_types.createSqlType(SqlTypeName.UUID), Ref(0, SqlTypeName.VARCHAR));

        /// <summary>
        /// <c>CAST(&lt;ref&gt; AS UUID) = UUID'…'</c>, the shape a view over a typed column produces.
        /// </summary>
        RexNode UuidEquality() => _rex.makeCall(SqlStdOperatorTable.EQUALS, AsUuid(), Uuid(Canonical));

        RexNode KindIs(string value) => _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1, SqlTypeName.VARCHAR), Str(value));

        RexNode And(params RexNode[] operands)
        {
            var list = new java.util.ArrayList();
            foreach (var operand in operands)
                list.add(operand);

            return RexUtil.composeConjunction(_rex, list);
        }

        string Rewrite(RexNode condition, string? schema) =>
            CosmosFactRewriter.Rewrite(condition, _fields, Container(schema), "c", _rex).ToString();

        const string Millis = """
        { "properties": { "ref": { "type": "string",
                                   "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$" } } }
        """;

        const string Seconds = """
        { "properties": { "ref": { "type": "string",
                                   "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$" } } }
        """;

        const string Unshaped = """{ "properties": { "ref": { "type": "string" } } }""";

        /// <summary>A <c>TIMESTAMP</c> literal, at millisecond precision.</summary>
        RexNode Instant(string text) => _rex.makeTimestampLiteral(new org.apache.calcite.util.TimestampString(text), 3);

        /// <summary><c>CAST(&lt;ref&gt; AS TIMESTAMP)</c>, the shape a view over an instant produces.</summary>
        /// <remarks>
        /// Recognised and no longer <em>licensed</em> over an instant shape: measured, Calcite's cast
        /// raises for every ISO-8601 instant, so lowering it would answer rows for a query that has
        /// none. See <c>ACastTheEngineCannotPerformLowersNothing</c>, and
        /// <see cref="Adapter.Metadata.CosmosTemporalForms.EngineReads"/> for the measurement.
        /// </remarks>
        RexNode AsInstant() => _rex.makeCast(_types.createSqlType(SqlTypeName.TIMESTAMP), Ref(0, SqlTypeName.VARCHAR));

        /// <summary>
        /// <c>PARSE_DATETIME(&lt;format&gt;, &lt;ref&gt;)</c>, which is the temporal spelling that
        /// survives: it names how the text is read, and the engine can read it.
        /// </summary>
        RexNode AsParsedInstant(string format) =>
            _rex.makeCall(org.apache.calcite.sql.fun.SqlLibraryOperators.PARSE_DATETIME, _rex.makeLiteral(format), Ref(0, SqlTypeName.VARCHAR));

        /// <summary>The format that reads a milliseconds shape, in the spelling the model reads.</summary>
        const string MillisFormat = @"%Y-%m-%d'T'%H:%M:%S.%E3S'Z'";

        /// <summary>
        /// An ordering over a path confined to one fixed shape lowers to a string comparison, with
        /// the literal written in that shape.
        /// </summary>
        [Fact]
        public void AnOrderingOverAFixedShapeLowersToAStringComparison()
        {
            Rewrite(_rex.makeCall(SqlStdOperatorTable.GREATER_THAN, AsParsedInstant(MillisFormat), Instant("2024-01-15 12:30:00")), Millis)
                .Should().Be(""">($0, '2024-01-15T12:30:00.000Z')""",
                    "the lexical order of a fixed shape is the chronological one, and the literal joins that shape");
        }

        /// <summary>
        /// A cast the engine could not perform lowers nothing, however fixed the shape is.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The condition that was missing until the engine was asked.</b> The shape is fixed, so
        /// its lexical order is chronological and its equality is faithful — both bits hold. What does
        /// not hold is that the expression means anything: measured,
        /// <c>CAST('2024-01-15T12:30:00.123Z' AS TIMESTAMP)</c> raises <c>Invalid DATE value</c>, as
        /// does every ISO-8601 instant. Lowering it onto the stored strings answered rows for a query
        /// that raises, which is a different query rather than a faster one.
        /// </para>
        /// <para>
        /// The same comparison written as a parse does lower — the row above — because a format the
        /// shape is read by is one the engine reads too.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACastTheEngineCannotPerformLowersNothing()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, AsInstant(), Instant("2024-01-15 12:30:00"));

            Rewrite(condition, Millis).Should().Be(condition.ToString(),
                "the engine raises on the cast, so there is no answer for the service to give faster");
        }

        /// <summary>
        /// And a format that does not read the shape lowers nothing either, which is the other half of
        /// the same rule.
        /// </summary>
        [Fact]
        public void AParseTheFormatDoesNotFitLowersNothing()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, AsParsedInstant(@"%Y-%m-%d"), Instant("2024-01-15 12:30:00"));

            Rewrite(condition, Millis).Should().Be(condition.ToString(),
                "a date format over an instant shape parses something else, where it parses at all");
        }

        /// <summary>
        /// A literal carrying less precision than the path stores is written out in full, which loses
        /// nothing and is the ordinary case.
        /// </summary>
        [Fact]
        public void ACoarserLiteralIsWrittenOutInTheStoredShape()
        {
            Rewrite(_rex.makeCall(SqlStdOperatorTable.LESS_THAN_OR_EQUAL, AsParsedInstant(MillisFormat), Instant("2024-01-15 12:30:00")), Millis)
                .Should().Be("""<=($0, '2024-01-15T12:30:00.000Z')""");
        }

        /// <summary>
        /// Reading the path off the right-hand operand means reading the comparison backwards, so the
        /// operator is reversed with it.
        /// </summary>
        /// <remarks>
        /// The case that would be silently wrong rather than merely unpushed: keeping the operator as
        /// written turns <c>&lt;literal&gt; &gt; &lt;path&gt;</c> into <c>&lt;path&gt; &gt;
        /// &lt;literal&gt;</c>, which selects the complement of what the query asked for.
        /// </remarks>
        [Fact]
        public void TheOperatorIsReversedWhenTheLiteralIsOnTheLeft()
        {
            Rewrite(_rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Instant("2024-01-15 12:30:00"), AsParsedInstant(MillisFormat)), Millis)
                .Should().Be("""<($0, '2024-01-15T12:30:00.000Z')""",
                    "`literal > path` is `path < literal`");
        }

        /// <summary>
        /// A literal finer than the stored shape lowers nothing, truncating it not being an
        /// equivalence.
        /// </summary>
        /// <remarks>
        /// Against a seconds path, <c>&gt; '…12:30:00.5'</c> truncated to <c>&gt; '…12:30:00Z'</c>
        /// would admit the stored value <c>12:30:00Z</c>, which is earlier than the literal. So the
        /// comparison stays where it was and is applied in process.
        /// </remarks>
        [Fact]
        public void ALiteralFinerThanTheStoredShapeLowersNothing()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, AsParsedInstant(@"%Y-%m-%d'T'%H:%M:%S'Z'"), Instant("2024-01-15 12:30:00.500"));

            Rewrite(condition, Seconds).Should().Be(condition.ToString(), "truncating the literal would select different rows");
        }

        /// <summary>
        /// A path with no declared shape lowers no ordering, whatever else is known about it.
        /// </summary>
        /// <remarks>
        /// The common case rather than the exotic one: the .NET SDK's default serializer trims
        /// trailing zeros from the fraction, so a container written without a converter holds exactly
        /// the mixed path this refuses.
        /// </remarks>
        [Fact]
        public void AnUnshapedPathLowersNoOrdering()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, AsInstant(), Instant("2024-01-15 12:30:00"));

            Rewrite(condition, Unshaped).Should().Be(condition.ToString());
            Rewrite(condition, null).Should().Be(condition.ToString(), "and a container declaring nothing proves nothing");
        }

        /// <summary>
        /// A UUID range comparison lowers to a string comparison, with the literal written in the
        /// stored spelling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This lowered nothing until #142, and the refusal was right at the time: Calcite compared
        /// UUIDs as two <em>signed</em> 64-bit halves, so an unconfined canonical form preserved
        /// equality and not order and lowering a range on one would have returned the wrong rows.
        /// CALCITE-7716 made the comparison unsigned in 1.43 and
        /// <see cref="CosmosUuidForms.CanonicalLower"/> preserves order with it.
        /// </para>
        /// <para>
        /// A keyset-paginated <c>WHERE id &gt; @last</c> is the shape that wants it, and the whole of
        /// what it is worth: without the lowering the comparison has no Cosmos form and the container
        /// is read whole for every page.
        /// </para>
        /// </remarks>
        [Fact]
        public void AUuidRangeLowersToAStringComparison()
        {
            foreach (var (operator_, rendered) in new (org.apache.calcite.sql.SqlOperator, string)[]
            {
                (SqlStdOperatorTable.GREATER_THAN, ">"),
                (SqlStdOperatorTable.GREATER_THAN_OR_EQUAL, ">="),
                (SqlStdOperatorTable.LESS_THAN, "<"),
                (SqlStdOperatorTable.LESS_THAN_OR_EQUAL, "<="),
                (SqlStdOperatorTable.NOT_EQUALS, "<>"),
            })
                Rewrite(_rex.makeCall(operator_, AsUuid(), Uuid(Canonical)), Unconditional)
                    .Should().Be($"{rendered}($0, '{Canonical}')", "for " + rendered);
        }

        /// <summary>
        /// Reading the path off the right-hand operand means reading the comparison backwards, so the
        /// operator is reversed with it.
        /// </summary>
        /// <remarks>
        /// The case that would be silently wrong rather than merely unpushed, and the one the UUID
        /// lowering could not get wrong while it only ever built an <c>EQUALS</c>. It can now, so it
        /// is pinned here exactly as the instant's reversal is.
        /// </remarks>
        [Fact]
        public void TheOperatorIsReversedWhenTheUuidLiteralIsOnTheLeft()
        {
            Rewrite(_rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Uuid(Canonical), AsUuid()), Unconditional)
                .Should().Be($"<($0, '{Canonical}')", "`literal > path` is `path < literal`");

            Rewrite(_rex.makeCall(SqlStdOperatorTable.LESS_THAN_OR_EQUAL, Uuid(Canonical), AsUuid()), Unconditional)
                .Should().Be($">=($0, '{Canonical}')");
        }

        /// <summary>
        /// A range over a path whose declared form is not a UUID lowers nothing, the literal having no
        /// spelling there.
        /// </summary>
        /// <remarks>
        /// The gate that is not the order bit. An instant path preserves order, so the ordering is
        /// licensed and the rewrite still declines — <c>RenderUuid</c> answers null for a form that
        /// stores something else, and comparing a UUID against an ISO-8601 string would select
        /// whatever the code points happened to say.
        /// </remarks>
        [Fact]
        public void AUuidRangeOverAnInstantPathLowersNothing()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, AsUuid(), Uuid(Canonical));

            Rewrite(condition, Millis).Should().Be(condition.ToString());
            Rewrite(condition, Unshaped).Should().Be(condition.ToString(), "and an undeclared shape says nothing either");
        }

        [Fact]
        public void ADeclaredFormLowersTheComparisonToAStringEquality()
        {
            Rewrite(UuidEquality(), Unconditional).Should().Be($"=($0, '{Canonical}')",
                "the stored spelling is the canonical one, so comparing the strings answers what comparing the values answers");
        }

        [Fact]
        public void NothingDeclaredLeavesThePredicateAlone()
        {
            var original = UuidEquality();

            Rewrite(original, null).Should().Be(original.ToString(),
                "which is every container today, and asking has to cost nothing");
        }

        [Fact]
        public void AGuardedFormNeedsTheQueryToHaveProvenTheGuard()
        {
            var alone = UuidEquality();

            Rewrite(alone, Discriminated).Should().Be(alone.ToString(),
                "a document of the other kind may not carry the path at all, so nothing is known about it");

            Rewrite(And(KindIs("A"), UuidEquality()), Discriminated).Should().Contain("CAST",
                "the wrong discriminator proves nothing");

            Rewrite(And(KindIs("B"), UuidEquality()), Discriminated).Should().Be($"AND(=($1, 'B'), =($0, '{Canonical}'))",
                "the guard is proven by a conjunct of the very predicate being rewritten, and that conjunct stays");
        }

        [Fact]
        public void TheLoweredFormIsWhatRoutingReads()
        {
            var rewritten = CosmosFactRewriter.Rewrite(UuidEquality(), _fields, Container(Unconditional), "c", _rex);

            CosmosPartitionKeyExtractor.TryExtract(rewritten, _fields, Container(Unconditional), "c", out var values)
                .Should().BeTrue("a plain string equality on the partition key path is what pins a partition");

            values.Should().ContainSingle().Which.Should().Be(Canonical);

            CosmosPartitionKeyExtractor.TryExtract(UuidEquality(), _fields, Container(Unconditional), "c", out _)
                .Should().BeFalse("and the cast form pins nothing, which is the state of things today");
        }

        [Fact]
        public void TheLiteralIsWrittenInTheStoredSpelling()
        {
            var upper = _rex.makeCall(SqlStdOperatorTable.EQUALS,
                _rex.makeCast(_types.createSqlType(SqlTypeName.UUID), Ref(0, SqlTypeName.VARCHAR)),
                _rex.makeLiteral(java.util.UUID.fromString(Canonical.ToUpperInvariant()), _types.createSqlType(SqlTypeName.UUID), false));

            Rewrite(upper, Unconditional).Should().Be($"=($0, '{Canonical}')",
                "the value is the same however the query spelled it, and the stored form is the canonical one");
        }

        /// <summary>
        /// A disjunction is descended into, and an earlier draft refused to on a reason that does not
        /// hold.
        /// </summary>
        /// <remarks>
        /// The worry was that a fact proven from the conjuncts beside a branch does not hold of the
        /// rows that branch keeps. It does: a conditional fact is proven from the <em>top-level</em>
        /// conjuncts, which hold of every row the whole predicate keeps. Over a row where the guard
        /// fails the conjunction is false however the branch reads, and over one where it holds the
        /// fact holds too. Refusing cost the <c>IN</c> case, which is a disjunction once expanded.
        /// </remarks>
        [Fact]
        public void ADisjunctionIsDescendedInto()
        {
            var disjunction = _rex.makeCall(SqlStdOperatorTable.OR, UuidEquality(), KindIs("B"));

            Rewrite(disjunction, Unconditional).Should().Be($"OR(=($0, '{Canonical}'), =($1, 'B'))",
                "the fact holds of every document, so it holds inside a branch");

            // And a guarded fact still needs its guard, wherever the comparison sits.
            Rewrite(disjunction, Discriminated).Should().Contain("CAST",
                "nothing proved the branch applies, so there is no fact to lower with");

            Rewrite(And(KindIs("B"), disjunction), Discriminated).Should().Be(
                $"AND(=($1, 'B'), OR(=($0, '{Canonical}'), =($1, 'B')))",
                "and the conjunct that proves it holds of every row the predicate keeps, branch or no branch");
        }

        [Fact]
        public void APathTheSchemaSaysNothingAboutIsLeftAlone()
        {
            var other = _rex.makeCall(SqlStdOperatorTable.EQUALS,
                _rex.makeCast(_types.createSqlType(SqlTypeName.UUID), Ref(1, SqlTypeName.VARCHAR)),
                Uuid(Canonical));

            Rewrite(other, Unconditional).Should().Be(other.ToString());
        }

    }

}
