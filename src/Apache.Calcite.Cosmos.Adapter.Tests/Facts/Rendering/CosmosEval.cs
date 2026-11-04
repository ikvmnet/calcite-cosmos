using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The service's answer to a written form on one document, written from what the registry's rows state and independent
    /// of <see cref="ServiceLeaf"/> and <see cref="ServiceCompound"/>, which read cells through the settled table: what
    /// source A and the gate faults hold S's verdicts to, and <c>CosmosEvalServiceTests</c> holds to the emulator.
    /// </summary>
    internal static partial class CosmosEval
    {

        /// <summary>
        /// What a <c>WHERE</c> makes of the form on the document, on a target: kept, kept by <c>NOT</c>, or neither; or
        /// <c>null</c> where no row says, an <c>IN</c>, an opaque text and a computed place <paramref name="places"/> does
        /// not name among them.
        /// </summary>
        /// <param name="places">The calls the form's computed places spell, by spelling (<see cref="Place"/>).</param>
        public static ServiceOutcomes? On(ServiceForm form, J.Obj document, RenderingTarget target, IReadOnlyDictionary<string, SqlValue.Call>? places = null) => form.Kind switch
        {
            // Service.AConstantIsItsOwnTruth.
            ServiceFormKind.Constant => form.Literal == Of(true) ? ServiceOutcomes.True : ServiceOutcomes.False,

            // Service.BarePathKeepsOnlyTrue, Service.NotOverAPathKeepsOnlyFalse: anything but a boolean is unknown.
            ServiceFormKind.Bare => Read(document, form.Path!, target, places) is (true, var v) ? v is J.Bool b ? Truth(b.Value) : ServiceOutcomes.Undefined : null,
            ServiceFormKind.Test => Read(document, form.Path!, target, places) is (true, var v) ? Truth(Test(form.Test, v)) : null,
            ServiceFormKind.Compare => Read(document, form.Path!, target, places) is (true, var v) ? Compare(form.Test, v, form.Literal!, target) : null,
            ServiceFormKind.Affix => Read(document, form.Path!, target, places) is (true, var v) ? Affix(form, v, target) : null,

            // Service.TwoPathsCompareAsAPathAndALiteral, measured on the emulator alone: the left against the right's value
            // as a literal, but two numbers each as the service holds it, the diagonal a value against itself; nothing of
            // a container on either side.
            ServiceFormKind.ComparePaths when target == RenderingTarget.Emulator && IsPath(form.Path!) && IsPath(form.Right!) => (At(document, form.Path!), At(document, form.Right!)) switch
            {
                (null, _) or (_, null) => ServiceOutcomes.Undefined,
                ({ IsScalar: false }, _) or (_, { IsScalar: false }) => null,
                (J left and (J.Num or J.Huge), J.Num r) => Ordered(form.Test, Numeric(left, r.Value, Held(r.Value, target), target)),
                var (left, right) => Compare(form.Test, left, right.Encoded, target),
            },

            // Service.NotOverUndefinedIsUndefined.
            ServiceFormKind.Not => On(form.Members[0], document, target, places) switch
            {
                ServiceOutcomes.True => ServiceOutcomes.False,
                ServiceOutcomes.False => ServiceOutcomes.True,
                var other => other,
            },

            ServiceFormKind.And => Kleene(Arms(form, document, target, places), ServiceOutcomes.False, ServiceOutcomes.True),
            ServiceFormKind.Or => Kleene(form.Members.Select(m => On(m, document, target, places)), ServiceOutcomes.True, ServiceOutcomes.False),

            // Service.TernaryTakesThenOnTrueAlone, measured on the emulator alone: the then-arm where the condition is true,
            // the else-arm over every other value, undefined and an absent path among them.
            ServiceFormKind.Choose when target == RenderingTarget.Emulator => On(form.Members[0], document, target, places) switch
            {
                null => null,
                ServiceOutcomes.True => On(form.Members[1], document, target, places),
                _ => On(form.Members[2], document, target, places),
            },
            _ => null,
        };

        /// <summary>
        /// Service.ConnectivesAreKleene, in either order: a deciding arm decides, then an unknown, then the other value.
        /// </summary>
        static ServiceOutcomes? Kleene(IEnumerable<ServiceOutcomes?> arms, ServiceOutcomes deciding, ServiceOutcomes otherwise)
        {
            var all = arms.ToList();
            return all.Contains(null) ? null : all.Contains(deciding) ? deciding : all.Contains(ServiceOutcomes.Undefined) ? ServiceOutcomes.Undefined : otherwise;
        }

        /// <summary>
        /// A conjunction's arms as written, but on Azure two string orderings of one path whose literals the two string
        /// orders rank apart, which Service.RangeOverPartedBoundsIsEmpty reads as one range: undefined between them by code
        /// point, and over anything but a string; false outside.
        /// </summary>
        static IEnumerable<ServiceOutcomes?> Arms(ServiceForm form, J.Obj document, RenderingTarget target, IReadOnlyDictionary<string, SqlValue.Call>? places)
        {
            var members = form.Flat.ToList();
            var orderings = target == RenderingTarget.Azure ? members.Where(Ordering).ToList() : [];
            var (lower, upper) = orderings.SelectMany(a => orderings.Select(b => (a, b))).FirstOrDefault(p => p.a.Path == p.b.Path
                && p.a.Test is CosmosServiceTest.Greater or CosmosServiceTest.GreaterOrEqual && p.b.Test is CosmosServiceTest.Less or CosmosServiceTest.LessOrEqual
                && Math.Sign(string.CompareOrdinal(StringOf(p.a.Literal!), StringOf(p.b.Literal!))) != Math.Sign(ByCodePoint(StringOf(p.a.Literal!), StringOf(p.b.Literal!))));
            if (lower is null)
                return members.Select(m => On(m, document, target, places));

            var range = At(document, lower.Path!) is J.Str
                ? Compare(lower.Test, At(document, lower.Path!), lower.Literal!, target) == ServiceOutcomes.True && Compare(upper.Test, At(document, upper.Path!), upper.Literal!, target) == ServiceOutcomes.True
                    ? ServiceOutcomes.Undefined : ServiceOutcomes.False
                : ServiceOutcomes.Undefined;
            return members.Where(m => m != lower && m != upper).Select(m => On(m, document, target, places)).Append(range);

            static bool Ordering(ServiceForm m) => m.Kind == ServiceFormKind.Compare && m.Test is not (CosmosServiceTest.Equal or CosmosServiceTest.NotEqual) && IsString(m.Literal!);
        }

        /// <summary>
        /// An affix or <c>LIKE</c>: undefined over anything but a string (Service.StringPredicatesOverNonStringsAreUndefined);
        /// over one by code point (Service.AffixesMatchByCodePoint, Service.LikeMatchesByCodePoint), on the emulator an affix
        /// matching the stored string against the argument as sent in that spelling (Service.EmulatorComparesTheSpellingItIsSent).
        /// None for an argument no pattern of runs says, for the emulator's LIKE where either holds an escape, unmeasured, and
        /// on Azure where either holds U+0000, which ends what CONTAINS reads (Service.NulEndsWhatReplaceAndContainsRead).
        /// </summary>
        static ServiceOutcomes? Affix(ServiceForm form, J? value, RenderingTarget target)
        {
            if (value is not J.Str s)
                return ServiceOutcomes.Undefined;

            if (form.Pattern is not { } pattern)
                return null;

            return target switch
            {
                RenderingTarget.Emulator when form.Test == CosmosServiceTest.Like =>
                    EmulatorStoredText.Escapes(s.Value) || EmulatorStoredText.Escapes(pattern) ? null : Truth(AffixPattern.Matches(s.Value, pattern)),
                RenderingTarget.Emulator => Truth(AffixPattern.Matches(EmulatorStoredText.Of(s.Value), EmulatorStoredText.Of(pattern))),
                _ => s.Value.Contains('\0') || pattern.Contains('\0') ? null : Truth(AffixPattern.Matches(s.Value, pattern)),
            };
        }

        /// <summary>Service.IsDefined, Service.IsNull, Service.IsPrimitive, Service.KindTests: never undefined.</summary>
        static bool Test(CosmosServiceTest test, J? value) => test switch
        {
            CosmosServiceTest.IsDefined => value is not null,
            CosmosServiceTest.IsNull => value is J.Null,
            CosmosServiceTest.IsPrimitive => value is J.Null or J.Bool or J.Num or J.Huge or J.Str,
            CosmosServiceTest.IsString => value is J.Str,
            CosmosServiceTest.IsNumber => value is J.Num or J.Huge,
            CosmosServiceTest.IsBool => value is J.Bool,
            CosmosServiceTest.IsObject => value is J.Obj,
            CosmosServiceTest.IsArray => value is J.Arr,
            _ => throw new ArgumentOutOfRangeException(nameof(test), test, null),
        };

        /// <summary>
        /// A comparison with a literal: over an absent path undefined; within a type by the type's order
        /// (Service.ComparisonWithinAType), a string's by code point, on the emulator the stored value against the literal
        /// as the statement sends it, each in the stored spelling with both escapes (EmulatorStoredText,
        /// Service.EmulatorComparesTheSpellingItIsSent), and a number by the value the service holds
        /// (Service.NumbersBeyondLongCompareAsDoubles); across types = false, != true and an ordering undefined
        /// (Service.EqualityAcrossTypesIsFalse, Service.InequalityOverNullIsTrue, Service.OrderingAcrossTypesIsUndefined).
        /// An ordering of booleans, a null literal, and on the emulator an ordering against a literal holding U+0000, which
        /// the stored spelling does not keep (U+FDD0 0 above U+0001..U+FDCF), have none.
        /// </summary>
        static ServiceOutcomes? Compare(CosmosServiceTest op, J? value, string literal, RenderingTarget target)
        {
            if (value is null)
                return ServiceOutcomes.Undefined;

            var equality = op is CosmosServiceTest.Equal or CosmosServiceTest.NotEqual;
            int? order = (value, TypeOf(literal)) switch
            {
                (_, NullType) => null,
                (J.Str, StringType) when target == RenderingTarget.Emulator && equality == false && StringOf(literal).Contains('\0') => null,
                (J.Str s, StringType) => target == RenderingTarget.Emulator ? ByCodePoint(EmulatorStoredText.Of(s.Value), EmulatorStoredText.Of(StringOf(literal))) : ByCodePoint(s.Value, StringOf(literal)),
                (J.Num or J.Huge, IntegerType or FractionType) when IsExactNumber(literal) => Numeric(value, NumberOf(literal), literal.StartsWith(IntegerType, StringComparison.Ordinal), target),
                (J.Num or J.Huge, IntegerType or FractionType) => null,
                (J.Bool b, BooleanType) when equality => (b.Encoded == literal) ? 0 : 1,
                (J.Bool, BooleanType) => null,
                _ => int.MinValue,
            };

            return order switch
            {
                null => null,
                int.MinValue => equality ? Truth(op == CosmosServiceTest.NotEqual) : ServiceOutcomes.Undefined,
                int o => Ordered(op, o),
            };
        }

        /// <summary>What a comparator answers of two values that stand in an order.</summary>
        static ServiceOutcomes Ordered(CosmosServiceTest op, int o) => Truth(op switch
        {
            CosmosServiceTest.Equal => o == 0,
            CosmosServiceTest.NotEqual => o != 0,
            CosmosServiceTest.Less => o < 0,
            CosmosServiceTest.LessOrEqual => o <= 0,
            CosmosServiceTest.Greater => o > 0,
            _ => o >= 0,
        });

        /// <summary>Whether the service holds a document's number as an int64: one written whole, and on Azure one written with a point too.</summary>
        static bool Held(decimal x, RenderingTarget target) => x == decimal.Truncate(x) && x >= long.MinValue && x <= long.MaxValue && (target == RenderingTarget.Azure || x.Scale == 0);

        /// <summary>Strings by code point, a lone surrogate as U+FFFD.</summary>
        static int ByCodePoint(string a, string b)
        {
            var (x, y) = (a.EnumerateRunes(), b.EnumerateRunes());
            while (true)
            {
                var (more, others) = (x.MoveNext(), y.MoveNext());
                if (more == false || others == false)
                    return more.CompareTo(others);

                if (x.Current.Value.CompareTo(y.Current.Value) is var order and not 0)
                    return order;
            }
        }

        /// <summary>
        /// Two numbers by what the service holds: an int64 as itself, any other as its correctly rounded double, compared by
        /// value. Azure holds a whole number written with a point in int64 as an int64, the emulator as a double.
        /// </summary>
        static int Numeric(J value, decimal literal, bool integral, RenderingTarget target)
        {
            var held = value switch
            {
                J.Num n when Whole(n.Value) && (target == RenderingTarget.Azure || n.Value.Scale == 0) => (BigInteger?)(BigInteger)n.Value,
                _ => null,
            };
            var other = integral && Whole(literal) ? (BigInteger?)(BigInteger)literal : null;
            var (d, e) = (value is J.Huge h ? h.Value : DoubleOf(((J.Num)value).Value), DoubleOf(literal));
            return (held, other) switch
            {
                ({ } a, { } b) => a.CompareTo(b),
                ({ } a, null) => -Exact(e, a),
                (null, { } b) => Exact(d, b),
                _ => d.CompareTo(e),
            };

            static bool Whole(decimal x) => x == decimal.Truncate(x) && x >= long.MinValue && x <= long.MaxValue;
        }

        /// <summary>A decimal's correctly rounded double, read from its invariant text, never by a cast (spec §3.14).</summary>
        static double DoubleOf(decimal value) => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        /// <summary>A double against an integer by value, exactly.</summary>
        static int Exact(double d, BigInteger n)
        {
            if (double.IsInfinity(d))
                return Math.Sign(d);

            var bits = BitConverter.DoubleToInt64Bits(d);
            var (exponent, mantissa) = ((int)((bits >> 52) & 0x7FF), bits & 0xFFFFFFFFFFFFFL);
            var m = new BigInteger(exponent == 0 ? mantissa : mantissa | 1L << 52) * (d < 0 ? -1 : 1);
            var shift = (exponent == 0 ? 1 : exponent) - 1075;
            return shift >= 0 ? (m << shift).CompareTo(n) : m.CompareTo(n << -shift);
        }

        static ServiceOutcomes Truth(bool value) => value ? ServiceOutcomes.True : ServiceOutcomes.False;

        /// <summary>
        /// The value at a path as a form spells it (<c>$["a"][0]</c>), or <c>null</c> where it is absent: an index the element
        /// of an array, and absent over every value that is no array and past the end (Service.IndexOverANonArrayIsUndefined).
        /// </summary>
        public static J? At(J.Obj document, string path) => IrEval.At(document, PathOf(path));

        /// <summary>
        /// What a form's path holds on a document as the service reads it: a path's value, or a computed place's where
        /// <paramref name="places"/> names its call; unknown for any other spelling, which is never read as the path inside it.
        /// </summary>
        static (bool Known, J? Value) Read(J.Obj document, string path, RenderingTarget target, IReadOnlyDictionary<string, SqlValue.Call>? places) =>
            places is not null && places.TryGetValue(path, out var call) ? Place(call, document, target) : IsPath(path) ? (true, At(document, path)) : (false, null);

        /// <summary>Whether a form's path is a path, every segment a quoted name or an index, and not a computed place's spelling.</summary>
        static bool IsPath(string spelled) => Whole().IsMatch(spelled);

        /// <summary>
        /// The service's value of a computed place, from the function rows alone (Service.StringFunctionAnswersByBand,
        /// Service.IntegerArithmeticIsExactWithinLong) and never the settled table: each operand the rows key read as the
        /// service holds it, a nested place by its own rows, keyed by its kind and by every band a string of it holds.
        /// Undefined where a row says so; nothing over a null, an absent path or a structure, which a row reads Same as
        /// Calcite's null; Calcite's value where every row is Same over a string or a number. Unknown where a row parts or
        /// is missing, an operand the rows do not key is no literal, or Calcite raises.
        /// </summary>
        static (bool Known, J? Value) Place(SqlValue.Call call, J.Obj document, RenderingTarget target)
        {
            var function = RenderingImage.KeyFunction(call.Function, call.Type!);
            if (CosmosFunctionSpellings.Subjects.TryGetValue(function, out var subject) == false)
                return (false, null);

            int[] keyed = subject == FunctionKey.Every ? [.. Enumerable.Range(0, call.Operands.Count)] : [(int)subject];
            if (call.Operands.Where((_, i) => keyed.Contains(i) == false).Any(o => o is not SqlValue.Literal))
                return (false, null);

            var (undefined, nothing) = (false, false);
            foreach (var i in keyed)
            {
                var (known, value) = call.Operands[i] switch
                {
                    SqlValue.Literal l => (true, PathCellTests.Decode(l.Value)),
                    SqlValue.Call { Spelled: not null } inner => Place(inner, document, target),
                    SqlValue.Text or SqlValue.Raw or SqlValue.Typed => (true, IrEval.At(document, call.Operands[i].PathRead!.Value)),
                    _ => (false, (J?)null),
                };
                if (known == false)
                    return (false, null);

                var (kind, mask) = value switch
                {
                    null => (CosmosValueClass.Absent, JsonCharacterBand.None),
                    J.Null => (CosmosValueClass.Null, JsonCharacterBand.None),
                    J.Bool b => (b.Value ? CosmosValueClass.True : CosmosValueClass.False, JsonCharacterBand.None),
                    J.Num or J.Huge => (CosmosValueClass.Number, JsonCharacterBand.None),
                    J.Str s => (CosmosValueClass.String, MaskOf(s.Value)),
                    J.Obj => (CosmosValueClass.Object, JsonCharacterBand.None),
                    _ => (CosmosValueClass.Array, JsonCharacterBand.None),
                };
                foreach (var band in mask == JsonCharacterBand.None ? [JsonCharacterBand.None] : Enum.GetValues<JsonCharacterBand>().Where(b => b != JsonCharacterBand.None && mask.HasFlag(b)))
                    switch (CosmosFunctionSpellings.Rows.TryGetValue((FunctionKey.Of(function, kind, band), target), out var answer) ? answer : (CosmosFunctionAnswer?)null)
                    {
                        case CosmosFunctionAnswer.Undefined:
                            undefined = true;
                            break;
                        case CosmosFunctionAnswer.Same:
                            nothing |= kind is not (CosmosValueClass.String or CosmosValueClass.Number);
                            break;
                        default:
                            return (false, null);
                    }
            }

            if (undefined || nothing)
                return (true, null);

            var world = new World(0);
            return (IrEval.Read(call, document, world), world.Tossed) switch
            {
                (SqlDatum.Text t, 0) => (true, new J.Str(t.Value)),
                (SqlDatum.Exact x, 0) => (true, new J.Num(x.Value)),
                _ => (false, null),
            };
        }

        [GeneratedRegex("""^\$(?:\[(?:"(?:[^"\\]|\\.)*"|[0-9]+)\])*$""")]
        private static partial Regex Whole();

        /// <summary>A path as a form spells it, read back: a quoted name a property, a bare number an element.</summary>
        public static JsonDocumentPath PathOf(string spelled) => Segment().Matches(spelled)
            .Aggregate(JsonDocumentPath.Root, (path, m) => m.Groups[1].Success
                ? path.Property(JsonSerializer.Deserialize<string>(m.Groups[1].Value)!)
                : path.Element(long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));

        [GeneratedRegex("""\[(?:("(?:[^"\\]|\\.)*")|([0-9]+))\]""")]
        private static partial Regex Segment();

    }

}
