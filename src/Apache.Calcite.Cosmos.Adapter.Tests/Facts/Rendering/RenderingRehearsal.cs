using System;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The whole-suite rehearsal of the switch (spec g15): where <c>COSMOS_RENDERING_WRITES</c> is set before the test
    /// assembly loads, every flow takes S's answers as g16 will — <c>1</c> under each container's own set, <c>production</c>
    /// or <c>emulator</c> under that set for every statement. Unset, nothing is armed.
    /// </summary>
    static class RenderingRehearsal
    {

        /// <summary>The variable that arms the rehearsal.</summary>
        public const string Variable = "COSMOS_RENDERING_WRITES";

        [ModuleInitializer]
        internal static void Arm()
        {
            RenderingSet? set;
            switch (Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant())
            {
                case "1" or "true":
                    set = null;
                    break;
                case "production":
                    set = RenderingSet.Production;
                    break;
                case "emulator":
                    set = RenderingSet.Emulator;
                    break;
                default:
                    return;
            }

            // The shared reasoning's S build started as the assembly loads, as the switch starts it at the first table, so
            // that no plan's question waits on it past its bound and declines.
            CosmosRenderingShadow.ArmEverywhere(set);
            TestReasoning.Shared.Rendering.Warm();
        }

    }

}
