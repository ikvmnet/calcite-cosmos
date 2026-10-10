# Apache Calcite Cosmos Adapter

Query [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) with SQL, through
[Apache Calcite](https://calcite.apache.org/), from .NET.

Containers become relational tables. As much of each query as Cosmos can evaluate is translated to
**Cosmos SQL** and executed by the service; whatever it cannot — relational joins, set operations,
functions it has no counterpart for — Calcite evaluates in-process over the rows that come back.
Calcite runs in-process via [IKVM](https://github.com/ikvmnet/ikvm): no JVM, no JDBC, no second
process.

**[Read the user manual →](docs/README.md)**

## Install

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
dotnet add package Apache.Calcite.Data
```

## A first query

```csharp
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
      "endpoint": "https://myaccount.documents.azure.com:443/",
      "database": "inventory",
      "containers": [ "products" ]
    }
  }]
}
""";

_ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();   // load the assembly the model names

await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
    DefaultNullCollation = "LOW",
}.ConnectionString);

await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = """
    SELECT c."id", JSON_VALUE(c."DOC", '$.name') AS "name"
    FROM "products" AS c
    WHERE c."$.category" = 'bikes'
    ORDER BY c."id" FETCH NEXT 10 ROWS ONLY
    """;

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine($"{reader.GetString(0)} {reader.GetString(1)}");
```

With no `key`, the adapter signs in with Microsoft Entra ID as whoever the process runs as. The whole
query is answered by Cosmos, routed to one partition, as one statement.

Three things to get right from the start, each explained in the manual:

- **Name the factory assembly-qualified**, and make sure its assembly is loaded —
  [Installation](docs/02-installation.md).
- **Set `DefaultNullCollation = "LOW"`**, or most sorts run in process —
  [Sorting and paging](docs/09-sorting-and-paging.md).
- **Use `ExecuteReaderAsync` and `ReadAsync`** — the Cosmos SDK has no synchronous reads —
  [Connections](docs/06-connections.md).

## What it does

- **Pushdown** of filters, projections, sorts, row limits, aggregation and array traversal, partially
  where a whole operator cannot go — [Filtering](docs/08-filtering-and-projection.md),
  [Sorting](docs/09-sorting-and-paging.md), [Aggregation](docs/10-aggregation.md),
  [Arrays](docs/11-arrays.md).
- **Partition routing and point reads**, recovered from the predicate.
- **Joins** that fetch only the documents another source's keys can match, and joins of a container
  to itself answered with one read — [Joins](docs/12-joins.md).
- **Writes** — `INSERT`, `UPDATE` and `DELETE` as item operations — [Writing data](docs/13-writing.md).
- **Declared facts**: a JSON Schema and `UNIQUE` constraints that let UUIDs, timestamps and numbers
  stored as text be compared, sorted and routed at the service —
  [JSON Schema](docs/14-json-schema.md), [Dates and times](docs/15-dates-and-times.md),
  [Uniqueness](docs/16-constraints.md).
- **Full text, vector and geodesic search** —
  [Full text and vector](docs/17-full-text-and-vector-search.md), [Geography](docs/18-geography.md).
- **Request-unit telemetry** through `System.Diagnostics` — [Monitoring](docs/20-monitoring.md).

It never infers a document's shape by sampling, and declines to push down anything Cosmos would answer
differently from SQL — the query still runs, in process.

## Documentation

- [User manual](docs/README.md) — installation, configuration, querying, writing, performance,
  troubleshooting and reference
- [DESIGN.md](src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) — why it works the way it does, and what the
  service was measured to do
- [TODO.md](TODO.md) — what is left
- [Apache Calcite for .NET](https://github.com/ikvmnet/calcite-dotnet) — the ADO.NET provider

## Building

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

Part of the test suite runs against Cosmos DB, starting an emulator in Docker where none is running,
or against a real account named by `COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` —
[Appendix E](docs/appendix-e-development.md).

## License

Apache License 2.0.
