namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What the loader reads of a predicate it leaves opaque, as a value: its top call, with each operand the loader reads
    /// as a value and none it does not; or the value the predicate is, a reading of a boolean, say; or none.
    /// </summary>
    /// <remarks>
    /// Kept beside the <see cref="SqlPredicate.Opaque"/> term, never in it, so the facts graph's terms and memo keys do not
    /// move. What it is strict in is the rendering graph's rules' — a <c>strictop</c> row of the call's name, a
    /// <c>strictreader</c> row of a reading, through <c>callarg</c> — and the loader decides nothing.
    /// </remarks>
    internal sealed record OpaqueReading(long Id, SqlValue? Top);

}
