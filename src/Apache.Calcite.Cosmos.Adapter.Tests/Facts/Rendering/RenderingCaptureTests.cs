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
    /// The capture and the shadow change no plan: every plan digest is the same with a recorder and the generate shadow armed
    /// as without, each arm in a reasoning of its own — S asked at all four of production's sites, under every set, beside
    /// the translator (spec g15, line 21).
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
            var (recorder, shadow) = (new RenderingRecorder(), new RenderingShadowCounter());
            PlanDigests.Unchanged(() => new Both(recorder.Arm(), CosmosRenderingShadow.Arm(shadow, writes: false)), statements, _ =>
            {
                recorder.Records.Should().NotBeEmpty("the capture was armed");
                TestContext.Current.TestOutputHelper?.WriteLine($"the shadow: {shadow.Counts}");
                (shadow.Counts.Opened, shadow.Counts.Printed).Should().Match<(int Opened, int Printed)>(c => c.Opened > 0 && c.Printed > 0, "S was asked at admission and printed at implementation");
            });
        }

        sealed class Both(IDisposable first, IDisposable second) : IDisposable
        {

            public void Dispose()
            {
                second.Dispose();
                first.Dispose();
            }

        }

    }

}
