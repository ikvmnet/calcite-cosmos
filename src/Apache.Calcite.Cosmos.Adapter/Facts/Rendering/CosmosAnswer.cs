namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What one target answered for one key: what a <c>WHERE</c> and a <c>WHERE NOT</c> keep, or the comparator a type is
    /// ordered by. <see cref="AnswerMeanings"/> says what each means.
    /// </summary>
    internal enum CosmosAnswer
    {

        /// <summary>A <c>WHERE</c> keeps the row.</summary>
        True,

        /// <summary>A <c>WHERE NOT</c> keeps the row.</summary>
        False,

        /// <summary>Neither keeps it: undefined, or a value that is no boolean.</summary>
        Undefined,

        /// <summary>A <c>WHERE</c> does not keep it, and <c>NOT</c> was not measured.</summary>
        NotKept,

        /// <summary>The statement fails.</summary>
        Fails,

        /// <summary>Strings ordered by code point, which is UTF-8 byte order, U+0000 the least.</summary>
        CodePoint,

        /// <summary>Strings ordered by UTF-16 code unit.</summary>
        Utf16,

        /// <summary>
        /// Numbers ordered by the value compared: an int64 as itself, any other number as its correctly rounded double, and
        /// the two by value (<c>Service.NumbersBeyondLongCompareAsDoubles</c>).
        /// </summary>
        Numeric,

        /// <summary>
        /// Strings ordered by code point as the emulator stores them, a U+0000 as U+FDD0 followed by <c>0</c> and a U+FDD0
        /// as U+FDD0 followed by <c>1</c> (<see cref="EmulatorStoredText"/>, <c>Service.StringOrderIsCodePoint</c>); a value
        /// compared with them is compared as it is sent, so a statement sends it in that spelling.
        /// </summary>
        StoredCodePoint,

        /// <summary>
        /// Numbers ordered as <see cref="Numeric"/> orders them, but a whole number written with a point held as its double,
        /// as the emulator holds one (<c>Service.IntegralNumbersReadBackAsHeld</c>): 9007199254740993.0 as 9007199254740992,
        /// where Azure holds it as the int64 it is.
        /// </summary>
        NumericPointedAsDouble,

    }

}
