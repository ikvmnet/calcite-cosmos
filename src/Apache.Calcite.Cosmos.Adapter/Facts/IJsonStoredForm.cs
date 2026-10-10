namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A stored form: how the values at a path are spelled, as a token this namespace carries and never
    /// reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The theory knows that a form is a string encoding, and nothing else about it.</b> JSON has no
    /// UUID and no instant, so a document stores one as a string, and whether every such string is
    /// spelled one way is what makes comparing the strings mean comparing the values. That is a claim
    /// about the document, and the theory derives it, combines it and answers for it. What the spelling
    /// <em>is</em> — which pattern recognises it, how to write a value in it, which comparisons it
    /// preserves under which engine's ordering — belongs to whoever consumes the claim, and lives there.
    /// </para>
    /// <para>
    /// So the one thing the theory assumes is the one thing every form shares: a value in a stored form
    /// is a string, or a null, the form saying how strings are written and nothing about whether one is
    /// there. See <see cref="JsonFact.Entails"/>.
    /// </para>
    /// <para>
    /// <b>Equality is the implementer's, and has to be value equality.</b> Two declarations of one form
    /// are one claim, and a union's meet keeps a form only where every branch states the same one.
    /// </para>
    /// </remarks>
    public interface IJsonStoredForm
    {

        /// <summary>
        /// Gets a stable name for the form, for diagnostics.
        /// </summary>
        string Name { get; }

    }

}
