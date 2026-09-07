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

**655 tests: 646 passing, 9 skipped**, on net8.0 and net10.0, against Apache.Calcite 2.0.1-pre.11.
The skips are things only a real account can answer; the suite runs against one when
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` name it, and reports inconclusive rather than passing
where the emulator cannot — and each of them detects the gap it is skipping for, so an environment
that closes one asserts rather than going quiet. Several facts in this file and in `DESIGN.md` were
settled by measurement, each time with an Azure account, used and deleted.

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
than once and is recorded in section 6. The row model is the map column and the `_JSON` column, and
a query works off those. What answers the dependency instead is the service's own type predicates —
`IS_NUMBER`, `IS_STRING`, `IS_DATETIME` and the rest — which say per row, at query time, what a
declaration could only promise; see *Rewriting a typed comparison into one the service can evaluate*
in section 4.

The fourth, **a sort key that can be non-nullable**, turned out not to need a declaration at all: a
query that removes the nulls itself settles the null placement, and the planner already carries
that fact. It is done for the promoted columns and out of reach inside the map column — and *why*
it is out of reach says more about the surface than the original argument did. See section 6.

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
what a row costs to move, which for a map row model carrying whole documents dominates.

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

`SET "_MAP" = …` executes as a whole-document replace. What remains is the cheap tier: a targeted
`SET` of a plain document property as `PatchItemAsync`, sending changed properties rather than the
document. The execution ladder above it (static decomposition via a mutation operator, the diff and
blind-patch optimizations) is recorded in `DESIGN.md` under *Updating*.

**This entry used to say the tier was blocked on a typed column, and that was wrong.** A patch sends
whatever value it is handed, so no type is needed, and the path is written in the *statement* rather
than declared anywhere — a planner can see that a single-path `SET` is not a whole-document replace
without anything being declared. What is missing is a way to *write* the statement, and there are
three walls, each measured:

1. `SET "_MAP"['data']['name'] = 'x'` does not parse. Calcite's `UPDATE` grammar accepts only `=` or
   `.` after the target identifier — *Encountered "[" … Was expecting one of: "=" … "." …*
2. `SET "_MAP"."data"."name" = 'x'` parses and the validator refuses it: *Unknown target column
   `_MAP.data.name`*. A `SET` target is resolved against the row type, and a map has no fields.
3. `SET "_MAP" = JSON_SET("_MAP", '$.data.name', 'x')` converts in isolation but dies through a
   connection: `JSON_SET` returns `VARCHAR`, the column is `(VARCHAR, ANY) MAP`, and Calcite cannot
   build a cast spec for it — *Unsupported type when convertTypeToSpec: ANY*. Calcite's SQL/JSON
   functions follow SQL:2016, where JSON is character data, so none of the family can address a map.

**What does work is a source expression already of the map type.** Measured with Spark's
`MAP_CONCAT`, which returns a map: the statement converts, plans, and arrives as a
`CosmosTableModify(updateColumnList=[[_MAP]])` over a calc holding the expression — the shape a patch
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

**The shape chosen is a second column, `_JSON`.** Typed `VARCHAR`, over the same document, so the
standard `JSON_SET`, `JSON_REPLACE`, `JSON_INSERT` and `JSON_REMOVE` type-check against it —
operators every tool already knows, nothing new to name. The column is a handle rather than a
representation: on the write path it is never built, and projected it can be handed over as the
service returned it rather than rebuilt from the map. Its costs are that the row type carries the
document twice, and that a statement naming both columns needs a rule saying what that means.

The alternative considered and not taken was **adapter map-typed operators** — the same functions
declared over `MAP`, one column, no cast, inheriting the refusal that a Cosmos function has no
in-process body. Rejected for using names nobody outside this adapter knows, where the JSON family
is already in every tool.

**Reads through `_JSON` are worth more than they look, and that is the surprise.** The read side was
first written off here on the grounds that Calcite's SQL/JSON functions are string-typed, so pushing
`JSON_VALUE(c."_JSON", '$.price')` down as the bare path `c.price` would hit the same wall as
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

**Which reaches past this entry.** A typed column over a document path is the surface section 6
rejects, and this is one written in standard SQL, in a view, with no operand and nothing declared to
the schema — which is the whole difference. It is still the caller's word — but a wrong word fails rather than lies: `RETURNING INTEGER`
over a path holding a string makes the service return a string where the plan declared an integer,
and `CosmosJson` refuses to coerce it, which is the opposite failure mode from the one section 6
declines an operand for. What it does **not** give is a `RexInputRef`: a `JSON_VALUE` call is an
expression like `ITEM`, so predicate flow, keys and distinctness stay where they are. Section 6's
split holds; this answers the typed half and not the reference half.

**Parity is done, and it was one change rather than sixty.** Every pushdown that needs a document
path asks `CosmosRexTranslator.TryResolvePath` for it — a filter, a projection, a sort key, an
aggregate argument, an unnest array, the partition key extractor, and the full text and vector
legality gates — so teaching that one function `JSON_VALUE` and `JSON_QUERY` gave all of them the
second spelling at once, and `WriteCall` renders the same call as the same path. Measured through a
connection, `_MAP` against `_JSON`, node for node: projection, filter, sort, sort with fetch,
`GROUP BY`, `DISTINCT`, a nested path, a bracketed name, an array subscript, a numeric comparison,
`IS NOT NULL`, `UNNEST` and the lookup join from either side all produce the identical plan, and a
path assembled at run time declines on both sides.

One shape was missing from that list, and it was the one a view is made of: `CAST(… AS VARCHAR) =
'text'` was dropped over the map subscript and not over `JSON_VALUE`, because the test was that the
operand is typed `ANY` and Calcite types the accessor `VARCHAR(2000)` (#71). The cast is now dropped
over either spelling, on a measurement of Calcite's own runtime that the accessor renders what the
cast over `ANY` renders and applies no width. What is deliberately *not* carried over is the same
cast in a projection: `JSON_VALUE` answers null for an object or an array where the reader renders
one, so a `_JSON` view's text columns stay in process. Recorded in `DESIGN.md` under *Casts over
document values*.

**The bare accessor is a conversion, and the parity above compared plans rather than rows.** Measured
against Calcite's runtime, `JSON_VALUE(doc, '$.x') = '30'` keeps the document storing the *number*
30, because SQL:2016 casts the scalar to the returning type and the default is a character string;
pushed as `c.x = '30'` it did not. The adapter behaves like Calcite, so the equality is now held to
the cast form's literal test: unambiguous text pushes, anything else is declined and the split rule
pushes `IS_DEFINED`. *Open, and a decision rather than a fix:* the same reasoning reaches every other
operator over a bare `JSON_VALUE` — `<>`, the ordering comparisons, `LIKE`, `ORDER BY`, and
`RETURNING <number>` against a number, which is the numeric cast `_MAP` declines and bounds — since
Calcite sees a rendered or converted value and the service sees the raw one. The consistent rule
would be to treat `JSON_VALUE(doc, path [RETURNING T])` exactly as `CAST(<path> AS T)` in every
clause, which is what it is; the cost is that a `_JSON` numeric comparison would push a bound rather
than the comparison and a sort on a bare accessor would decline as a sort on a rendered cast does.
Needs the owner's yes and a differential pass over the `typed` container before any of it is built.

Two things the measurement settled that are worth keeping. `UNNEST` needs
`JSON_VALUE(…, '$.tags' RETURNING VARCHAR ARRAY)` — `RETURNING` names array types, and that is the
spelling; `JSON_QUERY` is `VARCHAR(2000)` even `WITH ARRAY WRAPPER` and can never be an unnest
source. And the accepted path grammar is `$` followed by `.name`, `['name']` and `[0]` steps: a
wildcard, a descent or a filter is refused rather than approximated, and the path argument must be a
literal for the reason the full text functions' first argument must be.

**What is left is the patch tier itself** — the rule matching a `JSON_SET`, `JSON_REPLACE`,
`JSON_INSERT` or `JSON_REMOVE` call over `_JSON` in a `TableModify`, a `PatchItemAsync` on the
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

### Geography

The translations, the refusal and the path pushdown are in place, and every form they emit has been
executed against an account. What is left is one sort that could push and does not, one operator that
is not offered, and one thing that cannot be fixed here at all.

- **`ORDER BY` over a distance now pushes** — *built; one shape left to verify.* A distance-ordered
  query used to read every matching document and sort in process. `CosmosSort` now writes the
  expression into the clause — twice over, once selected and once ordered, because Cosmos cannot order
  by a projection alias — and `CosmosSortRule` admits the sort when the projection beneath it is a
  geodesic distance.

  Narrow on purpose. Only `ST_GEOG_DISTANCE` qualifies: the service accepts it in the clause and
  refuses `DateTimeToTicks` and `IIF` with 400, error 2206, so `CosmosProject` records that one
  expression and nothing else. And it is the whole collation or none — a second key beside it draws
  the same 2206 — which is narrower than the multi-key sort the composite-index path handles.

  **What is not verified is the shape a connection presents.** The node and the rule are covered by
  `CosmosRelImplementTests`, which builds `Sort(Project(scan))` directly. A connection wraps the
  finished plan in a calc, which is what strands `ORDER BY RANK` in
  [#46](https://github.com/ikvmnet/calcite-cosmos/issues/46) — that case cannot project its score, and
  this one can, so the outer projection should merely drop a column that the statement still carries.
  Should. Plan the same statement from a `CalciteConnection` and see.
- **`ST_ISVALIDDETAILED` is not offered** — *small.* The one Cosmos spatial function with no
  counterpart in the geography package, and rightly so: it is the service's own rather than a geodesic
  operation anyone else has. It belongs in `CosmosOperators` beside the full text functions, which is
  where this adapter's own operators live. It answers with a document rather than a boolean, so what
  it is typed as wants deciding first.
- **`ST_GEOG_X` and `ST_GEOG_Y` are not pushed** — *not available; recorded so nobody looks again.*
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
  distance it reports and `<` not, so rendering `ST_GEOG_DWITHIN` as `<=` is right rather than an
  inference from PostGIS.
- **A mixed expression is not refused** — *not available; recorded so nobody looks again.* There is no
  `GEOGRAPHY` type — a geography and a geometry are the same type carried by the same class — so
  `ST_GEOG_DISTANCE(ST_BUFFER(g, 0.1), h)` buffers in degrees, measures in metres, and both halves run.
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
  below is the standing warning that a native spelling often is not.
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

**That promise is much narrower than a type**, and worth separating from section 6 for exactly that
reason. Not *this column is a `TIMESTAMP`*, only *these strings share a shape*. An operand could
carry it without settling the typed-column question at all.

**And the mechanism is already built.** An uncast path sorts at the service today — it is why a page
ordered by a raw path reads a page while one ordered by a cast column reads everything, measured and
recorded in section 6. What does not push is the cast. So the change is one rewrite: drop an
order-preserving cast from a sort key, under that promise. Not a new sort pushdown; the existing one,
reached through a cast it currently refuses. Range predicates over the same shape are the easier
half — string comparisons, and those *do* have the recheck escape.

See *Temporal* above, whose prerequisite this is a narrower statement of.

### Clause-level

- **Native `IN` and `BETWEEN` — closed by measurement, not built.** `expandSearch` turns both into
  comparison chains, and the question was whether the native spelling is priced differently.
  Measured on a real account over five hundred documents: `IN` and its OR-chain cost *identically*
  at three, ten and fifty values — 6.06, 7.62 and 16.52 RU, matching to the hundredth — and
  `BETWEEN` costs exactly what its two comparisons do (7.90 RU). Neither form used an index on an
  unindexed path, so the reference's "index-friendly" is a property of the path rather than of the
  spelling. Emitting the native form would be a change with no effect.
- **`DISTINCT` with `ORDER BY` reaches promoted columns and not the map column** — *small, and what
  is left of it waits on section 6.* The null-placement rule refuses a nullable sort key, and a
  query that removes the nulls itself now satisfies it: `WHERE c.category IS NOT NULL ORDER BY
  c.category` pushes, read from `RelMdPredicates` at the rule. That covers the promoted columns.
  It does not reach a path inside the map column, and not for want of a type — such a path projects
  as `ITEM($0, 'name')` rather than as a reference, and `RelMdPredicates` carries a predicate
  through a projection only where the projection is a reference. See `DESIGN.md` under *Ordering is
  a total order over JSON types*, and section 6 below, whose case this sharpens.
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
and cannot be applied blindly (`* 1` over a string silently drops it from `MIN`/`MAX`), and a path
inside the map column is `ANY` — but the service answers the type question itself, so
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
`CosmosDistanceSortPlanningTests`, which sets it for that reason.

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
  with real index awareness rather than living in the map column.

### Recorded decisions worth revisiting

- **`SELECT VALUE` for a single column** — `DESIGN.md` chose the uniform object form deliberately,
  "whatever the arity", and the materializer depends on it. A single-column projection could be bare
  scalars. Reversing a recorded decision is the work; the code is trivial.
- **`SELECT *` sends promoted columns twice** — `_MAP` is the whole document and every promoted column
  is a path within it. Reading them out of the map value client-side would avoid it; the saving is a
  few short scalars against a whole document, so smaller than it first looks.

---

## 6. Row model and types

- **A typed column over a document path — *rejected; written down here so it stops being
  reopened*.** The question was whether a caller could declare paths and types — a `columns` operand,
  or the container's computed properties — so that the planner could see a type where the map column
  gives `ANY`. The answer is no, and it has been given more than once. The row model is the map
  column and the `_JSON` column, and a query has to work directly off those; a surface that asks the
  caller to describe the documents before querying them is the thing being avoided, not a feature
  that is missing.

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
  `RexInputRef`, and a document path projects as `ITEM($0, 'name')` over the map column. So what a
  declared column buys is not only a type the planner can see but a path that projects as a
  *reference*, at which point Calcite's whole existing metadata layer — predicates, nullability,
  keys, distinctness — begins working over it with no adapter code at all. That is a larger and
  more concrete account of what the map row model costs than "no type to work with". It is a cost
  the model accepts, not an argument to reopen it. Measured; recorded in `DESIGN.md`.

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
- **Binary** — *small.* `BINARY`/`VARBINARY` read base64 from a JSON string. Unverified against the
  service, because nothing in the test data is binary.
- **Temporal representation** — see *Temporal* above. The reading side handles ISO strings and epoch
  numbers; there is no *declared* basis for deciding which a column holds and there will not be one,
  so the basis is asked of the service per row.

---

## 7. Provider and integration

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

## 8. Observability

- **`CosmosDiagnostics`** — *small.* The one signal not surfaced: a large JSON blob per response, so
  it wants a switch of its own rather than to ride on the `cosmos.query` span.
- **RU regression tracking** — *medium.* The charge is on the histogram; assert that a query shape
  does not get more expensive.

---

## 9. Testing

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

## 10. Unsettled questions

These are not features. They are things believed but not measured, and each one is a defect waiting
for the right query.

- **Two-argument `TRIM` and `TRUNCATE`.** Left out for want of a measurement.

---

## 11. Read off Flink's connector SPI

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
| `SupportsRowLevelModificationScan` | **worth taking.** The scan is told it is feeding an `UPDATE`/`DELETE`, so it can read only what the modification needs. Both are implemented and read whole documents to use two paths out of them — `id` and the partition key — which for a map row model is the whole cost of the statement. |
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
