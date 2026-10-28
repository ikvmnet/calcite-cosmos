using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// What S sent over the pull request's run, read through its schema's probe (spec g17; lines 6 and 20): each input sends
    /// exactly at least as many conjuncts under each set the legs plan under as it did at the switch, the regression line
    /// once no translator stands beside S to compare with; no bundle stopped short, conflicted or declined; the bundles read
    /// are production's; no projection reaches S; and a parameter atom's weakening is presence.
    /// </summary>
    public sealed class RenderingSentTests
    {

        /// <summary>
        /// Line 6's floors (gates map §4.6): the conjuncts each input sent exactly at the switch (g16), under each asserted
        /// set, at the pull request's size and at the gate's, where the partition run plans 1,000 predicates.
        /// </summary>
        static readonly (string Input, RenderingSet Set, int Default, int Gate)[] Floors =
        [
            ("corpus", RenderingSet.Production, 74, 74), ("corpus", RenderingSet.Emulator, 74, 74),
            ("differential", RenderingSet.Production, 388, 388), ("differential", RenderingSet.Emulator, 377, 377),
            ("partitions", RenderingSet.Production, 238, 1131), ("partitions", RenderingSet.Emulator, 225, 1068),
            ("probe", RenderingSet.Production, 7, 7), ("probe", RenderingSet.Emulator, 7, 7),
        ];

        static bool Gate => CosmosDifferentialTests.PartitionedPredicates == CosmosDifferentialTests.GatePartitionedPredicates;

        /// <summary>
        /// Line 6 after the switch: each input sends exactly no fewer conjuncts than at the switch under each asserted set,
        /// the beside set's counts reported; the run planned every statement, and <c>sent.tsv</c> holds each conjunct.
        /// </summary>
        [Fact]
        public void EachInputSendsAtLeastItsFloor()
        {
            var run = RenderingCheckRun.PerPullRequest.Value;
            foreach (var line in RenderingReports.WriteSent(run.Generated!))
                TestContext.Current.TestOutputHelper?.WriteLine(line);

            var sent = run.Generated!.Sends.Where(s => s.Form is not null).GroupBy(s => (s.Input, s.Set)).ToDictionary(g => g.Key, g => g.Count());
            run.Failures.Should().BeEmpty();
            Floors.Where(f => sent.GetValueOrDefault((f.Input, f.Set)) < (Gate ? f.Gate : f.Default))
                .Select(f => $"{f.Input} under {f.Set}: {sent.GetValueOrDefault((f.Input, f.Set))} sent exactly, the floor is {(Gate ? f.Gate : f.Default)}")
                .Should().BeEmpty("no input sends fewer conjuncts exactly than at the switch");
        }

        /// <summary>
        /// Line 20 in the bundles production opens: none stopped short, conflicted or declined, and none lifted a sentinel.
        /// </summary>
        [Fact]
        public void EveryBundleCompletesAndNoneDeclines()
        {
            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            generated.Declines.Should().BeEmpty("no question declines in CI");
            generated.Bundles.Should().NotBeEmpty();
            generated.Bundles.Where(b => b.Failure is not null || b.Run != new RenderingRun(Eggnog.RunStopReason.Completed, true, false))
                .Select(b => $"bundle {b.Id} {b.Set}: {b.Run} {b.Failure?.Message}").Should().BeEmpty();
            generated.Bundles.SelectMany(b => b.Forms.Select(f => f.Form).Concat(b.Answers.Values.SelectMany(a => new[] { a.Best, a.Weakening }).OfType<ServiceForm>()))
                .Where(RenderingCheckRun.Opaque).Select(f => f.Text).Distinct().Should().BeEmpty("no sentinel is lifted");
        }

        /// <summary>
        /// The bundles judged are production's: every question a site asked was answered under a key whose bundle the
        /// oracle's hook let the run read, and the schema's oracle opened each once and no other — one bundle code path.
        /// </summary>
        [Fact]
        public void TheRunJudgesTheBundlesProductionOpened()
        {
            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            var keys = generated.Bundles.Select(b => b.Key).ToHashSet();
            generated.Asked.Should().NotBeEmpty();
            generated.Asked.Where(a => a.Bundle == 0).Select(a => $"{a.Question.Set} {a.Input} {a.Name}/{a.Index}").Should().BeEmpty("every question's bundle was read");
            generated.Bundles.Should().OnlyContain(b => b.Key != null, "each bundle is filed under the oracle's key");
            keys.Count.Should().Be(generated.Bundles.Count, "a key's bundle is opened once");
            var (hits, bundles, declines) = generated.Oracle.Counts;
            TestContext.Current.TestOutputHelper?.WriteLine($"{generated.Asked.Count} questions; the oracle: {hits} hits, {bundles} bundles, {declines} declines");
            (bundles, declines).Should().Be((generated.Bundles.Count, 0L), "the oracle opened each bundle the run read, and no other");
        }

        /// <summary>
        /// Spec §8's guard: S is asked only of a <c>WHERE</c>'s conjuncts and the junctions of them, through filter admission
        /// (site A) and the split's closing bundle (W), and never of a projected expression. Planned: the <c>CASE</c> whose
        /// branch reads a refusing <c>BIGINT</c>, which the projection split pushes as a column (<c>TODO.md</c>), alone and
        /// beside a filter; and projections over a filter wholly residual, the residual projection rule's statements, beside
        /// one that renders.
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
            var generated = new RenderingGenerateRun(sets: [RenderingSet.Production]);
            var run = RenderingCheckRun.Over(RenderingInputs.Differential("projection", sql, generated.Bind(CosmosDifferentialTests.Containers)), generated: generated);
            run.Failures.Should().BeEmpty();
            TestContext.Current.TestOutputHelper?.WriteLine($"{generated.Asked.Count} questions");
            generated.Asked.Should().NotBeEmpty();
            foreach (var asked in generated.Asked)
            {
                var (conjuncts, question) = (asked.Statement.Statement.Conjuncts, asked.Question);
                question.Site.Should().BeOneOf(Adapter.Metadata.CosmosFilterRendering.Admission, Adapter.Metadata.CosmosFilterRendering.Weakening);
                question.Roots.Should().OnlyContain(r => conjuncts.Contains(r) || r is Cosmos.Facts.SqlPredicate.And && ((Cosmos.Facts.SqlPredicate.And)r).Arms.All(conjuncts.Contains),
                    "S is asked of a WHERE's conjuncts and their junctions alone");
                question.Roots.SelectMany(r => r.Paths).Select(Cosmos.Facts.SqlValue.Spell).Should().NotContain(["$[\"e\"]", "$[\"w\"]"], "nothing the CASE projects reaches S");
            }
        }

        /// <summary>
        /// The parameterised residual the inputs gain (spec g05): its parameter atom is the translator's, not S's, and its
        /// weakening, under each set and in each statement that holds it, is the presence its strict reading gives alone,
        /// since no template spells a parameter.
        /// </summary>
        [Fact]
        public void AParameterAtomsWeakeningIsPresenceAlone()
        {
            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            var label = ServiceForm.TestOf(CosmosServiceTest.IsDefined, ProbeRows.Label);
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var rows = generated.Weakenings.Where(w => w.Set == set && w.Name == RenderingInputs.ParameterisedResidual && w.Owned == false).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: " + string.Join("; ", rows.Select(w => $"{w.Ir}: {w.Mine?.Text}")));
                rows.Should().NotBeEmpty(set.ToString()).And.OnlyContain(w => label.Equals(w.Mine), "J's string branch is O7's listed loss, and S keeps presence");
            }
        }

    }

}
