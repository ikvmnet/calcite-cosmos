using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// How the emulator spells a string it stores: each U+0000 as U+FDD0 followed by <c>0</c>, and each U+FDD0 as U+FDD0
    /// followed by <c>1</c>. Every other unit is stored as written, so a string holding neither is its own spelling.
    /// </summary>
    /// <remarks>
    /// The spelling keeps code-point order everywhere but at U+0000, which it puts above every unit from U+0001 to
    /// U+FDCF where Calcite puts it least (<c>Service.StringOrderIsCodePoint</c>); what the emulator compares, counts and
    /// returns in it is <c>Service.EmulatorComparesTheSpellingItIsSent</c> and the rows beside it. Azure stores every
    /// string as written: nothing outside the emulator reads this.
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

        /// <summary>
        /// Whether a value to send holds a string the emulator spells otherwise than as written, at any depth: a member's
        /// name or value, or an element.
        /// </summary>
        public static bool Escapes(object? value)
        {
            switch (value)
            {
                case string text:
                    return Escapes(text);
                case IDictionary<string, object?> members:
                    foreach (var (name, member) in members)
                        if (Escapes(name) || Escapes(member))
                            return true;

                    return false;
                case IEnumerable items:
                    foreach (var item in items)
                        if (Escapes(item))
                            return true;

                    return false;
                default:
                    return false;
            }
        }

    }

}
