# 20. Monitoring and diagnostics

The adapter reports what it does through the standard .NET diagnostics APIs — a `Meter` and an
`ActivitySource` — so it is collected the way anything else in a .NET application is, and costs nothing
when nobody is listening. For what a query *will* do, Calcite's `EXPLAIN PLAN FOR` shows the plan.

## 20.1 Collecting with OpenTelemetry

Both the meter and the activity source are named `Apache.Calcite.Cosmos.Adapter`
(`CosmosInstrumentation.Name`):

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Apache.Calcite.Cosmos.Adapter"))
    .WithTracing(t => t.AddSource("Apache.Calcite.Cosmos.Adapter"));
```

## 20.2 Metrics

| instrument | kind | unit | measures |
| --- | --- | --- | --- |
| `cosmos.request_charge` | histogram | `{request_unit}` | request units charged, one measurement per response |
| `cosmos.responses` | counter | `{response}` | responses received |

Both carry two tags:

| tag | values |
| --- | --- |
| `cosmos.container` | the container's name |
| `cosmos.request_kind` | `query`, `point_read` or `write` |

The charge is recorded **per response** rather than per statement, because a query spanning several
pages is charged per page, and the spread is itself worth seeing — a query answered in one page and
one answered in forty cost very differently. The `request_kind` tag is what makes a point read visible
as a point read; without it, it could not be told from the query it replaced.

## 20.3 Traces

Each statement is an activity named **`cosmos.query`**, started when the statement is sent and stopped
when the reader lets go of it — however many pages that took. Its tags:

| tag | value |
| --- | --- |
| `db.query.text` | the Cosmos SQL that was sent |
| `cosmos.container` | the container |
| `cosmos.request_charge` | the total charge across every page |
| `cosmos.pages` | how many pages were read |
| `cosmos.index_metrics` | which indexes the statement used, when `indexMetrics` is on |

A span with no `cosmos.request_charge` is a statement whose reader was abandoned before the end — which
is its own signal. Point reads and writes are recorded on the metrics, not as `cosmos.query` spans.

## 20.4 Index metrics

```json
"indexMetrics": true
```

Asks the service, for every query, which indexes it used and which it could have used. The answer
appears on the span as `cosmos.index_metrics` (JSON). It is **off by default**: the service computes it
per query, so switch it on while working out why a query is expensive, not permanently. It is the
quickest way to find a missing composite index or an excluded path.

## 20.5 Watching statements in code

To print every statement and its charge — in a test, a sample or a debugging session — listen
directly:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Apache.Calcite.Cosmos.Adapter.Client;

using var activities = new ActivityListener
{
    ShouldListenTo = s => s.Name == CosmosInstrumentation.Name,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = a => Console.WriteLine(
        $"{a.GetTagItem("db.query.text")}  [{a.GetTagItem("cosmos.request_charge")} RU, {a.GetTagItem("cosmos.pages")} pages]"),
};
ActivitySource.AddActivityListener(activities);

double total = 0;
using var meters = new MeterListener
{
    InstrumentPublished = (instrument, listener) =>
    {
        if (instrument.Meter.Name == CosmosInstrumentation.Name && instrument.Name == "cosmos.request_charge")
            listener.EnableMeasurementEvents(instrument);
    },
};
meters.SetMeasurementEventCallback<double>((_, charge, _, _) => total += charge);
meters.Start();
```

The repository's sample program does exactly this to show what each query sent.

## 20.6 Reading a plan

```csharp
command.CommandText = "EXPLAIN PLAN FOR " + sql;
```

returns the physical plan as text, one node per line, indented by depth. Read it from the top:

| node | meaning |
| --- | --- |
| `CosmosToClrCursorConverter` | the boundary — everything **below** it is one Cosmos statement |
| `CosmosTableScan` | the container |
| `CosmosFilter`, `CosmosProject`, `CosmosSort`, `CosmosAggregate`, `CosmosUnnest`, `CosmosRank` | clauses of that statement |
| `CosmosLookupJoin` | a join that fetches the container by the other side's keys (Chapter 12) |
| `CosmosTableModify` | an `INSERT`, `UPDATE` or `DELETE` (Chapter 13) |
| `ClrCursor…` nodes — a calc, a sort, a join, an aggregate | work done **in process** |

For example, a sort that pushed:

```
ClrCursorCalc(…)
  CosmosToClrCursorConverter
    CosmosSort(sort0=[$1], dir0=[ASC], fetch=[10])
      CosmosProject(…)
        CosmosTableScan(table=[[COSMOS, products]])
```

and one that did not:

```
ClrCursorSort(sort0=[$1], dir0=[ASC], fetch=[10])     ← every matching document sorted here
  CosmosToClrCursorConverter
    CosmosProject(…)
      CosmosTableScan(table=[[COSMOS, products]])
```

The questions to ask of a plan: is the `Filter` below the converter? Is the `Sort` (with its `fetch`)?
Is a join a `CosmosLookupJoin`, or an in-process join over two converters — two full reads?

## 20.7 Errors

| exception | meaning |
| --- | --- |
| `CosmosExecutionException` | a request failed — a write was refused (`409 Conflict` on insert, a rejected replace), or a compiled plan could not reach the schema or client that executes it |
| `CosmosMaterializationException` | a document did not hold what the query said it would — a string where `RETURNING INTEGER` was declared, text that is not a UUID under a UUID cast |
| `CosmosException` (SDK) | anything the service itself refused — throttling beyond the retry policy, authorisation, a statement the service rejects |
| errors from `OpenAsync` | the model or an operand is wrong; the useful message is usually a cause or two down |

`CosmosTranslationException` is internal — the adapter's way of declining a pushdown — and never
reaches a caller.

Because the first page of a statement is fetched when the reader is opened, a statement the service
refuses fails at `ExecuteReaderAsync`, not at the first `ReadAsync`. A document that cannot be read
fails at the `ReadAsync` that reaches it.

---

[← Previous: Performance and cost](19-performance.md) · [Contents](README.md) · [Next: Embedding the adapter in your own planner →](21-custom-planner.md)
