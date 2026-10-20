using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What one path may hold, as imported whole: its values, and the classes of the characters its strings may hold.
    /// </summary>
    /// <param name="Path">The path, spelled as <c>SqlValue.Spell</c> spells it.</param>
    /// <param name="Values">What it may hold.</param>
    /// <param name="Classes">The classes its strings' characters may be in, read through <c>JsonValues</c>.</param>
    internal sealed record PathFacts(string Path, PathValues Values, ValueSet<string> Classes);

}
