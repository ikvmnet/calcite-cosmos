using System;

using System.Text.Json;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Decides whether a declared subschema constrains a path to a value the service will measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A proof read off the schema, not a token taken on trust.</b> Two earlier attempts declared a
    /// geography instead of deriving one — a <c>format</c> of this repository's own, and a
    /// <c>$ref</c> to a published GeoJSON schema. Both claimed more than the schema said.
    /// </para>
    /// <para>
    /// <b>GeoJSON validity is not Cosmos measurability, and the gap is most of the difference.</b>
    /// Measured in <c>CosmosGeographyValidityMeasurementTests</c>: a structurally perfect point whose
    /// coordinates are out of range, a polygon whose ring is not closed, a ring that intersects
    /// itself, a <c>type</c> written in the wrong case, and a <c>MultiPoint</c> of any shape at all —
    /// every one of them validates against the published schema and every one answers
    /// <em>undefined</em>. So referencing GeoJSON proves GeoJSON, which is not the thing that needed
    /// proving.
    /// </para>
    /// <para>
    /// <b>What a schema can and cannot pin is therefore the whole of the scope.</b> Coordinate ranges
    /// are expressible, per position, with <c>prefixItems</c> or draft-07's array form of
    /// <c>items</c>; a member count is expressible; an exact type name is expressible. Ring closure
    /// is not — it is a relation between the first element and the last — and neither is a ring that
    /// does not cross itself. So <c>Point</c> and <c>LineString</c> can be proven and
    /// <c>Polygon</c> and <c>MultiPolygon</c> cannot, however carefully a container declares them.
    /// </para>
    /// <para>
    /// The same discipline as <see cref="CosmosUuidForms.Recognise"/>: the shape is <em>decided</em>
    /// from what the schema says rather than matched against a table of spellings, because the ways
    /// of writing one do not close.
    /// </para>
    /// </remarks>
    public static class CosmosGeographyForms
    {

        /// <summary>
        /// The bound a longitude is constrained to before the service will measure it.
        /// </summary>
        public const double LongitudeLimit = 180d;

        /// <summary>
        /// The bound a latitude is constrained to.
        /// </summary>
        /// <remarks>
        /// Measured rather than assumed from the reference: <c>[0,91]</c> and <c>[181,0]</c> both
        /// answer undefined, so the service enforces each ordinate separately and a schema has to
        /// constrain them separately to prove anything.
        /// </remarks>
        public const double LatitudeLimit = 90d;

        /// <summary>
        /// Determines whether a subschema proves that every value at the path is one the service
        /// will measure.
        /// </summary>
        /// <param name="node">The subschema declared for the path.</param>
        /// <returns><c>true</c> where the declaration proves it.</returns>
        public static bool Recognise(JsonNode? node)
        {
            if (node is not JsonObject)
                return false;

            // An object, and one whose two GeoJSON members are both required. Without `required` the
            // schema constrains the members it happens to carry and admits a document with neither,
            // which is the `{"kind":"somewhere"}` case: a conforming object the service cannot read.
            if (Declares(node, "type") == false || Requires(node, "type") == false || Requires(node, "coordinates") == false)
                return false;

            var shape = Constant(Property(node, "type"));

            // Exact case, measured: `point` answers undefined where `Point` does not, so a schema
            // pinning the lowercase spelling proves the opposite of what it looks like.
            return shape switch
            {
                "Point" => IsPosition(Property(node, "coordinates")),
                "LineString" => IsPositionArray(Property(node, "coordinates"), minimum: 2),
                _ => false,
            };
        }

        /// <summary>
        /// Determines whether a subschema constrains a value to a single GeoJSON position.
        /// </summary>
        /// <remarks>
        /// Two ordinates or three — a third is an elevation and is measured as readily as a pair —
        /// each within the bound the service enforces for its position. A schema that leaves either
        /// unbounded admits the coordinates that answer undefined, so it proves nothing.
        /// </remarks>
        static bool IsPosition(JsonNode? node)
        {
            if (node is null || IsType(node, "array") == false)
                return false;

            if (Integer(node, "minItems") is not int least || least < 2)
                return false;

            if (Integer(node, "maxItems") is not int most || most > 3)
                return false;

            var positions = node.Get("prefixItems") ?? node.Get("items");
            if (positions is not JsonArray || positions.Size() < 2)
                return false;

            return IsBounded(positions.At(0), LongitudeLimit)
                && IsBounded(positions.At(1), LatitudeLimit);
        }

        /// <summary>
        /// Determines whether a subschema constrains a value to an array of positions.
        /// </summary>
        /// <param name="node">The subschema.</param>
        /// <param name="minimum">How many positions the shape needs to be measurable.</param>
        static bool IsPositionArray(JsonNode? node, int minimum)
        {
            if (node is null || IsType(node, "array") == false)
                return false;

            // A line of one point answers undefined, measured, so the count is part of the proof.
            if (Integer(node, "minItems") is not int least || least < minimum)
                return false;

            // The uniform form only. `prefixItems` here would constrain the first positions and leave
            // the rest, which proves nothing about a line of any length.
            return node.Get("items") is JsonObject items && IsPosition(items);
        }

        /// <summary>
        /// Determines whether a subschema bounds a number to within <paramref name="limit"/> either way.
        /// </summary>
        static bool IsBounded(JsonNode? node, double limit)
        {
            if (node is not JsonObject || IsType(node, "number") == false && IsType(node, "integer") == false)
                return false;

            return Number(node, "minimum") is double least && least >= -limit
                && Number(node, "maximum") is double most && most <= limit;
        }

        static JsonNode? Property(JsonNode node, string name) => node.Get("properties").Get(name);

        static bool Declares(JsonNode node, string name) =>
            Property(node, name) is not null;

        static bool Requires(JsonNode node, string name)
        {
            var required = node.Get("required");

            for (var i = 0; i < required.Size(); i++)
                if (string.Equals(required.At(i).Text(), name, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>
        /// Reads the one value a subschema admits, written either way round.
        /// </summary>
        /// <remarks>
        /// <c>const</c> and a single-member <c>enum</c> say the same thing, and a container writing a
        /// draft-07 schema has only the second.
        /// </remarks>
        static string? Constant(JsonNode? node)
        {
            if (node is not JsonObject)
                return null;

            if (node.Get("const").Text() is string constant)
                return constant;

            if (node.Get("enum") is JsonArray domain && domain.Count == 1)
                return domain.At(0).Text();

            return null;
        }

        static bool IsType(JsonNode node, string name) =>
            string.Equals(node.Get("type").Text(), name, StringComparison.Ordinal);

        static int? Integer(JsonNode node, string keyword) =>
            node.Get(keyword) is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var integer) ? integer : null;

        static double? Number(JsonNode node, string keyword) =>
            node.Get(keyword) is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    }

}
