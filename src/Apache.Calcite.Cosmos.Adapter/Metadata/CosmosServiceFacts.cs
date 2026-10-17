using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What the service guarantees about the properties it maintains itself, read as a source of
    /// facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not delivered through any schema, and true of every container.</b> A model file can describe
    /// what an application writes; it has nothing to say about <c>id</c> or <c>_ts</c>, which the
    /// service defines. So these are stated here and assembled into the same theory a declared schema
    /// contributes to — one theory rather than two, because a declared rule's body may be satisfied by
    /// a fact stated here and only a context holding both fires it.
    /// </para>
    /// <para>
    /// <b>The trust is of a different kind, and that is worth knowing rather than encoding.</b> A fact
    /// from a model file is a promise the adapter believes; a fact here is a property of the store. A
    /// document violating one of these does not exist.
    /// </para>
    /// <para>
    /// Three of them, and no more. <c>_rid</c>, <c>_self</c> and <c>_attachments</c> are just as
    /// guaranteed and nothing would read a fact about them — they are not what anyone filters on — so
    /// stating them would be weight with no consumer.
    /// </para>
    /// <para>
    /// <b>Each says what an order needs of it, as a fact and not as a case.</b> An order the service
    /// computes is read through <see cref="CosmosServiceOrder"/> from what the facts say of the path, and
    /// the service's own properties are paths like any other: <c>_ts</c> orders as a <c>BIGINT</c> because
    /// it is an integer with a bound, and <c>_etag</c> as text because its characters are stated. Nothing
    /// there names either.
    /// </para>
    /// </remarks>
    public static class CosmosServiceFacts
    {

        /// <summary>
        /// The rules themselves, built once.
        /// </summary>
        static readonly JsonFactRule[] Stated = Build();

        /// <summary>
        /// Builds the rules for the properties the service maintains.
        /// </summary>
        /// <returns>The rules.</returns>
        static JsonFactRule[] Build()
        {
            JsonFactRule[] About(string property, JsonType type)
            {
                var path = JsonDocumentPath.Root.Property(property);

                return new[]
                {
                    JsonFactRule.Unconditional(new JsonFact(path, new JsonClaim.OfType(type))),
                    JsonFactRule.Unconditional(new JsonFact(path, new JsonClaim.Present())),
                };
            }

            var rules = new List<JsonFactRule>();

            // Required of every item, and a string: the service rejects a document whose id is a
            // number, and generates one where none is given.
            rules.AddRange(About(CosmosContainerMetadata.IdPropertyName, JsonType.String));

            // Service-generated on every write, in epoch seconds. The only temporal value in a
            // container whose encoding is defined rather than a matter of application convention,
            // which is why it is the one a query can reason about without being told anything.
            rules.AddRange(About(CosmosContainerMetadata.TimestampPropertyName, JsonType.Integer));

            // And a bound, which is what lets it order as the BIGINT it is read as: an integer below 2^53
            // is one value whichever way the service holds it or a response prints it, where an
            // unbounded BIGINT is not (see CosmosServiceOrder). Epoch seconds are not negative, and 2^53
            // seconds is some 285 million years.
            var timestamp = JsonDocumentPath.Root.Property(CosmosContainerMetadata.TimestampPropertyName);
            rules.Add(JsonFactRule.Unconditional(new JsonFact(timestamp, new JsonClaim.LowerBound(0))));
            rules.Add(JsonFactRule.Unconditional(new JsonFact(timestamp, new JsonClaim.UpperBound(CosmosServiceOrder.TwoTo53, Exclusive: true))));

            // Service-generated, and a string. Excluded from the index by default, which bears on what
            // a predicate over it costs, and makes an ORDER BY over it a 400 (see
            // CosmosContainerMetadata.IsPathIndexed); not on what it is.
            rules.AddRange(About(CosmosContainerMetadata.ETagPropertyName, JsonType.String));

            // An entity tag, whose grammar RFC 9110 fixes: a quoted run of '!', 0x23 to 0x7E and 0x80
            // to 0xFF. No unit at or above U+D800, so its strings order alike by code point and by
            // UTF-16 code unit (see CosmosCharacterBand), which is what lets MIN and MAX over it push.
            var etag = JsonDocumentPath.Root.Property(CosmosContainerMetadata.ETagPropertyName);
            rules.Add(JsonFactRule.Unconditional(new JsonFact(etag, new CosmosCharacters(CosmosCharacterBand.Surrogates))));
            rules.Add(JsonFactRule.Unconditional(new JsonFact(etag, new CosmosCharacters(CosmosCharacterBand.HighBmp))));

            return rules.ToArray();
        }

        /// <summary>
        /// Gets the facts every container's documents satisfy.
        /// </summary>
        public static IReadOnlyList<JsonFactRule> Rules => Stated;

    }

}
