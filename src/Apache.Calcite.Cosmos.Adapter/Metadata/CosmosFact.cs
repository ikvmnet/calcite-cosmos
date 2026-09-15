using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// An atom: one claim about one path.
    /// </summary>
    /// <param name="Path">The path the claim is about.</param>
    /// <param name="Claim">What is claimed.</param>
    public readonly record struct CosmosFact(CosmosDocumentPath Path, CosmosClaim Claim)
    {

        /// <summary>
        /// Determines whether this fact, being known, establishes <paramref name="other"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Subsumption lives here rather than in the theory's clauses, and that is a size decision
        /// rather than a taste one: <c>EqualTo v</c> entails <c>OneOf S</c> for every set containing
        /// <c>v</c>, so materialising the entailments would be unbounded where asking is a switch.
        /// See <c>DESIGN.md</c> under <em>Atoms, clauses, and why asking is linear</em>.
        /// </para>
        /// <para>
        /// Only claims about the same path relate. Nothing here crosses paths; that is what the
        /// theory's clauses are for.
        /// </para>
        /// </remarks>
        /// <param name="other">The fact to establish.</param>
        /// <returns><c>true</c> if knowing this fact means <paramref name="other"/> holds.</returns>
        public bool Entails(CosmosFact other)
        {
            if (Path.Equals(other.Path) == false)
                return false;

            if (Claim.Equals(other.Claim))
                return true;

            // Nothing here entails Present, and that is the whole of what a claim about a value means.
            // A schema's `properties` constrains the value a path holds *if it holds one*; only
            // `required` says it holds one at all. Reading "this is a canonical UUID" as "this is
            // there" would claim of every document what the schema claimed of none, which is exactly
            // the mistake a container holding more than one kind of document punishes.
            return (Claim, other.Claim) switch
            {
                // A known value settles membership, type and every disequality but its own.
                (CosmosClaim.EqualTo a, CosmosClaim.OneOf b) => CosmosClaim.OneOf.Contains(b.Values, a.Value),
                (CosmosClaim.EqualTo a, CosmosClaim.OfType b) => TypeOf(a.Value) == b.Type || b.OrNull && TypeOf(a.Value) == CosmosJsonType.Null,
                (CosmosClaim.EqualTo a, CosmosClaim.NotEqualTo b) => Equals(a.Value, b.Value) == false,

                // A domain settles a type where every member shares one, and refutes anything
                // outside it.
                (CosmosClaim.OneOf a, CosmosClaim.OfType b) => AllOfType(a.Values, b.Type, b.OrNull),
                (CosmosClaim.OneOf a, CosmosClaim.NotEqualTo b) => CosmosClaim.OneOf.Contains(a.Values, b.Value) == false,

                // A stored form is a string.
                // A stored form says what the strings at a path look like, and says nothing about
                // whether a null is there beside them -- so it entails only the claim that admits one.
                (CosmosClaim.Represents, CosmosClaim.OfType b) => b.Type == CosmosJsonType.String && b.OrNull,

                // Admitting a null is weaker than not admitting one.
                (CosmosClaim.OfType a, CosmosClaim.OfType b) => a.Type == b.Type && b.OrNull,

                _ => false,
            };
        }

        /// <summary>
        /// Determines whether every member of a domain is of one JSON type.
        /// </summary>
        /// <remarks>
        /// An empty domain answers <c>false</c>: it settles nothing, and a mixed one settles nothing
        /// either, which is the same answer for the same reason.
        /// </remarks>
        /// <param name="values">The domain.</param>
        /// <param name="type">The type to test for.</param>
        /// <param name="orNull">Whether a JSON null counts as a member.</param>
        /// <returns><c>true</c> where every member is of that type.</returns>
        static bool AllOfType(IReadOnlyList<object?> values, CosmosJsonType type, bool orNull)
        {
            if (values.Count == 0)
                return false;

            foreach (var value in values)
                if (TypeOf(value) != type && (orNull == false || TypeOf(value) != CosmosJsonType.Null))
                    return false;

            return true;
        }

        /// <summary>
        /// The JSON type a literal read out of a schema has.
        /// </summary>
        /// <remarks>
        /// <see cref="CosmosJsonType.Integer"/> is reported for a whole number, matching the service's
        /// <c>IS_INTEGER</c> and JSON Schema's own <c>integer</c>, which is a number with no fractional
        /// part rather than a distinct JSON type.
        /// </remarks>
        internal static CosmosJsonType TypeOf(object? value) => value switch
        {
            null => CosmosJsonType.Null,
            string => CosmosJsonType.String,
            bool => CosmosJsonType.Boolean,
            sbyte or byte or short or ushort or int or uint or long or ulong => CosmosJsonType.Integer,
            decimal d => decimal.Truncate(d) == d ? CosmosJsonType.Integer : CosmosJsonType.Number,
            double d => Math.Floor(d) == d && double.IsInfinity(d) == false ? CosmosJsonType.Integer : CosmosJsonType.Number,
            float f => Math.Floor(f) == f && float.IsInfinity(f) == false ? CosmosJsonType.Integer : CosmosJsonType.Number,
            _ => CosmosJsonType.Object,
        };

    }

}
