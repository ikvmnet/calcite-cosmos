using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What Calcite's traversal of an array of an element type, in a form, yields in process, a <c>Calcite</c> row's:
    /// <c>UNNEST</c> of a plain <c>JSON_QUERY</c> returning a collection of the type, in the form <c>Form</c> names, yields an
    /// element of each class named, and each reading of it answers what the reading named answers over a path holding the
    /// same value. Read by <see cref="CosmosAxiomRows.ReadYieldKinds"/> and <see cref="CosmosAxiomRows.ReadYieldReads"/> into
    /// both graphs' <c>yieldkinds</c> and <c>yieldreads</c>; an element of a type or a form no row names holds anything and
    /// reads as nothing.
    /// </summary>
    /// <param name="Type">The element type, by its <c>SqlTypeName</c>'s name, any length dropped: <c>VARCHAR</c>.</param>
    /// <param name="Form">The traversal's form, as the binding names it (<c>CosmosPathSegment.Yielded</c>): the collection's
    /// <c>SqlTypeName</c>'s name, and <c>WITH ORDINALITY</c> after it where the planned traversal keeps its ordinal:
    /// <c>ARRAY</c>.</param>
    /// <param name="Classes">The classes of value the traversal yields an element of.</param>
    /// <param name="Reader">The reading an element reads as, by the name a reader takes: <c>text</c>.</param>
    internal sealed record CosmosTraversalYield(string Type, string Form, IReadOnlyList<CosmosValueClass> Classes, string Reader);

}
