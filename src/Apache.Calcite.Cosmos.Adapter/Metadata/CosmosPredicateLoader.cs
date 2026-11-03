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
    /// over a reading of a path on one side and a literal or a parameter on the other; the service's type tests over a
    /// reading of a path; <c>LIKE</c> with a literal pattern holding no bracket; and a boolean <c>CASE</c>, with the truth
    /// tests, <c>COALESCE(p, FALSE)</c> and <c>IS NULL</c> over a predicate read as the cases Calcite defines them as
    /// (<c>Calcite.TruthTestsAreCases</c>, <c>Calcite.CoalesceIsACase</c>). Anything else is
    /// <see cref="SqlPredicate.Opaque"/>, which proves nothing and decides nothing: loading less loses facts, and can only
    /// lose a pushdown. <c>LIKE</c> with <c>ESCAPE</c>, a computed pattern or a bracket stays opaque: the service reads a
    /// bracket as a range (<c>Service.LikeReadsBracketsAsARange</c>), and nothing is read of what no form can say.
    /// </para>
    /// <para>
    /// <b>A literal is a JSON scalar</b>, converted once, as the translator would send it (<see cref="JsonScalar"/>), and a
    /// literal of a kind a reading is never compared with — a number against a text accessor, say, which Calcite would have
    /// cast — makes the comparison opaque rather than guessed at.
    /// </para>
    /// <para>
    /// <b>An opaque predicate is read as a value beside its term</b> (<see cref="PredicateStatement.Opaques"/>), only when
    /// a statement's readings are first asked, which planning never does.
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
        /// Every name a call of an operator may be read by as a function of values: its own, and as <c>Computed</c>
        /// spells a <c>TRIM</c> by its flag, a cast from text to text and a <c>CASE</c>.
        /// </summary>
        internal static IEnumerable<string> Spellings(SqlOperator op) => op.getKind().name() switch
        {
            nameof(SqlKind.__Enum.TRIM) => [op.getName(), .. new[] { org.apache.calcite.sql.fun.SqlTrimFunction.Flag.BOTH, org.apache.calcite.sql.fun.SqlTrimFunction.Flag.LEADING, org.apache.calcite.sql.fun.SqlTrimFunction.Flag.TRAILING }.Select(f => "TRIM " + f.name())],
            nameof(SqlKind.__Enum.CAST) or nameof(SqlKind.__Enum.SAFE_CAST) => [op.getName(), "CAST"],
            nameof(SqlKind.__Enum.CASE) => [op.getName(), "CASE"],
            _ => [op.getName()],
        };

        /// <summary>
        /// One binding being read: the paths its fields address, how each is read, and the numbering of what is opaque,
        /// shared by everything read through it.
        /// </summary>
        internal sealed class Loading
        {

            readonly CosmosRexPaths _paths;
            readonly IReadOnlyList<CosmosReading> _readings;
            readonly string _rootAlias;
            readonly bool _legacy;
            readonly bool _places;
            readonly Dictionary<RexNode, long> _opaque = new();
            readonly List<RexNode> _opaqueNodes = new();
            readonly object _lock = new();

            /// <param name="legacy">
            /// Reads only what was read before the families S does not yet own: no constant, truth test, <c>CASE</c>,
            /// <c>LIKE</c>, type test or element, each opaque as before, for the rendering graph until its switch.
            /// </param>
            /// <param name="places">
            /// Reads, outside the legacy reading, a call the function rows name as a computed place (h10) and two readings
            /// of one kind compared as a pair (h11); a number compared with <c>RETURNING DOUBLE</c> and a null test of a plain
            /// <c>JSON_QUERY</c>, and a text boxed as a <c>VARIANT</c> (h14). Only S's tests read them until its switch (h15), which lists the IR
            /// moves they make: F's terms and memo keys stay as they were, each such call and comparison opaque to F.
            /// </param>
            public Loading(IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, string rootAlias, bool legacy = false, bool places = false)
            {
                _legacy = legacy;
                _places = places && legacy == false;
                // The path readers a statement resolves through, so the accepted path forms are the ones it would address.
                _paths = new CosmosRexPaths(fields, spelled: false, readings);
                _readings = readings;
                _rootAlias = rootAlias;
            }

            /// <summary>
            /// Reads conjuncts as a statement: one IR conjunct each, beside the node it was read from.
            /// </summary>
            public PredicateStatement Statement(IReadOnlyList<RexNode> conjuncts, IEnumerable<JsonDocumentPath>? mentions = null)
            {
                // Its opaque predicates are read when first asked, through this reading, which none is held to where none was read.
                var read = conjuncts.Select(Read).ToArray();
                return new(read, conjuncts.Cast<object?>().ToArray(), mentions, _opaqueNodes.Count > 0 ? Reading : null);
            }

            /// <summary>
            /// Reads an opaque predicate as a value: the value it is, or its call with each operand read as a value where it
            /// is one and left out where it is not, at every depth. A predicate compared with <c>TRUE</c> is that call too,
            /// whose strictness is the rows' (<c>strictop</c> of <c>=</c>), never the loader's.
            /// </summary>
            SqlValue? Reading(long id)
            {
                lock (_lock)
                    return Partially(_opaqueNodes[(int)id]);
            }

            SqlValue? Partially(RexNode node) => Value(node) ?? (node is RexCall call ? Computed(call, partial: true) : null);

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
                if (_legacy == false && node is RexLiteral { } truth && truth.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN)
                    return new SqlPredicate.Constant(truth.getValue() is java.lang.Boolean b ? b.booleanValue() : null);

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
                        return Comparison(call, SqlPredicate.Compare.Equal) ?? (_legacy ? null : Truth(call, equal: true)) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.NOT_EQUALS) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.NotEqual) ?? (_legacy ? null : Truth(call, equal: false)) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.LESS_THAN) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.Less) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.LESS_THAN_OR_EQUAL) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.LessOrEqual) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.GREATER_THAN) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.Greater) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.GREATER_THAN_OR_EQUAL) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.GreaterOrEqual) ?? Opaque(node);

                    // Over a predicate, TRUE where it is UNKNOWN: neither TRUE nor FALSE.
                    case nameof(SqlKind.__Enum.IS_NULL) when operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is SqlValue tested && IsPlace(tested)
                            ? new SqlPredicate.IsNull(tested)
                            : Structure((RexNode)operands.get(0)) is SqlPredicate structure
                                ? new SqlPredicate.Not(structure)
                            : Boxed((RexNode)operands.get(0)) is SqlValue boxed
                                ? new SqlPredicate.IsNull(boxed)
                            : _legacy == false && IsPredicate((RexNode)operands.get(0)) && Predicate((RexNode)operands.get(0)) is var unknown
                                ? new SqlPredicate.Case(unknown, False, new SqlPredicate.Case(new SqlPredicate.Not(unknown), False, True))
                                : Opaque(node);

                    // A value that is not null: the negation of a test that is never unknown.
                    case nameof(SqlKind.__Enum.IS_NOT_NULL) when operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is SqlValue present && IsPlace(present)
                            ? new SqlPredicate.Not(new SqlPredicate.IsNull(present))
                            : Structure((RexNode)operands.get(0)) is SqlPredicate held
                                ? held
                            : Boxed((RexNode)operands.get(0)) is SqlValue boxedPresent
                                ? new SqlPredicate.Not(new SqlPredicate.IsNull(boxedPresent))
                            : _legacy == false && IsPredicate((RexNode)operands.get(0)) && Predicate((RexNode)operands.get(0)) is var known
                                ? new SqlPredicate.Case(known, True, new SqlPredicate.Case(new SqlPredicate.Not(known), True, False))
                                : Opaque(node);

                    case nameof(SqlKind.__Enum.IS_TRUE) when _legacy == false && operands.size() == 1:
                        return new SqlPredicate.Case(Predicate((RexNode)operands.get(0)), True, False);
                    case nameof(SqlKind.__Enum.IS_NOT_TRUE) when _legacy == false && operands.size() == 1:
                        return new SqlPredicate.Case(Predicate((RexNode)operands.get(0)), False, True);
                    case nameof(SqlKind.__Enum.IS_FALSE) when _legacy == false && operands.size() == 1:
                        return new SqlPredicate.Case(new SqlPredicate.Not(Predicate((RexNode)operands.get(0))), True, False);
                    case nameof(SqlKind.__Enum.IS_NOT_FALSE) when _legacy == false && operands.size() == 1:
                        return new SqlPredicate.Case(new SqlPredicate.Not(Predicate((RexNode)operands.get(0))), False, True);

                    case nameof(SqlKind.__Enum.COALESCE) when _legacy == false && operands.size() == 2 && operands.get(1) is RexLiteral otherwise
                        && otherwise.getValue() is java.lang.Boolean { } fallback && fallback.booleanValue() == false:
                        return new SqlPredicate.Case(Predicate((RexNode)operands.get(0)), True, False);

                    // Read outermost first, each further arm the else of the one before.
                    case nameof(SqlKind.__Enum.CASE) when _legacy == false && operands.size() % 2 == 1 && call.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN:
                    {
                        var chosen = Predicate((RexNode)operands.get(operands.size() - 1));
                        for (var i = operands.size() - 3; i >= 0; i -= 2)
                            chosen = new SqlPredicate.Case(Predicate((RexNode)operands.get(i)), Predicate((RexNode)operands.get(i + 1)), chosen);

                        return chosen;
                    }

                    case nameof(SqlKind.__Enum.LIKE) when _legacy == false:
                        return Like(call) ?? Opaque(node);

                    // Over a reading of a path alone, as IS_DEFINED is read; over a call it asks of a computed value.
                    case var _ when _legacy == false && TypeTests.TryGetValue(call.getOperator(), out var test) && operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is { PathRead: JsonDocumentPath typed } typedRead && typedRead is not SqlValue.Cast
                            ? new SqlPredicate.TypeTest(test, new SqlValue.Raw(typed))
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

            static readonly SqlPredicate True = new SqlPredicate.Constant(true), False = new SqlPredicate.Constant(false);

            /// <summary>
            /// Reads a text reading cast to <c>VARIANT</c> as the text: a string box, null where the text is
            /// (<c>Calcite.VariantEqualsOnlyItsOwnBox</c>), for an equality with a boxed string and a null test alone. Only
            /// S's tests read it until its switch (h15), F's terms unchanged.
            /// </summary>
            SqlValue? Boxed(RexNode node) =>
                _places && node is RexCall call && call.getKind().name() == nameof(SqlKind.__Enum.CAST) && call.getOperands().size() == 1
                && call.getType()?.getSqlTypeName() == SqlTypeName.VARIANT && Value((RexNode)call.getOperands().get(0)) is SqlValue.Text text
                    ? text
                    : null;

            /// <summary>A string literal cast to <c>VARIANT</c>, as the string.</summary>
            SqlValue? BoxedString(RexNode node) =>
                node is RexCall { } call && call.getType()?.getSqlTypeName() == SqlTypeName.VARIANT && CosmosRexShapes.StripVariantCoercion(node) is RexLiteral literal
                && Literal(literal) is SqlValue.Literal { Kind: Cosmos.Facts.Saturation.JsonValues.StringType } text
                    ? text
                    : null;

            /// <summary>
            /// Reads a plain <c>JSON_QUERY</c> of a path, under a null test, as the path holding a structure: it answers
            /// an object or an array and null for anything else (<c>Calcite.JsonQueryAnswersStructureOnly</c>). Only S's
            /// tests read it until its switch (h15), F's terms unchanged.
            /// </summary>
            SqlPredicate? Structure(RexNode node) =>
                _places && CosmosRexShapes.IsPlainJsonQuery(node) && Document(node) is JsonDocumentPath { HasElement: false } path
                    ? SqlPredicate.AnyOf([new SqlPredicate.TypeTest(TypeTests[CosmosOperators.IsObject], new SqlValue.Raw(path)),
                        new SqlPredicate.TypeTest(TypeTests[CosmosOperators.IsArray], new SqlValue.Raw(path))])
                    : null;

            /// <summary>The service's type tests but <c>IS_DEFINED</c>, by operator, each read by its name.</summary>
            static readonly Dictionary<SqlOperator, string> TypeTests = new[] { CosmosOperators.IsNull, CosmosOperators.IsString, CosmosOperators.IsNumber,
                CosmosOperators.IsBool, CosmosOperators.IsObject, CosmosOperators.IsArray, CosmosOperators.IsPrimitive }.ToDictionary(o => (SqlOperator)o, o => o.getName());

            /// <summary>Whether a node is a predicate the loader reads as one rather than a value: a boolean call.</summary>
            static bool IsPredicate(RexNode node) => node is RexCall && node.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN;

            /// <summary>
            /// Reads a predicate compared with a truth value, as Calcite defines it: <c>x = TRUE</c> and <c>x &lt;&gt; FALSE</c>
            /// are <c>x</c>, and <c>x = FALSE</c> is <c>NOT x</c>; <c>null</c> for anything else.
            /// </summary>
            SqlPredicate? Truth(RexCall call, bool equal)
            {
                var (a, b) = ((RexNode)call.getOperands().get(0), (RexNode)call.getOperands().get(1));
                var (predicate, literal) = IsPredicate(a) && b is RexLiteral right ? (a, right) : IsPredicate(b) && a is RexLiteral left ? (b, left) : (null, null);
                if (predicate is null || literal!.getValue() is not java.lang.Boolean truth)
                    return null;

                return (equal, truth.booleanValue()) switch
                {
                    (true, true) or (false, false) => Predicate(predicate),
                    (true, false) => new SqlPredicate.Not(Predicate(predicate)),
                    _ => null,
                };
            }

            /// <summary>
            /// Reads <c>LIKE</c> of a reading of a path as text, against a literal pattern holding no bracket, and with no
            /// <c>ESCAPE</c>; <c>NOT LIKE</c> as its negation; <c>null</c> for anything else.
            /// </summary>
            SqlPredicate? Like(RexCall call)
            {
                if (call.getOperands().size() != 2 || call.getOperator().getName() is not ("LIKE" or "NOT LIKE"))
                    return null;

                if (Value((RexNode)call.getOperands().get(0)) is not { PathRead: not null } subject || subject is not (SqlValue.Text or SqlValue.Cast { Target: "VARCHAR" or "CHAR" }))
                    return null;

                if (call.getOperands().get(1) is not RexLiteral literal || Literal(literal) is not SqlValue.Literal { Kind: Cosmos.Facts.Saturation.JsonValues.StringType } pattern)
                    return null;

                var text = Cosmos.Facts.Saturation.JsonValues.StringOf(pattern.Value);
                if (text.IndexOfAny(['[', ']']) >= 0)
                    return null;

                SqlPredicate like = new SqlPredicate.Affix(Cosmos.Facts.Saturation.PredicateAtom.LikeTest, subject, text);
                return call.getOperator().getName() == "NOT LIKE" ? new SqlPredicate.Not(like) : like;
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
            /// reading on the left; and, where places are read, two readings of one kind compared (h11).
            /// </summary>
            SqlPredicate? Comparison(RexCall call, string op)
            {
                var a = (RexNode)call.getOperands().get(0);
                var b = (RexNode)call.getOperands().get(1);

                if (Value(a) is { } left && IsPlace(left) && Comparand(b, left) is SqlValue right && Compares(left, right))
                    return new SqlPredicate.Compare(op, left, right);

                if (Value(b) is { } mirrored && IsPlace(mirrored) && Comparand(a, mirrored) is SqlValue other && Compares(mirrored, other))
                    return new SqlPredicate.Compare(SqlPredicate.Compare.Mirror(op), mirrored, other);

                if (_places && Value(a) is { } first && IsPlace(first) && Value(b) is { } second && IsPlace(second) && KindOf(first) is { } kind && KindOf(second) == kind)
                    return new SqlPredicate.Compare(op, first, second);

                // A text boxed as a VARIANT equals a boxed string as the text does (Calcite.VariantEqualsOnlyItsOwnBox).
                if (op is SqlPredicate.Compare.Equal or SqlPredicate.Compare.NotEqual)
                {
                    if (Boxed(a) is { } boxed && BoxedString(b) is { } literal)
                        return new SqlPredicate.Compare(op, boxed, literal);
                    if (Boxed(b) is { } mirroredBox && BoxedString(a) is { } mirroredLiteral)
                        return new SqlPredicate.Compare(op, mirroredBox, mirroredLiteral);
                }

                return null;
            }

            /// <summary>What two readings compare as: text, a number or a truth; a raw reading as nothing, which is not paired.</summary>
            static string? KindOf(SqlValue reading) => reading switch
            {
                SqlValue.Text or SqlValue.Cast { Target: "VARCHAR" or "CHAR" } or SqlValue.Call { Type: "VARCHAR" or "CHAR" } => "text",
                SqlValue.Typed { Type: "BOOLEAN" } or SqlValue.Call { Type: "BOOLEAN" } => "truth",
                SqlValue.Typed or SqlValue.Cast or SqlValue.Call { Type: not null } => "number",
                _ => null,
            };

            /// <summary>
            /// Reads what a reading is compared with: a parameter, a literal, and against a conversion to a number the
            /// constant <see cref="TryConstantNumber"/> reads there.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>A number a conversion is compared with arrives cast</b> — <c>&lt;= 30.5</c> against a cast to
            /// <c>DOUBLE</c> as <c>CAST(30.5:DECIMAL(3, 1)):DOUBLE</c>, and against <c>RETURNING DECIMAL</c> as the
            /// literal cast to the clause's scale — and is read in one place: a cast to a number is the constant,
            /// except one to a <c>DECIMAL</c> its whole digits do not fit, which raises in Calcite and is no constant
            /// at all. A cast that drops scale truncates the literal, which the conversion
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

                // RETURNING DOUBLE is a conversion too; read only for S until its switch (h15), F's terms unchanged.
                var converts = reading is SqlValue.Cast { Target: "INTEGER" or "BIGINT" or "DOUBLE" } or SqlValue.Typed { Type: "DECIMAL" } || _places && reading is SqlValue.Typed { Type: "DOUBLE" };
                if (converts && node is RexCall && Constant(node, truncates: reading is SqlValue.Typed) is java.math.BigDecimal number)
                    return Number(number);

                return CosmosRexShapes.StripVariantCoercion(node) is RexLiteral literal ? Literal(literal) : null;
            }

            /// <summary>
            /// Reads a number through the casts around it to <c>DOUBLE</c> and to <c>DECIMAL</c>, or answers <c>null</c>:
            /// where another cast converts it, where a <c>DECIMAL</c> drops its scale and <paramref name="truncates"/> is
            /// clear, or where <see cref="TryConstantNumber"/> reads no constant.
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

                return TryConstantNumber(node);
            }

            /// <summary>
            /// Reads the number a constant side of a comparison denotes, through any casts around it.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The constant arrives wrapped as often as not — Calcite widens a literal to the type it is being
            /// compared against, so <c>&lt;= 30.5</c> arrives as <c>CAST(30.5:DECIMAL(3, 1)):DOUBLE</c>
            /// — and a cast of a constant to a number is that constant. Any imprecision in the widening is
            /// far inside the whole unit the atom's window is loosened by (<c>PredicateAtom</c>). A literal that
            /// is not a number, which is what a string on this side would be, is refused: nothing is known about
            /// where converting it lands.
            /// </para>
            /// <para>
            /// <b>Except a cast to a <c>DECIMAL</c> too narrow for it, which raises.</b> Where the two sides'
            /// common type would pass nineteen digits, Calcite casts the literal down to the other side's
            /// type rather than both up: <c>RETURNING DECIMAL(19, 18) = 50</c> compares with
            /// <c>CAST(50):DECIMAL(19, 18)</c>, which has one whole digit and raises on every row. Read as 50,
            /// a window around it answered where the engine raises — over a path declared a number the service kept
            /// nothing between 49 and 51, and over one declared at most 10 the filter was rewritten to
            /// <c>FALSE</c>. A cast that drops scale truncates, which moves the literal by less than one; one
            /// its whole digits do not fit is refused.
            /// </para>
            /// </remarks>
            static java.math.BigDecimal? TryConstantNumber(RexNode node)
            {
                var narrowest = int.MaxValue;

                while (node is RexCall call &&
                       (SqlKind.__Enum)call.getKind().ordinal() is SqlKind.__Enum.CAST or SqlKind.__Enum.SAFE_CAST &&
                       call.getOperands().size() == 1)
                {
                    if (call.getType()?.getSqlTypeName() == SqlTypeName.DECIMAL)
                        narrowest = Math.Min(narrowest, call.getType().getPrecision() - call.getType().getScale());

                    node = (RexNode)call.getOperands().get(0);
                }

                if (node is not RexLiteral literal || literal.getValue() is not java.math.BigDecimal value)
                    return null;

                // The whole digits the literal has, against the fewest any cast on the way down allows.
                var whole = value.precision() - value.scale();
                return whole > narrowest && value.signum() != 0 ? null : value;
            }

            static RexNode Innermost(RexNode node)
            {
                while (node is RexCall call && call.getOperands().size() == 1)
                    node = (RexNode)call.getOperands().get(0);

                return node;
            }

            /// <summary>
            /// The literal of a number, as the translator would send it: past 2^53, the whole number it is, and none where
            /// no value the service compares as the number exists (<see cref="CosmosRexLiterals.TryGetExactNumberValue"/>).
            /// </summary>
            static SqlValue? Number(java.math.BigDecimal number) =>
                CosmosRexLiterals.TryGetExactNumberValue(number) is object value && JsonScalar.TryFrom(value, out var scalar) && scalar.IsNull == false
                    ? SqlValue.Of(scalar)
                    : null;

            /// <summary>What an atom reads: a reading of a path, or a computed place (h10), which only the non-legacy reading types.</summary>
            static bool IsPlace(SqlValue value) => value.PathRead is not null || value is SqlValue.Call { Type: not null };

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
                    SqlValue.Call { Type: "VARCHAR" or "CHAR" } => literal.Kind == "string",
                    SqlValue.Call { Type: "BOOLEAN" } => literal.Kind == "boolean",
                    SqlValue.Call { Type: not null } => literal.IsNumber,
                    _ => false,
                };
            }

            /// <summary>
            /// Reads a value: a reading of a path, a literal, or a parameter; <c>null</c> where it is none of these.
            /// </summary>
            SqlValue? Value(RexNode node)
            {
                node = CosmosRexShapes.StripRedundantTextCast(node);

                switch (node)
                {
                    case RexLiteral literal:
                        return Literal(literal);

                    case RexDynamicParam parameter:
                        return new SqlValue.Parameter(parameter.getIndex(), parameter.getType()?.getSqlTypeName()?.getName() ?? "ANY");

                    case RexInputRef reference:
                        return Field(reference);

                    // RETURNING DECIMAL parses the rendering, and the translator reads it as the cast of the text it
                    // converts (CosmosRexShapes.IsParsingJsonValue); PredicateAtom's typed:DECIMAL is that parse.
                    case RexCall call when CosmosRexShapes.IsParsingJsonValue(call):
                        return CosmosRexShapes.TryNumericCastOperand(call, RexBuilderHolder.Value) is RexNode rendered && Value(rendered) is SqlValue.Text text
                            ? new SqlValue.Typed(text.Path, "DECIMAL")
                            : null;

                    case RexCall call when call.getOperator().getName() == "JSON_VALUE":
                        return Accessor(call);

                    case RexCall call when Cast(call) is SqlValue cast:
                        return cast;

                    // An item chain over a map addresses the value the service holds; one over an array's element is no
                    // path's reading, an ANY's raising over a scalar and a VARIANT's element unboxed (Calcite.ItemIsNullWhereNoElementIs).
                    case RexCall call when IsDocumentValue(call) && Document(call) is JsonDocumentPath { HasElement: false } item:
                        return new SqlValue.Raw(item);

                    case RexCall call:
                        return Computed(call);

                    default:
                        return null;
                }
            }

            /// <summary>
            /// Reads a value, for a question of what it holds rather than of what a filter keeps: a reading of a path, a
            /// literal, a parameter, or a function of values; <c>null</c> where it is none of these.
            /// </summary>
            /// <remarks>
            /// What a string function asks of its argument's characters is asked of the value this reads
            /// (<c>JsonFactSet.CharactersOf</c>), so the translator's question and the oracle's terms are read by one
            /// reader of a <see cref="RexNode"/>.
            /// </remarks>
            public SqlValue? ReadValue(RexNode node)
            {
                lock (_lock)
                    return Value(node);
            }

            /// <summary>
            /// Each path a conjunct reads, by its spelling (<see cref="SqlValue.Spell"/>), and how: the node its raw value
            /// is, and each node it reads the path through, by that reading's reader, which the Rex builder writes a member
            /// judged over the reader through (the member carries its reader, spec §4.2 h09).
            /// </summary>
            /// <remarks>
            /// Each largest node read as a reading of a path, inside an opaque term too, and beneath a cast the reading it
            /// converts as well, a cast's reader being its own. The raw value is a raw reading itself, and a rendering or a
            /// typed <c>JSON_VALUE</c> re-typed <c>ANY</c> (<see cref="RenderingRexBuilder.RawValue"/>); a field typed
            /// otherwise has none to re-type, the translator writing a cast to <c>ANY</c> only over a rendering. The first
            /// raw value, and the first node of each reader, is kept, each being the same value.
            /// </remarks>
            public IReadOnlyDictionary<string, RenderingRexBuilder.Accessor> AccessorsOf(RexNode conjunct, RexBuilder rexBuilder)
            {
                var (raws, readers) = (new Dictionary<string, RexNode>(StringComparer.Ordinal), new Dictionary<string, Dictionary<string, RexNode>>(StringComparer.Ordinal));

                void Walk(RexNode node)
                {
                    node = CosmosRexShapes.StripRedundantTextCast(node);
                    var value = Value(node);

                    // A computed place (h10): the call is its own raw value and its type's reading, keyed by its spelling.
                    if (value is SqlValue.Call { Spelled: { } place, Type: { } type })
                    {
                        (readers.TryGetValue(place, out var held) ? held : readers[place] = new(StringComparer.Ordinal)).TryAdd(Cosmos.Facts.Saturation.PredicateAtom.TypedReader(type), node);
                        raws.TryAdd(place, node);
                    }
                    if (value is { PathRead: JsonDocumentPath path } && Cosmos.Facts.Rendering.RenderingFamily.ReaderOf(value) is { } reader)
                    {
                        var spelled = SqlValue.Spell(path);
                        (readers.TryGetValue(spelled, out var read) ? read : readers[spelled] = new(StringComparer.Ordinal)).TryAdd(reader, node);
                        if (value is not SqlValue.Cast)
                        {
                            if ((value is SqlValue.Raw ? node : value is SqlValue.Text || node is RexCall ? RenderingRexBuilder.RawValue(rexBuilder, node) : null) is { } raw)
                                raws.TryAdd(spelled, raw);
                            return;
                        }
                    }

                    if (node is RexCall call)
                        foreach (var operand in Operands(call))
                            Walk(operand);
                }

                lock (_lock)
                    Walk(Expand(conjunct));

                return readers.ToDictionary(r => r.Key, r => new RenderingRexBuilder.Accessor(raws.GetValueOrDefault(r.Key), r.Value), StringComparer.Ordinal);
            }

            /// <summary>
            /// Reads a function of values: its operator's name, and each operand read as a value; <c>null</c> where one is
            /// not read.
            /// </summary>
            /// <remarks>
            /// Spelled as the operator is, so that what the theory knows of a function is what it knows of that name. A
            /// <c>TRIM</c>'s flag is spelled into the name, <c>TRIM BOTH</c>; a <c>CASE</c> is read as its conditions and its
            /// arms (<see cref="SqlValue.Case"/>), whose characters are its arms'. A cast is read only from text to text, a
            /// conversion being a function of its own. Read <paramref name="partial"/>ly, an operand that is no value is left
            /// out rather than the call, and a <c>CASE</c> missing one is the call of its arms read.
            /// </remarks>
            SqlValue? Computed(RexCall call, bool partial = false)
            {
                var operands = Operands(call).ToList();
                string function;
                IEnumerable<RexNode> read;

                switch (call.getKind().name())
                {
                    case nameof(SqlKind.__Enum.TRIM) when operands.Count == 3 && operands[0] is RexLiteral { } specification && specification.getValue() is org.apache.calcite.sql.fun.SqlTrimFunction.Flag flag:
                        function = "TRIM " + flag.name();
                        read = operands.Skip(1);
                        break;

                    case nameof(SqlKind.__Enum.CASE) when operands.Count % 2 == 1:
                        function = "CASE";
                        read = operands.Where((_, i) => i % 2 == 1 || i == operands.Count - 1);
                        break;

                    case nameof(SqlKind.__Enum.CAST) or nameof(SqlKind.__Enum.SAFE_CAST):
                        if (operands.Count != 1 || IsCharacter(call.getType()?.getSqlTypeName()) == false || IsCharacter(operands[0].getType()?.getSqlTypeName()) == false)
                            return null;

                        function = "CAST";
                        read = operands;
                        break;

                    default:
                        function = call.getOperator().getName();
                        read = operands;
                        break;
                }

                var values = new List<SqlValue>();
                foreach (var operand in read)
                {
                    if ((partial ? Partially(operand) : Value(operand)) is SqlValue value)
                        values.Add(value);
                    else if (partial == false)
                        return null;
                }

                if (_legacy == false && function == "CASE" && values.Count == (operands.Count + 1) / 2)
                    return new SqlValue.Case([.. operands.Where((_, i) => i % 2 == 0 && i < operands.Count - 1).Select(Predicate)], values);

                var computed = new SqlValue.Call(function, values);

                // A call the function rows name, read whole, is a place of its own in S (h10): its type and its spelling
                // ride on it, no part of its identity. Until h15's switch only a loading that reads places types one.
                if (_places == false || partial || call.getType()?.getSqlTypeName()?.getName() is not string type
                    || Sql.CosmosFunctionSpellings.RowFunctions.Contains(Cosmos.Facts.Rendering.RenderingImage.KeyFunction(function, type)) == false
                    || Sql.CosmosFunctionSpellings.Spell(computed, PlaceSpelling) is not string spelled || Depth(computed) > PlaceDepth)
                    return computed;

                return computed with { Type = type, Spelled = spelled };
            }

            /// <summary>The deepest nesting of computed places S gathers, one outer pass a level (h10, R7): a deeper call has no place.</summary>
            const int PlaceDepth = 4;

            static int Depth(SqlValue value) => value is SqlValue.Call call ? 1 + call.Operands.Select(o => o is SqlValue.Call { Type: not null } ? Depth(o) : 0).DefaultIfEmpty(0).Max() : 0;

            /// <summary>An operand's spelling in a computed place's: a path's, a literal's own, a nested place's; none for the rest.</summary>
            static string? PlaceSpelling(SqlValue operand) => operand switch
            {
                SqlValue.Call call => call.Spelled,
                SqlValue.Literal literal => literal.Value,
                _ when operand.PathRead is JsonDocumentPath path => SqlValue.Spell(path),
                _ => null,
            };

            /// <summary>
            /// Reads a field of the input: a rendering where the binding says it is read as text, the value the service
            /// holds where it is typed as one, and a <c>RETURNING</c> where it is read as a scalar type.
            /// </summary>
            SqlValue? Field(RexInputRef reference)
            {
                if (Document(reference) is not JsonDocumentPath { HasElement: false } path)
                    return null;

                var index = reference.getIndex();
                var reading = index < _readings.Count ? _readings[index] : CosmosReading.Typed;
                var type = reference.getType()?.getSqlTypeName();

                if (reading == CosmosReading.Text)
                    return IsCharacter(type) ? new SqlValue.Text(path) : null;

                if (reading != CosmosReading.Typed)
                    return null;

                if (CosmosRexShapes.IsDocumentValueType(type) || IsCharacter(type))
                    return new SqlValue.Raw(path);

                return Scalar(type) is string scalar ? new SqlValue.Typed(path, scalar) : null;
            }

            /// <summary>
            /// Reads <c>JSON_VALUE</c>: the plain accessor typed as text is a rendering, re-typed <c>ANY</c> it is the raw
            /// value, and under a scalar <c>RETURNING</c> it asserts its type.
            /// </summary>
            SqlValue? Accessor(RexCall call)
            {
                if (CosmosRexShapes.IsPlainJsonValue(call) == false || CosmosRexShapes.IsArrayReturningJsonValue(call))
                    return null;

                if (Document(call) is not JsonDocumentPath path)
                    return null;

                var type = call.getType()?.getSqlTypeName();
                if (IsCharacter(type))
                    return new SqlValue.Text(path);

                // An element's rendering and RETURNING are as of a path holding it (Calcite.ReadingsOfAnIndexAreAsOfAPath);
                // its raw value was not measured.
                if (CosmosRexShapes.IsDocumentValueType(type))
                    return path.HasElement ? null : new SqlValue.Raw(path);

                return Scalar(type) is string scalar ? new SqlValue.Typed(path, scalar) : null;
            }

            /// <summary>
            /// Reads a cast: the coercion Calcite puts around a literal, the split rule's <c>ANY</c> naming the raw value
            /// under a rendering, a numeric cast the translator recognises, and the cast to text over a raw value.
            /// </summary>
            /// <remarks>
            /// A numeric cast is the shape readers' to recognise (<see cref="CosmosRexShapes.TryNumericCastOperand(RexNode, RexBuilder)"/>),
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
                    return Value(operand) is { PathRead: JsonDocumentPath { HasElement: false } raw } inner && inner is SqlValue.Text or SqlValue.Raw ? new SqlValue.Raw(raw) : null;

                // A field bound to a rendering, cast to a number, parses it as the accessor's cast does: the guarded arm the
                // Rex builder writes over a text column (spec h09), read so by S alone until h15.
                if (_legacy == false && operand is RexInputRef && (target == SqlTypeName.INTEGER || target == SqlTypeName.BIGINT || target == SqlTypeName.DOUBLE)
                    && Value(operand) is SqlValue.Text rendering)
                    return new SqlValue.Cast(target!.getName(), rendering);

                if (CosmosRexShapes.TryNumericCastOperand(call, RexBuilderHolder.Value) is RexNode converted)
                    return Value(converted) is SqlValue inner && inner is SqlValue.Text or SqlValue.Raw ? new SqlValue.Cast(target!.getName(), inner) : null;

                if (CosmosRexShapes.TryTextCastValue(call) is RexNode rendered)
                    return Value(rendered) switch
                    {
                        SqlValue.Text text => text,
                        SqlValue.Raw raw => new SqlValue.Cast("VARCHAR", raw),
                        _ => null,
                    };

                return null;
            }

            /// <summary>
            /// A literal as the value Calcite evaluates, exactly, or <c>null</c>: an approximate number is its double's own
            /// value, a <c>REAL</c>'s its float's, and one within a decimal's range that no decimal holds exactly — most
            /// fractions, a negative zero — is none, since every cell and every reading compares the literal by that value.
            /// One beyond the range keeps its approximate spelling, which no cell reads a value from.
            /// </summary>
            static SqlValue? Literal(RexLiteral literal)
            {
                try
                {
                    var value = CosmosRexLiterals.GetLiteralValue(literal);
                    if (value is double d && (d == 0 && double.IsNegative(d) || literal.getType()?.getSqlTypeName() == SqlTypeName.REAL && (float)d != d))
                        return null;

                    return value is not null && JsonScalar.TryFrom(value, out var scalar) && scalar.IsNull == false
                        && (scalar.ToClrValue() is not double approximate || Math.Abs(approximate) >= Math.ScaleB(1, 96)) ? SqlValue.Of(scalar) : null;
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
                return CosmosDocumentPaths.From(path, _rootAlias, elements: _legacy == false) is JsonDocumentPath document && document.IsRoot == false ? document : null;
            }

            static bool IsDocumentValue(RexCall call) => CosmosRexShapes.IsDocumentValueType(call.getType()?.getSqlTypeName());

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
                    _opaqueNodes.Add(node);
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
