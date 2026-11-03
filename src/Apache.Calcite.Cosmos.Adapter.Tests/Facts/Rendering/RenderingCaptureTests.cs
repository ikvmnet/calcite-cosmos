using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The capture and a schema's probe change no plan: every plan digest is the same with a recorder armed, in a reasoning
    /// whose probe asks S under every set beside the statement's own, as in one of its own with neither (spec g15, g17,
    /// line 21).
    /// </summary>
    public sealed class RenderingCaptureTests
    {

        /// <summary>Over every input; explicit, and run at the gate, where the partition run plans its 1,000 predicates.</summary>
        [Fact(Explicit = true)]
        public void CaptureChangesNoPlan() => Unchanged(reasoning => RenderingInputs.All(CosmosDifferentialTests.PartitionedPredicates, reasoning));

        /// <summary>Over twenty statements spread across the inputs, per pull request.</summary>
        [Fact]
        public void CaptureChangesNoPlanOverTwenty() => Unchanged(reasoning => RenderingInputs.All(CosmosDifferentialTests.DefaultPartitionedPredicates, reasoning).Where((_, i) => i % 59 == 0).Take(20));

        /// <summary>
        /// Every shape the facts graph reads from h07 is the translator's until S's switch (h15): S reads each as it did,
        /// opaque, so that it owns none and weakens none otherwise than before, and the plan is <c>main</c>'s (line 21,
        /// spec F3). Each statement holds one: <c>LIKE</c>, a truth test, <c>COALESCE</c>, a boolean <c>CASE</c>, a
        /// predicate's null test and an element, and the corpus's type tests.
        /// </summary>
        [Fact]
        public void EveryNewShapeIsTheTranslatorsUntilTheSwitch()
        {
            const string Where = "SELECT c.\"id\" FROM products AS c WHERE ";
            string[] sql =
            [
                Where + "JSON_VALUE(c.\"DOC\", '$.name') LIKE 'T%' AND JSON_VALUE(c.\"DOC\", '$.name') NOT LIKE '%x'",
                Where + "(JSON_VALUE(c.\"DOC\", '$.category') = 'bikes') IS NOT TRUE",
                Where + "COALESCE(JSON_VALUE(c.\"DOC\", '$.category') <> 'bikes', FALSE)",
                Where + "CASE WHEN JSON_VALUE(c.\"DOC\", '$.category') = 'bikes' THEN JSON_VALUE(c.\"DOC\", '$.name') = 'Kickstand' ELSE JSON_VALUE(c.\"DOC\", '$.name') <> 'Lace' END",
                Where + "(JSON_VALUE(c.\"DOC\", '$.category') <> 'bikes') IS NULL",
                Where + "JSON_VALUE(c.\"DOC\", '$.tags[0]') = 'outdoor' AND JSON_VALUE(c.\"DOC\", '$.category') = 'bikes'",
            ];

            var recorder = new RenderingRecorder();
            using (recorder.Arm())
                foreach (var statement in RenderingInputs.Differential("h07", sql, RenderingInputs.Containers).Concat(RenderingInputs.Corpus().Where(s => s.Name == "Filter.TypeTests")))
                    statement.Plan();

            var read = recorder.Read.SelectMany(s => s.Statement.Conjuncts.Select((f, i) => (Statement: s, F: f, S: s.RenderingStatement.Conjuncts[i]))).ToList();
            read.Where(c => New(c.F)).Should().NotBeEmpty("the facts graph reads the new shapes");
            read.Should().OnlyContain(c => New(c.S) == false, "S reads none of them");
            read.Where(c => New(c.F)).Should().OnlyContain(c => RenderingFamily.Owns(c.S) == false, "so S owns none of them");
            foreach (var kind in new[] { typeof(SqlPredicate.TypeTest), typeof(SqlPredicate.Affix), typeof(SqlPredicate.Case) })
                read.Should().Contain(c => c.F.Subterms.Any(t => t.GetType() == kind), kind.Name);
            read.Should().Contain(c => c.F.Paths.Any(p => p.HasElement), "an element");
        }

        /// <summary>Whether a conjunct holds a shape read from h07: a type test, a case, an affix, a constant or an element.</summary>
        static bool New(SqlPredicate conjunct) => conjunct.Paths.Any(p => p.HasElement)
            || conjunct.Subterms.Any(t => t is SqlPredicate.TypeTest or SqlPredicate.Case or SqlPredicate.Affix or SqlPredicate.Constant);

        static void Unchanged(Func<CosmosReasoning, IEnumerable<RenderingInputs.Statement>> statements)
        {
            var (recorder, probe) = (new RenderingRecorder(), new RenderingProbeCounter());
            PlanDigests.Unchanged(recorder.Arm, statements, _ =>
            {
                recorder.Records.Should().NotBeEmpty("the capture was armed");
                TestContext.Current.TestOutputHelper?.WriteLine($"the probe: {probe.Opened} bundles opened");
                probe.Opened.Should().BePositive("S was asked at admission");
            }, new CosmosReasoning(new Cosmos.Facts.JsonFactGenerations(), probe: probe));
        }

    }

}
