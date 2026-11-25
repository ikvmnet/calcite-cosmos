using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The evaluator a <c>Calcite</c> row says reads a reading's rows (<see cref="CosmosAxiomRows.ReadCalciteEvaluators"/>,
    /// G7): which of the evaluators evaluates an atom of the reading on a cell, and the SQL type it evaluates a value of where
    /// it takes one. The reading rests on the row that names it.
    /// </summary>
    /// <param name="Reader">The reading, by the name a reader takes (<c>PredicateAtom.TextReader</c>, <c>PredicateAtom.TypedReader</c>).</param>
    /// <param name="Evaluator">The evaluator.</param>
    /// <param name="Type">The SQL type it evaluates a value of: a typed reading's or a parse's; empty for every other evaluator.</param>
    internal sealed record CosmosCalciteEvaluator(string Reader, CalciteEvaluator Evaluator, string Type = "");

}
