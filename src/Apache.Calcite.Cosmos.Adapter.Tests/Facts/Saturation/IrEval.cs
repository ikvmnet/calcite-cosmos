using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The typed IR's meaning: a Kleene evaluator of a <see cref="SqlPredicate"/> over one document, written from what
    /// Calcite was measured to do and independent of <c>PredicateAtom</c>, which it is the ground truth for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A predicate takes one of four values on a row</b>: TRUE, FALSE, UNKNOWN, or raises — Calcite fails the query.
    /// The connectives are decided first: a conjunction with a FALSE arm is FALSE and a disjunction with a TRUE arm is
    /// TRUE whatever another arm does. Calcite evaluates both arms of a nullable one, and raises there
    /// (<c>Calcite.ConnectivesAreKleene</c>); <see cref="Raises"/> marks such a row, which the checks against Calcite
    /// leave out. Otherwise a raise anywhere raises. So a verdict checked against this is checked against the answer
    /// Calcite is bound to give wherever it gives one.
    /// </para>
    /// <para>
    /// <b>What each reading does was measured</b> (<c>IrEvalCalciteTests</c> holds it to Calcite's runtime):
    /// </para>
    /// <list type="bullet">
    /// <item><c>text</c> renders a string as itself, a boolean as <c>true</c> or <c>false</c>, a number Jackson reads as
    /// an integer in its digits and any other as Java spells a double — <c>30.0</c>, <c>1.0E30</c> — and an absent path,
    /// a null, an object and an array as SQL null;</item>
    /// <item><c>typed</c> casts the value Jackson read and raises on any other class: <c>RETURNING INTEGER</c> takes an
    /// int-sized integer, <c>BIGINT</c> only one beyond an int, <c>DOUBLE</c> only a number written with a point or an
    /// exponent, <c>BOOLEAN</c> a boolean, and <c>DECIMAL</c> parses the rendering, a string of digits included, and is
    /// compared with a literal cast to its scale of zero;</item>
    /// <item>a numeric cast over <c>text</c> parses the rendering, trimmed: <c>INTEGER</c> and <c>BIGINT</c> as Java parses
    /// an integer, so <c>30.0</c> and <c>2.5</c> raise; <c>DOUBLE</c> as Java parses a double. Its null test is read as
    /// its operand's, which Calcite simplifies it to where it stands alone; beside other conjuncts it sometimes makes the
    /// parse, and <see cref="Raises"/> counts that row as one an order of evaluation decides;</item>
    /// <item>a column typed as <c>typed</c> reads (<see cref="TypedAs.Column"/>) holds what the adapter's reader makes of
    /// the value, which is not what <c>RETURNING</c> asserts (<c>Calcite.ColumnReadsWholeNumbersAsItsType</c>);</item>
    /// <item><c>raw</c> is one of three columns (<see cref="RawAs"/>), whose comparisons and null were measured
    /// (<c>Calcite.RawStringEqualityIsOrdinal</c>, <c>Calcite.RawNullIsSqlNull</c>), and so was the cast of one to text
    /// (<c>Calcite.CastOfARawValueToText</c>).</item>
    /// </list>
    /// <para>
    /// <b>What was not measured is a coin</b> (<see cref="World"/>): a raw value compared with a parameter, a raw number
    /// in a <c>VARIANT</c> compared with a number whose SQL type the IR does not keep, an <c>ANY</c>'s structure cast to
    /// text, the cast to text over a raw value read through no named column, which is the facts graph's rendering, and a
    /// numeric cast over one where it is not a number.
    /// </para>
    /// </remarks>
    static class IrEval
    {

        const Truths T = Truths.True;
        const Truths F = Truths.False;
        const Truths U = Truths.Unknown;
        const Truths R = Truths.Raises;

        /// <summary>How a <c>typed</c> reading's value reaches Calcite, which the IR does not say.</summary>
        public enum TypedAs
        {

            /// <summary><c>JSON_VALUE … RETURNING T</c>.</summary>
            Returning,

            /// <summary>A column typed <c>T</c>, which the adapter's reader fills (<c>CosmosJson.GetValue</c>).</summary>
            Column,

        }

        /// <summary>
        /// The column a <c>raw</c> reading reads, which the IR does not say (<c>CosmosPredicateLoader</c> reads all three as
        /// one): a promoted path's <c>VARIANT</c>, an <c>ANY</c>, or a <c>VARCHAR</c> column read as the value it holds.
        /// </summary>
        public enum RawAs
        {

            Variant,

            Any,

            Varchar,

        }

        /// <summary>
        /// Returns the truth value a predicate takes on a document, its raw readings read through one column or, where
        /// none is named, through <c>VARIANT</c> or <c>ANY</c> by the world's coin.
        /// </summary>
        /// <remarks>
        /// Unnamed, a raw value is read as the facts graph's raw reading answers it: by the columns whose <c>=</c> and null
        /// test never raise. A <c>VARCHAR</c> column's read and a raw ordering raise where that reading says they do not
        /// (<c>TODO.md</c>), so they are read through a named column alone, as <see cref="RawModes"/> names one for the
        /// cells, and unnamed an ordering stays the coin that reading is.
        /// </remarks>
        public static Truths Truth(SqlPredicate predicate, J.Obj document, World world, TypedAs typed = TypedAs.Returning, RawAs? raw = null)
        {
            switch (predicate)
            {
                case SqlPredicate.Not n:
                    return Swap(Truth(n.Operand, document, world, typed, raw));

                case SqlPredicate.And a:
                    return All(a.Arms.Select(arm => Truth(arm, document, world, typed, raw)).ToArray());

                case SqlPredicate.Or o:
                    return Any(o.Arms.Select(arm => Truth(arm, document, world, typed, raw)).ToArray());

                case SqlPredicate.Opaque o:
                    return world.Choose(T | F | U, "opaque#" + o.Id + "@" + Documents.Json(document));

                // A value cast to VARIANT (Calcite.VariantEqualsOnlyItsOwnBox, G8): equal to a boxed literal within its own box,
                // as what it holds is to the literal, and unequal to it in any other; a raw value is boxed by the kind it holds,
                // so within the literal's kind's box the raw value compared with the literal decides. Null where it holds null.
                case SqlPredicate.Compare { Op: PredicateAtom.Equal or PredicateAtom.NotEqual, Right: SqlValue.Box { Value: SqlValue.Literal literal } right } c:
                {
                    var held = c.Left is SqlValue.Box box ? box.Value : c.Left;
                    var alike = c.Left is SqlValue.Box ? BoxOf(Apache.Calcite.Cosmos.Facts.Rendering.RenderingFamily.ReaderOf(held)) is { } own && own == BoxOf(right.Type)
                        : BoxOf(literal.Kind) is { } kind && kind == BoxOf(right.Type);
                    if (alike)
                        return Truth(new SqlPredicate.Compare(c.Op, held, literal), document, world, typed, raw);

                    var absent = IsRaw(held) ? At(document, PathOf(held)) is null or J.Null : Read(held, document, world, typed) is SqlDatum.NullDatum;
                    return absent ? U : c.Op == PredicateAtom.Equal ? F : T;
                }

                case SqlPredicate.IsNull { Value: SqlValue.Box box }:
                    return Truth(new SqlPredicate.IsNull(box.Value), document, world, typed, raw);

                // A box no case above reads, its order or two readings boxed (G8): what Calcite answers no row says, so it is
                // any answer, as the opaque conjunct the loader made of it before was.
                case SqlPredicate.Compare c when c.Left is SqlValue.Box || c.Right is SqlValue.Box:
                    return world.Choose(T | F | U, "box " + c + "@" + Documents.Json(document));

                // A comparison with SQL's null (G8): UNKNOWN, raising where reading the other side raises.
                case SqlPredicate.Compare { Right: SqlValue.Null } c:
                    return IsRaw(c.Left) == false && Read(c.Left, document, world, typed) is SqlDatum.Raised ? R : U;

                case SqlPredicate.Compare c when IsRaw(c.Left):
                    return RawCompare(c.Op, c.Left, c.Right, document, world, raw);

                case SqlPredicate.Compare c:
                    return Compare(c.Op, Read(c.Left, document, world, typed), c.Right is SqlValue.Literal or SqlValue.Parameter
                        ? Operand(c.Left, Read(c.Right, document, world, typed)) : Read(c.Right, document, world, typed));

                case SqlPredicate.In i when IsRaw(i.Value):
                    return Any(i.Points.Select(point => RawCompare(PredicateAtom.Equal, i.Value, new SqlValue.Literal(point), document, world, raw)).ToArray());

                case SqlPredicate.In i:
                {
                    var value = Read(i.Value, document, world, typed);
                    return Any(i.Points.Select(point => Compare(PredicateAtom.Equal, value, Operand(i.Value, Datum(point)))).ToArray());
                }

                case SqlPredicate.IsNull n when IsRaw(n.Value):
                    return RawIsNull(n.Value, document, world, raw);

                // Calcite simplifies the null test of a cast to the null test of what it casts, a cast being null exactly
                // where its operand is, and so never makes the parse that would have raised.
                case SqlPredicate.IsNull { Value: SqlValue.Cast { Value: SqlValue.Text cast } }:
                    return Rendering(At(document, cast.Path)) is null ? T : F;

                case SqlPredicate.IsNull n:
                    return Read(n.Value, document, world, typed) switch
                    {
                        SqlDatum.NullDatum => T,
                        SqlDatum.Raised => R,
                        _ => F,
                    };

                // The service's test of the document, which the in-process body reads too: a null is defined.
                case SqlPredicate.IsDefined d:
                    return At(document, d.Value.PathRead!.Value) is null ? F : T;

                // The in-process bodies, by the kind at the path (Calcite.TypeTestsAreTheirBodies): never UNKNOWN.
                case SqlPredicate.TypeTest t:
                    return Kinds(t.Test, At(document, t.Value.PathRead!.Value)) ? T : F;

                case SqlPredicate.Affix a:
                    return Like(a, document, world, typed, raw);

                // The first arm whose condition is TRUE, FALSE and UNKNOWN alike passing it by (Calcite.CaseTakesTheFirstTrueArm).
                case SqlPredicate.Case c:
                    return Truth(c.Condition, document, world, typed, raw) switch
                    {
                        T => Truth(c.Then, document, world, typed, raw),
                        R => R,
                        _ => Truth(c.Else, document, world, typed, raw),
                    };

                case SqlPredicate.Constant k:
                    return k.Truth switch { true => T, false => F, null => U };

                default:
                    throw new NotSupportedException(predicate.GetType().Name);
            }
        }

        /// <summary>Whether a type test's body is TRUE of a stored value, <c>null</c> where the path is absent.</summary>
        static bool Kinds(string test, J? stored) => (test, stored) switch
        {
            (_, null) => false,
            ("IS_NULL", J.Null) or ("IS_STRING", J.Str) or ("IS_NUMBER", J.Num or J.Huge) or ("IS_BOOL", J.Bool) or ("IS_OBJECT", J.Obj) or ("IS_ARRAY", J.Arr) => true,
            ("IS_PRIMITIVE", J.Null or J.Str or J.Num or J.Huge or J.Bool) => true,
            ("IS_NULL" or "IS_STRING" or "IS_NUMBER" or "IS_BOOL" or "IS_OBJECT" or "IS_ARRAY" or "IS_PRIMITIVE", _) => false,
            _ => throw new NotSupportedException(test),
        };

        /// <summary>
        /// <c>LIKE</c> over text, as Calcite's runtime matches it: UNKNOWN over null. A cast to text of a raw value is read
        /// through its column, or unnamed as either a <c>VARIANT</c> or an <c>ANY</c> reads it, an <c>ANY</c>'s structure as
        /// Java's map or list renders, between its brackets, a coin where the pattern may match that; and a cast of a
        /// rendering to a character type, whose length the IR does not keep, is a coin where it is not null.
        /// </summary>
        static Truths Like(SqlPredicate.Affix affix, J.Obj document, World world, TypedAs typed, RawAs? raw)
        {
            Truths Of(SqlDatum? text, J? stored = null) => text switch
            {
                null when stored is J.Obj or J.Arr => Bracketed(stored is J.Arr ? "[]" : "{}", affix.Pattern) ? T | F : F,
                null => T | F,
                SqlDatum.NullDatum => U,
                SqlDatum.Raised => R,
                SqlDatum.Text t => new org.apache.calcite.runtime.SqlFunctions.LikeFunction().like(t.Value, affix.Pattern) ? T : F,
                _ => throw new InvalidOperationException($"{affix} reads no text."),
            };

            var question = affix + " @ " + Documents.Json(document);
            switch (affix.Value)
            {
                case SqlValue.Cast { Value: SqlValue.Raw r }:
                    raw = Own(r, raw);
                    var stored = At(document, r.Path);
                    return stored is null ? U : world.Choose(raw is RawAs one ? Of(CastText(stored, one), stored) : Of(CastText(stored, RawAs.Variant), stored) | Of(CastText(stored, RawAs.Any), stored), question);

                case SqlValue.Cast { Value: SqlValue.Text text }:
                    return Rendering(At(document, text.Path)) is null ? U : world.Choose(T | F, question);

                default:
                    return Of(Read(affix.Value, document, world, typed));
            }

            // Whether a pattern of runs and % may match some text between the brackets: its first run opens, its last closes.
            static bool Bracketed(string brackets, string pattern)
            {
                var runs = pattern.Split('%');
                return pattern.IndexOfAny(['_', '\\']) >= 0
                    || (runs[0].Length == 0 || runs[0][0] == brackets[0]) && (runs[^1].Length == 0 || runs[^1][^1] == brackets[1]) && (runs.Length > 1 || pattern.Length >= 2);
            }
        }

        /// <summary>
        /// The columns to read a predicate's raw values through: where it casts one to text, orders one or compares one with a
        /// number or a boolean, which each column answers or raises on its own way, a <c>VARIANT</c>, an <c>ANY</c> and,
        /// where every raw literal is a string, a <c>VARCHAR</c>; otherwise none named.
        /// </summary>
        public static IReadOnlyList<RawAs?> RawModes(SqlPredicate predicate)
        {
            static (SqlValue? Value, string[] Literals) Reading(SqlPredicate term) => term switch
            {
                SqlPredicate.Compare c => (c.Left, c.Right is SqlValue.Literal l ? [l.Value] : Uncast(c.Right) is SqlValue.Literal u ? [u.Value] : []),
                SqlPredicate.In i => (i.Value, i.Points.ToArray()),
                SqlPredicate.IsNull n => (n.Value, []),
                _ => (null, []),
            };

            var read = predicate.Subterms.Select(Reading).ToList();
            var named = predicate.Subterms.Any(t => t is SqlPredicate.Compare { Left: SqlValue.Raw, Op: not (PredicateAtom.Equal or PredicateAtom.NotEqual) })
                || read.Any(r => r.Value is SqlValue.Cast { Target: "VARCHAR" or "CHAR", Value: SqlValue.Raw } || r.Value is SqlValue.Raw && r.Literals.Any(l => IsString(l) == false));
            if (named == false)
                return [null];

            return read.Where(r => r.Value is SqlValue.Raw).SelectMany(r => r.Literals).All(IsString) ? [RawAs.Variant, RawAs.Any, RawAs.Varchar] : [RawAs.Variant, RawAs.Any];
        }

        /// <summary>The box each reading, kind of stored value and value cast is held in, as its row says (G8).</summary>
        static readonly IReadOnlyList<(string Kind, string Box)> Boxes = Apache.Calcite.Cosmos.Adapter.Sql.CosmosAxiomRows.ReadBoxes(Apache.Calcite.Cosmos.Adapter.Sql.CosmosAxioms.All);

        static string? BoxOf(string? kind) => kind is null ? null : Boxes.Where(b => b.Kind == kind).Select(b => b.Box).FirstOrDefault();

        /// <summary>The literal a number Calcite wrote cast casts (G8), or a box holds, or the value itself.</summary>
        static SqlValue Uncast(SqlValue value) => value switch
        {
            SqlValue.Cast { PathRead: null } cast => Uncast(cast.Value),
            SqlValue.DecimalCast cast => Uncast(cast.Value),
            SqlValue.Box { PathRead: null } box => Uncast(box.Value),
            _ => value,
        };

        /// <summary><c>NOT</c>: TRUE and FALSE swap, UNKNOWN and a raise stay.</summary>
        public static Truths Swap(Truths truth) => truth == T ? F : truth == F ? T : truth;

        /// <summary>A conjunction, decided first by a FALSE arm.</summary>
        public static Truths All(IReadOnlyList<Truths> arms) =>
            arms.Contains(F) ? F : arms.Contains(R) ? R : arms.Contains(U) ? U : T;

        /// <summary>A disjunction, decided first by a TRUE arm.</summary>
        public static Truths Any(IReadOnlyList<Truths> arms) =>
            arms.Contains(T) ? T : arms.Contains(R) ? R : arms.Contains(U) ? U : F;

        /// <summary>
        /// Determines whether evaluating a predicate on a document reaches a raise anywhere within it, decided or not: a
        /// row where Calcite's order of evaluation chooses between an answer and a failure. The null test of a cast that
        /// would raise is one: Calcite simplifies it to its operand's where it stands alone, and not always beside others.
        /// </summary>
        public static bool Raises(SqlPredicate predicate, J.Obj document, World world, TypedAs typed = TypedAs.Returning, RawAs? raw = null) =>
            predicate.Subterms.Any(term => term switch
            {
                SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not => false,
                SqlPredicate.IsNull { Value: SqlValue.Cast { Value: SqlValue.Text } cast } => Read(cast, document, world, typed) is SqlDatum.Raised,
                _ => Truth(term, document, world, typed, raw) == R,
            });

        /// <summary>
        /// The value a literal is compared as: itself, but beside <c>RETURNING DECIMAL</c>, which Calcite types
        /// <c>DECIMAL(19, 0)</c>, cast to that scale — <c>2.5</c> is compared as <c>2</c>, and the value read keeps its
        /// fraction.
        /// </summary>
        static SqlDatum Operand(SqlValue reading, SqlDatum operand) =>
            reading is SqlValue.Typed { Type: "DECIMAL" } && operand is SqlDatum.Exact e ? new SqlDatum.Exact(decimal.Truncate(e.Value)) : operand;

        /// <summary>
        /// Returns the value at a path of a document, or <c>null</c> where it is absent.
        /// </summary>
        public static J? At(J document, JsonDocumentPath path)
        {
            J? current = document;
            foreach (var segment in path.Segments)
                current = segment switch
                {
                    string name => current is J.Obj o && o.Properties.TryGetValue(name, out var child) ? child : null,
                    long index => current is J.Arr a && index < a.Elements.Count ? a.Elements[(int)index] : null,
                    _ => null,
                };

            return current;
        }

        /// <summary>
        /// Returns what <c>JSON_VALUE</c> renders a stored value as, or <c>null</c> for SQL null.
        /// </summary>
        public static string? Rendering(J? value) => value switch
        {
            J.Str s => s.Value,
            J.Bool b => b.Value ? "true" : "false",
            J.Num n when IntegerSpelled(n) => n.Value.ToString(CultureInfo.InvariantCulture),
            J.Num n => JavaDouble(DoubleBound.Of(n.Value)),
            J.Huge h => JavaDouble(h.Value),
            _ => null,
        };

        /// <summary>
        /// Whether Jackson reads a stored number as an integer: written without a point or an exponent.
        /// </summary>
        static bool IntegerSpelled(J.Num number) => number.Value.ToString(CultureInfo.InvariantCulture).IndexOfAny(new[] { '.', 'E', 'e' }) < 0;

        /// <summary>A number written as an integer within a long: what a reader boxes as a long.</summary>
        static bool LongSpelled(J.Num number) => IntegerSpelled(number) && number.Value >= long.MinValue && number.Value <= long.MaxValue;

        /// <summary>
        /// Spells a double as Java's <c>Double.toString</c> does: plain between a thousandth and ten million, with at least
        /// one digit after the point, and otherwise <c>d.dddE±n</c>.
        /// </summary>
        public static string JavaDouble(double value)
        {
            if (double.IsNaN(value))
                return "NaN";
            if (double.IsPositiveInfinity(value))
                return "Infinity";
            if (double.IsNegativeInfinity(value))
                return "-Infinity";
            if (value == 0)
                return double.IsNegative(value) ? "-0.0" : "0.0";

            var sign = value < 0 ? "-" : "";
            var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
            var e = shortest.IndexOf('E');
            var mantissa = e < 0 ? shortest : shortest.Substring(0, e);
            var exponent = e < 0 ? 0 : int.Parse(shortest.Substring(e + 1), CultureInfo.InvariantCulture);
            var point = mantissa.IndexOf('.');
            var whole = point < 0 ? mantissa.Length : point;
            var digits = mantissa.Replace(".", "");
            var leading = digits.TakeWhile(c => c == '0').Count();
            digits = digits.Substring(leading).TrimEnd('0');
            if (digits.Length == 0)
                digits = "0";

            // The value is 0.d1d2… × 10^(scientific + 1).
            var scientific = whole - 1 - leading + exponent;
            if (scientific >= -3 && scientific < 7)
            {
                if (scientific >= 0)
                {
                    var integral = digits.Length > scientific + 1 ? digits.Substring(0, scientific + 1) : digits.PadRight(scientific + 1, '0');
                    var fraction = digits.Length > scientific + 1 ? digits.Substring(scientific + 1) : "0";
                    return sign + integral + "." + fraction;
                }

                return sign + "0." + new string('0', -scientific - 1) + digits;
            }

            return sign + digits[0] + "." + (digits.Length > 1 ? digits.Substring(1) : "0") + "E" + scientific.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Reads a value that is not a raw reading: a rendering, a <c>RETURNING</c>, a cast over a rendering, a literal or a
        /// parameter.
        /// </summary>
        public static SqlDatum Read(SqlValue value, J.Obj document, World world, TypedAs typed = TypedAs.Returning)
        {
            switch (value)
            {
                case SqlValue.Text t:
                    return Rendering(At(document, t.Path)) is string rendered ? new SqlDatum.Text(rendered) : SqlDatum.Null;

                // RETURNING VARCHAR and CHAR read a string as itself, unpadded, and a null and an absent path as null
                // (Calcite.TextPlacesCompareAsTheirStrings); what they make of any other class was not measured, a coin.
                case SqlValue.Typed { Type: "VARCHAR" or "CHAR" } t when typed == TypedAs.Returning:
                    return At(document, t.Path) switch
                    {
                        null or J.Null => SqlDatum.Null,
                        J.Str s => new SqlDatum.Text(s.Value),
                        var other => world.Unread(t.Type + " " + Documents.Json(document)),
                    };

                case SqlValue.Typed t:
                    return typed == TypedAs.Column ? Column(At(document, t.Path), t.Type) : Returning(At(document, t.Path), t.Type);

                case SqlValue.Cast { Value: SqlValue.Text text } c:
                    return Parse(Rendering(At(document, text.Path)), c.Target);

                case SqlValue.Literal l:
                    return Datum(l.Value);

                case SqlValue.Null:
                    return SqlDatum.Null;

                // A number Calcite wrote cast, as a loading that reads places reads one (G8): to DOUBLE the double nearest it.
                case SqlValue.Cast { Target: "DOUBLE", PathRead: null } c:
                    return Read(c.Value, document, world, typed) is SqlDatum.Exact e
                        ? new SqlDatum.Double(double.Parse(e.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))
                        : Read(c.Value, document, world, typed);

                case SqlValue.DecimalCast d:
                    return Read(d.Value, document, world, typed) switch
                    {
                        SqlDatum.Exact number => CastDecimal(number.Value, d.Precision, d.Scale),
                        SqlDatum.Double x when TryExactly(x.Value, out var exact) => CastDecimal(exact, d.Precision, d.Scale),
                        var other => other,
                    };

                // A VARCHAR column reads a string as itself, a null as null, and fails over anything else (Calcite.RawNullIsSqlNull).
                case SqlValue.Raw { Column: "VARCHAR" } r:
                    return At(document, r.Path) switch { null or J.Null => SqlDatum.Null, J.Str s => new SqlDatum.Text(s.Value), _ => SqlDatum.Raise };

                case SqlValue.Parameter p:
                    return world.Parameter(p);

                case SqlValue.Call c:
                    return Call(c, document, world);

                // The arm of the first condition that is TRUE, else the last.
                case SqlValue.Case c:
                {
                    for (var i = 0; i < c.Conditions.Count; i++)
                        switch (Truth(c.Conditions[i], document, world, typed))
                        {
                            case T:
                                return Read(c.Arms[i], document, world, typed);
                            case R:
                                return SqlDatum.Raise;
                        }

                    return Read(c.Arms[^1], document, world, typed);
                }

                default:
                    throw new NotSupportedException(value.ToString());
            }
        }

        /// <summary>
        /// A number cast to <c>DECIMAL(p, s)</c> as Calcite casts one: set to <c>s</c> places rounding toward zero, and raising
        /// where what that leaves has more than <c>p</c> digits (<c>CalciteJsonValueMeasurementTests</c>, the literal a
        /// comparison through <c>RETURNING DECIMAL</c> casts down).
        /// </summary>
        static SqlDatum CastDecimal(decimal value, long precision, long scale)
        {
            var cast = new java.math.BigDecimal(value.ToString(CultureInfo.InvariantCulture)).setScale(checked((int)scale), java.math.RoundingMode.DOWN);
            return cast.precision() > precision ? SqlDatum.Raise : new SqlDatum.Exact(decimal.Parse(cast.toPlainString(), CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A string function of values, as Calcite computes it in process: by its runtime's own functions
        /// (<c>org.apache.calcite.runtime.SqlFunctions</c>), null over a null operand, and raising where they raise; a
        /// count in UTF-16 units and a position from 1, as exact numbers; and a typed arithmetic operator at the call's
        /// type (<see cref="Arithmetic"/>). A <c>CASE</c> read as its arms alone, its conditions not read, takes the arm a
        /// coin chooses.
        /// </summary>
        static SqlDatum Call(SqlValue.Call call, J.Obj document, World world)
        {
            if (call.Function == "CASE")
                return Read(call.Operands[world.Pick(call.Operands.Count, call + " @ " + Documents.Json(document))], document, world);

            var operands = call.Operands.Select(operand => Read(operand, document, world)).ToArray();
            if (operands.Any(operand => operand is SqlDatum.Raised))
                return SqlDatum.Raise;

            if (operands.Any(operand => operand is SqlDatum.NullDatum))
                return SqlDatum.Null;

            string Text(int i) => operands[i] is SqlDatum.Text t ? t.Value : throw new InvalidOperationException($"{call} reads no text at {i}: the generator built a call the loader never does.");
            int Count(int i) => operands[i] is SqlDatum.Exact e ? (int)e.Value : throw new InvalidOperationException($"{call} reads no count at {i}.");

            if (call.Function is "+" or "-" or "*" or "/" or "%" or "MOD")
                return Arithmetic(call.Function, call.Type ?? throw new InvalidOperationException($"{call} has no type: the loader types every arithmetic place."), operands[0], operands[1]);

            try
            {
                if (call.Function is "CHAR_LENGTH" or "CHARACTER_LENGTH" or "LENGTH")
                    return new SqlDatum.Exact(org.apache.calcite.runtime.SqlFunctions.charLength(Text(0)));

                if (call.Function == "POSITION")
                    return new SqlDatum.Exact(org.apache.calcite.runtime.SqlFunctions.position(Text(0), Text(1)));

                return new SqlDatum.Text(call.Function switch
                {
                    "||" or "CONCAT" => string.Concat(Enumerable.Range(0, operands.Length).Select(Text)),
                    "UPPER" => org.apache.calcite.runtime.SqlFunctions.upper(Text(0)),
                    "LOWER" => org.apache.calcite.runtime.SqlFunctions.lower(Text(0)),
                    "SUBSTRING" => org.apache.calcite.runtime.SqlFunctions.substring(Text(0), Count(1), Count(2)),
                    "LEFT" => org.apache.calcite.runtime.SqlFunctions.left(Text(0), Count(1)),
                    "RIGHT" => org.apache.calcite.runtime.SqlFunctions.right(Text(0), Count(1)),
                    "REVERSE" => org.apache.calcite.runtime.SqlFunctions.reverse(Text(0)),
                    "REPEAT" => org.apache.calcite.runtime.SqlFunctions.repeat(Text(0), Count(1)),
                    "REPLACE" => org.apache.calcite.runtime.SqlFunctions.replace(Text(0), Text(1), Text(2), true),
                    "TRIM BOTH" => org.apache.calcite.runtime.SqlFunctions.trim(true, true, Text(0), Text(1)),
                    "TRIM LEADING" => org.apache.calcite.runtime.SqlFunctions.trim(true, false, Text(0), Text(1)),
                    "TRIM TRAILING" => org.apache.calcite.runtime.SqlFunctions.trim(false, true, Text(0), Text(1)),
                    _ => throw new NotSupportedException(call.Function),
                });
            }
            catch (java.lang.RuntimeException)
            {
                return SqlDatum.Raise;
            }
        }

        /// <summary>
        /// A typed arithmetic operator over two numbers as Calcite computes it at the call's type: an integer type wraps
        /// at its width and divides truncating toward zero (<c>Calcite.IntegerArithmeticWraps</c>), raising over a zero
        /// divisor; a <c>DECIMAL</c> is exact (<c>Calcite.DecimalArithmeticIsExact</c>), raising past what a decimal
        /// holds and over a zero divisor; a double is IEEE's, an infinity over a zero divisor. A <c>DECIMAL</c> quotient
        /// takes a scale this model does not, so it is not modelled.
        /// </summary>
        static SqlDatum Arithmetic(string op, string type, SqlDatum left, SqlDatum right)
        {
            static double Real(SqlDatum d) => d switch
            {
                SqlDatum.Exact e => (double)e.Value,
                SqlDatum.Double x => x.Value,
                SqlDatum.Decimal s => Renderings.ParseDouble(s.Spelled) ?? throw new InvalidOperationException($"{s} is no number."),
                _ => throw new InvalidOperationException($"{d} is no number."),
            };

            static decimal Whole(SqlDatum d) => d is SqlDatum.Exact e ? e.Value : throw new NotSupportedException($"{d} is no exact number this model holds.");

            switch (type)
            {
                case "DOUBLE" or "FLOAT" or "REAL":
                {
                    var (a, b) = (Real(left), Real(right));
                    return new SqlDatum.Double(op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => a % b });
                }

                case "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT":
                {
                    var (a, b) = ((long)Whole(left), (long)Whole(right));
                    if (op is "/" or "%" or "MOD" && b == 0)
                        return SqlDatum.Raise;

                    // Java wraps the one quotient C# refuses: the least long over -1 is itself, its remainder 0.
                    var wide = a == long.MinValue && b == -1 ? (op == "/" ? a : 0)
                        : unchecked(op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => a % b });
                    return new SqlDatum.Exact(type switch { "TINYINT" => unchecked((sbyte)wide), "SMALLINT" => unchecked((short)wide), "INTEGER" => unchecked((int)wide), _ => wide });
                }

                case "DECIMAL" when op != "/":
                {
                    var (a, b) = (Whole(left), Whole(right));
                    if (op is "%" or "MOD" && b == 0)
                        return SqlDatum.Raise;

                    try
                    {
                        return new SqlDatum.Exact(op switch { "+" => a + b, "-" => a - b, "*" => a * b, _ => a % b });
                    }
                    catch (OverflowException)
                    {
                        return SqlDatum.Raise;
                    }
                }

                default:
                    throw new NotSupportedException($"{op} at {type}");
            }
        }

        /// <summary>
        /// Denotes a value on a document, for comparing two values a class holds: what a reading yields, or for a raw
        /// reading, which no coin has to be tossed for, the stored value it reads.
        /// </summary>
        public static string Denote(SqlValue value, J.Obj document, World world) => value switch
        {
            SqlValue.Raw r => "raw " + Show(At(document, r.Path)),
            SqlValue.Cast { Value: SqlValue.Raw r } c => "cast " + c.Target + " " + Show(At(document, r.Path)),
            _ => Read(value, document, world).ToString(),
        };

        static string Show(J? value) => value is null ? "absent" : Documents.Json(value);

        /// <summary>
        /// Returns a literal in the theory's spelling as the value Calcite compares.
        /// </summary>
        public static SqlDatum Datum(string literal) => TypeOf(literal) switch
        {
            NullType => SqlDatum.Null,
            StringType => new SqlDatum.Text(StringOf(literal)),
            BooleanType => new SqlDatum.Boolean(literal == Of(true)),
            _ when TryNumberOf(literal, out var exact) => new SqlDatum.Exact(exact),
            _ => new SqlDatum.Double(ApproximateOf(literal)!.Value),
        };

        /// <summary>
        /// <c>JSON_VALUE … RETURNING type</c>: the value Jackson read, cast to the type's class, which raises on any other.
        /// </summary>
        static SqlDatum Returning(J? value, string type)
        {
            if (value is null or J.Null or J.Obj or J.Arr)
                return SqlDatum.Null;

            switch (type)
            {
                case "INTEGER":
                    return value is J.Num n && IntegerSpelled(n) && n.Value >= int.MinValue && n.Value <= int.MaxValue ? new SqlDatum.Exact(n.Value) : SqlDatum.Raise;

                case "BIGINT":
                    // Jackson reads an int-sized number as an Integer, which is no Long.
                    return value is J.Num l && IntegerSpelled(l) && (l.Value < int.MinValue || l.Value > int.MaxValue) && l.Value >= long.MinValue && l.Value <= long.MaxValue
                        ? new SqlDatum.Exact(l.Value) : SqlDatum.Raise;

                case "DOUBLE":
                    return value switch
                    {
                        J.Num d when IntegerSpelled(d) == false => new SqlDatum.Double(DoubleBound.Of(d.Value)),
                        J.Huge h => new SqlDatum.Double(h.Value),
                        _ => SqlDatum.Raise,
                    };

                case "DECIMAL":
                    return Parse(Rendering(value), "DECIMAL");

                case "BOOLEAN":
                    return value is J.Bool b ? new SqlDatum.Boolean(b.Value) : SqlDatum.Raise;

                default:
                    // SMALLINT, TINYINT, REAL, FLOAT: Jackson never reads a value of their class.
                    return SqlDatum.Raise;
            }
        }

        /// <summary>
        /// A column typed <c>type</c>: what <c>CosmosJson.GetInteger</c>, <c>GetDouble</c>, <c>GetDecimal</c> and
        /// <c>GetBoolean</c> read, and a failed read, which fails the statement, where they refuse. An integer type holds a
        /// whole number in its range, a stored <c>42.0</c> among them; a floating type any number; <c>DECIMAL</c> a number's
        /// text exactly; <c>BOOLEAN</c> a boolean.
        /// </summary>
        static SqlDatum Column(J? value, string type)
        {
            if (value is null or J.Null)
                return SqlDatum.Null;

            switch (type)
            {
                case "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT":
                {
                    (decimal least, decimal most) = type switch
                    {
                        "TINYINT" => (sbyte.MinValue, sbyte.MaxValue),
                        "SMALLINT" => (short.MinValue, short.MaxValue),
                        "INTEGER" => (int.MinValue, int.MaxValue),
                        _ => (long.MinValue, long.MaxValue),
                    };

                    // Written as an int64, read as one; otherwise as its double, whole and checked in doubles. A J.Huge is
                    // beyond any of them.
                    if (value is not J.Num n)
                        return SqlDatum.Raise;
                    if (LongSpelled(n))
                        return n.Value >= least && n.Value <= most ? new SqlDatum.Exact(n.Value) : SqlDatum.Raise;

                    var d = DoubleBound.Of(n.Value);
                    return Math.Floor(d) == d && d >= (double)least && d < (double)most + 1.0 ? new SqlDatum.Exact((long)d) : SqlDatum.Raise;
                }

                case "FLOAT" or "DOUBLE":
                    return value switch
                    {
                        J.Num n => new SqlDatum.Double(DoubleBound.Of(n.Value)),
                        J.Huge h => new SqlDatum.Double(h.Value),
                        _ => SqlDatum.Raise,
                    };

                case "DECIMAL":
                    return value switch
                    {
                        J.Num x => new SqlDatum.Exact(x.Value),
                        J.Huge h => new SqlDatum.Double(h.Value),
                        _ => SqlDatum.Raise,
                    };

                case "BOOLEAN":
                    return value is J.Bool b ? new SqlDatum.Boolean(b.Value) : SqlDatum.Raise;

                default:
                    throw new NotSupportedException($"A column typed {type} was not measured.");
            }
        }

        /// <summary>
        /// A cast of text to a number, as Calcite converts one: by the parse of its type (<c>Renderings</c>), which raises on
        /// what it cannot parse.
        /// </summary>
        static SqlDatum Parse(string? text, string target) => text is null ? SqlDatum.Null : target switch
        {
            "BIGINT" when Renderings.ReadsAsTimestamp(text) => throw new NotSupportedException("BIGINT reads it as a timestamp in the local time zone"),
            "INTEGER" or "BIGINT" => Renderings.ParseInteger(text, target == "INTEGER" ? int.MinValue : long.MinValue, target == "INTEGER" ? int.MaxValue : long.MaxValue) is decimal whole
                ? new SqlDatum.Exact(whole) : SqlDatum.Raise,
            "DOUBLE" => Renderings.ParseDouble(text) is double d ? new SqlDatum.Double(d) : SqlDatum.Raise,
            "DECIMAL" => Renderings.ParseDecimal(text) is string number ? IsExactNumber(number) ? Datum(number) : new SqlDatum.Decimal(text) : SqlDatum.Raise,
            "BOOLEAN" => JavaBoolean(text) is bool truth ? new SqlDatum.Boolean(truth) : SqlDatum.Raise,
            _ => throw new NotSupportedException(target),
        };

        /// <summary>
        /// Calcite's parse of text as a boolean (<c>Calcite.CastToBooleanParses</c>): trimmed of every character up to
        /// U+0020, as Java's <c>String.trim</c> trims, then <c>TRUE</c> or <c>FALSE</c> in any ASCII case, <c>"falſe"</c>
        /// not folded; <c>null</c>, a raise, for anything else.
        /// </summary>
        static bool? JavaBoolean(string text)
        {
            var trimmed = text.Trim(Enumerable.Range(0, 33).Select(c => (char)c).ToArray());
            static bool Same(string a, string b) => a.Length == b.Length && a.Zip(b).All(p => p.First < 128 && char.ToUpperInvariant(p.First) == p.Second);

            return Same(trimmed, "TRUE") ? true : Same(trimmed, "FALSE") ? false : null;
        }

        /// <summary>
        /// A comparison of two values: UNKNOWN where either is null, a raise where either raises.
        /// </summary>
        public static Truths Compare(string op, SqlDatum left, SqlDatum right)
        {
            if (left is SqlDatum.Raised || right is SqlDatum.Raised)
                return R;

            if (left is SqlDatum.NullDatum || right is SqlDatum.NullDatum)
                return U;

            int order = (left, right) switch
            {
                (SqlDatum.Text a, SqlDatum.Text b) => Math.Sign(string.CompareOrdinal(a.Value, b.Value)),
                (SqlDatum.Exact a, SqlDatum.Exact b) => a.Value.CompareTo(b.Value),
                (SqlDatum.Decimal a, SqlDatum.Exact b) => Renderings.DecimalSide(a.Spelled, b.Value)!.Value,
                (SqlDatum.Exact a, SqlDatum.Decimal b) => -Renderings.DecimalSide(b.Spelled, a.Value)!.Value,
                (SqlDatum.Boolean a, SqlDatum.Boolean b) => a.Value.CompareTo(b.Value),
                _ when Number(left) is double a && Number(right) is double b => a.CompareTo(b),
                _ => throw new InvalidOperationException($"No comparison of {left} with {right}: the generator built one the loader never does."),
            };

            return Holds(op, order);
        }

        /// <summary>What a comparison answers of two values in this order.</summary>
        static Truths Holds(string op, int order) => op switch
        {
            PredicateAtom.Equal => order == 0,
            PredicateAtom.NotEqual => order != 0,
            PredicateAtom.Less => order < 0,
            PredicateAtom.LessOrEqual => order <= 0,
            PredicateAtom.Greater => order > 0,
            PredicateAtom.GreaterOrEqual => order >= 0,
            _ => throw new NotSupportedException(op),
        } ? T : F;

        static double? Number(SqlDatum datum) => datum switch
        {
            SqlDatum.Exact e => DoubleBound.Of(e.Value),
            SqlDatum.Double d => d.Value,
            SqlDatum.Decimal d => Renderings.ParseDouble(d.Spelled),
            _ => null,
        };

        /// <summary>
        /// Whether a value is read off the stored value itself rather than its rendering: <c>raw</c>, or a cast over it.
        /// </summary>
        public static bool IsRaw(SqlValue value) => value is SqlValue.Raw or SqlValue.Cast { Value: SqlValue.Raw };

        /// <summary>The column a raw reading names, which overrides the one asked for; the one asked for where it names none.</summary>
        public static RawAs? Own(SqlValue reading, RawAs? raw) => Named((reading switch { SqlValue.Raw r => r, SqlValue.Cast { Value: SqlValue.Raw r } => r, _ => null })?.Column) ?? raw;

        /// <summary>The column a raw reading's name for it stands for; <c>null</c> for none.</summary>
        public static RawAs? Named(string? column) => column switch
        {
            "VARIANT" => RawAs.Variant,
            "ANY" => RawAs.Any,
            "VARCHAR" => RawAs.Varchar,
            _ => null,
        };

        static JsonDocumentPath PathOf(SqlValue value) => value.PathRead ?? throw new InvalidOperationException(value + " reads no path.");

        /// <summary>
        /// A comparison over a raw reading, whose behaviour is a coin wherever it was not measured.
        /// </summary>
        static Truths RawCompare(string op, SqlValue reading, SqlValue operand, J.Obj document, World world, RawAs? raw)
        {
            raw = Own(reading, raw);
            var stored = At(document, PathOf(reading));

            // A number Calcite wrote cast (G8) is the value the cast makes, a cast that raises raising on every row; beside the
            // raw value itself, whose box a column compares by, what it compares as is not modelled.
            if (operand is SqlValue.Cast { PathRead: null } or SqlValue.DecimalCast)
            {
                var asked = reading + " " + op + " " + operand + " @ " + (stored is null ? "absent" : Documents.Json(stored));
                switch (Read(operand, document, world))
                {
                    case SqlDatum.Raised:
                        return R;
                    case var _ when reading is SqlValue.Raw:
                        return world.Choose(T | F | U | R, asked);
                    case SqlDatum.Exact e:
                        operand = new SqlValue.Literal(Of(e.Value));
                        break;
                    case SqlDatum.Double x:
                        operand = new SqlValue.Literal(Of(x.Value));
                        break;
                    case var other:
                        throw new NotSupportedException(other.ToString());
                }
            }
            if (reading is SqlValue.Raw && operand is SqlValue.Literal literal && (raw is not null || op is PredicateAtom.Equal or PredicateAtom.NotEqual))
            {
                // <> is = swapped, on the one coin.
                var asked = op == PredicateAtom.NotEqual ? PredicateAtom.Equal : op;
                var answers = raw is RawAs one
                    ? RawLiteral(asked, stored, literal.Value, one)
                    : RawLiteral(asked, stored, literal.Value, RawAs.Variant) | RawLiteral(asked, stored, literal.Value, RawAs.Any);
                var truth = world.Choose(answers, reading + " " + asked + " " + operand + " @ " + (stored is null ? "absent" : Documents.Json(stored)));
                return op == PredicateAtom.NotEqual ? Swap(truth) : truth;
            }

            if (stored is null)
                return U;

            var question = reading + " " + op + " " + operand + " @ " + Documents.Json(stored);
            if (operand is SqlValue.Parameter)
                return reading is SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") } && stored is J.Null or J.Obj or J.Arr
                    ? world.Choose(U | R, question)
                    : reading is SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") } && stored is J.Str or J.Bool
                        ? world.Choose(T | F | U | R, question)
                        : world.Choose(T | F | U, question);

            var value = ((SqlValue.Literal)operand).Value;
            switch (reading)
            {
                case SqlValue.Raw:
                    return world.Choose(T | F | U, question);

                case SqlValue.Cast { Target: "VARCHAR" or "CHAR" } when raw is RawAs column && CastText(stored, column) is SqlDatum text:
                    return Compare(op, text, Datum(value));

                case SqlValue.Cast { Target: "VARCHAR" or "CHAR" }:
                    return RawText(op, stored, StringOf(value), world, question);

                case SqlValue.Cast c:
                    return stored switch
                    {
                        J.Num or J.Huge => Compare(op, Converted(stored, c.Target), Datum(value)),
                        J.Null or J.Obj or J.Arr => world.Choose(U | R, question),
                        _ => world.Choose(T | F | R, question),
                    };

                default:
                    throw new NotSupportedException(reading.ToString());
            }
        }

        /// <summary>
        /// What <c>=</c> or an ordering of a raw value with a literal answers through one column, as measured
        /// (<c>Calcite.RawNullIsSqlNull</c>): a null is SQL null, a value is compared with a literal of its own type, a
        /// number in the box the reader built, and a string by its UTF-16 units (<c>Calcite.RawStringEqualityIsOrdinal</c>).
        /// </summary>
        static Truths RawLiteral(string op, J? stored, string literal, RawAs raw)
        {
            // A VARIANT has no order at run time: an ordering over one fails to compile, whatever the rows hold.
            if (raw == RawAs.Variant && op != PredicateAtom.Equal)
                return R;

            if (stored is null or J.Null)
                return U;

            if (raw == RawAs.Varchar)
            {
                // Beside anything but a string Calcite casts the column, which the loader reads as a cast, not as raw.
                if (IsString(literal) == false)
                    throw new InvalidOperationException($"A VARCHAR column is compared with {literal} through a cast: the generator built one the loader never does.");

                // The reader reads a string alone into a VARCHAR column, and fails the statement over anything else.
                if (stored is not J.Str)
                    return R;
            }

            // A number written as an integer within a long is boxed as a long, and any other as its double.
            var value = stored switch
            {
                J.Str s => new SqlDatum.Text(s.Value),
                J.Num n when LongSpelled(n) => new SqlDatum.Exact(n.Value),
                J.Num n => new SqlDatum.Double(DoubleBound.Of(n.Value)),
                J.Huge h => new SqlDatum.Double(h.Value),
                J.Bool b => new SqlDatum.Boolean(b.Value),
                _ => (SqlDatum?)null,
            };
            var datum = Datum(literal);

            // Unequal across types, a structure included; ANY raises on an ordering across them.
            if (value is null || Kind(value) != Kind(datum))
                return op == PredicateAtom.Equal ? F : R;

            // A double's box compares by its exact value. A VARIANT is equal to a number boxed with its own SQL type alone,
            // BIGINT for a long and DOUBLE for a double, and the IR keeps no literal's: equal by value or by double, either.
            var truth = value is SqlDatum.Double d && datum is SqlDatum.Exact e ? Holds(op, DoubleBound.Compare(d.Value, e.Value)) : Compare(op, value, datum);
            return raw == RawAs.Variant && Kind(datum) == "number" && (truth == T || Number(value) == Number(datum)) ? T | F : truth;
        }

        static string Kind(SqlDatum datum) => datum switch
        {
            SqlDatum.Text => "string",
            SqlDatum.Boolean => "boolean",
            _ => "number",
        };

        /// <summary>
        /// A stored number converted by a numeric cast, as the translator measured one: truncated toward zero and held at
        /// the limits for <c>INTEGER</c> and <c>BIGINT</c>, the number itself for <c>DOUBLE</c>.
        /// </summary>
        static SqlDatum Converted(J stored, string target)
        {
            var approximate = stored is J.Huge h ? h.Value : (double?)null;
            var exact = stored is J.Num n ? n.Value : (decimal?)null;

            if (target == "DOUBLE")
                return new SqlDatum.Double(approximate ?? DoubleBound.Of(exact!.Value));

            var (least, most) = target == "INTEGER" ? ((decimal)int.MinValue, (decimal)int.MaxValue) : (long.MinValue, long.MaxValue);
            if (exact is decimal x)
                return new SqlDatum.Exact(Math.Clamp(decimal.Truncate(x), least, most));

            return new SqlDatum.Exact(approximate!.Value >= (double)most ? most : approximate.Value <= (double)least ? least : 0m);
        }

        /// <summary>
        /// The cast to text of a stored value through one column (<c>Calcite.CastOfARawValueToText</c>): a string is itself
        /// and a null null; a <c>VARIANT</c> casts a number or a boolean to null and raises over a structure, a
        /// <c>VARCHAR</c> column reads nothing else, and an <c>ANY</c> renders a scalar as Java renders the reader's box, a
        /// long in its digits. An <c>ANY</c>'s structure, Java's map or list, is <c>null</c>: <see cref="RawText"/>'s coin.
        /// </summary>
        static SqlDatum? CastText(J stored, RawAs raw) => (stored, raw) switch
        {
            (J.Null, _) => SqlDatum.Null,
            (J.Str s, _) => new SqlDatum.Text(s.Value),
            (_, RawAs.Varchar) or (J.Obj or J.Arr, RawAs.Variant) => SqlDatum.Raise,
            (_, RawAs.Variant) => SqlDatum.Null,
            (J.Bool b, _) => new SqlDatum.Text(b.Value ? "true" : "false"),
            (J.Num n, _) when LongSpelled(n) => new SqlDatum.Text(n.Value.ToString(CultureInfo.InvariantCulture)),
            (J.Num n, _) => new SqlDatum.Text(JavaDouble(DoubleBound.Of(n.Value))),
            (J.Huge h, _) => new SqlDatum.Text(JavaDouble(h.Value)),
            _ => null,
        };

        /// <summary>
        /// A comparison over the cast to text of a raw value read through no named column, or an <c>ANY</c>'s structure: a
        /// scalar renders as the accessor renders it, and a null and a structure as a coin admits.
        /// </summary>
        static Truths RawText(string op, J stored, string text, World world, string question)
        {
            switch (stored)
            {
                case J.Null:
                    return op == PredicateAtom.Equal && text == "null" || op == PredicateAtom.NotEqual && text == "null" || PredicateAtom.IsComparison(op) && op is not (PredicateAtom.Equal or PredicateAtom.NotEqual)
                        ? world.Choose(T | F | U, question)
                        : op == PredicateAtom.Equal ? world.Choose(F | U, question) : world.Choose(T | U, question);

                case J.Obj or J.Arr:
                {
                    var opens = stored is J.Arr ? '[' : '{';
                    if (op is PredicateAtom.Equal or PredicateAtom.NotEqual)
                    {
                        var equal = text.StartsWith(opens) ? world.Choose(T | F, question) : F;
                        return op == PredicateAtom.Equal ? equal : Swap(equal);
                    }

                    return world.Choose(T | F, question);
                }

                default:
                    return Compare(op, new SqlDatum.Text(Rendering(stored)!), new SqlDatum.Text(text));
            }
        }

        /// <summary>
        /// <c>IS NULL</c> over a raw reading: of a null alone, through every column, where the reader reads the value
        /// (<c>Calcite.RawNullIsSqlNull</c>).
        /// </summary>
        static Truths RawIsNull(SqlValue reading, J.Obj document, World world, RawAs? raw)
        {
            raw = Own(reading, raw);
            var stored = At(document, PathOf(reading));
            var question = reading + " is null @ " + (stored is null ? "absent" : Documents.Json(stored));
            return (reading, stored) switch
            {
                (_, null) => T,
                (SqlValue.Cast { Target: "VARCHAR" or "CHAR" }, _) when raw is RawAs column => CastText(stored, column) switch { SqlDatum.NullDatum => T, SqlDatum.Raised => R, _ => F },
                (SqlValue.Raw, J.Null) => T,
                (SqlValue.Raw, J.Str) => F,
                (SqlValue.Raw, _) => raw == RawAs.Varchar ? R : F,
                (SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") }, J.Num or J.Huge) => F,
                (SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") }, J.Null or J.Obj or J.Arr) => world.Choose(T | R, question),
                (SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") }, _) => world.Choose(F | R, question),
                (_, J.Null) => world.Choose(T | F, question),
                _ => F,
            };
        }

    }

}
