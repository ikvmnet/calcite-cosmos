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
| **Sorting on `JSON_VALUE` as text over a path no schema declares a string** | In process. *The service orders the stored value, by JSON type and numbers by value; SQL orders the text.* |
| **Sorting on a `JSON_QUERY`, `DOC` or array column** | In process. *The service orders objects and arrays by rules of its own.* |
| **Sorting, comparing against a parameter, or taking `MIN`/`MAX` of strings whose characters nothing states** | In process — over `id` too, so a page or a keyset seek on `id` reads the container. Pushes where a schema `pattern` rules the characters out (Chapter 14.4), or a schema `enum` or `const`, a declared stored form or the `WHERE` lists the values (Chapter 9.3). *Cosmos orders strings by code point and Calcite by UTF-16 code unit, which part where a character above U+FFFF meets one in U+E000–U+FFFF. A check of the rows as they arrive is not done.* |
| **Multi-key sorts** | Need a matching composite index; a key the `WHERE` pins, or one repeated, is not sent and does not count. *Cosmos.* |
| **Sorting on a path the indexing policy does not index** | In process: `_etag`, which every policy excludes, an excluded path, and a computed property no included path names. *Cosmos — a 400 on Azure; the emulator accepts it.* |
| **Sorting on `RETURNING BIGINT` over an integer the schema does not bound within int64** | In process. *A number past int64 is read back rounded below one inside it.* |
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
  key also needs its characters known, which `ORDER BY c."id"` has only where a `pattern` declares them
  (Chapter 14.4).
- `MAX(c."_etag")` pushes and `ORDER BY c."_etag"` does not: an aggregate scans, and an `ORDER BY`
  needs an index `_etag` never has (Chapter 9.2).
- `CAST(<ISO-8601 text> AS TIMESTAMP)` raises in Calcite, and so is not pushed (Chapter 15).
- Out-of-domain arithmetic (`SQRT(-1)`) fails the whole Cosmos statement rather than one row (Chapter 8).
- A Cosmos query is not a snapshot: a long, paged read can see writes made while it runs. That is the
  service's behaviour for any query, with or without the adapter.

---

[← Previous: Troubleshooting](22-troubleshooting.md) · [Contents](README.md) · [Appendix A: Operand reference →](appendix-a-operands.md)
