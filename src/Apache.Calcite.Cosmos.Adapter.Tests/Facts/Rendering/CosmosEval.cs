using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        /// <c>null</c> where no row says, an <c>IN</c> and an opaque text among them.
        /// </summary>
        public static ServiceOutcomes? On(ServiceForm form, J.Obj document, RenderingTarget target) => form.Kind switch
        {
            // Service.AConstantIsItsOwnTruth.
            ServiceFormKind.Constant => form.Literal == Of(true) ? ServiceOutcomes.True : ServiceOutcomes.False,

            // Service.BarePathKeepsOnlyTrue, Service.NotOverAPathKeepsOnlyFalse: anything but a boolean is unknown.
            ServiceFormKind.Bare => At(document, form.Path!) is J.Bool b ? Truth(b.Value) : ServiceOutcomes.Undefined,
            ServiceFormKind.Test => Truth(Test(form.Test, At(document, form.Path!))),
            ServiceFormKind.Compare => Compare(form.Test, At(document, form.Path!), form.Literal!, target),

            // Service.NotOverUndefinedIsUndefined.
            ServiceFormKind.Not => On(form.Members[0], document, target) switch
            {
                ServiceOutcomes.True => ServiceOutcomes.False,
                ServiceOutcomes.False => ServiceOutcomes.True,
                var other => other,
            },

            ServiceFormKind.And => Kleene(Arms(form, document, target), ServiceOutcomes.False, ServiceOutcomes.True),
            ServiceFormKind.Or => Kleene(form.Members.Select(m => On(m, document, target)), ServiceOutcomes.True, ServiceOutcomes.False),
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
        static IEnumerable<ServiceOutcomes?> Arms(ServiceForm form, J.Obj document, RenderingTarget target)
        {
            var members = form.Flat.ToList();
            var orderings = target == RenderingTarget.Azure ? members.Where(Ordering).ToList() : [];
            var (lower, upper) = orderings.SelectMany(a => orderings.Select(b => (a, b))).FirstOrDefault(p => p.a.Path == p.b.Path
                && p.a.Test is CosmosServiceTest.Greater or CosmosServiceTest.GreaterOrEqual && p.b.Test is CosmosServiceTest.Less or CosmosServiceTest.LessOrEqual
                && Math.Sign(string.CompareOrdinal(StringOf(p.a.Literal!), StringOf(p.b.Literal!))) != Math.Sign(ByCodePoint(StringOf(p.a.Literal!), StringOf(p.b.Literal!))));
            if (lower is null)
                return members.Select(m => On(m, document, target));

            var range = At(document, lower.Path!) is J.Str
                ? Compare(lower.Test, At(document, lower.Path!), lower.Literal!, target) == ServiceOutcomes.True && Compare(upper.Test, At(document, upper.Path!), upper.Literal!, target) == ServiceOutcomes.True
                    ? ServiceOutcomes.Undefined : ServiceOutcomes.False
                : ServiceOutcomes.Undefined;
            return members.Where(m => m != lower && m != upper).Select(m => On(m, document, target)).Append(range);

            static bool Ordering(ServiceForm m) => m.Kind == ServiceFormKind.Compare && m.Test is not (CosmosServiceTest.Equal or CosmosServiceTest.NotEqual) && IsString(m.Literal!);
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
        /// (Service.ComparisonWithinAType), a string's by code point, as the emulator stores U+0000 there
        /// (Service.StringOrderIsCodePoint), and a number by the value the service holds
        /// (Service.NumbersBeyondLongCompareAsDoubles); across types = false, != true and an ordering undefined
        /// (Service.EqualityAcrossTypesIsFalse, Service.InequalityOverNullIsTrue, Service.OrderingAcrossTypesIsUndefined).
        /// An ordering of booleans, a null literal, and on the emulator a literal holding U+0000, which no row says how it
        /// sends, have none.
        /// </summary>
        static ServiceOutcomes? Compare(CosmosServiceTest op, J? value, string literal, RenderingTarget target)
        {
            if (value is null)
                return ServiceOutcomes.Undefined;

            var equality = op is CosmosServiceTest.Equal or CosmosServiceTest.NotEqual;
            int? order = (value, TypeOf(literal)) switch
            {
                (_, NullType) => null,
                (J.Str, StringType) when target == RenderingTarget.Emulator && StringOf(literal).Contains('\0') => null,
                (J.Str s, StringType) => target == RenderingTarget.Emulator ? ByCodePoint(Stored(s.Value), StringOf(literal)) : ByCodePoint(s.Value, StringOf(literal)),
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
                var o => Truth(op switch
                {
                    CosmosServiceTest.Equal => o == 0,
                    CosmosServiceTest.NotEqual => o != 0,
                    CosmosServiceTest.Less => o < 0,
                    CosmosServiceTest.LessOrEqual => o <= 0,
                    CosmosServiceTest.Greater => o > 0,
                    _ => o >= 0,
                }),
            };
        }

        static string Stored(string text) => text.Replace("\0", "﷐" + "0", StringComparison.Ordinal);

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

        /// <summary>The value at a path as a form spells it (<c>$["a"]["b"]</c>), or <c>null</c> where it is absent.</summary>
        public static J? At(J.Obj document, string path) => IrEval.At(document, PathOf(path));

        /// <summary>A path as a form spells it, read back.</summary>
        public static JsonDocumentPath PathOf(string spelled) => Segment().Matches(spelled)
            .Aggregate(JsonDocumentPath.Root, (path, m) => path.Property(JsonSerializer.Deserialize<string>(m.Groups[1].Value)!));

        [GeneratedRegex("""\[("(?:[^"\\]|\\.)*")\]""")]
        private static partial Regex Segment();

    }

}
