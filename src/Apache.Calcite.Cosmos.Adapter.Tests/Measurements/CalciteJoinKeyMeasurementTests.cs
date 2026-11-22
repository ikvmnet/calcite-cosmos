using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

using FluentAssertions;

using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Which keys Calcite's own plan matches when it joins two in-process tables on one equality, which the lookup join's
    /// in-process match follows (<c>CosmosLookup.Normalize</c>).
    /// </summary>
    /// <remarks>
    /// No service: two tables whose column holds what the adapter's reader makes of each value, joined through the
    /// connection, <c>ON a.v = b.v</c> and, beside it, <c>ON a.v &lt;= b.v AND a.v &gt;= b.v</c>, which Calcite cannot plan
    /// as an equi-join and so evaluates as a comparison; and <c>=</c> itself, projected for every pair of a cross join, so
    /// that no join condition holds it. A pair is the two rows' keys, left then right.
    /// </remarks>
    public class CalciteJoinKeyMeasurementTests
    {

        const string Equi = "a.\"v\" = b.\"v\"";

        const string Compared = "a.\"v\" <= b.\"v\" AND a.\"v\" >= b.\"v\"";

        /// <summary>
        /// Calcite plans the equi-join as a merge join over its sorted keys, the comparison as a join that evaluates it, and
        /// <c>=</c> projected over a cross join as a nested-loop join under a calc. The comparison and <c>=</c> match the
        /// same pairs in every case: <c>BIGINT</c> 2^53 and 2^53 + 1 apart and <c>DOUBLE</c> 2^53 and 2^53 + 1 together,
        /// one double; <c>VARCHAR</c> by its units and <c>BOOLEAN</c> as itself; across types an <c>INTEGER</c> 7 with a
        /// <c>BIGINT</c> 7 and a <c>BIGINT</c> 2^53 + 1 with a <c>DOUBLE</c> 2^53, Calcite casting the narrower key to the
        /// join's comparison type; and <c>DOUBLE</c> and <c>REAL</c> −0.0 with 0.0 and <c>DECIMAL</c> 1.0 with 1.00,
        /// every pair of each. The merge join never matches −0.0 with 0.0, alone or beside one; it matches a lone −0.0 with
        /// a lone −0.0, and with −0.0 and 0.0 on each side matches −0.0 with neither; and of 1.0 and 1.00 it matches one
        /// pair each, along its sort's diagonal.
        /// </summary>
        [Fact, CosmosPins("Calcite.AnEquiJoinMatchesKeysAsEquality")]
        public void AnEquiJoinMatchesKeysAsEquality()
        {
            using var connection = MaterialisedTable.Open();

            // One double holds 2^53 and 2^53 + 1, and a BIGINT holds each apart.
            var bigint = Join(connection, SqlTypeName.BIGINT, ["9007199254740992", "9007199254740993"], SqlTypeName.BIGINT, ["9007199254740993", "9007199254740992"]);
            bigint.Equi.Should().Equal("(0,1)", "(1,0)");
            bigint.Compared.Should().Equal("(0,1)", "(1,0)");
            bigint.EquiPlan.Should().Contain("ClrCursorMergeJoin(condition=[=($1, $3)]");

            var dbl = Join(connection, SqlTypeName.DOUBLE, ["9007199254740992", "9007199254740993"], SqlTypeName.DOUBLE, ["9007199254740993", "9007199254740992"]);
            dbl.Equi.Should().Equal("(0,0)", "(0,1)", "(1,0)", "(1,1)");
            dbl.Compared.Should().Equal("(0,0)", "(0,1)", "(1,0)", "(1,1)");

            // A key of each type, the narrower cast to the join's comparison type: beneath the merge join, whose equality's
            // operands are then both of that type, and in the comparison itself.
            var widened = Join(connection, SqlTypeName.INTEGER, ["7", "8"], SqlTypeName.BIGINT, ["7", "9"]);
            widened.Equi.Should().Equal("(0,0)");
            widened.Compared.Should().Equal("(0,0)");
            widened.EquiPlan.Should().Contain("ClrCursorMergeJoin(condition=[=($1, $3)]").And.Contain("CAST($t2):BIGINT");
            widened.ComparedPlan.Should().Contain("CAST($1):BIGINT");

            var approximate = Join(connection, SqlTypeName.BIGINT, ["9007199254740993"], SqlTypeName.DOUBLE, ["9007199254740992"]);
            approximate.Equi.Should().Equal("(0,0)");
            approximate.Compared.Should().Equal("(0,0)");
            approximate.EquiPlan.Should().Contain("ClrCursorMergeJoin(condition=[=($1, $3)]").And.Contain("CAST($t2):DOUBLE");
            approximate.ComparedPlan.Should().Contain("CAST($1):DOUBLE");

            // Text by its units, a trailing space and all, and a boolean as itself.
            var text = Join(connection, SqlTypeName.VARCHAR, ["\"a\"", "\"a \""], SqlTypeName.VARCHAR, ["\"a \"", "\"a\""]);
            text.Equi.Should().Equal("(0,1)", "(1,0)");
            text.Compared.Should().Equal("(0,1)", "(1,0)");

            var flag = Join(connection, SqlTypeName.BOOLEAN, ["true", "false"], SqlTypeName.BOOLEAN, ["false", "true"]);
            flag.Equi.Should().Equal("(0,1)", "(1,0)");
            flag.Compared.Should().Equal("(0,1)", "(1,0)");

            // Where the merge join parts from =: −0.0 and 0.0 on each side, where it matches −0.0 with neither, −0.0 alone
            // against 0.0, and one DECIMAL value written to two scales. = itself, evaluated on each pair, matches every pair
            // of each.
            var zero = Join(connection, SqlTypeName.DOUBLE, ["-0.0", "0.0", "1.5"], SqlTypeName.DOUBLE, ["0.0", "-0.0", "1.5"]);
            zero.Equi.Should().Equal("(1,0)", "(2,2)");
            zero.Compared.Should().Equal("(0,0)", "(0,1)", "(1,0)", "(1,1)", "(2,2)");
            zero.EquiPlan.Should().Contain("ClrCursorMergeJoin");

            var lone = Join(connection, SqlTypeName.DOUBLE, ["-0.0"], SqlTypeName.DOUBLE, ["0.0"]);
            lone.Equi.Should().BeEmpty();
            lone.Compared.Should().Equal("(0,0)");

            // A lone −0.0 is matched with a lone −0.0, which with 0.0 beside it on each side, above, it is not.
            var itself = Join(connection, SqlTypeName.DOUBLE, ["-0.0"], SqlTypeName.DOUBLE, ["-0.0"]);
            itself.Equi.Should().Equal("(0,0)");
            itself.Compared.Should().Equal("(0,0)");

            var real = Join(connection, SqlTypeName.REAL, ["-0.0", "0.0", "1.5"], SqlTypeName.REAL, ["0.0", "-0.0", "1.5"]);
            real.Equi.Should().Equal("(1,0)", "(2,2)");
            real.Compared.Should().Equal("(0,0)", "(0,1)", "(1,0)", "(1,1)", "(2,2)");

            var realLone = Join(connection, SqlTypeName.REAL, ["-0.0"], SqlTypeName.REAL, ["0.0"]);
            realLone.Equi.Should().BeEmpty();
            realLone.Compared.Should().Equal("(0,0)");

            var realItself = Join(connection, SqlTypeName.REAL, ["-0.0"], SqlTypeName.REAL, ["-0.0"]);
            realItself.Equi.Should().Equal("(0,0)");
            realItself.Compared.Should().Equal("(0,0)");

            var scaled = Join(connection, SqlTypeName.DECIMAL, ["1.0", "1.00", "2"], SqlTypeName.DECIMAL, ["1.00", "1.0", "2.0"]);
            scaled.Equi.Should().Equal("(0,0)", "(1,1)", "(2,2)");
            scaled.Compared.Should().Equal("(0,0)", "(0,1)", "(1,0)", "(1,1)", "(2,2)");
            scaled.EquiPlan.Should().Contain("ClrCursorMergeJoin");

            foreach (var measured in new[] { zero, lone, itself, real, realLone, realItself, scaled })
                measured.EqualPlan.Should().Contain("=($t1, $t3)");

            // The comparison is never planned as an equi-join: it evaluates <= and >= on each pair it is offered. = is
            // evaluated on every pair of the cross join, and is true on exactly the pairs the comparison matches.
            foreach (var measured in new[] { bigint, dbl, widened, approximate, text, flag, zero, lone, itself, real, realLone, realItself, scaled })
            {
                measured.EquiPlan.Should().Contain("ClrCursorMergeJoin");
                measured.ComparedPlan.Should().NotContain("MergeJoin").And.Contain("AND(<=(");
                measured.EqualPlan.Should().Contain("ClrCursorNestedLoopJoin(condition=[true]").And.NotContain("MergeJoin");
                measured.Equal.Should().Equal(measured.Compared);
            }
        }

        /// <summary>
        /// The pairs each join matches, and the pairs on which <c>=</c> is true, in order, and the plan Calcite chose for
        /// each.
        /// </summary>
        sealed record Measured(string[] Equi, string[] Compared, string[] Equal, string EquiPlan, string ComparedPlan, string EqualPlan);

        static Measured Join(DbConnection connection, SqlTypeName leftType, string?[] left, SqlTypeName rightType, string?[] right)
        {
            var a = MaterialisedTable.Of(leftType, left).AddTo(connection);
            var b = MaterialisedTable.Of(rightType, right).AddTo(connection);

            string Sql(string on) => $"SELECT a.\"k\", b.\"k\" FROM \"{a}\" a JOIN \"{b}\" b ON {on}";

            var equal = $"SELECT a.\"k\", b.\"k\", a.\"v\" = b.\"v\" FROM \"{a}\" a CROSS JOIN \"{b}\" b";

            return new Measured(
                Pairs(connection, Sql(Equi)), Pairs(connection, Sql(Compared)), Truths(connection, equal),
                Plan(connection, Sql(Equi)), Plan(connection, Sql(Compared)), Plan(connection, equal));
        }

        /// <summary>The pairs on which a statement's third column is true, failing on any it holds unknown.</summary>
        static string[] Truths(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var pairs = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                reader.IsDBNull(2).Should().BeFalse("no key is null, so = is never unknown");
                if (reader.GetBoolean(2))
                    pairs.Add(string.Create(CultureInfo.InvariantCulture, $"({reader.GetValue(0)},{reader.GetValue(1)})"));
            }

            return [.. pairs.Order(StringComparer.Ordinal)];
        }

        static string[] Pairs(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var pairs = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                pairs.Add(string.Create(CultureInfo.InvariantCulture, $"({reader.GetValue(0)},{reader.GetValue(1)})"));

            return [.. pairs.Order(StringComparer.Ordinal)];
        }

        static string Plan(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql;

            var lines = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                lines.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);

            return string.Join("\n", lines);
        }

    }

}
