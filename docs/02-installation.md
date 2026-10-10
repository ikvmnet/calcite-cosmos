# 2. Installation

## 2.1 Requirements

- **.NET 8 or later.** The adapter targets `net8.0` and runs on later runtimes.
- **An Azure Cosmos DB for NoSQL account**, or the Cosmos DB emulator for development
  ([Appendix E](appendix-e-development.md)).
- **A data-plane identity for that account** — an account key, or a Microsoft Entra ID identity with
  a Cosmos DB data-plane role assignment (Chapter 5).

There is no Java to install. Calcite and its dependencies are compiled to .NET assemblies by IKVM and
arrive as ordinary NuGet dependencies.

## 2.2 Packages

Add the adapter and the ADO.NET provider:

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
dotnet add package Apache.Calcite.Data
```

`Apache.Calcite.Data` is what your code talks to: `CalciteConnection`, `CalciteCommand`,
`CalciteDataReader`. The adapter brings in the Cosmos SDK, `Azure.Identity`, the IKVM runtime, Calcite
itself, and the `Apache.Calcite.Extensions`, `Apache.Calcite.Geography` and `Apache.Calcite.FullText`
packages it builds on.

Keep `Apache.Calcite.Data` on the same `Apache.Calcite` version the adapter was built against (this
manual describes 2.0.1-pre.267). The packages share one Calcite, and mixing versions mixes two.

## 2.3 Name the factory assembly-qualified

A Calcite model names the class that builds each schema. **For this adapter that name must be
assembly-qualified:**

```json
"factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter"
```

`Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory` on its own does **not** resolve. The name is
looked up through IKVM, where a bare namespace-qualified .NET type name finds nothing, and the
failure reads as a `ClassNotFoundException` for a type your project plainly references. The same rule
applies to any other .NET schema factory, and to Java ones packaged in their own assembly — Calcite's
CSV adapter is `org.apache.calcite.adapter.csv.CsvSchemaFactory, calcite.csv`.

## 2.4 Make sure the assembly is loaded

The factory is resolved by name when the model is read, and only an assembly that is already loaded
can be found. If nothing in your program mentions the adapter except the string in the model, the
assembly may not be loaded yet. Touch it once at start-up:

```csharp
_ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();
```

A discarded `typeof(…)` is not enough — the compiler may remove it. Constructing an instance has a
runtime effect and cannot be elided. Do the same for any other adapter your model names by string.

A program that uses an adapter type anywhere else — an `ICosmosClientFactory` implementation, a
`CosmosInstrumentation` listener, a call to `RefreshStatistics` — already loads it.

## 2.5 What gets created at run time

Nothing is started in the background when the package is referenced. The first connection that reads
a model containing a Cosmos schema:

1. creates a `CosmosClient` for the account (Chapter 5);
2. reads the definition of each container to be exposed — its partition key, indexing policy, full
   text and vector declarations, unique key policy and geospatial configuration. **Documents are not
   read.**

Row counts and the account's capabilities are read lazily, the first time a plan needs them.
Chapter 6 explains how long all of this is kept and how to share it across connections.

## 2.6 Building from source

The repository builds with the .NET SDK named in `global.json`:

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

[Appendix E](appendix-e-development.md) covers running the sample program and the test suite.

---

[← Previous: Introduction](01-introduction.md) · [Contents](README.md) · [Next: Quick start →](03-quick-start.md)
