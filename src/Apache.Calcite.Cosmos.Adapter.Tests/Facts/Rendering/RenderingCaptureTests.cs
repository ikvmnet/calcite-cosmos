using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;

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
