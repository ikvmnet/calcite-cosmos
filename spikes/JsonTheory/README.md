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

**Patterns**
- An element is an instance of every element.
- So is a property of an instance, an element of an instance, and every element of an instance.
- An instance inherits:
  - `types`, `dom`, `ne` and both numeric bounds;
  - both length bounds and both item bounds;
  - `closed` and `required`;
  - absence.

**Cosmos DB:** the root context also states that every document is a present object.

## What is checked

- **Soundness.** The theory never derives a falsehood from truths. 4,000 seeded trials each do the following:
  - generate a document;
  - state up to six random facts that are true of it, over twenty concrete paths and seven patterns;
  - saturate;
  - check that the context is not contradictory, that every fact derived about every path is true of that
    document, and that twelve random predicates decided *always* or *never* are decided rightly for it.

  An unsound rule is a plan that drops rows, so this oracle stands behind the rules, rather than agreement with any
  other engine.
- **The oracle is shown to catch unsound rules.** Each of three planted bugs fails it:
  - an off-by-one on the maximum array length;
  - a pattern that passes presence on to its elements;
  - `required` firing without checking for an object.
- **25 scenarios**, among them:
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
- **A schema's guarded rules** (`oneOf`, `if`/`then`) go in as rows, read by fixed rules in the root, as
  `spikes/EggnogDerive` measured.

**More rules of JSON**
- `uniqueItems`: an array of unique items is no longer than the domain its items are drawn from.
- `multipleOf`.
- `minProperties` and `maxProperties`.
- `contains`, `minContains` and `maxContains`.
- `propertyNames`.
- `ARRAY_LENGTH(p) = n` pinning which elements are present.
- An equality between two paths, for `CHECK (a = b)`. This one wants values as e-classes rather than domains.

**Reasoning that needs a case split**
- **The converse of inheritance:** what every individual element is proven to be, every element is. This holds
  only once the length fixes which elements exist.
- **A disjunction in the schema.**
