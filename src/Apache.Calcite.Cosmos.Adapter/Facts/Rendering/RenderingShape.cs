using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The shape a query's cost line is read under (spec §6.3), off the union of the atoms of every root its plan asked S
    /// of: an atom is a predicate other than a junction, a negation or an opaque term, holding no parameter; a list is an
    /// <c>IN</c> of five or more points, one of two to four counting as one condition.
    /// </summary>
    /// <remarks>
    /// The union, not the widest statement: a query whose conditions are split across filter statements is read by all of
    /// them, as the lines were set.
    /// </remarks>
    internal static class RenderingShape
    {

        /// <summary>The shapes, from the narrowest.</summary>
        public const string Few = "1-3 conditions", Four = "4 conditions", Several = "5-7 conditions", List = "a list of 5-19", Rare = "rare";

        /// <summary>The shapes in order, from the narrowest.</summary>
        public static IReadOnlyList<string> All { get; } = [Few, Four, Several, List, Rare];

        /// <summary>The shape of what was asked: the union of the roots' atoms, each counted once.</summary>
        public static string Of(IEnumerable<SqlPredicate> asked)
        {
            var atoms = asked.SelectMany(r => r.Subterms).Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque)
                && t is not (SqlPredicate.Compare { Left: SqlValue.Parameter } or SqlPredicate.Compare { Right: SqlValue.Parameter })).DistinctBy(t => t.Digest).ToList();
            var list = atoms.OfType<SqlPredicate.In>().Select(i => i.Points.Count).DefaultIfEmpty(0).Max();
            var conditions = atoms.Count(t => t is not SqlPredicate.In { Points.Count: >= 5 });
            return list >= 20 || conditions >= 8 ? Rare : list >= 5 ? List : conditions switch { <= 3 => Few, 4 => Four, _ => Several };
        }

    }

}
