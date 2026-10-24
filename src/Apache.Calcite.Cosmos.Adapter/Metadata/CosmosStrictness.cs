using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What Calcite answers null for where an operand, or the path a reading reads, is null: the one reader of Calcite's
    /// declared null policy and the two strictness rows into the rendering graph's <c>strictop</c> and <c>strictreader</c>
    /// rows (<c>RenderingDefinition.StrictOperators</c>, <c>StrictReaders</c>).
    /// </summary>
    /// <remarks>
    /// One row an operator, under each name the loader reads its call by (<see cref="CosmosPredicateLoader.Spellings"/>):
    /// strict where Calcite declares <c>Strong.Policy.ANY</c> of it or the row measured its name, and not otherwise. The
    /// operators are the standard table's, every library's, the adapter's and the geography functions'. Nothing is combined
    /// here: the IR names a call by its name alone, and the theory's lattice meets the rows of one name, so a name is strict
    /// only where every operator it may stand for is. A call nothing names is strict in nothing, which drops a presence
    /// test from a weakening and no rows.
    /// </remarks>
    internal static class CosmosStrictness
    {

        /// <summary>The row of the functions measured null over a null operand.</summary>
        public const string Functions = "Calcite.ListedFunctionsAreNullOverANullOperand";

        /// <summary>The row of the readings measured null over an absent path.</summary>
        public const string Readings = "Calcite.ReadingsAreNullOverAnAbsentPath";

        /// <summary>Each operator, by every name in the IR's spelling it is read by, and whether Calcite declares or was measured null over a null operand.</summary>
        public static IReadOnlyList<(string Name, bool Strict)> Operators { get; } = Read(CosmosAxioms.Get(Functions).Subjects!);

        /// <summary>Every reader, in <c>PredicateAtom</c>'s spelling, measured null over an absent path.</summary>
        public static IReadOnlyList<string> Readers { get; } = CosmosAxioms.Get(Readings).Subjects!;

        static IReadOnlyList<(string Name, bool Strict)> Read(IReadOnlyList<string> measured)
        {
            var tables = new[]
            {
                SqlStdOperatorTable.instance(),
                SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(java.util.EnumSet.allOf(java.lang.Class.forName("org.apache.calcite.sql.fun.SqlLibrary"))),
                CosmosOperators.Instance,
                Geography.Sql.GeographyOperatorTable.Instance(),
            };

            return [.. (from table in tables
                        let list = table.getOperatorList()
                        from i in Enumerable.Range(0, list.size())
                        let op = (SqlOperator)list.get(i)
                        from name in CosmosPredicateLoader.Spellings(op)
                        select (name, Strong.policy(op) == Strong.Policy.ANY || measured.Contains(op.getName()))).Distinct()];
        }

    }

}
