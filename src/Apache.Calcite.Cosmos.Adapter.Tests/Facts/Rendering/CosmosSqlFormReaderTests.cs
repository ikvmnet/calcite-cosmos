using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The reader of the text sent (<see cref="CosmosSqlFormReader"/>): every text the check run captured reads back to
    /// itself byte for byte, none in the family reads as opaque, and its precedence is the grammar's, pinned.
    /// </summary>
    public sealed class CosmosSqlFormReaderTests
    {

        static readonly CosmosParameter[] Parameters = [new("@p0", "x")];

        /// <summary>
        /// Texts that mix <c>NOT</c>, <c>AND</c> and <c>OR</c> without parentheses, and two that keep them, each with the tree
        /// it must read as: <c>NOT</c> binds tighter than <c>AND</c>, <c>AND</c> than <c>OR</c>, and grouping stays as written.
        /// The service is held to the same trees (<see cref="CosmosSqlPrecedenceMeasurementTests"/>).
        /// </summary>
        internal static readonly (string Text, ServiceForm Tree)[] Pinned =
        [
            ("NOT IS_DEFINED(c.a) AND IS_DEFINED(c.b)", And(Not(D("a")), D("b"))),
            ("IS_DEFINED(c.a) OR IS_DEFINED(c.b) AND IS_DEFINED(c.c)", Or(D("a"), And(D("b"), D("c")))),
            ("IS_DEFINED(c.a) AND IS_DEFINED(c.b) OR IS_DEFINED(c.c)", Or(And(D("a"), D("b")), D("c"))),
            ("NOT IS_DEFINED(c.a) OR IS_NULL(c.a)", Or(Not(D("a")), N("a"))),
            ("IS_DEFINED(c.a) AND NOT IS_NULL(c.a) AND (c.a != @p0)", And(D("a"), Not(N("a")), ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S("a"), JsonValues.Of("x")))),
            ("NOT NOT IS_DEFINED(c.a) AND IS_NULL(c.b)", And(Not(Not(D("a"))), N("b"))),
            ("IS_NULL(c.a) OR NOT IS_DEFINED(c.b) AND IS_DEFINED(c.c)", Or(N("a"), And(Not(D("b")), D("c")))),
            ("NOT IS_DEFINED(c.a) AND NOT IS_DEFINED(c.b) OR IS_NULL(c.c)", Or(And(Not(D("a")), Not(D("b"))), N("c"))),
            ("IS_DEFINED(c.a) AND (IS_DEFINED(c.b) OR IS_NULL(c.c))", And(D("a"), Or(D("b"), N("c")))),
            ("NOT (IS_DEFINED(c.a) AND IS_NULL(c.b)) OR IS_DEFINED(c.c)", Or(Not(And(D("a"), N("b"))), D("c"))),
            ("(IS_DEFINED(c.a) AND IS_NULL(c.b)) AND IS_DEFINED(c.c)", And(And(D("a"), N("b")), D("c"))),
            ("IS_NULL(c.a) OR NOT IS_NULL(c.b) AND NOT IS_DEFINED(c.c) OR IS_DEFINED(c.a)", Or(N("a"), And(Not(N("b")), Not(D("c"))), D("a"))),
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
            captured.Where(c => c.InFamily && Opaque(c.Read.Form)).Select(c => c.Text + " read as " + c.Read.Form).Should().BeEmpty();
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
        /// A parameter reads as its value, one bound only when the statement runs makes its comparison opaque, and a primary
        /// outside the grammar reads as opaque up to the connective after it, while what it stands beside reads as itself.
        /// </summary>
        [Fact]
        public void WhatTheGrammarLeavesOutIsOpaque()
        {
            CosmosSqlFormReader.Read("(c.n > @p0)", [new("@p0", 30L)]).Form.Should().Be(ServiceForm.CompareOf(CosmosServiceTest.Greater, S("n"), JsonValues.Of(30m)));
            CosmosSqlFormReader.Read("(c.n > @p0)", [new("@p0", new CosmosDynamicValue(0))]).Form.Kind.Should().Be(ServiceFormKind.Opaque);
            CosmosSqlFormReader.Read("(STARTSWITH(c.a, @p0) AND IS_DEFINED(c.b))", Parameters).Should().Be(new ReadForm(
                And(ServiceForm.OpaqueOf("STARTSWITH(c.a, @p0)", Eggnog.ValueSet.Of(S("a"))), D("b")), "(STARTSWITH(c.a, @p0) AND IS_DEFINED(c.b))"));
        }

        static bool Opaque(ServiceForm form) => form.Kind == ServiceFormKind.Opaque || form.Members.Any(Opaque);

        static string S(string name) => SqlValue.Spell(P(name));

        static ServiceForm D(string name) => ServiceForm.TestOf(CosmosServiceTest.IsDefined, S(name));

        static ServiceForm N(string name) => ServiceForm.TestOf(CosmosServiceTest.IsNull, S(name));

        static ServiceForm Not(ServiceForm operand) => ServiceForm.NotOf(operand);

        static ServiceForm And(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.And, members);

        static ServiceForm Or(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.Or, members);

    }

}
