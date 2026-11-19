using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Project"/> to a <see cref="CosmosProject"/>.
    /// </summary>
    public class CosmosProjectRule : CosmosConverterRule
    {

        /// <summary>
        /// Returns a projected expression as the service is sent it: with the condition of every <c>CASE</c> in it lowered as
        /// a filter's condition is.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The legality test, the splits and <see cref="CosmosProject.Implement"/> all render this form, as a filter's
        /// text is its condition's rendering form (<see cref="CosmosFilter.Sent"/>). See
        /// <see cref="CosmosFactRewriter.RewriteConditions"/>.
        /// </para>
        /// <para>
        /// <b>Rendered, never planned.</b> The node holds the expression as written. A condition the rewrite writes
        /// <c>FALSE</c> is the condition's value on every document the declaration admits, not Calcite's, which still
        /// evaluates the condition; held in the plan, Calcite's simplifier carried it out of the expression, a <c>CASE</c>
        /// folded to a null making a comparison beside a cast in a sibling column, or in a filter above, never true, and
        /// the plan the empty relation where Calcite fails on the cast. In the text alone it changes what the service
        /// computes for the expression, and nothing else.
        /// </para>
        /// </remarks>
        internal static RexNode Rendered(CosmosContainerMetadata? container, RexNode expression, IReadOnlyList<CosmosPath?> fields, RexBuilder rexBuilder) =>
            CosmosFactRewriter.RewriteConditions(expression, fields, container, CosmosImplementor.DefaultRootAlias, rexBuilder);

        /// <summary>
        /// Determines whether every projected expression can be rendered as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Checked against the binding derived by walking the input, which is the same binding
        /// implementation will use, so a projection accepted here will not fail later. Reading it off
        /// the input row type would take a projection's aliases for document properties.
        /// </para>
        /// <para>
        /// The same walk says which clauses the subtree has already written, and the <c>SELECT</c>
        /// among them is the other half of the answer: a statement has one, and Cosmos has no derived
        /// table to nest a second in. Refusing here rather than at implementation is what makes an
        /// ordinary <c>SELECT c."id" FROM t AS c, UNNEST(…)</c> plan at all — a host running Calcite's
        /// own rule set hoists the traversed array into a projection below the correlate, leaving this
        /// projection above one, and converting it optimistically turned that into a plan the
        /// implementor then refused rather than a projection Calcite applies itself.
        /// </para>
        /// </remarks>
        static bool IsTranslatable(CosmosConvention convention, Project project)
        {
            if (project.getProjects().size() == 0)
                return false;

            if (CosmosImplementor.TryBindOutput(project.getInput(), out var fields, out _, out _, out var written, out var container) == false
                || convention.Owns(container) == false)
                return false;

            if ((written & CosmosClauses.Projection) != 0)
                return false;

            var projects = project.getProjects();
            var rexBuilder = project.getCluster().getRexBuilder();

            // The same facts CosmosProject.Implement will translate against, for the same reason the
            // bindings are the ones it will use: a rule admitting a projection the node then refuses
            // is a plan that fails at implementation rather than a plan that was never chosen. What the
            // filters beneath establish of every row it reads (L1), decided once for this projection
            // and handed to the node convert builds.
            var facts = Facts(project, container);
            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList(), facts, null, convention.Container);

            // TryTranslateProjection rather than TryTranslate, so that the rule admits exactly the
            // expressions CosmosProject.Implement can render — including the cast to text it sends the
            // value underneath of. Asking the plain translator here would decline a projection the node
            // can push.
            for (var i = 0; i < projects.size(); i++)
                if (translator.TryTranslateProjection(Rendered(convention.Container, (RexNode)projects.get(i), fields, rexBuilder), out _, out _) == false)
                    return false;

            // Window functions have no Cosmos equivalent.
            return project.containsOver() == false;
        }

        /// <summary>
        /// Returns what every row a projection reads is known to satisfy: the filters beneath it (L1), the same each time
        /// it is asked of the same projection. See <see cref="CosmosRowFacts"/>.
        /// </summary>
        internal static JsonFactSet Facts(Project project, CosmosContainerMetadata? container) =>
            CosmosRowFacts.For(project, container, OracleLicence.Select);

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosProjectRule Create(CosmosConvention convention)
        {
            return (CosmosProjectRule)Config.INSTANCE
                .withConversion(typeof(Project), new DelegatePredicate<Project>(p => IsTranslatable(convention, p)), Convention.NONE, convention, "CosmosProjectRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosProjectRule>(c => new CosmosProjectRule(c)))
                .toRule(typeof(CosmosProjectRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosProjectRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            // A correlated projection references a variable bound outside this statement, which
            // Cosmos cannot express here. One that declares a variable no expression reads binds
            // nothing: a scalar sub-query in a select list that nothing above reads leaves one, its
            // column trimmed before any rule removes the sub-query, and no other rule converts it.
            var projects = ((Project)call.rel(0)).getProjects();
            for (var i = 0; i < projects.size(); i++)
                if (RexUtil.containsCorrelation((RexNode)projects.get(i)))
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var project = (Project)rel;

            if (@out is not CosmosConvention || CosmosImplementor.TryBindOutput(project.getInput(), out _, out _, out _, out _, out var container) == false)
                return null;

            if (CosmosImplementor.NamesAnEscape(project.getRowType(), container))
                return null;

            // Built under the facts the legality test decided under, which the node renders with, and holding the
            // expressions as written, which it renders as Rendered does.
            return new CosmosProject(
                project.getCluster(),
                project.getTraitSet().replace(@out),
                convert(project.getInput(), project.getInput().getTraitSet().replace(@out)),
                project.getProjects(),
                project.getRowType(),
                Facts(project, container));
        }

    }

}
