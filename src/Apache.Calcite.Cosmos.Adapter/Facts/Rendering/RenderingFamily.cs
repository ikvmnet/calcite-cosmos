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
        /// answer (O2), and a parameter stays the translator's. A type test, a <c>CASE</c>, an affix, a truth value and a
        /// reading of an element the loader reads since h07 stay the translator's until the switch (spec §4.2 h15).
        /// </summary>
        public static bool Owns(SqlPredicate conjunct) => conjunct.Paths.Any(p => p.HasElement) == false && conjunct.Subterms.All(term => term switch
        {
            SqlPredicate.Opaque => false,
            SqlPredicate.TypeTest or SqlPredicate.Case or SqlPredicate.Affix or SqlPredicate.Constant => false,
            SqlPredicate.Compare c => Unboxed(c.Right) is SqlValue.Literal && HasEdges(Unboxed(c.Left)),
            SqlPredicate.In i => HasEdges(i.Value),
            SqlPredicate.IsNull n => HasEdges(Unboxed(n.Value)),
            SqlPredicate.IsDefined d => HasEdges(d.Value),
            _ => true,
        });

        /// <summary>
        /// Whether the facts graph reads every atom of a conjunct as Calcite does on TRUE and FALSE, by the reading
        /// precision's rows, so that what a stratum that assumes it says of the documents is so. A presence test, and an
        /// opaque term and two readings compared, from which the facts graph learns nothing, exclude nothing; nor do a junction, a negation, a
        /// <c>CASE</c> and a truth value, of which no rule of its reads more than their atoms, each read here in its turn. A
        /// kind not named is not read exactly.
        /// </summary>
        public static bool ReadExactly(SqlPredicate conjunct) => conjunct.Subterms.All(term => term switch
        {
            // Two readings compared (h11) are no atom of the facts graph's, which learns nothing from them.
            SqlPredicate.Compare c when Unboxed(c.Right) is not (SqlValue.Literal or SqlValue.Parameter) => true,
            SqlPredicate.Compare c => ReadExactly(Unboxed(c.Left), Unboxed(c.Right) is SqlValue.Literal { IsNumber: true }),
            SqlPredicate.In i => ReadExactly(i.Value, i.Points.Any(JsonValues.IsNumber)),
            SqlPredicate.IsNull n => ReadExactly(Unboxed(n.Value), false),
            SqlPredicate.Affix a => ReadExactly(a.Value, false),
            SqlPredicate.TypeTest => ReadingPrecisionTable.Of(ReadingPrecisionTable.TypeTest) == Exact,
            SqlPredicate.IsDefined or SqlPredicate.Opaque or SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Case or SqlPredicate.Constant => true,
            _ => false,
        });

        static bool ReadExactly(SqlValue reading, bool besideNumber) => (ReaderOf(reading) is { } reader ? ReadingPrecisionTable.Of(reader) : Coarse) switch
        {
            Exact => true,
            ExactBesideNoNumber => besideNumber == false,
            _ => false,
        };

        static bool HasEdges(SqlValue value) => ReaderOf(value) is { } reader && ReaderEdgesTable.Rows.ContainsKey(reader);

        /// <summary>
        /// What a value cast to <c>VARIANT</c> holds, which the family is keyed by as the IR was before S read the box (G8), so
        /// that the family is what it was; the value itself otherwise. Deleted with the family's shape list at h15 (G10).
        /// </summary>
        static SqlValue Unboxed(SqlValue value) => value is SqlValue.Box box ? box.Value : value;

        /// <summary>A reading's reader as the vocabulary's analysis names it; <c>null</c> for a call, a literal and a parameter.</summary>
        internal static string? ReaderOf(SqlValue value) => value switch
        {
            SqlValue.Text => PredicateAtom.TextReader,
            SqlValue.Raw { Column: { } column } => PredicateAtom.RawIn(column),
            SqlValue.Raw => PredicateAtom.RawReader,
            SqlValue.Typed t => PredicateAtom.TypedReader(t.Type),
            SqlValue.Cast c => ReaderOf(c.Value) is { } inner ? PredicateAtom.CastReader(c.Target, inner) : null,
            _ => null,
        };

    }

}
