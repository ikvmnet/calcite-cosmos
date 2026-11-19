using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

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
