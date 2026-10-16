using System;
using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A filter's conjuncts as the oracle reads them: each in the IR, beside what it was loaded from, and the paths the
    /// statement reads or will be asked about.
    /// </summary>
    /// <remarks>
    /// The conjuncts keep the filter's order, which is what a question names one by. The digest does not: it is the
    /// conjuncts' digests in order, and the paths, so that two filters differing only in the order of their conjuncts
    /// are answered by one tree of contexts.
    /// </remarks>
    internal sealed class PredicateStatement
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="conjuncts">The conjuncts, in the filter's order.</param>
        /// <param name="sources">What each conjunct was loaded from — a <c>RexNode</c> — for a renderer to render; or <c>null</c>.</param>
        /// <param name="mentions">Paths a question will be asked about beside those the conjuncts read.</param>
        public PredicateStatement(IReadOnlyList<SqlPredicate> conjuncts, IReadOnlyList<object?>? sources = null, IEnumerable<JsonDocumentPath>? mentions = null)
        {
            if (conjuncts is null)
                throw new ArgumentNullException(nameof(conjuncts));

            if (sources is not null && sources.Count != conjuncts.Count)
                throw new ArgumentException("One source per conjunct.", nameof(sources));

            Conjuncts = conjuncts.ToArray();
            Sources = sources?.ToArray() ?? new object?[conjuncts.Count];
            Paths = Conjuncts.SelectMany(c => c.Paths).Concat(mentions ?? Array.Empty<JsonDocumentPath>()).Distinct().ToArray();
            Digest = string.Join(" & ", Conjuncts.Select(c => c.Digest).OrderBy(d => d, StringComparer.Ordinal))
                + " @ " + string.Join(" ", Paths.Select(SqlValue.Spell).OrderBy(p => p, StringComparer.Ordinal));
        }

        /// <summary>
        /// Gets the conjuncts, in the filter's order.
        /// </summary>
        public IReadOnlyList<SqlPredicate> Conjuncts { get; }

        /// <summary>
        /// Gets what each conjunct was loaded from, parallel to <see cref="Conjuncts"/>.
        /// </summary>
        public IReadOnlyList<object?> Sources { get; }

        /// <summary>
        /// Gets every path the statement reads or mentions.
        /// </summary>
        public IReadOnlyList<JsonDocumentPath> Paths { get; }

        /// <summary>
        /// Gets the canonical digest: the conjuncts and the paths, each in order.
        /// </summary>
        public string Digest { get; }

        /// <inheritdoc />
        public override string ToString() => Digest;

    }

}
