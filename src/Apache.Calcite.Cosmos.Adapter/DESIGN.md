# Apache.Calcite.Cosmos.Adapter — Design

`Apache.Calcite.Cosmos.Adapter` exposes Azure Cosmos DB containers to Apache Calcite as
relational schemas, and pushes as much of the relational plan as possible down to Cosmos by
generating **Cosmos SQL**. Calcite's planner runs in-process via IKVM.

This document records the shape of the target language, the resulting design decision, and the
structure that follows from it.

> **Status.** Under development. Statement generation, container metadata, the schema and table
> layer, the scan/filter/project/sort/unnest/aggregate/rank nodes with their conversion rules, and
> the converter that hands results to `ClrCursorConvention` are in place and tested. Items
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
| Unique key policy | Container definition | `UNIQUE(pk…, paths…)` — see *A constraint says what holds across documents* |
| Computed properties | Container definition | Named, queryable, declared paths |
| Full text policy and full text indexes | Container definition, indexing policy | **Whether a full text function pushes at all** |
| Vector embedding policy and vector indexes | Container definition, indexing policy | **Whether `VECTORDISTANCE` pushes at all** |
| Tuple indexes | Indexing policy | Nothing yet |
| `geospatialConfig` | Container definition | **Whether an `CLR_ST_GEOG_*` pushes at all** |
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

### What a caller may declare beyond it, and what it is worth

Everything above comes from the container definition or is guaranteed by the service. A caller may
also describe the documents themselves, with a JSON Schema on the `containers` operand, and that is
the one input here whose source is the model file. #93.

**Why anything is gained by it.** Cosmos stores strings where Calcite has `UUID` and `TIMESTAMP`, so a
comparison against one has no form the service can evaluate and the container is read whole. What
closes the gap is a proof about the *stored* form: if every value at a path is the canonical spelling
of its UUID, then comparing the stored strings answers exactly what comparing the values answers, and
the comparison lowers to a string equality. Everything after that is machinery that already existed —
the translator renders it, the index serves it, `CosmosPartitionKeyExtractor` pins a partition from
it, and a point read follows where the predicate says nothing else. A *range* against one lowers on
the second bit rather than the first, and pins no partition, having named no value.

**What it is worth, measured.** On a serverless container of 2000 documents, one matching:

| | RU | rows |
| --- | --- | --- |
| `SELECT * FROM c` — what the predicate costs with nothing declared | 33.55 | 2000 |
| `STRINGEQUALS(c.u, '<UPPER>', true)` — the case-insensitive fallback | 3.12 | 1 |
| `c.u = '<lower>'` — what a declaration unlocks | 2.82 | 1 |
| `ReadItemStreamAsync` | 1.00 | 1 |

Two things to read off it. The case-insensitive form **used the index** — it is not the scan it is
easy to assume — so most of the available saving comes from pushing *anything*, and the declaration's
share is the exactness, the routing and the point read. And this container has one physical partition,
so it prices the index question and not the fan-out one; *The lookup restriction is already routed* is
what says the fan-out difference is real, the router pruning from an equality in the predicate where a
case-insensitive call names no value to prune on.

**What a caller writes to reach it.** Two spellings, and a third that looks like the obvious one and
cannot work. `CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID)` and
`CAST(JSON_VALUE(c."DOC", '$.ref' RETURNING VARCHAR) AS UUID)` both lower, the path underneath being
the same path either way. `JSON_VALUE(…, '$.ref' RETURNING UUID)` does not: `RETURNING` *asserts* the
extracted type rather than converting to it, and JSON has no UUID, so the extraction always yields a
string and the assertion always fails. Measured — it validates, plans, and throws
`InvalidCastException` at run time whatever the document holds. It is a landmine rather than an
alternative, and the same shape as the `RETURNING` defect recorded under *Casts over document values*,
with the mismatch now unavoidable rather than merely possible.

#### A fact says which relations it preserves, not what type it is

The obvious model — a path "is a UUID" — is wrong, and it is wrong for a reason that outlived the
measurement that first showed it. A form is a claim about a *spelling*; what a comparison does with
the values is the *engine's*; and the two are separate claims that can each change without the other.
The UUID row is where that was learned, and then where it was demonstrated a second time by the
engine's half moving, so it is worth reading in order.

**What was measured, and was true.** Calcite used to order UUIDs as two signed 64-bit halves
(`java.util.UUID.compareTo`):

| | |
| --- | --- |
| `UUID'80000000-…' > UUID'00000000-…'` | **False** |
| `ORDER BY` over four values | `8000…`, `ffff…`, `0000…`, `7fff…` |
| the same four as text | `0000…`, `7fff…`, `8000…`, `ffff…` |

So for half of all v4 UUIDs the lexical order of the canonical string was not Calcite's order, and a
proof of canonical form licensed `=`, `IN`, `DISTINCT` and routing while licensing **nothing** about
`<`, `ORDER BY`, `MIN` or `MAX`. The condition under which it did was expressible as a `pattern` —
which is the argument for taking the whole vocabulary rather than a flag — and the argument was about
two bits rather than about the value being a UUID. Lexical order of the canonical string compares the
same two halves as *unsigned*: the dashes sit at fixed positions so they never decide anything, and
the hex characters sort in value order. Signed and unsigned comparison of two 64-bit values agree
exactly when their top bits match, so the two orders coincided over a *set* of values only where the
top bit of each half was constant across all of them — the top bit of `mostSigBits` being the top bit
of the 1st hex digit, which `[0-7]` pins to 0, and the top bit of `leastSigBits` the top bit of the
17th, which RFC 4122's `10xx` variant pins to 1 for every conforming value. Pin both and the orders
were the same order; a v4 pattern was not sortable and a v7 one was.

**[CALCITE-7716](https://issues.apache.org/jira/browse/CALCITE-7716) removed the condition, in 1.43.0.**
The JIRA treats the signed comparison as a defect rather than as semantics — `UUID#compareTo`'s
halves-as-signed-longs is a long-documented JDK quirk (JDK-7025832), Postgres orders UUIDs unsigned,
and every other part of Calcite's UUID feature reads the other way. The fix added
`org.apache.calcite.util.UuidValue`, which compares with `Long.compareUnsigned`, behind
`calcite.uuid.unsigned.comparison` — **defaulting to on**. Measured here, through Calcite's own JDBC
driver with the ADO.NET wrapper out of the path: the property is on, all three comparisons the signed
order got wrong now answer **True**, and `ORDER BY` over a set chosen to separate the two on *both*
halves is exactly `StringComparer.Ordinal` over the canonical spellings.
`CalciteUuidOrderingMeasurementTests` is the measurement.

So the confinement is unnecessary now, and for the reason the paragraph above already contains: a
canonical **lowercase** UUID draws from `0-9a-f` alone, over which ordinal text order *is* the
unsigned 128-bit order — for every value, with nothing pinned. Uppercase is the same within `0-9A-F`,
`A`–`F` sitting above `0`–`9` in code point order exactly as `a`–`f` do. A container holding both
cases is not canonical and no pattern recognises it, so nothing changes there.

**What is conditioned on, and why it is read rather than assumed.** The property can be turned off,
and under it the old semantics return — over which an unconfined row claiming an order is a sort
pushed to the service that comes back in the wrong order, silently. So `CosmosStoredForms` reads
`CalciteSystemProperty.UUID_UNSIGNED_COMPARISON` once, and the unconfined rows carry `PreservesOrder`
from it; Calcite reads the same property once into `UuidValue`'s own static, so the two agree for the
life of the process. The confined rows stay, unchanged and always sortable, because they are what
survives the switch being off — under the default they license nothing the unconfined rows do not.

**And the service orders strings by code point, measured.** The whole argument is about *lexical*
order, which is the adapter's word for what Cosmos will do — so it was worth asking rather than
assuming. Over values chosen to separate an ordinal comparison from a linguistic one, `ORDER BY`
matched `StringComparer.Ordinal` exactly and differed from `InvariantCulture` on every case that
distinguishes them: `B` before `a`, `a-b` before `a_b`, and `0000000A-…` before `0000000a-…`. So a
collation is not quietly reordering what the argument rests on — and this half of it was never about
the sign bit, which is why it survives the change above intact.

**Code point, which `StringComparer.Ordinal` is not beyond the BMP — measured since.** Ordinal
comparison in .NET, like Java's `String.compareTo` and so Calcite's in-process sort, compares UTF-16
code units, and the two orders part where a supplementary character, encoded from `0xD800`, meets one
between U+E000 and U+FFFF. On the vnext-preview emulator `ORDER BY` put U+E000, U+FF21 and U+FFFD
before U+10000, U+1F600 and U+10FFFF, where Calcite put them after: code point order, or UTF-8 byte
order, which is the same thing. Nothing a UUID pattern admits is above `z`, so nothing here is touched
by it; what is touched is every sort of free text the service performs, `id` included — see *Ordering a
sort key the way it reads*.

**And the pattern is read as a shape rather than matched against a table.** The rows above were
generated across four axes — case, first-digit confinement, version nibble, variant nibble — and the
product was eighty patterns. It was the wrong shape for the problem, because the axes a UUID pattern
varies along do not close: `[1-5]` is what a schema written against RFC 4122 pins and is the single
commonest spelling published, `[8-9a-b]` is the variant written as ranges, `[0-9abcdef]` is the hex
class spelled out, and a container generating v7 identifiers within a known epoch pins a prefix. None
of those was a row, and an unrecognised pattern states **nothing** — so each of them lost the path its
*equality* as well as its order, and every comparison against it read whole documents.

`CosmosUuidForms.Recognise` decides the shape instead: 32 nibble slots, hyphens at four fixed
positions, anchored at both ends, every slot admitting some set of hex digits. Three things follow for
every string such a pattern accepts, with nothing enumerated — one spelling per value so long as the
whole pattern draws from a single case, lexical order being nibble order because the hyphens are fixed
and `0`–`9` sort below `a`–`f` and `A`–`F` alike, and nibble order in significance order being the
unsigned order of the 128-bit value. Confining a slot only removes strings, so a narrower pattern
inherits all three. The confinement the signed comparison needs is then read off the shape rather than
matched: slot 1 within `0`–`7` and slot 17 within `8`–`f` are exactly the two sign bits, so
`uuid-canonical-lower-sortable` is what the shape *says* rather than what a row asserted.

Two things fall out of it. An alternation is a **union** of its branches' slots, which *derives* the
nil-UUID rule that was previously written by hand: the nil value's variant nibble is `0`, the union's
17th slot becomes `[089ab]`, the confinement is gone, and the pattern lands on the plain row — in any
of the four ways the alternation and its anchors can be spelled. And a character-class range is read
over **code points**, which is a trap rather than a spelling: `[8-f]` spans `0x38`–`0x66`, so it
admits `@`, `Z` and `_`, and admits `A`–`F` beside `a`–`f`, which is two spellings of one nibble. It
is refused, and so is `[0-;]`, which admits no letter to give the case away. Every member of a range
is asked rather than its two ends.

**The brace and the hyphen are axes of the spelling, not of the argument.** A UUID is also written
brace-wrapped and written without its hyphens, and both are read as forms of their own — sixteen in
all, the product of brace, hyphen, case and confinement. Neither new axis costs a relation, for the
reason the hyphens already did not: a character in the same place in every stored string never decides
a comparison, so the strings still compare nibble by nibble in significance order and one value still
has one spelling. What the shape decides is only how `RenderUuid` writes a literal. The names are
systematic — `uuid-braced-lower`, `uuid-hyphenless-upper-sortable` — and the four canonical ones are
what the scheme already spells, so they are unchanged.

**What licensed those two and refuses two others is the reader, measured.** `CosmosRexTranslator`
renders a projected `CAST(<path> AS UUID)` as the raw path and lets the row builder convert, so a form
may admit only text the reader takes. `UuidValue.fromString` — `SqlFunctions.stringToUuid` under the
wrapper — reads all four shapes in either case, and **raises** on `urn:uuid:…` and on the
parenthesised `(…)` spelling. So those two are not forms here although each is exactly as canonical
and as sortable as the four — a projection over one
would raise at read time rather than answer. `ShouldReadEverySpellingThisRecognises` is the
measurement.

A branch of an alternation may not disagree with its siblings about the shape, which is the one place
the union is not slot-wise: a value written hyphenated in one branch and hyphenless in the other has
two spellings, and no equality survives that.

One case is deliberately not claimed. A shape sign-constant the *other* way round — a first digit
confined to `8`–`f`, so that the high half is negative throughout — is sortable under the signed
comparison on exactly the argument above, and is read as the plain row anyway. The claim would need a
form of its own, because `RenderUuid` refuses a literal outside the confined form's sign class and has
only the form's name to decide which class that is; under the unsigned default the plain row carries
the order regardless, so the name would buy nothing but a name.

**The literal is not in the container, which the confinement argument nearly missed.** A confined row
says the lexical order *is* Calcite's order under the signed comparison, and the argument for it is
that each half's sign is constant — across the container. A comparison is against one of each: a path
confined to a first digit of `0`–`7` holds only values whose high half is signed-positive, so `>`
against a literal whose high half is signed-negative is true of every document and lexically true of
none. `CosmosUuidForms.Render` therefore asks the literal for the two bits the confinement pins
and declines rather than answering wrongly — and asks **only** where the answer can differ, which is
the switch being off. Under the unsigned default every pair of canonical spellings compares lexically
as Calcite compares it, in the container or out of it, so refusing there would decline sound rewrites
including the equality ones, which never needed the sign at all.

So a representation carries two independent bits — whether comparing the stored strings for *equality*
answers what comparing the values answers, and whether their *order* does — and `CosmosStoredForms`
sets them per recognised pattern. The UUID rows no longer separate on the second, and the temporal and
numeric ones still do, which is what keeps the two bits independent:

| representation | equality | order |
| --- | --- | --- |
| canonical lowercase UUID, any version | ✔ | ✔ under unsigned comparison, ✘ without it |
| the same, first digit `0`–`7` and variant pinned | ✔ | ✔ |
| ISO-8601 UTC at one fixed precision | ✔ | ✔ |
| ISO-8601 at mixed precision or mixed `Z`/offset | ✘ | ✘ |
| an integer written without padding | ✔ | ✘ |
| an integer zero-padded to a fixed width | ✔ | ✔ |
| an `enum` of strings | ✔ | ✘ |

**A family per file, because the three differ in kind.** `CosmosStoredForms` is the dispatcher and
nothing else: it asks the reader of a pattern for the strings the pattern admits, once, and offers
them to each family in turn. What it dispatches to does not share an implementation, only an output —
`CosmosTemporalForms` is a generated **table** because a temporal shape has a width and a zone spelling
and nothing else to vary; `CosmosUuidForms` **decides a shape** because a UUID pattern's axes do not
close; `CosmosNumericForms` is **parametric** because a fixed width is a family with one member per
width. What they share is the reader, `CosmosPatternReader`, whose parse makes one language written
several ways into one structure (*One reader of a pattern: a form is a language*). They were one
1,400-line class, and the shape of the argument was the thing that got lost in it.

**The temporal case needs its own measurement, and it moves what to look for.** `CAST(<string> AS
TIMESTAMP)` accepts only `yyyy-MM-dd HH:mm:ss`; every ISO-8601 form a document stores raises. But the
function libraries do parse one — `PARSE_DATE`, `PARSE_DATETIME`, `PARSE_TIMESTAMP` and `TO_TIMESTAMP`
all take a format and read the text with it. So does the `REPLACE`/`SUBSTRING`/`CAST` chain a view
writes by hand. What a rewrite has to recognise is therefore a *chain* rather than a cast, and the
notion it needs is that an expression is **form-preserving for a relation**: under the path's
representation, comparing the expression's results agrees with comparing the raw stored strings. A
cast to `UUID` is the degenerate one-link chain.

**A rewrite has to answer what the engine would have answered, and for two years the temporal ones
did not.** The two bits say the *stored strings* compare the way the *values* do. They say nothing
about whether the engine can produce the values at all — and over these shapes it cannot. Measured at
Calcite's own runtime:

| expression | over a stored | |
| --- | --- | --- |
| `CAST(<s> AS TIMESTAMP)` | `2024-01-15T12:30:00Z`, any precision, any zone | raises `Invalid DATE value` |
| `CAST(<s> AS TIMESTAMP)` | `2024-01-15 12:30:00` — space-separated | ✔, and it is the only instant it reads |
| `CAST(<s> AS DATE)` / `AS TIMESTAMP` | `2024-01-15` | ✔ |
| `CAST(<s> AS TIME)` | `12:30:00`, `12:30` | ✔ |
| `CAST(<s> AS TIME)` | `12:30:00.123` | `12:30:00` — the fraction is dropped |
| `CAST(<s> AS TIME)` | `20240115` | `09:37:03` — a value, silently wrong |
| `JSON_VALUE(…, RETURNING TIMESTAMP)` | any string at all | raises |
| `JSON_VALUE(…, RETURNING TIMESTAMP)` | the JSON *number* `1705321800000` | ✔ |

The last two rows are the ones that reframe the clause. `RETURNING` **asserts the extracted type
rather than converting to it** — the reading this document already records for `RETURNING INTEGER`
under *A number spelled as a string* — so it means "this property holds epoch milliseconds as a
number", and over text there is no shape it reads. The adapter had been treating it as a parse.

**So the pushed plan answered where the query raises**, which is not an optimisation: it is a
different query, and it hands a caller rows the engine cannot produce. `CosmosTemporalForms.EngineReads`
is the condition now, it is three rows long, and every site that drops a conversion asks it — the
comparison in `CosmosFactRewriter`, the sort and the ordering candidate in `CosmosServiceOrder`, which
reads a conversion through it, and `CosmosRexTranslator.RequireTheEngineCouldRead`, which is the one
place every clause reaches an accessor through and therefore catches the projection too. The other two
families never had this problem and it is worth saying why: `UuidValue.fromString` and Calcite's
integer cast were both measured against the engine when their rows were written, and the temporal
rows never were.

**What is left after the cut**, and it is the shape of the section from here: a calendar date and a
time of day through a cast, and *everything else through a parse*, which names a reading the engine
performs. A container of ISO-8601 instants has exactly one spelling that pushes, and it is
`PARSE_DATETIME`.

**A parse carries a format, and the format had to be measured rather than read.** Built:
`CosmosTemporalParse` recognises `PARSE_DATE(<format>, <path>)` and its siblings, and
`CosmosTemporalForms` carries, per shape, the formats that read it — so a comparison against one
lowers exactly as a cast's does, and a sort over one binds the ordinal to the path the way a rendered
`UUID` column already did. `CalciteTemporalParseMeasurementTests` is the record.

**An earlier version of this paragraph said these functions read `2024-01-02T03:04:05Z` "once the
literal `T` and `Z` are given Java-style quoting", and that is wrong in the way that matters.** They
do not raise on `yyyy-MM-dd'T'HH:mm:ss'Z'`; they answer **April the 2nd at 03:00:05** for a document
storing January the 2nd at 03:04:05. Calcite reads the format with its own format model and lowers it
to a Java `SimpleDateFormat` pattern, and the model matches its elements without regard to case: the
`mm` is read as a second *month*, the pattern becomes `yyyy-MM-dd'T'HH:MM:s'Z'`, and the minute is
never set at all. The measurement that produced the old sentence had checked only that nothing was
thrown.

**So a format is an input to a parser this does not own, and every row is asked rather than derived.**
That is the difference between this table and every other form in the section. A stored shape is read
out of a declared pattern by reasoning — the pattern *is* the language. A format has no such reading;
what it means is whatever the engine's model makes of it, which changed under `TO_TIMESTAMP` once
already. The claim a row makes is exactly what the rewrite needs and no more: over every string the
shape admits, the parse answers the instant that string denotes, one for one. Injective and monotone
follow, so the chain preserves whatever the shape preserves and the two bits answer for it unchanged.

**What is spellable, measured.** The `%`-style and the `HH24`-style elements both work, quoting a
literal `T` or `Z` as `'T'`, `'Z'`; a zero offset is `'+00:00'` or bare `+00:00`, its characters being
literal either way. Mixed dialects are sound and are not generated, an unrecognised format costing a
pushdown rather than an answer.

| shape | reads | why |
| --- | --- | --- |
| `…T…:…:…Z`, `+00:00`, zone-less, minute precision | ✔ | `%Y-%m-%d'T'%H:%M:%S'Z'`, `YYYY-MM-DD'T'HH24:MI:SS'Z'` |
| the same with three fraction digits | ✔ | `.%E3S`, `.MS`, `.FF3` |
| a fraction of any other width | ✘ | every fraction element lowers to a Java *millisecond* field, so `.678901` reads as 11m18s |
| `yyyyMMdd'T'HHmmss` and the basic instant | ✘ | the model's one-letter fields are greedy: `030405` reads as hour 0, minute 945 |
| a calendar date, basic or extended, and a year-month | ✔ | `%Y-%m-%d`, `YYYY-MM-DD`, and here `yyyy-MM-dd` too |
| a time of day | ✔ | `%H:%M:%S`, `HH24:MI:SS` |

The fourth row is why the shape Azure recommends — seven fraction digits — gets a sort and a range
through a `CAST` or a `RETURNING` clause and gets nothing at all through a parse. Calcite's timestamps
are milliseconds, so there is no spelling to add rather than one that was not looked for.

The fifth row is the measurement in a line: `yyyy-MM-dd` is a spelling for a *date* and for nothing
else, a date having no minute to be mistaken for a month.

**And the type the function answers is half the question.** `PARSE_DATE` over a path storing a full
instant reads the format faithfully and then throws the clock away, so every instant on a day shares
one value and the equality a rewrite would lower is finer than the one the query asked. The parse is
licensed only where the type holds everything the shape carries. A projection asks the converse as
well — the column comes back as the stored text and `CosmosJson` converts it, so a *reader* filling a
half the shape does not carry invents one, and a time of day read back as a `TIMESTAMP` acquires
today's date where the engine's parse gives it the epoch's.

**`CAST(<text> AS TIMESTAMP(3) FORMAT '<format>')` is the same parse, and the one a model view can
write.** Calcite plans it as a `CAST` carrying the format as a second operand and reads the format with
the same model, so the table above answers for it unchanged — measured through both, every spelling.
It exists for a reason the library functions cannot meet: a model view is analyzed under Calcite's
default configuration whatever the connection enabled (`ViewTableMacro.apply` →
`MATERIALIZATION_CONNECTION`), so `PARSE_DATETIME` does not validate inside one and the cast, being in
the core operator table, does. What a cast adds is a precision it names itself, and the same rule as
the type applies one field finer: a bare `TIMESTAMP` is `TIMESTAMP(0)`, so a millisecond format into
one promises a value with no fraction and is refused, even though Calcite's runtime keeps the
milliseconds today. That promise is what the plan widens from to compare against a `.123` literal and
what a reader may round to — ikvmnet/calcite-cosmos#170 saw `.000` there — so the pushdown agrees
with the declared type rather than with what the runtime happens to do.

**Two functions are recognised and deliberately go no further.** `PARSE_TIMESTAMP` answers
`TIMESTAMP WITH LOCAL TIME ZONE`, so a comparison against a zone-less literal is a question about the
session's zone that no declared form answers, and `CosmosJson` has no reading for the type either.
`TO_DATE` and `TO_TIMESTAMP` are each *two* operators under one SQL name — Calcite registers `TO_DATE`
beside `TO_DATE_PG` — and which one a query resolves to depends on the libraries the connection
enabled, which a `RexCall`'s name does not say; only one of the two was measured. Both are in
`TODO.md`.

#### A number spelled as a string, and what the spelling has to promise

Cosmos has numbers, so a path holding one needs nothing here. What needs it is the very common path
holding a **string** that spells one — an account number, a product code, a zero-padded sequence —
which a caller reaches through `CAST(… AS INTEGER)` and which was read whole, the cast having no
Cosmos form.

**What the cast accepts is the whole problem, and it is measured.** Calcite reads every one of these
as forty-two:

| stored | `CAST(… AS INTEGER)` |
|---|---|
| `'42'` | 42 |
| `'042'`, `'0042'` | 42 |
| `'+42'` | 42 |
| `' 42'`, `'42 '` | 42 |
| `'-0'` | 0 |

and for `DECIMAL` also `'1.50'` = `'1.5'` = `'01.5'` = `'+1.5'`, and `'1e2'` = 100. The consequence is
one line:

```
CAST('042' AS DECIMAL) = CAST('42' AS DECIMAL)   is true,   while   '042' = '42'   is false
```

So a form admitting two spellings of one value cannot have its equality lowered to a string equality:
the comparison would miss every document written the other way.

**Two families are injective, and only one of them is ordered.**

| declared `pattern` | what it proves |
|---|---|
| `^(0\|[1-9][0-9]*)$`, `^[1-9][0-9]*$` | equality |
| `^[0-9]{n}$` | equality **and** ordering |
| `^[0-9]+$`, `^\d+$`, anything signed | nothing |

Forbidding the leading zero gives one spelling per value, which is all equality asks. Ordering asks
that the lexical order of the stored strings be the numeric order, and a lexical comparison decides on
the first differing character — which compares digits at equal significance only when the strings are
the same length. Unpadded, `'9'` sorts after `'42'`. Padded to a fixed width, it does not, and the
service's ordinal string comparison is what makes that the numeric order (measured under *A container
settles the null placement*).

**`^[0-9]+$` is the trap and is read as saying nothing.** It looks like the obvious way to write "a
number" and it admits `42` and `042` alike. The sign is refused by the same test rather than a
separate one: `'-0'` casts to zero, so a signed form gives zero two spellings before ordering is even
asked about.

**The width is carried on the form because rendering needs it.** A literal is written into the
container's own spelling — `42` is `'00042'` at width five — and a value that has no spelling there is
declined rather than approximated: six digits beside five-character strings would compare by length
rather than by value, and a negative has no spelling in an unsigned form at all.

**What the equality still carries, and why.** The pushed comparison keeps a second disjunct reading
the path as a stored *number*, because a stored `42` and a stored `"00042"` both satisfy the cast in
Calcite and pushing one alone would drop rows. That disjunction is a superset, so the lowered equality
is rechecked above — the cast itself is gone from the plan either way. A declared `type: string`
ought to delete the numeric disjunct and does not yet; `TODO.md` records it beside the same gap the
parameter work found.

#### The theory of JSON, in layers

**There are no facts about the documents but the theory's.** That is the owner's rule for all of this, stated
once here. What a path can hold — its types, its values, its bounds, its characters, its stored form, whether
it is there — and how two such facts combine are derived in the theory and nowhere else. C# does three things
and no fourth:

- **It reads an input into facts, with one reader for each kind of input.** A schema is read by
  `JsonSchemaFacts`, a `pattern` by `CosmosPatternReader`, a filter by `CosmosPredicateLoader`, a value's
  spelling by `JsonFactEncoding`, the service's guarantees by `CosmosServiceFacts`. A reader states what it
  read and decides nothing about what the fact is worth.
- **It decides what is legal**, given the facts and the service's measured behaviour: whether an order, a
  comparison, a rendering, a route or a point read keeps exactly the rows Calcite does (`CosmosServiceOrder`,
  the translator's renderings, `IsSortSupported`, the partition key extractor).
- **It writes the statement.**

A helper that derives or combines a fact about the data belongs in the theory, as a rule. Where one has not
moved yet, it is listed with the reason at the end of *Characters, a fact of the theory* (*What is left in C#*),
each with its `TODO.md` entry.

A container's declaration is reasoned about by a theory of JSON written as an
[Eggnog](https://github.com/alethic/eggnog) e-graph, under `Facts/Saturation/`. What a query established is
saturated beneath the declaration, and a question is a read of what saturation proved. `JsonSchemaFacts`
reads a schema into `JsonFactRule`s of `(path, claim)` atoms, and `JsonFactTheory` compiles the rules into the
theory. A filter's predicate is loaded beside them as terms by the predicate oracle (*Predicates as facts*,
below), which every rule that reads a filter's facts asks; `JsonFactSet` answers in the old vocabulary, so a
consumer asks what it always asked.

**Four contexts, three of them frozen.**

1. **The root holds the rules of JSON**, global to the graph: what is true of every document because it is
   JSON. A present property has a present object for a parent. Only an object has properties and only an array
   has elements. A value is of its own type, and its characters are its own. A bound, a length or a disequality
   narrows a domain, and a domain's numbers bound the path by the least and the most of them. A present path of
   one value holds it in every document (`constant`, the value part of the fact so that a split lifts it only
   where its cases agree). A string is stored as it was written, a lone surrogate as U+FFFD, which is the
   service's measured behaviour read as data (`Service.LoneSurrogateIsStoredAsReplacement`). A path with no
   possible type is absent, and absent and present together is a contradiction.
2. **A child of the root holds the rules of JSON Schema**, as local rules (`JsonContexts.JsonSchema`). These
   read what only a schema keyword states — `required`, a closed object, `multipleOf`, `uniqueItems`, counts of
   items and properties, a stored form and the characters it is written in — and they include the interpreter for a
   schema's conditional rules.
3. **Each declaration is a context of its own** beneath that. It holds the unconditional facts as facts, the
   conditional ones as rows, and its discriminators marked. Its own split is saturated and frozen once.
4. **Each established set is a query context**, transient, beneath its declaration.

A rule belongs to JSON when everything it reads is a property of the document itself: presence, type, value,
lengths, bounds, membership, elements, equality. It belongs to JSON Schema when it reads a fact only a schema
keyword states, or when it is the conditional-rule machinery. The split keeps the layers distinct, so that
a context could one day hold JSON's rules without a schema's.

**Paths are terms, and two equal paths are one class.** `Root`, `Prop`, `Index`, `Each` for every element
of an array, and `Ghost` for a discriminator no document holds. Congruence makes the properties of equal
paths equal, which is what `CHECK (a = b)` will need.

**A fact says what a path holds if it holds anything.** The facts are: a set of types over string, integer,
fraction, boolean, null, object and array; a domain; disequalities; numeric bounds; string and array
lengths; the bands of a string's characters; a closed object; required properties; and the rest of JSON Schema's
assertions. Each is
conditional on presence, as JSON Schema's own keywords are, and an absent path satisfies all of them. Only
`present`, `absent` and `contains` say whether something is there. A claim `OfType(T, OrNull)` is the set of
`T`'s members, with `null` added where it is admitted, so `number` is `{integer, fraction}` and *an integer is
a number* is a subset test rather than a rule.

**Nothing entails presence** — the stance this section has always taken, now carried by the encoding rather
than by a table. A schema's `properties` constrains the value a path holds if it holds one; only `required`
says it holds one. A host's extension is a token and entails nothing: a geography says what the value is if
there is one, and the subschema's own `"type": "object"` and its parent's `required` are what say it is an
object and is there. It used to say for itself that it was both, stating `present` beside its token, and an
optional location's distance was then sorted at the service as though every document had one — where a
document without one sorts first at the service and, under Calcite's default placement, last.

#### Absence is not a contradiction

Two claims no one value satisfies — a string and an object, an enum and a type outside it — leave the path no
possible type, so it is absent. That is a fact about documents, and every document without the path
satisfies it. It becomes a contradiction only beside a presence.

The Horn engine had no atom for absence. It paired such claims as excluding each other whether or not
anything said the path was there. A schema stating `type: integer` beside a string `enum` therefore made
every predicate over the container `false`, every document of it lost (F3, pinned by
`TwoDeclaredClaimsNoValueSatisfiesContradictNoQuery`). Its stance of "no exclusion between two type claims"
avoided only half of that mistake, and was right only for a theory that could not say *absent*.

**The query supplies the presence.** A comparison is undefined at the service over an absent path and
unknown in process, and a filter keeps no row for either. So every row a comparison keeps has its path, and
what an assumed comparison establishes (`PredicateAtom.Kept`) says so beside what it says of the value (F4).
That presence is what turns `status = 'deleted'` against a declared `enum` into a contradiction rather than a
proof that `status` is absent. It is also why an `IS_DEFINED` guard beside a comparison on the same path is
deleted.

**A range supplies a bound, and binds only numbers.** A comparison through a numeric cast puts the raw
value in a window — `CAST(n AS INTEGER) > 500` keeps numbers above 499 — and the atom states that window as
two bounds beside the presence, the window `CosmosNumericWindow` gives the split rule to push: one window,
read through the casts of the literal the window reads through, refusing a bound past 2^52 for a cast to
`DOUBLE` as the window does. A bound binds numbers only, because the cast converts a stored string too:
`> 500` keeps `"600"`. So a range
beyond a declared `maximum` leaves the path no number, which contradicts the presence only where the
declaration leaves it nothing else, a `type: integer` (gap 4, `ARangeBeyondADeclaredBoundKeepsNoNumber`).
A range no member of an integer `enum` lies in empties the domain (gap 5, `ARangeOutsideAnEnumKeepsNothing`).
`RETURNING DECIMAL` is such a cast written as a clause, and an equality through it is a window too and
not a value: it parses a stored `"2"` as 2, so read as `EqualTo 2` it contradicted a declared string and
the filter was rewritten to `FALSE` (`ADecimalReturningIsNoClaimThatThePathHoldsANumber`).
Any other order comparison states only the presence. Over a promoted `VARIANT` it follows Calcite's
ordering across types, which has not been measured to keep the stored number on its side of the
constant. Over a `RETURNING` that asserts it now has been — the clause converts nothing — and the bound
it would state is in `TODO.md`.

#### A conditional rule is a row

A schema's conditional statements — a `oneOf` branch under its discriminator, an `if`/`then` — are held as
rows of the declaration's context and read by about fifteen fixed rules in the JSON Schema layer.

- `step(r, k)` says that the first `k` atoms of rule `r`'s body hold.
- An atom keyed `(r, k)` extends the chain.
- A complete body turns the head rows of `r` into facts.

An engine rule costs every query an evaluation, fired or not. The spike measured one engine rule per
declared rule at 510 ms and 251 MB per query, at 33k rules. As rows, a query cost 0.63 ms at 33k and 1.06 ms
at 109k (`spikes/EggnogDerive/README.md`): it costs what it fires. A schema of tens of thousands of rules is
the case this has to survive.

**A body atom is known the way any fact is, and means what the claim means.** The atom `kind = 'A'` holds
where every value `kind` may hold is `'A'`, a domain within `{A}`. That is exactly JSON Schema's
`if: {properties: {kind: {const: "A"}}}`, which holds vacuously where `kind` is absent, and the `then` branch
applies there. So the schema reader states a presence wherever the condition is one:

- a discriminated branch applies where the discriminator is there and holds the branch's value — a document
  without one may be in any branch;
- an `else` applies where the property is there and holds another value;
- the object branch of a nullable union applies where the value is there and is not null.

**What a conditional fact needs, then, is what the query proved, and nothing else.** A query's `kind = 'A'`
states the value and the presence. The rule fires there, and over a document the guard does not cover, the
conjunct that proved it has already excluded the row — the argument *A declaration is trusted* makes for
every guard.

#### What makes a split sound

A Horn theory cannot close `kind IN ('order', 'invoice')`, each kind declaring `ref` a canonical UUID,
without a disjunctive head. The theory splits instead (gap 1, `TwoKindsNamedTogetherProveWhatBothDeclare`):
one case per value the query leaves the discriminator, each assuming the domain is that value. Saturation
runs in each case, and what every live case proves is lifted to the context that split.

The split is a domain split guarded by three facts: the path is a discriminator, it is present, and a query
mentions it. It is sound because the cases cover the context.

- **The path is there**, so its value is one of its domain's. A split on a path that may be absent would
  leave out every document without it, which is why presence is a guard and not an option.
- **There is one case per remaining value**, and a case whose value no document holds is contradicted and
  drops out of the lift.
- **Domains only narrow**, so cases made for a wider domain still cover a narrower one.

A guard can only make a split less frequent: fewer cases and fewer facts, never an unsound one. That is what
lets the relevance gate choose where a query splits. A path is relevant if a query mentions it, if it is the
parent, array or pattern of a relevant path, or if a conditional rule reads it where the rule's head speaks of
a relevant path. Every path a body reads a value of is marked a discriminator. A declaration's own context
splits on each one that is present, so that what every branch implies holds outright, and a query splits on
the relevant ones. A case may split again, to a depth of two.

**A deletion reads its siblings, and never itself.** The rewrite deletes a conjunct only where every other
conjunct and the declaration make it always true (L3, *Which facts may justify what*). A fact the conjunct
itself proved may be what made it look redundant, and the predicate left behind would prove less than it did;
and of two conjuncts that each imply the other only one goes, asked one at a time. Eggnog's
`RulesetOptions.Licence` says the same thing inside the engine, for a rewrite done there; here it is the choice
of which context to ask.

#### Values, and one spelling each

**A value is a JSON scalar, compared as JSON compares it (#198).** A claim's value comes from a schema and
from a query, and each side used to hand over a CLR box: the schema reader a `long` or a `double`, a query an
exact `decimal` for `1.5` or `1.0`. Two boxes of one number are unequal, so the exclusion table read
`EqualTo 1.5` as excluding a domain holding `1.5`, and the rewrite answered `FALSE` — no rows, where every
matching document should have come back. So a value is a `JsonScalar`: null, a boolean, a string or a number,
with a number compared by its value and held as an exact `decimal` wherever one can hold it. Each side
converts once, at its boundary — the reader from JSON, the extractor from a literal — and a literal that is no
JSON scalar, a `UUID` or an instant, proves nothing.

In the theory a scalar is a string tagged with its type, `string|x`, `integer|2`, `fraction|1.5`, so a domain
is a set and each member's type is read off its tag. `1.0` and `1` are one spelling. A number no decimal can
hold keeps its type and an approximate spelling no exact number shares. No bound, multiple or range reads its
value, so each keeps it rather than guess where it lies.

**A type is a place in a lattice, not a member of a list (#217).** JSON has one number type, and JSON
Schema's `integer` is the subset of `number` with no fractional part. Compared as a list, a path declared
`"type": "number"` excluded every whole number, and the rewrite answered `FALSE`. As sets of members the
question cannot be asked wrongly: `{integer}` is within `{integer, fraction}`. The front end still compares
`JsonType`s, for `JsonSchemaFacts.Meet`, through `JsonTypes.IsA`.

#### Generations, and what a question costs

**One graph per schema object and generation.** A graph holds the rules once and every container's context beneath
them, so the containers of a schema object share it. Each schema object has one `CosmosReasoning`, made where its
containers are bound: `CosmosSchemaFactory.create` (an account one for all its databases), a host's `CosmosSchema` or
`CosmosAccountSchema` for the containers handed to it unbound, or a `CosmosTable` built alone over unbound metadata.
Metadata already bound keeps the reasoning it carries: a schema or table handed another's bound copy
(`CosmosTable.Container`, or what an executor factory was handed) reasons for that container in the first schema's,
and keeps it alive as long as it holds it, so such a schema holds two. The reasoning holds the schema's
`JsonFactGenerations` and the service's facts bound to them, which every container that declares nothing more takes,
so they share one compiled context and one oracle. The oracle and its memo live on the bound theory
(`JsonFactTheory.Oracle`), so they go with it. S's rendering oracle hangs beside them, F's and S's graphs alike the
schema's (*The production oracle*). There is no default: a theory bound to none is rules and their encoding, a value a
host may keep anywhere, and compiles nothing — asked to, it throws `ArgumentException`, which `IsEngineFailure` does
not admit, so a missed binding fails its plan rather than reading as "unknown".

**What is static, and what a schema learns.** The process keeps only facts fixed in the assembly: the rules
of JSON and JSON Schema as code, the service's guarantees (`CosmosServiceFacts.Rules`), the unbound
`ServiceFacts` and `Empty` theories, S's definition read from the measured registry
(`CosmosRendering.Definition`), and bounded pure tables of such facts (`PathCell.Units`,
`PredicateAtom.Parsed`). They are facts, not a cache. Everything a question can teach — graphs, interned
values, compiled declarations, `D0`, statement trees, remembered answers, S's readings of a node — hangs off the
schema's reasoning and goes with it; connections share it as they share the schema (docs chapter 6). A weak table
keyed on a static object, whose value reaches the table, is a holder in its own right, whatever the table's own
lifetime: `JsonFactGenerations._bound`, an instance table keyed on the static `ServiceFacts`, held each factory
schema's bound copy and through it the generations, so every factory-built schema's graph lived for the process
(`CosmosReasoningTests.ASchemaLetGoTakesItsGraphsWithIt`, which failed on the code before this). A run's report
reaches its graph through its rules, so `JsonContexts` hands one only to a flow that asks (`JsonContexts.Report`);
a `[ThreadStatic]` last report kept each thread's last graph. A weak table keyed on whatever a caller passes can be
such a holder too, and two were: `CosmosConstraintCompiler` keys compiled constraints on the caller's type factory,
and `CosmosFilterStatement` keys a filter's statements on its condition, which a public caller of
`CosmosFactRewriter` or `CosmosPartitionKeyExtractor` passes, and which may be a long-lived builder's cached `TRUE`.
Each is weak on the table, or the container, beneath as well
(`CosmosStaticsTests.AConstraintRememberedUnderALongLivedFactoryKeepsNoTable`,
`AStatementRememberedUnderALongLivedConditionKeepsNoTable`).

**The line is held by tests.** `CosmosStaticsTests.StaticsHoldNoLearnedState` reads every static field of the
assembly and fails on one not classed: a fixed fact, infrastructure (telemetry, reflection handles, settings), a
test's arm, or a weak table keyed on what dies with a plan, a graph or a connection, each with its reason. A fact
and a weak table must be read-only, and only an arm may be thread-static or async-local. No other static's declared
type may reach a reasoning, generations, an oracle, a graph or a memo but as a weak table's key, followed through
every field of every adapter type it can hold, derived types included; three facts whose type could and whose value
does not (`ServiceFacts`, `JsonFactTheory.Empty`, `JsonFactSet.Empty`) are listed as such.
`NoStaticReachesWhatASchemaLearned` lets a schema plan, implement and execute through a data source, drops it, and
walks every static's value but the weak tables', field by field and through Calcite's objects, which catches what a
declared `object` holds. A weak table's entries belong to whatever test is running beside while their keys live, so
neither check reads them: each names the test that finds its entries gone once what they remember is let go, and
what that test shows is for the reviewer of the line to judge. No learned static is left: the last,
`CosmosFunctionBodies.Paths`, is parsed per call since #243, and a static listed as leaving fails once it has gone.
Eggnog's own thread statics are listed beside, since one holds a scope while an extraction runs.

**Bound metadata carries its reasoning.** The bound copy is another object than the metadata handed in, for a schema
and for a `CosmosTable` built alone alike: whoever holds it holds the schema's graphs, the executor factory is handed
it, and a host's `RefreshStatistics` on its own object does not reach it — the schema's does, and a table's
`Container.RefreshStatistics()` for a table built alone.

**A graph interns every value it is written, and never frees one**, so a graph serving queries forever grows
forever; and its ids and epochs are finite. A generation is therefore retired once it has interned a million
values, or used half of its ids or epochs (`Usage.FractionUsed`). The next question starts a new one, and each
declaration compiles itself into it when it is next asked. Nothing disposes a retired generation: a statement
holding one of its answers keeps it alive, and once none does it is collected whole. Running out of either is an
engine failure, which loses facts and fails no plan (*F's generations before S reaches a plan*).

**A query's context lives as long as its answer.** A planner asks about one statement a dozen times —
matching a rule, converting, splitting, implementing — so a compiled declaration remembers the answers to its
last 256 established sets, compared as sets. A context the memo lets go of is released once nothing holds it.
Its parent lists it until then, so without the release it would live as long as the generation.

**What it costs.** Measured on four cores in the cloud:

- a query with no split costs 4.6 ms against 47k conditional facts;
- a split of two cases costs 4.6 ms, and one of five 11 ms;
- a contradicted query costs 0.46 ms;
- compiling and freezing that declaration takes 0.8 s;
- 512 concurrent split queries under one frozen declaration answer as serial ones do.

A small schema's query costs a few milliseconds on a workstation, once per distinct predicate. The Horn
closure cost about 5 ms at 33k rules and 18.6 ms at 109k, and so did every planner question, with no memo.

**Every question goes to the engine, a schema-less container's too.** A container that declares no schema
still states what the service guarantees of `id`, `_ts` and `_etag`, so its theory is never empty, and every
stratum of a filter over it is a context the engine opens, as over any declaration.

It was not always so. A filter the split rule divides asked with four distinct sets, at about 1.8 ms each when
the engine replaced the closure, which doubled what a single-container plan cost. `JsonFactOverlay` was the
plan's demand gate for the commonest case: over a declaration with no conditional rule, a stratum whose
conjuncts each said one thing of a path that met nothing the declaration stated was read off those facts and
the declaration's own context, without opening one. To answer without the engine it restated seven rules of
JSON in C# — a value is of its own types, holds its own characters and bounds the path by its numbers; a present
path of one value is constant; a JSON null excluded leaves every other type; a present path has present objects
above it; a path beneath a scalar is absent — and read each conjunct as the oracle's establishing rules do. That
is the rule at the head of *The theory of JSON, in layers* broken for the commonest container there is, and the
plan kept it only until a schema-less question cost a millisecond or less through the engine.

**Retired on Eggnog 1.1.1, which drops idle rule variants without allocating.** Measured in Release, a fresh
process a run, the builds alternating:
- a schema-less question — a stratum, a leave-one-out child, or a child made for a verdict — costs 1.4 ms at the
  median over the differential's statements and 4.9 ms over the corpus's, p90 5–11 ms; 14–39 % less than on 1.1.0,
  and not under the line;
- retiring the overlay costs about 0.12 s (3 %) on the corpus of 78 planned new, and about 1.0 s (3 %) over the
  differential's 3,244 statements; nothing planned again, a statement's tree being remembered;
- 1.1.1 without the overlay plans the corpus as fast as 1.1.0 with it (−0.06 s, p 0.75);
- plans and the SQL sent were identical with the overlay and without it, over every corpus statement, every one of
  the differential's and the sort inventory's 17,107 — though not over every statement there is (below).

So retiring it spent what 1.1.1 saved, and no more. The owner moved the line and retired it: no fact about a
schema-less container's documents is derived beside the theory, and the cost is a millisecond or a few for each
distinct stratum a new statement asks.

**A path a clause reads is told to the context it reads.** Rules reason over paths that exist, and a stratum
mentions what its conjuncts read and nothing else, so a projection, a sort or a grouping after the filter asks it of
paths it was never told of. Beneath a scalar the filter pins that is a fact the theory has — `WHERE name = 'x' ORDER
BY name.first` sorts by a path no row holds, since a path beneath one holding no object is absent — and the overlay
had derived it in C#. Retired, it went: 110 of 720 statements written to read such a path planned otherwise, 30 sorts
no longer pushed, and extremes, groupings and `DISTINCT`s ran in process; none was in the measured sets. So the
tables a fact set reads tell their context of a path the first time they are asked about one it has no term for
(`JsonScopeTables`): a child that mentions it and assumes nothing, run as a question's child is, without a split,
and frozen (`JsonContexts.Mentioning`). A stratum's are leased with its statement; the declaration's are kept as its
context is, and every statement's root reads the declaration's view, so a path is told to a declaration once. Then
each of the 110 plans as it did beside the overlay, the sort inventory still plans as it did, and 78 more of the 720
push what neither build pushed — a key beneath `id`, which the service states a string, or a path beneath a scalar a
declaration states, which the engine had never been told of either — and each answers on the emulator as Calcite
does. Two of them send a `CASE` on a constant where a constant was sent, the same rows; none pushes less. The
corpus planned new costs what it did: 4.61 s against 4.60 s at the median of five rounds, 2.22 s against 2.21 s
again.

**Nothing from the engine reaches the planner.** A context the engine cannot open answers with the
declaration's own facts. A declaration it cannot compile answers nothing. Either way facts are lost and none
is invented.

#### Predicates as facts: the oracle

The planner read a filter's facts through a dozen sites, each deciding for itself which conjuncts to read and what
they prove: a fact extractor's dispatch, the split rule's two passes, the rewriter's tautology test, the translator's
choice between outright and established facts. A defect in that family had usually been the same knowledge reaching
one more site. The predicate oracle (`Facts/CosmosOracle.cs`) is the other arrangement: one statement per filter,
every question about it answered from the facts its licence admits, and no site choosing facts. It was built in
shadow and measured beside the sites it replaced, 97.3 % of 114,922 decisions agreeing and every other one
explained, and now every rule that reads a filter's facts asks it (*The oracle in the planner*, below).

**Eggnog decides what is true; the host decides what is written and what is legal; Volcano decides which plan.**
The oracle loads a filter's conjuncts into the theory of JSON as terms, beside `JsonRules` in the same graph
(`PredicateRules`), and answers with values: a verdict, a fact about a path, whether anything is left. It never
answers with a term. Whether a conjunct renders is asked of today's translator, handed a `JsonFactSet` of the facts
that question may use, and the translator's answer is the host's.

**A reading is a constructor, not a SQL type used as a tag.** `CosmosPredicateLoader` decodes Calcite's types once,
into the typed IR's predicate subset (`SqlValue`, `SqlPredicate`):

- `text(p)` is `JSON_VALUE` read as text, SQL null for an absent path, a null, an object and an array;
- `raw(p)` is the value the service holds: a promoted `VARIANT` column, the accessor the split rule re-types `ANY`;
- `typed(p, T)` is a `RETURNING`, which asserts its type and raises on anything else — but for `DECIMAL`, which
  parses the rendering, a stored string of digits among what it reads, and is compared with a literal cast to its
  scale of zero, so that `= 2.5` keeps a stored 2 and not a stored 2.5;
- `cast(T, v)` is a numeric cast, or the cast to text over a raw value, which renders an object and an array too.

Comparisons with a literal or a parameter, `IN` (a `SEARCH` expanded and read back as one membership),
`IS [NOT] NULL`, `IS_DEFINED`, `AND`, `OR` and `NOT` are read. Everything else is `opaque`, which proves nothing and
decides nothing. `IS_DEFINED` asks of the document whatever the reading, as the service and the in-process body do:
true where the path is there, a JSON null included, and never unknown. Opaque, a definedness test established
nothing, and a weakening beside one kept a test the presence it states would have deleted.

`RETURNING DECIMAL` addresses no path: it is the cast of the rendering written as a clause, and
`CosmosRexTranslator.TryNumericCastOperand` reads it so, returning the text it converts. The loader asks that, and
names the reading whose meaning is that parse, `typed(p, DECIMAL)`; a cast is read the same way, so what a
conversion is and what it is compared with are the translator's to recognise and the atom's to say, once each.

**What an atom means is written once.** `PredicateAtom` is a reading, a test and an operand, and it answers two
questions. `Kept` is what a row on which the atom takes a truth value can hold at its path, which is what an assumed
predicate establishes. `Over` is which truth values the documents a path's knowledge admits can give it, which is
what a verdict is read from. Both over-approximate, in opposite directions of safety, and a property test holds them
to each other on every reading, test and kind of stored value. No rule names a reading or an operator: the rules read
atoms, and the table of what a stored value does under each reading is the one place a measured axiom lives.

**The rendering preimage.** `JSON_VALUE(…) = '30'` keeps a document storing the string and one storing the number,
so it establishes both — `{string|30, integer|30}` — and nothing narrower. A declared `type: string` then leaves the
string alone, which is the deletion of the stored-number disjunct `TODO.md` records as open. It took one rule the
theory of JSON never needed: a domain keeps only values of a type its path may hold. The planner never states a
domain beside a narrower type; a preimage does.

**A row Calcite raises on is no truth value.** `RETURNING INTEGER` raises on a stored `30.0`, and a numeric cast on
a string it cannot parse. Such a row is no row a predicate keeps, so it constrains nothing an assumed predicate
establishes — the asymmetry *Reading a value back* records for `RETURNING`, the caller's declaration held rather than
checked. It supports no verdict either: deleting the predicate would answer where Calcite fails, and which of the two
a pushdown may do is not settled.

An integer cast of the accessor is a parse of its rendering, not a conversion of the number: `CAST(JSON_VALUE(…) AS
INTEGER)` raises on a stored `30.0`, `2.5` or `1e30`, and on `3000000000`, where a cast of the number would have
truncated or held it at the limit. The theory spells `30` and `30.0` alike, so a number there may always raise, and
nothing is decided of one; a range its declared bounds satisfy is a tautology of `CAST(… AS DOUBLE)`, whose parse reads
every rendering, and of a cast of the raw value, and not of this. The first table said otherwise, and the population-D
checker found it.

**A predicate holds with a truth value.** `holds(e, true)` is a conjunct the context assumes. `NOT` swaps the value,
a conjunction that holds holds on both sides, and a disjunction that fails fails on both. A disjunction that holds,
or a conjunction that fails, splits: one case per arm, each assuming its arm takes the value, and what every live
case proves is lifted. The cases cover the context, because a row a disjunction keeps is kept by some arm. That is
gap 1 spelled per kind: `(kind = 'order' AND …) OR (kind = 'invoice' AND …)` proves what both kinds declare.

Arms split before discriminators. Split together, the discriminator `kind` opened a case for each of the sixty
kinds a declaration had beside the two arms, 0.8 s a statement; split after the arms, it opens one for each kind they
lifted, and the statement costs 60 ms.

**A context mentions what it assumes**, as a query's context mentions what it established, so a split opens only
on a discriminator something there narrows. A first version mentioned every path the statement read in a context
holding its terms and nothing else, and split thirty-two ways on a discriminator nothing had narrowed yet: 1,171
iterations and 0.7 s a statement, against 19 iterations without.

**Three verdicts, and twelve connectives.** `always true`, `always false` and `never true`, in Kleene's logic.
`always false` and `never true` are kept apart because UNKNOWN is not FALSE under `NOT`: a comparison over an absent
path keeps no row, and neither does its negation. What is always false is never true; nothing reads never-true
beneath a `NOT`. An atom's verdict reads one value per path — `possible(p)`, what the path may hold, gathered from
presence, types, a domain, disequalities and bounds — so that three rules decide every atom rather than one rule per
shape and per table. These give tautology the shapes the equality test before them lacked: an `IN` covering an
enum, a `<>` outside one, `IS NOT NULL` of a required scalar, a range its declared bounds satisfy, a disjunction
with a sibling for a branch.

**What a context assumes, it has decided.** A predicate holding with a truth value on every row a context admits has
that verdict there, whatever its atoms can be said to mean. The host reads the same of a predicate a context assumes,
and of a conjunction of what it assumes, off the assumptions themselves and before any run, so that the answer rests
on no run the engine might not finish — which is two of those rules read in C#, and listed as such (*What is left in
C#*). Without the two rules that say so, a predicate given was
decided only where its atoms decided it, so an opaque one — a definedness test beside a type test, which is what the
split rule pushes as a weakening — was never redundant beside itself, and a planner asking the oracle whether a
weakening is already beneath a filter would push it under itself for ever. Two opaque duplicates were both kept. The
verdicts are read after the splits, and what holds inside a case is never lifted to be read there.

#### Which facts may justify what

The soundness review's statement of the criterion, which the planner once approximated per site and now asks of
one table:

- facts from siblings or from the declaration may justify any rewrite;
- a conjunct's own facts may justify only a strengthening, and the oracle never offers them;
- a deletion or a weakening needs facts from outside the conjunct;
- a consumer that is not a filter needs facts that hold of every row it touches.

**The strata.** A conjunct is admitted in the first stratum that renders it, and its own facts enter only the
stratum below. So nothing a conjunct establishes is ever among the facts it was rendered from, to any depth, and two
conjuncts cannot license each other: the later one was rendered from the earlier one's facts, never the reverse.
#117 had a conjunct certify its own exactness from a fact its own rendering established; in the strata it cannot be
written, and `TheSelfCertificationOf117CannotBeDerived` shows each way it could have been. The first stratum is the
declaration. The split rule's two passes were the strata cut at two rounds, the first handed nothing at all; a
conjunct the second round's facts would admit, and which admits a third, was left above.

**The licence table**, in one place (`CosmosStatementScope.ScopeFor`), so that no call site chooses facts:

| the question | the facts it may use |
| --- | --- |
| whether a conjunct renders | the stratum before the one that admitted it; for one never admitted, the last stratum, which holds its siblings and never itself (L2) |
| whether a conjunct can be deleted | the other conjuncts still kept (L3); for one the strata admit, only the admitted ones, so that what the service applies is no weaker without it |
| a comparison inside a conjunct lowered | every other conjunct the deletions keep (L3, of the statement they leave) |
| inside the arm of a `CASE` in a conjunct | every other conjunct the deletions keep, and the conditions the arm is read under |
| whether the filter keeps nothing | every conjunct, admitted or not: a strengthening, which a conjunct's own facts may justify |
| routing a statement that applies its predicate, and how many documents it matches | every conjunct, admitted or not: a partition skipped holds no row the predicate keeps |
| what a point read may rely on | the conjuncts that pin its keys, and no other: a point read applies no predicate, so every other conjunct has to be always true there |
| whether a weakening adds anything to what is applied | every admitted conjunct, what the filters beneath apply, and the weakenings already pushed beside them |
| a projection, a sort, a grouping, a key a lookup restricts | the last stratum: every admitted conjunct, applied before the clause reads a row or beside the key's restriction (L1) |
| a residual, a side with no filter, a key another node asks of a clause | the declaration (L0) |

**An order the service computes is read through `CosmosServiceOrder` under the consumer's own row, and nowhere
else.** The table gains no row for it: a sort and an extreme ask under the last stratum (L1), an ordering comparison
under its conjunct's render row (L2), the string branch of a weakening under the weakening's. No site reads
characters, types, presence or bounds for an order itself (*The service's order, asked once*).

**Redundancy is asked one conjunct at a time**, in digest order, each against the conjuncts still kept, so two
duplicates do not delete each other. "If I add X, is Y irrelevant?" is a statement of X asked of Y under `Kept`: Y
always true where X holds.

**A deletion keeps what the service applies.** Deleting a conjunct its siblings imply changes no row, whichever
siblings imply it; it changes what reaches the service where the sibling does not. `label IS NOT NULL AND label <>
'zzz'` over a path declared nothing renders the first exactly and the second only as a weakening, which does not
imply the first, and deleted for the second the exact test stopped reaching the service: the null, the array and the
object it excludes crossed the wire for the recheck to drop. So a conjunct the strata admit is asked only of the
admitted siblings, and one left above of every sibling, as before. The strata are those of the condition as written,
before a lowering: a sibling admitted only once lowered counts as left above, and the conjunct is kept, which costs a
redundant test and never a row.

**A lowering is asked of the statement the deletions leave**, for the same reason two duplicates are not both
deleted: each rewrite is sound on the statement it is asked of, and the rewrite does two in turn. A conjunct deleted
because another implies it holds wherever the other does; lent to that other as a sibling's facts, it would let the
other be lowered on what the other itself establishes — #117's self-certification one step removed — and the lowered
form would be the original only where a conjunct the filter no longer applies holds. An ordering onto a stored form
is no strengthening, so a row the original drops could be kept.

**The arm of a `CASE` is a licence of its own**, which the plan's table lacked and the shadow's judge found it
needed. A merged view's column is `CASE WHEN <that view's filter> THEN … END`, and what its arm is lowered by is what
the filter proves of the rows the arm is read for, beside what the conjunct's siblings prove of the rows the
conjunct is decided for: over a row where the condition is not true the `CASE` answers its `ELSE` however the arm
reads. The conjunct the `CASE` is part of is not among them, for the reason a deletion's own facts are not. A
condition spelled as a sibling is that sibling's term, so the copies a merged view repeats in each column are one
child each.

#### What the oracle promises, and what it costs

**It returns values, never a term to execute.** Every constructor `PredicateRules` defines is unextractable, and
`NothingIsExtractableAndTheRulesStayWithinTheirBudget` holds it so.

**The only unions are path unions and congruence.** No predicate rule unions two predicates or two values: an
implication is a verdict in a scope, never a merge of classes, which is the trap a filter-truth equivalence becomes
under hash-consing. The only unions in the graph are `JsonRules`', of paths.

**A run that does not complete knows nothing it did not know before.** A context whose run stops on a limit, or that
the engine cannot open, is released, and the stratum it would have been reads the one above it, whose facts hold of
the fewer documents below. A contradiction is no failure: it is the answer that nothing is kept.

**The planner's graphs.** `CosmosOracle.For` reasons in the generations the declaration compiles into, so a
declaration compiles once whoever asks: the oracle, and the clauses that still read the declaration directly.
`PredicateRules` is defined in a graph when the oracle first asks of it, and its rulesets run only in the oracle's
schedules, so a query context the planner opens for itself pays nothing for them. A test makes an oracle in graphs
of its own, so that no test reads another's memo.

**One tree of contexts per statement**, remembered by a digest that ignores the conjuncts' order, beneath the
declaration's frozen context. The strata and the leave-one-out children each hold the statement's terms and their
verdicts. A question about a predicate a context does not hold is answered in a child made for it that assumes
nothing, and so splits on nothing: the context above has lifted what a split there proves. Asked in a child of a
split stratum with the split allowed, the same question cost 45 ms where it now costs about 5.

**What it costs**, measured by `CosmosOracleMeasureTests` at `MeasureTests`' sizes, in a Debug build on a busy
workstation, beside a query context of what the same statements establish, which is how the planner read a filter
before the oracle (`Derive`, since deleted; the test assembly keeps it as a reference for the theory's own tests):

| | 5,069 conditional facts | 47,502 conditional facts |
| --- | --- | --- |
| a new statement, `kind = 'k0'` | 7.4 ms (`Derive` 3.8) | 12.4 ms (`Derive` 8.8) |
| a new statement, `kind IN` two kinds | 15.6 ms (`Derive` 10.6) | 24.6 ms (`Derive` 24.2) |
| a new statement, one kind per arm of an `OR` | 51 ms | 57 ms |
| a new statement no document satisfies | 1.2 ms | 1.3 ms |
| a deletion's question, of a statement already asked | 5.1 ms | 9.9 ms |
| a predicate the statement does not hold | 0.7 ms | 0.6 ms |

The figures are p50s; the p95s are two to four times them, and the machine was shared. A new statement costs between
what the planner's `Derive` of the same facts costs and about twice it, because a stratum also runs the verdicts and
is frozen; what `Derive` cannot do at all — the disjunction per kind — costs a split per arm. A question of a
statement already asked costs about a millisecond. Stage 1's gate asks p50 within 2 ms and p95 within 11 ms per
distinct question: a new statement is outside it, a question of an existing one inside, and what each costs once
Eggnog's idle rules allocate nothing is the measurement still owed.

#### The oracle in the planner

**One statement per filter, asked by every rule that reads its facts.** `Metadata/CosmosFilterStatement.cs` is a
filter as the oracle reads it: its conjuncts, loaded once through the input's binding; the statement the oracle holds
for them; and the questions a rule asks, each named for its licence rather than for the rule that asks it.

| who asks | what | the licence |
| --- | --- | --- |
| `CosmosFilterRule`, whether the filter pushes whole | `Renders` | each conjunct from the stratum before the one that admitted it (L2) |
| `CosmosImplementor.TranslateCondition` | `Translate`, conjunct by conjunct | the same strata, so what was admitted is what renders |
| `CosmosFilterSplitRule`, which conjuncts push | `IsAdmitted` | L2 |
| the split rule, a weakening rendered and its guards deleted | `TranslatorFor` a conjunct left above | the last stratum: what every pushed conjunct establishes, never the conjunct's own |
| the split rule, whether a weakening is already applied | `IsApplied` | every admitted conjunct, what lies beneath and the weakenings pushed before it (`Applied`) |
| `CosmosFactRewriter`, the filter made `FALSE` | `IsContradictory` | every conjunct (a strengthening) |
| the rewriter, a conjunct deleted | `Redundant` | its siblings still kept, one at a time, the admitted only for an admitted conjunct (L3) |
| the rewriter, a comparison lowered onto its stored form | `Siblings` | L3, of the conjuncts the deletions keep |
| the rewriter, inside the arm of a `CASE` | `Within`, `NeverWithin` | those siblings and the arm's conditions |
| the translator, of a rendering or a typed field | `IsDeclared`, of the facts it was handed | the caller's licence, which every caller names; `JsonFactSet.Empty` where it only resolves paths |
| a node built without facts, by hand or by a host's rule | `CosmosImplementor.FactsOf` | the declaration (L0), spelled there and nowhere else |
| `CosmosProjectRule`, `CosmosProjectSplitRule`, `CosmosProjectResidualRule`, `CosmosProject` | a projection's rendering, a form the filter proves among it | the last stratum of the filters beneath (L1), carried on the node |
| `CosmosRankRule`, `CosmosRank` | the projections a rank writes | L1, carried, as a projection's |
| `CosmosUnnestRule`, the predicate over a traversed element | `Renders` of its statement | the strata, as the `CosmosFilter` it builds renders it |
| `CosmosSortRule`, `CosmosSort` | the null placement, a computed column's ordering licence, a key read as stored, a temporal key's order | L1, carried |
| `CosmosAggregateRule`, `CosmosAggregate` | a grouping key normalised, a key read as stored | L1, carried; the key a `DISTINCT` binds, which another node asks, L0 |
| `CosmosLookupJoinRule`, `CosmosLookupJoin`, the probe's key restricted as stored | a text key's path a string, a `UUID` key's one spelling | L1 of the probe side (`Key`), carried |
| `CosmosSelfJoinRule`, a constraint's predicate proved of a side | `Proves`, `Declares` | every conjunct of the side's filter; the declaration where it has none (`Declaration`) |
| `CosmosSelfJoinRule`, a key read faithfully | one scalar type, one `UUID` spelling | every conjunct of the two sides' filters' disjunction |
| `CosmosPartitionKeyExtractor`, the partition a statement is routed to, and whether it matches one document | the key there and holding one value | every conjunct |
| the extractor, a point read, a batch of them, a whole-partition delete | the same, and every other conjunct `Always` | the conjuncts that pin the keys, and no other |

What these replaced: a fact extractor reading a predicate's conjuncts by hand, and every `Derive` of what it read at a
filter site — the filter rule's whole predicate, the implementor's, the rewriter's whole predicate and its
declaration alone, the `CASE` arm's, the split rule's unaided pass; the split rule's two passes and its digest test
of what is beneath; and the translator's four questions of a type, two over what a filter established and two over
the declaration alone, which existed because what a filter established included the conjunct being translated.
After the filter, a `Derive(null)` at every projection, sort and grouping site, which read the declaration alone
where the rows had passed a `WHERE`; Calcite's pulled-up `IS NOT NULL` for a sort's null placement; and the
partition-key extractor's own reading of a predicate, its equalities by hand and a seed of what the declaration
states outright, with a second walk asking whether anything else was said.

**The clauses after a filter read the rows it kept (L1).** `WHERE` runs before `SELECT`, `ORDER BY` and `GROUP BY`, so
what the pushed conjuncts establish holds of every row a clause reads: a sort over `t IS NOT NULL` has no null to
place, a grouping over `x IS NOT NULL` no absent key to tell from a null one, and a projection over `kind = 'B'` reads
`ref` in the form kind `B` declares. `Metadata/CosmosRowFacts.cs` reads it through the input as the binding is read —
a set's original member, through filters, projections and sorts, each keeping the rows it is given or fewer — and
stops at anything that is a relation of other rows. Several filters are one statement of what each admitted, asked
under `Kept`, each filter's opaque terms numbered apart from the others', since each is numbered over its own binding.
Asked once per clause, by its rule — remembered by the clause, the container, the licence and the input — and carried on the
node it builds, as the sort's non-null fields always were: which filters a walk finds depends on the member of a set
it reads, and implementation reads the one tree the planner chose, whose filter may be the same condition rewritten.
A question another node asks of a clause — the key a `DISTINCT` binds, which a sort above asks — is answered on both
walks, and reads the declaration: a key only the filter proves present is grouped as the path and bound to none, which
costs the sort above and cannot fail the plan.

**A key is pinned where the oracle says it holds one value.** `CosmosPartitionKeyExtractor` reads a filter's condition
as a statement and asks, of the key's path, whether the theory knows it holds one value in every document — its
`constant`, a presence and a domain of one combined by a rule (`JsonFactSet.TryGetConstant`).
Routing a statement that still applies its predicate reads every conjunct, a strengthening's argument: a partition it
skips holds no row the predicate keeps, so a key a sibling's kind declares routes as surely as one the predicate names
— `kind <> 'b' AND kind <> 'c'` over a required enum routes to `a`. A point read, a batch of them and a
whole-partition delete apply no predicate, so they read only the conjuncts that pin their keys
(`CosmosStatementScope.IsPin`: an equality of what the path holds or reads as, with one literal, never through a
cast), and every other conjunct has to be `Always` in that scope or the operation answers another question than the
query asked. The value is the oracle's, not the literal's: an equality over text a number renders as pins nothing,
`'30'` being a stored `"30"` and a stored `30`, two partitions. A number is sent as the client sends a literal of it, a
whole one as a `long` and any other as a `double`, the oracle holding a `decimal` no partition key component takes.

**A filter that keeps nothing is no request, and one that keeps everything has no `WHERE`.** The rewrite answers
`FALSE` where every conjunct together contradicts the declaration, and `TRUE` where it deleted every conjunct.
Either reached the planner only on the converted filter, a `CosmosFilter` carrying the constant — `WHERE @p0` bound to
`false`, a round trip for no rows — which `CoreRules.FILTER_REDUCE_EXPRESSIONS`, configured for a `LogicalFilter`,
never sees. `CosmosFilterReduceRule` matches the logical filter, over its own convention's container only, and puts
the empty relation, or the filter's input, beside it; a host with a rule for an empty `Values` answers the first with
no request at all. Only the logical filter: a converted one carries a condition some convention already rewrote, and
a filter in a host's convention is not the adapter's to reduce.

**Each rule reads its own container's declaration.** A rule set is registered per container, and every rule in it
sees every operator in the plan, whoever's table it reads. The split rules always found the scan beneath them and
declined another convention's (`CosmosFilterSplitRule.FindTable`); the conversions did not, and decided under their
own convention's declaration. Unimplementable, the converted filter was still a member of the original's set, and
once a converted filter carried a constant, the reduction read it there: in `SELECT … FROM typed WHERE kind IS NULL
UNION ALL SELECT … FROM kinds`, the kinds' required `kind` made typed's filter `FALSE`, and nine rows were lost; `kind
IN ('a', 'b', 'c')`, which the kinds' enum covers, dropped typed's filter and kept nine too many. Every conversion now
asks whether the walk of its input found its own container (`CosmosConvention.Owns`), and the reduction reads only
the logical filter. It also takes the oracle's work for a filter from once per registered container to once.

**Remembered against the condition node.** A rule is asked the same filter each time Volcano matches it and again at
implementation, and a condition is an immutable node the planner hands back unchanged. So a statement is remembered
against the node, with the binding it was read through — the binding, because one condition over two inputs is two
statements, and the container, because two containers declaring nothing share one declaration and not one
translator — and released with it. The rewrite's answer is remembered on the statement, so the node it returns is
one node each time, and the statement of the rewritten condition is remembered too. Beneath them the oracle
remembers its contexts by the statement's digest.

**Asked only where something could answer.** A statement has one scope, whose rounds of admission run the first time
a question names a stratum — a render, a deletion beside what reaches the service, a clause after the filter, a
weakening — and never for a contradiction, a lowering's siblings or a key's pins; a second scope exists only where the
rewrite deletes, for the statement the deletions leave. The last stratum is opened only where something asks of every
admitted conjunct, which a filter pushed whole never does. And the oracle asks a conjunct whether it is redundant only
where something could decide it — a sibling reading a path it reads, or one above or beneath it, or sharing a term
with it, or a path the declaration speaks of (`CosmosStatementScope.RedundantConjuncts`). That is the oracle's own
bound on what it looks for, and it costs what it bounds: `IS_DEFINED(x) OR NOT IS_DEFINED(x)` over a path nothing
speaks of is true of every document and is not deleted.

**A weakening the rest already implies is not pushed.** Asked of what the statement will apply beside it — the
conjuncts beneath, those pushed with it, the weakenings before it — and of the declaration. That is the digest test
of what lies beneath, which kept the split rule from pushing a weakening under itself for ever, made general: the
oracle reads a weakening spelled as a conjunct beneath as assumed, before any run, so the loop cannot return. And it
is more: `IS_DEFINED(c.id)`, which a comparison over `id` implies, is true of every document by the service's own
statement, and pushed it filtered nothing while making a filter a host could plan in process, where the service's
test has no body.

#### How the oracle is checked: population D

`SoundnessTests` states facts true of one document and checks that what follows is true of the same document. That
is the right test of a theory of facts, and blind to the mistake a theory of predicates makes most: a conjunct
licensed by its own facts is right of every document that satisfies it, and wrong only of the others, which that test
never draws. So the oracle's checker (`PopulationD`, run by `PredicateSoundnessTests`) draws a random declaration — a
discriminator, and claims about a handful of paths, outright and per kind — and 64 documents from **the
declaration**, not from the filter. From a stream of their own, so that the declaration's other claims and the
filter are drawn as they always were, it draws the characters a declared `pattern` would state, `enum`s and `const`s
of strings from all three bands, sixteen more documents holding such strings where the declaration lets one stand,
and conjuncts comparing a path with them by `=`, `IN` and an `OR` of the two (*Characters, a fact of the theory*). A
random filter over its paths is admitted with a random renderer, and every
scope the licence table names is checked on the declared documents its licence's conjuncts keep: the declaration,
the last stratum, the stratum each conjunct was rendered from, each leave-one-out child, a point read's pins.

**The ground truth is an evaluator of the IR, and it is not `PredicateAtom`.** `IrEval` takes a predicate to TRUE,
FALSE, UNKNOWN, or a raise, written from what Calcite was measured to do, and a checker that read the table would
only agree with it. It is held to Calcite's runtime itself (`IrEvalCalciteTests`): one statement per random predicate,
`CASE WHEN p THEN 'T' WHEN NOT p THEN 'F' ELSE 'U' END` over two hundred documents in a `VALUES`, and the documents it
says raise asked one at a time. The raw readings cannot be asked that way — a `VARIANT` is what the adapter's reader
makes of the service's JSON, and no expression over a JSON literal produces one — so where nothing was measured the
evaluator draws from what the table admits, a coin per question tossed once a trial. That tests the reasoning over
the table and not the table, which only a measurement can.

**What is checked.** Every fact of JSON a context holds, read row by row from its tables; every claim and
disequality its fact set answers a consumer; every verdict, of every term of every conjunct and of predicates the
statement does not hold; every `holds`, verdict and `possible` row; the facts each conjunct was handed to render
from, on the documents the conjuncts admitted before it keep; and that a conjunct called redundant drops no row the
rest keep. Two checks look at every declared document, kept or not, because what they check is a property of a term:
each `atom` row's meaning — the truth value the evaluator gives is among those `Over` names, and the document is among
those `Kept` admits for it — and C5, every e-node of the predicate, value and path sorts evaluated over its
children's representatives against its class's. Where nothing was ever unioned every class has one node and C5 has
nothing to compare; it is the only check that sees a union, and it is there for the day one is written.

**A raise is held at the atoms, not at the verdicts.** The connectives' rules do not model a raise, and whether an
answer may stand where Calcite fails is the refinement order still to settle; so a verdict is checked on the rows
Calcite answers, and the connectives decide first, as Calcite may: a conjunction with a FALSE arm is FALSE whatever
the other arm does. Whether a row raises is the table's to say, and the atom check holds it to say so.

**What it found**: two rows of the table that contradicted Calcite. An integer cast of the accessor is a parse of the
rendering, which raises on a stored `30.0`, and the table converted the number; `RETURNING DECIMAL` parses the
rendering, a string of digits included, and the table asserted a number. Both are fixed, and measured in
`CalciteJsonValueMeasurementTests`. Holding the evaluator to Calcite found two things the measurements had not
written down, and the evaluator follows both: the null test of a cast is simplified to its operand's where it stands
alone, so the parse that would raise is never made — beside other conjuncts it sometimes is, and those rows are left
out of the comparison as an order of evaluation is; and `RETURNING DECIMAL` is typed `DECIMAL(19, 0)`, so a literal
compared with it is cast to scale zero, `= 2.5` keeping a stored 2.

**What it is worth is what it catches.** `PlantedBugTests` plants ten faults, each in a graph of its own — a rule
added beside the right one, which for a rule that only adds is the same fault as replacing it; the arm split
scheduled without its guard; the renderer handed the wrong stratum; a declaration read wider than it was written —
and requires each caught within the trials a run pays for:

| planted | caught in trial | by |
| --- | --- | --- |
| a too-wide preimage: an equality with text decided true where the path holds only its preimage | 54 | a verdict |
| establishing from a weakening: a disjunction that holds read as each arm holding | 6 | a `holds` row |
| never-true read beneath `NOT` | 3 | a verdict |
| the arm split without its `holds` guard | 0 | a contradiction |
| an axiom row contradicting the measurement: `IS NULL` of the accessor false of an object | 18 | a fact |
| a stratum handing a conjunct its own facts | 0 | the strata |
| a union of a conjunction with its other arm where one arm is always true | 0 | C5 |
| a guarded characters claim read of every kind | 0 | a fact |
| a value's characters read without its surrogates | 9 | a fact |
| an arm's characters stated of its disjunction | 13 | a fact |

The C5 plant was caught in trial 60 until each trial drew sixteen more documents holding strings of either band
(*Reading a pattern as characters*), which see it in the first.

The three seams the plants go through are internal and set by nothing else: `PredicateRules.ScheduleWith` schedules
the rules around another arm split, `CosmosOracle.RenderedFrom` chooses the stratum a conjunct is handed, and
`CosmosStatementScope.ContextFor` gives a checker the context a licence reads, to read every row of it.

**What it costs**, in a Debug build on a shared workstation: 200 trials a run, which take 27 to 41 seconds, 110 to
150 ms a trial at the median and 350 to 560 at the 95th percentile; the plants another minute, most of it the two
caught latest; and the comparison with Calcite 24 predicates, 54 statements and 3,135 answers in 21 to 34 seconds. The
long runs are explicit (`-explicit only`), for a change to the rules, the table or the strata, or a Calcite or IKVM
upgrade: 5,000 trials took 16 minutes in Release on a shared machine — 4.8 million facts, 249,259 of them about
characters, 569,024 verdicts of which 469,452 decided something, 36,052 atoms and 222,347 e-nodes over 400,000
documents, and nothing false — and 400 predicates against Calcite four to six,
54,141 answers and 491 raises, and no disagreement.

#### A schema is one source of facts, and not the only one

What the service guarantees about the properties it maintains — `id` a string, `_ts` an integer,
`_etag` a string, each always present — is not delivered through any schema and is true of every
container. `CosmosServiceFacts` states it, `JsonSchemaFacts` reads a model's, and
`CosmosContainerMetadata.Facts` is the assembly.

**They have to share one theory rather than sit in two**, and that is correctness rather than tidiness:
a rule stated by one source may have a body satisfied by a fact from the other, and only a context
holding both fires it. Two theories asked separately would lose the derivation silently. So the service's
facts are stated into every container's own context, beside what its schema declares.

It also removed a duplicate. `CosmosAggregate` carried a hard-coded list of the three service
properties to decide whether a grouping key needs its `IS_DEFINED` normalisation; asking the fact set
answers for a schema's `required` paths too, which the list could never have reached.

#### Reading a schema: recognition, not inference

A `pattern` yields a stored form only by admitting a form's language, as the one reader of the
keyword reads it (*One reader of a pattern: a form is a language*). The tempting
alternative is to probe — generate a canonical sample and an uppercased one, and conclude from which is
accepted — and it is evidence rather than proof. The conclusion needed is universal, that *every*
string the pattern accepts is canonical, and two samples say nothing about the rest: a pattern with one
group left case-insensitive accepts the lowercase sample, rejects the fully uppercased one, and still
admits `123e4567-E89B-12d3-…`, which conforms to the schema and would be dropped by the equality the
probe licensed. An unanchored pattern fails the same way.

`format` yields nothing on its own: JSON Schema calls it an annotation rather than an assertion, and
RFC 9562 dropped the lowercase-output rule RFC 4122 §3 had.

**The audit, keyword by keyword.** Written down because the trap is uniform — a keyword that *looks*
like an assertion about a document is often an assertion about a document **if something else holds**.

| keyword | what it asserts | what is read |
| --- | --- | --- |
| `properties` | the value **if the path has one** | the value; nothing about presence |
| `required` | these children exist **if this object does** | presence, guarded by the parent's |
| `type` | the type, unless `nullable` or a union widens it | nothing for a union or under `nullable: true` |
| `const`, `enum` | the value if present; scalars only | value and domain |
| `pattern` | a regular expression | read once, under both of ECMA-262's readings: a stored form, where the strings it admits are a form's language; and the characters a string may hold (*Reading a pattern as characters*) |
| `format` | an annotation | nothing |
| `allOf` | every branch applies | every branch, same guard |
| `oneOf`/`anyOf`, discriminated | the branch the discriminator selects | that branch, guarded by the discriminator's presence and value; and the meet, unguarded |
| `oneOf`/`anyOf`, undiscriminated | one of them applies | the meet |
| `oneOf`/`anyOf`, `null` and one other branch | the other branch, or a null | that branch, each fact widened to admit the null |
| `if`/`then`/`else` | a conditional | a guard, only where the whole condition is read |
| `dependentRequired`, `dependentSchemas` | conditionals keyed on presence | the same, guarded by the trigger |
| `not` | a negation | one property under one `const` or `enum`; otherwise nothing |
| `$ref` | the target, and siblings too under 2020-12 | the target; siblings dropped, which is Draft 7's rule |
| `$defs`, `definitions` | a place to keep schemas | nothing — they constrain no path |
| `additionalProperties`, `patternProperties`, `propertyNames` | constrain paths that cannot be named here | nothing |
| `items`, `prefixItems`, `contains` | constrain elements | nothing |
| `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum` | bounds on a number | a bound, binding numbers only; Draft 4's flag beside `minimum` and 2019-09's number alike |
| `minLength`, `maxLength`, `multipleOf`, … | other bounds | nothing yet — the theory has them, and no claim states them |

An OpenAPI `discriminator` is read alongside a branch's own `const`, and the two rest on different
ground. A branch that pins the property refutes every other branch, and `oneOf` does the rest. A
`mapping` refutes nothing — validation alone could not tell the branches apart — so what carries it is
that the author declared it, which is the footing the whole schema is trusted on anyway.

**Two readings that look unsound and are not.** The guard taken from an `if` is the `if` exactly —
`{properties: {k: {const: "B"}}}` is satisfied vacuously by a document with no `k`, and a body atom about
`k`'s value holds vacuously where `k` is absent, as every claim about a value does. Its `else` is the `if`
failing, which needs `k` there and something else, so it carries the presence. And the meet holds under
`oneOf` and `anyOf` alike: a fact every branch states is true whether exactly one validates or at least
one does.

**A union with `null` is a third spelling of nullable, not a union.** `anyOf: [{type: null}, X]`
is how 2020-12 writes an optional value most often, and the meet reads nothing from it — a null and a
string share no fact. So where every branch but one admits only `null`, that one is read instead, and
each fact it yields is made true of the null too: a type takes its `OrNull`, a constant or a domain
gains `null` as a member, a disequality of anything but `null` is kept, and a stored form is kept
as it is, already being silent about whether a string is there. A fact that says something is
**there** — a `required` child, a `not`'s property, a geography of the union's value — is false of a null standing in for
the object, and `Present` cannot tell them apart since `IS_DEFINED` is true of a null; so those rules
are kept under two more conditions at the union's path, `Present` and `NotEqualTo(null)`: a disequality
alone holds of an absent path too, where no branch describes anything. Everything else below is a claim
about a value if there is one, and a null makes it vacuous rather than false. #172.

**The meet is taken by entailment, and a discriminated union has one too (#175).** It used to be the
claims every branch stated *equally*, so `"a string"` in one branch and `null` in another cancelled —
though both entail `"a string or null"`, which is one claim and not a disjunction, and both entail a
stored form, which says how the strings are written and nothing about whether one is there. So the meet
now offers candidates — each branch's claims, each type widened to admit a null, and at each path the
domain joining every constant and domain the branches name — and keeps every candidate each branch has a
claim entailing. All of them, not only the strongest: a consumer that asks for a type by its shape
rather than through entailment finds it beside the stored form that entails it. `JsonFact.Entails`
needed the rows saying so — a null entails a stored form, a type admitting a null, a domain holding one,
and every disequality but its own; and a domain entails every domain holding it, which is how an `anyOf`
of two enums meets in the enum of their members (gap 3, `AUnionOfEnumsIsTheirMembers`).

A discriminated union took only its branches, each guarded by the discriminator's value, which pays
only where a query proves one. A query over every kind proves none and got nothing — even where every
kind says the same thing, the canonical spelling of an identifier in the kinds that carry one and `null`
in the kinds that do not. The meet holds there for the reason it holds anywhere, some branch applying to
every valid document, so it is added unguarded beside the per-branch facts, the discriminator's own
values joining into the domain every kind is in. A schema can then state a form where it is true,
branch by branch, rather than on a nullable envelope above the union.

**A condition meets only the same condition.** Each branch's rules are compared by what they add to that
branch's own guard, so a `required` in every branch — conditional on its parent object being there —
meets the same `required` in the others, and a condition only some branches state meets nothing. Under
the discriminated union the guard includes the discriminator, which is what makes "what it adds"
comparable across branches whose guards differ. A union of which some branch was not read — one a
discriminator could not place — has no meet: it would be claiming for a branch it never asked.

**And one about resolution rather than a keyword.** A nested `$id` starts a new base URI, so a pointer
written under it names a fragment of *that* resource, and the same `#/$defs/x` reaches a different node
on either side of it. A bundle is built entirely from this — every resource embedded under `$defs` with
its own `$id`, every reference relative to the resource it sits in — which is what JsonSchema.Net's
`SchemaRegistry.CreateBundle` writes. Resolving a reference against the wrong base is the one failure
that yields facts about the **wrong path** rather than none, so a reference is never resolved by its
text. Every object is indexed with the base URI in effect where it sits — the document's own, or the
one the nearest enclosing `$id` sets — and a reference resolves against that: its resource is found by
URI, and its fragment followed as a JSON Pointer or looked up as an anchor. The cycle guard is keyed by
the target's position in the document for the same reason: in a bundle one text names different
targets, and different texts the same one.

**Resolution is written here, over `System.Text.Json`, and asks nothing of a library.** In-document
resolution is all the reader needs — `$id` base URIs, `$anchor` and draft 6/7's `"$id": "#name"`, JSON
Pointer, bundles — and it is a few hundred lines with `System.Uri` doing the URI half. What was weighed:

- **`com.networknt`**, through IKVM, resolved correctly once asked by position rather than by text, and
  did for a long time. It tied the fact theory to Jackson and to Java, which a theory meant to stand on
  its own one day should not be.
- **JsonSchema.Net** resolves bundles over `System.Text.Json`, and its NuGet binaries ship under an Open
  Source Maintenance Fee agreement — a monthly fee for revenue-generating users above a threshold, the
  source itself being MIT. A package depending on it passes those terms to everyone downstream.
- **`Microsoft.OpenApi`** does not resolve a JSON Schema `$ref` at all — given `#/$defs/x` it rebases the
  pointer onto the current node — that being OpenAPI's `$ref` rather than JSON Schema's. The BCL's own
  `System.Text.Json.Schema` is an *exporter*: a type in, a schema out.
- **`Corvus.Json.JsonReference`** (Apache-2.0) handles pointers and references and would take the URI and
  pointer half; the half specific to JSON Schema — which objects are resources, which anchors exist — is
  most of the resolver either way.

**Keyword names and property names share one namespace.** The previous resolver's first draft looked for
Draft 4's `id` and fired on every schema describing a property *called* `id`. Here an identifier counts
only where it is a string on an object, and the members that hold data — `const`, `enum`, `examples`,
`default`, and OpenAPI's `discriminator`, whose `mapping` is keyed by data values — are not walked. A
`properties.id` holds a schema, which is no string, so it is never read as one.

**Nothing is fetched.** A reference to a document that is not this one names no resource the index
holds, and resolves to nothing: no network call at schema registration, and only the facts behind that
reference are lost.

**The model arrives as Java maps and leaves as `System.Text.Json`.** Calcite reads a model file with
Jackson and hands the factory a `java.util.Map`; `CosmosSchemaFactory` writes the schema operand out as
text and parses it once, and from there nothing in the fact namespace knows Jackson was involved.

#### A guard exists to admit what a fact would have excluded

Which is the general statement of what a declaration is for here, and it applies to every guard the
adapter injects rather than to the one comparison the feature started with.

A weakening pushes what a predicate *implies* and admits everything it cannot reason about, so that
the recheck above decides those rows rather than the service dropping them. That admission is a
guard, and every guard names a set of values. A fact that says the path holds no such value makes the
guard admit nothing that exists, and it comes out.

| guard | injected to admit | deleted by |
| --- | --- | --- |
| `NOT IS_STRING(x)` | a value of some other kind | `OfType(String)`, nullable or not |
| `NOT IS_NUMBER(x)` | a non-number | `OfType(Number)` or `OfType(Integer)` |
| `IS_DEFINED(x)` | nothing — it *excludes* an absent path | the type guard going, or `Present` |

The definedness test is the one worth reading twice. It is not there to admit anything: it is there
because the disjunction it guards is *true* of an absent path — `IS_STRING` of nothing is false, so
the negation is true — and without it the weakening would admit every document lacking the property.
So it exists for the type guard's sake, and dropping the type guard drops the need for it. A declared
`required` drops it independently.

**Which facts delete one is the oracle's licence table.** A weakening is pushed beside every conjunct the
strata admitted, so its guards are deleted by what those establish beside the declaration — the last stratum,
which the conjunct it weakens is never in. A guard a sibling left above would have proved stays: the service would
decide rows that sibling exists to exclude.

**What this buys is not a shorter statement.** Each guard admits documents the service returns for the
recheck to throw away, so deleting one is fewer documents over the wire. And a conjunct that needed
none of them is *exact*, which is what lets a row limit travel with it — a weakening cannot carry a
`FETCH`, because the recheck above may discard what the page already counted.

**It pays without a schema.** `id`, `_ts` and `_etag` are typed and always present on every container
by the service's own guarantee, so a comparison over one of them is exact today whether or not anyone
has described anything. The declaration extends the same mechanism to an application's own paths
rather than introducing it.

**And the numeric bound stays a bound.** Knowing the type deletes the disjunct that admitted
non-numbers; it does not make the comparison exact, because the cast still *converts* and the window
is still the whole unit either side that `Casts over document values` argues for. Two different
things are being weakened there, and only one of them is a guard.

#### A form reads back as well as it compares

Every use above is a *predicate*: the fact says the stored string is a faithful stand-in for the
value, so a comparison written against the string answers what a comparison against the value would.
The select list asks the mirror question — not which documents match, but what each one returns — and
the same fact answers it, because a spelling that is exact enough to compare is exact enough to parse.

The case that forced it is `CAST(JSON_VALUE(doc, '$.ref') AS UUID)` in a projection. Without a form
there is nothing to send: the service holds a string and the plan wants a `UUID`, and no Cosmos
expression converts one to the other. With one, the statement sends the guarded path and
`CosmosJson` reads the text back as `UuidValue` — through `UuidValue.fromString`, which is the method
`BuiltInMethod.UUID_FROM_STRING` names and therefore the one a cast Calcite generated itself calls, so
the pushed projection and the in-process cast it replaced are not two implementations that could
drift.

**The box is `UuidValue` and not the `java.util.UUID` inside it**, and reading the inner one was wrong
in a way only a narrow row could see. CALCITE-7716 moved the representation of a `UUID` onto
`UuidValue` — it is what `JavaTypeFactoryImpl` answers for the type and what a `RexLiteral` holds —
but left `SqlFunctions.stringToUuid` returning the bare `UUID` it always returned, which is the value
the wrapper wraps. Calling the inner function therefore produced the right value in a class the plan
does not use. A row of two columns or more is an `object[]`, so the row builder boxes each column
rather than casting it, and the wrong class survives to a reader that converts either one; a row of
**one** column is the value itself, and `CosmosConverters.RowBuilder` casts it to the field's physical
type. So `SELECT <a lone UUID column>` threw `java.util.UUID cannot be cast to
UuidValue` at the first row while the same column beside any other read fine — which is #150, and why
it surfaced through a `$count` and an `$expand` rather than through an ordinary page.

**Measured, and below this adapter.** `CalciteUuidReadingMeasurementTests` puts each of the two
classes into a `UUID` column of a plain in-process table and reads it through `CalciteDataReader`:
a `UuidValue` arrives as a `System.Guid` at either arity, and the bare `java.util.UUID` arrives as
one at two columns and raises exactly the reported cast at one. So the defect is Calcite's
convention rather than anything about Cosmos, the `Guid` a caller maps is what the right box buys,
and a change to `stringToUuid` upstream is reported there. `ShouldReadALoneUuidColumn` is the same
shape through the adapter, over a pushed statement.

**What it costs to leave a cast untranslated is not the cast.** A projection that does not convert
stays in the client, and a sort cannot be pushed through one — `SORT_PROJECT_TRANSPOSE` has nothing to
transpose below. So a query selecting an identifier and ordering by a name read the container whole
and sorted it in memory, while the identical query *without* the identifier pushed both the `ORDER BY`
and the page. Every query that returns an entity selects its identifier, so the untranslated cast was
paid for by the whole sorted-list path rather than by the one column.

**The guard is the accessor's, not a new one.** `JSON_VALUE` answers null for an object, an array and
an absent path; the bare path answers the object. `IIF(IS_PRIMITIVE(p), p, null)` is that distinction,
which is already how a bare accessor is projected, so the two sides agree for every JSON type —
including a document that contradicts the declaration, where both answer null. A stored scalar that is
not a UUID is the other case, and there the two agree by both failing: reading refuses it exactly as
Calcite's cast does, rather than answering a null the query would read as a missing value.

**Equality is the whole of what is asked.** A cast resolves to no path, so the projected column binds
to none and nothing above it orders, groups or filters on it. Whether the lexical order of the stored
strings is the order Calcite compares the values in is therefore never consulted, and the sortable
forms are admitted here on the same terms as the rest. Ordering *by* such a column needs the column to
address something, which is a different piece of work.

**And a fact of the rows the projection reads.** A projection carries no predicate, so it discharges no
guard of its own; the filters beneath it can. What their pushed conjuncts establish holds of every row the
projection reads (L1, *The oracle in the planner*), so where a form is declared inside a branch and the
filter proves the branch — `kind = 'B'` beneath `CAST(ref AS UUID)` — the column is the stored spelling,
guarded as any accessor is, and the reader makes the `UUID` of it rather than Calcite in process. A filter
keeping another kind, or none, leaves the cast above. `CosmosDifferentialTests` holds both to Calcite over
the kinds container (`CosmosPushdownProbeTests.ClauseProbes`).

#### A declaration is trusted, and that is a change in kind

Every other row of *What a container declares* is sourced from the container definition or a service
guarantee. A schema is neither, and it is trusted the way the partition key is trusted: a document
violating it is a data-integrity problem rather than something defended against per row. So this is the
first input anywhere in the adapter that can silently change **which rows a query returns** — a schema
wrong by one character drops rows, with no error and a plan that looks correct. There is no cheap
detection, since checking would mean reading the documents, which is the inference
`CosmosContainerMetadata` exists to refuse.

**Which makes completeness the caller's obligation and not merely accuracy.** An unconditional fact is
a claim about *every* document in the container, so describing only the kind you query is not
under-describing — it is mis-describing the rest. A container holding several kinds has to say so, with
`oneOf` and a discriminator or with `if`/`then`, at which point a fact inside a branch is used only
once a query has proven the branch applies. Being incomplete in the other direction is free: keywords
not understood are ignored, an unrecognised pattern yields no fact, and a schema that cannot be read at
all leaves the container working exactly as it did.

**Where a guard is enough, and where it is not.** A fact may be conditional, and the conjunct that
proves it is usually a sibling of the one it licenses. For a filter rewrite that is enough on its own:
the guard conjunct stays in the predicate, so over a document the fact was never claimed for, it has
already excluded the row. For anything decided before a row is seen — a sort, routing, a point read, a
pushed `LIMIT` — nothing rechecks, and the condition becomes that the statement's *pushed* predicate
entails the guard. So a clause after a filter reads the filter's last stratum, what the admitted
conjuncts establish — those the service applies — and never a residual's; routing reads every conjunct
of a filter the service applies; and a point read reads only its pins (*The oracle in the planner*).

#### A constraint says what holds across documents

A fact is a claim about one document. Some things worth knowing are claims about every *pair* of
documents — no two share a value at a path — and no single document can satisfy or violate one, so they
have no place in a theory of per-document atoms. #177 needed one: whether a join of a container to
itself pairs a document only with itself. `CosmosConstraintSet` holds them, beside the fact theory and
assembled the same way, from several sources and asked in one place.

**Written as SQL DDL, with Calcite SQL for the expressions.** `UNIQUE (k₁, …) [WHERE p]`: among the
documents `p` admits, no two share the values of the keys. The grammar of our own is only the part around
the expressions. The keys and the predicate are compiled as `SELECT k₁, … FROM container WHERE p` — parsed,
validated against the container's row type, and converted by Calcite — so they arrive as expressions over the
same scan a query reads, in the dialect a model's views are already written in.

Two other languages were tried and dropped, and why is worth keeping:

- **An object from path to value** (`{"/type": "Link"}`) was a grammar of its own that could say only
  equality and membership, and it spelled paths differently from the views beside it.
- **A JSON Schema fragment** was the language a model already writes, but it cannot relate two paths. That
  rules it out for `CHECK (data.id = linkId)`, and for any predicate written in terms of a relation. It also
  compiles to facts in the wrong direction for a filter: the schema compiler drops what it cannot read, which
  makes facts weaker than the schema. That is safe for a guarantee, and would make a filter wider than
  written.

**What a key means.** A plain accessor — `JSON_VALUE(DOC, '$.x')`, or a promoted column — stands for the
value *stored* at its path, which is what the service enforces and what a model usually means. Any other
expression stands for its own value: `UNIQUE (LOWER(JSON_VALUE(DOC, '$.email')))` says two emails differing
in case are one. A document for which a key is null is outside the claim rather than against it — a null
equals nothing — which is what makes a constraint over an optional path still mean something.

| source | constraint | stands behind it |
| --- | --- | --- |
| Service | `UNIQUE (pk…, id)`; `UNIQUE (id)` where there is no partition key | the service, on every write |
| Container definition | `UNIQUE (pk…, paths…)` per unique key policy entry | the service, on every write |
| Model | whatever `constraints` declares | nothing |

The derived ones are written in the same DDL, with accessors, so every constraint prints as what it says and
is compiled the same way.

**Every constraint is stated across the whole container.** `id` and a unique key policy are unique within a
logical partition, so the source adds the partition key paths rather than leaving every consumer to remember
which constraints are partition-scoped. Uniqueness within a partition is not something a planner can use
alone, and stating it as though it were invites that mistake. Every path of a hierarchical key is added: two
documents agreeing on a prefix may be in different partitions. Where the partition key is `/id` the
service's constraint is `UNIQUE(id)`, the two sets being one.

**A partition key that is absent or null is no exception, and that was checked rather than assumed.** Both
read as SQL null, and a null equates nothing — so a join over `pk + id` never pairs two documents through a
null partition key, whatever partitions they sit in. Measured against the emulator, absent and null are
*one* partition there: a document `A` with no partition key value is refused, 409, after a document `A`
whose partition key is null. Whether Azure keeps them apart is not measured, and nothing depends on it.

**A predicate scopes the claim, and a consumer has to prove it of every document it compares.** A
constraint on `linkId` among Links says nothing about a Link and a document of another kind sharing a
`linkId`, so a join can use it only where *both* sides' predicates imply it — proving it of one side is not
enough. Implication is the oracle's: the constraint's predicate decided `Always` in each side's statement,
every conjunct of the side's filter assumed beside the declaration (`CosmosFilterStatement.Proves`), and in
the declaration alone for a side with no filter. The side's filter and the predicate are read through one
loading, so a term they share is one term whether the oracle can take it apart or not, and a view writing the
very predicate the constraint names proves it before any run; `type = 'A'` proves `type IN ('A', 'B')` by
what `type` may then hold, and a container declaring every document of kind `k` proves `kind = 'k'` of a side
that says nothing of it. Calcite's `RexImplicationChecker` decided it before, each distinct accessor term
stood for by a column of its own, and knew nothing a declaration says. A predicate the oracle cannot prove
leaves the constraint unused for that query, which costs a read and never a row; one that does not *compile*
is refused when the model is read.

**`UNIQUE` contributes no facts.** It says nothing about any one document. `CHECK (data.id = linkId)` —
a constraint relating two paths of one document — would: it is checkable per document, the same trust as a
schema, and it would both carry facts across paths and widen what a join equates. It is refused by name until
it is read; see `TODO.md`.

**A declared constraint is a stronger claim than a schema.** A schema says what each document holds and can
be checked one document at a time — the write path can validate against it. A `UNIQUE` constraint is about
every pair, and checking it means reading every document. A wrong one pairs two documents as one document
with itself, and the rows the second contributed are dropped, with no error and a plan that looks correct.
So the model names constraints in a block of their own, refused rather than ignored where it says anything
this does not read: a constraint silently dropped is one the caller believes is in force.

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
> | `SUBSTRING`, `LEFT`, `RIGHT`, `INDEX_OF` past U+FFFF | code points | **UTF-16 units**, a split pair sent as U+FFFD |
> | `UPPER`/`LOWER` of U+017F, U+0130, U+212A | mapped | **left** |
> | `TRIM` of U+0085, U+00A0, U+180E, U+2007, U+202F | kept | **removed** |
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

**Every measured fact a pushdown relies on is a row in `Sql/CosmosAxioms.cs`.** Each row has an id,
the statement, the targets it was measured on (emulator, Azure, or Calcite's own runtime), the date,
and the test that pins it. `CosmosAxiomsTests` requires every named test to exist, so a row cannot
outlive its evidence. Two reasons:

- **The facts were spread out, and two of them disagreed.** They sat in translator remarks, in this
  document and in the measurement classes. `IS_PRIMITIVE` over an absent path was false in the
  translator and undefined here, and false is what the emulator answers. The translator's comment on
  `LOG(0)` said the service accepts it; the test beside it, then and now, says it fails the
  statement.
- **A wrong axiom is wrong everywhere it is consulted**, so the places should consult one list.

Nothing reads the registry yet. A rendering that rests on a row should name its id beside the
measurement it already cites, until the translator reads the rows itself. The registry is also a
record of what was run, not of what is believed: a row measured on the emulator alone is owed on
Azure, and `TODO.md` section 10 lists them. Where the two targets disagree, the reading to encode is
the conservative one, a capability holding only where both accept it.

Four rows say something other than what was expected when they were measured, and are worth reading
before writing against them:

- **A comparison across types is not uniformly undefined.** An ordering (`<`, `>`) across types is
  undefined. An equality across types is *false*, and so `!=` is true, over a null as over a string
  compared with a number. Only over an absent path is an equality undefined. That is why
  `NOT (p = x)` keeps a null at the service and not in process.
- **The `IS_*` family never answers undefined**, `NOT` over one included.
- **`ReadManyItemsAsync` answers an id asked twice once.** It still answers two documents equal in
  every value as two, and applies no `DISTINCT`.
- **`UPPER`, `LOWER` and `CONCAT` are undefined over every non-string**, the guarded accessor
  included. A null is undefined too, not null.

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
its `x-ms-resource-usage` header — on Azure only when the read asks for it, with
`ContainerRequestOptions.PopulateQuotaInfo`, which the emulator does not require, so until #209 no
Azure account ever answered — and immediately after writing four documents it reports **zero** —
the statistic is computed in the background. That is what the row count in `getStatistic` is, and it
is what a planner row count is allowed to be: approximate and stale. It is not what the rule against
inferring from documents forbids, which is about the *shape* of the data — a wrong shape yields an
incorrect plan, a wrong row count yields a slow one.

The partition count is answered immediately, being a fact about the container rather than its
contents.

**And the emulator never reports one.** `documentsCount` is zero for every container it holds — zero for
`links` holding 200 documents that had been there for days. So every plan the emulator measures is planned
at zero rows, unless something stops it.

**Zero is the one count that is worse than none, so it is reported as unknown.** Volcano compares row counts
and nothing else. At zero rows every plan costs the same, and the tie goes to whichever registered first. Measured
through a connection, that was the plan that pushes no part of a split predicate:
`type = 'Link' AND label SIMILAR TO 'M%'` read the container whole. `CosmosFilterSplitRule` produced the split
and `CosmosFilterRule` converted its pushed half, and the planner chose neither. Under the planner the other
tests use, with no count, the same query split, and it split at 5 rows and at 1000 — it lost only at zero and
one. A container that is really empty costs nothing to read whichever plan wins, so `getStatistic` reports a
zero count as unmeasured and lets the planner fall back on its own default. A count of one still ties; a
container of one document is not one where a plan matters.

It looked like a defect of partial pushdown through a connection, and was not: every measurement of it
was taken on the emulator.

**Out-of-domain arithmetic fails the whole query.** `ASIN(2)`, `ACOS(2)`, `SQRT(-1)` and `LOG(0)`
each return a 400 rather than yielding undefined for the offending row. Calcite evaluates all four
as NaN, so pushing any of them down trades a row of NaN for a failed statement — over data no schema
lets the adapter check first. They are pushed anyway, `SQRT` and `LOG` always having been, and this
is recorded so that the consistency is a decision rather than an oversight.

**Integer overflow is computed, not wrapped.** The service's arithmetic is in doubles, so
`price * 100000000` is the product, where Calcite's `INTEGER` arithmetic wraps as Java's does and SQL
raises. A filter over it keeps rows the engine does not, and a projection of it fails the read, the
product not being the `INTEGER` the plan declared. Pushed anyway, for the reasons under *A quotient is
a double at the service*, and recorded here beside out-of-domain arithmetic for the same reason that
is.

**A number sent with a point is read as a double, and a stored integer is kept as one.** The SDK writes
a `decimal` parameter with a point, and every exact literal was bound as one, so every exact literal
reached the service as a double; a document's integer stays the integer it is, and the service compares
the two exactly. Measured over the differential corpus's `declared`, whose `v` holds 2^53, 2^53 + 1,
2^60 and 2^60 + 1: `RETURNING BIGINT = 1152921504606846977` matched the document holding 2^60 and not
the one holding the literal, `= 9007199254740993` matched 2^53, and an `IN` of the two matched both
wrong ones. A double that is the number is exact against a stored integer: `> 1152921504606846976`
keeps 2^60 + 1, and `= 9007199254740992` keeps 2^53 alone. The bounds a cast's comparison pushes fared
the same (*Casts over document values*): loosened by one around 2^60, each came back as 2^60, and the
window kept nothing.

Nothing stored lies between a number and the nearest double but that double, and through a typed
accessor Calcite tells that double from the number only where it is whole — `RETURNING DOUBLE` rounds
the literal as the service does, and an integer is read exactly. So a decimal is wrong exactly where its double is a whole number
other than the number: an integer beyond 2^53 a double does not hold, and a fraction a hair from a whole
number, `2.9999999999999999`, which reads as 3. `CosmosRexTranslator.TryGetExactNumberValue` sends a
decimal everywhere else, a `long` for a whole number there, and nothing for the fraction or for a whole
number beyond a `long` — the literal is declined, and a bound not stated. A parameter's value arriving
with the execution binds the same way, and fails the execution where nothing serves, the plan having
been made to send it. Ten statements in the corpus answered wrongly before.

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
`WHERE t IS NOT NULL ORDER BY t` over a path declared a string pushes in both directions where
`ORDER BY t` alone is refused.

That default is a connection setting rather than a fact, and the other value worth knowing is
`LOW` — nulls first ascending, last descending, which is Cosmos's own order and SQL Server's. A
connection set that way asks for what the service already does, so the placement never conflicts and
no predicate is needed. It is a property of the connection and not of the schema, so it changes what
`ORDER BY` means for every schema on it, which is why it is guidance rather than a default the
adapter could set — see the user manual, `docs/09-sorting-and-paging.md`, which carries the
measurements and the trade.

Two things make it sound rather than merely plausible. **Both senses of absent go.** Cosmos
distinguishes a property holding JSON `null` from a property that is not there, and sorts
`undefined` below `null` below everything else — so excluding only one of them would leave the
other to arrive first ascending and the guarantee would be false. What the placement asks of a key is
that its null class lies on the side Calcite puts nulls, which a key that is there and holds a scalar
settles by having none (`CosmosServiceOrder`, R3). And **the
predicate and the ordering leave as one statement**, so the guarantee holds at the service and not
only in the plan: it is read off the conjuncts the service applies.

**The guarantee is the filter's last stratum (L1).** `WHERE` runs before `ORDER BY`, so what the
pushed conjuncts establish holds of every row the sort orders, and the oracle reads it off the filters
beneath the sort (`Metadata.CosmosRowFacts`) through whatever projects the key. `IS NOT NULL` says the
path is there and not null; so does a comparison over its text, an atom that takes a truth value only
where the path holds a scalar — `WHERE t > 'A' ORDER BY t` pushes over a declared string, where the
wider form used to wait on evidence that Cosmos's undefined for a comparison across types drops the
row. `CosmosDifferentialTests` holds both, a typed key under `IS NOT NULL` and a page of each, to
Calcite on the emulator (`CosmosPushdownProbeTests.ClauseProbes`). What stops a key then is its
order: the text of a path not declared a string orders as text in Calcite and as the value at the
service (*Ordering a sort key the way it reads*).

**It used to reach promoted columns only.** The guarantee was read from `RelMdPredicates`, which
carries a predicate through a projection only where the projection is a `RexInputRef`: a document path
projects as an accessor call, over an input the projection does not output, and the predicate was
dropped. Measured, at rule-firing time, against a live planner:

```
WHERE c."category" IS NOT NULL        →  {pulled[IS NOT NULL($1)]}
WHERE JSON_VALUE(c."DOC", '$.name') IS NOT NULL    →  {}
```

Once a promoted column was a `VARIANT`, which no sort pushes (#165), it reached nothing at all. The
oracle reads the path the filter reads whatever projects it, so a key needs no column.

**The guarantee is taken once, in the rule, and carried on the node.** Which filters a walk of the
input finds depends on which member of a set it reads, and implementation reads the one tree the
planner chose: the pulled-up predicates were measured to answer while the input was logical and with
nothing once it had been converted, and a walk can meet a filter spelled another way. Re-deriving it
during implementation would therefore throw on a plan the planner had already chosen, which is the
rule-and-renderer disagreement `CosmosSortRule` exists to prevent. So the rule hands the node the
non-null fields and the facts they came from (`CosmosSort.Facts`), and every question it asked — the
placement, a computed column's ordering licence, a key read as stored, a temporal key's lexical order —
is asked again of them. Carrying it is sound because every member of an equivalence set produces the
same rows.

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
already-collated input and `ClrCursorSort` does not override it. So the alternative is
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
| `CosmosSort` | `CosmosSortRule` | ✔ Carries `OFFSET`/`LIMIT`. Blocked if aggregation present. Multi-key sorts require a matching composite index; null placement must be honourable; each key must be read as the value it orders by — a text accessor only over a declared string, a typed accessor only over a path declared its type, and never a fragment, the document or an array (*Ordering a sort key the way it reads*). |
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

**Which clauses a subtree has written is half of what a rule decides on; what its output binds is the
other half, and a `DISTINCT` is where the two halves were encoded twice.** A `DISTINCT` is a projection
of its own keys, and over a key some documents lack it projects `IS_DEFINED(p) ? p : null` — SQL's one
null for the service's absent and null (see `CosmosAggregate.GroupingKey`). That value is not the path:
the service will not order by a select-list alias, and the path sorts an absent property somewhere the
projected null does not. So `EmitDistinct` binds such a key to nothing, and binds the path only where
the container says it is always present. `TryBindOutput` bound the input's path regardless, so
`CosmosSortRule` converted a sort over `SELECT DISTINCT JSON_VALUE(…, '$.price' …) … ORDER BY 1 NULLS
FIRST`, and `CosmosSort.Implement` refused it with "the sort keys do not resolve to document paths" —
after the planner had chosen the plan, so the query failed rather than sorting in process (soundness
probe P16). Each half was right about its own encoding. Both now ask
`CosmosAggregate.DistinctKeyBinding`, and the walk finds the container on the way down, at the scan,
because the answer depends on what the container declares.

The walk may know *less* than implementation and never more: where it binds nothing a rule declines,
which is always correct, and where it names a path implementation does not bind a rule fires and
implementation throws. `CosmosRelTests.TheWalkBindsNoMoreThanImplementation` holds every shape the
walk knows to that.

**The same holds of every walk a rule decides on and a node asks again, and the binding walk is not
the only one.** A walk that knows the logical form of a node and not its converted form answers for
the rule and declines for the node — and where the node asks at implementation, declining is a
throw. An array traversal is the node with two forms, the `Correlate` a rule sees and the
`CosmosUnnest` implementation sees. The binding walk learned the second (cause K), and the lookup join
rule's own walks did not: the container a text key's declaration is read from, and the cast a
`UUID` key is restricted through, were found by walks that knew only the `Correlate`, so a text key
over a declared string and a `UUID` key through a traversal still planned a lookup `Prepare` then
refused (review of the K fix, reproduced on the stub executor). Every walk now sees through a
traversal by `CosmosImplementor.TraversedInput`, and `TryProbeKey` takes the container from the
binding walk itself rather than from a second walk that could reach a different answer. The facts it
asks of are the rule's, carried on the join (`CosmosLookupJoin.Facts`): what the probe's filters
establish, which a walk reads off whichever member it finds.
`CosmosLookupJoinTests.ADeclaredKeyOverATraversalIsRestrictedAsTheRuleDecided` asks `TryProbeKey` of
the logical and the converted subtree and holds the two answers equal.

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

#### A prepared statement's value belongs to the execution

A host that compiles a statement once and runs it many times writes `?` where a value goes, and
Entity Framework does it for every filter and for every `Take` — a literal `Take(3)` included, so
that one plan serves every page. So `RexDynamicParam` is the ordinary shape rather than an exotic
one, and the adapter used to meet it twice and badly: the row limit asked for its count with
`RexLiteral.intValue`, which asserts, so a parameterised `FETCH` planned happily and threw when it
ran; and a comparison against one was declined, so the split rule weakened it to a definedness test
and rechecked it in process.

**Both were asking for a value the plan was never going to have, and neither needed one.** Every
decision made while a statement is built is a decision about *types* — whether a comparison is exact,
whether a guard is wanted, whether a limit is an integer. The value decides nothing here. So a
dynamic parameter is written exactly as a literal of its type would be, bound through the same list
under the same kind of name, and the slot behind the name is closed by `CosmosQueries.Bind` out of
the data context the execution supplies.

**Every path to the service closes them, the lookup join's included.** A lookup's statement is the
probe side's filter with the keys appended per batch, and its slots are closed once, at the join's
open, before any batch is built. They were once left open there: the slot itself was serialized as
the value, a comparison against an object is never true, and a lookup filtered on a parameter
returned no rows and no error (#219). A lookup whose filter held only literals was unaffected, which
is how it went unseen.

**The service takes it.** Measured against a container: `OFFSET @skip LIMIT @take`, the same with an
`ORDER BY` above it, and `SELECT TOP @take` all run. So nothing about the statement text changes for
a limit that is not a constant, which is the property that makes one plan serve every page.

| | |
|---|---|
| `FETCH NEXT ? ROWS ONLY` | `OFFSET 0 LIMIT @p0` |
| `OFFSET ? ROWS FETCH NEXT ? ROWS ONLY` | `OFFSET @p0 LIMIT @p1` |
| `WHERE c."id" = ?` | `c.id = @p0`, exact, nothing rechecked above |

**The page size is the one thing that cannot be known, and it does not need to be.** `MaxItemCount`
is a hint about how many rows come back per round trip; what bounds the result is the `LIMIT` the
statement carries. A limit the plan does not have therefore asks for the service's own page rather
than for a page it cannot size.

**What degrades without a type, and why that is the right place for it to degrade.** A bare `?`
compared against a document path is still weakened, because the accessor renders every JSON scalar as
text and an equality against text is exact only where the text is one no number and no boolean
renders as — a fact about the *value*, which a parameter has none to inspect. A consumer generating
SQL should cast its parameters, at which point Calcite knows the type and the plan can reason from
it; a consumer that does not gets correct rows and a weaker statement. That is a bug in the consumer
rather than a gap here, and `TODO.md` records the one case a declaration ought to recover.

**One comparison needs the value written rather than bound as it stands: an instant against a stored
form (#182).** Against a literal, a comparison of a path read as an instant is lowered while the plan
is made — `CosmosFactRewriter` writes the literal in the spelling the container declares, and the
service compares stored strings. Against a parameter it declined, the parameter being no literal, so
`WHERE "ChangeUtcTime" > CAST(? AS TIMESTAMP)` — a "changed since" refresh, sent parameterised by every
host that prepares — pushed a definedness test and compared every document in process. Nothing in the
decision needs the value: whether the stored strings answer for the instants is a question about the
path and the operator, and `CosmosFactRewriter.TryStoredInstant` answers it for the literal and the
parameter alike. So the translator writes `c.at > @p0` and binds the slot with the path's form;
`CosmosQueries.Bind` writes the value in it when the statement runs, as the plan would have written the
literal.

**A UUID against a declared form is the same move (#207)**, and it was the costlier gap: every Entity
Framework and OData key filter parameterises its id, so an id lookup over a declared UUID path read the
container whole — 448 MB for one document, measured on an Azure container of 11,868. The decision is
`CosmosFactRewriter.TryStoredUuid`, which the literal lowering and the translator both ask; the slot
carries the UUID form, and `CosmosQueries.Bind` spells the value into it from whichever box it arrives
in. One difference from the literal: a literal outside a confined form's sign class is refused under
the signed comparison, and a parameter cannot be. An equality needs no class, since a spelling no
stored value has matches nothing. So only an ordering is held back, to where the order is the
engine's for every value (`CosmosUuidForms.OrdersEveryValue`).

**The value written is the one Calcite's runtime holds, and that was measured rather than assumed.**
Through Calcite's own driver and through the ADO.NET one alike, a `TIMESTAMP` parameter reaches the
data context as a `java.lang.Long` of epoch milliseconds — a .NET tick is gone on the way in — and a
`DATE` as a `java.lang.Integer` of days (`CalciteTemporalParameterMeasurementTests`). And
`CAST(? AS TIMESTAMP)`, which is `TIMESTAMP(0)`, keeps the milliseconds at run time: only its text is
written to the declared precision. That sits against the section on casts with a format, which refuses
a millisecond parse into `TIMESTAMP(0)` so that the pushdown agrees with the declared type rather than
with the runtime. The two are not in conflict. Refusing leaves the answer where it was, which is
always available to a plan and never to a parameter's statement once written; and between writing the
value the runtime compares and writing a truncation of it, only the first leaves every query answering
the rows it answered before it was pushed. If a release ever applies the precision at run time, the
measurement fails and this has to follow it.

**A value between two stored spellings cannot be refused, so it is rounded — exactly.** A literal
finer than the form keeps its comparison in process. A parameter's statement is already written, and
against a seconds form a millisecond value is one Calcite's runtime can hold. Every stored value lands
on the form, though, so nothing is stored between two consecutive spellings, and the comparison
decides which neighbour the value may stand in for without changing what it keeps: `s > v` keeps what
`s > ⌊v⌋` keeps, `s >= v` what `s >= ⌈v⌉` keeps, and the mirrored pair the same way. An equality is
true of no stored value and an inequality of every one, which the value written in full gives both, at
a precision no spelling of the form has. The direction is fixed when the statement is written —
`CosmosTemporalRounding` on the slot — and the value only decides how far. Against a form of
millisecond precision or finer, which is the case #182 reported, every value lands and the rounding is
never consulted.

| comparison | `12:00:00.500` against a seconds form is written as |
| --- | --- |
| `>`, `<=` | `12:00:00Z` |
| `>=`, `<` | `12:00:01Z` |
| `=`, `<>` | `12:00:00.5000000Z`, which no stored value equals |

**A null is the one value the type does not decide, and the statement tests for it.** A SQL `NULL`
bound to a parameter arrives as a JSON null, and the service compares a JSON null as a value: `c.t =
@p` is true of a document storing null, `c.t != @p` of every other, `c.t <= @p` of a stored null too, and
an `IN` or a `NOT IN` naming the parameter likewise. SQL's comparison with a null is unknown, whatever the
other side holds, and keeps nothing. The guards `WriteComparison` writes were about a null at the *path*,
in the position each operator needs; a parameter is now guarded the same way, in the positions its
operator would keep a row for the JSON null: `NOT IS_NULL(@p) AND` before `=`, `!=`, `<=` and `>=` in a
positive position, and `OR IS_NULL(@p)` after `=`, `!=`, `<` and `>` under a `NOT`. Where it is not null
the test is false, or true, and the comparison decides as it did. `<` and `>` against a JSON null are false
at the service and `<=` and `>=` true, and against any other type each is undefined, which a `WHERE` drops
in either position: so those need nothing where they stand. Measured on the emulator over the
differential's `ordering`, whose `t` is declared a string or null, and held there by its parameter rows:
without the test eleven of them answered rows Calcite does not, from `t = ?` keeping the stored null to
`t NOT IN (?, 'apple')` keeping every other. `CosmosServiceOrder` reads a parameter's null class as empty
for this reason, the statement never comparing a null one. What the test costs on Azure, a constant the
service may or may not fold before it picks an index, is not measured (`TODO.md`).

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

**Undecorated, as the projection's is.** A width is a second conversion on top of the rendering —
`VARCHAR(3)` truncates and `CHAR(8)` pads, measured below — and the comparison side took the cast
whatever its width, where the projection side had always refused one. Measured over `typed`,
`CAST(label AS VARCHAR(3)) = 'bik'` was dropped to `c.label = 'bik'` and lost the stored `bikes`, which
Calcite truncates and keeps; and because the fact and partition-key extractors read a value through the
same recogniser, the same statement over `category` routed the read to a partition named `bik`, which
holds nothing. `TryTextCastValue` now asks for a `VARCHAR` with no precision, and the translator and the
oracle's loader, which every fact and every pin is now read through, stop there.

**The same cast over `JSON_VALUE` is the same cast.** A view over `DOC` writes
`CAST(JSON_VALUE(doc, '$.x') AS VARCHAR)`, whose operand is not typed `ANY`: Calcite types a
`JSON_VALUE` without `RETURNING` as `VARCHAR(2000)`. Measured against Calcite's own runtime, the
accessor renders what the cast over `ANY` renders — `30` as `30`, `1e30` as `1.0E30`, `true` as
`true`, a string as itself — and answers null for an absent path, a null, an object and an array,
which the comparison then does not keep; and it applies no width at run time, `RETURNING VARCHAR(3)`
returning `'bikes'` whole. So the argument above holds for it unchanged, and
`CosmosRexTranslator.IsRenderedDocumentValue` admits the two-operand `JSON_VALUE` of a character type
beside a value typed `ANY` (#71). A `RETURNING` that converts is refused, since the cast then renders
a converted value; `JSON_QUERY` is refused, being the JSON text of an object and null for a scalar;
and a behaviour clause is refused, substituting a value where the path has none — a refusal that,
until `IsPlainJsonValue`, held in this test and in no other; see *`JSON_VALUE`'s behaviour clauses*
below.

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
rendering: a number-like literal takes the parsed number — the whole number it spells, as a `BIGINT`,
past where a double holds every whole number, since the service keeps a stored integer exactly and the
double `'9007199254740993'` rounds to is another number there (measured on the emulator: it lost the
document storing 2^53 + 1) — or `IS_NUMBER` where the double cannot hold it; exactly `true` or `false`
takes the boolean, since Calcite renders in lowercase and `'TRUE'`
matches only the string; and over a value typed `ANY`, whose cast renders an array as `[x, y]` and
an object as `{x=1}`, a literal opening with that bracket takes `IS_ARRAY` or `IS_OBJECT`. `JSON_VALUE`
answers null for those and for a JSON null, so under it no such literal is ambiguous at all and the
translator pushes it exactly — its literal test is the narrower one, `IsUnambiguousTextFor`. The
branches are written against the accessor with its type discarded, which is how the translator is
told to render the path without applying the conjunct's test to a branch that is not the conjunct.

**And each branch reads the value in its own literal's type, because Calcite simplifies what it is
never asked to evaluate.** The branches were first written over one raw value, a string beside a
double or a boolean, and a connection could not plan them: `RelOptUtil.registerDefaultRules` adds
`FILTER_REDUCE_EXPRESSIONS` to every connection's planner, and its simplifier takes the literals one
expression is compared with to be of one class — it gathers them into one range set, and reads each
branch of a disjunction under the negation of those before it. `= '30'` and `= 'true'` threw a
`ClassCastException` there, measured through a connection against the emulator; the bare planner most
tests use never simplified the filter. So the string branch compares the accessor typed `ANY`, and the number and
the boolean compare it re-typed to the literal's type, which is the accessor with a `RETURNING` of
that type and a separate expression to the simplifier. The statement is unchanged — `c.x = 30` — but
for the boolean under a reducing host, whose equality with `TRUE` folds to the read and is written
`c.x`, true of the stored `true` and of nothing else. A column has no call to re-type, and a cast of
one to a number is a conversion the translator refuses, so its literal is boxed as `VARIANT` instead,
the box Calcite's own coercion uses. That box is not free — the simplifier hands a cast of a literal
to the executor, which compiles it every time — which is why the accessor, the spelling nearly every
caller writes, is not boxed too.

The literal is what carries the argument, so the literal is what is checked. Anything that parses as a
number, `true`, `false`, `null`, and anything opening with a bracket or a quote are refused, because a
non-string value could have rendered as them. This is not caution for its own sake: in the differential
container, `= '30'` matches the document storing the *number* 30 and `= 'true'` the one storing the
boolean, and both would have gone missing.

**What it recovers.** A view exposing the partition key as text routes to one partition again, which is
the largest cost lever there is and the one that was being lost in silence. Recovered for routing only:
`TryExtractPrefix` reads every conjunct, the cast's among them, where `TryExtract` reads only the
equalities over the path itself, so the point read and the whole-partition delete — each of which
replaces the predicate with an operation that applies none — keep the cast opaque. Routing narrows which partitions are visited and filters nothing, so the rows are
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
| `DOUBLE` | identity | yes, below 2^52 |
| `SMALLINT`, `TINYINT` | **wraps** — `-1` and `255` | no: unrelated to the stored value |
| `FLOAT`, `REAL` | rounds to float precision, 1.5e22 away | no: further than a unit |
| `DECIMAL` | raises, where it does not fit the precision | no: excluding the document would turn a failing query into a passing one |
| `JSON_VALUE(… RETURNING DECIMAL)` | its exact value, at any precision | yes, where the literal is cast to the clause's type and fits it — see *The document column* |

The first column is a cast of the raw value. Over `JSON_VALUE`'s rendering, a cast to `INTEGER` or
`BIGINT` is not a conversion at all but Java's integer parse of the text: a whole number lands exactly,
and a rendering with a point or an exponent, or a whole number beyond the type, raises
(`CalciteJsonValueMeasurementTests.ACastOfTheAccessorToANumberParsesTheRendering`, taken from the
predicate oracle's checker, which found it). Landing exactly is within one, and a document that raises
fails the statement, so the window holds there too and the limit's side is spared for nothing. So a
stored `30.7` converts to 30 under a cast of the raw value, as measured above, and raises under a cast
of the accessor; chapter 8 said the second converts too.

A double is within one only below 2^52. From there its unit in the last place is whole, and the stored
value and the literal each round by up to half of it, so a whole unit apart they land on one double:
`CAST(JSON_VALUE(…) AS DOUBLE) = 2^60` keeps a stored 2^60 + 1 the window would exclude. So
`TryNumericCastOperand` reports where the conversion rounds from, and no window is read around a bound
that far out. And a window's bound is a number sent to the service, which reads one sent with a point
as a double — *Verified against the emulator*. A whole bound a double does not hold is sent as a
`long`, and a side no value it can be sent compares as, the lower bound of `= 2.9999999999999999`, is
not stated.

Saturation is the row that had to be found rather than reasoned about. `CAST(x AS INTEGER) =
2147483647` is true of a document storing `1e30`, so a window around the limit excludes exactly the
document that matches — and the corpus caught it as a lost row before the bound stopped stating that
side. The same is true of any comparison the limit itself satisfies: `> -3000000000` keeps a stored
`-1e30`, which converts to the lower limit. So `CosmosNumericWindow` states a side only where the limit
fails the comparison. No literal reaches it that way today — a constant beyond the target's range is
wider than it, and Calcite widens the cast to compare them, which the window does not read through — and
`AnInequalityTheLimitSatisfiesDoesNotBoundThatSide` pins what is pushed instead. The fact extractor reads
the same window as a bound, so the two cannot come to disagree about it.

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
**Only a value typed `ANY`.** The cast over `JSON_VALUE` drops in a comparison, where the literal
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

**`JSON_QUERY` is the other half, and it is guarded by the complement.** SQL/JSON splits the two
accessors on the same line: `JSON_VALUE` answers for a string, a number, a boolean and a JSON null,
and `JSON_QUERY` answers for an object and an array. So the guards are complementary —
`IIF(IS_PRIMITIVE(p), p, null)` against `IIF(IS_OBJECT(p) OR IS_ARRAY(p), p, null)` — and the pushed
column carries a value for exactly the documents the function carries one for.

Before it had a rendering the projection was wrong in *both* directions, which is worse than the array
case that merely came back empty. The bare path was sent and the column read as the declared
`VARCHAR`, so an object or an array — the values the function exists to return — was refused by
`CosmosJson.GetString` with *Expected a JSON string, got Object*, while a scalar at the path came back
as itself where the function answers null.

**The reading is a third one, and whitespace is why.** `CosmosReading.Json` hands over the service's
own bytes, which is right for the `DOC` column and wrong here: measured, Calcite's `JSON_QUERY` parses
and writes the fragment back, answering `["a","b"]` for a path stored as `[ "a" ,   "b" ]`. So
`CosmosReading.JsonText` re-serialises compactly, and the pushed column and the in-process one are the
same string rather than differing by whatever spaces the document happened to carry.

**Only the plain form.** A validated `JSON_QUERY` always carries five operands — the wrapper and both
behaviours are present as symbols whether or not they were written — and each of them substitutes
something the path does not hold: measured, `WITH UNCONDITIONAL ARRAY WRAPPER` over the string `bikes`
answers `["bikes"]` and `EMPTY OBJECT ON ERROR` answers `{}`. `CosmosRexTranslator.IsPlainJsonQuery`
is the test, read by enum *name* rather than ordinal, and `IsJsonAccessor` now applies it — so a
wrapper form addresses no path in any clause and is left in process, where the engine computes what it
means.

**`JSON_VALUE`'s behaviour clauses are refused the same way, and for a while they were not.** The
argument above was written for `JSON_QUERY` and applied to it; `IsJsonAccessor` took any
`JSON_VALUE` with a path, whatever followed it. So `JSON_VALUE(doc, '$.flag' DEFAULT 'none' ON
ERROR)` was projected as `(IS_PRIMITIVE(c.flag) ? c.flag : null)` and read as text — null for a
document with no `flag`, where the engine answers `'none'` — and a view's column spelled that way bound
to `c.flag`, so `p."Flag" = 'none'`, being unambiguous text, pushed whole as `c.flag = 'none'` and
dropped exactly the documents the clause exists for. Wrong answers, not missed pushdowns. Three tests
had refused the clause by counting operands — `IsRenderedDocumentValue`, `TryRenderedTextValue` and
the accessor half of `IsUnambiguousTextEquality` — and the last had written the gap down: a field bound
to one "carries no clause to inspect". It cannot carry one; what it can do is not be bound.

**Rendering `DEFAULT x ON EMPTY` was the alternative, and measurement closed it.** It looks like
`IS_DEFINED(p) ? … : @x`, which is the standard's reading — *empty* is "the path found nothing". It is
not Calcite's. Measured (`CalciteJsonValueMeasurementTests`), a path written without a mode behaves as
`strict`: an absent property is an *error*, so the `ON ERROR` clause answers it and `ON EMPTY` never
runs —

| expression, over `{}` unless shown | Calcite answers |
| --- | --- |
| `'$.v' DEFAULT 'none' ON EMPTY` | null |
| `'$.v' DEFAULT 'none' ON ERROR` | `'none'` |
| `'lax $.v' DEFAULT 'none' ON EMPTY` | `'none'` |
| `'$.v' DEFAULT 'a' ON EMPTY DEFAULT 'b' ON ERROR`, over a JSON null, an object, an array | `'b'` |
| `'$.v' ERROR ON ERROR` | raises |
| `'$.v' ERROR ON EMPTY` | null |

A rendering would have to reproduce that table, keyed on the clause, the mode the path is spelled in
and the JSON type found, and keep reproducing it across releases; and `ERROR ON ERROR` raises, which —
as with the array `RETURNING` — no pushed column reproduces at all. Declining is right for every row,
because it is not an answer. So `CosmosRexTranslator.IsPlainJsonValue` is the test and `IsJsonAccessor`
applies it: a clause-carrying accessor addresses no path in any clause, its column binds to nothing,
and operators above it decline as they do over any computed column. The operand-count tests now ask
the same question instead of a proxy for it.

**`NULL ON EMPTY` and `NULL ON ERROR` are plain.** They are the defaults written down, but the
validator keeps them — `JSON_VALUE($0, '$.v', FLAG(NULL), FLAG(ON EMPTY))` — so counting operands
refused them too. Each clause is a behaviour, a value where the behaviour is `DEFAULT`, and the
condition it governs; a call is plain when everything after the path is a run of `NULL` and a
condition, read by name as `IsPlainJsonQuery` reads its flags. The cost of the refusal is the column
and whatever is written over it; a caller wanting a fallback writes `COALESCE`, which means what it
looks like and pushes.

**What the guard is depends on what the accessor is typed, and for a while it did not.** Every
`JSON_VALUE` was rendered `IIF(IS_PRIMITIVE(p), p, null)` and read as text, whatever its `RETURNING`
clause said. That is right for the bare accessor and wrong for every other spelling of it, in two
ways at once. `IS_PRIMITIVE` is *false* of an array, so `JSON_VALUE(doc, '$.tags' RETURNING VARCHAR
ARRAY)` projected as a column answered null for exactly the documents it was written to read — while
the *same call* as an `UNNEST` source resolved to the path and returned the elements, so one
expression meant two things (#119). And a scalar `RETURNING` carried the text reading with it, so a
column the plan had declared `INTEGER` came back holding a string and the row could not be built at
all. The declared type is what tells them apart, and it is the same test
`CosmosImplementor.TryBindOutput` already recorded the reading by:

| the accessor, and what it is typed | rendered | read as |
| --- | --- | --- |
| `JSON_VALUE`, `VARCHAR` or `CHAR` — the bare accessor | `IIF(IS_PRIMITIVE(p), p, null)` | `Text`, the rendering |
| `JSON_VALUE`, a number or `BOOLEAN` — `RETURNING INTEGER`, … | `IIF(IS_NUMBER(p), p, null)`, `IIF(IS_BOOL(p), p, null)` | `Typed`, the declared type |
| `JSON_VALUE`, a date or a time | `p` | `Typed`, the declared type |
| `JSON_VALUE`, any other type | `IIF(IS_PRIMITIVE(p), p, null)` | `Typed`, the declared type |
| `JSON_VALUE`, `DECIMAL` | *refused* — the clause parses a string; see *The document column* | — |
| `JSON_VALUE`, `ARRAY` or `MULTISET` | *refused* — the column is left in process | — |
| `JSON_VALUE` with a behaviour clause other than `NULL` | *refused* — the column is left in process | — |
| `JSON_QUERY`, `VARCHAR` — the plain accessor | `IIF(IS_OBJECT(p) OR IS_ARRAY(p), p, null)` | `JsonText`, re-serialised compactly |
| `JSON_QUERY`, `ARRAY` or `MULTISET` — `RETURNING … ARRAY` | `IIF(IS_ARRAY(p), p, null)` | `Typed`, a `java.util.List` |

The `JSON_VALUE`, `ARRAY` row is the one the rest of this section is about, and it is a reversal of
what #119 first shipped.

**The second row was the bare path for a while too, on an argument the measurements below refute.** A
typed accessor *asserts* its type, so a document holding another was taken to fail the read as it fails
Calcite (ikvmnet/calcite-dotnet#120), and the reader refusing it was the clause being trusted. For a
scalar of another type that holds; for an object and an array it does not. Neither is a scalar, so the
extraction fails, its error path answers null under the default `NULL ON ERROR`, and the cast that
raises is never reached — `CalciteJsonValueMeasurementTests.AnAssertingReturningReadsNullForWhatIsNoScalar`,
under `INTEGER`, `BIGINT`, `DOUBLE` and `BOOLEAN`. The bare path handed the service's object to the
reader, which refused it for the declared type, so the statement failed where the query answers: over
the differential suite's typed container, whose `label` holds an array in one document and an object in
another, the column, a `GROUP BY` and a `DISTINCT` on it, a `COUNT` of it, a `COALESCE` over it and a
read of it by id all failed with *Expected a JSON number, got Array*. The guard is now the type's own
test, which is the line the accessor draws; a scalar of another type reads null under it too, which is
SQL:2016's answer where Calcite raises. Which test is the reading's, for every writer that needs it,
is `CosmosRexTranslator.ReadingTests` — *What an accessor reads, stated once*. `DECIMAL` has none, being
the one numeric clause that parses rather than asserts — a stored `"30"` is 30 to it
(`EachNumericOrBooleanReturningAssertsItsClassButDecimal`) — so no type test is where it answers, and it
addresses no path at all (*The document column*). A date or a time stays bare: it pushes only where the
container declares a stored form the engine reads (`RequireTheEngineCouldRead`, above), and a stored
form is a string, so the path holds nothing the guard would read null for. The guard costs a point
read whose columns include one, which runs as the routed query instead — see *Not every statement is
a query* — and a sort, which orders a path and not the guard: see *Ordering a sort key the way it
reads*.

**An array `RETURNING` on `JSON_VALUE` is refused, and this section used to argue the opposite.** It
is worth keeping the reversal visible, because the earlier argument was coherent and still wrong.

Measured against Calcite's own runtime — `CalciteJsonValueMeasurementTests` — that spelling never
answers an array: the array is not a scalar, so the extraction fails, the error path runs and null
comes back; a scalar at the path throws a raw cast failure instead, outside that handling. The first
version of this work read those as *defects* and rendered the array at the service anyway, on the
ground that the traversal already reads the elements and a column that answered null was the odd one
out.

**What that argument missed is that there is no construct to be faithful to.** SQL restricts
`JSON_VALUE`'s `RETURNING` to a predefined scalar type and gives `JSON_QUERY` for structure, so an
array `RETURNING` on this accessor is not a thing the standard defines and then Calcite implements
badly — it is a spelling Calcite happens to accept. Implementing a meaning for it invents one, and
invents it *only here*: the same query against a plain Calcite connection answers null, so a caller
who moved a query off this adapter would silently get different rows. That is the divergence,
whichever direction it falls in.

So the column is not rendered. The projection declines, the expression is evaluated in process, and
a caller gets exactly what the engine gives:

| clause | in process, and therefore here |
| --- | --- |
| default, or `NULL ON ERROR` | null |
| `ERROR ON ERROR` | the engine's failure, raised |

The second is the half that settles it. A pushed column cannot reproduce a raised error — the service
has no equivalent — so rendering could only ever be right for one of the two clauses. Declining is
right for both, because it is not an answer at all.

**And the spelling that does mean an array works already.** `JSON_QUERY` takes a `RETURNING` too, SQL
permits it there, and measured, Calcite implements it:

```
JSON_QUERY(doc, '$.v' RETURNING VARCHAR ARRAY)          ->  string[2]{a,b}
JSON_VALUE(doc, '$.v' RETURNING VARCHAR ARRAY)          ->  null
UNNEST(JSON_QUERY(doc, '$.v' RETURNING VARCHAR ARRAY))  ->  two rows
```

That one is rendered, guarded by `IS_ARRAY`, and read as the list. A caller wanting an array column
has a spelling that works pushed *and* in process, which the refused one never would.

**It is Calcite's, and the layers were ruled out one at a time**, because "the wrapper loses the array"
is the obvious competing explanation and would have meant a different fix. `JsonFunctions.StatefulFunction.jsonValue`
— the extraction, invoked directly with `NULL ON EMPTY` / `NULL ON ERROR`, no plan, no code generation,
no reader — answers `null` over `{"v":["a","b"]}` and `bikes` over `{"v":"bikes"}`. The function is
scalar-only by construction, which is correct for SQL/JSON; the `RETURNING` type is applied *afterwards*
as a cast, so an array answers null and a scalar throws `String → java.util.List` outside the `ON ERROR`
handling. The validator admits a return type the runtime cannot produce.

The same answers come back through Calcite's **own JDBC driver** rather than the ADO.NET wrapper, over a
literal and over a table column alike, with `UNNEST` of one yielding no rows. Controls rule out every
layer in between: `ARRAY['a','b']` arrives as an `ArrayImpl`, and a `java.util.List` in a table's own row
arrives as a CLR array.

**The strongest case for rendering it anyway is [CALCITE-6208](https://issues.apache.org/jira/browse/CALCITE-6208)**,
fixed in 1.37.0, which tunes the element nullability of exactly
`unnest(json_value(col, '$.c' returning bigint array))` — upstream writing the spelling down as
though it worked. It is worth answering rather than ignoring, and the answer is that a JIRA touching
an example is not the standard defining a construct, and it plainly did not make the runtime produce
an array: the measurement above is against a release that has that fix in it. Rendering on the
strength of it would be betting on a future Calcite, and *shipping different rows in the present* to
hold the bet. If a release ever does answer the array, the measurements here fail loudly and the
question can be reopened against evidence instead of against a hope.

The guard reasoning that used to justify rendering this survives — it just attaches to the accessor
that has a meaning. `IS_ARRAY` answers null for an object, a JSON null and an absent path, where the
bare path would have the reader refuse to read an object as a list and fail the whole query, so one
oddly-shaped document reads null for its own row rather than failing every row with it. That is why
`JSON_QUERY`'s array form is guarded rather than bare, and `CosmosFilterSplitRule` takes the same care
on the filter side.

**A collection column is read to its element type.** `CosmosJson.GetList` takes the component type the
plan declared, so `INTEGER ARRAY` holds `java.lang.Integer` rather than the `java.lang.Long` an
integral JSON number is discovered as where there is no schema to consult, and `VARCHAR ARRAY` over
`[1,2]` fails with *Expected a JSON string, got Number* rather than handing back boxes of the wrong
class. The same refusal a scalar column makes, for the same reason: a wrong `RETURNING` fails rather
than lies. The value itself is a `java.util.List`, which is what Calcite holds a collection in and what
`CalciteArrayReadingMeasurementTests` pins reaching a `DbDataReader` as a CLR array — the last link a
caller mapping the column to a primitive collection depends on.

The bookkeeping moved with it. `CosmosProject` recorded the rendered expression wherever the *reading*
was not `Typed`, which was a proxy for "the rendering is not the path" and held while the only guarded
column was also the only rendered one. An array column is guarded and read as its declared type, so
the proxy stopped holding; the question is now asked of the two texts directly.

**What the column binds to did not change.** An array column addresses the path, as the accessor
always has — that is what an `UNNEST` source needs — so a filter above the projection pushes against
`c.tags`. A document holding no array there makes `ARRAY_LENGTH` or `ARRAY_CONTAINS` *undefined* and
is excluded, and in process the column is null and is excluded too; `IS NULL` is true of exactly the
documents with nothing at the path, pushed or not. That agreement is a consequence of which accessor
is rendered rather than a separate piece of care: it holds because `JSON_QUERY` answers the array in
process, and it is precisely what the array `JSON_VALUE` could not have given — in process *that*
accessor is null for every document, so `IS NULL` over a pushed one would have kept a different set of
rows than the same query without this adapter.

**Ordering by an expression is refused by the service anyway.** Measured, and it closes the question
rather than leaving it a matter of caution: `ORDER BY ToString(c.label)`, `ORDER BY UPPER(c.label)` and
`ORDER BY c.label || 'x'` each answer 400, error code 2206 — *"Unsupported ORDER BY clause. ORDER BY
item expression could not be mapped to a document path."* `ORDER BY (c.label)` is accepted, so the
restriction is exactly what the message says: the sort item must *be* a path. Rendering a cast into the
clause was therefore never available, whatever it would have cost. So paging a view by one of its own
cast columns reads every matching document. The one thing that would change it is a column the sort
could name, and that surface is rejected — `TODO.md` section 7 — which makes this a standing cost of
the row model rather than a pending item.

`COALESCE` and `NULLIF` need no entry — the validator expands both to `CASE` before a `RexCall`
exists. Several plausible additions are deliberately absent: `LOG(x, base)` and `SQUARE` are not in
Calcite's standard table, so nothing can produce them; `CBRT` is, and Cosmos has no counterpart.
`IS DISTINCT FROM` is declined because reproducing its null semantics over a property that may be
*undefined* needs a Cosmos behaviour that has not been measured, and a wrong answer is worse than a
refused pushdown. The `IS TRUE` / `IS FALSE` family was declined for the same reason, and now has
that behaviour measured over the one operand it is written for — the next section.

#### A cast to a boolean is the stored boolean, where the schema says so

A view types a boolean column the way it types any other, by casting the text accessor —
`CAST(JSON_VALUE(DOC, '$.data.offline' RETURNING VARCHAR) AS BOOLEAN)` — and Entity Framework writes
`WHERE "Offline"` as `"Offline" IS TRUE`. Nothing about it reached the service but a definedness test,
and every document carrying the flag was read to test it in process (#181).

**The cast is a parse, so without a declaration it is not the stored value.** Measured at Calcite's
runtime (`CalciteJsonValueMeasurementTests`): the accessor renders a stored `true` as `true` and the
cast reads it back, a JSON null and an absent path stay null — and a stored *string* is parsed,
trimmed and case-insensitively, so `"TRUE"` is true there and a string at the service, where it equals
no boolean; a number raises. Either is a document the two would answer differently, and the cast stays
declined wherever one could exist.

**Where the facts give the path a boolean type, neither can.** Then the cast is exactly the value at
the path: the boolean where there is one, null where there is a null or nothing. And the service's
logic over what is not a boolean is SQL's logic over unknown — measured against the emulator, one
document per thing a path can hold (`CosmosBooleanLogicMeasurementTests`): a bare path as a whole
condition keeps only `true`, `NOT` over it only `false`, and `OR true`, `NOT (… AND false)` and
`NOT (… OR false)` treat a null, an absent path, a string and a number as unknown in every case. So
`CosmosRexTranslator.TryStoredBoolean` writes the cast as the path, and everything over it follows
with nothing added: `WHERE c.flag`, `NOT c.flag`, and a comparison with a boolean through the guards
`WriteComparison` already writes.

**The truth tests are the one place the service's logic differs, and it is the absent path.** SQL's
`x IS TRUE` is never unknown; the service's `c.flag = true` is undefined where the path is absent —
measured, while it is false over a null and over another type — so a `NOT` above it keeps nothing
there, where SQL keeps the row. The negative tests name the absent case,
`NOT IS_DEFINED(c.flag) OR NOT (c.flag = true)`, which is true over everything but the stored `true`;
the positive ones need nothing in a positive position, undefined being kept no more than false is, and
take `IS_DEFINED` beside the comparison under a `NOT`, where it would show.

| written | rendered |
| --- | --- |
| `f`, `NOT f` | `c.flag`, `(NOT c.flag)` |
| `f IS TRUE`, `f = TRUE` | `(c.flag = @p0)`, `@p0 = true` |
| `f IS FALSE`, `f = FALSE` | `(c.flag = @p0)`, `@p0 = false` |
| `f IS NOT TRUE` | `(NOT IS_DEFINED(c.flag) OR (NOT (c.flag = @p0)))`, `@p0 = true` |
| `f IS NULL` | `(NOT IS_DEFINED(c.flag) OR IS_NULL(c.flag))` |

**Which facts.** The translator's own, which in a filter is the declaration closed under the
predicate's conjuncts — so a boolean declared only under a discriminator pushes beside the
discriminator, by the sibling-conjunct argument the fact rewriter makes. The conjunct cannot certify
itself: the cast resolves to no path, and the fact extractor reads nothing from a comparison over one.

**Which operands.** The bare accessor and a raw value typed `ANY` or `VARIANT`, and not a field a
projection bound to an accessor, although the comparisons take one. A behaviour clause substitutes a
value the path does not hold — the cast of `DEFAULT 'true' ON EMPTY` is true over a document with no
flag — and the binding records the path and that the column is text, not the clause, so the one thing
this has to refuse would be invisible through it. Nothing is lost: `FILTER_PROJECT_TRANSPOSE` takes the
filter below the view, where the cast is written over the accessor itself. Projected, the cast is the
same stored value, guarded as every accessor inside an expression is and read back as a boolean.

#### Splitting inside an expression, not only between them

`CosmosProjectSplitRule` partitioned a projection by whole expressions: each one either rendered or it
did not, and what did not was computed above with whatever inputs it read projected beside it. It
never looked *inside* one. So `CAST(JSON_VALUE(DOC, '$.n' RETURNING VARCHAR) AS DOUBLE)` was residual
whole — the cast converts where the service would not — and what the residual needed was `DOC`, so an
entire document crossed the wire to supply one scalar the service was willing to extract.

It now walks the residual for the **maximal** sub-expressions that translate, projects each as a column
of its own, and rewrites the residual to read them:

```
SELECT CAST(JSON_VALUE(DOC, '$.n' RETURNING VARCHAR) AS DOUBLE) AS x, JSON_QUERY(DOC, '$.tags' RETURNING VARCHAR ARRAY) AS a

  before   SELECT VALUE { "$f0": (IS_ARRAY(c.tags) ? c.tags : null), "": c }
  after    SELECT VALUE { "$f0": (IS_ARRAY(c.tags) ? c.tags : null), "$f1": (IS_PRIMITIVE(c.n) ? c.n : null) }
```

**Maximal is the whole of the discipline.** The walk is top-down and the first node that renders is
taken whole rather than descended past, because a deeper split would push `c.n` and compute above it an
accessor the service had already answered.

**What the residual still reads comes from the same walk, and that is what saves the bytes rather than
merely moving the work.** An input reached *inside* a pushed fragment is not needed above — the column
supplies it — so the walk records an input only where it reaches one outside every fragment.
Collecting them from the original expression instead would project `DOC` beside the scalar extracted
from it, which is the shape the rule used to produce.

**A bare reference and a literal are never fragments.** The reference is already available to the outer
projection and the literal costs nothing to compute there, so pushing either buys a column and no work.
That is also what keeps the rule from matching its own output: the projection it produces is references
beside residuals, and neither half offers anything new to push.

**A fragment is a column, so it is read as a column** — which is what makes a reading that renders
safe rather than a trap. A plain `JSON_QUERY` cannot be rendered *in place* inside an expression,
because its value is the fragment as text and the text is produced by the reading rather than by the
service. Lifted out, the reading exists: `UPPER(JSON_QUERY(DOC, '$.o'))` sends the guarded accessor
down as its own column, the compact JSON arrives as Calcite would have computed it, and `UPPER` runs
above over exactly that. The split does not weaken that refusal; it supplies the one thing the refusal
said was missing.

**Two things fell out of it that are worth recording.** A residual cast no longer pins the document, so
a projected cast over a path now costs the scalar rather than the row — the case #125 restored
correctness for and left the bytes of. And because the pushed half is now a `CosmosProject` binding a
path, a sort above it has something to name: the temporal cast whose sort `TODO.md` recorded as
"blocked before the sort" pushes as `ORDER BY c.at ASC` where the container declares the shape, and
stays in process where it does not.

#### An accessor keeps its guard wherever it is written

**The guard was dropped merely by nesting, and the bare accessor kept it the whole time — which is
what made it hard to see.** Measured on `main`:

| projection | rendered |
| --- | --- |
| `JSON_VALUE(DOC, '$.a')` | `(IS_PRIMITIVE(c.a) ? c.a : null)` |
| `UPPER(JSON_VALUE(DOC, '$.a'))` | `UPPER(c.a)` |
| `JSON_VALUE(DOC, '$.a') \|\| 'x'` | `CONCAT(c.a, @p0)` |
| `CASE WHEN … THEN JSON_VALUE(DOC, '$.a') ELSE 'x' END` | `((c.id = @p0) ? c.a : @p1)` |

All four push; only the first agrees with the engine. `JSON_VALUE` extracts *scalars*, so over a
document holding an object at the path Calcite answers null while the service was handing the object
to `UPPER`, to `CONCAT`, or out through the ternary (#131). The array form is worse than a wrong
value: an object reaching a column the plan typed a list is #129's cast failure by another route.

`TranslateProjection` sets a flag for its compound branch — the one reached once every whole-projection
shape has declined — and `WriteGuardedAccessor` renders each accessor the way its own projection
would, through the reading's tests: `IS_PRIMITIVE` for a text `JSON_VALUE`, `IS_ARRAY` for an array
`JSON_QUERY`, its type's test for a scalar `RETURNING`, and the bare path for a date or a time (the
table above); `DECIMAL` addresses no path to render. A scalar `RETURNING` alone reaches the same
method, the compound branch being where every whole-projection shape has sent it. A plain
`JSON_QUERY` is **refused** rather than guarded, because a guard is not what it needs — its column is
the fragment *as text*, produced by the reading and not by the service, and nested there is no
reading to do it.

**Scoped to a projection.** A filter compares the raw value the service holds on purpose, which is
what `CosmosFilterSplitRule` and `WriteComparand` are built on — for the comparison itself. What an
operator *beneath* a comparison makes of the value is the question below, and it is the same in both.

**One exception, and a test found it rather than the reasoning.** A geography function's operand is an
*address*, not a value: a stored geography is a GeoJSON object and
`ST_DISTANCE(JSON_VALUE(DOC, '$.location'), …)` is how a query names it, so `IS_PRIMITIVE` is false of
exactly the documents that have one and the pushdown would answer nothing.
`ASortOverADistanceRendersTheExpression` failed the moment the guard went in, and `WriteCall` holds it
back for that family.

**The guard settles objects and arrays, and a value is more than that.** The last three rows above
were rendered guarded after #131, and all three were still wrong for a number and a boolean — two
answering null, the third failing the read. The accessor renders a stored `30` as the text `30` and a
`true` as `true`; the guarded path hands the service the number and the boolean themselves, and the
service's string functions are undefined over either. Measured against the emulator over the
differential corpus's `typed` container, which holds one JSON type per document at `label`:

| statement | Calcite | pushed |
| --- | --- | --- |
| `SELECT UPPER(label)` | `30`, `TRUE` | null, null |
| `SELECT label \|\| '!'` | `30!`, `true!` | null, null |
| `WHERE UPPER(label) = 'TRUE'` | document 3 | nothing |
| `WHERE CHAR_LENGTH(label) = 2` | document 2 | nothing |
| `WHERE CASE WHEN … THEN label ELSE 'z' END = '30'` | document 2 | nothing |
| `SELECT COALESCE(label, 'none')` | `30`, `true` | the read failed: *Expected a JSON string* |

The filter rows are the same gap from the other side. `WriteComparand` asks whether its *operands* are
renderings, and the rendering was one call further down, so `UPPER(c.label) = @p0` went to the service
whole.

**So the question is asked of the edge, once.** `RequireRenderingsReadAsRenderings` walks the
expression before anything is written and refuses a rendering wherever what reads it is not told it is
reading one. Told are a comparison, which reasons about which documents a rendering matches; a null
test, which asks only whether there is one; `LIKE`, which refuses a rendering subject on its own
terms (#84 had taught it this and nothing else); a cast that names the raw value or that a stored
boolean proves; and the geography, full text, vector and type-test functions, which take the path as
an address. A cast that converts nothing hands its operand on to whatever encloses it. Everything else
— a string function, arithmetic, the arm of a `CASE` — is refused, and a new operator is refused until
it is told, which is the direction a mistake should fall in.

**A string is its own rendering.** Where what is known of the container says the path holds a string or
nothing, the value the service hands on is the text Calcite renders, and every operator agrees over
it: over a declared string `UPPER(label)` projects and filters at the service exactly as it did. A
plain `JSON_QUERY` never qualifies, being null for a string, and its equality is never exact either.
Over a container that declares nothing this is a loss of pushdown by design, and the planner corpus's
`Project.Arithmetic` left the wholly-pushed list for it: the arithmetic and the accessor still go
down, and the fold is Calcite's. `CosmosDeclaredFactPlanningTests.Renderings` pins each declared case
beside its undeclared control.

**Every rendering a comparison reads has to be a string's, not one of them.** Against a literal or a
parameter one string path suffices, there being one stored value to compare. Two renderings are two
stored values: `JSON_VALUE(a) = JSON_VALUE(b)` keeps a document holding `"3"` and `3` in Calcite, which
compares `'3'` with `'3'`, and not at the service, which compares a string with a number. Either side
sufficed once, which was already wrong over a container declaring one of the two a string and nothing of
the other; once a conjunct's facts were its stratum's, a sibling proving one path a string by its kind
pushed the raw comparison too (`RendersOnlyStrings`).

**A guard keeps the value, not the rendering, and a grouping is where that shows.** The guard makes
the service answer null where `JSON_VALUE` does, and passes every scalar through as itself; the text
the column carries is the reader's rendering of it. So an operator that *compares* the guarded value
at the service compares raw JSON, and over a path holding more than one type that is a different
equality from the rendering's: a stored `30` and a stored `"30"` are two values there and one, `'30'`,
in SQL. The filter translator refuses `JSON_VALUE(…) = ?` over an undeclared path for this reason, and
the lookup join a text key; a `GROUP BY` or `DISTINCT` keyed by the guarded accessor was the third
reader, and nothing asked it. Measured on the emulator over the typed container (review probes J1, J2,
J13 and J14), `SELECT DISTINCT JSON_VALUE(c."DOC", '$.price')` returned `'30'` twice, the grouping
counted two groups of one where SQL has one of two, and `COUNT(DISTINCT …)`, whose inner half is that
distinct, counted six where SQL counts five. `CosmosAggregate.GroupsAsTheColumnReads` now asks of
each grouping key what the other two ask, and the rule's `CanPush` and implementation both ask it, each
of the binding its own walk produced: a key read as its declared type groups as it reads (a typed
accessor's key being the guard its projection writes, null exactly where the column is), a key read as
text only where `IsDeclaredString` holds of its path, and a `JSON_QUERY` fragment not at all —
its text is a serialisation, and whether the service holds two objects equal exactly where their
compact texts are equal, in key order and number spelling, is unmeasured. The document column groups
nothing either way, no two documents being equal. The grouping then runs in process over the column
the service still sends guarded. The raw value groups refine the renderings' — each value has one
rendering — so pushing the grouping by value and finishing it by rendering in process, as a grouping
set is finished, would recover the pushdown exactly; `TODO.md` carries it.

#### Ordering a sort key the way it reads

**A sort orders the path and SQL orders the column, and the binding says only the first.** A
projection of `JSON_VALUE` read as text binds the path beneath it, so that an operator above can
address the document. `CosmosSortRule` and `CosmosSort` resolved a key to that path and wrote
`ORDER BY c.label` without asking how the column was read, and the service ordered the stored values —
by JSON type first, numbers by value — where SQL orders their text. Measured on the emulator over the
differential suite's typed container, whose `label` holds one document of each JSON type:

| `ORDER BY 1 NULLS FIRST` over | pushed | SQL |
| --- | --- | --- |
| `JSON_VALUE(c."DOC", '$.label')` | `null, null, 'true', '30', 'bikes', 'huge', 'shoes', null, null` | `null ×4, '30', 'bikes', 'huge', 'shoes', 'true'` |
| `JSON_VALUE(c."DOC", '$.price')` | `null, null, '7', '8', '9', '9', '30', '30.7', '30'` | `null, null, '30', '30', '30.7', '7', '8', '9', '9'` |

The stored `true` sorts below every number at the service; the object and the array read null in the
column — the guard — and sort above every scalar there. A view's column bound to the accessor did the
same. It showed only where the null placement agrees — an explicit `NULLS FIRST` ascending or
`DESC NULLS LAST`, or `defaultNullCollation=LOW` — which is the configuration the user manual tells
every caller to use, and its table 9.1 advertised exactly this pushdown.

**Every reading that is not the stored value fails the same way.** The document column, read as JSON,
pushed `ORDER BY c` and came back in the service's order of the objects rather than their text's. A
`JSON_QUERY` fragment pushed `ORDER BY c.f`, and the service put `[]` between `[1,2]` and `[2]`, and
`{"b":1}` before `{"a":2}`. An array read as declared — `JSON_QUERY(… RETURNING INTEGER ARRAY)` — is the
same question with SQL's element-wise order on the other side, and the service put `[]` after
`[1, 5]`. All measured on the emulator, all declined now.

**So the sort asks what the other three readers ask, and the question is asked once.** The filter
translator refuses a comparison against the rendering over an undeclared path; the lookup join a text
key, since its restriction is the raw `path IN (@k…)`; a grouping a text key, since the service groups
the stored values. The last two each wrapped the declared-string test in a dispatch on the reading
of their own, which differed only over the document column — the second encoding the rule against
encodings is for. `CosmosRexTranslator.IsReadAsStored` is that dispatch: a field read as declared is
the stored value, a typed accessor only where its type is declared (below); read as text, only where
`IsDeclaredString` holds of its path, what is known saying it holds a string or null and nothing else —
of what is known of the rows each reads — the declaration, or a filter beneath keeping a kind that declares it (L1,
*The oracle in the planner*); read as
JSON, never. The lookup join and the grouping ask it now — each taking an
exception of its own first, the grouping for the document column, no two documents being equal. The
sort asked it per ordinal too, until the service's order was asked once (below): `CosmosServiceOrder`
reads each key's reading itself (R2), of the binding, readings and facts `TryResolveSortKeys` hands it in
the rule and in the node alike, and refuses an array, a multiset, a map and a `VARIANT` whatever their
reading.

**A `RETURNING` key is the stored value only where the container declares its type, and this
paragraph used to say it always was.** The argument was that a document holding another type fails
the read rather than sorting somewhere, and that Calcite fails the same statement
(ikvmnet/calcite-dotnet#120). Both halves were false for an object and an array. Calcite reads both as
null (*Casts over document values*, beside the table of readings), and the service sorts both after
every scalar — so under a page the read never reached them. Measured on the emulator over the typed
container, `label` holding 30, an array, an object, null and nothing among the five documents
`RETURNING INTEGER` answers for:

| over those five, `ORDER BY 1` | pushed | SQL |
| --- | --- | --- |
| `NULLS FIRST FETCH NEXT 3 ROWS ONLY` | `null, null, 30` | `null, null, null` |
| `DESC NULLS LAST OFFSET 2 ROWS` | `30, null, null` | `null, null, null` |
| `RETURNING BOOLEAN`, over `true` in place of 30: `NULLS FIRST FETCH NEXT 3 ROWS ONLY` | `null, null, true` | `null, null, null` |

Unpaged the same order failed the read on the object, and with the column guarded it would have
answered in the wrong order instead: `null, null, 30, null, null`. So `IsReadAsStored` asks of a
nullable field read as declared what it asks of text, of the type the clause names
(`CosmosRexTranslator.TypeTestOf`): what is known of the rows the clause reads — the declaration, and
the filters beneath (L1) — must give the path that type, or null.
`JSON_VALUE(… '$.price' RETURNING INTEGER)` over products, which agreed pushed because no document there
holds anything but a number, now sorts in process; over a path declared an integer it pushes, and
agrees — the differential suite's `ordering` container. A field that cannot be null is the service's
own `id`, `_ts` or `_etag` and is asked nothing, and a `VARIANT` column was already declined.

**A grouping and a lookup take a typed key before asking, and a sort cannot.** The grouping key is
what the projection wrote, which for a typed accessor is now its guard, null exactly where the column
is; the lookup's restriction compares a parameter of the column's type, which no stored value of
another type equals, and a document whose column is null matches no key anyway. A sort has no such
key to be given: the service orders only a path (error 2206, above), and the path is not the column.

**Declining costs the sort and its row limit**: they run in process over the guarded column the
service still sends, and a page over an undeclared text or typed key reads the container. A declaration
recovers it. Nothing in one statement can, the service refusing an `ORDER BY` item that is not a path
(error 2206, above), so the rendering cannot be written as the key; `TODO.md` section 4 records the two
statements that could.

**A declared string is then ordered by code point, which is SQL's order only below the surrogates.**
With the reading settled, a sort over a declared string compared strings at the service by code point, where Calcite
compares UTF-16 code units: the service put U+E000, U+FF21 and U+FFFD below U+1F600, and Calcite above it. The
reading had nothing to do with it — `ORDER BY c.id`, a comparison between two declared strings and `MAX(c.id)` parted
the same way, nine recorded divergences in all — and what cured it was asking every order the service computes one
question, which the next section is.

#### The service's order, asked once

**Every order the service computes for a statement is one question**, `CosmosServiceOrder`, asked at the six places
the planner hands the service an order: the sort (`CosmosSort.TryResolveSortKeys`, which the rule and `Implement`
share), the extreme (`CosmosAggregate.CanImplement`), an ordering comparison (`CosmosRexTranslator.CompareOrder`),
the string branch of a text comparison's weakening (`CosmosFilterSplitRule`), an ordering through a stored form
(`CosmosFactRewriter`, whose lowering of a literal and binding of a parameter into the path's spelling both ask it of
the conversion), and the rank (`CosmosRankRule`). Each
builds its operands from its binding — a path and its reading, a conversion through a stored form, a geodesic
distance, a score, a literal, a parameter, an expression — and hands over the facts it was decided under, so that a
rule and the node it builds, asking the same question of the same facts, cannot disagree. The owner's rule is the
whole specification: if the plan can prove the order is one Calcite allows, it pushes; otherwise it is declined, and
declining is always correct.

**Three orders, and every proof makes them one.** Within a physical partition the service orders stored values by
JSON type — undefined, null, false, true, numbers, strings, arrays, objects; `DESC` the exact reverse — numbers by
value, int64 exactly, and strings by code point. Across partitions the SDK merges the streams by
`String.CompareOrdinal`, UTF-16 code units, which is also Calcite's order. Measured on the emulator and on the probe
account, one partition and two. A proof here never depends on how many partitions a container has, because it makes
the three agree over the values the facts admit.

**Four rules decide every reading.**
- **R1, characters.** Code point order and UTF-16 order part only where a surrogate meets a unit from U+E000: at the
  first unit two strings differ in, a unit below U+D800 orders alike against anything. So two sets of strings order
  alike unless one may hold a surrogate and the other a high unit — the service's measured behaviour, and the one
  thing about characters decided here (`Agree`). What characters a path's strings hold is a fact of the theory
  (*Characters, a fact of the theory*), asked of the facts the consumer was decided under (`JsonFactSet.CharactersOf`):
  a declared `pattern` and a service fact state it, a domain — an `enum`, a `const`, a filter's `=` or `IN`, per
  disjunct — and a stored form derive it by rules of the theory. Otherwise anything: Azure allows every character in
  an `id` but `/` and `\`. A literal's are its own, read by the theory's own reader of a value
  (`JsonFactEncoding.Characters`). A parameter's are anything until it is bound, and then the same reader's.
- **R2, readings.** Calcite orders what the column reads and the service the stored value: text over strings is the
  strings, a rendering of a number is not; a typed accessor over its declared type is exact; a `BIGINT` read back is
  the stored order within int64, a `DOUBLE` within ±2^53 and refines past it. A comparison reads the document, not a
  response, so there the readings are exact.
- **R3, the null class.** What a reading reads as null — absent, JSON null, any type it does not read — has to lie
  wholly on Calcite's side of the values, the values have to be one JSON type, and before the last key the null class
  has to be one tie class, the service putting undefined below null where Calcite ties them. A comparison asks it of
  its two sides together: what one side reads as null and the other may hold as the same JSON type, the service
  compares as two values, and a JSON null equals a JSON null there. Measured on the emulator, `c.t = c.t`,
  `c.t <= c.t` and `c.k >= c.k` over paths declared a string or null and an integer or null kept the documents
  holding a null, which Calcite's unknown drops; `<` and `>` keep none either way. So `Compare` refuses such a pair
  where equal values satisfy the comparison, and `Equate` asks the same of an equality, whose renderings the
  translator decides as it did. An absent path is undefined against anything at the service, and drops the row on
  both sides.
- **R4, keys.** Every key but the last agrees exactly; the last may be finer at the service. A key the facts make one
  value, or one read as null over every row, orders nothing and is not written; nor is a key an earlier exact key on
  the same path already ties. Calcite's own constant-key rule is inert in this planner.

**An operand says what the service compares, and only a path's facts say what it holds.** The translator's
`OrderOperand` reads each side of a comparison as one of four things, and none of them by its SQL type where the
service compares something else. A literal is the value it binds, of any type, and a null literal the JSON null it
binds, which `<=` keeps against a stored null. A rendering, or a typed accessor, or a raw value, is a path, whether or not
a fact can be about it: an array's element has no document path (`CosmosDocumentPaths.From`), and nor has a path under a
traversal's alias or an accessor over a constant, and such a path holds anything. It used to be read as a value its type
alone knew, so `JSON_VALUE(c."DOC", '$.tags[0]') < 'm'` was a string against a string, proved, and sent exact as
`c.tags[0] < @p0` with no type test and nothing rechecked: a stored 5 or `true`, which Calcite compares as `'5'` and
`'true'` and the service across types, was lost, on the emulator and on Azure (bisected to the stage that first asked
the question once). Read as a path that may hold anything, its rendering is refused and the split rule sends main's
weakening, `IS_DEFINED(c.tags[0]) AND (NOT IS_STRING(c.tags[0]) OR c.tags[0] < @p0)`, beneath the recheck; and two
typed elements that may both hold a JSON null are not compared. Anything else is a value the service computes — an
operator, a function, a `CASE`, a cast — which is Calcite's value where Calcite has one, and where Calcite reads null
may be an undefined, a JSON null a `CASE` writes or a stored value one hands through, so its null class is every other
type unless its SQL type admits no null: `CASE WHEN category = 'bikes' THEN NULL ELSE price END <= price` kept the
document whose price is a JSON null, the `CASE` handing the null through and the service's `null <= null` true, on the
emulator; and `CAST(2.5 AS DOUBLE)`, which the validator writes around a constant, is never null. What it declines that
pushed is a computed value against a path that may hold another type, `price + 1 <= c.other` among them (`TODO.md`).

**What the service accepts is a second question, and it is the container's.** Measured on the probe account,
`ORDER BY` over a path the indexing policy excludes is a 400, single key or not, and so is one over a computed
property no included path names; `_etag` is excluded from every policy the service hands back, though the emulator
reads none of that back. A multi-key `ORDER BY` needs a composite index, and a repeated key is two keys there:
`ORDER BY c.a, c.a` is a 400 without one. So `CosmosContainerMetadata.IsSortSupported` asks that every written key's
path be indexed and that the keys left after R4 match a composite index. An extreme is not asked it: `MIN` and `MAX`
scan, and Azure answers both over an excluded path, `_etag` among them.

**The extreme and the rank are the same question in other words.** The service's `MIN` and `MAX` skip an absent path,
count a JSON null and follow the type order, where Calcite skips every null: so the argument's null class may hold
only the absent path, its values one type, ordered alike. `ORDER BY RANK` takes no direction at all on Azure (SC2222):
it answers best first, which for a full text score and for `RRF` is descending, so `DESC` pushes as a bare
`ORDER BY RANK` and an ascending ranking is refused at plan time with a message naming `DESC`. A geodesic distance
means the service's WGS84 value, as a pushed spatial predicate does (the manual's 18.8), and keeps pushing.

**What it costs, and what it replaces.** The questions read frozen fact tables — presence, types, domains, bounds,
characters — and open no oracle scope; the planning gate measured no time added. They replace `NonNullFields`,
`NeverNull`, `AlwaysDefined`, `SortableFields`, `CosmosProject.IsOrderable` and the per-site order checks, each of
which encoded part of this. What they decline that pushed before is the order of strings whose characters nothing
states: a sort, a page and a keyset seek over `id`, a sort over a declared string, a comparison against a parameter
or between two such paths, the extreme of either. The declaration is the first answer, and is read now: a JSON
Schema `pattern` read as the characters a path may hold (below). The second is a proof completed when the rows
arrive — the anchored read, which checks at run time what no declaration proved (below) — and it pushes all of that
again but a comparison of two paths and a grouped extreme. A sort split by JSON type is next (`TODO.md` section 5).

#### Reading a pattern as characters

**A declaration is the first proof a user can write, and `pattern` is the one keyword that states what characters a
string holds.** That is the owner's decision (D2): not `format`, which JSON Schema calls an annotation, and not a
container operand, which would say per container what a pattern on `id` says per path. `CosmosPatternReader` reads
a pattern into the bands its strings may hold, a declared fact of the path (`JsonClaim.Characters`, *Characters, a
fact of the theory*), and R1 reads the fact: a path declared below U+D800 sorts, takes `MIN` and `MAX`, and compares
with a parameter and with any other path at the service, and `id` declared so pages and seeks there, proved when the
plan is made with nothing checked when the rows arrive.

**Two readings, and a claim has to hold under both.** JSON Schema names ECMA-262's dialect and no flags. Without the
`u` flag a pattern is a sequence of UTF-16 units, matched unit by unit: `[^a]` matches either half of a surrogate
pair, and `\uD83D\uDE00` is two atoms. With it, a sequence of code points matched code point by code point: `[^a]` is
a whole character, the pair is one atom, and a range may run past U+FFFF. A JavaScript validator built on `RegExp`
with the flag reads one way and .NET's `Regex` the other, and nothing in a container says which checked its
documents. So every pattern is parsed twice, and a band is absent only where both readings exclude it. They part
where it matters. `^[^\uD800-\uDFFF]*$` excludes the surrogates read by unit and admits every supplementary
character read by code point; `[\uD800-\uDBFF\uDC00-\uDFFF]` is the surrogates read by unit and, read by code point,
a range from U+D800 to U+10FC00 holding the whole high band. A pattern either reading rejects proves nothing: a
validator using that reading could not have checked a document with it.

**The grammar read is the strict one the two share**, and anything outside it is refused.
- The pattern is anchored: every way through it asserts `^` before it consumes anything and `$` after it has
  consumed everything. `pattern` searches, so an unanchored alternative matches inside a string of anything. With
  both anchors and no `m` flag every character of a matching string is one an atom consumed, which is what lets the
  atoms' bands bound the string's. An alternative is anchored by its first and last terms, and an unquantified group
  by its own alternatives (*One reader of a pattern*, below). An assertion inside — `^`, `$`, `\b` — consumes nothing
  and only narrows.
- An atom is a literal, an escaped syntax character or `/`, a control escape, `\cX`, `\0`, `\xHH`, `\uXXXX`, `\d`,
  `\w`, a class, or a group, capturing or `(?:`, under any quantifier. `\d` and `\w` are ASCII in ECMA-262 under
  either reading, a schema having no `i` flag to widen `\w`.
- A range's bands are those of every unit, or code point, it spans. `[\uD000-\uE100]` holds both bands though
  neither end is in one; taking a range's top unit as its band, as the plan first wrote it, would call it free of
  surrogates.
- A negated class is its complement under each reading, over that reading's alphabet: the units to U+FFFF, the code
  points to U+10FFFF. Computed exactly, so it is accepted. Read by code point the complement holds every
  supplementary character the class does not name, and no class both readings accept can name them all — a range of two
  supplementary characters runs backwards read by unit — so a negated class never excludes the surrogates, though it
  can exclude the high band.
- Refused: `.`; `\s`, which matches U+FEFF, and `\S`, `\D` and `\W`, which bound nothing; `\p{…}` and `\P{…}`, whose
  sets a Unicode version decides and which read by unit are a letter and a brace; `\u{…}`, a code point read by code
  point and `u` repeated read by unit; back-references and `\k`; lookarounds, named groups and inline flags; a legacy
  octal escape; an identity escape of anything but a syntax character or `/`; a class escape ending a range; and a
  lone `]`, `{` or `}`. Each of the last group is a literal read by unit and an error read by code point.

**What is stored is not always what was written, and that is the theory's to say.** Measured on the probe account, a
lone surrogate written as a JSON escape is stored as U+FFFD, a high unit; the emulator refuses the write. Read by unit
a pattern admitting a surrogate admits a lone one, so a claim that its strings lack the high band would be true of the
written document and false of the stored one. The reader used to decide that itself, claiming nothing for such a
pattern, which folded a service fact into the reading of an input. It now states what a validator saw, the bands the
strings were written in (`JsonClaim.Written`, the theory's `written`), and one rule of the theory derives what is
stored from it, reading the measured behaviour as data (`JsonValues.Stored`, the axiom
`Service.LoneSurrogateIsStoredAsReplacement`, pinned by a measurement that asserts on Azure and skips where the
emulator refuses the write): written with a surrogate, stored with the high band too. So what a pattern can prove of
the stored strings is still "neither band" or "no surrogate", and the deciding is in one place. A domain needs no such
care: `System.Text.Json` will not read a lone surrogate escape as a string, so no `enum` or `const` holds one.

**Checked against the engine, not only the grammar.** While it was written the reader was run against V8's own
`RegExp`, with the flag and without, over 36,000 generated patterns — literals and escapes from every band, ranges
across U+D800 and U+E000, negated classes, groups, quantifiers, and the refused constructs — each matched against
some four thousand strings. No band the reader called absent was held by a string either reading matched, and no
pattern either reading rejects was read. `CosmosPatternReaderTests` names the cases.

**What it reads is a declared fact, and it decides nothing.** The reader's verdict is stated of the path as a type is,
read off a subschema beside any type, a pattern being vacuous for anything but a string as the fact is; what the
fact is worth, and how it meets what else is known of the path, is the theory's (*Characters, a fact of the
theory*, below).

**`\d` in another dialect is the one place the reading trusts the name.** JSON Schema names ECMA-262, and the reader
reads `\d` as `[0-9]` for the characters and the stored forms alike. A validator in .NET's default dialect or
Python's reads `\d` and `\w` as every Unicode digit or word character, fullwidth and supplementary ones among them;
the manual says to spell the classes out where documents are validated that way (chapter 14.4). The reader being one,
refusing the two here would close it for both at once, and would cost every published date and integer pattern
written with `\d` its form. Recorded in `TODO.md` for the owner.

**One reader of the language.** This reader is the only one: the stored forms are recognised off the same parse,
from the strings a pattern admits (*One reader of a pattern: a form is a language*, below). Every pattern is read for
its characters, a stored form's among them: the theory derives the same of the path from the form, and meets the two
as it meets any two facts of one path.

**What it costs and what it buys.** A pattern is read once, when the container's schema is, and adds at most one
fact to a path. Timed against main, interleaved, in Release: the corpus's 78 statements planned new in
4.32 s on both and at a p90 of 152 ms against 159; planned again in 2.06 s against 2.03 and at a p90 of 19.4 ms against
18.5, the ranges overlapping as the one question's own did; the hierarchy and a declared container's steady state
the same within their noise. On the yardstick `orders_ascii`'s judged sorts went from 38 to 84 and its extremes from 7
to 13, its ids, their three-page keysets and its declared text among them, and the other two containers push what
they did (*Differential testing*).

#### One reader of a pattern: a form is a language

**There is one reader of a `pattern`, and both things the adapter reads off one come from its parse.** The owner's
rule is that C# reads an input into facts with one reader for each kind of input. A `pattern` was read twice:
`CosmosPatternAlphabet` parsed it strictly for its characters, and `CosmosPatternLanguage` rewrote its text into a
key the stored forms were looked up by — `\d` as `[0-9]`, a class's members sorted, a run's counts merged — and split
a UUID's text at its bars. The two read one keyword differently. The second had dropped a space as though it meant
nothing, read `Z{0}` as `Z`, and took an unescaped brace and `\-` for a UUID's brace and hyphen, which the reading by
code point rejects. `CosmosPatternReader` is now the only reader. Its parse yields the bands the strings may hold
(*Reading a pattern as characters*) and the strings the pattern admits, and the stored forms are recognised from the
second.

**The strings.** `CosmosPatternStrings` is a union of alternatives, each an anchored sequence of runs, and a run is a
class (`CosmosPatternClass`, a set of characters held as merged spans) repeated between two counts
(`CosmosPatternRun`). The parse puts a pattern in that shape by identities of the language alone:
- a class is the set it holds: `\d`, `[0-9]`, `[0123456789]` and `[5-90-4]` are one; so are a literal, an escape and
  a class of one character: `-`, `[-]` and `\x2D`;
- a group is what it holds, capturing or not, there being no back-reference to read it;
- an alternation of single characters is their class, `(?:0|1)` being `[01]`;
- an alternation inside a sequence is distributed across it, `^a(?:b|cd)$` being `^ab$|^acd$`;
- a count of a group is the group written out that many times; a count of one run multiplies into one range where
  the result is one;
- neighbouring runs of one class are one run, `A{m,n}A{p,q}` being `A{m+p,n+q}`, and a run repeated no times is
  dropped;
- the alternatives are a set.

Two patterns read as equal strings admit the same strings. The converse is not claimed — `^[0-4][0-9]$|^[5-9][0-9]$`
and `^[0-9]{2}$` are one language read as two — and a spelling read differently costs a form and never mistakes one.
What is no union of sequences of runs has no strings: a range of counts of a group, `(?:ab)*`; an assertion between
the anchors; an empty class; a count past 100,000. Such a pattern is still read for its characters.

**Sound under both readings, as the characters are.** The strings are read only where both readings accept the
pattern and read the same strings, and where every class lies in the basic plane outside the surrogates, where a
unit and a code point are one character. `[\uD800-\uDFFF]{2}` is one sequence of classes and two languages — a
surrogate pair read by unit, two lone surrogates read by code point — and has no strings; every form is ASCII, so the
condition costs none. A pattern one reading rejects is one a validator using that reading could not have checked a
document with. `^{…}$` with its braces unescaped, and `\-` outside a class, are literals read by unit and errors read
by code point, and are no form now.

**Anchoring is one decision, read off the structure.** A sequence begins with `^` where its first term does, an
alternation where every branch does, and an unquantified group where what it holds does; likewise at the end. A
quantified term may be passed over, and anchors nothing. Every way through a pattern that begins and ends so asserts
`^` before it consumes a character and `$` after it has consumed the last, which is all the characters need to bound
the string's and all a form needs to be the string. So `(?:^a$)|(?:^b$)`, the shape the nil-UUID alternation is
published in, is read for its characters now, where before only a top-level alternative's first and last terms were
looked at.

**A form is a language.**
- **Temporal.** Each form is defined by its pattern, which the reader parses as it parses a declaration. A declaration
  is the form where its strings are that language, exactly.
- **Numeric.** A fixed width is one run of `[0-9]` counted from one to ninety-nine times. An unpadded integer is either
  of its two defining languages.
- **UUID.** The strings are read as a shape. Each alternative is written out one character a slot. It is braced where
  a `{` opens it and a `}` closes it, its hyphens sit at the four places or at none, and every other slot is a set of
  hex digits; the alternatives' slots are united. A UUID form is recognised where the strings lie within the shape,
  because confining a slot only removes strings and every relation the form licenses is a claim about all of them.
  A table's form claims only its languages, and a narrowing of one — a month written `[01][0-9]` — is no form, as it
  was not before.

The registry of normalised spellings, the two regular expressions over normalised text, the UUID's splitting of the
text and `CosmosPatternLanguage` are deleted.

**The order the families are asked in decides two languages, as it did.** Eight digits is a basic date and a fixed
width, and is read as the date. Thirty-two digits is a hyphenless UUID with no letter and a fixed width, and is read
as the UUID. Either form proves both relations.

**A form's characters come from the form.** Nothing the reader decides about a form states characters. What gives a
form's path its characters is the theory's rule that a path in a form holds the characters the form is written in
(*Characters, a fact of the theory*). `AFormGivesItsCharactersThroughTheTheory` holds the rule to it with the
pattern's characters left unread. The reader still states the characters of every pattern it reads, a form's among
them, as reading an input, and the theory meets the two.

**What changed against main, and how it is held.** `CosmosStoredFormsSpellingTests` lists the 193 spellings the forms
were registered under on `4b2ccb3`, each with the form main recognised it as: the 77 temporal rows, every fixed
width a pattern could name, both unpadded integers and the sixteen UUID forms. It writes each every way the old
rewrites made one key of, and every one is the same form. Run against main, the same file fails exactly where main
and the reader part:
- **Recognised before, and no form now.** A space anywhere, which main read away: `^[0-9]{5} $` admits no string of
  five digits. `Z{0}`, which main read as `Z`, so a pattern with no zone was the UTC form. An unescaped brace and `\-`,
  which one reading rejects.
- **No form before, and the form now.** The same language written in ways main's rewrites did not reach: a class of
  the digits written out, `{4,4}`, a group, `[-]` or `\x2D` for a separator, the unpadded integer's alternatives in
  the other order or anchored apart, a count of a group, and a width of one digit, which main's merging of counts
  never wrote as a count.

A class one character wider than a form's, at any atom, is no form; for the tables, one character narrower is none
either.

**Checked against the engine.** As the characters were, the reader was run against V8's own `RegExp`, with the `u`
flag and without, over 9,159 patterns:
- every spelling of every registered form and its one-class neighbours, as the spelling test writes them, with
  their anchors grouped and their braces unescaped;
- 6,000 random patterns whose anchors sit inside groups, quantified or not, over atoms from every band.

No pattern either reading rejects was read for anything. No string either reading matched — 2.7 million pairs —
held a band the reader called absent. And every pattern read as a form, 1,342 of them over 411,820 matching
samples, matched exactly what the form's definition matches, or, for a UUID, nothing outside the form's shape.
Planted readings were each caught: a spaced form, `Z{0}` read as `Z`, a claim made of `(?:^a)?b$`, a mixed-case UUID
read as lowercase, and an unescaped brace.

**What it costs.** A pattern is read when its container's schema is, once for its form and once for its characters,
and each reading parses it under both readings. Measured in Release, interleaved, a fresh process a run, on a machine
other sessions kept at 90–100% of its four cores (main's corpus took 8.2 s here, where the orders measurement took
4.3 s):
- **The reader alone**, over the 198 patterns of the spelling test and five others, asked as the schema reader asks
  them, five runs a build: 12.0 µs a pattern on main `4b2ccb3`, which read forms only; 16.5 µs on `140eb4c`, the
  branch with two readers; 18.4 µs here, the runs' medians ranging 5.8–13.1, 14.6–18.8 and 11.7–21.4. It allocates
  2.8, 4.1 and 22 KB a pattern. The first version cost 30 µs and 42 KB: it built the strings for the characters,
  which ask only the bands, and copied a sequence at every term. Both are gone.
- **The corpus of 78**, at the final code, ten runs: planned new in 8.24 s on main (4.70–8.95) and 8.03 s here
  (5.08–9.17); p50 35.1 and 34.7 ms; p90 287 and 266 ms; planned again in 3.71 and 3.67 s. Allocated planning new:
  1,055 MB on main and 1,045 here.
- **The hierarchy**, before the reader's allocation was cut: MapLinks over one connection, 4.06 s a plan on main and
  3.22 s here, the ranges overlapping; it allocates 1,077 MB a plan on main and 1,083 here, the difference the
  characters' measurement already recorded.
- **A declared container's new statement in steady state:** no shape apart (Mann-Whitney p ≥ 0.38).

#### Characters, a fact of the theory

**What characters a path's strings hold is a fact about the documents like any other** (*The theory of JSON, in
layers*, for the rule). So it is the theory's `chars(p)`: the
set of classes a string at the path may hold characters of — the two bands an order reads, a surrogate unit and a
unit from U+E000; the rest of what lies outside ASCII, U+0080 to U+D7FF; what the service's `TRIM` removes beside a
space; and U+0000, which the service's searches stop at (*What a string function computes, asked of its characters*,
below) — a string holding none of them being ASCII that the service trims and searches as Calcite does. It is a fact like a type, and reasoned with as one. It is said of the value if it
is a string, so a number, a null or an absent path satisfies every such fact. It lies on the intersect lattice the
types use: a conjunction meets two sets, and a case join — a discriminator's split, the split over a disjunction's
arms, the lift of what every case proves — unites them. It holds per branch, a conditional rule stating it as a head,
and it is licensed per scope, read off the stratum, the child or the declaration a question's licence names. A
union of a branch lacking both bands with one lacking one keeps the one.

**Each source reaches it the same way: stated or derived in the theory, never computed beside it.**
- **A declared `pattern`.** `CosmosPatternReader` is a reader of an input. It states what a validator saw, the bands
  the strings were written in, as a declared fact of the path (`JsonClaim.Written`, the theory's `written`), beside
  any type, and decides nothing about what the fact is worth. A rule of JSON derives `chars` from it: a string is
  stored as it was written, a lone surrogate as U+FFFD (*Reading a pattern as characters*). A pattern that is also a
  stored form is read like any other; the theory meets the two.
- **A value.** A rule of JSON: a value's characters are its own. A domain's `chars` is the bands of the strings it
  holds, read off each value's spelling as its type is (`JsonValues.CharactersOf`, beside `TypesOf`). So an `enum` or a
  `const` says its characters in the declaration's context, and a filter's `=` or `IN` in the stratum it holds in,
  through the predicate oracle's establishing rules, which state the domain. Under an `OR` each arm's case states its
  own and the lift joins them. `WHERE c.s IN ('a', 'b')` proves `s`'s characters in the last stratum (L1) with nothing
  in C# computing them.
- **A stored form.** A rule of JSON Schema: a path holding a string in a form holds the characters the form is
  written in. `JsonFactTheory` states, for every form a declaration names, what the form is written in
  (`IJsonStoredForm.Characters`), which each family reads off what defines the form — a language the one reader of a
  pattern parsed, or a UUID spelling's alphabet — by the reader's own reading of a range's bands
  (`CosmosStoredForms.CharactersOf`). It used to be a constant beside the definition, a third encoding of one fact.
  Every registered form comes out ASCII, and `CosmosStoredFormsTests` checks what the definitions read.
- **A service fact.** `_etag`'s characters are stated as data, of the grammar RFC 9110 fixes, as `id`'s type is: read
  as the theory reads a range, the grammar's 0x80 to 0xFF giving characters outside ASCII and two the service trims, and
  nothing from U+D800.

**Stated of every domain, a domain of no string saying none.** The first version derived a row only from a domain
holding a string. The engine states a row of every domain a run passes through, and a domain only narrows, so a
row derived from a domain that still held a string outlived it. That happened to `text(x) = '30'` beside a typed
reading, whose preimage held the string `"30"` until the type took it out. The overlay, since retired (*Generations,
and what a question costs*), read the last domain and answered otherwise, and its differential caught the
difference. Stated of every domain, the row a run ends with is
the one its last domain says, whichever domains it passed through.

**Who reads it.** `JsonFactSet.CharactersOf` is what R1 asks (*The service's order, asked once*), and `Knows` and
`ClaimsFor` answer it as a claim, each off a context the engine ran. A literal's characters and type, and a
parameter's characters once it is bound, are read by the theory's own reader of a value (`JsonFactEncoding.Characters`,
`JsonValues.TypesOf`), so what a consumer asks of a value is what the theory knows of it. A computed string's are the
theory's too, said of the value rather than a path, `value chars`, by rules over the call's term
(*What a string function computes*).

**What is left in C#,** every fact about the documents still derived or combined outside the theory, each with why it
has not moved and its `TODO.md` entry. Everything else C# does with a fact reads it, decides what is legal, or writes.
- **The meet of an undiscriminated union, and the widening of a nullable one.** `JsonSchemaFacts.Meet` reads an
  `anyOf` or a `oneOf` with no discriminator before the theory sees it, and keeps every claim each branch entails
  (`JsonFact.Entails`, whose rows read a value's type through `JsonScalar.Type`): types, domains, bounds, forms,
  characters, what was written. `AdmitNull` widens every fact of a nullable union's branch to admit the null. Both are
  combinations of facts. They go when the reader states such a union as cases of a hidden discriminator, a null one
  among them, which the theory's split already reasons over (`TODO.md`, *The schema reader emits splits rather than a
  meet*).
- **A geography's proof.** `CosmosGeographyForms` reads a subschema's array counts and its elements' bounds itself,
  the schema reader stating no facts of array elements yet; it reads `type`, `required`, `const` and the bounds through
  the schema reader's own readers. It goes when `JsonSchemaFacts` states the element facts and a rule of the theory
  reads a measurable point off them, the measured ranges as data (`TODO.md`, *Facts about array elements*).
- **A disequality, or a bound, that what a path holds leaves out.** `JsonFactSet.Knows(NotEqualTo)` answers from a
  domain or types that exclude the value, as the subset test a domain claim is read by; and `Knows` of a lower or an
  upper bound from types that hold no number, which every bound holds of (`NoNumber`). No finite table states "not v"
  for every value a domain leaves out, nor a bound for a path that holds no number. Only the checks ask the first;
  `CosmosServiceOrder.Within` asks a bound, but over a path its callers have found may hold a number, so no plan
  reads the second today.
- **A predicate a context assumes.** `StatementTerms.Judge` asks `Node.Assumes` before anything else: a
  predicate the context assumes, or a conjunction of predicates it assumes, is always true there. That is two of
  `PredicateRules`' Decide rules — *a predicate that holds true of every row is always true*, and *a conjunction of
  truths is true* — read off the assumptions before any run, so that a verdict a weakening's question needs rests on no
  run that might stop on a limit and be discarded (*Predicates as facts: the oracle*). It is live through
  `CosmosFilterStatement.IsApplied` and `RedundantConjuncts`. It goes when the verdicts of what a context assumes
  survive a run that stops (`TODO.md`, *A verdict that rests on no run*).

The overlay that answered a schema-less container's strata beside the engine, restating seven rules of JSON and the
oracle's establishing rules, was on this list and is retired (*Generations, and what a question costs*). So is
`PathValues.Normal`'s restatement of the rules of JSON on the `possible` lattice: the tables the lattice meets are
narrowed by those rules before a verdict reads it, and what is left of the normal form is the value's own spelling,
an exclusion folded into a known domain, which the lattice's laws need. And `JsonContexts.Decide`, a decision of a
`JsonAtom` over a context's tables that restated a few of the theory's rules and that nothing in production asked, is
the theory's tests' own reading now (`ContextVerdicts`).

Not facts, and in C# by the rule: the reader of the pattern language, which reads an input; `CosmosServiceOrder.Agree`,
which says what the service was measured to do with two sets of strings; and the rest of the one question.

**How it is checked.**
- `SoundnessTests` draw strings of all three bands, characters facts true of the document, and a form whose
  characters are stated, and check every `chars` row derived.
- Population D (*How the oracle is checked*) draws patterns, `enum`s and `const`s of strings from all three bands,
  and filters comparing paths with them by `=`, `IN` and an `OR` of the two. It checks every `chars` row and every
  characters claim each scope answers, on the documents it admits.
- Two plants are the characters' own, each caught within a run's trials: a value read without its surrogates, and
  an arm's characters stated of its disjunction.
- `CharactersTests` and `CosmosOracleCharactersTests` name the cases: a conjunction narrowing, a split widening, a
  branch, a form, an instance, a filter in its stratum and not in the one it is rendered from.
- The `written` rows are checked as `chars` rows are, by both; what is stored from them is `CosmosPatternReaderTests`'
  to name, a surrogate written proving nothing of the high band stored.
- The other two rules of a domain have rows of their own in both checks, and a plant each, caught within a run: a
  domain's numbers read as bounding the path above their least, and a domain of one read as one value in every
  document without the path's presence.

**What it costs.** The theory has one rule of JSON more, over every domain a context holds, an inheritance through a
pattern, and one rule of JSON Schema over a stated form. A path with a domain or a declaration carries one row more.
It was measured in Release, interleaved, a fresh process a run, against main `4b2ccb3` and against `d00c0df`, the
branch before it. Other sessions' builds and tests shared the four cores throughout, so the ranges are wide: main's
corpus took 4.9 s here, where the orders measurement took 4.3 s.
- **The corpus of 78.** Planned new: 4.94 s on main (4.38–5.30) and 4.72 s here (4.47–5.74); p50 19.4 and 19.7 ms;
  p90 163 and 173 ms (141–192 against 145–196). Planned again: 2.17 and 2.21 s, p90 22.2 and 21.2 ms. Allocated
  planning new: 1,062 MB on main and 1,081 here; in another window, 1,049 at `d00c0df` and 1,021 here. That is
  inside the run-to-run spread of about 35 MB.
- **A declared container's new statement in steady state.** No shape is apart (Mann-Whitney p ≥ 0.18). `ne-gt`
  planned again is 22.8 ms on main and 19.7 here; `in-eq-ne` is 29.3 and 29.2.
- **The hierarchy.** MapLinks over one connection costs 5.17 s of CPU a plan on main and 5.72 here; in another
  window, 4.81 s at `d00c0df` and 4.92 here. The wall clock is 7.06 against 7.01 s, and 6.13 against 5.85, the ranges
  overlapping. It allocates 1,087 MB a plan here, as at `d00c0df` (main 1,081).
- **Population D's long run.** 5,000 trials took 945 s, two other sessions' long runs sharing the machine. It
  checked 4.8 million facts, 249,259 of them about characters, and nothing was false.

#### What a string function computes, asked of its characters

**Calcite's string functions are Java's, and the service's are its own.** Measured on Calcite in process, the emulator
and Azure, over sixteen strings and every function the translator pushes (`reports/verify-strings.md`), they part over
three things; a review measured a fourth, and three functions that part over something other than a character:
- **A count, over the surrogates.** Calcite counts UTF-16 units. `LENGTH` counts code points on both targets; Azure's
  `SUBSTRING`, `LEFT` and `RIGHT` count units and send the half of a pair a window splits back as U+FFFD, where Calcite
  keeps a lone surrogate; the emulator's count code points. Without a surrogate a unit is a code point, and every
  string measured without one agreed — U+E000, U+FFFD and a combining sequence among them.
- **A case mapping, outside ASCII.** Calcite maps by Java's full mapping: ß to `SS`, U+FB00 to `FF`, U+0130 to `i` and
  U+0307. Both services map by simple ones that leave ß and U+FB00; Azure's leaves U+017F, U+0130 and U+212A too, which
  the emulator maps. Over ASCII the three agree. The case-insensitive `CONTAINS`, `STARTSWITH` and `ENDSWITH` fold the
  service's way, so the `LIKE` rewrites built on them lost the rows Calcite's fold keeps — `aßb` under `UPPER(s) LIKE
  '%SS%'` — on both targets, and so did the weakening that stood in for them over an undeclared path: a recheck sees
  only the rows that arrive.
- **A trim, over white space.** Calcite's `TRIM` removes U+0020 alone. The service's removes white space: measured on
  Azure over every unit of the basic plane, every code point of plane 1 and U+E0000 to U+E01EF, each first and last,
  projected and in a `WHERE`, it removes U+0009 to U+000D, U+0085, U+00A0, U+1680, U+180E, U+2000 to U+200A, U+2028,
  U+2029, U+202F, U+205F and U+3000 beside the space, the three functions alike — an older Unicode's white space,
  U+180E in it and U+200B and U+FEFF not. The emulator removes a subset: not U+0085, U+00A0, U+180E, U+2007 or U+202F.
- **A search, over U+0000.** Azure's `REPLACE` and `CONTAINS`, both modes, read a string only up to its first U+0000:
  `REPLACE(s, 'b', '!')` leaves `a`U+0000`b` as it is, and `CONTAINS` finds no `b` in it. So does a `LIKE` whose pattern
  is one run of text between two `%`, which the service answers as a `CONTAINS`: `%b%`, `%%b%` and `%b%%` lose the
  string, where `%a%b%`, `%_b%`, `%b`, `a%b` and `a_b` keep it. A search holding U+0000 matches every string, and
  `REPLACE` of one replaces every byte. `INDEX_OF`, `STARTSWITH`, `ENDSWITH`, `LENGTH`, the comparisons, the order and a
  replacement holding U+0000 read past it (`Service.NulEndsWhatReplaceAndContainsRead`). The emulator stores U+0000 as
  U+FDD0 followed by `0`, so there every function over one differs, and nothing is learned of the service.
- **Not a character: a window's start, a repetition's count, a replacement's search.** Calcite clips a window that
  starts before the string, `SUBSTRING('abc' FROM 0 FOR 2)` being `a`, and the service answers a negative start with the
  empty string projected (`Service.SubstringOfANegativeStartIsEmpty`) — in a `WHERE` on Azure, compared with `''`, for
  nothing but the empty string. The service's `REPLICATE` is undefined for a negative count, where Calcite's `REPEAT`
  is the empty string, and on Azure past 10,000 code points, inclusive, where Calcite repeats on
  (`Service.ReplicateStopsAtTenThousandCodePoints`); the emulator has no limit. And the service's `REPLACE` of an
  empty search is undefined, where Calcite's is the subject (`Service.ReplaceOfAnEmptySearchIsUndefined`).

`POSITION` parts on the emulator alone. Azure's `INDEX_OF` counts units, as Calcite does, so `INDEX_OF + 1` is
Calcite's answer over every string; the emulator's counts code points. Azure is the target, so it stays pushed, the
emulator's difference recorded (`Service.IndexOfCountsUnits`). `REVERSE` and `CONCAT` agree everywhere projected, plain
`LIKE` — `_` is one code point everywhere — in every shape but the containment over U+0000, and `STARTSWITH`
everywhere. Azure's `REVERSE` compared in a `WHERE` with a parameter holding a supplementary character keeps nothing,
where projected the comparison is true (`reports/verify-strings.md` §4.4); nothing pushes a function compared with a
parameter, so nothing reaches it, and `TODO.md` holds it beside the item that would. Until this, over a path declared
a string, every function was pushed exactly with nothing rechecked, and every family above was a wrong answer a user
could reach in a projection and in a filter.

**So the characters fact gains three classes, and each part is a class.** Beside the surrogates S and the high band H,
which an order reads (*Characters, a fact of the theory*), the theory's `chars` holds N, a unit from U+0080 to U+D7FF,
so that a string with none of S, H and N is ASCII; W, a character the service's trim removes beside U+0020, which cuts
across the bands — a tab is ASCII, U+00A0 is in N; and Z, U+0000, which is ASCII. Each is reached as S and H are, and
combined by the lattice that combines them: the one reader of a unit and of a range (`JsonValues.MaskOf`) says which
classes a character is in, W read off the measured table as data (`Service.TrimRemovesWhiteSpace`,
`JsonValues.IsTrimmed`); so the pattern reader computes them under both readings, a literal's are known when it is
read, a stored form's alphabet gives its own — every registered form is ASCII with no W and no Z — and `_etag`'s are
read off its grammar. Each class is named once, its flag beside its member (`JsonValues.CharacterClasses`), and every
other spelling of a set of them is read off that table. A conjunction narrows them and a case join widens them. No C#
derives or combines one. R1 reads S and H alone (`JsonCharacterBand.FromD800`), so no order moved.

**Each function asks one question of its arguments, in one place.** `Sql/CosmosStringFunctions` says what each
needs: a count — `CHAR_LENGTH`, `SUBSTRING`, `LEFT`, `RIGHT` — that no string it counts hold S; a case mapping that it
hold none of S, H and N; a trim that it hold no W; `REPLACE` that its subject and its search hold no Z, and a `LIKE` of
the containment shape that its subject and its pattern hold none; everything else nothing of the characters. Every
string function the translator writes has a verdict there, the ones that ask nothing among them, and one written by
name with none is refused, so a function added to the translator's table is computed in process until what it asks has
been said. `CosmosRexTranslator.RequireStringFunctionsAgree` asks it once of the whole expression, before anything is
written, as a rendering is asked, of the facts the translation was licensed (*Which facts may justify what*). Asked at a
writer it would miss the case-insensitive rewrite, which writes the folded value and not the fold: asked of the `LIKE`
it stands for, the rewrite to a `CONTAINS` asks Z of the folded value, and the fold beneath it S, H and N. The rewrite
asks the same of its pattern's text (`FoldsAlike`), the case mapping's class named once for both. Where one is not
proved the translation is refused, and the function stays in process over the value the service sends, which is what a
path declared nothing of always did; the rest of the statement pushes.

**What stays in process still reads only what it needs, in a projection.** A declined function over a path's reading
sends the reading, `{"$f0": (IS_PRIMITIVE(c.s) ? c.s : null)}`, the projection split putting the accessor beneath the
function. Over a plain column there was no accessor to push, and the split was not made where nothing else beside it
rendered: `UPPER(c."id")` over an `id` nothing proves ASCII, which pushed before the characters were asked, sent
every document whole for Calcite to read one property of. The split now also counts a narrowing as worth a node —
only references, none of them the document itself, fewer than the input's columns (`CosmosProjectSplitRule.Narrows`)
— and sends `{"$f0": c.id}`. It terminates as the guard beside it does: the projection it makes reads exactly its
inner one's columns, which split again would be no narrower. In a filter it does not reach: a declined comparison
is Calcite's, above a scan with no projection, and the documents arrive whole (`TODO.md`).

**`LIKE` over a declared string is `LIKE` over the string.** A path declared a string renders as the string, so the
service's functions have no other type to be undefined over, as no operator over one has
(`RequireRenderingsReadAsRenderings`); refusing `LIKE` over its rendering whatever was declared sent the filter to
Calcite over whole documents, where the same statement over an undeclared path pushed as a weakening. It is pushed
exactly now: a prefix as `STARTSWITH`, another pattern as the service's `LIKE` — `_` one code point at both ends — the
containment shape where the subject and the pattern hold no Z, and a fold beneath it as the case-insensitive match
where the fold's classes are proved too (`CosmosRexTranslator.IsUndeclaredRendering`). On the `ORDER BY` yardstick's gate
it gives back six sorts and a page that a `LIKE 'a%'` beside them kept in process.

**What an argument holds is the theory's, a computed one's too.** The argument is read by the one reader of a
`RexNode` into the theory's terms (`CosmosPredicateLoader.Loading.ReadValue`): a path's reading, a literal, a
parameter, or a call — the operator's name and its operands, a `TRIM`'s flag spelled in the name, a `CASE` read as its
arms. The fact set answers what classes it may hold (`JsonFactSet.CharactersOf(SqlValue)`): a path its `chars`, a
literal its own, and a call what the theory derives in a context made for the question beneath the licensed one, which
mentions the paths the call reads, makes its term and runs the rules of a value's characters
(`PredicateRules.Characters`). There a reading of a path holds the path's `chars`, the cast to text of the value a path
holds them where the path holds scalars alone, and a call of a function `StringCalls` names draws from each operand it
reads the image of that operand's classes: its own for a concatenation, a window, a reversal, a repetition, a trim, a
replacement's subject and replacement and a conditional's arms; for `UPPER` also N where it was given H, Java's full
mapping taking U+FB13 to Armenian letters below U+D800, the one class any case mapping adds over every code point. A
parameter, and a call of a function the table does not name — a conversion such as `ToString` — hold anything. Before,
C# asked each operand in turn through a table of its own and called that a conjunction of questions; it was the theory's
join over the operands, written beside the theory, and the oracle and population D never saw it.

**And what is not a character is asked by the function's writer.** `SUBSTRING` is shifted alone only where its start
is proved at least one — a literal, or a path the facts bound there — and is otherwise written with Calcite's clip, from
0 for `start + length - 1` and no less than 0: a `POSITION` that finds nothing, a column, a parameter.
`REPEAT` pushes for a literal count of 0, or a positive one whose subject's length — a literal's own, or what the facts
bound a path's strings to — keeps it within 10,000 code points; a UTF-16 length bounds a code-point one from above. A
domain's strings now bound the path's lengths, a rule of JSON, and nothing else states a length yet: the schema reader
does not read `minLength` or `maxLength`, which count code points where the theory's count units. `REPLACE` pushes
where its search is a literal holding something or a path bound to a length of one or more. And `CONCAT` is matched by
operator, not by name: the PostgreSQL, SQL Server and Oracle `CONCAT`s read a null as the empty string and share the
name with MySQL's, which, as `||` is, is null over a null as the service's is undefined over one.

**A lone surrogate is never sent.** The SDK's UTF-8 encoder refuses one while it writes the statement
(`Service.LoneSurrogateParameterIsNeverSent`), so a statement carrying one failed on both targets where Calcite
answers. Only a `U&` literal can hold one, inside `POSITION` or `REPLACE`, Calcite refusing one against a column
elsewhere; `CosmosParameterList.Add`, which every value a statement carries passes through, refuses it, wherever in
the statement it stands, and the expression it stood in is computed in process. A parameter's value arrives only when
the statement runs. A column computed from parameters alone is never sent — the service would only hand back what it
was given, and `UPPER(CAST(? AS VARCHAR))` sent the parameter as a column for the function above to compute — so the
translator refuses one in a projection and Calcite computes it above. A parameter the service reads, in a comparison,
is refused by name when it is bound (`CosmosQueries.Bind`), before the encoder throws: the check is
`Client/CosmosEncodable`'s, asked at both ends, a property of the value as the encoder is handed it and no class of the
theory's, a stored string never holding a lone surrogate.

**What it costs, and what it gives back.** Timed against `702129b` in Release, interleaved, a fresh process a run,
two passes of ten paired rounds after a dropped first. In the first another session's test run held the machine at
about 100 % of its four cores, so every time is about twice a quiet run's: the corpus of 78 planned new in 9.38 s at
`702129b` (7.56–10.63) and 9.22 s here (7.85–10.24), the paired difference −0.12 s (−1.65 to +1.99), here the higher
in 4 of 10, p 0.56; planned again 4.03 and 4.04 s, p 0.92. In the second the machine was at 20–60 % but for three
rounds: planned new in 5.46 s (4.86–9.12) and 5.37 s (4.65–8.38), −0.02 s, 5 of 10, p 0.63; planned again 2.46 and
2.34 s, p 0.56; p90 157 and 166 ms; 1,017 and 1,015 MB allocated. In the four rounds both builds ran under 50 %, the
differences were +0.05, 0.00, +0.22 and −0.03 s on 5.2 s. No statement moved at p < 0.01 in either pass. Nothing
measurable was added. The corpus's 78 plans are the same at both builds. Over the 3,960 statements of the
differential corpus, the partition generator and the adversarial set, 3,916 plan alike and 44 push less, every one
a case mapping, a length or a trim over a path whose characters nothing proves: 20 send the presence a case fold
under `LIKE` implies where they sent the case-insensitive match, and 24 compute a function in process they used to
push. On the `ORDER BY` yardstick's gate one sort goes, beneath a case mapping of an `enum` holding é, and nothing
fails.

**How it is checked.**
- `CosmosStringFunctionMeasurementTests` pins the service's ten rows on a real account, each asserting what the
  emulator shares and skipping where it differs; on the probe account all ten pass, and the trim test holds the
  theory's trimmed class to what Azure removed. One asks the adapter through a connection, over a path declared a
  string and nothing more, and answers Calcite's length, case, window, trim and match there, and `POSITION`'s 4,
  pushed. `CalciteStringFunctionMeasurementTests` pins Calcite's six.
- Population D draws strings from every class — ß, the Kelvin sign, a tab, U+00A0, U+3000, U+0000 and U+FB13 beside
  the bands — and checks every class fact it derives against them, read unit by unit apart from the theory's table;
  and, from a stream of its own, strings computed from the declaration's paths and from those strings, nested two
  deep, whose characters each scope is asked as a string function's rule asks them and held to the string Calcite's
  runtime computes. Five plants are the classes' own, a value's characters read without its S, N, W or Z and an arm's
  stated of its disjunction; two are a computed string's, a call's characters read off its first operand alone and an
  upper-case mapping read as keeping its classes. Each is caught within a run's trials. `StringCallsTests` holds the
  table of what each function draws to Calcite's runtime over every code point.
- The translator's tests ask every function proved and unproved, a computed argument and a literal; the planner's
  (`CosmosDeclaredFactPlanningTests`, *what a declared pattern changes about a string function*) ask each over a path
  declared nothing of its characters, printable ASCII, every unit but the surrogates, and everything but white space.
- The differential corpus's `strings` container holds a supplementary character, ß, U+212A, a tab, U+00A0 and U+3000 at
  each path its declaration admits them, and its statements agree with Calcite on the emulator; four state Calcite's
  answers outright, so that an oracle agreeing with the service would be seen, a window starting before the string
  among them. `POSITION` over a supplementary character is the one statement it leaves out, the emulator counting it
  otherwise, and U+0000 is in none of its documents, the emulator storing it as two characters.

**What is not done.** A weakening would push a function wherever it is not proved: a filter sent as `(<pushed>) OR
RegexMatch(c.s, "[\x{10000}-\x{10FFFF}]")` and rechecked above, exact over the rows without the class and shipping the
rows with it; a projection with a fallback member keyed on the same test. A pattern `RegexMatch` cannot compile
answers undefined, which drops the row its arm was to keep, so each would need one measured to compile on both
targets. A declined comparison over a plain column reads whole documents in a filter, as above. And the lengths
`REPEAT` and `REPLACE` ask come from a domain alone, the schema's `minLength` and `maxLength` unread. All three are in
`TODO.md`.

#### The anchored read: a proof completed as the rows arrive

**What the plan cannot prove, the rows can.** A string order whose characters nothing states, a `BIGINT` read back
past int64, a `DOUBLE` past 2^53 before the last key: in each the one thing the facts cannot say is something about
the values, and the values arrive with the answer. The owner counts a proof completed when they do as a proof (D1),
so the question answers `Anchored` there (`CosmosOrderVerdictKind`), for a sort, an ungrouped extreme and a
comparison with a parameter, and the reader completes it. Everything a value cannot certify — a null placement the
service does not share, two JSON types, a rendering, the two nulls of a leading key, two paths' strings — is refused
as before.

**An anchor is a value the two orders agree about** (`CosmosServiceOrder.IsAnchor`, `CosmosOrderAnchor`):
- a string with no UTF-16 unit at or above U+D800, which orders against any other the same by code point and by code
  unit — at the first unit two strings differ in, a unit below U+D800 is below every surrogate and every unit from
  U+E000 under both rules;
- a number strictly within ±2^53, which is read back as itself, and past which every value — an int64 the service
  orders exactly, or a double printed as another integer — reads back on the same side. Strictly, because a service
  comparing doubles ties 2^53 with 2^53 + 1, which a `BIGINT` orders (`anchors.py` §6b);
- a value the key reads as null, which R3 has already placed alike in both orders: exactly before the last key, where
  R3 makes the null class one tie class at the service, and as a refinement on the last, where a tie of Calcite's is
  free and the service may order undefined below null.

**An anchor is where it belongs in the stream, through the merge.** Within a partition every row before an anchor
is at most it in the partition's order, so at most it in Calcite's. Across partitions the SDK emits the least head
in Calcite's order: a row of another partition emitted before the anchor was at most the head of the anchor's own
partition, which was at most the anchor; one emitted after was at least the head of its own partition when the
anchor went, which was at least the anchor. So every row before an anchor is at most it in Calcite's order and every
row after at least, whichever partitions they came from — which the probe account measured on 40 one-iterator
streams and 880 windows across two physical partitions, none accepted wrongly. Three programs follow:
- **A stream**: emit each anchor as it arrives, after the run of rows since the last one, sorted. The runs are
  ordinarily empty.
- **A window**, `OFFSET o LIMIT f`: its rows are Calcite's where its last row is an anchor or the stream ended inside
  it, and its first is one where `o > 0` — every row skipped is at most the first and every row past it at least
  the last. Sorted, the window is allowable. Otherwise the statement is read again without its page, and the page is
  taken from the sorted stream, stopping once it is out and the run it ended in is closed.
- **An extreme**: one that is an anchor is Calcite's. Any other is replaced by the argument read again in its order
  toward the extreme, `SELECT VALUE c.k … ORDER BY c.k DESC`, up to the first anchor — past which nothing lies beyond
  it in either order — or read whole where the path is not indexed. Grouped, there is no one row to check, and it
  is refused.

**A comparison with a parameter is decided by the value.** Against an anchor it is exact. Against any other the
statement is sent with the comparison weakened to a bound every string Calcite keeps satisfies at the service, and
the rows are rechecked by Calcite's comparison: for `>` and `>=`, `x >= P`, where `P` is the value up to its first
unit at or above U+D800 (`anchors.py` §5, 342,225 pairs); for `<` and `<=`, `x < P′`, `P` with its last character
raised, U+D7FF going to U+E000 (`keyset_mirror.py`, 170,820 pairs); and the type test alone where `P` is empty. The
translator writes the comparison as it is and records the three forms beside the parameters
(`CosmosOrderComparison`), with a second slot for the bound that `CosmosQueries.Bind` writes from the same value
(`CosmosAnchorBound`). A weaker comparison makes a weaker conjunct wherever `AND` and `OR` alone lead to it, each being
monotone in its operands, so the translator writes it there, and records the conjunct as a tree the reader rechecks
whole (`CosmosRecheck`): an `AND` and an `OR` of the comparisons, which the reader makes as Calcite does, and of the
other predicates, each selected as a member of its own and read back as the service answered it. Only truth is asked
of a leaf — a `WHERE` keeps a row where its condition is true, and through `AND` and `OR` alone unknown and false
count alike — so a predicate the service answers exactly in a `WHERE` is read back exactly. So the keyset seek past
two keys chapter 9.5 recommends, `s > ? OR (s = ? AND id > ?)`, pushes whole, ordered and paged. Under a `NOT` or in a
value a weaker leaf would not make a weaker condition, and the comparison is refused there. And the rows have to be the
filter's: the binding walk marks a filter that may hold one (`CosmosClauses.Recheck`, asked of the declaration alone,
which over-approximates what a stratum renders anchored) so that a grouping, a `DISTINCT`, a rank and a lookup decline
above it, and a filter declines above a `DISTINCT`. A comparison of two paths is refused: the rows the service drops
never arrive.

**One program, on the statement.** The node that asked records what the reader needs — a sort its keys
(`CosmosImplementor.OrderAnchored`), an aggregate its extremes, a translator its comparisons — and `Build` assembles
`CosmosOrderProgram` beside the statement: the keys with how Calcite compares each (`CosmosOrderedKey`), the page, the
statement unpaged, the comparisons and the extremes. A value the reader checks is selected under a member no column
has, `"$k0": c.s`, so a key the statement does not select is still there to read; the row builders read columns by
name and never see one. A `DISTINCT` is given none — a member it de-duplicated by would change what it returns — and
its keys are read from the members it already selects, which hold the key's value or null as the key's reading does.
`CosmosCursors.OpenAsync` hands a statement carrying a program to `CosmosOrderedCursor`; one whose orders are proved
carries none and is read as it always was. `CosmosSort` shows `order=anchored` in the digest, so that `EXPLAIN` tells
the two apart.

**Every statement is one iterator, from its start.** Measured on the probe account across two physical partitions, a
cross-partition `ORDER BY` resumed from a continuation token in a new iterator lost and repeated rows — the token's
`resumeValues` are compared by code point at the service, and rows the merge had not yet emitted from the other
partition are skipped or re-sent — and the certificate cannot see a row that never arrives. So the fallback is a
statement of its own, read from its start, and nothing resumes anything.

**A `DISTINCT` across partitions may repeat a value.** The SDK de-duplicates adjacent rows of the merged stream, and
where the orders part the merge can separate two copies: one partition holds U+1F600, the other U+E000 then U+1F600,
and the merge emits U+1F600, U+E000, U+1F600. A repeat ties the row it repeats, and a tie class is contiguous in the
sorted stream, so the reader drops repeats within each one. A repeat before a window is counted by the service's
`OFFSET` and cannot be seen, so a `DISTINCT`'s window past an offset is never certified and is taken from the stream
read unpaged.

**The comparator is Calcite's, written here** (`CosmosKeyComparer`): text by UTF-16 code unit, which is
`String.compareTo`; an exact number by `BigDecimal.compareTo`; a double as `Double.compare`, reading two integers past
2^53 alike as Calcite does, which keeps the order of a later key among them; a boolean false first; every null one tie
class placed absolutely. Not the CLR runtime's `JavaComparisons`, which orders multi-key text by culture.

**What it costs.** At planning, nothing measurable: the question's new answer reads what it read, and the sort rule
asks it once more in `convert`, for the digest. At run time, on data whose strings stay below U+D800 — nearly all text
— a scan of each key and the members selected for it, and no second request. Elsewhere, a run held and re-sorted, as
long as the run; for a page whose edge holds such a string, an extreme that is one, or a parameter that is one, a
second statement, in order and stopping at the next anchor, never more rows than sorting in process reads. The
`cosmos.order` activity reports which (`cosmos.order.program`, `cosmos.order.reissued`, `cosmos.order.buffered`; the
manual's 20.3).

**How it is checked.** `CosmosServiceOrderModelTests` holds every anchored verdict to the reader's own check over the
model's three orders: each value `IsAnchor` admits compares alike with every admitted value, ties included before
the last key; and an extreme read again to the first anchor through the model's merge is Calcite's. The sweep found
the last key's refinement needed saying, and says it. `CosmosOrderedCursorTests` runs the reader over the probe
account's measured merge, windows that pass and fail at either edge, a `DISTINCT`'s separated repeat, keysets past a
value that is no anchor in both directions, and extremes; `CosmosKeyComparerTests` pins the comparator to the Calcite
inventory's measurements. The eight recorded divergences of string order push again, each held to the reader having
re-sorted a run or read its fallback (`CosmosDifferentialTests.CompletedByTheReader`), and the yardstick judges every
order the reader completes (*Differential testing*). `CosmosOrderMergeMeasurementTests` runs the reader through the
adapter's executor over two physical partitions, on an account that has them.

**What it does not reach.** A comparison of two paths, a grouped extreme, a comparison under a `NOT` or beneath a
grouping, and `REAL`, which no anchor is stated for; and everything that is not about the values — the null placement
and the mixed types the sort split by JSON type is for (`TODO.md` section 5).

#### `COALESCE` pushes, and what stopped it was not `COALESCE`

A `COALESCE` over pushable expressions took the whole projection in process and shipped `DOC` to
supply one column (#130). The validator expands it before a `RexCall` exists:

```
COALESCE(x, y)  ->  CASE(IS NOT NULL(x), CAST(x):T NOT NULL, y)
```

Measured, every piece of that already pushed on its own — the `CASE`, the `IS NOT NULL`, the accessor,
the literal. What did not was **the cast, which converts nothing**: Calcite writes it to assert what
the null test has just proved, and one refused node fails the whole expression. Nothing about arrays
or about `JSON_QUERY` was involved either, which is where the report pointed — a scalar `COALESCE` to a
string literal de-pushed identically.

`WriteCast` now takes a cast whose operand is nullable, whose target is not, and whose types are
otherwise equal. **Narrow in both directions, and tests caught the broader version rather than the
reasoning catching it.** A cast that changes the family, precision or scale is a conversion the service
would not perform and is still refused. And a cast between two *identical* types is a marker rather
than a conversion — `WriteComparand` reads one to decide what an equality compares,
`CosmosFilterSplitRule` writes one to say the comparison is against the raw value — so stripping those
would have quietly pushed equalities the filter side declines on purpose.

The null test is the accessor's own null set, said over the path — see *What an accessor reads, stated
once* — and the asserted branch carries the guard:

```
SELECT VALUE { "a": ((IS_DEFINED(c.tags) AND NOT IS_NULL(c.tags) AND IS_ARRAY(c.tags))
                     ? (IS_ARRAY(c.tags) ? c.tags : null) : @p0) }
```

It was written as the null test of the guarded value, three guards deep, which is right only by way of
what the service does with a ternary whose condition is undefined. Said over the path, `IS_DEFINED`
answers the absent case before any type test is asked. The record used to have `IS_PRIMITIVE` over an
absent path both false and undefined; measured on the emulator (`CosmosJsonTypeMeasurementTests`) the
whole `IS_*` family answers a real boolean there, never undefined, so it is false. The rendering does
not lean on that: one conjunct or the other catches the case either way. Azure is owed the same
measurement; `TODO.md` section 10 records it.

**Over a text accessor nothing says holds a string, the coalesce no longer pushes whole**, because its
branch hands the value out as a text column and the service sends a number as a number — measured, the
read failed at the stored boolean. The split rule then pushes its pieces: the null test and the
asserted branch, `CAST(JSON_VALUE(…)):VARCHAR(2000) NOT NULL`, each a column of its own, with the
`CASE` above. A cast that differs only in nullability converts nothing, so `TranslateProjection` takes
that column as the accessor's and reads it as text, and the coalesce runs in process over the values
Calcite would have computed. The array coalesce and the one over a scalar `RETURNING` push whole, their
branch being the value.

#### An accessor over a literal is a constant

`JSON_QUERY('[]', '$' RETURNING VARCHAR ARRAY)` is how #130 spells "an empty array", and it addressed
no path — `TryResolveJsonPath` resolves the document operand to a path and a literal is not one — so
it took its whole expression in process with it. Both operands are constants, so the call is one: it
answers the same value for every document and is computed while rendering.

**Calcite's own reducer does not close this**, which is worth recording because it is the obvious
first answer. Measured with `PROJECT_REDUCE_EXPRESSIONS` registered and an executor set, a constant
`JSON_VALUE` folds — `JSON_VALUE('{"v":3}', '$.v' RETURNING INTEGER)` becomes `CAST(3)` — and the
constant `JSON_QUERY` does not, there being no `RexLiteral` for an array to fold it to. The collection
case is the one that was asked for, so it needs its own answer whether or not that rule is ever
registered.

The accessor's own shape test is applied rather than assumed, and it is the guard written out: an
array `RETURNING` answers the value only where it is an array, a text accessor only where it is a
primitive, null otherwise. The path grammar is the one a document column is held to — a wildcard, a
descent or a filter is refused here as it is there, one grammar rather than two. The value is bound as
a parameter in plain CLR form, so the service is sent the JSON the constant is and the column's own
reading converts what comes back, exactly as for a path.

#### What an accessor reads, stated once

An accessor is null for more than an absent path and a JSON null, and what more depends on which
accessor it is. `CosmosRexTranslator.ReadingTests` says it, as the service's type tests whose
disjunction is true exactly where the reading answers a value:

| reading | null where the path is absent, null, or | test |
| --- | --- | --- |
| `JSON_VALUE` read as text, or a field bound to one | an object or an array | `IS_PRIMITIVE` |
| `JSON_VALUE` with a numeric or boolean `RETURNING` but `DECIMAL`, or a field bound to one | any other type | `IS_NUMBER`, `IS_BOOL` |
| `JSON_QUERY` with an array `RETURNING`, or a field bound to one | anything but an array | `IS_ARRAY` |
| `JSON_QUERY`, plain, or a field bound to one | any scalar | `IS_OBJECT OR IS_ARRAY` |
| the raw value — a promoted column, the service's own columns, an accessor re-typed `ANY` | — | none |

A typed `RETURNING` over another type is an error that SQL:2016 answers null for and Calcite raises
over (ikvmnet/calcite-dotnet#120), so the test of the type never admits a row the engine answers a
value for. It is not a conversion either: `RETURNING DOUBLE` over a stored integer raises in process
too, an `Integer` that cannot be cast to a `Double`, while `IS_NUMBER` admits the row. Over integer
data such a statement has no oracle at all. `RETURNING DECIMAL` has no row, and had `IS_NUMBER`: it
parses a stored string too, so no type test is where it answers, and it addresses no path — see *The
document column*. Three writers read the line, and before this each stated it or did not for itself:

- **The projection's guard**, `(test ? p : null)`, which was right all along — #131 put it on every
  nested accessor — but for a typed accessor, which was the bare path until its type's test went on it
  too (*Casts over document values*, beside the table of readings). `WriteGuardedAccessor` now takes
  the guard from here for every accessor, leaving bare only a date or a time, which the stored form it
  is admitted over makes a string.
- **The null test.** `IS NOT NULL` over an accessor was `IS_DEFINED(p) AND NOT IS_NULL(p)`, the path's
  null set and not the accessor's. Measured against the emulator, `JSON_VALUE(DOC, '$.metadata') IS NOT
  NULL` kept the product whose `metadata` is an object, which Calcite does not return; over `typed`'s
  `label`, `IS NOT NULL` gained the array and the object and `IS NULL` lost them, through a view's
  column and inside a `COALESCE` as well. A planner test pinned the wrong rendering as right. It is now
  `IS_DEFINED(p) AND NOT IS_NULL(p) AND IS_PRIMITIVE(p)`, and its complement for `IS NULL`.
- **The guard a comparison takes where SQL's unknown has to be restored.** Under a `NOT` the comparison
  has to be true wherever SQL's is unknown, so that the negation discards the row, and the guard named
  the absent path and the null alone. Over an object the service's `=` is false and its negation
  true: `NOT (label = 'bikes')` kept the array and the object, directly and under a negated `AND` and
  `OR`. A typed `RETURNING` had the same gap, its `!=` restricted to the type in the positive position
  and its `=` not under a `NOT`. The guard now adds `OR NOT test` per path, read from the operand that
  named it, so a field cast to `ANY` — the raw value, named on purpose — keeps the path's null set.

**Written so that it does not matter what the service says of a type test over an absent path.** The
record has `IS_PRIMITIVE` there both false and undefined, and which it is has not been settled here. In
every form above `IS_DEFINED`, which was measured, decides the absent case first: `NOT IS_DEFINED(p) OR
…` is true and `IS_DEFINED(p) AND …` false whatever follows, so a type test is only ever asked of a
value that is there.

**A field bound to a typed accessor reads as the accessor too.** The binding records a field's path and
whether it is a rendering, and a field bound to `JSON_VALUE(… RETURNING INTEGER)` is not one: it is read
as its type. So `ReadingTests` had nothing to say of it, and `IS NOT NULL` over the field, or the guard
under a `NOT`, tested the path's null set — keeping an object the accessor answers null for. No probe
showed it, because `FILTER_PROJECT_TRANSPOSE` takes every filter in the corpus below the projection and
the accessor is then written itself; a host without that rule, or a plan choosing a `CosmosFilter` over
a `CosmosProject`, reaches the field. The field's type is the accessor's, `RETURNING` being what types
the call, so a nullable field read as its type takes the test its type names — `TypedFieldTest`, which
`IsReadAsStored` asks too, a sort over such a field being right only where the container declares the
type the test names (*Ordering a sort key the way it reads*). Nothing else bound to a path is such a
field: the service's `id`, `_ts` and `_etag` are never null, and a promoted column is a `VARIANT`, the
raw value, which names no test.

**A box is not a reading, and the box is seen through.** Calcite's coercion puts a `CAST(… AS VARIANT)`
around a side compared with a `VARIANT` — a literal, or the accessor where a promoted column is the
other side — and the box carries the value unchanged. `WriteCast` writes the value underneath, and the
comparison, `LIKE` and the null test each asked their question of the box: measured over `typed`,
`label = CAST('30' AS VARIANT)` pushed as `c.label = '30'` and lost the stored number 30, which Calcite
renders as `'30'` and keeps; `CAST(CAST(label AS VARIANT) AS VARCHAR) LIKE '3%'` pushed as
`STARTSWITH(c.label, '3')` and lost it too; and `CAST(label AS VARIANT) IS NULL` tested the path's null
set and lost the array and the object. Each now asks of what is in the box, through
`StripVariantCoercion`. Against unambiguous text the box changes nothing, and the equality pushes.

**And the document an accessor reads through has to be a document.** `TryResolveJsonPath` took any base
that resolved, a text `JSON_VALUE` among them, and its value is a rendering: Calcite parses the text as
JSON before reading the path out of it, and the service has the value. Measured over `products`,
`JSON_VALUE(JSON_VALUE(DOC, '$.name'), '$') IS NOT NULL` resolved to `c.name` and kept every document,
where Calcite cannot parse `Trail Blazer` and answers null for each — and the case it means, a document
stored inside a string, is the mirror: Calcite reads the nested property and the service's path is
undefined. A scalar rendering is refused as a base. A plain `JSON_QUERY` stays one, its text being the
fragment's, which parses back to the fragment the nested path addresses.

#### A position belongs to a predicate

The translator tracks whether an odd number of `NOT`s encloses what it writes, because a comparison
needs its guard in one position or the other — see *Residual type problems*. The position passes
through the connectives and nothing else, and the field's own remark said so; only the comparison and
the truth test cleared it. `WriteCallCore` now clears it once, for every call `CarriesPosition` does not
name, and three things change with it.

**A `CASE` passes it to its arms and not to its conditions.** An arm's value is the `CASE`'s value, so
under a `NOT` it is negated with it. A condition selects an arm, and SQL and the service both take the
next arm for a condition that is not true — the corpus's `CASE WHEN category = 'bikes' THEN 1 ELSE 0
END` answers 0 at the service for the document with no category, whose condition is undefined there —
so a condition has to be true exactly where SQL's is, which is the positive position whatever encloses
it. Written in the `NOT`'s position, `NOT (CASE WHEN category <> 'bikes' THEN … ELSE … END)` left the
inequality unguarded, true over the null category, and took the then arm where SQL takes the else.

**`IS NULL` over a predicate is declined.** It asks where the predicate is unknown, and there is
nothing at the service to ask: written positively, a comparison is guarded to be false wherever SQL's
is unknown, which leaves the test nothing to find, and written bare the service's `!=` is true over a
null. Measured, `(category <> 'bikes') IS NULL` kept neither the null category nor the absent one, and
`NOT COALESCE(category <> 'bikes', FALSE)` — whose expansion is such a test — lost the null one. A
stored boolean is a value and is tested as one.

**A `CASE` observes absence.** Its else arm answers where a path it reads is absent, so it does not
follow its paths into unknown — and two things assumed it did. The guard a comparison takes tests the
paths the comparison reads, which says SQL's comparison is unknown exactly where one of them is null:
`NOT (COALESCE(label, 'none') = 'x')` and `COALESCE(label, 'none') <> 'x'` each lost the null label and
the absent one. And the split rule weakens a conjunct it cannot push to `IS_DEFINED` of its paths:
`CASE WHEN category = 'bikes' THEN INITCAP(name) ELSE 'z' END = 'z'` became `IS_DEFINED(c.category) AND
IS_DEFINED(c.name)` and lost the document with no category. They are one question, and
`CosmosRexTranslator.ObservesAbsence` answers it for both: a `CASE`, `COALESCE` and `NULLIF` beside the
null tests, the truth tests and the `IS_*` family. A comparison that needs the guard declines over an
operand that observes absence; the positive equality needs none and is untouched.

**A disjunction implies what every branch implies, and no more.** The weakening pushes `IS_DEFINED` of
the paths a conjunct cannot do without, and the walk that finds them treated every operator as needing
every operand. A comparison, arithmetic and most functions do, answering null for a null; a conjunction
does where it must be true. But a disjunction is true where one branch is, whatever the others read,
and a conjunction read as a value — compared with something — is false where one conjunct is. So
`CosmosFilterSplitRule.CollectPaths` now unions only through the first kind, and takes what every
operand needs through the second; a negated compound needed nothing (until the falsity reading,
below). The disjunction reached it
whole wherever `TryWeakenDisjunction` gave up on a branch, and Calcite writes that shape unasked:
`SqlToRelConverter` pushes a `NOT` down through the connectives above an `IN`, so ternary logic
partitioning's `NOT ((tags IN (…) OR sku <> 'SPRINT') AND (price - 3 = 5 AND name || '!' > 'bikes'))`
arrived as a disjunction with a `NOT (… AND …)` branch, which has no weakening, and pushed
`IS_DEFINED(c.tags) AND IS_DEFINED(c.metadata.sku)` against a negation true of all seven products.
Measured against the emulator, six statements lost rows that way — the two that partitioning found,
the one recorded beside them, `price > 100 OR NOT (category = 'bikes' AND INITCAP(name) = 'Sprint')`
written directly, and `(category = 'bikes' AND price > 100)` compared with `(id = '5')` and with
`FALSE` — and they are in the corpus. That ends cause L; its `CASE` half is the paragraph above.

**So does a null test folded into a range.** A host running Calcite's own rules simplifies the filter
it moves beneath a projection, and `n IS NULL OR n = 'B-2'` comes out as `SEARCH(n, Sarg['B-2'; NULL
AS TRUE])`: true where `n` is null, as the test was, but no longer spelled as one. The split rule read
it as a comparison and pushed `IS_DEFINED(c.metadata.sku)` under a predicate true of every product.
`ObservesAbsence` now counts a `SEARCH` whose Sarg answers null outright, either way; one that answers
unknown, which is what an `IN` builds, is still the comparisons it stands for. The translator expands a
`SEARCH` before writing it, so only the weakening ever saw the folded form, and only under a host's
rules — `CosmosPlannerTests.ANullTestFoldedIntoASearchIsNotWeakened` is the witness, the differential
harness registering none of them.

**And a function is read through only where it is null for a null.** The walk descended into every
operator but the connectives, on the premise that a function answers null for a null, and some answer a
value instead. Measured against the emulator over `products`: `CONCAT_WS('-', name, sku) <> 'Road
Runner-B-2'` pushed `IS_DEFINED(c.name) AND IS_DEFINED(c.metadata.sku)` and kept none of the six
documents it is true of, `CONCAT_WS` skipping the absent sku; `CARDINALITY(ARRAY[INITCAP(sku)]) = 1`
pushed `IS_DEFINED(c.metadata.sku)` and kept one of seven, an array holding a null not being null; and
`ARRAY_CONTAINS(ARRAY[INITCAP(sku), 'x'], 'x')` did the same. The question was older than the
disjunction and the negation: none of these has either. `CosmosFilterSplitRule.IsNullWhereAnOperandIs`
now says which calls may be read through, and a call it does not know implies nothing — a list of what
is safe rather than of what is not, since the function a library adds next is the one nobody listed,
and declining a weakening keeps every row. It is Calcite's `Strong.Policy.ANY` (the comparisons,
`LIKE`, arithmetic, the cast, `NOT`, `ITEM`), a `SEARCH` that answers unknown over a null, a plain
accessor, and the string and numeric functions measured to answer null over a path no document has:
`||`, `UPPER`, `LOWER`, `INITCAP`, `SUBSTRING`, `CHAR_LENGTH`, `POSITION`, `REPLACE`, `LEFT`, `RIGHT`,
`REVERSE`, `REPEAT`, `MOD`, `ABS`, `ROUND`, `POWER` and `SAFE_CAST`. The self-join rule's
`IsNullExactlyWhereAnOperandIs` is the stronger question, null *exactly* where an operand is, and stays
its own. The guard a comparison takes rests on the same premise and still asks `ObservesAbsence`, the
list of what is not safe; it is written only over what the translator writes, and the translator
declines `CONCAT_WS`, the constructors and `ARRAY_CONTAINS` alike. Across the differential corpus, the
planner corpus and the gate's 1,000 predicates in all three partitions, planned before and after, the
weakening changed for those statements alone.

**What every branch needs is matched by path, not by spelling.** One path has several spellings — the
promoted column `c."$.category"` and `JSON_VALUE(c."DOC", '$.category')`, a typed accessor and a text
one — and the walk told paths apart by expression, so a disjunction reading the category one way in each
branch needed it in neither: `price > 300 OR (INITCAP(name) <> 'Zzz' AND (c."$.category" = 'bikes' OR
JSON_VALUE(c."DOC", '$.category') = 'shoes'))` pushed `(c.price > @p0) OR IS_DEFINED(c.name)`. Sound, and
less than it could. The walk now keys each path on what it resolves to and keeps the first expression
read there, which renders the same `IS_DEFINED`; the statement pushes the category too, and agrees.
Two *conjuncts* weakened to one path through different spellings still push it twice — one of the
gate's predicates sends `IS_DEFINED(c.price) AND IS_DEFINED(c.price)` — which is harmless and is left:
what is already below is recognised by expression, and dropping a weakening for matching another
spelling is a change to the guard against pushing one under itself for ever (#210), not to this walk.

**A predicate is read for its truth, its falsity or its value.** The walk had two readings, the paths a
predicate needs to be true and those it needs to be anything but null, and two shapes fell between
them. A negated compound needed nothing at all, because `NOT (a AND b)` is true where `a` alone is
false. And a compound compared with a boolean literal was read as a value, which for a compound is
what every operand needs. Both were sound and both declined restrictions that hold: measured over
`products`, `price > 100 OR NOT ((category = 'shoes' OR INITCAP(sku) = 'B-2') <> FALSE)`, whose second
branch is true only where both disjuncts are false, pushed nothing where the category and the sku are
each needed. The third reading is falsity, the dual of truth: a conjunction is false where one conjunct
is, so false it needs what every conjunct needs to be false; a disjunction is false where every branch
is, so false it needs what any branch does. A `NOT` swaps truth and falsity and keeps a value a value,
and `x = TRUE` and `x <> FALSE` are `x` while `x = FALSE` and `x <> TRUE` are `NOT x`, read either way.
`NOT (a AND b)` still needs nothing, `a` false and `b` false having nothing in common, so the witnesses
above stand. Every reading needs at least what the value reading does — a predicate that is true or
false is not null — so the change only adds: `NOT (a OR b)` now needs both, `NOT NOT (a AND b)` both,
and `(a AND b) = TRUE` both. The refusal of `NOT (a OR b)`, which the comment beside it admitted cost
a weakening and never a row, goes with it. Planned before and after, 62 of the partition run's
statements and 3 of the corpus's push more than they did and none less — what any of them pushed
before is still there, with `IS_DEFINED` terms beside it — and the gate's 1,000 predicates pass over
the change, as does the corpus with a statement for each shape.

#### A predicate's value is not its truth

Every guard in the section above answers one question: is the predicate *true*. A `WHERE` keeps a row
for true and discards it for false and for unknown alike, so a comparison written there may be false
where SQL's is unknown — that is what the positive guard on `!=` is, and why the service's own `=`,
false over a JSON null, needs none. `IS NULL` over a predicate was the first place the difference
showed, and declining it closed that place only. Wherever else a predicate's *value* is read, false and
unknown are different answers. Measured against the emulator over `products`, whose category is null
in one document and absent in another:

| statement | Calcite | pushed |
| --- | --- | --- |
| `WHERE (category <> 'bikes') = FALSE` | documents 1, 2 | 1, 2, 6, 7 |
| `WHERE (category = 'bikes') = FALSE` | 3, 4, 5 | 3, 4, 5, 6 |
| `WHERE (category <> 'bikes') IN (FALSE)` | 1, 2 | 1, 2, 6, 7 |
| `SELECT category = 'bikes'` | null for 6 | false for 6 |
| `SELECT category = 'bikes', COUNT(*) GROUP BY 1` | false 3, null 2 | false 4, null 1 |

The translator now carries a second piece of state beside the position: whether what it writes is read
for its value. `TranslateValue` — a projection, a rank's select list — starts there, and every operand
of an operator that reads its operands as values is there: a comparison's, a function's, a cast's.
The connectives pass it on as they pass the position on, and a `CASE` passes it to its arms and clears
it for its conditions, which select an arm by their truth. `Translate`, a `WHERE`, starts without it.

**What it declines is a comparison Calcite types nullable.** None of the forms a comparison is written
in answers null exactly where SQL does, and rendering one that did — the comparison under a ternary on
its operands' null sets — would be a new thing to measure, where declining costs the comparison alone:
the projection split sends its operands and Calcite compares. Typed `NOT NULL`, SQL never answers
unknown and the comparison is two-valued at both ends, so `SELECT id = '3'` pushes as it did. An
ordering comparison against a constant measured right as a value, the service's cross-type ordering
being undefined over a null and so reading back null, and is declined with the rest rather than kept by
a second rule. A truth test over a stored boolean is written in the form it takes under a `NOT`, which
names the absent path and is two-valued by construction; written bare it was undefined over an absent
path, which a projection reads back as null where `IS TRUE` answers false.

**What it costs.** A predicate projected for its own sake is rare. The common shape it touches is a
`CASE` whose condition the projection split pushed as a column of its own — the condition is read
there as a value, which it is, since the split cannot know the `CASE` above reads only its truth — and
there the comparison now runs in process over the operands the service sends.

#### A quotient is a double at the service

The service divides in doubles. SQL divides in the type of its operands, and Calcite truncates an
integer quotient toward zero. Measured over `products`, whose prices are integers, `price / 2 = 2` lost
the document priced 5 — 2 in process, 2.5 at the service — and `price / 3` projected failed the read at
`113.33333333333333`, which is not the integer the plan declared. A division pushes only where Calcite
types the quotient approximate. `TRUNC(a / b)` is the obvious rendering, and it would have to be shown
to agree on negatives, past what a double holds and through the reader, for each integer type; it is
declined instead, which costs the division and nothing else. `CosmosAggregate.CanImplement` made the
same call for `AVG`, which an integer argument sends to `SUM` and `COUNT` with the division above.

The control the planner test and the corpus carry divides the integer accessor by a double, which
Calcite widens. `RETURNING DOUBLE` was the first spelling, and over these prices it has no oracle:
Calcite raises over a stored integer rather than widening it — see *What an accessor reads, stated
once* — so the agreement it claimed was never measured.

**A decimal fraction is the quotient's neighbour.** Calcite types an integer times `1.1` a `DECIMAL`
with a scale and computes it exactly; the service computes it in doubles, where `1.1` is not `1.1`.
Measured over `products`: `price * 1.1 = 374` lost the document priced 340, whose product the service
makes `374.00000000000006`; `price * 0.07 = 0.35` and `price + 0.1 + 0.2 = 10.3` lost theirs;
projected, `price * 0.7` was `237.99999999999997` against Calcite's `238.0`, a grouping key by the
product came back as `374.00000000000006`, a `SUM` over it drifted in its last place, and a remainder by
`0.3` lost two documents. `RequireNoFraction` declines `+`, `-`, `*` and `%` where Calcite types the
result a `DECIMAL` with a non-zero scale. A scale of zero is an integer, exact in doubles to 2^53, and
an approximate result is the service's arithmetic outright, so both push as before.

**Integer overflow is pushed, and that is a decision.** Calcite's integer arithmetic wraps, as Java's
does: over `products`, `price * 100000000 > 0` keeps the two prices whose product fits and drops the
three whose product wraps negative, where the service computes the products and keeps all five;
projected, the service's product is not an `INTEGER` and the read fails. SQL raises for both — an integer result outside its type is a data
exception — so the engine's answer is no more SQL's than the service's, and of the two the failed read
is the one the standard gives. Declining would take every integer sum, difference and product, which
are the arithmetic this pushes most, to protect an answer the standard does not give, over data that
has to exceed two billion to reach it; a declared range could prove a sum fits, and nothing reads one
for that yet. So it is recorded beside out-of-domain arithmetic under *Verified against the emulator*,
and kept out of the differential corpus by name, as that is. A `BIGINT` past 2^53 is the same question
asked of a double's precision rather than its range, and is answered the same way.

### A projection that cannot be pushed is not a wall

A view is how a caller gives a container a relational shape, and a view has to cast: the row model
types every path `ANY`, and nothing downstream that expects columns of a type can consume `ANY`. A cast
to text is rendered by the reader and pushes; the rest — a width, a numeric target — stays in process,
and a sort and a row limit above it used to stay with it, so a bounded page over a view read every
document the predicate matched.

`CoreRules.SORT_PROJECT_TRANSPOSE` is registered for this, alongside the other Calcite rewrites the
rule set carries because a bare Volcano planner has none. Transposed, the sort and its limit sit under
the projection and push; the cast runs over the rows that come back.

**This section used to say the transpose declines unless every sort key is a plain reference, and that
it is that condition which makes the transpose sound.** Measured, the first half is right and the
second is not the whole story. `SELECT CAST(JSON_VALUE(DOC, '$.n' RETURNING VARCHAR) AS DOUBLE)
ORDER BY 1` keeps its sort in process — the key is a cast and the transpose declines — but the same
shape over a path a container has *declared* a fixed ISO-8601 instant does transpose, and the sort
renders as `ORDER BY c.at ASC`. The declaration is the second route: it says the lexical order of the
stored text is the temporal order, which is exactly the claim a sort through that cast needs. So a
cast key is refused for want of a licence rather than by kind, and a declaration is a licence. The two
plans are pinned side by side in `CosmosDeclaredFactPlanningTests`.

```
ClrCursorProject(id=[$1], p=[CAST(ITEM($0, 'price')):INTEGER])
  CosmosToClrCursorConverter
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
sort could name — a surface the row model declines, `TODO.md` section 7. What it removes is such a
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
declaration is never reached. The user manual says the chaining is optional rather than required.

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

#### The shared vocabulary, and what it maps onto

`Apache.Calcite.FullText` declares `CLR_FT_*` — a full text surface a query can be written against
without naming a store. This adapter renders them as the service's own spellings, and keeps its own
names beside them: a query already written against `FULLTEXTCONTAINS` goes on working, and one written
against the shared surface plans here as well as anywhere else. **Two spellings of one rendering**,
not two implementations.

| written | rendered |
| --- | --- |
| `CLR_FT_CONTAINS(p, kw)` | `FULLTEXTCONTAINS(p, @p0)` |
| `CLR_FT_CONTAINS_ALL(p, kw, …)` | `FULLTEXTCONTAINSALL(p, @p0, …)` |
| `CLR_FT_CONTAINS_ANY(p, kw, …)` | `FULLTEXTCONTAINSANY(p, @p0, …)` |
| `CLR_FT_SCORE(p, kw, …)` | `FULLTEXTSCORE(p, @p0, …)`, `ORDER BY RANK` only |
| `CLR_FT_RRF(score, …)` | `RRF(…)`, `ORDER BY RANK` only |
| `CLR_FT_PHRASE(text)` | the term itself |
| `CLR_FT_FUZZY(text, edits)` | `{"term": …, "distance": …}` |
| `CLR_FT_PREFIX(text)` | **refused** |

**Where a call is legal stays this adapter's business**, which is the line the package itself draws.
The shared score is refused everywhere the service's own is — a projection, a `WHERE`, a derived
table — because those are `SC2240` here whatever the call is spelled.

**A phrase renders as the term itself**, because that is already what the service reads a multi-word
term as. The constructor exists because the bare spelling is not portable: `FullTextContains(c.text,
"red bicycle")` is a phrase here, while PostgreSQL's `plainto_tsquery` reads the same two words as
`red & bicycle` and matches a document holding them paragraphs apart. Same query, two answers, no
error. What the constructor buys is that the query said which it meant.

**A fuzzy term gains a spelling this adapter did not previously offer.** `{"term": …, "distance": …}`
is documented by the text of `SC2241` — the refusal a keyword *array* earns — and was recorded here
as a form not offered. It is offered now, because the shared vocabulary gave it a name.

**`CLR_FT_PREFIX` is refused, and not approximated.** The service matches whole analyzed terms and has
no prefix form. `STARTSWITH` over the same property is not the same question — a substring test over
the stored text against a prefix over the analyzer's terms — and answering a different question is
worse than refusing this one. The call then has no body, so the query says so.

**One route, never both.** The declarations are merged into `CosmosSchemaFunctions`, which is where a
connection finds them; a host assembling its own planner chains `FullTextOperatorTable.Instance()`
instead. Registering both leaves two candidates for one name, which reaches a type-precedence pass
that a single candidate skips — and an `ARRAY` argument throws there rather than declining, with
`IllegalArgumentException: must contain type: ANY`. The package's README records it against
`SqlUtil.lookupSubjectRoutines`. It is not a hazard Cosmos's own names have, their `searched` position
never being an array.

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

**The text was not the whole of it: the value folded has to be ASCII too.** This section used to call the rewrite
exact on the pattern's text alone, and the measurement said otherwise (`reports/verify-strings.md`): `UPPER(s) LIKE
'%SS%'` keeps `aßb` in Calcite, which folds ß to `SS`, and the case-insensitive `CONTAINS` keeps nothing, on both
targets; Azure loses U+212A under `LOWER … '%k%'` and U+0130 under `'%i%'` as well. So the fold is held to what `UPPER`
and `LOWER` are held to — its value's inputs proved ASCII (*What a string function computes, asked of its
characters*) — and over a path nothing proves ASCII the fold and the pattern stay in process, in either spelling, the
plain `UPPER(c.s) LIKE @p` folding at the service as wrongly as the flag does.

Over a text accessor the fold has the gap `LIKE` has — `UPPER(30)` is `30` in Calcite, having folded
the rendering, and undefined at the service — so `UPPER(JSON_VALUE(…)) LIKE '%ACADIA%'` is declined
and weakened like `LIKE`: the split rule rebuilds the fold over the raw value, and the guard's own
comparison is what renders as `CONTAINS(c.name, 'ACADIA', true)`, under `NOT IS_STRING(c.name) OR`, where the
path is proved ASCII. Where it is not, the guarded comparison is refused with the fold, and the weakening is the
presence it implies; it used to push the rewrite there, rechecked above, and the recheck could not restore the rows
the service dropped.
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
`CLR_ST_GEOG_*` operators that read coordinates as WGS84 and answer in metres, over S2.

**There is still no `GEOGRAPHY` type, and that is a decision rather than an omission.** A geography
and a geometry are the same type carried by the same class, and the operator's name is the whole of
the marking. `SqlTypeName` is a closed enum, so a type of the package's own has to impersonate one of
Calcite's — and the enum is the key to every table that makes a type behave. A name with no entry in
the assignment table is asserted on rather than rejected, so a function declared through a schema over
such a type takes the validator down. Since a schema is the only way an adapter brings its functions
with it, the type gave way to the registration.

**What that costs is a mixed expression nothing refuses.** `CLR_ST_GEOG_DISTANCE(ST_BUFFER(g, 0.1), h)`
buffers in degrees and then measures in metres, and both halves run. This adapter cannot close that;
it is a property of there being one type for two readings.

**What this adapter adds is the one refusal it can make.** The Cosmos spelling of every one of these
is the *unprefixed* one, so what a rendered `ST_DISTANCE` means at the service is decided by the
container's `geospatialConfig` and not by the name in the query. So `CosmosRexTranslator` refuses to
render any `CLR_ST_GEOG_*` over a container reading `Geometry`. That is unlike the full text gate, which
exists because the service returns an *error*; here the service returns an *answer*, which is the
worse failure and the reason this one is checked while planning.

**Measured, and the numbers are the argument.** The same statement over the same two points, against
two containers differing only in `geospatialConfig`:

| container | `ST_DISTANCE(c.location, <point>)` |
| --- | --- |
| `Geography` | `1342.1433132701966` — metres |
| `Geometry` | `0.014142135623733162` — the planar hypotenuse in degrees |

Nothing in either response says which question was answered.
`CosmosGeographyServiceMeasurementTests` holds this and the rest of the forms.

**A geography is not promoted to a column, and does not need to be.** The row model is unchanged: the
document column, `DOC`, and the columns the service guarantees. Nothing in Calcite converts a document
value into a geometry, so a shape in a document reaches an operator by being parsed out of text:

```sql
CLR_ST_GEOG_DWITHIN(CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location')), …, 1000)
```

In process that is exactly what happens. **Pushed down it is not.** `JSON_QUERY` over `DOC` already
resolves to a document path, so the constructor collapses onto it and the statement names the property:
`ST_DISTANCE(c.location, {…}) <= 1000`. The service reads that property as the shape, so the text and
the parsing are a round trip it never needed. A constructor over a literal is written out as the object
instead; over a computed string it is declined, because rendering one would mean evaluating it and
evaluating it is what the service is being asked to do.

**Overloading Calcite's own constructor does not work, which is why these operators carry their own
names.** Supplying a better implementation of `ST_GEOMFROMGEOJSON` cannot be made to take effect:
`SqlUtil.lookupRoutine` resolves across every chained operator table by parameter match, so Calcite's
`VARCHAR` overload beats an adapter's `ANY` one regardless of chain order. Hence
`CLR_ST_GEOG_GEOMFROMGEOJSON` and its siblings, named for this adapter rather than overloading Calcite's.

**What is in scope is what Cosmos evaluates** — `ST_DISTANCE`, `ST_WITHIN`, `ST_INTERSECTS` and
`ST_ISVALID` — with `CLR_ST_GEOG_DWITHIN` rendering as a distance comparison, Cosmos having no counterpart.

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
member, so `CLR_ST_GEOG_GEOMETRYTYPE` over a stored geography is `c.location.type` — an ordinary property
read, no spatial function and no spatial index involved. The vocabularies agree for anything a
container can hold: JTS also spells `LinearRing`, which GeoJSON has no member for, so a stored shape
cannot be one. A geometry built inside the query has no path and is declined, which is also the case
where that difference could otherwise have appeared.

`CLR_ST_GEOG_ASGEOJSON` is the second, and it is a projection only. The document already holds the
GeoJSON, so parsing it into a geometry and writing it back out is a round trip the service never asked
for; the property is selected instead and read as the JSON the service sent — the same
`CosmosReading.Json` the `DOC` column takes, and for the same reason, the value being an object
where the projection is declared `VARCHAR`. In a *predicate* the call is declined, because the column
carries text where the path carries an object and a comparison against one is not a comparison against
the other.

**Selecting the shape itself is the third, and it is the constructor with nothing around it.** A query
returning a geography writes `CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location'))` in the
select list, the same expression it would write to reach any operator, and the same collapse applies:
the path goes down and `CosmosJson` puts the constructor back, reading the GeoJSON object into the JTS
geometry through `GeographyFunctions.FromGeoJson` — the geography package's own function, so the
pushed column and the in-process constructor are one rather than two. The SRID is what makes that
matter rather than a preference: the geodesic constructor stamps 4326 and Calcite's planar GeoJSON
reader does not.

**Until #149 there was no reading for `GEOMETRY` at all**, so the column planned, rendered, executed,
and then raised *No Cosmos JSON reading is defined for SQL type 'GEOMETRY'* at the first row. It had
been latent since the operators went in and became reachable when projection push-down was fixed
(#145): a projection that used to stay in process now goes to the service, so a query returning a
shape started failing where it had worked.

**This projection is guarded where the operand form is bare**, which looks like an inconsistency and
is the opposite. The nested-accessor guard is held back for a geography *operand* because the function
consumes the object the path holds and `IS_PRIMITIVE` is false of it — recorded above, under the one
exception a test found. That argument is about the scalar guard and about an operand. A projected
shape is the whole column, and what it needs is the guard `JSON_QUERY` itself carries: object or
array, not scalar. Measured in `CalciteGeographyReadingMeasurementTests` — in process a scalar at the
path makes the accessor answer null and the constructor never runs, while the bare path would have
sent the scalar and the reader would have raised over text that is not GeoJSON. An array raises on
both sides, so the guard admits exactly what agrees.

**A geography a caller passes in is bound as the same object.** A constant is inlined into the SQL,
which is only possible because the plan holds it; a parameter's value arrives with the execution, so
it is bound like every other and `CosmosJson.ToGeoJsonValue` shapes it on the way. That has to be
said out loud because a geography is the one type whose constant form need not be a bound value: the
constructor over a literal is written into the statement, so the agreement between a literal and a
parameter is reached here rather than inherited.

**A connection folds the constant before the adapter sees it, and the folded form was refused.** A
bare planner leaves `CLR_ST_GEOG_GEOMFROMGEOJSON('{…}')` as the call `WriteGeographyLiteral` writes
out; a connection's preparation reduces constant expressions first, evaluates the constructor, and
hands over `POINT (-111.5 38.3):GEOMETRY` — a literal `GetLiteralValue` had no case for. So a distance
to a literal point pushed in every planner test and stayed in process through every connection, the
sort with it. The folded shape is a value the plan holds, so it is bound like any other literal, and
as the object a geometry parameter is bound as: `ToGeoJsonValue`, without `crs`. The two constant
forms then differ in where the object is written — inline or behind a name — and not in what it is.
`CosmosProject.IsGeographyLiteral` takes the folded form too, a shape the plan holds being no more
null than the call that made it.

Left alone the bound value was the JTS geometry itself, and the SDK's serializer wrote the IKVM
object graph — `$0`-keyed, assembly-qualified, three kilobytes for a point — which the service
received in place of a shape. Where the statement also *projected* the parameter it came back and was
read as a geography, and that is the stack #154 reports. What is bound is a CLR object graph rather
than text or one serializer's tree: the parameter goes to `QueryDefinition.WithParameter` and is
written by whichever serializer the client carries, and a dictionary and a list are the two shapes
every serializer writes as an object and an array. Text would be worse than wrong — it would arrive
as a JSON *string*, and a spatial function over a string is not one over a shape.

The `crs` member `AsGeoJson` writes is dropped. A geography is WGS84 and has no second reference
system to be in, so it says nothing; what decides it is that the constant form — whose every emitted
shape has been executed against an account — carries the caller's own text and no `crs`, and sending
one where the working spelling sends none would be a second convention tested nowhere.

**Binding it was half of it; the parameter also had to be written, and for a while it was not.** A
caller types a parameter with `CAST(? AS GEOMETRY)` — Entity Framework does for every spatial
argument — and the validator types `?0` from the cast and keeps it, so a plan carries
`CAST(?0):GEOMETRY` over a parameter already typed `GEOMETRY`. `WriteCast` took a cast over a literal
only, so `CLR_ST_GEOG_DISTANCE(location, CAST(? AS GEOMETRY)) < 50000` was declined whole: the
comparison stayed above the statement, the sort became a `ClrCursorSort`, and every document crossed
the wire to be measured, where the same query against a literal point pushed both (#187). The cast
converts nothing, so `CosmosRexTranslator.IsGeographyParameter` names the shape and the parameter is
written where the cast stood — `ST_DISTANCE(c.location, @p0)` — the way the constructor falls away
over a literal. Only `GEOMETRY` to `GEOMETRY`: a cast into one from text is a parse, and nothing here
performs it.

**That is sound only if the slot holds a geometry, and measurement says it can hold nothing else.**
Text in the slot would reach the service as a JSON string, `ST_DISTANCE` over a string is undefined,
and every row would be dropped in silence. It cannot arrive: the ADO.NET driver converts a parameter
to its declared type before execution — `CalciteGeometryParameterMeasurementTests` — so WKT text
reaches the data context as a JTS geometry, and GeoJSON text or a number fails with *Unable to parse
WKT* before either plan runs. The type the plan already has is therefore the whole of what the
rendering needs, which is the position every other parameter is in.

**A distance to a parameter is as orderable as a distance to a literal.** The sort's licence under
the default placement is that no key is null or undefined, and a declared geography at the stored
path settles that beside a literal, because a literal written into the statement is a shape.
A parameter can be null, or a shape the service will not measure, which a literal cannot — and
either makes the distance null or undefined in *every* row at once. No key is then placed against
another, so the two placements have nothing to disagree about, and the stored operand is still the
whole of what the declaration has to close. `CosmosProject.SortableOperandsOf` passes over a
parameter as it does a literal.

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

### A join of a container to itself

A federation presents one container as several views, each a projection of the same documents narrowed by
a discriminator, and a table-per-type mapping joins them back together on the key they share. Every view
reads the same document by the same key, and each read brings the document whole. Measured over the
link views in `CosmosSelfJoinTests`, before #177: the map links on one map read `links` three times, and every link with its body
read it five times, joined by four hash joins — and two of the reads carried no predicate the query asked.

`CosmosSelfJoinRule` reads such a join as one read:

```
Join(l.k = r.k)                          Project(P, CASE WHEN M THEN Q END)
  Project(P, Filter(F, Scan(c)))    →      Filter(F, Scan(c))
  Project(Q, Filter(G, Scan(c)))
```

**Why it is sound.** Each input yields at most one row per document, each a function of its document.
Where the join equates what a `UNIQUE` constraint's keys depend on, any pair it makes is a document paired
with itself, and the join becomes a function of one document. Call `M` the other side's filter and the join
condition, both evaluated over that document:

| join | one read |
| --- | --- |
| inner | `σ(F ∧ M)`, both sides' columns |
| left | `σ(F)`, each right column `CASE WHEN M THEN q END` |
| right | the mirror |
| full | declined: a document with a null key is one row merged and two joined |
| semi, anti | declined for now; the same substitution answers them |

The key equality `k = k` becomes `k IS NOT NULL` by that substitution, which *is* the join's null
semantics — a document holding no key pairs with nothing — so no case needs handling of its own. A stricter
right filter is a stricter `M`; the left row survives it with nulls. And it closes over itself: what it
produces is again a projection over a filter over the scan, so the join above merges too, and the five reads
become one. A merged left join carries its key as `CASE WHEN M THEN k END`, which is `k` or null and equal to
anything only where it is `k`, so the key is read through it.

**A page on the preserved side is taken after the join (#192).** A paged query over a table-per-type
hierarchy pages the root first and joins the rest onto the page, so a sort with an offset and a fetch sits
between the join and the projection, and the merge does not see a self-join: the page was read with its
sort and limit, and the bodies by a second read of the whole container, hash-joined in process. But a left
join keeps every row of its left side, and where the key join holds the right side adds at most one row to
each — the right side yields one row per document and pairs a document only with itself — so the join maps
the page's rows one to one onto its own:

```
Join(LEFT, Sort(c, o, f, A), B)   =   Sort(c, o, f, Join(LEFT, A, B))
```

the sort's keys being the left side's columns, which sit where they did. That is the merge's own proof, so
the rule takes a sort above either side as a fourth operand rather than a rule of its own: the left side
under a left join, the right side under a right join with its keys moved past the left side's columns, and
nothing else. An inner join can drop a row of the page — a link that is not a map link — and a page taken
after it would reach past that row for another; a page on the side an outer join does not keep is what may
not pair, and taking it afterwards would page the other side. Ties in the sort are no new freedom: which
of the tied rows a page keeps was the plan's choice before, and still is.

**It fires first, and the join it replaces is explored no further (#201).** As an ordinary rule the merge
waited in Volcano's queue behind every match the joins had made — filters pushed into a join and out again,
joins commuted, each condition reduced — and every one of those was a new expression in a set of its own,
which the merge then fired on again, each time producing another spelling of the same read. Planned offline
through a host's own prepare, the five-view hierarchy filtered on its discriminator registered 8,250
expressions and took three minutes; paged and ordered by a joined container's name, four and a half. The
merge is the one rewrite that makes everything after it small, so it is now a `SubstitutionRule`: Volcano
queues its matches ahead of all others and, with `autoPruneOld`, prunes the join it matched, so no other
rule fires on that join again. Pruning is sound because what replaces the join is always at least as good —
one read in place of several, implementable wherever a scan is — so no plan the join could have led to is
lost. The same two queries now register about 1,200 expressions each and plan in a few seconds, from the
same plans.

Two things had to change for it to fire at all. A substitution rule is a transformation rule, and Volcano
matches a transformation rule's operands within one convention: a child in another is skipped. The scan is
in the container's convention and the filter above it is not, so the operand naming the scan never matched,
and the rule silently never ran. So each side is matched as a projection — or a projection over a
projection — and read down to its scan through the equivalence sets beneath it, composing every projection
and carrying every filter's condition down beside it. That second half is the other change: a subquery
projects a view's columns again, and the join above a merged one sees the query's projection standing over
the merged read, so the side was a projection over a projection, and the merge used to wait for Calcite's
`PROJECT_MERGE` to remove it — by which time the joins had been explored. Read through, the whole hierarchy
collapses before any other rule runs. A projection holding a window or a non-deterministic expression is
not read through, because substituting it moves a condition beneath it.

What is left is Calcite's own exploration of the projections and filters above the one read, and constant
reduction: `ReduceExpressionsRule` compiles each set of constants it folds, through Janino, which under IKVM
is a quarter of a second each time, and the discriminator's comparison is folded once per filter spelling
that carries it — fourteen compilations of one expression in the first query. That is the host's executor
to cache; see `TODO.md`.

**The uniqueness is asked of the container, and of both sides.** The tempting route is Calcite's own:
`RelMdColumnUniqueness` on each input. It is the wrong question. With park links on the left and map links
on the right, a guid unique among the map links still lets one park link and one map link share it; the
join pairs them, and one read of each document would not. What licenses the merge is that the key names at
most one document among *everything either side reads* — which a `UNIQUE` constraint over the container
says, and a filtered one says only where both sides' predicates prove its filter. See *A constraint says what
holds across documents*.

**Equal join keys have to force equal constraint keys**, and there are two ways to know they do.

- **A constraint key that is an accessor stands for a stored value**, so the join has to compare a reading
  of that value that is faithful. Uniqueness of the stored value does not give that on its own: `JSON_VALUE`
  reads the number `1` and the string `"1"` as the same text, and a cast to `UUID` reads `ABC…` and `abc…` as
  the same value. So a text accessor or a promoted column counts where the facts give the path one scalar
  type, and a `UUID` cast counts where they give it a canonical UUID form — the same fact that makes the cast
  push. The facts asked are those of every document either side reads: the two sides' filters' disjunction,
  which the oracle splits over its arms and keeps what both prove, and the declaration alone where a side has
  no filter — so a form both sides' kind declares is the key's, and one only a side's kind declares is not.
- **Any other constraint key counts where it is computed from the equated expressions alone**, since equal
  inputs make equal outputs. `UNIQUE (LOWER(code))` covers a join on `code`. It does not work the other way
  round: `UNIQUE (code)` does not cover a join on `LOWER(code)`, because two codes can share a lowercase. The
  computation may pass only through operators that are null exactly where an operand is. Otherwise a key could
  be null where the join key is not, which would put the pair outside the claim. Calcite's
  `Strong.Policy.ANY` answers that for the operators it classifies, and a short named list covers the string
  functions it does not.

**Why it is not Calcite's.** `ProjectJoinRemoveRule` removes a join only where none of one side's columns is
used. `LoptMultiJoin.RemovableSelfJoin`, inside `LoptOptimizeJoinRule`, needs the key to be a column of the
table with a unique key reported for it. Here the key is an expression over `DOC`, with no ordinal a
`getStatistic` key could name, and both sides contribute columns. Nothing in Calcite 1.43 merges that.

**What it took to push the result.** Over one document `k = k` is `k IS NOT NULL`, and an identifier key is
a `UUID` cast, which the service cannot evaluate — so every merged join carried a conjunct that kept its
whole predicate in process. Where the path's form is a declared canonical UUID a cast is null exactly where
its text is, and `CosmosFactRewriter` now lowers the test onto the text. With that, the map links on a map
plan as one `CosmosFilter` over one scan: `type = 'Link'`, `data.type = 'map'`, `mapId = '…'`,
`guid IS NOT NULL`, the projection pushed with it.

**What it does not reach alone, and why.** Every link with its body reads once, but measured on the
emulator that read is unfiltered: the merged projection holds `CASE` columns that do not push, and the
filter beneath them stayed in process with them. That is not this rule's. The `Link` view alone showed it
before #177, as the unfiltered read the plan began with, and the cause is the row count — the emulator
reports zero documents for every container, and at zero rows every plan ties and the tie went to the one
that pushes nothing. #180 reports a zero count as unknown; with it, every link plans as one `CosmosFilter`
over one scan, with only the `CASE` columns computed in process.

**And the `CASE` columns now push too (#192).** Each is `CASE WHEN M THEN q END`, and `M` carries the
key's null test over a `UUID` cast — the conjunct the paragraph above lowers in a filter, and nothing
lowered in a projection. A `CASE` takes an arm where its condition is true and passes over it where the
condition is false or unknown, which is the distinction a filter draws and the only one the rewriter
preserves; so `CosmosFactRewriter.RewriteConditions` rewrites each condition as a predicate, and the
projection rules push what it gives them. It mattered most for a page: a sort above a projection that
stays in process stays in process with it.

**A filter through two merged views is a filter through two `CASE`s, and nothing walked into one
(#183).** Through one view a host's simplifier takes `CASE WHEN M THEN q END = X` apart itself. Through
two, a host was seen to leave `OR(CASE(base, CASE(type = 'park', parkId = X, false), false), …)`: each
arm compares a `UUID` cast, which can raise, and Calcite's simplifier will not turn a `CASE` into a
conjunction over an arm that can — a conjunction need not spare the arm the rows its condition
excludes. The fact rewriter stopped at the `CASE` too, so the arm never lowered and the whole
disjunction stayed in process. It now goes in. `CASE WHEN p THEN q ELSE FALSE END` is true on exactly
the rows `p AND q` is, the `ELSE` never being true, and through `AND` and `OR` — the only nodes the
rewriter is reached through — a part true on the same rows makes a whole true on the same rows; under a
`NOT` the two would differ where `p` is null, and nothing there is under one. `q` is lowered against
what `p` proves as well as what the query does, since it is read only where `p` holds — which is the
merged view's discriminator doing what it is for — and once `q` compares stored strings it raises
nowhere, and the `CASE` becomes the conjunction. A `q` still holding a conversion keeps its `CASE`, so a
conjunction rechecked in process never evaluates a cast over a document of another kind. The service
gets `(type = 'park' AND parkId = X) OR (type = 'map' AND parkId = X)`, beside the shared base.

**A merged column used inside an expression is the same `CASE` one step further out (#189).** A distance
from a merged view's location, with nothing else narrowing the query to that view, reached the filter
rules as `CLR_ST_GEOG_DISTANCE(CASE(p, location, null), ?) < 50000` — and `p` carries the left join's
match test, `IS NOT NULL` over a `UUID` cast, which the rewriter lowers only where it reaches it. It did
not reach inside an expression, so the filter stayed in process and every link was read to be measured.
The argument is the one above with one more step. Every operator between the `CASE` and the comparison
is null where its operand is — named, not assumed: a cast, arithmetic, and the distance, measured to
answer null in process for a null on either side — so where `p` is not true the comparison is unknown
and a filter keeps nothing, and where it is the `CASE` is `location`. The comparison is therefore true
exactly where `CASE WHEN p THEN <the comparison over location> ELSE FALSE END` is, and
`CosmosFactRewriter.TryLiftStrictCase` builds that and hands it to the rewrite above. An operator that
answers for a null — `COALESCE`, `IS NULL`, a `CASE` — keeps rows `p` excludes and is not walked
through.

**The distance's arm can raise, so its `CASE` stays, and `p` goes beside it as well.** The arm builds a
geography from the document, and measured, that raises over an object that is not GeoJSON — which a
document of another kind may hold at the same path; the end-to-end suite's park link does. Taken apart,
a recheck in process could build it there. So the service gets
`p AND (p ? ST_DISTANCE(location, @p0) < @p1 : false)`: the `CASE` keeps the arm to the rows `p`
holds for, and `p` beside it is a conjunct the service applies through its index. What it does not get
is the spatial index, which does not serve a distance inside a ternary; the bare
`p AND ST_DISTANCE(…) < d` is the same rows at the service, and writing it is the translator's to
license — `TODO.md` says what would.

**What a consumer has to state.** A join on the partition key and `id` needs nothing: the service enforces
it. A join on anything else needs a `UNIQUE` constraint the model declares, and that is a promise nothing
checks. For the links in `CosmosSelfJoinTests`, partitioned on `/linkId` and keyed by a guid, that is a
choice between three:

- **`UNIQUE (JSON_VALUE(DOC, '$.data.guid'))`**, declared. Keeps the views as they are. True by whatever
  convention the application keeps, and by nothing the adapter can check across every writer and every
  document.
- **Keyed on `linkId` with `id`.** Provable today with nothing declared, at the cost of a composite key
  where the views had one column.
- **Keyed on an integer id**, where the application writes one value into `data.id`, `linkId` and the
  document `id` alike. Not provable from uniqueness alone; provable from `UNIQUE (pk, id)` plus `CHECK`
  constraints equating those paths, once `CHECK` is read — a promise each document can be checked against,
  rather than one about every pair.

### Reading a value back

A row arrives as one JSON value and `CosmosJson` reads it into the representation Calcite holds that
SQL type in — **Java boxes, not CLR primitives**. A CLR `int` in a row compiles and then fails at the
first Calcite operator that casts it, a long way from where it was produced.

| JSON | as `ANY` / inside `MAP` | as a declared type |
| --- | --- | --- |
| string | `string` | `CHAR`, `VARCHAR`; `UUID` where a container declared the spelling |
| object, GeoJSON | `java.util.LinkedHashMap` | `GEOMETRY`, where a geography operator asked for one |
| number, whole | `java.lang.Long` | `TINYINT`…`BIGINT` as their boxes, `DECIMAL` from the raw digits |
| number, fractional | `java.lang.Double` | `REAL`, `FLOAT`, `DOUBLE` |
| `true` / `false` | `java.lang.Boolean` | `BOOLEAN` |
| object | `java.util.LinkedHashMap` | `MAP` |
| array | `java.util.ArrayList` | `ARRAY`, `MULTISET` |
| `null`, or absent | `null` | `null` |

`UUID` is the one row of that column a container has to earn. Cosmos has no such type, so the value is
a string whose spelling a declared form pinned; the conversion is Calcite's own `UuidValue.fromString`,
which is what makes a pushed projection and the in-process cast it replaced the same conversion rather
than two of them — and the box is the `UuidValue` that method returns, not the `java.util.UUID` inside
it. See *A form reads back as well as it compares*.

`GEOMETRY` is the other row a query has to earn, and it earns it the same way. Cosmos has no such type
either, so the value is the GeoJSON object a geography operator named; the conversion is
`GeographyFunctions.FromGeoJson`, which is what `CLR_ST_GEOG_GEOMFROMGEOJSON` is implemented as, so
again the pushed column and the constructor it replaced are one function. See *Geography*, and #149 for
what it cost to have the statement without the reading.

Two choices worth stating. A whole number reads as a `Long` rather than a `Double` so that an
identifier or a count does not surface as `42.0`; the choice is the value's, there being no schema to
consult. And a document that disagrees with the declared type is **refused**, not coerced — reading
`42` as `VARCHAR` throws rather than yielding `"42"`, because a row type that bends to the data is a
suggestion rather than a declaration.

**A whole number is read within its type's range, and refused outside it, never saturated.** Every
integral reading — `TINYINT` to `BIGINT`, and a `DATE`, `TIME` or `TIMESTAMP` stored as a number — goes
through `CosmosJson.GetInteger`, and a number arrives as a double where it is not integer text: `42.0`, or
an integer Azure stored as a double because it was written outside int64
(`Service.IntegerOutsideLongIsStoredAsADouble`). The range is checked in doubles, and the bounds have to be
exact there. The least value, -2^(k-1), is. The greatest is not, past 2^53: `(double)long.MaxValue` rounds
up to 2^63, which is no `long`, and testing against it let a stored 2^63 through to a cast that saturated
it, so `RETURNING BIGINT` answered `9223372036854775807` over Azure's `9.223372036854776E+18`, where Calcite
raises. One past the greatest, 2^(k-1), is exact in every width, so the refusal is "at or above max + 1"
and "below min" (the mirror of the first, "at or below min - 1", rounds to -2^63 and would refuse
`long.MinValue` itself). The emulator prints 2^63 projected as `9223372036854775800`, which is valid int64
text that no reader can tell from a stored integer; that one is the emulator's (`TODO.md`, section 7).

**Text that is not UTF-8 is refused by name, by every reading alike.** Azure validates nothing about UTF-8
in a document body: whatever bytes a client writes are stored, returned and compared as written, in a value
or in a name (`Service.MalformedTextIsStoredAsWritten`; a lone surrogate written as an escape is stored as
U+FFFD instead). Calcite holds a string as UTF-16 units, and nothing it can hold compares as the service
compares those bytes. U+FFFD, which a lenient decoder makes of them, is not that: the service ranks `80` and
`C0 AF` below U+00E9 and says the raw document is not the replaced one, where Calcite would rank U+FFFD above
every unit below U+D800 and call them equal; and the document column would stop being the text the service
sent. So failing is the one reading that is not wrong, and it is made to say what it failed over:
`CosmosJson.Undecodable` raises a `CosmosMaterializationException` naming the column or path, the document
where the row says which, where in the value the bytes lie, and the bytes. `System.Text.Json`'s own refusal
named none of them and arrived mid-stream, after earlier rows had been handed out.

- **Each reading of a row's column catches the decoder's refusal and names it.** The document is named from
  the plan's bindings, not from a column's name: the column bound to the path `id`, or the `id` inside the
  column bound to the document. A point read's row is the document, so its own `id` names it.
- **The JSON fragment has to look.** A projected `JSON_QUERY` writes its value out rather than decoding it,
  and the writer replaces each byte it cannot read with an escaped U+FFFD, so it answered without a word
  where the in-process plan raised. It searches the value's strings and names first, one validation of each
  string's bytes.
- **The reader completing an order decodes its text keys strictly too**, tells a `DISTINCT`'s repeat apart
  by the bytes the service sent, and copies an extreme's row byte for byte, so it neither fails unnamed nor
  replaces in silence.

The failure is per value: a statement fails exactly where a row carries the bytes into a reading, and one
whose rows leave the value out is answered, in the same logical partition too. Reading a WTF-8 surrogate as
the lone unit it encodes is the one lenient reading that would agree with the service, and `TODO.md`
(section 7) keeps it as an option.

Nesting is not truncated: the document column carries the document and the value goes as deep as the
document does. Addressing is not limited either — `JSON_VALUE(c."DOC", '$.a.b.c')` folds the whole
path into one Cosmos path, `c.a.b.c`, array subscripts included. What depth costs is planner
metadata: nothing addressed this way has a type, a key or a collation, so it can be addressed but not
reasoned about. The one limit is that the path must be a literal — a path assembled at run time names
a property whose name is not known until the row is read, and is declined.

#### A column with no reading is refused while the plan is made

The table above is the whole of what this reads, and Calcite's type list is longer than it. Thirteen
interval types, four unsigned integer ones, three further temporal spellings and a dozen internal
names have no entry — 32 of 57 when this was written. A column typed one of them renders, goes to the
service, comes back, and fails as the first row is built.

**What that costs is not the failure but its timing.** A read that has begun has already sent a
caller `200` and the headers, so a failure there arrives as a *truncated body* rather than as an
error: nothing downstream can tell it from a short answer. #149 was exactly this, over `GEOMETRY`, and
the report leads with the truncation rather than with the missing case. So the question is asked while
the plan is being made instead, in two places that answer it differently on purpose.

**`CosmosRexTranslator.TranslateProjection` declines to push such a column**, which is the better of
the two outcomes and the ordinary refusal this adapter is built out of: the column stays in process,
Calcite computes it, and the query answers. It is asked once, after the recognisers, because each of
them returns early with a reading of its own and one question after them all is the only way to be
sure none skipped it. A predicate is not asked — a `WHERE` renders an expression the service evaluates
and returns nothing of, so what can be read back is not a question about it.

**`CosmosImplementor.RequireReadableRow` refuses to build a reader for one**, which is the half that
cannot be gone around. A translator sees expressions; a row is assembled by *rules* — a projection
split, an aggregate, a lookup join, an unnest — each composing a row type of its own, and both row
builders go through this whatever composed them. The statement then does not prepare, and a caller
gets an error while it can still act on one.

**Only a `Typed` column is asked about.** `Text`, `Json` and `JsonText` read what the service sent
whatever the plan calls it — that is why they exist, the `DOC` column being an object in a column
declared `VARCHAR`. Gating those on the declared type would refuse the row model itself.

**`CosmosJson.CanRead` is a second switch beside the reader's, and a test is what keeps it from
becoming a different one.** Deriving both from one table would put a dictionary lookup and a delegate
call on the per-value path for a question asked once per statement, so they are written twice;
`EveryTypeAgreesWithWhatCanReadSays` walks `SqlTypeName.values()` and asserts the two answer alike for
every one. A type added to Calcite, or to one switch and not the other, fails there — which is the
failure this guard exists to move earlier, applied to itself.

**Nothing reachable is refused by this today**, and that is the intended state rather than a gap.
`GEOMETRY` was the one type a pushed projection could produce without a reading, and it has one now.
Measured: an interval, a local-time-zone cast and a date difference all leave their expression above
the converter, so what the guard holds is the boundary for the next operator, cast or rule that
renders something new.

### The row model

Calcite has **no JSON type**. `SqlTypeName` in 1.41.0 has no `JSON` constant, and Calcite's
SQL/JSON functions (`JSON_VALUE`, `JSON_QUERY`, `JSON_EXISTS`, …) follow SQL:2016, where JSON is
*character data* — VARCHAR in, VARCHAR out, re-parsed per call. That is the wrong substrate.

What Calcite does have:

| Option | Availability | Assessment |
| --- | --- | --- |
| SQL/JSON over `VARCHAR` | SQL:2016, honoured by Calcite | **Chosen.** JSON is character data here, which reads as the wrong substrate and is the right one: it is what the service already sent, and it is the only shape `JSON_SET` can be written over. |
| `VARIANT` | 1.41.0 (`SqlTypeName.VARIANT`, `org.apache.calcite.runtime.variant`, operators `VARIANT`/`VARIANTNULL`/`TYPEOF`) | Not the document substrate: no shipped adapter models a whole row type on it, and `JSON_SET` cannot be written over it. **Adopted for the promoted declared-path columns** (#163), where it is exactly right — a partition-key value is a scalar whose concrete type is learned per row, which is what `VARIANT` models and what `ANY`, the top "type unknown", erases. Pushdown was the open question and is now mostly settled: the machinery reads `VARIANT` as "the value the service holds" wherever it read the accessor's `ANY`, so filter and partition-key point-read pushdown carry through. A *sort* keyed on the column does not — Calcite cannot order a `VARIANT` in process (`VariantValue` is not `Comparable`), so `CosmosSort` declines it (#165). See *Promoted columns* below and `CosmosTable.getRowType`. |
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

#### Why a targeted patch cannot be written in SQL, measured

There is no Cosmos-side way to express one. Measured against an account: no JSON-transform function
under any of a dozen names, no object spread, no `ArrayToObject` to undo `ObjectToArray`, and **no
`UPDATE` statement at all** — *SC1001, syntax error near 'UPDATE'*. Cosmos SQL is read-only; the
targeted write is `PatchItemAsync`.

Two things make this less alarming than it reads. `TableModify` has no implementation in the CLR
conventions, so an `UPDATE` the adapter declines fails to plan rather than evaluating in memory and
writing a re-serialised document. And `JSON_VALUE` carries SQL:2016's `RETURNING` clause, which
Calcite honours — `RETURNING INTEGER` types the call `INTEGER`, `RETURNING TIMESTAMP` types it
`TIMESTAMP(0)`, nullable, and the clause survives inside a view, where it presents to a
`DbDataReader` as a real typed column. So the JSON family is not uniformly stringly typed, and a
document path *can* be given a SQL type by the caller in standard SQL.

What a rule could read a patch operation off is `SET "DOC" = JSON_SET(c."DOC", '$.a.b', v)`, an
expression over a value the accessor family is typed for. That is the third rung of the ladder under
*Updating*.

#### The document column

`DOC` is the document, and the only column a statement writes. First in the row type; `VARCHAR` and
`NOT NULL`, because every row is a document; `DEFAULT` in the column strategies, every other column
being `STORED`.

Read, it is the JSON the service sent —
`GetRawText`, the original span — so it is a copy rather than a round trip and cannot differ from what
is stored in key order or number formatting. `CosmosReading.Json` is what says so, distinct from
`Text`, which renders a value the way a cast over `ANY` would and is the wrong answer for a document.

What the service sends is not one text, though, and that is measured: on the emulator a point read
and a query return the same document in different key orders, at different indentation, and the
read with a `_self` the query leaves out. So `DOC` is the bytes of *a query's* answer, and a point
read does not serve it — see *Not every statement is a query*.

**Every pushdown resolves its path in one place.** A clause that needs a document path gets it from
`CosmosRexTranslator.TryResolvePath`, so a projection, a filter, a sort, a sort with a fetch,
`GROUP BY`, `DISTINCT`, a nested path, a bracketed name, an array subscript, a numeric comparison,
`IS NOT NULL`, `UNNEST` and a lookup join on either side all reach a path the same way — and a path
assembled at run time is declined for all of them at once.

**A projection of an accessor is guarded, and that is not decoration.** `JSON_VALUE` answers a
scalar's text and null for an object or an array. Pushed as the bare path it returns the raw value,
which the reader refuses where the plan declared `VARCHAR` — a number is not a string — so the
statement threw for data the in-process plan renders as `30`. `IS_PRIMITIVE` is exactly SQL/JSON's
line, true of a string, a number, a boolean and a JSON null and false of an object and an array, so
`(IS_PRIMITIVE(p) ? p : null)` with the `Text` reading means at the service what the function means
in process, for every JSON type; over an absent property the record has it both false and undefined,
and either way the column reads null. A filter's comparison needs no guard in the positive position: a
comparison against an object is false at the service and null in process, and both drop the row.
Everywhere that difference shows — a `NOT`, an `IS NULL` — it takes the accessor's null set; see
*What an accessor reads, stated once*. And where it is the comparison's value that is read rather
than its truth — a column, a grouping key, the operand of another comparison — false and null are
different answers, and the comparison is declined; see *A predicate's value is not its truth*.

**The gates that admit a document value ask for one, not for a type.** Three of them — the text cast
and the numeric cast in a comparison, and the rendering in a projection — must spell *is this a value
out of a document* rather than *is this typed `ANY`*, a `JSON_VALUE` being typed `VARCHAR(2000)` and a
document value all the same. `IsDocumentValue` asks the question the gates mean; a gate asking the
narrower one instead fails silently, a bound the filter rule should have pushed simply not pushed,
which reads as a slower plan rather than a wrong one.

The cast to text a view writes was the shape that did not hold (#71). It is dropped over a
`JSON_VALUE` as over a value typed `ANY` — see *The same cast over `JSON_VALUE` is the same cast*
under *Casts over document values* — with the one asymmetry that the same cast in a projection is
rendered only over a value typed `ANY`, for the reason recorded there.

The path argument must be a literal, and the grammar is `$` with `.name`, `['name']` and `[0]` steps —
a wildcard, a descent or a filter has no Cosmos rendering and is refused. `RETURNING` is not rendered:
it told the plan what the service will return, and a clause that disagrees with the document fails in
materialisation rather than answering wrongly, which is what makes it worth trusting. `UNNEST` wants
`RETURNING <type> ARRAY`, and **either accessor may carry it** — this used to say `JSON_QUERY` is
`VARCHAR` whatever it is asked for and can never be an unnest source, which is wrong twice. A wrapper
clause does not change its type, but a `RETURNING` does, and measured, `JSON_QUERY` is the accessor
whose array `RETURNING` actually produces an array:

```
JSON_QUERY(doc, '$.v' RETURNING VARCHAR ARRAY)          ->  string[2]{a,b}
JSON_VALUE(doc, '$.v' RETURNING VARCHAR ARRAY)          ->  null
UNNEST(JSON_QUERY(doc, '$.v' RETURNING VARCHAR ARRAY))  ->  two rows
```

So the spelling recorded here as the one a traversal needs is the one that answers null in process,
and the one dismissed is the one that works. Pushed down it makes no difference — all three
spellings, including `StringToArray(JSON_QUERY(…))`, render the same `JOIN t0 IN c.tags`, because
what the adapter reads is the path and not the function. It makes every difference to a plan that
does *not* push: `JSON_QUERY` evaluates correctly in process and `JSON_VALUE` answers null, which is
the defect behind the array column returning nothing when a projection is lifted (#125,
ikvmnet/calcite-dotnet#152).

**That "no difference" was one traversal short of true, and closing it is what made the refusal mean
anything.** A pushed `UNNEST(JSON_VALUE(… RETURNING VARCHAR ARRAY))` returned the elements; the same
statement in process returns *no rows*, the null array unnesting to nothing. Refusing the column
alone would have moved that divergence rather than removed it — measured, with the projection
declining and the traversal still pushing, the plan is `CosmosUnnest` over the table scan, so the
adapter answered rows where the engine answers none.

So the refusal lives in `IsJsonAccessor`, the single gate a projection, a filter, a partition key and
a traversal all pass through: the spelling addresses no path *in any clause*. One expression, one
meaning, which is the whole of what #119 was filed about. The cost is real and is named rather than
hidden — a caller who wrote this and got rows now gets none, because that is what the statement
means. `UNNEST(JSON_QUERY(… RETURNING VARCHAR ARRAY))` agrees pushed and in process, and is what a
caller should be writing.

**Element nullability is the type's, and there is no syntax to say otherwise.** `RETURNING INTEGER
ARRAY` reads back as `int?[]`: nullability in SQL is a constraint rather than part of a data type, the
`RETURNING` clause names a type, and measured, `INTEGER NOT NULL ARRAY` and `INTEGER ARRAY NOT NULL`
are both parse errors. Calcite forces the array *and* its elements nullable deliberately —
[CALCITE-6208](https://issues.apache.org/jira/browse/CALCITE-6208) — because non-null elements let a
`WHERE c IS NOT NULL` over an unnested array be optimised away and rows be lost. `CosmosJson.GetList`
reads each element as the declared component type and carries a JSON null through as a null entry,
which is the same reading.

That is also what Calcite does with it in process, and it is worth knowing how literally. Measured
against Calcite's runtime, `RETURNING` a numeric or boolean type other than `DECIMAL` performs no
conversion at all: the scalar is cast to the Java class the JSON reader would have built for the type,
and anything else throws. `RETURNING INTEGER` returns a stored `30` and throws on `30.7`, on `"30"`, on
`true` and on `3000000000`, which parses as a long; `RETURNING DOUBLE` and `FLOAT` return `30.0` and
throw on `30`, which parses as an integer, and `RETURNING BIGINT` throws on `30` for the same reason;
`SMALLINT`, `TINYINT` and `REAL` name classes the reader never builds and throw on every scalar;
`BOOLEAN` throws on `"true"`; and `NULL ON ERROR` does not catch any of it
(`CalciteJsonValueMeasurementTests.EachNumericOrBooleanReturningAssertsItsClassButDecimal`). So a
comparison through an asserting `RETURNING` pushes exactly, as it always has: for every document Calcite
can evaluate, the declared type is the stored type and the service compares the same value. What the
pushdown changes is the document Calcite would have thrown on, which the service excludes instead —
the caller's declaration held rather than checked, the same asymmetry the reading side already accepts.

**`DECIMAL` converts, and this paragraph used to say it did not.** It was measured only over numbers,
where `DECIMAL(3, 1)` returning `30.75` unrounded looked like the same assertion. It is a parse: a
stored number is converted to its exact value, a stored string to the number its trimmed text spells,
so `"2"`, `" 2 "` and `"2.0"` are each 2 to it, and only what does not parse raises — a boolean, `"x"`,
`""` — again past `ON ERROR`. Two more things separate it from the stored value even over numbers. A
fraction is the reader's double converted exactly, so a stored `0.1` under `RETURNING DECIMAL(10, 1)`
is not equal to `0.1` and is greater than it, where the service's doubles are equal. And the literal
it is compared with is cast to the comparison's type, which against the default `DECIMAL(19, 0)`
truncates it toward zero: `= 2.5` keeps a stored 2, and `= -2.5` a stored -2. Measured, first by the
predicate oracle's checker and then here
(`CalciteJsonValueMeasurementTests.ReturningDecimalParsesTheRendering` and
`ReturningDecimalReadsAStoredFractionExactlyAndTruncatesItsLiteral`).

Written as the path it was wrong three ways. The translator sent `= 2` as `c.body = 2`, which loses
every document storing `"2"`, and the null test and the guard under a `NOT` took `IS_NUMBER` as its
null set and lost them too. A column, and a sort or a grouping over one, was sent back as the stored
string where the plan declared a number, and failed to read where Calcite answers the number the
string spells. And the fact extractor of the day read the equality as exact, the path holding the
number 2, which beside a declared `"type": "string"` is a contradiction: the filter was rewritten to
`FALSE`. Over a container declared so, holding `"2"`, `" 2 "`, `"3"` and `"2.0"`, `= 2` returned
nothing where Calcite returns three documents, and `> 2` nothing where it returns one.

So `CosmosRexTranslator.IsParsingJsonValue` names it, and `IsJsonAccessor` refuses it as the array
`RETURNING` is refused: it addresses no path in any clause, so a column, a sort key, a grouping key, a
partition key and a fact all stay where Calcite computes them. What it still implies is what a cast to
a number implies — it *is* that cast, written as a clause — so `TryNumericCastOperand` reads it, and a
comparison through it pushes the window `CosmosNumericWindow` states and is rechecked above:
`IS_DEFINED(c.body) AND (NOT IS_NUMBER(c.body) OR (c.body > 1 AND c.body < 3))` for `= 2`. The fact
extractor reads the same window, which binds numbers and says nothing a declared string contradicts.
Over a path declared an integer the type test goes, and the service does all the narrowing. The clause
adds no operand, so the value the window bounds is written out: the same call typed as the bare
accessor, which is the rendering a cast reads.

**The clause applies no precision, and the comparison may.** The window was first said to hold because
the clause never checks its precision; it does not, but the comparison casts one side to the other's
type, measured (`AComparisonThroughReturningDecimalCastsTheWiderSide`). Where the literal's type has
more digits or scale than the clause's — an `INTEGER` has ten — the value is cast up:
`RETURNING DECIMAL(5, 1) = 12345` compares `CAST(…):DECIMAL(11, 1)`, which raises on a stored 2^60.
The window does not read through a cast to `DECIMAL`, so nothing is pushed: `RETURNING DECIMAL(10, 2) = 2.255` reads the container whole, which
is correct and recorded in `TODO.md`. Where the two together would pass nineteen digits, the literal is
cast down instead: against the default `DECIMAL(19, 0)` that truncates it, which moves it by less than
one, and where its whole digits do not fit it raises — `RETURNING DECIMAL(19, 18) = 50` raises on every
row. Read as 50, the window answered there: over a declared number the service kept nothing between 49
and 51, and over a path declared at most 10, `> 50` was rewritten to `FALSE`. So
`CosmosNumericWindow` reads no bound through a cast its literal does not fit. And a bound past 2^53 a
double does not hold is sent as a `long` — *Verified against the emulator*. Before, the window around
`= 2^60` lost the document holding 2^60, which the exact comparison it replaced had kept.

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
| Partition key path(s) | `VARIANT` | `getDistribution`; single-partition detection |
| Composite index paths | `VARIANT` | `getCollations`; `CosmosSortRule` legality |
| Computed properties | `VARIANT` | Named projections |

**A promoted declared-path column is typed `VARIANT`** (#163). Cosmos requires a partition-key
value to be a scalar — string, number, boolean or null; an object or array at the path is not a
valid key — but *which* scalar is a per-row fact the schema does not fix, so the column is a value
the service holds whose concrete type is learned at read time. That is the semi-structured shape
`VARIANT` was added for; `ANY`, the top "type unknown", would erase that it is a document value at
all. It stays nullable — a document may omit the path, landing in the "none" logical partition — so
the planner does not rewrite `COUNT(x)` into `COUNT(*)`. The service-owned `id`, `_ts` and `_etag`
keep the concrete types they are guaranteed to carry.

The typing is load-bearing for pushdown, and the change from `ANY` to `VARIANT` is carried through
rather than dropped. `CosmosRexTranslator.IsRenderedDocumentValue` reads a `VARIANT` operand as "the
value the service holds", exactly as it read the `ANY` an accessor is re-typed to, so a cast over it
renders rather than converts. Calcite coerces the *other* side of a comparison with a `VARIANT`
column — a literal becomes `CAST(… AS VARIANT)`, a `LIKE` subject `CAST(… AS VARCHAR)` — where an
`ANY` column left both bare; `StripVariantCoercion` and `StripTextCoercion` see through those
coercions so the point-read and fact extractors read the literal and the raw path renders as it did.
A rendered-text comparison is still declined against ambiguous text, `WriteCast` refusing what
survives. See `CosmosTable.getRowType`.

**In process, a `VARIANT` is not the raw object an `ANY` is, and that cuts two ways.** Calcite's
runtime carries a `VARIANT` column as an `org.apache.calcite.runtime.variant.VariantValue`, so where a
predicate over the column is evaluated in process rather than pushed — a residual, or the differential
oracle — the value must *be* one or the cast to it throws. `CosmosJson.GetVariant` boxes the scalar the
service holds with the runtime type its JSON shape names (`VariantSqlValue.create` over a
`BasicSqlTypeRtti`); a JSON null stays SQL `NULL`. The other cut is ordering: `VariantValue` is not
`Comparable` and names no order across types, and the SQL standard defines none for a variant — so
Calcite cannot sort the column in process, no oracle can measure a pushed sort, and `CosmosSort`
declines a sort keyed on a `VARIANT` column rather than push one on faith. A caller orders by
`JSON_VALUE` over the path instead, which is text and sorts as text in process — and at the service
only where what is known of the rows says the path holds a string, the declaration or a filter keeping a
kind that declares it, the service ordering the stored value rather than its text; a `RETURNING` likewise
only where the rows are known to hold its type (*Ordering a sort key the way it reads*). This waits on an
upstream Calcite decision about a canonical variant order; see #165.

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
  Cosmos. In the document this is representable — the property is absent versus present-and-null —
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
    and a guard around the whole negation discarded it. Two things the guard rests on were learned
    later and are under *Expression translation*: an accessor is null for more than the path is
    (*What an accessor reads, stated once*), and the position belongs to the connectives' operands
    and to nothing else (*A position belongs to a predicate*). A third bounds what the guard is for:
    it makes the predicate's truth right, and its value — read by a column, a grouping key or an
    enclosing comparison — is a different question (*A predicate's value is not its truth*).
  - **A grouping key.** The service groups an absent property apart from a present-and-null one, and
    SQL has one `NULL`. The key is therefore grouped and projected as `IS_DEFINED(p) ? p : null`,
    which is SQL's reading of both — except where the key is known present in every row grouped: `id`,
    `_ts` and `_etag`, which the service guarantees, a path a schema requires, and one the filter
    beneath proves present (`WHERE x IS NOT NULL`, `x = 'bikes'`), where normalising would buy nothing
    and cost the plain path form an index is defined on. See `CosmosAggregate.GroupingKey`.

    A key read as text is grouped by its rendering in Calcite and by the value at the service, and
    those part wherever a path holds more than one type, `30` and `"30"` being one group or two. It
    groups at the service only where what is known of the rows grouped says the path holds a string —
    the declaration, or a filter keeping a kind that declares it one — and a key
    read as a fragment's text never does — measured, and said once, under *An accessor keeps its guard
    wherever it is written*.
- **Heterogeneous types per path.** The same path may be a string in one item and a number in
  the next. This is why a promoted declared-path column is typed `VARIANT` rather than a concrete
  type: `VARIANT`, like the `ANY` a document accessor carries, learns the value's type per row and
  absorbs the heterogeneity (#163). A concretely-typed promoted column could not, which is a second
  reason a *sampled* path — whose type would have to be guessed and fixed — is never promoted.

---

## Planned Project Layout

```
src/
  Apache.Calcite.Cosmos.Adapter/
    CosmosConvention.cs               ✔ Per-container calling convention
    CosmosImplementor.cs              ✔ Mutable SQL accumulator
    CosmosQuery.cs, CosmosClauses.cs, CosmosOrdering.cs ✔ A rendered statement, the clauses a subtree wrote,
                                        and what a sort may order an ordinal by
    CosmosRules.cs                    ✔ Rule set for a convention instance
    CosmosSchema.cs                   ✔ Calcite Schema over a database
    CosmosTable.cs                    ✔ Calcite Table over a container; Statistic
    CosmosSchemaFactory.cs            ✔ SchemaFactory for JSON model registration
    CosmosColumnStrategies.cs         ✔ Which columns an INSERT may omit, and which it may not name
    Client/
      CosmosQueryExecutor.cs          ✔ Executes a rendered statement via the Cosmos SDK; writes items
      CosmosCursors.cs                ✔ The cursors a compiled plan reads rows from, and writes through
      CosmosJson.cs                   ✔ JSON value → the representation Calcite holds a value in
      CosmosDocument.cs               ✔ The reverse: a row → the JSON document it describes
      CosmosWrite.cs                  ✔ What a write does, decided while the plan is built
      ICosmosItemWriter.cs            ✔ Creating and deleting documents
      CosmosSchemas.cs                ✔ Resolves the table's executor from the DataContext
      CosmosExecutionException.cs     ✔ The plan cannot reach what would execute it
      CosmosMaterializationException.cs ✔ A document does not hold what the query assumed
    Facts/                            ✔ Namespace Apache.Calcite.Cosmos.Facts: a fact theory over JSON
                                        documents, knowing nothing of Calcite, Cosmos or the adapter
      JsonClaim.cs, JsonFact.cs   ✔ Claims about one path, and entailment between them
      JsonFactRule.cs               ✔ A guarded claim
      JsonFactTheory.cs, JsonFactSet.cs ✔ A declaration compiled into the theory, and what a set of facts knows
      JsonFactDeclaration.cs        ✔ The declaration in one generation, as the oracle reads it
      JsonFactTables.cs, JsonScopeTables.cs ✔ What a set reads: a context's tables
      JsonFactEncoding.cs, JsonFactVocabulary.cs ✔ Claims, paths, scalars and characters in the theory's terms,
                                        and back; the tokens of forms and extensions
      JsonCharacterBand.cs          ✔ The bands of UTF-16 units a string's characters are known by
      JsonFactGenerations.cs        ✔ The graphs a schema object reasons in, one generation at a time
      Saturation/                   ✔ The rules of JSON and of JSON Schema, on Eggnog; internal
                                        JsonRules.cs, JsonContexts.cs, JsonFact.cs, JsonPathSpec.cs, JsonValues.cs
                                        (JPath.cs, Bound.cs): the theory, its contexts, its facts and its values;
                                        PredicateRules.cs (Val.cs, Pred.cs), PredicateAtom.cs (Truths.cs),
                                        Renderings.cs, PathValues.cs: the predicate rules and their sorts, what an
                                        atom means, the grammar a number renders in, and what a path may hold
      SqlValue.cs, SqlPredicate.cs, PredicateStatement.cs ✔ The typed IR's predicate subset: readings, atoms and
                                        connectives, and a statement of them
      CosmosOracle.cs               ✔ The predicate oracle: a declaration's statements, and the questions
      CosmosStatementScope.cs       ✔ One statement's strata, and the licence table that answers from them
      StatementTerms.cs             ✔ The contexts of one statement beneath a declaration
      OracleQuestion.cs, OracleLicence.cs, OracleVerdict.cs ✔ What a question is for, its licence, its answer
      JsonDocumentPath.cs, JsonType.cs ✔ What a claim is about, and the types it can name
      JsonScalar.cs                   ✔ A claim's value, compared as JSON compares it
      IJsonStoredForm.cs            ✔ A stored form, as a token the theory carries and never reads
      JsonSchemaFacts.cs            ✔ JSON Schema → rules, asking JsonSchemaRecognisers for forms
      JsonSchemaResolver.cs         ✔ $ref / $id resolution for the reader
    Metadata/
      CosmosCompositeIndex.cs         ✔ Composite index and sort-key matching
      CosmosContainerMetadata.cs      ✔ Declared container facts; sort legality
      CosmosContainerMetadataReader.cs ✔ ContainerProperties → CosmosContainerMetadata
      CosmosReasoning.cs              ✔ What one schema object learns as it plans: its graphs and bound facts
      CosmosPredicateLoader.cs        ✔ A filter → the IR, for the oracle
      CosmosFilterStatement.cs        ✔ A filter as the oracle reads it, and every question a rule asks of it
      CosmosRowFacts.cs               ✔ What the rows a clause reads satisfy: the filters beneath, last stratum (L1)
      CosmosPartitionKeyExtractor.cs  ✔ What a predicate pins, and whether a read answers it, asked of the oracle
      CosmosPatternReader.cs          ✔ The one reader of a pattern, under both readings: its characters and its strings
      CosmosPatternStrings.cs         ✔ What a pattern admits: anchored sequences of CosmosPatternRun, each a
                                        CosmosPatternClass counted
      CosmosStoredForms.cs            ✔ The form a pattern's strings are; CosmosTemporalForms, CosmosUuidForms and
                                        CosmosNumericForms define the families
    Rel/
      CosmosRel.cs                    ✔ Implement contract
      CosmosTableScan.cs              ✔
      CosmosFilter.cs                 ✔
      Convert/CosmosFilterReduceRule.cs ✔ A filter the oracle says keeps nothing, or everything
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
      CosmosAxioms.cs                 ✔ The measured facts a pushdown relies on, each with its test
    Internal/
      BigDecimalConverter.cs          ✔ Lossless BigDecimal → decimal
  Apache.Calcite.Cosmos.Adapter.Tests/
    <mirrors the tree above>       A class under test has one FooTests beside it, at the same
                                   relative namespace: Adapter.Sql.CosmosPath is tested by
                                   Adapter.Tests.Sql.CosmosPathTests
    EndToEnd/                      Whole-pipeline tests, which belong to no one class
      Corpus/                      The corpus, its schema, and the harness that plans it
    Measurements/                  What Calcite, IKVM or the service does — not this adapter
    Infrastructure/                The emulator fixture and the assembly's boot-time setup
```

✔ marks what exists today. The `Sql/` layer is deliberately free of any dependency on the
convention or on the CLR conventions in `calcite-dotnet`, which is what let it be completed and
tested ahead of them, and is why it remains testable without one.

**`Facts/` is a project in waiting, kept as a namespace for now.** The theory is about JSON documents
with known types, and nothing in it needs a query engine or a service: a schema is one source of rules,
the service's guarantees another, a query's own conjuncts a third, and the theory derives what holds.
So the namespace references only itself and the base library, `System.Text.Json` included — the
schema reader walks its tree and resolves references itself. Two things a JSON fact store does not know were kept on the adapter's side:

- **What a stored form is.** The theory carries `Represents(form)` as an opaque `IJsonStoredForm`
  and assumes only what every form shares — a string, or a null. Which pattern recognises a canonical
  UUID, how to write a value in it, and which comparisons it preserves under Calcite's ordering are the
  adapter's: `CosmosRepresentation` implements the token, the form tables stay in `Metadata/`, and
  `RepresentationOf` — which ranks forms by what they license — is an extension method in the adapter
  over the neutral `FormsOf`. The schema reader asks `JsonSchemaRecognisers` for a pattern's form;
  `CosmosSchemaRecognition.Cosmos` is the adapter's answer.
- **What the service will measure.** A geography is a claim JSON cannot state, so it is an
  `Extension` claim the adapter defines, `CosmosGeography`: a token the theory carries and reads
  nothing of. What the value is in JSON's terms — an object, and whether it is there — the schema
  reader states from the keywords that say so.

Where the adapter meets it: `CosmosDocumentPaths.From` turns a statement's path into a document path,
and everything that turns a predicate into terms or facts into a rewrite — `CosmosPredicateLoader`,
`CosmosFilterStatement`, `CosmosRowFacts`, `CosmosFactRewriter`, `CosmosPartitionKeyExtractor` — stays in
`Metadata/`, being about `RexNode`.
Constraints stay too: a `UNIQUE` is SQL compiled by Calcite and says what holds across documents,
which is not what the theory is about.

The mirror is the rule, and the three folders that break it say so by their names. A test asserts
something about one class or it does not; where it does, it is named for that class and sits where
that class sits, so the tests for a thing are found by knowing where the thing is rather than by
searching. Two consequences worth stating:

- **One test class per class under test, even where its tests want different fixtures.** A group
  that needs its own becomes a nested class inside the partial — `CosmosRexTranslatorTests`
  carries `Casts`, `Geography`, `FullText` and `Functions` that way, each with the builder and
  operand set it wants, filed under one name and filtered by it.
- **What does not belong to one class is not filed as though it did.** `EndToEnd/CosmosPlannerTests`
  drives the whole rule set over real SQL; `Measurements/` holds what is asked of something other
  than this adapter, so a failure there is news about Calcite or the service rather than a
  regression here. Naming either for a class would be a claim about coverage that is not true.

The framework is xUnit v3. Three of its defaults are set against, and each for a reason worth
knowing before changing it:

- **Parallelism is off** — `[assembly: Parallelization(Mode = ParallelMode.None)]`. The boot class
  path is process-wide, Calcite's registries are static, and the service-backed classes share one
  account and one emulator. MSTest ran serially and nothing was written to survive otherwise, so
  turning this on is a change to make and measure rather than one to inherit.
- **One-time setup is a class fixture**, not a static hook. A class that provisions a container
  declares `IClassFixture<Fixture>` and the nested `Fixture` drives the same static methods MSTest
  called by attribute, so what the tests read did not move.
- **A test that needs an account skips itself** with `Assert.Skip`, which v3 allows during
  execution. That is what `Assert.Inconclusive` did, and it is why v3 rather than v2: there is no
  dynamic skip in v2 to say it with.

**And CI executes the assembly rather than handing it to `dotnet test`**, which is a consequence of
how this repository is built rather than a preference. A v3 test assembly is its own runner, and
`dotnet test` drives it through VSTest, whose xunit adapter launches it **out of process through its
app host**. The app host is a native executable built for the platform that produced it — and the
`build` job runs on `ubuntu-24.04` while the `test` matrix downloads that one artifact onto Windows,
macOS and arm64 runners. So every leg but `linux-x64` failed before a test ran, with *"Could not find
app host executable `…Tests.exe`"*. `dotnet exec` needs only the managed assembly, which is
platform-neutral; `--roll-forward Major` is what lets the `net8.0` assembly run on the runtimes that
job installs, which are 9 and 10. The consequences for anyone changing the workflow:

- **The `?` filter after a suite name is xunit's now, not VSTest's.** `-class- "*FooTests*"` where it
  was `FullyQualifiedName!~FooTests`, and the query language is the other spelling.
- **The TRX comes from `-result-trx`** rather than from `--logger:trx`, and the results directory is
  made rather than implied.
- **`.runsettings` no longer applies**, there being no VSTest to read one. The repository has none.

---

## Leaving the Convention

A subtree of Cosmos nodes is a statement, not rows. `CosmosToClrCursorConverter` is where
it becomes rows: it renders the statement, executes it, and reads the JSON value each row arrives
as into the row the plan above expects.

**It leads into `ClrCursorConvention`, and into nothing else.** There is no converter from the
Cosmos convention into Calcite's own `EnumerableConvention`, and none is to be added. A plan that
wants rows there gets them higher up, through the converter `Apache.Calcite.Extensions` has between
the two; the adapter's concern ends at the cursor. The cursor convention is also the only CLR one:
calcite-dotnet removed the sequence convention, `ClrEnumerableConvention`, once the cursor one had
every node it had. The lookup join and the table modify, the two nodes that know a container without being
inside the convention that renders one, are in the cursor convention for the same reason.

**A cursor is the shape a Cosmos statement already has.** The SDK returns results a page at a time,
each fetched by awaiting `FeedIterator.ReadNextAsync` with a token, and a cursor advances with a
token of its own on every `ReadAsync`. So the executor's seam, `ICosmosQueryExecutor.OpenAsync`,
opens a cursor rather than returning a sequence: the statement is sent and the first page awaited at
the open, under the open's token, and every later page is fetched by the advance that runs out of
rows, under *that* advance's token. Under the sequence convention the token entered once, at
`GetAsyncEnumerator`, and a reader's per-call token had nowhere to go.

**The synchronous side still blocks, but per page rather than per row.** The v3 SDK has no
synchronous data-plane API, so `Implement` opens by waiting for the first page and a synchronous
`Read` waits wherever it has to fetch another; a row already in a page is reached without waiting.
Every such wait is in `CosmosCursors.Wait`, with the synchronization context suppressed before the
call starts rather than around the wait, for the reason `ClrCursors.Block` gives upstream. Under the
sequence convention the synchronous body was a bridge over the awaiting one and blocked a thread for
every row, because an `IEnumerable` over an `IAsyncEnumerable` waits on each `MoveNextAsync`.

**Opening is acquisition, and one write depends on it.** In the cursor convention an input's leaf
sends its statement when the input is opened, not when it is first read. A whole-partition `DELETE`
reads no rows — it counts the partition and removes it — so `CosmosTableModify` does not visit its
input at all for that operation. Visited and merely left unread, the input would have scanned a page
of the partition being emptied, which is the request the fast path exists to avoid.

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
wrong answer, not a slow one. So `id` and every partition key path must be pinned by conjuncts that
are equalities over them, and every other conjunct must be true of whatever those pins and the
declaration admit. `CosmosPartitionKeyExtractor` asks the oracle both, in a scope that assumes the
pins and nothing else: each key there and holding one value, and each other conjunct `Always` — a
`kind` the container declares required and constant asks nothing of the document read, and
`price > 100` asks what the read would not (*The oracle in the planner*).

**A predicate is the statement's, and a filter holds only part of one.** The extractor is asked
of one `CosmosFilter`'s condition, and a statement can carry two: a view's `WHERE`, queried from
outside by partition key and `id`, reaches the service as a filter beneath the pinning one, and
the planner does not merge them. Both conjoin into the `WHERE`, and the read was offered on the
outer one's say alone — a soft-delete view returned the document it excludes (review probe H6c,
measured on the emulator), and a filter above the pinning one returned both documents of a batch
where the query returned one (H5b). So a filter offers a read only where its condition is the
whole `WHERE` so far, and records that text (`PointReadPredicate`); `ReadsAnswerTheStatement`
withdraws the read wherever the `WHERE` the statement ends with is not the one it was offered for.
The comparison is the invariant rather than a list of who else writes a `WHERE`: the lookup join's
restriction is a writer too, and the comparison withdraws that read as well as the restriction's own
nulling of it does.

The rest of the statement rules it out just as firmly. A read returns one document, whole, so an
ordering, a row limit, a grouping, an aggregate, an array traversal and a `DISTINCT` each describe
something a document is not. `CosmosImplementor.Build` withholds the read for any of them, and the
batch of reads (`PointReadIds`, answered by `ReadMany`) for the same list, asked once in
`ReadsAnswerTheStatement`.

**`DISTINCT` was missing from that list, in both its copies, and the batch is where it showed.** A
batch returns one row per document and `DISTINCT` one per value, so
`SELECT DISTINCT c._ts … WHERE pk = 'bikes' AND id IN ('1', '2')` returned the same timestamp twice —
soundness probe P22c, measured on the emulator. It reached only a value present on every document:
`DISTINCT` binds its key to the path only there, and only a path lets the converter read the value
out of a document. Anywhere else the key was computed, and the
converter withdrew the read — by accident, which is the reason the list now names the clause rather
than leaning on that.

**The projection is the interesting one.** A read returns the document; the statement would have
returned `SELECT VALUE { … }`. Two row shapes again — and this time the answer is not to force one,
because forcing the projected shape is exactly the query the read is avoiding. Instead the converter
builds a second row builder that walks each output field's *path* in the returned document. It can
only do that where every output field addresses a path, so a computed projection withdraws the read
and the statement executes as the query it already is.

**Addressing a path is not enough: the query has to read the path's own value, as its declared
type.** The second row builder took every field that way, and the query does not. A guarded
accessor renders `IS_PRIMITIVE(c.p) ? c.p : null` and is read as text, a `JSON_QUERY` renders the
complementary guard and is read as a fragment, and the document column is read as the JSON the
service sent; each bound a path, so the read was taken and each failed as `VARCHAR` over the
object, number or boolean at the path — `SELECT *` by partition key and `id` among them (review
probes H4, H4h, H4f and H4l, measured on the emulator). Each now withdraws the read, decided by the
reading and by `RenderedExpressions`, which already say where a column is not its path.

A typed accessor joined them when its column took its type's guard (*Casts over document values*,
beside the table of readings): read out of the document, an object at the path failed as the integer
the plan declared, by `id` and by a batch of them, where the query's guard reads null as Calcite does —
the typed container's `label`, measured on the emulator. Its rendering is no longer the path, so the
read is withdrawn by the same test and the statement runs as the routed query. Applying the type's
test to the read's document would keep the read; `TODO.md` section 2 records it beside `DOC`.

The document column was the one worth trying to keep, and measurement settled it: the service does
not send a document the same way twice. On the emulator `ReadItem` returned it in the order it was
written, with `_self`, at one indentation; the query returned it reordered, without `_self`, at
another; `ReadMany` differed from the query in indentation alone. `DOC` is the service's bytes by
design — see *The document column* — so a read would make it a different string according to
whether the statement pinned an `id`. Keeping the read means deciding `DOC` is a JSON value rather
than those bytes, which TODO.md records.

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
not one, so `CosmosTableModify` is in `ClrCursorConvention` — a node whose input is rows and
whose effect is a sequence of SDK calls. That makes it the same shape as `CosmosLookupJoin`: a node
that knows about a container without being inside the convention that renders one.

Two consequences worth stating because neither is obvious.

**The cursor convention has no modify node**, so this is the first. Calcite's own
`EnumerableTableModify` is not a model to copy: it writes through
`ModifiableTable.getModifiableCollection()`, calling `Collection.add` and `Collection.remove` on
whatever the table hands back. For Cosmos that collection would have to block on `CreateItemAsync`
per element — and it would block wherever the plan happened to put it, with nothing in the signature
saying a write was waiting on a round trip. The awaiting open keeps that in one place instead, and
the writes are its acquisition: every row is written when the modify is opened, under the open's
token, and the cursor handed back holds only the count.

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
notation.

> **`STORED`, not `VIRTUAL`, and the difference is not cosmetic.** Both refuse a write. `VIRTUAL`
> additionally means *not stored*: measured, it makes `RelOptTableImpl.toRel` drop the column from the
> scan and project a literal null in its place, so `SELECT _ts` returns nothing the service holds.
> Forty tests failed on it at once. The row type is identical either way, which is why the regression
> guard asserts the *plan* rather than the type.

**Nothing invents an `id`.** A document reaching the service without one is the service's business to
accept or refuse, and guessing here would make the adapter the author of a key the caller did not
choose.

**The service's own properties are stripped from the document, wherever in it they appear.** `_ts`
cannot be *named*, but it arrives inside the document column whenever one document is copied to
another — which is what `INSERT INTO t ("DOC") SELECT "DOC" FROM t2` hands over, and the obvious use
of that statement. **Measured, and this is a decision rather than a requirement:** with the stripping removed
a document carrying a bogus `_ts`, `_etag` and `_rid` was still accepted, and still came back with
values the service had assigned. What stripping buys is that the document written is the document
described — a new item does not silently carry another item's identity.

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
nested partition key path is not promoted, so it is read out of the document instead; a container
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
   rejected outright (`TODO.md` section 7), so the target cannot arrive by declaration. It has to
   arrive by expression instead, which is (3).
3. **Static decomposition — future.** A mutation operator in the Cosmos table (`JSON_SET`-style,
   the way JSON-column databases spell copy-and-modify) would let a rule read patch operations
   straight off a `SET "DOC" = JSON_SET(…)` expression at plan time, which the document column
   admits, being `VARCHAR` and writable.
4. **Optimizations, recorded not built.** A runtime diff of old against new document into patch
   operations is only equivalent to a replace under `If-Match`, and is bounded by the ten-operation
   patch limit; a *blind* patch — no read at all — is possible exactly when the predicate pins
   `id` plus the full key and every `SET` value is a literal.

**What a replace refuses, by enumeration.** `SET "id"` renames identity and `SET` of a partition
key path changes placement; the service forbids both on an existing document, so both are declined
at planning — a plan that fails once, rather than a request that fails per row. Honouring a
placement change would be a delete and a create, which is a different statement. `_ts` and `_etag`
are declared `STORED`, so the validator refuses them before any rule runs. The document column may
still *carry* a different identity or placement inside its value — invisible at plan time, and the
service rejects the resulting request loudly, which is the correct fate for it.

**Building the replacement document.** The row's table columns hold what the scan read; the `SET`
values trail them. The old values identify the target — `id` and the partition key are read out of
the document they describe, as a delete's are. The body is the new document column and nothing else:
one column describes the document, so what is written is what the `SET` computed, minus the service's
own properties.

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

## What the lookup join reaches

A page joined to another container on that container's key was read the slow way twice over (#193): a
page of 25 links, each naming a park by a `UUID`, read every park and hash-joined them, and the join was
a left join besides. Each of those declined the lookup on its own.

**A left join is a batch's to answer.** The fetch reads every document the batch's keys could match, so
for each build row it knows whether anything matched; a left join emits the row with nulls where nothing
did, which is what it would have been joined to in process. A null key matches nothing either way, so it
is unmatched without being asked for. A right or full join is different in kind rather than degree: it
keeps the *container's* unmatched documents, and a fetch by the other side's keys is exactly the read
that never sees them. Those, and semi and anti joins, stay joins.

**A `UUID` key is restricted where the container stores one spelling per value.** The key column is a
cast, which binds to no path and has no parameter type, and that used to be the end of it. But the cast
is of a text accessor, and where what is known of the probe's rows gives that path a UUID form — the
declaration, or the probe's filter keeping a kind that declares it (`OracleLicence.Key`, L1) — the
stored text is one spelling of each value: `CosmosUuidForms.Render` writes a key in it, and
`path IN (@k0, …)` matches exactly the documents whose cast equals the key. The same fact lowers a
comparison against a literal and makes a self-join's key count; here the key is data rather than a
literal, so it is spelled per batch rather than once. Without the form, `ABC…` and `abc…` cast to one key
and no single spelling names both, so the join is the ordinary one. A key the form has no spelling for —
one a confined form's sign class excludes — names no document, and is answered as absent without a
request. Both sides compare as a `Guid` once fetched, whichever of `UuidValue`, `java.util.UUID` or
`Guid` carried them.

**A key read as text is restricted only where what is known of the probe's rows says the path holds a
string** — the declaration, or the probe's filter keeping a kind that declares it (L1). The
restriction names the stored value, `path IN (@k0, …)`, and the join then matches the fetched rows to
the build side's keys in process, by the key *as the column reads it*. So the fetch may return more
than matches and never less, and whether it can return less is a question about the column's reading,
which `TryBindOutput` derives beside its path. A column read as declared is the stored value. A
`JSON_VALUE` read as text is its rendering, and the filter translator refuses `JSON_VALUE(…) = ?` over
an undeclared path for exactly that reason: a stored `30` renders as `'30'` and Calcite matches it,
while the service compares a number to a string and does not. The rule used to check the key's SQL
type and discard its reading, and wrote the comparison the translator refuses. Measured on the
emulator (soundness probes P23b and P24b), a view's text column joined to `VALUES ('30'), ('31')` lost
the document storing `30`, and to `VALUES ('true'), ('huge')` the one storing `true`. The probes'
first spelling agreed, and only because `VALUES` types its literals `CHAR` of the longest, so `'30'`
beside `'bikes'` was the padded `'30   '`, which matched nothing on either side.

`CosmosLookupJoinRule.IsRestrictedAsStored` asks the translator's own question, `IsDeclaredString`, of
what the probe's filters establish (L1, `OracleLicence.Key`): the restriction is written beside the probe's
`WHERE` and has to be exact only on the rows that keeps. It takes the one exception: what is known of
those rows says the path holds a string — the declaration, or a filter keeping a kind that declares it
one — where the rendering is the value. The translator's other exception,
text no number or boolean renders as, is a fact about a literal, and a batch's keys are parameters.
The document column and a `JSON_QUERY` column are declined outright. A guarded restriction —
`path IN (@k…) OR IS_NUMBER(path) OR IS_BOOL(path)`, rechecked by the join's own comparison — would be
sound, and would fetch every non-string document on every batch at a price the cost model cannot see,
so it waits on a measurement.

**A number or a boolean is a key only once it is out of Java's box.** `CosmosLookup.Normalize` is what
a key is compared as, on both sides, and what a batch binds — and it reduced the CLR's numbers to a
`double` while passing Java's through. Java's are the ordinary case: a container row is read into the
boxes Calcite holds values in, so an `INTEGER` key is a `java.lang.Integer`, and a build side Calcite
computed carries the same. Bound as that box, the SDK wrote an object the service matched nothing
against: measured on the emulator, `c.price IN (@k0)` found the bike bound to a CLR `120` and nothing
bound to a `java.lang.Integer` 120, and a lookup on a numeric key, on a `_ts` read by another statement,
on a boolean and on a `_ts` through a traversal each returned no rows where the in-process join returned
them (review probes I4, I12, I13 and K9). In process the box equalled only a key of its own width, so
even rows fetched would not have paired across an `INTEGER` and a `BIGINT`. Every `java.lang.Number` is
now a `double` and a `java.lang.Boolean` a `bool`, the forms `CosmosQueries` binds a statement's own
parameters as. The rule always admitted these keys; it was the batch that could not carry them.

**A traversal on the probe side is restricted per document, and the binding walk has to know both of
its forms to say so.** The restriction is a `WHERE` the service evaluates after the `JOIN … IN`, and it
names a property of the document, so it keeps or drops a document with every one of its elements —
which is what the join on that property does. `IsRestrictable` admitted the traversal from the start.
But the key is asked about twice: by the rule, of the logical subtree, where the traversal is a
`Correlate`; and by `CosmosLookupJoin.Prepare`, of the converted one, where it is a `CosmosUnnest` — a
`SingleRel`, not a `Correlate`, and a node `TryBindOutput` had no case for. So the rule fired and
`Prepare` threw "The lookup key can no longer be restricted." once the planner had chosen the plan
(soundness review, cause K: found in the code, then reproduced on the emulator by a lookup from
`VALUES` onto a view over `UNNEST(JSON_QUERY(… RETURNING VARCHAR ARRAY))`). Both forms now bind through
`TryBindTraversal`, the element unbound in each; a key on the element is declined, as it was. That
fixed the key on `id`, which needs nothing but the binding. A key that needs the container — text over
a declared string, or a `UUID` over a declared form — went through the rule's own walks, which knew only
the `Correlate`, and failed the same way; see *Plan order is not clause order* for why every walk now
asks `TraversedInput`.

**"When the near side is small" is the cost model's to say, and stays so.** The issue asked for the lookup
where the near side is bounded; the join is chosen where it is cheaper, which for a page of 25 against a
container is always, and for an unbounded near side against a small container is not. A hard gate on a
fetch would decline the unbounded cases the cost model already prices correctly, and it would not see a
bound that arrives as a key equality.

**But the model had to know what a key returns (#208).** Calcite estimates an inner join as the two
sides' product scaled by a selectivity. Against a distinct key list, the one `IN (?, …)` expands to, a
hundred keys over 445 matching documents came to 667 rows: more than the container could return to
them. Every node above the join is priced on its rows, so the projection above a lookup cost more than
the whole in-process semi-join it competed with, and a list of a hundred keys read every matching
document. `CosmosRelMetadataQuery` now bounds a lookup's row count by what its keys allow. Distinct
build keys return at most the documents they name, which is no more than the probe side and no more
than the keys times the documents per key. That is one per key where no distinct count is known, which
is the assumption a lookup is for. A unique probe key bounds the count by the build side instead. The
lookup's own charge is the documents fetched under that bound, plus one per request.

What it cannot fix is a container with no row count. The planner then takes it for Calcite's default
of a hundred documents, under four of which match a typical view's filter, and reading four documents
once really is cheaper than deduplicating fifty keys and sending them. That was the issue's "no row
count" column on Azure, where #209 was the cause: the statistics were never asked for.

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
  shape cannot cross. The values are the ones the execution bound, not the slots a prepared
  statement carries: named by its slots, every execution of one plan would share answers whatever
  values it was run with. Rows are rebuilt from JSON per execution, which is the price of sharing.
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

## The rendering graph in check mode

The rendering graph, S, is a second e-graph that proves a written Cosmos form keeps the rows Calcite keeps for the
predicate it was written from. Its first stage checks the SQL today's translator writes, in CI and with no service, and
changes nothing the planner decides; every commit that says so is held to it by the plan digests.

### Plan digests

**"Changes no plan" is compared with the parent, not argued.** `Tests/Planning/PlanDigests.cs` plans every statement
the check reads, and executes none: the planner corpus, the differential's 465 statements without parameters, the
statements of the partition run's first 200 predicates (1,000 where `COSMOS_PARTITION_GATE=1`), and the definedness
probe, whose operator the differential's validator does not know. `PlanDigestTests.WriteDigests` writes a line a
statement, at the parent and at the commit, and the two files must be byte-equal. Each input is planned as the test
that runs it plans it, off that test's own lists, so a statement added there is digested too.

**A digest holds what a plan can change without changing its text.** Beside the plan's text: each statement it builds,
with its parameters' values and where it is sent, since a partition key read under other facts routes a statement
whose text is the same; and each pushed conjunct's IR digest and the round that admitted it. A conjunct admitted a round
later renders from other facts, and often into the same SQL.

**The round is read off the statement the implementor rendered from**, looked up by the condition and the binding the
implementor holds once it has implemented the filter's input. A binding walked off the plan (`TryBindOutput`) leaves
an array's element unbound where the implementor binds it to its alias, and read the filter over an `UNNEST` as an
opaque conjunct no round admits: another statement than the one that was sent.

**Two files compare only if planning is deterministic**, and the oracle remembers statements across a process.
`TheDigestsAreDeterministic` plans the corpus twice in one process, per pull request, and requires the same lines.

### Calcite's side, measured in process

**A reading in the IR is several readers to Calcite, and each is measured where the reader fills it.** `typed` is a
`RETURNING` and a column the reader types; `raw` is a promoted path's `VARIANT`, an `ANY` and a `VARCHAR` column. No
expression over a JSON literal yields any of those columns, so `Tests/Measurements/MaterialisedTable.cs` scans rows
through `CosmosJson.GetValue` itself. Six `Calcite.*` rows hold what it measured, `IrEval` answers from them through a
named reader (`TypedAs`, `RawAs`), and `IrEvalCalciteTests` holds each to Calcite, on random predicates and, for the
cast of a raw value to text and a raw number in its box, over a fixed set. What a cell answers is the union over the
readers its reading stands for. Unnamed, `IrEval` reads raw through the two columns whose `=` and null test never raise,
which is what the facts graph's raw reading answers for; the raises it leaves out are in `TODO.md`, and the soundness
line names each column wherever they part (`IrEval.RawModes`).

**The column reader is not `RETURNING`** (`Calcite.ColumnReadsWholeNumbersAsItsType`). The column reads a whole number
as its type whatever its spelling, `42.0` under `INTEGER` and `42` under `BIGINT`, where `RETURNING` raises over both,
and fails the statement over a fraction, a number beyond the type, a string or a boolean.

**The raw columns agree on strings and null, and part elsewhere** (`Calcite.RawStringEqualityIsOrdinal`,
`Calcite.RawNullIsSqlNull`). A string is equal to the string of its UTF-16 units alone, and a null or an absent path is
SQL null, so `NOT (raw = 'bikes')` keeps neither. A value of another type is unequal in `VARIANT` and `ANY`, and fails a
`VARCHAR` column's read. `ANY` raises on an ordering across types, and within one orders as the type: a string by its
UTF-16 units, as a `VARCHAR` column does, and a number in the box the reader built, a long by its value and any other
number by its double's exact value, so `9007199254740993.0` is below 2^53 + 1 and a stored `0.1` above `0.1`. A
`VARIANT` has no ordering at all, the statement failing to compile, and is equal to a number boxed with its own SQL type
alone, `BIGINT` for a JSON integer and `DOUBLE` otherwise, which the IR does not keep: its numeric `=` stays a coin in
`IrEval`.

**A raw value cast to text is each column's own too** (`Calcite.CastOfARawValueToText`). A string is itself and a null
null in each. A `VARIANT` casts a number and a boolean to null and raises over an object or an array; an `ANY` renders
each value as Java renders the reader's box, a long in its digits, any other number as Java spells its double, an object
as `{a=1}`; and a `VARCHAR` column reads a string alone. `IrEval` reads the cast through a named column; unnamed, it
keeps the facts graph's rendering, which population D holds F to (`TODO.md`).

**Text orders by UTF-16 code unit** (`Calcite.TextOrderIsByCodeUnit`) and the service by code point, so the two part
where one string holds U+E000 or above and another a supplementary character.

**The connectives are Kleene's, and evaluate both operands** (`Calcite.ConnectivesAreKleene`). A nullable `AND` beside
FALSE raises where its other operand raises, in either order. `IrEval` answers the deciding arm and `Raises` marks the
row, which is what the check against Calcite leaves out.

### Cells, and Calcite on one

**A cell is a set of the values one path may hold, cut so that each side of a pair answers one way on it.**
`PathCell.Split` cuts one imported `PathValues`, with the classes of its strings' characters, at the values the pair
compares with: absent, null, each structure and each boolean apart; a literal the import admits as a point, and one it
excludes as a boundary; strings between two literals in Calcite's order, by UTF-16 unit; numbers of a kind between two
by value, and by the double a literal rounds to (*Numbers on cells*, below). The cut points are handed in, for S's rules
to choose. Every value the import admits lies in exactly one cell,
and a value it refuses in none.

**Every cell is inhabited, and the search for an inhabitant is complete.** A refutation names a cell, and a cell nothing
lies in refutes nothing, so `Split` keeps an interval only where `Witness` finds a value in it. An interval's strings
are tried from the least above its low end, then each next in order — the last with the least unit its classes allow
appended, nothing lying between — until one is not excepted or one reaches the high end; its numbers from its bounds.
So the strings below `""`, those between `l` and `l` + U+0000, and bounds that hold no number of the kind are dropped,
and nothing the import admits is. Classes are read through `JsonValues.BandsOfText` a unit at a time, so classes the
string fixes add come through unchanged.

**`PredicateAtom.OnCell` stands beside `On`, and the facts graph reads only `On`.** A reading's cell methods default to
its answer on the state the cell lies in. Text places a string, a boolean, and an interval wholly to one side of a
literal exactly, and a number by its rendering's first unit, `-` or a digit; raw string equality is exact on every cell,
a raw ordering and a raw equality with a number read their columns (*The raw reading's raises*, below);
a typed equality on a point reads its value as the column and as `RETURNING` read it; and `IN` is the `OR` of its
points' equalities, each as its reading answers it on the cell. Making `On` exact on a scalar instead would change the
facts graph's verdicts wherever a domain is known.

**A reading on a cell is the union of the readers it stands for, raises included.** The coarse raw and typed readings
leave out raises their readers make: a `VARCHAR` column's read of anything but a string, a raw ordering, and a typed
column's read of a structure, which `RETURNING` answers null for. A cell answers them, and only there stands outside the
coarse answer (`TODO.md`). Under decision 2 a raise beside a truth value constrains nothing, so the union only keeps a
cell from refuting where Calcite may fail.

**What holds it.** `PredicateAtomCellTests` holds every cell answer to `IrEval` through each reader; `PathCellTests`
catches a split that drops the absent cell or keeps one below `""`.

### Forms, rows, and the service on a cell

**A written condition is a value, never a class.** `ServiceForm` is a test, a comparison or a membership of one path, a
path written bare, a constant, or a connective. Its canonical text — fully parenthesised, a path spelled as
`SqlValue.Spell` spells it, a literal quoted in `JsonValues`' spelling — is its equality, hash and order, and a junction
keeps its members as written. `First`, `Rest`, `Without`, `Decomposes` and `Strip` only take a form apart, so that rules
can pair a junction's members with the IR's children every way. A junction without its one member, however often
written, is that member: `AND` and `OR` are idempotent.

**The service's behaviour is rows, keyed by what was asked, of what operand, over what class of value.** An entry holds
every answer the targets a set serves gave for its key, and the outcomes they mean through `AnswerMeanings`, host data:
that `NotKept` is false or undefined is a row, not C#. `ServiceTable` views a set's entries and is equal by content.
Eggnog interns a value by its equality, and the probe's table, equal by its size, would have let a bundle read another
set's rows.

**Four primitives evaluate on concrete cells and decide nothing else** (decision D1). `CutPoints` cuts at the literals
and a reader's limits, `ServiceLeaf` reads one test's row on each cell, `ServiceCompound` maps one connective over two
vectors by its rows, and `CellOutcomes` pairs Calcite's `OnCell` with the service's outcome on each cell. Which cuts,
which literals part the orders, which subforms are evaluated, how verdicts compose and every verdict are S's rules.

**A key no row settles answers everything, a failure included, and is named** `missing:test/operand/cell`. So no branch
encodes a behaviour without a row — an empty table answers widest everywhere — and an unproven conjunct names the
measurement that would decide it. A side that may fail lets its connective fail the statement where it is evaluated
(decision 2), and never makes it: no row says the service evaluates a side its sibling decides the connective without,
so beside its failure the side stands for every truth. A failure that refutes an atom composes, since a sibling that is
the identity, or a `NOT`, decides nothing without it.

**Strings are cut in Calcite's order and placed in the service's.** An interval of strings is cut by UTF-16 unit, so by
code point it lies wholly to one side of a literal unless the literal parts the two orders over the path; S's rules
decide that for every comparison with a string, `=` and `!=` as well as an ordering, since each reads the row of the
side an interval lies on, and a parted interval answers both sides. A point is placed by each comparator the type's
`Order` entry names, their answers unioned, beside whatever else the entry answers. A number is placed by the value the
service compares (*Numbers on cells*, below). A boolean and a null are keyed as their own classes, which no comparator
orders.

**A path written bare carries the class of a value that is no boolean**, and a connective over it reads that class's
row, not unknown's. As a whole condition, where its row keeps it not, such a value is kept by neither `WHERE` nor
`WHERE NOT` (`Service.BarePathKeepsOnlyTrue`, `Service.NotOverAPathKeepsOnlyFalse`): `means()` says it reads as
undefined, and the `classes` rule hands that to `CellOutcomes`. What its row keeps stands, so a row that kept a number
would refute; at `Pos` false and undefined are alike, and at `Neg` the reading is the licensed `NOT`'s.

**What holds it.** The primitives' tests catch a table equal by its size and a `NOT` that drops a failure.

### One vocabulary for both graphs

**F and S read one conjunct as the same term, because they define it from the same code.** `PathVocabulary` holds the
`Path` sort and its four constructors; `PredicateVocabulary` the `Val` and `Pred` sorts, the IR's fourteen constructors,
`reads`, `atom` and `point`, and the `analyse` ruleset's nine rules. Each graph defines both, and `PredicateWriter`
writes the loader's IR into them, a literal's `point` always beside its atom. `JsonRules` and `PredicateRules` forward
to them, so the facts graph's callers read them where they did; what `PredicateRules` keeps — `possible`, `holds`,
`chosen`, `arms`, `arm` and the two later rulesets — is F's alone.

**A junction's arms are the caller's.** The writer hands each junction, with its arms flattened, to a callback: F's
numbers them for its split over arms, as it did; S pairs written members with arms by its own rules. A hidden
discriminator is the theory's and no path, so `JsonPathSpec.ToTerm(PathVocabulary)`, the overload a graph without the
theory writes paths with, refuses one.

**What holds it.** The plan digests, byte-equal at the parent and at the commit.

### The theory, and the gate on an atom

**S is a frozen theory and a bundle per statement round.** `RenderingTheory` loads the measured rows one batch per
axiom, so a second row for one key and target that answers otherwise is refused whole and named, and then the host rows:
the sets each target serves, what an answer means, the agreement's boxes, the 225 classes they range over, and each
reader's edges. A reader with no edges row is cut nowhere, so an atom it reads is never evaluated: text, raw and, since
g09, the text cast of a raw value have one; a numeric cast of a raw value has none, each column casting it its own way
(`TODO.md`). Its rules settle the rows and derive the classes each box
allows and refutes; then it freezes, and every bundle is a child of it. A `RenderingBundle` holds one round's conjuncts
as the shared vocabulary's terms, each path's import written once before the run, and the forms claimed for the
conjuncts; it is run once, read, and disposed.

**A key is settled in a set once every target the set serves has measured it.** `measure` gathers each key's answers and
the targets that gave them per set; `tabulate`, a stratum later, when the gathering is whole, enters a key only where
its targets cover the set's. So under `Portable` a key the emulator alone measured is no entry, and answers widest; a
key the two targets answered differently is one entry holding both answers, whose outcomes are their union.

**What a position keeps is rows, and the classes they allow are derived.** A cell's class pairs the truths Calcite may
give on it with the outcomes the service may give; any of the one may meet any of the other on some document. Equal
holds where every pair keeps alike, Implies where the service keeps whatever Calcite keeps, and a class refutes where
every pair breaks the verdict and Calcite never raises. Decision 2 is two rows: `tolerated` sets a raise aside in an
allowing box, and no allowing box admits a failure. `AgreementRows` writes these as boxes, and a brute-force test holds
the sets the rules derive to the pairwise definition, class by class.

**The universal over cells is a rule, and reads only complete inputs.** A candidate agrees where its cells' classes lie
within the allowed ones, and is refuted where they meet the refuting ones. The bundle runs in strata — terms into atoms;
cuts, partitions, the parts each form needs and the literals that part the string orders; the service on each part,
leaves before connectives; the gate — so the classes a universal reads are whole when it reads them, and so is the set
of parted literals a leaf reads as not holding a literal. The order test reads every rule's rendering and holds each
universal, each lattice-free head and each superset to it; a plant after critic-validation F1, a set grown and tested in
one stratum, is refused.

**A proof and a refutation are packing.** The gate decides; the value says why. A proof carries the rows both sides read
and the boxes' citations. A refutation names the least refuting cell by its text, so of the null, the object and the
array that refute a bare `!=` it names the array. Both are ordered values under `Min`, so the value does not depend on
which is found first.

**Vacuity is a table.** An agreement is vacuous where every cell is one Calcite raises on alone, or there are none: the
gate writes `set-subset(classes, raiseonly)` beside it, and a junction or a negation copies each side's. `raiseonly` is
derived too: the classes whose truths of Calcite's the policy sets aside whole, so a plant of `tolerated` moves it.
`vacuous` is a `Max` over booleans, so a claim holds of nothing where any of its proofs does, whichever `Min` keeps.

**What the evaluation fills is held to one value a key.** `partition`, which cuts at the reader's edges, `svc` and
`outcomes` have no lattice: each is computed once for each match of its rule, and a key two matches reach — a part two
candidates sharing a partition both need — must come out one value, so a primitive that answered one key two ways faults
the bundle rather than meeting silently. A form that reads any path but its atom's is never partitioned, so never
evaluated.

**An answer is read off a completed run, and a contradiction is loud.** The limits are counts, 64 iterations and 50,000
rows, never time, and a run they stop answers `Incomplete`. An agreement and a refutation of one claim derive the
contradiction, and `Read` answers `Conflict`, never either verdict. After every run the tripwire requires each term to
be its class's one node, which a merge would break, and a run it trips answers `Conflict` too.

**What holds it.** Plants a regression would meet: a merge (T7), a leaf that answers twice, a box that both allows and
refutes, a form over another path, a contradicting row (T6), and a set grown and read in one stratum.

### Composition through AND, OR and NOT

**A connective composes in a set where its measured rows are Kleene's on every key Kleene's logic expects there.**
`KleeneTables` writes what each target must answer: the service's targets over the three truths and the six classes of
a value that is no boolean, which the service reads as unknown inside a connective; Calcite over the truths alone, since
its connectives take booleans. `expected` gathers a set's keys from the targets it serves, `matched` those it settled as
Kleene's, and `licence`, a stratum later, licenses a connective whose expected keys all matched. A key missing, answered
apart by two targets, or not Kleene's leaves the connective unlicensed, and every step through it unproven. Each step
reads the set's licence and Calcite's: `T(a∧b) = T(a) ∩ T(b)`, `F(a∧b) = F(a) ∪ F(b)`, their duals and `T(¬a) = F(a)`
must hold on both sides. One expectation for every set, as first planned, would have asked Calcite for the six classes
too, and licensed nothing under it.

**On one atom's cells, a junction of three members or more is regrouped only under its licence.** It is evaluated as
its first member and the rest, a junction rid of every copy of that member, which takes associativity and idempotence
for granted; a missing row of the grouping as written was never read. So it is evaluated only where the set licenses
its connective. Two members read their row as written, and a junction nested as written is evaluated as nested.

**Positions and pairings are derived, and a junction's form is composed up from its arms'.** A junction of the IR has
a `junction` row from each arm; a negation's operand is a candidate for the form stripped of a `NOT` at the opposite
position. A junction lies `within` each form of its own connective it is a candidate for, and an arm that is a junction
of that connective lies within the same forms. Each arm of a junction lying within a form is offered each of its parts,
and an atom also the form kept to the parts that read its path alone (`ServiceForm.Reading`); so every pairing is tried,
and the gate, not C#, says which holds. A junction agrees where both sides agree on one verdict, with the form their
forms make of the one it lies within (`ServiceForm.Spanned`: every part neither side is or holds taken out, the grouping
kept), and only where `form-decomposes` holds that form to exactly their join, in either order: the helper builds a
value and the filter decides. A check reads the root's agreement with the claim, so a member no arm agreed with leaves
the root agreeing with less than was claimed, and the claim unproven. A node may hold candidates from two parents, so
the connective is read where the step is taken: an `OR` of the same members, claimed beside an `AND`, offers its sides
the same candidates and must not compose. A written member the IR merged is dropped once where its text matches, `AND`
and `OR` being idempotent; one written twice differently, or beside a test of its own path, is proven where its atom
evaluates the parts reading its path together on its cells, which is a proof of what was written.

**Why up, not down.** A junction's form handed down — its first arm offered each member and its second the rest, whose
members a rule restated — gave a right-nested junction of n arms a candidate for every subset of its members,
2^n − n − 1 of them, and a run that stopped on its row limit at 13 arms. Composed up, a junction is offered at most n²
parts, which nothing at the junction evaluates, and its atoms at most n(n + 1) where every part shares one path; a proof
composes one form a junction where each atom agrees with one part. The premises are the old ones word for word — one
verdict on both sides, or a definite refutation beside the identity on a disjoint location; exactly the join; the
connective licensed by the set and by Calcite — and the need checked is the written form's, which a rest's never was.
Only where the form comes from changed, so a proof is still one the universal over each atom's cells backs, whatever
proposed the candidates. What it gives up: a leftover member that reads no path, a constant, or more paths than the atom
beside it, which the old descent proved only where the IR's order of arms made that atom the last.

**A refutation composes across locations a document sets apart.** A side refuted on a cell, beside a sibling that is
the connective's identity on one of its own — `(T,t)` for `AND`, `(F,f)` for `OR` — refutes the junction on a document
setting both; `disjoint` holds of two properties of one parent, never of an element against an index or a path against
one beneath it. Only a refutation made on one atom's cells composes so: the gate writes the location it sets in
`refutedat`, a negation passes it on unchanged, and a junction's, which sets two, has none, since its sibling's could be
the other at another cell. The sibling's location is its atom's. Without that, `(kind = 'a' AND (label != 'bikes' OR
kind = 'a'))` was refuted by a witness setting `kind` to `false` and to `'a'`, where both sides keep the documents whose
`kind` is `'a'` and no others.

**Two bounds that part the orders need `Range`.** Where two string orderings of one path stand in one written
conjunction, nested conjunctions flattened, and a literal of one holds a character class that parts the orders against
one the import may hold, the service may answer the pair as a range, which Azure answers empty (N8). The conjunction is
then evaluated on an atom's cells, or composed, only where the set licenses `Range`. The orderings read are the form's,
not the IR's: the service is asked what was written, and a range written for one atom, or across arms whose own
literals part nothing, is a range all the same. What licenses it is N8's rows (*The family's last rows*).

**What holds it.** Plants a regression would meet: an `AND` row dropped under a pairing and under a regrouping, the
non-boolean rows dropped, an arm offered the rest without a member that differs in text, as the aligner dropped it,
which proves no more than the arms make, a superset that narrows in its stratum, and a lambda filter.

### The registry, read into S

**A row carries what its measurement answered, and one reader makes it S's rows.** A `CosmosOutcome` on a
`CosmosAxiom` names a test, an operand's class, the targets that ran its own witnesses, and the truth a `WHERE` reads on
each class of cell, `absent=false null=true …`, parsed as the registry is initialised. `CosmosAxiomRows.Read` writes a
row per cell and per target, citing the axiom, and merges nothing: which targets settle a key, and what two that answer
apart mean together, are `measure` and `tabulate`. A composite, `NOT IS_DEFINED(c.v)` say, carries its formula and is a
check of composition through the atomic rows, never a row.

**An outcome says no more than was run.** Its targets lie within its row's, two outcomes of a row name no target for one
cell, and the row's pinning test names the row back (`[CosmosPins]`). A cell no outcome covers for a target is declared
in `CosmosAxiomRows.Owed`, so it answers widest on purpose, and a key the emulator and Azure answer apart is declared
too, since a divergence is data, not a conflict, and would pass unseen.

**Azure is claimed only where a run read the cell as its pinning test does.** `Tests/Measurements/Runs/` holds what the
probe account answered, and an outcome naming Azure must find each cell's answer there, read by the same statements —
a `WHERE`, a `WHERE NOT` beside it where one ran, the literal sent as a parameter — over every witness the test holds
the cell to. A projection is no evidence of a filter on Azure, whose parted range projects true where its filter keeps
nothing; nor is a literal written inline, nor an `ORDER BY` with no filter beside it. Such lines are recorded for what
they are and back nothing. The check runs both ways, so a line read as the test reads it, at a cell its row declares,
is claimed, by its answer or a later run's that means no more: evidence is neither stretched nor dropped by hand. A line
at a cell no outcome of its row declares was measured beside its key's and backs nothing, nor does a cell one run
answered two ways.

**The probe account has run every row's `Holds` (D7, 2026-10-21).** Its record has the 650 lines the emulator's runs
write, read as the tests read them, and all but two answer as the emulator's do. So every cell is claimed on both
targets and nothing is owed; the two that differ are an outcome per target, and declared: the parted range's between
cell, which Azure keeps neither by `WHERE` nor by `WHERE NOT`, and the string order over U+0000. Its `WHERE NOT` beside
each `NotKept` of 2026-10-16 supersedes it, so no outcome answers `NotKept` now.

**The rendering tests read the registry and nothing else.** `ProbeRows` is `CosmosAxiomRows.Read` of the registry, its
table the one S settles for the emulator's set; a test that needs a row the registry lacks plants it under an id no row
holds. Calcite's connectives are a row like the service's, `Calcite.ConnectivesAreKleene`, its measurement reading its
expectations off the row, so the registry alone licenses `AND`, `OR` and `NOT` under every set.

### The rows' vectors, held by their tests

**Each pinning test is `Holds` of its row.** `CosmosOutcomeHarness` writes the catalogue's witnesses under a partition
of their own and reads each cell off a `WHERE`, a `WHERE NOT` and the projection. It never asks which endpoint it
reached: an answer must mean nothing a target the outcome names did not declare, so `NotKept` is matched by false or
undefined, and a declared divergence is held as far as a blind check can hold one, each target answering one of the
two. The projection is held to the filters except at a cell declared off a `WHERE` alone or answered apart, Azure's
parted range projecting true where its filter keeps nothing. Every cell is written to `runs.tsv` before any is
asserted, so one run of the probe account records them all.

**The catalogue has one literal per class of operand,** so a cell's witnesses are a function of the operand and the
cell: U+E000, where the two string orders part; 2^53 + 1, where a double and an int64 part; `true`.

**The emulator orders a string holding U+0000 as it stores it,** U+FDD0 then `0`: measured on both images, it puts
U+0000 above U+E000 and keeps it under `>` U+E000. `StoredCodePoint` names that comparator, and `ServiceLeaf` places a
point by what is stored, a range that may hold U+0000 on either side — equal only where it holds the literal, or the
literal holds the high band U+FDD0 lies in — and a literal holding one nowhere. So `=` and `!=` with ASCII stay exact
whatever a path holds, and an ordering is proven only where the import holds no U+0000. The order's witnesses hold
U+0000, and an order is the comparator that sorts them as `ORDER BY` does and places them as `>` beside each does,
which also parts code point from a comparator that stops reading at U+0000, as Azure's `CONTAINS` does. Azure's order
is `CodePoint` over them: its `ORDER BY` and its `>` both put U+0000 least and read past it. So the string order is a
key the targets answer apart, and a set serving both places a string by both comparators.

**A composite is checked by composing its atoms** through `ServiceLeaf` and `ServiceCompound` on the table S settles for
each target it names, and is the measured answer on each, every atom it reads being measured definite on both. A
constant is read by its row (`Service.AConstantIsItsOwnTruth`), as S reads it. The guarded accessor answers values, not
truths, so it stays its test's assertion.

### The family's last rows, and the range's licence

**What S still lacked is measured on the emulator and the probe account (D7):** `Service.ComparisonWithinAType` (N1),
`Service.ConnectivesAreKleene` (N3), `Service.TypeTestBodiesMatchTheService` (N13),
`Service.NumbersBeyondLongCompareAsDoubles` (N14) and the constants. Azure answered every cell of them as the emulator
does, an int64 against a double by value among them.

**A connective's undefined is an ordering across types, not an absent path.** S keys a side written bare by the class of
what it holds and any other side by its truth, so undefined and absent are two operands, each witnessed in the shape
that reaches S. The first side is held at `c.u` beside the second at `c.v`, every witness of each beside every one of
the other.

**A range is licensed where its rows are pointwise.** `KleeneTables` expects a range of one path to keep a value between
its bounds, `NOT` one outside, and neither one of another type or none, whether the bounds part the orders or not; and
`licence` reads `Range` as it reads a connective. The emulator answers so for both kinds of bounds, in the `>= AND <=`
spelling S reads and in `BETWEEN`, which keep alike; Azure keeps nothing between parted bounds, by `WHERE` or by
`WHERE NOT`, and answers the rest as the emulator does. So `Range` is licensed under the emulator's set and under no set
that serves Azure, and the parted range's between cell is a declared divergence.

**Numbers compare by the value compared:** an int64 as itself, anything else as its correctly rounded double, the two by
value, never both rounded. That is what `Numeric` means; S places a number only within ±2^53, where the two agree,
until c13's double slots read N14.

### The declaration's `possible` rows: `D0`

**Round 1 renders from the root, and the root holds none of a statement's terms,** so the facts graph's `possible`,
written only where an atom reads a path, held nothing for most round-1 conjuncts to import. Gathering `PathValues` in C#
would restate `Decide`'s seven rules. `D0` is a frozen child of the declaration's context instead: a `declared(p)` row
for every path the declaration mentions, and one local rule in `Decide` writing `possible(p) = Anything`, which the
other six narrow from the declaration's tables as they narrow an atom's path. It is built once per declaration and
generation, however many planning threads ask (`Lazy`, `ExecutionAndPublication`), and is `null` where nothing is
mentioned or its run does not complete.

**Every statement is rooted there.** `StatementTerms` roots a statement's tree at `D0`, or at the declaration's context
where it has none, so every stratum lies beneath `D0`, where the local rule is idle. A generation's trees share one
root, and the oracle remembers a tree by its digest alone. Until g08 a statement was
rooted there only in a flow a test armed, keyed apart, and production armed nothing (*`D0`, every statement's root*,
below).

**It changes no verdict, fact set or plan.** The root's view is still the declaration's own fact set (`ViewOf` compares
the root's scope by reference), so the translator's round-1 facts are unchanged. A stratum's row for an atom's path
starts from `D0`'s instead of `Anything`, and `D0`'s, read from tables the stratum inherits, is no narrower than the
stratum's, so the meet is the stratum's own. The plan digests with statements rooted at the context and at `D0` are
byte-equal.

**`D0` keeps a declared domain as stated.** For a path an atom reads, a stratum keeps only the domain's values of a type
the path may hold (the last rule of what a predicate establishes), and `D0` holds no atom. So where a declaration states
a domain beside narrower types, `D0`'s row lists values of a kind it excludes. It allows the same documents, since
`PathValues.Admits` and `PathCell.Split` read a value only of a kind the row allows; it is the spelling that differs.

**`D0` makes no term.** It writes a row only for a path the declaration's context holds a term of: it runs no rule of
JSON, and a term made there would put into every stratum beneath it a path no stratum of the context holds. A body atom
of a rule the theory cannot read is mentioned and never made, and has no row. A path the round-1 view mentions lazily,
an element or a child of a declared path, has none either; the importer counts each as a default.

### The importer, and S's generations

**The importer copies one value per path per table.** For each path a bundle's atoms read, `RenderingImporter` reads the
licensed node's `possible` and `chars` rows with `TryGet`, which on a frozen scope interns nothing, each table on its
own, and writes them as S's lattice-free `imported` and `importedchars`. It reads none of F's verdicts, nor `types`,
`dom`, `ne`, `lo` and `hi`, which F has already met into `possible`. A refutation is not monotone under narrowing, so an
import must be the whole value before the run: a bundle takes one `ImportSource`, refuses an import after its run or
from a second node, and throws where a path is imported again with another value, so S never meets two. The source holds
the node itself, compared by reference: a statement's digest, a licence and a round name no generation and no root.

**A path F holds no row for is the top, and counted.** Where the declaration mentions nothing at, within or above the
path, the top is exact, and a refutation over it is one. Elsewhere — a child of a declared object, a parent of a
declared path, a path `D0` made no term for — the top loses precision, so the default is counted as constrained and the
check run warns. A node that keeps no document imports nothing, and the report says so.

**The digests are content.** An import's digest is the import written as JSON, every value quoted, so a memo keyed on it
holds every value a rule reads and two imports that differ never share an entry; `D0`'s wider spelling of a domain
(above) keys the same documents apart, which costs a bundle and never a verdict. The theory's digest hashes the frozen
theory's rows, so generations defined alike share entries, a planted row shares none, and no table can be left off a
list; a primitive is no row, so a plant of one keeps a memo of its own.

**Generations, as F's.** A graph's ids and epochs are finite and its pools never shrink. `RenderingGraph` defines a new
theory from the same definition, on a thread of its own, once the graph has used two fifths of either
(`Usage.FractionUsed`) or interned 800,000 values, and hands it out once ready, the old one answering until then
(*The production oracle*); nothing disposes the old one, which is collected with its last bundle. An engine failure in any of a bundle's calls
declines it rather than throwing, and every claim of it reads Unproven, naming the failure. Running out of ids or
epochs is the graph's, so it retires the generation at once; a fault, such as a primitive that answered one key two
ways, is the bundle's, and the generation serves on.

### The check run: what was sent, read back and checked

**The text sent is read, not the translator's structure.** A sink inside each writer would be a second encoding of what
the code under test meant, and a string assembled wrongly would report the right structure. `CosmosSqlFormReader`
(tests) reads the text into a `ServiceForm` by the service's grammar: `NOT` binds tighter than a comparison and than
`AND`, and `AND` than `OR`, which the guards rely on (`IS_DEFINED(p) AND NOT IS_NULL(p) AND (p != @p0)`); so
`NOT c.a = @p0`, which compares `NOT c.a`, is opaque. Grouping is kept as written, with no junction flattened, regrouped
or deduplicated; a parameter is its value; anything else is opaque, up to the next connective outside its own
parentheses and ternaries, and `<>`, which the translator never writes, is no comparison. It prints its parse back with
the source's parentheses and names, and every text the run captures must print back byte for byte. The grammar is a
measured row, `Service.NotBindsTightestThenAndThenOr`, which every proof rests on in `proof-lines.tsv`: over the 27
documents that set three paths to nothing, a null or a string, `WHERE` keeps for each pinned text what it keeps for the
pinned tree written with every grouping explicit, which the other binding's tree does not, and `NOT c.a = @p0` keeps
what `(NOT c.a) = @p0` keeps. The service evaluates both sides, so no evaluator of the tests in C# is trusted. The
emulator and the probe account (2026-10-22) both answer so, and the row names both.

**The capture is inert unless a test arms it.** The two sites that write a filter's conjuncts — admission, the renderer
`CosmosOracle.Admit` is handed, and `Translate`, which the implementor appends — tell an observer each text and the
parameters it bound, as the split rule's weakening does (below). Production arms nothing; unarmed, each site reads one `volatile bool`, and the type's `AsyncLocal`
is allocated once a process. Armed, the observer is the flow's own, so a test planning beside it is told nothing. Two
accessors on the statement hand the check its scope and its declaration, and the declaration says whether a rule it
reads is conditional, for the triage; nothing in production calls them.

**A check is what the service is asked together.** The service reads a whole `WHERE`: a filter's conjuncts, and those of
any filter stacked beneath it that the implementor conjoined in. Two conjuncts proven apart, `label >= X` and
`label <= Y`, may still be a range over bounds that part, and the `AND` composes only under its licence. So each `WHERE`
sent is one check: every conjunct of every statement it holds is written into one bundle as one IR junction and claimed
as the reader reads the `WHERE` itself, and S reads the licence and the range need across all of it. A statement never
sent is claimed as `Translate` would write what its rounds admitted, read from those texts joined, each one's parameter
names apart. A conjunct whose IR holds a parameter or an opaque term, or whose text does not read back, is skipped,
never one whose reading S cannot yet judge; it is written as an opaque term, which no form agrees with, so a check that
holds one is unproven and nothing the service reads beside a checked conjunct is left out of its check. Each conjunct's
own position is read from the same run, and reported beside, never claimed.

**The import is the statements' root.** A bundle takes one source, and a check holds every conjunct its statements'
rounds admitted. A stratum assumes the conjuncts admitted before it, and the node a residual conjunct renders from is
the last stratum, which assumes them all: checked there, those conjuncts would be agreed with only where they already
hold. The root (`D0`) assumes none; it is the node round 1 renders from, and every statement over one container
shares it in a generation, so statements rooted apart fail the run. Its facts are weaker, never wrong.

**A later round's conjunct is read beside the facts it was rendered from.** The translator renders a round-2 conjunct
knowing the round-1 conjuncts hold: over `kinds`, where kind is `"a"` the label is a string, so `kind = 'a' AND label =
'30'` is sent with a bare `c.label = @p1`. From the root alone a number 30 stands beside kind `"a"`, two paths a
conditional rule ties that S imports apart, and the check was unproven, or refuted on a cell no declared document holds
(differential 394, 405, 410). So each atom of a later round's conjunct is stated beside what its path may hold in the
node the licence table rendered that conjunct from (`assumed`, `RenderingBundle.Assume`). No licence is chosen among:
an atom reads its own conjunct's stratum, and one two conjuncts share reads none, since one may be what the other's
stratum assumes. The bundle refuses a stratum that is no later round of the import's statements, one beside a path it
did not import, and one that admits more than the import.

**A stratum is read only where the facts graph reads what it assumes as Calcite does.** Every conjunct it assumes must
be in the check, and read by the text accessor, typed `BOOLEAN`, a cast of the accessor to an integer type, a raw value
beside no number, or a presence test (`RenderingCheckRun.ReadExactly`), whose coarse readings leave out no truth Calcite
gives, which `CellAnswersStayInsideTheCoarseAnswer` holds them to. A stratum resting on a coarser reading can be wrong
where the translator relied on it: the facts graph reads typed `INTEGER` `= 5` as the integer 5 alone, where a column
reads 5.0000000000000001 as 5 too, so beside a label the declaration ties to kind 5, a bare `c.label = "7"` would be
proven where a column keeps a row the service drops. There the label is read from the root, and is not proven.

**A cell the stratum admits nothing of is set aside.** It is found by cutting the stratum's value where the import was
cut (`PathCell.SetAside`): each of its cells lies within one of the import's, and a cell their inhabitants miss holds
only documents that fail a conjunct of the same junction. Its Calcite truths are the policy's set-aside ones
(`tolerated`, which the `classes` rule hands in): the service may answer anything there but a failure, and the cell
refutes nothing. That is sound for the claim at the root: the check is the `AND` of every conjunct at `Pos`; the assumed
conjuncts agree, so on a document outside the stratum both sides drop the row, and the service is still evaluated on
every cell, so no failure hides there. A conjunct's own verdict beside such a cell is exact where the conjuncts its
stratum assumes hold, which is what the translator's licence promised. No fact is met: the import stays one value a
path from one node, and a stratum narrows only the cells of its own conjunct's atoms.

**The run decides whether a verdict may be read, never what it is.** Each input is planned and implemented in a flow
armed for the capture, and read only afterwards, so the importer opens nothing planning did not; the
corpus's statements share a harness and are planned one at a time, the rest two at a time. A bundle runs within count
limits that grow with the form, since descent and evaluation peel a junction a member at a time. A check is memoised by
its IR, the import spelled whole, the form's text, the claim, the set and the theory's rows hashed: the claim, because a
weakening's `Implies` holds where an exact push's `Equal` does not; the theory, because a planted row is another theory.
A bundle that declines on an engine failure, at any call, fails the discipline line as a run stopped short does: its
Unproven is no answer of S's. CI asserts under `Portable`, every target the adapter serves, since the probe account's
record of 2026-10-21 widened to Azure the outcomes it was run on; every check is read under `Emulator` beside it and
reported, so a key the two answer apart shows where only the emulator would agree. The reader's grammar is the one
row every proof rests on that the probe account has not run (`TODO.md`).

**Two cross-checks hold the capture to what was sent.** A conjunct's text at implementation is its text at admission,
its values inlined: both render from the stratum that admitted it. And the implementor's `WHERE` holds the conjunction
of the texts its statements wrote, and reads back as itself; anything else in it is counted as written outside the
translator, and a claim that holds it is unproven.

**The reports** (`TestResults/rendering/`): `verdicts.tsv`, every conjunct a check holds with the check's verdict under
each set and its own; `refutations.tsv`, each refuting cell, the value it puts at its path, what the emulator made of
that document where the witnesses ran, and the triage, from the check's own import: a refutation warns where a refuting
cell's path defaulted constrained, or where a cell spans two paths under conditional rules the facts graph reads, and
otherwise fails CI, and beside them each conjunct refuted at its own position in a check that is not, reported, since
the service is asked the junction its siblings may decide; `witnesses.tsv`, each witness the emulator ran; `vacuous-proofs.tsv`, a proof that holds only where Calcite raises, or a
render licence that keeps nothing (#217's shape), which fails CI; `proof-lines.tsv`, the checks resting on each registry
row, which every proof must name; and `skipped.tsv`. Every statement that fails to plan or implement fails CI too,
rather than leaving the check smaller.

### Numbers on cells

**A literal's double parts its cells, since rounding never falls as a number rises.** A double compares doubles; so does
the service any number it holds as one, an integer type's column any number not written as an integer, and `RETURNING
DECIMAL` the rendering a double gives. Cut by value alone, the fractions beside a literal hold some whose double is the
literal's and some whose is not, and answer both ways. So a reader's `Double` edge parts each interval at every double a
literal rounds to that its values' doubles may reach — below it, at it, above it — which with the literal's point are
its five slots. `Numbers` holds its double bounds closed: a double below another is at most the one before it. A decimal
becomes a double by parsing its invariant text (`DoubleBound`), never by .NET's cast, which rounds twice and misses at
9007199254740994.4. The search for an inhabitant halves toward the double bounds and steps beside the edge it finds, so
an empty slot is dropped and an inhabited one never is; the property test asks each cell of values a rounding's width
beside every cut.

**Each reader's edges are a row** (`ReaderEdgesTable`): an integer type's limits, and a cast's one either side of a
literal, where it raises, truncates or saturates; a double edge for every reading that compares a double; and the text
a literal renders as for a parse, which reads `"2"` as 2. A cast of a raw value has none, to text or to a number: each column the raw value is read through casts as its
own, an `ANY` wrapping a long beyond an int where a `VARIANT` raises, and no row states it (`TODO.md`).

**A typed reading is the union of `RETURNING` and the column, read from the code.** `RETURNING` reads an integer written
so and raises over anything else, `BIGINT`'s over one an int holds; the column reads that, and any other number as its
double where that is whole and within the type: 42.0, a fraction that rounds to a whole double, past 2^53 another
integer (`CosmosJson.GetInteger`). So a `BIGINT` past 2^53 answers both ways at the literal, and a bound one off under an
int is unproven, not refuted, since `RETURNING` raises there; the column's own reading is decision D12's. The coarse
typed reading leaves both out (`TODO.md`), and the cells do not copy it. A cast or `RETURNING DECIMAL` reads a string
by its own grammar, trimmed as Java's `String.trim` trims, of every character up to U+0020 — Java's integer parse, a
double parse that reads no type suffix, or `BigDecimal`'s (`Renderings`), which `IrEvalCalciteTests` holds to Calcite
over numbers padded with control characters — and a `BIGINT` cast reads text that starts `199` and holds a colon as a
timestamp, which no cell places. `BigDecimal`'s is read exactly, as digits and a power of ten, and compared with the
literal so: .NET's decimal parse read `1e-50` as 0 and a text past 28 places rounded, each an exact spelling of another
number, so a stored string holding one was placed equal to a whole literal Calcite finds unequal. A text no decimal
holds is spelled approximately, and a rendering is read as a decimal only where one holds it. `typed:DECIMAL` is `RETURNING DECIMAL` and a `DECIMAL` column at once: the clause
reads a number not written as an integer through its double's rendering and a string by its parse, the column a
number's text exactly and raises over anything else, and the cells read both. So the string `"2"` is TRUE to
`RETURNING DECIMAL = 2` beside the column's raise, and no cell of it is definite.

**The service compares the value it holds** (`Service.NumbersBeyondLongCompareAsDoubles`): an int64 as itself, any other
number as its double, by value. A whole number in int64, or in [2^63, 2^64), which the emulator holds as written, may
be held either way, so it is placed by both; a literal sent as a double is placed only where that double is it.

### A grouped claim, and the coverage line

**A claim grouped as the `WHERE` writes it composes over the IR's flat junction.** A junction's parts are its members
and those within a member of its own connective, however deep. `Claim` states each junction's members as written, and a
rule states a member of a member of one connective a member of the outer junction, so an arm may take any part; a
nested junction of the IR lies within the same form as its parent, and the form each composes keeps the claim's
grouping of what its arms took, so the composition climbs the IR's flat spine whatever the claim's grouping. That is the
join because the licensed connective is associative, commutative and idempotent, which evaluating a regrouped rest
already took for granted. S has 42 rules, the descent's first arm and rest having become four; Stage 2's line is 56.

**The coverage line reads each conjunct sent by its check's verdict, and by its own where the check holds a skipped
conjunct**, which the line excludes with what it does to the check; decision D3's Unproven, left out until S read the
raw reading's raises, is counted. `verdicts.tsv` names `no exact join`, from S's rows, where a conjunct was never
offered its own written member, and `beside a skipped conjunct`.

### Planted faults: recall and mutations

**A fault is planted where the translator writes, never where anything else reads.** The ten historical translator
faults (M1–M10) and two controls are switches in the translator's own writers (`CosmosTranslatorFaults`): a reading's
test dropped, a guard skipped, a refusal ignored, a position left unflipped. None sits in a recogniser the loader, the
facts or the split rule call too, or the plant would change the IR the check reads and judge a wrong translator against
a wrong reference: M9 ignores the verdict at `WriteComparand`'s call of `CompareOrder`, which the split rule calls
itself, and M10 lets `WriteCallCore`'s path branch take a parsing `RETURNING` rather than change `IsJsonAccessor`, the
gate every clause shares. Production plants nothing; unarmed, each site reads one `volatile bool` (M6, M8 and M9 only on
the refusal path they skip), and a plant is the flow's `AsyncLocal`, so a planning thread beside a test is untouched.

**Each fault is caught as the check run catches a wrong push.** Its triggers are planned and checked between two
unplanted runs: what is written changes under the fault and is the tip's again after it, and each trigger it was found by
is refuted with the least refuting cell by text, as definite as the cells first named. A comparison with `'30'` stays unproven under M6, M7 and M8,
since a number equal to 30 may render so or not; and M10's path over the string `"2"`, TRUE to the clause beside a
`DECIMAL` column's raise, is unproven too, so nine of the ten are caught. The controls are reported: X1's `CASE` condition, in a predicate's place, is
opaque to the loader, so its text is skipped as the tip's was; X2's dropped type test lets a structure through, which
`RETURNING` reads as null and the column refuses, so under the typed union the check is unproven, never refuted.

**Recall replays what 4b65754 sent.** Its eight first-family texts (P1–P6, P19, P25), carried verbatim, are claimed
against the IR the loader reads for the same statements at the tip and refuted with a structure cell; offered to S as
planted templates they are never what it sends (*Faults planted in S*); and on a service each returns the rows
4b65754's pushdown returned while Calcite returns its own.

**The gains were first claimed against the IR the loader reads for each refused statement**, read off the statement
planning read whether or not the translator admits its conjunct. The guarded inequality was Exact; the orderings'
forms, `> @p0 OR IS_BOOL` and `< @p0 OR IS_NUMBER`, unproven over an undeclared label, whose strings may hold U+0000,
which the emulator orders as it stores it. Generate mode now proposes them itself (*The rendering graph in generate
mode*), where the witnesses still replay the inequality's text.

### The soundness line: documents, an evaluator of the service's own, and gate faults

**Every reported proof is held to documents by evaluators S does not share.** `IrEval` is Calcite's side, measured and
held to Calcite; `CosmosEval` is the service's, a document at a time from what the rows state — a test true or false of a
kind, a comparison within a type by its order and across types false, true or undefined, Kleene's connectives, an int64
against a double by value, the emulator's U+0000 as it stores it, Azure's range over bounds the two string orders rank
apart — and never through `ServiceLeaf`, `ServiceCompound` or a cell. `CosmosEvalServiceTests` holds it to the emulator:
each of the reader's forms over a path holding each kind, kept by `WHERE`, by `WHERE NOT` and projected as it says, on
both images, over values on which the two targets' models part — 2^53 + 1 written with a point, and strings holding
U+0000 — so that the model held is the emulator's. A literal holding U+0000 it leaves unsaid on the emulator, which
no row says how it sends. The reader reads a double parameter as the number the service compares, never by .NET's
cast to a decimal, which keeps fifteen digits. A proof is judged on every target the asserted set serves and in both typed modes; a coin `IrEval` tosses
makes a document unmeasured, not a witness, and one every mode raises on is skipped (decision 2).

**Source A samples each proven check's import.** Up to 64 documents set each path to values its import admits: the
literals and their neighbours, a fraction either side of each numeric literal whose double is the literal's, the text
other values render as, strings with a character of each band beside each literal where the import's classes admit it,
numbers either side of 2^53, beside a typed or cast atom the type limits and either side of them and a literal's
rendering padded with control characters, and every kind, mixed so that each value stands in one at least. A document a stratum's facts rule out beside
the conjuncts it assumes is no declared one and is left out, and a conjunct's own proof beside a stratum is judged where
those conjuncts hold. **Source B** plans population D's conjuncts, printed as one Calcite `WHERE` over a container the
trial's declaration describes, and judges every proof on the trial's declared documents, whose conditional rules hold
together, as no sampled document's need: 30 trials a pull request, 5,000 explicit. A raw value is a column only at a key
path, so the container's hierarchical key is three of the declaration's paths, and a parameter, `IS_DEFINED` and a
literal with a control character are not printed.

**The gate faults are each in a graph of their own.** G1, a row contradicting another for one key and target, is
refused at load on either target; G5, a union, by a rule's head or the bundle's scope, is refused by the order test and
trips the wire. G2–G4, a template without the null guard, a service evaluation answering `!=` over a null as false
beneath it, and a disjunct's form offered to its sibling, are now planted over templates, and G6–G8, G11, G12 and G14
beside them, in generate mode (*Faults planted in S*).

### Witnesses, and the precision line

**A verdict's cells are documents the emulator runs.** Each refuting cell of the tip's refutations, the recall's and
G2's, each document that shows one of S's plants of M1–M10 wrong, and each cell the gains' exact inequality was proved
over, is one document
(`PathCell.Witness`), written to a database of the witnesses' own. The text sent runs as a `WHERE`, under `NOT` and
projected, which must say the same, and the statement runs in Calcite over the same documents: a refuting cell's
document must keep apart on the two, an agreed one alike, and the outcome must be `CosmosEval`'s. A refuting document's
outcome must lie in S's predicted set: a refutation carries the class of the form at the root on its document, the
atom's swapped by each `NOT` above it (`Refutation.Negated`) and kept through a junction, whose sibling stands at the
connective's identity, so the service's outcome must be among its outcomes and Calcite's keeping among its truths.

**Precision triages the tip's refutations by their witnesses**: reproduced is the translator's (`KnownRefutations`),
not reproduced is false. With each later round read beside its facts the tip refutes nothing, so nothing is triaged and
nothing is false; the three refutations PR C reported were all of that cause.

### Weakenings, claimed as implied

**A weakening is held to what the split rule's argument needs of it: every row its conjunct keeps.** Beside a residual
conjunct the rule pushes something the conjunct implies, and Calcite rechecks the conjunct above; a weakening too strong
drops a row before the recheck sees it, and nothing restores it (decision D8). So the rule's own site, once it has
translated a weakening it pushes, tells the capture the residual conjunct and the text — the third site, inert
as the other two are, one `volatile bool` a pushed weakening — and the check claims `Implies` of the conjunct's IR
against the text read. Sites 1 and 2 hold the weakening's text to its own node, where the loader reads it; the third
holds it to the conjunct.

**It is imported from the stratum it was rendered from.** The rule renders a weakening from the facts its residual
conjunct renders from — the last stratum, which assumes every admitted conjunct — and pushes it beside all of them, so a
guard those facts delete is gone only where they hold: beside `kind = 'b'`, where the label is an integer, the cast's
weakening is the bare bound `c.label > @p0`, implied there and unproven from the root. A residual conjunct's facts enter
no round, so the stratum assumes nothing the check holds. A refutation is a row lost, triaged as an exact push's is.

**Implied counts toward no line but soundness**, which source A samples as it does every proof, a `RETURNING DECIMAL`
in both typed modes; coverage is the exact pushes'. A weakening is unproven over strings that may hold U+0000, over a
cast of a raw value, past 2^53, and where it is shaped otherwise than its conjunct — `IS_DEFINED(c.category) AND
IS_DEFINED(c.price)` for `NOT (a OR b)` — which S composes only where the form is the join of its children's.

**M11 is caught as Implied → Unproven.** Without its number branch the weakening of text `label = '30'` keeps the string
alone and loses the number 30, which Calcite may render as `30` or as `30.0`, so no cell is definite and nothing refutes
it; the tip's weakening is implied and the planted one is not. Its switch is read only where the literal parses as a
number.

### Cost, size, and where the check runs

**The check runs with every suite, once a process, and planning is most of what it costs.** The pull request's run is
one lazy value the check, soundness and witness tests share. It plans and implements every input's statements armed,
the corpus's one at a time and the rest two at a time, and checks each memo entry under two sets; its bundles are a
small share of its time, and the rest is planning, which the stage's plan budgeted at 9.6 ms a statement warm on a quiet
machine. What it costs a leg, and the emulator leg beside it, stands above the +13–17 s and +10–20 s decision D11
accepted (`TODO.md`): the owner accepts the measured figures, or takes D11's fallback, the partition run's first 100
predicates, which moves the coverage and precision lines off the 200 they are measured on.

**The reports travel with the leg's results.** The runner sets each test's working folder to its assembly's, so
`TestResults/rendering/` lands there, and CI's archive step copies what tests wrote there in beside the trx. Witnesses
run on the Linux x64 leg, the one with an emulator; elsewhere `refutations.tsv`'s emulator column reads `not run`. The
rendering tests build under the asserted set, all but four, which state what the emulator's set alone holds — `Range`
licensed, and a key it alone measured — and name it.

**A junction's descent is polynomial in its width**, composed up from what its arms agree with (*Composition through
AND, OR and NOT*): what is left of a bundle's cost is the floor every bundle pays, 25 to 34 iterations of five strata,
and the compound evaluated a member at a time on its cells, which an `OR` of 16 equalities peels in 88 iterations.


## The rendering graph in generate mode

S's second stage writes what it proves. For each `WHERE` conjunct it owns it proposes forms from templates, its gate
admits or refutes each exactly as in check mode, `BestExact` keeps the cheapest exact one, and `Implied` gathers what a
residual conjunct's weakening is built from. Until the switch it is judged in tests alone: nothing in planning reaches
it, and every commit's plan digests are byte-equal to its parent's, which the subsections below take as read.

### Templates, prices, and the weakening's assembly

**A template only proposes; the gate disposes.** A template is a core — the comparison, an `IN` as its equalities, the
presence it tests — and a guard: A1–A4 are what the translator writes, A5, A6, C1 and C4 what it pushes as a weakening
or what the gains need. Which template is asked at which position, and of which atom's shape, are host rows
(`RenderingTemplateRows`), and they alone: `RenderingTemplates.Spell` writes whatever pair it is asked that it can, from
the atom's literal and reader and the path's spelling, and reads no fact. A shape is the atom's reader, test and
operand's kind; a parameter's is its own, which no row names, so a parameter atom gets presence and no candidate.
`Spell` is total, since Eggnog 1.1.1 runs a body's calls in no promised order: a pair it cannot write — no comparison
to guard, no literal rendering a boolean beside A5 — is an opaque sentinel, which the service leaf answers widest, a
failure among it, so no box allows it and nothing refutes it. A6 is two templates, one a kind, each asked at both
positions: which kind a text ordering's literal leaves on the kept side follows from Calcite's order by code unit
(`Calcite.TextOrderIsByCodeUnit`, `'true'` and `'false'` above the digits and `-`), and the gate reads that, never the
operator. C3, the text-equality alternatives, is not built: its Rex would re-type the accessor, which Calcite reads as
an assertion that raises on a string.

**A1–A4 are the translator's spelling, so S proposes today's text.** Every one-atom conjunct the check run's
statements send is its template's form, but for the two respellings R5 names: an `IN`'s points in the set's order, and
`NOT (x IS NULL)` in `IS NOT NULL`'s spelling, which A4 proposes at `Neg` alone because its negation strips to the
shorter form. The bare form beats a guarded one only where it is exact too, which is P8.

**C2, the numeric window, is not built either, nor C4 over a cast.** Every reader C2 fits is a cast of the text or
`RETURNING DECIMAL`, and each parses a string Calcite then compares where the service compares no string, so no accessor
of the conjunct writes a number comparison over it as the form means (below), and the window is never exact, admitting
every non-number. It could only ever be refuted or implied and unwritable, a candidate an atom for nothing. K's windows
over those readers and P6's C4 `IS_NUMBER(c.price) AND c.price > 50` are O7's loss 1, listed until Stage 3 (owner,
2026-10-24): no carrier operator, re-typed accessor or other way of sending what the Rex does not mean is built. The fix
is a guarded spelling S can prove, in the shape of `CASE WHEN IS_NUMBER(x) THEN CAST(x AS DOUBLE) > n ELSE FALSE END`,
once the loader reads `IS_NUMBER` and `CASE`.

**A price is a total order.** `Priced` is the form's nodes and its text. `Min` keeps one value whatever order rows
arrive in, and `None`, greater than every real price, is a complete value from the start, so a rule reads "no exact
form" (`priced-is-real`) and never an absence. No registry row ranks a filter form by its cost, so nothing ranks above
the size.

**What may be sent is read off the form, against data beside it.** `sendable` refuses a string ordering whose literal
holds a character of a parting band, read against the parting bands loaded as data, so that no form S sends is one
bound of a range the two orders rank apart, beside any conjunct in any round; and a literal with an unpaired surrogate,
which the SDK refuses to send on either target. That second is one test of a concrete literal,
`JsonValues.IsEncodable`, the one `CosmosEncodable` asks of every string a statement carries: no band can hold it, since
a pair's units are surrogates too, and it is a property of the value the encoder is handed, which no fact about the
documents decides. `rex-writable` admits a form only where the Rex builder writes every comparison, membership and test
in it, by the grammar's rows (`RenderingRexGrammar`): a string over any reader, the accessor re-typed `ANY`; a number
over a `RETURNING` of a number type, which Calcite reads as the value the service compares or raises on; a boolean over
`RETURNING BOOLEAN`; the eight type tests, each with an in-process body; constants and the connectives. A number over a
cast of the text or `RETURNING DECIMAL` is not written: both parse the string `"60"`, which Calcite then finds `> 49`
where the service's `$.label > 49` finds nothing, so the Rex would not mean the form and the builder's round trip would
refuse it. So the builder never meets a member it would have to leave out, which beneath an `OR` or a `NOT` would
strengthen the weakening. Which reader is typed as a number is one table, the readers' type tests `T(p)`
(`RenderingRexGrammar.TypeTests`), which the templates' guards read too.

**The weakening's assembly decides nothing.** `ServiceForm.Joined` flattens a member of its own connective, keeps one of
each part in the arms' order, and folds Kleene's constants at either position; `Negated` folds both constants and strips
one `NOT`; `ServiceForm.Folded` joins a leaf's implied set in the set's order, by `AND` at `Pos` and `OR` at
`Neg`, the identity for none, and prunes nothing: flattening and one part each make a presence test vanish into a
guarded form that holds it. Whether a leaf takes its best or this fold is two rules (`best-is-implied` and its
negation), and `form-is-join-of` holds a composed form to exactly its arms' parts, as a set.

**What holds it.** `RenderingTemplatesTests`: totality over every atom the inputs load, a synthetic atom of every shape
and a parameter; every pair the rows ask spells a form; the translator's spelling, over every one-atom conjunct the check
run sends and a sample of the one shape it sends none of; no two string orderings of one path; the order, the join, the
negation, the sentinels inert, and the surrogate, band and grammar filters.

### The generate theory: positions, bests and weakenings

**Fourteen rules over the check's gate, in seven strata.** Generate mode runs its own schedule beside check mode's, which
is unchanged. Each step that derives or combines a fact is a rule; C# writes values from complete inputs and reads one
answer.
1. *Positions and proposals.* A root stands at `Pos` (`RenderingBundle.Generate`, as `Claim` writes a candidate). Two
   `at` rules carry the position down: an arm stands where its junction stands, and a negation's operand at the
   opposite position. Every node reached starts with `BestExact` = `None` and `Implied` = ∅, complete values that later
   strata only lower or grow, so nothing reads an absence. The `spell` rule joins `at`, `atom`, `template` and `fits`,
   and proposes `Spell`'s form.
2. *Cells, the service, the gate* (three strata): Stage 1's, unchanged, on every candidate.
3. *Lifts and strictness.* An exact agreement that is not vacuous and is `sendable` becomes a price under `Min`. An
   implied one that is not vacuous, `sendable` and `rex-writable` joins `Implied` under `Union`; every Equal box lies
   within an Implies box, so a writable exact form is a member too. Three rules state presence. A reading the
   `strictreader` rows name is strict in its path. A call whose name a `strictop` row holds is strict in what an
   operand is strict in, through `callarg`. A predicate `over` a strict value is null where the path is absent, so
   neither TRUE nor FALSE there: it implies `IS_DEFINED(p)` at `Pos` and `NOT IS_DEFINED(p)` at `Neg`, which a `NOT`
   above turns back into presence.
4. *Bests.* A junction's best is `Joined` of its arms' bests, held by `form-is-join-of` under the connective's licence,
   with an Equal agreement whose proof joins theirs, so the conflict rule and the proof lines carry over. A negation's
   best is its operand's at the opposite position, stripped of a `NOT`; so `NOT (x IS NULL)` is written in `IS NOT
   NULL`'s spelling, and a double `NOT` collapses.
5. *Weakenings.* A leaf takes its best where that best is one of its implied forms (`best-is-implied`), and otherwise
   the fold of its implied set, under both connectives' licences (`best-is-not-implied`). A junction joins its arms'
   weakenings. A negation negates its operand's at the opposite position. `weakening` has no lattice, so a primitive
   that answers one node two ways faults the bundle.

The writer states what the strictness rules read beside the terms, as it states `point`: through a second callback of
`PredicateWriter` it writes `leaf` for every predicate that is no junction or negation, `over` the value that predicate
is, and `callarg` for each operand of every call. A comparison is the call of its operator over both operands; a
membership, `IN` over its value; a null or presence test, `IS NULL` or `IS_DEFINED` over its value; an opaque term, the
value the loader read it as. So which predicate is null where its operand is, is the `strictop` rows' and never a case
the writer picks. The facts graph passes no such callback and builds what it always built, allocating nothing new.
Which operators and readings are strict are host rows, from the definition's `StrictOperators` and `StrictReaders`,
read from the registry (*Strictness from rows*, below). A definition that names none implies no presence, which loses
rows from no weakening.

**Why the lifts stand apart.** No junction composes from an atom's best before that best is final. Every atom's best is
complete when `best` starts, so each junction's best is computed once, from final arms, bottom up. `best` still reads a
value its own stratum writes, and that is sound: a price only lowers and `priced-is-real` only turns true, so every value
it composed from is an exact form. `weaken` runs last and reads every best and implied set complete, so exactly one of
the two leaf rules fires for a leaf. A junction's best joins its arms' bests. Joining dedupes shared parts, so a dearer
arm form that shares more with its sibling could join cheaper. That is not searched: the text may differ, the rows
never do.

**The round's junction is one more root.** `Through(k)` and `Before(k)` are written beside the conjuncts and share
their terms, so the `best` rule proves the `WHERE`'s `AND`. A conjunct with no exact form leaves the junction with none,
and the host derives nothing. Width sets the count limit: the roots' atoms, every `IN`'s points, and a junction root's
arms, at 80 + 8 a unit (`RenderingBundle.WidthOf`, `RunGenerate`). The check run's limit is 64 + 8 a leaf, and the two
extra strata take the 16.

**One discipline for every read.** `Guard` answers for a bundle that declined on an engine failure, and inside it
`Readable` holds that the run completed within its limits, kept one node per class and derived no contradiction.
`ReadBest`, `Agreements` and check mode's `Read` all pass it.
- A run that stopped short reads `Incomplete`, and a merge or a contradiction reads `Conflict`. A contradicted run still
  writes weakenings, and none is read.
- `ReadBest` gives the best and its proof, or Unproven naming the least refuted cell and the cheapest form refuted
  there; a junction root names none, since generate mode composes no refutation. Beside either it gives the weakening,
  `TRUE` read as none: `Joined` and `Negated` fold every constant, so it is the one trivial spelling.
- `Agreements` lists the non-vacuous agreements, which source A and the pairwise check judge.

**The definition holds generate mode's rows.** The template, fits and strictness rows join the parting bands in
`RenderingDefinition`, so a planted fault replaces one row in a graph of its own. The parting bands load as one set.
The grammar is the Rex builder's own and loads from `RenderingRexGrammar` as one registered value.

**The order test reads generate mode too.** It reads the three schedules. Beyond the universals, every other filter
reads complete values, except those that hold of the value they are handed however it moves: `priced-is-real` and
`form-is-join-of`. A function's value matched against a constant must be complete: `Is(false)` on a `Max<bool>` reads
"not yet true". So `best-is-implied` and its negation read finished sets, and the lift reads a vacuity the gate
finished a stratum before. One plant shows each refusal.

**What holds it.** `RenderingGenerateTests`:
- P1 restricted under the asserted set and `Production`, and P8 bare.
- The negations; a junction's best; the round's junction, absent where an arm has none.
- Nothing vacuous lifted; the band filter; a lone surrogate never admitted, though the gate agrees with it.
- Presence at both positions and through calls; a leaf's best or its fold; no sentinel lifted.
- Probe T4's weakening keeping arm 2's `c.kind = 'c'`, and P6's cast comparison weakening to its presence at `Neg`.
- A contradicted and a merged bundle reading nothing.

Every run over the inputs completing within its limits, with one node per class, no contradiction, no decline and no
sentinel lifted, is the shadow's assert (`RenderingShadowTests`), over the bundles in the shape production opens them.

### Strictness from rows, and what an opaque conjunct is read as

**What Calcite answers null for is measured, or Calcite's own declaration.** Two `Calcite.*` rows hold it as data, in a
row's `Subjects`: the functions the split rule's walk listed as code, null wherever an operand is null in every form
each takes (`Calcite.ListedFunctionsAreNullOverANullOperand`), and every reading the loader builds, null over an absent
path (`Calcite.ReadingsAreNullOverAnAbsentPath`). Each row's pinning test reads its subjects and measures each in
process, every operand position of every form and every column a reading fills, so a name added to a row is measured
or refused. The first holds `IN` over literals too, which Calcite declares nothing of and the IR's membership is.
Beside them Calcite declares `Strong.Policy.ANY` of the comparisons, `LIKE`, the arithmetic, the cast, `NOT`, `ITEM`,
`TRIM`, `FLOOR`, `CEIL` and `EXTRACT`. `CosmosStrictness` is the one reader of the three, into the definition's
`strictop` and `strictreader` rows; the rules join them, and no rule was added.

**One row an operator, met by name in S.** The loader spells a call by its operator's name, a `TRIM` by its flag and a
cast from text to text as `CAST`, so the IR cannot tell apart two operators of one name. The reader writes one row for
each operator under each name it is read by — the standard table's, every library's, the adapter's and the geography
functions' — strict or not by that operator's own declaration or measurement, and combines nothing. `strictop` is a
`Min` over booleans, so the theory meets the rows of a name as it loads them: a name is strict only where every
operator it may stand for is, and that conclusion is Eggnog's. It holds `EXTRACT` not strict, since a library also
names a path into XML so and declares nothing of it; the loader never reads an `EXTRACT` as a value, its unit being no
value, so nothing is lost. `CAST` would be held not strict too, since `TRY_CAST` is read by the same name, so the row
measures `TRY_CAST` beside `SAFE_CAST`. A call no row and no declaration names is strict in nothing.

**An opaque conjunct is read as a value beside its term.** `Loading.Opaque` keeps the node it numbered, and a
statement's `Opaques` reads each when first asked, which planning never does: the only new work on a planning path is
keeping that node, and handing a statement the reading where its loading numbered an opaque term at all; nothing read
enters a term, a digest or a memo key. The reading is the predicate's top call, its operands read as values at every
depth and the rest left out, since a strict call is null where any one operand is; and a predicate that is a reading, a
boolean column, is the reading. A predicate compared with `TRUE` is that comparison's call, so `=` being strict is the
row's, not the loader's: no peel stands in for it, and the split rule keeps its own. `RenderingBundle.Write` writes it
with `over` and its calls' `callarg` rows, so `UPPER(c.name) = 'X'` implies the name present at `Pos` and absent at `Neg`,
`CONCAT_WS('-', name, sku) <> 'x'` implies nothing, and a strict call's operand beside a `CONCAT_WS` still gives its
path. Presence stops at the call that answers over a null, so beside a null test or a `CASE` a sibling's path is
still given, where the split rule refused any expression that observes absence. What it reads less than the split
rule: a connective inside the term, read for its truth or falsity (`TODO.md`).

**What holds it.** The two rows' measurements; `CosmosStrictnessTests`, reading S's lattice (the declared and measured
names strict, a name another operator declares nothing for not); the loader's reading of an opaque conjunct beside a
digest that does not move; `RenderingGenerateTests` over the three conjuncts through the loader.

### Ownership, the reading precision, and the generate shadow

**Who owns a conjunct is read off the IR, never off S's answer.** `RenderingFamily.Owns` holds a conjunct with no opaque
term and no parameter, every comparison a reading against a literal, and every reading a key of the edges rows. It
names no shape: within the family S's Unproven is the answer (O2), declined and listed, and a parameter stays the
translator's, whose recheck spine the printer will not write. Nothing in planning calls it yet.

**Which stratum a later round may rest on is data.** `RenderingFamily.ReadExactly` reads `ReadingPrecisionTable`: the
text accessor, `RETURNING BOOLEAN` and a cast of the accessor to an integer type exact, the raw value exact beside no
number literal, every other reading coarse. The check run reads it there too. What makes a row exact is what a stratum
needs: the coarse answer on a cell's state holds every truth the cell gives, and `Kept` of each truth admits every
value Calcite gives that truth on, through every reader the reading stands for, so a stratum assuming such an atom
leaves out no document Calcite keeps. `PredicateAtomCellTests.EveryExactReadingAnswersAsItsCell` holds each row to it.

**The shadow asks S what production will.** For each statement the check run plans, riding its pass, it reads the
bundles of §4.4 in the translator's rounds — production's own from g15 (*The shadow in production code*): each imported from the statement's root, every pending conjunct a root
beside `Through(k)` and `Before(k)`, a later round's atoms stated beneath `StratumFor` — the deepest node of the chain
every conjunct of which S admitted and the facts graph reads exactly, the root otherwise — and S admitting each owned
conjunct it proves. The composite is read where §5.2 reads it, and each residual's weakening from the round after S's
last admission that holds it, or from one closing bundle. A bundle is remembered by its content, the opaque terms'
readings among it, so a statement asked again opens none.
- *It follows the translator's rounds.* Their nodes are the facts graph's, built from the translator's admissions;
  S's own would change a later node, which only the sites writing see (the rehearsal).
- *The translator is compared at one key.* Beside each round's bundle a check bundle of the same roots, import and
  strata claims the translator's texts, so no claimed text's parts reach `BestExact`; and J's weakening of each
  residual is claimed implied beside the bundle S's is read from.
- *Every statement is read.* A statement none of whose conjuncts rendered writes no text, so the capture also tells its
  observer when a statement's rounds run, from the renderer, one `volatile bool` a round, inert unarmed.
- *Every leg's set is asserted on every leg.* `Production` and `Emulator` are both asserted, which holds each leg to
  its own a fortiori, and `Portable` is reported beside. The check run's own lines stay under `Portable`, the stricter.

**What each conjunct is, read off rows the shadow holds.** Same; respelled, where S's form and the translator's text
are both exact at the key (R5's orders, and P8 where S's form is shorter); S's alone exact, where the translator's text
is not, which no switch listing may read as cosmetic; gain; the translator shown wrong (its text refuted at a key as
deep as its own round's stratum: where S admitted a conjunct in an earlier round, from a shallower stratum, the text is
claimed beside S's form there and its refutation says nothing of the translator's own round);
regression (the translator pushes, S owns and declines, not refuted) with its reason; translator-written; and engine. A
regression's reason claims no more than a row says: a form the gate agreed exact that the band filter refused, by S's
own bands; the translator's text exact at the key, so a form no template proposes; a stratum shallower than the
translator's, which names no cause; the composite; or the conjunct on its merits. A residual's weakening is the same as
J's, tighter or looser on source A's documents, S's or J's alone, dropped, or pushed exactly. A looser one says whether
O7 lists it, read from J's verdict at the weakening's key and the grammar's rows and never from J's shape: implied but
holding a member the Rex builder cannot write over the conjunct's readings (loss 1, the text-equality alternatives'
number branch among them, and now K's windows over a cast), or present and unproven to S (loss 5). One whose J verdict
is missing or refuted is unlisted.

**What holds a generated form.** Source A judges every form the shadow's bundles derived over IR-read atoms — each
agreement at both positions, the composites among them, each implied form and each weakening — on documents drawn from
its bundle's import, on every target its set serves: at `Pos` the service's true against Calcite's TRUE, at `Neg` its
false against Calcite's FALSE. A form whose atoms are stated beneath a stratum is exact where the conjuncts that stratum
assumes hold in any mode Calcite may read them in, `RETURNING` or a column, and is held to no failure elsewhere, the
set-aside cells' promise. **The judge never rests on the stratum it judges.** A document is ruled out only where the
container's declaration rejects it, each rule's body and head read by an evaluator of the declaration's own over the
paths the document sets; never by what the facts graph says a stratum admits, which a coarse reading may narrow. So a
stratum fault — G12's coarse reading, G11's own facts — shows as a witness of line 1 itself. The check run's source A
reads its strata the same way, and the pairwise check judges the same documents by the same definition. The sampler
draws the values an import names, so a declared enum is in the documents, and beneath a stratum draws 512, so its
conjuncts hold on some. No form S would send — a root's best or a residual root's weakening — may rest on documents
nothing measured; a raw ordering, or a raw comparison with a number or a boolean, is judged through each column
(*The raw reading's raises*). Source B's trials are shadowed and judged on their declared documents.
- *The pairwise check* needs no `IrEval`: two exact agreements of one node at one position, and S's best beside the
  translator's text where the key proved both, keep alike where Calcite answers; where it raises the Equal box allows
  any answer but a failure, and those documents are reported apart.
- *The fact rewrite* is held by Calcite itself: each conjunct it lowered is asked of S, and S's best is held to the
  original's node compiled by Calcite's executor through the filter's binding, on source A's documents beside the
  original's literals, so a UUID or instant cast the loader reads as opaque is judged too. A lowering to the stored
  form in the wrong case is caught. A conjunct the rewrite deleted is unpaired, and counted.
- *An opaque residual's weakening* is judged at the gate by Calcite itself: the conjunct's node compiled by Calcite's
  executor, each field filled as the reader fills it through the filter's binding, over source A's documents widened to
  the weakening's paths and the node's literals. Where Calcite keeps a row the weakening keeps it. A binding that reads
  a field as text or JSON is left out and counted.

**The gains, as forms.** P1's inequality is sent restricted under every set; P2 and P3 exactly over a twin of `typed`
whose declaration keeps U+0000 out of the label, and under `Production` over `typed` itself; P4's inequality weakens
tighter than J, and its orderings as J's under `Production` and to presence where J's string branch is unproven to S;
P5's second arm keeps its kind; P8 is counted; and P7, a `LIKE` the translator pushes, is not S's. **Line 19 passes with
P6 listed** (owner, 2026-10-24): its C4 form compares the price through a cast, which no accessor writes as it means, so
its weakening keeps presence beneath the `NOT` where J's carried K's window, O7's loss 1 with every number comparison
over a cast or `RETURNING DECIMAL` until Stage 3's guarded spelling. P4's kill reads "no unlisted looser", so its `>=
'30'`, looser under `Emulator` and `Portable` where J's string branch is unproven to S, passes on O7's loss 5. A
parameter atom left residual weakens to its presence alone. On a service each exact gain's text keeps Calcite's rows.

**What holds it.** `RenderingShadowTests`, `RenderingSoundnessTests.NoGeneratedFormHasAWitness` and, at the gate,
`NoOpaqueWeakeningHasAWitnessInProcess`, `RenderingPairwiseTests`, `RenderingRewriteTests`, `RenderingGainsTests`,
`EveryExactReadingAnswersAsItsCell`; the reports `shadow-admission.tsv`, `shadow-weakening.tsv`, `shadow-summary.txt` and
`pairwise.tsv`. The summary gives S's time and bundles by shape twice: of each statement, and of each query, every
statement its plan asked S of, the shape read off the union of their atoms, as §6.3's lines read cost. The machine's load is
recorded beside it by whoever runs it; no figure carries a line before g15.

### Faults planted in S

**Each of Stage 1's historical faults is planted where S would carry it, in a graph of its own**, and Stage 1's
triggers are shadowed through it under both sets a leg plans under. The translator's own plants stay until the switch,
when it no longer writes the family.
- *A template is untrusted.* M1–M3 replace a template's row: the right row goes, and a rule over the same `fits` rows
  and positions proposes the template's form with the guard or type test taken out, since a template's spelling is
  C#'s and a row alone cannot write it wrongly. The gate refutes the planted form at the cell Stage 1 pinned, read at
  the atom, where a `NOT` above swaps the class; nothing sends it; it is implied only where its cell keeps more rows
  than Calcite; and the loss shows as a regression, the right form never proposed.
- *A rule or a primitive is trusted*, so S sends or implies a wrong form, and source A, which shares none of S, finds
  the document. M4 and M5 put a position in the wrong place, and a position is derived down the IR, so each plants
  the descent beside the right one as well as the best that reads it: a negation's operand standing at the negation's
  own position, a junction's arms at `Pos`. M6–M10 come through `Pair` and `Leaf`. The loader reads a cast of the
  accessor to `VARCHAR` as the accessor itself, so M7's dropped cast is the text reading read as the raw value. M8 and
  M9 answer a boolean's `!=` and orderings with a string by its rendering, which is the inequality and order the
  translator borrowed from Calcite; answering `!=` false across every kind would refute the restricted form at every
  number too, and send nothing wrong. M11 lifts a text equality's string form into `Implied` with no agreement.
- *M10 is caught.* The check left it unproven, `typed:DECIMAL` being the clause and a column at once; source A judges a
  document in `RETURNING` mode, where the string the clause parses as 2 is TRUE and the bare equality false.

**Recall, in generate mode.** Each 4b65754 text is taken apart at its conjuncts' atoms, through a `NOT` stripped and
through a junction by the paths each arm reads, and each part is offered as a planted template at the atoms of its
path and position. The part a structure shows wrong is refuted at the pinned cell, never S's best, and implied only
where that cell keeps a superset of Calcite's rows, (F,t) or (U,t) or their duals beneath a `NOT`: never P3's (T,f).
S sends what it sent unplanted. Until the switch the translator writes the family, so under its fault it still writes
each text again (g16 retires that half).

**The gate faults, in generate mode.** G9, G10 and G13 plant what is not built yet, the production memo, the oracle's
generations and the Rex builder, and come with them.
- *G2* proposes beside every template's form that form with its null guard dropped; no node keeps a dropped form as
  its best where source A's documents show the guard kept something out. *G3* adds the service leaf answering `!=`
  over a null as false: the gate agrees with the dropped inequality, `Min` prefers it, S sends it, source A finds the
  null label, and the pairwise check parts the two exact candidates there. Their check-mode replays need no fault now, the conjunct read off the statement planning read; S's
  plants of M8 and M9 beneath them change no cell of `<> 'bikes'`.
- *G4* offers the second disjunct's form to the first; it is never the first's best nor within the root's.
- *G6* lifts a best from an implied agreement, so the raw inequality's bare form, which keeps a null, is sent. A best
  carries an Equal agreement by construction, so source A now judges every best as the Equal agreement it must be,
  which an unplanted run adds nothing to, and finds the null; the pairwise check parts it from the translator's text.
- *G7* writes presence at `Neg` as at `Pos`, which source A's Implies at `Neg` refutes. The fold's own dual is not
  planted: a weakening is lattice-free, so a second fold beside the right one faults the bundle, which is the
  tripwire; and a conjunction of implied members at `Neg` is looser, never wrong, but over none, where it is `TRUE`.
- *G8* builds a negation's best from its operand's best at `Pos` where both stand; source A at the root finds it.
- *G11* hands a conjunct its own stratum. Planted in the oracle (`CosmosOracle.RenderedFrom`) it never reaches S, the
  importer reading the licence table's node, which renders no conjunct from its own facts; so it is planted where it
  acts, the shadow stating a round's conjunct beneath its own facts with the licence table's assumptions kept. S then
  proves the bare form, and line 1 itself finds the null label it keeps.
- *G12* reads a stratum resting on a coarse reading as exact, the TODO's kind 5.0000000000000001 beside the label 7:
  beneath it the bare label is exact and drops that document, which what S sends as built keeps, and line 1 itself
  finds a document whose label is the number 7 beside a kind a column reads as 5.
- *G14* fails a later round's arm on the cells its stratum sets aside; a set-aside cell tolerates anything but a
  failure, so neither the arm nor the round's junction has an exact form, and nothing is admitted.

**The witnesses run S's plants.** An untrusted plant's refuted form is read at the conjunct, beneath its `NOT`s, with
its cells' document; a trusted one's wrong form, with the least document source A found it on where the emulator's
model parts from Calcite. A trigger with a second conjunct or a junction above the planted atom gives none, since
generate mode composes no refutation through a junction (`TODO.md`).

**What holds it.** `RenderingGenerateMutationTests`, the ten caught and M11 beside, the tip wrong on no trigger and every
trusted plant changing what S sends or implies; `RenderingGateFaultTests`; `RenderingRecallTests`;
`RenderingWitnessTests`.

### F's generations before S reaches a plan

**Why first.** g08 roots every statement at `D0`, and the switch puts S in every plan; both live in long-lived graphs, whose
ids and epochs are finite (about 2.1 billion and 4.3 billion; Eggnog's architecture §2.12). Past either, every call that
needs one throws `OverflowException`. S retired on `Usage.FractionUsed` and declined that exception from the start; F
retired on interned values alone, and only `D0`'s build caught it, so anywhere else in F running out failed the plan.

**What F does now.** A generation is also retired once its graph has used half of its ids or epochs
(`JsonFactGenerations.DefaultUsedLimit`), the other half being headroom for the contexts still open on it; S builds its
successor earlier, at four fifths of it (*The production oracle*). `OverflowException` is one of F's engine failures (`JsonFactTheory.IsEngineFailure`): the context it met is released
and its node reads its parent's, a path the context is told of is read in the context, knowing nothing, and a declaration
that cannot compile states nothing — facts lost, none invented. Running out leaves `FractionUsed` at 1, so the next
question starts the next generation, with no flag of its own. S's bundle and `D0`'s build share the list.

**What a plan runs now.** Each read of F's current generation reads the graph's `Usage`, two counters beside the interned
count it read before. No plan moves: the digests at 200 and 1,000 predicates are byte-equal, both frameworks.

**What holds it.** `JsonFactTheoryTests.AGraphPastTheGaugeStartsTheNextGeneration`, the gauge forced to just past what a
generation uses defining the rules, since no test can spend half of 2^31 ids; and
`AnOverflowLosesAFactAndThrowsNothing`, a context's mention throwing as the graph would.

### `D0`, every statement's root

**Why before the oracle.** Every bundle S opens imports from its statement's root, and round 1 renders from it. The
declaration's context holds no `possible` row, so rooted there a round-1 conjunct imports the top for every path it
reads, counted constrained, and S proves little of it. The check run and the shadow were rooted at `D0` by a test's
switch; g11's oracle and g12's admission import from production's root, so production roots there first, in a commit of
its own that changes no plan.

**What F does now.** `StatementTerms` roots every statement at `D0`, or at the declaration's context where it has none
(nothing mentioned, or a run that stopped short). The switch, its `AsyncLocal` and the oracle's `d0|` key prefix are
gone. A planning path now builds `D0` once a declaration and generation, on the first statement asked of it, and opens
every stratum and every question's context beneath it, where the local rule is idle, rather than beneath the context.

**What it costs, and what holds it.** `D0`'s build is F's time: 1.8–5.6 ms a declaration at the median at 16 % load
(Stage 1), 5.3–15.0 ms with the machine saturated. The corpus planned new rooted at `D0` against the context differed by
a median of −0.40 s (quartiles −0.84 to +0.25 s) saturated, and −0.05 to +0.08 s at Stage 1: below the noise. The plan
digests before and after are byte-equal at 200 and 1,000 predicates, both frameworks, the evidence
`TheDeclarationsPossibleRowsChangeNoPlan` gave with the switch off and on; it retires with the switch, as does the cost
test that timed the corpus both ways. Population D's 200 trials a run and 5,000 on request root at `D0`, so the 50 armed
trials beside them retire; `DeclaredPossibleTests` still compares `D0`'s rows with a stratum rooted at the context.

### The cast of a raw value to text

**Why it was the translator's.** `Owns` holds a conjunct only where every reading has an edges row, and
`CAST(c."$.category" AS VARCHAR)` had none: its cells read it as the rendering, where over a `VARIANT` Calcite casts a
number or a boolean to null. So the translator wrote it beside S, outside the family, and O2's residue listed it:
differential 206 and 236 a pull request, five more at the gate. What it lacked was one measurement in process.

**What Calcite does is a row** (`Calcite.CastOfARawValueToText`, *Calcite's side, measured in process*). Over a value
that is no string the three columns part: a `VARIANT` answers null, an `ANY` its rendering, a `VARCHAR` column fails
the statement, and over a structure the `VARIANT` raises too.

**What S reads.** The reader has an edges row with no edges, as text has: it answers at the literals alone. On a cell
`PredicateAtom.OnCell` reads it as `RawText`, the union of the three columns: a string exactly as text, a null as null,
and anything else as the `ANY`'s rendering beside the `VARIANT`'s null and the raises. A long renders in its digits,
which hold no point, and any other number as Java spells its double, which always holds one; so a cell reads a text
without a point as a rendering only of a long it holds, and one with a point only where its numbers reach the text's
double. Under decision 2 the raises refute nothing. The union proves `c.category = "b"` for `CAST(…) = 'b'`, refutes
`NOT (c.category = "b")` for its negation on a stored null, which every column reads as null, and over a boolean finds a
`VARIANT`'s UNKNOWN beside an `ANY`'s TRUE, which no form keeps alike; so S declines the negation, and the translator's
guarded form for it is implied and no more.

**What F reads is unchanged.** The coarse reading, `Text` over structures, stays the `ANY`'s rendering, and the facts
graph decides from it as it did (`TODO.md`). A planning path parses the reader into `RawText`, whose coarse answers are
`Text`'s own: the plan digests at 200 and 1,000 predicates are byte-equal, both frameworks.

**What moved in the shadow,** at 200 predicates, under every set. S owns 12 conjuncts more. 206 and 236 are the same
form as the translator's; partition 584's disjunction holding the cast is respelled; 588's `NOT (CAST(…) <> 'Sprint!')`
is a gain; the other eight are declined alike. The regressions are unchanged. Three weakenings J held unproven at the
key are now implied there, and stay looser as O7's loss 1, the number or boolean member being no string comparison the
Rex builder writes; two S had read as J's are tighter than J's now, and 588's is pushed exactly. The check run reads 206 and 236 Exact.
At 1,000 predicates 41 rows move: partitions 1371, 1650 and 1908 are the same as the translator's, and 1372 and 1651,
`NOT (CAST(…) = 'x')`, are regressions on their merits, 36 under `Production` and 44 under `Emulator` against O2's 53.
The translator's guarded form keeps there a number or a boolean that a `VARIANT` reads as null and Calcite drops: a
decline that is a correctness fix, which no form proves and the `VARCHAR` column's raise keeps S from refuting.

**What holds it.** `CalciteVariantMeasurementTests.ACastOfARawValueToTextIsEachColumnsOwn` pins the row;
`IrEvalCalciteTests.TheCastOfARawValueToTextAgreesWithCalcite` holds `IrEval`'s cast, through each column, to
Calcite; `PredicateAtomCellTests` holds every cell to `IrEval` through the three columns, a long's digits and a
double's spellings among the literals; and the soundness line judges every form over the cast through each column
(`IrEval.RawModes`). `RenderingGateTests.ACastToTextOverARawValueReadsItsThreeColumns` pins the three verdicts above.

### The raw reading's raises

**Why it was unproven.** On a cell S read a raw ordering as the facts graph does, any truth beside a raise, and a raw
equality with a number as UNKNOWN beside every string: no string ordering over `id` was exact, though every column
orders a string by its units. `IrEval` tossed a coin on a raw ordering, so no document measured a form over one.
Decision D3 left their Unproven out of Stage 1's coverage line, and PR A's line 2 counted their forms apart.

**What Calcite does is the row's** (`Calcite.RawNullIsSqlNull`, *Calcite's side, measured in process*). A `VARIANT`
has no ordering; an `ANY` orders within a type as the type and raises across types; a `VARCHAR` column, beside a
string, orders a string by its units and fails to read anything else. An `ANY` compares a number in the box the reader
built, a long by its value and any other number by its double's exact value, and a `VARIANT` is equal to a number boxed
with its own type alone, which the IR does not keep. The row now says so, measured on 2^53 + 1 written with a point and
a stored `0.1`.

**What S reads.** `PredicateAtom.OnCell` reads the union of the three columns, each as `RawColumnsTable` states it:
whether it reads a value of each kind, whether it orders, what its cast to text makes of each kind. The table is the
rows' facts as data, asserted beside their measurement, and the cells only look it up. An ordering places a value of
the literal's type — a string by its units, a boolean false first, a number by `PathCell.SidesOf` — beside the
`VARIANT`'s raise, and across types is the raise alone, which under decision 2 any answer but a failure refines.
`SidesOf` places a number as a box holds it, a whole number within a long by its value, its double or both as its
caller's row reads one, any other by its double, and a literal neither a long nor a double nowhere; the service leaf
and the raw cells each name their own readings, both today since the theory spells 30 and 30.0 alike, so that a run of
the probe account that separates the service's moves no Calcite cell. Two doubles compare by `Double.compareTo`, which
puts a stored -0.0 below a double 0 (measured), and the IR spells that 0 as any other, so an ordering against 0 over
a cell holding 0 reads TRUE or FALSE. An equality with a number
is FALSE of any other type and of a number no box reading equals, and TRUE or FALSE of one a box may equal, since the
`VARIANT`'s is the literal's. The raw reader's edges row cuts at a number literal's double, where a double's box
changes its answer. So `c.id < "shoes"` is exact for `raw(id) < 'shoes'` under `Production`, and under `Emulator` where
no string may hold U+0000.

**What F reads is unchanged.** The coarse raw reading answers as it did, and the cell methods are S's alone, so no
plan moves: the digests at 200 and 1,000 predicates are byte-equal, both frameworks. The facts graph still reads a raw
number by its value where an `ANY` reads one not written as a long by its double (`TODO.md`).

**What `IrEval` reads.** Through a named column, a number in its box. The soundness line names every column for a
predicate that orders a raw value or compares one with a number or a boolean (`IrEval.RawModes`), so each document is
measured through each column but at the `VARIANT`'s numeric coin; unnamed, `IrEval` keeps the facts graph's coin, which
population D holds F to. Line 2 counts nothing apart, and the coverage line counts D3's Unproven.

**What moved.** At 200 predicates no shadow class moves: D3's one conjunct there, differential 237's
`raw(category) IN (30, '30')`, stays a regression on its merits, a `VARIANT` boxing the 30 as no document's number may.
Source A's unmeasured documents fall from 972 to 134 under each set, and the shadow derives 42 forms more under
`Production`. At 1,000 predicates the 18 raw orderings over `id` are S's under `Production`, 12 the same as the
translator's and 6 respelled shorter without the null guards an ordering's UNKNOWN makes vacuous: 18 regressions where
there were 36, against O2's 53. Under `Emulator` they stay, `id`'s strings holding U+0000 for all the import says, which
the emulator orders as stored: a correctness fix there, as the text orderings are. The three raw `IN`s stay on their
merits, and line 2's 69 forms are measured.

**What holds it.** `CalciteVariantMeasurementTests.ARawNullIsSqlNullAndAnotherTypeIsUnequal` pins the boxes;
`IrEvalCalciteTests.ARawNumberInItsBoxAgreesWithCalcite` holds `IrEval`'s to Calcite; `PredicateAtomCellTests` holds
every cell to `IrEval` through the three columns, and `ARawReadingPlacesItsTypeAndRaisesAcrossTypes` pins how tight the
cells are; `RenderingGateTests.ARawOrderingAndANumberReadTheirColumns` pins the verdicts.

### A number literal, as Calcite evaluates it

**What was wrong.** The loader read an approximate literal through `JsonScalar.From(double)`, whose cast to a decimal
keeps fifteen significant digits: `1.0000000000000002E0` became 1, and 2^53 9007199254740990. Every cell, S's and F's,
compares the IR's literal by its value, so the raw cells g10 sharpened proved `c.category != 1` for `<>
CAST(1.0000000000000002E0 AS VARIANT)`, which keeps a stored 1.0000000000000002 Calcite drops; and the facts graph read
`raw = 1 AND text = '1.0000000000000002'` as a contradiction and planned a statement Calcite keeps a row for as empty
`Values`. No soundness line could see it: the cells, `IrEval` and the judge all read the loaded IR.

**What it reads now.** A number literal is the value Calcite evaluates, exactly, or its conjunct is opaque: an
approximate one is its double's own value (`JsonValues.TryExactly`, a REAL's its float's), and one within a decimal's
range that no decimal holds exactly — most fractions, a negative zero — is no literal. One past the range, `1E30`, keeps
the approximate spelling it had, which no cell reads a value from. `JsonScalar.From(double)` and `JsonValues.Of(double)`
hold a double so too, never by .NET's cast. So `1.5E0` and 2^53 are read as themselves, `0.1E0` is opaque, and both
findings are gone. A fraction is not given the approximate spelling: the facts graph would meet it with the exact
decimal a text's preimage holds, and prune a row Calcite keeps.

**What moved.** No plan. One line of the plan digests, at 200 predicates and at 1,000, reads another IR: differential 453's pushed
weakening, `OR(… = '9007199254740992', … = 9.007199254740992E15)`, whose number arm the loader read as 9007199254740990,
now reads 2^53; its plan, its text and its parameters are the same. In the shadow that conjunct was S exact where the
translator's text is unproven, for the literal misread; at 2^53, where a typed `DOUBLE`'s rounding and the service's
reading held wide part, it is a regression on its merits: 12 under `Production` and 18 under `Emulator` against 25, 19
and 45 at the gate against 53. Line 1 judges four proofs fewer at either size, 0 witnesses.

**What holds it.** `CosmosPredicateLoaderTests.ANumberLiteralIsReadAsCalcitesValueOrNotAtAll` holds every number
literal, against a typed reading and boxed for a `VARIANT`, to Calcite's value of it, and
`AnApproximateLiteralPrunesNoPlanItsDoubleKeeps` plans the statement F pruned.

### The production oracle

**What it is.** `RenderingOracle` asks S as production's sites do from g15: one generate bundle a question, of one statement
round's roots, imported from the node the round renders from with each later round's atoms stated beneath its stratum,
run, and each root's `ReadBest` read, its weakening beside. Each schema object has one, `CosmosReasoning.Rendering`,
beside its `JsonFactGenerations`, over the registry's rows and `CosmosStrictness`'s lists. Those are read once a process
(`CosmosRendering.Definition`): fixed facts, not something a schema learns. Planning asks it only where a test arms
the shadow, so no plan moves.

**One a schema object, and nothing the process's but the definition.** The factory makes one reasoning a schema object
(an account one for all its databases), a host's `CosmosSchema` or `CosmosAccountSchema` one for the containers it is
handed unbound, and a `CosmosTable` over unbound metadata its own; the oracle is made with it, and its graph, memo and
readings are that schema's. Metadata already bound keeps its own schema's, so a schema handed another's bound copy
asks that schema's S for it too. Connections share it as they share the schema (docs chapter 6), and a root that built
its schema takes it along when released; a schema added with `AddSchema` keeps it across roots, for as long as the
host holds the schema. No static holds a graph, an answer or a reading, and
`CosmosReasoningTests.ASchemaLetGoTakesItsGraphsWithIt` finds S's oracle, generation and graph collected with the
schema. Every schema's generations are defined from the one definition, but each builds its own: Eggnog 1.1.1 interns
a child scope's values into its graph, whose pools "are permanent and shared by every scope" (`ValueStore`), and
counts every scope's ids and epochs on the graph's counters, so a frozen theory shared between schemas would share
what each schema's questions taught it. A sealed root nothing can enter would be a fixed fact, and could be shared;
Eggnog offers none yet (`TODO.md`). Made is not built: the graph is defined on its own thread the first time it is
warmed or asked, and until the switch nothing in planning does either but under a test's arm, so a schema made and
planned builds no S (`EachSchemaObjectReasonsInItsOwn`).

**The key is what the bundle reads.** `RenderingQuestionKey` holds the roots' digests sorted, each opaque term beside
the value it was read as (its number names it only within one loading, so two conjuncts the loader read apart may share
a digest), the import spelled whole with every stratum stated beneath it, the set, and the frozen theory's rows hashed
(`RenderingAnswerMemo.DigestOf`, moved from the check run's memo). No form and no question: in generate mode the form
is the answer, and one bundle answers admission and the weakening. A parameter is spelled by its ordinal and type,
never its value; a literal is spelled, so each new literal is a new first plan, as in F's memo; and the run's limit is
the roots'. The importer's count of open and constrained defaults scans the declaration's mentions and is no part of
the digest, so the oracle hands it no declaration and a key never scans them.

**What is remembered.** Each schema's `RenderingAnswerMemo` holds 4,096 bundles' answers, the least recently asked out
first, each a `Lazy` the first asker computes and the others wait on. An answer is kept, `Incomplete` among them:
limits are counts, so the same question stops alike. A decline is handed back, counted and forgotten — S unavailable,
an engine failure, running out of ids or epochs, or a conflict, which production reads as a decline and CI fails on —
since the theory's digest outlives a generation, and a remembered `OverflowException` would decline its key for the
schema's life. Answers survive a retirement within the schema: generations defined alike hash alike. So a remembered
answer is served whether a generation answers or not: the graph keeps the current generation's digest, the key is
built under it and looked up first, and only a miss waits for a generation. After a run out of ids, or while a
successor builds, the questions a statement asked before are hits, not waits and declines. A node is read for its key
inside the same guard as the bundle: one its owner let go is disposed, and the question declines rather than throwing,
so the caller keeps the node's owner reachable.

**Generations off the planning thread.** Every generation, the first and each successor, is defined on a dedicated
thread (`TaskCreationOptions.LongRunning`), its rows hashed and one question asked before it is current, so a plan never
pays a definition, a hash, or a generation's first run. The current one is a volatile field read without a lock. Past
two fifths of its ids or epochs, or 800,000 values interned, four fifths of F's gauge, its successor is built and
swapped in when ready; until then it answers, past F's half too, which stands well short of the engine's own limits. A
bundle that runs out of ids or epochs declines and retires its generation at once, and the next question waits for the
successor. A question that finds no generation answering, before the first is built or after the current ran out,
waits for its schema's build at most 5 s from when a question first waited on it, so a plan of many questions waits
once, and past that declines as unavailable. The first definition that throws, a contradicting row or `Settle`
stopping short, leaves S unavailable for the process: the rows are fixed in the assembly, and the definition is read
once and keeps what it threw, so every schema's would throw alike; every question declines, and none throws to a plan.
So does one whose warm-up question fails in the engine or conflicts, which would otherwise decline every question at a
bundle's cost. A first build that fails for another reason, memory short, leaves that schema's S unavailable for its
life (`TODO.md`). A successor is the definition that already built once, so its failure is no row's — memory
short while two generations live, at the moment that is likeliest — and the spec's rationale does not reach it: the
current answers on, the failure is counted, and the next build starts after 10 s. A generation is published under the
lock builds start under, so an asker still holding the one it replaced starts no second build, and every build runs
under no caller's execution context.

**The set per endpoint (O1).** `CosmosSchemaFactory` reads `serviceTarget`, `azure` by default or `emulator`, onto each
container's metadata (`CosmosContainerMetadata.ServiceTarget`), where a host building its own tables sets it.
`CosmosRendering.SetFor` proves Azure's statements under `Production` and the emulator's under `Emulator`, and the set
is in the key: `label < 'a'`, over a label that may hold U+0000, is proven for Azure and declined for the emulator,
which orders U+0000 as stored. `CosmosServiceTarget` is public, where the spec drew it internal, since a host could not
set it otherwise. Nothing reads it in planning yet.

**Cost, read as the lines are set.** Every answer carries a `RenderingCost`: a hit, a bundle or a decline, a waiter
handed another asker's decline counting the decline alone; its run's iterations, which the bundle now keeps; and S's
time from the lookup to the roots' answers. g15's accumulator sums them a query. The tests read a query's shape off the
union of the atoms of every root its plan asked (§6.3, `RenderingGenerateRun.ShapeOf`), as PR A's measure read it, not
off its widest statement; the shadow's summary reads its per-query shapes so, and prints the tightest run's iterations
against its limit.

**What it costs.** Nothing yet on a plan. A remembered answer, its key built from a frozen `D0` included, took 37–59
µs at the median and 82–85 µs at the 90th centile over 56 questions (18 keys) from the declared containers' `D0`s, in
Release with the machine at 65–100 % (`AHitCostsMicroseconds`, three runs), the import's spelling most of it. §6.1's
lever is taken: `RenderingImporter` reads and spells what each frozen node holds of a path once, in a table on the
schema's oracle keyed weakly by the node (`RenderingOracle.Readings`), which the node takes with it when collected and
the schema when it goes; a caller that hands the importer no table, as the check runs do, reads afresh and spells
alike. The same questions took 13–23 µs at the median and 16–31 µs at the 90th centile, the machine at 43–71 %. The
first generation, built first in a process, took 6.0–8.4 s at that load, where a warm one defines in 267–477 ms,
hashes in 70–143 ms and asks its question in 14–50 ms: the rest is the process's first touch of the registry, of
Calcite's operator tables that strictness reads, and of the JIT. In a process that has planned before, the build took
598 ms. Line 29 reads it at g15.

**What one schema's graphs hold.** `CosmosReasoningTests.ASchemasGraphsCost` makes a schema in a warm process and
reads the heap it adds (Release, net10.0, the machine at 100 %). F, with the twelve declared fixtures and the
service's facts compiled and a statement admitted over each, holds 2.7 MB; S built, 4.4 MB more, 4,625 values
interned. 4,096 distinct questions, each opening a bundle, add 36.2 MB and leave 122,153 values interned; the same
questions asked of an oracle that remembers one answer add 32.6 MB, so the memo's 4,096 answers are 3.6 MB, 0.9 KB an
answer, and the rest is the generation's interning, about 280 bytes a value, which no bundle's disposal gives back.
The service facts' memo of 256 statements adds 0.4 MB. So the bound that matters is the generation's, not the memo's:
its successor is built at 800,000 values, and two live while it builds. Each schema object will pay that once S is
asked, where the process paid it once (`TODO.md`, *S's frozen theory is built once a schema*).

**The tests.** `RenderingOracleTests`: `TheKeyHoldsEveryValueTheImportReads` reads every `imported`, `importedchars`
and `assumed` row of a bundle imported as the oracle's was, and finds each spelled in the key, and keys a narrower
node, a stratum stated otherwise, or none, apart; `AnOpaquesReadingIsInTheKey`, `AParameterNeverReachesTheKey`,
`ADeclineIsNotRemembered`, eight askers at once among it, `AnIncompleteIsRemembered`,
`AFailedDefinitionDeclinesAndCounts`, eight questions over a held build waiting one wait,
`ARememberedAnswerIsServedWhileNoGenerationAnswers`, and `AHitCostsMicroseconds`, explicit, which times hits by the
oracle's own clock. `RenderingGraphTests.ASpentGenerationAnswersUntilItsSuccessorIsReady` holds a successor's
definition at a gate while the spent generation answers, `ASuccessorIsBuiltOffThePlanningThread` finds every
definition on a thread neither the asker's nor the pool's and under none of its async locals, and
`ASuccessorsFailureLeavesTheCurrentAnswering` fails one. `CosmosRenderingTests.TheTargetChoosesTheSet` holds the
operand, the metadata and the set, asked of a reasoning of its own that both containers are bound to.
`CosmosReasoningTests` holds two schemas to two oracles and a made schema to no build, and `ASchemasGraphsCost`,
explicit, reads what one schema's graphs hold (*What one schema's graphs hold*). From g15 the shadow asks through the
oracle, so the bundle is written once, and the tests define S from `CosmosRendering.Definition`.

### Admission asked once a round

**What changed.** `CosmosStatementScope` hands its renderer each round whole (`AdmissionRound`): the round's number and
node, the conjuncts still pending in index order, those S admitted in earlier rounds, and the node each conjunct is
rendered from, the round's but where a test plants another. One question a round is what lets S answer a round's owned
conjuncts in one bundle from g15, imported from the statement's root as `RenderingOracle.Ask` imports, and the
translator answer each of the rest as before. Until then the filter's renderer asks the translator of every pending
conjunct, in order, from the round's node, as each conjunct was asked alone, and S proves nothing, so every plan is
byte-equal. The rounds themselves are unchanged: what a round admits enters only the next stratum (#117's shape), a
round that admits nothing ends them, and so does a child that did not complete or keeps nothing. The per-conjunct
`CosmosOracle.Admit` stays for the tests, a three-line adapter over the round.

**The answer says S's apart.** The spec drew the renderer returning the set a round admitted. A set does not say who
admitted, and three things need it: the round's junction, whose arms are S's alone (`Through(k)`, `Before(k)`); which
stratum a later round may rest on; and the composite, which voids S's admissions and none of the translator's. So
`AdmissionRound.Answer` holds what the translator rendered and what S proved apart, and the scope hands S's back on each
later round as `AdmittedByS`.

**Which stratum a later round is stated beneath** is legality, and the scope's: `StratumFor(node, inBundle)` walks the
chain up from the round's node to the deepest node every conjunct of which it assumes is in the bundle and read exactly
by the facts graph (`RenderingFamily.ReadExactly`, the reading precision's rows), and gives the root, which assumes
none, where no node qualifies, and for a node not of the chain. A stratum resting on a coarse reading — a typed number,
`DECIMAL`, a double, a cast of a raw value, a raw value beside a number — may leave out cells Calcite keeps, and one
resting on the translator's conjunct rests on a proof S does not hold; either way S states nothing beneath it and proves
from the root, the shadow's *a stratum read at the root*. The renderer asks it while the rounds run, so it reads the
chain so far under the statement's lock, and asked after it answers alike.

**The whole `WHERE`.** S's admissions stand only where their `AND` is proven, and the host concludes nothing from their
parts. After the last round, and only where S admitted something, the renderer is asked once more (`Closing`) which of
S's admissions stand: every one where the composite is proven — `Through(k)` of the last round S admitted in, where that
round admitted all it asked S; else `Before(k)` of a round after; else `Before` in one closing bundle, stated beneath
`StratumFor` of where the rounds stopped, which answers the residuals' weakenings too — and none where it is not. Then
the rounds run again from the root with S admitting nothing (`Again`): an owned conjunct is residual, never handed to
the translator (O2, O8), and the scope drops any conjunct the renderer still calls S's. The rounds run again once, in
the same admission; the closing call is not repeated, since S admitted nothing.

**One admission a statement.** Implementation finds admission's statement by condition node and binding
(`CosmosFilterStatement.Of`), so it prints what admission chose and asks S nothing. A statement it did not find would
admit afresh — through the memo, or a bundle that could stop short and make `Translate` throw for a plan Volcano chose.
`RenderingCheckTests.ImplementationNeverAdmitsAgain` counts each statement object's admissions over the check run's
pass, while it plans and while it implements: the capture now tells its observer once a round, with the round, so an
admission is its first round. Of the 1,446 statements a pull request plans, each admits once, and three first while
implementing: the unnest's element filters, which `CosmosUnnestRule` asks in planning under a placeholder alias, so
implementation, binding the alias it allocates, finds no statement. Their element paths are opaque in the IR, so S owns
none of them and the translator answers as it did; the test holds the rule for a statement with a conjunct S owns.

**What runs unarmed.** A round allocates its record, its pending array, the closure that names each conjunct's node and
the list of what the translator rendered, and makes one call; the capture's `volatile bool` is read once a round, where
it was read once a conjunct asked. Nothing asks after the last round, `StratumFor` is not called, and nothing reaches
S's graph, its memo or a bundle. Each filter statement holds two arrays, S's forms and weakenings by conjunct, filled
only where the shadow writes; implementation prints the first and the split rule's weakening reads the second.

### The printer

**What it writes.** `Sql/RenderingPrinter.Print` writes a form S chose as the text the service is sent: writing code,
which decides nothing about a document and is held by what reads it back. It writes in the translator's spelling, so a
form S chose as the translator wrote it prints the translator's text: a test `IS_x(p)`, a comparison `(p op @pN)`, an
`IN` `(p IN (@p0, …))`, a junction its members parenthesised in the form's order, a `NOT` over a test bare, as the
translator's guards write `NOT IS_NULL(p)`, and over anything else parenthesised, as `WriteNot` does. A path is the root
alias and each name through `CosmosSql.WritePropertyAccess`, from the spelling the conjunct's IR gives it. An opaque
form is refused: only the check reader and the sentinels make one, and no rule lifts a sentinel. The names of tests and
operators are the form's own (`ServiceForm.NameOf`, `OperatorOf`), so a form's text and its print cannot spell one apart.

**Every literal is bound as the translator binds it.** The spec drew a whole number a `long` holds as a `long` and
anything else as a double. The translator does otherwise: Calcite holds an exact literal as a `DECIMAL`, whole or not,
and `GetLiteralValue` binds it through `TryGetExactNumberValue`, which gives the decimal, unless the number is whole and
its double another number, then the `long` the service holds exactly past 2^53, and declines where neither carries it.
Of the numbers the gate's plan digests bind, 238 are decimals, 41 doubles and 16 longs, every long past 2^53. So
the printer binds the number a form holds the same way, its double read from its invariant text; one held
approximately, which no decimal is, as its double; and refuses one neither carries, as the translator declines it.
`CosmosParameterList.Add` refuses a lone surrogate, which `sendable` keeps out of every form S sends. There is no
recheck spine: S owns no parameter.

**What the form cannot say is the type Calcite gave a literal.** A number compared through a reading Calcite types
approximate, `RETURNING DOUBLE`, is typed `DOUBLE` with it, and the translator binds a double; the form holds the
number, and the printer binds the decimal. To the service they are one number: within 2^53 an integer and a double
compare alike, and past it the decimal is the double's own value, held exactly. Nor does the form keep a decimal's
scale, `30.0` against `30`, which the loader's spelling drops. Of the conjuncts S chose as the translator wrote them,
158 a pull request and 275 at the gate, every one prints the translator's text byte for byte, each parameter of the
translator's value, type and scale, but 2 and 8: J's text-equality number branch, its accessor re-typed to `DOUBLE`,
which an inner filter admits and S's weakening replaces at the switch (O7, loss 1). Were the switch's rehearsal to list
others, the printer would be handed each comparison's reading beside the form (`TODO.md`).

**What holds it** (line 14). `RenderingPrinterTests`, riding the check run's pass: every best of every bundle the shadow
opened, 310 forms under the three sets a pull request and 665 at the gate, prints a text `CosmosSqlFormReader` reads back as that form, written as it was
printed, and printed again from what was read, the same text and values; the same-form conjuncts above; each kind's
spelling; and each literal against `TryGetExactNumberValue` itself, refusals included. The reader reads no `IN`, bare
path or constant, and S chose none; those three are held by their spelling alone.

**Its faults** (line 17, O4). `RenderingPrinterFault` M12–M16, planted through `RenderingPrinterFaults`, an `AsyncLocal`
arm as the translator's: an `OR` in an `AND` unparenthesised, a junction's last member dropped, a `NOT` over a bare
comparison, each comparison of a junction and each point of an `IN` bound with its neighbour's value, a property's first
letter's case flipped. Each is caught by the round trip over the forms S chose, on every form it can change: 22, 206,
10, 91 and 307 of the pull request's 310, and 50, 485, 55, 211 and 662 of the gate's 665 (M16 cannot change a name with
no letter first, `_ts`). A plant reaches no flow that did not plant it.

**What runs unarmed.** Nothing in planning prints at this commit. The printer runs where S's oracle is warmed, which no
plan does yet: each schema's rendering graph prints each generation's warm-up answer on the build's thread, kept as its
reasoning's `Warmed`, so that the first plan pays neither S's first question nor the printer's JIT; it is the
translator's text for P1. When implementation
prints (g16), each node reads the fault switch's `volatile bool`, and a junction its members as they are.

### The weakening as a Rex

**What it writes.** `Sql/RenderingRexBuilder.Build` writes the weakening S read for a residual conjunct as the Rex the
split rule pushes beneath it (g16): writing code, which decides nothing about a document. It writes over the conjunct's
own accessors, which the loader reads (`CosmosPredicateLoader.Loading.AccessorsOf`): for each path, its raw value — a raw
reading itself, a rendering or a typed `JSON_VALUE` re-typed `ANY` by `RawValue`, moved here from the split rule, whose J
calls it here until g18 — and an accessor typed as a number or a boolean. A comparison with a string is written over the
raw value; with a number or a boolean over the typed accessor, as it stands; a type test as its `CosmosOperators` call
over the raw value; an `IN` as its points' equalities, the membership the loader reads back; a constant, `NOT`, `AND`
and `OR` as Calcite's, in the form's order. A view's column that is no rendering has no raw value to re-type: the
translator writes a cast to `ANY` only over a rendering, and re-typing every accessor left 81 of the pull request's
weakenings under the three sets unwritten (`IS_DEFINED(CAST($1):ANY)` over a raw string column). So the builder is
handed a path's raw value and its typed accessor apart, not one node a path as the spec drew it.

**Never a member left out.** Beneath an `OR` or an odd number of `NOT`s a member left out strengthens the weakening and
drops rows Calcite keeps. A node outside the grammar — a path no accessor reads, a literal its accessor holds no class
of, a bare path, an opaque form — makes the whole weakening `null`, and none is pushed: sound, since each residual's
weakening is its own conjunct of the filter beneath. `rex-writable` admits into `implied` only what the grammar writes,
so only a fault makes one. Nothing proves the Rex in production: a claim a weakening would cost each residual
statement's first plan a bundle, for what CI holds over every shape S can hold.

**The translator writes its guard around a comparison.** Calcite's comparison is unknown over a null; the service's
`!=` keeps a stored null, and its `=` is false of one, which a `NOT` keeps. So the translator writes `IS_DEFINED(p) AND
NOT IS_NULL(p) [AND T(p)] AND` before a `!=` at `Pos`, and `OR IS_NULL(p) OR NOT IS_DEFINED(p) [OR NOT T(p)]` after
any comparison at `Neg` (`WriteComparison`), and the Rex of a member there is tighter than the member, by a stored null
(and, through a typed accessor, a value of another type, on which Calcite raises). The spec's round trip — the text read
back as exactly the member, and each member `Equal` to its Rex — cannot hold at those positions, where 129 comparisons of
the pull request's 809 weakenings under the asserted sets stand. Two other answers were measured and not taken. Writing
the stored null into the Rex (`OR p IS NULL AND IS_DEFINED(p)`) makes it mean the member, but check mode cannot prove one
comparison `Equal` to a compound reading, and the disjunct only admits rows the conjunct rejects. A grammar that refuses
those positions drops the restricted and completed templates from every weakening, looser than J.
Neither is needed: the guard costs no row a comparison keeps, since Calcite's comparison is unknown over a null whatever
the reader, and S holds that of every shape.

**What holds it** (line 13). `RenderingRexBuilderTests`:
- *The round trip.* The Rex's text reads back as the member with each comparison as the translator writes that
  comparison's own Rex where it stands, every other node, grouping and order the member's. Each comparison's text is
  proven `Equal` to the loader's reading of its Rex, so what is sent is what Calcite reads, and where the text is the
  translator's guard the member is proven to imply the Rex: the Rex is never the looser. A type test reads back as
  itself, `IS_DEFINED` proven `Equal` to its reading, each other with its in-process body. A comparison whose Rex reads
  as one of the conjunct's own atoms — the bare form of a typed or raw atom — Calcite reads as the conjunct itself, and
  is held to the atom directly: the translator is not exact for some (a typed `REAL`, a `DOUBLE` past 2^53: the residue
  S declines) and refuses others (a raw string ordering it cannot prove from no facts).
- `EveryWritableShapeRoundTrips`, exhaustive over the grammar: every atom the loader reads over each accessor kind —
  text, raw, a `RETURNING` of each kind of number, `BOOLEAN` and `DECIMAL`, the casts, a view's four kinds of column —
  with each comparison and literal class, an `IN`, a null test and presence, generated under each asserted set, an
  accessor's atoms in one bundle, the path free and, under `Emulator`, holding no U+0000, where its order parts from
  Calcite's. Of 1,138 shapes, the 240 outside the grammar (a number over a cast, `RETURNING DECIMAL` or a raw value)
  build nothing and the 898 it writes build; the 792 S holds implied round trip, 712 written as themselves, and each is
  written as a form its atom implies. So the guard costs no row a leaf keeps, and through the connectives, Kleene's on
  both sides (`Calcite.ConnectivesAreKleene`), none a conjunct keeps.
- `EveryPushedWeakeningRoundTrips`, riding the check run's pass: the 809 weakenings S gives a residual under the asserted
  sets, 2,289 at the gate, each built over its conjunct's accessors, translated from its stratum's facts, and claimed at
  the import of the bundle it was read from, under `Emulator` over strings holding no U+0000 as above: at the gate a
  text ordering beneath a `NOT`, over a label that may hold one, is otherwise Unproven, the emulator's order and not the
  builder's, and S's own admission declines what it cannot prove there. 732 are written as themselves (1,987 at the
  gate), the rest under the translator's guard; 93 comparisons (223) read as their conjunct's own atoms.
- `TheSameFormBuildsTheSameRex`: one form builds one Rex by Calcite's equality, read as one term, which the loop guard
  finds beneath by identity. `AWeakeningIsPushedOnce` (#210, over S): each of the 719 weakenings that adds something
  (2,106 at the gate) is applied beneath once pushed, so the split rule matching the residual again pushes it no more.
- G13 (line 18): a builder planted to drop a disjunct is caught by both round trips.

**What runs unarmed.** The split rule's J calls `RenderingRexBuilder.RawValue`, the same lines it held. Nothing in
planning reads a conjunct's accessors or builds a weakening but site W where a test arms the shadow (g15), so no plan
moves.

### The shadow in production code

**What it is.** S beside the translator at production's four sites (spec g15), armed only by tests: admission asks S
once a round (site A), the split rule builds S's weakening of each residual (W), implementation prints S's form (P), and
the residual projection rule says where it fires by S's split (R). `CosmosRenderingShadow` holds a `volatile bool`, a
flow's arm in an `AsyncLocal` — who is told (`ICosmosRenderingShadowObserver`), whether the sites take S's answers
(`Writes`), and the set to prove under, each container's own where none — and the process's arm, which the whole-suite
rehearsal sets from `COSMOS_RENDERING_WRITES` as the test assembly loads, the shared test reasoning's S build started
there as the switch starts it at the first table. Every site asks through `CosmosRendering.Ask`, which is
`RenderingOracle.Ask`, of an observer's oracle or else the statement's schema's, its container's reasoning's: one bundle
code path, the oracle's, whatever asks.

**S's side of an admission** is `CosmosFilterRendering`, one a set the flow asks under, the statement's own first. A
round's bundle holds every pending conjunct and every conjunct S admitted, imported from the statement's root, a
conjunct's atoms stated beneath its stratum — `StratumFor` of the round's node over what S admitted, the root where none
qualifies — but an atom two conjuncts share; and `Through(k)` and `Before(k)` beside. The scope's lock is held, so the
chain it reads is the rounds' so far. Writing, the translator is not asked of an owned conjunct, its "no" is S's (O2,
O8), S admits what it proved, the scope asks after the last round whether S's admissions stand, and where the composite
is not proven runs the rounds again with S admitting nothing: what the switch does, flipped by one default. Beside the
translator, the rounds are the translator's, and what S proves is what it would admit in them.

**What it came to** is read once the rounds are done, when the split rule or implementation first asks: the composite
where §5.2 reads it (`Through(k)` of the last round S admitted in where it admitted all it was asked, else `Before` of
the round after, else `Before` in one closing bundle); S's forms, void where the composite is not; the residuals; and
each one's weakening, from the last round after S's last admission that holds them all, the closing bundle where it
opened, round 1 where S's admissions were voided, or else from a closing bundle opened then, stated beneath `StratumFor`
of where the rounds stopped — never the last stratum a later question opened, which a coarse reading may narrow. A
closing bundle is opened only where a statement asks for it.

**A statement Volcano reads again asks nothing new.** Volcano reads one condition as many nodes, each a statement of
the same terms (`CosmosOracle` keeps one tree a digest), whose rounds ask the same questions of the same frozen nodes. The
merged-view hierarchy's first plan asks S 449 questions over 25 keys, where the spec drew about 23 statements a plan: at
the oracle's 20 µs a hit, 9–27 ms each later plan, against line 30's 10. So the oracle keeps what each statement's terms
were answered (`RenderingOracle.Answered`, weak on the terms), by set and theory and a question named within them — its
conjuncts, the node each is stated beneath, since a node names its import within its terms, the junctions and the opaque
terms' readings — and a question asked before is handed its answer with no key built. A test's plant reads a node no key
names and is asked afresh, a decline is never kept, and the oracle's memory cleared takes what the terms kept with it,
so that a measure's first plan asks again.

**W, P and R.** Writing, the split rule pushes S's weakening of every residual, built as a Rex over the conjunct's
accessors and held by `IsApplied` and `TryTranslate` as J's was, and calls no J; implementation prints the form admission
kept into the statement's list, asking S nothing; the capture is told of S's prints as of the translator's texts.
Beside the translator, the split rule also makes S's split under each set and tells the shadow each weakening's text,
implementation prints S's form into a list of its own, and the residual projection rule says whether it fires by each
split, the class *projection placement*.

**The shadow judges production's bundles.** `RenderingGenerateRun` is the observer: its own oracle over its graph, so
that a plant's graph is the one production asks and each key's first question opens a bundle, which the oracle's hook
lets the shadow read before it goes. After planning, a check bundle of each recorded question's roots, import and strata
claims the translator's texts: one key by construction. Moved onto production's sites, it answers as the reconstructing
harness did, row for row: the pull request's 5,670 admission rows and 2,925 weakening rows under the three sets are
g14's, every class, form, reason and stratum; 1,078 bundles under `Production`, 1,232 candidates over 830 atoms. PR B's
cross-check of the two copies of the bundle goes with the copy.

**The cost instrument.** `CosmosRenderingCost` is an `AsyncLocal` list a test arms around a plan; each question `Ask`
answers adds a `RenderingCostSample`: the site and set, the roots, the oracle's count and time, the candidates proposed,
the bytes the thread allocated, the graph's use before and after, and, where the test hands it the thread's processor
clock, the thread's own time, which a busy host stretches far less than the wall's. Unmeasured, `Ask` is the oracle's
call alone.

**What it costs, on the build machine.** `RenderingPerQueryCostTests`, Release `net10.0`, four processes a fact, S
writing, on a host of 4 cores and 8 threads at 100 % load at the median and the 90th centile of every sample. S's wall
time on a first plan, median / 90th centile: 1–3 conditions 8–21 / 53–76 ms (line 23: 30 / 75), 5–7 conditions 33–56 /
95–237 ms and lists of 5–19 21–109 / 68–357 ms (lines 24 and 25: 120 / 160); again 0.002–0.034 / 0.003–0.044 ms, no
bundle (26); no filter, no bundle (27); the rare shapes 41–670 ms, none above 1 s and none failing (28). The hierarchy's
first plan 0.97–1.59 s (0.70 s at 89 % load), a later one 5–10 ms at the median over fresh connections and 1.2–1.8 ms
over kept ones (line 30: 1.0 s; 10 ms). These move with the host, one process's reading up to three times another's,
while S's share of the plan holds: 3.3–4.0 % of the hierarchy's first plan, which itself took 28–40 s against 22 s at
89 % load. S's thread time, which a thread waiting to run does not gain, reads 4.6–11.9 / 21–28 ms for 1–3 conditions,
14–38 / 36–84 ms for 5–7, 12–45 / 32–112 ms for lists, 0.48–0.62 s for the hierarchy's first plan, and 3.3–8.5 ms at the
median for a later one over fresh connections, 7.8–13.7 ms at most, a collection on the thread among it: within each
pass line but that last. Over a fresh connection the containers' statement trees are new, so the answers their terms
kept are not there, and the plan's questions reach the oracle's memory, which spells each new node's import. The first
plan in a process, paired with S off over three runs of five pairs, differs by −0.77, −0.34 and +1.21 s at the median,
−0.47 s over all fifteen, each pair −2.5 to +2.7 s (line 29: 0.5 s, killed above 1.0 s): S's build costs the process
0.9–2.9 s of processor time, which a saturated host takes from the plan.

**What runs unarmed.** Site A reads one `volatile bool` a round, W one a split, P one a filter written; R has none of its
own. Nothing is allocated, nothing reaches S, and each statement holds three fields more. Armed beside the translator,
the sites ask S and judge its weakenings in the facts graph; neither changes what the translator is asked, so no plan
moves, which `CaptureChangesNoPlan` holds at the gate with the shadow armed.

**What holds it.** `RenderingShadowTests`: line 6 asserted — under each set the legs plan under no more regressions than
O2's cap, each for a reason O2 lists; line 13b — no weakening looser than J's but O7's listed losses, none dropped;
`TheShadowJudgesTheBundlesProductionOpened`; `NoProjectionReachesS` (§8), S asked only of a `WHERE`'s conjuncts and
their junctions. `RenderingGainsTests` holds line 19 on what is sent: P1 as the printer writes it, P5 in the text its
pushed Rex translates to, P6 a listed loss there too. `RenderingGateFaultTests`: G9, the key without the characters,
the strata stated, the set, the theory's digest or an opaque's reading, two questions differing in that alone handed one
answer; G10, a run that stopped short and a contradicted bundle admitting and weakening nothing, and a generation
retired between admission and implementation changing nothing implementation prints. `RenderingConformanceTests` (C6,
line 4) runs every form S chose under `Emulator`, printed, on the emulator over one document per cell its bundle cut.
`PlanDigestTests.RepeatsOpenNoBundle` rides the determinism test's two passes. `RenderingPerQueryCostTests` holds lines
23–30, explicit, on S's wall time, the machine's load and S's thread time read beside, every reading printed before any
is judged.

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
- **Decision, and the owner's to confirm: the error refinement order.** A comparison needs to know
  what a pushed plan may do where one side raises, and the adapter had answered that three ways
  without saying so. #162 treated rows where Calcite raises as a defect, and withdrew the temporal
  rewrites that produced them. The `RETURNING` statement under `WithoutAnOracle` keeps the opposite:
  the pushed statement answers where the engine throws, deliberately. And `SQRT(-1)` is pushed
  although the service fails the statement where Calcite answers NaN, so the corpus left it out by
  name. The rule adopted:

  - **A Calcite error refines to anything.** Where the engine raises, a pushdown may answer rows,
    or fail in its turn.
  - **A failure of the pushdown is never acceptable where Calcite answers,** except the service
    failing a statement that names a declared out-of-domain function: `ASIN`, `ACOS`, `SQRT` and
    `LN`, the four measured by `OutOfDomainArithmeticFailsTheQuery`. A plan that cannot be
    implemented, or a row the reader refuses, is the adapter's failure and never excepted.

  `CosmosDifferentialTests.Refines` applies it, and nothing else in the harness decides it. The
  corpus is stricter on one side, on purpose. A corpus statement whose oracle raises compares
  nothing, so it is reported and belongs in `WithoutAnOracle`. That list in turn requires its oracle
  still to raise, so a repaired engine moves the statement back.

  **What it costs.** The order permits what #162 forbade, so the differential can no longer see a
  regression of #162. That guard is held by `CalciteTemporalReadingMeasurementTests`, which pins the
  table `CosmosTemporalForms.EngineReads` is, and by the rewriter's own tests, not by the oracle. The
  code may always be stricter than the order: #162 is a choice of fidelity, and the order only says
  what a comparison accepts. If the
  owner wants rows where Calcite raises to remain a defect, the first bullet becomes "a Calcite
  error refines only to an error", and the `RETURNING` statement becomes a recorded divergence.
- **The corpus leans into the semantics that have bitten**: null against absent, `NOT` over both,
  grouping by a key some documents lack, `LIKE`'s shapes, and the aggregate forms. It needs the
  emulator and reports inconclusive without one, like every test that needs a service.
- **Zero divergences said less than it looked.** A soundness review ran 31 statements one step from
  the corpus through this same harness, against the emulator, and 21 answered wrong rows or values;
  two more failed. None was exotic: `IS NOT NULL` over an accessor, a `NOT` over a text equality, a
  `CASE` under a `NOT`, `UPPER` over a mixed-type path, an integer division. The corpus had never asked
  them, because it was grown by hand from the defects already found. The translator's share, and the
  point reads that agreed, are kept in the corpus, named `P1` to `P27` as the review named them, beside
  the statements found while fixing them — so the oracle checks every one of them from now on. The
  rest belong to the point read, the lookup join and `DISTINCT`, and `TODO.md` carries them. The
  lesson is the one the subscript taught: sweep the shapes, do not wait for the reports.

  A second review swept the neighbours of those fixes the same way — 147 statements, each run on the
  fixed branch and on `main` — and found every fix holding and some thirty further statements wrong on
  both, none of them new: a predicate read as a value, a cast to text with a width, a rendering grouped by
  or inside a `VARIANT` box, arithmetic in a decimal fraction, integer overflow, and an accessor
  reading through a rendering. All but the overflow were then fixed, and their statements are in the
  corpus under the names that review gave them, beside the controls that say each shape still
  pushes where it agrees; the overflow is a decision, recorded under *Verified against the
  emulator*.
- **A declaration is a premise the corpus had never been given.** `products` and `typed` declare
  nothing, so no statement asked what a declared fact lets the adapter conclude, and a conclusion
  the operator does not license is a wrong answer only a declared container can show. The review of
  the predicate oracle's gate found one: `RETURNING DECIMAL`, read as the stored number, rewrote a
  filter over a declared string to `FALSE`. The third container, `declared`, says `body` is a string
  and `n` an integer, and its documents keep to that — a container that broke its declaration would
  test the caller's word rather than the reasoning. Twenty statements over `RETURNING DECIMAL` came
  with it, there and over `typed` and `products`, and fourteen answered differently before the fix:
  rows lost, and columns that failed to read.

  The review of that fix found it had routed the clause into a window that was already wrong past
  2^53, where a bound loosened by one comes back as the number it was loosened from, and that any exact
  literal there was sent as a double. `declared` gained `v`, an integer holding 2^53, 2^53 + 1, 2^60
  and 2^60 + 1, and sixteen statements over it and `n`: the window through `RETURNING DECIMAL` and
  through the casts, a literal a hair below a whole number, exact comparisons and an `IN` through
  `RETURNING BIGINT`, and controls that still push. Ten answered differently before; a statement
  Calcite raises on whatever the data, `RETURNING DECIMAL(19, 18) = 50`, has no oracle and is pinned by
  planner tests instead.
- **And it is grown by generation: ternary logic partitioning.** The written corpus recorded no
  divergence while ordinary statements one step from it answered wrong rows, so predicates are now
  drawn as well. `EveryPredicatePartitionsTheRows` draws 1,000 from a fixed seed over `typed` and
  `products` — the first 200 of them unless the gate is asked for, as below — from the shapes the
  adapter pushes:
  - comparisons over accessors, promoted columns and casts;
  - the null tests and the truth tests;
  - `NOT`, `AND`, `OR`, `CASE` and `COALESCE`;
  - `LIKE`, `IN`, the case folds and concatenation;
  - `RETURNING` and arithmetic.

  For each φ it pushes `WHERE φ`, `WHERE NOT φ` and `WHERE φ IS NULL` and requires two things. The
  three must partition the container by id, each row once, which is SQL's three-valued logic and
  needs no oracle. And each must be the partition Calcite computes, read in process as the
  predicate's value per row. Both are judged by `Refines`, so a predicate Calcite raises over is not
  judged. A failure is narrowed to the smallest operand that still fails, and the report lists those
  witnesses, not the predicates.

  **On main it fails, and is left failing until the root-cause fixes merge.** Over the adapter at
  `4b65754`, 131 of the 1,000 fail and none goes unjudged. Classified by the smallest witness, a
  predicate counted under each cause it shows:

  | Cause | Predicates |
  | --- | --- |
  | D: `IS NULL` over a boolean expression | 64 |
  | F: a case fold or a concatenation over the accessor of a number or a boolean | 39 |
  | **M, new:** the null guard over a `COALESCE` | 17 |
  | **L, new:** the presence weakening through a `CASE` or a disjunction | 12 |
  | B: the negated comparison over the accessor of an object or an array | 8 |
  | A: `IS NULL` or `COALESCE` over the accessor of an object or an array | 8 |
  | C: the negation leaking into an `IS NULL` operand | 1 |

  G does not appear: the literals rarely meet a quotient Calcite truncates. H to K are about point
  reads, lookups and `DISTINCT`, which a predicate never reaches. F reaches further than the case
  folds: `COALESCE(JSON_VALUE(…), 'none') = '120'` compares the raw number too, because the
  rendering test reaches only the comparison's direct operands. M closed with the guard declining over
  an operand that observes absence, and no predicate fails for it over the fixes; `TODO.md` section
  10 keeps its witnesses, and they are in the corpus with its neighbours. L closed in two halves, a
  `CASE` and then a disjunction, each under *A position belongs to a predicate*: the second was the
  last of the 1,000 to fail over the fixes, two predicates whose `NOT φ` arrived as a disjunction with
  a negated compound for a branch.

  **What it costs, and why.** On a workstation that other work held at full load throughout, two runs
  on main took 17.5 and 23.5 minutes. That was 16 to 21 minutes judging the 4,403 statements, and
  about 1.5 narrowing the failures; a passing run skips the narrowing. Both runs found the same 131.
  The median statement took about 100 ms, and the tail is the cost.
  - **Most of a slow statement is Calcite reducing a literal's cast by compiling it.** `RexSimplify`
    hands the cast to `RexExecutorImpl`, which compiles it with Janino, and under IKVM each compile
    looks classes up by throwing. In a profile of the slowest statements that was 37% of the CPU.
  - **A constant compared with a `DOUBLE` is spelled as one** (`50E0`), and that halved a
    100-predicate sample. A comparison with a `VARIANT` column has no literal spelling and still
    pays.
  - **Batching the oracle made it worse.** Twenty-five predicates projected in one statement took
    95–164 s each, against a median of 50 ms apart.
  - **Four statements at a time was no faster than one** on the loaded machine.

  Whether that fits the CI budget is the owner's call: the whole suite took 12.5 minutes on main.

  **So the run has two sizes, and they are one sequence.** Every run of the class judges the first
  200 predicates; `COSMOS_PARTITION_GATE=1` judges all 1,000, which is Stage 0's gate, and any other
  value, or none, is the default. The seed does not change with the size and the generator draws in
  order, so the default is a prefix of the gate, not a sample of its own: a predicate failing in one
  fails in the other, as the same statement, and the full run only asks more.
  The size was measured against two things:
  - **What it would have found.** Of the 129 distinct predicates that failed on main, the first 200
    hold 20, and they show every cause the 1,000 did except two: C, which one predicate showed, at
    553, and L's disjunction half, whose first witness is predicate 281. The corpus holds written
    statements for both. The first 150 hold 15 and miss L altogether, its first witness being
    predicate 178.
  - **What it costs.** Against the emulator, on a workstation running other work, the default judged
    its 200 in 276 s and the class took 8.1 minutes; the gate judged its 1,000 in 983 s and the class
    took 19.3, and an earlier gate run over the fixes judged for 1,278 s. Inside that 983 s the same
    200 took 182 s. The tail decides it, single statements of 30 to 46 s, so a run's time moves by
    a third with the machine, and a fifth of the predicates costs a fifth to a quarter of the
    time.

  Over the fixes for A to K and both halves of L, the gate's 1,000 pass: 973 distinct, one
  not judged because Calcite raised, none failing, 3,880 statements run.

- **And orders are judged by what Calcite allows: the `ORDER BY` yardstick.** The corpus compares an
  ordered answer as a sequence, which is stricter than SQL on ties, and against a runtime that orders
  multi-key text by the host's culture. Neither is the rule a pushed order is held to. That rule is that
  the answer be *allowable*: rows `o+1` to `o+f` of some arrangement of the input that Calcite's
  comparator finds non-decreasing. Which rows of a tie a page takes is free, and so is their order.
  `EveryOrderIsOneCalciteAllows` (`CosmosDifferentialTests.Orders.cs`) draws the statements whose
  answers depend on an order and judges each by that.
  - **What it draws.** Sorts of one to three keys, over every reading the adapter has: text accessors,
    every `RETURNING`, `id`, `_ts` and `_etag`, monotone and other computed keys, casts to `UUID` and
    `TIMESTAMP`, a JSON fragment and the promoted column. Each key has a direction, a placement, and
    sometimes a page. Then `DISTINCT`, a subquery's page, `UNION ALL … ORDER BY`, `MIN` and `MAX`, a
    keyset chained three pages, comparisons with a parameter and of a path with a path, and a sort led
    by a key that one document leaves absent and another holds null. Its filters are the ternary logic
    gate's predicates one connective deep, beside equalities, memberships, ranges against a literal or a
    parameter, `LIKE` and a pinned partition.
  - **Over what.** Three containers of 48 documents in four logical partitions, written in a
    permutation. They hold strings from both sides of U+D800, ids repeated across partitions, integers
    and doubles either side of 2^53 and 2^63, every JSON type at an undeclared path, and ties at every
    key. `orders_plain` declares nothing. `orders_declared` declares types, bounds, `required`, an `enum`
    whose strings straddle U+D800, and the composite indexes. `orders_ascii` declares the same, and the
    characters of its strings by `pattern` — a UUID, an identifier, nothing at or above U+D800 — and its
    documents keep that.
  - **How it judges.** The input is the statement without its order and page, read in process with its
    keys selected. `OrderAllowability` checks four things of the answer: its rows are the input's, its
    count is the window's, it is non-decreasing, and each tie class holds its share of the window. The
    comparator is written in the test from Calcite's Java semantics (`CalciteOrder`): text by code unit,
    exact numbers, `-0.0` by the number of keys, unsigned UUIDs, `NaN` last. An extreme is judged by
    Calcite's extreme tie class, and a comparison by its exact rows.
  - **And what the service accepts, as Azure accepts it.** Every `ORDER BY` a plan sends is held to the
    indexing policy the container was created with. An excluded path is a 400 on Azure, and
    `/"_etag"/?` is excluded by default; so is a multi-key order that no composite index serves in its
    directions or their exact reverse. The emulator accepts both, so the check is the test's.
  - **Where Calcite raises over the input** the statement is not judged, with one exception: an answer
    that shows its keys is held to its own order. No input makes rows allowable whose keys, as the
    answer shows them, run against the `ORDER BY`. That is how 2^63 stored as a double is judged at
    all: the emulator returns a document whole as it was written, which Calcite's `BIGINT` cannot read,
    and prints a projection of it to 17 digits, as another integer, while ordering it by the double.
  - **A failure is narrowed and named.** It is narrowed through its page, its keys and its conjuncts, as
    a predicate is, and named by its family. The family is read from what the failure holds. Strings
    from both sides of U+D800, or an answer that would be allowable had Calcite compared text by code
    point, are the string order's. A leading key that one document leaves absent and another holds null
    is the null class's. An integer the container holds written in [2^63, 2^64), as written or as the
    emulator prints it, is the read-back's. Where the service was handed no order and
    the answer still fails, the CLR runtime's own order is to blame — two or more keys by culture — and
    the failure is reported apart, as `calcite-dotnet`'s.

  **On main it failed.** Over the adapter at `4b2ccb3`, on
  the emulator, the gate's 1,000 items came to 1,092 statements, of which 115 were not judged because
  Calcite raised. Of the rest, 267 sent the service an `ORDER BY` and 17 an extreme, and 130 failed:

  | Family | Statements |
  | --- | --- |
  | Text from both sides of U+D800 | 80 |
  | Undefined and null on a leading key | 26 |
  | `_etag`, an excluded path | 12 |
  | 2^63 stored as a double, read back as another integer | 4 |
  | *Not an order:* a projected double printed two ways | 5 |
  | *Not an order:* `CHAR_LENGTH` pushed as `LENGTH` | 3 |

  The string order's are sorts over `id`, over declared strings and the `enum`, their keysets, a union
  merging a pushed sort, the text weakening beneath a page, `MAX(c."id")`, and comparisons of `id` or a
  declared string with another or with a parameter. The null class's are two-key sorts led by `t` or
  `k` under the service's placement. `orders_ascii`, whose strings stay below U+D800, has no string
  order failure at all: its failures are the null class's, `_etag`'s and the 2^63 sort's. Two families
  are not orders', and `TODO.md` records them: `CHAR_LENGTH` pushed as `LENGTH`, which counts code
  points, and the emulator printing a projected double past 2^53 to 17 digits where a document read
  whole keeps what was written — since measured to be the emulator's alone, and only an integer written in
  [2^63, 2^64); the family is now named only where it is the whole of the difference, reading each printing
  as the double it stands for leaving the rows agreeing, so it can hide nothing beside it. A third finding
  came from an earlier draw of the generator and is in
  `TODO.md` too: a comparison of two nullable declared strings keeps the rows where both are null. 94
  answers were the CLR runtime's own, every one of them a sort of two keys or more done in process.

  **Asked once, no order it pushes fails** (*The service's order, asked once*). Over the same 1,000
  items, 1,092 statements, 116 not judged: 64 sent the service an `ORDER BY`, 14 an extreme and 2 a page
  with no order, and none of them failed. What fails is the two families that are not orders' — 5 a
  projected double, 3 `CHAR_LENGTH` — and 119 answers were the CLR runtime's own, more than before
  because more multi-key sorts now run in process. The default's 200 items push 14 sorts and 3 extremes
  and fail only in `CHAR_LENGTH`. Those counts are the test's floors (`PushedFloors`): a change that
  pushes fewer fails it, which is how "as many as can be proved" is held.

  **With the declared characters read, `orders_ascii` pushes what its patterns prove** (*Reading a pattern as
  characters*). Over the same items, 110 statements sent the service an `ORDER BY`, 20 an extreme and 2 a page, and
  none failed: `orders_ascii`'s sorts went from 38 to 84 and its extremes from 7 to 13 — its ids and their
  three-page keysets, its declared text, `MIN` of it — and the other two containers push what they did. The same 8
  fail, none an order, and 119 answers were the CLR runtime's own. The default's 200 items push 17 sorts and 4
  extremes. The floors are kept per container too, `orders_ascii`'s among them, so that a reading of the patterns
  that proved less fails there first.

  **With the anchored read, what the rows prove pushes too, and the yardstick runs by default** (*The anchored
  read*). Over the same items, 1,092 statements, 113 not judged: 217 sent the service an `ORDER BY`, 25 an extreme,
  2 a page and 26 a comparison with a parameter the reader rechecks, and none answered what Calcite does not allow.
  The gain is in the containers that declare no characters — `orders_plain`'s judged sorts from 12 to 27 and
  `orders_declared`'s from 14 to 99, with their extremes 3 to 4 and 4 to 8: sorts over `id` and over text of any
  character, their pages and three-page keysets, `RETURNING BIGINT` over an integer nothing bounds, `MAX(c."id")` —
  and `orders_ascii` gains its unbounded integers, 84 to 91, of which the string functions' rule took one back, a
  sort beneath a case mapping of an `enum` holding é (*What a string function computes, asked of its characters*).
  About 117 or 118 answers were the CLR runtime's own — the
  count follows the order the service returns tied rows in, which the in-process multi-key sort is sensitive to, and
  not the build: a Release build gave 118 on the probe account. The two families
  that are not orders' are findings of their own (`Findings`), recorded in `TODO.md` and reported apart where the
  statement sent no order — every one of the 8 does — so the test holds every order it judges and runs with the
  class. Both are the emulator's alone now: the code-point family, three of the 8, was `CHAR_LENGTH` pushed as
  `LENGTH`, which no longer pushes over text of any character, and is narrowed to `INDEX_OF`, which Azure counts as
  Calcite does; the gate meets none of it, and the five left are the integers printed two ways. The default's 200 items push 31 sorts, 5 extremes and 5 comparisons, and the floors are raised to all of
  it, per container, the comparisons among them.

  **What it costs.** On the emulator, on a workstation running other work, the default's 200 items took
  53 to 76 s and the gate's 1,000 took 157 to 267 s when it was built, the class's setup included: about
  two runs a statement, planning being the cost and each read of 48 documents next to nothing. Later runs,
  the containers already seeded, took about 20 s and 70 s in Debug. The default is a run of the class.

  **The model checks the question rather than the answers.** `CosmosServiceOrderModelTests` models each
  partition's order (type order, exact numbers, code point text), the SDK's merge (the same over what
  was printed, UTF-16 text, the lower feed range first on a tie) and Calcite's, and checks every verdict
  of the question "may the service compute this order" pairwise over the values each fact set admits.
  The merge is pinned to the two-partition streams the probe account returned, which no single order
  produces. Built before the one place existed, it asked a mirror of main's decisions, which it found
  wrong in three families: text across U+D800 (52 verdicts), undefined and null on a leading key (20) and
  2^63 read back (14). It asks the one place now, by default, and holds an anchored verdict to the reader's
  own check: every anchor compares alike with every admitted value under the three orders, ties free only on
  the last key, and an extreme read again to the first anchor through the merge is Calcite's. The checker's
  own test plants each family. `CosmosOrderMergeMeasurementTests` measures the merge itself, on a container with two
  physical partitions that the caller names; it creates none, and skips without one.

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
its failure. The test assembly does it from a `[ModuleInitializer]`; a test framework's own
assembly-level hook is not reliably early enough.

The adapter itself does not need any of this: it never opens a connection, and the SQL planning
in `CosmosSqlPlanningTests` drives `SqlParser`, `SqlValidator` and `SqlToRelConverter` directly,
none of which require the driver. The note is recorded because any consumer reaching for
`Frameworks` or `RelBuilder` will hit it.

---

## Cost

Two properties of a predicate dominate what a Cosmos query costs, and neither is visible in the
shape of the plan, so `CosmosFilter.computeSelfCost` reflects both:

- **Naming the partition key** confines execution to one physical partition rather than fanning
  out across every one and merging. `CosmosPartitionKeyExtractor` recovers the value where the
  predicate and the declaration leave the key one value; a disjunction of values or a range does not
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

### What the wire costs is width, and the document is not one value

`CosmosToClrCursorConverter` is the one node every pushed row crosses, so it is where what
crosses it is priced: rows times the width they carry. Width is counted in *values*, and the `DOC`
column counts as many — it carries an entire item where every other column carries a scalar.

A count of columns was tried first and is wrong in a way that decides plans. Calcite's field trimmer
runs in every host and leaves a `LogicalProject(DOC)` above a Cosmos scan, so the subtree being
converted is one column wide before the query's own projection is pushed and two or more after it.
Counted, pushing a projection therefore *widens* this node and charges back precisely what the
projection saved — and since `VolcanoCost` compares `rowCount` alone, the two plans tie rather than
one winning, and the tie falls to registration order. That is #145: a two-column query read a
container whole while the one-column form of it did not.

The document's weight comes from `CosmosContainerStatistics` where the account was asked for one.
Where it was not — the common case — it falls to a floor of six values, which needs no measurement:
the service generates `id`, `_rid`, `_self`, `_etag`, `_attachments` and `_ts` on every item and
returns them with it, so selecting the document returns six values before anything the document
itself holds. What a single value weighs is the one guess, and it is made in the direction that can
only under-sell projecting.

**A residual filter was the same wall by another route (#210).** Where one conjunct does not render,
the filter split leaves it above the conjunct that does, and the query's projection is above both.
One statement has one `SELECT`, and the residual sat between the projection and the statement, so the
statement selected the document. `CosmosProjectResidualRule` moves the projection below a wholly
residual filter, carrying the paths the residual reads, and rebases the residual onto them. It is
narrow on purpose, and each limit was measured:

- **The projection must render whole.** Taking apart a mixed projection here as well gave every
  merged view's `CASE` a projection of its own, and a five-view hierarchy planned for minutes.
- **Paths only, never sub-predicates.** A column per comparison is a projection per spelling of the
  predicate.
- **It declines where the document itself would have to be carried.** That saves nothing, and
  carrying it looped: the carried reference was pushed as a column on the next firing and carried
  again beside it.
- **It declines a projection with a reading that can refuse a value.** The two commute in what they
  answer, not in what they raise. Beneath the filter, the projection is read for every row the service
  sends, and Calcite computes it only for the rows the filter keeps. So a value the reader refuses
  (2^63 or 42.5 as a `BIGINT`, text that is no date) failed the statement over a row the filter would
  drop, where Calcite answers, which the error refinement order never allows. The ORDER BY yardstick
  found it on CI's linux-x64 emulator, which prints a stored 2^63 as Azure does,
  `9.223372036854776E+18`, under `LOWER` over a string nothing proves ASCII. `CosmosJson.CanRefuse`
  says which typed readings can refuse, beside the reader's switch, and a test holds the two together.
  A projection holding one stays above, and the statement selects the document. The filter's own
  paths still go down, because Calcite reads them for every row it tests. Asked by type alone, so a
  path declared within bounds pays too (`TODO.md`, section 7).

The split, for its part, no longer weakens a filter over a projection. `FILTER_PROJECT_TRANSPOSE`
gives that filter an equivalent beneath the projection, where its weakenings are derived against what
is already applied; derived above, they were blind to it and pushed a definedness test twice.

`RelMdSize` is the metadata that would answer this properly, and it does not: measured over these
plans `getAverageRowSize` returns `null` at every node. A handler for the Cosmos nodes is what
`TODO.md` still asks for, and would reach the cost decisions below this node, which price nothing
about what they return.

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
MongoDB all use `Hook.QUERY_PLAN` and stop, which this adapter does too — once per statement per
plan, although the plan is implemented twice, once for the synchronous open and once for the
awaiting one (#211). A meter and an activity
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

**What a type test says of an absent path.** The record disagrees with itself: one remark in the
translator has `IS_PRIMITIVE` false there, this document had it undefined, and the operator table says
only `IS_DEFINED` was measured. So no rendering depends on it. Every null test and guard that asks a
type test names the absent case with `IS_DEFINED` first — see *What an accessor reads, stated once* —
and the projection's `(IS_PRIMITIVE(p) ? p : null)` reads null either way. When it is measured it
belongs here as a fact, and nothing should have to change.

**That nothing but a typed accessor binds a nullable typed field to a path.** `ReadingTests` gives a
field read as its own type the test its type names — `IS_NUMBER` for a number, `IS_BOOL`, `IS_ARRAY` —
on the reading that such a field is a projection of `JSON_VALUE` or `JSON_QUERY` with a `RETURNING`,
whose type the field carries. It is true of every binding today: the scan's own typed columns are
`id`, `_ts` and `_etag`, which are never null, and a promoted column is a `VARIANT`. A column promoted
with a concrete type would be the first to make it a question.

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
