using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using org.apache.calcite.rel;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The capture changes no plan: every plan digest is the same with a recorder armed as without, each arm in generations
    /// of its own.
    /// </summary>
    public sealed class RenderingCaptureTests
    {

        /// <summary>Over every input; explicit, and run at the gate, where the partition run plans its 1,000 predicates.</summary>
        [Fact(Explicit = true)]
        public void CaptureChangesNoPlan() => Compare(generations => RenderingInputs.All(CosmosDifferentialTests.PartitionedPredicates, generations));

        /// <summary>Over twenty statements spread across the inputs, per pull request.</summary>
        [Fact]
        public void CaptureChangesNoPlanOverTwenty() => Compare(generations => RenderingInputs.All(CosmosDifferentialTests.DefaultPartitionedPredicates, generations).Where((_, i) => i % 59 == 0).Take(20));

        static void Compare(Func<JsonFactGenerations, IEnumerable<(string Input, string Name, string Sql, Func<RelNode> Plan)>> inputs)
        {
            var off = PlanDigests.Lines(inputs(new JsonFactGenerations())).ToList();
            var recorder = new RenderingRecorder();
            List<string> on;
            using (recorder.Arm())
                on = PlanDigests.Lines(inputs(new JsonFactGenerations())).ToList();

            recorder.Records.Should().NotBeEmpty("the capture was armed");
            on.Should().Equal(off);
        }

    }

}
