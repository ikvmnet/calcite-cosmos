using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The operands of a call, as a list: a class of <c>no operands</c> and <c>operand</c> terms, the first operand the
    /// head of the list.
    /// </summary>
    internal readonly record struct Args(ClassId Id) : ISort<Args>
    {

        public static Args FromId(ClassId id) => new(id);

    }

}
