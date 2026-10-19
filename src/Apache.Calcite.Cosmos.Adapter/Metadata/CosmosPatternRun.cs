using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// One run of an anchored sequence a declared <c>pattern</c> admits: a class of characters, repeated a number of
    /// times between two bounds.
    /// </summary>
    /// <remarks>
    /// For a class, <c>A{m,n}A{p,q}</c> and <c>A{m+p,n+q}</c> accept the same strings, so a sequence holds no two
    /// neighbouring runs of one class (<see cref="CosmosPatternStrings"/>): <c>\d\d\d\d</c>, <c>[0-9]{4}</c> and
    /// <c>\d{2}[0-9]{2}</c> are one run.
    /// </remarks>
    /// <param name="Class">The characters each repetition matches.</param>
    /// <param name="Min">The fewest repetitions.</param>
    /// <param name="Max">The most, or <c>null</c> where there is no bound.</param>
    readonly record struct CosmosPatternRun(CosmosPatternClass Class, int Min, int? Max)
    {

        /// <summary>
        /// Gets whether the run is always the same width: a count, rather than a range of them.
        /// </summary>
        public bool IsFixed => Max == Min;

        /// <summary>
        /// Writes the run as a pattern would, for a diagnostic.
        /// </summary>
        public override string ToString() => Class + (Min, Max) switch
        {
            (1, 1) => string.Empty,
            (0, null) => "*",
            (1, null) => "+",
            (0, 1) => "?",
            _ when IsFixed => "{" + Min.ToString(CultureInfo.InvariantCulture) + "}",
            _ => "{" + Min.ToString(CultureInfo.InvariantCulture) + "," + (Max?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + "}",
        };

    }

}
