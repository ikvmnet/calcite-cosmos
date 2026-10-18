using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// What the theory of JSON proves of the documents a query keeps, given facts it established: a context of the facts
    /// beneath the declaration's, opened on the engine and remembering nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A reference for the theory's own tests, and nobody else's.</b> The planner read a filter this way before the
    /// predicate oracle, a fact extractor stating what each conjunct established. It reads every filter through the oracle
    /// now, whose strata are contexts beneath the same declaration, so the adapter keeps no second engine for what a
    /// filter establishes; what the rules of JSON and of JSON Schema prove of a set of facts is still the question the
    /// theory's tests ask, and this answers it as the planner once did.
    /// </para>
    /// <para>
    /// With nothing established it is the declaration's own context (<see cref="JsonFactTheory.Declared"/>), and so it is
    /// where the engine cannot open a context: what holds of every document holds of the ones a query keeps.
    /// </para>
    /// </remarks>
    internal static class JsonFactDerivation
    {

        static readonly RunOptions Run = new() { StopOnContradiction = true };

        /// <summary>
        /// Saturates established facts beneath the declaration.
        /// </summary>
        /// <param name="theory">The declaration.</param>
        /// <param name="established">What a query established, which may be empty.</param>
        /// <returns>What is known.</returns>
        public static JsonFactSet Derive(this JsonFactTheory theory, IEnumerable<JsonFact>? established) =>
            theory.Derive(established ?? Array.Empty<JsonFact>(), Array.Empty<JsonDocumentPath>());

        /// <summary>
        /// Saturates established facts beneath the declaration, the context told of paths beside those they speak of.
        /// </summary>
        /// <param name="theory">The declaration.</param>
        /// <param name="established">What a query established.</param>
        /// <param name="mentioned">Paths for the context to know of beside those it established, which a test asks about.</param>
        /// <returns>What is known.</returns>
        public static JsonFactSet Derive(this JsonFactTheory theory, IEnumerable<JsonFact> established, IEnumerable<JsonDocumentPath> mentioned)
        {
            var facts = established.Distinct().ToArray();
            if (facts.Length == 0 || theory.Declaration() is not JsonFactDeclaration declaration)
                return theory.Declared;

            var contexts = declaration.Generation.Contexts;
            var encoded = new List<Apache.Calcite.Cosmos.Facts.Saturation.JsonFact>();
            var paths = new HashSet<JsonPathSpec>(mentioned.Select(JsonFactEncoding.Path));

            foreach (var fact in facts)
            {
                paths.Add(JsonFactEncoding.Path(fact.Path));
                encoded.AddRange(JsonFactEncoding.Statement(fact, declaration.Vocabulary, register: false));
            }

            try
            {
                var scope = contexts.Open(declaration.Context, encoded, paths, rules: null, split: true, options: Run);
                return new JsonFactSet(contexts.Rules, declaration.Vocabulary, scope, owned: true);
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                return theory.Declared;
            }
        }

    }

}
