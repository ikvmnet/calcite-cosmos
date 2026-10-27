using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The capture changes no plan: every plan digest is the same with a recorder armed as without, each arm in a reasoning
    /// of its own.
    /// </summary>
    public sealed class RenderingCaptureTests
    {

        /// <summary>Over every input; explicit, and run at the gate, where the partition run plans its 1,000 predicates.</summary>
        [Fact(Explicit = true)]
        public void CaptureChangesNoPlan() => Unchanged(reasoning => RenderingInputs.All(CosmosDifferentialTests.PartitionedPredicates, reasoning));

        /// <summary>Over twenty statements spread across the inputs, per pull request.</summary>
        [Fact]
        public void CaptureChangesNoPlanOverTwenty() => Unchanged(reasoning => RenderingInputs.All(CosmosDifferentialTests.DefaultPartitionedPredicates, reasoning).Where((_, i) => i % 59 == 0).Take(20));

        static void Unchanged(Func<CosmosReasoning, IEnumerable<RenderingInputs.Statement>> statements)
        {
            var recorder = new RenderingRecorder();
            PlanDigests.Unchanged(recorder.Arm, statements, _ => recorder.Records.Should().NotBeEmpty("the capture was armed"));
        }

    }

}
