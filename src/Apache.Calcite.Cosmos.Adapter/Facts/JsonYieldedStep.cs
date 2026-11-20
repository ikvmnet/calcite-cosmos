using System;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The element a traversal of an array yields, as a step of a path (<see cref="JsonDocumentPath.Yielded"/>) and a
    /// segment of the theory's (<see cref="Saturation.JsonPathSpec"/>): the element type the traversal reads it as, and the
    /// traversal's number. An element of its own, neither an index nor every element, so two traversals of one array are two
    /// paths; the facts graph reads it as <c>Yield</c> (<see cref="Saturation.PathVocabulary.Yield"/>), and nothing spells it
    /// into a request, a policy or a document read.
    /// </summary>
    /// <param name="Type">The element type, by its <c>SqlTypeName</c>'s name, any length dropped: <c>VARCHAR</c>. Data: what an
    /// element of it holds is its row's (<c>yieldkinds</c>), a reading of it reads only where its row names that reading
    /// (<c>yieldreads</c>), and a type no row names reads nothing.</param>
    /// <param name="Traversal">The traversal's number, from zero, in the order the binding walks a statement's traversals.</param>
    internal sealed record JsonYieldedStep(string Type, long Traversal)
    {

        /// <summary>The element type, by its <c>SqlTypeName</c>'s name.</summary>
        public string Type { get; } = Type ?? throw new ArgumentNullException(nameof(Type));

        /// <summary>The traversal's number, from zero.</summary>
        public long Traversal { get; } = Traversal >= 0 ? Traversal : throw new ArgumentOutOfRangeException(nameof(Traversal));

        /// <summary>Spells the step as no name and no index is spelled: <c>yield VARCHAR 0</c>.</summary>
        public override string ToString() => "yield " + Type + " " + Traversal.ToString(CultureInfo.InvariantCulture);

    }

}
