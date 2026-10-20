using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The terms of a path: <c>Root</c>, <c>Prop(p, name)</c>, <c>Index(p, i)</c>, and <c>Each(p)</c> for every element of
    /// the array at <c>p</c>. Defined once in each graph that speaks of paths — the theory's (<see cref="JsonRules"/>) and
    /// the rendering graph — so that both write one path alike.
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
        }

        public Constructor<JPath> Root { get; }

        public Constructor<JPath, string, JPath> Prop { get; }

        public Constructor<JPath, long, JPath> Index { get; }

        public Constructor<JPath, JPath> Each { get; }

    }

}
