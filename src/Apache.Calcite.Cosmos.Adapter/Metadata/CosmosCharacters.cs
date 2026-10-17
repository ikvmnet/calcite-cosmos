using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The strings at the path hold no code unit in a band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What an order over strings rests on.</b> The service orders a document's strings by code point and
    /// Calcite by UTF-16 code unit, and the two part only where a surrogate meets a unit from U+E000 — see
    /// <see cref="CosmosCharacterBand"/>. A path known to lack either band is one whose strings order alike
    /// under both, and alike under the SDK's merge of two partitions, which compares by code unit too. That is
    /// what <see cref="CosmosServiceOrder"/> reads this for.
    /// </para>
    /// <para>
    /// <b>Said of the strings, if any, as the service stores them.</b> Like a type or a stored form, the claim
    /// constrains the value only where there is a string: a path holding a number satisfies it, and so does an
    /// absent one or a JSON null, which is what <see cref="IsEntailedBy"/> says. It entails nothing else. And it
    /// is about what the service holds, which is not always what was written: a lone surrogate written as an
    /// escape is stored as U+FFFD (<see cref="CosmosPatternAlphabet"/>).
    /// </para>
    /// <para>
    /// <b>Who says it.</b> A schema, by a <c>pattern</c>, which is JSON Schema's own way of saying what characters
    /// a string holds — read by <see cref="CosmosPatternAlphabet"/>, a declaration being the first proof a user
    /// can write. And the service, of a property it maintains whose grammar a standard fixes — see
    /// <see cref="CosmosServiceFacts"/>. One claim per band, so that each is known or not on its own, as the theory
    /// knows any claim, and a union of a branch lacking both and one lacking one keeps the one.
    /// </para>
    /// </remarks>
    /// <param name="Band">The band the strings hold no unit of.</param>
    public sealed record CosmosCharacters(CosmosCharacterBand Band) : JsonClaim.Extension
    {

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// A value of another type than a string holds no character, so a type claim naming one establishes this,
        /// a null included: which is what lets the claim survive a union with <c>{"type": "null"}</c>, or one with a
        /// branch declaring a number. A value or a domain establishes it where every string in it lacks the band.
        /// </para>
        /// <para>
        /// So does a stored form: every registered form is ASCII, which <c>CosmosStoredFormsTests</c> holds each one
        /// to, and a form says nothing about whether a string is there, which this does not either.
        /// </para>
        /// </remarks>
        public override bool IsEntailedBy(JsonClaim other) => other switch
        {
            JsonClaim.OfType typed => typed.Type != JsonType.String,
            JsonClaim.EqualTo equal => Admits(equal.Value),
            JsonClaim.OneOf domain => System.Linq.Enumerable.All(domain.Values, Admits),
            JsonClaim.Represents { Form: CosmosRepresentation } => true,
            _ => false,
        };

        /// <summary>
        /// Determines whether a value satisfies the claim: it is no string, or a string with no unit in the band.
        /// </summary>
        bool Admits(JsonScalar value) => value.Type != JsonType.String || (BandsOf((string)value.ToClrValue()!) & Band) == CosmosCharacterBand.None;

        /// <summary>
        /// Returns the band a UTF-16 code unit is in: <see cref="CosmosCharacterBand.None"/> below U+D800.
        /// </summary>
        /// <remarks>
        /// The one place a unit's band is decided: a string's, a range's in a pattern, a literal's and a parameter's
        /// anchor are each read through it.
        /// </remarks>
        /// <param name="unit">The unit.</param>
        /// <returns>Its band.</returns>
        public static CosmosCharacterBand BandOf(char unit) =>
            unit < '\uD800' ? CosmosCharacterBand.None : unit <= '\uDFFF' ? CosmosCharacterBand.Surrogates : CosmosCharacterBand.HighBmp;

        /// <summary>
        /// Returns the bands a string's units are in.
        /// </summary>
        /// <param name="text">The string.</param>
        /// <returns>The bands; <see cref="CosmosCharacterBand.None"/> where every unit is below U+D800.</returns>
        public static CosmosCharacterBand BandsOf(string text)
        {
            var bands = CosmosCharacterBand.None;
            foreach (var unit in text)
                bands |= BandOf(unit);

            return bands;
        }

        /// <summary>
        /// Returns the bands of every character from one to another, each a UTF-16 code unit or a code point: a code
        /// point past U+FFFF is written in surrogates.
        /// </summary>
        /// <param name="lo">The first character.</param>
        /// <param name="hi">The last, at least <paramref name="lo"/>.</param>
        /// <returns>The bands of the units the characters are written in.</returns>
        public static CosmosCharacterBand BandsOf(int lo, int hi)
        {
            var bands = CosmosCharacterBand.None;

            if (lo <= 0xDFFF && hi >= 0xD800 || hi > 0xFFFF)
                bands |= CosmosCharacterBand.Surrogates;

            if (lo <= 0xFFFF && hi >= 0xE000)
                bands |= CosmosCharacterBand.HighBmp;

            return bands;
        }

    }

}
