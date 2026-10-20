# 7. Tables, columns and documents

A Cosmos container has no row schema. Two documents in the same container may share nothing but
`id`. This chapter explains how the adapter presents such a container as a table anyway, what each
column is, and how a query reaches the properties inside a document.

## 7.1 Every table has the same shape

Each container becomes a table with:

| column | SQL type | what it holds |
| --- | --- | --- |
| `DOC` | `VARCHAR NOT NULL` | the whole document, as the JSON text the service returned |
| `id` | `VARCHAR NOT NULL` | the document id |
| `_ts` | `BIGINT NOT NULL` | the last-modified time, in epoch seconds, set by the service |
| `_etag` | `VARCHAR NOT NULL` | the document's entity tag, set by the service |
| one per partition key path | `VARIANT`, nullable | the partition key value, named for its JSON path |

For the `products` container, partitioned on `/category`, that is `DOC`, `id`, `_ts`, `_etag` and
`$.category`. A partition key path is named for the JSON path it addresses:

| container's partition key | column |
| --- | --- |
| `/category` | `"$.category"` |
| `/inventory/sku` | `"$.inventory.sku"` |
| `/tenantId`, `/userId` (hierarchical) | `"$.tenantId"`, `"$.userId"` |
| `/id` | none extra — `id` is already a column |

`SELECT *` returns all of them. Nothing in the row type is inferred from documents; the columns come
from the container definition and from what the service guarantees.

## 7.2 `DOC` is the document

`DOC` is the document itself — every property, at every depth, exactly as stored. Read as a column,
it is the JSON text the service sent, unaltered: key order and number formatting are the service's,
and the system properties (`_rid`, `_self`, `_etag`, `_attachments`, `_ts`) are in it.

Everything inside a document is reached from `DOC` with SQL/JSON (7.4). **`DOC` is also the only
column a statement writes**: an `INSERT` supplies a document, an `UPDATE` replaces one (Chapter 13).

## 7.3 The promoted columns are for the planner

`id`, `_ts`, `_etag` and the partition key columns are not another way to address the document — `DOC`
already addresses all of it. They exist because Calcite's planner expresses what it knows about a
table in terms of *columns*: a unique key is a set of column positions, and nullability and predicate
reasoning follow a plain column reference rather than a function call. So a path the container
declares or the service guarantees gets a column for that knowledge to hang on:

- **The partition key with `id` is the table's unique key.** `id` is unique within a logical
  partition, so the two together identify a document.
- **`id`, `_ts` and `_etag` are non-nullable**, because the service puts them on every document. That
  is what lets a sort on one of them push down under any null placement (Chapter 9).
- **The partition key columns are nullable**, because a document may omit its partition key — it then
  lives in the container's "none" partition.

They are read-only; naming one in an `INSERT` or a `SET` is an error.

### Why the partition key column is `VARIANT`

A partition key value is a scalar — a string, a number or a boolean — but *which* one is a fact about
each document, not about the container. `VARIANT` is the SQL type for exactly that: a value whose
concrete type is learned per row. In practice:

- **Filtering on it pushes down**, and an equality pins the partition: `WHERE c."$.category" = 'bikes'`
  runs against one partition, and with `c."id" = …` beside it becomes a point read (Chapter 8).
- **Sorting on it does not.** Calcite cannot order `VARIANT` values in process and the SQL standard
  defines no order across types, so a sort keyed directly on the column is declined and runs in
  process. Sort on the path instead: `ORDER BY JSON_VALUE(c."DOC", '$.category')`, which sorts at the
  service where the container's schema declares the path a string (Chapter 9).
- **It cannot be a lookup-join key** (Chapter 12) — there is no parameter type to bind a `VARIANT` as.
  Join on `JSON_VALUE(c."DOC", '$.category')` instead.
- **To read a typed value**, select the path rather than the column: `JSON_VALUE(c."DOC",
  '$.category')` is text.

## 7.4 Reaching into a document

Use the standard SQL/JSON functions over `DOC`:

```sql
SELECT JSON_VALUE(c."DOC", '$.metadata.sku') AS "sku",
       JSON_QUERY(c."DOC", '$.tags')         AS "tagsJson"
FROM "products" AS c
WHERE JSON_VALUE(c."DOC", '$.name') = 'Trail Blazer'
```

```
→ SELECT VALUE { "sku": (IS_PRIMITIVE(c.metadata.sku) ? c.metadata.sku : null),
                 "tagsJson": ((IS_OBJECT(c.tags) OR IS_ARRAY(c.tags)) ? c.tags : null) }
  FROM products c WHERE …
```

| function | answers | over an object or array | over a scalar |
| --- | --- | --- | --- |
| `JSON_VALUE(doc, path)` | a scalar, as text | null | its text |
| `JSON_QUERY(doc, path)` | an object or array, as JSON text | the JSON text | null |

Both collapse to a plain Cosmos path, so the service evaluates them; the guards make the service
answer exactly as SQL/JSON does (null where the function is null).

**The path grammar** is `$` followed by any number of these steps:

| step | example | Cosmos path |
| --- | --- | --- |
| `.name` | `$.metadata.sku` | `c.metadata.sku` |
| `['name']` | `$['odd name']` | `c["odd name"]` |
| `[n]` | `$.tags[0]` | `c.tags[0]` |

Nesting is unlimited: `$.a.b.c.d` is one path. Three things are not supported and keep the
expression in process: a wildcard (`$.tags[*]`), a recursive descent (`$..name`), and a filter
(`$.tags[?(@ == 'x')]`). The path must be a **literal**: a path assembled at run time names a property
that is not known until the row is read, and is declined.

### What `JSON_VALUE` means

`JSON_VALUE` returns text — `VARCHAR` — whatever the property holds. The number `30` comes back as
`'30'`, `true` as `'true'`, `1e30` as `'1.0E30'` (Calcite renders numbers the Java way). An object, an
array, a JSON `null` and a missing property all come back as SQL `NULL`.

That has a consequence worth knowing for comparisons. In SQL, `JSON_VALUE(c."DOC", '$.code') = '30'`
is true of a document storing the *string* `"30"` **and** of one storing the *number* `30`, because
both render as `'30'`. The adapter preserves that: it sends Cosmos a comparison that admits both and
rechecks the rows in process. A comparison against text that no number or boolean could render as —
`= 'bikes'` — is sent exactly. Chapter 8 covers comparisons in detail.

### Giving a value a type: `RETURNING`

```sql
SELECT JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) AS "price"
FROM "products" AS c
WHERE JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) > 1000
```

`RETURNING` types the result: `INTEGER`, `BIGINT`, `DOUBLE`, `BOOLEAN`, and so on. It is the standard
way to give a document property a SQL type, it works inside a view, and it pushes down — the service
compares and returns the stored value directly. `DECIMAL` is the exception, below.

Know what it does and does not do. **`RETURNING` asserts the type; it does not convert.** In Calcite,
`RETURNING INTEGER` over a stored `30` gives 30, but over `"30"`, `30.7` or `true` it fails rather
than converting. The type it checks for is the one the JSON reader builds, which is narrower than it
looks:

| `RETURNING` | answers for a stored | fails for |
| --- | --- | --- |
| `INTEGER` | whole number in an `INTEGER`'s range | anything else — a string, a fraction, a larger number |
| `BIGINT` | whole number *beyond* that range | anything else — including `30` |
| `DOUBLE`, `FLOAT` | number written with a point or an exponent, `30.0` | anything else — including `30` |
| `SMALLINT`, `TINYINT`, `REAL` | nothing | every value |
| `BOOLEAN` | `true`, `false` | anything else — including the string `"true"` |
| `DECIMAL` | any number, **and a string that spells one** — `"30"`, `" 30 "` | a string that does not, a boolean |

A `NULL ON ERROR` clause does not catch any of these failures. A JSON `null`, an object, an array and
a missing property give `NULL` under every one. So:

- Use it where the documents really hold that type. `RETURNING INTEGER` over a property holding
  numbers is exactly right.
- If a document holds another JSON type, the column is null. The adapter reads the property through
  the type's own test — `IS_NUMBER`, `IS_BOOL` — so an object or an array reads null as it does in
  Calcite, and so does a string or a boolean under `RETURNING INTEGER`, which is what SQL says
  (`NULL ON ERROR` is the default) where Calcite fails instead (Chapter 23).
- A number the type cannot hold — `30.7` or `3000000000` under `INTEGER` — fails to read with a
  `CosmosMaterializationException` rather than coming back rounded. That is deliberate: a row type
  that bends to the data would be a suggestion, not a type. So does an integer written outside the
  64-bit range under `BIGINT`: Azure stores 2^63 and beyond as a double, `9.223372036854776E+18`, which
  no `BIGINT` holds. `RETURNING DOUBLE` reads it. Only a row the select list is computed for fails. A
  row the `WHERE` clause drops is not read as the type, even where part of that clause runs in process.
  In that case the adapter reads those documents whole rather than having the service project the column.
- Text that is not UTF-8, which Azure stores as a client wrote it, fails to read wherever a column
  carries it, `DOC` included, with a `CosmosMaterializationException` naming the column, the document
  and the bytes (Chapter 22).
- A sort on a `RETURNING` column reaches the service only where the container's schema declares the
  property that type (Chapter 9).
- To *convert* a string to a number, use `CAST(JSON_VALUE(…) AS INTEGER)`. That converts, which the
  service cannot reproduce, so the cast itself runs in process (Chapter 8 shows how much of the
  predicate around it still pushes).
- **`RETURNING DECIMAL` converts too, and is treated as that cast.** It reads a stored `"2"`, `" 2 "`
  or `"2.0"` as the number 2, which the service cannot reproduce, so a column, sort, grouping or
  partition key over it is computed in process; a comparison through it sends the service a bound
  around the number and rechecks the comparison (Chapter 8). It is not the stored value even over
  numbers: a stored `0.1` is the double's exact binary value, so `RETURNING DECIMAL(10, 1) = 0.1` is
  false of it. And under plain `DECIMAL` the literal it is compared with is truncated to a whole
  number: `= 2.5` is true of a stored 2. Where the documents hold whole numbers, `RETURNING INTEGER` is
  the spelling that pushes.
- `RETURNING TIMESTAMP` expects the property to hold a **number of epoch milliseconds**, not an
  ISO-8601 string. For instants stored as text, see Chapter 15.

### Arrays: use `JSON_QUERY … RETURNING … ARRAY`

To read an array as an array — to project it as a list, or to `UNNEST` it — use `JSON_QUERY` with an
array `RETURNING`:

```sql
SELECT JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY) AS "tags" FROM "products" AS c
```

The column arrives in .NET as an array of the element type. **Do not write `JSON_VALUE(… RETURNING
VARCHAR ARRAY)`**: Calcite accepts it, but `JSON_VALUE` extracts scalars only, so it is null for every
document in Calcite — and the adapter gives the same answer, null, rather than inventing a different
meaning. Chapter 11 covers arrays.

### Behaviour clauses: `ON EMPTY` and `ON ERROR`

`JSON_VALUE` accepts SQL's behaviour clauses — `DEFAULT 'none' ON EMPTY`, `DEFAULT 0 ON ERROR`,
`ERROR ON ERROR`. A column carrying one **is computed in process**, and so is any filter, sort or join
written over it: the clause answers for documents the path holds nothing for, so the property at the
service is not the column. `NULL ON EMPTY` and `NULL ON ERROR` are the defaults written down and push
like the bare accessor.

Know what Calcite does with them, because it is not the obvious reading. A path written without a mode
(`'$.flag'`) is **strict**: an absent property is an *error*, not an empty result, so it is `ON ERROR`
that answers for it, and `DEFAULT 'none' ON EMPTY` over a document with no `flag` gives null. A JSON
null, an object and an array take the `ON ERROR` branch too. Write `'lax $.flag'` for the absence to
count as empty.

For a column with a fallback, prefer `COALESCE(JSON_VALUE(c."DOC", '$.flag'), 'none')`: it means what
it looks like, and it pushes.

## 7.5 Null and absent

Cosmos distinguishes a property that holds JSON `null` from a property that is not there at all
(*undefined*). SQL has one `NULL`. The adapter maps both to SQL `NULL`, and:

- `IS NULL` is true of both; `IS NOT NULL` excludes both.
- A comparison against either is unknown, as in SQL, so the row is excluded either way.
- In a result, a missing property and a null one both read as `DBNull`.
- When grouping, a missing key and a null key form one group, as SQL's single `NULL` would.

When a query needs to tell them apart, use the Cosmos functions the adapter supplies:

```sql
WHERE IS_DEFINED(JSON_VALUE(c."DOC", '$.discontinuedAt'))       -- present, null or not
WHERE NOT IS_DEFINED(JSON_VALUE(c."DOC", '$.discontinuedAt'))   -- absent
```

`IS_DEFINED`, `IS_NULL`, `IS_STRING`, `IS_NUMBER`, `IS_BOOL`, `IS_ARRAY`, `IS_OBJECT` and
`IS_PRIMITIVE` all take any expression ([Appendix B](appendix-b-functions.md)).

## 7.6 Reading values in .NET

What a `DbDataReader` hands back depends on the column's SQL type:

| expression | SQL type | .NET value |
| --- | --- | --- |
| `c."DOC"`, `JSON_VALUE(…)`, `JSON_QUERY(…)` | `VARCHAR` | `string` |
| `c."id"`, `c."_etag"` | `VARCHAR` | `string` |
| `c."_ts"` | `BIGINT` | `long` (epoch seconds) |
| `JSON_VALUE(… RETURNING INTEGER)` | `INTEGER` | `int` |
| `JSON_VALUE(… RETURNING DOUBLE)` | `DOUBLE` | `double` |
| `JSON_VALUE(… RETURNING BOOLEAN)` | `BOOLEAN` | `bool` |
| `JSON_QUERY(… RETURNING VARCHAR ARRAY)` | `VARCHAR ARRAY` | `string?[]` |
| `CAST(JSON_VALUE(…) AS UUID)` over a declared UUID path | `UUID` | `Guid` |

[Appendix C](appendix-c-types.md) has the full table, including how JSON numbers become SQL numbers
and what is refused.

`JSON_QUERY` returns compact JSON — `["a","b"]` — whatever whitespace the document was stored with, so
the same query gives the same text pushed down or in process.

## 7.7 A typed view over a container

Put the pieces together and a view gives a container a relational face that tools can consume:

```sql
CREATE VIEW "Products" AS
SELECT p."id"                                              AS "Id",
       JSON_VALUE(p."DOC", '$.category')                   AS "Category",
       JSON_VALUE(p."DOC", '$.name')                       AS "Name",
       JSON_VALUE(p."DOC", '$.price' RETURNING INTEGER)    AS "Price",
       JSON_QUERY(p."DOC", '$.tags' RETURNING VARCHAR ARRAY) AS "Tags"
FROM "products" AS p
```

(as DDL where your connection accepts it, or the same `SELECT` as a model view — Chapter 4). Every
column pushes down: a query over the view reads only the properties it selects and filters at the
service, and — with `DefaultNullCollation = "LOW"` — sorts and pages there too. A text column such as
`"Category"` reads whatever the document holds as text, so sorting on it, grouping on it, or joining
another container to it by key, reaches the service only where the container's schema declares the
path a string (Chapters 9, 10, 12 and 14).

Prefer these spellings in views:

- **Text**: bare `JSON_VALUE`. Wrapping it in `CAST(… AS VARCHAR)` adds nothing to the value, but a
  cast column is computed in process over the extracted text, and a sort on it cannot be pushed. A
  cast to a *sized* type (`VARCHAR(50)`, `CHAR(8)`) truncates or pads, which the service cannot
  reproduce either.
- **Numbers and booleans**: `RETURNING`, where the documents hold that type. A `CAST` converts, which
  keeps the column in process — and a sort on it with it. So does `RETURNING DECIMAL`, which parses.
- **Arrays**: `JSON_QUERY … RETURNING <type> ARRAY`.
- **Fallbacks**: `COALESCE(JSON_VALUE(…), …)`, not a `DEFAULT … ON EMPTY` clause, which keeps the
  column in process.
- **UUIDs and instants stored as text**: see Chapters 14 and 15 — a declared stored form is what lets
  those push.

## 7.8 Why there are no other columns

It would be convenient to declare "this container has a `price` column of type `INTEGER`" and have it
appear in the row type. The adapter deliberately does not offer that. A container's documents need not
agree with any such declaration, and a column the planner believes in feeds keys, nullability and
ordering decisions; a wrong one produces wrong plans rather than slow ones. The row model is the
document column, and a query works directly off it — with `RETURNING` to state a type in standard SQL,
and the service's own type tests (`IS_NUMBER`, `IS_STRING`, …) to ask what a value is, per document,
at query time.

What *can* be declared is how a value is **stored** — that a path always holds a canonical UUID, or an
instant in one fixed format. That is narrower than a type, it is optional, and Chapter 14 describes
it.

---

[← Previous: Connections, data sources and schema lifetime](06-connections.md) · [Contents](README.md) · [Next: Filtering and projecting →](08-filtering-and-projection.md)
