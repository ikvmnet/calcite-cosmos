using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// One entry of the <c>containers</c> operand: a container to expose, and whatever the model
    /// declared about the documents it holds.
    /// </summary>
    /// <remarks>
    /// The schema is compiled here rather than carried as text, because it is read once and asked many
    /// times — it comes from an operand and so cannot change while the process runs, which makes it a
    /// cache with no expiry rather than one with a policy. <see cref="CosmosFactTheory.Empty"/> is the
    /// ordinary case and the one every container has today.
    /// </remarks>
    /// <param name="Name">The container name.</param>
    /// <param name="Facts">What the model declared, compiled; empty where it declared nothing.</param>
    public readonly record struct CosmosContainerDeclaration(string Name, CosmosFactTheory Facts);

}
