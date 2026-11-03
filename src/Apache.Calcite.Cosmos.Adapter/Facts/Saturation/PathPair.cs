using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// What two places a comparison of two readings reads may hold together (S's alone, spec §4.2 h11): each side's import
    /// and the classes of its strings, independently, a product whose cells are pairs. F never makes one.
    /// </summary>
    /// <param name="Left">What the left place may hold.</param>
    /// <param name="LeftClasses">The classes the left place's strings' characters are in.</param>
    /// <param name="Right">What the right place may hold.</param>
    /// <param name="RightClasses">The classes the right place's strings' characters are in.</param>
    internal sealed record PathPair(PathValues Left, ValueSet<string> LeftClasses, PathValues Right, ValueSet<string> RightClasses);

}
