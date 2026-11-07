using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Calcite's atom and the service's form on every cell of one path: each cell with its class, the classes the gate
    /// decides on, and the rows both sides read.
    /// </summary>
    /// <param name="Path">The path, spelled as the form spells it.</param>
    /// <param name="Cells">Every cell with its class, so that a verdict can name one.</param>
    /// <param name="Classes">The classes the cells fall in.</param>
    /// <param name="Rows">The rows the service's outcomes and Calcite's readings rest on.</param>
    internal sealed record CellOutcomes(string Path, ValueSet<CellAt> Cells, ValueSet<CellClass> Classes, ValueSet<string> Rows)
    {

        /// <summary>
        /// Pairs the atom's truths on each cell with the service's outcomes there, as each side gives them: a cell whose value
        /// is no boolean marked so, and a cell only a document an earlier round drops holds marked set aside. What a class so
        /// marked is to the gate, and the rows it then rests on, are S's rules' (<see cref="AnswerMeanings.Read"/>,
        /// <see cref="AnswerMeanings.NonBooleanRows"/>, G2). A raw reading reads its columns' union.
        /// </summary>
        public static CellOutcomes Of(PredicateAtom atom, RawTruths columns, OutcomeVector service)
        {
            var cells = service.Cells.Cells;
            var at = cells.Select((cell, i) => new CellAt(cell, new CellClass(atom.OnCell(cell, columns), service.Outcomes[i].Outcomes,
                service.Outcomes[i].NonBoolean != CosmosValueClass.None, service.Cells.Outside?.Contains(cell) == true))).ToList();
            return new CellOutcomes(service.Cells.Path, ValueSet.From(at), ValueSet.From(at.Select(c => c.Class)), service.Rows.Union(atom.Rests));
        }

        /// <summary>Whether a cell holds a value that is no boolean, which S's rule rests on the rows that read one.</summary>
        public bool HoldsNoBoolean => Classes.AsEnumerable().Any(k => k.NonBoolean);

        /// <summary>The outcomes resting on more rows, those S's rules found the classes read by too.</summary>
        public static CellOutcomes Resting(CellOutcomes outcomes, ValueSet<string> rows) => outcomes with { Rows = outcomes.Rows.Union(rows) };

    }

}
