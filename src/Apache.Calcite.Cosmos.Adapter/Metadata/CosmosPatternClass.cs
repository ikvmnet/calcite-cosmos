using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The characters one atom of a declared <c>pattern</c> matches: a set, held as the sorted, disjoint and
    /// non-adjacent spans of the characters in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A set has one form however it was written.</b> <c>[0-9]</c>, <c>\d</c>, <c>[0123456789]</c>,
    /// <c>[5-90-4]</c> and <c>(?:[0-4]|[5-9])</c> are one class, and so are <c>-</c>, <c>[-]</c> and <c>\x2D</c>: the
    /// spans are merged where they touch, so two classes are equal exactly where they hold the same characters. That
    /// is what lets a stored form be recognised by what a pattern admits rather than by how it was spelled
    /// (<see cref="CosmosPatternStrings"/>).
    /// </para>
    /// <para>
    /// A character is a UTF-16 unit or a code point, as the reading that made the class reads one
    /// (<see cref="CosmosPatternReader"/>). The two are one below U+D800 and from U+E000 to U+FFFF, which is everything a
    /// pattern's strings are read for.
    /// </para>
    /// </remarks>
    sealed class CosmosPatternClass : IEquatable<CosmosPatternClass>
    {

        readonly (int Lo, int Hi)[] _spans;

        CosmosPatternClass((int Lo, int Hi)[] spans)
        {
            _spans = spans;
        }

        /// <summary>
        /// Gets the spans, sorted, disjoint and with a gap between every two.
        /// </summary>
        public IReadOnlyList<(int Lo, int Hi)> Spans => _spans;

        /// <summary>
        /// Gets the bands the class's characters hold units of, read as the reader reads a range's: every unit, or code
        /// point, a span covers (<see cref="Facts.JsonFactEncoding.Characters(int, int)"/>).
        /// </summary>
        public Facts.JsonCharacterBand Characters
        {
            get
            {
                var bands = Facts.JsonCharacterBand.None;
                foreach (var (lo, hi) in _spans)
                    bands |= Facts.JsonFactEncoding.Characters(lo, hi);

                return bands;
            }
        }

        /// <summary>
        /// Returns the class holding every character of some spans, in whatever order and overlap they were written.
        /// </summary>
        /// <param name="spans">The spans, each from its first character to its last.</param>
        /// <returns>The class.</returns>
        public static CosmosPatternClass Of(IEnumerable<(int Lo, int Hi)> spans)
        {
            var sorted = new List<(int Lo, int Hi)>(spans);
            sorted.Sort((a, b) => a.Lo != b.Lo ? a.Lo.CompareTo(b.Lo) : a.Hi.CompareTo(b.Hi));

            var merged = new List<(int Lo, int Hi)>(sorted.Count);
            foreach (var (lo, hi) in sorted)
            {
                if (merged.Count > 0 && lo <= merged[merged.Count - 1].Hi + 1)
                    merged[merged.Count - 1] = (merged[merged.Count - 1].Lo, Math.Max(merged[merged.Count - 1].Hi, hi));
                else
                    merged.Add((lo, hi));
            }

            return new CosmosPatternClass(merged.ToArray());
        }

        /// <summary>
        /// Returns the class of the characters from one to another.
        /// </summary>
        public static CosmosPatternClass Range(char lo, char hi) => new(new[] { ((int)lo, (int)hi) });

        /// <summary>
        /// Returns the class of one character.
        /// </summary>
        public static CosmosPatternClass Of(char c) => Range(c, c);

        /// <summary>
        /// Returns the class holding the characters of this one and another: what an alternation of two single
        /// characters, or of two classes, matches.
        /// </summary>
        public CosmosPatternClass Union(CosmosPatternClass other)
        {
            var spans = new List<(int Lo, int Hi)>(_spans);
            spans.AddRange(other._spans);
            return Of(spans);
        }

        /// <summary>
        /// Determines whether the class is exactly one character.
        /// </summary>
        public bool Is(char c) => _spans.Length == 1 && _spans[0].Lo == c && _spans[0].Hi == c;

        /// <summary>
        /// Determines whether every character in the class lies in the basic plane outside the surrogates, where a unit
        /// and a code point are one character, so that the class means the same under either reading.
        /// </summary>
        public bool IsPlain()
        {
            foreach (var (lo, hi) in _spans)
                if (hi > 0xFFFF || lo <= 0xDFFF && hi >= 0xD800)
                    return false;

            return true;
        }

        /// <summary>
        /// Returns the characters of the class where it holds no more than some number of them, or <c>null</c>.
        /// </summary>
        /// <param name="most">The most characters to list.</param>
        /// <returns>The characters in order, or <c>null</c> where there are more.</returns>
        public string? Members(int most)
        {
            var count = 0L;
            foreach (var (lo, hi) in _spans)
                count += hi - lo + 1L;

            if (count > most || IsPlain() == false)
                return null;

            var members = new StringBuilder((int)count);
            foreach (var (lo, hi) in _spans)
                for (var c = lo; c <= hi; c++)
                    members.Append((char)c);

            return members.ToString();
        }

        /// <inheritdoc />
        public bool Equals(CosmosPatternClass? other) => other is not null && _spans.AsSpan().SequenceEqual(other._spans);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as CosmosPatternClass);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var span in _spans)
                hash.Add(span);

            return hash.ToHashCode();
        }

        /// <summary>
        /// Writes the class as a pattern would, for a diagnostic.
        /// </summary>
        public override string ToString()
        {
            var text = new StringBuilder("[");
            foreach (var (lo, hi) in _spans)
            {
                text.Append(Write(lo));
                if (hi != lo)
                    text.Append('-').Append(Write(hi));
            }

            return text.Append(']').ToString();

            static string Write(int c) =>
                c is >= 0x21 and <= 0x7E && "\\]^-[".IndexOf((char)c) < 0
                    ? ((char)c).ToString()
                    : c <= 0xFFFF ? "\\u" + c.ToString("X4", CultureInfo.InvariantCulture) : "\\u{" + c.ToString("X", CultureInfo.InvariantCulture) + "}";
        }

    }

}
