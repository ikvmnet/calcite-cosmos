# Apache.Calcite.Cosmos.Benchmarks

Benchmarks for the query planner, not for Cosmos DB.

Nothing here opens a connection, reads a document or measures a request charge. Every measurement
ends at a plan — or at the Cosmos SQL a plan renders to — and the containers it plans against are
declarations rather than data. What is being timed is the work between a statement arriving and the
adapter knowing what to send: parsing, validation, conversion to a relational tree, the rewrites a
host applies to it, and the cost-based search that decides how much of the statement the service
will answer.

That search is the part worth watching. A rule that fires where it should not, a metadata lookup
that stops being cached, a cost function that stops distinguishing two plans — none of these break a
test, and all of them show up here first.

## Running

```sh
dotnet run --project src/Apache.Calcite.Cosmos.Benchmarks -c Release -f net10.0
```

That lists the benchmark classes and asks which to run. To go straight at one:

```sh
# one area
dotnet run --project src/Apache.Calcite.Cosmos.Benchmarks -c Release -f net10.0 -- --filter '*Aggregate*'

# everything, which is long
dotnet run --project src/Apache.Calcite.Cosmos.Benchmarks -c Release -f net10.0 -- --filter '*'

# a quick look, three iterations instead of ten
dotnet run --project src/Apache.Calcite.Cosmos.Benchmarks -c Release -f net10.0 -- --filter '*Pipeline*' --job short
```

The whole of [BenchmarkDotNet's command line](https://benchmarkdotnet.org/articles/guides/console-args.html)
works — `--filter`, `--job`, `--list`, `--runtimes`, `--exporters`, `--memoryRandomization`. Results
land in `BenchmarkDotNet.Artifacts/results` as GitHub-flavoured markdown and as JSON, the second so
that two runs can be compared by something other than eye.

`Release` is not optional. BenchmarkDotNet refuses to run a debug build, and the numbers from one
would be meaningless.

The project multi-targets `net8.0` and `net10.0`. Pass `-f` to choose, or `--runtimes net8.0 net10.0`
to have one run measure both and put them in the same table.

## Verifying the corpus

```sh
dotnet run --project src/Apache.Calcite.Cosmos.Benchmarks -c Release -f net10.0 -- verify
```

A benchmark corpus rots quietly. A statement that stops parsing still produces a number; a statement
that stops being pushed down produces a *better* number, which is worse. `verify` plans every
statement in the corpus once and prints what happened to each — whether it planned, how long an
unwarmed pass took, whether the chosen plan reaches the Cosmos convention at all, and whether it
plans wholly inside it.

It fails, with a non-zero exit, on any statement that does not plan and on any disagreement with the
wholly-pushed list in `PlannerQueries` — in both directions. A statement that starts pushing is good
news and a stale list, and the list is what two of the benchmark classes are run over. CI runs this
on every push.

`--sql` also prints the Cosmos statement each wholly pushed plan renders to, which is the quickest
way to see what the adapter is actually sending. `--scale` adds a table of the generated statements
at sizes from one to sixty-four, which is how the scaling benchmarks' parameters were chosen.

## What is measured

| Class | Measures |
| --- | --- |
| `PipelineBenchmarks` | Each stage as a prefix of the next — parse, then also validate, then also convert, then also rewrite, then also search. A stage's own cost is the difference between two rows. |
| `FilterPlanningBenchmarks` and its siblings | Planning one statement the way a host asks for it: the whole pipeline, ending in a plan in the asynchronous convention. Split by area so that a change to the aggregate rules is not a reason to re-time the joins. |
| `PushdownBenchmarks` | The same statements planned for the Cosmos convention alone, with the in-process alternative removed. The difference from the row above is what having an alternative costs. |
| `ImplementBenchmarks` | Rendering an already-chosen plan to Cosmos SQL: binding paths, translating every expression, collecting parameters, extracting the partition key. |
| `PredicateScalingBenchmarks`, `ShapeScalingBenchmarks`, `JoinScalingBenchmarks` | How planning time grows with the size of a predicate, the shape of a statement, and the number of containers joined. |
| `RuleSetBenchmarks` | What every plan pays before the search begins: building a container's rules, constructing a planner, registering them. This is the cost that grows with the schema rather than with the statement. |
| `MetadataBenchmarks` | The questions the rules ask of a container — is this path indexed, what is the row type, does a composite index cover this ordering — timed on their own, because each of them happens many times per search. |

Allocation is reported everywhere. For a Volcano search it is the more stable of the two signals: it
does not move with the machine, and a rule that starts producing an alternative nothing selects
shows up in the bytes before it shows up in the mean.

## The corpus

`PlannerQueries` holds around eighty statements, each with a note saying what about the planner it is
there to reach. They are not a survey of SQL — the test suite does that — but a set of distinct
amounts of work: the deepest predicate that pushes whole, the shallowest one that cannot push at all,
a grouping the service has no syntax for, a ranked hybrid search whose page becomes the probe side of
a lookup join, and a handful of statements the size an application actually writes.

Several of them deliberately do **not** push. A rule declining is a measurement, and a corpus of only
pushable statements would never time the path a real schema spends most of its time on.

`BenchmarkSchema` holds the six containers they plan against, chosen for the branches they put the
rules down rather than for realism: one partition key, a three-level hierarchical one, a key that is
`id`, a nested key that promotes to no column at all, an indexing policy that excludes nearly
everything, and declared full text and vector paths. All six carry statistics, because the choice
between pushing a filter down and applying it here is a cost comparison, and a cost model with no row
counts makes it by tie-break rather than by arithmetic.

`PlannerQueryGenerator` builds statements of a chosen size for the scaling benchmarks. Each generator
varies one dimension and holds the rest fixed.

## How the planner is wired

`PlannerHarness` builds a parser, a validator, a catalogue reader and a `VolcanoPlanner` directly,
the same way the adapter's planner tests do and for the same reason: Calcite's usual entry points
open an internal JDBC connection, which fails under IKVM.

Three things about that wiring are worth knowing when reading a number:

- **Every container's rules are registered for every statement**, whether or not it names them. That
  is what a host does — it registers what its schema holds, not what the query turned out to touch —
  and it is why `RuleSetBenchmarks` exists.
- **The convention asked for is `ClrAsyncEnumerableConvention`, not the Cosmos one.** Asked for
  Cosmos, the planner has one way to answer and takes it. Asked for the convention a host consumes,
  it has to reach both the pushed form and the in-process form and cost them against each other,
  which is most of the work and all of the benefit.
- **Sub-query removal happens in a Hep pass before the search**, as it does in Calcite's standard
  program. A sub-query is not an alternative to anything: the converter leaves one as a `RexSubQuery`
  inside a predicate, and no convention has a rule that matches a node containing one.

Join reordering is *not* registered by default, because a host on Calcite's standard rule set does
not have it — associativity is behind a system property there. `JoinScalingBenchmarks` measures both
ways, because for this adapter which side of a join is the probe decides whether it fetches keys or
reads a container.

## Sample output

One unwarmed pass per cell, from `verify --scale` on a four-core Xeon. Not a measurement — the
benchmarks are what measure — but it is what the scaling parameters were chosen from, and it is the
shape the curves have.

```
scaling, one unwarmed pass each, milliseconds
dimension                 1         2         4         8        16        32        64
conjuncts                14        12        13        14        17        25        47
disjuncts                22        17        19        23        30        35        56
point reads              14        11        11        12        13        14        12
projections              11        11        12        13        17        27        55
nesting                  13        15        21        28        49        94       222
unnests                  16        19        29        56       130       429      1269
joins                     -         5         4         7        12        28    failed
union branches            -         6         7        13        19        37       103
```

Two things in that table are the reason it is printed. An `IN` list is free — the converter expands
it into an OR ladder and the planner does not care how long the ladder is — and array traversal is
not: each `UNNEST` is a correlate the planner converts and costs, and eight of them cost more than
sixty-four conjuncts. The sixty-four-way join does not plan at all.
