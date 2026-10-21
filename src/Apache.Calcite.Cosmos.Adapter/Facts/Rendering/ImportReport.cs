namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What an import copied: how many paths; of those the facts graph held no row for, how many the declaration says
    /// nothing at, within or above, where the top is exact, and which it constrains, spelled as a cell spells its path,
    /// where the top loses precision; the import spelled whole, which a memo of its verdicts keys on; and whether the
    /// node keeps no document, when nothing is imported.
    /// </summary>
    internal sealed record ImportReport(int Paths, int DefaultedOpen, Eggnog.ValueSet<string> Constrained, string Digest, bool Contradicted);

}
