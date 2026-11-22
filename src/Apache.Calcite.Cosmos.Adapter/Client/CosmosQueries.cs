using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Internal;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.runtime;
using org.apache.calcite.sql.type;

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
            {
                if (parameters[i].Value is not CosmosDynamicValue slot)
                {
                    bound[i] = parameters[i];
                    continue;
                }

                bound[i] = parameters[i] with { Value = slot.Presence ? Present(root, slot) : slot.Form is Metadata.CosmosRepresentation form ? Stored(root, slot, form) : slot.Bound != CosmosAnchorBound.None ? Bounded(root, slot) : Value(root, slot) };

                // In the stored spelling of the target, the emulator's, where the service compares the value with a path
                // (Service.EmulatorComparesTheSpellingItIsSent): after Held's truncation, Calcite truncating what it was
                // given, and before the encoder is asked, which the spelling cannot change the answer of.
                // A list or an object holding either character is not sent: its elements are not spelled, and the
                // statement fails rather than compare their raw spelling with a stored one.
                if (bound[i].Value is string text && IsStored(query, parameters[i].Name))
                    bound[i] = bound[i] with { Value = Facts.EmulatorStoredText.Of(text) };
                else if (IsStored(query, parameters[i].Name) && Facts.EmulatorStoredText.Escapes(bound[i].Value))
                    throw new CosmosExecutionException($"Parameter {slot.VariableName} holds U+0000 or U+FDD0 inside a list or an object, which the emulator stores escaped and the statement does not spell, so it cannot be sent there.");

                // A value the SDK cannot send as itself (CosmosEncodable): its UTF-8 encoder refuses an unpaired surrogate
                // while it writes the statement, unnamed and before anything is sent (Service.LoneSurrogateParameterIsNeverSent),
                // and its serializer writes a NaN or an infinity as a JSON string, which the service reads as a string
                // wherever the statement reads the parameter. A literal holding either is refused when the plan is made,
                // and computed in process; a parameter's value arrives only now, and a column computed from parameters
                // alone is never sent, so what is left is a value the service had to read, and the statement cannot run
                // as it was planned. For a NaN or an infinity that is a bridge: Calcite's conversion has refused one bound
                // to a numeric parameter, and what is left is a finite value it carries past an approximate type's range,
                // which Calcite compares and projects, or one an ANY or a VARIANT parameter holds as it came, over which
                // Calcite may answer, its answer against an ANY value unmeasured. The statement fails where Calcite may
                // answer, until the request decision reads a bound value (TODO.md).
                switch (CosmosEncodable.Unencodable(bound[i].Value))
                {
                    case string:
                        throw new CosmosExecutionException($"Parameter {slot.VariableName} holds a lone surrogate, which the Cosmos DB SDK cannot encode as UTF-8, so the statement cannot be sent; a string holding a surrogate pair, or none, can.");
                    case double number:
                        throw new CosmosExecutionException($"Parameter {slot.VariableName} holds {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}, which the Cosmos DB SDK writes as a JSON string rather than a number, so the statement cannot be sent; a finite number can.");
                }
            }

            return query with { Parameters = bound };
        }

        /// <summary>
        /// Reads a string parameter's value and writes the bound its comparison's fallback compares against in its place.
        /// </summary>
        /// <remarks>
        /// Written whatever the value: the reader decides from the value itself whether the fallback is read at all, and
        /// a bound for one that is an anchor is never sent. Anything but a string — a null — has no bound, and its
        /// comparison is sent as it is: where its operator would keep a row for the JSON null a SQL NULL arrives as,
        /// the statement tests the parameter beside it (<see cref="Sql.CosmosRexTranslator"/>'s
        /// <c>WriteComparison</c>), so it keeps nothing, as Calcite's does.
        /// </remarks>
        /// <param name="root">The data context.</param>
        /// <param name="slot">The ordinal to read, and which bound to write.</param>
        /// <returns>The bound, or <c>null</c>.</returns>
        static object? Bounded(org.apache.calcite.DataContext root, CosmosDynamicValue slot) => Value(root, slot) switch
        {
            string value when slot.Bound == CosmosAnchorBound.Lower => Metadata.CosmosServiceOrder.LowerBound(value),
            string value => Metadata.CosmosServiceOrder.UpperBound(value),
            _ => null,
        };

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
        /// <b>It is converted first, as every value is</b> (<see cref="Converted"/>): Calcite casts a
        /// <c>TIMESTAMP</c> parameter to <c>java.lang.Long</c>, a <c>DATE</c> one to
        /// <c>java.lang.Integer</c> and a <c>UUID</c> one to <c>UuidValue</c>, so a value of another
        /// class, which Calcite's own driver hands through, fails by name, as Calcite's plan fails reading
        /// it. Those are the only types whose slots take a stored form, each carrying its type
        /// (<c>TryWriteStoredInstantParameter</c>, <c>TryWriteStoredUuidParameter</c>), so the value read here is
        /// of the class its type is held in.
        /// </para>
        /// <para>
        /// <b>A null is bound as null</b>, which is what a parameter of any other kind binds — and
        /// that the service compares a JSON null as a value where SQL compares nothing is answered
        /// for every parameter alike, by the test of it the statement carries beside each comparison
        /// whose operator would keep a row for one.
        /// </para>
        /// </remarks>
        /// <param name="root">The data context.</param>
        /// <param name="slot">The ordinal to read, and how it is rounded.</param>
        /// <param name="form">The stored form to write it in.</param>
        /// <returns>The bound value.</returns>
        /// <exception cref="CosmosExecutionException">The value is not of its type, or cannot be written in the form.</exception>
        static object? Stored(org.apache.calcite.DataContext root, CosmosDynamicValue slot, Metadata.CosmosRepresentation form)
        {
            var value = Converted(root.get(slot.VariableName), slot);

            if (value is null)
                return null;

            if (Metadata.CosmosUuidForms.IsUuid(form))
                return StoredUuid(slot, form, (org.apache.calcite.util.UuidValue)value);

            try
            {
                var instant = value is java.lang.Long millis
                    ? DateTimeOffset.FromUnixTimeMilliseconds(millis.longValue()).UtcDateTime
                    : DateTime.SpecifyKind(DateTime.UnixEpoch.AddDays(((java.lang.Integer)value).intValue()), DateTimeKind.Utc);

                return Metadata.CosmosStoredForms.RenderDateTime(form, instant, slot.Rounding)
                    ?? throw new CosmosExecutionException($"Parameter {slot.VariableName} cannot be written in the stored form '{form.Name}'.");
            }
            catch (ArgumentOutOfRangeException e)
            {
                throw new CosmosExecutionException($"Parameter {slot.VariableName} is outside the range of the stored form '{form.Name}'.", e);
            }
        }

        /// <summary>
        /// Writes a <c>UUID</c> parameter's value in the stored spelling of the path it is compared with.
        /// </summary>
        /// <remarks>
        /// Calcite holds a <c>UUID</c> as <c>UuidValue</c>, which the slot's type has converted the value to.
        /// </remarks>
        /// <param name="slot">The ordinal the value was read from.</param>
        /// <param name="form">The stored form to write it in.</param>
        /// <param name="value">The value, converted to its type.</param>
        /// <returns>The bound value.</returns>
        /// <exception cref="CosmosExecutionException">The value cannot be written in the form.</exception>
        static object? StoredUuid(CosmosDynamicValue slot, Metadata.CosmosRepresentation form, org.apache.calcite.util.UuidValue value)
        {
            return Metadata.CosmosUuidForms.Spell(form, Guid.Parse(value.toString()))
                ?? throw new CosmosExecutionException($"Parameter {slot.VariableName} cannot be written in the stored form '{form.Name}'.");
        }

        /// <summary>Whether a statement sends a parameter in its target's stored spelling (<see cref="CosmosQuery.Stored"/>).</summary>
        internal static bool IsStored(CosmosQuery query, string name)
        {
            if (query.Stored is not { } names)
                return false;

            for (var i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                    return true;

            return false;
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
        /// Reads whether one dynamic parameter holds a value: <c>true</c> where it does, <c>null</c> where it does not.
        /// </summary>
        static object? Present(org.apache.calcite.DataContext root, CosmosDynamicValue slot) =>
            root.get(slot.VariableName) is null ? null : true;

        /// <summary>
        /// Reads one dynamic parameter's value out of the data context, converted to the parameter's type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The value is first converted as Calcite converts it</b> (<see cref="Converted"/>): what the data context
        /// holds is the caller's class through Calcite's own driver, and the value Calcite compares is that value of the
        /// parameter's type.
        /// </para>
        /// <para>
        /// Then into the same shapes <see cref="CosmosRexLiterals.GetLiteralValue"/> produces, so that a value
        /// arriving late is indistinguishable from one written into the statement: every integer width
        /// as a <c>long</c>, every approximate one as a <c>double</c>, and an exact one as
        /// <see cref="CosmosRexLiterals.TryGetExactNumberValue"/> sends it — a decimal, or a long
        /// where the decimal's double would be another whole number. A parameter that bound
        /// differently from a literal of the same type would be a second convention for the same
        /// thing, and the service would be the one to notice. One no value serves fails the
        /// execution, the plan having been made to send it, where the literal is declined.
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
        /// service got in place of a shape (#154). Once converted, the value is in the class Calcite holds
        /// its type in, so switching on that class is switching on the type, and a slot made without a type,
        /// or of one Calcite reads as it stands, is shaped by the class it arrived in.
        /// </para>
        /// </remarks>
        /// <param name="root">The data context.</param>
        /// <param name="value">The ordinal to read, and the type it is converted to.</param>
        /// <returns>The bound value.</returns>
        /// <exception cref="CosmosExecutionException">Calcite cannot convert the value to the parameter's type.</exception>
        static object? Value(org.apache.calcite.DataContext root, CosmosDynamicValue value)
        {
            return Converted(root.get(value.VariableName), value) switch
            {
                string text when value.Length is int length => Held(text, length, value.Padded),
                null => null,
                java.lang.Byte b => (long)unchecked((sbyte)b.byteValue()),
                java.lang.Short s => (long)s.shortValue(),
                java.lang.Integer i => (long)i.intValue(),
                java.lang.Long l => l.longValue(),
                java.lang.Float f => (double)f.floatValue(),
                java.lang.Double d => d.doubleValue(),
                java.math.BigDecimal d => CosmosRexLiterals.TryGetExactNumberValue(d)
                    ?? throw new CosmosExecutionException($"Parameter {value.VariableName} is the number {d}, which no value the service can be sent compares there as."),
                org.joou.UNumber u => CosmosRexLiterals.TryGetExactNumberValue(new java.math.BigDecimal(u.toBigInteger()))
                    ?? throw new CosmosExecutionException($"Parameter {value.VariableName} is the number {u}, which no value the service can be sent compares there as."),
                java.lang.Boolean b => b.booleanValue(),
                org.locationtech.jts.geom.Geometry g => CosmosJson.ToGeoJsonValue(g),
                var other => other,
            };
        }

        /// <summary>
        /// Converts the value the data context holds for a parameter to the parameter's type, as Calcite's generated code
        /// converts it when a row reads the parameter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Calcite's own driver hands the caller's class through, and Calcite converts it when the row's code reads
        /// it</b> (<c>Calcite.AParameterIsConvertedToItsTypeWhenRead</c>): a long bound to a <c>DOUBLE</c> parameter is
        /// compared as its double, past 2^53 another number; a long bound to an <c>INTEGER</c> one wraps; a double bound to
        /// a <c>BIGINT</c> one is cut to its whole part; a number bound to a text one is compared as its text, and a string
        /// bound to a number as the number it spells. The ADO.NET driver converts each value to the placeholder's type
        /// before Calcite sees it, so through it the conversion finds the type's own class and changes nothing. Sent as the
        /// data context held it, the value was compared by the service as another value than Calcite's.
        /// </para>
        /// <para>
        /// <b>The dispatch is <c>RexToLixTranslator.visitDynamicParam</c>'s, over Calcite's own list and classes.</b> A
        /// type in Calcite's numeric family (<c>SqlTypeName.NUMERIC_TYPES</c>) is read through <c>java.lang.Number</c>,
        /// which <c>EnumUtils.convert</c> writes as <c>SqlFunctions.toBigDecimal(Object)</c> and then the conversion to the
        /// class Calcite holds the type in: the box's own <c>…Value()</c>, as linq4j's <c>Primitive.number</c> converts it,
        /// the decimal itself, or <c>UnsignedType</c>'s for an unsigned type. Any other type is read as the class Calcite holds it in, as its own type factory answers
        /// (<c>JavaTypeFactoryImpl.getJavaClass</c>): a <c>String</c> through the object's <c>toString()</c>, any other
        /// class through a cast, which <c>Object</c>, an <c>ANY</c>'s or a <c>VARIANT</c>'s class, passes as it is. The
        /// leaf conversions and the classes are Calcite's, called; the dispatch between them is written here, and the
        /// measurement, and a test against the code Calcite generates for a parameter of each type
        /// (<c>CosmosQueriesTests.TheConversionIsTheCodeCalciteGeneratesForAParameter</c>), hold it to Calcite's. A parameter's type is always nullable, the validator making every one so,
        /// so the conversion is the boxed one: an integer past its type wraps, as <c>BigDecimal.intValue()</c> does,
        /// rather than raise as the unboxed <c>intValueExact()</c> would.
        /// </para>
        /// <para>
        /// <b>A value Calcite cannot convert fails the statement by name</b>, as Calcite's own plan fails reading it: a
        /// string that spells no number bound to a numeric parameter, a NaN or an infinite double bound to any numeric one
        /// (<c>toBigDecimal</c> refuses both), and a value of another class than its type's bound to a parameter of a type
        /// Calcite casts to, a string bound to a <c>BOOLEAN</c> or a <c>UUID</c> one. Calcite fails only once a row reaches
        /// the parameter's reading, and answers where none does: no rows over an input that holds none, and what the other
        /// arms decide where no row takes the <c>CASE</c> arm reading it. The bind fails whatever the input holds
        /// (TODO.md).
        /// </para>
        /// </remarks>
        /// <param name="held">The value the data context holds, or <c>null</c>.</param>
        /// <param name="slot">The parameter, and the type it is converted to.</param>
        /// <returns>The value of the parameter's type, in the class Calcite holds the type in; <c>null</c> for <c>null</c>.</returns>
        /// <exception cref="CosmosExecutionException">Calcite cannot convert the value to the type.</exception>
        internal static object? Converted(object? held, CosmosDynamicValue slot)
        {
            if (held is null || slot.TypeName is not SqlTypeName type)
                return held;

            var boxed = JavaClassOf(type);

            try
            {
                if (SqlTypeName.NUMERIC_TYPES.contains(type))
                {
                    var number = SqlFunctions.toBigDecimal(held);
                    if (org.apache.calcite.linq4j.tree.Primitive.ofBox(boxed) is { } primitive)
                        return primitive.number(number);

                    return boxed switch
                    {
                        _ when boxed == (java.lang.Class)typeof(org.joou.UByte) => org.apache.calcite.linq4j.tree.UnsignedType.toUByte(number),
                        _ when boxed == (java.lang.Class)typeof(org.joou.UShort) => org.apache.calcite.linq4j.tree.UnsignedType.toUShort(number),
                        _ when boxed == (java.lang.Class)typeof(org.joou.UInteger) => org.apache.calcite.linq4j.tree.UnsignedType.toUInteger(number),
                        _ when boxed == (java.lang.Class)typeof(org.joou.ULong) => org.apache.calcite.linq4j.tree.UnsignedType.toULong(number),
                        _ => number,
                    };
                }

                return boxed == (java.lang.Class)typeof(string) ? java.lang.String.valueOf(held) : boxed.cast(held);
            }
            catch (Exception e) when (e is java.lang.RuntimeException or InvalidCastException)
            {
                throw new CosmosExecutionException($"Parameter {slot.VariableName} cannot be converted to its type, {type.getName()}, as Calcite converts it when a row reads it ({e.GetType().Name}: {e.Message}), so the statement fails as Calcite's own plan fails.", e);
            }
        }

        /// <summary>
        /// Answers the class Calcite holds a parameter of a type in: its own type factory's answer for the nullable type,
        /// the validator making every parameter's type nullable.
        /// </summary>
        /// <param name="type">The parameter's type.</param>
        /// <returns>The class.</returns>
        static java.lang.Class JavaClassOf(SqlTypeName type)
        {
            var factory = new org.apache.calcite.jdbc.JavaTypeFactoryImpl();
            return (java.lang.Class)factory.getJavaClass(factory.createTypeWithNullability(factory.createSqlType(type), true));
        }

        /// <summary>
        /// Holds a string to the length its parameter's type holds, as Calcite does: truncated by code unit, and padded
        /// with spaces where the type is a <c>CHAR</c> (<c>SqlFunctions.truncate</c> and <c>truncateOrPad</c>).
        /// </summary>
        /// <param name="text">The value.</param>
        /// <param name="length">The type's length.</param>
        /// <param name="padded">Whether a shorter value is padded.</param>
        /// <returns>The value as Calcite reads it.</returns>
        static string Held(string text, int length, bool padded) =>
            text.Length > length ? text.Substring(0, length) : padded ? text.PadRight(length) : text;

    }

}
