namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the service is asked of one path, against at most one operand: the first column of a measured row's key.
    /// </summary>
    /// <remarks>
    /// The <c>IS_*</c> tests and the comparisons are their Cosmos SQL; <see cref="In"/> is read once its rows are entered.
    /// A connective's rows are keyed by its operands' outcomes.
    /// </remarks>
    internal enum CosmosServiceTest
    {

        IsDefined,
        IsNull,
        IsPrimitive,
        IsString,
        IsNumber,
        IsBool,
        IsObject,
        IsArray,
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        In,

        /// <summary>A path written as a whole condition, read as a <c>WHERE</c> reads the value it holds.</summary>
        Bare,

        /// <summary><c>true</c> or <c>false</c> written as a condition.</summary>
        Constant,

        Not,
        And,
        Or,

        /// <summary>Two orderings of one path against constant bounds, the one shape measured not to be pointwise.</summary>
        Range,

        /// <summary>Which comparator a type's values are ordered by: the answer names it.</summary>
        Order,

        /// <summary>
        /// The ternary <c>c ? a : b</c>: keyed by the condition's outcome alone, the answer <see cref="CosmosAnswer.True"/>
        /// where it takes the then-arm and <see cref="CosmosAnswer.False"/> where it takes the else-arm.
        /// </summary>
        Choose,

        /// <summary>
        /// A comparison of two paths: keyed by nothing, the answer <see cref="CosmosAnswer.True"/> where it is measured to
        /// answer as the left compared with the right's value, and mirrored where only the mirror's key is a row.
        /// </summary>
        ComparePaths,

    }

}
