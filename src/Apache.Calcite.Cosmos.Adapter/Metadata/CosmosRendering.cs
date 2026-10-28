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

        static readonly System.Lazy<RenderingDefinition> Defined = new(() => RenderingDefinition.Measured(CosmosAxiomRows.Read(CosmosAxioms.All)) with
        {
            StrictOperators = CosmosStrictness.Operators,
            StrictReaders = CosmosStrictness.Readers,
        });

        static readonly System.Threading.AsyncLocal<bool> Unasked = new();

        /// <summary>Whether any flow has turned S off; once one has, a site also reads the flow's.</summary>
        static volatile bool _offAnywhere;

        /// <summary>
        /// Whether a test turned S off for this flow: the translator writes every conjunct, nothing is weakened, and no plan
        /// starts a build — the pairing lines 29 and 29b read, and a sample of the translator's own text. Production never
        /// does.
        /// </summary>
        internal static bool Off => _offAnywhere && Unasked.Value;

        /// <summary>Turns S off for the flow until disposed (<see cref="Off"/>).</summary>
        internal static IDisposable TurnOff()
        {
            var restore = new Restore(Unasked.Value);
            (Unasked.Value, _offAnywhere) = (true, true);
            return restore;
        }

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

        /// <summary>Puts the flow's S back as <see cref="TurnOff"/> found it.</summary>
        sealed class Restore(bool was) : IDisposable
        {

            public void Dispose() => Unasked.Value = was;

        }

    }

}
