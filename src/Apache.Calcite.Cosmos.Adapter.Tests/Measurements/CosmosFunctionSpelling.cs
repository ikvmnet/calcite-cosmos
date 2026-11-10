using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// One spelling a function row is measured by (<see cref="CosmosFunctionComparison"/>): the function by the name its
    /// rows read it by, as Calcite and as the service write it over <c>s</c>, the one lower-case <c>s</c> in each standing
    /// for the witness at <c>c.v</c>.
    /// </summary>
    /// <param name="Function">The rows' name of the function.</param>
    /// <param name="Ordinal">The operand <c>s</c> stands for, by the loader's ordinals, keyed by each witness's kind, band and
    /// atom; <see cref="FunctionKey.Every"/> where the rows read each alike, and <c>null</c> where the spelling keys only its
    /// literals (<paramref name="Fixed"/>), the witness then the subject they are asked over.</param>
    /// <param name="Calcite">Calcite's spelling.</param>
    /// <param name="Service">The service's spelling, which the probe asks.</param>
    /// <param name="Library">The function libraries Calcite's spelling is read under, as a connection's <c>fun</c> names them.</param>
    /// <param name="Fixed">The keys of the spelling's literal operands, each answered as the call answers over the witnesses
    /// of a string of no band, where every other operand's key is its base.</param>
    internal sealed record CosmosFunctionSpelling(string Function, long? Ordinal, string Calcite, string Service, string Library = "all", FunctionKey[]? Fixed = null);

}
