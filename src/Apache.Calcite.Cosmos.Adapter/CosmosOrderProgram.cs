using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// What the reader of a statement does to complete a proof the plan could not: the order it checks as the rows
    /// arrive, the page it takes itself where it must, the comparisons a parameter's value decides, the extremes it
    /// checks, and the fallback statement it reads where a check fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One program, carried by the statement it completes.</b> Where <see cref="Metadata.CosmosServiceOrder"/> answers
    /// <see cref="Metadata.CosmosOrderVerdictKind.Anchored"/>, the node that asked records what the reader needs —
    /// a sort its keys, an aggregate its extremes, a translator its comparisons — and
    /// <see cref="CosmosImplementor.Build"/> assembles them here beside the statement, selecting each value the reader
    /// checks under a member no column has. <see cref="Client.CosmosCursors"/> hands a statement carrying one to
    /// <c>Client.CosmosOrderedCursor</c> instead of reading it as it comes. A statement whose every order is proved when
    /// the plan is made carries none, and is read as it always was.
    /// </para>
    /// <para>
    /// <b>Every statement it sends is read by one iterator, start to end.</b> Measured on the probe account across two
    /// physical partitions, a cross-partition <c>ORDER BY</c> resumed from a continuation token in a new iterator lost
    /// and repeated rows, which no check of the rows that did arrive can see; read through one iterator, every stream
    /// and every window held the certificate. So the fallback is a statement of its own, read from its start, and
    /// nothing resumes another.
    /// </para>
    /// </remarks>
    /// <param name="Keys">The keys of an anchored sort, every key written, in order; empty where the statement's order is
    /// proved, or it has none.</param>
    /// <param name="Distinct">Whether the rows are a <c>DISTINCT</c>'s, which the service may return a value of twice
    /// across partitions where its order and Calcite's part.</param>
    /// <param name="Offset">The statement's <c>OFFSET</c>, which the reader applies itself to what it reads unpaged.</param>
    /// <param name="Fetch">The statement's <c>LIMIT</c>, likewise.</param>
    /// <param name="Unpaged">The statement without its <c>OFFSET</c> and <c>LIMIT</c>: the fallback, with each
    /// comparison in <paramref name="Comparisons"/> whose parameter is no anchor weakened in it.</param>
    /// <param name="Comparisons">The comparisons a parameter's value decides.</param>
    /// <param name="Rechecks">The conjuncts of the <c>WHERE</c> holding them, which the reader rechecks whole wherever
    /// it reads the fallback.</param>
    /// <param name="Extremes">The extremes of an aggregate to check.</param>
    public sealed record CosmosOrderProgram(
        IReadOnlyList<CosmosOrderedKey> Keys,
        bool Distinct,
        CosmosRowLimit? Offset,
        CosmosRowLimit? Fetch,
        string Unpaged,
        IReadOnlyList<CosmosOrderComparison> Comparisons,
        IReadOnlyList<CosmosRecheck> Rechecks,
        IReadOnlyList<CosmosOrderExtreme> Extremes)
    {

        /// <summary>
        /// Gets the name the reader reports it under, on the <c>cosmos.order.program</c> tag: <c>page</c> for an anchored
        /// sort under a page, <c>sort</c> for one without, <c>extreme</c> for anchored extremes, and <c>comparison</c> for
        /// a statement whose only check is of a parameter.
        /// </summary>
        public string Name => Keys.Count > 0
            ? Offset is not null || Fetch is not null ? "page" : "sort"
            : Extremes.Count > 0 ? "extreme" : "comparison";

    }

}
