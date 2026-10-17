using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// The judge the <c>ORDER BY</c> yardstick holds every pushed order to, held in its turn to Calcite's
    /// semantics: what it calls allowable, what it does not, and the comparator under it pinned to the
    /// cases the Calcite inventory measured.
    /// </summary>
    /// <remarks>
    /// A judge that is wrong passes a wrong push or fails a right one, and either is invisible from the
    /// yardstick's own report. So each rule of <see cref="OrderAllowability"/> is asked here of rows
    /// written by hand, both ways.
    /// </remarks>
    public class CosmosOrderAllowabilityTests
    {

        /// <summary>Rows of one column, the column being the key.</summary>
        static List<object?[]> Rows(params object?[] values) => values.Select(v => new[] { v }).ToList();

        static readonly CalciteSortKey[] Ascending = [CalciteSortKey.Default(0)];

        static OrderAllowability.Verdict Judge(List<object?[]> rows, List<object?[]> answer, IReadOnlyList<CalciteSortKey> keys, int offset = 0, int? fetch = null, int width = 1, bool ordered = true) =>
            OrderAllowability.Judge(rows, answer, width, keys, offset, fetch, ordered);

        /// <summary>
        /// Two rows tied at the edge of a window: either may be the one answered.
        /// </summary>
        [Fact]
        public void EitherRowOfATieAtTheWindowsEdgeIsAllowable()
        {
            // (id, k): b and c tie on k, and FETCH 2 takes one of them.
            var rows = new List<object?[]> { new object?[] { "a", 1L }, new object?[] { "b", 2L }, new object?[] { "c", 2L }, new object?[] { "d", 3L } };
            var keys = new[] { CalciteSortKey.Default(1) };

            Judge(rows, [rows[0], rows[1]], keys, fetch: 2).Failed.Should().BeFalse();
            Judge(rows, [rows[0], rows[2]], keys, fetch: 2).Failed.Should().BeFalse();
            Judge(rows, [rows[0], rows[3]], keys, fetch: 2).Failure.Should().StartWith("(A3)", "d is not in the window's tie classes");
            Judge(rows, [rows[1], rows[2]], keys, offset: 1, fetch: 2).Failed.Should().BeFalse("the window at 1 is the tie class whole");
            Judge(rows, [rows[2], rows[1]], keys, offset: 1, fetch: 2).Failed.Should().BeFalse("and in either order");
        }

        /// <summary>
        /// Nulls go where the key says, whatever the direction: <c>DESC NULLS LAST</c> puts them at the
        /// end, and unqualified <c>DESC</c> at the start.
        /// </summary>
        [Fact]
        public void NullsArePlacedAbsolutely()
        {
            var rows = Rows("a", null, "b");

            Judge(rows, Rows("b", "a", null), [new CalciteSortKey(0, true, false)]).Failed.Should().BeFalse();
            Judge(rows, Rows(null, "b", "a"), [new CalciteSortKey(0, true, false)]).Failure.Should().StartWith("(A2)");
            Judge(rows, Rows(null, "b", "a"), [CalciteSortKey.Default(0, descending: true)]).Failed.Should().BeFalse("DESC is NULLS FIRST unqualified");
            Judge(rows, Rows(null, "a", "b"), [new CalciteSortKey(0, false, true)]).Failed.Should().BeFalse();
            Judge(rows, Rows("a", "b", null), Ascending).Failed.Should().BeFalse("ASC is NULLS LAST unqualified");
        }

        /// <summary>
        /// Two keys tied on the first are ordered by the second, and only there may the answer be finer
        /// than Calcite.
        /// </summary>
        [Fact]
        public void TwoKeysTiedOnTheFirstAreOrderedByTheSecond()
        {
            var rows = new List<object?[]> { new object?[] { null, "b" }, new object?[] { null, "a" }, new object?[] { "x", "c" } };
            CalciteSortKey[] keys = [new CalciteSortKey(0, false, true), CalciteSortKey.Default(1)];

            Judge(rows, [rows[1], rows[0], rows[2]], keys, width: 2).Failed.Should().BeFalse();
            Judge(rows, [rows[0], rows[1], rows[2]], keys, width: 2).Failure.Should().StartWith("(A2)", "the second key decides between the two nulls");

            // FETCH 1 must take the null keyed "a", not the one keyed "b": the multi-key hazard's shape.
            Judge(rows, [rows[0]], keys, fetch: 1, width: 2).Failure.Should().StartWith("(A3)");
        }

        /// <summary>
        /// An answer must hold the rows it is a window of, the right number of them, each once.
        /// </summary>
        [Fact]
        public void TheCountAndTheRowsAreJudged()
        {
            var rows = Rows("a", "b", "c");

            Judge(rows, Rows("a", "b"), Ascending).Failure.Should().StartWith("(A1)");
            Judge(rows, Rows("a", "b", "c"), Ascending, offset: 1).Failure.Should().StartWith("(A1)");
            Judge(rows, Rows("a", "a", "b"), Ascending).Failure.Should().StartWith("(A0)", "a is in the input once");
            Judge(rows, Rows("a", "z"), Ascending, fetch: 2).Failure.Should().StartWith("(A0)");
            Judge(rows, Rows(), Ascending, offset: 5).Failed.Should().BeFalse("a window past the end is empty");
            Judge(rows, Rows("c", "a"), [], fetch: 2).Failed.Should().BeFalse("with no order, any two rows in any order");
            Judge(rows, Rows("c", "a"), Ascending, fetch: 3, ordered: false).Failure.Should().StartWith("(A1)");
            Judge(rows, Rows("b", "a"), Ascending, fetch: 2, ordered: false).Failed.Should().BeFalse("a window read in no order needs only its rows");
        }

        /// <summary>
        /// A row whose selected columns do not say where it sorts is not judged, rather than judged by
        /// a guess.
        /// </summary>
        [Fact]
        public void AnAnswerThatCannotBeMatchedIsNotJudged()
        {
            // The answer selects the first column, and two rows select "x" while sorting apart.
            var rows = new List<object?[]> { new object?[] { "x", 1L }, new object?[] { "x", 2L } };

            var verdict = Judge(rows, [new object?[] { "x" }], [CalciteSortKey.Default(1)], fetch: 1);
            verdict.Judged.Should().BeFalse();
            verdict.Failed.Should().BeFalse();
        }

        /// <summary>
        /// Text by UTF-16 code unit, as Calcite's <c>String.compareTo</c>: the inventory's S1 and U1,
        /// which the CLR runtime answers by culture under two keys.
        /// </summary>
        [Fact]
        public void TextIsOrderedByCodeUnit()
        {
            Sorted(Ascending, "b", "B", "a", "A", "_", "1", "\u00E9").Should().Equal("1", "A", "B", "_", "a", "b", "\u00E9");
            Sorted(Ascending, "\uE000", "\U0001F600", "\uFFFD", "\uFF21", "a").Should().Equal("a", "\U0001F600", "\uE000", "\uFF21", "\uFFFD");

            // Under two keys too: the second key is what the CLR runtime's culture order would reach.
            var rows = new List<object?[]> { new object?[] { "a", 1L }, new object?[] { "B", 1L } };
            CalciteOrder.Compare(rows[1], rows[0], [CalciteSortKey.Default(0), CalciteSortKey.Default(1)]).Should().BeNegative("'B' is below 'a' by code unit");
            CalciteOrder.CompareValues("a\u00ADb", "ab", several: true).Should().NotBe(0, "an ignorable character is a character");
        }

        /// <summary>
        /// The inventory's B1, M1, N1–N4, Q1 and the two readings of ±0.
        /// </summary>
        [Fact]
        public void TheOtherTypesAreOrderedAsCalciteOrdersThem()
        {
            Sorted(Ascending, true, null, false).Should().Equal(false, true, null);

            CalciteOrder.CompareValues(new java.math.BigDecimal("1.1"), new java.math.BigDecimal("1.10"), several: false).Should().Be(0, "a DECIMAL's scale is ignored");
            CalciteOrder.CompareValues(9007199254740993L, java.lang.Long.valueOf(9007199254740992L), several: false).Should().Be(1, "an integer is exact past 2^53");

            Sorted(Ascending, double.NaN, double.PositiveInfinity, 5d).Should().Equal(5d, double.PositiveInfinity, double.NaN);
            CalciteOrder.CompareValues(-0.0, 0.0, several: false).Should().Be(0, "one key compares d0 == d1 first");
            CalciteOrder.CompareValues(-0.0, 0.0, several: true).Should().Be(-1, "several compare Double.compare alone");

            CalciteOrder.CompareValues(System.Guid.Parse("ffffffff-0000-0000-0000-000000000000"), System.Guid.Parse("7fffffff-0000-0000-0000-000000000000"), several: false)
                .Should().Be(1, "a UUID is unsigned");
            CalciteOrder.Canonical(System.Guid.Parse("ABCDEF01-2345-6789-ABCD-EF0123456789")).Should().Be(CalciteOrder.Canonical(System.Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789")));
        }

        /// <summary>
        /// An extreme is judged by Calcite's extreme tie class over what is not null.
        /// </summary>
        [Fact]
        public void AnExtremeIsJudgedOverWhatIsNotNull()
        {
            OrderAllowability.JudgeExtreme(["\uFFFD", "\U0001F600", null], "\uFFFD", max: true).Failed.Should().BeFalse("U+FFFD is the greatest by code unit");
            OrderAllowability.JudgeExtreme(["\uFFFD", "\U0001F600", null], "\U0001F600", max: true).Failed.Should().BeTrue("the service's code point MAX");
            OrderAllowability.JudgeExtreme([null, null], null, max: false).Failed.Should().BeFalse();
            OrderAllowability.JudgeExtreme(["a", null], null, max: false).Failed.Should().BeTrue("MIN skips the null");
        }

        /// <summary>
        /// A comparison's rows are judged as a multiset, exactly: a window of no order over all of them,
        /// read in no order.
        /// </summary>
        [Fact]
        public void AComparisonsRowsAreJudgedExactly()
        {
            Judge(Rows("a", "b"), Rows("b", "a"), [], ordered: false).Failed.Should().BeFalse();
            Judge(Rows("a", "b"), Rows("a"), [], ordered: false).Failure.Should().StartWith("(A1)", "b is lost");
            Judge(Rows(9007199254740993L), Rows(9007199254740992L), [], ordered: false).Failure.Should().StartWith("(A0)", "the two integers are two values, where a double has one");
        }

        static List<object?> Sorted(IReadOnlyList<CalciteSortKey> keys, params object?[] values)
        {
            var rows = Rows(values);
            rows.Sort((a, b) => CalciteOrder.Compare(a, b, keys));
            return rows.Select(r => r[0]).ToList();
        }

    }

}
