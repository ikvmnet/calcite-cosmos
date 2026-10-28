using System;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// The reasoning the suite's fixtures plan in: one for the test run, as a host that owns one data source for its life
    /// keeps one schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the fixtures bind to one.</b> A table over unbound metadata owns a reasoning of its own, as a host's does,
    /// so a fixture built per test or per plan would define a graph and compile its declaration each time. Bound here
    /// once, a static fixture keeps one theory across tests, compiled once, as it did in the process's default graphs
    /// before those went. A test about what a schema owns, or that wants graphs apart, makes its own.
    /// </para>
    /// <para>
    /// <b>The same copy each time it is asked of the same metadata or theory.</b> Binding makes a copy, and a copy
    /// compiles again; the copies are remembered against what they were bound from, weakly, so that a fixture shared by
    /// several harnesses compiles once here, and one a test drops goes with it.
    /// </para>
    /// </remarks>
    internal static class TestReasoning
    {

        static readonly ConditionalWeakTable<CosmosContainerMetadata, CosmosContainerMetadata> Containers = new();
        static readonly ConditionalWeakTable<JsonFactTheory, JsonFactTheory> Theories = new();

        /// <summary>
        /// Gets the reasoning the fixtures share.
        /// </summary>
        public static CosmosReasoning Shared { get; } = new();

        /// <summary>
        /// Returns the metadata bound to <see cref="Shared"/>, or as it is where a schema has bound it already.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <returns>The bound metadata, the same copy each time for the same metadata.</returns>
        public static CosmosContainerMetadata Bind(CosmosContainerMetadata metadata) =>
            metadata.Reasoning is not null ? metadata : Containers.GetValue(metadata, m => m.WithReasoning(Shared));

        /// <summary>
        /// Returns the theory reasoning in <see cref="Shared"/>'s generations, or as it is where it is bound already.
        /// </summary>
        /// <param name="theory">The theory.</param>
        /// <returns>The bound theory, the same copy each time for the same theory.</returns>
        public static JsonFactTheory Bind(JsonFactTheory theory) =>
            theory.Generations is not null ? theory : Theories.GetValue(theory, t => t.In(Shared.Facts));

        /// <summary>
        /// Returns a table a harness built per plan, having checked that it reasons in <see cref="Shared"/> or in the
        /// reasoning its metadata was handed with: a fixture left unbound makes a reasoning of its own each plan and
        /// compiles its declaration again, which passes every assertion and only costs time.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="given">The metadata it was built over.</param>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The table made a reasoning of its own.</exception>
        public static CosmosTable Checked(CosmosTable table, CosmosContainerMetadata given) =>
            ReferenceEquals(table.Container.Reasoning, Shared) || (given.Reasoning is not null && ReferenceEquals(table.Container.Reasoning, given.Reasoning))
                ? table
                : throw new InvalidOperationException($"The fixture '{given.Name}' made a reasoning of its own: bind it with TestReasoning.Bind where it is declared.");

    }

}
