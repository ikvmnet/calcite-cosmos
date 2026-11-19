using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's traversal of an array yields in process: <c>UNNEST</c> of a plain <c>JSON_QUERY … RETURNING VARCHAR
    /// ARRAY</c>, the element a condition over a correlate reads.
    /// </summary>
    /// <remarks>
    /// In process, over <see cref="MaterialisedTable"/>'s document column, as <see cref="CalciteItemMeasurementTests"/> measures
    /// an index: no service call, the traversal Calcite's own.
    /// </remarks>
    public class CalciteUnnestMeasurementTests
    {

        /// <summary>
        /// The values an element holds: strings a cast, a trim or a pattern reads apart, a number beyond an int, an empty one,
        /// control characters, a boolean's spellings, a non-ASCII character and a surrogate pair; and a null.
        /// </summary>
        static readonly string[] Values =
        [
            "\"1\"", "\" 1\"", "\"1 \"", "\"+1\"", "\"-0\"", "\"01\"", "\"1.0\"", "\"1e0\"", "\"2147483648\"", "\"x\"", "\"\"", "\"\\t7\\n\"", "\"true\"",
            "\" TRUE \"", "\"abc\"", "\"\\u00e9\"", "\"\\ud83d\\ude00\"", "null",
        ];

        /// <summary>Each reading measured, over a value written <c>{0}</c>: the value itself, each test and each call.</summary>
        static readonly string[] Readings =
        [
            "{0}", "{0} = '1'", "{0} <> 'x'", "{0} < 'b'", "{0} IN ('1', 'x')", "{0} IS NULL", "{0} LIKE '1%'", "{0} LIKE '_'", "CAST({0} AS INTEGER)",
            "CAST({0} AS BIGINT)", "CAST({0} AS DOUBLE)", "CAST({0} AS BOOLEAN)", "CHAR_LENGTH({0})", "UPPER({0})", "TRIM({0})", "{0} || 'a'",
        ];

        /// <summary>The element types measured: <c>VARCHAR</c>, and <c>VARCHAR(1)</c>, which yields the same values uncut.</summary>
        static readonly string[] Types = ["VARCHAR", "VARCHAR(1)"];

        /// <summary>The element as a statement reads it.</summary>
        const string Element = "\"u\".\"t\"";

        /// <summary>The text accessor of a path holding the same value.</summary>
        const string Text = "JSON_VALUE(\"d\", '$.s')";

        /// <summary>
        /// A traversal of a <c>VARCHAR</c> array of any length yields each string and null its array holds, uncut, and each
        /// reading of it, a test, a cast or a call, answers what the same reading of the text accessor answers over a path
        /// holding the same value: the value, or a raise of the same type. Over an array holding a number, a fraction, a
        /// boolean, an object or an array anywhere in it, and over an object where the array is, the traversal itself raises,
        /// so <c>COUNT(*)</c>, which reads no element, raises too; over an empty, absent or null array, and over a scalar where
        /// the array is, it yields no row.
        /// </summary>
        [Fact, CosmosPins("Calcite.AVarcharArrayYieldsItsStringsAndNullsAsTheText")]
        public void AVarcharArrayYieldsItsStringsAndNullsAsTheText()
        {
            using var connection = MaterialisedTable.Open();
            var elements = MaterialisedTable.Of(Values.Select(v => $"{{\"tags\":[{v}]}}"));
            var paths = MaterialisedTable.Of(Values.Select(v => $"{{\"s\":{v}}}"));

            foreach (var type in Types)
            {
                // Each document's one element is one row.
                Run(connection, elements, "\"k\"", type, " ORDER BY \"k\"").Should().Equal(Values.Select((_, k) => (object)k), type);

                var read = new Dictionary<string, object?[]>();
                foreach (var reading in Readings)
                {
                    var a = read[reading] = elements.Values(connection, string.Format(reading, Element), Traversal(type));
                    var b = paths.Values(connection, string.Format(reading, Text));
                    Same(a, b).Should().BeTrue($"{string.Format(reading, "u.t")} over RETURNING {type} ARRAY answers {Shown(a)} against the text's {Shown(b)}");
                }

                // A cast raises over a string it cannot parse, so a raise is among what is measured; and no element is cut.
                read["CAST({0} AS INTEGER)"][Array.IndexOf(Values, "\"x\"")].Should().BeOfType<java.lang.NumberFormatException>(type);
                read["CHAR_LENGTH({0})"][Array.IndexOf(Values, "\"abc\"")].Should().Be(3, $"RETURNING {type} ARRAY cuts no element");
            }

            foreach (var type in Types)
            {
                Run(connection, Document("{\"tags\":[\"1\",null,\"x\"]}"), Element, type).Should().Equal(["1", null, "x"], type);

                foreach (var raising in new[] { "[1]", "[2.5]", "[true]", "[{\"a\":1}]", "[[1]]", "[\"1\",2]", "[2,\"1\"]", "{\"a\":\"1\"}" })
                {
                    var table = Document($"{{\"tags\":{raising}}}");
                    Raised(() => Run(connection, table, "COUNT(*)", type)).Should().BeOfType<InvalidCastException>($"COUNT(*) over {raising} as {type}");
                    Raised(() => Run(connection, table, Element, type)).Should().BeOfType<InvalidCastException>($"u.t over {raising} as {type}");
                }

                foreach (var none in new[] { "{\"tags\":[]}", "{}", "{\"tags\":null}", "{\"tags\":\"ab\"}", "{\"tags\":30}", "{\"tags\":true}" })
                {
                    Run(connection, Document(none), "COUNT(*)", type).Should().Equal([0L], $"COUNT(*) over {none} as {type}");
                    Run(connection, Document(none), Element, type).Should().BeEmpty($"u.t over {none} as {type}");
                }
            }
        }

        /// <summary>The <c>FROM</c> clause after the table: the table as <c>x</c>, and the traversal of its <c>tags</c> as <c>u(t)</c>.</summary>
        static string Traversal(string type) => $" AS \"x\", UNNEST(JSON_QUERY(\"x\".\"d\", '$.tags' RETURNING {type} ARRAY)) AS \"u\"(\"t\")";

        static MaterialisedTable Document(string document) => MaterialisedTable.Of([document]);

        /// <summary>
        /// What a projection is on each row a traversal of a table's documents joins, each a value or <c>null</c>, the rest of
        /// the statement following; a statement that fails throws.
        /// </summary>
        static List<object?> Run(DbConnection connection, MaterialisedTable table, string projection, string type, string rest = "")
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {projection} FROM \"{table.AddTo(connection)}\"{Traversal(type)}{rest}";
            var values = new List<object?>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));

            return values;
        }

        /// <summary>The innermost exception an action raises, or <c>null</c> where it raises none.</summary>
        static Exception? Raised(Action action)
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

        /// <summary>
        /// Whether two rows of values are the same: each value equal and of one type, or a raise of one type. A row that raises
        /// on every value measures nothing, so it is never the same.
        /// </summary>
        static bool Same(object?[] a, object?[] b) =>
            a.Length == b.Length && a.Any(v => v is not Exception) && a.Zip(b).All(p => p.First is Exception x ? p.Second is Exception y && x.GetType() == y.GetType() : p.First?.GetType() == p.Second?.GetType() && Equals(p.First, p.Second));

        static string Shown(object?[] values) => string.Join(",", values.Select(v => v is Exception e ? "!" + e.GetType().Name : v?.ToString() ?? "null"));

    }

}
