using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What production's rendering graph S is defined from: the registry's rows, strict where Calcite answers null for a
    /// null as its one reader of that says, read once a process, being facts fixed in the assembly; and the set a
    /// container's statements are proven under. Each schema's oracle is its reasoning's
    /// (<see cref="CosmosReasoning.Rendering"/>). Nothing in planning asks one yet.
    /// </summary>
    internal static class CosmosRendering
    {

        static readonly System.Lazy<RenderingDefinition> Defined = new(() => RenderingDefinition.Measured(CosmosAxiomRows.Read(CosmosAxioms.All)) with
        {
            StrictOperators = CosmosStrictness.Operators,
            StrictReaders = CosmosStrictness.Readers,
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

    }

}
