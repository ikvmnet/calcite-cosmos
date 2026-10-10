# 15. Dates and times

Cosmos has no date or time type. JSON has strings and numbers, and a date is one or the other by
application convention: usually an ISO-8601 string such as `"2024-01-15T12:30:00Z"`, sometimes a
number of epoch seconds or milliseconds. Only `_ts` — the last-modified time, in epoch seconds — has
an encoding the service defines.

Filtering and sorting on instants is common, and this chapter explains how to write such queries so
the service answers them. Two things have to be true for a temporal comparison or sort to push down:

1. **The container declares the stored shape** with a `pattern` (Chapter 14), so the adapter knows the
   strings compare and sort the way the instants do.
2. **The query reads the value with a spelling Calcite itself can evaluate** over that shape, so the
   pushed statement and the in-process plan would give the same rows.

## 15.1 `_ts`

`_ts` is a promoted `BIGINT` column, always present, always indexed. Compare it directly:

```sql
WHERE c."_ts" > 1704067200          -- modified since 2024-01-01T00:00:00Z
→ WHERE (c._ts > @p0)
```

It sorts and pages under any null collation, being non-nullable.

## 15.2 Instants stored as ISO-8601 text

Declare the shape the container stores, then read it with `PARSE_DATETIME` — or, inside a model view,
with `CAST … FORMAT` (15.4).

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
→ WHERE c.at > '2024-02-01T00:00:00Z'

SELECT PARSE_DATETIME('%Y-%m-%d''T''%H:%M:%S''Z''', JSON_VALUE(c."DOC", '$.at')) AS "At"
FROM "events" AS c ORDER BY 1 FETCH NEXT 20 ROWS ONLY
→ ORDER BY c.at at the service, 20 documents returned
```

The literal is written in the container's own spelling, the comparison and the sort are made on the
stored strings, and the projected value is converted back to a `TIMESTAMP` as it is read.

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

Use `PARSE_DATETIME` for an instant and `PARSE_DATE` for a calendar date. A parse is pushed only where
its result type holds everything the shape carries — `PARSE_DATE` over a full instant throws the clock
away, so it is not.

### The format that looks right and is not

**Do not write `yyyy-MM-dd'T'HH:mm:ss'Z'`.** It looks like the Java pattern it resembles, and Calcite
accepts it without complaint, but Calcite's format model matches its elements without regard to case:
`mm` is read as a second *month* and the minute is never set. Measured, a document storing
`2024-01-02T03:04:05Z` comes back as **2024-04-02 03:00:05**. That is Calcite's answer with or without
this adapter, and the adapter declines to push it rather than turn one wrong answer into another. (For
a plain date, `yyyy-MM-dd` is correct — a date has no minute to be mistaken for a month.)

### Shapes a parse cannot reach

- **A fraction that is not exactly three digits** — including the seven-digit
  `yyyy-MM-ddTHH:mm:ss.fffffffZ` that Azure's own documentation recommends. Calcite's timestamps are
  milliseconds, and every fraction element reads its digits as milliseconds.
- **The separator-less basic format**, `20240102T030405Z`.
- **A mixture of shapes in one container** — some values with a fraction and some without, or some with
  `Z` and some with an offset. No pattern describes them, and strings of different shapes do not sort
  as their instants do.

`PARSE_TIMESTAMP` returns a `TIMESTAMP WITH LOCAL TIME ZONE` and is not pushed; use `PARSE_DATETIME`.
`TO_DATE` and `TO_TIMESTAMP` are not pushed either.

## 15.3 Why not `CAST` or `RETURNING TIMESTAMP`?

They look like the obvious spellings, and they do not work over ISO-8601 text — not in this adapter,
and not in Calcite:

| expression | over a stored | result in Calcite |
| --- | --- | --- |
| `CAST(<s> AS TIMESTAMP)` | `2024-01-15T12:30:00Z` (any precision, any zone) | raises *Invalid DATE value* |
| `CAST(<s> AS TIMESTAMP)` | `2024-01-15 12:30:00` (space, no zone) | ✔ — the only instant it reads |
| `CAST(<s> AS DATE)` or `AS TIMESTAMP` | `2024-01-15` | ✔ |
| `CAST(<s> AS TIME)` | `12:30:00`, `12:30` | ✔ |
| `JSON_VALUE(… RETURNING TIMESTAMP)` | any string | raises |
| `JSON_VALUE(… RETURNING TIMESTAMP)` | the number `1705321800000` | ✔ — epoch milliseconds |

`RETURNING TIMESTAMP` asserts that the property already holds a timestamp — a JSON number of epoch
milliseconds — and does not parse text. Because the engine cannot evaluate a cast or a `RETURNING`
clause over an ISO-8601 instant, the adapter does not push them either: pushing would return rows for
a query that, run anywhere else, raises an error. They stay in process, where they raise as they
always would.

What a cast *can* do still pushes, where the shape is declared: a calendar date read as a `DATE` or a
`TIMESTAMP`, and a whole-second or whole-minute time of day read as a `TIME`.

## 15.4 Inside a model view: `CAST … FORMAT`

Calcite analyses a model view with its default configuration, without the connection's function
libraries, so `PARSE_DATETIME` does not validate inside one. The standard `CAST … FORMAT` does, and the
adapter reads it as the same parse, with the same formats:

```sql
CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''')
CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(3) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z''')
```

(The container's shape is still declared by a `pattern`; the format is how the query reads it.)

**Give the type the precision the format reads.** A bare `TIMESTAMP` is `TIMESTAMP(0)` and holds no
fraction, so a millisecond format cast into one is not pushed.

A view column written this way filters, sorts and pages at the service over a declared container:

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
exactly at the service. Epoch seconds is a number like any other: read it with `RETURNING BIGINT` and
compare numbers.

## 15.6 Why your container may not have a fixed shape

The Cosmos .NET SDK serialises a `DateTime` with Newtonsoft.Json's ISO writer, which writes the
*minimum* number of fraction digits: `DateTime.UtcNow` produces six or seven digits depending on the
value, and a value with no fractional part — `DateTime.Today`, a parsed `"2024-01-15"` — has none at
all. A container written by an ordinary .NET application therefore mixes shapes, and no pattern
describes it. Such a container is correctly left alone: its strings do not sort as its instants do.

To make instants pushable, write them in one fixed shape — for example with a converter that always
writes `yyyy-MM-ddTHH:mm:ssZ` or `yyyy-MM-ddTHH:mm:ss.fffZ` — and declare that pattern.

## 15.7 Date functions

Calcite's temporal functions — `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`, `FLOOR(… TO …)`,
`CURRENT_TIMESTAMP` — are evaluated in process. Cosmos's own date functions work on ISO strings and
map onto them only conditionally on the stored shape; that mapping is not built yet.

---

[← Previous: Describing documents with JSON Schema](14-json-schema.md) · [Contents](README.md) · [Next: Declaring uniqueness →](16-constraints.md)
