using System;
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
        /// parted, sets and spell rules read (<see cref="RenderingDefinition.Parts"/> and its neighbours, F3 and G1), and of
        /// readings, which the roles and pair rules read (<see cref="RenderingDefinition.WholeReadings"/> and its neighbours,
        /// G2 and G3): the definition a test that plants rows starts from.
        /// </summary>
        public static RenderingDefinition Planted(IReadOnlyList<MeasuredOutcome> rows) => RenderingDefinition.Measured(rows) with
        {
            Parts = Measured.Parts,
            Moves = Measured.Moves,
            StoredAs = Measured.StoredAs,
            Truncations = Measured.Truncations,
            Folds = Measured.Folds,
            Rewrites = Measured.Rewrites,
            Unsent = Measured.Unsent,
            WholeReadings = Measured.WholeReadings,
            PairReadings = Measured.PairReadings,
            ReaderOrders = Measured.ReaderOrders,
            ReaderKinds = Measured.ReaderKinds,
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

        /// <summary>Every reading's rows S's rules join from the registry's (G7), which Calcite's side of a cell evaluates a reading by.</summary>
        public static ReadingTable ReaderRows => ProbeTheory.Theory.Get(ProbeTheory.ReaderRows.Of());

        /// <summary>The table S settles for the set CI asserts under, with its pair table and the units its LIKE reads (G7).</summary>
        public static ServiceTable Table => ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingCheckRun.AssertedSet)), Placed,
            ProbeTheory.Theory.Get(ProbeTheory.PairTable.Of(RenderingCheckRun.AssertedSet)), ProbeTheory.Theory.Get(ProbeTheory.LikesOf.Of(RenderingCheckRun.AssertedSet)));

        /// <summary>The form a template proposes for an atom, its LIKE's pattern read by Calcite's rows as S's template rule reads it (G7).</summary>
        public static ServiceForm Spell(TemplateId id, RenderPosition pos, PredicateAtom atom, string path) => RenderingTemplates.Spell(id, pos, RenderingTemplates.Read(atom, ReaderRows), path);

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
        /// A plant's table as S settles it, never as a test computes one: S defined from the rows given (<see cref="Planted"/>)
        /// and viewed under a set (<see cref="RenderingTheory.View"/>). One entry a key every target the set serves measured,
        /// holding their answers and rows, with the readers S's roles rules find; the key of a side that may fail where the
        /// set settled every key it stands for, by S's failing rules; the classes the rows order placed; and the pair table.
        /// </summary>
        public static ServiceTable TableOf(IEnumerable<MeasuredOutcome> rows, RenderingSet set = RenderingSet.Emulator) => TableOf(Planted([.. rows]), set);

        /// <summary>The table S settles under a set from a definition (<see cref="TableOf(IEnumerable{MeasuredOutcome}, RenderingSet)"/>), empty where it settled no key.</summary>
        public static ServiceTable TableOf(RenderingDefinition definition, RenderingSet set)
        {
            var theory = RenderingTheory.Define(definition);
            var table = ViewOf(theory, set) ?? ServiceTable.Of(ValueSet<MeasuredEntry>.Empty, theory.Theory.Get(theory.Placed.Of()));
            GC.KeepAlive(theory);
            return table;
        }

        /// <summary>Every set's table S settles from the rows given, where it settled any key.</summary>
        public static IReadOnlyList<ServiceTable> TablesOf(IEnumerable<MeasuredOutcome> rows)
        {
            var definition = Planted([.. rows]);
            var theory = RenderingTheory.Define(definition);
            var tables = definition.Serves.Keys.Select(set => ViewOf(theory, set)).OfType<ServiceTable>().ToList();
            GC.KeepAlive(theory);
            return tables;
        }

        static ServiceTable? ViewOf(RenderingTheory theory, RenderingSet set) => theory.Theory.TryGet(theory.View.Of(set), out var view) ? view : null;

        /// <summary>
        /// Rows giving each key the answers listed, read under <see cref="RenderingSet.Portable"/>, which serves both targets: S
        /// holds one answer a key on a target, so a key of two answers gives the emulator its first and Azure its second, and
        /// a key of one answer both.
        /// </summary>
        public static IEnumerable<MeasuredOutcome> Portable(IEnumerable<(MeasuredKey Key, CosmosAnswer Answer)> answers) =>
            from key in answers.GroupBy(each => each.Key, each => each.Answer)
            let given = key.Distinct().ToList()
            from target in given.Count <= 2 ? new[] { RenderingTarget.Emulator, RenderingTarget.Azure } : throw new ArgumentException($"{key.Key} is given {given.Count} answers, more than the two targets hold", nameof(answers))
            select new MeasuredOutcome("Service.Planted", key.Key, target, given[target == RenderingTarget.Emulator ? 0 : given.Count - 1]);

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
