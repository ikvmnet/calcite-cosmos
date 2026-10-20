namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the service is asked of one path, against at most one operand: the first column of a measured row's key.
    /// </summary>
    /// <remarks>
    /// The <c>IS_*</c> tests and the comparisons are their Cosmos SQL. <see cref="In"/>, <see cref="StartsWith"/>,
    /// <see cref="EndsWith"/>, <see cref="Contains"/> and <see cref="Like"/> are read once their rows are entered. A
    /// connective's rows are keyed by its operands' outcomes.
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
        StartsWith,
        EndsWith,
        Contains,
        Like,

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

        /// <summary>Which matcher an affix or a pattern is matched by.</summary>
        Match,

    }

}
