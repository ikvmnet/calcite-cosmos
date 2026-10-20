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
    /// <param name="Count">How many cells there are.</param>
    /// <param name="Answering">How many cells Calcite answers on other than by raising alone.</param>
    internal sealed record CellOutcomes(string Path, ValueSet<CellAt> Cells, ValueSet<CellClass> Classes, ValueSet<string> Rows, int Count, int Answering)
    {

        static readonly ValueSet<string> WholeCondition = ValueSet.Of("Service.BarePathKeepsOnlyTrue", "Service.NotOverAPathKeepsOnlyFalse");

        /// <summary>
        /// Pairs the atom's truths on each cell with the service's outcome there. A value that is no boolean, as a whole
        /// condition, is kept by neither <c>WHERE</c> nor <c>WHERE NOT</c>, and is read as undefined on those rows' word.
        /// </summary>
        public static CellOutcomes Of(PredicateAtom atom, OutcomeVector service)
        {
            var cells = service.Cells.Cells;
            var at = cells.Select((cell, i) => new CellAt(cell, new CellClass(atom.OnCell(cell), Whole(service.Outcomes[i])))).ToList();
            var rows = service.Rows.Union(atom.Rests);
            if (service.Outcomes.Any(o => o.NonBoolean != CosmosValueClass.None))
                rows = rows.Union(WholeCondition);

            return new CellOutcomes(service.Cells.Path, ValueSet.From(at), ValueSet.From(at.Select(c => c.Class)), rows, cells.Count,
                at.Count(c => (c.Class.Calcite & Truths.Any) != Truths.None));
        }

        static ServiceOutcomes Whole(CellOutcome outcome) =>
            outcome.NonBoolean == CosmosValueClass.None ? outcome.Outcomes : ServiceOutcomes.Undefined | (outcome.Outcomes & ServiceOutcomes.Fails);

    }

}
