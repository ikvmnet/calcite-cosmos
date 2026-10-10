# Appendix C. Type reference

How values travel from a Cosmos document, through Calcite's SQL types, to .NET.

## C.1 The row type

| column | SQL type | nullable | written by |
| --- | --- | --- | --- |
| `DOC` | `VARCHAR` | no | the statement (`INSERT`, `UPDATE`) |
| `id` | `VARCHAR` | no | inside `DOC` |
| `_ts` | `BIGINT` | no | the service |
| `_etag` | `VARCHAR` | no | the service |
| `"$.<path>"` per partition key path | `VARIANT` | yes | inside `DOC` |

## C.2 Reading a JSON value as a SQL type

When the service returns a value, it is read according to the SQL type the plan declared for it.

| JSON value | read as an untyped value (`ANY`, inside `MAP`/`ARRAY`) | read as a declared type |
| --- | --- | --- |
| string | string | `CHAR`, `VARCHAR`; `UUID` where the container declares the spelling; `DATE`, `TIME`, `TIMESTAMP` through a declared parse |
| whole number | 64-bit integer | `TINYINT`, `SMALLINT`, `INTEGER`, `BIGINT`; `DECIMAL` from the exact digits |
| fractional number | double | `REAL`, `FLOAT`, `DOUBLE`, `DECIMAL` |
| `true` / `false` | boolean | `BOOLEAN` |
| object | map | `MAP`; `GEOMETRY` where a geography operator named it |
| array | list | `ARRAY`, `MULTISET`, each element read as the element type |
| `null`, or absent | null | null |

A whole number is read as an integer rather than a double, so an identifier or a count does not come
back as `42.0`.

**A document that disagrees with the declared type is refused, not coerced.** Reading the number `42`
where the plan declared `VARCHAR` raises `CosmosMaterializationException` rather than producing
`"42"`. A row type that bends to the data would be a suggestion, not a type.

Some SQL types have no reading — the interval types, unsigned integers, and times with a time zone. A
projection of one is never sent to the service; it is computed in process, and a plan that would need
to read one from Cosmos is refused while it is prepared.

## C.3 What .NET receives

`CalciteDataReader` converts Calcite's values to CLR types:

| SQL type | .NET type |
| --- | --- |
| `VARCHAR`, `CHAR` | `string` |
| `BOOLEAN` | `bool` |
| `INTEGER`, `BIGINT` | `int`, `long` |
| `DOUBLE` | `double` |
| `DECIMAL` | `decimal` |
| `UUID` | `Guid` |
| `ARRAY` of a scalar type | an array of the nullable element type, e.g. `string?[]`, `int?[]` |
| SQL `NULL` | `DBNull.Value` |

The mapping is the provider's (`Apache.Calcite.Data`), not the adapter's; consult its documentation
for the temporal and other types not listed here.

## C.4 Type of each accessor

| expression | SQL type |
| --- | --- |
| `JSON_VALUE(doc, path)` | `VARCHAR(2000)`, nullable |
| `JSON_VALUE(doc, path RETURNING t)` | `t`, nullable |
| `JSON_QUERY(doc, path)` | `VARCHAR(2000)`, nullable — compact JSON text |
| `JSON_QUERY(doc, path RETURNING t ARRAY)` | `t ARRAY`, nullable, with nullable elements |
| `IS_DEFINED(x)` and the other type tests | `BOOLEAN NOT NULL` |
| `FULLTEXTCONTAINS…` | `BOOLEAN` |
| `ToString(x)`, `StringToNumber(x)`, … | `ANY` |

`JSON_VALUE`'s declared width is not applied at run time: a longer value is returned whole.

## C.5 Parameters

A parameter is sent to Cosmos as a JSON value of its type: strings as strings, numbers as numbers,
booleans as booleans, a geometry as its GeoJSON object. A lookup join's keys must be strings, numbers or
booleans; a `VARIANT` or `ANY` key cannot be bound (Chapter 12).

A partition key value recovered from a predicate is built from the literal's type — a string, a
boolean, a number, or null.

---

[← Previous: Function and operator reference](appendix-b-functions.md) · [Contents](README.md) · [Appendix D: Cosmos SQL in brief →](appendix-d-cosmos-sql.md)
