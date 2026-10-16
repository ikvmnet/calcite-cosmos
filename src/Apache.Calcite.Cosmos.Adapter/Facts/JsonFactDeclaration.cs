using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A declaration compiled into one generation: its frozen context, and the vocabulary its forms and extensions are
    /// written in.
    /// </summary>
    /// <param name="Generation">The generation, whose graph the context belongs to.</param>
    /// <param name="Context">The declaration's context, frozen.</param>
    /// <param name="Vocabulary">The tokens of the declaration's forms and extensions.</param>
    /// <param name="Unconditional">
    /// Where no rule of the declaration is conditional, what <see cref="JsonFactOverlay"/> reads beside what a filter
    /// establishes without the engine; <c>null</c> where one is.
    /// </param>
    internal sealed record JsonFactDeclaration(JsonFactGenerations.Generation Generation, Scope Context, JsonFactVocabulary Vocabulary, JsonFactUnconditional? Unconditional = null);

}
