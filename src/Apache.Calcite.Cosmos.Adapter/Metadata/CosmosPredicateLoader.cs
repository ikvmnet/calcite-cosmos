using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using Eggnog;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Reads a filter condition into the typed IR's predicate subset, for the predicate oracle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Calcite's types are decoded once, here.</b> The same path arrives as <c>JSON_VALUE</c> typed <c>VARCHAR</c>, as
    /// the accessor re-typed <c>ANY</c>, as a promoted <c>VARIANT</c> column, under a <c>RETURNING</c>, through a numeric
    /// cast, or as a field a projection bound to any of those. Each becomes a reading of its own —
    /// <see cref="SqlValue.Text"/>, <see cref="SqlValue.Raw"/>, <see cref="SqlValue.Typed"/>, <see cref="SqlValue.Cast"/> —
    /// and the oracle reasons about the reading, never the type. The recognisers are the translator's own, so a shape is
    /// read the way a statement would carry it.
    /// </para>
    /// <para>
    /// <b>What is read</b> is what a guard is written in: <c>=</c>, <c>&lt;&gt;</c>, the orderings, <c>IN</c> (a
    /// <c>SEARCH</c> expanded, read back as one membership), <c>IS [NOT] NULL</c>, <c>AND</c>, <c>OR</c> and <c>NOT</c>,
    /// over a reading of a path on one side and a literal or a parameter on the other. Anything else is
    /// <see cref="SqlPredicate.Opaque"/>, which proves nothing and decides nothing: loading less loses facts, and can only
    /// lose a pushdown.
    /// </para>
    /// <para>
    /// <b>A literal is a JSON scalar</b>, converted once, as the translator would send it (<see cref="JsonScalar"/>), and a
    /// literal of a kind a reading is never compared with — a number against a text accessor, say, which Calcite would have
    /// cast — makes the comparison opaque rather than guessed at.
    /// </para>
    /// <para>
    /// <b>What a statement asks of later is read the same way.</b> A <c>CASE</c> condition a rewrite descends to, or a
    /// weakening asked about beside the conjuncts beneath, is read by the <see cref="Loading"/> that read the statement,
    /// so that what is opaque is numbered alike and a condition spelled as a conjunct is the conjunct's term.
    /// </para>
    /// </remarks>
    internal static class CosmosPredicateLoader
    {

        /// <summary>
        /// Expands a <c>SEARCH</c> the way translation does, so that what is read is the shape a statement would carry.
        /// </summary>
        static RexNode Expand(RexNode node)
        {
            try
            {
                return RexUtil.expandSearch(RexBuilderHolder.Value, null, node);
            }
            catch (Exception)
            {
                return node;
            }
        }

        /// <summary>
        /// One binding being read: the paths its fields address, how each is read, and the numbering of what is opaque,
        /// shared by everything read through it.
        /// </summary>
        internal sealed class Loading
        {

            readonly CosmosRexTranslator _paths;
            readonly IReadOnlyList<CosmosReading> _readings;
            readonly string _rootAlias;
            readonly Dictionary<RexNode, long> _opaque = new();
            readonly object _lock = new();

            public Loading(IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, string rootAlias)
            {
                // Resolution reuses the translator so the accepted path forms are the ones a statement would address.
                // Parameters are discarded; only the shape matters.
                _paths = new CosmosRexTranslator(RexBuilderHolder.Value, fields, new CosmosParameterList(), null, null, readings);
                _readings = readings;
                _rootAlias = rootAlias;
            }

            /// <summary>
            /// Reads conjuncts as a statement: one IR conjunct each, beside the node it was read from.
            /// </summary>
            public PredicateStatement Statement(IReadOnlyList<RexNode> conjuncts, IEnumerable<JsonDocumentPath>? mentions = null) =>
                new(conjuncts.Select(Read).ToArray(), conjuncts.Cast<object?>().ToArray(), mentions);

            /// <summary>
            /// Reads one predicate — a conjunct, a <c>CASE</c> condition, a weakening — into the IR, a <c>SEARCH</c>
            /// expanded as translation expands it.
            /// </summary>
            public SqlPredicate Read(RexNode node)
            {
                lock (_lock)
                    return Predicate(Expand(node));
            }

            SqlPredicate Predicate(RexNode node)
            {
                if (node is not RexCall call)
                    return Opaque(node);

                var operands = call.getOperands();
                switch (call.getKind().name())
                {
                    case nameof(SqlKind.__Enum.AND) when operands.size() >= 2:
                        return SqlPredicate.AllOf(Operands(call).Select(Predicate));

                    case nameof(SqlKind.__Enum.OR) when operands.size() >= 2:
                        return Membership(call) ?? SqlPredicate.AnyOf(Operands(call).Select(Predicate));

                    case nameof(SqlKind.__Enum.NOT) when operands.size() == 1:
                        return new SqlPredicate.Not(Predicate((RexNode)operands.get(0)));

                    case nameof(SqlKind.__Enum.EQUALS) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.Equal) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.NOT_EQUALS) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.NotEqual) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.LESS_THAN) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.Less) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.LESS_THAN_OR_EQUAL) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.LessOrEqual) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.GREATER_THAN) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.Greater) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.GREATER_THAN_OR_EQUAL) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.GreaterOrEqual) ?? Opaque(node);

                    case nameof(SqlKind.__Enum.IS_NULL) when operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is SqlValue tested && tested.PathRead is not null
                            ? new SqlPredicate.IsNull(tested)
                            : Opaque(node);

                    // A value that is not null: the negation of a test that is never unknown.
                    case nameof(SqlKind.__Enum.IS_NOT_NULL) when operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is SqlValue present && present.PathRead is not null
                            ? new SqlPredicate.Not(new SqlPredicate.IsNull(present))
                            : Opaque(node);

                    // The service's test of the document, which a weakening writes and a caller may: what it asks is the
                    // path's whatever reads it, so it is read of the raw value. A cast is no accessor the in-process body
                    // can take apart, and is not read.
                    case var _ when ReferenceEquals(call.getOperator(), CosmosOperators.IsDefined) && operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is { PathRead: JsonDocumentPath defined } read && read is not SqlValue.Cast
                            ? new SqlPredicate.IsDefined(new SqlValue.Raw(defined))
                            : Opaque(node);

                    default:
                        return Opaque(node);
                }
            }

            /// <summary>
            /// Reads a disjunction of equalities between one reading and literals as the membership an <c>IN</c> is.
            /// </summary>
            SqlPredicate? Membership(RexCall call)
            {
                SqlValue? value = null;
                var points = new List<string>();

                foreach (var operand in Operands(call))
                {
                    if (operand is not RexCall branch || branch.getKind().name() != nameof(SqlKind.__Enum.EQUALS) || branch.getOperands().size() != 2)
                        return null;

                    if (Comparison(branch, SqlPredicate.Compare.Equal) is not SqlPredicate.Compare { Right: SqlValue.Literal literal } compare)
                        return null;

                    if (value is null)
                        value = compare.Left;
                    else if (value.Equals(compare.Left) == false)
                        return null;

                    points.Add(literal.Value);
                }

                return value is null ? null : new SqlPredicate.In(value, ValueSet.From(points));
            }

            /// <summary>
            /// Reads a comparison between a reading of a path and a literal or a parameter, either way round, with the
            /// reading on the left.
            /// </summary>
            SqlPredicate? Comparison(RexCall call, string op)
            {
                var a = (RexNode)call.getOperands().get(0);
                var b = (RexNode)call.getOperands().get(1);

                if (Value(a) is { PathRead: not null } left && Comparand(b, left) is SqlValue right && Compares(left, right))
                    return new SqlPredicate.Compare(op, left, right);

                if (Value(b) is { PathRead: not null } mirrored && Comparand(a, mirrored) is SqlValue other && Compares(mirrored, other))
                    return new SqlPredicate.Compare(SqlPredicate.Compare.Mirror(op), mirrored, other);

                return null;
            }

            /// <summary>
            /// Reads what a reading is compared with: a parameter, a literal, and against a conversion to a number the
            /// constant <see cref="CosmosNumericWindow"/> reads there.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>A number a conversion is compared with arrives cast</b> — <c>&lt;= 30.5</c> against a cast to
            /// <c>DOUBLE</c> as <c>CAST(30.5:DECIMAL(3, 1)):DOUBLE</c>, and against <c>RETURNING DECIMAL</c> as the
            /// literal cast to the clause's scale — and is read as the window reads it, in one place: a cast to a
            /// number is the constant, except one to a <c>DECIMAL</c> its whole digits do not fit, which raises in
            /// Calcite and is no constant at all. A cast that drops scale truncates the literal, which the conversion
            /// <c>RETURNING DECIMAL</c> is, alone, reads as such (<c>PredicateAtom</c>); against any other reading a
            /// truncated literal would be read as a number Calcite does not compare with, and is not read.
            /// </para>
            /// <para>
            /// Anything else is a literal as written, or boxed for a <c>VARIANT</c> comparison.
            /// </para>
            /// </remarks>
            SqlValue? Comparand(RexNode node, SqlValue reading)
            {
                if (node is RexDynamicParam)
                    return Value(node);

                var converts = reading is SqlValue.Cast { Target: "INTEGER" or "BIGINT" or "DOUBLE" } or SqlValue.Typed { Type: "DECIMAL" };
                if (converts && node is RexCall && Constant(node, truncates: reading is SqlValue.Typed) is java.math.BigDecimal number)
                    return Number(number);

                return CosmosRexTranslator.StripVariantCoercion(node) is RexLiteral literal ? Literal(literal) : null;
            }

            /// <summary>
            /// Reads a number through the casts around it to <c>DOUBLE</c> and to <c>DECIMAL</c>, or answers <c>null</c>:
            /// where another cast converts it, where a <c>DECIMAL</c> drops its scale and <paramref name="truncates"/> is
            /// clear, or where <see cref="CosmosNumericWindow"/> reads no constant.
            /// </summary>
            static java.math.BigDecimal? Constant(RexNode node, bool truncates)
            {
                for (var inner = node; inner is RexCall call; inner = (RexNode)call.getOperands().get(0))
                {
                    if (call.getKind().name() is not (nameof(SqlKind.__Enum.CAST) or nameof(SqlKind.__Enum.SAFE_CAST)) || call.getOperands().size() != 1)
                        return null;

                    var type = call.getType();
                    if (type?.getSqlTypeName() == SqlTypeName.DOUBLE)
                        continue;

                    if (type?.getSqlTypeName() != SqlTypeName.DECIMAL)
                        return null;

                    if (truncates == false && Innermost(call) is RexLiteral { } literal && literal.getValue() is java.math.BigDecimal value && value.scale() > type.getScale())
                        return null;
                }

                return CosmosNumericWindow.TryConstantNumber(node);
            }

            static RexNode Innermost(RexNode node)
            {
                while (node is RexCall call && call.getOperands().size() == 1)
                    node = (RexNode)call.getOperands().get(0);

                return node;
            }

            /// <summary>
            /// The literal of a number, as the translator would send it: past 2^53, the whole number it is, and none where
            /// no value the service compares as the number exists (<see cref="CosmosRexTranslator.TryGetExactNumberValue"/>).
            /// </summary>
            static SqlValue? Number(java.math.BigDecimal number) =>
                CosmosRexTranslator.TryGetExactNumberValue(number) is object value && JsonScalar.TryFrom(value, out var scalar) && scalar.IsNull == false
                    ? SqlValue.Of(scalar)
                    : null;

            /// <summary>
            /// Determines whether a reading is compared with an operand of this kind: a parameter of any, and a literal of
            /// the kind the reading yields.
            /// </summary>
            static bool Compares(SqlValue reading, SqlValue operand)
            {
                if (operand is SqlValue.Parameter)
                    return true;

                if (operand is not SqlValue.Literal literal)
                    return false;

                return reading switch
                {
                    SqlValue.Text => literal.Kind == "string",
                    SqlValue.Raw => literal.Kind != "null",
                    SqlValue.Typed { Type: "BOOLEAN" } => literal.Kind == "boolean",
                    SqlValue.Typed => literal.IsNumber,
                    SqlValue.Cast { Target: "VARCHAR" or "CHAR" } => literal.Kind == "string",
                    SqlValue.Cast => literal.IsNumber,
                    _ => false,
                };
            }

            /// <summary>
            /// Reads a value: a reading of a path, a literal, or a parameter; <c>null</c> where it is none of these.
            /// </summary>
            SqlValue? Value(RexNode node)
            {
                node = CosmosRexTranslator.StripRedundantTextCast(node);

                switch (node)
                {
                    case RexLiteral literal:
                        return Literal(literal);

                    case RexDynamicParam parameter:
                        return new SqlValue.Parameter(parameter.getIndex(), parameter.getType()?.getSqlTypeName()?.getName() ?? "ANY");

                    case RexInputRef reference:
                        return Field(reference);

                    // RETURNING DECIMAL parses the rendering, and the translator reads it as the cast of the text it
                    // converts (CosmosRexTranslator.IsParsingJsonValue); PredicateAtom's typed:DECIMAL is that parse.
                    case RexCall call when CosmosRexTranslator.IsParsingJsonValue(call):
                        return CosmosRexTranslator.TryNumericCastOperand(call, RexBuilderHolder.Value) is RexNode rendered && Value(rendered) is SqlValue.Text text
                            ? new SqlValue.Typed(text.Path, "DECIMAL")
                            : null;

                    case RexCall call when call.getOperator().getName() == "JSON_VALUE":
                        return Accessor(call);

                    case RexCall call when Cast(call) is SqlValue cast:
                        return cast;

                    // An item chain over a map addresses the value the service holds.
                    case RexCall call when IsDocumentValue(call) && Document(call) is JsonDocumentPath item:
                        return new SqlValue.Raw(item);

                    default:
                        return null;
                }
            }

            /// <summary>
            /// Reads a field of the input: a rendering where the binding says it is read as text, the value the service
            /// holds where it is typed as one, and a <c>RETURNING</c> where it is read as a scalar type.
            /// </summary>
            SqlValue? Field(RexInputRef reference)
            {
                if (Document(reference) is not JsonDocumentPath path)
                    return null;

                var index = reference.getIndex();
                var reading = index < _readings.Count ? _readings[index] : CosmosReading.Typed;
                var type = reference.getType()?.getSqlTypeName();

                if (reading == CosmosReading.Text)
                    return IsCharacter(type) ? new SqlValue.Text(path) : null;

                if (reading != CosmosReading.Typed)
                    return null;

                if (CosmosRexTranslator.IsDocumentValueType(type) || IsCharacter(type))
                    return new SqlValue.Raw(path);

                return Scalar(type) is string scalar ? new SqlValue.Typed(path, scalar) : null;
            }

            /// <summary>
            /// Reads <c>JSON_VALUE</c>: the plain accessor typed as text is a rendering, re-typed <c>ANY</c> it is the raw
            /// value, and under a scalar <c>RETURNING</c> it asserts its type.
            /// </summary>
            SqlValue? Accessor(RexCall call)
            {
                if (CosmosRexTranslator.IsPlainJsonValue(call) == false || CosmosRexTranslator.IsArrayReturningJsonValue(call))
                    return null;

                if (Document(call) is not JsonDocumentPath path)
                    return null;

                var type = call.getType()?.getSqlTypeName();
                if (IsCharacter(type))
                    return new SqlValue.Text(path);

                if (CosmosRexTranslator.IsDocumentValueType(type))
                    return new SqlValue.Raw(path);

                return Scalar(type) is string scalar ? new SqlValue.Typed(path, scalar) : null;
            }

            /// <summary>
            /// Reads a cast: the coercion Calcite puts around a literal, the split rule's <c>ANY</c> naming the raw value
            /// under a rendering, a numeric cast the translator recognises, and the cast to text over a raw value.
            /// </summary>
            /// <remarks>
            /// A numeric cast is the translator's to recognise (<see cref="CosmosRexTranslator.TryNumericCastOperand(RexNode, RexBuilder)"/>),
            /// and what it does to a number is <c>PredicateAtom</c>'s to say, in one place each: a cast to <c>DOUBLE</c>
            /// moving a stored integer by a unit from 2^52 among it. A number compared with one is read in
            /// <see cref="Comparand"/>.
            /// </remarks>
            SqlValue? Cast(RexCall call)
            {
                var kind = call.getKind().name();
                if (kind is not (nameof(SqlKind.__Enum.CAST) or nameof(SqlKind.__Enum.SAFE_CAST)) || call.getOperands().size() != 1)
                    return null;

                var operand = (RexNode)call.getOperands().get(0);
                var target = call.getType()?.getSqlTypeName();

                // A literal boxed for a VARIANT comparison is the literal.
                if (operand is RexLiteral literal)
                    return target == SqlTypeName.VARIANT ? Literal(literal) : null;

                if (target == SqlTypeName.ANY)
                    return Value(operand) is { PathRead: JsonDocumentPath raw } inner && inner is SqlValue.Text or SqlValue.Raw ? new SqlValue.Raw(raw) : null;

                if (CosmosRexTranslator.TryNumericCastOperand(call, RexBuilderHolder.Value) is RexNode converted)
                    return Value(converted) is SqlValue inner && inner is SqlValue.Text or SqlValue.Raw ? new SqlValue.Cast(target!.getName(), inner) : null;

                if (CosmosRexTranslator.TryTextCastValue(call) is RexNode rendered)
                    return Value(rendered) switch
                    {
                        SqlValue.Text text => text,
                        SqlValue.Raw raw => new SqlValue.Cast("VARCHAR", raw),
                        _ => null,
                    };

                return null;
            }

            static SqlValue? Literal(RexLiteral literal)
            {
                try
                {
                    return CosmosRexTranslator.GetLiteralValue(literal) is object value && JsonScalar.TryFrom(value, out var scalar) && scalar.IsNull == false
                        ? SqlValue.Of(scalar)
                        : null;
                }
                catch (CosmosTranslationException)
                {
                    return null;
                }
            }

            /// <summary>
            /// Resolves a node to the container-rooted document path it addresses, or <c>null</c>.
            /// </summary>
            JsonDocumentPath? Document(RexNode node)
            {
                if (_paths.TryResolvePath(node, out var path) == false || path is null)
                    return null;

                // A path rooted at a traversal alias addresses an element of an array, not the document; and the
                // document itself is no path a filter compares.
                if (string.Equals(path.Alias, _rootAlias, StringComparison.Ordinal) == false)
                    return null;

                return CosmosDocumentPaths.From(path) is JsonDocumentPath document && document.IsRoot == false ? document : null;
            }

            static bool IsDocumentValue(RexCall call) => CosmosRexTranslator.IsDocumentValueType(call.getType()?.getSqlTypeName());

            static bool IsCharacter(SqlTypeName? type) => type == SqlTypeName.VARCHAR || type == SqlTypeName.CHAR;

            static string? Scalar(SqlTypeName? type) => type?.getName() switch
            {
                "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "FLOAT" or "REAL" or "DOUBLE" or "DECIMAL" or "BOOLEAN" => type.getName(),
                _ => null,
            };

            /// <summary>
            /// Numbers what is not read by the node itself, so that one predicate is one term and two are two.
            /// </summary>
            /// <remarks>
            /// By Calcite's equality, which compares a call's type at every level, and not by its digest, which prints
            /// none: the text accessor and <c>RETURNING DECIMAL</c> over one path both print <c>JSON_VALUE($0, '$.a')</c>,
            /// so <c>a RETURNING DECIMAL = b RETURNING DECIMAL AND NOT (a = b)</c> read as <c>X AND NOT X</c>, and the
            /// filter keeping the rows that spell one number two ways was made the empty relation. Whatever rests on a
            /// term's identity read the same two predicates as one: a deletion as implied, a <c>CASE</c> arm as never
            /// read, a constraint's scope as proved.
            /// </remarks>
            SqlPredicate Opaque(RexNode node)
            {
                if (_opaque.TryGetValue(node, out var id) == false)
                {
                    id = _opaque.Count;
                    _opaque[node] = id;
                }

                return new SqlPredicate.Opaque(id);
            }

            static IEnumerable<RexNode> Operands(RexCall call)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    yield return (RexNode)call.getOperands().get(i);
            }

        }

        /// <summary>
        /// A builder used only for resolution and for expanding a search, which constructs nothing a statement carries.
        /// </summary>
        static class RexBuilderHolder
        {

            internal static readonly RexBuilder Value = new(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());

        }

    }

}
