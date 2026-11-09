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
    /// reading of a path; <c>LIKE</c> with a literal pattern; and a boolean <c>CASE</c>, with the truth tests,
    /// <c>COALESCE(p, FALSE)</c> and <c>IS NULL</c> over a predicate read as the cases Calcite defines them as
    /// (<c>Calcite.TruthTestsAreCases</c>, <c>Calcite.CoalesceIsACase</c>). Anything else is
    /// <see cref="SqlPredicate.Opaque"/>, which proves nothing and decides nothing: loading less loses facts, and can only
    /// lose a pushdown. <c>LIKE</c> with <c>ESCAPE</c> or a computed pattern stays opaque; one with a literal pattern is read
    /// of any place, a bracket a unit of its pattern like any, what each side reads it as their rows' (<c>likeunits</c>,
    /// <c>likereads</c>, G7/G8). One reading for both graphs since the switch (h15).
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
        /// Every name a call of an operator may be read by as a function of values: its own (<see cref="NameOf"/>), and as
        /// <c>Computed</c> spells a <c>TRIM</c> by its flag, a cast from text to text and a <c>CASE</c>.
        /// </summary>
        internal static IEnumerable<string> Spellings(SqlOperator op) => op.getKind().name() switch
        {
            nameof(SqlKind.__Enum.TRIM) => [op.getName(), .. new[] { org.apache.calcite.sql.fun.SqlTrimFunction.Flag.BOTH, org.apache.calcite.sql.fun.SqlTrimFunction.Flag.LEADING, org.apache.calcite.sql.fun.SqlTrimFunction.Flag.TRAILING }.Select(f => "TRIM " + f.name())],
            nameof(SqlKind.__Enum.CAST) or nameof(SqlKind.__Enum.SAFE_CAST) => [op.getName(), "CAST"],
            nameof(SqlKind.__Enum.CASE) => [op.getName(), "CASE"],
            _ => [NameOf(op)],
        };

        /// <summary>
        /// The name a call of an operator is read by: its own, but the concatenation that reads a null as the empty string,
        /// PostgreSQL's and SQL Server's <c>CONCAT</c> and Oracle's two-argument one, which share <c>CONCAT</c>'s name and
        /// not its meaning, by a name of its own found by its kind (<see cref="Cosmos.Facts.Saturation.StringCalls.NullAsEmpty"/>),
        /// so that rows of its own answer for it.
        /// </summary>
        internal static string NameOf(SqlOperator op) =>
            op.getKind().name() is nameof(SqlKind.__Enum.CONCAT_WITH_NULL) or nameof(SqlKind.__Enum.CONCAT2) ? Cosmos.Facts.Saturation.StringCalls.NullAsEmpty : op.getName();

        /// <summary>
        /// One binding being read: the paths its fields address, how each is read, and the numbering of what is opaque,
        /// shared by everything read through it.
        /// </summary>
        internal sealed class Loading
        {

            readonly CosmosRexPaths _paths;
            readonly IReadOnlyList<CosmosReading> _readings;
            readonly string _rootAlias;
            readonly Dictionary<RexNode, long> _opaque = new();
            readonly List<RexNode> _opaqueNodes = new();
            readonly object _lock = new();

            /// <summary>
            /// A binding read as both graphs read it since the switch (h15): a call the service can spell as a computed place
            /// (h10, G6) and two places compared as a pair (h11, G3); a number compared with <c>RETURNING DOUBLE</c>, a null
            /// test of a plain <c>JSON_QUERY</c> and a truth reading as a whole condition (h14); the text cast to
            /// <c>BOOLEAN</c> and a schema's own <c>IS_DEFINED</c> (the owner's decisions, 2026-11-03); and, as written (G8), a
            /// value of any kind cast to <c>VARIANT</c> as its box, a null literal as SQL's null of its type, a number Calcite
            /// wrote cast as the casts' terms, a one-place test of the reading as it stands and <c>LIKE</c> of any place
            /// against a pattern a bracket may be in.
            /// </summary>
            public Loading(IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, string rootAlias)
            {
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
                if (node is RexLiteral { } truth && truth.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN)
                    return new SqlPredicate.Constant(truth.getValue() is java.lang.Boolean b ? b.booleanValue() : null);

                if (node is not RexCall call)
                    return Bare(node) ?? Opaque(node);

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
                        return Comparison(call, SqlPredicate.Compare.Equal) ?? Truth(call, equal: true) ?? Opaque(node);
                    case nameof(SqlKind.__Enum.NOT_EQUALS) when operands.size() == 2:
                        return Comparison(call, SqlPredicate.Compare.NotEqual) ?? Truth(call, equal: false) ?? Opaque(node);
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
                            : IsPredicate((RexNode)operands.get(0)) && Predicate((RexNode)operands.get(0)) is var unknown
                                ? new SqlPredicate.Case(unknown, False, new SqlPredicate.Case(new SqlPredicate.Not(unknown), False, True))
                                : Opaque(node);

                    // A value that is not null: the negation of a test that is never unknown.
                    case nameof(SqlKind.__Enum.IS_NOT_NULL) when operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is SqlValue present && IsPlace(present)
                            ? new SqlPredicate.Not(new SqlPredicate.IsNull(present))
                            : IsPredicate((RexNode)operands.get(0)) && Predicate((RexNode)operands.get(0)) is var known
                                ? new SqlPredicate.Case(known, True, new SqlPredicate.Case(new SqlPredicate.Not(known), True, False))
                                : Opaque(node);

                    case nameof(SqlKind.__Enum.IS_TRUE) when operands.size() == 1:
                        return new SqlPredicate.Case(Predicate((RexNode)operands.get(0)), True, False);
                    case nameof(SqlKind.__Enum.IS_NOT_TRUE) when operands.size() == 1:
                        return new SqlPredicate.Case(Predicate((RexNode)operands.get(0)), False, True);
                    case nameof(SqlKind.__Enum.IS_FALSE) when operands.size() == 1:
                        return new SqlPredicate.Case(new SqlPredicate.Not(Predicate((RexNode)operands.get(0))), True, False);
                    case nameof(SqlKind.__Enum.IS_NOT_FALSE) when operands.size() == 1:
                        return new SqlPredicate.Case(new SqlPredicate.Not(Predicate((RexNode)operands.get(0))), False, True);

                    case nameof(SqlKind.__Enum.COALESCE) when operands.size() == 2 && operands.get(1) is RexLiteral otherwise
                        && otherwise.getValue() is java.lang.Boolean { } fallback && fallback.booleanValue() == false:
                        return new SqlPredicate.Case(Predicate((RexNode)operands.get(0)), True, False);

                    // Read outermost first, each further arm the else of the one before.
                    case nameof(SqlKind.__Enum.CASE) when operands.size() % 2 == 1 && call.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN:
                    {
                        var chosen = Predicate((RexNode)operands.get(operands.size() - 1));
                        for (var i = operands.size() - 3; i >= 0; i -= 2)
                            chosen = new SqlPredicate.Case(Predicate((RexNode)operands.get(i)), Predicate((RexNode)operands.get(i + 1)), chosen);

                        return chosen;
                    }

                    case nameof(SqlKind.__Enum.LIKE):
                        return Like(call) ?? Opaque(node);

                    // The test of the value as it stands, whatever it reads: what the in-process body takes it apart into, and
                    // whether it raises, is the vocabulary's test rules' over Calcite.TypeTestsAreTheirBodies' rows (testreads,
                    // G8), a reading no row names, a cast or a computed value's, leaving the atom unread.
                    case var _ when TypeTests.TryGetValue(call.getOperator(), out var test) && operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is { } typedValue ? new SqlPredicate.TypeTest(test, typedValue) : Opaque(node);

                    // The service's test of the document, which a weakening writes and a caller may, read as the type tests
                    // are. A schema's own declaration of it is Calcite's wrapping, with the one body, read by reference too.
                    case var _ when (ReferenceEquals(call.getOperator(), CosmosOperators.IsDefined) || CosmosSchemaFunctions.Presents(call.getOperator(), CosmosOperators.IsDefined))
                        && operands.size() == 1:
                        return Value((RexNode)operands.get(0)) is { } defined ? new SqlPredicate.IsDefined(defined) : Opaque(node);

                    default:
                        return Bare(node) ?? Opaque(node);
                }
            }

            /// <summary>
            /// Reads a truth reading written as a whole condition as its equality with <c>TRUE</c>, which Calcite defines it
            /// as (<c>Calcite.TruthTestsAreCases</c>).
            /// </summary>
            SqlPredicate? Bare(RexNode node) =>
                node.getType()?.getSqlTypeName() == SqlTypeName.BOOLEAN && Value(node) is { } truth && IsPlace(truth)
                    ? new SqlPredicate.Compare(SqlPredicate.Compare.Equal, truth, Literal((RexLiteral)RexBuilderHolder.Value.makeLiteral(true))!)
                    : null;

            static readonly SqlPredicate True = new SqlPredicate.Constant(true), False = new SqlPredicate.Constant(false);

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
            /// Reads <c>LIKE</c> against a literal pattern with no <c>ESCAPE</c>, <c>NOT LIKE</c> as its negation, <c>null</c>
            /// for anything else: of any place as it stands, every unit of its pattern a bracket among them. Which readings it
            /// decides is the vocabulary's over Calcite's rows, and what each side reads a unit as their rows' (<c>likeunits</c>,
            /// <c>likereads</c>, G7/G8).
            /// </summary>
            SqlPredicate? Like(RexCall call)
            {
                if (call.getOperands().size() != 2 || call.getOperator().getName() is not ("LIKE" or "NOT LIKE"))
                    return null;

                var operand = (RexNode)call.getOperands().get(0);
                if (Value(operand) is not { } subject || IsPlace(subject) == false)
                    return null;

                if (call.getOperands().get(1) is not RexLiteral literal || Literal(literal) is not SqlValue.Literal { Kind: Cosmos.Facts.Saturation.JsonValues.StringType } pattern)
                    return null;

                var text = Cosmos.Facts.Saturation.JsonValues.StringOf(pattern.Value);
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
            /// reading on the left; and two readings compared (h11), whose kinds S's pair rule reads (G3).
            /// </summary>
            SqlPredicate? Comparison(RexCall call, string op)
            {
                var a = (RexNode)call.getOperands().get(0);
                var b = (RexNode)call.getOperands().get(1);

                if (Value(a) is { } left && IsPlace(left) && Comparand(b) is SqlValue right && (right is SqlValue.Box or SqlValue.Null || Compares(left, right)))
                    return new SqlPredicate.Compare(op, left, right);

                if (Value(b) is { } mirrored && IsPlace(mirrored) && Comparand(a) is SqlValue other && (other is SqlValue.Box or SqlValue.Null || Compares(mirrored, other)))
                    return new SqlPredicate.Compare(SqlPredicate.Compare.Mirror(op), mirrored, other);

                if (Value(a) is { } first && IsPlace(first) && Value(b) is { } second && IsPlace(second))
                    return new SqlPredicate.Compare(op, first, second);

                return null;
            }

            /// <summary>
            /// Reads what a reading is compared with: a parameter, a literal, SQL's null (<see cref="NullOf"/>), a value cast to
            /// <c>VARIANT</c> as the box it is (<see cref="SqlValue.Box"/>), and a number Calcite wrote cast as the casts'
            /// terms (<see cref="CastNumber"/>).
            /// </summary>
            /// <remarks>
            /// <b>A number a conversion is compared with arrives cast</b> — <c>&lt;= 30.5</c> against a cast to <c>DOUBLE</c> as
            /// <c>CAST(30.5:DECIMAL(3, 1)):DOUBLE</c>, and against <c>RETURNING DECIMAL</c> as the literal cast to the clause's
            /// scale — and is read as it stands (G8): what the casts denote and which readings compare with it are the shared
            /// vocabulary's rules and rows (<c>denotes</c>, <c>takesconstant</c>). A cast to a <c>DECIMAL</c> its whole digits
            /// do not fit raises in Calcite and denotes nothing, and one that drops scale moves the literal, which only a
            /// reading its row says reads the move takes. A box holds a literal, a parameter or a call, what it equals the box
            /// rules' (G8), which read a reading's and a literal's box alone.
            /// </remarks>
            SqlValue? Comparand(RexNode node)
            {
                if (node is RexDynamicParam)
                    return Value(node);

                return node is RexLiteral literal ? Literal(literal) ?? NullOf(literal) : Value(node) is SqlValue.Box boxed ? boxed : CastNumber(node);
            }

            /// <summary>
            /// Reads a number Calcite wrote cast, as the casts' terms: each cast to <c>DOUBLE</c> a <see cref="SqlValue.Cast"/>,
            /// each to <c>DECIMAL(p, s)</c> a <see cref="SqlValue.DecimalCast"/>, over the literal as the translator would send
            /// it (<see cref="Number"/>); <c>null</c> where anything else is cast, or the literal is no number the service
            /// compares as one.
            /// </summary>
            static SqlValue? CastNumber(RexNode node)
            {
                if (node is RexLiteral literal)
                    return literal.getValue() is java.math.BigDecimal value ? Number(value) : null;

                if (node is not RexCall call || call.getKind().name() is not (nameof(SqlKind.__Enum.CAST) or nameof(SqlKind.__Enum.SAFE_CAST)) || call.getOperands().size() != 1
                    || CastNumber((RexNode)call.getOperands().get(0)) is not { } cast)
                    return null;

                var type = call.getType();
                return type?.getSqlTypeName() == SqlTypeName.DOUBLE ? new SqlValue.Cast(SqlTypeName.DOUBLE.getName(), cast)
                    : type?.getSqlTypeName() == SqlTypeName.DECIMAL ? new SqlValue.DecimalCast(type.getPrecision(), type.getScale(), cast)
                    : null;
            }

            /// <summary>
            /// The literal of a number, as the translator would send it: past 2^53, the whole number it is, and none where
            /// no value the service compares as the number exists (<see cref="CosmosRexLiterals.TryGetExactNumberValue"/>).
            /// </summary>
            static SqlValue? Number(java.math.BigDecimal number) =>
                CosmosRexLiterals.TryGetExactNumberValue(number) is object value && JsonScalar.TryFrom(value, out var scalar)
                    ? SqlValue.Of(scalar)
                    : null;

            /// <summary>What an atom reads: a reading of a path, or a computed place (h10).</summary>
            static bool IsPlace(SqlValue value) => value.PathRead is not null || value is SqlValue.Call { Type: not null };

            /// <summary>
            /// Determines whether a reading is compared with an operand of this kind: a parameter of any, and a literal of
            /// the kind the reading yields, a number Calcite wrote cast by the literal it casts.
            /// </summary>
            static bool Compares(SqlValue reading, SqlValue operand)
            {
                if (operand is SqlValue.Parameter)
                    return true;

                if (Uncast(operand) is not SqlValue.Literal literal)
                    return false;

                return reading switch
                {
                    SqlValue.Text => literal.Kind == "string",
                    // Literal reads no SQL null, and a JSON null is no literal a filter writes: any literal.
                    SqlValue.Raw => true,
                    SqlValue.Typed { Type: "BOOLEAN" } => literal.Kind == "boolean",
                    SqlValue.Typed => literal.IsNumber,
                    SqlValue.Cast { Target: "VARCHAR" or "CHAR" } => literal.Kind == "string",
                    SqlValue.Cast { Target: "BOOLEAN" } => literal.Kind == "boolean",
                    SqlValue.Cast => literal.IsNumber,
                    SqlValue.Call { Type: "VARCHAR" or "CHAR" } => literal.Kind == "string",
                    SqlValue.Call { Type: "BOOLEAN" } => literal.Kind == "boolean",
                    SqlValue.Call { Type: not null } => literal.IsNumber,
                    _ => false,
                };
            }

            /// <summary>The literal a number Calcite wrote cast casts (<see cref="CastNumber"/>), or the operand itself.</summary>
            static SqlValue Uncast(SqlValue operand) => operand switch
            {
                SqlValue.Cast { PathRead: null } cast => Uncast(cast.Value),
                SqlValue.DecimalCast cast => Uncast(cast.Value),
                _ => operand,
            };

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

                    // A JSON_QUERY with no clause is its own reading of the path (G8): what it answers
                    // of each class its row's (Calcite.JsonQueryAnswersStructureOnly), an element's as of a path holding it
                    // (Calcite.ReadingsOfAnIndexAreAsOfAPath), and how a test of it is written its template's. A clause answers
                    // what the path does not hold (Calcite.JsonQueryClausesSubstitute), and is read as a call.
                    case RexCall call when CosmosRexShapes.IsPlainJsonQuery(call) && Document(call) is JsonDocumentPath query:
                        return new SqlValue.Query(query);

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
                        function = NameOf(call.getOperator());
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

                if (function == "CASE" && values.Count == (operands.Count + 1) / 2)
                    return new SqlValue.Case([.. operands.Where((_, i) => i % 2 == 0 && i < operands.Count - 1).Select(Predicate)], values);

                var computed = new SqlValue.Call(function, values);

                // A call read whole that the service can spell is a place of its own in S (h10): its type and its spelling
                // ride on it, no part of its identity. Whether its rows name it, and how deep S images it, are S's (G6). A
                // call only the service evaluates (REGEXMATCH, the full text family) has no value Calcite computes to read:
                // it is the translator's to write, as a call Calcite has no body of.
                if (partial || call.getType()?.getSqlTypeName()?.getName() is not string type || Sql.CosmosOperators.ReferencesServiceOnlyFunction(call)
                    || Sql.CosmosFunctionSpellings.Spell(computed, PlaceSpelling) is not string spelled)
                    return computed;

                return computed with { Type = type, Spelled = spelled };
            }

            /// <summary>
            /// An operand's spelling in a computed place's: a path's, a literal's own, a nested place's, and a box's what it
            /// holds, the service holding no box; none for the rest. Whether S images a call over a box is its image rules'
            /// (G8): those that gather an operand gather none from a box, which reads no path, and the D class's, which reads
            /// no operand, images it as the call over what the box holds, whose spelling it shares.
            /// </summary>
            static string? PlaceSpelling(SqlValue operand) => operand switch
            {
                SqlValue.Call call => call.Spelled,
                SqlValue.Literal literal => literal.Value,
                SqlValue.Box box => PlaceSpelling(box.Value),
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

                // The field's declared type is the column Calcite reads it through, named (rawtyped), as read: what each
                // column reads is its rows', a CHAR's the union's until a row measures its padding (PredicateAtom.UnionColumns).
                if (CosmosRexShapes.IsDocumentValueType(type) || IsCharacter(type))
                    return new SqlValue.Raw(path) { Column = type?.getName() };

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

                // A value cast to VARIANT is the box it is, of its SQL type, whatever value it holds (G8): what it equals and
                // where it is null are the box rules', which read a literal's and a reading's alone.
                if (target == SqlTypeName.VARIANT)
                    return operand.getType()?.getSqlTypeName()?.getName() is string boxed
                        && (operand is RexLiteral boxedLiteral ? Literal(boxedLiteral) ?? NullOf(boxedLiteral) : Value(operand)) is SqlValue held
                        ? new SqlValue.Box(boxed, held) : null;

                if (operand is RexLiteral)
                    return null;

                if (target == SqlTypeName.ANY)
                    return Value(operand) is { PathRead: JsonDocumentPath { HasElement: false } raw } inner && inner is SqlValue.Text or SqlValue.Raw ? new SqlValue.Raw(raw) : null;

                // A field bound to a rendering, cast to a number, parses it as the accessor's cast does: the guarded arm the
                // Rex builder writes over a text column (spec h09).
                if (operand is RexInputRef && (target == SqlTypeName.INTEGER || target == SqlTypeName.BIGINT || target == SqlTypeName.DOUBLE)
                    && Value(operand) is SqlValue.Text rendering)
                    return new SqlValue.Cast(target!.getName(), rendering);

                // The text cast to BOOLEAN parses it (Calcite.CastToBooleanParses), the way a view types a boolean column. Over
                // anything but the text no row measures the cast.
                if (target == SqlTypeName.BOOLEAN)
                    return Value(operand) is SqlValue.Text parsed ? new SqlValue.Cast(target.getName(), parsed) : null;

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
            /// SQL's null, as a literal of a type holding none is read (G8): what a comparison with it answers is the shared
            /// vocabulary's rule over the operator's row (<c>strictop</c>); <c>null</c> for a literal holding a value.
            /// </summary>
            static SqlValue? NullOf(RexLiteral literal) =>
                literal.isNull() ? new SqlValue.Null(literal.getType()?.getSqlTypeName()?.getName() ?? SqlTypeName.NULL.getName()) : null;

            /// <summary>
            /// Resolves a node to the container-rooted document path it addresses, or <c>null</c>.
            /// </summary>
            JsonDocumentPath? Document(RexNode node)
            {
                if (_paths.TryResolvePath(node, out var path) == false || path is null)
                    return null;

                // A path rooted at a traversal alias addresses an element of an array, not the document; and the
                // document itself is no path a filter compares.
                return CosmosDocumentPaths.From(path, _rootAlias, elements: true) is JsonDocumentPath document && document.IsRoot == false ? document : null;
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
