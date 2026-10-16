using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A SQL predicate over a row: a class of <c>compare</c>, <c>in</c>, <c>is null</c>, <c>is defined</c>, <c>and</c>,
    /// <c>or</c>, <c>not</c> and <c>opaque</c> terms.
    /// </summary>
    internal readonly record struct Pred(ClassId Id) : ISort<Pred>
    {

        public static Pred FromId(ClassId id) => new(id);

    }

}
