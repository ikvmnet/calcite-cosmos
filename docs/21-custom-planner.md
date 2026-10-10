# 21. Embedding the adapter in your own planner

Most applications reach the adapter through a `CalciteConnection` and never see a planner. This
chapter is for hosts that assemble Calcite themselves — parsing, validating and planning with
`SqlParser`, `SqlValidator`, `SqlToRelConverter` and a `VolcanoPlanner` — for example a query service
with its own rule set, or a tool that only plans and explains.

Everything here is done for you by a connection. The adapter's own test harness
(`src/Apache.Calcite.Cosmos.Adapter.Tests/EndToEnd/Corpus/PlannerHarness.cs`) is a complete working
example.

## 21.1 Schemas

Build a schema with `CosmosSchemaFactory` and an operand map, or with `CosmosSchema`'s constructor
(Chapter 6), and add it to a root schema:

```csharp
var root = CalciteSchema.createRootSchema(false);
root.add("COSMOS", cosmosSchema);
```

A `CosmosSchema` built with no executor factory plans normally and cannot be executed — useful for
planning and explaining without a network.

## 21.2 Operator tables

A connection finds the adapter's functions on the schema. A validator you build resolves names against
the operator table you give it, chained with the catalog reader — which also reads schema functions, so
qualified or default-schema calls resolve either way. To make the names resolvable everywhere, and to
lift the 16-argument bound on variadic functions, chain the adapter's table:

```csharp
var operators = SqlOperatorTables.chain(
    SqlStdOperatorTable.instance(),
    CosmosOperators.Instance,                       // FULLTEXT*, IS_DEFINED, …
    GeographyOperatorTable.Instance());             // CLR_ST_GEOG_*, if you use geography
```

Chaining `CosmosOperators.Instance` beside a Cosmos schema is not a duplicate definition: overload
resolution takes the first candidate whose arity fits, so the chained operator answers.

For the shared full text vocabulary (`CLR_FT_*`), use **one** route: either let the Cosmos schema
declare it (it does by default) or chain `FullTextOperatorTable.Instance()` — registering both leaves
two candidates for one name and an `ARRAY` argument then fails validation.

## 21.3 Rules

Each container has its own convention, and its rules are registered by the convention itself the
first time the planner sees a scan of it. To register them up front — for every container, which is
what a host should do:

```csharp
foreach (var table in tables)                       // every CosmosTable in the schema
    foreach (var rule in CosmosRules.GetRules(table.Convention))
        planner.addRule(rule);

foreach (var rule in ClrCursorRules.Rules())        // the in-process operators
    planner.addRule(rule);

planner.addRule(CoreRules.PROJECT_TO_LOGICAL_PROJECT_AND_WINDOW);
```

The adapter's rule set includes the Calcite rewrites its pushdown depends on — filter and sort
transposes, projection merge, distinct-aggregate expansion, aggregate reduction — because a bare
Volcano planner has none of them and a pushdown must not depend on which rules a caller happened to
add. Register `ConventionTraitDef.INSTANCE` and `RelCollationTraitDef.INSTANCE` on the planner.

Ask for **`ClrCursorConvention`** as the root convention:

```csharp
var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
planner.setRoot(planner.changeTraits(logical, desired));
var best = planner.findBestExp();
```

The adapter's results leave the Cosmos convention into `ClrCursorConvention` and into nothing else —
there is no converter into Calcite's `EnumerableConvention`. A plan that wants rows elsewhere converts
higher up, through `Apache.Calcite.Extensions`.

## 21.4 Passes around the planner

**Before** the cost-based search, remove sub-queries with a `HepPlanner` pass, as Calcite's standard
program does:

```csharp
var program = new HepProgramBuilder()
    .addRuleInstance(CoreRules.FILTER_SUB_QUERY_TO_CORRELATE)
    .addRuleInstance(CoreRules.PROJECT_SUB_QUERY_TO_CORRELATE)
    .addRuleInstance(CoreRules.JOIN_SUB_QUERY_TO_CORRELATE)
    .build();
```

**After** it, run the calc rules as a pass — this is Calcite's `Programs.CALC_PROGRAM`:

```csharp
var program = new HepProgramBuilder();
foreach (var rule in ClrCursorRules.CalcRules())
    program.addRuleInstance(rule);

var hep = new HepPlanner(program.build());
hep.setRoot(best);
best = hep.findBestExp();
```

This must be a pass, not rules given to the planner. Without it, a projection that survives above a
join or a residual filter has nothing to implement it, and the plan fails saying it cannot be
implemented. It never arises without a join, because otherwise every projection is pushed into the
container.

## 21.5 Root and projection

Convert with `RelRoot.project()`, not `RelRoot.rel`, for a query. Ordering by an expression that is not
selected makes Calcite carry it as an extra column and record in the root that it is not output;
`project()` applies that, and it is the shape the `ORDER BY RANK` rule needs (Chapter 17). A write has no
such mapping; use `RelRoot.rel` for `INSERT`, `UPDATE` and `DELETE`.

## 21.6 Null collation

`DefaultNullCollation` is a connection property. With your own validator, set it on the validator
configuration:

```csharp
SqlValidator.Config.DEFAULT.withDefaultNullCollation(NullCollation.LOW)
```

Chapter 9 explains why.

## 21.7 Calcite's JDBC entry points under IKVM

`Frameworks.getPlanner` and `RelBuilder.create` open an internal Calcite JDBC connection, and under IKVM
that fails with `ClassNotFoundException: org.apache.calcite.jdbc.CalciteJdbc41Factory` — though the
class is present. IKVM gives each assembly its own class loader, and Avatica looks the factory up
through its own. Publish Calcite's assembly to the boot class path **before anything touches the
driver**:

```csharp
ikvm.runtime.Startup.addBootClassPathAssembly(typeof(org.apache.calcite.jdbc.CalciteFactory).Assembly);
```

A type initializer runs once and caches its failure, so this must run first — a `[ModuleInitializer]`
is the reliable place. The adapter itself never opens such a connection and does not need this; it is
for hosts that use those entry points.

## 21.8 Executing a plan

Implement the chosen plan with the `ClrCursor` implementor from `Apache.Calcite.Extensions`, against a
`DataContext` whose root schema contains the Cosmos schema under the same path the plan was built with.
A compiled plan holds the table's qualified name, not the client, and finds the executor through the
data context's root on each run — so one compiled plan can be executed many times, and against whichever
schema instance is current.

---

[← Previous: Monitoring and diagnostics](20-monitoring.md) · [Contents](README.md) · [Next: Troubleshooting →](22-troubleshooting.md)
