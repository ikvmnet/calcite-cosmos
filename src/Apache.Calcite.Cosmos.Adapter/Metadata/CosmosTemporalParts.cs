using System;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Which halves of an instant something holds — a stored shape, or the SQL type a function
    /// answers — and, for a type, whether it holds a place in a zone beside them.
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

        /// <summary>
        /// A place in a time zone, which a <c>TIMESTAMP WITH LOCAL TIME ZONE</c>, a <c>TIMESTAMP WITH TIME ZONE</c> and a
        /// <c>TIME WITH LOCAL TIME ZONE</c> hold beside their halves, and a zone-less type does not.
        /// </summary>
        /// <remarks>
        /// Kept apart because Calcite's plain cast does not read a zone-less string into such a type as it reads it into
        /// the zone-less one: measured, it raises over every string of the calendar date and of the two times of day
        /// (<c>Calcite.TemporalCastReadsFewShapes</c>), where the cast to <c>DATE</c> and <c>TIMESTAMP</c> reads the date.
        /// So <see cref="CosmosTemporalForms.EngineReads"/>, asked by what a cast's type holds, lists no target holding
        /// this, and a cast to a zoned type reads no form.
        /// </remarks>
        Zone = 4,

    }

}
