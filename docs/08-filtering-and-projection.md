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
| `+`, `-`, `*`, `%`, unary `-` | the same |
| `/` | the same, where Calcite computes the quotient as a `DOUBLE`; an integer or `DECIMAL` division runs in process |
| `x IS NULL` | `(NOT IS_DEFINED(x) OR IS_NULL(x))`, and over an accessor whatever else it reads as null (8.4) |
| `x IS NOT NULL` | `(IS_DEFINED(x) AND NOT IS_NULL(x))`, likewise |
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

`id`, `_ts` and `_etag` are typed and present on every document, so a comparison of one with a literal
is always exact:

```sql
WHERE c."id" > 'bike-1' AND c."_ts" > 1700000000
→ WHERE ((c.id > @p0) AND (c._ts > @p1))
```

A comparison of `id` with a parameter or with another path turns on the characters `id` holds as well:
Azure allows every character in an `id` but `/` and `\`, and the service orders strings by code point
where SQL orders them by UTF-16 code unit (Chapter 9.3). Where a schema `pattern` declares them (Chapter
14.4) it is exact; otherwise it is made in process. `_ts` and `_etag` compare exactly with anything.

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

**A function over `JSON_VALUE` runs in process unless the path is declared a string.** `UPPER`,
`LOWER`, `||`, `CHAR_LENGTH`, `SUBSTRING` and the rest read the *text*, so `UPPER(JSON_VALUE(…))` is
`'TRUE'` for a stored boolean and `'30'` for a stored number. The service's string functions are
undefined over a number or a boolean, so the two would disagree for exactly those documents. The same
goes for a `CASE` or `COALESCE` that passes the value on. The property itself is still extracted at
the service, and only the function runs in process:

```sql
WHERE UPPER(JSON_VALUE(c."DOC", '$.status')) = 'OPEN'
→ Cosmos:     … WHERE IS_DEFINED(c.status)
  In process: UPPER(status) = 'OPEN'
```

Declaring the path a string (Chapter 14) sends the whole comparison: `WHERE (UPPER(c.status) = @p0)`.
`UPPER(…) LIKE '%ABC%'` keeps its own rewrite (8.2), restricted to the strings and rechecked.

### `RETURNING`: exact

A comparison through a typed accessor compares the stored value directly, and is exact:

```sql
WHERE JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) > 1000
→ WHERE (c.price > @p0)
```

This is the spelling to use for numbers that are stored as numbers. **Except `RETURNING DECIMAL`**,
which parses a stored string — `"2"` is 2 to it (Chapter 7) — and is handled as the cast to a number
below: a bound is sent, and the comparison rechecked in process.

### `CAST` to text: exact

`CAST(x AS VARCHAR) = 'text'`, over a document value, selects exactly the documents storing the
string `'text'` when no other JSON value renders as that text — so the cast is dropped and the
equality sent. This is what keeps a view's text columns filterable.

Only a `VARCHAR` with no length. `CAST(x AS VARCHAR(3))` truncates and `CHAR(8)` pads, so
`CAST(x AS VARCHAR(3)) = 'bik'` is true of a stored `'bikes'` and is evaluated in process; the service
is sent only what it implies. The same holds for routing: a sized cast over the partition key does not
route (8.5).

### `CAST` to a number: a bound, then a recheck

`CAST(JSON_VALUE(c."DOC", '$.price') AS INTEGER) > 10` converts — `"30"` becomes 30, and `30.7`,
whose text is no integer, fails the statement (cast to `DOUBLE` to read it) — and the service cannot
reproduce a conversion. But the comparison still *implies* something the service can check: every
document it keeps has a raw value above 9. So the adapter sends that bound and rechecks the cast in
process:

```
→ WHERE IS_DEFINED(c.price) AND (NOT IS_NUMBER(c.price) OR c.price > 9)
```

Non-numbers pass through to the recheck, because Calcite converts a stored `"30"` too. For a property
that really holds numbers, the service does nearly all the filtering — and where the container
declares the property an integer (Chapter 14), the type test is dropped and it does all of it. The
bound is applied for casts to `INTEGER`, `BIGINT` and `DOUBLE`, and for `RETURNING DECIMAL`, which is
the same conversion written as a clause. Casts to `SMALLINT` and `TINYINT` (which wrap), `FLOAT` and
`REAL` (which round further than a unit) and `DECIMAL` (which can raise) state no usable bound and are
evaluated in process only. So is a cast to `DOUBLE` compared with a number beyond 2^52
(4,503,599,627,370,496), where a double no longer holds a unit: `= 2^60` is true of a stored
2^60 + 1.

`RETURNING DECIMAL` sends the bound where Calcite compares the literal at the clause's type, which
under plain `DECIMAL` it always does, truncating the literal to a whole number. Where the literal's
type is the wider — `RETURNING DECIMAL(10, 2) = 2.255`, or `RETURNING DECIMAL(5, 1) = 12345`, an
`INTEGER` literal — Calcite casts the value up instead, and the comparison runs in process whole.
Where the clause cannot hold the literal — `RETURNING DECIMAL(19, 18) = 50` — Calcite raises on every
row, and so does the statement.

```sql
WHERE JSON_VALUE(c."DOC", '$.body' RETURNING DECIMAL) = 2
→ WHERE IS_DEFINED(c.body) AND (NOT IS_NUMBER(c.body) OR (c.body > 1 AND c.body < 3))
  In process: the comparison, which keeps "2", " 2 " and "2.0"
```

A declaration that the property is a string does not make that comparison false: the clause reads the
string.

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

**Null means what the accessor reads as null.** `JSON_VALUE` is null for an object or an array as well
as for a missing property and a JSON null, `JSON_QUERY` is null for anything that is not an object or
an array, and a `RETURNING INTEGER` accessor is null for anything that is not a number. So `IS NULL`,
`IS NOT NULL` and the guard under a `NOT` carry the accessor's own test:

```sql
WHERE JSON_VALUE(c."DOC", '$.metadata') IS NOT NULL
→ WHERE (IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata) AND IS_PRIMITIVE(c.metadata))

WHERE NOT (JSON_VALUE(c."DOC", '$.label') = 'bikes')
→ WHERE (NOT ((c.label = @p0) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label) OR NOT IS_PRIMITIVE(c.label)))
```

A `CASE` condition is evaluated as SQL selects an arm, whatever `NOT` encloses the `CASE`.

Two shapes in a `WHERE` have no guard that would make them right, and run in process instead.
`IS NULL` over a comparison — `(x <> 'a') IS NULL`, or `COALESCE(x <> 'a', FALSE)`, which expands to
one — asks where the comparison is unknown, and the service has no unknown to find. And a comparison
under a `NOT`, or an inequality, over a `CASE` or `COALESCE` — `NOT (COALESCE(x, 'none') = 'a')` —
would be guarded on `x`, which the `COALESCE` does not follow into null. In a `WHERE`, a comparison
over one that needs no guard, such as a plain equality, is sent as before.

**A comparison whose value is read, rather than its truth, runs in process where it can be null.** The
guards above make a comparison true or false exactly where SQL's is — and false where SQL's is
unknown, which a `WHERE` treats the same. A selected column, a grouping key and the operand of another
comparison do not:

```sql
SELECT c."id", c."$.category" = 'bikes' AS "isBike" FROM "products" AS c
→ Cosmos:     SELECT VALUE { "$f0": c.id, "$f1": @p0, "$f2": c.category } FROM products c
  In process: $f2 = $f1   -- null where category is null or missing, as SQL says

WHERE (c."$.category" <> 'bikes') = FALSE
→ Cosmos:     WHERE IS_DEFINED(c.category)   -- then rechecked in process
```

A comparison that cannot be null — over `id` or `_ts`, say — is still sent, and so is a `CASE`
condition, which selects an arm by its truth alone.

`IS TRUE`, `IS FALSE`, `IS NOT TRUE` and `IS NOT FALSE` reach the service over a boolean column —
`CAST(JSON_VALUE(…) AS BOOLEAN)` — where the container's schema says the path holds a boolean
(Chapter 14). The cast is then the stored value: `WHERE "Offline"` becomes `WHERE c.offline`,
`"Offline" IS TRUE` becomes `c.offline = true`, and `IS NOT TRUE` keeps a document with no `offline`
at all, as SQL does. Selected rather than filtered on, `IS TRUE` is sent in the form that is false for a
missing `offline`, since a column, unlike a `WHERE`, would read the service's *undefined* as null.
Without the declaration the cast parses text — `"TRUE"` is true to Calcite and a string to Cosmos —
and the test stays in process. Over anything else the truth tests, and `IS DISTINCT FROM`, are
evaluated in process: reproducing their null semantics over a property that may be *absent* needs
Cosmos behaviour that has not been measured, and a wrong answer is worse than a declined one.

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

Through `JSON_VALUE` the comparison is with the key's text, and text a number also renders as names two
partitions: `JSON_VALUE(c."DOC", '$.category') = '30'` matches a stored `"30"` and a stored `30`, so it
is not routed unless the container's schema declares the key a string (Chapter 14). A schema can route a
statement whose filter names no value at all: where the filter and the schema together leave the key one
value — `kind <> 'b' AND kind <> 'c'` over a required `kind` declared `enum: ["a", "b", "c"]` — the
statement goes to that partition.

**Point reads.** A lookup by `id` and a complete partition key, with nothing else in the predicate —
nothing the schema does not already make true of every document — is not run as a query at all. It
is a point read — `ReadItem`, about 1 RU, no query engine — against the 2.3 RU or more a query costs
at best:

```sql
SELECT c."id", c."_ts" FROM "products" AS c
WHERE c."id" = 'bike-1' AND c."$.category" = 'bikes'
→ ReadItem("bike-1", partition "bikes")
```

A point read returns a whole document, so anything that is not a document stops it: an `ORDER BY`, a
row limit, a `GROUP BY` or an aggregate, a `SELECT DISTINCT`, an `UNNEST`, or a computed column in the
select list. Selecting columns that are the stored value at a path is fine — the promoted columns and
the service's own are taken out of the document after it is read. Compare the promoted columns
(`"id"` and the partition key column) for a point read; a cast or a `JSON_VALUE` over the partition key
still routes, but is not used to recover a read.

**`DOC` and `*` are queries.** The document column is the JSON the service sent, and the service
does not send a document the same way twice: measured on the emulator, a read returned it in the
order it was written and with a `_self` property, and a query reordered and without one. A read
would make `DOC` a different string
according to whether the statement named an `id`, so selecting it runs the lookup as a query, still
routed to one partition. So does any `JSON_VALUE` or `JSON_QUERY`, with `RETURNING` or without: each
is read through a guard the document alone does not carry — `IS_NUMBER(c.price) ? c.price : null` for
`RETURNING INTEGER`, which reads an object there as null (Chapter 7).

**A point read with something more.** `WHERE id = 'x' AND pk = 'y' AND deletedAt IS NULL` — the
shape of every by-id lookup through a soft-delete view — cannot be a point read as written, since a
read applies no predicate. The planner also considers reading the document and applying the extra
condition in process, and chooses between that and the query by what each costs for the container's
document size: a point read wins for small documents and loses for very large ones.

The predicate that counts is the whole statement's, wherever it was written. A view's own `WHERE`
and the `id` and partition key equalities written against the view are one predicate to the
service, and a lookup through the view is not read blind any more than the conjunction written out
by hand would be.

**A set of ids.** `WHERE c."id" IN ('a', 'b') AND c."$.category" = 'bikes'`, with nothing else, can be
answered with `ReadMany` — one request for the set. The planner prices it against the query. The same
clauses stop it as stop a single read; `ReadMany` returns a row per document, so a `SELECT DISTINCT`
over the set runs as a query, which returns a row per value.

## 8.6 Projections

A select list becomes a Cosmos object constructor, and the service returns only what it names:

```sql
SELECT c."id", JSON_VALUE(c."DOC", '$.name') AS "name", JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) * 2 AS "double"
FROM "products" AS c
→ SELECT VALUE { "id": c.id, "name": (IS_PRIMITIVE(c.name) ? c.name : null), "double": ((IS_NUMBER(c.price) ? c.price : null) * @p0) } FROM products c
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

**A function over `JSON_VALUE`, or a `COALESCE` or `CASE` passing one out, is computed in process
unless the path is declared a string** (8.3). The property is still extracted at the service and
rendered as text on the way back, so the document is not shipped:

```sql
SELECT UPPER(JSON_VALUE(c."DOC", '$.name')) AS "name" FROM "products" AS c
→ Cosmos:     SELECT VALUE { "$f0": (IS_PRIMITIVE(c.name) ? c.name : null) } FROM products c
  In process: UPPER($f0)
```

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
    ORDER BY c."_ts" DESC
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

**A number is sent as one Cosmos reads as itself.** Cosmos reads a number written with a decimal point
as a double, and keeps a stored integer exact. So a whole number beyond 2^53
(9,007,199,254,740,992) that a double does not hold — `1152921504606846977` — is sent as a 64-bit
integer, which compares exactly; sent with a point, it would match the document holding
`1152921504606846976` instead. A number no value Cosmos reads is — a fraction a hair from a whole number,
`2.9999999999999999`, which a double reads as 3 — is compared in process if it is a literal, and fails
the statement if it is a parameter's value.

**A parameter compared as text is checked when the statement runs.** Whether a text comparison can be
exact depends on the value — `'bikes'` can be, `'30'` cannot (8.3) — and on the characters either side
holds: a parameter may hold any character, and the service orders strings by code point where SQL orders
them by UTF-16 code unit, which part where an emoji meets a character from U+E000 (Chapter 9.3). Against a
value with nothing at or above U+D800 the two agree whatever the path holds, so a comparison of `id`, or
of `JSON_VALUE` over a path declared a string, with a parameter is sent as it is, `WHERE (c.name > @p0)`,
and the value decides when it arrives: below the line it is exact; otherwise the statement is sent with
the comparison weakened to the part of the value below U+D800 — every string SQL keeps satisfies it — and
the rows are rechecked in process. A `pattern` declaring the path's characters (Chapter 14.4) makes it
exact when the plan is made. A comparison is sent this way wherever `AND` and `OR` alone lead to it from a
conjunct of the `WHERE` — the keyset seek past two keys of Chapter 9.5 among them — and the reader
rechecks that conjunct whole, reading back what the service answered for the rest of it. Under a `NOT`
the comparison is made in process, the service sent `IS_DEFINED(…)` for the path: a weaker comparison
there would make the condition stronger. A grouping, a `DISTINCT` or a ranking over such a filter runs in
process too, there being no row of the comparison's left to recheck. Comparisons with parameters
over `_ts`, `_etag` and `RETURNING` accessors are exact already, and so is a comparison of a declared
instant with a parameter — the value is written in the stored spelling when the statement runs
(Chapter 15).

**A parameter is the value of its type.** Calcite gives `?` the type of what it is compared with, and
holds a string to that type's length: compared with `JSON_VALUE(… RETURNING VARCHAR(1))`, `'ab'` is
`'a'`, and against a `CHAR(3)` `'a'` is `'a  '`. The adapter sends the value Calcite compares, truncated
or padded the same way, so the service keeps the rows SQL does.

**Two values that may both be a JSON null are compared in process** where equal values satisfy the
comparison — `=`, `<=`, `>=` — as in `JSON_VALUE(c."DOC", '$.t') = JSON_VALUE(c."DOC", '$.u')` over paths
a schema declares a string or null. Cosmos holds a JSON null equal to a JSON null; SQL's comparison with a
null is unknown, and keeps nothing. `<`, `>` and `<>` agree and are sent.

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
| `CAST` to a sized or numeric type in a projection, and a sized one in a comparison | it converts, and the service would not; `VARCHAR(3)` truncates |
| `JSON_VALUE(… RETURNING DECIMAL)`, anywhere but the bound a comparison through it sends | it parses a stored string, and the service has the string (Chapter 7) |
| a number literal a double reads as another whole number, `2.9999999999999999` | Cosmos reads it as a double, 3, and no value it can be sent is the number (8.7) |
| `IS TRUE`, `IS FALSE` over anything but a declared boolean; `IS DISTINCT FROM` | null-versus-absent behaviour not measured |
| a string function, `\|\|`, or a `CASE`/`COALESCE` arm over `JSON_VALUE`, where the path is not declared a string | the function reads the text, and the service the stored number or boolean (8.3) |
| an integer or `DECIMAL` division | Calcite truncates or rounds in the operands' type; the service divides in doubles |
| `+`, `-`, `*` or `%` with a decimal fraction, such as `price * 1.1` | Calcite computes it exactly; the service in doubles, where `1.1` is not exact |
| a comparison selected, grouped by, or compared with something, where it can be null | the service's comparison is false where SQL's is null (8.4) |
| `GROUP BY` or `DISTINCT` over `JSON_VALUE`, where the path is not declared a string | Calcite groups the text, the service the value — `30` and `"30"` are one group or two (Chapter 10) |
| `JSON_VALUE` or `JSON_QUERY` over the result of `JSON_VALUE` | Calcite parses the text as a document; the service has the value |
| `IS NULL` over a comparison, and `COALESCE(comparison, …)` | the service has no unknown to test for (8.4) |
| a comparison under a `NOT`, or an inequality, over a `CASE` or `COALESCE` | no guard follows the `CASE` into null (8.4) |
| `SUBSTRING` without a length, `TRIM` of other characters, `TRUNCATE`/`ROUND` to decimal places | the Cosmos forms are not verified |
| an array literal, `ARRAY['x', 'y']` | not rendered yet — so functions taking one stay in process too |
| a relational join, `UNION`, `INTERSECT`, `EXCEPT` | no Cosmos equivalent (Chapter 12) |
| a subquery (`EXISTS`, `IN (SELECT …)`) | Cosmos subqueries are document-scoped only |

**Out-of-domain arithmetic fails the whole statement.** `SQRT(-1)`, `ASIN(2)` and `ACOS(2)` make Cosmos
reject the query, where Calcite would produce `NaN` for that row. They are pushed anyway, consistently
with how `SQRT` always has been; if your data can produce such inputs, guard them in the query.

**Integer overflow is computed, not wrapped.** Cosmos does arithmetic in doubles, so
`price * 100000000` is the true product. Calcite's `INTEGER` arithmetic wraps where SQL would raise,
so the two disagree once a result passes about two billion: a filter keeps the rows the true product
satisfies, and a selected product fails the statement, not being an `INTEGER`. If your data can get
there, make an operand a `DOUBLE` — `CAST(100000000 AS DOUBLE)`. Reading the value `RETURNING BIGINT`
pushes the same way, but in process it fails for every value within an `INTEGER`'s range (Chapter 7).

---

[← Previous: Tables, columns and documents](07-row-model.md) · [Contents](README.md) · [Next: Sorting and paging →](09-sorting-and-paging.md)
