using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Legality read off the IR alone, never off S's answer: which conjuncts S is asked (spec §5.2).
    /// </summary>
    internal static class RenderingFamily
    {

        /// <summary>
        /// Whether S owns a conjunct: it holds no opaque term and no parameter, anywhere within it. No list of shapes: within
        /// the family S's Unproven is the answer (O2), so since the switch (h15) S owns every reading the loader makes — a
        /// type test, a <c>CASE</c>, a truth value, an affix, a call, a pair and an element among them. A parameter stays the
        /// translator's until S reads one (PR 3B), and an opaque term, which S has no reading of, for good.
        /// </summary>
        public static bool Owns(SqlPredicate conjunct) => conjunct.Subterms.All(term => term switch
        {
            SqlPredicate.Opaque => false,
            SqlPredicate.Compare c => Owns(c.Left) && Owns(c.Right),
            SqlPredicate.In i => Owns(i.Value),
            SqlPredicate.IsNull n => Owns(n.Value),
            SqlPredicate.IsDefined d => Owns(d.Value),
            SqlPredicate.TypeTest t => Owns(t.Value),
            SqlPredicate.Affix a => Owns(a.Value),
            _ => true,
        });

        /// <summary>Whether a value holds no parameter, and no opaque term in a <c>CASE</c>'s conditions, anywhere within it.</summary>
        static bool Owns(SqlValue value) => value switch
        {
            SqlValue.Parameter => false,
            SqlValue.Cast c => Owns(c.Value),
            SqlValue.DecimalCast d => Owns(d.Value),
            SqlValue.Box b => Owns(b.Value),
            SqlValue.Call c => c.Operands.All(Owns),
            SqlValue.Case c => c.Conditions.All(Owns) && c.Arms.All(Owns),
            _ => true,
        };

        /// <summary>The reading a type test's row is keyed by, whatever reads its path: what it asks is the document's kind (<c>Calcite.TypeTestsAreTheirBodies</c>).</summary>
        public const string TypeTest = "type test";

        /// <summary>The reading a presence test's row is keyed by, whatever reads its path: what it asks is whether the document holds the path.</summary>
        public const string PresenceTest = "presence test";

        /// <summary>A reading's reader as the vocabulary's analysis names it; <c>null</c> for a call, a literal and a parameter.</summary>
        internal static string? ReaderOf(SqlValue value) => value switch
        {
            SqlValue.Text => PredicateAtom.TextReader,
            SqlValue.Raw { Column: { } column } => PredicateAtom.RawIn(column),
            SqlValue.Raw => PredicateAtom.RawReader,
            SqlValue.Typed t => PredicateAtom.TypedReader(t.Type),
            SqlValue.Query => PredicateAtom.QueryReader,
            SqlValue.Cast c => ReaderOf(c.Value) is { } inner ? PredicateAtom.CastReader(c.Target, inner) : null,
            _ => null,
        };

    }

}
