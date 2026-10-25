using System;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A fault planted in <see cref="RenderingPrinter"/> by <see cref="RenderingPrinterFaults"/>, each one a way a writer of
    /// S's forms could send other than what S chose, which the round trip with the reader must catch (spec g13, line 17).
    /// </summary>
    [Flags]
    internal enum RenderingPrinterFault
    {

        None = 0,

        /// <summary>An <c>OR</c> that is a member of an <c>AND</c> written without its parentheses, so <c>AND</c> takes its first member.</summary>
        M12 = 1 << 0,

        /// <summary>A junction's last member left out.</summary>
        M13 = 1 << 1,

        /// <summary>A <c>NOT</c> over a comparison written without the comparison's parentheses, so it negates the path.</summary>
        M14 = 1 << 2,

        /// <summary>Parameters bound out of order: each comparison of a junction, and each point of an <c>IN</c>, named with its neighbour's value.</summary>
        M15 = 1 << 3,

        /// <summary>A property name spelled wrongly: its first letter in upper case, which the service, case-sensitive, reads as another property.</summary>
        M16 = 1 << 4,

    }

}
