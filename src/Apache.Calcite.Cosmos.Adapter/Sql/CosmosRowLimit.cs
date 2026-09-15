using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// How many rows a statement skips or takes: a count the planner knew, or a parameter whose value
    /// arrives with the execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a limit is not simply an <c>int</c>.</b> A host that prepares a statement once and runs
    /// it many times parameterises the page size — Entity Framework does it for every <c>Take</c> and
    /// <c>First</c>, a literal one included, so that one plan serves every page. The count is
    /// therefore a value the plan does not have, and asking for one is what made a parameterised
    /// limit throw rather than run.
    /// </para>
    /// <para>
    /// <b>The plan needs the type and not the count.</b> Knowing the parameter is an integer is enough
    /// to decide the limit is pushable and to write its name where the digits would have gone; the
    /// service binds it like any other parameter. Measured against a container: <c>OFFSET @skip LIMIT
    /// @take</c> and <c>SELECT TOP @take</c> both run, so nothing about the statement text has to
    /// change for a limit that is not a constant.
    /// </para>
    /// <para>
    /// An <c>int</c> converts implicitly, so a caller that knows the count writes one.
    /// </para>
    /// </remarks>
    public readonly record struct CosmosRowLimit
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="count">The count, where the planner knew it.</param>
        /// <param name="parameter">The parameter name, where it did not.</param>
        CosmosRowLimit(int? count, string? parameter)
        {
            Count = count;
            Parameter = parameter;
        }

        /// <summary>
        /// Gets the count, or <c>null</c> where a parameter carries it.
        /// </summary>
        /// <remarks>
        /// The question everything that needs the number asks, and the <c>null</c> is the honest
        /// answer rather than a missing case: a page size derived from a limit the plan does not have
        /// is not derivable either. See <c>CosmosImplementor.MaxItemCount</c>, which returns none.
        /// </remarks>
        public int? Count { get; }

        /// <summary>
        /// Gets the parameter name, including the leading <c>@</c>, or <c>null</c> where a count
        /// carries it.
        /// </summary>
        public string? Parameter { get; }

        /// <summary>
        /// Creates a limit carried by a bound parameter.
        /// </summary>
        /// <param name="parameter">The parameter name, including the leading <c>@</c>.</param>
        /// <returns>The limit.</returns>
        public static CosmosRowLimit Bound(string parameter) => new(null, parameter);

        /// <summary>
        /// Converts a known count.
        /// </summary>
        /// <param name="count">The count.</param>
        public static implicit operator CosmosRowLimit(int count) => new(count, null);

        /// <summary>
        /// Renders the limit as the statement writes it.
        /// </summary>
        /// <returns>The digits, or the parameter name.</returns>
        public override string ToString() => Parameter ?? (Count ?? 0).ToString(CultureInfo.InvariantCulture);

    }

}
