# 17. Full text and vector search

Cosmos has full text search and vector search; SQL has neither. The adapter supplies the functions, so
a query can name them, and translates them into the service's own.

## 17.1 The functions

| function | returns | where it may appear |
| --- | --- | --- |
| `FULLTEXTCONTAINS(path, keyword)` | boolean | `WHERE` |
| `FULLTEXTCONTAINSALL(path, keyword, …)` | boolean — every keyword occurs | `WHERE` |
| `FULLTEXTCONTAINSANY(path, keyword, …)` | boolean — any keyword occurs | `WHERE` |
| `FULLTEXTSCORE(path, keyword, …)` | a BM25 relevance score | `ORDER BY` only |
| `RRF(score, score, …[, weights])` | a fused score (reciprocal rank fusion) | `ORDER BY` only |
| `VECTORDISTANCE(v1, v2[, bruteForce[, options]])` | similarity | anywhere; ordering by it ranks |

```sql
SELECT c."id"
FROM "products" AS c
WHERE FULLTEXTCONTAINS(JSON_VALUE(c."DOC", '$.description'), 'steel')
→ … WHERE FULLTEXTCONTAINS(c.description, @p0)
```

**The first argument must be a property path** — `JSON_VALUE(c."DOC", '$.x')` or a column. Anything else
is refused, because the service requires a path. Keywords are bound as parameters.

The keyword arguments are separate arguments, not an array.

## 17.2 Reaching the names

The functions are declared by the Cosmos schema itself, so a connection resolves them the way it
resolves a table — **from the connection's default schema and the root, and nowhere else**:

- Name the Cosmos schema as the model's `defaultSchema`, and call them unqualified; or
- qualify the call: `"COSMOS"."FULLTEXTCONTAINS"(…)`.
- A model view resolves names against its own `path`: give it `"path": [ "COSMOS" ]` or qualify.

With an account-level schema (Chapter 4), the functions are declared on the account schema too, so a
query rooted there can name them while reading `"inventory"."products"`.

Through a schema, the variadic functions accept up to **16** arguments. A host that assembles its own
planner can chain the adapter's operator table instead (Chapter 21), which has no such bound.

## 17.3 Ranking with `ORDER BY RANK`

Ordering by a score becomes Cosmos's `ORDER BY RANK`:

```sql
SELECT c."id"
FROM "products" AS c
ORDER BY FULLTEXTSCORE(JSON_VALUE(c."DOC", '$.description'), 'steel', 'frame')
FETCH FIRST 10 ROWS ONLY
→ SELECT TOP 10 VALUE { "id": c.id } FROM products c
  ORDER BY RANK FULLTEXTSCORE(c.description, @p0, @p1)
```

`RRF` fuses several scores — two full text scores, or a full text score and a vector distance for
hybrid search:

```sql
ORDER BY RRF(FULLTEXTSCORE(JSON_VALUE(c."DOC", '$.description'), 'steel'),
             VECTORDISTANCE(JSON_QUERY(c."DOC", '$.embedding'), ?))
```

Rules the service imposes:

- **A score cannot be selected.** `FULLTEXTSCORE` and `RRF` may appear only in `ORDER BY RANK`; every
  other place — the select list, a `WHERE`, a derived table — is rejected by the service. The score
  orders the rows and never appears in the result.
- **`ORDER BY RANK` ranks without filtering.** It returns every document, matches first. Add a
  `FULLTEXTCONTAINS…` predicate to return only matches.
- **No other ordering**, and no `GROUP BY`, in a ranked statement.

> **Ranking needs a planner you build.** Through a `CalciteConnection` the score-ordering does not
> reach the service: the connection removes the extra score column only after planning, so the
> pattern the adapter recognises never appears, and the statement fails to implement. The predicates
> are unaffected. Hosts that plan statements themselves (Chapter 21) get `ORDER BY RANK`. This is
> [#46](https://github.com/ikvmnet/calcite-cosmos/issues/46), and the provider's prepare path is not
> expected to change, so treat it as a limitation.

## 17.4 What the container declares decides the cost

A full text function pushes over **any** property path. What the container declares about that path —
in its full text policy, in a full text index, or both — decides the **price**: an index seek over a
declared path, a scan over an undeclared one. Measured against several accounts, the service answers a
full text call over an undeclared path, over a container with no full text policy, and even on an
account without the full text capability; refusing such a plan would turn a slow query into a failed
one. The planner prices undeclared paths as scans and keeps the plan.

For anything beyond a small container, declare the paths you search in the container's full text policy
and indexing policy.

`VECTORDISTANCE` is different: it pushes only where **at least one** of its two vectors is a path the
container declares in its vector embedding policy or vector indexes, because the service requires a
vector policy to search. The other vector may be a literal or parameter — searching for the neighbours
of a supplied embedding is the point.

## 17.5 The shared full text vocabulary

`Apache.Calcite.FullText` defines store-neutral `CLR_FT_*` functions, so a query can be written once
against any full text store. The Cosmos schema declares them beside its own names, and they render as
the service's spellings:

| written | rendered |
| --- | --- |
| `CLR_FT_CONTAINS(p, kw)` | `FULLTEXTCONTAINS(p, @p0)` |
| `CLR_FT_CONTAINS_ALL(p, kw, …)` | `FULLTEXTCONTAINSALL(p, …)` |
| `CLR_FT_CONTAINS_ANY(p, kw, …)` | `FULLTEXTCONTAINSANY(p, …)` |
| `CLR_FT_SCORE(p, kw, …)` | `FULLTEXTSCORE(p, …)` — `ORDER BY RANK` only |
| `CLR_FT_RRF(score, …)` | `RRF(…)` — `ORDER BY RANK` only; `CLR_FT_WEIGHT` becomes its weights array |
| `CLR_FT_PHRASE(text)` | the text itself — the service already reads a multi-word term as a phrase |
| `CLR_FT_FUZZY(text, edits)` | `{"term": text, "distance": edits}` |
| `CLR_FT_PREFIX(text)` | **refused** — the service has no prefix term, and `STARTSWITH` is a different question |

Use one route per planner: register the vocabulary through the Cosmos schema (automatic through a
connection) *or* chain `FullTextOperatorTable` in your own planner — not both.

## 17.6 What is deliberately not done

`LIKE '%steel%'` is **not** rewritten into `FULLTEXTCONTAINS`, even over a declared path. They answer
different questions: `LIKE` matches `steelworks` and not `Steel`; full text matches `Steel` (case
folding) and probably `steels` (stemming, in the language the container's policy declares) and not
`steelworks`. Neither implies the other, so no rewrite is safe. Ask for full text by name.

## 17.7 Testing

The Cosmos DB emulator does not implement full text search: it rejects the functions and discards
full text policies. Test these queries against a real account (Appendix E). A function that is not
pushed down — for example, because its first argument is not a path — fails when the query runs with
*… is evaluated by the service and has no in-process body*.

---

[← Previous: Declaring uniqueness](16-constraints.md) · [Contents](README.md) · [Next: Geography →](18-geography.md)
