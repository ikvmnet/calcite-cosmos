using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What S's rules hand the leaf beside its table, each a set they derived from rows (G1): the leaf tests them on concrete
    /// cells and names no band, character or comparator itself (<c>DESIGN.md</c>, <i>Forms, rows, and the service on a cell</i>).
    /// </summary>
    /// <param name="Parted">The literals over the leaf's path whose order against a range its bounds do not say: a band a
    /// comparator ranks apart from Calcite's or moves (<c>parts</c>, <c>moves</c>).</param>
    /// <param name="Unplaced">The literals that lie anywhere against every string, holding a band a comparator moves.</param>
    /// <param name="Loose">The literals a stored string may equal that a range does not hold (<c>collides</c>).</param>
    /// <param name="Unread">The bands a cell, a point or a range, holding one is matched by no reading of the leaf's: a band
    /// its test reads a string only up to (<c>truncates</c>), or one a target stores as what its pattern holds (<c>storedas</c>).</param>
    /// <param name="Uncut">The bands a range holding one is not told by its affix cut, a band a matcher's spelling rewrites
    /// where that splits the leaf's pattern (<c>rewrites</c>).</param>
    /// <param name="Blind">The bands the leaf's pattern holds that its test reads a string only up to: none, or every cell is
    /// unread.</param>
    /// <param name="Departs">The pairs of bands the comparators of a pair's set rank apart from Calcite's order, where a pair's
    /// two places may hold them (<c>parts</c>).</param>
    /// <param name="Moved">The bands the comparators of a pair's set move, where its places may hold them (<c>moves</c>).</param>
    internal sealed record LeafReads(ValueSet<string> Parted, ValueSet<string> Unplaced, ValueSet<string> Loose, ValueSet<string> Unread,
        ValueSet<string> Uncut, ValueSet<string> Blind, ValueSet<(string Band, string Other)> Departs, ValueSet<string> Moved)
    {

        /// <summary>Nothing beside the table: a leaf S's rules handed no set.</summary>
        public static LeafReads None { get; } = new(ValueSet<string>.Empty, ValueSet<string>.Empty, ValueSet<string>.Empty, ValueSet<string>.Empty,
            ValueSet<string>.Empty, ValueSet<string>.Empty, ValueSet<(string, string)>.Empty, ValueSet<string>.Empty);

        /// <summary>What a comparison with a literal reads: the literals parted, unplaced and loose.</summary>
        public static LeafReads Places(ValueSet<string> parted, ValueSet<string> unplaced, ValueSet<string> loose) =>
            None with { Parted = parted, Unplaced = unplaced, Loose = loose };

        /// <summary>What an affix reads: the bands unread and uncut, and those that blind it.</summary>
        public static LeafReads Matches(ValueSet<string> unread, ValueSet<string> uncut, ValueSet<string> blind) =>
            None with { Unread = unread, Uncut = uncut, Blind = blind };

        /// <summary>What a comparison of two paths reads: the bands departing and moved.</summary>
        public static LeafReads Pairs(ValueSet<(string Band, string Other)> departs, ValueSet<string> moved) =>
            None with { Departs = departs, Moved = moved };

        /// <summary>The three together, as the evaluation rule hands them (a <c>Compute</c> takes four operands).</summary>
        public static LeafReads Of(LeafReads places, LeafReads matches, LeafReads pairs) =>
            places with { Unread = matches.Unread, Uncut = matches.Uncut, Blind = matches.Blind, Departs = pairs.Departs, Moved = pairs.Moved };

    }

}
