# 15. Dates and times

Cosmos has no date or time type. JSON has strings and numbers, and a date is one or the other by
application convention: usually an ISO-8601 string such as `"2024-01-15T12:30:00Z"`, sometimes a
number of epoch seconds or milliseconds. Only `_ts` — the last-modified time, in epoch seconds — has
an encoding the service defines.

Filtering and sorting on instants is common, and this chapter says which such queries the service
answers and which are computed in process. The service answers `_ts` (15.1) and a calendar date read
with a plain cast where the container declares its shape (15.3). An instant stored as ISO-8601 text
is read in Calcite with a parse, and a comparison, a sort or a projection through a parse is computed
in process (15.2): the parse reads strings the declared pattern admits as instants their text does
not compare as.

## 15.1 `_ts`

`_ts` is a promoted `BIGINT` column, always present, always indexed. Compare it directly:

```sql
WHERE c."_ts" > 1704067200          -- modified since 2024-01-01T00:00:00Z
→ WHERE (c._ts > @p0)
```

It sorts and pages under any null collation, being non-nullable.

## 15.2 Instants stored as ISO-8601 text

Calcite reads an instant stored as text with `PARSE_DATETIME` — or, inside a model view, with
`CAST … FORMAT` (15.4). A comparison, a sort or a projection through either is computed in process,
whatever the container declares:

```json
{
  "name": "events",
  "schema": {
    "properties": {
      "at": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$" }
    }
  }
}
```

```sql
SELECT c."id"
FROM "events" AS c
WHERE PARSE_DATETIME('%Y-%m-%d''T''%H:%M:%S''Z''', JSON_VALUE(c."DOC", '$.at'))
      > TIMESTAMP '2024-02-01 00:00:00'
→ SELECT VALUE { "DOC": c, "id": c.id } FROM events c
  In process: the comparison, over every document

SELECT PARSE_DATETIME('%Y-%m-%d''T''%H:%M:%S''Z''', JSON_VALUE(c."DOC", '$.at')) AS "At"
FROM "events" AS c ORDER BY 1 FETCH NEXT 20 ROWS ONLY
→ SELECT VALUE { "$f0": (IS_PRIMITIVE(c.at) ? c.at : null) } FROM events c
  In process: the parse, the sort and the 20 rows
```

**Why: a parse carries a field past its range over.** The pattern says each field is a run of digits,
so it admits `2016-12-31T24:00:00Z` and `2016-12-31T23:60:00Z` as surely as `2017-01-01T00:00:00Z`.
Calcite's parse reads such a field as it stands and carries the excess into the next, raising nothing
over these: both are midnight on 1 January 2017 to it, `2016-11-61T00:00:00Z` is 31 December and
`2023-02-30T00:00:00Z` is 2 March. Three strings of one instant are three values to the service, so an
equality with `TIMESTAMP '2017-01-01 00:00:00'` sent as a comparison of the stored strings would keep
one of them where Calcite keeps all three, and a sort would order them apart. That holds for every
format, every stored shape and a parameter in place of the literal, so the comparison stays where
Calcite makes it, over the documents the rest of the query sends.

**The text itself compares at the service.** `JSON_VALUE` with no parse is a string, and a comparison
of it with a literal in the stored spelling, or with a parameter, is sent as it is where the schema
declares the path a string — and so is a sort on it (Chapter 9.3):

```sql
WHERE JSON_VALUE(c."DOC", '$.at') > '2024-02-01T00:00:00Z'
→ WHERE (c.at > @p0)
```

That is a comparison of text, and it answers what the text says: a writer that writes every instant in
one shape writes strings that compare as their instants do, while a stored `2024-01-31T24:00:00Z`
compares below `2024-02-01T00:00:00Z` as text although it is the same instant.

`PARSE_DATETIME` is in Calcite's BigQuery function library, so the connection needs `Fun =
"bigquery"` (or `"all"`) — Chapter 6.

### Writing the format

Write the format with BigQuery's `%` elements or PostgreSQL-style elements, and quote a literal `T`
or `Z` as `'T'` and `'Z'` (doubled again inside a SQL string, as above):

| stored shape | declared `pattern` | a format that reads it |
| --- | --- | --- |
| `2024-01-15T12:30:00Z` | `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$` | `%Y-%m-%d'T'%H:%M:%S'Z'` or `YYYY-MM-DD'T'HH24:MI:SS'Z'` |
| `2024-01-15T12:30:00.123Z` | the same with `\.[0-9]{3}` before the `Z` | `%Y-%m-%d'T'%H:%M:%S.%E3S'Z'`, or `YYYY-MM-DD'T'HH24:MI:SS.MS'Z'` (or `.FF3`) |
| `2024-01-15T12:30:00+00:00` | the same ending `\+00:00` | end the format with `'+00:00'` or bare `+00:00` |
| `2024-01-15T12:30:00` | the same with no zone | the format with no zone element |
| `2024-01-15` | `^[0-9]{4}-[0-9]{2}-[0-9]{2}$` | `%Y-%m-%d`, `YYYY-MM-DD`, or `yyyy-MM-dd` (with `PARSE_DATE`) |
| `12:30:00` | a time of day | `%H:%M:%S`, `HH24:MI:SS` |

Use `PARSE_DATETIME` for an instant; `PARSE_DATE` over a full instant throws the clock away. A calendar
date is better read with a plain cast, which pushes (15.3), than with `PARSE_DATE`, which does not.

### The format that looks right and is not

**Do not write `yyyy-MM-dd'T'HH:mm:ss'Z'`.** It looks like the Java pattern it resembles, and Calcite
accepts it without complaint, but Calcite's format model matches its elements without regard to case:
`mm` is read as a second *month* and the minute is never set. Measured, a document storing
`2024-01-02T03:04:05Z` comes back as **2024-04-02 03:00:05**. That is Calcite's answer with or without
this adapter. (For a plain date, `yyyy-MM-dd` is correct — a date has no minute to be mistaken for a
month.)

### Shapes a parse cannot reach

- **A fraction that is not exactly three digits** — including the seven-digit
  `yyyy-MM-ddTHH:mm:ss.fffffffZ` that Azure's own documentation recommends. Calcite's timestamps are
  milliseconds, and every fraction element reads its digits as milliseconds.
- **The separator-less basic format**, `20240102T030405Z`.
- **A mixture of shapes in one container** — some values with a fraction and some without, or some with
  `Z` and some with an offset. No pattern describes them, and strings of different shapes do not sort
  as their instants do.

## 15.3 Why not `CAST` or `RETURNING TIMESTAMP`?

They look like the obvious spellings, and they do not work over ISO-8601 text — not in this adapter,
and not in Calcite:

| expression | over a stored | result in Calcite |
| --- | --- | --- |
| `CAST(<s> AS TIMESTAMP)` | `2024-01-15T12:30:00Z` (any precision, any zone) | raises *Invalid DATE value* |
| `CAST(<s> AS TIMESTAMP)` | `2024-01-15 12:30:00` (space, no zone) | ✔ — the only instant it reads |
| `CAST(<s> AS DATE)` or `AS TIMESTAMP` | `2024-01-15` | ✔ — and day `00` as the month before's last day; any other field out of range raises |
| `CAST(<s> AS TIME)` | `12:30:00`, `12:30` | ✔ — and a field past its range added on: `12:60:00` is 13:00 |
| `JSON_VALUE(… RETURNING TIMESTAMP)` | any string | raises |
| `JSON_VALUE(… RETURNING TIMESTAMP)` | the number `1705321800000` | ✔ — epoch milliseconds |
| `JSON_VALUE(… RETURNING DATE)` | any string, `2024-01-15` included | raises |
| `JSON_VALUE(… RETURNING TIME)` | any string, `12:30:00` included | raises |

`RETURNING TIMESTAMP` asserts that the property already holds a timestamp — a JSON number of epoch
milliseconds — and does not parse text; `RETURNING DATE` wants a number of days and `RETURNING TIME` one
of milliseconds into the day. Because the engine cannot evaluate a cast or a `RETURNING`
clause over an ISO-8601 instant, the adapter does not push them either: pushing would return rows for
a query that, run anywhere else, raises an error. They stay in process, where they raise as they
always would.

**A calendar date read with a cast pushes**, where the container declares `^[0-9]{4}-[0-9]{2}-[0-9]{2}$`:
a comparison as `DATE` or `TIMESTAMP` with a literal or a parameter, and a sort and a page on it.

```sql
WHERE CAST(JSON_VALUE(c."DOC", '$.day') AS DATE) > DATE '2024-01-15'
→ WHERE (c.day > @p0)
```

**Day `00` is the exception, and is not handled yet.** The cast reads `2024-03-00` as 29 February, and
the pattern admits it. A comparison sent to the service compares the stored strings, so against a
month's last day a stored `2024-03-00` is missed by `=` and `<=` and kept by `<>` and `>`, where Calcite
reads it as that day; `<` and `>=` agree. And a sort on the cast before another key orders `2024-02-29`
and `2024-03-00` apart, where Calcite ties them for the next key (Chapter 23).

**A time of day read with a cast to `TIME` is computed in process.** The cast adds a field past its
range on, so `12:60:00` is 13:00 and `12:30:99` is 12:31:39, though its text sorts before `12:31:00`,
and the stored strings neither compare nor sort that way. **A date, a time or an instant read with `RETURNING` from a stored
string raises**, in Calcite and so here: the accessor is computed in process, whatever the container
declares, rather than sent as the path for the adapter to read the string back.

## 15.4 Inside a model view: `CAST … FORMAT`

Calcite analyses a model view with its default configuration, without the connection's function
libraries, so `PARSE_DATETIME` does not validate inside one. The standard `CAST … FORMAT` does, and it
reads the text with the same formats:

```sql
CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''')
CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(3) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z''')
```

It is a parse, and carries a field past its range over as `PARSE_DATETIME` does (15.2), so a
comparison, a sort or a page over a view column written this way is computed in process, over the
documents the rest of the query sends:

```json
{
  "name": "EVENTS",
  "type": "view",
  "path": [ "COSMOS" ],
  "sql": "SELECT e.\"id\" AS \"Id\", CAST(JSON_VALUE(e.\"DOC\", '$.at') AS TIMESTAMP FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''') AS \"At\" FROM \"events\" AS e"
}
```

## 15.5 Instants stored as numbers

A property holding epoch milliseconds is read with `JSON_VALUE(… RETURNING TIMESTAMP)` and compares
exactly at the service. Epoch seconds is a number like any other: read it with `RETURNING INTEGER` and
compare numbers. Not `RETURNING BIGINT`, which Calcite answers only for a number past an `INTEGER`'s
range and fails on today's epoch seconds wherever it runs in process (Chapter 7).

## 15.6 Why your container may not have a fixed shape

The Cosmos .NET SDK serialises a `DateTime` with Newtonsoft.Json's ISO writer, which writes the
*minimum* number of fraction digits: `DateTime.UtcNow` produces six or seven digits depending on the
value, and a value with no fractional part — `DateTime.Today`, a parsed `"2024-01-15"` — has none at
all. A container written by an ordinary .NET application therefore mixes shapes, and no pattern
describes it. Such a container is correctly left alone: its strings do not sort as its instants do.

To compare and sort the stored text at the service, write instants in one fixed shape — for example
with a converter that always writes `yyyy-MM-ddTHH:mm:ssZ` or `yyyy-MM-ddTHH:mm:ss.fffZ` — and declare
that pattern: such a writer's strings compare as their instants do (15.2). A calendar date written in
one shape pushes through a plain cast as well (15.3).

## 15.7 Date functions

Calcite's temporal functions — `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`, `FLOOR(… TO …)`,
`CURRENT_TIMESTAMP` — are evaluated in process. Cosmos's own date functions work on ISO strings and
map onto them only conditionally on the stored shape; that mapping is not built yet.

---

[← Previous: Describing documents with JSON Schema](14-json-schema.md) · [Contents](README.md) · [Next: Declaring uniqueness →](16-constraints.md)
