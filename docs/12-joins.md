# 12. Joins

Cosmos has no relational join. Its `JOIN` iterates a document's own arrays (Chapter 11); there is no
way to say "match each order with its product" in one statement. So a join between a container and
anything else — another container, a CSV file, a JDBC table, the same container through another view
— is performed by Calcite, in process.

That does not mean every join reads whole containers. The adapter has two strategies that keep a join
cheap, and this chapter is about when each applies.

## 12.1 The lookup join

Where one side of a join is small — a set of ids from a file, a page of orders — the adapter does not
read the whole container on the other side. It collects the small side's join keys, de-duplicates
them, and sends them to Cosmos with the statement, so only documents that could match come back:

```sql
SELECT o."id", JSON_VALUE(p."DOC", '$.name') AS "product"
FROM "orders" AS o
JOIN "products" AS p ON p."id" = JSON_VALUE(o."DOC", '$.productId')
WHERE o."$.customerId" = 'C-42'
```

```
1. orders:    … WHERE (c.customerId = @p0)                          -- the customer's orders
2. products:  … WHERE c.id IN (@k0, @k1, …, @k99)                   -- only the products they name
```

The keys go in batches of 100. A short batch is padded by repeating keys, so every batch is the same
statement; padding was measured to cost nothing. A hundred-term `IN` over an indexed path is served by
the index, and the service's router sends it only to the partitions that hold those values.

In a plan it appears as `CosmosLookupJoin`.

### When it applies

The lookup join is chosen by the planner where it is cheaper and **only** where it gives exactly the
answer a full join would:

- **An inner or a left join**, with the container on the right. A left join keeps a row whose key
  matched nothing — a null key, or a key the container has no document for — with nulls for the
  container's columns, exactly as a full join would. A right or full join has to keep the container's
  unmatched documents, which a fetch by key never reads, so those, and semi and anti joins, are joined
  the ordinary way.
- **On one equality, and nothing else in the condition.** A second condition would have to be applied
  after the fetch, and is not.
- **The container's side of the key is a document path** — `p."id"`, or `JSON_VALUE(p."DOC", '$.x')` —
  so a `WHERE` can name it.
- **Both keys are of a type a parameter can carry**: a string, a number or a boolean. The promoted
  partition key column is `VARIANT` and does not qualify — join on `JSON_VALUE(p."DOC", '$.category')`
  instead (Chapter 7).
- **Or both keys are `UUID`s, and the container's side is `CAST(JSON_VALUE(p."DOC", '$.x') AS UUID)`
  over a path the container's schema gives a UUID format** (Chapter 14). The path then holds one
  spelling per value, so each key is sent in that spelling — `c.x IN ('3f2a…', …)` — and matches exactly
  the documents whose cast equals it. Without the format, `ABC…` and `abc…` cast to the same key and no
  one spelling finds both, so the join is the ordinary one.
- **Nothing above the container's scan that a restriction would change the meaning of.** A filter,
  projection or traversal is fine; a `LIMIT`, sort or grouping on the container side is not, because
  "the first five products" is not "the first five products of each batch".

Anything else is joined by reading both sides, with the container side still filtered by whatever
predicates of its own push down.

### Which side is fetched by key

The side whose keys are collected is read normally; the container on the other side is fetched by
key. When both sides are Cosmos containers, which one is fetched follows the join as planned —
Calcite's default rule set does not swap a join's sides — so check `EXPLAIN PLAN FOR`: the container
beneath `CosmosLookupJoin`'s fetched side should be the large one.

### Caching lookups

**Within one execution** the join remembers what each key answered — absence included — for up to
4096 keys, so a key that appears many times on the small side is fetched once. This is always on.

**Across executions**, reference data tends to be looked up again and again, and a remembered answer
costs no request units at all. Declare a cache policy on the schema:

```json
"operand": {
  "endpoint": "…",
  "database": "inventory",
  "lookupCacheMaxRows": 10000,
  "lookupCacheExpireSeconds": 300
}
```

| operand | meaning |
| --- | --- |
| `lookupCacheMaxRows` | how many rows the cache may hold; a key with no match counts as one row |
| `lookupCacheExpireSeconds` | how long an answer may be believed after it was fetched |

**Both or neither.** A cache without a bound, or without a freshness policy, is not something the
adapter will guess into existence; giving one without the other is a model error.

The cache belongs to the schema, one per container, so it lives as long as the schema does
(Chapter 6). Entries are keyed by the statement and the key, so two queries filtering the same
container differently do not share answers. When the cache is full it evicts what has expired and
otherwise stops admitting new entries until the expiry provides room.

**A write through the adapter clears its container's cache.** A write from anywhere else is what the
expiry is for — choose it to match how stale an answer you can accept.

## 12.2 Joining a container to itself

A common way to give one container several relational shapes is a view per document type, each a
projection of the same container narrowed by a discriminator — `type = 'Order'`, `type = 'Invoice'` —
and a query that joins them back together on a key they share. Read naively, every view reads the
container, and a five-view query reads it five times and hash-joins the copies.

Where the join key names **at most one document**, every pair the join makes is a document paired with
itself, and the adapter answers the whole join with **one read**: the views' filters combined, and,
for a left join, the right side's columns null where its filter does not hold.

```sql
SELECT o."ref", o."total", i."paidAt"
FROM (SELECT JSON_VALUE(d."DOC", '$.ref') AS "ref", JSON_VALUE(d."DOC", '$.total') AS "total"
        FROM "docs" AS d WHERE JSON_VALUE(d."DOC", '$.type') = 'Order') AS o
LEFT JOIN
     (SELECT JSON_VALUE(d."DOC", '$.ref') AS "ref", JSON_VALUE(d."DOC", '$.paidAt') AS "paidAt"
        FROM "docs" AS d WHERE JSON_VALUE(d."DOC", '$.paidAt') IS NOT NULL) AS i
  ON i."ref" = o."ref"
```

Each side is a view-shaped projection over a filter over the same container. If `ref` is known to
identify a single document, this becomes one statement over `docs`: the order filter applied, and
`paidAt` null wherever the invoice side's filter does not hold for that document. The merge closes
over itself, so a chain of such joins becomes one read too.

A filter on a merged view's column reaches the service with that view's filter beside it — whether the
column is compared directly (`i."paidAt" = …`) or used inside an expression that is null when it is,
such as a distance from a merged view's location (Chapter 18). Wrapped in something that answers for a
null — `COALESCE`, `IS NULL` — it is not the same filter and stays in process.

### What makes a key unique

- **The partition key with `id`** always identifies one document — the service enforces it. A join
  that equates both needs nothing declared.
- **A unique key policy** on the container, with the partition key, is enforced by the service too and
  is read from the container definition.
- **Anything else must be declared** with a `UNIQUE` constraint in the model (Chapter 16). That is a
  promise the adapter cannot check, and a wrong one loses rows.

A constraint scoped by a predicate — "unique among invoices" — is used only where *both* sides of the
join are proved to satisfy that predicate.

### What it declines

Full outer joins (a document without a key would be one row merged and two joined); semi and anti
joins (not built yet); joins whose sides are not deterministic; and joins whose key is read in a way
that could make two different stored values look equal — `JSON_VALUE` reads the number `1` and the
string `"1"` alike, so a text key qualifies only where the container's declared facts give the path
one scalar type (Chapter 14).

## 12.3 Joining Cosmos to other sources

A model may hold schemas from several adapters (Chapter 4), and any of them can be joined to a Cosmos
container. The lookup join is what makes this practical: in the repository's sample, a CSV file of
three supplier rows is joined to a product container, and Cosmos is sent the three product ids rather
than asked for every product.

Make the non-Cosmos side the small one where you can, join on a column with a plain type, and check
`EXPLAIN PLAN FOR` for `CosmosLookupJoin`.

## 12.4 For hosts that build their own planner

Through a connection, nothing more is needed. A host that assembles its own planner must run
Calcite's calc rules as a pass **after** the cost-based planner — otherwise a projection that sits
above a join has nothing to implement it and the plan fails with an "unable to implement" error.
Chapter 21 shows the pass.

---

[← Previous: Arrays](11-arrays.md) · [Contents](README.md) · [Next: Writing data →](13-writing.md)
