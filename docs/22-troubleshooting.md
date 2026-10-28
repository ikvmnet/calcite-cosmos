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
promoted partition key column; that a `JSON_VALUE` key is over a path the schema declares the type it
reads — a string without `RETURNING`, the `RETURNING` type with it — since anything else sorts in
process; and that a multi-key sort has a matching composite index. Chapter 9's checklist.

**A sort on the partition key column runs in process.**
That column is `VARIANT` and cannot be sorted. Sort on `JSON_VALUE(c."DOC", '$.<path>')`, and declare
the path a string in the container's schema for that sort to reach the service. Chapters 7 and 9.

**A filter on a `JSON_VALUE` column sends only `IS_DEFINED(…)`, or a longer form, and a recheck.**
That is what the adapter can prove of a text comparison, a `LIKE` or a cast to a number (Chapter 8). If
the property is always a string, declare `"type": "string"` for it (Chapter 14) and the comparison is sent
as written; if it is always a number, read it with `RETURNING` rather than a cast.

**A filter that pushed before reads the whole container.**
Watch `cosmos.rendering.declines` (Chapter 20). Its `unavailable` reason on a schema's first plans means
the plan arrived before the schema's reasoning was built and waited five seconds for it — reuse schemas
rather than building one per request (Chapter 6); `incomplete` means the filter's question is too large,
which fewer conditions or a shorter `IN` list avoids. A decline keeps the rows right and costs request
units. On the emulator, a text ordering over a string that may hold U+0000 stays in process by design
(Appendix E.2).

**`UPPER`, `LOWER`, `CHAR_LENGTH`, `SUBSTRING`, `TRIM`, `REPLACE` or `LIKE '%a%'` over a declared string runs in process.**
Calcite's string functions are Java's and the service's are its own, and they part over some characters:
a case mapping outside ASCII (`ß` is `SS` to Calcite), a count over a character beyond U+FFFF (an emoji is
two to Calcite), a trim over white space other than a space (a tab, U+00A0), and a search past a U+0000
(Azure's `REPLACE` and `CONTAINS`, which answers a `LIKE '%a%'`, stop at the first). Each pushes only
where the container proves the string holds none of those — a `pattern` such as `^[ -~]*$`, an `enum`, a
stored form, or the statement's own `IN (…)`. In a projection the property alone is still read at the
service, and only the function runs in process; in a filter the comparison runs in process, over the
documents the rest of the filter keeps, which are read whole. `REPLACE` and `REPEAT` ask something of
their other operands as well, a search that is not empty and a count the service repeats as far as
Calcite does. Chapter 14.4 and Appendix B.2 say which pattern lets which function push.

**A comparison with a UUID, a timestamp or a number stored as text reads the whole container.**
The container does not declare the stored form, or the query reads it with a spelling that cannot push.
Declare a `pattern` (Chapter 14); for instants, read with `PARSE_DATETIME` or `CAST … FORMAT`, not
`CAST` or `RETURNING TIMESTAMP` (Chapter 15). A pattern that one reading of ECMA-262 rejects is no form:
a UUID pattern with bare braces, `^{…}$`, or with `\-` for a hyphen outside a class, is a literal without
the `u` flag and an error with it, and earlier versions read both as forms. Write `\{…\}`, and `-` or
`[\-]` (Chapter 14.3). A pattern with a space in it is no form either, unless the stored strings hold that
space.

**A query with a parameterised partition key fans out to every partition.**
Routing is decided from literals while planning; a parameter does not route. If the partition key is
fixed per tenant or per request, consider writing it as a literal (it is still sent as a bound
parameter) — at the cost of a plan per value. Chapter 8.

**`SUM`/`AVG`/`MIN`/`MAX` over a property read every document.**
Value aggregates push only over non-nullable inputs, and a document property is nullable. Chapter 10.

**A join reads both containers whole.**
The lookup join did not apply: check it is an inner or left join on one equality with the container on
the right, the keys are of a bindable type (not the `VARIANT` partition key column; a `UUID` only where
the container's schema gives the path a UUID format), and there is no limit or grouping on the container
side.
Chapter 12.

**Plans on the emulator are worse than on Azure.**
The emulator reports zero documents for every container, so plans are made without row counts. Test
plan choices against a real account. Appendix E.

**Many short-lived connections are slow to open.**
Each new data source reads every container's definition, creates a client, and plans its first
statements from nothing (Chapter 19). Keep connection strings identical so pooling shares one, or own the
data source. Chapter 6.

## Queries that fail

**`CosmosMaterializationException: Expected a JSON string, got Number` (or similar).**
A document holds something the query's type cannot hold — `RETURNING INTEGER` over a property that is
sometimes `30.7`, `VARCHAR ARRAY` over an array of numbers. Fix the declaration or the data; the
adapter refuses rather than coerces. (A property that is sometimes a string or an object under
`RETURNING INTEGER` reads null instead.) Chapter 7.

**`CosmosMaterializationException: A value read as a UUID is not one`.**
A document contradicts a declared UUID form. The schema is wrong for this container, or a writer wrote
something else. Chapter 14.

**`CosmosMaterializationException: A JSON number of '9.223372036854776E+18' is outside the range of BIGINT`.**
A document holds an integer written outside the 64-bit range — 2^63 or more, or less than -2^63. Azure
stores such a number as a double, and a double that size is no `BIGINT`: Calcite refuses it too, and the
adapter will not round it to the nearest `BIGINT` instead. Read the property with `RETURNING DOUBLE`, or as
text with no `RETURNING`. On the emulator the same document reads `9223372036854775800` rather than
failing: the emulator prints such a number that way, and it is a valid `BIGINT` (Appendix E). Chapter 7.

**`CosmosMaterializationException: Column 'DOC' of document "…" holds text that is not UTF-8 at $.…: the
bytes ED A0 80 in "…"`.**
Some client wrote that document with bytes that are not UTF-8 — a lone surrogate encoded on its own, a
CESU-8 pair, a stray byte. Azure stores such bytes exactly as they were written; the emulator refuses them.
Calcite has no string that compares the way the service compares those bytes, so the adapter refuses the
value rather than guess at one. The message says which column or path holds it, which document (where the
row identifies it), where in the value the bytes are, and the bytes themselves.
- **Rewrite the document with valid UTF-8.** A writer that escapes the character instead (`\ud800` in the
  JSON) has it stored as U+FFFD, which reads.
- **Or leave the value out of what the statement reads.** Only a statement whose rows carry the bytes into
  a reading fails: one that selects other columns, or filters the document away at the service, answers,
  even in the same logical partition.
- **But `SELECT *`, and any statement not pushed to the service in full, read the `DOC` column of every
  document they scan**, so one such document fails all of those over its container, and fails them part
  way through, after earlier rows have been returned. Chapter 7.

**"… is evaluated by the service and has no in-process body."**
A Cosmos-only function (full text, scoring, `REGEXMATCH`, the string conversions) could not be pushed
— most often because its first argument is not a property path. Rewrite it so it can be. Chapter 17.

**"Cosmos never returns a relevance score" / `ORDER BY FULLTEXTSCORE(…)` fails through a connection.**
`ORDER BY RANK` cannot be recovered through a `CalciteConnection` ([#46](https://github.com/ikvmnet/calcite-cosmos/issues/46)).
Use a planner you build (Chapter 21), or filter with `FULLTEXTCONTAINS…` instead of ranking. Whichever
route, an ordering on a score has to say `DESC`: Cosmos ranks only best first, and an ascending one —
the default when no direction is written — is never pushed (Chapter 17.3). The message says both, since
it cannot see which ordering it is under.

**"cannot translate call FULLTEXTSCORE" in a planner you build.**
The ordering on the score is ascending, or has no direction: add `DESC` (Chapter 17.3).

**"The index path corresponding to the specified order-by item is excluded" (400).**
Should not happen through the adapter, which reads the container's indexing policy and sorts an
excluded path, `_etag` and an unindexed computed property in process (Chapter 9.2). If it does, the
policy changed after the schema was built: rebuild it.

**`SC2005 'FullTextScore' is not a recognized built-in function` (or the full text functions fail).**
You are on the emulator, which does not implement full text search. Use a real account.

**"ORDER BY item expression could not be mapped to a document path" (400, 2206).**
Should not happen through the adapter — it never sends an expression sort other than a geodesic
distance. If you see it, report the query.

**"Parameter ?0 holds a lone surrogate, which the Cosmos DB SDK cannot encode as UTF-8, so the statement
cannot be sent" (`CosmosExecutionException`) when a statement runs.**
A parameter was bound to a string holding a lone surrogate — half of a character beyond U+FFFF, such as one
cut from an emoji by a substring in the application — and the statement sends it for the service to read: a
comparison, say. The Cosmos SDK encodes a statement as UTF-8 and cannot encode one, so nothing is sent; the
adapter refuses the value by name before the SDK's encoder throws `EncoderFallbackException`. A literal holding
one, `U&'\D83D'`, is computed in process instead, and so is a column computed from parameters alone,
`UPPER(CAST(? AS VARCHAR))`, which is never sent; a parameter the service reads is not known until it is
bound. Bind a whole character, or compare in the application. Chapter 8.

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
service is the reference. One that shows in rows: the emulator returns an integer written between 2^63
and 2^64 as written in a document read whole, and as a nearby integer when a statement selects it — 2^63 as
`9223372036854775800` — so a query that pushes the selection and one that reads the whole document answer
different numbers. Azure stores such an integer as a double and answers it one way.

**Sorting puts missing values first.**
That is Cosmos's order, and what `DefaultNullCollation = "LOW"` asks for. For nulls last, the sort runs
in process.

---

[← Previous: Embedding the adapter in your own planner](21-custom-planner.md) · [Contents](README.md) · [Next: Limitations and known issues →](23-limitations.md)
