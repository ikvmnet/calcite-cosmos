using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;

using Apache.Calcite.Extensions.Adapter.Cursor;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.util;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// Plans every statement the rendering check reads, executes none, and digests each plan: the planner corpus, the
    /// differential's statements without parameters, ternary logic partitioning's, and the one kind probe the
    /// differential leaves out.
    /// </summary>
    /// <remarks>
    /// Each is planned as the test that runs it plans it: the corpus through <see cref="PlannerHarness"/>, the
    /// differential's and the partitions' as <c>CosmosDifferentialTests.Run</c> plans them over every container it
    /// seeds, and the probe as <see cref="CosmosPushdownProbeTests"/> does. The order is fixed, so two runs compare line
    /// by line.
    /// </remarks>
    internal static class PlanDigests
    {

        /// <summary>
        /// Every digest, one line a statement: the input, the statement's name or index, its text, and its digest.
        /// </summary>
        /// <param name="partitionedPredicates">How many of the partition run's predicates to plan the statements of.</param>
        public static IEnumerable<string> All(int partitionedPredicates)
        {
            foreach (var line in Corpus())
                yield return line;

            var statements = CosmosDifferentialTests.Statements;
            for (var i = 0; i < statements.Count; i++)
                yield return Line("differential", i, statements[i], Of(() => Differential(statements[i])));

            var partitions = CosmosDifferentialTests.PartitionStatements(partitionedPredicates);
            for (var i = 0; i < partitions.Count; i++)
                yield return Line("partitions", i, partitions[i], Of(() => Differential(partitions[i])));

            var kinds = CosmosDifferentialTests.Containers.Single(c => c.Name == "kinds");
            foreach (var (name, where) in CosmosPushdownProbeTests.KindProbes.Where(p => p.Where.Contains("IS_DEFINED", StringComparison.Ordinal)))
            {
                var sql = $"SELECT c.\"id\" FROM kinds AS c WHERE {where}";
                yield return Line("probe", name, sql, Of(() => CosmosPushdownProbeTests.Plan(sql, kinds)));
            }
        }

        /// <summary>
        /// The planner corpus's digests, one line a statement.
        /// </summary>
        public static IEnumerable<string> Corpus()
        {
            var harness = new PlannerHarness();
            foreach (var query in PlannerQueries.All)
                yield return Line("corpus", query.Name, query.Sql, Of(() => harness.PlanToAsync(query.Sql)));
        }

        static string Line(string input, object name, string sql, PlanDigest digest) =>
            $"{input}\t{name}\t{PlanDigest.Escape(sql)}\t{digest}";

        /// <summary>
        /// The planning half of <c>CosmosDifferentialTests.Run</c> with the pushdown rules: the same schema, operators,
        /// traits, rules and calc pass, and nothing executed.
        /// </summary>
        static RelNode Differential(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var tables = CosmosDifferentialTests.Containers.Select(c => new CosmosTable(c)).ToList();

            var rootSchema = CalciteSchema.createRootSchema(false);
            foreach (var table in tables)
                rootSchema.add(table.Container.Name, table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var operators = SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(java.util.EnumSet.of(SqlLibrary.MYSQL, SqlLibrary.SPARK, SqlLibrary.HIVE, SqlLibrary.BIG_QUERY)));
            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = PlannerHarness.CreatePlanner();
            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var table in tables)
                foreach (var rule in CosmosRules.GetRules(table.Convention))
                    planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify()));

            var program = new HepProgramBuilder();
            foreach (var rule in ClrCursorRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new HepPlanner(program.build());
            hep.setRoot(planner.findBestExp());
            return hep.findBestExp();
        }

        /// <summary>
        /// Plans a statement and digests the plan: its text, each Cosmos subtree implemented, and each pushed filter's
        /// conjuncts as the statement the implementor renders from reads them.
        /// </summary>
        static PlanDigest Of(Func<RelNode> plan)
        {
            RelNode planned;
            try { planned = plan(); }
            catch (Exception e) { return new PlanDigest("fails: " + Failure(e), [], []); }

            var statements = new List<string>();
            var conjuncts = new List<string>();
            var filters = 0;

            foreach (var cosmos in Subtrees(planned, n => n is CosmosRel))
            {
                if (PlannerHarness.TableOf(cosmos) is not CosmosTable table)
                    continue;

                try
                {
                    statements.Add(Spell(PlannerHarness.Implement(cosmos, table.Container)));

                    foreach (CosmosFilter filter in Subtrees(cosmos, n => n is CosmosFilter, descend: true))
                        conjuncts.AddRange(ConjunctsOf(filter, cosmos.getCluster().getRexBuilder(), table.Container, filters++));
                }
                catch (Exception e)
                {
                    statements.Add("implements: " + Failure(e));
                }
            }

            return new PlanDigest(PlannerHarness.Explain(planned), statements, conjuncts);
        }

        static List<string> ConjunctsOf(CosmosFilter filter, RexBuilder rexBuilder, CosmosContainerMetadata container, int index)
        {
            // The statement the implementor rendered the filter from, remembered against the condition and the binding it
            // holds once the filter's input is implemented. A binding walked off the plan instead leaves an array's element
            // unbound, which the implementor binds to its alias, and reads another statement.
            var implementor = new CosmosImplementor(rexBuilder, container);
            implementor.Visit(filter.getInput());

            var statement = CosmosFilterStatement.Of(filter.getCondition(), implementor.Fields, implementor.Readings, container, rexBuilder, implementor.RootAlias);
            var lines = Enumerable.Range(0, statement.Conjuncts.Count)
                .Select(i => $"{index}.{i} {statement.StratumOf(i)} {(statement.IsAdmitted(i) ? "admitted" : "residual")} {statement.Statement.Conjuncts[i].Digest}")
                .ToList();

            GC.KeepAlive(statement);
            return lines;
        }

        /// <summary>
        /// The nodes a predicate holds of, in a walk from the root, left to right; beneath one only where asked.
        /// </summary>
        static IEnumerable<RelNode> Subtrees(RelNode node, Func<RelNode, bool> match, bool descend = false)
        {
            if (match(node))
            {
                yield return node;
                if (descend == false)
                    yield break;
            }

            for (var i = 0; i < node.getInputs().size(); i++)
                foreach (var found in Subtrees((RelNode)node.getInputs().get(i), match, descend))
                    yield return found;
        }

        /// <summary>
        /// A built statement: its text, its parameters in order, and where it is sent, a stored 30 and a stored "30"
        /// being two partitions.
        /// </summary>
        static string Spell(CosmosQuery query)
        {
            var text = query.Sql + " | " + string.Join(", ", query.Parameters.Select(p => p.Name + " = " + Spell(p.Value)));

            if (query.PartitionKeyValues is { } key)
                text += " | routes " + string.Join(", ", key.Select(Spell)) + (query.PartitionKeyIsComplete ? "" : " (a prefix)");
            if (query.PointReadId is { } id)
                text += " | reads " + id;
            if (query.PointReadIds is { } ids)
                text += " | reads many " + string.Join(", ", ids);

            return text;
        }

        static string Spell(object? value) => value switch
        {
            null => "null",
            string s => JsonSerializer.Serialize(s),
            bool b => b ? "true" : "false",
            IFormattable f => value.GetType().Name + " " + f.ToString(null, CultureInfo.InvariantCulture),
            IDictionary d => "{" + string.Join(", ", d.Cast<DictionaryEntry>().Select(e => Spell(e.Key) + ": " + Spell(e.Value))) + "}",
            IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Spell)) + "]",
            _ => value.GetType().Name + " " + value,
        };

        static string Failure(Exception e) => e.GetType().Name + ": " + e.Message.Split('\n')[0].TrimEnd('\r');

    }

}
