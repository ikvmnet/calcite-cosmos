using System;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Which halves of an instant something holds — a stored shape, or the SQL type a function
    /// answers.
    /// </summary>
    /// <remarks>
    /// <b>The two questions a parse has to answer are both about these.</b> A parse truncates where the
    /// type it answers holds less than the shape it read — <c>PARSE_DATE</c> over a path storing a full
    /// instant throws the clock away, and many stored strings then share one value. And a reader
    /// invents where the type holds more than the shape carries — a time of day read back as a
    /// <c>TIMESTAMP</c> acquires <em>today's</em> date, where the engine's parse gives it the epoch's.
    /// Neither is visible in the format string, and both are decided by comparing these two sets.
    /// </remarks>
    [Flags]
    public enum CosmosTemporalParts
    {

        /// <summary>
        /// Neither half, which is not a shape this knows.
        /// </summary>
        None = 0,

        /// <summary>
        /// A calendar date.
        /// </summary>
        Date = 1,

        /// <summary>
        /// A time of day.
        /// </summary>
        Time = 2,

        /// <summary>
        /// Both, which is what an instant is and what a <c>TIMESTAMP</c> holds.
        /// </summary>
        Instant = Date | Time,

    }

}
