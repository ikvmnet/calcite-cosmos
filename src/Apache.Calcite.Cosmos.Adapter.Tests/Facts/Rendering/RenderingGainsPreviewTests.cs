using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The gains preview (spec §6): forms the translator does not write today, for text comparisons over typed's bare
    /// accessor that it refuses, each claimed against the IR the loader reads for the statement; the bare comparison is
    /// the control, refuted (T5). On a service, each form keeps Calcite's rows and the bare one does not.
    /// </summary>
    /// <remarks>
    /// The inequality's form is Exact. The orderings' are not refuted and not proven: the label is undeclared, so its
    /// strings may hold U+0000, which the emulator stores as U+FDD0 and 0 and orders as stored, and the asserted set
    /// serves the emulator (<c>Service.StringOrderIsCodePoint</c>). Over strings that hold none they are Exact.
    /// </remarks>
    public sealed class RenderingGainsPreviewTests : IClassFixture<RenderingGainsPreviewTests.Fixture>
    {

        const string Typed = "SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') ",
            View = "SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" ";

        const string Unequal = "IS_PRIMITIVE(c.label) AND NOT IS_NULL(c.label) AND c.label != @p0", Above = "c.label > @p0 OR IS_BOOL(c.label)";

        /// <summary>Each statement, the form that would be its gain and what the check reads it as, and the bare comparison and its refutation.</summary>
        static readonly (string Sql, string Gain, string Reads, string Bare, string Refuted)[] Cases =
        [
            (Typed + "<> 'bikes'", Unequal, "Exact", "c.label != @p0", "$[\"label\"]=array (U,t)"),
            (Typed + "> 'bikes'", Above, "Unproven", "c.label > @p0", "$[\"label\"]=boolean|false (T,u)"),
            (Typed + "< 'bikes'", "c.label < @p0 OR IS_NUMBER(c.label)", "Unproven", "c.label < @p0", "$[\"label\"]=fractions (T,u)"),
            (View + "<> 'bikes'", Unequal, "Exact", "c.label != @p0", "$[\"label\"]=array (U,t)"),
            (View + "> 'bikes'", Above, "Unproven", "c.label > @p0", "$[\"label\"]=boolean|false (T,u)"),
        ];

        readonly Fixture _service;

        public RenderingGainsPreviewTests(Fixture service) => _service = service;

        /// <summary>
        /// Each gain reads as pinned, and its bare form is refuted. The faults M8 and M9 let the translator admit the
        /// statement, so that its conjunct is captured; its IR is the loader's, which no fault touches.
        /// </summary>
        [Fact]
        public void EachGainReadsAsPinnedAndItsBareFormIsRefuted()
        {
            var read = Cases.Select(c => (c.Sql, Gain: Read(c.Sql, c.Gain), Bare: Read(c.Sql, c.Bare))).ToList();
            foreach (var (sql, gain, bare) in read)
                TestContext.Current.TestOutputHelper?.WriteLine($"{sql}: {gain}; bare {bare}");

            read.Should().Equal(Cases.Select(c => (c.Sql, c.Reads, c.Refuted)));

            static string Read(string sql, string form) => RenderingMutationTests.Show(RenderingRecallTests.Replay(sql, [form], [RenderingRecallTests.Bikes], M8 | M9).Asserted);
        }

        /// <summary>Where no string may hold U+0000, the orderings' gains are Exact under the asserted set.</summary>
        [Fact]
        public void TheOrderingsAreExactWhereNoStringHoldsUPlus0000()
        {
            var bands = ValueSet.From(JsonValues.AllBands.AsEnumerable().Where(b => b != JsonValues.NulClass));
            foreach (var (op, gain) in new[] { (">", Above), ("<", Cases[2].Gain) })
                ProbeRows.Check(Cmp(Text(ProbeRows.LabelPath), op, Lit("bikes")), CosmosSqlFormReader.Read(gain, [RenderingRecallTests.Bikes]).Form,
                    set: RenderingCheckRun.AssertedSet, path: ProbeRows.LabelPath, values: PathValues.Anything, classes: bands).Equal.Kind.Should().Be(RenderingVerdictKind.Proven, gain);
        }

        /// <summary>On a service each gain keeps the ids Calcite keeps, and each bare form does not.</summary>
        [Fact]
        public async Task OnTheService()
        {
            if (_service.Typed is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            foreach (var (sql, gain, _, bare, _) in Cases)
            {
                var calcite = await _service.Calcite(sql);
                (await _service.Service(_service.Typed, gain, [RenderingRecallTests.Bikes])).Should().Equal(calcite, gain);
                (await _service.Service(_service.Typed, bare, [RenderingRecallTests.Bikes])).Should().NotEqual(calcite, bare);
            }
        }

        /// <summary>A database of the preview's own.</summary>
        public sealed class Fixture() : RenderingRecallTests.Fixture("gains");

    }

}
