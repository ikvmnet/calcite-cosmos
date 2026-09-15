using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What is claimed about a path.
    /// </summary>
    /// <remarks>
    /// One hierarchy for two jobs, deliberately. A claim a query can establish — a discriminator
    /// property holding a particular value — and a claim a rewrite can consume — a path holding a
    /// canonical lowercase UUID — are the same kind of thing, and keeping them the same kind is what
    /// lets a schema express facts that are conditional on other facts without a second mechanism. See
    /// <c>DESIGN.md</c> under <em>Atoms, clauses, and why asking is linear</em>.
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
        /// <param name="OrNull">
        /// Whether a JSON null is admitted beside that type. Nullability is an axis of its own rather
        /// than the absence of a type: a schema writes it as <c>["string", "null"]</c> under 2020-12
        /// and as <c>nullable: true</c> under OpenAPI 3.0, and both are the commonest shape there is.
        /// A claim that admits null is weaker than one that does not, and is the one most consumers
        /// want — a JSON null and an absent path are dropped by every comparison on both sides.
        /// </param>
        public sealed record OfType(CosmosJsonType Type, bool OrNull = false) : CosmosClaim;

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

}
