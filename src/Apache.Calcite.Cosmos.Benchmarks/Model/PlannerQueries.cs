using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Benchmarks.Model
{

    /// <summary>
    /// The statements every planning benchmark is run over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One corpus rather than one per benchmark class, so that the parse, convert, search and render
    /// benchmarks are timing the same statements and their numbers can be read against each other.
    /// A stage that is cheap for a statement and a stage that is expensive for it is the whole point
    /// of separating them.
    /// </para>
    /// <para>
    /// <b>What earns a place here.</b> Not coverage of SQL — the test suite does that — but a
    /// distinct amount of work for the planner. A second statement that reaches the same rules by the
    /// same route costs a benchmark run and reports nothing new, so each entry names, in its note,
    /// the decision it is here to make the planner take. Several are deliberately statements that do
    /// <em>not</em> push: a rule declining is a measurement, and a corpus of only pushable statements
    /// would never time the path a real schema spends most of its time on.
    /// </para>
    /// </remarks>
    public static class PlannerQueries
    {

        static readonly PlannerQuery[] Corpus = new[]
        {
            // ── Predicates ───────────────────────────────────────────────────────

            new PlannerQuery(
                "Filter.PartitionPin",
                PlannerQueryCategory.Filter,
                """SELECT * FROM products AS c WHERE c."category" = 'bikes'""",
                "The floor: one equality on the partition key, which routes and filters both."),

            new PlannerQuery(
                "Filter.PointRead",
                PlannerQueryCategory.Filter,
                """SELECT * FROM products AS c WHERE c."category" = 'bikes' AND c."id" = 'x'""",
                "Partition key and id together address one item, which the planner has to recognise as a point read rather than a query."),

            new PlannerQuery(
                "Filter.HierarchicalFullPin",
                PlannerQueryCategory.Filter,
                """SELECT * FROM events AS c WHERE c."tenant" = 'acme' AND c."user" = 'kim' AND c."session" = 's3'""",
                "All three levels of a hierarchical key, which confines execution to one logical partition out of 192."),

            new PlannerQuery(
                "Filter.HierarchicalPrefixPin",
                PlannerQueryCategory.Filter,
                """SELECT * FROM events AS c WHERE c."tenant" = 'acme' AND c."_ts" > 1700000000""",
                "A prefix of a hierarchical key narrows execution; the extractor has to stop at the prefix rather than take nothing."),

            new PlannerQuery(
                "Filter.HierarchicalGap",
                PlannerQueryCategory.Filter,
                """SELECT * FROM events AS c WHERE c."user" = 'kim' AND c."session" = 's3'""",
                "The second and third levels without the first, which routes nothing — the case the extractor must decline."),

            new PlannerQuery(
                "Filter.DeepConjunction",
                PlannerQueryCategory.Filter,
                """
                SELECT * FROM products AS c
                WHERE c."category" = 'bikes'
                  AND c."_ts" > 1700000000
                  AND CAST(c."_MAP"['price'] AS DOUBLE) BETWEEN 10.0 AND 500.0
                  AND CAST(c."_MAP"['stock'] AS INTEGER) > 0
                  AND CAST(c."_MAP"['brand'] AS VARCHAR) <> 'unbranded'
                  AND CAST(c."_MAP"['colour'] AS VARCHAR) IN ('red', 'blue', 'green')
                  AND IS_DEFINED(c."_MAP"['warranty'])
                  AND NOT IS_NULL(c."_MAP"['sku'])
                """,
                "Eight conjuncts of five different shapes, every one of them translatable — the longest all-push predicate here."),

            new PlannerQuery(
                "Filter.DisjunctionOfConjunctions",
                PlannerQueryCategory.Filter,
                """
                SELECT * FROM products AS c
                WHERE (c."category" = 'bikes' AND CAST(c."_MAP"['price'] AS DOUBLE) < 100.0)
                   OR (c."category" = 'shoes' AND CAST(c."_MAP"['price'] AS DOUBLE) < 60.0)
                   OR (c."category" = 'tents' AND CAST(c."_MAP"['stock'] AS INTEGER) > 20)
                   OR (c."category" = 'packs' AND IS_DEFINED(c."_MAP"['clearance']))
                """,
                "A disjunction over the partition key: every branch pins a different value, so routing is a set rather than a value, and no conjunct is common to all four."),

            new PlannerQuery(
                "Filter.MixedPushable",
                PlannerQueryCategory.Filter,
                """SELECT * FROM products AS c WHERE c."category" = 'bikes' AND INITCAP(c."id") = 'X' AND c."_ts" > 5""",
                "Two translatable conjuncts around one that is not, which is the split rule's reason to exist: the service applies what it can rather than the plan declining the whole predicate."),

            new PlannerQuery(
                "Filter.UntranslatableWhole",
                PlannerQueryCategory.Filter,
                """SELECT * FROM products AS c WHERE INITCAP(c."id") = 'X' OR INITCAP(c."_etag") = 'Y'""",
                "Nothing translatable and no conjunct to split on, so the container is read whole — the declining path, which every schema spends real time on."),

            new PlannerQuery(
                "Filter.PointReadSet",
                PlannerQueryCategory.Filter,
                """SELECT * FROM products AS c WHERE c."category" = 'bikes' AND c."id" IN ('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p')""",
                "One partition and sixteen ids, which is a set of point reads rather than a query; the candidate has to survive the converter's rewrite of IN into an OR ladder."),

            new PlannerQuery(
                "Filter.PartitionInList",
                PlannerQueryCategory.Filter,
                """SELECT * FROM events AS c WHERE c."tenant" IN ('acme', 'globex', 'initech', 'umbrella') AND c."_ts" > 1700000000""",
                "Four routes and a range, over the container with the most partitions — where getting routing right is worth the most."),

            new PlannerQuery(
                "Filter.TypeTests",
                PlannerQueryCategory.Filter,
                """
                SELECT * FROM products AS c
                WHERE IS_DEFINED(c."_MAP"['dimensions'])
                  AND IS_OBJECT(c."_MAP"['dimensions'])
                  AND IS_ARRAY(c."_MAP"['tags'])
                  AND IS_NUMBER(c."_MAP"['price'])
                  AND IS_STRING(c."_MAP"['sku'])
                  AND NOT IS_NULL(c."_MAP"['brand'])
                """,
                "The type predicates a schemaless container is actually queried with, all six of them at once."),

            new PlannerQuery(
                "Filter.RegexAndLike",
                PlannerQueryCategory.Filter,
                """SELECT * FROM products AS c WHERE REGEXMATCH(c."id", '^a[0-9]+$') AND c."category" LIKE 'bi%' AND CAST(c."_MAP"['sku'] AS VARCHAR) NOT LIKE '%-old'""",
                "String matching in three forms, one of them a Cosmos function and two of them Calcite operators that have to be rendered as one."),

            new PlannerQuery(
                "Filter.NestedKeyContainer",
                PlannerQueryCategory.Filter,
                """SELECT * FROM shipments AS c WHERE c."_MAP"['shipment']['region'] = 'eu-west' AND CAST(c."_MAP"['weight'] AS DOUBLE) > 2.5""",
                "A partition key that promotes to no column, so the predicate that would route is written against the map and the rules take their other branch throughout."),

            new PlannerQuery(
                "Filter.UnindexedPath",
                PlannerQueryCategory.Filter,
                """SELECT * FROM archive AS c WHERE CAST(c."_MAP"['reason'] AS VARCHAR) = 'expired' AND c."category" = 'bikes'""",
                "One conjunct over an excluded path and one over an indexed one, in the largest container: legal either way, and a different cost each way."),

            new PlannerQuery(
                "Filter.CastLadder",
                PlannerQueryCategory.Filter,
                """
                SELECT * FROM products AS c
                WHERE CAST(c."_MAP"['price'] AS DOUBLE) > 10.0
                  AND CAST(c."_MAP"['stock'] AS INTEGER) < 500
                  AND CAST(c."_MAP"['label'] AS VARCHAR) = 'sale'
                  AND CAST(c."_MAP"['listed'] AS DATE) > DATE '2020-01-01'
                  AND CAST(c."_MAP"['active'] AS BOOLEAN)
                """,
                "Five casts to five types, of which the translator takes some and refuses others — the per-cast decision, five times over, in one predicate."),

            // ── Projections ──────────────────────────────────────────────────────

            new PlannerQuery(
                "Project.RelationalView",
                PlannerQueryCategory.Projection,
                """SELECT CAST(c."_MAP"['sku'] AS VARCHAR) AS "sku", CAST(c."_MAP"['price'] AS DOUBLE) AS "price", CAST(c."_MAP"['stock'] AS INTEGER) AS "stock", c."category" AS "cat" FROM products AS c""",
                "What a view over a container is made of: four casts giving four document paths a relational type."),

            new PlannerQuery(
                "Project.DeepPaths",
                PlannerQueryCategory.Projection,
                """SELECT c."_MAP"['a']['b']['c']['d'] AS "deep", c."_MAP"['tags'][1] AS "first", c."_MAP"['metadata']['sku'] AS "sku", c."_MAP"[c."id"] AS "dynamic" FROM products AS c""",
                "Four ways of naming a path — nesting, indexing, both, and a key not known until the row is read."),

            new PlannerQuery(
                "Project.OverFilterAndSort",
                PlannerQueryCategory.Projection,
                """SELECT CAST(c."_MAP"['sku'] AS VARCHAR) AS "sku" FROM products AS c WHERE c."category" = 'bikes' ORDER BY c."_ts" DESC FETCH NEXT 25 ROWS ONLY""",
                "The transpose that decides everything: untransposed the cast blocks the sort from pushing and the container is read whole to answer a page of 25."),

            new PlannerQuery(
                "Project.Arithmetic",
                PlannerQueryCategory.Projection,
                """SELECT c."id", CAST(c."_MAP"['price'] AS DOUBLE) * 1.2 + 5.0 AS "gross", UPPER(CAST(c."_MAP"['sku'] AS VARCHAR)) AS "sku" FROM products AS c WHERE c."category" = 'bikes'""",
                "Expressions over projected columns, one of which renders at the service and one of which does not."),

            // ── Aggregation ──────────────────────────────────────────────────────

            new PlannerQuery(
                "Aggregate.GroupBy",
                PlannerQueryCategory.Aggregate,
                """SELECT c."category", COUNT(*) AS "n" FROM products AS c GROUP BY c."category" """,
                "The one grouping Cosmos expresses natively."),

            new PlannerQuery(
                "Aggregate.GroupByHavingOnKey",
                PlannerQueryCategory.Aggregate,
                """SELECT c."category", COUNT(*) AS "n" FROM products AS c GROUP BY c."category" HAVING c."category" <> 'bikes' """,
                "A HAVING on a grouping key, which transposes below the aggregate into an ordinary WHERE — Cosmos has no HAVING."),

            new PlannerQuery(
                "Aggregate.GroupByHavingOnAggregate",
                PlannerQueryCategory.Aggregate,
                """SELECT c."category", COUNT(*) AS "n" FROM products AS c GROUP BY c."category" HAVING COUNT(*) > 100""",
                "A HAVING on an aggregated value, which does not transpose and must stay outside: the same statement, the opposite decision."),

            new PlannerQuery(
                "Aggregate.Rollup",
                PlannerQueryCategory.Aggregate,
                """SELECT c."category", SUM(CAST(c."_MAP"['price'] AS DOUBLE)) AS "s", MAX(c."_ts") AS "m" FROM products AS c GROUP BY ROLLUP(c."category")""",
                "Two groupings at once, which Cosmos cannot express: the finest one is pushed and the plan rolls the partials up."),

            new PlannerQuery(
                "Aggregate.Cube",
                PlannerQueryCategory.Aggregate,
                """SELECT c."tenant", c."user", COUNT(*) AS "n" FROM events AS c GROUP BY CUBE(c."tenant", c."user")""",
                "Four groupings at once over two keys, which is the split rule's largest case."),

            new PlannerQuery(
                "Aggregate.GroupingSets",
                PlannerQueryCategory.Aggregate,
                """SELECT c."tenant", c."user", COUNT(*) AS "n" FROM events AS c GROUP BY GROUPING SETS ((c."tenant", c."user"), (c."tenant"), ())""",
                "Groupings chosen rather than generated, including the empty one — the partials do not nest as neatly as a rollup's."),

            new PlannerQuery(
                "Aggregate.CountDistinct",
                PlannerQueryCategory.Aggregate,
                """SELECT COUNT(DISTINCT c."category") AS "n" FROM products AS c""",
                "Rewritten into an aggregate over an aggregate, whose inner half is a plain GROUP BY the service answers — one row per distinct value crosses the wire instead of every document."),

            new PlannerQuery(
                "Aggregate.Average",
                PlannerQueryCategory.Aggregate,
                """SELECT c."category", AVG(CAST(c."_MAP"['price'] AS DOUBLE)) AS "a" FROM products AS c GROUP BY c."category" """,
                "AVG, which survives as itself and also as SUM over COUNT; the planner costs both forms and keeps one."),

            new PlannerQuery(
                "Aggregate.RollupAverage",
                PlannerQueryCategory.Aggregate,
                """SELECT c."category", AVG(CAST(c."_MAP"['price'] AS DOUBLE)) AS "a" FROM products AS c GROUP BY ROLLUP(c."category")""",
                "An average that has to be decomposed before it can be rolled up, because an average of averages weights every group equally."),

            new PlannerQuery(
                "Aggregate.ManyAggregates",
                PlannerQueryCategory.Aggregate,
                """
                SELECT c."category",
                       COUNT(*) AS "n",
                       SUM(CAST(c."_MAP"['price'] AS DOUBLE)) AS "total",
                       MIN(CAST(c."_MAP"['price'] AS DOUBLE)) AS "cheapest",
                       MAX(CAST(c."_MAP"['price'] AS DOUBLE)) AS "dearest",
                       AVG(CAST(c."_MAP"['stock'] AS INTEGER)) AS "stock",
                       COUNT(c."_MAP"['clearance']) AS "clearance"
                FROM products AS c
                WHERE c."_ts" > 1700000000
                GROUP BY c."category"
                """,
                "Six aggregates over one grouping, each of which the rule accepts or declines on its own."),

            new PlannerQuery(
                "Aggregate.OverPagedInput",
                PlannerQueryCategory.Aggregate,
                """SELECT COUNT(*) AS "n" FROM (SELECT * FROM products AS p ORDER BY p."_ts" DESC FETCH NEXT 500 ROWS ONLY) AS g""",
                "An aggregate over a page, which must not push: counting after the limit and counting before it are different answers."),

            // ── Ordering and paging ──────────────────────────────────────────────

            new PlannerQuery(
                "Sort.CompositeIndexOrder",
                PlannerQueryCategory.Sort,
                """SELECT * FROM products AS c WHERE c."category" = 'bikes' ORDER BY c."category", c."_ts" DESC FETCH NEXT 50 ROWS ONLY""",
                "A two-key ordering the container declares a composite index for, which is the only two-key ordering the service will answer."),

            new PlannerQuery(
                "Sort.UnsupportedTwoKeyOrder",
                PlannerQueryCategory.Sort,
                """SELECT * FROM products AS c ORDER BY c."_etag", c."id" FETCH NEXT 50 ROWS ONLY""",
                "A two-key ordering no composite index covers, which the service refuses — so the sort stays here and the page cannot be taken there."),

            new PlannerQuery(
                "Sort.OffsetPage",
                PlannerQueryCategory.Sort,
                """SELECT * FROM products AS c WHERE c."category" = 'bikes' ORDER BY c."_ts" DESC OFFSET 200 ROWS FETCH NEXT 20 ROWS ONLY""",
                "A page from the middle, which pushes as OFFSET and LIMIT together or not at all."),

            new PlannerQuery(
                "Sort.ThroughProjection",
                PlannerQueryCategory.Sort,
                """SELECT * FROM (SELECT CAST(c."_MAP"['sku'] AS VARCHAR) AS "sku", c."id" AS "ident" FROM products AS c) AS t ORDER BY t."ident" FETCH NEXT 10 ROWS ONLY""",
                "Ordering by a column the projection passes through, which transposes; ordering by the cast beside it would not, and must not."),

            new PlannerQuery(
                "Sort.ByCastColumn",
                PlannerQueryCategory.Sort,
                """SELECT * FROM (SELECT CAST(c."_MAP"['sku'] AS VARCHAR) AS "sku", c."id" AS "ident" FROM products AS c) AS t ORDER BY t."sku" FETCH NEXT 10 ROWS ONLY""",
                "The half that must not transpose: as text, 10 sorts before 9, so ordering by the cast is not ordering by the path underneath."),

            new PlannerQuery(
                "Sort.DistinctOrdered",
                PlannerQueryCategory.Sort,
                """SELECT DISTINCT c."category" FROM products AS c ORDER BY c."category" """,
                "A de-duplication expressed as a grouping with no aggregate, with an ordering over its output."),

            new PlannerQuery(
                "Sort.LimitOverLimit",
                PlannerQueryCategory.Sort,
                """SELECT * FROM (SELECT * FROM products AS c ORDER BY c."_ts" FETCH NEXT 100 ROWS ONLY) AS x ORDER BY x."id" FETCH NEXT 10 ROWS ONLY""",
                "A page of a page, where only the inner ordering can push and the outer one must re-sort what comes back."),

            // ── Array traversal ──────────────────────────────────────────────────

            new PlannerQuery(
                "Unnest.Traversal",
                PlannerQueryCategory.Unnest,
                """SELECT c."id" FROM products AS c, UNNEST(c."_MAP"['tags']) AS t""",
                "The traversal on its own, which Calcite writes as a correlate over an uncollect and Cosmos as a JOIN in the FROM clause."),

            new PlannerQuery(
                "Unnest.FilteredElement",
                PlannerQueryCategory.Unnest,
                """SELECT c."id" FROM products AS c, UNNEST(c."_MAP"['tags']) AS t WHERE CAST(t AS VARCHAR) = 'steel'""",
                "A predicate over the traversed element, which only pushes once it has been transposed into the correlate — above it, the element has no path to name."),

            new PlannerQuery(
                "Unnest.TwoArrays",
                PlannerQueryCategory.Unnest,
                """SELECT c."id" FROM products AS c, UNNEST(c."_MAP"['tags']) AS t, UNNEST(c."_MAP"['sizes']) AS s WHERE CAST(t AS VARCHAR) = 'steel' AND CAST(s AS VARCHAR) = 'L'""",
                "Two traversals of one document with a predicate over each, which is two correlates the rule has to accept in sequence."),

            new PlannerQuery(
                "Unnest.OverPagedInput",
                PlannerQueryCategory.Unnest,
                """SELECT c."id" FROM (SELECT * FROM products AS p ORDER BY p."_ts" FETCH NEXT 50 ROWS ONLY) AS c, UNNEST(c."_MAP"['tags']) AS t""",
                "A traversal over a page, which must not push: the page is taken before the traversal, and pushing would take it after."),

            new PlannerQuery(
                "Unnest.GroupedElements",
                PlannerQueryCategory.Unnest,
                """SELECT CAST(t AS VARCHAR) AS "tag", COUNT(*) AS "n" FROM products AS c, UNNEST(c."_MAP"['tags']) AS t WHERE c."category" = 'bikes' GROUP BY CAST(t AS VARCHAR)""",
                "Grouping by the traversed element, which puts a filter, a traversal and an aggregate in one statement — three rules that each have to accept the other two's output."),

            // ── Search ───────────────────────────────────────────────────────────

            new PlannerQuery(
                "Search.FullTextContains",
                PlannerQueryCategory.Search,
                """SELECT c."id" FROM products AS c WHERE FULLTEXTCONTAINS(c."_MAP"['name'], 'steel') AND c."category" = 'bikes'""",
                "A full text predicate over a declared path beside a routing one — pushable only because the container declares the path."),

            new PlannerQuery(
                "Search.FullTextUndeclaredPath",
                PlannerQueryCategory.Search,
                """SELECT c."id" FROM products AS c WHERE FULLTEXTCONTAINS(c."_MAP"['notes'], 'steel')""",
                "The same predicate over a path the container says nothing about, which the service would refuse — so the rule declines it and the plan reads the container."),

            new PlannerQuery(
                "Search.FullTextContainsAll",
                PlannerQueryCategory.Search,
                """SELECT c."id" FROM products AS c WHERE FULLTEXTCONTAINSALL(c."_MAP"['description'], 'steel', 'frame', 'road', 'touring')""",
                "A variadic full text predicate, whose operand count the rule has to check against the service's limit."),

            new PlannerQuery(
                "Search.RankedByScore",
                PlannerQueryCategory.Search,
                """SELECT c."id" FROM products AS c ORDER BY FULLTEXTSCORE(c."_MAP"['name'], 'steel') FETCH FIRST 10 ROWS ONLY""",
                "Three Calcite nodes — a projected score, an ordering, a limit — that are one Cosmos clause, and whose middle node is a statement the service rejects."),

            new PlannerQuery(
                "Search.ReciprocalRankFusion",
                PlannerQueryCategory.Search,
                """SELECT c."id" FROM products AS c ORDER BY RRF(FULLTEXTSCORE(c."_MAP"['name'], 'steel'), FULLTEXTSCORE(c."_MAP"['tags'], 'frame')) FETCH FIRST 10 ROWS ONLY""",
                "Two scores fused into one ranking, which is the same shape again with a scoring function that takes scoring functions."),

            new PlannerQuery(
                "Search.HybridWithFilter",
                PlannerQueryCategory.Search,
                """
                SELECT c."id" FROM products AS c
                WHERE c."category" = 'bikes' AND FULLTEXTCONTAINS(c."_MAP"['description'], 'steel')
                ORDER BY RRF(FULLTEXTSCORE(c."_MAP"['name'], 'steel'), FULLTEXTSCORE(c."_MAP"['description'], 'frame'))
                FETCH FIRST 20 ROWS ONLY
                """,
                "Routing, a full text predicate and a fused ranking in one statement, which is what a search box over a catalogue actually is."),

            new PlannerQuery(
                "Search.VectorDistance",
                PlannerQueryCategory.Search,
                """SELECT c."id" FROM products AS c WHERE VECTORDISTANCE(c."_MAP"['embedding'], c."_MAP"['query']) < 0.5 AND c."category" = 'bikes'""",
                "A vector predicate over a declared vector path, which is gated on the declaration the same way full text is."),

            // ── Joins ────────────────────────────────────────────────────────────

            new PlannerQuery(
                "Join.LookupByKey",
                PlannerQueryCategory.Join,
                """SELECT o."id", c."_MAP" FROM orders AS o JOIN customers AS c ON o."customer" = c."id" WHERE o."customer" = 'acme'""",
                "The join the adapter exists to make cheap: the probe side supplies keys, and the build side is a container whose partition key is id — so the keys are fetched rather than the container read."),

            new PlannerQuery(
                "Join.NonKeyEquality",
                PlannerQueryCategory.Join,
                """SELECT o."id", p."id" FROM orders AS o JOIN products AS p ON o."customer" = p."category" """,
                "An equality that is not the build side's key, which fetching cannot answer — so the container is read and the join happens here."),

            new PlannerQuery(
                "Join.Inequality",
                PlannerQueryCategory.Join,
                """SELECT o."id", p."id" FROM orders AS o JOIN products AS p ON o."id" < p."id" """,
                "A join with no equality at all, which nothing about the adapter helps with and the planner must not think it does."),

            new PlannerQuery(
                "Join.LeftOuter",
                PlannerQueryCategory.Join,
                """SELECT o."id", c."_MAP" FROM orders AS o LEFT JOIN customers AS c ON o."customer" = c."id" """,
                "The same key equality under an outer join, where a missing key is a row of nulls rather than no row."),

            new PlannerQuery(
                "Join.ThreeWay",
                PlannerQueryCategory.Join,
                """
                SELECT o."id", c."id", p."id"
                FROM orders AS o
                JOIN customers AS c ON o."customer" = c."id"
                JOIN products AS p ON o."id" = p."id"
                WHERE o."_ts" > 1700000000
                """,
                "Three containers and two joins, which is where the planner starts costing orders rather than shapes."),

            new PlannerQuery(
                "Join.FourWayWithFilters",
                PlannerQueryCategory.Join,
                """
                SELECT o."id", c."id", p."category", a."id"
                FROM orders AS o
                JOIN customers AS c ON o."customer" = c."id"
                JOIN products AS p ON o."id" = p."id"
                JOIN archive AS a ON p."category" = a."category"
                WHERE o."_ts" > 1700000000 AND p."category" = 'bikes' AND a."_ts" < 1600000000
                """,
                "Four containers of wildly different sizes with a pushable predicate on three of them — the join order is a real decision and the statistics are what decides it."),

            new PlannerQuery(
                "Join.AggregateOverJoin",
                PlannerQueryCategory.Join,
                """
                SELECT c."id", COUNT(*) AS "n", SUM(CAST(o."_MAP"['total'] AS DOUBLE)) AS "spend"
                FROM orders AS o JOIN customers AS c ON o."customer" = c."id"
                WHERE o."_ts" > 1700000000
                GROUP BY c."id"
                HAVING COUNT(*) > 5
                """,
                "A join under an aggregate under a HAVING, where only the leaves push and everything above them has to be costed anyway."),

            // ── Set operations ───────────────────────────────────────────────────

            new PlannerQuery(
                "SetOp.UnionAll",
                PlannerQueryCategory.SetOp,
                """SELECT c."id" FROM products AS c WHERE c."category" = 'bikes' UNION ALL SELECT a."id" FROM archive AS a WHERE a."category" = 'bikes'""",
                "Two pushed branches under an operation Cosmos does not have, so the statements are separate and the concatenation happens here."),

            new PlannerQuery(
                "SetOp.UnionDistinct",
                PlannerQueryCategory.SetOp,
                """SELECT c."category" FROM products AS c UNION SELECT a."category" FROM archive AS a""",
                "The same, plus a de-duplication that the planner may or may not decide to push into each branch first."),

            new PlannerQuery(
                "SetOp.Intersect",
                PlannerQueryCategory.SetOp,
                """SELECT c."id" FROM products AS c WHERE c."category" = 'bikes' INTERSECT SELECT a."id" FROM archive AS a WHERE a."category" = 'bikes'""",
                "An intersection, which Calcite may rewrite into a join and may not — and either way costs the branches the same."),

            new PlannerQuery(
                "SetOp.Except",
                PlannerQueryCategory.SetOp,
                """SELECT c."id" FROM products AS c EXCEPT SELECT a."id" FROM archive AS a WHERE a."_ts" < 1600000000""",
                "A difference, whose right branch is the 900-million-document container: an order the cost model has an opinion about."),

            // ── Subqueries ───────────────────────────────────────────────────────

            new PlannerQuery(
                "Subquery.InList",
                PlannerQueryCategory.Subquery,
                """SELECT c."id" FROM products AS c WHERE c."id" IN (SELECT o."id" FROM orders AS o WHERE o."customer" = 'acme')""",
                "An uncorrelated IN, which the converter turns into a semi-join before any Cosmos rule sees it."),

            new PlannerQuery(
                "Subquery.CorrelatedExists",
                PlannerQueryCategory.Subquery,
                """SELECT c."id" FROM products AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o."id" = c."id" AND o."_ts" > 1700000000)""",
                "A correlated EXISTS, which is decorrelated into a join whose build side has a pushable predicate."),

            new PlannerQuery(
                "Subquery.NotExists",
                PlannerQueryCategory.Subquery,
                """SELECT c."id" FROM products AS c WHERE NOT EXISTS (SELECT 1 FROM archive AS a WHERE a."id" = c."id")""",
                "An anti-join, whose null semantics stop several of the rewrites the positive form allows."),

            new PlannerQuery(
                "Subquery.ScalarCorrelated",
                PlannerQueryCategory.Subquery,
                """SELECT c."id", (SELECT COUNT(*) FROM orders AS o WHERE o."customer" = c."category") AS "n" FROM products AS c WHERE c."category" = 'bikes'""",
                "A scalar subquery per row, which decorrelates into an aggregate under a left join — the largest rewrite the converter performs."),

            new PlannerQuery(
                "Subquery.DerivedTableJoin",
                PlannerQueryCategory.Subquery,
                """
                SELECT t."cat", t."n", p."id"
                FROM (SELECT c."category" AS "cat", COUNT(*) AS "n" FROM products AS c GROUP BY c."category") AS t
                JOIN products AS p ON p."category" = t."cat"
                WHERE t."n" > 10
                """,
                "An aggregate joined back to the container it aggregated, where the same scan appears twice and the planner has to cost it twice."),

            // ── Window functions ─────────────────────────────────────────────────

            new PlannerQuery(
                "Window.RowNumber",
                PlannerQueryCategory.Window,
                """SELECT c."id", ROW_NUMBER() OVER (PARTITION BY c."category" ORDER BY c."_ts" DESC) AS "rn" FROM products AS c WHERE c."category" = 'bikes'""",
                "A window over a pushed scan: nothing about the window pushes, and the question is whether the filter under it still does."),

            new PlannerQuery(
                "Window.RankedTopPerGroup",
                PlannerQueryCategory.Window,
                """
                SELECT t."id", t."cat" FROM (
                    SELECT c."id" AS "id", c."category" AS "cat",
                           RANK() OVER (PARTITION BY c."category" ORDER BY CAST(c."_MAP"['price'] AS DOUBLE) DESC) AS "r"
                    FROM products AS c WHERE c."_ts" > 1700000000
                ) AS t
                WHERE t."r" <= 3
                """,
                "Top-n-per-group, which is a window under a filter that cannot be pushed past it — the filter has to stay above and the scan below still has to push."),

            new PlannerQuery(
                "Window.RunningTotal",
                PlannerQueryCategory.Window,
                """SELECT c."id", SUM(CAST(c."_MAP"['total'] AS DOUBLE)) OVER (PARTITION BY c."customer" ORDER BY c."_ts" ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS "running" FROM orders AS c WHERE c."customer" = 'acme'""",
                "An aggregate window with an explicit frame, which needs its input ordered — a collation the plan has to produce or sort for."),

            // ── Writes ───────────────────────────────────────────────────────────

            new PlannerQuery(
                "Dml.InsertValues",
                PlannerQueryCategory.Dml,
                """INSERT INTO archive ("id", "category") VALUES ('1', 'books')""",
                "A write of one row, which never enters the convention: Cosmos SQL has no DML, so this is item CRUD over a row source."),

            new PlannerQuery(
                "Dml.InsertSelect",
                PlannerQueryCategory.Dml,
                """INSERT INTO archive ("_MAP") SELECT p."_MAP" FROM products AS p WHERE p."category" = 'bikes' """,
                "A write whose source is a read that does push — one statement, one side of it at the service and the other side item by item."),

            new PlannerQuery(
                "Dml.Update",
                PlannerQueryCategory.Dml,
                """UPDATE products SET "_MAP" = "_MAP" WHERE "category" = 'bikes' AND "id" = 'y' """,
                "An update of the document itself, which is a replace. A SET naming the partition key or id is declined at planning rather than at the service, so the statement that reads most naturally is one that has no plan."),

            new PlannerQuery(
                "Dml.DeleteByPartition",
                PlannerQueryCategory.Dml,
                """DELETE FROM products WHERE "category" = 'bikes' """,
                "A delete confined to one partition, which the service has a bulk operation for and a row-by-row plan does not need."),

            // ── Whole statements ─────────────────────────────────────────────────

            new PlannerQuery(
                "Composite.CatalogueReport",
                PlannerQueryCategory.Composite,
                """
                SELECT t."cat" AS "cat", t."tag" AS "tag", t."n" AS "n", t."spend" AS "spend"
                FROM (
                    SELECT p."category" AS "cat",
                           CAST(g AS VARCHAR) AS "tag",
                           COUNT(*) AS "n",
                           SUM(CAST(o."_MAP"['total'] AS DOUBLE)) AS "spend"
                    FROM products AS p, UNNEST(p."_MAP"['tags']) AS g
                    JOIN orders AS o ON o."id" = p."id"
                    WHERE p."category" IN ('bikes', 'shoes')
                      AND CAST(p."_MAP"['price'] AS DOUBLE) > 20.0
                      AND o."_ts" > 1700000000
                    GROUP BY p."category", CAST(g AS VARCHAR)
                ) AS t
                WHERE t."n" > 5
                ORDER BY t."spend" DESC
                FETCH NEXT 50 ROWS ONLY
                """,
                "A filter, a traversal, a join, a grouping, a HAVING, an ordering and a page in one statement — every rule in the set, in the arrangement an application writes."),

            new PlannerQuery(
                "Composite.TenantFunnel",
                PlannerQueryCategory.Composite,
                """
                SELECT t."tenant" AS "tenant", t."user" AS "user", t."n" AS "n", t."rank" AS "rank"
                FROM (
                    SELECT s."tenant" AS "tenant", s."user" AS "user", s."n" AS "n",
                           RANK() OVER (PARTITION BY s."tenant" ORDER BY s."n" DESC) AS "rank"
                    FROM (
                        SELECT e."tenant" AS "tenant", e."user" AS "user", COUNT(*) AS "n"
                        FROM events AS e
                        WHERE e."tenant" IN ('acme', 'globex')
                          AND e."_ts" > 1700000000
                          AND IS_DEFINED(e."_MAP"['step'])
                          AND CAST(e."_MAP"['step'] AS VARCHAR) <> 'abandoned'
                        GROUP BY e."tenant", e."user"
                    ) AS s
                ) AS t
                WHERE t."rank" <= 10
                ORDER BY t."tenant", t."rank"
                """,
                "Three levels of derived table over the most-partitioned container: a routed and filtered grouping, a window over it, and a filter over that."),

            new PlannerQuery(
                "Composite.HybridSearchJoin",
                PlannerQueryCategory.Composite,
                """
                SELECT p."id" AS "id", c."_MAP" AS "customer"
                FROM (
                    SELECT x."id" AS "id", x."category" AS "category"
                    FROM products AS x
                    WHERE x."category" = 'bikes'
                      AND FULLTEXTCONTAINSALL(x."_MAP"['description'], 'steel', 'frame')
                      AND VECTORDISTANCE(x."_MAP"['embedding'], x."_MAP"['query']) < 0.4
                    ORDER BY RRF(FULLTEXTSCORE(x."_MAP"['name'], 'steel'), FULLTEXTSCORE(x."_MAP"['tags'], 'frame'))
                    FETCH FIRST 25 ROWS ONLY
                ) AS p
                JOIN customers AS c ON c."id" = p."id"
                """,
                "A ranked hybrid search whose page becomes the probe side of a lookup join — the two most valuable pushdowns the adapter has, in one statement."),

            new PlannerQuery(
                "Composite.ArchiveReconciliation",
                PlannerQueryCategory.Composite,
                """
                SELECT u."cat" AS "cat", COUNT(*) AS "n"
                FROM (
                    SELECT p."category" AS "cat", p."id" AS "id" FROM products AS p WHERE p."_ts" > 1700000000
                    UNION ALL
                    SELECT a."category" AS "cat", a."id" AS "id" FROM archive AS a WHERE a."_ts" < 1600000000
                ) AS u
                JOIN orders AS o ON o."id" = u."id"
                WHERE u."cat" IN ('bikes', 'shoes', 'tents')
                GROUP BY u."cat"
                ORDER BY COUNT(*) DESC
                """,
                "A union of two pushed branches joined to a third container and grouped, where the filter above the union can be pushed into both branches or neither."),

            new PlannerQuery(
                "Composite.WideDisjunction",
                PlannerQueryCategory.Composite,
                """
                SELECT c."id" AS "id", c."category" AS "cat"
                FROM products AS c
                WHERE (c."category" = 'bikes' AND CAST(c."_MAP"['price'] AS DOUBLE) BETWEEN 100.0 AND 500.0)
                   OR (c."category" = 'shoes' AND CAST(c."_MAP"['size'] AS INTEGER) IN (40, 41, 42, 43))
                   OR (c."category" = 'tents' AND FULLTEXTCONTAINS(c."_MAP"['description'], 'ultralight'))
                   OR (c."category" = 'packs' AND IS_DEFINED(c."_MAP"['clearance']) AND NOT IS_NULL(c."_MAP"['clearance']))
                   OR (c."category" = 'tools' AND REGEXMATCH(c."id", '^t[0-9]+$'))
                   OR (c."id" IN (SELECT o."id" FROM orders AS o WHERE o."customer" = 'acme' AND o."_ts" > 1700000000))
                ORDER BY c."_ts" DESC
                FETCH NEXT 100 ROWS ONLY
                """,
                "Six alternatives of five different shapes, one of them a subquery — the predicate a faceted search builds, and the widest disjunction the translator is asked to render."),
        };

        /// <summary>
        /// The statements that also plan wholly inside the Cosmos convention.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A subset rather than a predicate, because it is a claim about the adapter and not a
        /// property of the statement: these are the ones the service answers by itself, with nothing
        /// left for the plan to do but read rows. <c>verify</c> checks the list against what the
        /// planner actually does and reports both directions of drift, so a rule that quietly stops
        /// pushing something is a failed check rather than a benchmark that got faster.
        /// </para>
        /// <para>
        /// It is what <see cref="Benchmarks.PushdownBenchmarks"/> and
        /// <see cref="Benchmarks.ImplementBenchmarks"/> can be run over at all: one needs a statement
        /// with a plan in the convention, and the other needs a subtree to render.
        /// </para>
        /// </remarks>
        static readonly string[] WhollyPushedNames =
        {
            "Filter.PartitionPin",
            "Filter.PointRead",
            "Filter.HierarchicalFullPin",
            "Filter.HierarchicalPrefixPin",
            "Filter.HierarchicalGap",
            "Filter.PointReadSet",
            "Filter.PartitionInList",
            "Filter.TypeTests",
            "Filter.UnindexedPath",
            "Project.OverFilterAndSort",
            "Aggregate.GroupBy",
            "Aggregate.GroupByHavingOnKey",
            "Sort.OffsetPage",
            "Sort.ThroughProjection",
            "Unnest.Traversal",
            "Unnest.FilteredElement",
            "Unnest.TwoArrays",
            "Search.FullTextContains",
            "Search.FullTextContainsAll",
            "Search.RankedByScore",
            "Search.ReciprocalRankFusion",
            "Search.HybridWithFilter",
            "Search.VectorDistance",
        };

        /// <summary>
        /// Gets every statement in the corpus.
        /// </summary>
        public static IReadOnlyList<PlannerQuery> All => Corpus;

        /// <summary>
        /// Gets the statements that plan wholly inside the Cosmos convention.
        /// </summary>
        public static IEnumerable<PlannerQuery> WhollyPushed => WhollyPushedNames.Select(Get);

        /// <summary>
        /// Determines whether a statement is expected to plan wholly inside the Cosmos convention.
        /// </summary>
        /// <param name="query">The statement.</param>
        /// <returns><c>true</c> where it is expected to.</returns>
        public static bool IsWhollyPushed(PlannerQuery query) => WhollyPushedNames.Contains(query.Name);

        /// <summary>
        /// Returns the statements in a category.
        /// </summary>
        /// <param name="category">The category.</param>
        /// <returns>The statements.</returns>
        public static IEnumerable<PlannerQuery> In(PlannerQueryCategory category) =>
            Corpus.Where(q => q.Category == category);

        /// <summary>
        /// Returns the statements in any of the given categories, in corpus order.
        /// </summary>
        /// <param name="categories">The categories.</param>
        /// <returns>The statements.</returns>
        public static IEnumerable<PlannerQuery> In(params PlannerQueryCategory[] categories) =>
            Corpus.Where(q => categories.Contains(q.Category));

        /// <summary>
        /// Returns the statement with the given name.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>The statement.</returns>
        public static PlannerQuery Get(string name) => Corpus.Single(q => q.Name == name);

    }

}
