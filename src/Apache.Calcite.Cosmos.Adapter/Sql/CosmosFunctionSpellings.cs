using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// How a function is spelled at the service, and what its spelling asks beyond what the function rows say: the
    /// renames, the window, search and trim spellings, the operands the rows describe, and the legality of the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a function computes over a band is the rows'</b> (<see cref="CosmosAxiomRows.ReadFunctions"/>), read by S at a
    /// computed place and by the translator's column gate alike; this holds what the rows do not carry. Reasoning:
    /// <c>DESIGN.md</c>, <em>A computed value is a place</em>.
    /// </para>
    /// <para>
    /// <b>Azure has no function rows until the probe account's run 1 (h14b).</b> Until then the column gate reads the
    /// verdicts the translator held Azure to (<see cref="AzureVerdicts"/>), measured on 2026-10-19 with no run record, and S
    /// reads none: under <c>Production</c> no computed place has an image. A bridge, gone when the rows land.
    /// </para>
    /// </remarks>
    internal static class CosmosFunctionSpellings
    {

        /// <summary>
        /// What a spelling asks of some of its operands' characters beyond the rows: the classes refused, what they are
        /// called, the operands asked (Rex ordinals), and why.
        /// </summary>
        public sealed record Refusal(JsonCharacterBand Refused, string Named, int[] Asked, string Why);

        /// <summary>
        /// Scalar functions whose Cosmos counterpart takes the same arguments in the same order
        /// and means the same thing, mapped from the SQL name to the Cosmos name.
        /// </summary>
        /// <remarks>
        /// Verified against the service. Functions needing an argument adjustment are handled
        /// separately below; anything absent from both is declined.
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, (string Name, int MinArgs, int MaxArgs)> DirectFunctions = new Dictionary<string, (string Name, int MinArgs, int MaxArgs)>(StringComparer.Ordinal)
        {
            ["UPPER"] = ("UPPER", 1, 1),
            ["LOWER"] = ("LOWER", 1, 1),
            ["LENGTH"] = ("LENGTH", 1, 1),
            ["CHAR_LENGTH"] = ("LENGTH", 1, 1),
            ["CHARACTER_LENGTH"] = ("LENGTH", 1, 1),
            ["REPLACE"] = ("REPLACE", 3, 3),
            ["CONCAT"] = ("CONCAT", 2, int.MaxValue),
            ["||"] = ("CONCAT", 2, int.MaxValue),
            ["ABS"] = ("ABS", 1, 1),
            ["EXP"] = ("EXP", 1, 1),
            ["LN"] = ("LOG", 1, 1),
            ["LOG10"] = ("LOG10", 1, 1),
            ["POWER"] = ("POWER", 2, 2),
            ["SQRT"] = ("SQRT", 1, 1),
            ["SIGN"] = ("SIGN", 1, 1),
            ["ROUND"] = ("ROUND", 1, 1),
            // Cosmos carries the whole trigonometric set under the names SQL uses, ATAN2 excepted:
            // Cosmos spells it ATN2, after T-SQL.
            ["SIN"] = ("SIN", 1, 1),
            ["COS"] = ("COS", 1, 1),
            ["TAN"] = ("TAN", 1, 1),
            ["COT"] = ("COT", 1, 1),
            // ASIN and ACOS carry a domain, and Cosmos fails the whole query for a value outside it
            // rather than yielding undefined — measured, along with SQRT(-1), which does the same and
            // which this adapter has always pushed, and LOG(0), which does too. This comment used to
            // say LOG(0) was accepted; the measurement beside it said otherwise, and still does. So
            // this is one property of the service rather than a fact about these two, and they are
            // treated as SQRT already is; see OutOfDomainArithmeticFailsTheQuery, which pins the
            // measurement (CosmosAxioms: Service.OutOfDomainArithmeticFailsTheStatement), and
            // DESIGN.md, which records that the choice to live with it is a choice.
            ["ASIN"] = ("ASIN", 1, 1),
            ["ACOS"] = ("ACOS", 1, 1),
            ["ATAN"] = ("ATAN", 1, 1),
            ["ATAN2"] = ("ATN2", 2, 2),
            ["DEGREES"] = ("DEGREES", 1, 1),
            ["RADIANS"] = ("RADIANS", 1, 1),
            // Niladic, and written PI() at both ends.
            ["PI"] = ("PI", 0, 0),
            // The two-argument SQL form — truncate to a number of decimal places — is left out
            // rather than guessed at, the arity of Cosmos's TRUNC not having been verified.
            ["TRUNCATE"] = ("TRUNC", 1, 1),
            // String functions SQL and Cosmos spell alike and mean alike. LEFT and RIGHT clamp rather
            // than fail on a count longer than the string at both ends; REVERSE is a reversal.
            ["LEFT"] = ("LEFT", 2, 2),
            ["RIGHT"] = ("RIGHT", 2, 2),
            ["REVERSE"] = ("REVERSE", 1, 1),
            // SQL repeats a string with REPEAT and Cosmos with REPLICATE. Same arguments, same order,
            // same meaning.
            ["REPEAT"] = ("REPLICATE", 2, 2),
            // The array functions, mapped from their SQL counterparts. ARRAY_SLICE differs only in
            // index origin and is adjusted like SUBSTRING, below; these three agree outright.
            ["ARRAY_CONCAT"] = ("ARRAY_CONCAT", 2, int.MaxValue),
            ["ARRAY_INTERSECT"] = ("SETINTERSECT", 2, 2),
            ["ARRAY_UNION"] = ("SETUNION", 2, 2),
            // Under its own name, and this one is not an index origin: a regular expression dialect
            // is a semantic difference a query cannot see. Cosmos documents PCRE with constructs it
            // does not support, so REGEXP_LIKE and this are two languages, not two spellings — the
            // LIKE bracket measurement is the standing argument.
            ["REGEXMATCH"] = ("REGEXMATCH", 2, 3),
            // The JSON conversions. Cosmos spells these in camel case and the service is
            // case-insensitive about function names, but they are emitted as documented.
            ["ToString"] = ("ToString", 1, 1),
            ["StringToNumber"] = ("StringToNumber", 1, 1),
            ["StringToObject"] = ("StringToObject", 1, 1),
            ["StringToArray"] = ("StringToArray", 1, 1),
            ["StringToBoolean"] = ("StringToBoolean", 1, 1),
            ["ObjectToArray"] = ("ObjectToArray", 1, 1),
            // The type tests. Their argument is an ordinary expression rather than a path, so they need
            // nothing beyond the name — which is already the Cosmos one, these operators being this
            // adapter's own rather than translations of a SQL counterpart. See CosmosOperators.
            ["IS_DEFINED"] = ("IS_DEFINED", 1, 1),
            ["IS_ARRAY"] = ("IS_ARRAY", 1, 1),
            ["IS_BOOL"] = ("IS_BOOL", 1, 1),
            ["IS_NULL"] = ("IS_NULL", 1, 1),
            ["IS_NUMBER"] = ("IS_NUMBER", 1, 1),
            ["IS_OBJECT"] = ("IS_OBJECT", 1, 1),
            ["IS_PRIMITIVE"] = ("IS_PRIMITIVE", 1, 1),
            ["IS_STRING"] = ("IS_STRING", 1, 1),
            // The geography operators, whose Cosmos spellings are the unprefixed ones. The prefix exists
            // because Calcite's own ST_* are planar and mean something else — see DESIGN.md — and it goes
            // away here because the service has only the one reading, which is the geodesic one.
            [Geography.Sql.GeographyOperatorTable.ClrStGeogDistance.getName()] = ("ST_DISTANCE", 2, 2),
            [Geography.Sql.GeographyOperatorTable.ClrStGeogWithin.getName()] = ("ST_WITHIN", 2, 2),
            [Geography.Sql.GeographyOperatorTable.ClrStGeogIntersects.getName()] = ("ST_INTERSECTS", 2, 2),
            [Geography.Sql.GeographyOperatorTable.ClrStGeogIsValid.getName()] = ("ST_ISVALID", 1, 1),
        };

        /// <summary>
        /// The classes a case mapping asks its argument to be free of: everything outside ASCII. Kept as legality under
        /// every set: the rows' witnesses of a surrogate and of the high band hold no cased letter, and Java maps both
        /// (U+10428, U+FF21), so a row's <c>Same</c> there proves nothing of a case mapping.
        /// </summary>
        public const JsonCharacterBand CaseMapped = JsonCharacterBand.BeyondAscii;

        /// <summary>The two characters the emulator stores escaped, U+0000 and U+FDD0, the second read as the high band.</summary>
        const JsonCharacterBand Escaped = JsonCharacterBand.Nul | JsonCharacterBand.HighBmp;

        const string EscapedNamed = "U+0000 or unit from U+E000, U+FDD0 among them,";

        /// <summary>The single bands a string's characters fall in, each a key of the rows.</summary>
        static readonly JsonCharacterBand[] Bands = [JsonCharacterBand.Surrogates, JsonCharacterBand.HighBmp, JsonCharacterBand.NonAscii, JsonCharacterBand.Trimmed, JsonCharacterBand.Nul];

        static readonly Refusal Mapped = new(CaseMapped, "character outside ASCII", [0],
            "the service maps case by a simple mapping and Calcite by Java's full one, which agree over ASCII alone");

        static readonly Refusal Searched = new(JsonCharacterBand.Nul, "U+0000", [0, 1],
            "the service's REPLACE, CONTAINS and the LIKE it answers as a CONTAINS read a string only up to its first U+0000, and a search holding one matches every string");

        static readonly Refusal SearchedFor = Searched with { Asked = [1] };

        static readonly Refusal ReplacedStored = new(Escaped, EscapedNamed, [1, 2],
            "the emulator's REPLACE works on the units it stores, a search beginning 0 or 1 meeting an escape's second unit, and a replacement holding U+0000 fails the statement where one holding U+FDD0 mixes the written spelling into the stored one");

        static readonly Refusal ConcatenatedStored = new(Escaped, EscapedNamed, [],
            "the emulator joins what it stores with what it makes, and a U+0000 made there fails the statement or vanishes where a U+FDD0 made there is read back as an escape's first unit");

        static readonly Refusal RepeatedStored = ConcatenatedStored with { Asked = [0] };

        static readonly Refusal PatternedStored = new(Escaped, EscapedNamed, [0],
            "the emulator matches a pattern against the units it stores, so `_` matches one unit of an escape and a 0 or a 1 an escape's second unit");

        /// <summary>
        /// The bands each string function was measured on Azure to part from Calcite over (2026-10-19, no run record), its
        /// subject a string: what the column gate holds Azure to until run 1 gives Azure rows. A bridge (TODO.md).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, JsonCharacterBand> AzureVerdicts = new Dictionary<string, JsonCharacterBand>(StringComparer.Ordinal)
        {
            // LENGTH counts code points; SUBSTRING, LEFT and RIGHT units, a split pair sent as U+FFFD.
            ["CHAR_LENGTH"] = JsonCharacterBand.Surrogates,
            ["CHARACTER_LENGTH"] = JsonCharacterBand.Surrogates,
            ["LENGTH"] = JsonCharacterBand.Surrogates,
            ["SUBSTRING"] = JsonCharacterBand.Surrogates,
            ["LEFT"] = JsonCharacterBand.Surrogates,
            ["RIGHT"] = JsonCharacterBand.Surrogates,
            ["UPPER"] = CaseMapped,
            ["LOWER"] = CaseMapped,
            ["TRIM BOTH"] = JsonCharacterBand.Trimmed,
            ["TRIM LEADING"] = JsonCharacterBand.Trimmed,
            ["TRIM TRAILING"] = JsonCharacterBand.Trimmed,
            ["REPLACE"] = JsonCharacterBand.Nul,
            // INDEX_OF counts units as Calcite does; a reversal, a concatenation and a repetition keep every character.
            ["POSITION"] = JsonCharacterBand.None,
            ["REVERSE"] = JsonCharacterBand.None,
            ["CONCAT"] = JsonCharacterBand.None,
            ["||"] = JsonCharacterBand.None,
            ["REPEAT"] = JsonCharacterBand.None,
        };

        /// <summary>Every function row by key and target, as S reads them.</summary>
        public static readonly IReadOnlyDictionary<(FunctionKey Key, RenderingTarget Target), CosmosFunctionAnswer> Rows =
            CosmosAxiomRows.ReadFunctions(CosmosAxioms.All).ToDictionary(r => (r.Key, r.Target), r => r.Answer);

        /// <summary>The operand each function's rows describe, by the loader's ordinals, as S reads it.</summary>
        public static readonly IReadOnlyDictionary<string, long> Subjects =
            CosmosAxiomRows.ReadFunctionReads(CosmosAxioms.All).ToDictionary(f => f.Function, f => f.Subject, StringComparer.Ordinal);

        /// <summary>The rows' names of the functions some target measured: what the loader reads as a computed place (h10).</summary>
        public static readonly IReadOnlySet<string> RowFunctions = Rows.Keys.Select(k => k.Key.Function).ToHashSet(StringComparer.Ordinal);

        /// <summary>The functions whose result is the service's own, which Calcite has no body for: what the service computes is what it means.</summary>
        public static readonly IReadOnlySet<string> Declared = new HashSet<string>(StringComparer.Ordinal) { "ToString" };

        /// <summary>
        /// The bands of its subject's characters over which a target answers a function otherwise than Calcite, or
        /// <c>null</c> where its answer over a string of no band is unmeasured there or not Calcite's.
        /// </summary>
        /// <param name="function">The rows' name of the function (<see cref="KeyOf"/>).</param>
        /// <param name="target">The target.</param>
        public static JsonCharacterBand? Parting(string function, RenderingTarget target)
        {
            if (target == RenderingTarget.Azure && Rows.Keys.Any(k => k.Target == RenderingTarget.Azure) == false)
                return AzureVerdicts.TryGetValue(function, out var verdict) ? verdict : null;

            if (Answer(function, target, JsonCharacterBand.None) != CosmosFunctionAnswer.Same)
                return null;

            var parting = JsonCharacterBand.None;
            foreach (var band in Bands)
                if (Answer(function, target, band) != CosmosFunctionAnswer.Same)
                    parting |= band;

            return parting;
        }

        static CosmosFunctionAnswer? Answer(string function, RenderingTarget target, JsonCharacterBand band) =>
            Rows.TryGetValue((FunctionKey.Of(function, CosmosValueClass.String, band), target), out var answer) ? answer : null;

        /// <summary>
        /// The rows' name of a call: its operator's, a trim with its flag as the loader names it, or <c>null</c> for a
        /// trim whose flag is no literal.
        /// </summary>
        public static string? KeyOf(RexCall call) =>
            call.getKind().name() != "TRIM" ? call.getOperator().getName()
            : call.getOperands().size() == 3 && call.getOperands().get(0) is RexLiteral { } flag && flag.getValue() is org.apache.calcite.sql.fun.SqlTrimFunction.Flag f ? "TRIM " + f.name()
            : null;

        /// <summary>
        /// The Rex ordinals of a call the rows describe (<see cref="Subjects"/>), a trim's flag shifting them by one; each
        /// ordinal of a call no row describes, which the Azure bridge alone names.
        /// </summary>
        public static IEnumerable<int> SubjectsOf(string function, RexCall call)
        {
            var shift = function.StartsWith("TRIM ", StringComparison.Ordinal) ? 1 : 0;
            var subject = Subjects.TryGetValue(function, out var s) ? s : FunctionKey.Every;
            for (var i = shift; i < call.getOperands().size(); i++)
                if (subject == FunctionKey.Every || subject == i - shift)
                    yield return i;
        }

        /// <summary>Whether the column gate holds a function to a verdict: rows name it, or the Azure bridge does.</summary>
        public static bool Named(string function) => Subjects.ContainsKey(function) || AzureVerdicts.ContainsKey(function);

        /// <summary>
        /// Determines whether a string function written by name has a verdict: rows the column gate reads, or a result that
        /// is the service's own.
        /// </summary>
        public static bool HasVerdict(string name) => Named(name) || Declared.Contains(name);

        /// <summary>
        /// What a call's spelling asks of its operands' characters beyond the rows, under a set (<see cref="CosmosRendering.StoresSpelled"/>).
        /// </summary>
        /// <remarks>
        /// The rows describe the subject alone. A case mapping asks ASCII under every set, its rows unwitnessed beyond it;
        /// Azure's search reads to its first U+0000; and under the emulator, which stores U+0000 and U+FDD0 escaped, a
        /// replacement's search and replacement, a concatenation's operands, a repetition's subject and a pattern's subject
        /// are asked to hold neither (#246). Carried as legality: no row says them.
        /// </remarks>
        public static IEnumerable<Refusal> Refusals(RexCall call, RenderingSet set)
        {
            var stored = CosmosRendering.StoresSpelled(set);
            if (call.getKind().name() == "LIKE")
            {
                if (LikeRefusal(call, stored) is { } like)
                    yield return like;
                yield break;
            }

            switch (call.getOperator().getName())
            {
                case "UPPER" or "LOWER":
                    yield return Mapped;
                    break;
                case "REPLACE":
                    yield return stored ? ReplacedStored : SearchedFor;
                    break;
                case "CONCAT" or "||" when stored:
                    yield return ConcatenatedStored with { Asked = [.. Enumerable.Range(0, call.getOperands().size())] };
                    break;
                case "REPEAT" when stored:
                    yield return RepeatedStored;
                    break;
            }
        }

        static Refusal? LikeRefusal(RexCall call, bool stored)
        {
            var text = call.getOperands().size() == 2 && call.getOperands().get(1) is RexLiteral { } literal && literal.getValue() is org.apache.calcite.util.NlsString pattern ? pattern.getValue() : null;

            // One run of text between two `%` is answered as a CONTAINS, which reads the subject to its first U+0000 and,
            // seeking one, matches everything.
            var rule = text is not null && IsContainment(text) ? Searched : null;
            if (stored == false)
                return rule;

            // A prefix is sent stored whatever it holds, E being a prefix code: E(s) starts with E(p) exactly where s starts with p.
            return text is null ? PatternedStored
                : IsPrefix(text) ? rule
                : text.IndexOfAny(['_', '0', '1']) >= 0 ? PatternedStored
                : rule;
        }

        /// <summary>The service's spelling of a trim's flag, or <c>null</c>.</summary>
        public static string? TrimOf(string flag) => flag switch
        {
            "BOTH" => "TRIM",
            "LEADING" => "LTRIM",
            "TRAILING" => "RTRIM",
            _ => null,
        };

        /// <summary><c>SUBSTRING</c> over a start proved at least one: the service counts from 0. M30 drops the shift (line 17).</summary>
        public static string SubstringFrom(string subject, string start, string length) => RenderingPrinterFaults.Has(RenderingPrinterFault.M30)
            ? $"SUBSTRING({subject}, {start}, {length})"
            : $"SUBSTRING({subject}, ({start} - 1), {length})";

        /// <summary><c>SUBSTRING</c> over any start, clipped as Calcite clips a window that starts before the string.</summary>
        public static string SubstringClipped(string subject, string s, string n) =>
            $"SUBSTRING({subject}, ({s} < 1 ? 0 : ({s} - 1)), ({s} < 1 ? ((({s} + {n}) < 1) ? 0 : (({s} + {n}) - 1)) : {n}))";

        /// <summary><c>POSITION</c> as <c>INDEX_OF</c>, which takes the string first and is 0-based, -1 where absent.</summary>
        public static string Position(string needle, string haystack) => $"(INDEX_OF({haystack}, {needle}) + 1)";

        /// <summary>
        /// The service's spelling of a loader call over its operands' spellings, or <c>null</c> where it has none: a
        /// computed place's <c>Spelling</c> in S. The literal operands a spelling's legality reads (a window's start, a
        /// trim's character, a repetition's count, a search) are asked here, and a parameter is never one.
        /// </summary>
        /// <param name="call">The loader's call.</param>
        /// <param name="spell">An operand's spelling, or <c>null</c> where it has none.</param>
        public static string? Spell(SqlValue.Call call, Func<SqlValue, string?> spell)
        {
            // A literal the emulator would store escaped, or the SDK refuse to send, is never spelled into a place.
            if (call.Operands.Any(o => o is SqlValue.Literal l && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.IsString(l.Value)
                && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.StringOf(l.Value).Any(c => c == '\0' || c == '﷐' || char.IsSurrogate(c))))
                return null;

            var operands = new string[call.Operands.Count];
            for (var i = 0; i < operands.Length; i++)
                if (spell(call.Operands[i]) is string s)
                    operands[i] = s;
                else
                    return null;

            long? Integral(int i) => call.Operands[i] is SqlValue.Literal { IsNumber: true } l && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.TryNumberOf(l.Value, out var n) && n == decimal.Truncate(n) && Math.Abs(n) < long.MaxValue ? (long)n : null;
            string? Text(int i) => call.Operands[i] is SqlValue.Literal l && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.IsString(l.Value) ? Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.StringOf(l.Value) : null;

            switch (call.Function)
            {
                case "SUBSTRING" when operands.Length == 3 && Integral(1) >= 1:
                    return SubstringFrom(operands[0], operands[1], operands[2]);
                case "POSITION" when operands.Length == 2:
                    return Position(operands[0], operands[1]);
                case "TRIM BOTH" or "TRIM LEADING" or "TRIM TRAILING" when operands.Length == 2 && Text(0) == " ":
                    return $"{TrimOf(call.Function[5..])}({operands[1]})";
                // A positive count reads Azure's length bound, a result key no row holds yet (h14b).
                case "REPEAT" when operands.Length == 2 && Integral(1) == 0:
                    return $"REPLICATE({operands[0]}, {operands[1]})";
                case "REPLACE" when operands.Length == 3 && Text(1) is { Length: > 0 }:
                    return $"REPLACE({operands[0]}, {operands[1]}, {operands[2]})";
                case "REPEAT" or "REPLACE" or "SUBSTRING" or "POSITION":
                    return null;
                // An arithmetic operator is infix at both ends; what it computes is its typed rows'.
                case "+" or "-" or "*" or "/" or "%" when operands.Length == 2:
                    return $"({operands[0]} {call.Function} {operands[1]})";
            }

            if (call.Function.StartsWith("TRIM", StringComparison.Ordinal) || DirectFunctions.TryGetValue(call.Function, out var mapping) == false || operands.Length < mapping.MinArgs || operands.Length > mapping.MaxArgs)
                return null;

            return $"{mapping.Name}({string.Join(", ", operands)})";
        }

        /// <summary>
        /// The most code points the service's <c>REPLICATE</c> answers, inclusive: past them it is undefined on Azure, where
        /// Calcite's <c>REPEAT</c> repeats (<c>Service.ReplicateStopsAtTenThousandCodePoints</c>). The emulator has no limit.
        /// </summary>
        public const long RepetitionLimit = 10_000;

        /// <summary>
        /// Whether <c>REPLICATE</c> answers Calcite's <c>REPEAT</c> for a count and a subject at most so many code points
        /// long: 0 is empty at both, a negative count undefined at the service, a positive one Calcite's within the limit.
        /// </summary>
        public static bool RepeatsAsCalcite(long count, long? most) =>
            count == 0 || count > 0 && most is long length && length <= RepetitionLimit / count;

        /// <summary>
        /// Whether the service's <c>REPLACE</c> answers Calcite's for a search at least so many units long: of an empty
        /// search it is undefined (<c>Service.ReplaceOfAnEmptySearchIsUndefined</c>).
        /// </summary>
        public static bool SearchesSomething(long? least) => least >= 1;

        /// <summary>Whether a <c>LIKE</c> pattern is a prefix match, its one wildcard a trailing <c>%</c>, written as <c>STARTSWITH</c>.</summary>
        static bool IsPrefix(string pattern) =>
            pattern.EndsWith('%') && pattern.IndexOfAny(['%', '_']) == pattern.Length - 1;

        /// <summary>
        /// Whether a <c>LIKE</c> pattern is one the service answers as a <c>CONTAINS</c>: one run of text with no wildcard,
        /// after one or more <c>%</c> and before one or more. Measured on the probe account: <c>%b%</c>, <c>%%b%</c> and
        /// <c>%b%%</c> lose <c>a</c>U+0000<c>b</c>, and <c>%</c>U+0000<c>%</c> keeps every string.
        /// </summary>
        internal static bool IsContainment(string pattern)
        {
            var start = 0;
            while (start < pattern.Length && pattern[start] == '%')
                start++;

            var end = pattern.Length;
            while (end > start && pattern[end - 1] == '%')
                end--;

            return start > 0 && end < pattern.Length && end > start && pattern.IndexOfAny(['%', '_'], start, end - start) < 0;
        }

        /// <summary>Whether a text the case-insensitive match is handed folds alike at both ends: ASCII, read as the theory reads characters.</summary>
        public static bool FoldsAlike(string text) =>
            (JsonFactEncoding.Characters(text) & CaseMapped) == JsonCharacterBand.None;

    }

}
