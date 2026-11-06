using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

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
    /// <b>Azure's function rows are the probe account's run 1 (2026-11-04),</b> read by the column gate and by S alike, as
    /// the emulator's are; the verdicts declared for Azure on 2026-10-19 with no run record went with them.
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
            // The concatenation that reads a null as the empty string, by the name the loader reads it by: spelled as CONCAT,
            // its own rows saying where the two answer alike.
            [Apache.Calcite.Cosmos.Facts.Saturation.StringCalls.NullAsEmpty] = ("CONCAT", 2, int.MaxValue),
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
        /// The classes outside ASCII, where the service's simple case folding and Calcite's full one are not known to agree:
        /// what a case-insensitive match asks its text to be free of (<see cref="FoldsAlike"/>). A case mapping's own rows
        /// say it of UPPER and LOWER, each parting over every band beyond ASCII (<c>Service.StringFunctionAnswersByBand</c>, F5).
        /// </summary>
        public const JsonCharacterBand CaseMapped = JsonCharacterBand.BeyondAscii;

        /// <summary>The two characters the emulator stores escaped, U+0000 and U+FDD0, the second read as the high band.</summary>
        const JsonCharacterBand Escaped = JsonCharacterBand.Nul | JsonCharacterBand.HighBmp;

        const string EscapedNamed = "U+0000 or unit from U+E000, U+FDD0 among them,";

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
        /// The string functions the column gate holds to their rows on every target the set serves, whether a row names them
        /// or not: one no row names, <c>LENGTH</c> (Calcite's library function, keyed by its own name), is refused.
        /// </summary>
        public static readonly IReadOnlySet<string> Gated = new HashSet<string>(StringComparer.Ordinal)
        {
            "CHAR_LENGTH", "CHARACTER_LENGTH", "LENGTH", "SUBSTRING", "LEFT", "RIGHT", "UPPER", "LOWER", "TRIM BOTH", "TRIM LEADING", "TRIM TRAILING",
            "REPLACE", "POSITION", "REVERSE", "CONCAT", "||", "REPEAT",
        };

        /// <summary>The rows' names of the functions some target measured: what the loader reads as a computed place (h10).</summary>
        public static readonly IReadOnlySet<string> RowFunctions = CosmosAxiomRows.ReadFunctions(CosmosAxioms.All).Select(r => r.Key.Function).ToHashSet(StringComparer.Ordinal);

        /// <summary>The functions whose result is the service's own, which Calcite has no body for: what the service computes is what it means.</summary>
        public static readonly IReadOnlySet<string> Declared = new HashSet<string>(StringComparer.Ordinal) { "ToString" };

        /// <summary>
        /// Why the function rows do not agree on a call's operands under a set, or <c>null</c> where they do: the rows S's
        /// image rule reads (<c>RenderingTheory.DefineValued</c>), read as it reads them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>S's keys over S's rows.</b> The keys are S's own (<see cref="RenderingImage.Keys"/>), under the operands the
        /// rows name and the readers they were measured through, met across the axioms that name the function. A key is
        /// measured where a row of the function covers it, the row's class holding the key's atom
        /// (<see cref="FunctionValueClasses.Holding"/>), and agrees where every row covering it answers <c>Same</c> on every
        /// target the set serves, a target that measured none of them answering it unmeasured. A function no row names has
        /// no key measured, and one whose rows name no operand it was measured over, or no reader, has none S's image rule
        /// reads (<c>FunctionSubjects</c>, <c>MeasuredReaders</c>). Nothing is read beside the definition the schema's S is
        /// built from.
        /// </para>
        /// <para>
        /// <b>The bridge G6 replaces</b>, which asks S's image of each call the translator writes (TODO.md).
        /// </para>
        /// </remarks>
        /// <param name="definition">The definition the schema's S is built from.</param>
        /// <param name="set">The set the statement is sent under.</param>
        /// <param name="function">The rows' name of the call (<see cref="RenderingImage.KeyFunction"/>).</param>
        /// <param name="gathered">The call's operands as S's place rule gathers them, one import an ordinal of the loader's.</param>
        public static string? Disagreeing(RenderingDefinition definition, RenderingSet set, string function, ValueSet<PathImport> gathered)
        {
            var rows = definition.Functions.Where(f => f.Key.Function == function).ToDictionary(f => (f.Key, f.Target), f => f.Answer);
            var reads = definition.FunctionReads.Where(f => f.Function == function).Select(f => ValueSet.From(f.Readers)).ToList();
            var subjects = ValueSet.From(definition.FunctionSubjects.Where(f => f.Function == function).Select(f => f.Subject));
            if (rows.Count == 0 || reads.Count == 0 || subjects.IsEmpty)
                return "what it computes has not been measured";

            foreach (var key in RenderingImage.Keys(function, gathered, subjects, reads.Aggregate((a, b) => a.Intersect(b))).AsEnumerable())
            {
                FunctionKey[] covering = [.. rows.Keys.Select(r => r.Key).Distinct().Where(r => r with { Value = key.Value } == key && FunctionValueClasses.Holding.Contains((key.Kind, r.Value, key.Value)))];
                if (covering.Length == 0)
                    return key == RenderingImage.Malformed ? "an operand is no value S gathers, or is read through no reader its rows were measured through" : $"no row measured {key}";

                foreach (var row in covering)
                    foreach (var target in definition.Serves[set])
                        if ((rows.TryGetValue((row, target), out var answer) ? answer : CosmosFunctionAnswer.Unmeasured) is var held && held != CosmosFunctionAnswer.Same)
                            return $"{target} answers {row} {held}";
            }

            return null;
        }

        /// <summary>
        /// The rows' name of a call as the loader names it (<see cref="CosmosPredicateLoader.NameOf"/>): its operator's, a trim
        /// with its flag, or <c>null</c> for a trim whose flag is no literal.
        /// </summary>
        public static string? KeyOf(RexCall call) =>
            call.getKind().name() != "TRIM" ? CosmosPredicateLoader.NameOf(call.getOperator())
            : call.getOperands().size() == 3 && call.getOperands().get(0) is RexLiteral { } flag && flag.getValue() is org.apache.calcite.sql.fun.SqlTrimFunction.Flag f ? "TRIM " + f.name()
            : null;

        /// <summary>Whether the column gate holds a function to a verdict: rows name it, or it is a string function gated.</summary>
        public static bool Named(string function) => RowFunctions.Contains(function) || Gated.Contains(function);

        /// <summary>
        /// Determines whether a string function written by name has a verdict: rows the column gate reads, or a result that
        /// is the service's own.
        /// </summary>
        public static bool HasVerdict(string name) => Named(name) || Declared.Contains(name);

        /// <summary>
        /// What a call's spelling asks of its operands' characters beyond the rows, under a set (<see cref="CosmosRendering.StoresSpelled"/>).
        /// </summary>
        /// <remarks>
        /// Azure's search reads to its first U+0000; and under the emulator, which stores U+0000 and U+FDD0 escaped, a
        /// replacement's search and replacement, a concatenation's operands, a repetition's subject and a pattern's subject
        /// are asked to hold neither (#246). The rows say the functions' for S, keyed by operand (F2), a target that measured
        /// none answering it unmeasured; the bridge keeps this copy until it asks S's image (G6).
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
        /// trim's character, a repetition's count) are asked here, and a parameter is never one; what a search or a window's
        /// length answers is the rows'.
        /// </summary>
        /// <param name="call">The loader's call.</param>
        /// <param name="spell">An operand's spelling, or <c>null</c> where it has none.</param>
        public static string? Spell(SqlValue.Call call, Func<SqlValue, string?> spell)
        {
            // A literal the SDK refuses to send, one holding an unpaired surrogate, is never bound (Service.LoneSurrogateParameterIsNeverSent):
            // legality. What any other literal makes, U+0000 and U+FDD0 among them, is the function rows' (F2, G1).
            if (call.Operands.Any(o => o is SqlValue.Literal l && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.IsString(l.Value)
                && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.IsEncodable(Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.StringOf(l.Value)) == false))
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
                // A window from before the string, clipped as Calcite clips it, is the service's window from 0
                // (Calcite.SubstringClipsAWindowBeforeTheString, Service.SubstringOfANegativeStartIsEmpty). A length below 0,
                // which Calcite raises over, is its rows' (Service.StringFunctionAnswersByBand), as every length is.
                case "SUBSTRING" when operands.Length == 3 && Integral(1) <= 0:
                    return SubstringClipped(operands[0], operands[1], operands[2]);
                case "POSITION" when operands.Length == 2:
                    return Position(operands[0], operands[1]);
                case "TRIM BOTH" or "TRIM LEADING" or "TRIM TRAILING" when operands.Length == 2 && Text(0) == " ":
                    return $"{TrimOf(call.Function[5..])}({operands[1]})";
                // A positive count reads Azure's length bound, a result key no row holds yet (h14b).
                case "REPEAT" when operands.Length == 2 && Integral(1) == 0:
                    return $"REPLICATE({operands[0]}, {operands[1]})";
                // An empty search, which the service answers undefined, is the rows' (Service.StringFunctionAnswersByBand).
                case "REPLACE" when operands.Length == 3:
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
