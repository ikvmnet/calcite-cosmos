using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;

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
    /// <c>RETURNING INTEGER</c> over a string, which raises; and, <c>NOT NULL</c>, a test of the key and a cast of the
    /// document's text, which raises.
    /// </remarks>
    public class CalciteConnectiveMeasurementTests
    {

        const string A = "JSON_VALUE(\"d\", '$.a' RETURNING BOOLEAN)";
        const string B = "JSON_VALUE(\"d\", '$.b' RETURNING BOOLEAN)";
        const string Raising = "(JSON_VALUE(\"d\", '$.r' RETURNING INTEGER) = 1)";

        // NOT NULL operands: a cast of the document's text, no integer, which raises; and a test of the key, which every
        // row's is at least zero.
        const string RaisingNotNull = "(CAST(\"d\" AS INTEGER) = 1)";
        const string NotNullFalse = "(\"k\" < 0)";
        const string NotNullTrue = "(\"k\" >= 0)";

        /// <summary>
        /// <c>AND</c>, <c>OR</c> and <c>NOT</c> are Kleene's, in either order; and an operand that raises, nullable or
        /// <c>NOT NULL</c>, raises the connective beside FALSE under <c>AND</c> and beside TRUE under <c>OR</c>, nullable or
        /// <c>NOT NULL</c>, in either order, projected and as a <c>WHERE</c>.
        /// </summary>
        /// <remarks>
        /// What each answers is the row's (<c>Calcite.ConnectivesAreKleene</c>), the first operand its key and the second
        /// its cell, so either order reads it. Calcite evaluates both operands of a connective before combining them, a
        /// <c>NOT NULL</c> one read once among them, since Linq4j keeps a declaration that may throw where it is read
        /// (<c>BlockBuilder</c>), so the arm that decides it does not keep the other from being evaluated.
        /// </remarks>
        [Fact, CosmosPins("Calcite.ConnectivesAreKleene")]
        public void TheConnectivesAreKleenesAndEvaluateBothOperands()
        {
            using var connection = MaterialisedTable.Open();

            // a and b each TRUE, FALSE and UNKNOWN: TT, TF, TU, FT, FF, FU, UT, UF, UU.
            var values = new[] { "true", "false", null };
            var pairs = MaterialisedTable.Of(values.SelectMany(a => values.Select(b =>
                "{" + string.Join(",", new[] { a is null ? null : "\"a\":" + a, b is null ? null : "\"b\":" + b }.Where(p => p is not null)) + "}")));

            var row = CosmosAxioms.Get("Calcite.ConnectivesAreKleene").Outcomes!.ToDictionary(o => (o.Test, o.Operand), o => o.Cells.ToDictionary(c => c.Cell, c => c.Answer.ToString()[0]));
            CosmosValueClass[] truths = [CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Undefined];
            string Each(Func<CosmosValueClass, CosmosValueClass, char> answer) => string.Concat(truths.SelectMany(a => truths.Select(b => answer(a, b))));
            pairs.Ask(connection, $"{A} AND {B}", $"{B} AND {A}", $"{A} OR {B}", $"{B} OR {A}", $"NOT {A}").Should().Equal(
                Each((a, b) => row[(CosmosServiceTest.And, a)][b]), Each((a, b) => row[(CosmosServiceTest.And, b)][a]),
                Each((a, b) => row[(CosmosServiceTest.Or, a)][b]), Each((a, b) => row[(CosmosServiceTest.Or, b)][a]), Each((a, _) => row[(CosmosServiceTest.Not, CosmosValueClass.None)][a]));

            var falseBeside = MaterialisedTable.Of(new[] { "{\"a\":false,\"r\":\"x\"}" });
            var trueBeside = MaterialisedTable.Of(new[] { "{\"a\":true,\"r\":\"x\"}" });
            falseBeside.Failure(connection, $"NOT {Raising}").Should().BeOfType<InvalidCastException>("NOT raises over an operand that raises");

            // An operand that raises, nullable or NOT NULL, is evaluated beside the arm that decides the connective, FALSE
            // under AND and TRUE under OR, nullable or NOT NULL, either way round: projected, and as a WHERE, which a Calc
            // evaluates as its condition.
            var columns = falseBeside.getRowType(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());
            new[] { "k", "d" }.Select(c => columns.getField(c, true, false).getType().isNullable()).Should().Equal([false, false], "the key and the document are NOT NULL");
            var unraised = new List<string>();
            foreach (var (raising, raises) in new[] { (Raising, typeof(InvalidCastException)), (RaisingNotNull, typeof(java.lang.NumberFormatException)) })
                foreach (var (beside, connective) in new[] { (A, "AND"), (NotNullFalse, "AND"), (A, "OR"), (NotNullTrue, "OR") })
                    foreach (var p in new[] { $"{beside} {connective} {raising}", $"{raising} {connective} {beside}" })
                    {
                        var table = connective == "AND" ? falseBeside : trueBeside;
                        foreach (var (form, failure) in new[] { ("projected", table.Failure(connection, p)), ("as a WHERE", Failure(() => table.Keeps(connection, p))) })
                            if (failure?.GetType() != raises)
                                unraised.Add($"{p} {form}: {failure?.GetType().Name ?? "no raise"}");
                    }

            unraised.Should().BeEmpty("the arm that decides a connective does not keep the other from being evaluated");
        }

        /// <summary>The innermost exception an action raises, or <c>null</c> where it raises none.</summary>
        static Exception? Failure(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                while (e.InnerException is Exception inner)
                    e = inner;

                return e;
            }
        }

    }

}
