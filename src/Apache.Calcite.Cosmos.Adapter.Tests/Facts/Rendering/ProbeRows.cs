using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The rendering tests' fixture: the registry's rows as S reads them (<see cref="CosmosAxiomRows"/>), beside the probe's
    /// (<c>probes/rcore/Program.cs</c>) for the keys and targets the registry leaves to them, so that both service sets
    /// settle what the first checks read; the theory defined from them, once; the probe's label forms; and a bundle that
    /// checks one claim. The probe's rows are test data, each to go as the registry answers its key.
    /// </summary>
    internal static class ProbeRows
    {

        public static IReadOnlyList<MeasuredOutcome> Rows { get; } = Build();

        /// <summary>The table the service sets settle the rows into: one entry a key, holding what either target answered.</summary>
        public static ServiceTable Table { get; } = TableOf(Rows.Where(row => row.Target != RenderingTarget.Calcite));

        public static RenderingDefinition Measured { get; } = RenderingDefinition.Measured(Rows);

        static readonly System.Lazy<RenderingTheory> Probe = new(() => RenderingTheory.Define(Measured));

        /// <summary>S defined from the probe's rows, once a run.</summary>
        public static RenderingTheory ProbeTheory => Probe.Value;

        public static readonly JsonDocumentPath LabelPath = P("label"), KindPath = P("kind"), CategoryPath = P("category"), MetadataPath = P("metadata"), FlagPath = P("flag");

        public static readonly string Label = SqlValue.Spell(LabelPath);

        /// <summary>The bare comparison for text <c>label &lt;&gt; 'bikes'</c>, refuted for Equal where a <c>WHERE</c> keeps a null, an object or an array (T5).</summary>
        public static readonly ServiceForm Bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Label, JsonValues.Of("bikes"));

        /// <summary>The guarded comparison the translator writes for it, exact (T1).</summary>
        public static readonly ServiceForm Guarded = ServiceForm.JunctionOf(CosmosServiceTest.And,
            [ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, Label), ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, Label)), Bare]);

        /// <summary>
        /// One entry a key, holding every answer its rows give and every row's id: what <c>tabulate</c> settles where every
        /// row is written for each target a set serves, as the probe's are. The primitives' tests build plants with it.
        /// </summary>
        public static ServiceTable TableOf(IEnumerable<MeasuredOutcome> rows) => ServiceTable.Of(ValueSet.From(rows.GroupBy(row => row.Key).Select(key =>
            MeasuredEntry.Of(key.Key, ValueSet.From(key.Select(row => row.Answer)), ValueSet.From(key.Select(row => row.AxiomId)), AnswerMeanings.Measured))));

        /// <summary>
        /// Checks a form claimed for a conjunct in a bundle of its own, every path imported as anything unless given, and
        /// reads both claims; the run must complete with one node per class and no contradiction.
        /// </summary>
        public static (RenderingVerdict Equal, RenderingVerdict Implies) Check(SqlPredicate conjunct, ServiceForm form, RenderingTheory? theory = null,
            RenderingSet set = RenderingSet.Emulator, JsonDocumentPath? path = null, PathValues? values = null, ValueSet<string>? classes = null)
        {
            using var bundle = (theory ?? ProbeTheory).Open(set);
            var root = bundle.Write(conjunct);
            foreach (var each in new[] { LabelPath, KindPath, CategoryPath, MetadataPath, FlagPath })
                bundle.Import("test", each, each == path ? values! : PathValues.Anything, each == path ? classes ?? JsonValues.AllBands : JsonValues.AllBands);

            bundle.Claim(root, form);
            var run = bundle.Run();
            (run.StopReason, run.OneNodePerClass, run.Contradicted).Should().Be((RunStopReason.Completed, true, false), form.Text);
            return (bundle.Read(root, form, Verdict.Equal), bundle.Read(root, form, Verdict.Implies));
        }

        static List<MeasuredOutcome> Build()
        {
            // A probe row for a key and target the registry answers is refused, so none outlives the outcome that replaces it.
            var rows = CosmosAxiomRows.Read(CosmosAxioms.All).ToList();
            var registry = rows.Select(row => (row.Key, row.Target)).ToHashSet();
            void Add(MeasuredOutcome row) =>
                rows.Add(registry.Contains((row.Key, row.Target)) ? throw new System.InvalidOperationException($"The registry answers {row.Key} on {row.Target}: drop the probe's row.") : row);

            void Service(string id, CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell, CosmosAnswer answer, params RenderingTarget[] targets)
            {
                foreach (var target in targets)
                    Add(new MeasuredOutcome("Service." + id, MeasuredKey.Of(test, operand, cell), target, answer));
            }

            // On Azure, what the emulator alone has measured over false.
            Service("IsPrimitive", CosmosServiceTest.IsPrimitive, None, False, CosmosAnswer.True, RenderingTarget.Azure);
            Service("IsNull", CosmosServiceTest.IsNull, None, False, CosmosAnswer.False, RenderingTarget.Azure);
            Service("IsDefined", CosmosServiceTest.IsDefined, None, False, CosmosAnswer.True, RenderingTarget.Azure);
            Service("EqualityAcrossTypesIsFalse", CosmosServiceTest.Equal, String, False, CosmosAnswer.False, RenderingTarget.Azure);
            Service("InequalityOverNullIsTrue", CosmosServiceTest.NotEqual, String, False, CosmosAnswer.True, RenderingTarget.Azure);

            // On Azure, a string either side of the literal under = and !=, which the emulator alone has measured; and code
            // point standing in for the emulator's comparator, which no answer names (it orders U+0000 as it stores it).
            foreach (var side in new[] { Below, Above })
            {
                Service("ComparisonWithinAType", CosmosServiceTest.Equal, String, side, CosmosAnswer.False, RenderingTarget.Azure);
                Service("ComparisonWithinAType", CosmosServiceTest.NotEqual, String, side, CosmosAnswer.True, RenderingTarget.Azure);
            }

            Service("StringOrderIsCodePoint", CosmosServiceTest.Order, String, None, CosmosAnswer.CodePoint, RenderingTarget.Emulator);

            // Azure's connectives, which the emulator alone has measured, and Calcite's, which no outcome holds: Kleene's
            // expectations written as rows, not measurements. A range stays as measured.
            foreach (var (target, key, answer) in KleeneTables.Rows.Where(row => row.Item1 == RenderingTarget.Calcite
                || row.Item1 == RenderingTarget.Azure && row.Item2.Test != CosmosServiceTest.Range && (row.Item2.Test != CosmosServiceTest.Not || row.Item2.Cell == Undefined)))
                Add(new MeasuredOutcome(target == RenderingTarget.Calcite ? "Calcite.ConnectivesAreKleene" : "Service.ConnectivesAreKleene", key, target, answer));

            return rows;
        }

    }

}
