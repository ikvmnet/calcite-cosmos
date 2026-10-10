# 16. Declaring uniqueness

A JSON Schema describes each document. What it cannot say is anything about *two* documents — and one
such thing decides whether several views of one container cost one read or several: whether a value
names at most one document. That is what a `UNIQUE` constraint declares, and what the self-join merge
(Chapter 12) uses.

## 16.1 What the adapter already knows

Some uniqueness needs nothing declared, because the service enforces it:

| source | constraint |
| --- | --- |
| the service | `UNIQUE (partition key…, id)` — or `UNIQUE (id)` where the partition key is `/id` |
| the container's unique key policy | `UNIQUE (partition key…, paths…)` for each unique key |

`id` and a unique key policy are unique *within a logical partition*, so the adapter states them with
the partition key paths added, which makes them true across the whole container. A document whose
partition key is absent or null is outside these claims — a null equals nothing — so it never pairs
through them.

## 16.2 Declaring a constraint

Anything else you declare in the container's entry, as SQL DDL. The expressions are Calcite SQL over the
document — the same dialect a view is written in:

```json
{
  "name": "links",
  "schema": { },
  "constraints": [
    "UNIQUE (JSON_VALUE(DOC, '$.data.guid'))",
    "UNIQUE (JSON_VALUE(DOC, '$.linkId')) WHERE JSON_VALUE(DOC, '$.type') = 'Link'",
    "UNIQUE (LOWER(JSON_VALUE(DOC, '$.email')))"
  ]
}
```

- The first says no two documents in the container hold the same `data.guid`.
- The second says no two documents whose `type` is `Link` hold the same `linkId`. It is used only where
  **both** sides of a join are proved to be Links. Proved means implied, not spelled the same: a
  constraint scoped `WHERE JSON_VALUE(DOC, '$.type') IN ('Link', 'Other')` is proved by a view
  filtered on `= 'Link'`.
- The third says two emails differing only in case are one.

The grammar is `UNIQUE (expression, …) [WHERE predicate]`. Column names are the table's: `DOC`, `id`,
`_ts`, `_etag` and the partition key columns. `constraints` may appear with or without a `schema`.

## 16.3 What a key means

- **A plain accessor** — `JSON_VALUE(DOC, '$.x')`, or a column such as `id` — stands for the value
  *stored* at that path, which is what a unique key policy means too.
- **Any other expression** stands for its own value: `UNIQUE (LOWER(…))` is about the lower-cased
  value.
- **A document whose key is null is outside the claim**, because a null equals nothing. A constraint
  over an optional property still means something.

Using a constraint also requires that the *query* reads the key faithfully. A text accessor reads the
number `1` and the string `"1"` the same way, so a join on `JSON_VALUE` uses a stored-value
constraint only where the container's facts (Chapter 14) give the path one scalar type; a join on a
`UUID` cast uses it where the path has a declared UUID form. A declared expression key covers joins
computed from what it names: `UNIQUE (LOWER(code))` covers a join on `code`, but `UNIQUE (code)` does
not cover a join on `LOWER(code)`, since two codes can share a lowercase.

## 16.4 When it is checked

Each constraint is parsed and validated against the container's columns **when the model is read**. One
that does not parse or compile fails the model there, naming the container. `CHECK` constraints are not
read yet and are **refused by name**, not ignored — a constraint silently dropped is one you believe is
in force. So is anything else in `constraints` the adapter does not understand.

A constraint whose `WHERE` the adapter cannot prove from a query's own predicates is simply not used
for that query. That costs a read, never a row.

## 16.5 This is a stronger promise than a schema, and nothing checks it

A schema can be checked one document at a time. A `UNIQUE` constraint is about every *pair* of
documents, and checking it would mean reading the container. If two documents do share a value, a
join that should pair them is read as one document paired with itself, and **the second document's rows
are missing from the answer** — no error, and a plan that looks right.

Declare one only where the application makes it so, for every writer, for every document already
in the container. The service-enforced alternative is a unique key policy — which can only be set when
a container is created, and is unique within a partition.

## 16.6 Choosing a key for views you will join

If you control the data, the cheapest key to join views on is the one the service already enforces:
the partition key with `id`. A join that equates both needs nothing declared and nothing trusted. A
single-column key on anything else needs a declaration, and the declaration is your promise.

---

[← Previous: Dates and times](15-dates-and-times.md) · [Contents](README.md) · [Next: Full text and vector search →](17-full-text-and-vector-search.md)
