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
    /// <b>Said of the strings, if any.</b> Like a type or a stored form, the claim constrains the value only
    /// where there is a string: a path holding a number satisfies it, and so does an absent one. It entails
    /// nothing else.
    /// </para>
    /// <para>
    /// <b>Who says it.</b> The service, of a property it maintains whose grammar a standard fixes — see
    /// <see cref="CosmosServiceFacts"/> — and nothing else yet. A schema's <c>pattern</c> declares the
    /// characters of a path in its own vocabulary, and reading one into this claim is the next thing to build.
    /// </para>
    /// </remarks>
    /// <param name="Band">The band the strings hold no unit of.</param>
    public sealed record CosmosCharacters(CosmosCharacterBand Band) : JsonClaim.Extension;

}
