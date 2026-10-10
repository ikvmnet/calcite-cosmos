# Appendix A. Operand reference

Every key the adapter reads from a Cosmos schema's `operand` object. The constants are on
`CosmosSchemaFactory` (`EndpointOperand`, `KeyOperand`, …) for code that builds an operand map.

## Account and authentication

| operand | type | default | meaning | chapter |
| --- | --- | --- | --- | --- |
| `endpoint` | string | — | The account endpoint, `https://<account>.documents.azure.com:443/`. Required unless `clientFactory` is given. | 5 |
| `key` | string | — | An account key. Omit to authenticate with Microsoft Entra ID. | 5 |
| `tenantId` | string | — | Tenant for Entra ID authentication. | 5 |
| `clientId` | string | — | Client id of a user-assigned managed identity. | 5 |
| `connectionMode` | string | SDK default (direct) | `"gateway"` routes requests through the gateway; any other value leaves direct mode. | 5 |
| `clientFactory` | string | — | Type name of an `ICosmosClientFactory`. When given, `endpoint`, `key`, `tenantId`, `clientId` and `connectionMode` are not read. | 5 |

## What is exposed

| operand | type | default | meaning | chapter |
| --- | --- | --- | --- | --- |
| `database` | string | — | The database to expose. Omit to expose the whole account, one sub-schema per database. | 4 |
| `containers` | array | all containers | Containers to expose. Each entry is a name, or an object (below). May also be a comma-separated string of names. | 4 |

### Container entry object

| key | type | meaning | chapter |
| --- | --- | --- | --- |
| `name` | string | The container's name. Required. | 4 |
| `schema` | object | A JSON Schema describing the container's documents. | 14 |
| `constraints` | array of strings | `UNIQUE (…) [WHERE …]` constraints over the container's documents. | 16 |

## Behaviour

| operand | type | default | meaning | chapter |
| --- | --- | --- | --- | --- |
| `lookupCacheMaxRows` | positive integer | off | Rows the lookup join's cross-execution cache may hold. Requires `lookupCacheExpireSeconds`. | 12 |
| `lookupCacheExpireSeconds` | positive integer | off | Seconds a cached lookup answer may be believed. Requires `lookupCacheMaxRows`. | 12 |
| `statisticsExpireSeconds` | positive integer | 300 | Seconds a container's row count is remembered. | 6 |
| `indexMetrics` | boolean | `false` | Ask the service which indexes each statement used; reported on the `cosmos.query` span. | 20 |

Numbers and booleans may be written as JSON values or as strings (`"300"`, `"true"`); both are read.

## Errors

| message | cause |
| --- | --- |
| `Operand 'endpoint' is required unless 'clientFactory' is given.` | neither given |
| `Operand 'clientFactory' names the type '…', which could not be found.` | the type name does not resolve — use an assembly-qualified name |
| `'…' does not implement ICosmosClientFactory.` | wrong type |
| `'…' could not be constructed; it needs a public parameterless constructor.` | no usable constructor |
| `'…' returned no client.` | the factory returned null |
| `Operands 'lookupCacheMaxRows' and 'lookupCacheExpireSeconds' come together…` | one given without the other |
| `Operand '…' must be a positive integer; '…' is not.` | a non-numeric or non-positive value |
| `Every object in 'containers' must carry a 'name'.` | a container entry object without `name` |
| `Operand 'schema' on container '…' must be a JSON Schema object.` | `schema` is not an object |
| `Operand 'constraints' on container '…' must be a list of …` | `constraints` is not an array of strings |
| `Operand 'constraints' on container '…': …` | a constraint failed to parse or compile |

## A complete example

```json
{
  "name": "COSMOS",
  "type": "custom",
  "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
  "operand": {
    "endpoint": "https://myaccount.documents.azure.com:443/",
    "tenantId": "00000000-0000-0000-0000-000000000000",
    "database": "inventory",
    "containers": [
      "products",
      "orders",
      {
        "name": "shipments",
        "schema": {
          "properties": {
            "trackingId": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" }
          }
        },
        "constraints": [ "UNIQUE (JSON_VALUE(DOC, '$.reference'))" ]
      }
    ],
    "lookupCacheMaxRows": 10000,
    "lookupCacheExpireSeconds": 300,
    "statisticsExpireSeconds": 600,
    "indexMetrics": false
  }
}
```

---

[← Previous: Limitations and known issues](23-limitations.md) · [Contents](README.md) · [Appendix B: Function and operator reference →](appendix-b-functions.md)
