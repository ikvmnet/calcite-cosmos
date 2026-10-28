using System;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What one schema object learns as it plans: the graphs its containers' declarations and filters are reasoned about
    /// in, the service's facts compiled into them, and the rendering graph its filters' Cosmos text is proven in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hung off the schema, never off the process.</b> The schema factory makes one per schema object (an account one
    /// for all its databases), a host's <see cref="CosmosSchema"/> or <see cref="CosmosAccountSchema"/> one for the
    /// containers it is handed unbound, and a <see cref="CosmosTable"/> over unbound metadata its own; each binds its
    /// containers to it (<see cref="CosmosContainerMetadata.WithReasoning"/>). Metadata already bound keeps the reasoning
    /// it carries, so a schema handed a table's metadata reasons for it in that table's schema, and keeps it alive.
    /// Connections share it as they share the schema, and it goes when the schema does. Only facts fixed in the assembly
    /// are the process's: S's <see cref="CosmosRendering.Definition"/> among them, which every schema's rendering graph is
    /// defined from.
    /// </para>
    /// <para>
    /// <b>Holds nothing disposable, and must not.</b> The provider disposes an <see cref="IDisposable"/> schema added with
    /// <c>AddSchema</c> each time a root holding it is released, and adds the same instance to the next root; the schemas
    /// are to become disposable, to release their clients (TODO.md, <i>A rebuilt root leaks its client</i>).
    /// </para>
    /// </remarks>
    internal sealed class CosmosReasoning
    {

        /// <summary>
        /// Initializes a new instance, with generations of the default limits and a rendering oracle over the registry's
        /// definition, its graph not yet built, which prints each generation's warm-up answer as it is built.
        /// </summary>
        public CosmosReasoning() :
            this(new JsonFactGenerations())
        {

        }

        /// <summary>
        /// Initializes a new instance over the given generations, and the given oracle and probe where there are: a test's,
        /// with limits or a plant of its own.
        /// </summary>
        /// <param name="facts">The generations the schema's declarations compile into.</param>
        /// <param name="rendering">The rendering oracle; <c>null</c> for one over the registry's definition.</param>
        /// <param name="probe">A test's probe of its filters' admission; <c>null</c> for none, as in production.</param>
        /// <exception cref="ArgumentNullException"><paramref name="facts"/> is <c>null</c>.</exception>
        internal CosmosReasoning(JsonFactGenerations facts, RenderingOracle? rendering = null, ICosmosRenderingProbe? probe = null)
        {
            Facts = facts ?? throw new ArgumentNullException(nameof(facts));
            ServiceFacts = new JsonFactTheory(CosmosServiceFacts.Rules, facts);
            Probe = probe;

            // Made, never started: its graph is defined on a thread of its own once something first asks it, and each
            // generation's warm-up answer printed there, into a box the build holds rather than the reasoning, so that a
            // schema let go while its build runs goes with it.
            Rendering = rendering ?? new RenderingOracle(new RenderingGraph(() => CosmosRendering.Definition, exercised: Printing(_warmed)));
        }

        readonly StrongBox<string?> _warmed = new();

        static Action<SqlPredicate, RenderingAnswer> Printing(StrongBox<string?> warmed) =>
            (question, answer) => warmed.Value = CosmosRendering.Print(question, answer);

        /// <summary>
        /// Gets the generations of graphs every container of the schema reasons in.
        /// </summary>
        public JsonFactGenerations Facts { get; }

        /// <summary>
        /// Gets what the service guarantees of every container, bound to <see cref="Facts"/>: the theory of each container
        /// that declares nothing more, so that they share one compiled context and one oracle.
        /// </summary>
        public JsonFactTheory ServiceFacts { get; }

        /// <summary>
        /// Gets the schema's rendering oracle: its S, generation by generation, the answers it remembers and the readings
        /// their keys are spelled from.
        /// </summary>
        public RenderingOracle Rendering { get; }

        /// <summary>A test's probe of the schema's filters' admission (<see cref="ICosmosRenderingProbe"/>); none in production.</summary>
        internal ICosmosRenderingProbe? Probe { get; }

        /// <summary>What the rendering graph's last generation printed its warm-up answer as; none before the first is built.</summary>
        internal string? Warmed => _warmed.Value;

    }

}
