# 5. Connecting and authenticating

The adapter reaches Cosmos through a `CosmosClient` from the Cosmos DB .NET SDK. There are three ways
to say how that client is built, chosen by which operands the model gives:

| operands given | client built with |
| --- | --- |
| `endpoint` and `key` | the account key |
| `endpoint` alone | Microsoft Entra ID, as whoever the process is |
| `clientFactory` | your own code, which returns the client |

## 5.1 Account keys

```json
"operand": {
  "endpoint": "https://myaccount.documents.azure.com:443/",
  "key": "<primary or secondary key>",
  "database": "inventory"
}
```

Simple, and the only option for some environments — but an account key is a bearer secret with full
access to the account and no expiry, and it has to live somewhere a model can read it. If you use
one, do not commit it in a model file: build the model string at start-up from configuration or a
secret store, and pass it as `Model = "inline:" + json`.

## 5.2 Microsoft Entra ID

**Give the endpoint and no key**, and the adapter authenticates as the identity the process runs
under:

```json
"operand": {
  "endpoint": "https://myaccount.documents.azure.com:443/",
  "database": "inventory"
}
```

The absence of a key is the request. The adapter uses `DefaultAzureCredential`, which tries the ways
an application is normally identified in turn — environment variables, workload identity, managed
identity, then a developer's signed-in tools (Visual Studio, the Azure CLI, Azure PowerShell, the
Azure Developer CLI). So the same model works on a laptop and in Azure without saying which is which.

Two operands narrow the choice where the ambient identity is ambiguous:

| operand | sets | use when |
| --- | --- | --- |
| `tenantId` | `DefaultAzureCredentialOptions.TenantId` | your signed-in account belongs to several tenants |
| `clientId` | `DefaultAzureCredentialOptions.ManagedIdentityClientId` | more than one user-assigned managed identity is attached |

### The identity needs a data-plane role

This is the usual stumbling block. Cosmos DB has two separate permission systems:

- **Control plane** — Azure RBAC roles such as *Reader*, *Contributor* or *DocumentDB Account
  Contributor*. These let an identity see and manage the account in the portal. **They do not let it
  read a single document.**
- **Data plane** — Cosmos DB's own SQL role assignments, made with `az cosmosdb sql role assignment
  create` (or the equivalent in Bicep or the portal). This is what the adapter needs.

The built-in roles are:

| role | allows |
| --- | --- |
| Cosmos DB Built-in Data Reader | queries and point reads, **and the metadata reads the adapter performs** on start-up |
| Cosmos DB Built-in Data Contributor | all of the above, plus creating, replacing and deleting items |

The adapter reads every exposed container's definition when the schema is built, and lists the
containers (or databases) when the model does not name them. A hand-built custom role that grants item
reads but not `readMetadata` fails there, before any query runs. The built-in Data Reader includes
it; start from that.

Use Data Contributor, or a custom role that adds item writes, if you will run `INSERT`, `UPDATE` or
`DELETE` (Chapter 13).

## 5.3 A client factory

For anything the operands cannot say — a certificate credential, a token cache of your own, a custom
serializer or retry policy, preferred regions, or a client your application already owns — name an
`ICosmosClientFactory`:

```json
"operand": {
  "clientFactory": "MyApp.Data.CosmosClients, MyApp",
  "database": "inventory"
}
```

```csharp
using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;

using Microsoft.Azure.Cosmos;

namespace MyApp.Data;

public sealed class CosmosClients : ICosmosClientFactory
{
    // One client for the process. The adapter never disposes the client it is given (see 5.5), so
    // handing back a shared instance is the right thing to do.
    static CosmosClient? shared;

    public static void Use(CosmosClient client) => shared = client;

    public CosmosClient Create(IReadOnlyDictionary<string, object?> operand) =>
        shared ?? throw new InvalidOperationException("Call CosmosClients.Use at start-up.");
}
```

The rules:

- **The name is resolved like any type name in a model**: assembly-qualified, or namespace-qualified
  and found among the loaded assemblies. Assembly-qualified is the safe choice (Chapter 2).
- **The type needs a public parameterless constructor.** The adapter constructs it itself.
- **It is given the whole operand map**, so anything else your factory needs can be written beside
  `clientFactory` in the model. Strings arrive as .NET strings; other JSON values arrive as the
  objects Calcite's model reader produced, so call `ToString()` on anything that is not a string.
- **A factory replaces `endpoint`, `key`, `tenantId`, `clientId` and `connectionMode`.** They are not
  read when `clientFactory` is given — the factory decides everything about the client.

The three failures are reported separately, because they are three different mistakes: a name that
resolves to nothing, a type that does not implement `ICosmosClientFactory`, and a type that cannot be
constructed.

## 5.4 Connection mode

```json
"connectionMode": "gateway"
```

The SDK's default is **direct** mode — TCP connections to the replicas — which is faster and what a
deployed application normally wants. Set `"gateway"` to route every request through the gateway over
HTTPS instead. Use gateway mode for the emulator, and behind a firewall that allows only port 443.
Any value other than `gateway` leaves the SDK default.

## 5.5 The emulator

The Cosmos DB emulator accepts a well-known key, published by Microsoft and not a secret:

```json
"operand": {
  "endpoint": "http://localhost:8081/",
  "key": "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
  "database": "inventory",
  "connectionMode": "gateway"
}
```

If your emulator serves HTTPS with its self-signed certificate, the operands cannot turn certificate
validation off — use a client factory that builds a `CosmosClient` with
`ServerCertificateCustomValidationCallback` and `LimitToEndpoint = true`, as the repository's sample
does. [Appendix E](appendix-e-development.md) starts an emulator and lists the ways it differs from
the real service.

## 5.6 How long the client lives

The client belongs to the schema that created it and is kept for as long as the schema is. **The
adapter never disposes it**: Calcite's schema interface offers no hook to release resources on. This
matters because, depending on how connections are opened, a schema can be built more than once in
the life of a process — Chapter 6 explains when. Each build through `endpoint` creates a new client.

Two ways to keep that to one client:

- Share the schema, so it is built once (Chapter 6, *Owning the data source*).
- Use a `clientFactory` that returns one shared `CosmosClient`, as in the example above.

Either way, a `CosmosClient` is designed to be a long-lived singleton; creating many is what the SDK's
own guidance warns against.

---

[← Previous: Registering Cosmos in a model](04-models.md) · [Contents](README.md) · [Next: Connections, data sources and schema lifetime →](06-connections.md)
