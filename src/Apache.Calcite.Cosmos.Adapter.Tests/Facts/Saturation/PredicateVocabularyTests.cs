using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The predicate vocabulary in a graph that holds nothing of the theory's, as the rendering graph will: it writes and
    /// analyses a conjunct with no rule of JSON beside it.
    /// </summary>
    public sealed class PredicateVocabularyTests
    {

        [Fact]
        public void AGraphOfTheVocabularyAloneReadsAConjunctIntoAtoms()
        {
            var graph = new EGraph();
            var paths = new PathVocabulary(graph);
            graph.Values.RegisterEnum<CosmosValueClass>();
            graph.Values.RegisterEnum<CosmosServiceTest>();
            graph.Values.RegisterEnum<Truths>();
            graph.Values.Register<ReadingTable>("reading table");
            graph.Values.Register<LikeUnit>("like unit");
            var readings = new ReadingVocabulary(graph, graph.DefineFunction<string, ValueSet<string>>("testkinds", ValueLattice.Union<string>(), options: new TableOptions { Role = TableRole.Analysis }),
                graph.DefineRelation<string, string>("testreads", new TableOptions { Role = TableRole.Analysis }));
            var ir = new PredicateVocabulary(graph, paths, readings);
            var junctions = new List<(bool Truth, int Arms)>();
            var writer = new PredicateWriter(ir, path => JsonFactEncoding.Path(path).ToTerm(paths), (_, truth, arms) =>
            {
                junctions.Add((truth, arms.Count));
                return Array.Empty<Fact>();
            });

            var facts = new List<Fact>();
            var scope = graph.Root;
            scope.Make(writer.Term(And(Ne(Text(P("label")), "bikes"), Or(IsNull(Raw(P("a"))), In(Text(P("b")), "x", "y")), Eq(Typed(P("n"), "BIGINT"), 3L)), facts));
            scope.Assume(facts.ToArray());

            scope.Run(Schedule.Saturate(ir.Analyse), cancellationToken: TestContext.Current.CancellationToken).StopReason.Should().Be(RunStopReason.Completed);
            ir.Analyse.Rules.Should().HaveCount(42, "nine of them a JSON_QUERY's, the one-place tests' and the affix's, thirteen G8's, a column's, a call's test, a cast number's, SQL's null's and the boxes', ten h15b's, an element's and a form's, and two an element's raw value read as a path's, which this conjunct holds none of");
            scope.CountRows(ir.Atom).Should().Be(4);
            scope.CountRows(ir.Point).Should().Be(4, "'bikes', 'x', 'y' and 3, beside their atoms");
            junctions.Should().Equal(new[] { (true, 2), (false, 2), (false, 3) }, "the disjunction is written as an arm, before the conjunction it nests in");
        }

        /// <summary>
        /// A cast is a reading where a <c>Calcite</c> row measured it, keyed by the reading it converts, its column apart
        /// (<c>measuredcast</c>, h15b): the text's parse as an integer and a raw value's cast to text in a column are atoms; a
        /// raw value's numeric cast is none, in a column or not, since over an <c>ANY</c> Calcite wraps a long beyond an int
        /// (<c>CalciteVariantMeasurementTests.ACastOfARawNumberIsEachColumnsOwn</c>: 4294967297 is 1), which no window of the
        /// stored number holds.
        /// </summary>
        [Fact]
        public void ACastIsAReadingWhereARowMeasuredIt()
        {
            AtomsOf(Eq(Cast("INTEGER", Text(P("a"))), 1L)).Should().Be(1);
            AtomsOf(Eq(Cast("VARCHAR", new SqlValue.Raw(P("a")) { Column = "ANY" }), "x")).Should().Be(1);
            AtomsOf(Eq(Cast("BOOLEAN", Text(P("a"))), true)).Should().Be(1);
            AtomsOf(Eq(Cast("INTEGER", Raw(P("a"))), 1L)).Should().Be(0, "no row measured a raw value's numeric cast");
            AtomsOf(Eq(Cast("INTEGER", new SqlValue.Raw(P("a")) { Column = "ANY" }), 1L)).Should().Be(0, "over an ANY 4294967297 is 1");
            AtomsOf(Eq(Cast("SMALLINT", Text(P("a"))), 1L)).Should().Be(0, "no row measured the text's parse as a SMALLINT");
        }

        /// <summary>
        /// A reading written apart reads what its reading reads, and a test of it as its form's row says the body reads it
        /// (h15b): a field read as text or as a scalar type, an item chain and a cast to <c>ANY</c> raise, the accessor
        /// re-typed <c>ANY</c> reads the raw value. A raw value of a path holding an element reads the raw value, a test of it
        /// read as its body reads it, as of a path holding it (<c>asofapath</c>), and its text likewise; one in a column, a field
        /// of one and one written apart read the element reading, which no row measured.
        /// </summary>
        [Fact]
        public void AReadingWrittenApartIsItsReadingButToItsTest()
        {
            var field = new SqlValue.Text(P("a")) { Form = PredicateAtom.FieldForm };
            AtomsIn(Eq(field, "x")).Should().Equal(AtomsIn(Eq(Text(P("a")), "x")));
            foreach (var apart in new SqlValue[] { field, new SqlValue.Typed(P("a"), "BIGINT") { Form = PredicateAtom.FieldForm }, new SqlValue.Raw(P("a")) { Form = PredicateAtom.ItemForm },
                new SqlValue.Raw(P("a")) { Form = PredicateAtom.AnyCastForm } })
            {
                AtomsIn(new SqlPredicate.TypeTest("IS_STRING", apart)).Should().ContainSingle().Which.Body.Should().Be(PredicateAtom.RaisesReader, apart.ToString());
                AtomsIn(new SqlPredicate.IsDefined(apart)).Should().ContainSingle().Which.Body.Should().Be(PredicateAtom.RaisesReader, apart.ToString());
            }
            AtomsIn(new SqlPredicate.TypeTest("IS_STRING", Raw(P("a")))).Should().ContainSingle().Which.Body.Should().Be(PredicateAtom.RawReader, "the accessor re-typed ANY is taken apart");

            var element = P("tags").Element(0);
            foreach (var read in new SqlValue[] { new SqlValue.Raw(element) { Form = PredicateAtom.ItemForm }, new SqlValue.Raw(element.Property("n")) { Column = "VARIANT" },
                new SqlValue.Text(element) { Form = PredicateAtom.FieldForm }, new SqlValue.Raw(element) { Form = PredicateAtom.AnyCastForm } })
                AtomsIn(Eq(read, "x")).Should().ContainSingle().Which.Reader.Should().Be(PredicateAtom.ElementReader, read.ToString());
            AtomsIn(Eq(Text(element), "x")).Should().ContainSingle().Which.Reader.Should().Be(PredicateAtom.TextReader, "the text of an element is as of a path holding it");
            AtomsIn(Eq(Raw(element), "x")).Should().ContainSingle().Which.Reader.Should().Be(PredicateAtom.RawReader, "an element's raw value is as of a path holding it");
            AtomsIn(new SqlPredicate.TypeTest("IS_STRING", Raw(element))).Should().ContainSingle().Which.Should().Match<PredicateAtom>(a => a.Reader == PredicateAtom.RawReader && a.Body == PredicateAtom.RawReader,
                "a test's body over an element's raw value reads it as of a path holding it");
            AtomsIn(Eq(Raw(element.Property("n")), "x")).Should().ContainSingle().Which.Reader.Should().Be(PredicateAtom.RawReader, "a property of an element is a path holding one");
        }

        /// <summary>The atoms the vocabulary reads a conjunct into, beside the registry's rows of readings.</summary>
        static long AtomsOf(SqlPredicate conjunct) => AtomsIn(conjunct).Count;

        /// <summary>The atoms themselves.</summary>
        static IReadOnlyList<PredicateAtom> AtomsIn(SqlPredicate conjunct)
        {
            var graph = new EGraph();
            var paths = new PathVocabulary(graph);
            graph.Values.RegisterEnum<CosmosValueClass>();
            graph.Values.RegisterEnum<CosmosServiceTest>();
            graph.Values.RegisterEnum<Truths>();
            graph.Values.Register<ReadingTable>("reading table");
            graph.Values.Register<LikeUnit>("like unit");
            var readings = new ReadingVocabulary(graph, graph.DefineFunction<string, ValueSet<string>>("testkinds", ValueLattice.Union<string>(), options: new TableOptions { Role = TableRole.Analysis }),
                graph.DefineRelation<string, string>("testreads", new TableOptions { Role = TableRole.Analysis }));
            var ir = new PredicateVocabulary(graph, paths, readings);
            var reads = graph.DefineRuleset("reads");
            readings.Define(reads);
            var writer = new PredicateWriter(ir, path => JsonFactEncoding.Path(path).ToTerm(paths), (_, _, _) => Array.Empty<Fact>());

            var facts = new List<Fact>();
            var scope = graph.Root;
            scope.Assume([.. readings.Facts(Adapter.Sql.CosmosAxiomRows.ReadCalcite(Adapter.Sql.CosmosAxioms.All), KleeneTables.Reads, KleeneTables.Compares)]);
            scope.Run(Schedule.Saturate(reads), cancellationToken: TestContext.Current.CancellationToken).StopReason.Should().Be(RunStopReason.Completed);
            scope.Make(writer.Term(conjunct, facts));
            scope.Assume(facts.ToArray());
            scope.Run(Schedule.Saturate(ir.Analyse), cancellationToken: TestContext.Current.CancellationToken).StopReason.Should().Be(RunStopReason.Completed);
            return [.. scope.Rows(ir.Atom).Select(r => (PredicateAtom)r.Keys[2])];
        }

        [Fact]
        public void AHiddenDiscriminatorIsNoPath() =>
            FluentActions.Invoking(() => JsonPathSpec.HiddenNamed("kind").ToTerm(new PathVocabulary(new EGraph()))).Should().Throw<InvalidOperationException>();

    }

}
