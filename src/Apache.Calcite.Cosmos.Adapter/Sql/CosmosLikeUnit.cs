namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What Calcite's <c>LIKE</c> reads one unit of its pattern as, a <c>Calcite</c> row's (G7): <see cref="Run"/>, any run of
    /// characters; <see cref="One"/>, one code point that is in no class named beside it; <see cref="Itself"/>, the character
    /// itself. A unit spelled empty is every unit no other row names. Read by <see cref="CosmosAxiomRows.ReadLikeUnits"/> into
    /// <c>likeunits</c>; a pattern holding a unit no row reads is undecided.
    /// </summary>
    /// <param name="Unit">The pattern's character, or empty for every other.</param>
    /// <param name="Reads">What it matches.</param>
    /// <param name="Except">The character class a <see cref="One"/> does not match, by its name in <c>JsonValues</c>; none for any other.</param>
    internal sealed record CosmosLikeUnit(string Unit, string Reads, string? Except = null)
    {

        /// <summary>Any run of characters, the empty one among them.</summary>
        public const string Run = Facts.Rendering.LikeUnit.Run;

        /// <summary>One code point.</summary>
        public const string One = Facts.Rendering.LikeUnit.One;

        /// <summary>The character itself.</summary>
        public const string Itself = Facts.Rendering.LikeUnit.Itself;

    }

}
