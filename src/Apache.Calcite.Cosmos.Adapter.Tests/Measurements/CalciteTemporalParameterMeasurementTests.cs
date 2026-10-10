using System;
using System.Collections.Generic;

using Apache.Calcite.Data;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What a temporal parameter is by the time a statement can read it, measured through Calcite's own
    /// driver and through the ADO.NET one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>CosmosQueries.Bind</c> writes a parameter compared with a stored instant in the path's own
    /// spelling, and has to write the value the in-process comparison would have compared.</b> That
    /// value is whatever Calcite's runtime holds, which is not what the caller sent and not what the
    /// parameter's type says — so it is asked here, of a table whose scan records what the data context
    /// hands it, rather than assumed.
    /// </para>
    /// <para>
    /// No service and no adapter: a table that answers one row and remembers <c>?0</c>.
    /// </para>
    /// </remarks>
    public class CalciteTemporalParameterMeasurementTests
    {

        /// <summary>
        /// A one-row table whose scan records the first parameter's value.
        /// </summary>
        public sealed class RecordingTable : org.apache.calcite.schema.impl.AbstractTable, org.apache.calcite.schema.ScannableTable
        {

            static volatile object? _seen;

            /// <summary>
            /// The value <c>?0</c> held at the last scan. One class reads it, and a class's tests run
            /// one at a time.
            /// </summary>
            public static object? Seen => _seen;

            /// <inheritdoc />
            public override org.apache.calcite.rel.type.RelDataType getRowType(org.apache.calcite.rel.type.RelDataTypeFactory typeFactory) =>
                typeFactory.builder().add("X", org.apache.calcite.sql.type.SqlTypeName.INTEGER).build();

            /// <inheritdoc />
            public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
            {
                _seen = root.get("?0");
                return org.apache.calcite.linq4j.Linq4j.asEnumerable(new object[] { new object[] { java.lang.Integer.valueOf(1) } });
            }

        }

        /// <summary>
        /// Builds the schema holding <see cref="RecordingTable"/>, for a model to name.
        /// </summary>
        public sealed class RecordingSchemaFactory : org.apache.calcite.schema.SchemaFactory
        {

            /// <inheritdoc />
            public org.apache.calcite.schema.Schema create(org.apache.calcite.schema.SchemaPlus parentSchema, string name, java.util.Map operand) => new Schema();

            sealed class Schema : org.apache.calcite.schema.impl.AbstractSchema
            {

                protected override java.util.Map getTableMap()
                {
                    var map = new java.util.HashMap();
                    map.put("T", new RecordingTable());
                    return map;
                }

            }

        }

        /// <summary>
        /// Runs a statement through Calcite's own driver with one parameter set, answering the first
        /// column and what the scan saw.
        /// </summary>
        static (string? Column, object? Seen) Jdbc(string sql, Action<java.sql.PreparedStatement> set)
        {
            var connection = java.sql.DriverManager.getConnection("jdbc:calcite:", new java.util.Properties());

            try
            {
                var calcite = (org.apache.calcite.jdbc.CalciteConnection)connection.unwrap(typeof(org.apache.calcite.jdbc.CalciteConnection));
                calcite.getRootSchema().add("T", new RecordingTable());

                var statement = connection.prepareStatement(sql);
                set(statement);

                var results = statement.executeQuery();
                results.next().Should().BeTrue();

                return (results.getString(1), RecordingTable.Seen);
            }
            finally
            {
                connection.close();
            }
        }

        /// <summary>
        /// A <c>TIMESTAMP</c> parameter is a <c>java.lang.Long</c> of epoch milliseconds, and the
        /// nanoseconds the caller sent beyond them are gone before anything reads it.
        /// </summary>
        [Fact]
        public void ATimestampParameterIsEpochMilliseconds()
        {
            var (_, seen) = Jdbc("""SELECT 1 FROM "T" WHERE CAST(? AS TIMESTAMP(3)) > TIMESTAMP '2000-01-01 00:00:00'""",
                s => s.setTimestamp(1, java.sql.Timestamp.valueOf("2026-08-02 12:00:00.123456789")));

            seen.Should().BeOfType<java.lang.Long>()
                .Which.longValue().Should().Be(new DateTimeOffset(2026, 8, 2, 12, 0, 0, 123, TimeSpan.Zero).ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// <c>CAST(? AS TIMESTAMP)</c> keeps the milliseconds its <c>TIMESTAMP(0)</c> has no room for:
        /// the value the comparison sees is the value the parameter carries.
        /// </summary>
        /// <remarks>
        /// The text of the value is written at the declared precision, which is why this reads the
        /// timestamp's milliseconds rather than its rendering: the rendering drops them and the value
        /// does not.
        /// </remarks>
        [Fact]
        public void ACastToTimestampZeroKeepsTheMilliseconds()
        {
            var connection = java.sql.DriverManager.getConnection("jdbc:calcite:", new java.util.Properties());

            try
            {
                var statement = connection.prepareStatement("""SELECT CAST(? AS TIMESTAMP), CAST(CAST(? AS TIMESTAMP) AS VARCHAR), CAST(? AS TIMESTAMP) > TIMESTAMP '2026-08-02 12:00:00' FROM (VALUES (1))""");
                var value = java.sql.Timestamp.valueOf("2026-08-02 12:00:00.123");
                statement.setTimestamp(1, value);
                statement.setTimestamp(2, value);
                statement.setTimestamp(3, value);

                var results = statement.executeQuery();
                results.next().Should().BeTrue();

                results.getTimestamp(1).getTime().Should().Be(value.getTime(), "the value keeps its milliseconds");
                results.getString(2).Should().Be("2026-08-02 12:00:00", "while its text is written to the declared precision");
                results.getBoolean(3).Should().BeTrue("and a comparison reads the value, not the text");
            }
            finally
            {
                connection.close();
            }
        }

        /// <summary>
        /// A <c>DATE</c> parameter is a <c>java.lang.Integer</c> of days since the epoch.
        /// </summary>
        [Fact]
        public void ADateParameterIsEpochDays()
        {
            var (_, seen) = Jdbc("""SELECT 1 FROM "T" WHERE CAST(? AS DATE) > DATE '2000-01-01'""",
                s => s.setDate(1, java.sql.Date.valueOf("2026-08-02")));

            seen.Should().BeOfType<java.lang.Integer>()
                .Which.intValue().Should().Be((int)(new DateTime(2026, 8, 2) - DateTime.UnixEpoch).TotalDays);
        }

        /// <summary>
        /// Through the ADO.NET driver a <see cref="DateTime"/> arrives the same way, its ticks beyond
        /// the millisecond dropped — and a <see cref="DateTimeKind.Utc"/> value is the wall clock it
        /// carries, read as UTC.
        /// </summary>
        [Fact]
        public void AnAdoDateTimeIsEpochMillisecondsToo()
        {
            _ = new RecordingSchemaFactory();

            const string Model = """
                inline:{ "version": "1.0", "defaultSchema": "R",
                  "schemas": [ { "name": "R", "type": "custom",
                    "factory": "Apache.Calcite.Cosmos.Adapter.Tests.Measurements.CalciteTemporalParameterMeasurementTests+RecordingSchemaFactory, Apache.Calcite.Cosmos.Adapter.Tests" } ] }
                """;

            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder { Model = Model }.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """SELECT 1 FROM "T" WHERE CAST(? AS TIMESTAMP) > TIMESTAMP '2000-01-01 00:00:00'""";

            var parameter = command.CreateParameter();
            parameter.Value = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
            command.Parameters.Add(parameter);

            using (var reader = command.ExecuteReader())
                reader.Read().Should().BeTrue();

            RecordingTable.Seen.Should().BeOfType<java.lang.Long>()
                .Which.longValue().Should().Be(new DateTimeOffset(2026, 8, 2, 12, 0, 0, 123, TimeSpan.Zero).ToUnixTimeMilliseconds());
        }

    }

}
