using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Internal;
using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Closes the slots a prepared statement left open.
    /// </summary>
    /// <remarks>
    /// A plan is compiled once and run many times, so a statement written with <c>?</c> carries an
    /// ordinal where a value will be. Everything else about the statement is settled while it is
    /// built — the text, the names, the clauses — and this is the one step that cannot be, because the
    /// value belongs to the execution rather than to the plan.
    /// </remarks>
    public static class CosmosQueries
    {

        /// <summary>
        /// Fills in the values of any dynamic parameters the statement carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns the statement unchanged where it carries none, which is every statement a host
        /// writes its values into directly. Nothing is copied and nothing is walked twice for the
        /// ordinary case.
        /// </para>
        /// <para>
        /// The page size is recomputed rather than carried, for the reason
        /// <c>CosmosImplementor.MaxItemCount</c> gives: a limit the plan did not have could not size a
        /// page, and now that the value is here it still need not, since the <c>LIMIT</c> in the
        /// statement is what bounds the result.
        /// </para>
        /// </remarks>
        /// <param name="query">The statement as the plan built it.</param>
        /// <param name="root">The data context the execution supplies.</param>
        /// <returns>The statement with every parameter carrying a value.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> is <c>null</c> and a value is wanted.</exception>
        public static CosmosQuery Bind(CosmosQuery query, org.apache.calcite.DataContext root)
        {
            var parameters = query.Parameters;
            if (HasDynamic(parameters) == false)
                return query;

            if (root is null)
                throw new ArgumentNullException(nameof(root), "The statement carries a parameter whose value belongs to the execution.");

            var bound = new CosmosParameter[parameters.Count];

            for (var i = 0; i < parameters.Count; i++)
                bound[i] = parameters[i].Value is CosmosDynamicValue slot
                    ? parameters[i] with { Value = Value(root, slot) }
                    : parameters[i];

            return query with { Parameters = bound };
        }

        /// <summary>
        /// Determines whether any parameter is still waiting for its value.
        /// </summary>
        /// <param name="parameters">The statement's parameters.</param>
        /// <returns><c>true</c> where one is.</returns>
        static bool HasDynamic(IReadOnlyList<CosmosParameter> parameters)
        {
            for (var i = 0; i < parameters.Count; i++)
                if (parameters[i].Value is CosmosDynamicValue)
                    return true;

            return false;
        }

        /// <summary>
        /// Reads one dynamic parameter's value out of the data context.
        /// </summary>
        /// <remarks>
        /// Into the same shapes <c>CosmosRexTranslator.GetLiteralValue</c> produces, so that a value
        /// arriving late is indistinguishable from one written into the statement: every integer width
        /// as a <c>long</c>, every approximate one as a <c>double</c>. A parameter that bound
        /// differently from a literal of the same type would be a second convention for the same
        /// thing, and the service would be the one to notice.
        /// </remarks>
        /// <param name="root">The data context.</param>
        /// <param name="value">The ordinal to read.</param>
        /// <returns>The bound value.</returns>
        static object? Value(org.apache.calcite.DataContext root, CosmosDynamicValue value)
        {
            return root.get(value.VariableName) switch
            {
                null => null,
                java.lang.Byte b => (long)unchecked((sbyte)b.byteValue()),
                java.lang.Short s => (long)s.shortValue(),
                java.lang.Integer i => (long)i.intValue(),
                java.lang.Long l => l.longValue(),
                java.lang.Float f => (double)f.floatValue(),
                java.lang.Double d => d.doubleValue(),
                java.math.BigDecimal d => BigDecimalConverter.ToDecimal(d),
                java.lang.Boolean b => b.booleanValue(),
                var other => other,
            };
        }

    }

}
