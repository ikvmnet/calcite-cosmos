using System;
using System.Linq;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's <c>AND</c>, <c>OR</c> and <c>NOT</c> answer over TRUE, FALSE and UNKNOWN, and beside an operand that
    /// raises.
    /// </summary>
    /// <remarks>
    /// The facts graph composes in Kleene's logic, and the rendering graph's licences compare the service's connectives
    /// with Calcite's, so Calcite's half is a row too. The operands are read from documents in a scanned table, so that
    /// nothing is folded at planning: <c>RETURNING BOOLEAN</c> over a boolean or an absent path, and
    /// <c>RETURNING INTEGER</c> over a string, which raises.
    /// </remarks>
    public class CalciteConnectiveMeasurementTests
    {

        const string A = "JSON_VALUE(\"d\", '$.a' RETURNING BOOLEAN)";
        const string B = "JSON_VALUE(\"d\", '$.b' RETURNING BOOLEAN)";
        const string Raising = "(JSON_VALUE(\"d\", '$.r' RETURNING INTEGER) = 1)";

        /// <summary>
        /// <c>AND</c>, <c>OR</c> and <c>NOT</c> are Kleene's, in either order; and an operand that raises raises the
        /// connective beside FALSE under <c>AND</c> and beside TRUE under <c>OR</c>, in either order.
        /// </summary>
        /// <remarks>
        /// Calcite evaluates both operands of a nullable connective before combining them, so the arm that decides it
        /// does not keep the other from being evaluated.
        /// </remarks>
        [Fact]
        public void TheConnectivesAreKleenesAndEvaluateBothOperands()
        {
            using var connection = MaterialisedTable.Open();

            // a and b each TRUE, FALSE and UNKNOWN: TT, TF, TU, FT, FF, FU, UT, UF, UU.
            var values = new[] { "true", "false", null };
            var pairs = MaterialisedTable.Of(values.SelectMany(a => values.Select(b =>
                "{" + string.Join(",", new[] { a is null ? null : "\"a\":" + a, b is null ? null : "\"b\":" + b }.Where(p => p is not null)) + "}")));

            pairs.Ask(connection, $"{A} AND {B}", $"{B} AND {A}", $"{A} OR {B}", $"{B} OR {A}", $"NOT {A}").Should().Equal(
                "TFUFFFUFU", "TFUFFFUFU", "TTTTFUTUU", "TTTTFUTUU", "FFFTTTUUU");

            var falseBeside = MaterialisedTable.Of(new[] { "{\"a\":false,\"r\":\"x\"}" });
            foreach (var p in new[] { $"{A} AND {Raising}", $"{Raising} AND {A}", $"NOT {Raising}" })
                falseBeside.Failure(connection, p).Should().BeOfType<InvalidCastException>($"FALSE does not keep {p} from raising");

            var trueBeside = MaterialisedTable.Of(new[] { "{\"a\":true,\"r\":\"x\"}" });
            foreach (var p in new[] { $"{A} OR {Raising}", $"{Raising} OR {A}" })
                trueBeside.Failure(connection, p).Should().BeOfType<InvalidCastException>($"TRUE does not keep {p} from raising");
        }

    }

}
