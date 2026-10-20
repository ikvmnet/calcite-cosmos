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
        /// Pairs the atom's truths on each cell with the service's outcome there. Where a value that is no boolean is not
        /// kept, a whole condition reads as the meanings say; what its row keeps, and a failure, stand.
        /// </summary>
        public static CellOutcomes Of(PredicateAtom atom, OutcomeVector service, AnswerMeanings means)
        {
            var cells = service.Cells.Cells;
            var at = cells.Select((cell, i) => new CellAt(cell, new CellClass(atom.OnCell(cell), Whole(service.Outcomes[i], means)))).ToList();
            var rows = service.Rows.Union(atom.Rests);
            if (service.Outcomes.Any(o => o.NonBoolean != CosmosValueClass.None))
                rows = rows.Union(WholeCondition);

            return new CellOutcomes(service.Cells.Path, ValueSet.From(at), ValueSet.From(at.Select(c => c.Class)), rows, cells.Count,
                at.Count(c => (c.Class.Calcite & Truths.Any) != Truths.None));
        }

        static ServiceOutcomes Whole(CellOutcome outcome, AnswerMeanings means) => outcome.NonBoolean == CosmosValueClass.None ? outcome.Outcomes
            : (outcome.Outcomes & (ServiceOutcomes.True | ServiceOutcomes.Fails)) | ((outcome.Outcomes & (ServiceOutcomes.False | ServiceOutcomes.Undefined)) != 0 ? means.NonBoolean : 0);

    }

}
