using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Extensions.Adapter.Cursor;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using org.apache.calcite;
using org.apache.calcite.adapter.java;
using org.apache.calcite.jdbc;
using org.apache.calcite.schema;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary>
        /// <c>kind</c> is <c>A</c> or <c>B</c>, <c>m</c> <c>"1"</c> or <c>"2"</c>, <c>body</c> a string and <c>tags</c> an
        /// array of strings, all required.
        /// </summary>
        internal const string Traversed = """{ "type": "object", "required": ["kind", "m", "body", "tags"], "properties": { "kind": { "enum": ["A", "B"] }, "m": { "enum": ["1", "2"] }, "body": { "type": "string" }, "tags": { "type": "array", "items": { "type": "string" } } } }""";

        /// <summary>What <see cref="Traversed"/> admits: an element <c>"x"</c> and a <c>body</c> of <c>"abc"</c>, which no cast to <c>INTEGER</c> reads.</summary>
        internal static readonly string[] TraversedDocuments =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","body":"abc","tags":["x","1"]}""",
            """{"id":"2","category":"a","kind":"B","m":"2","body":"7","tags":["2"]}""",
        ];

        /// <summary>
        /// A <c>WHERE</c> over a correlate, an <c>UNNEST</c> of <c>tags</c> or a lateral of <c>body</c>, one of whose conjuncts
        /// reads the document alone and another the correlate's right side beside it.
        /// </summary>
        internal static string BesideACorrelate(string name, string t)
        {
            var m = Json("c", "m");
            var unnest = $"""{t} AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)""";
            var lateral = $"""{t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "body")} AS b2 FROM (VALUES (1)) AS v(x0)) AS l""";
            return name switch
            {
                "u18a" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1)""",
                "u18ac" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1)""",
                "u18d" => $"""SELECT c."id", u.t FROM {unnest} WHERE {KindOf("c")} IS NOT NULL AND ({KindOf("c")} IS NOT NULL OR CAST(u.t AS INTEGER) = 1)""",
                "u18f" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NOT NULL AND NOT ({m} IS NULL AND CAST(u.t AS INTEGER) = 1)""",
                "u18b" => $"""SELECT c."id", u.t FROM {unnest} WHERE {KindOf("c")} IN ('A', 'B') AND ({KindOf("c")} IN ('A', 'B') OR CAST(u.t AS INTEGER) = 1)""",
                "l18a" => $"""SELECT c."id" FROM {lateral} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(l.b2 AS INTEGER) = 1)""",
                "l18ac" => $"""SELECT COUNT(*) AS q FROM {lateral} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(l.b2 AS INTEGER) = 1)""",
                _ => BesideACorrelateMore(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a conjunct of both sides the whole condition reads down to one side, beside
        /// a conjunct moved, and a whole read as false.
        /// </summary>
        static string BesideACorrelateMore(string name, string t)
        {
            var k = KindOf("c");
            var m = Json("c", "m");
            var cb = $"CAST({Json("c", "body")} AS INTEGER)";
            var flag = """JSON_VALUE(c."DOC", '$.flag' RETURNING BOOLEAN)""";
            var unnest = $"""{t} AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)""";
            var lateral = $"""{t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "body")} AS b2 FROM (VALUES (1)) AS v(x0)) AS l""";
            var some = $"""{t} AS c CROSS JOIN LATERAL (SELECT {k} AS k2 FROM (VALUES (1)) AS v(x0) WHERE {m} = '1') AS l""";
            var x3 = $"(({k} = 'B' AND u.t = 'x') OR {cb} = 7)";
            var z189 = $"((u.t = 'x' AND {cb} = 7) OR CAST(u.t AS INTEGER) = 1) AND u.t = '1' AND CASE WHEN {m} = '1' THEN u.t <> '1' ELSE NOT {flag} END";
            return name switch
            {
                "x3" => $"""SELECT c."id", u.t FROM {unnest} WHERE {k} = 'A' AND {x3}""",
                "x9" => $"""SELECT c."id", u.t FROM {unnest} WHERE u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} = 7)""",
                "x9b" => $"""SELECT c."id", u.t FROM {unnest} WHERE u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} > 0)""",
                "x8" => $"""SELECT c."id" FROM {some} WHERE {k} = 'A' AND (({k} = 'B' AND l.k2 = 'x') OR {cb} = 7)""",
                "x8c" => $"""SELECT COUNT(*) AS q FROM {some} WHERE {k} = 'A' AND (({k} = 'B' AND l.k2 = 'x') OR {cb} = 7)""",
                "mx1" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1) AND {k} = 'A' AND {x3}""",
                "mx1c" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1) AND {k} = 'A' AND {x3}""",
                "z189u" => $"""SELECT c."id", u.t FROM {unnest} WHERE {z189}""",
                "z189uc" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {z189}""",
                "w1" => $"""SELECT c."id", u.t FROM {unnest} WHERE CAST(u.t AS INTEGER) > 0 AND u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} = 7)""",
                "w1c" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE CAST(u.t AS INTEGER) > 0 AND u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} = 7)""",
                "v0" => $"""SELECT c."id", u.t FROM {unnest} WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(u.t AS INTEGER) = 1)""",
                "v0c" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(u.t AS INTEGER) = 1)""",
                "v2" => $"""SELECT c."id" FROM {lateral} WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(l.b2 AS INTEGER) = 1)""",
                "v3" => $"""SELECT c."id", u.t FROM {t} AS c LEFT JOIN UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t) ON TRUE WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(u.t AS INTEGER) = 1)""",
                "u6" => $"""SELECT c."id", u.t FROM {unnest} WHERE {k} = 'A' AND NOT ({k} = 'A' OR CAST(u.t AS INTEGER) = 1)""",
                _ => BesideACorrelateWritten(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a conjunct of both sides that Calcite's simplification of it takes down to
        /// terms of one side, over a traversal, a lateral and two traversals; a conjunct of both sides beside one that holds
        /// it; and a whole read as false each of whose conjuncts reads one side.
        /// </summary>
        static string BesideACorrelateWritten(string name, string t)
        {
            var k = KindOf("c");
            var m = Json("c", "m");
            var b = Json("c", "body");
            var cb = $"CAST({b} AS INTEGER)";
            var tags = """JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)""";
            var unnest = $"""{t} AS c, UNNEST({tags}) AS u(t)""";
            var twice = $"""{t} AS c, UNNEST({tags}) AS u(t), UNNEST({tags}) AS v(s)""";
            var lateral = $"""{t} AS c CROSS JOIN LATERAL (SELECT {b} AS b2 FROM (VALUES (1)) AS v(x0)) AS l""";
            var some = $"""{t} AS c CROSS JOIN LATERAL (SELECT {k} AS k2 FROM (VALUES (1)) AS v(x0) WHERE {m} = '1') AS l""";
            var p = $"(({k} = 'B') = (u.t = 'x'))";
            var pl = $"(({k} = 'B') = (l.b2 = 'abc'))";
            string U(string where) => $"""SELECT c."id", u.t FROM {unnest} WHERE {where}""";
            string UC(string where) => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {where}""";
            string S(string where) => $"""SELECT c."id" FROM {some} WHERE {where}""";
            string SC(string where) => $"""SELECT COUNT(*) AS q FROM {some} WHERE {where}""";
            return name switch
            {
                "n1" => U($"{k} = 'A' AND NOT (u.t IS NULL OR {cb} <> 7)"),
                "n1c" => UC($"{k} = 'A' AND NOT (u.t IS NULL OR {cb} <> 7)"),
                "n2" => U($"u.t = '1' AND NOT (u.t IS NULL OR {cb} <> 7)"),
                "n5" => S($"{k} = 'A' AND NOT (l.k2 IS NULL OR {cb} <> 7)"),
                "n5c" => SC($"{k} = 'A' AND NOT (l.k2 IS NULL OR {cb} <> 7)"),
                "k1" => U($"u.t = '1' AND NOT ({cb} <> 7 OR (u.t = '5' AND {k} = 'B'))"),
                "k1c" => UC($"u.t = '1' AND NOT ({cb} <> 7 OR (u.t = '5' AND {k} = 'B'))"),
                "k2" => S($"l.k2 = 'A' AND NOT ({cb} <> 7 OR (l.k2 = 'B' AND {m} = '2'))"),
                "bd1" => U($"{m} IS NOT NULL AND NOT (u.t = 'x' OR {cb} <> 7)"),
                "bd1c" => UC($"{m} IS NOT NULL AND NOT (u.t = 'x' OR {cb} <> 7)"),
                "bd4" => S($"{k} IS NOT NULL AND NOT (l.k2 = 'x' OR {cb} <> 7)"),
                "bd8" => $"""SELECT c."id", u.t, v.s FROM {twice} WHERE {m} IS NOT NULL AND NOT (u.t = 'x' OR {cb} <> 7)""",
                "p1" => U($"{k} = 'A' AND {p} AND ({p} OR CAST(u.t AS INTEGER) = CHAR_LENGTH({b}))"),
                "p1c" => UC($"{k} = 'A' AND {p} AND ({p} OR CAST(u.t AS INTEGER) = CHAR_LENGTH({b}))"),
                "p14" => U($"{k} = 'A' AND {p} AND ({p} OR {cb} = 7)"),
                "p6" => $"""SELECT c."id" FROM {lateral} WHERE {k} = 'A' AND {pl} AND ({pl} OR CAST(l.b2 AS INTEGER) = CHAR_LENGTH({m}))""",
                "wf1" => U($"u.t = '1' AND u.t = '2' AND {cb} = 7"),
                "wf1c" => UC($"u.t = '1' AND u.t = '2' AND {cb} = 7"),
                "wf2" => U($"u.t = '1' AND u.t IN ('2', '3') AND {cb} = 7"),
                "wf3" => S($"l.k2 = 'A' AND l.k2 = 'B' AND {cb} = 7"),
                _ => BesideACorrelateRead(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a conjunct of the document whose move would decide what Calcite reads of the
        /// rest, beside a cast that conjunct leaves unread, over a traversal, a lateral and a second traversal; and a conjunct
        /// of both sides Calcite reads beside one of the document as a side read as false.
        /// </summary>
        static string BesideACorrelateRead(string name, string t)
        {
            var k = KindOf("c");
            var m = Json("c", "m");
            var b = Json("c", "body");
            var cb = $"CAST({b} AS INTEGER)";
            var tags = """JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)""";
            var xs = """JSON_QUERY(c."DOC", '$.xs' RETURNING VARCHAR ARRAY)""";
            var unnest = $"""{t} AS c, UNNEST({tags}) AS u(t)""";
            var twice = $"""{t} AS c, UNNEST({tags}) AS u(t), UNNEST({xs}) AS w(s)""";
            var lateral = $"""{t} AS c CROSS JOIN LATERAL (SELECT {b} AS t, {k} AS k2 FROM (VALUES (1)) AS v(x0) WHERE {m} = '1') AS l""";
            var no = $"NOT ({k} = 'B' OR u.t = 'x')";
            var nol = $"NOT ({k} = 'B' OR l.t = 'abc')";
            var now = $"NOT ({k} = 'B' OR w.s = 'x')";
            var eq = $"(({k} = 'B') = (u.t = 'x'))";
            var k5 = $"{m} = '1' AND {k} = 'A' AND {eq} AND {no} AND ({eq} OR {cb} = 7) AND ({no} OR CAST(u.t AS INTEGER) = 1)";
            var arm = $"CASE WHEN l.t <> 'x' THEN {cb} > 0 ELSE {cb} > 0 END";
            string U(string where) => $"""SELECT c."id", u.t FROM {unnest} WHERE {where}""";
            string UC(string where) => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {where}""";
            string L(string where) => $"""SELECT c."id", l.t FROM {lateral} WHERE {where}""";
            string LC(string where) => $"""SELECT COUNT(*) AS q FROM {lateral} WHERE {where}""";
            return name switch
            {
                "j1" => U($"{k} = 'A' AND {no} AND ({no} OR CAST(u.t AS INTEGER) = 1)"),
                "j1c" => UC($"{k} = 'A' AND {no} AND ({no} OR CAST(u.t AS INTEGER) = 1)"),
                "jb4" => L($"{k} = 'A' AND {nol} AND ({nol} OR CAST(l.t AS INTEGER) = 1)"),
                "jb4c" => LC($"{k} = 'A' AND {nol} AND ({nol} OR CAST(l.t AS INTEGER) = 1)"),
                "jb7" => $"""SELECT c."id", u.t, w.s FROM {twice} WHERE {k} = 'A' AND {now} AND ({now} OR CAST(w.s AS INTEGER) = 2)""",
                "jb7c" => $"""SELECT COUNT(*) AS q FROM {twice} WHERE {k} = 'A' AND {now} AND ({now} OR CAST(w.s AS INTEGER) = 2)""",
                "k5u" => U(k5),
                "k5uc" => UC(k5),
                "dd2" => U($"{cb} IN (3, 5) AND NOT (u.t IS NULL OR {cb} <> 7)"),
                "dd2c" => UC($"{cb} IN (3, 5) AND NOT (u.t IS NULL OR {cb} <> 7)"),
                "dd1" => L($"{cb} IN (3, 5) AND NOT (NOT ({cb} = 7 AND l.t IS NOT NULL))"),
                "dd1c" => LC($"{cb} IN (3, 5) AND NOT (NOT ({cb} = 7 AND l.t IS NOT NULL))"),
                "d392l" => L($"{cb} IN (3, 5) AND {arm} AND NOT ({arm} OR CAST(l.t AS INTEGER) = 1)"),
                "d392lc" => LC($"{cb} IN (3, 5) AND {arm} AND NOT ({arm} OR CAST(l.t AS INTEGER) = 1)"),
                _ => BesideANestedCorrelate(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a <c>WHERE</c> over a correlate whose side holds another correlate
        /// beneath a sub-query's projection, its own <c>WHERE</c>, a second projection or a grouping, each traversed again
        /// by an <c>UNNEST</c> or a lateral, whose conjunct of the document decides what Calcite reads of a cast beside it.
        /// </summary>
        static string BesideANestedCorrelate(string name, string t)
        {
            static string J(string a, string p) => $"""JSON_VALUE({a}."DOC", '$.{p}')""";
            static string Q(string a, string p) => $"""JSON_QUERY({a}."DOC", '$.{p}' RETURNING VARCHAR ARRAY)""";
            var tags = $"""{t} AS c, UNNEST({Q("c", "tags")}) AS u(t)""";
            var x = $"""(SELECT c."id", c."DOC", u.t FROM {tags}) AS x""";
            var xs = $"UNNEST({Q("x", "xs")}) AS v(s)";
            var or = $"({J("x", "kind")} = 'B' OR CAST(x.t AS INTEGER) = 1) AND {J("x", "kind")} = 'A'";
            var body = $"(SELECT {J("c", "body")} AS t FROM (VALUES (1)) AS w0(x0) WHERE {J("c", "m")} = '1') AS l";
            var y = $"""(SELECT c."id", c."DOC", l.t AS lt FROM {t} AS c CROSS JOIN LATERAL {body}) AS y""";
            var yl = $"""(SELECT c."id", c."DOC", l.t AS lt FROM {t} AS c LEFT JOIN LATERAL {body} ON TRUE) AS y""";
            var ws = $"UNNEST({Q("y", "xs")}) AS w(s)";
            var ory = $"{J("y", "kind")} = 'A' AND ({J("y", "kind")} = 'B' OR CAST(y.lt AS INTEGER) = 1)";
            var more = $"UNNEST({Q("x", "more")}) AS v(s)";
            var p1 = $"WHERE {J("c", "kind")} = 'A' AND (({J("c", "kind")} = 'B' AND u.t = 'x') OR u.t <> 'z')";
            var px = $"""(SELECT c."id", c."DOC", u.t FROM {tags} {p1}) AS x""";
            var cast = $"CAST({J("x", "body")} AS INTEGER) = 7";
            var keys = $"""(SELECT c."id", u.t FROM {tags} GROUP BY c."id", u.t) AS x""";
            var some = """CROSS JOIN LATERAL (SELECT x.t AS t2 FROM (VALUES (1)) AS w(x0) WHERE x."id" <> '2') AS l""";
            var ork = """x."id" = '1' AND (x."id" = '2' OR CAST(x.t AS INTEGER) = 1)""";
            return name switch
            {
                "np1" => $"""SELECT x."id", x.t, v.s FROM {x}, {xs} WHERE {or}""",
                "np1c" => $"""SELECT COUNT(*) AS q FROM {x}, {xs} WHERE {or}""",
                "np6" => $"""SELECT x."id", x.t, v.s FROM (SELECT c."id", c."DOC", u.t FROM {t} AS c LEFT JOIN UNNEST({Q("c", "tags")}) AS u(t) ON TRUE) AS x, {xs} WHERE {or}""",
                "np8" => $"""SELECT x."id", x.t, l.t2 FROM {x} CROSS JOIN LATERAL (SELECT x.t AS t2 FROM (VALUES (1)) AS w(x0) WHERE {J("x", "m")} = '1') AS l WHERE {or}""",
                "np10" => $"""SELECT x."id", x.t, v.s FROM (SELECT c."id", c."DOC", l.t FROM {t} AS c CROSS JOIN LATERAL {body}) AS x, {xs} WHERE ({J("x", "kind")} = 'B' OR CAST(x.t AS INTEGER) = 7) AND {J("x", "kind")} = 'A'""",
                "np10c" => $"""SELECT COUNT(*) AS q FROM (SELECT c."id", c."DOC", l.t FROM {t} AS c CROSS JOIN LATERAL {body}) AS x, {xs} WHERE ({J("x", "kind")} = 'B' OR CAST(x.t AS INTEGER) = 7) AND {J("x", "kind")} = 'A'""",
                "nq1" => $"""SELECT x."id", x.t, v.s FROM (SELECT c."id", c."DOC", u.t FROM {tags} WHERE u.t <> 'zz') AS x, {xs} WHERE {or}""",
                "nt1" => $"""SELECT x."id", x.t, v.s FROM (SELECT y."id", y."DOC", y.t FROM (SELECT c."id", c."DOC", u.t FROM {tags}) AS y) AS x, {xs} WHERE {or}""",
                "nd1" => $"""SELECT x."id", x.t FROM {x} WHERE {or} AND EXISTS (SELECT 1 FROM {xs} WHERE v.s = '1')""",
                "c2" => $"""SELECT c."id", u.t, v.s FROM {tags}, UNNEST({Q("c", "xs")}) AS v(s) WHERE ({J("c", "kind")} = 'B' OR CAST(u.t AS INTEGER) = 1) AND {J("c", "kind")} = 'A'""",
                "ag1" => $"""SELECT x."id", x.t, l.t2 FROM {keys} {some} WHERE {ork}""",
                "ag1c" => $"""SELECT COUNT(*) AS q FROM {keys} {some} WHERE {ork}""",
                "h1pl" => $"""SELECT y."id", w.s FROM {y}, {ws} WHERE {ory} AND w.s <> 'q'""",
                "h1plc" => $"""SELECT COUNT(*) AS q FROM {y}, {ws} WHERE {ory} AND w.s <> 'q'""",
                "h8pll" => $"""SELECT y."id", w.s FROM {yl}, {ws} WHERE {ory}""",
                "h8pld" => $"""SELECT DISTINCT y."id" FROM {y}, {ws} WHERE {ory}""",
                "h3" => $"""SELECT c."id", w.s FROM {t} AS c CROSS JOIN LATERAL {body} CROSS JOIN UNNEST({Q("c", "xs")}) AS w(s) WHERE {J("c", "kind")} = 'A' AND ({J("c", "kind")} = 'B' OR CAST(l.t AS INTEGER) = 1)""",
                "px5" => $"""SELECT x."id", x.t, v.s FROM {px}, {more} WHERE {cast} AND v.s IS NOT NULL""",
                "px5c" => $"""SELECT COUNT(*) AS q FROM {px}, {more} WHERE {cast} AND v.s IS NOT NULL""",
                "pz2" => $"""SELECT x."id", x.t, v.s FROM {px}, {more} WHERE CAST(x.t AS INTEGER) > 0 AND v.s IS NOT NULL""",
                "pz4" => $"""SELECT x."id", x.t, l.k2 FROM {px} CROSS JOIN LATERAL (SELECT {J("x", "kind")} AS k2 FROM (VALUES (1)) AS w(x0)) AS l WHERE {cast} AND l.k2 IS NOT NULL""",
                _ => BesideAGrouping(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a <c>WHERE</c> over a lateral whose side groups another traversal by
        /// grouping sets, by keys beside a <c>ROLLUP</c> or a <c>CUBE</c>, or by one grouping set.
        /// </summary>
        static string BesideAGrouping(string name, string t)
        {
            static string J(string a, string p) => $"""JSON_VALUE({a}."DOC", '$.{p}')""";
            var tags = $"""UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)""";
            var m = J("c", "m");
            var k = J("c", "kind");
            var some = """CROSS JOIN LATERAL (SELECT x.t AS t2 FROM (VALUES (1)) AS w(x0) WHERE x."id" <> '2') AS l""";
            var ork = """x."id" = '1' AND (x."id" = '2' OR CAST(x.t AS INTEGER) = 1)""";
            var z = $"""(SELECT c."id" AS "id", u.t AS t, {m} AS mm, {k} AS kk FROM {t} AS c, {tags}) AS z""";
            var body = $"(SELECT {J("c", "body")} AS t FROM (VALUES (1)) AS w0(x0) WHERE {m} = '1') AS l";
            var lat = """CROSS JOIN LATERAL (SELECT x.t AS t2 FROM (VALUES (1)) AS w(x0) WHERE x."id" <> '5') AS l2""";
            var orl = """x."id" = '1' AND (x."id" = '2' OR CAST(x.t AS INTEGER) = 7)""";
            string S(string select, string from, string groupBy) => $"""(SELECT {select} FROM {from} GROUP BY {groupBy}) AS x""";
            string Q(string x, string where) => $"""SELECT x."id", x.t, l.t2 FROM {x} {some} WHERE {where}""";
            string QC(string x, string where) => $"""SELECT COUNT(*) AS q FROM {x} {some} WHERE {where}""";
            var sets = $"""GROUPING SETS ((c."id", u.t), (c."id", u.t, {k}))""";
            var gs1 = S($"""c."id", u.t, {m} AS m""", $"{t} AS c, {tags}", $"""GROUPING SETS ((c."id", u.t), (c."id", u.t, {m}))""");
            var gs3 = S($"""c."id", u.t, {k} AS k""", $"{t} AS c, {tags}", $"""GROUPING SETS ((c."id", u.t, {k}), (c."id", u.t))""");
            var gso = S("""c."id", u.t""", $"{t} AS c, {tags}", sets);
            var gro = S("""c."id", u.t""", $"{t} AS c, {tags}", $"""c."id", u.t, ROLLUP({k})""");
            var gwo = S("""c."id", u.t""", $"{t} AS c, {tags} WHERE {m} IS NOT NULL", sets);
            var gsl = S("""c."id", u.t""", $"{t} AS c LEFT JOIN {tags} ON TRUE", sets);
            var gsk = S($"""c."id", u.t, {k} AS k""", $"{t} AS c, {tags}", $"""GROUPING SETS ((c."id", u.t, {k}), (c."id", u.t, {k}, {m}))""");
            var gl1 = S($"""c."id", l.t, {k} AS k""", $"{t} AS c CROSS JOIN LATERAL {body}", $"""GROUPING SETS ((c."id", l.t), (c."id", l.t, {k}))""");
            var onk = """CROSS JOIN LATERAL (SELECT x.t AS t2 FROM (VALUES (1)) AS w(x0) WHERE x.k = 'A') AS l""";
            var ork2 = "x.k = 'A' AND (x.k = 'B' OR CAST(x.t AS INTEGER) = 1)";
            return name switch
            {
                "gs1" => Q(gs1, ork),
                "gs1c" => QC(gs1, ork),
                "gs3" => Q(gs3, ork),
                "gs4" => Q(S("""c."id", u.t""", $"{t} AS c, {tags}", """ROLLUP (c."id", u.t)"""), ork),
                "gs5" => Q(S("""c."id", u.t""", $"{t} AS c, {tags}", """GROUPING SETS ((c."id", u.t))"""), ork),
                "gl1" => $"""SELECT x."id", x.t, l2.t2 FROM {gl1} {lat} WHERE {orl}""",
                "gl1c" => $"""SELECT COUNT(*) AS q FROM {gl1} {lat} WHERE {orl}""",
                "gs5p" => Q(S("""c."id", u.t""", $"{t} AS c, {tags}", $"""GROUPING SETS ((c."id", u.t), (c."id", u.t, {m}))"""), ork),
                "gs7" => Q(S("""z."id", z.t""", z, """z."id", z.t, ROLLUP(z.mm)"""), ork),
                "gso" => Q(gso, ork),
                "gsoc" => QC(gso, ork),
                "gro" => Q(gro, ork),
                "groc" => QC(gro, ork),
                "gwo" => Q(gwo, ork),
                "gwoc" => QC(gwo, ork),
                "gsl" => Q(gsl, ork),
                "gslc" => QC(gsl, ork),
                "gsk" => $"""SELECT x."id", x.k, l.t2 FROM {gsk} {onk} WHERE {ork2}""",
                "gskc" => $"""SELECT COUNT(*) AS q FROM {gsk} {onk} WHERE {ork2}""",
                "wr" => $"""SELECT x."id", x.t, l.t2, x.rn FROM (SELECT c."id", u.t, ROW_NUMBER() OVER (PARTITION BY c."id", u.t ORDER BY u.t) AS rn FROM {t} AS c, {tags}) AS x {some} WHERE {ork}""",
                "wr2" => $"""SELECT x."id", x.t, l.t2, x.rn FROM (SELECT c."id", u.t, ROW_NUMBER() OVER (PARTITION BY c."id" ORDER BY u.t) AS rn FROM {t} AS c, {tags}) AS x {some} WHERE {ork}""",
                "wj2" => $"""SELECT x."id", x.t, l.t2 FROM (SELECT c."id", u.t FROM {t} AS c CROSS JOIN {tags} JOIN {t} AS d ON d."id" = c."id") AS x {some} WHERE {ork}""",
                "wm6" => $"""SELECT x."id", x.t, l.t2 FROM (SELECT y."id", y.t FROM (SELECT c."id", u.t FROM {t} AS c, {tags}) AS y TABLESAMPLE BERNOULLI(50) REPEATABLE(7)) AS x {some} WHERE {ork}""",
                _ => BesideTwoCorrelates(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a <c>WHERE</c> over a traversal or a lateral of a sub-query that holds its
        /// own <c>WHERE</c> over another, the outer one holding a conjunct of the document that decides the rest and the
        /// sub-query's none (ws1 to ws5, ws8 and their <c>COUNT</c>s); neither holding one (ws6); and the sub-query's alone
        /// (ws7).
        /// </summary>
        static string BesideTwoCorrelates(string name, string t)
        {
            static string J(string a, string p) => $"""JSON_VALUE({a}."DOC", '$.{p}')""";
            static string Q(string a, string p) => $"""JSON_QUERY({a}."DOC", '$.{p}' RETURNING VARCHAR ARRAY)""";
            var xn = """JSON_VALUE(x."DOC", '$.n' RETURNING INTEGER)""";
            var xb = J("x", "body");
            var k = J("c", "kind");
            var un = $"UNNEST({Q("c", "tags")}) AS u(t)";
            var more = $"UNNEST({Q("x", "more")}) AS v(s)";
            var lateral = $"CROSS JOIN LATERAL (SELECT {J("x", "s")} AS s FROM (VALUES (1)) AS w(x0) WHERE {J("x", "s")} IS NOT NULL) AS v";
            string U(string where) => $"""(SELECT c."id", c."DOC", u.t FROM {t} AS c, {un} WHERE {where}) AS x""";
            string S(string sub, string where) => $"""SELECT x."id", x.t, v.s FROM {sub}, {more} WHERE {where}""";
            string SC(string sub, string where) => $"""SELECT COUNT(*) AS q FROM {sub}, {more} WHERE {where}""";
            var w = $"CAST({J("c", "w")} AS INTEGER) > 50";
            var inner = $"CAST(u.t AS INTEGER) = 1 AND {w}";
            var outer = $"CAST({xb} AS INTEGER) = 7 AND {xn} > 50 AND ({xn} > 40 OR v.s = '1')";
            var swapped = $"({xn} > 40 OR v.s = '1') AND {xn} > 50 AND CAST({xb} AS INTEGER) = 7";
            var l = $"""(SELECT c."id", c."DOC", l.t FROM {t} AS c CROSS JOIN LATERAL (SELECT {J("c", "m")} AS t FROM (VALUES (1)) AS w0(x0)) AS l WHERE CAST(l.t AS INTEGER) = 1 AND {w}) AS x""";
            var ul = $"""(SELECT c."id", c."DOC", u.t FROM {t} AS c LEFT JOIN {un} ON TRUE WHERE {inner}) AS x""";
            var byKind = $"CAST({xb} AS INTEGER) = 7 AND {J("x", "kind")} = 'A' AND ({J("x", "kind")} = 'B' OR v.s = 'a')";
            var decides = $"{k} = 'A' AND ({k} = 'B' OR u.t = '1')";
            return name switch
            {
                "ws1" => S(U(inner), outer),
                "ws1c" => SC(U(inner), outer),
                "ws2" => S(U($"{w} AND CAST(u.t AS INTEGER) = 1"), swapped),
                "ws2c" => SC(U($"{w} AND CAST(u.t AS INTEGER) = 1"), swapped),
                "ws3" => S(l, outer),
                "ws3c" => SC(l, outer),
                "ws4" => $"""SELECT x."id", x.t, v.s FROM {U(inner)} {lateral} WHERE {outer}""",
                "ws4c" => $"""SELECT COUNT(*) AS q FROM {U(inner)} {lateral} WHERE {outer}""",
                "ws5" => S(U(inner), byKind),
                "ws5c" => SC(U(inner), byKind),
                "ws8" => S(ul, outer),
                "ws8c" => SC(ul, outer),
                "ws6" => S(U(inner), $"CAST({xb} AS INTEGER) = 7 AND v.s = 'a'"),
                "ws6c" => SC(U(inner), $"CAST({xb} AS INTEGER) = 7 AND v.s = 'a'"),
                "ws7" => S(U(decides), $"CAST({xb} AS INTEGER) = 7 AND v.s IS NOT NULL"),
                "ws7c" => SC(U(decides), $"CAST({xb} AS INTEGER) = 7 AND v.s IS NOT NULL"),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
            };
        }

        /// <summary>
        /// <c>kind</c> is <c>A</c> or <c>B</c> and <c>body</c> a string, both required; <c>n</c> an integer from 0 to 100;
        /// <c>m</c> and <c>s</c> strings; <c>tags</c> and <c>more</c> arrays of strings.
        /// </summary>
        internal const string TwoTraversals = """{ "type": "object", "required": ["kind", "body"], "properties": { "kind": { "enum": ["A", "B"] }, "m": { "type": "string" }, "n": { "type": "integer", "minimum": 0, "maximum": 100 }, "body": { "type": "string" }, "s": { "type": "string" }, "tags": { "type": "array", "items": { "type": "string" } }, "more": { "type": "array", "items": { "type": "string" } } } }""";

        /// <summary>
        /// What <see cref="TwoTraversals"/> admits: a document of kind <c>B</c> holding an element and an <c>m</c> of
        /// <c>"x"</c>; one of kind <c>A</c> with a <c>body</c> of <c>"zz"</c> and nothing in <c>more</c>; and one of kind
        /// <c>A</c> whose every cast reads.
        /// </summary>
        internal static readonly string[] TwoTraversalsDocuments =
        [
            """{"id":"1","category":"a","kind":"B","m":"x","w":10,"tags":["x"],"body":"5","n":10,"more":["b"],"s":"b"}""",
            """{"id":"2","category":"a","kind":"A","m":"1","w":60,"tags":["1"],"body":"zz","n":60}""",
            """{"id":"3","category":"b","kind":"A","m":"1","w":60,"tags":["1"],"body":"7","n":60,"more":["a"],"s":"a"}""",
        ];

        /// <summary>
        /// A statement is planned with Calcite's transposition at every correlate or at none: where the <c>WHERE</c> over a
        /// traversal or a lateral of a sub-query would not be transposed, the sub-query's own <c>WHERE</c> over a traversal
        /// is not either, so ws1 to ws5, ws8 and their <c>COUNT</c>s fail on the element <c>"x"</c> as Calcite with no
        /// transposition does, where moving the sub-query's alone answered; ws6, whose two are both transposed, fails on the
        /// <c>body</c> <c>"zz"</c> as Calcite with <c>FILTER_CORRELATE</c> does; and ws7, whose sub-query's is not, answers
        /// as Calcite with no transposition does, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("ws1", "raises x")]
        [InlineData("ws1c", "raises x")]
        [InlineData("ws2", "raises x")]
        [InlineData("ws2c", "raises x")]
        [InlineData("ws3", "raises x")]
        [InlineData("ws3c", "raises x")]
        [InlineData("ws4", "raises x")]
        [InlineData("ws4c", "raises x")]
        [InlineData("ws5", "raises x")]
        [InlineData("ws5c", "raises x")]
        [InlineData("ws8", "raises x")]
        [InlineData("ws8c", "raises x")]
        [InlineData("ws6", "raises zz")]
        [InlineData("ws6c", "raises zz")]
        [InlineData("ws7", "3,1,a")]
        [InlineData("ws7c", "1")]
        public async Task AStatementIsTransposedAtEveryCorrelateOrAtNone(string name, string rows)
        {
            await Answers(name, rows, TwoTraversals, TwoTraversalsDocuments);
        }

        /// <summary>
        /// What <see cref="Traversed"/> admits: a document of kind <c>B</c> holding an element <c>"x"</c> and no <c>xs</c>,
        /// beside documents of kind <c>B</c> with no element, one of which holds a <c>body</c> of <c>"zz"</c>.
        /// </summary>
        internal static readonly string[] NestedBesideNoElement =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","tags":["1"],"xs":["1"],"body":"7","n":1}""",
            """{"id":"2","category":"a","kind":"B","m":"2","tags":["x"],"xs":[],"body":"q","n":2}""",
            """{"id":"3","category":"b","kind":"A","m":"1","tags":["1","2"],"xs":["2"],"body":"5","n":3}""",
            """{"id":"4","category":"b","kind":"B","m":"2","tags":[],"xs":["3"],"body":"abc","n":4}""",
            """{"id":"5","category":"c","kind":"B","m":"1","tags":[],"xs":[],"body":"zz","n":5}""",
        ];

        /// <summary>
        /// What <see cref="Traversed"/> admits: documents of kind <c>B</c> whose lateral returns a <c>body</c> no integer
        /// parses and whose <c>xs</c> is empty.
        /// </summary>
        internal static readonly string[] LateralBesideNoElement =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","body":"1","tags":["1","x"],"xs":["2"],"n":1}""",
            """{"id":"2","category":"a","kind":"B","m":"1","body":"q","tags":["z"],"xs":[],"n":2}""",
            """{"id":"3","category":"b","kind":"A","m":"1","body":"7","tags":["7"],"xs":["x"],"n":3}""",
            """{"id":"4","category":"b","kind":"B","m":"2","body":"5","tags":["x"],"xs":["1"],"n":4}""",
            """{"id":"5","category":"c","kind":"A","m":"2","body":"abc","tags":[],"xs":[],"n":5}""",
            """{"id":"6","category":"c","kind":"B","m":"1","body":"zz","tags":["5"],"xs":[],"n":6}""",
        ];

        /// <summary>
        /// What <see cref="Traversed"/> admits: a document of kind <c>B</c> whose <c>tags</c> and <c>more</c> hold elements,
        /// with a <c>body</c> of <c>"zz"</c>, or, with <paramref name="element"/>, of kind <c>B</c> holding an element
        /// <c>"x"</c>.
        /// </summary>
        static string[] ElementsBesideAKindRuledOut(bool element) =>
        [
            """{"id":"1","category":"a","kind":"A","m":"1","tags":["1","3"],"more":["1"],"body":"7","n":1}""",
            """{"id":"2","category":"a","kind":"A","m":"2","tags":[],"more":[],"body":"9","n":2}""",
            element
                ? """{"id":"3","category":"b","kind":"B","m":"1","tags":["x"],"more":["2"],"body":"5","n":60}"""
                : """{"id":"3","category":"b","kind":"B","m":"1","tags":["2"],"more":["2"],"body":"5","n":60}""",
            """{"id":"4","category":"b","kind":"A","m":"1","tags":["2"],"more":[],"body":"3","n":4}""",
            element
                ? """{"id":"5","category":"c","kind":"B","m":"2","tags":["1"],"more":["1"],"body":"8","n":70}"""
                : """{"id":"5","category":"c","kind":"B","m":"2","tags":["1"],"more":["1"],"body":"zz","n":70}""",
            """{"id":"6","category":"c","kind":"A","m":"1","tags":["7","1"],"more":["1","2"],"body":"7","n":50}""",
        ];

        /// <summary>
        /// Where a correlate's side holds another correlate beneath a sub-query's projection, its own <c>WHERE</c>, a second
        /// projection or a grouping, the conjuncts Calcite's rule would move into that side reach the inner correlate, and
        /// are moved only where its transposition of them, beside any <c>WHERE</c> they meet, is taken too: np1, np6, the
        /// lateral np8, np10 over a lateral, nq1 and px5 over a sub-query's <c>WHERE</c>, nt1 through two projections, nd1
        /// beside an <c>EXISTS</c>, ag1 through a grouping, h1pl, h8pll and h8pld over a lateral traversed again, pz2, pz4
        /// and the <c>COUNT</c>s cast no element or <c>body</c> of a kind the condition rules out, and answer the rows it
        /// keeps, as c2 and h3, the same conditions with no sub-query, do, declared or not, under both targets. So do the
        /// groupings by grouping sets, by keys beside a <c>ROLLUP</c> and by one grouping set, past which Calcite's rule
        /// carries a conjunct whose columns every set holds: gs1, gs3, gs5, gs5p, gs7, gso, gro, gwo beside a sub-query's
        /// <c>WHERE</c>, gsl over a <c>LEFT JOIN</c>, gsk, the lateral gl1 and the <c>COUNT</c>s; and gs4, a <c>ROLLUP</c>
        /// of every key, which no column of every set lets the conjuncts pass, fails on <c>"x"</c> as Calcite with
        /// <c>FILTER_CORRELATE</c> does.
        /// </summary>
        [Theory]
        [InlineData("np1", "n", "1,1,1;3,1,2")]
        [InlineData("np1c", "n", "2")]
        [InlineData("np6", "n", "1,1,1;3,1,2")]
        [InlineData("np8", "n", "1,1,1;3,1,1")]
        [InlineData("np10", "n", "1,7,1")]
        [InlineData("np10c", "n", "1")]
        [InlineData("nq1", "n", "1,1,1;3,1,2")]
        [InlineData("nt1", "n", "1,1,1;3,1,2")]
        [InlineData("nd1", "n", "1,1")]
        [InlineData("c2", "n", "1,1,1;3,1,2")]
        [InlineData("ag1", "n", "1,1,1")]
        [InlineData("ag1c", "n", "1")]
        [InlineData("gs1", "n", "1,1,1;1,1,1")]
        [InlineData("gs1c", "n", "2")]
        [InlineData("gs3", "n", "1,1,1;1,1,1")]
        [InlineData("gs4", "n", "raises x")]
        [InlineData("gs5", "n", "1,1,1")]
        [InlineData("gl1", "n", "1,7,7;1,7,7")]
        [InlineData("gl1c", "n", "2")]
        [InlineData("gs5p", "n", "1,1,1;1,1,1")]
        [InlineData("gs7", "n", "1,1,1;1,1,1")]
        [InlineData("gso", "n", "1,1,1;1,1,1")]
        [InlineData("gsoc", "n", "2")]
        [InlineData("gro", "n", "1,1,1;1,1,1")]
        [InlineData("groc", "n", "2")]
        [InlineData("gwo", "n", "1,1,1;1,1,1")]
        [InlineData("gwoc", "n", "2")]
        [InlineData("gsl", "n", "1,1,1;1,1,1")]
        [InlineData("gslc", "n", "2")]
        [InlineData("gsk", "n", "1,A,1;1,A,1;3,A,1;3,A,1")]
        [InlineData("gskc", "n", "4")]
        [InlineData("h1pl", "h", "1,2")]
        [InlineData("h1plc", "h", "1")]
        [InlineData("h8pll", "h", "1,2")]
        [InlineData("h8pld", "h", "1")]
        [InlineData("h3", "h", "1,2")]
        [InlineData("px5", "zz", "1,1,1;1,3,1;6,1,1;6,1,2;6,7,1;6,7,2")]
        [InlineData("px5c", "zz", "6")]
        [InlineData("pz4", "zz", "1,1,A;1,3,A;6,1,A;6,7,A")]
        [InlineData("pz2", "x", "1,1,1;1,3,1;6,1,1;6,1,2;6,7,1;6,7,2")]
        public async Task AConjunctMovedIntoASideIsMovedOnlyAsTheCorrelateItReachesTakesIt(string name, string documents, string rows)
        {
            await Answers(name, rows, Traversed, documents switch
            {
                "n" => NestedBesideNoElement,
                "h" => LateralBesideNoElement,
                _ => ElementsBesideAKindRuledOut(documents == "x"),
            });
        }

        /// <summary>
        /// What <see cref="Traversed"/> admits: a document with no element and a <c>body</c> of <c>"q"</c>, which no cast to
        /// <c>INTEGER</c> reads, beside one with an element <c>"x"</c>, and a lateral that returns no row for the first.
        /// </summary>
        internal static readonly string[] BesideAnEmptySide =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","flag":true,"body":"7","tags":["1","x"]}""",
            """{"id":"2","category":"a","kind":"A","m":"2","flag":true,"body":"q","tags":[]}""",
            """{"id":"3","category":"b","kind":"A","m":"1","flag":false,"body":"3","tags":["2"]}""",
        ];

        /// <summary><see cref="BesideAnEmptySide"/> and a document with an element and a <c>body</c> of <c>"abc"</c>.</summary>
        internal static readonly string[] KeptBesideAnEmptySide =
        [
            .. BesideAnEmptySide,
            """{"id":"4","category":"b","kind":"A","m":"2","flag":true,"body":"abc","tags":["3"]}""",
        ];

        /// <summary><see cref="Traversed"/> with <c>body</c> one of <c>"5"</c> and <c>"6"</c>.</summary>
        internal const string TraversedFixed = """{ "type": "object", "required": ["kind", "m", "body", "tags"], "properties": { "kind": { "enum": ["A", "B"] }, "m": { "enum": ["1", "2"] }, "body": { "enum": ["5", "6"] }, "tags": { "type": "array", "items": { "type": "string" } } } }""";

        /// <summary>What <see cref="TraversedFixed"/> admits: an element <c>"x"</c>, and a document with no element.</summary>
        internal static readonly string[] FixedBesideAnEmptySide =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","body":"5","tags":["1","x"]}""",
            """{"id":"2","category":"a","kind":"B","m":"2","body":"6","tags":["1"]}""",
            """{"id":"3","category":"b","kind":"A","m":"1","body":"5","tags":[]}""",
        ];

        /// <summary>
        /// Where a conjunct the rule would move decides what Calcite's simplification of the whole reads of a conjunct of both
        /// sides, nothing moves and the term that reading leaves stays above the correlate: x3, x9, x9b and the lateral x8
        /// cast no <c>body</c> of a document with no element or no lateral row, and answer the rows the condition keeps, as
        /// does mx1, which beside such a conjunct holds one the whole reads away, and z189u and w1 fail on the element
        /// <c>"x"</c> as Calcite does, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("x3", "1,1;1,x")]
        [InlineData("x9", "1,1")]
        [InlineData("x9b", "1,1")]
        [InlineData("x8", "1")]
        [InlineData("x8c", "1")]
        [InlineData("mx1", "1,1;1,x")]
        [InlineData("mx1c", "2")]
        [InlineData("z189u", "raises x")]
        [InlineData("z189uc", "raises x")]
        [InlineData("w1", "raises x")]
        [InlineData("w1c", "raises x")]
        public async Task ATermTheWholeReadsIntoOneSideStaysAboveACorrelate(string name, string rows)
        {
            var (schema, documents) = name.StartsWith('w') ? (TraversedFixed, FixedBesideAnEmptySide) : (Traversed, BesideAnEmptySide);
            await Answers(name, rows, schema, documents);
        }

        /// <summary>
        /// A conjunct of both sides that Calcite's simplification takes down to a term of one side is planned as one of
        /// Calcite's plans: where all its rule leaves above after its last firing is read in the whole, as that rule leaves
        /// it, which moves the term into that side, so n1, n2, bd1, the laterals n5 and bd4, the two traversals of bd8 and
        /// the <c>COUNT</c>s fail on the <c>body</c> of a document with no element or no lateral row as Calcite with
        /// <c>FILTER_CORRELATE</c> does; and elsewhere with no transposition, so k1, the lateral k2 and k1's <c>COUNT</c>
        /// cast no such <c>body</c> and answer the rows the condition keeps, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("n1", "raises q")]
        [InlineData("n1c", "raises q")]
        [InlineData("n2", "raises q")]
        [InlineData("n5", "raises q")]
        [InlineData("n5c", "raises q")]
        [InlineData("k1", "1,1")]
        [InlineData("k1c", "1")]
        [InlineData("k2", "1")]
        [InlineData("bd1", "raises q")]
        [InlineData("bd1c", "raises q")]
        [InlineData("bd4", "raises q")]
        [InlineData("bd8", "raises q")]
        public async Task AConjunctOfBothSidesAboveACorrelateIsPlannedAsOneOfCalcitesPlans(string name, string rows)
        {
            await Answers(name, rows, Traversed, BesideAnEmptySide);
        }

        /// <summary>
        /// A conjunct of both sides kept above a correlate is read beside the rest, which it absorbs, where the conjunct the
        /// rule would move decides it: p1, p14, the lateral p6 and p1's <c>COUNT</c> cast no element <c>"x"</c> and no
        /// <c>body</c> of <c>"abc"</c>, and answer the rows the condition keeps, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("p1", "1,1;3,2;4,3")]
        [InlineData("p1c", "3")]
        [InlineData("p14", "1,1;3,2;4,3")]
        [InlineData("p6", "1;2;3")]
        public async Task AConjunctKeptAboveACorrelateIsReadBesideTheRest(string name, string rows)
        {
            await Answers(name, rows, Traversed, KeptBesideAnEmptySide);
        }

        /// <summary>
        /// Where Calcite's simplification of the whole condition above a correlate reads it as false, nothing is moved into a
        /// side, where a conjunct that can fail would be evaluated alone: v0, its <c>COUNT</c>, the lateral v2, the left
        /// traversal v3, and wf1, wf2, the lateral wf3 and wf1's <c>COUNT</c>, each of whose conjuncts reads one side, answer no
        /// rows over a <c>body</c> of <c>"abc"</c>, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("v0", "")]
        [InlineData("v0c", "0")]
        [InlineData("v2", "")]
        [InlineData("v3", "")]
        [InlineData("u6", "")]
        [InlineData("wf1", "")]
        [InlineData("wf1c", "0")]
        [InlineData("wf2", "")]
        [InlineData("wf3", "")]
        public async Task AConditionReadAsFalseMovesNothingIntoACorrelatesSide(string name, string rows)
        {
            await Answers(name, rows, Traversed, TraversedDocuments);
        }

        /// <summary>
        /// What <see cref="Traversed"/> admits: a document of kind <c>A</c> holding an element <c>"x"</c> and one holding an
        /// element <c>"x"</c> and a <c>body</c> of <c>"abc"</c>, which no cast to <c>INTEGER</c> reads, both with
        /// <c>xs</c> holding <c>"x"</c>.
        /// </summary>
        internal static readonly string[] KindABesideX =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","body":"7","tags":["1","x"],"xs":["2","x"]}""",
            """{"id":"2","category":"a","kind":"B","m":"1","body":"5","tags":["x"],"xs":["x"]}""",
            """{"id":"3","category":"b","kind":"A","m":"2","body":"5","tags":["2"],"xs":["3"]}""",
            """{"id":"4","category":"b","kind":"A","m":"1","body":"abc","tags":["x"],"xs":["x"]}""",
        ];

        /// <summary>
        /// Where a conjunct Calcite's rule would move into a side decides what Calcite's simplification of the whole reads of
        /// the rest, nothing moves, and the cast that reading leaves unread is not evaluated: j1, the lateral jb4, jb7 over a
        /// second traversal, k5u and their <c>COUNT</c>s answer the rows the condition keeps over a document of kind
        /// <c>A</c> holding an element <c>"x"</c>, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("j1", "1,1;3,2")]
        [InlineData("j1c", "2")]
        [InlineData("jb4", "1,7")]
        [InlineData("jb4c", "1")]
        [InlineData("jb7", "1,1,2;1,x,2;3,2,3")]
        [InlineData("jb7c", "3")]
        [InlineData("k5u", "1,1")]
        [InlineData("k5uc", "1")]
        public async Task AConjunctThatDecidesTheRestIsNotMovedIntoACorrelatesSide(string name, string rows)
        {
            await Answers(name, rows, Traversed, KindABesideX);
        }

        /// <summary>
        /// A conjunct of the document is not evaluated alone in a side beside which Calcite's simplification reads a term it
        /// derives from a conjunct of both sides as false: dd2, the laterals dd1 and d392l and their <c>COUNT</c>s answer no
        /// rows over a document whose <c>body</c> no integer parses and which holds no element or has no lateral row,
        /// declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("dd2", "")]
        [InlineData("dd2c", "0")]
        [InlineData("dd1", "")]
        [InlineData("dd1c", "0")]
        [InlineData("d392l", "")]
        [InlineData("d392lc", "0")]
        public async Task AConjunctMovedBesideADerivedTermIsNotEvaluatedAlone(string name, string rows)
        {
            await Answers(name, rows, Traversed, BesideAnEmptySide);
        }

        /// <summary>
        /// Plans and runs a shape of <see cref="BesideACorrelate"/> over documents, declared and not, under both targets: the rows
        /// given, separated by <c>;</c>, or, written <c>raises x</c>, a failure on the text <c>"x"</c>.
        /// </summary>
        static async Task Answers(string name, string rows, string schema, string[] documents)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(schema)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var (answer, raised, plan) = await Answer(BesideACorrelate(name, "items"), container.WithServiceTarget(target), documents);
                    if (rows.StartsWith("raises ", StringComparison.Ordinal))
                    {
                        var inner = raised;
                        while (inner?.InnerException is not null)
                            inner = inner.InnerException;

                        inner.Should().NotBeNull($"{name} {target} {declared}: " + plan);
                        inner!.Message.Should().Contain($"\"{rows["raises ".Length..]}\"", $"{name} {target} {declared}: " + plan);
                    }
                    else
                    {
                        raised.Should().BeNull($"{name} {target} {declared}: " + plan);
                        answer.Should().BeEquivalentTo(rows.Length == 0 ? [] : rows.Split(';'), $"{name} {target} {declared}: " + plan);
                    }
                }
            }
        }

        /// <summary>
        /// u18a, u18b, u18d, u18f, the lateral l18a and their <c>COUNT</c>s, each a conjunct of the document beside a part of
        /// both sides it decides, cast no element and no <c>body</c>, and answer the rows the condition keeps, declared or not,
        /// under both targets.
        /// </summary>
        [Theory]
        [InlineData("u18a", "1,x;1,1;2,2")]
        [InlineData("u18ac", "3")]
        [InlineData("u18b", "1,x;1,1;2,2")]
        [InlineData("u18d", "1,x;1,1;2,2")]
        [InlineData("u18f", "1,x;1,1;2,2")]
        [InlineData("l18a", "1;2")]
        [InlineData("l18ac", "2")]
        public async Task APartLeftAboveACorrelateIsReadBesideWhatItMoves(string name, string rows)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(Traversed)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var (answer, raised, plan) = await Answer(BesideACorrelate(name, "items"), container.WithServiceTarget(target), TraversedDocuments);
                    raised.Should().BeNull($"{name} {target} {declared}: " + plan);
                    answer.Should().BeEquivalentTo(rows.Split(';'), $"{name} {target} {declared}: " + plan);
                }
            }
        }

        /// <summary>
        /// Where Calcite's simplification of the whole condition above a correlate reads it as false, nothing moves into a
        /// side: u6 plans within the bound, declared or not, under both targets, its condition, cast and all, in the node above
        /// the correlate, and no node beneath the correlate holds a condition.
        /// </summary>
        [Fact]
        public void AConditionReadAsFalseIsKeptWholeAboveACorrelate()
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(Traversed)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var text = PlanText(PlanToCosmos(BesideACorrelate("u6", "items"), container.WithServiceTarget(target), out _, listener: new Infrastructure.SplitBound(SplitsBound)));
                    var lines = text.Split('\n');
                    var correlate = System.Array.FindIndex(lines, l => l.Contains("Correlate(", StringComparison.Ordinal));
                    correlate.Should().BePositive($"{target} {declared}: " + text);
                    lines[..correlate].Should().Contain(l => l.Contains(":INTEGER", StringComparison.Ordinal) && l.Contains("'$.kind'", StringComparison.Ordinal), $"{target} {declared}: " + text);
                    lines[correlate..].Should().NotContain(l => l.Contains("condition", StringComparison.OrdinalIgnoreCase), $"{target} {declared}: " + text);
                }
            }
        }

        /// <summary>
        /// Plans a statement in the host Chapter 21 describes, its calc pass after, and runs it over a container whose every
        /// request is answered with the same documents, each as the row a statement reading the document and its id is
        /// returned: the rows, each as its columns' text joined by commas, or what the run raised.
        /// </summary>
        static async Task<(List<string>? Rows, Exception? Raised, string Plan)> Answer(string sql, CosmosContainerMetadata container, string[] documents)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container, new DocumentsExecutor(documents));
            var plan = "";
            try
            {
                var best = PlanOver(sql, table, typeFactory, out var rootSchema, windows: true, subqueries: true);

                var program = new org.apache.calcite.plan.hep.HepProgramBuilder();
                foreach (var rule in ClrCursorRules.CalcRules())
                    program.addRuleInstance(rule);

                var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
                hep.setRoot(best);
                best = hep.findBestExp();
                plan = PlanText(best);

                var factory = new ClrCursorRelImplementor(best.getCluster().getRexBuilder(), new java.util.HashMap()).ImplementRoot((ClrCursorRel)best, ClrCursorPrefer.Array);
                var rows = new List<string>();
                await using (var cursor = await factory.OpenAsync(new DocumentsContext(rootSchema.plus(), typeFactory), TestContext.Current.CancellationToken))
                    while (await cursor.ReadAsync(TestContext.Current.CancellationToken))
                        rows.Add(cursor.Current is object?[] columns ? string.Join(",", columns.Select(Text)) : Text(cursor.Current));

                return (rows, null, plan);
            }
            catch (Exception e)
            {
                return (null, e, plan);
            }

            static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }

        /// <summary>
        /// An executor that answers every request reading the document with the same documents, whatever its <c>WHERE</c>,
        /// each as <c>{ "DOC": c, "id": c.id }</c>, and refuses any other.
        /// </summary>
        sealed class DocumentsExecutor(string[] documents) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default) =>
                query.Sql.StartsWith("SELECT VALUE { \"DOC\": c,", StringComparison.Ordinal) && query.Sql.Contains(" JOIN ", StringComparison.Ordinal) == false
                    ? new(Infrastructure.ListCursor.Documents(documents.Select(d => new JsonObject { ["DOC"] = JsonNode.Parse(d), ["id"] = JsonNode.Parse(d)!["id"]!.DeepClone() }.ToJsonString())))
                    : throw new InvalidOperationException("Not answered here: " + query.Sql);

        }

        /// <summary>
        /// The minimum a compiled plan needs to find its way back to the table it reads.
        /// </summary>
        sealed class DocumentsContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory) : DataContext
        {

            public SchemaPlus getRootSchema() => rootSchema;

            public JavaTypeFactory getTypeFactory() => typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => null!;

        }

    }

}
