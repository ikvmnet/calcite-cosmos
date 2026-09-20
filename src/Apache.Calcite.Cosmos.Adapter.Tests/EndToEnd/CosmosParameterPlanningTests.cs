using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What a prepared statement's <c>?</c> does to a plan: the value belongs to the execution, and
    /// everything decided here is decided from the type.
    /// </summary>
    /// <remarks>
    /// A host that prepares once and runs many times parameterises its values — Entity Framework does
    /// it for every filter and for every <c>Take</c>, a literal one included — so this is the ordinary
    /// shape rather than an exotic one. A limit asked for its count and threw; a comparison was
    /// weakened to a definedness test and rechecked in process. Both were asking for a value the plan
    /// was never going to have.
    /// </remarks>
    [TestClass]
    public class CosmosParameterPlanningTests
    {

        static readonly CosmosContainerMetadata Items = new("items", new[] { "/k" });

        static RelNode PlanToCosmos(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(Items);

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("items", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Enumerable.ClrEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.Enumerable.ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// The deepest node still in the Cosmos convention, which is the statement's root.
        /// </summary>
        /// <param name="node">The node to search from.</param>
        /// <returns>The node, or <c>null</c> where the plan reached the service at all.</returns>
        static RelNode FindCosmos(RelNode node)
        {
            if (node is CosmosRel)
                return node;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCosmos((RelNode)inputs.get(i)) is RelNode found)
                    return found;

            return null!;
        }

        /// <summary>
        /// Renders the statement a plan's Cosmos subtree carries.
        /// </summary>
        /// <param name="sql">The SQL to plan.</param>
        /// <returns>The statement.</returns>
        static CosmosQuery Query(string sql)
        {
            var best = PlanToCosmos(sql);
            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), Items);
            implementor.Visit(FindCosmos(best));
            return implementor.Build();
        }

        static string PlanText(string sql) => RelOptUtil.toString(PlanToCosmos(sql)).Trim().Replace("\r\n", "\n");

        /// <summary>
        /// A parameterised row limit reaches the service instead of throwing. #103.
        /// </summary>
        /// <remarks>
        /// The count was read with <c>RexLiteral.intValue</c>, which asserts its argument is a literal
        /// — so a plan that carried a parameter there planned happily and threw only when it ran, and
        /// <c>Take</c> and <c>First</c> over a Cosmos-backed entity failed outright.
        /// </remarks>
        [TestMethod]
        public void AParameterisedRowLimitReachesTheService()
        {
            var query = Query("""SELECT c."id" FROM items AS c FETCH NEXT ? ROWS ONLY""");

            query.Sql.Should().Contain("LIMIT @", "the name is written where the digits would have gone: " + query.Sql);
            query.Parameters.Should().ContainSingle(p => p.Value is CosmosDynamicValue,
                "and the slot behind it is left for the execution");
        }

        /// <summary>
        /// Both halves of the clause, and the one <c>ORDER BY RANK</c> writes, are the same question.
        /// </summary>
        [TestMethod]
        public void AParameterisedOffsetAndLimitBothReachTheService()
        {
            var query = Query("""SELECT c."id" FROM items AS c ORDER BY c."id" OFFSET ? ROWS FETCH NEXT ? ROWS ONLY""");

            query.Sql.Should().Contain("OFFSET @").And.Contain("LIMIT @");
            query.Parameters.Should().HaveCount(2).And.OnlyContain(p => p.Value is CosmosDynamicValue);
        }

        /// <summary>
        /// A page whose size the plan does not know asks for the service's own page rather than for a
        /// page it cannot size.
        /// </summary>
        /// <remarks>
        /// Not a loss: <c>MaxItemCount</c> is a hint about how many rows come back per round trip, and
        /// what bounds the result is the <c>LIMIT</c> the statement already carries.
        /// </remarks>
        [TestMethod]
        public void AnUnknownLimitLeavesThePageSizeToTheService()
        {
            Query("""SELECT c."id" FROM items AS c FETCH NEXT ? ROWS ONLY""").MaxItemCount
                .Should().BeNull();

            Query("""SELECT c."id" FROM items AS c FETCH NEXT 3 ROWS ONLY""").MaxItemCount
                .Should().Be(3, "while a count the plan does have still sizes the page");
        }

        /// <summary>
        /// A parameterised comparison over a path the service types is pushed exactly.
        /// </summary>
        /// <remarks>
        /// <c>id</c> is a string on every container by the service's own guarantee, so there is no
        /// second type for the comparison to disagree over and nothing to weaken. Whoever supplies the
        /// value does not enter into it.
        /// </remarks>
        [TestMethod]
        public void AParameterisedComparisonOverATypedPathIsPushedExactly()
        {
            const string Sql = """SELECT c."id" FROM items AS c WHERE c."id" = ?""";

            Query(Sql).Sql.Should().Contain("c.id = @");

            PlanText(Sql).Should().NotContain("ClrEnumerableFilter",
                "so nothing is left above to recheck it: " + PlanText(Sql));
        }

        /// <summary>
        /// Over an undeclared document path it is weakened instead, and that is the value's absence
        /// rather than the parameter's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The accessor renders every JSON scalar as text, so an equality against text is exact only
        /// where the text is one no number and no boolean renders as — which is a fact about the
        /// <em>value</em>, and a parameter has none to inspect. So the conjunct pushes weakened and
        /// the equality is rechecked above, which is the same trade the adapter makes for any
        /// comparison it cannot show exact.
        /// </para>
        /// <para>
        /// This is what degrades when a plan is given less to work with, and it is sound rather than
        /// unfortunate: the rows are right either way.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void OverAnUndeclaredPathTheComparisonIsWeakenedRatherThanRefused()
        {
            const string Sql = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.type') = ?""";

            Query(Sql).Sql.Should().Contain("IS_DEFINED(c.type)",
                "the restriction the comparison implies still reaches the service");

            PlanText(Sql).Should().Contain("ClrEnumerableFilter",
                "and the equality itself is rechecked above: " + PlanText(Sql));
        }

        /// <summary>
        /// The plan reasons from the type and never from the value.
        /// </summary>
        /// <remarks>
        /// Which is the whole of the fix. Nothing here has a value to record, and the ordinal is what
        /// the plan does know — a consumer that casts its parameters, as a provider generating SQL
        /// should, gives it the type as well.
        /// </remarks>
        [TestMethod]
        public void TheValueIsNeverRead()
        {
            var query = Query("""SELECT c."id" FROM items AS c WHERE c."id" = ?""");

            query.Parameters.Should().ContainSingle()
                .Which.Value.Should().BeOfType<CosmosDynamicValue>()
                .Which.Ordinal.Should().Be(0, "only the ordinal, and no value was invented");
        }

        /// <summary>
        /// The slot is closed at execution, out of the context the run supplies.
        /// </summary>
        [TestMethod]
        public void BindingFillsTheSlotFromTheDataContext()
        {
            var query = Query("""SELECT c."id" FROM items AS c FETCH NEXT ? ROWS ONLY""");

            var bound = CosmosQueries.Bind(query, new Context(new Dictionary<string, object?> { ["?0"] = java.lang.Integer.valueOf(20) }));

            bound.Sql.Should().Be(query.Sql, "the statement is the same statement every execution");
            bound.Parameters[0].Value.Should().Be(20L, "and the value arrives as a literal of that type would have");
        }

        /// <summary>
        /// A statement carrying no parameter of its own is handed back untouched.
        /// </summary>
        [TestMethod]
        public void BindingLeavesAnOrdinaryStatementAlone()
        {
            var query = Query("""SELECT c."id" FROM items AS c FETCH NEXT 3 ROWS ONLY""");

            CosmosQueries.Bind(query, new Context(new Dictionary<string, object?>())).Should().Be(query);
        }

        /// <summary>
        /// A data context holding nothing but the values a run supplies.
        /// </summary>
        sealed class Context : org.apache.calcite.DataContext
        {

            readonly Dictionary<string, object?> _values;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="values">The values, keyed as Calcite keys them.</param>
            public Context(Dictionary<string, object?> values)
            {
                _values = values;
            }

            /// <inheritdoc />
            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            /// <inheritdoc />
            public org.apache.calcite.adapter.java.JavaTypeFactory getTypeFactory() => null!;

            /// <inheritdoc />
            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            /// <inheritdoc />
            public object get(string name) => _values.TryGetValue(name, out var value) ? value! : null!;

        }

    }

}
