using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The guarded comparison (spec h09): a number compared through a cast of the text, <c>RETURNING DECIMAL</c> or the
    /// text, which Calcite parses where the service compares no string, written only as its pair beside
    /// <c>IS_NUMBER(p)</c>; C4n built as the absorbing <c>CASE</c> and C2 as the opposite one, each read back by the loader
    /// and proven by S at the filter beneath; the literals the cut leaves Unproven there; the text alternatives; and
    /// production asking the three templates only from the switch.
    /// </summary>
    public sealed class RenderingGuardedTests
    {

        static readonly string P = SqlValue.Spell(JsonDocumentPath.Root.Property("castDOUBLE")), Text = SqlValue.Spell(JsonDocumentPath.Root.Property("text"));

        static readonly string Double = PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader), Decimal = PredicateAtom.TypedReader("DECIMAL");

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias);

        /// <summary>A cast of the text to <c>DOUBLE</c> compared with a number, the conjunct whose accessors a form is built over.</summary>
        static RexNode Conjunct(string literal) => Rex.makeCall(SqlStdOperatorTable.GREATER_THAN,
            Rex.makeCast(Nullable(SqlTypeName.DOUBLE), JsonValue("castDOUBLE", SqlTypeName.VARCHAR)), Rex.makeExactLiteral(new java.math.BigDecimal(literal)));

        static ServiceForm Compared(string literal) => ServiceForm.Read(ServiceForm.CompareOf(CosmosServiceTest.Greater, P, JsonValues.Of(decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture))), Double);

        static ServiceForm C4n(string literal) => ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.TestOf(CosmosServiceTest.IsNumber, P), Compared(literal)]);

        static ServiceForm C2(string literal) => ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.TestOf(CosmosServiceTest.IsDefined, P),
            ServiceForm.JunctionOf(CosmosServiceTest.Or, [ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNumber, P)), Compared(literal)])]);

        /// <summary>The form's Rex over the conjunct's accessors, and the loader's reading of it.</summary>
        static (RexNode Rex, SqlPredicate Read) Built(ServiceForm form, RexNode conjunct)
        {
            var rex = RenderingRexBuilder.Build(form, Rex, Loading.AccessorsOf(conjunct, Rex))!;
            return (rex, Loading.Read(rex));
        }

        /// <summary>
        /// C4n is <c>CASE WHEN IS_NUMBER(raw) THEN acc op n ELSE FALSE END</c>, which S reads by the absorbing rule, and C2
        /// <c>IS_DEFINED(raw) AND CASE … ELSE TRUE END</c>, by the opposite rule over <c>IS_NUMBER</c>, two-valued: each read
        /// back by the loader as a <c>case</c> and proven by S as the form under each asserted set.
        /// </summary>
        [Fact]
        public void TheGuardedPairIsACaseSProvesAtTheFilterBeneath()
        {
            var (c4n, c4nRead) = Built(C4n("50"), Conjunct("50"));
            var (c2, c2Read) = Built(C2("50"), Conjunct("50"));
            c4n.toString().Should().StartWith("CASE(IS_NUMBER(").And.EndWith("false)");
            c2.toString().Should().StartWith("AND(IS_DEFINED(").And.Contain("CASE(IS_NUMBER(").And.EndWith("true))");
            (c4nRead.Subterms.OfType<SqlPredicate.Case>().Count(), c2Read.Subterms.OfType<SqlPredicate.Case>().Count()).Should().Be((1, 1));
            foreach (var set in RenderingGenerateRun.AssertedSets)
            {
                Claim(C4n("50"), c4nRead, RenderPosition.Pos, Verdict.Equal, set).Kind.Should().Be(RenderingVerdictKind.Proven, $"C4n under {set}");
                Claim(C2("50"), c2Read, RenderPosition.Pos, Verdict.Equal, set).Kind.Should().Be(RenderingVerdictKind.Proven, $"C2 under {set}");
            }
        }

        /// <summary>
        /// A literal at 2^53, at 2^60 + 1, and one no double holds is Unproven at the filter beneath: the cut the service's
        /// doubles give (<c>Service.NumbersBeyondLongCompareAsDoubles</c>) leaves the arm's cells apart, not a C# window.
        /// </summary>
        [Fact]
        public void ALiteralTheCutLeavesIsUnprovenBeneath()
        {
            foreach (var literal in new[] { "9007199254740992", "1152921504606846977", "0.1" })
                foreach (var form in new[] { C4n(literal), C2(literal) })
                    Claim(form, Built(form, Conjunct(literal)).Read, RenderPosition.Pos, Verdict.Equal, RenderingSet.Production).Kind
                        .Should().Be(RenderingVerdictKind.Unproven, $"{form}");
        }

        /// <summary>
        /// A number over a parsing reader is written only as its guarded pair, and over <c>RETURNING DECIMAL</c>, typed
        /// <c>DECIMAL(19, 0)</c>, a whole number alone; over a reader typed as a number it is written as it stands.
        /// </summary>
        [Fact]
        public void ANumberOverAParsingReaderIsWrittenOnlyAsItsPair()
        {
            var grammar = RenderingRexGrammar.Written;
            ServiceForm Over(string reader, decimal n) => ServiceForm.Read(ServiceForm.CompareOf(CosmosServiceTest.Greater, P, JsonValues.Of(n)), reader);
            ServiceForm Pair(ServiceForm member) => ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.TestOf(CosmosServiceTest.IsNumber, P), member]);

            (grammar.Writes(Over(Double, 50m), Double), grammar.Writes(Pair(Over(Double, 50m)), Double), grammar.Writes(Pair(Over(Double, 2.5m)), Double)).Should().Be((false, true, true));
            (grammar.Writes(Pair(Over(Decimal, 50m)), Decimal), grammar.Writes(Pair(Over(Decimal, 2.5m)), Decimal)).Should().Be((true, false));
            grammar.Writes(ServiceForm.JunctionOf(CosmosServiceTest.Or, [ServiceForm.TestOf(CosmosServiceTest.IsNumber, P), Over(Double, 50m)]), Double)
                .Should().BeFalse("IS_NUMBER(p) guards a comparison beside it in an AND, and NOT IS_NUMBER(p) in an OR");
            grammar.Writes(Over(PredicateAtom.TypedReader("DOUBLE"), 50m), PredicateAtom.TypedReader("DOUBLE")).Should().BeTrue();
        }

        /// <summary>
        /// A text equality with a string spelling a number is the string or the number it spells (loss 1): S holds it implied
        /// at <c>Pos</c>, its number written as the guarded pair through the text's cast to <c>DOUBLE</c> and proven beneath;
        /// a string spelling no number has none.
        /// </summary>
        [Fact]
        public void ATextEqualityWithANumeralIsTheStringOrTheNumber()
        {
            var form = ProbeRows.Spell(TemplateId.TextAlternatives, RenderPosition.Pos, PredicateAtom.Compare(PredicateAtom.TextReader, "=", JsonValues.Of("30")), Text);
            form.Text.Should().Be(ServiceForm.JunctionOf(CosmosServiceTest.Or, [ServiceForm.CompareOf(CosmosServiceTest.Equal, Text, JsonValues.Of("30")),
                ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.TestOf(CosmosServiceTest.IsNumber, Text), ServiceForm.CompareOf(CosmosServiceTest.Equal, Text, JsonValues.Of(30m))])]).Text);
            ProbeRows.Spell(TemplateId.TextAlternatives, RenderPosition.Pos, PredicateAtom.Compare(PredicateAtom.TextReader, "=", JsonValues.Of("bikes")), Text).Kind
                .Should().Be(ServiceFormKind.Opaque);

            var conjunct = Rex.makeCall(SqlStdOperatorTable.EQUALS, JsonValue("text", SqlTypeName.VARCHAR), Rex.makeLiteral("30"));
            var atom = Loading.Read(conjunct);
            using (var bundle = ProbeRows.SwitchedTheory.Open(RenderingSet.Production))
            {
                var term = bundle.Write(atom);
                foreach (var path in atom.Paths)
                    bundle.Import(ProbeRows.Source, path, PathValues.Anything, JsonValues.AllBands);
                bundle.Generate(term);
                bundle.RunGenerate(RenderingBundle.WidthOf([atom]));
                (bundle.Scope.TryGet(bundle.Theory.Implied.Of(term, RenderPosition.Pos), out var implied) ? implied : ValueSet<ServiceForm>.Empty).Should().Contain(form);
            }

            Claim(form, Built(form, conjunct).Read, RenderPosition.Pos, Verdict.Equal, RenderingSet.Production).Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// Production asks the guarded comparison's templates only from the switch (h15): until site W asks S of the filter
        /// beneath, the translator writes no <c>CASE</c>, and a weakening holding one would be lost.
        /// </summary>
        [Fact]
        public void ProductionAsksTheGuardedTemplatesOnlyFromTheSwitch()
        {
            CosmosRendering.Definition.Templates.Select(t => t.Item1).Should().NotContain(RenderingTemplateRows.FromTheSwitch);
            ProbeRows.Switched.Templates.Select(t => t.Item1).Should().Contain(RenderingTemplateRows.FromTheSwitch);
        }

    }

}
