using System.Collections.Generic;

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
    /// The <c>JOIN IN</c> a traversal renders, and the alias it binds the element to.
    /// </summary>
    public class CosmosUnnestTests : CosmosRelNodeFixture
    {

        [Fact]
        public void UnnestRendersJoinIn()
        {
            var unnest = UnnestOver(Scan(), MapItem("tags"));

            Sql(unnest, Implementor()).Should().Be("SELECT VALUE c FROM products c JOIN t0 IN c.tags");
        }

        [Fact]
        public void UnnestBindsTheElementToItsAlias()
        {
            var implementor = Implementor();
            UnnestOver(Scan(), MapItem("tags")).Implement(implementor);

            implementor.Fields.Should().HaveCount(6);
            implementor.Fields[5]!.ToString().Should().Be("t0");
        }

        [Fact]
        public void StackedUnnestsGetDistinctAliases()
        {
            var inner = UnnestOver(Scan(), MapItem("tags"));
            var outer = UnnestOver(inner, MapItem("sizes"), "s");

            Sql(outer, Implementor()).Should().Be("SELECT VALUE c FROM products c JOIN t0 IN c.tags JOIN t1 IN c.sizes");
        }

        [Fact]
        public void FilterAboveUnnestAddressesTheElement()
        {
            var unnest = UnnestOver(Scan(), MapItem("tags"));
            var filter = new CosmosFilter(_cluster, Traits(), unnest,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(5), Str("outdoor")));

            Sql(filter, Implementor()).Should().Be("SELECT VALUE c FROM products c JOIN t0 IN c.tags WHERE (t0 = @p0)");
        }

        /// <remarks>
        /// The array expression of a lateral unnest addresses a correlation variable rather than
        /// the input directly, so it must resolve through the same bindings.
        /// </remarks>
        [Fact]
        public void CorrelationVariableResolvesToTheInputBinding()
        {
            var correlationId = _cluster.createCorrel();
            var correlated = _rex.makeCorrel(_table.getRowType(), correlationId);
            var array = DocArray("tags", _rex.makeFieldAccess(correlated, 0));

            Sql(UnnestOver(Scan(), array, correlationId: correlationId), Implementor()).Should().Be("SELECT VALUE c FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// A correlation variable standing for some other row does not resolve against these bindings.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same shape of expression means opposite things depending on what the variable stands
        /// for. A lateral traversal correlates an input on itself, so <c>JSON_QUERY($cor0."DOC",
        /// '$.tags')</c> under one is a path of the document being scanned — the test above. A join
        /// correlates it on the <em>other</em> side, and there the identical expression is a value of a
        /// row this statement knows nothing about.
        /// </para>
        /// <para>
        /// Resolving it anyway would emit <c>c.tags</c>: a real path, of the wrong document, in a
        /// statement the service would run without complaint. So the variable is checked rather than
        /// the shape.
        /// </para>
        /// </remarks>
        [Fact]
        public void AForeignCorrelationVariableDoesNotResolve()
        {
            var correlated = _rex.makeCorrel(_table.getRowType(), _cluster.createCorrel());
            var array = DocArray("tags", _rex.makeFieldAccess(correlated, 0));

            // A different variable from the one the traversal declares as its own.
            var unnest = UnnestOver(Scan(), array, correlationId: _cluster.createCorrel());

            var implement = () => Sql(unnest, Implementor());
            implement.Should().Throw<CosmosTranslationException>();
        }

        /// <summary>
        /// A projection below a traversal is completed with the element rather than refused.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos evaluates <c>SELECT</c> after <c>JOIN</c>, so the object a projection below a
        /// traversal constructs is still the right one — it is a property short, having been written
        /// before the element existed. Adding it is the whole difference.
        /// </para>
        /// <para>
        /// Not an exotic shape: Calcite's own rule set hoists the traversed array into a projection on
        /// the correlate's left, so every array traversal a host plans arrives this way, and refusing
        /// it refused the feature. See <c>CosmosPlannerTests.UnnestOverAHoistedArrayCarriesTheElement</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void UnnestAboveAProjectionAddsTheElementToIt()
        {
            // The document first, so that the traversed array is addressed through the projection
            // rather than off the scan -- which is the shape being tested.
            var project = ProjectOver(Scan(), new[] { ("doc", Ref(0)), ("theId", Ref(1)) });
            var unnest = UnnestOver(project, MapItem("tags"));

            Sql(unnest, Implementor()).Should().Be("SELECT VALUE { \"doc\": c, \"theId\": c.id, \"t\": t0 } FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// A traversal above a pushed <c>DISTINCT</c> is refused.
        /// </summary>
        /// <remarks>
        /// The one projection a traversal may not complete. <c>DISTINCT</c> de-duplicates what
        /// <c>SELECT</c> constructs, and the service constructs it after the <c>JOIN</c> — so folding
        /// the traversal in would de-duplicate the multiplied rows where the plan asked for the rows
        /// of an already-distinct set to be multiplied. Two documents sharing a tag would yield one
        /// row rather than two.
        /// </remarks>
        [Fact]
        public void UnnestAboveADistinctIsRefused()
        {
            var distinct = new CosmosAggregate(
                _cluster, Traits(), Scan(),
                org.apache.calcite.util.ImmutableBitSet.of(new[] { 0 }), null, new java.util.ArrayList());

            var unnest = UnnestOver(distinct, MapItem("tags"));

            var act = () => Sql(unnest, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*DISTINCT*");
        }

        [Fact]
        public void UnnestOfANonPathIsRefused()
        {
            var computed = _rex.makeCall(SqlStdOperatorTable.PLUS, Ref(2), Num(1));
            var unnest = UnnestOver(Scan(), computed);

            var act = () => Sql(unnest, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*path*");
        }

    }

}
