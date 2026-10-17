using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The value at the path, where there is one, is a geography the service will measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read off a subschema, and the one claim here JSON's own cannot state.</b> Measured against an
    /// account, <c>{"type":"Point","coordinates":[999,999]}</c> validates against every published GeoJSON
    /// subschema and <c>ST_DISTANCE</c> over it still answers <em>undefined</em>, because the service
    /// range-checks coordinates; so referencing GeoJSON, by a <c>$ref</c> or a <c>format</c>, proves GeoJSON,
    /// which is not what needed proving. <see cref="CosmosGeographyForms"/> reads the proof off what the
    /// subschema says — an object, its members required, its type pinned, its ordinates bounded — and states
    /// this. See <c>CosmosGeographyValidityMeasurementTests</c>.
    /// </para>
    /// <para>
    /// <b>A token the theory carries and reads nothing of</b> (<see cref="JsonClaim.Extension"/>). Like every
    /// claim but a presence it says what the value is if there is one, and says nothing of whether there is:
    /// that is the parent's <c>required</c>, which the schema reader states as it states any. It entails
    /// nothing either. The subschema's <c>"type": "object"</c>, which the recogniser asks for, is the schema
    /// reader's to state, and is.
    /// </para>
    /// <para>
    /// <b>What it buys is a sort.</b> A geodesic distance over a path that is there and holds this can be
    /// neither null nor undefined, so the placement Calcite asks for has nothing to disagree with and the
    /// <c>ORDER BY</c> reaches the service under either collation — see <see cref="CosmosServiceOrder"/> and
    /// <see cref="CosmosFactSetExtensions.IsAlwaysGeography"/>, which asks both.
    /// </para>
    /// </remarks>
    public sealed record CosmosGeography : JsonClaim.Extension;

}
