# 10. Aggregation

Cosmos has `GROUP BY` and five aggregate functions — `COUNT`, `SUM`, `MIN`, `MAX` and `AVG`. An
aggregate pushed down returns one row per group; in process it returns every document first. This
chapter describes which aggregates push and why some do not.

## 10.1 What pushes

```sql
SELECT JSON_VALUE(c."DOC", '$.category') AS "category", COUNT(*) AS "n"
FROM "products" AS c
GROUP BY JSON_VALUE(c."DOC", '$.category')
→ SELECT (IS_DEFINED(c.category) ? c.category : null) AS "category", COUNT(1) AS "n"
  FROM products c GROUP BY (IS_DEFINED(c.category) ? c.category : null)
```

| aggregate | pushes |
| --- | --- |
| `COUNT(*)` | always |
| `COUNT(x)` | where `x` cannot be null (Calcite then rewrites it to `COUNT(*)`) |
| `SUM(x)`, `MIN(x)`, `MAX(x)`, `AVG(x)` | only where `x` cannot be null |
| `COUNT(DISTINCT x)` | the de-duplication pushes as a `GROUP BY x`; the count finishes in process over one row per value |
| `SELECT DISTINCT …` | as Cosmos `SELECT DISTINCT` |
| `GROUP BY ROLLUP (…)`, `CUBE`, `GROUPING SETS` | the finest grouping pushes; the roll-up finishes in process |
| `HAVING` on a grouping key | moved below the grouping, as a `WHERE` |
| `HAVING` on an aggregated value | in process, over one row per group |
| `agg(x) FILTER (WHERE …)`, `SUM(DISTINCT x)` | in process |
| anything else (`STDDEV`, `LISTAGG`, …) | in process |

A grouped statement cannot also carry an `ORDER BY` (Chapter 9), so a grouped and sorted query
groups at the service and sorts the groups in process.

## 10.2 Why value aggregates need a non-nullable input

Cosmos aggregates do not share SQL's null handling. Measured over the values `10`, `20`, `null`, `5`
and two documents without the property:

| expression | Cosmos | SQL |
| --- | --- | --- |
| `COUNT(1)` | 6 | 6 |
| `COUNT(c.v)` | 4 — it counts the JSON `null` | 3 |
| `SUM(c.v)` | *undefined* | 35 |
| `AVG(c.v)` | *undefined* | 11.67 |

So pushing `SUM` over a property that might be null or missing would return nothing where SQL returns
a number. The adapter pushes value aggregates only over inputs that cannot be null. A document
property is nullable as far as the planner knows, so in practice:

- **`COUNT(*)` always pushes.** This is the most common aggregate and it is unaffected.
- **`SUM`, `MIN`, `MAX` and `AVG` over a document property run in process** — unless the input is
  known non-null. Two ways it can be: aggregate `_ts` or a non-nullable expression, or filter the
  nulls out in a way the planner can see. A `WHERE x IS NOT NULL` on a *promoted column* works; on a
  `JSON_VALUE` path the knowledge does not survive the projection, and the aggregate stays in
  process.

Even then, the grouping and filtering around an in-process aggregate still push: the service applies
the `WHERE` and the planner reads only what it needs.

## 10.3 Groups and nulls

Cosmos keeps a document with no grouping property apart from one whose property is `null`, and omits
the key from the result for the former. SQL has one `NULL` group. The adapter normalises the key —
`IS_DEFINED(p) ? p : null` — so both land in SQL's single null group. The normalisation is skipped for
`id`, `_ts` and `_etag`, which are always present.

## 10.4 Grouping sets

`ROLLUP`, `CUBE` and `GROUPING SETS` group several ways at once, and Cosmos groups one way per
statement. Every grouping set is a coarsening of the finest one, so the adapter pushes the finest
grouping as a plain `GROUP BY` and rolls the partial results up in process:

```sql
SELECT JSON_VALUE(c."DOC", '$.category') AS "category", JSON_VALUE(c."DOC", '$.brand') AS "brand", COUNT(*)
FROM "products" AS c
GROUP BY ROLLUP (JSON_VALUE(c."DOC", '$.category'), JSON_VALUE(c."DOC", '$.brand'))
```

Cosmos returns one row per category and brand; the per-category subtotals and the grand total are
computed from those. A partial `COUNT` is finished by summing; `SUM`, `MIN` and `MAX` finish as
themselves; `AVG` is decomposed into `SUM` and `COUNT` so it can be finished correctly.

## 10.5 `DISTINCT`

`SELECT DISTINCT` pushes as Cosmos `SELECT DISTINCT` over the selected paths, with the same null
normalisation as a grouping key. `COUNT(DISTINCT x)` becomes a grouping on `x` at the service — one row
per distinct value crosses the network — and the count is taken in process.

Unlike `GROUP BY`, `DISTINCT` *can* share a statement with an `ORDER BY`, so a distinct, sorted query
can be answered whole by the service — subject to the sorting rules of Chapter 9.

Cosmos does not support continuation for `GROUP BY` and `DISTINCT` results, so such a statement is
always read to the end once started.

---

[← Previous: Sorting and paging](09-sorting-and-paging.md) · [Contents](README.md) · [Next: Arrays →](11-arrays.md)
