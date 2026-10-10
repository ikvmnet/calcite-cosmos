# Appendix B. Function and operator reference

What each SQL operator and function becomes in Cosmos SQL. A function that pushes is evaluated by the
service; one that does not is evaluated by Calcite over the rows that come back, and the rest of the
query around it can still push (Chapter 8).

Functions marked *library* are not in Calcite's standard operator table; enable a library that has
them with the connection's `Fun` setting (Chapter 6), e.g. `Fun = "all"`.

## B.1 Operators

| SQL | Cosmos SQL | notes |
| --- | --- | --- |
| `=` `<>` `<` `<=` `>` `>=` | `=` `!=` `<` `<=` `>` `>=` | over `JSON_VALUE`, see Chapter 8 |
| `AND` `OR` `NOT` | `AND` `OR` `NOT` | with null guards |
| `+` `-` `*` `/` `%`, unary `-` | the same | |
| `\|\|` | `CONCAT` | |
| `IS NULL` / `IS NOT NULL` | `NOT IS_DEFINED(x) OR IS_NULL(x)` / `IS_DEFINED(x) AND NOT IS_NULL(x)` | |
| `IN (…)`, `NOT IN (…)`, `BETWEEN` | comparisons | same cost as the native forms |
| `LIKE` | `STARTSWITH` for a prefix; Cosmos `LIKE` otherwise | no brackets, no `ESCAPE`, literal pattern only |
| `UPPER(x) LIKE` / `LOWER(x) LIKE` | `CONTAINS`, `STARTSWITH`, `ENDSWITH` with the case-insensitive flag | ASCII text, one leading and/or trailing `%` |
| `CASE` | nested `? :` | |
| `COALESCE`, `NULLIF`, `NVL`, `NVL2`, `IFNULL`, `IF` | via `CASE` | Calcite expands them first |
| `CAST(x AS VARCHAR)` | dropped in a comparison over a document value; read as text in a projection | unsized `VARCHAR` only |
| `CAST(x AS <number>)` | a bound plus recheck in a filter; in process in a projection | Chapter 8 |
| `CAST(x AS UUID)`, parse to `TIMESTAMP`/`DATE` | the path, where the container declares the stored form | Chapters 14, 15 |
| `arr[n]` | `arr[n-1]` | SQL is one-based |
| `x MEMBER OF arr` | `ARRAY_CONTAINS(arr, x)` | |
| `IS TRUE`, `IS FALSE`, `IS NOT TRUE`, `IS NOT FALSE` | `c.x = true`, … | over a boolean cast where the schema declares a boolean; in process otherwise |
| `IS DISTINCT FROM` | — | in process |
| `SIMILAR TO`, `REGEXP_LIKE` | — | in process; use `REGEXMATCH` for Cosmos regular expressions |

## B.2 String functions

| SQL | Cosmos | notes |
| --- | --- | --- |
| `UPPER`, `LOWER` | `UPPER`, `LOWER` | |
| `CHAR_LENGTH`, `CHARACTER_LENGTH`, `LENGTH` | `LENGTH` | |
| `SUBSTRING(s FROM i FOR n)` | `SUBSTRING(s, i - 1, n)` | the form without a length stays in process |
| `POSITION(a IN b)` | `(INDEX_OF(b, a) + 1)` | SQL positions are one-based |
| `TRIM`, `LTRIM`, `RTRIM` (spaces) | `TRIM`, `LTRIM`, `RTRIM` | trimming other characters stays in process |
| `REPLACE` | `REPLACE` | |
| `CONCAT` | `CONCAT` | |
| `LEFT`, `RIGHT`, `REVERSE` (*library*) | `LEFT`, `RIGHT`, `REVERSE` | |
| `REPEAT` (*library*) | `REPLICATE` | |
| `INSTR`, `STRPOS`, `SUBSTR`, `LEN` (*library*) | via the standard forms | |
| `REGEXMATCH(s, pattern[, modifiers])` | `REGEXMATCH` | Cosmos's own; PCRE with documented exceptions |
| `ToString`, `StringToNumber`, `StringToBoolean`, `StringToArray`, `StringToObject`, `ObjectToArray` | the same | Cosmos's own; no in-process body |

## B.3 Numeric functions

| SQL | Cosmos | notes |
| --- | --- | --- |
| `ABS`, `SIGN`, `SQRT`, `EXP`, `POWER`, `LOG10` | the same | `SQRT` of a negative fails the whole statement |
| `LN` | `LOG` | |
| `ROUND(x)` | `ROUND` | the two-argument form stays in process |
| `TRUNCATE(x)` | `TRUNC` | the two-argument form stays in process |
| `FLOOR(x)`, `CEIL(x)` | `FLOOR`, `CEILING` | numeric forms; `FLOOR(<timestamp> TO …)` stays in process |
| `SIN`, `COS`, `TAN`, `COT`, `ASIN`, `ACOS`, `ATAN` | the same | `ASIN`/`ACOS` out of domain fail the whole statement |
| `ATAN2` | `ATN2` | |
| `DEGREES`, `RADIANS`, `PI()` | the same | |
| `GREATEST`, `LEAST` (*library*) | via `CASE` | |

## B.4 Array functions

| SQL | Cosmos | notes |
| --- | --- | --- |
| `CARDINALITY(arr)` | `ARRAY_LENGTH` | not over a `MAP` |
| `ARRAY_CONCAT(a, b, …)` | `ARRAY_CONCAT` | not against an array literal |
| `ARRAY_INTERSECT(a, b)` | `SETINTERSECT` | not against an array literal |
| `ARRAY_UNION(a, b)` | `SETUNION` | not against an array literal |
| `ARRAY_SLICE(a, start, n)` | `ARRAY_SLICE(a, start - 1, n)` | |
| `UNNEST(arr)` | `JOIN t IN arr` | Chapter 11 |

## B.5 Type tests (Cosmos functions)

`IS_DEFINED`, `IS_NULL`, `IS_STRING`, `IS_NUMBER`, `IS_BOOL`, `IS_ARRAY`, `IS_OBJECT`, `IS_PRIMITIVE`.

Each takes any expression and returns a non-null boolean. `IS_DEFINED` is the only way to tell an absent
property from one holding JSON `null`. These also have in-process implementations that answer as the
service does, so a predicate using one can be split and rechecked.

## B.6 Full text and vector (Cosmos functions)

`FULLTEXTCONTAINS`, `FULLTEXTCONTAINSALL`, `FULLTEXTCONTAINSANY` (in `WHERE`), `FULLTEXTSCORE`, `RRF`
(in `ORDER BY` only), `VECTORDISTANCE`, and the shared `CLR_FT_*` vocabulary. Chapter 17.

## B.7 Geography

`CLR_ST_GEOG_DISTANCE`, `CLR_ST_GEOG_WITHIN`, `CLR_ST_GEOG_INTERSECTS`, `CLR_ST_GEOG_ISVALID`,
`CLR_ST_GEOG_DWITHIN`, `CLR_ST_GEOG_GEOMETRYTYPE`, `CLR_ST_GEOG_ASGEOJSON`,
`CLR_ST_GEOG_GEOMFROMGEOJSON`. Chapter 18. Calcite's planar `ST_*` functions are never pushed.

## B.8 Aggregates

| SQL | Cosmos | notes |
| --- | --- | --- |
| `COUNT(*)` | `COUNT(1)` | always |
| `SUM`, `MIN`, `MAX`, `AVG` | the same | non-nullable input only |
| `COUNT(DISTINCT x)` | `GROUP BY x`, counted in process | |
| others | — | in process |

Chapter 10.

## B.9 No Cosmos counterpart

These are always evaluated in process. Not exhaustive; it lists the families people reach for.

- **Strings:** `LPAD`, `RPAD`, `INITCAP`, `ASCII`, `CHR`, `TRANSLATE`, `SOUNDEX`, `LEVENSHTEIN`,
  `SPLIT_PART`, `FORMAT_NUMBER`, `TO_CHAR`, `REGEXP_LIKE`, `REGEXP_REPLACE`, `REGEXP_EXTRACT`.
- **Hashing and encoding:** `MD5`, `SHA1`, `SHA256`, `TO_BASE64`, `FROM_BASE64`, `TO_HEX`.
- **Math:** `CBRT`, `LOG2`, `HYPOT`, `FACTORIAL`, the hyperbolic and degree-based trigonometric
  functions, `RAND`.
- **Temporal:** `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`, `DATE_TRUNC`, `CURRENT_TIMESTAMP`, and the
  rest.
- **Arrays and maps:** `ARRAY_POSITION`, `ARRAY_DISTINCT`, `ARRAY_REVERSE`, `ARRAY_APPEND`,
  `SORT_ARRAY`, the `MAP_*` family.
- **JSON:** `JSON_EXISTS`, `JSON_TYPE`, `JSON_KEYS`, `JSON_LENGTH`, and the mutators `JSON_SET`,
  `JSON_INSERT`, `JSON_REPLACE`, `JSON_REMOVE` (usable in an `UPDATE`'s new value, Chapter 13).

Some have a Cosmos counterpart that is not mapped yet — `STARTS_WITH`, `ENDS_WITH`, `SPLIT`,
`REGEXP_REPLACE`, the bitwise functions, `COUNTIF`, `BOOL_AND`/`BOOL_OR`, `STDDEV`/`VARIANCE` among them.
[`TODO.md`](../TODO.md) lists them.

---

[← Previous: Operand reference](appendix-a-operands.md) · [Contents](README.md) · [Appendix C: Type reference →](appendix-c-types.md)
