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
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;

using FluentAssertions;

using org.apache.calcite.rel;
using org.apache.calcite.rex;

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
        /// <param name="reasoning">The reasoning every container is bound to, or <c>null</c> for the suite's shared one.</param>
        public static IEnumerable<string> All(int partitionedPredicates, CosmosReasoning? reasoning = null) =>
            Lines(RenderingInputs.All(partitionedPredicates, reasoning));

        /// <summary>
        /// The planner corpus's digests, one line a statement.
        /// </summary>
        /// <param name="reasoning">The reasoning every container is bound to, or <c>null</c> for the suite's shared one.</param>
        public static IEnumerable<string> Corpus(CosmosReasoning? reasoning = null) => Lines(RenderingInputs.Corpus(reasoning));

        /// <summary>
        /// The digests of the statements given, one line a statement.
        /// </summary>
        public static IEnumerable<string> Lines(IEnumerable<RenderingInputs.Statement> statements) =>
            statements.Select(s => Line(s.Input, s.Name, s.Sql, Of(s.Plan)));

        /// <summary>
        /// Plans the statements twice, each time in a reasoning of their own, the second under a switch; holds that the
        /// switch reached the planner, and that every digest is the same.
        /// </summary>
        public static void Unchanged(Func<IDisposable> arm, Func<CosmosReasoning, IEnumerable<RenderingInputs.Statement>> statements, Action<CosmosReasoning> reached)
        {
            var off = Lines(statements(new CosmosReasoning())).ToList();
            var armed = new CosmosReasoning();
            List<string> on;
            using (arm())
                on = Lines(statements(armed)).ToList();

            reached(armed);
            on.Should().Equal(off);
        }

        static string Line(string input, string name, string sql, PlanDigest digest) =>
            $"{input}\t{name}\t{PlanDigest.Escape(sql)}\t{digest}";

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
        /// Whether a plan holds a condition a filter statement could be read off: a filter, or a join, correlate or unnest,
        /// which may carry one. What "no filter" means to the cost lines (line 27), read off the plan, never off what S was asked.
        /// </summary>
        internal static bool Filters(RelNode planned) => Subtrees(planned, n => n is org.apache.calcite.rel.core.Filter or org.apache.calcite.rel.core.Join
            or org.apache.calcite.rel.core.Correlate or CosmosUnnest).Any();

        /// <summary>
        /// The nodes a predicate holds of, in a walk from the root, left to right; beneath one only where asked.
        /// </summary>
        internal static IEnumerable<RelNode> Subtrees(RelNode node, Func<RelNode, bool> match, bool descend = false)
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

        internal static string Failure(Exception e) => e.GetType().Name + ": " + e.Message.Split('\n')[0].TrimEnd('\r');

    }

}
