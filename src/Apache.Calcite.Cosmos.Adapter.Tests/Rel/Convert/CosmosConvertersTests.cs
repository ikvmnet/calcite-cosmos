using System;

using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Adapter.Cursor;

using FluentAssertions;

using org.apache.calcite.adapter.enumerable;
using org.apache.calcite.jdbc;
using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel.Convert
{

    /// <summary>
    /// The readers a converter builds over a statement's rows.
    /// </summary>
    public class CosmosConvertersTests
    {

        /// <summary>
        /// A point read's row is built by walking each field's path in the document, and the element a traversal yields is no
        /// step it can walk: a field bound to one is refused as the defect it is, since only the correlate rule's question
        /// binds one.
        /// </summary>
        [Fact]
        public void APointReadRefusesAFieldBoundToTheElementATraversalYields()
        {
            var types = new JavaTypeFactoryImpl();
            var rowType = types.builder().add("t", types.createSqlType(SqlTypeName.VARCHAR)).build();
            var physType = ClrPhysTypeImpl.Of(types, rowType, JavaRowFormat.ARRAY);
            var element = CosmosPath.Root("c").Property("tags").Append(CosmosPathSegment.Yielded("VARCHAR", "ARRAY", 0));

            CosmosConverters.DocumentRowBuilder(physType, rowType, [CosmosPath.Root("c").Property("t")], [CosmosReading.Typed], [null]).Should().NotBeNull();

            var act = () => CosmosConverters.DocumentRowBuilder(physType, rowType, [element], [CosmosReading.Typed], [null]);
            act.Should().Throw<InvalidOperationException>().WithMessage("*which a document read cannot address*");
        }

    }

}
