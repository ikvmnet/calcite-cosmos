using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite answers comparing each reading the facts graph reads with a parameter, bound through the driver as a
    /// caller binds one, over a stored value of every class: the premise the raise evaluator reads a parameter by, that
    /// Calcite types a parameter as the value it is compared with (<c>PredicateAtom.ParameterOn</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// No service: an in-process table whose columns hold what the adapter's reader makes of each value
    /// (<see cref="MaterialisedTable"/>), asked one statement a reading, an operator and a bound value.
    /// </para>
    /// <para>
    /// <b>Three outcomes are a statement's, not a row's.</b> A statement that fails over an empty table fails before it
    /// reads a row. Where it fails so with the parameter bound to null too, it fails to compile, whatever is bound: a
    /// raise over every row, an absent path's included. Where it answers with null bound, the driver refused the value
    /// bound, before the statement ran and whatever was planned for it (<c>ParameterBinder.Bind</c>, against the type the
    /// validator gave the parameter): the parameter is never bound to a value of that class, so the class is not one it
    /// may be bound to, and is left out.
    /// </para>
    /// </remarks>
    public class CalciteParameterMeasurementTests
    {

        const Truths T = Truths.True;
        const Truths F = Truths.False;
        const Truths U = Truths.Unknown;
        const Truths R = Truths.Raises;

        /// <summary>A stored value of every class, by its class, a JSON text, <c>null</c> for an absent path; a string of digits among them.</summary>
        static readonly (CosmosValueClass Class, string? Json)[] Stored =
        [
            (CosmosValueClass.String, "\"x\""), (CosmosValueClass.String, "\"30\""), (CosmosValueClass.Number, "30"), (CosmosValueClass.Number, "2.5"),
            (CosmosValueClass.True, "true"), (CosmosValueClass.False, "false"), (CosmosValueClass.Object, "{\"a\":1}"), (CosmosValueClass.Array, "[1]"),
            (CosmosValueClass.Null, "null"), (CosmosValueClass.Absent, null),
        ];

        /// <summary>A value of every class a caller binds, as the driver takes one: strings, an integer, a fraction, the booleans, a null.</summary>
        static readonly object?[] Bound = ["x", "y", "30", 30L, 2.5d, true, false, null];

        /// <summary>Every operator the facts graph reads a comparison by.</summary>
        static readonly string[] Ops = [PredicateAtom.Equal, PredicateAtom.NotEqual, PredicateAtom.Less, PredicateAtom.LessOrEqual, PredicateAtom.Greater, PredicateAtom.GreaterOrEqual];

        /// <summary>The scalar types the loader reads a column and a <c>RETURNING</c> as, each <c>typed</c>.</summary>
        static readonly SqlTypeName[] Scalars =
        [
            SqlTypeName.TINYINT, SqlTypeName.SMALLINT, SqlTypeName.INTEGER, SqlTypeName.BIGINT, SqlTypeName.FLOAT, SqlTypeName.REAL, SqlTypeName.DOUBLE,
            SqlTypeName.DECIMAL, SqlTypeName.BOOLEAN,
        ];

        /// <summary>The columns a raw value is read through, the one no literal's row measured among them.</summary>
        static readonly (string Column, SqlTypeName Type)[] RawColumns =
            [("VARIANT", SqlTypeName.VARIANT), ("ANY", SqlTypeName.ANY), ("VARCHAR", SqlTypeName.VARCHAR), ("CHAR", SqlTypeName.CHAR)];

        /// <summary>One reading as Calcite is asked it: its reader, the table its values are read from, and the expression read.</summary>
        sealed record Asked(Func<IEnumerable<string?>, MaterialisedTable> Table, string Expression);

        /// <summary>What one comparison answers: each stored value's truths over every value bound, and the values bound the driver refused.</summary>
        sealed record Answered(Truths[] Truths, IReadOnlyList<object> Refused, bool Compiles);

        /// <summary>
        /// Each reading compared with a parameter answers as its comparand's type compares the value bound: a raw value as
        /// its column, and the typed, cast and text readings as their type; the raw columns' parameter keys hold every
        /// raise the measurement makes and no other, and every truth value; and the facts graph's answer to a parameter
        /// holds every answer of every reading.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A raw value.</b> A <c>VARIANT</c>'s parameter is a <c>VARIANT</c>, which the driver writes from no .NET value:
        /// it binds null alone, and refuses a string, a number and a boolean. Its ordering fails to compile, whatever is
        /// bound, as against a literal. An <c>ANY</c>'s parameter holds the value bound as it is, unequal across types and
        /// to a string of its digits, and its ordering raises across types and over a structure. A <c>VARCHAR</c> column's
        /// parameter is a <c>VARCHAR</c>: the driver binds a number or a boolean as its text, so 30 is equal to a stored
        /// <c>"30"</c>. A <c>CHAR</c> column's is the column's type, here <c>CHAR(1)</c>, which cuts the text bound to one
        /// character. Each fails the statement over any stored value but a string, null bound or not.
        /// </para>
        /// <para>
        /// <b>The cast of a raw value to text</b> compares the text it casts to with the parameter, typed <c>VARCHAR</c>:
        /// through a <c>VARIANT</c> a number and a boolean are null and a structure raises, through an <c>ANY</c> a scalar
        /// renders, through a <c>VARCHAR</c> or <c>CHAR</c> column anything but a string raises.
        /// </para>
        /// <para>
        /// <b>A typed reading, a cast of the text and the text</b> type the parameter as theirs: the driver refuses a
        /// string bound to a number that it does not parse as one, and converts the rest. Each raises where it raises
        /// against a literal.
        /// </para>
        /// <para>
        /// The keys are the readings rules' join over every class a column is compared beside, and the driver binds fewer:
        /// no structure, and to a <c>VARIANT</c> only null. So a key holds every truth value measured, and some no caller
        /// can reach, which nothing reads: the evaluator reads a key's raise alone, and the raises are exactly the
        /// measurement's on every column. A <c>CHAR</c> column, which no literal's row measured, answers a parameter as a
        /// <c>VARCHAR</c> column does (<c>parameteras</c>), as Calcite answers it class by class.
        /// </para>
        /// </remarks>
        [Fact, CosmosPins("Calcite.AParameterComparesAsItsComparand")]
        public void AParameterComparesAsItsComparand()
        {
            using var connection = MaterialisedTable.Open();
            var columns = FactsReadings.Columns;
            var readings = FactsReadings.Rows;
            var lacking = new List<string>();
            var extra = new List<string>();
            var output = TestContext.Current.TestOutputHelper;

            // The parameter's type, as the driver binds it: a VARIANT takes no .NET value, an ANY the value as it is, a
            // VARCHAR column the value's text, a number no string.
            var variant = Measure(connection, Raw(SqlTypeName.VARIANT), PredicateAtom.Equal);
            variant.Refused.Should().Equal(Bound.OfType<object>(), "a VARIANT is written from no .NET value");
            variant.Truths.Should().AllSatisfy(t => t.Should().Be(U), "a VARIANT bound to null alone");
            Measure(connection, Raw(SqlTypeName.VARIANT), PredicateAtom.Less).Compiles.Should().BeFalse("a VARIANT has no order at run time");
            Ask(connection, Raw(SqlTypeName.ANY), PredicateAtom.Equal, 30L).Should().Be("FFTFFFFFUU", "an ANY's parameter holds the integer bound, unequal to its digits");
            Ask(connection, Raw(SqlTypeName.ANY), PredicateAtom.Equal, "30").Should().Be("FTFFFFFFUU", "an ANY's parameter holds the string bound, unequal to the number");
            Ask(connection, Raw(SqlTypeName.ANY), PredicateAtom.Less, 30L).Should().Be("RRFTRRRRUU", "an ANY's ordering raises across types and over a structure");
            Ask(connection, Raw(SqlTypeName.VARCHAR), PredicateAtom.Equal, 30L).Should().Be("FTRRRRRRUU", "a VARCHAR column's parameter is the integer's text");
            Ask(connection, Raw(SqlTypeName.CHAR), PredicateAtom.Equal, 30L).Should().Be("FFRRRRRRUU", "a CHAR column's parameter is the column's CHAR(1), its text cut to one character");
            Measure(connection, Typed("INTEGER"), PredicateAtom.Equal).Refused.Should().Equal(["x", "y"], "a number's parameter takes no string that is no number");

            // A raw value through each column, and through their union, as the columnless reader reads it.
            var union = new Truths[Stored.Length * Ops.Length];
            foreach (var (column, type) in RawColumns)
                foreach (var (op, o) in Ops.Select((op, o) => (op, o)))
                {
                    var answered = Measure(connection, Raw(type), op);
                    for (var s = 0; s < Stored.Length; s++)
                        union[o * Stored.Length + s] |= answered.Truths[s];

                    Check(PredicateAtom.RawIn(column), op, answered.Truths, exact: true);
                }

            foreach (var (op, o) in Ops.Select((op, o) => (op, o)))
                Check(PredicateAtom.RawReader, op, union[(o * Stored.Length)..((o + 1) * Stored.Length)], exact: false);

            // The cast of a raw value to text through each column.
            foreach (var (column, type) in RawColumns)
                foreach (var op in Ops)
                    Check(PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawIn(column)), op, Measure(connection, Cast(type), op).Truths, exact: true);

            // A typed reading, the union of RETURNING and the column the reader fills, as the loader reads both.
            foreach (var type in Scalars)
                foreach (var op in Ops)
                {
                    var returning = Measure(connection, Typed(type.getName()), op).Truths;
                    var column = Measure(connection, Column(type), op).Truths;
                    Check(PredicateAtom.TypedReader(type.getName()), op, [.. returning.Zip(column, (a, b) => a | b)], exact: false);
                }

            // A cast of the text to each type the loader reads one as, and the text.
            foreach (var target in new[] { "INTEGER", "BIGINT", "DOUBLE", "BOOLEAN", "VARCHAR", "CHAR" })
                foreach (var op in Ops)
                    Check(PredicateAtom.CastReader(target, PredicateAtom.TextReader), op, Measure(connection, TextCast(target), op).Truths, exact: false);

            foreach (var op in Ops)
                Check(PredicateAtom.TextReader, op, Measure(connection, Text(), op).Truths, exact: false);

            foreach (var line in extra)
                output?.WriteLine("more than measured: " + line);

            lacking.Should().BeEmpty("the facts graph's answer to a parameter holds every answer Calcite gives");

            // Measured truths against the facts graph's answer to a parameter on each stored value's cell, and for a raw
            // reader against its column's key beside a parameter; exact: a column's, whose key's raises are the
            // measurement's, a CHAR's read as a VARCHAR's.
            void Check(string reader, string op, Truths[] measured, bool exact)
            {
                var atom = PredicateAtom.CompareParameter(reader, op);
                for (var s = 0; s < Stored.Length; s++)
                {
                    var cell = CellOf(Stored[s].Json);
                    var answer = atom.ParameterOn(cell, columns, readings);
                    var at = $"{reader} {op} ? over {Stored[s].Json ?? "absent"}: measured {measured[s]}, ";
                    if ((measured[s] & ~answer) != Truths.None)
                        lacking.Add(at + "answered " + answer);
                    else if ((answer & R) != 0 && (measured[s] & R) == 0)
                        extra.Add(at + "answered " + answer);

                    if (PredicateAtom.IsRaw(reader) || PredicateAtom.Columnless(reader) == PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader))
                    {
                        var key = columns[RawKey.Of(reader, Stored[s].Class, TestOf(op), CosmosValueClass.None)];
                        var held = key.Whole | key.Compared;
                        if ((measured[s] & ~U & ~held) != Truths.None || (measured[s] & R) != 0 && (held & R) == 0)
                            lacking.Add(at + "key " + key);
                        else if (exact && (held & R) != 0 && (measured[s] & R) == 0)
                            lacking.Add(at + "key " + key + ", a raise Calcite never makes");
                    }
                }
            }
        }

        /// <summary>
        /// Calcite's own JDBC driver hands the caller's class to the data context, and Calcite converts the value to the
        /// parameter's type when a row reads it; the ADO.NET driver converts it to the type before Calcite sees it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each case is a statement over an in-process table that records what the data context holds for <c>?0</c> when
        /// it is scanned (<see cref="Recording"/>), bound once through <c>jdbc:calcite:</c> with the setter a caller of it
        /// would use, and once through the ADO.NET driver with the .NET value a caller of it would use; with the rows kept,
        /// or the innermost exception.
        /// </para>
        /// <para>
        /// <b>Through Calcite's own driver the data context holds the caller's class:</b> a long for a <c>DOUBLE</c>, an
        /// <c>INTEGER</c>, a <c>SMALLINT</c>, a <c>TINYINT</c>, a <c>REAL</c>, a <c>VARCHAR</c>, a <c>CHAR</c> and a
        /// <c>DATE</c> parameter, a double for a <c>BIGINT</c> one, a string for a <c>BIGINT</c>, a <c>DECIMAL</c>, an
        /// <c>INTEGER</c>, a <c>BOOLEAN</c> and a <c>UUID</c> one, a <c>java.util.UUID</c> for a <c>UUID</c> one and an
        /// integer for a <c>TIMESTAMP</c> one. Calcite compares the value of the parameter's type, converted when the row's
        /// code reads it: the long 2^53 + 1 as the double 2^53, keeping the stored 2^53 and not 1.5 below it;
        /// 3,000,000,000 wrapped to −1,294,967,296; 65,537 wrapped to 1 as a <c>SMALLINT</c> and 257 as a <c>TINYINT</c>;
        /// 2^24 + 1 rounded to 2^24 as a <c>REAL</c>; 1.5 cut to 1; the string <c>'2'</c> as 2 and <c>'2.50'</c> as the
        /// decimal 2.5; the long 30 as the text <c>'30'</c>, the double 1e20 as Java writes it, <c>'1.0E20'</c>, and the
        /// long 17 as <c>'17'</c> held to a <c>CHAR(1)</c>, <c>'1'</c>. It fails converting what it cannot: a string that
        /// spells no number bound to a <c>BIGINT</c>, a NaN bound to a <c>BIGINT</c> or a <c>DOUBLE</c> and −Infinity
        /// bound to a <c>DOUBLE</c> (<c>NumberFormatException</c>); and, casting what the data context holds to the class
        /// it holds the type in, a string bound to a <c>BOOLEAN</c>, a string or a <c>java.util.UUID</c> bound to a
        /// <c>UUID</c>, an integer bound to a <c>TIMESTAMP</c> and a long bound to a <c>DATE</c>
        /// (<c>ClassCastException</c>). It fails only once a row reaches the parameter's reading: over an empty table it
        /// answers no rows, a NaN bound to a <c>BIGINT</c> and a string bound to a <c>BOOLEAN</c> alike, and where no
        /// row takes the <c>CASE</c> branch that reads the parameter it answers what the other branch decides, a string
        /// that spells no number bound to a <c>DOUBLE</c> or an <c>INTEGER</c>, a NaN bound to a <c>DOUBLE</c> and a
        /// string bound to a <c>BOOLEAN</c> alike, and projected a string bound to an <c>INTEGER</c> so too; a branch a
        /// row takes fails. A projected parameter is read so too.
        /// </para>
        /// <para>
        /// <b>Through the ADO.NET driver the data context holds the parameter's type's own class</b>, converted from the
        /// .NET value before the statement runs (<c>ParameterBinder.Bind</c>), by its own rule: it rounds 1.5 to 2 for a
        /// <c>BIGINT</c> where Calcite's conversion cuts it to 1, refuses a long past an <c>INTEGER</c>, a <c>SMALLINT</c>
        /// or a <c>TINYINT</c> rather than wrap it, refuses a NaN or a string that spells no number for a <c>BIGINT</c> and
        /// a string for a <c>UUID</c>, writes the double 1e20 as .NET's text, <c>'1E+20'</c>, and makes Calcite's
        /// <c>UuidValue</c> of a <see cref="Guid"/>, the epoch milliseconds of a <c>TIMESTAMP</c> of a
        /// <see cref="DateOnly"/> and the days of a <c>DATE</c> of a <see cref="DateTime"/>. Calcite's conversion then
        /// finds the type's class and changes nothing, so a value converts to what the data context already holds.
        /// </para>
        /// <para>
        /// A string the data context holds is recorded without a cast to <c>java.lang.Object</c>: IKVM holds Java's
        /// <c>String</c> as <c>System.String</c>, which no such cast takes.
        /// </para>
        /// </remarks>
        [Fact, CosmosPins("Calcite.AParameterIsConvertedToItsTypeWhenRead")]
        public void AParameterIsConvertedToItsTypeWhenRead()
        {
            var doubles = MaterialisedTable.Of(SqlTypeName.DOUBLE, ["9007199254740992", "1.5"]);
            var integers = MaterialisedTable.Of(SqlTypeName.INTEGER, ["-1294967296", "5"]);
            var longs = MaterialisedTable.Of(SqlTypeName.BIGINT, ["1", "2"]);
            var texts = MaterialisedTable.Of(SqlTypeName.VARCHAR, ["\"30\"", "\"x\"", "\"1.0E20\"", "\"1E+20\""]);
            var documents = MaterialisedTable.Of(["{\"s\":\"30\"}", "{\"s\":\"x\"}", "{\"n\":2}"]);
            var keys = MaterialisedTable.Of(SqlTypeName.INTEGER, ["1"]);
            const string Uuid = "123e4567-e89b-12d3-a456-426614174000";
            const string UuidEquals = "SELECT \"k\" FROM {t} WHERE CAST('" + Uuid + "' AS UUID) = ?";

            var cases = new Conversion[]
            {
                new("DOUBLE v = ?, long 2^53 + 1", doubles, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 9007199254740993L), 9007199254740993L,
                    "java.lang.Long 9007199254740993: rows [0]", "java.lang.Double 9.007199254740992E15: rows [0]"),
                new("DOUBLE v < ?, long 2^53 + 1", doubles, "SELECT \"k\" FROM {t} WHERE \"v\" < ?", s => s.setLong(1, 9007199254740993L), 9007199254740993L,
                    "java.lang.Long 9007199254740993: rows [1]", "java.lang.Double 9.007199254740992E15: rows [1]"),
                new("INTEGER v = ?, long 3,000,000,000", integers, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 3000000000L), 3000000000L,
                    "java.lang.Long 3000000000: rows [0]", "not read: OverflowException"),
                new("SMALLINT v = ?, long 65,537", MaterialisedTable.Of(SqlTypeName.SMALLINT, ["1", "2"]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 65537L), 65537L,
                    "java.lang.Long 65537: rows [0]", "not read: OverflowException"),
                new("TINYINT v = ?, long 257", MaterialisedTable.Of(SqlTypeName.TINYINT, ["1", "2"]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 257L), 257L,
                    "java.lang.Long 257: rows [0]", "not read: OverflowException"),
                new("REAL v = ?, long 2^24 + 1", MaterialisedTable.Of(SqlTypeName.REAL, ["16777216", "1.5"]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 16777217L), 16777217L,
                    "java.lang.Long 16777217: rows [0]", "java.lang.Float 1.6777216E7: rows [0]"),
                new("BIGINT v = ?, double 1.5", longs, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setDouble(1, 1.5), 1.5d,
                    "java.lang.Double 1.5: rows [0]", "java.lang.Long 2: rows [1]"),
                new("BIGINT v = ?, double NaN", longs, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setDouble(1, double.NaN), double.NaN,
                    "java.lang.Double NaN: NumberFormatException", "not read: OverflowException"),
                new("BIGINT v = ?, double NaN, no row", MaterialisedTable.Of(SqlTypeName.BIGINT, []), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setDouble(1, double.NaN), double.NaN,
                    "java.lang.Double NaN: rows []", "not read: OverflowException"),
                new("DOUBLE v = ?, double NaN", doubles, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setDouble(1, double.NaN), double.NaN,
                    "java.lang.Double NaN: NumberFormatException", "java.lang.Double NaN: NumberFormatException"),
                new("DOUBLE v = ?, double -Infinity", doubles, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setDouble(1, double.NegativeInfinity), double.NegativeInfinity,
                    "java.lang.Double -Infinity: NumberFormatException", "java.lang.Double -Infinity: NumberFormatException"),
                new("BIGINT v = ?, string '2'", longs, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setString(1, "2"), "2",
                    "java.lang.String 2: rows [1]", "java.lang.Long 2: rows [1]"),
                new("BIGINT v = ?, string 'x'", longs, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setString(1, "x"), "x",
                    "java.lang.String x: NumberFormatException", "not read: FormatException"),
                new("DECIMAL v = ?, string '2.50'", MaterialisedTable.Of(SqlTypeName.DECIMAL, ["2.5", "3"]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setString(1, "2.50"), "2.50",
                    "java.lang.String 2.50: rows [0]", "java.math.BigDecimal 2.50: rows [0]"),
                new("RETURNING INTEGER = ?, string '2'", documents, "SELECT \"k\" FROM {t} WHERE JSON_VALUE(\"d\", '$.n' RETURNING INTEGER) = ?", s => s.setString(1, "2"), "2",
                    "java.lang.String 2: rows [2]", "java.lang.Integer 2: rows [2]"),
                new("BOOLEAN v = ?, string 'true'", MaterialisedTable.Of(SqlTypeName.BOOLEAN, ["true", "false"]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setString(1, "true"), "true",
                    "java.lang.String true: ClassCastException", "java.lang.Boolean true: rows [0]"),
                new("BOOLEAN v = ?, string 'true', no row", MaterialisedTable.Of(SqlTypeName.BOOLEAN, []), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setString(1, "true"), "true",
                    "java.lang.String true: rows []", "java.lang.Boolean true: rows []"),
                new("VARCHAR v = ?, long 30", texts, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 30L), 30L,
                    "java.lang.Long 30: rows [0]", "java.lang.String 30: rows [0]"),
                new("VARCHAR v = ?, double 1e20", texts, "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setDouble(1, 1e20), 1e20d,
                    "java.lang.Double 1.0E20: rows [2]", "java.lang.String 1E+20: rows [3]"),
                new("CHAR(1) v = ?, long 17", MaterialisedTable.Of(SqlTypeName.CHAR, ["\"1\"", "\"2\""]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?", s => s.setLong(1, 17L), 17L,
                    "java.lang.Long 17: rows [0]", "java.lang.String 17: rows [0]"),
                new("UUID = ?, string", keys, UuidEquals, s => s.setString(1, Uuid), Uuid,
                    "java.lang.String " + Uuid + ": ClassCastException", "not read: InvalidCastException"),
                new("UUID = ?, java.util.UUID and Guid", keys, UuidEquals, s => s.setObject(1, java.util.UUID.fromString(Uuid)), Guid.Parse(Uuid),
                    "java.util.UUID " + Uuid + ": ClassCastException", "org.apache.calcite.util.UuidValue " + Uuid + ": rows [0]"),
                new("? > TIMESTAMP, integer 20667 and DateOnly", keys, "SELECT \"k\" FROM {t} WHERE ? > TIMESTAMP '2000-01-01 00:00:00'", s => s.setInt(1, 20667), new DateOnly(2026, 8, 2),
                    "java.lang.Integer 20667: ClassCastException", "java.lang.Long 1785628800000: rows [0]"),
                new("? > DATE, long and DateTime", keys, "SELECT \"k\" FROM {t} WHERE ? > DATE '2000-01-01'", s => s.setLong(1, 1785672000000L), new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc),
                    "java.lang.Long 1785672000000: ClassCastException", "java.lang.Integer 20667: rows [0]"),
                new("CASE no row takes, DOUBLE, string 'x'", doubles, "SELECT \"k\" FROM {t} WHERE CASE WHEN \"v\" > 1e301 THEN ? ELSE 0e0 END < 5", s => s.setString(1, "x"), "x",
                    "java.lang.String x: rows [0, 1]", "not read: FormatException"),
                new("CASE no row takes, DOUBLE, double NaN", doubles, "SELECT \"k\" FROM {t} WHERE CASE WHEN \"v\" > 1e301 THEN ? ELSE 0e0 END < 5", s => s.setDouble(1, double.NaN), double.NaN,
                    "java.lang.Double NaN: rows [0, 1]", "java.lang.Double NaN: rows [0, 1]"),
                new("CASE no row takes, INTEGER, string 'x'", integers, "SELECT \"k\" FROM {t} WHERE CASE WHEN \"v\" > 100 THEN ? ELSE 0 END = 0", s => s.setString(1, "x"), "x",
                    "java.lang.String x: rows [0, 1]", "not read: FormatException"),
                new("CASE no row takes, BOOLEAN, string 'true'", integers, "SELECT \"k\" FROM {t} WHERE CASE WHEN \"v\" > 100 THEN ? ELSE TRUE END", s => s.setString(1, "true"), "true",
                    "java.lang.String true: rows [0, 1]", "java.lang.Boolean true: rows [0, 1]"),
                new("CASE a row takes, INTEGER, string 'x'", integers, "SELECT \"k\" FROM {t} WHERE CASE WHEN \"v\" > 1 THEN ? ELSE 0 END = 0", s => s.setString(1, "x"), "x",
                    "java.lang.String x: NumberFormatException", "not read: FormatException"),
                new("projected CASE no row takes, INTEGER, string 'x'", integers, "SELECT \"k\", CASE WHEN \"v\" > 100 THEN ? ELSE 0 END FROM {t}", s => s.setString(1, "x"), "x",
                    "java.lang.String x: rows [0 0, 1 0]", "not read: FormatException"),
                new("JSON_VALUE = ?, long 30", documents, "SELECT \"k\" FROM {t} WHERE JSON_VALUE(\"d\", '$.s') = ?", s => s.setLong(1, 30L), 30L,
                    "java.lang.Long 30: rows [0]", "java.lang.String 30: rows [0]"),
                new("projected DOUBLE, long 2^53 + 1", MaterialisedTable.Of(SqlTypeName.DOUBLE, ["1.5"]), "SELECT \"k\", CAST(? AS DOUBLE) FROM {t}", s => s.setLong(1, 9007199254740993L), 9007199254740993L,
                    "java.lang.Long 9007199254740993: rows [0 9.007199254740992E15]", "java.lang.Double 9.007199254740992E15: rows [0 9007199254740992]"),
                new("projected DOUBLE, double NaN", MaterialisedTable.Of(SqlTypeName.DOUBLE, ["1.5"]), "SELECT \"k\", CAST(? AS DOUBLE) FROM {t}", s => s.setDouble(1, double.NaN), double.NaN,
                    "java.lang.Double NaN: NumberFormatException", "java.lang.Double NaN: NumberFormatException"),
            };

            using var drivers = new Drivers();
            drivers.Measure(cases).Should().Equal(Drivers.Expected(cases));
        }

        /// <summary>
        /// A NaN or an infinite double bound to a parameter of an approximate type fails the statement in Calcite, through
        /// either driver, under every comparison operator, once a row is read; one of an integer or a <c>DECIMAL</c> type
        /// fails in Calcite, or in the ADO.NET driver first; and a finite value whose conversion to an approximate type
        /// passes its range is converted to an infinity, compared with no failure and projected as the infinity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each case is a statement over an in-process table that records what the data context holds for <c>?0</c> when
        /// it is scanned, bound once through <c>jdbc:calcite:</c> and once through the ADO.NET driver, as
        /// <see cref="AParameterIsConvertedToItsTypeWhenRead"/> binds its cases.
        /// </para>
        /// <para>
        /// <b>A NaN, +Infinity or −Infinity bound to a <c>DOUBLE</c>, a <c>REAL</c> or a <c>FLOAT</c> column's parameter,
        /// or to a <c>RETURNING DOUBLE</c>'s,</b> fails under <c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>,
        /// <c>&gt;</c> and <c>&gt;=</c>, through either driver, with <c>NumberFormatException</c>: over a row holding a
        /// value, over a row holding null, and over a row an earlier conjunct is false for; over a table holding no row it
        /// answers no rows. The failure is the conversion's: the data context holds the double, and Calcite's generated
        /// code reads a numeric parameter through <c>SqlFunctions.toBigDecimal</c>, which refuses it
        /// (<c>Calcite.AParameterIsConvertedToItsTypeWhenRead</c>), before the comparison is asked.
        /// </para>
        /// <para>
        /// <b>A NaN bound to a <c>BIGINT</c>, an <c>INTEGER</c> or a <c>DECIMAL</c> parameter</b> fails in Calcite the
        /// same way through its own driver; the ADO.NET driver refuses it before the statement runs.
        /// </para>
        /// <para>
        /// <b>A value the conversion carries past the approximate type's range</b> is no failure. Through Calcite's own
        /// driver the string <c>'1e400'</c> bound to a <c>DOUBLE</c> parameter is projected as +Infinity, and the double
        /// 1e39 bound to a <c>REAL</c> one as the float +Infinity; compared, under every operator and beneath arithmetic
        /// too, each keeps 1.5 as a value above every finite one would, and its negative as one below. No finite stored
        /// value tells the infinity from a value past every double, so the comparisons show only that none fails. Through
        /// the ADO.NET driver the same values reach Calcite as an infinity of the type, which its conversion refuses.
        /// </para>
        /// </remarks>
        [Fact, CosmosPins("Calcite.ANonFiniteDoubleParameterRaises")]
        public void ANonFiniteDoubleParameterRaises()
        {
            var readings = new (string Name, Func<IEnumerable<string?>, MaterialisedTable> Table, string Expression)[]
            {
                ("DOUBLE v", values => MaterialisedTable.Of(SqlTypeName.DOUBLE, values), "\"v\""),
                ("REAL v", values => MaterialisedTable.Of(SqlTypeName.REAL, values), "\"v\""),
                ("FLOAT v", values => MaterialisedTable.Of(SqlTypeName.FLOAT, values), "\"v\""),
                ("RETURNING DOUBLE", Documents, "JSON_VALUE(\"d\", '$.v' RETURNING DOUBLE)"),
            };

            var inputs = new (string Name, string?[] Values, string Before)[]
            {
                ("one row", ["1.5"], ""),
                ("a null row", [null], ""),
                ("an earlier conjunct false", ["1.5"], "\"k\" < 0 AND "),
                ("no row", [], ""),
            };

            var cases = new List<Conversion>();

            // A non-finite double bound to an approximate parameter: every reading, every operator, every input.
            foreach (var (reading, table, expression) in readings)
                foreach (var (input, values, before) in inputs)
                    foreach (var bound in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                        foreach (var op in Ops)
                        {
                            var held = "java.lang.Double " + Java(bound);
                            var answer = values.Length == 0 ? ": rows []" : ": NumberFormatException";
                            var ado = (reading == "REAL v" ? "java.lang.Float " : "java.lang.Double ") + Java(bound);
                            cases.Add(new($"{reading} {op} ?, {input}, double {Java(bound)}", table(values), $"SELECT \"k\" FROM {{t}} WHERE {before}{expression} {op} ?",
                                s => s.setDouble(1, bound), bound, held + answer, ado + answer));
                        }

            // A NaN bound to an exact parameter.
            foreach (var type in new[] { SqlTypeName.BIGINT, SqlTypeName.INTEGER, SqlTypeName.DECIMAL })
                cases.Add(new($"{type.getName()} v = ?, double NaN", MaterialisedTable.Of(type, ["1", null]), "SELECT \"k\" FROM {t} WHERE \"v\" = ?",
                    s => s.setDouble(1, double.NaN), double.NaN, "java.lang.Double NaN: NumberFormatException", "not read: OverflowException"));

            // A finite value the conversion carries past the approximate type's range, both ways: every reading, every
            // operator, over a row holding a value and a row holding null.
            foreach (var (reading, table, expression) in readings)
                foreach (var positive in new[] { true, false })
                    foreach (var op in Ops)
                    {
                        var real = reading == "REAL v";
                        var infinity = positive ? "Infinity" : "-Infinity";
                        Action<java.sql.PreparedStatement> bind = real ? s => s.setDouble(1, positive ? 1e39 : -1e39) : s => s.setString(1, positive ? "1e400" : "-1e400");
                        object bound = real ? positive ? 1e39 : -1e39 : positive ? "1e400" : "-1e400";
                        var held = bound is double number ? "java.lang.Double " + Java(number) : "java.lang.String " + bound;
                        var kept = Kept(op, positive) ? "0" : "";
                        cases.Add(new($"{reading} {op} ?, {(bound is double ? Java((double)bound) : "'" + bound + "'")}", table(["1.5", null]), $"SELECT \"k\" FROM {{t}} WHERE {expression} {op} ?",
                            bind, bound, $"{held}: rows [{kept}]", (real ? "java.lang.Float " : "java.lang.Double ") + infinity + ": NumberFormatException"));
                    }

            // The same beneath arithmetic, and projected.
            cases.Add(new("DOUBLE v + CAST(? AS DOUBLE) > 0, '1e400'", MaterialisedTable.Of(SqlTypeName.DOUBLE, ["1.5", null]), "SELECT \"k\" FROM {t} WHERE \"v\" + CAST(? AS DOUBLE) > 0",
                s => s.setString(1, "1e400"), "1e400", "java.lang.String 1e400: rows [0]", "java.lang.Double Infinity: NumberFormatException"));
            cases.Add(new("projected DOUBLE, '1e400'", MaterialisedTable.Of(SqlTypeName.DOUBLE, ["1.5"]), "SELECT \"k\", CAST(? AS DOUBLE) FROM {t}",
                s => s.setString(1, "1e400"), "1e400", "java.lang.String 1e400: rows [0 Infinity]", "java.lang.Double Infinity: NumberFormatException"));
            cases.Add(new("projected REAL, 1.0E39", MaterialisedTable.Of(SqlTypeName.DOUBLE, ["1.5"]), "SELECT \"k\", CAST(? AS REAL) FROM {t}",
                s => s.setDouble(1, 1e39), 1e39, "java.lang.Double 1.0E39: rows [0 Infinity]", "java.lang.Float Infinity: NumberFormatException"));

            using var drivers = new Drivers();
            drivers.Measure(cases).Should().Equal(Drivers.Expected(cases));

            // Java's spelling of a double.
            static string Java(double value) => java.lang.Double.toString(value);

            // Whether the finite value 1.5 compares TRUE with +Infinity (positive) or -Infinity under an operator.
            static bool Kept(string op, bool positive) => op switch
            {
                PredicateAtom.Equal => false,
                PredicateAtom.NotEqual => true,
                PredicateAtom.Less or PredicateAtom.LessOrEqual => positive,
                _ => positive == false,
            };
        }

        /// <summary>One conversion case: a statement over a table, how each driver binds its one parameter, and what each answers.</summary>
        sealed record Conversion(string Label, MaterialisedTable Table, string Sql, Action<java.sql.PreparedStatement> Bind, object? Bound, string Calcite, string Ado);

        /// <summary>
        /// Calcite's own JDBC driver and the ADO.NET driver, each over a root schema a case's table is added to under a name
        /// of its own.
        /// </summary>
        sealed class Drivers : IDisposable
        {

            readonly java.sql.Connection _jdbc = new org.apache.calcite.jdbc.Driver().connect("jdbc:calcite:", new java.util.Properties());
            readonly DbConnection _ado = MaterialisedTable.Open();
            int _named;

            /// <summary>What each case answers through each driver, a line a driver, as <see cref="Expected"/> spells it.</summary>
            public List<string> Measure(IEnumerable<Conversion> cases)
            {
                var schema = ((org.apache.calcite.jdbc.CalciteConnection)_jdbc.unwrap(typeof(org.apache.calcite.jdbc.CalciteConnection))).getRootSchema();
                var measured = new List<string>();
                foreach (var conversion in cases)
                {
                    measured.Add(conversion.Label + " | jdbc:calcite | " + Run(conversion, schema.add, sql =>
                    {
                        var statement = _jdbc.prepareStatement(sql);
                        conversion.Bind(statement);
                        var result = statement.executeQuery();
                        var rows = new List<string>();
                        while (result.next())
                            rows.Add(string.Join(" ", Enumerable.Range(1, result.getMetaData().getColumnCount()).Select(c => result.getString(c))));

                        return rows;
                    }));

                    measured.Add(conversion.Label + " | ADO.NET | " + Run(conversion, ((SchemaPlus)((Apache.Calcite.Data.CalciteConnection)_ado).RootSchema).add, sql =>
                    {
                        using var command = _ado.CreateCommand();
                        command.CommandText = sql;
                        var parameter = command.CreateParameter();
                        parameter.Value = conversion.Bound;
                        command.Parameters.Add(parameter);
                        var rows = new List<string>();
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                            rows.Add(string.Join(" ", Enumerable.Range(0, reader.FieldCount).Select(c => Convert.ToString(reader.GetValue(c), System.Globalization.CultureInfo.InvariantCulture))));

                        return rows;
                    }));
                }

                return measured;
            }

            /// <summary>What each case says each driver answers, a line a driver.</summary>
            public static IEnumerable<string> Expected(IEnumerable<Conversion> cases) =>
                cases.SelectMany(c => new[] { c.Label + " | jdbc:calcite | " + c.Calcite, c.Label + " | ADO.NET | " + c.Ado });

            /// <summary>
            /// One statement over a fresh recording of the case's table, named afresh: what the data context held when the
            /// table was scanned, or "not read" where the driver failed first, and the rows kept or the innermost exception.
            /// </summary>
            string Run(Conversion conversion, Action<string, org.apache.calcite.schema.Table> add, Func<string, List<string>> rows)
            {
                var recording = new Recording(conversion.Table);
                var name = "c" + (++_named).ToString(System.Globalization.CultureInfo.InvariantCulture);
                add(name, recording);
                try
                {
                    var kept = rows(conversion.Sql.Replace("{t}", "\"" + name + "\""));
                    return $"{recording.Held}: rows [{string.Join(", ", kept)}]";
                }
                catch (Exception e)
                {
                    while (e.InnerException is { } inner)
                        e = inner;

                    return $"{recording.Held}: {e.GetType().Name}";
                }
            }

            public void Dispose()
            {
                _jdbc.close();
                _ado.Dispose();
            }

        }

        /// <summary>
        /// A table that records what the data context holds for <c>?0</c> when it is scanned, and answers the rows of the
        /// table it wraps.
        /// </summary>
        sealed class Recording(MaterialisedTable table) : org.apache.calcite.schema.impl.AbstractTable, ScannableTable
        {

            /// <summary>The class and the value <c>?0</c> held at the scan, Java's spelling of each; "not read" before one.</summary>
            public string Held { get; private set; } = "not read";

            public override RelDataType getRowType(RelDataTypeFactory typeFactory) => table.getRowType(typeFactory);

            public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
            {
                Held = root.get("?0") switch
                {
                    null => "null",
                    string text => "java.lang.String " + text,
                    java.lang.Object held => held.getClass().getName() + " " + held.toString(),
                    var other => other.GetType().FullName + " " + other,
                };

                return table.scan(root);
            }

        }

        /// <summary>
        /// What a comparison with a parameter answers over each stored value, over every value bound the driver takes, and
        /// which classes it refused; a statement that fails to compile raises over every value.
        /// </summary>
        static Answered Measure(DbConnection connection, Asked asked, string op)
        {
            var truths = new Truths[Stored.Length];
            var refused = new List<object>();
            var predicate = asked.Expression + " " + op + " ?";
            if (asked.Table([]).Failure(connection, [null], predicate) is not null)
                return new(Enumerable.Repeat(R, Stored.Length).ToArray(), refused, false);

            foreach (var value in Bound)
            {
                if (asked.Table([]).Failure(connection, [value], predicate) is not null)
                {
                    refused.Add(value!);
                    continue;
                }

                var letters = Ask(connection, asked, op, value);
                for (var s = 0; s < Stored.Length; s++)
                    truths[s] |= letters[s] switch { 'T' => T, 'F' => F, 'U' => U, _ => R };
            }

            return new(truths, refused, true);
        }

        /// <summary>
        /// What a comparison with one value bound answers over each stored value, a letter a value, <c>R</c> where it
        /// raises: the whole table asked at once, and where that fails, each value alone.
        /// </summary>
        static string Ask(DbConnection connection, Asked asked, string op, object? value)
        {
            var predicate = asked.Expression + " " + op + " ?";
            try
            {
                return asked.Table(Stored.Select(s => s.Json)).Ask(connection, [value], predicate)[0];
            }
            catch (Exception)
            {
                return new string([.. Stored.Select(s => Alone(s.Json))]);
            }

            char Alone(string? json)
            {
                try
                {
                    return asked.Table([json]).Ask(connection, [value], predicate)[0][0];
                }
                catch (Exception)
                {
                    return 'R';
                }
            }
        }

        /// <summary>The test a comparison's key is read by: <c>=</c>'s for <c>&lt;&gt;</c>, as the evaluator reads it.</summary>
        static CosmosServiceTest TestOf(string op) => op switch
        {
            PredicateAtom.Less => CosmosServiceTest.Less,
            PredicateAtom.LessOrEqual => CosmosServiceTest.LessOrEqual,
            PredicateAtom.Greater => CosmosServiceTest.Greater,
            PredicateAtom.GreaterOrEqual => CosmosServiceTest.GreaterOrEqual,
            _ => CosmosServiceTest.Equal,
        };

        /// <summary>The cell holding a stored value alone.</summary>
        static PathCell CellOf(string? json) => json switch
        {
            null => new PathCell.Absent(),
            "null" => new PathCell.Null(),
            ['{', ..] => new PathCell.Structure(ObjectType),
            ['[', ..] => new PathCell.Structure(ArrayType),
            "true" or "false" => new PathCell.Point(Of(json == "true")),
            ['"', ..] => new PathCell.Point(Of(json[1..^1])),
            _ => new PathCell.Point(Of(decimal.Parse(json, System.Globalization.CultureInfo.InvariantCulture))),
        };

        static MaterialisedTable Documents(IEnumerable<string?> values) => MaterialisedTable.Of(values.Select(v => v is null ? "{}" : "{\"v\":" + v + "}"));

        static Asked Raw(SqlTypeName type) => new(values => MaterialisedTable.Of(type, values), "\"v\"");

        static Asked Cast(SqlTypeName type) => new(values => MaterialisedTable.Of(type, values), "CAST(\"v\" AS VARCHAR)");

        static Asked Column(SqlTypeName type) => new(values => MaterialisedTable.Of(type, values), "\"v\"");

        static Asked Typed(string type) => new(Documents, $"JSON_VALUE(\"d\", '$.v' RETURNING {type})");

        static Asked TextCast(string target) => new(Documents, $"CAST(JSON_VALUE(\"d\", '$.v') AS {target})");

        static Asked Text() => new(Documents, "JSON_VALUE(\"d\", '$.v')");

    }

}
