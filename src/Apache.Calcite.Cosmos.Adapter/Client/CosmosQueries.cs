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
                    ? parameters[i] with { Value = slot.Form is Metadata.CosmosRepresentation form ? Stored(root, slot, form) : Value(root, slot) }
                    : parameters[i];

            return query with { Parameters = bound };
        }

        /// <summary>
        /// Reads one dynamic parameter's value out of the data context and writes it in the stored
        /// form of the path it is compared with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The value is what Calcite's runtime holds, and that is what the in-process comparison
        /// would have compared.</b> Measured, through Calcite's own driver and through the ADO.NET one
        /// alike: a <c>TIMESTAMP</c> parameter reaches the data context as a <c>java.lang.Long</c> of
        /// epoch milliseconds, whatever precision its cast declares and whatever the caller sent —
        /// a .NET tick is dropped on the way in, and <c>CAST(? AS TIMESTAMP)</c> keeps the
        /// milliseconds its <c>TIMESTAMP(0)</c> has no room for. A <c>DATE</c> arrives as a
        /// <c>java.lang.Integer</c> of days. Both are read as UTC, which is the reading the literal
        /// path gives a literal and the one these forms store.
        /// </para>
        /// <para>
        /// <b>A null is bound as null</b>, which is what a parameter of any other kind binds — and
        /// that the service compares a JSON null as a value where SQL compares nothing is the same
        /// question for every parameter, recorded in <c>TODO.md</c> rather than answered for this one
        /// alone.
        /// </para>
        /// </remarks>
        /// <param name="root">The data context.</param>
        /// <param name="slot">The ordinal to read, and how it is rounded.</param>
        /// <param name="form">The stored form to write it in.</param>
        /// <returns>The bound value.</returns>
        /// <exception cref="CosmosExecutionException">The value is not an instant, or cannot be written in the form.</exception>
        static object? Stored(org.apache.calcite.DataContext root, CosmosDynamicValue slot, Metadata.CosmosRepresentation form)
        {
            var value = root.get(slot.VariableName);

            DateTime instant;

            try
            {
                switch (value)
                {
                    case null:
                        return null;
                    case java.lang.Long millis:
                        instant = DateTimeOffset.FromUnixTimeMilliseconds(millis.longValue()).UtcDateTime;
                        break;
                    case java.lang.Integer days:
                        instant = DateTime.SpecifyKind(DateTime.UnixEpoch.AddDays(days.intValue()), DateTimeKind.Utc);
                        break;
                    default:
                        throw new CosmosExecutionException($"Parameter {slot.VariableName} is compared with a path stored as '{form.Name}', and arrived as '{value.GetType().FullName}' rather than as an instant.");
                }

                return Metadata.CosmosStoredForms.RenderDateTime(form, instant, slot.Rounding)
                    ?? throw new CosmosExecutionException($"Parameter {slot.VariableName} cannot be written in the stored form '{form.Name}'.");
            }
            catch (ArgumentOutOfRangeException e)
            {
                throw new CosmosExecutionException($"Parameter {slot.VariableName} is outside the range of the stored form '{form.Name}'.", e);
            }
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
        /// <para>
        /// Into the same shapes <c>CosmosRexTranslator.GetLiteralValue</c> produces, so that a value
        /// arriving late is indistinguishable from one written into the statement: every integer width
        /// as a <c>long</c>, every approximate one as a <c>double</c>. A parameter that bound
        /// differently from a literal of the same type would be a second convention for the same
        /// thing, and the service would be the one to notice.
        /// </para>
        /// <para>
        /// <b>A geography is the one type whose constant form need not be a bound value</b>, so the
        /// agreement has to be reached rather than inherited. The constructor over a literal is
        /// written into the SQL where the call stood — a geography in a Cosmos statement <em>is</em> a
        /// GeoJSON object, see <c>CosmosRexTranslator.WriteGeographyLiteral</c> — while a parameter's
        /// value arrives with the execution and is bound, <see cref="CosmosJson.ToGeoJsonValue"/>
        /// making what is bound the same object the constant form inlines. A constant a connection
        /// has already folded into a <c>GEOMETRY</c> literal is bound the same way, by
        /// <c>GetLiteralValue</c>.
        /// </para>
        /// <para>
        /// <b>Left alone it was the geometry itself</b>, and the SDK's serializer wrote the IKVM
        /// object graph — <c>$0</c>-keyed, assembly-qualified, three kilobytes for a point — which the
        /// service got in place of a shape (#154). Switching on the value's class rather than on a
        /// declared type is what every case here does, and is right for the same reason: what the
        /// context hands over is a Java object, and which one it is decides what it means.
        /// </para>
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
                org.locationtech.jts.geom.Geometry g => CosmosJson.ToGeoJsonValue(g),
                var other => other,
            };
        }

    }

}
