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
    /// <para>
    /// A value holding text that is not UTF-8, which Azure stores as a client wrote it, has no reading
    /// and is refused by every reading alike, with an error that names what holds it, the document where
    /// the row says which, and the bytes — <see cref="Undecodable"/>. Each reading of a row's column
    /// catches the decoder's refusal and names it; the one that writes a value out rather than decoding
    /// it looks for the bytes first.
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
        /// <param name="identity">
        /// Where the row holds its document's <c>id</c>, as member names from the row, for the error a
        /// value that is not UTF-8 is refused with; <c>null</c> where it holds none. See
        /// <see cref="Undecodable"/>.
        /// </param>
        public static object? GetProperty(JsonElement row, string name, SqlTypeName typeName, SqlTypeName? componentTypeName, string[]? identity = null)
        {
            // A row that is not an object has no properties to name. That is not reachable through the
            // converters, which always project an object, but it is worth failing loudly rather than
            // silently yielding a row of nulls if one ever stops doing so.
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            if (row.TryGetProperty(name, out var value) == false)
                return null;

            try
            {
                return GetValue(value, typeName, componentTypeName);
            }
            catch (InvalidOperationException e) when (IsUndecodable(e))
            {
                throw Undecodable($"Column '{name}'", DocumentIdOf(row, identity), value, e);
            }
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
        /// <param name="identity">Where the row holds its document's <c>id</c>; see <see cref="GetProperty(JsonElement, string, SqlTypeName, SqlTypeName?, string[])"/>.</param>
        /// <returns>The rendered text, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The row is not a JSON object, or the value holds text that is not UTF-8.</exception>
        public static string? GetTextProperty(JsonElement row, string name, string[]? identity = null)
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            if (row.TryGetProperty(name, out var value) == false)
                return null;

            try
            {
                return GetText(value);
            }
            catch (InvalidOperationException e) when (IsUndecodable(e))
            {
                throw Undecodable($"Column '{name}'", DocumentIdOf(row, identity), value, e);
            }
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
        /// <param name="identity">Where the row holds its document's <c>id</c>; see <see cref="GetProperty(JsonElement, string, SqlTypeName, SqlTypeName?, string[])"/>.</param>
        /// <returns>The JSON text, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The row is not an object, or the value holds text that is not UTF-8.</exception>
        public static string? GetJsonProperty(JsonElement row, string name, string[]? identity = null)
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            if (row.TryGetProperty(name, out var value) == false || value.ValueKind == JsonValueKind.Null)
                return null;

            try
            {
                return value.GetRawText();
            }
            catch (InvalidOperationException e) when (IsUndecodable(e))
            {
                throw Undecodable($"Column '{name}'", DocumentIdOf(row, identity), value, e);
            }
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
        /// <para>
        /// <b>Text that is not UTF-8 is refused here as everywhere, and here it has to be looked for.</b>
        /// Every other reading decodes, and <c>System.Text.Json</c> refuses to decode it; writing a
        /// value out does not decode it, and the writer replaces each byte it cannot read with an
        /// escaped U+FFFD. So <c>x ED A0 80 y</c> came out as <c>"x\uFFFD\uFFFD\uFFFDy"</c> and was
        /// answered without a word, where the in-process plan raised and the service compares the bytes
        /// as they are (<c>reports/verify-bytes.md</c>, L15). The value is searched first instead,
        /// which is one validation of each string's bytes and nothing for a value that has none.
        /// </para>
        /// </remarks>
        /// <param name="row">The row object.</param>
        /// <param name="name">The property to read.</param>
        /// <param name="identity">Where the row holds its document's <c>id</c>; see <see cref="GetProperty(JsonElement, string, SqlTypeName, SqlTypeName?, string[])"/>.</param>
        /// <returns>The JSON text, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The row is not an object, or the value holds text that is not UTF-8.</exception>
        public static string? GetJsonTextProperty(JsonElement row, string name, string[]? identity = null)
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            if (row.TryGetProperty(name, out var value) == false || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            if (IsDecodable(value) == false)
                throw Undecodable($"Column '{name}'", DocumentIdOf(row, identity), value, null);

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

            try
            {
                return GetValue(value, typeName, componentTypeName);
            }
            catch (InvalidOperationException e) when (IsUndecodable(e))
            {
                // A point read's row is the document, so its id is known whatever the statement selects.
                var path = new System.Text.StringBuilder("$");
                foreach (var segment in segments)
                    AppendStep(path, segment.IsIndex ? null : segment.Name, segment.ArrayIndex);

                throw Undecodable($"The path '{path}'", DocumentIdOf(document, [Metadata.CosmosContainerMetadata.IdPropertyName]), value, e);
            }
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
                case nameof(SqlTypeName.VARIANT):
                    return GetVariant(value);
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
                nameof(SqlTypeName.VARIANT) => true,
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
        /// Reads a value as a Calcite <c>VARIANT</c>: the natural value boxed with the runtime type
        /// learned from its JSON shape.
        /// </summary>
        /// <remarks>
        /// A promoted declared-path column is typed <c>VARIANT</c> — <see cref="CosmosTable.getRowType"/>
        /// — and Calcite's runtime carries a VARIANT column as an
        /// <c>org.apache.calcite.runtime.variant.VariantValue</c>, not the raw object an <c>ANY</c> value
        /// is. When a predicate over the column does not push — or when the differential oracle evaluates
        /// the pushed one in process — the value has to be a <c>VariantValue</c> or the cast to it throws
        /// (ikvmnet/calcite-cosmos#163). A partition-key value is a scalar, which Cosmos requires, so the
        /// JSON kind names the runtime type: a string is <c>VARCHAR</c>, an integral number <c>BIGINT</c>,
        /// a fractional one <c>DOUBLE</c>, a boolean <c>BOOLEAN</c>, and an absent or null value the
        /// variant null. An object or array is not a valid key and is read raw, as it was under <c>ANY</c>.
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <returns>The boxed variant, or the raw value where it is not a scalar.</returns>
        static object? GetVariant(JsonElement value)
        {
            org.apache.calcite.runtime.rtti.RuntimeTypeInformation.RuntimeSqlTypeName typeName;

            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    return GetNatural(value);
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    // SQL NULL, which Calcite's runtime carries as a bare null rather than a variant --
                    // its null handling runs before any VariantValue operation, and a nullable column
                    // reads its absent value as one.
                    return null;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    typeName = org.apache.calcite.runtime.rtti.RuntimeTypeInformation.RuntimeSqlTypeName.BOOLEAN;
                    break;
                case JsonValueKind.String:
                    typeName = org.apache.calcite.runtime.rtti.RuntimeTypeInformation.RuntimeSqlTypeName.VARCHAR;
                    break;
                case JsonValueKind.Number:
                    typeName = value.TryGetInt64(out _)
                        ? org.apache.calcite.runtime.rtti.RuntimeTypeInformation.RuntimeSqlTypeName.BIGINT
                        : org.apache.calcite.runtime.rtti.RuntimeTypeInformation.RuntimeSqlTypeName.DOUBLE;
                    break;
                default:
                    throw new CosmosMaterializationException($"Unexpected JSON value kind '{value.ValueKind}'.");
            }

            var rtti = new org.apache.calcite.runtime.rtti.BasicSqlTypeRtti(typeName);
            return org.apache.calcite.runtime.variant.VariantSqlValue.create(java.math.RoundingMode.HALF_UP, GetNatural(value), rtti);
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

        // ── Text that is not UTF-8 ──────────────────────────────────────────────────────────

        /// <summary>
        /// Determines whether an exception is <c>System.Text.Json</c> refusing to decode text that is not
        /// UTF-8.
        /// </summary>
        /// <remarks>
        /// What <c>GetString</c>, <c>GetRawText</c> and a property's <c>Name</c> throw over such bytes, and
        /// only then: an <see cref="InvalidOperationException"/>, <em>"Cannot transcode invalid UTF-8 JSON
        /// text to UTF-16 string."</em>, caused by the decoder's <see cref="System.Text.DecoderFallbackException"/>.
        /// The cause is what is tested, the reader throwing that type for other reasons too.
        /// </remarks>
        /// <param name="e">The exception.</param>
        /// <returns><c>true</c> where it is that refusal.</returns>
        internal static bool IsUndecodable(Exception e) =>
            e is InvalidOperationException { InnerException: System.Text.DecoderFallbackException };

        /// <summary>
        /// Reads a JSON string, refusing one whose bytes are not UTF-8 with the error that says so.
        /// </summary>
        /// <remarks>
        /// For a reader that decodes a value outside a row's columns — the one completing an order, which
        /// compares the keys and asks whether each certifies the rows around it — and so has a subject to
        /// name but no column and no document.
        /// </remarks>
        /// <param name="value">A JSON string.</param>
        /// <param name="subject">What holds the value, for the error.</param>
        /// <returns>The text.</returns>
        /// <exception cref="CosmosMaterializationException">The bytes are not UTF-8.</exception>
        internal static string Decode(JsonElement value, string subject)
        {
            try
            {
                return value.GetString()!;
            }
            catch (InvalidOperationException e) when (IsUndecodable(e))
            {
                throw Undecodable(subject, null, value, e);
            }
        }

        /// <summary>
        /// Builds the error a value holding text that is not UTF-8 is refused with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Refused, because nothing Calcite can hold is right.</b> Azure validates nothing about UTF-8
        /// in a document body (<c>Service.InvalidUtf8IsStoredAsWritten</c>). A client that writes raw bytes
        /// — a lone surrogate as WTF-8, CESU-8, an overlong form, a stray continuation byte, in a value or
        /// in a name — has them stored and returned as written, compared as bytes and counted by lead
        /// byte. Calcite holds a string as UTF-16 units and has no value for them that compares as the
        /// service does. U+FFFD, a lenient decoder's reading, is not one: the service ranks <c>80</c> and
        /// <c>C0 AF</c> below U+00E9 and says the raw document is not <c>"x\uFFFDy"</c>, where Calcite would
        /// rank the replacement above every unit below U+D800 and call the two equal; and the document
        /// column would stop being the text the service sent. Failing is the one reading that is not
        /// wrong, and the differential counts a Calcite error as refining to anything.
        /// </para>
        /// <para>
        /// <b>Named, because the decoder's own error names nothing</b>, and one such document anywhere in
        /// a container fails every statement that reads its <c>DOC</c> — every plan not pushed in full —
        /// mid-stream, after earlier rows have been handed out. So this says what holds the value, the
        /// document where the row identifies one, where in the value the bytes lie, and the bytes: what a
        /// caller needs to find the document and rewrite it.
        /// </para>
        /// <para>
        /// <b>A value fails, not a row or a partition.</b> A statement fails exactly where a row carries
        /// the bytes into something that decodes them. One whose rows leave the value out is answered, in
        /// the same logical partition too (<c>reports/verify-bytes.md</c>, §2.5).
        /// </para>
        /// </remarks>
        /// <param name="subject">What holds the value: a column, a path, a key.</param>
        /// <param name="document">The document's <c>id</c> as <see cref="DocumentIdOf"/> shows it, or <c>null</c> where it is not known.</param>
        /// <param name="value">The value the bytes are in.</param>
        /// <param name="inner">The decoder's refusal, where there was one.</param>
        /// <returns>The error.</returns>
        internal static CosmosMaterializationException Undecodable(string subject, string? document, JsonElement value, Exception? inner)
        {
            var message = new System.Text.StringBuilder(subject);

            if (document is not null)
                message.Append(" of document ").Append(document);

            message.Append(" holds text that is not UTF-8");

            if (FindUndecodable(value) is { } found)
            {
                var (steps, bytes, name) = found;

                message.Append(" at $");
                for (var i = steps.Count - 1; i >= 0; i--)
                    message.Append(steps[i]);

                message.Append(name ? ", in a property name" : "").Append(": the bytes ");
                AppendInvalidBytes(message, bytes);
                message.Append(" in ");
                AppendShown(message, bytes);
            }

            message.Append(". Cosmos DB stores the bytes a client writes without checking them, and Calcite has no string that compares as the service compares these, so the value cannot be read. Rewrite it as UTF-8, or leave it out of what the statement reads.");

            return inner is null ? new(message.ToString()) : new(message.ToString(), inner);
        }

        /// <summary>
        /// Shows the <c>id</c> a row holds where it says it holds one, for the error a value that is not
        /// UTF-8 is refused with.
        /// </summary>
        /// <param name="row">The row, or the document.</param>
        /// <param name="identity">The members leading to the <c>id</c>, from the row; <c>null</c> where the row holds none.</param>
        /// <returns>The <c>id</c>, quoted as JSON writes it, or <c>null</c> where it is not there.</returns>
        internal static string? DocumentIdOf(JsonElement row, string[]? identity)
        {
            if (identity is null)
                return null;

            var value = row;
            foreach (var member in identity)
                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(member, out value) == false)
                    return null;

            // Shown from its bytes, an id being as able as anything else to hold some that are not UTF-8.
            if (value.ValueKind != JsonValueKind.String)
                return null;

            var shown = new System.Text.StringBuilder();
            AppendShown(shown, System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value)[1..^1]);
            return shown.ToString();
        }

        /// <summary>
        /// Finds the first string, or property name, in a value whose bytes are not UTF-8.
        /// </summary>
        /// <remarks>
        /// Over the bytes the service sent, which <c>JsonMarshal</c> hands over undecoded: a string's
        /// token is its quotes, its text and any escapes, and an escape is ASCII, so the text between the
        /// quotes is UTF-8 exactly where the bytes are. (An escaped lone surrogate is ASCII and still does
        /// not decode; the service never returns one, storing the escape as U+FFFD,
        /// <c>Service.LoneSurrogateIsStoredAsReplacement</c>.) Nothing is allocated unless something is
        /// found, which is what lets <see cref="GetJsonTextProperty"/> ask it of every value; the steps to
        /// it are gathered on the way out, innermost first.
        /// </remarks>
        /// <param name="value">The value.</param>
        /// <returns>The steps to it from the value, innermost first; its bytes; and whether they are a property's name; or <c>null</c>.</returns>
        static (List<string> Steps, byte[] Bytes, bool Name)? FindUndecodable(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value)[1..^1];
                    return System.Text.Unicode.Utf8.IsValid(text) ? null : ([], text.ToArray(), false);

                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        var name = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8PropertyName(property);
                        if (System.Text.Unicode.Utf8.IsValid(name) == false)
                            return ([], name.ToArray(), true);

                        if (FindUndecodable(property.Value) is { } found)
                        {
                            var step = new System.Text.StringBuilder();
                            AppendStep(step, property.Name, -1);
                            found.Steps.Add(step.ToString());
                            return found;
                        }
                    }

                    return null;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var element in value.EnumerateArray())
                    {
                        if (FindUndecodable(element) is { } found)
                        {
                            var step = new System.Text.StringBuilder();
                            AppendStep(step, null, index);
                            found.Steps.Add(step.ToString());
                            return found;
                        }

                        index++;
                    }

                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Determines whether every string and property name in a value is UTF-8.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> where nothing in it would be refused.</returns>
        internal static bool IsDecodable(JsonElement value) => FindUndecodable(value) is null;

        /// <summary>
        /// Writes one step of a JSON path: <c>.name</c>, <c>['odd name']</c> or <c>[3]</c>.
        /// </summary>
        static void AppendStep(System.Text.StringBuilder builder, string? name, int index)
        {
            if (name is null)
            {
                builder.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                return;
            }

            var plain = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_');
            for (var i = 1; plain && i < name.Length; i++)
                plain = char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_';

            if (plain)
                builder.Append('.').Append(name);
            else
                builder.Append("['").Append(name.Replace("\\", "\\\\").Replace("'", "\\'")).Append("']");
        }

        /// <summary>
        /// Writes the bytes of the first run of a text that are not UTF-8, in hex: <c>ED A0 80</c>.
        /// </summary>
        static void AppendInvalidBytes(System.Text.StringBuilder builder, ReadOnlySpan<byte> bytes)
        {
            var at = 0;
            while (at < bytes.Length && System.Text.Rune.DecodeFromUtf8(bytes[at..], out _, out var consumed) == System.Buffers.OperationStatus.Done)
                at += consumed;

            // A run, because a decoder rejects a sequence a byte at a time where its second byte is out
            // of range: a WTF-8 surrogate, ED A0 80, is three rejections, and it is one thing to rewrite.
            var first = true;
            while (at < bytes.Length && System.Text.Rune.DecodeFromUtf8(bytes[at..], out _, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                for (var i = 0; i < consumed; i++, first = false)
                    builder.Append(first ? "" : " ").Append(bytes[at + i].ToString("X2", CultureInfo.InvariantCulture));

                at += consumed;
            }
        }

        /// <summary>
        /// Writes a text as JSON would quote it, with each byte that is not UTF-8 as <c>\xHH</c>; a long
        /// one is cut around the first such byte, or after its start where it has none.
        /// </summary>
        static void AppendShown(System.Text.StringBuilder builder, ReadOnlySpan<byte> bytes)
        {
            const int Around = 32;

            var first = 0;
            while (first < bytes.Length && System.Text.Rune.DecodeFromUtf8(bytes[first..], out _, out var consumed) == System.Buffers.OperationStatus.Done)
                first += consumed;

            // Cut where a sequence starts, so that the cut itself shows no invalid bytes: a sequence is
            // at most four bytes, so at most three continuation bytes are stepped over at either end.
            var invalid = first < bytes.Length;

            var start = invalid ? Math.Max(0, first - Around) : 0;
            for (var i = 0; i < 3 && start > 0 && (bytes[start] & 0xC0) == 0x80; i++)
                start--;

            var end = Math.Min(bytes.Length, invalid ? first + Around : Around * 4);
            for (var i = 0; i < 3 && end < bytes.Length && (bytes[end] & 0xC0) == 0x80; i++)
                end++;

            builder.Append(start > 0 ? "\"…" : "\"");

            for (var at = start; at < end;)
            {
                if (System.Text.Rune.DecodeFromUtf8(bytes[at..], out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                {
                    for (var i = 0; i < consumed; i++)
                        builder.Append("\\x").Append(bytes[at + i].ToString("X2", CultureInfo.InvariantCulture));
                }
                else if (rune.Value < 0x20)
                {
                    builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(rune.ToString());
                }

                at += consumed;
            }

            builder.Append(end < bytes.Length ? "…\"" : "\"");
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

        /// <summary>
        /// Reads a JSON number as a whole number within a two's complement width.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every integral reading comes here</b> — <c>TINYINT</c> to <c>BIGINT</c>, and a <c>DATE</c>,
        /// <c>TIME</c> or <c>TIMESTAMP</c> stored as a number — so the range is decided once, for each
        /// width by the same two bounds.
        /// </para>
        /// <para>
        /// <b>A number outside the width is refused, never saturated.</b> Azure stores an integer
        /// written outside int64 as a double (<c>Service.IntegerOutsideInt64IsADouble</c>), so a
        /// document written with 2^63 or 2^63 + 1 comes back as <c>9.223372036854776E+18</c>, which
        /// is no <c>BIGINT</c>; Calcite's <c>RETURNING BIGINT</c> raises over it in process
        /// (<c>Calcite.ReturningAsserts</c>), and so does this.
        /// </para>
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <param name="min">The width's least value, -2^(k-1).</param>
        /// <param name="max">The width's greatest value, 2^(k-1) - 1.</param>
        /// <param name="typeName">The SQL type, which the refusal names.</param>
        /// <returns>The value.</returns>
        /// <exception cref="CosmosMaterializationException">The value is not a whole number within the width.</exception>
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

                // Checked in doubles before the cast, which saturates rather than fails, and the bounds
                // have to be exact there. The least value is -2^(k-1), a double exactly. The greatest is
                // not, past 2^53: (double)long.MaxValue rounds up to 2^63, which is no long, so testing
                // "number > max" admitted a stored 2^63 and the cast read it as 9223372036854775807 — the
                // answer Azure's 9.223372036854776E+18 got under RETURNING BIGINT, where Calcite raises.
                // One past the greatest, 2^(k-1), is a double exactly in every width: where max is one,
                // adding one is exact, and where it rounded up to 2^63, adding one rounds back to it. So
                // the refusal is "at or above max + 1" and "below min". Not "at or below min - 1", the
                // mirror of it, which is -2^63 in doubles and would refuse long.MinValue itself.
                if (number < min || number >= (double)max + 1.0)
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
