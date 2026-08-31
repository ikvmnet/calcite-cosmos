using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// One parameter of a <see cref="CosmosSchemaFunction"/>.
    /// </summary>
    /// <remarks>
    /// Typed <c>ANY</c>, which is both what the row model types every document value and what these
    /// operators ask of their arguments: what the first argument of a full text predicate has to be
    /// is a <em>path</em>, which is a question about the expression rather than about its type, and
    /// the translator is where it is asked.
    /// </remarks>
    sealed class CosmosSchemaFunctionParameter : FunctionParameter
    {

        readonly int _ordinal;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ordinal">The parameter's position.</param>
        public CosmosSchemaFunctionParameter(int ordinal)
        {
            _ordinal = ordinal;
        }

        /// <summary>
        /// Returns the nullable <c>ANY</c> every one of these parameters carries.
        /// </summary>
        /// <param name="typeFactory">The type factory.</param>
        /// <returns>The type.</returns>
        public static RelDataType Any(RelDataTypeFactory typeFactory)
        {
            return typeFactory.createTypeWithNullability(typeFactory.createSqlType(SqlTypeName.ANY), true);
        }

        /// <inheritdoc />
        public int getOrdinal()
        {
            return _ordinal;
        }

        /// <inheritdoc />
        public string getName()
        {
            return "ARG" + _ordinal;
        }

        /// <inheritdoc />
        public RelDataType getType(RelDataTypeFactory typeFactory)
        {
            return Any(typeFactory);
        }

        /// <summary>
        /// Returns <c>false</c>: a declaration takes exactly the operands it names.
        /// </summary>
        /// <returns><c>false</c>.</returns>
        public bool isOptional()
        {
            return false;
        }

    }

}
