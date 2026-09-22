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

### Integration requirements, recorded in the README

Three things trip a host and fail with messages that name nothing useful: the calc rules must run as
a *pass* after the planner (`Programs.CALC_PROGRAM`'s shape — given to Volcano they do nothing), a
model must name `CosmosSchemaFactory` assembly-qualified with the assembly already loaded, and
`defaultNullCollation` defaults to the opposite of the service's null placement, so every sort on a
document path silently declines until the connection says `LOW`. The README carries all three with
the reasoning.

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

### Nothing is remembered between connections — *medium, and it now costs more than it did*

`CosmosSchemaFactory.create` runs per model read, which in the ADO.NET path is per *connection*, so
every connection builds fresh `CosmosContainerMetadata` and with it fresh lazy cells. Within a
connection each fact is computed once; across connections nothing is shared, though the client can
be. That was two round trips per container for statistics; the whole-partition delete capability
adds a third for any connection that plans one, and a short-lived-connection application pays them
all again each time.

**The cache hangs off the schema**, which is where the lookup cache already hangs and for the same
reason: no global static, no leak between accounts, and the lifetime is the caller's to choose. It
does not help a host that rebuilds its schema per connection — but that is the honest shape, because
the alternative is a process-wide cache keyed by `CosmosClient.Endpoint` that outlives every
decision anyone made about it. ADO.NET pushes callers to recreate connections freely and pool them
underneath; reusing the *schema* across those connections is the documented way to keep what it
learnt, and the README should say so beside the client-factory guidance.

Three facts, three lifetimes, and they are not the same:

- **The container definition** — partition key paths, indexing policy. Changes only by a control
  plane operation; cache for the life of the schema.
- **The whole-partition delete capability** — a property of the account, changed only by a support
  request. Same treatment.
- **Statistics** — genuinely mutable, which is why they carry a time to live and a
  `RefreshStatistics()`: without both, one connection's stale row count would outlive the connection
  that fetched it.

**Deferred: the shape this wants belongs to the provider, not here.**

What was asked first — whether a host can reuse a schema through `Apache.Calcite.Data` — has an
answer. `CalciteConnection.RootSchema` is public and writable, so a host builds one schema and
registers the same instance on each connection; what is registered there survives a `Close`/`Open`
cycle, the engine session being torn down only on dispose. A *new* connection gets a new session and
a new root schema, so the registration is per connection while the object, and everything it learnt,
is not. The README carries that as the way to keep the reads down today.

It is a workaround rather than the design. A model-built schema is still constructed per connection,
and making that path share anything means changing how `CalciteConnection` builds schemas — which is
`Apache.Calcite.Data`'s to decide, in a different repository, and not something to design around from
here.

So this waits on that, and the note about *where* the cache belongs stands: on the schema, for the
reasons above. Whatever the provider ends up offering, the schema is where the logic hangs, and
nothing here should grow a process-wide cache in the meantime.

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
as it did, and needs no bound; `DESIGN.md` records the measurement. *Settled by the same measurement:* the
other operators over the bare text accessor — `<>`, the ordering comparisons and `LIKE` — diverged in
both directions over the `typed` container, and are now declined and weakened to the case the two
agree on, `NOT IS_STRING(x) OR <comparison>`. The string case is exact, the rendering being the
value; every other document reaches the recheck. The same holds over a *view's* column bound to the
accessor, which is the spelling every typed caller writes and which had been pushed raw (#83);
`DESIGN.md` records it under *Projecting a cast to text*. `ORDER BY` was measured alongside them and
did not diverge, so nothing was done to it — which is a statement about that corpus rather than a
proof, and a sort still has no weakening to fall back on if one is found.

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
writer, the routing in `CosmosSequences`, and the refusal of every form that cannot be rendered. The
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
- **A host must chain a library operator table** to name `LEFT`, `RIGHT`, `REVERSE` or `REPEAT` at
  all: Calcite's standard table carries none of them, and the adapter translates whatever arrives
  rather than deciding which library a caller uses. Worth a line in the README beside the
  `CosmosOperators` chaining it already documents — *small*.
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
  decimal places, both needing Cosmos's two-argument arity **verified** first; `IS TRUE`/`IS FALSE`/
  `IS DISTINCT FROM`, expressible with the `??` operator once the null-versus-undefined semantics are
  measured.

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
- `CosmosFactRewriter.TryLowerInstant` — `=`, `<>`, `<`, `<=`, `>`, `>=` over `CAST(<path> AS
  TIMESTAMP)` and over `JSON_VALUE(…, RETURNING TIMESTAMP)`, lowered to a string comparison. The
  flipped orientation reverses the operator, which is the case that would have been silently wrong
  rather than merely unpushed. Equality is gated on `PreservesEquality` and the rest on
  `PreservesOrder`.
- `CosmosFactRewriter.TryLowerUuid` — the same six over `CAST(<path> AS UUID)`, on the same two
  gates, **as of #142**. It took no operator and built an `EQUALS` before, which was right while the
  engine compared UUIDs as signed halves and an unconfined canonical form preserved equality alone;
  [CALCITE-7716](https://issues.apache.org/jira/browse/CALCITE-7716) made the comparison unsigned in
  1.43, so the range is licensed and is now written. A keyset-paginated `WHERE id > @last ORDER BY
  id FETCH NEXT n` is the shape that wants it, and the page beside it is the entry below. The
  reversal is pinned in two places, the rewriter's own test and the planning one, because it is the
  case that selects the complement rather than merely failing to push; and a range pins no partition,
  `CosmosPartitionKeyExtractor` reading an equality and nothing looser.

**Not built, and each for a stated reason:**

- **`ORDER BY CAST(<path> AS TIMESTAMP)` still does not push** — measured, `SELECT VALUE c FROM items
  c` under a `ClrEnumerableSort`. This is the cast-drop the paragraph above calls *the* change, and it
  is the one thing here that is not done. The range rewrite reaches it because a filter's predicate is
  rewritten before the split rule reads it; a sort key is not a predicate. The key is a *computed
  projection*, so `fields[index]` is null and the key is refused before its form is ever asked about.
  Licensing it means the implementor carrying a temporal binding per ordinal the way it already
  carries `SortableExpressions` — `CosmosProject.Implement` recording that ordinal *n* is the path
  `c.at` read as an instant — so that the rule and the implementation decide on the same binding. Note
  the spelling is less urgent than it looks: section 6 records that `CAST(<string> AS TIMESTAMP)`
  accepts only `yyyy-MM-dd HH:mm:ss` and *raises* on every ISO-8601 form a document stores, so the
  cast is the spelling that cannot work in process, while `RETURNING` is the one that does.
- **A space-separated instant is not recognised, and the obstacle is the normaliser rather than the
  shape.** `2024-01-15 12:30:00Z` is as fixed as its `T` spelling and would sort as well — Python's
  `isoformat(sep=' ')` and a good many SQL exports write it. But `Normalise` strips whitespace outside
  a character class, on the stated grounds that it "means nothing in an unextended ECMA-262 pattern",
  and that is **not so**: whitespace is significant in ECMA-262, which is the dialect JSON Schema
  specifies, and only the `x` flag other dialects have makes it insignificant. So the space-separated
  pattern cannot be told apart from one written with no separator at all, and registering it would
  claim this form for that one. Two consequences, and the second is the one worth acting on: the
  shape stays unsupported, and a declared pattern carrying *significant* whitespace is today
  recognised as the form it would be without it — which is a wrong answer rather than a missing
  feature, since `RenderUuid` then writes a spelling the stored values do not have. The rule is
  pinned by a row in `TheSpellingsInTheWildAreRecognised`, so flipping it is a deliberate change and
  not a bug fix to slip in; `ASpaceSeparatedInstantIsNotYetRecognised` is the row that would flip
  with it.
- **`PARSE_TIMESTAMP`, `PARSE_DATETIME`, `TO_TIMESTAMP` are not recognised.** Adding them is not the
  one-line extension of `TextAccessorOf` it appears to be, because the parse carries a *format* and
  the rewrite is an equivalence only where that format denotes the path's stored shape — a format
  that does not match parses to null for every row, and ordering by the path is then not what
  ordering by the parse means. Recognising the format is the *form-preserving chain* this section
  already names as the notion needed; a spelling table per form, in the manner of
  `CosmosStoredForms.Recognise`, is the sound way to it and none of it is measured yet.

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
- **`DISTINCT` with `ORDER BY` reaches promoted columns and not unpromoted document paths** —
  *small, and what is left of it waits on section 7.* The null-placement rule refuses a nullable sort
  key, and a query that removes the nulls itself now satisfies it: `WHERE c.category IS NOT NULL
  ORDER BY c.category` pushes, read from `RelMdPredicates` at the rule. That covers the promoted
  columns. It does not reach an unpromoted document path, and not for want of a type — such a path
  projects as an accessor call rather than as a reference, and `RelMdPredicates` carries a predicate
  through a projection only where the projection is a reference. See `DESIGN.md` under *Ordering is
  a total order over JSON types*, and section 7 below, whose case this sharpens.
- **`TOP` — closed by the same measurement.** Emitted for a rank clause and nowhere else. `TOP 10`
  and `OFFSET 0 LIMIT 10` cost the same 2.37 RU on a real account, so the spelling the adapter
  already emits is the cheaper of nothing.

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
`LOW` or every sort over a nullable path silently declines, which the README carries as an
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
was a missing pass read as a defect.** `ClrEnumerableProject.Implement` throws exactly as Calcite's
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
  cannot answer. Measure it against an account before binding it.
- **Unique key policy** — *small.* Declared unique keys are keys `getStatistic` does not report.
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
  paths. Declared metadata is the one kind this adapter trusts, so they should promote to real columns
  with real index awareness rather than being reached as ordinary document paths.

### A row's width is weighed at the wire and nowhere else — *medium*

`CosmosToClrEnumerableConverter` is the wire, and since #125 it costs rows times the width it
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

### Recorded decisions worth revisiting

- **`SELECT VALUE` for a single column** — `DESIGN.md` chose the uniform object form deliberately,
  "whatever the arity", and the materializer depends on it. A single-column projection could be bare
  scalars. Reversing a recorded decision is the work; the code is trivial.
- **`SELECT *` sends promoted columns twice** — `DOC` is the whole document and every promoted column
  is a path within it. Reading them out of the document client-side would avoid it; the saving is a
  few short scalars against a whole document, so smaller than it first looks.

---

## 6. A container's declared facts

Built, #93: a JSON Schema on the `containers` operand, compiled to a Horn theory of
`(path, claim)` atoms the planner asks. `DESIGN.md` under *What a caller may declare beyond it* is
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
  `CosmosSort.OrderIsLexical` now gates it. What is still not built is this entry's original case: a
  sort key that is a *chain* rather than a path — a `CAST`, or `PARSE_DATETIME`, `TO_TIMESTAMP`, or
  the `REPLACE`/`SUBSTRING`/`CAST` a view writes — where the rewrite is to drop the order-preserving
  chain and leave the raw path. See *Ordering by a rendered column* below, which turns out to be the
  same mechanism at a different site and is the cheaper way in.
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

**Built.** `CosmosImplementor.OrderingPaths` carries, per ordinal, a path a sort may order by where
the ordinal binds to none — recorded by `CosmosProject.Implement` the way it already records
`SortableExpressions`, and decided by `CosmosProject.OrderingPathOf`, which `CosmosSortRule` calls
too so the rule and the implementation cannot drift apart. Measured, with a `FETCH`:

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
`CosmosFactSet.IsAlwaysScalar` is now where both ask it.

**The temporal spelling is _not_ closed by this, and the obvious reading that it is was wrong.**
`ORDER BY CAST(<path> AS TIMESTAMP)` looks like the UUID case with a different type, and the sort
machinery would carry it — `OrderingPathOf` admits a temporal cast and the form licenses the order.
The obstacle is a step earlier: the projection does not push, so there is no `CosmosProject` to
record the binding on. Measured:

```
ClrEnumerableSort(sort0=[$0], dir0=[ASC])
  ClrEnumerableProject(at=[CAST(JSON_VALUE($0, '$.at')):TIMESTAMP(0)])
    CosmosToClrEnumerableConverter
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

**Built: a contradicted predicate keeps nothing.** `CosmosFact.Excludes` is the exclusion table beside
`Entails` — a separate table because exclusion is not entailment's negation, most pairs being neither,
and `CosmosFactSet.IsContradictory` asks it of every path. Where the container's declaration and the
query's own conjuncts cannot both hold, `CosmosFactRewriter` answers the constant. Declare
`status` as `enum: ["active","archived"]`, ask for `'deleted'`, and the plan carries
`CosmosFilter(condition=[false])` rather than a comparison. A guarded declaration reaches the same
answer by the argument it always uses: over a document the guard does not cover the conjunct that
proved it has already excluded the row.

**It still costs one round trip, and should not.** The constant is reached inside the converter, so it
lands on a `CosmosFilter` — and `CoreRules.FILTER_REDUCE_EXPRESSIONS`, which turns an always-false
filter into an empty `Values`, is configured for a `LogicalFilter` and does not match it. Registering
it changes nothing; measured. Getting to no statement at all means detecting the contradiction on the
*logical* filter, in a rule of its own that produces the empty relation, which is its own change.

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

**Also built: a declared tautology is not asked.** Where the container guarantees both that a path is
present and what value it holds, a query comparing that path to that value decides nothing, and
`CosmosFactRewriter` answers the constant — which `RexUtil` then drops from a conjunction. Two claims
are needed and the second is the whole of the argument: a declared value says what a path holds *if it
holds anything*, so a container declaring `kind` is `"A"` and nothing more still admits a document
with no `kind`, over which `kind = 'A'` is unknown and the row is dropped. Removing the conjunct would
keep that row. Only `required` beside the `const` rules that document out. It is read from
`Derive(null)` and never from a guarded fact, because the conjunct being deleted may be the one that
proved the guard.

**The payoff this was predicted to have, it does not have, and that is worth recording.** This entry
said it would unblock a point read, on the reasoning that `TryExtractPointRead` refuses any conjunct
that is not an `id` or partition-key equality — so a redundant conjunct stood between a routed query
at 2.82 RU and a point read at 1.00. Measured, the read is reached either way: `CosmosPointReadSplitRule`
already partitions the conjunction and holds the extra conjunct back, which is what #92 built it for.

What changes is that the conjunct held back was then **rechecked in process for every row the read
returned**, and a conjunct no document can fail has nothing to recheck. Measured, over the same query
with and without the declaration:

```
declared:  CosmosToClrEnumerableConverter … (the conjunct is gone)
plain:     ClrEnumerableFilter(condition=[=(JSON_VALUE($0, '$.kind'), 'A')])
             CosmosToClrEnumerableConverter …
```

A plan node and a per-row test rather than a request. Smaller than this entry claimed, and still worth
taking — and the statement sent is one predicate shorter either way.

**Also built: a declared partition key routes.** `CosmosPartitionKeyExtractor` now seeds its pinned
map from what the container declares outright, for the paths the predicate did not pin itself — so a
query pinning only an `id` supplies the key and reaches a **point read**, which is the saving the
tautology entry above was predicted to have and does not. Measured, over the same query:

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
and routing past it would lose it. Outright only, and for a sharper reason than elsewhere too: the
routing applies to the whole statement while a guarded fact holds only of the rows a sibling conjunct
keeps, so a key pinned from one would route away documents the statement had not excluded.

**What the predicate pinned still wins.** A declaration disagreeing with the predicate describes a
document the predicate excludes, which is the contradiction `CosmosFactRewriter` settles rather than
something to resolve by routing.

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

**And it is worth recording what it cost to get right, because the first attempt was unsound.**
`IsDeclaredString` reads the translator's fact set, which is the declaration *closed under what the
query's own conjuncts proved* — and `CosmosFact.Entails` reads `EqualTo v` as `OfType` of `v`'s type.
So over `JSON_VALUE(…, '$.label') = '30'` the extractor records `EqualTo "30"`, that entails
`OfType String`, and the comparison certifies **itself** exact: precisely the conflation the guard
exists to prevent. Four tests in `CosmosPlannerTests` caught it.

The fix asks `Derive(null)` instead. Note this is not an argument against the derived set generally —
the ordering branch is right to use it, because there the fact comes from a *sibling* conjunct which
reaches the service too, so the rows the ordering sees are rows the equality already confined to a
string. The circularity is only where the fact comes from the conjunct being translated, which cannot
confine anything it is itself the test of.

### The numeric forms stop at whole numbers — *small, and deliberate*

A fixed-point decimal spelled as a string — `^[0-9]{5}\.[0-9]{2}$` — is injective and its lexical
order is numeric order, by the same argument the padded integer uses, so it belongs beside them. It
is not built because it needs a second dimension on the form (a scale as well as a width) and its own
rendering, and nothing has asked yet. The approximate types are a different matter and are refused on
purpose: a stored spelling maps to one `DOUBLE`, but a `DOUBLE` maps back to many, so a literal could
not be written into the container's shape without changing which documents match.

Also open beside it: a declared `type: string` ought to delete the stored-number disjunct the lowered
equality still carries, and does not — the same shape as the entry above, the exactness test reading
the literal rather than the fact set.

### Facts about array elements — *small, and waiting for a consumer*

`CosmosDocumentPath` carries property names only, so `items` and `prefixItems` state nothing. The
path model has room for an element segment and `ARRAY_CONTAINS` and the traversal are the rules that
would read one. Not built because nothing asks yet, and a path model that admits an index has to
answer what `$.tags[0]` means against a fact declared for `items`.

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

- **A schema carried by reference** rather than inline. Inline is the right default and the README
  says why, but a long schema buries the operands beside it, and a path or URL wants deciding — a URL
  being a fetch at schema registration.

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

  **The sort key is no longer one of the four, and what it left behind reframes the other three.**
  A nullable sort key is now reachable when the query itself removes the nulls — for a promoted
  column. It is not reachable for a document path, and the obstacle turned out not to be the type
  at all: `RelMdPredicates` carries a predicate through a projection only where the projection is a
  `RexInputRef`, and a document path projects as an accessor call. So what a declared column buys is
  not only a type the planner can see but a path that projects as a *reference*, at which point
  Calcite's whole existing metadata layer — predicates, nullability, keys, distinctness — begins
  working over it with no adapter code at all. That is a larger and more concrete account of what the
  row model costs than "no type to work with". It is a cost the model accepts, not an argument to
  reopen it. Measured; recorded in `DESIGN.md`.

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

---

## 8. Provider and integration

- **Connection options as operands** — *small.* Consistency level, preferred regions, application name,
  for callers who do not want to write a factory.
- **Lazy subschemas** — *small.* Container *definitions* are read eagerly when an account-level schema
  is built, so an account with many databases pays a read per container to reach one. Statistics are
  already lazy; the definitions want a lazy `Map`.
- **Client disposal** — *small.* The schema owns a client for the life of the process because Calcite
  offers no disposal hook. Worth revisiting against `SchemaPlus` rather than left as a comment.
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
- **A malformed response's failure mode** — *small.* A lookup-join stub returning raw documents
  instead of the statement's projection once produced a null reference inside the join's result
  selector, and which access produced it was never established. Worth knowing whether a malformed
  service response fails loudly or quietly.

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
