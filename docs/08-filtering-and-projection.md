# 8. Filtering and projecting

A `WHERE` clause is where most of a query's cost is decided. A predicate the service evaluates
returns the matching documents; a predicate evaluated in process returns every document in the
container first. This chapter describes what reaches the service, what does not, and how to write
predicates and projections that do.

## 8.1 How a predicate reaches the service

The adapter takes a `WHERE` clause apart at its top-level `AND`s and treats each conjunct separately:

- A conjunct that renders exactly is **sent as written**.
- A conjunct that does not render, but *implies* something that does, has that implication sent, and
  the original conjunct is **rechecked in process** over the rows that come back.
- A conjunct that implies nothing renderable is evaluated **in process** only.

```sql
SELECT c."id" FROM "products" AS c
WHERE c."$.category" = 'bikes'
  AND JSON_VALUE(c."DOC", '$.name') SIMILAR TO 'T%'
```

```
→ Cosmos:     … WHERE (c.category = @p0)        -- one partition, bikes only
  In process: name SIMILAR TO 'T%', over the bikes that come back
```

This is always safe — the service returns a superset and the recheck restores the exact answer — and
it is often most of the benefit, because the conjunct that pushes is usually the selective one.

An `OR` is different: dropping a branch would lose the rows only that branch matches. A disjunction
is sent whole where every branch renders, and otherwise **weakened** — something every branch implies
is sent, and the disjunction is rechecked in process.

## 8.2 What renders

| SQL | Cosmos SQL |
| --- | --- |
| `=`, `<>`, `<`, `<=`, `>`, `>=` | the same operators (`<>` as `!=`) — subject to 8.3 |
| `AND`, `OR`, `NOT` | the same, with guards that keep SQL's three-valued logic (8.4) |
| `+`, `-`, `*`, `/`, `%`, unary `-` | the same |
| `x IS NULL` | `(NOT IS_DEFINED(x) OR IS_NULL(x))` |
| `x IS NOT NULL` | `(IS_DEFINED(x) AND NOT IS_NULL(x))` |
| `x IN (a, b, c)`, `x BETWEEN a AND b` | equivalent comparisons — measured to cost exactly what the native forms cost |
| `x LIKE 'abc%'` | `STARTSWITH(x, 'abc')` |
| `x LIKE '%abc%'`, other patterns | Cosmos `LIKE` |
| `UPPER(x) LIKE '%ABC%'` (ASCII) | `CONTAINS(x, 'ABC', true)` — case-insensitive at the service |
| `CASE WHEN … THEN … ELSE … END` | nested `(cond ? a : b)` |
| `COALESCE`, `NULLIF`, `NVL`, `IFNULL` | the `CASE` Calcite expands them to |
| `SUBSTRING`, `POSITION`, `TRIM` (spaces), string, math and array functions | Cosmos functions — [Appendix B](appendix-b-functions.md) |
| `IS_DEFINED(x)`, `IS_STRING(x)`, … | the same Cosmos functions |

`LIKE` has three refusals. A pattern containing `[` or `]` is declined — Cosmos reads a bracket as a
character range, SQL reads it literally. `LIKE … ESCAPE` is declined. And a pattern that is not a
literal is declined, because only a literal can be checked for brackets.

The case-insensitive rewrite applies when the pattern is one leading and one trailing `%` around plain
text, the text is already in the case `UPPER` (or `LOWER`) produces, and the text is ASCII. `LIKE
'abc%'` and `LIKE '%abc'` under `UPPER` become `STARTSWITH(x, 'ABC', true)` and `ENDSWITH(x, 'ABC',
true)`. This is the shape an ORM writes for a case-insensitive "contains", and it is served by the
index.

## 8.3 Comparisons over document values

Comparing a document property is where SQL and Cosmos most often disagree, because SQL compares typed
values and a JSON property can hold anything. How the adapter handles a comparison depends on how the
property is read.

### System properties: exact

`id`, `_ts` and `_etag` are typed and present on every document, so a comparison over them is always
exact:

```sql
WHERE c."id" > 'bike-1' AND c."_ts" > 1700000000
→ WHERE ((c.id > @p0) AND (c._ts > @p1))
```

### `JSON_VALUE`: exact against unambiguous text

`JSON_VALUE` reads a property as text (Chapter 7), so `JSON_VALUE(…) = '30'` is true of the string
`"30"` *and* the number `30`. An equality against text that nothing but a string can render as is
sent exactly:

```sql
WHERE JSON_VALUE(c."DOC", '$.name') = 'Trail Blazer'
→ WHERE (c.name = @p0)
```

An equality against text that a number or boolean could also render as is sent as both, and
rechecked:

```sql
WHERE JSON_VALUE(c."DOC", '$.code') = '30'
→ WHERE (c.code = '30' OR c.code = 30)   -- then rechecked in process
```

Inequalities, range comparisons and `LIKE` over `JSON_VALUE` are sent restricted to the case where
the two engines agree — the property is a string — and everything else is passed through for the
recheck:

```sql
WHERE JSON_VALUE(c."DOC", '$.name') LIKE 'Trail%'
→ WHERE (NOT IS_STRING(c.name) OR STARTSWITH(c.name, @p0))   -- then rechecked
```

For a property that holds strings in every document, the first branch matches nothing and the service
does all the work; the recheck is cheap. **If the container declares that the path holds a string**
(Chapter 14), there is no second type to disagree about, the comparison is sent exactly, and nothing
is rechecked — which also lets a row limit travel with it.

### `RETURNING`: exact

A comparison through a typed accessor compares the stored value directly, and is exact:

```sql
WHERE JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) > 1000
→ WHERE (c.price > @p0)
```

This is the spelling to use for numbers that are stored as numbers.

### `CAST` to text: exact

`CAST(x AS VARCHAR) = 'text'`, over a document value, selects exactly the documents storing the
string `'text'` when no other JSON value renders as that text — so the cast is dropped and the
equality sent. This is what keeps a view's text columns filterable.

### `CAST` to a number: a bound, then a recheck

`CAST(JSON_VALUE(c."DOC", '$.price') AS INTEGER) > 10` converts — `"30"` becomes 30, `30.7` becomes
30 — and the service cannot reproduce a conversion. But the comparison still *implies* something the
service can check: every document it keeps has a raw value above 9. So the adapter sends that bound
and rechecks the cast in process:

```
→ WHERE IS_DEFINED(c.price) AND (NOT IS_NUMBER(c.price) OR c.price > 9)
```

Non-numbers pass through to the recheck, because Calcite converts a stored `"30"` too. For a property
that really holds numbers, the service does nearly all the filtering. The bound is applied for casts
to `INTEGER`, `BIGINT` and `DOUBLE`. Casts to `SMALLINT` and `TINYINT` (which wrap), `FLOAT` and `REAL`
(which round further than a unit) and `DECIMAL` (which can raise) state no usable bound and are
evaluated in process only.

If the values are numbers in the documents, prefer `RETURNING` to `CAST`: it is exact.

### Text that spells a number, a UUID or a date

A property holding `"00042"`, `"123e4567-…"` or `"2024-01-15T12:30:00Z"` is a string to Cosmos and a
number, UUID or timestamp to the query. Comparing it as the latter has no Cosmos form — unless the
container declares how the value is written. Chapter 14 (numbers and UUIDs) and Chapter 15 (dates and
times) cover what to declare and what it unlocks.

## 8.4 `NOT` and nulls

SQL's comparison with a null is *unknown*, and a row is kept only where the whole predicate is true.
Cosmos's comparisons are two-valued — `!=` against a null is true — and the adapter adds guards so
the statement means what SQL means, in either polarity. `NOT (x = 1 AND y = 2)` keeps a row where `x`
is null and `y` is not 2, exactly as SQL does. You do not need to write anything for this; it is
mentioned so that the guards in a generated statement are not a surprise.

`IS TRUE`, `IS FALSE`, `IS NOT TRUE` and `IS NOT FALSE` reach the service over a boolean column —
`CAST(JSON_VALUE(…) AS BOOLEAN)` — where the container's schema says the path holds a boolean
(Chapter 14). The cast is then the stored value: `WHERE "Offline"` becomes `WHERE c.offline`,
`"Offline" IS TRUE` becomes `c.offline = true`, and `IS NOT TRUE` keeps a document with no `offline`
at all, as SQL does. Without the declaration the cast parses text — `"TRUE"` is true to Calcite and a
string to Cosmos — and the test stays in process. Over anything else the truth tests, and
`IS DISTINCT FROM`, are evaluated in process: reproducing their null semantics over a property that
may be *absent* needs Cosmos behaviour that has not been measured, and a wrong answer is worse than a
declined one.

## 8.5 Partition routing and point reads

The partition key is the largest cost lever in Cosmos, and the adapter recovers it from the
predicate without being asked.

**Routing.** An equality on every partition key path, against a constant, confines the statement to
one logical partition:

```sql
WHERE c."$.category" = 'bikes' AND JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) > 1000
→ one partition, bikes only
```

The equality may be on the promoted column (`c."$.category"`) or on the path through `JSON_VALUE`. A
range or a disjunction names no single value and does not route — though the service itself prunes an
`IN` list over the partition key to the partitions that hold its values. With a hierarchical key,
pinning a prefix of the paths routes to the partitions under that prefix.

**Point reads.** A lookup by `id` and a complete partition key, with nothing else in the predicate,
is not run as a query at all. It is a point read — `ReadItem`, about 1 RU, no query engine — against
the 2.3 RU or more a query costs at best:

```sql
SELECT c."DOC" FROM "products" AS c
WHERE c."id" = 'bike-1' AND c."$.category" = 'bikes'
→ ReadItem("bike-1", partition "bikes")
```

A point read returns a whole document, so anything that is not a document stops it: an `ORDER BY`, a
row limit, a `GROUP BY`, an `UNNEST`, or a computed column in the select list. Selecting columns that
are document paths is fine — they are taken out of the document after it is read. Compare the
promoted columns (`"id"` and the partition key column) for a point read; a cast or a `JSON_VALUE` over
the partition key still routes, but is not used to recover a read.

**A point read with something more.** `WHERE id = 'x' AND pk = 'y' AND deletedAt IS NULL` — the
shape of every by-id lookup through a soft-delete view — cannot be a point read as written, since a
read applies no predicate. The planner also considers reading the document and applying the extra
condition in process, and chooses between that and the query by what each costs for the container's
document size: a point read wins for small documents and loses for very large ones.

**A set of ids.** `WHERE c."id" IN ('a', 'b') AND c."$.category" = 'bikes'`, with nothing else, can be
answered with `ReadMany` — one request for the set. The planner prices it against the query.

## 8.6 Projections

A select list becomes a Cosmos object constructor, and the service returns only what it names:

```sql
SELECT c."id", JSON_VALUE(c."DOC", '$.name') AS "name", JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) * 2 AS "double"
FROM "products" AS c
→ SELECT VALUE { "id": c.id, "name": (IS_PRIMITIVE(c.name) ? c.name : null), "double": (c.price * @p0) } FROM products c
```

Selecting only what you need matters more here than in a relational database. A Cosmos document
comes back whole unless the statement says otherwise, and selecting `DOC` — or `*` — means every
property of every matching document crosses the network, along with six system properties the
service adds to each.

**A projection is pushed piece by piece.** Where part of a select list renders and part does not, the
renderable expressions are sent and the rest computed in process from what comes back. Inside a single
expression, the largest renderable sub-expressions are sent: for
`CAST(JSON_VALUE(c."DOC", '$.n') AS DOUBLE)`, the service extracts `c.n` and only the cast runs in
process — the document is not shipped to supply one value.

**Some projections are read rather than rendered.** A cast to text over a document value, a
`JSON_QUERY`, a declared UUID or a stored geography is sent as the plain property and converted as it
is read, by the same code Calcite would have used in process. The result is identical and the
projection still pushes.

**A projection that would read as nothing is declined while planning.** A column of a SQL type the
reader has no conversion for — an `INTERVAL`, an unsigned integer, a time with a time zone — is never
sent: it is computed in process instead. Nothing reachable produces one today; the check exists so a
future case fails while the statement is prepared rather than after rows have started arriving.

## 8.7 Parameters

Parameters are written as `?` and bound positionally:

```csharp
command.CommandText = """
    SELECT c."id" FROM "products" AS c
    WHERE c."$.category" = ? AND JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) > ?
    ORDER BY c."id"
    OFFSET ? ROWS FETCH NEXT ? ROWS ONLY
    """;

foreach (var value in new object[] { "bikes", 1000, 20, 10 })
{
    var parameter = command.CreateParameter();
    parameter.Value = value;
    command.Parameters.Add(parameter);
}
```

A parameter is sent as a Cosmos parameter, exactly as a literal of the same type would be. That
includes the row limit — `OFFSET ? ROWS FETCH NEXT ? ROWS ONLY` becomes `OFFSET @p0 LIMIT @p1` — so one
prepared statement serves every page.

**A parameter compared with `JSON_VALUE` is sent in its weakened form.** Whether a text comparison
can be exact depends on the value — `'bikes'` can be, `'30'` cannot (8.3) — and a parameter's value
is not known when the statement is planned. So the comparison is sent as `NOT IS_STRING(…) OR …` and
rechecked; the rows are correct, the statement slightly wider. Declaring that the path holds a string
(Chapter 14) makes it exact. Comparisons with parameters over `id`, `_ts`, `_etag` and `RETURNING`
accessors are exact already, and so is a comparison of a declared instant with a parameter — the
value is written in the stored spelling when the statement runs (Chapter 15).

**Give a parameter a type where Calcite cannot infer one** — `CAST(? AS VARCHAR)` — so the plan can
reason from it. A parameter's value also does not route the statement to a partition the way a
literal does: routing is decided while the plan is made, from constants.

A geography parameter is bound as the GeoJSON object it represents (Chapter 18).

## 8.8 What runs in process, and why

These are evaluated by Calcite rather than by the service. None of them is an error; each costs the
documents it has to read.

| construct | reason |
| --- | --- |
| a function with no Cosmos counterpart (`LPAD`, `MD5`, `REGEXP_LIKE`, …) | nothing to render it as — [Appendix B](appendix-b-functions.md) lists them |
| a JSON path with a wildcard, descent or filter | no Cosmos path expresses it |
| `LIKE` with brackets, `ESCAPE` or a computed pattern | Cosmos reads `[…]` differently |
| `CAST` to a sized or numeric type in a projection | it converts, and the service would not |
| `IS TRUE`, `IS FALSE` over anything but a declared boolean; `IS DISTINCT FROM` | null-versus-absent behaviour not measured |
| `SUBSTRING` without a length, `TRIM` of other characters, `TRUNCATE`/`ROUND` to decimal places | the Cosmos forms are not verified |
| an array literal, `ARRAY['x', 'y']` | not rendered yet — so functions taking one stay in process too |
| a relational join, `UNION`, `INTERSECT`, `EXCEPT` | no Cosmos equivalent (Chapter 12) |
| a subquery (`EXISTS`, `IN (SELECT …)`) | Cosmos subqueries are document-scoped only |

**Out-of-domain arithmetic fails the whole statement.** `SQRT(-1)`, `ASIN(2)` and `ACOS(2)` make Cosmos
reject the query, where Calcite would produce `NaN` for that row. They are pushed anyway, consistently
with how `SQRT` always has been; if your data can produce such inputs, guard them in the query.

---

[← Previous: Tables, columns and documents](07-row-model.md) · [Contents](README.md) · [Next: Sorting and paging →](09-sorting-and-paging.md)
