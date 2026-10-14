using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// A schema's conditional statements, held as rows of its context, and the case splits that let a query reason
    /// about a disjunction: one case per value a present discriminator can still hold, and whatever every case proves
    /// lifted to the context that split.
    /// </summary>
    public sealed class SplitTests
    {

        static readonly JsonContexts Contexts = new();

        static JsonFact.OneOf Is(JsonPathSpec path, params string[] values) => new(path, ValueSet.From(values.Select(Of)));

        static JsonGuardedRule When(JsonFact when, params JsonFact[] then) => new(new[] { when }, then);

        /// <summary>
        /// A container of orders, invoices and notes, discriminated by kind. Orders and invoices carry an integer ref;
        /// a note has none.
        /// </summary>
        static Scope Orders(bool discriminated = true)
        {
            var facts = new JsonFact[] { new JsonFact.Present("$.kind"), Is("$.kind", "order", "invoice", "note") }.ToList();
            if (discriminated)
                facts.Add(new JsonFact.Discriminates("$.kind"));

            var schema = Contexts.OpenSchema(facts, new[]
            {
                When(Is("$.kind", "order"), new JsonFact.Present("$.ref"), new JsonFact.OfType("$.ref", Types("integer"))),
                When(Is("$.kind", "invoice"), new JsonFact.Present("$.ref"), new JsonFact.OfType("$.ref", Types("integer"))),
                When(Is("$.kind", "note"), new JsonFact.Absent("$.ref")),
            }, new JsonPathSpec[] { "$.ref" });
            schema.Freeze();
            return schema;
        }

        static Truth Ask(Scope schema, JsonAtom atom, params JsonFact[] facts)
        {
            using var query = Contexts.Open(schema, facts, new[] { atom.Path });
            query.Children.Count.Should().Be(0, "cases are gone once the run is");
            return Contexts.Decide(query, atom);
        }

        [Fact]
        public void OneKindProvesItsBranch()
        {
            var schema = Orders();
            Ask(schema, new JsonAtom.Defined("$.ref"), Is("$.kind", "order")).Should().Be(Truth.Always);
            Ask(schema, new JsonAtom.Defined("$.ref"), Is("$.kind", "note")).Should().Be(Truth.Never);
        }

        [Fact]
        public void TwoKindsNamedTogetherProveWhatBothBranchesSay()
        {
            // The integration plan's gap 1: `kind IN ('order', 'invoice')` proves ref, though neither branch's guard
            // holds alone. A split opens a case per kind, each proves ref, and that is lifted.
            var schema = Orders();
            Ask(schema, new JsonAtom.Defined("$.ref"), Is("$.kind", "order", "invoice")).Should().Be(Truth.Always);
            Ask(schema, new JsonAtom.EqualTo("$.ref", Of("x")), Is("$.kind", "order", "invoice")).Should().Be(Truth.Never);
        }

        [Fact]
        public void TwoKindsOneOfWhichSaysOtherwiseProveNothing()
        {
            // The control: a note carries no ref, so `kind IN ('order', 'note')` proves neither presence nor absence.
            var schema = Orders();
            Ask(schema, new JsonAtom.Defined("$.ref"), Is("$.kind", "order", "note")).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void WithoutADiscriminatorTwoKindsProveNothing()
        {
            var schema = Orders(discriminated: false);
            Ask(schema, new JsonAtom.Defined("$.ref"), Is("$.kind", "order", "invoice")).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void ADomainLessARefusedValueProvesTheBranchLeft()
        {
            // The integration plan's gap 2, with no split at all: a disequality narrows the domain to one value.
            var schema = Orders();
            Ask(schema, new JsonAtom.Defined("$.ref"),
                new JsonFact.NotEqual("$.kind", Of("note")), new JsonFact.NotEqual("$.kind", Of("invoice"))).Should().Be(Truth.Always);
        }

        [Fact]
        public void WhatEveryBranchSaysHoldsOfTheSchemaItself()
        {
            // Split at the schema: no branch leaves ref a string, whichever applies, so none does.
            var schema = Orders();
            Contexts.Decide(schema, new JsonAtom.HasType("$.ref", "string")).Should().Be(Truth.Never);
            Contexts.Decide(schema, new JsonAtom.Defined("$.ref")).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AnUndiscriminatedUnionSplitsOnAHiddenDiscriminator()
        {
            // The integration plan's gap 3: `anyOf` of two status enums. A hidden discriminator stands for which branch
            // applies; a query for a status neither allows has no case left.
            var branch = JsonPathSpec.HiddenNamed("status union");
            var schema = Contexts.OpenSchema(
                new JsonFact[] { new JsonFact.Present(branch), Is(branch, "0", "1"), new JsonFact.Discriminates(branch) },
                new[]
                {
                    When(Is(branch, "0"), Is("$.status", "active", "archived")),
                    When(Is(branch, "1"), Is("$.status", "draft")),
                },
                new JsonPathSpec[] { "$.status" });
            schema.Freeze();

            using (var query = Contexts.Open(schema, new JsonFact[] { new JsonFact.Present("$.status"), Is("$.status", "purged") }, new[] { branch }))
                query.IsContradictory.Should().BeTrue("no branch allows purged");

            using (var query = Contexts.Open(schema, new JsonFact[] { new JsonFact.Present("$.status"), Is("$.status", "draft") }, new[] { branch }))
                query.IsContradictory.Should().BeFalse();

            // And at the schema, what both branches allow is all status can be.
            Contexts.Decide(schema, new JsonAtom.EqualTo("$.status", Of("purged"))).Should().Be(Truth.Never);
        }

        [Fact]
        public void ADiscriminatorInsideABranchSplitsInsideTheCase()
        {
            // kind A has a sub-kind x or y, each of which requires z; kind B requires z itself. Any A or B has a z,
            // which takes a split inside a split.
            var schema = Contexts.OpenSchema(
                new JsonFact[]
                {
                    new JsonFact.Present("$.kind"), Is("$.kind", "A", "B", "C"), new JsonFact.Discriminates("$.kind"),
                    new JsonFact.Discriminates("$.sub"),
                },
                new[]
                {
                    When(Is("$.kind", "A"), new JsonFact.Present("$.sub"), Is("$.sub", "x", "y")),
                    When(Is("$.sub", "x"), new JsonFact.Present("$.z")),
                    When(Is("$.sub", "y"), new JsonFact.Present("$.z")),
                    When(Is("$.kind", "B"), new JsonFact.Present("$.z")),
                },
                new JsonPathSpec[] { "$.z", "$.sub" });
            schema.Freeze();

            Ask(schema, new JsonAtom.Defined("$.z"), Is("$.kind", "A", "B")).Should().Be(Truth.Always);
            Ask(schema, new JsonAtom.Defined("$.z"), Is("$.kind", "A", "C")).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void ABodyOfTwoFactsNeedsBoth()
        {
            var schema = Contexts.OpenSchema(
                new JsonFact[] { new JsonFact.Discriminates("$.kind") },
                new[] { new JsonGuardedRule(new JsonFact[] { Is("$.kind", "A"), new JsonFact.Present("$.meta") }, new JsonFact[] { new JsonFact.OfType("$.meta.v", Types("string")) }) },
                new JsonPathSpec[] { "$.meta.v" });
            schema.Freeze();

            Ask(schema, new JsonAtom.EqualTo("$.meta.v", Of(1m)), Is("$.kind", "A"), new JsonFact.Present("$.meta")).Should().Be(Truth.Never);
            Ask(schema, new JsonAtom.EqualTo("$.meta.v", Of(1m)), Is("$.kind", "A")).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void ABranchThatContradictsTheQueryIsNoCase()
        {
            // kind A says n is at most 10, kind B that n is a string; a query for n > 50 that is a number leaves only
            // kinds other than A and B, so with the domain {A, B} nothing is left.
            var schema = Contexts.OpenSchema(
                new JsonFact[] { new JsonFact.Present("$.kind"), Is("$.kind", "A", "B"), new JsonFact.Discriminates("$.kind") },
                new[]
                {
                    When(Is("$.kind", "A"), new JsonFact.Upper("$.n", Bound.AtMost(10))),
                    When(Is("$.kind", "B"), new JsonFact.OfType("$.n", Types("string"))),
                },
                new JsonPathSpec[] { "$.n" });
            schema.Freeze();

            using var query = Contexts.Open(schema, new JsonFact[]
            {
                new JsonFact.Present("$.n"), new JsonFact.OfType("$.n", Types("number")), new JsonFact.Lower("$.n", Bound.Above(50)),
            }, new JsonPathSpec[] { "$.kind" });
            query.IsContradictory.Should().BeTrue();
        }


        [Fact]
        public void ABranchNoDocumentCanSatisfyLeavesTheDomain()
        {
            // ref is a string in every document; the bad kind says it is a present integer. No document is of the
            // bad kind, which the schema's own split finds when it is compiled: that case is contradictory, and the
            // lift joins only the live cases' domains.
            var schema = Contexts.OpenSchema(
                new JsonFact[]
                {
                    new JsonFact.Present("$.kind"), Is("$.kind", "good", "bad"), new JsonFact.Discriminates("$.kind"),
                    new JsonFact.OfType("$.ref", Types("string")),
                },
                new[] { When(Is("$.kind", "bad"), new JsonFact.Present("$.ref"), new JsonFact.OfType("$.ref", Types("integer"))) },
                new JsonPathSpec[] { "$.ref" });
            schema.Freeze();

            Contexts.Decide(schema, new JsonAtom.EqualTo("$.kind", Of("bad"))).Should().Be(Truth.Never);
            Contexts.Decide(schema, new JsonAtom.EqualTo("$.kind", Of("good"))).Should().Be(Truth.Always);
            Ask(schema, new JsonAtom.Defined("$.ref"), Is("$.kind", "bad")).Should().Be(Truth.Never, "a query for the bad kind keeps nothing");
        }
    }

}
