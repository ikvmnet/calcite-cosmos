# Spike: the rules of JSON as an Eggnog root context

The rules that hold of every JSON document, written once into a root context and frozen. Beneath the root sit a
context for a schema's facts and, beneath that, one for a query's facts, which read through to the contexts above
them. The theory answers each predicate a query can contain with *always*, *never* or *unknown*.

```bash
dotnet run                                             # every test
dotnet run -- --filter "FullyQualifiedName~Scenario"   # only the scenarios
```

It is not part of the solution. It depends on `Eggnog` `0.0.1-pre.46` from nuget.org, and its tests run on
MSTest's own runner.

## Vocabulary

A path is a term: `Root`, `Prop(p, name)`, `Index(p, i)`, and `Each(p)`, which stands for every element of the
array at `p`. Written out, these are `$`, `$.a.b`, `$.tags[10]` and `$.items[*].id`.

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
| `p ≡ q` | the two paths hold one value, or are both absent: a union of the two path terms |

Nearly every fact is conditional on the path being there, which is what JSON Schema's keywords mean. That is what
lets two claims no single value satisfies prove something without contradicting each other: a string and an
object at one path prove the path is **absent**. Only presence beside that is a contradiction. The adapter's Horn
theory had no atom for absence, which is why `DESIGN.md` said two type claims never exclude each other.

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

**Objects**
- A present property has a present object for its parent.
- An absent path has no properties.
- Only an object has properties.
- A closed object has no other properties.
- A present object has its required properties.

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
- A multiple narrows a domain.
- A multiple of a whole number is whole.
- No multiple lies between bounds, so there is no number.
- An integer between near bounds is one of the integers between them. This turns `1 ≤ n ≤ 3` into the domain
  `{1, 2, 3}`, which disequalities can then empty.
- A unique array is no longer than the values its elements can be: two for booleans, `|enum|` for an enum.
- Two present elements of a unique array are not one value.
- Properties are counted:
  - an object has no more properties, present or required, than `maxProperties` allows;
  - a closed object has no more properties than it names;
  - no object has crossed property counts.

**Two paths holding one value.** This is a union of two path terms, which is what an e-graph is for:
- Congruence makes their properties and elements one as well: `$.a ≡ $.b` gives `$.a.x ≡ $.b.x` and
  `$.a[2] ≡ $.b[2]`.
- Every fact about either, presence included, is a fact about both.
- A query's `c.a = c.b` proves it, and so does a `CHECK (a = b)` over paths that are both there.
- No value contains itself, so a path equal to one of its own descendants is absent.

**`contains`, `minContains`, `maxContains`**
- An array holding a value is a present, non-empty array; holding it `n` times, it is at least `n` long.
- An array cannot hold a value its elements cannot be: not outside their domain, not of a type they cannot
  have, and not one they are all unequal to.
- A unique array holds a value at most once.
- No array holds a value more often than at most.
- An array holding a value no times has no element equal to it.

**`propertyNames`:** a property whose name is too short or too long is absent.

**What cannot be there says what its parent is not**
- A required property that cannot be there means no object. So does one a closed object excludes.
- An element an array must hold and cannot means no array.
- An array whose elements cannot be there is empty, and an empty array has no element.

**Patterns**
- An element is an instance of every element.
- So is a property of an instance, an element of an instance, and every element of an instance.
- An instance inherits:
  - `types`, `dom`, `ne` and both numeric bounds;
  - both length bounds and both item bounds;
  - `closed` and `required`;
  - `multipleOf`, `unique`, the property counts, the name lengths and the counts of a value;
  - absence.

**Cosmos DB:** the root context also states that every document is a present object.

## A schema's conditional statements, and case splits

A `oneOf` branch under its discriminator, or an `if`/`then`, holds only in some documents, so it is not a rule of
JSON. It is a **conditional rule** of one schema: `JsonGuardedRule(When, Then)`. Conditional rules go into the
schema's context as rows, and a fixed interpreter in the root reads them:
- `step(r, k)` says that the first `k` facts of rule `r`'s body are known;
- an atom keyed `(r, k)` extends the chain;
- once the body is complete, the head rows become facts.

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
  - it is a parent, property, element or pattern of a relevant path;
  - a conditional rule reads it and states something about a relevant path.

  That is the integration plan's "governs" demand gate, written as rules.

**Hidden discriminators.** An undiscriminated `anyOf` gets a discriminator over its branch numbers, at a path that
is no place in any document (`JsonPathSpec.HiddenNamed`). Every document matches some branch, so a branch number is
always a consistent choice.

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
naming it alongside another needed no split at all. The behaviour is sound, and it is now a test of its own.

## What a split costs

`MeasureTests` builds a container of many kinds, each declaring typed properties. Two kinds require `p0` and a
third does not. It runs in Release, writes its figures to `measure.txt` beside the test assembly, and asserts only
the answers and the number of cases opened. The machine has 4 cores and runs .NET 10.0.13.

| conditional facts | `kind = 'k0'` | `kind IN` two (2 cases) | `kind IN` five (5 cases) | contradicted | schema compiled |
| --- | --- | --- | --- | --- | --- |
| 477 | 7.3 ms (warm-up) | 47 ms (warm-up) | 11.0 ms | 0.82 ms | 491 ms |
| 5,069 | 2.1 ms | 5.2 ms | 6.6 ms | 0.44 ms | 138 ms |
| 47,502 | 4.6 ms | 4.6 ms | 11.1 ms | 0.46 ms | 776 ms |

The first row runs first, while tiered compilation is still settling.

Three observations:
- **The run is not the bulk of the cost.** A 2-case query at 47k facts takes 5.2 ms in the run, of which search
  is 1.3 ms and merge 1.1 ms; the rest is opening, running and lifting cases.
- **The cost of a query follows its cases, not the size of the schema.** The 5k and 47k containers cost about
  the same per case. The root's rules (110 of them) are evaluated on every iteration, so this is slower than
  `spikes/EggnogDerive`'s rows-only interpreter, which had 15 rules.
- **Concurrency holds.** 512 split queries on 4 threads under one frozen schema context answered exactly as one
  thread does, in 540 ms.

**Relevance is what keeps it there.** A first version also made every property of a relevant path relevant.
Every path is a property of the root, so that made everything relevant, and one rule matched 30,000 times per
query at 40k facts. Without it, run time fell from 5.1 ms to 1.6 ms. The gate decides only where to split, so
narrowing it can lose a proof, never invent one.

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
- **The oracle is shown to catch unsound rules.** Each of fourteen planted bugs fails it:
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

  Five of these slipped through earlier generators, which in turn:
  - rarely put facts on an array and its elements together in one trial;
  - rarely counted a value an array actually holds;
  - never let a body name two values of a discriminator;
  - never let a body refuse one of its values.

  Each time the generator was fixed, not the expectation.
- **59 scenarios**, ten of them conditional rules and splits, and among the rest:
  - a string has no properties;
  - a number is not another number;
  - an absent value is not null;
  - an undefined object has no properties, nor do theirs;
  - the eleventh item of a ten-item array does not exist, so neither does its `.name`, and `ARRAY_LENGTH > 10` is
    never true;
  - a fact about every item reaches each item, each item's property, and items of items;
  - an array holds no value its items cannot be;
  - `items: false`;
  - the integration plan's gaps 4 and 5;
  - a query context reading through a frozen schema context.

## What is not here yet

**For the adapter to use it**
- **The schema reader has to state these facts.** `JsonSchemaFacts` does not read `items`, `prefixItems`,
  `minItems`, `maxItems`, `minimum` and its relatives, `minLength`, `maxLength` or `additionalProperties`.
  `JsonDocumentPath` has no element segment.
- **The extractor has to state what a predicate proves.** A comparison proves its path present, which is the
  integration plan's F4.
- **The rewriter has to ask `Decide`,** where it now asks `Knows`.
- **The schema compiler has to emit conditional rules and discriminators.** That means `oneOf` under its
  discriminator, `if`/`then`/`else`, `dependentRequired`, and a hidden discriminator for each undiscriminated
  `anyOf`. Today's compiler emits `JsonFactRule`s and a meet.

**More rules of JSON**
- `contains` with a subschema rather than a scalar, which is an existential and the schema compiler's to expand.
- `propertyNames` with a pattern, which is the adapter's to recognise.
- `const` and `enum` over objects and arrays, which the schema compiler expands into facts about each path.
- An equality that holds only where both paths are there: SQL's `CHECK`, over an optional path. The strong form
  above is a union; this weak one is not.
- Rules that are a schema's rather than JSON's (`dependentRequired`, `if`/`then`, `oneOf`). These go in as rows
  beneath the root, not as root rules.

**Reasoning still to add**
- **The converse of inheritance:** what every individual element is proven to be, every element is. This holds
  only once the length fixes which elements exist.
- **A disjunction in the query.** A split on `OR` between predicates over different paths: the integration plan's
  per-kind spelling, `(type = 'park' AND …) OR (type = 'map' AND …)`.
- **A per-statement memo.** A plan asks a dozen questions of one derivation, and each figure above is a fresh
  query context.
- **Root rules that could be cheaper:**
  - several rules match only paths that are rarely touched;
  - Eggnog's roadmap lists demand-indexed rules;
  - a theory compiled into fewer, wider rules is the other route.
