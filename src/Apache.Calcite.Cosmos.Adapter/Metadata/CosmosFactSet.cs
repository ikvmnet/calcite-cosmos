using System;
using System.Collections.Generic;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What is known about a container's documents, for one query.
    /// </summary>
    /// <remarks>
    /// The closure of what the query established under the container's rules. Asking it a question is
    /// a dictionary hit plus a short scan, because a path carries few claims.
    /// </remarks>
    public sealed class CosmosFactSet
    {

        /// <summary>
        /// What is known when nothing is declared and nothing was established.
        /// </summary>
        public static readonly CosmosFactSet Empty = new(new Dictionary<CosmosDocumentPath, List<CosmosFact>>());

        readonly Dictionary<CosmosDocumentPath, List<CosmosFact>> _byPath;

        internal CosmosFactSet(Dictionary<CosmosDocumentPath, List<CosmosFact>> byPath)
        {
            _byPath = byPath;
        }

        /// <summary>
        /// Determines whether a fact holds.
        /// </summary>
        /// <remarks>
        /// Through subsumption, so a known equality answers yes to the definedness of the same path,
        /// and to every disequality but its own, without either having been derived.
        /// <see cref="CosmosFact.Entails"/> carries the table.
        /// </remarks>
        /// <param name="fact">The fact to test.</param>
        /// <returns><c>true</c> if it holds.</returns>
        public bool Knows(CosmosFact fact)
        {
            if (_byPath.TryGetValue(fact.Path, out var known) == false)
                return false;

            foreach (var candidate in known)
                if (candidate.Entails(fact))
                    return true;

            return false;
        }

        /// <summary>
        /// Returns the stored form known for a path, or <c>null</c> where none is.
        /// </summary>
        /// <remarks>
        /// The question every rewrite asks first. Where a path somehow carries two representations the
        /// strongest is returned — one that preserves order beats one that only preserves equality —
        /// since both were proven and the caller wants the most it can use.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns>The representation, or <c>null</c>.</returns>
        public CosmosRepresentation? RepresentationOf(CosmosDocumentPath path)
        {
            if (path is null || _byPath.TryGetValue(path, out var known) == false)
                return null;

            CosmosRepresentation? best = null;

            foreach (var fact in known)
                if (fact.Claim is CosmosClaim.Represents represents)
                    if (best is not CosmosRepresentation current || Rank(represents.Representation) > Rank(current))
                        best = represents.Representation;

            return best;
        }

        /// <summary>
        /// Orders two stored forms by how much each one licenses.
        /// </summary>
        /// <param name="representation">The form.</param>
        /// <returns>A rank, higher being the one a caller can do more with.</returns>
        static int Rank(CosmosRepresentation representation) =>
            (representation.PreservesOrder ? 2 : 0) + (representation.PreservesEquality ? 1 : 0);

        /// <summary>
        /// Returns everything known about a path, for diagnostics.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>The claims, which may be empty.</returns>
        public IReadOnlyList<CosmosClaim> ClaimsFor(CosmosDocumentPath path)
        {
            if (path is null || _byPath.TryGetValue(path, out var known) == false)
                return Array.Empty<CosmosClaim>();

            var claims = new CosmosClaim[known.Count];
            for (var i = 0; i < known.Count; i++)
                claims[i] = known[i].Claim;

            return claims;
        }

    }

}
