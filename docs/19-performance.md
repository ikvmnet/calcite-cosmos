# 19. Performance and cost

Cosmos charges for every request in **request units** (RU), and reports the charge on every response.
What a query costs depends almost entirely on how much the service has to read and return, which is
what this chapter is about: how to keep work at the service, how the planner chooses, and how to find
out what a query really cost.

## 19.1 What makes a query cheap

In rough order of how much they matter:

1. **Pin the partition key.** An equality on the complete partition key confines a query to one
   logical partition instead of fanning out to every physical one. The adapter recovers it from the
   predicate automatically — but only from literals, not parameters (Chapter 8).
2. **Look up by `id` and partition key.** That is a point read: about 1 RU, against 2.3 RU or more for
   the cheapest query. Keep the predicate to exactly those two equalities and the select list to
   document paths (Chapter 8).
3. **Keep filters at the service.** Prefer predicates that render exactly (Chapter 8): comparisons on
   `id`, `_ts`, `RETURNING` accessors other than `DECIMAL`, declared string paths. A predicate that does
   not render costs a read of everything the rest of the predicate matches.
4. **Keep sorts and limits at the service.** Set `DefaultNullCollation = "LOW"`, sort on paths whose
   type the schema declares, and add composite indexes for multi-key sorts (Chapter 9). A bounded page
   that pushes reads a page; one that does not reads every matching document.
5. **Select only what you need.** A document comes back whole unless the statement names properties.
   Avoid `SELECT *` and selecting `DOC` in hot paths.
6. **Let joins fetch by key.** A join against a container is cheap when it can be a lookup join
   (Chapter 12), and costs a full read of the container when it cannot.
7. **Declare what you know.** A JSON Schema turns UUID, numeric-text and instant comparisons that would
   read the container into indexed, routed ones (Chapters 14 and 15).
8. **Index what you filter on.** A filter over a path the indexing policy excludes is a scan of that
   path. The adapter reads the indexing policy and prices such filters higher, but it cannot make them
   cheap.

## 19.2 Measured facts worth knowing

These were measured against real accounts; they say where *not* to spend effort.

| question | answer |
| --- | --- |
| Is `IN (…)` cheaper than an `OR` chain? `BETWEEN` than two comparisons? | No — identical to the hundredth of an RU at 3, 10 and 50 values. The service normalises them. |
| Is `TOP n` cheaper than `OFFSET 0 LIMIT n`? | No — identical. |
| Is a 100-key `IN` (the lookup join's batch) served by the index? | Yes, over an indexed path. |
| Does an `IN` over the partition key fan out to every partition? | No — the gateway routes it to the partitions holding the values. |
| Is a point read always cheaper than the query it replaces? | For small documents, about threefold cheaper. For very large documents (~100 KB) it can be about threefold *more* expensive; the planner prices both by document size. |
| Is a case-insensitive comparison (`STRINGEQUALS(…, true)`) a scan? | No — it uses the index. What an exact comparison adds is routing and point reads. |
| Is an index used for a predicate on an unindexed path, whatever the spelling? | No. "Index-friendly" is a property of the path, not of the spelling. |

## 19.3 How the planner chooses

Calcite's planner is cost-based: it builds alternatives — pushed down, partly pushed, in process — and
picks the cheapest by its estimate. The adapter's estimates come from:

- **The container's row count**, read from the service (Chapter 6). It is approximate and lags writes.
  A count of zero is treated as unknown, because at zero rows every plan ties and the tie favours plans
  that push nothing.
- **The partition key**: a statement pinned to one partition is estimated cheaper.
- **Index coverage**: a filter over an unindexed path, or a full text call over an undeclared path, is
  priced as a scan.
- **Document width**: what crosses the network is priced by row width, with the whole document counted
  as many values — so pushing a projection is seen to save something.
- **Request-unit coefficients** for point reads against queries, measured, so the choice between them
  depends on document size.
- A flat **discount** on work in the Cosmos convention, so that, other things equal, the service does
  the work.

The model is still mostly inference rather than a model in request units; it ranks plans well in the
common cases and can choose badly in unusual ones. When a plan looks wrong, check the row count first:
a stale or missing count is the most common cause, and `RefreshStatistics()` (Chapter 6) is the fix
after a load.

## 19.4 Statistics

| what | where it comes from | kept for |
| --- | --- | --- |
| row count | the container's reported document count | 5 minutes; `statisticsExpireSeconds` to change |
| unique keys | partition key + `id`; unique key policy; declared constraints | the life of the schema |
| sort order of a scan | *never reported* — a Cosmos scan has no order without `ORDER BY`, whatever is indexed | — |

## 19.5 Finding out what a query cost

The adapter reports the charge of every response as a metric and on a trace span (Chapter 20). The
quickest way to see the cost of a specific query in development is an `ActivityListener` (Chapter 3)
that prints each `cosmos.query` span's `db.query.text` and `cosmos.request_charge` tags. Set
`"indexMetrics": true` in the operand while investigating, and each span also says which indexes the
statement used and which it could have used.

## 19.6 Threads and latency

- **Read asynchronously** (Chapter 6). The synchronous `Read` blocks a thread every time a page is
  fetched.
- **Direct mode** is the SDK's default and lower-latency; gateway mode adds a hop (Chapter 5).
- **Reuse schemas and clients** (Chapter 6). A schema built per connection pays a definition read per
  container, a new `CosmosClient`, and a first plan of every statement each time: declarations compiled
  and filters reasoned about again.
- **Planning has a cost** of its own, in process and independent of Cosmos. It grows with the number of
  containers in the schema, since each container's rules are registered. Expose only the containers an
  application uses.
- **A filter's facts are reasoned about once per distinct predicate.** What a filter proves of the documents
  it keeps is worked out when a statement is first planned — a millisecond or a few for each combination of
  its conditions the planner asks about — and reused when the same statement is planned again over the same
  schema, on any connection that shares it (Chapter 6). A container that declares no schema pays this too,
  since what Cosmos guarantees of `id`, `_ts` and `_etag` is reasoned about beside every filter.
- **What a filter sends is proved once per distinct filter, too** (Chapter 8). A filter's first plan pays a
  few to some tens of milliseconds for it — one to three conditions about 5 to 30 ms, five to seven or a long
  `IN` list up to about 160 ms, a rare filter of many conditions more — and the same filter planned again over
  the same schema looks the answer up, in a millisecond or less. A schema keeps the last 4,096 such answers.
- **Each schema builds its rendering reasoning once, in the background.** The first plan over a schema object
  starts it on a thread of its own: the process's first a few seconds, since it reads the adapter's rules
  then, and a schema made later a fraction of a second to about two. A first plan that arrives before it is
  ready waits for it, at most five seconds, then declines and reads the container for that filter (Chapter
  8). Keep schemas — one data source, or one `CosmosSchema` — rather than building one per request.
- **A declared schema adds to planning** (Chapter 14). A filter over a container whose schema has conditional
  rules — a `oneOf` by kind, an `if` — costs more for each predicate, since the kinds a document may be are
  reasoned about case by case, and the first statement to reach the container in each schema object compiles
  its schema: a few hundred milliseconds, rising to about a second at tens of thousands of rules.
- **Joins of views over one container are merged before anything else is planned** (Chapter 12), so a
  table-per-type hierarchy plans like the one read it becomes rather than like the joins it was written
  as. A query over five such views still takes seconds to plan rather than milliseconds — Calcite folds
  constants by compiling them, and explores the projections and filters above the read — and a statement
  is planned on every execution, so this is paid each time it runs.

## 19.7 Throttling

When an account's throughput is exhausted, Cosmos answers 429 and the SDK retries with back-off,
according to the client's retry options. The adapter leaves that to the client; configure retries on
the `CosmosClient` through a client factory (Chapter 5) if the defaults do not suit.

## 19.8 Orders checked as the rows arrive

A sort, a `MIN` or `MAX`, or a comparison with a parameter over strings whose characters nothing
declares — `id`, or a path declared only `"type": "string"` — is sent to the service and checked by the
reader as the rows come back (Chapter 9.3). So is a `RETURNING BIGINT` sort over an integer nothing
bounds. What that costs:

- **On data whose strings lie below U+D800 — nearly all text — a scan of each key, and nothing else.**
  The statement is sent as written, read once, and its page is the service's page. The statement also
  selects each key it checks under a member of its own (`"$k0": c.id`), a few bytes a row.
- **Where a page's first or last row holds a character at or above U+D800** — an emoji, a private-use
  character, a fullwidth form, U+FFFD — the reader cannot tell whether the service chose Calcite's rows,
  and reads the statement again without its `OFFSET`/`LIMIT`, in order, stopping once the page is out and
  the next row below U+D800 has arrived. That is one more request, never more rows than sorting in process
  reads. A `DISTINCT` under an `OFFSET` is read that way from the start.
- **Where a keyset seek's parameter holds such a character**, the comparison is sent weakened to the part
  of the value below U+D800 and rechecked in process: the page reads from a little before the value
  rather than from it.
- **Where a `MIN` or `MAX` comes back as such a string**, or as a number past 2^53, its argument is read
  again in order, up to the first value below the line.
- **Rows between two such values** are held and re-sorted: as many as there are in a row of them.

`cosmos.order.reissued` and `cosmos.order.buffered` on the `cosmos.order` activity say when either
happened (Chapter 20.3). **Declaring the characters removes the check altogether**: a `pattern` that rules
out U+D800 and above — an identifier on `id`, a UUID, `^[\u0000-퟿]*$` — proves the order when the
plan is made, and the statement is read as it comes (Chapter 14.4). A `minimum` and `maximum` within
±2^53 do the same for an integer.

Each statement the reader sends is read by one iterator, from its start. A cross-partition `ORDER BY`
resumed from a continuation token in a new iterator was measured to lose and repeat rows, so the
fallback is always a statement of its own.

---

[← Previous: Geography](18-geography.md) · [Contents](README.md) · [Next: Monitoring and diagnostics →](20-monitoring.md)
