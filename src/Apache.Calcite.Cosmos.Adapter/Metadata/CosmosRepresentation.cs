using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// A stored textual form, and — the part that matters — which relations comparing that form
    /// preserves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious model is that a path "is a UUID" or "is a date-time", and it is wrong. A date-time
    /// written at mixed precision has one spelling per value, so equality agrees for all of them,
    /// while <c>'…:56.5Z'</c> sorts before <c>'…:56Z'</c> and the order does not — and an unpadded
    /// integer fails the same way, <c>'9'</c> sorting after <c>'42'</c>. One fixed ISO-8601 UTC shape
    /// preserves both. So the two properties are independent and a representation has to carry them
    /// separately, or a sort gets pushed that returns the wrong rows. Measured; see <c>DESIGN.md</c>
    /// under <em>A fact says which relations it preserves</em>.
    /// </para>
    /// <para>
    /// Both are claims about the <em>stored</em> string against the <em>logical</em> value Calcite
    /// compares. Neither is a claim about the service, which compares strings lexically whatever is in
    /// them.
    /// </para>
    /// </remarks>
    /// <param name="Name">A stable name for the form, for diagnostics and for equality.</param>
    /// <param name="PreservesEquality">
    /// Whether one logical value has exactly one stored spelling, so that comparing the stored strings
    /// for equality answers what comparing the values answers. Licenses <c>=</c>, <c>&lt;&gt;</c>,
    /// <c>IN</c>, <c>DISTINCT</c>, <c>GROUP BY</c>, a join key, partition routing and the point read.
    /// </param>
    /// <param name="PreservesOrder">
    /// Whether the lexical order of the stored strings is the order Calcite compares the values in.
    /// Licenses the range comparisons, <c>ORDER BY</c>, <c>MIN</c>, <c>MAX</c>, and a <c>LIMIT</c>
    /// pushed beneath a sort. Implies <see cref="PreservesEquality"/> is worth nothing on its own —
    /// the two are set independently.
    /// </param>
    /// <param name="Width">
    /// The number of characters every stored spelling occupies, where the form fixes one, and
    /// <c>null</c> where it does not. Only a form that pins the width can say what a value looks like
    /// stored — <c>42</c> is <c>00042</c> at width five — so a rewrite rendering a literal into the
    /// stored shape has to read it, and a form without one renders the value as it stands.
    /// </param>
    public readonly record struct CosmosRepresentation(string Name, bool PreservesEquality, bool PreservesOrder, int? Width = null);

}
