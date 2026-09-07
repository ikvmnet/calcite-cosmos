using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Builds the JSON document a row describes, and reads back the values a write request needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reverse of <see cref="CosmosJson"/>, and far the simpler half. A row describes its document
    /// with the document column and nothing else — every other column is a projection of that one and
    /// is declared <c>STORED</c> — so building a document is copying the JSON that arrived, minus the
    /// properties the service owns.
    /// </para>
    /// <para>
    /// Nothing is reinterpreted on the way through. A number keeps the digits it was given rather than
    /// a round trip through a double, and a property keeps its place. The value-by-value writer this
    /// class used to carry, which had to accept a Java box or a CLR primitive for every JSON type,
    /// went with the map column that produced them.
    /// </para>
    /// </remarks>
    public static class CosmosDocument
    {

        /// <summary>
        /// The properties the service owns, which are never written whatever a row carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>_ts</c> and <c>_etag</c> cannot be named in an <c>INSERT</c> — they are declared
        /// <c>STORED</c>, so the validator refuses — but they still arrive <em>inside</em> the document
        /// column, which is what <c>INSERT INTO t ("DOC") SELECT "DOC" FROM t2</c> hands over: a whole
        /// document, system properties and all. Copying one document to another is the obvious use of
        /// that statement, and the service's bookkeeping is not part of what is being copied.
        /// </para>
        /// <para>
        /// <b>This is a decision rather than a requirement, and it was measured.</b> With the stripping
        /// removed, a document carrying a bogus <c>_ts</c>, <c>_etag</c> and <c>_rid</c> was still
        /// accepted and still came back with values the service had assigned — so the service does not
        /// need to be protected from these. What stripping buys is that the document written is the
        /// document described: a new item does not silently carry another item's identity, and a caller
        /// reading back what they inserted is not told a value they never supplied was theirs. The
        /// measurement was against the emulator, which has disagreed with Azure in both directions
        /// before.
        /// </para>
        /// </remarks>
        public static readonly string[] ServiceProperties =
        [
            "_ts",
            "_etag",
            "_rid",
            "_self",
            "_attachments",
        ];

        /// <summary>
        /// Determines whether a property belongs to the service rather than to the document.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns><c>true</c> if the service owns it.</returns>
        public static bool IsServiceProperty(string name)
        {
            foreach (var reserved in ServiceProperties)
                if (string.Equals(name, reserved, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>
        /// Builds the document a row describes.
        /// </summary>
        /// <remarks>
        /// A row describes its document with the document column, which is the only one a statement
        /// may write — every other column is a projection of the same document and is declared
        /// <c>STORED</c>. The service's own properties are stripped wherever they appear, so a row
        /// read from a scan can be written back as another document without carrying that one's
        /// identity.
        /// </remarks>
        /// <param name="columnNames">The row's field names.</param>
        /// <param name="values">The row's values, in the same order.</param>
        /// <returns>The document, as UTF-8 JSON.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="columnNames"/> or <paramref name="values"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosExecutionException">The document column holds something that is not a JSON object.</exception>
        public static byte[] Build(IReadOnlyList<string> columnNames, IReadOnlyList<object?> values)
        {
            if (columnNames is null)
                throw new ArgumentNullException(nameof(columnNames));
            if (values is null)
                throw new ArgumentNullException(nameof(values));

            var count = Math.Min(columnNames.Count, values.Count);
            var ordinal = -1;

            for (var i = 0; i < count; i++)
                if (string.Equals(columnNames[i], CosmosImplementor.DocumentColumnName, StringComparison.Ordinal))
                    ordinal = i;

            var value = ordinal >= 0 ? values[ordinal] : null;

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                if (value is not null)
                {
                    if (value is not string text)
                        throw new CosmosExecutionException($"The '{CosmosImplementor.DocumentColumnName}' column holds a {value.GetType().Name} rather than JSON text, so it does not describe a document.");

                    JsonDocument parsed;

                    try
                    {
                        parsed = JsonDocument.Parse(text);
                    }
                    catch (JsonException e)
                    {
                        throw new CosmosExecutionException($"The '{CosmosImplementor.DocumentColumnName}' column does not hold well-formed JSON: {e.Message}", e);
                    }

                    using (parsed)
                    {
                        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                            throw new CosmosExecutionException($"The '{CosmosImplementor.DocumentColumnName}' column holds a JSON {parsed.RootElement.ValueKind.ToString().ToLowerInvariant()} rather than an object, so it does not describe a document.");

                        // Streamed straight through rather than collected first. Nothing overrides a
                        // property any more -- one column describes the document -- so what is written
                        // is what arrived, minus the service's own, in the order it arrived in. Values
                        // are copied verbatim, which is what keeps a number the digits it was given
                        // rather than a round trip through a double.
                        foreach (var property in parsed.RootElement.EnumerateObject())
                        {
                            if (IsServiceProperty(property.Name))
                                continue;

                            writer.WritePropertyName(property.Name);
                            property.Value.WriteTo(writer);
                        }
                    }
                }

                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>
        /// Reads the value at a policy path out of a document.
        /// </summary>
        /// <remarks>
        /// Used to recover the partition key and the <c>id</c> from the document a row describes, which
        /// is why it walks rather than naming a property: a partition key path may be nested, and a
        /// nested path is not promoted to a column.
        /// </remarks>
        /// <param name="document">The document.</param>
        /// <param name="policyPath">The path in policy form, such as <c>/inventory/sku</c>.</param>
        /// <returns>The value as a CLR primitive, or <c>null</c> where the path is absent.</returns>
        public static object? Read(JsonElement document, string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return null;

            var value = document;

            foreach (var segment in policyPath.Split('/'))
            {
                if (segment.Length == 0)
                    continue;

                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment, out value) == false)
                    return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetDouble(),
                // Null is a partition key value in Cosmos and is distinct from an absent one, which is
                // the "none" logical partition. Undefined is the absence.
                JsonValueKind.Null => null,
                _ => null,
            };
        }

        /// <summary>
        /// Determines whether a path is present in a document, distinguishing absence from a null value.
        /// </summary>
        /// <param name="document">The document.</param>
        /// <param name="policyPath">The path in policy form.</param>
        /// <returns><c>true</c> if the path is present, whatever it holds.</returns>
        public static bool Contains(JsonElement document, string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return false;

            var value = document;

            foreach (var segment in policyPath.Split('/'))
            {
                if (segment.Length == 0)
                    continue;

                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment, out value) == false)
                    return false;
            }

            return true;
        }

    }

}
