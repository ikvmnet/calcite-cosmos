using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A SQL value a filter reads or names: a class of <c>text</c>, <c>raw</c>, <c>typed</c>, <c>cast</c>, <c>literal</c>
    /// and <c>parameter</c> terms.
    /// </summary>
    internal readonly record struct Val(ClassId Id) : ISort<Val>
    {

        public static Val FromId(ClassId id) => new(id);

    }

}
