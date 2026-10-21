using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// What a check's verdict depends on, so that two checks with one key share one verdict: the IR, the import spelled
    /// whole, the form's text, what is claimed of it, the set asserted under, and the frozen theory's rows hashed.
    /// </summary>
    /// <remarks>
    /// The claim is in the key because a weakening's <c>Implies</c> holds where an exact push's <c>Equal</c> does not; the
    /// theory's digest because a planted row is another theory. A planted primitive is no row, so a check that plants one
    /// keeps a memo of its own.
    /// </remarks>
    internal sealed record RenderingMemoKey(string Ir, string Import, string Form, Verdict Claim, RenderingSet Set, string Theory);

}
