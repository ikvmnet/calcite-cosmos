using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.tools;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// The part of <c>Implement</c> that belongs to no single node: the order a plan is allowed to
    /// write its clauses in, and the refusal to visit anything outside the Cosmos convention.
    /// </summary>
    public class CosmosRelTests : CosmosRelNodeFixture
    {

        // Cosmos applies OFFSET/LIMIT last. An operator the plan places above a row restriction
        // but that would be written into an earlier clause cannot be folded into the same
        // statement: it would run before the restriction rather than after, returning different
        // rows. These are silent wrong answers, not service errors.

        CosmosSort LimitOver(RelNode input, int fetch) =>
            SortOver(input, RelCollations.EMPTY, null, _rex.makeExactLiteral(new java.math.BigDecimal(fetch)));

        [Fact]
        public void FilterAboveARowLimitIsRefused()
        {
            var filter = new CosmosFilter(_cluster, Traits(), LimitOver(Scan(), 5),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("x")));

            var act = () => Sql(filter, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*row limit*");
        }

        [Fact]
        public void AggregateAboveARowLimitIsRefused()
        {
            var aggregate = new CosmosAggregate(
                _cluster, Traits(), LimitOver(Scan(), 5),
                org.apache.calcite.util.ImmutableBitSet.of(new[] { 4 }), null, new java.util.ArrayList());

            var act = () => Sql(aggregate, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*row limit*");
        }

        [Fact]
        public void UnnestAboveARowLimitIsRefused()
        {
            var unnest = UnnestOver(LimitOver(Scan(), 5), MapItem("tags"));

            var act = () => Sql(unnest, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        /// <remarks>
        /// A traversal multiplies rows, so folding one above a sort would sort the unmultiplied
        /// set.
        /// </remarks>
        [Fact]
        public void UnnestAboveASortIsRefused()
        {
            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
            var unnest = UnnestOver(sort, MapItem("tags"));

            var act = () => Sql(unnest, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        /// <remarks>
        /// A sort without a restriction commutes with a filter, so this stays available.
        /// </remarks>
        [Fact]
        public void FilterAboveAnUnlimitedSortIsAllowed()
        {
            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
            var filter = new CosmosFilter(_cluster, Traits(), sort,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(4), Str("bikes")));

            Sql(filter, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE (c.category = @p0) ORDER BY c.id ASC");
        }

        // ── What a rule decides on is what implementation binds ───────────────────

        /// <summary>
        /// Asserts that every path the rule-time binding names is the path implementation binds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>CosmosImplementor.TryBindOutput</c>
        /// is what every rule decides on, and it mirrors each node's <c>Implement</c> so that a rule
        /// cannot fire on a binding implementation then refuses — after the planner has committed to
        /// the plan, where a refusal is a query that fails rather than one that falls back.
        /// </para>
        /// <para>
        /// <b>One direction only, and deliberately.</b> Where the walk binds nothing, a rule declines,
        /// which is always correct; where it names a path implementation does not bind, a rule fires
        /// and implementation throws. So the walk may know less and must never know more. The
        /// traversal's element is the standing case of knowing less: implementation binds it to its
        /// alias and the walk leaves it unbound, pending a measurement (<c>TODO.md</c>, section 5).
        /// </para>
        /// </remarks>
        void BindsNoMoreThanItsImplementation(CosmosRel node)
        {
            CosmosImplementor.TryBindOutput(node, out var fields, out var readings, out _).Should().BeTrue();

            var implementor = Implementor();
            node.Implement(implementor);

            fields.Should().HaveCount(node.getRowType().getFieldCount());

            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i] is not CosmosPath path)
                    continue;

                implementor.Fields.Should().HaveCountGreaterThan(i);
                implementor.Fields[i]?.ToString().Should().Be(path.ToString(), "ordinal {0} is bound to {1} where the rule decides", i, path);

                var reading = i < implementor.Readings.Count ? implementor.Readings[i] : CosmosReading.Typed;
                reading.Should().Be(readings[i], "ordinal {0} is read the way the rule was told", i);
            }
        }

        CosmosAggregate DistinctOver(RelNode input) =>
            new(_cluster, Traits(), input, org.apache.calcite.util.ImmutableBitSet.range(input.getRowType().getFieldCount()), null, new java.util.ArrayList());

        /// <summary>
        /// A <c>DISTINCT</c> of a path some documents lack binds nothing, at the rule as in the node.
        /// </summary>
        /// <remarks>
        /// The node groups such a key as <c>IS_DEFINED(p) ? p : null</c>, SQL's one null for the
        /// service's two, and the projected value is then not the path: the service will not order by
        /// a select-list alias, and the path sorts an absent property somewhere the projected null
        /// does not. So it binds the key only where the container says the path is always there. The
        /// walk bound the input's path regardless, and a sort above such a distinct was converted on
        /// the walk's answer and refused by the node's — soundness probe P16, "the sort keys do not
        /// resolve to document paths", at implementation.
        /// </remarks>
        [Fact]
        public void ADistinctOfAPathSomeDocumentsLackBindsNothing()
        {
            var distinct = DistinctOver(ProjectOver(Scan(), new[] { ("p", Doc("price")) }));

            CosmosImplementor.TryBindOutput(distinct, out var fields, out _).Should().BeTrue();
            fields.Should().Equal(new CosmosPath?[] { null });

            BindsNoMoreThanItsImplementation(distinct);
        }

        /// <summary>
        /// A <c>DISTINCT</c> of a path the service writes on every document binds it, so a sort above
        /// can still name it.
        /// </summary>
        [Fact]
        public void ADistinctOfAPathEveryDocumentHasBindsIt()
        {
            var distinct = DistinctOver(ProjectOver(Scan(), new[] { ("ts", Ref(2)) }));

            CosmosImplementor.TryBindOutput(distinct, out var fields, out _).Should().BeTrue();
            fields.Select(f => f?.ToString()).Should().Equal("c._ts");

            BindsNoMoreThanItsImplementation(distinct);
        }

        /// <remarks>
        /// The shapes the walk already agreed with implementation on, held to the same assertion so
        /// that the next node it learns is.
        /// </remarks>
        [Fact]
        public void TheWalkBindsNoMoreThanImplementation()
        {
            BindsNoMoreThanItsImplementation(Scan());
            BindsNoMoreThanItsImplementation(ProjectOver(Scan(), new[] { ("id", Ref(1)), ("p", Doc("price")), ("n", _rex.makeCall(SqlStdOperatorTable.PLUS, Ref(2), Num(1))) }));
            BindsNoMoreThanItsImplementation(new CosmosFilter(_cluster, Traits(), Scan(), _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(4), Str("bikes"))));
            BindsNoMoreThanItsImplementation(SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING))));
            BindsNoMoreThanItsImplementation(DistinctOver(ProjectOver(Scan(), new[] { ("id", Ref(1)), ("p", Doc("price")) })));
            BindsNoMoreThanItsImplementation(SortOver(DistinctOver(ProjectOver(Scan(), new[] { ("ts", Ref(2)) })), Collation((0, RelFieldCollation.Direction.ASCENDING))));
            BindsNoMoreThanItsImplementation(ProjectOver(UnnestOver(Scan(), MapItem("tags")), new[] { ("id", Ref(1)), ("p", Doc("price")) }));
        }

        /// <summary>
        /// A converted traversal binds as its logical form does: the document's columns as they were,
        /// and the element unbound.
        /// </summary>
        /// <remarks>
        /// The walk knew the <c>Correlate</c> a rule sees and not the <see cref="CosmosUnnest"/> it
        /// becomes, which is a <c>SingleRel</c>. The lookup join asks the walk again of the converted
        /// subtree when it is implemented, so a lookup over a traversal was chosen and then failed to
        /// implement (soundness review, cause K; <c>CosmosLookupJoinTests</c> runs the whole path).
        /// </remarks>
        [Fact]
        public void ATraversalBindsAsItsLogicalFormDoes()
        {
            var unnest = UnnestOver(Scan(), MapItem("tags"));

            CosmosImplementor.TryBindOutput(unnest, out var fields, out _).Should().BeTrue();
            fields.Select(f => f?.ToString()).Should().Equal("c", "c.id", "c._ts", "c._etag", "c.category", null);

            BindsNoMoreThanItsImplementation(unnest);
        }

        // ── Convention boundary ───────────────────────────────────────────────────

        [Fact]
        public void VisitingANonCosmosNodeIsRefused()
        {
            var logical = org.apache.calcite.rel.logical.LogicalTableScan.create(_cluster, _table, java.util.Collections.emptyList());

            var act = () => Implementor().Visit(logical);
            act.Should().Throw<CosmosTranslationException>();
        }

    }

}
