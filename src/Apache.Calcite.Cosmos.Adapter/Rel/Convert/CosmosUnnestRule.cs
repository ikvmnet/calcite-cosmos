using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a lateral <see cref="Correlate"/> over an <see cref="Uncollect"/> into a
    /// <see cref="CosmosUnnest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only rule that produces a Cosmos <c>JOIN</c>, and it deliberately matches
    /// <see cref="Correlate"/> rather than <see cref="Join"/>. Cosmos <c>JOIN</c> has no predicate
    /// in its grammar; it cross-products a document with one of its own arrays. A relational join
    /// is not expressible and no rule may convert one.
    /// </para>
    /// <para>
    /// The shape produced for <c>UNNEST</c> is a correlate whose right input is an
    /// <c>Uncollect</c> over a single-expression <c>Project</c> over a one-row <c>Values</c>. The
    /// projected expression addresses the correlation variable and must resolve to a path.
    /// </para>
    /// </remarks>
    public class CosmosUnnestRule : CosmosConverterRule
    {

        /// <summary>
        /// Extracts the array expression from the right side of a correlate, or returns <c>null</c>
        /// when the node is not a lateral array traversal this rule can express.
        /// </summary>
        /// <remarks>
        /// Public so that the shape this rule depends on can be asserted against real planner
        /// output rather than assumed. Planning <c>… FROM products AS c, UNNEST(c."_MAP"['tags'])</c>
        /// yields a correlate over <c>Uncollect(Project(ITEM($cor0._MAP, 'tags')))</c>, which is
        /// what this recognises.
        /// </remarks>
        /// <param name="correlate">The correlate to inspect.</param>
        /// <returns>The array expression, or <c>null</c>.</returns>
        public static RexNode? GetArrayExpression(Correlate correlate)
        {
            if (GetUncollect(correlate) is not Uncollect uncollect)
                return null;

            if (Strip(uncollect.getInput()) is not Project project || project.getProjects().size() != 1)
                return null;

            return (RexNode)project.getProjects().get(0);
        }

        /// <summary>
        /// Finds the <see cref="Uncollect"/> the correlate traverses, seeing through a predicate
        /// applied to the element.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A predicate over the element does not stay above the correlate where it was written.
        /// Calcite pushes it into the right side, between the traversal and the uncollect, because
        /// that is where the rows it speaks about are, so it arrives as an <c>Uncollect</c> wrapped
        /// in a <c>Filter</c>. Cosmos writes that as a <c>WHERE</c> over the traversal alias,
        /// evaluated after the <c>JOIN</c>, so the shape is recognised rather than declined. See
        /// <see cref="GetElementCondition"/> for the predicate itself.
        /// </para>
        /// <para>
        /// Exactly one column per element, because <c>JOIN … IN</c> binds one alias to it and
        /// <see cref="CosmosUnnest"/> appends one binding. An uncollect yielding two — a map's key
        /// and value — is a different operator; Calcite names those columns through a projection,
        /// which is not this shape, so this is belt and braces rather than a case seen from SQL.
        /// </para>
        /// </remarks>
        static Uncollect? GetUncollect(Correlate correlate)
        {
            var right = Strip(correlate.getRight());

            if (right is Filter filter)
                right = Strip(filter.getInput());

            if (right is not Uncollect uncollect)
                return null;

            // Cosmos has no way to surface an element's position.
            if (uncollect.withOrdinality)
                return null;

            return uncollect.getRowType().getFieldCount() == 1 ? uncollect : null;
        }

        /// <summary>
        /// Extracts the predicate applied to the traversed element, addressed against the
        /// correlate's own output, or <c>null</c> where there is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The predicate arrives written against the uncollect's row, where the element is field
        /// zero. The correlate's output is the left's fields followed by it, so every reference is
        /// shifted by the left's arity — which is what turns it into a predicate
        /// <see cref="CosmosFilter"/> can be given over a <see cref="CosmosUnnest"/>, addressing the
        /// element by the ordinal the traversal binds to its alias.
        /// </para>
        /// <para>
        /// Public for the same reason <see cref="GetArrayExpression"/> is: the shape is asserted
        /// against real planner output rather than assumed.
        /// </para>
        /// </remarks>
        /// <param name="correlate">The correlate to inspect.</param>
        /// <returns>The shifted predicate, or <c>null</c>.</returns>
        public static RexNode? GetElementCondition(Correlate correlate)
        {
            if (Strip(correlate.getRight()) is not Filter filter)
                return null;

            return RexUtil.shift(filter.getCondition(), correlate.getLeft().getRowType().getFieldCount());
        }

        /// <summary>
        /// Resolves an input to a concrete node.
        /// </summary>
        /// <remarks>
        /// Once a tree is registered with the Volcano planner, an operator's inputs are equivalence
        /// sets rather than the nodes themselves, so a rule inspecting more than its own node has
        /// to see through them. This is why the rule is written against the whole correlate: the
        /// shape it needs spans three levels, and only the top one is bound directly.
        /// </remarks>
        static RelNode? Strip(RelNode? node)
        {
            for (var i = 0; node is org.apache.calcite.plan.volcano.RelSubset subset && i < 8; i++)
                node = subset.getBest() ?? subset.getOriginal();

            return node;
        }

        /// <summary>
        /// Determines whether the correlate is an array traversal whose array resolves to a path on
        /// the left input.
        /// </summary>
        static bool IsTranslatable(Correlate correlate)
        {
            var array = GetArrayExpression(correlate);
            if (array is null)
                return false;

            // Only an inner traversal matches Cosmos's cross-product semantics; a left join would
            // have to preserve documents whose array is empty, which JOIN … IN does not.
            if (correlate.getJoinType() != JoinRelType.INNER)
                return false;

            // Walked rather than read off the left's row type: above a projection those names are
            // aliases, and the array expression would resolve against paths the container has not got.
            if (CosmosImplementor.TryBindOutput(correlate.getLeft(), out var fields, out var written) == false)
                return false;

            // A traversal multiplies rows, and the service applies the JOIN before every clause a
            // left side can have written. Folded onto one it would order, page or de-duplicate the
            // multiplied rows, where the plan asked for the rows of an ordered, paged or distinct set
            // to be multiplied. A projection is the exception, and the reason this is a mask rather
            // than a flag: SELECT is evaluated after JOIN either way, so the object below is still
            // the right one and the node completes it with the element.
            if ((written & (CosmosClauses.OrderBy | CosmosClauses.RowLimit | CosmosClauses.Distinct)) != 0)
                return false;

            // The correlate's own id: this is a lateral traversal, so the variable its array
            // expression is written against stands for the very row being scanned.
            var translator = new CosmosRexTranslator(correlate.getCluster().getRexBuilder(), fields, new CosmosParameterList(), correlate.getCorrelationId());

            if (translator.TryResolvePath(array, out _) == false)
                return false;

            // A predicate over the element becomes a WHERE this rule builds itself, so whether it
            // renders is decided here. CosmosFilterRule never sees it: it is inside the correlate.
            if (GetElementCondition(correlate) is RexNode condition)
            {
                // The alias the element is bound to is allocated while the statement is built, and
                // this asks only whether the predicate renders at all, which no alias changes. Any
                // name that is not the container's will do.
                var bound = new List<CosmosPath?>(fields) { CosmosPath.Root("t") };

                // No correlation id, mirroring CosmosFilter.Implement. A predicate comparing the
                // element to the document being traversed is expressible at the service and is not
                // expressible by that node, so it is declined rather than rendered against bindings
                // it does not have.
                var predicate = new CosmosRexTranslator(correlate.getCluster().getRexBuilder(), bound, new CosmosParameterList());

                if (predicate.TryTranslate(condition, out _) == false)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosUnnestRule Create(CosmosConvention convention)
        {
            return (CosmosUnnestRule)Config.INSTANCE
                .withConversion(typeof(Correlate), new DelegatePredicate<Correlate>(IsTranslatable), Convention.NONE, convention, "CosmosUnnestRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosUnnestRule>(c => new CosmosUnnestRule(c)))
                .toRule(typeof(CosmosUnnestRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosUnnestRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var correlate = (Correlate)rel;

            var array = GetArrayExpression(correlate);
            if (array is null)
                return null;

            var left = correlate.getLeft();

            // The correlate's row type is the left's fields followed by the element's whether or not a
            // predicate sits between the two — a filter does not change its input's shape — so it is
            // the traversal's row type either way.
            RelNode traversal = new CosmosUnnest(
                correlate.getCluster(),
                correlate.getTraitSet().replace(@out),
                convert(left, left.getTraitSet().replace(@out)),
                array,
                correlate.getRowType(),
                correlate.getCorrelationId());

            // Two nodes for one correlate, because Cosmos spells them as two clauses: the traversal is
            // a JOIN and the predicate over its element is a WHERE the service evaluates after it.
            // Emitting a CosmosFilter rather than teaching CosmosUnnest a predicate keeps one node per
            // clause, and that node already renders the element by the alias the traversal bound.
            if (GetElementCondition(correlate) is RexNode condition)
                traversal = new CosmosFilter(correlate.getCluster(), traversal.getTraitSet(), traversal, condition);

            return traversal;
        }

    }

}
