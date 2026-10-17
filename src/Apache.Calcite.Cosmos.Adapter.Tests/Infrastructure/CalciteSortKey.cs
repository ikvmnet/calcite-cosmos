namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// One key of a Calcite sort, as its comparator applies it: a field, a direction, and where the
    /// nulls go.
    /// </summary>
    /// <remarks>
    /// The null placement is resolved, never defaulted: SQL's unqualified <c>ASC</c> is
    /// <c>NULLS LAST</c> and its unqualified <c>DESC</c> is <c>NULLS FIRST</c> under Calcite's default
    /// collation (<c>HIGH</c>), and the caller that read the statement says which it was. Placement is
    /// absolute — the end of the output, whatever the direction — which is why it is a flag of its own
    /// rather than part of the direction.
    /// </remarks>
    /// <param name="Field">The ordinal of the key in the row.</param>
    /// <param name="Descending">Whether the non-null values run from greatest to least.</param>
    /// <param name="NullsFirst">Whether every value Calcite reads as null comes before every other.</param>
    public readonly record struct CalciteSortKey(int Field, bool Descending, bool NullsFirst)
    {

        /// <summary>
        /// The key as SQL writes it unqualified: nulls last ascending, first descending.
        /// </summary>
        /// <param name="field">The ordinal of the key in the row.</param>
        /// <param name="descending">Whether the key is descending.</param>
        public static CalciteSortKey Default(int field, bool descending = false) => new(field, descending, descending);

        public override string ToString() => $"${Field} {(Descending ? "DESC" : "ASC")} NULLS {(NullsFirst ? "FIRST" : "LAST")}";

    }

}
