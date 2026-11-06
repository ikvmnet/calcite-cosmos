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
        public static readonly FunctionKey Malformed = FunctionKey.Of("", FunctionKey.Every, CosmosValueClass.None, JsonCharacterBand.None);

        /// <summary>The rows' name of a call of a function at a SQL type.</summary>
        public static string KeyFunction(string function, string type) => Typed.Contains(function) ? function + ":" + type : function;

        /// <summary>Whether a call's operator's rows are keyed by its type.</summary>
        static bool IsArithmetic(string key) => key.Contains(':');

        /// <summary>
        /// The keys a call's operands' cells fall under (F2): for every operand the rows name — by its ordinal, by
        /// <see cref="FunctionKey.Every"/> where they name each alike, and a literal by <see cref="FunctionKey.Literal"/> where
        /// they key one apart from a stored operand — each kind it may hold, a string by the band each class
        /// its characters may hold lies within and by none, and each by the atoms of its kind its values may be in
        /// (<see cref="FunctionValueClasses.Holding"/>); <see cref="Malformed"/> where the gathering is not one import an
        /// ordinal, each a literal or read through a reader the rows were measured over. Which rows cover an atom is S's.
        /// </summary>
        /// <param name="function">The rows' name of the call (<see cref="KeyFunction"/>).</param>
        /// <param name="gathered">The call's gathered operands.</param>
        /// <param name="subjects">The operands the rows name, <see cref="FunctionKey.Every"/> among them where they read each alike and
        /// <see cref="FunctionKey.Literal"/> where they key a literal apart: the rows' own.</param>
        /// <param name="readers">The readers the rows were measured through: the rows' own.</param>
        public static ValueSet<FunctionKey> Keys(string function, ValueSet<PathImport> gathered, ValueSet<long> subjects, ValueSet<string> readers)
        {
            var imports = gathered.AsEnumerable().ToList();
            if (Complete(imports) == false || imports.Any(i => i.Reader != PathImport.LiteralReader && readers.Contains(i.Reader) == false))
                return ValueSet.Of(Malformed);

            return ValueSet.From(from i in imports
                                 from ordinal in i.Reader == PathImport.LiteralReader ? new[] { FunctionKey.Every, i.Ordinal, FunctionKey.Literal } : new[] { FunctionKey.Every, i.Ordinal }
                                 where subjects.Contains(ordinal)
                                 from key in KeysOf(function, ordinal, i)
                                 select key);
        }

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

        // A function row's band is a class that lies within itself; a refinement keys under the band it lies within.
        static IEnumerable<FunctionKey> KeysOf(string function, long ordinal, PathImport import)
        {
            foreach (var kind in import.Values.Kinds.AsEnumerable())
                switch (kind)
                {
                    case JsonValues.StringType:
                        var bands = JsonValues.Within(JsonValues.ClassMask(import.Chars));
                        foreach (var band in JsonValues.CharacterClasses.Where(c => c.Flag == c.Within && bands.HasFlag(c.Flag)).Select(c => c.Flag).Prepend(JsonCharacterBand.None))
                            foreach (var atom in Atoms(CosmosValueClass.String, import))
                                yield return FunctionKey.Of(function, ordinal, CosmosValueClass.String, band, atom);
                        break;
                    case JsonValues.BooleanType:
                        foreach (var truth in new[] { CosmosValueClass.True, CosmosValueClass.False })
                            foreach (var atom in Atoms(truth, import))
                                yield return FunctionKey.Of(function, ordinal, truth, JsonCharacterBand.None, atom);
                        break;
                    default:
                        if (CosmosValueClasses.OfKind(kind) is var c and not CosmosValueClass.None)
                            foreach (var atom in Atoms(c, import))
                                yield return FunctionKey.Of(function, ordinal, c, JsonCharacterBand.None, atom);
                        else
                            yield return Malformed;
                        break;
                }
        }

        // The atoms of a kind a cell's values may be in, each tested; every one where its domain names no value of the kind,
        // which says nothing of it: no domain, a kind no domain holds (absent, a structure), or one it lists none of.
        static IEnumerable<FunctionValueClass> Atoms(CosmosValueClass kind, PathImport import)
        {
            var atoms = FunctionValueClasses.AtomsOf(kind).ToList();
            var values = import.Values.Values?.AsEnumerable().Where(v => CosmosValueClasses.Of(v) == kind).ToList();
            return values is { Count: > 0 } ? atoms.Where(a => values.Any(v => FunctionValueClasses.Holds(a, v))) : atoms;
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
                ("CONCAT" or "||" or StringCalls.NullAsEmpty, _) => JsonValues.Of(string.Concat(texts)),
                _ => null,
            };
        }

    }

}
