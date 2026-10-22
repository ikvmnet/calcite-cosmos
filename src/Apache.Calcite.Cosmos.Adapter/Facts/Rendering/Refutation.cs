using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where a claim fails: the cells a counterexample sets its paths to, each <c>path=cell</c>, the class of the form it
    /// refutes on that document, and the rows both sides read there.
    /// </summary>
    /// <remarks>
    /// Packing only: the gate decided the class refutes, and this names the least cell of it, so a refutation is one value
    /// whichever cell is found first. Ordered by its cells' count, then its text.
    /// </remarks>
    internal sealed record Refutation(ValueSet<string> Cells, CellClass Class, ValueSet<string> Rows) : IComparable<Refutation>
    {

        /// <summary>The least cell whose class is among the refuting ones; they must meet the cells'.</summary>
        public static Refutation Of(CellOutcomes outcomes, ValueSet<CellClass> refuting)
        {
            var at = outcomes.Cells.First(c => refuting.Contains(c.Class));
            return new(ValueSet.Of(outcomes.Path + "=" + at.Cell.Text), at.Class, outcomes.Rows);
        }

        /// <summary>
        /// A refutation of one side of a junction, its sibling set where the sibling is the connective's identity, so that
        /// the junction's class there is the side's.
        /// </summary>
        public static Refutation With(Refutation child, Witness sibling) =>
            child with { Cells = child.Cells.Add(sibling.Path + "=" + sibling.Cell.Text) };

        /// <summary>
        /// A refutation beneath a <c>NOT</c>, which takes its operand's class negated: TRUE and FALSE swapped on both sides, as
        /// Kleene's <c>NOT</c> and the licence S composed through it hold of each.
        /// </summary>
        public static Refutation Negated(Refutation operand) => operand with
        {
            Class = new(PredicateAtom.Swap(operand.Class.Calcite), (operand.Class.Service & (ServiceOutcomes.Undefined | ServiceOutcomes.Fails))
                | ((operand.Class.Service & ServiceOutcomes.True) != 0 ? ServiceOutcomes.False : 0) | ((operand.Class.Service & ServiceOutcomes.False) != 0 ? ServiceOutcomes.True : 0)),
        };

        /// <inheritdoc />
        public int CompareTo(Refutation? other) => other is null ? 1
            : Cells.Count != other.Cells.Count ? Cells.Count.CompareTo(other.Cells.Count)
            : string.CompareOrdinal(Text, other.Text);

        string Text => string.Join("\n", Cells.AsEnumerable()) + "\n" + Class + "\n" + string.Join("\n", Rows.AsEnumerable());

    }

}
