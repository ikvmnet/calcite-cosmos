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
    /// Compiles a constraint's SQL against a container, and answers what a compiled one licenses.
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
    /// to a factory, so a constraint compiled for one planner is compiled again for another — once.
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

        static readonly ConditionalWeakTable<RelDataTypeFactory, ConcurrentDictionary<(CosmosTable, CosmosConstraint.Unique), Compiled>> Cache = new();

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

            return Cache.GetOrCreateValue(rexBuilder.getTypeFactory()).GetOrAdd((table, unique), key => CompileCore(key.Item2, key.Item1, rexBuilder));
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

        /// <summary>
        /// Determines whether a predicate proves another, over a container's documents.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Calcite's <see cref="RexImplicationChecker"/> decides it — the checker materialized-view matching
        /// uses, which answers no where it cannot tell. It reasons about comparisons of columns, and a
        /// predicate over a document compares accessor calls, so each distinct term is first stood for by a
        /// column of its own, the same column wherever the same term appears in either predicate. That is
        /// sound because a deterministic term is one function of the document: two equal terms are one value.
        /// </para>
        /// <para>
        /// A premise of <c>null</c> is every document, which proves only what is always true.
        /// </para>
        /// </remarks>
        /// <param name="rexBuilder">The builder.</param>
        /// <param name="premise">What is known, or <c>null</c>.</param>
        /// <param name="conclusion">What is to be proved.</param>
        /// <returns><c>true</c> where every document satisfying the premise satisfies the conclusion.</returns>
        public static bool Implies(RexBuilder rexBuilder, RexNode? premise, RexNode conclusion)
        {
            if (conclusion.isAlwaysTrue())
                return true;

            if (premise is null)
                return false;

            premise = RexUtil.expandSearch(rexBuilder, null, premise);
            conclusion = RexUtil.expandSearch(rexBuilder, null, conclusion);

            // Every conjunct of the conclusion already among the premise's is the common case — the
            // view writes the very predicate the constraint names — and needs no checker.
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (RexNode conjunct in RelOptUtil.conjunctions(premise).toArray())
                known.Add(conjunct.ToString());

            var all = true;
            foreach (RexNode conjunct in RelOptUtil.conjunctions(conclusion).toArray())
                all &= known.Contains(conjunct.ToString());

            if (all)
                return true;

            var terms = new Abstract(rexBuilder);
            var p = (RexNode)premise.accept(terms);
            var c = (RexNode)conclusion.accept(terms);

            if (terms.Failed)
                return false;

            try
            {
                return new RexImplicationChecker(rexBuilder, RexUtil.EXECUTOR, terms.RowType()).implies(p, c);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Replaces each maximal term under a comparison by a column standing for it.
        /// </summary>
        sealed class Abstract : RexShuttle
        {

            readonly RexBuilder _rexBuilder;
            readonly Dictionary<string, RexInputRef> _columns = new(StringComparer.Ordinal);
            readonly List<RelDataType> _types = new();

            public Abstract(RexBuilder rexBuilder)
            {
                _rexBuilder = rexBuilder;
            }

            public bool Failed { get; private set; }

            public RelDataType RowType()
            {
                var builder = _rexBuilder.getTypeFactory().builder();
                for (var i = 0; i < _types.Count; i++)
                    builder.add("t" + i, _types[i]);

                return builder.build();
            }

            public override RexNode visitCall(RexCall call)
            {
                var kind = call.getKind();

                if (kind == SqlKind.AND || kind == SqlKind.OR || kind == SqlKind.NOT)
                    return base.visitCall(call);

                if (kind == SqlKind.EQUALS || kind == SqlKind.NOT_EQUALS || kind == SqlKind.LESS_THAN || kind == SqlKind.LESS_THAN_OR_EQUAL
                    || kind == SqlKind.GREATER_THAN || kind == SqlKind.GREATER_THAN_OR_EQUAL || kind == SqlKind.IS_NULL || kind == SqlKind.IS_NOT_NULL)
                {
                    var operands = new java.util.ArrayList();
                    for (var i = 0; i < call.getOperands().size(); i++)
                        operands.add(Term((RexNode)call.getOperands().get(i)));

                    return call.clone(call.getType(), operands);
                }

                // Anything else as a whole condition is a term that is true or not, which the checker
                // cannot reason about but can still match against itself.
                return Term(call);
            }

            public override RexNode visitInputRef(RexInputRef inputRef) => Term(inputRef);

            RexNode Term(RexNode node)
            {
                if (node is RexLiteral)
                    return node;

                if (RexUtil.isDeterministic(node) == false)
                {
                    Failed = true;
                    return node;
                }

                var digest = node.ToString() + ":" + node.getType().getFullTypeString();
                if (_columns.TryGetValue(digest, out var column) == false)
                {
                    column = _rexBuilder.makeInputRef(node.getType(), _types.Count);
                    _types.Add(node.getType());
                    _columns[digest] = column;
                }

                return column;
            }

        }

    }

}
