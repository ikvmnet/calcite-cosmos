using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Extensions.Adapter.Cursor;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using org.apache.calcite;
using org.apache.calcite.adapter.java;
using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// The rows a lookup join writes, read by what sits above it, against Calcite's own join of the same statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each statement is planned twice, each time over fresh stub containers holding the same documents: once as the
    /// adapter plans it, with a <see cref="CosmosLookupJoin"/>, and once with the lookup rule excluded, so that Calcite
    /// joins the two sides itself. Both are run, and the rows must be the same rows holding the same values in the same
    /// boxes. The box is the point: a joined row is read by the operators above it, a sort, a grouping, a calc, a
    /// second join, and by the connection's reader, and each reads a field in the box Calcite holds a value of that
    /// type in.
    /// </para>
    /// <para>
    /// The small side holds a <c>NOT NULL</c> value of each type <see cref="Types"/> lists, each held in a CLR value
    /// type in Calcite's physical row. A reader above the join casts such a field to Java's box, or to
    /// <see cref="java.lang.Number"/>, so a field unboxed and boxed again as the CLR's own <see cref="byte"/>,
    /// <see cref="short"/>, <see cref="int"/>, <see cref="long"/>, <see cref="float"/>, <see cref="double"/> or
    /// <see cref="bool"/> fails it where Calcite's join answers.
    /// </para>
    /// </remarks>
    public class CosmosLookupJoinRowTests
    {

        static readonly CosmosContainerMetadata Products = TestReasoning.Bind(new CosmosContainerMetadata("products", new[] { "/category" }));
        static readonly CosmosContainerMetadata Orders = TestReasoning.Bind(new CosmosContainerMetadata("orders", new[] { "/customer" }));

        /// <summary>
        /// The products the probe side reads, two of them, keyed <c>p7</c> and <c>p8</c>, numbered 7 and 8, and stamped
        /// as the service stamps every document.
        /// </summary>
        static readonly string[] ProductDocuments =
        [
            """{"id":"p7","category":"x","n":7,"_ts":1700000007}""",
            """{"id":"p8","category":"x","n":8,"_ts":1700000008}""",
        ];

        /// <summary>
        /// The orders a second lookup reads, keyed as the products are.
        /// </summary>
        static readonly string[] OrderDocuments =
        [
            """{"id":"p7","customer":"c"}""",
            """{"id":"p8","customer":"c"}""",
        ];

        /// <summary>
        /// Answers every statement with every document, so that the join itself decides which rows pair.
        /// </summary>
        sealed class StubExecutor(string[] documents) : ICosmosQueryExecutor
        {

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                return ListCursor.Documents(documents);
            }

        }

        sealed class TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory) : DataContext
        {

            public SchemaPlus getRootSchema() => rootSchema;

            public JavaTypeFactory getTypeFactory() => typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            /// <summary>
            /// The session's time zone, which a local-time-zone value is read in, and nothing else.
            /// </summary>
            public object get(string name) =>
                name == DataContext.Variable.TIME_ZONE.camelName ? java.util.TimeZone.getTimeZone("UTC") : null!;

        }

        /// <summary>
        /// A SQL type of the small side's column: two values in ascending order, on the rows keyed <c>p7</c> and
        /// <c>p8</c>, a third on the row keyed <c>p9</c>, which no document matches, and an expression computing over
        /// the column.
        /// </summary>
        sealed record ColumnType(string Name, string First, string Second, string Unmatched, string Compute);

        /// <summary>
        /// Types whose <c>NOT NULL</c> value Calcite holds in a CLR value type in its physical row, the dates and
        /// times in the <see cref="int"/> and <see cref="long"/> it holds them in, and one interval of each:
        /// <c>INTERVAL YEAR TO MONTH</c> in an <see cref="int"/> and <c>INTERVAL DAY TO SECOND</c> in a
        /// <see cref="long"/>.
        /// </summary>
        /// <remarks>
        /// <c>DECIMAL</c> is not among them: Calcite holds it in a <see cref="java.math.BigDecimal"/>, a reference. Nor
        /// is <c>TIME WITH TIME ZONE</c>, which Calcite holds in an <see cref="int"/> but whose value its own plan makes
        /// from no literal or cast, so that no small side holds one.
        /// </remarks>
        static readonly ColumnType[] Types =
        [
            new("TINYINT", "CAST(3 AS TINYINT)", "CAST(5 AS TINYINT)", "CAST(4 AS TINYINT)", "v.x + 1"),
            new("SMALLINT", "CAST(3 AS SMALLINT)", "CAST(5 AS SMALLINT)", "CAST(4 AS SMALLINT)", "v.x + 1"),
            new("INTEGER", "3", "5", "4", "v.x + 1"),
            new("BIGINT", "CAST(3 AS BIGINT)", "CAST(5 AS BIGINT)", "CAST(4 AS BIGINT)", "v.x + 1"),
            new("REAL", "CAST(1.5 AS REAL)", "CAST(2.5 AS REAL)", "CAST(2 AS REAL)", "v.x + 1"),
            new("FLOAT", "CAST(1.5 AS FLOAT)", "CAST(2.5 AS FLOAT)", "CAST(2 AS FLOAT)", "v.x + 1"),
            new("DOUBLE", "1.5e0", "2.5e0", "2e0", "v.x + 1"),
            new("BOOLEAN", "FALSE", "TRUE", "TRUE", "NOT v.x"),
            new("DATE", "DATE '2026-01-02'", "DATE '2026-03-04'", "DATE '2026-02-03'", "v.x + INTERVAL '1' DAY"),
            new("TIME", "TIME '01:02:03'", "TIME '05:06:07'", "TIME '03:04:05'", "v.x + INTERVAL '1' HOUR"),
            new("TIMESTAMP", "TIMESTAMP '2026-01-02 03:04:05'", "TIMESTAMP '2026-03-04 05:06:07'", "TIMESTAMP '2026-02-03 04:05:06'", "v.x + INTERVAL '1' DAY"),
            new("TIME WITH LOCAL TIME ZONE", "CAST(TIME '01:02:03' AS TIME WITH LOCAL TIME ZONE)", "CAST(TIME '05:06:07' AS TIME WITH LOCAL TIME ZONE)", "CAST(TIME '03:04:05' AS TIME WITH LOCAL TIME ZONE)", "v.x + INTERVAL '1' HOUR"),
            new("TIMESTAMP WITH LOCAL TIME ZONE", "CAST(TIMESTAMP '2026-01-02 03:04:05' AS TIMESTAMP WITH LOCAL TIME ZONE)", "CAST(TIMESTAMP '2026-03-04 05:06:07' AS TIMESTAMP WITH LOCAL TIME ZONE)", "CAST(TIMESTAMP '2026-02-03 04:05:06' AS TIMESTAMP WITH LOCAL TIME ZONE)", "v.x + INTERVAL '1' DAY"),
            new("TIMESTAMP WITH TIME ZONE", "TIMESTAMP WITH TIME ZONE '2026-01-02 03:04:05 UTC'", "TIMESTAMP WITH TIME ZONE '2026-03-04 05:06:07 UTC'", "TIMESTAMP WITH TIME ZONE '2026-02-03 04:05:06 UTC'", "v.x + INTERVAL '1' DAY"),
            new("INTERVAL DAY TO SECOND", "INTERVAL '1 02:03:04' DAY TO SECOND", "INTERVAL '2 03:04:05' DAY TO SECOND", "INTERVAL '1 12:00:00' DAY TO SECOND", "v.x * 2"),
            new("INTERVAL YEAR TO MONTH", "INTERVAL '1-2' YEAR TO MONTH", "INTERVAL '2-3' YEAR TO MONTH", "INTERVAL '1-8' YEAR TO MONTH", "v.x * 2"),
        ];

        /// <summary>
        /// What reads the small side's column above the join, each written over the small side <c>v(x, k)</c>, the probe
        /// side <c>t</c> and the join between them.
        /// </summary>
        /// <remarks>
        /// Each names how many lookups its plan holds, so that a plan which no longer has the shape is a failure rather
        /// than a comparison of two of Calcite's own joins.
        /// </remarks>
        static readonly (string Name, int Lookups, Func<ColumnType, string> Sql)[] Shapes =
        [
            ("a projection computing over it", 1, c => $"SELECT {c.Compute} AS y, v.x, t.\"id\" FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\""),
            ("a sort by it", 1, c => $"SELECT v.x, t.\"id\" FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" ORDER BY v.x DESC"),
            ("a grouping by it with aggregates", 1, c => $"SELECT v.x, COUNT(*) AS c, MAX(t.\"id\") AS m FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" GROUP BY v.x"),
            ("a filter over both sides", 1, c => $"SELECT v.x, t.\"id\" FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" WHERE v.x > {c.First} OR t.\"n\" = 7"),
            ("a second lookup", 2, c => $"SELECT v.x, t.\"id\", u.\"id\" AS o FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" JOIN (SELECT o.\"id\" FROM orders AS o) AS u ON v.k = u.\"id\""),
            ("a hash join on it", 1, c => $"SELECT v.x, t.\"id\", w.name FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" JOIN (VALUES ({c.First}, 'p7', 'one'), ({c.Second}, 'p8', 'two')) AS w(x, id, name) ON v.x = w.x AND t.\"id\" = w.id"),
            ("a left join's unmatched row sorted by it", 1, c => $"SELECT v.x, t.\"id\" FROM {Small(c, unmatched: true)} LEFT JOIN {Probe} AS t ON v.k = t.\"id\" ORDER BY v.x, v.k"),
            ("a left join's unmatched row computed over", 1, c => $"SELECT {c.Compute} AS y, v.x, t.\"id\" FROM {Small(c, unmatched: true)} LEFT JOIN {Probe} AS t ON v.k = t.\"id\""),
            ("a window ordered by it", 1, c => $"SELECT v.x, t.\"id\", ROW_NUMBER() OVER (ORDER BY v.x DESC) AS r FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" ORDER BY r"),
            ("a sub-query over it", 1, c => $"SELECT v.x, t.\"id\" FROM {Small(c)} JOIN {Probe} AS t ON v.k = t.\"id\" WHERE v.x IN (SELECT w.x FROM (VALUES ({c.Second})) AS w(x))"),
        ];

        /// <summary>
        /// The container side: a product's id and its number, read as a <c>BIGINT</c>.
        /// </summary>
        const string Probe = "(SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING BIGINT) AS \"n\" FROM products AS c)";

        /// <summary>
        /// The small side: a value of the type on each of the rows keyed <c>p7</c> and <c>p8</c>, and on one keyed
        /// <c>p9</c>, which no product is, where a left join keeps it.
        /// </summary>
        static string Small(ColumnType c, bool unmatched = false) =>
            unmatched
                ? $"(VALUES ({c.First}, 'p7'), ({c.Second}, 'p8'), ({c.Unmatched}, 'p9')) AS v(x, k)"
                : $"(VALUES ({c.First}, 'p7'), ({c.Second}, 'p8')) AS v(x, k)";

        public static TheoryData<string, string> TypesAndShapes()
        {
            var data = new TheoryData<string, string>();

            foreach (var type in Types)
                foreach (var shape in Shapes)
                    data.Add(type.Name, shape.Name);

            return data;
        }

        /// <summary>
        /// A value of the small side read above the join, by each operator that reads one, answers what Calcite's own
        /// join answers, in the same box.
        /// </summary>
        [Theory]
        [MemberData(nameof(TypesAndShapes))]
        public async Task TheSmallSidesValueIsReadAboveTheJoinAsCalcitesJoinWritesIt(string type, string shape)
        {
            var (_, lookups, sql) = Shapes.Single(s => s.Name == shape);

            await ShouldAnswerAsCalciteDoes(sql(Types.Single(t => t.Name == type)), lookups);
        }

        /// <summary>
        /// The statements of the numeric keys, each named for what reads the numbers above the join.
        /// </summary>
        static readonly (string Name, string Sql)[] NumericKeys =
        [
            ("its other number projected", "SELECT v.i, t.\"id\" FROM (VALUES (1, CAST(7 AS BIGINT)), (2, CAST(8 AS BIGINT)), (3, CAST(9 AS BIGINT))) AS v(i, k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING BIGINT) AS \"n\" FROM products AS c) AS t ON v.k = t.\"n\""),
            ("its other number, sorted by the key", "SELECT v.i, t.\"id\" FROM (VALUES (1, CAST(7 AS BIGINT)), (2, CAST(8 AS BIGINT)), (3, CAST(9 AS BIGINT))) AS v(i, k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING BIGINT) AS \"n\" FROM products AS c) AS t ON v.k = t.\"n\" ORDER BY v.k"),
            ("the key plus one", "SELECT v.k + 1 AS b, t.\"id\" FROM (VALUES (1, CAST(7 AS BIGINT)), (2, CAST(8 AS BIGINT))) AS v(i, k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING BIGINT) AS \"n\" FROM products AS c) AS t ON v.k = t.\"n\""),
            ("an INTEGER key plus one, sorted by the key", "SELECT v.k + 1 AS b, t.\"id\" FROM (VALUES (1, 7), (2, 8)) AS v(i, k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t ON v.k = t.\"n\" ORDER BY v.k"),
            ("a DOUBLE key plus one, sorted by the key", "SELECT v.k + 1 AS b, t.\"id\" FROM (VALUES (1, 7e0), (2, 8e0)) AS v(i, k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING DOUBLE) AS \"n\" FROM products AS c) AS t ON v.k = t.\"n\" ORDER BY v.k"),
        ];

        public static TheoryData<string> NumericKeyStatements()
        {
            var data = new TheoryData<string>();

            foreach (var (name, _) in NumericKeys)
                data.Add(name);

            return data;
        }

        /// <summary>
        /// A small side of numbers joined to a container's number, read above the join: its other number projected,
        /// the rows sorted by the key, and the key plus one.
        /// </summary>
        [Theory]
        [MemberData(nameof(NumericKeyStatements))]
        public async Task ANumericKeyIsReadAboveTheJoinAsCalcitesJoinWritesIt(string statement)
        {
            await ShouldAnswerAsCalciteDoes(NumericKeys.Single(k => k.Name == statement).Sql, 1);
        }

        /// <summary>
        /// A count of the small side's rows per key, a <c>BIGINT</c> an aggregate computed, read above the join.
        /// </summary>
        [Fact]
        public async Task ACountOfTheSmallSideIsReadAboveTheJoinAsCalcitesJoinWritesIt()
        {
            await ShouldAnswerAsCalciteDoes(
                "SELECT x.c, t.\"id\" FROM (SELECT v.k, COUNT(*) AS c FROM (VALUES (1, 'p7'), (2, 'p7'), (3, 'p8')) AS v(i, k) GROUP BY v.k) AS x JOIN (SELECT c.\"id\" FROM products AS c) AS t ON x.k = t.\"id\" ORDER BY x.c",
                1);
        }

        /// <summary>
        /// A number of the small side beside a text key on the document's id, projected and nothing more, which is the
        /// connection's reader's case: the row above the join is the row it reads.
        /// </summary>
        [Fact]
        public async Task ASmallSidesNumberBesideATextKeyIsWrittenAsCalcitesJoinWritesIt()
        {
            await ShouldAnswerAsCalciteDoes(
                "SELECT v.q, t.\"id\" FROM (VALUES (5, 'p7'), (6, 'p8')) AS v(q, k) JOIN (SELECT c.\"id\" FROM products AS c) AS t ON v.k = t.\"id\"",
                1);
        }

        /// <summary>
        /// A <c>NOT NULL</c> number of the container's side, the service's own stamp, read above the join: the
        /// container's row is read into Calcite's boxes and is joined as Calcite's join would join it.
        /// </summary>
        [Fact]
        public async Task TheContainersNumberIsReadAboveTheJoinAsCalcitesJoinWritesIt()
        {
            await ShouldAnswerAsCalciteDoes(
                "SELECT v.i, t.\"id\", t.\"_ts\" + 1 AS s FROM (VALUES (1, 'p7'), (2, 'p8')) AS v(i, k) JOIN (SELECT c.\"id\", c.\"_ts\" FROM products AS c) AS t ON v.k = t.\"id\" ORDER BY t.\"_ts\" DESC",
                1);
        }

        /// <summary>
        /// Plans and runs a statement with the lookup and without it, and holds the lookup's rows, read by both of its
        /// bodies, to Calcite's.
        /// </summary>
        async Task ShouldAnswerAsCalciteDoes(string sql, int lookups)
        {
            var (lookupPlan, lookupContext) = Plan(sql, lookup: true);
            var lookupText = RelOptUtil.toString(lookupPlan);
            Count<CosmosLookupJoin>(lookupPlan).Should().Be(lookups, "the statement is planned with {0} lookup join(s): {1}", lookups, lookupText);

            var (calcitePlan, calciteContext) = Plan(sql, lookup: false);
            var calciteText = RelOptUtil.toString(calcitePlan);
            Count<CosmosLookupJoin>(calcitePlan).Should().Be(0, "Calcite joins the sides itself once the rule is excluded: " + calciteText);

            var ordered = sql.Contains("ORDER BY", StringComparison.Ordinal);
            var expected = Rows(await ExecuteAsync(calcitePlan, calciteContext), ordered);

            Rows(await ExecuteAsync(lookupPlan, lookupContext), ordered).Should().Equal(expected, "the lookup's rows, read by its awaiting body, are Calcite's:\n{0}\nagainst\n{1}", lookupText, calciteText);
            Rows(ExecutePulled(lookupPlan, lookupContext), ordered).Should().Equal(expected, "and read by its synchronous body:\n{0}", lookupText);
        }

        /// <summary>
        /// Plans a statement over fresh stub containers, with the lookup rule or without it.
        /// </summary>
        static (RelNode Plan, DataContext Context) Plan(string sql, bool lookup)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var products = new CosmosTable(Products, new StubExecutor(ProductDocuments));
            var orders = new CosmosTable(Orders, new StubExecutor(OrderDocuments));

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", products);
            rootSchema.add("orders", orders);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var validator = SqlValidatorUtil.newValidator(
                org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance), catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(org.apache.calcite.rel.RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            // The sub-queries removed first, and the windows lifted out of their projections by the planner, as a host's
            // program does: see PlannerHarness.Rewrite and AddAsyncRules.
            var logical = PlannerHarness.Rewrite(converter.convertQuery(validator.validate(parsed), false, true).project());

            var excluded = new List<string>();
            foreach (var table in new[] { products, orders })
                foreach (var rule in CosmosRules.GetRules(table.Convention))
                {
                    planner.addRule(rule);
                    if (rule is CosmosLookupJoinRule)
                        excluded.Add(java.util.regex.Pattern.quote(rule.ToString()));
                }

            if (lookup == false)
                planner.setRuleDescExclusionFilter(java.util.regex.Pattern.compile(string.Join("|", excluded.Distinct())));

            PlannerHarness.AddAsyncRules(planner);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            // Calcite's CALC_PROGRAM, after the planner: see CosmosLookupJoinTests.ToCalc for why it is a pass of its own.
            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();
            foreach (var rule in ClrCursorRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(planner.findBestExp());

            return (hep.findBestExp(), new TestDataContext(rootSchema.plus(), typeFactory));
        }

        static ClrCursorFactory Implement(RelNode rel)
        {
            var implementor = new ClrCursorRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            return implementor.ImplementRoot((ClrCursorRel)rel, ClrCursorPrefer.Array);
        }

        static async Task<List<object?>> ExecuteAsync(RelNode rel, DataContext context)
        {
            var rows = new List<object?>();

            await using (var cursor = await Implement(rel).OpenAsync(context, CancellationToken.None))
                while (await cursor.ReadAsync(CancellationToken.None))
                    rows.Add(cursor.Current);

            return rows;
        }

        static List<object?> ExecutePulled(RelNode rel, DataContext context)
        {
            var rows = new List<object?>();

            using (var cursor = Implement(rel).Open(context))
                while (cursor.Read())
                    rows.Add(cursor.Current);

            return rows;
        }

        /// <summary>
        /// Writes each row as its values, each with the class of the box holding it, in the order read where the
        /// statement orders its rows and sorted where it does not.
        /// </summary>
        static List<string> Rows(List<object?> rows, bool ordered)
        {
            var written = rows.Select(Row).ToList();

            if (ordered == false)
                written.Sort(StringComparer.Ordinal);

            return written;
        }

        static string Row(object? row) => row is object[] values ? "(" + string.Join(", ", values.Select(Value)) + ")" : Value(row);

        static string Value(object? value) => value switch
        {
            null => "null",
            IFormattable formattable => value.GetType().FullName + " " + formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.GetType().FullName + " " + value,
        };

        /// <summary>
        /// Counts the nodes of a type in a tree.
        /// </summary>
        static int Count<T>(RelNode node) where T : class
        {
            var count = node is T ? 1 : 0;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                count += Count<T>((RelNode)inputs.get(i));

            return count;
        }

    }

}
