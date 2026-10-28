using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.adapter.java;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Compiles a constraint's SQL against a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The expressions are Calcite's own.</b> <c>UNIQUE (k₁, …) WHERE p</c> is compiled as
    /// <c>SELECT k₁, … FROM container WHERE p</c>: parsed, validated against the container's row type, and
    /// converted, so the keys and the predicate arrive as expressions over the same scan a query reads — the
    /// same form a view's projection and filter take, and comparable with them by structure.
    /// </para>
    /// <para>
    /// <b>Compiled per type factory, and remembered.</b> An expression carries its types, and types belong
    /// to a factory, so a constraint compiled for one planner is compiled again for another — once. Remembered
    /// weakly under both the factory and the table: a table reaches its schema's reasoning, and a caller may pass
    /// a factory that lives for the process, so a factory that outlives a table keeps none of it.
    /// </para>
    /// </remarks>
    public static class CosmosConstraintCompiler
    {

        /// <summary>
        /// A constraint compiled against a container: key expressions, and the predicate, over its scan.
        /// </summary>
        /// <param name="Keys">The key expressions.</param>
        /// <param name="Filter">The predicate, or <c>null</c> for every document.</param>
        public sealed record Compiled(IReadOnlyList<RexNode> Keys, RexNode? Filter);

        static readonly ConditionalWeakTable<RelDataTypeFactory, ConditionalWeakTable<CosmosTable, ConcurrentDictionary<CosmosConstraint.Unique, Compiled>>> Cache = new();

        /// <summary>
        /// Compiles a <c>UNIQUE</c> constraint against a container's table, for expressions built by the
        /// given builder.
        /// </summary>
        /// <param name="unique">The constraint.</param>
        /// <param name="table">The container's table.</param>
        /// <param name="rexBuilder">The builder whose type factory the expressions must share.</param>
        /// <returns>The compiled constraint.</returns>
        /// <exception cref="ArgumentException">The constraint's SQL does not compile against the container.</exception>
        public static Compiled Compile(CosmosConstraint.Unique unique, CosmosTable table, RexBuilder rexBuilder)
        {
            if (unique is null)
                throw new ArgumentNullException(nameof(unique));
            if (table is null)
                throw new ArgumentNullException(nameof(table));
            if (rexBuilder is null)
                throw new ArgumentNullException(nameof(rexBuilder));

            return Cache.GetOrCreateValue(rexBuilder.getTypeFactory()).GetOrCreateValue(table).GetOrAdd(unique, u => CompileCore(u, table, rexBuilder));
        }

        /// <summary>
        /// Compiles every declared constraint of a container, so that one that does not compile is reported
        /// when the model is read rather than at the first query that would have used it.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <exception cref="ArgumentException">A constraint does not compile.</exception>
        public static void Validate(CosmosContainerMetadata container)
        {
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            var table = new CosmosTable(container);
            var rexBuilder = new RexBuilder(new JavaTypeFactoryImpl());

            foreach (var constraint in container.Constraints.Constraints)
                if (constraint is CosmosConstraint.Unique unique)
                    CompileCore(unique, table, rexBuilder);
        }

        const string TableName = "constrained";

        static Compiled CompileCore(CosmosConstraint.Unique unique, CosmosTable table, RexBuilder rexBuilder)
        {
            var sql = $"SELECT {unique.Keys} FROM \"{TableName}\"" + (unique.Filter is null ? "" : $" WHERE {unique.Filter}");

            try
            {
                var typeFactory = (JavaTypeFactory)rexBuilder.getTypeFactory();

                var rootSchema = CalciteSchema.createRootSchema(false);
                rootSchema.add(TableName, table);

                var properties = new java.util.Properties();
                properties.setProperty("caseSensitive", "true");

                var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
                var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance);
                var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

                var cluster = RelOptCluster.create(new HepPlanner(HepProgram.builder().build()), rexBuilder);
                var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

                var parsed = SqlParser.create(sql, SqlParser.config()).parseQuery();
                var rel = converter.convertQuery(validator.validate(parsed), false, true).rel;

                if (rel is not Project project)
                    throw new ArgumentException($"'{unique.Text}' did not compile to a projection.");

                RexNode? filter = null;
                var input = project.getInput();

                if (input is Filter predicate)
                {
                    filter = predicate.getCondition();
                    input = predicate.getInput();
                }

                if (input is not TableScan)
                    throw new ArgumentException($"'{unique.Text}' reads something other than the container.");

                var keys = new List<RexNode>();
                var projects = project.getProjects();
                for (var i = 0; i < projects.size(); i++)
                {
                    var key = (RexNode)projects.get(i);
                    if (RexUtil.isDeterministic(key) == false)
                        throw new ArgumentException($"'{unique.Text}': a key must be deterministic.");

                    keys.Add(key);
                }

                if (filter is not null && RexUtil.isDeterministic(filter) == false)
                    throw new ArgumentException($"'{unique.Text}': the predicate must be deterministic.");

                return new Compiled(keys, filter);
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ArgumentException($"'{unique.Text}' does not compile against container '{table.Container.Name}': {e.Message}", e);
            }
        }

    }

}
