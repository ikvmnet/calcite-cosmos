using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What a reading answers over a stored value of one kind in process, a <c>Calcite</c> row's (F3): the Calcite twin of
    /// <see cref="CosmosFunctionOutcome"/>, as <c>Calcite.CastToBooleanParses</c> measured a cast of the accessor to
    /// <c>BOOLEAN</c>. Read by <see cref="CosmosAxiomRows.ReadCalciteReadings"/> into S's <c>calcitereads</c>, and the classes
    /// its witnesses of a string held into <c>calcitebands</c>: what lies beyond them was not measured.
    /// </summary>
    /// <param name="Reader">The reading, by the name a reader takes.</param>
    /// <param name="Kind">The class of the stored value.</param>
    /// <param name="Answer">What it answers: <see cref="Itself"/>, <see cref="Renders"/>, <see cref="Null"/>, <see cref="Raises"/> or <see cref="Parses"/>.</param>
    /// <param name="Bands">The character classes the witnesses of a string held, by their members (<c>JsonValues.CharacterClasses</c>);
    /// none for any other kind.</param>
    internal sealed record CosmosCalciteReading(string Reader, CosmosValueClass Kind, string Answer, IReadOnlyList<string>? Bands = null)
    {

        /// <summary>The value itself, as the reading's type holds it.</summary>
        public const string Itself = CalciteRead.Itself;

        /// <summary>The value as Calcite renders it as text.</summary>
        public const string Renders = CalciteRead.Renders;

        /// <summary>SQL null.</summary>
        public const string Null = CalciteRead.Null;

        /// <summary>The statement fails.</summary>
        public const string Raises = CalciteRead.Raises;

        /// <summary>The rendering parsed as the reading's type: a value where it spells one, a raise where it does not.</summary>
        public const string Parses = CalciteRead.Parses;

    }

}
