using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.rules;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.util;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Benchmarks.Model
{

    /// <summary>
    /// Everything between a statement and a plan, wired the way a host wires it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same direct wiring the adapter's planner tests use, and for the same reason they give:
    /// Calcite's usual entry points open an internal JDBC connection, which fails under IKVM. What
    /// it adds is a seam at every stage — parse, validate, convert, search, render — so that a
    /// benchmark can ask for one of them rather than for all of them, and a change in the parser
    /// cannot be mistaken for a change in the planner.
    /// </para>
    /// <para>
    /// <b>What is shared and what is not.</b> The type factory, the root schema, the catalogue reader
    /// and the operator table are built once and reused, because a connection builds them once and
    /// reuses them; the type factory in particular interns row types, and rebuilding it per
    /// invocation would measure that interning rather than the planner. Everything downstream of
    /// them is per invocation, because it has to be: a validator carries the scopes of one statement,
    /// and a <see cref="VolcanoPlanner"/> that has answered <c>findBestExp</c> once cannot be asked
    /// again.
    /// </para>
    /// </remarks>
    public sealed class PlannerHarness
    {

        readonly IReadOnlyDictionary<string, CosmosTable> _tables;
        readonly JavaTypeFactoryImpl _typeFactory;
        readonly CalciteSchema _rootSchema;
        readonly CalciteCatalogReader _catalogReader;
        readonly SqlOperatorTable _operators;
        readonly SqlParser.Config _parserConfig;

        /// <summary>
        /// Initializes a new instance over <see cref="BenchmarkSchema"/>.
        /// </summary>
        public PlannerHarness()
        {
            _tables = BenchmarkSchema.CreateTables();
            _typeFactory = new JavaTypeFactoryImpl();
            _rootSchema = CalciteSchema.createRootSchema(false);

            foreach (var pair in _tables)
                _rootSchema.add(pair.Key, pair.Value);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            _catalogReader = new CalciteCatalogReader(
                _rootSchema,
                java.util.Collections.emptyList(),
                _typeFactory,
                new CalciteConnectionConfigImpl(properties));

            // Chained so that a statement can name the adapter's own functions. Calcite's standard
            // table has nothing to resolve IS_DEFINED or FULLTEXTCONTAINS to, and this is the seam a
            // caller wires the same way.
            _operators = SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance);

            _parserConfig = SqlParser.config().withUnquotedCasing(Casing.UNCHANGED);
        }

        /// <summary>
        /// Gets the tables the schema was built from, keyed by container name.
        /// </summary>
        public IReadOnlyDictionary<string, CosmosTable> Tables => _tables;

        /// <summary>
        /// Parses a statement.
        /// </summary>
        /// <remarks>
        /// <c>parseStmt</c> rather than <c>parseQuery</c>, because the corpus contains writes and a
        /// query is a statement.
        /// </remarks>
        /// <param name="sql">The statement.</param>
        /// <returns>The parse tree.</returns>
        public SqlNode Parse(string sql) => SqlParser.create(sql, _parserConfig).parseStmt();

        /// <summary>
        /// Validates a parsed statement against the schema.
        /// </summary>
        /// <param name="parsed">The parse tree.</param>
        /// <returns>The validated tree.</returns>
        public SqlNode Validate(SqlNode parsed) => CreateValidator().validate(parsed);

        /// <summary>
        /// Creates a validator for one statement.
        /// </summary>
        /// <remarks>
        /// One per statement, because a validator accumulates the scopes and derived types of the
        /// statement it validated and Calcite creates a new one per prepare.
        /// </remarks>
        /// <returns>The validator.</returns>
        public SqlValidator CreateValidator() =>
            SqlValidatorUtil.newValidator(_operators, _catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

        /// <summary>
        /// Creates a planner with the trait definitions every rule set here needs, and no rules.
        /// </summary>
        /// <remarks>
        /// <see cref="RelCollationTraitDef"/> is registered whether or not anything produces a
        /// collation: the CLR join rules include a merge join, which asks its inputs for one and
        /// fails outright where the trait is not registered.
        /// </remarks>
        /// <returns>The planner.</returns>
        public static VolcanoPlanner CreatePlanner()
        {
            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);
            return planner;
        }

        /// <summary>
        /// Converts a validated statement to a logical tree, on a fresh planner.
        /// </summary>
        /// <remarks>
        /// <c>project()</c> for a query and <c>rel</c> for a write. Ordering by an expression outside
        /// the select list makes <see cref="SqlToRelConverter"/> carry it as an extra column and record
        /// in the <see cref="RelRoot"/> that it is not output; <c>project()</c> is what applies that,
        /// and is what a real consumer uses. A write has no such mapping and <c>project()</c> over one
        /// is not meaningful.
        /// </remarks>
        /// <param name="parsed">The parse tree.</param>
        /// <returns>The logical tree.</returns>
        public RelNode ToRel(SqlNode parsed)
        {
            var validator = CreateValidator();
            var validated = validator.validate(parsed);

            var cluster = RelOptCluster.create(CreatePlanner(), new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, _catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            var root = converter.convertQuery(validated, false, true);

            return SqlKind.DML.contains(parsed.getKind()) ? root.rel : root.project();
        }

        /// <summary>
        /// Parses, validates and converts a statement to a logical tree.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <returns>The logical tree.</returns>
        public RelNode ToRel(string sql) => ToRel(Parse(sql));

        /// <summary>
        /// The rewrites a host applies between the converter and the search.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Calcite's standard program does this in a <see cref="HepPlanner"/> pass rather than in the
        /// cost-based search, and so does this, because a sub-query is not an alternative to anything:
        /// the converter leaves one as a <c>RexSubQuery</c> inside a predicate, no convention has a
        /// rule that matches a node containing one, and a planner asked for such a tree reports that
        /// it has not enough rules rather than choosing badly. Removing them exhaustively first and
        /// searching afterwards is what a host does and what makes the correlated statements in the
        /// corpus plannable at all.
        /// </para>
        /// <para>
        /// Its own stage, and benchmarked as one, because it is a rewriting pass with no costing in
        /// it: time spent here is not time spent searching, and a statement with no sub-query pays
        /// only for the pattern match that finds none.
        /// </para>
        /// </remarks>
        /// <param name="rel">The converter's output.</param>
        /// <returns>The tree the search is given.</returns>
        public static RelNode Rewrite(RelNode rel)
        {
            var program = new HepProgramBuilder()
                .addRuleInstance(CoreRules.FILTER_SUB_QUERY_TO_CORRELATE)
                .addRuleInstance(CoreRules.PROJECT_SUB_QUERY_TO_CORRELATE)
                .addRuleInstance(CoreRules.JOIN_SUB_QUERY_TO_CORRELATE)
                .build();

            var hep = new HepPlanner(program);
            hep.setRoot(rel);

            return hep.findBestExp();
        }

        /// <summary>
        /// Parses, validates, converts and rewrites a statement — everything before the search.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <returns>The tree the search is given.</returns>
        public RelNode ToLogical(string sql) => Rewrite(ToRel(sql));

        /// <summary>
        /// Registers every container's conversion rules on a planner.
        /// </summary>
        /// <remarks>
        /// Every container, whether or not the statement names it. A rule that is only correct when
        /// its container's rules were registered last is not correct, and a host registers what its
        /// schema holds rather than what the statement turned out to touch. It is also the honest
        /// measurement: rule registration is per plan, and its cost grows with the schema.
        /// </remarks>
        /// <param name="planner">The planner.</param>
        public void AddCosmosRules(RelOptPlanner planner)
        {
            foreach (var table in _tables.Values)
                foreach (var rule in CosmosRules.GetRules(table.Convention))
                    planner.addRule(rule);
        }

        /// <summary>
        /// Registers the asynchronous convention's rules on a planner.
        /// </summary>
        /// <param name="planner">The planner.</param>
        public static void AddAsyncRules(RelOptPlanner planner)
        {
            foreach (var rule in ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);

            // The window rule above matches a LogicalWindow, and the converter does not produce one:
            // it puts the RexOver in a projection and leaves this rewrite to lift it out. Registered
            // here rather than with the adapter's rules because nothing about it is the adapter's —
            // it is in every host's rule set, and without it a statement with an OVER clause reports
            // that there are not enough rules.
            planner.addRule(CoreRules.PROJECT_TO_LOGICAL_PROJECT_AND_WINDOW);
        }

        /// <summary>
        /// Plans a statement for the asynchronous convention, with every rule a host would have.
        /// </summary>
        /// <remarks>
        /// The measurement that matters. A host asks for <c>ClrAsyncEnumerableConvention</c>, never for
        /// the Cosmos one, so the planner has to reach the pushed form and the in-process form both,
        /// cost them against each other, and choose — which is the work these benchmarks exist to
        /// time. It also cannot fail for want of a plan: reading the container and doing everything
        /// here is always available.
        /// </remarks>
        /// <param name="sql">The statement.</param>
        /// <param name="reorderJoins">Whether to also register the rule that lets the planner swap a
        /// join's sides. Off by default, because a host on Calcite's standard rule set does not have
        /// it: reordering is behind a system property there.</param>
        /// <returns>The best plan.</returns>
        public RelNode PlanToAsync(string sql, bool reorderJoins = false)
        {
            var logical = ToLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            AddCosmosRules(planner);
            AddAsyncRules(planner);

            if (reorderJoins)
                AddJoinOrderRules(planner);

            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Registers the rule that lets the planner swap a join's sides.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The classic reason a cost-based planner is expensive, and the one thing in this harness a
        /// default host does not do. With commutation registered an <c>n</c>-way chain has two
        /// choices per join and Volcano costs all of them; without it the sides are whatever the
        /// statement said.
        /// </para>
        /// <para>
        /// Worth measuring, and worth measuring separately, because for this adapter the swap is not
        /// a detail. Which side is the probe decides whether the join fetches keys or reads a
        /// container, and that is the largest difference in cost the adapter can make — so what it
        /// would cost to let the planner choose is a real question rather than a hypothetical one.
        /// </para>
        /// <para>
        /// Commutation only. <c>JOIN_ASSOCIATE</c> would complete the reordering and cannot be used
        /// here: its operand is <c>Join</c> rather than <c>LogicalJoin</c>, so it also matches the
        /// physical joins the CLR rules have already produced and rewrites them into a tree the
        /// planner rejects as mixing the two.
        /// </para>
        /// </remarks>
        /// <param name="planner">The planner.</param>
        public static void AddJoinOrderRules(RelOptPlanner planner)
        {
            planner.addRule(CoreRules.JOIN_COMMUTE);
        }

        /// <summary>
        /// Plans a statement for the Cosmos convention alone.
        /// </summary>
        /// <remarks>
        /// Pushdown with the alternative removed. Where a plan exists it is one the service answers
        /// whole, so this times the rules rather than the comparison between them and the CLR ones —
        /// and where none exists the planner throws, which is this harness's way of saying the
        /// statement does not push. Use <see cref="PlanToAsync"/> for anything that might not.
        /// </remarks>
        /// <param name="sql">The statement.</param>
        /// <returns>The best plan.</returns>
        public RelNode PlanToCosmos(string sql)
        {
            var logical = ToLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            AddCosmosRules(planner);

            var convention = ConventionOf(logical) ?? throw new InvalidOperationException("The statement names no Cosmos container.");
            var desired = logical.getTraitSet().replace(convention).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Renders a planned Cosmos subtree to the statement it would execute.
        /// </summary>
        /// <param name="rel">A tree rooted at a node in the Cosmos convention.</param>
        /// <param name="container">The container the subtree reads.</param>
        /// <returns>The query.</returns>
        public static CosmosQuery Implement(RelNode rel, CosmosContainerMetadata container)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), container);
            implementor.Visit(rel);
            return implementor.Build();
        }

        /// <summary>
        /// Returns the convention of the first Cosmos table scanned by a tree, or <c>null</c>.
        /// </summary>
        /// <param name="rel">The tree.</param>
        /// <returns>The convention, or <c>null</c> where the tree scans no container.</returns>
        public static CosmosConvention? ConventionOf(RelNode rel)
        {
            var table = TableOf(rel);
            return table?.Convention;
        }

        /// <summary>
        /// Returns the first Cosmos table scanned by a tree, or <c>null</c>.
        /// </summary>
        /// <param name="rel">The tree.</param>
        /// <returns>The table, or <c>null</c>.</returns>
        public static CosmosTable? TableOf(RelNode rel)
        {
            if (rel.getTable() is RelOptTable optTable && optTable.unwrap(typeof(CosmosTable)) is CosmosTable found)
                return found;

            var inputs = rel.getInputs();

            for (var i = 0; i < inputs.size(); i++)
                if (TableOf((RelNode)inputs.get(i)) is CosmosTable inner)
                    return inner;

            return null;
        }

        /// <summary>
        /// Determines whether any node of a tree is in the Cosmos convention.
        /// </summary>
        /// <remarks>
        /// What makes a corpus entry a pushdown benchmark rather than a planning benchmark. A
        /// statement that plans in a millisecond and pushes nothing is timing the CLR rules.
        /// </remarks>
        /// <param name="rel">The tree.</param>
        /// <returns><c>true</c> if the plan pushes anything to the service.</returns>
        public static bool Pushes(RelNode rel)
        {
            if (rel is CosmosRel)
                return true;

            var inputs = rel.getInputs();

            for (var i = 0; i < inputs.size(); i++)
                if (Pushes((RelNode)inputs.get(i)))
                    return true;

            return false;
        }

        /// <summary>
        /// Renders a tree to its plan text.
        /// </summary>
        /// <param name="rel">The tree.</param>
        /// <returns>The plan.</returns>
        public static string Explain(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

    }

}
