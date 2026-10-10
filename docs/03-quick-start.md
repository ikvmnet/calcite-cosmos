# 3. Quick start

This chapter builds a complete console program that queries a container, then shows how to see what
the adapter sent to Cosmos. It assumes a container named `products` in a database named `inventory`,
partitioned on `/category`, holding documents such as:

```json
{ "id": "bike-1", "category": "bikes", "name": "Trail Blazer", "price": 1200, "tags": ["steel", "29er"] }
```

No account to hand? [Appendix E](appendix-e-development.md) starts the Cosmos DB emulator in one
command, and the repository's sample program seeds a `products` container like this one.

## 3.1 The program

```csharp
using System;
using System.Threading.Tasks;

using Apache.Calcite.Data;

class Program
{
    const string Model = """
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

    static async Task Main()
    {
        // The model names the factory by string; make sure its assembly is loaded.
        _ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();

        await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
        {
            Model = "inline:" + Model,
            CaseSensitive = true,
            DefaultNullCollation = "LOW",
        }.ConnectionString);

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c."id", JSON_VALUE(c."DOC", '$.name') AS "name"
            FROM "products" AS c
            WHERE c."$.category" = 'bikes'
            ORDER BY c."id"
            FETCH NEXT 10 ROWS ONLY
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Console.WriteLine($"{reader.GetString(0)}  {reader.GetString(1)}");
    }
}
```

With no `key` in the operand, the adapter signs in with Microsoft Entra ID as whoever the process is
running as — your signed-in Azure CLI or Visual Studio account on a laptop, a managed identity in
Azure. Add `"key": "…"` to use an account key instead. Chapter 5 covers both.

## 3.2 What each part does

**The model** registers a schema named `COSMOS`, built by `CosmosSchemaFactory`, exposing the
`products` container of the `inventory` database as a table named `products`. `"defaultSchema"`
makes unqualified names — `"products"`, and the adapter's own functions — resolve there. Chapter 4
describes the model in full.

**`Model = "inline:" + …`** passes the model as a string. A path to a model file works too:
`Model = "path/to/model.json"`.

**`CaseSensitive = true`** keeps identifiers as written. Cosmos names are case-sensitive, so this is
the setting the rest of the manual assumes.

**`DefaultNullCollation = "LOW"`** tells Calcite to place nulls the way Cosmos does — first when
ascending, last when descending. Without it, almost every `ORDER BY` over a document property is
evaluated in process rather than by the service. Chapter 9 explains why; for now, set it.

**`c."$.category"`** is the partition key, promoted to a column of its own and named for the JSON
path it addresses. `c."id"` is the document id. Everything else in a document is reached through the
`DOC` column with `JSON_VALUE` and `JSON_QUERY`. Chapter 7 describes the row model.

**`ExecuteReaderAsync` and `ReadAsync`.** Use the asynchronous methods. The Cosmos SDK has no
synchronous way to fetch a page of results, so the synchronous `ExecuteReader` and `Read` block a
thread every time a page has to be fetched. The asynchronous pair also cancels properly: the token you
pass to `ReadAsync` is the token the page request it causes runs under.

## 3.3 What Cosmos was sent

The query above is answered entirely by the service, as one statement routed to one partition:

```sql
SELECT VALUE { "id": c.id, "name": (IS_PRIMITIVE(c.name) ? c.name : null) }
FROM products c
WHERE (c.category = @p0)
ORDER BY c.id ASC
OFFSET 0 LIMIT 10
```

Three things to notice:

- The projection is a Cosmos object constructor, `SELECT VALUE { … }`.
- `JSON_VALUE` became a guarded path. SQL's `JSON_VALUE` answers null for an object or an array, and
  the guard makes the service answer the same way.
- The `FETCH` became `OFFSET 0 LIMIT 10`, and the adapter asks the service for pages of ten, so it is
  not charged for rows it would discard.

## 3.4 Seeing the plan and the statement

**The plan.** Prefix any query with `EXPLAIN PLAN FOR` and read the result as text:

```csharp
command.CommandText = "EXPLAIN PLAN FOR " + sql;
await using var plan = await command.ExecuteReaderAsync();
while (await plan.ReadAsync())
    Console.WriteLine(plan.GetString(0));
```

Nodes named `Cosmos…` — `CosmosFilter`, `CosmosSort`, `CosmosProject` — are inside the statement.
Everything above `CosmosToClrCursorConverter` runs in process. Chapter 20 shows how to read a plan.

**The statement.** The adapter publishes an `ActivitySource` named `Apache.Calcite.Cosmos.Adapter`.
Each statement is an activity named `cosmos.query`, tagged `db.query.text` with the Cosmos SQL it
sent. A minimal listener:

```csharp
using System.Diagnostics;

using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == "Apache.Calcite.Cosmos.Adapter",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => Console.WriteLine(activity.GetTagItem("db.query.text")),
};

ActivitySource.AddActivityListener(listener);
```

In a real application, collect the same source with OpenTelemetry (Chapter 20) and the request
charge comes with it.

## 3.5 Where to go next

- To expose more containers, a whole account, or views: [Chapter 4](04-models.md).
- To authenticate differently: [Chapter 5](05-authentication.md).
- Before putting this in a service that opens many short-lived connections:
  [Chapter 6](06-connections.md).
- To understand which queries the service answers: Chapters [7](07-row-model.md) through
  [9](09-sorting-and-paging.md).

---

[← Previous: Installation](02-installation.md) · [Contents](README.md) · [Next: Registering Cosmos in a model →](04-models.md)
