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
   `id`, `_ts`, `RETURNING` accessors, declared string paths. A predicate that does not render costs a
   read of everything the rest of the predicate matches.
4. **Keep sorts and limits at the service.** Set `DefaultNullCollation = "LOW"`, sort on paths, and add
   composite indexes for multi-key sorts (Chapter 9). A bounded page that pushes reads a page; one that
   does not reads every matching document.
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
  container and a new `CosmosClient` each time.
- **Planning has a cost** of its own, in process and independent of Cosmos. It grows with the number of
  containers in the schema, since each container's rules are registered. Expose only the containers an
  application uses.

## 19.7 Throttling

When an account's throughput is exhausted, Cosmos answers 429 and the SDK retries with back-off,
according to the client's retry options. The adapter leaves that to the client; configure retries on
the `CosmosClient` through a client factory (Chapter 5) if the defaults do not suit.

---

[← Previous: Geography](18-geography.md) · [Contents](README.md) · [Next: Monitoring and diagnostics →](20-monitoring.md)
