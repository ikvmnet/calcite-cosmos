# Appendix E. Development, the emulator and the test suite

## E.1 Running the emulator

The Linux Cosmos DB emulator runs in Docker:

```sh
docker run -d --name cosmos-emu -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

Connect to `http://localhost:8081/` with the emulator's well-known key and `"connectionMode":
"gateway"` (Chapter 5).

## E.2 How the emulator differs from Azure

The emulator is convenient and **not a substitute for the service**. It has been found both to accept
statements Azure rejects and to reject features Azure implements:

| | emulator | Azure |
| --- | --- | --- |
| `ORDER BY t` over `JOIN t IN c.tags` | accepted | rejected (400) |
| full text functions and `ORDER BY RANK` | rejected (`SC2005`) | accepted |
| full text policies and indexes | accepted on create, then reported absent | kept |
| composite indexes | discarded on create | kept |
| multi-key `ORDER BY` without a composite index, a key repeated included | accepted | rejected (400) |
| `ORDER BY` over a path the indexing policy excludes, `_etag` included | accepted | rejected (400) |
| `ORDER BY` over a computed property no included path names | computed properties dropped on create | rejected (400) |
| `ORDER BY RANK … ASC` or `DESC` | rejected (`SC2005`) | rejected — no direction is taken |
| two string constants compared, `'\uE000' < '\uD83D\uDE00'` | by code point | by UTF-16 code unit |
| a container's document count | always zero | the count, lagging recent writes |
| request charges | not representative | real |

So: develop against the emulator, but verify sorting, full text search, and anything cost-related
against a real account. A plan chosen on the emulator is chosen without row counts (Chapter 6).

## E.3 The sample program

`samples/Apache.Cosmos.Sample` joins a CSV file of suppliers to a Cosmos container of products through
a view, prints each query's plan, the statements sent to Cosmos and what they cost. It needs the
emulator on `localhost:8081`, seeds both sources, and is safe to re-run:

```sh
dotnet run --project samples/Apache.Cosmos.Sample/Apache.Cosmos.Sample.csproj
```

## E.4 Building

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

The build resolves Calcite through IKVM's Maven support, including an Apache snapshot repository while
the Calcite version the packages use is unreleased (see `Directory.Build.props`).

## E.5 The test suite

Most of the suite plans and renders statements with no service at all. The part that executes against
Cosmos **starts an emulator for itself** where Docker is available and nothing is already listening on
port 8081. Nothing is started unless a test needs an account, so a run of the planner tests never waits
on one.

To use an emulator you started — which is what CI does — start it before the run and the suite finds
it.

To run the same suite against a **real account**, set:

```sh
COSMOS_TEST_ENDPOINT=https://myaccount.documents.azure.com:443/
COSMOS_TEST_KEY=<key>
```

A real account takes precedence over any emulator. Without Docker and without an account, the
service-backed tests report themselves skipped (inconclusive), so the suite stays runnable — but be
aware of what a skipped run does not check: **a pushdown that plans correctly and returns the wrong
rows is invisible without a service.**

### Differential tests

The heart of the service-backed suite is a differential corpus: each statement is planned twice — once
with the adapter's rules, once with every pushdown disabled so Calcite evaluates everything in process
— and both run against the same container. Equal rows, or a defect. Every new pushdown should bring its
statements to the corpus.

A corpus grown only from the defects already found says little about the ones not yet found: a review
that ran 31 ordinary statements one step from it found 21 that answered wrongly while the corpus had
none. Those statements are kept in it, named `P1` to `P27` after the review, beside what was found while
fixing them. When adding a pushdown, add the statements that put every JSON type at the path it reads —
the `typed` container holds one per document for that — and its negation, its null test, and the same
expression inside a `CASE`. Then read it as a value as well as a truth: selected, grouped by, and
compared with `FALSE`, since a `WHERE` cannot tell false from unknown and a column can. A second
review of the fixes, sweeping exactly those neighbours, found some thirty more; they are in the corpus
under the names it gave them. Where a pushdown reads a declaration, add its statements over the
`declared` container too, whose documents keep what it declares: a conclusion the operator does not
license is wrong only where something is declared. Where it sends a number, ask it past 2^53 as well,
over `declared`'s `v`, which holds integers a double does not: a bound loosened by one, or a literal,
that the service reads as a double comes back as another number.

What counts as agreement where one side fails is decided in one place, `Refines`:

- Where Calcite raises, the pushdown may answer anything.
- Where Calcite answers, the pushdown may not fail. The one exception is the service failing a
  statement that names an out-of-domain function: `ASIN`, `ACOS`, `SQRT` or `LN`.

A statement whose in-process plan raises compares nothing, so it goes in `WithoutAnOracle` with the
reason, not in the corpus.

**`EveryPredicatePartitionsTheRows`** generates predicates from a fixed seed. For each one φ, it
requires that `WHERE φ`, `WHERE NOT φ` and `WHERE φ IS NULL`, pushed, return every document exactly
once between them, and that each returns the documents Calcite puts there. When it fails, it reports
the smallest part of each failing predicate that still fails. Set `COSMOS_MEASUREMENT_LOG` to a file
path to have every failure written there in full, with the Cosmos statement each partition sent.

It runs at two sizes:

- **By default, the first 200 predicates**, which is what every run of the differential class pays:
  three to five minutes of judging on a workstation, and the class about eight.
- **With `COSMOS_PARTITION_GATE=1`, all 1,000.** That is Stage 0's gate, and it takes sixteen to
  twenty-one minutes of judging, and the class about twenty.

The seed does not change with the size, and the predicates are drawn in order, so the default run asks
exactly the first 200 the full run asks. A predicate that fails in one fails in the other, as the same
statement. Where the full run belongs, a nightly job or a gate before merging, is not settled yet
(`TODO.md`, section 10); until it is, run it by hand after changing the translator or the split rule.

**`EveryOrderIsOneCalciteAllows`** generates statements whose answers depend on an order — sorts over
every reading, `DISTINCT`, a subquery's page, `UNION ALL`, `MIN`/`MAX`, keysets, comparisons with a
parameter — and judges each by whether Calcite's semantics *allow* its answer, not by whether it equals
the answer Calcite's own run gave: ties are free, and a `FETCH` may take any of a tie's rows. It also
holds every `ORDER BY` a plan sends to what Azure accepts, which the emulator does not: no excluded path
(`_etag` is excluded by default), and a composite index for every order of more than one key. Its three
containers, `orders_plain`, `orders_declared` and `orders_ascii`, are created the first time it runs. On
an account, a document in a container with the yardstick's composite indexes costs about 25 RU to write,
more than a 400 RU/s container takes in the time the seed writes them: the seed writes a throttled
document again after the interval the service asks for, and a statement the service throttles still fails.

It runs with the class, and fails on any answer Calcite does not allow. Two families of wrong answer
that are no order's — a projected double printed two ways, and `CHAR_LENGTH` pushed as `LENGTH` — are
findings of their own, recorded in `TODO.md`, and are reported apart where the statement sent no order.
It also holds a floor for how many statements push each kind of order — sorts, extremes, pages and
comparisons a parameter decides — in every container and in each, so that a change declining more than it
must fails it too, and a change losing what the reader completes shows in the containers that declare no
characters:

```sh
Apache.Calcite.Cosmos.Adapter.Tests.exe \
  -method Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.CosmosDifferentialTests.EveryOrderIsOneCalciteAllows
```

- **By default it draws 200 items**, about two minutes against the emulator.
- **With `COSMOS_ORDER_GATE=1`, 1,000**, about six; the default is their prefix, as above.
- **`COSMOS_MEASUREMENT_LOG`** receives every smallest failing statement with its family and what it
  sent, and **`COSMOS_ORDER_TRACE`** names a file that receives one line per statement — container,
  form, what was pushed, the verdict and the family — for comparing two commits.

`CosmosServiceOrderModelTests` asks the same question with no service at all: a model of each
partition's order, the SDK's merge and Calcite's, over every value a fact set admits. Its sweep,
`EveryVerdictOfTheQuestionHolds`, runs by default, and holds an order completed as the rows arrive to the
reader's own check: every value it calls an anchor compares alike with every other under all three orders,
and an extreme read again up to the first anchor is Calcite's. `CosmosOrderedCursorTests` runs the reader
itself over canned streams in the service's orders, the merge of two partitions among them.

### Measurements

Tests under `Measurements/` assert what Calcite, IKVM or the service does — not what the adapter does.
A failure there is news about a dependency, and usually means a recorded assumption in `DESIGN.md`
needs revisiting.

Each measured fact a pushdown relies on is also a row in `Sql/CosmosAxioms.cs`, with where and when it
was measured and the test that pins it, and a test checks every named test still exists. A new
measurement a rendering depends on should add its row. A row measured on the emulator alone is owed on
Azure: run the measurement with `COSMOS_TEST_ENDPOINT` naming an account, and add `Azure` to the row.

One measurement needs more than an account: `CosmosOrderMergeMeasurementTests` needs a container with two
physical partitions, which only throughput above 10,000 RU/s makes, so it creates none. Name one the
account already has:

```sh
COSMOS_TEST_MULTIPARTITION_DATABASE=<database>
COSMOS_TEST_MULTIPARTITION_CONTAINER=<container>
```

Its partition key must be one top-level path and its indexing policy must index `/s`. The test writes a
few dozen documents tagged with its run and deletes them before it ends; without the variables it skips.
Beside the merge it measures, it runs the adapter's reader over the same documents through the adapter's own
executor — the stream, every window, the `MAX` — and holds each to Calcite's order: the one place the
reader meets a merge the service made rather than a model of one.

### Generated soundness checks

Some tests draw their cases at random from a fixed seed, and run a CI-sized number of them with every
run of the suite: `SoundnessTests` for the theory of JSON, `PredicateSoundnessTests` for the predicate
oracle checked on documents drawn from random declarations, `PlantedBugTests` for the faults that
checker must catch, and `IrEvalCalciteTests` for the evaluator it checks against, held to Calcite. Each
has a long run, marked explicit, for a change to the rules they cover or a Calcite or IKVM upgrade:

```sh
Apache.Calcite.Cosmos.Adapter.Tests.exe -explicit only \
  -method Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation.PredicateSoundnessTests.NothingTheOracleSaysIsFalseOfADeclaredDocumentOverALongRun \
  -method Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation.IrEvalCalciteTests.TheEvaluatorAgreesWithCalciteOverALongRun
```

A failure prints the trial's seed, its declaration and its filter; `PopulationD.Run(seed)` replays one
trial alone.

## E.6 Where the reasoning lives

- [`DESIGN.md`](../src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) — why each decision was made, and what
  the service was measured to do.
- [`TODO.md`](../TODO.md) — what is left, sized and reasoned.
- [`Facts/Saturation/README.md`](../src/Apache.Calcite.Cosmos.Adapter/Facts/Saturation/README.md) — the
  catalogue of the theory a container's declared facts are reasoned in, and of the predicate rules the
  oracle beside it uses. Every fact a plan reads off a filter is the oracle's, through
  `Metadata/CosmosFilterStatement.cs`; `DESIGN.md`, under *The oracle in the planner*, says which rule asks
  which question, and the licence each question is answered under.

Two explicit measurements put a change to what planning reads beside the commit before it. Run each on both
builds and compare:

```sh
COSMOS_PROBE_OUT=probes.txt Apache.Calcite.Cosmos.Adapter.Tests.exe -explicit only -class Apache.Calcite.Cosmos.Adapter.Tests.Planning.CosmosPushdownProbeTests
Apache.Calcite.Cosmos.Adapter.Tests.exe -explicit only -showLiveOutput -class Apache.Calcite.Cosmos.Adapter.Tests.Planning.CosmosPlanningTimeTests
```

The first writes what every corpus statement, and a set of probes over a discriminated container, sends;
the second prints how long the corpus and the self-join hierarchy take to plan, new and again, and the ten
corpus statements slowest to plan new. A clock is the machine's, so interleave the two builds' runs and
compare medians; a Debug build costs between one and three times a Release one.

---

[← Previous: Cosmos SQL in brief](appendix-d-cosmos-sql.md) · [Contents](README.md) · [Appendix F: Glossary →](appendix-f-glossary.md)
