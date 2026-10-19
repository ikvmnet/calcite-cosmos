using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// What characters a path's strings hold, as the theory reasons with them: a set of bands per path, a fact like a
    /// type, which every source states or derives in the theory and every combination the theory makes combines.
    /// </summary>
    /// <remarks>
    /// The soundness tests say nothing derived is false of a document, characters among them; these say what is derived:
    /// that a conjunction narrows, a case split widens, a branch holds its own, and a value, a form and a pattern each
    /// reach the one fact.
    /// </remarks>
    public sealed class CharactersTests
    {

        static readonly JsonContexts Contexts = new();

        static readonly ValueSet<string> Surrogate = ValueSet.Of(SurrogateBand);

        static readonly ValueSet<string> High = ValueSet.Of(HighBand);

        static readonly ValueSet<string> NonAscii = ValueSet.Of(NonAsciiBand);

        static readonly ValueSet<string> Trimmed = ValueSet.Of(TrimmedClass);

        const string Emoji = "a😀";

        const string Private = "";

        static JsonFact.OneOf Is(JsonPathSpec path, params string[] values) => new(path, ValueSet.From(values.Select(Of)));

        /// <summary>What a context holds of a path's characters, or <c>null</c> where it holds nothing.</summary>
        static ValueSet<string>? Chars(Scope scope, JsonPathSpec path) =>
            scope.TryGet(Contexts.Rules.Chars.Of(path.ToTerm(Contexts.Rules)), out var bands) ? bands : null;

        static ValueSet<string>? Chars(JsonPathSpec path, params JsonFact[] facts)
        {
            using var scope = Contexts.Open(null, facts, new[] { path });
            return Chars(scope, path);
        }

        [Fact]
        public void AValuesCharactersAreItsOwn()
        {
            Chars("$.s", Is("$.s", "a", "B")).Should().Equal(NoBand);
            Chars("$.s", Is("$.s", "a", Emoji)).Should().Equal(Surrogate);
            Chars("$.s", Is("$.s", Private, Emoji)).Should().Equal(ValueSet.Of(SurrogateBand, HighBand));
            Chars("$.s", Is("$.s", "中")).Should().Equal(NonAscii, "a character outside ASCII below U+D800 is in the third band");
        }

        /// <summary>
        /// What the service's <c>TRIM</c> removes beside a space is a class of its own, cutting across the bands: a tab is
        /// ASCII and in it, U+00A0 and U+3000 are in it and the third band, a space is in neither, and U+FEFF, which no
        /// trim removes, is in the high band alone.
        /// </summary>
        [Fact]
        public void WhatTheServiceTrimsIsAClassOfItsOwn()
        {
            Chars("$.s", Is("$.s", "a b")).Should().Equal(NoBand);
            Chars("$.s", Is("$.s", "a\tb")).Should().Equal(Trimmed);
            Chars("$.s", Is("$.s", "a\u00A0b", "\u3000")).Should().Equal(ValueSet.Of(NonAsciiBand, TrimmedClass));
            Chars("$.s", Is("$.s", "\uFEFFa")).Should().Equal(High);
            Chars("$.s", Is("$.s", "a\u200Bb")).Should().Equal(NonAscii, "a zero-width space is no space the service trims");
        }

        [Fact]
        public void ADomainOfNoStringHoldsNoCharacter()
        {
            Chars("$.n", new JsonFact.OneOf("$.n", ValueSet.Of(Of(1m), Of(2m)))).Should().Equal(NoBand);
            Chars("$.b", new JsonFact.OfType("$.b", Types("boolean"))).Should().Equal(NoBand, "a boolean is true or false, and holds no character");
            Chars("$.s", new JsonFact.OfType("$.s", Types("string"))).Should().BeNull("a string nothing states the characters of may hold any");
        }

        [Fact]
        public void AConjunctionNarrows()
        {
            Chars("$.s", new JsonFact.Characters("$.s", Surrogate), new JsonFact.Characters("$.s", High)).Should().Equal(NoBand);
            Chars("$.s", new JsonFact.Characters("$.s", ValueSet.Of(NonAsciiBand, TrimmedClass)), Is("$.s", "a\tb", "c")).Should().Equal(Trimmed);
            Chars("$.s", new JsonFact.Characters("$.s", High), Is("$.s", "a", Emoji)).Should().Equal(NoBand, "the pattern's and the domain's meet");
        }

        [Fact]
        public void TwoPathsHoldingOneValueHoldOneSetOfCharacters()
        {
            using var scope = Contexts.Open(null, new JsonFact[] { new JsonFact.Same("$.a", "$.b"), new JsonFact.Characters("$.a", High) }, new JsonPathSpec[] { "$.a", "$.b" });
            Chars(scope, "$.b").Should().Equal(High);
        }

        [Fact]
        public void AStoredFormHoldsTheCharactersItIsWrittenIn()
        {
            using var schema = Contexts.OpenSchema(new JsonFact[] { new JsonFact.FormCharacters("uuid", NoBand), new JsonFact.Form("$.id", "uuid") }, null, new JsonPathSpec[] { "$.id" });
            Chars(schema, "$.id").Should().Equal(NoBand);

            using var unstated = Contexts.OpenSchema(new JsonFact[] { new JsonFact.Form("$.id", "uuid") }, null, new JsonPathSpec[] { "$.id" });
            Chars(unstated, "$.id").Should().BeNull("a form whose characters nothing states says nothing of them");
        }

        [Fact]
        public void AnInstanceInheritsCharacters()
        {
            Chars("$.tags[3]", new JsonFact.Characters("$.tags[*]", NoBand)).Should().Equal(NoBand);
        }

        /// <summary>
        /// A container of two kinds of document whose <c>label</c> a pattern declares differently in each: a kind's
        /// branch holds its own, and a split over both holds what either may.
        /// </summary>
        static Scope Kinds()
        {
            var schema = Contexts.OpenSchema(new JsonFact[] { new JsonFact.Present("$.kind"), Is("$.kind", "a", "b", "c"), new JsonFact.Discriminates("$.kind") }, new[]
            {
                new JsonGuardedRule(new[] { Is("$.kind", "a") }, new JsonFact[] { new JsonFact.Characters("$.label", NoBand) }),
                new JsonGuardedRule(new[] { Is("$.kind", "b") }, new JsonFact[] { new JsonFact.Characters("$.label", High) }),
            }, new JsonPathSpec[] { "$.label" });
            schema.Freeze();
            return schema;
        }

        static ValueSet<string>? Under(Scope schema, params JsonFact[] facts)
        {
            using var query = Contexts.Open(schema, facts, new JsonPathSpec[] { "$.label" });
            return Chars(query, "$.label");
        }

        [Fact]
        public void ABranchHoldsItsOwn()
        {
            var schema = Kinds();
            Under(schema, Is("$.kind", "a")).Should().Equal(NoBand);
            Under(schema, Is("$.kind", "b")).Should().Equal(High);
            Under(schema, Is("$.kind", "c")).Should().BeNull("the kind declares nothing of them");
        }

        [Fact]
        public void ASplitWidensToWhatEitherCaseMayHold()
        {
            var schema = Kinds();
            Under(schema, Is("$.kind", "a", "b")).Should().Equal(High, "each kind's, joined");
            Under(schema, Is("$.kind", "a", "c")).Should().BeNull("one kind declares nothing, so either may hold anything");
            Chars(schema, "$.label").Should().BeNull("the declaration over every kind knows nothing of them either");
        }

    }

}
