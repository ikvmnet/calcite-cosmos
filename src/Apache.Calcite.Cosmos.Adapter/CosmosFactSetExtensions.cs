using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// The questions the adapter asks a fact set in its own terms: which stored form it can use, and
    /// whether a path always holds a geography.
    /// </summary>
    /// <remarks>
    /// In the adapter's root namespace, so that every rule and node reaches them without a <c>using</c>
    /// of its own — the same reach the methods had when they were the fact set's own.
    /// </remarks>
    public static class CosmosFactSetExtensions
    {

        /// <summary>
        /// Returns the stored form known for a path that the adapter can use, or <c>null</c> where none is.
        /// </summary>
        /// <remarks>
        /// The question every rewrite asks first. Where a path somehow carries two representations the
        /// strongest is returned — one that preserves order beats one that only preserves equality —
        /// since both were proven and the caller wants the most it can use. Which relations a form
        /// preserves is the adapter's knowledge, measured against Calcite's comparisons, which is why
        /// the choice is made here and not in the fact set.
        /// </remarks>
        /// <param name="facts">The fact set.</param>
        /// <param name="path">The path.</param>
        /// <returns>The representation, or <c>null</c>.</returns>
        public static CosmosRepresentation? RepresentationOf(this CosmosFactSet facts, CosmosDocumentPath path)
        {
            CosmosRepresentation? best = null;

            foreach (var form in facts.FormsOf(path))
                if (form is CosmosRepresentation representation)
                    if (best is not CosmosRepresentation current || Rank(representation) > Rank(current))
                        best = representation;

            return best;
        }

        /// <summary>
        /// Orders two stored forms by how much each one licenses.
        /// </summary>
        /// <param name="representation">The form.</param>
        /// <returns>A rank, higher being the one a caller can do more with.</returns>
        static int Rank(CosmosRepresentation representation) =>
            (representation.PreservesOrder ? 2 : 0) + (representation.PreservesEquality ? 1 : 0);

        /// <summary>
        /// Determines whether a path is guaranteed to hold a geography in every document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The claim a distance sort rests on, and the reason it rests on this and not on a type.</b> A
        /// geodesic distance is null where its operand is and undefined where the service cannot measure
        /// the operand, and those are different sets: an object that is not a shape is a perfectly good
        /// object. Declaring the <em>type</em> at the path closes the first and leaves the second open,
        /// which is a key that still arrives undefined and still sorts at the wrong end. This closes
        /// both, because there is no geography that is absent, null, or one the service will not measure.
        /// </para>
        /// <para>
        /// Two claims, as everywhere else here: the path has to be there, and what is there has to be the
        /// declared thing. <see cref="CosmosGeography"/> entails <see cref="CosmosClaim.Present"/>, the one
        /// claim that does, so a container that declares the shape without marking the property required
        /// has still said a shape is there.
        /// </para>
        /// </remarks>
        /// <param name="facts">The fact set.</param>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> where every document holds a geography there.</returns>
        public static bool IsAlwaysGeography(this CosmosFactSet facts, CosmosDocumentPath path)
        {
            return path is not null && facts.Knows(new CosmosFact(path, new CosmosGeography()));
        }

    }

}
