# 23. Limitations and known issues

The adapter is under active development. This chapter collects what it does not do, so nothing here
is a surprise in production. Each item says whether it is a property of Cosmos (and will not change), a
deliberate choice, or work not done yet. The open work is tracked in
[`TODO.md`](../TODO.md).

## Scope

| | |
| --- | --- |
| **Cosmos DB for NoSQL only** | The MongoDB, Cassandra, Gremlin and Table APIs have their own query languages. *Scope.* |
| **No change feed** | Reading the change feed as a stream or table function is not offered. *Not done.* |
| **No stored procedures, triggers or JavaScript UDFs** | *Not done.* |
| **No continuation tokens** | A query is read to its end within one execution; a result cannot be resumed later from a token. *Not done.* |

## Querying

| | |
| --- | --- |
| **Relational joins, `UNION`, `INTERSECT`, `EXCEPT`** | Always in process. A join against a container can be a lookup join or a self-join merge (Chapter 12). *Cosmos.* |
| **Sorting with nulls last ascending / first descending** | In process. Cosmos has one null placement. *Cosmos.* |
| **Sorting on the promoted partition key column** | Declined; sort on `JSON_VALUE` of the path. *Calcite has no order for `VARIANT`.* |
| **An ordering comparison over the promoted partition key column; a type test of anything but `JSON_VALUE` or `JSON_QUERY` of a path** | The statement fails, as it fails in Calcite; compare or test the path (Chapter 7.3, Appendix B.5). *Calcite has no order for `VARIANT` and no in-process body for such a test, so it fails on every row; the adapter answers as Calcite does rather than send the service a filter Calcite cannot answer.* |
| **On Azure, an ordering comparison over the promoted partition key column against a parameter** | Sent, and answered by the service, where Calcite fails to compile it; on the emulator it fails as in Calcite. Compare the path. *Not done: a comparison with a parameter is planned outside the reasoning that keeps the one with a literal in Calcite.* |
| **A filter the schema says no document satisfies, where the adapter cannot show that nothing in the request's `WHERE` fails** (a comparison of two properties, a function's result, a cast it has no measurement of, a weaker form sent beside a part the service cannot take exactly) | Sent, one request that finds nothing, or kept in Calcite, which fails as it does, rather than left unsent (Chapter 14.2). *A request is left out only where the service's answer is known, and on a document Calcite fails on the service may answer anything.* |
| **A part of a `WHERE` the schema, or what the service guarantees, rules out that the target sends no form of** — a null test of a `LIKE` over `id` the target sends no form of, of an ordering of `id` the emulator keeps, or of a `COALESCE` over `id`, which every document holding an `id` rules out | One request that reads every document the rest of the `WHERE` keeps, the whole container where that part is all of it, Calcite finding nothing there, where none is needed: the answer is the same (Chapter 14.2). *Not done: whether a request goes out is asked of the `WHERE` it sends, which holds no such part, and the reasoning reads no `COALESCE`.* |
| **A filter part of which stays in process, beside a part sent that keeps nothing** | Answered with no rows, a `COUNT` over it with 0, even where Calcite fails on a row the part kept in process reads, with a schema or without; with one, no request is made. A part kept that fails before it reads a row, a type test of anything but a path, fails as in Calcite (Chapter 14.2, Appendix B.5). *Not decided: the part kept reads only the rows the part sent returns, which here are none, request or not; failing as Calcite does would mean reading the whole container in process.* |
| **A condition the schema makes true wherever it does not fail, in a projected `CASE` or beside a `DELETE` of a whole partition** — `NOT (CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) = 1 AND JSON_VALUE(c."DOC", '$.kind') = 'C')` where no document's `kind` is `C` | In a `WHERE` the plan keeps it, and where it stays in Calcite it fails as Calcite does; one the service takes is left out of the request, and nothing evaluates it. In a projected `CASE` condition it is dropped from the request and the column answered, even where Calcite fails on the condition, and a filter above such a projection then reads the column's answer; beside `DELETE … WHERE c."$.category" = 'bikes'`, the whole partition is deleted where Calcite fails and deletes nothing (Chapters 13, 14.2). *Not done: a condition is dropped as implied before anything asks whether it can fail.* |
| **A semi-join whose outer side keeps nothing, over a subquery that can fail** — `WHERE JSON_VALUE(c."DOC", '$.kind') = 'C' AND c."id" IN (SELECT d."id" FROM … WHERE CAST(JSON_VALUE(d."DOC", '$.body') AS INTEGER) = 1)`, its correlated `EXISTS`, or an `IN` over the cast in the subquery's select list, where no document's `kind` is `C`; or an `EXISTS` whose subquery keeps nothing beside such a cast | Fails on the cast, with a schema or without one, where Calcite in process answers no rows: the lookup join reads the subquery first, or the join the side beside the `EXISTS`, where Calcite's own plan reads the empty side first and never evaluates the cast; with a schema, the empty side's request or probes are not sent (Chapters 12.1, 14.2). *Not decided: the lookup join is chosen by cost, and nothing yet asks whether the side it reads first can fail.* |
| **A part of a `WHERE` kept in process beside a part sent, which Calcite would have simplified together** — `SELECT … FROM (SELECT c."id", c."DOC" FROM … AS c WHERE CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) = 1 AND JSON_VALUE(c."DOC", '$.kind') IN ('A', 'B')) AS x WHERE JSON_VALUE(x."DOC", '$.kind') = 'C'` | Fails on a `body` that is no number, with a schema or without one, where Calcite reads the three conditions together as false and answers no rows: the `IN` is sent, and the cast evaluated in process beside `kind = 'C'` alone (Chapter 14.2). The part kept is the rest of the condition, as Calcite's simplification of the whole reads it beside the part sent: `kind = 'A' AND (kind = 'A' OR CAST(… AS INTEGER) = 1)` keeps nothing above `kind = 'A'`. *Not done: across two filters the part kept is simplified without the part sent beneath the filter above.* |
| **Over an `UNNEST` or a lateral, a `WHERE` that reads the document and the element together** | May answer the rows the condition keeps where Calcite in process fails, or fail where it answers, with a schema or without one: the plan the adapter sends and the plan Calcite in process takes may move different parts of such a `WHERE`, or terms Calcite derives from it, beneath its traversals, and a part moved there keeps a document from reaching what Calcite's own plan may fail on: a cast, or the traversal of an array holding a value that is no string, or of an object where the array is. Where a condition on the document is sent beside the traversal, a part kept in process reads only the documents the request returns, so the query can answer where Calcite fails on that part (Chapter 14.2). *Not done: which plan Calcite takes is its own planner's choice.* |
| **Through a connection that runs Calcite's default rules, a part of a `WHERE` kept in process that tests what a part sent holds** — `JSON_VALUE(c."DOC", '$.flag' RETURNING BOOLEAN) AND ((JSON_VALUE(c."DOC", '$.flag' RETURNING BOOLEAN) IS NOT NULL OR CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) = 1) IS NOT TRUE)` | No rows and no request, with a schema or without one, where Calcite in process fails on a `body` that is no number; without the `IS NOT TRUE`, the rows the part sent keeps. Those rules read the filter left in process beside what the part sent holds, which Calcite in process, sending nothing, never reads (Chapter 14.2). *Not done: keeping those rules from reading what the part sent holds moves the plans such a connection makes.* |
| **A filter on a grouping's key over a subquery that computes a cast for each group** — `SELECT x.k, x.m FROM (SELECT JSON_VALUE(c."DOC", '$.kind') AS k, MAX(CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER)) AS m FROM … AS c WHERE JSON_VALUE(c."DOC", '$.n') = '1' GROUP BY JSON_VALUE(c."DOC", '$.kind')) AS x WHERE x.k = 'B'` | Fails on a `body` that is no number in a group the filter drops, with a schema or without one, where Calcite moves the filter beneath the grouping, computes the cast for the groups it keeps and answers no rows (Chapter 14.2). *Not decided: which side of the grouping the filter takes is chosen by cost, and nothing asks whether what the grouping computes can fail.* |
| **A clause over a `WHERE` the schema rules out, or a part of such a `WHERE`, that the adapter pushes only on what it knows of the rows the `WHERE` keeps** — `SELECT UPPER(JSON_VALUE(c."DOC", '$.body')) … WHERE JSON_VALUE(c."DOC", '$.kind') = 'C' AND JSON_VALUE(c."DOC", '$.body') = 'abc'`, where no `kind` is `C`; a `GROUP BY` of `kind` with its `COUNT(*)`, a `DISTINCT` of `kind`, a `MIN`, a `MAX` or an `ORDER BY` over such a `WHERE`; `UPPER(JSON_VALUE(c."DOC", '$.body')) = 'ABC'` beside `JSON_VALUE(c."DOC", '$.body') = 'abc'` and such a `kind` | Computed in Calcite over the empty answer where, without a schema or beside a `WHERE` the schema admits, the service computes it; the same answer, and a plan the schema changes (Chapter 14.2). *Not done: what the rows hold is read assuming the `WHERE`, which then holds nothing, and what the adapter sends is admitted part by part, each beside what the parts before it say, which ends at a part that keeps nothing.* |
| **A condition on a container's side that stays above a lookup join** — `WHERE CAST(JSON_VALUE(d."DOC", '$.body') AS INTEGER) = 1 AND d."id" IN (SELECT …)` | Evaluated only on the documents the keys fetch, where Calcite evaluates it on every document, so a query Calcite fails on is answered with no rows where the subquery matches nothing; with a schema ruling the subquery out, no request is made (Chapter 12.1). *Not decided: the lookup join is chosen by cost, and nothing yet asks whether such a condition can fail.* |
| **A cast in the select list beneath a sort or a limit, under a filter that keeps nothing** — `SELECT … FROM (SELECT c."id", JSON_VALUE(c."DOC", '$.kind') AS k, CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) AS w FROM … ORDER BY c."id" FETCH FIRST 5 ROWS ONLY) AS x WHERE x.k = 'C'` | The plan computes the cast above the filter, which keeps no row, so it is never evaluated and the query answers no rows where Calcite, computing it beneath the sort for every document, fails (Chapter 14.2). *Not done: keeping such a residual where Calcite evaluates it moves plans, and is its own change.* |
| **A filter on a `DISTINCT`'s keys over a cast the `DISTINCT` computes** — `SELECT y.k FROM (SELECT x.k FROM (SELECT DISTINCT JSON_VALUE(c."DOC", '$.kind') AS k, CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) AS w FROM … AS c) AS x WHERE x.k IS NOT NULL) AS y WHERE y.k IS NULL` | The plan moves the filter beneath the `DISTINCT` into the request, which keeps nothing, and the cast reads no row, where Calcite keeps the filter above the `DISTINCT` and fails (Chapter 14.2). *Not decided: which side of the `DISTINCT` the filter takes is chosen by cost.* |
| **A side beside a join, a set operation or a lateral, whose request keeps nothing** | Its request is not made, and the plan reads the other side as it would over the service's empty answer, one request even where nothing there can fail, and fails where that side fails, a semi-join's subquery among them (above). *Not done: nothing yet asks whether the other side can fail, which would let the host leave it unread.* |
| **A filter Calcite fails on for some documents, or for every one** — `RETURNING DOUBLE` over a property declared `"integer"`, which Calcite fails on for every whole number within 64 bits, since the service returns one as its digits however it was written, whether the schema bounds the integers, lists them or neither; `RETURNING BIGINT` over integers within an `INTEGER`'s range; a cast to `DOUBLE` over an `enum` that lists a number beside a string or a boolean the cast cannot read, `CAST(JSON_VALUE(c."DOC", '$.n') AS DOUBLE) > 2` where the schema lists `[1, 2, 3, "XL"]` | Sent, and answered by what the service finds (Chapters 7.3, 7.4, 14.2). *Not decided: the reasoning reads such a `RETURNING` as it reads a column of its type, which reads those numbers, and an answer beside a failure as the answer.* |
| **Sorting on `JSON_VALUE` as text over a path no schema declares a string** | In process. *The service orders the stored value, by JSON type and numbers by value; SQL orders the text.* |
| **Sorting on a `JSON_QUERY`, `DOC` or array column** | In process. *The service orders objects and arrays by rules of its own.* |
| **Sorting, comparing against a parameter, or taking `MIN`/`MAX` of strings whose characters nothing states** | Pushed, and checked as the rows arrive — over `id` too: a page or a keyset seek on `id` is one statement and one page on ordinary text, and reads again only where a character at or above U+D800 lands at a page's edge (Chapters 9.3, 19.8). Proved outright where a schema `pattern` rules the characters out (Chapter 14.4), or a schema `enum` or `const`, a declared stored form or the `WHERE` lists the values. *Cosmos orders strings by code point and Calcite by UTF-16 code unit, which part where a character above U+FFFF meets one in U+E000–U+FFFF.* |
| **A comparison with a parameter under a `NOT`, or beneath a grouping, a `DISTINCT` or a ranking, over strings whose characters nothing states** | In process. *The reader rechecks the conjunct a comparison it cannot prove sits in, over the rows the statement returns: a weaker comparison under `AND` and `OR` makes a weaker conjunct, and under a `NOT` a stronger one; and a grouping's rows are not the filter's.* |
| **A grouped `MIN` or `MAX` of strings whose characters nothing states** | In process. *There is no one row to check.* |
| **Comparing two paths' strings whose characters nothing states** | In process. *The rows the service drops never arrive to be checked.* |
| **`=`, `<=` or `>=` between two values that may both be a JSON null** | In process. *Cosmos holds a JSON null equal to a JSON null; SQL's comparison with a null is unknown.* |
| **Multi-key sorts** | Need a matching composite index; a key the `WHERE` pins, or one repeated, is not sent and does not count. *Cosmos.* |
| **Sorting on a path the indexing policy does not index** | In process: `_etag`, which every policy excludes, an excluded path, and a computed property no included path names. *Cosmos — a 400 on Azure; the emulator accepts it.* |
| **Sorting on `RETURNING BIGINT` over an integer the schema does not bound within int64** | Pushed, and checked as the rows arrive: a number within ±2^53 certifies the order around it, and the rows between two such are re-sorted. *A number past int64 is read back rounded below one inside it.* |
| **Sorting on a computed expression** | In process, except a geodesic distance. *Cosmos.* |
| **`GROUP BY` with `ORDER BY`** | The groups are sorted in process. *Cosmos.* |
| **`SUM`, `AVG` over nullable inputs; `MIN`, `MAX` over a path that may hold a JSON null or two types** | In process — Cosmos's null semantics differ, and its `MIN` and `MAX` compare by JSON type first (Chapter 10.2). *Cosmos; a guarded rewrite is possible and not built.* |
| **`FILTER` clauses and `DISTINCT` inside aggregates** (other than `COUNT(DISTINCT)`) | In process. *Cosmos.* |
| **Other aggregates** (`STDDEV`, `VARIANCE`, `BOOL_AND`, `LISTAGG`, …) | In process. Several are expressible in Cosmos's five and are not rewritten yet. *Not done.* |
| **Subqueries** (`EXISTS`, `IN (SELECT …)`, scalar subqueries) | In process. *Cosmos subqueries are document-scoped; mapping the item-scoped forms is not done.* |
| **Temporal functions** (`EXTRACT`, `TIMESTAMPADD`, …) | In process. *Not done — needs the stored shape.* |
| **Array literals** (`ARRAY['a','b']`) | Not rendered, so functions taking one stay in process. *Not done.* |
| **Many library functions** | In process where Cosmos has no counterpart; some have one and are not mapped yet (Appendix B). |
| **`IS TRUE`, `IS FALSE` over anything but a declared boolean; `IS DISTINCT FROM`** | In process. *Not measured.* |
| **Partition routing from parameters** | Only literal partition key values route a statement; a parameter does not. *Not done.* |
| **A comparison through a cast to `DOUBLE` that the schema rules out** — `CAST(JSON_VALUE(c."DOC", '$.n') AS DOUBLE) > 100` where `n` is an integer at most 100 | May be sent, one request that finds nothing, and on the partition key may be routed to a partition where it finds nothing (Chapters 7.3, 14.2). *Not done.* |
| **A comparison of a partition key the schema fixes to one value** — `JSON_VALUE(c."DOC", '$.category') = 'bikes'` where a required `const` is `"bikes"` | May be dropped from the request as one the schema shows true, and the request may then read every partition (Chapter 7.3). *Not done.* |
| **A conjunct the rendering reasoning declines** | In process, nothing sent for it, the container read: the reasoning was not ready within five seconds of a schema's first plan, failed, or the question was too large; where conjuncts proved alone were not proved together, each one's proved weakening is sent (Chapter 8). Counted on `cosmos.rendering.declines`, by reason (Chapter 20.2). *By design: only what is proved is sent.* |
| **A number compared through a cast to `INTEGER`, `BIGINT` or `DOUBLE`; a text equality an integer renders as** | The comparison over a stored number, or the string or the number, is sent, and the rest rechecked in process; against a literal past 2^53, the property's presence alone. *Not done: a form the service runs that is proved to mean the conversion or the value.* |
| **A comparison through a cast to `DOUBLE`, through a connection that runs Calcite's default rules** — `CAST(JSON_VALUE(c."DOC", '$.n') AS DOUBLE) > 500` through the README's connection with no schema | May fail to plan, with a `ClassCastException` from Calcite's simplifier (Chapters 7.3, 14.2). *Not done.* |
| **A number compared through `RETURNING DECIMAL`; `LIKE` over an undeclared text with a pattern a number's text or a boolean's may match; a text equality a double renders as; a parameter compared over a path not declared a string; arithmetic inside an `OR` or a `NOT` not sent whole; on the emulator, a text ordering over a path nothing keeps U+0000 out of** | The property's presence is sent and the rest rechecked in process. *Not done: a form the service runs that is proved to mean the conversion, the pattern or the value.* |
| **A comparison through a cast to `SMALLINT`, `TINYINT`, `REAL`, `FLOAT`, `DECIMAL(p, s)`, `DATE` or `UUID`, or through a cast Calcite widens around another; a typed reading compared with a fraction; an `AND` or an `OR` compared unequal to `TRUE`** | In process, nothing sent for it. *Not done: the proofs read each as one opaque value.* |
| **A `LIKE` with a `_` in a select list, over a string that may hold a line terminator** | Sent, and answered by the service, whose `_` matches a line terminator where Calcite's never does, so such a value reads true where Calcite reads false. In a filter the same `LIKE` runs in process (Chapter 8.2). *Not done: a select list's `LIKE` is written without asking what the string may hold.* |
| **The partition key column cast to text in a select list or a `GROUP BY`** — `SELECT c."id", CAST(c."$.category" AS VARCHAR) …` | Pushed, and answered with the stored value's text, `30` or `true`, where Calcite answers `NULL` for a key that is no string (Chapter 7.3). Read the path instead: `JSON_VALUE(c."DOC", '$.category')`. *Not done: the select list writes the column's text without asking what the key may hold.* |
| **A projection over an unnested element** | The element can be filtered at the service, but selecting it can bring the document along. *Not done.* |

## Declarations

| | |
| --- | --- |
| **Declarations are trusted, not checked** | A wrong JSON Schema or `UNIQUE` constraint loses rows silently. *Deliberate — checking means reading every document.* |
| **No typed columns** | A path cannot be declared as a column; the row model is the document. *Deliberate (Chapter 7).* |
| **`CHECK` constraints** | Refused by name. *Not done.* |
| **Schema facts about arrays, ranges, decimals, nullable unions written as `type: [x, "null"]`** | Not read. *Not done.* |
| **Schemas by reference** | A schema must be inline; `$ref` to another document is not fetched. *Deliberate (no network at model read); a file reference may come.* |
| **Seven-digit fractions and mixed-shape instants** | No pushdown. *Calcite timestamps are milliseconds; mixed shapes do not sort.* |

## Full text, vector and geography

| | |
| --- | --- |
| **An ascending ordering on a score** | Refused at plan time, with a message naming `DESC`. *Cosmos ranks the best match first and takes no direction.* |
| **Ordering by `VECTORDISTANCE` alone** | In process over the distances the service computes. *Which way it ranks is the distance function's, and nothing read declares it to the planner.* |
| **`ORDER BY RANK` through a connection** | Fails to implement ([#46](https://github.com/ikvmnet/calcite-cosmos/issues/46)); works in a planner you build. *Provider behaviour.* |
| **Fuzzy and prefix terms** | Fuzzy via `CLR_FT_FUZZY`; prefix refused. *Cosmos has no prefix term.* |
| **Variadic full text functions through a schema** | Up to 16 arguments. Chain the operator table for more. |
| **Vector search over undeclared paths** | Declined. *Cosmos requires a vector policy.* |
| **No `GEOGRAPHY` type** | Mixed planar/geodesic expressions are not refused. *Calcite's type system.* |
| **Spatial predicates are never weakened and rechecked** | Ellipsoid vs sphere disagree near boundaries. *Measured.* |
| **`ST_ISVALIDDETAILED`, geodesic `ST_AREA`** | Not offered. *Not done.* |

## Writing

| | |
| --- | --- |
| **No patch** | Every `UPDATE` replaces the whole document. *Not done.* |
| **No upsert, `MERGE`, `TRUNCATE`** | *Not done.* |
| **No transactions** | Each document is written separately; a failure part-way leaves earlier writes in place. *Cosmos transactions are per partition; not exposed.* |
| **No optimistic concurrency** | No `If-Match`; last write wins. *Deliberate for now.* |
| **Whole-partition delete** | Needs an account capability granted by Azure Support; built but not exercised against such an account. |

## Operational

| | |
| --- | --- |
| **The `CosmosClient` is never disposed by the adapter** | Calcite's schema interface has no disposal hook. Share one client or own the data source (Chapters 5 and 6). |
| **An account-level schema reads every container's definition** when built | Even those a query never uses. *Not done (lazy loading).* |
| **No connection options as operands** beyond `connectionMode` | Consistency level, preferred regions and the like need a client factory. *Not done.* |
| **The cost model is approximate** | Not denominated in request units; can choose badly in unusual plans. *Not done.* |
| **The emulator differs from Azure** | See Appendix E. *Emulator.* |

## Behaviours that are correct but surprising

- `JSON_VALUE` returns text, so `= '30'` matches the number 30 (Chapter 7).
- `RETURNING` asserts a type rather than converting to it — except `DECIMAL`, which parses a stored
  string, and is computed in process for that reason (Chapter 7).
- A `RETURNING` column over a value of another type is null; Calcite answers null for an object or an
  array but fails for a string or a boolean, and the adapter answers null for those too, as SQL says
  (Chapter 7).
- A sort on a `JSON_VALUE` column reaches the service only where the container's schema declares the
  type it reads — a string, or the `RETURNING` type — because the service orders the stored value and
  places an object or an array after every number, where the column is null (Chapter 9). A string
  key whose characters nothing declares is checked as the rows arrive (Chapter 9.3); a `pattern`
  proves them when the plan is made (Chapter 14.4).
- `MAX(c."_etag")` pushes and `ORDER BY c."_etag"` does not: an aggregate scans, and an `ORDER BY`
  needs an index `_etag` never has (Chapter 9.2).
- `CAST(<ISO-8601 text> AS TIMESTAMP)` raises in Calcite, and so is not pushed (Chapter 15).
- Out-of-domain arithmetic (`SQRT(-1)`) fails the whole Cosmos statement rather than one row (Chapter 8).
- A Cosmos query is not a snapshot: a long, paged read can see writes made while it runs. That is the
  service's behaviour for any query, with or without the adapter.

---

[← Previous: Troubleshooting](22-troubleshooting.md) · [Contents](README.md) · [Appendix A: Operand reference →](appendix-a-operands.md)
