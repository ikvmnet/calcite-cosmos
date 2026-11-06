using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What production's rendering graph S is defined from: the registry's rows, strict where Calcite answers null for a
    /// null as its one reader of that says, read once a process, being facts fixed in the assembly; the set a container's
    /// statements are proven under; and the one path every site asks S through. Each schema's oracle is its reasoning's
    /// (<see cref="CosmosReasoning.Rendering"/>), which every filter a plan pushes asks: admission, the split's weakening,
    /// and implementation's print (<c>DESIGN.md</c>, <i>The switch</i>).
    /// </summary>
    internal static class CosmosRendering
    {

        // The guarded comparison's templates are asked from the switch (spec §4.2 h15), a bridge: until site W asks whether
        // S owns the filter beneath, the translator writes no CASE they are pushed as, and a weakening holding one is lost.
        static readonly System.Lazy<RenderingDefinition> Defined = new(() => RenderingDefinition.Measured(CosmosAxiomRows.Read(CosmosAxioms.All)) with
        {
            StrictOperators = CosmosStrictness.Operators,
            StrictReaders = CosmosStrictness.Readers,
            Functions = CosmosAxiomRows.ReadFunctions(CosmosAxioms.All),
            FunctionReads = CosmosAxiomRows.ReadFunctionReads(CosmosAxioms.All),
            FunctionSubjects = CosmosAxiomRows.ReadFunctionSubjects(CosmosAxioms.All),
            Columns = CosmosAxiomRows.ReadColumns(CosmosAxioms.All),
            Parts = CosmosAxiomRows.ReadParts(CosmosAxioms.All),
            Moves = CosmosAxiomRows.ReadMoves(CosmosAxioms.All),
            StoredAs = CosmosAxiomRows.ReadStoredOn(CosmosAxioms.All),
            Truncations = CosmosAxiomRows.ReadTruncations(CosmosAxioms.All),
            PairReadings = CosmosAxiomRows.ReadPairReadings(CosmosAxioms.All),
            Boxes = CosmosAxiomRows.ReadBoxes(CosmosAxioms.All),
            SeesThrough = CosmosAxiomRows.ReadSeesThrough(CosmosAxioms.All),
            StructureOnly = CosmosAxiomRows.ReadStructureOnly(CosmosAxioms.All),
            CalciteReadings = CosmosAxiomRows.ReadCalciteReadings(CosmosAxioms.All),
            CalciteBands = CosmosAxiomRows.ReadCalciteBands(CosmosAxioms.All),
            Casts = CosmosAxiomRows.ReadCasts(CosmosAxioms.All),
            ReaderOrders = CosmosAxiomRows.ReadReaderOrders(CosmosAxioms.All),
            TestKinds = CosmosAxiomRows.ReadTestKinds(CosmosAxioms.All),
            Templates = [.. RenderingTemplateRows.Templates.Where(t => RenderingTemplateRows.FromTheSwitch.Contains(t.Item1) == false)],
        });

        /// <summary>The registry's S, read on first use, the first build's thread in production: what every schema's generations, and the tests, are defined from.</summary>
        public static RenderingDefinition Definition => Defined.Value;

        /// <summary>
        /// What a generation's warm-up answer prints as, printed on its build's thread so that a plan pays neither S's first
        /// question nor the printer's: its best form in the translator's spelling, or none where S gave none. Each schema's
        /// reasoning keeps what its own last generation printed (<see cref="CosmosReasoning.Warmed"/>).
        /// </summary>
        internal static string? Print(SqlPredicate question, RenderingAnswer answer) => answer.Best is { } best
            ? RenderingPrinter.Print(best, question.Paths.Distinct().ToDictionary(SqlValue.Spell), CosmosImplementor.DefaultRootAlias, new CosmosParameterList()) : null;

        /// <summary>The set a statement is proven under: what Azure was measured to do, or the emulator (O1).</summary>
        public static RenderingSet SetFor(CosmosServiceTarget target) => target == CosmosServiceTarget.Emulator ? RenderingSet.Emulator : RenderingSet.Production;

        /// <summary>
        /// Whether a set holds a target that stores a string in another spelling than it is sent, the emulator's
        /// (<see cref="EmulatorStoredText"/>): read off each target's measured string order, the row S reads
        /// (<c>Service.StringOrderIsCodePoint</c>), answering <see cref="CosmosAnswer.StoredCodePoint"/>. Such a set's
        /// statements send a compared value in the stored spelling, and decline what was not measured in it. A constant of
        /// the registry, read once, not learned state.
        /// </summary>
        public static bool StoresSpelled(RenderingSet set) => Spelled.Value.Contains(set);

        /// <summary>Whether a target's statements are written for a stored spelling: <see cref="StoresSpelled(RenderingSet)"/> of its set.</summary>
        public static bool StoresSpelled(CosmosServiceTarget target) => StoresSpelled(SetFor(target));

        static readonly System.Lazy<HashSet<RenderingSet>> Spelled = new(() =>
        {
            var definition = Definition;
            var order = MeasuredKey.Of(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None);
            return [.. definition.Serves.Where(s => definition.Rows.Any(r => r.Key == order && r.Answer == CosmosAnswer.StoredCodePoint && s.Value.Contains(r.Target))).Select(s => s.Key)];
        });

        /// <summary>
        /// Asks S one question through an oracle, the statement's schema's or a test's (<see cref="RenderingOracle.Ask"/>):
        /// the one bundle code path every site takes, and where the flow is measured (<see cref="CosmosRenderingCost"/>), what
        /// it cost: the oracle's count and time, the candidates proposed, counted without reading a row, the bytes the thread
        /// allocated, and the graph's use.
        /// </summary>
        internal static RenderingOracle.RenderingBundleAnswer Ask(RenderingOracle oracle, RenderingSet set, IReadOnlyList<SqlPredicate> roots,
            IReadOnlyDictionary<long, SqlValue>? opaques, ImportSource source, Eggnog.Scope? root, PredicateRules? rules,
            IReadOnlyList<(SqlPredicate Atom, ImportSource Source, Eggnog.Scope? Node, PredicateRules? Rules)> beneath,
            Action<RenderingBundle, IReadOnlyList<Pred>, ImportReport, RenderingRun, TimeSpan>? opened)
        {
            if (CosmosRenderingCost.Measuring == false)
                return oracle.Ask(set, roots, opaques, source, root, rules, beneath, opened);

            var (bytes, before, candidates, clock) = (System.GC.GetAllocatedBytesForCurrentThread(), oracle.Usage, 0L, CosmosRenderingCost.ThreadClock);
            var started = clock?.Invoke();
            var answer = oracle.Ask(set, roots, opaques, source, root, rules, beneath, (bundle, terms, import, run, elapsed) =>
            {
                candidates = bundle.Failure is null ? bundle.Scope.CountRows(bundle.Theory.Candidate) : 0;
                opened?.Invoke(bundle, terms, import, run, elapsed);
            });
            CosmosRenderingCost.Add(new(roots, answer.Cost, candidates, System.GC.GetAllocatedBytesForCurrentThread() - bytes, before, oracle.Usage, clock?.Invoke() - started));
            return answer;
        }

        /// <summary>An answer a statement of the same terms was given for the same question, handed again: a hit, counted where the flow is measured.</summary>
        internal static RenderingOracle.RenderingBundleAnswer Reused(RenderingOracle oracle, IReadOnlyList<SqlPredicate> roots, RenderingOracle.RenderingBundleAnswer answer, TimeSpan elapsed)
        {
            answer = answer with { Cost = RenderingCost.None with { Hits = 1, Elapsed = elapsed } };
            if (CosmosRenderingCost.Measuring)
                CosmosRenderingCost.Add(new(roots, answer.Cost, 0, 0, oracle.Usage, oracle.Usage));

            return answer;
        }

    }

}
