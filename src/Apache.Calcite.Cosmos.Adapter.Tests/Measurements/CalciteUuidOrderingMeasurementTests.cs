using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Data;

using FluentAssertions;
using Xunit;

using org.apache.calcite.config;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// The order Calcite compares UUIDs in, measured and asserted so that a change upstream is
    /// reported here rather than discovered in a pushed sort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the fact every UUID row in <see cref="CosmosStoredForms"/> rests on.</b> A form
    /// claims that comparing the <em>stored strings</em> answers what comparing the <em>values</em>
    /// answers; the left side is the service's business and the right side is the engine's, and
    /// nothing in the adapter can settle the right side by reasoning. So it is asked.
    /// </para>
    /// <para>
    /// <b>It has already changed once, which is why it is asserted rather than assumed.</b>
    /// <c>java.util.UUID#compareTo</c> compares the two 64-bit halves as <em>signed</em> longs — a
    /// long-documented JDK quirk — so the lexical order of the canonical string was Calcite's order
    /// only where the top bit of each half was constant across the container. CALCITE-7716 treated
    /// that as a defect rather than as semantics and added <c>org.apache.calcite.util.UuidValue</c>,
    /// which compares unsigned, in 1.43. The rows were written against the old answer and #142 is the
    /// cost of their having outlived it.
    /// </para>
    /// <para>
    /// <b>Measured below the wrapper, with the wrapper as a control.</b> Calcite's own JDBC driver
    /// answers first, because an ADO.NET layer that reordered rows and an engine that ordered them
    /// that way look identical from <c>CalciteDataReader</c>; the same query through
    /// <c>CalciteConnection</c> then says the two agree.
    /// </para>
    /// </remarks>
    public class CalciteUuidOrderingMeasurementTests
    {

        /// <summary>
        /// Values chosen to separate a signed comparison from an unsigned one on <em>both</em> halves.
        /// </summary>
        /// <remarks>
        /// The 1st hex digit decides the sign of <c>mostSigBits</c> and the 17th that of
        /// <c>leastSigBits</c>, so each is given values on either side of <c>8</c>: a signed
        /// comparison puts <c>8000…</c> and <c>ffff…</c> below <c>0000…</c>, and puts
        /// <c>…-8000-…</c> and <c>…-a000-…</c> below <c>…-0000-…</c>. The nil UUID and the max one are
        /// the ends, and a v1 value is an ordinary one from the middle.
        /// </remarks>
        static readonly string[] Values =
        {
            "00000000-0000-0000-0000-000000000000",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
            "7fffffff-ffff-ffff-ffff-ffffffffffff",
            "80000000-0000-0000-0000-000000000000",
            "0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f",
            "8ba7b810-9dad-11d1-80b4-00c04fd430c8",
            "00000000-0000-0000-7fff-ffffffffffff",
            "00000000-0000-0000-8000-000000000000",
            "00000000-0000-0000-a000-000000000000",
        };

        static string OrderByUuid() =>
            "SELECT u FROM (VALUES " + string.Join(", ", Values.Select(v => "(CAST('" + v + "' AS UUID))")) + ") AS t(u) ORDER BY u";

        /// <summary>
        /// Runs a query through Calcite's own JDBC driver, with no ADO.NET layer in the path.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <returns>The first column of every row, rendered.</returns>
        static IReadOnlyList<string?> Jdbc(string sql)
        {
            java.lang.Class.forName("org.apache.calcite.jdbc.Driver");

            var connection = java.sql.DriverManager.getConnection("jdbc:calcite:", new java.util.Properties());

            try
            {
                var results = connection.createStatement().executeQuery(sql);
                var rows = new List<string?>();

                while (results.next())
                    rows.Add(results.getObject(1)?.ToString());

                return rows;
            }
            finally
            {
                connection.close();
            }
        }

        /// <summary>
        /// The property the fix sits behind defaults to on.
        /// </summary>
        /// <remarks>
        /// <see cref="CosmosStoredForms"/> reads the same property to decide what the unconfined rows
        /// license, so this is the tie between the engine's switch and the adapter's claim — and the
        /// reason the claim is conditioned rather than hard-coded, a runtime that turns it off getting
        /// the signed semantics back.
        /// </remarks>
        [Fact]
        public void TheUnsignedComparisonIsOnByDefault()
        {
            ((java.lang.Boolean)CalciteSystemProperty.UUID_UNSIGNED_COMPARISON.value()).booleanValue()
                .Should().BeTrue("CALCITE-7716 defaults calcite.uuid.unsigned.comparison to on");

            CosmosUuidForms.CanonicalLower.PreservesOrder.Should().BeTrue(
                "and an unconfined canonical form carries the order under it");
        }

        /// <summary>
        /// <c>ORDER BY</c> over UUIDs is exactly the ordinal order of their canonical spellings.
        /// </summary>
        /// <remarks>
        /// The whole of what a stored form needs. A canonical lowercase spelling draws from
        /// <c>0-9a-f</c> alone, the hyphens sit at fixed positions, and those characters sort in
        /// nibble order — so ordinal text order is the unsigned 128-bit order, and this says the
        /// engine orders that way too. Under the signed comparison the two lists differed on four of
        /// the nine values.
        /// </remarks>
        [Fact]
        public void OrderingIsTheOrdinalOrderOfTheCanonicalSpelling()
        {
            Jdbc(OrderByUuid()).Should().Equal(Values.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                "which is what lets a sort over the stored strings stand in for a sort over the values");
        }

        /// <summary>
        /// The wrapper agrees with the driver, so the ordering above is the engine's and not a layer's.
        /// </summary>
        [Fact]
        public void TheWrapperReportsTheSameOrder()
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = OrderByUuid();

            var rows = new List<string?>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(reader.GetValue(0)?.ToString());

            rows.Should().Equal(Jdbc(OrderByUuid()));
        }

        /// <summary>
        /// The three comparisons the signed order got wrong, each answering the other way now.
        /// </summary>
        /// <remarks>
        /// The first is the JIRA's own case and turns on the 1st hex digit; the second and third turn
        /// on the 17th, which RFC 4122 pins to <c>8</c>–<c>b</c> for every conforming value and which
        /// is why the low half used to agree wherever the variant nibble was in the pattern. All three
        /// answered <b>False</b> before 1.43.
        /// </remarks>
        [Fact]
        public void TheComparisonsTheSignedOrderGotWrongAnswerTheOtherWay()
        {
            Jdbc("""
                SELECT CAST('80000000-0000-0000-0000-000000000000' AS UUID) > CAST('00000000-0000-0000-0000-000000000000' AS UUID)
                """).Should().Equal(new[] { "true" });

            Jdbc("""
                SELECT CAST('00000000-0000-0000-0000-000000000000' AS UUID) < CAST('00000000-0000-0000-a000-000000000000' AS UUID)
                """).Should().Equal(new[] { "true" });

            Jdbc("""
                SELECT CAST('00000000-0000-0000-8000-000000000000' AS UUID) < CAST('ffffffff-ffff-ffff-ffff-ffffffffffff' AS UUID)
                """).Should().Equal(new[] { "true" });
        }

    }

}
