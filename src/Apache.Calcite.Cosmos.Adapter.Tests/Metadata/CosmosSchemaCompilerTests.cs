using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    [TestClass]
    public class CosmosSchemaCompilerTests
    {

        static readonly CosmosDocumentPath Type = CosmosDocumentPath.Root.Property("type");
        static readonly CosmosDocumentPath Data = CosmosDocumentPath.Root.Property("data");
        static readonly CosmosDocumentPath ParkId = Data.Property("parkId");
        static readonly CosmosDocumentPath At = Data.Property("at");

        static CosmosFactTheory Compile(string json) =>
            CosmosSchemaCompiler.Compile(new com.fasterxml.jackson.databind.ObjectMapper().readTree(json));

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

        [TestMethod]
        public void AFactUnderADiscriminatorNeedsTheDiscriminatorProven()
        {
            var theory = Compile(Parks);

            theory.Derive(null).RepresentationOf(ParkId).Should().BeNull(
                "a Park does not carry parkId at all, so nothing about it is known unconditionally");

            theory.Derive(new[] { Equals(Type, "Park") }).RepresentationOf(ParkId).Should().BeNull();

            theory.Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(ParkId)
                .Should().Be(CosmosStoredForms.UuidCanonicalLower,
                    "the discriminator selects the branch, and the branch is where the pattern was declared");
        }

        [TestMethod]
        public void AReferenceIsFollowedToWhereThePatternIsDeclared()
        {
            // The pattern is not in the branch; it is in $defs, reached through a $ref.
            Compile(Parks).Derive(new[] { Equals(Type, "ParkMap") }).RepresentationOf(ParkId)
                .Should().Be(CosmosStoredForms.UuidCanonicalLower);
        }

        [TestMethod]
        public void RequiredIsRead_AndIsAboutTheChild()
        {
            var derived = Compile(Parks).Derive(new[] { Equals(Type, "ParkMap") });

            derived.Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeTrue();
            derived.Knows(new CosmosFact(Type, new CosmosClaim.Present())).Should().BeTrue();

            Compile(Parks).Derive(new[] { Equals(Type, "Park") })
                .Knows(new CosmosFact(ParkId, new CosmosClaim.Present())).Should().BeFalse();
        }

        [TestMethod]
        public void ADateAtOneFixedShapePreservesOrderWhereAUuidDoesNot()
        {
            var derived = Compile(Parks).Derive(new[] { Equals(Type, "ParkMap") });

            derived.RepresentationOf(At)!.Value.PreservesOrder.Should().BeTrue(
                "one fixed ISO-8601 UTC shape sorts lexically the way it sorts chronologically");

            derived.RepresentationOf(ParkId)!.Value.PreservesOrder.Should().BeFalse(
                "Calcite compares UUIDs as two signed halves, so the canonical string does not sort in its order");

            derived.RepresentationOf(ParkId)!.Value.PreservesEquality.Should().BeTrue();
        }

        [TestMethod]
        public void AFirstDigitConfinedUuidIsSortableAndAPlainOneIsNot()
        {
            const string Sortable = """
            { "type": "object", "properties": {
                "v7": { "type": "string", "pattern": "^[0-7][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" },
                "v4": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" },
                "loose": { "type": "string", "pattern": "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" }
            } }
            """;

            var derived = Compile(Sortable).Derive(null);

            derived.RepresentationOf(CosmosDocumentPath.Root.Property("v7")).Should().Be(CosmosStoredForms.UuidCanonicalLowerSortable);
            derived.RepresentationOf(CosmosDocumentPath.Root.Property("v4")).Should().Be(CosmosStoredForms.UuidCanonicalLower);
            derived.RepresentationOf(CosmosDocumentPath.Root.Property("loose")).Should().BeNull(
                "a case-insensitive class is a different language and gets no entry, which is the whole point of recognising rather than probing");
        }

        [TestMethod]
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

        [TestMethod]
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
                .Should().Be(CosmosStoredForms.Iso8601Date);
        }

        [TestMethod]
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

        [TestMethod]
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

        [TestMethod]
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

        [TestMethod]
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
                .Should().Be(CosmosStoredForms.Iso8601Date);
        }

        [TestMethod]
        public void AnUnreadableSchemaIsNoFactsRatherThanAFailure()
        {
            CosmosSchemaCompiler.Compile(new com.fasterxml.jackson.databind.ObjectMapper().readTree("[]"))
                .IsEmpty.Should().BeTrue("a declaration meant to add pushdowns must never be a reason a query stops working");

            Compile("""{ "type": "object", "properties": { "a": { "minimum": 3, "maxLength": 9 } } }""")
                .Derive(null).RepresentationOf(CosmosDocumentPath.Root.Property("a")).Should().BeNull();
        }

    }

}
