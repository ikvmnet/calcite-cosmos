using System;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What one schema object learns as it plans: the graphs its containers' declarations and filters are reasoned about
    /// in, and the service's facts compiled into them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hung off the schema, never off the process.</b> The schema factory makes one per schema object (an account one
    /// for all its databases), a host's <see cref="CosmosSchema"/> or <see cref="CosmosAccountSchema"/> one for the
    /// containers it is handed unbound, and a <see cref="CosmosTable"/> over unbound metadata its own; each binds its
    /// containers to it (<see cref="CosmosContainerMetadata.WithReasoning"/>). Connections share it as they share the
    /// schema, and it goes when the schema does. Only facts fixed in the assembly are the process's.
    /// </para>
    /// <para>
    /// <b>Holds nothing disposable, and must not.</b> The provider disposes a schema added with <c>AddSchema</c> each time a
    /// root holding it is released, and adds the same instance to the next root.
    /// </para>
    /// </remarks>
    internal sealed class CosmosReasoning
    {

        /// <summary>
        /// Initializes a new instance, with generations of the default limits.
        /// </summary>
        public CosmosReasoning() :
            this(new JsonFactGenerations())
        {

        }

        /// <summary>
        /// Initializes a new instance over the given generations: a test's, with limits of its own.
        /// </summary>
        /// <param name="facts">The generations the schema's declarations compile into.</param>
        /// <exception cref="ArgumentNullException"><paramref name="facts"/> is <c>null</c>.</exception>
        internal CosmosReasoning(JsonFactGenerations facts)
        {
            Facts = facts ?? throw new ArgumentNullException(nameof(facts));
            ServiceFacts = new JsonFactTheory(CosmosServiceFacts.Rules, facts);
        }

        /// <summary>
        /// Gets the generations of graphs every container of the schema reasons in.
        /// </summary>
        public JsonFactGenerations Facts { get; }

        /// <summary>
        /// Gets what the service guarantees of every container, bound to <see cref="Facts"/>: the theory of each container
        /// that declares nothing more, so that they share one compiled context and one oracle.
        /// </summary>
        public JsonFactTheory ServiceFacts { get; }

    }

}
