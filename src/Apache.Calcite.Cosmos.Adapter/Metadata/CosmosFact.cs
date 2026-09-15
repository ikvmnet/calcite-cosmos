using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The JSON types a claim can name, which are the seven the service's own type predicates
    /// distinguish.
    /// </summary>
    public enum CosmosJsonType
    {

        /// <summary>A JSON string.</summary>
        String,

        /// <summary>A JSON number that is not an integer.</summary>
        Number,

        /// <summary>A JSON number with no fractional part.</summary>
        Integer,

        /// <summary>A JSON boolean.</summary>
        Boolean,

        /// <summary>A JSON object.</summary>
        Object,

        /// <summary>A JSON array.</summary>
        Array,

        /// <summary>A JSON null, which is not the same as an absent path.</summary>
        Null,

    }

    /// <summary>
    /// A stored textual form, and — the part that matters — which relations comparing that form
    /// preserves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious model is that a path "is a UUID" or "is a date-time", and it is wrong. Calcite
    /// compares UUIDs as two <em>signed</em> 64-bit halves, so for half of all v4 values the lexical
    /// order of the canonical string is not the order Calcite sorts in — while equality agrees for all
    /// of them. A date-time at one fixed ISO-8601 UTC shape preserves both. So the two properties are
    /// independent and a representation has to carry them separately, or a sort gets pushed that
    /// returns the wrong rows. Measured; see <c>DESIGN-93.md</c> §1.
    /// </para>
    /// <para>
    /// Both are claims about the <em>stored</em> string against the <em>logical</em> value Calcite
    /// compares. Neither is a claim about the service, which compares strings lexically whatever is in
    /// them.
    /// </para>
    /// </remarks>
    /// <param name="Name">A stable name for the form, for diagnostics and for equality.</param>
    /// <param name="PreservesEquality">
    /// Whether one logical value has exactly one stored spelling, so that comparing the stored strings
    /// for equality answers what comparing the values answers. Licenses <c>=</c>, <c>&lt;&gt;</c>,
    /// <c>IN</c>, <c>DISTINCT</c>, <c>GROUP BY</c>, a join key, partition routing and the point read.
    /// </param>
    /// <param name="PreservesOrder">
    /// Whether the lexical order of the stored strings is the order Calcite compares the values in.
    /// Licenses the range comparisons, <c>ORDER BY</c>, <c>MIN</c>, <c>MAX</c>, and a <c>LIMIT</c>
    /// pushed beneath a sort. Implies <see cref="PreservesEquality"/> is worth nothing on its own —
    /// the two are set independently.
    /// </param>
    public readonly record struct CosmosRepresentation(string Name, bool PreservesEquality, bool PreservesOrder);

    /// <summary>
    /// What is claimed about a path.
    /// </summary>
    /// <remarks>
    /// One hierarchy for two jobs, deliberately. A claim a query can establish — <c>$.type</c> equals
    /// <c>'ParkMap'</c> — and a claim a rewrite can consume — <c>$.data.parkId</c> is a canonical
    /// lowercase UUID — are the same kind of thing, and keeping them the same kind is what lets a
    /// schema express facts that are conditional on other facts without a second mechanism. See
    /// <c>DESIGN-93.md</c> §2.
    /// </remarks>
    public abstract record CosmosClaim
    {

        CosmosClaim()
        {
        }

        /// <summary>
        /// The value at the path is of the named JSON type.
        /// </summary>
        /// <param name="Type">The type.</param>
        public sealed record OfType(CosmosJsonType Type) : CosmosClaim;

        /// <summary>
        /// The path is present in the document.
        /// </summary>
        /// <remarks>
        /// Present, not non-null: a JSON null is a value the path has, and <c>IS_DEFINED</c> is true
        /// of it. This is what <c>required</c> declares.
        /// </remarks>
        public sealed record Present : CosmosClaim;

        /// <summary>
        /// The value at the path is exactly this literal.
        /// </summary>
        /// <param name="Value">The literal, as a CLR value — a string, a boxed number, a boolean, or <c>null</c> for a JSON null.</param>
        public sealed record EqualTo(object? Value) : CosmosClaim;

        /// <summary>
        /// The value at the path is not this literal.
        /// </summary>
        /// <remarks>
        /// Carried as its own claim rather than as a negation of <see cref="EqualTo"/> because an
        /// <c>else</c> branch's guard is exactly this, and because a query establishes it directly
        /// from a <c>&lt;&gt;</c>.
        /// </remarks>
        /// <param name="Value">The excluded literal.</param>
        public sealed record NotEqualTo(object? Value) : CosmosClaim;

        /// <summary>
        /// The value at the path is one of a finite set.
        /// </summary>
        /// <param name="Values">The domain. Order is not significant; membership is.</param>
        public sealed record OneOf(IReadOnlyList<object?> Values) : CosmosClaim
        {

            /// <inheritdoc />
            public bool Equals(OneOf? other) => other is not null && SetEquals(Values, other.Values);

            /// <inheritdoc />
            public override int GetHashCode()
            {
                // Order-independent, because the domain is a set and two spellings of one domain are
                // one claim.
                var hash = 0;
                foreach (var value in Values)
                    hash ^= value?.GetHashCode() ?? 0;

                return hash;
            }

            static bool SetEquals(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
            {
                if (left.Count != right.Count)
                    return false;

                foreach (var value in left)
                    if (Contains(right, value) == false)
                        return false;

                return true;
            }

            internal static bool Contains(IReadOnlyList<object?> values, object? value)
            {
                foreach (var candidate in values)
                    if (Equals(candidate, value))
                        return true;

                return false;
            }

        }

        /// <summary>
        /// The value at the path is a string in the named stored form.
        /// </summary>
        /// <param name="Representation">The form, and which relations it preserves.</param>
        public sealed record Represents(CosmosRepresentation Representation) : CosmosClaim;

    }

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
        /// See <c>DESIGN-93.md</c> §2.
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

            return (Claim, other.Claim) switch
            {
                // A known value settles membership, type, presence and every disequality but its own.
                (CosmosClaim.EqualTo a, CosmosClaim.OneOf b) => CosmosClaim.OneOf.Contains(b.Values, a.Value),
                (CosmosClaim.EqualTo a, CosmosClaim.OfType b) => TypeOf(a.Value) == b.Type,
                (CosmosClaim.EqualTo, CosmosClaim.Present) => true,
                (CosmosClaim.EqualTo a, CosmosClaim.NotEqualTo b) => Equals(a.Value, b.Value) == false,

                // A domain settles presence, settles a type where every member shares one, and
                // refutes anything outside it.
                (CosmosClaim.OneOf, CosmosClaim.Present) => true,
                (CosmosClaim.OneOf a, CosmosClaim.OfType b) => AllOfType(a.Values, b.Type),
                (CosmosClaim.OneOf a, CosmosClaim.NotEqualTo b) => CosmosClaim.OneOf.Contains(a.Values, b.Value) == false,

                // A stored form is a string, and a string is there.
                (CosmosClaim.Represents, CosmosClaim.OfType b) => b.Type == CosmosJsonType.String,
                (CosmosClaim.Represents, CosmosClaim.Present) => true,

                _ => false,
            };
        }

        static bool AllOfType(IReadOnlyList<object?> values, CosmosJsonType type)
        {
            if (values.Count == 0)
                return false;

            foreach (var value in values)
                if (TypeOf(value) != type)
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
