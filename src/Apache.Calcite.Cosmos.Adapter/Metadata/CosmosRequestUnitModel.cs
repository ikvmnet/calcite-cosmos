using System;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Prices the routes a statement can take in request units, so that alternatives which differ in
    /// mechanism rather than in shape can be ranked against one another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calcite costs in abstract units and the adapter has until now discounted them by a constant —
    /// pushing down is cheaper, by however much <c>CosmosConvention.CostMultiplier</c> says. That is
    /// enough to prefer a pushed plan to an unpushed one and not enough to choose between two pushed
    /// plans that reach the same rows by different mechanisms, which is what a point read against a
    /// query is.
    /// </para>
    /// <para>
    /// <b>These coefficients are measured, not posited.</b> They were fitted against a real serverless
    /// account by <c>CosmosPointReadResidualMeasurementTests</c>, whose charges reproduced exactly
    /// across runs. The three forms and their fits:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A point read costs <c>1.00 RU</c> for a small document and <c>9.95 RU</c> for one of ~100 KB —
    /// a floor of one and about <c>0.090 RU</c> per kilobyte of body.
    /// </description></item>
    /// <item><description>
    /// A query costs a floor near <c>2.90 RU</c>, plus about <c>0.058 RU</c> per document returned and
    /// <c>0.0155 RU</c> per kilobyte returned. Both terms are needed: one large document and sixty-four
    /// small ones are different regimes, and a model with only one of them fits one and misses the
    /// other. A query that returns nothing pays the floor and nothing else — the scan is not charged by
    /// size, only the output is.
    /// </description></item>
    /// <item><description>
    /// A batch read costs about <c>2.69 RU</c> plus <c>0.225 RU</c> per document. At one document it is
    /// not a batch at all and is charged as the point read it is, which is the largest single step in
    /// the measured table.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The consequence worth naming</b> is that a point read is charged roughly six times per
    /// kilobyte what a query is charged for returning the same document. That is not guessable, and it
    /// is why the cheapest route depends on document size rather than on the shape of the predicate:
    /// the point read's advantage is a constant — the query floor it does not pay — and a large enough
    /// body swamps a constant. The two meet at <see cref="BreakEvenDocumentSizeInBytes"/>.
    /// </para>
    /// <para>
    /// <b>What the numbers are allowed to be.</b> The planner ranks plans; it does not report a bill. An
    /// estimate wrong by a constant factor but right in its ordering is worth as much as an accurate
    /// one, so these are used as ratios — see <see cref="RelativeTo"/> — and never as an absolute.
    /// Expressing the model as a ratio is also what keeps it comparable with the rest of Calcite's
    /// costing, which is in abstract units: a ratio has no units to disagree about.
    /// </para>
    /// <para>
    /// <b>Where the batch fit stops holding.</b> It is taken over two to thirty-two documents, where
    /// its residuals are under <c>0.02 RU</c>. Past that the measured charge bends upward — the marginal
    /// cost rises from <c>0.225</c> to about <c>0.29 RU</c> per document by 128, most likely paging —
    /// and the straight line underestimates, by <c>5.5 RU</c> at 128. That is the harmless direction:
    /// it flatters the batch, and the batch has already lost to a query by fourfold there, so no
    /// ordering turns on it. The range that <em>is</em> fitted tightly is the one where the two curves
    /// actually cross, which is between one document and two.
    /// </para>
    /// <para>
    /// <b>What was not measured, and is therefore a guess.</b> The fan-out multiplier is reasoned rather
    /// than fitted — a serverless account holds one physical partition, so there was no fan-out to
    /// price. A batch read's dependence on document size is likewise unmeasured; every document in the
    /// batch was small. Both are flagged where they are applied.
    /// </para>
    /// </remarks>
    public static class CosmosRequestUnitModel
    {

        /// <summary>Bytes in the kilobyte the service bills by.</summary>
        const double BytesPerKilobyte = 1024d;

        /// <summary>What a point read costs before its body is counted.</summary>
        public const double PointReadFloor = 1.00d;

        /// <summary>What a point read costs per kilobyte of document body.</summary>
        public const double PointReadPerKilobyte = 0.090d;

        /// <summary>What a query costs before anything it returns is counted.</summary>
        public const double QueryFloor = 2.90d;

        /// <summary>What a query costs per document returned.</summary>
        public const double QueryPerDocumentReturned = 0.058d;

        /// <summary>What a query costs per kilobyte returned.</summary>
        public const double QueryPerKilobyteReturned = 0.0155d;

        /// <summary>What a batch read costs before its documents are counted.</summary>
        public const double ReadManyFloor = 2.69d;

        /// <summary>What a batch read costs per document, fitted over two to thirty-two documents.</summary>
        public const double ReadManyPerDocument = 0.225d;

        /// <summary>
        /// Returns what a point read of a document of the given size costs.
        /// </summary>
        /// <remarks>
        /// A point read applies no predicate and returns the document whole, so its size is the whole
        /// story beyond the floor. A projection does not reduce it: the document crosses the wire either
        /// way and the paths are read out of it afterwards.
        /// </remarks>
        /// <param name="documentSizeInBytes">The document's size, or zero where it is unknown.</param>
        /// <returns>The estimated charge in request units.</returns>
        public static double PointRead(double documentSizeInBytes)
        {
            return PointReadFloor + PointReadPerKilobyte * Kilobytes(documentSizeInBytes);
        }

        /// <summary>
        /// Returns what a batch read of the given number of documents costs.
        /// </summary>
        /// <remarks>
        /// One document is not a batch — the service charges it as the point read it is, at
        /// <see cref="PointReadFloor"/> rather than <see cref="ReadManyFloor"/>, and the discontinuity
        /// between one and two documents is the largest single step in the measured table. Beyond one,
        /// the per-document term is what dominates, and it is what makes a batch lose to a single query
        /// at every size measured above one.
        /// </remarks>
        /// <param name="count">How many documents the batch reads.</param>
        /// <param name="documentSizeInBytes">Each document's size, or zero where it is unknown.</param>
        /// <returns>The estimated charge in request units.</returns>
        public static double ReadMany(int count, double documentSizeInBytes)
        {
            if (count <= 0)
                return 0d;

            if (count == 1)
                return PointRead(documentSizeInBytes);

            // The size term is the point read's, applied per document. Unmeasured — every document in
            // the batch that was measured was small — but a batch cannot plausibly move a body for less
            // than a point read does, so this is a floor on the size term rather than a fit.
            return ReadManyFloor + ReadManyPerDocument * count + PointReadPerKilobyte * Kilobytes(documentSizeInBytes) * count;
        }

        /// <summary>
        /// Returns what a query returning the given number of documents costs.
        /// </summary>
        /// <param name="documentsReturned">How many documents the query is expected to return.</param>
        /// <param name="documentSizeInBytes">The size of a returned document, or zero where it is unknown.</param>
        /// <param name="partitionsContacted">
        /// How many physical partitions the query reaches. One where the partition key is pinned.
        /// </param>
        /// <returns>The estimated charge in request units.</returns>
        public static double Query(double documentsReturned, double documentSizeInBytes, int partitionsContacted = 1)
        {
            if (documentsReturned < 0)
                documentsReturned = 0;

            var returned = QueryPerDocumentReturned * documentsReturned +
                QueryPerKilobyteReturned * Kilobytes(documentSizeInBytes) * documentsReturned;

            // Each partition runs the statement and pays the floor; what they return is divided among
            // them rather than multiplied. Reasoned, not measured: see the remarks on the class.
            var contacted = partitionsContacted < 1 ? 1 : partitionsContacted;

            return QueryFloor * contacted + returned;
        }

        /// <summary>
        /// The document size at which a point read and the single-document query it would replace cost
        /// the same, for a statement that returns the whole document.
        /// </summary>
        /// <remarks>
        /// Below it the point read wins, by up to the 2 RU query floor; above it the query wins, without
        /// bound, because only the read's charge grows with the body. It is about 26 KB, which is large
        /// for a document and small for a blob — so the answer is genuinely container-dependent rather
        /// than one side always being right.
        /// </remarks>
        public static double BreakEvenDocumentSizeInBytes
        {
            get
            {
                // PointReadFloor + a·kb = QueryFloor + QueryPerDocumentReturned + b·kb
                var numerator = QueryFloor + QueryPerDocumentReturned - PointReadFloor;
                var slope = PointReadPerKilobyte - QueryPerKilobyteReturned;

                return numerator / slope * BytesPerKilobyte;
            }
        }

        /// <summary>
        /// Expresses one estimate as a multiplier against another, which is the form the planner can
        /// use.
        /// </summary>
        /// <remarks>
        /// Cosmos charges in request units and Calcite costs in abstract ones, and converting between
        /// them is a guess that would be wrong with confidence. A ratio avoids the question: it says
        /// this route costs a third of that one, which is true in either currency.
        /// </remarks>
        /// <param name="requestUnits">The route being priced.</param>
        /// <param name="referenceRequestUnits">The route it is being priced against.</param>
        /// <returns>The multiplier, or one where the reference is not positive.</returns>
        public static double RelativeTo(double requestUnits, double referenceRequestUnits)
        {
            if (referenceRequestUnits <= 0d || double.IsNaN(referenceRequestUnits))
                return 1d;

            return requestUnits / referenceRequestUnits;
        }

        /// <summary>
        /// Returns the average size of a document in the container, or zero where the service was not
        /// asked.
        /// </summary>
        /// <remarks>
        /// A container average standing in for one document's size. Sound where documents are alike,
        /// which the shapes this is used for — a view over a container of records — generally are; wrong
        /// where a container mixes small records with occasional large bodies, and wrong there in the
        /// direction that costs the most. There is nothing better available without sampling documents,
        /// which the adapter does not do.
        /// </remarks>
        /// <param name="container">The container being read.</param>
        /// <returns>The average document size in bytes, or zero.</returns>
        public static double AverageDocumentSizeInBytes(CosmosContainerMetadata? container)
        {
            return container?.Statistics is CosmosContainerStatistics statistics
                ? statistics.AverageDocumentSizeInBytes
                : 0d;
        }

        static double Kilobytes(double bytes)
        {
            return bytes <= 0d || double.IsNaN(bytes) ? 0d : bytes / BytesPerKilobyte;
        }

    }

}
