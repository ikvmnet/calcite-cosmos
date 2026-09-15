# Issue #93 — evaluation

*A container described by a plain JSON Schema in the operand, compiled to a cached
(guard ⟹ path-facts) truth table, with exact UUID equality / routing / point read as the first use.*

**The verdict in one paragraph.** The mechanism is sound where it is trusted and the design is a good
one; what does not survive contact is the *size of the prize*. Measured against a real account, the
flagship case — `CAST(JSON_VALUE(c."DOC",'$.id') AS UUID) = UUID'x'` — goes from **33.55 RU** today to
**3.12 RU** with a pushdown that needs no schema whatsoever, and to **2.82 RU** with one. The schema is
worth the last **0.30 RU of 30.7**, on a container with one physical partition. Its real case is
partition *pruning* on a container with several, which is unmeasured here and is the thing to measure
before building anything. Two defects in the proposal as written: the pattern probe is not a proof and
can produce wrong rows from a conforming document, and the worked example cannot reach a point read on
today's `main`. And one boundary worth stating out loud: this would be the first caller-supplied
operand in the adapter that can silently change *which rows a query returns*.

---

## 1. What was measured

Against the probe account (`calcite-geo-probe-79422`, serverless, East US 2) over a new container
`calcite_cosmos_pointread/uuidprobe` — partition key `/k`, default indexing policy, 2000 documents
across eight logical partitions, each carrying a lowercase-canonical UUID at `/u` and the same value at
`/id`. One document matches. `PopulateIndexMetrics` on.

| form | RU | rows returned |
| --- | --- | --- |
| `SELECT * FROM c` — what the predicate costs today | **33.55** | 2000 |
| `WHERE UPPER(c.u) = '<UPPER>'` | 3.12 | 1 |
| `WHERE STRINGEQUALS(c.u, '<UPPER>', true)` | **3.12** | 1 |
| `WHERE REGEXMATCH(c.u, '^<lower>$')` | 2.83 | 1 |
| `WHERE c.u = '<lower>'` | **2.82** | 1 |
| `WHERE STRINGEQUALS(c.u, '<lower>')` | 2.82 | 1 |
| `ReadItemStreamAsync(id, pk)` | **1.00** | 1 |

**The case-insensitive form is not a scan.** The issue's premise — that `STRINGEQUALS(…, true)` is "a
service-side scan, not a point read" — is half right, and the half that is wrong is the expensive half.
The index was utilised for it (`{"IndexSpec":"/u/?"}`), it returned one document, and it cost 3.12 RU
against 2.82 for the exact form: **11% more, not a scan**. For contrast, reading the container whole is
33.55 RU and 2000 documents over the wire, and that is what the predicate does today.

So of the 30.7 RU the whole exercise can save on this container, **30.4 are saved by pushing
*anything*, and 0.3 by pushing something *exact*.** The point read saves a further 1.8.

**What this container cannot show is fan-out**, and that is where the schema's case actually lives. It
is serverless with one physical partition, so "routed" and "cross-partition" are the same request —
which the numbers say plainly, the explicitly routed forms costing 0.10 RU *more* for the partition-key
index lookup they add. `DESIGN.md` under *The lookup restriction is already routed* records the fact
that matters here, measured on a four-partition account: **the router prunes from an equality in the
predicate**, with nothing pinned by the adapter. An exact `c.pk = 'v'` therefore contacts one
partition; `STRINGEQUALS(c.pk, 'V', true)` names no value and contacts all of them. That difference
scales with partition count and is the only place the schema earns a large number.

### What Calcite's UUID cast actually accepts — measured

Through a `CalciteConnection`, run-time cast over a row value, kept by `= UUID'123e4567-…'`:

| stored | kept |
| --- | --- |
| `123e4567-e89b-12d3-a456-426614174000` | yes |
| `123E4567-E89B-12D3-A456-426614174000` | yes |
| `123e4567e89b12d3a456426614174000` | yes |
| `123E4567E89B12D3A456426614174000` | yes |
| `{123e4567-e89b-12d3-a456-426614174000}` | yes |
| `(…)`, `urn:uuid:…`, leading or trailing space, the `0x…,{0x…}` form | **raises** `IllegalArgumentException` |

Two things follow. **`TODO.md`'s enumeration is right**: hyphenated or not, braced or not, four forms,
with case collapsed by `STRINGEQUALS(a, b, true)` — so the equivalence that section claims is available
is available, and it is available with no schema. (Note that the cast does *not* go through
`java.util.UUID.fromString`, which rejects the unhyphenated and braced forms and accepts the
zero-suppressed `1-2-3-4-5` that Calcite refuses. Measured both ways; IKVM reports `java.version`
`1.8.0_504`.)

**And there is no `SAFE_CAST` to `UUID`** — *No match found for function signature
`SAFE_CAST(<CHARACTER>, <UUID>)`* — so a stored string that is not one of those four forms makes the
query **raise**. That is the same objection `DESIGN.md` records against bounding a `DECIMAL` cast:
"excluding the document would turn a failing query into a passing one." A weakening for UUID has to
*admit* every value it cannot vouch for, exactly as the numeric bound does, and `NOT IS_STRING(x)` is
not wide enough on its own — a non-UUID *string* must also reach the recheck. `REGEXMATCH` is already
mapped and pushable (`CosmosOperators.RegexMatch`), so the admitting guard is constructible:
`NOT (IS_STRING(x) AND REGEXMATCH(x, '<the four forms>')) OR <the STRINGEQUALS disjunction>`.

---

## 2. What the schema buys that nothing else does

Stripping out everything obtainable another way, the residue is small and real:

- **Partition pruning.** Routing is decided before any row is seen, so no per-row guard can substitute
  for it. This is the one item with no alternative, and on `DESIGN.md`'s own account it is "usually the
  largest single difference in cost".
- **A point read.** Same argument, one step further: the read applies no predicate at all.
- **An exact filter, so a `LIMIT` can be pushed with it.** A weakening leaves a recheck above, and a
  recheck means the fetch cannot stop at *n*. This is worth more than the 0.3 RU above and is not
  mentioned in the issue.
- **A guaranteed sort order.** The date case: with the stored shape pinned, `ORDER BY c.v` pushes over
  every row in one statement, where the sound alternative is a guarded sort plus a complement read plus
  an in-process merge — "a larger one than this item", as `TODO.md` puts it.

Everything else the issue claims is already reachable without it, and the measurements above are why.

---

## 3. Where the proposal is unsound as written

**The pattern probe is evidence, not proof.** The rule proposed is: generate one canonical lowercase
sample and one uppercased sample, run the declared `pattern` against each, and conclude
*lowercase-canonical* when the first is accepted and the second rejected. The conclusion the adapter
needs is universal —

> every string the pattern accepts is lowercase-canonical

— and two probes establish two facts about two strings. They do not imply it. A pattern with one group
left case-insensitive,

```
^[0-9a-f]{8}-[0-9a-fA-F]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$
```

accepts the lowercase sample, rejects the fully-uppercased one, and still admits
`123e4567-E89B-12d3-a456-426614174000`. A document storing that **conforms to the declared schema** and
is dropped by the exact equality the probe licensed. That is a wrong answer produced by a *conforming*
document, which is not the failure mode the issue reserves for data-integrity problems — it breaks the
claim that "an under-specified schema loses pushdowns, it does not produce wrong rows." Unanchored
patterns fail the same way: JSON Schema's `pattern` is an unanchored partial match, so `^[0-9a-f-]{36}`
admits a conforming value with a suffix, and no two-point probe can see it.

Three ways out, in order of preference:

1. **Recognise, don't probe.** Keep a small table of exact canonical pattern spellings per format; take
   the fact only when the declared `pattern` is one of them. Sound by construction, trivial to test,
   extends by adding a row — and schema authors copy these patterns from the same handful of sources
   anyway. It also removes the "sample generation per format" open question rather than answering it.
2. **Say the fact instead of implying it.** One annotation keyword —
   `"x-cosmos-stored-form": "canonical-lower"` — is legal plain JSON Schema (unknown keywords are
   annotations; validators ignore them), keeps the standard vocabulary the issue wants for everything
   else, and states the thing the adapter is going to trust rather than inferring it from a regex. This
   is not the bespoke path-fact list the issue rejected; it is JSON Schema plus one annotation at the
   single point where inference is unsound.
3. **Decide language containment properly** — `L(pattern) ⊆ L(canonical)` is decidable, and it wants an
   automaton library with ECMA-262 semantics. Correct, and far more machinery than the payoff.

Whichever is chosen, probing should be kept as a *refutation*: any generated non-canonical sample the
pattern accepts withdraws the fact. It is cheap, and it turns a mistyped canonical pattern into "no
fact" rather than into a wrong one. It also needs a `Regex` match timeout — an author-supplied pattern
run at registration is a catastrophic-backtracking target.

---

## 4. The worked example does not reach a point read

`WHERE type='ParkMap' AND parkId = UUID'x'` is offered as ending in a point read. On today's `main` it
cannot. `CosmosPartitionKeyExtractor.TryExtractPointRead` requires `CoversExactly` — *every* top-level
conjunct must be the `id` equality or a partition-key equality, because the read applies no predicate of
its own. The discriminator conjunct that licenses the fact is itself the conjunct that disqualifies the
read. Unless `/type` is the partition key, a discriminated container never point-reads.

So the chain the issue describes is **exact equality → pruning → *#92* → point read**, and the
dependency on #92 (read-then-filter for a residual conjunct) is tighter than the issue allows: it is not
"with #92 for any residual", it is "only with #92". Worth noting that #92 is unmerged.

Routing is unaffected — `TryExtract` tolerates extra conjuncts — so the pruning half of the payoff
stands on its own.

---

## 5. The boundary against a surface already rejected

`TODO.md` §6 records, twice, that a caller-declared description of the documents is the thing being
avoided: "a surface that asks the caller to describe the documents before querying them is the thing
being avoided, not a feature that is missing", covering "every spelling — a `columns` operand, promoted
caller-declared paths, container computed properties". #93 is a caller-declared description of the
documents. It is worth being explicit about what is and is not reopened.

**What is not reopened.** The row model does not move. `DOC` stays the only column; no path is promoted;
nothing gets a SQL type it did not have; the planner's type reasoning is untouched. The schema is
consulted only to decide whether one rewrite is legal. `TODO.md` §4 already sanctions exactly this narrow
form, in as many words: *"That promise is much narrower than a type … Not this column is a `TIMESTAMP`,
only these strings share a shape. An operand could carry it without settling the typed-column question at
all."*

**What is reopened, and should be said rather than discovered.** `DESIGN.md`'s table under *What a
container declares* has a **Source** column, and every row in it reads "Container definition" or "Service
guarantee". A JSON Schema in the operand would be the first row sourced from the model file — and the
first caller-supplied operand anywhere in the adapter that can change *which rows a query returns*.
`containers` decides what exists; `statisticsExpireSeconds` and `indexMetrics` are cost;
`lookupCacheExpireSeconds` is the nearest precedent and it is a bounded, named staleness rather than a
silent wrong answer. A schema that is wrong by one character drops rows, with no error and a plan that
looks correct, and there is no cheap way to detect it — checking would mean reading the documents, which
is the inference `CosmosContainerMetadata` exists to refuse.

That is the decision, and it is not a technical one: **is the adapter willing to take a
correctness-bearing promise from a model file?** Everything else in the issue follows from the answer.

---

## 6. For everything except routing, the guard is still available — and it is stronger

`TODO.md` §6's replacement for a declaration is the service's own per-row predicates, and the argument
given is that "a guard is stronger than a declaration, because it is evaluated against the document
rather than promised about it". That argument has not weakened; it has got better, because **`REGEXMATCH`
is already mapped and pushable** and expresses any shape check directly:

- The date-sort guard `TODO.md` builds out of `LENGTH(c.v) = 20 AND ENDSWITH(c.v,'Z')` — explicitly noted
  there as "not one shape", wanting a different guard per container — is one `REGEXMATCH` with one
  pattern, for every shape, with no per-shape rule.
- The UUID admitting guard §1 needs is one `REGEXMATCH`.

Which suggests the most useful thing the schema's `pattern` could do is **be passed to `REGEXMATCH` as a
service-side guard rather than trusted as a promise.** Then a wrong or sloppy schema costs a pushdown and
never a row, and the issue's own "the onus is on the schema author" becomes true in the strong sense
rather than the weak one. Measured cost of that choice, from §1: `REGEXMATCH` on an anchored exact
pattern is 2.83 RU against 2.82 for the equality — for the filter it is free.

It does not extend to routing or to a point read, because both are decided before a row is seen. So the
clean split is:

| | sound without trust | needs the promise |
| --- | --- | --- |
| filter pushdown | yes — weaken and recheck, or `REGEXMATCH` guard | |
| index utilisation | yes — measured, even case-insensitively | |
| `LIMIT` with the filter | | yes |
| sort order | partly — guarded sort plus complement read | yes, for one statement |
| **partition pruning** | | **yes** |
| **point read** | | **yes** |

**If the schema is built, that table is the argument for scoping it to what only it can do** — a routing
declaration — rather than to a general basis for pushdown.

---

## 7. What it would cost, and what is already in place

More in place than the issue suggests:

- `CosmosContainerMetadata` is already the per-container fact bag, already reaches the rules through
  `CosmosConvention.Container`, and is already handed to `CosmosRexTranslator` in
  `CosmosFilterSplitRule.Split`. A compiled truth table hangs off it with no new plumbing.
- `CosmosFilterSplitRule.TryWeakenConjunct` is the extension point for the schema-free tier, beside the
  numeric bound and the text-comparison weakening that are already there.
- Routing, point reads and batch point reads are built (`CosmosPartitionKeyExtractor`,
  `CosmosQueryExecutor.ReadItemAsync`). Nothing new is needed downstream.
- **The only consumer needed for the flagship case is one rewrite** — replace
  `CAST(<path> AS UUID) = UUID'v'` with `<path> = 'v'` when the table says the path is
  lowercase-canonical and the guard is entailed. Everything after that (translation, index use, pruning,
  `TryExtract`, the point read) already follows from a plain string equality.

What is new: operand reading and validation; a schema model over the `java.util.Map` the model delivers;
the truth-table compiler; fact derivation; guard entailment; the one rewrite; and tests at this
repository's usual ratio. Call it **~1000 lines of adapter and ~1000 of tests** — against 130–300 for each
of the recent feature commits, so *large*, and the largest single item since full text.

One note on entailment. Conjunct containment is sufficient here for a *weakening*, because the split rule
keeps the original as a recheck; a `Park` document whose `parkId` is uppercase is dropped by the pushed
equality and would have been dropped by the retained `type='ParkMap'` anyway. It is **not** sufficient by
inspection for routing or a point read, where nothing is rechecked. Also: entailment must be evaluated
against the *original* condition, before `Split` partitions it, and `IN` arrives as a `SEARCH` over a
`Sarg` — `RexUtil.expandSearch` first, the way `TryExtractPointReadSet` already does.
`RelMdPredicates.getPulledUpPredicates` would be a stronger source than syntactic conjuncts, and the
null-placement rule already reads it.

---

## 8. Open questions the issue does not list

- **Which dialect.** "Plain JSON Schema (OpenAPI Schema Object)" names two different things: OpenAPI
  3.0's Schema Object is *not* JSON Schema and has no `if`/`then`/`else` at all, which is the keyword the
  whole discriminated-container argument rests on. OpenAPI 3.1 is JSON Schema 2020-12. Pick 2020-12 and
  say so.
- **`$ref` and `$defs` are missing from the interpreted subset**, and a real schema — certainly any
  schema generated from an OpenAPI document — is mostly `$ref`. Without them the feature yields no facts
  on the schemas people actually have. They belong in the first cut, not in "deferred". `allOf` belongs
  with them for the same reason.
- **Where the schema goes.** `containers` is a list of strings today (`GetStrings` calls `ToString` on
  whatever it finds), so per-container configuration means teaching it to accept objects —
  `{"name":"parks","schema":{…}}` — which is the first time any container has had configuration of its
  own. The account-level schema (no `database` operand) has no way to key it: `containers` already
  "applies to every database, which is only useful where they share container names", and a schema
  inherits that wart.
- **Inline or referenced.** A model file is JSON; a real schema inline in an operand is large and
  unreadable. A path or URL wants deciding, and a URL is a fetch at schema-registration time.
- **Where the `required` keyword lands.** The issue's own reasoning ("a `Park` may not even have
  `parkId`") is a `required` fact, and `required` is not in the interpreted subset.
- **An `enum` on a value field has no consumer.** `const` on a discriminator is load-bearing — it is what
  produces the guards. A general `enum` is named "first-class" and no rule is identified that would read
  one. That is machinery ahead of the feature that uses it, which has been declined here before.

---

## 9. The measurement that would decide it

One, and it is the one this evaluation could not run:

> On a container with **several physical partitions**, over an indexed partition-key path holding
> lowercase-canonical UUIDs: the RU and partitions contacted for `WHERE c.pk = '<lower>'` against
> `WHERE STRINGEQUALS(c.pk, '<UPPER>', true)`, at one match.

That is the entire case for the schema, in the currency the rest of the file is written in. The probe
account is serverless and has one physical partition, so it cannot answer; the question needs a
provisioned container above the throughput at which Cosmos splits, which is a new account or a
provisioned database on an existing one — the owner's call, not this branch's.

Two smaller ones worth having beside it:

- Whether a **container computed property** — `LOWER(c.data.id)`, declared to the service, indexable —
  answers the exact-match half with no trust and no new operand. `TODO.md` §5 still lists computed
  properties as an unclaimed *medium* item while §6 records them as covered by the typed-column
  rejection; the two disagree, and a computed property is declared metadata of the kind
  `CosmosContainerMetadata` already trusts. It cannot route — a partition key is a stored path — so it
  does not replace the schema, but it may replace most of what the schema would be used for. Whether one
  can carry an `ORDER BY` is the interesting part, and the 2206 measured for a computed *expression* in
  the clause says nothing about a computed *property*.
- Whether the case-insensitive 11% premium is constant or grows with container size. On 2000 documents
  it is 0.30 RU.

---

## 10. Recommendation

**Do not build it yet, and do not build it at the proposed scope.**

1. Take the schema-free tier first. It is `TODO.md` §4's existing design, it needs no new surface, no
   trust and no decision, and it is measured at **30.4 RU of the 30.7 available** on the container above.
   It also has to be built anyway, as the fallback for every container without a schema. One correction
   to that plan from §1: the admitting guard needs `REGEXMATCH`, not `NOT IS_STRING` alone, or the
   rewrite turns a raising query into a passing one.
2. Run the multi-partition measurement in §9. If the fan-out difference is what `DESIGN.md`'s routing
   note implies, the schema has a real number behind it and the rest of this is worth doing.
3. If it is built, scope it to what only it can do — routing, the point read, `LIMIT`-with-filter and the
   sort — and take the `pattern` as an argument to a service-side `REGEXMATCH` guard everywhere else.
4. Replace the two-point probe with recognition of known canonical patterns, or with one stated
   annotation. Do not ship the probe as specified.
5. Put `$ref`, `$defs`, `allOf` and `required` in the first interpreted subset, and name the dialect.

The thing to decide before any of that is §5: whether this adapter takes a correctness-bearing promise
from a model file at all. Every current row of *What a container declares* is sourced from the service.

---

## Appendix — reproducing the measurements

Both were run from throwaway test classes in `Apache.Calcite.Cosmos.Adapter.Tests` and removed
afterwards; neither is committed, because neither has a consumer yet.

- **The Calcite cast probe** needs nothing but a `CalciteConnection`: `SELECT "v" FROM (VALUES
  ('<stored>')) AS t("v") WHERE CAST("v" AS UUID) = UUID'<canonical>'`, one statement per stored form.
- **The RU probe** used `CosmosClient(endpoint, new DefaultAzureCredential())` against
  `https://calcite-geo-probe-79422.documents.azure.com:443/`, database `calcite_cosmos_pointread`,
  container `uuidprobe` — created for this with `az cosmosdb sql container create … --partition-key-path
  "/k"` and seeded with 2000 documents (13340 RU). The container is left in place, as `pointread` was for
  #92; delete it if it is not wanted.
