using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// One rule of the compiled schema: a conjunction of facts that must hold, and the one fact that
    /// follows when they do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A conjunctive body and a single-atom head is what makes the compiled schema a definite Horn
    /// theory, and that is not a stylistic choice — it is what keeps asking it a linear-time question
    /// rather than a satisfiability one. The moment a rule could conclude "A or B" the theory leaves
    /// Horn and entailment becomes intractable, so an undiscriminated <c>anyOf</c> contributes only
    /// the facts every branch agrees on rather than a disjunction. See <c>DESIGN-93.md</c> §2.
    /// </para>
    /// <para>
    /// An empty body is the ordinary case: a fact the schema states unconditionally.
    /// </para>
    /// </remarks>
    /// <param name="Body">The facts that must hold, all of them.</param>
    /// <param name="Head">The fact that follows.</param>
    public sealed record CosmosFactRule(IReadOnlyList<CosmosFact> Body, CosmosFact Head)
    {

        /// <summary>
        /// Creates a rule with nothing to prove.
        /// </summary>
        /// <param name="head">The fact the schema states outright.</param>
        /// <returns>The rule.</returns>
        public static CosmosFactRule Unconditional(CosmosFact head) => new(Array.Empty<CosmosFact>(), head);

    }

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
        /// Through subsumption, so a known <c>$.type = 'ParkMap'</c> answers yes to
        /// <c>$.type IS DEFINED</c> and to <c>$.type &lt;&gt; 'Park'</c> without either having been
        /// derived. <see cref="CosmosFact.Entails"/> carries the table.
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

    /// <summary>
    /// A container's declared schema, compiled to rules the planner can ask questions of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compiled once, when the schema is registered — unlike the row count beside it this is derived
    /// from an operand and never changes, so it is a cache with no expiry. Asking it is
    /// <see cref="Derive"/>, which forward-chains from what a query established to everything that
    /// follows.
    /// </para>
    /// <para>
    /// The cost is linear in the size of the theory: each rule fires at most once, and a rule is
    /// reconsidered only when a fact about a path its body mentions is added. Dowling and Gallier
    /// (1984) is the result; the counter-per-clause form there is adapted here to re-scan the body
    /// instead, because a body atom is satisfied by any known fact that <em>entails</em> it rather
    /// than only by its literal self, and bodies are one or two atoms long.
    /// </para>
    /// </remarks>
    public sealed class CosmosFactTheory
    {

        /// <summary>
        /// The theory of a container that declares nothing, which proves nothing and costs nothing.
        /// </summary>
        public static readonly CosmosFactTheory Empty = new(Array.Empty<CosmosFactRule>());

        readonly CosmosFactRule[] _rules;
        readonly Dictionary<CosmosDocumentPath, List<int>> _mentioning;
        readonly int[] _unconditional;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rules">The compiled rules.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <c>null</c>.</exception>
        public CosmosFactTheory(IEnumerable<CosmosFactRule> rules)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));

            _rules = new List<CosmosFactRule>(rules).ToArray();
            _mentioning = new Dictionary<CosmosDocumentPath, List<int>>();

            var unconditional = new List<int>();

            for (var i = 0; i < _rules.Length; i++)
            {
                if (_rules[i].Body.Count == 0)
                {
                    unconditional.Add(i);
                    continue;
                }

                foreach (var atom in _rules[i].Body)
                {
                    if (_mentioning.TryGetValue(atom.Path, out var list) == false)
                        _mentioning[atom.Path] = list = new List<int>();

                    if (list.Contains(i) == false)
                        list.Add(i);
                }
            }

            _unconditional = unconditional.ToArray();
        }

        /// <summary>
        /// Gets the compiled rules.
        /// </summary>
        public IReadOnlyList<CosmosFactRule> Rules => _rules;

        /// <summary>
        /// Gets whether the container declares nothing.
        /// </summary>
        public bool IsEmpty => _rules.Length == 0;

        /// <summary>
        /// Closes a query's established facts under the container's rules.
        /// </summary>
        /// <remarks>
        /// The facts a query establishes are read off its predicate — an equality pins a value, an
        /// <c>IN</c> a domain, an <c>IS NOT NULL</c> a presence. What comes back is those plus
        /// everything the schema says follows from them, which is what a rewrite consults.
        /// </remarks>
        /// <param name="established">What the query proved, which may be empty.</param>
        /// <returns>The closure.</returns>
        public CosmosFactSet Derive(IEnumerable<CosmosFact>? established)
        {
            if (_rules.Length == 0 && established is null)
                return CosmosFactSet.Empty;

            var byPath = new Dictionary<CosmosDocumentPath, List<CosmosFact>>();
            var pending = new Queue<CosmosDocumentPath>();
            var fired = new bool[_rules.Length];

            void Add(CosmosFact fact)
            {
                if (byPath.TryGetValue(fact.Path, out var known) == false)
                    byPath[fact.Path] = known = new List<CosmosFact>();

                foreach (var candidate in known)
                    if (candidate.Entails(fact))
                        return;

                known.Add(fact);
                pending.Enqueue(fact.Path);
            }

            if (established is not null)
                foreach (var fact in established)
                    Add(fact);

            // What the schema states outright holds before anything is established, and may itself
            // satisfy another rule's body.
            foreach (var i in _unconditional)
            {
                fired[i] = true;
                Add(_rules[i].Head);
            }

            var set = new CosmosFactSet(byPath);

            while (pending.Count > 0)
            {
                var path = pending.Dequeue();

                if (_mentioning.TryGetValue(path, out var candidates) == false)
                    continue;

                foreach (var i in candidates)
                {
                    if (fired[i])
                        continue;

                    var rule = _rules[i];
                    var satisfied = true;

                    foreach (var atom in rule.Body)
                    {
                        if (set.Knows(atom) == false)
                        {
                            satisfied = false;
                            break;
                        }
                    }

                    if (satisfied == false)
                        continue;

                    fired[i] = true;
                    Add(rule.Head);
                }
            }

            return set;
        }

    }

}
