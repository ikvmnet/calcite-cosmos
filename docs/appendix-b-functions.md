# Appendix B. Function and operator reference

What each SQL operator and function becomes in Cosmos SQL. A function that pushes is evaluated by the
service; one that does not is evaluated by Calcite over the rows that come back, and the rest of the
query around it can still push (Chapter 8).

Functions marked *library* are not in Calcite's standard operator table; enable a library that has
them with the connection's `Fun` setting (Chapter 6), e.g. `Fun = "all"`.

## B.1 Operators

| SQL | Cosmos SQL | notes |
| --- | --- | --- |
| `=` `<>` `<` `<=` `>` `>=` | `=` `!=` `<` `<=` `>` `>=` | over `JSON_VALUE`, see Chapter 8; selected, grouped by or compared with something, in process where it can be null |
| `AND` `OR` `NOT` | `AND` `OR` `NOT` | with null guards |
| `+` `-` `*` `%`, unary `-` | the same | not with a decimal fraction (`price * 1.1`), which Calcite computes exactly and Cosmos in doubles; integer overflow is computed rather than wrapped (Chapter 8) |
| `/` | `/` | a `DOUBLE` quotient only; an integer or `DECIMAL` division stays in process |
| `\|\|` | `CONCAT` | over `JSON_VALUE`, only where the path is declared a string |
| `IS NULL` / `IS NOT NULL` | `NOT IS_DEFINED(x) OR IS_NULL(x)` / `IS_DEFINED(x) AND NOT IS_NULL(x)` | over an accessor, plus its own test, e.g. `IS_PRIMITIVE`; over a comparison, in process (Chapter 8) |
| `IN (…)`, `NOT IN (…)`, `BETWEEN` | comparisons | same cost as the native forms |
| `LIKE` | `STARTSWITH` for a prefix; Cosmos `LIKE` otherwise | no brackets, no `ESCAPE`, literal pattern only. A pattern that is one run of text between two `%`, which the service answers as a `CONTAINS`, only over a string proved to hold no U+0000: Azure's `CONTAINS` stops at the first (B.2) |
| `UPPER(x) LIKE` / `LOWER(x) LIKE` | `CONTAINS`, `STARTSWITH`, `ENDSWITH` with the case-insensitive flag | ASCII text, one leading and/or trailing `%`, and `x` proved ASCII as `UPPER` asks (B.2), and for `CONTAINS` to hold no U+0000; otherwise in process |
| `CASE` | nested `? :` | |
| `COALESCE`, `NULLIF`, `NVL`, `NVL2`, `IFNULL`, `IF` | via `CASE` | Calcite expands them first |
| `CAST(x AS VARCHAR)` | dropped in a comparison over a document value; read as text in a projection | unsized `VARCHAR` only, in both: a length truncates |
| `CAST(x AS <number>)` | in a filter, the property's presence plus a recheck (`INTEGER`, `BIGINT`, `DOUBLE`), exact over a path declared an integer, and nothing for `SMALLINT`, `TINYINT`, `REAL`, `FLOAT` or `DECIMAL(p, s)`; in process in a projection | Chapter 8 |
| `CAST(x AS UUID)`, parse to `TIMESTAMP`/`DATE` | the path, where the container declares the stored form | Chapters 14, 15 |
| `arr[n]` | `arr[n-1]` | SQL is one-based |
| `x MEMBER OF arr` | `ARRAY_CONTAINS(arr, x)` | |
| `IS TRUE`, `IS FALSE`, `IS NOT TRUE`, `IS NOT FALSE` | `c.x = true`, … | over a boolean cast where the schema declares a boolean; in process otherwise. Selected, `IS TRUE` is sent as `IS_DEFINED(x) AND x = true`, false where `x` is missing |
| `IS DISTINCT FROM` | — | in process |
| `SIMILAR TO`, `REGEXP_LIKE` | — | in process; use `REGEXMATCH` for Cosmos regular expressions |

## B.2 String functions

Over `JSON_VALUE`, each of these pushes only where the container declares the path a string: the
function reads the text, which for a stored number or boolean is not what the service has (Chapter 8).

**And some push only where the characters are proved.** Calcite's string functions are Java's and the
service's are its own, and they part over some characters: Calcite counts UTF-16 units where the
service's `LENGTH` counts code points, maps case by Java's full mapping where the service leaves `ß` and
`ﬀ` as they are, trims spaces where the service trims white space, and reads past a U+0000 where Azure's
`REPLACE` and `CONTAINS` stop at the first. Each such function pushes only where what is known of its
argument — a `pattern`, an `enum`, a stored form, or the statement's own `WHERE … IN (…)` (Chapter 14.4)
— says it holds none of the characters the two part over; otherwise Calcite computes it over the value
the service sends. In a projection the path is still read at the service, alone, and the rest of the
statement pushes; in a filter the comparison is Calcite's, made over the documents the rest of the filter
keeps, which are read whole. Every input counts: in `CHAR_LENGTH(s || t)` both `s` and `t`, and a
literal by its own characters. A parameter may hold anything, so a function over one stays in process.

**And some ask something that is not a character.** Where the service answers a window, a repetition or
a search otherwise than Calcite, the function pushes only where the operand is proved to be one they
agree over, and the table says which.

| SQL | Cosmos | pushes where |
| --- | --- | --- |
| `UPPER`, `LOWER` | `UPPER`, `LOWER` | its argument holds ASCII alone |
| `CHAR_LENGTH`, `CHARACTER_LENGTH`, `LENGTH` | `LENGTH` | its argument holds no character beyond U+FFFF (an emoji is one; Calcite counts it as two) |
| `SUBSTRING(s FROM i FOR n)` | `SUBSTRING(s, i - 1, n)` | `s` holds no character beyond U+FFFF: Azure returns half of one as U+FFFD. A start not proved at least one — a column, a parameter, a `POSITION` that may find nothing — is written with Calcite's clip to the string, `SUBSTRING(s, (i < 1 ? 0 : (i - 1)), …)`, since the service answers a negative start with the empty string. The form without a length stays in process |
| `POSITION(a IN b)` | `(INDEX_OF(b, a) + 1)` | anything: Azure counts as Calcite does. The emulator counts an emoji as one, so there the position after one differs |
| `TRIM`, `LTRIM`, `RTRIM` (spaces) | `TRIM`, `LTRIM`, `RTRIM` | its argument holds nothing the service trims beside a space: U+0009–U+000D, U+0085, U+00A0, U+1680, U+180E, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000. Trimming other characters stays in process |
| `REPLACE(s, a, b)` | `REPLACE` | `s` and `a` hold no U+0000, which Azure's reads up to and no further; and `a` holds something — a literal that does, or a path whose `enum` lists no empty string — since the service's is undefined for an empty search, where Calcite's is `s` |
| `CONCAT`, `\|\|` | `CONCAT` | anything. The `CONCAT` of the PostgreSQL, SQL Server and Oracle libraries reads a null as the empty string, where the service's is undefined, and stays in process |
| `LEFT`, `RIGHT` (*library*) | `LEFT`, `RIGHT` | no character beyond U+FFFF, as `SUBSTRING` |
| `REVERSE` (*library*) | `REVERSE` | anything |
| `REPEAT(s, n)` (*library*) | `REPLICATE` | `n` is a literal: 0, or a positive count with `s` short enough — a literal, or a path whose `enum` bounds its length — that the result stays within 10,000 code points. Azure's is undefined for a negative count, where Calcite's is the empty string, and past that length; the emulator's has no limit |
| `INSTR`, `STRPOS`, `SUBSTR`, `LEN` (*library*) | via the standard forms | as the form each becomes |

A `U&` literal holding a lone surrogate, `U&'\D83D'`, is never sent: the SDK cannot encode it. The
expression it stands in is computed in process. A parameter bound to one is refused by name when the statement
runs (Chapter 22).

These are the service's own, with nothing of Calcite's to agree with, and are written as they are:

| SQL | Cosmos | notes |
| --- | --- | --- |
| `REGEXMATCH(s, pattern[, modifiers])` | `REGEXMATCH` | PCRE with documented exceptions |
| `ToString`, `StringToNumber`, `StringToBoolean`, `StringToArray`, `StringToObject`, `ObjectToArray` | the same | no in-process body |

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
