using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What a pushed string function computes against Calcite's, decided in one place: for each function the translator
    /// writes, the classes of character (<see cref="JsonCharacterBand"/>) its counted arguments have to be proved free of
    /// for the service's answer to be Calcite's, and which operands of a computed argument its characters come from.
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
    /// <item><description><b>Nothing, for the rest.</b> <c>INDEX_OF</c> counts units on Azure, so <c>POSITION</c> is
    /// Calcite's there over every string, and is pushed; the emulator counts code points, a difference recorded
    /// (<c>Service.IndexOfCountsUnits</c>) and not declined. <c>REVERSE</c>, <c>CONCAT</c>, <c>REPLACE</c>,
    /// <c>REPEAT</c>, plain <c>LIKE</c> and <c>STARTSWITH</c> agree everywhere.</description></item>
    /// </list>
    /// <para>
    /// <b>The rule asks the facts, and decides nothing about the documents.</b> What classes a path's strings may hold is
    /// the theory's <c>chars</c>, read off the facts the translation was licensed (<see cref="CosmosRexTranslator.Facts"/>):
    /// a declared <c>pattern</c>, an <c>enum</c> or a filter's <c>=</c> and <c>IN</c>, a stored form, or a service fact
    /// states it. A literal's are its own, read as the theory reads a value's. A parameter's are anything until it is
    /// bound. A computed argument is asked through every input its characters come from (<see cref="Sources"/>): each has
    /// to be free of the class, which is a conjunction of questions and not a fact combined here. Where any is not proved,
    /// the function stays in process, which is where it was computed over a path nothing is declared of.
    /// </para>
    /// <para>
    /// <b>And what cannot be sent at all.</b> A value holding a lone surrogate is refused by the SDK's UTF-8 encoder
    /// before the statement leaves (<c>Service.LoneSurrogateParameterIsNeverSent</c>): only a <c>U&amp;</c> literal can
    /// hold one, and Calcite answers the statement in process. <see cref="CosmosParameterList.Add"/> asks
    /// <see cref="IsEncodable"/> of every value the statement would carry.
    /// </para>
    /// </remarks>
    internal static class CosmosStringFunctions
    {

        /// <summary>
        /// The counts that agree with Calcite's only where no counted string holds a surrogate.
        /// </summary>
        const string Counts = "the service counts code points (LENGTH) or, on Azure, UTF-16 units with a split pair sent as U+FFFD (SUBSTRING, LEFT, RIGHT), where Calcite counts units and keeps a lone surrogate";

        /// <summary>
        /// The case mappings, which agree with Calcite's over ASCII alone.
        /// </summary>
        const string Cases = "the service maps case by a simple mapping and Calcite by Java's full one, which agree over ASCII alone";

        /// <summary>
        /// The trims, which agree with Calcite's where nothing the service trims beside a space is held.
        /// </summary>
        const string Trims = "the service's trim removes white space and Calcite's U+0020 alone";

        /// <summary>
        /// Returns what a function asks of its arguments' characters for the service's answer to be Calcite's, or
        /// <c>null</c> where it asks nothing.
        /// </summary>
        /// <param name="call">The function.</param>
        /// <returns>The classes no counted argument's input may hold, the operands counted, and why.</returns>
        public static (JsonCharacterBand Refused, int[] Counted, string Why)? RuleOf(RexCall call)
        {
            if (call.getKind().name() == "TRIM")
                return (JsonCharacterBand.Trimmed, [2], Trims);

            return call.getOperator().getName() switch
            {
                "CHAR_LENGTH" or "CHARACTER_LENGTH" or "LENGTH" or "SUBSTRING" or "LEFT" or "RIGHT" => (JsonCharacterBand.Surrogates, [0], Counts),
                "UPPER" or "LOWER" => (JsonCharacterBand.BeyondAscii, [0], Cases),
                "LTRIM" or "RTRIM" or "BTRIM" => (JsonCharacterBand.Trimmed, [0], Trims),
                _ => null,
            };
        }

        /// <summary>
        /// Returns the operands a string-valued call's characters are drawn from, so that a function over it is asked of
        /// those; <c>null</c> where they may come from anywhere.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each of these answers a string whose every character is a character of one of these operands, at the service
        /// as in Calcite, wherever the rule its own position asks holds: a concatenation, a repetition, a reversal, a
        /// replacement's subject and replacement; a window, a trim and a case mapping, which remove or map characters
        /// within the classes asked of them; a conditional's arms; a cast to text of a string. A split pair Azure sends as
        /// U+FFFD comes only of a surrogate, which is refused before it can matter to a count, and Java's full mapping keeps
        /// every character of the basic plane in it.
        /// </para>
        /// <para>
        /// Anything else — a conversion such as <c>ToString</c>, a JSON function, a function this list does not name —
        /// may make characters of its own, and an argument computed by one is not proved. A value that is not text has
        /// none to give, and is asked nothing (<see cref="CosmosRexTranslator"/>).
        /// </para>
        /// </remarks>
        /// <param name="call">The call.</param>
        /// <returns>The operands, or <c>null</c>.</returns>
        public static IEnumerable<RexNode>? Sources(RexCall call)
        {
            var count = call.getOperands().size();

            switch (call.getKind().name())
            {
                case "TRIM":
                    return count == 3 ? [Operand(call, 1), Operand(call, 2)] : null;
                case "CASE":
                    return Arms(call);
                case "CAST" or "SAFE_CAST":
                    return count == 1 ? [Operand(call, 0)] : null;
            }

            return call.getOperator().getName() switch
            {
                "||" or "CONCAT" or "COALESCE" => Operands(call, 0, count),
                "UPPER" or "LOWER" or "REVERSE" or "SUBSTRING" or "LEFT" or "RIGHT" or "REPEAT" or "LTRIM" or "RTRIM" or "BTRIM" when count >= 1 => [Operand(call, 0)],
                "REPLACE" when count == 3 => [Operand(call, 0), Operand(call, 2)],
                _ => null,
            };
        }

        /// <summary>
        /// Determines whether a text the case-insensitive match is handed folds alike at both ends: ASCII, read as the
        /// theory reads a value's characters.
        /// </summary>
        /// <param name="text">The text.</param>
        public static bool FoldsAlike(string text) =>
            (JsonFactEncoding.Characters(text) & JsonCharacterBand.BeyondAscii) == JsonCharacterBand.None;

        /// <summary>
        /// Determines whether a value can be sent: whether every string in it, at any depth, holds no unpaired surrogate,
        /// which the SDK's UTF-8 encoder refuses.
        /// </summary>
        /// <param name="value">The value a statement would carry.</param>
        public static bool IsEncodable(object? value)
        {
            switch (value)
            {
                case string text:
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                            i++;
                        else if (char.IsSurrogate(text[i]))
                            return false;
                    }

                    return true;

                case IDictionary<string, object?> members:
                    foreach (var (name, member) in members)
                        if (IsEncodable(name) == false || IsEncodable(member) == false)
                            return false;

                    return true;

                case System.Collections.IEnumerable items:
                    foreach (var item in items)
                        if (IsEncodable(item) == false)
                            return false;

                    return true;

                default:
                    return true;
            }
        }

        static RexNode Operand(RexCall call, int index) => (RexNode)call.getOperands().get(index);

        static IEnumerable<RexNode> Operands(RexCall call, int from, int to)
        {
            for (var i = from; i < to; i++)
                yield return Operand(call, i);
        }

        /// <summary>
        /// A <c>CASE</c>'s arms, which are its value, and not its conditions, which only choose one.
        /// </summary>
        static IEnumerable<RexNode> Arms(RexCall call)
        {
            var count = call.getOperands().size();
            for (var i = 1; i < count; i += 2)
                yield return Operand(call, i);

            if (count % 2 == 1)
                yield return Operand(call, count - 1);
        }

    }

}
