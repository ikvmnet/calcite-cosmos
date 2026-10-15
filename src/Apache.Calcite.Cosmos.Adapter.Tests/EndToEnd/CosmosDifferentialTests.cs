using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Extensions.Adapter.Cursor;

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
using Xunit.Sdk;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// Checks every pushdown against an oracle: the same SQL planned with the full rule set and with
    /// only the way-out converter, both executed against the same live container, rows required
    /// equal.
    /// </summary>
    /// <remarks>
    /// See <c>DESIGN.md</c> under <em>Differential testing</em>. The oracle is the adapter's own
    /// minimal mode — the scan read whole, Calcite evaluating everything in process — so a mismatch
    /// indicts the pushdown rather than the plumbing around it. The corpus leans into the semantics
    /// that have bitten: null against absent, <c>NOT</c> over both, grouping by a key some documents
    /// lack, <c>LIKE</c>'s shapes, and the aggregate forms.
    /// </remarks>
    public class CosmosDifferentialTests : IClassFixture<CosmosDifferentialTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown. xUnit drives these through a fixture the
        /// class asks for rather than through static hooks the framework calls by attribute.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }


        static string Endpoint => CosmosEmulator.Endpoint!;
        static string Key => CosmosEmulator.Key!;
        static bool IsEmulator => CosmosEmulator.IsEmulator;

        static readonly string DatabaseName = "calcite_cosmos_diff_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        /// <summary>
        /// A second container, seeded so that the declared type of a cast column is a lie for some of
        /// its documents.
        /// </summary>
        /// <remarks>
        /// Separate from <c>products</c> deliberately. A path holding a number in one document and a
        /// string in the next is exactly what the rest of the corpus must not carry: an ordinary
        /// comparison over such a path asks a question neither side is obliged to answer the same way,
        /// and the divergence it produced would say nothing about casts. Keeping the hazard here means
        /// only the statements that name it read it.
        /// </remarks>
        static readonly CosmosContainerMetadata Typed = new("typed", new[] { "/category" });

        /// <summary>
        /// A third container, for what a sort asks of a column: one that declares a path a string, and
        /// holds arrays and objects at paths that declare nothing.
        /// </summary>
        /// <remarks>
        /// Declared, where the other two declare nothing, because a declaration is what a text sort key
        /// needs before the service may order it — and the other two must go on answering as a
        /// container that says nothing does.
        /// </remarks>
        static readonly CosmosContainerMetadata Ordering = new CosmosContainerMetadata("ordering", new[] { "/category" })
            .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse("""
                { "type": "object", "properties": { "t": { "type": ["string", "null"] }, "s": { "type": "string" }, "u": { "type": "string" } } }
                """)));

        /// <summary>
        /// The documents both plans read: prices present, null and absent; a document with a null
        /// category and one with none, which land in different groups than either expects; names for
        /// the LIKE shapes; a nested object and arrays.
        /// </summary>
        static readonly string[] Documents =
        [
            """{"id":"1","category":"bikes","name":"Trail Blazer","price":120,"tags":["outdoor","steel"]}""",
            """{"id":"2","category":"bikes","name":"Road Runner","price":340,"metadata":{"sku":"B-2"}}""",
            """{"id":"3","category":"shoes","name":"Sprint","price":80}""",
            """{"id":"4","category":"shoes","name":"Marathon","price":null}""",
            """{"id":"5","category":"shoes","name":"Slipper"}""",
            """{"id":"6","category":null,"name":"Uncategorized","price":5}""",
            """{"id":"7","name":"Unfiled","price":10}""",
        ];

        /// <summary>
        /// The documents a typed view lies about: a number of the declared type, a string where the
        /// declaration says integer, a fractional value where it says integer, the property absent, and
        /// the property present and null.
        /// <para>
        /// <c>label</c> holds a different JSON type in every document — a string, a number, a boolean,
        /// an array, an object, null, and nothing at all — because rendering a value as text is asked
        /// of whatever is there, and the claim that only a stored string can render as <c>'bikes'</c>
        /// is a claim about all seven.
        /// </para>
        /// <para>
        /// The partitions are <c>a</c>, <c>b</c>, and <c>30</c>. The last is a partition key that looks
        /// like a number while being a string, which is the case that separates a literal safe to route
        /// on from one that is not.
        /// </para>
        /// </summary>
        static readonly string[] TypedDocuments =
        [
            """{"id":"1","category":"a","name":"Exact","price":30,"label":"bikes"}""",
            """{"id":"2","category":"a","name":"Stringy","price":"30","label":30}""",
            """{"id":"3","category":"a","name":"Fractional","price":30.7,"label":true}""",
            """{"id":"4","category":"a","name":"Absent","label":["bikes"]}""",
            """{"id":"5","category":"a","name":"Null","price":null,"label":{"v":"bikes"}}""",
            """{"id":"6","category":"b","name":"Other","price":7,"label":null}""",
            """{"id":"7","category":"b","name":"NoLabel","price":8}""",
            """{"id":"8","category":"30","name":"NumericLookingKey","price":9,"label":"shoes"}""",
            """{"id":"9","category":"a","name":"Huge","price":9,"label":"huge","big":1e30}""",
        ];

        /// <summary>
        /// The documents of <see cref="Ordering"/>: <c>t</c> a string, null or absent, as declared;
        /// <c>f</c> arrays and objects, which the service orders other than as their text; and
        /// <c>n</c> integer arrays, which it orders other than as SQL compares them.
        /// <para>
        /// The last five are strings on either side of the line where code point order and UTF-16 order
        /// part, as ids and as the declared <c>s</c> and <c>u</c>: U+E000, U+FF21, U+FF20 and U+FFFD sort
        /// below U+1F600 by code point and above it by UTF-16 code unit, a supplementary character being
        /// encoded from <c>0xD800</c>. Written as escapes so that the source says which characters they
        /// are.
        /// </para>
        /// </summary>
        static readonly string[] OrderingDocuments =
        [
            """{"id":"1","category":"o","t":"Trail","f":[2],"n":[2]}""",
            """{"id":"2","category":"o","t":"apple","f":[10],"n":[10]}""",
            """{"id":"3","category":"o","t":"Zebra","f":["a"],"n":[1,5]}""",
            """{"id":"4","category":"o","t":"a-b","f":{"b":1},"n":[]}""",
            """{"id":"5","category":"o","t":null,"f":{"a":2},"n":[1]}""",
            """{"id":"6","category":"o","f":[1,2],"n":[1,2]}""",
            """{"id":"7","category":"o","t":"a_b","f":[]}""",
            """{"id":"8","category":"o","t":"Trail blazer","f":{}}""",
            """{"id":"a","category":"o","s":"a","u":"b"}""",
            """{"id":"\uE000","category":"o","s":"\uE000","u":"\uD83D\uDE00"}""",
            """{"id":"\uFF21","category":"o","s":"\uFF21","u":"\uFF20"}""",
            """{"id":"\uD83D\uDE00","category":"o","s":"\uD83D\uDE00","u":"a"}""",
            """{"id":"\uFFFD","category":"o","s":"\uFFFD","u":"\uFFFD"}""",
        ];

        static CosmosClient? _client;
        static Container? _container;
        static Container? _typedContainer;
        static Container? _orderingContainer;
        static string? _initializationFailure;

        static async Task ClassInitialize()
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(IsEmulator ? 5 : 30),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
            };

            if (IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsEmulator ? 10 : 120));
                var client = new CosmosClient(Endpoint, Key, options);

                var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                _container = await Seed(database, "products", Documents, cts.Token);
                _typedContainer = await Seed(database, "typed", TypedDocuments, cts.Token);
                _orderingContainer = await Seed(database, "ordering", OrderingDocuments, cts.Token);

                _client = client;
            }
            catch (Exception e)
            {
                _initializationFailure = e.ToString();
                _client?.Dispose();
                _client = null;
                _container = null;
                _typedContainer = null;
                _orderingContainer = null;
            }
        }

        /// <summary>
        /// Recreates a container and writes the given documents into it.
        /// </summary>
        static async Task<Container> Seed(Database database, string name, string[] documents, CancellationToken cancellationToken)
        {
            try { await database.GetContainer(name).DeleteContainerAsync(cancellationToken: cancellationToken); } catch (CosmosException) { }
            var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/category"), cancellationToken: cancellationToken)).Container;

            foreach (var json in documents)
            {
                using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                using var document = System.Text.Json.JsonDocument.Parse(json);

                var partitionKey = document.RootElement.TryGetProperty("category", out var category)
                    ? category.ValueKind == System.Text.Json.JsonValueKind.Null ? PartitionKey.Null : new PartitionKey(category.GetString())
                    : PartitionKey.None;

                using var response = await container.CreateItemStreamAsync(stream, partitionKey, cancellationToken: cancellationToken);
                response.EnsureSuccessStatusCode();
            }

            return container;
        }

        static void ClassCleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
            _container = null;
            _typedContainer = null;
            _orderingContainer = null;
        }

        sealed class TestDataContext : DataContext
        {

            readonly SchemaPlus _rootSchema;
            readonly JavaTypeFactory _typeFactory;

            public TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory)
            {
                _rootSchema = rootSchema;
                _typeFactory = typeFactory;
            }

            public SchemaPlus getRootSchema() => _rootSchema;

            public JavaTypeFactory getTypeFactory() => _typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => null!;

        }

        /// <summary>
        /// Plans and executes a statement, with the pushdown rules or with only the way out.
        /// </summary>
        static async Task<List<object>> Run(string sql, bool pushdown)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(Products, new CosmosQueryExecutor(_container!));
            var typed = new CosmosTable(Typed, new CosmosQueryExecutor(_typedContainer!));
            var ordering = new CosmosTable(Ordering, new CosmosQueryExecutor(_orderingContainer!));

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", table);
            rootSchema.add("typed", typed);
            rootSchema.add("ordering", ordering);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            // Chained with the library table so the corpus can name LEFT, RIGHT, REVERSE and REPEAT
            // — Calcite's standard table has none of them, and they are the functions whose two
            // dialects this suite exists to compare.
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
                    java.util.EnumSet.of(
                        org.apache.calcite.sql.fun.SqlLibrary.MYSQL,
                        org.apache.calcite.sql.fun.SqlLibrary.SPARK,
                        org.apache.calcite.sql.fun.SqlLibrary.HIVE,
                        org.apache.calcite.sql.fun.SqlLibrary.BIG_QUERY)));

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            // A convention per container, so both tables need their rules registered whichever a
            // statement happens to name.
            var pushdownRules = new List<string>();

            foreach (var convention in new[] { table.Convention, typed.Convention, ordering.Convention })
            {
                foreach (var rule in CosmosRules.GetRules(convention))
                {
                    planner.addRule(rule);

                    if (IsPushdown(rule))
                        pushdownRules.Add(java.util.regex.Pattern.quote(rule.ToString()));
                }
            }

            // The oracle: nothing is pushed, so Calcite reads the container whole and evaluates
            // everything in process.
            //
            // Excluded rather than never added, because never adding them does not keep them out. A
            // convention registers its own rules -- Convention.register, which a Volcano planner calls
            // the first time it sees a node carrying one -- so a scan arriving in the Cosmos convention
            // brings the whole pushdown set with it however the planner was built. Adding only the way
            // out therefore built no oracle at all: the two runs planned identically, and every
            // statement in the corpus agreed with itself.
            //
            // Removing them again does not work either, and the reason says why this is the lever: the
            // planner queues a rule's matches when the root is registered, so by the first moment the
            // rules provably exist the matches that fire them are already waiting. An exclusion filter
            // is read when a match fires rather than when it is queued, and is set before either.
            if (pushdown == false)
                planner.setRuleDescExclusionFilter(java.util.regex.Pattern.compile(string.Join("|", pushdownRules)));

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            var best = planner.findBestExp();

            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();
            foreach (var rule in ClrCursorRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(best);
            best = hep.findBestExp();

            var implementor = new ClrCursorRelImplementor(best.getCluster().getRexBuilder(), new java.util.HashMap());
            var factory = implementor.ImplementRoot((ClrCursorRel)best, ClrCursorPrefer.Array);

            var context = new TestDataContext(rootSchema.plus(), typeFactory);

            var rows = new List<object>();

            await using (var cursor = await factory.OpenAsync(context, CancellationToken.None))
                while (await cursor.ReadAsync(CancellationToken.None))
                    rows.Add(cursor.Current!);

            return rows;
        }

        /// <summary>
        /// Determines whether a rule is one the oracle must not have.
        /// </summary>
        /// <remarks>
        /// Everything that moves work to the service. Not the way out, which is what makes a pushed
        /// subtree readable at all and which the oracle needs for the scan; and not Calcite's own
        /// rewrites, which the convention registers because a bare Volcano planner has no logical rule
        /// set -- they preserve meaning, and one of them is what makes a grouping-set AVG planable by
        /// the CLR convention in the first place.
        /// </remarks>
        static bool IsPushdown(org.apache.calcite.plan.RelOptRule rule)
        {
            return rule is CosmosAggregateRule or CosmosAggregateSplitRule
                or CosmosFilterRule or CosmosFilterSplitRule
                or CosmosProjectRule or CosmosRankRule or CosmosSortRule
                or CosmosUnnestRule or CosmosLookupJoinRule;
        }

        /// <summary>
        /// Reduces a row to a canonical text, so that two boxes meaning the same value compare equal
        /// and a document's entry order means nothing.
        /// </summary>
        static string Canonical(object? value)
        {
            switch (value)
            {
                case null:
                    return "null";

                case object[] row:
                    return "(" + string.Join(", ", row.Select(Canonical)) + ")";

                case string s:
                    return "\"" + s + "\"";

                case bool b:
                    return b ? "true" : "false";
                case java.lang.Boolean jb:
                    return jb.booleanValue() ? "true" : "false";

                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                case java.lang.Number number:
                    return number.doubleValue().ToString("R", CultureInfo.InvariantCulture);

                case java.util.Map map:
                    {
                        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
                        var iterator = map.entrySet().iterator();
                        while (iterator.hasNext())
                        {
                            var entry = (java.util.Map.Entry)iterator.next();
                            entries[entry.getKey()?.ToString() ?? "null"] = Canonical(entry.getValue());
                        }

                        return "{" + string.Join(", ", entries.Select(e => e.Key + ": " + e.Value)) + "}";
                    }

                case java.util.List list:
                    {
                        var items = new List<string>();
                        for (var i = 0; i < list.size(); i++)
                            items.Add(Canonical(list.get(i)));

                        return "[" + string.Join(", ", items) + "]";
                    }

                default:
                    return value.GetType().Name + ":" + value;
            }
        }

        /// <summary>
        /// Runs one statement both ways and describes the difference, or returns <c>null</c> where
        /// there is none.
        /// </summary>
        /// <remarks>
        /// Which side failed is part of the answer, not an incidental detail of the message: a pushdown
        /// that throws where the oracle returns rows is a different defect from one that returns the
        /// wrong rows, and the two were indistinguishable while both were reported as "failed to run".
        /// </remarks>
        static async Task<string?> Compare(string sql, bool ordered)
        {
            List<string> pushed;
            List<string> oracle;

            try { oracle = (await Run(sql, pushdown: false)).Select(Canonical).ToList(); }
            catch (Exception e) when (e is not SkipException) { return $"{sql}\n  the oracle failed to run: {e.Message}"; }

            try { pushed = (await Run(sql, pushdown: true)).Select(Canonical).ToList(); }
            catch (Exception e) when (e is not SkipException) { return $"{sql}\n  the pushdown failed to run: {e.Message}\n  oracle: [{string.Join("; ", oracle)}]"; }

            if (ordered == false)
            {
                pushed.Sort(StringComparer.Ordinal);
                oracle.Sort(StringComparer.Ordinal);
            }

            if (pushed.SequenceEqual(oracle))
                return null;

            return $"{sql}\n  pushed: [{string.Join("; ", pushed)}]\n  oracle: [{string.Join("; ", oracle)}]";
        }

        /// <summary>
        /// The corpus. Ordered statements compare as sequences; the rest as multisets.
        /// </summary>
        /// <remarks>
        /// Excluded by name, with the recorded reason: out-of-domain arithmetic (<c>SQRT(-1)</c> and
        /// kin), which is pushed deliberately and diverges deliberately — the service fails the
        /// query where Calcite yields NaN; see <c>DESIGN.md</c>.
        /// </remarks>
        static readonly (string Sql, bool Ordered)[] Corpus =
        [
            // MEASURING: the operators over the bare text accessor, where Calcite compares the
            // rendering and the service the raw value. Over the typed container, whose label holds
            // one document per JSON type.
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') <> 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') <> '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') > 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') < 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') >= '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') LIKE 'bi%'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') LIKE '3%'", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') FROM typed AS c ORDER BY 2, c.\"id\"", true),
            ("SELECT c.\"id\" FROM typed AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.label'), c.\"id\"", true),

            // The same accessor as a sort's only key, with the placement the service's own order gives,
            // so that nothing but the reading decides whether it pushes. The sort bound the path and
            // never asked how the column was read, so the service ordered the stored values -- by JSON
            // type, numbers by value -- where SQL orders their text: the label came back [null, null,
            // 'true', '30', 'bikes', 'huge', 'shoes', null, null] against [null x4, '30', 'bikes',
            // 'huge', 'shoes', 'true'], and the price '7', '8', '9', '9', '30', '30.7', '30' where text
            // puts '30' first. Both directions, the accessor named in the ORDER BY rather than by
            // ordinal, and a view's column bound to it. The document column is the same question asked
            // of a reading as JSON: the service orders the objects, and SQL their text.
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label') FROM typed AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label') FROM typed AS c ORDER BY 1 DESC NULLS LAST", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price') FROM typed AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price') FROM typed AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.price') DESC NULLS LAST", true),
            ("SELECT p.\"Label\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p ORDER BY p.\"Label\" NULLS FIRST", true),
            ("SELECT c.\"id\" FROM typed AS c ORDER BY c.\"DOC\"", true),

            // And where it still pushes: a path the container declares a string or null, whose text is
            // the stored value, in both directions and with a page. Beside it the fragments and the
            // arrays, which never do -- the service put [] after [1, 5] and {"b":1} before {"a":2},
            // neither of which is their text's order or SQL's.
            ("SELECT JSON_VALUE(c.\"DOC\", '$.t') FROM ordering AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.t') FROM ordering AS c ORDER BY 1 DESC NULLS LAST FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT JSON_QUERY(c.\"DOC\", '$.f') FROM ordering AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT JSON_QUERY(c.\"DOC\", '$.n' RETURNING INTEGER ARRAY) FROM ordering AS c ORDER BY 1 NULLS FIRST", true),

            // The same operators over a view's column, which binds to the accessor's path and reads
            // as text -- the spelling that had pushed the raw comparison (#83). Each is the accessor
            // statement above written through a subquery, and must agree with it row for row.
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" <> 'bikes'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" > 'bikes'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" >= '30'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" LIKE 'bi%'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" LIKE '3%'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" = '30'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" = 'bikes'", false),

            // The same rendering as a lookup join's key, where the batch's keys arrive as parameters
            // and the restriction is the raw `c.label IN (@k…)` (soundness probes P23b and P24b). It
            // lost the stored 30 to the key '30' and the stored true to 'true', so a text key over a
            // path nothing declares a string is joined in process instead. The keys are of equal
            // width on purpose: VALUES types a list of literals CHAR of the longest, and the padded
            // '30   ' matches nothing on either side, which is what hid this the first time. The
            // join on id is the control, a key read as stored, which still looks up.
            ("SELECT t.\"id\" FROM (VALUES ('30'), ('31')) AS v(k) JOIN typed AS t ON v.k = JSON_VALUE(t.\"DOC\", '$.label')", false),
            ("SELECT t.\"id\" FROM (VALUES ('true'), ('huge')) AS v(k) JOIN typed AS t ON v.k = JSON_VALUE(t.\"DOC\", '$.label')", false),
            ("SELECT p.\"id\" FROM (VALUES ('30'), ('31')) AS v(k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p ON v.k = p.\"Label\"", false),
            ("SELECT t.\"id\" FROM (VALUES ('1'), ('3')) AS v(k) JOIN typed AS t ON v.k = t.\"id\"", false),

            // A lookup whose container side traverses an array (soundness review, cause K). The rule
            // decided on the logical correlate and the join asked again of the converted traversal,
            // which the binding walk had no case for, so the plan was chosen and failed to implement.
            // The spelling is the one the oracle can evaluate: JSON_QUERY with an array RETURNING
            // agrees pushed and in process, where StringToArray has no Calcite implementation. The
            // container side projects document columns only, since the element binds nothing where a
            // rule decides; and the bike with two tags is two rows, the one with none is none.
            ("SELECT v.k, p.\"name\" FROM (VALUES ('1'), ('2')) AS v(k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"name\" FROM products AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t) AS p ON v.k = p.\"id\"", false),

            // A lookup on a number or a boolean (review probes I4, I12 and I13). The keys reached the
            // batch in Java's boxes, which the SDK wrote as objects, and the service matched nothing:
            // each returned no rows where the in-process join returned them. Integer keys against an
            // integer column, a DOUBLE and a BIGINT against it, a _ts read by one statement and looked
            // up by another, a boolean through a filtering view, and a _ts through a traversal (K9).
            ("SELECT t.\"id\" FROM (VALUES (120), (80)) AS v(k) JOIN products AS t ON v.k = JSON_VALUE(t.\"DOC\", '$.price' RETURNING INTEGER)", false),
            ("SELECT t.\"id\" FROM (VALUES (CAST(120 AS DOUBLE)), (CAST(80.5 AS DOUBLE))) AS v(k) JOIN products AS t ON v.k = JSON_VALUE(t.\"DOC\", '$.price' RETURNING INTEGER)", false),
            ("SELECT t.\"id\" FROM (VALUES (CAST(120 AS BIGINT)), (CAST(80 AS BIGINT))) AS v(k) JOIN products AS t ON v.k = JSON_VALUE(t.\"DOC\", '$.price' RETURNING INTEGER)", false),
            ("SELECT p.\"id\" FROM (SELECT c2.\"_ts\" AS \"ts\" FROM products AS c2 WHERE c2.\"id\" = '1') AS v JOIN products AS p ON v.\"ts\" = p.\"_ts\"", false),
            ("SELECT p.\"id\" FROM (VALUES (TRUE)) AS v(k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label' RETURNING BOOLEAN) AS \"b\" FROM typed AS c WHERE c.\"id\" = '3') AS p ON v.k = p.\"b\"", false),
            ("SELECT p.\"id\" FROM (SELECT c2.\"_ts\" AS \"ts\" FROM products AS c2 WHERE c2.\"id\" = '1') AS v JOIN (SELECT c.\"id\", c.\"_ts\" AS \"ts\" FROM products AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t) AS p ON v.\"ts\" = p.\"ts\"", false),

            // A case fold under LIKE, rendered as the service's case-insensitive CONTAINS, STARTSWITH
            // and ENDSWITH (#84). Over the accessor it takes the guard LIKE takes; over a promoted
            // column, which is the path and not a rendering, it renders directly. The mixed-type
            // label is what checks the guard, and the mixed-case literal is what checks the flag.
            ("SELECT c.\"id\" FROM typed AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.label')) LIKE '%IKE%'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE LOWER(JSON_VALUE(c.\"DOC\", '$.label')) LIKE 'bik%'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.label')) LIKE '%KES'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.label')) LIKE '%3%'", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE UPPER(p.\"Label\") LIKE '%IKE%'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE UPPER(c.\"$.category\") LIKE '%A%'", false),

            // Projections.
            ("SELECT * FROM products", false),
            ("SELECT c.\"id\", c.\"$.category\" FROM products AS c", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.metadata.sku') FROM products AS c", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price') FROM products AS c", false),

            // Filters: comparisons, null against absent, NOT over both, disjunction, LIKE's shapes.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') IS NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') IS NOT NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" IS NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes' OR c.\"$.category\" = 'shoes'", false),

            // Negation over a path that is null in one document and absent in another. The service's
            // equality over a null is false where SQL's is unknown, and only the negation tells them
            // apart -- in a positive position false and unknown both discard the row.
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" <> 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" <> 'bikes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (UPPER(CAST(c.\"$.category\" AS VARCHAR)) = 'BIKES')", false),

            // The shapes the guard is deliberately not applied to, because applying it would be too
            // strong. Here to say whether leaving them alone is right.
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes' AND CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes' OR CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (NOT (c.\"$.category\" = 'bikes'))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" IN ('bikes', 'shoes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') BETWEEN 10 AND 200", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"id\" = '3' AND c.\"$.category\" = 'shoes'", false),

            // Sorts and row restrictions, ordered by the unique id so the comparison is meaningful.
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\"", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY", true),

            // Aggregates: the forms, and grouping by a key some documents lack.
            ("SELECT COUNT(*) FROM products", false),
            ("SELECT MIN(c.\"_ts\"), MAX(c.\"_ts\") FROM products AS c", false),

            // Over _ts rather than a user path, and that is a limit rather than a choice. MIN, MAX,
            // SUM and AVG over an ANY column have no implementation in the CLR convention,
            // and the aggregate rule declines to push one -- so a statement naming any of them forms
            // no plan in either mode and cannot be compared or even run. Measured, on all four.
            // Nothing about pushdown; there is simply nothing to execute. Written down because the
            // absence of these from a corpus about null and absent values otherwise reads as an
            // oversight, and because the day the convention grows them is the day they belong here.
            ("SELECT SUM(c.\"_ts\") FROM products AS c", false),
            ("SELECT COUNT(DISTINCT c.\"$.category\") FROM products AS c", false),
            ("SELECT c.\"$.category\", COUNT(*) FROM products AS c GROUP BY c.\"$.category\"", false),
            ("SELECT c.\"$.category\", COUNT(*) FROM products AS c GROUP BY ROLLUP(c.\"$.category\")", false),
            ("SELECT c.\"$.category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"$.category\" HAVING c.\"$.category\" = 'bikes'", false),
            ("SELECT AVG(c.\"_ts\") FROM products AS c", false),

            // LIKE's shapes: the prefix that becomes STARTSWITH, and the general form that stays LIKE.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') LIKE 'S%'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') LIKE '%Runner%'", false),

            // Array traversal.

            // The string functions mapped from a SQL counterpart, which is exactly where the two
            // could disagree — the oracle evaluates SQL's, the pushdown Cosmos's.
            ("SELECT LEFT(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR), 3) FROM products AS c", false),
            ("SELECT RIGHT(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR), 3) FROM products AS c", false),
            ("SELECT REVERSE(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR)) FROM products AS c", false),
            ("SELECT REPEAT(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR), 2) FROM products AS c", false),

            // And the boundary that most often differs between dialects: a count longer than the
            // string, which both are documented to clamp rather than fail.
            ("SELECT LEFT(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR), 99) FROM products AS c", false),
            ("SELECT RIGHT(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR), 99) FROM products AS c", false),

            // The array functions, and the index shift above all: the oracle counts from one and
            // the pushdown from zero, so an off-by-one in the translation shows here as different
            // elements rather than as an error.

            // SUBSTRING carries the same adjustment ARRAY_SLICE carried wrongly, and SQL's origin
            // really is one here. Covered so that the two are not assumed to be the same question.
            ("SELECT SUBSTRING(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) FROM 1 FOR 3) FROM products AS c", false),
            ("SELECT SUBSTRING(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) FROM 2 FOR 3) FROM products AS c", false),


            // Not here, and the absences are facts rather than oversights: the library's
            // ARRAY_SLICE takes exactly three arguments, so Cosmos's two-argument form has no SQL
            // spelling to compare against; and ARRAY_CONCAT's operand checker refuses a map value,
            // which is typed ANY, so the statement does not validate over this row model at all.

            // DISTINCT, which the adapter now emits as the keyword rather than as a GROUP BY over
            // every key. The seeded documents include a null category and an absent one, so this
            // asks the question that matters: whether the service's dedup agrees with SQL's about
            // null and undefined.
            ("SELECT DISTINCT c.\"$.category\" FROM products AS c", false),
            ("SELECT DISTINCT c.\"$.category\", c.\"id\" FROM products AS c", false),
            ("SELECT DISTINCT c.\"_ts\" FROM products AS c ORDER BY c.\"_ts\"", true),

            // DISTINCT under a batch of point reads, which return a row per document where DISTINCT
            // wants one per value (soundness probe P22c). Over _ts because the service writes it on
            // every document, so the distinct binds the path and nothing else withdrew the read.
            // The two bikes share a _ts whenever they were seeded within the same second, which
            // seven documents almost always are; where they were not, the two sides agree on two
            // rows and the statement says nothing, rather than failing. The planner tests pin the
            // withdrawal itself.
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$._ts' RETURNING INTEGER) FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", false),
            ("SELECT DISTINCT c.\"_ts\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", false),
            ("SELECT DISTINCT c.\"$.category\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", false),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.category') FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", false),
            ("SELECT DISTINCT c.\"$.category\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", false),

            // A WHERE the pinning filter did not write (review probes H5 and H6). A view's filter
            // and one written outside it reach the service as two filters and conjoin into one
            // WHERE, and the read was offered on the outer one's say alone: the soft-delete view
            // returned the document it excludes, and the batch both bikes where one is over 200.
            // Both orders, both reads.
            ("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"$.category\" AS \"cat\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) IS NULL) AS t WHERE t.\"cat\" = 'shoes' AND t.\"id\" = '3'", false),
            ("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"$.category\" AS \"cat\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 200) AS t WHERE t.\"cat\" = 'bikes' AND t.\"id\" = '1'", false),
            ("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"$.category\" AS \"cat\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 200) AS t WHERE t.\"cat\" = 'bikes' AND t.\"id\" IN ('1', '2')", false),
            ("SELECT t.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')) AS t WHERE t.\"p\" > 200", false),
            ("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"_ts\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')) AS t WHERE t.\"id\" <> '1'", false),

            // What a read selects (review probe H4). The read took every column as its declared type,
            // and the document column, an accessor read as text and a JSON_QUERY are VARCHAR over an
            // object, a number or a boolean: each failed to run where the query answered. Single and
            // batch, the document whole and beside other columns, and each JSON type an accessor
            // reads as text, over the typed container whose label holds one of each.
            ("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", false),
            ("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", false),
            ("SELECT c.\"id\", c.\"DOC\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price') FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", false),
            ("SELECT JSON_QUERY(c.\"DOC\", '$.metadata') FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '2'", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') FROM typed AS c WHERE c.\"$.category\" = 'a' AND c.\"id\" IN ('1', '2', '3', '4', '5')", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label') FROM typed AS c WHERE c.\"$.category\" = 'a' AND c.\"id\" = '5'", false),

            // A sort over a DISTINCT of a path some documents lack (soundness probe P16). The distinct
            // groups the key as IS_DEFINED(p) ? p : null and binds nothing above it; the rule used to
            // read the path regardless, convert the sort, and leave implementation to refuse it. The
            // placements are the ones the service's own order would honour, so nothing but the binding
            // declines them. The filter is the binding's other reader, which the planner moves beneath
            // the distinct; it agreed before and is here to keep agreeing.
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c ORDER BY 1 DESC NULLS LAST", true),
            ("SELECT d.\"p\" FROM (SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c) AS d WHERE d.\"p\" > 10", false),

            // A DISTINCT or a grouping over the text accessor, over the typed container, whose price
            // is 30 in one document and "30" in another (review probes J1, J2, J13 and J14). The
            // service grouped the stored values, keeping the two apart, where SQL groups the rendering
            // '30': the distinct returned it twice, the grouping two groups of one, COUNT(DISTINCT)
            // one too many, and the distinct under a pinned id pair, once a batch of reads, twice.
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price') FROM typed AS c", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price'), COUNT(*) FROM typed AS c GROUP BY JSON_VALUE(c.\"DOC\", '$.price')", false),
            ("SELECT COUNT(DISTINCT JSON_VALUE(c.\"DOC\", '$.price')) FROM typed AS c", false),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price') FROM typed AS c WHERE c.\"$.category\" = 'a' AND c.\"id\" IN ('1', '2')", false),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.label') FROM typed AS c", false),


            // ── A sweep over the surfaces that carried no statement at all ─────────
            //
            // Every one of these reads a path that is null in one document and absent in another,
            // because that is where this adapter has been wrong every time so far.

            // Comparison and range, in both positions.
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) < 100", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) >= 120", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.price') BETWEEN 10 AND 200)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') NOT IN (120, 340)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" NOT IN ('bikes', 'shoes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" IN ('bikes', 'shoes'))", false),

            // LIKE and its negation over a path some documents lack.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') NOT LIKE 'S%'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.name') LIKE '%Runner%')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') LIKE '_prin_'", false),

            // Arithmetic over a null and an absent operand, which SQL makes unknown.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') + 1 > 100", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.price') + 1 > 100)", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price') + 1 FROM products AS c", false),

            // Conjunction and disjunction mixing a known-true arm with an unknown one.
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50 OR c.\"$.category\" = 'shoes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50 AND c.\"$.category\" = 'shoes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.price') IS NULL)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.price') IS NOT NULL)", false),

            // Aggregates over a column that is null in one document, absent in another, and a
            // string in none -- what SUM and AVG skip is not obviously the same on both sides.
            ("SELECT COUNT(JSON_VALUE(c.\"DOC\", '$.price')) FROM products AS c", false),
            ("SELECT COUNT(c.\"$.category\") FROM products AS c", false),
            ("SELECT c.\"$.category\", COUNT(*) FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') IS NOT NULL GROUP BY c.\"$.category\"", false),
            ("SELECT COUNT(DISTINCT JSON_VALUE(c.\"DOC\", '$.name')) FROM products AS c", false),

            // Ordering, where the placement of a null and an absent value is the whole question.
            ("SELECT c.\"id\", c.\"$.category\" FROM products AS c ORDER BY c.\"id\" DESC", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" OFFSET 6 ROWS", true),

            // The string functions over a path some documents lack, which is where a service
            // function returning undefined and SQL returning null could part company.
            ("SELECT c.\"id\", UPPER(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR)) FROM products AS c", false),
            ("SELECT c.\"id\", CHAR_LENGTH(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR)) FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CHAR_LENGTH(CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR)) > 6", false),

            // Nested and array-valued paths, read where they are absent.
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.metadata.sku') FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata.sku') = 'B-2'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.metadata.sku') = 'B-2')", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.tags[0]') FROM products AS c", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.tags[1]') FROM products AS c", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.tags[2]') FROM products AS c", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.tags[3]') FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.tags[1]') = 'outdoor'", false),

            // CASE, whose arms are where an unknown condition goes somewhere visible.
            ("SELECT c.\"id\", CASE WHEN c.\"$.category\" = 'bikes' THEN 1 ELSE 0 END FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (CASE WHEN c.\"$.category\" = 'bikes' THEN 1 ELSE 0 END) = 0", false),


            // ── A second sweep: ordering, aggregation and row restriction over a path
            //    that is null in one document and absent in another ─────────────────

            // Ordering by a nullable user path. Ordering by the promoted $.category column is gone from
            // the corpus: it is a VARIANT column, which the sort declines to push, and which the oracle
            // cannot sort in process either — VariantValue is not Comparable and names no order across
            // types. So there is no ordering behaviour to compare, and none is claimed; a caller orders
            // by JSON_VALUE instead. See #165, and #163 for the type. Ordering by a rendered JSON_VALUE
            // is text, which Calcite sorts in process and the service only over a declared string; this
            // one has a second key and no composite index, so it stays in process either way.
            ("SELECT c.\"id\" FROM products AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.price'), c.\"id\"", true),

            // A view's shape: the projection casts, so it cannot be pushed, and the ordering and row
            // limit go under it rather than staying above. The cast runs over the rows that come back,
            // which is what these compare.
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c ORDER BY c.\"id\"", true),
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c WHERE c.\"$.category\" = 'shoes' ORDER BY c.\"id\" FETCH NEXT 2 ROWS ONLY", true),

            // Row restriction combined with a predicate over a path some documents lack, where a
            // wrongly pushed TOP takes the wrong rows rather than the wrong number of them.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') IS NOT NULL ORDER BY c.\"id\" FETCH NEXT 2 ROWS ONLY", true),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes') ORDER BY c.\"id\" OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY", true),

            // The aggregate forms over a path that is null in one document and absent in another.
            // What SUM and AVG skip, and what COUNT counts, is the whole question.
            ("SELECT COUNT(*), COUNT(JSON_VALUE(c.\"DOC\", '$.price')) FROM products AS c", false),
            ("SELECT c.\"$.category\", COUNT(*) FROM products AS c GROUP BY c.\"$.category\" HAVING COUNT(*) > 1", false),

            // Grouping by something other than the partition key, and by more than one thing.
            ("SELECT JSON_VALUE(c.\"DOC\", '$.name'), COUNT(*) FROM products AS c GROUP BY JSON_VALUE(c.\"DOC\", '$.name')", false),
            ("SELECT c.\"$.category\", JSON_VALUE(c.\"DOC\", '$.name'), COUNT(*) FROM products AS c GROUP BY c.\"$.category\", JSON_VALUE(c.\"DOC\", '$.name')", false),

            // DISTINCT over a computed column and over more than one, where the normalised key and
            // the plain one sit side by side.
            ("SELECT DISTINCT c.\"$.category\", JSON_VALUE(c.\"DOC\", '$.name') FROM products AS c", false),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price') FROM products AS c", false),

            // The whole document, and a document with no user properties beyond the seeded ones.
            ("SELECT c.\"DOC\" FROM products AS c", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.metadata') FROM products AS c", false),

            // Casts over document values, which is how a view gives a column a SQL type over this row
            // model. Read against the typed container, whose documents disagree with the declaration on
            // purpose: a string where it says integer, a fraction where it says integer, the property
            // absent, and the property null.
            //
            // These agree because nothing here is pushed. A cast is opaque to translation, so every
            // operator reading one declines and Calcite answers it over the whole container — slowly,
            // and with the rows SQL says. That is the property under test: it is what makes looking
            // through a cast a change that has to prove itself here first. Measured, an erasing
            // translation fails these — Calcite converts "30" and 30.7 to 30 and matches both, and the
            // service compares the stored value as it stands and matches neither.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) > 10", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) > 0 ORDER BY c.\"id\"", true),
            // Saturation. A stored value far past what the target can hold converts to the limit, so a
            // comparison against the limit is true of it — and a bound around the limit would exclude
            // exactly that document. Measured as a lost row before the bound stopped stating that side.

            // The spellings differ in what they do with a value that will not convert -- CAST raises,
            // SAFE_CAST yields null -- and the bound must not change which happens, because it never
            // excludes a value that is not a number.
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) > 10", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) IS NULL", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS DOUBLE) = 30.7", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) = 'Stringy'", false),
            ("SELECT c.\"id\", CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) FROM typed AS c", false),
            ("SELECT CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'a'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"$.category\" AS VARCHAR) = 'b'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DECIMAL(10, 2)) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"id\" AS INTEGER) = 3", false),

            // Equality against text, which is the one cast shape that is dropped — and dropped because
            // the two forms select the same documents, not because the difference is tolerable. Asked
            // of a field holding a string, a number, a boolean, an array, an object, null and nothing.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'shoes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) <> 'bikes'", false),

            // The same equality with the accessor said in SQL/JSON, which is the cast a DOC view
            // writes (#71). Over the same seven values, because the claim is the same: JSON_VALUE
            // without RETURNING renders what the cast over ANY renders and answers null for the rest.
            // The refused literal, the partition key through the cast, and the projection above the
            // dropped comparison -- which stays in process over this spelling -- come with it.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'shoes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.category') AS VARCHAR) = 'b'", false),
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'", false),

            // The bare accessor, which is that cast with nothing written: Calcite renders the number 30
            // as '30' and keeps the document, the service does not. Held to the same literal test, so
            // the first pushes and the other two are declined and decided in process, over the
            // disjunction the split rule pushes -- the string or the number, the string or the
            // boolean. The bracketed literal is exact over the accessor, an array being null to it.
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = 'true'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '[bikes]'", false),

            // The same refused literals through the accessor, where no array branch is wanted: the
            // function answers null for an array, so no stored array matches however the literal
            // looks and the string comparison is exact.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = '[bikes]'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'TRUE'", false),

            // Projecting a cast to text, which the statement sends as the value and the reader renders.
            // The claim is that Calcite's cast over an ANY value is Java's rendering of the box the
            // reader already builds, so these are asked of a field seeded to hold a string, a number, a
            // boolean, an array, an object, null and nothing — and of one holding 1e30, whose rendering
            // is the one a JSON writer would not have produced. A rendering that drifts from Calcite's
            // fails here, which is the only place it would be caught.
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c", false),
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c", false),
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.big') AS VARCHAR) FROM typed AS c", false),
            ("SELECT SAFE_CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c", false),
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c WHERE c.\"$.category\" = 'a'", false),
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'", false),
            ("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM typed AS c ORDER BY c.\"id\" FETCH NEXT 3 ROWS ONLY", true),

            // A width is a second conversion the reader does not perform, so these keep the cast in
            // process — and the rows are what says so: VARCHAR(3) truncates and CHAR(8) pads.
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR(3)) FROM typed AS c", false),
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.label') AS CHAR(8)) FROM typed AS c", false),

            // Ordering by a rendered column is not ordering by the path underneath — as text 10 sorts
            // before 9, and the service puts a boolean and a null before either — so the column
            // addresses nothing and the sort stays above. Compared in order, so a sort that quietly
            // reached the service is a failure here rather than a coincidence: over these documents
            // the two orders genuinely differ.
            //
            // One key and one column deliberately. A second key would need a composite index and be
            // refused for that instead, saying nothing; and a row that is only the rendering makes a
            // tie between equal values invisible, so the comparison does not depend on which of two
            // identical rows came first.
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) FROM typed AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.price') AS VARCHAR) FROM typed AS c ORDER BY 1 NULLS FIRST", true),

            // The literals that are refused, each because some other JSON value renders as them. If any
            // of these starts being dropped, these are the statements that say so.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'true'", false),

            // The partition key reached through a view, which is the routing this recovers. The rows
            // must not change; that they are fetched from one partition is measured separately.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"$.category\" AS VARCHAR) = 'b'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"$.category\" AS VARCHAR) = '30'", false),
            ("SELECT CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER), COUNT(*) FROM typed AS c GROUP BY CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER)", false),
        ];

        /// <summary>
        /// The statements the pushdown answers differently, each with what makes it differ.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Empty for a long time, and that was the point of keeping it.</b> Repairing the oracle put
        /// five statements here — negation over a null-valued property, grouping and dedup over one, and
        /// an <c>ARRAY_SLICE</c> origin adjustment that was never needed — and each was fixed and
        /// promoted into the corpus. What it holds now is one open defect, the order of strings beyond
        /// the BMP, witnessed four ways.
        /// </para>
        /// <para>
        /// A statement belongs here only while it is an open defect with a witness, never as a
        /// decision. Asserting that each still differs is what keeps that true: one that starts
        /// agreeing fails <see cref="EveryRecordedDivergenceStillDiverges"/> and is meant to move up
        /// into the corpus rather than sit here looking settled.
        /// </para>
        /// </remarks>
        static readonly (string Sql, bool Ordered, string Reason)[] Divergences =
        [
            ("SELECT c.\"id\" FROM ordering AS c ORDER BY c.\"id\"", true,
                "The service orders strings by code point and Calcite by UTF-16 code unit, measured on the vnext-preview emulator: U+E000, U+FF21 and U+FFFD sort below U+1F600 at the service and above it in process, a supplementary character being encoded from 0xD800; below the surrogates the two orders are one. Whatever order the service computes is its own -- a sort, a range comparison, a MAX -- over id as over a declared string. Open: TODO.md section 4, DESIGN.md under Ordering a sort key the way it reads."),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.s') FROM ordering AS c ORDER BY 1 NULLS FIRST", true,
                "The same order over a path declared a string, which is the one text sort key the service is given. As the id above."),
            ("SELECT c.\"id\" FROM ordering AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') < JSON_VALUE(c.\"DOC\", '$.u')", false,
                "The same order in a comparison, which a declared string pushes exactly: U+E000 < U+1F600 is true at the service and false in process. As the id above."),
            ("SELECT MAX(c.\"id\") FROM ordering AS c", false,
                "The same order in an aggregate over a column that cannot be null: the service's MAX is U+1F600 and Calcite's U+FFFD. As the id above."),
        ];

        /// <summary>
        /// Statements with no oracle to compare against, and why there is none.
        /// </summary>
        static readonly (string Sql, string Reason)[] WithoutAnOracle =
        [
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) <> 30",
                "Calcite throws rather than answering: RETURNING asserts the type instead of converting to it, and the ON ERROR clause that SQL:2016 says governs the mismatch is not consulted -- ikvmnet/calcite-dotnet#120. The typed container stores a string at this path, so the oracle cannot run. The pushdown restricts to the type the RETURNING names, which excludes exactly the documents that would have thrown and which the standard says answer null and are therefore excluded anyway. A deliberate divergence: the pushed statement succeeds where the engine fails."),

            // Soundness probes P22 and P22b, the DISTINCT under a batch of point reads with a wider
            // RETURNING. Not WithStatedRows: the rows are the service's own timestamps, which are
            // not known until the documents are written, and how many there are depends on whether
            // the two were written in the same second. P22c in the corpus is the same statement with
            // an oracle.
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$._ts' RETURNING BIGINT) FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')",
                "Calcite throws rather than answering: RETURNING BIGINT asserts java.lang.Long, and a stored _ts that fits an int is read as java.lang.Integer -- InvalidCastException, the RETURNING assertion of ikvmnet/calcite-dotnet#120 over a value of the right kind and a narrower width. The pushdown reads the number as the BIGINT the plan declares."),
            ("SELECT JSON_VALUE(c.\"DOC\", '$._ts' RETURNING BIGINT) FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')",
                "As the DISTINCT above, which is this statement with the de-duplication taken away: the oracle throws the same InvalidCastException. Here the batch of point reads is the right access path, a row per document being what the statement asks for."),

        ];

        /// <summary>
        /// Statements with no oracle, whose rows are written out here instead.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="WithoutAnOracle"/> gives the weaker guarantee that a statement answers at all,
        /// and for an array traversal that is not enough. Its defect was a row arriving with the
        /// element missing rather than a statement that failed: a Cosmos object constructor omits a
        /// property whose value is undefined, and an omitted property reads back as null without
        /// complaint. Only stating the rows catches that.
        /// </para>
        /// <para>
        /// The traversal is written through a sub-select on purpose. A bare planner reaches the array
        /// straight off the scan; a host running Calcite's own rule set hoists it into a projection
        /// below the correlate, and the traversal is then above a projection — which is the shape that
        /// failed, and the shape every host produces. See ikvmnet/calcite-cosmos#36.
        /// </para>
        /// </remarks>
        static readonly (string Sql, string[] Rows)[] WithStatedRows =
        [

            // The same traversal with a predicate over the element, which the service applies after
            // the JOIN. Stated rather than compared for the same reason, and it is the row the whole
            // predicate pushdown is about: one of the two elements, not both and not none.
        ];

        [Fact]
        public async Task EveryStatementAgreesWithTheOracle()
        {
            // Gated here, outside any catch: raising the gate inside Run turned "no emulator" into
            // twenty-seven failures on every platform without one, which is how CI first said so.
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, ordered) in Corpus)
                if (await Compare(sql, ordered) is string failure)
                    failures.Add(failure);

            failures.Should().BeEmpty("every pushdown must answer as Calcite would:\n" + string.Join("\n", failures));
        }

        [Fact]
        public async Task EveryRecordedDivergenceStillDiverges()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var agreed = new List<string>();

            foreach (var (sql, ordered, reason) in Divergences)
                if (await Compare(sql, ordered) is null)
                    agreed.Add($"{sql}\n  recorded as: {reason}");

            // Agreement is the good outcome and still a failure here, because the record is now wrong.
            // Move the statement into the corpus and delete its entry.
            agreed.Should().BeEmpty("a recorded divergence that has closed belongs in the corpus:\n" + string.Join("\n", agreed));
        }

        /// <summary>
        /// Runs what cannot be compared, so that it is at least known to answer.
        /// </summary>
        /// <remarks>
        /// Weaker than the corpus by a long way, and the strongest thing available while the oracle
        /// cannot be built for these — see <see cref="WithoutAnOracle"/>. It still catches a pushdown
        /// that stops running at all.
        /// </remarks>
        [Fact]
        public async Task EveryStatementWithoutAnOracleStillRuns()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, reason) in WithoutAnOracle)
            {
                try
                {
                    await Run(sql, pushdown: true);
                }
                catch (Exception e) when (e is not SkipException)
                {
                    failures.Add($"{sql}\n  no oracle because: {reason}\n  and the pushdown failed to run: {e.Message}");
                }
            }

            failures.Should().BeEmpty("a statement with no oracle must at least answer:\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// Runs what cannot be compared but whose answer is known, and checks it against that answer.
        /// </summary>
        [Fact]
        public async Task EveryStatementWithStatedRowsReturnsThem()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, rows) in WithStatedRows)
            {
                List<string> returned;

                try { returned = (await Run(sql, pushdown: true)).Select(Canonical).ToList(); }
                catch (Exception e) when (e is not SkipException) { failures.Add($"{sql}\n  failed to run: {e.Message}"); continue; }

                returned.Sort(StringComparer.Ordinal);
                var stated = rows.OrderBy(r => r, StringComparer.Ordinal).ToList();

                if (returned.SequenceEqual(stated) == false)
                    failures.Add($"{sql}\n  returned: [{string.Join("; ", returned)}]\n  stated:   [{string.Join("; ", stated)}]");
            }

            failures.Should().BeEmpty("a statement whose rows are stated must return them:\n" + string.Join("\n", failures));
        }

    }

}
