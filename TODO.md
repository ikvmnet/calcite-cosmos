# Outstanding work

What a complete adapter would have, sized and reasoned, so that the next session picks up an argument
rather than a list.

**Sizes.** *Small* is a translator case and a test. *Medium* is a node, a rule, or an SDK surface.
*Large* needs a design decision recorded in `DESIGN.md` before any code.

**On finishing.** When an item is done, remove it — the entry, its rationale, and any *done* marker
elsewhere in this file. This file holds only work still to be done; what was decided belongs in
`DESIGN.md`, what was built is visible in the code and its tests, and history lives in git. A *done*
paragraph kept here is a second copy of one of those, aging independently.

**On testing a change.** Before believing a test covers what it claims, check that it *fails without
the change*. This has repeatedly told both stories: fixes whose tests genuinely depended on them, and
guards that turned out to be dead code — the case they guarded already unreachable. Neither is
visible from a green suite.

**On evidence.** Where a claim about the service is unverified it says so. The emulator has disagreed
with Azure in both directions — accepting an `ORDER BY` over an unnest alias that Azure rejects, and
rejecting the full text search Azure runs — so "the reference says" is not a measurement. Point
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` at a real account and the suite runs against one.

---

## 0. Resuming

**661 passing on net8.0 and net10.0 alike** against Apache.Calcite 2.0.1-pre.103, with the six
service-backed classes excluded — no account and no emulator, so that run says nothing about them and
they were not run at all. The last full run, at 2.0.1-pre.64, was **731 tests: 717 passing, 14
inconclusive**; the inconclusive ones are things only a service can answer. The suite runs against one
when `COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` name it, and reports inconclusive rather than
passing where the emulator cannot — and each of them detects the gap it is skipping for, so an
environment that closes one asserts rather than going quiet. Several facts in this file and in `DESIGN.md` were settled by measurement, each
time with an Azure account, used and deleted. **Not yet measured against an account:** the view
spellings and the case folds added to the differential corpus with #83 and #84.

No PRs are open and nothing is parked; `main` is where the work is and a new branch starts from it.

Reading, writing (`INSERT`, `DELETE` including the whole-partition form, and `UPDATE` of the map
column as a whole-document replace), the lookup join, partial aggregates, `DISTINCT`, the scalar
functions and the diagnostics surface are complete and covered. What remains below is not started.

**The Cosmos functions are now nameable through a connection**, a `CosmosSchema` and a
`CosmosAccountSchema` declaring them where the catalog reader looks — so full text search is no
longer the one feature a supported host could not use, and `CosmosConnectionFunctionTests` is the
proof, against a real `DbConnection` over a model document. Chaining `CosmosOperators.Instance` is
optional now rather than required, and is still the route for a host that assembles its own planner.
One thing did **not** come with it: `ORDER BY RANK` still does not survive a connection, for a reason
that is about plan shape rather than about names, and it is the first item under section 4.

**And a declared index now decides whether a full text or vector function pushes at all.**
`CosmosContainerMetadataReader` reads the container's full text and vector declarations — policy and
index both — and the translator refuses a call over a path named by neither, which is a plan-time
refusal that names the path in place of the service's bodyless 400. The second legality gate after
`IsSortSupported`; see `DESIGN.md` under *The declaration decides whether a full text or vector
function pushes*, and the open measurement under section 5.

**Declared columns were built and then dropped, and they are not coming back.** A caller-declared,
typed document path promoted to a real column — through a `columns` operand — would have given three
things a type to work with: a patchable `UPDATE` target, an argument the nullable-aggregate rewrite
could fire on, and a declared temporal representation. The decision against them has been taken more
than once and is recorded in section 7. The row model is the document column, `DOC`, and a query
works off that. What answers the dependency instead is the service's own type predicates —
`IS_NUMBER`, `IS_STRING`, `IS_DATETIME` and the rest — which say per row, at query time, what a
declaration could only promise; see *Rewriting a typed comparison into one the service can evaluate*
in section 4.

The fourth, **a sort key that can be non-nullable**, turned out not to need a declaration at all: a
query that removes the nulls itself settles the null placement, and the planner already carries
that fact. It is done for the promoted columns and out of reach for an unpromoted document path —
and *why* it is out of reach says more about the surface than the original argument did. See
section 7.

### Running the sample

```
dotnet run --project samples/Apache.Cosmos.Sample/Apache.Cosmos.Sample.csproj
```

It needs the Cosmos emulator on `localhost:8081` and prints the docker command if it is missing. It
seeds both sources and is safe to re-run. What it demonstrates is the lookup join across two adapters:
the CSV side's three product ids are pushed into Cosmos, so the container is filtered at the service
rather than read whole.

### Integration requirements, recorded in the user manual

Three things trip a host and fail with messages that name nothing useful: the calc rules must run as
a *pass* after the planner (`Programs.CALC_PROGRAM`'s shape — given to Volcano they do nothing), a
model must name `CosmosSchemaFactory` assembly-qualified with the assembly already loaded, and
`defaultNullCollation` defaults to the opposite of the service's null placement, so every sort on a
document path silently declines until the connection says `LOW`. The manual in `docs/` carries all
three with the reasoning — chapters 2, 9 and 21.

### Where to start

1. **Whether an RU estimate can be inferred at all** (section 1) — cheap to settle and it gates
   two things: the cost model in RU, and the toolbox model in `DESIGN.md`, since a *k*-way split
   currently scores *cheaper* than the statement it replaces and would be chosen for the wrong
   reason.

---

## 1. Statistics and the cost model

`getStatistic` reports keys from declared metadata and a row count read from the service; the cost
model is still inference over constants. Everything here is a read of something the service already
knows, or the model those reads deserve.

### Document size into the cost model — *small*

Average document size is already derived from the container's resource usage and not yet used. It is
what a row costs to move, which for a row model carrying whole documents dominates.

### Provisioned throughput — *small*

`ReadThroughputAsync`. Not needed to compare two plans, but it is the denominator that turns an RU
estimate into a latency estimate, and it distinguishes a container that can absorb a scan from one
that cannot.

### Feeding `RequestCharge` back — *large*

The measured charge is on the `cosmos.request_charge` histogram and the `cosmos.query` span. A
measured charge for a query shape is worth more than the estimate that was used to choose it, but a
cost model that learns needs somewhere to keep what it learnt. Statistics now have a lifetime and a
way for a caller to end it; what they still lack is a writer other than the service.

### Per-partition skew — *not available; recorded so nobody looks again*

Per-partition storage is an Azure Monitor metric, not data plane. The count is reachable and the
distribution is not, so a hot-partition estimate would have to come from outside the adapter.

### A rebuilt root leaks its client — *small*

The provider answered the question this entry used to ask. Since `Apache.Calcite.Data` 2.0.1-pre.267 a connection
draws on a `CalciteDataSource` shared by every connection with an equivalent connection string, so the model is read
and `CosmosSchemaFactory.create` runs once per data source rather than per connection; a host wanting the schema
object itself registers it with `CalciteDataSourceBuilder.AddSchema`. `CalciteConnection.RootSchema` is now read-only.
The manual (chapter 6) documents both; what a schema learns still hangs off the schema, and nothing here should grow a
process-wide cache, and none does: `CosmosFunctionBodies.Paths`, the last, is parsed per call since #243, and F's
default graphs and the rendering oracle are each schema's now (*S's state was the process's*, section 10).

What is left is disposal. A pooled data source is released after `ConnectionIdleLifetime` with no
open connection, and the next connection builds a new root — and with it, through `endpoint`, a new
`CosmosClient` — while the old client is never disposed. The provider disposes every `IDisposable`
schema on a released root, so `CosmosSchema` and `CosmosAccountSchema` can now release the client they
created. Only one they created: a client from `clientFactory` may be shared and is the factory's to
dispose.

A rebuilt root also starts the planner over: the schema's reasoning (`CosmosReasoning`) — its generations, every
compiled declaration and every statement's tree, its rendering graph and the answers it remembers — goes with the old
root, and the new schema compiles again. A schema added with `AddSchema` that implements `IDisposable` is disposed
each time a root holding it is released, `Clear()` included, though the same instance is added to the next root.
`CosmosSchema` is not disposable yet, but is to become so for the client above: disposal must then leave such a schema
usable, its reasoning above all, which is why `CosmosReasoning` holds nothing disposable.

### Statistics after pushdown — *large*

Flink collects connector statistics *after* partition pruning and filter pushdown, so the number the
planner sees describes the scan it will actually do rather than the whole table. Here that would mean
a row count for a partition-pinned scan rather than for the container — which is the difference
between costing a single-partition read and costing everything. It needs a statistic attached to a
`RelNode` rather than to a table, which is a larger change than it sounds.

### A cost model in RU — *large*

The above are inputs; this is the model. Cosmos charges in RUs and the current model multiplies
Calcite's abstract cost by constants. A model in RUs — a point read is 1, a query is 2.3 plus scanned
size, a cross-partition query is that times the fan-out — would make pushdown decisions comparable
with in-process alternatives on a real scale rather than a notional one.

### Can an RU estimate be inferred for a plan at all? — *investigate first, then implement or not*

The item above assumes a number can be produced *before* the query runs. That is not settled, and it
is the thing to settle first, because the rest is wasted if it cannot.

**What the service offers.** The charge, after the fact, which the adapter already records on
`cosmos.request_charge`. Query metrics in the diagnostics naming what the charge was made of —
retrieved document count and size, output count and size, index hit counts. Index metrics, already
reachable through the `indexMetrics` operand, saying which indexes a statement used. What does not
exist is a dry run: Cosmos has no `EXPLAIN` that prices a statement without executing it, so nothing
can be asked, only computed.

**So an estimate would be analytic over inputs the adapter already holds** — row count from
statistics, average document size (derived and still unused, per the item above), Calcite's own
selectivity for the pushed predicate, the projection width, and `PartitionKeyIsComplete` for the
fan-out multiplier. The coefficients are measurable rather than guessable: fit them against a matrix
of shapes on a real account, which is what `CosmosLookupRoutingMeasurementTests` and the *spelling is
not a price* table already do on a smaller scale.

**The bar is lower than it looks.** The planner ranks plans; it does not report a bill. An estimate
wrong by a constant factor but right in its ordering is worth as much as an accurate one, which
makes this far more tractable than predicting a charge.

**Two ways it fails, which is why the answer may be no.**

- *Comparability.* Cosmos nodes would cost in RU while the in-process side costs in Calcite's
  abstract units, and the conversion between them is itself a guess. A wrong conversion is worse
  than today's flat `CosmosConvention.CostMultiplier`, because it is wrong with confidence and at
  scale.
- *Account dependence.* If the coefficients move with indexing policy, document size distribution,
  or serverless versus provisioned, a fit taken on one account mispredicts on another — at which
  point *Feeding `RequestCharge` back* is the honest route, since it measures the account in hand
  rather than assuming one.

**What would settle it.** Not "how close is the estimate" but "does it order a set of real plan
alternatives the way the measured charges do". A disagreement on ranking is the only error that
costs anything, and the measurement is cheap given the harness that exists.

---

## 2. Execution paths

Queries execute through `GetItemQueryStreamIterator`, with a pinned `id` and complete partition key
recovered as a point read. The SDK's other cheap routes are unused.

### The document column through a point read — *small, needs a decision*

`SELECT *` or `DOC` by partition key and `id` runs as a query, routed to one partition, rather than as
the 1 RU read its predicate allows. It used to take the read and fail, reading `DOC` as `VARCHAR`
(review probe H4l). Serving it from the read is not a matter of reading it as JSON instead: measured
on the emulator, `ReadItem` returns the document in the order it was written, at another indentation
and with `_self`, where the query reorders it and leaves `_self` out; `ReadMany` differs from the
query in indentation. `DOC` is the service's bytes by design, so the read would make it a different
string depending on whether the statement pinned an `id`. Recovering the read means deciding `DOC` is
a JSON value — re-serialised to one canonical text on every route, with the system properties
settled — which changes what every `SELECT *` returns. Measure against a real account first: whether
a query there carries `_self` and the written order is the first question.

### A typed accessor through a point read — *small*

A pinned `id` and partition key whose projection includes `JSON_VALUE(… RETURNING INTEGER)`, or any
typed accessor with a type's test — not a date or a time, which stays bare, nor `DECIMAL`, which
addresses no path — runs as the routed query rather than as the read. The column is written
through its type's guard, `IS_NUMBER(p) ? p : null`, so that an object at the path reads null as
Calcite reads it; read out of the document, the same object failed the read as the integer the plan
declared (the typed container's `label`, by `id` and by a batch; `DESIGN.md` under *Not every
statement is a query*). Its rendering is no longer the path, so `DocumentRowBuilder` withdraws the
read, as it does for the text accessor. Keeping the read means applying the type's test to the read's
document before the reader sees the value — `CosmosFunctionBodies.IsNumber` and `IsBool` are those
tests in process — which the converter would have to be told per column, the rendering being Cosmos
SQL text it cannot evaluate.

### A read is priced where the converter withdraws it — *small*

`CosmosFilter.RequestUnits` prices a filter as a point read from its own condition, and the read is
withdrawn after planning wherever the statement rules it out: a computed or guarded column, the
document column, or a second filter conjoining into the `WHERE`. The rows are right either way; the
planner compares a route it will not take. Asking the walk whether the filter's input wrote a `WHERE`,
and whether the projection above reads every column as its path, would price what runs.

### The sample against SQL Server — *small*

Apache.Calcite 2.0.0-pre.3 fixed the ADO.NET adapter against SQL Server
([calcite-dotnet#24](https://github.com/ikvmnet/calcite-dotnet/issues/24)), so the sample's CSV side
can become the SQL Server it was meant to be — a one-line change, plus the SQL Server the sample
would then need running beside the emulator, which is the actual decision. Nothing in CI runs the
sample either way; it was last verified by hand against pre.3.

### Change feed — *large*

`GetChangeFeedIterator` is a fundamentally different read: ordered by `_ts` within a partition,
resumable, and the basis of every incremental pipeline built on Cosmos. It is not a table in the
relational sense — it has no end — so exposing it means deciding what it *is* to Calcite: a table
function taking a start time, a streaming source, or something a caller drives and the adapter only
materializes.

### Continuation tokens — *medium*

A query's continuation token makes a result resumable, and the adapter reads every page eagerly within
one enumeration. `GROUP BY` and `DISTINCT` results are documented as not resumable, which is a
constraint on where this can apply rather than a reason not to.

**Not for a string order, measured.** On the probe account, across two physical partitions, a
cross-partition `ORDER BY c.s` resumed page by page from each continuation token in a new iterator lost 3
to 5 of 28 rows, and at a page of 5 descending repeated four rows four times over: the token's
`resumeValues` are compared by code point at the service, where the merge had emitted by UTF-16 code unit.
No check of the rows that arrive can see a row that never does, so the reader of an anchored order
(`DESIGN.md`, *The anchored read*) reads every statement through one iterator from its start, and a
resumable cursor would have to keep to that, or resume only orders proved when the plan is made.

---

## 3. Writing

Writes are item CRUD behind a `TableModify` — Cosmos SQL has no DML, and does not need to for the
adapter to write. What each statement does and refuses is recorded in `DESIGN.md` under *Writing*.

### `UPDATE`, the patch tier — *medium, and the blocker is a way to write it, not a type*

`SET "DOC" = …` executes as a whole-document replace. What remains is the cheap tier: a targeted
`SET` of a plain document property as `PatchItemAsync`, sending changed properties rather than the
document. The execution ladder above it (static decomposition via a mutation operator, the diff and
blind-patch optimizations) is recorded in `DESIGN.md` under *Updating*.

**This entry used to say the tier was blocked on a typed column, and that was wrong.** A patch sends
whatever value it is handed, so no type is needed, and the path is written in the *statement* rather
than declared anywhere — a planner can see that a single-path `SET` is not a whole-document replace
without anything being declared. What is missing is a way to *write* the statement, and there are
three walls, each measured:

**The substrate is not the blocker.** `DOC` is `VARCHAR`, so Calcite's SQL/JSON family type-checks
over it: `SET "DOC" = JSON_SET(c."DOC", '$.data.name', 'x')` converts and plans, arriving as a
`CosmosTableModify(updateColumnList=[[DOC]])` over a calc holding the expression — the shape a patch
rule would match, intact.

**And there is no in-process fallback to be afraid of.** `TableModify` has no implementation in the
CLR conventions at all — *Missing conversion is LogicalTableModify\[convention: NONE ->
CLR_ASYNC_ENUMERABLE\]* — so an `UPDATE` whose expression the adapter declines fails to plan rather
than evaluating in memory and writing a re-serialised document back.

**Nor is there anything to render into a Cosmos query.** Measured against a real account: Cosmos has
no JSON-transform function under any of a dozen names, no object spread, no `ArrayToObject` to undo
`ObjectToArray`, and **no `UPDATE` statement** — *SC1001, syntax error near 'UPDATE'*. The targeted
write is the item API, `PatchItemAsync` with `set`/`add`/`replace`/`remove`/`incr`, ten operations to
a call. So a rule reads the path and the value out of the SQL expression and issues patch operations;
nothing is rendered.

**The shape chosen is the document column, `DOC`.** Typed `VARCHAR`, so the standard `JSON_SET`,
`JSON_REPLACE`, `JSON_INSERT` and `JSON_REMOVE` type-check against it — operators every tool already
knows, nothing new to name. The column is a handle rather than a representation: on the write path it
is never built, and projected it is handed over as the service returned it.

The alternative considered and not taken was **adapter-declared operators over a non-standard type** —
one family of functions nobody outside this adapter knows, where the JSON family is already in every
tool.

**Reads through `DOC` are worth more than they look, and that is the surprise.** The read side was
first written off here on the grounds that Calcite's SQL/JSON functions are string-typed, so pushing
`JSON_VALUE(c."DOC", '$.price')` down as the bare path `c.price` would hit the same wall as
projecting a cast to text — the service answering with a number where the plan declared text, which
`CosmosJson.GetString` refuses. That is wrong: SQL:2016's `RETURNING` clause is implemented, and
Calcite honours it. Measured:

```
JSON_VALUE(doc, '$.a')                       ->  VARCHAR(2000)
JSON_VALUE(doc, '$.a' RETURNING INTEGER)     ->  INTEGER
JSON_VALUE(doc, '$.a' RETURNING DOUBLE)      ->  DOUBLE
JSON_VALUE(doc, '$.a' RETURNING BOOLEAN)     ->  BOOLEAN
JSON_VALUE(doc, '$.a' RETURNING TIMESTAMP)   ->  TIMESTAMP(0)
JSON_VALUE(doc, '$.a' RETURNING INTEGER) + 1 ->  INTEGER
JSON_QUERY(doc, '$.a')                       ->  VARCHAR(2000)   genuinely text, a JSON fragment
JSON_EXISTS(doc, '$.a')                      ->  BOOLEAN
```

So the declared type matches what the service returns, and rendering the call as a path is sound.
The field comes out **nullable** even over a `NOT NULL` column, which is right — a path may be
absent. And the clause survives inside a view: a model view selecting
`JSON_VALUE(p."_etag", '$.a' RETURNING INTEGER) AS "N"` presents `N` to a `DbDataReader` as
`INTEGER`/`Int32`, over three rows.

**Which reaches past this entry.** A typed column over a document path is the surface section 7
rejects, and this is one written in standard SQL, in a view, with no operand and nothing declared to
the schema — which is the whole difference. It is still the caller's word — but a wrong word fails rather than lies: `RETURNING INTEGER`
over a path holding a string makes the service return a string where the plan declared an integer,
and `CosmosJson` refuses to coerce it, which is the opposite failure mode from the one section 7
declines an operand for. What it does **not** give is a `RexInputRef`: a `JSON_VALUE` call is an
expression like `ITEM`, so predicate flow, keys and distinctness stay where they are. Section 6's
split holds; this answers the typed half and not the reference half.

**Every pushdown resolves its path in one place.** A filter, a projection, a sort key, an aggregate
argument, an unnest array, the partition key extractor, and the full text and vector legality gates
all ask `CosmosRexTranslator.TryResolvePath` for a document path, and `WriteCall` renders the call as
that path. Measured through a connection: projection, filter, sort, sort with fetch, `GROUP BY`,
`DISTINCT`, a nested path, a bracketed name, an array subscript, a numeric comparison, `IS NOT NULL`,
`UNNEST` and the lookup join from either side all push, and a path assembled at run time is declined
for all of them at once.

The shape a view is made of took its own measurement: `CAST(… AS VARCHAR) = 'text'` over a
`JSON_VALUE` was not dropped, because the test was that the operand is typed `ANY` and Calcite types
the accessor `VARCHAR(2000)` (#71). The cast is now dropped over it too, on a measurement of
Calcite's own runtime that the accessor renders what the cast over `ANY` renders and applies no
width. What is deliberately *not* carried over is the same cast in a projection: `JSON_VALUE` answers
null for an object or an array where the reader renders one, so a view's text columns stay in
process. Recorded in `DESIGN.md` under *Casts over document values*.

**The bare accessor is a conversion, and the measurement above compared plans rather than rows.** Measured
against Calcite's runtime, `JSON_VALUE(doc, '$.x') = '30'` keeps the document storing the *number*
30, because SQL:2016 casts the scalar to the returning type and the default is a character string;
pushed as `c.x = '30'` it did not. The adapter behaves like Calcite, so the equality is now held to
the cast form's literal test: unambiguous text pushes, and anything else is declined and the split
rule pushes what it implies — `c.x = '30' OR c.x = 30`, the string or the number, under the
comparison Calcite makes. The same disjunction serves the cast form. *Settled by measurement:*
`RETURNING` a non-text type converts nothing in Calcite — it asserts the Java class and throws on
disagreement — so a comparison through one pushes exactly,
as it did, and needs no bound; `DESIGN.md` records the measurement. **Except `DECIMAL`, which that
measurement never put a string under:** it parses, and is now read as the cast it is (below).
*Settled by the same measurement:* the
other operators over the bare text accessor — `<>`, the ordering comparisons and `LIKE` — diverged in
both directions over the `typed` container, and are now declined and weakened to the case the two
agree on, `NOT IS_STRING(x) OR <comparison>`. The string case is exact, the rendering being the
value; every other document reaches the recheck. The same holds over a *view's* column bound to the
accessor, which is the spelling every typed caller writes and which had been pushed raw (#83);
`DESIGN.md` records it under *Projecting a cast to text*. `ORDER BY` was measured alongside them and
did not diverge, so nothing was done to it — which is a statement about that corpus rather than a
proof, and a sort still has no weakening to fall back on if one is found.

*Settled by the soundness review's probes:* the operators *beneath* a comparison, and the null test.
`UPPER(label) = 'TRUE'`, a concatenation, a length and a `CASE` arm each read the rendering as a value,
and a string function is undefined at the service over the number the accessor renders as text; they
are declined unless the container declares the path a string. `IS NULL` and a negated equality take the
accessor's null set rather than the path's. `DESIGN.md` records both under *Expression translation*.
Still open from the same probes, and outside the translator: a point read and a batch of them ignore
`DISTINCT` (H); the lookup join compares a key's rendering against the raw value (I); `SELECT DISTINCT`
binds its keys to paths where the aggregate binds null (J); and an `UNNEST` probe under a lookup join
fails at implementation (K).

`<>` over the accessor against unambiguous text could now push exactly, guarded by
`IS_PRIMITIVE` in the position that needs a guard. It is declined and weakened as before; pushing it
is an addition the corpus would have to show agrees, not part of the repair.

*Settled by the review of those fixes,* which swept their neighbours on the fixed branch and on `main`:
a comparison read as a value rather than for its truth (projected, grouped by, or the operand of another
comparison) is declined where Calcite types it nullable; a cast to text with a width is no longer taken
as the rendering, by the comparison or by the fact and partition-key extractors; a rendering inside a
`VARIANT` box is seen through by the comparison, `LIKE` and the null test; a grouping key or `DISTINCT`
column read as text pushes only over a declared string; `+ - * %` in a decimal fraction is declined; a
text `JSON_VALUE` is no longer a document an accessor addresses through; and a field bound to a typed
accessor carries its type test. `DESIGN.md` records each. What that leaves, each a recovery rather than
a repair:

- **Grouping by text over a path nothing declares** — *medium*. Section 4 carries it, beside the sort.
- **`COALESCE(JSON_VALUE(x), 'lit') = 'unambiguous'`** — *small*. The equality never reaches the case
  where the fallback and a rendering could be confused, so with the accessor's null test it could push
  exactly. Corpus cases over each JSON type in `typed` first.
- **A comparison read as a value** — *small*. An ordering against a constant measured right — the
  service's ordering across types is undefined over a null and reads back null — and the others could
  be written under a ternary on their operands' null sets. Neither is worth a second rule until a
  projected comparison is a shape someone writes.
- **Integer overflow** — *a decision, recorded*. Pushed, and computed rather than wrapped; see
  *Verified against the emulator* in `DESIGN.md`. A declared numeric range could prove an integer sum
  fits, and let the adapter decline only the arithmetic that might not.

*Settled after that review,* by the review of the predicate oracle's gate: `RETURNING DECIMAL` parses a
stored string — `"2"`, `" 2 "` and `"2.0"` are each 2 to it — where every other numeric and boolean
`RETURNING` asserts its class (`CalciteJsonValueMeasurementTests`). It was written as the path, losing
the strings at the service, and read by the fact extractor as the path holding the number, which over a
declared string rewrote the filter to `FALSE`. It now addresses no path, and a comparison through it
pushes the window a cast to a number pushes. The corpus gained a declared container and twenty
statements, fourteen of which answered differently before. What that leaves:

- **`RETURNING DECIMAL` over a path declared an integer** — *small*. The parse of a whole number is the
  number, and the service keeps a stored integer exact at any size (*Verified against the emulator*), so
  over a declaration that admits nothing else the accessor is the value at the path and could be written
  as one: a column, a sort key, a grouping key, an exact comparison. A path declared `number` is not
  enough — a stored `0.1` parses to the double's binary expansion, which `= 0.1` under `DECIMAL(10, 1)`
  is false of, and the projected value carries it. The gate needs the facts, which `IsJsonAccessor` does
  not have, so it is a reading beside the refusal rather than a hole in it. Measured over `declared`'s
  `n` first. The same reading would make the clause a second spelling of the numeric-string lowering
  (`CosmosFactRewriter.NumericAccessorOf`), which reads only the cast.
- **`<>`, a negated equality, `IS NOT NULL`, and a literal with more digits than the clause** —
  *small*. They pushed nothing, not even the `IS_DEFINED` every comparison implies, because
  `CosmosFilterSplitRule.CollectPaths` found no path in an accessor that addresses none. *Since the switch*
  S reads the clause: `<> 2` and `NOT (= 2)` send the path's presence and `IS NOT NULL` is exact
  (differential 323–325), and the walk went with J (g18). The last, `RETURNING DECIMAL(10, 2) = 2.255`,
  where Calcite casts the value up to `DECIMAL(11, 3)`, is the shape of §10's *A typed reading compared
  with a fraction keeps no presence*.

*Settled after that,* by the review of the fix: the window was wrong past 2^53, and so was every exact
literal. The service reads a number sent with a point as a double, and keeps a stored integer exact, so a
bound loosened by one around 2^60 came back as 2^60 and `RETURNING BIGINT = 2^60 + 1` matched 2^60.
`CosmosRexTranslator.TryGetExactNumberValue` now sends a whole number a double does not hold as a `long`,
and a cast to `DOUBLE` reads no window from 2^52, where a stored integer and the literal each round by
half a unit. A literal cast down to a `DECIMAL` its whole digits do not fit raises in Calcite on every row,
and no window is read from it, where one answered nothing and a declared maximum rewrote it to `FALSE`.
`declared` gained `v` and sixteen statements, ten of which answered differently before. What that leaves:

- **A double's window past 2^52** — *small*. It states nothing there, which is sound. Loosening each side
  by the double's unit in the last place at the bound, rather than by one, would recover it.
- **A number no value serves** — *small, and rare*. A literal a hair from a whole number,
  `2.9999999999999999`, or a whole number beyond a `long` the double does not hold, is declined, and a
  bound of one is not stated; a parameter's value of one fails the execution. Sending the nearest double
  on the side the comparison allows would serve a bound, though not an equality.

Two things the measurement settled that are worth keeping. `UNNEST` needs `RETURNING <type> ARRAY`,
which is what names an array type — **and either accessor may carry it.** This entry used to say
`JSON_QUERY` is `VARCHAR(2000)` whatever it is asked for and can never be an unnest source; that was
wrong, and wrong about the accessor that actually works. A wrapper clause leaves it `VARCHAR`, but a
`RETURNING` does not, and measured, `JSON_QUERY(…, '$.v' RETURNING VARCHAR ARRAY)` answers
`string[2]{a,b}` where `JSON_VALUE`'s answers null. Pushed down the two are indistinguishable — every
spelling renders `JOIN t0 IN c.tags`, the adapter reading the path rather than the function — so the
correction costs nothing here and matters entirely to a plan that does not push. And the accepted path grammar is `$` followed by `.name`, `['name']` and `[0]` steps: a
wildcard, a descent or a filter is refused rather than approximated, and the path argument must be a
literal for the reason the full text functions' first argument must be.

**The same spelling projected meant something else, and that was a bug rather than a limit (#119).**
Every `JSON_VALUE` was rendered as the bare accessor's guard, `IIF(IS_PRIMITIVE(p), p, null)`, and
read as text — whatever the `RETURNING` clause said. `IS_PRIMITIVE` is false of an array, so the
array a traversal read elements out of was null as a column; and a scalar `RETURNING` carried the text
reading into a column the plan had declared a number, which the row builder could not hand over at
all. The guard and the reading now follow the accessor and its declared type: `IS_PRIMITIVE` and text
for the bare accessor it was written for, the bare path and the declared type for a scalar
`RETURNING`, and — on `JSON_QUERY` — `IS_ARRAY` and a `java.util.List` for an array one. An array
`RETURNING` on `JSON_VALUE` is refused outright rather than rendered, which reverses what #119 first
shipped; the paragraph below says why. A collection is read to its element type as well, so
`INTEGER ARRAY` holds `Integer` rather than the `Long` a schemaless read discovers.
`DESIGN.md` records it under *Projecting a cast to text*. **`JSON_QUERY`, the mirror, is handled too
now** — it was wrong in the way this was and in the other direction besides: the bare path was sent
and read as the declared `VARCHAR`, so the object or array the function exists to return was refused
by `CosmosJson.GetString` while a scalar came back as itself, where SQL/JSON says null. It renders
guarded by the complement of the scalar guard, `IS_OBJECT(p) OR IS_ARRAY(p)`, and reads as
`CosmosReading.JsonText`.

**And the array `RETURNING` on `JSON_VALUE` is refused rather than rendered — a reversal of what #119
shipped, on the same measurement read differently.** Measured at `JsonFunctions.jsonValue` itself,
with no plan, no code generation and no reader in the way, the extraction is scalar-only: an array
`RETURNING` answers null over an array and throws over a scalar, while the validator admits the array
type and `UNNEST` consumes it. #119 read those as defects and rendered the array at the service. What
that missed is that SQL restricts the clause to a predefined scalar type and gives `JSON_QUERY` for
structure, so there is no construct here to be faithful to — only a spelling Calcite accepts. Giving
it a meaning invents one, and invents it *only here*, so a query moved off this adapter silently
returns different rows; and under `ERROR ON ERROR` the engine raises, which a pushed column cannot
reproduce at all, so rendering could only ever have been right for one of the two clauses. The column
is left in process, where a caller gets null or the raised failure exactly as the engine gives them.
[CALCITE-6208](https://issues.apache.org/jira/browse/CALCITE-6208) — which tunes element nullability
for exactly `unnest(json_value(col, '$.c' returning bigint array))` — is the strongest case the other
way, and `DESIGN.md` answers it: a JIRA touching an example is not a construct, and the measurement is
against a release that already has that fix. **Nothing upstream is filed for the extraction itself**,
and filing it is the owner's call.

**The traversal goes with it, and that is what makes the refusal mean anything.** A first pass
refused the column and left `UNNEST(JSON_VALUE(…, '$.tags' RETURNING VARCHAR ARRAY))` resolving to
the path — measured, still `CosmosUnnest` over the scan — which moved the divergence rather than
removing it: in process the null array unnests to *no rows*, which
`CalciteJsonValueArrayMeasurementTests` pins, so the adapter answered rows the engine does not. The
refusal therefore lives in `IsJsonAccessor`, the one gate a projection, a filter, a partition key and
a traversal all pass through, and the spelling addresses no path in any clause. The cost is named
rather than hidden: a caller who wrote it and got rows now gets none, because that is what the
statement means. `UNNEST(JSON_QUERY(… RETURNING VARCHAR ARRAY))` agrees pushed and in process and is
where such a caller should be pointed.

**What is left is the patch tier itself** — the rule matching a `JSON_SET`, `JSON_REPLACE`,
`JSON_INSERT` or `JSON_REMOVE` call over `DOC` in a `TableModify`, a `PatchItemAsync` on the
writer, the routing in `CosmosCursors`, and the refusal of every form that cannot be rendered. The
column and the reads are in; the write is not.

### Whole-partition `DELETE` — *built, and unverified on the path it exists for*

A predicate pinning exactly the complete partition key plans as
`DeleteAllItemsByPartitionKeyStreamAsync` — one request, no query — with a probed account
capability deciding which way the rule goes, and `COUNT(*)` first for the affected count. The
design is in `DESIGN.md` under *Deleting a whole partition*; the fallback is exercised, and the
fast path is not, because no account this repository can reach will run it.

**The gate is a support request, not a switch.** The capability is an account capability —
`az cosmosdb update --capabilities DeleteAllItemsByPartitionKey` — not a subscription preview
registration, which is what made it look portal-only the first time it was measured. Set on a fresh
account and reported back by `az cosmosdb show`, the operation still answers 400:

> Partition key delete feature is disabled for this account. Please contact Azure Support to enable
> it.

What remains, therefore, is a measurement on an account somebody has had enabled: that the fast
path fires, what it costs against the per-document loop, and one documented hazard worth confirming
— an index-using `COUNT` issued *during* an ongoing delete may still count the documents being
removed, which decides whether the reported count can be trusted. Hierarchical partition keys are
documented as unsupported, which the recovery condition already required.

### Transactional batch — *medium*

`TransactionalBatch` is atomic within a single partition key. That is a real transactional guarantee
Calcite has no way to ask for, so exposing it means a session-level or hint-level surface rather than
SQL.

### Bulk mode — *small*

`CosmosClientOptions.AllowBulkExecution` changes the throughput profile of many small writes
dramatically. A client factory can already set it; whether the adapter should is a question about who
owns the client.

---

## 4. Query language coverage

### Ranking and search

- **`ORDER BY RANK` does not survive a connection** — *medium, and it is a shape rather than a
  translation, and it is [#46](https://github.com/ikvmnet/calcite-cosmos/issues/46).* The functions
  are nameable from a `CalciteConnection` now, and the predicates push;
  the rank clause does not, because `CosmosRankRule` matches `Project(Sort(Project))` and a
  connection never presents that. `Prepare` plans `RelRoot.rel` and applies the root's field mapping
  by wrapping the finished plan in a calc, so the projection that discards the score is built after
  every rule has run. Measured both ways: the same statement planned from `RelRoot.project()` gives
  `CosmosRank`, and from `RelRoot.rel` gives an in-process sort over a projected `FULLTEXTSCORE` that
  then fails to implement. `CosmosConnectionFunctionTests.AScoreResolvesThroughAConnection` detects
  it and reports inconclusive, telling it apart from the emulator's own refusal.

  **Decided, and what is left is nothing.** Matching `Sort(Project)` alone was the candidate, and it
  is refused by measurement rather than by argument: `SELECT id ... ORDER BY <score>` and
  `SELECT id, <score> AS s ... ORDER BY s` reach the planner as the same plan, node for node and
  expression for expression, differing only in the name of the output column. A rule accepting the
  first would accept the second, whose score the service will not produce — a wrong answer where
  there is now an error. The fact that separates them is `RelRoot.fields`, which `Prepare` applies
  after planning, and **the prepare path is not changing**: it is `CalcitePrepareImpl`'s behaviour
  faithfully ported, and one adapter's rule is not a reason to diverge from it
  (`ikvmnet/calcite-dotnet#80`, closed on that).

  So `ORDER BY RANK` needs a planner the host assembles, and that is a limit rather than a gap. What
  did change is the refusal: a scoring function reaching code generation now says that Cosmos never
  returns a relevance score, in place of Calcite's `must implement ImplementableFunction`.

- **Price a path in the full text policy but not in the index as the scan it is** — *small; measure
  first.* The declaration gate was a refusal and is now a cost (#85), and the cost reads the union of
  the policy and the index because `CosmosContainerMetadataReader` stores one list. A path in the
  policy and not in the index runs — measured — and most likely scans; pricing it as one is one more
  list on the metadata and one more read, and the measurement that decides it is an index-metrics
  read over such a container.

- **A full text call the translator still declines fails in process, not while planning** — *small,
  and a shape rather than a translation.* The one remaining refusal is a first argument that is not
  a path. The split rule pushes the definedness the call implies and leaves the call above the scan,
  where a schema function has no body, so the caller sees `FULLTEXTCONTAINS is evaluated by the
  service and has no in-process body` at execution. A planning-time refusal needs the rule to decline
  the split where the residual carries a bodyless Cosmos function, so that the whole filter — and its
  message — surfaces at once; recorded here rather than built, since the gate that used to produce
  most of these is gone.

### Geography

The translations, the refusal and the path pushdown are in place, and every form they emit has been
executed against an account. What is left is one sort that could push and does not, one operator that
is not offered, and one thing that cannot be fixed here at all.

- **Selecting a shape materializes** — *built, by #149, and the gap is worth remembering.* Everything
  above was about reaching an *operator*, and nothing had asked what happens when the geography is
  the answer rather than an argument. `CosmosJson.GetValue` had no `GEOMETRY` case, so a projected
  shape planned, rendered, executed and raised at the first row. Latent since the operators went in
  and reachable once a projection pushed (#145). The reading is
  `GeographyFunctions.FromGeoJson`, and the projection now sends the path under the guard
  `JSON_QUERY` carries rather than bare — a scalar at the path being the one thing the two readings
  disagreed about. `CalciteGeographyReadingMeasurementTests` is the measurement.

  **`CLR_ST_GEOG_ASGEOJSON` has the same divergence and still has it** — *small.* It projects the
  bare path and reads it as JSON text, so over a *scalar* at the path it answers that scalar's text
  where the in-process expression answers null, `JSON_QUERY` being null for a scalar. The same guard
  closes it. Left out of #149 deliberately: it is a different column type and a different reading,
  and folding it in would have made that fix two.

- **`ORDER BY` over a distance now pushes** — *built; one shape left to verify.* A distance-ordered
  query used to read every matching document and sort in process. `CosmosSort` now writes the
  expression into the clause — twice over, once selected and once ordered, because Cosmos cannot order
  by a projection alias — and `CosmosSortRule` admits the sort when the projection beneath it is a
  geodesic distance.

  Narrow on purpose. Only `CLR_ST_GEOG_DISTANCE` qualifies: the service accepts it in the clause and
  refuses `DateTimeToTicks` and `IIF` with 400, error 2206, so `CosmosProject` records that one
  expression and nothing else. And it is the whole collation or none — a second key beside it draws
  the same 2206 — which is narrower than the multi-key sort the composite-index path handles.

  **What is not verified is the shape a connection presents.** The node and the rule are covered by
  `CosmosSortTests.Implement`, which builds `Sort(Project(scan))` directly. A connection wraps the
  finished plan in a calc, which is what strands `ORDER BY RANK` in
  [#46](https://github.com/ikvmnet/calcite-cosmos/issues/46) — that case cannot project its score, and
  this one can, so the outer projection should merely drop a column that the statement still carries.
  Should. Plan the same statement from a `CalciteConnection` and see.
- **A geography parameter binds as GeoJSON** — *built, by #154, and it is what makes a parameterised
  proximity query possible at all.* The value a data context hands over is a JTS geometry, and a JTS
  geometry is not a document value: bound as itself the SDK's serializer wrote its object graph and
  the service got that in place of a shape. `CosmosJson.ToGeoJsonValue` shapes it, dropping the `crs`
  member the writer adds, so a parameter and an inlined constant say the same thing.

  **A projected parameter is still sent to the service and echoed back** — *small, and it is the
  shape #154 was reported through.* `SELECT VALUE { "$f2": @p0 }` asks the service to return a value
  the client already has; for a geography it also round-trips the instance through GeoJSON, which
  keeps the shape and not the object. Declining to push a projection that is *only* a parameter or a
  literal would remove both, and nothing needs the value to have travelled. Left out of #154 because
  it changes which plans are chosen and the fix does not need it.
- **`ST_ISVALIDDETAILED` is not offered** — *small.* The one Cosmos spatial function with no
  counterpart in the geography package, and rightly so: it is the service's own rather than a geodesic
  operation anyone else has. It belongs in `CosmosOperators` beside the full text functions, which is
  where this adapter's own operators live. It answers with a document rather than a boolean, so what
  it is typed as wants deciding first.
- **`CLR_ST_GEOG_X` and `CLR_ST_GEOG_Y` are not pushed** — *not available; recorded so nobody looks again.*
  They look like `c.location.coordinates[0]` and `[1]` and are only that for a `Point`. Nothing
  declares a path's shape, and over a `Polygon` the service would return a ring array where the
  in-process answer throws — a wrong answer in place of an error, which is the trade this adapter
  refuses everywhere else.
- **A pushed predicate cannot be rechecked in process** — *blocked upstream, and now for a measured
  reason.* `CosmosFilterSplitRule` pushes a weakened predicate and rechecks the original above, which
  needs an in-process answer that agrees with the service. **It does not agree.** `ST_DISTANCE` beside
  `GeographyFunctions.Distance`, same pairs, one account:

  | pair | service | in process | relative |
  | --- | --- | --- | --- |
  | equator, 1° east | 111319.490736 | 111195.101177 | 1.1e-3 |
  | equator, 1° north | 110574.388493 | 111195.101177 | 5.6e-3 |
  | 47°N, short hop | 1342.143313 | 1341.006922 | 8.5e-4 |
  | 80°N, 1° east | 19393.246802 | 19308.589000 | 4.4e-3 |
  | antimeridian | 21927.872478 | 21901.159212 | 1.2e-3 |
  | near the pole | 22338.795683 | 22239.020235 | 4.5e-3 |
  | continental | 1544278.966766 | 1545986.824436 | 1.1e-3 |

  The numbers say what the difference *is* rather than that there is one. 111195.101177 is one degree
  on a sphere of mean radius 6371008.8; 111319.490736 is one degree on the WGS84 ellipsoid at the
  equator. **The service is ellipsoidal and the package is spherical**, and the gap reaches 0.56%. A
  recheck would discard rows whose true distance sits within half a percent of the threshold, which is
  exactly the failure the gate exists to prevent. Nothing here fixes it — the evaluator would have to
  answer on the ellipsoid, which is `Apache.Calcite.Geography`'s to change.

  What the same run settles is the boundary: the service's comparison is exact, `<=` matching at the
  distance it reports and `<` not, so rendering `CLR_ST_GEOG_DWITHIN` as `<=` is right rather than an
  inference from PostGIS.
- **A mixed expression is not refused** — *not available; recorded so nobody looks again.* There is no
  `GEOGRAPHY` type — a geography and a geometry are the same type carried by the same class — so
  `CLR_ST_GEOG_DISTANCE(ST_BUFFER(g, 0.1), h)` buffers in degrees, measures in metres, and both halves run.
  Nothing in this adapter can see the difference, and the type that would show it cannot exist while
  `SqlTypeName` is closed. See `DESIGN.md`.
- **A geometry cannot be an `ORDER BY` key at the service** — *not available; recorded so nobody looks
  again.* Cosmos orders by a document path and a geometry is not an orderable value, so a query asking
  for one sorts in process over whatever the scan returns.

### Subqueries

- **`EXISTS` over an item-scoped subquery** — *large.* `EXISTS (SELECT VALUE t FROM t IN c.tags WHERE …)`
  is a semi-join over a nested array. Today the only route to a nested array is `Unnest`, which
  cross-products the document with it and de-duplicates above — the wrong shape and the wrong cost for
  an existence test.
- **Scalar and multi-value subqueries** — *medium.* Item-scoped only; there are no derived tables. The
  correlated forms are what `ARRAY(SELECT …)` and `IN (SELECT …)` need.

### Scalar functions still to map

- **Temporal is what is left**, and its blocker is a declared representation rather than a
  translation — see below.
- **A cast of a numeric literal renders**, which is what lets a comparison against a function
  returning a double push: `VECTORDISTANCE(…) < 0.5` coerces the literal and arrives as
  `CAST(0.5):DOUBLE`, and declining the cast declined the predicate. A cast over a *document
  value* is refused as it always was — see `DESIGN.md` under *Casts over document values*, whose
  argument this does not touch.
- **`ENDSWITH` and `CONTAINS` for the other two `LIKE` shapes** — *small, and might be nothing.*
  `LIKE 'abc%'` is already rewritten into `STARTSWITH`; `LIKE '%abc'` has the same exact counterpart
  in `ENDSWITH`, and `LIKE '%abc%'` in Cosmos's own `CONTAINS(str, substr [, ignoreCase])`, which is
  genuine substring matching and is also what BigQuery's `CONTAINS_SUBSTR` wants. **Measure first.**
  Both forms already render as Cosmos `LIKE` and the service already evaluates them, so the only
  question is whether the named function is priced differently — and the `IN`/`BETWEEN` measurement
  below is the standing warning that a native spelling often is not. The case-*insensitive* forms are
  a different question and are done: `UPPER(x) LIKE '%abc%'` has no `LIKE` spelling at the service
  and renders as `CONTAINS(x, 'ABC', true)` — `DESIGN.md` under *A case fold under `LIKE`*, which
  also records the ASCII line the rewrite stops at and why.
  Two things measured while writing this down. SQL's own `CONTAINS` is **not** a candidate: it is the
  period operator, and `c."id" CONTAINS 'steel'` fails to validate, so there is no user-facing query
  to accelerate — `CONTAINS_SUBSTR` from the BigQuery library and `LIKE '%…%'` are the reachable
  spellings. And Calcite's one full text precedent is a loose end rather than a pattern to copy: the
  Elasticsearch adapter maps `SqlStdOperatorTable.CONTAINS` onto an ES `match` query
  ([CALCITE-3437](https://issues.apache.org/jira/browse/CALCITE-3437), 1.22.0) — the period operator,
  reused for text — with no tests, no mention in that adapter's documentation, and, per the above, no
  way to reach it from SQL.
- **Currently declined, admissible with work** — `SUBSTRING` without a length (`LENGTH(s)` supplies
  it); `LIKE` with `ESCAPE`, and a bracket-escaping rewrite that would lift the bracket-pattern
  decline (Cosmos `LIKE` reads `[…]` as a character range where SQL does not — measured, and why
  bracket and computed patterns are refused); `TRIM` of a non-space character and `TRUNCATE` to
  decimal places, both needing Cosmos's two-argument arity **verified** first; `IS DISTINCT FROM`,
  expressible with the `??` operator once the null-versus-undefined semantics are measured. `IS TRUE`
  and its family are written over a stored boolean since #181 — `DESIGN.md` under *A cast to a boolean
  is the stored boolean* — and remain declined over anything else, an arbitrary expression's undefined
  arising where a test written at the path cannot see it. `JSON_VALUE(…, RETURNING BOOLEAN)` is the
  nearest candidate to take next: it asserts a boolean rather than parsing one, so it needs no
  declaration to be the stored value, and nothing has asked.

#### The whole surface, enumerated

The entries above are the ones that came up one at a time. What follows is the same question asked
*systematically*, because a list assembled from whatever a query happened to decline is not a list of
what is missing.

**How it was made, and what each half is worth.** Three inputs, crossed.

1. **Every operator Calcite offers**, read off `SqlStdOperatorTable` and every `SqlLibrary` table —
   715 of them, of which 474 are scalar functions and 70 are aggregates. Enumerated rather than
   recalled.
2. **What the translator does with each**, by building a `CosmosRexTranslator` over a document column
   and asking it to render a call. This half is *measured*: a row saying something does not push was
   watched not to push, and it caught several beliefs that were wrong in both directions — see the
   paragraph below.
3. **What Cosmos offers**, from the published function list (`learn.microsoft.com`, revised
   2025-11-10). This half is **not measured against an account**, and nothing below should be built
   without measuring the Cosmos side first. The standing warning is the `IN`/`BETWEEN` result under
   *Clause-level*: a native spelling that looks cheaper often is not.

**Asking mechanically corrected four things.** `LTRIM`, `RTRIM`, `INSTR`, `STRPOS`, `LEN`, `SUBSTR`,
`IF`, `IFNULL`, `NVL`, `NVL2`, `GREATEST` and `LEAST` all push already, by kind or by a convertlet
that rewrites them before the translator sees them — they look missing in the source and are not.
`ROUND` pushes at one argument and not two, which is Cosmos's arity rather than an omission. The
numeric probes first ran over `CAST(JSON_VALUE(…) AS INTEGER)` and declined *for the cast*, which is
section 6's subject and not the function's — every row below was re-run over an accessor that renders
on its own. And `ARRAY_CONCAT`, `ARRAY_UNION` and `ARRAY_INTERSECT` decline against an array
**literal** while pushing against two paths, which is a gap in one rendering rather than three
functions.

**Renames — a row in `DirectFunctions` and nothing else.** *Small, each.* The Cosmos function takes
the same arguments in the same order and means the same thing; what is missing is the entry.

| Calcite | Cosmos | note |
| --- | --- | --- |
| `POW` | `POWER` | an alias of `POWER`, which is already mapped |
| `TRUNC` | `TRUNC` | the table carries `TRUNCATE` and not this spelling |
| `STARTS_WITH`, `STARTSWITH` | `STARTSWITH` | reachable today only by writing `LIKE 'a%'` |
| `ENDS_WITH`, `ENDSWITH` | `ENDSWITH` | and see the `LIKE` entry above, which is the same function by another road |
| `ARRAY_LENGTH`, `ARRAY_SIZE` | `ARRAY_LENGTH` | `CARDINALITY` already maps; these are the library spellings of it |
| `ARRAY_CONTAINS` | `ARRAY_CONTAINS` | `MEMBER OF` already maps; this is the same function with the operands the other way round |

**A shape rather than a rename.** *Small to medium.* The Cosmos function exists and the call has to be
rewritten rather than renamed.

| Calcite | Cosmos | what has to happen |
| --- | --- | --- |
| `CONTAINS_SUBSTR` | `CONTAINS(s, t [, ignoreCase])` | already the target of the `LIKE '%…%'` rewrite; measure before adding a second road to it |
| `SPLIT`, `STRING_TO_ARRAY` | `STRINGSPLIT` | argument order to check |
| `ARRAY_JOIN`, `ARRAY_TO_STRING` | `STRINGJOIN` | the three-argument form (a null replacement) has no Cosmos counterpart |
| `REGEXP_CONTAINS` | `REGEXMATCH` | already mapped under its Cosmos name; this is the portable spelling of it, and it inherits the dialect caveat recorded there |
| `REGEXP_EXTRACT`, `REGEXP_EXTRACT_ALL` | `REGEXEXTRACT`, `REGEXEXTRACTALL` | same dialect caveat: Cosmos documents PCRE with constructs it does not support |
| `REGEXP_REPLACE` | `REGEXREPLACE`, `REGEXREPLACEALL` | Cosmos splits first-match and all-matches into two functions, so the occurrence argument decides which name is written |
| `SUBSTRING_INDEX` | `SUBSTRINGBEFORE`, `SUBSTRINGAFTER`, `LASTSUBSTRINGBEFORE`, `LASTSUBSTRINGAFTER` | a positive or negative count picks the pair, and only counts of ±1 are expressible |
| `IS_INF`, `IS_NAN` | `IS_FINITE_NUMBER` | the negation covers `IS_INF` for a number; `IS_NAN` needs the type test beside it |
| `ARRAYS_OVERLAP` | `ARRAY_CONTAINS_ANY` | close, not identical — `ARRAY_CONTAINS_ANY` takes loose values rather than an array |
| `BITAND`, `BITOR`, `BITXOR`, `BITNOT`, `LEFTSHIFT`, `RIGHTSHIFT` | `INTBITAND`, `INTBITOR`, `INTBITXOR`, `INTBITNOT`, `INTBITLEFTSHIFT`, `INTBITRIGHTSHIFT` | Cosmos's are integer-only, so the operand type has to be known — which is section 6's question again |
| `RAND`, `RANDOM` | `RAND` | non-deterministic, so pushing it moves *where* the value is drawn; the planner's treatment of a dynamic function has to be checked before this is a win rather than a change |

**Gated on a declared stored shape, not on a translation.** *Large, and the machinery now exists.*
Cosmos's date functions read and write ISO-8601 **strings**; Calcite's read and write its own
`TIMESTAMP`. So each of these is a rewrite conditional on the path's declared shape, exactly as the
comparison and the sort already are — `CosmosTemporalForms` is where the condition lives and
`CosmosStoredForms.RenderDateTime` is what writes a literal back into the container's spelling.

| Calcite | Cosmos |
| --- | --- |
| `EXTRACT(<unit> FROM …)`, `DATE_PART`, `DATEPART`, `YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `SECOND`, `QUARTER`, `WEEK`, `DAYOFYEAR` | `DATETIMEPART` |
| `TIMESTAMPADD`, `DATE_ADD`, `DATETIME_ADD`, `DATEADD`, `TIMESTAMP_ADD`, and the `_SUB` forms | `DATETIMEADD` |
| `TIMESTAMPDIFF`, `DATE_DIFF`, `DATETIME_DIFF`, `DATEDIFF` | `DATETIMEDIFF` |
| `FLOOR(<ts> TO <unit>)`, `DATE_TRUNC`, `TIMESTAMP_TRUNC`, `DATETIME_TRUNC` | `DATETIMEBIN` |
| `UNIX_MILLIS`, `UNIX_SECONDS` | `DATETIMETOTIMESTAMP` |
| `TIMESTAMP_MILLIS`, `TIMESTAMP_SECONDS` | `TIMESTAMPTODATETIME` |
| `CURRENT_TIMESTAMP`, `LOCALTIMESTAMP`, `CURRENT_DATE`, `CURRENT_DATETIME` | `GETCURRENTDATETIME`, and the `STATIC` variants where a statement wants one reading for every row |
| `DATETIME(y, m, d, h, mi, s)` | `DATETIMEFROMPARTS` |

Two of these need nothing from section 6 and could go first. `GETCURRENTDATETIME` has no stored value
to interpret, and `_ts` is the one path whose encoding the service defines — the paragraph below says
so already and is the entry this table supersedes rather than repeats.

**Rewrites rather than mappings.** *Medium each, and the payoff is an aggregate that stops reading the
container.* Cosmos has five aggregates and Calcite has seventy; several of the rest are expressible in
the five.

| Calcite | as |
| --- | --- |
| `COUNTIF(p)` | `SUM(IIF(p, 1, 0))` |
| `EVERY`, `BOOL_AND`, `LOGICAL_AND` | `MIN(IIF(p, 1, 0)) = 1` |
| `SOME`, `BOOL_OR`, `LOGICAL_OR` | `MAX(IIF(p, 1, 0)) = 1` |
| `VAR_POP`, `VAR_SAMP`, `STDDEV_POP`, `STDDEV_SAMP` | `SUM(x)`, `SUM(x * x)` and `COUNT(x)`, combined above the scan — the shape Calcite's own `AggregateReduceFunctionsRule` already produces |
| `ANY_VALUE` | `MIN` or `MAX`, which is a legal choice of *some* value |

`ARRAY_AGG`, `COLLECT`, `LISTAGG` and `STRING_AGG` are **not** in this list: Cosmos's published
aggregate set is `AVG`, `COUNT`, `MAX`, `MIN` and `SUM`, and nothing there accumulates a list.

**Not a function, and it blocks a family.** *Small, and it is the cheapest thing here.* An **array
literal** does not render — measured, `ARRAY['x','y']` declines — so `ARRAY_CONCAT(<path>, ARRAY[…])`,
`SETINTERSECT`, `SETUNION` and an `ARRAY_CONTAINS` against a constant set all decline for the operand
rather than for themselves. Cosmos writes one as JSON, `["x","y"]`, so this is a rendering and a null
question and nothing more.

**Cosmos has it and Calcite has no spelling for it**, so reaching these means declaring an operator in
`CosmosOperators` the way `IS_DEFINED` and `ToString` already are — a feature rather than a pushdown,
and listed so the asymmetry is visible: `IS_DATETIME`, `IS_INTEGER`, `STRINGTONULL`, `STRINGEQUALS`
(with its case-insensitivity flag), `DOCUMENTID`, `CHOOSE`, `NUMBERBIN`, `SQUARE`, `ARRAY_CONTAINS_ALL`,
`ST_ISVALIDDETAILED`, `GETCURRENTTICKS`, `DATETIMETOTICKS`, `TICKSTODATETIME`. **`ST_AREA` belongs
here rather than among the renames**, which is the trap the geography package exists to avoid:
Calcite's `ST_AREA` is planar and the service's is geodesic, so they are different measures under
one name, and the package declares no `CLR_ST_GEOG_AREA` to carry the geodesic one yet.

**No Cosmos form, recorded so nobody looks again.** Every one of these is a scalar function Calcite
offers and the service does not: `LPAD`, `RPAD`, `INITCAP`, `SPACE`, `ASCII`, `CHR`, `TRANSLATE3`,
`SOUNDEX`, `DIFFERENCE`, `LEVENSHTEIN`, `STRCMP`, `FIND_IN_SET`, `SPLIT_PART`, `PARSE_URL`,
`URL_ENCODE`, `URL_DECODE`, `FORMAT_NUMBER`, `TO_CHAR`; `MD5`, `SHA1`, `SHA256`, `SHA512`, `CRC32`,
`COMPRESS`, `BASE64`, `TO_BASE64`, `FROM_BASE64`, `TO_BASE32`, `FROM_BASE32`, `HEX`, `TO_HEX`,
`FROM_HEX`, `CODE_POINTS_TO_STRING`, `TO_CODE_POINTS`; `CBRT`, `FACTORIAL`, `HYPOT`, `LOG2`, `LOG1P`,
`BIT_COUNT`, `BIT_GET`, `GETBIT`, and the hyperbolic and degree-taking trigonometric families
(`SINH`, `COSH`, `TANH`, `ASINH`, `ACOSH`, `ATANH`, `SEC`, `CSC`, `COTH`, `SECH`, `CSCH`, `SIND`,
`COSD`, `TAND`, `ASIND`, `ACOSD`, `ATAND`); `ARRAY_POSITION`, `ARRAY_REVERSE`, `ARRAY_DISTINCT`,
`ARRAY_EXCEPT`, `ARRAY_MAX`, `ARRAY_MIN`, `ARRAY_APPEND`, `ARRAY_PREPEND`, `ARRAY_REMOVE`,
`ARRAY_REPEAT`, `ARRAY_INSERT`, `ARRAY_COMPACT`, `ARRAYS_ZIP`, `SORT_ARRAY`; the whole `MAP_*` family,
JSON mutation (`JSON_SET`, `JSON_INSERT`, `JSON_REMOVE`, `JSON_REPLACE`) and JSON introspection
(`JSON_TYPE`, `JSON_DEPTH`, `JSON_KEYS`, `JSON_LENGTH`, `JSON_PRETTY`, `JSON_STORAGE_SIZE`); the
`SAFE_` arithmetic family, `CONVERT_TIMEZONE`, `LAST_DAY`, `AGE`, `ADD_MONTHS`, `DAYNAME`,
`MONTHNAME`; and the planar `ST_*` surface, which is a different geometry from the service's and is
already refused for that reason under *Geography*.

### Temporal — *large, and the representation is discovered rather than stated*

Cosmos has `DateTimeAdd`, `DateTimeDiff`, `DateTimePart`, `DateTimeBin` and tick conversions; Calcite
has `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`. The mapping is mechanical and the representation is
not: a date is an ISO string or an epoch number by application convention, and `_ts` is the only value
whose encoding the service defines. Pushing a temporal function down means knowing what the column
*is*, and nothing in the row model says. `_ts` alone is reachable without answering that. For the
rest the question goes to the service rather than to the caller: `IS_DATETIME` recognises an ISO 8601
string and `IS_NUMBER` an epoch, and where the parse check is too weak a shape check is constructible
out of `LENGTH` and `ENDSWITH` — measured, and written up under *Rewriting a typed comparison into one
the service can evaluate* below. Section 6 records why that is the route and not a declaration.

### Rewriting a typed comparison into one the service can evaluate

A family rather than an item, and it is written down because the same argument keeps being had one
function at a time. A comparison Cosmos cannot evaluate — `CAST(<path> AS UUID) = <literal>`,
`CAST(<path> AS TIMESTAMP) > <literal>` — can sometimes be rewritten into one it can, by rendering
both sides into a representation the service compares natively. The value is real: these are the
predicates a view over a container produces, and today every one of them reads whole documents.

**What makes a rewrite sound is not that the type has an unambiguous string form.** It is that the
*stored* string is in the form the literal renders to. Only one side is ours: the literal we render,
the document we do not. A UUID written `A1B2…`, unhyphenated, or brace-wrapped parses to the same
value and is string-equal to none of the others. That is the same objection `DESIGN.md` records under
*Casts over document values*, and it is why this is a family of arguments rather than one rule.

Two ways past it, and they are not interchangeable.

- **Enumerate the renderings, where the set is finite.** UUID qualifies: hyphenated or not, braced or
  not, and case, which Cosmos's `STRINGEQUALS(a, b, true)` collapses on its own. Four literals under
  an `OR` and the rewrite is an *equivalence* rather than an approximation. The criterion is
  finitely-many textual forms, which is narrower than "has a canonical form" and is what makes UUID
  the first candidate and `TIMESTAMP` not one.
- **Push weaker and recheck**, which needs no enumeration at all. `CosmosFilterSplitRule` already
  pushes a weakened conjunct and rechecks the original in process, so a rewrite only has to produce a
  *superset*. This is the general mechanism; what is missing is the table of supersets, not the rule.

**Measured, against an account, and it splits the idea in two.** A filter takes any computed
expression the service can evaluate; a sort takes a document path and almost nothing else.

| | |
| --- | --- |
| `WHERE DateTimeToTicks(c.v) > <n>` | accepted |
| `ORDER BY DateTimeToTicks(c.v)` | refused, 400, code 2206 |
| `ORDER BY IIF(…)` | refused, 400, code 2206 |
| `ORDER BY c.v` | accepted |
| `ORDER BY ST_DISTANCE(…)` | accepted — see *Geography* |

So the rewrite family below is a **filter** technique, and `ST_DISTANCE` in an `ORDER BY` is an
exception the service makes rather than a rule anything else can be read into. Two computed keys were
tried against it and both were refused with the same 2206 the cast column already gets.

**The vocabulary available to a rewrite is wide.** `IS_DEFINED`, `IS_ARRAY`, `IS_BOOL`, `IS_NULL`,
`IS_NUMBER`, `IS_INTEGER`, `IS_FINITE_NUMBER`, `IS_OBJECT`, `IS_PRIMITIVE`, `IS_STRING` and
`IS_DATETIME`; `IIF` for a conditional; the `DateTime*` family; and arithmetic. That is enough to
recover Calcite's meaning for most comparisons, inside a `WHERE`.

**Comparison is type-strict, and the type order is the service's own.** Measured over one path
holding a number, the string `"5"`, `"abc"`, a boolean, a null, an array, an object and nothing at
all: `v > 3` returns only the number, `v = 5` only the number, `v = '5'` only the string. Nothing is
coerced across types, so a pushed comparison does not silently widen — which is why the existing
numeric guard can *admit* non-numbers rather than needing to exclude them. `ORDER BY v` returns them
in the service's own order: absent, null, boolean, number, string, array, object. That order is
Cosmos's and not Calcite's, which is the standing reason a sort over a mixed path is not simply
pushed.

**`STRINGEQUALS` takes the third argument the UUID candidate needs**, measured:
`STRINGEQUALS(c.v, 'ABC', true)` matches a stored `"abc"`.

**`IS_DATETIME` is a parse check and not a shape check**, which is the thing to know before reaching
for it. Measured: `2024-01-01`, `2024-01-01T00:00:00Z`, `2024-01-01T00:00:00.500Z` and
`2024-01-01T00:00:00+00:00` all answer `true`; `"hello"` and a number answer `false`. It says a value
*is* a datetime, never that two values are written the same way, so it guards a comparison and does
not establish an order.

**A rewrite is a pair, and the second half is a guard.** The pushed form need not be the rewritten
comparison alone — extra conditions can be pushed alongside it to make the service's answer mean what
Calcite means. This adapter already does exactly that for numbers, and the shape is worth copying
rather than reinventing. `CosmosFilterSplitRule` renders a numeric comparison as

```
IS_DEFINED(x) AND (NOT IS_NUMBER(x) OR (x > bound - 1 AND x < bound + 1))
```

Note which way the guard runs. It does not *exclude* the values it cannot reason about, it **admits**
them — a non-number passes the pushed filter and is thrown out by the recheck above. Excluding them
would have made the pushed predicate a subset, and a subset drops rows the query should have returned.
Widening where you are unsure is what keeps it a superset, and a superset is the whole requirement.

So a rewrite is: a pushed comparison, plus whatever guard admits the rows it does not describe. For
the UUID case that is `NOT IS_STRING(x)` beside the `STRINGEQUALS` disjunction; for a temporal range,
`NOT IS_STRING(x)` beside the string comparison.

**Ordering is a different problem and does not get the escape hatch.** A wrong order under a `LIMIT`
returns the wrong rows, and re-sorting in process is what already happens, so a maybe-wrong pushed
sort buys nothing. It has to be guaranteed rather than approximated.

A normalising key cannot rescue it: `DateTimeToTicks` answers the same ticks for all three shapes of
one instant — the obvious way out — and `ORDER BY DateTimeToTicks(…)` is refused, measured.

**A guard can, and this is the part that took measuring.** A guard cannot make a doubtful row sort
correctly, because ordering is not a per-row question. What it can do is *restrict the sort to the
rows whose shape is known*, and the shape is checkable at the service rather than promised by a
caller. Measured, over one path holding five datetimes in three shapes, a non-date and a null:

```sql
WHERE IS_DATETIME(c.v) AND IS_STRING(c.v) AND LENGTH(c.v) = 20 AND ENDSWITH(c.v, 'Z')
ORDER BY c.v
```

selects exactly the fixed-shape values, excludes the fractional-precision one, the `+00:00` one, the
non-date and the null, and orders what remains chronologically — under a page and under `DESC` as
well. So `IS_DATETIME` being a parse check rather than a shape check is not the obstacle it looks
like: a shape check is *constructible* out of `LENGTH`, `ENDSWITH` and the type tests.

**What that leaves is a rule rather than a promise**, which is a much better place to be. The pushed
half is the guarded sort; the other half is `WHERE NOT (<guard>)`, read separately and merged in
process. Two things it has to get right: the complement is unbounded in principle, so a `LIMIT` cannot
simply be pushed with the guarded half and stopped — enough has to come from both sides before the
merge — and where nothing conforms it must degrade to what happens today rather than to nothing.
The shape is also not one shape: `LENGTH(c.v) = 20 AND ENDSWITH(c.v, 'Z')` is seconds-and-`Z`, and a
container written to a different fixed shape wants a different guard, which is an argument for the
guard being derived from a sample or an operand rather than hard-coded. There is no
disjunct that makes a sort right for rows whose shape you could not vouch for. The nearest thing
would be sorting the conforming rows at the service, reading the rest separately and merging the two
in process — which is a real technique, and a larger one than this item.

Where it can be guaranteed is ISO-8601 UTC, and the condition is sharper than it looks: lexicographic
order matches chronological order only if **every value shares one exact shape**. Mixed fractional
precision breaks it — `…T00:00:00.500Z` sorts before `…T00:00:00Z`, because `.` is 0x2E and `Z` is
0x5A — and so does `Z` against `+00:00`. So the promise is not "ISO-8601 UTC" but "one fixed
ISO-8601 UTC shape, for this path".

**That promise is much narrower than a type**, and worth separating from section 7 for exactly that
reason. Not *this column is a `TIMESTAMP`*, only *these strings share a shape*. An operand could
carry it without settling the typed-column question at all.

**And the mechanism is already built.** An uncast path sorts at the service today — it is why a page
ordered by a raw path reads a page while one ordered by a cast column reads everything, measured and
recorded in section 7. What does not push is the cast. So the change is one rewrite: drop an
order-preserving cast from a sort key, under that promise. Not a new sort pushdown; the existing one,
reached through a cast it currently refuses. Range predicates over the same shape are the easier
half — string comparisons, and those *do* have the recheck escape.

See *Temporal* above, whose prerequisite this is a narrower statement of.

#### Partly built, and the unbuilt part is named

**The sort was not merely unpushed — one spelling of it pushed unguarded, which was a wrong answer.**
`JSON_VALUE(doc, '$.at' RETURNING TIMESTAMP)` resolves to a path (`TryResolvePath` accepts a
two-or-more-operand accessor and the `RETURNING` flags are trailing operands nothing reads), so a sort
over one bound to the path and rendered as `ORDER BY c.at` — a lexicographic string sort the plan
believed was chronological. Measured, over a container declaring `at` present and a string and nothing
about its shape:

```
SELECT VALUE { "at": (IS_PRIMITIVE(c.at) ? c.at : null) } FROM items c ORDER BY c.at ASC
```

The .NET SDK's default serializer writes exactly the mixed path that breaks, so this was the common
case rather than the exotic one — see *Why the SDK writes an unsortable shape* below.
`CosmosSort.OrderIsLexical` now gates it on `PreservesOrder`, and refuses where no container is in
hand. Two tests pin both directions.

**Built, with the ordering licensed:**

- **A UUID pattern is read as a shape rather than matched against a table.** The rows were generated
  across four axes and the product was eighty patterns — which was the wrong shape for the problem,
  because the axes a UUID pattern varies along do not close. `[1-5]` is what a schema written against
  RFC 4122 pins and is the commonest spelling published; `[8-9a-b]` is the variant as ranges;
  `[0-9abcdef]` is the hex class spelled out; a v7 container within a known epoch pins a prefix. None
  was a row, and an unrecognised pattern states **nothing**, so each lost the path its *equality* as
  well as its order and every comparison against it read whole documents.
  `CosmosUuidForms.Recognise` decides the shape instead — 32 nibble slots, hyphens at four fixed
  positions, anchored, every slot a set of hex digits — and the confinement the signed comparison
  needs is read off slots 1 and 17 rather than matched. An alternation is a **union** of its branches,
  which derives the nil-UUID rule that was written by hand, in any of the four ways its anchors can be
  spelled. A class range is read over **code points**, which is the trap rather than the spelling:
  `[8-f]` spans `0x38`–`0x66` and admits `@` and `A`–`F`, so it is refused, and so is `[0-;]`, which
  has no letter to give the case away. `DESIGN.md` carries the argument.
- **The brace and the hyphen are axes of the spelling**, so a brace-wrapped and a hyphenless UUID are
  forms of their own — sixteen in all, the product of brace, hyphen, case and confinement. Neither
  costs a relation: a character in the same place in every stored string never decides a comparison.
  What licensed them and refuses `urn:uuid:` and the parenthesised `(…)` spelling is the **reader**,
  measured — a projected `CAST(<path> AS UUID)` renders as the raw path, and
  `SqlFunctions.stringToUuid` takes all four shapes in either case and raises on those two. So both
  remain unclaimed although each is as canonical and as sortable as the four; claiming either means
  the projection rendering a transformation rather than the path.
- **`RenderUuid` refuses a literal outside a confined form's sign class**, and only where the answer
  can differ. The confined rows say lexical order *is* Calcite's order under the signed comparison,
  and the argument is that each half's sign is constant — across the container, which the literal is
  not in. Against a path confined to a first digit of `0`–`7`, `>` on a signed-negative literal is
  true of every document and lexically true of none. Under the unsigned default the sign decides
  nothing and nothing is refused, which is why the refusing branch has **no test**: it needs
  `calcite.uuid.unsigned.comparison` off before Calcite loads, which is a process rather than a test.
- **The temporal rows are generated rather than written out**, because what makes a shape usable is
  its *fixedness* and not which shape it is. The UUID rows were generated the same way and are not
  any more — see the entry above — because their axes do not close the way
  these do: a fraction has a width and a zone has a spelling, and there is nothing else to vary. Five spellings became
  seventy-seven, across four axes: fraction width (0–9 digits, seven being a tick and nine what a Java
  or Go writer produces), zero-offset spelling (`Z`, `+00:00`, absent), extended against basic format
  (`2024-01-15T12:30:00Z` against `20240115T123000Z`), and how much of the instant is stored at all —
  minute precision, a calendar date in either format, a year-month, and a clock with no date.
  `iso8601-utc-seconds`, `-milliseconds`, `-microseconds`, `-ticks` and `iso8601-date` keep their
  names; the rest are named systematically.
- **What is never generated is the shape that varies**, and that is the whole of the argument: a
  fraction of unstated width (`[0-9]{1,7}`, `[0-9]+`, or an optional group) and an offset not pinned
  to zero both admit a conforming document the order gets wrong. `AShapeThatVariesIsNotRecognised`
  pins all six spellings of that.
- **A clock with no date is recognised but not renderable.** Its lexical order *is* its chronological
  order, so a sort over one is sound; writing an instant into it would drop the date, so
  `RenderDateTime` answers null and the comparison stays in process. Recognition and renderability are
  separate questions and this is the row that shows it.
- **`Normalise` gained a fourth identity**: for an atom matching one character, `A{m}A{n}` and
  `A{m+n}` accept the same strings, and so do `A` and `A{1}`. That collapses the axis a date pattern
  varies along most — `\d\d\d\d-\d\d-\d\d` is as common as `\d{4}-\d{2}-\d{2}`. Only the counted
  quantifier merges; `?`, `*`, `+` and `{m,n}` all admit a range of widths, which is the one property
  these forms turn on, so an atom carrying one ends the run beside it.
- `CosmosTemporalForms.Render` — the instant analogue of `CosmosUuidForms.Render`, writing a literal in the
  path's own shape, driven by the same generated table. It **refuses a literal finer than the form**
  rather than truncating: against a seconds path, `> '…12:30:00.5'` truncated to `> '…12:30:00Z'`
  admits a stored `12:30:00Z` that is earlier than the literal. A coarser literal is written out in
  full and loses nothing.
- `CosmosFactRewriter.TryLowerInstant` — `=`, `<>`, `<`, `<=`, `>`, `>=` lowered to a string
  comparison. The flipped orientation reverses the operator, which is the case that would have been
  silently wrong rather than merely unpushed. Equality is gated on `PreservesEquality` and the rest on
  `PreservesOrder`. **Which expressions it reads has since been cut back, and the cut is the
  important part.** It took `CAST(<path> AS TIMESTAMP)` and `JSON_VALUE(…, RETURNING TIMESTAMP)`,
  neither of which Calcite can *evaluate* over an ISO-8601 instant — so the lowered comparison
  answered rows for a query that raises, which is a different query rather than a faster one. It now
  reads a cast only where `CosmosTemporalForms.EngineReads` says the engine performs it (a calendar
  date, and a whole-second or whole-minute time of day), and a parse only where the format reads the
  shape. The `RETURNING` spelling is gone entirely.
- `CosmosFactRewriter.TryLowerUuid` — the same six over `CAST(<path> AS UUID)`, on the same two
  gates, **as of #142**. It took no operator and built an `EQUALS` before, which was right while the
  engine compared UUIDs as signed halves and an unconfined canonical form preserved equality alone;
  [CALCITE-7716](https://issues.apache.org/jira/browse/CALCITE-7716) made the comparison unsigned in
  1.43, so the range is licensed and is now written. A keyset-paginated `WHERE id > @last ORDER BY
  id FETCH NEXT n` is the shape that wants it, and the page beside it is the entry below. The
  reversal is pinned in two places, the rewriter's own test and the planning one, because it is the
  case that selects the complement rather than merely failing to push; and a range pins no partition,
  `CosmosPartitionKeyExtractor` reading an equality and nothing looser.
- **The `PARSE_` chain, at both sites.** `CosmosTemporalParse` reads
  `PARSE_DATE(<format>, <path>)`, `PARSE_DATETIME`, `PARSE_TIME` and `PARSE_TIMESTAMP`;
  `CosmosTemporalForms` carries, per shape, the formats that read it exactly; and the two sites ask
  the same question — `TryLowerInstant` for a comparison, `CosmosProject.OrderingCandidateOf` with
  `IsOrderable` for a sort key. The sort needed a third thing that the entry below did not expect: the
  projection has to *render*, or there is no `CosmosProject` under the sort to record the binding on,
  and Calcite does not transpose a sort through a projection whose key is a function call the way it
  does through a cast. `CosmosRexTranslator.TryStoredInstantProjection` sends the guarded path down
  and leaves the conversion to the reader, which is the same move `TryStoredUuidProjection` makes.
- **And the format had to be measured, which turned up a wrong answer in `DESIGN.md` rather than a
  missing feature.** The entry below said a format that does not match "parses to null for every
  row". It does not. `yyyy-MM-dd'T'HH:mm:ss'Z'` — the spelling `DESIGN.md` recommended — reads
  January the 2nd at 03:04:05 as **April the 2nd at 03:00:05**, because Calcite's format model matches
  its elements without regard to case and takes the `mm` for a second month. A query written that way
  is already returning wrong rows in process; the adapter declines to push it, which leaves that
  answer where it is rather than replacing it with a different one.
  `CalciteTemporalParseMeasurementTests` runs every spelling the table claims against a sample of the
  shape it claims, so a row that stops being exact fails a test.
- **`CAST(<text> AS TIMESTAMP(p) FORMAT '<format>')` is the same chain (#170).** `CosmosTemporalParse`
  reads it with the operands the other way round, so both sites take it with no change of their own;
  the measurement runs every claimed spelling through the cast as well. It is the spelling a model
  view can hold, `PARSE_DATETIME` not validating under the default configuration views are analyzed
  with (ikvmnet/calcite-dotnet#189). A cast whose precision is below the digits its format reads is
  refused. The issue reported `.000` for every millisecond format; measured over a column, the cast
  keeps the fraction even into `TIMESTAMP(0)`, so the truncation it saw is downstream of the cast.
  Where exactly has not been traced. A finer precision does not open the wider shapes, measured:
  the default type system caps `TIMESTAMP` at 3, so `TIMESTAMP(6)` and up come back as `(3)`, and
  `FF4` through `FF9` read the digits as milliseconds exactly as `FF3` does (`.6789` is 6.789 s).

**Not built, and each for a stated reason:**

- **`ORDER BY CAST(<path> AS TIMESTAMP)` still does not push** — measured, `SELECT VALUE c FROM items
  c` under a `ClrCursorSort`. This is the cast-drop the paragraph above calls *the* change, and it
  is the one thing here that is not done. The range rewrite reaches it because a filter's predicate is
  rewritten before the split rule reads it; a sort key is not a predicate. The key is a *computed
  projection*, so `fields[index]` is null and the key is refused before its form is ever asked about.
  Licensing it means the implementor carrying a temporal binding per ordinal the way it already
  carries `SortableExpressions` — `CosmosProject.Implement` recording that ordinal *n* is the path
  `c.at` read as an instant — so that the rule and the implementation decide on the same binding. Note
  the spelling is less urgent than it looks: section 6 records that `CAST(<string> AS TIMESTAMP)`
  accepts only `yyyy-MM-dd HH:mm:ss` and *raises* on every ISO-8601 form a document stores, so the
  cast is the spelling that cannot work in process — **and neither can `RETURNING`, which this entry
  used to point at as the one that does.** Measured since: `JSON_VALUE(…, RETURNING TIMESTAMP)` raises
  for every string, the clause asserting the extracted type rather than converting to it and wanting
  a JSON number of epoch milliseconds. Both are refused now, and the parse is the spelling that
  works — see *A parse carries a format* in `DESIGN.md`.
- **A space-separated instant is not recognised** — *small, and the obstacle is gone.*
  `2024-01-15 12:30:00Z` is as fixed as its `T` spelling and would sort as well — Python's
  `isoformat(sep=' ')` and a good many SQL exports write it. Matching the forms by spelling once
  stripped whitespace outside a class, which made the pattern indistinguishable from one with no
  separator and recognised a pattern carrying a significant space as the form without it — a wrong
  answer, `RenderUuid` writing a spelling the stored values do not have. The one reader of a pattern
  reads a space as the character ECMA-262 matches, and `TheSpellingsInTheWildAreRecognised` and
  `ASpaceTheRegistryDroppedIsAnotherLanguage` hold spaced patterns unrecognised. What is left is
  defining the space-separated forms; `ASpaceSeparatedInstantIsNotYetRecognised` is the row that flips.
- **`PARSE_TIMESTAMP` is recognised and pushes at neither site, and the reason is its type rather
  than its parse** — *small, and two separate pieces of work.* It is the name a caller reaches for
  first and it reads a format exactly as `PARSE_DATETIME` does, measured. What it answers is
  `TIMESTAMP WITH LOCAL TIME ZONE`. For a comparison that means the other operand is a zone-less
  literal and the question is what the session's zone is, which no declared stored form answers —
  the internal representations happen to line up, and "happen to" is not an argument. For a sort it
  means `CosmosJson` has no reading for the type, so the projection cannot be sent down and there is
  nothing for the sort to stand on; adding the reading is a line, and it would change every
  `TIMESTAMP WITH LOCAL TIME ZONE` projection rather than only this one, so it is its own decision.
  `ParseTimestampIsRefusedForItsTypeRatherThanItsParse` is the row that would flip.
- **`TO_DATE` and `TO_TIMESTAMP` are not recognised, and the obstacle is a name rather than a
  format.** Each is *two* operators under one SQL name — Calcite registers `TO_DATE` beside
  `TO_DATE_PG`, implemented by `SqlFunctions.DateFormatFunction` and `DateFormatFunctionPg` — and
  which one a query resolves to depends on the libraries the connection enabled, which a `RexCall`'s
  name does not say. The first was measured and agrees with the `PARSE_` family on every spelling in
  the table; the second was not. Reference identity against `SqlLibraryOperators.TO_DATE` would tell
  them apart, and the operand order is the other way round (`TO_DATE(<text>, <format>)`), so it is a
  small entry rather than an empty one.
- **A microsecond or tick fraction has no parse spelling, and there is none to find.** Every fraction
  element the model has — `%E1S` through `%E5S`, `FF1` through `FF7`, `MS` — lowers to a Java
  *millisecond* field, which reads whatever digits are in front of it as milliseconds: `.678901` is
  11 minutes and 18 seconds rather than 679 milliseconds. Calcite's timestamps are milliseconds, so
  this is a property of the target type rather than a gap in the table. The shape Azure recommends is
  therefore the one a parse cannot reach — it reaches a `CAST` and a `RETURNING` clause as before.
- **Neither has the separator-less basic shape**, for a second reason: the model's one-letter fields
  are greedy, so `%H%M%S` reads `030405` as hour 0 and minute 945, and .NET's `DateTime.TryParse`
  refuses `20240102T030405Z` outright, so the projection could not be read back either.

### Why the SDK writes an unsortable shape

Worth recording because it settles how common the mixed path is, and it is not a matter of opinion.
The .NET SDK never chose a date format: `CosmosJsonDotNetSerializer` is Newtonsoft at stock settings,
and Newtonsoft's ISO writer emits the *minimum* fraction digits because ISO 8601 treats trailing
fractional zeros as insignificant. System.Text.Json's ISO 8601-1:2019 profile trims the same way, so
switching serializers does not help. `DateTime.UtcNow` carries seven significant tick digits about
nine times in ten, so the shape wobbles between six and seven digits; a deliberately truncated value —
`DateTime.Today`, a parsed `"2024-01-15"` — drops the fraction entirely and sorts *after* everything
with one.

Azure's own documentation recommends `yyyy-MM-ddTHH:mm:ss.fffffffZ` and then asserts, wrongly, that
ordering "is preserved when they're transformed to strings". Two issues report the contradiction —
[#1468](https://github.com/Azure/azure-cosmos-dotnet-v3/issues/1468), closed with no fix or rationale
posted, and [#4904](https://github.com/Azure/azure-cosmos-dotnet-v3/issues/4904), open since November
2024 with no maintainer reply. `CosmosSerializationOptions` exposes no date handling and
`CosmosJsonDotNetSerializer` is `internal sealed`, so the documented format requires writing an entire
`CosmosSerializer`; requests to expose the settings ([#551](https://github.com/Azure/azure-cosmos-dotnet-v3/issues/551),
[#1813](https://github.com/Azure/azure-cosmos-dotnet-v3/issues/1813)) have stood for years.

So: a container written by an ordinary .NET application has a mixed path and is correctly refused, and
a container that applied a converter has one of the fixed shapes and is correctly licensed. That is
the distribution the gate is sized for.

### Clause-level

- **Native `IN` and `BETWEEN` — closed by measurement, not built.** `expandSearch` turns both into
  comparison chains, and the question was whether the native spelling is priced differently.
  Measured on a real account over five hundred documents: `IN` and its OR-chain cost *identically*
  at three, ten and fifty values — 6.06, 7.62 and 16.52 RU, matching to the hundredth — and
  `BETWEEN` costs exactly what its two comparisons do (7.90 RU). Neither form used an index on an
  unindexed path, so the reference's "index-friendly" is a property of the path rather than of the
  spelling. Emitting the native form would be a change with no effect.
- **`DISTINCT` with `ORDER BY` over a key only the filter proves present** — *small, and it waits on
  the statement node.* The null-placement rule refuses a nullable sort key, and a query that removes
  the nulls itself satisfies it: `WHERE t IS NOT NULL ORDER BY t` pushes over any document path, the
  sort reading what the filter beneath establishes (L1; `DESIGN.md` under *Ordering is a total order
  over JSON types*). Over a `DISTINCT` the placement is not the only gate: the key a `DISTINCT` binds
  is a question the sort's rule asks on its own walk and implementation asks again, so it reads the
  declaration (`CosmosAggregate.DistinctKeyBinding`), and a key only the filter proves present binds
  nothing and its sort stays in process — `CosmosDeclaredFactPlanningTests.ADistinctBindsItsKeyByTheDeclaration`.
  A statement node carrying the scope it was decided in (#148) would let the binding read the rows too.
  A key some documents lack is normalised, and whether `ORDER BY c.p` beside
  `SELECT DISTINCT VALUE { "p": IS_DEFINED(c.p) ? c.p : null }` orders the rows the way the projected
  key would is unmeasured (`DESIGN.md` under *Plan order is not clause order*).
- **A grouping or `DISTINCT` over a text accessor of an undeclared path** — *medium, and the shape
  is known.* It runs in process now: the service groups the stored values and SQL the rendering, so a
  stored `30` and `"30"` were two groups of what SQL calls one (review probes J1, J2 and J13;
  `DESIGN.md` under *An accessor keeps its guard wherever it is written*). A container declaring the
  path a string still pushes. A value has one rendering, so the groups by value refine the groups by
  rendering: push the grouping by the guarded value, read each key back as text, and finish by
  rendering in process, re-aggregating the partials the way `CosmosAggregateSplitRule` finishes a
  grouping set. That would recover every `GROUP BY` and `DISTINCT` over `JSON_VALUE` the decline
  costs, seven statements of the differential corpus when it was counted. The other way back, a key
  at the service that renders as the reader does — something like `IS_STRING(p) ? p : ToString(p)`
  under the guard — is only sound where the service's `ToString` spells every number and boolean as
  Java's `toString` does, which `1e30` (`1.0E30` in process) already suggests it does not; measure
  the spellings before writing that one.
- **A sort over a text accessor of an undeclared path** — *medium, and the shape is known.* It runs
  in process now, with its row limit: the service orders the stored value, by JSON type and numbers
  by value, and SQL the text, so over the typed container the label came back `'true'` before `'30'`
  and the price `'7'` before `'30'` (`DESIGN.md` under *Ordering a sort key the way it reads*). A
  container declaring the path a string still pushes. No single statement recovers the rest, since
  the service orders by a path and nothing else. Two would, in the shape section 5 gives the null
  placement: the strings, `WHERE IS_STRING(p) ORDER BY p` with the page, whose text is their value;
  and every other document, rendered and sorted in process, merged with the first by text. The page
  bounds the first statement and not the second, so it buys back a path that is nearly always a
  string and that nobody declared, and the cost model would have to know the second half's size.
- **A sort over a typed accessor of a path not declared its type** — *small, and the shape is
  known.* It runs in process now, with its row limit: `JSON_VALUE(… RETURNING INTEGER)` reads null
  for an object and an array, and the service sorts both after every number, so a page over the
  typed container's `label` came back `[null, null, 30]` where SQL answers three nulls
  (`DESIGN.md` under *Ordering a sort key the way it reads*). A container declaring the path the type
  still pushes. Easier than the text case, because every value of another type reads one null: the
  numbers, `WHERE IS_NUMBER(p) ORDER BY p` with the page, and the rest, whose key is null and needs no
  order, placed before or after them as the placement says. The cost model would have to know the
  second half's size, as there.
- **String orders the plan cannot prove: declared, or checked as the rows arrive; four shapes still
  declined** — *done, and small each for what is left.* Calcite compares strings by UTF-16 code unit, as
  Java does, and the service by code point, so the two part where a character above U+FFFF meets one
  between U+E000 and U+FFFF — private use, the CJK compatibility block, the fullwidth forms, U+FFFD.
  Measured on the emulator and on the probe account (`Service.StringOrderIsCodePoint`, and
  `Service.ConstantStringComparisonIsByCodeUnit` for two constants). Every order the service computes is
  asked of `CosmosServiceOrder` (`DESIGN.md`, *The service's order, asked once*). **A declaration proves it
  when the plan is made:** a JSON Schema `pattern` read as the characters a path may hold
  (`CosmosPatternReader`; *Reading a pattern as characters*), and an `enum`, a `const`, a stored form or a
  filter's `=` or `IN`, each of which the theory derives the characters from (*Characters, a fact of the
  theory*). **The rows prove the rest:** the anchored
  read (*The anchored read*) sends `ORDER BY c.id`, its page and its keyset seek `WHERE c.id > ?`, a sort
  over a declared string, a comparison against a parameter, `MAX(c.id)` and a `BIGINT` past int64, and the
  reader checks each value as it arrives and reads a fallback where one breaks the proof. Eight of the nine
  recorded divergences push again, held to the reader having done its work. What neither reaches:
  - **a comparison of two paths of unknown characters** — the rows the service drops never arrive to be
    checked. Only a declaration recovers it;
  - **a grouped `MIN` or `MAX`** — no one row to check. A grouped reread by key would, at a second statement
    per group whose extreme is no anchor;
  - **a comparison with a parameter under a `NOT`, or beneath a grouping, a `DISTINCT`, a rank or a lookup** —
    a weaker comparison under a `NOT` makes a stronger condition, and the recheck needs the rows the filter
    kept (`CosmosClauses.Recheck`); under `AND` and `OR` alone the conjunct is rechecked whole
    (`CosmosRecheck`). The walk asks the declaration alone, so a comparison a sibling proves exact in its
    stratum still declines a grouping above it; reading the strata there would cost a scope per walk. Two
    recoveries, neither built: `NOT (x <= ?)` is `x > ?` in SQL's logic and the service's alike, so the
    filter could be read with each `NOT` of a comparison turned into the comparison it negates; and a
    `COUNT`, a `GROUP BY` or a `DISTINCT` over such a filter could carry its own statement as the primary
    and a row statement aggregated in process as the fallback, the value deciding which when the statement
    runs — two plans for one statement, which nothing here builds yet. Main pushed the aggregate with the
    exact comparison for every value, right only where the value is an anchor;
  - **a `REAL` key before the last**, for which no anchor is stated (within ±2^24 would do).

  The rest is not about characters: the null placement and the mixed-type keys, which **a sort split by
  JSON type** is for (section 5).
- **A grouped statement ordered by its key** — *small, and measured.* `GROUP BY k ORDER BY k` is
  accepted and honoured on Azure, both directions and with `OFFSET LIMIT`, but only where the select
  list carries an aggregate or the statement has a partition key: a grouped statement selecting only
  the key, sent across partitions, fails with SC2102, the SDK's cross-partition rewrite adding
  `c._rid`. `ORDER BY` an aggregate, or its alias, is refused. The groups are sorted in process now
  (chapter 9.4), which is correct and costs only the rows the groups are.
- **`ORDER BY` an array element** — *small, and measured.* `ORDER BY c.arr[0]` is accepted on Azure
  and answers as the emulator does; it is a path, so `CosmosServiceOrder` reads it as any other, a
  missing element undefined and first. Nothing binds an indexed element as a sort key yet.
- **`TOP` — closed by the same measurement.** Emitted for a rank clause and nowhere else. `TOP 10`
  and `OFFSET 0 LIMIT 10` cost the same 2.37 RU on a real account, so the spelling the adapter
  already emits is the cheaper of nothing.
- **A `JSON_VALUE` behaviour clause is dropped from a projection** — *small, and a wrong answer.*
  `SELECT JSON_VALUE(c."DOC", '$.flag' DEFAULT 'none' ON EMPTY)` renders as
  `(IS_PRIMITIVE(c.flag) ? c.flag : null)`, so a document with no `flag` reads null where Calcite
  answers `'none'` — found beside #181, and older than it. `IsJsonAccessor` admits a `JSON_VALUE` of
  any operand count and `TryJsonValueProjection` never reads the trailing ones, and the same leniency
  binds such a column to the bare path as text, which is why #181's boolean cast refuses a field. The
  sound answer is to address no path for a clause that substitutes a value; rendering `DEFAULT … ON
  EMPTY` as an `IS_DEFINED` ternary is the cheaper one, once Calcite's own answers are measured.
- **A string function pushes only where its characters are proved, and could push more** — *medium; what
  is left of a wrong answer closed.* `CHAR_LENGTH`, `SUBSTRING`, `LEFT`, `RIGHT`, `UPPER`, `LOWER`, the
  case-insensitive `LIKE` rewrites and the trims were pushed exactly over any declared string, and were wrong
  where a string held a character beyond U+FFFF, ß or the Kelvin sign, or white space at an end
  (`reports/verify-strings.md`). Each now pushes only where the facts prove the classes of character it needs
  absent, and is computed in process otherwise (`Sql/CosmosStringFunctions.cs`; `DESIGN.md`, *What a string
  function computes, asked of its characters*). What is left:
  - **A weakening that would push them anyway** — *not built.* A filter could be sent as `(<pushed>) OR
    RegexMatch(c.s, "[\\x{10000}-\\x{10FFFF}]")` for a count, `"[^\\x00-\\x7F]"` for a case mapping, a test of
    the trimmed class at either end for a trim, with Calcite's filter kept above: the rows without the class are
    exact, and the rows with it are shipped and Calcite decides them. A projection would select a fallback
    member under a name no column has, keyed on the same test, and the reader would compute Calcite's value
    from it. **The warning:** a pattern `RegexMatch` cannot compile answers undefined, not an error, and an
    `OR` arm that is undefined drops the row it was there to keep — `RegexMatch(c.s, "(")` keeps nothing, and
    neither does its `NOT` (`reports/azure-axioms.md` 3.4). Each disjunct has to be one measured to compile on
    Azure and the emulator alike, as `[\x{10000}-\x{10FFFF}]` is. For `CHAR_LENGTH(s) = n` an index-friendly
    form exists, units lying between code points and twice them: `LENGTH(c.s) BETWEEN ceil(n/2) AND n`.
  - **`LIKE` over a declared string** — *done.* It was refused as a rendering whatever was declared, and the
    filter read whole documents where the same statement over an undeclared path pushed as a weakening. A path
    declared a string renders as the string, so it is pushed exactly as every other operator over one is: a
    prefix as `STARTSWITH`, another pattern as the service's `LIKE`, the containment shape only where the
    subject holds no U+0000, and a fold beneath one as the case-insensitive match where it is proved ASCII too
    (`CosmosDeclaredFactPlanningTests.ALikeOverAPathDeclaredAStringPushesExactly`).
  - **A declined function in a filter reads whole documents** — *a missed pushdown, small.* In a projection a
    function the service does not compute over a plain column now sends the column alone, `{"$f0": c.id}`
    (`CosmosProjectSplitRule.Narrows`), as it sends an accessor. In a filter, `WHERE UPPER(c."id") = 'AB'` over
    an `id` nothing proves ASCII is Calcite's, above a scan with no projection, and every document arrives whole
    though one property is read; beside a conjunct that pushes, the same, over fewer. Calcite's
    `PROJECT_FILTER_TRANSPOSE` would put the columns the filter and the projection read beneath the filter,
    where the split sends them; it is not in the rule set, and moving every residual filter's projection
    reshapes plans across the corpus, so it wants its own measurement.
  - **`_etag` declines what Azure's etags would push** — *decided, not a defect.* RFC 9110's grammar admits
    `obs-text`, 0x80 to 0xFF, read as U+0080 to U+00FF, so the theory gives `_etag` N and two of W (U+0085,
    U+00A0), and `UPPER`, `LOWER` and `TRIM` over it are computed in process, the property alone sent
    (`Metadata/CosmosServiceFacts.cs`). The etags Azure issues are a quoted GUID, ASCII; stating that would be a
    fact measured on Azure and not the grammar, which no axiom row pins. Measured and pinned, it would narrow
    the grammar to `"`, `!` and 0x23 to 0x7E.
  - **`REPEAT` and `REPLACE` read a length, and only a domain states one** — *small.* `REPEAT` pushes where its
    subject's length keeps the result within Azure's 10,000 code points, and `REPLACE` where its search holds
    something; a literal knows its own length, and a domain — an `enum`, a `const` — bounds a path's
    (`JsonValues.LengthsOf`). The schema reader does not read `minLength` or `maxLength`, which count code
    points: a `minLength` of n bounds the UTF-16 units from below by n and a `maxLength` of n from above by 2n,
    which is all the theory's units need of them (below, *The other bounds*).
  - **The emulator's U+0000, `REPLICATE` and `REPLACE` are not Azure's** — *recorded.* It stores U+0000 as
    U+FDD0 followed by `0`, so every function over such a string differs; its `REPLICATE` has no limit; and a
    parameter holding U+0000 in `REPLACE`'s replacement or a `CONCAT` fails the statement with 500
    `PostgresError(E22P05)` (Appendix E). Azure is the target, so none of them is declined. The differential
    corpus holds no U+0000 and no repetition near the limit, and the measurement tests that pin Azure's rows
    skip on the emulator's.
  - **A bound parameter holding a lone surrogate** cannot be sent (`Service.LoneSurrogateParameterIsNeverSent`) —
    *named, and narrowed.* A literal is refused when the plan is made and computed in process; a column computed
    from parameters alone is computed above the statement and never sent; and a parameter the service reads is
    refused by name when it is bound (`CosmosQueries.Bind`, `Client/CosmosEncodable.cs`), where Calcite would have
    answered. A plan that reads a fallback for such a value — the filter's rows unfiltered, Calcite's filter above —
    is what would answer it.
  - **`POSITION` over a supplementary character differs on the emulator**, whose `INDEX_OF` counts code points;
    Azure's counts units as Calcite does, and the function is pushed (`Service.IndexOfCountsUnits`). The
    differential corpus asks it only over strings without one, and the `ORDER BY` yardstick names it as the
    family it meets (`Findings`).
  - **A function over a parameter, or over an array's element,** is computed in process: a parameter may hold
    anything until it is bound, and an element is no path a fact is about (below). Deciding a parameter's
    characters as its value arrives, as the anchored read does, would push the first. **What it would reach
    first:** on Azure, `WHERE REVERSE(c.s) = @p` keeps nothing where `@p` holds a character beyond U+FFFF,
    though the same comparison projected is true, and the ternary the adapter writes behaves the same
    (`reports/verify-strings.md` §4.4). `REVERSE` asks nothing because it is measured to agree projected; a
    filter comparing it with such a parameter is not measured to, and would have to ask S of the parameter, or
    be measured, before it pushes.
- **`LIKE`'s `_` — closed by measurement, not a defect.** It matches one code point at the service,
  and so does Calcite's: `a`, U+1F600, `b` matches `LIKE 'a_b'` in both, on Azure and on the emulator
  alike.
- **`\d` and `\w` are read as ECMA-262's** — *the owner's to confirm.* A validator in .NET's default
  dialect or Python's matches every Unicode digit or word character, U+FF10 and U+1D7CE among them, so a
  path declared `^\d+$` and validated that way can hold strings whose order the declaration claims and
  the service does not keep. JSON Schema names ECMA-262, and chapter 14.4 tells a user to spell the
  classes out. There is one reader of a pattern now, so refusing the two there would refuse them for the
  characters and the stored forms at once: consistent, and costing nothing a user cannot write as
  `[0-9]` — but every published date and integer pattern written with `\d` would lose its form.
- **A table's form is recognised from its language exactly** — *small, and the owner's decision.* A
  pattern admitting fewer strings than a temporal or numeric form — a month written `[01][0-9]`, an
  hour `[0-2][0-9]`, a fixed width with a first digit `[1-9]` — is no form, though every string it
  admits is the form's and both relations hold of them all. A UUID already reads its narrowings, its
  shape being decided (`DESIGN.md`, *One reader of a pattern*). Reading a table's narrowings is the
  same argument, read slot by slot against the definition's runs; the price is that a literal outside
  the narrowing, which the form can spell and no document holds, is compared rather than refused,
  which answers what the query does.
- **One language read as two** — *small.* The reader puts a pattern's strings in one structure by
  identities of the language (`CosmosPatternStrings`), and does not claim the converse: `^a$|^b$` and
  `^[ab]$`, or `^[0-4][0-9]$|^[5-9][0-9]$` and `^[0-9]{2}$`, are one language read as two, and a form
  written the first way is not recognised. Alternatives differing in one run of one character could be
  merged into one class; no published spelling of a form has been seen to need it.
- **Two values that may both be a JSON null are compared in process** — *small; closed as a wrong answer,
  left as a pushdown.* `=`, `<=` and `>=` between two paths that may each hold a JSON null kept the
  documents holding one at the service, which equates a null with a null where Calcite's comparison is
  unknown — over declared strings and declared integers alike, and for equality too (`CosmosServiceOrder.
  Compare` and `Equate`, measured on the emulator). They are declined now. A guard would push them: `NOT
  IS_NULL` on one side, in a positive position, which is what the filter's conjuncts are.
- **A computed value against a path that may hold another type is compared in process** — *small; closed as
  a wrong answer, left as a pushdown.* A value the service computes is read as its type's values, and, where
  its type admits a null, every other JSON type in its null class: a `CASE` writes a JSON null or hands a
  stored value through, and `CASE WHEN category = 'bikes' THEN NULL ELSE price END <= price` kept the
  marathon, whose price is null, on the emulator. So `price + 1 <= c.other` is declined too, though the
  service's arithmetic answers undefined over what it does not take and never a null. Saying which operators
  answer only undefined — measured for the string functions and division (`Service.StringFunctionsOverNonStrings`,
  `Service.DivisionIsFloatingPoint`), not yet for `+`, `-`, `*`, `%` or the numeric functions — would push them.
- **An array's element is no path the facts are about** — *medium.* `CosmosDocumentPaths.From` answers no
  document path for `$.tags[0]`, so a comparison over one is read as a path that may hold anything, and its
  rendering is weakened and rechecked however the array is declared. A declared `items` read into a fact of
  the element, in the theory, would let a declared array of strings compare its elements at the service.

---

## 5. Planner

### Nullable aggregates — *the guard stands in for the type*

The null-semantics refusals are the biggest source of declined aggregates: `SUM(c.v)` over a nullable
column is `undefined` at the service where SQL skips the null. The fix is rewriting the rendered
argument so Cosmos skips it too — aggregates skip *undefined*, and arithmetic on a JSON null yields
it, so `SUM(c.v * 1)` is the candidate for a column known to be numeric. The rewrite is type-directed
and cannot be applied blindly (`* 1` over a string silently drops it from `MIN`/`MAX`), and a
document path has no declared type — but the service answers the type question itself, so
`IIF(IS_NUMBER(c.v), c.v * 1, undefined)` guards the rewrite per row where a declaration would have
guarded it per column. Section 6 rejected the declaration; this is what stands in its place.
Measure on the emulator before building: that the null is skipped, that an all-null group comes back
as SQL's null does, and that `* 1` does not disturb a large integer.

### A sort whose null placement disagrees is declined, and need not be — *medium*

Today the service's null placement is a constraint on the caller: `defaultNullCollation` must say
`LOW` or every sort over a nullable path silently declines, which the manual carries as an
integration requirement. That is the adapter asking the query to match the store rather than
implementing what Calcite asked for.

It can be implemented, because **the store is a toolbox rather than a counterpart**. Nulls all
compare equal, so their order among themselves is free, and the requested placement is a
concatenation:

- the non-null rows, ordered, from one statement;
- the null and absent rows, in any order, from a second — `WHERE NOT IS_DEFINED(x) OR IS_NULL(x)`;
- read in whichever order the collation asked for.

That is exact rather than approximate, and it is the same partition-and-merge shape as the guarded
temporal sort in section 4. What it costs is a second round trip, an `OFFSET`/`LIMIT` that has to be
split across the two halves rather than pushed to either, and a null half that is unbounded in
principle.

**What is not available is doing it in one statement.** Ordering by a computed key that puts the
nulls where they are wanted needs an expression in the clause, and the service refuses those —
`ORDER BY IIF(…)` answers 400, error 2206, measured. A second sort key is refused beside a distance
for the same reason. So the two-statement shape is not one option among several; it is the option.

**A distance makes this more pressing than it was.** A computed distance is always nullable, where a
promoted `id` is not, so a distance-ordered query needs `LOW` in every case rather than only when the
path happens to be nullable — see the geography items in section 4, and
`CosmosSortRuleTests`, which sets it for that reason.

### Pushing part of an expression — *done*

`CosmosProjectSplitRule` partitioned a projection by whole expressions and never looked inside one, so
`CAST(JSON_VALUE(DOC, '$.n' RETURNING VARCHAR) AS DOUBLE)` was residual whole and what it needed was
`DOC` — a document across the wire for one scalar the service was willing to extract. It now walks the
residual for the maximal sub-expressions that translate, projects each as a column, and rewrites the
residual to read them. `DESIGN.md` records it under *Splitting inside an expression, not only between
them*.

The two things the entry said to get right were the ones that mattered. **Maximality** is the walk
being top-down with the first success taken whole, so a split cannot push a path and then compute an
accessor above it. And **what the residual still reads** is collected by that same walk rather than
from the original expression, which is what saves the bytes: an input reached inside a fragment is
supplied by the fragment, and sweeping the original would have projected `DOC` beside the scalar taken
out of it.

The reading worry turned out to be the opposite of a trap. A fragment is a column, so it is read by the
column's own rules — and that is precisely what makes a rendering reading *safe*: a plain `JSON_QUERY`
cannot be rendered in place inside an expression, because its value is text the reading produces and
the service cannot, but lifted out into a column there is a reading to produce it. The split supplies
what the refusal said was missing rather than working around it.

**The correctness half of #125 is closed too, and this entry said otherwise until it was measured.**
It read: a residual that *consumes* an array still evaluates in process, where Calcite cannot produce
one. That was written while the adapter pushed `JSON_VALUE(… RETURNING … ARRAY)`, whose in-process
answer is null — so lifting a projection emptied the column. Two things since have removed it.
`JSON_QUERY`'s array `RETURNING` does produce an array in process, so a residual over one reads a real
list; and `JSON_VALUE`'s is now refused in every clause, so the case the sentence was about is not
pushed at all and its null is the engine's own answer rather than a divergence. Measured:

```
CARDINALITY(JSON_QUERY(DOC, '$.tags' RETURNING VARCHAR ARRAY))  ->  pushes whole, ARRAY_LENGTH(...)
(JSON_QUERY(DOC, '$.tags' RETURNING VARCHAR ARRAY))[1]          ->  array pushed as a column, ITEM above
CARDINALITY(JSON_VALUE(DOC, '$.tags' RETURNING VARCHAR ARRAY))  ->  nothing pushed, null in process
```

`AResidualConsumingAnArrayReadsThePushedColumn` holds the middle one.

**What it still does not do**: a fragment is only ever as good as the translator — an operator with no
Cosmos form over operands that also have none pushes nothing, as before.

An earlier version of this entry said executing a residual could not be verified offline, the
enumerable adapter's `ClrEnumerableProject` being unimplementable. **It is verifiable, and the claim
was a missing pass read as a defect.** `ClrCursorProject.Implement` throws exactly as Calcite's
own `EnumerableProject.implement` does, and the rule that rewrites one into a calc is a
`TransformationRule` — `VolcanoPlanner.addRule` will not register one against a `PhysicalNode`, so it
can only fire in a hep pass run over the chosen plan, which is what `Programs.standard`'s last pass is
for. The harness now runs it, and `ShouldComputeAResidualCastOverThePushedFragment` reads the row
back. Filed and closed as not-a-defect:
[calcite-dotnet#155](https://github.com/ikvmnet/calcite-dotnet/issues/155).

One thing fell out that was not asked for: because the pushed half is now a `CosmosProject` binding a
path, a sort above it has something to name. The temporal cast this file recorded as blocked before the
sort now pushes where the container declares the shape — and `DESIGN.md`'s account of when
`SORT_PROJECT_TRANSPOSE` declines a cast key was corrected with it, a declaration being a licence the
old wording did not allow for.

### Smaller rules

- **Binding the traversal element in `TryBindOutput`** — *small, and blocked on one measurement.*
  `CosmosUnnest.Implement` binds the element to its traversal alias; `TryBindOutput` leaves it
  unbound, so a rule deciding above a correlate sees a column with no path. A predicate no longer
  needs this — `FILTER_CORRELATE` and `CosmosUnnestRule` between them push one that is written above
  the traversal — but a *projection* over the element still declines and would not have to. What
  stops the one-line change is that the same binding would let `CosmosAggregateRule` group by the
  element, and whether Azure accepts `GROUP BY t0` over `JOIN t0 IN c.tags` is unmeasured. `ORDER BY
  t0` is a 400 there and accepted by the emulator, so this is precisely the shape where the emulator
  cannot answer. Measure it against an account before binding it. The change is now one place for
  both forms a traversal takes — `TryBindTraversal`, which the logical `Correlate` and the converted
  `CosmosUnnest` share since the lookup join's implementation-time walk found no case for the second.
  A lookup whose container side projects the element is the other consumer it would unblock.
- **Tuple indexes** — *small, and unclaimed.* The one indexing-policy declaration
  `CosmosContainerMetadataReader` still does not read. Nothing consults it yet, which is why it was
  left where the full text and vector paths were not.
- **The full text and vector gate reads the policy and the index as one list** — *small, and it is a
  measurement rather than work.* `IsPathFullTextSearchable` and `IsPathVectorSearchable` ask whether
  the container declared **anything** about a path, because the reference and the measurement here
  disagree about which of the two declarations is required — the reference now calls both indexes
  something a query benefits from, and the 400 measured here was over a path with neither. The union
  is the weakest gate both readings agree on. What would tighten it is measuring, against a real
  account, a full text predicate over a path named in the container's full text policy but *not*
  indexed: if that is a 400 too, the two lists want reading separately and the gate wants both.
  See `DESIGN.md` under *The declaration decides whether a full text or vector function pushes*.
- **Computed properties** — *medium.* A container can declare named, queryable, indexable computed
  paths. Their names are read now, for one question only: a sort over one pushes only where an
  included path names it, which Azure requires. Declared metadata is the one kind this adapter trusts, so they should promote to real columns
  with real index awareness rather than being reached as ordinary document paths.

### Two weakenings over one path still share one raw value — *small, found beside the alternatives*

A connection's planner simplifies every filter the split rule makes — `RelOptUtil.registerDefaultRules`
adds `FILTER_REDUCE_EXPRESSIONS` — and Calcite's simplifier takes every literal one expression is
compared with in a condition to be of one class. The alternatives a refused text equality pushes broke
that inside one weakening and no longer do: their number and boolean read the accessor re-typed, or a
literal boxed as `VARIANT` (`DESIGN.md`, beside *What a refused text equality implies*). Two weakenings
in one filter still meet on the accessor typed `ANY`, the numeric window's bound being a `DECIMAL` and
the text comparison's guard a string, so `CAST(JSON_VALUE(DOC, '$.label') AS INTEGER) > 10 AND
JSON_VALUE(DOC, '$.label') > 'bikes'` fails to plan through a connection with a `ClassCastException`,
on `main` too, and so does the same bound beside `= '30'`. Measured through the host's prepare, offline
and against the emulator; `CosmosPlannerTests`' `reduce` flag does *not* reproduce it, so a test for it
wants a connection, as `CosmosConnectionFunctionTests` opens one. The fix is the same one, the bound
reading the value in its own type — but the bound's literal is the type `f9c159e` chose so that the
service reads it as itself, so it wants that measurement redone rather than a re-typing done blind.

### A row's width is weighed at the wire and nowhere else — *medium*

`CosmosToClrCursorConverter` is the wire, and since #125 it costs rows times the width it
carries rather than rows alone — which is what lets a partial projection win, a pushed projection
having no measurable benefit before it.

**Width used to be a column count, and #145 was the day that broke.** The entry here predicted the
shape of it: a count answers badly the moment two plans differ in *which* columns rather than how
many. Field trimming makes that the ordinary case rather than an exotic one — Calcite's trimmer runs
in every host and leaves a `LogicalProject(DOC)` above the scan, so the subtree being converted is
already one column wide and pushing the query's real projection *widens* it. Counted, that charged
back exactly what the projection saved; the two plans then tied on rows, which is the only component
`VolcanoCost` compares — it does not consult `cpu` or `io` at all — and the tie went to whichever was
registered first. One column passed and two failed, which is what made a cost problem look like
anything but one. The converter weighs the document now: by `CosmosContainerStatistics` where a size
was measured, and by a floor of six values where none was, six being the system properties the
service returns with every item and therefore needing no measurement.

**What is still owed is the metadata provider, not another patch to the converter.**
`getAverageRowSize` is the metadata for this and it does not answer: measured over these plans it
returns `null` at every node — scan, projection and converter alike — so nothing was scaled by it and
the count was doing all the work. A `RelMdSize` handler registered for the Cosmos nodes would answer
`averageRowSize` and `averageColumnSizes` from what the adapter knows and Calcite cannot infer, and
would feed every *other* cost decision that still sees rows and nothing else. The converter knowing
its own width leaves those untouched: nothing below it prices what it returns, so a projection pushed
under a sort or an aggregate is still free as far as the model can see, and `io` is still `0.0` at
every node of every plan.

**And what a value weighs is a guess.** The document is weighed in values, so something has to say
what one value is worth; 64 bytes is chosen to over-state a value and therefore under-state the
document, which is the direction that can only ever under-sell projecting and never over-sell it. A
measured figure would come from the same place the handler above would.

### A merged hierarchy still takes seconds to plan — *medium, and half of it is the host's*

Since #201 the self-join merge fires before anything else and prunes the joins it replaces, which took a
five-view hierarchy from 8,250 registered expressions to about 1,200 and from minutes to seconds. A query
over one view registers about 135 and plans in a few hundred milliseconds, and that is the bar. What is
left, measured offline through the host's prepare (`CosmosPlanningTests`):

- **Constant reduction compiles every time.** `ReduceExpressionsRule` hands the constants it folds to the
  planner's executor, the host's `RexExecutorImpl`, which compiles them through Janino — about a quarter of
  a second each under IKVM — and nothing caches the result. The discriminator's comparison is folded once
  per filter spelling that carries it, fourteen times for one expression. A cache keyed on the expressions,
  in the host's prepare, took the first query from 7.5 s to 5.8 s. It is the host's to make; nothing here
  can, short of a rule that folds literal comparisons first.
- **Calcite still explores the projections and filters above the one read.** The query's filter is pushed
  through the merged projection a step at a time, each step a new spelling of the condition and a new set,
  and every projection is registered again over each. A substitution rule that read the whole stack as
  one projection over one filter was tried and made it worse: `RexSimplify` will not flatten the nested
  boolean `CASE`s that pushing the discriminator comparison into the merged `CASE`s leaves, so the read it
  built did not render whole, it could not prune, and it only added alternatives. It would need a
  simplification of `CASE` with boolean arms that Calcite does not do — and pruning that is safe only where
  what replaces the stack pushes whole.
- **The translator runs in rule matching.** `CosmosFilterSplitRule` and `CosmosProjectSplitRule` translate
  every candidate to decide it, about a sixth of what remains.

### Recorded decisions worth revisiting

- **`SELECT VALUE` for a single column** — `DESIGN.md` chose the uniform object form deliberately,
  "whatever the arity", and the materializer depends on it. A single-column projection could be bare
  scalars. Reversing a recorded decision is the work; the code is trivial.
- **`SELECT *` sends promoted columns twice** — `DOC` is the whole document and every promoted column
  is a path within it. Reading them out of the document client-side would avoid it; the saving is a
  few short scalars against a whole document, so smaller than it first looks.

---

## 6. A container's declared facts

Built, #93: a JSON Schema on the `containers` operand, read into `(path, claim)` atoms the planner
asks, and reasoned about since on Eggnog (*The fact reasoning on Eggnog*, below). `DESIGN.md` under *What a caller may declare beyond it* is
the design record — the model, the measurements, the keyword audit and the trust boundary. What is
below is what it does not do yet.

**This does not reopen the typed column**, which section 7 records as rejected and which stays
rejected. Nothing here gives a path a SQL type or promotes it to a column; the row model is
untouched. A fact says how a value is *stored*, which is a narrower thing than a type and is the
thing `TODO.md` already asked an operand to carry under *Rewriting a typed comparison into one the
service can evaluate*.

### Consumers for `PreservesOrder` — *partly built in #106; what is left is below*

Two bits are set per representation and, until #106, only one was read. `PreservesOrder` says the
lexical order of the stored strings is the order Calcite compares in.

The full account of what #106 built, measured and deliberately left — including the wrong answer it
turned out to be fixing rather than a feature it was adding — is under *Rewriting a typed comparison
into one the service can evaluate* in section 4, which is where the argument lives. In this section's
terms:

- **Range comparisons** against a temporal literal, lowered to string comparisons with the literal
  rendered into the declared stored shape — **built**. `=`, `<>`, `<`, `<=`, `>` and `>=` over
  `CAST(<path> AS TIMESTAMP)` and over `JSON_VALUE(…, RETURNING TIMESTAMP)`. A literal finer than the
  stored shape is refused rather than truncated, truncation not being an equivalence.
- **`ORDER BY` with a `FETCH`** — the difference between reading a page and reading the container,
  which is the largest number in this whole area. **Half built, and the half that was missing was
  not the half this entry expected.** `JSON_VALUE(…, RETURNING TIMESTAMP)` already resolved to a path,
  so a sort over one was already pushing — *ungated*, as a lexical string sort the plan believed was
  chronological, which is a wrong answer over any path whose shape is not fixed.
  `CosmosSort.OrderIsLexical` now gates it. This entry's original case — a sort key that is a *chain*
  rather than a path, where the rewrite is to drop the order-preserving chain and leave the raw path
  — is now built for the `PARSE_` family and not for the rest: `PARSE_DATE(<format>, <path>)` and its
  siblings bind the ordinal through `CosmosProject.OrderingCandidateOf`, gated on the format reading
  the declared shape as well as on the two bits. The plain `CAST` and the `REPLACE`/`SUBSTRING`/`CAST`
  a view writes are still outstanding, and for the reason section 4 gives rather than for want of the
  mechanism — what the cast's column would *read back as* is its own question. See *Ordering by a
  rendered column* below, which is the same mechanism at a different site.
- **`MIN` and `MAX`**, which are the same argument over an aggregate — **not built**. The site is
  `CosmosAggregate` rather than the sort or the rewriter, and the condition is the statement-wide one
  for the same reason a sort's is: nothing rechecks an aggregate either.

### Ordering by a rendered column — *built; the temporal spelling is not, and the reason is recorded*

Projecting `CAST(<path> AS UUID)` renders as of #100, so a sort on a *neighbouring* column pushes.
The column itself binds to no path, a cast resolving to none, so `ORDER BY` on it was refused —
correctly, the guarded accessor not being the value and a stored order being the compared order only
where the form says so. Binding it meant recording that an ordinal addresses a path *for ordering
only*, gated on `PreservesOrder`: the same two-bit question as the entry above, asked at a different
site.

**Built.** `CosmosImplementor.Orderings` carries, per ordinal, the candidate a sort may order by
where the ordinal binds to none — recorded by `CosmosProject.Implement` as the binding walk derives
it — and the sort licenses it (`CosmosSort.OrderingPaths`, through `CosmosProject.IsOrderable`), at
the rule and again at implementation, of the facts the sort was decided under: what the filters
beneath it establish (L1), so the rule and the implementation cannot drift apart, and a form only a
kind declares licenses the order beneath a filter keeping that kind. Measured, with a `FETCH`:

```
SELECT VALUE { "id": (IS_PRIMITIVE(c.ref) ? c.ref : null) } FROM items c ORDER BY c.ref ASC OFFSET 0 LIMIT 5
```

— a page read at the service, against a whole container read in process before.

**And #142 is what made the first condition reachable for an ordinary container.** The form had to
preserve order, and only a `pattern` confining the first hex digit gave that — which in practice
meant v7 and nothing else, so every v4 identifier and every unconfined declaration was refused and
the sort ran in process with the projection collapsing to whole documents beside it. That was the
signed comparison, and [CALCITE-7716](https://issues.apache.org/jira/browse/CALCITE-7716) removed it
in 1.43; `CosmosStoredForms` now reads `calcite.uuid.unsigned.comparison` and the unconfined rows
carry `PreservesOrder` from it. Measured in the report: ordering a 9,370-row view by its key did not
return in 120 seconds in process. Nothing in this entry's machinery changed — the condition it asks
is simply now met.

**Two conditions, and the second was not in this entry's original statement.** The form has to
preserve order, which is the two-bit question above. And the guard the projection renders has to be
*vacuous*: the column comes back as `IS_PRIMITIVE(c.ref) ? c.ref : null`, so over a document holding
an object at the path the column is null while the path is the object — and Cosmos sorts an object
above every scalar while null sorts below them. A path declared present and a scalar admits no such
document, which is the same claim the null-placement rule already makes of any sort key;
`CosmosServiceOrder`'s null class (R3) is now where both ask it.

**The temporal spelling is _not_ closed by this, and the obvious reading that it is was wrong.**
`ORDER BY CAST(<path> AS TIMESTAMP)` looks like the UUID case with a different type, and the sort
machinery would carry it — `OrderingPathOf` admits a temporal cast and the form licenses the order.
The obstacle is a step earlier: the projection does not push, so there is no `CosmosProject` to
record the binding on. Measured:

```
ClrCursorSort(sort0=[$0], dir0=[ASC])
  ClrCursorProject(at=[CAST(JSON_VALUE($0, '$.at')):TIMESTAMP(0)])
    CosmosToClrCursorConverter
      CosmosTableScan
```

#100 made the `UUID` cast renderable as a guarded accessor; nothing has done that for a temporal one,
and section 6 records why it is not the same job — `CAST(<string> AS TIMESTAMP)` accepts only
`yyyy-MM-dd HH:mm:ss` and raises on every ISO-8601 form a document stores, so what such a column reads
back as is its own question. A temporal sort is not unavailable meanwhile: the `RETURNING TIMESTAMP`
spelling binds to the path directly and pushes, gated on the same bit.

### The value claims are read as conclusions now, and only the first of three

`EqualTo`, `OneOf` and `NotEqualTo` were produced from `const`, `enum` and a discriminated `oneOf`,
carried through `Entails`, and used as **premises** — what a query proves, to unlock a guarded fact.
Nothing read one as a **conclusion**, as a statement about the data that changes a plan. The same
shape `PreservesOrder` was in before #106, and not recorded here either until now.

**Built: a contradicted predicate keeps nothing.** `JsonFact.Excludes` is the exclusion table beside
`Entails` — a separate table because exclusion is not entailment's negation, most pairs being neither,
and `JsonFactSet.IsContradictory` asks it of every path. Where the container's declaration and the
query's own conjuncts cannot both hold, `CosmosFactRewriter` answers the constant. Declare
`status` as `enum: ["active","archived"]`, ask for `'deleted'`, and the plan carries
`CosmosFilter(condition=[false])` rather than a comparison. A guarded declaration reaches the same
answer by the argument it always uses: over a document the guard does not cover the conjunct that
proved it has already excluded the row.

**Built since: it costs no round trip.** The constant was reached inside the converter, so it landed
on a `CosmosFilter`, and `CoreRules.FILTER_REDUCE_EXPRESSIONS`, configured for a `LogicalFilter`, never
saw it. `CosmosFilterReduceRule` now matches the logical filter, asks the predicate oracle whether it
keeps anything, and puts the empty relation beside it, which a host with a rule for an empty `Values` answers
with no request (`AContradictedPredicateIsReducedToTheConstant`). The paragraph below is what a planner
without such a rule still does.

**And one thing is measured only on CI.** The constant renders as `WHERE @p0` with a boolean
parameter, because the translator binds every literal rather than inlining — deliberately, so that
statement text is independent of data. Whether Cosmos accepts a lone bound boolean as a whole `WHERE`
clause is a question about the service, and no service-backed class declared a schema on a container
it could query, so nothing reached the path. `CosmosConnectionFunctionTests` now builds a second
container, `catalog`, declared with an `enum`, and asks it for a value outside the domain; a sibling
test asks for one inside it, so an empty answer is the contradiction rather than an empty container.
Both need a service and report inconclusive without one, which means the `linux-x64` leg is where they
actually run. The failure mode being guarded against is narrow but real: the rewrite turns a query
that was merely slow into one that does not run.

**Also built: a declared tautology is not asked.** Where the declaration and the other conjuncts make
a conjunct true of every document, it decides nothing, and `CosmosFactRewriter` deletes it — asked of
the predicate oracle under the deletion's licence, every sibling and never the conjunct itself, one at
a time. It was an equality whose path the declaration made present and that value; it is any shape the
oracle decides: an `IN` covering a required enum, a `<>` outside one, `IS NOT NULL` of a required
scalar, a range a `CAST(… AS DOUBLE)` reads inside the declared bounds, a disjunction holding a sibling
for a branch. `required` is still the whole of the argument: a declared value says what a path holds *if
it holds anything*, and a document with no `kind` keeps no row of `kind = 'A'`. Where every conjunct
goes, `CosmosFilterReduceRule` puts the filter's input in its place, and the statement has no `WHERE`.

**The payoff this was predicted to have, it does not have, and that is worth recording.** This entry
said it would unblock a point read, on the reasoning that `TryExtractPointRead` refuses any conjunct
that is not an `id` or partition-key equality — so a redundant conjunct stood between a routed query
at 2.82 RU and a point read at 1.00. Measured, the read is reached either way: `CosmosPointReadSplitRule`
already partitions the conjunction and holds the extra conjunct back, which is what #92 built it for.

What changes is that the conjunct held back was then **rechecked in process for every row the read
returned**, and a conjunct no document can fail has nothing to recheck. Measured, over the same query
with and without the declaration:

```
declared:  CosmosToClrCursorConverter … (the conjunct is gone)
plain:     ClrCursorFilter(condition=[=(JSON_VALUE($0, '$.kind'), 'A')])
             CosmosToClrCursorConverter …
```

A plan node and a per-row test rather than a request. Smaller than this entry claimed, and still worth
taking — and the statement sent is one predicate shorter either way.

**Also built: a declared partition key routes.** `CosmosPartitionKeyExtractor` reads the key off
what the container declares as well as what the predicate pins — so a query pinning only an `id`
supplies the key and reaches a **point read**, which is the saving the tautology entry above was
predicted to have and does not. Measured, over the same query:

```
const + required:  pointRead=[x]  pk=[<value>]  complete=True
const alone:       pointRead=<null>  pk=<null>
nothing declared:  pointRead=<null>  pk=<null>
```

About 1 RU against 2.3 at best for the query, before the fan-out the query would also have paid.

**The presence claim matters more here than anywhere else it is asked.** A partition key *routes and
does not filter*, so supplying a value some document does not hold does not return fewer rows — it
returns rows from the wrong partitions, which is to say none of the right ones. A `const` without
`required` still admits a document with no such property, which Cosmos places in its own partition,
and routing past it would lose it.

**Now the oracle's, and a guarded fact routes too.** The pin is `Present` and one value in the facts
of every conjunct and the declaration (`OracleLicence.Kept`): the statement routed still applies its
whole predicate, so a partition it skips holds no row the predicate keeps, and a key a sibling's kind
declares is as safe to route on as one the predicate names — `kind <> 'b' AND kind <> 'c'` over a
required enum routes to `a`. A point read, a batch of them and a whole-partition delete apply no
predicate and read only the conjuncts that pin their keys, with every other conjunct `Always` there
(*The predicate oracle*, below; `DESIGN.md` under *The oracle in the planner*). A declaration
disagreeing with the predicate describes a document the predicate excludes, which the oracle reads as
a scope that keeps nothing and pins nothing, and the rewriter answers with the empty relation.

**Which leaves the fan-out measurement below as the thing this is priced on, still untaken.** All
three conclusions a declared value supports are now read; what none of them has is a number from a
container with more than one physical partition.

### A declared type makes a parameterised comparison exact — *built, and it took the sibling with it*

`WHERE <path> = ?` was weakened to a definedness test even where the container declared the path a
string. The weakening exists because the accessor renders every JSON scalar as text, so an equality
against text is exact only where the text is one no number and no boolean renders as — a fact about
the *value*, and a parameter has none. A declared `type: string` settles it from the other side: if
the path holds a string and nothing else, the rendering *is* the stored value and the comparand need
not be inspected at all. The equality branch of `WriteComparand` now makes the move the ordering
branch beside it has made since #93.

**The sibling in the entry below went with it.** A literal `= '30'` against an undeclared path pushes
as `(c.t = '30') OR (c.t = 30)`, the alternative covering a stored number the accessor renders as
`'30'`. Where the container says the path holds a string there is no such document, and the same
check deletes the alternative.

**The first attempt was unsound**: the facts a comparison was rendered from held what it established
itself, and `= '30'` certified itself exact (#117). The strata make that unrepresentable; `DESIGN.md`
under *Which facts may justify what*.

**A declared instant took the parameter too (#182).** A comparison of a stored instant against `?` is
written in the path's own spelling when the statement runs, the slot carrying the form and the way a
value between two spellings rounds — `DESIGN.md` under *A prepared statement's value belongs to the
execution*. A declared UUID took it next (#207), the slot carrying the UUID form and the value spelled
into it when it arrives. The numeric forms still lower against a literal only: the same move would
take them, with an integer written into its width, and nothing has asked.

### A null parameter is not SQL's null at the service — *done; its cost on Azure is owed*

A parameter is bound as it arrives, and a SQL `NULL` arrives as a JSON `null`, which the service compares
as a value. Measured against the emulator, over one path holding `true`, `false`, `null`, a string, a
number and nothing:

| | keeps | SQL keeps |
| --- | --- | --- |
| `c.flag = @n` | the document storing `null` | nothing |
| `c.flag != @n`, `NOT (c.flag = @n)` | every document holding something other than `null` | nothing |
| `c.flag > @n` | nothing | nothing |
| `c.t <= @n`, `c.t >= @n`, `c.t BETWEEN @n AND @m` | the document storing `null` | nothing |
| `c.t IN (@n, 'apple')` | `apple` and the document storing `null` | `apple` |
| `c.t NOT IN (@n, 'apple')` | every document holding something other than `null` or `apple` | nothing |

So every parameterised equality, inequality and negated equality answered rows a null parameter should
not, and so did `<=`, `>=` and `BETWEEN` over a path that may hold a JSON null, `IN` and `NOT IN` with a
parameter among the values, and an `OR` beside any of them: measured again over the differential's
`ordering`, whose `t` is declared a string or null, `t <= ?` and `t BETWEEN ? AND ?` bound to nulls kept the
document storing a null, `t NOT IN (?, 'apple')` every other, and `t = ? OR s = 'a'` the null one beside
`a`. The guards `WriteComparison` wrote were about a null at the *path*.

**Done: the parameter is guarded too**, in every position its operator would keep a row for a JSON null —
`NOT IS_NULL(@p) AND` before `=`, `!=`, `<=` and `>=` in a positive position, `OR IS_NULL(@p)` after `=`,
`!=`, `<` and `>` under a `NOT` (`DESIGN.md`, *A prepared statement's value belongs to the execution*). The
differential's parameter rows bind each shape to a null and to a value. **Still owed:** what the test costs
on Azure, where it is a constant the service may or may not fold before it picks an index; not measured. A
host that rewrites `= NULL` into `IS NULL` before it sends anything, as Entity Framework does, never sends
one, which is likely why nothing had reported it.

### The numeric forms stop at whole numbers — *small, and deliberate*

A fixed-point decimal spelled as a string — `^[0-9]{5}\.[0-9]{2}$` — is injective and its lexical
order is numeric order, by the same argument the padded integer uses, so it belongs beside them. It
is not built because it needs a second dimension on the form (a scale as well as a width) and its own
rendering, and nothing has asked yet. The approximate types are a different matter and are refused on
purpose: a stored spelling maps to one `DOUBLE`, but a `DOUBLE` maps back to many, so a literal could
not be written into the container's shape without changing which documents match.

The stored-number disjunct a lowered equality carried beside a declared string is gone: a declared
`type: string`, or a sibling's kind declaring one, makes `label = '30'` exact from the stratum it is
rendered from, and the oracle's rendering preimage meets a declared string down to the string.

### Facts about array elements — *small, and a geography's proof waits on it*

`JsonDocumentPath` carries property names only, so `items` and `prefixItems` state nothing. The
path model has room for an element segment and `ARRAY_CONTAINS` and the traversal are the rules that
would read one. Not built because nothing asks yet, and a path model that admits an index has to
answer what `$.tags[0]` means against a fact declared for `items`.

**A geography's proof is the one reader that asks now.** `CosmosGeographyForms` reads a subschema's
`minItems`, `maxItems`, `prefixItems`/`items` and its elements' `minimum` and `maximum` itself, a second
reader of the schema and a derivation in C# of "the service will measure this value"; it reads `type`,
`required`, `const` and the bounds through `JsonSchemaFacts`' own readers. What has to move: the schema
reader states the element facts — `prefixItems` as `Index` paths, `items` as `Each`, the counts as
`minItems`/`maxItems` and the elements' bounds as `lo`/`hi`, all of which the theory already has — and a rule
of the theory derives the geography from a pinned `type`, both members present, and the coordinates' counts
and bounds, the measured ranges (±180 and ±90, `CosmosGeographyValidityMeasurementTests`) stated as data.
Then `CosmosGeographyForms` goes. Not done in the review of `feature/allowable-orders`: it needs the element
segment above first.

### The keywords still not read — *small each, and none of them blocked*

`additionalProperties`, `patternProperties` and `propertyNames` constrain paths that cannot be named
in this path model. `minimum`, `maxLength` and the rest are bounds, and no claim in the model is
about an interval — adding one means adding the rule that reads it at the same time. A `$ref`'s
sibling keywords are dropped, which is Draft 7's rule and conservative under 2020-12.

### A nullability claim — *small, and it would recover a common case*

`{"type": ["string", "null"]}` and OpenAPI 3.0's `nullable: true` both state nothing today, because
`OfType` would be a claim a stored null violates. Both are extremely common, and both would be
readable as a type plus a nullability claim rather than as nothing. What is missing is the claim, and
a consumer that cares about the difference.

### A point read on a discriminated container — *done, by #92 rather than by anything here*

Written down because this branch's design note called it blocked and it is not. A point read applies
no predicate, so `TryExtractPointRead` refuses any conjunct that is not an `id` or partition-key
equality — and the discriminator conjunct that licenses a guarded fact is exactly such a conjunct, so
the chain used to end one step early at a routed query.

`CosmosPointReadSplitRule` closed it without relaxing that standard: it *partitions* the conjunction,
so the pinned equalities reach the read and the discriminator is held back and applied above. The two
features compose without either knowing about the other — by the time the rule runs, a lowered
comparison is an ordinary string equality. `ALoweredComparisonUnderAGuardStillReachesAPointRead` is
the proof.

### The fan-out measurement — *the number the whole feature is priced on, and it is not taken*

What a declaration is worth was measured on a serverless container with one physical partition, which
prices the index question and not the routing one: 33.55 RU read whole, 3.12 with a schema-free
weakening, 2.82 exact, 1.00 as a point read. The case-insensitive form **used the index**, so most of
the saving is in pushing anything at all.

What is unmeasured is the fan-out, which is where the exactness should earn a large number: on a
container with several physical partitions, `WHERE c.pk = '<lower>'` against
`WHERE STRINGEQUALS(c.pk, '<UPPER>', true)`, in RU and partitions contacted. It needs a provisioned
container above the throughput at which Cosmos splits; the probe account is serverless and cannot
answer.

### One smaller one

- **A schema carried by reference** rather than inline. Inline is the right default and the manual
  says why, but a long schema buries the operands beside it, and a path or URL wants deciding — a URL
  being a fetch at schema registration.

### The fact reasoning on Eggnog — *done; what is left is below*

[Eggnog](https://github.com/alethic/eggnog) (`Eggnog` on NuGet, pinned at `1.1.1`, MIT, no dependencies) is
the engine. It is an egglog-style e-graph with scopes, lattices and case splits, built for this adapter. The plan
of record is in its repository, as
[`docs/history/cosmos-integration.md`](https://github.com/alethic/eggnog/blob/eda0c61973cb1e41fc0d9994af3e013a9252c4bd/docs/history/cosmos-integration.md)
(revision 2, read here at `eda0c61`). This entry records where the adapter stands against that plan.

**What is built.**
- **The theory is under `Facts/Saturation/`,** internal to the adapter, and its tests are the adapter's. Its
  `README.md` is the catalogue. `DESIGN.md`, from *The theory of JSON, in layers* through *Generations, and what a
  question costs*, is the record.
- **The Horn closure is gone,** with the exclusion table. `JsonFactTheory` compiles a declaration into a frozen
  context; `Derive` saturates a query's established facts beneath it; `JsonFactSet` reads the answers back. The
  public API is unchanged, and `ClaimsFor`'s two scans became questions: `HasOneScalarType` and `TryGetValue`.
- **Generations.** One graph per schema object and generation, retired at a million interned values or half its ids
  or epochs (g07). Each query
  context is remembered per declaration for the next 256 established sets, and released once nothing holds it.
- **The graphs are the schema's, and nothing else's.** Each schema object's `CosmosReasoning` holds its generations
  and the service's facts bound to them, and each bound theory its oracle; a theory bound to none compiles nothing and
  says so. `JsonFactGenerations.Default`, its binding table and the oracle's own generations and table are gone, and
  with them the pin the binding table's static key put on every factory schema's graphs. A host's schema, or a table
  built alone, now compiles its own declarations: its first plan pays that compile, as the rule asks. No static holds
  what a question taught (`CosmosFunctionBodies.Paths`, the last, is parsed per call since #243), and
  `CosmosStaticsTests` keeps it so: every static field of the adapter is classed with its
  reason, and one not classed fails; a static's type is followed through the fields it can hold, its value walked
  after a schema has worked and gone, and each weak table names the test that finds it released.
  `CosmosConstraintCompiler`'s table, keyed on whatever type factory a caller passes, is weak on the table too, and
  `CosmosFilterStatement`'s, keyed on whatever condition a caller passes, weak on the container, so a long-lived
  factory or condition keeps none.
- **The extractor states `Present` beside every comparison** (F4's query half). The schema reader states it in
  four guards (F4's other half): a discriminated branch, an `else`, the object branch of a nullable union, and a
  nested `not`.
- **Fixed on the way:**
  - F3: absence is no longer a contradiction;
  - F2: an equality with text a number renders as is no longer read as a stored string;
  - F5: `ReferenceEquals` on a struct.
- **Gaps closed**, each with a control, in `CosmosDeclaredFactPlanningTests.Gaps.cs`:
  - gap 1, `kind IN (…)`, by a query split;
  - gap 2, a disequality narrowing a domain;
  - gap 3, an `anyOf` of enums, by one `(OneOf, OneOf)` row in `JsonFact.Entails`, the meet's;
  - gap 4, a range beyond a declared `maximum`, and gap 5, a range outside an integer `enum`. `JsonSchemaFacts`
    reads `minimum`, `maximum` and both spellings of their exclusive forms as `LowerBound` and `UpperBound` claims,
    which bind numbers only. The extractor reads a comparison through a numeric cast as the window
    `PredicateAtom`'s numeric cast states (`CosmosNumericWindow`'s until g19), which S states and does not send.
- **F10's window, asked of every comparison.** The window states a side only where the cast's saturation limit
  fails the comparison, where it used to ask that of an equality alone. No literal reaches the window with a
  bound beyond the opposite limit today: Calcite widens the cast to compare one, a cast of a cast the loader reads
  as opaque, so nothing is sent for it (`ACastTheLoaderReadsAsOpaqueSendsNothing`).

**The gate is the adapter suite and the theory's soundness oracle**, not agreement with Horn. Horn's known wrong
answers (F2–F4) are not the specification. The oracle covers stored forms, extension claims and numbers no decimal
holds. Three bugs planted in what was added were each caught by it.

**What is left, roughly in order of value.**
- **A bound from a comparison on a typed path** — *small, half measured.* `c.price > 500` over a promoted
  `VARIANT`, or `JSON_VALUE(… RETURNING INTEGER) > 500`, states only that the path is there. A bound needs the
  comparison to keep the stored number on its side of the constant. For a `RETURNING` that asserts it is now
  measured: it converts nothing, so a row it keeps holds a number on the comparison's side of the literal
  (`EachNumericOrBooleanReturningAssertsItsClassButDecimal`). `DECIMAL` parses, and states its window already.
  Calcite's ordering of two `VARIANT`s of different numeric types is still unmeasured.
- **A bound inside a condition** — *small.* An `if` naming a `minimum` is not read, which drops its `then`. The
  theory's rows have no bound atom yet.
- **The other bounds** — *small each.* The theory has `minLength`, `maxLength`, `multipleOf`, `minItems` and the
  rest, and the adapter has no claim for them, so the reader states none. The lengths have a reader waiting:
  `REPEAT` and `REPLACE` ask one of their operands (above, *A string function pushes only where its characters are
  proved*).
- **Gap 1 spelled per kind** — *done.* `(type = 'park' AND …) OR (type = 'map' AND …)` proves what both kinds
  declare, by the predicate oracle's split over the arms, which the rewriter asks; a UUID comparison beside it is
  lowered (`CosmosPushdownProbeTests`' "each kind spelled per arm", held to Calcite by `CosmosDifferentialTests`).
- **The schema reader emits splits rather than a meet** — *large.* A hidden discriminator per undiscriminated
  `anyOf` (`JsonPathSpec.HiddenNamed`, the plan's §2.6), and the discriminators and conditional rules stated
  directly. `JsonSchemaFacts.Meet` and `JsonFact.Entails` then go. `Meet` is quadratic in a `oneOf`: 7.5 s to read a
  33k-rule schema and 76 s at 109k, against 0.8 s to compile one into the theory. At that size the reader is the
  bottleneck. **It is also where facts are still combined outside the theory, against the owner's rule**
  (`DESIGN.md`, *Characters, a fact of the theory*, *What is left in C#*), and this is exactly what has to move:
  - `JsonSchemaFacts.Meet`, which keeps a claim of every kind — a type, a domain joining the branches' constants, a
    bound, a form, characters stored and written — where every branch entails one, by the rows of
    `JsonFact.Entails`, which read a value's type through `JsonScalar.Type` beside the theory's `JsonValues.TypeOf`;
  - `AdmitNull` and `NullableBranch`, which widen every fact of a nullable union's one branch to admit the null —
    a type to its `OrNull`, a domain by a null, a presence and a geography under the union's presence and
    non-null — and which are a hidden discriminator with a null case;
  - and with them `JsonFact.Entails` itself, which nothing else asks.

  Stated as a hidden discriminator's cases, the theory's lift joins what the branches say as it joins a
  discriminated union's, and widens nothing by hand. Not done in the review of `feature/allowable-orders` because
  it is the reader's rewrite the plan sizes as *large*, and every rule a union states would move with it.
  Population D draws its declarations as claims, so the meet and the nullable widening are fuzzed only by the
  schema reader's own tests; drawing some declarations as JSON Schema text through
  `CosmosSchemaRecognition.ReadFrom` would fuzz them end to end until they go.
- **The overlay restated the theory** — *done: retired on Eggnog 1.1.1, by the owner's decision.*
  `JsonFactOverlay` answered a stratum over a declaration with no conditional rule — every container that declares
  no schema — without opening a context, and to do it restated seven rules of JSON and the oracle's establishing
  rules in C#. The plan kept it until a schema-less question cost a millisecond or less through the engine; on
  1.1.1 one costs 1.4 ms at the median over the differential's statements and 4.9 ms over the corpus's. Retiring it
  costs about 0.12 s (3 %) on the corpus planned new and nothing planned again, and 1.1.1 without it is level with
  1.1.0 with it. The owner moved the line. Every stratum is the engine's context now.

  The corpus, the differential's 3,244 statements and the sort inventory's 17,107 plan alike with it and without,
  but they hold no clause beneath a scalar the filter pins, and the overlay derived what the engine did not there:
  a stratum mentions only what its conjuncts read, so `ORDER BY name.first` over `WHERE name = 'x'` asked of a path
  the engine was never told of, and 110 of 720 crafted probes planned otherwise, 30 sorts no longer pushed. A
  context's tables now tell it of a path the first time they are asked about one it has no term for
  (`JsonScopeTables`, `JsonContexts.Mentioning`), and the rule that a path beneath one holding no object is absent
  says so. Each of the 110 plans as it did beside the overlay, the inventory still plans as it did, and 78 more push
  what neither build did — a key beneath `id`, which the service states a string, or a path beneath a scalar a
  declaration states — each answered on the emulator as Calcite answers it. The corpus planned new costs what it
  did, 4.61 s against 4.60 s at the median of five rounds.

  What is left in C# of the facts about the documents is the meet and the nullable widening (above), a geography's
  proof (*Facts about array elements*), a disequality or a bound read off what a domain or a path's types leave out,
  and a predicate a context assumes read as decided before any run (*A verdict that rests on no run*, below)
  (`DESIGN.md`, *What is left in C#*). `PathValues.Normal` no longer restates the rules of JSON — the tables its
  lattice reads are already narrowed by them — and keeps only the fold of an exclusion into a known domain, the
  value's own spelling, which the lattice's laws need; and `JsonContexts.Decide`, a C# reading of a context's
  verdict on an atom that nothing in production asked, is the theory's tests' own (`ContextVerdicts`).
- **A verdict that rests on no run** — *medium, and it waits on the engine.* `StatementTerms.Judge` first asks
  `Node.Assumes`: a predicate the context assumes, or a conjunction of predicates it assumes, is always true there.
  That restates two of `PredicateRules`' Decide rules — *a predicate that holds true of every row is always true*, and
  *a conjunction of truths is true* — in C#, on purpose: a context whose run stops on a limit is discarded and its
  node reads its parent's, which assumed less, and an answer of *no* for want of a run is what made the split rule
  push a weakening under itself for ever (`DESIGN.md`, *What a context assumes, it has decided*). It is live through
  `CosmosFilterStatement.IsApplied` and `CosmosStatementScope.RedundantConjuncts`. What has to move: the verdicts of
  what a context assumes must survive a run that stops — a context opened in two runs, the assumptions and those two
  rules over them first, a run bounded by the terms assumed, frozen beneath the full run, so that an incomplete second
  run leaves the first to read; or an Eggnog that keeps a stopped run's sound part readable. Then `Assumes`, `Holding`
  and `Hold` go, and `Judge` reads every verdict off a context.
- **What a small declared container costs** — *medium.* Every declaration opens a context for every distinct
  established set, four a plan where the split rule fires, and one with no conditional rule — which every container
  that declares no schema has — does too, at a millisecond or a few each (*The overlay restated the theory*, above).
  Against the closure, on one workstation, a filter over a discriminated `oneOf` costs 19–26 ms a plan at two kinds
  and 25–34 ms at twenty, against 10–13 and 14–15; the engine is ahead at two hundred kinds (10 ms against 41) and at
  a thousand (23 ms against 78). The first plan in a generation also compiles the declaration: 0.4 s at two hundred
  kinds and 0.6 s at a thousand, against the closure's 65 and 190 ms. The plan's gate (§3.6) extends to these only
  as rules of the theory, as relevance is (`Facts/Saturation/README.md`, *A schema's conditional statements*): an
  established path no conditional rule's body reads, directly or through the path-dependency closure, cannot
  change an answer, and a gate that decided so in C# beside the engine would be the overlay again. The memo helps
  less than it might: four sets a plan, keyed by literal, fill 256 entries in sixty-odd distinct statements.
- **Interning bounded by the vocabulary, not the traffic** — *medium.* The plan's §2.1 renames a query's literals
  outside the declared vocabulary, which bounds interning and makes memo keys independent of literals. A generation
  is retired instead today, which bounds memory and not the rate of rebuilds.
- **Compiling off the planning thread** — *small.* A declaration compiles on the first question in each generation,
  on whichever thread asks: tens of milliseconds for a small schema, 0.8 s at 47k facts. The plan's §3.4 compiles and
  warms each declaration on a background worker, and swaps a generation only once every declaration is ready.
- **One context per declaration, not per container** — *small.* An account schema applies one declaration to a
  container in every database, and each still compiles its own.
- **`PredicateRules` defines under one lock for the process** — *small.* `PredicateRules.Of` locks its static table,
  keyed weakly on each graph's `JsonRules`, so every schema's first question in each generation waits on every
  other's definition. The table holds nothing past its graph; the lock is the shared part. A lazy field on
  `JsonRules` would take both away.
- **Run limits** — *small.* A query's run stops on contradiction and otherwise has the engine's defaults: 10,000
  iterations, 64 cases, a split depth of 4. The plan's §3.8 proposes limits per profile, measured on a corpus.
- **The declared differential corpus** — *medium.* The rows the service returns for a pushed plan, against an
  in-process evaluation of the same query, over containers with schemas. The plan's step 1, still owed.
- F7, alias-blind consumers, wants the tests over `CosmosUnnest` aliases the plan names.
- F9, documentation drift, wants re-reading against what is here now.
- **What Eggnog adds beyond the closure** — *large, each its own decision.*
  - The rewriter as extraction under an RU cost.
  - Path equality for `CHECK`. The theory has it: a union of two path terms.
  - One context shared between the implementation plan and the execution plan.

**Pin the exact Eggnog version as Calcite is pinned.** It is a single-maintainer prerelease whose public surface is
pinned by a test and only grows. A wrong lift drops rows with no error, which is the same blast radius as a wrong
schema (*A declaration is trusted*).

### The predicate oracle — *in the planner; what is left is below*

Eggnog decides what is true, the host what is written and what is legal, Volcano which plan. The oracle was built
and measured in shadow (Stage 1), and now answers every question the planner asks of a fact: the filter rule's whether
it pushes whole, the implementor's rendering, the split rule's which conjuncts push and which weakening, the rewriter's
contradiction, deletion and lowering, the translator's every question of a type; the clauses after a filter, what a
statement pins and whether a read answers it, and a self-join's and a lookup join's keys. `JsonFactTheory.Derive`, the
second engine for what a filter establishes, is gone: the theory states its declaration and compiles it for the oracle,
and the test assembly keeps a reference evaluator of established facts for the theory's own tests. Every scope a
question reads is a row of the licence table, a translator is always handed the facts it reads, and each converter
rule reads only its own container. `DESIGN.md`, from *Predicates as facts: the oracle* through *How the oracle is
checked: population D*, is the record; `Facts/Saturation/README.md` is the catalogue.

**What is built.**
- **The typed IR's predicate subset** (`Facts/SqlPredicate.cs`) and its loader (`Metadata/CosmosPredicateLoader.cs`):
  the text accessor, the raw value, `RETURNING`, the numeric cast and the cast to text as readings of their own;
  comparisons with a literal or a parameter, `IN`, `IS [NOT] NULL`, `IS_DEFINED`, `AND`, `OR`, `NOT`; everything else
  opaque. `RETURNING DECIMAL` is read as the translator reads it, and the constants a conversion is compared with by
  the loader's own `TryConstantNumber`, `CosmosNumericWindow`'s until g19 deleted it.
- **`PredicateRules`**, 45 rules and a split beside `JsonRules`. Every constructor is unextractable.
- **`CosmosOracle`**: a tree of contexts per statement remembered by digest, the strata, the licence table with the
  arm of a `CASE` and the rows a whole filter keeps beside the plan's rows, what a scope assumes read before any run,
  every stratum the engine's context, a schema-less declaration's too, and the planner's own graphs.
- **`CosmosFilterStatement`**: a filter as the oracle reads it, remembered against its condition node, and the
  questions each rule asks. `CosmosFactExtractor`, the split rule's two passes and its digest test of what is
  beneath, the rewriter's fact half and every `Derive` at a filter site are gone, and so are the translator's four
  questions of a type, now one.
- **The clauses after a filter** (`Metadata/CosmosRowFacts.cs`): a projection, a sort, a grouping and a lookup join's
  key read the last stratum of the filters beneath (L1), decided once by the rule and carried on the node it builds.
  Calcite's pulled-up `IS NOT NULL` (`FindNonNullFields`) and the `Derive(null)` at each site are gone. A `DISTINCT`'s
  key binding, which another node asks on its own walk, reads the declaration.
- **Pins** (`CosmosPartitionKeyExtractor`): `Present` and one value, of every conjunct for routing and a row count and
  of the pins alone for a point read, a batch and a whole-partition delete, every other conjunct `Always` there. The
  extractor's hand reading of equalities, its declared seed and its "says nothing else" walk are gone.
- **The self-join's two documents**, the part of Stage 4 that fell out: a constraint's predicate decided `Always` in
  each side's statement (`CosmosFilterStatement.Proves`), where Calcite's `RexImplicationChecker` over abstracted
  terms decided it; a key's faithful reading asked of what both sides' filters establish, the oracle's split over
  their disjunction.
- **Tests**: the oracle's unit tests; `CosmosOracleReplayTests`, the family's defects answered by the general rules
  through the planner's statement, two of the shadow's rows corrected; the population-D checker and the planted-bug
  suite, asking the arm and the whole filter's licences too; and the kinds container in `CosmosDifferentialTests`,
  holding what the oracle pushes that the planner before it did not to Calcite on the emulator.
  `CosmosPushdownProbeTests` and `CosmosPlanningTimeTests`, explicit, put a change beside the commit before it.

**What is left.**
- **Text sorts the rows' facts give the service are ordered by code point there** — *the open defect in section 4,
  widened.* A key a filter proves present sorts under Calcite's own placement, and a kind's string as a declared one
  does; three divergences record it. The decline waiting on that decision is one place, the text exception of
  `IsReadAsStored`.
- **A batch of point reads lost to a pushed `DISTINCT`** — *small, a cost-model choice.* Corpus row P21, `SELECT
  DISTINCT category … WHERE category = 'bikes' AND id IN ('1', '2')`, read the two documents and de-duplicated in
  process on `main`; with the grouping reading the rows the filter kept, the `DISTINCT` pushes, and Volcano prefers
  one routed query with it to the batch. The rows are the same. Pricing `ReadMany` below a query where it reads a few
  ids would bring the batch back; nothing measured says which is cheaper for two documents.
- **The atom's readers are strings** — *small, no behaviour.* `typed:T` and `cast:T:inner` are parsed back by
  `Reading.Parse`, restating the IR's `SqlValue`, because the rules carry a reader as a string value. A reader
  registered as a record value would state it once. (What a conversion does is read off its target once, in
  `NumberCast`'s constructor: saturation, rounding, a parse that raises, a decimal's exact parse.)
- **A `DISTINCT`'s key binding reads the declaration** — *small, and it waits on the statement node.* The binding is
  asked by the sort's rule on its own walk and again at implementation, so only what holds of every document answers
  alike on both; a key only the filter proves present binds nothing, and a sort over the `DISTINCT` stays in process
  (`CosmosDeclaredFactPlanningTests.ADistinctBindsItsKeyByTheDeclaration`). A statement node carrying the scope it
  was decided in (#148) lets it read the rows.
- **A value aggregate over a path the filter proves present** — *small, a pushdown not taken.* `SUM`, `MIN`, `MAX`
  and `AVG` push only over an input the plan types non-null (`CosmosAggregate.CanImplement`); the service answers
  undefined where one row's value is null or absent, so `WHERE x IS NOT NULL` would settle it as it settles a sort's
  null placement, and needs a type too (the null class `CosmosServiceOrder.Extreme` reads, and the number the
  aggregate reads). The grouping reads L1
  already; the calls do not.
- **Deletions inside a conjunct** — *small, and the measurement says not yet.* The rewriter deletes whole conjuncts,
  one at a time, and the shadow found 2,263 `CASE` conditions on the hierarchy that a sibling conjunct states.
  Measured with a prototype that took a `CASE WHEN p THEN q ELSE FALSE END` as `q` where the kept siblings assume
  `p`, read off the scope's premises with nothing opened: asked 624 times over `MapLinks` and 1,776 over
  `MapLinksByMapName`, and taken none. So the copies are in shapes the flattening does not take apart — a `CASE` of
  more than one arm, or one reached through a comparison or a null test, as the hierarchy's discriminator column is
  written; not attributed further — and taking them wants a general arm selection in the rewriter. Not built.
- **Two orderings lowered on each other** — *small, and a declaration has to be built for it.* The rewriter lowers a
  comparison onto its stored form by what its siblings establish (L3, of the conjuncts the deletions keep). An equality
  lowered so is a strengthening — a stored spelling that matches is a value the cast reads as the literal — but an
  ordering is not: text past the stored form's bound need not be a value of the form at all. So two conjuncts whose
  facts each prove the other's stored form could each be lowered on the other, and a row both originals drop pass both
  lowered forms. It wants a declaration in which each path's form hangs on the other's value, which nothing in the
  corpus or the tests has; the strata would settle it as they settle rendering, an ordering lowered only on the
  conjuncts lowered before it.
- **The translator over the IR** — *large, Stage 3a's other half.* The numeric window was `CosmosNumericWindow`'s and
  the atom's both, agreeing through one reading of the constant. Since g19 it is the atom's alone, which S states and
  does not write (O7, below), and the constant is the loader's; a translator over the IR would write it from there.
- **What the checker does not reach yet** — *small each.*
  - **The raw readings against Calcite.** A `VARIANT` is the adapter reader's, so C2 needs the reader's
    materialisation of a document, or the emulator; until then the evaluator draws them from the table, which tests
    the reasoning over those rows and not the rows.
  - **The generator's declarations are flat**: one discriminator, five paths, one of them nested; no array
    elements, forms, extensions or path equalities. C5 compares nothing where nothing was unioned, so only a planted
    union has exercised it.
  - **The corpus as population D's workload.** The 78 planner statements, loaded, over documents drawn from
    `PlannerSchema`'s declarations, is the C5 run the plan asks for per PR.
- **What a new statement costs** — *small, and the lever is known.* Measured with `CosmosPlanningTimeTests`, Debug,
  interleaved with `origin/main` (`1a55518`) on one busy workstation, six runs of the corpus and three of the
  hierarchy; a run's own spread is as wide as the difference, so read the medians:
  - **The corpus, each statement new** (78, planned and implemented over containers kept between statements): 8.8 s
    in all on `main` and 11.3 s here; p50 53 ms against 59 ms, p90 203 ms against 360 ms. Planned again it costs what
    it did, 5.3 s against 5.4 s, p90 84 ms against 74 ms.
  - **The self-join hierarchy is faster**, through a connection: `MapLinks` 9.1–9.7 s on `main` and 8.0–9.4 s here
    over containers built for each plan, 9.2–9.6 s and 6.9–7.6 s planned again on one connection; `MapLinksByMapName`
    9.0–10.6 s and 7.1–8.3 s, 8.5–10.0 s and 7.2–7.5 s. Stage 1 projected half a second more. Not measured apart, the
    likely reason: a filter is read once per condition node and Volcano matches one node many times, where `main`
    extracted and derived its facts afresh at every match.
  - **Where the new statement's cost lands**: statements of many conjuncts over a path the declaration states.
    `Filter.DeepConjunction` costs 0.1–0.3 s on `main` and 0.7–1.0 s here, `Composite.TenantFunnel` about 0.1 s and
    0.2–0.4 s. Timed inside, over the six plans the test makes of each, nearly all of it the first,
    `DeepConjunction`'s oracle is 0.19 s of redundancy, 0.16 s of contradiction, 0.14 s of strata (0.10 s of it the
    translator) and 0.14 s asking whether a weakening is applied; `TenantFunnel`'s is 0.30 s of redundancy. Each
    leave-one-out child of a redundancy question is a context opened for its verdict (read off the overlay first,
    when this was measured; it is retired). Eggnog 1.1.1, whose idle rules allocate nothing, makes each cheaper:
    14–39 % at the median for a schema-less one.
  - **The clauses, the pins and the keys added nothing the runs can see.** Interleaved with the commit that moved
    the clauses (`115ad32`), the pins planned the corpus in 9.4–14.2 s against 12.6–14.6 s, three runs each, and the
    pins and the self-join the hierarchy in `MapLinks` 5.4–6.3 s against 5.6–6.3 s and `MapLinksByMapName` 5.8–7.0 s
    against 6.0–7.0 s, two runs each (p50s over five plans). The whole stage against `origin/main`, on a workstation
    another suite was loading: the corpus new 5.2–11.4 s on `main` and 8.3–13.6 s here, medians 7.6 s and 9.3 s, p90
    168 ms against 235 ms; planned again 4.0 s against 4.5 s; the hierarchy's p50s 3.5–12.1 s on both, which no
    difference survives.
  - **The review's follow-up cost nothing the runs can see**: rules scoped to their own container, every scope the
    licence table's, a deletion asked beside what reaches the service, required translator facts. Interleaved with
    the review's tip (`14d6d46`) rebuilt, four runs each: the corpus new 4.33–4.53 s there and 4.24–4.61 s here, p50
    20–22 ms against 17–19 ms, p90 130–150 ms against 157–164 ms; planned again 1.99–2.09 s against 1.89–2.08 s. The
    hierarchy here, three runs, planned again on one connection: `MapLinks` p50 2.8–3.2 s, `MapLinksByMapName`
    2.8–3.1 s; over containers built for each plan 2.9–3.3 s and 2.9–3.6 s. (A first build of the tip in a fresh
    worktree planned the corpus new in 2.5–3.1 s, and rebuilt in the same worktree in 4.3–4.5 s; unexplained, and not
    the follow-up's, which the rebuilt tip matches.)

**Decided conservatively, and worth deciding properly.**
- **A row Calcite raises on** is no truth value: it supports no verdict, and an assumption establishes nothing about
  it, which is the `RETURNING` asymmetry. The refinement order between an error and an answer is still to settle.
  The connectives do not model a raise — a conjunction with a never-true arm is never true whatever its other arm
  does — so the checker holds a raise to the atom rows and lets a verdict stand over a row Calcite fails on. Settling
  the order settles which of the two is to be tightened.
- **Two `VARIANT` numbers** are never decided equal: Calcite's comparison across numeric types is unmeasured.
- **An ordering over a rendering** is TRUE or FALSE and no finer: Calcite's string order is unmeasured. The evaluator
  orders text as Java's `compareTo` does, and `IrEvalCalciteTests` agrees over the values it draws; a measurement in
  `Measurements/` would let the table read it.
- **`NOT` of what is only ever UNKNOWN** is not decided to keep nothing. A fourth verdict, never-false, would decide
  it, at six connectives more.
- **A literal under a cast to `FLOAT` or `REAL`** is not read, the cast changing it; nor a `DECIMAL` cast that drops
  its scale, except against `RETURNING DECIMAL`, whose atom knows the literal moves.
- **`CASE`, `LIKE` and the string functions** load as opaque.

### Constraints beyond `UNIQUE`, and what the self-join merge does not reach yet

Built in #177: `CosmosConstraintSet`, constraints written as SQL DDL (`UNIQUE (expr, …) [WHERE p]`, compiled
by Calcite against the container), the ones the service and the container definition state, a model's
`constraints`, and `CosmosSelfJoinRule` reading a join of a container to itself on one as one read. `DESIGN.md` under *A constraint says what holds
across documents* and *A join of a container to itself* is the record.

- **`CHECK`, a constraint relating two paths of one document** — *large.* `CHECK (data.id = linkId)`,
  under a `WHERE type = 'Link'` or written into the predicate. Refused by name today. Checkable per document, so the same trust as a schema rather than a `UNIQUE`'s. Two
  consumers: a join equating `data.id` then equates `linkId` too, which with `UNIQUE (pk, id)` and
  `id = 'Link$' || linkId` makes an integer key held at all three provable with no claim about pairs of
  documents; and facts carry across the paths. It wants path equality in the fact theory, whose atoms are `(path, claim)`
  today — and path equality is also what would let a contradiction like `foo = 1 AND bar = 2` over
  `foo == bar` be decided.
- **A contradiction should send nothing** — *done.* `CosmosFilterReduceRule` puts the empty relation beside a
  filter the oracle says keeps nothing, and a host with a rule for an empty `Values` sends no request.
- **A partition key pinned by both sides makes `id` alone unique** — *medium.* Both sides proving
  `pk = 'X'` puts every candidate document in one logical partition, where `id` is unique. A filter that is
  a function of the query rather than a constant, so it does not fit a `UNIQUE`'s fixed predicate and wants
  its own case beside the constraints.
- **More faithful readings of a stored key** — *small each.* A declared expression key covers a join it is
  computed from, but a stored-value key — every derived one — is covered only by text or a `UUID` cast.
  `CAST(… AS INTEGER)` over a path declared integer would count, and so would a concatenation such as
  `linkId || '/' || id`, injective because an `id` cannot hold `/`, which would give a single-column key the
  service proves.
- **Semi and anti joins** — *small.* The substitution answers them: `σ(F ∧ M)` and `σ(F ∧ NOT M IS TRUE)`.
  Nothing has asked for them.
- **A page under an inner join** — *small.* #192 takes a page after a left join, which keeps every row of
  it. An inner join keeps every row too where the page's own filter implies `M` — a link view joined to a
  body view whose filter it already proves — and the oracle can say so (`CosmosFilterStatement.Proves`);
  until then it stays a join.
- **A filter through merged views, in the shapes #183 did not take** — *small each.* `CASE WHEN p THEN q
  ELSE FALSE` becomes `p AND q` once `q` cannot raise, which is the shape a host's simplifier leaves; a
  comparison over a merged column, directly or through a strict expression, is brought to that shape
  (#189). Left as it was: a `CASE` of more than one arm, whose later arms need `NOT p IS TRUE` beside
  them.
- **A distance from a merged column at the service, bare** — *small, once one of two things exists.*
  #189 pushes `p AND (p ? ST_DISTANCE(…) < d : false)`: right, and narrowed by `p`'s index, but the
  spatial index does not serve a distance inside a ternary. The bare `p AND ST_DISTANCE(…) < d` is the
  same rows at the service and unsafe only as an in-process recheck, the arm's constructor raising over
  an object that is not GeoJSON. Either a translator told it is writing a filter's positive position —
  where the two are one — or an unconditional declaration that the path holds a geography, which makes
  the arm unable to raise, would license it.
- **How a legacy container's system partition key is reported** — *measurement.* Believed to be
  `/_partitionKey`, which its documents do not hold, so `UNIQUE (_partitionKey, id)` is stated — true,
  and never matched — where `UNIQUE (id)` would be. Measure against an account holding one.
- **Whether absent and null partition key values are one partition on Azure** — *measurement.* One on the
  emulator (409 on a second `A`). Nothing depends on the answer; DESIGN.md says what is measured.

---

## 7. Row model and types

- **A typed column over a document path — *rejected; written down here so it stops being
  reopened*.** The question was whether a caller could declare paths and types — a `columns` operand,
  or the container's computed properties — so that the planner could see a type where a document path
  gives none. The answer is no, and it has been given more than once. The row model is the document
  column, `DOC`, and a query has to work directly off it; a surface that asks the caller to describe
  the documents before querying them is the thing being avoided, not a feature that is missing.

  **What replaces it is the service's own type predicates.** Cosmos will say what a value *is*, per
  row, at query time — `IS_NUMBER`, `IS_INTEGER`, `IS_STRING`, `IS_DATETIME`, `IS_NULL`,
  `IS_DEFINED` — with `IIF` to act on the answer, and a shape check is constructible on top of them
  out of `LENGTH` and `ENDSWITH` where a parse check is too weak. A guard is *stronger* than a
  declaration, because it is evaluated against the document rather than promised about it; and where
  a guard partitions the rows rather than filtering them, the toolbox model in `DESIGN.md` admits a
  statement for each side. The worked form is in section 4 under *Rewriting a typed comparison into
  one the service can evaluate*. Each item that once named the declaration as its blocker still
  needs its own measurement. None of them needs a type the caller states.

  **The sort key is no longer one of the four.** A nullable sort key is reachable when the query
  itself removes the nulls, over any document path: the sort reads what the filter beneath
  establishes off the predicate oracle (L1), which reaches the path the filter reads whatever projects
  it. Calcite's metadata layer reached it only through a column — `RelMdPredicates` carries a
  predicate through a projection only where the projection is a `RexInputRef` — and the account this
  entry gave of what a declared column would buy, Calcite's predicates, nullability, keys and
  distinctness working over a reference, is now the oracle's to give, over paths. Recorded in
  `DESIGN.md` under *Ordering is a total order over JSON types*.

  **Paging a view by one of its own columns is the fourth thing it would buy, and the one with a
  measurement behind it.** A cast to text now projects — the value is sent as it stands and rendered
  as it is read — so a view's columns push and the statement stops carrying whole documents. What
  does not push is an `ORDER BY` over such a column: the rendering is not the path, and as text `10`
  sorts before `9`. Nor could the rendering be put into the clause instead — measured, the service
  answers `ORDER BY ToString(c.x)` with 400, error 2206, *"ORDER BY item expression could not be
  mapped to a document path"*. So a page ordered by a cast column reads every matching document, and
  the only thing that would change it is a column the sort can name — which is the surface this
  section declines. A standing cost of the row model, then, rather than an open item. See
  `DESIGN.md` under *Projecting a cast to text is a reading, not a translation*.
- **A column with no reading is refused while the plan is made** — *built, and the list it guards is
  worth reading.* `CosmosJson.CanRead` says what the reader covers; a projection of anything else is
  declined and a row carrying one is refused, so a failure that used to arrive as a truncated `200`
  arrives as a statement that does not prepare. Nothing reachable is refused today — `GEOMETRY` was
  the one live case and #149 gave it a reading — so what this holds is the boundary.

  **What is outside it**, from `EveryTypeAgreesWithWhatCanReadSays`: the thirteen `INTERVAL_*` types,
  the four unsigned integers, and `TIME_TZ`, `TIME_WITH_LOCAL_TIME_ZONE` and
  `TIMESTAMP_WITH_LOCAL_TIME_ZONE`. The last three are the ones worth a second look — `CosmosProject.IsStoredAsText`
  already names two of them as ordering candidates, so the adapter contemplates a type its reader
  cannot read. Neither is reachable, because nothing renders a cast to one; the pair is recorded
  because the two halves disagreeing is how #149 happened.
- **Binary** — *small.* `BINARY`/`VARBINARY` read base64 from a JSON string. Unverified against the
  service, because nothing in the test data is binary.
- **Temporal representation** — see *Temporal* above. The reading side handles ISO strings and epoch
  numbers; there is no *declared* basis for deciding which a column holds and there will not be one,
  so the basis is asked of the service per row.
- **Text that is not UTF-8 is refused by name** — *done; a lenient reading of a lone surrogate is the
  option left.* Measured on the probe account (`reports/verify-bytes.md` §2): Azure validates nothing about
  UTF-8 in a document body. Any sequence a client writes raw is stored, returned by every query and by a
  point read, and compared as written — a WTF-8 surrogate, a CESU-8 pair, `FF`, the overlong `C0 AF`, a
  lone continuation byte, in a value or in a property name (`Service.MalformedTextIsStoredAsWritten`). A
  lone surrogate written as a JSON escape is the exception, stored as U+FFFD
  (`Service.LoneSurrogateIsStoredAsReplacement`). The emulator refuses every such write, with a 403 or, on
  the image CI runs, a 400.

  Every reading refuses such a value with a `CosmosMaterializationException` naming the column or path,
  the document where the row says which, where in the value the bytes lie, and the bytes
  (`CosmosJson.Undecodable`; `docs/22-troubleshooting.md`). It used to fail with *"Cannot transcode invalid
  UTF-8 JSON text to UTF-16 string"*, which names nothing, mid-stream; and a projected `JSON_QUERY`, which
  writes the value out rather than decoding it, answered each byte as an escaped U+FFFD without a word. It
  looks for the bytes first now. So does the reader completing an order, over its text keys, and a
  `DISTINCT`'s repeat is told apart by the bytes. The failure is per decoded value, not per row or per
  partition: a statement fails exactly where a row carries the bytes into a reading, and one whose rows
  leave the value out is answered, in the same logical partition too. Every plan that is not pushed in full
  reads `DOC` for every document it scans, though, so one such document fails all of those over its
  container.

  **U+FFFD is no reading of them**, and was dropped as a candidate. The service compares the bytes as they
  are and counts a sequence by its lead byte: it ranks `80` and `C0 AF` below U+00E9, and says the raw
  document is not the one U+FFFD would make of it. Calcite would rank the replacement above every unit
  below U+D800, and call the two equal. The document column would stop being the text the service sent.

  **The option left, only if raw lone surrogates from other clients turn out to matter:** read a WTF-8
  surrogate (`ED A0`–`BF 80`–`BF`) as the lone UTF-16 unit it encodes, and go on refusing every other
  invalid sequence. It is the one lenient reading that agrees with the service: Azure ranks `ED A0 80`
  between U+D7FF and U+E000, where Calcite's UTF-16 order puts U+D800; `LENGTH` counts it as 1, as
  `CHAR_LENGTH` counts one unit; R1 already places a string holding a unit at or above U+D800 in the high
  band; and a Java string can hold one. A CESU-8 pair read so becomes the UTF-16 pair, which the service
  ranks as two surrogates and counts as 2. It needs a transcoder of its own, `System.Text.Json` having
  none, so it is not small. `FF`, `C0 AF` and a lone `80` have no such reading and must still fail.
- **A projected `JSON_QUERY` is written out by another writer than Calcite's** — *small, a wrong answer, found
  beside the work on bytes and not yet fixed.* `CosmosJson.GetJsonTextProperty` writes the fragment with
  `Utf8JsonWriter`'s default encoder, which escapes every character outside ASCII and the HTML-sensitive ones,
  and copies a number's text as stored. Calcite's own `JSON_QUERY` writes through Jackson, which escapes
  neither and re-renders a number from what it parsed. Measured in process on 2026-10-19: over
  `{"t":"é","u":"<>&"}` Calcite answers `{"t":"é","u":"<>&"}`, and the reader answers
  `{"t":"\u00E9","u":"\u003C\u003E\u0026"}`. So the pushed column is another string than the in-process one
  wherever a fragment holds such a character, and by the same token wherever it holds a number Jackson would
  print otherwise (`1.50` as `1.5`). The rendering should be delegated as `GetText`'s is, to the function
  Calcite writes the fragment with, over the value built as Calcite would hold it: a number past int64 is a
  `BigInteger` to Jackson and a `Double` to `GetNatural`, so that reading needs care too.
- **An integer written in [2^63, 2^64) reads back two ways on the emulator** — *the emulator's alone,
  measured.* The emulator keeps such an integer as written and returns it so in a document read whole,
  but prints it projected as the double nearest it, to 17 significant digits: 2^63 reads
  `9223372036854775808` through `SELECT VALUE c` and `9223372036854775800` through `c.v`. So a pushed
  projection answers another number than the plan that reads the document. Nothing else prints two ways
  on it: 2^53 + 1, 2^63 - 1, -2^63 - 1 and 2^64 each read back one way. Azure stores every integer
  written outside int64 as a double and prints it one way, whole or projected
  (`Service.IntegerOutsideLongIsStoredAsADouble`), so there the two plans agree (`reports/verify-bytes.md`
  §1). The `ORDER BY` yardstick's family *a double printed two ways* is exactly this now: it is named only
  where reading each printing as the double it stands for leaves the rows agreeing (`DESIGN.md`,
  *Differential testing*). It used to be named wherever a differing row held any number past 2^53, which
  could have hidden a real difference anywhere in [2^53, 2^63).

  The adapter's own wrong value beside it is fixed: `RETURNING BIGINT` over Azure's
  `9.223372036854776E+18` answered `9223372036854775807`, `CosmosJson.GetInteger` letting the double 2^63
  past a bound that was 2^63 itself and then saturating. It refuses now, as Calcite does in process. The
  emulator's `9223372036854775800` is valid int64 text, which no reader can tell from a stored integer;
  that one stays the emulator's.
- **Whether a reading can refuse is asked by its type alone** — *small; it decides how often a filter in
  process reads whole documents.* A typed reading that can refuse a value (`CosmosJson.CanRefuse`: an
  integral width, a date or time, a UUID, GeoJSON, base64) is no longer moved beneath a filter that stays in
  process, since there it was read for rows the filter drops and failed the statement where Calcite answers.
  The ORDER BY yardstick found it on CI's linux-x64 emulator, which prints a stored 2^63 as Azure does
  (`DESIGN.md`, *A residual filter was the same wall*). Such a statement reads whole documents now, as it
  did before #210. A path the schema declares an integer within the width's bounds, or a `date-time`
  string, cannot be refused, and could move. That is a fact about documents, so the question belongs to
  the theory rather than to a second list in C#.
- **A refusing reading under a branch Calcite does not take** — *small, a failure where Calcite answers;
  found beside the fix above, and not yet fixed.* `CosmosProjectSplitRule` pushes the sub-expressions of a
  projection it cannot push whole, and each is read for every row the service sends. Calcite computes a
  `CASE` branch only where its condition holds, so
  `CASE WHEN LOWER(JSON_VALUE(c."DOC", '$.e')) = 'x' THEN JSON_VALUE(c."DOC", '$.w' RETURNING BIGINT) END`
  sends `(IS_NUMBER(c.w) ? c.w : null)` as a column, planned on 2026-10-20, and a 1E+19 at `w` would fail
  the statement where the condition is false and Calcite answers null. `COALESCE`'s later operands, and the
  right side of an `AND` or `OR` Calcite short-circuits, are the same question. The fix is the one above:
  such a fragment, where a branch guards it and its reading can refuse, is not pushed. The rendering graph's switch
  (Stage 2) neither fixes nor worsens it: it writes `WHERE` conjuncts alone, `NoProjectionReachesS` holds that it is
  never asked of a projected expression, and the loader reads `CASE` as opaque. Stage 3 owns it: once value positions
  are in the IR, a branch's fragment is read beneath its condition and failure-free off it, with the set-aside cells the
  strata use, and nothing added in C#; its gates are the statement and a document in the differential, recorded as a
  divergence the day value positions open, source A sampling a branch's false condition beside a refusing reading, and
  C6 running projections.

---

## 8. Provider and integration

- **Connection options as operands** — *small.* Consistency level, preferred regions, application name,
  for callers who do not want to write a factory.
- **Lazy subschemas** — *small.* Container *definitions* are read eagerly when an account-level schema
  is built, so an account with many databases pays a read per container to reach one. Statistics are
  already lazy; the definitions want a lazy `Map`.
- **Client disposal** — *small.* See *A rebuilt root leaks its client* in section 1.
- **Server-side functions** — *medium.* Cosmos has stored procedures and JavaScript UDFs. A UDF is
  nameable in a query, so it could be exposed as a Calcite operator the way the built-ins are.

---

## 9. Observability

- **`CosmosDiagnostics`** — *small.* The one signal not surfaced: a large JSON blob per response, so
  it wants a switch of its own rather than to ride on the `cosmos.query` span.
- **RU regression tracking** — *medium.* The charge is on the histogram; assert that a query shape
  does not get more expensive.

---

## 10. Testing

- **A real account in CI** — *medium.* The emulator accepts statements Azure rejects and rejects
  features Azure implements; both have been found by hand. A nightly job against a real account is
  what stops the next one being found by a user — and it is where `CosmosDifferentialTests` and the
  routing measurement rerun their evidence against the real service.
- **Growing the differential corpus** — *small, forever.* The harness is done (`DESIGN.md` under
  *Differential testing*); every new pushdown should bring its statements to the corpus, and every
  translator addition is a candidate. Probed and in: filters, sorts, the aggregate forms, `LIKE`,
  and the array traversal — the guess that the oracle could not evaluate an in-process unnest was
  wrong, and the corpus says so.
- **Emulator gaps, asserted — done for the two that were wrong; keep the shape.** A skip must be
  earned by detecting the gap, never by asking which endpoint answered: the flat request charge and
  the discarded composite index were both hard-coded to `IsEmulator`, so an emulator that fixed
  either would have gone on skipping for ever. Both now measure the gap and report it, and the
  index-metrics pair already did. Any future gap belongs in that shape. *(Retained here as the rule
  rather than as a task.)*
- **The axiom registry is not read by the translator** — *small.* Every `Service.` row of
  `Sql/CosmosAxioms.cs` that was measured on the emulator alone is now measured on the probe account
  too, and the order's own rows — code point order, the constants, the excluded path, the composite
  index, the rank's direction, the merge across partitions — are pinned by tests that skip where the
  account in hand cannot show them. The rendering graph reads a row's outcomes (`CosmosAxiomRows`); the
  translator still encodes each fact where it uses it, and reading the registry, refusing where a row
  names only one target, is the step after.
- **Ternary logic partitioning passes over the fixes** — *done; what it found is kept below.*
  `EveryPredicatePartitionsTheRows` (`DESIGN.md`, *Differential testing*) fails on main: 131 of
  1,000 predicates. Over the fixes for A to K it failed two, both L, and with L closed the gate's
  1,000 pass. Whatever fails from here is a defect, a fix missing a shape or a cause nobody has
  named. Besides A to K it found two causes, each a wrong answer from ordinary SQL:
  - **L: the presence weakening read a `CASE`, or a disjunction, as needing every path it reads** —
    *fixed.* A `CASE` now observes absence, and `CollectPaths` takes only what every branch of a
    disjunction needs, or every operand of a compound read as a value (`DESIGN.md`, *A position
    belongs to a predicate*). The disjunction half was the last to fail over the fixes: two of the
    1,000 predicates, each a `NOT φ` the converter had turned into a disjunction with a negated
    compound for a branch. Those two and both witnesses below are in the differential corpus.
    - `SELECT c."id" FROM typed AS c WHERE CASE WHEN CAST(JSON_VALUE(c."DOC", '$.price') AS DOUBLE)
      > 8E0 THEN c."$.category" = 'b' ELSE c."$.category" = 'a' END` should keep 4 and 5. It kept
      5, having sent `WHERE (IS_DEFINED(c.price) AND IS_DEFINED(c.category))`. Document 4 has no
      price.
    - `SELECT c."id" FROM products AS c WHERE NOT ((JSON_VALUE(c."DOC", '$.price') <> 'SPRINT' AND
      JSON_VALUE(c."DOC", '$.price' RETURNING INTEGER) < 10) AND (c."$.category" IN ('Sprint!',
      'none') OR (CAST(JSON_VALUE(c."DOC", '$.price') AS DOUBLE) * 3E0) = 100E0))` should keep 1,
      2, 3 and 7. It kept 1, 2 and 3, having sent `WHERE (IS_DEFINED(c.category) AND
      IS_DEFINED(c.price))`. Document 7 has no category.
  - **M: the null guard over a `COALESCE` named the paths inside it** — *fixed, by the guard
    declining over an operand that observes absence (`DESIGN.md`, *A position belongs to a
    predicate*).* `<>` was pushed as `IS_DEFINED(p) AND NOT IS_NULL(p) AND (… != @p1)`, and a
    negated comparison gained `OR IS_NULL(p) OR NOT IS_DEFINED(p)`, both over the path the operand
    reads. Where the operand is `COALESCE(JSON_VALUE(…), 'none')` it is never null, so the guard
    dropped every document without the path. Rechecked over the fixes: none of the 1,000 predicates
    fails for it, the three witnesses agree, and so do seven neighbours, a typed accessor, the
    promoted column, a fold, a `NULLIF`, a coalesce of two paths, a `CASE` under a `NOT` and an
    ordering. All ten are in the differential corpus.
    - `SELECT c."id" FROM typed AS c WHERE COALESCE(JSON_VALUE(c."DOC", '$.big'), 'none') <>
      'bikes'` should keep all nine documents, and kept 9.
    - `SELECT c."id" FROM products AS c WHERE COALESCE(JSON_VALUE(c."DOC", '$.category'), 'none')
      <> 'bikes'` should keep 3 to 7, and kept 3 to 5.
    - `SELECT c."id" FROM products AS c WHERE NOT (COALESCE(JSON_VALUE(c."DOC", '$.tags[0]'),
      'none') = 'outdoor')` should keep 2 to 7, and kept nothing.

  And F was wider than the case folds. `SELECT c."id" FROM products AS c WHERE
  COALESCE(JSON_VALUE(c."DOC", '$.price'), 'none') = '120'` should keep 1, and kept nothing: the
  `COALESCE` was compared as the raw number, the rendering test reaching only the comparison's
  direct operands. Over the fixes it agrees, and it is in the corpus.

  Review found one more of L's kind that partitioning could not: the weakening read through every
  function as though each were null for a null, and `CONCAT_WS`, `ARRAY[…]` and `ARRAY_CONTAINS`
  over one are not — *fixed* (`DESIGN.md`, *A position belongs to a predicate*), and the four
  statements that lost rows are in the corpus.
- **The `ORDER BY` yardstick** — *built, and a default run of the class; one thing owed.*
  `EveryOrderIsOneCalciteAllows` and `CosmosServiceOrderModelTests` (`DESIGN.md`, *Differential testing*)
  judge every order the service computes by what Calcite allows. Over main both failed, in the families
  expected: text across U+D800, undefined and null on a leading key, 2^63 read back, and `_etag`, which
  Azure refuses to order by. Since the anchored read neither fails, the floors (`PushedFloors`) hold what
  each container pushes, and the model asks the one place by default. Two families of wrong answer that
  are no order's, both the emulator's, are reported apart where the statement sent no order (`Findings`):
  `INDEX_OF` counting code points (section 4; `CHAR_LENGTH` pushed as `LENGTH` was here, and no longer pushes
  over text of any character) and the integer in [2^63, 2^64) printed two ways (section 7). Owed:
  - **The merge, on Azure.** `CosmosOrderMergeMeasurementTests` has not run, no container with two
    physical partitions existing now; it runs against one the caller names (appendix E), and runs the
    reader itself over the merge beside measuring it.

  The CLR runtime's own misorders are reported apart and are not the adapter's: about 117 or 118 of the
  gate's statements at the tip (94 on main), every one a sort of two keys or more done in process,
  multi-key text by culture, the count varying with the order the service returns ties in. The fix is
  `calcite-dotnet`'s: `JavaComparisons.Compare` should compare a string ordinally and route NaN through
  `Double.compare`.
- **The partition generator's vocabulary** — *small.* Its text expressions are an accessor, a
  column or a cast under `UPPER`, `LOWER`, `COALESCE` or `||`, and every one of those but the
  `COALESCE` is null for a null — which is why the run never met a function that answers a value
  over one, and review did. Drawing `CONCAT_WS`, a constructor read through `CARDINALITY` or an
  element, and a library function or two would let it ask the question the strictness rows answer
  (`CosmosStrictness`; J's `IsNullWhereAnOperandIs` until g18). Each costs Calcite's evaluation of it in
  every statement that draws it.
- **Where the gate's partition run belongs** — *small, and the owner's call.* Every run of the
  differential class judges the first 200 predicates, in three to five minutes; the gate's 1,000
  run only where `COSMOS_PARTITION_GATE=1`, in sixteen to twenty-one, and nothing sets it yet. The
  full run belongs in a nightly job or in a gate before merging, a change to the translator or the
  split rule above all, and which of the two is the owner's to decide. The default being its
  prefix, either is the pull request's run carried further, and the variable is how it asks. The
  rendering check reuses it (decision D6), and its 1,000 run before each of Stage 1's pull requests.
- **What the partition run costs** — *medium.* Most of it is Calcite compiling the reduction of a
  literal's cast with Janino, under IKVM, in statements the adapter plans. A host planning through a
  connection pays the same, which makes it worth reducing for its own sake, and the cheaper it is the
  more of the 1,000 a pull request can afford.
- **A malformed response's failure mode** — *small.* A lookup-join stub returning raw documents
  instead of the statement's projection once produced a null reference inside the join's result
  selector, and which access produced it was never established. Worth knowing whether a malformed
  service response fails loudly or quietly.
- **The plan digests leave out the parameterised statements** — *small.* `WriteDigests` plans what the rendering
  check reads, and the check skips the differential's 33 statements with parameters whole, so a commit's "changes no
  plan" is not compared over them. Planning them needs no values, and adding them is a line.
- **The raw reading leaves out raises Calcite was measured to make** — *medium, and it touches plans.* A `VARCHAR`
  column's reader fails over anything but a string, an ordering over a `VARIANT` fails to compile, and `ANY` raises on
  an ordering across types (`Calcite.RawNullIsSqlNull`). `PredicateAtom`'s raw reading answers there without
  `Raises`, which `NeverTrue` and `AlwaysFalse` read as leave to decide. `id` and `_etag` hold strings, so the first
  needs a field typed `VARCHAR` and read as stored; whether a plan reaches any of the three is unchecked. `IrEval`
  reads them through a named column alone, and unnamed stays inside the table, so population D does not yet hold the
  table to them. Adding `Raises` changes verdicts. The rendering graph's raw cells read them since g10, the raise alone
  across types, outside the coarse answer.
- **The facts graph reads a raw number by its value, where an `ANY` reads one by its box** — *small, and it touches
  plans.* An `ANY` holds a number not written as a long as its double (`Calcite.RawNullIsSqlNull`), so a stored
  `29.9999999999999999` is equal to 30 and `9007199254740993.0` below 2^53 + 1. The coarse raw equality says no number of
  another value equals the literal, so `NeverTrue` may decide it over a path whose numbers round onto the literal; the
  precision table calls raw beside a number coarse, so no stratum rests on it. S's cells read the box (g10).
- **A `BIGINT` compared with a fraction raises near its limit** — *small.* Calcite raises comparing 2^63 − 1 in a
  `BIGINT` column with `42.5`, where it compares 2^53 + 1. Neither mode of `IrEval` models it, and nothing it is held
  to reaches it; the typed orderings over number cells should.
- **A `VARIANT`'s numeric equality is exact in type** — *small.* `"$.pk" = CAST(42 AS VARIANT)` is false of a stored 42
  in process, which reads it as a `BIGINT`; the service keeps it. Whether the translator writes such a comparison as
  `c.pk = 42` is unchecked.
- **The typed reading leaves out a column's raise over a structure** — *small, and it touches plans.* A field typed
  `BIGINT`, `INTEGER`, `DOUBLE` or `BOOLEAN` fails the statement over an object or an array (`CosmosJson.GetInteger`,
  `GetDouble`, `GetBoolean`), where `RETURNING` answers null, and `PredicateAtom`'s typed reading answers as
  `RETURNING` does, which `NeverTrue` reads as leave to decide. Read from the code and modelled in `IrEval`'s column
  mode; the column measurement holds no structure. The typed cells answer the raise.
- **The typed reading compares numbers exactly, and says a fraction raises alone** — *small, and it touches plans.*
  `Typed.Equal` decides a stored number equal to the literal by value, where a `DOUBLE` column and `RETURNING DOUBLE`
  compare the doubles they round to; and an integer type's column reads any number not written as an integer as its
  double where that is whole (`CosmosJson.GetInteger`): a fraction that rounds to one, which the coarse reading says
  raises alone, and past 2^53 another integer, which `KeptEqual` establishes is the literal. The typed cells read both.
- **A numeric cast of a raw value is each column's own, and the facts graph reads it as a truncation** — *medium, and it
  touches plans.* `CalciteVariantMeasurementTests.ACastOfARawNumberIsEachColumnsOwn`: an `ANY` converts the Java number
  the reader built, so `CAST(… AS INTEGER)` wraps a long beyond an int (4294967297 is 1) and holds a double at the limit;
  a `VARIANT` raises beyond the type, casts a string or a boolean to null, and under `BIGINT` does not read 2.5 as 2; a
  `VARCHAR` column refuses a number. `NumberCast` reads every one as truncating and saturating, and so does `IrEval`,
  which population D holds it to, so neither sees it; the split rule's window for `CAST(… AS INTEGER) = 1` drops a stored
  4294967297 an `ANY` reads as 1. S cuts no cells for a raw cast, so its checks stay unproven, until a `Calcite.*` row
  states each column's cast and both graphs read it.
- **The coarse cast to a double reads a decimal's double by .NET's cast** — *small, and it touches plans.*
  `Renderings.DoubleOf` casts, which rounds twice and misses at 9007199254740994.4: the coarse `CAST(… AS DOUBLE) =
  9007199254740994` answers FALSE of a stored 9007199254740994.4 where Calcite answers TRUE, and `Renderings.SameNumber`
  reads a raw value's number alike. `DoubleBound`, which the cells read, parses the invariant text, as spec §3.14 asks of
  the split; the facts graph reading it would change plans at such boundaries, and `CellAnswersStayInsideTheCoarseAnswer`
  allows the cast's cells the truth its coarse reading leaves out.
- **`RETURNING DECIMAL` compares a stored fraction through its double's rendering** — *small, and it touches plans.*
  Jackson reads a number not written as an integer as a double, and the clause parses the text Calcite renders it as:
  29.9999999999999982 reads as 30. `NumberCast.Holds` compares the value exactly, so the coarse reading answers FALSE
  where Calcite answers TRUE; the cells read the rendering.
- **One numeric order answer serves both targets** — *small, for the next probe-account run.* `Service.IntegersOrderExactly`
  answers `Order/Number` `numeric` on both, and `ServiceLeaf` places a whole number in int64 both as itself and as its
  double, since the theory spells it alike written with a point, which the emulator holds as a double and Azure as an
  int64 (`reports/verify-bytes.md`). Splitting the answer per target, `numeric` for Azure and `storednumeric` for the
  emulator, as `Order/String` is, needs order witnesses that tell them apart: 2^54 + 1 beside 18014398509481986.0, which
  the emulator holds as 2^54 and Azure as itself, neither tying another witness. Added to the catalogue, they leave the
  record of 2026-10-21 short of Azure's claim (`CosmosAxiomsTests`), so the split waits on the probe account's run of
  `IntegersOrderExactly` over them.
- **A comparison with a null literal has no row** — *small.* `ServiceLeaf` keys `c.v = null` over a null as
  `Equal/Null/Null`, which no planned row holds, so it answers widest. The translator records that the service equates
  a JSON null with a null (`CosmosRexTranslator`, its equality of two values that may be null), with no registry row; one
  is owed if a written form carries a null literal.
- **A stratum resting on a coarse reading is not read beside a later round** — *small, and it touches plans.* A later
  round's conjunct is read beside the facts it was rendered from only where the facts graph reads every conjunct that
  stratum assumes as Calcite does (`DESIGN.md`); beside a typed number, `DECIMAL`, a double or a cast of a raw value the
  stratum may be wrong, and the translator relies on it: over a typed column, kind 5.0000000000000001 reads as 5 and a
  label the declaration leaves free beside it may be the number 7, which the bare `c.label = "7"` a later round writes
  drops (`AStratumAConjunctReadCoarselyRestsOnIsNotRead`). Since the switch S writes the family and states nothing
  beneath such a stratum (`CosmosStatementScope.StratumFor` gives the root), so the label stays in process with its
  presence sent: a regression O2 lists, its reason *a stratum read at the root*. RU, never rows; the facts graph reading
  the cells (*F does not read the cell readings*, below) would give the push back.
- **A check that holds a conjunct it cannot judge is unproven** — *medium.* Nothing the service reads beside a checked
  conjunct is left out of its check, so a conjunct skipped as outside the IR makes every conjunct beside it unproven:
  94 per pull request after c13, most beside `IS_STRING`, a pattern or arithmetic the loader reads as opaque. The
  coverage line reads such a conjunct at its own position, which leaves the check's `AND` licence and range need unread
  there; bringing those terms into the IR is the fix.
- **A refutation over two paths composes no further** — *small.* A junction refuted through its sibling's identity sets
  two paths and has no `refutedat` row, so composing it again would need each path it sets to lie apart from the next
  sibling's; a refutation two junctions deep is unproven. The first family's wrong answers need one.
- **Only sibling properties are disjoint locations** — *small.* `$.a.b` and `$.c` lie apart in every document, but the
  `disjoint` rule derives it only for two properties of one parent, so a refutation across them is unproven.
- **The facts graph reads a text cast of a raw value as its rendering, and over a `VARIANT` it is null** — *medium, a
  wrong answer, and it touches plans.* Over a promoted path's `VARIANT` column Calcite casts every boolean and number to
  `NULL` and raises over a structure; an `ANY` renders them; a `VARCHAR` column reads a string alone
  (`Calcite.CastOfARawValueToText`). `PredicateAtom`'s coarse cast reading (`cast:VARCHAR:raw`, `Text` over structures)
  answers a boolean or a number as its rendering, and so does the translator (`CosmosRexTranslator.IsDocumentValueType`:
  "Calcite's cast over either is the value's own rendering"). Over a present boolean the facts graph decides `NOT
  (CAST(raw(flag) AS VARCHAR) = 'x')` `Always` and `CAST(raw(flag) AS VARCHAR) IS NULL` `Never`, where Calcite answers
  `UNKNOWN` and `TRUE`, and `WHERE NOT (CAST("v" AS VARCHAR) = 'b')` keeps `"a"` alone in process where `NOT (c.v = "b")`
  keeps the booleans too. S reads the union of the three columns on its cells since g09 (`RawText`), and `IrEval` reads
  the cast through a named column; F's reading, and the unnamed `IrEval` population D holds it to, want the same union.
- **The text reading's preimage of a number reads its value, where Calcite renders its double** — *small.* `JSON_VALUE`
  renders a fraction as Java spells its double, so a stored `0.10000000000000001` reads `'0.1'` (measured in process),
  which `Renderings.MayRenderAs` says no number of another value renders as; the text reading's cells inherit it. A
  service that holds numbers as doubles returns no such spelling. The cast's cells (g09) read a number's double.
- **The probe account's run of 2026-10-19 has no record** — *small.* The Azure targets of the string functions' and the
  stored values' rows rest on it, and neither record under `Tests/Measurements/Runs/`, 2026-10-16's or 2026-10-21's,
  ran their witnesses, so an outcome on one of those rows cannot claim Azure until a run of its witnesses is recorded.
- **Two constants compared are a divergence S cannot see** — *small.* Azure compares two string constants by UTF-16
  unit and the emulator by code point (`Service.ConstantStringComparisonIsByCodeUnit`), but no form S reads compares two
  constants, so the declared divergences hold no key for it. A written form that does needs its key and both outcomes.
- **Orderings over strings that may hold U+0000 are unproven on the emulator** — *small, for the coverage line (c12).*
  The emulator orders a string holding U+0000 as U+FDD0 then `0` (`CosmosAnswer.StoredCodePoint`), so S proves `<`,
  `<=`, `>` and `>=` over strings only where the import says they hold no U+0000; `=` and `!=` with ASCII are exact
  whatever they hold. A path whose strings were never stated holds every class, U+0000 among them. If the check run
  finds such orderings common, what keeps U+0000 out of a path is a declaration to read, not a reason to read the
  comparator as code point. Since g10 that includes the raw orderings over `id`: the gate's 18 are S's under
  `Production` and declined under `Emulator`, where nothing keeps U+0000 out of an `id`.
- **`Sql.CosmosFunctionBodiesTests` did not run at D7** — *small.* It needs an Entra role assignment and a container
  made beforehand (its remarks), so the probe account ran the JSON type class's
  `TheTypeTestBodiesAnswerWhatTheServiceAnswersOverEveryKind` instead, which holds the same bodies over the catalogue's
  kinds and passed, and N13 names both targets on that. The older test reads the bodies over the document text a query
  returns; run it when a container for it exists, or retire it to the newer one.
- **The next probe-account run holds the projection at eleven more cells** — *small, whenever that run is approved.*
  D7 skipped it where Azure was declared `NotKept`: `Bare/None` at false, null and absent, `Not/None` and
  `NOT (c.v OR false)` at true, null and absent, and `Greater/String` below and equal. Those cells are filtered answers
  now, so `Holds` checks the projection there; the emulator's agrees at each, and Azure's has answered apart from its
  filter only over the parted range.
- **The reader's grammar is measured on the probe account** — *done.* Every proof the check run asserts under
  `Portable` rests on `Service.NotBindsTightestThenAndThenOr`. The probe account's run of 2026-10-22 passed its
  measurement, so the row names both targets. The same run held `CosmosEval`, the witnesses (53 documents) and the
  recall and gains statements to Azure, all passing, and its 649 `Holds` cells repeat the record of 2026-10-21 exactly,
  so no second record is committed. Still owed to a later run: the order witnesses 2^54 + 1 and 18014398509481986.0,
  which no catalogue holds yet, for splitting `Order/Number` per target.
- **The service-gated measurements each seed a container of their own** — *small.* The precedence measurement copies the
  fixture of the boolean-logic one (client options, emulator timeouts, a database a framework, one container, seeded
  documents, `Kept`), as most `Cosmos*MeasurementTests` do; one shared seeded-container fixture would take about 80
  lines off each.
- **A body atom of a rule the theory cannot read has no `D0` row** — *small.* `D0` makes no term, and the context never
  makes one for such an atom, though it is mentioned; its import defaults, counted constrained.
- **Running out of ids or epochs is reached by no test** — *small.* A bundle that meets `OverflowException` declines and
  retires its generation, and F's context loses its facts and fails no plan (g07, one list for both), but Eggnog 1.1.1
  cannot lower its limits (`EGraphOptions` has none), and an `OverflowException` a primitive throws arrives as a
  `RuleEvaluationException`, a fault. `RenderingGraphTests` tells the bundle as its catch would, and
  `JsonFactTheoryTests` a context's mention; the usage gauge is forced down, not reached.
- **A statement that admits one conjunct twice has no exact join** — *small.* The IR keeps both arms, and the claim's
  textually equal members are one row, so the rest of the junction is a junction of copies that no row gives members:
  partitions 150 and 187 at 200 predicates, never sent. Writing a rest's members where only copies are left is the fix.
- **A typed bound one off under an int is unproven, not refuted** — *small, for D12.* `RETURNING` raises over an integer
  written the other way, and `BIGINT`'s over an int-sized one, so an integer type's cells hold a raise beside their truth
  and none refutes: `_ts > 1700000001` written for `> 1700000000` is unproven, the column answering TRUE at 1700000001
  where the service answers false. The column's own reading (decision D12) would refute it.
- **P2 and P3 land only where U+0000 is ruled out** — *small, for the owner's O1.* Generate mode sends the orderings'
  forms, `c.label > @p0 OR IS_BOOL(c.label)` and `c.label < @p0 OR IS_NUMBER(c.label)`, exactly over a twin of `typed`
  whose declaration keeps U+0000 out of the label, and under `Production` over `typed` itself (`RenderingGainsTests`);
  over the undeclared label the emulator orders a U+0000 as stored, so under `Emulator` they are declined. A declaration
  is what a user writes for them there.
- **A typed inequality's type test guards structures, which the typed union cannot refute** — *small, for D12.* X2,
  the translator's control that dropped it (deleted at g20), let an object or an array through: `RETURNING` reads one
  as null, where the service's `!=` is true, and the column fails the statement, so the union read the cell `(UR,t)`,
  unproven. The spec expected Exact, counting strings and booleans alone. The column's own reading would separate the
  two, and `RETURNING`'s would refute such a drop.
- **Source B prints what the differential's operator table plans** — *small.* A raw value is a column only at a key
  path, so the container keys on three of the declaration's six paths and a raw reading of `b`, `o` or `o.v` is not
  printed, nor is a parameter, `IS_DEFINED` (`CosmosOperators` is not in that table) or a literal with a control
  character. At 30 trials 25 statements plan, holding 17 proofs over 1,360 declared documents; drawing for source B, so
  that more of its conjuncts push and prove, would weigh it more.
- **The witnesses run on the emulator leg alone** — *small.* `RenderingWitnessTests` and `CosmosEvalServiceTests` are
  service-gated, so on a leg with no emulator `refutations.tsv`'s emulator column reads `not run`; the witness run
  rewrites it where it runs, and writes `witnesses.tsv`, which CI archives from the Linux x64 leg alone.
- **A weakening shaped otherwise than its conjunct is unproven** — *small.* S pairs a form with an IR node only where the
  form is the join of its children's, so `IS_DEFINED(c.category) AND IS_DEFINED(c.price)`, pushed for `NOT (c."$.category"
  = 'bikes' OR CAST(… AS DOUBLE) > 50)`, and `IS_DEFINED(c.price)` for a negated `BETWEEN` are unproven, never refuted.
  Reading a negated junction through De Morgan, or a presence test over the cells of every path it names, would prove
  them; the weakening checks report them beside the others.
- **`typed:DECIMAL` is the clause and a `DECIMAL` column at once, so M10 is unproven** — *small, for D12.* The loader
  writes `typed:DECIMAL` for `RETURNING DECIMAL` and for a column typed `DECIMAL`, which reads a number's text exactly
  and raises over anything else (`Calcite.ColumnReadsWholeNumbersAsItsType`). The cells read the union, so the string
  `"2"` the clause parses is TRUE beside the column's raise, and M10's path there is unproven where §5.4 expected the
  definite cell: the translator's mutation line held nine of ten until it retired. S's plant of M10 is caught, source A
  judging a document in `RETURNING` mode. The coarse reading leaves the column's raise over a structure out of
  `IS NULL`. The column's own reading in the IR would give both back.
- **The weakening checked is the text the split rule wrote** — *small.* Since the switch the weakening is S's, built as
  a Rex (`RenderingRexBuilder`) and written by the residual conjunct's translator; shim 3 reads that text, and the filter
  beneath admits the weakening again, S writing every member the loader reads and the translator the opaque ones
  (`IS_STRING`, `IS_NUMBER`), which sites 1 and 2 hold to the weakening's node where the loader reads it. That the two
  texts agree is not checked; the grammar's round trip holds the writer to the form.
- **Parameters stay the translator's** — *small, Stage 3.* S owns no conjunct holding a parameter (`RenderingFamily.Owns`),
  so the translator still writes them, with its recheck spine, and the check run skips them whole: a parameter's value
  is in neither the IR nor the import, and a claim proven for the value one plan bound says nothing of the next; the
  differential's 33 statements with parameters are not planned for the check. A relational gate claiming a form over
  every value a parameter's type admits, with parameter witnesses, is Stage 3's, and with it the parameterised
  weakenings the translator kept (O7).
- **`LIKE`, `STARTSWITH` and `ENDSWITH` are outside the IR, and N4–N7 and N11 are unmeasured** — *medium, Stage 3 (O3,
  deferred).* The loader reads a pattern as opaque, so a conjunct holding one is the translator's when exact (P7, a
  declared string), skipped by the check, and weakened by S to the presence its accessor implies, where the translator
  pushed a string branch with `STARTSWITH` or `CONTAINS`: O7's loss 4, 18 such statements at the gate and one more whose
  disjunction S then cannot weaken at all (partitions 2278). RU, never rows. The rows S would read are owed with it:
  `IN` as membership (N4), the string predicates over values that are no string (N5), affixes (N6) and `LIKE` (N7)
  matched by code point, and how an integral number reads back (N11).
- **A raw equality with a number is never exact over a number a `VARIANT` may hold** — *small.* S reads the raw
  reading's raises on cells since g10, and decision D3's denominator rule is gone. A `VARIANT` is equal to a number boxed
  with its own SQL type alone, and the loader reads `CAST(30 AS VARIANT)` as the literal 30, its box lost, so over a cell
  holding 30 the equality is TRUE or FALSE. Differential 237 and partitions 611 and 779, the `raw IN (n, 'n')` the
  text-equality alternatives write, stay in O2's residue on their merits. Keeping a boxed literal's type in the IR would
  decide them, and changes F's terms.
- **The column reader has no reading of its own in the IR** — *medium, and it touches plans; decision D12.* `typed:T` is
  `RETURNING` and a column the reader types at once, and the cells read the union, which is sound for both and costs two
  verdicts above: a bound one off under an int, and X2's structure. Giving the column its own reading changes F's terms
  and the oracle's memo keys, which Stage 1 promised not to touch.
- **The raw cells take the union of their three columns in C#** — *medium, Stage 3, beside D12.* What each column makes
  of each kind of value is host data (`RawColumnsTable`), asserted by the rows' pinning tests; but `PredicateAtom`'s raw
  and cast cells look it up and union the columns themselves, and the `Calcite.*` rows carry no outcomes S reads. The
  Eggnog home: the table as `Calcite`-target rows read through `CosmosAxiomRows.Read`, a column row per reader, and one
  rule taking the union under a `Truths` lattice, C# keeping only the primitives that read a literal (`MayRender`,
  `SidesOf`, the ordinal placement). S is at the rule line, so it waits for Stage 3.
- **Two atoms of one path refute no junction** — *small.* A refutation composes across a junction only where the sibling's
  path lies apart from the refuted one (`disjoint`), so an atom refuted beside another of its path leaves the check
  unproven. Partitions 1,428's `NOT (category < 'bikes')`, a second round's beside `category IN ('bikes!', 'shoes')`,
  was refuted at its own position on a string holding U+0000, which the emulator orders as stored; read beneath its
  stratum, where the category is one of the two, it is not. Evaluating such a pair on the path's own cells, as one atom
  claiming both is, would decide it.
- **A path beneath a declared scalar imports the top in round 1** — *small.* `D0` writes a row only for a path the
  declaration mentions and holds a term of, so an element or a child of a declared path, or one beneath a declared
  scalar or a closed object, defaults to anything, counted constrained, and a refutation over it only warns. The facts
  graph reads such a path through a mention child (`JsonContexts.Mentioning`), which the importer could read instead. No
  check met one: every default in the run is open.
- **F does not read the cell readings** — *medium, and it touches plans.* `PredicateAtom.OnCell` is exact where `On`
  is coarse: a scalar against a literal, a typed number by its double, the raw reading's raises, `RETURNING DECIMAL`'s
  rendering. Only S reads it. The facts graph deciding from cells would close the coarse readings' gaps above and decide
  more, and would change plans.
- **The error policy has no strict form** — *small.* Decision 2 sets aside a raise beside a truth in every allowing box.
  A strict policy — Equal only where Calcite never raises, Implies keeping the cells it raises on for the recheck — is
  neither defined nor loaded, and would not compose through `AND`, for the reason a service failure does not. A rewrite
  that needs it, as #162's temporal rewrites once did, designs it.
- **A number compared through a cast or `RETURNING DECIMAL` weakens to presence, P6 among them** — *medium, O7's listed
  loss until Stage 3 (owner, 2026-10-24).* Both readings parse a string Calcite then compares where the service compares
  none, so no accessor of the conjunct writes `c.price > 50` as the form means, and `rex-writable` admits no such member:
  K's numeric windows (C2, not built), the 21 weakenings the review of PR A found tighter through one and P6's C4
  `IS_NUMBER(c.price) AND c.price > 50` weaken to presence. RU, never rows. No carrier operator, re-typed accessor or
  other way of sending what the Rex does not mean is built. The fix is a guarded spelling S can prove, in the shape of
  `CASE WHEN IS_NUMBER(x) THEN CAST(x AS DOUBLE) > n ELSE FALSE END`, once the loader reads `IS_NUMBER` and `CASE`: Stage 3.
- **C1 keeps the guards a declaration makes vacuous** — *small, text only.* J deleted `IS_DEFINED` and the type test
  where the facts say the path holds the type (`Guarded`, gone with J at g18); the template always writes them, so under
  such a declaration S's weakening is longer text for the same rows.
- **Strictness inside an opaque conjunct reads values only** — *small, a loss beside J (O7).* J's walk read a
  connective inside a conjunct it could not translate for its truth or falsity, `AND` true needing every conjunct's
  paths and `OR` false every branch's (`CollectPaths`, gone with J at g18); S reads the conjunct's top call and its
  operands as values, and no connective is strict, so `(a = 1 AND UPPER(b) = 'x') <> FALSE` weakens to nothing where
  the walk kept both paths. RU, never rows. Reading connectives at a position inside the term would retire it, with
  type tests and `LIKE` in the IR.
- **The shadow's sites read the flow's arm** — *done at g17.* The arm is gone, and the sites ask S unconditionally but
  in a schema a test made with S off (`CosmosReasoning.RenderingOff`), read once a statement and at a `toRel`. At g17 S
  off was a flow arm; the switch's review moved it onto the schema, so no arm of the shadow's reaches `main`.
- **What g17 deleted** — *done.* The shadow's arm and observer, the split rule's `Shadowed` and `projecting`, the
  statement's record of whether its admission took S's answers and its sets beside its own; the bundle hook, the sets
  asked beside and the plants G9, G11 and G12 read moved to a test schema's probe (`ICosmosRenderingProbe`). The
  production and test lines are in the pull request's measure report.
- **What g18 deleted** — *done.* J, the split rule's own weakening, which nothing had called since g17: 933 lines,
  with M11's site and its value in the translator's faults (`DESIGN.md`, *J deleted*).
- **What g19 deleted** — *done.* K, `CosmosNumericWindow`, 191 lines, its `TryConstantNumber` moved into the loader (44),
  and J's two helpers in the translator, `TryRenderedTextValue` and `TryParseRenderedNumber` (63), with
  `TryNumericCastOperand`'s saturation and rounding outputs, which only K read (`DESIGN.md`, *K deleted*).
- **What g20 deleted** — *done.* The translator's fault plants, M1–M10, X1 and X2, which no test had planted since the
  switch: the enum, the flow's arm and the thirteen sites that read it, 109 lines (`DESIGN.md`, *The translator's fault
  plants deleted*). Spec §3.5's deletions are done; what the translator still decides beside S is Stage 3's (site W's
  gate and the translator over the IR, above and below).
- **The unnest rule asks its element filter under a placeholder alias** — *small, before the IR reads an element path.*
  `CosmosUnnestRule` asks whether the element's filter renders over a binding whose element is `t`, any alias not the
  container's, and implementation binds the alias it allocates, so `CosmosFilterStatement.Of` finds no statement and the
  filter admits again at implementation (three statements a pull request: `Unnest.FilteredElement`, `Unnest.TwoArrays`
  twice). The translator answers alike, and S owns none of it while element paths are opaque in the IR
  (`CosmosDocumentPaths`), so `ImplementationNeverAdmitsAgain` holds the rule for statements with an owned conjunct.
  Once the IR reads an element path, the rule asks under the alias implementation binds, or the statement is found
  without it.
- **The printer binds the number a form holds, not the type Calcite gave its literal** — *small, for g16's rehearsal.*
  A number compared through a reading Calcite types approximate (`RETURNING DOUBLE`) is bound by the translator as a
  double and by the printer as the decimal the form holds, and a decimal's scale (`30.0`) is not in the form: the same
  number to the service, but another parameter in the plan digest. Of the same-form conjuncts, 2 of 158 a pull request
  and 8 of 275 at the gate are such, all J's re-typed text-equality number branch, which the switch stops pushing (O7,
  loss 1), and none differs in scale, since no literal of the corpus or the inputs has a trailing zero: of 16,285
  random literals the loader reads, 1,376 bind another scale (`30.0` as `30`, `100E-2` as `1`), the same value and type.
  The rehearsal's listing holds no statement whose literal carries another scale, and its 23 respellings by parameter
  are the same values bound in another order (R5), so the printer was not changed at the switch. Should a literal with
  a trailing zero reach it, `Print` takes each comparison's reading beside the form, from the conjunct's IR, binding a
  number compared through an approximate one as a double and a decimal at its literal's scale: about 15 lines.
- **The reader reads no `IN`, bare path or constant** — *small.* `CosmosSqlFormReader` reads each as opaque, so the
  printer's round trip cannot hold one; S chooses none (no template proposes an `IN` until N4, and `sendable` lifts no
  constant), and their spelling is held by a unit test alone. A template that proposes one brings the reader with it.
- **The printer reads its fault switch at each node** — *small, O4's inventory.* `RenderingPrinterFaults` is read, one
  `volatile bool`, for each path name, `NOT` and junction a print writes; since the switch implementation prints every
  form S chose, so the count of inert sites O4 accepted grows by those reads, unmeasured beside a plan's own time.
- **Site W gates S's weakening on the translator** — *small, kept at the switch.* Site W builds S's weakening
  (`RenderingRexBuilder`) and asks `TryTranslate`, as spec §5.1 keeps it for Stage 2: writing legality, which can only
  withhold a push (RU), never add or change one. A weakening holding its conjunct's own comparison that the translator
  refuses from the stratum's facts, as a raw string ordering it cannot prove, is then pushed by nothing: the grammar's
  round trip shows the shape (a raw column's orderings), and the switch's listing classes each such statement with its
  weakening (none dropped for it at the gate). With S writing, the inner filter's statement asks S of every member the
  loader reads, so the gate need only be the translator's for a member S does not own. g18 deleted J and kept the gate,
  its plans byte-equal to the switch's, as spec §5.1 keeps `TryTranslate` for Stage 2; narrowing it is Stage 3's.
- **A path read only through a view's typed column has no raw value** — *small.* The translator writes a cast to `ANY`
  only over a rendering, so `AccessorsOf` gives such a path none: a string comparison over it builds nothing, and that
  residual's weakening is not pushed (sound, the whole weakening null), and a type test is written over the typed column,
  as J wrote `IS_DEFINED` over it until g18, which `CosmosTypeTestImplementor` cannot take apart in process. No weakening in the
  inputs meets it. Writing nothing there instead leaves 20 shapes S holds implied in the grammar unwritten, against
  "S holds only what the builder writes"; the reader is the same `typed:T` as a `RETURNING`, so the grammar cannot tell
  the two apart. The column's document accessor, where the binding holds one, would give it a raw value.
- **A path read through typed accessors of two SQL types writes no number** — *small, a bridge until each member carries
  its reader.* S proves a comparison over one atom's reader and the form keeps no reader, so where a conjunct reads one
  path as `RETURNING INTEGER` and `BIGINT` (or `DOUBLE`, or `BOOLEAN`), `AccessorsOf` keeps no typed accessor and the
  builder writes no member comparing it with a number or a boolean: that weakening is not pushed, RU, never rows. None of
  the inputs' weakenings meets it (`APathReadThroughTwoTypesWritesNoNumberOverEither` holds the shape). The proper fix:
  the lift writes each `implied` member with the reader `rex-writable` judged it over (a value change, no rule), and the
  builder looks up the accessor by it; it lands with Stage 3's value positions, which need a reader each anyway, or
  before, should the rehearsal list a weakening this refuses.
- **A typed reading compared with a fraction keeps no presence** — *small, O7's loss 5 until Stage 3.* Calcite compares
  `RETURNING INTEGER >= 2.5` through a cast to `DECIMAL`, which the loader reads no reading of (it reads a numeric cast of
  text or a raw value alone), so the conjunct is one opaque term with no path, and S proves no weakening where J keeps
  `IS_DEFINED`: J's was unproven to S, the listed loss (`RenderingInputs.CastOfTyped`, a check-run input, held it in
  line 13b until that line retired at g17). RU, never
  rows. The loader reading such a cast as a value would give the strictness rows presence; it changes F's terms, and so
  plans, and lands with D12, the typed column's own reading.
- **Tests that held J's or the translator's reasons** — *done at the switch.* Each was restated over S's behaviour —
  the weakening held to the conjunct's rows, a respelling or a gain pinned as S writes it, a loss named by its O7 or O2
  class — and none by changing S: the planner's and the declared facts' SQL-asserting tests, the stratum test, the
  reader's order test (its `u > 'a'` sent on Azure alone), the translator's mutation line (retired; its faults live in
  S's plants), recall's replay (no translator fault reaches the statement), the weakening checks and the corpus's wholly
  pushed list (`Filter.DeepConjunction` joins it, P9). One respelling stays: where a weakening holds an `IS_NULL` the
  loader reads as opaque, the translator writes the whole Rex and guards S's guarded member again — the same rows.
- **The grammar's round trip costs a leg seconds** — *small, O5.* `EveryWritableShapeRoundTrips` took 9.3 s at 100 %
  load in a process of its own, S defined (1,138 shapes; a generate bundle an accessor and set, and a check bundle a
  claim, a comparison's once a set). Line 32 reads it in the leg's wall time. If PR C's legs pass O5's budget, an atom's
  claims go into one check bundle.
- **The determinism test asks S, and costs a leg about 23 s** — *small, O5.* `TheDigestsAreDeterministic` builds its
  harness schema's oracle and asks S over the corpus twice, both passes over that one schema, sites A and W, for line
  26's `RepeatsOpenNoBundle`: 31–33 s against 8.5–8.9 s at `main`, at 82–100 % load, hidden on 8 threads by a longer test
  beside it. O5 priced it at +0 to +1 s. Line 32 is read from the whole-suite rehearsal and the pull request's own CI
  against `main`; past the budget, what is left to cut is the harness schema's own build, defined from the definition
  the process has already read, never a graph another schema built.
- **Conjunct filing stays a lever, not taken** — *small.* §6.3 takes it if the 5–7-condition shape or the hierarchy's first
  plan reads above its pass line on the build machine at low load; at 100 % load S's wall time crossed both while its
  thread time held them (the measure report). A reading at lower load decides it. Filing each conjunct's answer under its
  own key cuts the first plans of the statements Volcano splits.
- **A later plan over a new data source is a first plan** — *done at g15r: line 30 restated (owner, 2026-10-27), the
  content-keyed memo withdrawn.* Connections that share a data source, pooled or owned, plan against one schema and its
  containers, so any later plan reads what a kept connection reads; a new data source, a root rebuilt after an idle
  prune or `ClearPool`, and each connection under `Pooling=false` are new schemas, whose first plans are lines 29b's and
  30's first. The line's earlier "fresh connection" built a data source and containers per connection, a new schema each,
  which only a memory shared across schemas could hand hits (2.4–5.0 ms at the median, 10.2 and 34.9 ms outliers, at
  67–85 % load); a memo keyed by the declaration's content would be that, a process-wide learned cache, which the schema
  rule excludes. `TheHierarchyIsWithinItsLine` now plans over one data source, a `CosmosSchema` added with `AddSchema`:
  at g15r (Release, net10.0, 100 % load, three runs) the first plans over a new data source, its schema's build
  awaited, read 182–757 ms of S, and the later plans, over five new connections and five on one kept connection,
  0.85–3.14 ms at the median and 6.07 ms at most, opening no bundle.
- **S's first build in a process is the registry's first read** — *small.* Started at a schema's first plan, as the
  switch does at a table's first `toRel`, the build ran beside the first plan, whose first question waited
  94–251 ms with none declined; the registry read is most of the build (2.4–5.6 s at 100 % load). Paired with S off, the
  first plan's wall time differs by −0.47 s over fifteen pairs, against line 29's 0.5 s and its 1.0 s kill; one run of
  five read +1.21 s. The registry's first read is where to cut, should a quieter host show the build in the plan. The
  registry's rows and the definition read from them are fixed facts and stay once a process (`CosmosRendering.Definition`);
  each schema object builds its own graph from them at its first plan, so a schema made in a warm process pays the warm
  build off its planning thread, its first plan waiting at most 5 s. Line 29 reads the cold case, line 29b the warm one:
  at g15r, ten new schemas in a warm process, alternately with S writing and without, read 459–497 ms more wall time at
  the median over three runs (pairs 337–708 ms) at 100 % load, against 29b's 0.5 s and its 1.0 s kill; with the build
  awaited first, S's own time in the plan was 15.2–22.6 ms at the median. So `CosmosSchemaFactory.create` does not warm
  before it reads the containers' definitions; should a reading go over, that warm overlaps the build (the theory defined
  and settled in 329–402 ms there, hashed in 101–116 ms) with the reads.
- **The thread clock stays beside the wall** — *small, decided 2026-10-27.* `ThreadCpu` (80 test lines) and its plumbing
  in the cost instrument (about 9 production lines) read S's own processor time beside each wall reading: the only
  evidence that separates a busy host's stretch from S's own cost, on which lines 24, 25, 29 and 30's crossings at 100 %
  load are judged. It survives g17 with the cost instrument.
- **An answer's size is read off the heap** — *small.* `InterningPerBundleIsCounted` reports the heap grown over the
  corpus's answers remembered, divided by their count: an upper bound, since F's contexts and Calcite's caches grow beside
  them. Measuring an answer's own graph of objects would read §6.1's estimate of 1–4 KB an entry exactly.
- **A successor's failed build is retried, not fatal** — *small, for the owner.* Spec §6.2 makes a definition that
  throws S's end for the process: the rows are fixed in the assembly, so every schema's definition would throw alike;
  that holds of the first definition alone. A successor's build is one schema's graph's, the same definition built once
  already, and fails only for want of memory or the like, so the current generation answers on and the next build starts
  10 s later (`RenderingGraph.DefaultRetry`). If it fails for ever, S answers from the current until it runs out, then
  declines as unavailable but for remembered answers (O8).
- **A first build that fails for want of memory ends that schema's S** — *small, for the owner.* A definition that
  throws throws for every schema alike (`CosmosRendering.Definition` is read once and keeps what it threw), but a
  first build that fails after the definition returned — memory short, say — leaves that schema's S unavailable for
  its life, which for a schema added with `AddSchema`, or a host's own data source, is the process's. Retrying it
  after `DefaultRetry`, as a successor's failure is, would keep a passing failure passing.
- **S's state was the process's, not the schema's** — *done.* `CosmosRendering.Oracle` was a static whose generations
  interned every question's values, whose memo kept 4,096 answers, and whose importer kept every node's readings in a
  static table: all learned, all living for the process, against *A rebuilt root leaks its client* and chapter 6. The
  definition stays the process's, being fixed facts (`CosmosRendering.Definition`); the oracle, its graph and
  generations, the memo and the readings are each schema object's, on its `CosmosReasoning` beside its
  `JsonFactGenerations`, made with it and built when first warmed or asked, which nothing in planning does until the
  switch. Unbound metadata reasons in nothing. Left for the switch, which warms it: line 29b, the first plan on a schema
  made in a warm process; CI's builds, one for each test schema that asks S; and what a schema's S holds once filled,
  below.
- **S's frozen theory is built once a schema** — *medium, Eggnog's.* Every schema object builds its own S from the
  one definition: Eggnog 1.1.1 interns a child scope's values into its graph, whose pools are permanent and shared by
  every scope (`ValueStore`), and counts every scope's ids and epochs on the graph's (`GraphUsage`), so a frozen
  theory shared between schemas would share what each schema's questions taught it. So from the switch, a schema made
  in a warm process will pay the warm build (352–1,832 ms at 85–100 % load, 598 ms quieter) once S is asked, and hold
  its own generation; nothing in planning builds S before then. Built, a generation is 4.4 MB and 4,625 interned
  values; after 4,096 distinct questions, 32.6 MB more and 122,153 values, about 280 bytes a value, where the memo of
  their answers is 3.6 MB, 0.9 KB an answer (`CosmosReasoningTests.ASchemasGraphsCost`, Release, net10.0, the machine
  at 100 %). Its successor is built at 800,000 values, so at that rate a schema's S nears 200 MB first (extrapolated,
  not measured), and two live while the successor builds: once a process before, now once a live schema. Scoped
  interning with child-local ids and epochs, or a seal or tenant API that lets graphs share a frozen root nothing can
  enter, would let every schema share one: a root nothing enters is a fixed fact (owner, 2026-10-27). Until then,
  hashing the theory once a definition (70–143 ms of each build) and loading the settled rows are levers that are
  fixed facts too; retiring a generation sooner, or interning a question's literals by the vocabulary, would bound the
  rest.
- **Concurrent builds are unmeasured** — *small, now that the switch warms S.* Schemas made at once build at once, each
  on a dedicated thread, and a first plan waits at most 5 s for its own schema's build, blocking the thread it plans
  on. Measure 4 and 8 builds at once, and `Pooling=false` connections opened and closed in a loop, with the thread
  pool's queue and unrelated async work's latency beside. A process-wide gate on builds would hold no learned state,
  and would be a stopgap; the proper fix is the shared frozen theory above.
- **Line 32 is read from the pull request's own CI run** — *small, at PR A's measure report.* O5's budget is whole-leg
  wall time against `main`, every leg: the build reports timed `Facts.Rendering` alone on this machine (+5 to +33 s,
  with 18 to 41 s between two runs of one binary), and the new tests outside it add about 12 s cold. The slowest leg,
  osx-x64 net8.0, ran 2.4 times linux-x64 net10.0 on `main`, so its increment is the one to read; past the budget, O5's
  levers (`Portable` reported at the gate alone, among them).
- **A junction root's Unproven names no refutation** — *small, diagnostic.* Generate mode composes no refutation, so
  `ReadBest` on a junction with no exact form gives no reason. An atom's names its least refuted cell.
- **A test's schema probes admission** — *small, kept.* What the shadow's observer handed production's sites and was told
  of them, but for the comparisons with the translator, is a test schema's probe (`CosmosReasoning.Probe`): the sets a
  statement is asked under beside its own, each bundle S opens, and G9's, G11's and G12's plants. G11 and G12 plant
  where they act, the node a round's conjunct is stated beneath and the reading precision `StratumFor` reads: planted in
  `CosmosOracle.RenderedFrom` G11 never reaches S, which G11 itself shows, so the TODO's plan to plant there falls.
  Production's reasonings hold none, and a statement reads it once.
- **Line 6's floors count conjuncts, not which** — *small.* Each input holds at least the conjuncts it sent exactly at
  the switch under each asserted set, so a conjunct lost beside one gained in the same input passes; `sent.tsv` lists
  each conjunct with what it sent, so a floor by statement digest would hold which, at a few test lines.
- **C6 lists the forms over a path the service sets apart** — *small.* A form over `id`, the partition key or a system
  property cannot be run over the document drawn, since the fixture writes `id` and the partition key and the service
  `_ts`; each is a row of `conformance.tsv` with why it was not run (32 a pull request, 52 at the gate), every one a
  spelling the translator writes alike. Writing each such document in a partition of its own, under the `id` drawn
  where it is a string, would run those reading `id`.
- **The answers a statement's terms keep live as long as the terms** — *small.* Kept by the oracle, set, theory and the
  question's name by content, in a weak table keyed by the terms object, which F's memory of statement trees bounds
  (its least recently used out first), not §6.1's 4,096 entries of the oracle's memory. G9 plants the name without the
  opaque readings, which two statements of one digest can part in; the conjuncts', junctions' and strata's parts are
  digests and node names, which no two questions of one statement's terms share.
- **A generate weakening folds every implied member** — *small, text only.* A leaf with no writable best weakens to the
  `AND` of all its implied forms, so `<> '30'` over text sends five members where one guarded form holds the rest; the
  rows are the same. Pruning a member another implies would be a rule; `ServiceForm.Folded` decides nothing.
- **P4's orderings are no tighter than J's** — *small, a gain not delivered.* No template excludes the nulls and
  structures C1's string branch keeps, so `>= '30'` weakens to J's form under `Production`, and to presence under
  `Emulator`, where J's branch over strings that may hold U+0000 is unproven to S (O7, listed). A guarded string branch,
  `IS_DEFINED AND NOT IS_NULL AND IS_PRIMITIVE AND (NOT IS_STRING OR c)`, would be a template of its own.
- **Looser weakenings the shadow listed against J** — *small, O7's.* Read off J's verdict at the weakening's key and the
  grammar's rows: J's implied but holding a member the Rex builder cannot write (the text-equality alternatives' number
  branch, C3, and K's windows over a cast), and J's unproven to S (a typed `DECIMAL` window past 2^53, which a double
  near the bound would leave out, and the string branch over strings that may hold U+0000 under `Emulator`). Line 13b
  read them from g15 to the switch, whose listing holds each; it retired with the shadow at g17, J being asked by
  nothing, and P4 is held to J's weakenings as the shadow read them at the switch.
- **A guarded string branch would deliver P4's `>= '30'` tighter under `Emulator`** — *small.* It passes line 19 on O7's
  loss 5; the branch would be a template of its own.
- **The witnesses hold S's plants where one conjunct is wrong at one cell** — *small.* P19, P5, P6 and the `HAVING`
  statement give no witness case for S's plants: generate mode composes no refutation through a junction, so no single
  refuting cell makes a document that shows the statement wrong, and the `HAVING` statement's rows are no ids. Their
  refutations are asserted at the atom; composing refutations in generate mode would give the cases back.
- **G7 plants presence's dual, not the fold's** — *small.* A weakening is lattice-free, so a fold planted beside the
  right one faults the bundle, and the definition names no fold a plant could replace. Naming
  `ServiceForm.Folded` in the definition, as `Leaf` and `Pair` are, would let one, at a production line or two.
- **A raw accessor compared with a parameter does not plan in the check run's harness** — *small.* `c."$.label" <> ?`
  leaves the parameter untyped beside an `ANY`, so the parameterised residual the inputs gain reads the label through
  the accessor.
- **The in-process judge of an opaque weakening fills typed fields alone** — *small.* A field read as text or JSON, a
  view's column among them, is not filled, and its conjunct is counted and left out; filling it as the reader renders it
  would judge those too.
- **The rewrite test judges a lowering, never a deletion** — *small.* Line 33 holds S's best for each lowered conjunct to
  Calcite's own evaluation of the original, opaque or not; a conjunct the rewrite deleted as implied by the declaration
  is unpaired and counted, and that deletion is held by nothing in S until Stage 3 makes the lowering templates.
- **The switch's listing** — *done at g16.* With S writing, 699 of the gate's 3,492 statements plan otherwise than at
  `main`, each in `Planning/Switch/switch-listing.tsv` with its class, held by
  `PlanDigestTests.TheSwitchChangesOnlyWhatTheShadowListed` (explicit, at the gate): 116 gains, 102 respellings, 208
  weakenings tighter, 61 alike, 18 regressions O2 lists, 187 weakenings looser or dropped on O7's list (4 beside a gain,
  11 beside another conjunct's tighter), and 7 benign — 6 whose weakening's Rex types a literal otherwise with the SQL
  byte-equal, and 1 (`differential 404`) admitted a round earlier, plan and SQL unchanged. Each class is read off the
  shadow's weakening rows for its statement; g16's labeller read a row's first token, an admission's, so 43 changed
  weakenings were labelled respellings and 11 looser ones tighter, until the switch's review relabelled them, the
  hashes untouched.
- **The switch's listing retires after the switch merges** — *small, the pull request after it.* The test pins every
  gate plan the listing does not name to pre-switch `main` by one hash, so once the switch is `main` any pull request
  that rightly moves a plan fails it, and nothing regenerates it. It is line 21's evidence at this pull request's gates
  alone: delete `TheSwitchChangesOnlyWhatTheShadowListed`, `Planning/Switch/switch-listing.tsv` and its embedded
  resource once the gates are recorded in the measure report. Later plan changes are held by `WriteDigests` at a
  change's parent and tip, as appendix E says. Where the shadow could not read the translator's weakening (58 statements), the two
  were read side by side: an element path (21 dropped, 14 a member dropped), `LIKE` (19), arithmetic (4), each O7's.
- **O2's residue at the switch** — *small each, the stage named.* What S declines inside its family, each RU, never rows,
  the conjunct kept in process with its weakening: a typed `BIGINT` compared past 2^53 (differential 348–351, 354, 452;
  D12's column reading); a raw value cast to text (partitions 1372, 1651; the raw reading's cast rows); a text ordering
  over a string nothing keeps U+0000 out of, under the emulator's set (R4; a declaration that keeps it out, or Azure's
  set); a later round whose stratum rests on a coarse reading, read at the root (F reading the cells); and an inner
  filter's conjunct the translator admitted exactly and S weakens (differential 237, 449–451, 453; the raw reading's
  boxed literal). The yardstick at the gate lost seven pushes to the third, each a correctness fix on the emulator
  (owner, 2026-10-22), its floors restated: `MAX(_ts)` where `s > '_'` and a sort where `t > 'é'` over `orders_ascii`,
  whose pattern admits U+0000; sorts where `t > '_'`, `t > 'true'`, `id > '1'` and `s > 'ab'` over `orders_declared`;
  `MIN(_ts)` where `id > '_'` over `orders_plain`. The reader's order test sends `u > 'a'` on Azure alone, for the same
  reason.
- **O7's losses at the switch** — *small each, Stage 3 unless named.* The translator's weakenings S does not reproduce,
  each RU, never rows, the residual rechecked above: the text-equality alternatives, as presence (loss 1); a compound
  compared with `TRUE` or `FALSE`, read as opaque, as nothing (loss 3); `LIKE`'s string branch, as presence (loss 4, with
  O3); presence the translator read through a term the IR holds opaque — a cast to `DATE`, `UUID` or a wrapping type, a
  cast Calcite widens around another to compare a literal past the target's range, a nested accessor, a typed reading
  compared with a fraction — as nothing (loss 5); an element path, as nothing or with its member gone (until the IR
  reads one); arithmetic, as presence; a number compared through a cast or `RETURNING DECIMAL`, as presence (the owner's
  2026-10-24 decision, until the guarded spelling); and a parameter compared over a path not declared a string, as
  presence, where the translator sent an inequality's string branch (a parameter atom is presence and no candidate to S).
  Each the gate plans is held in the listing by its statement. The listing digests no statement with a parameter, so the
  last is held by `CosmosParameterPlanningTests` alone, and the cast of a cast by `CosmosPlannerTests`.
- **The stacked-filter composite is not proved** — *small, Stage 3.* Filters stacked over one scan are conjoined at
  `Rel/CosmosFilter.cs` outside any one statement, so S proves each filter's `WHERE` and not their `AND`; S's forms hold
  no parted bound, so they cannot form a parted range there, and the translator's own orderings carry its exposure as
  before. `CosmosFilterRule.IsTranslatable` asking S for the `WHERE` with the one beneath would close it.
- **The declared-fact rewrite lowers before S** — *medium, Stage 3.* `CosmosFactRewriter` turns a UUID or instant
  comparison into a string one from the declared stored form before any statement is built, so S proves forms for the
  lowered conjunct and the lowering itself is C#'s. Line 33 checks it against S meanwhile (`RenderingRewriteTests`).
  Stage 3 makes the lowering templates over the declaration's stored-form facts and deletes the rewrite before the split.
- **A weakening's opaque members mean in Calcite what their bodies compute** — *small, Stage 3.* The inner filter's
  statement hands members the loader reads as opaque (`IS_STRING`, `IS_NUMBER`) to the translator, which writes them
  back; that they mean in process what the service computes rests on `CosmosFunctionBodiesTests`, not on S. Type tests
  in the IR would put it in S.
- **Lines 6 and 13b read the listing until g17** — *done at g17.* Line 6 is floors per input, each the conjuncts the
  input sent exactly at the switch under each asserted set (`RenderingSentTests.EachInputSendsAtLeastItsFloor`); line
  13b retired with the shadow. The mutation and gate-fault plants plan with S writing, their losses read against the
  unplanted run's.
- **Line 12 reads a sent `WHERE` under `Portable`** — *small.* The check run claims each `WHERE` as sent under the asserted
  set, `Portable`, and the emulator's beside it, while production plans under each container's own, `Production` on
  Azure; so a `WHERE` S proved under `Production` alone — an ordering over a string that may hold U+0000, as
  differential 2's `(c.label > @p0) OR IS_BOOL(c.label)` — reads Unproven there, and line 12's kill on "an Unproven
  `WHERE` S's composite proved" cannot be told apart from it; at the gate those forms took the coverage line past 5 % (82
  of 1,358). Every conjunct the check reads is S's now, so the coverage line — retired as a kill line by spec §7 — reports
  S's figures, and line 12's other parts hold: no refutation, every bundle complete. Checking each `WHERE` under the set
  it was planned under too would let the kill be asserted, and S's forms be held to the line again.
- **Source B's long run judges some forms on no measured document** — *small.* At 5,000 trials with S writing
  (`NoGeneratedFormHasAWitnessOverALongRun`, line 1b), 0 witnesses over 110,580 proofs, and 213 forms beneath a stratum
  no cell meets; but 621 forms S would send are judged on no measured document. The 40 it prints are each over a reading
  the evaluator tosses a coin on — a cast of a raw value or of text to a number, a typed `DECIMAL` — and none is beneath a
  stratum. The pull request's 30 trials meet none (line 2). Modelling those readings in `IrEval`, beside the raw orderings above, would
  measure them.
- **The service-backed fixtures name their service** — *small.* A fixture that plans against the account the tests run on
  (the differential, the connection, lookup and self-join tests) names the emulator (`serviceTarget`) wherever the
  endpoint is this machine's, so the emulator legs plan under the emulator's set and the probe account's run under
  Azure's; the planner's tests, which run no statement, plan under each container's own set, Azure's, on every leg.
- **The rendering check costs more than decision D11 accepted** — *small, for the owner.* D11 accepted about +13–17 s
  a pull request on a leg with no emulator and +10–20 s on the emulator leg. Measured at PR D's review (2026-10-22,
  net10.0 Release, the machine 56–83 % busy): `Facts.Rendering` took 83.5 s against 58.1 s at `main`, +25.4 s a leg (98.0 s
  on net8.0); its shared check run 58.7 s against 46.4 s, first in a process; and on the emulator leg the service-gated
  classes beside it about 23 s more (witnesses 15.6 s, `CosmosEval` 1.4 s, recall about 3 s, gains 0.4 s, fixtures about
  2.4 s). Planning is most of the run: its bundles took 5.3–5.6 s of 36 s warm. The owner accepts the measured figures,
  or D11's fallback runs the partition run at 100 predicates a pull request, about 3 s off, which moves the coverage and
  precision lines off the 200 they are measured on.

---

## 11. Unsettled questions

These are not features. They are things believed but not measured, and each one is a defect waiting
for the right query.

- **Two-argument `TRIM` and `TRUNCATE`.** Left out for want of a measurement.

---

## 12. Read off Flink's connector SPI

Flink is the most complete Calcite-based connector framework in the open, and its source and sink
*ability* interfaces are a catalogue of what a pushdown-capable connector can offer. Each row below is
an interface a Flink connector implements and what it would mean for Cosmos; abilities already
covered here are not listed. Every Cosmos operation named was compile-checked against the SDK this
project references.

### Source abilities

| Flink | Here |
|---|---|
| `SupportsPartitionPushDown` | **worth taking.** Hands the planner the list of partitions. `GetFeedRangesAsync` gives the physical ones. |
| `SupportsDynamicFiltering`, `SupportsLookupCustomShuffle` | **closed by measurement** — the service's query router already prunes an `IN` over the partition key to the partitions owning the values, cross-partition execution already fans out per feed range, and per-key routing costs the per-query floor times the key count. See *The lookup restriction is already routed* in `DESIGN.md`; `CosmosLookupRoutingMeasurementTests` reruns the evidence against any real account. |
| `SupportsReadingMetadata` | **small.** Metadata columns declared rather than always promoted: `_rid`, `_self`, `_attachments`, and the per-item `ttl`. Would also let `_ts`/`_etag` stop occupying ordinary column ordinals. |
| `SupportsRowLevelModificationScan` | **worth taking.** The scan is told it is feeding an `UPDATE`/`DELETE`, so it can read only what the modification needs. Both are implemented and read whole documents to use two paths out of them — `id` and the partition key — which for a row model carrying whole documents is the whole cost of the statement. |
| `SupportsWatermarkPushDown`, `SupportsSourceWatermark` | **only with the change feed.** Streaming concepts; the change feed is the analogue, and `_ts` the natural watermark. See *change feed*. |

### Lookup abilities

| Flink | Here |
|---|---|
| `FullCachingLookupProvider` | **worth considering** for small containers: load the whole thing once and never call the service on a miss, with a reload strategy. A lookup table of a few thousand documents is exactly this. The partial cache — per execution and, by declared policy, across them — is done; see `DESIGN.md` under *The lookup join's caches*. |
| Lookup retry (FLIP-234) | **probably not.** Flink retries a lookup that comes back empty, for late-arriving reference data. The SDK already retries throttling, which is the failure that actually happens here. |

What the lookup join still declines, after #193 took left joins and `UUID` keys:

- **A `UUID` key whose form is guarded** — *small.* The form is asked of the container's unconditional
  facts. One stated under a discriminator holds of the documents the probe side's own filter admits, and
  that filter is in the same statement as the restriction, so the probe's conjuncts could prove the guard
  the way `CosmosFactRewriter` proves one for a sibling conjunct.
- **Other converted keys** — *small each.* A `CAST(… AS INTEGER)` over a path declared integer, or an
  instant over a declared ISO shape, is restricted the same way: spell the key in the stored form. Only
  the `UUID` was asked for.
- **A text key over a path not declared a string** — *small, and a measurement first.* Such a key is a
  rendering, and the raw `path IN (@k…)` loses a stored number or boolean that renders as a key, so it
  is declined (see `DESIGN.md` under *What the lookup join reaches*). Guarding the restriction —
  `OR IS_NUMBER(path) OR IS_BOOL(path)`, rechecked by the join's key comparison — is sound and fetches
  every non-string document per batch; spelling each key as the string and, where it reads as one, the
  number and the boolean it renders from is tighter and needs Calcite's rendering of every number
  pinned. Either wants the cost of a mixed-type container measured against reading it once.

### Sink abilities

| Flink | Here |
|---|---|
| `SupportsTargetColumnWriting`, `SupportsRowLevelUpdate` | **Patch** — the `UPDATE` tier, waiting on a column a `SET` could target; see sections 3 and 6. |
| `SupportsDeletePushDown` | **Done, and unverifiable** — the whole-partition delete plans and is gated on a probed account capability; see section 3. |
| `SupportsTruncate` | `TRUNCATE TABLE` — per-partition deletes, or recreating the container, which is cheaper and has different semantics. Worth deciding deliberately rather than by default. |
| `SupportsOverwrite` | Upsert, which is native (`UpsertItemStreamAsync`). |
| `SupportsPartitioning` | Writes routed by partition key. Bulk mode already groups by partition, so this is mostly about telling the planner. |
| `SupportsWritingMetadata` | Writing the per-item `ttl`, which is a real Cosmos feature with no column to put it in today. |
| `SupportsStaging`, `SupportsBucketing` | **Not applicable** — two-phase commit for `CTAS` and bucketed layouts have no Cosmos counterpart; recorded so nobody looks again. |
