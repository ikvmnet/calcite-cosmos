using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Holds <see cref="CosmosRequestUnitModel"/> to the charges it was fitted against, and to the
    /// orderings it exists to produce.
    /// </summary>
    /// <remarks>
    /// The measured figures come from <c>CosmosPointReadResidualMeasurementTests</c> against a real
    /// serverless account. They are reproduced here as constants so the model can be checked without
    /// an account: if the service reprices and the measurement moves, these move with it, and the
    /// orderings below are what must survive either way.
    /// </remarks>
    [TestClass]
    public class CosmosRequestUnitModelTests
    {

        /// <summary>The documents the measurement seeded, ~200 bytes each.</summary>
        const double SmallDocument = 200d;

        /// <summary>The padded document the measurement seeded.</summary>
        const double LargeDocument = 100d * 1024d;

        /// <summary>How far a fitted estimate may sit from the charge it was fitted to.</summary>
        const double Tolerance = 0.9d;

        [TestMethod]
        public void TheModelReproducesTheMeasuredPointReadCharges()
        {
            CosmosRequestUnitModel.PointRead(SmallDocument).Should().BeApproximately(1.00d, Tolerance);
            CosmosRequestUnitModel.PointRead(LargeDocument).Should().BeApproximately(9.95d, Tolerance);
        }

        [TestMethod]
        public void TheModelReproducesTheMeasuredQueryCharges()
        {
            // One small document returned, partition key pinned.
            CosmosRequestUnitModel.Query(1, SmallDocument).Should().BeApproximately(3.02d, Tolerance);

            // The same statement whose residual rejects: the floor, and nothing else.
            CosmosRequestUnitModel.Query(0, SmallDocument).Should().BeApproximately(2.99d, Tolerance);

            // One large document returned — the regime a single-term model misses.
            CosmosRequestUnitModel.Query(1, LargeDocument).Should().BeApproximately(4.51d, Tolerance);

            // Sixty-four small documents returned, which is what the 128-id set query returns.
            CosmosRequestUnitModel.Query(64, SmallDocument).Should().BeApproximately(6.79d, Tolerance);
        }

        /// <summary>
        /// The batch fit, over the range it claims: two to thirty-two documents.
        /// </summary>
        [TestMethod]
        public void TheModelReproducesTheMeasuredBatchReadCharges()
        {
            // One document is charged as the point read it is, which is the step the fit must not smooth
            // over — the crossing against a query happens between one document and two.
            CosmosRequestUnitModel.ReadMany(1, SmallDocument).Should().BeApproximately(1.00d, Tolerance);

            CosmosRequestUnitModel.ReadMany(2, SmallDocument).Should().BeApproximately(3.14d, Tolerance);
            CosmosRequestUnitModel.ReadMany(4, SmallDocument).Should().BeApproximately(3.59d, Tolerance);
            CosmosRequestUnitModel.ReadMany(8, SmallDocument).Should().BeApproximately(4.48d, Tolerance);
            CosmosRequestUnitModel.ReadMany(16, SmallDocument).Should().BeApproximately(6.27d, Tolerance);
            CosmosRequestUnitModel.ReadMany(32, SmallDocument).Should().BeApproximately(9.89d, Tolerance);
        }

        /// <summary>
        /// Beyond the fitted range the straight line underestimates, and this pins the direction — it
        /// flatters the batch rather than the query, which is the error that cannot flip an ordering
        /// the model is already deciding by fourfold.
        /// </summary>
        [TestMethod]
        public void TheBatchFitUnderestimatesBeyondItsRange()
        {
            CosmosRequestUnitModel.ReadMany(64, SmallDocument).Should().BeLessThan(18.35d);
            CosmosRequestUnitModel.ReadMany(128, SmallDocument).Should().BeLessThan(36.95d);

            // And still orders the decision correctly despite it.
            CosmosRequestUnitModel.ReadMany(128, SmallDocument)
                .Should().BeGreaterThan(CosmosRequestUnitModel.Query(64, SmallDocument) * 3d);
        }

        /// <summary>
        /// The ordering issue #92 turns on, for the shape that motivated it.
        /// </summary>
        [TestMethod]
        public void APointReadBeatsTheQueryItReplacesForASmallDocument()
        {
            var read = CosmosRequestUnitModel.PointRead(SmallDocument);
            var query = CosmosRequestUnitModel.Query(1, SmallDocument);

            read.Should().BeLessThan(query, "the read does not pay the query floor");
            (query / read).Should().BeGreaterThan(2d, "the measured advantage is about threefold");
        }

        /// <summary>
        /// And the ordering that reverses it, which is the reason the decision is not a constant.
        /// </summary>
        [TestMethod]
        public void AQueryBeatsThePointReadForALargeDocument()
        {
            var read = CosmosRequestUnitModel.PointRead(LargeDocument);
            var query = CosmosRequestUnitModel.Query(1, LargeDocument);

            read.Should().BeGreaterThan(query, "a point read is charged for the body at roughly six times the query's rate");
        }

        [TestMethod]
        public void TheBreakEvenSitsWhereTheMeasuredCurvesCross()
        {
            var breakEven = CosmosRequestUnitModel.BreakEvenDocumentSizeInBytes;

            breakEven.Should().BeInRange(20d * 1024d, 32d * 1024d, "the fitted crossing is about 26 KB");

            // Either side of it, the ordering is the one the break-even claims.
            CosmosRequestUnitModel.PointRead(breakEven * 0.5)
                .Should().BeLessThan(CosmosRequestUnitModel.Query(1, breakEven * 0.5));

            CosmosRequestUnitModel.PointRead(breakEven * 2d)
                .Should().BeGreaterThan(CosmosRequestUnitModel.Query(1, breakEven * 2d));
        }

        /// <summary>
        /// The set leg: a batch read wins only at one document, which is the single read's case.
        /// </summary>
        [TestMethod]
        public void ABatchReadLosesToOneQueryBeyondASingleDocument()
        {
            // At one id the batch is a point read, and wins.
            CosmosRequestUnitModel.ReadMany(1, SmallDocument)
                .Should().BeLessThan(CosmosRequestUnitModel.Query(1, SmallDocument));

            // At every size above it the query wins, and by more as the set grows. Half the documents
            // are soft-deleted in the measured shape, so the query returns half of what the batch reads.
            foreach (var n in new[] { 2, 4, 8, 16, 32, 64, 128 })
            {
                var batch = CosmosRequestUnitModel.ReadMany(n, SmallDocument);
                var query = CosmosRequestUnitModel.Query(n / 2d, SmallDocument);

                batch.Should().BeGreaterThan(query, $"the batch pays per document at N={n}");
            }

            // And the gap widens rather than closing.
            var small = CosmosRequestUnitModel.ReadMany(8, SmallDocument) / CosmosRequestUnitModel.Query(4, SmallDocument);
            var large = CosmosRequestUnitModel.ReadMany(128, SmallDocument) / CosmosRequestUnitModel.Query(64, SmallDocument);

            large.Should().BeGreaterThan(small);
        }

        [TestMethod]
        public void AFanOutMultipliesTheFloorRatherThanWhatIsReturned()
        {
            var pinned = CosmosRequestUnitModel.Query(1, SmallDocument);
            var fanned = CosmosRequestUnitModel.Query(1, SmallDocument, partitionsContacted: 4);

            fanned.Should().BeGreaterThan(pinned, "each partition pays the floor");
            fanned.Should().BeLessThan(pinned * 5d, "what is returned is not multiplied with it");
        }

        [TestMethod]
        public void ARatioIsOneWhereTheReferenceIsNotUsable()
        {
            CosmosRequestUnitModel.RelativeTo(5d, 0d).Should().Be(1d);
            CosmosRequestUnitModel.RelativeTo(5d, double.NaN).Should().Be(1d);
            CosmosRequestUnitModel.RelativeTo(1d, 4d).Should().Be(0.25d);
        }

        [TestMethod]
        public void AnUnknownDocumentSizeCostsAsTheFloorAlone()
        {
            CosmosRequestUnitModel.PointRead(0d).Should().Be(CosmosRequestUnitModel.PointReadFloor);
            CosmosRequestUnitModel.AverageDocumentSizeInBytes(null).Should().Be(0d);
        }

    }

}
