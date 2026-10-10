# Apache Calcite Adapter for Azure Cosmos DB — User Manual

This manual covers everything needed to use `Apache.Calcite.Cosmos.Adapter`: installing it,
registering a Cosmos DB database with Calcite, writing queries that the service answers rather than
the client, writing data, describing what a container holds so more of each query can be pushed
down, and diagnosing what happened when a query was slower or different than expected.

It is written to be read in order the first time and looked things up in afterwards. Part I gets a
query running. Part II explains how the adapter is configured and how long what it learns is kept.
Part III is the query language, chapter by chapter. Part IV is about cost, monitoring and the cases
that go wrong. The appendices are reference material.

The reasoning behind each decision lives in
[`DESIGN.md`](../src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) and the work still outstanding in
[`TODO.md`](../TODO.md). This manual says what the adapter does and how to use it; where the *why*
helps you use it well, it is given briefly and the design document is named.

## Contents

### Part I — Getting started

1. [Introduction](01-introduction.md) — what the adapter is, how a query runs, and the principles
   that shape its behaviour
2. [Installation](02-installation.md) — packages, requirements, and the one naming rule that trips
   everyone
3. [Quick start](03-quick-start.md) — a complete program, from model to rows

### Part II — Setting up

4. [Registering Cosmos in a model](04-models.md) — the schema factory, databases, accounts,
   containers and views
5. [Connecting and authenticating](05-authentication.md) — keys, Microsoft Entra ID, client
   factories and connection modes
6. [Connections, data sources and schema lifetime](06-connections.md) — connection settings that
   matter, and how long a schema and its client live

### Part III — Querying and writing

7. [Tables, columns and documents](07-row-model.md) — the row model: `DOC`, the promoted columns,
   and reaching into a document
8. [Filtering and projecting](08-filtering-and-projection.md) — `WHERE`, `SELECT`, casts,
   parameters, partition routing and point reads
9. [Sorting and paging](09-sorting-and-paging.md) — `ORDER BY`, null placement, composite indexes
   and `OFFSET`/`FETCH`
10. [Aggregation](10-aggregation.md) — `GROUP BY`, the aggregate functions, `DISTINCT` and grouping
    sets
11. [Arrays](11-arrays.md) — `UNNEST`, subscripts and the array functions
12. [Joins](12-joins.md) — the lookup join, the self-join merge, and joining Cosmos to other sources
13. [Writing data](13-writing.md) — `INSERT`, `UPDATE` and `DELETE`
14. [Describing documents with JSON Schema](14-json-schema.md) — declared facts, what they unlock,
    and the promise they make
15. [Dates and times](15-dates-and-times.md) — instants stored as text, and the spellings that push
16. [Declaring uniqueness](16-constraints.md) — `UNIQUE` constraints and what they let the planner
    merge
17. [Full text and vector search](17-full-text-and-vector-search.md) — `FULLTEXTCONTAINS`,
    `ORDER BY RANK`, `RRF` and `VECTORDISTANCE`
18. [Geography](18-geography.md) — geodesic operators, stored GeoJSON, and what reaches the service

### Part IV — Operating

19. [Performance and cost](19-performance.md) — request units, what makes a query cheap, and the
    statistics the planner uses
20. [Monitoring and diagnostics](20-monitoring.md) — metrics, traces, index metrics and reading a
    plan
21. [Embedding the adapter in your own planner](21-custom-planner.md) — for hosts that build a
    Calcite planner rather than open a connection
22. [Troubleshooting](22-troubleshooting.md) — symptoms, causes and fixes
23. [Limitations and known issues](23-limitations.md) — what the adapter does not do, and why

### Appendices

- A. [Operand reference](appendix-a-operands.md) — every model operand, its type and default
- B. [Function and operator reference](appendix-b-functions.md) — what each SQL function becomes in
  Cosmos SQL, and what stays in process
- C. [Type reference](appendix-c-types.md) — how JSON values are read as SQL types and handed to
  .NET
- D. [Cosmos SQL in brief](appendix-d-cosmos-sql.md) — the target language, for reading the
  statements the adapter sends
- E. [Development, the emulator and the test suite](appendix-e-development.md)
- F. [Glossary](appendix-f-glossary.md)

## Conventions used in this manual

**SQL examples assume a case-sensitive connection** (`CaseSensitive = true`), which is what the
adapter's own samples use, so identifiers are double-quoted: `c."id"`, `"products"`. Cosmos
container and property names are case-sensitive, and quoting keeps them as written.

**The example containers** recur throughout:

| container | partition key | holds |
| --- | --- | --- |
| `products` | `/category` | `{ "id", "category", "name", "price", "tags": [...], "metadata": { "sku" }, "location": <GeoJSON> }` |
| `orders` | `/customerId` | orders, each with an `id`, a `customerId`, a `productId` and a `placedAt` instant |
| `shipments` | `/trackingId` | shipments, each with a `trackingId` UUID stored as text and a `carrier` |
| `events` | `/source` | events with an `at` instant stored as ISO-8601 text |

Unless a chapter says otherwise, the model registers these under a schema named `COSMOS` and names
it as the `defaultSchema`.

**What the service is sent** is shown after an arrow:

```
WHERE c."$.category" = 'bikes'     →   WHERE (c.category = @p0)
```

Literals are sent as bound parameters (`@p0`, `@p1`, …) rather than written into the statement, so
statement text never depends on the values being searched for.

**"Pushed down"** means evaluated by Cosmos DB. **"In process"** means evaluated by Calcite inside
your application, over the rows Cosmos returned. Chapter 1 explains the difference and why it
matters.

## Version

This manual describes the adapter as built against `Apache.Calcite` 2.0.1-pre.267 and the Cosmos DB
.NET SDK 3.63. It targets .NET 8 and runs on .NET 8 and later. The adapter is under active
development; where behaviour is expected to change, the chapter says so and names the issue.
