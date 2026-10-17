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

            // Nothing here entails Present, and that is the whole of what a claim about a value means.
            // A schema's `properties` constrains the value a path holds *if it holds one*; only
            // `required` says it holds one at all. Reading "this is a canonical UUID" or "this is a
            // geography" as "this is there" would claim of every document what the schema claimed of
            // none, which is exactly the mistake a container holding more than one kind of document
            // punishes. An extension is a token and entails only itself, which the equality above says.
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

                // A bound binds numbers only. A stricter bound settles a looser one, a value or a domain
                // settles one every number in it satisfies, and a claim that the path holds no number
                // settles every bound: what is a string or a number of at least 0 is, if a number, at
                // least 0.
                (JsonClaim.LowerBound a, JsonClaim.LowerBound b) => a.Value > b.Value || a.Value == b.Value && (a.Exclusive || b.Exclusive == false),
                (JsonClaim.UpperBound a, JsonClaim.UpperBound b) => a.Value < b.Value || a.Value == b.Value && (a.Exclusive || b.Exclusive == false),
                (JsonClaim.EqualTo a, JsonClaim.LowerBound b) => Satisfies(a.Value, b.Admits),
                (JsonClaim.EqualTo a, JsonClaim.UpperBound b) => Satisfies(a.Value, b.Admits),
                (JsonClaim.OneOf a, JsonClaim.LowerBound b) => a.Values.Count > 0 && AllSatisfy(a.Values, b.Admits),
                (JsonClaim.OneOf a, JsonClaim.UpperBound b) => a.Values.Count > 0 && AllSatisfy(a.Values, b.Admits),
                (JsonClaim.OfType a, JsonClaim.LowerBound or JsonClaim.UpperBound) => a.Type is not (JsonType.Number or JsonType.Integer),
                (JsonClaim.Represents, JsonClaim.LowerBound or JsonClaim.UpperBound) => true,

                // What characters a string holds binds strings only, as a bound binds numbers: fewer bands settle more, a
                // claim that the path holds no string settles every such claim, a value or a domain one its strings'
                // characters satisfy — read as the theory reads a value's, through JsonFactEncoding.Characters — and a
                // stored form one the characters it is written in satisfy. The theory derives the same of one path by its
                // rules; these say it of a union's branches, which the meet reads before the theory sees them, as the
                // bounds' rows do, and go with the meet (TODO.md, *The schema reader emits splits rather than a meet*).
                (JsonClaim.Characters a, JsonClaim.Characters b) => (a.Bands & ~b.Bands) == JsonCharacterBand.None,
                (JsonClaim.OfType a, JsonClaim.Characters) => a.Type != JsonType.String,
                (JsonClaim.EqualTo a, JsonClaim.Characters b) => (JsonFactEncoding.Characters(a.Value) & ~b.Bands) == JsonCharacterBand.None,
                (JsonClaim.OneOf a, JsonClaim.Characters b) => a.Values.Count > 0 && System.Linq.Enumerable.All(a.Values, v => (JsonFactEncoding.Characters(v) & ~b.Bands) == JsonCharacterBand.None),
                (JsonClaim.Represents a, JsonClaim.Characters b) => (a.Form.Characters & ~b.Bands) == JsonCharacterBand.None,

                // And what a string was written in, which a pattern says, by the same rows: a value or a form is written as
                // it is stored, none of them holding a lone surrogate.
                (JsonClaim.Written a, JsonClaim.Written b) => (a.Bands & ~b.Bands) == JsonCharacterBand.None,
                (JsonClaim.OfType a, JsonClaim.Written) => a.Type != JsonType.String,
                (JsonClaim.EqualTo a, JsonClaim.Written b) => (JsonFactEncoding.Characters(a.Value) & ~b.Bands) == JsonCharacterBand.None,
                (JsonClaim.OneOf a, JsonClaim.Written b) => a.Values.Count > 0 && System.Linq.Enumerable.All(a.Values, v => (JsonFactEncoding.Characters(v) & ~b.Bands) == JsonCharacterBand.None),
                (JsonClaim.Represents a, JsonClaim.Written b) => (a.Form.Characters & ~b.Bands) == JsonCharacterBand.None,

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
        /// Determines whether a value satisfies a bound: a number the bound admits, or no number at all.
        /// </summary>
        /// <remarks>
        /// A number held approximately is refused, its place against a bound not being read.
        /// </remarks>
        static bool Satisfies(JsonScalar value, Func<decimal, bool> admits) => value.ToClrValue() switch
        {
            decimal number => admits(number),
            double => false,
            _ => true,
        };

        static bool AllSatisfy(IReadOnlyList<JsonScalar> values, Func<decimal, bool> admits)
        {
            foreach (var value in values)
                if (Satisfies(value, admits) == false)
                    return false;

            return true;
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
