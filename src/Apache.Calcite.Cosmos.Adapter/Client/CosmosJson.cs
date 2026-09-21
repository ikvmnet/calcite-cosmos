using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Reads the JSON value Cosmos returns for a row into the representation Calcite holds a value of
    /// that SQL type in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are Java boxes rather than CLR primitives — <see cref="java.lang.Integer"/>, not
    /// <see cref="int"/> — because everything above this reads them through Calcite's own operators,
    /// which are compiled Java. A CLR <see cref="int"/> in a row would compile and then fail at the
    /// first operator that casts. Calcite's internal encodings apply too: a <c>DATE</c> is a day count
    /// and a <c>TIME</c> a millisecond-of-day, both as <see cref="java.lang.Integer"/>.
    /// </para>
    /// <para>
    /// A property Cosmos does not return is absent from the object rather than present and null — the
    /// service elides <c>undefined</c>. Both read as SQL <c>NULL</c> here, which is the only available
    /// reading: the document column aside, nothing in this adapter can distinguish a missing property from a
    /// null one, and SQL has no third value to distinguish them with.
    /// </para>
    /// </remarks>
    public static class CosmosJson
    {

        /// <summary>
        /// The day <see cref="SqlTypeName.DATE"/> counts from.
        /// </summary>
        static readonly DateOnly UnixEpochDay = new(1970, 1, 1);

        /// <summary>
        /// Reads a named property of a row as a value of the given SQL type.
        /// </summary>
        /// <param name="row">The JSON value Cosmos returned for the row.</param>
        /// <param name="name">The property to read.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <returns>The value, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The value cannot be read as that type.</exception>
        public static object? GetProperty(JsonElement row, string name, SqlTypeName typeName)
        {
            return GetProperty(row, name, typeName, null);
        }

        /// <inheritdoc cref="GetProperty(JsonElement, string, SqlTypeName)" />
        /// <param name="row">The JSON value Cosmos returned for the row.</param>
        /// <param name="name">The property to read.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <param name="componentTypeName">
        /// The element type, where <paramref name="typeName"/> is a collection. See
        /// <see cref="GetList(JsonElement, SqlTypeName?)"/> for what it buys.
        /// </param>
        public static object? GetProperty(JsonElement row, string name, SqlTypeName typeName, SqlTypeName? componentTypeName)
        {
            // A row that is not an object has no properties to name. That is not reachable through the
            // converters, which always project an object, but it is worth failing loudly rather than
            // silently yielding a row of nulls if one ever stops doing so.
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            return row.TryGetProperty(name, out var value) ? GetValue(value, typeName, componentTypeName) : null;
        }

        /// <summary>
        /// Reads a named property of a row as the text Calcite's cast over an <c>ANY</c> value renders
        /// it as.
        /// </summary>
        /// <remarks>
        /// What a projection that dropped a <c>CAST(&#8230; AS VARCHAR)</c> reads back with — see
        /// <see cref="Sql.CosmosRexTranslator.TryRenderedTextOperand"/>. Not a coercion of a typed
        /// column: <see cref="GetValue"/> still refuses to read a number as <c>VARCHAR</c>, and this is
        /// reached only where the plan asked for a rendering rather than for a value of a declared type.
        /// </remarks>
        /// <param name="row">The JSON value Cosmos returned for the row.</param>
        /// <param name="name">The property to read.</param>
        /// <returns>The rendered text, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The row is not a JSON object.</exception>
        public static string? GetTextProperty(JsonElement row, string name)
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            return row.TryGetProperty(name, out var value) ? GetText(value) : null;
        }

        /// <summary>
        /// Reads a JSON value as the text Calcite's cast over an <c>ANY</c> value renders it as.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Java's rendering, and that is the whole of the equivalence.</b> Calcite holds an
        /// <c>ANY</c> value in the boxes <see cref="GetNatural"/> builds, and its cast to <c>VARCHAR</c>
        /// is those boxes' own <c>toString</c>. Measured over one document per JSON type, against the
        /// in-process plan:
        /// </para>
        /// <list type="table">
        /// <item><description><c>"bikes"</c> &#8594; <c>bikes</c>; <c>30</c> &#8594; <c>30</c>;
        /// <c>30.7</c> &#8594; <c>30.7</c>; <c>1e30</c> &#8594; <c>1.0E30</c></description></item>
        /// <item><description><c>true</c> &#8594; <c>true</c>; <c>["bikes"]</c> &#8594; <c>[bikes]</c>;
        /// <c>{"x":1}</c> &#8594; <c>{x=1}</c></description></item>
        /// <item><description>JSON null and an absent property &#8594; SQL <c>NULL</c></description></item>
        /// </list>
        /// <para>
        /// So the rendering is not reproduced here, it is delegated: the value is built as Calcite would
        /// have received it and rendered by the same code that would have rendered it. A stored string
        /// is returned as itself rather than through <c>toString</c>, which is the same answer and skips
        /// a round trip through the bridge.
        /// </para>
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <returns>The rendered text, or <c>null</c> where the value is JSON null or undefined.</returns>
        public static string? GetText(JsonElement value)
        {
            return GetNatural(value) switch
            {
                null => null,
                string text => text,
                var natural => natural.ToString(),
            };
        }

        /// <summary>
        /// Reads a named property as the JSON text the service sent for it.
        /// </summary>
        /// <remarks>
        /// The document as it arrived, not a re-serialisation of a parsed form: <c>GetRawText</c>
        /// returns the original span. So the <c>DOC</c> column costs a copy rather than a round trip,
        /// and cannot differ from what is stored — key order, number formatting and all.
        /// </remarks>
        /// <param name="row">The row object.</param>
        /// <param name="name">The property to read.</param>
        /// <returns>The JSON text, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The row is not an object.</exception>
        public static string? GetJsonProperty(JsonElement row, string name)
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            if (row.TryGetProperty(name, out var value) == false || value.ValueKind == JsonValueKind.Null)
                return null;

            return value.GetRawText();
        }

        /// <summary>
        /// Reads a named property as compact JSON text, which is what <c>JSON_QUERY</c> answers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Re-serialised rather than handed over, which is the whole difference from
        /// <see cref="GetJsonProperty"/>. A document may be stored with whitespace between its
        /// tokens; Calcite's own <c>JSON_QUERY</c> parses and writes the fragment back, and measured,
        /// answers <c>["a","b"]</c> for a path holding <c>[ "a" ,   "b" ]</c>. Writing it out here
        /// the same way is what keeps the pushed column and the in-process one the same string.
        /// </para>
        /// <para>
        /// A scalar at the path is not this operator's business and answers null — the guard renders
        /// that at the service, and this is the reading for what the guard admits.
        /// </para>
        /// </remarks>
        /// <param name="row">The row object.</param>
        /// <param name="name">The property to read.</param>
        /// <returns>The JSON text, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The row is not an object.</exception>
        public static string? GetJsonTextProperty(JsonElement row, string name)
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            if (row.TryGetProperty(name, out var value) == false || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            using var buffer = new System.IO.MemoryStream();

            // Default options, so no indentation: the compact form Calcite writes.
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
                value.WriteTo(writer);

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// Reads a document path as a value of the given SQL type.
        /// </summary>
        /// <remarks>
        /// What a point read needs, and what the projected form does not: the row is the document
        /// itself, so a field is reached by walking its path rather than by naming a property of a
        /// constructed object. An empty path is the document — the document column.
        /// <para>
        /// A segment that is not there reads as SQL <c>NULL</c>, exactly as an absent property does in
        /// the projected form, because both are Cosmos returning nothing for that path.
        /// </para>
        /// </remarks>
        /// <param name="document">The document.</param>
        /// <param name="segments">The path's segments, relative to the document root.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <returns>The value, or <c>null</c> where the path is absent or JSON null.</returns>
        public static object? GetPath(JsonElement document, IReadOnlyList<CosmosPathSegment> segments, SqlTypeName typeName)
        {
            return GetPath(document, segments, typeName, null);
        }

        /// <inheritdoc cref="GetPath(JsonElement, IReadOnlyList{CosmosPathSegment}, SqlTypeName)" />
        /// <param name="document">The document.</param>
        /// <param name="segments">The path's segments, relative to the document root.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <param name="componentTypeName">
        /// The element type, where <paramref name="typeName"/> is a collection. Carried so that a
        /// point read and a query read a collection column identically, which is the whole reason the
        /// two readings are written against the same functions.
        /// </param>
        public static object? GetPath(JsonElement document, IReadOnlyList<CosmosPathSegment> segments, SqlTypeName typeName, SqlTypeName? componentTypeName)
        {
            if (segments is null)
                throw new ArgumentNullException(nameof(segments));

            var value = document;

            foreach (var segment in segments)
            {
                if (segment.IsIndex)
                {
                    if (value.ValueKind != JsonValueKind.Array || segment.ArrayIndex >= value.GetArrayLength())
                        return null;

                    value = value[segment.ArrayIndex];
                    continue;
                }

                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment.Name!, out value) == false)
                    return null;
            }

            return GetValue(value, typeName, componentTypeName);
        }

        /// <summary>
        /// Reads a JSON value as a value of the given SQL type.
        /// </summary>
        /// <param name="value">The value to read.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <returns>The value, or <c>null</c> where the value is JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The value cannot be read as that type.</exception>
        public static object? GetValue(JsonElement value, SqlTypeName typeName)
        {
            return GetValue(value, typeName, null);
        }

        /// <inheritdoc cref="GetValue(JsonElement, SqlTypeName)" />
        /// <param name="value">The value to read.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <param name="componentTypeName">
        /// The element type, where <paramref name="typeName"/> is a collection, and <c>null</c> where
        /// the caller has none to give. See <see cref="GetList(JsonElement, SqlTypeName?)"/>.
        /// </param>
        public static object? GetValue(JsonElement value, SqlTypeName typeName, SqlTypeName? componentTypeName)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            // Dispatched on the name: a Java enum's ordinals are not stable across versions, its names are.
            switch (typeName?.name())
            {
                case nameof(SqlTypeName.NULL):
                    return null;
                case nameof(SqlTypeName.BOOLEAN):
                    return java.lang.Boolean.valueOf(GetBoolean(value));
                case nameof(SqlTypeName.TINYINT):
                    // A Java byte is signed and IKVM surfaces it as a CLR byte, so the range is checked
                    // against sbyte and the bit pattern reinterpreted rather than clamped.
                    return java.lang.Byte.valueOf(unchecked((byte)(sbyte)GetInteger(value, sbyte.MinValue, sbyte.MaxValue, typeName)));
                case nameof(SqlTypeName.SMALLINT):
                    return java.lang.Short.valueOf((short)GetInteger(value, short.MinValue, short.MaxValue, typeName));
                case nameof(SqlTypeName.INTEGER):
                    return java.lang.Integer.valueOf((int)GetInteger(value, int.MinValue, int.MaxValue, typeName));
                case nameof(SqlTypeName.BIGINT):
                    return java.lang.Long.valueOf(GetInteger(value, long.MinValue, long.MaxValue, typeName));
                case nameof(SqlTypeName.REAL):
                    return java.lang.Float.valueOf((float)GetDouble(value));
                case nameof(SqlTypeName.FLOAT):
                case nameof(SqlTypeName.DOUBLE):
                    return java.lang.Double.valueOf(GetDouble(value));
                case nameof(SqlTypeName.DECIMAL):
                    return GetDecimal(value);
                case nameof(SqlTypeName.CHAR):
                case nameof(SqlTypeName.VARCHAR):
                    return GetString(value);
                case nameof(SqlTypeName.UUID):
                    return GetUuid(value);
                case nameof(SqlTypeName.GEOMETRY):
                    return GetGeography(value);
                case nameof(SqlTypeName.BINARY):
                case nameof(SqlTypeName.VARBINARY):
                    return GetBinary(value);
                case nameof(SqlTypeName.DATE):
                    return java.lang.Integer.valueOf(GetDate(value));
                case nameof(SqlTypeName.TIME):
                    return java.lang.Integer.valueOf(GetTime(value));
                case nameof(SqlTypeName.TIMESTAMP):
                case nameof(SqlTypeName.TIMESTAMP_TZ):
                    return java.lang.Long.valueOf(GetTimestamp(value));
                case nameof(SqlTypeName.MAP):
                    return GetMap(value);
                case nameof(SqlTypeName.ARRAY):
                case nameof(SqlTypeName.MULTISET):
                    return GetList(value, componentTypeName);
                case nameof(SqlTypeName.ANY):
                case nameof(SqlTypeName.OTHER):
                    return GetNatural(value);
                default:
                    throw new CosmosMaterializationException(NoReadingMessage(typeName));
            }
        }

        /// <summary>
        /// The message a type with no reading fails with, in one place because two things say it.
        /// </summary>
        /// <remarks>
        /// <see cref="GetValue(JsonElement, SqlTypeName, SqlTypeName?)"/> throws it and
        /// <c>CosmosJsonTests.EveryTypeAgreesWithWhatCanReadSays</c> recognises it, which is what
        /// makes <see cref="CanRead(SqlTypeName?, SqlTypeName?)"/> a claim about this switch rather
        /// than a second list beside it.
        /// </remarks>
        internal static string NoReadingMessage(SqlTypeName? typeName) =>
            $"No Cosmos JSON reading is defined for SQL type '{typeName?.name() ?? "null"}'.";

        /// <summary>
        /// Determines whether a value of the given SQL type can be read back at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The same switch, asked in advance.</b> Cosmos has no row schema, so the plan's type is
        /// the whole of what a <see cref="CosmosReading.Typed"/> column is read by; a type this has no
        /// case for is a column that renders, executes, and then fails at the first row. #149 is that,
        /// and what made it expensive is <em>when</em> it failed: over HTTP a caller had already had a
        /// <c>200</c>, the headers and part of the body by the time the reader was asked. So the
        /// question is asked while the plan is being made instead —
        /// <see cref="CosmosRexTranslator.TranslateProjection"/> declines to push such a column, and
        /// <see cref="CosmosImplementor.RequireReadableRow"/> refuses to build a reader for one.
        /// </para>
        /// <para>
        /// <b>It is a second list, and a test is what keeps it from becoming a different one.</b>
        /// Writing it as a switch beside the reader's rather than deriving it from one table is a
        /// choice: the reader is on the per-value path and a dictionary of delegates would price every
        /// row for a question asked once per statement. <c>EveryTypeAgreesWithWhatCanReadSays</c>
        /// walks <c>SqlTypeName.values()</c> and asserts the two answer alike, so a type added to one
        /// and not the other is reported here rather than in a caller's response body.
        /// </para>
        /// </remarks>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <param name="componentTypeName">
        /// The element type, where <paramref name="typeName"/> is a collection. A collection whose
        /// element type is not given is read naturally and needs nothing of this.
        /// </param>
        /// <returns><c>true</c> where a value of that type can be read.</returns>
        public static bool CanRead(SqlTypeName? typeName, SqlTypeName? componentTypeName = null)
        {
            // Dispatched on the name for the reason GetValue is: a Java enum's ordinals are not stable
            // across versions, its names are.
            var readable = typeName?.name() switch
            {
                nameof(SqlTypeName.NULL) => true,
                nameof(SqlTypeName.BOOLEAN) => true,
                nameof(SqlTypeName.TINYINT) => true,
                nameof(SqlTypeName.SMALLINT) => true,
                nameof(SqlTypeName.INTEGER) => true,
                nameof(SqlTypeName.BIGINT) => true,
                nameof(SqlTypeName.REAL) => true,
                nameof(SqlTypeName.FLOAT) => true,
                nameof(SqlTypeName.DOUBLE) => true,
                nameof(SqlTypeName.DECIMAL) => true,
                nameof(SqlTypeName.CHAR) => true,
                nameof(SqlTypeName.VARCHAR) => true,
                nameof(SqlTypeName.UUID) => true,
                nameof(SqlTypeName.GEOMETRY) => true,
                nameof(SqlTypeName.BINARY) => true,
                nameof(SqlTypeName.VARBINARY) => true,
                nameof(SqlTypeName.DATE) => true,
                nameof(SqlTypeName.TIME) => true,
                nameof(SqlTypeName.TIMESTAMP) => true,
                nameof(SqlTypeName.TIMESTAMP_TZ) => true,
                nameof(SqlTypeName.MAP) => true,
                nameof(SqlTypeName.ARRAY) => true,
                nameof(SqlTypeName.MULTISET) => true,
                nameof(SqlTypeName.ANY) => true,
                nameof(SqlTypeName.OTHER) => true,
                _ => false,
            };

            if (readable == false)
                return false;

            // A collection is read to its element type, so an unreadable element is an unreadable
            // column -- the failure would simply arrive one level down. Where no element type was
            // given the elements are read by their own JSON type and there is nothing to refuse.
            return componentTypeName is null || CanRead(componentTypeName);
        }

        /// <inheritdoc cref="CanRead(SqlTypeName?, SqlTypeName?)" />
        /// <param name="type">The column's type, whose element type is read off it.</param>
        public static bool CanRead(org.apache.calcite.rel.type.RelDataType? type) =>
            type is not null && CanRead(type.getSqlTypeName(), ComponentTypeNameOf(type));

        /// <summary>
        /// Returns the element type of a collection type, or <c>null</c> where the type is not one.
        /// </summary>
        /// <remarks>
        /// What tells <c>VARCHAR ARRAY</c> from <c>INTEGER ARRAY</c> at the point the value is read.
        /// The reader needs it because a JSON array carries no element type of its own — see
        /// <see cref="GetList(JsonElement, SqlTypeName?)"/> — and it lives here rather than with the
        /// row builder because it is a fact about how this takes its arguments.
        /// </remarks>
        /// <param name="type">The type.</param>
        /// <returns>The element type, or <c>null</c>.</returns>
        public static SqlTypeName? ComponentTypeNameOf(org.apache.calcite.rel.type.RelDataType? type)
        {
            var name = type?.getSqlTypeName();
            if (name != SqlTypeName.ARRAY && name != SqlTypeName.MULTISET)
                return null;

            return type!.getComponentType()?.getSqlTypeName();
        }

        /// <summary>
        /// Reads a value by its JSON type rather than by a declared SQL type.
        /// </summary>
        /// <remarks>
        /// What a <c>MAP</c>, an <c>ARRAY</c> and an <c>ANY</c> hold. A container has no row schema, so a
        /// document's shape is discovered here and nowhere earlier — which is exactly why these three
        /// types exist in the row model at all.
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <returns>The value.</returns>
        public static object? GetNatural(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.True:
                    return java.lang.Boolean.TRUE;
                case JsonValueKind.False:
                    return java.lang.Boolean.FALSE;
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Object:
                    return GetMap(value);
                case JsonValueKind.Array:
                    return GetList(value);
                case JsonValueKind.Number:
                    // JSON numbers are IEEE doubles in Cosmos, but an integral one arrives as a Long so
                    // that an identifier or a count does not surface as 42.0. The choice is the value's,
                    // not the schema's, there being no schema to consult.
                    return value.TryGetInt64(out var integral)
                        ? java.lang.Long.valueOf(integral)
                        : java.lang.Double.valueOf(value.GetDouble());
                default:
                    throw new CosmosMaterializationException($"Unexpected JSON value kind '{value.ValueKind}'.");
            }
        }

        /// <summary>
        /// Reads a JSON object as the <see cref="java.util.Map"/> a <c>MAP</c> or <c>ANY</c> value holds.
        /// </summary>
        /// <param name="value">The value to read.</param>
        /// <returns>The map.</returns>
        public static java.util.Map GetMap(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for a MAP, got {value.ValueKind}.");

            // Insertion-ordered, so that a map rendered back to text reads in document order.
            var map = new java.util.LinkedHashMap();

            foreach (var property in value.EnumerateObject())
                map.put(property.Name, GetNatural(property.Value));

            return map;
        }

        /// <summary>
        /// Reads a JSON array as the <see cref="java.util.List"/> an <c>ARRAY</c> column holds.
        /// </summary>
        /// <param name="value">The value to read.</param>
        /// <returns>The list.</returns>
        public static java.util.List GetList(JsonElement value)
        {
            return GetList(value, null);
        }

        /// <inheritdoc cref="GetList(JsonElement)" />
        /// <param name="value">The value to read.</param>
        /// <param name="componentTypeName">
        /// The element type the plan declared, or <c>null</c> to read each element by its own JSON
        /// type.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>The element type is read the same way the column's own is, and for the same reason.</b>
        /// A collection column carries a declared element type — <c>VARCHAR ARRAY</c> says the
        /// elements are strings — and reading them naturally instead would hand a
        /// <see cref="java.lang.Long"/> to a plan that declared <see cref="string"/>, which is the
        /// coercion <see cref="GetString"/> refuses for a scalar column. So a document that
        /// contradicts the declaration fails here rather than further down, which is the whole reason
        /// a <c>RETURNING</c> clause is worth trusting.
        /// </para>
        /// <para>
        /// <c>null</c> where the caller has no element type — an <c>ANY</c> value's own list, which
        /// has no schema to consult and is discovered element by element.
        /// </para>
        /// </remarks>
        public static java.util.List GetList(JsonElement value, SqlTypeName? componentTypeName)
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw new CosmosMaterializationException($"Expected a JSON array for an ARRAY, got {value.ValueKind}.");

            var list = new java.util.ArrayList(value.GetArrayLength());

            foreach (var element in value.EnumerateArray())
                list.add(componentTypeName is null ? GetNatural(element) : GetValue(element, componentTypeName));

            return list;
        }

        static bool GetBoolean(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CosmosMaterializationException($"Expected a JSON boolean, got {value.ValueKind}."),
        };

        static string GetString(JsonElement value)
        {
            // Only a JSON string reads as a character value. Cosmos is schemaless and a property may hold
            // a number where the plan expected text; coercing it would make the row type a suggestion.
            if (value.ValueKind != JsonValueKind.String)
                throw new CosmosMaterializationException($"Expected a JSON string, got {value.ValueKind}.");

            return value.GetString()!;
        }

        /// <summary>
        /// Reads a JSON string as the <c>org.apache.calcite.util.UuidValue</c> a <c>UUID</c> value
        /// holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The read side of a container's declared facts. Cosmos has no UUID type, so a column the
        /// plan types <c>UUID</c> is a string property whose spelling a declaration pinned; reading it
        /// back is the conversion Calcite's own <c>CAST(… AS UUID)</c> performs, and it is literally
        /// that function rather than a second implementation of it, so the pushed statement and the
        /// in-process plan cannot answer differently. <c>UuidValue.fromString</c> is that function:
        /// <c>BuiltInMethod.UUID_FROM_STRING</c> names it, so it is what a cast Calcite generated
        /// itself calls.
        /// </para>
        /// <para>
        /// <b>The box is <c>UuidValue</c>, and reading it as a bare <c>java.util.UUID</c> was only
        /// ever visible on a narrow row.</b> CALCITE-7716 moved the runtime representation of a
        /// <c>UUID</c> onto <c>UuidValue</c> — it is what <c>JavaTypeFactoryImpl</c> answers for the
        /// type, and what a <c>RexLiteral</c> of one holds — while leaving
        /// <c>SqlFunctions.stringToUuid</c> returning the <c>java.util.UUID</c> it always returned,
        /// which is the value the wrapper wraps. Reading through the inner function therefore handed
        /// back a class the plan does not use, and a wide row never noticed: two columns or more is
        /// an <c>object[]</c>, which boxes whatever it is given. One column <em>is</em> the value,
        /// and <see cref="Rel.Convert.CosmosConverters.RowBuilder"/> casts it to the physical type
        /// — so a projection of a lone UUID column threw <c>java.util.UUID cannot be cast to
        /// UuidValue</c> at the first row, while the same column read beside any other was fine
        /// (#150).
        /// </para>
        /// <para>
        /// A value that is not a string, or a string that is not a UUID, is a failure and not a null.
        /// The declaration said what the property holds and a document contradicting it is the one
        /// thing the fact model cannot check in advance, so the two readings agree here as well:
        /// Calcite's cast throws over the same values. Answering null instead would turn a wrong
        /// declaration into wrong rows, which is the trade this adapter refuses everywhere else.
        /// </para>
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <returns>The value.</returns>
        /// <exception cref="CosmosMaterializationException">The value is not a UUID.</exception>
        static org.apache.calcite.util.UuidValue GetUuid(JsonElement value)
        {
            var text = GetString(value);

            try
            {
                return org.apache.calcite.util.UuidValue.fromString(text);
            }
            catch (java.lang.IllegalArgumentException e)
            {
                throw new CosmosMaterializationException($"A value read as a UUID is not one: '{text}'.", e);
            }
        }

        /// <summary>
        /// Reads a JSON value as the <c>org.locationtech.jts.geom.Geometry</c> a <c>GEOMETRY</c> value
        /// holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The service sends the shape and the reader puts the constructor back.</b> No column is
        /// typed as a geometry, so a stored shape reaches an operator by being parsed out of the
        /// document — <c>CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location'))</c>. Pushed
        /// down the constructor disappears and the path is sent, because Cosmos reads the property
        /// itself as the shape; what comes back is therefore the GeoJSON object, and this is the half
        /// that makes the column materialize. Without it a projection carrying a geography planned,
        /// rendered, executed, and then threw at the first row (#149).
        /// </para>
        /// <para>
        /// <b>The conversion is the geography package's own.</b>
        /// <c>GeographyFunctions.FromGeoJson</c> is what <c>CLR_ST_GEOG_GEOMFROMGEOJSON</c> is
        /// implemented as, so the pushed projection and the in-process constructor it replaced are one
        /// function rather than two — including the SRID, which that function stamps 4326 and Calcite's
        /// own planar reader does not.
        /// </para>
        /// <para>
        /// A value that is not GeoJSON is a failure and not a null, which is the refusal every other
        /// declared reading makes and is what the in-process constructor does over the same input. A
        /// scalar at the path is the one case that would disagree — there the accessor answers null
        /// rather than reaching the constructor at all — and it never arrives here, because
        /// <c>CosmosRexTranslator.TryStoredGeographyProjection</c> sends the path under the same guard
        /// a <c>JSON_QUERY</c> carries.
        /// </para>
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <returns>The value.</returns>
        /// <exception cref="CosmosMaterializationException">The value is not GeoJSON.</exception>
        static org.locationtech.jts.geom.Geometry GetGeography(JsonElement value)
        {
            var text = value.GetRawText();

            try
            {
                return Geography.Runtime.GeographyFunctions.FromGeoJson(text);
            }
            catch (java.lang.RuntimeException e)
            {
                throw new CosmosMaterializationException($"A value read as a geography is not GeoJSON: {Abbreviate(text)}.", e);
            }
        }

        /// <summary>
        /// Converts a JTS geometry into the value a Cosmos statement means by a geography.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The other direction of <see cref="GetGeography"/>, and the one a parameter needs.</b>
        /// A geography in a Cosmos statement <em>is</em> a GeoJSON object — there is no constructor to
        /// call and no text form the service accepts — so a constant is written into the SQL as that
        /// object by <c>CosmosRexTranslator.WriteGeographyLiteral</c>. A parameter cannot be written
        /// into the SQL, its value arriving with the execution, so it is bound; and what is bound has
        /// to be the same object the constant form inlines, or the two spellings of one query mean
        /// different things.
        /// </para>
        /// <para>
        /// <b>Without this the bound value was the geometry itself</b>, and the SDK's serializer wrote
        /// the IKVM object graph — <c>$0</c>-keyed, assembly-qualified, three kilobytes for a point.
        /// The service received that in place of a shape, and where the statement also projected the
        /// parameter it came back and was read as a geography, which is how it was reported (#154).
        /// </para>
        /// <para>
        /// <b>A CLR object graph rather than text or a serializer's own tree.</b> The parameter is
        /// handed to <c>QueryDefinition.WithParameter</c> and serialized by whichever serializer the
        /// client carries; a dictionary and a list are the two shapes every JSON serializer writes as
        /// an object and an array, so this does not depend on which one that is. Text would be worse
        /// than wrong — it would arrive as a JSON <em>string</em>, and a spatial function over a string
        /// is not a spatial function over a shape.
        /// </para>
        /// <para>
        /// <b>The <c>crs</c> member is dropped, and it says nothing this loses.</b>
        /// <c>GeographyFunctions.AsGeoJson</c> writes <c>"crs":{"type":"name","properties":{"name":"EPSG:4326"}}</c>
        /// beside the shape. A geography is WGS84 and there is no second reference system for one to
        /// be in, so the member is redundant; what decides it is that the constant form — the one
        /// whose every emitted shape has been executed against an account — carries the caller's own
        /// text and no <c>crs</c>. Sending one where the working spelling sends none would be a second
        /// convention, tested nowhere.
        /// </para>
        /// </remarks>
        /// <param name="geometry">The geometry to bind.</param>
        /// <returns>The value to bind, as nested dictionaries, lists and primitives.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="geometry"/> is <c>null</c>.</exception>
        public static object ToGeoJsonValue(org.locationtech.jts.geom.Geometry geometry)
        {
            if (geometry is null)
                throw new ArgumentNullException(nameof(geometry));

            var text = Geography.Runtime.GeographyFunctions.AsGeoJson(geometry);

            using var document = JsonDocument.Parse(text);
            var value = ToClrValue(document.RootElement);

            // Only ever at the top level -- a nested geometry of a collection is written without one.
            if (value is Dictionary<string, object?> shape)
                shape.Remove("crs");

            return value!;
        }

        /// <summary>
        /// Reads a JSON value into the CLR object graph a serializer writes back out unchanged.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="GetNatural"/>, which builds the <em>Java</em> collections
        /// Calcite holds a value in. That is the shape a row carries; this is the shape a request
        /// carries, and the two go opposite ways.
        /// </remarks>
        static object? ToClrValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var shape = new Dictionary<string, object?>(StringComparer.Ordinal);

                    foreach (var property in value.EnumerateObject())
                        shape[property.Name] = ToClrValue(property.Value);

                    return shape;

                case JsonValueKind.Array:
                    var items = new List<object?>();

                    foreach (var item in value.EnumerateArray())
                        items.Add(ToClrValue(item));

                    return items;

                case JsonValueKind.String:
                    return value.GetString();

                case JsonValueKind.Number:
                    return value.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Shortens a value for a message, a shape being large enough to bury one.
        /// </summary>
        static string Abbreviate(string text)
        {
            return text.Length <= 120 ? $"'{text}'" : $"'{text.Substring(0, 120)}…' ({text.Length} characters)";
        }

        static double GetDouble(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new CosmosMaterializationException($"Expected a JSON number, got {value.ValueKind}.");

            return value.GetDouble();
        }

        static long GetInteger(JsonElement value, long min, long max, SqlTypeName typeName)
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new CosmosMaterializationException($"Expected a JSON number, got {value.ValueKind}.");

            // A Cosmos number is a double, so an integral column legitimately arrives written as 42.0 —
            // which TryGetInt64 rejects, integer syntax being what it tests. Falling back to the double
            // accepts that, and still refuses 42.5: rounding would answer a question the query did not ask.
            if (value.TryGetInt64(out var integral) == false)
            {
                var number = value.GetDouble();

                if (double.IsFinite(number) == false || Math.Floor(number) != number)
                    throw new CosmosMaterializationException($"A JSON number of '{value.GetRawText()}' is not a whole number and cannot be read as {typeName.name()}.");

                // Checked before the cast, which would otherwise wrap rather than fail. The bounds are
                // widened to double for the comparison, so a value within one ULP of the extreme is
                // admitted here and caught by the range check below.
                if (number < min || number > max)
                    throw new CosmosMaterializationException($"A JSON number of '{value.GetRawText()}' is outside the range of {typeName.name()}.");

                integral = (long)number;
            }

            if (integral < min || integral > max)
                throw new CosmosMaterializationException($"A JSON number of '{value.GetRawText()}' is outside the range of {typeName.name()}.");

            return integral;
        }

        static java.math.BigDecimal GetDecimal(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new CosmosMaterializationException($"Expected a JSON number, got {value.ValueKind}.");

            // Built from the raw text rather than from a double, which is the whole point of a decimal:
            // the digits JSON carried are the digits the value keeps.
            return new java.math.BigDecimal(value.GetRawText());
        }

        static org.apache.calcite.avatica.util.ByteString GetBinary(JsonElement value)
        {
            // JSON has no binary type; base64 in a string is the encoding Cosmos and every JSON API use.
            if (value.ValueKind != JsonValueKind.String)
                throw new CosmosMaterializationException($"Expected a base64 JSON string for a binary value, got {value.ValueKind}.");

            if (value.TryGetBytesFromBase64(out var bytes) == false)
                throw new CosmosMaterializationException("A binary value is not valid base64.");

            return new org.apache.calcite.avatica.util.ByteString(bytes);
        }

        static int GetDate(JsonElement value)
        {
            // A whole number is already the day count Calcite wants; a string is the ISO-8601 date Cosmos
            // documents as the way to store one.
            if (value.ValueKind == JsonValueKind.Number)
                return (int)GetInteger(value, int.MinValue, int.MaxValue, SqlTypeName.DATE);

            return DateOnly.FromDateTime(GetDateTime(value)).DayNumber - UnixEpochDay.DayNumber;
        }

        static int GetTime(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number)
                return (int)GetInteger(value, int.MinValue, int.MaxValue, SqlTypeName.TIME);

            return (int)GetDateTime(value).TimeOfDay.TotalMilliseconds;
        }

        static long GetTimestamp(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number)
                return GetInteger(value, long.MinValue, long.MaxValue, SqlTypeName.TIMESTAMP);

            return new DateTimeOffset(DateTime.SpecifyKind(GetDateTime(value), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }

        static DateTime GetDateTime(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new CosmosMaterializationException($"Expected a JSON string or number for a temporal value, got {value.ValueKind}.");

            var text = value.GetString()!;

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) == false)
                throw new CosmosMaterializationException($"A temporal value of '{text}' is not an ISO-8601 date or time.");

            return parsed;
        }

    }

}
