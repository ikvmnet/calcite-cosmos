# The rules of JSON and of JSON Schema, on Eggnog

The theory the adapter's declared facts are reasoned in. `JsonFactTheory` compiles a container's declaration into
it and `JsonFactSet` reads the answers back in the adapter's vocabulary; nothing outside `Facts/` uses this
namespace, and it is internal to the adapter. `DESIGN.md`, under *The theory of JSON, in layers* and the sections
after it, is the record of why it is built this way. This file is the catalogue: what the tables mean, what the
rules are, and how they are checked.

It depends on `Eggnog` `0.0.1-pre.46`, an egglog-style e-graph with scopes, lattices and case splits. Its tests are
the adapter's, in `Apache.Calcite.Cosmos.Adapter.Tests/Facts/Saturation`:

```bash
dotnet exec --roll-forward Major Apache.Calcite.Cosmos.Adapter.Tests.dll -namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
```

## Contexts

| context | holds | frozen |
| --- | --- | --- |
| root | the rules of JSON, global to the graph; that every document is a present object | yes |
| JSON Schema (`JsonContexts.JsonSchema`) | the rules of JSON Schema, as local rules, and the interpreter of conditional rules | yes |
| a schema's (`JsonContexts.OpenSchema`) | its facts, its conditional rules as rows, its discriminators, split on each | once compiled |
| a query's (`JsonContexts.Open`) | what the query established, split on the discriminators relevant to it | no |

A rule is JSON's when everything it reads is a property of the document itself: presence, type, value, lengths,
bounds, membership, elements, equality. It is JSON Schema's when it reads a fact only a schema keyword states, or
when it is the conditional-rule machinery. Below, a rule of JSON Schema is marked †.

## Vocabulary

A path is a term: `Root`, `Prop(p, name)`, `Index(p, i)`, and `Each(p)`, which stands for every element of the
array at `p`. Written out, these are `$`, `$.a.b`, `$.tags[10]` and `$.items[*].id`. `Ghost(name)` is a
discriminator that is no place in any document (`JsonPathSpec.HiddenNamed`), for a union whose branches nothing
in the document tells apart.

| fact | means |
| --- | --- |
| `present(p)` | the path is there |
| `absent(p)` | the path is not there |
| `types(p) = S` | **if** there, its type is in `S`, over `string, integer, fraction, boolean, null, object, array` |
| `dom(p) = S` | **if** there, its value is one of these scalars |
| `ne(p, v)` | **if** there, its value is not `v` |
| `lo(p)`, `hi(p)` | **if** a number, it lies within these bounds, each open or closed |
| `minLength(p)`, `maxLength(p)` | **if** a string, its length lies within these |
| `minItems(p)`, `maxItems(p)` | **if** an array, its length lies within these |
| `closed(p) = S` | **if** an object, its properties are among `S` (`additionalProperties: false`) |
| `required(p, n)` | **if** an object, it has `n` |
| `contains(p, v)` | the path is an array with an element equal to `v` |
| `multipleOf(p) = m` | **if** a number, it is a multiple of `m`; two of them merge to their least common multiple |
| `unique(p)` | **if** an array, no two elements are equal (`uniqueItems`) |
| `minProperties(p)`, `maxProperties(p)` | **if** an object, its property count lies within these |
| `containsAtLeast(p, v)`, `containsAtMost(p, v)` | **if** an array, this many of its elements equal `v` (`contains`, `minContains`, `maxContains`) |
| `nameMinLength(p)`, `nameMaxLength(p)` | **if** an object, its property names are within these lengths (`propertyNames`) |
| `form(p, f)` | **if** a string, it is spelled in the stored form `f`: a token the theory reads nothing of |
| `extension(p, k)` | the path holds what a host's claim `k` says; what that entails in JSON's terms is stated beside it |
| `p ≡ q` | the two paths hold one value, or are both absent: a union of the two path terms |

Nearly every fact is conditional on the path being there, which is what JSON Schema's keywords mean. That is what
lets two claims no single value satisfies prove something without contradicting each other: a string and an
object at one path prove the path is **absent**. Only presence beside that is a contradiction.

A scalar is a string tagged with its type: `null|`, `boolean|true`, `string|x`, `integer|2`, `fraction|1.5`. Two
spellings of one number are one value, `1.0` being `integer|1`. A number no `decimal` can hold keeps its type and
an approximate spelling (`integer|~1E+30`) that no exact number shares; no bound, multiple or range reads its
value, so each keeps it rather than guess where it lies.

A pattern carries conditional facts and absence, and never presence. A fact about every element is a fact about
each element. `instance(c, e)` relates each concrete path to the patterns that cover it, and every fact a pattern
carries is inherited by its instances. That holds through properties of elements, elements of elements, and
patterns of patterns.

No rule makes a path. Rules match paths that a schema or a query mentions, and every lattice only narrows, so
saturation terminates.

## The rules

**Values and types**
1. A value is of its own type.
2. A boolean is true or false.
3. Null is one value.
4. A disequality narrows a domain.
5. A value that is not null is of another type.
6. A path of no possible type is absent.
7. A path is not both absent and present.

**Numbers**
- A lower bound narrows a domain.
- An upper bound narrows a domain.
- No number lies between crossed bounds.
- No integer lies between bounds closer than one.

**Strings**
- A minimum length narrows a domain.
- A maximum length narrows a domain.
- No string has crossed lengths.
- † A stored form is a string or null.

**Objects**
- A present property has a present object for its parent.
- An absent path has no properties.
- Only an object has properties.
- † A closed object has no other properties.
- † A present object has its required properties.

**Arrays**
- A present element has a present array long enough to hold it.
- An absent path has no elements.
- Only an array has elements.
- An element beyond the most an array holds is absent.
- An element within the least a present array holds is present.
- No array has crossed lengths.
- An array holding a value is a present, non-empty array.
- An array holds no value its elements cannot be: not outside their domain, not of a type they cannot have, and
  nothing at all where no element can be there.

**`multipleOf`, integer ranges, `uniqueItems`, property counts**
- † A multiple narrows a domain.
- † A multiple of a whole number is whole.
- † No multiple lies between bounds, so there is no number.
- An integer between near bounds is one of the integers between them. This turns `1 ≤ n ≤ 3` into the domain
  `{1, 2, 3}`, which disequalities can then empty.
- † A unique array is no longer than the values its elements can be: two for booleans, `|enum|` for an enum.
- † Two present elements of a unique array are not one value.
- † Properties are counted:
  - an object has no more properties, present or required, than `maxProperties` allows;
  - a closed object has no more properties than it names;
  - no object has crossed property counts.

**Two paths holding one value.** This is a union of two path terms, which is what an e-graph is for:
- Congruence makes their properties and elements one as well: `$.a ≡ $.b` gives `$.a.x ≡ $.b.x` and
  `$.a[2] ≡ $.b[2]`.
- Every fact about either, presence included, is a fact about both.
- A query's `c.a = c.b` proves it, and so does a `CHECK (a = b)` over paths that are both there.
- No value contains itself, so a path equal to one of its own descendants is absent.

**† `contains`, `minContains`, `maxContains`**
- An array holding a value is a present, non-empty array; holding it `n` times, it is at least `n` long.
- An array cannot hold a value its elements cannot be: not outside their domain, not of a type they cannot
  have, and not one they are all unequal to.
- A unique array holds a value at most once.
- No array holds a value more often than at most.
- An array holding a value no times has no element equal to it.

**† `propertyNames`:** a property whose name is too short or too long is absent.

**What cannot be there says what its parent is not**
- † A required property that cannot be there means no object. So does one a closed object excludes.
- An element an array must hold and cannot means no array.
- An array whose elements cannot be there is empty, and an empty array has no element.

**Patterns**
- An element is an instance of every element.
- So is a property of an instance, an element of an instance, and every element of an instance.
- An instance inherits:
  - `types`, `dom`, `ne` and both numeric bounds;
  - both length bounds and both item bounds;
  - absence;
  - † `closed`, `required` and a stored form;
  - † `multipleOf`, `unique`, the property counts, the name lengths and the counts of a value.

  Not an extension, which may entail a presence a pattern never carries.

## † A schema's conditional statements, and case splits

A `oneOf` branch under its discriminator, or an `if`/`then`, holds only in some documents, so it is not a rule of
JSON. It is a **conditional rule** of one schema: `JsonGuardedRule(When, Then)`. Conditional rules go into the
schema's context as rows, and a fixed interpreter in the JSON Schema context reads them:
- `step(r, k)` says that the first `k` facts of rule `r`'s body are known;
- an atom keyed `(r, k)` extends the chain;
- once the body is complete, the head rows become facts.

A body fact is known the way any fact is: a domain within the set, types within the set, the path present or
absent, a disequality stated or proven by a domain. So the body `kind = 'A'` holds where `kind`, if there, is
`'A'`, which is how JSON Schema reads `if: {properties: {kind: {const: "A"}}}`; a guard that needs the path there
says so with `present`.

A query therefore costs the rules it fires, not the rules there are. `spikes/EggnogDerive` measured this layout at
109,000 rules.

**Splits.** Where a discriminator is present and its domain still holds several values, the context opens one case
per value and lifts to itself what every case proves. The cases cover the context, because the path is there and
its value is one of the domain's.
- A schema's context splits on every discriminator when it is compiled, so what every branch implies holds of the
  schema outright.
- A query's context splits only on **relevant** discriminators, and a case may split again on a discriminator its
  own branch makes present. A path is relevant if any of these holds:
  - the query mentions it;
  - it is a parent, element or pattern of a relevant path;
  - a conditional rule reads it and states something about a relevant path.

  That is the integration plan's "governs" demand gate, written as rules. A query context beneath a schema that
  names no discriminator is opened without the split step, which costs a run even where it opens nothing.

**Hidden discriminators.** An undiscriminated `anyOf` can be given a discriminator over its branch numbers, at a
`Ghost` path. Every document matches some branch, so a branch number is always a consistent choice. The theory
reasons with one; the adapter's schema reader does not state one yet, and reads such a union as its meet.

| scenario | what the split proves |
| --- | --- |
| gap 1: `kind IN ('order','invoice')`, both of which declare `ref` | `ref` is present, and is an integer |
| its control: `kind IN ('order','note')`, a note having no `ref` | nothing about `ref`, rightly |
| the same, with no discriminator declared | nothing |
| gap 2: `kind <> 'note' AND kind <> 'invoice'` | the order branch, with no split at all |
| at the schema itself | no branch leaves `ref` a string, so `ref` is never a string |
| gap 3: an `anyOf` of two `status` enums, and `status = 'purged'` | no case is left: no document |
| kind `A` with sub-kinds `x` and `y` that both require `z`, and kind `B`, which requires `z` | `kind IN ('A','B')` proves `z`, through a split inside a split |
| a kind whose branch contradicts what the schema says outright | the schema's own split finds the case contradictory, and the kind leaves the domain: `kind = 'bad'` keeps no document |

That last row was found by accident. A synthetic container meant for timing declared one kind's property both a
present string and an integer. The schema's compile-time split dropped that kind from the domain, so a query
naming it alongside another needed no split at all. The behaviour is sound, and it is a test of its own.

## What a split costs

`MeasureTests` builds a container of many kinds, each declaring typed properties. Two kinds require `p0` and a
third does not. It writes its figures to `measure.txt` beside the test assembly, and asserts only the answers and
the number of cases opened. Measured in the cloud on 4 cores, .NET 10.0.13, in Release:

| conditional facts | `kind = 'k0'` | `kind IN` two (2 cases) | `kind IN` five (5 cases) | contradicted | schema compiled |
| --- | --- | --- | --- | --- | --- |
| 477 | 7.3 ms (warm-up) | 47 ms (warm-up) | 11.0 ms | 0.82 ms | 491 ms |
| 5,069 | 2.1 ms | 5.2 ms | 6.6 ms | 0.44 ms | 138 ms |
| 47,502 | 4.6 ms | 4.6 ms | 11.1 ms | 0.46 ms | 776 ms |

The first row runs first, while tiered compilation is still settling. A Debug build on a workstation runs the
same at between one and three times these.

Three observations:
- **The run is not the bulk of the cost.** A 2-case query at 47k facts takes 5.2 ms in the run, of which search
  is 1.3 ms and merge 1.1 ms; the rest is opening, running and lifting cases.
- **The cost of a query follows its cases, not the size of the schema.** The 5k and 47k containers cost about
  the same per case. The rules (110 of them) are evaluated on every iteration, so this is slower than
  `spikes/EggnogDerive`'s rows-only interpreter, which had 15 rules.
- **Concurrency holds.** 512 split queries on 4 threads under one frozen schema context answered exactly as one
  thread does, in 540 ms.

**Relevance is what keeps it there.** A first version also made every property of a relevant path relevant.
Every path is a property of the root, so that made everything relevant, and one rule matched 30,000 times per
query at 40k facts. Without it, run time fell from 5.1 ms to 1.6 ms. The gate decides only where to split, so
narrowing it can lose a proof, never invent one.

The adapter asks a compiled declaration about one established set once per statement: `JsonFactTheory` remembers
the last 256 sets' contexts.

## What is checked

- **Soundness.** The theory never derives a falsehood from truths. 12,000 seeded trials each do the following:
  - generate a document;
  - state up to six random facts that are true of it, over twenty concrete paths and seven patterns. Most are about
    one path and its relatives (its elements and its parent), and they are weighted towards facts that bear on
    what the document holds there;
  - saturate;
  - check that the context is not contradictory, that every fact derived about every path is true of that
    document, and that twelve random predicates decided *always* or *never* are decided rightly for it.

  An unsound rule is a plan that drops rows, so this oracle stands behind the rules, rather than agreement with any
  other engine.
- **Conditional rules and splits checked too.** Half the trials carry the following:
  - **A discriminator** over a path holding a scalar, whose domain is its value plus others, so that splits open.
  - **Up to three conditional rules true of the document.** A body may name one or two of the discriminator's
    values or refuse one of them. Where a body is false of the document, the head is drawn from another document,
    so a rule that fires when it should not states a falsehood and is caught.
- **Equalities checked too.** A third of the generated documents hold one value at two places. Every pair of
  concrete paths the theory proves equal must hold one value in the document.
- **Forms, extensions and numbers held approximately.** A document may hold `1e30`, `-1e30` or `1.5e-30`. A fact
  may name a stored form — any string, or a string of at most one character — or the extension "an object", and a
  conditional rule may state either. A proposed fact false of the document is drawn again.
- **The oracle is shown to catch unsound rules.** Each planted bug fails it. Among them:
  - an off-by-one on the maximum array length;
  - a pattern that passes presence on to its elements;
  - `required` firing without checking for an object;
  - any multiple read as whole;
  - a unique array allowed one value fewer than it can hold;
  - a closed object counted wrong against `minProperties`;
  - a value held `n` times requiring `n + 1` elements;
  - a name exactly as long as the limit read as too long;
  - at most one occurrence read as none;
  - two present properties of one object made one value;
  - a conditional rule firing one atom early;
  - a body's domain atom checked the wrong way round;
  - a body's disequality read from a domain that holds the value;
  - a lift that intersects types across cases instead of joining them. This shows the trials really split.
  - a stored form read as a string, forgetting the null;
  - a lower bound dropping a number it cannot read rather than keeping it;
  - a conditional rule's stored form stated whatever its body says.

  Five of these slipped through earlier generators, which in turn:
  - rarely put facts on an array and its elements together in one trial;
  - rarely counted a value an array actually holds;
  - never let a body name two values of a discriminator;
  - never let a body refuse one of its values.

  Each time the generator was fixed, not the expectation.
- **62 scenarios**, eleven of them conditional rules and splits, and among the rest:
  - a string has no properties;
  - a number is not another number;
  - an absent value is not null;
  - an undefined object has no properties, nor do theirs;
  - the eleventh item of a ten-item array does not exist, so neither does its `.name`, and `ARRAY_LENGTH > 10` is
    never true;
  - a fact about every item reaches each item, each item's property, and items of items;
  - an array holds no value its items cannot be;
  - `items: false`;
  - the integration plan's gaps 4 and 5, which the adapter's plans now show too;
  - a query context reading through a frozen schema context.

The adapter's own tests ask the same questions through `JsonFactTheory`: `JsonFactTheoryTests`, `JsonSchemaFactsTests`,
and `CosmosDeclaredFactPlanningTests`, whose gap tests assert the plans gaps 1–5, F2 and F3 should produce.

## Predicates, for the oracle

`PredicateRules` is defined beside `JsonRules` in the same graph, for the predicate oracle (`Facts/CosmosOracle.cs`),
which every rule that reads a filter's facts asks through `Metadata/CosmosFilterStatement.cs`. It is defined in a
generation's graph the first time the oracle asks, in the generations the declaration compiles into, and its rulesets
run only in the oracle's schedules, so a query context the planner opens for itself pays nothing for them.
`DESIGN.md`, under *Predicates as facts* and the sections after it, is the record.

**Vocabulary.** Two sorts beside `JPath`, and every constructor unextractable: nothing here is a term to execute.

| term | means |
| --- | --- |
| `text(p)` | `JSON_VALUE(DOC, p)` read as text: a scalar's rendering, SQL null for anything else |
| `raw(p)` | the value the service holds: a promoted `VARIANT` column, the accessor re-typed `ANY`, `id` |
| `typed(p, T)` | `JSON_VALUE(… RETURNING T)`: the stored scalar, asserted to be of `T`; for `DECIMAL`, its rendering parsed |
| `cast(T, v)` | a numeric cast, or the cast to text over a raw value |
| `literal(s)`, `parameter(n, T)` | a JSON scalar in this theory's spelling; a parameter, with no value |
| `compare(op, v, w)`, `in(v, S)`, `is null(v)`, `is defined(v)` | the atoms; `is defined` asks of the document, whatever the reading |
| `and`, `or`, `not`, `opaque(n)` | the connectives, right-nested in digest order; what the loader does not read |

| table | means |
| --- | --- |
| `holds(e, t)` | the predicate takes truth value `t` on every row the context admits; never lifted |
| `arms(e, t)`, `arm(e, t, i, a)` | the numbered arms of a junction that decide it at `t`; stated by the host |
| `point(e, s)` | a literal an atom compares with; stated by the host |
| `reads(v, p, k)`, `atom(e, p, a)` | analyses of terms alone: the path a value reads and how, and the atom a test is |
| `possible(p)` | what a path an atom reads may hold, gathered into one value (`PathValues`) |
| `always true(e)`, `always false(e)`, `never true(e)` | the verdicts |

**What an atom means is `PredicateAtom`'s, in one place**, and no rule names a reading or an operator. It answers two
questions, each an over-approximation: what a row on which the atom takes a truth value can hold at its path
(`Kept`), and which truth values the documents a path's knowledge admits can give it (`Over`). A row Calcite raises
on is no truth value: it is no row a predicate keeps, and it supports no verdict.

**The rules**, 45 of them and a split.
- **Analysis** (9): a reading of a path reads it; a cast reads what it converts; a comparison with a literal or a
  parameter, a membership, a null test and a definedness test are each an atom of what they read.
- **What a predicate taking a truth value establishes** (12):
  - a conjunction that holds holds on both sides, and a disjunction that fails fails on both;
  - a negation takes the other truth value of its operand;
  - the arm a case chose takes the junction's truth value;
  - a predicate is not both true and false of a row;
  - an atom says what `Kept` says of its path: that it is there, its types, its values, its bounds, and which of
    its points it does not hold. The rendering preimage is here: `= '30'` keeps the string and the number;
  - a domain an atom's path holds keeps only values of a type the path may hold. The rules of JSON leave this out,
    because nothing the planner states needed it; it is what lets a declared string meet a preimage.
- **The split over arms**: one case per arm of a disjunction that holds, or of a conjunction that fails. Arms split
  before discriminators.
- **Decide** (24), run once the facts have settled:
  - what a path an atom reads may hold, narrowed by presence, types, a domain, a disequality and either bound (7);
  - an atom is always true, always false or never true where `Over` says so (3);
  - a predicate that holds with a truth value on every row has that verdict, whatever its atoms mean (2): an opaque
    predicate given is one decided, which is the question a weakening already beneath a filter asks;
  - twelve connectives in Kleene's logic. What is always false is never true; nothing reads never-true beneath a
    `NOT`, because UNKNOWN is not FALSE there.

**Schedules.** `Saturation` runs JSON, JSON Schema, analysis and establishment to saturation with the two splits,
then the verdicts. `Answer`, for a context that assumes nothing, runs JSON and analysis without a split, then the
verdicts.

**What is checked**, in the adapter's tests:
- `PredicateAtomTests`: the truth tables, and the property that `Kept` covers every stored value on which `Over`
  lets an atom take that truth value, for ten readings, nine tests, ten literals and fifteen kinds of value;
- `PredicateRulesTests`: each family through the engine, each verdict, and each under `NOT`;
- `CosmosOracleTests`: the strata, #117's self-certification, the licence table, redundancy, what a scope assumes, and
  an incomplete run;
- `CosmosPredicateLoaderTests`: the loader, `RETURNING DECIMAL` and the constants a conversion is compared with, and
  #117 through the translator;
- `CosmosOracleReplayTests`: the fact family's defects, each answered by the general rules through the planner's
  statement;
- `JsonFactOverlayTests`: a stratum read off the overlay, against the engine's context for it;
- `CosmosOracleMeasureTests`: what a question costs, at `MeasureTests`' sizes;
- `PredicateSoundnessTests`, through `PopulationD`: random declarations with a discriminator, random filters over
  their paths, and 64 documents drawn from each declaration and not from its filter. Every fact a context holds, row
  by row; every claim and verdict each licence's scope answers; every `holds`, verdict and `possible` row; every
  `atom` row against `IrEval` on every declared document; C5 over the `Pred`, `Val` and `JPath` classes; the facts
  each conjunct was handed to render from; and the conjuncts called redundant. 200 trials a run, 5,000 on request;
- `PlantedBugTests`: seven faults planted in graphs of their own — a too-wide preimage, establishing from a weakening,
  never-true read beneath `NOT`, the arm split without its guard, an axiom row contradicting the measurement, a
  stratum handing a conjunct its own facts, and a union of two predicates — each caught within a run's trials;
- `IrEvalCalciteTests`: the evaluator those are checked against, held to Calcite's runtime over the readings that
  were measured. The raw readings are drawn from what the table admits, and nothing holds those rows to Calcite yet.

## What is not here yet

**For the adapter to use more of it**
- **The schema reader has to state these facts.** `JsonSchemaFacts` reads `minimum`, `maximum` and their exclusive
  forms as bounds, and does not read `items`, `prefixItems`, `minItems`, `maxItems`, `minLength`, `maxLength`,
  `multipleOf` or `additionalProperties`. `JsonDocumentPath` has no element segment.
- **A range is read only through a numeric cast,** whose window was measured (`PredicateAtom`'s `NumberCast`). A
  comparison on a typed path or a raw value states the types and the presence alone.
- **A verdict of an atom over one path is the engine's**, opened for it, where a stratum read off the overlay could
  read it as `PredicateAtom.Over` reads one; the planner's redundancy questions are where that would pay
  (`TODO.md`, *What a new statement costs*).
- **The schema compiler has to emit conditional rules and discriminators directly.** That means a hidden
  discriminator for each undiscriminated `anyOf`, at which point `JsonSchemaFacts.Meet` and `JsonFact.Entails` go.
  Today's compiler emits `JsonFactRule`s and a meet.

**More rules of JSON**
- `contains` with a subschema rather than a scalar, which is an existential and the schema compiler's to expand.
- `propertyNames` with a pattern, which is the adapter's to recognise.
- `const` and `enum` over objects and arrays, which the schema compiler expands into facts about each path.
- An equality that holds only where both paths are there: SQL's `CHECK`, over an optional path. The strong form
  above is a union; this weak one is not.

**Reasoning still to add**
- **The converse of inheritance:** what every individual element is proven to be, every element is. This holds
  only once the length fixes which elements exist.
- **A disjunction inside a conjunct the strata leave above.** The oracle splits on an `OR`'s arms
  (`PredicateRules.ByArm`) wherever it is assumed; a conjunct no stratum admits is assumed only by the questions that
  read every conjunct, and what its arms prove reaches a render only through the conjuncts admitted beside it.
- **Rules that could be cheaper:**
  - several rules match only paths that are rarely touched;
  - Eggnog's roadmap lists demand-indexed rules;
  - a theory compiled into fewer, wider rules is the other route.
