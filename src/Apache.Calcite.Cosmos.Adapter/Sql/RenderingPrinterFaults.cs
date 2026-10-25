using System;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The switch the printer's planted faults read (O4): inert unless a test plants one, which production never does. A
    /// site reads <see cref="Armed"/> alone until some flow has planted; then the flow's own faults, so a plant reaches no
    /// other flow, as <see cref="CosmosTranslatorFaults"/>.
    /// </summary>
    internal static class RenderingPrinterFaults
    {

        static readonly AsyncLocal<RenderingPrinterFault> Current = new();

        /// <summary>Whether any flow has planted a fault.</summary>
        internal static volatile bool Armed;

        /// <summary>Whether this flow planted the fault.</summary>
        internal static bool Has(RenderingPrinterFault fault) => Armed && (Current.Value & fault) != 0;

        /// <summary>Plants the faults in this flow until disposed, which puts back what was planted before.</summary>
        internal static IDisposable Plant(RenderingPrinterFault faults)
        {
            var planting = new Planting(Current.Value);
            Current.Value = faults;
            Armed = true;
            return planting;
        }

        sealed class Planting(RenderingPrinterFault was) : IDisposable
        {

            public void Dispose() => Current.Value = was;

        }

    }

}
