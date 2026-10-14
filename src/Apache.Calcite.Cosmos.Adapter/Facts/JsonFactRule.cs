using System;
using System.Collections.Generic;
using System.Threading;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// One rule of the compiled schema: a conjunction of facts that must hold, and the one fact that
    /// follows when they do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A conjunctive body and a single-atom head. The theory holds a rule with a body as a row, read by
    /// a fixed interpreter, so a query costs the rules it fires rather than the rules there are; and it
    /// reasons about a disjunction by splitting on a discriminator rather than by a disjunctive head, so
    /// an undiscriminated <c>anyOf</c> contributes only the facts every branch agrees on. See
    /// <c>DESIGN.md</c> under <em>A conditional rule is a row</em>.
    /// </para>
    /// <para>
    /// An empty body is the ordinary case: a fact the schema states unconditionally.
    /// </para>
    /// </remarks>
    /// <param name="Body">The facts that must hold, all of them.</param>
    /// <param name="Head">The fact that follows.</param>
    public sealed record JsonFactRule(IReadOnlyList<JsonFact> Body, JsonFact Head)
    {

        /// <summary>
        /// Creates a rule with nothing to prove.
        /// </summary>
        /// <param name="head">The fact the schema states outright.</param>
        /// <returns>The rule.</returns>
        public static JsonFactRule Unconditional(JsonFact head) => new(Array.Empty<JsonFact>(), head);

    }

}
