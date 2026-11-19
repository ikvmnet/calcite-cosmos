using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;

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
            new(_rex, container ?? TestReasoning.Bind(new CosmosContainerMetadata("products")));

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
            var implementor = new CosmosImplementor(_rex, TestReasoning.Bind(new CosmosContainerMetadata("products")), "p");

            implementor.Build().Sql.Should().Be("SELECT VALUE p FROM products p");
            implementor.Root.ToString().Should().Be("p");
        }

        [Fact]
        public void TranslatedPredicateAndParametersFlowIntoTheStatement()
        {
            var implementor = Implementor();
            implementor.Fields = new[] { CosmosPath.Root("c").Property("name") };

            implementor.Query.Where = implementor.TranslateCondition(
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

            var first = implementor.TranslateCondition(_rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("a")));
            var second = implementor.TranslateCondition(_rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("b")));

            first.Should().Be("(c.name = @p0)");
            second.Should().Be("(c.name = @p1)");
            implementor.Parameters.Count.Should().Be(2);
        }

        /// <remarks>
        /// A projection rebinds the field ordinals, so a condition rendered afterwards must see
        /// the new paths rather than the ones captured at construction. A boolean column is written
        /// as S proves it since the switch (h15), compared with a bound <c>true</c>.
        /// </remarks>
        [Fact]
        public void RebindingFieldsAffectsSubsequentTranslations()
        {
            var implementor = Implementor();

            implementor.Fields = new[] { CosmosPath.Root("c").Property("a") };
            implementor.TranslateCondition(Ref(0, SqlTypeName.BOOLEAN)).Should().Be("(c.a = @p0)");

            implementor.Fields = new[] { CosmosPath.Root("c").Property("b") };
            implementor.TranslateCondition(Ref(0, SqlTypeName.BOOLEAN)).Should().Be("(c.b = @p1)");
            implementor.Parameters.Parameters.Should().HaveCount(2).And.OnlyContain(p => Equals(p.Value, true));
        }

        /// <remarks>
        /// The path readers a node takes from the implementor resolve what its translator does, the emulator's escaped
        /// member names among them: a path naming U+0000 is no path where the name is stored escaped, so the traversal
        /// that asks it is not written over a name that reads nothing.
        /// </remarks>
        [Theory]
        [InlineData(CosmosServiceTarget.Azure, true)]
        [InlineData(CosmosServiceTarget.Emulator, false)]
        public void PathsResolveWhatTheTranslatorResolves(CosmosServiceTarget target, bool resolves)
        {
            var implementor = Implementor(TestReasoning.Bind(new CosmosContainerMetadata("products")).WithServiceTarget(target));
            implementor.Fields = new[] { CosmosPath.Root("c").Property("m\0") };

            implementor.CreatePaths().TryResolvePath(Ref(0, SqlTypeName.VARCHAR), out _).Should().Be(resolves);
            implementor.CreateTranslator(null).Paths.TryResolvePath(Ref(0, SqlTypeName.VARCHAR), out _).Should().Be(resolves);
        }

        /// <remarks>
        /// The element a traversal yields is no path the service addresses: where the target stores a name escaped the path
        /// readers decline it as one naming an escape, and elsewhere a translator that reaches it to write it refuses it as
        /// the defect it is, since only the correlate rule's question binds one and nothing it binds is translated.
        /// </remarks>
        [Theory]
        [InlineData(CosmosServiceTarget.Azure)]
        [InlineData(CosmosServiceTarget.Emulator)]
        public void TheElementATraversalYieldsIsNoPathTheServiceAddresses(CosmosServiceTarget target)
        {
            var implementor = Implementor(TestReasoning.Bind(new CosmosContainerMetadata("products")).WithServiceTarget(target));
            implementor.Fields = new[] { CosmosPath.Root("c").Property("tags").Append(CosmosPathSegment.Yielded("VARCHAR", 0)) };

            CosmosRexPaths.NamesAnEscape(implementor.Fields[0]).Should().BeTrue();
            if (target == CosmosServiceTarget.Emulator)
            {
                implementor.CreatePaths().TryResolvePath(Ref(0, SqlTypeName.VARCHAR), out _).Should().BeFalse();
                implementor.CreateTranslator(null).TryTranslateValue(Ref(0, SqlTypeName.VARCHAR), out _).Should().BeFalse();
                return;
            }

            var act = () => implementor.CreateTranslator(null).TryTranslateValue(Ref(0, SqlTypeName.VARCHAR), out _);
            act.Should().Throw<System.InvalidOperationException>().WithMessage("*c.tags[yield VARCHAR 0]*element a traversal yields*");
        }

        // ── Binding a correlate's row for the correlate rule's question ────────────

        const string Tags = """JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)""";

        /// <summary>The products container's row as every binding binds it.</summary>
        static readonly string[] Documents = ["c", "c.id", "c._ts", "c._etag", "c.category"];

        /// <summary>The products container's row, then the given columns.</summary>
        static string?[] Products(params string?[] right) => [.. Documents, .. right];

        /// <summary>The registered members of a statement of a kind, outermost first.</summary>
        static IReadOnlyList<T> Members<T>(string sql) => RegisteredMembers.Of(sql).OfType<T>().ToList();

        /// <summary>
        /// Binds a correlate's row with the elements on, as the correlate rule's question does; and checks that the same row
        /// without them binds as every other caller has it bound, every field the elements bind there unbound and every
        /// other field the same, with the readings the same where both bind.
        /// </summary>
        static (IReadOnlyList<string?> Fields, IReadOnlyList<CosmosReading> Readings) Elements(org.apache.calcite.rel.core.Correlate correlate, params string?[] plain)
        {
            CosmosImplementor.TryBindElements(correlate, out var fields, out var readings, out var container).Should().BeTrue();
            container!.Name.Should().Be("products");

            CosmosImplementor.TryBindOutput(correlate, out var unbound, out var unboundReadings, out _).Should().BeTrue();
            unbound.Select(f => f?.ToString()).Should().Equal(plain, "without the elements the row binds as it always has");
            fields.Should().HaveCount(unbound.Count);
            for (var i = 0; i < unbound.Count; i++)
                if (unbound[i] is not null)
                {
                    fields[i].Should().Be(unbound[i]);
                    readings[i].Should().Be(unboundReadings[i]);
                }

            return (fields.Select(f => f?.ToString()).ToList(), readings);
        }

        /// <summary>
        /// The element of an <c>UNNEST</c> binds, through the renaming projection its alias registers, to its array's path and
        /// a step naming the array's element type and the traversal, read as the text.
        /// </summary>
        [Fact]
        public void AnElementBindsToItsArraysPathAndAStepOfItsOwn()
        {
            var correlate = Members<org.apache.calcite.rel.core.Correlate>($"""SELECT c."id", u.t FROM products AS c, UNNEST({Tags}) AS u(t) WHERE JSON_VALUE(c."DOC", '$.kind') = 'A' AND u.t = '1'""")[0];
            ((org.apache.calcite.plan.volcano.RelSubset)correlate.getRight()).getOriginal().Should().BeAssignableTo<org.apache.calcite.rel.core.Project>("the alias registers a renaming projection over the uncollect");

            var (fields, readings) = Elements(correlate, Products([null]));

            fields.Should().Equal(Products("c.tags[yield VARCHAR 0]"));
            readings[5].Should().Be(CosmosReading.Text);
        }

        /// <summary>
        /// Through the right's own filter, the one Calcite's transposition writes into the right, and over a <c>LEFT</c>
        /// correlate, whose element is null where the array holds none, the element binds alike.
        /// </summary>
        [Fact]
        public void AnElementBindsThroughTheRightsOwnFilterAndOverALeftCorrelate()
        {
            var correlate = Members<org.apache.calcite.rel.core.Correlate>($"""SELECT c."id", u.t FROM products AS c, UNNEST({Tags}) AS u(t) WHERE u.t = '1'""")[0];
            var right = correlate.getRight();
            var element = _rex.makeInputRef(((org.apache.calcite.rel.type.RelDataTypeField)right.getRowType().getFieldList().get(0)).getType(), 0);
            var filtered = org.apache.calcite.rel.logical.LogicalFilter.create(right, _rex.makeCall(SqlStdOperatorTable.NOT_EQUALS, element, Str("z")));
            var moved = (org.apache.calcite.rel.core.Correlate)correlate.copy(correlate.getTraitSet(), com.google.common.collect.ImmutableList.of(correlate.getLeft(), filtered));

            Elements(moved, Products([null])).Fields.Should().Equal(Products("c.tags[yield VARCHAR 0]"));

            var left = Members<org.apache.calcite.rel.core.Correlate>($"""SELECT c."id", u.t FROM products AS c LEFT JOIN UNNEST({Tags}) AS u(t) ON TRUE WHERE u.t = '1'""")[0];
            left.getJoinType().Should().Be(org.apache.calcite.rel.core.JoinRelType.LEFT);
            Elements(left, Products([null])).Fields.Should().Equal(Products("c.tags[yield VARCHAR 0]"));
        }

        /// <summary>
        /// The element type is the array's component's by name, any length dropped, and data: a <c>RETURNING INTEGER
        /// ARRAY</c>'s element binds with its type, which no row reads, and <c>VARCHAR(1)</c>'s is <c>VARCHAR</c>'s.
        /// <c>StringToArray</c>'s element, an array parsed out of the text, is no plain <c>JSON_QUERY</c>'s and stays unbound.
        /// </summary>
        [Fact]
        public void AnElementBindsWithItsTypeAsDataAndStringToArraysStaysUnbound()
        {
            var integers = Members<org.apache.calcite.rel.core.Correlate>("""SELECT c."id", u.n FROM products AS c, UNNEST(JSON_QUERY(c."DOC", '$.nums' RETURNING INTEGER ARRAY)) AS u(n) WHERE u.n = 1""")[0];
            var (fields, readings) = Elements(integers, Products([null]));
            fields.Should().Equal(Products("c.nums[yield INTEGER 0]"));
            readings[5].Should().Be(CosmosReading.Text);

            var short1 = Members<org.apache.calcite.rel.core.Correlate>("""SELECT c."id", u.t FROM products AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR(1) ARRAY)) AS u(t) WHERE u.t = '1'""")[0];
            Elements(short1, Products([null])).Fields.Should().Equal(Products("c.tags[yield VARCHAR 0]"));

            var parsed = Members<org.apache.calcite.rel.core.Correlate>("""SELECT c."id" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c."DOC", '$.tags'))) AS t""")[0];
            Elements(parsed, Products([null])).Fields.Should().Equal(Products([null]));
        }

        /// <summary>
        /// A lateral's column that reads the correlation variable alone binds to the document path it reads, with the reading
        /// a projection gives it: the text accessor as text, a <c>RETURNING</c> as typed, a plain <c>JSON_QUERY</c> as JSON
        /// text, a field of the left as the left reads it. A computed column and a column of the lateral's own row stay
        /// unbound. The lateral's own <c>WHERE</c> is walked through.
        /// </summary>
        [Fact]
        public void ALateralsAccessorColumnsBindToThePathsTheyRead()
        {
            var correlate = Members<org.apache.calcite.rel.core.Correlate>("""
                SELECT c."id", l.k2 FROM products AS c CROSS JOIN LATERAL (SELECT JSON_VALUE(c."DOC", '$.kind') AS k2,
                JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER) AS n2, JSON_QUERY(c."DOC", '$.o') AS o2, UPPER(JSON_VALUE(c."DOC", '$.body')) AS b2,
                c."id" AS i2, v.x0 AS z FROM (VALUES (1)) AS v(x0) WHERE JSON_VALUE(c."DOC", '$.m') = '1') AS l WHERE l.k2 = 'A'
                """)[0];

            var (fields, readings) = Elements(correlate, Products(null, null, null, null, null, null));

            fields.Should().Equal(Products("c.kind", "c.n", "c.o", null, "c.id", null));
            readings.Skip(5).Should().Equal(CosmosReading.Text, CosmosReading.Typed, CosmosReading.JsonText, CosmosReading.Typed, readings[1], CosmosReading.Typed);

            // Over a sub-query, a field the left reads as text passes through as text, and an accessor over it, which parses
            // the text, is refused as it is over a field reference; the element beneath passes through as the element.
            var over = Members<org.apache.calcite.rel.core.Correlate>($"""
                SELECT x.k, l.k3 FROM (SELECT c."DOC", JSON_VALUE(c."DOC", '$.kind') AS k, u.t FROM products AS c, UNNEST({Tags}) AS u(t)) AS x
                CROSS JOIN LATERAL (SELECT x.k AS k3, JSON_VALUE(x.k, '$') AS j3, x.t AS t3, JSON_VALUE(x.t, '$') AS e3 FROM (VALUES (1)) AS v(x0)) AS l WHERE l.k3 = 'A'
                """)[0];

            (fields, readings) = Elements(over, "c", "c.kind", null, null, null, null, null);

            fields.Should().Equal("c", "c.kind", "c.tags[yield VARCHAR 0]", "c.kind", null, "c.tags[yield VARCHAR 0]", null);
            readings[3].Should().Be(CosmosReading.Text);
            readings[5].Should().Be(CosmosReading.Text);
        }

        /// <summary>
        /// A projected accessor over a field its input reads as text, which Calcite parses before reading the path, binds
        /// nothing, as a sub-query's column with the elements on and off and as a lateral's column alike: one reader binds a
        /// projected column for both.
        /// </summary>
        [Fact]
        public void AnAccessorOverATextFieldBindsNothingAsASubQuerysColumnAsALaterals()
        {
            var sub = Members<org.apache.calcite.rel.core.Correlate>($"""
                SELECT y.j, u.t FROM (SELECT x."DOC", JSON_VALUE(x.k, '$') AS j FROM (SELECT c."DOC", JSON_VALUE(c."DOC", '$.kind') AS k FROM products AS c) AS x WHERE x.k <> 'zz') AS y,
                UNNEST(JSON_QUERY(y."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t) WHERE y.j = 'A' AND u.t = '1'
                """)[0];
            Elements(sub, "c", null, null).Fields.Should().Equal("c", null, "c.tags[yield VARCHAR 0]");

            var lateral = Members<org.apache.calcite.rel.core.Correlate>("""
                SELECT x.k, l.j3 FROM (SELECT c."DOC", JSON_VALUE(c."DOC", '$.kind') AS k FROM products AS c) AS x
                CROSS JOIN LATERAL (SELECT JSON_VALUE(x.k, '$') AS j3 FROM (VALUES (1)) AS v(x0)) AS l WHERE l.j3 = 'A'
                """)[0];
            Elements(lateral, "c", "c.kind", null).Fields.Should().Equal("c", "c.kind", null);
        }

        /// <summary>
        /// An element surfaces through a sub-query on the left as the column it is, bound by the walk beneath, and the
        /// correlate above numbers its own traversal after it. The walk goes through the sub-query's filter, one that asks a
        /// recheck where the elements are off.
        /// </summary>
        [Fact]
        public void AnElementBeneathSurfacesThroughTheLeftAndTheWalkNumbersTraversalsInOrder()
        {
            var members = RegisteredMembers.Of($"""
                SELECT x."id", x.t, v.s FROM (SELECT c."id", c."DOC", u.t FROM products AS c, UNNEST({Tags}) AS u(t) WHERE u.t > ?) AS x,
                UNNEST(JSON_QUERY(x."DOC", '$.xs' RETURNING VARCHAR ARRAY)) AS v(s) WHERE JSON_VALUE(x."DOC", '$.kind') = 'A'
                """);
            var outer = members.OfType<org.apache.calcite.rel.core.Correlate>().First();
            var recheck = members.OfType<org.apache.calcite.rel.core.Filter>().Single(f => f.getCondition().ToString()!.Contains('?'));

            CosmosImplementor.TryBindOutput(recheck, out _, out var written).Should().BeTrue();
            written.HasFlag(CosmosClauses.Recheck).Should().BeTrue("the filter asks a recheck where the elements are off");

            Elements(outer, "c.id", "c", null, null).Fields.Should().Equal("c.id", "c", "c.tags[yield VARCHAR 0]", "c.xs[yield VARCHAR 1]");
        }

        /// <summary>
        /// Two traversals of one array are two paths; one traversal is one path wherever its column surfaces; and an accessor
        /// over the element, which reads its text parsed, binds nothing.
        /// </summary>
        [Fact]
        public void TwoTraversalsAreTwoPathsAndOneIsOneWhereverItSurfaces()
        {
            var twice = Members<org.apache.calcite.rel.core.Correlate>($"""
                SELECT c."id", u.t, v.s FROM products AS c, UNNEST({Tags}) AS u(t), UNNEST({Tags}) AS v(s) WHERE u.t = '1' AND v.s = '2'
                """)[0];
            Elements(twice, Products(null, null)).Fields.Should().Equal(Products("c.tags[yield VARCHAR 0]", "c.tags[yield VARCHAR 1]"));

            var surfaced = Members<org.apache.calcite.rel.core.Correlate>($"""
                SELECT x.a, x.b, x.j, x.ja, v.s FROM (SELECT c."DOC", u.t AS a, u.t AS b, JSON_VALUE(u.t, '$') AS j, JSON_VALUE(u.t, '$.a') AS ja
                FROM products AS c, UNNEST({Tags}) AS u(t)) AS x, UNNEST(JSON_QUERY(x."DOC", '$.xs' RETURNING VARCHAR ARRAY)) AS v(s) WHERE x.a = v.s
                """)[0];
            Elements(surfaced, "c", null, null, null, null, null).Fields
                .Should().Equal("c", "c.tags[yield VARCHAR 0]", "c.tags[yield VARCHAR 0]", null, null, "c.xs[yield VARCHAR 1]");
        }

        [Fact]
        public void ContainerMetadataIsReachableForRuleGuards()
        {
            var container = TestReasoning.Bind(new CosmosContainerMetadata("products", new[] { "/tenant" }));
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
            implementor.CreateTranslator(null).TryTranslate(node, out _).Should().BeFalse();
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

        // ── What a blind read can answer ──────────────────────────────────────────

        /// <summary>
        /// Each clause a statement can carry beyond its predicate and its projection, written into the
        /// builder the way the node that owns it writes it.
        /// </summary>
        public static TheoryData<string> ClausesBeyondThePredicate() =>
        [
            "DISTINCT",
            "aggregate",
            "GROUP BY",
            "ORDER BY",
            "ORDER BY RANK",
            "TOP",
            "OFFSET/LIMIT",
            "JOIN",
            "WHERE",
        ];

        static void Write(CosmosQueryBuilder query, string clause)
        {
            switch (clause)
            {
                case "DISTINCT": query.Distinct = true; break;
                case "aggregate": query.FlatProjection = true; query.SelectProperty("n", "COUNT(1)"); break;
                case "GROUP BY": query.AddGroupBy("c._ts"); break;
                case "ORDER BY": query.AddOrderBy("c._ts", descending: false); break;
                case "ORDER BY RANK": query.RankBy = "FullTextScore(c.name, 'a')"; break;
                case "TOP": query.Top = 1; break;
                case "OFFSET/LIMIT": query.Fetch = 1; break;
                case "JOIN": query.AddUnnest("t0", "c.tags"); break;
                case "WHERE": query.Where = query.Where is null ? "(c.price > 200)" : $"({query.Where} AND (c.price > 200))"; break;
            }
        }

        /// <remarks>
        /// The pinned predicate alone, which both reads answer: what the clauses below are measured
        /// against.
        /// </remarks>
        [Fact]
        public void APinnedPredicateAloneIsRead()
        {
            var single = Implementor();
            single.PointReadCandidate = "1";
            single.Build().PointReadId.Should().Be("1");

            var batch = Implementor();
            batch.PointReadSetCandidate = new[] { "1", "2" };
            batch.Build().PointReadIds.Should().Equal("1", "2");
        }

        /// <remarks>
        /// A read returns a row per document, and every one of these asks for something else: a row
        /// per value, per group, per element, or a subset in an order. <c>DISTINCT</c> was missing
        /// from both lists, and over two documents sharing a value the batch returned the value twice
        /// (soundness probe P22c). A <c>WHERE</c> the pinning filter did not write is a subset the
        /// read ignores, which a view queried by partition key and <c>id</c> reached (review probe
        /// H6c).
        /// </remarks>
        [Theory]
        [MemberData(nameof(ClausesBeyondThePredicate))]
        public void AClauseBeyondThePredicateWithdrawsBothReads(string clause)
        {
            var single = Implementor();
            single.PointReadCandidate = "1";
            Write(single.Query, clause);

            single.Build().PointReadId.Should().BeNull("a point read cannot answer a statement carrying {0}", clause);

            var batch = Implementor();
            batch.PointReadSetCandidate = new[] { "1", "2" };
            Write(batch.Query, clause);

            batch.Build().PointReadIds.Should().BeNull("a batch of point reads cannot answer a statement carrying {0}", clause);
        }

    }

}
