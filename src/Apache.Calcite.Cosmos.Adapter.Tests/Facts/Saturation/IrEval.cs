using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

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
    /// TRUE whatever another arm does, because Calcite may evaluate that arm first and never reach the one that raises.
    /// Otherwise a raise anywhere raises. So a verdict checked against this is checked against the answer Calcite is
    /// bound to give wherever it gives one.
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
    /// parse, and <see cref="Raises"/> counts that row as one an order of evaluation decides.</item>
    /// </list>
    /// <para>
    /// <b>What was not measured is a coin</b> (<see cref="World"/>): every comparison of a raw value, the cast to text
    /// over one, and a numeric cast over one where it is not a number.
    /// </para>
    /// </remarks>
    static class IrEval
    {

        const Truths T = Truths.True;
        const Truths F = Truths.False;
        const Truths U = Truths.Unknown;
        const Truths R = Truths.Raises;

        /// <summary>
        /// Returns the truth value a predicate takes on a document.
        /// </summary>
        public static Truths Truth(SqlPredicate predicate, J.Obj document, World world)
        {
            switch (predicate)
            {
                case SqlPredicate.Not n:
                    return Swap(Truth(n.Operand, document, world));

                case SqlPredicate.And a:
                    return All(a.Arms.Select(arm => Truth(arm, document, world)).ToArray());

                case SqlPredicate.Or o:
                    return Any(o.Arms.Select(arm => Truth(arm, document, world)).ToArray());

                case SqlPredicate.Opaque o:
                    return world.Choose(T | F | U, "opaque#" + o.Id + "@" + Documents.Json(document));

                case SqlPredicate.Compare c when IsRaw(c.Left):
                    return RawCompare(c.Op, c.Left, c.Right, document, world);

                case SqlPredicate.Compare c:
                    return Compare(c.Op, Read(c.Left, document, world), Operand(c.Left, Read(c.Right, document, world)));

                case SqlPredicate.In i when IsRaw(i.Value):
                    return Any(i.Points.Select(point => RawCompare(PredicateAtom.Equal, i.Value, new SqlValue.Literal(point), document, world)).ToArray());

                case SqlPredicate.In i:
                {
                    var value = Read(i.Value, document, world);
                    return Any(i.Points.Select(point => Compare(PredicateAtom.Equal, value, Operand(i.Value, Datum(point)))).ToArray());
                }

                case SqlPredicate.IsNull n when IsRaw(n.Value):
                    return RawIsNull(n.Value, document, world);

                // Calcite simplifies the null test of a cast to the null test of what it casts, a cast being null exactly
                // where its operand is, and so never makes the parse that would have raised.
                case SqlPredicate.IsNull { Value: SqlValue.Cast { Value: SqlValue.Text cast } }:
                    return Rendering(At(document, cast.Path)) is null ? T : F;

                case SqlPredicate.IsNull n:
                    return Read(n.Value, document, world) switch
                    {
                        SqlDatum.NullDatum => T,
                        SqlDatum.Raised => R,
                        _ => F,
                    };

                // The service's test of the document, which the in-process body reads too: a null is defined.
                case SqlPredicate.IsDefined d:
                    return At(document, d.Value.PathRead!.Value) is null ? F : T;

                default:
                    throw new NotSupportedException(predicate.GetType().Name);
            }
        }

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
        public static bool Raises(SqlPredicate predicate, J.Obj document, World world) =>
            predicate.Subterms.Any(term => term switch
            {
                SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not => false,
                SqlPredicate.IsNull { Value: SqlValue.Cast { Value: SqlValue.Text } cast } => Read(cast, document, world) is SqlDatum.Raised,
                _ => Truth(term, document, world) == R,
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
            foreach (var name in path.Names)
                current = current is J.Obj o && o.Properties.TryGetValue(name, out var child) ? child : null;

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
            J.Num n => JavaDouble((double)n.Value),
            J.Huge h => JavaDouble(h.Value),
            _ => null,
        };

        /// <summary>
        /// Whether Jackson reads a stored number as an integer: written without a point or an exponent.
        /// </summary>
        static bool IntegerSpelled(J.Num number) => number.Value.ToString(CultureInfo.InvariantCulture).IndexOfAny(new[] { '.', 'E', 'e' }) < 0;

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
        public static SqlDatum Read(SqlValue value, J.Obj document, World world)
        {
            switch (value)
            {
                case SqlValue.Text t:
                    return Rendering(At(document, t.Path)) is string rendered ? new SqlDatum.Text(rendered) : SqlDatum.Null;

                case SqlValue.Typed t:
                    return Returning(At(document, t.Path), t.Type);

                case SqlValue.Cast { Value: SqlValue.Text text } c:
                    return Parse(Rendering(At(document, text.Path)), c.Target);

                case SqlValue.Literal l:
                    return Datum(l.Value);

                case SqlValue.Parameter p:
                    return world.Parameter(p);

                default:
                    throw new NotSupportedException(value.ToString());
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
                        J.Num d when IntegerSpelled(d) == false => new SqlDatum.Double((double)d.Value),
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

        static readonly Regex JavaInteger = new(@"^[+-]?[0-9]+$", RegexOptions.CultureInvariant);

        static readonly Regex JavaDoubleText = new(@"^[+-]?(NaN|Infinity|(([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?[fFdD]?))$", RegexOptions.CultureInvariant);

        static readonly Regex BigDecimalText = new(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// A cast of text to a number, as Calcite converts one: the text trimmed, then parsed by Java, which raises on what
        /// it cannot parse.
        /// </summary>
        static SqlDatum Parse(string? text, string target)
        {
            if (text is null)
                return SqlDatum.Null;

            var trimmed = text.Trim(' ', '\t', '\n', '\r', '\f', '\v', '\0');
            switch (target)
            {
                case "INTEGER" or "BIGINT":
                {
                    if (JavaInteger.IsMatch(trimmed) == false || decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) == false)
                        return SqlDatum.Raise;

                    var (least, most) = target == "INTEGER" ? ((decimal)int.MinValue, (decimal)int.MaxValue) : (long.MinValue, long.MaxValue);
                    return whole >= least && whole <= most ? new SqlDatum.Exact(whole) : SqlDatum.Raise;
                }

                case "DOUBLE":
                    return JavaDoubleText.IsMatch(trimmed) && double.TryParse(trimmed.TrimEnd('f', 'F', 'd', 'D'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? new SqlDatum.Double(d) : SqlDatum.Raise;

                case "DECIMAL":
                    if (BigDecimalText.IsMatch(trimmed) == false)
                        return SqlDatum.Raise;

                    return decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                        ? new SqlDatum.Exact(exact)
                        : new SqlDatum.Double(double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture));

                default:
                    throw new NotSupportedException(target);
            }
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
                (SqlDatum.Boolean a, SqlDatum.Boolean b) => a.Value.CompareTo(b.Value),
                _ when Number(left) is double a && Number(right) is double b => a.CompareTo(b),
                _ => throw new InvalidOperationException($"No comparison of {left} with {right}: the generator built one the loader never does."),
            };

            var holds = op switch
            {
                PredicateAtom.Equal => order == 0,
                PredicateAtom.NotEqual => order != 0,
                PredicateAtom.Less => order < 0,
                PredicateAtom.LessOrEqual => order <= 0,
                PredicateAtom.Greater => order > 0,
                PredicateAtom.GreaterOrEqual => order >= 0,
                _ => throw new NotSupportedException(op),
            };

            return holds ? T : F;
        }

        static double? Number(SqlDatum datum) => datum switch
        {
            SqlDatum.Exact e => (double)e.Value,
            SqlDatum.Double d => d.Value,
            _ => null,
        };

        /// <summary>
        /// Whether a value is read off the stored value itself rather than its rendering: <c>raw</c>, or a cast over it.
        /// </summary>
        public static bool IsRaw(SqlValue value) => value is SqlValue.Raw or SqlValue.Cast { Value: SqlValue.Raw };

        static JsonDocumentPath PathOf(SqlValue value) => value.PathRead ?? throw new InvalidOperationException(value + " reads no path.");

        /// <summary>
        /// A comparison over a raw reading, whose behaviour is a coin wherever it was not measured.
        /// </summary>
        static Truths RawCompare(string op, SqlValue reading, SqlValue operand, J.Obj document, World world)
        {
            var stored = At(document, PathOf(reading));
            if (stored is null)
                return U;

            var question = reading + " " + op + " " + operand + " @ " + Documents.Json(stored);
            if (operand is SqlValue.Parameter)
                return reading is SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") } && stored is J.Null or J.Obj or J.Arr
                    ? world.Choose(U | R, question)
                    : reading is SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") } && stored is J.Str or J.Bool
                        ? world.Choose(T | F | U | R, question)
                        : world.Choose(T | F | U, question);

            var literal = ((SqlValue.Literal)operand).Value;
            switch (reading)
            {
                case SqlValue.Raw:
                    if (op is PredicateAtom.Equal or PredicateAtom.NotEqual)
                    {
                        var equal = RawEqual(stored, literal, world, reading + " = " + operand + " @ " + Documents.Json(stored));
                        return op == PredicateAtom.Equal ? equal : Swap(equal);
                    }

                    return world.Choose(T | F | U, question);

                case SqlValue.Cast { Target: "VARCHAR" or "CHAR" }:
                    return RawText(op, stored, StringOf(literal), world, question);

                case SqlValue.Cast c:
                    return stored switch
                    {
                        J.Num or J.Huge => Compare(op, Converted(stored, c.Target), Datum(literal)),
                        J.Null or J.Obj or J.Arr => world.Choose(U | R, question),
                        _ => world.Choose(T | F | R, question),
                    };

                default:
                    throw new NotSupportedException(reading.ToString());
            }
        }

        /// <summary>
        /// An equality of a raw value with a literal: a scalar of the literal's own type is the literal or not, and two
        /// numbers, or values of two types, are a coin.
        /// </summary>
        static Truths RawEqual(J stored, string literal, World world, string question)
        {
            if (stored is J.Null or J.Obj or J.Arr)
                return world.Choose(F | U, question);

            var value = stored.Encoded;
            if (IsNumber(literal))
                return IsNumber(value) && Renderings.SameNumber(value, literal) ? world.Choose(T | F | U, question) : world.Choose(F | U, question);

            if (TypeOf(value) == TypeOf(literal))
                return value == literal ? T : F;

            return world.Choose(F | U, question);
        }

        /// <summary>
        /// A stored number converted by a numeric cast, as the translator measured one: truncated toward zero and held at
        /// the limits for <c>INTEGER</c> and <c>BIGINT</c>, the number itself for <c>DOUBLE</c>.
        /// </summary>
        static SqlDatum Converted(J stored, string target)
        {
            var approximate = stored is J.Huge h ? h.Value : (double?)null;
            var exact = stored is J.Num n ? n.Value : (decimal?)null;

            if (target == "DOUBLE")
                return new SqlDatum.Double(approximate ?? (double)exact!.Value);

            var (least, most) = target == "INTEGER" ? ((decimal)int.MinValue, (decimal)int.MaxValue) : (long.MinValue, long.MaxValue);
            if (exact is decimal x)
                return new SqlDatum.Exact(Math.Clamp(decimal.Truncate(x), least, most));

            return new SqlDatum.Exact(approximate!.Value >= (double)most ? most : approximate.Value <= (double)least ? least : 0m);
        }

        /// <summary>
        /// A comparison over the cast to text of a raw value: a scalar renders as the accessor renders it, and a null and
        /// a structure as a coin admits.
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
        /// <c>IS NULL</c> over a raw reading.
        /// </summary>
        static Truths RawIsNull(SqlValue reading, J.Obj document, World world)
        {
            var stored = At(document, PathOf(reading));
            var question = reading + " is null @ " + (stored is null ? "absent" : Documents.Json(stored));
            return (reading, stored) switch
            {
                (_, null) => T,
                (SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") }, J.Num or J.Huge) => F,
                (SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") }, J.Null or J.Obj or J.Arr) => world.Choose(T | R, question),
                (SqlValue.Cast { Target: not ("VARCHAR" or "CHAR") }, _) => world.Choose(F | R, question),
                (_, J.Null) => world.Choose(T | F, question),
                _ => F,
            };
        }

    }

}
