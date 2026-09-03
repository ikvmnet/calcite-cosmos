using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Benchmarks.Model
{

    /// <summary>
    /// The containers every benchmark plans against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A benchmark of a planner is a benchmark of the metadata it consults, because that is what the
    /// rules ask questions of. A container declaring nothing takes a fast path through most of them —
    /// an undeclared full text path is refused outright, an unstated indexing policy answers "indexed"
    /// to every question without matching a pattern, and an unknown row count leaves the cost model
    /// with one plan it can distinguish from another. So none of these declare nothing.
    /// </para>
    /// <para>
    /// The shapes here are the ones the pushdown rules actually branch on: a single partition key, a
    /// hierarchical one, a key that is <c>id</c>, a nested key that promotes to no column at all, an
    /// indexing policy with exclusions, composite indexes a sort can be answered from, and declared
    /// full text and vector paths. Statistics are attached because the choice between pushing a
    /// filter down and applying it here is a cost comparison, and a cost model with no row counts
    /// makes it by tie-break rather than by arithmetic.
    /// </para>
    /// <para>
    /// The numbers are plausible rather than measured. Nothing here reaches a service; what they have
    /// to be is stable, so that a difference between two runs is a difference in the planner.
    /// </para>
    /// </remarks>
    public static class BenchmarkSchema
    {

        /// <summary>
        /// A catalogue container: one partition key, composite indexes, and declared full text and
        /// vector paths.
        /// </summary>
        /// <remarks>
        /// The container most of the single-table pushdown queries name. Its composite index over
        /// <c>/category</c> and <c>/_ts</c> is what makes a two-key sort answerable, and the declared
        /// full text paths are what make <c>FULLTEXTCONTAINS</c> pushable rather than refused.
        /// </remarks>
        public static readonly CosmosContainerMetadata Products = new(
            "products",
            new[] { "/category" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/category", false),
                    new CosmosCompositeIndexPath("/_ts", true),
                }),
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/id", false),
                    new CosmosCompositeIndexPath("/_ts", false),
                }),
            },
            includedPaths: new[] { "/*" },
            excludedPaths: new[] { "/payload/*", "/audit/?" },
            fullTextPaths: new[] { "/name", "/description", "/tags" },
            vectorPaths: new[] { "/embedding" },
            statistics: new CosmosContainerStatistics(2_400_000, 4_800_000_000, 24));

        /// <summary>
        /// A transactional container keyed by customer, an order of magnitude larger than
        /// <see cref="Products"/> and spread over four times the partitions.
        /// </summary>
        /// <remarks>
        /// The size difference is the point. A join between this and <see cref="Customers"/> has a
        /// cheap side and an expensive one, and which side the planner puts on the probe is a
        /// decision it can only make from these numbers.
        /// </remarks>
        public static readonly CosmosContainerMetadata Orders = new(
            "orders",
            new[] { "/customer" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/customer", false),
                    new CosmosCompositeIndexPath("/_ts", true),
                }),
            },
            includedPaths: new[] { "/*" },
            excludedPaths: new[] { "/payload/*" },
            statistics: new CosmosContainerStatistics(48_000_000, 96_000_000_000, 96));

        /// <summary>
        /// A container whose partition key is <c>id</c>, so that a single equality is a point read.
        /// </summary>
        /// <remarks>
        /// The shape that makes a lookup join worth choosing: every key the probe side supplies
        /// addresses one item in one partition, and the planner has to notice that before it will
        /// prefer fetching to reading the container.
        /// </remarks>
        public static readonly CosmosContainerMetadata Customers = new(
            "customers",
            new[] { "/id" },
            includedPaths: new[] { "/*" },
            statistics: new CosmosContainerStatistics(1_200_000, 1_800_000_000, 12));

        /// <summary>
        /// A container with a three-level hierarchical partition key.
        /// </summary>
        /// <remarks>
        /// Routing here is a prefix question rather than a yes-or-no one: pinning <c>tenant</c> alone
        /// narrows execution, pinning all three confines it to a single logical partition, and pinning
        /// the second without the first narrows nothing. Every predicate benchmark that cares about
        /// routing names this container.
        /// </remarks>
        public static readonly CosmosContainerMetadata Events = new(
            "events",
            new[] { "/tenant", "/user", "/session" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/tenant", false),
                    new CosmosCompositeIndexPath("/_ts", true),
                }),
            },
            includedPaths: new[] { "/*" },
            excludedPaths: new[] { "/body/*" },
            statistics: new CosmosContainerStatistics(310_000_000, 620_000_000_000, 192));

        /// <summary>
        /// A cold container with an indexing policy that excludes nearly everything.
        /// </summary>
        /// <remarks>
        /// The one where <see cref="CosmosContainerMetadata.IsPathIndexed"/> actually says no. A
        /// predicate over an excluded path still pushes — indexing bears on cost, not on legality —
        /// so this is here to make the planner cost the same statement two ways.
        /// </remarks>
        public static readonly CosmosContainerMetadata Archive = new(
            "archive",
            new[] { "/category" },
            includedPaths: new[] { "/id/?", "/category/?", "/_ts/?" },
            excludedPaths: new[] { "/*" },
            statistics: new CosmosContainerStatistics(900_000_000, 2_700_000_000_000, 384));

        /// <summary>
        /// A container whose partition key is nested, so it promotes to no column.
        /// </summary>
        /// <remarks>
        /// A partition key of <c>/shipment/region</c> cannot be named as a column, which means no key
        /// is derived for it and every predicate over it is written against the map. The rules take a
        /// different branch throughout, and a corpus without one of these never visits it.
        /// </remarks>
        public static readonly CosmosContainerMetadata Shipments = new(
            "shipments",
            new[] { "/shipment/region" },
            includedPaths: new[] { "/*" },
            statistics: new CosmosContainerStatistics(15_000_000, 22_500_000_000, 48));

        /// <summary>
        /// Every container, in the order they are registered.
        /// </summary>
        public static IReadOnlyList<CosmosContainerMetadata> All { get; } = new[]
        {
            Products,
            Orders,
            Customers,
            Events,
            Archive,
            Shipments,
        };

        /// <summary>
        /// Creates a fresh table per container.
        /// </summary>
        /// <remarks>
        /// Fresh, because a <see cref="CosmosTable"/> owns the <see cref="CosmosConvention"/> its
        /// rules are bound to and a convention is identified by reference. Two harnesses sharing one
        /// table would share a convention, which is the one piece of state that would let one
        /// benchmark's registrations reach another's planner.
        /// </remarks>
        /// <returns>The tables, keyed by container name.</returns>
        public static IReadOnlyDictionary<string, CosmosTable> CreateTables()
        {
            var tables = new Dictionary<string, CosmosTable>();

            foreach (var container in All)
                tables[container.Name] = new CosmosTable(container);

            return tables;
        }

    }

}
