using System;
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
    /// An operator is strict where Calcite declares <c>Strong.Policy.ANY</c> of it or the row measured it, and by the name
    /// the loader reads its call by (<see cref="CosmosPredicateLoader.Spellings"/>), so a name is strict only where every
    /// operator it may stand for — standard, any library's, the adapter's, the geography functions' — is. A call nothing
    /// names is strict in nothing, which drops a presence test from a weakening and no rows.
    /// </remarks>
    internal static class CosmosStrictness
    {

        /// <summary>The row of the functions measured null over a null operand.</summary>
        public const string Functions = "Calcite.ListedFunctionsAreNullOverANullOperand";

        /// <summary>The row of the readings measured null over an absent path.</summary>
        public const string Readings = "Calcite.ReadingsAreNullOverAnAbsentPath";

        /// <summary>Every name, in the IR's spelling, whose every operator Calcite declares or was measured null over a null operand.</summary>
        public static IReadOnlyList<string> Operators { get; } = Strict(CosmosAxioms.Get(Functions).Subjects!);

        /// <summary>Every reader, in <c>PredicateAtom</c>'s spelling, measured null over an absent path.</summary>
        public static IReadOnlyList<string> Readers { get; } = CosmosAxioms.Get(Readings).Subjects!;

        static IReadOnlyList<string> Strict(IReadOnlyList<string> measured)
        {
            var tables = new[]
            {
                SqlStdOperatorTable.instance(),
                SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(java.util.EnumSet.allOf(java.lang.Class.forName("org.apache.calcite.sql.fun.SqlLibrary"))),
                CosmosOperators.Instance,
                Geography.Sql.GeographyOperatorTable.Instance(),
            };

            var spelled = from table in tables
                          let list = table.getOperatorList()
                          from i in Enumerable.Range(0, list.size())
                          let op = (SqlOperator)list.get(i)
                          from name in CosmosPredicateLoader.Spellings(op)
                          select (Name: name, Strict: Strong.policy(op) == Strong.Policy.ANY || measured.Contains(op.getName()));

            return [.. spelled.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.All(s => s.Strict))
                .SelectMany(g => g.Select(s => s.Name)).Distinct().Order(StringComparer.Ordinal)];
        }

    }

}
