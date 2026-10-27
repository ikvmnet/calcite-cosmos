using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// One round of a statement's admission, as <see cref="CosmosStatementScope"/> asks its renderer: once a round, so that S
    /// can answer the round's owned conjuncts in one bundle and the translator each of the rest as before (spec §5.2;
    /// <c>DESIGN.md</c>, <i>Admission asked once a round</i>).
    /// </summary>
    /// <param name="Number">The round, counting from one; on the closing call, one past the last round that admitted.</param>
    /// <param name="Node">The stratum the round renders from: every conjunct an earlier round admitted assumed. On the
    /// closing call, where the rounds stopped: the last round's, or the child that stopped them.</param>
    /// <param name="Pending">The conjuncts no round has admitted, in index order.</param>
    /// <param name="NodeFor">The node a conjunct is rendered from: <paramref name="Node"/>, but where a test plants another
    /// (<see cref="CosmosOracle.RenderedFrom"/>).</param>
    internal sealed record AdmissionRound(int Number, StatementTerms.Node Node, IReadOnlyList<int> Pending, Func<int, StatementTerms.Node> NodeFor)
    {

        /// <summary>Whether the round is of the rounds run again where S's composite was not proven: S admits nothing in it.</summary>
        public bool Again { get; init; }

        /// <summary>
        /// Whether this is the call after the last round, made where S admitted anything, which admits nothing: its answer's
        /// <see cref="Answer.ByS"/> is every conjunct S admitted where their composite is proven, and none where it is not.
        /// </summary>
        public bool Closing { get; init; }

        /// <summary>
        /// What a round admitted: the conjuncts the translator rendered, and those S proved, apart, so that S's alone are
        /// voided where their composite is not proven. A conjunct named that the round does not hold pending is not
        /// admitted, and on a round run <see cref="Again"/> nor is any of S's.
        /// </summary>
        internal sealed record Answer(IReadOnlyCollection<int> Translated, IReadOnlyCollection<int> ByS);

    }

}
