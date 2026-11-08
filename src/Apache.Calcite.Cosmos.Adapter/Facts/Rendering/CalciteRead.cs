namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a reading answers over a stored value of one kind in process, as a <c>Calcite</c> row states it (F3): S's
    /// <c>calcitereads</c> and <c>rawreads</c> hold these spellings, and its rules match them.
    /// </summary>
    internal static class CalciteRead
    {

        /// <summary>The value itself, as the reading's type holds it: a string the string, a raw value the value.</summary>
        public const string Itself = "itself";

        /// <summary>The value as Calcite renders it as text: a number in Java's spelling, a boolean <c>true</c> or <c>false</c>.</summary>
        public const string Renders = "renders";

        /// <summary>SQL null.</summary>
        public const string Null = "null";

        /// <summary>The statement fails.</summary>
        public const string Raises = "raises";

        /// <summary>The rendering parsed as the reading's type: a value where it spells one, a raise where it does not.</summary>
        public const string Parses = "parses";

    }

}
