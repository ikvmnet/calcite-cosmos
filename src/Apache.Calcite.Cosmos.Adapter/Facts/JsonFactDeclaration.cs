using System;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A declaration compiled into one generation: its frozen context, the vocabulary its forms and extensions are written
    /// in, and the context read as a fact set.
    /// </summary>
    /// <param name="Generation">The generation, whose graph the context belongs to.</param>
    /// <param name="Context">The declaration's context, frozen.</param>
    /// <param name="Vocabulary">The tokens of the declaration's forms and extensions.</param>
    /// <param name="Declared">The context as a fact set: what every statement's root reads, so that the paths a root is
    /// told of are the declaration's, shared by every statement beneath it.</param>
    /// <param name="DeclaredPossible">The context's <c>D0</c>, a frozen child with a <c>possible</c> row for every path the
    /// declaration mentions, opened on first call; <c>null</c> where it states nothing. Every statement is rooted there
    /// (<see cref="StatementTerms.Root"/>).</param>
    internal sealed record JsonFactDeclaration(JsonFactGenerations.Generation Generation, Scope Context, JsonFactVocabulary Vocabulary, JsonFactSet Declared, Func<Scope?> DeclaredPossible);

}
