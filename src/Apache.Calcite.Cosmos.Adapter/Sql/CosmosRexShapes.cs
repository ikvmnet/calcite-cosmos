using System;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Tests of a Rex expression's shape that read no binding: which SQL/JSON accessors address a path, which casts
    /// render a document value and which convert it, and the coercions Calcite's validator adds.
    /// </summary>
    /// <remarks>
    /// The translator's accessor-shape tests, moved so that the loader, the fact rewrite, the joins and the implementor
    /// ask them without building one. Each reads a Rex's shape and nothing about the documents. What reads the binding
    /// is <see cref="CosmosRexPaths"/>'s. See DESIGN.md, <i>Casts over document values</i> and <i>The Rex readers, apart
    /// from the translator</i>.
    /// </remarks>
    internal static class CosmosRexShapes
    {

        /// <summary>
        /// Determines whether a call is one of the SQL/JSON accessors this can render as a path.
        /// </summary>
        /// <remarks>
        /// A plain <c>JSON_VALUE</c> that neither parses nor returns an array, or a plain <c>JSON_QUERY</c>; by name, since a
        /// call resolved through a schema carries an operator Calcite built around the declaration. The single gate every
        /// consumer of a path goes through, so refusing a spelling here refuses it everywhere. See DESIGN.md, <i>Casts over
        /// document values</i> and <i>The document column</i>.
        /// </remarks>
        internal static bool IsJsonAccessor(RexCall call)
        {
            if (call.getOperator().getName() == "JSON_VALUE")
            {
                if (IsPlainJsonValue(call) == false)
                    return false;
                if (IsParsingJsonValue(call))
                    return false;
                return IsArrayReturningJsonValue(call) == false;
            }

            // A wrapper or a behaviour clause substitutes something the path does not hold -- measured,
            // WITH UNCONDITIONAL ARRAY WRAPPER over the string `bikes` answers ["bikes"] and
            // EMPTY OBJECT ON ERROR answers {} -- so only the plain form is the path it addresses.
            return IsPlainJsonQuery(call);
        }

        /// <summary>
        /// Determines whether an expression is a plain <c>JSON_VALUE</c> — one whose behaviour clauses, if it carries any, say
        /// what saying nothing says.
        /// </summary>
        /// <remarks>
        /// A behaviour clause answers something the path does not hold, and rendering one would have to reproduce Calcite's
        /// measured table of them, so it is refused. <c>NULL ON EMPTY</c> and <c>NULL ON ERROR</c> are the defaults written
        /// down, read by name. See DESIGN.md, <i>Casts over document values</i>.
        /// </remarks>
        internal static bool IsPlainJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            var count = call.getOperands().size();
            if (count < 2 || count % 2 != 0)
                return false;

            for (var i = 2; i < count; i += 2)
                if (IsFlag(call, i, "NULL") == false || (IsFlag(call, i + 1, "EMPTY") || IsFlag(call, i + 1, "ERROR")) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Determines whether an expression is a plain <c>JSON_QUERY</c> — the accessor that answers a JSON fragment rather
        /// than a scalar.
        /// </summary>
        /// <remarks>
        /// The two-operand call built by hand, or the validated five-operand call whose wrapper and behaviours say nothing;
        /// read by name, a Java enum's ordinals not being stable. See DESIGN.md, <i>Casts over document values</i>.
        /// </remarks>
        internal static bool IsPlainJsonQuery(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_QUERY")
                return false;

            if (call.getOperands().size() == 2)
                return true;

            return call.getOperands().size() == 5
                && IsFlag(call, 2, "WITHOUT_ARRAY")
                && IsFlag(call, 3, "NULL")
                && IsFlag(call, 4, "NULL");
        }

        /// <summary>
        /// Determines whether an operand is a symbol literal naming the given enum constant.
        /// </summary>
        internal static bool IsFlag(RexCall call, int ordinal, string name)
        {
            if ((RexNode)call.getOperands().get(ordinal) is not RexLiteral literal)
                return false;

            var value = literal.getValue();
            var text = value is java.lang.Enum symbol ? symbol.name() : value?.ToString();

            return string.Equals(text, name, StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether an expression is a <c>JSON_VALUE</c> whose <c>RETURNING</c> names an array type — a spelling
        /// that is refused rather than rendered.
        /// </summary>
        /// <remarks>
        /// SQL restricts <c>JSON_VALUE</c>'s <c>RETURNING</c> to a scalar type, and Calcite answers the spelling by its own
        /// lights; it is refused in every clause so that one expression never means two things (#119). A caller wanting the
        /// array writes <c>JSON_QUERY</c>. See DESIGN.md, <i>The document column</i>.
        /// </remarks>
        internal static bool IsArrayReturningJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            var name = call.getType()?.getSqlTypeName();
            return name == SqlTypeName.ARRAY || name == SqlTypeName.MULTISET;
        }

        /// <summary>
        /// Determines whether an expression is a <c>JSON_VALUE</c> whose <c>RETURNING</c> parses the value it reads rather
        /// than asserting its type — <c>DECIMAL</c>, of any precision and scale.
        /// </summary>
        /// <remarks>
        /// Measured (<c>CalciteJsonValueMeasurementTests</c>), every other numeric or boolean <c>RETURNING</c> asserts one
        /// class and <c>DECIMAL</c> parses a stored string, so it is not the value at the path. It is refused as a path
        /// (<see cref="IsJsonAccessor"/>), and the oracle's loader reads it as the reading it is, its parse of the rendering
        /// its row's (<c>Calcite.ReturningDecimalParsesTheRendering</c>). See DESIGN.md, <i>The document column</i>.
        /// </remarks>
        internal static bool IsParsingJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            return call.getType()?.getSqlTypeName() == SqlTypeName.DECIMAL;
        }

        /// <summary>
        /// Determines whether a call is <c>StringToArray</c> over a SQL/JSON accessor, which addresses an array in the
        /// document rather than parsing one out of a string.
        /// </summary>
        /// <remarks>
        /// The path already holds the array, and the service's <c>StringToArray</c> is <c>undefined</c> over one, so the
        /// composition is elided. Only over an accessor: over a path holding a string the caller means the function.
        /// </remarks>
        internal static bool IsArrayFromJson(RexCall call)
        {
            return string.Equals(call.getOperator().getName(), "StringToArray", StringComparison.Ordinal)
                && call.getOperands().size() == 1
                && call.getOperands().get(0) is RexCall inner
                && IsJsonAccessor(inner);
        }

        /// <summary>
        /// Determines whether an expression is a <c>JSON_VALUE</c> read as text, which is what the accessor is without a
        /// <c>RETURNING</c> clause.
        /// </summary>
        /// <remarks>
        /// A rendering of the value at the path, not the value: a stored number 30 arrives as the text <c>30</c>. See
        /// DESIGN.md, <i>Casts over document values</i>.
        /// </remarks>
        internal static bool IsTextJsonValue(RexNode node)
        {
            if (node is not RexCall call || call.getOperator().getName() != "JSON_VALUE")
                return false;

            var name = call.getType()?.getSqlTypeName();
            return name == SqlTypeName.VARCHAR || name == SqlTypeName.CHAR;
        }

        /// <summary>
        /// Drops a cast to <c>VARCHAR</c> that converts nothing.
        /// </summary>
        /// <remarks>
        /// <c>JSON_VALUE</c> is already <c>VARCHAR</c>, so an undecorated cast over an accessor is an identity, and dropping it
        /// lets the accessor underneath be rendered as itself.
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns>The expression, or the value underneath a redundant cast.</returns>
        internal static RexNode StripRedundantTextCast(RexNode node)
        {
            if (node is not RexCall call || call.getOperands().size() != 1)
                return node;

            var kind = KindOf(call);
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
                return node;

            var type = call.getType();
            if (type?.getSqlTypeName() != SqlTypeName.VARCHAR || type.getPrecision() != org.apache.calcite.rel.type.RelDataType.PRECISION_NOT_SPECIFIED)
                return node;

            var operand = (RexNode)call.getOperands().get(0);
            return operand is RexCall inner && IsJsonAccessor(inner) ? operand : node;
        }

        /// <summary>
        /// Recognises <c>CAST(&lt;document value&gt; AS VARCHAR)</c> over a value the cast renders rather than converts, and
        /// returns the value underneath.
        /// </summary>
        /// <remarks>
        /// Only an undecorated <c>VARCHAR</c>: a width truncates or pads, a second conversion on top of the rendering. What the
        /// oracle's loader reads a value through. See DESIGN.md, <i>Casts over document values</i>.
        /// </remarks>
        internal static RexNode? TryTextCastValue(RexNode node)
        {
            if (node is not RexCall call)
                return null;

            var kind = KindOf(call);
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
                return null;

            if (call.getOperands().size() != 1)
                return null;

            var type = call.getType();
            if (type?.getSqlTypeName() != SqlTypeName.VARCHAR || type.getPrecision() != org.apache.calcite.rel.type.RelDataType.PRECISION_NOT_SPECIFIED)
                return null;

            var operand = Operand(call, 0);
            return IsRenderedDocumentValue(operand) ? operand : null;
        }

        /// <summary>
        /// Determines whether an expression is a document value that a cast to text renders rather than converts.
        /// </summary>
        /// <remarks>
        /// A column typed <c>ANY</c> or <c>VARIANT</c>, or a plain <c>JSON_VALUE</c> of a character type, whose width no run
        /// time applies. A converting <c>RETURNING</c> and <c>JSON_QUERY</c> are refused. See DESIGN.md, <i>Casts over document
        /// values</i>.
        /// </remarks>
        internal static bool IsRenderedDocumentValue(RexNode operand)
        {
            if (IsDocumentValueType(operand.getType()?.getSqlTypeName()))
                return true;

            return IsTextJsonValue(operand) && IsPlainJsonValue(operand);
        }

        /// <summary>
        /// Determines whether a type is one a document value carries: the raw value at a path.
        /// </summary>
        /// <remarks>
        /// A promoted declared-path column is typed <c>VARIANT</c>, and an accessor named as its raw value <c>ANY</c>; both say
        /// "the value the service holds". See DESIGN.md, <i>Promoted columns</i>.
        /// </remarks>
        internal static bool IsDocumentValueType(SqlTypeName? name)
        {
            return name == SqlTypeName.ANY || name == SqlTypeName.VARIANT;
        }

        /// <summary>
        /// Sees through the cast Calcite's type coercion adds to the other side of a comparison with a <c>VARIANT</c> column,
        /// returning the value underneath.
        /// </summary>
        /// <remarks>
        /// The box carries the value unchanged, so the point-read and fact extractors read the literal under it, and the
        /// comparison, <c>LIKE</c> and the null test ask their questions beneath it. Only the single-operand cast whose sole
        /// effect is the retyping. See DESIGN.md, <i>Promoted columns</i>.
        /// </remarks>
        internal static RexNode StripVariantCoercion(RexNode node)
        {
            if (node is RexCall call
                && (KindOf(call) == nameof(SqlKind.__Enum.CAST) || KindOf(call) == nameof(SqlKind.__Enum.SAFE_CAST))
                && call.getOperands().size() == 1
                && call.getType()?.getSqlTypeName() == SqlTypeName.VARIANT)
                return Operand(call, 0);

            return node;
        }

        /// <summary>
        /// Determines whether an expression is of a character type.
        /// </summary>
        /// <remarks>
        /// What decides that a comparison is against a rendering: <c>JSON_VALUE</c> read as text is <c>VARCHAR</c>.
        /// </remarks>
        internal static bool IsCharacter(RexNode node)
        {
            var name = node.getType()?.getSqlTypeName();
            return name == SqlTypeName.VARCHAR || name == SqlTypeName.CHAR;
        }

        /// <summary>
        /// Determines whether a cast differs from its operand only in nullability: a nullable operand cast to the same type
        /// not null, which is Calcite asserting what a null test has proved.
        /// </summary>
        internal static bool IsNullabilityAssertion(RexCall call)
        {
            var kind = KindOf(call);

            return (kind == nameof(SqlKind.__Enum.CAST) || kind == nameof(SqlKind.__Enum.SAFE_CAST))
                && call.getOperands().size() == 1
                && call.getType()?.isNullable() == false
                && Operand(call, 0).getType()?.isNullable() == true
                && org.apache.calcite.sql.type.SqlTypeUtil.equalSansNullability(call.getType(), Operand(call, 0).getType());
        }

        /// <summary>
        /// Recognizes an array subscript and converts it to the service's origin.
        /// </summary>
        /// <remarks>
        /// SQL subscripts from one and Cosmos from zero, so the subscript is <c>index - 1</c>; one below one names no element
        /// and is refused. See DESIGN.md, <i>Differential testing</i>.
        /// </remarks>
        internal static bool TryGetArrayIndex(object? value, out int index)
        {
            switch (value)
            {
                case long l when l >= 1 && l <= int.MaxValue:
                    index = (int)l - 1;
                    return true;
                case int i when i >= 1:
                    index = i - 1;
                    return true;
                case decimal m when m >= 1 && m <= int.MaxValue && decimal.Truncate(m) == m:
                    index = (int)m - 1;
                    return true;
                default:
                    index = 0;
                    return false;
            }
        }

        /// <summary>
        /// Returns a call's <see cref="SqlKind"/> by name, to compare with <c>nameof(SqlKind.__Enum.X)</c>. IKVM compiles that
        /// enumeration's values from the ordinals of the Calcite this was built against, which a newer Calcite may have moved.
        /// </summary>
        internal static string KindOf(RexCall call) => call.getKind().name();

        /// <summary>
        /// Returns an operand as a <see cref="RexNode"/>. Calcite's operand lists are raw Java collections, which surface as
        /// <see cref="object"/>.
        /// </summary>
        internal static RexNode Operand(RexCall call, int index) => (RexNode)call.getOperands().get(index);

    }

}
