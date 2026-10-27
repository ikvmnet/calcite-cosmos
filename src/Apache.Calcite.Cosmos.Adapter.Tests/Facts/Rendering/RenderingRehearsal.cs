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

        /// <summary>Whether the rehearsal armed the process: where the digests' run fails on a decline.</summary>
        public static bool Armed { get; private set; }

        [ModuleInitializer]
        internal static void Arm()
        {
            var value = Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant();
            if (value is not ("1" or "true" or "production" or "emulator"))
                return;

            // The shared reasoning's S build started as the assembly loads, as the switch starts it at the first table, and
            // awaited: a plan whose question waited past its bound would decline, and change its digest for a reason no
            // shadow row lists.
            CosmosRenderingShadow.ArmEverywhere(value switch { "production" => RenderingSet.Production, "emulator" => RenderingSet.Emulator, _ => null });
            TestReasoning.Shared.Rendering.Ready.Wait();
            Armed = true;
        }

    }

}
