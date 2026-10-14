# Spike: the fact layer on Eggnog, timed

What the adapter does today with `JsonFactTheory.Derive`, done with Eggnog instead. The aim is to find the shape
that holds up at tens of thousands of compiled rules. It is not part of the solution and not built by CI.

```bash
dotnet run -c Release                          # every size
dotnet run -c Release -- 3000 30000            # only these target rule counts
dotnet run -c Release -- 60000 --no-layered    # skip the rules-as-code engine, which is slow at size
```

It compiles the adapter's own `src/Apache.Calcite.Cosmos.Adapter/Facts/*.cs` from source, so the schemas go through
the real `JsonSchemaFacts` and both engines see exactly the same `JsonFactRule`s. The project depends on `Eggnog`
`0.0.1-pre.46` from nuget.org.

## The engines

| | what it is |
| --- | --- |
| `horn` | `JsonFactTheory.Derive`, as the planner calls it now |
| `flat` | Eggnog with no contexts: a fresh graph per derivation, holding JSON's rules, the container's rules and the query's facts |
| `layered` | Eggnog with contexts, one Eggnog rule per compiled rule (`JsonFactProgram`). JSON's rules and the service's facts sit in a frozen root, a container's rules are local rules of a frozen child, and each query is a child of that, disposed after use |
| `merged` | `layered`, with rules of identical bodies merged into one rule of several heads, as the plan of record's §2.5 does |
| `data` | the same contexts, with a container's rules held as **rows** of its frozen child and read by a fixed set of about fifteen rules in the root (`JsonFactData`) |

Every derivation is followed by the questions consumers ask of a `JsonFactSet`: whether it is contradictory, and
four or five `Knows`. Before anything is timed, **parity** asks both engines about every head the theory can
conclude and every fact a query states. On every corpus measured, every answer agrees.

The synthetic corpus is a container of many kinds, discriminated by a `oneOf` on `kind`. Each kind declares typed
properties (string, integer, number, a canonical UUID, an enum), and a third of them are required. Property names
are shared across kinds with types that differ by kind, so the discriminator guards nearly every rule.

## Results

4 cores, .NET 10.0.13. Each figure is the median time of one derivation plus its questions, with the allocation
beside it. `kind = 'k0'` pins the discriminator, and `kind = 'nope'` is contradicted by the declared kinds.

| compiled rules | distinct guards | query | horn | data | merged | layered | flat |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 11 (the planning tests' schema) | 2 | `kind = 'B'` | 3.4 µs | 455 µs | — | 85 µs | 461 µs |
| 327 | 10 | `kind = 'k0'` | 50–120 µs, 35 KB | 218–670 µs, 347 KB | 158 µs, 179 KB | 1.35 ms, 2.5 MB | 4.7–10 ms, 9.8 MB |
| 3,231 | 32 | `kind = 'k0'` | 454 µs, 315 KB | 289 µs, 415 KB | 255 µs, 362 KB | 16.6 ms, 24 MB | — |
| 3,231 | 32 | `kind = 'nope'` | 802 µs | 54 µs | 635 µs | 14.5 ms | — |
| 11,018 | 60 | `kind = 'k0'` | 1.6 ms, 1.1 MB | 410 µs, 505 KB | 516 µs, 598 KB | 236 ms, 85 MB | — |
| 11,018 | 60 | `kind = 'nope'` | 2.7 ms | 57 µs | 1.9 ms | 134 ms | — |
| 32,805 | 103 | `kind = 'k0'` | 4.9–5.3 ms, 3.1 MB | **626 µs**, 577 KB | 1.24 ms, 966 KB | 510 ms, **251 MB** | — |
| 32,805 | 103 | `kind = 'nope'` | 7.9–8.5 ms | **53 µs** | 4.9 ms | 519 ms | — |
| 65,525 | 146 | `kind = 'k0'` | 11.5 ms, 6.3 MB | **853 µs**, 698 KB | — | — | — |
| 65,525 | 146 | `kind = 'nope'` | 18.5 ms | **58 µs** | — | — | — |
| 109,454 | 189 | `kind = 'k0'` | 18.6 ms, 10.4 MB | **1.06 ms**, 728 KB | — | — | — |
| 109,454 | 189 | `kind = 'nope'` | 28.7 ms | **58 µs** | — | — | — |

Two notes on reading the table:

- **Ranges.** Where a cell gives a range, the figure differed between runs. The first query of a small size runs
  partly before tiered compilation settles.
- **Growth in `data`.** The growth in the `data` column is `k0` itself: at larger sizes it declares more
  properties, so pinning it fires more rules. That is work done, not theory size. A contradicted query does no
  such work, and stays at 55 µs from 3k rules to 109k.

Registering a container is a one-time cost per container:

| compiled rules | read the JSON Schema | horn | data | merged | layered |
| --- | --- | --- | --- | --- | --- |
| 3,231 | 66–116 ms | 2–20 ms | 13–44 ms | 4.6 ms | 0.2–0.5 s |
| 11,018 | 0.7 s | 13–15 ms | 60 ms | 15 ms | 2.4 s |
| 32,805 | 7.3–7.7 s | 41–49 ms | 205–257 ms | 38 ms | **23 s** |
| 65,525 | 29 s | 146 ms | 322 ms | — | — |
| 109,454 | **76 s** | 421 ms | 1.2 s | — | — |

The JSON Schema read is common to every engine. Eggnog's shared root is a one-time cost per process: about 210 ms
for `layered` and `merged`, including first-call JIT, and 24–27 ms for `data`.

## What it shows

- **One Eggnog rule per compiled rule does not scale.** Every rule is evaluated on every iteration of every query,
  whether or not anything it reads changed. So a query costs time and memory in proportion to the size of the
  theory, on the planning thread: 510 ms and 251 MB at 33k rules. Eggnog's roadmap names this cost (idle local
  rules) as unmeasured.
- **Merging identical bodies, as the plan of record's §2.5 does, mostly rescues it.** The cost then follows the
  number of distinct guards rather than the number of rules: 1.2 ms at 33k rules and 103 guards. But a contradicted
  query still evaluates every guard (4.9 ms), and real schemas multiply distinct guards through several things:
  - nested discriminators;
  - `if`/`then`;
  - the `required` carrier of each object;
  - `else` branches;
  - the plan's own F4 carriers.
- **Rules as data does not depend on either count.** `step(r, k)` says that the first `k` atoms of rule `r` hold.
  A query that pins `kind` derives `pinned(kind, v)`, which joins the equality atoms on `(path, value)` through an
  index. The work is the rules that value guards, so at 109k rules a pinned query costs 1.06 ms, against Horn's
  18.6 ms, and a contradicted one costs 58 µs, against Horn's 28.7 ms. The price is registration, about five times
  `merged`'s, and still 0.26 s at 33k rules.
- **Horn grows with the theory.** `Derive(established)` closes the whole theory again from nothing on every call,
  unconditional rules included.
- **Our own schema reader is the bottleneck at size.** It took 76 s for 109k rules. `JsonSchemaFacts.Meet` scans,
  for every candidate claim, every rule of every other branch: quadratic in the size of a `oneOf`, and independent
  of the engine.
- **Eggnog without contexts is a harness, not a planner.** A fresh graph per derivation re-plans the whole theory
  every time.

## What Eggnog proves on small schemas that Horn does not

Speed per derivation is only half the comparison: on small schemas, Horn is fast partly because it proves less.
Two corpora show it on schemas of 8 and 14 compiled rules, and all three Eggnog encodings reach the same answers.
On every corpus, Horn knows nothing Eggnog does not.

| schema | query | Eggnog proves | Horn | what the plan gains |
| --- | --- | --- | --- | --- |
| `kind` required, `enum: [order, invoice]`; `order` declares `ref` a canonical UUID (gap 2) | `kind <> 'invoice'` | `kind = 'order'`, so `ref` is a canonical lowercase UUID | nothing: `Knows` asks one fact at a time | the UUID comparison is pushed as an indexed string equality, rather than run in process over every order |
| `data` declared a string | `data.id = 'x'` | contradictory: a present `data.id` makes `data` a present object, and no string is one | nothing: no rule relates a path to its parent | no request at all |
| `meta` declared an object | `meta.v = 'x'` | `meta` is present | nothing | an `IS_DEFINED(c.meta)` guard is deleted |

The second and third rows are **the rules of JSON itself**, which live in the root:
- a present property has a present object for its parent;
- a path whose possible types have no member in common is **absent**;
- a path both absent and present is a contradiction.

`DESIGN.md` says that two type claims never exclude each other, and for Horn that is right. A type claim
constrains a value only if one is there, and Horn has no atom for absence, so a string-or-object conflict could
mean nothing to it. With a type lattice it means absence, which is a fact. It becomes a contradiction only beside
presence.

Both rows depend on the plan's F4 change, in which a comparison on an accessor proves that its path is present.
`CosmosFactExtractor` does not do that yet, so the corpora state presence explicitly.

These cost 35–175 µs per derivation, against Horn's 1–2 µs. Against that sits a round trip and its RU, or a scan
read whole and filtered in process. A per-statement memo pays the cost once per statement, not once per question.

## What it does not cover

- **Case splits.** No split is used, so gap 1 and gap 3 are not here. Splits and lift are the largest thing
  Eggnog adds beyond Horn, and they want their own spike over this encoding.
- **Concurrency.** Everything runs on one thread.
- **A cache across derivations.**
- **The engine's own logging.** Its statistics in `RunReport` and its timing options are unused.
- **Encoding details.** The encodings are the spike's own: a path is its `$.a.b` spelling, and a scalar is a
  string tagged with its type. The plan of record's encodings (§2.1) differ in detail.
