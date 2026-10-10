using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What is true of a container's documents taken together, assembled from every source that knows
    /// something and asked in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Several sources, one set</b>, for the reason <see cref="JsonFactTheory"/> gives for facts: a
    /// consumer asks one question and should not have to know which source could answer it. What the
    /// service guarantees and what the container definition says are derived when the container is read —
    /// see <see cref="FromContainer"/> — and what a model declares is added to them. Every one is written in
    /// the same DDL, so a derived constraint and a declared one read alike and are compiled alike.
    /// </para>
    /// <para>
    /// <b>The trust boundary is carried rather than flattened.</b> Each constraint says where it came from,
    /// because a declared one is the only kind that can be wrong, and a wrong one drops rows silently.
    /// </para>
    /// </remarks>
    public sealed class CosmosConstraintSet
    {

        /// <summary>
        /// The set that knows nothing.
        /// </summary>
        public static readonly CosmosConstraintSet Empty = new(Array.Empty<CosmosConstraint>());

        readonly CosmosConstraint[] _constraints;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="constraints">The constraints.</param>
        /// <exception cref="ArgumentNullException"><paramref name="constraints"/> is <c>null</c>.</exception>
        public CosmosConstraintSet(IEnumerable<CosmosConstraint> constraints)
        {
            if (constraints is null)
                throw new ArgumentNullException(nameof(constraints));

            var list = new List<CosmosConstraint>();
            foreach (var constraint in constraints)
                if (constraint is not null && list.Contains(constraint) == false)
                    list.Add(constraint);

            _constraints = list.ToArray();
        }

        /// <summary>
        /// Gets the constraints.
        /// </summary>
        public IReadOnlyList<CosmosConstraint> Constraints => _constraints;

        /// <summary>
        /// Derives the <c>UNIQUE</c> constraints the service and the container definition make true.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>From the service:</b> <c>id</c> is unique within a logical partition. With no partition key
        /// that is the container, and the constraint is <c>UNIQUE (id)</c>; with one it is
        /// <c>UNIQUE (pk…, id)</c> — every path of a hierarchical key, since two documents agreeing on a
        /// prefix may still be in different partitions. Where the partition key is <c>/id</c> the two are one.
        /// </para>
        /// <para>
        /// <b>From the container definition:</b> each unique key policy entry is unique within a logical
        /// partition the same way, and is <c>UNIQUE (pk…, paths…)</c>.
        /// </para>
        /// <para>
        /// Each is written with accessors, which in a key stand for the stored value — what the service
        /// enforces. None has a predicate. A legacy container whose partition is a system key is reported, as
        /// far as is known, with that key's path, which its documents do not hold — so it gets
        /// <c>UNIQUE (_partitionKey, id)</c>, true and never matched. How the SDK reports one is not measured.
        /// </para>
        /// </remarks>
        /// <param name="partitionKeyPaths">The partition key paths, in policy form.</param>
        /// <param name="uniqueKeys">The unique key policy's path sets, in policy form.</param>
        /// <returns>The constraints.</returns>
        public static IReadOnlyList<CosmosConstraint> FromContainer(IReadOnlyList<string> partitionKeyPaths, IEnumerable<IReadOnlyList<string>> uniqueKeys)
        {
            if (partitionKeyPaths is null)
                throw new ArgumentNullException(nameof(partitionKeyPaths));
            if (uniqueKeys is null)
                throw new ArgumentNullException(nameof(uniqueKeys));

            var constraints = new List<CosmosConstraint>();

            if (Of(partitionKeyPaths, new[] { "/" + CosmosContainerMetadata.IdPropertyName }, CosmosConstraintSource.Service) is CosmosConstraint id)
                constraints.Add(id);

            foreach (var paths in uniqueKeys)
                if (paths.Count > 0 && Of(partitionKeyPaths, paths, CosmosConstraintSource.ContainerDefinition) is CosmosConstraint unique)
                    constraints.Add(unique);

            return constraints;
        }

        /// <summary>
        /// Writes <c>UNIQUE</c> over the accessors of the partition key paths and the given paths, or returns
        /// <c>null</c> where one of them has no accessor.
        /// </summary>
        static CosmosConstraint? Of(IReadOnlyList<string> partitionKeyPaths, IReadOnlyList<string> paths, CosmosConstraintSource source)
        {
            var keys = new List<string>();

            foreach (var path in Concat(partitionKeyPaths, paths))
            {
                if (CosmosConstraint.AccessorOf(path) is not string accessor)
                    return null;

                if (keys.Contains(accessor) == false)
                    keys.Add(accessor);
            }

            return keys.Count == 0 ? null : new CosmosConstraint.Unique(string.Join(", ", keys), null, source);
        }

        static IEnumerable<string> Concat(IReadOnlyList<string> first, IReadOnlyList<string> second)
        {
            foreach (var item in first)
                yield return item;
            foreach (var item in second)
                yield return item;
        }

        /// <summary>
        /// Returns a set knowing these constraints as well.
        /// </summary>
        /// <param name="constraints">The constraints to add.</param>
        /// <returns>The set, or this one where nothing was new.</returns>
        public CosmosConstraintSet With(IEnumerable<CosmosConstraint> constraints)
        {
            if (constraints is null)
                throw new ArgumentNullException(nameof(constraints));

            var combined = new List<CosmosConstraint>(_constraints);
            combined.AddRange(constraints);

            var set = new CosmosConstraintSet(combined);
            return set._constraints.Length == _constraints.Length ? this : set;
        }

    }

}
