using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What S reads off a call's gathered operands at a computed place (h10): the function keys their cells fall under, and
    /// the call's image on them. <b>It decides nothing</b>: whether the keys agree is the image rule's, over the rows
    /// (<c>DESIGN.md</c>, <em>A computed value is a place</em>).
    /// </summary>
    internal static class RenderingImage
    {

        /// <summary>The operators whose rows are keyed by the call's type as well, <c>+:INTEGER</c>.</summary>
        static readonly HashSet<string> Typed = new(StringComparer.Ordinal) { "+", "-", "*", "/", "%", "MOD" };

        /// <summary>A key no row holds: what a gathering that is not one import an ordinal falls under, so no image is written.</summary>
        public static readonly FunctionKey Malformed = FunctionKey.Of("", CosmosValueClass.None, JsonCharacterBand.None);

        /// <summary>The rows' name of a call of a function at a SQL type.</summary>
        public static string KeyFunction(string function, string type) => Typed.Contains(function) ? function + ":" + type : function;

        /// <summary>Whether a call's operator's rows are keyed by its type.</summary>
        static bool IsArithmetic(string key) => key.Contains(':');

        /// <summary>
        /// The keys a call's operands' cells fall under: each kind the operand the rows describe may hold, a string by each
        /// band its characters may hold and by none; <see cref="Malformed"/> where the gathering is not one import an
        /// ordinal through a reader the rows were measured over.
        /// </summary>
        /// <param name="function">The rows' name of the call (<see cref="KeyFunction"/>).</param>
        /// <param name="gathered">The call's gathered operands.</param>
        public static ValueSet<FunctionKey> Keys(string function, ValueSet<PathImport> gathered)
        {
            var imports = gathered.AsEnumerable().ToList();
            if (Complete(imports) == false || imports.Any(i => Readable(function, i.Reader) == false))
                return ValueSet.Of(Malformed);

            return ValueSet.From(from i in imports
                                 where IsArithmetic(function) || FunctionSubjects.Describes(function, i.Ordinal)
                                 from key in KeysOf(function, i)
                                 select key);
        }

        /// <summary>
        /// The keys a call's image falls under that its rows bound: none yet, no row holding a result key (a <c>REPEAT</c>'s
        /// length bound waits for Azure's rows, h14b).
        /// </summary>
        public static ValueSet<FunctionKey> ResultKeys(string function, PathValues image) => ValueSet<FunctionKey>.Empty;

        /// <summary>
        /// The call's image on its operands: the call evaluated where every operand is one value C# computes as Calcite does
        /// (ASCII, or no surrogate); elsewhere every value of the result's type, and absent (Calcite's null, the service's
        /// undefined) wherever an operand may be other than what the function reads. Never narrower than the call.
        /// </summary>
        /// <param name="function">The rows' name of the call.</param>
        /// <param name="type">The call's SQL type.</param>
        /// <param name="gathered">The call's gathered operands, complete.</param>
        public static PathValues Of(string function, string type, ValueSet<PathImport> gathered)
        {
            var imports = gathered.AsEnumerable().OrderBy(i => i.Ordinal).ToList();
            if (imports.All(i => i.Values.Values is { Count: 1 } && i.Values.IsPresent) && Evaluate(function, [.. imports.Select(i => i.Values.Values!.AsEnumerable().Single())]) is string point)
                return PathImport.CellOf(point);

            var domain = IsArithmetic(function) ? JsonValues.Numbers : ValueSet.Of(JsonValues.StringType);
            var kinds = ResultKinds(type);
            return PathValues.OfKinds(imports.Any(i => i.Values.Kinds.AsEnumerable().Any(k => domain.Contains(k) == false)) ? kinds.Add(PathValues.AbsentKind) : kinds);
        }

        static ValueSet<string> ResultKinds(string type) => type switch
        {
            "VARCHAR" or "CHAR" => ValueSet.Of(JsonValues.StringType),
            "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" => ValueSet.Of(JsonValues.IntegerType),
            "FLOAT" or "REAL" or "DOUBLE" or "DECIMAL" => JsonValues.Numbers,
            "BOOLEAN" => ValueSet.Of(JsonValues.BooleanType),
            _ => JsonValues.AllTypes,
        };

        /// <summary>One import an ordinal, from 0.</summary>
        static bool Complete(List<PathImport> imports) =>
            imports.Select(i => i.Ordinal).OrderBy(o => o).SequenceEqual(Enumerable.Range(0, imports.Count).Select(o => (long)o));

        /// <summary>The readers the rows were measured through: Calcite's text accessor and what reads as it, a literal, and for arithmetic a number's.</summary>
        static bool Readable(string function, string reader) =>
            reader == PathImport.LiteralReader || (IsArithmetic(function)
                ? reader.StartsWith("typed:", StringComparison.Ordinal) && reader is not ("typed:VARCHAR" or "typed:CHAR" or "typed:BOOLEAN")
                : reader is PredicateAtom.TextReader or "typed:VARCHAR" or "typed:CHAR" || reader.StartsWith("cast:VARCHAR:", StringComparison.Ordinal));

        static IEnumerable<FunctionKey> KeysOf(string function, PathImport import)
        {
            foreach (var kind in import.Values.Kinds.AsEnumerable())
                switch (kind)
                {
                    case JsonValues.StringType:
                        yield return FunctionKey.Of(function, CosmosValueClass.String, JsonCharacterBand.None);
                        foreach (var (flag, _) in JsonValues.CharacterClasses)
                            if (JsonValues.ClassMask(import.Chars).HasFlag(flag))
                                yield return FunctionKey.Of(function, CosmosValueClass.String, flag);
                        break;
                    case JsonValues.IntegerType or JsonValues.FractionType:
                        yield return FunctionKey.Of(function, CosmosValueClass.Number, JsonCharacterBand.None);
                        break;
                    case JsonValues.BooleanType:
                        yield return FunctionKey.Of(function, CosmosValueClass.True, JsonCharacterBand.None);
                        yield return FunctionKey.Of(function, CosmosValueClass.False, JsonCharacterBand.None);
                        break;
                    case JsonValues.NullType:
                        yield return FunctionKey.Of(function, CosmosValueClass.Null, JsonCharacterBand.None);
                        break;
                    case PathValues.AbsentKind:
                        yield return FunctionKey.Of(function, CosmosValueClass.Absent, JsonCharacterBand.None);
                        break;
                    case JsonValues.ObjectType:
                        yield return FunctionKey.Of(function, CosmosValueClass.Object, JsonCharacterBand.None);
                        break;
                    case JsonValues.ArrayType:
                        yield return FunctionKey.Of(function, CosmosValueClass.Array, JsonCharacterBand.None);
                        break;
                    default:
                        yield return Malformed;
                        break;
                }
        }

        /// <summary>A call on single values, where C#'s answer is provably Calcite's; <c>null</c> elsewhere.</summary>
        static string? Evaluate(string function, IReadOnlyList<string> values)
        {
            if (values.All(JsonValues.IsString) == false)
                return null;

            var texts = values.Select(JsonValues.StringOf).ToList();
            static bool Ascii(string s) => s.All(c => c < 0x80);
            static bool NoSurrogate(string s) => s.All(c => char.IsSurrogate(c) == false);
            return (function, texts) switch
            {
                ("UPPER", [var s]) when Ascii(s) => JsonValues.Of(s.ToUpperInvariant()),
                ("LOWER", [var s]) when Ascii(s) => JsonValues.Of(s.ToLowerInvariant()),
                ("CHAR_LENGTH" or "CHARACTER_LENGTH", [var s]) => JsonValues.Of((decimal)s.Length),
                ("TRIM BOTH", [" ", var s]) => JsonValues.Of(s.Trim(' ')),
                ("TRIM LEADING", [" ", var s]) => JsonValues.Of(s.TrimStart(' ')),
                ("TRIM TRAILING", [" ", var s]) => JsonValues.Of(s.TrimEnd(' ')),
                ("REVERSE", [var s]) when NoSurrogate(s) => JsonValues.Of(new string([.. s.Reverse()])),
                ("CONCAT" or "||", _) => JsonValues.Of(string.Concat(texts)),
                _ => null,
            };
        }

    }

}
