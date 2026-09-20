using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The liberties taken with a declared <c>pattern</c> before any form is read out of it, each one
    /// an identity rather than a guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whitespace outside a character class means nothing in an unextended ECMA-262 pattern, which is
    /// the dialect JSON Schema specifies; <c>\d</c> is exactly <c>[0-9]</c> there; a character class
    /// is a <em>set</em>, so the order its members are written in carries no meaning; and for an atom
    /// matching exactly one character, <c>A{m}A{n}</c> and <c>A{m+n}</c> accept the same strings.
    /// Four rewrites, and each collapses an axis along which one language is written several ways.
    /// </para>
    /// <para>
    /// Anything else — a different quantifier, a looser class, a missing anchor — is a different
    /// language and is left exactly as written, which costs a spelling and never mistakes one.
    /// </para>
    /// </remarks>
    static class CosmosPatternLanguage
    {

        /// <summary>
        /// Splits an expression at the <c>|</c>s that are not inside a group or a character class.
        /// </summary>
        /// <param name="pattern">The expression.</param>
        /// <returns>The branches, which is a one-element list where there is no alternation.</returns>
        internal static List<string> SplitAlternatives(string pattern)
        {
            var parts = new List<string>();
            var depth = 0;
            var characters = false;
            var start = 0;

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (characters)
                {
                    if (c == ']')
                        characters = false;

                    continue;
                }

                switch (c)
                {
                    case '[':
                        characters = true;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case '|' when depth == 0:
                        parts.Add(pattern.Substring(start, i - start));
                        start = i + 1;
                        break;
                }
            }

            parts.Add(pattern.Substring(start));

            return parts;
        }

        /// <summary>
        /// Takes off a group that spans the whole expression, or returns <c>null</c> where none does.
        /// </summary>
        /// <remarks>
        /// Only a capturing group and a non-capturing one are taken off. A lookaround, an inline flag
        /// and a named group each change what the expression means or what it matches, and a
        /// recogniser that stripped them would be claiming a language it had not read — so they are
        /// refused, which costs a spelling and never mistakes one.
        /// </remarks>
        /// <param name="pattern">The expression.</param>
        /// <returns>The inside of the group, or <c>null</c>.</returns>
        internal static string? Unwrap(string pattern)
        {
            if (pattern.Length < 2 || pattern[0] != '(' || pattern[pattern.Length - 1] != ')')
                return null;

            var depth = 0;
            var characters = false;

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (characters)
                {
                    if (c == ']')
                        characters = false;

                    continue;
                }

                if (c == '[')
                    characters = true;
                else if (c == '(')
                    depth++;
                else if (c == ')' && --depth == 0 && i != pattern.Length - 1)
                    return null;
            }

            if (depth != 0)
                return null;

            var inner = pattern.Substring(1, pattern.Length - 2);

            if (inner.StartsWith("?:", StringComparison.Ordinal))
                return inner.Substring(2);

            return inner.Length > 0 && inner[0] == '?' ? null : inner;
        }

        /// <summary>
        /// Puts a pattern into the one spelling the table is keyed by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three liberties, and each is an identity rather than a guess. Whitespace outside a character
        /// class means nothing in an unextended ECMA-262 pattern, which is the dialect JSON Schema
        /// specifies; <c>\d</c> is exactly <c>[0-9]</c> there; and a character class is a <em>set</em>,
        /// so the order its members are written in carries no meaning — <c>[a-f0-9]</c> and
        /// <c>[0-9a-f]</c> are one class written two ways, and both are in use.
        /// </para>
        /// <para>
        /// Anything else — a different quantifier, a looser class, a missing anchor — is a different
        /// language and gets no entry.
        /// </para>
        /// </remarks>
        internal static string? Normalise(string? pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return null;

            var builder = new StringBuilder(pattern!.Length);

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '\\' && i + 1 < pattern.Length && pattern[i + 1] == 'd')
                {
                    builder.Append("[0-9]");
                    i++;
                    continue;
                }

                if (c == '\\' && i + 1 < pattern.Length)
                {
                    builder.Append(c).Append(pattern[i + 1]);
                    i++;
                    continue;
                }

                if (c == '[' && pattern.IndexOf(']', i + 1) is int close && close > i)
                {
                    builder.Append(SortClass(pattern.Substring(i, close - i + 1)));
                    i = close;
                    continue;
                }

                builder.Append(c);
            }

            return Collapse(builder.ToString());
        }

        /// <summary>
        /// Writes a run of one repeated atom as a single counted one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A fourth liberty, and an identity like the other three: for an atom matching exactly one
        /// character, <c>A{m}A{n}</c> and <c>A{m+n}</c> accept the same strings, and so do <c>A</c> and
        /// <c>A{1}</c>. That collapses the axis a date pattern varies along most — <c>\d\d\d\d-\d\d-\d\d</c>
        /// is as common as <c>\d{4}-\d{2}-\d{2}</c>, and neither spelling is more canonical than the
        /// other.
        /// </para>
        /// <para>
        /// Only the counted quantifier merges. <c>?</c>, <c>*</c>, <c>+</c> and <c>{m,n}</c> all admit a
        /// range of widths, which is the one property these forms turn on, so an atom carrying one is
        /// emitted exactly as written and ends the run beside it. So is anything that is not a
        /// single-character atom — an anchor, a group, an alternation — which is what keeps the
        /// nil-UUID alternation above intact.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The pattern, already normalised in the other three respects.</param>
        /// <returns>The pattern with its runs counted.</returns>
        internal static string Collapse(string pattern)
        {
            var output = new StringBuilder(pattern.Length);
            string? pending = null;
            var count = 0L;

            void Flush()
            {
                if (pending is null)
                    return;

                output.Append(pending);

                if (count > 1)
                    output.Append('{').Append(count.ToString(CultureInfo.InvariantCulture)).Append('}');

                pending = null;
                count = 0;
            }

            var i = 0;
            while (i < pattern.Length)
            {
                var start = i;

                if (ReadAtom(pattern, ref i) is not string atom)
                {
                    Flush();
                    output.Append(pattern[i]);
                    i++;
                    continue;
                }

                var repeats = 1L;

                if (i < pattern.Length && pattern[i] == '{' && pattern.IndexOf('}', i + 1) is int close && close > i)
                {
                    var inner = pattern.Substring(i + 1, close - i - 1);

                    if (inner.Length > 0 && long.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var exact))
                    {
                        repeats = exact;
                        i = close + 1;
                    }
                    else
                    {
                        // A range rather than a count, which is a different language.
                        Flush();
                        output.Append(pattern, start, close + 1 - start);
                        i = close + 1;
                        continue;
                    }
                }
                else if (i < pattern.Length && (pattern[i] is '?' or '*' or '+'))
                {
                    Flush();
                    output.Append(pattern, start, i + 1 - start);
                    i++;
                    continue;
                }

                if (pending is not null && string.Equals(pending, atom, StringComparison.Ordinal))
                {
                    count += repeats;
                    continue;
                }

                Flush();
                pending = atom;
                count = repeats;
            }

            Flush();
            return output.ToString();
        }

        /// <summary>
        /// Reads the atom at a position, where one matching a single character starts there.
        /// </summary>
        /// <remarks>
        /// A character class, an escape and a bare literal each match exactly one character and are
        /// therefore mergeable. A structural character is not read at all — the position is left where
        /// it was and the caller copies it across, which is what keeps groups and anchors untouched.
        /// </remarks>
        /// <param name="pattern">The pattern.</param>
        /// <param name="i">The position, advanced past the atom where one was read.</param>
        /// <returns>The atom, or <c>null</c> where the position holds no single-character atom.</returns>
        internal static string? ReadAtom(string pattern, ref int i)
        {
            var c = pattern[i];

            if (c is '^' or '$' or '|' or '(' or ')' or '?' or '*' or '+' or '{' or '}')
                return null;

            if (c == '[')
            {
                var close = pattern.IndexOf(']', i + 1);
                if (close < 0)
                    return null;

                var characters = pattern.Substring(i, close - i + 1);
                i = close + 1;
                return characters;
            }

            if (c == '\\' && i + 1 < pattern.Length)
            {
                var escape = pattern.Substring(i, 2);
                i += 2;
                return escape;
            }

            i++;
            return c.ToString();
        }

        /// <summary>
        /// Writes a character class with its members in one order.
        /// </summary>
        /// <remarks>
        /// A class is a set, so <c>[a-f0-9]</c> and <c>[0-9a-f]</c> denote the same characters and only
        /// one of them can be the key the table is looked up by. The members are ranges and single
        /// characters; anything else in there — an escape, a negation, a literal <c>-</c> that is not
        /// part of a range — is left exactly as written rather than guessed at, which costs a spelling
        /// and never mistakes one class for another.
        /// </remarks>
        /// <param name="characters">The class, including its brackets.</param>
        /// <returns>The class with its members ordered, or unchanged where it could not be taken apart.</returns>
        internal static string SortClass(string characters)
        {
            var inner = characters.Substring(1, characters.Length - 2);

            if (inner.Length == 0 || inner[0] == '^' || inner.IndexOf('\\') >= 0)
                return characters;

            var members = new List<string>();

            for (var i = 0; i < inner.Length; i++)
            {
                if (i + 2 < inner.Length && inner[i + 1] == '-')
                {
                    members.Add(inner.Substring(i, 3));
                    i += 2;
                    continue;
                }

                // A bare '-' is a literal whose meaning depends on where it sits, so the class is left
                // as written rather than reordered around it.
                if (inner[i] == '-')
                    return characters;

                members.Add(inner[i].ToString());
            }

            members.Sort(StringComparer.Ordinal);

            return "[" + string.Concat(members) + "]";
        }

    }

}
