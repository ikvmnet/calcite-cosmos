using System;
using System.Linq.Expressions;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.Enumerable;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.runtime;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Relational operator that converts a tree of <see cref="CosmosConvention"/> nodes into a
    /// <see cref="ClrEnumerableConvention"/> result by executing the generated Cosmos SQL against the
    /// container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only way out of the Cosmos convention, and the point at which a pushed-down subtree stops being
    /// a statement and becomes rows. Everything below it contributed to one Cosmos SQL statement; this
    /// renders that statement, executes it, and reads the JSON value each row arrives as into the row the
    /// plan above expects.
    /// </para>
    /// <para>
    /// <b>The awaiting body is the real one, and the pulled body is a bridge over it.</b> The v3 Cosmos
    /// SDK exposes no synchronous data-plane API — a page of results arrives only by awaiting
    /// <c>FeedIterator.ReadNextAsync</c> — so there is nothing for <see cref="Implement"/> to call that
    /// does not wait on a page. It is written as the delegation
    /// <see cref="ClrEnumerableRelImplementor.Pulled"/> exists for, which is the shape
    /// <see cref="ClrEnumerableRel"/> prescribes for an adapter whose client is asynchronous.
    /// </para>
    /// <para>
    /// <b>What that costs is a thread per row, and it is now a caller's choice rather than a refusal.</b>
    /// While the two conventions were separate, the absence of a converter into the pulled one was what
    /// kept sync-over-async out of a plan: a query over a Cosmos table simply did not plan unless the root
    /// was asked for asynchronously. One convention leaves no such gate — the plan is the same either way
    /// and the kind is chosen by whoever calls the root — so the cost has moved from a plan that does not
    /// exist to a thread that blocks at the leaf. A host that reads a Cosmos table through
    /// <see cref="ClrEnumerableRelImplementor.ImplementRoot"/> rather than
    /// <see cref="ClrEnumerableRelImplementor.ImplementRootAsync"/> pays it, and pays it per row.
    /// </para>
    /// <para>
    /// Rendering the statement, resolving the partition key and building the row builder all happen here,
    /// once, while the statement is prepared. Only the sequence and the row builder are on the per-row
    /// path.
    /// </para>
    /// </remarks>
    public class CosmosToClrEnumerableConverter : ConverterImpl, ClrEnumerableRel
    {

        static readonly System.Reflection.MethodInfo ReadAsyncMethod = typeof(CosmosSequences).GetMethod(nameof(CosmosSequences.ReadAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosSequences.ReadAsync)}' is missing from {nameof(CosmosSequences)}.");

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traits">The trait set, which must carry the CLR convention.</param>
        /// <param name="input">The Cosmos subtree being converted.</param>
        public CosmosToClrEnumerableConverter(RelOptCluster cluster, RelTraitSet traits, RelNode input) :
            base(cluster, ConventionTraitDef.INSTANCE, traits, input)
        {

        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosToClrEnumerableConverter(getCluster(), traitSet, (RelNode)sole(inputs));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <b>This node is the wire, so what it costs is how much crosses it.</b> Until that was
        /// priced the cost was rows alone, and a projection pushed below this node reduced nothing the
        /// model could measure while adding the node that pushed it — so a mixed projection was always
        /// cheaper left whole and in process, and <see cref="CosmosProjectSplitRule"/> produced a plan
        /// the planner then discarded every time (#125).
        /// </para>
        /// <para>
        /// <b>Width is values, not columns, and the whole difference is the document.</b> The
        /// <c>DOC</c> column carries an entire item where every other column carries a value, and
        /// counting both as one made pushing a projection <em>cost</em> rather than save. After field
        /// trimming that is the ordinary case and not a corner: the trimmer leaves a projection of
        /// <c>DOC</c> alone above the scan, so the subtree this node converts is one column wide
        /// already, and pushing the query's real projection widens it to as many columns as the
        /// query selects. Counted, that charged back exactly what the projection saved, the two
        /// plans tied on rows — which is the only component <c>VolcanoCost</c> compares — and a
        /// two-column query read the container whole (#145).
        /// </para>
        /// <para>
        /// <b>So the document is weighed rather than counted</b>, by the container's average document
        /// size where that has been measured and by <see cref="MinimumDocumentWidth"/> where it has
        /// not. <c>getAverageRowSize</c> is the metadata that would answer this properly, and it was
        /// tried first: measured over these plans it answers <c>null</c> at every node, scan and
        /// projection alike, so asking for it and quietly falling back would have described a
        /// mechanism that never ran. A <c>RelMdSize</c> handler for the Cosmos nodes is what would
        /// make it answer, and is still the larger piece of work this stands in for.
        /// </para>
        /// </remarks>
        public override RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var cost = base.computeSelfCost(planner, mq);
            if (cost == null)
                return null!;

            return cost.multiplyBy(ClrEnumerableConvention.CostMultiplier * Math.Max(1d, Width(getInput())));
        }

        /// <summary>
        /// What one projected value is taken to weigh, in bytes.
        /// </summary>
        /// <remarks>
        /// A guess, and deliberately a generous one: a UUID rendered as JSON is 38 bytes and a
        /// timestamp 26, so 64 over-states a value and therefore under-states how many values a
        /// document is worth. That is the harmless direction — the ratio only ever argues for
        /// projecting, so under-stating it can leave a projection unpushed and can never push one
        /// that should not be.
        /// </remarks>
        public const double BytesPerValue = 64d;

        /// <summary>
        /// The fewest values the document column is ever worth.
        /// </summary>
        /// <remarks>
        /// The half that needs no measurement, which is what makes it the half that works on a
        /// container nobody has asked for statistics — and that container is the common case, since
        /// the statistics are read only where the account will answer for them. The service generates
        /// <c>id</c>, <c>_rid</c>, <c>_self</c>, <c>_etag</c>, <c>_attachments</c> and <c>_ts</c> on
        /// every item and returns them with it, so a statement selecting the document returns six
        /// values before anything the document itself holds, while one projecting two paths returns
        /// two.
        /// </remarks>
        public const double MinimumDocumentWidth = 6d;

        /// <summary>
        /// Returns how many values a row of the converted subtree is worth.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read off the row type rather than off the plan, because the row type is what crosses this
        /// node: a subtree that has pushed a projection presents the projected columns here, and one
        /// that has not presents the document. Which column <em>is</em> the document is left to
        /// <see cref="CosmosImplementor.BindReadings"/> rather than tested again here — it is the one
        /// column the row model reserves, and one place should say so.
        /// </para>
        /// <para>
        /// <b>That answer is the column's name, so an alias can fool it either way</b>, and neither
        /// direction reaches a decision. A computed column aliased <c>DOC</c> is weighed as a document
        /// and over-prices a pushed projection; the document column aliased to anything else is
        /// weighed as a value and under-prices one. Both only matter where one plan ships the document
        /// and the other does not — and a statement that names the document at all ships it on every
        /// plan, so the two alternatives are mis-priced identically and rank the same.
        /// </para>
        /// </remarks>
        /// <param name="input">The subtree being converted.</param>
        /// <returns>The width, in values.</returns>
        static double Width(RelNode input)
        {
            var container = input.getConvention() is CosmosConvention convention ? convention.Container : null;
            var document = Metadata.CosmosRequestUnitModel.AverageDocumentSizeInBytes(container);
            var documentWidth = Math.Max(MinimumDocumentWidth, document / BytesPerValue);

            var readings = CosmosImplementor.BindReadings(input.getRowType());
            var width = 0d;

            for (var i = 0; i < readings.Count; i++)
                width += readings[i] == CosmosReading.Json ? documentWidth : 1d;

            return width;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The bridge, for the reason the type's own remarks give: there is no synchronous read to write
        /// here, so this is the awaiting body read across, and it blocks a thread per row.
        /// </remarks>
        public ClrEnumerableResult Implement(ClrEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            return implementor.Pulled(ImplementAsync(implementor, pref));
        }

        /// <inheritdoc />
        public ClrAsyncEnumerableResult ImplementAsync(ClrEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var input = getInput();

            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());
            var rowType = physType.RowType;

            var (query, fields, readings) = CosmosConverters.GenerateQuery(input, implementor.RexBuilder);

            // A point read — one, or a batch of many — returns documents rather than the object the
            // statement constructs, so the two paths need different row builders; and the read needs
            // every output field to address a path, which only this knows. Where one does not, the
            // read is withdrawn and the statement is executed as the query it already is.
            var rowBuilder = query.PointReadId is null && query.PointReadIds is null
                ? null
                : CosmosConverters.DocumentRowBuilder(physType, getRowType(), fields);

            if ((query.PointReadId is not null || query.PointReadIds is not null) && rowBuilder is null)
                query = query with { PointReadId = null, PointReadIds = null };

            rowBuilder ??= CosmosConverters.RowBuilder(physType, getRowType(), readings);

            Hook.QUERY_PLAN.run(query.Sql);

            return implementor.ResultAsync(physType,
                Expression.Call(null,
                    ReadAsyncMethod.MakeGenericMethod(rowType),
                    CosmosConverters.ExecutorExpression(input, implementor.Root),
                    implementor.Root,
                    Expression.Constant(query),
                    rowBuilder,
                    // Default on purpose, and load-bearing. An expression tree applies no optional
                    // default, so the token has to be written here; writing the *default* one is what
                    // lets [EnumeratorCancellation] substitute the token the reader gave
                    // GetAsyncEnumerator, which the operators hand down to this leaf. So the plan
                    // carries no token, is reusable across executions, and each enumeration supplies
                    // its own -- reaching ReadNextAsync, where it cancels a page already in flight.
                    // ShouldCarryTheReadersTokenIntoTheExecutor holds that.
                    //
                    // A plan read synchronously gets none of it: IEnumerable has no token to hand down,
                    // so there cancellation really is only declining to ask for the next page.
                    Expression.Constant(CancellationToken.None)));
        }

    }

}
