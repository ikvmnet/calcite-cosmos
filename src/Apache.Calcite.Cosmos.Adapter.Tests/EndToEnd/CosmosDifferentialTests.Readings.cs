using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    public partial class CosmosDifferentialTests
    {

        /// <summary>
        /// Statements reading one path two ways in one projection, or one way in the projection and another in a residual
        /// filter, each with the readings its statement must send.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The projection split keyed what it pushed by the node's digest</b>, which prints no type: the text accessor,
        /// <c>RETURNING INTEGER</c> and <c>RETURNING DECIMAL</c> over <c>$.price</c> all print
        /// <c>JSON_VALUE($0, '$.price')</c>, so the second reading found took the first's column. The decimal beside the
        /// integer read the integer's, and 25 halved answered 12 where Calcite answers 12.5, with no error; the text beside
        /// the integer, and a residual filter's integer beside a projected text, failed to implement. Both orders, since
        /// the reading found first is the one whose column the other took.
        /// </para>
        /// <para>
        /// The decimal reading does not render as a projection, so its statements send the document for it beside the
        /// integer; what each sends is held only to carry the readings that do render. <c>RETURNING DOUBLE</c> beside
        /// <c>RETURNING INTEGER</c> is not here: Calcite raises on it in process, so there is nothing to compare, and
        /// <c>CosmosPlannerTests.TwoReadingsOfOnePathAreTwoColumns</c> holds its plan.
        /// </para>
        /// </remarks>
        static readonly (string Sql, string[] Sends)[] TwoReadings =
        [
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) / 2, JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 FROM products AS c",
                ["(IS_NUMBER(c.price) ? c.price : null)"]),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2, JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) / 2 FROM products AS c",
                ["(IS_NUMBER(c.price) ? c.price : null)"]),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price') || '!', JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 FROM products AS c",
                ["(IS_PRIMITIVE(c.price) ? c.price : null)", "(IS_NUMBER(c.price) ? c.price : null)"]),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2, JSON_VALUE(c.\"DOC\", '$.price') || '!' FROM products AS c",
                ["(IS_PRIMITIVE(c.price) ? c.price : null)", "(IS_NUMBER(c.price) ? c.price : null)"]),
            ("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price') FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 = 12",
                ["(IS_PRIMITIVE(c.price) ? c.price : null)", "(IS_NUMBER(c.price) ? c.price : null)"]),
        ];

        /// <summary>
        /// Every statement reading one path two ways answers as Calcite would, and sends each reading that renders.
        /// </summary>
        [Fact]
        public async Task TwoReadingsOfOnePathAnswerWhatCalciteAnswers()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, sends) in TwoReadings)
            {
                var oracle = await Attempt(sql, pushdown: false);
                if (oracle.Raised is Exception e)
                {
                    failures.Add($"{sql}\n  the oracle failed to run, so this compares nothing: {e.Message}");
                    continue;
                }

                var sent = new List<string>();
                if (Refines(sql, false, oracle, await Attempt(sql, pushdown: true, sent)) is string failure)
                    failures.Add(failure);
                else if (sends.FirstOrDefault(s => sent.Any(q => q.Contains(s, StringComparison.Ordinal)) == false) is string missing)
                    failures.Add($"{sql}\n  sent {string.Join(" || ", sent)}, none holding {missing}");
            }

            failures.Should().BeEmpty("each reading of a path is its own column:\n" + string.Join("\n", failures));
        }

    }

}
