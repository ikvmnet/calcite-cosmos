using System;
using System.Collections.Generic;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// One rule of the compiled schema: a conjunction of facts that must hold, and the one fact that
    /// follows when they do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A conjunctive body and a single-atom head is what makes the compiled schema a definite Horn
    /// theory, and that is not a stylistic choice — it is what keeps asking it a linear-time question
    /// rather than a satisfiability one. The moment a rule could conclude "A or B" the theory leaves
    /// Horn and entailment becomes intractable, so an undiscriminated <c>anyOf</c> contributes only
    /// the facts every branch agrees on rather than a disjunction. See <c>DESIGN.md</c> under <em>Atoms, clauses, and why asking is linear</em>.
    /// </para>
    /// <para>
    /// An empty body is the ordinary case: a fact the schema states unconditionally.
    /// </para>
    /// </remarks>
    /// <param name="Body">The facts that must hold, all of them.</param>
    /// <param name="Head">The fact that follows.</param>
    public sealed record CosmosFactRule(IReadOnlyList<CosmosFact> Body, CosmosFact Head)
    {

        /// <summary>
        /// Creates a rule with nothing to prove.
        /// </summary>
        /// <param name="head">The fact the schema states outright.</param>
        /// <returns>The rule.</returns>
        public static CosmosFactRule Unconditional(CosmosFact head) => new(Array.Empty<CosmosFact>(), head);

    }

}
