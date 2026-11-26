namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The evaluators a <c>Calcite</c> row may name for a reading (<c>calciteevaluator</c>, G7): each one way of evaluating
    /// an atom of the reading on a cell, as the reading's rows say. Which one reads a reading is the rows', never its
    /// spelling's; over a class no rule derives a reading for, a reading is read by its rows alone.
    /// </summary>
    internal enum CalciteEvaluator
    {

        /// <summary>The accessor's text: a string itself, a number and a boolean their renderings, anything else null.</summary>
        Text,

        /// <summary>A raw value's cast to text, as its columns answer on the cell's key (G5).</summary>
        RawText,

        /// <summary>A raw value, as its columns answer on the cell's key (G5).</summary>
        Raw,

        /// <summary>A value of the SQL type the row names, as <c>RETURNING</c> and the column the reader fills read it.</summary>
        Typed,

        /// <summary>The rendering parsed as a number of the SQL type the row names.</summary>
        NumberCast,

        /// <summary>The rendering parsed as a boolean, class by class as the reading's rows say.</summary>
        BooleanCast,

        /// <summary>A raise over every value: what a test's body reads over a reading it cannot take apart.</summary>
        Raising,

    }

}
