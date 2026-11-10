using System;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A row saying that where a string holds characters of one class, what is made of it may hold characters of another
    /// too: what the service stores of a string written in a class (<c>storedAs</c>), or what a function Calcite computes
    /// makes of an operand's (<c>callMaps</c>). Data the rules read; the one evaluation of a set of them is
    /// <see cref="JsonValues.Mapped"/>.
    /// </summary>
    /// <param name="From">The class held, by its member (<see cref="JsonValues.CharacterClasses"/>).</param>
    /// <param name="To">The class it may make, by its member.</param>
    internal readonly record struct JsonClassMap(string From, string To) : IComparable<JsonClassMap>
    {

        /// <inheritdoc/>
        public int CompareTo(JsonClassMap other) =>
            string.CompareOrdinal(From, other.From) is var f and not 0 ? f : string.CompareOrdinal(To, other.To);

    }

}
