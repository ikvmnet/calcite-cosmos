namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The one facts-graph node a bundle's imports are copied from: the statement, by its digest; the licence that chose
    /// the node; the round that rendered from it; and the node itself, which the importer sets and which compares by
    /// reference, since the rest names no generation and no root. A bundle accepts one, so it never holds two nodes' facts.
    /// </summary>
    internal sealed record ImportSource(string Statement, string Licence, int Round, Eggnog.Scope? Node = null);

}
