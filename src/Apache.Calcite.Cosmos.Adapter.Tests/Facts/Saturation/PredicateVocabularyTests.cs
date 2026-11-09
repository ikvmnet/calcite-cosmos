using System;
using System.Collections.Generic;

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
            var ir = new PredicateVocabulary(graph, readings);
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
            ir.Analyse.Rules.Should().HaveCount(30, "nine of them a JSON_QUERY's, the one-place tests' and the affix's, and thirteen G8's, a column's, a call's test, a cast number's, SQL's null's and the boxes', which this conjunct holds none of");
            scope.CountRows(ir.Atom).Should().Be(4);
            scope.CountRows(ir.Point).Should().Be(4, "'bikes', 'x', 'y' and 3, beside their atoms");
            junctions.Should().Equal(new[] { (true, 2), (false, 2), (false, 3) }, "the disjunction is written as an arm, before the conjunction it nests in");
        }

        [Fact]
        public void AHiddenDiscriminatorIsNoPath() =>
            FluentActions.Invoking(() => JsonPathSpec.HiddenNamed("kind").ToTerm(new PathVocabulary(new EGraph()))).Should().Throw<InvalidOperationException>();

    }

}
