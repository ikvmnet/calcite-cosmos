using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure.ServiceOrderModel;

using Value = Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure.ServiceOrderModel.Value;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Every verdict of the question "may the service compute this order", checked against a model of
    /// the service, of the SDK's merge and of Calcite, over every value the verdict's facts admit: no
    /// emulator, and every database at once rather than the one a container holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a verdict claims, and what the model asks of it.</b> An order the service computes is
    /// allowable for every database the facts admit iff, for every two admitted rows that Calcite orders
    /// strictly, both of the service's comparators order them the same way strictly: the partition's,
    /// since two rows in one partition come back in its order, and the merge's, since two rows in two
    /// partitions come back in its. A tie at the service is not enough, since it may break either way,
    /// and where Calcite ties anything is. That is the whole condition, pairwise, because a merge of
    /// streams each in an order refining C, by a comparator refining C, is itself C-non-decreasing.
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Proved</b> must satisfy it over the keys it writes.</description></item>
    /// <item><description><b>Anchored</b>'s check must pick values that every admitted value compares
    /// with alike under all three orders: an anchor is what makes the rows around it
    /// certifiable.</description></item>
    /// <item><description>An <b>extreme</b> must be in Calcite's extreme tie class for every small
    /// database and every way it is spread over two partitions.</description></item>
    /// <item><description>A <b>comparison</b> must keep exactly the rows Calcite keeps, for every
    /// admitted value and every value a parameter may be bound to.</description></item>
    /// </list>
    /// <para>
    /// <b>The role population D plays for the predicate oracle.</b> The question is asked through
    /// <see cref="IQuestion"/>, and <see cref="Question"/> is <see cref="MainsDecisions"/> until the
    /// adapter asks it in one place: a mirror of what the adapter decides today, so that the sweep says
    /// what is wrong now in the model's terms. The sweep is explicit while that is so, and its failures
    /// are the families the <c>ORDER BY</c> yardstick finds on the emulator; a checker that passes
    /// everything says nothing, so <see cref="TheCheckCatchesWhatItMustCatch"/> runs by default and
    /// plants each of them.
    /// </para>
    /// <para>
    /// <b>The model is pinned to what was measured, not to what was reasoned.</b>
    /// <see cref="TheMergeIsTheOneMeasuredOnAzure"/> reproduces the two-partition streams the probe
    /// account returned, which no single order produces.
    /// </para>
    /// </remarks>
    public class CosmosServiceOrderModelTests
    {

        // ── What the question is asked of ──────────────────────────────────────────────────────

        /// <summary>
        /// How a key reads the value its path stores.
        /// </summary>
        internal enum Reading
        {
            /// <summary>The service's <c>id</c>: a string every document has.</summary>
            Id,

            /// <summary>The service's <c>_etag</c>: an RFC 9110 entity tag, ASCII.</summary>
            ETag,

            /// <summary>The service's <c>_ts</c>, read as <c>BIGINT</c>.</summary>
            Timestamp,

            /// <summary><c>JSON_VALUE</c> unqualified: a string as itself, a number or a boolean as its
            /// text, anything else null.</summary>
            Text,

            /// <summary><c>RETURNING INTEGER</c>.</summary>
            Integer,

            /// <summary><c>RETURNING BIGINT</c>.</summary>
            Bigint,

            /// <summary><c>RETURNING DOUBLE</c>.</summary>
            Double,

            /// <summary><c>RETURNING BOOLEAN</c>.</summary>
            Boolean,
        }

        /// <summary>
        /// The JSON types facts may admit at a path, a number split as JSON Schema's <c>integer</c>
        /// and the rest.
        /// </summary>
        [Flags]
        internal enum Types
        {
            Absent = 1,
            Null = 2,
            Boolean = 4,
            Integer = 8,
            Fraction = 16,
            String = 32,
            Array = 64,
            Object = 128,
            All = 255,
        }

        /// <summary>
        /// The characters facts may admit in a path's strings, by the bands where the service's order
        /// and Calcite's part.
        /// </summary>
        /// <remarks>
        /// Two strings sets order alike under code point and under UTF-16 unless one holds a unit of a
        /// surrogate pair and the other a unit from U+E000 up: below U+D800 the two orders are one.
        /// </remarks>
        [Flags]
        internal enum Bands
        {
            /// <summary>U+0000 to U+D7FF.</summary>
            Low = 1,

            /// <summary>U+E000 to U+FFFF.</summary>
            High = 2,

            /// <summary>Beyond U+FFFF, written as a surrogate pair.</summary>
            Supplementary = 4,

            All = 7,
        }

        /// <summary>
        /// What a declaration or a filter says of one path.
        /// </summary>
        /// <param name="Admitted">The JSON types it may hold.</param>
        /// <param name="Characters">The characters its strings may hold, as a <c>pattern</c> would
        /// declare them.</param>
        /// <param name="Bounded">Whether its numbers lie strictly within ±2^53, as a <c>minimum</c> and
        /// <c>maximum</c> would declare.</param>
        /// <param name="Domain">The values it may hold where they are finitely many, as an
        /// <c>enum</c> or a filter's <c>IN</c> would say; <c>null</c> where nothing says.</param>
        internal sealed record Facts(Types Admitted, Bands Characters = Bands.All, bool Bounded = false, IReadOnlyList<Value>? Domain = null)
        {

            /// <summary>Whether the facts admit a stored value.</summary>
            public bool Admits(Value value)
            {
                if (Domain is not null && Domain.Contains(value) == false)
                    return false;

                return value.Kind switch
                {
                    Kind.Absent => Admitted.HasFlag(Types.Absent),
                    Kind.Null => Admitted.HasFlag(Types.Null),
                    Kind.Boolean => Admitted.HasFlag(Types.Boolean),
                    Kind.Number => (value.Number == decimal.Truncate(value.Number) ? Admitted.HasFlag(Types.Integer) : Admitted.HasFlag(Types.Fraction))
                        && (Bounded == false || Math.Abs(value.Number) < 9007199254740992m),
                    Kind.String => Admitted.HasFlag(Types.String) && (BandsOf(value.Text) & ~Characters) == 0,
                    Kind.Array => Admitted.HasFlag(Types.Array),
                    _ => Admitted.HasFlag(Types.Object),
                };
            }

            public override string ToString() =>
                $"{Admitted}{(Admitted.HasFlag(Types.String) ? $" chars {Characters}" : "")}{(Bounded ? " within 2^53" : "")}{(Domain is null ? "" : " in [" + string.Join(", ", Domain) + "]")}";

        }

        /// <summary>
        /// A key's operand: how it reads, and what is known of the path it reads.
        /// </summary>
        internal sealed record Operand(Reading Reading, Facts Facts)
        {
            public override string ToString() => $"{Reading} over {Facts}";
        }

        /// <summary>
        /// One key of a sort, its null placement resolved.
        /// </summary>
        internal sealed record Key(Operand Operand, bool Descending, bool NullsFirst)
        {
            public override string ToString() => $"{Operand} {(Descending ? "DESC" : "ASC")} NULLS {(NullsFirst ? "FIRST" : "LAST")}";
        }

        /// <summary>
        /// What a comparison's right-hand side is: a literal, a parameter bound to any value, or a path.
        /// </summary>
        internal sealed record Comparand(Value? Literal = null, Operand? Path = null)
        {
            public bool IsParameter => Literal is null && Path is null;

            public override string ToString() => Literal is not null ? "literal " + Literal : Path is not null ? "path " + Path : "a parameter";
        }

        internal enum VerdictKind
        {
            Proved,
            Anchored,
            Split,
            Refused,
        }

        /// <summary>
        /// The question's answer.
        /// </summary>
        /// <param name="Kind">Whether the order is proved, certified when the rows arrive, split, or
        /// refused.</param>
        /// <param name="Reason">Why, in words a report can group by.</param>
        /// <param name="Written">The keys the service is handed, by position; <c>null</c> for all of
        /// them. A key the facts pin is dropped.</param>
        /// <param name="IsAnchor">For <see cref="VerdictKind.Anchored"/>, the run-time check: whether a
        /// stored value of the key at a position certifies the rows around it, or whether a bound
        /// parameter makes a comparison exact.</param>
        internal sealed record Verdict(VerdictKind Kind, string Reason, IReadOnlyList<int>? Written = null, Func<int, Value, bool>? IsAnchor = null)
        {
            public static Verdict Proved(string reason = "proved") => new(VerdictKind.Proved, reason);

            public static Verdict Refused(string reason) => new(VerdictKind.Refused, reason);
        }

        internal enum Comparison
        {
            Less,
            GreaterOrEqual,
        }

        /// <summary>
        /// The question the adapter asks before the service computes an order: the sort's, the
        /// extreme's, and the ordering comparison's.
        /// </summary>
        internal interface IQuestion
        {
            Verdict OrderBy(IReadOnlyList<Key> keys);

            Verdict Extreme(Operand operand, bool max);

            Verdict Compare(Operand left, Comparand right, Comparison kind);
        }

        /// <summary>
        /// The question the sweep asks. Main's decisions until the one place exists; then the one place.
        /// </summary>
        static IQuestion Question => new MainsDecisions();

        /// <summary>
        /// What the adapter decides today, check by check, as <c>CosmosSortRule</c> and
        /// <c>CosmosSort.TryResolveSortKeys</c> ask it: the stored value is the column, and the null
        /// placement agrees or nothing is null. Nothing asks how the values order.
        /// </summary>
        /// <remarks>
        /// The composite index is taken as declared: it decides what the service accepts, not how what
        /// it accepts is ordered.
        /// </remarks>
        sealed class MainsDecisions : IQuestion
        {

            public Verdict OrderBy(IReadOnlyList<Key> keys)
            {
                foreach (var key in keys)
                {
                    var facts = key.Operand.Facts;

                    // IsReadAsStored: a typed column is; a text reading where the facts say string or
                    // null; a RETURNING where they say its tested type or null.
                    var asStored = key.Operand.Reading switch
                    {
                        Reading.Id or Reading.ETag or Reading.Timestamp => true,
                        Reading.Text => (facts.Admitted & ~(Types.String | Types.Null | Types.Absent)) == 0,
                        Reading.Boolean => (facts.Admitted & ~(Types.Boolean | Types.Null | Types.Absent)) == 0,
                        _ => (facts.Admitted & ~(Types.Integer | Types.Fraction | Types.Null | Types.Absent)) == 0,
                    };

                    if (asStored == false)
                        return Verdict.Refused("the column is not the stored value");

                    // TryGetDescending: a key that cannot be null, IsAlwaysScalar, or the service's
                    // placement asked for.
                    var nonNull = key.Operand.Reading is Reading.Id or Reading.ETag or Reading.Timestamp
                        || (facts.Admitted & (Types.Null | Types.Absent)) == 0 && facts.Admitted is Types.String or Types.Boolean or Types.Integer or Types.Fraction or (Types.Integer | Types.Fraction);

                    if (nonNull == false && key.NullsFirst == key.Descending)
                        return Verdict.Refused("null placement");
                }

                return Verdict.Proved("the stored value is the column");
            }

            /// <summary><c>CosmosAggregate.CanImplement</c>: an argument non-nullable in the row type.</summary>
            public Verdict Extreme(Operand operand, bool max) => operand.Reading is Reading.Id or Reading.ETag or Reading.Timestamp
                ? Verdict.Proved("non-nullable in the row type")
                : Verdict.Refused("nullable in the row type");

            /// <summary>
            /// <c>WriteComparand</c>: a typed column compares raw; a rendering only where every rendering
            /// side is a declared string; a typed reading under its own guard.
            /// </summary>
            public Verdict Compare(Operand left, Comparand right, Comparison kind)
            {
                bool DeclaredString(Operand o) => o.Reading is Reading.Id or Reading.ETag
                    || o.Reading == Reading.Text && (o.Facts.Admitted & ~(Types.String | Types.Null | Types.Absent)) == 0;

                return left.Reading switch
                {
                    Reading.Id or Reading.ETag => Verdict.Proved("a typed column compares raw"),
                    Reading.Text when DeclaredString(left) && (right.Path is null || DeclaredString(right.Path)) => Verdict.Proved("renders only strings"),
                    Reading.Text => Verdict.Refused("a rendering of what may not be a string"),
                    _ => Verdict.Proved("guarded by its type test"),
                };
            }

        }

        /// <summary>
        /// A question that proves everything: what the checker must find wrong wherever the model says
        /// so.
        /// </summary>
        sealed class ProvesEverything : IQuestion
        {
            public Verdict OrderBy(IReadOnlyList<Key> keys) => Verdict.Proved();

            public Verdict Extreme(Operand operand, bool max) => Verdict.Proved();

            public Verdict Compare(Operand left, Comparand right, Comparison kind) => Verdict.Proved();
        }

        // ── The values, and how each side reads them ───────────────────────────────────────────

        static readonly string[] LowStrings = ["", "1", "A", "B", "a", "ab", "\u00E9", "\uD7FF"];

        static readonly string[] HighStrings = ["\uE000", "\uFF21", "\uFFFF", "a\uE000"];

        static readonly string[] SupplementaryStrings = ["\U00010000", "\U0001F600", "a\U0001F600", "\U0010FFFF"];

        /// <summary>
        /// Every stored value a sweep asks about: each JSON type, the numbers where an integer, a double
        /// and their printing part, and strings from each band.
        /// </summary>
        static readonly Value[] Universe =
        [
            Value.Absent,
            Value.Null,
            Value.Of(false),
            Value.Of(true),
            Value.Of(0),
            Value.Of(1),
            Value.Of(-1),
            Value.Of(2147483647),
            Value.Of(2147483648),
            Value.Of(9007199254740992),
            Value.Of(9007199254740993),
            Value.Of(9223372036854775807),
            Value.OfDouble(1.0),
            Value.OfDouble(1.5),
            Value.OfDouble(-2.5),
            Value.OfDouble(9223372036854775808d),
            .. LowStrings.Select(s => Value.Of(s)),
            .. HighStrings.Select(s => Value.Of(s)),
            .. SupplementaryStrings.Select(s => Value.Of(s)),
            Value.Array("[]"),
            Value.Array("[1]"),
            Value.Object("{}"),
            Value.Object("""{"a":1}"""),
        ];

        /// <summary>Which bands a string's characters fall in.</summary>
        static Bands BandsOf(string text)
        {
            var bands = (Bands)0;
            foreach (var c in text)
                bands |= char.IsSurrogate(c) ? Bands.Supplementary : c >= '\uE000' ? Bands.High : Bands.Low;

            return bands;
        }

        /// <summary>
        /// What Calcite reads a stored value as under a reading: a value, SQL null, or an error, which
        /// a statement over such a document raises and so refines to anything.
        /// </summary>
        readonly record struct Read(object? Value, bool Raises)
        {
            public static readonly Read SqlNull = new(null, false);

            public static readonly Read Error = new(null, true);
        }

        /// <summary>
        /// Calcite's reading of what the service returns for a stored value, as measured: a typed
        /// <c>RETURNING</c> asserts rather than converts (calcite-dotnet#120), so a number of another
        /// width or kind raises, and <c>RETURNING DOUBLE</c> raises over any integral value, the service
        /// printing <c>1.0</c> as <c>1</c>.
        /// </summary>
        static Read ReadAs(Reading reading, Value value, Printer printer)
        {
            if (value.Kind is Kind.Absent or Kind.Null or Kind.Array or Kind.Object)
                return Read.SqlNull;

            var number = value.ReadBack(printer);
            var integral = value.Kind == Kind.Number && number == decimal.Truncate(number);

            switch (reading)
            {
                case Reading.Id or Reading.ETag or Reading.Text:
                    return value.Kind switch
                    {
                        Kind.String => new(value.Text, false),
                        Kind.Boolean => new(value.Boolean ? "true" : "false", false),
                        _ => new(integral ? number.ToString("0", CultureInfo.InvariantCulture) : ((double)number).ToString("R", CultureInfo.InvariantCulture), false),
                    };

                case Reading.Integer:
                    return integral && number is >= int.MinValue and <= int.MaxValue ? new((long)number, false) : Read.Error;

                case Reading.Bigint or Reading.Timestamp:
                    return integral && number is >= long.MinValue and <= long.MaxValue ? new((long)number, false) : Read.Error;

                case Reading.Double:
                    return value.Kind == Kind.Number && integral == false ? new((double)number, false) : Read.Error;

                default:
                    return value.Kind == Kind.Boolean ? new(value.Boolean, false) : Read.Error;
            }
        }

        /// <summary>The values an operand's facts admit that Calcite reads without raising.</summary>
        static List<Value> Admitted(Operand operand, Printer printer) =>
            Universe.Where(v => operand.Facts.Admits(v) && ReadAs(operand.Reading, v, printer).Raises == false).ToList();

        // ── The checks ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds two admitted rows that Calcite orders strictly and the service may not, or returns
        /// <c>null</c> where a sort's verdict holds.
        /// </summary>
        static string? CheckOrderBy(IReadOnlyList<Key> keys, Verdict verdict)
        {
            foreach (var printer in Enum.GetValues<Printer>())
            {
                var written = verdict.Written ?? Enumerable.Range(0, keys.Count).ToList();
                var columns = keys.Select(k => Admitted(k.Operand, printer)).ToList();

                if (verdict.Kind == VerdictKind.Anchored)
                {
                    if (CheckAnchors(keys, written, columns, verdict.IsAnchor!, printer) is string unanchored)
                        return unanchored;

                    continue;
                }

                var rows = Product(columns);
                var calcite = keys.Select((k, i) => new CalciteSortKey(i, k.Descending, k.NullsFirst)).ToList();

                var reads = rows.Select(row => row.Select((v, i) => ReadAs(keys[i].Operand.Reading, v, printer).Value).ToArray()).ToList();

                for (var r = 0; r < rows.Count; r++)
                    for (var s = 0; s < rows.Count; s++)
                    {
                        if (CalciteOrder.Compare(reads[r], reads[s], calcite) >= 0)
                            continue;

                        var inPartition = Lexicographic(written, keys, i => CompareInPartition(rows[r][i], rows[s][i]));
                        var inMerge = Lexicographic(written, keys, i => CompareInMerge(rows[r][i], rows[s][i], printer));

                        if (inPartition is not < 0 || inMerge is not < 0)
                            return $"[{Family(rows[r].Concat(rows[s]), keys.Count > 1 && rows[r][0].Kind != rows[s][0].Kind && reads[r][0] is null && reads[s][0] is null)}] " +
                                $"Calcite orders ({string.Join(", ", rows[r].AsEnumerable())}) before ({string.Join(", ", rows[s].AsEnumerable())}); " +
                                $"{(inPartition is not < 0 ? "one partition " + Says(inPartition) : "the merge " + Says(inMerge))} ({printer} printing)";
                    }
            }

            return null;
        }

        /// <summary>
        /// Finds an anchor that some admitted value compares with otherwise under one of the three
        /// orders, ties included, or returns <c>null</c>: an anchored verdict's run-time check is only
        /// as good as that.
        /// </summary>
        static string? CheckAnchors(IReadOnlyList<Key> keys, IReadOnlyList<int> written, IReadOnlyList<List<Value>> columns, Func<int, Value, bool> isAnchor, Printer printer)
        {
            foreach (var i in written)
            {
                var key = keys[i];
                var calcite = new CalciteSortKey(0, key.Descending, key.NullsFirst);
                object? Read(Value v) => ReadAs(key.Operand.Reading, v, printer).Value;
                int? Directed(int? c) => key.Descending ? -c : c;

                foreach (var a in columns[i].Where(a => isAnchor(i, a)))
                    foreach (var x in columns[i])
                    {
                        var c = Math.Sign(CalciteOrder.CompareKey(Read(a), Read(x), calcite, keys.Count > 1));
                        var p = Directed(CompareInPartition(a, x));
                        var m = Directed(CompareInMerge(a, x, printer));

                        if (p != c || m != c)
                            return $"[{Family([a, x], false)}] the anchor {a} of key {i + 1} compares with {x} as {c} in Calcite, {p?.ToString() ?? "unknown"} in a partition and {m?.ToString() ?? "unknown"} in the merge ({printer} printing)";
                    }
            }

            return null;
        }

        /// <summary>
        /// Names the family a witness belongs to, by what its values hold: the order of text either
        /// side of U+D800, a double printed as another integer, the two nulls of a leading key, a null
        /// placed where Calcite does not place it, or a value with no measured order.
        /// </summary>
        static string Family(IEnumerable<Value> values, bool leadingNulls)
        {
            var all = values.ToList();
            var bands = all.Where(v => v.Kind == Kind.String).Aggregate((Bands)0, (b, v) => b | BandsOf(v.Text));

            if (bands.HasFlag(Bands.High) && bands.HasFlag(Bands.Supplementary))
                return "text across U+D800";
            if (all.Any(v => v.IsDouble && Math.Abs(v.Number) >= 9007199254740992m))
                return "a double read back as another integer";
            if (leadingNulls)
                return "undefined and null on a leading key";
            if (all.Any(v => v.Kind is Kind.Array or Kind.Object))
                return "an array or an object";
            if (all.Any(v => v.Kind is Kind.Absent or Kind.Null))
                return "null placement";
            if (all.Select(v => v.Kind).Distinct().Count() > 1)
                return "across JSON types";

            return "other";
        }

        static string Says(int? c) => c switch { null => "orders them by no measured rule", 0 => "ties them", _ => "orders them the other way" };

        static int? Lexicographic(IReadOnlyList<int> written, IReadOnlyList<Key> keys, Func<int, int?> compare)
        {
            foreach (var i in written)
            {
                var c = compare(i);
                if (c is null)
                    return null;
                if (c != 0)
                    return keys[i].Descending ? -c : c;
            }

            return 0;
        }

        static List<Value[]> Product(IReadOnlyList<List<Value>> columns)
        {
            var rows = new List<Value[]> { Array.Empty<Value>() };
            foreach (var column in columns)
                rows = rows.SelectMany(row => column.Select(v => row.Append(v).ToArray())).ToList();

            return rows;
        }

        /// <summary>
        /// Finds a database of up to three admitted values, spread over two partitions, whose extreme at
        /// the service Calcite does not allow, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The service's <c>MIN</c>/<c>MAX</c> follows its type order and counts a JSON null, skips an
        /// absent path, and is undefined over a set holding an array or an object; each partition's is
        /// merged by the SDK's comparator. Calcite skips every value it reads as null.
        /// </remarks>
        static string? CheckExtreme(Operand operand, bool max)
        {
            foreach (var printer in Enum.GetValues<Printer>())
            {
                var values = Admitted(operand, printer);

                foreach (var database in Subsets(values, 3))
                    for (var spread = 0; spread < 1 << database.Count; spread++)
                    {
                        var partitions = Enumerable.Range(0, 2).Select(p => database.Where((_, i) => ((spread >> i) & 1) == p).ToList()).ToList();
                        var results = partitions.Select(p => ServiceExtreme(p, max, CompareInPartition)).Where(v => v.Kind != Kind.Absent).ToList();
                        var service = results.Any(v => v.Kind is Kind.Array) ? Value.Absent : ServiceExtreme(results, max, (a, b) => CompareInMerge(a, b, printer));
                        var answered = ReadAs(operand.Reading, service, printer).Value;

                        var verdict = OrderAllowability.JudgeExtreme(database.Select(v => ReadAs(operand.Reading, v, printer).Value).ToList(), answered, max);
                        if (verdict.Failed)
                            return $"[{Family(database, false)}] over [{string.Join(", ", database)}], partitions [{string.Join("] [", partitions.Select(p => string.Join(", ", p)))}]: {verdict.Failure} ({printer} printing)";
                    }
            }

            return null;
        }

        /// <summary>
        /// One partition's extreme: undefined over an array or an object, which is written here as an
        /// array so that the merge passes it on; the absent path skipped.
        /// </summary>
        static Value ServiceExtreme(IReadOnlyList<Value> values, bool max, Func<Value, Value, int?> compare)
        {
            var defined = values.Where(v => v.Kind != Kind.Absent).ToList();
            if (defined.Any(v => v.Kind is Kind.Array or Kind.Object))
                return Value.Array("undefined");

            if (defined.Count == 0)
                return Value.Absent;

            return defined.Aggregate((a, b) => compare(a, b) is int c && (max ? c >= 0 : c <= 0) ? a : b);
        }

        static IEnumerable<List<Value>> Subsets(IReadOnlyList<Value> values, int size)
        {
            for (var i = 0; i < values.Count; i++)
            {
                yield return [values[i]];
                if (size < 2)
                    continue;

                for (var j = i; j < values.Count; j++)
                {
                    yield return [values[i], values[j]];
                    if (size < 3)
                        continue;

                    for (var k = j; k < values.Count; k++)
                        yield return [values[i], values[j], values[k]];
                }
            }
        }

        /// <summary>
        /// Every value a parameter compared with an operand may be bound to: the universe's scalars of
        /// the operand's type, a text parameter carrying any character and an integer one any int64.
        /// </summary>
        static IEnumerable<Value> Parameters(Reading reading) => Universe.Where(v => reading switch
        {
            Reading.Id or Reading.ETag or Reading.Text => v.Kind == Kind.String,
            Reading.Boolean => v.Kind == Kind.Boolean,
            Reading.Double => v.Kind == Kind.Number && v.IsDouble,
            _ => v.Kind == Kind.Number && v.IsDouble == false,
        });

        /// <summary>
        /// Finds a row a comparison keeps at the service and not in Calcite or the reverse, or returns
        /// <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The service compares values of one type, numbers exactly and strings by code point, and
        /// leaves a comparison across types undefined, which keeps nothing; a typed reading's guard
        /// keeps only its type. Calcite compares what it reads.
        /// </remarks>
        static string? CheckCompare(Operand left, Comparand right, Comparison kind, Verdict verdict)
        {
            foreach (var printer in Enum.GetValues<Printer>())
            {
                var rights = right.Literal is { } literal ? [literal]
                    : right.Path is { } path ? Admitted(path, printer)
                    : Parameters(left.Reading).Where(p => verdict.Kind != VerdictKind.Anchored || verdict.IsAnchor!(0, p)).ToList();

                var rightReading = right.Path?.Reading ?? (left.Reading is Reading.Id or Reading.ETag ? Reading.Text : left.Reading);

                foreach (var v in Admitted(left, printer))
                    foreach (var w in rights)
                    {
                        var x = ReadAs(left.Reading, v, printer);
                        var y = ReadAs(rightReading, w, printer);
                        if (y.Raises)
                            continue;

                        var calcite = x.Value is not null && y.Value is not null && Holds(CalciteOrder.CompareValues(x.Value, y.Value, false), kind);

                        var guarded = left.Reading is Reading.Id or Reading.ETag or Reading.Text || TypeOf(left.Reading) == v.Kind;
                        var service = guarded && v.Kind == w.Kind && v.Kind is Kind.Boolean or Kind.Number or Kind.String
                            && CompareInPartition(v, w) is int c && Holds(c, kind);

                        if (calcite != service)
                            return $"[{Family([v, w], false)}] {v} {kind} {w}: Calcite {(calcite ? "keeps" : "drops")} the row and the service {(service ? "keeps" : "drops")} it ({printer} printing)";
                    }
            }

            return null;
        }

        static Kind TypeOf(Reading reading) => reading == Reading.Boolean ? Kind.Boolean : Kind.Number;

        static bool Holds(int c, Comparison kind) => kind == Comparison.Less ? c < 0 : c >= 0;

        // ── The cases ──────────────────────────────────────────────────────────────────────────

        static readonly Types[] TextTypes =
        [
            Types.String,
            Types.String | Types.Null,
            Types.String | Types.Absent,
            Types.String | Types.Null | Types.Absent,
            Types.String | Types.Integer,
            Types.String | Types.Array | Types.Object,
            Types.Integer | Types.Fraction,
            Types.All,
        ];

        static readonly Bands[] Characters = [Bands.Low, Bands.Low | Bands.High, Bands.Low | Bands.Supplementary, Bands.All];

        static readonly Types[] NumberTypes =
        [
            Types.Integer,
            Types.Integer | Types.Null | Types.Absent,
            Types.Integer | Types.Fraction,
            Types.Fraction,
            Types.Fraction | Types.Null,
            Types.Integer | Types.String,
            Types.Integer | Types.Array,
            Types.All,
        ];

        static readonly Types[] BooleanTypes = [Types.Boolean, Types.Boolean | Types.Null, Types.Boolean | Types.Null | Types.Absent, Types.Boolean | Types.String];

        /// <summary>
        /// Every operand the sweep asks about: each reading over each fact set that reading can be
        /// asked of, the service's own properties under what the service guarantees of them.
        /// </summary>
        static IEnumerable<Operand> Operands()
        {
            foreach (var bands in Characters)
                yield return new(Reading.Id, new(Types.String, bands));

            yield return new(Reading.ETag, new(Types.String, Bands.Low));
            yield return new(Reading.Timestamp, new(Types.Integer, Bounded: true));

            foreach (var types in TextTypes)
                foreach (var bands in types.HasFlag(Types.String) ? Characters : [Bands.All])
                    yield return new(Reading.Text, new(types, bands));

            yield return new(Reading.Text, new(Types.String, Domain: [Value.Of("a"), Value.Of("B")]));
            yield return new(Reading.Text, new(Types.String, Domain: [Value.Of("\uE000"), Value.Of("\U0001F600")]));
            yield return new(Reading.Text, new(Types.String | Types.Null, Domain: [Value.Null, Value.Of("\uE000"), Value.Of("\uFF21")]));

            foreach (var reading in new[] { Reading.Integer, Reading.Bigint, Reading.Double })
                foreach (var types in NumberTypes)
                    foreach (var bounded in new[] { false, true })
                        yield return new(reading, new(types, Bands.All, bounded));

            foreach (var types in BooleanTypes)
                yield return new(Reading.Boolean, new(types));
        }

        /// <summary>
        /// The two-key sorts the sweep asks about: a leading key that may be null in either way, or
        /// not, before a key that never is.
        /// </summary>
        static IEnumerable<IReadOnlyList<Key>> TwoKeys()
        {
            Operand[] leading =
            [
                new(Reading.Text, new(Types.String, Bands.Low)),
                new(Reading.Text, new(Types.String | Types.Null, Bands.Low)),
                new(Reading.Text, new(Types.String | Types.Absent, Bands.Low)),
                new(Reading.Text, new(Types.String | Types.Null | Types.Absent, Bands.Low)),
                new(Reading.Integer, new(Types.Integer | Types.Null | Types.Absent)),
                new(Reading.Integer, new(Types.Integer | Types.Null)),
            ];

            Operand[] trailing =
            [
                new(Reading.Id, new(Types.String, Bands.All)),
                new(Reading.Id, new(Types.String, Bands.Low)),
                new(Reading.Integer, new(Types.Integer)),
            ];

            foreach (var first in leading)
                foreach (var second in trailing)
                    foreach (var d1 in new[] { false, true })
                        foreach (var n1 in new[] { false, true })
                            foreach (var d2 in new[] { false, true })
                                yield return [new Key(first, d1, n1), new Key(second, d2, d2)];
        }

        static IEnumerable<IReadOnlyList<Key>> OneKey() =>
            from operand in Operands()
            from down in new[] { false, true }
            from nullsFirst in new[] { false, true }
            select (IReadOnlyList<Key>)[new Key(operand, down, nullsFirst)];

        static readonly Value[] Literals = [Value.Of(""), Value.Of("a"), Value.Of("B"), Value.Of("\u00E9"), Value.Of(1), Value.Of(9223372036854775801)];

        static IEnumerable<(Operand Left, Comparand Right, Comparison Kind)> Comparisons()
        {
            Operand[] lefts =
            [
                new(Reading.Id, new(Types.String, Bands.All)),
                new(Reading.Id, new(Types.String, Bands.Low)),
                new(Reading.Text, new(Types.String | Types.Null | Types.Absent, Bands.All)),
                new(Reading.Text, new(Types.String | Types.Null | Types.Absent, Bands.Low)),
                new(Reading.Text, new(Types.String | Types.Integer, Bands.Low)),
                new(Reading.Bigint, new(Types.Integer | Types.Null)),
                new(Reading.Bigint, new(Types.Integer | Types.Null, Bounded: true)),
            ];

            foreach (var left in lefts)
                foreach (var kind in Enum.GetValues<Comparison>())
                {
                    foreach (var literal in Literals.Where(l => (l.Kind == Kind.String) == (left.Reading is Reading.Id or Reading.Text)))
                        yield return (left, new Comparand(Literal: literal), kind);

                    yield return (left, new Comparand(), kind);

                    if (left.Reading is Reading.Text or Reading.Id)
                    {
                        yield return (left, new Comparand(Path: new(Reading.Text, new(Types.String, Bands.All))), kind);
                        yield return (left, new Comparand(Path: new(Reading.Text, new(Types.String, Bands.Low))), kind);
                    }
                }
        }

        // ── The tests ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The strings the probe account was given across two physical partitions, as its report writes
        /// them: below U+D800, in both partitions; from U+E000; and beyond U+FFFF.
        /// </summary>
        static readonly string[] MeasuredLow = ["", "a", "A", "B", "_", "1", "\u00E9", "e\u0301", "\uD7FF"];

        /// <summary>
        /// Arrangement C: one partition holds characters from both sides of the line, so its stream is
        /// code point order and not UTF-16, and the merge with the other is neither.
        /// </summary>
        static readonly string[] MeasuredRange0 = ["\uFF21", "\uFFFD", "\uFFFF", "a\uE000", "\U00010000", "\U0001F600", "a\U0001F600"];

        static readonly string[] MeasuredRange1 = ["\uE000", "\U0010FFFF", "\U0001F600a"];

        /// <summary>
        /// The SDK's merge of two code-point-ordered partitions, by <c>CompareOrdinal</c> with the lower
        /// feed range first on a tie, is the stream the probe account returned over arrangement C in both
        /// directions — which is neither the code point order nor Calcite's.
        /// </summary>
        [Fact]
        public void TheMergeIsTheOneMeasuredOnAzure()
        {
            var range0 = MeasuredLow.Concat(MeasuredRange0).ToList();
            var range1 = MeasuredLow.Concat(MeasuredRange1).ToList();

            foreach (var descending in new[] { false, true })
            {
                int Partition(string a, string b) => descending ? -CompareCodePoints(a, b) : CompareCodePoints(a, b);
                int Merge(string a, string b) => descending ? -string.CompareOrdinal(a, b) : string.CompareOrdinal(a, b);

                var streams = new[] { range0, range1 }.Select(r => (IReadOnlyList<string>)r.OrderBy(s => s, Comparer<string>.Create(Partition)).ToList()).ToList();
                var merged = ServiceOrderModel.Merge(streams, Merge);

                // Measured, the probe account's report §3.2: the tail after U+D7FF, which is where the
                // two orders part, and the pair "a" + U+E000, "a" + U+1F600 where they part early.
                string[] tail = descending
                    ? ["\U0010FFFF", "\U0001F600a", "\uE000", "\U0001F600", "\U00010000", "\uFFFF", "\uFFFD", "\uFF21", "\uD7FF", "\uD7FF"]
                    : ["\uD7FF", "\uD7FF", "\uE000", "\U0001F600a", "\U0010FFFF", "\uFF21", "\uFFFD", "\uFFFF", "\U00010000", "\U0001F600"];
                string[] pair = descending ? ["a\U0001F600", "a\uE000"] : ["a\uE000", "a\U0001F600"];

                var at = descending ? 0 : merged.Count - tail.Length;
                merged.Skip(at).Take(tail.Length).Should().Equal(tail, $"the merge {(descending ? "descending" : "ascending")} ends as the probe account's did");
                merged.IndexOf(pair[0]).Should().Be(merged.IndexOf(pair[1]) - 1, "the pair comes in code point order, from one partition");

                merged.Should().NotEqual(merged.OrderBy(s => s, Comparer<string>.Create(Partition)), "the stream is not code point order");
                merged.Should().NotEqual(merged.OrderBy(s => s, Comparer<string>.Create(Merge)), "nor Calcite's");
            }
        }

        /// <summary>
        /// One partition orders by JSON type and then by value, numbers exactly and strings by code
        /// point; the merge reads a number as printed and a string by code unit.
        /// </summary>
        [Fact]
        public void ThePartitionAndTheMergeOrderAsMeasured()
        {
            Value[] typeOrder = [Value.Absent, Value.Null, Value.Of(false), Value.Of(true), Value.Of(-1), Value.OfDouble(1.5), Value.Of(""), Value.Array("[]"), Value.Object("{}")];
            for (var i = 0; i + 1 < typeOrder.Length; i++)
                CompareInPartition(typeOrder[i], typeOrder[i + 1]).Should().Be(-1, $"{typeOrder[i]} sorts below {typeOrder[i + 1]}");

            CompareInPartition(Value.Of(9007199254740992), Value.Of(9007199254740993)).Should().Be(-1, "int64 is ordered exactly");
            CompareInPartition(Value.Of(1), Value.OfDouble(1.0)).Should().Be(0, "1 and 1.0 tie");
            CompareInPartition(Value.Of(0), Value.OfDouble(-0.0)).Should().Be(0, "-0.0 is stored as 0");

            CompareInPartition(Value.Of("\uE000"), Value.Of("\U0001F600")).Should().Be(-1, "a partition orders by code point");
            CompareInMerge(Value.Of("\uE000"), Value.Of("\U0001F600"), Printer.Azure).Should().Be(1, "the merge by UTF-16 code unit");

            // 2^63 stored as a double: above 2^63 - 1 where it is stored, and below it as the emulator prints it.
            var big = Value.OfDouble(9223372036854775808d);
            CompareInPartition(Value.Of(long.MaxValue), big).Should().Be(-1);
            big.ReadBack(Printer.Emulator).Should().Be(9223372036854775800m);
            CompareInMerge(Value.Of(long.MaxValue), big, Printer.Emulator).Should().Be(1);
            CompareInMerge(Value.Of(long.MaxValue), big, Printer.Azure).Should().Be(-1);

            CompareInPartition(Value.Array("[1]"), Value.Array("[]")).Should().BeNull("arrays are ordered by no rule anyone measured");
        }

        /// <summary>
        /// The checker finds each family main is wrong in on the emulator, and passes what the facts
        /// prove: asked of a question that proves everything.
        /// </summary>
        [Fact]
        public void TheCheckCatchesWhatItMustCatch()
        {
            var everything = new ProvesEverything();

            IReadOnlyList<Key> One(Reading reading, Facts facts, bool descending = false, bool? nullsFirst = null) => [new Key(new(reading, facts), descending, nullsFirst ?? descending)];

            string? Sort(IReadOnlyList<Key> keys) => CheckOrderBy(keys, everything.OrderBy(keys));

            // The string order: a declared string, and id, holding both sides of the line.
            Sort(One(Reading.Text, new(Types.String))).Should().NotBeNull("code point and UTF-16 part over U+E000 and U+1F600");
            Sort(One(Reading.Id, new(Types.String))).Should().NotBeNull();
            Sort(One(Reading.Id, new(Types.String, Bands.Low | Bands.High))).Should().BeNull("without a surrogate the orders are one");
            Sort(One(Reading.Text, new(Types.String, Domain: [Value.Of("\uE000"), Value.Of("\U0001F600")]))).Should().NotBeNull("a domain that straddles the line");
            Sort(One(Reading.Text, new(Types.String, Domain: [Value.Of("a"), Value.Of("B")]))).Should().BeNull();

            // Null placement: the service puts undefined and null first ascending.
            Sort(One(Reading.Text, new(Types.String | Types.Null, Bands.Low))).Should().NotBeNull("NULLS LAST ascending is not the service's");
            Sort(One(Reading.Text, new(Types.String | Types.Null | Types.Absent, Bands.Low), nullsFirst: true)).Should().BeNull("the last key may be finer than Calcite");
            Sort(One(Reading.Text, new(Types.String | Types.Array, Bands.Low))).Should().BeNull("an array reads null and sorts last, where NULLS LAST puts it");
            Sort(One(Reading.Text, new(Types.String | Types.Array, Bands.Low), nullsFirst: true)).Should().NotBeNull();

            // The multi-key hazard: undefined and null tie in Calcite and not at the service.
            IReadOnlyList<Key> hazard = [new Key(new(Reading.Text, new(Types.String | Types.Null | Types.Absent, Bands.Low)), false, true), new Key(new(Reading.Id, new(Types.String, Bands.Low)), false, false)];
            Sort(hazard).Should().NotBeNull("a leading key that is null one way in one document and the other in the next");
            IReadOnlyList<Key> nulls = [new Key(new(Reading.Text, new(Types.String | Types.Null, Bands.Low)), false, true), new Key(new(Reading.Id, new(Types.String, Bands.Low)), false, false)];
            Sort(nulls).Should().BeNull("null one way only");

            // The read-back: 2^63 stored as a double beside 2^63 - 1.
            Sort(One(Reading.Bigint, new(Types.Integer))).Should().NotBeNull("2^63 comes back as 9223372036854775800");
            Sort(One(Reading.Bigint, new(Types.Integer, Bounded: true))).Should().BeNull("within 2^53 every integer is one value either way");
            Sort(One(Reading.Integer, new(Types.Integer))).Should().BeNull("an INTEGER is read only within its range, and raises past it");
            Sort(One(Reading.Boolean, new(Types.Boolean))).Should().BeNull();
            Sort(One(Reading.Timestamp, new(Types.Integer, Bounded: true))).Should().BeNull();

            // Extremes: the service's MAX of id is U+1F600 where Calcite's is U+FFFF; a JSON null is counted.
            CheckExtreme(new(Reading.Id, new(Types.String)), max: true).Should().NotBeNull();
            CheckExtreme(new(Reading.Id, new(Types.String, Bands.Low)), max: true).Should().BeNull();
            CheckExtreme(new(Reading.Text, new(Types.String | Types.Null, Bands.Low)), max: false).Should().NotBeNull("the service's MIN is the null");
            CheckExtreme(new(Reading.Text, new(Types.String | Types.Absent, Bands.Low)), max: false).Should().BeNull("an absent path is skipped");

            // Comparisons: a parameter may carry any character, a literal under the default charset cannot.
            var id = new Operand(Reading.Id, new(Types.String));
            CheckCompare(id, new Comparand(), Comparison.GreaterOrEqual, Verdict.Proved()).Should().NotBeNull("id > ? where ? is U+1F600");
            CheckCompare(id, new Comparand(Literal: Value.Of("a")), Comparison.GreaterOrEqual, Verdict.Proved()).Should().BeNull();
            CheckCompare(new(Reading.Text, new(Types.String | Types.Integer, Bands.Low)), new Comparand(Literal: Value.Of("1")), Comparison.GreaterOrEqual, Verdict.Proved())
                .Should().NotBeNull("a number renders as text in Calcite and is another type at the service");
            CheckCompare(new(Reading.Bigint, new(Types.Integer)), new Comparand(Literal: Value.Of(9223372036854775801)), Comparison.GreaterOrEqual, Verdict.Proved())
                .Should().NotBeNull("2^63 is above it stored and below it read back");
        }

        /// <summary>
        /// Every verdict the question gives over the sweep's operands holds in the model, and the
        /// verdicts it refuses that the model says it could have given are counted.
        /// </summary>
        /// <remarks>
        /// Explicit while <see cref="Question"/> is main's decisions, which the model says are wrong in
        /// the families the yardstick finds on the emulator: text orders across U+D800, the null class
        /// of a leading key, and 2^63 read back.
        /// </remarks>
        [Fact(Explicit = true)]
        public void EveryVerdictOfTheQuestionHolds()
        {
            var question = Question;
            var failures = new List<string>();
            var counts = new Dictionary<string, int>();

            void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;

            foreach (var keys in OneKey().Concat(TwoKeys()))
            {
                var verdict = question.OrderBy(keys);
                Count("sort " + verdict.Kind);

                if (verdict.Kind is VerdictKind.Proved or VerdictKind.Anchored)
                {
                    if (CheckOrderBy(keys, verdict) is string failure)
                        failures.Add($"ORDER BY {string.Join(", ", keys)}: {verdict.Kind} ({verdict.Reason}), but {failure}");
                }
                else if (verdict.Kind == VerdictKind.Refused && CheckOrderBy(keys, Verdict.Proved()) is null)
                    Count("sort refused that the model proves");
            }

            foreach (var operand in Operands())
                foreach (var max in new[] { false, true })
                {
                    var verdict = question.Extreme(operand, max);
                    Count("extreme " + verdict.Kind);

                    if (verdict.Kind == VerdictKind.Proved && CheckExtreme(operand, max) is string failure)
                        failures.Add($"{(max ? "MAX" : "MIN")} of {operand}: {verdict.Kind} ({verdict.Reason}), but {failure}");
                    else if (verdict.Kind == VerdictKind.Refused && CheckExtreme(operand, max) is null)
                        Count("extreme refused that the model proves");
                }

            foreach (var (left, right, kind) in Comparisons())
            {
                var verdict = question.Compare(left, right, kind);
                Count("comparison " + verdict.Kind);

                if (verdict.Kind is VerdictKind.Proved or VerdictKind.Anchored && CheckCompare(left, right, kind, verdict) is string failure)
                    failures.Add($"{left} {kind} {right}: {verdict.Kind} ({verdict.Reason}), but {failure}");
                else if (verdict.Kind == VerdictKind.Refused && CheckCompare(left, right, kind, Verdict.Proved()) is null)
                    Count("comparison refused that the model proves");
            }

            var summary = string.Join("; ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}"));

            // Each failure opens with its family in brackets, after the verdict.
            var families = string.Join("; ", failures
                .Select(f => f[(f.IndexOf("but [", StringComparison.Ordinal) + 5)..])
                .GroupBy(f => f[..f.IndexOf(']')])
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} {g.Count()}"));

            failures.Should().BeEmpty($"every verdict must hold in the model ({summary}; {failures.Count} failing: {families}):\n{string.Join("\n", failures)}");
        }

    }

}
