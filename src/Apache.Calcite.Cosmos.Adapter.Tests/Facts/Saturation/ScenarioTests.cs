using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// What the rules of JSON decide about a query's predicates from a schema's facts, case by case. The soundness
    /// tests say nothing derived is false; these say what is derived.
    /// </summary>
    public sealed class ScenarioTests
    {

        static readonly JsonContexts Contexts = new();

        /// <summary>Opens a context holding the facts, mentioning the predicate's path, and decides the predicate.</summary>
        static Truth Decide(JsonAtom atom, params JsonFact[] facts)
        {
            using var scope = Contexts.Open(null, facts, new[] { atom.Path });
            return Contexts.Decide(scope, atom);
        }

        static bool Contradictory(params JsonFact[] facts)
        {
            using var scope = Contexts.Open(null, facts);
            return scope.IsContradictory;
        }

        static JsonFact.OfType OfType(string path, params string[] types) => new(path, Types(types));

        // A property that is a string cannot be an object, so it has no properties.

        [Fact]
        public void AStringHasNoProperties()
        {
            Decide(new JsonAtom.Defined("$.data.id"), OfType("$.data", "string")).Should().Be(Truth.Never);
            Decide(new JsonAtom.EqualTo("$.data.id", Of("x")), OfType("$.data", "string")).Should().Be(Truth.Never);
            Contradictory(OfType("$.data", "string"), new JsonFact.Present("$.data.id")).Should().BeTrue();
        }

        [Fact]
        public void TwoTypesThatShareNoValueMeanThePathIsAbsent()
        {
            // Not a contradiction: a document without the path satisfies both. Only presence makes it one.
            Contradictory(OfType("$.v", "string"), OfType("$.v", "object")).Should().BeFalse();
            Decide(new JsonAtom.Defined("$.v"), OfType("$.v", "string"), OfType("$.v", "object")).Should().Be(Truth.Never);
            Contradictory(OfType("$.v", "string"), OfType("$.v", "object"), new JsonFact.Present("$.v")).Should().BeTrue();
        }

        // A number cannot be a different number.

        [Fact]
        public void ANumberIsNotAnotherNumber()
        {
            var one = new JsonFact.OneOf("$.n", ValueSet.Of(Of(1m)));
            Decide(new JsonAtom.EqualTo("$.n", Of(2m)), one).Should().Be(Truth.Never);
            Decide(new JsonAtom.EqualTo("$.n", Of(1m)), one).Should().Be(Truth.Unknown);
            Decide(new JsonAtom.EqualTo("$.n", Of(1m)), one, new JsonFact.Present("$.n")).Should().Be(Truth.Always);
            Decide(new JsonAtom.EqualTo("$.n", Of(1.0m)), one, new JsonFact.Present("$.n")).Should().Be(Truth.Always, "1.0 is 1");
        }

        // Values that do not exist cannot be null.

        [Fact]
        public void AnAbsentValueIsNotNull()
        {
            var absent = new JsonFact.Absent("$.x");
            Decide(new JsonAtom.EqualTo("$.x", NullValue), absent).Should().Be(Truth.Never);
            Decide(new JsonAtom.HasType("$.x", "null"), absent).Should().Be(Truth.Never);
        }

        [Fact]
        public void AValueThatIsNotNullIsNotOfTypeNull()
        {
            Decide(new JsonAtom.HasType("$.x", "null"), new JsonFact.NotEqual("$.x", NullValue)).Should().Be(Truth.Never);
        }

        // If an object is not defined, its properties do not exist, and neither do theirs.

        [Fact]
        public void AnUndefinedObjectHasNoProperties()
        {
            Decide(new JsonAtom.Defined("$.a.b.c"), new JsonFact.Absent("$.a")).Should().Be(Truth.Never);
            Decide(new JsonAtom.EqualTo("$.a.b.c", Of("x")), new JsonFact.Absent("$.a")).Should().Be(Truth.Never);
        }

        [Fact]
        public void APresentPropertyHasAPresentObjectForAParent()
        {
            Decide(new JsonAtom.HasType("$.a.b", "object"), new JsonFact.Present("$.a.b.c")).Should().Be(Truth.Always);
            Decide(new JsonAtom.Defined("$.a"), new JsonFact.Present("$.a.b.c")).Should().Be(Truth.Always);
        }

        // A schema declaring a ten-item array says the eleventh item does not exist, and that proves other
        // predicates false.

        [Fact]
        public void TheEleventhOfTenItemsDoesNotExist()
        {
            var ten = new JsonFact.MaxItems("$.tags", 10);
            Decide(new JsonAtom.Defined("$.tags[10]"), ten).Should().Be(Truth.Never);
            Decide(new JsonAtom.EqualTo("$.tags[10]", Of("x")), ten).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.tags[10].name"), ten).Should().Be(Truth.Never);
            Decide(new JsonAtom.LengthIn("$.tags", 11, null), ten).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.tags[9]"), ten).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AnArrayWithAnEleventhItemIsNotATenItemArray()
        {
            // The query proves the eleventh is there; the schema says no array there holds one; so the path is no
            // array at all, which a present element contradicts.
            Contradictory(new JsonFact.MaxItems("$.tags", 10), new JsonFact.Present("$.tags[10]")).Should().BeTrue();
        }

        [Fact]
        public void AnItemWithinTheLeastAPresentArrayHoldsIsThere()
        {
            Decide(new JsonAtom.Defined("$.tags[1]"),
                new JsonFact.MinItems("$.tags", 2), new JsonFact.Present("$.tags"), OfType("$.tags", "array")).Should().Be(Truth.Always);
        }

        // A fact about every item of an array is a fact about each item.

        [Fact]
        public void AFactAboutEveryItemIsAFactAboutEachItem()
        {
            var strings = OfType("$.tags[*]", "string");
            Decide(new JsonAtom.EqualTo("$.tags[3]", Of(5m)), strings).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.tags[3].x"), strings).Should().Be(Truth.Never);
            Decide(new JsonAtom.HasType("$.tags[3]", "string"), strings, new JsonFact.Present("$.tags[3]")).Should().Be(Truth.Always);
        }

        [Fact]
        public void APropertyRequiredOfEveryItemIsOnEachPresentItem()
        {
            Decide(new JsonAtom.Defined("$.orders[2].id"),
                OfType("$.orders[*]", "object"), new JsonFact.Requires("$.orders[*]", "id"), new JsonFact.Present("$.orders[2]")).Should().Be(Truth.Always);
        }

        [Fact]
        public void AFactAboutEveryItemOfEveryItemReachesANestedItem()
        {
            var digits = new JsonFact.OneOf("$.m[*][*]", ValueSet.Of(Of(1m), Of(2m)));
            Decide(new JsonAtom.EqualTo("$.m[1][0]", Of(3m)), digits).Should().Be(Truth.Never);
        }

        [Fact]
        public void AFactAboutAPropertyOfEveryItemReachesThatPropertyOfEachItem()
        {
            var kinds = new JsonFact.OneOf("$.lines[*].kind", ValueSet.Of(Of("debit"), Of("credit")));
            Decide(new JsonAtom.EqualTo("$.lines[4].kind", Of("refund")), kinds).Should().Be(Truth.Never);
        }

        [Fact]
        public void AnArrayHoldsNoValueItsItemsCannotBe()
        {
            var ab = new JsonFact.OneOf("$.tags[*]", ValueSet.Of(Of("a"), Of("b")));
            Decide(new JsonAtom.ArrayHolds("$.tags", Of("c")), ab).Should().Be(Truth.Never);
            Decide(new JsonAtom.ArrayHolds("$.tags", Of("a")), ab).Should().Be(Truth.Unknown);
            Contradictory(ab, new JsonFact.Holds("$.tags", Of("c"))).Should().BeTrue();
        }

        [Fact]
        public void AnArrayWhoseItemsHaveNoTypeHasNoItems()
        {
            // `items: false`.
            var none = new JsonFact.OfType("$.tags[*]", ValueSet.Of<string>());
            Decide(new JsonAtom.Defined("$.tags[0]"), none).Should().Be(Truth.Never);
            Decide(new JsonAtom.ArrayHolds("$.tags", Of("a")), none).Should().Be(Truth.Never);
        }

        // Numbers and their bounds.

        [Fact]
        public void AComparisonBeyondADeclaredBoundIsNever()
        {
            // The integration plan's gap 4.
            Decide(new JsonAtom.NumberIn("$.n", Bound.Above(500), null), new JsonFact.Upper("$.n", Bound.AtMost(100))).Should().Be(Truth.Never);
            Decide(new JsonAtom.NumberIn("$.n", Bound.Above(50), null), new JsonFact.Upper("$.n", Bound.AtMost(100))).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AnIntegerBetweenBoundsCloserThanOneIsNotThere()
        {
            Decide(new JsonAtom.Defined("$.n"),
                OfType("$.n", "integer"), new JsonFact.Lower("$.n", Bound.Above(499)), new JsonFact.Upper("$.n", Bound.Below(500))).Should().Be(Truth.Never);
        }

        [Fact]
        public void AnEnumOfIntegersOutsideARangeIsNever()
        {
            // The integration plan's gap 5.
            var small = new JsonFact.OneOf("$.n", ValueSet.Of(Of(1m), Of(2m), Of(3m)));
            Decide(new JsonAtom.NumberIn("$.n", Bound.Above(499), Bound.Below(501)), small).Should().Be(Truth.Never);
        }

        // Strings, booleans, objects.

        [Fact]
        public void AStringLongerThanTheMostAllowedIsNever()
        {
            Decide(new JsonAtom.EqualTo("$.code", Of("abc")), new JsonFact.MaxLength("$.code", 2)).Should().Be(Truth.Never);
        }

        [Fact]
        public void ABooleanThatIsNotTrueIsFalse()
        {
            Decide(new JsonAtom.EqualTo("$.flag", Of(false)),
                OfType("$.flag", "boolean"), new JsonFact.NotEqual("$.flag", Of(true)), new JsonFact.Present("$.flag")).Should().Be(Truth.Always);
        }

        [Fact]
        public void AClosedObjectHasNoOtherProperties()
        {
            Decide(new JsonAtom.Defined("$.other"), new JsonFact.ClosedTo("$", ValueSet.Of("id", "kind"))).Should().Be(Truth.Never);
        }

        [Fact]
        public void ARequiredPropertyOfADocumentIsThere()
        {
            // Every Cosmos DB item is a present object, which the root context states.
            Decide(new JsonAtom.Defined("$.kind"), new JsonFact.Requires("$", "kind")).Should().Be(Truth.Always);
        }

        [Fact]
        public void WhatIsNotProvenStaysUnknown()
        {
            Decide(new JsonAtom.Defined("$.a"), OfType("$.a", "string")).Should().Be(Truth.Unknown);
            Decide(new JsonAtom.EqualTo("$.a", Of("x")), OfType("$.a", "string")).Should().Be(Truth.Unknown);
        }

        // multipleOf, and integer ranges.

        [Fact]
        public void ANumberThatIsNotAMultipleIsNever()
        {
            Decide(new JsonAtom.EqualTo("$.n", Of(7m)), new JsonFact.MultipleOf("$.n", 5)).Should().Be(Truth.Never);
            Decide(new JsonAtom.EqualTo("$.n", Of(15m)), new JsonFact.MultipleOf("$.n", 5)).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AMultipleOfAWholeNumberIsAnInteger()
        {
            Decide(new JsonAtom.HasType("$.n", "integer"),
                new JsonFact.MultipleOf("$.n", 2), OfType("$.n", "number"), new JsonFact.Present("$.n")).Should().Be(Truth.Always);
        }

        [Fact]
        public void TwoMultiplesAreAMultipleOfTheirLeastCommonMultiple()
        {
            Decide(new JsonAtom.EqualTo("$.n", Of(6m)), new JsonFact.MultipleOf("$.n", 4), new JsonFact.MultipleOf("$.n", 6)).Should().Be(Truth.Never);
            Decide(new JsonAtom.EqualTo("$.n", Of(12m)), new JsonFact.MultipleOf("$.n", 4), new JsonFact.MultipleOf("$.n", 6)).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void NoMultipleBetweenBoundsMeansNoNumber()
        {
            Decide(new JsonAtom.HasType("$.n", "number"),
                new JsonFact.MultipleOf("$.n", 10), new JsonFact.Lower("$.n", Bound.AtLeast(11)), new JsonFact.Upper("$.n", Bound.AtMost(19))).Should().Be(Truth.Never);
        }

        [Fact]
        public void AnIntegerInASmallRangeIsOneOfItsValues()
        {
            JsonFact[] facts =
            {
                OfType("$.n", "integer"), new JsonFact.Lower("$.n", Bound.AtLeast(1)), new JsonFact.Upper("$.n", Bound.AtMost(3)),
                new JsonFact.NotEqual("$.n", Of(1m)), new JsonFact.NotEqual("$.n", Of(3m)), new JsonFact.Present("$.n"),
            };
            Decide(new JsonAtom.EqualTo("$.n", Of(2m)), facts).Should().Be(Truth.Always);
            Contradictory(facts.Append(new JsonFact.NotEqual("$.n", Of(2m))).ToArray()).Should().BeTrue();
        }

        // uniqueItems.

        [Fact]
        public void AUniqueArrayIsNoLongerThanTheValuesItsItemsCanBe()
        {
            JsonFact[] facts = { new JsonFact.UniqueItems("$.tags"), new JsonFact.OneOf("$.tags[*]", ValueSet.Of(Of("a"), Of("b"))) };
            Decide(new JsonAtom.LengthIn("$.tags", 3, null), facts).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.tags[2]"), facts).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.tags[1]"), facts).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AUniqueArrayOfBooleansHasAtMostTwo()
        {
            Decide(new JsonAtom.Defined("$.flags[2]"), new JsonFact.UniqueItems("$.flags"), OfType("$.flags[*]", "boolean")).Should().Be(Truth.Never);
        }

        [Fact]
        public void TwoEqualItemsOfAUniqueArrayContradict()
        {
            Contradictory(new JsonFact.UniqueItems("$.t"),
                new JsonFact.Present("$.t[0]"), new JsonFact.OneOf("$.t[0]", ValueSet.Of(Of("a"))),
                new JsonFact.Present("$.t[1]"), new JsonFact.OneOf("$.t[1]", ValueSet.Of(Of("a")))).Should().BeTrue();
        }

        // What cannot be there says what its parent is not.

        [Fact]
        public void ARequiredPropertyThatCannotBeThereMeansNoObject()
        {
            Decide(new JsonAtom.HasType("$.o", "object"), new JsonFact.Requires("$.o", "x"), new JsonFact.Absent("$.o.x")).Should().Be(Truth.Never);
            Decide(new JsonAtom.HasType("$.o", "object"), new JsonFact.Requires("$.o", "x"), new JsonFact.ClosedTo("$.o", ValueSet.Of("y"))).Should().Be(Truth.Never);
        }

        [Fact]
        public void ARequiredItemThatCannotBeThereMeansNoArray()
        {
            Decide(new JsonAtom.HasType("$.t", "array"), new JsonFact.MinItems("$.t", 1), new JsonFact.OfType("$.t[*]", ValueSet.Of<string>())).Should().Be(Truth.Never);
            Decide(new JsonAtom.HasType("$.t", "array"), new JsonFact.MinItems("$.t", 3), new JsonFact.Absent("$.t[2]")).Should().Be(Truth.Never);
        }

        [Fact]
        public void AnArrayOfExactlyThreeHasItsThirdAndNoFourth()
        {
            JsonFact[] facts = { new JsonFact.MinItems("$.t", 3), new JsonFact.MaxItems("$.t", 3), new JsonFact.Present("$.t"), OfType("$.t", "array") };
            Decide(new JsonAtom.Defined("$.t[2]"), facts).Should().Be(Truth.Always);
            Decide(new JsonAtom.Defined("$.t[3]"), facts).Should().Be(Truth.Never);
        }

        // Counting properties.

        [Fact]
        public void AnObjectHasNoMorePropertiesThanAllowed()
        {
            Contradictory(new JsonFact.MaxProperties("$.o", 1), new JsonFact.Present("$.o.a"), new JsonFact.Present("$.o.b")).Should().BeTrue();
            Decide(new JsonAtom.HasType("$.o", "object"),
                new JsonFact.MaxProperties("$.o", 1), new JsonFact.Requires("$.o", "a"), new JsonFact.Requires("$.o", "b")).Should().Be(Truth.Never);
            Decide(new JsonAtom.HasType("$.o", "object"),
                new JsonFact.MinProperties("$.o", 3), new JsonFact.ClosedTo("$.o", ValueSet.Of("a", "b"))).Should().Be(Truth.Never);
        }

        // Two paths holding one value: a union of two path terms, and congruence does the rest.

        [Fact]
        public void PathsHoldingOneValueShareEveryFact()
        {
            JsonFact[] facts = { new JsonFact.Same("$.a", "$.b"), new JsonFact.OneOf("$.a", ValueSet.Of(Of(1m))) };
            Decide(new JsonAtom.EqualTo("$.b", Of(2m)), facts).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.b"), facts.Append(new JsonFact.Present("$.a")).ToArray()).Should().Be(Truth.Always);
            Decide(new JsonAtom.Defined("$.b"), facts.Append(new JsonFact.Absent("$.a")).ToArray()).Should().Be(Truth.Never);
        }

        [Fact]
        public void PathsHoldingOneValueHaveOneValueBeneathThem()
        {
            // Congruence: if $.a and $.b are one value, so are $.a.x and $.b.x, and $.a[2] and $.b[2].
            Decide(new JsonAtom.Defined("$.b.x"), new JsonFact.Same("$.a", "$.b"), new JsonFact.Present("$.a.x")).Should().Be(Truth.Always);
            Decide(new JsonAtom.EqualTo("$.b[2]", Of("z")),
                new JsonFact.Same("$.a", "$.b"), new JsonFact.OneOf("$.a[2]", ValueSet.Of(Of("y")))).Should().Be(Truth.Never);
        }

        [Fact]
        public void PathsHoldingDifferentValuesAreNotOne()
        {
            Contradictory(new JsonFact.Same("$.a", "$.b"), new JsonFact.Present("$.a"),
                new JsonFact.OneOf("$.a", ValueSet.Of(Of(1m))), new JsonFact.OneOf("$.b", ValueSet.Of(Of(2m)))).Should().BeTrue();
        }

        [Fact]
        public void EqualityBetweenPathsIsTransitive()
        {
            using var scope = Contexts.Open(null, new JsonFact[] { new JsonFact.Same("$.a", "$.b"), new JsonFact.Same("$.b", "$.c"), new JsonFact.Present("$.a") },
                new JsonPathSpec[] { "$.a", "$.c" });
            Contexts.Decide(scope, new JsonAtom.SameAs("$.a", "$.c")).Should().Be(Truth.Always);
        }

        [Fact]
        public void ACheckAcrossPathsCarriesTypes()
        {
            // CHECK (data.id = linkId), with linkId declared a string: data.id is no integer.
            Decide(new JsonAtom.HasType("$.data.id", "integer"),
                new JsonFact.Same("$.data.id", "$.linkId"), OfType("$.linkId", "string")).Should().Be(Truth.Never);
        }

        [Fact]
        public void PathsOfDifferentTypesAreNeverEqual()
        {
            Decide(new JsonAtom.SameAs("$.a", "$.b"), OfType("$.a", "string"), OfType("$.b", "number")).Should().Be(Truth.Never);
        }

        [Fact]
        public void NoValueIsOneOfItsOwnProperties()
        {
            Decide(new JsonAtom.Defined("$.a"), new JsonFact.Same("$.a", "$.a.x")).Should().Be(Truth.Never);
        }

        // contains, minContains and maxContains.

        [Fact]
        public void AnArrayHoldingAValueThreeTimesIsAtLeastThreeLong()
        {
            JsonFact[] facts = { new JsonFact.ContainsAtLeast("$.t", Of("a"), 3), new JsonFact.Present("$.t"), OfType("$.t", "array") };
            Decide(new JsonAtom.Defined("$.t[2]"), facts).Should().Be(Truth.Always);
            Decide(new JsonAtom.ArrayHolds("$.t", Of("a")), facts).Should().Be(Truth.Always);
            Decide(new JsonAtom.LengthIn("$.t", null, 2), facts).Should().Be(Truth.Never);
        }

        [Fact]
        public void AnArrayCannotHoldWhatItsItemsCannotBe()
        {
            Decide(new JsonAtom.HasType("$.t", "array"),
                new JsonFact.ContainsAtLeast("$.t", Of("z"), 1), new JsonFact.OneOf("$.t[*]", ValueSet.Of(Of("a"), Of("b")))).Should().Be(Truth.Never);
        }

        [Fact]
        public void AUniqueArrayHoldsAValueAtMostOnce()
        {
            Decide(new JsonAtom.HasType("$.t", "array"),
                new JsonFact.UniqueItems("$.t"), new JsonFact.ContainsAtLeast("$.t", Of("a"), 2)).Should().Be(Truth.Never);
        }

        [Fact]
        public void AnArrayHoldingAValueNoTimesHasNoItemEqualToIt()
        {
            Decide(new JsonAtom.EqualTo("$.t[4]", Of("a")), new JsonFact.ContainsAtMost("$.t", Of("a"), 0)).Should().Be(Truth.Never);
            Decide(new JsonAtom.ArrayHolds("$.t", Of("a")), new JsonFact.ContainsAtMost("$.t", Of("a"), 0)).Should().Be(Truth.Never);
        }

        // propertyNames.

        [Fact]
        public void APropertyWhoseNameIsTooLongIsAbsent()
        {
            Decide(new JsonAtom.Defined("$.o.longname"), new JsonFact.NameMaxLength("$.o", 4)).Should().Be(Truth.Never);
            Decide(new JsonAtom.Defined("$.o.name"), new JsonFact.NameMaxLength("$.o", 4)).Should().Be(Truth.Unknown);
        }

        // The layers: the rules of JSON in the root, the rules of JSON Schema in a context beneath it.

        [Fact]
        public void TheRulesOfJsonHoldInTheRootAlone()
        {
            using var scope = Contexts.Open(Contexts.Json, new JsonFact[] { OfType("$.data", "string") }, new JsonPathSpec[] { "$.data.id" });
            Contexts.Decide(scope, new JsonAtom.Defined("$.data.id")).Should().Be(Truth.Never, "a string has no properties, which is JSON's to say");
        }

        [Fact]
        public void TheRulesOfJsonSchemaHoldOnlyBeneathTheirContext()
        {
            // `required` means something only because JSON Schema says what it means.
            var required = new JsonFact[] { new JsonFact.Requires("$", "kind") };
            using (var json = Contexts.Open(Contexts.Json, required, new JsonPathSpec[] { "$.kind" }))
                Contexts.Decide(json, new JsonAtom.Defined("$.kind")).Should().Be(Truth.Unknown, "the root knows no keyword");
            using (var schema = Contexts.Open(Contexts.JsonSchema, required, new JsonPathSpec[] { "$.kind" }))
                Contexts.Decide(schema, new JsonAtom.Defined("$.kind")).Should().Be(Truth.Always);
        }

        // The layering: a schema's facts in a frozen context, a query's in a child of it.

        [Fact]
        public void AQueryContextReadsThroughItsSchemaContext()
        {
            var schema = Contexts.Open(null, new JsonFact[] { new JsonFact.MaxItems("$.tags", 10), OfType("$.tags[*]", "string") },
                new JsonPathSpec[] { "$.tags[10]", "$.tags[3]" });
            schema.Freeze();

            using (var query = Contexts.Open(schema, new JsonFact[] { new JsonFact.Present("$.tags[10]") }))
                query.IsContradictory.Should().BeTrue("the query's eleventh item against the schema's ten");

            using (var query = Contexts.Open(schema, new JsonFact[] { new JsonFact.Present("$.tags[3]") }))
            {
                query.IsContradictory.Should().BeFalse();
                Contexts.Decide(query, new JsonAtom.HasType("$.tags[3]", "string")).Should().Be(Truth.Always);
                Contexts.Decide(query, new JsonAtom.LengthIn("$.tags", 4, 10)).Should().Be(Truth.Always);
            }

            // The schema context is untouched by its queries.
            schema.IsContradictory.Should().BeFalse();
            Contexts.Decide(schema, new JsonAtom.Defined("$.tags[3]")).Should().Be(Truth.Unknown);
        }

    }

}
