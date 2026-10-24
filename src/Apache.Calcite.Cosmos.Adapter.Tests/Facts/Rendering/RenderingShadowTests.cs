using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The generate shadow over the pull request's run (spec g05; line 6): every statement shadowed under each set, no
    /// conjunct in the engine's class, and the translator's exact pushes S declines reported by reason against O2's cap,
    /// which line 6 asserts from g15.
    /// </summary>
    public sealed class RenderingShadowTests
    {

        /// <summary>O2's residue: 25 a pull request, 53 at the gate's 1,000 predicates.</summary>
        static int Cap => CosmosDifferentialTests.PartitionedPredicates > 200 ? 53 : 25;

        /// <summary>
        /// The translator's exact pushes are S's, but for the regressions each with its reason, reported against the cap; the
        /// reports are written. No bundle stopped short, conflicted or declined, none lifted a sentinel (line 20, in the
        /// shape production opens bundles), and the shadow failed on no statement.
        /// </summary>
        [Fact]
        public void TheTranslatorsExactPushesAreS()
        {
            var run = RenderingCheckRun.PerPullRequest.Value;
            var shadow = run.Shadow!;
            foreach (var line in RenderingReports.WriteShadow(shadow, Cap).Where(l => l.StartsWith("    ", StringComparison.Ordinal) == false))
                TestContext.Current.TestOutputHelper?.WriteLine(line);

            run.Failures.Where(f => f.Failure.StartsWith("shadows", StringComparison.Ordinal)).Should().BeEmpty();
            shadow.Classes.Should().NotBeEmpty();
            shadow.Classes.Where(c => c.Class == RenderingGenerateRun.Engine).Select(c => $"{c.Set} {c.Input} {c.Name}/{c.Statement}/{c.Conjunct}").Should().BeEmpty();
            shadow.Bundles.Where(b => b.Failure is not null || b.Run != new RenderingRun(Eggnog.RunStopReason.Completed, true, false))
                .Select(b => $"bundle {b.Id} {b.Set}: {b.Run} {b.Failure?.Message}").Should().BeEmpty();
            shadow.Bundles.SelectMany(b => b.Forms.Select(f => f.Form).Concat(b.Answers.Values.SelectMany(a => new[] { a.Best, a.Weakening }).OfType<ServiceForm>()))
                .Where(RenderingCheckRun.Opaque).Select(f => f.Text).Distinct().Should().BeEmpty("no sentinel is lifted");
        }

        /// <summary>
        /// The parameterised residual the inputs gain (spec g05): its parameter atom is the translator's, not S's, and its
        /// weakening, under each set and in each statement that holds it, is the presence its strict reading gives alone,
        /// since no template spells a parameter.
        /// </summary>
        [Fact]
        public void AParameterAtomsWeakeningIsPresenceAlone()
        {
            var shadow = RenderingCheckRun.PerPullRequest.Value.Shadow!;
            var label = ServiceForm.TestOf(CosmosServiceTest.IsDefined, ProbeRows.Label);
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var rows = shadow.Weakenings.Where(w => w.Set == set && w.Name == RenderingInputs.ParameterisedResidual && w.Owned == false).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: " + string.Join("; ", rows.Select(w => $"{w.Ir}: J {w.Text}, S {w.Mine?.Text}")));
                rows.Should().NotBeEmpty(set.ToString()).And.OnlyContain(w => label.Equals(w.Mine), "J's string branch is O7's listed loss, and S keeps presence");
            }
        }

    }

}
