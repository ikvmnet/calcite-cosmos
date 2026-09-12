# Apache Calcite Cosmos Adapter

Query [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) with SQL, through
[Apache Calcite](https://calcite.apache.org/), from .NET.

Containers become relational tables. As much of each query as Cosmos can evaluate is translated to
**Cosmos SQL** and executed by the service; whatever it cannot — joins, set operations, `HAVING` —
Calcite evaluates in-process over the rows that come back. Calcite itself runs in-process via
[IKVM](https://github.com/ikvmnet/ikvm): no JDBC, no Avatica, no second process.

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
dotnet add package Apache.Calcite.Data
```

**The factory must be named assembly-qualified.** `Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory` on its own does not resolve — the name is looked up through IKVM, where a bare namespace-qualified .NET name finds nothing, and the failure reads `ClassNotFoundException` on a type your project plainly references. The assembly must also be loaded by the time the model is read; if nothing in your program mentions the adapter except that string, touch it first:

```csharp
_ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();
```

## Signing in

Give an `endpoint` and a `key` for key authentication, or **give the endpoint alone to authenticate with Microsoft Entra ID**:

```json
"operand": {
  "endpoint": "https://account.documents.azure.com:443/",
  "database": "inventory"
}
```

The absence of a key is the request. The adapter then reaches the account as whoever the process is — a managed identity in a cluster, your signed-in tooling on a laptop — so one model file serves both. Add `tenantId` or `clientId` where that identity is ambiguous.

The identity needs a Cosmos DB **data plane** role assignment. A control-plane role that shows the account in the portal does not let it read a document, and the built-in Data Reader role includes the container metadata read this adapter performs on startup.

For anything else — a certificate, a bespoke token cache, a client your application already owns — supply `clientFactory` naming an `ICosmosClientFactory`.

## Reuse the schema to keep what it learnt

A schema reads each container's definition when it is built, and works out the rest — a row count, whether the account permits a whole-partition delete — the first time something asks. Those answers live on the schema, so within one they are computed once.

**A model builds a new schema per connection.** `CosmosSchemaFactory` runs per model read, and in the ADO.NET path that is once per `DbConnection`, so a short-lived-connection application pays those reads again every time. Nothing is shared across them, though the `CosmosClient` can be.

Build the schema yourself and register it, and the reads happen once:

```csharp
// Once, for the life of the application.
var schema = CosmosSchemaFactory.Create(...);

// Per connection.
await using var connection = new CalciteConnection("...");
await connection.OpenAsync();
connection.RootSchema.add("COSMOS", schema);
```

`RootSchema` is the supported way in, and what is registered there outlives a `Close`/`Open` cycle — the engine session is torn down only when the connection is disposed. A new `CalciteConnection` gets a new session, so register the same instance again; it is the *schema object* that carries what was learnt, not the connection.

**What this deliberately is not** is a process-wide cache keyed by account endpoint. Such a thing outlives every decision anyone made about it and leaks between accounts. The lifetime here is the caller's to choose, which is the same reason the lookup cache hangs off the schema too.

## Querying a container

`Apache.Calcite.Data` is the ADO.NET provider. Point its `Model` at a JSON model that registers the
container as a schema, and query it with `DbCommand`.

```csharp
using System.Data.Common;
using Apache.Calcite.Data;

const string model = """
{
  "version": "1.0",
  "defaultSchema": "COSMOS",
  "schemas": [{
    "name": "COSMOS",
    "type": "custom",
    "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
    "operand": {
      "endpoint": "https://account.documents.azure.com:443/",
      "key": "…",
      "database": "inventory",
      "containers": [ "products" ]
    }
  }]
}
""";

await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
}.ConnectionString);

await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = """SELECT c."id", c."category" FROM "products" AS c WHERE c."category" = 'bikes'""";

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine($"{reader.GetString(0)} {reader.GetString(1)}");
```

Omit `containers` to expose every container in the database.

## Use the asynchronous methods

**`ExecuteReaderAsync` and `ReadAsync`, not `ExecuteReader` and `Read`.** Both work. The synchronous
pair blocks a thread per row, and nothing reports that it is doing so.

This follows from the service rather than from the adapter. The Cosmos SDK has no synchronous
data-plane API — a page of results arrives only by awaiting it — so there is no synchronous read to
call. `ExecuteReader` gets its rows by waiting on the asynchronous one, which costs a blocked thread
for a network round trip per page.

**This changed, and a caller who relied on the old behaviour should read this line.** Until the CLR
conventions were merged, `ExecuteReader` over a Cosmos table failed to plan at all, which made the
cost impossible to pay by accident. It now plans and runs. Code that was correct because it could not
compile a synchronous read is no longer protected by that.

## Set `defaultNullCollation` to `LOW`

**Calcite's default null placement is the opposite of the service's, in both directions.** A bare
`ORDER BY` means *nulls last ascending, first descending* — Oracle's convention, and Calcite's
default. Cosmos sorts a null or absent property first ascending and last descending, and offers no
control over it. So a sort on a nullable key is declined for disagreeing with a placement the caller
never wrote, and the refusal is silent: the ordering runs in-process over a full container read
rather than failing. Everything reachable through the map column is nullable, so out of the box that
is every document path.

`defaultNullCollation=LOW` asks for the placement Cosmos already implements — nulls low, first
ascending and last descending — and the sort pushes:

```csharp
await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
    DefaultNullCollation = "LOW",
}.ConnectionString);
```

| statement | default (`HIGH`) | `LOW` |
|---|---|---|
| `ORDER BY JSON_VALUE(c."DOC", '$.name')` | in-process | `ORDER BY c.name ASC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.name') DESC` | in-process | `ORDER BY c.name DESC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.name') FETCH NEXT 10 ROWS ONLY` | in-process | `ORDER BY c.name ASC OFFSET 0 LIMIT 10` |
| `ORDER BY JSON_VALUE(c."DOC", '$.metadata.sku')` | in-process | `ORDER BY c.metadata.sku ASC` |
| `ORDER BY JSON_VALUE(c."DOC", '$.name') NULLS LAST` | in-process | in-process |

The row limit rides along, which is the shape that matters: a bounded page stops being a full
container read. The last row is what says this is not a fudge — `LOW` does not weaken the rule, it
changes what the query asks for, and an explicit `NULLS LAST` is still declined because Cosmos
genuinely cannot do it.

**`LOW`, not `FIRST`.** `FIRST` places nulls first in *both* directions; Cosmos reverses exactly. So
`FIRST` pushes an ascending sort and declines a descending one, which looks like nothing at all.

**It is a property of the connection, not of the schema.** A connection that also carries a JDBC or
CSV schema gets this placement over those too. For a Cosmos-primary application that is a reasonable
trade; for a mixed one it is a decision, and there is no per-schema lever to make it with.

Leaving the connection alone, two things reach the same pushdown from inside a query: state the
placement — `ORDER BY … NULLS FIRST` ascending, `ORDER BY … DESC NULLS LAST` — or remove the nulls,
since `WHERE c."category" IS NOT NULL ORDER BY c."category"` has no placement left to disagree
about. The second reaches promoted columns only; a path inside the map column projects as an
expression rather than a reference, and the guarantee does not survive that.

> **A view whose columns are `CAST(…)` does not benefit yet.** A sort written directly on a
> container pushes; the same sort through such a view still runs in-process, because the cast keeps
> the whole `Calc` above the converter. That is [#37](https://github.com/ikvmnet/calcite-cosmos/issues/37),
> and it gates this for anything consuming the adapter through a view.

## Joining a container to something else

Cosmos has no relational join — its `JOIN` cross-products a document with its own nested arrays — so a join between a container and anything else is performed outside the service. The adapter does not read the whole container to do it: the other side's join keys are collected, deduplicated, and sent with the statement, so only documents that could match come back. This is the shape Flink calls a lookup join.

It applies to an inner join on a single equality where the container's side of the key is a document path. Anything else is joined the ordinary way, by reading both sides.

Within one execution the join remembers what each key answered, absence included. To remember across executions — reference data is looked up repeatedly by definition, and a remembered answer costs no request units at all — declare a policy in the operand:

```json
"operand": {
  "…": "…",
  "lookupCacheMaxRows": 10000,
  "lookupCacheExpireSeconds": 300
}
```

Both together or neither: the bound says what the cache may hold (an absent key counts as one row), the expiry says how long an answer may be believed, and a cache missing either is not something the adapter will guess into existence. A write through the adapter clears its container's cache; a write from outside the process is what the expiry is for.

**One thing a host has to do for this to plan.** After the cost-based planner runs, apply the calc rules as a pass over the result:

```csharp
var program = new HepProgramBuilder();
foreach (var rule in ClrEnumerableRules.CalcRules())
    program.addRuleInstance(rule);
```

This is Calcite's own `Programs.CALC_PROGRAM` and it is a pass, not a set of rules for the planner. Without it a projection that sits above a join has nothing to implement it, and the failure says only that the plan cannot be implemented. It does not arise without a join, because every other projection is pushed into the container.

## The row model

A container has no row schema: two items may share nothing but `id`. So a table is **one column
holding the whole document as JSON**, named `DOC`, and everything inside a document is reached from
it with SQL/JSON:

```sql
SELECT JSON_VALUE(c."DOC", '$.metadata.sku') AS "sku"
FROM "products" AS c
WHERE JSON_VALUE(c."DOC", '$.tags[0]') = 'steel'
```

Those collapse to the Cosmos paths `c.metadata.sku` and `c.tags[0]` and are evaluated by the service.
The path must be a constant — a Cosmos path names a property statically.

Beside `DOC` are promoted scalar columns for the paths the service guarantees or the container
declares. **They are not another way to address the document; `DOC` already addresses all of it.**
They exist because Calcite's planner metadata is expressed over *field ordinals* — a key is an
`ImmutableBitSet`, and nullability and predicate flow follow a plain column reference rather than a
function call — so a path the container declares or guarantees gets an ordinal to hang that on. The
service's own keep the names it gives them, `id`, `_ts` and `_etag`; a declared path is named for the
JSON path it addresses:

| container | column |
| --- | --- |
| `"paths": ["/category"]` | `"$.category"` |
| `"paths": ["/inventory/sku"]` | `"$.inventory.sku"` |

```sql
SELECT c."id" FROM "products" AS c ORDER BY c."id" FETCH NEXT 10 ROWS ONLY
```

`id`, `_ts` and `_etag` are declared `NOT NULL`, which is what lets a sort on one push under
Calcite's default null placement. Nothing is inferred by sampling documents, because a wrong guess
yields an incorrect plan rather than a slow one.

**`DOC` is the only column a statement writes.** Every other one is a projection of it, so an insert
supplies a document and an update replaces one:

```sql
INSERT INTO "products" ("DOC") VALUES ('{"id":"1","category":"bikes","name":"Trail Blazer"}')
```

Naming any other column in an `INSERT` or a `SET` is a validation error rather than something the
adapter drops later without comment.

### Giving a column a type

`JSON_VALUE` is typed `VARCHAR`, so a view over a container needs no cast to give a column a type
that an ORM or a BI tool can consume, and `RETURNING` gives it another:

```sql
CREATE VIEW "catalogue" AS
SELECT JSON_VALUE(c."DOC", '$.name') AS "name",
       JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) AS "price"
FROM "products" AS c
```

Both project at the service rather than over whole documents. A cast written over one anyway
converts nothing and is dropped.

What `JSON_VALUE` means is reproduced at the service rather than approximated: it answers a scalar's
text and null for an object or an array, so the statement carries
`(IS_PRIMITIVE(c.name) ? c.name : null)`. Reading the raw path instead would return a number where
the plan declared text, which the reader refuses rather than coerces.

Two limits are worth knowing. A cast to a **number** converts rather than renders — `CAST(x AS
INTEGER)` reads the stored string `"30"` as 30 — and nothing at the service reproduces that, so it
stays in process, as does any cast carrying a width. And a rendered column **cannot be an `ORDER BY`
key at the service**: the rendering is not the path underneath, and the service will not order by an
expression in any case, answering one with *"ORDER BY item expression could not be mapped to a
document path"*. So a page ordered by such a column reads every matching document. Order by the path
itself and it reads a page — subject to the null placement above, which `id`, `_ts` and `_etag` are
exempt from, being non-nullable.

## What gets pushed down

| | |
|---|---|
| Filters | `WHERE`, including partial predicates — the renderable conjuncts push and the rest are rechecked in-process |
| Projections | `SELECT VALUE { … }` |
| Sorts, limits | `ORDER BY`, `OFFSET`/`LIMIT`; a multi-key sort only where a matching composite index is declared |
| Aggregation | `GROUP BY` with `COUNT`, `SUM`, `MIN`, `MAX`, `AVG` |
| Array traversal | `JOIN alias IN path` |
| Scalar functions | string, numeric and trigonometric functions where SQL and Cosmos agree on meaning |
| Partition key | recovered from the predicate, so execution stays on one physical partition |
| Row limits | a `FETCH` becomes the page size, so a bounded query stops paying for a full page |

Relational joins, `UNION`/`INTERSECT`/`EXCEPT` and `HAVING` have no Cosmos equivalent and run
in-process. Anything the adapter cannot render faithfully it declines rather than approximating.

## Full text search

Cosmos has full text search and SQL does not, so the functions — `FULLTEXTCONTAINS`,
`FULLTEXTSCORE`, `RRF` and the `IS_DEFINED` family — come from this package. A Cosmos schema declares
them, so a connection resolves them the way it resolves a table:

```sql
SELECT c."id" FROM "products" AS c WHERE FULLTEXTCONTAINS(JSON_VALUE(c."DOC", '$.name'), 'steel')
```

Ordering by a score becomes `ORDER BY RANK`, and `RRF` fuses two scores for hybrid search. The score
ranks the rows and never appears in the result, the service not permitting it to be projected.

**The container decides what these cost.** A full text function pushes down over any property path,
and what the container declares about the path — in its full text policy, in a full text index, or
both — decides the price: an index seek over a declared path, a scan over an undeclared one. Measured
against three accounts, the service answers a full text call over an undeclared path, over a container
with no policy at all, and on an account without the full text capability; refusing such a plan would
turn a slow query into a failed one. `VECTORDISTANCE` is different and pushes only where one of its
two vectors is a declared vector path, the reference requiring a vector policy to search at all; that
gate was not measured and stands. Multi-property `ORDER BY` is the other gate that stands, pushing
only where a matching composite index is declared, because there the service does refuse.

**Where the name is looked for.** An unqualified function name is resolved against the connection's
default schema and the root, and nowhere else — so name the Cosmos schema as `defaultSchema` in the
model, or qualify the call as `"COSMOS"."FULLTEXTCONTAINS"(…)` from a query rooted elsewhere. A view
declared in a model resolves against its own `path`, so a view over a Cosmos container either
qualifies the call or declares `"path": [ "COSMOS" ]`.

**Chaining the operator table is optional.** `CosmosOperators.Instance` is still there, and a host
that assembles its own planner rather than opening a connection still needs it:

```csharp
SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance)
```

Chaining it alongside a Cosmos schema is not a duplicate definition: overload resolution takes the
first candidate whose arity fits, so the chained operator answers and the schema's declaration is
never reached. It is also the way past one limit of the schema route — Calcite builds a schema
function's operand count from its parameter list, so the variadic functions are declared there up to
sixteen operands, while the operator table's checker has no bound at all.

> **`ORDER BY RANK` does not yet survive a connection.** The names resolve and the statement is built,
> but the projection that discards the score is applied by `Prepare` after planning rather than being
> a node the rank rule can match, so the clause is not recovered and the plan fails to implement. The
> predicates — `FULLTEXTCONTAINS` and the rest — are unaffected. See
> [#46](https://github.com/ikvmnet/calcite-cosmos/issues/46).

## Geography

Cosmos reads coordinates as WGS84 and answers in metres. Calcite's own `ST_*` are planar JTS over an
unprojected coordinate system and answer in the units of that system, so the two are different
questions with the same spelling — and not off by a factor, the ratio varying with latitude and with
bearing. The geodesic reading comes from
[`Apache.Calcite.Geography`](https://www.nuget.org/packages/Apache.Calcite.Geography), which this
package requires.

**There is no `GEOGRAPHY` type.** A geography and a geometry are the same type carried by the same
class, and the name of the operator applied to a value is the whole of what says which reading is
meant — `ST_GEOG_DISTANCE` rather than `ST_DISTANCE`. Calcite's `SqlTypeName` is a closed enum and a
type of one's own cannot be registered on a schema, which is how an adapter brings its functions with
it, so the type gave way to the registration. The cost is that a mixed expression is not refused:
`ST_GEOG_DISTANCE(ST_BUFFER(g, 0.1), h)` buffers in degrees and measures in metres, and both halves
run.

**Reaching the names.** Either register them on the root schema, or chain the table if you assemble
your own planner:

```csharp
GeographySchema.AddTo(rootSchema);
SqlOperatorTables.chain(SqlStdOperatorTable.instance(), GeographyOperatorTable.Instance())
```

**Reading a stored shape.** No column is typed as a geometry — nothing in Calcite converts the `ANY` a
map lookup yields into one — so a shape in a document reaches an operator by being parsed out of text:

```sql
SELECT c."id"
FROM "products" AS c
WHERE ST_GEOG_DWITHIN(
        ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location')),
        ST_GEOG_GEOMFROMGEOJSON('{"type":"Point","coordinates":[-122.3,47.6]}'),
        1000)
```

That pushes. `JSON_QUERY` over `DOC` resolves to a document path, so the constructor collapses onto
it and the statement names the property — `ST_DISTANCE(c.location, {…}) <= 1000`. The service reads
the property as the shape, so the text and the parsing are a round trip it never needed.

**What pushes.** `ST_GEOG_DISTANCE`, `ST_GEOG_WITHIN`, `ST_GEOG_INTERSECTS` and `ST_GEOG_ISVALID` are
the service's own functions under another name. Two more push without being spatial calls at
all: `ST_GEOG_GEOMETRYTYPE`, since GeoJSON records the type as a member, so over a stored shape it is
`c.location.type`; and `ST_GEOG_ASGEOJSON` in a projection, since the document already holds the
GeoJSON and re-serialising a shape just parsed is a round trip. `ST_GEOG_DWITHIN` becomes the distance comparison the
reference documents a spatial index as answering. A geography constant is written out as the GeoJSON
object. A constructor over a *computed* string is declined and stays in process, because rendering one
would mean evaluating it.

**A geodesic call over a planar container is refused while planning.** The Cosmos spelling is the
unprefixed one, so what a rendered `ST_DISTANCE` means at the service is decided by the container's
`geospatialConfig` rather than by the name in the query. Over a container reading `Geometry` the
service would answer the planar question, in the units of the coordinate system, and say nothing about
having done so.

> **A pushed predicate is not rechecked in process.** These push exactly or they do not push. Whether
> the package's S2 evaluator agrees with the service at a polygon edge, across the antimeridian, at
> the poles, or on a distance sitting exactly on a threshold has not been measured, and a recheck that
> disagrees discards rows the service returned.

## What a query cost

Cosmos charges in request units and reports the charge on every response. The adapter records it on a `Meter` and an `ActivitySource`, both named `Apache.Calcite.Cosmos.Adapter`, so it collects the way anything else in a .NET application does:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Apache.Calcite.Cosmos.Adapter"))
    .WithTracing(t => t.AddSource("Apache.Calcite.Cosmos.Adapter"));
```

`cosmos.request_charge` is measured per response and tagged with the container and with whether the request was a `query` or a `point_read`; the `cosmos.query` span carries the total across continuations. Set `"indexMetrics": true` in the operand and the service also reports which indexes each statement used.

## Telling the planner the data changed

A row count comes from the service and is remembered for five minutes; `"statisticsExpireSeconds"` in the operand says otherwise. A clock is the wrong instrument after a bulk load, though — the moment worth re-reading at is the one the loader knows about. A host holding the schema it registered can say so:

```csharp
schema.RefreshStatistics();
```

That discards what was read; it does not read anything. The next plan against a container pays for the round trip, and a container nothing plans against pays nothing. This matters more than it sounds, because the service's count lags its own writes — fetching at the instant a load finishes captures the number least likely to be right.


## Documentation

- [Adapter README](src/Apache.Calcite.Cosmos.Adapter/README.md) — the package's own overview
- [DESIGN.md](src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) — why Cosmos SQL is generated by hand, what
  the service was measured to do, and which assumptions are still unsettled
- [Benchmarks README](src/Apache.Calcite.Cosmos.Benchmarks/README.md) — what planning a statement
  costs, stage by stage, and how that cost grows
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [Apache Calcite for .NET](https://github.com/ikvmnet/calcite-dotnet) — the provider and calling conventions

## Building

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

Some of the suite runs against a Cosmos DB account, and **starts an emulator for itself** where
Docker is available and nothing is already listening on 8081. Nothing is started unless a test asks
for an account, so a run of the planner tests never waits on it.

To use one you started, which is what CI does, start it before the run and the suite will find it:

```sh
docker run -d --name cosmos-emu -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

Without Docker and without an emulator those tests report inconclusive, so the suite stays runnable
— though be aware of what a skipped run does not check: a pushdown that plans correctly and returns
the *wrong rows* is invisible without a service.

The emulator is not a substitute for the service either — it has been found both to accept statements
Azure rejects and to reject features Azure implements, full text search among them. Set
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` to run the same suite against a real account, which
takes precedence over any emulator.

The planner has its own benchmarks, which need no service at all:

```sh
dotnet run --project src/Apache.Calcite.Cosmos.Benchmarks -c Release -f net10.0 -- --filter '*Pipeline*'
```

They measure what it costs to turn a statement into a plan and into Cosmos SQL, never what it costs
to execute one. See the [benchmarks README](src/Apache.Calcite.Cosmos.Benchmarks/README.md).

## License

Apache License 2.0.
