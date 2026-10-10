# 6. Connections, data sources and schema lifetime

This chapter covers the connection settings that change how the adapter behaves, why the
asynchronous methods matter, and — the part that matters most in a service — how long a Cosmos schema,
its client and everything it has learnt are kept.

## 6.1 Connection settings that matter

`CalciteConnectionStringBuilder` carries every Calcite connection property. These are the ones that
affect a Cosmos query:

| setting | recommended | why |
| --- | --- | --- |
| `Model` | your model | `inline:` followed by the JSON, or a file path |
| `CaseSensitive` | `true` | Cosmos names are case-sensitive; the manual assumes quoting as written |
| `DefaultNullCollation` | `"LOW"` | places nulls where Cosmos does, so sorts on document paths push down (Chapter 9) |
| `Fun` | as needed | enables Calcite's function libraries: `PARSE_DATETIME` (Chapter 15), `LEFT`, `RIGHT`, `REVERSE`, `REPEAT` and others; `"all"` enables every library |
| `Pooling` | default (`true`) | shares one root schema among connections with the same connection string (6.3) |
| `ConnectionIdleLifetime` | default (300) | how long a shared root survives with no open connection (6.3) |

```csharp
var connectionString = new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
    DefaultNullCollation = "LOW",
    Fun = "bigquery",
}.ConnectionString;
```

**`DefaultNullCollation` belongs to the connection, not to the schema.** Every schema on the
connection gets the same null placement — a JDBC or CSV schema beside Cosmos included. For an
application whose data is mostly in Cosmos, `LOW` is the right trade. For a mixed one it is a
decision, and there is no per-schema setting to make it with; Chapter 9 describes how to get the same
pushdown from inside a query instead.

**`Fun` is not applied inside model views.** Calcite analyses a view declared in a model with its
default configuration, whatever the connection enables. Chapter 15 gives the standard-SQL spelling to
use in a view in place of `PARSE_DATETIME`.

## 6.2 Use the asynchronous methods

**`ExecuteReaderAsync` and `ReadAsync`, not `ExecuteReader` and `Read`.** Both pairs work, and they
return the same rows. The difference is threads.

The Cosmos SDK has no synchronous data-plane API: a page of results arrives only by awaiting it. So
the synchronous pair *waits* on the asynchronous one — `ExecuteReader` while the first page is
fetched, and `Read` wherever it reaches the end of a page and has to fetch the next. A row already in
a fetched page is returned without waiting, so the cost is one blocked thread per round trip rather
than per row, but nothing reports that it is happening.

The asynchronous pair also **cancels properly**. The adapter reads results through a cursor, and the
token passed to each `ReadAsync` is the token the page request it causes runs under — so cancelling a
read cancels the request in flight, not merely the next one.

The same applies to writes: prefer `ExecuteNonQueryAsync`.

## 6.3 How long a schema lives

A Cosmos schema is not free to build. Building one creates a `CosmosClient` and reads the definition
of every container it exposes. After that it learns more lazily — a container's row count the first
time a plan wants one, whether the account allows a whole-partition delete the first time a delete
could use it — and keeps what it learns:

| what the schema keeps | read | kept for |
| --- | --- | --- |
| the `CosmosClient` | when the schema is built | the life of the schema (never disposed; Chapter 5) |
| container definitions — partition key, indexing policy, full text, vector and unique key declarations, geospatial configuration | when the schema is built | the life of the schema |
| the whole-partition delete capability | the first time a `DELETE` could use it | the life of the schema |
| row counts | the first time a plan asks | 5 minutes, or `statisticsExpireSeconds` (6.5) |
| the lookup join's cross-execution cache | as joins run | per the declared policy (Chapter 12) |

So the useful question is how often a schema is built. With `Apache.Calcite.Data` that depends on
pooling.

**With pooling (the default)**, every connection opened with an *equivalent* connection string — the
same keys and values, in any order and casing — draws on one data source the provider keeps for the
process. The model is read and the schemas built once, when the first of those connections opens, and
every later connection plans against the same root schema and so the same Cosmos schema instance.
Once no connection has been open on it for `ConnectionIdleLifetime` seconds (300 by default) the data
source is released, and the next connection builds everything again — a new client, the definitions
read again, the statistics forgotten.

**With `Pooling = false`**, each connection builds its own root when it opens and releases it when it
is disposed. Every connection pays for a new client and a definition read per container. Avoid it for
anything that opens connections often.

**To pick up a changed model** in a running process, evict the pooled data source:
`CalciteConnection.ClearPool(connection)` for one connection string, or
`CalciteConnection.ClearAllPools()` for all of them. Connections already open keep the old root until
they are disposed.

Two consequences worth acting on in a long-running service:

- **A root rebuilt after an idle period creates a new `CosmosClient`**, and the old one is not
  disposed. If your traffic has gaps longer than the idle lifetime, either raise
  `ConnectionIdleLifetime`, own the data source (6.4), or use a `clientFactory` that returns one shared
  client (Chapter 5).
- **Keep the connection string identical** wherever you open connections. Two strings that differ in
  any key — a different `Fun`, a trailing option — are two data sources, two schemas and two clients.

## 6.4 Owning the data source

For full control of when a schema is built and how long it lives, build a `CalciteDataSource`
yourself, register it as a singleton, and open connections from it. A data source you build is never
released for being idle; you dispose it.

To keep a model in the connection string and simply own its lifetime:

```csharp
// Once, at start-up — for example as a DI singleton.
var dataSource = new CalciteDataSource(new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
    DefaultNullCollation = "LOW",
}.ConnectionString);

// Per unit of work.
await using var connection = await dataSource.OpenConnectionAsync();
```

To hold the Cosmos schema object yourself — which you need in order to call `RefreshStatistics()`
(6.5), or to build the schema from a client you already own — build it in code and add it with
`CalciteDataSourceBuilder`:

```csharp
using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Data;

// The same operands a model would carry, as Calcite's own map type.
var operand = new java.util.HashMap();
operand.put("endpoint", "https://myaccount.documents.azure.com:443/");
operand.put("database", "inventory");

var containers = new java.util.ArrayList();
containers.add("products");
containers.add("orders");
operand.put("containers", containers);

// A database operand yields a CosmosSchema; without one, a CosmosAccountSchema.
var cosmos = (CosmosSchema)new CosmosSchemaFactory().create(null!, "COSMOS", operand);

var dataSource = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder
    {
        CaseSensitive = true,
        DefaultNullCollation = "LOW",
    }.ConnectionString)
    .AddSchema("COSMOS", cosmos)
    .Build();

await using var connection = await dataSource.OpenConnectionAsync();
await using var command = connection.CreateCommand();
command.CommandText = """SELECT c."id" FROM "COSMOS"."products" AS c""";
```

The same schema instance is added to every root the data source builds — the first, and any rebuilt
after `dataSource.Clear()` — so what it has learnt survives both. Unqualified names resolve against
the connection's default schema; with no model to set one, set `Schema = "COSMOS"` on the connection
string builder, or qualify table and function names.

**What this deliberately is not** is a process-wide cache keyed by account. Such a thing outlives
every decision anyone made about it and leaks between accounts. The lifetime is yours to choose, and
the schema object is what carries it.

### Building a schema directly

`CosmosSchema` also has a public constructor, for a host that wants to manage the client itself:

```csharp
using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using Azure.Identity;

using Microsoft.Azure.Cosmos;

var client = new CosmosClient(endpoint, new DefaultAzureCredential());
var database = client.GetDatabase("inventory");

var products = await CosmosContainerMetadataReader.ReadAsync(database.GetContainer("products"));

var schema = new CosmosSchema(
    new[] { products },
    container => new CosmosQueryExecutor(database.GetContainer(container.Name)));
```

Built this way, the client is yours to dispose when the application stops. The constructor takes an
optional third argument, a factory for each container's lookup cache (Chapter 12). Declarations from a
model — a JSON Schema, constraints — are applied by `CosmosSchemaFactory`; to use them, build through
the factory as above.

A `CosmosSchema` built with no executor factory can be planned against but not read: planning needs
only the container definitions, and executing such a plan fails, saying so. That is how a host can
plan, explain or test queries without a network.

## 6.5 Row counts and `RefreshStatistics`

The planner uses each container's row count to compare plans. The count comes from the service — the
document count Cosmos reports for the container — and is remembered for five minutes. Set
`statisticsExpireSeconds` in the operand to change that:

```json
"statisticsExpireSeconds": 3600
```

A clock is the wrong instrument after a bulk load, though. The moment worth re-reading at is the one
the loader knows about, so a host holding the schema can say so directly:

```csharp
cosmos.RefreshStatistics();
```

That *discards* what was read; it does not read anything. The next plan against each container pays
for the round trip, and a container nothing plans against pays nothing. This matters more than it
sounds: the service's document count lags its own writes — immediately after a load it can still
report the old figure, or zero — so fetching at the instant a load finishes captures the number least
likely to be right.

A count of **zero** is treated as unknown rather than as an empty container, because at zero rows every
plan costs the same and the planner's tie-break favours plans that push nothing down. The Cosmos
emulator always reports zero, so plans on the emulator are made without a row count. Chapter 19 says
more about statistics and cost.

---

[← Previous: Connecting and authenticating](05-authentication.md) · [Contents](README.md) · [Next: Tables, columns and documents →](07-row-model.md)
