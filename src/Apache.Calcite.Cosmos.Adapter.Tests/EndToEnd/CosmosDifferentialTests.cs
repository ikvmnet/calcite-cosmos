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
    public partial class CosmosDifferentialTests : IClassFixture<CosmosDifferentialTests.Fixture>
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
        /// A third container, for what a sort asks of a column: one that declares a path a string, an
        /// integer and a boolean, and holds arrays and objects at paths that declare nothing.
        /// </summary>
        /// <remarks>
        /// Declared, where <c>products</c> and <c>typed</c> declare nothing, because a declaration is
        /// what a text sort key and a typed one need before the service may order them — and those two
        /// must go on answering as a container that says nothing does.
        /// </remarks>
        static readonly CosmosContainerMetadata Ordering = new CosmosContainerMetadata("ordering", new[] { "/category" })
            .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(Planning.CosmosPushdownProbeTests.OrderingSchema)));

        /// <summary>
        /// A fourth container, declaring what a filter concludes from: <c>body</c> is a string and <c>n</c> an
        /// integer, and every document keeps to that.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>products</c> and <c>typed</c> declare nothing, and <c>ordering</c> declares what a sort asks, so nothing
        /// else in the corpus asked what a declaration lets a filter conclude, and a conclusion the clause does not
        /// license is a wrong answer only a declared container shows: over this one, <c>JSON_VALUE(… RETURNING
        /// DECIMAL) = 2</c> was rewritten to <c>FALSE</c>, the equality read as the path holding the number 2 and the
        /// declaration saying it holds a string.
        /// </para>
        /// <para>
        /// The documents keep the declaration on purpose, unlike <c>typed</c>'s: a fact is the caller's word, and a
        /// container that broke it would test the word rather than the reasoning. So every <c>body</c> is a string, and
        /// most spell a number in a different way — <c>"2"</c>, <c>" 2 "</c>, <c>"2.0"</c> — which is what a parsing
        /// <c>RETURNING</c> reads alike. None fails to parse, which would make the oracle raise.
        /// </para>
        /// <para>
        /// <c>v</c> is an integer too, and holds the four that tell a double from the number: 2^53, 2^53 + 1, 2^60 and
        /// 2^60 + 1. The service keeps each as the integer it is, and reads a number it is sent with a point as a
        /// double, which holds the first and third and not the others; a whole unit is nothing to a double there, and
        /// a bound loosened by one comes back as the number it was loosened from. Every one lies beyond an
        /// <c>INTEGER</c>'s range, so <c>RETURNING BIGINT</c> answers each rather than raising.
        /// </para>
        /// </remarks>
        static readonly CosmosContainerMetadata Declared = new CosmosContainerMetadata("declared", new[] { "/category" })
            .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(
                """{ "type": "object", "properties": { "body": { "type": "string" }, "n": { "type": "integer" }, "v": { "type": "integer" } } }""")!));

        /// <summary>
        /// A fifth container, of three kinds, whose declaration is conditional: <c>label</c> a string in kind <c>a</c>
        /// and an integer in kind <c>b</c>, a canonical UUID <c>ref</c> in kinds <c>a</c> and <c>c</c>, and <c>kind</c>
        /// required and one of the three — <see cref="Planning.CosmosPushdownProbeTests.KindsSchema"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What a filter's siblings prove, rather than the declaration alone, is what the predicate oracle decides that
        /// the planner before it did not: an equality over <c>label</c> exact beside <c>kind = 'a'</c>, a membership
        /// covering the required enum deleted, a kind the enum has not kept nothing, a UUID lowered under each kind a
        /// disjunction spells. Every document keeps the declaration, as <c>declared</c>'s do.
        /// </para>
        /// <para>
        /// Partitioned on <c>kind</c>, the one container that is, so that every statement over it is routed on what its
        /// filter and the declaration say of the key: a partition skipped that holds a row the statement keeps is a row
        /// lost, which the comparison with Calcite sees.
        /// </para>
        /// </remarks>
        static readonly CosmosContainerMetadata Kinds = new CosmosContainerMetadata("kinds", new[] { "/kind" })
            .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(Planning.CosmosPushdownProbeTests.KindsSchema)!));

        /// <summary>
        /// The documents <see cref="Kinds"/> holds: each kind with and without what it declares, a string <c>"30"</c> and
        /// an integer 30 at <c>label</c> in the two kinds that differ there, and a string <c>"1"</c> beside an integer
        /// <c>n</c> of 1, which render alike.
        /// </summary>
        static readonly string[] KindsDocuments =
        [
            """{"id":"1","category":"k","kind":"a","label":"30","ref":"123e4567-e89b-12d3-a456-426614174000","n":1}""",
            """{"id":"2","category":"k","kind":"a","label":"zebra","ref":"223e4567-e89b-12d3-a456-426614174000"}""",
            """{"id":"3","category":"k","kind":"a","label":"x","ref":"123e4567-e89b-12d3-a456-426614174000","n":2}""",
            """{"id":"4","category":"k","kind":"b","label":30,"n":1}""",
            """{"id":"5","category":"k","kind":"b","label":7}""",
            """{"id":"6","category":"k","kind":"c","ref":"123e4567-e89b-12d3-a456-426614174000","n":1}""",
            """{"id":"7","category":"k","kind":"c","ref":"323e4567-e89b-12d3-a456-426614174000"}""",
            """{"id":"8","category":"k","kind":"a"}""",
            """{"id":"9","category":"k","kind":"b"}""",
            """{"id":"10","category":"k","kind":"a","label":"1","n":1}""",
        ];

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
        /// <c>n</c> integer arrays, which it orders other than as SQL compares them; <c>k</c> an integer
        /// and <c>b</c> a boolean, null or absent, as declared.
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
            """{"id":"1","category":"o","t":"Trail","f":[2],"n":[2],"k":3,"b":true}""",
            """{"id":"2","category":"o","t":"apple","f":[10],"n":[10],"k":-1,"b":false}""",
            """{"id":"3","category":"o","t":"Zebra","f":["a"],"n":[1,5],"k":10}""",
            """{"id":"4","category":"o","t":"a-b","f":{"b":1},"n":[],"k":null,"b":true}""",
            """{"id":"5","category":"o","t":null,"f":{"a":2},"n":[1],"k":3}""",
            """{"id":"6","category":"o","f":[1,2],"n":[1,2]}""",
            """{"id":"7","category":"o","t":"a_b","f":[],"k":0,"b":false}""",
            """{"id":"8","category":"o","t":"Trail blazer","f":{}}""",
            """{"id":"a","category":"o","s":"a","u":"b"}""",
            """{"id":"\uE000","category":"o","s":"\uE000","u":"\uD83D\uDE00"}""",
            """{"id":"\uFF21","category":"o","s":"\uFF21","u":"\uFF20"}""",
            """{"id":"\uD83D\uDE00","category":"o","s":"\uD83D\uDE00","u":"a"}""",
            """{"id":"\uFFFD","category":"o","s":"\uFFFD","u":"\uFFFD"}""",
        ];

        /// <summary>
        /// The documents the declared container holds: a string at <c>body</c> in all but one, which lacks it, an
        /// integer at <c>n</c>, and in the first four an integer at <c>v</c> a double does not always hold.
        /// </summary>
        static readonly string[] DeclaredDocuments =
        [
            """{"id":"1","category":"a","body":"2","n":1,"v":9007199254740992}""",
            """{"id":"2","category":"a","body":" 2 ","n":2,"v":9007199254740993}""",
            """{"id":"3","category":"a","body":"3","n":3,"v":1152921504606846976}""",
            """{"id":"4","category":"a","body":"2.0","n":2,"v":1152921504606846977}""",
            """{"id":"5","category":"b","body":"2.5","n":5}""",
            """{"id":"6","category":"b"}""",
        ];

        static CosmosClient? _client;
        static Container? _container;
        static Container? _typedContainer;
        static Container? _orderingContainer;
        static Container? _declaredContainer;
        static Container? _kindsContainer;
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
                _declaredContainer = await Seed(database, "declared", DeclaredDocuments, cts.Token);
                _kindsContainer = await Seed(database, "kinds", KindsDocuments, cts.Token, "kind");

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
                _declaredContainer = null;
                _kindsContainer = null;
            }
        }

        /// <summary>
        /// Recreates a container and writes the given documents into it.
        /// </summary>
        /// <remarks>
        /// Created with the service's default indexing policy unless a <c>policy</c> is given: the
        /// <c>ORDER BY</c> yardstick names the composite indexes its metadata declares, so that the
        /// policy Azure would hold a statement to is the one the container has.
        /// </remarks>
        static async Task<Container> Seed(Database database, string name, string[] documents, CancellationToken cancellationToken, string key = "category", IndexingPolicy? policy = null)
        {
            try { await database.GetContainer(name).DeleteContainerAsync(cancellationToken: cancellationToken); } catch (CosmosException) { }

            var properties = new ContainerProperties(name, "/" + key);
            if (policy is not null)
                properties.IndexingPolicy = policy;

            var container = (await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken)).Container;

            foreach (var json in documents)
            {
                using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                using var document = System.Text.Json.JsonDocument.Parse(json);

                var partitionKey = document.RootElement.TryGetProperty(key, out var category)
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
            _declaredContainer = null;
            _kindsContainer = null;
        }

        /// <summary>
        /// What a statement's plan reads at run time: the schema, and the values its parameters are
        /// bound to, under the names Calcite asks for them by: <c>?0</c>, <c>?1</c> and so on.
        /// </summary>
        sealed class TestDataContext : DataContext
        {

            readonly SchemaPlus _rootSchema;
            readonly JavaTypeFactory _typeFactory;
            readonly IReadOnlyList<object?> _parameters;

            public TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory, IReadOnlyList<object?>? parameters = null)
            {
                _rootSchema = rootSchema;
                _typeFactory = typeFactory;
                _parameters = parameters ?? [];
            }

            public SchemaPlus getRootSchema() => _rootSchema;

            public JavaTypeFactory getTypeFactory() => _typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => name.StartsWith('?') && int.TryParse(name.AsSpan(1), out var i) && i < _parameters.Count ? _parameters[i]! : null!;

        }

        /// <summary>
        /// The containers the corpus is written over, in the order their rules are registered.
        /// </summary>
        static IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)> CorpusContainers =>
        [
            (Products, _container!),
            (Typed, _typedContainer!),
            (Ordering, _orderingContainer!),
            (Declared, _declaredContainer!),
            (Kinds, _kindsContainer!),
        ];

        /// <summary>
        /// Plans and executes a statement, with the pushdown rules or with only the way out.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="pushdown">Whether the pushdown rules may fire.</param>
        /// <param name="sent">Where given, collects every Cosmos statement the plan sends, so that a
        /// report can say what was pushed and not only what came back.</param>
        static Task<List<object>> Run(string sql, bool pushdown, ICollection<string>? sent = null) => Run(sql, pushdown, sent, CorpusContainers, null);

        /// <summary>
        /// Plans and executes a statement over the given containers, its parameters bound to the given
        /// values.
        /// </summary>
        /// <remarks>
        /// The containers are the schema, each registered under its metadata's name, and the rules
        /// registered are theirs alone: a statement over one set of containers plans as it would with
        /// no others beside it.
        /// </remarks>
        static async Task<List<object>> Run(string sql, bool pushdown, ICollection<string>? sent, IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)> containers, IReadOnlyList<object?>? parameters)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var tables = containers.Select(c => new CosmosTable(c.Metadata, Recorded(new CosmosQueryExecutor(c.Container), sent))).ToList();

            var rootSchema = CalciteSchema.createRootSchema(false);
            foreach (var table in tables)
                rootSchema.add(table.Container.Name, table);

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

            // A convention per container, so every table needs its rules registered whichever a
            // statement happens to name.
            var pushdownRules = new List<string>();

            foreach (var convention in tables.Select(t => t.Convention))
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

            var context = new TestDataContext(rootSchema.plus(), typeFactory, parameters);

            var rows = new List<object>();

            await using (var cursor = await factory.OpenAsync(context, CancellationToken.None))
                while (await cursor.ReadAsync(CancellationToken.None))
                    rows.Add(cursor.Current!);

            return rows;
        }

        /// <summary>
        /// Wraps an executor so that what it is asked to send is written down, or returns it as it is
        /// where nothing is to be.
        /// </summary>
        static ICosmosQueryExecutor Recorded(ICosmosQueryExecutor executor, ICollection<string>? sent)
        {
            return sent is null ? executor : new RecordingExecutor(executor, sent);
        }

        sealed class RecordingExecutor(ICosmosQueryExecutor inner, ICollection<string> sent) : ICosmosQueryExecutor
        {

            public ValueTask<Apache.Calcite.Extensions.Runtime.IClrCursor<System.Text.Json.JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                lock (sent)
                    sent.Add(query.PointReadIds is { } ids ? $"ReadMany({string.Join(", ", ids)})" : query.PointReadId is string id ? $"Read({id})" : query.Sql);

                return inner.OpenAsync(query, partitionKey, cancellationToken);
            }

        }

        /// <summary>
        /// Determines whether a rule is one the oracle must not have.
        /// </summary>
        /// <remarks>
        /// Everything that moves work to the service, and everything that answers a filter by the
        /// adapter's own reasoning: <see cref="CosmosFilterReduceRule"/> moves nothing, but the empty
        /// relation or the bare input it puts in a filter's place is the predicate oracle's verdict, and
        /// an oracle that applied it would agree with a wrong one. Not the way out, which is what makes
        /// a pushed subtree readable at all and which the oracle needs for the scan; and not Calcite's
        /// own rewrites, which the convention registers because a bare Volcano planner has no logical
        /// rule set -- they preserve meaning, and one of them is what makes a grouping-set AVG planable
        /// by the CLR convention in the first place.
        /// </remarks>
        static bool IsPushdown(org.apache.calcite.plan.RelOptRule rule)
        {
            return rule is CosmosAggregateRule or CosmosAggregateSplitRule
                or CosmosFilterRule or CosmosFilterSplitRule or CosmosFilterReduceRule
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
        /// What one plan of a statement did: the rows it answered, canonical and in the order they
        /// came, or what it raised instead.
        /// </summary>
        readonly record struct Outcome(List<string>? Rows, Exception? Raised);

        /// <summary>
        /// Plans and executes a statement one way, and keeps a failure as an answer rather than
        /// letting it escape.
        /// </summary>
        static async Task<Outcome> Attempt(string sql, bool pushdown, ICollection<string>? sent = null)
        {
            try { return new Outcome((await Run(sql, pushdown, sent)).Select(Canonical).ToList(), null); }
            catch (Exception e) when (e is not SkipException) { return new Outcome(null, e); }
        }

        /// <summary>
        /// The functions whose argument outside their domain makes the service fail the whole
        /// statement where Calcite answers NaN for the row — measured in
        /// <c>CosmosQueryExecutorTests.OutOfDomainArithmeticFailsTheQuery</c>, and pushed anyway by
        /// decision; see <c>DESIGN.md</c>. Named as SQL spells them: <c>LN</c> is the service's
        /// <c>LOG</c>.
        /// </summary>
        /// <remarks>
        /// A declared list and not a pattern: a service error is acceptable only where it was measured
        /// and chosen, and nothing else earns the exception by resembling these.
        /// </remarks>
        static readonly string[] OutOfDomainFunctions = ["ASIN", "ACOS", "SQRT", "LN"];

        /// <summary>
        /// Decides whether a pushed plan's outcome refines the oracle's, and describes why not, or
        /// returns <c>null</c> where it does. <b>The error refinement order lives here and nowhere
        /// else</b>; see <c>DESIGN.md</c> under <em>Differential testing</em>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A Calcite error refines to anything.</b> Where the engine raises, the pushdown may answer
        /// rows or fail in its turn. That is what licenses the <c>RETURNING</c> statement in
        /// <see cref="WithoutAnOracle"/>: the engine asserts a type the standard says it should
        /// convert to, and the pushed statement answers what the standard says.
        /// </para>
        /// <para>
        /// <b>A failure of the pushdown is never acceptable where Calcite answers</b>, with one
        /// exception: the service failing a statement that names one of
        /// <see cref="OutOfDomainFunctions"/>. It must be the service's failure — a plan that cannot
        /// be implemented, or a row the reader cannot read, is the adapter's, and is a defect whatever
        /// the statement names.
        /// </para>
        /// <para>
        /// Which side failed is part of the answer, not an incidental detail of the message: a pushdown
        /// that throws where the oracle returns rows is a different defect from one that returns the
        /// wrong rows, and the two were indistinguishable while both were reported as "failed to run".
        /// </para>
        /// </remarks>
        static string? Refines(string sql, bool ordered, Outcome oracle, Outcome pushed)
        {
            if (oracle.Raised is not null)
                return null;

            if (pushed.Raised is Exception raised)
            {
                if (IsServiceFailure(raised) && NamesAnOutOfDomainFunction(sql))
                    return null;

                return $"{sql}\n  the pushdown failed to run: {raised.Message}\n  oracle: [{string.Join("; ", oracle.Rows!)}]";
            }

            var expected = oracle.Rows!.ToList();
            var actual = pushed.Rows!.ToList();

            if (ordered == false)
            {
                expected.Sort(StringComparer.Ordinal);
                actual.Sort(StringComparer.Ordinal);
            }

            if (actual.SequenceEqual(expected))
                return null;

            return $"{sql}\n  pushed: [{string.Join("; ", actual)}]\n  oracle: [{string.Join("; ", expected)}]";
        }

        /// <summary>
        /// Determines whether a failure is the service refusing the statement, rather than the adapter
        /// failing to plan, implement or read it.
        /// </summary>
        static bool IsServiceFailure(Exception e)
        {
            for (var inner = e; inner is not null; inner = inner.InnerException)
                if (inner is CosmosException)
                    return true;

            return false;
        }

        static bool NamesAnOutOfDomainFunction(string sql)
        {
            return OutOfDomainFunctions.Any(name => System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\b{name}\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        /// <summary>
        /// Runs one statement both ways and describes the difference, or returns <c>null</c> where
        /// there is none.
        /// </summary>
        /// <remarks>
        /// Stricter than <see cref="Refines"/> in one respect, and deliberately: an oracle that raises
        /// is reported here rather than accepted. Every outcome refines a Calcite error, so a corpus
        /// statement whose oracle raises compares nothing at all, and belongs in
        /// <see cref="WithoutAnOracle"/> with the reason rather than in a list that claims a comparison.
        /// </remarks>
        static async Task<string?> Compare(string sql, bool ordered)
        {
            var oracle = await Attempt(sql, pushdown: false);
            if (oracle.Raised is Exception e)
                return $"{sql}\n  the oracle failed to run, so this compares nothing: {e.Message}";

            return Refines(sql, ordered, oracle, await Attempt(sql, pushdown: true));
        }

        /// <summary>
        /// The corpus. Ordered statements compare as sequences; the rest as multisets.
        /// </summary>
        /// <remarks>
        /// Out-of-domain arithmetic (<c>SQRT(-1)</c> and kin) is pushed deliberately and diverges
        /// deliberately — the service fails the query where Calcite yields NaN; see <c>DESIGN.md</c>.
        /// It used to be excluded by name. It is judged now, by <see cref="Refines"/>, which accepts
        /// exactly that failure and no other.
        /// Integer overflow (<c>price * 100000000</c>) is left out of the corpus by name, pushed
        /// deliberately too — Calcite wraps, where SQL raises and the service computes the value, so a
        /// filter keeps the true product and a projected one fails the read. See <c>DESIGN.md</c>.
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

            // A typed accessor over the typed container's label, which holds 30 in document 2, an array
            // in 4, an object in 5, null in 6 and nothing in 7: the five a RETURNING INTEGER answers for,
            // 30 and four nulls. The rest hold a string or a boolean, which Calcite raises on rather than
            // answering the null SQL:2016 gives, and are filtered out so that the oracle runs (#120).
            // Under a page the sort pushed ORDER BY c.label, the service put the array and the object
            // after 30, and the page came back [null, null, 30] and [30, null, null] where SQL answers
            // three nulls; RETURNING BOOLEAN the same with true. Unpaged it failed the read on the
            // object, as did the column alone, the DISTINCT, the GROUP BY, the COUNT, the COALESCE and
            // the read by id and by a batch of ids -- the accessor was written bare and the reader
            // refused the object for an integer.
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" = '2' OR c.\"id\" = '4' OR c.\"id\" = '5' ORDER BY 1 NULLS FIRST FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" = '2' OR c.\"id\" = '4' OR c.\"id\" = '5' ORDER BY 1 DESC NULLS LAST OFFSET 2 ROWS", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" = '2' OR c.\"id\" = '4' OR c.\"id\" = '5' ORDER BY 1 NULLS FIRST", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label' RETURNING BOOLEAN) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" = '3' OR c.\"id\" = '4' OR c.\"id\" = '5' ORDER BY 1 NULLS FIRST FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" IN ('2', '4', '5')", false),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" IN ('2', '4', '5')", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER), COUNT(*) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" IN ('2', '4', '5') GROUP BY JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER)", false),
            ("SELECT COUNT(JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER)) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" IN ('2', '4', '5')", false),
            ("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.label' RETURNING BOOLEAN) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" IN ('3', '4', '5')", false),
            ("SELECT c.\"id\", COALESCE(JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER), -1) FROM typed AS c WHERE c.\"$.category\" = 'b' OR c.\"id\" IN ('2', '4', '5')", false),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'a' AND c.\"id\" = '5'", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c WHERE c.\"$.category\" = 'a' AND c.\"id\" IN ('2', '4', '5')", false),

            // And where a typed key still pushes: a path the container declares the type, or null --
            // k an integer, b a boolean -- whose documents hold nothing the accessor reads null for but
            // null and nothing.
            ("SELECT JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER) FROM ordering AS c ORDER BY 1 NULLS FIRST", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER) FROM ordering AS c ORDER BY 1 DESC NULLS LAST FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.b' RETURNING BOOLEAN) FROM ordering AS c ORDER BY 1 NULLS FIRST", true),

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
            ("SELECT c.\"$.category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"$.category\" HAVING c.\"$.category\" <> 'bikes'", false),
            // A sort over a key the filter confines to strings, which says what a declaration would.
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') IN ('Sprint', 'Marathon', 'Slipper') ORDER BY JSON_VALUE(c.\"DOC\", '$.name')", true),
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

            // Out of domain for three of the prices: Calcite answers NaN for those rows, and the
            // service fails the statement. The one service failure the refinement order accepts.
            ("SELECT SQRT(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) - 100) FROM products AS c", false),

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

            // The key against text a boolean renders as, whose alternative compares the key with a
            // boolean boxed as VARIANT -- the box the number beside '30' takes too, a column having no
            // call to re-type. Nothing here stores one, so what this checks is that the statement the
            // box writes runs at the service and loses nothing.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"$.category\" AS VARCHAR) = 'true'", false),


            // ── The probes of the soundness review, kept ─────────────────────────────
            //
            // Each was run once against the emulator and answered differently pushed, or agreed and is
            // kept as the control beside one that did not. Named P1 to P27 as the review named them, so
            // that a statement here can be traced to the root cause it was found under.

            // The accessor's null set (A). JSON_VALUE is null for an object and an array as well as for
            // an absent path and a JSON null, and IS NULL over it tested the path's null set alone:
            // products' metadata is an object, and typed's label holds one document per JSON type.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL", false),                                  // P1
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL AND c.\"$.category\" = 'bikes'", false),   // P19
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') IS NOT NULL", false),                                        // P2
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') IS NULL", false),                                            // P3
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.label') IS NULL)", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" IS NOT NULL", false), // P25
            ("SELECT c.\"id\" FROM typed AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.label'), 'none') = 'none'", false),                         // P26
            ("SELECT c.\"id\", CASE WHEN JSON_VALUE(c.\"DOC\", '$.label') IS NULL THEN 'missing' ELSE 'present' END FROM typed AS c", false),   // P27, the projection, which agreed

            // The same for the other readings: JSON_QUERY is null for every scalar, and a typed
            // RETURNING for an object -- found beside the probes, the same test applied to them.
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_QUERY(c.\"DOC\", '$.label') IS NULL", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_QUERY(c.\"DOC\", '$.label') AS \"L\" FROM typed AS c) AS p WHERE p.\"L\" IS NOT NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata' RETURNING INTEGER) IS NULL", false),

            // The negated equality's guard (B), which is the same null set in the other position: the
            // comparison has to be true wherever SQL's is unknown, so that the NOT discards the row,
            // and it named an absent path and a null but not the object and the array. Directly, and
            // under a negated conjunction and disjunction. The typed equality under a NOT had no type
            // test where its inequality had one; the last is its control, over a path holding no object.
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes')", false),                                     // P4
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' AND c.\"$.category\" = 'a')", false),          // P5
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' OR c.\"$.category\" = 'b')", false),           // P6
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.metadata' RETURNING INTEGER) = 5)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) = 5)", false),

            // A CASE condition under a NOT (C). The condition selects an arm, so it is a positive
            // position whatever encloses the CASE, and it was written in the NOT's: the inequality
            // unguarded and the equality guarded the wrong way, each taking the then arm for the
            // null category where SQL takes the else.
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (CASE WHEN c.\"$.category\" <> 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" = '6' END)", false),  // P7
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (CASE WHEN c.\"$.category\" = 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" <> '6' END)", false),  // P9

            // IS NULL over a predicate (D), which asks where it is unknown. The comparison written
            // positively is false there, so nothing is left to find; COALESCE(p, FALSE) expands to one.
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" <> 'bikes') IS NULL", false),                  // P18
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes') IS NOT NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT ((c.\"$.category\" = 'bikes') IS NULL)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT COALESCE(c.\"$.category\" <> 'bikes', FALSE)", false),        // P8

            // A CASE observes absence: its else arm answers where its condition's path is absent. The
            // guard a comparison takes tested the paths inside one, and the split rule weakened one
            // to IS_DEFINED of them -- found beside the probes, and each answered differently. The
            // last two are controls, a COALESCE over a path every document has, and one the split
            // already left alone.
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (COALESCE(JSON_VALUE(c.\"DOC\", '$.label'), 'none') = 'x')", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.label'), 'none') <> 'x'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT ((CASE WHEN c.\"$.category\" = 'bikes' THEN 1 ELSE 0 END) = 1)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (CASE WHEN c.\"$.category\" = 'bikes' THEN INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) ELSE 'z' END) = 'z'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (COALESCE(JSON_VALUE(c.\"DOC\", '$.name'), 'none') = 'Exact')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')), 'z') = 'z'", false),

            // An operator over the rendering, which reads text in Calcite and the stored value at the
            // service (E, F). A string function is undefined there over a number or a boolean, and a
            // value passed out through a CASE arrives as the number or the boolean: projected, filtered
            // on, and handed to a comparison that saw no rendering among its operands.
            ("SELECT c.\"id\", UPPER(JSON_VALUE(c.\"DOC\", '$.label')) FROM typed AS c", false),                                    // P10
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') || '!' FROM typed AS c", false),                                    // P11
            ("SELECT c.\"id\" FROM typed AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.label')) = 'TRUE'", false),                     // P12
            ("SELECT c.\"id\" FROM typed AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.label')) = '30'", false),                       // P13
            ("SELECT c.\"id\" FROM typed AS c WHERE NOT (UPPER(JSON_VALUE(c.\"DOC\", '$.label')) = 'BIKES')", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CHAR_LENGTH(JSON_VALUE(c.\"DOC\", '$.label')) = 2", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') || 'x' = '30x'", false),
            ("SELECT c.\"id\", COALESCE(JSON_VALUE(c.\"DOC\", '$.label'), 'none') FROM typed AS c", false),
            ("SELECT c.\"id\", CASE WHEN c.\"$.category\" = 'a' THEN JSON_VALUE(c.\"DOC\", '$.label') ELSE 'z' END FROM typed AS c", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CASE WHEN c.\"$.category\" = 'a' THEN JSON_VALUE(c.\"DOC\", '$.label') ELSE 'z' END = '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CASE WHEN c.\"$.category\" = 'a' THEN JSON_VALUE(c.\"DOC\", '$.label') ELSE 'z' END = 'z'", false),

            // Division (G): Calcite truncates an integer quotient toward zero, and the service answers
            // the fraction, which an integer column then cannot be read as. Filtered, projected, and
            // over a negative dividend, where truncation and flooring part; the last is the control,
            // a quotient Calcite computes in doubles.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 = 2", false),       // P14
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 3 FROM products AS c", false),                 // P15
            ("SELECT c.\"id\", (0 - JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER)) / 3 FROM products AS c", false),
            ("SELECT c.\"id\", c.\"_ts\" / CAST(2 AS DOUBLE) FROM products AS c", false),

            // An equality over a view's JSON_QUERY column. The fragment's text against text is never
            // the raw comparison -- the service's object equals no string, and the stored string
            // 'bikes' is one JSON_QUERY answers null for -- and the filter rule pushed it as though it
            // were the accessor's, which the implementor then refused: the plan failed where Calcite
            // answers no rows.
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_QUERY(c.\"DOC\", '$.label') AS \"L\" FROM typed AS c) AS p WHERE p.\"L\" = 'bikes'", false),

            // DISTINCT under a point read, single and batched (P17, P20 and P21). All three agreed when
            // the review ran them, and are kept with the DISTINCT statements above, beside the batch it
            // found duplicating a row: a key whose two documents share a value, which a ReadMany returns
            // once per document.

            // A control for the quotient: the integer accessor over a double divisor, which Calcite widens
            // and divides in doubles. RETURNING DOUBLE would be the obvious spelling and has no oracle over
            // these prices -- Calcite raises over a stored integer rather than widening it.
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / CAST(3 AS DOUBLE) FROM products AS c", false),


            // ── The probes of the review of the fixes, kept ──────────────────────────
            //
            // Named as that review named them. Each answered differently pushed before the change it is
            // kept beside, or is the control that says the shape still pushes where it agrees.

            // A predicate read as a value (D). The guard that makes a comparison right in a WHERE makes it
            // false where SQL's is unknown, and so does the service's own equality over a JSON null; a
            // WHERE cannot tell the two apart, and an enclosing comparison, a column and a grouping key
            // can. The last two are controls: a comparison Calcite types NOT NULL, and a CASE condition.
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" <> 'bikes') = FALSE", false),                                         // D1
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes') = FALSE", false),                                          // D2
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes') = (c.\"id\" = '3')", false),                               // D3
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" <> 'bikes') IN (FALSE)", false),                                      // D21
            ("SELECT c.\"id\" FROM products AS c WHERE (JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) = 5) = FALSE", false),
            ("SELECT c.\"id\", c.\"$.category\" <> 'bikes' FROM products AS c", false),                                                         // D5
            ("SELECT c.\"id\", c.\"$.category\" = 'bikes' FROM products AS c", false),                                                          // D6
            ("SELECT c.\"id\", NOT (c.\"$.category\" = 'bikes') FROM products AS c", false),                                                    // D7
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' FROM typed AS c", false),                                             // D8
            ("SELECT c.\"id\", CASE WHEN c.\"id\" <> '9' THEN c.\"$.category\" = 'bikes' ELSE FALSE END FROM products AS c", false),            // D20
            ("SELECT c.\"$.category\" = 'bikes', COUNT(*) FROM products AS c GROUP BY c.\"$.category\" = 'bikes'", false),                      // D29
            ("SELECT c.\"id\", c.\"id\" = '3' FROM products AS c", false),
            ("SELECT c.\"id\", CASE WHEN c.\"$.category\" <> 'bikes' THEN 'x' ELSE 'y' END FROM products AS c", false),

            // A cast to text with a width (W), which truncates where the cast without one renders. Taken as
            // the rendering it was dropped from the equality, and over the partition key it routed the read
            // to a partition named for the truncated text; the last is the same beside a point read.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR(3)) = 'bik'", false),                     // W1
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"$.category\" AS VARCHAR(3)) = 'bik'", false),                                 // W3
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR(2)) = 'Ex'", false),                       // W4
            ("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"$.category\" AS VARCHAR(3)) = 'bik' AND c.\"id\" = '1'", false),

            // A rendering inside the box Calcite's coercion puts around a side compared with a VARIANT (E7),
            // which the comparison, LIKE and IS NULL each looked at instead of the rendering. The fourth is
            // the control: against unambiguous text the box changes nothing, and the equality pushes.
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = CAST('30' AS VARIANT)", false),                         // E7
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARIANT) AS VARCHAR) LIKE '3%'", false),    // W5
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARIANT) IS NULL", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = CAST('bikes' AS VARIANT)", false),

            // Grouping by a rendering (E9, E10 and E13), which Calcite groups by its text and the service by
            // the value: typed's price is the number 30 in one document and the string "30" in another. The
            // same three statements as the first review's J2, J1 and J13, and kept with them above.

            // Arithmetic in a decimal fraction (G), which Calcite computes exactly and the service in
            // doubles. Filtered, projected, grouped by and summed; the remainder by 0.3 was found beside them.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 1.1 = 374", false),               // G4
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 1.1 FROM products AS c", false),                           // G5
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 0.07 = 0.35", false),             // G6
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 0.7 FROM products AS c", false),                           // G15
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) + 0.1 + 0.2 = 10.3", false),        // G20
            ("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 1.1, COUNT(*) FROM products AS c GROUP BY JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 1.1", false), // G26
            ("SELECT SUM(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 1.1) FROM products AS c", false),                                // G27
            ("SELECT c.\"id\" FROM products AS c WHERE MOD(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER), 0.3) = 0.2", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 2 > 100", false),

            // An accessor addressing through a rendering (N), whose text Calcite parses as JSON and whose
            // value the service holds. The last is the control: a JSON_QUERY's text is the fragment.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(JSON_VALUE(c.\"DOC\", '$.name'), '$') IS NOT NULL", false),                  // N1
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(JSON_VALUE(c.\"DOC\", '$.name'), '$') = 'Exact'", false),                       // N3
            ("SELECT c.\"id\", JSON_VALUE(JSON_VALUE(c.\"DOC\", '$.name'), '$') FROM typed AS c", false),
            ("SELECT c.\"id\", JSON_VALUE(JSON_QUERY(c.\"DOC\", '$.metadata'), '$.sku') FROM products AS c", false),                           // N2


            // ── RETURNING DECIMAL, which parses ──────────────────────────────────────
            //
            // Every other numeric RETURNING asserts its type and raises on anything else; DECIMAL parses what the path
            // holds, a stored "2" as 2. Read as the path, and as a claim that the path holds the number, it lost the
            // strings at the service and rewrote a filter over a declared string to FALSE. Found by the review of the
            // predicate oracle's gate, whose two statements open this.
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = 2", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) > 2", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = 2.5", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL(10, 1)) = 2.5", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) <> 2", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = 2)", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) IS NOT NULL", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) IN (2, 3)", false),
            ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) AS \"B\" FROM declared AS c) AS p WHERE p.\"B\" = 2", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) FROM declared AS c", false),
            ("SELECT c.\"id\" FROM declared AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL), c.\"id\"", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL), COUNT(*) FROM declared AS c GROUP BY JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL)", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.n' RETURNING DECIMAL) = 2", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.n' RETURNING DECIMAL) > 2", false),

            // The same over a container that declares nothing, where typed's price is the number 30 in one document and
            // the string "30" in another; and over products', which holds only numbers, as the control.
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) > 8", false),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) FROM typed AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) > 100", false),

            // The controls: text over the declared string is the value, and pushes exactly; a RETURNING that asserts is
            // the value wherever Calcite answers one.
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body') = '2'", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) = 2", false),


            // ── A number the service reads as a double ───────────────────────────────
            //
            // An exact literal was sent with a point, and the service reads it as a double; declared's v holds 2^53,
            // 2^53 + 1, 2^60 and 2^60 + 1 as the integers they are. Found by the review of the RETURNING DECIMAL fix.
            //
            // The window around a cast's comparison, loosened by one, where one is nothing to a double: 2^60 - 1 came
            // back as 2^60 and excluded it. A cast to DOUBLE beside them, which rounds a stored integer from 2^52. And
            // the window around a literal a hair below a whole number: RETURNING DECIMAL truncates 2.9999999999999999
            // to 2, and the bound below it, 1.9999999999999999, came back as 2 and excluded the stored 2.
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING DECIMAL) = 1152921504606846976", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING DECIMAL) >= 1152921504606846976", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING DECIMAL) = 9007199254740992", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING DECIMAL) >= 9007199254740992", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING DECIMAL) >= 9007199254740993", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.v') AS BIGINT) = 9007199254740992", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.v') AS BIGINT) = 1152921504606846976", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.v') AS DOUBLE) = 1152921504606846976", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.n' RETURNING DECIMAL) = 2.9999999999999999", false),

            // An exact comparison against a literal the double does not hold, which came back as its neighbour and
            // matched the document holding that; and one the double does hold, past 2^53, where how the service orders
            // a stored integer against a double is not measured. The last is an approximate literal, which Calcite
            // compares in doubles and the service with the integer as it is. The first of the controls pushes.
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) = 1152921504606846977", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) = 9007199254740993", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) IN (1152921504606846977, 9007199254740993)", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) > 1152921504606846976", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) = 1152921504606846976E0", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) > 9007199254740991", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) = 9007199254740992", false),

            // The presence weakening through a disjunction (L), the half a CASE did not cover. A
            // disjunction with a branch that has no weakening was weakened whole, to IS_DEFINED of every
            // path any branch reads, and a branch is true without the others' paths. The first two are
            // ternary logic partitioning's witnesses, the NOT φ it pushed: the converter pushes their NOT
            // down through the connectives above the IN, and a branch NOT (… AND …) is what arrives.
            // The third is the witness recorded beside them, the fourth the shape written directly. Then
            // a conjunction compared as a value, which is false where one conjunct is. The last two are
            // controls, a disjunction compared and a path both branches of an inner disjunction read,
            // which agreed before.
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (((JSON_VALUE(c.\"DOC\", '$.tags') IN ('Road Runner', 'BIKES') OR JSON_VALUE(c.\"DOC\", '$.metadata.sku') <> 'SPRINT') AND ((CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) - 3E0) = 5E0 AND (CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) || '!') > 'bikes')))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT ((((JSON_VALUE(c.\"DOC\", '$.name') || '!') = 'shoes' AND c.\"id\" > 'SPRINT') AND (JSON_VALUE(c.\"DOC\", '$.tags[0]') IN ('bikes', 'bikes') OR (CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) + 1E0) NOT IN (5E0, 340E0))))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT ((JSON_VALUE(c.\"DOC\", '$.price') <> 'SPRINT' AND JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) < 10) AND (c.\"$.category\" IN ('Sprint!', 'none') OR (CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) * 3E0) = 100E0))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Sprint')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes' AND JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100) <> (c.\"id\" = '5')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes' AND JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100) = FALSE", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes' OR JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100) <> (c.\"id\" = '5')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR (INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Road Runner' AND (INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'X'))", false),

            // The rest of the witnesses ternary logic partitioning recorded on main, each wrong there and
            // right over the fixes: L's CASE, whose condition is unknown where price is absent; M, the
            // null guard written over the path inside a COALESCE, which is never null; and last F
            // through a COALESCE, the raw number compared as its rendering. Between M's three and F,
            // the neighbours M was checked against, which agree: a typed accessor, the promoted
            // column, a fold, a NULLIF, a coalesce of two paths, a CASE under a NOT and an ordering.
            ("SELECT c.\"id\" FROM typed AS c WHERE CASE WHEN CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 8E0 THEN c.\"$.category\" = 'b' ELSE c.\"$.category\" = 'a' END", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.big'), 'none') <> 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.category'), 'none') <> 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (COALESCE(JSON_VALUE(c.\"DOC\", '$.tags[0]'), 'none') = 'outdoor')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER), 0) <> 80", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (COALESCE(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER), 0) = 80)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(c.\"$.category\", 'none') <> 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE UPPER(COALESCE(JSON_VALUE(c.\"DOC\", '$.category'), 'none')) <> 'BIKES'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE NULLIF(JSON_VALUE(c.\"DOC\", '$.name'), 'Exact') <> 'Null'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.metadata.sku'), JSON_VALUE(c.\"DOC\", '$.name')) <> 'Sprint'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (CASE WHEN c.\"$.category\" = 'bikes' THEN JSON_VALUE(c.\"DOC\", '$.name') ELSE 'z' END = 'z')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.metadata.sku'), 'none') > 'A'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.price'), 'none') = '120'", false),

            // The presence weakening read through a call that answers a value over a null, on the
            // premise that every function is null for a null. CONCAT_WS skips the absent sku, and an
            // array holding a null is neither null nor empty, so each is true for documents with no
            // sku, and each pushed IS_DEFINED of the paths beneath it: the first two kept none of the
            // six they are true of, the last two one of seven.
            ("SELECT c.\"id\" FROM products AS c WHERE CONCAT_WS('-', JSON_VALUE(c.\"DOC\", '$.name'), JSON_VALUE(c.\"DOC\", '$.metadata.sku')) <> 'Road Runner-B-2'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (CONCAT_WS('-', JSON_VALUE(c.\"DOC\", '$.name'), JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'Road Runner-B-2')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CARDINALITY(ARRAY[INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku'))]) = 1", false),
            ("SELECT c.\"id\" FROM products AS c WHERE ARRAY_CONTAINS(ARRAY[INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')), 'x'], 'x')", false),

            // A path every branch of a disjunction reads, spelled two ways: the category as the
            // promoted column and through the document. Matched by the path rather than the
            // expression, the weakening now implies it, and the restriction it adds keeps every row.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 300 OR (INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) <> 'Zzz' AND (c.\"$.category\" = 'bikes' OR JSON_VALUE(c.\"DOC\", '$.category') = 'shoes'))", false),

            // A predicate read for its falsity: a negated disjunction needs every branch's paths, a
            // doubly negated conjunction every conjunct's, a compound compared with TRUE or FALSE is the
            // compound read either way, and a negated conjunction only what every conjunct needs to be
            // false. Each pushed nothing before and now pushes those paths, and each keeps every row.
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT (c.\"$.category\" = 'shoes' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT ((c.\"$.category\" = 'shoes' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') <> FALSE)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (NOT (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2'))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') = TRUE", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Sprint' OR (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2'))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT (INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Sprint' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) > 'A')", false),

            // Negations the disjunction fix closed beyond its own witnesses, as review wrote them, each
            // losing rows as written before bbb7a61: a disjunction with a negated conjunction for a
            // branch under a conjunct, which pushed the price and lost the shoe without one; a
            // conjunction cast to text, which pushed the category and the sku and kept none of three;
            // and the partitioning witness's shape with an IN holding a function, which kept none of
            // three.
            ("SELECT c.\"id\" FROM products AS c WHERE INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) <> 'Zzz' AND (JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2'))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CAST((c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') AS VARCHAR) = 'FALSE'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT ((c.\"$.category\" IN ('bikes', INITCAP(JSON_VALUE(c.\"DOC\", '$.name'))) OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) <> 'B-2') AND (JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 AND INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) > 'A'))", false),

            // ── What the predicate oracle decides, and the planner before it did not ─
            //
            // A weakening every document satisfies restricts nothing, and is not pushed: the service states id and
            // _etag of every document, so the definedness each branch implies of them is true everywhere.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes' OR INITCAP(c.\"_etag\") = 'X'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Sprint'", false),
            // Over the kinds, a declaration each kind states differently: what a sibling conjunct proves, what the
            // declaration guarantees, what it refutes, and a disjunction spelled per kind. The probes'
            // definedness test is the one left out, IS_DEFINED being no operator this validator knows.
            .. Planning.CosmosPushdownProbeTests.KindProbes.Where(p => p.Where.Contains("IS_DEFINED", StringComparison.Ordinal) == false).Select(p => ($"SELECT c.\"id\" FROM kinds AS c WHERE {p.Where}", false)),
            // And what the rows a filter kept let the clauses after it lean on: a projection of a form the filter
            // proves, a sort of a key it proves present, a grouping by a rendering it proves a string; each beside a
            // control that leans on nothing the filter proves.
            .. Planning.CosmosPushdownProbeTests.ClauseProbes.Select(p => (p.Sql, p.Ordered)),
            // And where a statement is sent: a point read, a batch of them, a partition a filter or the declaration
            // leaves one value, and a key's text that a number also renders as, which pins nothing.
            .. Planning.CosmosPushdownProbeTests.RoutingProbes.Select(p => (p.Sql, p.Ordered)),

            // Two containers in one statement, each filter read by its own declaration. Every container's rules see every
            // operator, and a conversion decided under the kinds' declaration made typed's first filter the empty
            // relation, losing all nine rows, and dropped its second, keeping nine too many (CosmosConvention.Owns).
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') IS NULL UNION ALL SELECT k.\"id\" FROM kinds AS k", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') IN ('a', 'b', 'c') UNION ALL SELECT k.\"id\" FROM kinds AS k", false),
            ("SELECT k.\"id\" FROM kinds AS k WHERE JSON_VALUE(k.\"DOC\", '$.kind') = 'a' AND JSON_VALUE(k.\"DOC\", '$.label') = '30' UNION ALL SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '30'", false),

            // Two predicates that print alike: a comparison of the numbers RETURNING DECIMAL parses, and the negated
            // comparison of the same renderings. Read as one opaque term, Calcite's digest naming no call's type, they
            // were X AND NOT X and the plan the empty relation, where Calcite keeps 2 and 4, " 2 " and "2.0" beside 2.
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = JSON_VALUE(c.\"DOC\", '$.n' RETURNING DECIMAL) AND NOT (JSON_VALUE(c.\"DOC\", '$.body') = JSON_VALUE(c.\"DOC\", '$.n'))", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.body') = JSON_VALUE(c.\"DOC\", '$.n')) AND JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = JSON_VALUE(c.\"DOC\", '$.n' RETURNING DECIMAL)", false),

            // Two renderings compared, one path a string and the other a number: Calcite keeps a document holding "3"
            // and 3, the two rendering alike, and the service compares a string with a number. One string side, by the
            // declaration or by a sibling's kind, pushed the raw comparison.
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body') = JSON_VALUE(c.\"DOC\", '$.n')", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body') >= JSON_VALUE(c.\"DOC\", '$.n')", false),
            ("SELECT c.\"id\" FROM kinds AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = 'a' AND JSON_VALUE(c.\"DOC\", '$.label') = JSON_VALUE(c.\"DOC\", '$.n')", false),
            ("SELECT c.\"id\" FROM kinds AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = 'a' AND JSON_VALUE(c.\"DOC\", '$.label') <= JSON_VALUE(c.\"DOC\", '$.n')", false),

            // A sort of an integer beyond 2^53 that a filter proves present, which the service now takes: 2^53 and
            // 2^53 + 1, 2^60 and 2^60 + 1 are one double each, and only an order of the integers themselves tells them
            // apart, paged as unpaged.
            ("SELECT JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) > 0 ORDER BY 1", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) > 0 ORDER BY 1 DESC", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) > 0 ORDER BY 1 DESC FETCH NEXT 1 ROWS ONLY", true),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) IS NOT NULL ORDER BY 1", true),

            // Text a whole number past 2^53 renders as, whose weakening sent the double it rounds to beside the string:
            // the service keeps the integer exactly, the double is another number there, and the documents storing
            // 2^53 + 1 and 2^60 + 1 were lost (the review's probes, run on the emulator).
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v') = '9007199254740993'", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v') = '1152921504606846977'", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v') IN ('9007199254740993', '1152921504606846977')", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) = 9007199254740993 AND JSON_VALUE(c.\"DOC\", '$.v') = '9007199254740993'", false),
            ("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.v') = '9007199254740992'", false),
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
        /// the BMP, witnessed in a sort, a comparison and an aggregate: over <c>id</c>, over a path
        /// declared a string, and over one a filter proves present, which lets the service take the
        /// sort under Calcite's own placement.
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
            ("SELECT JSON_VALUE(c.\"DOC\", '$.s') FROM ordering AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') IS NOT NULL ORDER BY 1", true,
                "The same sort under Calcite's default placement, which a filter proving the key present lets the service take: the clauses after a filter read the rows it kept. As the id above."),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.s') FROM ordering AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') IS NOT NULL ORDER BY 1 DESC", true,
                "The same sort descending. As the id above."),
            ("SELECT c.\"id\" FROM ordering AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') IS NOT NULL ORDER BY JSON_VALUE(c.\"DOC\", '$.s')", true,
                "The same sort by a key the statement does not select. As the id above."),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.s') FROM ordering AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') IS NOT NULL ORDER BY 1 FETCH NEXT 2 ROWS ONLY", false,
                "The same sort paged, where the order chooses the rows: a and U+1F600 in process, a and U+E000 at the service. As the id above."),
            ("SELECT JSON_VALUE(c.\"DOC\", '$.u') FROM ordering AS c WHERE JSON_VALUE(c.\"DOC\", '$.u') > 'a' ORDER BY 1", true,
                "The same sort over a key a comparison proves present; the comparison itself agrees, its literal lying below the surrogates. As the id above."),
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
            // A typed accessor over every document of the typed container, whose label holds a
            // string, a boolean, an array and an object as well as 30, null and nothing. Calcite
            // throws on the string and the boolean -- the RETURNING assertion, ikvmnet/calcite-dotnet#120
            // -- so there is no oracle, and answers null for the array and the object, which the
            // corpus compares. The column is read through IS_NUMBER, null for every value that is not
            // a number: SQL:2016's answer under the default NULL ON ERROR, and the one the comparison
            // through the same clause already gives (WithoutAnOracle). A deliberate divergence from
            // the engine, stated so that it is seen.
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label' RETURNING INTEGER) FROM typed AS c",
                ["(\"1\", null)", "(\"2\", 30)", "(\"3\", null)", "(\"4\", null)", "(\"5\", null)", "(\"6\", null)", "(\"7\", null)", "(\"8\", null)", "(\"9\", null)"]),

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
        /// <para>
        /// Weaker than the corpus by a long way, and the strongest thing available while the oracle
        /// cannot be built for these — see <see cref="WithoutAnOracle"/>. It still catches a pushdown
        /// that stops running at all.
        /// </para>
        /// <para>
        /// Stricter than <see cref="Refines"/>, which accepts any outcome here, a failure included,
        /// since Calcite raises — so requiring an answer is this list's own standard. And the oracle
        /// is run too, because its raising is the only thing that licenses the list: a statement whose
        /// oracle has started answering belongs in the corpus, which is how a repaired engine is
        /// noticed, as a closed divergence is.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task EveryStatementWithoutAnOracleStillRuns()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, reason) in WithoutAnOracle)
            {
                var oracle = await Attempt(sql, pushdown: false);
                var pushed = await Attempt(sql, pushdown: true);

                if (oracle.Raised is null)
                    failures.Add($"{sql}\n  no oracle because: {reason}\n  but the oracle answered, so it belongs in the corpus: [{string.Join("; ", oracle.Rows!)}]");
                else if (pushed.Raised is Exception e)
                    failures.Add($"{sql}\n  no oracle because: {reason}\n  and the pushdown failed to run: {e.Message}");
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
