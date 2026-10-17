namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A value bound to a Cosmos SQL query parameter.
    /// </summary>
    /// <param name="Name">The parameter name, including the leading <c>@</c>.</param>
    /// <param name="Value">The bound value.</param>
    public readonly record struct CosmosParameter(string Name, object? Value);

}
