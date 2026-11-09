using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What one side's <c>LIKE</c> reads one unit of its pattern as, a row's (G7): <see cref="Run"/>, any run of characters;
    /// <see cref="One"/>, one code point that is in no class named beside it; <see cref="Itself"/>, the character itself;
    /// <see cref="Range"/>, a bracket opening a range; <see cref="Unmeasured"/>, what no witness measured. A unit spelled
    /// empty is every unit the side's other rows name not. Read by <see cref="CosmosAxiomRows.ReadLikeUnits"/> into
    /// <c>likeunits</c> for Calcite and by <see cref="CosmosAxiomRows.ReadServiceLikeUnits"/> into <c>likereads</c> for each
    /// target; a pattern holding a unit no row reads, or one read as neither of the first three, is undecided.
    /// </summary>
    /// <param name="Unit">The pattern's character, or empty for every other.</param>
    /// <param name="Reads">What it matches.</param>
    /// <param name="Except">The character class a <see cref="One"/> does not match, by its name in <c>JsonValues</c>; none for any other.</param>
    /// <param name="Over">The character classes the reading was measured over, by their names in <c>JsonValues</c>, the
    /// witnesses' own; <c>null</c> for every class.</param>
    /// <param name="On">The targets whose run measured the unit so, where they are fewer than the row's; <c>null</c> for
    /// every target the row was measured on.</param>
    internal sealed record CosmosLikeUnit(string Unit, string Reads, string? Except = null, IReadOnlyList<string>? Over = null, CosmosAxiomTarget? On = null)
    {

        /// <summary>Any run of characters, the empty one among them.</summary>
        public const string Run = Facts.Rendering.LikeUnit.Run;

        /// <summary>One code point.</summary>
        public const string One = Facts.Rendering.LikeUnit.One;

        /// <summary>The character itself.</summary>
        public const string Itself = Facts.Rendering.LikeUnit.Itself;

        /// <summary>A bracket opening a range of characters.</summary>
        public const string Range = Facts.Rendering.LikeUnit.Range;

        /// <summary>What no witness measured.</summary>
        public const string Unmeasured = Facts.Rendering.LikeUnit.Unmeasured;

    }

}
