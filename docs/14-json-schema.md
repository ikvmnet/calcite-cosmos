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

A parameter in place of the literal works the same way, which is the shape Entity Framework and
OData send for every key filter. `CAST(…) = ?` and `CAST(…) IN (?, ?, ?)` become `c.trackingId = @p0`,
and the adapter writes the bound value in the declared spelling when the statement runs. The value can
be a `Guid`, a `java.util.UUID`, Calcite's own `UuidValue`, or text that parses as a UUID. An ordering
against a parameter pushes only under the unsigned comparison (14.3). A value supplied at run time
can't be checked against a confined pattern the way a literal is.

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
same function Calcite's own cast uses — so the column arrives in .NET as a `Guid`. A form declared inside
one kind's branch (14.5) is used the same way where the `WHERE` keeps that kind: `SELECT CAST(… AS UUID)
… WHERE kind = 'shipment'` sends the stored text, and without the conjunct the cast is made in memory.

Sorting *by* such a column (`ORDER BY 1` above) additionally needs the stored strings to sort the way
the values do, which the form says too.

### An exact comparison where there would be a weakened one

A comparison over `JSON_VALUE` is normally sent weakened — `NOT IS_STRING(c.carrier) OR c.carrier >=
@p0` — and rechecked, because the service orders values across JSON types where SQL compares their
text (Chapter 8). Where the schema says the path holds a string, there is no second type to disagree
over: the comparison is sent exactly, nothing is rechecked, and a `FETCH` can travel with it. A
`type` declaration earns its keep on its own. It also lets a string function over the path push —
`UPPER(JSON_VALUE(c."DOC", '$.carrier'))`, a concatenation, a length, a `COALESCE` — in a filter and in
a projection, which over an undeclared path run in process, the accessor reading a stored number as
text.

The same declaration is what lets the text column be **sorted, grouped or joined on at the service**.
Each of those acts on the stored value in the column's place — `ORDER BY c.carrier`, `GROUP BY` the
path, a lookup restricting `c.carrier IN (…)` — and the stored value is the column's text only where
nothing but a string can be there: a stored `30` and `"30"` are one value as text and two at the
service. Without the declaration the sort and the grouping run in process and the join reads the
container whole (Chapters 9, 10 and 12).

A sort on a typed column needs its declaration as well. `JSON_VALUE(… RETURNING INTEGER)` is null where
the path holds an object, an array or a string, and the service sorts those after every number rather
than with the nulls; `"type": "integer"` or `"number"` (or `"boolean"` for `RETURNING BOOLEAN`), alone
or with `"null"`, says nothing else is there, and the sort is sent (Chapter 9.3).

A declaration inside one kind's branch (14.5) does the same once another conjunct proves the branch:
`kind = 'shipment' AND carrier = 'DHL'` sends the second comparison exactly where the shipment branch
declares `carrier` a string. Each conjunct is sent on what the conjuncts sent beside it prove, and never
on what it proves itself — `carrier = '30'` alone proves only that the path renders as `30`, which a
stored number does too.

Similarly, a path declared `required` needs no `IS_DEFINED` guard, which removes documents the service
would otherwise send only for the recheck to discard. So does a path the query itself compares: a
comparison keeps only documents that have the path, so `c.carrier = 'x'` beside a weakened comparison on
`$.carrier` makes the second one's guard redundant. And a guard the service already guarantees is never
sent: every document has an `id`, an `_etag` and a `_ts`, so what a comparison over one of them implies
of its presence restricts nothing.

### A conjunct the schema already guarantees is not sent

A conjunct that every document the schema admits satisfies — given the other conjuncts — decides
nothing, and is dropped from what is sent:

```sql
-- "required": ["status"], "properties": { "status": { "enum": ["active", "archived"] } }
SELECT c."id" FROM "shipments" AS c
WHERE JSON_VALUE(c."DOC", '$.status') IN ('active', 'archived')
  AND JSON_VALUE(c."DOC", '$.carrier') = 'DHL'
```

```
without a schema   WHERE (c.status = @p0 OR c.status = @p1) AND c.carrier = @p2
with one           WHERE c.carrier = @p0
```

The same holds of `<>` a value outside the enum, `IS NOT NULL` of a required path that is never null, a
range through `CAST(… AS DOUBLE)` that the declared `minimum` and `maximum` already satisfy, and a
disjunction one of whose branches another conjunct asserts: `kind = 'shipment' AND (kind = 'shipment'
OR …)` sends the first conjunct alone. Where every conjunct goes, the statement has no `WHERE` at all.
`required` matters: without it a document may lack the path, and a comparison over an absent path keeps
no row, so the conjunct is not guaranteed. A range through `CAST(… AS INTEGER)` is not dropped either:
Calcite's integer cast of the text raises on `30.0` and on numbers past an `INTEGER`, which an
`"integer"` declaration admits, and dropping the conjunct would answer where the query fails.

### A predicate no document can satisfy

A value the schema rules out keeps no row, and the plan says so: the query is the empty result, and
nothing is sent.

```sql
-- "status": { "enum": ["active", "archived"] }
SELECT c."id" FROM "shipments" AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted'
```

```
without a schema   WHERE c.status = @p0, which the service evaluates to find nothing
with one           no request: the plan is an empty Values
```

That takes a rule in the host for an empty `Values`, which Calcite's own and the CLR convention's
rule sets both have. A planner without one sends `WHERE @p0` bound to `false`: one round trip that
reads no document.

The same holds for an `anyOf` of enums, whose values are the members of them all, and for a `const`.

A range does the same against a bound or an enum, read through a numeric cast:

```sql
-- "n": { "type": "integer", "maximum": 100 }
SELECT c."id" FROM "shipments" AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) > 500

-- "n": { "enum": [1, 2, 3] }
SELECT c."id" FROM "shipments" AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) = 30
```

Both keep nothing. A bound binds only numbers, as JSON Schema's `minimum` and `maximum` do, and the cast
converts a stored string too — `> 500` keeps a document storing `"600"`. So without `"type": "integer"`
the first query is left as it is: a string there is not ruled out.

**What a schema rules out is a value, not a document.** A `status` declared both `"type": "integer"`
and a string `enum` can hold nothing, so the schema says documents have no `status`. A query that does
not mention `status` is unaffected, and one asking for a `status` keeps nothing.

## 14.3 Stored forms: what a `pattern` proves

A `pattern` is what pins the spelling of a string, and the adapter recognises a fixed family of
stored forms rather than interpreting arbitrary regular expressions. A pattern that is not one of
them yields no stored form, though what it says of the characters a string holds may still be read
(14.4). Each recognised form says two independent things: whether comparing the stored strings for
**equality** answers what comparing the values does, and whether their **order** does.

| declared `pattern` | equality | order |
| --- | --- | --- |
| `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$` — canonical lowercase UUID | ✔ | ✔ |
| the same with a version digit or the variant nibble `[89ab]` pinned | ✔ | ✔ |
| the same in uppercase throughout | ✔ | ✔ |
| the same braced (`\{…\}`) or without hyphens | ✔ | ✔ |
| `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$` — ISO-8601 UTC, whole seconds | ✔ | ✔ |
| the same with a fixed number of fraction digits, `\.[0-9]{n}`, and/or `\+00:00` or no zone in place of `Z` | ✔ | ✔ |
| `^[0-9]{4}-[0-9]{2}-[0-9]{2}$` — a calendar date | ✔ | ✔ |
| `^[0-9]{5}$` — digits at a fixed width | ✔ | ✔ |
| `^(0\|[1-9][0-9]*)$`, `^[1-9][0-9]*$` — digits with no leading zero | ✔ | ✘ |
| an `enum` of strings | ✔ | ✘ |
| `^[0-9]+$`, `^\d+$`, anything signed | ✘ | ✘ |
| any other pattern | ✘ | ✘ |

**A form is recognised by the strings a pattern admits, not by how it is spelled.** The pattern is
parsed, by the same reader that reads its characters (14.4), and it is a form where the strings it
admits are the form's. So the ways of writing one language are one form: `\d` or `[0-9]` or
`[0123456789]`; a run counted or spelled out, `\d{4}` or `\d\d\d\d` or `\d{2}\d{2}`; a separator
written `-`, `[-]` or `\x2D`; a group around any part; the alternatives of `^(0|[1-9][0-9]*)$` in
either order. Whitespace is a character the pattern matches, as ECMA-262 reads it: `^[0-9]{5} $` is
not the fixed-width form, nor a UUID pattern with a stray space a UUID.

The date, time and number patterns in the table must admit exactly the form's strings: a month
narrowed to `[01][0-9]` is no form. A UUID pattern is read as a shape — 32 hex slots, hyphens in
fixed places, anchored at both ends — so any narrower confinement of the slots (a pinned prefix,
`[1-5]` for the version, `[8-9a-b]` for the variant) is recognised too. A pattern that mixes cases,
or a character range like `[8-f]` that spans non-hex characters, is refused, because it admits two
spellings of one value.

**Escape the braces.** Validators differ on whether a pattern is read with ECMA-262's `u` flag, and
a pattern only one reading accepts is no form: the other reading rejects it, so a validator using
that reading cannot have checked a document. `^{…}$` with bare braces, and `\-` for a hyphen, are
literals without the flag and errors with it. Write `\{…\}` and `-`.

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

## 14.4 Characters: what a `pattern` proves about order

The service orders strings by code point and Calcite by UTF-16 code unit. The two agree everywhere
except where a character above U+FFFF — an emoji, say, which UTF-16 writes as two surrogates — meets
one between U+E000 and U+FFFF: private use, the CJK compatibility block, the fullwidth forms, U+FFFD.
The service puts the second first and Calcite the first. So where nothing says what characters a
string holds, a sort over it, its `MIN` and `MAX`, and a comparison of it with a parameter or another
path run in process — `id` included, since Azure allows every character in an `id` but `/` and `\`
(Chapter 9.3).

**A `pattern` that rules those characters out is the proof.** Any pattern the adapter can read
counts, not only the stored forms of 14.3, and the proof is made when the plan is: nothing is
checked when the rows come back. It is not the only proof — an `enum`, a `const`, a stored form and a
`WHERE` listing the values each prove the same (Chapter 9.3) — but it is the one that covers strings
no one listed. Declare the type beside it, since a pattern constrains only a string and a text key
sorts at the service only over a declared string (Chapter 9.3):

```json
"properties": {
  "id":    { "type": "string", "pattern": "^[A-Za-z0-9_-]+$" },
  "ref":   { "type": "string", "pattern": "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" },
  "title": { "type": "string", "pattern": "^[\\u0000-\\uD7FF]*$" }
}
```

- **An identifier on `id`.** `ORDER BY c."id"`, its pages, `MAX(c."id")` and the keyset seek
  Chapter 9.5 recommends all reach the service proved, with nothing checked as the rows arrive:

  ```sql
  SELECT c."id" FROM "products" AS c WHERE c."id" > ? ORDER BY c."id" FETCH NEXT 50 ROWS ONLY
  → SELECT VALUE { "id": c.id } FROM products c WHERE (c.id > @p0) ORDER BY c.id ASC OFFSET 0 LIMIT 50
  ```

- **A UUID in either case.** A pattern admitting both cases is no stored form — one value has two
  spellings (14.3) — so it gives no UUID comparison; but its characters are hex digits and hyphens,
  so a sort on the text pushes. A canonical UUID's stored form proves its characters already.
- **Text with no emoji and nothing from U+E000**: every character below the surrogates, which is
  every letter of the basic plane but the compatibility and fullwidth forms. In JSON the backslashes
  are doubled, as above; the pattern itself is `^[\u0000-\uD7FF]*$`.

What each says:

| the strings may hold | sort, `MIN`, `MAX` | compared with a literal | with a parameter | with a path of any character | with another declared path |
| --- | --- | --- | --- | --- | --- |
| nothing at or above U+D800 | ✔ | ✔ | ✔ | ✔ | ✔ |
| U+E000–U+FFFF but no surrogate: `^[\u0000-\uD7FF\uE000-\uFFFF]*$` | ✔ | ✔ | checked | ✘ | where the other holds no surrogate either |
| anything: no pattern, or one not read | checked | ✔ | checked | ✘ | where the other holds nothing at or above U+D800 |

**✔** is proved when the plan is made, and the statement is read as it comes. **Checked** is sent too, and
completed as the rows arrive: the reader checks each key, a page's edges, an extreme or a parameter for a
character at or above U+D800, and reads the statement again where one is (Chapter 9.3) — a scan of each key
on ordinary text, and a second request only where such a character lands where it matters (Chapter 19.8).
A grouped `MIN` or `MAX` is not checked, and needs the ✔. **✘** stays in process: the rows a comparison of
two paths drops never arrive to be checked.

A literal compares exactly in every row because, under the default charset, it holds nothing at or
above U+0100 (Chapter 8.3).

**How a pattern is read.** As ECMA-262, the dialect JSON Schema names — but JSON Schema does not say
whether the `u` flag applies, and each validator chooses. Without it a pattern is read and matched one
UTF-16 unit at a time; with it, one code point at a time; and the two can disagree:
`[^\uD800-\uDFFF]` excludes the surrogates read by unit, and admits every emoji read by code point.
So every pattern is read both ways, and a character is ruled out only where both readings rule it out.
What is read:

- anchored at both ends: every alternative begins with `^` and ends with `$`, or sits in a group
  that does, as in `^(?:a|b)$` or `(?:^a$)|(?:^b$)`. A `pattern` searches, so `[a-z]+` matches any
  string with a letter in it;
- literals and escaped characters, `\uXXXX`, `\xHH`, `\t` and the other control escapes, `\d` and `\w`
  (ASCII in ECMA-262), character classes, ranges, groups, alternation, and any quantifier;
- a range by every character it spans: `[\uD000-\uE100]` holds surrogates and U+E000 though neither
  end does;
- a negated class as its complement under each reading. Read by code point, `[^…]` admits every
  character above U+FFFF it does not name, so a negated class never rules out the surrogates.

**What is refused, and why.** A refused pattern proves nothing, and the path is reasoned about as if
it had none:

- an alternative not anchored at both ends, which matches inside a string of anything;
- `.`, `\s` (it matches U+FEFF and the line separators too), `\S`, `\D` and `\W`, none of which bounds
  the characters;
- `\p{…}`, `\u{…}`, named groups, lookarounds, inline flags and back-references, each read differently,
  or not at all, without the `u` flag;
- anything only one reading accepts, such as `\a`, a lone `]` or `{`, or a range that ends in `\d`;
- and a pattern that admits a surrogate says nothing about U+E000–U+FFFF either: Azure stores a lone
  surrogate written as a JSON escape as U+FFFD.

**`\d` and `\w` are ECMA-262's.** A validator in another dialect — .NET's default `Regex`, Python's
`re` — reads them as every digit or word character in Unicode, fullwidth and supplementary ones among
them. If your writers validate that way, spell the classes out: `[0-9]`, `[A-Za-z0-9_]`.

**`format` still proves nothing**, `"format": "uuid"` included (14.3).

A pattern is a promise like the rest of a schema (14.7). A document holding an emoji where the
pattern says none can is given a page from the wrong place, a wrong `MIN` or `MAX`, and comparisons
that keep the wrong rows.

## 14.5 Containers that hold several kinds of document

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
lookup by id still becomes one. And what the proved branch declares is used by the rest of the
statement too — the select list, `ORDER BY` and `GROUP BY` read the rows the `WHERE` kept — and by
routing, where the filter and the schema together leave the partition key one value (Chapter 8.5).

A query can also prove the branch in other ways:

- **Several kinds at once.** `kind IN ('shipment', 'return')` uses whatever both kinds declare. Each
  kind is reasoned about on its own, and what all of them say is kept: if both declare `trackingId` a
  canonical UUID, the UUID comparison beside it reaches the service. If one of them says nothing
  about `trackingId`, nothing is concluded.
- **By ruling the other kinds out.** In a container of orders and shipments, `kind <> 'order'` leaves
  only shipments, and the shipment branch's facts apply.
- **One kind per branch of a disjunction.** `(kind = 'order' AND …) OR (kind = 'shipment' AND …)` keeps
  each row by one branch or the other, and whatever both kinds declare holds of every row it keeps, as
  it does of `kind IN ('order', 'shipment')`.

What **every** branch implies holds without any conjunct, because every document is in some branch.
That includes a fact branches state differently but compatibly: if `trackingId` is a canonical UUID in
the shipment branch and `{ "type": "null" }` in the order branch, every document holds either a canonical
UUID or null there, and a query over both kinds gets the stored form. So state an identifier's form in
the branches that carry it and `null` in the ones that do not, rather than repeating it as a nullable
property above the `oneOf`.

## 14.6 How each keyword is read

| keyword | read as |
| --- | --- |
| `type` | the value's JSON type, where an `integer` is also a `number`. `["string","null"]` or OpenAPI's `nullable: true` reads as that type or null, and `["integer","number"]` as a number. Any other union states nothing |
| `properties` | facts about each property's value **if it is present** — never that it is present |
| `required` | presence, for children of an object that is itself present |
| `const`, `enum` | the value, or its domain (scalars only) |
| `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum` | a bound on the value **if it is a number**; the exclusive forms as numbers (2019-09 on) or as flags beside `minimum` and `maximum` (Draft 4, OpenAPI 3.0) |
| `pattern` | a stored form, where the strings it admits are a recognised form's (14.3); and, beside any type, the characters a string may hold (14.4). Both are read under both of its readings |
| `format` | nothing |
| `allOf` | every branch |
| `oneOf`, `anyOf` with a discriminating `const` | each branch, for documents that have the discriminator and its value; and what every branch implies, for every document |
| `oneOf`, `anyOf` without one | only what every branch implies; an `anyOf` of enums, the union of their values |
| `anyOf`/`oneOf` of `{"type":"null"}` and one other schema | that schema, with each fact widened to admit null |
| `if` / `then` / `else` | a guard, where the whole `if` can be read. `then` applies where the `if` holds, which includes documents without the property it names; `else` applies where the property is present and holds another value |
| `dependentRequired`, `dependentSchemas` | guarded by the triggering property's presence |
| `not` | a single property's `const` or `enum`; otherwise nothing |
| OpenAPI `discriminator` with `mapping` | which branch each discriminator value selects |
| `$ref`, `$defs`, `definitions` | followed by JSON Pointer or by anchor (`$anchor`, or `"$id": "#name"` before 2019-09), including inside bundles with nested `$id`s; sibling keywords beside a `$ref` are ignored |
| `additionalProperties`, `patternProperties`, `propertyNames`, `items`, `prefixItems`, `contains`, `minLength`, `maxLength`, `multipleOf`, … | nothing |

**References are resolved within the schema and nowhere else.** A `$ref` to another document is not
fetched — nothing in the model causes a network call — and fails to resolve.

**Anything not understood is ignored rather than refused.** A richer schema stays legal and yields the
facts that can be read from it. A schema that cannot be read at all leaves the container working
exactly as if it had none. A `schema` that is not a JSON object is a model error.

## 14.7 A schema is a promise, and the adapter believes it

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

## 14.8 What a schema does not do

- It does **not** add columns or types to the table (Chapter 7). It says how values are stored.
- It does **not** validate documents on write.
- It says nothing about **pairs** of documents — whether a value is unique. That is a constraint
  (Chapter 16).
- It does **not** yet state facts about array elements (`items`), string lengths (`maxLength`), or
  decimals stored as strings.

---

[← Previous: Writing data](13-writing.md) · [Contents](README.md) · [Next: Dates and times →](15-dates-and-times.md)
