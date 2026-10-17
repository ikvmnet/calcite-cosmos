using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The one reader of a declared <c>pattern</c>: it parses the pattern under every reading a validator may have given
    /// it, and reads two things off the parse — the characters a string at the path may hold, and the strings the
    /// pattern admits, which are what a stored form is recognised by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One keyword, one parse, two things read off it.</b> <see cref="Claims"/> reads the characters, for an order:
    /// the service orders a document's strings by code point and Calcite by UTF-16 code unit, and the two part only
    /// where a surrogate meets a unit from U+E000 (<see cref="JsonCharacterBand"/>), so a path whose strings are known to
    /// lack either band is one whose sorts, extremes and comparisons the service can be handed
    /// (<see cref="CosmosServiceOrder"/>). <see cref="Strings"/> reads the language, for a stored form
    /// (<see cref="CosmosStoredForms"/>): a form is recognised where what the pattern admits is the form's language, which
    /// only a parse can say — matching the text against spellings could not, and was a second reader of the keyword that
    /// read some of it differently.
    /// </para>
    /// <para>
    /// <b>A reader of an input, and nothing more.</b> What it reads becomes a declared fact of the path: the characters
    /// as <see cref="JsonClaim.Characters"/>, and a form as <see cref="JsonClaim.Represents"/> once a family has said
    /// which form the strings are. The theory meets each with what else is known of the path, and derives the
    /// characters of a form from the form (<see cref="IJsonStoredForm.Characters"/>), not from this. Which band a range's
    /// units are in is the theory's to say (<c>JsonValues.BandsOf</c>).
    /// </para>
    /// <para>
    /// <b>Two readings, and the answer has to be right under both.</b> JSON Schema names ECMA-262's dialect and no flag.
    /// Without the <c>u</c> flag a pattern is read, and matched, unit by unit: <c>[^a]</c> matches either half of a
    /// surrogate pair, and <c>😀</c> is two atoms. With it, code point by code point: <c>[^a]</c> matches a
    /// whole supplementary character, <c>😀</c> is one, and a range may span the planes. A validator picks
    /// one, and the adapter cannot know which wrote its documents. So the pattern is read twice, once each way: a band
    /// is absent only where both readings exclude it, and the strings are read only where both readings read the same
    /// ones. They part in places that matter: <c>^[^\uD800-\uDFFF]*$</c> excludes the surrogates read by unit and admits
    /// every supplementary character read by code point; <c>[\uD800-􏰀-\uDFFF]</c> is the surrogates read by
    /// unit and, read by code point, a range from U+D800 to U+10FC00 that holds the whole of the high band. A pattern
    /// either reading rejects as malformed proves nothing: a validator using that reading could not have checked
    /// anything with it. That holds of a form as of the characters — <c>^{…}$</c> with its braces unescaped is a
    /// braced UUID read by unit and an error read by code point, so it is no form.
    /// </para>
    /// <para>
    /// <b>The grammar read is the strict one both readings share</b>, and anything outside it is refused:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the pattern is anchored: every way through it asserts <c>^</c> before it consumes anything and
    /// <c>$</c> after it has consumed everything, so that every character of a matching string is one an atom consumed.
    /// JSON Schema's <c>pattern</c> searches: an unanchored alternative matches inside a string of anything. An
    /// alternative is anchored by its first and last terms, and a group, unquantified, by its own alternatives, so
    /// <c>^a$|^b$</c>, <c>^(?:a|b)$</c> and <c>(?:^a$)|(?:^b$)</c> are anchored alike, and <c>(?:^a)?b$</c> is
    /// not.</description></item>
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
    /// identity escape of anything but a syntax character or a solidus (<c>\-</c> outside a class among them), a class
    /// escape ending a range, and a lone <c>]</c>, <c>{</c> or <c>}</c> — each of the last group a literal read by unit
    /// and an error read by code point.</description></item>
    /// </list>
    /// <para>
    /// <b>A refused pattern proves nothing</b>, which is always correct: a path the schema says nothing readable about
    /// is reasoned about as it was. Nothing here is guessed from samples: the conclusion needed, of characters or of a
    /// form, is about every string the pattern admits, and only a reading of the language gives it.
    /// </para>
    /// <para>
    /// <b>What a written string becomes when it is stored.</b> Measured on the probe account, a lone surrogate written
    /// as a JSON escape is stored as U+FFFD, a high unit. A pattern that admits a surrogate admits a lone one wherever
    /// a validator reads by unit, so what it admits as written is not what the service orders: a pattern that lets a
    /// string hold a surrogate proves nothing about the high band either (<see cref="Claims"/>).
    /// </para>
    /// </remarks>
    static class CosmosPatternReader
    {

        /// <summary>
        /// How deep groups may nest before a pattern is refused rather than read.
        /// </summary>
        const int Depth = 64;

        /// <summary>
        /// How many alternatives a pattern's strings may be read as. An alternation inside a sequence is distributed
        /// across it, which multiplies, so a pattern past this is read for its characters and has no strings.
        /// </summary>
        const int Alternatives = 64;

        /// <summary>
        /// How many runs one alternative may be read as before its runs of one class are merged: a count of a group
        /// writes the group out that many times.
        /// </summary>
        const int Runs = 256;

        /// <summary>
        /// The largest count a run is read with. Every form is far narrower, and a pattern counting past this has no
        /// strings, which costs a form no pattern writes.
        /// </summary>
        const int Count = 100_000;

        /// <summary>
        /// Returns the claims a declared <c>pattern</c> makes about the characters of the strings at its path, as the
        /// service stores them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <see cref="JsonClaim.Characters"/> naming the bands the strings the pattern admits may hold units of, which
        /// the theory states of the path as declared: an input read into a fact, and nothing decided here about what
        /// the fact is for. Like the pattern, it is about a string if there is one, and holds of anything else.
        /// </para>
        /// <para>
        /// <b>What the service stores, not what was written.</b> A pattern admitting a surrogate admits a lone one under
        /// the reading by unit, which the service stores as U+FFFD, so its strings as stored may hold the high band too,
        /// and it claims nothing.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The claim, or none where the pattern is refused or its strings may hold either band.</returns>
        public static IReadOnlyList<JsonClaim> Claims(string? pattern)
        {
            if (pattern is null || Absent(pattern) is not { } absent || absent.Contains(JsonCharacterBand.Surrogates) == false)
                return Array.Empty<JsonClaim>();

            var held = JsonCharacterBand.Any;
            foreach (var band in absent)
                held &= ~band;

            return new JsonClaim[] { new JsonClaim.Characters(held) };
        }

        /// <summary>
        /// Returns the strings a declared <c>pattern</c> admits, where both readings read the same ones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What the stored forms are recognised by (<see cref="CosmosStoredForms.Recognise"/>): a form is recognised
        /// where these are its language.
        /// </para>
        /// <para>
        /// <b>Read where the two readings read one language.</b> Both readings must accept the pattern, as for its
        /// characters, and read the same strings from it. And every class must lie in the basic plane outside the
        /// surrogates, where a unit and a code point are one character: <c>[\uD800-\uDFFF]{2}</c> is a surrogate pair read
        /// by unit and two lone surrogates read by code point, one sequence of classes and two languages. Every stored
        /// form is ASCII, so the condition costs none.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The strings, or <c>null</c> where the pattern is refused, the readings part, or the pattern is not a
        /// union of anchored sequences of runs.</returns>
        internal static CosmosPatternStrings? Strings(string? pattern)
        {
            if (pattern is null || Read(pattern, unicode: false, strings: true)?.Strings is not { } units || Read(pattern, unicode: true, strings: true)?.Strings is not { } points)
                return null;

            return units.Equals(points) ? units : null;
        }

        /// <summary>
        /// Returns the bands no string a pattern matches holds a unit of, under both readings.
        /// </summary>
        /// <param name="pattern">The pattern.</param>
        /// <returns>The bands, or <c>null</c> where either reading refuses the pattern.</returns>
        internal static IReadOnlyList<JsonCharacterBand>? Absent(string pattern)
        {
            if (Read(pattern, unicode: false, strings: false) is not { } units || Read(pattern, unicode: true, strings: false) is not { } points)
                return null;

            return Bands(units.Bands | points.Bands);
        }

        /// <summary>
        /// Returns the bands no string a pattern matches holds a unit of, under one reading: a hook for the tests, which hold
        /// each reading apart, and read by nothing else.
        /// </summary>
        /// <param name="pattern">The pattern.</param>
        /// <param name="unicode">Whether it is read by code point, as the <c>u</c> flag reads it, rather than by unit.</param>
        /// <returns>The bands, or <c>null</c> where the reading refuses the pattern.</returns>
        internal static IReadOnlyList<JsonCharacterBand>? Absent(string pattern, bool unicode) =>
            Read(pattern, unicode, strings: false) is { } read ? Bands(read.Bands) : null;

        /// <summary>
        /// Returns the bands a set does not hold, one by one.
        /// </summary>
        static IReadOnlyList<JsonCharacterBand> Bands(JsonCharacterBand held)
        {
            var absent = new List<JsonCharacterBand>(2);
            if ((held & JsonCharacterBand.Surrogates) == JsonCharacterBand.None)
                absent.Add(JsonCharacterBand.Surrogates);
            if ((held & JsonCharacterBand.HighBmp) == JsonCharacterBand.None)
                absent.Add(JsonCharacterBand.HighBmp);

            return absent;
        }

        /// <summary>
        /// Parses a pattern under one reading, or returns <c>null</c> where the reading refuses it or it is not anchored.
        /// </summary>
        /// <param name="pattern">The pattern.</param>
        /// <param name="unicode">Whether it is read by code point, as the <c>u</c> flag reads it, rather than by unit.</param>
        /// <param name="strings">Whether the strings are read as well as the bands: the characters ask only the bands,
        /// and are read without building what they do not ask.</param>
        static Reading? Read(string pattern, bool unicode, bool strings)
        {
            var parser = new Parser(pattern, unicode, strings);

            if (parser.Pattern() is not { Begins: true, Ends: true } read)
                return null;

            return new Reading(parser.Bands, Canonical(read.Strings));
        }

        /// <summary>
        /// Puts the alternatives a parse read into the strings a form is recognised by.
        /// </summary>
        /// <remarks>
        /// Every alternative begins with <c>^</c> and ends with <c>$</c>, which the parse has established before this is
        /// asked, so they are taken off; an assertion left between them — another anchor, a <c>\b</c> — narrows the
        /// language in a way no run says, and the pattern has no strings. Nor has one with an empty class, <c>[]</c>,
        /// which matches nothing and would make a form of a pattern no string conforms to.
        /// </remarks>
        static CosmosPatternStrings? Canonical(List<List<Item>>? strings)
        {
            if (strings is null)
                return null;

            var alternatives = new List<List<CosmosPatternRun>>(strings.Count);

            foreach (var alternative in strings)
            {
                var start = 0;
                var end = alternative.Count;

                while (start < end && alternative[start].Mark == Mark.Start)
                    start++;

                while (end > start && alternative[end - 1].Mark == Mark.End)
                    end--;

                var runs = new List<CosmosPatternRun>(end - start);

                for (var i = start; i < end; i++)
                {
                    if (alternative[i].Mark != Mark.Run || alternative[i].Run.Class.Spans.Count == 0 || alternative[i].Run.Class.IsPlain() == false)
                        return null;

                    runs.Add(alternative[i].Run);
                }

                alternatives.Add(runs);
            }

            return CosmosPatternStrings.Of(alternatives);
        }

        /// <summary>
        /// What one reading of a pattern read: the bands of every character an atom can consume, and the strings, where
        /// the pattern is a union of sequences of runs.
        /// </summary>
        sealed record Reading(JsonCharacterBand Bands, CosmosPatternStrings? Strings);

        /// <summary>
        /// What an item of a read alternative is: a run of characters, or an assertion, which consumes nothing.
        /// </summary>
        enum Mark
        {
            Run,
            Start,
            End,
            Other,
        }

        /// <summary>
        /// One item of a read alternative: a run, or an assertion.
        /// </summary>
        readonly record struct Item(Mark Mark, CosmosPatternRun Run);

        /// <summary>
        /// What a term, a sequence or an alternation reads as: whether every way through it is anchored at its start and
        /// at its end, and the alternatives it is the union of, or <c>null</c> where it is not a union of sequences of
        /// runs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Anchoring is read off the structure.</b> A sequence begins with <c>^</c> where its first term does, an
        /// alternation where every branch does, an unquantified group where what it holds does; a quantified term may be
        /// passed over, so it begins with nothing. Every way through a fragment that begins asserts <c>^</c> before it
        /// consumes a character, and so starts at the start of the string, and likewise at the end — which is the whole
        /// of what anchoring has to give.
        /// </para>
        /// <para>
        /// <b>The strings are distributed, which is an identity.</b> A sequence of alternations is the alternation of
        /// every sequence through them, a group is what it holds, and a count is the sequence written out that many
        /// times. An alternation of single characters is their class, and one run of one class repeated is one run, where
        /// the counts multiply into a single range. A range of counts of anything else — <c>(?:ab)*</c> — is no sequence
        /// of runs, and reads as none.
        /// </para>
        /// </remarks>
        sealed record Fragment(bool Begins, bool Ends, List<List<Item>>? Strings)
        {

            /// <summary>
            /// Reads an assertion, which consumes nothing.
            /// </summary>
            /// <param name="mark">Which assertion.</param>
            /// <param name="strings">Whether the strings are being read.</param>
            public static Fragment Assertion(Mark mark, bool strings) =>
                new(mark == Mark.Start, mark == Mark.End, strings ? One(new Item(mark, default)) : null);

            /// <summary>
            /// Reads an atom matching one character of a class.
            /// </summary>
            /// <param name="characters">The class.</param>
            /// <param name="strings">Whether the strings are being read.</param>
            public static Fragment Of(CosmosPatternClass characters, bool strings) =>
                new(false, false, strings ? One(new Item(Mark.Run, new CosmosPatternRun(characters, 1, 1))) : null);

            /// <summary>
            /// Reads terms written one after another.
            /// </summary>
            /// <remarks>
            /// A term of one alternative, which is most of them, is written onto the end of every alternative read so
            /// far, the lists being this sequence's own; only a term of several takes the product.
            /// </remarks>
            public static Fragment Concatenation(List<Fragment> terms)
            {
                List<List<Item>>? strings = new() { new() };

                foreach (var term in terms)
                {
                    if (strings is null || term.Strings is null)
                    {
                        strings = null;
                        break;
                    }

                    if (term.Strings.Count != 1)
                    {
                        strings = Product(strings, term.Strings);
                        continue;
                    }

                    foreach (var alternative in strings)
                    {
                        if (alternative.Count + term.Strings[0].Count > Runs)
                        {
                            strings = null;
                            break;
                        }

                        alternative.AddRange(term.Strings[0]);
                    }
                }

                return new(terms.Count > 0 && terms[0].Begins, terms.Count > 0 && terms[terms.Count - 1].Ends, strings);
            }

            /// <summary>
            /// Reads the branches of an alternation.
            /// </summary>
            public static Fragment Union(List<Fragment> branches)
            {
                List<List<Item>>? strings = new();

                foreach (var branch in branches)
                {
                    if (strings is null || branch.Strings is null || strings.Count + branch.Strings.Count > Alternatives)
                    {
                        strings = null;
                        break;
                    }

                    strings.AddRange(branch.Strings);
                }

                // An alternation of single characters is their class: `(?:0|1)` is `[01]`.
                if (strings is { Count: > 1 } && strings.All(s => s is [{ Mark: Mark.Run, Run: { Min: 1, Max: 1 } }]))
                    strings = One(new Item(Mark.Run, new CosmosPatternRun(strings.Select(s => s[0].Run.Class).Aggregate((a, b) => a.Union(b)), 1, 1)));

                return new(branches.All(b => b.Begins), branches.All(b => b.Ends), strings);
            }

            /// <summary>
            /// Reads the fragment quantified, at least <paramref name="min"/> times and at most <paramref name="max"/>.
            /// </summary>
            /// <param name="min">The fewest repetitions.</param>
            /// <param name="max">The most, or <c>null</c> where there is no bound.</param>
            public Fragment Repeated(long min, long? max) => new(false, false, Repeat(Strings, min, max));

            static List<List<Item>> One(Item item) => new() { new() { item } };

            static List<List<Item>>? Repeat(List<List<Item>>? strings, long min, long? max)
            {
                if (strings is null || min > Count || max > Count)
                    return null;

                if (max == 0 || strings is [[]])
                    return new() { new() };

                // One run of one class: A{a,b} repeated k times is A{ka,kb}, and A or A* or A+ repeated m to n times is
                // one range too. Any other range of counts of one is a set of widths with gaps in it, and is not read.
                if (strings is [[{ Mark: Mark.Run, Run: var run }]])
                {
                    if (min == max && run.Min * min <= Count && (run.Max ?? 0) * min <= Count)
                        return One(new Item(Mark.Run, run with { Min = (int)(run.Min * min), Max = (int?)(run.Max * min) }));

                    if (run.Min == 1 && run.Max == 1)
                        return One(new Item(Mark.Run, run with { Min = (int)min, Max = (int?)max }));

                    if (run.Max is null && run.Min <= 1)
                        return One(new Item(Mark.Run, run with { Min = (int)(run.Min * min), Max = null }));

                    return null;
                }

                // Anything else is read only a fixed number of times, which is it written out that often.
                if (min != max)
                    return null;

                List<List<Item>>? repeated = new() { new() };
                for (var i = 0; i < min && repeated is not null; i++)
                    repeated = Product(repeated, strings);

                return repeated;
            }

            /// <summary>
            /// Reads two fragments one after the other: every alternative of the first followed by every one of the
            /// second.
            /// </summary>
            static List<List<Item>>? Product(List<List<Item>>? left, List<List<Item>>? right)
            {
                if (left is null || right is null || (long)left.Count * right.Count > Alternatives)
                    return null;

                var product = new List<List<Item>>(left.Count * right.Count);

                foreach (var l in left)
                    foreach (var r in right)
                    {
                        if (l.Count + r.Count > Runs)
                            return null;

                        var joined = new List<Item>(l.Count + r.Count);
                        joined.AddRange(l);
                        joined.AddRange(r);
                        product.Add(joined);
                    }

                return product;
            }

        }

        /// <summary>
        /// One reading of one pattern: a parser of the grammar both readings share, which records the bands of every
        /// character an atom can consume and reads what each term admits.
        /// </summary>
        sealed class Parser
        {

            static readonly (int Lo, int Hi)[] Digit = { (0x30, 0x39) };

            static readonly (int Lo, int Hi)[] Word = { (0x30, 0x39), (0x41, 0x5A), (0x5F, 0x5F), (0x61, 0x7A) };

            readonly string _source;
            readonly bool _unicode;
            readonly bool _strings;
            int _at;

            public Parser(string source, bool unicode, bool strings)
            {
                _source = source;
                _unicode = unicode;
                _strings = strings;
            }

            /// <summary>Gets the bands of the units a character an atom consumes may be, or be written in.</summary>
            public JsonCharacterBand Bands { get; private set; }

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
            /// Reads the whole pattern, or returns <c>null</c> where it is outside the grammar.
            /// </summary>
            public Fragment? Pattern() => Disjunction(0) is { } read && AtEnd ? read : null;

            /// <summary>
            /// Reads alternatives separated by <c>|</c>, up to a <c>)</c> or the end.
            /// </summary>
            Fragment? Disjunction(int depth)
            {
                var branches = new List<Fragment>();

                do
                {
                    if (Alternative(depth) is not { } branch)
                        return null;

                    branches.Add(branch);
                }
                while (Take('|'));

                return Fragment.Union(branches);
            }

            /// <summary>
            /// Reads one alternative up to a <c>|</c>, a <c>)</c> or the end.
            /// </summary>
            Fragment? Alternative(int depth)
            {
                var terms = new List<Fragment>();

                while (AtEnd == false && _source[_at] is not ('|' or ')'))
                {
                    if (Term(depth) is not { } term)
                        return null;

                    terms.Add(term);
                }

                return Fragment.Concatenation(terms);
            }

            Fragment? Term(int depth)
            {
                Fragment atom;

                switch (_source[_at])
                {
                    // Assertions consume nothing, and are not quantified: `^*` is an error read either way.
                    case '^':
                        _at++;
                        return Fragment.Assertion(Mark.Start, _strings);

                    case '$':
                        _at++;
                        return Fragment.Assertion(Mark.End, _strings);

                    case '\\' when Peek(1) is 'b' or 'B':
                        _at += 2;
                        return Fragment.Assertion(Mark.Other, _strings);

                    // Any character but a line terminator: it bounds nothing.
                    case '.':
                        return null;

                    // A quantifier with nothing before it; and a lone brace or bracket, which a reading by unit takes for a
                    // literal and one by code point refuses.
                    case '*' or '+' or '?' or '{' or '}' or ']':
                        return null;

                    case '(':
                    {
                        _at++;

                        // A non-capturing group. Every other `(?` is a lookaround, a named group or a flag: one changes
                        // what the rest matches, and the others are read differently, or not at all, by one reading. A
                        // capturing group admits what a non-capturing one does, there being no back-reference to read it.
                        if (Peek() == '?')
                        {
                            if (Peek(1) != ':')
                                return null;

                            _at += 2;
                        }

                        if (depth >= Depth || Disjunction(depth + 1) is not { } inner || Take(')') == false)
                            return null;

                        atom = inner;
                        break;
                    }

                    case '[':
                        _at++;
                        if (Class() is not { } members)
                            return null;

                        atom = Characters(members);
                        break;

                    case '\\':
                    {
                        _at++;
                        if (Escape(inClass: false, out var spans) == false)
                            return null;

                        atom = Characters(spans);
                        break;
                    }

                    default:
                    {
                        var c = Character();
                        atom = Characters(new[] { (c, c) });
                        break;
                    }
                }

                return Quantified(atom);
            }

            /// <summary>
            /// Reads a quantifier where there is one, and the atom it quantifies: <c>*</c>, <c>+</c>, <c>?</c> or a count
            /// in braces, each perhaps lazy, which matches what the greedy one does. A brace that opens no count is refused,
            /// a reading by unit taking it for a literal and one by code point for an error; a quantifier after a
            /// quantifier is an error read either way, and the next term refuses it.
            /// </summary>
            Fragment? Quantified(Fragment atom)
            {
                long min;
                long? max;

                if (Take('*'))
                    (min, max) = (0, null);
                else if (Take('+'))
                    (min, max) = (1, null);
                else if (Take('?'))
                    (min, max) = (0, 1);
                else if (Take('{'))
                {
                    if (Digits() is not string least)
                        return null;

                    var most = least;
                    if (Take(','))
                        most = Digits();

                    if (Take('}') == false || most is not null && Below(most, least))
                        return null;

                    (min, max) = (Number(least), most is null ? null : Number(most));
                }
                else
                {
                    return atom;
                }

                Take('?');
                return atom.Repeated(min, max);
            }

            string? Digits()
            {
                var start = _at;
                while (AtEnd == false && _source[_at] is >= '0' and <= '9')
                    _at++;

                return _at > start ? _source.Substring(start, _at - start) : null;
            }

            /// <summary>
            /// Reads a count, however many digits it has, as itself or as one past <see cref="Count"/> where it is more.
            /// </summary>
            static long Number(string digits)
            {
                digits = digits.TrimStart('0');

                return digits.Length > 9 ? Count + 1L : Math.Min(digits.Length == 0 ? 0 : long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture), Count + 1L);
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
            /// Reads an atom matching one character of a set, recording the bands of every character in it.
            /// </summary>
            Fragment Characters(IEnumerable<(int Lo, int Hi)> spans)
            {
                var members = CosmosPatternClass.Of(spans);

                foreach (var (lo, hi) in members.Spans)
                    Add(lo, hi);

                return Fragment.Of(members, _strings);
            }

            /// <summary>
            /// Reads a character class after its <c>[</c>, positive or negated, as the characters it matches.
            /// </summary>
            List<(int Lo, int Hi)>? Class()
            {
                var negated = Take('^');
                var members = new List<(int Lo, int Hi)>();

                while (Take(']') == false)
                {
                    if (AtEnd || ClassAtom(out var from) == false)
                        return null;

                    // A range, unless the dash is the class's last member: `[a-]` is two members.
                    if (Peek() == '-' && _at + 1 < _source.Length && _source[_at + 1] != ']')
                    {
                        _at++;

                        if (ClassAtom(out var to) == false)
                            return null;

                        // `[\d-z]` is three members read by unit and an error read by code point; a range running
                        // backwards is an error read either way.
                        if (IsOne(from) == false || IsOne(to) == false || to[0].Lo < from[0].Lo)
                            return null;

                        members.Add((from[0].Lo, to[0].Lo));
                    }
                    else
                    {
                        members.AddRange(from);
                    }
                }

                // The complement over the reading's own alphabet: every unit to U+FFFF, or every code point to U+10FFFF.
                return negated ? Complement(members, _unicode ? 0x10FFFF : 0xFFFF) : members;

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
            void Add(int lo, int hi) => Bands |= JsonFactEncoding.Characters(lo, hi);

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
