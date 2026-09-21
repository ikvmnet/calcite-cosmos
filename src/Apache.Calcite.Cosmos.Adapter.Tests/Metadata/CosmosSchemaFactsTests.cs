using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// A declared schema, compiled to rules, asked the questions a rewrite asks.
    /// </summary>
    /// <remarks>
    /// The shape that makes this worth having: one container holding two kinds of document,
    /// discriminated by a property, where only one kind carries the path a fact is declared for. A
    /// fact about that path is unusable until the query has proven which kind it is filtering.
    /// </remarks>
    public class CosmosSchemaFactsTests
    {

        static readonly CosmosDocumentPath Type = CosmosDocumentPath.Root.Property("type");
        static readonly CosmosDocumentPath Data = CosmosDocumentPath.Root.Property("data");
        static readonly CosmosDocumentPath ParkId = Data.Property("parkId");
        static readonly CosmosDocumentPath At = Data.Property("at");

        static IReadOnlyList<CosmosFactRule> Read(string json) =>
            CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(json));

        /// <summary>The facts a schema states, assembled into the theory a container would ask.</summary>
        static CosmosFactTheory Compile(string json) => new(Read(json));

        // -- A declared geography ---------------------------------------------------------------

        static readonly CosmosDocumentPath Location = CosmosDocumentPath.Root.Property("location");

        static CosmosFactSet Facts(string json) => Compile(json).Derive(null);

        /// <summary>
        /// <c>$ref</c> to a published geometry schema beside an object type declares that the path holds a geography.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one claim here that is declared rather than derived, and it has to be: measured
        /// against an account, <c>{"type":"Point","coordinates":[999,999]}</c> validates against
        /// every GeoJSON subschema anyone could write and the service still answers undefined for a
        /// distance over it. <c>format</c> is annotation-only in JSON Schema, which is the standing
        /// every claim here has — the container's word, believed.
        /// </para>
        /// <para>
        /// What it buys is <c>CosmosSortRule.AlwaysDefined</c>: a distance over such a path is
        /// neither null nor undefined, so the <c>ORDER BY</c> reaches the service under Calcite's
        /// default placement rather than only under <c>LOW</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void AReferenceToTheGeoJsonSchemaDeclaresAGeography()
        {
            Facts("""
            { "type": "object",
              "properties": { "location": { "$ref": "https://geojson.org/schema/Geometry.json" } } }
            """)
                .IsAlwaysGeography(Location).Should().BeTrue();
        }

        /// <remarks>
        /// An object type alone is the claim that is <em>not</em> enough, and the distinction is the
        /// whole point: an object that is not a shape is a perfectly good object, and a distance over
        /// it is undefined at the service.
        /// </remarks>
        [Fact]
        public void AnObjectTypeAloneDeclaresNoGeography()
        {
            Facts("""
            { "type": "object",
              "required": ["location"],
              "properties": { "location": { "type": "object" } } }
            """)
                .IsAlwaysGeography(Location).Should().BeFalse();
        }

        /// <remarks>
        /// A format beside a scalar type constrains nothing this can use, and reading it anyway would
        /// claim of a string that the service can measure it — the same shape as a pattern written
        /// beside no declared type.
        /// </remarks>
        [Fact]
        public void AReferenceToAnythingElseDeclaresNoGeography()
        {
            Facts("""
            { "type": "object",
              "properties": { "location": { "$ref": "https://example.com/not-a-geometry.json" } } }
            """)
                .IsAlwaysGeography(Location).Should().BeFalse();
        }

        /// <summary>
        /// The claim carries its own presence, which is why the property need not be required.
        /// </summary>
        /// <remarks>
        /// Every other claim about a value is silent about whether the value is there — a schema's
        /// <c>properties</c> constrains what a path holds <em>if</em> it holds anything. This one is
        /// the exception, because there is no geography that is absent: declaring the format is
        /// declaring a shape is there. <c>CosmosFact.Entails</c> carries it.
        /// </remarks>
        [Fact]
        public void ADeclaredGeographyIsPresentAndAnObject()
        {
            var facts = Facts("""
            { "type": "object",
              "properties": { "location": { "$ref": "https://geojson.org/schema/Geometry.json" } } }
            """);

            facts.Knows(new CosmosFact(Location, new CosmosClaim.Present())).Should().BeTrue();
            facts.Knows(new CosmosFact(Location, new CosmosClaim.OfType(CosmosJsonType.Object))).Should().BeTrue();
            facts.IsAlwaysScalar(Location).Should().BeFalse("a shape is not a scalar");
        }

        static CosmosFact Equals(CosmosDocumentPath path, object? value) => new(path, new CosmosClaim.EqualTo(value));

        const string Parks = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$defs": {
            "uuidLower": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" }
          },
          "oneOf": [
            {
              "type": "object",
              "required": ["type"],
              "properties": {
                "type": { "const": "Park" },
                "data": { "type": "object", "required": ["id"], "properties": { "id": { "$ref": "#/$defs/uuidLower" } } }
              }
            },
            {
              "type": "object",
              "required": ["type"],
              "properties": {
                "type": { "const": "ParkMap" },
                "data": {
                  "type": "object",
                  "required": ["parkId"],
                  "properties": {
                    "parkId": { "$ref": "#/$defs/uuidLower" },
                    "at": { "type": "string", "format": "date-time",
                            "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$" }
                  }
                }
              }
            }
          ],
          "x-cosmos-note": "an unknown keyword is ignored, not refused"
        }
        """;

        [Fact]
        public void AFactUnderADiscriminatorNeedsTheDiscriminatorProven()
        {
            var theory = Compile(Parks);

            theory.Derive(null).RepresentationOf(ParkId).Should().BeNull(
                "a Park does not carry parkId at all, so nothing about it is known unconditionally");

            theory.Derive(new[] { Equals(Type, "Park") }).RepresentationOf(ParkId).Should().BeNull();

            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(ParkId)
                .Should().Be(CosmosUuidForms.CanonicalLower,
                    "the discriminator selects the branch, and the branch is where the pattern was declared");
        }

        [Fact]
        public void AReferenceIsFollowedToWhereThePatternIsDeclared()
        {
            // The pattern is not in the branch; it is in $defs, reached through a $ref.
            Compile(Parks).Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(ParkId)
                .Should().Be(CosmosUuidForms.CanonicalLower);
        }

        [Fact]
        public void RequiredIsRead_AndIsAboutTheChild()
        {
            var derived = Compile(Parks).Derive(new[] { Equals(Type, "ParkMap") });

            derived.Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeFalse(
                "required constrains an object and says nothing where there is no object, so a nested one waits on its parent");

            Compile(Parks)
                .Derive(new[] { Equals(Type, "ParkMap"), new CosmosFact(Data, new CosmosClaim.Present()) })
                .Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeTrue("and holds once the parent is known to be there");
            derived.Knows(new CosmosFact(Type, new CosmosClaim.Present())).Should().BeTrue();
            derived.Knows(new CosmosFact(At, new CosmosClaim.Present())).Should().BeFalse(
                "properties says what a value is, not that there is one; only required says that");

            Compile(Parks).Derive(new[] { Equals(Type, "Park") })
                .Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeFalse();
        }

        /// <summary>
        /// A date at one fixed shape and a canonical UUID both preserve order, each on its own
        /// argument about the stored alphabet.
        /// </summary>
        /// <remarks>
        /// The UUID half was the other way round until CALCITE-7716 made the engine's comparison
        /// unsigned in 1.43; <c>CalciteUuidOrderingMeasurementTests</c> is what says it is so, and
        /// <see cref="CosmosUuidForms.CanonicalLower"/> records what the claim now rests on.
        /// That the two bits remain independent is shown by the forms that still separate on them —
        /// an unpadded integer, and an instant at mixed precision.
        /// </remarks>
        [Fact]
        public void ADateAtOneFixedShapeAndACanonicalUuidBothPreserveOrder()
        {
            var derived = Compile(Parks).Derive(new[] { Equals(Type, "ParkMap") });

            derived.RepresentationOf(At)!.Value.PreservesOrder.Should().BeTrue(
                "one fixed ISO-8601 UTC shape sorts lexically the way it sorts chronologically");

            derived.RepresentationOf(ParkId)!.Value.PreservesOrder.Should().BeTrue(
                "a canonical lowercase spelling draws from 0-9a-f alone, over which ordinal text order is the unsigned 128-bit order the engine compares in");

            derived.RepresentationOf(ParkId)!.Value.PreservesEquality.Should().BeTrue();

            CosmosNumericForms.IntegerUnpadded.PreservesEquality.Should().BeTrue();
            CosmosNumericForms.IntegerUnpadded.PreservesOrder.Should().BeFalse(
                "'9' sorts after '42', so the two properties are still carried separately");
        }

        /// <summary>
        /// A confined UUID and a plain one are recognised as different forms, and under the unsigned
        /// comparison both preserve order.
        /// </summary>
        /// <remarks>
        /// The confined row is kept because it is what survives <c>calcite.uuid.unsigned.comparison</c>
        /// being turned off — see <see cref="CosmosUuidForms.CanonicalLowerSortable"/>. Which
        /// pattern yields which form is therefore still worth pinning, even where the licence is now
        /// the same.
        /// </remarks>
        [Fact]
        public void AFirstDigitConfinedUuidAndAPlainOneAreDistinctFormsAndBothSort()
        {
            const string Sortable = """
            { "type": "object", "properties": {
                "v7": { "type": "string", "pattern": "^[0-7][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" },
                "v4": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" },
                "loose": { "type": "string", "pattern": "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" }
            } }
            """;

            var derived = Compile(Sortable).Derive(null);

            derived.RepresentationOf(CosmosDocumentPath.Root.Property("v7")).Should().Be(CosmosUuidForms.CanonicalLowerSortable);
            derived.RepresentationOf(CosmosDocumentPath.Root.Property("v4")).Should().Be(CosmosUuidForms.CanonicalLower);

            CosmosUuidForms.CanonicalLowerSortable.PreservesOrder.Should().BeTrue("the confinement licenses the order under either comparison");
            CosmosUuidForms.CanonicalLower.PreservesOrder.Should().BeTrue("and the unsigned comparison licenses it without one");

            derived.RepresentationOf(CosmosDocumentPath.Root.Property("loose")).Should().BeNull(
                "a case-insensitive class is a different language and gets no entry, which is the whole point of recognising rather than probing");
        }

        /// <summary>
        /// Uppercase is as canonical as lowercase, and the two differ only in what a comparison has to
        /// render its literal into.
        /// </summary>
        /// <summary>
        /// The keywords that bear on a stored form, in combination.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Written as a table because the interactions are the whole difficulty and each was got wrong
        /// once. A <c>pattern</c> constrains a string and is vacuous for anything else, so it says
        /// nothing without a type. A <c>format</c> is an annotation and says nothing at all. Two types
        /// beside each other state only what both agree on. And a nullable string is still a string
        /// wherever it is not null, which is a weaker claim rather than no claim.
        /// </para>
        /// <para>
        /// The two type columns are the point of the nullability axis: a consumer that does not care
        /// whether a null is there asks the weaker question and gets an answer, while one that does
        /// care is not told something the schema did not say.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheKeywordsThatBearOnAStoredFormInCombination()
        {
            const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";
            const string Loose = "^[0-9a-fA-F]{8}-.*$";

            var lower = CosmosUuidForms.CanonicalLower;

            //     the subschema for $.v                                  form    string  string-or-null
            var cases = new (string Subschema, CosmosRepresentation? Form, bool Strict, bool OrNull)[]
            {
                ("{}",                                                    null,   false,  false),
                ("{ 'type': 'string' }",                                  null,   true,   true),
                ("{ 'type': 'string', 'pattern': 'P' }",                  lower,  true,   true),
                ("{ 'pattern': 'P' }",                                    null,   false,  false),
                ("{ 'format': 'uuid' }",                                  null,   false,  false),
                ("{ 'format': 'uuid', 'pattern': 'P' }",                  null,   false,  false),
                ("{ 'type': 'string', 'format': 'uuid' }",                null,   true,   true),
                ("{ 'type': 'string', 'format': 'uuid', 'pattern': 'P' }", lower, true,   true),

                // Nullable, both spellings. A weaker type claim, and the form survives: a null is not
                // a string and so is not a counterexample to how the strings are written.
                ("{ 'type': ['string','null'], 'pattern': 'P' }",         lower,  false,  true),
                ("{ 'type': 'string', 'nullable': true, 'pattern': 'P' }", lower, false,  true),
                ("{ 'type': ['null','string'] }",                         null,   false,  true),
                ("{ 'type': 'string', 'nullable': false, 'pattern': 'P' }", lower, true,  true),

                // Two real types agree on nothing, so neither is stated and the pattern goes with them.
                ("{ 'type': ['string','number'], 'pattern': 'P' }",       null,   false,  false),

                // A type that is not a string, and a pattern that is not one this knows.
                ("{ 'type': 'integer', 'pattern': 'P' }",                 null,   false,  false),
                ("{ 'type': 'string', 'pattern': 'L' }",                  null,   true,   true),
            };

            var v = CosmosDocumentPath.Root.Property("v");

            foreach (var (subschema, form, strict, orNull) in cases)
            {
                var json = ("{ 'properties': { 'v': " + subschema + " } }")
                    .Replace("'P'", "\"" + Uuid + "\"")
                    .Replace("'L'", "\"" + Loose + "\"")
                    .Replace('\'', '"');

                var derived = Compile(json).Derive(null);

                derived.RepresentationOf(v).Should().Be(form, "form for " + subschema);
                derived.Knows(new CosmosFact(v, new CosmosClaim.OfType(CosmosJsonType.String)))
                    .Should().Be(strict, "strict string for " + subschema);
                derived.Knows(new CosmosFact(v, new CosmosClaim.OfType(CosmosJsonType.String, OrNull: true)))
                    .Should().Be(orNull, "string-or-null for " + subschema);
            }
        }

        /// <summary>
        /// A pattern beside no declared type states nothing, because a pattern constrains a string and
        /// is vacuous for everything else.
        /// </summary>
        /// <remarks>
        /// The same shape as <c>properties</c> being vacuous for an absent path, one level over: a
        /// document storing the number 30 conforms to a bare <c>pattern</c>, and a stored form would
        /// have claimed the value is a string -- which is what deletes the guard admitting non-strings
        /// from a comparison that still has to decide one.
        /// </remarks>
        [Fact]
        public void APatternStatesNothingWithoutADeclaredStringType()
        {
            var reference = CosmosDocumentPath.Root.Property("ref");
            const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

            Compile("""{ "properties": { "ref": { "pattern": "PATTERN" } } }""".Replace("PATTERN", Uuid))
                .Derive(null).RepresentationOf(reference).Should().BeNull(
                    "a document storing a number conforms to it, and would have been claimed a string");

            Compile("""{ "properties": { "ref": { "type": "string", "pattern": "PATTERN" } } }""".Replace("PATTERN", Uuid))
                .Derive(null).RepresentationOf(reference).Should().Be(CosmosUuidForms.CanonicalLower,
                    "and says it once the type is there to make it say anything");
        }

        /// <summary>
        /// The spellings people actually write, which vary along more axes than the shape does.
        /// </summary>
        /// <remarks>
        /// Each row here was found in a published schema, a validator guide or the uuid package own
        /// documentation. They describe the same handful of languages and are written a dozen ways.
        /// </remarks>
        [Fact]
        public void TheSpellingsInTheWildAreRecognised()
        {
            var lower = CosmosUuidForms.CanonicalLower;
            var upper = CosmosUuidForms.CanonicalUpper;
            var sortable = CosmosUuidForms.CanonicalLowerSortable;

            var recognised = new (string Pattern, CosmosRepresentation? Expected)[]
            {
                // The class written the other way round, which is as common as the first.
                ("^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$", lower),
                ("^[A-F0-9]{8}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{12}$", upper),

                // Every RFC version nibble, and the ranges of them: the five RFC 4122 defined, the
                // eight RFC 9562 does, and a pair from a writer that generates both.
                ("^[a-f0-9]{8}-[a-f0-9]{4}-1[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$", lower),
                ("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", lower),
                ("^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", lower),
                ("^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", lower),
                ("^[0-9a-f]{8}-[0-9a-f]{4}-[45][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", lower),

                // The variant class written out of order, and written as ranges — one set, four
                // spellings.
                ("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[ba98][0-9a-f]{3}-[0-9a-f]{12}$", lower),
                ("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[8-9a-b][0-9a-f]{3}-[0-9a-f]{12}$", lower),

                // The hex class spelled out rather than ranged.
                ("^[0-9abcdef]{8}-[0-9abcdef]{4}-[0-9abcdef]{4}-[0-9abcdef]{4}-[0-9abcdef]{12}$", lower),

                // The nil-UUID alternation the uuid package documents, which is registered as the
                // plain row rather than the confined one: the nil value's variant nibble is 0 rather
                // than 8-b, so it is on the other side of the sign the confinement argument needs.
                ("(?:^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[a-f0-9]{4}-[a-f0-9]{12}$)|(?:^0{8}-0{4}-0{4}-0{4}-0{12}$)", lower),
                ("(?:^[a-f0-9]{8}-[a-f0-9]{4}-5[a-f0-9]{3}-[a-f0-9]{4}-[a-f0-9]{12}$)|(?:^0{8}-0{4}-0{4}-0{4}-0{12}$)", lower),

                // First digit confined, so the high half keeps its sign and the order is usable.
                ("^[0-7][a-f0-9]{7}-[a-f0-9]{4}-7[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$", sortable),

                // Whitespace, which means nothing outside a class.
                ("^[0-9a-f]{8} -[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", lower),

                // And the ones that must stay unrecognised. Case-insensitive throughout, and mixed in
                // the variant nibble alone -- both admit two spellings of one value.
                ("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", null),
                ("^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-4[a-fA-F0-9]{3}-[89abAB][a-fA-F0-9]{3}-[a-fA-F0-9]{12}$", null),
                ("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89abAB][0-9a-f]{3}-[0-9a-f]{12}$", null),

                // Unanchored, which admits a conforming value with anything around it.
                ("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", null),

                // And the range that looks like the set it is not: [8-f] spans 0x38-0x66, so it
                // admits A-F beside a-f and punctuation between them.
                ("^[8-f][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", null),
            };

            foreach (var (pattern, expected) in recognised)
                CosmosStoredForms.Recognise(pattern).Should().Be(expected, "for " + pattern);
        }

        /// <summary>
        /// The temporal shapes, across every axis a fixed spelling can vary along.
        /// </summary>
        /// <remarks>
        /// Each row is a language whose every field is the same width in every value, which is the
        /// whole of what makes its lexical order chronological. The shape itself does not matter —
        /// which is why they are generated rather than chosen between.
        /// </remarks>
        [Fact]
        public void EveryFixedTemporalShapeIsRecognisedAndSortable()
        {
            foreach (var pattern in new[]
            {
                // Every fraction width, including the nine digits a Java or Go writer produces and the
                // seven the round-trip format specifier does.
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{1}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9}Z$",

                // The zero offset written out, and no zone at all.
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\+00:00$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}\+00:00$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$",

                // The basic format, and minute precision.
                @"^[0-9]{8}T[0-9]{6}Z$",
                @"^[0-9]{8}T[0-9]{6}\.[0-9]{3}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}$",

                // Calendar and clock shapes that store only part of an instant.
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}$",
                @"^[0-9]{8}$",
                @"^[0-9]{4}-[0-9]{2}$",
                @"^[0-9]{2}:[0-9]{2}:[0-9]{2}$",
                @"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$",
                @"^[0-9]{2}:[0-9]{2}$",

                // The same languages written the other common way: \d for the class, and the run
                // spelled out rather than counted.
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
                @"^\d\d\d\d-\d\d-\d\dT\d\d:\d\d:\d\d\.\d\d\dZ$",
                @"^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$",
            })
            {
                var recognised = CosmosStoredForms.Recognise(pattern);

                recognised.Should().NotBeNull("for " + pattern);
                recognised!.Value.PreservesOrder.Should().BeTrue("a fixed width sorts chronologically, for " + pattern);
                recognised.Value.PreservesEquality.Should().BeTrue("and has one spelling per value, for " + pattern);
            }
        }

        /// <summary>
        /// The shapes that vary, which are the ones the ordering argument fails for.
        /// </summary>
        /// <remarks>
        /// Each admits two widths for one path, and the wider sorts below the narrower wherever the
        /// character that follows the fraction outranks <c>'.'</c>. The .NET SDK's default serializer
        /// writes the first of these, which is why it is the common case rather than the exotic one.
        /// </remarks>
        [Fact]
        public void AShapeThatVariesIsNotRecognised()
        {
            foreach (var pattern in new[]
            {
                // An optional fraction, and a fraction of a range of widths -- the SDK's own output.
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{1,7}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]*Z$",

                // An offset that is not pinned to zero, which orders two instants by their local
                // clocks rather than by when they happened.
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[+-][0-9]{2}:[0-9]{2}$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(Z|\+00:00)$",

                // Unanchored, which admits a conforming value with anything around it.
                @"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z",
            })
                CosmosStoredForms.Recognise(pattern).Should().BeNull("for " + pattern);
        }

        /// <summary>
        /// A space-separated instant is not recognised, and the reason is the normaliser rather than
        /// the shape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>2024-01-15 12:30:00Z</c> is as fixed as its <c>T</c> spelling and would sort exactly as
        /// well — Python's <c>isoformat(sep=' ')</c> and a good many SQL exports write it. What stops
        /// it is that <see cref="CosmosStoredForms.Normalise"/> strips whitespace outside a character
        /// class, so the pattern cannot be told apart from one written with no separator at all, and
        /// registering it would claim this form for that one.
        /// </para>
        /// <para>
        /// Recorded as a test rather than a comment because the fix is a one-line change to the
        /// normaliser — whitespace is significant in unextended ECMA-262, which is the dialect JSON
        /// Schema specifies — and this is the row that would flip when someone makes it. Until then the
        /// failure is the safe direction: unrecognised, so the comparison stays in process.
        /// </para>
        /// </remarks>
        [Fact]
        public void ASpaceSeparatedInstantIsNotYetRecognised()
        {
            CosmosStoredForms.Recognise(@"^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}Z$")
                .Should().BeNull("the separator does not survive normalisation");
        }

        /// <summary>
        /// Each form writes a literal in its own spelling, and refuses a value it cannot hold.
        /// </summary>
        [Fact]
        public void EachTemporalFormWritesItsOwnSpelling()
        {
            var value = new System.DateTime(2024, 1, 15, 12, 30, 0, System.DateTimeKind.Utc);

            string? Render(string pattern) =>
                CosmosStoredForms.RenderDateTime(CosmosStoredForms.Recognise(pattern)!.Value, value);

            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$").Should().Be("2024-01-15T12:30:00Z");
            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$").Should().Be("2024-01-15T12:30:00.0000000Z");
            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9}Z$").Should().Be("2024-01-15T12:30:00.000000000Z",
                "nine digits is seven padded, which holds every value a DateTime carries");
            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\+00:00$").Should().Be("2024-01-15T12:30:00+00:00");
            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$").Should().Be("2024-01-15T12:30:00");
            Render(@"^[0-9]{8}T[0-9]{6}Z$").Should().Be("20240115T123000Z");
            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$").Should().Be("2024-01-15T12:30Z");

            // Values that do not land on the form are refused rather than truncated.
            Render(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$").Should().BeNull("the value carries a time of day");
            Render(@"^[0-9]{4}-[0-9]{2}$").Should().BeNull("and a day the year-month form does not store");

            // A clock with no date holds no instant at all, so nothing is written into one -- while it
            // is still recognised, which is what licenses a sort over it.
            Render(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}$").Should().BeNull("writing an instant here would drop the date");

            var midnight = new System.DateTime(2024, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            CosmosStoredForms.RenderDateTime(CosmosStoredForms.Recognise(@"^[0-9]{4}-[0-9]{2}$")!.Value, midnight)
                .Should().Be("2024-01", "a value that does land on the form is written");
        }

        [Fact]
        public void AnUppercaseSpellingIsCanonicalToo()
        {
            const string Upper = """
            { "properties": { "ref": { "type": "string",
                "pattern": "^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$" } } }
            """;

            var reference = CosmosDocumentPath.Root.Property("ref");
            var derived = Compile(Upper).Derive(null);

            derived.RepresentationOf(reference).Should().Be(CosmosUuidForms.CanonicalUpper);

            // And the two are different forms, not one form read twice: a container is written one way
            // or the other, and a schema admitting both spellings is canonical at neither.
            Compile("""
            { "properties": { "ref": { "type": "string",
                "pattern": "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" } } }
            """).Derive(null).RepresentationOf(reference).Should().BeNull();

            // The first hex digit decides ordering the same way it does in lowercase, 0-9 sorting
            // before A-F as it sorts before a-f.
            Compile("""
            { "properties": { "ref": { "type": "string",
                "pattern": "^[0-7][0-9A-F]{7}-[0-9A-F]{4}-7[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$" } } }
            """).Derive(null).RepresentationOf(reference).Should().Be(CosmosUuidForms.CanonicalUpperSortable);
        }

        [Fact]
        public void AnUndiscriminatedBranchYieldsOnlyWhatEveryBranchAgreesOn()
        {
            const string Vague = """
            { "anyOf": [
                { "properties": { "a": { "type": "string" }, "b": { "type": "integer" } } },
                { "properties": { "a": { "type": "string" }, "b": { "type": "boolean" } } }
            ] }
            """;

            var derived = Compile(Vague).Derive(null);

            derived.Knows(new CosmosFact(CosmosDocumentPath.Root.Property("a"), new CosmosClaim.OfType(CosmosJsonType.String)))
                .Should().BeTrue("both branches say so");

            derived.Knows(new CosmosFact(CosmosDocumentPath.Root.Property("b"), new CosmosClaim.OfType(CosmosJsonType.Integer)))
                .Should().BeFalse("only one branch says so, and nothing selects it");
        }

        [Fact]
        public void IfThenIsReadAsAGuard()
        {
            const string Conditional = """
            {
              "if": { "properties": { "kind": { "const": "map" } } },
              "then": { "properties": { "at": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" } } }
            }
            """;

            var theory = Compile(Conditional);
            var at = CosmosDocumentPath.Root.Property("at");

            theory.Derive(null).RepresentationOf(at).Should().BeNull();
            theory.Derive(new[] { Equals(CosmosDocumentPath.Root.Property("kind"), "map") }).RepresentationOf(at)
                .Should().Be(CosmosTemporalForms.Iso8601Date);
        }

        [Fact]
        public void AConditionCarryingAnythingNotUnderstoodContributesNothing()
        {
            // The condition is "kind is 'map' AND serial matches ^S". Reading only the first half
            // would be a weaker guard, and a weaker guard applies the branch's facts to documents the
            // schema never promised them for.
            const string PartlyUnderstood = """
            {
              "if": { "properties": { "kind": { "const": "map" }, "serial": { "pattern": "^S" } } },
              "then": { "properties": { "at": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" } } }
            }
            """;

            var at = CosmosDocumentPath.Root.Property("at");

            Compile(PartlyUnderstood).Derive(new[] { Equals(CosmosDocumentPath.Root.Property("kind"), "map") })
                .RepresentationOf(at).Should().BeNull(
                    "the whole condition has to be understood, or the guard is weaker than the schema's");
        }

        [Fact]
        public void AnElseIsReachableWhenTheConditionIsOneEquality()
        {
            const string Conditional = """
            {
              "if": { "properties": { "kind": { "const": "map" } } },
              "then": { "properties": { "at": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" } } },
              "else": { "properties": { "at": { "type": "integer" } } }
            }
            """;

            var at = CosmosDocumentPath.Root.Property("at");
            var kind = CosmosDocumentPath.Root.Property("kind");
            var theory = Compile(Conditional);

            theory.Derive(new[] { Equals(kind, "trail") }).Knows(new CosmosFact(at, new CosmosClaim.OfType(CosmosJsonType.Integer)))
                .Should().BeTrue("an equality to a different value discharges the negative premise");

            theory.Derive(new[] { Equals(kind, "map") }).Knows(new CosmosFact(at, new CosmosClaim.OfType(CosmosJsonType.Integer)))
                .Should().BeFalse();
        }

        [Fact]
        public void AnEnumIsADomainAndADiscriminatorIsAValue()
        {
            const string Enumerated = """
            { "type": "object", "properties": { "status": { "enum": ["open", "closed"] } } }
            """;

            var status = CosmosDocumentPath.Root.Property("status");
            var derived = Compile(Enumerated).Derive(null);

            derived.Knows(new CosmosFact(status, new CosmosClaim.OneOf(new object?[] { "open", "closed" }))).Should().BeTrue();
            derived.Knows(new CosmosFact(status, new CosmosClaim.OfType(CosmosJsonType.String))).Should().BeTrue(
                "every member is a string, so the type follows from the domain");
            derived.Knows(new CosmosFact(status, new CosmosClaim.NotEqualTo("archived"))).Should().BeTrue();
        }

        /// <summary>
        /// An OpenAPI discriminator, which is how a generated document writes a multi-type container:
        /// the branches are bare references that repeat no <c>const</c>, and a mapping says which
        /// value selects which.
        /// </summary>
        [Fact]
        public void AnOpenApiDiscriminatorSelectsABranchThroughItsMapping()
        {
            const string Mapped = """
            {
              "$defs": {
                "Order": { "properties": { "placed": { "type": "string" } } },
                "Shipment": { "properties": { "ref": { "type": "string",
                              "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
              },
              "oneOf": [ { "$ref": "#/$defs/Order" }, { "$ref": "#/$defs/Shipment" } ],
              "discriminator": {
                "propertyName": "kind",
                "mapping": { "order": "#/$defs/Order", "shipment": "#/$defs/Shipment" }
              }
            }
            """;

            var theory = Compile(Mapped);
            var reference = CosmosDocumentPath.Root.Property("ref");
            var kind = CosmosDocumentPath.Root.Property("kind");

            theory.Derive(null).RepresentationOf(reference).Should().BeNull();
            theory.Derive(new[] { Equals(kind, "order") }).RepresentationOf(reference).Should().BeNull();
            theory.Derive(new[] { Equals(kind, "shipment") }).RepresentationOf(reference)
                .Should().Be(CosmosUuidForms.CanonicalLower);
        }

        /// <summary>
        /// And with no mapping, OpenAPI's implicit rule: the value is the schema's own name.
        /// </summary>
        [Fact]
        public void AnOpenApiDiscriminatorWithNoMappingUsesTheSchemaName()
        {
            const string Implicit = """
            {
              "$defs": {
                "Order": { "properties": { "placed": { "type": "string" } } },
                "Shipment": { "properties": { "ref": { "type": "string",
                              "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
              },
              "oneOf": [ { "$ref": "#/$defs/Order" }, { "$ref": "#/$defs/Shipment" } ],
              "discriminator": { "propertyName": "kind" }
            }
            """;

            Compile(Implicit).Derive(new[] { Equals(CosmosDocumentPath.Root.Property("kind"), "Shipment") })
                .RepresentationOf(CosmosDocumentPath.Root.Property("ref"))
                .Should().Be(CosmosUuidForms.CanonicalLower);
        }

        /// <summary>
        /// A nested <c>$id</c> moves the base a reference resolves against, and resolution here is
        /// against the root — so nothing is followed at all rather than followed to the wrong node.
        /// </summary>
        [Fact]
        public void ASchemaThatRebasesItsReferencesIsNotFollowed()
        {
            const string Rebased = """
            {
              "$defs": { "inner": { "$id": "https://example.test/inner",
                                    "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" } },
              "properties": { "at": { "$ref": "#/$defs/inner" } }
            }
            """;

            Compile(Rebased).Derive(null).RepresentationOf(CosmosDocumentPath.Root.Property("at"))
                .Should().BeNull("a reference resolved against the wrong base states facts about the wrong path");
        }

        [Fact]
        public void ARecursiveReferenceTerminates()
        {
            const string Recursive = """
            {
              "$defs": { "node": { "type": "object", "properties": { "child": { "$ref": "#/$defs/node" },
                                                                     "id": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" } } } },
              "$ref": "#/$defs/node"
            }
            """;

            var theory = Compile(Recursive);

            theory.Rules.Should().NotBeEmpty();
            theory.Derive(null).RepresentationOf(CosmosDocumentPath.Root.Property("id"))
                .Should().Be(CosmosTemporalForms.Iso8601Date);
        }

        /// <summary>
        /// The two keywords that key a constraint on a property merely being there, and the one shape
        /// of <c>not</c> whose negation is a conjunction.
        /// </summary>
        [Fact]
        public void PresenceKeyedConditionalsAndANegationAreRead()
        {
            var a = CosmosDocumentPath.Root.Property("a");
            var b = CosmosDocumentPath.Root.Property("b");
            var k = CosmosDocumentPath.Root.Property("k");

            // dependentRequired: b is there whenever a is, and not before.
            var dependent = Compile("""{ "dependentRequired": { "a": ["b"] } }""");

            dependent.Derive(null).Knows(new CosmosFact(b, new CosmosClaim.Present())).Should().BeFalse();
            dependent.Derive(new[] { new CosmosFact(a, new CosmosClaim.Present()) })
                .Knows(new CosmosFact(b, new CosmosClaim.Present())).Should().BeTrue();

            // dependentSchemas: the same trigger, carrying a whole subschema.
            var schemas = Compile("""
            { "dependentSchemas": { "a": { "properties": { "b": { "type": "string",
                "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" } } } } }
            """);

            schemas.Derive(null).RepresentationOf(b).Should().BeNull();
            schemas.Derive(new[] { new CosmosFact(a, new CosmosClaim.Present()) }).RepresentationOf(b)
                .Should().Be(CosmosTemporalForms.Iso8601Date);

            // not: failing {properties: {k: {const: "A"}}} means k is there and is not "A" -- the
            // negation of the schema, which is vacuous on an absent k, rather than of the atom.
            var negated = Compile("""{ "not": { "properties": { "k": { "const": "A" } } } }""").Derive(null);

            negated.Knows(new CosmosFact(k, new CosmosClaim.Present())).Should().BeTrue();
            negated.Knows(new CosmosFact(k, new CosmosClaim.NotEqualTo("A"))).Should().BeTrue();
            negated.Knows(new CosmosFact(k, new CosmosClaim.NotEqualTo("B"))).Should().BeFalse();

            // Two constraints under a not is a disjunction over which of them failed, and yields none.
            Compile("""{ "not": { "properties": { "k": { "const": "A" }, "j": { "const": "B" } } } }""")
                .Derive(null).Knows(new CosmosFact(k, new CosmosClaim.Present())).Should().BeFalse();
        }

        /// <summary>
        /// Keywords that constrain nothing this model can state, and one that quietly unstates a type.
        /// </summary>
        [Fact]
        public void WhatIsNotInterpretedStatesNothing()
        {
            // OpenAPI 3.0 puts nullability beside the type rather than inside it.
            Compile("""{ "properties": { "a": { "type": "string", "nullable": true } } }""")
                .Derive(null).Knows(new CosmosFact(CosmosDocumentPath.Root.Property("a"), new CosmosClaim.OfType(CosmosJsonType.String)))
                .Should().BeFalse("a stored null conforms to it, so the type is not what the schema claimed");

            // 2020-12 spells the same thing as a union, and a union states only what its members agree on.
            Compile("""{ "properties": { "a": { "type": ["string", "null"] } } }""")
                .Derive(null).Knows(new CosmosFact(CosmosDocumentPath.Root.Property("a"), new CosmosClaim.OfType(CosmosJsonType.String)))
                .Should().BeFalse();

            // $defs is a place to put schemas, not a constraint on the document, so nothing under it
            // becomes a fact about a path of the same name.
            Compile("""{ "$defs": { "a": { "type": "string" } } }""")
                .Derive(null).Knows(new CosmosFact(CosmosDocumentPath.Root.Property("a"), new CosmosClaim.OfType(CosmosJsonType.String)))
                .Should().BeFalse();

            // Constraints with no claim in the model, and applicators not interpreted, are ignored
            // rather than refused -- a schema carrying them still yields what it does say.
            var mixed = Compile("""
            {
              "properties": { "a": { "type": "string", "minLength": 3 },
                              "b": { "type": "integer", "minimum": 0 } },
              "additionalProperties": false,
              "patternProperties": { "^x": { "type": "string" } },
              "dependentRequired": { "a": ["b"] },
              "not": { "properties": { "c": { "type": "string" } } }
            }
            """).Derive(null);

            mixed.Knows(new CosmosFact(CosmosDocumentPath.Root.Property("a"), new CosmosClaim.OfType(CosmosJsonType.String))).Should().BeTrue();
            mixed.Knows(new CosmosFact(CosmosDocumentPath.Root.Property("b"), new CosmosClaim.OfType(CosmosJsonType.Integer))).Should().BeTrue();
            mixed.Knows(new CosmosFact(CosmosDocumentPath.Root.Property("c"), new CosmosClaim.OfType(CosmosJsonType.String))).Should().BeFalse(
                "a negated schema states nothing, and reading it as though it did would invert the claim");
        }

        [Fact]
        public void AnUnreadableSchemaIsNoFactsRatherThanAFailure()
        {
            CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree("[]"))
                .Should().BeEmpty("a declaration meant to add pushdowns must never be a reason a query stops working");

            Compile("""{ "type": "object", "properties": { "a": { "minimum": 3, "maxLength": 9 } } }""")
                .Derive(null).RepresentationOf(CosmosDocumentPath.Root.Property("a")).Should().BeNull();
        }

    }

}
