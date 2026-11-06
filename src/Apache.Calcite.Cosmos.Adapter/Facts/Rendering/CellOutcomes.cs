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
        /// Pairs the atom's truths on each cell with the service's outcome there. A whole condition over a value that is no
        /// boolean reads as the meanings say, resting on the rows they name (<see cref="AnswerMeanings.Whole"/>,
        /// <see cref="AnswerMeanings.NonBooleanRows"/>). On a cell only a
        /// document an earlier round drops holds, Calcite's truths are those the policy sets aside (<c>tolerated</c>, which a
        /// rule hands in): anything but a failure refines them, and they refute nothing. A raw reading reads its columns' union.
        /// </summary>
        public static CellOutcomes Of(PredicateAtom atom, RawTruths columns, OutcomeVector service, AnswerMeanings means, Truths tolerated)
        {
            var cells = service.Cells.Cells;
            var at = cells.Select((cell, i) => new CellAt(cell, new CellClass(service.Cells.Outside?.Contains(cell) == true ? tolerated : atom.OnCell(cell, columns),
                service.Outcomes[i].NonBoolean == CosmosValueClass.None ? service.Outcomes[i].Outcomes : means.Whole(service.Outcomes[i].Outcomes)))).ToList();
            var rows = service.Rows.Union(atom.Rests);
            if (service.Outcomes.Any(o => o.NonBoolean != CosmosValueClass.None))
                rows = rows.Union(means.NonBooleanRows);

            return new CellOutcomes(service.Cells.Path, ValueSet.From(at), ValueSet.From(at.Select(c => c.Class)), rows);
        }

    }

}
