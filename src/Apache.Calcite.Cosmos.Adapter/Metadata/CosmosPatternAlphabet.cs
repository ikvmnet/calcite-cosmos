using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Reads what characters a declared <c>pattern</c> lets a string hold: the bands of UTF-16 code units no string it
    /// matches can draw from, under every reading a validator may have given it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is for.</b> The service orders a document's strings by code point and Calcite by UTF-16 code unit, and
    /// the two part only where a surrogate meets a unit from U+E000 (<see cref="CosmosCharacterBand"/>). A path whose
    /// strings are known to lack either band is one whose sorts, extremes and comparisons the service can be handed
    /// (<see cref="CosmosServiceOrder"/>). JSON Schema has one keyword that says what characters a string holds, and it
    /// is this one: a schema writing <c>"pattern": "^[A-Za-z0-9_-]+$"</c> on <c>id</c> has said that no id holds a
    /// character at or above U+D800, which is a proof the planner can use with nothing checked at run time.
    /// </para>
    /// <para>
    /// <b>Two readings, and the answer has to be right under both.</b> JSON Schema names ECMA-262's dialect and no flag.
    /// Without the <c>u</c> flag a pattern is read, and matched, unit by unit: <c>[^a]</c> matches either half of a
    /// surrogate pair, and <c>\uD83D\uDE00</c> is two atoms. With it, code point by code point: <c>[^a]</c> matches a
    /// whole supplementary character, <c>\uD83D\uDE00</c> is one, and a range may span the planes. A validator picks
    /// one, and the adapter cannot know which wrote its documents. So the pattern is read twice, once each way, and a
    /// band is absent only where both readings exclude it. They part in places that matter:
    /// <c>^[^\uD800-\uDFFF]*$</c> excludes the surrogates read by unit and admits every supplementary character read
    /// by code point; <c>[\uD800-\uDBFF\uDC00-\uDFFF]</c> is the surrogates read by unit and, read by code point, a
    /// range from U+D800 to U+10FC00 that holds the whole of the high band. A pattern either reading rejects as
    /// malformed proves nothing: a validator using that reading could not have checked anything with it.
    /// </para>
    /// <para>
    /// <b>The grammar read is the strict one both readings share</b>, and anything outside it is refused:
    /// </para>
    /// <list type="bullet">
    /// <item><description>every top-level alternative is anchored, beginning with <c>^</c> and ending with <c>$</c>, so
    /// that every character of a matching string is one an atom consumed. JSON Schema's <c>pattern</c> searches: an
    /// unanchored alternative matches inside a string of anything.</description></item>
    /// <item><description>an atom is a literal, an escaped syntax character, a control escape, <c>\xHH</c>,
    /// <c>\uXXXX</c>, <c>\0</c>, <c>\d</c>, <c>\w</c>, a character class, or a group, capturing or <c>(?:</c>, quantified
    /// any way; <c>\b</c> and <c>\B</c> consume nothing. <c>\d</c> and <c>\w</c> are ASCII in ECMA-262 under either
    /// reading, there being no flags in a schema to make them otherwise.</description></item>
    /// <item><description>a range's bands are those of every unit, or code point, it spans — not of its ends: a range
    /// from U+D000 to U+E100 holds a surrogate and a high unit though neither end is one.</description></item>
    /// <item><description>a negated class is its complement, computed under each reading over that reading's own
    /// alphabet: the units to U+FFFF, the code points to U+10FFFF, where every code point past U+FFFF is a surrogate
    /// pair. Exact, so it is accepted.</description></item>
    /// <item><description>refused, because they bound nothing or are read differently, or not at all, by one reading:
    /// <c>.</c>, <c>\s</c> (which matches U+FEFF), <c>\S</c>, <c>\D</c>, <c>\W</c>, <c>\p{…}</c>, <c>\P{…}</c>,
    /// <c>\u{…}</c>, a back-reference, <c>\k</c>, a lookaround, a named group, an inline flag, a legacy octal escape, an
    /// identity escape of anything but a syntax character or a solidus, a class escape ending a range, and a lone
    /// <c>]</c>, <c>{</c> or <c>}</c> — each of the last group a literal read by unit and an error read by code
    /// point.</description></item>
    /// </list>
    /// <para>
    /// <b>A refused pattern proves nothing</b>, which is always correct: a path the schema says nothing readable about
    /// is reasoned about as it was. Nothing here is guessed from samples: as with a stored form, the conclusion needed is
    /// about every string the pattern admits, and only a reading of the language gives it.
    /// </para>
    /// <para>
    /// <b>What a written string becomes when it is stored.</b> Measured on the probe account, a lone surrogate written
    /// as a JSON escape is stored as U+FFFD, a high unit. A pattern that admits a surrogate admits a lone one wherever
    /// a validator reads by unit, so what it admits as written is not what the service orders: a pattern that lets a
    /// string hold a surrogate proves nothing about the high band either (<see cref="Claims"/>).
    /// </para>
    /// <para>
    /// It reads the pattern as written, not as <c>CosmosPatternLanguage.Normalise</c> leaves it, which is a spelling
    /// for matching the stored forms and not a parse: the two readers are one keyword asked two questions, and
    /// DESIGN says why both exist for now.
    /// </para>
    /// </remarks>
    static class CosmosPatternAlphabet
    {

        /// <summary>
        /// How deep groups may nest before a pattern is refused rather than read.
        /// </summary>
        const int Depth = 64;

        /// <summary>
        /// Returns the claims a declared <c>pattern</c> makes about the characters of the strings at its path, as the
        /// service stores them.
        /// </summary>
        /// <remarks>
        /// A <see cref="CosmosCharacters"/> for each band no string the pattern admits holds a unit of. A pattern
        /// admitting a surrogate admits a lone one under the reading by unit, which the service stores as U+FFFD, so it
        /// says nothing of the high band either, and makes no claim at all. Like the pattern, each claim is about a
        /// string if there is one, and holds of anything else.
        /// </remarks>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The claims, which are empty where the pattern is refused or excludes nothing.</returns>
        public static IReadOnlyList<JsonClaim> Claims(string? pattern)
        {
            if (pattern is null || Absent(pattern) is not { } absent || absent.Contains(CosmosCharacterBand.Surrogates) == false)
                return Array.Empty<JsonClaim>();

            var claims = new List<JsonClaim>(2);
            foreach (var band in absent)
                claims.Add(new CosmosCharacters(band));

            return claims;
        }

        /// <summary>
        /// Returns the bands no string a pattern matches holds a unit of, under both readings.
        /// </summary>
        /// <param name="pattern">The pattern.</param>
        /// <returns>The bands, or <c>null</c> where either reading refuses the pattern.</returns>
        internal static IReadOnlyList<CosmosCharacterBand>? Absent(string pattern)
        {
            if (Read(pattern, unicode: false) is not { } units || Read(pattern, unicode: true) is not { } points)
                return null;

            return Bands(units | points);
        }

        /// <summary>
        /// Returns the bands no string a pattern matches holds a unit of, under one reading: a hook for the tests, which hold
        /// each reading apart, and read by nothing else.
        /// </summary>
        /// <param name="pattern">The pattern.</param>
        /// <param name="unicode">Whether it is read by code point, as the <c>u</c> flag reads it, rather than by unit.</param>
        /// <returns>The bands, or <c>null</c> where the reading refuses the pattern.</returns>
        internal static IReadOnlyList<CosmosCharacterBand>? Absent(string pattern, bool unicode) =>
            Read(pattern, unicode) is { } read ? Bands(read) : null;

        /// <summary>
        /// Returns the bands a set does not hold, one by one.
        /// </summary>
        static IReadOnlyList<CosmosCharacterBand> Bands(CosmosCharacterBand held)
        {
            var absent = new List<CosmosCharacterBand>(2);
            if ((held & CosmosCharacterBand.Surrogates) == CosmosCharacterBand.None)
                absent.Add(CosmosCharacterBand.Surrogates);
            if ((held & CosmosCharacterBand.HighBmp) == CosmosCharacterBand.None)
                absent.Add(CosmosCharacterBand.HighBmp);

            return absent;
        }

        static CosmosCharacterBand? Read(string pattern, bool unicode)
        {
            var reader = new Reader(pattern, unicode);
            return reader.Pattern() ? reader.Bands : null;
        }

        /// <summary>
        /// One reading of one pattern: a parser of the grammar both readings share, which records the bands of every
        /// character an atom can consume.
        /// </summary>
        sealed class Reader
        {

            /// <summary>The kinds of term an alternative's anchoring is decided by.</summary>
            enum Kind
            {
                None,
                Start,
                End,
                Other,
            }

            static readonly (int Lo, int Hi)[] Digit = { (0x30, 0x39) };

            static readonly (int Lo, int Hi)[] Word = { (0x30, 0x39), (0x41, 0x5A), (0x5F, 0x5F), (0x61, 0x7A) };

            readonly string _source;
            readonly bool _unicode;
            int _at;

            public Reader(string source, bool unicode)
            {
                _source = source;
                _unicode = unicode;
            }

            /// <summary>Gets the bands of the units a character an atom consumes may be, or be written in.</summary>
            public CosmosCharacterBand Bands { get; private set; }

            bool AtEnd => _at >= _source.Length;

            /// <summary>The character a source position holds, or a NUL past the end, which no test here mistakes for one.</summary>
            char Peek(int ahead = 0) => _at + ahead < _source.Length ? _source[_at + ahead] : '\0';

            bool Take(char c)
            {
                if (AtEnd || _source[_at] != c)
                    return false;

                _at++;
                return true;
            }

            /// <summary>
            /// Reads the whole pattern, every top-level alternative anchored at both ends.
            /// </summary>
            public bool Pattern()
            {
                do
                {
                    if (Alternative(0, out var first, out var last) == false || first != Kind.Start || last != Kind.End)
                        return false;
                }
                while (Take('|'));

                return AtEnd;
            }

            /// <summary>
            /// Reads one alternative up to a <c>|</c>, a <c>)</c> or the end, saying what kind of term it begins and ends
            /// with.
            /// </summary>
            bool Alternative(int depth, out Kind first, out Kind last)
            {
                first = last = Kind.None;

                while (AtEnd == false && _source[_at] is not ('|' or ')'))
                {
                    if (Term(depth, out var kind) == false)
                        return false;

                    if (first == Kind.None)
                        first = kind;

                    last = kind;
                }

                return true;
            }

            bool Term(int depth, out Kind kind)
            {
                kind = Kind.Other;

                switch (_source[_at])
                {
                    // Assertions consume nothing, and are not quantified: `^*` is an error read either way.
                    case '^':
                        _at++;
                        kind = Kind.Start;
                        return true;

                    case '$':
                        _at++;
                        kind = Kind.End;
                        return true;

                    case '\\' when Peek(1) is 'b' or 'B':
                        _at += 2;
                        return true;

                    // Any character but a line terminator: it bounds nothing.
                    case '.':
                        return false;

                    // A quantifier with nothing before it; and a lone brace or bracket, which a reading by unit takes for a
                    // literal and one by code point refuses.
                    case '*' or '+' or '?' or '{' or '}' or ']':
                        return false;

                    case '(':
                    {
                        _at++;

                        // A non-capturing group. Every other `(?` is a lookaround, a named group or a flag: one changes
                        // what the rest matches, and the others are read differently, or not at all, by one reading.
                        if (Peek() == '?')
                        {
                            if (Peek(1) != ':')
                                return false;

                            _at += 2;
                        }

                        if (depth >= Depth)
                            return false;

                        do
                        {
                            if (Alternative(depth + 1, out _, out _) == false)
                                return false;
                        }
                        while (Take('|'));

                        if (Take(')') == false)
                            return false;

                        break;
                    }

                    case '[':
                        _at++;
                        if (Class() == false)
                            return false;

                        break;

                    case '\\':
                    {
                        _at++;
                        if (Escape(inClass: false, out var spans) == false)
                            return false;

                        foreach (var (lo, hi) in spans)
                            Add(lo, hi);

                        break;
                    }

                    default:
                    {
                        var c = Character();
                        Add(c, c);
                        break;
                    }
                }

                return Quantifier();
            }

            /// <summary>
            /// Reads a quantifier where there is one: <c>*</c>, <c>+</c>, <c>?</c> or a count in braces, each perhaps lazy.
            /// A brace that opens no count is refused, a reading by unit taking it for a literal and one by code point for an
            /// error; a quantifier after a quantifier is an error read either way, and the next term refuses it.
            /// </summary>
            bool Quantifier()
            {
                if (Take('*') || Take('+') || Take('?'))
                {
                    Take('?');
                    return true;
                }

                if (Take('{') == false)
                    return true;

                if (Digits() is not string least)
                    return false;

                var most = least;
                if (Take(','))
                    most = Digits();

                if (Take('}') == false || most is not null && Below(most, least))
                    return false;

                Take('?');
                return true;
            }

            string? Digits()
            {
                var start = _at;
                while (AtEnd == false && _source[_at] is >= '0' and <= '9')
                    _at++;

                return _at > start ? _source.Substring(start, _at - start) : null;
            }

            /// <summary>
            /// Determines whether one count is below another, however many digits either has.
            /// </summary>
            static bool Below(string a, string b)
            {
                a = a.TrimStart('0');
                b = b.TrimStart('0');

                return a.Length != b.Length ? a.Length < b.Length : string.CompareOrdinal(a, b) < 0;
            }

            /// <summary>
            /// Reads one character of the source: a unit, or, by code point, a whole surrogate pair.
            /// </summary>
            int Character()
            {
                var c = _source[_at++];

                if (_unicode && char.IsHighSurrogate(c) && AtEnd == false && char.IsLowSurrogate(_source[_at]))
                    return char.ConvertToUtf32(c, _source[_at++]);

                return c;
            }

            /// <summary>
            /// Reads a character class after its <c>[</c>, positive or negated, and records the bands of what it matches.
            /// </summary>
            bool Class()
            {
                var negated = Take('^');
                var members = new List<(int Lo, int Hi)>();

                while (Take(']') == false)
                {
                    if (AtEnd || ClassAtom(out var from) == false)
                        return false;

                    // A range, unless the dash is the class's last member: `[a-]` is two members.
                    if (Peek() == '-' && _at + 1 < _source.Length && _source[_at + 1] != ']')
                    {
                        _at++;

                        if (ClassAtom(out var to) == false)
                            return false;

                        // `[\d-z]` is three members read by unit and an error read by code point; a range running
                        // backwards is an error read either way.
                        if (IsOne(from) == false || IsOne(to) == false || to[0].Lo < from[0].Lo)
                            return false;

                        members.Add((from[0].Lo, to[0].Lo));
                    }
                    else
                    {
                        members.AddRange(from);
                    }
                }

                // The complement over the reading's own alphabet: every unit to U+FFFF, or every code point to U+10FFFF.
                if (negated)
                    members = Complement(members, _unicode ? 0x10FFFF : 0xFFFF);

                foreach (var (lo, hi) in members)
                    Add(lo, hi);

                return true;

                static bool IsOne((int Lo, int Hi)[] spans) => spans.Length == 1 && spans[0].Lo == spans[0].Hi;
            }

            /// <summary>
            /// Reads one member of a class: a character, or a class escape standing for several.
            /// </summary>
            bool ClassAtom(out (int Lo, int Hi)[] spans)
            {
                if (Take('\\'))
                    return Escape(inClass: true, out spans);

                var c = Character();
                spans = new[] { (c, c) };
                return true;
            }

            /// <summary>
            /// Reads what follows a backslash, as the characters it stands for.
            /// </summary>
            /// <remarks>
            /// Each escape read here means one thing under both readings but one: <c>\uXXXX</c> naming a lead surrogate
            /// and followed by one naming a trail is a supplementary character read by code point, and two units read by
            /// unit. What a reading by unit takes for a literal and one by code point refuses — <c>\a</c>, <c>\-</c>
            /// outside a class, <c>\c</c> before anything but a letter, <c>\x</c> or <c>\u</c> short of their digits,
            /// <c>\8</c>, <c>\0</c> before a digit — is refused, as is every escape that bounds nothing and every
            /// back-reference.
            /// </remarks>
            bool Escape(bool inClass, out (int Lo, int Hi)[] spans)
            {
                spans = Array.Empty<(int, int)>();

                if (AtEnd)
                    return false;

                var c = _source[_at++];
                int value;

                switch (c)
                {
                    case 'd':
                        spans = Digit;
                        return true;

                    case 'w':
                        spans = Word;
                        return true;

                    case 'b' when inClass:
                        value = '\b';
                        break;

                    case '-' when inClass:
                        value = '-';
                        break;

                    case 't': value = '\t'; break;
                    case 'n': value = '\n'; break;
                    case 'v': value = '\v'; break;
                    case 'f': value = '\f'; break;
                    case 'r': value = '\r'; break;

                    case '0' when Peek() is < '0' or > '9':
                        value = 0;
                        break;

                    case 'c' when Peek() is >= 'a' and <= 'z' or >= 'A' and <= 'Z':
                        value = _source[_at++] % 32;
                        break;

                    case 'x':
                        if (Hex(2, out value) == false)
                            return false;

                        break;

                    case 'u':
                    {
                        // `\u{…}` names a code point read by code point, and is a `u` repeated read by unit.
                        if (Hex(4, out value) == false)
                            return false;

                        if (_unicode && char.IsHighSurrogate((char)value) && Peek() == '\\' && Peek(1) == 'u')
                        {
                            var mark = _at;
                            _at += 2;

                            if (Hex(4, out var trail) && char.IsLowSurrogate((char)trail))
                                value = char.ConvertToUtf32((char)value, (char)trail);
                            else
                                _at = mark;
                        }

                        break;
                    }

                    // An escaped syntax character, or a solidus: the only identity escapes read alike either way.
                    case '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/':
                        value = c;
                        break;

                    default:
                        return false;
                }

                spans = new[] { (value, value) };
                return true;
            }

            bool Hex(int digits, out int value)
            {
                value = 0;

                if (_at + digits > _source.Length)
                    return false;

                for (var i = 0; i < digits; i++)
                {
                    var c = _source[_at + i];
                    var d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                    if (d < 0)
                        return false;

                    value = value * 16 + d;
                }

                _at += digits;
                return true;
            }

            /// <summary>
            /// Records the bands of every character in a span: of each unit, or, read by code point, of the units each
            /// code point is written in, everything past U+FFFF being a surrogate pair.
            /// </summary>
            void Add(int lo, int hi) => Bands |= CosmosCharacters.BandsOf(lo, hi);

            /// <summary>
            /// Returns the characters from zero to <paramref name="last"/> that no member holds.
            /// </summary>
            static List<(int Lo, int Hi)> Complement(List<(int Lo, int Hi)> members, int last)
            {
                members.Sort((a, b) => a.Lo.CompareTo(b.Lo));

                var gaps = new List<(int Lo, int Hi)>();
                var next = 0;

                foreach (var (lo, hi) in members)
                {
                    if (lo > next)
                        gaps.Add((next, lo - 1));

                    next = Math.Max(next, hi + 1);
                }

                if (next <= last)
                    gaps.Add((next, last));

                return gaps;
            }

        }

    }

}
