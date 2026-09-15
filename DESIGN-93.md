# A container schema compiled to a fact theory (#93)

Cosmos stores strings where Calcite has `UUID` and `TIMESTAMP`. A *proof* that a path holds only
canonical strings of a type is a representation theorem: it says the service's string operations **are**
the typed operations on that path. So what a proven fact unlocks is not a rewrite, it is an operator
family — and the schema is the thing that supplies the proofs, for any type, without a rule per type.

This is the model, the compilation, and the sites that ask.

---

## 1. A fact has to say *which relations it preserves* — and measurement is why

The obvious model — a path "is a UUID", "is a date-time" — is wrong, and one measurement kills it.

**Calcite orders UUIDs as two *signed* 64-bit halves** (`java.util.UUID.compareTo`). Measured through a
`CalciteConnection`:

| | |
| --- | --- |
| `UUID'80000000-…' > UUID'00000000-…'` | **False** |
| `ORDER BY` over four values | `8000…`, `ffff…`, `0000…`, `7fff…` |
| the same four as text | `0000…`, `7fff…`, `8000…`, `ffff…` |

So for half of all v4 UUIDs the lexical order of the canonical string is *not* Calcite's order. A proof
of canonical lowercase form licenses `=` and `IN` and routing; it licenses **nothing** about `<`,
`ORDER BY`, `MIN` or `MAX`.

**And the condition under which it does is expressible as a `pattern`,** which is the best argument in
the issue's favour that I have found. The order is lexical exactly when the top bit of each half is
constant across the values — the 1st and 17th hex digits confined to one side of 8. Measured:

| | |
| --- | --- |
| `UUID'…-0000-…' < UUID'…-a000-…'` (17th digit `0` vs `a`) | **False** — lexically `0000 < a000` |
| `UUID'…-8000-…' < UUID'…-a000-…'` (both RFC variant) | **True** |
| sort over v7-shaped values, 1st digit ≤ 7, variant `8`–`b` | exactly lexical |

RFC 4122/9562 pins the 17th digit to `8`–`b` for every conforming UUID, so that half always agrees. The
first digit is what varies: v4 spreads it over `0`–`f`, and **v7 confines it to `0`–`7` for every
realistic timestamp**. So

```
^[0-7][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$
```

is a schema that proves a sorted, range-queryable UUID column, and

```
^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$
```

is one that proves equality and routing and nothing more. **No bespoke "this path is a UUID" flag could
have told those two apart.** That is the case for the full vocabulary, made concrete.

So a representation fact carries two independent bits:

- **`PreservesEquality`** — one logical value ↔ one stored string. Licenses `=`, `<>`, `IN`, `DISTINCT`,
  `GROUP BY`, a join or lookup key, partition routing, the point read.
- **`PreservesOrder`** — lexical order of the stored string is the order Calcite compares in. Licenses
  `<`, `<=`, `>`, `>=`, `BETWEEN`, `ORDER BY`, `MIN`, `MAX`, and a `LIMIT` pushed beneath a sort.

| representation | equality | order |
| --- | --- | --- |
| canonical lowercase UUID, any version | ✔ | ✘ |
| the same, first digit `0`–`7` (v7) | ✔ | ✔ |
| ISO-8601 UTC at one fixed precision | ✔ | ✔ |
| ISO-8601 with mixed precision or mixed `Z`/offset | ✘ | ✘ |
| a number written as a string | ✘ | ✘ |
| an `enum` of strings | ✔ | ✘ |

### The date case needs a second measurement, and it changes what to look for

**Calcite's `CAST(<string> AS TIMESTAMP)` accepts only `yyyy-MM-dd HH:mm:ss`.** Every form a document
actually stores raises:

| stored form | `CAST(… AS TIMESTAMP)` |
| --- | --- |
| `2024-01-02 03:04:05` | `1/2/2024 3:04:05 AM` |
| `2024-01-02T03:04:05` | raises — *Invalid DATE value* |
| `2024-01-02T03:04:05Z` | raises |
| `2024-01-02T03:04:05.123Z` | raises |
| `2024-01-02T03:04:05+00:00` | raises |
| `2024-01-02T03:04:05Z` as `TIMESTAMP WITH LOCAL TIME ZONE` | raises — *not in format `yyyy-MM-dd HH:mm:ss zone`* |

So for a date path there is no bare cast to recognise. What a view writes is a *reshaping chain* —
`CAST(REPLACE(SUBSTRING(p, 1, 19), 'T', ' ') AS TIMESTAMP)` or some cousin of it — which is exactly the
"manual `REPLACE`/`SUBSTRING`/`CAST` reshaping" the issue mentions in passing. The rewrite therefore
operates on a *chain*, not a cast, and the general notion the model needs is:

> an expression over a path is **form-preserving for a relation** when, under the path's representation
> fact, comparing the expression's results agrees with comparing the raw stored strings.

A cast to `UUID` is the degenerate one-link chain. Dropping an order-preserving chain from a sort key is
what `TODO.md` §4 already describes; this generalises it and says what licenses it.

---

## 2. The model

### Atoms

```
Atom = (CosmosPath path, Property property)
```

One kind, deliberately. `$.type = 'Bob'` and `$.id : uuid-canonical-lower` are both atoms; the only
difference is that a query can establish the first. Keeping them one kind is what makes
*conditions-on-characteristics* fall out instead of needing a second mechanism.

```
Property =
  | Type        of JsonType                    // string, number, integer, boolean, object, array, null
  | Defined                                    // the path is present
  | Equals      of literal
  | NotEquals   of literal
  | OneOf       of set<literal>
  | Representation of { form; preservesEquality; preservesOrder }
```

### Subsumption is a function, not more clauses

An atom is *known* when a known atom entails it. Materialising the entailments as clauses would blow the
theory up (`Equals v` entails `OneOf S` for every `S ∋ v`), so it lives in the lookup instead:

```
Equals v         ⊨ OneOf S (v ∈ S), Type (typeof v), Defined, NotEquals w (w ≠ v)
OneOf S          ⊨ Type t (all of S is t), Defined, NotEquals w (w ∉ S)
Representation r ⊨ Type String, Defined
```

That keeps the clause set linear in the size of the schema.

### Clauses

A conjunctive body, a single-atom head:

```
{ ($.type, Equals "Bob") }                          →  ($.id, Representation uuid-canonical-lower)
{ }                                                 →  ($.id, Type String)
{ ($.type, Equals "Bob"), ($.v, Equals 2) }         →  ($.at, Representation iso8601-utc-seconds)
```

Nesting needs no special case — a nested `if` is a two-atom body, and chaining handles depth. The
issue's flat *guard ⟹ facts* table is the depth-1 slice of this.

### Why this shape: Horn, and linear

Conjunctive bodies with single-atom heads make the compiled schema a **definite propositional Horn
theory**, and Horn entailment is decidable in time linear in the theory — Dowling and Gallier, 1984, by
forward chaining with a per-clause counter of unsatisfied body atoms. Decrement as atoms become known,
fire the head at zero. That is the whole algorithm, and it is cheap enough to run per rule invocation
without caching.

**The rule that keeps it there: never a disjunctive head.** The moment a branch yields "A or B" the
theory leaves Horn and entailment becomes co-NP-hard. So an undiscriminated `anyOf`/`oneOf` contributes
the **meet** — only the facts true in every branch — which is a sound under-approximation and free.

### Negative atoms

`if`/`then`/`else` produces negative premises, and they are discharged more easily than I first thought.
A plain disequality in the query, or an equality to a *different* value, entails `NotEquals` by
subsumption above — no closed world needed. What `enum` and `oneOf` add is the **exhaustion** direction:
given `enum: [Bob, Carol, Dave]`, proving `NotEquals Carol` and `NotEquals Dave` yields `Equals Bob`.
That is unit propagation over an at-most-one/at-least-one pair, still linear, still not SAT. It is the
concrete answer to "what does a value-field `enum` buy".

---

## 3. Compiling JSON Schema to clauses

Walk the resolved schema at path *P* under an accumulated guard *G* (a set of atoms, initially empty).

| keyword | contributes |
| --- | --- |
| `properties: {n: S}` | recurse into `S` at `P + n` |
| `required: [n…]` | `G → (P+n, Defined)` |
| `type: t` | `G → (P, Type t)`; a union type contributes the meet, except `[t, "null"]` which contributes `Type t` without `Defined` |
| `const: v` | `G → (P, Equals v)`, **and** registers `(P, Equals v)` as an atom a query may establish |
| `enum: [v…]` | `G → (P, OneOf {v…})`, plus the exhaustion axiom |
| `pattern: r` | **recognition**: if `r` is one of the canonical spellings in the table, the corresponding `Representation`. Otherwise nothing |
| `format: f` | never a fact on its own — `format` is an annotation, not an assertion. It only selects which recognition table `pattern` is matched against |
| `allOf: [S…]` | recurse each under the same `G`; facts join |
| `if: C, then: T, else: E` | `atoms(C)` become guard atoms; recurse `T` under `G ∪ atoms(C)`, `E` under `G ∪ ¬atoms(C)` |
| `oneOf` / `anyOf: [S…]` | **discriminated** — every branch fixes one property with `const`: recurse branch *i* under `G ∪ {that const}`, emit exclusivity. **Otherwise**: recurse all branches under `G`, keep the meet |
| `discriminator` (OpenAPI) | read as the discriminated `oneOf` above; `mapping` names the branch |
| `$ref`, `$defs`, `$id`, `$anchor` | resolved by the library; a cycle stops the walk at the repeat |
| `items`, `prefixItems` | element facts at `P + [*]`; **not in the first cut**, see §6 |
| `not` | yields only negative atoms; **not in the first cut** |
| anything else | ignored, never an error — a richer schema stays legal and yields the facts we can read |

`atoms(C)` admits only the forms a query can establish: `properties: {n: {const: v}}` → `(P+n, Equals v)`;
`{enum: […]}` → `OneOf`; `required` → `Defined`; `type` → `Type`. Anything else in `C` makes that branch's
guard unestablishable, so the branch contributes nothing — which loses facts and is sound.

### `pattern` is recognised, not probed

The issue proposes deriving the case fact by generating a lowercase sample and an uppercased one and
running the declared `pattern` against each. That is evidence, not proof: the conclusion needed is
universal — *every* string the pattern accepts is canonical — and two probes are two data points. A
pattern with one group left case-insensitive accepts the lowercase sample, rejects the fully-uppercased
one, and still admits `123e4567-E89B-12d3-a456-426614174000`; a document storing that **conforms to the
schema** and is dropped by the equality the probe licensed. Unanchored patterns fail the same way, JSON
Schema's `pattern` being a partial match.

So: a table of exact canonical spellings, matched after whitespace normalisation. Sound by construction,
trivially testable, extended by adding a row — and it is how schema authors write these anyway, the
patterns coming from the same handful of sources. It also disposes of the "sample generation per format"
open question rather than answering it.

Probing is still worth keeping, as **refutation only**: generate non-canonical samples and withdraw the
fact if the pattern accepts one. Cheap, and it turns a mistyped canonical pattern into *no fact* rather
than a wrong one. It wants a `Regex` match timeout — an author-supplied pattern run at registration is a
catastrophic-backtracking target — and a note that .NET's `RegexOptions.ECMAScript` is not ECMA-262.

---

## 4. Asking the question

### The known set

Atoms the planner can establish from a query, in order of strength:

1. `RexUtil.expandSearch`, then `RelOptUtil.conjunctions` over the `Filter` condition — `path = literal`
   → `Equals`, `IN` → `OneOf`, `<>` → `NotEquals`, `IS NOT NULL` / `IS_DEFINED` → `Defined`.
2. Better, `RelMdPredicates.getPulledUpPredicates` on the input, which carries predicates through joins
   and reference projections. The null-placement rule already reads it.

### The two queries

- **Forward** — *given what I know, what does path P have?* Chain to the fixpoint. This is the one the
  rules ask.
- **Backward** — *what would I have to prove to get `PreservesOrder` on P?* Returns the alternative
  bodies. Wanted for diagnostics: *this would have pushed had you filtered on `type`* is the difference
  between a feature people can use and one they cannot tell is not working.

### The side condition, in its two strengths

A guarded fact may be used only where the guard holds for the rows it decides. That is one idea with two
implementations, and the difference matters:

- **Sibling conjunct**, for a filter rewrite. Push `type='Bob' AND id='x'`: the rewrite that is invalid
  for non-Bob documents never decides one, because the conjunction has already dropped them. And
  `CosmosFilterSplitRule` keeps the original above as a recheck regardless.
- **Statement entailment**, for everything decided before a row is seen — a sort, routing, a point read,
  a pushed `LIMIT`. Nothing rechecks those, so the condition is that the statement's *pushed* `WHERE`
  entails the guard. Same engine, asked about the pushed predicate instead of one conjunct.

### The sites

| site | asks for | side condition |
| --- | --- | --- |
| `CosmosRexTranslator` comparison rendering | `PreservesEquality` / `PreservesOrder` on the path | sibling conjunct |
| `CosmosFilterSplitRule.TryWeakenConjunct` | either | sibling conjunct |
| `CosmosSortRule` | `PreservesOrder` | statement entailment |
| `CosmosPartitionKeyExtractor.TryExtract` / `TryExtractPrefix` | `PreservesEquality` | statement entailment |
| `TryExtractPointRead` | `PreservesEquality` | statement entailment — **and `CoversExactly` still refuses a discriminator conjunct**, see §5 |
| `CosmosAggregateRule` — `MIN`/`MAX` | `PreservesOrder` | statement entailment |
| `CosmosAggregateRule` — `GROUP BY` / `DISTINCT` | `PreservesEquality` | statement entailment |
| the `Sort`+`Fetch` push | filter exactness | the filter must be wholly pushed |
| #52, index-aware operator choice | either | none — cost, not legality |

`CosmosConvention.Container` already reaches every one of these, and `CosmosFilterSplitRule.Split`
already hands the container to `CosmosRexTranslator`. The theory hangs off `CosmosContainerMetadata`
with no new plumbing.

### Cost

Compiled once per container, at schema registration — the operand never changes, so this is not a cache
with an expiry, unlike the row count. Derivation is O(Σ|body|) per ask, over a theory whose clause count
is linear in the schema. Memoise on the known-atom set only if it ever appears in a profile.

---

## 5. What does not follow, and has to be said

**`type` alone is worth more than it looks.** Every weakening already built carries an admitting guard
and a recheck above it — `NOT IS_NUMBER(x) OR …`, `NOT IS_STRING(x) OR …`. A declared `type` deletes
both. The filter becomes wholly pushed, and a wholly pushed filter is one a `LIMIT` can ride down with.
That is a broad, mechanical gain across every pushdown already in the adapter, and it needs no
representation fact at all.

**A discriminated container still cannot point-read.** `TryExtractPointRead` requires `CoversExactly` —
every top-level conjunct must be the `id` equality or a partition-key equality, because the read applies
no predicate. The discriminator conjunct that licenses the fact is the conjunct that disqualifies the
read. Unless `/type` is the partition key, the chain ends at *routed query*, not *point read*, until #92
lands.

**This is the first operand that can change which rows come back.** Every row of `DESIGN.md`'s *What a
container declares* is sourced from the container definition or a service guarantee. A schema in the
model file is neither, and a schema wrong by one character drops rows with no error and a plan that looks
right. There is no cheap detection — checking means reading documents, which is the inference
`CosmosContainerMetadata` exists to refuse. It is a real change in kind and belongs in that table's
**Source** column explicitly, not discovered later.

---

## 6. The library

### The BCL writes JSON Schema and does not read it

Worth stating first, because it is the thing you would expect to exist. .NET **does** have an official
JSON Schema API — `System.Text.Json.Schema`, shipped in .NET 9 and available on `net8.0` through the
`System.Text.Json` 9.x package. Reflected over the shipped assembly, it is exactly three types:

```
JsonSchemaExporter, JsonSchemaExporterOptions, JsonSchemaExporterContext
JsonSchemaExporter statics: GetJsonSchemaAsNode
```

A .NET type in, a `JsonNode` schema out. That is the whole surface, and it is the wrong direction: it is
what the AI tool-calling and OpenAPI-generation paths use, which is why so many Microsoft APIs *emit*
JSON Schema. Nothing in the box reads one. The asymmetry is the answer — writing a schema from a type is
mechanical, and reading one means `$ref`, `$id`, `$anchor`, dialects and vocabularies.

### Measured against the real candidates

Parsing the parks schema from §3 — `oneOf` with `const` discriminators, `$defs` + `$ref`, `format` and
`pattern`, an `if`/`then`, and an unknown `x-` keyword — with each:

| | licence | JSON stack | result |
| --- | --- | --- | --- |
| **`Microsoft.OpenApi` 2.12.2** | **MIT** | `System.Text.Json`, and nothing else | **recommended** — read everything |
| `JsonSchema.Net` | MIT **through 8.x**, OSMF EULA from 9.0.0 | `System.Text.Json` | read side is awkward — the fluent `OneOf()`/`If()`/`Const()` names are *builder* extensions, and `JsonSchema` exposes only `BoolValue`, `BaseUri`, `Root` |
| `LateApexEarlySpeed.Json.Schema` 4.2 | BSD-3 | `System.Text.Json` | validator-shaped; constructs from the schema but exposes no traversable model |
| `Corvus.Json.Validator` 5.6 | Apache-2.0 | `System.Text.Json` | drags `Microsoft.CodeAnalysis.CSharp` — Roslyn in a database adapter, out |
| `com.networknt:json-schema-validator` 1.5.6 | Apache-2.0 | Jackson, via `MavenReference` | viable; see below |
| `NJsonSchema` 11.6 | MIT | Newtonsoft | out — no Newtonsoft |
| `Newtonsoft.Json.Schema` | AGPL / commercial | Newtonsoft | out twice over |

### `Microsoft.OpenApi`, measured

One dependency, `System.Text.Json`. `net8.0` and `netstandard2.0`. MIT, which an Apache-2.0 project can
consume. `OpenApiSchema` models the whole 2020-12 keyword surface — and my first reading of it, off a
strings scan of 2.0.0, was wrong:

```
Title, Schema, Id, Comment, Vocabulary, DynamicRef, DynamicAnchor, Definitions, Anchor,
Type, Const, Format, Pattern, Enum, Default, AllOf, OneOf, AnyOf, Not, Required, Items,
Contains, Properties, PatternProperties, AdditionalProperties, Discriminator,
UnevaluatedProperties, PropertyNames, DependentSchemas, DependentRequired,
If, Then, Else, Extensions, UnrecognizedKeywords, …
```

`If`, `Then` and `Else` are there. So is `Definitions` for `$defs`, `Anchor` for `$anchor`, and
**`UnrecognizedKeywords`** — which is exactly the "ignore what you do not understand, do not fail"
behaviour the issue asks for, already modelled. Parsing the §3 schema:

| | |
| --- | --- |
| diagnostic errors | 0 |
| `oneOf` branches | 2 |
| `if` / `then` | present / present |
| `oneOf[1].properties.type.Const` | `ParkMap` |
| `oneOf[1].required` | `type` |
| `…data.parkId` | `OpenApiSchemaReference` — a reference node, not a silent null |
| `…data.at` | `format=date-time`, `pattern=^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$` |
| a **bare** schema, no document wrapper | parses — `OpenApiModelFactory.Parse<OpenApiSchema>(json, OpenApi3_1, …)` |

That last row matters: the operand can carry a schema rather than an OpenAPI document. And `$ref`
arriving as a distinct `OpenApiSchemaReference` rather than an inlined copy is the right shape for a
compiler — the walk sees a reference, resolves it, and can cycle-detect at that point.

**Pin at or above 2.7.5.** `Microsoft.OpenApi` 2.0.0–2.7.4 carries GHSA-v5pm-xwqc-g5wc, high severity,
and its subject is *circular schema references may terminate OpenAPI parsing* — independent
corroboration that `$ref` cycles are the hazard §3 flags.

### The one thing the Java option still wins

The operand arrives from Calcite's `ModelHandler` as a `java.util.Map`, so a .NET reader needs it as
text: Jackson `writeValueAsString` out, `System.Text.Json` in. `com.networknt` would read the map
directly through `ObjectMapper.valueToTree` with no round trip, is Apache-2.0, and its required
dependencies are *already compiled into this build* — `jackson.databind.dll`, `jackson.core.dll`,
`jackson.dataformat.yaml.dll` and `org.slf4j.dll` are in the output directory today, dragged in by
calcite-core; `joni` and `graalvm-js` are `<optional>` and stay out.

It still loses. The fact compiler is C#, and walking a Java schema model through IKVM makes every
property access a bridge call against a Java-shaped object graph. One serialise/parse of a schema
document, once per container at registration, is not a cost worth a Java dependency to avoid.

**And it need not be paid at all.** If the operand carries the schema as a *reference* — a path or a URL
— rather than as an inline object, the text goes straight to `System.Text.Json` and there is no bridge
and no round trip. That is also the answer to §7's complaint that a real schema inline in a model file is
unreadable. Two problems, one decision.

### Name the dialect

The issue says "plain JSON Schema (OpenAPI Schema Object)", which names two different things — OpenAPI
3.0's Schema Object is *not* JSON Schema. OpenAPI 3.1 **is** JSON Schema 2020-12, which is what
`Microsoft.OpenApi` reads under `OpenApiSpecVersion.OpenApi3_1`. Pick 2020-12, say so in the operand's
documentation, and the two names stop disagreeing.

---

## 7. Deliberately not in the first cut

- **Array element facts.** `items` at `P + [*]` is well defined and there is no consumer until
  `ARRAY_CONTAINS` and the traversal want one. Named so the path model leaves room.
- **`not`, and undiscriminated `anyOf` beyond the meet.** Both push toward non-Horn.
- **Remote `$ref`.** A URL is a network fetch at schema registration; local `$defs` and same-document
  pointers cover the schemas people have.
- **Cross-database keying.** The account-level schema (no `database` operand) has nowhere to put a
  per-container schema: `containers` already "applies to every database, which is only useful where they
  share container names". Inherit the wart or refuse the combination; do not invent a third thing.
- **Where the schema physically lives.** `containers` is a list of strings today — `GetStrings` calls
  `ToString` on whatever it finds — so per-container configuration means teaching it objects,
  `{"name": "parks", "schema": {…}}`, which is the first time a container has had configuration of its
  own. A large schema inline in a model file is unreadable, so a file reference wants deciding too.

---

## Appendix — the measurements

All of §1 was measured through a `CalciteConnection` with no service and no adapter, from throwaway test
classes that were removed afterwards; they have no consumer yet. IKVM reports `java.version` `1.8.0_504`.

Run-time cast over a row value, `SELECT "v" FROM (VALUES ('<stored>')) AS t("v") WHERE CAST("v" AS UUID)
= UUID'<canonical>'`, kept exactly four stored forms — hyphenated or not, braced or not, any case — and
**raised** on everything else; there is no `SAFE_CAST` to `UUID`. `java.util.UUID.fromString` called
directly disagrees with all of that, rejecting the unhyphenated and braced forms and accepting a
zero-suppressed `1-2-3-4-5` that Calcite refuses, so the cast does not go through it.

The RU figures that sized the *first use* are in the commit that preceded this file: on a serverless
container of 2000 documents, the predicate costs 33.55 RU today, 3.12 RU with a schema-free weakening,
2.82 RU with an exact equality and 1.00 RU as a point read — and the case-insensitive form **used the
index**, so it is not the scan the issue assumes. Those numbers price equality, which is the one member
of the family that already had a schema-free route; they say nothing about the order family in §1, which
has none, and nothing about partition fan-out, which a one-partition account cannot show.
