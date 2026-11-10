using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The one facts-graph node a bundle's imports are copied from: the statement, by its digest; the licence that chose
    /// the node; the round that rendered from it; and the node itself, which the importer sets and which compares by
    /// reference, since the rest names no generation and no root. A bundle imports from one, so it never meets two nodes'
    /// facts; a later round's stratum is stated beside the atoms of the conjunct rendered from it alone, and an arm's, the
    /// stratum the bundle opened for it, beside the atoms tagged with it (<see cref="RenderingBundle.Assume"/>). An arm's
    /// atom is named by the conditions it is read beneath, outermost first, before any bundle numbers its stratum
    /// (<see cref="RenderingBundle.ArmOf"/>, h15).
    /// </summary>
    internal sealed record ImportSource(string Statement, string Licence, int Round, Eggnog.Scope? Node = null, long? Stratum = null, IReadOnlyList<SqlPredicate>? Arm = null)
    {

        /// <summary>
        /// What joins the statements a test's root names, where it names several: no digest holds it, where one holds
        /// <c>+</c>, an arithmetic's, which S reads since the switch (h15).
        /// </summary>
        public const char Separator = '\u001F';

        /// <summary>Whether a statement is this source's, or one of those it names.</summary>
        public bool Names(string statement) => Statement == statement || Statement.Split(Separator).Contains(statement);

    }

}
