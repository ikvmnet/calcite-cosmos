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

| statement | default (`HIGH`) | `LOW` |
| --- | --- | --- |
| `ORDER BY JSON_VALUE(c."DOC", '$.name')` | in process | `ORDER BY c.name ASC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.name') DESC` | in process | `ORDER BY c.name DESC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.name') FETCH NEXT 10 ROWS ONLY` | in process | `ORDER BY c.name ASC OFFSET 0 LIMIT 10` |
| `ORDER BY JSON_VALUE(c."DOC", '$.metadata.sku')` | in process | `ORDER BY c.metadata.sku ASC` |
| `ORDER BY c."id"` | `ORDER BY c.id ASC` | `ORDER BY c.id ASC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.name') NULLS LAST` | in process | in process |

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

**Keys that cannot be null are exempt.** `id`, `_ts` and `_etag` are non-nullable, so a sort on them
has no null placement to disagree about and pushes under any setting. Keyset paging on `id` works
without `LOW`.

## 9.2 One property, or a composite index

Cosmos requires a **composite index** for an `ORDER BY` on two or more properties, with the paths in
the order of the `ORDER BY` and directions that match it exactly or are exactly reversed. Without one,
the service rejects the statement. The adapter reads the container's indexing policy and pushes a
multi-key sort only where a matching composite index exists; otherwise the sort runs in process.

```sql
ORDER BY JSON_VALUE(c."DOC", '$.category'), JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) DESC
```

pushes on a container whose indexing policy has:

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

## 9.3 The key must be a property

Cosmos can order only by a **document path**. `ORDER BY UPPER(c.name)`, `ORDER BY c.name || 'x'` and
`ORDER BY ToString(c.label)` are each rejected by the service with *"ORDER BY item expression could not
be mapped to a document path"*. So:

- **A sort on a computed expression runs in process.** That includes a sort on a view column that is
  a `CAST`, a function call or a concatenation.
- **A sort on a column that *is* a path pushes**: `JSON_VALUE(…)`, `JSON_VALUE(… RETURNING …)`, `id`,
  `_ts`, `_etag`.
- **The promoted partition key column cannot be a sort key.** It is `VARIANT` (Chapter 7), and Calcite
  has no order for `VARIANT` values. Sort on `JSON_VALUE(c."DOC", '$.category')` instead.
- **One exception:** a geodesic distance. `ORDER BY CLR_ST_GEOG_DISTANCE(…)` pushes, because the
  service accepts `ST_DISTANCE` in an `ORDER BY` — as the only key (Chapter 18).
- **A declared stored form is the other route.** A column like `CAST(JSON_VALUE(…) AS UUID)` or a
  parsed timestamp is computed, but if the container declares that the stored strings sort the way the
  values do, the sort is sent on the underlying path (Chapters 14 and 15).

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
- **A `VARIANT` key**, as above.

## 9.5 Paging: `OFFSET` and `FETCH`

```sql
ORDER BY c."id" OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY
→ ORDER BY c.id ASC OFFSET 40 LIMIT 20
```

A row limit is pushed **with** its sort, and only once the sort is: the first twenty under the
service's order are the first twenty the query wants only if the orders agree. A `FETCH` with no
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
WHERE c."id" > ?
ORDER BY c."id"
FETCH NEXT 50 ROWS ONLY
→ … WHERE (c.id > @p0) ORDER BY c.id ASC OFFSET 0 LIMIT 50
```

A keyset over a declared UUID or timestamp path works the same way once the stored form is declared
(Chapter 14).

## 9.6 A filter that removes the nulls

A query that excludes nulls itself has no null placement left to disagree about. The adapter
recognises an explicit `IS NOT NULL` on the sort key for this — but only where the key is a promoted
column, because Calcite carries that knowledge through a plain column and not through a function
call. Since the only nullable promoted columns are the partition key columns, which cannot be sort
keys at all (9.3), in practice **use `DefaultNullCollation = "LOW"` or an explicit `NULLS FIRST`**
rather than relying on a filter.

## 9.7 Checklist: why did my sort not push?

1. Is `DefaultNullCollation` `LOW`, or does the query say `NULLS FIRST` ascending / `DESC NULLS LAST`?
   (Not needed for `id`, `_ts`, `_etag`.)
2. Is every key a document path — `JSON_VALUE`, `id`, `_ts`, `_etag` — and not a cast, function or
   the promoted partition key column?
3. Two or more keys? Does the container have a composite index matching their order and directions?
4. Is the query grouped, ranked, or sorting on an unnested element?
5. Is there a filter under the sort that runs in process? A sort can only push if everything beneath
   it does; check the plan with `EXPLAIN PLAN FOR` (Chapter 20).

---

[← Previous: Filtering and projecting](08-filtering-and-projection.md) · [Contents](README.md) · [Next: Aggregation →](10-aggregation.md)
