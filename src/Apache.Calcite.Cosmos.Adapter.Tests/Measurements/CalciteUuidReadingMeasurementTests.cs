using System;

using Apache.Calcite.Data;

using FluentAssertions;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What a <c>UUID</c> column holds, measured from the row a table yields to the value a
    /// <c>DbDataReader</c> hands back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No service and no adapter: an in-process table whose one row carries the very thing
    /// <c>CosmosJson.GetUuid</c> builds, in a column typed <c>UUID</c>. What is pinned is the
    /// convention the adapter has to meet — that <c>org.apache.calcite.util.UuidValue</c> <em>is</em>
    /// the representation of a UUID value, and that the reader is what turns it into the
    /// <see cref="Guid"/> a caller maps.
    /// </para>
    /// <para>
    /// <b>The reason it is worth a test of its own is #150, and the shape of that report is the
    /// second measurement here.</b> The adapter read a column typed <c>UUID</c> through
    /// <c>SqlFunctions.stringToUuid</c>, which returns the bare <c>java.util.UUID</c> a
    /// <c>UuidValue</c> wraps rather than the wrapper — the right value in a class the plan does not
    /// use. That is invisible at two columns and fatal at one, which is why it reached the field as
    /// a <c>$count</c> and an <c>$expand</c> failing where an ordinary page did not, and it is
    /// reproduced below without the adapter in the path at all.
    /// </para>
    /// <para>
    /// The arity is the whole of the difference: <c>JavaRowFormat.optimize</c> makes a one-field row
    /// the value itself, so the value is cast to the field's physical type, while a wider row is an
    /// <c>object[]</c> that boxes whatever it is handed and converts per column on the way out. So
    /// the wrong class survives the wide row and only the narrow one asks.
    /// </para>
    /// <para>
    /// If Calcite ever moves <c>stringToUuid</c> onto <c>UuidValue</c> too, the second test fails
    /// here rather than the discrepancy going quiet.
    /// </para>
    /// </remarks>
    public class CalciteUuidReadingMeasurementTests
    {

        const string Canonical = "0123456f-89ab-7cde-8f01-23456789abcd";

        /// <summary>
        /// A table whose one row holds the given value in a <c>UUID</c> column, beside a second
        /// column or alone.
        /// </summary>
        sealed class UuidTable : org.apache.calcite.schema.impl.AbstractTable, ScannableTable
        {

            readonly object _value;
            readonly bool _wide;

            public UuidTable(object value, bool wide)
            {
                _value = value;
                _wide = wide;
            }

            public override RelDataType getRowType(RelDataTypeFactory typeFactory)
            {
                var builder = typeFactory.builder().add("U", typeFactory.createSqlType(SqlTypeName.UUID));

                if (_wide)
                    builder = builder.add("N", typeFactory.createSqlType(SqlTypeName.VARCHAR));

                return builder.build();
            }

            public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
            {
                var rows = new java.util.ArrayList(1);
                rows.add(_wide ? new object[] { _value, "widget" } : new object[] { _value });

                return org.apache.calcite.linq4j.Linq4j.asEnumerable(rows);
            }

        }

        /// <summary>
        /// Reads the first column of the one row a table of the given shape yields.
        /// </summary>
        static object? Read(object value, bool wide)
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            ((SchemaPlus)connection.RootSchema).add("u", new UuidTable(value, wide));

            using var command = connection.CreateCommand();
            command.CommandText = wide ? "SELECT \"U\", \"N\" FROM \"u\"" : "SELECT \"U\" FROM \"u\"";

            using var reader = command.ExecuteReader();

            reader.GetFieldType(0).Should().Be(typeof(Guid), "a UUID column is declared as a CLR Guid");
            reader.Read().Should().BeTrue();

            return reader.GetValue(0);
        }

        /// <summary>
        /// A <c>UuidValue</c> in a <c>UUID</c> column reaches the reader as a <see cref="Guid"/>,
        /// whether it is the row or one column of it.
        /// </summary>
        /// <remarks>
        /// Which is what makes <c>UuidValue.fromString</c> the right thing for the row builder to
        /// produce, and what a caller mapping the column to a <see cref="Guid"/> property depends on.
        /// </remarks>
        [Fact]
        public void AUuidValueReachesTheReaderAsAGuid()
        {
            foreach (var wide in new[] { false, true })
                Read(org.apache.calcite.util.UuidValue.fromString(Canonical), wide)
                    .Should().BeOfType<Guid>("at width " + (wide ? 2 : 1))
                    .And.Be(Guid.Parse(Canonical));
        }

        /// <summary>
        /// The bare <c>java.util.UUID</c> inside it does not — at one column. At two it is silently
        /// fine.
        /// </summary>
        /// <remarks>
        /// #150 in one test, with nothing of this adapter in the path: the same value, the same
        /// column type, and the arity deciding between a <see cref="Guid"/> and the exact cast the
        /// issue reports. It is the argument for reading into the box rather than into the value —
        /// a reader that is right about the value alone is right only until a query projects one
        /// column.
        /// </remarks>
        [Fact]
        public void TheBareUuidInsideItIsFatalAtOneColumnAndInvisibleAtTwo()
        {
            var bare = java.util.UUID.fromString(Canonical);

            var narrow = () => Read(bare, wide: false);
            narrow.Should().Throw<InvalidCastException>(
                    "a one-field row is the value, and it is cast to the type the field declares")
                .WithMessage("*java.util.UUID*org.apache.calcite.util.UuidValue*");

            Read(bare, wide: true).Should().BeOfType<Guid>(
                    "while an object[] boxes whatever it is handed, which is what hid this")
                .And.Be(Guid.Parse(Canonical));
        }

    }

}
