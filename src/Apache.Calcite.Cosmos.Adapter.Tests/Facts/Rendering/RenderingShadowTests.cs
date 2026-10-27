using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The generate shadow over the pull request's run, told by production's sites (spec g15; lines 6 and 13b): every
    /// statement shadowed under each set, no conjunct in the engine's class, the translator's exact pushes S declines no
    /// more than O2's cap and each for a listed reason, and no weakening looser than J's but O7's listed losses.
    /// </summary>
    public sealed class RenderingShadowTests
    {

        /// <summary>O2's residue: 25 a pull request, 53 at the gate's 1,000 predicates.</summary>
        static int Cap => CosmosDifferentialTests.PartitionedPredicates > 200 ? 53 : 25;

        /// <summary>O2's reasons a regression may have: R4's band filter, a stratum read at the root, the composite, or the conjunct on its merits.</summary>
        static readonly string[] Listed = [RenderingGenerateRun.BandFilter, RenderingGenerateRun.Shallower, RenderingGenerateRun.Composite, RenderingGenerateRun.OnItsMerits];

        /// <summary>
        /// Line 6: the translator's exact pushes are S's, but for the regressions, under each set the legs plan under no more
        /// than O2's cap, each for a reason O2 lists; the reports are written. No bundle stopped short, conflicted or declined,
        /// none lifted a sentinel (line 20, in the bundles production opens), and the shadow failed on no statement.
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
            shadow.Declines.Should().BeEmpty("no question declines in CI");
            shadow.Bundles.Where(b => b.Failure is not null || b.Run != new RenderingRun(Eggnog.RunStopReason.Completed, true, false))
                .Select(b => $"bundle {b.Id} {b.Set}: {b.Run} {b.Failure?.Message}").Should().BeEmpty();
            shadow.Bundles.SelectMany(b => b.Forms.Select(f => f.Form).Concat(b.Answers.Values.SelectMany(a => new[] { a.Best, a.Weakening }).OfType<ServiceForm>()))
                .Where(RenderingCheckRun.Opaque).Select(f => f.Text).Distinct().Should().BeEmpty("no sentinel is lifted");

            foreach (var set in RenderingGenerateRun.AssertedSets)
            {
                var regressions = shadow.Classes.Where(c => c.Set == set && c.Class == RenderingGenerateRun.Regression).ToList();
                regressions.Count.Should().BeLessThanOrEqualTo(Cap, $"O2's cap under {set}");
                regressions.Where(r => Listed.Contains(r.Reason) == false).Select(r => $"{r.Input} {r.Name}/{r.Statement}/{r.Conjunct}: {r.Reason}").Should().BeEmpty($"every regression under {set} has a reason O2 lists");
            }
        }

        /// <summary>
        /// Line 13b: no residual whose J weakening the shadow saw is sent looser than J's, or with none — S's that site W
        /// refused to push counted as none — under each set the legs plan under, but O7's listed losses: J's holding a member
        /// the Rex builder cannot write, or J's unproven to S. The input whose typed reading is compared through a cast,
        /// which S reads no presence of, meets the second.
        /// </summary>
        [Fact]
        public void NoWeakeningIsLooserThanJsButTheListedLosses()
        {
            var shadow = RenderingCheckRun.PerPullRequest.Value.Shadow!;
            foreach (var set in RenderingGenerateRun.AssertedSets)
            {
                var classes = shadow.Weakenings.Where(w => w.Set == set).Select(w => (Row: w, Class: RenderingGenerateRun.ClassOf(w))).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: " + string.Join(", ", classes.GroupBy(c => c.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
                classes.Where(c => c.Class is "dropped" or "translator-only" or "looser" or "parting").Select(c => $"{c.Row.Input} {c.Row.Name}/{c.Row.Statement}/{c.Row.Conjunct}: {c.Class}, J {c.Row.Text}, S {c.Row.Mine?.Text}")
                    .Should().BeEmpty($"under {set}, only O7's listed losses are looser");

                var cast = classes.Where(c => c.Row.Name == RenderingInputs.CastOfTypedResidual && c.Row.Residual && c.Row.Owned == false).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}, {RenderingInputs.CastOfTypedResidual}: " + string.Join("; ", cast.Select(c => $"{c.Row.Ir}: {c.Class}, J {c.Row.Text}, S {c.Row.Mine?.Text}")));
                cast.Should().NotBeEmpty($"the cast's input is shadowed under {set}");
            }
        }

        /// <summary>
        /// The bundles the shadow judges are production's: every question a site asked was answered under a key whose bundle the
        /// oracle's hook let the shadow read, each round's row names it, and asked again after planning every question was a
        /// hit — one bundle code path, the oracle's.
        /// </summary>
        [Fact]
        public void TheShadowJudgesTheBundlesProductionOpened()
        {
            var shadow = RenderingCheckRun.PerPullRequest.Value.Shadow!;
            var keys = shadow.Bundles.Select(b => b.Key).ToHashSet();
            shadow.Asked.Should().NotBeEmpty();
            shadow.Admissions.Where(a => a.Bundle == 0).Select(a => $"{a.Set} {a.Input} {a.Name}/{a.Statement}/{a.Conjunct}").Should().BeEmpty("every round's bundle was read");
            shadow.Bundles.Should().OnlyContain(b => b.Key != null, "each bundle is filed under the oracle's key");
            keys.Count.Should().Be(shadow.Bundles.Count, "a key's bundle is opened once");
            var (hits, bundles, declines) = shadow.Oracle.Counts;
            TestContext.Current.TestOutputHelper?.WriteLine($"{shadow.Asked.Count} questions; the oracle: {hits} hits, {bundles} bundles, {declines} declines");
            (bundles, declines).Should().Be((shadow.Bundles.Count, 0L), "the oracle opened each bundle the shadow read, and no other");
        }

        /// <summary>
        /// Spec §8's guard: S is asked only of a <c>WHERE</c>'s conjuncts and the junctions of them, through filter admission
        /// (site A) and the split — the split rule's (W) and the residual projection rule's (R) — and never of a projected
        /// expression. Planned: the <c>CASE</c> whose branch reads a refusing <c>BIGINT</c>, which the projection split pushes
        /// as a column (<c>TODO.md</c>), alone and beside a filter; and projections over a filter wholly residual, the
        /// residual projection rule's statements, beside one that renders.
        /// </summary>
        [Fact]
        public void NoProjectionReachesS()
        {
            const string Case = "CASE WHEN LOWER(JSON_VALUE(c.\"DOC\", '$.e')) = 'x' THEN JSON_VALUE(c.\"DOC\", '$.w' RETURNING BIGINT) END AS \"w\"";
            string[] sql =
            [
                $"SELECT c.\"id\", {Case} FROM strings AS c",
                $"SELECT c.\"id\", {Case} FROM strings AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') <> 'a'",
                "SELECT JSON_VALUE(c.\"DOC\", '$.a') AS \"a\" FROM strings AS c WHERE SOUNDEX(JSON_VALUE(c.\"DOC\", '$.s')) = 'A120'",
                "SELECT JSON_VALUE(c.\"DOC\", '$.a') AS \"a\" FROM strings AS c WHERE SOUNDEX(JSON_VALUE(c.\"DOC\", '$.s')) = 'A120' AND JSON_VALUE(c.\"DOC\", '$.b') <> 'x'",
            ];
            var run = RenderingCheckRun.Over(RenderingInputs.Differential("projection", sql, [.. CosmosDifferentialTests.Containers]), shadow: new RenderingGenerateRun(sets: [RenderingSet.Production]), writes: false);
            run.Failures.Should().BeEmpty();
            var shadow = run.Shadow!;
            TestContext.Current.TestOutputHelper?.WriteLine($"{shadow.Asked.Count} questions; placements: " + string.Join("; ", shadow.Placements.Select(p => $"{p.Name}/{p.Statement} translator {p.Translator}, S {p.Mine}")));
            shadow.Asked.Should().NotBeEmpty();
            foreach (var (statement, question) in shadow.Asked)
            {
                var conjuncts = statement.Statement.Conjuncts;
                question.Site.Should().BeOneOf(Adapter.Metadata.CosmosFilterRendering.Admission, Adapter.Metadata.CosmosFilterRendering.Weakening);
                question.Roots.Should().OnlyContain(r => conjuncts.Contains(r) || r is Cosmos.Facts.SqlPredicate.And && ((Cosmos.Facts.SqlPredicate.And)r).Arms.All(conjuncts.Contains),
                    "S is asked of a WHERE's conjuncts and their junctions alone");
                question.Roots.SelectMany(r => r.Paths).Select(Cosmos.Facts.SqlValue.Spell).Should().NotContain(["$[\"e\"]", "$[\"w\"]"], "nothing the CASE projects reaches S");
            }

            shadow.Placements.Should().NotBeEmpty("the residual projection rule asked the split (site R)");
            shadow.Classes.Count(c => c.Class == RenderingGenerateRun.Placement).Should().Be(shadow.Placements.Count(p => p.Translator != p.Mine),
                "each filter the rule would fire over by one split and not the other is a projection placement row, which the switch's listing reads");
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
