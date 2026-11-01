using System;
using System.Text;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// How the emulator spells a string it stores: each U+0000 as U+FDD0 followed by <c>0</c>, and each U+FDD0 as U+FDD0
    /// followed by <c>1</c>. Every other unit is stored as written, so a string holding neither is its own spelling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the emulator does with what it is sent, measured on both images</b> (<c>Service.StringOrderIsCodePoint</c>,
    /// <c>Service.EmulatorComparesTheSpellingItIsSent</c>). A path is compared, ordered, counted, cut, reversed and searched
    /// in this spelling; a parameter, a literal and a constant built from them are compared as sent, unescaped, against
    /// it. So a value sent as <see cref="Of"/> of itself compares with a stored string as Calcite compares the two for
    /// <c>=</c>, <c>IN</c>, <c>!=</c> and <c>STARTSWITH</c>, and for <c>ENDSWITH</c> and <c>CONTAINS</c> unless the value
    /// begins with <c>0</c> or <c>1</c>, which can match an escape's second unit. What comes back is decoded wherever a
    /// whole escape pair comes back, a constant sent in this spelling included.
    /// </para>
    /// <para>
    /// <b>The spelling keeps code-point order everywhere but at U+0000.</b> U+FDD0 <c>1</c> sorts where U+FDD0 does among
    /// strings holding no U+0000, so U+FDD0 is an equality and spelling defect alone. U+FDD0 <c>0</c> puts U+0000 above
    /// every unit from U+0001 to U+FDCF, U+E000 among them, where Calcite puts it least.
    /// </para>
    /// <para>
    /// Azure stores every string as written: nothing outside the emulator reads this.
    /// </para>
    /// </remarks>
    internal static class EmulatorStoredText
    {

        /// <summary>The unit the emulator's escapes begin with, U+FDD0, a noncharacter.</summary>
        public const char Escape = '﷐';

        /// <summary>
        /// The emulator's spelling of a string: each U+0000 as U+FDD0 <c>0</c>, each U+FDD0 as U+FDD0 <c>1</c>, the rest as
        /// written. The string itself where it holds neither.
        /// </summary>
        public static string Of(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            if (Escapes(text) == false)
                return text;

            var stored = new StringBuilder(text.Length + 4);
            foreach (var unit in text)
            {
                if (unit == '\0')
                    stored.Append(Escape).Append('0');
                else if (unit == Escape)
                    stored.Append(Escape).Append('1');
                else
                    stored.Append(unit);
            }

            return stored.ToString();
        }

        /// <summary>Whether the emulator spells a string otherwise than as written: whether it holds U+0000 or U+FDD0.</summary>
        public static bool Escapes(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            return text.AsSpan().IndexOfAny('\0', Escape) >= 0;
        }

    }

}
