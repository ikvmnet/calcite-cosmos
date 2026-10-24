using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The process's rendering graph S as production asks it: one oracle over the registry's rows, strict where Calcite
    /// answers null for a null as its one reader of that says, and the set a container's statements are proven under.
    /// Nothing in planning asks it yet.
    /// </summary>
    internal static class CosmosRendering
    {

        /// <summary>The process's oracle; its graph is defined on a thread of its own once it is first warmed or asked.</summary>
        public static RenderingOracle Oracle { get; } = new(new RenderingGraph(() => RenderingDefinition.Measured(CosmosAxiomRows.Read(CosmosAxioms.All)) with
        {
            StrictOperators = CosmosStrictness.Operators,
            StrictReaders = CosmosStrictness.Readers,
        }));

        /// <summary>The set a statement is proven under: what Azure was measured to do, or the emulator (O1).</summary>
        public static RenderingSet SetFor(CosmosServiceTarget target) => target == CosmosServiceTarget.Emulator ? RenderingSet.Emulator : RenderingSet.Production;

        /// <summary>Starts S's build on its own thread, once, and returns at once.</summary>
        public static void Warm() => Oracle.Warm();

    }

}
