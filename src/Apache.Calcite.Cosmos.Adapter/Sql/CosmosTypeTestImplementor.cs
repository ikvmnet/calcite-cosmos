using System.Collections.Generic;

using org.apache.calcite.adapter.enumerable;
using org.apache.calcite.linq4j.tree;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Generates the in-process body for a Cosmos type test, where the call reads a document path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A type test is written against the value at a path — <c>IS_STRING(JSON_VALUE(c."DOC", '$.v'))</c>
    /// — and the value is the wrong thing to hand it. By the time <c>JSON_VALUE</c> has produced one,
    /// an absent path, a JSON null and anything that is not a scalar are all the same SQL NULL, and the
    /// test cannot tell apart the cases it exists to tell apart. So the accessor is taken to bits here
    /// rather than evaluated: the document expression and the path go to
    /// <see cref="CosmosFunctionBodies"/>, which reads the kind off the document itself.
    /// </para>
    /// <para>
    /// Where the operand is not a document accessor there is nothing to take to bits, and the call is
    /// refused exactly as it was before — a type test over a computed value is a question about
    /// something this cannot see into.
    /// </para>
    /// </remarks>
    sealed class CosmosTypeTestImplementor : CallImplementor
    {

        /// <summary>The bodies, by the operator name each answers for.</summary>
        static readonly Dictionary<string, string> Bodies = new(System.StringComparer.Ordinal)
        {
            ["IS_DEFINED"] = nameof(CosmosFunctionBodies.IsDefined),
            ["IS_NULL"] = nameof(CosmosFunctionBodies.IsNull),
            ["IS_STRING"] = nameof(CosmosFunctionBodies.IsString),
            ["IS_NUMBER"] = nameof(CosmosFunctionBodies.IsNumber),
            ["IS_BOOL"] = nameof(CosmosFunctionBodies.IsBool),
            ["IS_ARRAY"] = nameof(CosmosFunctionBodies.IsArray),
            ["IS_OBJECT"] = nameof(CosmosFunctionBodies.IsObject),
            ["IS_PRIMITIVE"] = nameof(CosmosFunctionBodies.IsPrimitive),
        };

        /// <summary>
        /// Determines whether an operator has an in-process body.
        /// </summary>
        /// <param name="name">The operator's name.</param>
        /// <returns><c>true</c> where one exists.</returns>
        public static bool Answers(string? name)
        {
            return name is not null && Bodies.ContainsKey(name);
        }

        readonly string _name;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The operator's name.</param>
        public CosmosTypeTestImplementor(string name)
        {
            _name = name;
        }

        /// <summary>
        /// Resolves a reference into the enclosing program's expression list, where the node is one.
        /// </summary>
        /// <remarks>
        /// A <c>Calc</c> holds its expressions once and refers to them by index, so inside one the
        /// operand of <c>IS_DEFINED</c> is a <c>RexLocalRef</c> and not the accessor it stands for.
        /// Matching the shape without resolving that first matches nothing, and the call refuses every
        /// plan that actually reaches code generation — which is precisely the plan this exists for.
        /// </remarks>
        static RexNode Deref(RexToLixTranslator translator, RexNode node)
        {
            return node is RexLocalRef ? (RexNode)translator.deref(node) : node;
        }

        /// <inheritdoc />
        public Expression implement(RexToLixTranslator translator, RexCall call, RexImpTable.NullAs nullAs)
        {
            if (Bodies.TryGetValue(_name, out var body) == false)
                throw new java.lang.UnsupportedOperationException(CosmosSchemaFunction.Refusal(_name));

            if (call.getOperands().size() != 1 || Deref(translator, (RexNode)call.getOperands().get(0)) is not RexCall accessor)
                throw new java.lang.UnsupportedOperationException(CosmosSchemaFunction.Refusal(_name));

            // JSON_VALUE and JSON_QUERY alike: which one the caller wrote says what they expected back,
            // and this wants neither — only the document and the path they name between them.
            if (accessor.getOperator().getName() is not ("JSON_VALUE" or "JSON_QUERY") || accessor.getOperands().size() < 2)
                throw new java.lang.UnsupportedOperationException(CosmosSchemaFunction.Refusal(_name));

            if (Deref(translator, (RexNode)accessor.getOperands().get(1)) is not RexLiteral literal)
                throw new java.lang.UnsupportedOperationException(CosmosSchemaFunction.Refusal(_name));

            var path = literal.getValueAs((java.lang.Class)typeof(java.lang.String)) as string;
            if (string.IsNullOrEmpty(path))
                throw new java.lang.UnsupportedOperationException(CosmosSchemaFunction.Refusal(_name));

            // translateList rather than translate: the single-operand form is protected, and this is
            // the accessible way to ask the translator for an operand's expression.
            var translated = translator.translateList(java.util.Collections.singletonList(accessor.getOperands().get(0)));
            var document = (Expression)translated.get(0);

            return Expressions.call(
                (java.lang.Class)typeof(CosmosFunctionBodies),
                body,
                new java.util.ArrayList { document, Expressions.constant(path) });
        }

    }

}
