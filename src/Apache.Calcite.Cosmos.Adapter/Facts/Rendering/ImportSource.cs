namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The one facts-graph node a bundle's imports are copied from: the statement, by its digest; the licence that chose
    /// the node; and the round that rendered from it. A bundle accepts one, so it never holds two nodes' facts.
    /// </summary>
    internal sealed record ImportSource(string Statement, string Licence, int Round);

}
