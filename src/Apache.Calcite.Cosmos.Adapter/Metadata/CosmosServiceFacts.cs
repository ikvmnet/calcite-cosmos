using System;
using System.Collections.Generic;

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
    /// a fact stated here and forward chaining fires such a rule only when it sees both at once.
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
    /// </remarks>
    public static class CosmosServiceFacts
    {

        static readonly CosmosFactRule[] Stated = Build();

        static CosmosFactRule[] Build()
        {
            CosmosFactRule[] About(string property, CosmosJsonType type)
            {
                var path = CosmosDocumentPath.Root.Property(property);

                return new[]
                {
                    CosmosFactRule.Unconditional(new CosmosFact(path, new CosmosClaim.OfType(type))),
                    CosmosFactRule.Unconditional(new CosmosFact(path, new CosmosClaim.Present())),
                };
            }

            var rules = new List<CosmosFactRule>();

            // Required of every item, and a string: the service rejects a document whose id is a
            // number, and generates one where none is given.
            rules.AddRange(About(CosmosContainerMetadata.IdPropertyName, CosmosJsonType.String));

            // Service-generated on every write, in epoch seconds. The only temporal value in a
            // container whose encoding is defined rather than a matter of application convention,
            // which is why it is the one a query can reason about without being told anything.
            rules.AddRange(About(CosmosContainerMetadata.TimestampPropertyName, CosmosJsonType.Integer));

            // Service-generated, and a string. Excluded from the index by default, which bears on what
            // a predicate over it costs and not on what it is.
            rules.AddRange(About(CosmosContainerMetadata.ETagPropertyName, CosmosJsonType.String));

            return rules.ToArray();
        }

        /// <summary>
        /// Gets the facts every container's documents satisfy.
        /// </summary>
        public static IReadOnlyList<CosmosFactRule> Rules => Stated;

    }

}
