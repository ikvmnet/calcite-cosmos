# 1. Introduction

`Apache.Calcite.Cosmos.Adapter` lets a .NET application query [Azure Cosmos DB for
NoSQL](https://learn.microsoft.com/azure/cosmos-db/) with ordinary SQL, through [Apache
Calcite](https://calcite.apache.org/). Containers appear as relational tables. A query is planned by
Calcite; as much of it as Cosmos can evaluate is translated into **Cosmos SQL** and executed by the
service, and whatever Cosmos cannot evaluate — a relational join, a set operation, a function it has
no counterpart for — Calcite evaluates inside your process over the rows that come back.

Calcite is a Java library. It runs here in-process, compiled to .NET by
[IKVM](https://github.com/ikvmnet/ikvm): there is no JVM, no JDBC driver, no Avatica server and no
second process. You open an ADO.NET connection, run a command, and read rows.

## 1.1 What the adapter is for

- **Querying Cosmos with SQL you already know** — joins, grouping, ordering, views, parameters —
  rather than with Cosmos SQL, which looks like SQL but is a much smaller language (see
  [Appendix D](appendix-d-cosmos-sql.md)).
- **Combining Cosmos with other data.** A Calcite connection can hold several schemas at once: a
  Cosmos database beside a CSV directory, a JDBC database, or another Cosmos account. A view can join
  across them, and the adapter fetches only the documents the join can use.
- **Giving schemaless documents a relational shape.** A view over a container, written in standard
  SQL/JSON, presents typed columns to an ORM, a reporting tool or a `DbDataReader`.
- **Writing.** `INSERT`, `UPDATE` and `DELETE` work against a container, though Cosmos SQL itself has
  no data-manipulation statements.

It is an **adapter, not a provider**. The ADO.NET provider is `Apache.Calcite.Data`, from the
[calcite-dotnet](https://github.com/ikvmnet/calcite-dotnet) project; this package plugs Cosmos into
it. The MongoDB, Cassandra, Gremlin and Table APIs of Cosmos DB are out of scope — they have their
own query languages.

## 1.2 How a query runs

```
  your SQL
     │
     ▼
  Calcite: parse → validate → convert to a relational plan
     │
     ▼
  Calcite's cost-based planner, with the adapter's rules registered
     │        the rules move each operator the service can evaluate into the
     │        Cosmos "convention", one container per convention
     ▼
  ┌─────────────────────────────────────┐
  │ Cosmos subtree                      │  rendered as one Cosmos SQL statement
  │ scan · filter · project · sort ·    │  and executed by the service, through
  │ aggregate · unnest · rank           │  the Cosmos DB .NET SDK
  └─────────────────────────────────────┘
     │  rows arrive a page at a time, as JSON
     ▼
  Calcite, in process: joins, set operations, residual predicates,
  anything the subtree could not express
     │
     ▼
  DbDataReader
```

A subtree of Cosmos nodes is a *statement*, not rows. Where it meets the rest of the plan, a
converter renders it to Cosmos SQL, sends it, and reads each JSON result into the row the plan above
expects. Nothing renders SQL text by string concatenation of user values: literals become bound
parameters.

Some operations are not statements at all. A lookup by `id` and a complete partition key becomes a
**point read** — about one request unit and no query engine. A write becomes item operations — create,
replace, delete — because Cosmos SQL is read-only.

## 1.3 Pushed down, or in process

Every operator in a plan is evaluated in one of two places:

- **Pushed down** — rendered into the Cosmos SQL statement and evaluated by the service, next to the
  data. Only the result crosses the network.
- **In process** — evaluated by Calcite in your application, over the rows the service returned.

Both give the same answer. They do not cost the same. A filter pushed down returns the matching
documents; the same filter in process returns *every* document in the container and then discards
most of them. A sort with a row limit pushed down returns one page; in process, it reads every
matching document to find the first page. The whole craft of using this adapter well is keeping the
expensive operators on the service side — and most of this manual is about what makes that possible.

The adapter decides operator by operator, and **partially where it can**: in `WHERE a AND b`, if `a`
can be rendered and `b` cannot, `a` is sent to Cosmos and `b` is rechecked in process over what comes
back. Chapter 8 describes this in detail.

## 1.4 The principles behind its behaviour

Several behaviours that look surprising at first follow from a small number of rules the adapter
holds itself to. Knowing them makes the rest of the manual predictable.

**It declines rather than approximates.** When Cosmos would answer a question slightly differently
from SQL — a null placed differently in a sort, a number compared with text, an aggregate that treats
JSON `null` differently — the adapter does not push that operator down. The query still runs; the
operator runs in process, where Calcite gives the SQL answer. Declining costs speed. Approximating
would cost correctness, silently.

**It never infers a document's shape by sampling.** A container has no row schema, and the adapter
does not guess one from the documents it happens to see. A wrong guess about a key, a type or an
ordering produces a *wrong* plan, not a slow one. Everything the planner knows comes from the
container's own definition (its partition key and indexing policy), from what the service guarantees
(`id`, `_ts`, `_etag`), or from what you declare (Chapters 14 and 16).

**It never emits a statement it is unsure of.** Every rule and every expression translation has a
refusal path. A statement Cosmos would reject — or worse, accept and answer differently — is never
sent.

**Its contract is Calcite's semantics.** The question the adapter asks of every pushdown is whether
the service's answer is the answer Calcite would have computed in process. Where Calcite's own
behaviour is surprising (it is, in places, and the manual says where), the adapter reproduces it
rather than "fixing" it, so that a query gives the same rows with and without the adapter.

**What you declare is trusted.** A JSON Schema or a `UNIQUE` constraint you put in the model is
believed, not checked against every document. That is what lets it make queries faster, and it is
the one place a model can change which rows a query returns. Chapters 14 and 16 say exactly what each
declaration promises.

## 1.5 What Cosmos SQL can and cannot express

The service's query language shapes what can be pushed down. In brief:

| Cosmos SQL has | Cosmos SQL does not have |
| --- | --- |
| `SELECT`, `WHERE`, `ORDER BY`, `GROUP BY`, `OFFSET … LIMIT`, `TOP`, `DISTINCT` | relational joins — its `JOIN` iterates a document's own arrays |
| scalar, string, math, array, type-checking and spatial functions | `UNION`, `INTERSECT`, `EXCEPT`, `HAVING`, window functions, CTEs |
| full text search and vector search | derived tables (`FROM (SELECT …)`) |
| `COUNT`, `SUM`, `MIN`, `MAX`, `AVG` | `ORDER BY` together with `GROUP BY` in one statement |
| | `INSERT`, `UPDATE`, `DELETE` |

So relational joins, set operations and `HAVING` on an aggregated value always run in process; a
multi-property `ORDER BY` pushes only where the container declares a matching composite index; and
writes are item operations. [Appendix D](appendix-d-cosmos-sql.md) covers the language in enough
detail to read the statements the adapter sends.

## 1.6 The pieces involved

| package | role |
| --- | --- |
| `Apache.Calcite.Cosmos.Adapter` | this adapter: the schema factory, the rules, translation and execution |
| `Apache.Calcite.Data` | the ADO.NET provider: `CalciteConnection`, `CalciteCommand`, `CalciteDataReader` |
| `Apache.Calcite.Extensions` | the cursor convention rows leave the adapter in, and the in-process operators |
| `Apache.Calcite.Geography` | the geodesic `CLR_ST_GEOG_*` operators (Chapter 18) |
| `Apache.Calcite.FullText` | the shared `CLR_FT_*` full text vocabulary (Chapter 17) |
| `Microsoft.Azure.Cosmos` | the Cosmos DB .NET SDK, which every request goes through |
| `Azure.Identity` | Microsoft Entra ID authentication when no key is given (Chapter 5) |

The adapter references the last five itself; an application adds the adapter and the provider.

---

[Contents](README.md) · [Next: Installation →](02-installation.md)
