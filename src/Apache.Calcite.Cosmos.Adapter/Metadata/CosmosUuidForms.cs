using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The UUID forms a declared <c>pattern</c> is recognised as, and what each one licenses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape is decided rather than looked up.</b> The axes a UUID pattern varies along do not
    /// close — which version nibbles are pinned, which digits the variant admits, how a character
    /// class is written, what prefix a container fixes — so a table of spellings reads most of what is
    /// published as stating <em>nothing</em>, which loses a path its equality along with its order.
    /// <see cref="Recognise"/> carries the argument for reading the shape instead.
    /// </para>
    /// <para>
    /// <b>The forms themselves are a table, and that is not a contradiction.</b> A <em>pattern</em>
    /// varies without bound; the <em>form</em> it denotes does not. A UUID is braced or not,
    /// hyphenated or not, upper or lower, confined or not, and there is nothing else for one to be —
    /// so <see cref="BuildUuids"/> enumerates sixteen and stops.
    /// </para>
    /// </remarks>
    public static class CosmosUuidForms
    {

        /// <summary>
        /// Whether the engine orders UUIDs as unsigned 128-bit values, which is what the two
        /// unconfined canonical rows rest on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It was not always so, and this reads the switch on the fix.</b>
        /// <c>java.util.UUID.compareTo</c> compares the two 64-bit halves as <em>signed</em> longs — a
        /// documented JDK quirk — so the lexical order of the canonical string was Calcite's order
        /// only where the top bit of each half was constant across the container, which is the 1st and
        /// 17th hex digits confined to one side of <c>8</c>. CALCITE-7716 treated that as a defect
        /// rather than as semantics and added <c>org.apache.calcite.util.UuidValue</c>, which compares
        /// unsigned, in 1.43.
        /// </para>
        /// <para>
        /// <b>Read rather than assumed, because the fix sits behind a property.</b>
        /// <c>calcite.uuid.unsigned.comparison</c> defaults to on, and a runtime that turns it off
        /// gets the old semantics back — over which an unconfined row claiming an order is a sort
        /// pushed to the service that returns the rows in the wrong order, which is the one failure
        /// this whole model exists to avoid. Calcite reads the property once into <c>UuidValue</c>'s
        /// own static, so reading it once here agrees with it for the life of the process.
        /// </para>
        /// <para>
        /// Measured, in <c>CalciteUuidOrderingMeasurementTests</c>: the property defaults to on, and
        /// under it <c>ORDER BY</c> over UUIDs is exactly <see cref="StringComparer.Ordinal"/> over
        /// their canonical spellings.
        /// </para>
        /// </remarks>
        static readonly bool UnsignedUuidComparison =
            ((java.lang.Boolean)org.apache.calcite.config.CalciteSystemProperty.UUID_UNSIGNED_COMPARISON.value()).booleanValue();

        /// <summary>
        /// A lowercase canonical UUID, whose ordinal text order is the unsigned order of the value.
        /// </summary>
        /// <remarks>
        /// The hyphens sit at fixed positions so they never decide anything, and <c>0</c>–<c>9</c>
        /// then <c>a</c>–<c>f</c> sort in nibble order, so comparing the stored strings compares the
        /// 32 nibbles in significance order — which is the unsigned comparison of the 128-bit value,
        /// for every value the form admits. Whether that is the order licensed is therefore not a
        /// question about the pattern at all but about the engine, and
        /// <see cref="UnsignedUuidComparison"/> is where it is asked.
        /// </remarks>
        public static readonly CosmosRepresentation CanonicalLower = new("uuid-canonical-lower", PreservesEquality: true, PreservesOrder: UnsignedUuidComparison);

        /// <summary>
        /// A lowercase canonical UUID whose first hex digit is confined and whose variant nibble is
        /// pinned, so that lexical order is Calcite's order under a signed comparison too.
        /// </summary>
        /// <remarks>
        /// Under the unsigned comparison that is the default this licenses nothing
        /// <see cref="CanonicalLower"/> does not, and it is kept rather than folded into it
        /// because the engine's switch can be turned off. Signed and unsigned comparison of two 64-bit
        /// values agree exactly where their top bits match, and that is what confining the 1st hex
        /// digit to <c>[0-7]</c> and pinning the 17th to the RFC variant range <c>8</c>–<c>b</c> buys:
        /// the high half's sign is then constant across the container and the low half's always was.
        /// </remarks>
        public static readonly CosmosRepresentation CanonicalLowerSortable = new("uuid-canonical-lower-sortable", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// An uppercase canonical UUID, which is as canonical as the lowercase one and spelled differently.
        /// </summary>
        /// <remarks>
        /// Canonical means one value has one spelling, not that the spelling is the one RFC 4122 prints.
        /// A container written in uppercase throughout is exactly as addressable; what changes is which
        /// way a comparison has to render its literal, and <see cref="Render"/> is where that is
        /// decided. What is <em>not</em> canonical is a container holding both, which no pattern here
        /// recognises — and which is why the case never costs the ordering either: <c>A</c>–<c>F</c>
        /// sit above <c>0</c>–<c>9</c> in code point order exactly as <c>a</c>–<c>f</c> do, so an
        /// all-uppercase container sorts in nibble order on the same terms as an all-lowercase one.
        /// </remarks>
        public static readonly CosmosRepresentation CanonicalUpper = new("uuid-canonical-upper", PreservesEquality: true, PreservesOrder: UnsignedUuidComparison);

        /// <inheritdoc cref="CanonicalLowerSortable" />
        public static readonly CosmosRepresentation CanonicalUpperSortable = new("uuid-canonical-upper-sortable", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// One of the sixteen UUID forms, as the four independent things a pattern says about it.
        /// </summary>
        /// <remarks>
        /// Only the first two decide how a literal is written; the last two decide what the form
        /// licenses and which name it carries. They are one record because a form is the product of
        /// all four and nothing here is ever asked about three of them.
        /// </remarks>
        /// <param name="Braced">Whether every stored string is wrapped in <c>{</c> and <c>}</c>.</param>
        /// <param name="Hyphenated">Whether the four hyphens are written.</param>
        /// <param name="Upper">Whether the hex digits are written in uppercase.</param>
        /// <param name="Confined">Whether both halves' signs are pinned across the container.</param>
        readonly record struct UuidSpelling(bool Braced, bool Hyphenated, bool Upper, bool Confined);

        /// <summary>
        /// The form each spelling is, keyed by the name it carries.
        /// </summary>
        /// <remarks>
        /// Read by <see cref="Render"/> the way <see cref="CosmosTemporalForms.Temporal"/> is read by
        /// <see cref="CosmosTemporalForms.Render"/>: recognition hands back a name, and writing a
        /// literal needs the shape the name stands for.
        /// </remarks>
        static readonly Dictionary<string, UuidSpelling> Uuids = BuildUuids();

        /// <summary>
        /// Builds the table of UUID forms, one per combination of the four axes.
        /// </summary>
        /// <remarks>
        /// Sixteen rows, and they are rows rather than a decision because these axes <em>do</em>
        /// close: a UUID is braced or not, hyphenated or not, upper or lower, confined or not, and
        /// there is nothing else for a form to be. That is the distinction
        /// <see cref="Recognise"/> turns on — the *pattern* varies without bound and the *form* it
        /// denotes does not.
        /// </remarks>
        /// <returns>The table.</returns>
        static Dictionary<string, UuidSpelling> BuildUuids()
        {
            var uuids = new Dictionary<string, UuidSpelling>(StringComparer.Ordinal);

            foreach (var braced in new[] { false, true })
                foreach (var hyphenated in new[] { false, true })
                    foreach (var upper in new[] { false, true })
                        foreach (var confined in new[] { false, true })
                        {
                            var spelling = new UuidSpelling(braced, hyphenated, upper, confined);
                            uuids[UuidForm(spelling).Name] = spelling;
                        }

            return uuids;
        }

        /// <summary>
        /// The form one spelling is, named systematically and licensing what the spelling licenses.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What the shape axes cost, which is nothing.</b> A brace is in the same place in every
        /// stored string and the hyphens are in the same places or in none, so neither ever decides a
        /// comparison: the strings still compare nibble by nibble in significance order, and one value
        /// still has one spelling. So all sixteen forms carry the same two bits, and the shape decides
        /// only how <see cref="Render"/> writes a literal into the container.
        /// </para>
        /// <para>
        /// <b>The four canonical names predate this and are not renamed.</b> They appear in
        /// diagnostics, in <c>DESIGN.md</c> and in the tests, so the scheme is chosen to reproduce
        /// them rather than to replace them — <c>uuid-canonical-lower</c> and
        /// <c>uuid-canonical-lower-sortable</c> are what <c>uuid-{shape}-{case}</c> already spells for
        /// the hyphenated, unbraced shape.
        /// </para>
        /// </remarks>
        /// <param name="spelling">The four axes.</param>
        /// <returns>The form.</returns>
        static CosmosRepresentation UuidForm(UuidSpelling spelling)
        {
            var shape = (spelling.Braced, spelling.Hyphenated) switch
            {
                (false, true) => "canonical",
                (false, false) => "hyphenless",
                (true, true) => "braced",
                (true, false) => "braced-hyphenless",
            };

            var name = $"uuid-{shape}-{(spelling.Upper ? "upper" : "lower")}{(spelling.Confined ? "-sortable" : string.Empty)}";

            return new CosmosRepresentation(name, PreservesEquality: true, PreservesOrder: spelling.Confined || UnsignedUuidComparison);
        }

        /// <summary>
        /// Where each hyphen sits in a canonical UUID, counted in nibbles before it.
        /// </summary>
        static readonly int[] Hyphens = { 8, 12, 16, 20 };

        /// <summary>
        /// How many nibbles a canonical UUID spells.
        /// </summary>
        const int Nibbles = 32;

        /// <summary>
        /// The nibble whose top bit is the sign of <c>mostSigBits</c>.
        /// </summary>
        const int HighSignNibble = 0;

        /// <summary>
        /// The nibble whose top bit is the sign of <c>leastSigBits</c> — the RFC variant nibble.
        /// </summary>
        const int LowSignNibble = 16;

        /// <summary>
        /// Reads a declared pattern as a canonical UUID, deciding the shape rather than looking it up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is not a table, and what the table cost.</b> A UUID pattern is written along
        /// axes that do not close. The version nibble is pinned to a digit, or to <c>[1-5]</c> because
        /// RFC 4122 defined five, or to <c>[1-8]</c> because RFC 9562 defined eight, or to <c>[45]</c>
        /// because the writer generates both; the variant nibble is <c>[89ab]</c> or <c>[8-9a-b]</c>
        /// or <c>[89a-b]</c>; the hex class is <c>[0-9a-f]</c> or <c>[0-9abcdef]</c> or
        /// <c>[a-cd-f0-9]</c>; a container with a known epoch pins a prefix. Enumerating that product
        /// is hopeless, and the failure was not a missing optimisation: a spelling outside the table
        /// was recognised as <em>nothing</em>, so the path lost its equality as well as its order and
        /// every comparison against it read whole documents.
        /// </para>
        /// <para>
        /// <b>What is decided instead, and why it is still a proof.</b> The shape is 32 nibble slots,
        /// anchored at both ends, where every slot admits some set of hex digits — with the four
        /// hyphens at their fixed positions or absent altogether, and the whole thing brace-wrapped or
        /// not. Those two are axes of the spelling rather than of the argument: a brace sits in the
        /// same place in every stored string and the hyphens sit in the same places or in none, so
        /// neither ever decides a comparison. Three things follow for <em>every</em> string such a pattern accepts,
        /// with nothing enumerated. One spelling per value, so long as the whole pattern draws from a
        /// single case — which is <see cref="Classify"/>'s test, and is the one thing the shape does
        /// not give on its own. Lexical order is nibble order, because the hyphens sit at fixed
        /// positions and so never decide a comparison, and <c>0</c>–<c>9</c> sort below <c>a</c>–<c>f</c>
        /// and below <c>A</c>–<c>F</c> alike. And nibble order taken in significance order is the
        /// unsigned order of the 128-bit value. Confining a slot only removes strings, so a narrower
        /// pattern inherits all three from the widest one.
        /// </para>
        /// <para>
        /// <b>The confinement is read off the shape rather than matched.</b> Under the signed
        /// comparison <see cref="UnsignedUuidComparison"/> guards, lexical order is Calcite's order
        /// only where each half's sign is constant, and the sign of a half is the top bit of one
        /// nibble: the 1st and the 17th. So a form is sortable-regardless exactly where slot
        /// <see cref="HighSignNibble"/> is confined to <c>0</c>–<c>7</c> and slot
        /// <see cref="LowSignNibble"/> to <c>8</c>–<c>f</c>, which is what RFC 4122's variant gives
        /// for free and what a v7 container gives for the first. A shape sign-constant the other way
        /// round is sound on the same argument and is deliberately <em>not</em> claimed here, because
        /// the claim would need a name of its own for <see cref="Render"/> to refuse literals
        /// against; under the unsigned default the plain row carries its order anyway.
        /// </para>
        /// <para>
        /// <b>An alternation is a union rather than a special case.</b> The nil-UUID alternation the
        /// uuid package documents is a second body whose every slot is pinned to <c>0</c>, and taking
        /// the union of the two bodies' slots derives what used to be written down by hand: the 17th
        /// slot becomes <c>[089ab]</c>, which is no longer confined to <c>8</c>–<c>f</c>, so the
        /// pattern lands on the plain row exactly as it did before. Any other alternation — two
        /// version nibbles written as two branches, a nil beside a max — is read on the same terms.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The pattern, already normalised.</param>
        /// <returns>The form, or <c>null</c> where the pattern is not a canonical UUID.</returns>
        internal static CosmosRepresentation? Recognise(string pattern)
        {
            return MatchUuid(pattern, 0) is UuidShape shape ? Classify(shape) : null;
        }

        /// <summary>
        /// What one pattern says a stored UUID looks like: which digits each nibble admits, and which
        /// of the three spellings the value is written in.
        /// </summary>
        /// <remarks>
        /// The slots decide which relations the form preserves and the two flags decide only how a
        /// literal is written into it, which is the same split the temporal forms make. A
        /// constant brace and an absent hyphen change neither relation: they are in the same place in
        /// every stored string, so they never decide a comparison.
        /// </remarks>
        /// <param name="Slots">What each of the 32 nibbles admits.</param>
        /// <param name="Braced">Whether every stored string is wrapped in <c>{</c> and <c>}</c>.</param>
        /// <param name="Hyphenated">Whether the four hyphens are written.</param>
        sealed record UuidShape(string[] Slots, bool Braced, bool Hyphenated);

        /// <summary>
        /// Reads an anchored expression, a group around one, or an alternation of them.
        /// </summary>
        /// <remarks>
        /// Each alternative carries its own anchors here, because a top-level <c>|</c> binds looser
        /// than they do: <c>^a|b$</c> is <c>(^a)|(b$)</c>, whose second branch is unanchored at the
        /// front and admits a conforming value with anything before it. Inside an anchored body the
        /// anchors are already outside the alternation, which is why <see cref="MatchBody"/> asks for
        /// none.
        /// </remarks>
        /// <param name="pattern">The expression.</param>
        /// <param name="depth">Bounds the recursion a nested group can drive.</param>
        /// <returns>The shape, or <c>null</c>.</returns>
        static UuidShape? MatchUuid(string pattern, int depth)
        {
            if (depth > Nesting)
                return null;

            if (CosmosPatternLanguage.SplitAlternatives(pattern) is { Count: > 1 } alternatives)
                return Union(alternatives, part => MatchUuid(part, depth + 1));

            if (CosmosPatternLanguage.Unwrap(pattern) is string grouped)
                return MatchUuid(grouped, depth + 1);

            if (pattern.Length < 2 || pattern[0] != '^' || pattern[pattern.Length - 1] != '$')
                return null;

            return MatchBody(pattern.Substring(1, pattern.Length - 2), depth + 1);
        }

        /// <summary>
        /// Reads the inside of an anchored expression: the slots themselves, a group around them, or
        /// an alternation of either.
        /// </summary>
        /// <param name="body">The body, with the anchors already taken off.</param>
        /// <param name="depth">Bounds the recursion a nested group can drive.</param>
        /// <returns>The shape, or <c>null</c>.</returns>
        static UuidShape? MatchBody(string body, int depth)
        {
            if (depth > Nesting)
                return null;

            if (CosmosPatternLanguage.SplitAlternatives(body) is { Count: > 1 } alternatives)
                return Union(alternatives, part => MatchBody(part, depth + 1));

            if (CosmosPatternLanguage.Unwrap(body) is string grouped)
                return MatchBody(grouped, depth + 1);

            return MatchSlots(body);
        }

        /// <summary>
        /// How deep a pattern may nest groups and alternations before it is refused rather than read.
        /// </summary>
        const int Nesting = 8;

        /// <summary>
        /// Reads a run of single-character atoms as the nibbles and hyphens of a canonical UUID.
        /// </summary>
        /// <remarks>
        /// The layout is checked after the atoms are read rather than while they are, because a
        /// pattern is free to write the same language with its runs counted or spelled out and
        /// <see cref="CosmosPatternLanguage.Collapse"/> has already made those one spelling. What the layout asks is only
        /// that the hyphens land where a canonical UUID puts them.
        /// </remarks>
        /// <param name="body">The body.</param>
        /// <returns>The shape, or <c>null</c>.</returns>
        static UuidShape? MatchSlots(string body)
        {
            var braced = false;

            // A literal brace at the front cannot be a quantifier, nothing being there for it to
            // count, so it is read as the delimiter it is -- escaped or not, both spellings being in
            // use. The closing one is only looked for once the opening one is found, because a
            // trailing `}` is otherwise the end of a count.
            if (body.StartsWith(@"\{", StringComparison.Ordinal) || (body.Length > 0 && body[0] == '{'))
            {
                var open = body[0] == '{' ? 1 : 2;
                var close = body.EndsWith(@"\}", StringComparison.Ordinal) ? 2 : body.Length > 0 && body[body.Length - 1] == '}' ? 1 : 0;

                if (close == 0 || body.Length <= open + close)
                    return null;

                body = body.Substring(open, body.Length - open - close);
                braced = true;
            }

            var slots = new List<string?>();
            var i = 0;

            while (i < body.Length)
            {
                if (CosmosPatternLanguage.ReadAtom(body, ref i) is not string atom)
                    return null;

                var repeats = 1;

                if (i < body.Length && body[i] == '{')
                {
                    var close = body.IndexOf('}', i + 1);
                    if (close < 0)
                        return null;

                    // A range of widths is a different language, and a count wider than the shape
                    // cannot be one whatever else it is.
                    if (int.TryParse(body.Substring(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out repeats) == false || repeats > Nibbles)
                        return null;

                    i = close + 1;
                }

                // A quantifier admitting a range of widths ends the shape, whatever it quantifies.
                if (i < body.Length && (body[i] is '?' or '*' or '+'))
                    return null;

                string? slot;

                if (string.Equals(atom, "-", StringComparison.Ordinal) || string.Equals(atom, @"\-", StringComparison.Ordinal))
                    slot = null;
                else if (HexSet(atom) is string set)
                    slot = set;
                else
                    return null;

                for (var n = 0; n < repeats; n++)
                {
                    slots.Add(slot);

                    if (slots.Count > Nibbles + Hyphens.Length)
                        return null;
                }
            }

            // Hyphenated or not, and nothing in between: a shape carrying some of the four would give
            // one value a spelling per hyphen it happened to omit.
            var hyphenated = slots.Count == Nibbles + Hyphens.Length;

            if (hyphenated == false && slots.Count != Nibbles)
                return null;

            var read = new string[Nibbles];
            var nibble = 0;
            var hyphen = 0;

            foreach (var slot in slots)
            {
                if (slot is null)
                {
                    if (hyphen >= Hyphens.Length || Hyphens[hyphen] != nibble)
                        return null;

                    hyphen++;
                    continue;
                }

                if (nibble >= Nibbles)
                    return null;

                read[nibble] = slot;
                nibble++;
            }

            if (nibble != Nibbles || hyphen != (hyphenated ? Hyphens.Length : 0))
                return null;

            return new UuidShape(read, braced, hyphenated);
        }

        /// <summary>
        /// Reads the hex digits one atom admits, or returns <c>null</c> where it admits anything else.
        /// </summary>
        /// <remarks>
        /// A class is taken apart into its members rather than compared as text, which is the liberty
        /// <see cref="CosmosPatternLanguage.SortClass"/> takes one step further: <c>[89ab]</c>, <c>[8-9a-b]</c> and
        /// <c>[89a-b]</c> are one set written three ways, and a form that turned on which way would be
        /// recognising the writer rather than the language. A negation, an escape or a bare <c>-</c>
        /// is refused rather than guessed at, on the same grounds <see cref="CosmosPatternLanguage.SortClass"/> refuses it.
        /// </remarks>
        /// <param name="atom">A single-character atom.</param>
        /// <returns>The admitted digits, sorted and distinct, or <c>null</c>.</returns>
        static string? HexSet(string atom)
        {
            if (atom.Length == 1)
                return IsHex(atom[0]) ? atom : null;

            if (atom.Length < 3 || atom[0] != '[' || atom[atom.Length - 1] != ']')
                return null;

            var inner = atom.Substring(1, atom.Length - 2);

            if (inner[0] == '^' || inner.IndexOf('\\') >= 0)
                return null;

            var members = new SortedSet<char>();

            for (var i = 0; i < inner.Length; i++)
            {
                if (i + 2 < inner.Length && inner[i + 1] == '-')
                {
                    var low = inner[i];
                    var high = inner[i + 2];

                    if (low > high)
                        return null;

                    // A range is over code points rather than over hex digits, which is the trap this
                    // has to test for rather than around: `[8-f]` spans 0x38-0x66 and so admits `@`,
                    // `Z` and `_` beside the digits its writer meant -- and admits `A`-`F` as well as
                    // `a`-`f`, which is two spellings of one nibble. So every member is asked, not
                    // just the two ends.
                    for (var c = low; c <= high; c++)
                    {
                        if (IsHex(c) == false)
                            return null;

                        members.Add(c);
                    }

                    i += 2;
                    continue;
                }

                if (IsHex(inner[i]) == false)
                    return null;

                members.Add(inner[i]);
            }

            return string.Concat(members);
        }

        /// <summary>
        /// Determines whether a character spells a nibble, in either case.
        /// </summary>
        /// <param name="c">The character.</param>
        /// <returns><c>true</c> where it is a hex digit.</returns>
        static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        /// <summary>
        /// Takes the slot-wise union of several alternatives.
        /// </summary>
        /// <remarks>
        /// A union is what an alternation means, and it is the right thing to take because every claim
        /// a UUID form makes is a claim about the whole set of strings the pattern admits. A branch
        /// widening the 17th slot past <c>8</c>–<c>f</c> costs the confinement for the pattern rather
        /// than for the branch, which is exactly what the nil alternation should do.
        /// </remarks>
        /// <param name="alternatives">The branches.</param>
        /// <param name="match">Reads one branch.</param>
        /// <returns>The union, or <c>null</c> where a branch is not a canonical UUID.</returns>
        static UuidShape? Union(List<string> alternatives, Func<string, UuidShape?> match)
        {
            UuidShape? union = null;

            foreach (var alternative in alternatives)
            {
                if (match(alternative) is not UuidShape branch)
                    return null;

                if (union is null)
                {
                    union = branch;
                    continue;
                }

                // The branches have to agree on the spelling, because a value written one way in one
                // branch and another way in the other has two spellings and no equality survives it.
                if (union.Braced != branch.Braced || union.Hyphenated != branch.Hyphenated)
                    return null;

                for (var i = 0; i < Nibbles; i++)
                {
                    var members = new SortedSet<char>(union.Slots[i]);
                    members.UnionWith(branch.Slots[i]);
                    union.Slots[i] = string.Concat(members);
                }
            }

            return union;
        }

        /// <summary>
        /// Says which form a read shape is, or that it is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The case test is the one thing the shape does not give.</b> A pattern admitting both
        /// <c>a</c> and <c>A</c> gives one value two spellings, and a comparison against either misses
        /// the documents written the other way — so a pattern drawing from both cases anywhere states
        /// nothing, which is the answer the enumerated rows gave <c>[0-9a-fA-F]</c> and this gives it
        /// for the same reason. A shape admitting no letter at all — the nil UUID alone — is read as
        /// lowercase, which costs nothing: the values it admits have no letters to spell either way.
        /// </para>
        /// <para>
        /// <b>Every slot is hex already</b>, so there is nothing left to check about the language.
        /// </para>
        /// </remarks>
        /// <param name="shape">The read shape.</param>
        /// <returns>The form, or <c>null</c>.</returns>
        static CosmosRepresentation? Classify(UuidShape shape)
        {
            var lower = false;
            var upper = false;

            foreach (var slot in shape.Slots)
                foreach (var c in slot)
                {
                    lower |= c is >= 'a' and <= 'f';
                    upper |= c is >= 'A' and <= 'F';
                }

            if (lower && upper)
                return null;

            var confined = Within(shape.Slots[HighSignNibble], '0', '7') && Within(shape.Slots[LowSignNibble], '8', 'f');

            return UuidForm(new UuidSpelling(shape.Braced, shape.Hyphenated, upper, confined));
        }

        /// <summary>
        /// Determines whether every digit a slot admits falls in a range, reading the two cases alike.
        /// </summary>
        /// <param name="slot">The admitted digits.</param>
        /// <param name="low">The range's first digit, in lowercase.</param>
        /// <param name="high">The range's last digit, in lowercase.</param>
        /// <returns><c>true</c> where the slot admits nothing outside the range.</returns>
        static bool Within(string slot, char low, char high)
        {
            foreach (var c in slot)
            {
                var folded = c is >= 'A' and <= 'F' ? (char)(c - 'A' + 'a') : c;

                if (folded < low || folded > high)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Writes a UUID the way a path in this form stores it, or returns <c>null</c> where the form
        /// is not a UUID at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked by a rewrite that has to put a literal into the stored spelling before comparing
        /// against it. Which spelling that is, is the whole of what the UUID forms differ by: the
        /// comparison is exact either way, and a container written in uppercase is addressable on the
        /// same terms as one written in lowercase.
        /// </para>
        /// <para>
        /// <b>A literal outside the confined form's own sign class is refused, and only then.</b> The
        /// confined rows say the lexical order <em>is</em> the engine's order under a signed
        /// comparison, and the argument for that is that each half's sign is constant — across the
        /// container. The literal is not in the container, and a comparison is against one of each: a
        /// path confined to a first digit of <c>0</c>–<c>7</c> holds only values whose high half is
        /// signed-positive, so <c>&gt;</c> against a literal whose high half is signed-negative is
        /// true of every document and lexically true of none. So the literal is asked for the two
        /// bits the confinement pins, and a rewrite is declined rather than answered wrongly.
        /// </para>
        /// <para>
        /// <b>It is asked only where the answer can differ</b>, which is the switch being off. Under
        /// <see cref="UnsignedUuidComparison"/> the lexical order is the engine's order for every pair
        /// of canonical spellings, in the container or out of it, so the class costs nothing and
        /// refusing on it would decline sound rewrites — including the equality ones, which never
        /// needed the sign at all and which push a predicate that correctly selects nothing.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <param name="value">The value to write.</param>
        /// <returns>The stored spelling, or <c>null</c> where this form does not store a UUID.</returns>
        public static string? Render(CosmosRepresentation representation, Guid value)
        {
            if (Uuids.TryGetValue(representation.Name, out var spelling) == false)
                return null;

            if (UnsignedUuidComparison == false && spelling.Confined && InSignClass(value) == false)
                return null;

            var text = value.ToString(spelling.Hyphenated ? "D" : "N");

            if (spelling.Upper)
                text = text.ToUpperInvariant();

            return spelling.Braced ? "{" + text + "}" : text;
        }

        /// <summary>
        /// Determines whether a value has the two sign bits the confined forms pin.
        /// </summary>
        /// <remarks>
        /// Read off the hyphenless spelling, where a nibble's index into the text is its own index and
        /// the two the confinement pins are therefore <see cref="HighSignNibble"/> and
        /// <see cref="LowSignNibble"/> exactly.
        /// </remarks>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> where the high half is signed-positive and the low half signed-negative.</returns>
        static bool InSignClass(Guid value)
        {
            var text = value.ToString("N");

            return Within(text.Substring(HighSignNibble, 1), '0', '7') && Within(text.Substring(LowSignNibble, 1), '8', 'f');
        }

        /// <summary>
        /// Determines whether a form spells a UUID, in any of the sixteen ways.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked where the spelling does not matter but the type does — a projection reading the
        /// stored text back as the <c>UUID</c> the plan declared, which needs to know only that the
        /// text is one. That is a claim about the reader rather than about this table, and it is
        /// measured: <c>SqlFunctions.stringToUuid</c> reads all four shapes — hyphenated, hyphenless,
        /// and either of those braced — in either case, so every form here parses back to the value
        /// the container means. It reads neither the <c>urn:uuid:</c> nor the parenthesised spelling,
        /// which is one of the reasons neither is a form here. <c>ShouldReadEverySpellingThisRecognises</c>
        /// is the measurement.
        /// </para>
        /// <para>
        /// Sortability is not consulted and must not be: it says whether the lexical order of the
        /// stored strings is the order Calcite compares the values in, which a projection never asks.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <returns><c>true</c> where the form stores a UUID.</returns>
        public static bool IsUuid(CosmosRepresentation representation) => Uuids.ContainsKey(representation.Name);

    }

}
