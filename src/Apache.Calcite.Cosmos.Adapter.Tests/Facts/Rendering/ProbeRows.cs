using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The rendering tests' fixture: the registry's rows as S reads them (<see cref="CosmosAxiomRows"/>), and no others; the
    /// theory defined from them, once, and the table it settles for the asserted set; the probe's label forms
    /// (<c>probes/rcore/Program.cs</c>); and a bundle that checks one claim. A test that needs a row the registry lacks
    /// plants it under an id no row holds.
    /// </summary>
    internal static class ProbeRows
    {

        public static IReadOnlyList<MeasuredOutcome> Rows { get; } = CosmosAxiomRows.Read(CosmosAxioms.All);

        public static RenderingDefinition Measured { get; } = RenderingDefinition.Measured(Rows);

        static readonly System.Lazy<RenderingTheory> Probe = new(() => RenderingTheory.Define(Measured));

        /// <summary>S defined from the registry, once a run.</summary>
        public static RenderingTheory ProbeTheory => Probe.Value;

        /// <summary>The table S settles for the set CI asserts under.</summary>
        public static ServiceTable Table => ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingCheckRun.AssertedSet)));

        public static readonly JsonDocumentPath LabelPath = P("label"), KindPath = P("kind"), CategoryPath = P("category"), MetadataPath = P("metadata"), FlagPath = P("flag");

        public static readonly string Label = SqlValue.Spell(LabelPath);

        public static ServiceForm All(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.And, members);

        public static ServiceForm Any(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.Or, members);

        public static ServiceForm Negated(ServiceForm operand) => ServiceForm.NotOf(operand);

        /// <summary>The one source a test's bundle imports from.</summary>
        public static readonly ImportSource Source = new("test", "test", 0);

        /// <summary>The bare comparison for text <c>label &lt;&gt; 'bikes'</c>, refuted for Equal where a <c>WHERE</c> keeps a null, an object or an array (T5).</summary>
        public static readonly ServiceForm Bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Label, JsonValues.Of("bikes"));

        /// <summary>The guarded comparison the translator writes for it, exact (T1).</summary>
        public static readonly ServiceForm Guarded = ServiceForm.JunctionOf(CosmosServiceTest.And,
            [ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, Label), ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, Label)), Bare]);

        /// <summary>A plant's table: one entry a key, holding every answer its rows give and every row's id.</summary>
        public static ServiceTable TableOf(IEnumerable<MeasuredOutcome> rows) => ServiceTable.Of(ValueSet.From(rows.GroupBy(row => row.Key).Select(key =>
            MeasuredEntry.Of(key.Key, ValueSet.From(key.Select(row => row.Answer)), ValueSet.From(key.Select(row => row.AxiomId)), AnswerMeanings.Measured))));

        /// <summary>
        /// Checks a form claimed for a conjunct in a bundle of its own, every path imported as anything unless given, and
        /// reads both claims; the run must complete with one node per class and no contradiction.
        /// </summary>
        public static (RenderingVerdict Equal, RenderingVerdict Implies) Check(SqlPredicate conjunct, ServiceForm form, RenderingTheory? theory = null,
            RenderingSet set = RenderingCheckRun.AssertedSet, JsonDocumentPath? path = null, PathValues? values = null, ValueSet<string>? classes = null)
        {
            using var bundle = (theory ?? ProbeTheory).Open(set);
            var root = bundle.Write(conjunct);
            foreach (var each in new[] { LabelPath, KindPath, CategoryPath, MetadataPath, FlagPath })
                bundle.Import(Source, each, each == path ? values! : PathValues.Anything, each == path ? classes ?? JsonValues.AllBands : JsonValues.AllBands);

            bundle.Claim(root, form);
            var run = bundle.Run();
            run.Should().Be(new RenderingRun(RunStopReason.Completed, true, false), form.Text);
            return (bundle.Read(root, form, Verdict.Equal), bundle.Read(root, form, Verdict.Implies));
        }

    }

}
