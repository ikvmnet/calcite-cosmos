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

**`ExecuteReaderAsync` and `ReadAsync`, not `ExecuteReader` and `Read`.** A query over a Cosmos table
plans only in the asynchronous calling convention, and `ExecuteReader` asks for a synchronous plan,
which will not be found.

This follows from the service rather than from the adapter. The Cosmos SDK has no synchronous
data-plane API — a page of results arrives only by awaiting it — so a synchronous plan could do
nothing but block a thread for a network round trip per page. Rather than hide that behind an
interface that looks cheap, the adapter offers only the asynchronous route.

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
| `ORDER BY c."_MAP"['name']` | in-process | `ORDER BY c.name ASC` |
| `ORDER BY c."_MAP"['name'] DESC` | in-process | `ORDER BY c.name DESC` |
| `ORDER BY c."_MAP"['name'] FETCH NEXT 10 ROWS ONLY` | in-process | `ORDER BY c.name ASC OFFSET 0 LIMIT 10` |
| `ORDER BY c."_MAP"['metadata']['sku']` | in-process | `ORDER BY c.metadata.sku ASC` |
| `ORDER BY c."_MAP"['name'] NULLS LAST` | in-process | in-process |

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
foreach (var rule in ClrAsyncEnumerableRules.CalcRules())
    program.addRuleInstance(rule);
```

This is Calcite's own `Programs.CALC_PROGRAM` and it is a pass, not a set of rules for the planner. Without it a projection that sits above a join has nothing to implement it, and the failure says only that the plan cannot be implemented. It does not arise without a join, because every other projection is pushed into the container.

## The row model

A container has no row schema: two items may share nothing but `id`. So a table is **one map column
holding the whole document**, named `_MAP`, plus promoted scalar columns for the paths the service
guarantees or the container declares — `id`, `_ts`, `_etag`, and the partition key. Nothing is
inferred by sampling documents, because a wrong guess yields an incorrect plan rather than a slow one.

Reach anything else through the map column, to any depth:

```sql
SELECT c."_MAP"['metadata']['sku'] AS "sku"
FROM "products" AS c
WHERE c."_MAP"['tags'][0] = 'steel'
```

Those collapse to the Cosmos paths `c.metadata.sku` and `c.tags[0]` and are evaluated by the service.
The key must be a constant — a Cosmos path names a property statically.

### Giving a column a type

A path read through the map column is typed `ANY`, which nothing expecting typed columns — an ORM, a
BI tool — can consume, so a view over a container casts. A cast to `VARCHAR` is carried: the service
returns the value and the adapter renders it exactly as Calcite would, so the view's projection is
evaluated by the service rather than over whole documents. `CAST(<path> AS VARCHAR) = 'text'` pushes
as a comparison too, wherever no other JSON value could render as that text.

Two limits are worth knowing before writing the view. A cast to a **number** converts rather than
renders — `CAST(x AS INTEGER)` reads the stored string `"30"` as 30 — and nothing at the service
reproduces that, so it stays in-process, as does any cast carrying a width. And a cast column
**cannot be an `ORDER BY` key at the service**: the rendering is not the path underneath, and the
service will not order by an expression in any case, answering one with *"ORDER BY item expression
could not be mapped to a document path"*. So a page ordered by a cast column reads every matching
document. Order by an uncast path instead and it reads a page — subject to the null placement
above, which `id` and the partition key are exempt from, being non-nullable.

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
SELECT c."id" FROM "products" AS c WHERE FULLTEXTCONTAINS(c."_MAP"['name'], 'steel')
```

Ordering by a score becomes `ORDER BY RANK`, and `RRF` fuses two scores for hybrid search. The score
ranks the rows and never appears in the result, the service not permitting it to be projected.

**The container decides which paths these reach.** A full text function pushes down only over a path
the container declares — in its full text policy, in a full text index, or both — and `VECTORDISTANCE`
only where one of its two vectors is a declared vector path. Over anything else the service answers a
bodyless 400 that names neither the path nor the function, so the adapter declines while planning and
says which path is at fault instead. This is the same shape as multi-property `ORDER BY`, which pushes
only where a matching composite index is declared.

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

## What a query cost

Cosmos charges in request units and reports the charge on every response. The adapter records it on a `Meter` and an `ActivitySource`, both named `Apache.Calcite.Cosmos.Adapter`, so it collects the way anything else in a .NET application does:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Apache.Calcite.Cosmos.Adapter"))
    .WithTracing(t => t.AddSource("Apache.Calcite.Cosmos.Adapter"));
```

`cosmos.request_charge` is measured per response and tagged with the container and with whether the request was a `query` or a `point_read`; the `cosmos.query` span carries the total across continuations. Set `"indexMetrics": true` in the operand and the service also reports which indexes each statement used.

## Documentation

- [Adapter README](src/Apache.Calcite.Cosmos.Adapter/README.md) — the package's own overview
- [DESIGN.md](src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) — why Cosmos SQL is generated by hand, what
  the service was measured to do, and which assumptions are still unsettled
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [Apache Calcite for .NET](https://github.com/ikvmnet/calcite-dotnet) — the provider and calling conventions

## Building

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

The test suite runs against the Cosmos DB emulator, and reports inconclusive without one:

```sh
docker run -d --name cosmos-emu -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

The emulator is not a substitute for the service — it has been found both to accept statements Azure
rejects and to reject features Azure implements, full text search among them. Set
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` to run the same suite against a real account.

## License

Apache License 2.0.
