using System;
using System.Text;
using System.Text.Json;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The GeoJSON geometry literal a spatial function takes as its constant side, and the one place
    /// this adapter emits an object into a statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos's spatial functions take and return <em>GeoJSON objects</em>, so a query naming a point
    /// has to get one into the statement. SQL has no object literal and the row model has no geometry
    /// type, so the spelling is a string literal holding GeoJSON text —
    /// <c>ST_DISTANCE(c.location, '{"type":"Point","coordinates":[-122.1,47.6]}')</c> — which this
    /// parses, validates and re-emits as the object constructor Cosmos expects.
    /// </para>
    /// <para>
    /// <b>Parsed and re-emitted rather than passed through</b>, and that is the point of the type. A
    /// literal reaches here from query text, so copying it into a statement verbatim would put caller
    /// text inside the SQL this adapter otherwise never lets it into. What is emitted is built here from
    /// what this validated: two known member names, a type from a fixed list, and numbers written by
    /// <see cref="CosmosSql"/>. Nothing else survives the round trip.
    /// </para>
    /// <para>
    /// <b>Inlined rather than bound</b>, which is the one place this adapter departs from binding
    /// literals — see <c>DESIGN.md</c> under <em>Spatial</em>. An object in the geometry position is
    /// what every documented example of these functions shows, and whether the service serves a
    /// parameter there off the spatial index is unmeasured. That index is the entire reason for pushing
    /// a spatial predicate down, so the documented form is emitted and the question is recorded rather
    /// than guessed at.
    /// </para>
    /// <para>
    /// The four types are the ones Cosmos documents for indexing and querying. A
    /// <c>GeometryCollection</c>, a <c>MultiPoint</c> and a <c>MultiLineString</c> are GeoJSON and are
    /// not in that list, so they are refused rather than emitted on the assumption that they behave.
    /// </para>
    /// </remarks>
    public static class CosmosGeoJson
    {

        /// <summary>
        /// Returns the nesting depth a geometry type's coordinates carry, or zero for a type that is
        /// not one of the four.
        /// </summary>
        /// <remarks>
        /// A position is a flat array of numbers, a line string is an array of positions, a polygon an
        /// array of rings, and a multi polygon an array of polygons. The depth is therefore decided by
        /// the type, which makes a mistyped geometry — a polygon's rings under <c>"type": "Point"</c> —
        /// something this refuses rather than the service.
        /// </remarks>
        static int CoordinateDepth(string type)
        {
            return type switch
            {
                "Point" => 1,
                "LineString" => 2,
                "Polygon" => 3,
                "MultiPolygon" => 4,
                _ => 0,
            };
        }

        /// <summary>
        /// Determines whether a literal value is text this renders as a geometry.
        /// </summary>
        /// <param name="value">The literal value to inspect.</param>
        /// <returns><c>true</c> where it is a GeoJSON geometry.</returns>
        public static bool IsGeometry(object? value) => value is string text && TryWrite(text, out _);

        /// <summary>
        /// Renders GeoJSON text as the Cosmos object literal for it.
        /// </summary>
        /// <param name="text">The GeoJSON text.</param>
        /// <param name="geometry">On success, the rendered object literal.</param>
        /// <returns><c>true</c> if <paramref name="text"/> is a geometry this renders; otherwise <c>false</c>.</returns>
        public static bool TryWrite(string? text, out string? geometry)
        {
            geometry = null;

            if (string.IsNullOrEmpty(text))
                return false;

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                string? type = null;
                var coordinates = default(JsonElement);
                var hasCoordinates = false;

                // Exactly the two members and no others. A bbox or a crs is legal GeoJSON that Cosmos
                // does not document taking here, and admitting a member this cannot reason about is
                // admitting caller text into the statement.
                foreach (var member in root.EnumerateObject())
                {
                    if (member.NameEquals("type"))
                    {
                        if (member.Value.ValueKind != JsonValueKind.String)
                            return false;

                        type = member.Value.GetString();
                    }
                    else if (member.NameEquals("coordinates"))
                    {
                        if (member.Value.ValueKind != JsonValueKind.Array)
                            return false;

                        coordinates = member.Value;
                        hasCoordinates = true;
                    }
                    else
                    {
                        return false;
                    }
                }

                if (type is null || hasCoordinates == false)
                    return false;

                var depth = CoordinateDepth(type);
                if (depth == 0)
                    return false;

                var builder = new StringBuilder();

                builder.Append("{ ");
                CosmosSql.WriteStringLiteral(builder, "type");
                builder.Append(": ");
                CosmosSql.WriteStringLiteral(builder, type);
                builder.Append(", ");
                CosmosSql.WriteStringLiteral(builder, "coordinates");
                builder.Append(": ");

                if (TryWriteCoordinates(builder, coordinates, depth) == false)
                    return false;

                builder.Append(" }");

                geometry = builder.ToString();
                return true;
            }
        }

        /// <summary>
        /// Writes a coordinate array, holding it to the nesting its type implies.
        /// </summary>
        /// <remarks>
        /// At depth one the elements are the numbers of a position — a longitude and a latitude, and a
        /// third for an altitude — and above it they are arrays. An empty array at any level is refused:
        /// it is not a geometry, and it is the shape a caller reaches by building the text
        /// programmatically out of nothing.
        /// </remarks>
        static bool TryWriteCoordinates(StringBuilder builder, JsonElement element, int depth)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return false;

            var count = 0;
            builder.Append('[');

            foreach (var item in element.EnumerateArray())
            {
                if (count > 0)
                    builder.Append(", ");

                count++;

                if (depth > 1)
                {
                    if (TryWriteCoordinates(builder, item, depth - 1) == false)
                        return false;

                    continue;
                }

                // Finite, because a JSON number can be written large enough to read back as an infinity
                // and CosmosSql has no literal for one — which would leave this throwing something the
                // translator does not catch.
                if (item.ValueKind != JsonValueKind.Number || item.TryGetDouble(out var ordinate) == false || double.IsFinite(ordinate) == false)
                    return false;

                CosmosSql.WriteLiteral(builder, ordinate);
            }

            builder.Append(']');

            // A position carries a longitude and a latitude at least; everything above it needs one
            // member.
            return depth == 1 ? count >= 2 : count >= 1;
        }

    }

}
