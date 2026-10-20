using System;
using System.Collections.Generic;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One connective over two concrete outcome vectors of one partition, cell by cell, by the connective's rows: each
    /// pair of the sides' outcomes, or a value that is no boolean by its class, through the row the pair keys.
    /// </summary>
    /// <remarks>
    /// <b>A side that may fail may fail the compound</b>, where the service evaluates it (decision 2); and no row says the
    /// service evaluates a side the other decides alone, so beside its failure it stands for every truth it could have
    /// given. A key the table does not hold answers every outcome and is named.
    /// </remarks>
    internal static class ServiceCompound
    {

        /// <param name="compound">A negation, whose two vectors are its operand's, or a junction.</param>
        /// <param name="first">The vector of <see cref="ServiceForm.First"/>.</param>
        /// <param name="rest">The vector of <see cref="ServiceForm.Rest"/>.</param>
        /// <param name="table">The set's table, which holds the connective's rows.</param>
        public static OutcomeVector On(ServiceForm compound, OutcomeVector first, OutcomeVector rest, ServiceTable table)
        {
            if (compound.Kind is not (ServiceFormKind.Not or ServiceFormKind.And or ServiceFormKind.Or) || first.Cells.Equals(rest.Cells) == false)
                throw new ArgumentException("A connective is evaluated over its parts' vectors on one partition.", nameof(compound));

            var rows = new HashSet<string>(first.Rows.Union(rest.Rows), StringComparer.Ordinal);
            var outcomes = new List<CellOutcome>(first.Outcomes.Count);
            for (var i = 0; i < first.Outcomes.Count; i++)
            {
                var (a, b) = (first.Outcomes[i], rest.Outcomes[i]);
                var result = (a.Outcomes | b.Outcomes) & ServiceOutcomes.Fails;
                foreach (var x in Operands(a))
                {
                    if (compound.Kind == ServiceFormKind.Not)
                        result |= table.Outcomes(MeasuredKey.Of(CosmosServiceTest.Not, CosmosValueClass.None, x), rows);
                    else
                        foreach (var y in Operands(b))
                            result |= table.Outcomes(MeasuredKey.Of(compound.Test, x, y), rows);
                }

                outcomes.Add(new CellOutcome(result, CosmosValueClass.None));
            }

            return new OutcomeVector(first.Cells, outcomes, ValueSet.From(rows));
        }

        /// <summary>
        /// What a side is keyed by: the class of a value that is no boolean, or each truth it may give; and every truth
        /// where it may fail.
        /// </summary>
        static IEnumerable<CosmosValueClass> Operands(CellOutcome side)
        {
            var truths = (side.Outcomes & ServiceOutcomes.Fails) != 0 ? ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined
                : side.NonBoolean == CosmosValueClass.None ? side.Outcomes : ServiceOutcomes.None;
            if (side.NonBoolean != CosmosValueClass.None)
                yield return side.NonBoolean;
            if ((truths & ServiceOutcomes.True) != 0)
                yield return CosmosValueClass.True;
            if ((truths & ServiceOutcomes.False) != 0)
                yield return CosmosValueClass.False;
            if ((truths & ServiceOutcomes.Undefined) != 0)
                yield return CosmosValueClass.Undefined;
        }

    }

}
