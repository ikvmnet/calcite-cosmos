using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One operand of a call at a computed place, as S gathers it beneath a stratum: its ordinal, the reader it is read
    /// through (<c>literal</c> for a literal, its own single cell), its values there and its characters (h10).
    /// </summary>
    internal sealed record PathImport(long Ordinal, string Reader, PathValues Values, ValueSet<string> Chars) : IComparable<PathImport>
    {

        /// <summary>The reader a literal operand is read through: it is its own single cell.</summary>
        public const string LiteralReader = "literal";

        /// <summary>An operand's import, as the place rule writes it.</summary>
        public static PathImport Of(long ordinal, string reader, PathValues values, ValueSet<string> chars) => new(ordinal, reader, values, chars);

        /// <summary>A literal operand: its own single cell, there.</summary>
        public static PathImport Literal(long ordinal, string value) => new(ordinal, LiteralReader, CellOf(value), JsonValues.CharactersOf(value));

        /// <summary>One value, there and nothing else.</summary>
        public static PathValues CellOf(string value) => PathValues.Meet(PathValues.OneOf(ValueSet.Of(value)), PathValues.OfKinds(ValueSet.Of(JsonValues.TypeOf(value))));

        /// <summary>The ordinals a gathering holds an import at, as a rule reads them: one each where it is complete (G6).</summary>
        public static ValueSet<long> OrdinalsOf(ValueSet<PathImport> gathered) => ValueSet.From(gathered.AsEnumerable().Select(i => i.Ordinal));

        /// <summary>The readers a gathering's imports are read through, as a rule reads them (G6).</summary>
        public static ValueSet<string> ReadersOf(ValueSet<PathImport> gathered) => ValueSet.From(gathered.AsEnumerable().Select(i => i.Reader));

        /// <summary>By ordinal, then by everything else spelled, so a set holds each distinct import once.</summary>
        public int CompareTo(PathImport? other) =>
            other is null ? 1 : Ordinal != other.Ordinal ? Ordinal.CompareTo(other.Ordinal) : string.CompareOrdinal(Key, other.Key);

        string Key => RenderingImporter.Spell(Facts.JsonDocumentPath.Root, Values, Chars) + Reader;

    }

}
