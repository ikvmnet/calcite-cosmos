using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What the targets in <see cref="On"/> answered for one test against one class of operand, cell class by cell class:
    /// the truth a <c>WHERE</c> reads, established by the row it keeps and the row <c>NOT</c> keeps, spelled
    /// <c>absent=false null=true …</c> in the names of <see cref="CosmosValueClass"/> and <see cref="CosmosAnswer"/>.
    /// </summary>
    /// <param name="On">The targets that ran this outcome's own witnesses; a target that answered otherwise, or a cell one
    /// ran and another did not, is an outcome of its own.</param>
    /// <param name="Formula">A composite's Cosmos SQL over <c>c.v</c>, whose answers are a check of composition through
    /// the atomic rows, never rows S loads.</param>
    internal sealed record CosmosOutcome(CosmosServiceTest Test, CosmosValueClass Operand, CosmosAxiomTarget On, string Answers, string? Formula = null)
    {

        /// <summary>
        /// The answers, parsed as the registry is initialised, so a malformed vector — a name no enum holds, a class
        /// twice — fails every test that reads the registry.
        /// </summary>
        public IReadOnlyList<(CosmosValueClass Cell, CosmosAnswer Answer)> Cells { get; } = Parse(Answers);

        static (CosmosValueClass, CosmosAnswer)[] Parse(string answers)
        {
            var cells = answers.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=') is [var cell, var answer] ? (Name<CosmosValueClass>(cell), Name<CosmosAnswer>(answer)) : throw new FormatException($"'{pair}' is not cell=answer."))
                .ToArray();

            return cells.Length > 0 && cells.DistinctBy(c => c.Item1).Count() == cells.Length ? cells : throw new FormatException($"'{answers}' names no cell, or one twice.");
        }

        /// <summary>A member by its name in any case; not a number, nor a list of flags, as <see cref="Enum.Parse{TEnum}(string, bool)"/> allows.</summary>
        static T Name<T>(string name) where T : struct, Enum =>
            Enum.GetValues<T>().Where(v => string.Equals(v.ToString(), name, StringComparison.OrdinalIgnoreCase)).Select(v => (T?)v).SingleOrDefault()
            ?? throw new FormatException($"{typeof(T).Name} has no member '{name}'.");

    }

}
