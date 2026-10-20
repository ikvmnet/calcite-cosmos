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

        /// <summary>Strings ordered by code point, which is UTF-8 byte order.</summary>
        CodePoint,

        /// <summary>Strings ordered by UTF-16 code unit.</summary>
        Utf16,

        /// <summary>Numbers ordered by value.</summary>
        Numeric,

    }

}
