using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What S reads off a call's gathered operands at a computed place (h10): the function keys their cells fall under, and
    /// the call at a point. <b>It decides nothing</b>: which name a call is keyed by, whether its gathering is complete and
    /// read through licensed readers, whether its keys agree, and what kinds its image holds are S's rules over the rows
    /// (<c>DESIGN.md</c>, <em>A computed value is a place</em>, G6).
    /// </summary>
    internal static class RenderingImage
    {

        /// <summary>
        /// The keys a call's operands' cells fall under (F2): for every operand the rows name — by its ordinal, by
        /// <see cref="FunctionKey.Every"/> where they name each alike, and a literal by <see cref="FunctionKey.Literal"/> where
        /// they key one apart, a literal's reader being one of S's (<see cref="FunctionKey.OrdinalsKeying"/>) — each kind it may hold, a string by the band each
        /// class its characters may hold lies within and by none, and each by the atoms of its kind its values may be in
        /// (<see cref="FunctionValueClasses.Holding"/>). A kind no class names is keyed as it is, a key no row holds. Key
        /// assembly: whether the gathering is complete, its readers licensed and its keys measured and agreeing is S's rule.
        /// </summary>
        /// <param name="function">The name the call's rows are filed under (S's <c>callkey</c>).</param>
        /// <param name="gathered">The call's gathered operands.</param>
        /// <param name="subjects">The operands the rows name, <see cref="FunctionKey.Every"/> among them where they read each alike and
        /// <see cref="FunctionKey.Literal"/> where they key a literal apart: the rows' own.</param>
        /// <param name="literals">The readers of a literal operand: S's <c>literalreaders</c>.</param>
        public static ValueSet<FunctionKey> Keys(string function, ValueSet<PathImport> gathered, ValueSet<long> subjects, ValueSet<string> literals) =>
            ValueSet.From(from i in gathered.AsEnumerable()
                          from ordinal in FunctionKey.OrdinalsKeying(i.Ordinal, i.Reader, literals).AsEnumerable()
                          where subjects.Contains(ordinal)
                          from key in KeysOf(function, ordinal, i)
                          select key);

        /// <summary>
        /// The call's image on its operands, where its keys say it is a value of its type: the call evaluated where every
        /// operand is one value C# computes as Calcite does (ASCII, or no surrogate), and the kinds S's rule gave it
        /// elsewhere. Never narrower than the call.
        /// </summary>
        /// <param name="function">The name the call's rows are filed under.</param>
        /// <param name="gathered">The call's gathered operands, complete.</param>
        /// <param name="kinds">What S's rule says the call may hold over them: its type's kinds, and absent where an operand
        /// no row keys may make it null.</param>
        public static PathValues Of(string function, ValueSet<PathImport> gathered, PathValues kinds)
        {
            var imports = gathered.AsEnumerable().OrderBy(i => i.Ordinal).ToList();
            return imports.All(i => i.Values.Values is { Count: 1 } && i.Values.IsPresent) && Evaluate(function, [.. imports.Select(i => i.Values.Values!.AsEnumerable().Single())]) is string point
                ? PathImport.CellOf(point)
                : kinds;
        }

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
                            yield return FunctionKey.Of(function, ordinal, CosmosValueClass.None, JsonCharacterBand.None);
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
