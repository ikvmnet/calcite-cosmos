using System.Linq;

using Eggnog;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static JsonTheory.JsonValues;

namespace JsonTheory.Tests
{

    /// <summary>
    /// What the rules of JSON decide about a query's predicates from a schema's facts, case by case. The soundness
    /// tests say nothing derived is false; these say what is derived.
    /// </summary>
    [TestClass]
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

        [TestMethod]
        public void AStringHasNoProperties()
        {
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.data.id"), OfType("$.data", "string")));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.data.id", Of("x")), OfType("$.data", "string")));
            Assert.IsTrue(Contradictory(OfType("$.data", "string"), new JsonFact.Present("$.data.id")));
        }

        [TestMethod]
        public void TwoTypesThatShareNoValueMeanThePathIsAbsent()
        {
            // Not a contradiction: a document without the path satisfies both. Only presence makes it one.
            Assert.IsFalse(Contradictory(OfType("$.v", "string"), OfType("$.v", "object")));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.v"), OfType("$.v", "string"), OfType("$.v", "object")));
            Assert.IsTrue(Contradictory(OfType("$.v", "string"), OfType("$.v", "object"), new JsonFact.Present("$.v")));
        }

        // A number cannot be a different number.

        [TestMethod]
        public void ANumberIsNotAnotherNumber()
        {
            var one = new JsonFact.OneOf("$.n", ValueSet.Of(Of(1m)));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.n", Of(2m)), one));
            Assert.AreEqual(Truth.Unknown, Decide(new JsonAtom.EqualTo("$.n", Of(1m)), one));
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.EqualTo("$.n", Of(1m)), one, new JsonFact.Present("$.n")));
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.EqualTo("$.n", Of(1.0m)), one, new JsonFact.Present("$.n")), "1.0 is 1");
        }

        // Values that do not exist cannot be null.

        [TestMethod]
        public void AnAbsentValueIsNotNull()
        {
            var absent = new JsonFact.Absent("$.x");
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.x", NullValue), absent));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.HasType("$.x", "null"), absent));
        }

        [TestMethod]
        public void AValueThatIsNotNullIsNotOfTypeNull()
        {
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.HasType("$.x", "null"), new JsonFact.NotEqual("$.x", NullValue)));
        }

        // If an object is not defined, its properties do not exist, and neither do theirs.

        [TestMethod]
        public void AnUndefinedObjectHasNoProperties()
        {
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.a.b.c"), new JsonFact.Absent("$.a")));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.a.b.c", Of("x")), new JsonFact.Absent("$.a")));
        }

        [TestMethod]
        public void APresentPropertyHasAPresentObjectForAParent()
        {
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.HasType("$.a.b", "object"), new JsonFact.Present("$.a.b.c")));
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.Defined("$.a"), new JsonFact.Present("$.a.b.c")));
        }

        // A schema declaring a ten-item array says the eleventh item does not exist, and that proves other
        // predicates false.

        [TestMethod]
        public void TheEleventhOfTenItemsDoesNotExist()
        {
            var ten = new JsonFact.MaxItems("$.tags", 10);
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.tags[10]"), ten));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.tags[10]", Of("x")), ten));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.tags[10].name"), ten));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.LengthIn("$.tags", 11, null), ten));
            Assert.AreEqual(Truth.Unknown, Decide(new JsonAtom.Defined("$.tags[9]"), ten));
        }

        [TestMethod]
        public void AnArrayWithAnEleventhItemIsNotATenItemArray()
        {
            // The query proves the eleventh is there; the schema says no array there holds one; so the path is no
            // array at all, which a present element contradicts.
            Assert.IsTrue(Contradictory(new JsonFact.MaxItems("$.tags", 10), new JsonFact.Present("$.tags[10]")));
        }

        [TestMethod]
        public void AnItemWithinTheLeastAPresentArrayHoldsIsThere()
        {
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.Defined("$.tags[1]"),
                new JsonFact.MinItems("$.tags", 2), new JsonFact.Present("$.tags"), OfType("$.tags", "array")));
        }

        // A fact about every item of an array is a fact about each item.

        [TestMethod]
        public void AFactAboutEveryItemIsAFactAboutEachItem()
        {
            var strings = OfType("$.tags[*]", "string");
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.tags[3]", Of(5m)), strings));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.tags[3].x"), strings));
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.HasType("$.tags[3]", "string"), strings, new JsonFact.Present("$.tags[3]")));
        }

        [TestMethod]
        public void APropertyRequiredOfEveryItemIsOnEachPresentItem()
        {
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.Defined("$.orders[2].id"),
                OfType("$.orders[*]", "object"), new JsonFact.Requires("$.orders[*]", "id"), new JsonFact.Present("$.orders[2]")));
        }

        [TestMethod]
        public void AFactAboutEveryItemOfEveryItemReachesANestedItem()
        {
            var digits = new JsonFact.OneOf("$.m[*][*]", ValueSet.Of(Of(1m), Of(2m)));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.m[1][0]", Of(3m)), digits));
        }

        [TestMethod]
        public void AFactAboutAPropertyOfEveryItemReachesThatPropertyOfEachItem()
        {
            var kinds = new JsonFact.OneOf("$.lines[*].kind", ValueSet.Of(Of("debit"), Of("credit")));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.lines[4].kind", Of("refund")), kinds));
        }

        [TestMethod]
        public void AnArrayHoldsNoValueItsItemsCannotBe()
        {
            var ab = new JsonFact.OneOf("$.tags[*]", ValueSet.Of(Of("a"), Of("b")));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.ArrayHolds("$.tags", Of("c")), ab));
            Assert.AreEqual(Truth.Unknown, Decide(new JsonAtom.ArrayHolds("$.tags", Of("a")), ab));
            Assert.IsTrue(Contradictory(ab, new JsonFact.Holds("$.tags", Of("c"))));
        }

        [TestMethod]
        public void AnArrayWhoseItemsHaveNoTypeHasNoItems()
        {
            // `items: false`.
            var none = new JsonFact.OfType("$.tags[*]", ValueSet.Of<string>());
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.tags[0]"), none));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.ArrayHolds("$.tags", Of("a")), none));
        }

        // Numbers and their bounds.

        [TestMethod]
        public void AComparisonBeyondADeclaredBoundIsNever()
        {
            // The integration plan's gap 4.
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.NumberIn("$.n", Bound.Above(500), null), new JsonFact.Upper("$.n", Bound.AtMost(100))));
            Assert.AreEqual(Truth.Unknown, Decide(new JsonAtom.NumberIn("$.n", Bound.Above(50), null), new JsonFact.Upper("$.n", Bound.AtMost(100))));
        }

        [TestMethod]
        public void AnIntegerBetweenBoundsCloserThanOneIsNotThere()
        {
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.n"),
                OfType("$.n", "integer"), new JsonFact.Lower("$.n", Bound.Above(499)), new JsonFact.Upper("$.n", Bound.Below(500))));
        }

        [TestMethod]
        public void AnEnumOfIntegersOutsideARangeIsNever()
        {
            // The integration plan's gap 5.
            var small = new JsonFact.OneOf("$.n", ValueSet.Of(Of(1m), Of(2m), Of(3m)));
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.NumberIn("$.n", Bound.Above(499), Bound.Below(501)), small));
        }

        // Strings, booleans, objects.

        [TestMethod]
        public void AStringLongerThanTheMostAllowedIsNever()
        {
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.EqualTo("$.code", Of("abc")), new JsonFact.MaxLength("$.code", 2)));
        }

        [TestMethod]
        public void ABooleanThatIsNotTrueIsFalse()
        {
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.EqualTo("$.flag", Of(false)),
                OfType("$.flag", "boolean"), new JsonFact.NotEqual("$.flag", Of(true)), new JsonFact.Present("$.flag")));
        }

        [TestMethod]
        public void AClosedObjectHasNoOtherProperties()
        {
            Assert.AreEqual(Truth.Never, Decide(new JsonAtom.Defined("$.other"), new JsonFact.ClosedTo("$", ValueSet.Of("id", "kind"))));
        }

        [TestMethod]
        public void ARequiredPropertyOfADocumentIsThere()
        {
            // Every Cosmos DB item is a present object, which the root context states.
            Assert.AreEqual(Truth.Always, Decide(new JsonAtom.Defined("$.kind"), new JsonFact.Requires("$", "kind")));
        }

        [TestMethod]
        public void WhatIsNotProvenStaysUnknown()
        {
            Assert.AreEqual(Truth.Unknown, Decide(new JsonAtom.Defined("$.a"), OfType("$.a", "string")));
            Assert.AreEqual(Truth.Unknown, Decide(new JsonAtom.EqualTo("$.a", Of("x")), OfType("$.a", "string")));
        }

        // The layering: a schema's facts in a frozen context, a query's in a child of it.

        [TestMethod]
        public void AQueryContextReadsThroughItsSchemaContext()
        {
            var schema = Contexts.Open(null, new JsonFact[] { new JsonFact.MaxItems("$.tags", 10), OfType("$.tags[*]", "string") },
                new JsonPathSpec[] { "$.tags[10]", "$.tags[3]" });
            schema.Freeze();

            using (var query = Contexts.Open(schema, new JsonFact[] { new JsonFact.Present("$.tags[10]") }))
                Assert.IsTrue(query.IsContradictory, "the query's eleventh item against the schema's ten");

            using (var query = Contexts.Open(schema, new JsonFact[] { new JsonFact.Present("$.tags[3]") }))
            {
                Assert.IsFalse(query.IsContradictory);
                Assert.AreEqual(Truth.Always, Contexts.Decide(query, new JsonAtom.HasType("$.tags[3]", "string")));
                Assert.AreEqual(Truth.Always, Contexts.Decide(query, new JsonAtom.LengthIn("$.tags", 4, 10)));
            }

            // The schema context is untouched by its queries.
            Assert.IsFalse(schema.IsContradictory);
            Assert.AreEqual(Truth.Unknown, Contexts.Decide(schema, new JsonAtom.Defined("$.tags[3]")));
        }

    }

}
