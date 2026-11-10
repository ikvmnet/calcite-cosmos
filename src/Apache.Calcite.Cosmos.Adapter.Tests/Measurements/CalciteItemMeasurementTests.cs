using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

using FluentAssertions;

using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's <c>ITEM</c> answers over the value the reader built, and what a reading of an element answers: the
    /// Calcite side of an index segment in a path.
    /// </summary>
    /// <remarks>
    /// In process, over <see cref="MaterialisedTable"/>: an <c>ANY</c> column holds what <c>GetNatural</c> makes of each
    /// value, and the document column what the accessors read.
    /// </remarks>
    public class CalciteItemMeasurementTests
    {

        /// <summary>
        /// The values: an array of one and of two; an empty array; a string, a number, a boolean, an object, a null and an
        /// absent path.
        /// </summary>
        static readonly string?[] Values = ["[2]", "[1,2]", "[]", "\"ab\"", "30", "true", "{\"a\":1}", "null", null];

        /// <summary>
        /// An index over a raw value is its element, counted from one, and null wherever there is no element: past the end,
        /// at zero, over an object, a null and an absent path. Over a scalar a <c>VARIANT</c> is null too, and an
        /// <c>ANY</c> raises, its <c>ITEM</c> reading the value as a struct. An <c>ANY</c>'s element is the value the reader
        /// built, and a <c>VARIANT</c>'s no box: unequal to every boxed value, and a cast of it raises.
        /// </summary>
        [Fact, CosmosPins("Calcite.ItemIsNullWhereNoElementIs")]
        public void AnItemIsNullWhereNoElementIs()
        {
            using var connection = MaterialisedTable.Open();
            string?[] structures = [.. Values.Where(v => v is null || v[0] is '[' or '{' || v == "null")];
            foreach (var type in new[] { SqlTypeName.ANY, SqlTypeName.VARIANT })
                MaterialisedTable.Of(type, structures).Ask(connection, "\"v\"[1] IS NULL", "\"v\"[2] IS NULL", "\"v\"[0] IS NULL").Should().Equal(["FFTTTT", "TFTTTT", "TTTTTT"], type.getName());

            // An ANY's element is the value the reader built; a VARIANT's is no box, unequal to every boxed value and no cast's.
            MaterialisedTable.Of(SqlTypeName.ANY, structures).Ask(connection, "\"v\"[1] = CAST(2 AS BIGINT)").Should().Equal("TFUUUU");
            MaterialisedTable.Of(SqlTypeName.VARIANT, structures).Ask(connection, "\"v\"[1] = CAST(CAST(2 AS BIGINT) AS VARIANT)").Should().Equal("FFUUUU");
            MaterialisedTable.Of(SqlTypeName.VARIANT, ["[2]"]).Failure(connection, "CAST(\"v\"[1] AS INTEGER) = 2").Should().BeOfType<System.InvalidCastException>();

            foreach (var scalar in new[] { "\"ab\"", "30", "true" })
            {
                MaterialisedTable.Of(SqlTypeName.VARIANT, [scalar]).Ask(connection, "\"v\"[1] IS NULL").Should().Equal(["T"], scalar);
                MaterialisedTable.Of(SqlTypeName.ANY, [scalar]).Failure(connection, "\"v\"[1] IS NULL").Should().BeOfType<java.lang.IllegalStateException>(scalar);
            }
        }

        /// <summary>
        /// A reading of an element answers what the same reading of a path holding the element answers: the text accessor,
        /// a <c>RETURNING</c> and <c>JSON_QUERY</c> alike; and where there is no element, past the end or over a value that
        /// is no array, what it answers over an absent path.
        /// </summary>
        [Fact, CosmosPins("Calcite.ReadingsOfAnIndexAreAsOfAPath")]
        public void AReadingOfAnIndexIsAsOfAPath()
        {
            using var connection = MaterialisedTable.Open();
            string[] elements = ["{\"f\":[30]}", "{\"f\":[\"x\"]}", "{\"f\":[{\"a\":1}]}", "{\"f\":[null]}", "{\"f\":[]}", "{\"f\":30}", "{}"];
            string[] paths = ["{\"g\":30}", "{\"g\":\"x\"}", "{\"g\":{\"a\":1}}", "{\"g\":null}", "{}", "{}", "{}"];
            string[] readings = ["JSON_VALUE(\"d\", '$.f[0]') = '30'", "JSON_VALUE(\"d\", '$.f[0]') IS NULL", "JSON_QUERY(\"d\", '$.f[0]') IS NULL", "JSON_EXISTS(\"d\", '$.f[0]')"];
            string Path(string reading) => reading.Replace("$.f[0]", "$.g");

            MaterialisedTable.Of(elements).Ask(connection, readings).Should().Equal(MaterialisedTable.Of(paths).Ask(connection, [.. readings.Select(Path)]));

            // A RETURNING raises over a scalar of another type, so it is asked over the rest.
            var typed = "JSON_VALUE(\"d\", '$.f[0]' RETURNING INTEGER) = 30";
            int[] numeric = [0, 2, 3, 4, 5, 6];
            MaterialisedTable.Of(numeric.Select(i => elements[i])).Ask(connection, typed).Should().Equal(MaterialisedTable.Of(numeric.Select(i => paths[i])).Ask(connection, Path(typed)));
        }

    }

}
