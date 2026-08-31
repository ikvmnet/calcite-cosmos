using com.google.common.collect;

using org.apache.calcite.sql;

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

}
