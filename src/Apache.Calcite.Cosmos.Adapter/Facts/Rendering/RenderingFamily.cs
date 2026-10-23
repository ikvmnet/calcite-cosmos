using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Rendering.ReadingPrecisionTable.ReadingPrecision;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Legality read off the IR alone, never off S's answer: which conjuncts generate mode writes, and which a stratum a
    /// later round is stated beneath may rest on (spec §5.2).
    /// </summary>
    internal static class RenderingFamily
    {

        /// <summary>
        /// Whether S owns a conjunct: it holds no opaque term and no parameter, every comparison is an atom, a reading
        /// against a literal, and every reading has an edges row. No list of shapes: within the family S's Unproven is the
        /// answer (O2), and a parameter stays the translator's.
        /// </summary>
        public static bool Owns(SqlPredicate conjunct) => conjunct.Subterms.All(term => term switch
        {
            SqlPredicate.Opaque => false,
            SqlPredicate.Compare c => c.Right is SqlValue.Literal && HasEdges(c.Left),
            SqlPredicate.In i => HasEdges(i.Value),
            SqlPredicate.IsNull n => HasEdges(n.Value),
            SqlPredicate.IsDefined d => HasEdges(d.Value),
            _ => true,
        });

        /// <summary>
        /// Whether the facts graph reads every atom of a conjunct as Calcite does on TRUE and FALSE, by the reading
        /// precision's rows, so that what a stratum that assumes it says of the documents is so. A presence test, and an
        /// opaque term, from which the facts graph learns nothing, exclude nothing.
        /// </summary>
        public static bool ReadExactly(SqlPredicate conjunct) => conjunct.Subterms.All(term => term switch
        {
            SqlPredicate.Compare c => ReadExactly(c.Left, c.Right is SqlValue.Literal { IsNumber: true }),
            SqlPredicate.In i => ReadExactly(i.Value, i.Points.Any(JsonValues.IsNumber)),
            SqlPredicate.IsNull n => ReadExactly(n.Value, false),
            _ => true,
        });

        static bool ReadExactly(SqlValue reading, bool besideNumber) => (ReaderOf(reading) is { } reader ? ReadingPrecisionTable.Of(reader) : Coarse) switch
        {
            Exact => true,
            ExactBesideNoNumber => besideNumber == false,
            _ => false,
        };

        static bool HasEdges(SqlValue value) => ReaderOf(value) is { } reader && ReaderEdgesTable.Rows.ContainsKey(reader);

        /// <summary>A reading's reader as the vocabulary's analysis names it; <c>null</c> for a call, a literal and a parameter.</summary>
        internal static string? ReaderOf(SqlValue value) => value switch
        {
            SqlValue.Text => PredicateAtom.TextReader,
            SqlValue.Raw => PredicateAtom.RawReader,
            SqlValue.Typed t => PredicateAtom.TypedReader(t.Type),
            SqlValue.Cast c => ReaderOf(c.Value) is { } inner ? PredicateAtom.CastReader(c.Target, inner) : null,
            _ => null,
        };

    }

}
