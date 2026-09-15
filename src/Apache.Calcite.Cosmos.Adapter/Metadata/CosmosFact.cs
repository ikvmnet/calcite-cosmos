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
        /// Determines whether this fact and <paramref name="other"/> cannot both hold of one document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not the negation of <see cref="Entails"/>, and a separate table for that reason.</b>
        /// Entailment is one claim implying another; exclusion is two claims having no document in
        /// common. Most pairs do neither — <c>OneOf {a,b}</c> and <c>NotEqualTo a</c> leave <c>b</c> —
        /// so the answer here is "no" unless a row says otherwise, which is the same
        /// sound-by-construction stance the table above takes.
        /// </para>
        /// <para>
        /// <b>A claim about a value does not claim the path is there</b>, and exclusion does not need
        /// it to. A declared domain constrains what a path holds <em>if it holds anything</em>; a
        /// query establishing a value outside that domain therefore keeps no document, because a
        /// document holding the value would contradict the declaration and one holding nothing fails
        /// the query's own comparison. Either way no row survives, which is the claim being made.
        /// </para>
        /// <para>
        /// An empty domain excludes nothing. It settles nothing, the same answer
        /// <see cref="AllOfType"/> gives for the same reason.
        /// </para>
        /// </remarks>
        /// <param name="other">The fact to test against.</param>
        /// <returns><c>true</c> where no document satisfies both.</returns>
        public bool Excludes(CosmosFact other)
        {
            if (Path.Equals(other.Path) == false)
                return false;

            return Exclusive(Claim, other.Claim) || Exclusive(other.Claim, Claim);
        }

        /// <summary>
        /// The exclusion table, read in one direction; <see cref="Excludes"/> asks it both ways.
        /// </summary>
        /// <param name="a">One claim.</param>
        /// <param name="b">The other.</param>
        /// <returns><c>true</c> where no document satisfies both.</returns>
        static bool Exclusive(CosmosClaim a, CosmosClaim b) => (a, b) switch
        {
            // A value settles everything about itself, so it excludes any other value, its own
            // disequality, a domain it is not in, and a type it is not of.
            (CosmosClaim.EqualTo x, CosmosClaim.EqualTo y) => Equals(x.Value, y.Value) == false,
            (CosmosClaim.EqualTo x, CosmosClaim.NotEqualTo y) => Equals(x.Value, y.Value),
            (CosmosClaim.EqualTo x, CosmosClaim.OneOf y) => y.Values.Count > 0 && CosmosClaim.OneOf.Contains(y.Values, x.Value) == false,
            (CosmosClaim.EqualTo x, CosmosClaim.OfType y) => TypeOf(x.Value) != y.Type && (y.OrNull == false || TypeOf(x.Value) != CosmosJsonType.Null),

            // Two domains exclude where they share no member; a domain and a disequality where the
            // disequality rules out every member there is.
            (CosmosClaim.OneOf x, CosmosClaim.OneOf y) => Disjoint(x.Values, y.Values),
            (CosmosClaim.OneOf x, CosmosClaim.NotEqualTo y) => OnlyValue(x.Values, y.Value),
            (CosmosClaim.OneOf x, CosmosClaim.OfType y) => NoneOfType(x.Values, y.Type, y.OrNull),

            _ => false,
        };

        /// <summary>
        /// Determines whether two domains share no member.
        /// </summary>
        /// <param name="left">One domain.</param>
        /// <param name="right">The other.</param>
        /// <returns><c>true</c> where they share none and both state something.</returns>
        static bool Disjoint(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
        {
            if (left.Count == 0 || right.Count == 0)
                return false;

            foreach (var value in left)
                if (CosmosClaim.OneOf.Contains(right, value))
                    return false;

            return true;
        }

        /// <summary>
        /// Determines whether a domain admits one value and nothing else.
        /// </summary>
        /// <param name="values">The domain.</param>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> where every member is that value.</returns>
        static bool OnlyValue(IReadOnlyList<object?> values, object? value)
        {
            if (values.Count == 0)
                return false;

            foreach (var member in values)
                if (Equals(member, value) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Determines whether no member of a domain is of one JSON type.
        /// </summary>
        /// <param name="values">The domain.</param>
        /// <param name="type">The type to test for.</param>
        /// <param name="orNull">Whether a JSON null counts as a member.</param>
        /// <returns><c>true</c> where no member is of that type and the domain states something.</returns>
        static bool NoneOfType(IReadOnlyList<object?> values, CosmosJsonType type, bool orNull)
        {
            if (values.Count == 0)
                return false;

            foreach (var value in values)
                if (TypeOf(value) == type || orNull && TypeOf(value) == CosmosJsonType.Null)
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
