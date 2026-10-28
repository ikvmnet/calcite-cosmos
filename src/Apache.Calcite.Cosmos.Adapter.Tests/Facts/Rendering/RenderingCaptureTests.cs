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
    /// The capture and the shadow change no plan: every plan digest is the same with a recorder and the generate shadow's
    /// observer armed as without, each arm in a reasoning of its own, S writing in both as production does since the switch
    /// (spec g15, g16, line 21).
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
            PlanDigests.Unchanged(() => new Both(recorder.Arm(), CosmosRenderingShadow.Arm(shadow)), statements, _ =>
            {
                recorder.Records.Should().NotBeEmpty("the capture was armed");
                TestContext.Current.TestOutputHelper?.WriteLine($"the shadow: {shadow.Counts}");
                shadow.Counts.Opened.Should().BePositive("S was asked at admission");
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
