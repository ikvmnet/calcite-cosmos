# 11. Arrays

Cosmos documents often hold arrays — tags, line items, addresses. Cosmos SQL reaches into them with
`JOIN alias IN path`, which iterates a document's own array (it is not a relational join). The adapter
maps SQL's `UNNEST` onto it, and the SQL array functions onto Cosmos's.

## 11.1 Reading an array as a column

```sql
SELECT c."id", JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY) AS "tags"
FROM "products" AS c
→ SELECT VALUE { "id": c.id, "tags": (IS_ARRAY(c.tags) ? c.tags : null) } FROM products c
```

The column is a SQL array of the element type, and arrives in .NET as an array — `string?[]` for
`VARCHAR ARRAY`, `int?[]` for `INTEGER ARRAY`. Elements are read as the declared type: a `VARCHAR
ARRAY` over `[1, 2]` fails with *Expected a JSON string*, rather than handing back numbers. Element
nullability always follows SQL's rule that array elements are nullable; there is no syntax to say
otherwise.

A document with no array at the path — absent, null, a scalar or an object — gives a null column.

**Use `JSON_QUERY`, not `JSON_VALUE`, for arrays.** `JSON_VALUE` extracts scalars only; with an array
`RETURNING` it is null for every document in Calcite, and the adapter answers the same way rather than
inventing another meaning.

## 11.2 `UNNEST`: one row per element

```sql
SELECT c."id"
FROM "products" AS c,
     UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS t("tag")
WHERE t."tag" = 'steel'
→ SELECT VALUE { "id": c.id } FROM products c JOIN t0 IN c.tags WHERE (t0 = @p0)
```

The traversal and a predicate over the element are both evaluated by the service. A predicate over
the element is written as a `WHERE` after the `JOIN`, which is exactly traverse-then-filter. When the
element itself is selected, it is added to the object the statement returns.

A traversal pushes the same way whichever accessor names the array — `JSON_QUERY(… RETURNING …
ARRAY)` or `StringToArray(JSON_QUERY(…))` — because the adapter reads the path, not the function.
Prefer `JSON_QUERY … RETURNING … ARRAY`: it is also the spelling that gives the right answer if a
plan ever evaluates it in process.

Limits on what can share the statement:

- **A sort on the element runs in process.** Azure rejects `ORDER BY t0` (the emulator accepts it).
- **A row limit or grouping above the traversal** runs in process — the traversal multiplies rows, so
  it has to come first, and Cosmos evaluates `OFFSET`/`LIMIT` and `GROUP BY` against the multiplied
  rows only in that order.
- **`DISTINCT` above a traversal** runs in process.

To ask only whether *some* element matches, filter on the array instead of unnesting it (11.4);
unnesting then de-duplicating is a heavier way to ask the same question.

## 11.3 Subscripts

SQL array subscripts count from **one**; Cosmos counts from **zero**. The adapter adjusts:

```sql
(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY))[1]   →   c.tags[0]
```

A JSON path subscript is already zero-based (SQL/JSON paths count from zero) and is passed through:

```sql
JSON_VALUE(c."DOC", '$.tags[0]')   →   (IS_PRIMITIVE(c.tags[0]) ? c.tags[0] : null)
```

Both name the first element. `ARRAY_SLICE` and `SUBSTRING` are adjusted for the origin the same way.

## 11.4 Array functions

| SQL | Cosmos |
| --- | --- |
| `CARDINALITY(a)` | `ARRAY_LENGTH(a)` |
| `x MEMBER OF a` | `ARRAY_CONTAINS(a, x)` |
| `ARRAY_CONCAT(a, b, …)` | `ARRAY_CONCAT(a, b, …)` |
| `ARRAY_INTERSECT(a, b)` | `SETINTERSECT(a, b)` |
| `ARRAY_UNION(a, b)` | `SETUNION(a, b)` |
| `ARRAY_SLICE(a, start, length)` | `ARRAY_SLICE(a, start - 1, length)` |
| `StringToArray(s)` | `StringToArray(s)` — parses JSON text; not Postgres's `STRING_TO_ARRAY` |

```sql
SELECT c."id" FROM "products" AS c
WHERE 'steel' MEMBER OF JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)
  AND CARDINALITY(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) > 1
→ WHERE (ARRAY_CONTAINS(c.tags, @p0) AND (ARRAY_LENGTH(c.tags) > @p1))
```

`CARDINALITY` of a `MAP` has no Cosmos counterpart and runs in process. `ARRAY_CONCAT`,
`ARRAY_INTERSECT` and `ARRAY_UNION` push between two paths but not against an **array literal** —
`ARRAY['x', 'y']` does not render yet, so a comparison with a constant set stays in process.

## 11.5 An empty array constant

`JSON_QUERY('[]', '$' RETURNING VARCHAR ARRAY)` — an accessor over a literal — is a constant, computed
once and sent as a parameter, so an expression like
`COALESCE(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY), JSON_QUERY('[]', '$' RETURNING VARCHAR ARRAY))`
still pushes.

---

[← Previous: Aggregation](10-aggregation.md) · [Contents](README.md) · [Next: Joins →](12-joins.md)
