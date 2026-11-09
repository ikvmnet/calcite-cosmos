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
| `LIKE` | the narrowest test that says the pattern: `=` for literal text, `STARTSWITH` for a prefix, `ENDSWITH` for a suffix, `CONTAINS` for one run between two `%`; Cosmos `LIKE` otherwise | a `[` only where one of the first four says it, since Cosmos `LIKE` reads it as a range (a `]` that closes none and a backslash are themselves to both); no `ESCAPE`, no U+0000, which Calcite reads as an escape where none is written, literal pattern only. A pattern that is one run of text between two `%` only over a string proved to hold no U+0000: Azure's `CONTAINS` stops at the first (B.2). A `_` only over a string proved to hold no line terminator, which the service's `_` matches and Calcite's never does (Chapter 8.2) |
| `UPPER(x) LIKE` / `LOWER(x) LIKE` | the `LIKE`'s own test over the fold, `CONTAINS(UPPER(x), …)`, `STARTSWITH` or `ENDSWITH` | `x` proved ASCII as `UPPER` asks (B.2), and for `CONTAINS` to hold no U+0000; otherwise in process. The service's case-insensitive flag, which asks the same without the fold, is not sent in a filter yet |
| `CASE` | nested `? :` | |
| `COALESCE`, `NULLIF`, `NVL`, `NVL2`, `IFNULL`, `IF` | via `CASE` | Calcite expands them first |
| `CAST(x AS VARCHAR)` | dropped in a comparison over a document value; read as text in a projection | unsized `VARCHAR` only, in both: a length truncates |
| `CAST(x AS <number>)` | in a filter, the comparison over a stored number plus a recheck (`INTEGER`, `BIGINT`, `DOUBLE`; against a literal past 2^53 the property's presence), exact over a path declared an integer, and nothing for `SMALLINT`, `TINYINT`, `REAL`, `FLOAT` or `DECIMAL(p, s)`; in process in a projection | Chapter 8 |
| `CAST(x AS UUID)`, parse to `TIMESTAMP`/`DATE` | the path, where the container declares the stored form | Chapters 14, 15 |
| `arr[n]` | `arr[n-1]` | SQL is one-based |
| `x MEMBER OF arr` | `ARRAY_CONTAINS(arr, x)` | |
| `IS TRUE`, `IS FALSE`, `IS NOT TRUE`, `IS NOT FALSE` | `c.x = true`, … | over a boolean cast where the schema declares a boolean; in process otherwise. Selected, `IS TRUE` is sent as `IS_DEFINED(x) AND x = true`, false where `x` is missing |
| `IS DISTINCT FROM` | — | in process |
| `SIMILAR TO`, `REGEXP_LIKE` | — | in process; use `REGEXMATCH` for Cosmos regular expressions |

## B.2 String functions

Over `JSON_VALUE`, each of these pushes only where the container declares the path a string: the
function reads the text, which for a stored number or boolean is not what the service has (Chapter 8).
Over the `VARIANT` partition key column none does: the function reads the column cast to text, which is
null for a number or a boolean where the service's is undefined, and the service's answers were not
measured through that reading (Chapter 7.3).

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

**On the emulator, a function over a stored U+0000 or U+FDD0 stays in process too.** The emulator stores
U+0000 as the two units U+FDD0 `0` and U+FDD0 as U+FDD0 `1` (Appendix E), and its functions count, cut,
reverse and search those units: `LENGTH` counts one U+0000 as two, `REVERSE` puts the `0` first,
`INDEX_OF` counts past it, a `LIKE` `_` matches half of it and a `0` or `1` in a pattern matches its
second unit, and a U+0000 the service would make itself, in a `CONCAT` or a `REPLACE`, is an error. So
on the emulator `CHAR_LENGTH` and the library's `LENGTH`, `SUBSTRING`, `LEFT`, `RIGHT`, `REVERSE`, the subject of `POSITION`, each
operand of `REPLACE`, `||` and `REPEAT`, and the subject of a `LIKE` whose pattern holds a `_`, a `0` or a
`1` beyond a plain prefix push only where it is proved to hold neither character; U+FDD0 is not told apart from
the characters from U+E000 up, so a value that may hold one of those stays in process there as well.
Azure stores both as they are, and none of this applies to it.

**And some ask something that is not a character.** Where the service answers a window, a repetition or
a search otherwise than Calcite, the function pushes only where the operand is proved to be one they
agree over, and the table says which.

| SQL | Cosmos | pushes where |
| --- | --- | --- |
| `UPPER`, `LOWER` | `UPPER`, `LOWER` | its argument holds ASCII alone; `LOWER` also over the characters from U+E000 up, whose only capitals, the fullwidth Latin letters, both engines lower alike; on Azure both also over the characters beyond U+FFFF, which Azure maps as Calcite does, Deseret's letters mapped and every script cased after Java 8 left as it is, from Warang Citi to Garay |
| `CHAR_LENGTH`, `CHARACTER_LENGTH` | `LENGTH` | its argument holds no character beyond U+FFFF (an emoji is one; Calcite counts it as two) |
| `LENGTH` (*library*) | `LENGTH` | as `CHAR_LENGTH` |
| `SUBSTRING(s FROM i FOR n)` | `SUBSTRING(s, i - 1, n)` | `s` holds no character beyond U+FFFF: Azure returns half of one as U+FFFD; and `i` and `n` are literals, the service's answers having been measured with literal bounds alone, so a column, a parameter or a `POSITION` there stays in process; and `n` is at least 0, the lengths both were measured with, since below 0 Calcite raises where the service answers the empty string. A start below one is written with Calcite's clip to the string, `SUBSTRING(s, (i < 1 ? 0 : (i - 1)), …)`, since the service answers a negative start with the empty string. The form without a length stays in process |
| `POSITION(a IN b)` | `(INDEX_OF(b, a) + 1)` | `a` is a string of ASCII alone that holds something, with no U+0000 and no white space but a space among it, the patterns the service's answers have been measured with: a literal, or a path the container declares `required` whose `enum` lists only such strings, so an empty pattern, one holding anything beyond ASCII and a path that may be absent stay in process. `b` anything on Azure, which counts as Calcite does. The emulator counts an emoji as one, so there the position after one differs, and counts a stored U+0000 or U+FDD0 as two, so there `b` must hold no character beyond U+FFFF and neither of those |
| `TRIM`, `LTRIM`, `RTRIM` (spaces) | `TRIM`, `LTRIM`, `RTRIM` | its argument holds nothing the service trims beside a space: U+0009–U+000D, U+0085, U+00A0, U+1680, U+180E, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000. Trimming other characters stays in process |
| `REPLACE(s, a, b)` | `REPLACE` | `s` and `a` hold no U+0000, which Azure's reads up to and no further; and `a` holds something — a literal that does, or a path whose `enum` lists no empty string — since the service's is undefined for an empty search, where Calcite's is `s`; and `b` is a string, the service's being undefined for a number or a boolean. On Azure `a` is proved a string of ASCII alone that holds something, with no U+0000 and no white space but a space among it, the searches its answers have been measured with: a literal, or a path the container declares `required` whose `enum` lists only such strings, so a search holding anything beyond ASCII, and one read from a path that may be absent, stay in process there, Azure having left a search read from a path unanswered when it was last asked one. `b` may be any string there — empty, holding U+0000 or anything beyond ASCII, or read from a path — each of which Azure answers as Calcite does |
| `CONCAT`, `\|\|` | `CONCAT` | anything but a literal on Azure, and on the emulator an operand holding no U+0000 or U+FDD0; a literal, on both, a string of ASCII alone that holds something, with no U+0000 and no white space but a space among it, the literals the service's answers have been measured with beside a path, so an empty literal or one holding anything beyond ASCII stays in process; on Azure `CONCAT`'s literal may also hold U+0000, or characters from U+E000 up, which Azure answers as Calcite does, where Calcite refuses a `U&` literal beside a column in `\|\|`. The `CONCAT` of the PostgreSQL, SQL Server and Oracle libraries reads a null as the empty string, where the service's is undefined, and stays in process |
| `LEFT`, `RIGHT` (*library*) | `LEFT`, `RIGHT` | no character beyond U+FFFF, and a literal count, as `SUBSTRING` |
| `REVERSE` (*library*) | `REVERSE` | anything on Azure; on the emulator, an argument holding no U+0000 or U+FDD0 |
| `REPEAT(s, n)` (*library*) | `REPLICATE` | `n` is a literal: 0, or a positive count with `s` a path whose `enum` bounds its length short enough that the result stays within 10,000 code points, a character beyond U+FFFF counting one. Azure's is undefined for a negative count, where Calcite's is the empty string, and past that length; the emulator's has no limit, but no run has measured it past that length, so the bound holds there too. A literal `s` stays in process, the service's answers having been measured over a stored subject alone, and on the emulator so does a count of 0, measured on Azure alone |
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
property from one holding JSON `null`.

In process each takes apart the `JSON_VALUE` or `JSON_QUERY` it is given and asks the document what it
holds at that path, as the service does, so a predicate using one can be split and rechecked. It asks
whatever the accessor returns — `IS_NUMBER(JSON_VALUE(c."DOC", '$.price'))` is true of a stored `30`,
whose text is a string — but the statement fails wherever the accessor fails, as `RETURNING INTEGER`
does over a stored `true` and `RETURNING DOUBLE` over a stored `30` (Chapter 7.4).

Over anything else Calcite has no body for a test, and fails it on every row with *… is evaluated by
the service and has no in-process body*: a promoted column such as `c."$.category"` or `c."id"`, an
item taken with `[…]`, a cast, or a function's result such as `UPPER(JSON_VALUE(c."DOC", '$.name'))`. A
filter holding one fails as it fails in Calcite: the adapter does not send it for the service to answer
in Calcite's place. Test the path instead — `IS_DEFINED(JSON_VALUE(c."DOC", '$.category'))`, or
`'$.tags[0]'` for an element.

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
