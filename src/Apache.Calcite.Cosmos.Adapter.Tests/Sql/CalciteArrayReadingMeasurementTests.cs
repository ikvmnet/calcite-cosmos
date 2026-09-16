using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// What an <c>ARRAY</c> column holds, measured from the row a table yields to the value a
    /// <c>DbDataReader</c> hands back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No service and no adapter: an in-process table whose one row carries the very thing
    /// <c>CosmosJson.GetList</c> builds — a <see cref="java.util.List"/> — in a column typed <c>VARCHAR ARRAY</c>. What is pinned is the
    /// convention the adapter has to meet: that a list <em>is</em> the representation of a collection
    /// value, and that the reader is what turns it into something a caller can use.
    /// </para>
    /// <para>
    /// The reason it is worth a test of its own is #119. The adapter had no way to produce a
    /// collection value at all, so nothing here had ever been exercised through it, and the question
    /// "what should the row carry" had no answer in this repository. It does now, and if Calcite ever
    /// changes it this fails here rather than in a caller's materialiser.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CalciteArrayReadingMeasurementTests
    {

        /// <summary>
        /// A table of one <c>VARCHAR ARRAY</c> column whose one row holds a
        /// <see cref="java.util.List"/>.
        /// </summary>
        sealed class ListTable : org.apache.calcite.schema.impl.AbstractTable, ScannableTable
        {

            public override RelDataType getRowType(RelDataTypeFactory typeFactory)
            {
                var element = typeFactory.createTypeWithNullability(typeFactory.createSqlType(SqlTypeName.VARCHAR), true);

                return typeFactory.builder().add("T", typeFactory.createArrayType(element, -1)).build();
            }

            public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
            {
                var value = new java.util.ArrayList(2);
                value.add("a");
                value.add("b");

                var rows = new java.util.ArrayList(1);
                rows.add(new object[] { value });

                return org.apache.calcite.linq4j.Linq4j.asEnumerable(rows);
            }

        }

        /// <summary>
        /// A <see cref="java.util.List"/> in an <c>ARRAY</c> column reaches the reader as a CLR array
        /// of the declared element type.
        /// </summary>
        /// <remarks>
        /// Which is what makes <c>CosmosJson.GetList</c> the right thing for the row builder to produce, and what a caller mapping the column to a
        /// primitive collection depends on. The element type is the column's, not the values' — so
        /// reading each element as the declared component type, rather than by its own JSON type, is
        /// what keeps the two in step.
        /// </remarks>
        [TestMethod]
        public void AListInAnArrayColumnReachesTheReaderAsAClrArray()
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            ((SchemaPlus)connection.RootSchema).add("lists", new ListTable());

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"T\" FROM \"lists\"";

            using var reader = command.ExecuteReader();

            reader.GetFieldType(0).Should().Be(typeof(string[]), "an ARRAY column is declared as a CLR array");
            reader.Read().Should().BeTrue();
            reader.IsDBNull(0).Should().BeFalse("the row carries a list, which is a value and not a null");
            reader.GetValue(0).Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
        }

    }

}
