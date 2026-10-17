using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What is claimed about a path.
    /// </summary>
    /// <remarks>
    /// One hierarchy for two jobs, deliberately. A claim a query can establish — a discriminator
    /// property holding a particular value — and a claim a rewrite can consume — a path holding a
    /// canonical lowercase UUID — are the same kind of thing, and keeping them the same kind is what
    /// lets a schema express facts that are conditional on other facts without a second mechanism. See
    /// <c>DESIGN.md</c> under <em>The theory of JSON, in layers</em>.
    /// </remarks>
    public abstract record JsonClaim
    {

        JsonClaim()
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
        public sealed record OfType(JsonType Type, bool OrNull = false) : JsonClaim;

        /// <summary>
        /// The path is present in the document.
        /// </summary>
        /// <remarks>
        /// Present, not non-null: a JSON null is a value the path has, and <c>IS_DEFINED</c> is true
        /// of it. This is what <c>required</c> declares.
        /// </remarks>
        public sealed record Present : JsonClaim;

        /// <summary>
        /// A claim this theory carries for a consumer, which says for itself what it entails.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The claims above are JSON's: a type, a presence, a value, a domain, a spelling.</b> A
        /// consumer may know more about a value than JSON can say — the adapter knows when the service
        /// will measure a shape as a geography, which no subschema can state because the service
        /// range-checks coordinates — and needs that claim derived, guarded and combined like any other.
        /// This is where it goes, without the theory learning what it means.
        /// </para>
        /// <para>
        /// <b>It answers its own entailments, toward JSON's claims or toward its own kind.</b> What it
        /// says about the value in JSON's terms — that a geography is an object, and one that is there —
        /// is what lets the rest of the theory use it; <see cref="JsonFact.Entails"/> asks it. An
        /// extension entails nothing it does not say it does, and is excluded by nothing, which loses
        /// facts rather than inventing them.
        /// </para>
        /// <para>
        /// A record, so its equality is value equality, which two declarations of one claim need.
        /// </para>
        /// </remarks>
        public abstract record Extension : JsonClaim
        {

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            protected Extension()
            {
            }

            /// <summary>
            /// Determines whether this claim, holding of a value, establishes <paramref name="other"/>
            /// of the same value.
            /// </summary>
            /// <param name="other">The claim to establish, of the same path.</param>
            /// <returns><c>true</c> where it does; the default answers only for an equal claim.</returns>
            public virtual bool Entails(JsonClaim other) => Equals(other);

        }

        /// <summary>
        /// The value at the path is exactly this literal.
        /// </summary>
        /// <param name="Value">The literal.</param>
        public sealed record EqualTo(JsonScalar Value) : JsonClaim;

        /// <summary>
        /// The value at the path is not this literal.
        /// </summary>
        /// <remarks>
        /// Carried as its own claim rather than as a negation of <see cref="EqualTo"/> because an
        /// <c>else</c> branch's guard is exactly this, and because a query establishes it directly
        /// from a <c>&lt;&gt;</c>.
        /// </remarks>
        /// <param name="Value">The excluded literal.</param>
        public sealed record NotEqualTo(JsonScalar Value) : JsonClaim;

        /// <summary>
        /// The value at the path is one of a finite set.
        /// </summary>
        /// <param name="Values">The domain. Order is not significant; membership is.</param>
        public sealed record OneOf(IReadOnlyList<JsonScalar> Values) : JsonClaim
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
                    hash ^= value.GetHashCode();

                return hash;
            }

            static bool SetEquals(IReadOnlyList<JsonScalar> left, IReadOnlyList<JsonScalar> right)
            {
                if (left.Count != right.Count)
                    return false;

                foreach (var value in left)
                    if (Contains(right, value) == false)
                        return false;

                return true;
            }

            internal static bool Contains(IReadOnlyList<JsonScalar> values, JsonScalar value)
            {
                foreach (var candidate in values)
                    if (candidate == value)
                        return true;

                return false;
            }

        }

        /// <summary>
        /// The value at the path, where it is a number, is at least this, or above it where exclusive.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A bound binds numbers and nothing else</b>, as JSON Schema's <c>minimum</c> does: a string
        /// at the path is no counterexample. That is also what a comparison through a numeric cast can
        /// say, because the cast converts a stored string too — see
        /// <c>CosmosNumericWindow</c>.
        /// </para>
        /// <para>
        /// One claim per side rather than an interval, so that a union whose branches bound the same
        /// path differently still meets on each side the branches agree on.
        /// </para>
        /// </remarks>
        /// <param name="Value">The bound.</param>
        /// <param name="Exclusive">Whether the bound itself is excluded.</param>
        public sealed record LowerBound(decimal Value, bool Exclusive = false) : JsonClaim
        {

            /// <summary>
            /// Determines whether a number satisfies the bound.
            /// </summary>
            public bool Admits(decimal number) => number > Value || number == Value && Exclusive == false;

        }

        /// <summary>
        /// The value at the path, where it is a number, is at most this, or below it where exclusive.
        /// </summary>
        /// <remarks>
        /// The upper side of <see cref="LowerBound"/>, and binding numbers only for the same reason.
        /// </remarks>
        /// <param name="Value">The bound.</param>
        /// <param name="Exclusive">Whether the bound itself is excluded.</param>
        public sealed record UpperBound(decimal Value, bool Exclusive = false) : JsonClaim
        {

            /// <summary>
            /// Determines whether a number satisfies the bound.
            /// </summary>
            public bool Admits(decimal number) => number < Value || number == Value && Exclusive == false;

        }

        /// <summary>
        /// The value at the path, where it is a string, is spelled in the named form.
        /// </summary>
        /// <remarks>
        /// Silent about whether a string is there: a null at the path is no counterexample to how the
        /// strings are written. What the form is, the theory does not know; see
        /// <see cref="IJsonStoredForm"/>.
        /// </remarks>
        /// <param name="Form">The form, as the consumer that recognised it identifies it.</param>
        public sealed record Represents(IJsonStoredForm Form) : JsonClaim;

        /// <summary>
        /// The value at the path, where it is a string, holds UTF-16 units only below U+D800 and in these bands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A fact of JSON's like a type</b>, and the theory reasons with it as one: per path, the bands a string there
        /// may hold units of, which a conjunction narrows and a disjunction, a case split or a union widens, per branch
        /// and per scope. A declared <c>pattern</c> states it, read as the characters it admits; so does a service fact,
        /// of a property whose grammar a standard fixes. And the theory derives it, from a value or a domain a
        /// declaration or a filter states — each value's characters being its own — and from a stored form, which
        /// says how its strings are written. See <c>DESIGN.md</c> under <em>Characters, a fact of the theory</em>.
        /// </para>
        /// <para>
        /// <b>Silent about anything but a string</b>, as a pattern is: a number, a null or an absent path holds no
        /// character, and satisfies every claim of this kind.
        /// </para>
        /// </remarks>
        /// <param name="Bands">The bands beside the one below U+D800; <see cref="JsonCharacterBand.None"/> for none.</param>
        public sealed record Characters(JsonCharacterBand Bands) : JsonClaim;

    }

}
