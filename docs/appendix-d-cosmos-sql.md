# Appendix D. Cosmos SQL in brief

Cosmos SQL looks like SQL and is a much smaller, non-relational language. Knowing its shape explains
most of what the adapter can and cannot push down, and makes the statements it sends (Chapter 20)
readable. The authoritative reference is Microsoft's
[query language documentation](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started).

## D.1 The whole language

**Clauses:** `SELECT`, `FROM`, `WHERE`, `GROUP BY`, `ORDER BY`, `ORDER BY RANK`, `OFFSET … LIMIT`, and
subqueries. **Keywords:** `BETWEEN`, `DISTINCT`, `LIKE`, `IN`, `TOP`. That is the complete list.

There is no `UNION`, `INTERSECT` or `EXCEPT`; no `HAVING`; no `CASE` (there is a ternary `? :`); no
`WITH`; no window functions; no `CAST`; and no `INSERT`, `UPDATE` or `DELETE`.

## D.2 Four properties that shape everything

**1. `JOIN` iterates a document's own array.** It has no `ON` clause. `FROM c JOIN t IN c.tags` is one
row per element of each document's `tags` — `UNNEST`, not a relational join. Joins between documents
cannot be expressed.

**2. There are no derived tables.** A subquery in `FROM` is only `JOIN x IN (SELECT …)`, scoped to the
current document. `FROM (SELECT … FROM c) t` does not exist.

**3. A result is a stream of JSON values, not of rows.** `SELECT a, b` is shorthand for the object
constructor `SELECT VALUE { "a": …, "b": … }`. The adapter always uses the explicit form.

**4. `GROUP BY` and `ORDER BY` cannot be used together.** Grouped and `DISTINCT` results also cannot be
resumed with a continuation token.

## D.3 Paths and parameters

A property is a path from the `FROM` alias: `c.name`, `c.metadata.sku`, `c.tags[0]`, `c["odd name"]`.
Array indexes count from zero. Parameters are `@name`; the adapter uses `@p0`, `@p1`, … for values and
`@k0`, `@k1`, … for lookup-join keys.

## D.4 Undefined and null

A property can be *absent* (undefined) or hold JSON `null`, and they are different:

| document | `IS_DEFINED(c.v)` | `IS_NULL(c.v)` | `c.v = null` |
| --- | --- | --- | --- |
| `{"v": 1}` | true | false | false |
| `{"v": null}` | true | true | true |
| `{}` | false | false | undefined — not matched |

A comparison involving undefined is undefined, and a document is returned only where `WHERE` is true.
An object constructor omits a property whose value is undefined.

## D.5 Ordering across types

`ORDER BY` orders values of different JSON types by type first, ascending:

```
undefined  <  null  <  boolean  <  number  <  string  <  array  <  object
```

`DESC` is the exact reverse. There is no `NULLS FIRST`/`NULLS LAST`. Strings compare by code point
(ordinal), not by culture.

An `ORDER BY` item must be a document path — an expression is rejected (*"ORDER BY item expression
could not be mapped to a document path"*), with `ST_DISTANCE` the one exception. An `ORDER BY` on two or
more properties requires a composite index matching the properties and directions.

## D.6 Aggregates

`COUNT`, `SUM`, `MIN`, `MAX`, `AVG` — and their null handling differs from SQL's:

| over `{10, 20, null, 5}` and two documents without `v` | Cosmos | SQL |
| --- | --- | --- |
| `COUNT(1)` | 6 | 6 |
| `COUNT(c.v)` | 4 | 3 |
| `SUM(c.v)` | undefined | 35 |
| `AVG(c.v)` | undefined | 11.67 |

An aggregate cannot appear inside an object constructor (*"Compositions of aggregates and other
expressions are not allowed"*), which is why the adapter writes a grouped projection as a flat select
list.

## D.7 Indexing

By default every path is range-indexed. `id` and `_ts` are always indexed; the partition key is not
indexed unless it is `/id`. Index coverage affects cost, not legality — except for multi-key sorts,
which need a composite index, and vector search, which needs a vector policy.

## D.8 Consistency

Cosmos does not snapshot a query across its pages: a long read can see a document written behind or
ahead of its cursor after it started. No consistency level changes that; consistency levels govern how
fresh a read is, not isolation between statements. Transactions exist only within one logical partition,
through stored procedures and transactional batches, which the adapter does not use.

## D.9 How SQL maps onto it

| SQL | Cosmos SQL |
| --- | --- |
| a table | a container, `FROM <container> c` |
| the `DOC` column | `c` |
| `JSON_VALUE(c."DOC", '$.a.b')` | `c.a.b`, guarded |
| `WHERE` | `WHERE` |
| `SELECT a, b` | `SELECT VALUE { "a": …, "b": … }` |
| `ORDER BY … FETCH n` | `ORDER BY … OFFSET 0 LIMIT n` |
| `GROUP BY` | `GROUP BY`, flat select list |
| `UNNEST` | `JOIN t IN …` |
| `x IS NULL` | `NOT IS_DEFINED(x) OR IS_NULL(x)` |
| a join | not expressible — a lookup batch `WHERE c.k IN (@k0, …)`, or one read for a self-join |
| `INSERT`, `UPDATE`, `DELETE` | item create, replace, delete through the SDK |

---

[← Previous: Type reference](appendix-c-types.md) · [Contents](README.md) · [Appendix E: Development, the emulator and the test suite →](appendix-e-development.md)
