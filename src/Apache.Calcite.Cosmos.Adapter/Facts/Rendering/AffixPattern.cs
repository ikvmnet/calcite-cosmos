using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What an affix or a <c>LIKE</c> asks, as one pattern of literal runs and <c>%</c> (spec §4.2 h07, the affix form):
    /// arithmetic on patterns and strings, which reads no row. Over a string holding no unpaired surrogate, which nothing a
    /// target stores holds, a pattern of runs matches by UTF-16 unit exactly where it matches by code point, which the rows
    /// measure (<c>Service.AffixesMatchByCodePoint</c>, <c>Service.LikeMatchesByCodePoint</c>), and as Calcite's runtime
    /// matches: so one matcher serves the cells, Calcite's side and the service's (<c>DESIGN.md</c>, <i>The affix form</i>).
    /// </summary>
    internal static class AffixPattern
    {

        /// <summary>
        /// The pattern a test asks with its argument: <c>STARTSWITH</c> the argument and <c>%</c>, <c>ENDSWITH</c> the reverse,
        /// <c>CONTAINS</c> it between two, <c>LIKE</c> its own; <c>null</c> where no pattern of runs says it, an argument
        /// holding a wildcard, an escape or a bracket among them.
        /// </summary>
        public static string? Of(CosmosServiceTest test, string argument) => test switch
        {
            CosmosServiceTest.StartsWith when Plain(argument) => Canonical(argument + "%"),
            CosmosServiceTest.EndsWith when Plain(argument) => Canonical("%" + argument),
            CosmosServiceTest.Contains when Plain(argument) => Canonical("%" + argument + "%"),
            CosmosServiceTest.Like => Canonical(argument),
            _ => null,
        };

        /// <summary>
        /// A <c>LIKE</c> pattern as runs and <c>%</c> alone, every run of <c>%</c> one; <c>null</c> for one holding <c>_</c>,
        /// which Calcite reads as one code point that is no line terminator and no row measures the service's of, a backslash,
        /// which either may read as an escape, or a bracket, which the service reads as a range.
        /// </summary>
        public static string? Canonical(string pattern) => Plain(pattern.Replace("%", "", StringComparison.Ordinal))
            ? string.Join("%", pattern.Split('%').Where((run, i) => i == 0 || run.Length > 0 || i == pattern.Split('%').Length - 1)) : null;

        /// <summary>Whether a pattern of runs matches a text, by UTF-16 unit (<see cref="PredicateAtom.Like"/>).</summary>
        public static bool Matches(string text, string pattern) => PredicateAtom.Like(text, pattern) == true;

        /// <summary>Whether a pattern matches every string: <c>%</c>.</summary>
        public static bool Universal(string pattern) => pattern == "%";

        /// <summary>The pattern's runs, in order; an empty one where it begins or ends with <c>%</c>.</summary>
        public static string[] Runs(string pattern) => pattern.Split('%');

        /// <summary>The shape of one run of text between two <c>%</c>, as a <c>CONTAINS</c> is written: what a truncation row names (F3).</summary>
        public const string OneRun = "onerun";

        /// <summary>The shape a truncation row may name of a pattern, read off its runs: <see cref="OneRun"/>, or none, the empty name.</summary>
        public static string ShapeOf(string pattern) => Runs(pattern) is ["", { Length: > 0 }, ""] ? OneRun : "";

        /// <summary>
        /// Whether a pattern matches what the emulator stores of a string (<see cref="EmulatorStoredText"/>, U+0000 as U+FDD0
        /// then <c>0</c> and U+FDD0 as U+FDD0 then <c>1</c>) exactly where it matches the string: it holds neither character,
        /// so no run splits or holds an escape, and no run but one the string must begin with begins with <c>0</c> or
        /// <c>1</c>, an escape's second unit, so none begins inside one. The emulator's affixes match what it stores against
        /// what is sent (<c>Service.EmulatorComparesTheSpellingItIsSent</c>).
        /// </summary>
        public static bool StoredAlike(string pattern) => EmulatorStoredText.Escapes(pattern) == false
            && Runs(pattern).Select((run, i) => (run, i)).All(r => r.i == 0 || r.run.Length == 0 || r.run[0] is not ('0' or '1'));

        /// <summary>
        /// The test and argument a pattern is written as, the narrowest that says it: the pattern of no <c>%</c> is no affix
        /// (<c>null</c>, an equality); <c>%</c> alone <c>STARTSWITH</c> of the empty string; one run after <c>%</c>
        /// <c>ENDSWITH</c>, before it <c>STARTSWITH</c>, between two <c>CONTAINS</c>; any other <c>LIKE</c>.
        /// </summary>
        public static (CosmosServiceTest Test, string Argument)? Written(string pattern) => Runs(pattern) switch
        {
            [_] => null,
            ["", ""] => (CosmosServiceTest.StartsWith, ""),
            [var start, ""] => (CosmosServiceTest.StartsWith, start),
            ["", var end] => (CosmosServiceTest.EndsWith, end),
            ["", var run, ""] => (CosmosServiceTest.Contains, run),
            _ => (CosmosServiceTest.Like, pattern),
        };

        /// <summary>
        /// The test a <c>LIKE</c>'s pattern is written as (<see cref="Written"/>): <see cref="CosmosServiceTest.Equal"/> for one
        /// of no <c>%</c>, and <see cref="CosmosServiceTest.Like"/> for one no pattern of runs says.
        /// </summary>
        public static CosmosServiceTest WrittenTest(string pattern) => Canonical(pattern) is not { } canonical ? CosmosServiceTest.Like
            : Written(canonical) is var (test, _) ? test : CosmosServiceTest.Equal;

        /// <summary>The argument a <c>LIKE</c>'s pattern is written with (<see cref="Written"/>): the pattern itself where no affix says it.</summary>
        public static string WrittenArgument(string pattern) => Canonical(pattern) is { } canonical && Written(canonical) is var (_, argument) ? argument : pattern;

        /// <summary>Text holding no wildcard, escape or bracket, and no unpaired surrogate, which no statement sends.</summary>
        static bool Plain(string text) => text.IndexOfAny(['%', '_', '\\', '[', ']']) < 0 && JsonValues.IsEncodable(text);

    }

}
