using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// One round of a statement's admission, as <see cref="CosmosStatementScope"/> asks its renderer: once a round, so that S
    /// can answer the round's owned conjuncts in one bundle and the translator each of the rest as before (spec §5.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// After the last round, where S admitted anything, the renderer is asked once more (<see cref="Closing"/>) which of S's
    /// admissions stand: all of them where the <c>AND</c> of them is proven, in a bundle a round opened or in one closing
    /// bundle, and none where it is not. Then the rounds run again with S admitting nothing (<see cref="Again"/>).
    /// </para>
    /// <para>
    /// <b>Its answer says S's apart</b> (<see cref="Answer"/>): the scope hands S's back as <see cref="AdmittedByS"/>, the
    /// arms of the round's junction, and voids them alone where the composite is not proven. A set of indices says neither.
    /// </para>
    /// </remarks>
    /// <param name="Number">The round, counting from one; on the closing call, one past the last round that admitted.</param>
    /// <param name="Node">The stratum the round renders from: every conjunct an earlier round admitted assumed. On the
    /// closing call, where the rounds stopped: the last round's, or the child that stopped them.</param>
    /// <param name="Pending">The conjuncts no round has admitted, in index order.</param>
    /// <param name="AdmittedByS">The conjuncts S admitted in earlier rounds, in index order: <c>Before(k)</c>'s arms.</param>
    /// <param name="NodeFor">The node a conjunct is rendered from: <paramref name="Node"/>, but where a test plants another
    /// (<see cref="CosmosOracle.RenderedFrom"/>).</param>
    internal sealed record AdmissionRound(int Number, StatementTerms.Node Node, IReadOnlyList<int> Pending, IReadOnlyList<int> AdmittedByS, Func<int, StatementTerms.Node> NodeFor)
    {

        /// <summary>Whether the round is of the rounds run again where S's composite was not proven: S admits nothing in it.</summary>
        public bool Again { get; init; }

        /// <summary>
        /// Whether this is the call after the last round, which admits nothing: its answer's <see cref="Answer.ByS"/> is
        /// every conjunct of <see cref="AdmittedByS"/> where their composite is proven, and none where it is not.
        /// </summary>
        public bool Closing { get; init; }

        /// <summary>
        /// What a round admitted: the conjuncts the translator rendered, and those S proved. A conjunct named that the round
        /// does not hold pending is not admitted, and on a round run <see cref="Again"/> nor is any of S's.
        /// </summary>
        internal sealed record Answer(IReadOnlyCollection<int> Translated, IReadOnlyCollection<int> ByS)
        {

            /// <summary>Nothing admitted.</summary>
            public static readonly Answer None = new(Array.Empty<int>(), Array.Empty<int>());

        }

    }

}
