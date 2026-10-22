using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The weakening checks (spec §3.17, decision D8): each weakening the split rule pushes beside a residual conjunct is
    /// captured where it is written (shim 3) and claimed as implied by the conjunct, imported from the stratum it was
    /// rendered from. A weakening too strong loses rows the recheck above cannot restore.
    /// </summary>
    public sealed class RenderingWeakeningTests
    {

        const string Typed = "SELECT c.\"id\" FROM typed AS c WHERE ";

        /// <summary>
        /// The pull request's run captures weakenings and claims each as implied; one refuted fails
        /// <see cref="RenderingCheckTests.NoRefutationFails"/> as any refutation does. Counted by what each reads.
        /// </summary>
        [Fact]
        public void EveryWeakeningIsClaimedAsImplied()
        {
            var weakenings = RenderingCheckRun.PerPullRequest.Value.Conjuncts.Where(c => c.Site == 3).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine($"{weakenings.Count} weakenings, {weakenings.Select(c => c.Text).Distinct().Count()} texts: "
                + string.Join(", ", weakenings.GroupBy(Read).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));

            weakenings.Where(c => c.Entry is not null).Should().NotBeEmpty().And.OnlyContain(c => c.Entry!.Key.Claim == Verdict.Implies);
            weakenings.Should().Contain(c => Read(c) == "Implied");
        }

        /// <summary>
        /// A text equality's weakening keeps the string and the number it renders as (8d57df0): implied by the conjunct, and
        /// not exact over the same import, since a stored 30.0 is kept by the number and rendered otherwise by Calcite.
        /// </summary>
        [Fact]
        public void ATextEqualitysWeakeningIsImpliedAndNotExact()
        {
            var run = RenderingMutationTests.Checked(None, Typed + "JSON_VALUE(c.\"DOC\", '$.label') = '30'");
            var weakening = run.Conjuncts.Single(c => c.Site == 3);
            (weakening.Text, Read(weakening)).Should().Be(("((c.label = @p0) OR (c.label = @p1))", "Implied"));

            var form = weakening.Entry!.What!.Form;
            run.Recheck(weakening.Entry.What, form, [form]).Asserted.Kind.Should().Be(RenderingVerdictKind.Unproven, "Equal is what a weakening does not claim");
        }

        /// <summary>
        /// A weakening is read from the stratum it was rendered from, which assumes the conjuncts pushed beside it. Beside
        /// <c>kind = 'b'</c> the label is an integer, so the cast's weakening drops its type test, and the bare bound is
        /// implied there; with the label anything, as the root has it, a string the cast parses stands beside it, and the
        /// same claim is unproven.
        /// </summary>
        [Fact]
        public void AWeakeningIsReadFromTheStratumItWasRenderedFrom()
        {
            var run = RenderingMutationTests.Checked(None, "SELECT c.\"id\" FROM kinds AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = 'b' AND CAST(JSON_VALUE(c.\"DOC\", '$.label') AS INTEGER) > 10");
            var bare = run.Conjuncts.Single(c => c.Site == 3 && c.LaterRound);
            (bare.Text, Read(bare)).Should().Be(("(c.label > @p0)", "Implied"));

            var (claim, label) = (bare.Entry!.What!, ProbeRows.Label);
            claim.Imports[label].Values.Should().Be(PathValues.OfTypes(ValueSet.Of(JsonValues.IntegerType)));
            RenderingVerdictKind From(PathValues values) =>
                ProbeRows.Check(claim.Conjuncts.Single(), claim.Form, set: RenderingCheckRun.AssertedSet, path: ProbeRows.LabelPath, values: values).Implies.Kind;

            (From(claim.Imports[label].Values), From(PathValues.Anything)).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven));
        }

        /// <summary>What a weakening's check reads: Implied where it is proven, else its kind, or why it was skipped.</summary>
        internal static string Read(RenderingCheckRun.Checked c) =>
            c.Skip ?? (c.Entry!.Asserted.Kind == RenderingVerdictKind.Proven ? "Implied" : c.Entry.Asserted.Kind.ToString());

    }

}
