# 13. Writing data

Cosmos SQL is read-only — it has no `INSERT`, `UPDATE` or `DELETE`. The adapter supports all three
anyway, by carrying them out as item operations through the SDK: a create, a replace or a delete per
document. Nothing about a write is rendered as Cosmos SQL text.

Execute writes with `ExecuteNonQueryAsync`; the result is the number of documents affected.

The identity needs a data-plane role that allows writes — Cosmos DB Built-in Data Contributor, or a
custom role with item writes (Chapter 5).

## 13.1 The document column is what you write

A table's columns are the document (`DOC`) and values taken out of it (`id`, `_ts`, `_etag`, the
partition key). Writing the projections as well would describe one document twice, so **`DOC` is the
only column a statement writes**. Naming any other column in an `INSERT` or a `SET` is an error.

## 13.2 `INSERT`

```sql
INSERT INTO "products" ("DOC")
VALUES ('{"id":"bike-4","category":"bikes","name":"Gravel King","price":1450}')
```

The partition key is read from the document, and the document is created with `CreateItem`. Several
rows insert several documents:

```sql
INSERT INTO "products" ("DOC") VALUES ('{"id":"a","category":"x"}'), ('{"id":"b","category":"x"}')
```

**Copying documents** is an `INSERT … SELECT` of `DOC`:

```sql
INSERT INTO "products_archive" ("DOC")
SELECT c."DOC" FROM "products" AS c WHERE c."_ts" < 1700000000
```

What is written:

- **Exactly the JSON you supplied, copied rather than re-rendered**, so large integers keep their
  digits and exponents their notation.
- **Without the service's own properties.** `_rid`, `_self`, `_etag`, `_attachments` and `_ts` are
  removed wherever they appear, so a copied document does not carry another document's identity and
  the service assigns its own.
- **No invented `id`.** A document without an `id` is sent as it is, and the service decides what to
  do with it. The adapter does not author keys.

**A conflict fails the statement.** Inserting a document whose `id` already exists in its partition
raises a `CosmosExecutionException` saying the create failed with `409 Conflict`. That is what
`INSERT` means; there is no upsert.

## 13.3 `UPDATE`

An `UPDATE` sets `DOC` — the whole document — and is carried out as a **replace** of each document the
`WHERE` clause selects:

```sql
UPDATE "products" AS c
SET "DOC" = '{"id":"bike-1","category":"bikes","name":"Trail Blazer","price":1150}'
WHERE c."id" = 'bike-1' AND c."$.category" = 'bikes'
```

The new value can be computed from the old one with any expression Calcite can evaluate over `DOC`.
For example, with a function library that has `JSON_SET` (`Fun = "mysql"`, or `"all"`):

```sql
UPDATE "products" AS c
SET "DOC" = JSON_SET(c."DOC", '$.price', 1150)
WHERE c."id" = 'bike-1' AND c."$.category" = 'bikes'
```

The new document is computed in process and written whole.

How an update works, step by step:

1. The `WHERE` clause is evaluated like any query — pushed down, routed, and a point read where it pins
   `id` and the complete partition key.
2. For each row read, the new `DOC` is computed, the service properties are removed from it, and the
   document is replaced, identified by the `id` and partition key it was read with.

Rules and refusals:

- **Only `SET "DOC" = …`.** A `SET` of `id`, `_ts`, `_etag` or a partition key column is refused while
  planning. The service forbids changing a document's identity or placement, and `_ts`/`_etag` are the
  service's.
- **The new document may not move.** If the value you write carries a different `id` or partition key
  inside it, the service rejects the replace — loudly, as it should.
- **No optimistic concurrency.** No `If-Match` is sent. The read informs the write but does not lock
  it; the last write wins.
- **A document deleted between the read and the replace** is simply not counted — the reported count is
  smaller, not an error.
- **`MERGE` is not supported.**

A targeted update — changing one property without sending the whole document, through Cosmos's patch
operation — is not implemented yet. Every `UPDATE` is a full replace, priced as one.

## 13.4 `DELETE`

```sql
DELETE FROM "products" AS c WHERE JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) < 100
```

A delete needs each document's `id` and partition key, so the adapter reads the rows the `WHERE`
selects — pushed down as usual — and deletes each one. Where the predicate pins `id` and the complete
partition key, that read is a point read. A predicate over anything is allowed: the plan shows the
read, and a container is not less deletable for lacking a predicate over its key.

A document already gone when its delete arrives is not counted, and is not an error.

### Deleting a whole partition

A `DELETE` whose predicate pins exactly the complete partition key, and nothing else:

```sql
DELETE FROM "products" AS c WHERE c."$.category" = 'discontinued'
```

can be carried out as one request — Cosmos's *delete all items by partition key* — instead of a read
and a delete per document. The adapter counts the partition first, for the affected-row count, and
then issues the single delete.

This needs an account capability that Azure enables **only on request to Azure Support**. The adapter
probes for it the first time a statement could use it (with a request that cannot delete anything)
and remembers the answer for the life of the schema. Without the capability, the same `DELETE` runs the
ordinary way. Hierarchical partition keys are not supported by the service for this operation.

> The fast path has been built and tested against its fallback; it has not been exercised against an
> account with the capability enabled. The service documents that a count taken while a partition
> delete is in progress may still include documents being removed.

## 13.5 What a write does to caches

A write through the adapter clears the lookup-join cache (Chapter 12) of the container it wrote to.
Statistics are not refreshed — call `RefreshStatistics()` after a bulk load (Chapter 6).

## 13.6 Not supported

| | |
| --- | --- |
| upsert, `MERGE` | not offered |
| patch (a partial update) | not yet — every `UPDATE` replaces |
| transactional batches | not offered; each document is written separately, so a statement that fails part-way leaves the documents already written |
| optimistic concurrency (`If-Match` on `_etag`) | not offered |
| `TRUNCATE TABLE` | not offered |
| bulk execution | configure it on the client, through a client factory (Chapter 5) |

---

[← Previous: Joins](12-joins.md) · [Contents](README.md) · [Next: Describing documents with JSON Schema →](14-json-schema.md)
