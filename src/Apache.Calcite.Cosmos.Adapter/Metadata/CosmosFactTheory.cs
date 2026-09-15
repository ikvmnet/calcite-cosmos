using System;
using System.Collections.Generic;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

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
        readonly Lazy<CosmosFactSet> _stated;
        readonly bool _conditional;
        readonly bool _representations;

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
            _conditional = _unconditional.Length != _rules.Length;

            foreach (var rule in _rules)
                if (rule.Head.Claim is CosmosClaim.Represents)
                    _representations = true;

            // What holds before a query proves anything is the same answer every time, and every
            // container has one now that the service's own guarantees are in here. Computed once.
            _stated = new Lazy<CosmosFactSet>(() => Close(null), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Gets whether every rule holds outright, so that nothing a query proves can add to them.
        /// </summary>
        /// <remarks>
        /// The question a caller asks before going to the trouble of reading facts off a predicate.
        /// A container that declares no schema is in exactly this state — what the service guarantees
        /// is conditional on nothing — so the common path does no work at all.
        /// </remarks>
        public bool IsUnconditional => _conditional == false;

        /// <summary>
        /// Gets whether anything here states a stored form, as distinct from a type or a value.
        /// </summary>
        /// <remarks>
        /// Asked by the rewrite, which has nothing to do without one: the service's guarantees say
        /// what a property <em>is</em> and never how it is written.
        /// </remarks>
        public bool HasRepresentations => _representations;

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
            if (established is null || established is ICollection<CosmosFact> { Count: 0 })
                return _stated.Value;

            return Close(established);
        }

        CosmosFactSet Close(IEnumerable<CosmosFact>? established)
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
