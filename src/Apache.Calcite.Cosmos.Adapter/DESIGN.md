# Apache.Calcite.Cosmos.Adapter — Design

`Apache.Calcite.Cosmos.Adapter` exposes Azure Cosmos DB containers to Apache Calcite as
relational schemas, and pushes as much of the relational plan as possible down to Cosmos by
generating **Cosmos SQL**. Calcite's planner runs in-process via IKVM.

This document records the shape of the target language, the resulting design decision, and the
structure that follows from it.

> **Status.** Under development. Statement generation, container metadata, the schema and table
> layer, the scan/filter/project/sort/unnest/aggregate/rank nodes with their conversion rules, and
> the converter that hands results to `ClrEnumerableConvention` are in place and tested. Items
> marked ✔ below exist; the rest are specification.
>
> One claim still rests on documentation rather than observation and needs a real Cosmos account
> to settle: that a multi-key `ORDER BY` requires a matching composite index. See *Verified
> against the emulator* and *Unvalidated assumptions*.

---

## Scope

- **Adapter, not a provider.** This package makes Cosmos DB queryable *from* Calcite. It is not
  an entry point for applications to execute SQL; that is the role of an ADO.NET provider.
- **No ADO.NET, no JDBC, no Avatica.** The adapter renders Cosmos SQL text and executes it
  through the Cosmos data-plane SDK. There is no intermediate relational protocol.
- **Cosmos DB for NoSQL only.** The MongoDB, Cassandra, Gremlin, and Table APIs are out of
  scope; they have their own query languages and, in the Cassandra and MongoDB cases, their own
  Calcite adapters upstream.

---

## The service is a toolbox, not a counterpart

A pushdown is usually read as a mapping: this Calcite operator becomes that Cosmos function, and
where there is no counterpart the operator stays in process. Most of this document is written that
way, and for the ordinary cases it is the right way.

It is not a law, and several refusals recorded here are refusals of that *model* rather than of the
operation. Cosmos is a storage engine with a set of operations; what the adapter owes the plan is the
right rows in the right order, by whatever combination of those operations produces them. **A node
may issue more than one statement and combine the results.**

**This already happens once.** `CosmosLookup` renders one statement per batch of build rows and joins
what comes back in process — the batch size fixed so the statement shape is stable, a short batch
padded rather than re-rendered. The model exists; it has simply not been generalised past the join.

What it reopens, each recorded elsewhere in this document or in `TODO.md` as a limit:

| | today | as a toolbox |
| --- | --- | --- |
| A disjunction | weakened, pushed loose, rechecked in process | one statement per branch, concatenated and de-duplicated by `id` — exact rather than approximate |
| A sort whose null placement disagrees | declined entirely | the non-null rows ordered, the null rows in any order, concatenated |
| A sort over strings of one shape | needs a promise about the stored shape | the conforming rows sorted at the service, the complement read separately |
| An item-scoped `EXISTS` | an `Unnest` that cross-products the document with its array | a second query over the distinct keys, which is the semi-join it was |

De-duplication is cheap in all of these because every document has an `id` and it is the one value
guaranteed unique within a partition.

### What bounds it, and what does not

**The obvious objection is that several statements are not one point in time.** It is true and it is
weaker than it sounds, because *one* statement is not one point in time either. Measured: a
paginated `ORDER BY c.id` scan, with a row written behind the cursor and another ahead of it after
the first page, returned **both**. Cosmos does not snapshot a query across its continuations, so a
split scan does not introduce a class of anomaly that a single scan avoids — it widens a window that
was already open.

What is genuinely worse is only this: a row can be *counted twice* by a split where a single scan
would see it once, because the two halves are separate predicates rather than one cursor. A
technique that halves a scan owes an answer about a row that moves between the halves, and
de-duplicating by `id` is that answer wherever the halves can overlap.

**No consistency level fixes it.** Strong, Bounded Staleness, Session, Consistent Prefix and Eventual
govern what a read sees relative to *writes* — replica staleness — not isolation between two
statements. Strong makes the split sharper rather than safer: it guarantees each query sees the
latest committed state at its own time, which is precisely the two states disagreeing.

**A `_ts` pin buys less than it appears to.** `WHERE c._ts <= <captured>` gives *rows unchanged since
then*, not *the state as of then*: measured, a row updated after the pin is absent from the result
rather than present at its prior value. It also has one-second granularity. As a way of making two
halves agree with each other it works — both see the same unchanged set — at the price of dropping
whatever moved.

**The one real snapshot is a single logical partition.** Cosmos's transactional guarantees are scoped
to one partition key: a stored procedure executes there with isolation, and a transactional batch is
atomic there. So a multi-statement plan confined to one logical partition could have a consistent
view, and **the adapter already computes that precondition** —
`CosmosImplementor.PartitionKeyValues` and `PartitionKeyIsComplete` record when a filter has pinned
every declared path. What it would cost is real and unmeasured here: the body is JavaScript, it must
be registered on the container rather than sent with the query, and it runs under an execution
budget.

**And the cheapest option is to be told.** Where the data is not being written — a nightly export, a
read replica, a container that is loaded and then queried — none of this matters, and the caller
knows. An operand saying so is a smaller thing to build than any of the above and covers the case
that most often motivates it.

**How many statements is a number for the cost model, not a bar.** Two, or ten: each is request
units and latency, and nothing about the count makes a plan illegal. Ten bounded reads can beat one
that walks a container, and deciding which is what a cost model is for.

**Which is a precondition rather than a description of what exists.** Every Cosmos node costs
Calcite's own cost times `CosmosConvention.CostMultiplier`, a flat `.8` — rows, no bytes, no request
units, no term for a round trip. Under that model a *k*-way split is *cheaper* than the statement it
replaces, because each branch carries a smaller row count at the same discount and the extra
requests are free. A split built before the cost model can see them would therefore be chosen for
the wrong reason, and the cost model is the part to build first.

`CosmosLookupJoin` is both the exception and the precedent. It already issues one statement per
batch and already charges for them, folding `ceil(buildRows / batchSize)` requests into the CPU term
precisely because a round trip is not a row read. Generalising the toolbox past the join means
generalising that term with it — and past a flat discount to something denominated in request units,
which is what the statistics and cost-model items in `TODO.md` exist to reach.

**A row limit does ride along.** An earlier draft of this section said it could not. That was wrong.
Where each branch is ordered the way the merge is — which is what makes a split a split rather than
an arbitrary set of queries — the first *n* rows of the merged result are drawn from the first *n*
of every branch. So `LIMIT n` goes to all *k* of them and the merge takes *n* of at most *k·n* rows.
The read stays bounded; the *k·n* fetched to return *n* is what it costs.

What does not ride along is the offset. `OFFSET m LIMIT n` becomes `LIMIT m+n` on each branch with
the offset applied after the merge — the bound survives but grows with the offset, which is the
ordinary distributed top-*n* arithmetic and the ordinary reason deep paging is the case to watch.

The genuinely unbounded case is a different one, recorded below under the declared collation trait:
there the service's order is *not* the plan's, so the first *n* it returns are not candidates for the
first *n* wanted, and no per-branch limit is sound. The distinction is whether each branch's order
agrees with the merge — not whether there is more than one statement.

None of this makes the model wrong. It makes it a technique with stated costs, which is what lets a
future refusal be argued on its merits rather than on the assumption that one plan means one
statement.

---

## The Target Language

Cosmos SQL is SQL-*shaped* but is not a relational language. Its surface is closed and small.
The design below follows directly from these properties, so they are recorded explicitly.

### Supported clauses

`SELECT`, `FROM`, `WHERE`, `GROUP BY`, `ORDER BY`, `ORDER BY RANK`, `OFFSET LIMIT`, and
subqueries. That is the complete list.

### Reserved keywords

`BETWEEN`, `DISTINCT`, `LIKE`, `IN`, `TOP`. That is the complete list.

There is no `UNION`, `INTERSECT`, or `EXCEPT`; no `HAVING`; no `CASE`/`WHEN`; no `WITH`/CTE; no
window functions; no `CAST`; and no DML. `SETUNION` is an array function, not a set operator
over rows.

### Four properties that determine the design

**1. `JOIN` has no join predicate.**

```
<from_specification> ::= <from_source> {[ JOIN <from_source>][,...n]}
<from_source>        ::= <container_expression> [[AS] input_alias] | input_alias IN <container_expression>
<container_expression> ::= ROOT | container_name | input_alias
                         | <container_expression> '.' property_name
                         | <container_expression> '[' "property_name" | array_index ']'
```

There is no `ON` production. Cosmos `JOIN` cross-products a document with its own nested arrays
— it is `UNNEST`/`CROSS APPLY` spelled `JOIN`. Relational joins are not expressible, and the
documented workaround for one is to inline a literal array of reference data into a subquery.

**2. There are no derived tables.** A Cosmos query always returns a single column, so only
*multi-value* and *scalar* subqueries exist. Subqueries in `FROM` appear exclusively as
`JOIN x IN (...)` and are **item-scoped** — they iterate an array belonging to the current
document. `FROM (SELECT ... FROM container WHERE ...) AS t` has no equivalent.

**3. The result is a JSON value stream, not a tuple stream.** Multi-column projection is
syntactic sugar for an object constructor:

```
SELECT <e1> AS p1, ..., <eN> AS pN     ≡     SELECT VALUE { p1: <e1>, ..., pN: <eN> }
```

**4. `GROUP BY` and `ORDER BY` cannot appear in the same query.** Additionally, neither
`GROUP BY` nor `DISTINCT` supports continuation tokens, so grouped and distinct results are not
resumable across pages.

### What a container declares

A container has **no row schema**. Two items in the same container may share nothing but `id`.
But a container is not metadata-free — it declares a good deal, and all of it is *planner*
metadata rather than *type* metadata:

| Declared / guaranteed | Source | Planner value |
| --- | --- | --- |
| `id` — required, string, unique within a logical partition | Service guarantee | Key component |
| Partition key path(s) — up to 3, hierarchical | Container definition | Distribution; filter priority |
| `_ts` (epoch seconds), `_etag`, `_rid`, `_self` | Service-generated on every item | Typed columns; `_ts` is a real timestamp |
| Included / excluded index paths | Indexing policy | Whether a predicate is cheap or a scan |
| Composite indexes (ordered, with direction) | Indexing policy | **Whether `ORDER BY` is legal at all** |
| Unique key policy | Container definition | Unique keys |
| Computed properties | Container definition | Named, queryable, declared paths |
| Full text policy and full text indexes | Container definition, indexing policy | **Whether a full text function pushes at all** |
| Vector embedding policy and vector indexes | Container definition, indexing policy | **Whether `VECTORDISTANCE` pushes at all** |
| Tuple indexes | Indexing policy | Nothing yet |
| `geospatialConfig` | Container definition | **Whether an `ST_GEOG_*` pushes at all** |
| Spatial indexes | Indexing policy | Nothing — a geodesic call pushes whether or not one is declared |

Three of these carry hard consequences:

- `id` and `_ts` are **always** indexed when the indexing mode is `Consistent`; `_etag` is
  excluded by default; the partition key is *not* indexed unless it is `/id`.
- *"Queries that have an `ORDER BY` clause with two or more properties require a composite
  index."* The index paths must match the `ORDER BY` sequence, and the directions must match
  exactly or be exactly inverted. A multi-property sort without a matching composite index is
  not a slow query — it is an invalid one.

- A `VECTORDISTANCE` over two paths the container declares nothing about is refused by the service
  rather than answered slowly. A full text function over such a path is **answered** — measured
  against three accounts, #85 — and is a cost rather than a legality. See *The declaration prices a
  full text function, and still gates a vector one*.

The middle point is the important one: **whether a `Sort` is pushable is a function of container
metadata, not of the plan.** `CosmosSortRule` must read the indexing policy. The last is the same
shape one level down for the vector function — the operator is legal, the *path* is what decides —
and for full text the path decides the price instead.

### Verified against the emulator

The following were established empirically against the Cosmos DB emulator
(`mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`) rather than taken from
documentation.

> **The emulator is not the service, and the difference has bitten twice.** It accepts statements
> Azure rejects, and rejects features Azure implements. Point `COSMOS_TEST_ENDPOINT` and
> `COSMOS_TEST_KEY` at a real account to run the same suite against one.
>
> | | emulator | Azure |
> | --- | --- | --- |
> | `ORDER BY t0` over `JOIN t0 IN c.tags` | accepted | **400** |
> | `FULLTEXTCONTAINS` and `ORDER BY RANK` | **400** | accepted |
> | multi-key `ORDER BY`, no composite index | accepted | **400** |
>
> The first is why `CosmosSort` refuses any sort key rooted at an unnest alias: a single-key
> allowance stood for a long time on the emulator's word, and emitted a statement Azure will not
> run. `ORDER BY t0.x` is rejected too, so it is the alias and not the arity.
>
> The second understates itself. The emulator does not merely reject the statements: it accepts a
> container declaring a `FullTextPolicy` and a full text index and then reports both back as absent,
> and it does not know the names either — `SC2005, 'FullTextScore' is not a recognized built-in
> function name`. The declaration used to decline the statement before it was sent; since #85 it is
> a cost rather than a gate, so the statement is sent and the `SC2005` that comes back is the
> emulator's own answer, worth telling apart from the service's when a test reports one.

**A hundred-term `IN` is served by the index.** Measured on a real account with
`PopulateIndexMetrics`: `WHERE c.category IN (@k0, ..., @k99)` reports
`{"UtilizedIndexes":{"SingleIndexes":[{"IndexSpec":"/category/?"}]},"PotentialIndexes":{"SingleIndexes":[]}}`
— the index on the restricted path is used and nothing is left unused. This is the form the lookup
join emits, at the batch size it emits, and the measurement is what says the feature is an
improvement rather than a scan with a large predicate bolted on.

Also measured: the index metrics header carries **JSON**, not the prose the documentation shows. The
prose is what the portal renders.

**A multi-key `ORDER BY` really does need a composite index spanning its keys.** Measured on a real
account against a container built with the default indexing policy and so with no composite index at
all: `ORDER BY c.category, c.price` is rejected with **400**, while `ORDER BY c.price` over the same
container is served. So `CosmosContainerMetadata.IsSortSupported` is refusing exactly what the
service refuses, and the guard costs no pushdown that was ever available.

This is the third case where the emulator's answer was the wrong one, and it is why it could not be
settled until now: the emulator discards composite indexes on create, reporting none on the create
response and none on a subsequent read, and then accepts the multi-key form regardless.

**The document count lags, and by more than a moment.** A container read reports `documentsCount` in
its `x-ms-resource-usage` header, and immediately after writing four documents it reports **zero** —
the statistic is computed in the background. That is what the row count in `getStatistic` is, and it
is what a planner row count is allowed to be: approximate and stale. It is not what the rule against
inferring from documents forbids, which is about the *shape* of the data — a wrong shape yields an
incorrect plan, a wrong row count yields a slow one.

The partition count is answered immediately, being a fact about the container rather than its
contents.

**Out-of-domain arithmetic fails the whole query.** `ASIN(2)`, `ACOS(2)`, `SQRT(-1)` and `LOG(0)`
each return a 400 rather than yielding undefined for the offending row. Calcite evaluates all four
as NaN, so pushing any of them down trades a row of NaN for a failed statement — over data no schema
lets the adapter check first. They are pushed anyway, `SQRT` and `LOG` always having been, and this
is recorded so that the consistency is a decision rather than an oversight.

**Ordering is a total order over JSON types.** Ascending:

```
undefined  <  null  <  boolean  <  number  <  string  <  array  <  object
```

`DESC` returns the exact reverse, including the placement of `undefined` and `null`. There is no
separate null-placement control.

This has a sharp consequence. Cosmos sorts nulls **first ascending and last descending**;
Calcite's `RelFieldCollation` defaults are the opposite on **both** counts — ascending defaults
to `NullDirection.LAST`, descending to `FIRST`. A sort on a nullable key therefore cannot
normally be pushed down, because doing so would return rows in an order the plan did not ask
for. `CosmosSort` refuses unless the placement matches or the key is non-nullable.

In practice this is what keeps sorting on `id` and the system properties available while
declining sorts on arbitrary document paths.

**A query that removes the nulls settles the placement itself.** The rule refuses a nullable key
because the two sides disagree about where nulls go; a predicate that leaves none in the rows being
sorted leaves nothing to disagree about, whichever way each side would have placed one. So under
Calcite's default placement — `NullCollation.HIGH`, ascending meaning nulls last —
`WHERE c.category IS NOT NULL ORDER BY c.category` pushes in both directions where
`ORDER BY c.category` alone is refused.

That default is a connection setting rather than a fact, and the other value worth knowing is
`LOW` — nulls first ascending, last descending, which is Cosmos's own order and SQL Server's. A
connection set that way asks for what the service already does, so the placement never conflicts and
no predicate is needed. It is a property of the connection and not of the schema, so it changes what
`ORDER BY` means for every schema on it, which is why it is guidance rather than a default the
adapter could set — see the README, which carries the measurements and the trade.

Two things make it sound rather than merely plausible. **Both senses of absent go.** Cosmos
distinguishes a property holding JSON `null` from a property that is not there, and sorts
`undefined` below `null` below everything else — so excluding only one of them would leave the
other to arrive first ascending and the guarantee would be false. SQL `IS NOT NULL` renders as
`IS_DEFINED(p) AND NOT IS_NULL(p)`, which excludes exactly the two. And **the predicate and the
ordering leave as one statement**, so the guarantee holds at the service and not only in the plan.

Only the explicit `IS NOT NULL` form is read. A comparison such as `c.v > 'a'` also drops nulls
under SQL's three-valued logic, and appears to under Cosmos's rule that a comparison across types
yields `undefined` — but that rule is unmeasured here, and over a path typed `ANY` the values
compared are whatever the documents hold. A wrong answer is the failure mode, so the wider form
waits on evidence.

**It reaches promoted columns and not paths inside the map column, and the reason is structural.**
The guarantee is read from `RelMdPredicates`, which carries a predicate through a projection only
where the projection is a `RexInputRef`. A promoted column projects as a plain reference and its
predicate survives; a document path projects as `ITEM($0, 'name')` over the map column — not a
reference, and over an input the projection does not output — so the predicate is dropped.
Measured, at rule-firing time, against a live planner:

```
WHERE c."category" IS NOT NULL        →  {pulled[IS NOT NULL($1)]}
WHERE JSON_VALUE(c."DOC", '$.name') IS NOT NULL    →  {}
```

That is worth stating plainly, because it says something about the typed-column question that the
question does not say about itself: what a declared column buys is not only a type the planner can
see, but a path that projects as a *reference*, at which point Calcite's existing metadata layer
starts working over it with no adapter code at all. Nullability in particular then needs nobody's
declaration — the query already carries it.

**The guarantee is taken once, in the rule, and carried on the node.** What the metadata answers
depends on which equivalent of the input is asked: measured, the same query answers with the
predicate while the input is still logical and with nothing once the input has been converted.
Re-deriving it during implementation would therefore throw on a plan the planner had already
chosen, which is the rule-and-renderer disagreement `CosmosSortRule` exists to prevent. Carrying it
is sound because every member of an equivalence set produces the same rows.

**Declaring the provided collation as a trait was considered and does not pay.** The idea is to
declare the order the adapter actually delivers and let the planner insert a corrective sort. It
buys no smaller read in either case: with no row limit the corrective sort consumes the whole
pushed result, so the service-side `ORDER BY` is paid on top of the same in-process sort; with a
row limit the limit cannot ride along, because the first *n* under Cosmos's order are not the first
*n* under the plan's, so it stays above the corrective sort and the read is unbounded again — the
bound being exactly what the mismatch destroys.

**The cost model settles it rather than merely disfavouring it.** The alternative is the plan that
exists today with a `CosmosSort` inserted beneath the same in-process sort: `CosmosSort` costs
`Sort`'s own cost times `CosmosConvention.CostMultiplier`, which is positive, and the in-process sort
above it costs `nLogN(rowCount)` either way — Calcite's `Sort.computeSelfCost` does not discount an
already-collated input and `ClrEnumerableSort` does not override it. So the alternative is
strictly dominated on every input and the planner would reject it every time it was offered. What the
trait would buy is plan legibility — the planner seeing and costing both alternatives instead of the
adapter refusing outright — at the price of a rule that fires constantly and never wins. Recorded as
a deliberate decline rather than an omission.

**`IS_DEFINED` and `IS_NULL` are independent**, confirming the translation of SQL `IS NULL`:

| document | `IS_DEFINED(v)` | `IS_NULL(v)` |
| --- | --- | --- |
| `{"v": 1}` | true | false |
| `{"v": null}` | true | true |
| `{}` | false | false |

`WHERE v = null` matches only the explicitly-null document — it is *not* SQL `IS NULL`. The
emitted `(NOT IS_DEFINED(v) OR IS_NULL(v))` matches both that and the absent case, as intended.
Documents missing the sort property are returned by `ORDER BY`, not dropped.

**Aggregates do not share SQL's null handling.** Measured over `{10, 20, null, 5}` with two
documents lacking the property:

| Expression | Cosmos | SQL |
| --- | --- | --- |
| `COUNT(1)` | 6 | 6 |
| `COUNT(c.v)` | 4 — counts the JSON `null` | 3 |
| `SUM(c.v)` | `undefined` | 35 |
| `AVG(c.v)` | `undefined` | 11.67 |
| `GROUP BY c.g` where `g` is absent | group whose key is omitted from the result | a null group |

So `COUNT(*)` is safe, while the value aggregates agree with SQL only over an input that cannot
be null — the same reasoning that governs sort null placement. `CosmosAggregate` pushes down
accordingly and declines otherwise.

**A flat select list and an object constructor are not interchangeable.** The documentation
presents `SELECT e1 AS p1, e2 AS p2` as sugar for `SELECT VALUE { p1: e1, p2: e2 }`, and for
ordinary projections they behave identically. But the service rejects an aggregate inside an
object constructor:

```
Compositions of aggregates and other expressions are not allowed.
```

So a grouped projection has to be written flat. `CosmosQueryBuilder.FlatProjection` selects the
form and `CosmosAggregate` sets it. Nothing in the documentation indicates this; it surfaced only
by executing generated statements against a live service.

This is also why only `id`, `_ts` and `_etag` are declared non-nullable. A partition key path is
declared but a document may still omit it, and typing such a column non-nullable licences the
planner to rewrite `COUNT(x)` into `COUNT(*)` on a guarantee the data does not provide.

**The emulator does not implement composite indexes at all.** A container created with one
composite index reports zero on both the create response and a subsequent read, while excluded
paths in the same policy survive — so the definition is silently discarded rather than rejected.
Consistently, multi-key `ORDER BY` was accepted on containers with no composite index,
cross-partition, with mixed directions, and even on a path explicitly excluded from the index.

This contradicts the documented service behaviour. The composite index guard is retained on the
strength of the documentation; **the emulator can verify neither the guard nor the metadata
round-trip**, and both should be re-checked against a real account before the adapter is relied
on.

---

## Decision: hand-built SQL, not `RelToSqlConverter`

Because Cosmos SQL is textually SQL-like, routing `RelNode` trees through Calcite's
`RelToSqlConverter` and a custom `SqlDialect` is the obvious first instinct. It is the wrong
choice, for two independent reasons.

### `SqlImplementor`'s core mechanism is unavailable

`SqlImplementor` has exactly one strategy for a plan that does not collapse into a single flat
`SELECT`: when the next operator would overwrite an already-occupied clause, it wraps the
current result in a **sub-select** and opens a fresh clause context. The `Result` type, the
`Clause` ordering, and the alias bookkeeping all exist to serve that mechanism.

Cosmos has no derived tables (property 2). The single most valuable thing `RelToSqlConverter`
would give us is the one thing the target cannot express — and the converter decides to nest
based on internal clause state, not on anything a `SqlDialect` can veto. When it nests, it
emits SQL Cosmos rejects.

### `SqlDialect` is the wrong lever

`SqlDialect` hooks *unparsing*: quoting, operator spelling, `OFFSET`/`FETCH` syntax. Every
Cosmos divergence is **structural**, not lexical:

| Divergence | Why a dialect cannot fix it |
| --- | --- |
| `JOIN` means unnest | Requires rewriting the plan, not the tokens |
| Projection is an object literal | `SELECT VALUE { … }` has no `SqlNode` |
| Identifiers are paths (`c.prop`) | Not a quoted `"c"."prop"` identifier pair |
| No `CASE` | Nothing valid to unparse `SqlCase` into |
| `GROUP BY` excludes `ORDER BY` | A planner-level constraint, not a syntax one |

### The counter-argument, and why it still loses

Because the pushable envelope is so small, a Filter+Project+Sort+Limit over one container is a
shallow tree that would never *trigger* nesting — so the derived-table problem might never bite
in practice. True, but it concedes the point: we would carry the full weight of
`RelToSqlConverter` for a job that never needs its hard part, while still fighting it on
`SELECT VALUE`, path identifiers, unnest, `CASE`, and `@p` parameter syntax.

### What is worth borrowing

The *shape* of `RexNode` → target-expression translation, not the implementation. See
*Expression translation* below.

---

## Planned Structure

### Convention

`CosmosConvention` is a `Convention.Impl` bound to a single container. It is not a singleton:
a query spanning two containers uses two instances, and the planner inserts converters between
them. `CostMultiplier` (0.8) biases the planner toward pushing work into Cosmos.

`register(RelOptPlanner)` will add the converter rules listed below.

### Implementor

`CosmosRel` nodes do not return SQL fragments. Each contributes state to a mutable
`CosmosImplementor`, which renders the final statement once the whole subtree has been visited:

```csharp
public interface CosmosRel : RelNode
{
    void Implement(CosmosImplementor implementor);
}
```

Naming follows the house style in the sibling `calcite-dotnet` repository: members overriding
Java declarations keep their lowercase Java names (`register`, `getInterface`), while new
.NET-side contracts are PascalCase.

`Fields` binds an input field ordinal to a document path, and an entry may be **null** — the field is
a computed projection, which addresses nothing Cosmos can name. That is per ordinal rather than per
binding, and the distinction is worth stating: a projection of `UPPER(c.id)` alongside `c.id` leaves
the second still sortable, where clearing the whole binding declined every operator above it. An
operator refuses only when it actually reads an unbound ordinal.

**A rule decides on the same binding implementation will use.** `CosmosImplementor.TryBindOutput`
derives it by walking the input and mirroring each node's `Implement`; a node it does not know returns
nothing and the rule declines. Deriving it from the input row type instead — which every rule used to
do — reads a projection's aliases as document properties: `CosmosSortRule` would name `c.u` for a
column called `u`, find it resolvable, convert, and leave the refusal to `Implement`, contrary to the
rule contract below. Worse, it checked a multi-key sort against the container's composite indexes
using those invented paths, so the legality answer was about paths the container does not have.

`CosmosImplementor` accumulates:

| Field | Renders to |
| --- | --- |
| Root alias | `FROM <container> <alias>` |
| Unnest bindings | `JOIN x IN <path>` (ordered) |
| Projection | `SELECT VALUE { … }` or `SELECT VALUE <expr>` |
| Predicate | `WHERE …` |
| Group keys + aggregates | `GROUP BY …` |
| Collations | `ORDER BY …` |
| Offset / fetch | `OFFSET n LIMIT m` |
| Parameters | `@p0`, `@p1`, … plus a bound value list |

This mirrors how Calcite's own non-JDBC adapters work. **Cassandra is the closest precedent** —
CQL is likewise SQL-shaped, and Calcite still hand-builds it rather than routing through
`RelToSqlConverter`.

### Pushdown envelope

| Node | Rule | Notes |
| --- | --- | --- |
| `CosmosTableScan` | — | ✔ Terminal. One per container; nothing composes beneath it. |
| `CosmosFilter` | `CosmosFilterRule` | ✔ Only when every `RexNode` is translatable. Refused above a projection, since `WHERE` precedes `SELECT`. |
| `CosmosProject` | `CosmosProjectRule` | ✔ Renders as an object constructor. Rebinds field ordinals to the projected paths, or clears them when any projection is computed. Declined where the subtree has already chosen its `SELECT`: a statement has one, and there is no derived table to nest a second in. |
| `CosmosSort` | `CosmosSortRule` | ✔ Carries `OFFSET`/`LIMIT`. Blocked if aggregation present. Multi-key sorts require a matching composite index; null placement must be honourable. |
| `CosmosUnnest` | `CosmosUnnestRule` | ✔ From `Correlate` over `Uncollect`, **never** from `Join`. Sits above a projection and adds the element to it. A predicate over the element is emitted beside it as a `CosmosFilter`. |
| `CosmosAggregate` | `CosmosAggregateRule` | ✔ `COUNT(*)` always; `SUM`/`MIN`/`MAX`/`AVG` only over a non-nullable input. Blocked if a sort is present. Supersedes a path-only pruning projection. |

Deliberately absent, and not to be added later without revisiting this document:

- **No `CosmosJoin`.** Relational joins are inexpressible (property 1). No rule may convert a
  `Join`. Array traversal arrives via `Uncollect`/`Correlate` instead. A predicate over a traversed
  element does not weaken this: `CosmosUnnestRule` emits it as a `CosmosFilter` beside the traversal,
  so it is a `WHERE` the service evaluates after the cross-product, never a condition on the `JOIN` —
  which still has no predicate in the grammar and still is not given one.
- **No `CosmosUnion` / `CosmosIntersect` / `CosmosMinus`.** No set operators exist. Calcite's
  enumerable runtime handles these in-process.
- **No `CosmosValues`.** There is no container-independent row source.

`CosmosAggregate` and `CosmosSort` are **mutually exclusive** (property 4). This is enforced as
a rule guard: each rule must refuse to fire if the implementor state already holds the other.
No `SqlDialect` could express this constraint, which is itself evidence for the chosen design.

Anything that cannot be pushed down falls back to Calcite's enumerable runtime. The adapter
must **never** emit a statement it is unsure of — an untranslatable operator is a signal to
decline conversion, not to guess.

### Plan order is not clause order

A statement has one of each clause, and Cosmos evaluates them in a fixed order. An operator the
plan places *above* another may therefore be written into a clause that runs *before* it, which
silently changes the result rather than producing an error. Every node guards against the cases
that matter:

| Node | Refuses above | Because |
| --- | --- | --- |
| `CosmosFilter` | a projection | `WHERE` is evaluated against the source document, before `SELECT` |
| `CosmosFilter` | a row limit | `WHERE` runs before `OFFSET`/`LIMIT`, so it would filter the whole set and then restrict |
| `CosmosAggregate` | a row limit | `GROUP BY` runs before the restriction |
| `CosmosUnnest` | a sort, grouping, or row limit | a traversal multiplies rows, so it must precede all three |
| `CosmosUnnest` | a `DISTINCT` | `DISTINCT` de-duplicates what `SELECT` constructs, which the service does after the `JOIN`, so folding the traversal in would de-duplicate the multiplied rows |
| `CosmosSort` | another sort, or a grouping | one `ORDER BY` per statement; Cosmos rejects it alongside `GROUP BY` |

A sort *without* a restriction commutes with a filter, so that pairing stays available.

**A predicate over the traversed element is a `WHERE`, and reaching it takes two rules.** A query
writes such a predicate above the correlate, where `CosmosUnnestRule` cannot see it and the element
has no document path a filter above the traversal could name — so it stayed outside the statement and
every element of every document crossed the wire to be discarded in process.
`CoreRules.FILTER_CORRELATE` pushes it into the correlate, between the traversal and the uncollect,
which is the shape the rule recognises; the rule lifts it back out as a `CosmosFilter` over the
`CosmosUnnest`, addressing the element by the ordinal the traversal binds to its alias. The clause
order does the rest: `JOIN` then `WHERE` is exactly traverse-then-restrict.

A host on Calcite's standard rule set already carries `FILTER_CORRELATE`, which is why the shape the
rule reads is the shape a host produces. It is registered here for the reason the other Calcite
rewrites are — a bare Volcano planner has none of them, and a pushdown must not depend on which rules
a caller happened to add. Where the predicate does not render, the untransposed plan survives the
transformation and the traversal still pushes with the predicate above it.

**A projection is not on that list, and it used to be.** `SELECT` runs *after* `JOIN`, so a
projection below a traversal is written into the clause the service evaluates last: the object it
constructs is the one the plan asked for, it is simply a property short, having been written
before the element existed. `CosmosUnnest` adds the element to it. Refusing instead refused every
traversal a host plans — Calcite's own rule set hoists the traversed array into a projection on
the correlate's left, so there is no traversal without one, and a feature the pushdown table
advertised could not be reached from SQL at all. `DISTINCT` is the exception in the table above
because it is the one projection whose *meaning* depends on running before the multiplication.

### Expression translation

A `RexVisitor` emits Cosmos scalar expressions directly, with no `SqlNode` round-trip. It is
roughly the effort of populating a dialect's operator table, minus the intermediate tree, and
it provides the natural place to **reject** unsupported operators so the planner falls back
cleanly.

Specific obligations:

- `RexInputRef` → a path expression (`c.prop`, `c.a.b`, `c["odd name"]`), not a quoted
  identifier. Bracket form whenever the property name is not a bare identifier.
- `RexLiteral` → JSON literal, or a bound `@pN` parameter for anything non-trivial.
- `CASE` → nested ternary (`? :`) where the arms permit it; otherwise decline.
- Unknown operator → decline. Never emit a best guess.

The scalar functions carried across are the ones where Calcite's standard operator table and Cosmos
agree on name, arity *and* meaning. Most are a direct rename; four are not, and those are the ones
worth naming:

| SQL | Cosmos | why it differs |
| --- | --- | --- |
| `ATAN2` | `ATN2` | Cosmos follows T-SQL's spelling |
| `TRUNCATE` | `TRUNC` | one argument only; the decimal-places form is unverified and declined |
| `CARDINALITY` | `ARRAY_LENGTH` | SQL counts a collection *or a map*, Cosmos only an array, so the map case is declined rather than answered wrongly |
| `x MEMBER OF a` | `ARRAY_CONTAINS(a, x)` | the operands swap |
| `TRIM`/`LTRIM`/`RTRIM` | same | Calcite carries `[flag, chars, string]`; the flag picks the function, and only trimming spaces is translated |

#### Casts over document values

The row model types every document path `ANY`, so a view can only give a column a SQL type by
wrapping the access in a cast — `CAST(JSON_VALUE(p."DOC", '$.price') AS INTEGER)`. A cast is opaque to translation,
which means every operator over a typed view column declines and the container is read whole. That is
worth fixing, and almost every way of fixing it is wrong.

**A cast is not a no-op, and dropping one is not a shortcut.** Calcite's cast over an `ANY` value
converts: measured against a container seeded to disagree with itself, `CAST(price AS INTEGER) = 30`
matches the document storing `"30"` and the one storing `30.7` as well as the one storing `30`. The
service compares the stored value as it stands and matches only the last. So dropping the cast loses
rows, and there is no cost argument that makes that acceptable. Numeric casts are declined.

**One shape is exempt, and it is an equivalence rather than a trade.** `CAST(x AS VARCHAR) = 'text'`
selects exactly the documents whose stored value is the string `'text'`, provided no other JSON value
renders as `'text'`: a string renders as itself, a number as digits, a boolean as `true` or `false`,
an array or object with a bracket. `c.x = 'text'` selects exactly the same documents at the service,
including for absent and null, which match under neither. So the cast is dropped there and only there
— see `CosmosRexTranslator.TryTextCastOperand`.

**The same cast over `JSON_VALUE` is the same cast.** A view over `_JSON` writes
`CAST(JSON_VALUE(doc, '$.x') AS VARCHAR)` where a view over `_MAP` writes `CAST(doc['x'] AS VARCHAR)`,
and for a while only the second was dropped, because the test was that the operand is typed `ANY` —
which a `JSON_VALUE` without `RETURNING` is not; Calcite types it `VARCHAR(2000)`. That keyed the
exemption off the map subscript rather than off the value, and a caller-applied equality over a `_JSON`
view read the container whole (#71). Measured against Calcite's own runtime, the accessor renders what
the cast over `ANY` renders — `30` as `30`, `1e30` as `1.0E30`, `true` as `true`, a string as itself —
and answers null for an absent path, a null, an object and an array, which the comparison then does
not keep; and it applies no width at run time, `RETURNING VARCHAR(3)` returning `'bikes'` whole. So the
argument above holds for it unchanged, and `CosmosRexTranslator.IsRenderedDocumentValue` admits the
two-operand `JSON_VALUE` of a character type beside a value typed `ANY`. A `RETURNING` that converts
is refused, since the cast then renders a converted value; `JSON_QUERY` is refused, being the JSON
text of an object and null for a scalar; and a behaviour clause is refused, substituting a value where
the path has none.

**The bare accessor is that cast with nothing written, and it had been pushed as the path.** SQL:2016
casts the scalar `JSON_VALUE` finds to the returning type, and Calcite does: measured,
`JSON_VALUE(doc, '$.x') = '30'` keeps the document storing the number 30, because the number arrives
as the text `30`. The path at the service holds the number, and `c.x = '30'` does not keep it. That
was a row lost in silence, and the parity measurement above did not see it because it compared plans
rather than rows. The adapter's contract is Calcite's semantics, which here are the standard's, so an
equality over the bare accessor is now held to the same literal test as the cast form: `= 'bikes'`
pushes, since the two select the same documents; `= '30'`, an equality against another expression,
and one carrying a behaviour clause are declined, and `CosmosFilterSplitRule` pushes what they imply.
`TODO.md` carries what the same reasoning says about every other operator over the bare accessor,
which is a decision rather than a fix.

**What a refused text equality implies is more than `IS_DEFINED`, and it is a disjunction.**
`= '30'` keeps a stored string `'30'` and every stored number Java renders as `30`, and every such
number has the value 30 — so the value is that string or it is that number, and the rule pushes
`c.x = '30' OR c.x = 30` under the comparison Calcite still makes. It is implied and not exact, by one
spelling: a stored `30.0` is a double in the JSON text Calcite reads and renders `30.0`, while at the
service it is the same number as `30`, so the number branch keeps it and the recheck drops it. That
row crossing the wire is the whole cost, against a container read whole. The branches follow the
rendering: a number-like literal takes the parsed number, or `IS_NUMBER` where the double cannot hold
it; exactly `true` or `false` takes the boolean, since Calcite renders in lowercase and `'TRUE'`
matches only the string; and over the map column, whose cast renders an array as `[x, y]` and an
object as `{x=1}`, a literal opening with that bracket takes `IS_ARRAY` or `IS_OBJECT`. `JSON_VALUE`
answers null for those and for a JSON null, so under it no such literal is ambiguous at all and the
translator pushes it exactly — its literal test is the narrower one, `IsUnambiguousTextFor`. The
branches are written against the accessor with its type discarded, which is how the translator is
told to render the path without applying the conjunct's test to a branch that is not the conjunct.

The literal is what carries the argument, so the literal is what is checked. Anything that parses as a
number, `true`, `false`, `null`, and anything opening with a bracket or a quote are refused, because a
non-string value could have rendered as them. This is not caution for its own sake: in the differential
container, `= '30'` matches the document storing the *number* 30 and `= 'true'` the one storing the
boolean, and both would have gone missing.

**What it recovers.** A view exposing the partition key as text routes to one partition again, which is
the largest cost lever there is and the one that was being lost in silence. Recovered for routing only:
`TryExtractPrefix` admits the cast form and `TryExtract` does not, so the point read and the
whole-partition delete — each of which replaces the predicate with an operation that applies none —
keep the cast opaque. Routing narrows which partitions are visited and filters nothing, so the rows are
decided by the same comparison either way.

**A numeric comparison states a bound even though it has no form.** `CAST(price AS INTEGER) > 10`
cannot be translated, and it still *implies* something the service can apply. Converting a number to a
number moves it by less than one — for the targets where that holds, which is fewer than it looks — so
every document the predicate keeps has a raw value greater than 9. `CosmosFilterSplitRule` pushes that and rechecks the predicate
above:

```sql
IS_DEFINED(c.price) AND (NOT IS_NUMBER(c.price) OR c.price > 9)
```

**The type test admits rather than excludes, and that is the whole of it.** Calcite converts a stored
string too — measured, `= 30` keeps a document storing `"30"` — so filtering to numbers would lose it.
Anything that is not a number passes untouched and is decided above. In a container whose field really
is numeric, which is what a typed view asserts, that branch matches nothing and the service does all the
work; in one that is not, the query is slower and the rows are the same. The bound is deliberately loose
— a whole unit either side — because the exact window depends on rounding direction and sign, and none
of that has to be decided to make the bound sound.

**Which targets, and why not the others.** Measured against Calcite's own runtime, because the premise
is a claim about it and nothing else:

| target | `1e30` converts to | bound |
| --- | --- | --- |
| `INTEGER`, `BIGINT` | saturates at the limit — `2147483647` | yes, except at the limit itself |
| `DOUBLE` | identity | yes |
| `SMALLINT`, `TINYINT` | **wraps** — `-1` and `255` | no: unrelated to the stored value |
| `FLOAT`, `REAL` | rounds to float precision, 1.5e22 away | no: further than a unit |
| `DECIMAL` | raises, where it does not fit the precision | no: excluding the document would turn a failing query into a passing one |

Saturation is the row that had to be found rather than reasoned about. `CAST(x AS INTEGER) =
2147483647` is true of a document storing `1e30`, so a window around the limit excludes exactly the
document that matches — and the corpus caught it as a lost row before the bound stopped stating that
side. Only equality is affected; the inequalities already admit everything past the limit.

`CAST` and `SAFE_CAST` are treated alike, and the difference between them is why the sieve has to admit
rather than exclude: a value that will not convert raises under one and yields null under the other,
and the bound never excludes a value that is not a number, so whichever it is still happens.

Opening this up meant weakening a plain conjunct at all. Only disjunctions were weakened before, which
left the commonest untranslatable shape — a single comparison with no Cosmos form — pushing nothing
whatever, not even the definedness it implies. A conjunct is positive by construction, which is the
whole of the polarity argument, so the same weakening applies directly.

**The general shape.** Where the two engines agree on part of the value space and not the rest, a type
test names the part they agree on, and the predicate is pushed there and left alone elsewhere. That is
what makes a JSON store with no schema addressable at all: `IS_NUMBER`, `IS_STRING`, `IS_BOOL`,
`IS_ARRAY`, `IS_OBJECT`, `IS_DEFINED` and `IS_NULL` are each a way of carving out a region where a SQL
operator means what it says. Every use has to be either implied by the predicate — pushed alongside it,
the predicate rechecked above — or equivalent to it, and the differential corpus is what tells the two
apart. There is more here than the two cases taken so far.

**What is still declined.** Sorting by a cast column and joining on one. Each needs a reader that
knows what type a path was declared to have — the schema-level `columns` binding — because each
carries the value rather than comparing it, and no filter helps with that. Projecting one does not,
for the reason below.

**Projecting a cast to text is a reading, not a translation.** Nothing at the service reproduces what
Calcite renders, and the obvious repairs are both wrong. Measured over one document per JSON type:

| stored value | Calcite `CAST(… AS VARCHAR)` | Cosmos `ToString` | the bare path |
| --- | --- | --- | --- |
| `"bikes"` | `"bikes"` | `"bikes"` | `"bikes"` |
| `30` | `"30"` | `"30"` | `30` |
| `30.7` | `"30.7"` | `"30.7"` | `30.7` |
| `1e30` | `"1.0E30"` | `"1e+30"` | `1e+30` |
| `true` | `"true"` | `"true"` | `true` |
| `{"v":"bikes"}` | `"{v=bikes}"` | `"{\"v\":\"bikes\",\"_propertyOrder\":[\"v\"]}"` | an object |
| `["x","y"]` | `"[x, y]"` | `"[\"x\",\"y\"]"` | an array |
| `null` | null | `"null"` | null |

The bare path returns a JSON number where the plan declared text, and `CosmosJson.GetString` refuses
to coerce one — deliberately, since coercing would make the row type a suggestion — so the statement
would not return a different answer, it would *fail*, for data the in-process plan handles. `ToString`
agrees on the plain scalars and disagrees on four things that matter: JSON null becomes the *string*
`"null"` where SQL wants null, an exponential number is written in JSON's notation rather than Java's,
and objects and arrays render in Cosmos's — with, on the emulator, an internal `_propertyOrder` key
leaking into the text. None of the four can be excluded statically over a path typed `ANY`.

**What the first column actually is, is Java's rendering of the box the reader already builds.** A
number is a `Long` or a `Double`, a boolean a `Boolean`, an array an `ArrayList`, an object a
`LinkedHashMap` — `CosmosJson.GetNatural` builds exactly those, because that is what Calcite holds an
`ANY` value in, and Calcite's cast to `VARCHAR` over one is that object's own `toString`. So the cast
does not have to be rendered at the service or reproduced here: the value is sent as it stands and
rendered as it is read, by the same code that would have rendered it. `CosmosJson.GetText` is one
`String.valueOf`, and the equality it rests on is the differential corpus's to keep — the `typed`
container seeds `label` as a string, a number, a boolean, an array, an object, null and nothing, and
those statements compare the pushed rendering against the in-process cast for all seven.

`CosmosRexTranslator.TryRenderedTextOperand` recognises the shape and `CosmosProject` records a
`CosmosReading.Text` for that ordinal, which the row builder reads by. **Only an undecorated
`VARCHAR`**: a width is a second conversion the reader does not perform, and measured, `VARCHAR(3)`
truncates `'bikes'` to `'bik'` while `CHAR(8)` pads it — so both keep the cast in process. `SAFE_CAST`
is admitted beside `CAST`, the two differing only in what happens when a conversion fails, which
rendering a value as text never does. **Casts to a number are still declined**, and for the reason
above: they convert rather than render, and no reading reproduces a conversion the service did not do.
**Only the map column's spelling.** The cast over `JSON_VALUE` drops in a comparison, where the literal
excludes every case that could differ; a projection has no literal, and measured, `JSON_VALUE` answers
null for an object or an array where the reader renders one as `{x=1}` or `[x, y]`. A rendered column
over it would carry text for a document the in-process plan carries nothing for, so it stays in
process — the filters around it push regardless.

**A rendered column addresses no document path, and that is what makes it sound.** The column carries
text where the document holds something else, so a filter or a sort above it written against the raw
path would mean something different — `= '30'` is true of the rendered number and false at the
service, and as text `10` sorts before `9`. Such a column binds to `null` exactly as a computed one
does, and the operators reading it decline. So the projection pushes and the ordering does not, which
is the split the next section is about.

**The accessor column is the exception, and it binds as a rendering.** A projection of `JSON_VALUE`
read as text is rendered guarded — `(IS_PRIMITIVE(c.name) ? c.name : null)`, read as text — and,
unlike the cast, it *binds to the path*: a view's `Name` column is `c.name` to a filter above it, so
that `IS_DEFINED`, an exact equality and a sort keep pushing through the view. What the binding did
not say was that the column is the rendering rather than the value, and so a comparison over the
column was pushed raw — `p."Name" LIKE 'Acadia%'` became `STARTSWITH(c.name, 'Acadia')` with no
guard, the very statement the accessor's own spelling is declined for. Wrong in silence under a bare
planner; a failure under a host's, whose `FILTER_PROJECT_TRANSPOSE` copies the accepted filter below
the projection with the accessor inlined, and the implementor then refuses what the rule had accepted
(#83). The binding now carries how each column is read — `CosmosImplementor.TryBindOutput` reports it
beside the paths, derived as `CosmosProject` derives it — and the translator holds a field read as
text to every test it holds the accessor to: `CosmosRexTranslator.IsTextRendering` is the accessor
test in both spellings. So the comparison declines over the column exactly as over the accessor, and
`CosmosFilterSplitRule` pushes the same guard above the projection, `NOT IS_STRING(c.name) OR
STARTSWITH(c.name, 'Acadia')`, and rechecks the pattern in process.

The raw value in that guard is named differently in the two spellings, and the difference is what
survives a host. Over the accessor the rule re-types the call `ANY`, which says "the value at this
path" rather than "its rendering". Over the column it *casts* the field to `ANY` instead: a re-typed
reference says the same thing, but `FILTER_PROJECT_TRANSPOSE` replaces a reference with the
projection's expression and the type that said it goes with it — the guard would arrive below the
projection as the rendered comparison again, and be refused again. A cast is a call of its own, is
copied intact, and `CosmosRexTranslator.WriteCast` renders a cast to `ANY` over a rendering as the
path. The residual half of the split needs no such care: no rule converts it, because the filter rule
declines it in either spelling, and nothing copies a Cosmos filter that was never made.

**Ordering by an expression is refused by the service anyway.** Measured, and it closes the question
rather than leaving it a matter of caution: `ORDER BY ToString(c.label)`, `ORDER BY UPPER(c.label)` and
`ORDER BY c.label || 'x'` each answer 400, error code 2206 — *"Unsupported ORDER BY clause. ORDER BY
item expression could not be mapped to a document path."* `ORDER BY (c.label)` is accepted, so the
restriction is exactly what the message says: the sort item must *be* a path. Rendering a cast into the
clause was therefore never available, whatever it would have cost. So paging a view by one of its own
cast columns reads every matching document. The one thing that would change it is a column the sort
could name, and that surface is rejected — `TODO.md` section 6 — which makes this a standing cost of
the row model rather than a pending item.

`COALESCE` and `NULLIF` need no entry — the validator expands both to `CASE` before a `RexCall`
exists. Several plausible additions are deliberately absent: `LOG(x, base)` and `SQUARE` are not in
Calcite's standard table, so nothing can produce them; `CBRT` is, and Cosmos has no counterpart. The
`IS TRUE` / `IS FALSE` family and `IS DISTINCT FROM` are declined because reproducing their null
semantics over a property that may be *undefined* needs a Cosmos behaviour that has not been
measured, and a wrong answer is worse than a refused pushdown.

### A projection that cannot be pushed is not a wall

A view is how a caller gives a container a relational shape, and a view has to cast: the row model
types every path `ANY`, and nothing downstream that expects columns of a type can consume `ANY`. A cast
to text is rendered by the reader and pushes; the rest — a width, a numeric target — stays in process,
and a sort and a row limit above it used to stay with it, so a bounded page over a view read every
document the predicate matched.

`CoreRules.SORT_PROJECT_TRANSPOSE` is registered for this, alongside the other Calcite rewrites the
rule set carries because a bare Volcano planner has none. Transposed, the sort and its limit sit under
the projection and push; the cast runs over the rows that come back.

```
ClrEnumerableProject(id=[$1], p=[CAST(ITEM($0, 'price')):INTEGER])
  CosmosToClrEnumerableConverter
    CosmosSort(sort0=[$1], dir0=[ASC], fetch=[10])
      CosmosTableScan(table=[[products]])
```

**It fires only where the collation survives the transpose**, and that is what makes it sound rather
than merely profitable. Calcite maps the sort keys through the projection and declines unless every
one is a plain reference — so ordering by a cast column, which is not ordering by the path underneath
(rendered as text, `10` sorts before `9`), stays above the projection where it belongs. A
transformation adds an equivalence rather than replacing one, so the untransposed plan survives and
the planner costs both.

This does not make an unrenderable projection pushable, and it is not a substitute for a column the
sort could name — a surface the row model declines, `TODO.md` section 6. What it removes is such a
projection's ability to strand everything above it. Where the
cast is to text the projection pushes on its own and there is nothing left to transpose past — but the
rule still carries the cases that do not render, and the guard is what keeps it from carrying the sort
that must not move.

### Full text search

Cosmos has full text search and SQL does not, so there is nothing in Calcite's standard operator table
to map onto it. `CosmosOperators` defines the operators, and they are offered two ways.

#### Two routes to a name, and why there are two

`CosmosOperators.Instance` is an operator table, chained into the one the validator is built with.
That is what a host does when it assembles its own planner, and for a long time it was the only route
— which meant full text search was the one thing on the feature list an application reaching the
adapter the documented way could not use, because a connection does not build a validator for anyone
to chain anything into.

`CosmosSchemaFunctions` is the other route: a `CosmosSchema` and a `CosmosAccountSchema` declare the
same operators through `Schema.getFunctions`, and the catalog reader — which `Prepare` chains into
every statement's operator table — resolves a schema's own functions. So a connection finds them
without being told.

**Declared at both levels**, and that is the decision the two-level shape forces. An unqualified name
is resolved against the connection's default schema and the root, and nowhere else: a model naming a
`database` roots the connection at a `CosmosSchema` with no account above it, and a model naming an
account roots it at the `CosmosAccountSchema` while the containers live a level down. Declaring at
both is what makes the name work wherever a query is rooted, and no arrangement searches both levels
for one unqualified name, so nothing resolves twice.

**Chaining as well is not a duplicate.** Overload resolution takes the first candidate whose arity
fits, and the chained table comes before the catalog reader — so the operator answers and the schema's
declaration is never reached. The README says the chaining is optional rather than required.

**Every one of these names is Cosmos's alone, and that is measured rather than assumed.** None of the
twenty-one appears in `SqlStdOperatorTable` or in the union of all fourteen `SqlLibrary` tables, at
any casing. It matters because the same resolution order that makes chaining harmless makes a
collision silent: a connection chains the table its `fun` property names *ahead* of the catalog
reader, so the day Calcite gives some library a function called `IS_ARRAY`, that operator would answer
and the schema's declaration would stop being reached — for hosts that set `fun` and for nobody else.
`NoneOfTheNamesIsOneCalciteAlreadyUses` is the tripwire.

The near misses are near on purpose. Calcite has `IS_INF` and `IS_NAN` beside this family, and
`STRING_TO_ARRAY` and `REGEXP_LIKE` beside `StringToArray` and `REGEXMATCH` — the last two named apart
deliberately, being different functions rather than different spellings, and the operator comments say
why. `IS JSON ARRAY` and the `JSON_*` family are the closest thing SQL has to the type tests and are
still not the same question: they ask whether a *string* holds JSON text of some shape, where the row
model holds parsed values and Cosmos asks what the value *is*.

**What arrives is not the operator.** Calcite reads a schema function's parameter list and builds a
`SqlUserDefinedFunction` of its own around it, carrying the name and the arity. The translator and the
rules therefore ask a call for its *name* rather than for its identity — `IsScoringFunction` always
did, and `CosmosOperators.IsAbsenceObserving` was changed to, an identity test there having been a
soundness hole waiting for the second route to exist.

**One declaration per arity, not one with optional parameters.** A call to a function whose type
checker has fixed parameters is padded out to the whole parameter list with `DEFAULT` before it
reaches the plan — `SqlCallBinding.operands` does it — so a single variadic declaration produced
`FULLTEXTCONTAINSALL(c.name, 'steel', DEFAULT(), …)` and a Cosmos statement has nothing to render
`DEFAULT` as. Declared one arity at a time, every parameter is required and nothing is padded. The
consequence is a bound: a schema function accepts as many operands as it declares parameters, so the
variadic operators are declared up to `CosmosSchemaFunctions.VariadicOperandLimit`, and a query
needing more chains the operator table, whose checker has no bound.

**None of them has a body**, deliberately. A schema function bound to a CLR method would let a call
that cannot be pushed down plan anyway and then answer with something Cosmos never computed; with no
body, the failure is before any row exists. They do nevertheless implement `ImplementableFunction`,
and the implementation throws: declining the interface left Calcite to report it, as `User defined
function FULLTEXTSCORE must implement ImplementableFunction`, which names an interface rather than a
reason and reads as a defect in this adapter. The refusal is the same refusal at the same moment —
Calcite asks for a body while generating code, so a call reaching there is one no rule pushed down —
and what it adds is the sentence saying why.

Signatures are the service's, from the query language reference:

| function | | |
| --- | --- | --- |
| `FULLTEXTCONTAINS(path, keyword)` | boolean | `WHERE` |
| `FULLTEXTCONTAINSALL(path, keyword, …)` | boolean | `WHERE` |
| `FULLTEXTCONTAINSANY(path, keyword, …)` | boolean | `WHERE` |
| `FULLTEXTSCORE(path, keyword, …)` | BM25 score | **`ORDER BY RANK` only** |
| `RRF(scoring function, …, weights)` | fused score | **`ORDER BY RANK` only** |

The first argument of every one is a **property path**, not an expression, and the translator holds
them to it: a call over anything that does not resolve to a path is declined rather than rendered.
Keywords bind as `@pN` like any other literal, so statement text stays independent of what is searched
for.

**The two scoring functions are a different kind of thing.** `FULLTEXTSCORE` and `RRF` may appear
*only* in an `ORDER BY RANK` clause and **cannot be part of a projection**. Measured against an Azure
account, because the emulator implements none of this — every route to the value is the same refusal,
`400`, `SC2240`, *the FullTextScore function is only allowed in the ORDER BY RANK clause*:

| statement | |
| --- | --- |
| `SELECT c.id, FullTextScore(c.name, 'steel') AS s FROM c ORDER BY RANK FullTextScore(…)` | **SC2240** |
| `SELECT c.id, FullTextScore(c.name, 'steel') AS s FROM c` | **SC2240** |
| `SELECT VALUE FullTextScore(c.name, 'steel') FROM c` | **SC2240** |
| `SELECT VALUE { "id": c.id, "s": FullTextScore(…) } FROM c ORDER BY RANK FullTextScore(…)` | **SC2240** |
| `SELECT s.id, s.score FROM (SELECT c.id, FullTextScore(…) AS score FROM c) s` | **SC2240** |
| `SELECT c.id FROM c WHERE FullTextScore(c.name, 'steel') > 0` | **SC2240** |
| `SELECT VALUE { "id": c.id } FROM c ORDER BY RANK FullTextScore(c.name, 'steel')` | 3 rows |
| `SELECT TOP 2 c.id FROM c ORDER BY RANK FullTextScore(c.name, 'steel')` | 2 rows |

The object-literal row is the shape this adapter emits, and the derived table was the last way around
it worth trying: a score computed one level down and read one level up is refused like the rest. So
there is no statement to fall back to, which is what makes these structural rather than tedious. Two
things fell out of the same measurement and are recorded because nothing else here says them: the
keyword arguments are varargs and **not** an array — `FullTextScore(c.name, ['steel'])` is `SC2241`,
whose text also documents a `{term, distance}` object form for fuzzy search that this adapter does not
offer — and `ORDER BY RANK` **ranks without filtering**, returning every document including those with
no match. Calcite sorts by field ordinal, so ordering by an expression outside
the select list becomes three nodes:

```
LogicalProject(id=[$0])                                     drops the score
  LogicalSort(sort0=[$1])                                   sorts on it
    LogicalProject(id=[$1], $f1=[FULLTEXTSCORE($0, 'kw')])  adds it
```

and the innermost is a statement Cosmos will not run. `CosmosRankRule` matches the whole shape and
collapses it into one `CosmosRank`, which projects what survives and renders the score into the clause
and nowhere else.

**Matching all three is what makes it safe.** Seeing only the sort would leave the score in the row
type for something above to read, and it would read null — the statement never projects it. Requiring
the outer projection to discard it is how the rule knows nothing does. A consumer reaches that shape
through `RelRoot.project()`, which is where the extra column stops being output; a plan taken from
`RelRoot.rel` still carries it, and is then correctly refused rather than silently returning nulls.

**A connection is the second of those, and that is measured.** `Prepare` plans `RelRoot.rel` and then,
where the root's field mapping is not trivial, wraps the finished plan in a calc that applies it — so
the projection this rule requires is built *after* every rule has run and the three-node shape never
exists while they are running. `ORDER BY FULLTEXTSCORE(…)` through a `CalciteConnection` therefore
plans an in-process sort over a projected score, and fails to implement, which is the refusal above
arriving where it is least useful. Planning the same statement from `RelRoot.project()` gives
`CosmosRank`; from `RelRoot.rel` it does not. Whether the rule should learn the second shape — and
what it would then have to promise about the score column nothing is known to read — is open; see
[#46](https://github.com/ikvmnet/calcite-cosmos/issues/46).

`CosmosQueryBuilder.RankBy` emits the clause and refuses to combine it with an ordinary `ORDER BY` or
with `GROUP BY` — one `ORDER BY` per statement, and the reference says as much of `RRF` explicitly.
The scoring functions are in the operator table so a query can name them, and the translator permits
them through `TranslateRank` alone; everywhere else is a place the service rejects them, so a `WHERE`
or a select list containing one declines.

### The declaration prices a full text function, and still gates a vector one

The operator being nameable is not the same as the *path* being searchable, and only the container
knows which paths are. `CosmosContainerMetadataReader` reads both declarations — the container's full
text policy and the indexing policy's full text indexes, and likewise the vector embedding policy and
the vector indexes — into `CosmosContainerMetadata.FullTextPaths` and `VectorPaths`. What is done with
them differs, and the difference is a measurement.

**Full text: a cost.** The translator used to refuse a full text call over a path neither list names,
as the second legality gate after `IsSortSupported`, on a measurement that such a predicate answered a
**bodyless 400**. That measurement does not reproduce. Measured on 2026-09-11 against three accounts
and four containers, all serverless, through the SDK with no adapter in the path (#85):

| account | full text capability | container policy / index | call | result |
| --- | --- | --- | --- | --- |
| probe | **absent** | none | `FULLTEXTCONTAINS(c.name, …)` | answered, correct row |
| dev1, throwaway | present | `/name` only | `FULLTEXTCONTAINS(c.description, …)` — undeclared | answered, correct row |
| dev1, throwaway | present | `/name` only | `ORDER BY RANK FULLTEXTSCORE(c.description, …)` | answered |
| dev1 `parks` | present | **none** | the predicates and `ORDER BY RANK` | answered, sensible rows |
| sit1 `parks` | present | none | `FULLTEXTCONTAINS`, `ORDER BY RANK` | answered |

Neither candidate boundary holds: not the account capability, and not the policy as an allow-list. So
the gate refused plans that run, and refusing had the worse failure of the two — the declined call
was left above the scan for in-process evaluation, where it has no body, so a query that would have
run raised `FULLTEXTCONTAINS is evaluated by the service and has no in-process body` at execution
rather than the readable planning-time refusal the gate was designed to give. Whatever produced the
original 400 is not the current contract.

What the declaration decides is therefore what the call *costs*: served by the full text index over a
declared path, by a scan over an undeclared one. `CosmosFilter.ReferencesUndeclaredFullTextPath`
reads the union for a predicate and `CosmosRank` for a score, and both apply `UnindexedPathPenalty`
— the price of a scan the ordinary index does not serve, reached another way. The planner keeps the
plan and the caller keeps the choice, which for a small container is often the right trade. Read as
one list, the union says whether the container has said anything at all about a path; a path in the
policy but not in the index most likely scans too, and pricing it as one needs the two lists read
apart, which `TODO.md` carries. The translator still refuses a call whose first argument is not a
path, since the service does; that refusal reaches a caller the same in-process way, and the same
note records it.

**Vector: still a gate, and unmeasured.** `VECTORDISTANCE` over a path the container declares nothing
about is still refused while planning. The reference requires a vector embedding policy to perform a
vector search at all, and describes the index as optional — the function's own brute-force argument
is documented as using "any index defined on the vector property, **if it exists**" — so the case
worth declining is a call in which *neither* vector is a declared path. Nothing in #85 measured it,
and nothing above should be read as evidence about it. Either vector may be a literal — searching for
the neighbours of a supplied embedding is the point of the function — so requiring the first argument
to be a path, as the full text predicates do, would refuse the ordinary case.

### A case fold under `LIKE` is the service's case-insensitive match

`UPPER(x) LIKE '%ACADIA%'` is what an ORM writes for a case-insensitive `contains`, and what a
typeahead is; `CONTAINS(x, 'ACADIA', true)` is the same question asked of the function that answers
it, `STARTSWITH` and `ENDSWITH` taking the same third argument for the prefix and suffix forms (#84).
The rewrite is exact rather than a weakening, and it is exact only under three conditions the
translator checks: the wildcards are one leading and one trailing `%` and nothing else, so what lies
between is matched literally as a unit; the text is already in the case the fold produces, since
`UPPER(x)` never contains a lowercase letter and `LIKE '%acadia%'` under it matches nothing, which the
plain form answers just as well; and the text is ASCII. The last is where the equivalence actually
rests — on the fold Calcite applies and the folding the service applies under the flag agreeing on
every character — and ASCII is where that is known. Java's `toUpperCase` maps `ß` to `SS` and a
ligature to its letters, which no case-insensitive comparison of the stored text reproduces, so
outside ASCII the plain form stands and folds at the service under its own rules as it did.

Over a text accessor the fold has the gap `LIKE` has — `UPPER(30)` is `30` in Calcite, having folded
the rendering, and undefined at the service — so `UPPER(JSON_VALUE(…)) LIKE '%ACADIA%'` is declined
and weakened like `LIKE`: the split rule rebuilds the fold over the raw value, and the guard's own
comparison is what renders as `CONTAINS(c.name, 'ACADIA', true)`, under `NOT IS_STRING(c.name) OR`.
The case-*sensitive* `'%abc%'` and `'%abc'` still render as `LIKE`; whether the named functions are
priced differently is the measurement `TODO.md` still asks for before they change.

### What is deliberately not done: inferring full text from a substring predicate

Rewriting `LIKE '%steel%'` into `FULLTEXTCONTAINS` where the path happens to be declared looks like
the same kind of win as `LIKE 'abc%'` becoming `STARTSWITH`, and it is not, because it **changes the
answer** in both directions:

- `'%steel%'` matches `"steelworks"`; full text does not — a different token.
- Full text matches `"Steel"` by case folding and likely `"steels"` by stemming, in the language the
  container's full text policy declares. `LIKE` matches neither.

Because neither predicate implies the other, `CosmosFilterSplitRule` cannot rescue it either: that
rule pushes a *weaker* predicate and rechecks the original above, which needs the pushed form to
return a superset. Full text is not a superset, so rows the `LIKE` should have returned would be gone
before the recheck ran.

The deeper reason is that a full text result depends on an analyzer and a language declared on the
container, and SQL has no way to name either — which is why the custom operators exist rather than
being an oversight. A caller who wants that trade should ask for it in the operand, the way
`lookupCacheMaxRows` is asked for, rather than have the planner make it for them.

Calcite's own precedent does not argue otherwise. The Elasticsearch adapter maps
`SqlStdOperatorTable.CONTAINS` onto an ES `match` query
([CALCITE-3437](https://issues.apache.org/jira/browse/CALCITE-3437), 1.22.0) — the *period* operator,
reused for text. It has no tests, that adapter's documentation does not mention it, and the operator
does not validate over strings, so no query reaches it. A loose end rather than a pattern.

### Spatial is reachable, and nothing but the operator's name says which reading is meant

This was recorded as closed on the grounds that no amount of translation reaches a type-system
mismatch:

> **Calcite has `GEOMETRY`. It does not have `GEOGRAPHY`.**

Calcite's spatial library is planar JTS over an unprojected coordinate system, answering in the units
of that system. Cosmos is geodesic over the WGS84 ellipsoid, answering in metres. So the two disagree
about what their identically-named functions *mean*.

| | disagreement |
| --- | --- |
| `ST_DISTANCE` | geodesic metres against planar degrees |
| `ST_WITHIN`, `ST_INTERSECTS` | a polygon edge is a great-circle arc to one, a straight line in longitude and latitude to the other |
| `ST_ISVALID` | JTS planar topological validity is not GeoJSON validity |

**The distance is not off by a factor.** The ratio varies with latitude *and* bearing — one degree of
longitude is 111 km at the equator and 19 km at 80° north — so no conversion of the result recovers
it, and no transformation of the inputs does either: a similarity between a curved surface and a flat
one is what Gauss ruled out. **An ordering is worse than wrong, it is differently ordered.** From 80°
north, a candidate one degree east and another half a degree north swap places between the two models,
so no scalar conversion reorders the rows.

None of that changed. What changed is that
[`Apache.Calcite.Geography`](https://github.com/ikvmnet/calcite-dotnet) supplies a family of
`ST_GEOG_*` operators that read coordinates as WGS84 and answer in metres, over S2.

**There is still no `GEOGRAPHY` type, and that is a decision rather than an omission.** A geography
and a geometry are the same type carried by the same class, and the operator's name is the whole of
the marking. `SqlTypeName` is a closed enum, so a type of the package's own has to impersonate one of
Calcite's — and the enum is the key to every table that makes a type behave. A name with no entry in
the assignment table is asserted on rather than rejected, so a function declared through a schema over
such a type takes the validator down. Since a schema is the only way an adapter brings its functions
with it, the type gave way to the registration.

**What that costs is a mixed expression nothing refuses.** `ST_GEOG_DISTANCE(ST_BUFFER(g, 0.1), h)`
buffers in degrees and then measures in metres, and both halves run. This adapter cannot close that;
it is a property of there being one type for two readings.

**What this adapter adds is the one refusal it can make.** The Cosmos spelling of every one of these
is the *unprefixed* one, so what a rendered `ST_DISTANCE` means at the service is decided by the
container's `geospatialConfig` and not by the name in the query. So `CosmosRexTranslator` refuses to
render any `ST_GEOG_*` over a container reading `Geometry`. That is unlike the full text gate, which
exists because the service returns an *error*; here the service returns an *answer*, which is the
worse failure and the reason this one is checked while planning.

**Measured, and the numbers are the argument.** The same statement over the same two points, against
two containers differing only in `geospatialConfig`:

| container | `ST_DISTANCE(c.location, <point>)` |
| --- | --- |
| `Geography` | `1342.1433132701966` — metres |
| `Geometry` | `0.014142135623733162` — the planar hypotenuse in degrees |

Nothing in either response says which question was answered. `CosmosGeographyServiceTests` holds this
and the rest of the forms.

**A geography is not promoted to a column, and does not need to be.** The row model is unchanged: the
map column, `DOC`, and the columns the service guarantees. Nothing in Calcite converts the `ANY` a
map lookup yields into a geometry, so a shape in a document reaches an operator by being parsed out of
text:

```sql
ST_GEOG_DWITHIN(ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location')), …, 1000)
```

In process that is exactly what happens. **Pushed down it is not.** `JSON_QUERY` over `DOC` already
resolves to a document path, so the constructor collapses onto it and the statement names the property:
`ST_DISTANCE(c.location, {…}) <= 1000`. The service reads that property as the shape, so the text and
the parsing are a round trip it never needed. A constructor over a literal is written out as the object
instead; over a computed string it is declined, because rendering one would mean evaluating it and
evaluating it is what the service is being asked to do.

**A prior measurement, kept because it is why the map column is not the answer.** Calcite's spatial
functions cannot evaluate over the map column: the row model materialises a geometry as a
`java.util.LinkedHashMap`, and the conversion Calcite inserts to reach its GeoJSON constructor
produces Java's `toString`:

```
CAST(JSON_VALUE(c."DOC", '$.location') AS VARCHAR)  →  {type=Point, coordinates=[0.5, 0.25]}
```

which its parser refuses. Nothing converts an `ANY` to a geometry — every constructor takes a typed
input, and every `JSON_*` function takes JSON *text* and fails its runtime cast when handed a map.

Two repairs were tried and are recorded as wrong rather than missing. **Rendering every document value
as JSON** — giving the materialised map a `toString` that writes JSON — works, and changes the runtime
type of every map in every row to serve one corner; the cost is out of all proportion to what it buys.
**Supplying a better implementation of `ST_GEOMFROMGEOJSON`** does not work at all: `SqlUtil.lookupRoutine`
resolves across every chained operator table by parameter match, so Calcite's `VARCHAR` overload beats
an adapter's `ANY` one regardless of chain order. That finding is also why the geography operators
carry their own names instead of overloading Calcite's.

**What is in scope is what Cosmos evaluates** — `ST_DISTANCE`, `ST_WITHIN`, `ST_INTERSECTS` and
`ST_ISVALID` — with `ST_GEOG_DWITHIN` rendering as a distance comparison, Cosmos having no counterpart.

**A distance orders at the service, which is not the general rule.** `ORDER BY ST_DISTANCE(c.location,
<point>)` is accepted, and stays accepted under a `WHERE` and an `OFFSET … LIMIT`. That had to be
measured rather than assumed: an `ORDER BY` over a computed expression is refused with 400, error 2206,
*"ORDER BY item expression could not be mapped to a document path"* — recorded above, under the cast
column — so spatial is a special case in that clause rather than an instance of a rule. Nothing pushes
a sort over one now: `CosmosSort` writes the expression into the clause, and
`CosmosSortRule` admits the sort where the projection beneath it is that distance.

The expression is written **twice** — once selected, once ordered — because Cosmos cannot order by a
projection alias, which is the same reason every other sort here names a document path. So the
projection records the rendered text against its ordinal, in `CosmosImplementor.SortableExpressions`,
and the sort writes it out again. Only a geodesic distance is recorded there, because only it was
measured to be accepted; and it must be the whole collation, a second key beside it drawing the same
2206 a computed key draws alone.

**Two more push without being spatial calls at all.** A GeoJSON shape records its type as a `type`
member, so `ST_GEOG_GEOMETRYTYPE` over a stored geography is `c.location.type` — an ordinary property
read, no spatial function and no spatial index involved. The vocabularies agree for anything a
container can hold: JTS also spells `LinearRing`, which GeoJSON has no member for, so a stored shape
cannot be one. A geometry built inside the query has no path and is declined, which is also the case
where that difference could otherwise have appeared.

`ST_GEOG_ASGEOJSON` is the second, and it is a projection only. The document already holds the
GeoJSON, so parsing it into a geometry and writing it back out is a round trip the service never asked
for; the property is selected instead and read as the JSON the service sent — the same
`CosmosReading.Json` the `DOC` column takes, and for the same reason, the value being an object
where the projection is declared `VARCHAR`. In a *predicate* the call is declined, because the column
carries text where the path carries an object and a comparison against one is not a comparison against
the other.

**What is still out is the loose bound with a recheck above.** `CosmosFilterSplitRule` pushes a
weakened predicate and rechecks the original in process, which needs an in-process answer that agrees
with the service. The geography package computes one over S2, and nothing has measured whether it
agrees with Cosmos at a polygon edge, across the antimeridian, at the poles, or on a distance sitting
exactly on a threshold — a sphere and an ellipsoid differ by tenths of a percent, far more than enough
to disagree about a threshold, and a recheck that disagrees discards rows the service returned. Until
that measurement exists these push exactly or they do not push at all.

### What a table tells the planner

`getStatistic` reports **keys** derived from declared facts, and a **row count** where the service was
asked for one. It reports **no collations**, and that absence is the point.

A statistic's collations are the order a scan's rows *already arrive in* — `RelOptTableImpl`
hands them to `RelMdCollation` as the collation of the scan. Reporting a composite index there claims
a Cosmos scan comes back sorted, which licences the planner to drop a `Sort` asking for exactly that
order. It does not: Cosmos guarantees no order without an `ORDER BY`, whatever is indexed. This was
reported for a while, and a probe of `mq.collations(scan)` showed the planner being told a bare scan
was ordered by `(id, _ts)`.

What a composite index decides is whether a multi-key `ORDER BY` is **legal**, and that question
belongs to the rule that pushes the sort, where `CosmosContainerMetadata.IsSortSupported` answers it.
Calcite's own adapters agree: Cassandra keeps its clustering order on the table for its rules to read
and does not implement `getStatistic` at all — and a Cassandra clustering order genuinely *is* the
storage order, which a Cosmos composite index is not.

### Reading a value back

A row arrives as one JSON value and `CosmosJson` reads it into the representation Calcite holds that
SQL type in — **Java boxes, not CLR primitives**. A CLR `int` in a row compiles and then fails at the
first Calcite operator that casts it, a long way from where it was produced.

| JSON | as `ANY` / inside `MAP` | as a declared type |
| --- | --- | --- |
| string | `string` | `CHAR`, `VARCHAR` |
| number, whole | `java.lang.Long` | `TINYINT`…`BIGINT` as their boxes, `DECIMAL` from the raw digits |
| number, fractional | `java.lang.Double` | `REAL`, `FLOAT`, `DOUBLE` |
| `true` / `false` | `java.lang.Boolean` | `BOOLEAN` |
| object | `java.util.LinkedHashMap` | `MAP` |
| array | `java.util.ArrayList` | `ARRAY`, `MULTISET` |
| `null`, or absent | `null` | `null` |

Two choices worth stating. A whole number reads as a `Long` rather than a `Double` so that an
identifier or a count does not surface as `42.0`; the choice is the value's, there being no schema to
consult. And a document that disagrees with the declared type is **refused**, not coerced — reading
`42` as `VARCHAR` throws rather than yielding `"42"`, because a row type that bends to the data is a
suggestion rather than a declaration.

Nesting is not truncated: the document column carries the document and the value goes as deep as the
document does. Addressing is not limited either — `JSON_VALUE(c."DOC", '$.a.b.c')` folds the whole
path into one Cosmos path, `c.a.b.c`, array subscripts included. What depth costs is planner
metadata: nothing addressed this way has a type, a key or a collation, so it can be addressed but not
reasoned about. The one limit is that the path must be a literal — a path assembled at run time names
a property whose name is not known until the row is read, and is declined.

### The row model

Calcite has **no JSON type**. `SqlTypeName` in 1.41.0 has no `JSON` constant, and Calcite's
SQL/JSON functions (`JSON_VALUE`, `JSON_QUERY`, `JSON_EXISTS`, …) follow SQL:2016, where JSON is
*character data* — VARCHAR in, VARCHAR out, re-parsed per call. That is the wrong substrate.

What Calcite does have:

| Option | Availability | Assessment |
| --- | --- | --- |
| `MAP<VARCHAR, ANY>` + `ITEM` | Since forever | **Chosen, then removed.** The pattern the MongoDB and Elasticsearch adapters use for a `_MAP` column. It carried the adapter to parity and was dropped once the SQL/JSON spelling reached everything it reached — see *Why the map column went*. |
| SQL/JSON over `VARCHAR` | SQL:2016, honoured by Calcite | **Chosen.** JSON is character data here, which reads as the wrong substrate and is the right one: it is what the service already sent, and it is the only shape `JSON_SET` can be written over. |
| `VARIANT` | 1.41.0 (`SqlTypeName.VARIANT`, `org.apache.calcite.runtime.variant`, operators `VARIANT`/`VARIANTNULL`/`TYPEOF`) | Semantically the best fit — `item`, `cast`, `getTypeString`. No shipped adapter models a row type on it; planner pushdown through VARIANT is unproven. Revisit. |
| `DynamicRecordType` + `DYNAMIC_STAR` | Present in 1.41.0 | Nicer ergonomics (`c.name` rather than an accessor call), but nested paths fall back to field access on an `ANY` anyway. Worth evaluating as a surface layer, not as the substrate. |

#### Base: one document column

Every Cosmos query returns exactly one JSON value per row, so the base row type is **one column**,
not N. This is not a compromise — the two models agree exactly:

| Cosmos | Calcite |
| --- | --- |
| `SELECT VALUE c` | the single `DOC` column |
| `SELECT a, b` → `{a:…, b:…}` | a document, which is what the column already is |
| `c.address.city` | `JSON_VALUE(DOC, '$.address.city')` |
| `c["odd name"]` | `JSON_VALUE(DOC, '$[''odd name'']')` |

Accessor → path expression is close to 1:1, and the coercion of a flat select list into an object
stops being an impedance mismatch — it is the identity of the column.

#### Why the map column went, measured

A map cannot be addressed by Calcite's SQL/JSON functions, which are typed over character strings,
and that is what stood between an `UPDATE` and a targeted patch. `SET "_MAP"['a']['b'] = …` did not
parse — the `UPDATE` grammar accepts only `=` or `.` after the target. `SET "_MAP"."a"."b" = …`
parses and the validator refuses it, *Unknown target column*, a map having no fields.
`SET "_MAP" = JSON_SET("_MAP", '$.a.b', …)` converts in isolation and dies through a connection,
`JSON_SET` returning `VARCHAR` where the column is `(VARCHAR, ANY) MAP` and Calcite being unable to
build a cast spec for it — *Unsupported type when convertTypeToSpec: ANY*. A source expression
already *of* the map type converts and plans (measured with Spark's `MAP_CONCAT`), so the obstacle is
the substrate mismatch and nothing else.

Nor is there a Cosmos-side way round it. Measured against an account: no JSON-transform function
under any of a dozen names, no object spread, no `ArrayToObject` to undo `ObjectToArray`, and **no
`UPDATE` statement at all** — *SC1001, syntax error near 'UPDATE'*. Cosmos SQL is read-only; the
targeted write is `PatchItemAsync`.

Two things make this less alarming than it reads. `TableModify` has no implementation in the CLR
conventions, so an `UPDATE` the adapter declines fails to plan rather than evaluating in memory and
writing a re-serialised document. And `JSON_VALUE` carries SQL:2016's `RETURNING` clause, which
Calcite honours — `RETURNING INTEGER` types the call `INTEGER`, `RETURNING TIMESTAMP` types it
`TIMESTAMP(0)`, nullable, and the clause survives inside a view, where it presents to a
`DbDataReader` as a real typed column. So the JSON family is not uniformly stringly typed, and a
document path *can* be given a SQL type by the caller in standard SQL. So the map column could address a document and could not be *written* through in any shape a rule
could read a patch operation off. The document column can be, which is the whole reason the row model
moved: `SET "DOC" = JSON_SET(c."DOC", '$.a.b', v)` is an expression over a value the accessor family
is typed for.

Two things then made the map column redundant rather than merely second. Parity was one function
(below), so it addressed nothing the accessor did not. And `UNNEST` — the last thing the map reached
that the accessor did not, `ITEM` over a map being `ANY` where `JSON_QUERY` is `VARCHAR` — is reached
by `StringToArray(JSON_QUERY(c."DOC", '$.tags'))`, `StringToArray` being typed `ANY`; both calls
resolve to the path and neither survives into the statement.

#### The document column

`DOC` is the document, and the only column a statement writes. First in the row type; `VARCHAR` and
`NOT NULL`, because every row is a document; `DEFAULT` in the column strategies, every other column
being `STORED`.

Read, it is the JSON the service sent —
`GetRawText`, the original span — so it is a copy rather than a round trip and cannot differ from what
is stored in key order or number formatting. `CosmosReading.Json` is what says so, distinct from
`Text`, which renders a value the way a cast over `ANY` would and is the wrong answer for a document.

**Parity was one function, not sixty.** Every pushdown that needs a document path resolves it through
`CosmosRexTranslator.TryResolvePath`, so `JSON_VALUE(<doc>, '$.a.b')` resolving to the same path as
`ITEM(ITEM(<doc>,'a'),'b')` gave every one of them the second spelling at once. Measured against a
connection, the two produced the identical plan for a projection, a filter, a sort, a sort with a
fetch, `GROUP BY`, `DISTINCT`, a nested path, a bracketed name, an array subscript, a numeric
comparison, `IS NOT NULL`, `UNNEST` and a lookup join on either side; a path assembled at run time
declined on both. That measurement is what made removing the map column a rename rather than a
redesign.

**A projection of an accessor is guarded, and that is not decoration.** `JSON_VALUE` answers a
scalar's text and null for an object or an array. Pushed as the bare path it returns the raw value,
which the reader refuses where the plan declared `VARCHAR` — a number is not a string — so the
statement threw for data the in-process plan renders as `30`. `IS_PRIMITIVE` is exactly SQL/JSON's
line, true of a string, a number, a boolean and a JSON null and false of an object, an array and an
absent property, so `(IS_PRIMITIVE(p) ? p : null)` with the `Text` reading means at the service what
the function means in process, for every JSON type. A filter needs no guard: a comparison against an
object is false at the service and null in process, and both drop the row.

**The gates that admit a document value ask for one, not for a type.** Three of them — the text cast
and the numeric cast in a comparison, and the rendering in a projection — spelled *is this a value
out of a document* as *is this typed `ANY`*. That was the same question while the map column was the
only way in. It stopped being so silently: a bound the filter rule would have pushed was not pushed,
which reads as a slower plan rather than a wrong one. `IsDocumentValue` asks the question the gates
mean.

One shape was not on that list and did not hold: the cast to text a view writes, which was keyed off
the operand being typed `ANY` and so off the map subscript rather than off the path (#71). It is now
dropped over either spelling — see *The same cast over `JSON_VALUE` is the same cast* under *Casts
over document values* — with the one asymmetry that the same cast in a projection is rendered over
`_MAP` and not over `_JSON`, for the reason recorded there.

The path argument must be a literal, and the grammar is `$` with `.name`, `['name']` and `[0]` steps —
a wildcard, a descent or a filter has no Cosmos rendering and is refused. `RETURNING` is not rendered:
it told the plan what the service will return, and a clause that disagrees with the document fails in
materialisation rather than answering wrongly, which is what makes it worth trusting. `UNNEST` wants
`RETURNING <type> ARRAY`; `JSON_QUERY` is `VARCHAR` even `WITH ARRAY WRAPPER` and is never an unnest
source.

That is also what Calcite does with it in process, and it is worth knowing how literally. Measured
against Calcite's runtime, `RETURNING` a type other than text performs no conversion at all: the
scalar is cast to the declared Java class and anything else throws. `RETURNING INTEGER` returns a
stored `30` and throws on `30.7`, on `"30"`, on `true` and on `3000000000`, which parses as a long;
`RETURNING DOUBLE` returns `30.0` and throws on `30`, which parses as an integer, and `RETURNING
BIGINT` throws on `30` for the same reason; `NULL ON ERROR` does not catch any of it, and
`DECIMAL(3, 1)` returns `30.75` unrounded. Only the text default converts. So a comparison through a
numeric `RETURNING` pushes exactly, as it always has: for every document Calcite can evaluate, the
declared type is the stored type and the service compares the same value. What the pushdown changes
is the document Calcite would have thrown on, which the service excludes instead — the caller's
declaration held rather than checked, the same asymmetry the reading side already accepts. It is why
the numeric bound the map column's cast needs has no counterpart here.

#### Promoted columns

A single column has one field ordinal, and Calcite's planner metadata is ordinal-based:
`Statistic` exposes `getKeys`, `getCollations`, `getDistribution`, `getReferentialConstraints`,
and `getRowCount`, with keys and collations expressed over field ordinals. With only `DOC`,
none of it is expressible — which is why the Mongo and Elasticsearch adapters supply no
statistics at all.

**This is the whole of why they exist.** They are not a second way to address the document; the
document column already addresses all of it. They carry ordinals for the facts the container states,
and nothing else, which is why they are read-only: a write goes through `DOC`.

A declared path is named for the JSON path it addresses — `/inventory/sku` promotes as
`$.inventory.sku` — so a nested one has a name at all. Under the older rule, which took a path's last
segment, it had none and a container keyed on one reported *no unique key*. The service's own keep
the names the service gives them.

The container metadata table above is exactly the material those methods want, so the row type
is `DOC` **plus promoted scalar columns** for paths that are declared or service-guaranteed:

| Promoted column | Type | Enables |
| --- | --- | --- |
| `id` | `VARCHAR NOT NULL` | `getKeys` (with partition key) |
| `_ts` | `BIGINT` | A genuinely typed timestamp |
| `_etag` | `VARCHAR` | Optimistic concurrency |
| Partition key path(s) | declared | `getDistribution`; single-partition detection |
| Composite index paths | declared | `getCollations`; `CosmosSortRule` legality |
| Computed properties | declared | Named projections |

**Only declared or guaranteed paths may be promoted. Never a sampled one.** Sampling a
container to guess its shape is fine as an opt-in convenience for projection ergonomics, but it
must never feed `Statistic` — an inferred key or collation that is wrong produces a silently
incorrect plan, not a slow one.

#### Residual type problems

- **No date/time type.** Cosmos JSON has six types — `undefined`, `null`, boolean, number,
  string, array, object. Dates are ISO 8601 strings or epoch numbers by application convention,
  and nothing declares which. `_ts` is the sole exception (epoch seconds, service-defined).
  Temporal predicates on user paths are only pushable once the encoding is declared in the
  model; otherwise decline.
- **`undefined` ≠ `null`.** A missing property and a null-valued property are distinct in
  Cosmos. In the map model this is representable — the key is absent versus present-and-null —
  which is strictly better than collapsing both to SQL `NULL`. Predicates distinguishing them
  translate to `IS_DEFINED`. Promoted columns *do* lose the distinction; that is the price of
  promotion and applies only to paths whose presence is guaranteed anyway.

  **Where a statement has to answer as SQL does, the distinction is spent rather than kept**, and
  the two places that came to are worth naming because both were shipping wrong answers:

  - **A comparison against a null.** SQL's is unknown, and a row is kept only where the predicate is
    true — so an unknown discards the row in a positive position and, since negating an unknown
    leaves it unknown, in a negated one too. The service is two-valued here: its `=` over a null is
    false, which matches; its `!=` is true, which does not; and under a `NOT` the two swap. So the
    position is tracked and a guard emitted in whichever one needs it — see
    `CosmosRexTranslator.WriteComparison`. Tracking the position rather than guarding the negation is
    what makes it compose: `NOT (x = 1 AND y = 2)` keeps its row where `x` is null and `y` is not 2,
    and a guard around the whole negation discarded it.
  - **A grouping key.** The service groups an absent property apart from a present-and-null one, and
    SQL has one `NULL`. The key is therefore grouped and projected as `IS_DEFINED(p) ? p : null`,
    which is SQL's reading of both — except for `id`, `_ts` and `_etag`, which the service guarantees
    are present, where normalising would buy nothing and cost the plain path form an index is defined
    on. See `CosmosAggregate.GroupingKey`.
- **Heterogeneous types per path.** The same path may be a string in one item and a number in
  the next. `ANY` absorbs this; a promoted column does not, which is a second reason promotion
  is restricted to declared paths.

---

## Planned Project Layout

```
src/
  Apache.Calcite.Cosmos.Adapter/
    CosmosConvention.cs               ✔ Per-container calling convention
    CosmosImplementor.cs              ✔ Mutable SQL accumulator
    CosmosRules.cs                    ✔ Rule set for a convention instance
    CosmosSchema.cs                   ✔ Calcite Schema over a database
    CosmosTable.cs                    ✔ Calcite Table over a container; Statistic
    CosmosSchemaFactory.cs            ✔ SchemaFactory for JSON model registration
    CosmosColumnStrategies.cs         ✔ Which columns an INSERT may omit, and which it may not name
    Client/
      CosmosQueryExecutor.cs          ✔ Executes a rendered statement via the Cosmos SDK; writes items
      CosmosSequences.cs              ✔ The IAsyncEnumerable a compiled plan reads rows from, and writes through
      CosmosJson.cs                   ✔ JSON value → the representation Calcite holds a value in
      CosmosDocument.cs               ✔ The reverse: a row → the JSON document it describes
      CosmosWrite.cs                  ✔ What a write does, decided while the plan is built
      ICosmosItemWriter.cs            ✔ Creating and deleting documents
      CosmosSchemas.cs                ✔ Resolves the table's executor from the DataContext
      CosmosExecutionException.cs     ✔ The plan cannot reach what would execute it
      CosmosMaterializationException.cs ✔ A document does not hold what the query assumed
    Metadata/
      CosmosCompositeIndex.cs         ✔ Composite index and sort-key matching
      CosmosContainerMetadata.cs      ✔ Declared container facts; sort legality
      CosmosContainerMetadataReader.cs ✔ ContainerProperties → CosmosContainerMetadata
    Rel/
      CosmosRel.cs                    ✔ Implement contract
      CosmosTableScan.cs              ✔
      CosmosFilter.cs                 ✔
      CosmosProject.cs                ✔
      CosmosSort.cs                   ✔
      CosmosUnnest.cs                 ✔
      CosmosAggregate.cs              ✔
      CosmosRank.cs                   ✔ ORDER BY RANK, which subsumes the projection
      CosmosLookupJoin.cs             ✔ Fetches only the documents another side's keys could match
      CosmosTableModify.cs            ✔ INSERT and DELETE; not in the convention, having no statement
      Convert/                        ✔ One converter rule per node, and the one way out
    Sql/
      CosmosSql.cs                    ✔ Lexical primitives: identifiers, paths, JSON literals
      CosmosPath.cs                   ✔ Immutable property path rooted at a FROM alias
      CosmosParameterList.cs          ✔ @pN binding
      CosmosQueryBuilder.cs           ✔ Statement assembly and language-constraint enforcement
      CosmosRexTranslator.cs          ✔ RexNode → Cosmos scalar expression
      CosmosTranslationException.cs   ✔ Refusal signal
    Internal/
      BigDecimalConverter.cs          ✔ Lossless BigDecimal → decimal
  Apache.Calcite.Cosmos.Adapter.Tests/
```

✔ marks what exists today. The `Sql/` layer is deliberately free of any dependency on the
convention or on the CLR conventions in `calcite-dotnet`, which is what let it be completed and
tested ahead of them, and is why it remains testable without one.

---

## Leaving the Convention

A subtree of Cosmos nodes is a statement, not rows. `CosmosToClrEnumerableConverter` is where
it becomes rows: it renders the statement, executes it, and reads the JSON value each row arrives
as into the row the plan above expects.

**The read is asynchronous, and only asynchronous — but the plan no longer is.** The v3 Cosmos SDK
has no synchronous data-plane API: a page arrives only by awaiting `FeedIterator.ReadNextAsync`. So
`ImplementAsync` is the converter's real body, and `Implement` is written as the delegation through
`ClrEnumerableRelImplementor.Pulled` that `ClrEnumerableRel` prescribes for exactly this case — an
adapter whose client is asynchronous.

**What changed, and it is the part worth recording.** While the pulled and awaiting conventions were
two, the adapter published no converter into the pulled one, and that absence was a gate: a query
over a Cosmos table would not plan at all unless the root was asked for asynchronously, so
sync-over-async could not appear in a plan because the plan did not exist. Calcite-dotnet merged the
two conventions, and a plan no longer carries a mode at all — the same plan is read either way, and
the kind is chosen by whoever calls the root. The gate is therefore gone, and there is nothing here
to put it back with: declining in `Implement` would move the refusal from plan time to execution
time, which is strictly worse — the same query, failing later and with less to say about why.

So the cost moved rather than vanished. **A host that reads a Cosmos plan through `ImplementRoot`
rather than `ImplementRootAsync` blocks a thread per row**, at the leaf where it is worst, and
nothing in the plan will warn it. That is a documented cost now instead of a planning failure, and
the README says so where a host will read it.

Three things follow from the row being one JSON value:

- **The result is always an object keyed by output field name.** A bare scan would otherwise render
  `SELECT VALUE c` and hand back the document itself, giving two row shapes for the materializer to
  tell apart. The converter projects the scan's own path bindings when nothing above it has
  projected, so `SELECT VALUE { … }` is the only shape that reaches the reader.
- **Fields are read by name, not position.** A Cosmos object constructor omits a property whose
  value is undefined, so the properties present in a row are a subset of the output fields.
- **A missing property and a null one are both SQL `NULL`.** Nothing in the row model can
  distinguish them, and SQL has no third value to distinguish them with.

A rendered statement carries one execution hint beyond its text. `OFFSET n LIMIT m` and `TOP n` bound
how many rows the statement can return, so `CosmosQuery.MaxItemCount` carries `n + m` (or `n`) and the
executor asks the service for pages that size. It is a page size, not a limit — it cannot change which
rows come back, only how many arrive per round trip — and without it a statement ending in `LIMIT 5`
fetches a full default page and pays for the rows it discards. An offset alone bounds nothing and asks
for nothing.

### Not every statement is a query

A lookup by `id` and a complete partition key is a **point read**: `ReadItem`, about 1 RU, no query
engine, against the 2.3 RU a query costs at best. `CosmosQuery.PointReadId` carries the `id` when the
statement is one, and the executor reads instead of querying.

**A point read applies no predicate**, and that governs when it is offered. Under
`WHERE id = 'x' AND pk = 'y' AND price > 100` a read would return a document the query excludes — a
wrong answer, not a slow one. So every top-level conjunct must be one of the equalities pinning `id`
or a partition key path, and all of them must be pinned. `CosmosPartitionKeyExtractor` answers that
question in the direction its name does not suggest: `Collect` records what a predicate pins and
ignores the rest, and `CoversExactly` asks whether there *is* a rest.

The rest of the statement rules it out just as firmly. A read returns one document, whole, so an
ordering, a row limit, a grouping and an array traversal each describe something a document is not.
`CosmosImplementor.Build` withholds the read for any of them.

**The projection is the interesting one.** A read returns the document; the statement would have
returned `SELECT VALUE { … }`. Two row shapes again — and this time the answer is not to force one,
because forcing the projected shape is exactly the query the read is avoiding. Instead the converter
builds a second row builder that walks each output field's *path* in the returned document, `DOC`
being the empty path. It can only do that where every output field addresses a path, so a computed
projection withdraws the read and the statement executes as the query it already is.

What executes the statement is *not* written into the plan. Calcite prepares a statement once and
executes it many times, so the plan holds the table's qualified name and `CosmosSchemas.GetExecutor`
walks it from the `DataContext`'s root schema on each run. A live `CosmosClient` compiled into the
expression tree would bind that plan to whichever schema instance happened to be current when it
was compiled. This is the same discipline as an adapter reaching its data source through
`Schemas.unwrap` over a convention's schema expression; it is spelled out here because the plan is a
`System.Linq.Expressions` tree and calls into managed code rather than carrying a linq4j expression.

A `CosmosTable` may hold no executor at all, which is what a table built from container metadata
alone is. Planning is unaffected — nothing about a statement or its cost depends on who runs it —
and enumerating such a plan is what fails, saying so. Most of the test suite plans against tables in
exactly that state.

---

## Writing

**Cosmos SQL has no DML, and that is not a reason the adapter cannot write.** The query language has
no `INSERT`, `UPDATE` or `DELETE`, but the SDK has item CRUD, and Calcite expresses a write as a
`TableModify` node consuming rows rather than as generated SQL. So the write path shares nothing with
the read path below the plan: no implementor, no statement, no `CosmosQuery`. Nothing here renders
text.

Which settles where the node lives. A subtree in `CosmosConvention` *is* a statement, and a write is
not one, so `CosmosTableModify` is in `ClrEnumerableConvention` — a node whose input is rows and
whose effect is a sequence of SDK calls. That makes it the same shape as `CosmosLookupJoin`: a node
that knows about a container without being inside the convention that renders one.

Two consequences worth stating because neither is obvious.

**The Clr convention has no modify node**, so this is the first. Calcite's own
`EnumerableTableModify` is not a model to copy: it writes through
`ModifiableTable.getModifiableCollection()`, calling `Collection.add` and `Collection.remove` on
whatever the table hands back. For Cosmos that collection would have to block on `CreateItemAsync`
per element — and it would block wherever the plan happened to put it, with nothing in the signature
saying a write was waiting on a round trip. The awaiting body keeps that in one place instead, at the
node boundary a caller asked for.

**`ModifiableTable` is therefore not implemented, and is not needed.** Measured:
`SqlToRelConverter.createModify` falls back to `LogicalTableModify.create` when the target unwraps to
no `ModifiableTable`, and `DELETE` and `UPDATE` plan through that fallback unchanged. A rule matching
`LogicalTableModify` over a Cosmos table is the whole entry point.

### What an insert writes

The row model settles this. A document *is* the document column, and every other column is a path
within the same document, so an insert naming more than one of them would be describing one document
twice over and asking the adapter to reconcile it.

**The document column is the only column a statement writes.** Everything else is `STORED`, which
makes naming one a validation error rather than something the adapter drops later without comment:

```sql
INSERT INTO products ("DOC") VALUES ('{"id":"1","category":"bikes","name":"Trail Blazer"}')
```

**Writing a projection was possible and is refused on purpose.** A derived column addresses a path
inside the document, so writing one means building that path into the document being written — for a
nested declaration, several levels of it — and the document column already says all of that,
unambiguously. `id` could have stayed writable, being a property in its own right, and did not: a
rule with one exception is a rule a reader has to remember.

**It is also what the patch tier needs.** A targeted change has to arrive as an expression over the
document — `SET "DOC" = JSON_SET(c."DOC", '$.category', 'x')` — because that is the form a rule can
read a patch operation off. A `SET` of a projected column carries the same intent in a shape nothing
can decompose. See *Updating* below.

**An `UPDATE` is refused in the rule rather than by the validator.** Measured: Calcite checks
`ColumnStrategy` for an `INSERT` and not for a `SET`, so a `SET` of a `STORED` column plans. The
value would then be carried into a replacement that discards it and the statement would report rows
affected while changing nothing — a silent no-op write, which is worse than a plan that fails.
`CosmosTableModifyRule` requires the set list to name the document column and nothing else.

**The service's own properties are stripped wherever they appear.** `_ts`, `_etag`, `_rid`, `_self`
and `_attachments` cannot be named — they are `STORED` like everything else — but they arrive
*inside* the document column whenever it came from a scan, which is what
`INSERT INTO t ("DOC") SELECT "DOC" FROM t2` hands over: a whole document, system properties and all.
Copying one document to another is the obvious use of that statement, and the service's bookkeeping is
not part of what is being copied.

**This is a decision rather than a requirement, and it was measured.** With the stripping removed, a
document carrying a bogus `_ts`, `_etag` and `_rid` was still accepted and still came back with values
the service had assigned — so the service does not need protecting from these. What stripping buys is
that the document written is the document described: a new item does not silently carry another item's
identity, and a caller reading back what they inserted is not told a value they never supplied was
theirs.

**Building the document is a copy.** Parse, write every property but those five, in the order they
arrived. Nothing is read and re-rendered, so a large integer keeps its digits and an exponential its
notation. The value-by-value writer this needed while a row could hand over a Java box or a CLR
primitive for every JSON type went with the map column that produced them.

> **`STORED`, not `VIRTUAL`, and the difference is not cosmetic.** Both refuse a write. `VIRTUAL`
> additionally means *not stored*: measured, it makes `RelOptTableImpl.toRel` drop the column from the
> scan and project a literal null in its place, so `SELECT _ts` returns nothing the service holds.
> Forty tests failed on it at once. The row type is identical either way, which is why the regression
> guard asserts the *plan* rather than the type.

**Nothing invents an `id`.** A document reaching the service without one is the service's business to
accept or refuse, and guessing here would make the adapter the author of a key the caller did not
choose.

**The service's own properties are stripped from the document, wherever in it they appear.** `_ts`
cannot be *named*, but it arrives inside the map column whenever one document is copied to another —
which is what `INSERT INTO t ("DOC") SELECT "DOC" FROM t2` hands over, and the obvious use of that
statement. **Measured, and this is a decision rather than a requirement:** with the stripping removed
a document carrying a bogus `_ts`, `_etag` and `_rid` was still accepted, and still came back with
values the service had assigned. What stripping buys is that the document written is the document
described — a new item does not silently carry another item's identity.

### A map literal cannot be inserted

`INSERT INTO t ("DOC") VALUES (MAP['id', 'x'])` fails in the validator, and the limitation is Calcite's:

```
java.lang.UnsupportedOperationException: Unsupported type when convertTypeToSpec: ANY
```

Implicit coercion casts the source row to the target row type, and building a `SqlDataTypeSpec` for
`MAP<VARCHAR, ANY>` is unimplemented. An explicit `CAST` fails identically, for the same reason.

Recorded rather than worked around, because the shape that does work is the more useful one: a source
column already typed `MAP<VARCHAR, ANY>` needs no coercion at all, and a scan of another container is
exactly that. Copying documents between containers — the case the map column exists for — is
unaffected.

### Why the table declares column strategies

`INSERT` does not reach a rule without this, and the failure is at validation:

```
Column 'DOC' has no default value and does not allow NULLs
```

`SqlValidatorImpl.checkFieldCount` requires every column that is neither nullable nor defaulted to be
supplied. `DOC` is `NOT NULL` and that is true — every row is a document. The row type is not going to
be weakened to admit a write; a row type that bends to what a caller wants to omit is a suggestion
rather than a declaration.

`ColumnStrategy` says the right thing instead, separating *not null in the table* from *optional in an
insert*. `CosmosTable` supplies an `InitializerExpressionFactory` reporting `DEFAULT` for `DOC` and
`STORED` for everything else.

The columns a modify's input carries are unaffected by any of this: the input row type is always the
table's whole row type, with the omitted columns as typed null literals. Only the *arity* an `INSERT`
without a column list expects changes, which is now one.

### Two rules are not bound to a convention, and neither can be

Every other rule here is created per `CosmosConvention`, because a convention is bound to a container
and some rules must consult that container's metadata. The write rule and the lookup join rule
cannot be, and the reason is a property of `ConverterRule`: its description is derived from the
traits it converts between. Both convert `NONE` to `CLR_ASYNC_ENUMERABLE`, neither of which names a
container, so every per-container instance carries the same description — and rules compare by
description, so a planner given two containers' rule sets keeps one and discards the rest.

**Measured twice, and the second measurement sharpened the first.** With a second container's rules
registered, an insert into the first stopped planning entirely: the surviving write-rule instance
was checking for the wrong convention and declined. The lookup join then showed why its own tests
had never caught the same defect — `addRule` does reject the later duplicates, but rejection does
not decide matching: in every measured plan the predicate that ran belonged to an instance `addRule`
had rejected, the one most recently constructed at the rule's *first* firing, and that instance
stayed bound for the rest of the run. `CosmosConvention.register` rebuilds the rule set per
convention and a join's inputs register left before right, so at a lone join's first match the
freshest instance is its own probe side's. Two containers therefore passed in either orientation by
an accident of registration order that a third container ends: the instance bound at the first join
judged the second join too, declined it, and that container was read whole, through a hash join,
silently.

The consequence for both rules is the same: an unbound rule is correct only if every instance is
interchangeable, so their predicates capture nothing and read everything from the matched node —
the container being named by the modify, or found beneath the join's probe side. There was nothing
for the binding to do anyway.

### Deleting, and reading first

`DeleteItemAsync` takes an `id` and a partition key, so a delete needs both per row, and a `WHERE`
clause that pins neither has to read the rows before it can delete them.

**Read-then-delete is allowed rather than refused, because the plan shows it.** The scan feeding the
modify is right there in the tree, and refusing would make `DELETE … WHERE price > 100` impossible
rather than expensive — a container is not less deletable for lacking a predicate over its key. Where
the predicate does pin `id` and a complete partition key the scan is already a point read, so the
cheap case falls out of work that is done.

What this does *not* do is the whole-partition case: a predicate pinning only the partition key could
be `DeleteAllItemsByPartitionKeyStreamAsync`, which is not a query at all. That is
`SupportsDeletePushDown` in the Flink table and is not attempted here.

**A delete needs the partition key as a value, and the promoted columns are where it comes from.** A
nested partition key path is not promoted, so it is read out of the map column instead; a container
whose key is nested is not therefore undeletable.

One more thing the rule must do, which nothing else here has needed: **simplify the input's trait
set.** A `Values` node advertises several collations at once — every ordering a single row trivially
satisfies — and asking such a trait set for its one collation throws. An `INSERT` whose source is
`VALUES` is the first statement anyone writes, so without it the rule fails immediately; a `DELETE`
never shows it, its input being a scan, which claims no collation at all.

### Updating

**SQL fixes the *what*; the adapter owns the *how*.** An `UPDATE` assigns whole values to named
columns, computed from the old row — there is no sub-path assignment in the grammar — and the
planner hands over the finished story: `updateColumnList`, `sourceExpressionList`, and the scanned
rows to evaluate them against. Any execution that lands that result is legitimate, chosen by cost.
That gives a ladder:

1. **Replace — implemented.** `SET "DOC" = …` is whole-document assignment by its own words, and
   `ReplaceItemAsync` is that operation, priced as what it is. Not a stand-in for a patch: when the
   named column is the document, replacing the document is the faithful reading. The read this
   requires is the scan the plan already shows — the same argument recorded for deleting — and
   where the predicate pins `id` and a complete partition key that scan is already a point read.
2. **Patch for targeted `SET`s — waits on there being a target.** A `SET` of a plain document
   property is `PatchItemAsync`'s native input, far cheaper than a replace. But no such column
   exists: the row model's columns are all identity, placement, service bookkeeping, or the document
   itself (the enumeration below), and a path *inside* the document has no column to be named by. A
   `columns` operand promoting caller-declared, typed paths was built for this, dropped, and is now
   rejected outright (`TODO.md` section 6), so the target cannot arrive by declaration. It has to
   arrive by expression instead, which is (3).
3. **Static decomposition — future.** A mutation operator in the Cosmos table (`JSON_SET`-style,
   the way JSON-column databases spell copy-and-modify) would let a rule read patch operations
   straight off a `SET "DOC" = JSON_SET(…)` expression at plan time — which the document column now
   admits, being `VARCHAR` and writable, where the map column admitted no such expression at all.
4. **Optimizations, recorded not built.** A runtime diff of old against new document into patch
   operations is only equivalent to a replace under `If-Match`, and is bounded by the ten-operation
   patch limit; a *blind* patch — no read at all — is possible exactly when the predicate pins
   `id` plus the full key and every `SET` value is a literal.

**What a replace refuses, by enumeration.** `SET "id"` renames identity and `SET` of a partition
key path changes placement; the service forbids both on an existing document, so both are declined
at planning — a plan that fails once, rather than a request that fails per row. Honouring a
placement change would be a delete and a create, which is a different statement. `_ts` and `_etag`
are declared `STORED`, so the validator refuses them before any rule runs. The map column may still
*carry* a different identity or placement inside its value — invisible at plan time, and the
service rejects the resulting request loudly, which is the correct fate for it.

**Building the replacement document.** The row's table columns hold what the scan read; the `SET`
values trail them. The old values identify the target — `id` and the partition key are read out of
the document they describe, as a delete's are. For the body, the old promoted values are *withheld*
rather than copied when the map is being set: the document builder lets a non-null promoted column
override the map's entry, which is right for an insert and would here silently write old values
over whatever the new map says. `id` is the one exception kept, so a new map that omits it still
describes the same document, while one that contradicts it fails loudly at the service.

Two decisions the patch tier inherits when it lands:

- **`SET x = NULL` writes a JSON null** rather than removing the property. An `INSERT` skips null
  promoted columns because an *unmentioned* column arrives as null; an `UPDATE`'s
  `updateColumnList` names exactly what the statement wrote, so its null is explicit and is
  written.
- **No `If-Match`.** No write sends an ETag, matching `DELETE`: the read informs rather than
  locks, last write wins, and optimistic concurrency is a session-level surface this adapter does
  not invent. Under that stance a replace is the honest reading of `SET "DOC"` — the document
  becomes what was computed from what was read.

---

## The lookup join's caches

Two caches with two jobs, after Flink's `LookupOptions`, whose names these deliberately echo.

**Within one execution** (built in from the start): a bounded map of built rows, keyed by the join
key, filled to its bound and never evicted — nothing knows which key is worth keeping, so the simple
rule is the honest one — remembering absence too, since a key the container has nothing for is the
case a cache most needs to hold. It answers for no staleness the join did not already have, which is
why it needs no configuration and is always on.

**Across executions** (`lookupCacheMaxRows` and `lookupCacheExpireSeconds`, off unless both are
given): reference data is looked up repeatedly by different queries, and a remembered answer costs
no request units at all. Its decisions:

- **The schema owns it, one instance per container, and the model states the freshness policy.**
  The earlier objection — two connections disagreeing about freshness — dissolves once the policy is
  an operand: connections sharing a schema share its declaration, the way they share its containers.
- **Entries are JSON rows keyed by statement and key, not built rows.** A plan's row builder is the
  plan's own; caching beneath it makes an entry serve every plan that renders the same statement,
  and the statement identity includes the non-key parameter values, so two filters over the same
  shape cannot cross. Rows are rebuilt from JSON per execution, which is the price of sharing.
- **Expire-after-write, and expiry is the only eviction.** A full cache purges what has expired and
  otherwise declines new entries — the same fill-to-bound honesty as the inner cache, with the TTL
  providing turnover. The bound counts rows, with an absence entry counting as one.
- **Half a configuration is a model error.** `lookupCacheMaxRows` without
  `lookupCacheExpireSeconds`, or the reverse, is refused: a cache without a bound or without a
  freshness policy is not something to guess into existence.
- **A write through the adapter clears the container's cache.** `INSERT`, `DELETE` and `UPDATE` all
  go through the same tables the cache hangs off, and goodwill is cheap there. A write from outside
  the process is the TTL's problem, and saying so is the point of requiring one.

---

## Differential testing

Every pushdown is checked against an oracle rather than an expected string: the same SQL is planned
twice — once with the full Cosmos rule set, once with only the way-out converter registered, so the
scan is read whole and Calcite evaluates everything in process — and both plans execute against the
same live container. Equal rows or a defect; there is no third outcome to hide in.

- **The oracle is the adapter's own minimal mode, not a second engine.** The in-process side
  exercises the same row builder, so a mismatch indicts the pushdown, not the plumbing around it.
- **The rules have to be excluded, not merely left unregistered — measured.** A convention registers
  its own rules: `Convention.register` is called by a Volcano planner the first time it sees a node
  carrying one, and a scan arrives already in the Cosmos convention. Building the planner with only
  the way out therefore withheld nothing, and every statement in the corpus was compared against
  itself. It passed for as long as it existed and measured nothing at all. Removing the rules again
  does not work either: the planner queues a rule's matches when the root is registered, so by the
  first moment the rules provably exist their matches are already waiting. `setRuleDescExclusionFilter`
  is read when a match fires rather than when it is queued, and is set before either.

  What the repaired oracle found on its first run was five defects, none of them new and none of them
  observable before: `NOT` over a null-valued property, `GROUP BY` and `DISTINCT` over a path that is
  null in one document and absent in another, and an `ARRAY_SLICE` origin adjustment the corpus was
  written to catch and could not. All five are fixed and their statements are in the corpus.
  `Divergences` is empty, which is the state it should be found in.

  Widening the corpus afterwards found a sixth the same way: an **array subscript** was passed to the
  service unchanged, and SQL counts from one where Cosmos counts from zero — so `tags[0]` returned the
  first element where SQL returns nothing, and every subscript after it named its predecessor. There
  had never been a statement in the corpus that subscripted an array. The two origin bugs were
  independent of each other and neither implied the other, which is the argument for sweeping rather
  than reasoning: a second sweep over ordering, aggregation and row restriction found nothing, and
  that is worth as much as the six.
- **Rows are compared canonically, as multisets unless the statement orders.** Values are reduced
  to a canonical text — numbers through double, documents with sorted keys — because the two sides
  may box a computed value differently while meaning the same thing, and a map's entry order means
  nothing.
- **Known divergences are recorded and asserted, not excluded.** A statement the pushdown answers
  differently moves into `Divergences` with what makes it differ, and a second test requires that it
  still differs — so a divergence that closes fails the suite and is meant to be promoted back into
  the corpus rather than sit there looking settled. Nothing belongs there as a decision. A statement
  with no oracle at all — the array traversal, whose unpushed form has no implementation in the
  CLR convention — is listed separately with the reason, and is at least required to run.
- **The corpus leans into the semantics that have bitten**: null against absent, `NOT` over both,
  grouping by a key some documents lack, `LIKE`'s shapes, and the aggregate forms. It needs the
  emulator and reports inconclusive without one, like every test that needs a service.

---

## Design Constraints

- **Generate only what Cosmos accepts.** Declining to push down is always correct; emitting a
  statement the service rejects is not. Every rule and every expression translation must have a
  refusal path.
- **Planner metadata comes only from declared facts.** `Statistic` may be populated from the
  container definition and indexing policy, never from sampled documents. A wrong key or
  collation yields an incorrect plan, not a slow one.
- **Rule legality can depend on container metadata.** `CosmosSortRule` consults the indexing
  policy. This is expected, not a leak.
- **No relational joins.** Not now, not behind a flag. The grammar has no join predicate.
- **One container per convention instance.** Cross-container work happens above the convention
  boundary, in Calcite.
- **No ADO.NET or JDBC dependency.** Execution goes through the Cosmos SDK.
- **SDK types stay at the edges.** `Microsoft.Azure.Cosmos` appears only in `Client/` and in the
  metadata reader. Planning, translation, and statement assembly are independent of the service,
  which is what lets the bulk of the suite run with no client, no emulator, and no network.
- **Parameterize rather than interpolate.** Literals that could carry user data bind as `@pN`.
- **Targeting.** The adapter targets .NET 8 (C# 12); tests target .NET 8 and .NET 10.

---

## Calcite's JDBC entry points under IKVM

`Frameworks.getPlanner` and `RelBuilder.create` open an internal Calcite JDBC connection. Under
IKVM that fails:

```
java.lang.RuntimeException: Error loading factory org.apache.calcite.jdbc.CalciteJdbc41Factory
 ---> java.lang.ClassNotFoundException: org.apache.calcite.jdbc.CalciteJdbc41Factory
```

The class is present and loadable — `Class.forName` on it from adapter code succeeds. The cause
is that **IKVM gives each assembly its own class loader**, where a JVM has one flat classpath.
Avatica's `UnregisteredDriver` resolves the factory with `Class.forName`, which binds against the
calling class's loader — `avatica.core`. The factory lives in `calcite.core`, and avatica does
not reference calcite; the dependency runs the other way. So the lookup fails, the driver's type
initializer throws, and every entry point that opens a connection fails with it.

The fix is to publish the assembly into the boot class loader, restoring the flat-classpath
assumption the Java code was written against:

```csharp
ikvm.runtime.Startup.addBootClassPathAssembly(typeof(org.apache.calcite.jdbc.CalciteFactory).Assembly);
```

This must run before the driver is first touched, since a type initializer runs once and caches
its failure. The test assembly does it from a `[ModuleInitializer]`; `[AssemblyInitialize]` is
not reliably early enough.

The adapter itself does not need any of this: it never opens a connection, and the SQL planning
in `CosmosSqlPlanningTests` drives `SqlParser`, `SqlValidator` and `SqlToRelConverter` directly,
none of which require the driver. The note is recorded because any consumer reaching for
`Frameworks` or `RelBuilder` will hit it.

---

## Cost

Two properties of a predicate dominate what a Cosmos query costs, and neither is visible in the
shape of the plan, so `CosmosFilter.computeSelfCost` reflects both:

- **Naming the partition key** confines execution to one physical partition rather than fanning
  out across every one and merging. `CosmosPartitionKeyExtractor` recovers the value from a
  conjunction of equalities against constants; a disjunction or a range predicate does not
  qualify, since either may span partitions. What it recovers also reaches the executor, so such
  a query becomes single-partition without the caller asking.
- **Filtering on an unindexed path** forces a scan of it. `CosmosContainerMetadata.IsPathIndexed`
  applies the documented precedence — deeper beats shallower, `/?` beats `/*` at equal depth —
  over the container's included and excluded paths. `id` and `_ts` are always indexed.

Index coverage bears on cost only. A predicate or sort over an unindexed path still runs; it is
the composite index requirement for multi-key sorts that affects legality.

Everything above is *inference* — from declared metadata and, where the service gave one, a
measured row count. The service reports what a request actually cost, and that number is the only
one in the system that is not a guess.

### A spelling is not a price — measured

`expandSearch` rewrites `IN` and `BETWEEN` into chains of comparisons before anything here sees
them, and the standing question was whether emitting the native spelling back would be cheaper.
Measured on a real account over five hundred documents:

| Form | Charge |
|---|---|
| `s IN (3 values)` / the same as an `OR` chain | 6.06 RU each |
| `s IN (10 values)` / chain | 7.62 RU each |
| `s IN (50 values)` / chain | 16.52 RU each |
| `n BETWEEN 100 AND 200` / `n >= 100 AND n <= 200` | 7.90 RU each |
| `TOP 10` / `OFFSET 0 LIMIT 10` | 2.37 RU each |

Identical to the hundredth of an RU at every size, and neither form used an index on an unindexed
path — so "index-friendly" is a property of the *path*, not of the spelling. The service normalises
these before costing them, which is why the adapter emits whatever the expansion produced and adds
nothing to say the same thing differently.

### The lookup restriction is already routed — measured, and it closed the shuffle idea

The lookup join sends one cross-partition `k IN (…)` batch per hundred build rows, and the open
question was whether routing it — per key with the partition key pinned, or grouped by feed range,
Flink's `SupportsLookupCustomShuffle` — would beat that. Measured on a real account with four
physical partitions (`CosmosLookupRoutingMeasurementTests`, which reruns the measurement whenever
`COSMOS_TEST_ENDPOINT` names an account):

- **The router prunes.** A single-key `IN` over the partition key, with nothing pinned, contacted
  one partition and cost the single-query floor. The gateway computes the relevant partitions from
  the `IN` values; the fan-out the shuffle would avoid does not happen.
- **Cross-partition execution already is per-feed-range fan-out.** The same `IN(10)` issued once
  per feed range priced identically to the plain query, page for page — grouping by feed range
  reproduces the SDK's own execution and buys nothing.
- **Per-key routing costs more, not less.** Ten pinned single-key queries cost 2.3× the one batch,
  each paying the per-query floor. The charge scales with partitions *contacted*, and pruning
  already minimises those; splitting the batch only multiplies the floors.
- **Padding is free.** The emitted form — a hundred parameters over ten distinct values, repeats
  padding the fixed statement — priced identically to the clean ten.

So the batched statement the lookup join sends is already the cheapest expressible form, and the
shuffle — and with it FLIP-248-style dynamic partition pruning, whose unit of pruning is exactly
what the router derives from the values — is not built because there is nothing left for it to
save. What the measurement is *not* is a statement about latency under load, where per-partition
parallelism inside one query is the SDK's `MaxConcurrency` and stays its business.

`CosmosInstrumentation` publishes a `Meter` and an `ActivitySource`, both named
`Apache.Calcite.Cosmos.Adapter`.

**Through .NET rather than through Calcite**, because Calcite has nowhere to put it. Every `Hook`
value is plan-time — `PARSE_TREE`, `CONVERTED`, `TRIMMED`, `PROGRAM`, `QUERY_PLAN` — and no adapter
in the tree reports execution statistics through one. Cassandra, Druid, Elasticsearch, Geode and
MongoDB all use `Hook.QUERY_PLAN` and stop, which this adapter does too. A meter and an activity
source are what a .NET caller already has a collector for, cost nothing when nobody is listening,
and require no coupling to this assembly.

| | |
|---|---|
| `cosmos.request_charge` | Request units, one measurement per response |
| `cosmos.responses` | Responses received |
| `cosmos.query` (span) | One statement, first request to last page |

Both instruments are tagged with `cosmos.container` and `cosmos.request_kind`, the latter being
`query` or `point_read`. The kind is what makes the point read visible at all: it is charged and
counted like any other request, and without the tag it cannot be told from the query it replaced.

Per *response* rather than per execution, because a query spanning continuations is charged per
page and the spread across pages is itself worth seeing. The span carries the totals —
`cosmos.request_charge` and `cosmos.pages` — since that is what one reader of one trace wants. A
span that never records them is an enumeration the caller abandoned, which is its own signal.

### Index metrics

`PopulateIndexMetrics`, behind the `indexMetrics` operand and off by default: the service computes
the answer per query, so it is a thing to switch on while working out why a query is expensive
rather than to leave on. It lands on the span as `cosmos.index_metrics`, a tag rather than a
measurement, because it is a paragraph of prose naming indexes — something to read, not aggregate.

It is also the instrument for settling the composite index question below.

---

## Unvalidated assumptions

Recorded so they are not mistaken for tested behaviour.

**Null placement on non-nullable keys.** Sorting a non-nullable key is accepted regardless of
requested placement, on the grounds that a key which cannot be null has no null ordering to
disagree about. This is sound provided the declared nullability is accurate — which for the map
row model means `id` and the system properties, whose presence the service guarantees.

**The service's case-insensitive flag folds ASCII the way `UPPER` does.** `UPPER(x) LIKE '%ACADIA%'`
is rendered as `CONTAINS(x, 'ACADIA', true)` on the reading that the flag compares the stored text
and the argument case-insensitively, character for character, over ASCII. The reference documents the
flag and says no more; the rewrite is confined to ASCII text so that nothing outside it is assumed,
and the differential corpus is where the assumption would be caught — it has not yet run against an
account with the rewrite in place.

---

## References

- [Query language overview](https://learn.microsoft.com/en-us/cosmos-db/query/overview)
- [Clauses](https://learn.microsoft.com/en-us/cosmos-db/query/clauses) ·
  [Keywords](https://learn.microsoft.com/en-us/cosmos-db/query/keywords)
- [FROM](https://learn.microsoft.com/en-us/cosmos-db/query/from) ·
  [SELECT](https://learn.microsoft.com/en-us/cosmos-db/query/select) ·
  [GROUP BY](https://learn.microsoft.com/en-us/cosmos-db/query/group-by) ·
  [ORDER BY](https://learn.microsoft.com/en-us/cosmos-db/query/order-by)
- [Subqueries](https://learn.microsoft.com/en-us/cosmos-db/query/subquery) ·
  [Pagination](https://learn.microsoft.com/en-us/cosmos-db/query/pagination)
- [Indexing policies](https://learn.microsoft.com/en-us/cosmos-db/indexing-policies) —
  composite index requirements, default indexing of `id`/`_ts`
- [Databases, containers, and items](https://learn.microsoft.com/en-us/azure/cosmos-db/resource-model) ·
  [Partitioning](https://learn.microsoft.com/en-us/azure/cosmos-db/partitioning-overview) ·
  [Unique keys](https://learn.microsoft.com/en-us/azure/cosmos-db/unique-keys)
- [Calcite adapters overview](https://calcite.apache.org/docs/adapter.html)
