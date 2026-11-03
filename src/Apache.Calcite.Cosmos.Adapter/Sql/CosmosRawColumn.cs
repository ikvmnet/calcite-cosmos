using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// One of the three columns the IR's raw reading is read through — a promoted path's <c>VARIANT</c>, an <c>ANY</c>, a
    /// field typed <c>VARCHAR</c> — and what it makes of a stored value of each kind, as <c>Calcite.RawNullIsSqlNull</c>
    /// measured it in process: whether it is raw beside a string literal alone; what it reads a value of each kind as;
    /// whether it orders within a type, since without an ordering any ordering raises; and what its cast to text makes of
    /// each kind. Each reads a stored null and an absent path as SQL null; across types an equality is unequal and an
    /// ordering raises. <see cref="CosmosAxiomRows.ReadColumns"/> reads it into S, whose rule takes the union.
    /// </summary>
    internal sealed record CosmosRawColumn(string Name, bool BesideStringsAlone, IReadOnlyDictionary<string, CosmosRawColumn.Answer> Read, bool Orders,
        IReadOnlyDictionary<string, CosmosRawColumn.Answer> AsText)
    {

        /// <summary>What a column makes of a stored value: the value itself, compared as its type or rendered; SQL null; or a raise.</summary>
        public enum Answer { Itself, Null, Raises }

        /// <summary>The kinds of stored value, a null apart, that a column answers for apart.</summary>
        public const string String = "string", Number = "number", Boolean = "boolean", Structure = "structure";

        /// <summary>One answer a kind, as a row states them.</summary>
        public static Dictionary<string, Answer> Kinds(Answer text, Answer number, Answer boolean, Answer structure) =>
            new() { [String] = text, [Number] = number, [Boolean] = boolean, [Structure] = structure };

    }

}
