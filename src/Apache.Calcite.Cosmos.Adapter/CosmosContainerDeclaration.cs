using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// One entry of the <c>containers</c> operand: a container to expose, and whatever the model
    /// declared about the documents it holds.
    /// </summary>
    /// <remarks>
    /// The schema is read here rather than carried as text, because it is read once and asked many
    /// times — it comes from an operand and so cannot change while the process runs, which makes it a
    /// cache with no expiry rather than one with a policy. Facts rather than a theory, because a schema
    /// is one source of them and the container is what assembles every source it has into one.
    /// </remarks>
    /// <param name="Name">The container name.</param>
    /// <param name="Facts">The facts the model's schema stated; empty where it stated none.</param>
    public readonly record struct CosmosContainerDeclaration(string Name, IReadOnlyList<CosmosFactRule> Facts);

}
