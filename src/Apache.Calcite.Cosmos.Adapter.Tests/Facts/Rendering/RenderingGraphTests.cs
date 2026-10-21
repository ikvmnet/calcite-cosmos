using System;
using System.Threading;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// S's generations: a spent one retires while its bundles finish on it, one a bundle found out of ids or epochs retires
    /// at once, and a bundle the engine failed declines rather than throwing.
    /// </summary>
    public sealed class RenderingGraphTests
    {

        static readonly SqlPredicate NotBikes = Ne(Text(LabelPath), "bikes");

        /// <summary>
        /// Forced past the gauge, the graph hands out the next bundle on a new generation, defined alike, while the bundle
        /// in flight on the old one answers as before; a bundle that found its graph out of ids or epochs declines, and its
        /// generation retires though the gauge is not past.
        /// </summary>
        [Fact]
        public void AGraphForcedPastTheGaugeDeclinesAndRetires()
        {
            var forced = false;
            var graph = new RenderingGraph(() => Measured, _ => forced);
            using var first = graph.Open(RenderingSet.Emulator);
            var root = first.Write(NotBikes);
            first.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
            first.Claim(root, Guarded);

            forced = true;
            using var second = graph.Open(RenderingSet.Emulator);
            forced = false;
            using var third = graph.Open(RenderingSet.Emulator);
            (graph.Started, ReferenceEquals(second.Theory, first.Theory), ReferenceEquals(third.Theory, second.Theory)).Should().Be((2, false, true));
            second.Theory.Digest.Should().Be(first.Theory.Digest, "generations defined alike share memo entries");
            RenderingTheory.Define(Measured with { Tolerated = Truths.None }).Digest.Should().NotBe(first.Theory.Digest, "a planted row shares none");

            first.Run().StopReason.Should().Be(RunStopReason.Completed);
            first.Read(root, Guarded, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Proven, "a retired generation serves the bundles open on it");

            // Out of ids or epochs is the graph's: no test can spend 2^31 ids, so the bundle is told as its catch would tell it.
            second.Decline(new OverflowException("planted"));
            second.Read(second.Write(NotBikes), Guarded, Verdict.Equal).Should().Match<RenderingVerdict>(v =>
                v.Kind == RenderingVerdictKind.Unproven && v.Reason == "declined: OverflowException: planted");
            using var fourth = graph.Open(RenderingSet.Emulator);
            (graph.Started, ReferenceEquals(fourth.Theory, third.Theory), third.Failure).Should().Be((3, false, (Exception?)null));
        }

        /// <summary>
        /// A bundle whose run the engine failed, here on a leaf that answers one key two ways, declines: every claim reads
        /// Unproven and names the failure. The fault is the bundle's own, so its generation serves the next.
        /// </summary>
        [Fact]
        public void AFaultedBundleMakesItsConjunctsUnproven()
        {
            var calls = 0;
            var graph = new RenderingGraph(() => Measured with
            {
                Leaf = (f, z, t, q) =>
                {
                    var v = Measured.Leaf(f, z, t, q);
                    return v with { Rows = v.Rows.Add("call " + Interlocked.Increment(ref calls)) };
                },
            });

            using var bundle = graph.Open(RenderingSet.Emulator);
            var unequal = bundle.Write(NotBikes);
            var equal = bundle.Write(Eq(Text(LabelPath), "bikes"));
            bundle.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
            bundle.Claim(unequal, Bare);
            bundle.Claim(equal, Bare);
            bundle.Run().Should().Be(new RenderingRun(null, true, false));
            bundle.Failure.Should().BeOfType<RuleEvaluationException>();

            foreach (var root in new[] { unequal, equal })
                foreach (var claim in new[] { Verdict.Equal, Verdict.Implies })
                    bundle.Read(root, Bare, claim).Should().Match<RenderingVerdict>(v => v.Kind == RenderingVerdictKind.Unproven && v.Reason!.StartsWith("declined: RuleEvaluationException"));

            using var next = graph.Open(RenderingSet.Emulator);
            var alone = next.Write(NotBikes);
            next.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
            next.Claim(alone, Bare);
            next.Run().StopReason.Should().Be(RunStopReason.Completed);
            (graph.Started, ReferenceEquals(next.Theory, bundle.Theory), next.Read(alone, Bare, Verdict.Equal).Kind).Should().Be((1, true, RenderingVerdictKind.Refuted));
        }

    }

}
