using Apache.Calcite.Cosmos.Adapter.Sql;

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
            var projects = project.getProjects();
            if (projects.size() == 0)
                return false;

            if (CosmosImplementor.TryBindOutput(project.getInput(), out var fields, out var written) == false)
                return false;

            if ((written & CosmosClauses.Projection) != 0)
                return false;

            var translator = new CosmosRexTranslator(project.getCluster().getRexBuilder(), fields, new CosmosParameterList(), null, convention.Container);

            // TryTranslateProjection rather than TryTranslate, so that the rule admits exactly the
            // expressions CosmosProject.Implement can render — including the cast to text it sends the
            // value underneath of. Asking the plain translator here would decline a projection the node
            // can push.
            for (var i = 0; i < projects.size(); i++)
                if (translator.TryTranslateProjection((RexNode)projects.get(i), out _, out _) == false)
                    return false;

            // Window functions have no Cosmos equivalent.
            return project.containsOver() == false;
        }

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
            // Cosmos cannot express here.
            return ((Project)call.rel(0)).getVariablesSet().isEmpty();
        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var project = (Project)rel;

            return new CosmosProject(
                project.getCluster(),
                project.getTraitSet().replace(@out),
                convert(project.getInput(), project.getInput().getTraitSet().replace(@out)),
                project.getProjects(),
                project.getRowType());
        }

    }

}
