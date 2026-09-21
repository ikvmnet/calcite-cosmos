using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Which declared subschemas prove that the service will measure what is at a path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal here has a document behind it in
    /// <c>CosmosGeographyValidityMeasurementTests</c>: something that validates against the published
    /// GeoJSON schema and still answers <em>undefined</em>. That is the whole reason the proof is read
    /// off the structure rather than taken from a <c>$ref</c> or a <c>format</c> — GeoJSON validity
    /// and Cosmos measurability are not the same property.
    /// </para>
    /// <para>
    /// The positives are narrow on purpose. A ring's closure is a relation between its first element
    /// and its last, and its simplicity is a relation among all of them; no schema states either, so
    /// no declaration of a <c>Polygon</c> can be believed however careful it is.
    /// </para>
    /// </remarks>
    public class CosmosGeographyFormsTests
    {

        static bool Recognise(string json) =>
            CosmosGeographyForms.Recognise(new com.fasterxml.jackson.databind.ObjectMapper().readTree(json));

        const string Ordinates = """
        "coordinates": { "type": "array", "minItems": 2, "maxItems": 3,
          "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                           { "type": "number", "minimum": -90, "maximum": 90 } ] }
        """;

        /// <summary>
        /// A point whose type is pinned, whose members are required, and whose ordinates are bounded.
        /// </summary>
        [Fact]
        public void APinnedAndBoundedPointIsRecognised()
        {
            Recognise($$"""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "Point" }, {{Ordinates}} } }
            """).Should().BeTrue();
        }

        /// <remarks>
        /// <c>const</c> and a one-member <c>enum</c> say the same thing, and a container on draft-07
        /// has only the second.
        /// </remarks>
        [Fact]
        public void ASingleMemberEnumPinsTheTypeAsWell()
        {
            Recognise($$"""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "enum": ["Point"] }, {{Ordinates}} } }
            """).Should().BeTrue();
        }

        /// <summary>
        /// Unbounded ordinates prove nothing, which is the case a published schema leaves open.
        /// </summary>
        /// <remarks>
        /// <c>{"type":"Point","coordinates":[999,999]}</c> is structurally perfect GeoJSON and answers
        /// undefined. A schema that does not bound the numbers admits it.
        /// </remarks>
        [Fact]
        public void UnboundedOrdinatesAreNotRecognised()
        {
            Recognise("""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "Point" },
                              "coordinates": { "type": "array", "minItems": 2, "maxItems": 3,
                                "prefixItems": [ { "type": "number" }, { "type": "number" } ] } } }
            """).Should().BeFalse();
        }

        /// <remarks>
        /// Each ordinate is enforced separately — <c>[0,91]</c> and <c>[181,0]</c> both answer
        /// undefined — so bounding one and leaving the other proves half of nothing.
        /// </remarks>
        [Fact]
        public void BoundingOnlyTheLongitudeIsNotEnough()
        {
            Recognise("""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "Point" },
                              "coordinates": { "type": "array", "minItems": 2, "maxItems": 3,
                                "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                                 { "type": "number" } ] } } }
            """).Should().BeFalse();
        }

        /// <remarks>
        /// Measured: <c>point</c> answers undefined where <c>Point</c> does not, so a schema pinning
        /// the lowercase spelling proves the opposite of what it looks like.
        /// </remarks>
        [Fact]
        public void TheTypeNameIsCaseSensitive()
        {
            Recognise($$"""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "point" }, {{Ordinates}} } }
            """).Should().BeFalse();
        }

        /// <remarks>
        /// Without <c>required</c> the schema constrains the members a document happens to carry and
        /// admits one with neither — <c>{"kind":"somewhere"}</c>, a conforming object the service
        /// cannot read.
        /// </remarks>
        [Fact]
        public void MembersThatAreNotRequiredAreNotRecognised()
        {
            Recognise($$"""
            { "type": "object",
              "properties": { "type": { "const": "Point" }, {{Ordinates}} } }
            """).Should().BeFalse();
        }

        /// <summary>
        /// A line of two positions or more is recognised; a line of one is not.
        /// </summary>
        /// <remarks>
        /// Measured, and the reason the count is part of the proof rather than a tidiness check:
        /// a <c>LineString</c> of a single position answers undefined.
        /// </remarks>
        [Fact]
        public void ALineNeedsTwoPositions()
        {
            const string Line = """
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "LineString" },
                              "coordinates": { "type": "array", "minItems": {0},
                                "items": { "type": "array", "minItems": 2, "maxItems": 3,
                                  "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                                   { "type": "number", "minimum": -90, "maximum": 90 } ] } } } }
            """;

            Recognise(Line.Replace("{0}", "2")).Should().BeTrue();
            Recognise(Line.Replace("{0}", "1")).Should().BeFalse();
        }

        /// <summary>
        /// A polygon is never recognised, however completely it is declared.
        /// </summary>
        /// <remarks>
        /// <b>The limit of the whole approach, and it is a limit of JSON Schema rather than of this
        /// recogniser.</b> Measured: an unclosed ring, a ring of two distinct positions, and a ring
        /// that crosses itself all answer undefined. Closure is a relation between the first element
        /// and the last and simplicity is a relation among all of them; a schema states neither. So
        /// the declaration below pins everything it can and still admits documents the service will
        /// not measure.
        /// </remarks>
        [Fact]
        public void APolygonIsNeverRecognised()
        {
            Recognise("""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "Polygon" },
                              "coordinates": { "type": "array", "minItems": 1,
                                "items": { "type": "array", "minItems": 4,
                                  "items": { "type": "array", "minItems": 2, "maxItems": 3,
                                    "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                                     { "type": "number", "minimum": -90, "maximum": 90 } ] } } } } }
            """).Should().BeFalse();
        }

        /// <remarks>
        /// Measured: the service answers undefined for a <c>MultiPoint</c> of any shape, so a
        /// declaration of one could only ever prove the wrong thing.
        /// </remarks>
        [Fact]
        public void AMultiPointIsNeverRecognised()
        {
            Recognise("""
            { "type": "object", "required": ["type", "coordinates"],
              "properties": { "type": { "const": "MultiPoint" },
                              "coordinates": { "type": "array", "minItems": 1,
                                "items": { "type": "array", "minItems": 2, "maxItems": 3,
                                  "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                                   { "type": "number", "minimum": -90, "maximum": 90 } ] } } } }
            """).Should().BeFalse();
        }

        [Fact]
        public void NothingAtAllIsNotRecognised()
        {
            CosmosGeographyForms.Recognise(null).Should().BeFalse();
            Recognise("\"a string\"").Should().BeFalse();
            Recognise("{}").Should().BeFalse();
        }

    }

}
