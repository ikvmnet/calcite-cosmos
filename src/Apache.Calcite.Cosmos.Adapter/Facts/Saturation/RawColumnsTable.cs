using System.Collections.Generic;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The three columns the IR's raw reading is read through — a promoted path's <c>VARIANT</c>, an <c>ANY</c>, a field
    /// typed <c>VARCHAR</c> — and what each makes of a stored value of each kind, as host data: measured in process
    /// (<c>Calcite.RawNullIsSqlNull</c>, <c>Calcite.CastOfARawValueToText</c>), looked up by the raw cells, and asserted by
    /// the rows' pinning tests, so that each fact is stated once. Each reads a stored null and an absent path as SQL null;
    /// across types an equality is unequal and an ordering raises.
    /// </summary>
    internal static class RawColumnsTable
    {

        /// <summary>What a column makes of a stored value: the value itself, compared as its type or rendered; SQL null; or a raise.</summary>
        public enum Answer { Itself, Null, Raises }

        /// <summary>The kinds of stored value, a null apart, that a column answers for apart.</summary>
        public const string String = "string", Number = "number", Boolean = "boolean", Structure = "structure";

        /// <summary>
        /// One column: whether it is raw beside a string literal alone; what it reads a value of each kind as; whether it
        /// orders within a type, since without an ordering any ordering raises; and what its cast to text makes of each kind.
        /// </summary>
        public sealed record Column(string Name, bool BesideStringsAlone, IReadOnlyDictionary<string, Answer> Read, bool Orders, IReadOnlyDictionary<string, Answer> AsText);

        /// <summary>The columns, as measured.</summary>
        public static IReadOnlyList<Column> Rows { get; } =
        [
            // No ordering, the statement failing to compile; a number equal to one boxed with its own type alone; cast to
            // text, a number or a boolean is null and a structure raises.
            new("VARIANT", false, Kinds(Answer.Itself, Answer.Itself, Answer.Itself, Answer.Itself), false, Kinds(Answer.Itself, Answer.Null, Answer.Null, Answer.Raises)),
            // Ordered as the type, a number in its box; cast to text, each value as Java renders the box.
            new("ANY", false, Kinds(Answer.Itself, Answer.Itself, Answer.Itself, Answer.Itself), true, Kinds(Answer.Itself, Answer.Itself, Answer.Itself, Answer.Itself)),
            // A string alone, the statement failing over anything else.
            new("VARCHAR", true, Kinds(Answer.Itself, Answer.Raises, Answer.Raises, Answer.Raises), true, Kinds(Answer.Itself, Answer.Raises, Answer.Raises, Answer.Raises)),
        ];

        static Dictionary<string, Answer> Kinds(Answer text, Answer number, Answer boolean, Answer structure) =>
            new() { [String] = text, [Number] = number, [Boolean] = boolean, [Structure] = structure };

        /// <summary>The kind of a value in <see cref="JsonValues"/>' spelling, a null's none.</summary>
        public static string? KindOf(string value) => TypeOf(value) switch
        {
            StringType => String,
            IntegerType or FractionType => Number,
            BooleanType => Boolean,
            ObjectType or ArrayType => Structure,
            _ => null,
        };

        /// <summary>The kind of every value a cell holds; none for an absent path, a null, or a cell nothing is known of.</summary>
        public static string? KindOf(PathCell cell) => cell switch
        {
            PathCell.Point { Value: var v } => KindOf(v),
            PathCell.Strings => String,
            PathCell.Numbers => Number,
            PathCell.Structure => Structure,
            _ => null,
        };

    }

}
