using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using org.apache.calcite.plan;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// Calcite calling convention that identifies relational nodes whose results are produced by
    /// executing Cosmos SQL against an Azure Cosmos DB container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like the JDBC convention, <see cref="CosmosConvention"/> is not a singleton: each instance
    /// is bound to a particular container. When a query touches two different containers the
    /// planner uses two separate convention instances and inserts the necessary converter nodes
    /// between them.
    /// </para>
    /// <para>
    /// The convention carries the container's metadata because conversion rules need it. Whether a
    /// <c>Sort</c> may be pushed down at all depends on the container's composite indexes, which
    /// is unusual for a planner rule but unavoidable here — a multi-key sort without a matching
    /// index is rejected by the service rather than merely being slow.
    /// </para>
    /// </remarks>
    public class CosmosConvention : Convention.Impl
    {

        /// <summary>
        /// Relative cost multiplier applied to Cosmos relational nodes during planning. A value
        /// below 1.0 makes the planner prefer pushing operations into Cosmos over equivalent
        /// in-memory implementations.
        /// </summary>
        public const double CostMultiplier = .8d;

        /// <summary>
        /// Creates a new <see cref="CosmosConvention"/> for the given container.
        /// </summary>
        /// <param name="container">The container this convention is bound to.</param>
        /// <returns>A new <see cref="CosmosConvention"/> instance.</returns>
        public static CosmosConvention Create(CosmosContainerMetadata container)
        {
            return new CosmosConvention(container);
        }

        readonly CosmosContainerMetadata _container;

        /// <summary>
        /// Initializes a new instance of <see cref="CosmosConvention"/>. Prefer
        /// <see cref="Create"/> over calling this constructor directly.
        /// </summary>
        /// <param name="container">The container this convention is bound to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosConvention(CosmosContainerMetadata container) :
            base("COSMOS." + (container ?? throw new ArgumentNullException(nameof(container))).Name, typeof(CosmosRel))
        {
            _container = container;
        }

        /// <summary>
        /// Gets the container this convention is bound to.
        /// </summary>
        /// <remarks>
        /// Metadata only. What executes a statement belongs to the table rather than to the convention,
        /// and a plan reaches it through the schema when it runs — see <see cref="CosmosTable.Executor"/>.
        /// A convention is a planning-time trait, and a client is not a planning-time fact.
        /// </remarks>
        public CosmosContainerMetadata Container => _container;

        /// <summary>
        /// Determines whether the container a walk of an operator's input found is this convention's own: whether a
        /// rule registered for it may convert the operator.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A rule set is registered per container, and every rule in it sees every operator in the plan</b>, whoever's
        /// table it reads. A conversion decides under its own convention's declaration, so one that fired on another
        /// container's operator rewrote that operator by facts about other documents — a filter over <c>customers</c>
        /// made <c>FALSE</c> by what <c>orders</c> declares of <c>type</c> — and registered the result as equivalent to
        /// what it converted. It could never be implemented, its input being another convention's scan, but it was a
        /// member of the original's set all the same, and a rule reading that set read its condition as the filter's:
        /// the filter became an empty relation, or its input (review of the oracle in the planner, two containers in a
        /// <c>UNION</c>).
        /// </para>
        /// <para>
        /// The rules that are not conversions find the scan beneath them and ask the same of its convention
        /// (<c>CosmosFilterSplitRule.FindTable</c>). The lookup join and the write take no convention, and read the
        /// container from the operator.
        /// </para>
        /// </remarks>
        /// <param name="container">The container the walk found, or <c>null</c> where it found none.</param>
        /// <returns><c>true</c> where the operator reads this convention's container.</returns>
        internal bool Owns(CosmosContainerMetadata? container) => ReferenceEquals(container, _container);

        /// <inheritdoc />
        public override void register(RelOptPlanner planner)
        {
            foreach (var rule in CosmosRules.GetRules(this))
                planner.addRule(rule);
        }

    }

}
