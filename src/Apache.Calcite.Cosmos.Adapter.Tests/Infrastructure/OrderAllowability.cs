using System;
using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// Decides whether an answer is one Calcite's semantics allow: the rule a pushed order is judged
    /// by, which is not "the rows Calcite's own run returned".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Allowable, not equal.</b> Let σ be a sort with collation C, offset o and fetch f over a
    /// multiset R of N rows. An answer S is allowable iff it is <c>L[o+1 … min(o+f, N)]</c> for some
    /// arrangement L of R that is non-decreasing under C. Which rows of a tie class appear is free, and
    /// so is their order; with no collation every row is one tie class, so a <c>FETCH</c> with no
    /// <c>ORDER BY</c> may answer any of them. Checked without searching arrangements:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>(A0)</b> every row of S is a row of R, each used once;</description></item>
    /// <item><description><b>(A1)</b> <c>|S| = max(0, min(f, N − o))</c>;</description></item>
    /// <item><description><b>(A2)</b> S is non-decreasing under C, where the answer's order is
    /// asked for at all;</description></item>
    /// <item><description><b>(A3)</b> each tie class holds exactly its share of the window
    /// <c>[o, o + |S|)</c>.</description></item>
    /// </list>
    /// <para>
    /// <b>Rows are matched by what the answer selects.</b> R's rows carry the answer's columns first and
    /// the keys after them, so a statement that does not select its key is still judged by it. A
    /// statement whose selected columns do not determine its keys — two rows selecting alike and
    /// sorting apart — cannot be matched, and is reported as not judged rather than judged by a guess:
    /// the statements the yardstick draws select a row's identity wherever they can.
    /// </para>
    /// </remarks>
    public static class OrderAllowability
    {

        /// <summary>
        /// What judging an answer found.
        /// </summary>
        /// <param name="Judged">Whether a judgement was possible at all: <c>false</c> where the keys
        /// hold values Calcite defines no order for, or the answer's rows cannot be matched.</param>
        /// <param name="Failure">What is not allowable, or <c>null</c> where the answer is allowable or
        /// was not judged.</param>
        /// <param name="Inversion">Where (A2) failed, the two adjacent rows of R that S holds in the
        /// wrong order, the first first.</param>
        public sealed record Verdict(bool Judged, string? Failure, (object?[] Before, object?[] After)? Inversion = null)
        {

            /// <summary>The answer is allowable.</summary>
            public static readonly Verdict Allowable = new(true, null);

            /// <summary>Whether the answer was judged and found not allowable.</summary>
            public bool Failed => Failure is not null;

            /// <summary>An answer that could not be judged, and why.</summary>
            public static Verdict NotJudged(string why) => new(false, null) { Reason = why };

            /// <summary>Why the answer was not judged, where it was not.</summary>
            public string? Reason { get; init; }

        }

        /// <summary>
        /// Judges a window of a sort.
        /// </summary>
        /// <param name="rows">R: every row the sort is over, the answer's columns first.</param>
        /// <param name="answer">S: the rows answered, in the order they came.</param>
        /// <param name="width">How many leading columns of each row of R the answer's rows hold.</param>
        /// <param name="keys">The collation, over the columns of R. Empty for a window of no
        /// order.</param>
        /// <param name="offset">The window's offset.</param>
        /// <param name="fetch">The window's size, or <c>null</c> for no limit.</param>
        /// <param name="ordered">Whether the answer's own order is asked for: <c>false</c> for a
        /// window whose rows a parent reads in no order, such as a subquery's
        /// <c>ORDER BY … FETCH</c>.</param>
        /// <param name="text">How strings compare: Calcite's order unless a caller asks whether the
        /// answer would be allowable under another, which is how a failure is told to be the string
        /// order's.</param>
        public static Verdict Judge(IReadOnlyList<object?[]> rows, IReadOnlyList<object?[]> answer, int width, IReadOnlyList<CalciteSortKey> keys, int offset, int? fetch, bool ordered, Comparison<string>? text = null)
        {
            try
            {
                return JudgeCore(rows, answer, width, keys, offset, fetch, ordered, text);
            }
            catch (NotSupportedException e)
            {
                return Verdict.NotJudged(e.Message);
            }
        }

        static Verdict JudgeCore(IReadOnlyList<object?[]> rows, IReadOnlyList<object?[]> answer, int width, IReadOnlyList<CalciteSortKey> keys, int offset, int? fetch, bool ordered, Comparison<string>? text)
        {
            int Compare(object?[] a, object?[] b) => CalciteOrder.Compare(a, b, keys, text);

            // (A0): match every answered row to a row of R, each once.
            var unused = rows
                .Select((row, i) => (Key: Projection(row, width), Index: i))
                .GroupBy(r => r.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Queue<int>(g.Select(r => r.Index)), StringComparer.Ordinal);

            var matched = new List<object?[]>(answer.Count);
            foreach (var row in answer)
            {
                var key = Projection(row, width);
                if (unused.TryGetValue(key, out var candidates) == false || candidates.Count == 0)
                {
                    // The input's row that starts alike, where there is one, says which column differs.
                    var near = rows.FirstOrDefault(r => width > 0 && CalciteOrder.Canonical(r[0]) == CalciteOrder.Canonical(row[0]) && (width < 2 || CalciteOrder.Canonical(r[1]) == CalciteOrder.Canonical(row[1])));
                    return new Verdict(true, $"(A0) the answer holds a row the input does not, or holds it more often: {Render(row, width)}" +
                        (near is null ? "" : $"; the input's row that starts alike is {Render(near, width)}"));
                }

                // Rows selecting alike must sort alike, or which of them was answered is unknowable.
                var first = rows[candidates.Peek()];
                foreach (var other in candidates)
                    if (keys.Count > 0 && Compare(first, rows[other]) != 0)
                        return Verdict.NotJudged($"the answer's columns do not determine its keys: {Render(row, width)} selects rows that sort apart");

                matched.Add(rows[candidates.Dequeue()]);
            }

            // (A1): the count.
            var expected = Math.Max(0, Math.Min(fetch ?? int.MaxValue, rows.Count - offset));
            if (matched.Count != expected)
                return new Verdict(true, $"(A1) the answer holds {matched.Count} rows where a window of {rows.Count} at offset {offset}{(fetch is int f ? $" fetching {f}" : "")} holds {expected}");

            // (A2): the order, where it is asked for.
            if (ordered)
                for (var i = 0; i + 1 < matched.Count; i++)
                    if (Compare(matched[i], matched[i + 1]) > 0)
                        return new Verdict(true, $"(A2) row {i + 1} of the answer, {Render(matched[i], width)}, comes before {Render(matched[i + 1], width)}, which Calcite orders first under {string.Join(", ", keys)}", (matched[i], matched[i + 1]));

            // (A3): each tie class's share of the window.
            var sorted = rows.ToList();
            sorted.Sort(Compare);

            var classOf = new int[sorted.Count];
            for (var i = 1; i < sorted.Count; i++)
                classOf[i] = Compare(sorted[i - 1], sorted[i]) == 0 ? classOf[i - 1] : classOf[i - 1] + 1;

            var share = new Dictionary<int, int>();
            for (var i = offset; i < offset + matched.Count && i < sorted.Count; i++)
                share[classOf[i]] = share.GetValueOrDefault(classOf[i]) + 1;

            var held = new Dictionary<int, int>();
            foreach (var row in matched)
            {
                var c = ClassOf(sorted, classOf, row, Compare);
                held[c] = held.GetValueOrDefault(c) + 1;
            }

            foreach (var c in share.Keys.Union(held.Keys).OrderBy(c => c))
            {
                var want = share.GetValueOrDefault(c);
                var have = held.GetValueOrDefault(c);
                if (want != have)
                {
                    var example = sorted[Array.IndexOf(classOf, c)];
                    var at = "(" + string.Join(", ", keys.Select(k => Show(example[k.Field]))) + ")";
                    return new Verdict(true, $"(A3) the answer holds {have} rows of the tie class keyed {at}, where the window holds {want}");
                }
            }

            return Verdict.Allowable;
        }

        /// <summary>
        /// Judges an extreme: <c>MIN</c> or <c>MAX</c> of one value per row.
        /// </summary>
        /// <remarks>
        /// SQL's extreme skips nulls, and answers null where every value is one. Otherwise any value of
        /// Calcite's extreme tie class is allowable, which for every type here is the extreme itself.
        /// </remarks>
        /// <param name="values">The argument's value in every row the aggregate reads.</param>
        /// <param name="answer">The extreme answered.</param>
        /// <param name="max">Whether it is <c>MAX</c>.</param>
        public static Verdict JudgeExtreme(IReadOnlyList<object?> values, object? answer, bool max)
        {
            try
            {
                var present = values.Where(v => v is not null).ToList();
                if (present.Count == 0)
                    return answer is null ? Verdict.Allowable : new Verdict(true, $"every value is null, so {(max ? "MAX" : "MIN")} is, and the answer is {Show(answer)}");

                var extreme = present.Aggregate((a, b) => CalciteOrder.CompareValues(a!, b!, false) is var c && (max ? c >= 0 : c <= 0) ? a : b);
                if (answer is null || CalciteOrder.CompareValues(answer, extreme!, false) != 0)
                    return new Verdict(true, $"{(max ? "MAX" : "MIN")} is {Show(extreme)}, and the answer is {Show(answer)}");

                return Verdict.Allowable;
            }
            catch (NotSupportedException e)
            {
                return Verdict.NotJudged(e.Message);
            }
        }

        static int ClassOf(List<object?[]> sorted, int[] classOf, object?[] row, Comparison<object?[]> compare)
        {
            // Binary search for any row tying with this one: the classes are runs of the sorted rows.
            int lo = 0, hi = sorted.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var c = compare(sorted[mid], row);
                if (c == 0)
                    return classOf[mid];
                if (c < 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            throw new InvalidOperationException("A matched row ties with no row of the input.");
        }

        static string Projection(object?[] row, int width) => string.Join(" | ", row.Take(width).Select(CalciteOrder.Canonical));

        static string Render(object?[] row, int width) => "(" + string.Join(", ", row.Take(width).Select(Show)) + ")";

        /// <summary>A value as a report shows it: text escaped, anything else canonical.</summary>
        public static string Show(object? value) => value is string s ? Escape(s) : CalciteOrder.Canonical(value);

        /// <summary>
        /// A string with every character outside printable ASCII written as its code point, so that a
        /// report says which characters a witness holds.
        /// </summary>
        public static string Escape(string s)
        {
            var text = new System.Text.StringBuilder("'");
            foreach (var rune in s.EnumerateRunes())
                text.Append(rune.Value is >= 0x20 and < 0x7F ? ((char)rune.Value).ToString() : $"<U+{rune.Value:X4}>");

            return text.Append('\'').ToString();
        }

    }

}
