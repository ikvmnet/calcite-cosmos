using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using org.apache.calcite.sql.type;

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
            Functions = CosmosAxiomRows.ReadFunctions(FunctionOutcomes),
            FunctionReads = CosmosAxiomRows.ReadFunctionReads(CosmosAxioms.All),
            FunctionSubjects = CosmosAxiomRows.ReadFunctionSubjects(FunctionOutcomes),
            FunctionNames = CosmosAxiomRows.ReadFunctionNames(FunctionOutcomes),
            FunctionYields = CosmosAxiomRows.ReadFunctionYields(FunctionOutcomes),
            CallSpellings = Sql.CosmosFunctionSpellings.Spellings,
            TypeKinds = ReadTypeKinds(),
            Columns = CosmosAxiomRows.ReadColumns(CosmosAxioms.All),
            Parts = CosmosAxiomRows.ReadParts(CosmosAxioms.All),
            Moves = CosmosAxiomRows.ReadMoves(CosmosAxioms.All),
            StoredAs = CosmosAxiomRows.ReadStoredOn(CosmosAxioms.All),
            Truncations = CosmosAxiomRows.ReadTruncations(CosmosAxioms.All),
            Folds = CosmosAxiomRows.ReadFolds(CosmosAxioms.All),
            Rewrites = CosmosAxiomRows.ReadRewrites(CosmosAxioms.All),
            Unsent = CosmosAxiomRows.ReadUnsent(CosmosAxioms.All),
            PairReadings = CosmosAxiomRows.ReadPairReadings(CosmosAxioms.All),
            WholeReadings = CosmosAxiomRows.ReadWholeReadings(CosmosAxioms.All),
            Boxes = CosmosAxiomRows.ReadBoxes(CosmosAxioms.All),
            SeesThrough = CosmosAxiomRows.ReadSeesThrough(CosmosAxioms.All),
            StructureOnly = CosmosAxiomRows.ReadStructureOnly(CosmosAxioms.All),
            CalciteReadings = CosmosAxiomRows.ReadCalciteReadings(CosmosAxioms.All),
            CalciteBands = CosmosAxiomRows.ReadCalciteBands(CosmosAxioms.All),
            Casts = CosmosAxiomRows.ReadCasts(CosmosAxioms.All),
            Comparands = CosmosAxiomRows.ReadComparands(CosmosAxioms.All),
            ReaderOrders = CosmosAxiomRows.ReadReaderOrders(CosmosAxioms.All),
            ReaderKinds = ReadReaderKinds(),
            TestKinds = CosmosAxiomRows.ReadTestKinds(CosmosAxioms.All),
            Templates = [.. RenderingTemplateRows.Templates.Where(t => RenderingTemplateRows.FromTheSwitch.Contains(t.Item1) == false)],
        });

        /// <summary>The function rows S reads: the axioms', and those declared beside them (<see cref="CosmosAxiomRows.Declared"/>, G6).</summary>
        static IEnumerable<CosmosFunctionOutcome> FunctionOutcomes => [.. CosmosAxiomRows.OutcomesOf(CosmosAxioms.All), .. CosmosAxiomRows.Declared];

        /// <summary>
        /// The kinds of value each SQL type a computed place may be of holds (G6): a character type a string, an integer
        /// type a whole number, any other numeric type any number, a boolean type a boolean, by the families Calcite's type
        /// system names, and <c>ANY</c>, the type of this adapter's own operators over a document's value, every kind there is.
        /// Read off it once, by the definition, a constant of it; a type none names holds no kind S images.
        /// </summary>
        internal static IReadOnlyList<(string Type, string Kind)> ReadTypeKinds() =>
            [.. from type in SqlTypeName.ALL_TYPES.toArray().Cast<SqlTypeName>().Append(SqlTypeName.ANY).Distinct()
                from kind in type == SqlTypeName.ANY ? [.. JsonValues.AllTypes.AsEnumerable()]
                    : SqlTypeName.CHAR_TYPES.contains(type) ? [JsonValues.StringType]
                    : SqlTypeName.INT_TYPES.contains(type) ? [JsonValues.IntegerType]
                    : SqlTypeName.NUMERIC_TYPES.contains(type) ? [.. JsonValues.Numbers.AsEnumerable()]
                    : SqlTypeName.BOOLEAN_TYPES.contains(type) ? new[] { JsonValues.BooleanType }
                    : []
                orderby type.getName(), kind
                select (type.getName(), kind)];

        /// <summary>
        /// The kind each reading S cuts compares as (<see cref="ReaderEdgesTable"/>, G3): the family Calcite's type system
        /// names the type the reading is of — the accessor's text <c>VARCHAR</c> (<c>Calcite.JsonValueRendersScalarsAsText</c>),
        /// a typed reading's its type, a cast's its target. A raw value is of no type, and has none. Read off Calcite once, by
        /// the definition, a constant of its type system.
        /// </summary>
        internal static IReadOnlyList<(string Reader, string Kind)> ReadReaderKinds() =>
            [.. from reader in ReaderEdgesTable.Rows.Keys
                let type = TypeOf(PredicateAtom.Outermost(reader))
                where type is not null
                let family = SqlTypeName.get(type)?.getFamily()
                where family is not null
                orderby reader
                select (reader, family.name())];

        /// <summary>The SQL type a reading is of, by its name: the text's <c>VARCHAR</c>, <c>typed:T</c>'s and <c>cast:T</c>'s <c>T</c>; none for a raw value.</summary>
        static string? TypeOf(string outermost) => outermost == PredicateAtom.TextReader ? "VARCHAR"
            : outermost.StartsWith("typed:", StringComparison.Ordinal) ? outermost.Substring("typed:".Length)
            : outermost.StartsWith("cast:", StringComparison.Ordinal) ? outermost.Substring("cast:".Length)
            : null;

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
        /// Whether S derived that a set's statements send a string in the spelling its target stores, the emulator's
        /// (<see cref="EmulatorStoredText"/>, <see cref="RenderingTheory.Sends"/>): from the set's string order and the rows of
        /// what its comparators' spelling rewrites (<c>Service.StringOrderIsCodePoint</c>,
        /// <c>Service.EmulatorComparesTheSpellingItIsSent</c>, G1). Such a set's statements send a compared value in the
        /// stored spelling, and decline what was not measured in it. Read once off S built from the registry's definition, a
        /// constant of the registry, not learned state.
        /// </summary>
        public static bool StoresSpelled(RenderingSet set) => Spelled.Value.Contains(set);

        /// <summary>Whether a target's statements are written for a stored spelling: <see cref="StoresSpelled(RenderingSet)"/> of its set.</summary>
        public static bool StoresSpelled(CosmosServiceTarget target) => StoresSpelled(SetFor(target));

        static readonly System.Lazy<HashSet<RenderingSet>> Spelled = new(() =>
        {
            var theory = RenderingTheory.Define(Definition);
            HashSet<RenderingSet> spelled = [.. Definition.Serves.Keys.Where(set => theory.SpellingOf(set) == SentSpelling.Stored)];
            GC.KeepAlive(theory);
            return spelled;
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

        /// <summary>
        /// Asks S the image of a call the translator's column bridge writes (<see cref="RenderingOracle.ImageOf"/>), measured and
        /// counted as every bridge question is (<see cref="Bridged"/>).
        /// </summary>
        /// <param name="countAs">The container a decline is counted under; none for a translator built without one.</param>
        internal static RenderingBridgeAnswer ImageOf(RenderingOracle oracle, RenderingSet set, SqlValue.Call call, Func<JsonDocumentPath, (PathValues Values, Eggnog.ValueSet<string> Chars)> read,
            Func<SqlValue.Call, Eggnog.ValueSet<string>> chars, string? countAs) =>
            Bridged(oracle, new SqlPredicate.IsDefined(call), () => oracle.ImageOf(set, call, read, chars), countAs);

        /// <summary>
        /// Asks S the case-insensitive affix a <c>LIKE</c> over a fold the translator writes answers as
        /// (<see cref="RenderingOracle.FoldOf"/>), measured and counted as every bridge question is (<see cref="Bridged"/>).
        /// </summary>
        /// <param name="countAs">The container a decline is counted under; none for a translator built without one.</param>
        internal static RenderingBridgeAnswer FoldOf(RenderingOracle oracle, RenderingSet set, SqlPredicate.Affix like, Func<JsonDocumentPath, (PathValues Values, Eggnog.ValueSet<string> Chars)> read,
            Func<SqlValue.Call, Eggnog.ValueSet<string>> chars, string? countAs) =>
            Bridged(oracle, like, () => oracle.FoldOf(set, like, read, chars), countAs);

        /// <summary>
        /// One question of the translator's bridge, measured as <see cref="Ask"/> measures a bundle where the flow is (a bridge
        /// sample, <see cref="RenderingCostSample.Bridge"/>), and a decline counted under the container on
        /// <see cref="Client.CosmosInstrumentation.RenderingDeclines"/>, since what S declines changes what is sent as a declined
        /// conjunct does: a call computed in process, a <c>LIKE</c> over a fold not rewritten.
        /// </summary>
        static RenderingBridgeAnswer Bridged(RenderingOracle oracle, SqlPredicate root, Func<RenderingBridgeAnswer> ask, string? countAs)
        {
            RenderingBridgeAnswer answer;
            if (CosmosRenderingCost.Measuring == false)
                answer = ask();
            else
            {
                var (bytes, before, clock) = (System.GC.GetAllocatedBytesForCurrentThread(), oracle.Usage, CosmosRenderingCost.ThreadClock);
                var started = clock?.Invoke();
                answer = ask();
                CosmosRenderingCost.Add(new([root], answer.Cost, 0, System.GC.GetAllocatedBytesForCurrentThread() - bytes, before, oracle.Usage, clock?.Invoke() - started, Bridge: true));
            }

            if (countAs is not null && answer.Declined is { } reason)
                Client.CosmosInstrumentation.RenderingDeclines.Add(1, new KeyValuePair<string, object?>("cosmos.container", countAs), new KeyValuePair<string, object?>("cosmos.rendering.reason", reason));

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
