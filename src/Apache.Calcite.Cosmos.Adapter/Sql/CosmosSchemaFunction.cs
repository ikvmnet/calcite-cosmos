using org.apache.calcite.adapter.enumerable;
using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

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
    /// <b>Implementable, and the implementation refuses.</b> These have no in-process body and are not
    /// going to acquire one: the point of every one of them is that the service evaluates it, and
    /// binding a CLR method here would let a call that cannot be pushed down plan anyway and then
    /// answer with something Cosmos never computed. Declining the interface said the same thing, but
    /// Calcite says it on this type's behalf — <c>User defined function FULLTEXTSCORE must implement
    /// ImplementableFunction</c> — which names an interface rather than the reason and reads as a
    /// defect in the adapter. Implementing it and throwing puts the refusal in the same place, at
    /// prepare time, in words a caller can act on.
    /// </para>
    /// </remarks>
    sealed class CosmosSchemaFunction : ScalarFunction, ImplementableFunction
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

        /// <summary>
        /// Refuses to supply an in-process body, saying why.
        /// </summary>
        /// <remarks>
        /// Calcite asks for this while generating code for a plan, so a call that survived to here is
        /// one no rule pushed down and the statement cannot be answered. Throwing is the same outcome
        /// as not implementing the interface at all, at the same moment; what it adds is the reason.
        /// </remarks>
        /// <returns>Never returns.</returns>
        /// <exception cref="java.lang.UnsupportedOperationException">Always.</exception>
        public CallImplementor getImplementor()
        {
            throw new java.lang.UnsupportedOperationException(Refusal(_operator.getName()));
        }

        /// <summary>
        /// Says why a function cannot be evaluated where the plan put it.
        /// </summary>
        /// <remarks>
        /// The scoring functions get their own sentence because their refusal is a property of the
        /// service rather than of this adapter, and because a caller reaching it has usually done one
        /// of two quite different things.
        /// </remarks>
        /// <param name="name">The function's name.</param>
        /// <returns>The message.</returns>
        static string Refusal(string name)
        {
            return name is "FULLTEXTSCORE" or "RRF"
                ? name + " has no value this adapter can produce. Cosmos computes a relevance score "
                    + "only while ordering and never returns one, so nothing may read it. A statement "
                    + "arrives here either by selecting the score, which the service rejects outright, "
                    + "or by ordering on it in a plan that could not push the whole ordering down."
                : name + " is evaluated by the service and has no in-process body. This plan asks for "
                    + "its value somewhere the call could not be pushed down.";
        }

    }

}
