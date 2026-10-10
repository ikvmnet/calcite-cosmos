using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The value at the path is a geography the service will measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared, not derived, and it is the one claim here that could not be either.</b> JSON's claims
    /// restate something a schema says in its own vocabulary — a type, a domain, a presence. This one
    /// cannot be: measured against an account, <c>{"type":"Point","coordinates":[999,999]}</c> validates
    /// against every GeoJSON subschema anyone could write and <c>ST_DISTANCE</c> over it still answers
    /// <em>undefined</em>, because the service range-checks coordinates. Expressing that in a schema
    /// would take numeric bounds on array elements, which this model does not carry. See
    /// <c>CosmosGeographyValidityMeasurementTests</c>.
    /// </para>
    /// <para>
    /// <b>So it is the container's word, on the same footing as every other.</b> Nothing verifies that a
    /// <c>pattern</c> naming a UUID is honoured by the documents either; a declaration is believed, and a
    /// document that contradicts it is the one thing the model has always said it cannot check in
    /// advance.
    /// </para>
    /// <para>
    /// <b>It is said in GeoJSON's own vocabulary and not in one invented here.</b> A container declares it
    /// by referencing a published geometry schema — <c>https://geojson.org/schema/Geometry.json</c>, or one
    /// of the six concrete shapes beside it — whose <c>$id</c> already means exactly this. An earlier
    /// draft read a <c>format</c> token of this repository's own; JSON Schema permits such a thing, but no
    /// peer implementation is expected to understand one, and a schema declaring the Format-Assertion
    /// vocabulary must <em>reject</em> an unknown format outright. A <c>$ref</c> asks nothing of anyone
    /// and is already what a schema author writes to say a property is a geometry.
    /// </para>
    /// <para>
    /// <b>The adapter's claim, carried by the fact theory as an extension.</b> What a geography is in
    /// JSON's terms — an object, and one that is there — is what it tells the theory, and is all the
    /// theory knows of it. See <see cref="CosmosClaim.Extension"/>.
    /// </para>
    /// <para>
    /// <b>What it buys is a sort.</b> A geodesic distance over a path this holds for can be neither null
    /// nor undefined, so the placement Calcite asks for has nothing to disagree with and the
    /// <c>ORDER BY</c> reaches the service under either collation — see <c>CosmosSortRule.AlwaysDefined</c>.
    /// </para>
    /// </remarks>
    public sealed record CosmosGeography : CosmosClaim.Extension
    {

        /// <inheritdoc />
        /// <remarks>
        /// A geography is an object, and one that is there: the declaration is about a value the service
        /// can measure, and there is no such value that is absent or null. So it settles the object claim
        /// either way round, unlike a stored form, which says what the strings look like without saying
        /// one is there — and it is the one claim the theory carries that entails a presence.
        /// </remarks>
        public override bool Entails(CosmosClaim other) => other switch
        {
            CosmosClaim.OfType typed => typed.Type == CosmosJsonType.Object,
            CosmosClaim.Present => true,
            _ => Equals(other),
        };

    }

}
