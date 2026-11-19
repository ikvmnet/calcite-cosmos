using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The terms of a path: <c>Root</c>, <c>Prop(p, name)</c>, <c>Index(p, i)</c>, <c>Each(p)</c> for every element of
    /// the array at <c>p</c>, and <c>Yield(p, t, n)</c> for the element traversal <c>n</c> of it yields, read as element type
    /// <c>t</c>. Defined once in each graph that speaks of paths — the theory's (<see cref="JsonRules"/>) and the rendering
    /// graph — so that both write one path alike.
    /// </summary>
    internal sealed class PathVocabulary
    {

        public PathVocabulary(EGraph graph)
        {
            graph.DefineSort<JPath>("Path");
            Root = graph.DefineConstructor<JPath>("Root");
            Prop = graph.DefineConstructor<JPath, string, JPath>("Prop");
            Index = graph.DefineConstructor<JPath, long, JPath>("Index");
            Each = graph.DefineConstructor<JPath, JPath>("Each");
            Yield = graph.DefineConstructor<JPath, string, long, JPath>("Yield");
        }

        public Constructor<JPath> Root { get; }

        public Constructor<JPath, string, JPath> Prop { get; }

        public Constructor<JPath, long, JPath> Index { get; }

        public Constructor<JPath, JPath> Each { get; }

        /// <summary>
        /// The element traversal <c>n</c> of the array at <c>p</c> yields, read as element type <c>t</c> by its SQL type name
        /// (<see cref="JsonYieldedStep"/>): a path of its own, so two traversals of one array yield two, an instance of
        /// every element of the array, and holding only what its type's row says the traversal yields.
        /// </summary>
        public Constructor<JPath, string, long, JPath> Yield { get; }

    }

}
