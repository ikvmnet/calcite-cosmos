using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
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
    /// The reader of the text sent (<see cref="CosmosSqlFormReader"/>): every text the check run captured reads back to
    /// itself byte for byte, none in the family reads as opaque, and its precedence is the grammar's, pinned.
    /// </summary>
    public sealed class CosmosSqlFormReaderTests
    {

        internal static readonly CosmosParameter[] Parameters = [new("@p0", "x")];

        /// <summary>
        /// Texts that mix <c>NOT</c>, <c>AND</c> and <c>OR</c> without parentheses, and two that keep them, each with the tree
        /// it must read as: <c>NOT</c> binds tighter than <c>AND</c>, <c>AND</c> than <c>OR</c>, and grouping stays as written;
        /// and <c>NOT</c> before a comparison, which negates the path and so reads as opaque. The service is held to the same
        /// trees (<see cref="Measurements.CosmosSqlPrecedenceMeasurementTests"/>).
        /// </summary>
        internal static readonly (string Text, ServiceForm Tree)[] Pinned =
        [
            ("NOT IS_DEFINED(c.a) AND IS_DEFINED(c.b)", All(Negated(D("a")), D("b"))),
            ("IS_DEFINED(c.a) OR IS_DEFINED(c.b) AND IS_DEFINED(c.c)", Any(D("a"), All(D("b"), D("c")))),
            ("IS_DEFINED(c.a) AND IS_DEFINED(c.b) OR IS_DEFINED(c.c)", Any(All(D("a"), D("b")), D("c"))),
            ("NOT IS_DEFINED(c.a) OR IS_NULL(c.a)", Any(Negated(D("a")), N("a"))),
            ("IS_DEFINED(c.a) AND NOT IS_NULL(c.a) AND (c.a != @p0)", All(D("a"), Negated(N("a")), ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S("a"), JsonValues.Of("x")))),
            ("NOT NOT IS_DEFINED(c.a) AND IS_NULL(c.b)", All(Negated(Negated(D("a"))), N("b"))),
            ("IS_NULL(c.a) OR NOT IS_DEFINED(c.b) AND IS_DEFINED(c.c)", Any(N("a"), All(Negated(D("b")), D("c")))),
            ("NOT IS_DEFINED(c.a) AND NOT IS_DEFINED(c.b) OR IS_NULL(c.c)", Any(All(Negated(D("a")), Negated(D("b"))), N("c"))),
            ("IS_DEFINED(c.a) AND (IS_DEFINED(c.b) OR IS_NULL(c.c))", All(D("a"), Any(D("b"), N("c")))),
            ("NOT (IS_DEFINED(c.a) AND IS_NULL(c.b)) OR IS_DEFINED(c.c)", Any(Negated(All(D("a"), N("b"))), D("c"))),
            ("(IS_DEFINED(c.a) AND IS_NULL(c.b)) AND IS_DEFINED(c.c)", All(All(D("a"), N("b")), D("c"))),
            ("IS_NULL(c.a) OR NOT IS_NULL(c.b) AND NOT IS_DEFINED(c.c) OR IS_DEFINED(c.a)", Any(N("a"), All(Negated(N("b")), Negated(D("c"))), D("a"))),
            ("NOT c.a = @p0", ServiceForm.OpaqueOf("NOT c.a = @p0", ValueSet.Of(S("a")))),
        ];

        /// <summary>
        /// Every text either site captured over every input prints back as itself, which catches a lost token, a misread
        /// literal or path, and a dropped parenthesis; and none whose conjunct's IR is in the family reads as opaque.
        /// </summary>
        [Fact]
        public void EveryCapturedTextReadsBackToItself()
        {
            var captured = RenderingCheckRun.PerPullRequest.Value.Captured.Values.Select(c => (c.Text, c.InFamily, Read: CosmosSqlFormReader.Read(c.Text, c.Parameters))).ToList();

            captured.Should().Contain(c => c.InFamily);
            captured.Where(c => c.Read.Written != c.Text).Select(c => c.Text + " read back as " + c.Read.Written).Should().BeEmpty();
            captured.Where(c => c.InFamily && RenderingCheckRun.Opaque(c.Read.Form)).Select(c => c.Text + " read as " + c.Read.Form).Should().BeEmpty();
        }

        /// <summary>
        /// Each pinned text reads as its tree, its text written back as given; and the plant "a reader that binds <c>NOT</c>
        /// looser than <c>AND</c>" reads some of them otherwise, so it fails this.
        /// </summary>
        [Fact]
        public void PrecedenceIsTheGrammars()
        {
            foreach (var (text, tree) in Pinned)
                CosmosSqlFormReader.Read(text, Parameters).Should().Be(new ReadForm(tree, text), text);

            Pinned.Where(p => CosmosSqlFormReader.Read(p.Text, Parameters, notOverAnd: true).Form.Equals(p.Tree) == false).Select(p => p.Text)
                .Should().Contain(Pinned[0].Text).And.Contain(Pinned[4].Text);
        }

        /// <summary>
        /// Branch-only, the Stage 3 census's (deleted at h16): every shape the translator writes beyond the forms reads whole
        /// and prints back to its text, type tests over a call, the ternary and <c>??</c>, <c>LIKE</c>'s spellings, calls,
        /// arithmetic and <c>[]</c> among them; <c>NOT</c> binds tightest; and what is outside the grammar reads as one opaque
        /// text.
        /// </summary>
        [Theory]
        [InlineData("(IS_NUMBER(c.price) ? c.price : null)", typeof(CosmosSqlFormReader.GroupSyntax))]
        [InlineData("IS_STRING(LOWER(c.name))", typeof(CosmosSqlFormReader.CallSyntax))]
        [InlineData("STARTSWITH(c.name, @p0, true)", typeof(CosmosSqlFormReader.CallSyntax))]
        [InlineData("(c.name LIKE @p0)", typeof(CosmosSqlFormReader.GroupSyntax))]
        [InlineData("c.name NOT LIKE \"a%\"", typeof(CosmosSqlFormReader.LikeSyntax))]
        [InlineData("c.tags[0] ?? c[\"$x\"].y", typeof(CosmosSqlFormReader.CoalesceSyntax))]
        [InlineData("((c.price - 2) % 3) >= -1.5E2", typeof(CosmosSqlFormReader.BinarySyntax))]
        [InlineData("c.a IN (1, \"b\", true) AND NOT IS_DEFINED(c.b) OR (c.k = 1 ? \"t\" : \"e\")", typeof(CosmosSqlFormReader.BinarySyntax))]
        [InlineData("SUBSTRING(c.s, 0, 2)[1] || ARRAY_SLICE([1, 2], 1).x", typeof(CosmosSqlFormReader.BinarySyntax))]
        public void EveryTranslatorShapeReadsAsSyntax(string text, System.Type root)
        {
            var read = CosmosSqlFormReader.ReadSyntax(text);
            read.Should().BeOfType(root);
            read.Opaque.Should().BeFalse(text);
            CosmosSqlFormReader.Squeezed(read.Written).Should().Be(CosmosSqlFormReader.Squeezed(text));
        }

        /// <summary>Branch-only (h16): <c>NOT</c> binds tighter than a comparison, so <c>NOT c.a = 1</c> compares <c>NOT c.a</c>; a text outside the grammar is one opaque text.</summary>
        [Fact]
        public void SyntaxBindsNotTightestAndReadsTheRestAsOpaque()
        {
            CosmosSqlFormReader.ReadSyntax("NOT c.a = 1").Should().BeOfType<CosmosSqlFormReader.BinarySyntax>()
                .Which.Left.Should().BeOfType<CosmosSqlFormReader.UnarySyntax>();
            CosmosSqlFormReader.ReadSyntax("c.a = { \"k\": 1 }").Should().Be(new CosmosSqlFormReader.OpaqueSyntax("c.a = { \"k\": 1 }"));
            CosmosSqlFormReader.ReadSyntax("(c.a = 1").Opaque.Should().BeTrue();
        }

        /// <summary>
        /// Branch-only (h16): the tree the reader reads beside the ternary and <c>??</c>, which a round trip cannot show: the
        /// ternary loosest, so <c>a OR b ? c : d</c> asks <c>a OR b</c>, and <c>??</c> beneath it and above <c>OR</c>.
        /// </summary>
        [Fact]
        public void SyntaxReadsTheTernaryLoosestAndCoalesceBeneathIt()
        {
            var ternary = CosmosSqlFormReader.ReadSyntax("c.a OR c.b ? c.c : c.d").Should().BeOfType<CosmosSqlFormReader.TernarySyntax>().Subject;
            ternary.Condition.Should().BeOfType<CosmosSqlFormReader.BinarySyntax>().Which.Operator.Should().Be("OR");
            CosmosSqlFormReader.ReadSyntax("c.a ?? c.b ? c.c : c.d").Should().BeOfType<CosmosSqlFormReader.TernarySyntax>()
                .Which.Condition.Should().BeOfType<CosmosSqlFormReader.CoalesceSyntax>();
            CosmosSqlFormReader.ReadSyntax("c.a ?? c.b OR c.c").Should().BeOfType<CosmosSqlFormReader.CoalesceSyntax>()
                .Which.Right.Should().BeOfType<CosmosSqlFormReader.BinarySyntax>().Which.Operator.Should().Be("OR");
        }

        static string S(string name) => SqlValue.Spell(P(name));

        static ServiceForm D(string name) => ServiceForm.TestOf(CosmosServiceTest.IsDefined, S(name));

        static ServiceForm N(string name) => ServiceForm.TestOf(CosmosServiceTest.IsNull, S(name));

    }

}
