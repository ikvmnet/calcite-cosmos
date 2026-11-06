using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What the targets in <see cref="On"/> computed for one function over an operand of one kind and band, against
    /// Calcite's: one key of a function row (<see cref="FunctionKey"/>), read by <see cref="CosmosAxiomRows.ReadFunctions"/>.
    /// </summary>
    /// <param name="Function">The function, by the name the IR reads it by.</param>
    /// <param name="Subject">The operand the key's kind and band are of, by the loader's ordinals (a trim's flag is no
    /// operand), or <see cref="FunctionKey.Every"/> where the function reads each alike: what its witnesses varied, the
    /// rest held to literals its spelling's legality judges.</param>
    /// <param name="Operand">The kind of the operand its characters are asked of.</param>
    /// <param name="Band">The band of the operand's characters, for a string; <see cref="JsonCharacterBand.None"/> otherwise.</param>
    /// <param name="On">The targets that ran this key's witnesses.</param>
    /// <param name="Answer">What they computed.</param>
    /// <param name="Value">The class of the operand's value the witnesses were drawn from (F2); <see cref="FunctionValueClass.Any"/>
    /// where they varied it over every value of the kind and band.</param>
    /// <param name="Evidence">The row whose own measurement ran this key's witnesses on <paramref name="On"/>, where the row it
    /// is listed in did not run them there; <c>null</c> where that row's run did.</param>
    internal sealed record CosmosFunctionOutcome(string Function, long Subject, CosmosValueClass Operand, JsonCharacterBand Band, CosmosAxiomTarget On, CosmosFunctionAnswer Answer,
        FunctionValueClass Value = FunctionValueClass.Any, string? Evidence = null);

}
