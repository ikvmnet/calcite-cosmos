using System;

using Apache.Calcite.Cosmos.Adapter.Internal;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Reads a Calcite literal as the CLR value the service is sent and compares as that value.
    /// </summary>
    /// <remarks>
    /// The translator's literal readers, moved so that the loader, the printer and the parameter binding read a literal
    /// as the translator binds it without building one. See DESIGN.md, <i>Verified against the emulator</i> and <i>The
    /// Rex readers, apart from the translator</i>.
    /// </remarks>
    internal static class CosmosRexLiterals
    {

        /// <summary>
        /// Converts a Calcite literal into the CLR value to bind.
        /// </summary>
        /// <remarks>
        /// Temporal literals are refused, Cosmos JSON having no date or time type, and so is an exact number the service would
        /// read as another number (<see cref="TryGetExactNumberValue"/>). A geometry is bound as the GeoJSON object a geometry
        /// parameter is (#156).
        /// </remarks>
        /// <exception cref="CosmosTranslationException">The literal has no value the service can be sent.</exception>
        internal static object? GetLiteralValue(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();

            if (type == SqlTypeName.BOOLEAN)
                return ((java.lang.Boolean)literal.getValue()).booleanValue();

            if (type == SqlTypeName.CHAR || type == SqlTypeName.VARCHAR)
                return literal.getValue() is NlsString s ? s.getValue() : literal.getValue()?.ToString();

            // Calcite is not consistent about the boxed representation of numeric literals:
            // exact literals arrive as BigDecimal, while those built through makeApproxLiteral
            // arrive as java.lang.Double. Accept either rather than assuming one.
            if (type == SqlTypeName.TINYINT || type == SqlTypeName.SMALLINT || type == SqlTypeName.INTEGER || type == SqlTypeName.BIGINT)
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal bd => bd.longValueExact(),
                    java.lang.Long l => l.longValue(),
                    java.lang.Integer i => (long)i.intValue(),
                    var v => throw new CosmosTranslationException($"Unexpected representation '{v?.GetType().Name}' for an integer literal."),
                };
            }

            if (type == SqlTypeName.DECIMAL)
                return TryGetExactNumberValue((java.math.BigDecimal)literal.getValue())
                    ?? throw new CosmosTranslationException($"No value the service can be sent compares there as the number {literal.getValue()}.");

            if (type == SqlTypeName.FLOAT || type == SqlTypeName.REAL || type == SqlTypeName.DOUBLE)
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal bd => bd.doubleValue(),
                    java.lang.Double d => d.doubleValue(),
                    java.lang.Float f => (double)f.floatValue(),
                    var v => throw new CosmosTranslationException($"Unexpected representation '{v?.GetType().Name}' for an approximate literal."),
                };
            }

            // A geography the plan already holds, which is what a connection makes of a constant one:
            // its reduction evaluates CLR_ST_GEOG_GEOMFROMGEOJSON('…') while planning and leaves the
            // shape as a literal, where a bare planner leaves the call for WriteGeographyLiteral. Bound
            // as the GeoJSON object a geometry parameter is bound as (#156), so the folded constant and
            // the bound one reach the service in one spelling.
            if (type == SqlTypeName.GEOMETRY && literal.getValue() is org.locationtech.jts.geom.Geometry geometry)
                return Client.CosmosJson.ToGeoJsonValue(geometry);

            throw new CosmosTranslationException($"Unsupported literal type '{type.getName()}'.");
        }

        /// <summary>
        /// Converts an exact number into the CLR value the service compares as that number, or answers <c>null</c> where
        /// there is none.
        /// </summary>
        /// <remarks>
        /// A decimal reaches the service as a double, so it is sent wherever its double is right; a whole number whose double
        /// is another whole number is sent as a <c>long</c>, and a fraction or a number beyond a <c>long</c> as nothing. See
        /// DESIGN.md, <i>Verified against the emulator</i>.
        /// </remarks>
        /// <param name="value">The number.</param>
        /// <returns>A <see cref="decimal"/> or a <see cref="long"/>, or <c>null</c>.</returns>
        internal static object? TryGetExactNumberValue(java.math.BigDecimal value)
        {
            var approximate = value.doubleValue();

            if (double.IsInfinity(approximate))
                return null;

            try
            {
                if (Math.Floor(approximate) != approximate || new java.math.BigDecimal(approximate).compareTo(value) == 0)
                    return BigDecimalConverter.ToDecimal(value);

                return value.longValueExact();
            }
            catch (Exception e) when (e is java.lang.ArithmeticException or OverflowException)
            {
                // A fraction or a number beyond a long, which longValueExact refuses; or one beyond a decimal.
                return null;
            }
        }

        /// <summary>
        /// Returns the value a literal binds as a JSON scalar, or <c>null</c> for one the statement cannot bind.
        /// </summary>
        internal static JsonScalar? TryLiteralScalar(RexLiteral literal)
        {
            try
            {
                return JsonScalar.TryFrom(GetLiteralValue(literal), out var value) ? value : null;
            }
            catch (CosmosTranslationException)
            {
                return null;
            }
        }

    }

}
