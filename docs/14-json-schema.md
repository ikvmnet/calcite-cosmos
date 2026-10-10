# 14. Describing documents with JSON Schema

A container has no row schema, so the adapter cannot know what a document path holds — and that is
what keeps some comparisons in process. Cosmos stores a UUID as a string; SQL has a `UUID` type; and
without knowing *how* the string is written, the adapter cannot turn a comparison of UUIDs into a
comparison of strings the service can make. The same is true of an instant stored as text, and of a
number stored as text.

You can tell it, by giving a container a **JSON Schema** in the model. This chapter describes what to
write, what it unlocks, and — because it is the one input that can change which rows a query returns —
exactly what it promises.

## 14.1 Adding a schema

A container entry in `containers` may be an object carrying the container's name and a `schema`
(Chapter 4):

```json
"containers": [
  "orders",
  {
    "name": "shipments",
    "schema": {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "properties": {
        "carrier":    { "type": "string" },
        "trackingId": {
          "type": "string",
          "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"
        }
      }
    }
  }
]
```

The schema is compiled once, when the model is read, into **facts** about paths — "`$.trackingId`
holds a canonical lowercase UUID", "`$.carrier` holds a string" — that the planner can prove things
against. Nothing is checked against documents.

Remember that listing a container turns discovery off (Chapter 4): once one container has a schema,
list every container you want exposed.

## 14.2 What it unlocks

### A comparison the service can make

```sql
SELECT c."id" FROM "shipments" AS c
WHERE CAST(JSON_VALUE(c."DOC", '$.trackingId') AS UUID) = UUID '123e4567-e89b-12d3-a456-426614174000'
```

```
without a schema   the container is read whole and the comparison made in process
with one           WHERE c.trackingId = @p0   — and routed to the one partition holding it,
                   or a point read where the predicate says nothing else
```

`<`, `<=`, `>` and `>=` lower the same way where the form preserves order (14.3). A keyset page —
`WHERE CAST(…) > ? ORDER BY 1 FETCH NEXT 50 ROWS ONLY` — becomes a page the service serves, rather
than a container read for every page. A range names no single value, so it is not routed to one
partition.

### Selecting the value

A UUID is usually in the select list as well as the `WHERE`, and until its spelling is known there is
nothing to send for it — the whole projection stays in process, and a sort above it cannot be pushed:

```sql
SELECT CAST(JSON_VALUE(c."DOC", '$.trackingId') AS UUID) AS "Id",
       JSON_VALUE(c."DOC", '$.carrier') AS "Carrier"
FROM "shipments" AS c ORDER BY 2 FETCH NEXT 20 ROWS ONLY
```

```
without a schema   the container is read whole, then sorted and paged in memory
with one           ORDER BY c.carrier at the service, 20 documents returned
```

The service sends the stored text and the adapter converts it back to a UUID as it is read — with the
same function Calcite's own cast uses — so the column arrives in .NET as a `Guid`.

Sorting *by* such a column (`ORDER BY 1` above) additionally needs the stored strings to sort the way
the values do, which the form says too.

### An exact comparison where there would be a weakened one

A comparison over `JSON_VALUE` is normally sent weakened — `NOT IS_STRING(c.carrier) OR c.carrier >=
@p0` — and rechecked, because the service orders values across JSON types where SQL compares their
text (Chapter 8). Where the schema says the path holds a string, there is no second type to disagree
over: the comparison is sent exactly, nothing is rechecked, and a `FETCH` can travel with it. A
`type` declaration earns its keep on its own.

Similarly, a path declared `required` needs no `IS_DEFINED` guard, which removes documents the service
would otherwise send only for the recheck to discard.

## 14.3 Stored forms: what a `pattern` proves

A `pattern` is what pins the spelling of a string, and the adapter recognises a fixed family of
spellings rather than interpreting arbitrary regular expressions. A pattern it does not recognise
simply yields no fact. Each recognised form says two independent things: whether comparing the stored
strings for **equality** answers what comparing the values does, and whether their **order** does.

| declared `pattern` | equality | order |
| --- | --- | --- |
| `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$` — canonical lowercase UUID | ✔ | ✔ |
| the same with a version digit or the variant nibble `[89ab]` pinned | ✔ | ✔ |
| the same in uppercase throughout | ✔ | ✔ |
| the same braced (`{…}`) or without hyphens | ✔ | ✔ |
| `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$` — ISO-8601 UTC, whole seconds | ✔ | ✔ |
| the same with a fixed number of fraction digits, `\.[0-9]{n}`, and/or `\+00:00` or no zone in place of `Z` | ✔ | ✔ |
| `^[0-9]{4}-[0-9]{2}-[0-9]{2}$` — a calendar date | ✔ | ✔ |
| `^[0-9]{5}$` — digits at a fixed width | ✔ | ✔ |
| `^(0\|[1-9][0-9]*)$`, `^[1-9][0-9]*$` — digits with no leading zero | ✔ | ✘ |
| an `enum` of strings | ✔ | ✘ |
| `^[0-9]+$`, `^\d+$`, anything signed | ✘ | ✘ |
| any other pattern | ✘ | ✘ |

`\d` and `[0-9]` are read as the same thing, and whitespace in the pattern is ignored. A UUID pattern
is read as a shape — 32 hex slots, hyphens in fixed places, anchored at both ends — so any narrower
confinement of the slots (a pinned prefix, `[1-5]` for the version, `[8-9a-b]` for the variant)
is recognised too. A pattern that mixes cases, or a character range like `[8-f]` that spans
non-hex characters, is refused, because it admits two spellings of one value.

**`format` proves nothing.** JSON Schema calls `format` an annotation, not an assertion, and RFC 9562
dropped the rule that UUIDs are written in lowercase. `"format": "uuid"` does not say how the value is
written; a `pattern` does. Write the pattern.

### Numbers stored as strings

A path holding `"00042"` rather than `42` — an account number, a padded code — is reached with
`CAST(JSON_VALUE(…) AS INTEGER)`, which has no Cosmos form, so the container is read whole. With a
pattern it becomes a comparison the service can make, and the literal is written in the container's
own spelling:

```
with ^[0-9]{5}$             c.n = '00042', and c.n > '00100' for a range — equality and order
with ^(0|[1-9][0-9]*)$      c.n = '42' — equality only, because '9' sorts after '42'
with ^[0-9]+$               nothing, because '42' and '042' would both be forty-two
```

**The last line is the one to watch.** `^[0-9]+$` is the natural way to say "digits", and it gives one
value two spellings — Calcite reads `'042'` and `'42'` both as 42 — so the adapter treats it as saying
nothing rather than pushing a comparison that would miss documents. Say `^[0-9]{5}$` if the values are
padded, `^(0|[1-9][0-9]*)$` if they are not. A literal with no spelling in the form — six digits
against a five-digit form, a negative number — is not pushed.

### Why a UUID's order depends on the engine

Lowercase canonical UUIDs sort as strings exactly as they sort as 128-bit unsigned numbers, which is
how Calcite compares UUIDs from version 1.43
([CALCITE-7716](https://issues.apache.org/jira/browse/CALCITE-7716)). Calcite can be switched back to
its old signed comparison with the system property `calcite.uuid.unsigned.comparison=false`; the
adapter reads that property and, if it is off, withdraws the ordering claim from patterns that do not
confine the first hex digit to `0`–`7` and the variant nibble. The adapter will not push a sort it
cannot vouch for.

Instants and dates are covered in Chapter 15, which also explains which *query* spellings reach them.

## 14.4 Containers that hold several kinds of document

Describe each kind with `oneOf` and a discriminating `const`, or with `if`/`then`/`else`:

```json
{
  "oneOf": [
    { "properties": { "kind": { "const": "order" } } },
    { "properties": { "kind":       { "const": "shipment" },
                      "trackingId": { "type": "string", "pattern": "^[0-9a-f]{8}-…$" } } }
  ]
}
```

A fact declared inside a branch is used **only when the query has proved the branch applies** —
`WHERE JSON_VALUE(c."DOC", '$.kind') = 'shipment' AND …` — because an order may not carry
`trackingId` at all. Without that conjunct the adapter uses only what holds unconditionally. Proving
the branch takes a conjunct the service will evaluate; it composes with point reads, so a guarded
lookup by id still becomes one.

What **every** branch implies holds without any conjunct, because every document is in some branch.
That includes a fact branches state differently but compatibly: if `trackingId` is a canonical UUID in
the shipment branch and `{ "type": "null" }` in the order branch, every document holds either a canonical
UUID or null there, and a query over both kinds gets the stored form. So state an identifier's form in
the branches that carry it and `null` in the ones that do not, rather than repeating it as a nullable
property above the `oneOf`.

## 14.5 How each keyword is read

| keyword | read as |
| --- | --- |
| `type` | the value's JSON type; a union (`["string","null"]`) or OpenAPI's `nullable: true` states nothing |
| `properties` | facts about each property's value **if it is present** — never that it is present |
| `required` | presence, for children of an object that is itself present |
| `const`, `enum` | the value, or its domain (scalars only) |
| `pattern` | a stored form, for the recognised spellings (14.3) |
| `format` | nothing |
| `allOf` | every branch |
| `oneOf`, `anyOf` with a discriminating `const` | each branch, guarded by its discriminator value; and what every branch implies, for every document |
| `oneOf`, `anyOf` without one | only what every branch implies |
| `anyOf`/`oneOf` of `{"type":"null"}` and one other schema | that schema, with each fact widened to admit null |
| `if` / `then` / `else` | a guard, where the whole `if` can be read |
| `dependentRequired`, `dependentSchemas` | guarded by the triggering property's presence |
| `not` | a single property's `const` or `enum`; otherwise nothing |
| OpenAPI `discriminator` with `mapping` | which branch each discriminator value selects |
| `$ref`, `$defs`, `definitions` | followed, including inside bundles with nested `$id`s; sibling keywords beside a `$ref` are ignored |
| `additionalProperties`, `patternProperties`, `propertyNames`, `items`, `prefixItems`, `contains`, `minimum`, `maxLength`, … | nothing |

**References are resolved within the schema and nowhere else.** A `$ref` to another document is not
fetched — nothing in the model causes a network call — and fails to resolve.

**Anything not understood is ignored rather than refused.** A richer schema stays legal and yields the
facts that can be read from it. A schema that cannot be read at all leaves the container working
exactly as if it had none. A `schema` that is not a JSON object is a model error.

## 14.6 A schema is a promise, and the adapter believes it

This is the one thing in a model that can change **which rows** a query returns. Everything else the
adapter knows comes from the container's definition or the service's guarantees. A schema does not —
it is trusted the way the partition key is trusted, and a document that contradicts it is a
data-integrity problem, not something checked per row. A schema that is wrong by one character drops
rows, with no error and a plan that looks correct.

**It has to describe every document in the container, not the ones you query.** A fact stated outside
a branch is a claim about *all* documents. A schema saying

```json
{ "properties": { "trackingId": { "type": "string", "pattern": "^[0-9a-f]{8}-…$" } } }
```

says that every document in the container that has a `trackingId` stores a canonical lowercase UUID
there. If some documents store something else, a query filtering on it will silently miss them — the
adapter believed you and sent an exact comparison.

So, before adding a schema to an existing container:

- **Describe the whole mix.** Use `oneOf` with a discriminator, or `if`/`then`/`else`, for each kind of
  document the container holds.
- **If you are not sure, describe less.** A path you say nothing about is reasoned about exactly as
  before. Saying nothing costs a pushdown; saying something untrue costs rows.
- **Check what the writers actually write.** The Cosmos .NET SDK, for example, writes `DateTime`
  values with a varying number of fraction digits (Chapter 15) — a container written that way does
  not match a fixed-precision pattern, whatever the application intended.

Incompleteness of the other kind is free: unknown keywords are ignored, unrecognised patterns yield
nothing, and an unreadable schema changes nothing.

## 14.7 What a schema does not do

- It does **not** add columns or types to the table (Chapter 7). It says how values are stored.
- It does **not** validate documents on write.
- It says nothing about **pairs** of documents — whether a value is unique. That is a constraint
  (Chapter 16).
- It does **not** yet state facts about array elements (`items`), numeric ranges (`minimum`), or
  decimals stored as strings.

---

[← Previous: Writing data](13-writing.md) · [Contents](README.md) · [Next: Dates and times →](15-dates-and-times.md)
