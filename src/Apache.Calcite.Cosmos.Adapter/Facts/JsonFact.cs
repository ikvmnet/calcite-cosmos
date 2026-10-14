using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// An atom: one claim about one path.
    /// </summary>
    /// <param name="Path">The path the claim is about.</param>
    /// <param name="Claim">What is claimed.</param>
    public readonly record struct JsonFact(JsonDocumentPath Path, JsonClaim Claim)
    {

        /// <summary>
        /// Determines whether this fact, being known, establishes <paramref name="other"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What the schema reader's meet of a union asks, and nothing else: a claim is kept where every
        /// branch states one entailing it. The theory answers a query's questions from its own tables,
        /// where a value settles its type and a domain within another settles that one. When the reader
        /// states a union's branches as cases of a hidden discriminator instead, this goes with the meet.
        /// See <c>DESIGN.md</c> under <em>The theory of JSON, in layers</em>.
        /// </para>
        /// <para>
        /// Only claims about the same path relate. Nothing here crosses paths; that is what the
        /// theory's rules are for.
        /// </para>
        /// </remarks>
        /// <param name="other">The fact to establish.</param>
        /// <returns><c>true</c> if knowing this fact means <paramref name="other"/> holds.</returns>
        public bool Entails(JsonFact other)
        {
            if (Path.Equals(other.Path) == false)
                return false;

            if (Claim.Equals(other.Claim))
                return true;

            // Almost nothing here entails Present, and that is the whole of what a claim about a
            // value means. A schema's `properties` constrains the value a path holds *if it holds
            // one*; only `required` says it holds one at all. Reading "this is a canonical UUID" as
            // "this is there" would claim of every document what the schema claimed of none, which is
            // exactly the mistake a container holding more than one kind of document punishes.
            //
            // An extension claim may be the exception, and says so itself: a consumer's claim about a
            // value can be one that no absent value satisfies, the way a geography the service will
            // measure is. It is still conditional on its guard like every other rule, so a path under
            // a discriminator says nothing until the discriminator is proven.
            return (Claim, other.Claim) switch
            {
                // A known value settles membership, type and every disequality but its own.
                (JsonClaim.EqualTo a, JsonClaim.OneOf b) => JsonClaim.OneOf.Contains(b.Values, a.Value),
                (JsonClaim.EqualTo a, JsonClaim.OfType b) => a.Value.Type.IsA(b.Type) || b.OrNull && a.Value.Type == JsonType.Null,
                (JsonClaim.EqualTo a, JsonClaim.NotEqualTo b) => a.Value == b.Value == false,

                // A domain settles a type where every member shares one, and refutes anything
                // outside it.
                (JsonClaim.OneOf a, JsonClaim.OfType b) => AllOfType(a.Values, b.Type, b.OrNull),
                (JsonClaim.OneOf a, JsonClaim.NotEqualTo b) => JsonClaim.OneOf.Contains(a.Values, b.Value) == false,

                // A domain within another settles it: what is one of {open, closed} is one of
                // {open, closed, archived}, which is how an anyOf of two enums meets.
                (JsonClaim.OneOf a, JsonClaim.OneOf b) => a.Values.Count > 0 && Within(a.Values, b.Values),

                // A stored form is a string.
                // A stored form says what the strings at a path look like, and says nothing about
                // whether a null is there beside them -- so it entails only the claim that admits one.
                (JsonClaim.Represents, JsonClaim.OfType b) => b.Type == JsonType.String && b.OrNull,

                // A consumer's claim answers for itself. See JsonClaim.Extension.
                (JsonClaim.Extension a, var b) => a.Entails(b),

                // A null is a value every nullable claim admits: a type that admits one, a domain
                // holding one, a disequality with anything else, and a stored form, which says how
                // the strings are written and nothing about whether one is there (#175). Without
                // these, a union whose branches say "a string" and "null" had no meet at all.
                (JsonClaim.OfType { Type: JsonType.Null }, JsonClaim.OfType b) => b.Type == JsonType.Null || b.OrNull,
                (JsonClaim.OfType { Type: JsonType.Null }, JsonClaim.EqualTo b) => b.Value.IsNull,
                (JsonClaim.OfType { Type: JsonType.Null }, JsonClaim.OneOf b) => JsonClaim.OneOf.Contains(b.Values, JsonScalar.Null),
                (JsonClaim.OfType { Type: JsonType.Null }, JsonClaim.NotEqualTo b) => b.Value.IsNull == false,
                (JsonClaim.OfType { Type: JsonType.Null }, JsonClaim.Represents) => true,
                (JsonClaim.EqualTo { Value.IsNull: true }, JsonClaim.Represents) => true,

                // A wider type is weaker than a narrower one -- an integer is a number -- and admitting
                // a null is weaker than not admitting one.
                (JsonClaim.OfType a, JsonClaim.OfType b) => a.Type.IsA(b.Type) && (b.OrNull || a.OrNull == false),

                _ => false,
            };
        }

        /// <summary>
        /// Determines whether every member of one domain is a member of another.
        /// </summary>
        static bool Within(IReadOnlyList<JsonScalar> values, IReadOnlyList<JsonScalar> of)
        {
            foreach (var value in values)
                if (JsonClaim.OneOf.Contains(of, value) == false)
                    return false;

            return true;
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
        static bool AllOfType(IReadOnlyList<JsonScalar> values, JsonType type, bool orNull)
        {
            if (values.Count == 0)
                return false;

            foreach (var value in values)
                if (value.Type.IsA(type) == false && (orNull == false || value.Type != JsonType.Null))
                    return false;

            return true;
        }

    }

}
