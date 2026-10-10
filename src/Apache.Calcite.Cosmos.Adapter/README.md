# Apache.Calcite.Cosmos.Adapter

An [Apache Calcite](https://calcite.apache.org/) adapter for [Azure Cosmos DB for NoSQL](https://learn.microsoft.com/azure/cosmos-db/).
Query Cosmos containers with ordinary SQL from .NET: as much of each query as Cosmos can evaluate is
translated to **Cosmos SQL** and run by the service, and the rest — relational joins, set operations,
functions Cosmos lacks — is evaluated in process by Calcite, which runs inside your application through
IKVM.

**[User manual](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/README.md)** ·
[Source](https://github.com/ikvmnet/calcite-cosmos)

## Install

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
dotnet add package Apache.Calcite.Data
```

`Apache.Calcite.Data` is the ADO.NET provider your code talks to; this package plugs Cosmos into it.

## Register a database

```json
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
      "containers": [ "products", "orders" ]
    }
  }]
}
```

Give a `key` to use an account key, or none to sign in with Microsoft Entra ID. Omit `containers` to
expose every container, or `database` to expose the whole account.

**The factory must be named assembly-qualified**, as above, and its assembly loaded before the model is
read — `_ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();` at start-up does it.

## Query

```csharp
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
    """;

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine(reader.GetString(1));
```

Each container is a table with one column holding the whole document, `DOC`, reached with `JSON_VALUE`
and `JSON_QUERY`, plus `id`, `_ts`, `_etag` and the partition key. Set `DefaultNullCollation = "LOW"` so
sorts can run at the service, and read with the asynchronous methods — the Cosmos SDK has no
synchronous reads.

## Features

- Filters, projections, sorts, paging, aggregation and array traversal pushed down — partially where a
  whole operator cannot be
- Partition routing and point reads recovered from the predicate
- Lookup joins that fetch only the documents another source's keys match
- `INSERT`, `UPDATE` and `DELETE`
- JSON Schema and `UNIQUE` declarations that let UUIDs, timestamps and numbers stored as text push down
- Full text search, vector search and geodesic geography
- Request-unit metrics and traces through `System.Diagnostics`

## Documentation

The [user manual](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/README.md) covers
configuration, the row model, what pushes down and why, writing, declarations, performance, monitoring
and troubleshooting.

- [Quick start](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/03-quick-start.md)
- [Connecting and authenticating](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/05-authentication.md)
- [Tables, columns and documents](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/07-row-model.md)
- [Troubleshooting](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/22-troubleshooting.md)
- [Limitations](https://github.com/ikvmnet/calcite-cosmos/blob/main/docs/23-limitations.md)

## License

Apache License 2.0.
