using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What an affix or a <c>LIKE</c> asks, as one pattern (spec §4.2 h07, the affix form; G7): an affix test's argument by
    /// the test's definition, literal, and a <c>LIKE</c>'s by each side's rows (<see cref="LikePattern"/>), so which units a
    /// pattern may hold is the rows', not a list. Spelled for the cells as <c>%</c> a run, <c>_</c> one code point, a
    /// backslash before a <c>%</c>, a <c>_</c> or a backslash it makes literal, and any other unit itself, matched by code
    /// point (<c>DESIGN.md</c>, <i>The affix form</i>). Arithmetic on patterns and strings, which reads no row.
    /// </summary>
    internal static class AffixPattern
    {

        /// <summary>
        /// The pattern a test asks with its argument: <c>STARTSWITH</c> the argument and a run, <c>ENDSWITH</c> the reverse,
        /// <c>CONTAINS</c> it between two, each unit of the argument itself; <c>LIKE</c> its own, as the rows handed read each
        /// unit (<see cref="LikePattern.Read"/>), <c>null</c> where they leave one unread; any other test none.
        /// </summary>
        public static LikePattern? Of(CosmosServiceTest test, string argument, ValueSet<LikeUnit> units) => test switch
        {
            CosmosServiceTest.StartsWith => LikePattern.Affix(false, argument, true),
            CosmosServiceTest.EndsWith => LikePattern.Affix(true, argument, false),
            CosmosServiceTest.Contains => LikePattern.Affix(true, argument, true),
            CosmosServiceTest.Like => LikePattern.Read(argument, units),
            _ => null,
        };

        /// <summary>Text spelled as literal units: each <c>%</c>, <c>_</c> and backslash after a backslash.</summary>
        public static string Literal(string text) => text.IndexOfAny(['%', '_', '\\']) < 0 ? text
            : string.Concat(text.Select(c => c is '%' or '_' or '\\' ? "\\" + c : c.ToString()));

        /// <summary>A string's code points, each spelled as the string holds it; an unpaired surrogate one of its own.</summary>
        public static IEnumerable<string> CodePoints(string text)
        {
            for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
                yield return text.Substring(i, char.IsSurrogatePair(text, i) ? 2 : 1);
        }

        /// <summary>A spelling's units: a run (<c>%</c>), one code point (<c>_</c>), or a literal code point, a backslash's next among them.</summary>
        static IEnumerable<(char Kind, string Unit)> Tokens(string spelled)
        {
            var escaped = false;
            foreach (var point in CodePoints(spelled))
            {
                if (escaped == false && point == "\\")
                {
                    escaped = true;
                    continue;
                }

                yield return escaped || point is not ("%" or "_") ? ('l', point) : (point[0], "");
                escaped = false;
            }
        }

        /// <summary>
        /// Whether a spelled pattern matches a text by code point, a run any run and <c>_</c> any one code point: what the
        /// cells are cut by. A pattern of runs alone is matched by its runs, its first a prefix, its last a suffix and each
        /// between found in order after the one before.
        /// </summary>
        public static bool Matches(string text, string spelled)
        {
            var runs = Runs(spelled).Select(Unescaped).ToArray();
            if (runs.All(r => r is not null && JsonValues.IsEncodable(r)))
                return MatchesRuns(text, runs!);

            var tokens = Tokens(spelled).ToList();
            var points = CodePoints(text).ToList();
            var reach = new bool[points.Count + 1];
            reach[0] = true;
            foreach (var (kind, unit) in tokens)
            {
                var next = new bool[points.Count + 1];
                for (var j = 0; j <= points.Count; j++)
                    next[j] = kind switch
                    {
                        '%' => reach[j] || j > 0 && next[j - 1],
                        '_' => j > 0 && reach[j - 1],
                        _ => j > 0 && reach[j - 1] && points[j - 1] == unit,
                    };

                reach = next;
            }

            return reach[points.Count];
        }

        static bool MatchesRuns(string text, string[] runs)
        {
            if (runs.Length == 1)
                return string.Equals(text, runs[0], StringComparison.Ordinal);

            if (text.Length < runs[0].Length + runs[^1].Length || text.StartsWith(runs[0], StringComparison.Ordinal) == false || text.EndsWith(runs[^1], StringComparison.Ordinal) == false)
                return false;

            var (at, end) = (runs[0].Length, text.Length - runs[^1].Length);
            foreach (var run in runs[1..^1])
            {
                var found = text.IndexOf(run, at, end - at, StringComparison.Ordinal);
                if (found < 0)
                    return false;

                at = found + run.Length;
            }

            return true;
        }

        /// <summary>Whether a pattern matches every string: <c>%</c>.</summary>
        public static bool Universal(string spelled) => spelled == "%";

        /// <summary>The pattern's runs, in order, as spelled; an empty one where it begins or ends with <c>%</c>.</summary>
        public static string[] Runs(string spelled)
        {
            var (runs, run) = (new List<string>(), new StringBuilder());
            foreach (var (kind, unit) in Tokens(spelled))
            {
                if (kind == '%')
                {
                    runs.Add(run.ToString());
                    run.Clear();
                }
                else
                    run.Append(kind == '_' ? "_" : Literal(unit));
            }

            runs.Add(run.ToString());
            return [.. runs];
        }

        /// <summary>A spelled run's text, each unit literal; <c>null</c> where it holds a <c>%</c> or a <c>_</c>.</summary>
        public static string? Unescaped(string spelled)
        {
            var text = new StringBuilder();
            foreach (var (kind, unit) in Tokens(spelled))
            {
                if (kind != 'l')
                    return null;

                text.Append(unit);
            }

            return text.ToString();
        }

        /// <summary>A spelled pattern's text with no run, each <c>_</c> the filler: a string it matches, where the filler is one code point.</summary>
        public static string Filled(string spelled, string filler) =>
            string.Concat(Tokens(spelled).Select(t => t.Kind switch { '%' => "", '_' => filler, _ => t.Unit }));

        /// <summary>Whether a spelled pattern holds <c>_</c>.</summary>
        public static bool HoldsOne(string spelled) => Tokens(spelled).Any(t => t.Kind == '_');

        /// <summary>The literal units of a spelled pattern, in order, with no run and no <c>_</c>.</summary>
        public static string LiteralUnits(string spelled) => string.Concat(Tokens(spelled).Where(t => t.Kind == 'l').Select(t => t.Unit));

        /// <summary>The shape of one run of text between two <c>%</c>, as a <c>CONTAINS</c> is written: what a truncation row names (F3).</summary>
        public const string OneRun = "onerun";

        /// <summary>
        /// The shape a truncation row may name of a pattern, read off its runs: <see cref="OneRun"/> where one run, a
        /// <c>_</c> in it or not, lies between two <c>%</c>; or none, the empty name.
        /// </summary>
        public static string ShapeOf(string spelled) => Runs(spelled) is ["", { Length: > 0 }, ""] ? OneRun : "";

        /// <summary>
        /// Whether a pattern matches what the emulator stores of a string (<see cref="EmulatorStoredText"/>, U+0000 as U+FDD0
        /// then <c>0</c> and U+FDD0 as U+FDD0 then <c>1</c>) exactly where it matches the string: it holds neither character
        /// and no <c>_</c>, which might read one unit of an escape, so no unit splits or holds an escape, and no run but one the
        /// string must begin with begins with <c>0</c> or <c>1</c>, an escape's second unit, so none begins inside one. The
        /// emulator's affixes match what it stores against what is sent (<c>Service.EmulatorComparesTheSpellingItIsSent</c>).
        /// </summary>
        public static bool StoredAlike(string spelled) => HoldsOne(spelled) == false && EmulatorStoredText.Escapes(LiteralUnits(spelled)) == false
            && Runs(spelled).Select((run, i) => (run: Unescaped(run) ?? "", i)).All(r => r.i == 0 || r.run.Length == 0 || r.run[0] is not ('0' or '1'));

        /// <summary>
        /// The argument a test says a pattern as read with, where it says it (B1): <c>LIKE</c> the pattern as written, whatever
        /// it holds; and where each run is of literal units, the equality its text where it is one run, <c>STARTSWITH</c> its
        /// run where <c>%</c> alone follows it, <c>ENDSWITH</c> where <c>%</c> alone precedes it and <c>CONTAINS</c> where it
        /// lies between two; none where the test says it not. Which of those the gate holds is sent is S's best's.
        /// </summary>
        public static string? Says(CosmosServiceTest test, LikePattern pattern)
        {
            if (test == CosmosServiceTest.Like)
                return pattern.Written;

            var runs = Runs(pattern.Spelled).Select(Unescaped).ToArray();
            if (runs.Any(r => r is null))
                return null;

            return (test, runs) switch
            {
                (CosmosServiceTest.Equal, [var text]) => text,
                (CosmosServiceTest.StartsWith, [var start, ""]) => start,
                (CosmosServiceTest.EndsWith, ["", var end]) => end,
                (CosmosServiceTest.Contains, ["", var run, ""]) => run,
                _ => null,
            };
        }

        /// <summary>The fold's subject, as a fold's candidate names it: the translator writes the operand it is asked over in its place.</summary>
        public const string FoldSubject = "subject";

        /// <summary>
        /// The tests the service asks case-insensitively, by a third operand: <c>STARTSWITH</c>, <c>ENDSWITH</c> and
        /// <c>CONTAINS</c>, the one shape a fold's form is written in (legality, not a reading).
        /// </summary>
        static readonly ValueSet<CosmosServiceTest> CaseInsensitive = ValueSet.Of(CosmosServiceTest.StartsWith, CosmosServiceTest.EndsWith, CosmosServiceTest.Contains);

        /// <summary>
        /// A test's candidate for a <c>LIKE</c> over a fold (<c>foldedas</c>, B1), as the <c>WHERE</c>'s affix templates propose
        /// one per test: its form over the fold's subject, of the argument it says the pattern with as Calcite's units read it
        /// (<see cref="Says"/>), priced as a template's form is (<see cref="Priced"/>); <see cref="Priced.None"/> where it says
        /// it not, the units leave the pattern unread, or the test has no case-insensitive form to be written in.
        /// </summary>
        public static Priced Folded(CosmosServiceTest test, string pattern, ReadingTable rows) =>
            CaseInsensitive.Contains(test) && LikePattern.Read(pattern, rows.Units) is { } read && Says(test, read) is { } argument
                ? Priced.Of(ServiceForm.AffixOf(test, FoldSubject, argument)) : Priced.None;

        /// <summary>A fold's candidate's argument (<see cref="Folded"/>); the empty string for <see cref="Priced.None"/>, which no rule keeps.</summary>
        public static string FoldedArgument(Priced candidate) => candidate.Form?.Literal is { } literal ? JsonValues.StringOf(literal) : "";

    }

}
