using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Asks a translator whether it writes an expression as a predicate, for the tests that ask that alone.
    /// </summary>
    /// <remarks>
    /// The translator's own <c>TryTranslate</c> lost its last production caller when the switch (h15) took the
    /// weakening's translation to S's Rex builder, and went at review 3: a <c>WHERE</c> is translated conjunct by conjunct
    /// (<see cref="CosmosRexTranslator.TryTranslateConjunct"/>), a value by <see cref="CosmosRexTranslator.TryTranslateValue"/>,
    /// and a test asking whether a predicate's shape has a Cosmos form asks <see cref="CosmosRexTranslator.Translate"/>,
    /// as this does.
    /// </remarks>
    internal static class CosmosRexTranslatorTrying
    {

        /// <summary>
        /// Translates <paramref name="node"/> as a predicate, answering whether it has a Cosmos form.
        /// </summary>
        /// <param name="translator">The translator.</param>
        /// <param name="node">The expression to translate.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the expression was translated; otherwise <c>false</c>.</returns>
        public static bool TryTranslate(this CosmosRexTranslator translator, RexNode node, out string? expression)
        {
            try
            {
                expression = translator.Translate(node);
                return true;
            }
            catch (CosmosTranslationException)
            {
                expression = null;
                return false;
            }
        }

    }

}
