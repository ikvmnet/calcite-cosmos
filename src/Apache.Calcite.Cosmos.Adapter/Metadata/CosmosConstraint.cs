using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Something true of a container's documents taken together, rather than of each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is not a fact.</b> A <see cref="CosmosFact"/> is a claim about one document — a path holds
    /// a string, a discriminator holds a value — and a theory of them is asked one document at a time. A
    /// uniqueness constraint is a claim about every <em>pair</em> of documents, which no single document can
    /// satisfy or violate, and which therefore has no place in a theory of per-document atoms.
    /// </para>
    /// <para>
    /// <b>One kind so far.</b> A relation between two paths of one document — <c>data.id = linkId</c> —
    /// is the next, and is a different kind of trust: it can be checked one document at a time.
    /// </para>
    /// </remarks>
    public abstract record CosmosConstraint
    {

        /// <summary>
        /// Where the constraint was learned, which is also what stands behind it.
        /// </summary>
        public abstract CosmosConstraintSource Source { get; }

        /// <summary>
        /// A <c>UNIQUE</c> constraint: among the documents a filter admits, no two share the values at a set
        /// of paths.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Always across the whole container.</b> The service makes <c>id</c> unique within a logical
        /// partition and a unique key policy does the same for its paths, so each source states the
        /// constraint it implies across the container — with the partition key paths added — rather than
        /// leaving every consumer to remember which are partition-scoped. Uniqueness within a partition is not
        /// something a planner can use on its own, and stating it as though it were invites that mistake.
        /// </para>
        /// <para>
        /// <b>The filter scopes the claim, and a consumer has to prove it.</b> A conjunction of facts — a
        /// discriminator holding a value, or one of several — naming the documents among which the paths are
        /// unique. Empty means every document. A constraint holding among the documents of one kind says
        /// nothing about a pair of which one is of another kind, so a consumer comparing two sets of documents
        /// uses it only where <em>both</em> sets are proved to satisfy the filter.
        /// </para>
        /// <para>
        /// A document holding no value at one of the paths shares nothing with anything: a null equals
        /// nothing, so the constraint says nothing about it and it says nothing against the constraint. That
        /// is also why a document with no partition key value is no exception to <c>pk + id</c>.
        /// </para>
        /// </remarks>
        /// <param name="Paths">The paths, which must all be equated for the constraint to identify a document.</param>
        /// <param name="Filter">The facts a document must satisfy to be among those the paths are unique over; empty for all.</param>
        /// <param name="Source">Where the constraint was learned.</param>
        public sealed record Unique(IReadOnlyList<CosmosDocumentPath> Paths, IReadOnlyList<CosmosFact> Filter, CosmosConstraintSource Source) : CosmosConstraint
        {

            /// <inheritdoc />
            public override CosmosConstraintSource Source { get; } = Source;

            /// <summary>
            /// Builds a constraint from paths in policy form, or returns <c>null</c> where one of them names
            /// nothing a constraint can be made of.
            /// </summary>
            /// <param name="policyPaths">The paths.</param>
            /// <param name="filter">The facts scoping it, or <c>null</c> for every document.</param>
            /// <param name="source">Where the constraint was learned.</param>
            /// <returns>The constraint, or <c>null</c>.</returns>
            public static Unique? Of(IEnumerable<string> policyPaths, IEnumerable<CosmosFact>? filter, CosmosConstraintSource source)
            {
                if (policyPaths is null)
                    throw new ArgumentNullException(nameof(policyPaths));

                var paths = new List<CosmosDocumentPath>();

                foreach (var policyPath in policyPaths)
                {
                    if (PathOf(policyPath) is not CosmosDocumentPath path)
                        return null;

                    if (paths.Contains(path) == false)
                        paths.Add(path);
                }

                return paths.Count == 0 ? null : new Unique(paths, filter is null ? Array.Empty<CosmosFact>() : new List<CosmosFact>(filter), source);
            }

            /// <inheritdoc />
            public bool Equals(Unique? other) =>
                other is not null
                && Source == other.Source
                && Paths.Count == other.Paths.Count && new HashSet<CosmosDocumentPath>(Paths).SetEquals(other.Paths)
                && Filter.Count == other.Filter.Count && new HashSet<CosmosFact>(Filter).SetEquals(other.Filter);

            /// <inheritdoc />
            public override int GetHashCode()
            {
                var hash = Source.GetHashCode();

                foreach (var path in Paths)
                    hash ^= path.GetHashCode();

                foreach (var fact in Filter)
                    hash ^= fact.GetHashCode() * 31;

                return hash;
            }

            /// <inheritdoc />
            public override string ToString() =>
                $"UNIQUE ({string.Join(", ", Paths)})" + (Filter.Count == 0 ? "" : $" WHERE {string.Join(" AND ", Filter)}") + $" from {Source}";

        }

        /// <summary>
        /// Reads a path in policy form, such as <c>/data/guid</c>.
        /// </summary>
        /// <param name="policyPath">The path.</param>
        /// <returns>The path, or <c>null</c> where it names nothing below the root, or many values.</returns>
        public static CosmosDocumentPath? PathOf(string? policyPath)
        {
            if (string.IsNullOrWhiteSpace(policyPath))
                return null;

            var path = CosmosDocumentPath.Root;

            foreach (var name in policyPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // A wildcard or an array step names many values rather than one, so a constraint over it is
                // not one over a value a join can equate.
                if (name is "*" or "?" or "[]")
                    return null;

                path = path.Property(name.Length > 1 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name);
            }

            return path.IsRoot ? null : path;
        }

    }

    /// <summary>
    /// Where a constraint was learned.
    /// </summary>
    /// <remarks>
    /// The first two are enforced by the service on every write; the third is trusted the way a schema is,
    /// and is the only one that can be wrong.
    /// </remarks>
    public enum CosmosConstraintSource
    {

        /// <summary>
        /// The service's own guarantee — <c>id</c> unique within a logical partition — read against the
        /// container's partition key.
        /// </summary>
        Service,

        /// <summary>
        /// The container definition: its unique key policy, read against its partition key.
        /// </summary>
        ContainerDefinition,

        /// <summary>
        /// A model's declaration, which nothing enforces.
        /// </summary>
        Declared,

    }

}
