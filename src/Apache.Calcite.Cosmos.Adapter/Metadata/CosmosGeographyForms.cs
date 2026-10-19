using System;

using System.Linq;
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
        /// <remarks>
        /// <para>
        /// <b>Every value, where there is one.</b> Like every claim read off a subschema, this says what
        /// the path holds if it holds anything: whether it is there is the parent's <c>required</c> to say,
        /// and the theory's to derive. A geography that said its path was there settled what no keyword
        /// had, and an optional location's distance was sorted as though no document lacked one.
        /// </para>
        /// <para>
        /// <b>Read by the schema reader's own readers of the keywords it shares with them</b> —
        /// <c>type</c>, <c>required</c>, <c>const</c> and <c>enum</c>, <c>minimum</c> and <c>maximum</c>
        /// (<see cref="JsonSchemaFacts"/>) — so that one keyword is read one way. What no reader of the
        /// theory's reads yet, an array's counts and its elements' subschemas, is read here; it moves into
        /// the theory with the facts about array elements (<c>TODO.md</c>).
        /// </para>
        /// </remarks>
        /// <param name="node">The subschema declared for the path.</param>
        /// <returns><c>true</c> where the declaration proves it.</returns>
        public static bool Recognise(JsonNode? node)
        {
            if (node is not JsonObject)
                return false;

            // An object, and nothing else: `properties` and `required` constrain an object and are vacuous
            // for anything else, so without the type a string conforms, and a null beside it does too.
            if (IsType(node, JsonType.Object) == false)
                return false;

            // And one whose two GeoJSON members are both required. Without `required` the schema
            // constrains the members it happens to carry and admits a document with neither, which is
            // the `{"kind":"somewhere"}` case: a conforming object the service cannot read.
            var required = JsonSchemaFacts.ReadRequired(node);
            if (Property(node, "type") is not JsonNode type || required.Contains("type") == false || required.Contains("coordinates") == false)
                return false;

            // Exact case, measured: `point` answers undefined where `Point` does not, so a schema
            // pinning the lowercase spelling proves the opposite of what it looks like.
            if (JsonSchemaFacts.TryReadConstant(type, out var shape) == false || shape.ToClrValue() is not string name)
                return false;

            return name switch
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
            if (node is null || IsType(node, JsonType.Array) == false)
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
            if (node is null || IsType(node, JsonType.Array) == false)
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
        /// <remarks>
        /// A number, an integer among them, and nothing else, with a bound on each side at least as strict
        /// as the limit, exclusive or not: the bounds as the schema reader reads them.
        /// </remarks>
        static bool IsBounded(JsonNode? node, double limit)
        {
            if (node is not JsonObject || JsonSchemaFacts.ReadType(node) is not (JsonType.Number or JsonType.Integer, false))
                return false;

            var below = false;
            var above = false;
            foreach (var bound in JsonSchemaFacts.ReadBounds(node))
            {
                below |= bound is JsonClaim.LowerBound lower && lower.Value >= (decimal)-limit;
                above |= bound is JsonClaim.UpperBound upper && upper.Value <= (decimal)limit;
            }

            return below && above;
        }

        static JsonNode? Property(JsonNode node, string name) => node.Get("properties").Get(name);

        /// <summary>
        /// Determines whether a subschema says its value is of one type and never null, as the schema
        /// reader reads <c>type</c>.
        /// </summary>
        static bool IsType(JsonNode node, JsonType type) =>
            JsonSchemaFacts.ReadType(node) is var (declared, orNull) && declared == type && orNull == false;

        static int? Integer(JsonNode node, string keyword) =>
            node.Get(keyword) is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var integer) ? integer : null;

    }

}
