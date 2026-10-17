using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A conjunct of a <c>WHERE</c> holding a comparison a parameter's value decides, as the reader rechecks it: its
    /// <c>AND</c>s and <c>OR</c>s, and at its leaves those comparisons and the predicates the service answers exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A comparison need not be the whole conjunct.</b> Where its parameter is no anchor the statement is sent with
    /// the comparison weakened to a bound every string Calcite keeps satisfies (<see cref="CosmosOrderComparison"/>), and
    /// a weaker leaf beneath <c>AND</c>s and <c>OR</c>s alone makes a weaker conjunct, each being monotone in its
    /// operands. So the rows the service returns are a superset of the conjunct's, beside the other conjuncts it
    /// applies, and the reader keeps those the conjunct as written keeps: the keyset seek past two keys, <c>s &gt; ? OR
    /// (s = ? AND id &gt; ?)</c>, which chapter 9.5 recommends, is one. Under a <c>NOT</c>, a <c>CASE</c> or a function
    /// a weaker leaf makes nothing weaker, and the comparison is refused there.
    /// </para>
    /// <para>
    /// <b>Only truth is asked.</b> A <c>WHERE</c> keeps a row where its condition is true, and through <c>AND</c> and
    /// <c>OR</c> alone the conjunct is true exactly where its leaves' truths make it so: an unknown leaf and a false one
    /// count alike. So a predicate leaf the service answers exactly is read back as the value it selects —
    /// true, false, or left out where undefined — and only <c>true</c> counts; a comparison leaf is made from the path's
    /// stored value, a string compared by UTF-16 code unit and anything else unknown, as Calcite reads text.
    /// </para>
    /// </remarks>
    /// <param name="Kind">What the node is.</param>
    /// <param name="Operands">An <c>AND</c>'s or an <c>OR</c>'s operands; empty for a leaf.</param>
    /// <param name="Comparison">For a comparison leaf, its index among the program's comparisons; otherwise -1.</param>
    /// <param name="Expression">For a predicate leaf, the predicate as the statement writes it; otherwise <c>null</c>.</param>
    /// <param name="Member">For a predicate leaf, the member of a returned row the statement selects it under; assigned
    /// when the statement is built, and <c>null</c> until then.</param>
    public sealed record CosmosRecheck(CosmosRecheckKind Kind, IReadOnlyList<CosmosRecheck> Operands, int Comparison, string? Expression, string? Member)
    {

        /// <summary>
        /// Returns an <c>AND</c> or an <c>OR</c> of rechecked operands.
        /// </summary>
        /// <param name="all">Whether it is an <c>AND</c>.</param>
        /// <param name="operands">The operands.</param>
        /// <returns>The node.</returns>
        public static CosmosRecheck Of(bool all, IReadOnlyList<CosmosRecheck> operands) =>
            new(all ? CosmosRecheckKind.All : CosmosRecheckKind.Any, operands ?? throw new ArgumentNullException(nameof(operands)), -1, null, null);

        /// <summary>
        /// Returns a leaf the reader compares itself.
        /// </summary>
        /// <param name="comparison">The comparison's index among the program's.</param>
        /// <returns>The node.</returns>
        public static CosmosRecheck Compared(int comparison) =>
            new(CosmosRecheckKind.Comparison, Array.Empty<CosmosRecheck>(), comparison, null, null);

        /// <summary>
        /// Returns a leaf the service answers, read back from a member.
        /// </summary>
        /// <param name="expression">The predicate as the statement writes it.</param>
        /// <returns>The node.</returns>
        public static CosmosRecheck Answered(string expression) =>
            new(CosmosRecheckKind.Predicate, Array.Empty<CosmosRecheck>(), -1, expression ?? throw new ArgumentNullException(nameof(expression)), null);

    }

}
