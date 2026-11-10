using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The name the function rows file a call of an operator at a SQL type under, read off a definition's names by the tests
    /// that read the rows apart from S: <see cref="CosmosEval"/>'s service answer, independent of S by design. S keys a call
    /// by its own rules (<c>callkey</c>, G6), which <c>RenderingValuedTests.SKeysACallByTheNameItsRowsGiveIt</c> holds to
    /// these names; production reads no copy of them.
    /// </summary>
    internal static class RowNames
    {

        /// <summary>
        /// The operator's name at the type where the rows name one, its own where they name it alone, <c>null</c> where they
        /// name neither: the rows never name one operator both ways, which S's key, a function, would refuse.
        /// </summary>
        public static string? KeyOf(RenderingDefinition definition, string op, string? type) =>
            definition.FunctionNames.FirstOrDefault(n => n.Operator == op && n.Type is not null && n.Type == type).Name
            ?? definition.FunctionNames.FirstOrDefault(n => n.Operator == op && n.Type is null).Name;

    }

}
