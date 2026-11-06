using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
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

        /// <summary>The registry's S, production's own definition (<see cref="CosmosRendering.Definition"/>).</summary>
        public static RenderingDefinition Measured => CosmosRendering.Definition;

        static readonly System.Lazy<RenderingTheory> Probe = new(() => RenderingTheory.Define(Measured));

        /// <summary>
        /// S as measured from the rows given, with nothing the host names but the registry's rows of characters, which the
        /// parted, sets and spell rules read (<see cref="RenderingDefinition.Parts"/> and its neighbours, F3 and G1): the
        /// definition a test that plants rows starts from.
        /// </summary>
        public static RenderingDefinition Planted(IReadOnlyList<MeasuredOutcome> rows) => RenderingDefinition.Measured(rows) with
        {
            Parts = Measured.Parts,
            Moves = Measured.Moves,
            StoredAs = Measured.StoredAs,
            Truncations = Measured.Truncations,
            Rewrites = Measured.Rewrites,
            Unsent = Measured.Unsent,
        };

        /// <summary>S defined from the registry, once a run.</summary>
        public static RenderingTheory ProbeTheory => Probe.Value;

        /// <summary>
        /// Production's definition with the templates it asks only from the switch as well (<see cref="RenderingTemplateRows.FromTheSwitch"/>),
        /// the definition production takes at h15.
        /// </summary>
        public static RenderingDefinition Switched => Measured with { Templates = RenderingTemplateRows.Templates };

        static readonly System.Lazy<RenderingTheory> SwitchedProbe = new(() => RenderingTheory.Define(Switched));

        /// <summary>S defined from <see cref="Switched"/>, once a run.</summary>
        public static RenderingTheory SwitchedTheory => SwitchedProbe.Value;

        /// <summary>The raw columns' truths S's rule joins from the registry's row (h12), which a raw reading's cells read.</summary>
        public static RawTruths Columns => ProbeTheory.Theory.Get(ProbeTheory.RawUnion.Of());

        /// <summary>The table S settles for the set CI asserts under.</summary>
        public static ServiceTable Table => ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingCheckRun.AssertedSet)), Placed);

        /// <summary>The classes S places by their order's comparators, derived from the registry's rows.</summary>
        public static ValueSet<CosmosValueClass> Placed => ProbeTheory.Theory.Get(ProbeTheory.Placed.Of());

        public static readonly JsonDocumentPath LabelPath = P("label"), KindPath = P("kind"), CategoryPath = P("category"), MetadataPath = P("metadata"), FlagPath = P("flag");

        public static readonly string Label = SqlValue.Spell(LabelPath);

        public static ServiceForm All(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.And, members);

        public static ServiceForm Any(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.Or, members);

        public static ServiceForm Negated(ServiceForm operand) => ServiceForm.NotOf(operand);

        /// <summary>The one source a test's bundle imports from.</summary>
        public static readonly ImportSource Source = new("test", "test", 0);

        /// <summary>Text <c>label &lt;&gt; 'bikes'</c>, the conjunct most tests ask.</summary>
        public static readonly SqlPredicate NotBikes = Ne(Text(LabelPath), "bikes");

        /// <summary>The bare comparison for text <c>label &lt;&gt; 'bikes'</c>, refuted for Equal where a <c>WHERE</c> keeps a null, an object or an array (T5).</summary>
        public static readonly ServiceForm Bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Label, JsonValues.Of("bikes"));

        /// <summary>The guarded comparison the translator writes for it, exact (T1).</summary>
        public static readonly ServiceForm Guarded = ServiceForm.JunctionOf(CosmosServiceTest.And,
            [ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, Label), ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, Label)), Bare]);

        /// <summary>
        /// A plant's table: one entry a key, holding every answer its rows give and every row's id, with the roles S's rule
        /// settles of them, and the classes its rows order placed, as S's rules derive both.
        /// </summary>
        public static ServiceTable TableOf(IEnumerable<MeasuredOutcome> rows)
        {
            var all = rows.ToList();
            return ServiceTable.Of(ValueSet.From(all.GroupBy(row => row.Key).Select(key => EntryOf(key.Key, ValueSet.From(key.Select(row => row.Answer)), ValueSet.From(key.Select(row => row.AxiomId))))),
                ValueSet.From(all.Where(row => row.Key.Test == CosmosServiceTest.Order).Select(row => row.Key.Operand)));
        }

        /// <summary>A key's entry as S's rule settles it from the measured meanings: its outcomes, and its answers' roles.</summary>
        static MeasuredEntry EntryOf(MeasuredKey key, ValueSet<CosmosAnswer> answers, ValueSet<string> evidence)
        {
            var means = AnswerMeanings.Measured;
            var placing = ValueSet.From(answers.Where(means.Comparators.Contains));
            return MeasuredEntry.Placed(MeasuredEntry.Of(key, answers, evidence, means), placing, ValueSet.From(answers.Where(means.Matchers.Contains)),
                ValueSet.From(placing.Where(a => means.Matchers.Contains(a) == false)));
        }

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
