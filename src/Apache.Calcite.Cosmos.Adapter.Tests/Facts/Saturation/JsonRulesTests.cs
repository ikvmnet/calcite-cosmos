using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

using JsonFact = Apache.Calcite.Cosmos.Facts.Saturation.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The rules of JSON over the element a traversal yields (<c>Yield</c>), its facts stated by hand on the theory's paths:
    /// it is an instance of every element of its array, so what is stated of <c>$.tags[*]</c> reaches it, and it holds only
    /// the kinds the row of its element type in its traversal's form says the traversal yields (<c>yieldkinds</c>), a type
    /// or a form no row names narrowing nothing. Neither rule makes a path; two traversals of one array are two paths; and a
    /// narrowing to no kind makes the element absent, never a node contradictory.
    /// </summary>
    public sealed class JsonRulesTests
    {

        static readonly JsonContexts Contexts = new(CosmosReasoning.Rows);

        static readonly JsonPathSpec Elements = "$.tags[*]";

        static readonly JsonDocumentPath Tags = P("tags");

        /// <summary>
        /// The element traversal <paramref name="traversal"/> of <c>tags</c> yields, read as <paramref name="type"/> by a
        /// traversal of form <paramref name="form"/>.
        /// </summary>
        static JsonDocumentPath Yielded(string type = "VARCHAR", long traversal = 0, string form = "ARRAY") => Tags.Yielded(type, form, traversal);

        static JsonPathSpec Spec(JsonDocumentPath path) => JsonFactEncoding.Path(path);

        static Term<JPath> PathOf(JsonDocumentPath path) => Spec(path).ToTerm(Contexts.Rules);

        static JsonFact.OneOf Is(JsonPathSpec path, params string[] values) => new(path, ValueSet.From(values.Select(Of)));

        static JsonFact.OfType OfTypes(JsonPathSpec path, params string[] types) => new(path, ValueSet.From(types));

        /// <summary>A context of facts stated by hand, mentioning the paths given, saturated by the rules of JSON.</summary>
        static Scope Open(IEnumerable<JsonDocumentPath> mentions, params JsonFact[] facts) => Contexts.Open(null, facts, mentions.Select(Spec));

        /// <summary>
        /// Whether a conjunct raises on nothing a context of facts stated by hand admits, asked as the reduce's node asks it
        /// before anything is assumed: the context saturated, then a node beneath it holding the conjunct's term, run through
        /// <see cref="PredicateRules.Reduction"/> with no <c>gated</c> row, so it assumes nothing. The node must not be
        /// contradictory.
        /// </summary>
        internal static bool NeverRaises(SqlPredicate conjunct, params JsonFact[] facts)
        {
            var rules = PredicateRules.Of(Contexts);
            var json = rules.Json;
            var writer = new PredicateWriter(rules.Ir, path => JsonFactEncoding.Path(path).ToTerm(json), (junction, truth, arms) =>
                new Fact[] { rules.Arms.Of(junction, truth).Is(ValueSet.From(Enumerable.Range(0, arms.Count).Select(m => (long)m))) }
                    .Concat(arms.Select((arm, m) => (Fact)rules.Arm.Of(junction, truth, (long)m, arm))));

            using var context = Open(conjunct.Paths, facts);
            context.IsContradictory.Should().BeFalse("the facts stated admit a document");
            context.Freeze();

            using var node = context.CreateChild();
            var bookkeeping = new List<Fact>();
            var w = writer.Term(conjunct, bookkeeping);
            node.Make(w);
            node.Assume([.. bookkeeping]);
            node.Run(rules.Reduction, cancellationToken: TestContext.Current.CancellationToken).StopReason.Should().Be(RunStopReason.Completed);
            node.IsContradictory.Should().BeFalse("a node that assumes nothing is contradictory only where its context is");
            return node.Check(rules.NeverRaises.Of(w));
        }

        /// <summary>
        /// What is stated of every element of <c>tags</c> reaches the element a traversal yields, as it reaches an index; and
        /// the rule makes no path, so where nothing is stated of every element there is no <c>Each</c> for it to be an instance
        /// of.
        /// </summary>
        [Fact]
        public void AnElementATraversalYieldsIsAnInstanceOfEveryElementOfItsArray()
        {
            using (var stated = Open([Yielded()], Is(Elements, "1", "2")))
            {
                stated.Check(Contexts.Rules.Instance.Of(PathOf(Yielded()), Elements.ToTerm(Contexts.Rules))).Should().BeTrue();
                stated.Get(Contexts.Rules.Dom.Of(PathOf(Yielded()))).Should().BeEquivalentTo(new[] { Of("1"), Of("2") }, "the domain of every element");
            }

            using var unstated = Open([Yielded()]);
            unstated.CountRows(Contexts.Rules.Each).Should().Be(0, "no rule makes a path");
            unstated.CountRows(Contexts.Rules.Instance).Should().Be(0);
        }

        /// <summary>
        /// A <c>VARCHAR ARRAY</c> element holds a string or a null alone
        /// (<c>Calcite.AVarcharArrayYieldsItsStringsAndNullsAsTheText</c>), whatever else its array's elements may be; an
        /// element of a type or a form no row names (<c>INTEGER ARRAY</c>, a <c>VARCHAR</c> one written <c>WITH
        /// ORDINALITY</c>, a <c>VARCHAR MULTISET</c>) is narrowed by nothing.
        /// </summary>
        [Fact]
        public void AnElementATraversalYieldsHoldsOnlyWhatItsTraversalYields()
        {
            var (integer, ordinal, multiset) = (Yielded("INTEGER", 1), Yielded("VARCHAR", 2, "ARRAY WITH ORDINALITY"), Yielded("VARCHAR", 3, "MULTISET"));
            using (var unstated = Open([Yielded(), integer, ordinal, multiset]))
            {
                unstated.Get(Contexts.Rules.Types.Of(PathOf(Yielded()))).Should().BeEquivalentTo(new[] { StringType, NullType });
                unstated.TryGet(Contexts.Rules.Types.Of(PathOf(integer)), out _).Should().BeFalse("no row names what an INTEGER traversal yields");
                unstated.TryGet(Contexts.Rules.Types.Of(PathOf(ordinal)), out _).Should().BeFalse("no row names what a traversal written WITH ORDINALITY yields");
                unstated.TryGet(Contexts.Rules.Types.Of(PathOf(multiset)), out _).Should().BeFalse("no row names what a traversal of a MULTISET yields");
            }

            using var mixed = Open([Yielded()], OfTypes(Elements, StringType, IntegerType, ObjectType));
            mixed.Get(Contexts.Rules.Types.Of(PathOf(Yielded()))).Should().BeEquivalentTo(new[] { StringType }, "the kinds narrow what every element may be");
        }

        /// <summary>
        /// A cast of the element to <c>INTEGER</c> may raise where nothing is stated, and where every element is stated a string,
        /// on a string it does not parse; and never raises where every element is stated one of <c>"1"</c> and <c>"2"</c> on
        /// <c>$.tags[*]</c>, which reaches the element through the instance rule. Asked of the text and of the field the loader
        /// writes for a column bound to the element.
        /// </summary>
        [Fact]
        public void ACastOfAnElementATraversalYieldsRaisesOnlyWhereItsArrayMayHoldWhatItDoesNotParse()
        {
            foreach (var element in new SqlValue[] { Text(Yielded()), new SqlValue.Text(Yielded()) { Form = PredicateAtom.FieldForm } })
            {
                var cast = Eq(Cast("INTEGER", element), 1L);
                NeverRaises(cast).Should().BeFalse("nothing is stated, and {0} may be \"x\"", element);
                NeverRaises(cast, OfTypes(Elements, StringType)).Should().BeFalse("every element is a string, and {0} may be \"x\"", element);
                NeverRaises(cast, Is(Elements, "1", "2")).Should().BeTrue("every element is \"1\" or \"2\", which {0} parses", element);
            }
        }

        /// <summary>
        /// Two traversals of one array yield two paths: what is stated of one is not of the other, and each is an instance of
        /// every element apart.
        /// </summary>
        [Fact]
        public void TwoTraversalsOfOneArrayAreTwoPaths()
        {
            var (first, second) = (Yielded("VARCHAR", 0), Yielded("VARCHAR", 1));
            using var scope = Open([first, second], Is(Spec(first), "1"), OfTypes(Elements, StringType, NullType));

            scope.AreEqual(scope.Make((ConstructorTerm<JPath>)PathOf(first)), PathOf(second)).Should().BeFalse();
            scope.Get(Contexts.Rules.Dom.Of(PathOf(first))).Should().BeEquivalentTo(new[] { Of("1") });
            scope.TryGet(Contexts.Rules.Dom.Of(PathOf(second)), out _).Should().BeFalse("the second traversal's element is not the first's");
            foreach (var element in new[] { first, second })
                scope.Check(Contexts.Rules.Instance.Of(PathOf(element), Elements.ToTerm(Contexts.Rules))).Should().BeTrue();
        }

        /// <summary>
        /// Where every element of <c>tags</c> is stated an integer, a <c>VARCHAR</c> element holds no kind and is absent: no
        /// document the context admits yields one, Calcite's traversal raising first over an array holding an integer. Nothing
        /// makes it present, so the context is not contradictory, and neither is a node holding a cast of it, which raises on
        /// nothing.
        /// </summary>
        [Fact]
        public void ANarrowingToNoKindMakesNoNodeContradictory()
        {
            using (var scope = Open([Yielded()], OfTypes(Elements, IntegerType)))
            {
                scope.IsContradictory.Should().BeFalse();
                scope.Get(Contexts.Rules.Types.Of(PathOf(Yielded()))).Should().BeEmpty();
                scope.Check(Contexts.Rules.Absent.Of(PathOf(Yielded()))).Should().BeTrue();
            }

            NeverRaises(Eq(Cast("INTEGER", Text(Yielded())), 1L), OfTypes(Elements, IntegerType)).Should().BeTrue("no element is yielded to cast");
        }

    }

}
