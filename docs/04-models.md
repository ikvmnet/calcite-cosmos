# 4. Registering Cosmos in a model

Calcite learns what it can query from a **model**: a JSON document listing schemas, each built by a
factory or declared inline. The adapter contributes one factory, `CosmosSchemaFactory`, which turns a
Cosmos database — or a whole account — into a Calcite schema whose tables are containers.

## 4.1 A minimal model

```json
{
  "version": "1.0",
  "defaultSchema": "COSMOS",
  "schemas": [
    {
      "name": "COSMOS",
      "type": "custom",
      "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
      "operand": {
        "endpoint": "https://myaccount.documents.azure.com:443/",
        "key": "…",
        "database": "inventory",
        "containers": [ "products", "orders" ]
      }
    }
  ]
}
```

| key | meaning |
| --- | --- |
| `name` | The Calcite schema's name. Queries qualify tables with it: `"COSMOS"."products"`. |
| `type` | Always `custom` for a factory-built schema. |
| `factory` | The schema factory, **assembly-qualified** (Chapter 2). |
| `operand` | The adapter's settings. Every operand is listed in [Appendix A](appendix-a-operands.md). |
| `defaultSchema` | Where unqualified names resolve. Naming the Cosmos schema here is also what lets queries call the adapter's own functions unqualified (Chapter 17). |

Give the model to a connection either inline or as a file:

```csharp
new CalciteConnectionStringBuilder { Model = "inline:" + json };
new CalciteConnectionStringBuilder { Model = "config/model.json" };
```

## 4.2 One database, or a whole account

**Naming a `database`** exposes that database. The schema's tables are its containers, and a query
names one as `"products"` (or `"COSMOS"."products"`).

**Omitting `database`** exposes the account. Cosmos nests account → database → container and Calcite
nests schema → sub-schema → table, so the schema then carries one sub-schema per database, and a
query names a container with both parts:

```sql
SELECT c."id" FROM "COSMOS"."inventory"."products" AS c
```

With `defaultSchema` set to `COSMOS`, `"inventory"."products"` is enough.

An account-level schema uses **one client for every database**, and a single query can join
containers in different databases. Several database-level schemas in one model each build their own
client. Prefer the account form when you need more than one database from the same account; prefer
the database form when you need one and want nothing else visible.

Be aware that an account-level schema reads the definition of every container in every database when
it is built. For an account with many databases, that is a read per container even if a query uses
one.

## 4.3 Choosing containers

**Omit `containers`** and every container in the database is exposed, discovered when the schema is
built.

**List them** and only those are exposed:

```json
"containers": [ "products", "orders" ]
```

An entry may also be an **object** carrying the container's name and declarations about its
documents — a JSON Schema (Chapter 14) and constraints (Chapter 16):

```json
"containers": [
  "orders",
  {
    "name": "shipments",
    "schema": { "type": "object", "properties": { "carrier": { "type": "string" } } },
    "constraints": [ "UNIQUE (JSON_VALUE(DOC, '$.reference'))" ]
  }
]
```

The two forms mix freely. Two different `name` keys are in play in a model like this and they are
unrelated: the schema's `name` is the Calcite schema; the `name` inside a container entry is the
Cosmos container.

> **Listing any container turns discovery off.** `containers` is all-or-nothing. Once you describe
> one container with an object entry, every other container you want exposed must be listed too —
> which is why names and objects can share the list.

With an account-level schema, `containers` applies to **every** database, which is only useful where
databases share container names. Usually you omit it there.

## 4.4 Table names

Each container becomes a table with **exactly the container's name**, case preserved. On a
case-sensitive connection, quote it as written: `"products"`, `"OrderLines"`. On a case-insensitive
connection Calcite matches names without regard to case, which is convenient until two containers
differ only in case.

Chapter 7 describes the columns each table has.

## 4.5 Views over containers

A view is the usual way to give a container a relational shape — named, typed columns for an ORM or
a report — and it is ordinary Calcite. Putting views in a schema of their own, as here, keeps the
Cosmos schema exactly what the account holds and gives the application one place to look:

```json
{
  "version": "1.0",
  "defaultSchema": "CATALOGUE",
  "schemas": [
    {
      "name": "COSMOS",
      "type": "custom",
      "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
      "operand": { "endpoint": "…", "database": "inventory", "containers": [ "products" ] }
    },
    {
      "name": "CATALOGUE",
      "tables": [
        {
          "name": "PRODUCTS",
          "type": "view",
          "path": [ "COSMOS" ],
          "sql": [
            "SELECT p.\"id\" AS \"Id\",",
            "       JSON_VALUE(p.\"DOC\", '$.name') AS \"Name\",",
            "       JSON_VALUE(p.\"DOC\", '$.price' RETURNING INTEGER) AS \"Price\"",
            "FROM \"products\" AS p"
          ]
        }
      ]
    }
  ]
}
```

Two things specific to views over Cosmos:

- **`path`** sets where the view's own unqualified names resolve. With `"path": [ "COSMOS" ]` the
  view can name `"products"` and the adapter's functions (`FULLTEXTCONTAINS`, `IS_DEFINED`, …)
  without qualifying them. Without it, qualify: `"COSMOS"."products"`.
- **A view is analysed without the connection's function libraries.** A function that needs `Fun`
  set on the connection — `PARSE_DATETIME`, for example — does not validate inside a model view.
  Chapter 15 gives the standard-SQL spelling to use there instead.

How a view's columns are typed, and which spellings push down, is covered in Chapters 7 and 8.

## 4.6 Cosmos beside other adapters

A model can hold several schemas built by different adapters, and a view can join across them. The
repository's sample does exactly this — a CSV directory of suppliers beside a Cosmos container of
products, joined in a view:

```json
{
  "version": "1.0",
  "defaultSchema": "SALES",
  "schemas": [
    {
      "name": "SUPPLIERS",
      "type": "custom",
      "factory": "org.apache.calcite.adapter.csv.CsvSchemaFactory, calcite.csv",
      "operand": { "directory": "data/suppliers" }
    },
    {
      "name": "COSMOS",
      "type": "custom",
      "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
      "operand": { "endpoint": "…", "key": "…", "database": "inventory", "containers": [ "products" ] }
    },
    {
      "name": "SALES",
      "tables": [
        {
          "name": "PRODUCT_SUPPLIERS",
          "type": "view",
          "sql": [
            "SELECT p.\"id\" AS \"PRODUCT\", JSON_VALUE(p.\"DOC\", '$.name') AS \"NAME\",",
            "       s.\"SUPPLIER\", s.\"LEAD_DAYS\"",
            "FROM \"COSMOS\".\"products\" AS p",
            "JOIN \"SUPPLIERS\".\"SUPPLIERS\" AS s ON p.\"id\" = s.\"PRODUCT\""
          ]
        }
      ]
    }
  ]
}
```

The join runs in process, but the adapter does not read the whole container to perform it: the CSV
side's product ids are sent to Cosmos with the statement, so only matching documents come back.
Chapter 12 explains the lookup join and when it applies.

## 4.7 When the model is read

A model is read, and its schemas built, when a connection first needs it. Building a Cosmos schema
creates a `CosmosClient` and reads each exposed container's definition; it does not read documents.
Errors in the operand — a missing `endpoint`, half a lookup-cache configuration, a constraint that
does not compile — are reported then, as an exception from `OpenAsync`, usually wrapped as an error
instantiating the schema with the useful message one or two causes down.

How often a model is read, and how to share one schema across many connections, is the subject of
Chapter 6.

---

[← Previous: Quick start](03-quick-start.md) · [Contents](README.md) · [Next: Connecting and authenticating →](05-authentication.md)
