using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rel;
using org.apache.calcite.rel.type;
using org.apache.calcite.sql.type;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Whether the service may compute an order: a sort, a <c>MIN</c> or a <c>MAX</c>, or an ordering comparison —
    /// asked once, here, of the facts each consumer was decided under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule.</b> An order the service computes is pushed only where the facts prove its answer is one Calcite
    /// allows, for every database they admit: ties are Calcite's to break any way, and with no <c>ORDER BY</c> the order
    /// is free, but where Calcite orders two rows strictly the service has to as well. Declining costs a sort in process;
    /// an answer Calcite does not allow is a wrong answer nothing downstream notices.
    /// </para>
    /// <para>
    /// <b>Three orders, not two.</b> Within a physical partition the service orders stored values by JSON type first —
    /// undefined, null, false, true, numbers, strings, arrays, objects, <c>DESC</c> the exact reverse — numbers by value,
    /// int64 exactly, and strings by code point, with no collation. Across partitions the SDK merges the partitions'
    /// streams comparing strings by UTF-16 code unit, so a stream from a container with several can be in neither order.
    /// Calcite compares strings by code unit too. Measured on the emulator and on Azure, one partition and two (the
    /// probe account's report). Every proof here makes all three one order over the values admitted, which is why none
    /// depends on how many partitions a container has.
    /// </para>
    /// <para>
    /// <b>Four rules decide every reading.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>R1, characters.</b> Code point and UTF-16 order part only where a surrogate unit meets a
    /// unit from U+E000 (<see cref="CosmosCharacterBand"/>): at the first unit two strings differ in, the prefixes agree,
    /// a unit below U+D800 orders alike against anything, and only a surrogate against a high unit reverses. So two sets
    /// of strings order alike unless one may hold a surrogate and the other a high unit. A path's characters are what
    /// the facts say of it as <see cref="CosmosCharacters"/>, one claim per band, which a declared pattern, a service
    /// fact, its domain and a stored form — every registered form is ASCII — each establish; and otherwise anything:
    /// Azure allows every character in <c>id</c> but <c>/</c> and <c>\</c>. A literal's are its own, which under the
    /// default charset are always below U+0100. Two
    /// constants the service compares by UTF-16 on Azure and by code point on the emulator, and a range whose bounds
    /// the two orders rank apart keeps nothing on Azure; both are inside what R1 refuses, since each needs a surrogate
    /// on one side and a high unit on the other.</description></item>
    /// <item><description><b>R2, readings.</b> Calcite orders what the column reads, and the service the stored value.
    /// Text over strings is the strings; text over a number or a boolean is a rendering, which sorts among the strings
    /// as text where the service sorts it by type and value. <c>INTEGER</c> and narrower read exactly what they read,
    /// raising on the rest (<c>Calcite.ReturningAsserts</c>). What a response prints is another matter: a double past
    /// int64 comes back on the emulator as <c>9223372036854775800</c>, below <c>2^63 - 1</c>, though the service holds
    /// and orders it above, so a <c>BIGINT</c> or a <c>DECIMAL</c> read back is the stored order only within int64, which
    /// Azure measured exact; and a <c>DOUBLE</c> reads an int64 past 2^53 as its nearest double, which ties what the
    /// service parts, so it is exact within ±2^53 and refines past it. A comparison reads no response — Calcite compares
    /// the document — so there the readings are exact. An instant or a UUID is a string at the service, ordered as one
    /// only by a stored form that keeps the order and that Calcite can read. A <c>VARIANT</c>, a collection, a JSON
    /// fragment and the document column have no order Calcite and the service share.</description></item>
    /// <item><description><b>R3, the null class.</b> What a reading reads as null — an absent path, a JSON null, and any
    /// type the reading does not read — has to lie wholly on the side of the values Calcite puts nulls (an unspecified
    /// placement is last, as Calcite runs it), the values have to be one JSON type, and before the last key the null
    /// class has to be one tie class: the service puts undefined before null where Calcite ties them, so a later key would
    /// order the two where the service already has.</description></item>
    /// <item><description><b>R4, keys.</b> Every key but the last must agree exactly, ties included; the last may be
    /// finer at the service. A key the facts make one value over every row orders nothing and is not written, nor is one
    /// whose reading reads every row as null, which Calcite ties however the service orders what it holds, nor a key an
    /// earlier exact key on the same path already ties — which also keeps a repeated key out of the composite
    /// index question, Azure refusing even <c>ORDER BY c.a, c.a</c> without one. Calcite's own constant-key rule is
    /// inert in this planner.</description></item>
    /// </list>
    /// <para>
    /// <b>No consumer reads characters, types, presence or bounds for an order itself.</b> Each builds its operands from
    /// its binding and hands over the facts it was decided under — a sort's and an extreme's last stratum, a comparison's
    /// own, the weakening's applied set — so that a rule and the node it builds, asking the same question of the same
    /// facts, cannot disagree. What the service <em>accepts</em> is not asked here: a key's path being indexed, a
    /// composite index, one <c>ORDER BY</c> per statement — see <see cref="CosmosContainerMetadata.IsSortSupported"/>.
    /// </para>
    /// <para>
    /// <b>What the plan cannot prove, the rows may.</b> Where the one thing missing is what the strings' characters are,
    /// or how large a number read back is, the facts still say which <em>values</em> every other compares with alike —
    /// an anchor (<see cref="CosmosOrderAnchor"/>, <see cref="IsAnchor"/>) — and the answer is
    /// <see cref="CosmosOrderVerdictKind.Anchored"/>: the reader checks each value as it arrives, keeps the service's order
    /// around the anchors and re-sorts what lies between them, and where a page's edge or an extreme or a parameter is no
    /// anchor, reads a fallback statement instead (<c>Client.CosmosOrderedCursor</c>). The owner counts a proof completed
    /// at run time as one (D1). Everything else the facts do not prove — a null placement, a mixed type, a rendering —
    /// no value can certify, and is refused. A sort split by JSON type into two statements is the next answer.
    /// </para>
    /// </remarks>
    internal static class CosmosServiceOrder
    {

        /// <summary>
        /// The service's JSON types, in its type order.
        /// </summary>
        [Flags]
        enum Classes
        {
            None = 0,
            Undefined = 1,
            Null = 2,
            Boolean = 4,
            Number = 8,
            String = 16,
            Array = 32,
            Object = 64,
            All = 127,
        }

        /// <summary>
        /// What an operand's values are under its reading: how the two orders over them stand, which types read as
        /// values and which as null, what characters the strings may hold, whether every row holds one value, and what
        /// check of each value would complete an agreement that falls short.
        /// </summary>
        readonly record struct Reading(CosmosOrderAgreement Agreement, Classes Values, Classes Nulls, CosmosCharacterBand Characters, bool Constant, JsonDocumentPath? Path, string Why, CosmosOrderAnchor Anchorable = CosmosOrderAnchor.None);

        /// <summary>
        /// Both bands: what strings nothing states the characters of may hold.
        /// </summary>
        const CosmosCharacterBand AnyCharacter = CosmosCharacterBand.Surrogates | CosmosCharacterBand.HighBmp;

        /// <summary>
        /// 2^53: past it a double ties an integer with its neighbour, and within it every integer is one value however
        /// the service holds it or a response prints it.
        /// </summary>
        internal const decimal TwoTo53 = 9007199254740992m;

        const decimal Int64Least = -9223372036854775808m;

        const decimal Int64Most = 9223372036854775807m;

        /// <summary>
        /// Asks whether the service may order rows by these keys, and which it is handed.
        /// </summary>
        /// <param name="facts">What every row ordered is known to satisfy: the sort's own row of the licence table.</param>
        /// <param name="keys">The keys, in order.</param>
        /// <returns>A proof naming the keys to write, or a refusal saying why.</returns>
        public static CosmosOrderVerdict OrderBy(JsonFactSet facts, IReadOnlyList<CosmosOrderKey> keys)
        {
            if (facts is null)
                throw new ArgumentNullException(nameof(facts));
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            var written = new List<int>(keys.Count);
            var readings = new List<(Reading Reading, bool Descending, bool NullsFirst, CosmosOrderedKeyKind Kind)>(keys.Count);

            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];

                if (Direction(key.Collation, out var descending, out var nullsFirst) is string unrunnable)
                    return CosmosOrderVerdict.Refused(unrunnable);

                // A score is ranked best first, and the service takes no direction for it (SC2222, measured on Azure): the
                // one collation it answers is the one that puts the best first.
                if (key.Operand.Kind == CosmosOrderOperandKind.Score && (key.Operand.BestFirstDescending == false || descending == false))
                    return CosmosOrderVerdict.Refused(key.Operand.BestFirstDescending
                        ? "the service ranks a score best first, which is descending"
                        : "the service ranks this score by a distance function nothing declares the direction of");

                var reading = Read(facts, key.Operand, printed: true);

                // R4: a key one value over every row orders nothing, whatever it reads that value as; and nor does a key
                // that reads every row as null, Calcite tying every null, though the service holds values there.
                if (reading.Constant || reading.Agreement != CosmosOrderAgreement.None && reading.Values == Classes.None)
                    continue;

                // R4: and nor does a key on a path an earlier key orders exactly. Tied there, two rows hold one stored
                // value, which every reading reads alike; refining is not enough, a finer earlier key having already
                // ordered rows this one might order the other way.
                if (reading.Path is JsonDocumentPath path && readings.Exists(r => r.Reading.Path == path && r.Reading.Agreement == CosmosOrderAgreement.Exact))
                    continue;

                written.Add(i);
                readings.Add((reading, descending, nullsFirst, ComparedAs(key.Operand)));
            }

            var anchors = new CosmosOrderAnchor[readings.Count];
            var unproved = default(string);

            for (var j = 0; j < readings.Count; j++)
            {
                var (reading, descending, nullsFirst, _) = readings[j];
                var last = j == readings.Count - 1;

                // R2, and where a number read back may not be what the service ordered, what the rows can still prove:
                // a value within 2^53 orders alike against every other, read back or not.
                if (reading.Agreement == CosmosOrderAgreement.None || last == false && reading.Agreement != CosmosOrderAgreement.Exact)
                {
                    var why = last ? reading.Why : "a key before the last orders only " + Grade(reading) + ": " + reading.Why;
                    if (reading.Anchorable == CosmosOrderAnchor.None)
                        return CosmosOrderVerdict.Refused(why);

                    anchors[j] = reading.Anchorable;
                    unproved ??= why;
                }

                if (IsOneClass(reading.Values) == false)
                    return CosmosOrderVerdict.Refused("two JSON types, which the service orders by type first");

                // R1: one path's strings against each other; and where their characters are not known, a string below
                // U+D800 still orders alike against every other, which the rows can show.
                if (OrdersItsStrings(reading) == false)
                {
                    anchors[j] = CosmosOrderAnchor.Text;
                    unproved ??= "text across U+D800: " + Describe(reading.Characters);
                }

                // R3: the null class on Calcite's side of the values. Ascending puts the low types first at the service,
                // descending the high ones; Calcite puts nulls first or last whatever the direction.
                var low = nullsFirst != descending;
                if (OnTheSide(reading.Nulls, reading.Values, low) == false)
                    return CosmosOrderVerdict.Refused("null placement: the service places " + Describe(reading.Nulls) + (low ? " above" : " below") + " the values");

                // R3: and one tie class before the last key, the service ordering undefined below null.
                if (last == false && reading.Nulls is not (Classes.None or Classes.Undefined or Classes.Null))
                    return CosmosOrderVerdict.Refused("undefined and null on a leading key, which Calcite ties and the service does not");
            }

            // Each written key as decided: how Calcite compares it, the direction and placement Calcite runs, and its anchor.
            var ordered = new CosmosOrderedKey[readings.Count];
            for (var j = 0; j < readings.Count; j++)
                ordered[j] = new CosmosOrderedKey(null, readings[j].Kind, readings[j].Descending, readings[j].NullsFirst, anchors[j]);

            if (unproved is not null)
                return CosmosOrderVerdict.Anchored("anchored, " + unproved, written, ordered);

            return CosmosOrderVerdict.Proved(written.Count == keys.Count ? "proved" : "proved, with constant keys dropped", written, ordered);
        }

        /// <summary>
        /// Asks whether the service's <c>MIN</c> or <c>MAX</c> of an operand is Calcite's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The service's extreme follows its type order: it skips an absent path, counts a JSON null, and is undefined
        /// over a set holding an array or an object; Calcite skips every value it reads as null. So the operand's null
        /// class may hold the absent path and nothing else, its values one type, ordered as Calcite orders them —
        /// refining is enough, the service's extreme then lying in Calcite's extreme tie class. Measured on Azure, over
        /// one partition and the merge of two.
        /// </para>
        /// <para>
        /// <b>Not whether the path is indexed.</b> An aggregate scans, and Azure answers <c>MIN</c> and <c>MAX</c> over a
        /// path its policy excludes, <c>_etag</c> among them, where it refuses an <c>ORDER BY</c>.
        /// </para>
        /// </remarks>
        /// <param name="facts">What every row aggregated is known to satisfy: the grouping's own row of the licence table.</param>
        /// <param name="operand">The argument.</param>
        /// <returns>A proof, or a refusal saying why.</returns>
        public static CosmosOrderVerdict Extreme(JsonFactSet facts, CosmosOrderOperand operand)
        {
            if (facts is null)
                throw new ArgumentNullException(nameof(facts));
            if (operand is null)
                throw new ArgumentNullException(nameof(operand));

            var reading = Read(facts, operand, printed: true);
            var anchor = CosmosOrderAnchor.None;
            var unproved = default(string);

            if (reading.Agreement == CosmosOrderAgreement.None)
            {
                if (reading.Anchorable == CosmosOrderAnchor.None)
                    return CosmosOrderVerdict.Refused(reading.Why);

                anchor = reading.Anchorable;
                unproved = reading.Why;
            }

            if (IsOneClass(reading.Values) == false)
                return CosmosOrderVerdict.Refused("two JSON types, which the service's extreme compares by type first");

            if (OrdersItsStrings(reading) == false)
            {
                anchor = CosmosOrderAnchor.Text;
                unproved ??= "text across U+D800: " + Describe(reading.Characters);
            }

            if ((reading.Nulls & ~Classes.Undefined) != Classes.None)
                return CosmosOrderVerdict.Refused("the service's extreme counts " + Describe(reading.Nulls & ~Classes.Undefined) + ", which Calcite skips");

            // An extreme that is an anchor is Calcite's: every partition's lies at or beyond it in the partition's order,
            // and every value on the far side of an anchor in one order is on the far side in the other, across the
            // merge too. Any other answer is read again, in order, up to the first anchor.
            return unproved is null
                ? CosmosOrderVerdict.Proved("proved")
                : CosmosOrderVerdict.Anchored("anchored, " + unproved, new[] { anchor });
        }

        /// <summary>
        /// Asks whether the service's ordering comparison of two operands keeps exactly the rows Calcite's does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Exact, not finer.</b> A comparison keeps a row or drops it, and there is no tie for Calcite to be free in,
        /// so each side has to read exactly what the service compares and the two sides' strings have to order alike
        /// (R1). A rendering of a number or a boolean does not: it compares as text, and at the service a number is
        /// another type than any string, which the comparison leaves undefined.
        /// </para>
        /// <para>
        /// <b>What reads as null on one side drops the row at the service only where the other side holds another
        /// type</b>, the service's comparison across types being undefined, as Calcite's with a null is unknown; and the
        /// two are one under <c>NOT</c>. Where both sides may hold one JSON type that a side reads as null, the service
        /// compares the two as values: measured on the emulator, <c>c.t &lt;= c.t</c> and <c>c.t = c.t</c> over a path
        /// declared a string or null keep the documents holding a JSON null, which Calcite drops. So the null classes are
        /// asked, of the two sides together (<see cref="Equate"/> asks the same of an equality).
        /// </para>
        /// </remarks>
        /// <param name="facts">What the comparison may rely on: its own row of the licence table.</param>
        /// <param name="left">One side.</param>
        /// <param name="right">The other.</param>
        /// <param name="inclusive">Whether two equal values satisfy the comparison — <c>&lt;=</c> and <c>&gt;=</c> — so that
        /// a JSON null against a JSON null keeps the row at the service.</param>
        /// <returns>A proof, or a refusal saying why.</returns>
        public static CosmosOrderVerdict Compare(JsonFactSet facts, CosmosOrderOperand left, CosmosOrderOperand right, bool inclusive)
        {
            if (facts is null)
                throw new ArgumentNullException(nameof(facts));
            if (left is null)
                throw new ArgumentNullException(nameof(left));
            if (right is null)
                throw new ArgumentNullException(nameof(right));

            var l = Read(facts, left, printed: false);
            var r = Read(facts, right, printed: false);

            if (l.Agreement != CosmosOrderAgreement.Exact)
                return CosmosOrderVerdict.Refused(l.Why);

            if (r.Agreement != CosmosOrderAgreement.Exact)
                return CosmosOrderVerdict.Refused(r.Why);

            if (ComparedNulls(l, r, inclusive) is string nulls)
                return CosmosOrderVerdict.Refused(nulls);

            if ((l.Values & r.Values & Classes.String) != Classes.None && Agree(l.Characters, r.Characters) == false)
            {
                var unproved = "text across U+D800: " + Describe(l.Characters) + " against " + Describe(r.Characters);

                // A parameter's value is known when the statement runs, and one below U+D800 compares alike with any string
                // a path holds: exact then, and otherwise read again under a bound it implies and rechecked in process.
                // Two paths' strings are never seen whole — the rows the service drops do not arrive — so nothing can.
                if (left.Kind == CosmosOrderOperandKind.Parameter && right.Kind == CosmosOrderOperandKind.Path
                    || right.Kind == CosmosOrderOperandKind.Parameter && left.Kind == CosmosOrderOperandKind.Path)
                    return CosmosOrderVerdict.Anchored("anchored, " + unproved, new[] { CosmosOrderAnchor.Text });

                return CosmosOrderVerdict.Refused(unproved);
            }

            return CosmosOrderVerdict.Proved("proved");
        }

        /// <summary>
        /// Asks whether the service's equality of two operands keeps no row Calcite's drops for a null: the question an
        /// equality shares with an ordering comparison.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only the null classes.</b> Whether each side reads what the service compares is the translator's, which
        /// lets a rendering through against text no other JSON value renders as; and characters do not matter, two
        /// strings being equal by code point exactly where they are by code unit. What is left is the row where both
        /// sides read null and the service holds one JSON type on both: a JSON null equals a JSON null at the service
        /// (<see cref="Compare"/>). An absent path is undefined against anything, and drops the row on both sides.
        /// </para>
        /// </remarks>
        /// <param name="facts">What the equality may rely on: its own row of the licence table.</param>
        /// <param name="left">One side.</param>
        /// <param name="right">The other.</param>
        /// <returns>A proof, or a refusal saying why.</returns>
        public static CosmosOrderVerdict Equate(JsonFactSet facts, CosmosOrderOperand left, CosmosOrderOperand right)
        {
            if (facts is null)
                throw new ArgumentNullException(nameof(facts));
            if (left is null)
                throw new ArgumentNullException(nameof(left));
            if (right is null)
                throw new ArgumentNullException(nameof(right));

            return ComparedNulls(Read(facts, left, printed: false), Read(facts, right, printed: false), inclusive: true) is string nulls
                ? CosmosOrderVerdict.Refused(nulls)
                : CosmosOrderVerdict.Proved("proved");
        }

        /// <summary>
        /// Says why the service would compare as values what one side reads as null, or returns <c>null</c> where it
        /// would not: where a side reads a JSON type as null that the other side may hold too. A JSON null against a
        /// JSON null counts only where equal values satisfy the comparison; an absent path never, being undefined
        /// against anything.
        /// </summary>
        static string? ComparedNulls(Reading l, Reading r, bool inclusive)
        {
            var shared = (l.Nulls & (r.Values | r.Nulls) | r.Nulls & (l.Values | l.Nulls)) & ~Classes.Undefined;
            if (inclusive == false)
                shared &= ~Classes.Null;

            return shared == Classes.None
                ? null
                : Describe(shared) + " on both sides, which the service compares as values and Calcite reads as null";
        }

        /// <summary>
        /// Determines whether a value certifies the order around it: whether every value the facts admit compares with it
        /// alike under the service's order, the SDK's merge and Calcite's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The lemma, and the bound, stated once.</b> A string with no UTF-16 unit at or above U+D800 orders the same
        /// against any other by code point and by code unit: at the first unit the two differ in, the prefixes agree, and
        /// a unit below U+D800 is below every surrogate and every unit from U+E000 under both. A number strictly within
        /// ±2^53 is read back exactly, and a value past it — an int64 the service orders exactly, or a double printed as
        /// another integer — reads back on the same side of it; strictly, because a service comparing doubles ties 2^53
        /// with 2^53 + 1, which a <c>BIGINT</c> orders. A value the key reads as null — absent, a JSON null, another type
        /// — the static question has already placed alike (R3), so it certifies too: exactly before the last key, where R3
        /// makes the null class one tie class at the service, and as a refinement on the last, where a tie of Calcite's
        /// is free.
        /// </para>
        /// <para>
        /// <b>What makes it enough.</b> An anchor's place in the stream is Calcite's whatever surrounds it: within a
        /// partition every value before it is at most it in the partition's order, so at most it in Calcite's; and the
        /// SDK merges partitions by picking the least head in Calcite's order, so a value of another partition that
        /// came out first was at most the head of the anchor's own, which is at most the anchor. So every row before an
        /// anchor is at most it in Calcite's order and every row after at least, and the rows between two anchors need only
        /// be re-sorted (<c>Client.CosmosOrderedCursor</c>). Measured on the probe account through a two-partition
        /// merge, 40 streams and 880 windows, none accepted wrongly.
        /// </para>
        /// </remarks>
        /// <param name="anchor">What the value has to be.</param>
        /// <param name="value">The value as the service returned it; an undefined element for one it left out.</param>
        /// <returns><c>true</c> where it certifies.</returns>
        public static bool IsAnchor(CosmosOrderAnchor anchor, System.Text.Json.JsonElement value) => anchor switch
        {
            CosmosOrderAnchor.Text => value.ValueKind != System.Text.Json.JsonValueKind.String || CosmosCharacters.BandsOf(value.GetString()!) == CosmosCharacterBand.None,
            CosmosOrderAnchor.Number => value.ValueKind != System.Text.Json.JsonValueKind.Number || value.TryGetDouble(out var number) && Math.Abs(number) < (double)TwoTo53,
            _ => true,
        };

        /// <summary>
        /// Says how Calcite compares an operand's values, for a reader that compares them itself: what its column reads.
        /// </summary>
        /// <remarks>
        /// Text, a stored form and a conversion through one compare as strings; an integer type and <c>DECIMAL</c>
        /// exactly; <c>DOUBLE</c> and <c>REAL</c> as what they read, which ties two large integers a double cannot tell
        /// apart, as Calcite does; a boolean as one. A key whose order is proved compares alike either way, and one the
        /// rows complete has to compare as Calcite does.
        /// </remarks>
        /// <param name="operand">The operand.</param>
        /// <returns>How its values compare.</returns>
        internal static CosmosOrderedKeyKind ComparedAs(CosmosOrderOperand operand)
        {
            if (operand.Kind is CosmosOrderOperandKind.Distance or CosmosOrderOperandKind.Score)
                return CosmosOrderedKeyKind.Double;

            if (operand.Kind != CosmosOrderOperandKind.Path || operand.Reading != CosmosReading.Typed || operand.Type is not SqlTypeName type)
                return CosmosOrderedKeyKind.Text;

            if (type == SqlTypeName.BOOLEAN)
                return CosmosOrderedKeyKind.Boolean;

            if (type == SqlTypeName.DOUBLE || type == SqlTypeName.FLOAT)
                return CosmosOrderedKeyKind.Double;

            if (type == SqlTypeName.REAL)
                return CosmosOrderedKeyKind.Real;

            return IntegerWidth(type) > 0 || type == SqlTypeName.DECIMAL ? CosmosOrderedKeyKind.Exact : CosmosOrderedKeyKind.Text;
        }

        /// <summary>
        /// Returns the bound the service can compare a path against in place of a parameter that is no anchor, keeping
        /// every string Calcite keeps: for <c>x &gt; p</c> and <c>x &gt;= p</c>, <c>x &gt;= P</c>, where <c>P</c> is
        /// <paramref name="value"/> up to its first unit at or above U+D800.
        /// </summary>
        /// <remarks>
        /// A string above <c>p</c> in UTF-16 order either leaves <c>P</c> below the line, where the two orders agree, or
        /// begins with it; either way it is at least <c>P</c> by code point. Checked over 342,225 pairs (the plan's
        /// <c>anchors.py</c>). An empty <c>P</c> keeps every string, which is the type test it then is.
        /// </remarks>
        /// <param name="value">The parameter's value.</param>
        /// <returns>The bound.</returns>
        public static string LowerBound(string value)
        {
            var below = 0;
            while (below < value.Length && CosmosCharacters.BandOf(value[below]) == CosmosCharacterBand.None)
                below++;

            return value.Substring(0, below);
        }

        /// <summary>
        /// Returns the bound the service can compare a path against in place of a parameter that is no anchor, keeping
        /// every string Calcite keeps: for <c>x &lt; p</c> and <c>x &lt;= p</c>, <c>x &lt; P′</c>, where <c>P′</c> is the
        /// lower bound with its last character raised by one, U+D7FF going to U+E000; or <c>null</c> where the lower
        /// bound is empty, and no string bounds every string from above.
        /// </summary>
        /// <remarks>
        /// A string at most <c>p</c> in UTF-16 order either parts from <c>P</c> below its last character, where the
        /// orders agree, or begins with <c>P</c> less its last character and then holds at most that character: below
        /// <c>P′</c> by code point either way. U+D7FF goes past the surrogates, since a supplementary character follows
        /// U+FFFF by code point. Checked over 170,820 pairs (the plan's <c>keyset_mirror.py</c>).
        /// </remarks>
        /// <param name="value">The parameter's value.</param>
        /// <returns>The bound, or <c>null</c>.</returns>
        public static string? UpperBound(string value)
        {
            var lower = LowerBound(value);
            if (lower.Length == 0)
                return null;

            var last = lower[^1];
            return lower.Substring(0, lower.Length - 1) + (last == '\uD7FF' ? '\uE000' : (char)(last + 1));
        }

        /// <summary>
        /// Determines whether a cast orders every value it can be given as its operand does, strictly, with no fact about
        /// the values: what lets a sort move beneath the projection that casts its key.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The question Calcite's transpose asks too loosely.</b> It moves a sort keyed by a cast beneath the
        /// projection, to the cast's operand, wherever the cast's monotonicity is not <c>NOT_MONOTONIC</c> — and it
        /// calls a cast of text to an instant or a UUID monotonic, which orders the operand's text where the plan asked
        /// for the converted value. A stored form can make the two one, and that is a fact about a path the sort itself
        /// asks with its facts in hand (<see cref="CosmosOrderOperandKind.Conversion"/>); with no facts, only a cast that
        /// is a strict widening is order-preserving for every value, and strict, because before the last key a tie the
        /// cast made would hand the next key rows the operand orders apart.
        /// </para>
        /// </remarks>
        /// <param name="from">The operand's type.</param>
        /// <param name="to">The cast's type.</param>
        /// <returns><c>true</c> for a cast that changes nullability, widens a number exactly, or widens text.</returns>
        public static bool CastPreservesOrder(RelDataType from, RelDataType to)
        {
            if (from is null || to is null)
                return false;

            var source = from.getSqlTypeName();
            var target = to.getSqlTypeName();

            if (source is null || target is null)
                return false;

            // The same type, no narrower: a nullability assertion, or a text widened.
            if (source == target)
                return from.getPrecision() == to.getPrecision()
                    || to.getPrecision() == RelDataType.PRECISION_NOT_SPECIFIED
                    || from.getPrecision() != RelDataType.PRECISION_NOT_SPECIFIED && to.getPrecision() >= from.getPrecision() && from.getScale() == to.getScale();

            var width = IntegerWidth(source);

            // An integer into a wider integer, or into a double where it is no wider than 2^53 can hold.
            if (width > 0 && IntegerWidth(target) >= width)
                return true;

            if (width is > 0 and <= 32 && (target == SqlTypeName.DOUBLE || target == SqlTypeName.FLOAT))
                return true;

            return source == SqlTypeName.REAL && (target == SqlTypeName.DOUBLE || target == SqlTypeName.FLOAT);
        }

        // ── Reading an operand ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads what an operand's values are under its reading, from the facts.
        /// </summary>
        /// <param name="facts">The facts its consumer was decided under.</param>
        /// <param name="operand">The operand.</param>
        /// <param name="printed">Whether Calcite reads the value as a response prints it — a sort's key and an extreme —
        /// rather than as the document holds it, which is what a comparison reads.</param>
        static Reading Read(JsonFactSet facts, CosmosOrderOperand operand, bool printed)
        {
            switch (operand.Kind)
            {
                case CosmosOrderOperandKind.Path:
                    return ReadPath(facts, operand.Path!.Value, operand.Type!, operand.Reading, printed);

                case CosmosOrderOperandKind.Conversion:
                    return ReadConversion(facts, operand.Path!.Value, operand.Format, operand.Held);

                case CosmosOrderOperandKind.Distance:
                {
                    // D4: a geodesic distance means the service's WGS84 value, which the sphere in process approximates;
                    // so the service's order of it is the order. It is null where an operand is, and undefined where an
                    // operand is a shape the service will not measure: the one claim that rules out both is the
                    // geography's own (CosmosGeography).
                    var always = operand.AccountedFor;
                    foreach (var path in operand.Operands)
                        always &= facts.IsAlwaysGeography(path);

                    return new(CosmosOrderAgreement.Exact, Classes.Number, always ? Classes.None : Classes.Undefined | Classes.Null, CosmosCharacterBand.None, false, null, "a geodesic distance");
                }

                case CosmosOrderOperandKind.Score:
                    return new(CosmosOrderAgreement.Exact, Classes.Number, Classes.None, CosmosCharacterBand.None, false, null, "a score");

                case CosmosOrderOperandKind.Literal:
                {
                    var value = operand.Value;
                    var characters = value.Type == JsonType.String ? CosmosCharacters.BandsOf((string)value.ToClrValue()!) : CosmosCharacterBand.None;
                    return new(CosmosOrderAgreement.Exact, ClassOf(value), Classes.None, characters, true, null, "a literal");
                }

                case CosmosOrderOperandKind.Parameter:
                    return new(CosmosOrderAgreement.Exact, ClassesOf(operand.Type!), Classes.None, AnyCharacter, false, null, "a parameter, which may hold any character");

                default:
                    return new(CosmosOrderAgreement.Exact, ClassesOf(operand.Type!), Classes.None, AnyCharacter, false, null, "a computed value, whose characters nothing states");
            }
        }

        /// <summary>
        /// Reads a document path under a column's type and reading.
        /// </summary>
        static Reading ReadPath(JsonFactSet facts, JsonDocumentPath path, SqlTypeName type, CosmosReading reading, bool printed)
        {
            var admitted = Admitted(facts, path, out var constant);
            var characters = Characters(facts, path);

            // What the column reads as a value, as null, and how the two orders over the values stand.
            Reading As(Classes values, CosmosOrderAgreement agreement, string why) =>
                new(agreement, admitted & values, admitted & ~values, characters, constant, path, why);

            if (reading is CosmosReading.Json or CosmosReading.JsonText)
                return As(Classes.None, CosmosOrderAgreement.None, "a JSON fragment, which the service orders by no structure Calcite shares");

            if (reading == CosmosReading.Text || IsCharacter(type))
            {
                // A rendering of a scalar: a string reads as itself, a number or a boolean as its text, and anything
                // else as null (Calcite.JsonValueRendersScalarsAsText).
                var text = As(Classes.String | Classes.Number | Classes.Boolean, CosmosOrderAgreement.Exact, "text");

                return (text.Values & (Classes.Number | Classes.Boolean)) == Classes.None
                    ? text
                    : text with { Agreement = CosmosOrderAgreement.None, Why = "a rendering of what may not be a string, which orders as text in Calcite and by type and value at the service" };
            }

            if (type == SqlTypeName.BOOLEAN)
                return As(Classes.Boolean, CosmosOrderAgreement.Exact, "a boolean");

            if (type == SqlTypeName.TINYINT || type == SqlTypeName.SMALLINT || type == SqlTypeName.INTEGER)
                return As(Classes.Number, CosmosOrderAgreement.Exact, "an integer, read only within its range");

            // Past the bound a value read back may not be the one the service ordered; within 2^53 every value is, and
            // compares with every other alike, which the rows can show (CosmosOrderAnchor.Number).
            if (type == SqlTypeName.BIGINT || type == SqlTypeName.DECIMAL)
                return printed == false || (admitted & Classes.Number) == Classes.None || Within(facts, path, Int64Least, Int64Most, open: false)
                    ? As(Classes.Number, CosmosOrderAgreement.Exact, "a number within int64")
                    : As(Classes.Number, CosmosOrderAgreement.None, "a number past 2^63, which a response prints below 2^63 - 1") with { Anchorable = CosmosOrderAnchor.Number };

            if (type == SqlTypeName.DOUBLE || type == SqlTypeName.FLOAT)
                return printed == false || (admitted & Classes.Number) == Classes.None || Within(facts, path, -TwoTo53, TwoTo53, open: true)
                    ? As(Classes.Number, CosmosOrderAgreement.Exact, "a number within 2^53")
                    : As(Classes.Number, CosmosOrderAgreement.Refines, "an integer past 2^53, which a double ties with its neighbour") with { Anchorable = CosmosOrderAnchor.Number };

            if (type == SqlTypeName.REAL)
                return As(Classes.Number, printed == false || (admitted & Classes.Number) == Classes.None ? CosmosOrderAgreement.Exact : CosmosOrderAgreement.Refines, "a number read as a float");

            if (IsTemporal(type))
            {
                // Cosmos has no temporal type: the path holds a string and the service orders the spelling, which is the
                // instant's order only in a stored form that keeps it, and one the engine can read at all (every ISO-8601
                // instant raises in a cast, measured: Calcite.TemporalCastReadsFewShapes).
                var stored = As(Classes.String | Classes.Number | Classes.Boolean, CosmosOrderAgreement.Exact, "an instant in a stored form that keeps its order");

                if ((stored.Values & (Classes.Number | Classes.Boolean)) != Classes.None)
                    return stored with { Agreement = CosmosOrderAgreement.None, Why = "an instant over what may not be a string" };

                return facts.RepresentationOf(path) is CosmosRepresentation representation
                    && representation.PreservesOrder
                    && CosmosStoredForms.EngineReads(representation, CosmosTemporalParse.PartsOf(type))
                        ? stored
                        : stored with { Agreement = CosmosOrderAgreement.None, Why = "an instant with no stored form that keeps its order" };
            }

            // A raw document value — the promoted column, or the accessor the split rule names as the value it reads — is
            // compared as it stands, and a comparison across types is undefined at the service: what is left is the order
            // of the strings, which R1 asks. As a sort key it is no order at all.
            if (printed == false && (type == SqlTypeName.ANY || type == SqlTypeName.VARIANT))
                return As(Classes.All, CosmosOrderAgreement.Exact, "the stored value");

            return As(Classes.None, CosmosOrderAgreement.None, type == SqlTypeName.VARIANT
                ? "a VARIANT, which Calcite does not order"
                : "a " + type.getName() + ", which the service orders by no rule Calcite shares");
        }

        /// <summary>
        /// Reads a conversion of a path's text: by the stored spelling, where the path's form keeps the order and the
        /// conversion reads that form.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A parse names how the text is read and is licensed by the format; a cast leaves it to the engine</b> and is
        /// licensed only where the engine reads the form at all — measured, every ISO-8601 instant raises in a temporal
        /// cast, so ordering by the stored strings would order rows for a query whose key raises. A conversion that names
        /// neither is a UUID cast, which the engine performs (<c>Calcite.UuidOrderIsCanonicalOrdinal</c>).
        /// </para>
        /// <para>
        /// <b>And its null class is the guard's.</b> The column is rendered through <c>IS_PRIMITIVE(c.p) ? c.p : null</c>,
        /// so an absent path, a JSON null, an object and an array read null in the column, and the service sorts the
        /// first two below every string and the others above: R3 decides, as of any key. A number or a boolean at the
        /// path is no spelling of the value, and is refused as a rendering is.
        /// </para>
        /// </remarks>
        static Reading ReadConversion(JsonFactSet facts, JsonDocumentPath path, string? format, CosmosTemporalParts held)
        {
            var admitted = Admitted(facts, path, out var constant);
            var values = admitted & (Classes.String | Classes.Number | Classes.Boolean);
            var refused = new Reading(CosmosOrderAgreement.None, values, admitted & ~values, Characters(facts, path), constant, path, "a conversion with no stored form that keeps its order");

            if ((values & (Classes.Number | Classes.Boolean)) != Classes.None)
                return refused with { Why = "a conversion over what may not be a string" };

            if (facts.RepresentationOf(path) is not CosmosRepresentation representation || representation.PreservesOrder == false)
                return refused;

            if (format is not null)
            {
                if (CosmosStoredForms.ParsesExactly(representation, format, held) == false)
                    return refused with { Why = "a parse whose format does not read the stored form" };
            }
            else if (held != CosmosTemporalParts.None && CosmosStoredForms.EngineReads(representation, held) == false)
            {
                return refused with { Why = "a cast the engine cannot read the stored form by" };
            }

            return refused with { Agreement = CosmosOrderAgreement.Exact, Why = "a conversion of a stored form" };
        }

        /// <summary>
        /// Reads which types a path may hold in a row, the absent path among them, and whether every row holds one value
        /// there.
        /// </summary>
        static Classes Admitted(JsonFactSet facts, JsonDocumentPath path, out bool constant)
        {
            var present = facts.Knows(new JsonFact(path, new JsonClaim.Present()));

            var admitted = facts.TryGetTypes(path, out var types) ? ClassesOf(types) : Classes.All & ~Classes.Undefined;

            constant = false;

            if (facts.TryGetDomain(path, out var domain))
            {
                var held = Classes.None;
                foreach (var value in domain)
                    held |= ClassOf(value);

                admitted &= held;
                constant = present && domain.Count == 1;
            }

            if (present == false)
                admitted |= Classes.Undefined;

            // Never there, or always a JSON null: one value in every row, read alike.
            constant |= admitted == Classes.Undefined || present && admitted == Classes.Null;

            return admitted;
        }

        /// <summary>
        /// Reads what characters a path's strings may hold: the bands the facts do not say it lacks.
        /// </summary>
        /// <remarks>
        /// Asked of the facts as a claim, one per band (<see cref="CosmosCharacters"/>), which a declared
        /// <c>pattern</c>, a service fact, a domain and a stored form each establish — the last two through the claim's
        /// own reading of them (<see cref="JsonFactSet.Knows"/>), so a filter's <c>IN</c> and an ASCII form say their
        /// characters here as a pattern does.
        /// </remarks>
        static CosmosCharacterBand Characters(JsonFactSet facts, JsonDocumentPath path)
        {
            var characters = AnyCharacter;

            if (facts.Knows(new JsonFact(path, new CosmosCharacters(CosmosCharacterBand.Surrogates))))
                characters &= ~CosmosCharacterBand.Surrogates;

            if (facts.Knows(new JsonFact(path, new CosmosCharacters(CosmosCharacterBand.HighBmp))))
                characters &= ~CosmosCharacterBand.HighBmp;

            return characters;
        }

        /// <summary>
        /// Determines whether the facts bound every number a path holds within a range.
        /// </summary>
        static bool Within(JsonFactSet facts, JsonDocumentPath path, decimal least, decimal most, bool open) =>
            facts.Knows(new JsonFact(path, new JsonClaim.LowerBound(least, open)))
            && facts.Knows(new JsonFact(path, new JsonClaim.UpperBound(most, open)));

        // ── The pieces ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a key's direction and null placement as Calcite runs them, or says why it cannot be run at the service.
        /// </summary>
        /// <remarks>
        /// Calcite's runtime runs only <c>DESCENDING</c> descending: a strictly descending key runs ascending, so writing
        /// <c>DESC</c> for it would be the other order. An unspecified placement runs last in both directions. A clustered
        /// key has no order at all.
        /// </remarks>
        static string? Direction(RelFieldCollation collation, out bool descending, out bool nullsFirst)
        {
            descending = false;
            nullsFirst = false;

            switch ((RelFieldCollation.Direction.__Enum)collation.getDirection().ordinal())
            {
                case RelFieldCollation.Direction.__Enum.ASCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_ASCENDING:
                    break;
                case RelFieldCollation.Direction.__Enum.DESCENDING:
                    descending = true;
                    break;
                case RelFieldCollation.Direction.__Enum.STRICTLY_DESCENDING:
                    return "a strictly descending key, which Calcite runs ascending";
                default:
                    return "a clustered key, which orders nothing";
            }

            nullsFirst = (RelFieldCollation.NullDirection.__Enum)collation.nullDirection.ordinal() == RelFieldCollation.NullDirection.__Enum.FIRST;
            return null;
        }

        /// <summary>
        /// Determines whether the strings a reading may hold order among themselves alike by code point and by UTF-16
        /// code unit.
        /// </summary>
        static bool OrdersItsStrings(Reading reading) =>
            (reading.Values & Classes.String) == Classes.None || Agree(reading.Characters, reading.Characters);

        /// <summary>
        /// Determines whether two sets of strings order alike by code point and by UTF-16 code unit: unless one may hold a
        /// surrogate and the other a unit from U+E000.
        /// </summary>
        static bool Agree(CosmosCharacterBand x, CosmosCharacterBand y) =>
            ((x & CosmosCharacterBand.Surrogates) == CosmosCharacterBand.None || (y & CosmosCharacterBand.HighBmp) == CosmosCharacterBand.None)
            && ((x & CosmosCharacterBand.HighBmp) == CosmosCharacterBand.None || (y & CosmosCharacterBand.Surrogates) == CosmosCharacterBand.None);

        /// <summary>
        /// Determines whether every type of a null class lies on one side of the values in the service's type order.
        /// </summary>
        static bool OnTheSide(Classes nulls, Classes values, bool low)
        {
            if (nulls == Classes.None || values == Classes.None)
                return true;

            return low ? Highest(nulls) < (int)values : Lowest(nulls) > (int)values;
        }

        static int Highest(Classes classes)
        {
            var highest = 0;
            for (var c = 1; c <= (int)Classes.Object; c <<= 1)
                if (((int)classes & c) != 0)
                    highest = c;

            return highest;
        }

        static int Lowest(Classes classes)
        {
            for (var c = 1; c <= (int)Classes.Object; c <<= 1)
                if (((int)classes & c) != 0)
                    return c;

            return 0;
        }

        static bool IsOneClass(Classes classes) => (classes & (classes - 1)) == Classes.None;

        static string Grade(Reading reading) => reading.Agreement == CosmosOrderAgreement.Refines ? "finer than Calcite" : "by no shared rule";

        static Classes ClassesOf(Eggnog.ValueSet<string> types)
        {
            var classes = Classes.None;

            foreach (var type in types)
                classes |= type switch
                {
                    StringType => Classes.String,
                    IntegerType or FractionType => Classes.Number,
                    BooleanType => Classes.Boolean,
                    NullType => Classes.Null,
                    ArrayType => Classes.Array,
                    ObjectType => Classes.Object,
                    _ => Classes.None,
                };

            return classes;
        }

        static Classes ClassOf(JsonScalar value) => value.Type switch
        {
            JsonType.String => Classes.String,
            JsonType.Boolean => Classes.Boolean,
            JsonType.Integer or JsonType.Number => Classes.Number,
            _ => Classes.Null,
        };

        static Classes ClassesOf(SqlTypeName type)
        {
            if (IsCharacter(type))
                return Classes.String;

            if (type == SqlTypeName.BOOLEAN)
                return Classes.Boolean;

            if (SqlTypeName.NUMERIC_TYPES.contains(type))
                return Classes.Number;

            return Classes.All & ~Classes.Undefined;
        }

        static bool IsCharacter(SqlTypeName type) => type == SqlTypeName.VARCHAR || type == SqlTypeName.CHAR;

        static bool IsTemporal(SqlTypeName type) =>
            type == SqlTypeName.DATE
            || type == SqlTypeName.TIME
            || type == SqlTypeName.TIME_WITH_LOCAL_TIME_ZONE
            || type == SqlTypeName.TIMESTAMP
            || type == SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE;

        /// <summary>
        /// The width of an exact integer type in bits, or zero for any other.
        /// </summary>
        static int IntegerWidth(SqlTypeName type) =>
            type == SqlTypeName.TINYINT ? 8
            : type == SqlTypeName.SMALLINT ? 16
            : type == SqlTypeName.INTEGER ? 32
            : type == SqlTypeName.BIGINT ? 64
            : 0;

        static string Describe(Classes classes)
        {
            var names = new List<string>();
            foreach (Classes c in Enum.GetValues(typeof(Classes)))
                if (c is not (Classes.None or Classes.All) && (classes & c) != Classes.None)
                    names.Add(c switch { Classes.Undefined => "an absent path", Classes.Null => "a JSON null", _ => "a" + (c is Classes.Array or Classes.Object ? "n " : " ") + c.ToString().ToLowerInvariant() });

            return string.Join(" and ", names);
        }

        static string Describe(CosmosCharacterBand bands) => bands switch
        {
            CosmosCharacterBand.None => "below U+D800",
            CosmosCharacterBand.Surrogates => "nothing from U+E000",
            CosmosCharacterBand.HighBmp => "no surrogate",
            _ => "any character",
        };

    }

}
