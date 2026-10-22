using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// What a check's verdict depends on, so that two checks with one key share one verdict: the IR, the import spelled
    /// whole, the form's text, what is claimed of it, the set asserted under, and the frozen theory's rows hashed.
    /// </summary>
    /// <remarks>Why the claim and the theory are in it: <c>DESIGN.md</c>, <i>The run decides whether a verdict may be read</i>.</remarks>
    internal sealed record RenderingMemoKey(string Ir, string Import, string Form, Verdict Claim, RenderingSet Set, string Theory);

}
