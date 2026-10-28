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

            // The arm is a switch and holds no answer. The schema the suite's fixtures plan in has its S build awaited here,
            // before anything plans, and each harness schema awaits its own before its first plan (HarnessReasonings): a
            // question that waited past its bound would decline, and change a plan for a reason no shadow row lists.
            CosmosRenderingShadow.ArmEverywhere(value switch { "production" => RenderingSet.Production, "emulator" => RenderingSet.Emulator, _ => null });
            TestReasoning.Shared.Rendering.Ready.Wait();
            Armed = true;
        }

    }

}
