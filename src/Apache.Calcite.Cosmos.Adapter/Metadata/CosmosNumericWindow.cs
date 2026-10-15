using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What a comparison through a numeric cast says of the raw value it converts: that the value, where
    /// it is a number, lies above one number and below another, either of which may be absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is implied.</b> <c>CAST(JSON_VALUE(c."DOC", '$.price') AS INTEGER) &gt; 10</c> has no Cosmos
    /// form — Calcite converts the stored value and the service compares it as it stands — but converting
    /// a number to a number moves it by less than one, whichever way the conversion rounds. So a document
    /// whose converted value is greater than 10 holds a raw value greater than 9, and one whose converted
    /// value equals 30 holds one strictly between 29 and 31. The window is loose on purpose: the exact one
    /// depends on which way the conversion rounds and on the sign, and none of that has to be decided to
    /// make it sound.
    /// </para>
    /// <para>
    /// <b>A number, and only a number.</b> Calcite converts a stored <em>string</em> too — measured,
    /// <c>= 30</c> keeps a document storing <c>"30"</c> — so the window says nothing about what is not a
    /// number. A caller pushing it admits the rest; the fact theory reads it as a bound, which binds numbers
    /// only.
    /// </para>
    /// <para>
    /// <b>Where the conversion saturates, a side holds only where the limit fails the comparison.</b>
    /// <c>INTEGER</c> and <c>BIGINT</c> convert a value beyond their range to the limit — measured,
    /// <c>toInt(1e30)</c> is <c>2147483647</c> — so every stored value past a limit converts to it, however
    /// far past. Where the comparison is true of the limit those documents are kept, and nothing bounds
    /// their raw value on that side. <c>= 2147483647</c> is one such comparison, and so is
    /// <c>&gt; -3000000000</c>: a stored <c>-1e30</c> converts to the lower limit, which is greater than
    /// the bound, while a window above <c>-3000000001</c> would refuse it. The second is the integration
    /// plan's F10. No literal reaches the window that way today — a constant beyond the target's range is
    /// wider than it, and Calcite widens the cast to compare them, which the window does not read through
    /// — so the question is asked of every comparison for the spelling that one day does.
    /// </para>
    /// <para>
    /// <b><c>RETURNING DECIMAL</c> is the same conversion written as a clause</b>, and was read as the stored
    /// value instead: an equality through it said the path holds the literal, and over a path declared a
    /// string the filter was rewritten to <c>FALSE</c>, where Calcite keeps every document storing
    /// <c>"2"</c>, <c>" 2 "</c> or <c>"2.0"</c> for <c>= 2</c>. It parses what the path holds, a string
    /// among it, so it puts a stored number in the same window and says nothing of a string — see
    /// <see cref="CosmosRexTranslator.IsParsingJsonValue"/>. The literal is cast to the comparison's scale,
    /// which against the default <c>DECIMAL(19, 0)</c> truncates <c>2.5</c> to 2; that moves it by less than
    /// one, and the window was loosened by one for exactly that kind of movement.
    /// </para>
    /// </remarks>
    /// <param name="Value">The document value the cast converts.</param>
    /// <param name="Above">A number the raw value is greater than, or <c>null</c>.</param>
    /// <param name="Below">A number the raw value is less than, or <c>null</c>.</param>
    internal readonly record struct CosmosNumericWindow(RexNode Value, java.math.BigDecimal? Above, java.math.BigDecimal? Below)
    {

        /// <summary>
        /// Reads the window a comparison between a numeric cast and a constant implies, or answers
        /// <c>false</c> where the comparison is not one or bounds neither side.
        /// </summary>
        /// <param name="node">The comparison.</param>
        /// <param name="rexBuilder">Builds the rendering a parsing <c>RETURNING</c> converts, which has no node of its own.</param>
        /// <param name="window">On success, the window.</param>
        /// <returns><c>true</c> where a window was read.</returns>
        public static bool TryRead(RexNode node, RexBuilder rexBuilder, out CosmosNumericWindow window)
        {
            window = default;

            if (node is not RexCall call || call.getOperands().size() != 2)
                return false;

            var kind = (SqlKind.__Enum)call.getKind().ordinal();
            if (kind is not (SqlKind.__Enum.EQUALS or SqlKind.__Enum.GREATER_THAN or SqlKind.__Enum.GREATER_THAN_OR_EQUAL
                or SqlKind.__Enum.LESS_THAN or SqlKind.__Enum.LESS_THAN_OR_EQUAL))
                return false;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            // The window is on the side the cast is, and a comparison read the other way round is the
            // mirrored operator.
            if (CosmosRexTranslator.TryNumericCastOperand(left, rexBuilder, out var saturates) is not RexNode value)
            {
                if (CosmosRexTranslator.TryNumericCastOperand(right, rexBuilder, out saturates) is not RexNode mirrored)
                    return false;

                value = mirrored;
                right = left;
                kind = Mirror(kind);
            }

            if (TryConstantNumber(right) is not java.math.BigDecimal bound)
                return false;

            var one = java.math.BigDecimal.ONE;
            java.math.BigDecimal? above = null;
            java.math.BigDecimal? below = null;

            if (kind is SqlKind.__Enum.EQUALS or SqlKind.__Enum.GREATER_THAN or SqlKind.__Enum.GREATER_THAN_OR_EQUAL)
                if (saturates is null || Holds(kind, saturates.Value.Min, bound) == false)
                    above = bound.subtract(one);

            if (kind is SqlKind.__Enum.EQUALS or SqlKind.__Enum.LESS_THAN or SqlKind.__Enum.LESS_THAN_OR_EQUAL)
                if (saturates is null || Holds(kind, saturates.Value.Max, bound) == false)
                    below = bound.add(one);

            if (above is null && below is null)
                return false;

            window = new CosmosNumericWindow(value, above, below);
            return true;
        }

        /// <summary>
        /// Determines whether a converted value satisfies the comparison against the bound.
        /// </summary>
        static bool Holds(SqlKind.__Enum kind, java.math.BigDecimal converted, java.math.BigDecimal bound) => kind switch
        {
            SqlKind.__Enum.EQUALS => converted.compareTo(bound) == 0,
            SqlKind.__Enum.GREATER_THAN => converted.compareTo(bound) > 0,
            SqlKind.__Enum.GREATER_THAN_OR_EQUAL => converted.compareTo(bound) >= 0,
            SqlKind.__Enum.LESS_THAN => converted.compareTo(bound) < 0,
            SqlKind.__Enum.LESS_THAN_OR_EQUAL => converted.compareTo(bound) <= 0,
            _ => true,
        };

        /// <summary>
        /// Reads the number a constant side of a comparison denotes, through any casts around it.
        /// </summary>
        /// <remarks>
        /// The bound arrives wrapped as often as not — Calcite widens a literal to the type it is being
        /// compared against, so <c>&lt;= 30.5</c> reaches here as <c>CAST(30.5:DECIMAL(3, 1)):DOUBLE</c>
        /// — and a cast of a constant to a number is that constant. Any imprecision in the widening is
        /// far inside the whole unit the window is loosened by. A literal that is not a number, which is
        /// what a string on this side would be, is refused: nothing is known about where converting it
        /// lands.
        /// </remarks>
        static java.math.BigDecimal? TryConstantNumber(RexNode node)
        {
            while (node is RexCall call &&
                   (SqlKind.__Enum)call.getKind().ordinal() is SqlKind.__Enum.CAST or SqlKind.__Enum.SAFE_CAST &&
                   call.getOperands().size() == 1)
                node = (RexNode)call.getOperands().get(0);

            return node is RexLiteral literal ? literal.getValue() as java.math.BigDecimal : null;
        }

        /// <summary>
        /// The operator a comparison becomes when its operands swap.
        /// </summary>
        internal static SqlKind.__Enum Mirror(SqlKind.__Enum kind) => kind switch
        {
            SqlKind.__Enum.GREATER_THAN => SqlKind.__Enum.LESS_THAN,
            SqlKind.__Enum.GREATER_THAN_OR_EQUAL => SqlKind.__Enum.LESS_THAN_OR_EQUAL,
            SqlKind.__Enum.LESS_THAN => SqlKind.__Enum.GREATER_THAN,
            SqlKind.__Enum.LESS_THAN_OR_EQUAL => SqlKind.__Enum.GREATER_THAN_OR_EQUAL,
            _ => kind,
        };

    }

}
