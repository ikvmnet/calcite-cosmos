using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Data;

using FluentAssertions;

using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite answers null for where an operand, or the path a reading reads, is null: the two rows the rendering
    /// graph's strictness reads (<c>Metadata.CosmosStrictness</c>), each name a row holds measured here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rows' names are the measurement's input.</b> Each test reads its row's subjects and fails on one it has no
    /// witness for, so a name added to a row is measured or refused, never trusted.
    /// </para>
    /// <para>
    /// No service: an in-process table whose columns hold what the adapter's reader makes of each value
    /// (<see cref="MaterialisedTable"/>), read in a connection with every library's functions. Each witness is two rows,
    /// the first null or absent and the second holding a value the expression answers for, so that a null answered on the
    /// first row is the null's and not the expression's.
    /// </para>
    /// </remarks>
    public class CalciteNullStrictnessMeasurementTests
    {

        static CalciteConnection Open()
        {
            var connection = new CalciteConnection(new CalciteConnectionStringBuilder { Fun = "all" }.ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Every form of each listed function, its operands by kind: <c>s</c> a string, <c>n</c> an integer, <c>x</c> a
        /// double.
        /// </summary>
        static readonly Dictionary<string, (string Form, string Kinds)[]> Forms = new()
        {
            ["||"] = [("{0} || {1}", "ss")],
            ["UPPER"] = [("UPPER({0})", "s")],
            ["LOWER"] = [("LOWER({0})", "s")],
            ["INITCAP"] = [("INITCAP({0})", "s")],
            ["SUBSTRING"] = [("SUBSTRING({0} FROM {1})", "sn"), ("SUBSTRING({0} FROM {1} FOR {2})", "snn")],
            ["CHAR_LENGTH"] = [("CHAR_LENGTH({0})", "s")],
            ["CHARACTER_LENGTH"] = [("CHARACTER_LENGTH({0})", "s")],
            ["POSITION"] = [("POSITION({0} IN {1})", "ss"), ("POSITION({0} IN {1} FROM {2})", "ssn")],
            ["REPLACE"] = [("REPLACE({0}, {1}, {2})", "sss")],
            ["LEFT"] = [("LEFT({0}, {1})", "sn")],
            ["RIGHT"] = [("RIGHT({0}, {1})", "sn")],
            ["REVERSE"] = [("REVERSE({0})", "s")],
            ["REPEAT"] = [("REPEAT({0}, {1})", "sn")],
            ["MOD"] = [("MOD({0}, {1})", "nn")],
            ["ABS"] = [("ABS({0})", "n"), ("ABS({0})", "x")],
            ["ROUND"] = [("ROUND({0})", "x"), ("ROUND({0}, {1})", "xn")],
            ["POWER"] = [("POWER({0}, {1})", "xx")],
            ["SAFE_CAST"] = [("SAFE_CAST({0} AS INTEGER)", "s"), ("SAFE_CAST({0} AS DOUBLE)", "n")],
            ["TRY_CAST"] = [("TRY_CAST({0} AS INTEGER)", "s"), ("TRY_CAST({0} AS DOUBLE)", "n")],
        };

        /// <summary>
        /// Each listed function is null wherever an operand is: in every form, each operand alone read from a column that
        /// is null on the first row and holds the constant's value on the second, the others the constants.
        /// </summary>
        [Fact, CosmosPins("Calcite.ListedFunctionsAreNullOverANullOperand")]
        public void EachListedFunctionIsNullOverANullOperand()
        {
            var names = CosmosAxioms.Get("Calcite.ListedFunctionsAreNullOverANullOperand").Subjects!;
            names.Where(n => Forms.ContainsKey(n) == false).Should().BeEmpty("every name the row holds is measured");

            var table = new MaterialisedTable([("s", SqlTypeName.VARCHAR), ("n", SqlTypeName.INTEGER), ("x", SqlTypeName.DOUBLE)],
                [("{}", [null, null, null]), ("{}", ["\"12\"", "2", "2.5"])]);
            var (columns, constants) = (new Dictionary<char, string> { ['s'] = "\"s\"", ['n'] = "\"n\"", ['x'] = "\"x\"" }, new Dictionary<char, string> { ['s'] = "'12'", ['n'] = "2", ['x'] = "2.5" });
            var asked = (from name in names
                         from form in Forms[name]
                         from i in Enumerable.Range(0, form.Kinds.Length)
                         select string.Format(form.Form, form.Kinds.Select((k, j) => j == i ? columns[k] : constants[k]).ToArray<object>())).ToList();

            using var connection = Open();
            var answers = table.Ask(connection, [.. asked.Select(e => e + " IS NULL")]);
            asked.Where((e, i) => answers[i] != "TF").Should().BeEmpty("each is null where its one operand is, and answers where that holds a value");
        }

        /// <summary>
        /// Every reading the loader builds is null over an absent path: the accessor read through a document without it, a
        /// column through the value the reader makes of nothing. With the path present each answers or raises, a
        /// <c>RETURNING</c> asserting its type (<c>Calcite.ReturningAsserts</c>), and neither is a null.
        /// </summary>
        [Fact, CosmosPins("Calcite.ReadingsAreNullOverAnAbsentPath")]
        public void EveryReadingIsNullOverAnAbsentPath()
        {
            var readers = CosmosAxioms.Get("Calcite.ReadingsAreNullOverAnAbsentPath").Subjects!;
            static Func<string?, MaterialisedTable> Document() => value => MaterialisedTable.Of([value is null ? "{}" : "{\"p\":" + value + "}"]);
            static Func<string?, MaterialisedTable> Column(SqlTypeName type) => value => MaterialisedTable.Of(type, [value]);
            var raw = new[] { SqlTypeName.VARIANT, SqlTypeName.ANY, SqlTypeName.VARCHAR };
            static string Holding(SqlTypeName type, string value) => type == SqlTypeName.VARCHAR ? "\"" + value + "\"" : value;

            IEnumerable<(Func<string?, MaterialisedTable> Table, string Value, string Expression)> Witnesses(string reader) => reader.Split(':') switch
            {
                ["text"] => [(Document(), "\"a\"", "JSON_VALUE(\"d\", '$.p')")],
                ["raw"] => raw.Select(t => (Column(t), Holding(t, "\"a\""), "\"v\"")),
                ["typed", var type] => [(Document(), type == "BOOLEAN" ? "true" : "7", $"JSON_VALUE(\"d\", '$.p' RETURNING {type})"),
                    (Column(SqlTypeName.valueOf(type)), type == "BOOLEAN" ? "true" : "7", "\"v\"")],
                ["cast", var target, "text"] => [(Document(), "7", $"CAST(JSON_VALUE(\"d\", '$.p') AS {target})")],
                ["cast", var target, "raw"] => raw.Select(t => (Column(t), Holding(t, target == "VARCHAR" ? "\"a\"" : "7"), $"CAST(\"v\" AS {target})")),
                _ => [],
            };

            readers.Where(r => Witnesses(r).Any() == false).Should().BeEmpty("every reading the row holds is measured");

            using var connection = Open();
            var asked = (from reader in readers from w in Witnesses(reader) select (reader, w.Table, w.Value, Expression: w.Expression + " IS NULL")).ToList();
            asked.Where(a => a.Table(null).Ask(connection, a.Expression)[0] != "T").Select(a => $"{a.reader}: {a.Expression}").Should().BeEmpty("null over an absent path");
            asked.Where(a => a.Table(a.Value).Failure(connection, a.Expression) is null && a.Table(a.Value).Ask(connection, a.Expression)[0] != "F")
                .Select(a => $"{a.reader}: {a.Expression}").Should().BeEmpty("present, it answers or raises");
            asked.Should().HaveCount(37, "the accessor, three raw columns, each type twice, three casts of the accessor and twelve of the raw value");
        }

    }

}
