using com.google.common.collect;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The same operators <see cref="CosmosOperators.Instance"/> carries, in the form a schema
    /// declares them, so that a connection can name one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A validator resolves a function name against the operator table it was built with, chained
    /// with the catalog reader — and the catalog reader resolves the schema's own functions. An
    /// operator table is therefore something a host has to hand a planner it built, while a schema
    /// function is something a connection finds by itself. Everything else this adapter offers
    /// arrives through a model document, so full text search arrived through neither until these
    /// existed.
    /// </para>
    /// <para>
    /// Derived from the operator table rather than declared beside it. The two would otherwise be one
    /// list written twice, and a function added to one and forgotten in the other would resolve
    /// through a planner a host built and not through a connection — which is the gap this closes.
    /// </para>
    /// </remarks>
    public static class CosmosSchemaFunctions
    {

        /// <summary>
        /// How many operands a function with no declared upper arity is offered through a schema.
        /// </summary>
        /// <remarks>
        /// Calcite builds a function's operand count range out of its parameter list, so a schema
        /// function accepts as many operands as it declares parameters and no more. The operators
        /// themselves are unbounded — <c>FULLTEXTCONTAINSALL</c> takes as many keywords as a caller
        /// has — and this is the arity at which that stops being true through a connection. A query
        /// needing more still resolves against <see cref="CosmosOperators.Instance"/>, whose operand
        /// checker is genuinely variadic, which is what chaining it is still for.
        /// </remarks>
        public const int VariadicOperandLimit = 16;

        /// <summary>
        /// Gets the functions a Cosmos schema declares, keyed by name.
        /// </summary>
        internal static Multimap Instance { get; } = Build();

        /// <summary>
        /// Declares each operator once per arity it accepts.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One declaration per arity rather than one with optional parameters</b>, and that is a
        /// measurement rather than a preference. A call to a function with optional parameters is
        /// padded out to the whole parameter list with <c>DEFAULT</c> before it reaches the plan —
        /// <c>SqlCallBinding.operands</c> does it for any operator whose type checker has fixed
        /// parameters — so <c>FULLTEXTCONTAINSALL(c.name, 'steel')</c> arrived carrying fourteen
        /// <c>DEFAULT()</c> operands, and a Cosmos statement has nothing to render one as. Declared
        /// one arity at a time, every parameter is required, nothing is padded, and overload
        /// resolution picks the declaration whose count matches the call.
        /// </para>
        /// <para>
        /// A name therefore carries several declarations, which is what a multimap is for and what
        /// Calcite's own overload resolution expects: it keeps the candidates whose operand count
        /// range accepts the call, and exactly one of these does.
        /// </para>
        /// </remarks>
        static Multimap Build()
        {
            var builder = ImmutableMultimap.builder();

            var operators = CosmosOperators.Instance.getOperatorList();

            for (var i = 0; i < operators.size(); i++)
            {
                if (operators.get(i) is not SqlFunction function)
                    continue;

                var range = function.getOperandCountRange();
                var minimum = range.getMin();

                // A count range with no maximum reports -1.
                var maximum = range.getMax() < 0 ? System.Math.Max(VariadicOperandLimit, minimum) : range.getMax();

                for (var arity = minimum; arity <= maximum; arity++)
                    builder.put(function.getName(), new CosmosSchemaFunction(function, arity));
            }

            return builder.build();
        }

    }

    /// <summary>
    /// One Cosmos operator at one arity, presented to a catalog reader as a function the schema
    /// declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schema function is a shape rather than an operator: Calcite reads the parameter list and
    /// builds a <c>SqlUserDefinedFunction</c> of its own around it, so what reaches the plan carries
    /// this one's name and arity rather than being this one. That is enough, and it is why the
    /// translator dispatches on a call's name — a Cosmos function exists to be rendered into a
    /// statement, and the name is the whole of what rendering needs.
    /// </para>
    /// <para>
    /// <b>Deliberately not an <c>ImplementableFunction</c>.</b> These have no in-process body and are
    /// not going to acquire one: the point of every one of them is that the service evaluates it.
    /// Binding a CLR method here would let a call that cannot be pushed down plan anyway and then
    /// answer with something Cosmos never computed.
    /// </para>
    /// </remarks>
    sealed class CosmosSchemaFunction : ScalarFunction
    {

        readonly SqlFunction _operator;
        readonly int _arity;
        readonly java.util.List _parameters;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="op">The operator to present.</param>
        /// <param name="arity">How many operands this declaration takes.</param>
        public CosmosSchemaFunction(SqlFunction op, int arity)
        {
            _operator = op;
            _arity = arity;

            // Every one required. An optional parameter is padded with DEFAULT at the call site, and
            // a Cosmos statement has nothing to render that as — see CosmosSchemaFunctions.Build.
            var parameters = new java.util.ArrayList();
            for (var i = 0; i < arity; i++)
                parameters.add(new CosmosSchemaFunctionParameter(i));

            _parameters = parameters;
        }

        /// <inheritdoc />
        public java.util.List getParameters()
        {
            return _parameters;
        }

        /// <summary>
        /// Returns what a call to this function is typed as.
        /// </summary>
        /// <remarks>
        /// Asked of the operator rather than restated here, over as many <c>ANY</c> operands as this
        /// declaration takes. Every one of these infers from nothing else — a boolean, a double, or
        /// the <c>ANY</c> the row model gives every document value — so the operands are a formality;
        /// asking anyway is what keeps a plan built through a connection and a plan built through a
        /// chained operator table the same plan.
        /// </remarks>
        /// <param name="typeFactory">The type factory.</param>
        /// <returns>The return type.</returns>
        public RelDataType getReturnType(RelDataTypeFactory typeFactory)
        {
            var types = new java.util.ArrayList();
            for (var i = 0; i < _arity; i++)
                types.add(CosmosSchemaFunctionParameter.Any(typeFactory));

            return _operator.inferReturnType(new ExplicitOperatorBinding(typeFactory, _operator, types));
        }

    }

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
