using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Facts;

using Apache.Calcite.Extensions.Adapter.Cursor;

using FluentAssertions;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.type;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// The inventory of sorts, kept as the mirror of the one question: thousands of statements that order, each planned,
    /// and what each chosen plan asks the service to order read off it and judged by an account of what proves an order
    /// that is written here, not asked of <see cref="CosmosServiceOrder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a mirror.</b> The plan for allowable orders was made from this inventory: every statement of the existing
    /// sets and a cross product of focused probes, planned, and every order the chosen plan pushed sorted into a group by
    /// what would prove it — a text key's characters, a <c>BIGINT</c>'s bounds, a rank's direction, an extreme's argument,
    /// a comparison's sides. On main, 598 pushed statements fell in groups nothing proves. The question decides on the
    /// same facts by its own rules; this decides by the inventory's, and they have to agree on the one thing that matters:
    /// <b>nothing pushed falls in a group nothing proves.</b> A planner change that pushes an unproved order somewhere the
    /// question is not asked — a site that does not call it, a rule that moves a key past it — shows here and nowhere
    /// else.
    /// </para>
    /// <para>
    /// <b>And the transpose (P6).</b> Calcite moves a sort beneath a projection that casts its key, keyed by the cast's
    /// operand, wherever it calls the cast monotonic, and it calls a cast of text to an instant or a UUID that. The text
    /// orders as the converted value only in a stored form that keeps the order; a pushed sort found beneath such a cast
    /// over a path with no such form is a wrong order, whatever any rule said.
    /// </para>
    /// <para>
    /// <b>And what the rows complete.</b> A group nothing proves when the plan is made may still be one the reader proves
    /// as the rows arrive: text of unknown characters, checked for a unit at or above U+D800; a <c>BIGINT</c> past int64,
    /// checked for a number past 2^53; a comparison with a parameter, checked against the value. Such an order counts
    /// here only where the statement the plan renders carries the program that checks it, with the check its group
    /// needs (<see cref="CosmosOrderProgram"/>); the program's own soundness is the model's and the reader's tests'.
    /// </para>
    /// <para>
    /// <b>What is not judged here.</b> Null placement, which the inventory read off the old rule's checks rather than the
    /// facts, and the composite index, which is the service's acceptance and not an order. Both stay with the planner
    /// tests and the <c>ORDER BY</c> yardstick on the emulator.
    /// </para>
    /// <para>
    /// <b>Sizes.</b> The probes run by default, a cross product over the differential suite's containers; the planner
    /// corpus and the ranks through <see cref="PlannerHarness"/> beside them.
    /// </para>
    /// </remarks>
    public class CosmosSortInventoryTests
    {

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

        static CosmosContainerMetadata Declaring(string name, string partitionKey, string schema, IEnumerable<CosmosCompositeIndex>? indexes = null) =>
            new CosmosContainerMetadata(name, new[] { partitionKey }, indexes).WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(schema)!));

        static CosmosCompositeIndex Index(params (string Path, bool Descending)[] paths) => new(paths.Select(p => new CosmosCompositeIndexPath(p.Path, p.Descending)));

        /// <summary>
        /// The differential suite's containers, as it declares them, and <c>ordering</c> again with composite indexes, which
        /// the emulator ignores and the planner reads.
        /// </summary>
        static readonly CosmosContainerMetadata[] Containers =
        [
            new("products", new[] { "/category" }),
            new("typed", new[] { "/category" }),
            Declaring("ordering", "/category", CosmosPushdownProbeTests.OrderingSchema),
            Declaring("declared", "/category", """{ "type": "object", "properties": { "body": { "type": "string" }, "n": { "type": "integer" }, "v": { "type": "integer" } } }"""),
            Declaring("kinds", "/kind", CosmosPushdownProbeTests.KindsSchema),
            Declaring("ordering_ci", "/category", CosmosPushdownProbeTests.OrderingSchema, new[]
            {
                Index(("/s", false), ("/id", false)),
                Index(("/id", false), ("/s", false)),
                Index(("/s", false), ("/u", false)),
                Index(("/k", false), ("/s", false)),
                Index(("/t", false), ("/id", false)),
                Index(("/id", false), ("/_ts", false)),
                Index(("/b", false), ("/k", false)),
            }),
        ];

        // ── The statements ─────────────────────────────────────────────────────────────────────

        static string Jv(string path, string? returning = null) => $"JSON_VALUE(c.\"DOC\", '$.{path}'{(returning is null ? "" : " RETURNING " + returning)})";

        /// <summary>Every fixture path of every container, with the type its declaration gives it, if any.</summary>
        static readonly (string Container, (string Path, string? Declared)[] Paths)[] Paths =
        [
            ("products", [("name", null), ("price", null), ("tags", null), ("category", null)]),
            ("typed", [("name", null), ("price", null), ("label", null), ("big", null)]),
            ("ordering", [("t", "string"), ("s", "string"), ("u", "string"), ("k", "integer"), ("b", "boolean"), ("f", null)]),
            ("declared", [("body", "string"), ("n", "integer"), ("v", "integer")]),
            ("kinds", [("kind", "string"), ("n", "integer"), ("label", null), ("ref", null)]),
        ];

        static readonly (string Name, Func<string, string> Form)[] Forms =
        [
            ("text", p => Jv(p)),
            ("varchar", p => Jv(p, "VARCHAR")),
            ("integer", p => Jv(p, "INTEGER")),
            ("bigint", p => Jv(p, "BIGINT")),
            ("double", p => Jv(p, "DOUBLE")),
            ("boolean", p => Jv(p, "BOOLEAN")),
            ("query", p => $"JSON_QUERY(c.\"DOC\", '$.{p}')"),
        ];

        static readonly string[] Directions = ["ASC", "DESC"];
        static readonly string[] Placements = ["", " NULLS FIRST", " NULLS LAST"];
        static readonly string[] Pages = ["", " FETCH NEXT 3 ROWS ONLY", " OFFSET 1 ROWS FETCH NEXT 3 ROWS ONLY"];

        /// <summary>
        /// The probes: the inventory's cross product, the groups whose pushes the plan's groups were counted over.
        /// </summary>
        static IEnumerable<(string Group, string Sql)> Probes()
        {
            // One key, selected: every reading, direction, placement and page.
            foreach (var (container, paths) in Paths)
                foreach (var (path, _) in paths)
                    foreach (var (_, form) in Forms)
                        foreach (var direction in Directions)
                            foreach (var placement in Placements)
                                foreach (var page in Pages)
                                    yield return ("key", $"SELECT c.\"id\", {form(path)} AS \"k\" FROM {container} AS c ORDER BY 2 {direction}{placement}{page}");

            // The promoted columns: id, _ts, _etag, the partition key, the document.
            foreach (var (container, _) in Paths)
                foreach (var column in new[] { "c.\"id\"", "c.\"_ts\"", "c.\"_etag\"", container == "kinds" ? "c.\"$.kind\"" : "c.\"$.category\"", "c.\"DOC\"" })
                    foreach (var direction in Directions)
                        foreach (var placement in Placements)
                            foreach (var page in Pages)
                                yield return ("column", $"SELECT c.\"id\" FROM {container} AS c ORDER BY {column} {direction}{placement}{page}");

            // Under a filter on the key, which may give it a domain or a presence.
            foreach (var (container, paths) in Paths)
                foreach (var (path, declared) in paths)
                    foreach (var reading in declared switch { "integer" => new[] { "text", "integer", "bigint" }, "boolean" => new[] { "text", "boolean" }, _ => new[] { "text", "integer" } })
                    {
                        var key = Forms.Single(f => f.Name == reading).Form(path);
                        var (equal, other, above) = reading switch
                        {
                            "text" => ("'a'", "'Trail'", "'m'"),
                            "boolean" => ("true", "false", (string?)null),
                            _ => ("3", "10", "0"),
                        };

                        var filters = new List<string> { $"{key} IS NOT NULL", $"{key} = {equal}", $"{key} IN ({equal}, {other})" };
                        if (above is not null)
                            filters.Add($"{key} > {above}");

                        foreach (var where in filters)
                            foreach (var direction in Directions)
                                foreach (var page in new[] { "", " FETCH NEXT 3 ROWS ONLY" })
                                    yield return ("filtered", $"SELECT c.\"id\", {key} AS \"k\" FROM {container} AS c WHERE {where} ORDER BY 2 {direction}{page}");
                    }

            // Expressions, casts, conversions and UUIDs, which reach the sort through a projection or the transpose.
            (string, Func<string, string>)[] expressions =
            [
                ("upper", p => $"UPPER({Jv(p)})"),
                ("coalesce", p => $"COALESCE({Jv(p)}, '')"),
                ("cast-varchar", p => $"CAST({Jv(p)} AS VARCHAR(20))"),
                ("cast-double", p => $"CAST({Jv(p, "INTEGER")} AS DOUBLE)"),
                ("plus", p => $"{Jv(p, "INTEGER")} + 1"),
                ("cast-uuid", p => $"CAST({Jv(p)} AS UUID)"),
                ("cast-ts", p => $"CAST({Jv(p)} AS TIMESTAMP)"),
            ];
            foreach (var (container, path) in new[] { ("ordering", "s"), ("ordering", "k"), ("declared", "body"), ("kinds", "ref"), ("kinds", "label"), ("products", "name") })
                foreach (var (_, expression) in expressions)
                    foreach (var placement in new[] { "", " NULLS FIRST" })
                        foreach (var page in new[] { "", " FETCH NEXT 3 ROWS ONLY" })
                            yield return ("expression", $"SELECT c.\"id\", {expression(path)} AS \"k\" FROM {container} AS c ORDER BY 2{placement}{page}");

            foreach (var where in new[] { "", $" WHERE {Jv("kind")} = 'a'", $" WHERE {Jv("kind")} = 'c'", $" WHERE {Jv("ref")} IS NOT NULL" })
                foreach (var direction in Directions)
                    foreach (var page in new[] { "", " FETCH NEXT 2 ROWS ONLY" })
                    {
                        yield return ("uuid", $"SELECT c.\"id\", CAST({Jv("ref")} AS UUID) AS \"r\" FROM kinds AS c{where} ORDER BY 2 {direction}{page}");
                        yield return ("uuid", $"SELECT c.\"id\" FROM kinds AS c{where} ORDER BY CAST({Jv("ref")} AS UUID) {direction} NULLS FIRST{page}");
                    }

            // Two keys, with and without composite indexes, null one way or two.
            (string, string)[] two =
            [
                ("c.\"id\"", Jv("s")),
                (Jv("s"), "c.\"id\""),
                (Jv("s"), Jv("u")),
                (Jv("k", "INTEGER"), Jv("s")),
                (Jv("t"), "c.\"id\""),
                ("c.\"id\"", "c.\"_ts\""),
                (Jv("b", "BOOLEAN"), Jv("k", "INTEGER")),
            ];
            foreach (var container in new[] { "ordering", "ordering_ci" })
                foreach (var (a, b) in two)
                    foreach (var (da, db) in new[] { ("ASC", "ASC"), ("DESC", "DESC"), ("ASC", "DESC") })
                        foreach (var placement in new[] { "", " NULLS FIRST" })
                            foreach (var where in new[] { "", $" WHERE {a} IS NOT NULL AND {b} IS NOT NULL" })
                                foreach (var page in new[] { "", " FETCH NEXT 2 ROWS ONLY" })
                                {
                                    var na = placement == "" ? "" : da == "ASC" ? " NULLS FIRST" : " NULLS LAST";
                                    var nb = placement == "" ? "" : db == "ASC" ? " NULLS FIRST" : " NULLS LAST";
                                    yield return ("two keys", $"SELECT c.\"id\" FROM {container} AS c{where} ORDER BY {a} {da}{na}, {b} {db}{nb}{page}");
                                }

            // A key the filter pins, beside a second key.
            foreach (var container in new[] { "ordering", "ordering_ci", "kinds" })
                foreach (var (first, where) in new[] { (Jv("s"), $"{Jv("s")} = 'a'"), (Jv("kind"), $"{Jv("kind")} = 'a'"), ("c.\"id\"", "c.\"id\" = 'a'") })
                    foreach (var second in new[] { "c.\"_ts\" DESC", "c.\"id\"", Jv("k", "INTEGER") + " NULLS FIRST" })
                        foreach (var page in new[] { "", " FETCH NEXT 2 ROWS ONLY" })
                            yield return ("pinned", $"SELECT c.\"id\" FROM {container} AS c WHERE {where} ORDER BY {first}, {second}{page}");

            // Keysets: a seek and a sort, against literals and a parameter. A literal at or above U+D800 the default
            // charset refuses before anything is planned (U&'\E000' does not validate); a parameter may be bound to
            // anything.
            foreach (var container in new[] { "ordering", "products", "kinds" })
                foreach (var bound in new[] { "'a'", "'5'", "?" })
                    foreach (var (direction, op) in new[] { ("ASC", ">"), ("DESC", "<") })
                        yield return ("keyset", $"SELECT c.\"id\" FROM {container} AS c WHERE c.\"id\" {op} {bound} ORDER BY c.\"id\" {direction} FETCH NEXT 3 ROWS ONLY");
            foreach (var (key, container) in new[] { (Jv("s"), "ordering"), (Jv("t"), "ordering"), (Jv("label"), "kinds"), (Jv("body"), "declared") })
                foreach (var bound in new[] { "'a'", "?" })
                {
                    yield return ("keyset", $"SELECT c.\"id\", {key} FROM {container} AS c WHERE {key} > {bound} ORDER BY 2 FETCH NEXT 3 ROWS ONLY");
                    yield return ("keyset", $"SELECT c.\"id\", {key} FROM {container} AS c WHERE {key} > {bound} OR ({key} = {bound} AND c.\"id\" > 'a') ORDER BY 2, 1 FETCH NEXT 3 ROWS ONLY");
                }
            foreach (var bound in new[] { "3", "?" })
            {
                yield return ("keyset", $"SELECT c.\"id\" FROM ordering AS c WHERE {Jv("k", "INTEGER")} > {bound} ORDER BY {Jv("k", "INTEGER")} FETCH NEXT 3 ROWS ONLY");
                yield return ("keyset", $"SELECT c.\"id\" FROM declared AS c WHERE {Jv("v", "BIGINT")} > {bound} ORDER BY {Jv("v", "BIGINT")} FETCH NEXT 3 ROWS ONLY");
            }

            // MIN and MAX.
            foreach (var (container, _) in Paths)
                foreach (var aggregate in new[] { "MIN", "MAX" })
                {
                    foreach (var argument in new[] { "c.\"id\"", "c.\"_ts\"", "c.\"_etag\"", Jv("name"), Jv("s"), Jv("k", "INTEGER"), Jv("v", "BIGINT"), Jv("n", "INTEGER") })
                        yield return ("extreme", $"SELECT {aggregate}({argument}) FROM {container} AS c");

                    yield return ("extreme", $"SELECT {aggregate}({Jv("s")}) FROM {container} AS c WHERE {Jv("s")} IS NOT NULL");
                    yield return ("extreme", $"SELECT {aggregate}(c.\"id\") FROM {container} AS c WHERE c.\"id\" IN ('a', 'b')");
                }

            // Ordering comparisons: path against path, and against literals and a parameter.
            foreach (var (container, a, b) in new[] { ("ordering", "s", "u"), ("ordering", "t", "s"), ("declared", "body", "body"), ("kinds", "label", "ref"), ("typed", "name", "label") })
            {
                foreach (var op in new[] { "<", ">=" })
                    yield return ("comparison", $"SELECT c.\"id\" FROM {container} AS c WHERE {Jv(a)} {op} {Jv(b)}");

                foreach (var literal in new[] { "'a'", "'m'", "?" })
                    yield return ("comparison", $"SELECT c.\"id\" FROM {container} AS c WHERE {Jv(a)} > {literal}");

                yield return ("comparison", $"SELECT c.\"id\" FROM {container} AS c WHERE c.\"id\" BETWEEN 'a' AND 'z'");
                yield return ("comparison", $"SELECT c.\"id\" FROM {container} AS c WHERE c.\"id\" < ?");
                yield return ("comparison", $"SELECT c.\"id\" FROM {container} AS c WHERE c.\"id\" < c.\"_etag\"");
            }

            // Unions the engine may merge-sort, subqueries and views.
            foreach (var (key, first, second) in new[] { (Jv("s"), $"{Jv("s")} IS NOT NULL", $"{Jv("s")} = 'a'"), ("c.\"id\"", "c.\"id\" > 'a'", "c.\"id\" < 'a'"), (Jv("k", "INTEGER"), $"{Jv("k", "INTEGER")} > 0", $"{Jv("k", "INTEGER")} < 0") })
                foreach (var page in new[] { "", " FETCH NEXT 2 ROWS ONLY" })
                    yield return ("union", $"SELECT {key} AS \"k\" FROM ordering AS c WHERE {first} UNION ALL SELECT {key} AS \"k\" FROM ordering AS c WHERE {second} ORDER BY 1{page}");

            yield return ("subquery", $"SELECT * FROM (SELECT c.\"id\", {Jv("s")} AS \"s\" FROM ordering AS c ORDER BY 2 NULLS FIRST FETCH NEXT 3 ROWS ONLY) AS z ORDER BY z.\"id\"");
            yield return ("subquery", $"SELECT * FROM (SELECT c.\"id\" FROM ordering AS c ORDER BY c.\"id\" FETCH NEXT 3 ROWS ONLY) AS z ORDER BY z.\"id\" DESC");
            yield return ("subquery", $"WITH v AS (SELECT c.\"id\" AS \"i\", {Jv("s")} AS \"s\" FROM ordering AS c WHERE {Jv("s")} IS NOT NULL) SELECT v.\"s\" FROM v ORDER BY v.\"s\" FETCH NEXT 2 ROWS ONLY");
            yield return ("subquery", $"SELECT * FROM (SELECT c.\"id\" AS \"i\", CAST({Jv("ref")} AS UUID) AS \"x\" FROM kinds AS c WHERE {Jv("kind")} = 'a') AS z ORDER BY z.\"x\"");
            yield return ("subquery", $"SELECT * FROM (SELECT c.\"id\" AS \"i\", {Jv("label")} AS \"x\" FROM kinds AS c WHERE {Jv("kind")} = 'a') AS z ORDER BY z.\"x\" FETCH NEXT 2 ROWS ONLY");
        }

        /// <summary>
        /// The statements planned through <see cref="PlannerHarness"/>, whose operator table has the scoring functions: the
        /// planner corpus, and every direction a rank may be asked in.
        /// </summary>
        static IEnumerable<(string Group, string Sql)> Harnessed()
        {
            foreach (var query in PlannerQueries.All)
                yield return ("corpus", query.Sql);

            foreach (var direction in new[] { "", " ASC", " DESC", " DESC NULLS LAST", " NULLS FIRST" })
                foreach (var page in new[] { "", " FETCH FIRST 10 ROWS ONLY" })
                {
                    yield return ("rank", $"SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel'){direction}{page}");
                    yield return ("rank", $"SELECT c.\"id\" FROM products AS c ORDER BY RRF(FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel'), FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.tags'), 'frame')){direction}{page}");
                    yield return ("rank", $"SELECT c.\"id\" FROM products AS c ORDER BY VECTORDISTANCE(JSON_QUERY(c.\"DOC\", '$.embedding'), ARRAY[0.1, 0.2]){direction}{page}");
                }

            foreach (var column in new[] { "c.\"id\"", "c.\"_ts\"", "JSON_VALUE(c.\"DOC\", '$.name')", "c.\"_etag\"" })
                yield return ("index", $"SELECT c.\"id\" FROM archive AS c ORDER BY {column} NULLS FIRST FETCH NEXT 5 ROWS ONLY");

            yield return ("index", "SELECT c.\"id\" FROM products AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.payload.x') NULLS FIRST FETCH NEXT 5 ROWS ONLY");
        }

        // ── Planning ───────────────────────────────────────────────────────────────────────────

        static RelNode PlanOver(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var tables = Containers.Select(container => new CosmosTable(container)).ToArray();

            var rootSchema = CalciteSchema.createRootSchema(false);
            foreach (var table in tables)
                rootSchema.add(table.Container.Name, table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);
            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var table in tables)
                foreach (var rule in CosmosRules.GetRules(table.Convention))
                    planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify()));
            return planner.findBestExp();
        }

        // ── The account of what proves an order ────────────────────────────────────────────────

        /// <summary>
        /// Whether a string holds nothing at or above U+D800, where code point and UTF-16 order are one.
        /// </summary>
        static bool BelowSurrogates(string text) => text.All(c => c < '\uD800');

        /// <summary>
        /// What the facts say of the characters of a path's strings, read off its claims: a domain all below U+D800, a
        /// stored form (every one ASCII), or the theory's statement of the bands its strings may hold — one band left
        /// out for an order among the path's own strings, both against a value that may hold anything. Read here, as a
        /// classifier of its own, rather than through the fact the question reads.
        /// </summary>
        static bool CharactersKnown(JsonFactSet facts, JsonDocumentPath path, bool againstAnything = false)
        {
            var claims = facts.ClaimsFor(path);

            if (claims.Any(c => c is JsonClaim.Represents))
                return true;

            foreach (var claim in claims)
            {
                if (claim is JsonClaim.EqualTo equal)
                    return equal.Value.ToClrValue() is not string one || BelowSurrogates(one);

                if (claim is JsonClaim.OneOf domain)
                    return domain.Values.All(v => v.ToClrValue() is not string s || BelowSurrogates(s));
            }

            var held = claims.OfType<JsonClaim.Characters>().Select(c => c.Bands).DefaultIfEmpty(JsonCharacterBand.Any).First();
            return againstAnything ? held == JsonCharacterBand.None : held != JsonCharacterBand.Any;
        }

        /// <summary>
        /// Whether every number at a path is an int64, by its bounds or its domain.
        /// </summary>
        static bool WithinInt64(JsonFactSet facts, JsonDocumentPath path)
        {
            var claims = facts.ClaimsFor(path);

            if (claims.OfType<JsonClaim.OneOf>().Any() || claims.OfType<JsonClaim.EqualTo>().Any())
                return true;

            var lower = claims.OfType<JsonClaim.LowerBound>().Any(l => l.Value >= -9223372036854775808m);
            var upper = claims.OfType<JsonClaim.UpperBound>().Any(u => u.Value <= 9223372036854775807m);
            return lower && upper || claims.OfType<JsonClaim.OfType>().Any(t => t.Type is not (JsonType.Integer or JsonType.Number));
        }

        /// <summary>
        /// Whether a path may be absent and also null in a row: the two nulls the service orders apart and Calcite ties.
        /// </summary>
        static bool NullTwoWays(JsonFactSet facts, JsonDocumentPath path, SqlTypeName type)
        {
            var claims = facts.ClaimsFor(path);
            if (claims.OfType<JsonClaim.Present>().Any() || claims.OfType<JsonClaim.EqualTo>().Any())
                return false;

            // Absent, then, and null wherever no type claim rules a null out: by a JSON null, or by a type the reading
            // reads null for.
            return claims.OfType<JsonClaim.OfType>().FirstOrDefault() is not { } typed || typed.OrNull || typed.Type == JsonType.Null
                || (SqlTypeName.CHAR_TYPES.contains(type) ? typed.Type is not JsonType.String : SqlTypeName.BOOLEAN == type ? typed.Type is not JsonType.Boolean : typed.Type is not (JsonType.Integer or JsonType.Number));
        }

        /// <summary>
        /// A group of what the service is asked to order, and whether anything proves it.
        /// </summary>
        sealed record Pushed(string Family, bool Proved);

        /// <summary>
        /// Reads off a chosen plan every order the service is asked for, and the group each falls in.
        /// </summary>
        static List<Pushed> Inventory(RelNode best)
        {
            var found = new List<Pushed>();
            var nodes = All(best).ToList();

            foreach (var sort in nodes.OfType<CosmosSort>())
                Keys(sort, found);

            foreach (var rank in nodes.OfType<CosmosRank>())
                found.Add(new("rank", true));

            foreach (var aggregate in nodes.OfType<CosmosAggregate>())
                for (var i = 0; i < aggregate.getAggCallList().size(); i++)
                    if ((AggregateCall)aggregate.getAggCallList().get(i) is var call
                        && (call.getAggregation().getKind() == org.apache.calcite.sql.SqlKind.MIN || call.getAggregation().getKind() == org.apache.calcite.sql.SqlKind.MAX))
                        found.Add(Extreme(aggregate, ((java.lang.Integer)call.getArgList().get(0)).intValue(), (string)aggregate.getRowType().getFieldNames().get(aggregate.getGroupCount() + i)));

            foreach (var filter in nodes.OfType<CosmosFilter>())
                Comparisons(filter, filter.getCondition(), found);

            foreach (var project in nodes.OfType<Project>().Where(p => p is not CosmosRel))
                Transposed(project, found);

            return found;
        }

        static void Keys(CosmosSort sort, List<Pushed> found)
        {
            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out var readings, out var candidates, out _, out var container) == false || container is null)
                return;

            var facts = sort.Facts ?? CosmosOracle.For(container.Facts).Declared;
            var collations = sort.getCollation().getFieldCollations();
            var (written, program) = Written(sort);
            var keys = new List<(RelFieldCollation Field, int Index)>();

            for (var i = 0; i < collations.size(); i++)
            {
                var field = (RelFieldCollation)collations.get(i);
                var index = field.getFieldIndex();
                var path = index < fields.Count ? fields[index] ?? (index < candidates.Count ? candidates[index].Path : null) : null;

                // A key the statement does not write orders nothing at the service.
                if (path is not null && written.Contains(path.ToString()) == false)
                    continue;

                keys.Add((field, index));
            }

            for (var k = 0; k < keys.Count; k++)
            {
                var (field, index) = keys[k];
                var type = ((org.apache.calcite.rel.type.RelDataTypeField)sort.getInput().getRowType().getFieldList().get(index)).getType().getSqlTypeName();
                var path = index < fields.Count ? fields[index] : null;
                var paged = sort.fetch is not null || sort.offset is not null;

                if (path is null)
                {
                    if (index < candidates.Count && candidates[index].Path is Adapter.Sql.CosmosPath converted && CosmosDocumentPaths.From(converted) is JsonDocumentPath document)
                        found.Add(new("sort: a conversion over a stored form", facts.RepresentationOf(document) is { PreservesOrder: true }));
                    else
                        found.Add(new("sort: a geodesic distance (the service's value)", true));

                    continue;
                }

                if (CosmosDocumentPaths.From(path) is not JsonDocumentPath at)
                {
                    found.Add(new("sort: the document", false));
                    continue;
                }

                var name = path.ToString();
                var reading = index < readings.Count ? readings[index] : CosmosReading.Typed;
                string family;
                bool proved;

                if (name == "c._etag")
                    (family, proved) = ("sort: _etag, which the default policy excludes", false);
                else if (reading is CosmosReading.Json or CosmosReading.JsonText || type == SqlTypeName.VARIANT)
                    (family, proved) = ("sort: a JSON value or a VARIANT", false);
                else if (SqlTypeName.CHAR_TYPES.contains(type) || reading == CosmosReading.Text)
                    (family, proved) = CharactersKnown(facts, at)
                        ? ("sort: text whose characters are known", true)
                        : (name == "c.id" ? "sort: id, of unknown characters" : "sort: text of unknown characters", false);
                else if (type == SqlTypeName.BIGINT || type == SqlTypeName.DECIMAL)
                    (family, proved) = WithinInt64(facts, at) ? ("sort: a BIGINT within int64", true) : ("sort: a BIGINT past int64", false);
                else
                    (family, proved) = ("sort: " + type.getName(), true);

                // What the plan did not prove, the reader may as the rows arrive: text by its characters, a BIGINT by its
                // magnitude, each value of the key checked as the statement's program says.
                var checks = program is { Keys.Count: > 0 } && k < program.Keys.Count ? program.Keys[k].Anchor : CosmosOrderAnchor.None;
                if (proved == false && (family.Contains("unknown characters") && checks == CosmosOrderAnchor.Text || family.Contains("past int64") && checks == CosmosOrderAnchor.Number))
                    (family, proved) = (family + ", checked as the rows arrive", true);

                // Before the last key, a null two ways is two tie classes at the service and one in Calcite.
                if (proved && k < keys.Count - 1 && NullTwoWays(facts, at, type))
                    (family, proved) = ("sort: a leading key null two ways", false);

                found.Add(new(family + (paged ? ", paged" : ""), proved));
            }
        }

        /// <summary>
        /// The statement a pushed subtree renders, as the converter renders it, its projection forced, with the program
        /// its reader runs.
        /// </summary>
        static CosmosQuery? Statement(RelNode rel)
        {
            try
            {
                return Adapter.Rel.Convert.CosmosConverters.GenerateQuery(rel, rel.getCluster().getRexBuilder()).Query;
            }
            catch (Adapter.Sql.CosmosTranslationException)
            {
                return null;
            }
        }

        /// <summary>
        /// The paths the statement a sort renders writes in its <c>ORDER BY</c>, and the program its reader runs.
        /// </summary>
        static (HashSet<string> Written, CosmosOrderProgram? Program) Written(CosmosSort sort)
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            var query = Statement(sort);

            if (query is not CosmosQuery statement)
                return (written, null);

            {
                var sql = statement.Sql;
                var at = sql.IndexOf(" ORDER BY ", StringComparison.Ordinal);
                if (at < 0)
                    return (written, statement.Order);

                var clause = sql[(at + 10)..];
                foreach (var stop in new[] { " OFFSET ", " LIMIT " })
                    if (clause.IndexOf(stop, StringComparison.Ordinal) is var cut and >= 0)
                        clause = clause[..cut];

                foreach (var item in clause.Split(", "))
                    written.Add(item.Replace(" ASC", "").Replace(" DESC", "").Trim());
            }

            return (written, statement.Order);
        }

        static Pushed Extreme(CosmosAggregate aggregate, int argument, string member)
        {
            if (CosmosImplementor.TryBindOutput(aggregate.getInput(), out var fields, out _, out _, out _, out var container) == false || container is null
                || argument >= fields.Count || fields[argument] is not Adapter.Sql.CosmosPath path || CosmosDocumentPaths.From(path) is not JsonDocumentPath at)
                return new("extreme: of no path", false);

            var facts = aggregate.Facts ?? CosmosOracle.For(container.Facts).Declared;
            var type = ((org.apache.calcite.rel.type.RelDataTypeField)aggregate.getInput().getRowType().getFieldList().get(argument)).getType().getSqlTypeName();

            // What the plan did not prove, the reader may: the answer checked, and read again where it is no anchor.
            var checks = Statement(aggregate)?.Order?.Extremes.FirstOrDefault(e => e.Member == member)?.Anchor ?? CosmosOrderAnchor.None;

            if (SqlTypeName.CHAR_TYPES.contains(type))
                return CharactersKnown(facts, at) ? new("extreme: text whose characters are known", true)
                    : checks == CosmosOrderAnchor.Text ? new("extreme: text of unknown characters, checked as the row arrives", true)
                    : new("extreme: text of unknown characters", false);

            if (type == SqlTypeName.BIGINT || type == SqlTypeName.DECIMAL)
                return WithinInt64(facts, at) ? new("extreme: a BIGINT within int64", true)
                    : checks == CosmosOrderAnchor.Number ? new("extreme: a BIGINT past int64, checked as the row arrives", true)
                    : new("extreme: a BIGINT past int64", false);

            return new("extreme: " + type.getName(), true);
        }

        /// <summary>
        /// Every ordering comparison of text a pushed filter makes, and whether its two sides order alike.
        /// </summary>
        static void Comparisons(CosmosFilter filter, RexNode node, List<Pushed> found)
        {
            if (node is not RexCall call)
                return;

            var kind = call.getKind();
            if (kind == org.apache.calcite.sql.SqlKind.LESS_THAN || kind == org.apache.calcite.sql.SqlKind.LESS_THAN_OR_EQUAL
                || kind == org.apache.calcite.sql.SqlKind.GREATER_THAN || kind == org.apache.calcite.sql.SqlKind.GREATER_THAN_OR_EQUAL)
            {
                var left = (RexNode)call.getOperands().get(0);
                var right = (RexNode)call.getOperands().get(1);

                if (IsText(left) || IsText(right))
                {
                    var (a, b) = (Side(filter, left), Side(filter, right));
                    found.Add(Agree(a, b) ? new("comparison: text whose characters agree", true)
                        : (left is RexDynamicParam || right is RexDynamicParam) && Statement(filter)?.Order?.Comparisons.Count > 0
                            ? new("comparison: text against a parameter, checked against its value", true)
                            : new("comparison: text against text the two orders part over", false));
                }
            }

            foreach (RexNode operand in call.getOperands().toArray())
                Comparisons(filter, operand, found);
        }

        /// <summary>
        /// Whether a side of a comparison is text. The raw value a weakening compares is typed as a document value, and
        /// such a comparison is of text by its other side.
        /// </summary>
        static bool IsText(RexNode node) => SqlTypeName.CHAR_TYPES.contains(node.getType().getSqlTypeName());

        /// <summary>
        /// Whether two sides' strings order alike by code point and by UTF-16 code unit: unless one may hold a surrogate
        /// and the other a unit from U+E000. The bands of each side, as two flags.
        /// </summary>
        static bool Agree((bool Surrogates, bool High) a, (bool Surrogates, bool High) b) =>
            (a.Surrogates && b.High || a.High && b.Surrogates) == false;

        /// <summary>
        /// What characters one side of a comparison of text may hold: a literal its own, a parameter anything, a path what
        /// the rows the filter keeps say of it.
        /// </summary>
        static (bool Surrogates, bool High) Side(CosmosFilter filter, RexNode node)
        {
            while (node is RexCall cast && cast.getKind() == org.apache.calcite.sql.SqlKind.CAST && cast.getOperands().size() == 1 && cast.getOperands().get(0) is RexLiteral)
                node = (RexNode)cast.getOperands().get(0);

            if (node is RexLiteral literal)
                return SqlTypeName.CHAR_TYPES.contains(literal.getType().getSqlTypeName()) && literal.getValue() is org.apache.calcite.util.NlsString text
                    ? (text.getValue().Any(char.IsSurrogate), text.getValue().Any(c => c >= ''))
                    : (false, false);

            if (node is RexDynamicParam)
                return (true, true);

            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out _, out _, out _, out var container) == false || container is null)
                return (true, true);

            var translator = new Adapter.Sql.CosmosRexTranslator(filter.getCluster().getRexBuilder(), fields, new Adapter.Sql.CosmosParameterList(), JsonFactSet.Empty);
            if (translator.TryResolvePath(node, out var path) == false || CosmosDocumentPaths.From(path) is not JsonDocumentPath at)
                return (true, true);

            // What the rows the filter keeps satisfy: a sibling's domain among it, which is what the comparison may lean on.
            var facts = CosmosRowFacts.Of(filter, container, OracleLicence.OrderBy);
            if (CharactersKnown(facts, at, againstAnything: true))
                return (false, false);

            var held = facts.ClaimsFor(at).OfType<JsonClaim.Characters>().Select(c => c.Bands).DefaultIfEmpty(JsonCharacterBand.Any).First();
            return ((held & JsonCharacterBand.Surrogates) != JsonCharacterBand.None, (held & JsonCharacterBand.HighBmp) != JsonCharacterBand.None);
        }

        /// <summary>
        /// P6: a projection in process that casts a pushed sort's key to an instant or a UUID, over a path with no stored
        /// form that keeps the order — Calcite's transpose having keyed the sort by the text beneath the cast.
        /// </summary>
        static void Transposed(Project project, List<Pushed> found)
        {
            // Through the way out, and nothing else: a pushed projection above the sort would have rendered the cast.
            var below = project.getInput();
            if (below is org.apache.calcite.plan.volcano.RelSubset subset)
                below = subset.getBest() ?? subset.getOriginal();
            if (below?.getRelTypeName() == "CosmosToClrCursorConverter")
                below = below.getInput(0) is org.apache.calcite.plan.volcano.RelSubset inner ? inner.getBest() ?? inner.getOriginal() : below.getInput(0);

            if (below is not CosmosSort sort || CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out _, out _, out _, out var container) == false || container is null)
                return;

            var facts = sort.Facts ?? CosmosOracle.For(container.Facts).Declared;
            var keys = sort.getCollation().getFieldCollations();

            for (var i = 0; i < project.getProjects().size(); i++)
            {
                if ((RexNode)project.getProjects().get(i) is not RexCall cast || cast.getKind() != org.apache.calcite.sql.SqlKind.CAST
                    || cast.getOperands().get(0) is not RexInputRef operand)
                    continue;

                var target = cast.getType().getSqlTypeName();
                if (target != SqlTypeName.UUID && SqlTypeName.DATETIME_TYPES.contains(target) == false)
                    continue;

                var keyed = false;
                for (var k = 0; k < keys.size(); k++)
                    keyed |= ((RelFieldCollation)keys.get(k)).getFieldIndex() == operand.getIndex();

                if (keyed == false || operand.getIndex() >= fields.Count || fields[operand.getIndex()] is not Adapter.Sql.CosmosPath path || CosmosDocumentPaths.From(path) is not JsonDocumentPath at)
                    continue;

                found.Add(new("transpose: a sort keyed by text beneath a cast to " + target.getName(), facts.RepresentationOf(at) is { PreservesOrder: true }));
            }
        }

        static IEnumerable<RelNode> All(RelNode node)
        {
            yield return node;
            for (var i = 0; i < node.getInputs().size(); i++)
                foreach (var found in All((RelNode)node.getInputs().get(i)))
                    yield return found;
        }

        // ── The run ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every statement's groups, planned once for the class.
        /// </summary>
        static readonly Lazy<IReadOnlyList<(string Group, string Sql, IReadOnlyList<Pushed> Pushed, string? Failure)>> Run = new(() =>
        {
            var probes = Probes().ToList();
            var results = new (string, string, IReadOnlyList<Pushed>, string?)[probes.Count];

            Parallel.For(0, probes.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var (group, sql) = probes[i];
                try
                {
                    results[i] = (group, sql, Inventory(PlanOver(sql)), null);
                }
                catch (Exception e)
                {
                    results[i] = (group, sql, Array.Empty<Pushed>(), e.GetType().Name + ": " + e.Message.Split('\n')[0]);
                }
            });

            var harness = new PlannerHarness();
            var harnessed = Harnessed().Select(s =>
            {
                try
                {
                    return (s.Group, s.Sql, (IReadOnlyList<Pushed>)Inventory(harness.PlanToAsync(s.Sql)), (string?)null);
                }
                catch (Exception e)
                {
                    return (s.Group, s.Sql, Array.Empty<Pushed>(), e.GetType().Name + ": " + e.Message.Split('\n')[0]);
                }
            });

            return results.Concat(harnessed).ToList();
        });

        /// <summary>
        /// Nothing the service is asked to order falls in a group nothing proves: no text sort over characters unknown,
        /// no <c>BIGINT</c> past int64, no <c>_etag</c>, no leading key null two ways, no extreme over unknown text, and no
        /// comparison of text the two orders part over.
        /// </summary>
        [Fact]
        public void NothingPushedFallsInAGroupNothingProves()
        {
            var run = Run.Value;

            var unproved = run.SelectMany(r => r.Pushed.Where(p => p.Proved == false).Select(p => $"[{r.Group}] {p.Family}: {r.Sql}")).ToList();
            var counts = run.SelectMany(r => r.Pushed).GroupBy(p => (p.Family, p.Proved)).OrderBy(g => g.Key.Family, StringComparer.Ordinal)
                .Select(g => $"{(g.Key.Proved ? "proved" : "UNPROVED")} {g.Key.Family} {g.Count()}");
            var summary = $"{run.Count} statements, {run.Count(r => r.Pushed.Count > 0)} asking the service for an order, {run.Count(r => r.Failure is not null)} failing to plan:\n" + string.Join("\n", counts);

            TestContext.Current.TestOutputHelper?.WriteLine(summary);

            unproved.Should().BeEmpty(summary + "\n\n" + string.Join("\n", unproved.Take(40)));
        }

        /// <summary>
        /// The planner plans every statement, so that a group that is empty is empty because nothing pushed into it.
        /// </summary>
        [Fact]
        public void EveryStatementPlans()
        {
            var failures = Run.Value.Where(r => r.Failure is not null).Select(r => $"[{r.Group}] {r.Failure}: {r.Sql}").ToList();

            failures.Should().BeEmpty(string.Join("\n", failures.Take(20)));
        }

        /// <summary>
        /// The ranks: a descending full text score and fusion push, an ascending one and a vector distance do not.
        /// </summary>
        [Fact]
        public void OnlyADescendingScoreIsRanked()
        {
            var ranks = Run.Value.Where(r => r.Group == "rank").ToList();

            ranks.Where(r => r.Pushed.Any(p => p.Family == "rank")).Select(r => r.Sql)
                .Should().OnlyContain(sql => sql.Contains(" DESC", StringComparison.Ordinal) && sql.Contains("VECTORDISTANCE", StringComparison.Ordinal) == false);

            ranks.Count(r => r.Pushed.Any(p => p.Family == "rank")).Should().BeGreaterThan(0, "a descending score is ranked at the service");
        }

    }

}
