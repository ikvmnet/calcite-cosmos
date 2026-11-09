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

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The text cast to <c>BOOLEAN</c>, the way a view types a boolean column (<c>Calcite.CastToBooleanParses</c>): S reads
    /// it as the text's parse, a reader of its own, and proves what the translator writes over a path the container declares
    /// a boolean, the path written bare, its negation, its comparisons with a boolean and its truth and null tests; over a
    /// path that may hold a string, which the parse reads as a boolean and the service as a string, it proves none.
    /// </summary>
    public sealed class RenderingBooleanCastTests
    {

        static readonly JsonDocumentPath FlagPath = JsonDocumentPath.Root.Property("flag");

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias);

        static RexNode Flag => Rex.makeAbstractCast(RenderingRexBuilderTests.Types.createSqlType(SqlTypeName.BOOLEAN), JsonValue("flag", SqlTypeName.VARCHAR));

        /// <summary>What a declared boolean holds: a boolean, or nothing; and beside it a null, which the schema may allow.</summary>
        static readonly PathValues[] Declared = [PathValues.OfKinds(ValueSet.Of(BooleanType, PathValues.AbsentKind)), PathValues.OfKinds(ValueSet.Of(BooleanType, NullType, PathValues.AbsentKind))];

        /// <summary>The shapes the translator writes over a stored boolean (<c>CosmosRexTranslator.TryStoredBoolean</c>, <c>WriteTruthTest</c>).</summary>
        static RexNode[] Shapes =>
        [
            Flag,
            Rex.makeCall(SqlStdOperatorTable.NOT, Flag),
            Rex.makeCall(SqlStdOperatorTable.EQUALS, Flag, Rex.makeLiteral(true)),
            Rex.makeCall(SqlStdOperatorTable.EQUALS, Flag, Rex.makeLiteral(false)),
            Rex.makeCall(SqlStdOperatorTable.IS_TRUE, Flag),
            Rex.makeCall(SqlStdOperatorTable.IS_FALSE, Flag),
            Rex.makeCall(SqlStdOperatorTable.IS_NOT_TRUE, Flag),
            Rex.makeCall(SqlStdOperatorTable.IS_NOT_FALSE, Flag),
            Rex.makeCall(SqlStdOperatorTable.IS_NULL, Flag),
        ];

        static RenderingAnswer Generate(SqlPredicate root, RenderingSet set, PathValues values)
        {
            using var bundle = ProbeTheory.Open(set);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, values, ClassSet(JsonCharacterBand.None));

            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([root])).StopReason.Should().Be(RunStopReason.Completed);
            bundle.Failure.Should().BeNull();
            return bundle.ReadBest(term);
        }

        /// <summary>The reader is the text's parse, and an atom of it has the edges row S cuts it by; S owns none until the switch.</summary>
        [Fact]
        public void TheCastIsReadAsTheTextsParse()
        {
            var read = Loading.Read(Flag);
            read.Should().Be(new SqlPredicate.Compare(PredicateAtom.Equal, new SqlValue.Cast("BOOLEAN", new SqlValue.Text(FlagPath)), SqlValue.Of(true)));
            RenderingFamily.ReaderOf(((SqlPredicate.Compare)read).Left).Should().Be(PredicateAtom.CastReader("BOOLEAN", PredicateAtom.TextReader));
            ReaderEdgesTable.Rows.Should().ContainKey(PredicateAtom.CastReader("BOOLEAN", PredicateAtom.TextReader));
            PredicateAtom.Compare(PredicateAtom.CastReader("BOOLEAN", PredicateAtom.TextReader), PredicateAtom.Equal, Of(true)).RestsOn(ProbeRows.ReaderRows).Should().Contain("Calcite.CastToBooleanParses");
        }

        /// <summary>
        /// Over a declared boolean every shape is Proven under <c>Emulator</c>, each with a form of S's own over the path; under
        /// <c>Production</c> so is every one but a truth test S reads as a <c>CASE</c> whose ternary row Azure has not measured
        /// (S1, h14b's run 1): those it may leave Unproven until then, and refutes none.
        /// </summary>
        [Fact]
        public void EveryShapeOverADeclaredBooleanIsProven()
        {
            foreach (var set in new[] { RenderingSet.Production, RenderingSet.Emulator })
                foreach (var values in Declared)
                    foreach (var shape in Shapes)
                    {
                        var read = Loading.Read(shape);
                        read.Should().NotBeOfType<SqlPredicate.Opaque>();
                        var answer = Generate(read, set, values);
                        if (set == RenderingSet.Production && read.Subterms.Any(t => t is SqlPredicate.Case))
                        {
                            answer.Kind.Should().BeOneOf([RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven], $"{shape} may wait on S1 under {set}");
                            TestContext.Current.TestOutputHelper?.WriteLine($"{shape} under {set} over {values}: {answer.Kind}");
                            continue;
                        }

                        answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{shape} under {set} over {values}: {answer.Reason}");
                        answer.Best!.Paths.Should().BeEquivalentTo([SqlValue.Spell(FlagPath)]);
                    }
        }

        /// <summary>
        /// Where the path may hold a string the parse reads <c>"TRUE"</c> as true, a string the service reads as a string, so
        /// the cast written bare is Unproven under both sets. Where it may hold a number instead the parse raises on every
        /// such row, which anything but a failure refines (<c>AgreementRows</c>), so it is Proven.
        /// </summary>
        [Fact]
        public void NothingIsProvenWhereThePathMayHoldAString()
        {
            foreach (var set in new[] { RenderingSet.Production, RenderingSet.Emulator })
            {
                foreach (var values in new[] { PathValues.Anything, PathValues.OfKinds(ValueSet.Of(BooleanType, StringType)) })
                    Generate(Loading.Read(Flag), set, values).Kind.Should().Be(RenderingVerdictKind.Unproven, $"under {set} over {values}");

                Generate(Loading.Read(Flag), set, PathValues.OfKinds(ValueSet.Of(BooleanType, IntegerType))).Kind.Should().Be(RenderingVerdictKind.Proven, $"a number raises, under {set}");
            }
        }

    }

}
