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

        readonly Func<long, SqlValue?>? _read;
        IReadOnlyDictionary<long, SqlValue>? _opaques;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="conjuncts">The conjuncts, in the filter's order.</param>
        /// <param name="sources">What each conjunct was loaded from — a <c>RexNode</c> — for a renderer to render; or <c>null</c>.</param>
        /// <param name="mentions">Paths a question will be asked about beside those the conjuncts read.</param>
        /// <param name="opaques">Reads an opaque predicate of the conjuncts as a value, by its id, when <see cref="Opaques"/> is first asked.</param>
        public PredicateStatement(IReadOnlyList<SqlPredicate> conjuncts, IReadOnlyList<object?>? sources = null, IEnumerable<JsonDocumentPath>? mentions = null,
            Func<long, SqlValue?>? opaques = null)
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
            _read = opaques;
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

        /// <summary>
        /// Gets the value the loader reads each opaque predicate of the conjuncts as, by its id, where it reads one: its top
        /// call, with each operand the loader reads as a value and none it does not, or the value it is, a reading of a
        /// boolean, say. Read when first asked and outside <see cref="Digest"/>, so nothing the facts graph remembers a
        /// statement by moves, and nothing planning asks; what it is strict in is the rendering graph's rules'.
        /// </summary>
        public IReadOnlyDictionary<long, SqlValue> Opaques => _opaques ??= Conjuncts.SelectMany(c => c.Subterms).OfType<SqlPredicate.Opaque>().Select(o => o.Id).Distinct()
            .Select(id => (Id: id, Value: _read?.Invoke(id))).Where(o => o.Value is not null).ToDictionary(o => o.Id, o => o.Value!);

        /// <inheritdoc />
        public override string ToString() => Digest;

    }

}
