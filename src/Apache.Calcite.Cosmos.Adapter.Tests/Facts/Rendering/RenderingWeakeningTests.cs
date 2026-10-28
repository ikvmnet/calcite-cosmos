using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The weakening checks (spec §3.17, decision D8): each weakening the split rule pushes beside a residual conjunct is
    /// captured where it is written (shim 3) and claimed as implied by the conjunct, imported from the stratum it was
    /// rendered from; S's since the switch. A weakening too strong loses rows the recheck above cannot restore.
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
        /// A text equality's weakening is the path's presence: implied by the conjunct, and not exact over the same import.
        /// The translator kept the string and the number it renders as (8d57df0), which S does not prove, since a stored
        /// 30.0 is kept by the number and rendered otherwise by Calcite: O7's listed loss 1.
        /// </summary>
        [Fact]
        public void ATextEqualitysWeakeningIsImpliedAndNotExact()
        {
            var run = Checked(Typed + "JSON_VALUE(c.\"DOC\", '$.label') = '30'");
            var weakening = run.Conjuncts.Single(c => c.Site == 3);
            (weakening.Text, Read(weakening)).Should().Be(("IS_DEFINED(c.label)", "Implied"));

            var form = weakening.Entry!.What!.Form;
            run.Recheck(weakening.Entry.What, form, [form]).Asserted.Kind.Should().Be(RenderingVerdictKind.Refuted, "Equal is what a weakening does not claim: presence keeps every other label");
        }

        /// <summary>
        /// A form is read from the stratum it was rendered from, which assumes the conjuncts pushed before it. Beside
        /// <c>kind = 'b'</c> the label is an integer, so the cast is the integer itself, and S sends the bare bound exactly
        /// in a later round, where the translator pushed it as a weakening; with the label anything, as the root has it, a
        /// string the cast parses stands beside it, and the same claim is unproven.
        /// </summary>
        [Fact]
        public void AFormIsReadFromTheStratumItWasRenderedFrom()
        {
            var run = Checked("SELECT c.\"id\" FROM kinds AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = 'b' AND CAST(JSON_VALUE(c.\"DOC\", '$.label') AS INTEGER) > 10");
            var bare = run.Conjuncts.Single(c => c.Site == 2 && c.Text == "(c.label > @p1)");
            (bare.LaterRound, bare.Entry!.Asserted.Kind).Should().Be((true, RenderingVerdictKind.Proven));

            var (claim, label) = (bare.Entry.What!, ProbeRows.Label);
            claim.Stated.Should().Contain(b => b.Path == label, "the label is read beneath the kind's stratum");
            var at = Enumerable.Range(0, claim.Conjuncts.Count).Single(i => claim.Conjuncts[i].Paths.Any(p => SqlValue.Spell(p) == label));
            RenderingVerdictKind From(PathValues values) =>
                ProbeRows.Check(claim.Conjuncts[at], claim.Members[at]!, set: RenderingCheckRun.AssertedSet, path: ProbeRows.LabelPath, values: values).Equal.Kind;

            (From(PathValues.OfTypes(ValueSet.Of(JsonValues.IntegerType))), From(PathValues.Anything)).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven));
        }

        /// <summary>Plans and checks one statement as the check run checks its inputs.</summary>
        static RenderingCheckRun Checked(string sql) => RenderingCheckRun.Over(RenderingInputs.Differential("weakening", [sql], RenderingInputs.Containers));

        /// <summary>What a weakening's check reads: Implied where it is proven, else its kind, or why it was skipped.</summary>
        internal static string Read(RenderingCheckRun.Checked c) =>
            c.Skip ?? (c.Entry!.Asserted.Kind == RenderingVerdictKind.Proven ? "Implied" : c.Entry.Asserted.Kind.ToString());

    }

}
