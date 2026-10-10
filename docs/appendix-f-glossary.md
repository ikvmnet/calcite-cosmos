# Appendix F. Glossary

**Account schema.** The schema built when the model omits `database`: one sub-schema per database.
Chapter 4.

**Composite index.** An index over several paths in a stated order and direction, declared in a
container's indexing policy. Required by Cosmos for an `ORDER BY` on more than one property.

**Container.** A Cosmos collection of JSON documents, exposed by the adapter as a table.

**Convention.** Calcite's name for where an operator runs. The adapter has one Cosmos convention per
container; rows leave it into `ClrCursorConvention`, where Calcite evaluates in process.

**Data source.** In `Apache.Calcite.Data`, the object that holds a root schema shared by many
connections. Chapter 6.

**Declared fact.** Something a container's JSON Schema says about a path — its type, its stored form,
its presence — which the planner may use. Chapter 14.

**`DOC`.** The column holding the whole document. Chapter 7.

**Gateway / direct mode.** How the SDK reaches the service: through the HTTPS gateway, or directly to
replicas over TCP. Chapter 5.

**In process.** Evaluated by Calcite inside the application, over rows already returned by Cosmos.

**Logical partition.** The documents sharing one partition key value. Cosmos's unit of transactions
and of `id` uniqueness.

**Lookup join.** A join that sends one side's keys to Cosmos in batches, so only matching documents are
read. Chapter 12.

**Model.** The JSON document listing the schemas a Calcite connection can query. Chapter 4.

**Null collation.** Where nulls sort. Cosmos's is `LOW`: first ascending, last descending. Chapter 9.

**Partition key.** The path (or up to three paths) whose value decides which partition a document lives
in. Promoted to a column of its own. Chapter 7.

**Point read.** Reading one document by `id` and partition key, without the query engine — about 1 RU.
Chapter 8.

**Promoted column.** `id`, `_ts`, `_etag` or a partition key column — a value from the document given
a column so the planner can reason about it. Chapter 7.

**Pushed down.** Rendered into the Cosmos SQL statement and evaluated by the service.

**Recheck.** Evaluating a predicate in process over rows the service returned under a weaker version of
it. Chapter 8.

**Request unit (RU).** Cosmos's unit of charge for a request.

**Self-join merge.** Answering a join of a container to itself with one read, where the key names one
document. Chapter 12.

**Stored form.** How a value is spelled in the document — a canonical lowercase UUID, an instant in one
fixed ISO-8601 shape — declared with a `pattern`. Chapter 14.

**Undefined.** Cosmos's term for an absent property, distinct from JSON `null`.

**`VARIANT`.** The SQL type of the promoted partition key columns: a scalar whose concrete type is
learned per row. Chapter 7.

**Weakening.** Sending the service a predicate that the original implies, and rechecking the original
in process. Chapter 8.

---

[← Previous: Development, the emulator and the test suite](appendix-e-development.md) · [Contents](README.md)
