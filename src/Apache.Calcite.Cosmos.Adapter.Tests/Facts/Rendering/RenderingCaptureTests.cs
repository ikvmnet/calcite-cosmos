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
        /// Every shape the loader reads from h07 to h11 is S's since the switch (h15): one reading for both graphs, which
        /// reads places, so that S owns each conjunct holding one, but one holding an opaque term, which S has no reading of
        /// (<c>COALESCE</c>'s expansion casts its comparison to <c>BOOLEAN NOT NULL</c>). Each statement holds one: <c>LIKE</c>, a truth test,
        /// <c>COALESCE</c>, a boolean <c>CASE</c>, a predicate's null test and an element, the corpus's type tests, a call the
        /// function rows name compared (h10) and two readings compared (h11). h09's number cast of a bound text is the
        /// <c>SqlValue.Cast</c> over a text S read before h07, no new term.
        /// </summary>
        [Fact]
        public void EveryNewShapeIsSSinceTheSwitch()
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
                Where + "UPPER(JSON_VALUE(c.\"DOC\", '$.name')) = 'KICKSTAND'",
                Where + "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) < JSON_VALUE(c.\"DOC\", '$.stock' RETURNING INTEGER)",
            ];

            var recorder = new RenderingRecorder();
            using (recorder.Arm())
                foreach (var statement in RenderingInputs.Differential("h07", sql, RenderingInputs.Containers).Concat(RenderingInputs.Corpus().Where(s => s.Name == "Filter.TypeTests")))
                    statement.Plan();

            var read = recorder.Read.SelectMany(s => s.Statement.Conjuncts.Select((ir, i) => (Statement: s, Rex: s.Conjuncts[i], Ir: ir))).ToList();
            read.Where(c => (New(c.Ir) || Placed(c.Ir)) && HoldsOpaque(c.Ir) == false).Should().NotBeEmpty().And.OnlyContain(c => RenderingFamily.Owns(c.Ir), "S owns every new shape");
            foreach (var kind in new[] { typeof(SqlPredicate.TypeTest), typeof(SqlPredicate.Affix), typeof(SqlPredicate.Case) })
                read.Should().Contain(c => c.Ir.Subterms.Any(t => t.GetType() == kind), kind.Name);
            read.Should().Contain(c => c.Ir.Paths.Any(p => p.HasElement), "an element");
            read.Should().Contain(c => ComparesACall(c.Ir), "a call the rows name");
            read.Should().Contain(c => ComparesTwoReadings(c.Ir), "two readings");

            // One reading: the loader's of each conjunct is the statement's, which both graphs read.
            read.Where(c => c.Ir.Subterms.Any(t => t is SqlPredicate.Opaque) == false)
                .Select(c => (c.Ir.Digest, Loaded: new CosmosPredicateLoader.Loading(c.Statement.BindingForCheck.Fields, c.Statement.BindingForCheck.Readings, "c").Read(c.Rex).Digest))
                .Should().OnlyContain(c => c.Digest == c.Loaded, "the statement reads each conjunct as the loader does");
        }

        /// <summary>Whether a conjunct holds a term S has no reading of, the translator's for good.</summary>
        static bool HoldsOpaque(SqlPredicate conjunct) => conjunct.Subterms.Any(t => t is SqlPredicate.Opaque);

        /// <summary>Whether a conjunct holds a shape read from h10 or h11: a typed call, or a comparison of two readings.</summary>
        static bool Placed(SqlPredicate conjunct) => ComparesACall(conjunct) || ComparesTwoReadings(conjunct);

        static bool ComparesACall(SqlPredicate conjunct) => conjunct.Subterms.Any(t => t is SqlPredicate.Compare { Left: SqlValue.Call { Type: not null } });

        static bool ComparesTwoReadings(SqlPredicate conjunct) => conjunct.Subterms.Any(t => t is SqlPredicate.Compare { Left.PathRead: not null, Right.PathRead: not null });

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
            }, new CosmosReasoning(new Cosmos.Facts.JsonFactGenerations(CosmosReasoning.Rows), probe: probe));
        }

    }

}
