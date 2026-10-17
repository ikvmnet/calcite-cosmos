using System;
using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The strings a declared <c>pattern</c> admits, read off its parse: the union of anchored alternatives, each a
    /// sequence of runs of classes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a stored form is recognised by.</b> A form is a language — a UUID's 32 nibbles and four hyphens, an
    /// instant's fixed fields, a fixed width of digits — and a pattern is that form where the strings it admits are the
    /// form's. So the question is asked of what the pattern admits rather than of how it was spelled, and these are
    /// the strings in a form where two spellings of one language read alike: a class is a set
    /// (<see cref="CosmosPatternClass"/>), a group is what it holds, a count is the run repeated, an alternation of
    /// single characters is their class, an alternation inside a sequence is distributed across it, and neighbouring
    /// runs of one class are one run. Each of those is an identity of the language, so two patterns whose strings are
    /// equal here admit the same strings; the converse is not claimed — <c>[0-4][0-9]|[5-9][0-9]</c> and <c>[0-9]{2}</c>
    /// are one language read as two — and a spelling that reads differently costs a form and never mistakes one.
    /// </para>
    /// <para>
    /// <b>Anchored, and nothing else asserted.</b> Every alternative is anchored at both ends by the parse that made
    /// these (<see cref="CosmosPatternReader"/>), so the anchors are not held; a pattern asserting anything between them
    /// — another <c>^</c>, a <c>\b</c> — has no strings here.
    /// </para>
    /// </remarks>
    sealed class CosmosPatternStrings : IEquatable<CosmosPatternStrings>
    {

        readonly CosmosPatternRun[][] _alternatives;

        CosmosPatternStrings(CosmosPatternRun[][] alternatives)
        {
            _alternatives = alternatives;
        }

        /// <summary>
        /// Gets the alternatives, each a sequence of runs with no two neighbours of one class and none empty, and no two
        /// alike.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<CosmosPatternRun>> Alternatives => _alternatives;

        /// <summary>
        /// Returns the strings of some alternatives, each put in the one form its language is held in here.
        /// </summary>
        /// <remarks>
        /// A run repeated no times matches nothing and is dropped, and the runs either side of it, if they are of one
        /// class, become one: <c>A{2}B{0}A{3}</c> is <c>A{5}</c>. An alternative written twice is held once.
        /// </remarks>
        /// <param name="alternatives">The alternatives, each a sequence of runs.</param>
        /// <returns>The strings.</returns>
        public static CosmosPatternStrings Of(IEnumerable<IEnumerable<CosmosPatternRun>> alternatives)
        {
            var read = new List<CosmosPatternRun[]>();

            foreach (var alternative in alternatives)
            {
                var runs = new List<CosmosPatternRun>();

                foreach (var run in alternative)
                {
                    if (run.Max == 0)
                        continue;

                    if (runs.Count > 0 && runs[runs.Count - 1].Class.Equals(run.Class))
                    {
                        var last = runs[runs.Count - 1];
                        runs[runs.Count - 1] = last with { Min = last.Min + run.Min, Max = last.Max + run.Max };
                        continue;
                    }

                    runs.Add(run);
                }

                if (read.Any(r => r.AsSpan().SequenceEqual(runs.ToArray())) == false)
                    read.Add(runs.ToArray());
            }

            return new CosmosPatternStrings(read.ToArray());
        }

        /// <summary>
        /// Determines whether two patterns' strings are the same union of the same sequences, in whatever order the
        /// alternatives were written.
        /// </summary>
        public bool Equals(CosmosPatternStrings? other)
        {
            if (other is null || other._alternatives.Length != _alternatives.Length)
                return false;

            foreach (var alternative in _alternatives)
                if (other._alternatives.Any(a => a.AsSpan().SequenceEqual(alternative)) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as CosmosPatternStrings);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // The alternatives are a set, so their hashes are combined in no order.
            var hash = 0;
            foreach (var alternative in _alternatives)
            {
                var one = new HashCode();
                foreach (var run in alternative)
                    one.Add(run);

                hash ^= one.ToHashCode();
            }

            return hash;
        }

        /// <summary>
        /// Writes the strings as a pattern would, for a diagnostic.
        /// </summary>
        public override string ToString() =>
            string.Join("|", _alternatives.Select(a => "^" + string.Concat(a.Select(r => r.ToString())) + "$"));

    }

}
