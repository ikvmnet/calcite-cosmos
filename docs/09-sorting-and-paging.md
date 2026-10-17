# 9. Sorting and paging

A sort with a row limit is the shape of every paged list, and it is where the difference between
pushed down and in process is largest. Pushed down, the service returns one page. In process, the
adapter has to read *every* matching document to find the first page, because nothing else knows
which documents come first.

This chapter is about the conditions under which an `ORDER BY` reaches the service. There are four,
and the first is the one that catches everyone.

## 9.1 Null placement: set `DefaultNullCollation = "LOW"`

Cosmos sorts a property that is null or absent **first when ascending and last when descending**, and
offers no way to change that. Calcite's default is the opposite in both directions: a bare `ORDER BY`
means *nulls last ascending, nulls first descending*.

A sort pushed to the service has to return rows in the order the plan asked for. So where the two
disagree — on any key that could be null — the adapter declines to push the sort, and the ordering
runs in process over a full read. The refusal is silent: the query works, it just reads every
matching document. And every document path is nullable, because any document might lack the
property. Out of the box, therefore, **almost every sort on a document property runs in process**.

Set the connection's default null collation to `LOW` — nulls low, which is exactly Cosmos's order:

```csharp
new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
    DefaultNullCollation = "LOW",
}
```

The `JSON_VALUE` rows below assume the container's schema declares `status` and `metadata.region` an
`enum` of strings and `price` an integer (Chapter 14); a `JSON_VALUE` key needs the type it reads
declared before it sorts at the service at all; a string key's characters are checked as the rows arrive
unless they are declared too, which 9.3 explains. `_ts` needs nothing declared.

| statement | default (`HIGH`) | `LOW` |
| --- | --- | --- |
| `ORDER BY JSON_VALUE(c."DOC", '$.status')` | in process | `ORDER BY c.status ASC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.status') DESC` | in process | `ORDER BY c.status DESC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.status') FETCH NEXT 10 ROWS ONLY` | in process | `ORDER BY c.status ASC OFFSET 0 LIMIT 10` |
| `ORDER BY JSON_VALUE(c."DOC", '$.metadata.region')` | in process | `ORDER BY c.metadata.region ASC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER)` | in process | `ORDER BY c.price ASC` |
| `ORDER BY c."_ts"` | `ORDER BY c._ts ASC` | `ORDER BY c._ts ASC` |
| `ORDER BY c."id"` | `ORDER BY c.id ASC`, checked as the rows arrive (9.3) | `ORDER BY c.id ASC`, checked as the rows arrive (9.3) |
| `ORDER BY JSON_VALUE(c."DOC", '$.status') NULLS LAST` | in process | in process |

`LOW` does not loosen anything; it changes what the query *asks for* to something the service can
deliver. The last row shows that: an explicit `NULLS LAST` is still declined, because Cosmos genuinely
cannot do it.

**`LOW`, not `FIRST`.** `FIRST` places nulls first in *both* directions, while Cosmos reverses
exactly. Under `FIRST` an ascending sort pushes and a descending one silently does not.

**It is a connection setting.** It applies to every schema on the connection (Chapter 6). If you
cannot set it, write the placement Cosmos uses into the query instead:

```sql
ORDER BY JSON_VALUE(c."DOC", '$.name') NULLS FIRST          -- ascending
ORDER BY JSON_VALUE(c."DOC", '$.name') DESC NULLS LAST      -- descending
```

Those push under any default.

**Keys that cannot be null are exempt.** `id` and `_ts` are never null, so a sort on them has no null
placement to disagree about. `_ts` pushes under any setting, and keyset paging on it works without
`LOW`, and so does `id`, whose characters the reader checks as the rows arrive (9.3). `_etag` is never
null either, and never sorts at the service (9.2).

## 9.2 One property, or a composite index

Cosmos requires a **composite index** for an `ORDER BY` on two or more properties, with the paths in
the order of the `ORDER BY` and directions that match it exactly or are exactly reversed. Without one,
the service rejects the statement. The adapter reads the container's indexing policy and pushes a
multi-key sort only where a matching composite index exists; otherwise the sort runs in process.

```sql
ORDER BY JSON_VALUE(c."DOC", '$.category'), JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) DESC
```

pushes on a container whose schema declares `category` a string and `price` an integer (9.3), and
whose indexing policy has:

```json
"compositeIndexes": [
  [ { "path": "/category", "order": "ascending" }, { "path": "/price", "order": "descending" } ]
]
```

and is evaluated in process on one that does not. A single-property sort needs no composite index: the
service serves it from the range index every path has by default.

If a multi-key sort matters, add the composite index to the container. The Cosmos emulator ignores
composite indexes entirely — it discards them on create and accepts any multi-key sort — so verify this
against a real account (Appendix E).

**A key the `WHERE` pins is not sent, and nor is a key repeated.** A key that is one value over every
row the statement reads orders nothing, so

```sql
WHERE JSON_VALUE(c."DOC", '$.category') = 'bikes'
ORDER BY JSON_VALUE(c."DOC", '$.category'), c."_ts" DESC
→ … WHERE (c.category = @p0) ORDER BY c._ts DESC
```

is a single-key sort and needs no composite index. A key that orders a path an earlier key already
orders is dropped the same way — Azure refuses even `ORDER BY c.a, c.a` without a composite index.

**Every key must be on an indexed path.** Azure refuses an `ORDER BY` over a path the indexing policy
excludes — *"The index path corresponding to the specified order-by item is excluded"* — for one key
as for several. The adapter reads the policy's included and excluded paths and sorts such a key in
process. Two cases catch people out:

- **`_etag`** is excluded from every indexing policy the service hands back, so `ORDER BY c."_etag"`
  runs in process — unless the policy's included paths name it, `"includedPaths": [{ "path":
  "/_etag/?" }]`, which the adapter reads as the one way a policy asks for it. `MAX(c."_etag")` pushes
  either way (Chapter 10): an aggregate scans, where an `ORDER BY` needs the index.
- **A computed property** is reached by no wildcard. It sorts at the service only where an included
  path names it — `"includedPaths": [{ "path": "/cp_lower/?" }]` — and in process otherwise.

The emulator enforces neither, so verify against a real account.

## 9.3 The key must be a property

Cosmos can order only by a **document path**. `ORDER BY UPPER(c.name)`, `ORDER BY c.name || 'x'` and
`ORDER BY ToString(c.label)` are each rejected by the service with *"ORDER BY item expression could not
be mapped to a document path"*. So:

- **A sort on a computed expression runs in process.** That includes a sort on a view column that is
  a `CAST`, a function call or a concatenation.
- **A sort on a column that *is* a path pushes, where the column is the value stored there**:
  `_ts` and `id` always — `id` checked for its characters as the rows arrive unless a `pattern` declares
  them (below); `JSON_VALUE(…)` read as text where the schema declares the path a string, and
  `JSON_VALUE(… RETURNING …)` of a number or a boolean where it declares the path that type (all below). Not `_etag`, which
  the service leaves out of the index unless the policy names it (9.2). A date or a time read by `RETURNING` needs its stored form declared instead
  (Chapter 15). Not `RETURNING DECIMAL` under any declaration: it parses a stored string, and so
  orders `"10"` after `"9"` where the service orders the strings (Chapter 7).
- **The promoted partition key column cannot be a sort key.** It is `VARIANT` (Chapter 7), and Calcite
  has no order for `VARIANT` values. Sort on `JSON_VALUE(c."DOC", '$.category')` instead, which pushes
  where the schema declares `category` a string.
- **One exception:** a geodesic distance. `ORDER BY CLR_ST_GEOG_DISTANCE(…)` pushes, because the
  service accepts `ST_DISTANCE` in an `ORDER BY` — as the only key (Chapter 18).
- **A declared stored form is the other route.** A column like `CAST(JSON_VALUE(…) AS UUID)` or a
  parsed timestamp is computed, but if the container declares that the stored strings sort the way the
  values do, the sort is sent on the underlying path (Chapters 14 and 15).

**A key read as text sorts at the service only over a declared string.** `JSON_VALUE` without
`RETURNING` is text: Calcite orders the text, and the service orders the stored value — by JSON type
first (Appendix D.5), and numbers by value. Over a property holding a number in one document and a
string in another those are different orders, and a pushed sort would return the service's:

```
-- over documents whose price is 7, 8, 9, 9, 30 and 30.7, and "30" in one of them
SELECT JSON_VALUE(c."DOC", '$.price') FROM "products" AS c ORDER BY 1 NULLS FIRST

as text, which is what the query asks:   '30', '30', '30.7', '7', '8', '9', '9'
the service's order:                     7, 8, 9, 9, 30, 30.7, "30"
```

So the sort runs in process unless the container's schema says the property holds a string —
`"type": "string"`, or `["string", "null"]` (Chapter 14) — where the text *is* the stored value and the
two orders are one. The same goes for a view column bound to the accessor.

**A key read with `RETURNING` sorts at the service only over a declared type, for the same reason.**
`JSON_VALUE(… RETURNING INTEGER)` is a number where the property holds one and null where it holds
anything else — an object or an array as surely as a missing property. The service still sorts by
what is stored, and puts objects and arrays after every number, so the documents the column calls
null are not where `NULLS FIRST` puts them, and a page is the wrong page:

```
-- over documents whose label is 30, [1], {"v": 1}, null, and missing
SELECT JSON_VALUE(c."DOC", '$.label' RETURNING INTEGER) FROM "items" AS c
ORDER BY 1 NULLS FIRST FETCH NEXT 3 ROWS ONLY

what the query asks:   null, null, null
the service's page:    null, null, 30        -- the array and the object sort after 30
```

So it runs in process unless the schema declares the property that type, or null: `"integer"` or
`"number"` for a numeric `RETURNING`, `"boolean"` for `RETURNING BOOLEAN`, each alone or with
`"null"`. Declare the properties you sort by, whichever way you read them.

**`RETURNING BIGINT` past the int64 range is checked as the rows arrive.** A sort reads back what the
service returns, and a stored number beyond the int64 range comes back rounded — on the emulator `2^63`
as `9223372036854775800`, below `2^63 - 1`, though the service orders it above. A number within ±2^53
reads back as itself and on the right side of every other, so the reader keeps the service's order
around those and re-sorts the rows between them, as it does for strings (below). A schema bounding the
integer within int64, with `minimum` and `maximum`, proves the order outright and the check is skipped.
`RETURNING INTEGER` needs neither: it raises on a value it cannot hold. `RETURNING DOUBLE` as the last
key needs nothing either, reading nearby large integers as one double, which only ties what the service
orders; before the last key it is checked the same way.

**Some columns never sort at the service**: a `JSON_QUERY` column, the `DOC` column, and an array
(`JSON_QUERY … RETURNING … ARRAY`). The service orders objects and arrays by rules of its own — `[]`
after `[1, 5]`, `{"b":1}` before `{"a":2}` — which are not the order of their text or of SQL's arrays.

**A string key's characters are proved when the plan is made, or checked as the rows arrive.** The
service orders strings by code point, and Calcite by UTF-16 code unit. The two agree everywhere except
where a character above U+FFFF — an emoji, say — meets one between U+E000 and U+FFFF, such as a fullwidth
letter or U+FFFD: the service puts the second first, Calcite the first. Measured on the emulator and on
Azure, and across two physical partitions a stream can be in neither order. A string sort is proved
outright where the strings it orders cannot hold both kinds:

- the schema's `pattern` rules them out — an identifier on `id`, a UUID in either case, or every
  character below U+D800 (Chapter 14.4);
- the schema lists them — an `enum` or a `const` (Chapter 14);
- the key is a declared stored form — a UUID or a timestamp, all of whose forms are ASCII (Chapters 14
  and 15);
- or the `WHERE` lists them: `WHERE c."id" IN ('a', 'b', 'c') ORDER BY c."id"` pushes, and so does a list in
  every arm of an `OR`.

Each holds in a kind's branch too: a `pattern` that one branch of a `oneOf` declares proves the sort where
the `WHERE` keeps that kind, or kinds whose patterns all prove it (Chapter 14.5).

**Anything else is sent too, and its order completed by the reader.** That includes **`id`** where no
pattern declares its characters — Azure allows every character in an `id` but `/` and `\` — and a path
declared only `"type": "string"`. A string with no character at or above U+D800 orders the same against
any other under both rules, so wherever one arrives the service's order around it is Calcite's; the
reader passes such rows on as they come, re-sorts the rare rows between two of them, and checks a page's
first and last rows the same way, reading the statement again unpaged where either is not one:

```sql
SELECT c."id" FROM "products" AS c ORDER BY c."id" OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY
→ SELECT VALUE { "id": c.id, "$k0": c.id } FROM products c ORDER BY c.id ASC OFFSET 40 LIMIT 20
```

The `"$k0"` member is the key the reader checks, selected under a name no column has; `EXPLAIN` shows the
sort as `order=[anchored]`. On ordinary text this is one statement and one page, as a declared key's would
be; Chapter 19.8 says what it costs where the characters are exotic, and the `cosmos.order` activity
(Chapter 20.3) whether a check sent anything more. Declaring `id` an identifier,
`"id": { "type": "string", "pattern": "^[A-Za-z0-9_-]+$" }`, proves the order when the plan is made and
the statement is read as it comes (Chapter 14.4). The same question decides a range comparison with a
parameter (Chapter 8.3) and `MIN` and `MAX` (Chapter 10).

Through a view, the adapter can move a sort and its row limit beneath a projection it cannot push, so
that the page is still taken at the service and the projection computed over it. That works whenever
the sort keys are plain columns of the view; it does not make a cast column orderable — as text, `10`
sorts before `9`, and ordering by the rendering is not ordering by the value.

## 9.4 What a sort cannot share a statement with

A Cosmos statement has one `ORDER BY`, and some clauses cannot be combined with it:

- **`GROUP BY`.** Cosmos does not allow both in one statement. A grouped query is grouped at the
  service and the groups sorted in process — there is one row per group by then, so this is cheap.
- **`ORDER BY RANK`** (Chapter 17). A ranked query has no other ordering.
- **An array traversal's element.** Azure rejects `ORDER BY t` over `JOIN t IN c.tags`, though the
  emulator accepts it; a sort on an unnested element runs in process.
- **A `VARIANT` key**, a `JSON_QUERY` or `DOC` key, an array key, a text key over a path no schema
  declares a string, or a `RETURNING` key over a path no schema declares that type, as above.
- **A `SELECT DISTINCT` of a property some documents lack.** A distinct and a sort share a statement
  where the key is always present: `id`, `_ts`, `_etag`, or a property the container's schema
  requires (Chapter 14). Anywhere else the distinct folds a missing property and a `null` into SQL's
  one null (Chapter 10.5), so the value it returns is no longer the property, and the sort runs in
  process over the distinct rows — one per value, so cheaply. The distinct itself still pushes.

## 9.5 Paging: `OFFSET` and `FETCH`

```sql
ORDER BY c."_ts" DESC OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY
→ ORDER BY c._ts DESC OFFSET 40 LIMIT 20
```

A row limit is pushed **with** its sort, and only once the sort is: the first twenty under the
service's order are the first twenty the query wants only if the orders agree. On a key checked as the
rows arrive (9.3) the reader checks the page's first and last rows, and takes the page itself from the
statement read again unpaged where either holds a character at or above U+D800. A `FETCH` with no
`ORDER BY` pushes on its own — "any twenty" is something the service can answer.

The limit also sets the **page size**. Cosmos returns results a page at a time, and by default fills a
page with rows a `LIMIT 5` would discard and still charges for them. The adapter asks for pages the
size of `OFFSET + LIMIT`, so a bounded query pays for the rows it returns.

Both bounds may be parameters — `OFFSET ? ROWS FETCH NEXT ? ROWS ONLY` becomes `OFFSET @p0 LIMIT @p1`
— so one prepared statement serves every page (Chapter 8).

**Prefer keyset paging to deep offsets.** `OFFSET 10000 LIMIT 20` makes the service walk ten thousand
documents to return twenty, at a cost that grows with the offset. Paging by the last key seen stays
flat:

```sql
SELECT c."id", JSON_VALUE(c."DOC", '$.name') AS "name"
FROM "products" AS c
WHERE JSON_VALUE(c."DOC", '$.seq' RETURNING INTEGER) > ?
ORDER BY JSON_VALUE(c."DOC", '$.seq' RETURNING INTEGER)
FETCH NEXT 50 ROWS ONLY
→ … WHERE (c.seq > @p0) ORDER BY c.seq ASC OFFSET 0 LIMIT 50     -- where the schema declares seq an integer
```

A keyset over a declared UUID or timestamp path works the same way once the stored form is declared
(Chapter 14). **A keyset over `id` pushes too**:

```sql
WHERE c."id" > ? ORDER BY c."id" FETCH NEXT 50 ROWS ONLY
→ … WHERE (c.id > @p0) ORDER BY c.id ASC OFFSET 0 LIMIT 50
```

The comparison against the parameter is a string order as much as the sort is, and a parameter may hold
any character. A value with nothing at or above U+D800 — the last `id` of the page before, ordinarily —
compares alike under both rules, and the statement is sent as written. Any other value is checked when the
statement runs: the comparison is sent weakened to the part of the value below U+D800, which every `id`
Calcite keeps satisfies, and the reader rechecks it and takes the page. Where a `pattern` declares `id`'s
characters (Chapter 14.4) nothing is checked at all. A comparison under an `OR` is sent the same way, and
its whole conjunct rechecked; under a `NOT` it stays in process, as does a grouping or a `DISTINCT` over a
filter holding one, which leaves no row of the comparison's to recheck.

**Key the page on `(partition key, id)`, not on `id` alone.** An `id` is unique only within its logical
partition: two documents in different partitions may share one, and where a page ends between them the
next page's `id > ?` skips the second. Each page is still one Calcite allows; the sequence of pages is not
complete. Two ways close it:

- **Page inside one partition**, with an equality on its key, where `id` is unique:

  ```sql
  WHERE JSON_VALUE(c."DOC", '$.category') = ? AND c."id" > ? ORDER BY c."id" FETCH NEXT 50 ROWS ONLY
  ```

  which pushes whole where the schema declares the partition key path a string (Chapter 14), the `id`
  comparison and the sort checked as the rows arrive.
- **Seek past both keys**, ordering by both:

  ```sql
  WHERE JSON_VALUE(c."DOC", '$.category') > ?
     OR (JSON_VALUE(c."DOC", '$.category') = ? AND c."id" > ?)
  ORDER BY JSON_VALUE(c."DOC", '$.category'), c."id"
  FETCH NEXT 50 ROWS ONLY
  ```

  which needs a composite index on `(/category, /id)` (9.2), and pushes whole: ordered, paged and filtered.
  Where a value holds a character at or above U+D800 the comparisons are weakened as above and the reader
  rechecks the `OR` whole — the equality as the service answered it — before it takes the page. Where a
  schema proves both keys' characters (Chapter 14.4) nothing is checked at all.

## 9.6 A filter that removes the nulls

A query that excludes nulls itself has no null placement left to disagree about. Where the statement's
`WHERE` reaches the service and says the sort key is there and holds a value — `IS NOT NULL` on the
key, or a comparison over it, which no null or absent property satisfies — the sort pushes under the
default placement too:

```sql
SELECT JSON_VALUE(c."DOC", '$.genre') FROM "books" AS c
WHERE JSON_VALUE(c."DOC", '$.genre') IS NOT NULL
ORDER BY 1 FETCH NEXT 20 ROWS ONLY
→ … WHERE (IS_DEFINED(c.genre) AND NOT IS_NULL(c.genre) AND IS_PRIMITIVE(c.genre))
    ORDER BY c.genre ASC OFFSET 0 LIMIT 20      -- where the schema lists the genres
```

The key still has to be one the service orders the way SQL does (9.7, item 3): the filter settles where
nulls go, and the schema says the text is the value; its characters are checked as the rows arrive
where nothing declares them (9.3) — in the example an `enum` of genres declares them. A filter that keeps
one kind of document counts
too, when that kind's branch of the schema declares the key's type (Chapter 14.5). For keys no filter
settles, **use `DefaultNullCollation = "LOW"` or an explicit `NULLS FIRST`**.

## 9.7 Checklist: why did my sort not push?

1. Is `DefaultNullCollation` `LOW`, or does the query say `NULLS FIRST` ascending / `DESC NULLS LAST`?
   (Not needed for `id`, `_ts`, or for a key the `WHERE` proves present — 9.6.)
2. Is every key a document path — `JSON_VALUE`, `id`, `_ts` — and not a cast, function or the
   promoted partition key column? Is that path indexed — not `_etag`, not excluded by the policy, and
   for a computed property, named by an included path (9.2)?
3. Does the container's schema declare the type the `JSON_VALUE` key reads? A string for a key
   without `RETURNING`; for `RETURNING INTEGER` and the other numbers an integer or a number; for
   `RETURNING BOOLEAN` a boolean — each alone or with null, outright or in the branch of a kind the
   `WHERE` keeps. Anything less sorts in process. A
   `JSON_QUERY`, `DOC` or array key never sorts at the service.
4. A string key, `id` included, pushes whatever its characters, checked as the rows arrive where nothing
   declares them (9.3); a `pattern` (Chapter 14.4), an `enum` or `const`, a declared UUID or timestamp
   form, or a `WHERE` listing the values proves it outright. A two-key sort led by a nullable key that some
   documents lack and others hold null in stays in process.
5. Two or more keys? Does the container have a composite index matching their order and directions?
   (A key the `WHERE` pins does not count — 9.2.)
6. Is the query grouped, ranked, or sorting on an unnested element — or on a `SELECT DISTINCT` of a
   property some documents lack?
7. Is there a filter under the sort that runs in process? A sort can only push if everything beneath
   it does; check the plan with `EXPLAIN PLAN FOR` (Chapter 20).

---

[← Previous: Filtering and projecting](08-filtering-and-projection.md) · [Contents](README.md) · [Next: Aggregation →](10-aggregation.md)
