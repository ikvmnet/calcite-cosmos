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

        static string S(string name) => SqlValue.Spell(P(name));

        static ServiceForm D(string name) => ServiceForm.TestOf(CosmosServiceTest.IsDefined, S(name));

        static ServiceForm N(string name) => ServiceForm.TestOf(CosmosServiceTest.IsNull, S(name));

    }

}
