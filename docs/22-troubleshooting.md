# 22. Troubleshooting

Each entry is a symptom, its usual cause, and the fix. Most problems are of one of two kinds: a
connection that fails to open because of the model, or a query that works but reads far more than it
should because part of it did not push down. For the second kind, start with `EXPLAIN PLAN FOR`
(Chapter 20).

## Opening a connection

**`ClassNotFoundException` naming `CosmosSchemaFactory`, or "error instantiating schema".**
The factory is not named assembly-qualified, or the adapter assembly is not loaded yet. Use
`"Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter"` and touch the
assembly at start-up with `_ = new CosmosSchemaFactory();` (Chapter 2). The real message is usually one
or two inner exceptions down — print the whole chain.

**"Operand 'endpoint' is required unless 'clientFactory' is given."**
Neither was supplied. Chapter 5.

**401/403, or "does not have required RBAC permissions", with Entra ID.**
The identity has a control-plane role but no data-plane role assignment, or a custom role without
metadata reads. Assign Cosmos DB Built-in Data Reader (or Contributor) on the account. Chapter 5.

**"Operands 'lookupCacheMaxRows' and 'lookupCacheExpireSeconds' come together."**
Give both or neither. Chapter 12.

**"Operand 'constraints' on container '…': …"**
A constraint did not parse or compile, or is a kind not read yet (`CHECK`). The message names the
container. Chapter 16.

**"Operand 'schema' on container '…' must be a JSON Schema object."**
`schema` must be an inline object, not a string or a path. Chapter 14.

**Every container disappeared except the one I described.**
Listing any container turns discovery off. List every container you want. Chapter 4.

## Queries that are slow or expensive

**Sorted, paged queries read the whole container.**
Almost always null placement. Set `DefaultNullCollation = "LOW"` on the connection, or write
`NULLS FIRST` (ascending) / `DESC NULLS LAST`. Also check the key is a path and not a cast or the
promoted partition key column, and that a multi-key sort has a matching composite index. Chapter 9's
checklist.

**A sort on the partition key column runs in process.**
That column is `VARIANT` and cannot be sorted. Sort on `JSON_VALUE(c."DOC", '$.<path>')`. Chapter 7.

**A filter on a `JSON_VALUE` column shows `NOT IS_STRING(…) OR …` and a recheck.**
That is the safe form of a text comparison (Chapter 8). If the property is always a string, declare
`"type": "string"` for it (Chapter 14) and the comparison becomes exact.

**A comparison with a UUID, a timestamp or a number stored as text reads the whole container.**
The container does not declare the stored form, or the query reads it with a spelling that cannot push.
Declare a `pattern` (Chapter 14); for instants, read with `PARSE_DATETIME` or `CAST … FORMAT`, not
`CAST` or `RETURNING TIMESTAMP` (Chapter 15).

**A query with a parameterised partition key fans out to every partition.**
Routing is decided from literals while planning; a parameter does not route. If the partition key is
fixed per tenant or per request, consider writing it as a literal (it is still sent as a bound
parameter) — at the cost of a plan per value. Chapter 8.

**`SUM`/`AVG`/`MIN`/`MAX` over a property read every document.**
Value aggregates push only over non-nullable inputs, and a document property is nullable. Chapter 10.

**A join reads both containers whole.**
The lookup join did not apply: check it is an inner join on one equality, the keys are of a bindable
type (not the `VARIANT` partition key column), and there is no limit or grouping on the container side.
Chapter 12.

**Plans on the emulator are worse than on Azure.**
The emulator reports zero documents for every container, so plans are made without row counts. Test
plan choices against a real account. Appendix E.

**Many short-lived connections are slow to open.**
Each new data source reads every container's definition and creates a client. Keep connection strings
identical so pooling shares one, or own the data source. Chapter 6.

## Queries that fail

**`CosmosMaterializationException: Expected a JSON string, got Number` (or similar).**
A document holds a different JSON type than the query declared — `RETURNING INTEGER` over a property
that is sometimes a string, `VARCHAR ARRAY` over an array of numbers. Fix the declaration or the data;
the adapter refuses rather than coerces. Chapter 7.

**`CosmosMaterializationException: A value read as a UUID is not one`.**
A document contradicts a declared UUID form. The schema is wrong for this container, or a writer wrote
something else. Chapter 14.

**"… is evaluated by the service and has no in-process body."**
A Cosmos-only function (full text, scoring, `REGEXMATCH`, the string conversions) could not be pushed
— most often because its first argument is not a property path. Rewrite it so it can be. Chapter 17.

**"Cosmos never returns a relevance score" / `ORDER BY FULLTEXTSCORE(…)` fails through a connection.**
`ORDER BY RANK` cannot be recovered through a `CalciteConnection` ([#46](https://github.com/ikvmnet/calcite-cosmos/issues/46)).
Use a planner you build (Chapter 21), or filter with `FULLTEXTCONTAINS…` instead of ranking.

**`SC2005 'FullTextScore' is not a recognized built-in function` (or the full text functions fail).**
You are on the emulator, which does not implement full text search. Use a real account.

**"ORDER BY item expression could not be mapped to a document path" (400, 2206).**
Should not happen through the adapter — it never sends an expression sort other than a geodesic
distance. If you see it, report the query.

**400 on a query with `SQRT`, `ASIN` or `ACOS`.**
Cosmos fails the whole statement for an out-of-domain argument. Guard the input. Chapter 8.

**`409 Conflict` from an `INSERT`.**
A document with that `id` already exists in that partition. Chapter 13.

**An `UPDATE` is refused while planning.**
It sets a column other than `DOC`. Only `SET "DOC" = …` is supported. Chapter 13.

**A `DELETE` of a whole partition answers 400 "Partition key delete feature is disabled".**
Should not reach you — the adapter probes for the capability first and falls back. If it does, report
it. Chapter 13.

**`ClassNotFoundException: org.apache.calcite.jdbc.CalciteJdbc41Factory`.**
Your code called `Frameworks.getPlanner` or `RelBuilder.create`. Publish Calcite's assembly to IKVM's
boot class path first. Chapter 21.

**"Unable to implement" / "could not be implemented" for a plan with a join, in your own planner.**
The calc pass after the planner is missing. Chapter 21.

## Results that look wrong

**A query returns fewer rows than expected, with no error.**
Suspect a declaration first. A JSON Schema that does not describe every document (Chapter 14) or a
`UNIQUE` constraint that is not really unique (Chapter 16) loses rows silently. Remove the declaration
and compare.

**A `JSON_VALUE(…) = '30'` filter also matches documents storing the number 30.**
That is SQL's meaning — `JSON_VALUE` returns text, and the number renders as `'30'`. Use `RETURNING
INTEGER` to compare numbers, or `IS_STRING` to restrict to strings. Chapter 7.

**A timestamp parsed with `yyyy-MM-dd'T'HH:mm:ss` has the wrong month and no minutes.**
Calcite reads `mm` as a month. Use `%M` or `MI`. Chapter 15.

**`JSON_VALUE(… RETURNING VARCHAR ARRAY)` is always null, and `UNNEST` of it gives no rows.**
Use `JSON_QUERY(… RETURNING VARCHAR ARRAY)`. Chapter 11.

**Rows differ between the emulator and Azure.**
The emulator accepts some statements Azure rejects and rejects some Azure runs (Appendix E). The real
service is the reference.

**Sorting puts missing values first.**
That is Cosmos's order, and what `DefaultNullCollation = "LOW"` asks for. For nulls last, the sort runs
in process.

---

[← Previous: Embedding the adapter in your own planner](21-custom-planner.md) · [Contents](README.md) · [Next: Limitations and known issues →](23-limitations.md)
