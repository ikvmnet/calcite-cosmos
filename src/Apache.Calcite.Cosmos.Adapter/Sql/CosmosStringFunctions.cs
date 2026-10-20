using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What a pushed string function computes against Calcite's, decided in one place: for each string function the
    /// translator writes, the classes of character (<see cref="JsonCharacterBand"/>) the operands it asks have to be proved
    /// free of for the service's answer to be Calcite's, and what else the service was measured to need of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each function was measured at both ends, and each part is a class.</b> Calcite's string functions are Java's,
    /// and the service's are its own (<c>reports/verify-strings.md</c>; the rows of <see cref="CosmosAxioms"/> under
    /// <em>What a string function computes</em>):
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>A count, over the surrogates.</b> <c>LENGTH</c> counts code points at the service, where
    /// Calcite's <c>CHAR_LENGTH</c> counts UTF-16 units. Azure's <c>SUBSTRING</c>, <c>LEFT</c> and <c>RIGHT</c> count
    /// units and send a pair a window splits back as U+FFFD, where Calcite's keep a lone surrogate; the emulator's count
    /// code points. Without a surrogate a unit is a code point, and every one of them agreed on every string measured,
    /// U+E000, U+FFFD and a combining sequence among them.</description></item>
    /// <item><description><b>A case mapping, outside ASCII.</b> Calcite maps case by Java's full mapping — ß to
    /// <c>SS</c>, U+FB00 to <c>FF</c> — and the services by simple ones that leave more unmapped, Azure's more than the
    /// emulator's; over ASCII the three agree. The case-insensitive <c>LIKE</c> rewrites fold under the service's mapping
    /// too, and lose the rows Calcite's fold keeps, which no recheck can bring back, so they ask what <c>UPPER</c> and
    /// <c>LOWER</c> ask, of the pattern's text as of the folded value (<see cref="FoldsAlike"/>).</description></item>
    /// <item><description><b>A trim, over the trimmed class.</b> The service's <c>TRIM</c>, <c>LTRIM</c> and
    /// <c>RTRIM</c> remove white space, and Calcite's U+0020 alone.</description></item>
    /// <item><description><b>A search, over U+0000.</b> Azure's <c>REPLACE</c> and <c>CONTAINS</c>, and the
    /// <c>LIKE</c> it answers as a <c>CONTAINS</c>, read a string only up to its first U+0000, and a search holding one
    /// matches every string (<c>Service.NulEndsWhatReplaceAndContainsRead</c>).</description></item>
    /// <item><description><b>Nothing of the characters, for the rest.</b> <c>INDEX_OF</c> counts units on Azure, so
    /// <c>POSITION</c> is Calcite's there over every string, and is pushed; the emulator counts code points, a difference
    /// recorded (<c>Service.IndexOfCountsUnits</c>) and not declined. <c>REVERSE</c> and <c>CONCAT</c> agree projected
    /// everywhere; Azure's <c>REVERSE</c> compared in a <c>WHERE</c> with a parameter holding a supplementary character
    /// keeps nothing, which nothing pushes (<c>TODO.md</c>). <c>REPEAT</c> and <c>REPLACE</c> ask of what is not a
    /// character: a count and a length that keep a repetition within the service's limit, and a search that is not empty
    /// (<see cref="RepeatsAsCalcite"/>, <see cref="SearchesSomething"/>).</description></item>
    /// </list>
    /// <para>
    /// <b>The rule asks the facts, and decides nothing about the documents.</b> What classes an operand may hold is the
    /// theory's, read off the facts the translation was licensed (<see cref="CosmosRexTranslator.Facts"/>,
    /// <see cref="JsonFactSet.CharactersOf(SqlValue)"/>): a path's <c>chars</c>, which a declared <c>pattern</c>, an
    /// <c>enum</c> or a filter's <c>=</c> and <c>IN</c>, a stored form or a service fact state; a literal's own; and a
    /// computed value's, which the theory derives from what its function draws from each operand. A parameter's are
    /// anything until it is bound. Where one is not proved, the function stays in process, which is where it was
    /// computed over a path nothing is declared of.
    /// </para>
    /// <para>
    /// <b>Every string function the translator writes has a verdict here</b> (<see cref="HasVerdict"/>), the ones that ask
    /// nothing of the characters among them, and one written by name with none is refused: a function added to the
    /// translator's table is computed in process until what it asks has been said.
    /// </para>
    /// <para>
    /// <b>And what cannot be sent at all.</b> A value holding a lone surrogate is refused by the SDK's UTF-8 encoder
    /// before the statement leaves (<c>Service.LoneSurrogateParameterIsNeverSent</c>): only a <c>U&amp;</c> literal can
    /// hold one, and Calcite answers the statement in process. <see cref="CosmosParameterList.Add"/> asks
    /// <see cref="Client.CosmosEncodable.IsEncodable"/> of every value the statement would carry.
    /// </para>
    /// </remarks>
    internal static class CosmosStringFunctions
    {

        /// <summary>
        /// What a function asks of the characters of some of its operands: the classes refused, what they are called, the
        /// operands asked, by position, and why.
        /// </summary>
        public sealed record Rule(JsonCharacterBand Refused, string Named, int[] Asked, string Why);

        /// <summary>
        /// The classes a case mapping asks its argument to be free of: everything outside ASCII, where Java's full mapping
        /// and the service's simple ones part. Named once, for the mapping and for the case-insensitive match a fold
        /// beneath a pattern is written as (<see cref="FoldsAlike"/>).
        /// </summary>
        const JsonCharacterBand CaseMapped = JsonCharacterBand.BeyondAscii;

        static readonly Rule Counted = new(JsonCharacterBand.Surrogates, "surrogate", [0],
            "the service counts code points (LENGTH) or, on Azure, UTF-16 units with a split pair sent as U+FFFD (SUBSTRING, LEFT, RIGHT), where Calcite counts units and keeps a lone surrogate");

        static readonly Rule Mapped = new(CaseMapped, "character outside ASCII", [0],
            "the service maps case by a simple mapping and Calcite by Java's full one, which agree over ASCII alone");

        static readonly Rule Trimmed = new(JsonCharacterBand.Trimmed, "character the service trims beside a space", [2],
            "the service's trim removes white space and Calcite's U+0020 alone");

        static readonly Rule Searched = new(JsonCharacterBand.Nul, "U+0000", [0, 1],
            "the service's REPLACE, CONTAINS and the LIKE it answers as a CONTAINS read a string only up to its first U+0000, and a search holding one matches every string");

        /// <summary>
        /// Every string function the translator writes by name, and what it asks of its operands' characters; <c>null</c>
        /// for one that asks nothing of them. <c>TRIM</c> and <c>LIKE</c> are read by their kind (<see cref="RuleOf"/>).
        /// </summary>
        static readonly Dictionary<string, Rule?> Verdicts = new(StringComparer.Ordinal)
        {
            ["CHAR_LENGTH"] = Counted,
            ["CHARACTER_LENGTH"] = Counted,
            ["LENGTH"] = Counted,
            ["SUBSTRING"] = Counted,
            ["LEFT"] = Counted,
            ["RIGHT"] = Counted,
            ["UPPER"] = Mapped,
            ["LOWER"] = Mapped,
            ["REPLACE"] = Searched,

            // Azure's INDEX_OF counts units as Calcite does.
            ["POSITION"] = null,

            // A reversal and a concatenation keep every character as it is at both ends; a repetition does too, and its
            // count is asked apart.
            ["REVERSE"] = null,
            ["CONCAT"] = null,
            ["||"] = null,
            ["REPEAT"] = null,

            // The service's own function, which Calcite has no body for: what the service computes is what it means.
            ["ToString"] = null,
        };

        /// <summary>
        /// Returns what a function asks of its operands' characters for the service's answer to be Calcite's, or
        /// <c>null</c> where it asks nothing of them.
        /// </summary>
        /// <param name="call">The function.</param>
        public static Rule? RuleOf(RexCall call)
        {
            switch (call.getKind().name())
            {
                case "TRIM":
                    return Trimmed;

                // The service answers one run of text between two `%` as a CONTAINS, which reads the subject only up to its
                // first U+0000 and, seeking one, matches everything; every other pattern was measured to read past it.
                // Whatever the pattern is written as — the case-insensitive CONTAINS of a fold beneath it among them — the
                // subject's characters, and the pattern's own, are asked here.
                case "LIKE" when call.getOperands().size() == 2 && call.getOperands().get(1) is RexLiteral { } literal && literal.getValue() is org.apache.calcite.util.NlsString pattern && IsContainment(pattern.getValue()):
                    return Searched;
            }

            return Verdicts.TryGetValue(call.getOperator().getName(), out var rule) ? rule : null;
        }

        /// <summary>
        /// Determines whether a string function written by name has been given a verdict: what it asks of its operands'
        /// characters, or that it asks nothing.
        /// </summary>
        /// <param name="name">The function's name, as the translator's table maps it.</param>
        public static bool HasVerdict(string name) => Verdicts.ContainsKey(name);

        /// <summary>
        /// The most code points the service's <c>REPLICATE</c> answers, inclusive: past them it is undefined on Azure, where
        /// Calcite's <c>REPEAT</c> repeats (<c>Service.ReplicateStopsAtTenThousandCodePoints</c>). The emulator has no limit.
        /// </summary>
        public const long RepetitionLimit = 10_000;

        /// <summary>
        /// Determines whether the service's <c>REPLICATE</c> answers what Calcite's <c>REPEAT</c> does for a count and a
        /// subject at most so many code points long: a count of 0 is the empty string at both, a negative one undefined at
        /// the service and empty in Calcite, and a positive one Calcite's wherever the repetition stays within the limit.
        /// </summary>
        /// <param name="count">The count, a literal.</param>
        /// <param name="most">The most code points the subject holds, or <c>null</c> where nothing bounds it; a count of
        /// UTF-16 units bounds it from above.</param>
        public static bool RepeatsAsCalcite(long count, long? most) =>
            count == 0 || count > 0 && most is long length && length <= RepetitionLimit / count;

        /// <summary>
        /// Determines whether the service's <c>REPLACE</c> answers Calcite's for a search at least so many units long: of
        /// an empty search it is undefined, where Calcite's is the subject (<c>Service.ReplaceOfAnEmptySearchIsUndefined</c>).
        /// </summary>
        /// <param name="least">The fewest units the search holds, or <c>null</c> where nothing bounds it.</param>
        public static bool SearchesSomething(long? least) => least >= 1;

        /// <summary>
        /// Determines whether a <c>LIKE</c> pattern is one the service answers as a <c>CONTAINS</c>: one run of text with no
        /// wildcard, after one or more <c>%</c> and before one or more. Measured on the probe account, <c>%b%</c>,
        /// <c>%%b%</c> and <c>%b%%</c> lose <c>a</c>U+0000<c>b</c> and <c>%</c>U+0000<c>%</c> keeps every string, where
        /// <c>%a%b%</c>, <c>%_b%</c>, <c>%b_%</c>, <c>%b</c>, <c>a%</c>, <c>a%b</c> and <c>a_b</c> answer as Calcite's.
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

        /// <summary>
        /// Determines whether a text the case-insensitive match is handed folds alike at both ends: ASCII, read as the
        /// theory reads a value's characters.
        /// </summary>
        /// <param name="text">The text.</param>
        public static bool FoldsAlike(string text) =>
            (JsonFactEncoding.Characters(text) & CaseMapped) == JsonCharacterBand.None;

    }

}
