using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A text the translator sent, read: the form, and the parse printed back with the source's own parentheses and
    /// parameter names, which must be the text sent, byte for byte.
    /// </summary>
    internal sealed record ReadForm(ServiceForm Form, string Written);

}
