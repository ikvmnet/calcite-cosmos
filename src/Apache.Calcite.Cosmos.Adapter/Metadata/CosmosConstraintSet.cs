using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What is true of a container's documents taken together, assembled from every source that knows
    /// something and asked in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Several sources, one set</b>, for the reason <see cref="CosmosFactTheory"/> gives for facts: a
    /// consumer asks one question and should not have to know which source could answer it. What the
    /// service guarantees and what the container definition says are derived when the container is read —
    /// see <see cref="FromContainer"/> — and what a model declares is added to them.
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
        /// that is the container, and the constraint is <c>UNIQUE(id)</c>; with one it is
        /// <c>UNIQUE(pk…, id)</c> — every path of a hierarchical key, since two documents agreeing on a prefix
        /// may still be in different partitions. Where the partition key is <c>/id</c> the two are one set.
        /// </para>
        /// <para>
        /// <b>From the container definition:</b> each unique key policy entry is unique within a logical
        /// partition the same way, and is <c>UNIQUE(pk…, paths…)</c>.
        /// </para>
        /// <para>
        /// Neither has a filter: both hold of every document. A legacy container whose partition is a
        /// system key is reported, as far as is known, with that key's path, which its documents do not hold —
        /// so it is given <c>UNIQUE(_partitionKey, id)</c>, true and never matched. How the SDK reports one
        /// is not measured.
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

            if (CosmosConstraint.Unique.Of(Append(partitionKeyPaths, new[] { "/" + CosmosContainerMetadata.IdPropertyName }), null, CosmosConstraintSource.Service) is CosmosConstraint.Unique id)
                constraints.Add(id);

            foreach (var paths in uniqueKeys)
                if (paths.Count > 0 && CosmosConstraint.Unique.Of(Append(partitionKeyPaths, paths), null, CosmosConstraintSource.ContainerDefinition) is CosmosConstraint.Unique unique)
                    constraints.Add(unique);

            return constraints;
        }

        static List<string> Append(IReadOnlyList<string> first, IReadOnlyList<string> rest)
        {
            var list = new List<string>(first);
            list.AddRange(rest);
            return list;
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

        /// <summary>
        /// Determines whether a document of one set and a document of another, agreeing at every one of the
        /// given paths, are the same document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// True where some <c>UNIQUE</c> constraint's paths are all among them, and its filter is proved of
        /// <em>both</em> sets. A constraint holding among the documents of one kind says nothing about a pair
        /// of which one is of another kind, so proving the filter of one side is not enough.
        /// </para>
        /// <para>
        /// What each set is proved to satisfy is the caller's to establish — the facts the container states
        /// unconditionally, closed under whatever predicate selects the set.
        /// </para>
        /// </remarks>
        /// <param name="equated">The paths a predicate equates.</param>
        /// <param name="one">What every document of one set is proved to satisfy.</param>
        /// <param name="other">What every document of the other is.</param>
        /// <returns><c>true</c> if the paths identify at most one document across both sets.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        public bool IsUnique(IReadOnlyCollection<CosmosDocumentPath> equated, CosmosFactSet one, CosmosFactSet other)
        {
            if (equated is null)
                throw new ArgumentNullException(nameof(equated));
            if (one is null)
                throw new ArgumentNullException(nameof(one));
            if (other is null)
                throw new ArgumentNullException(nameof(other));

            if (equated.Count == 0)
                return false;

            var set = new HashSet<CosmosDocumentPath>(equated);

            foreach (var constraint in _constraints)
            {
                if (constraint is not CosmosConstraint.Unique unique || set.IsSupersetOf(unique.Paths) == false)
                    continue;

                var proved = true;
                foreach (var fact in unique.Filter)
                {
                    if (one.Knows(fact) == false || other.Knows(fact) == false)
                    {
                        proved = false;
                        break;
                    }
                }

                if (proved)
                    return true;
            }

            return false;
        }

    }

}
