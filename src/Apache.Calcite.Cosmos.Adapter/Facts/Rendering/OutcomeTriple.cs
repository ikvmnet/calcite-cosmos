namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The three outcome vectors of a ternary's parts on one partition, packed as one value, so that its evaluation is a
    /// computation of four operands at most, as Eggnog 1.1.1 allows (spec §8). Packing only: it decides nothing.
    /// </summary>
    /// <param name="Condition">The condition's vector.</param>
    /// <param name="Then">The then-arm's.</param>
    /// <param name="Otherwise">The else-arm's.</param>
    internal sealed record OutcomeTriple(OutcomeVector Condition, OutcomeVector Then, OutcomeVector Otherwise)
    {

        /// <summary>The three, as a rule computes them.</summary>
        public static OutcomeTriple Of(OutcomeVector condition, OutcomeVector then, OutcomeVector otherwise) => new(condition, then, otherwise);

    }

}
