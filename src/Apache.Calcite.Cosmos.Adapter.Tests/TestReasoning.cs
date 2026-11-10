using System;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Data;

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

        /// <summary>The harness planning in this flow, whose reasonings <see cref="Checked"/> holds a table to; none outside one.</summary>
        static readonly AsyncLocal<HarnessReasonings?> Harness = new();

        /// <summary>
        /// Gets the reasoning the fixtures share, made where first asked for and its S build awaited before anything plans
        /// in it: every filter a plan pushes asks S, and a first question that waited past its bound would decline and
        /// change the plan. A process that never asks, as the first-plan line's child does not, builds none.
        /// </summary>
        public static CosmosReasoning Shared => Made.Value;

        static readonly Lazy<CosmosReasoning> Made = new(Own);

        /// <summary>
        /// Makes a reasoning of a test's own, for a test about what a schema owns or that wants graphs apart, its S build
        /// awaited as <see cref="Shared"/>'s is: a question waits for the first build at most its bound and declines past it,
        /// so on a machine slow enough a plan made at once would send less than the test asserts, and fail for no row's
        /// reason. A test whose reasoning holds a probe, rows or an oracle of its own awaits it where it makes it.
        /// </summary>
        /// <returns>The reasoning, its first generation built.</returns>
        public static CosmosReasoning Own()
        {
            var reasoning = new CosmosReasoning();
            reasoning.Rendering.Ready.Wait();
            return reasoning;
        }

        /// <summary>
        /// The S build of the reasoning a connection's Cosmos schema plans in, started where it was not, read off the root
        /// the connection plans against: a schema a model's factory makes reasons in one of its own, made as the connection
        /// opened, so that a test reading what a plan pushes awaits it before the plan, as a fixture's is awaited.
        /// </summary>
        /// <param name="connection">The connection, open.</param>
        /// <param name="schema">The Cosmos schema's name in its root.</param>
        /// <param name="table">One of its containers, whose reasoning is every one of the schema's.</param>
        /// <returns>The build, done once a generation answers or the definition threw.</returns>
        public static Task Ready(DbConnection connection, string schema, string table)
        {
            var cosmos = (org.apache.calcite.schema.Schema)((CalciteConnection)connection).RootSchema.subSchemas().get(schema)!;
            return ((CosmosTable)cosmos.tables().get(table)!).Container.Reasoning!.Rendering.Ready;
        }

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
        /// compiles its declaration again, which passes every assertion and only costs time. Where a harness of its own
        /// plans (<see cref="HarnessReasonings.Planning"/>), it must reason in one that harness made.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="given">The metadata it was built over.</param>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The table made a reasoning of its own, or reasons outside its harness's.</exception>
        public static CosmosTable Checked(CosmosTable table, CosmosContainerMetadata given) => Harness.Value is { } harness
            ? harness.Made(table.Container.Reasoning)
                ? table
                : throw new InvalidOperationException($"The fixture '{given.Name}' reasons outside its harness's reasonings: bind it to the harness's reasoning where the harness builds it.")
            : (given.Reasoning is not null && ReferenceEquals(table.Container.Reasoning, given.Reasoning)) || ReferenceEquals(table.Container.Reasoning, Shared)
                ? table
                : throw new InvalidOperationException($"The fixture '{given.Name}' made a reasoning of its own: bind it with TestReasoning.Bind where it is declared.");

        /// <summary>Holds every table checked in this flow to the harness's reasonings until disposed, then puts back what it found.</summary>
        internal static IDisposable Within(HarnessReasonings harness)
        {
            var restore = new Restore(Harness.Value);
            Harness.Value = harness;
            return restore;
        }

        /// <summary>Puts the flow's harness back as <see cref="Within"/> found it.</summary>
        sealed class Restore(HarnessReasonings? was) : IDisposable
        {

            public void Dispose() => Harness.Value = was;

        }

    }

}
