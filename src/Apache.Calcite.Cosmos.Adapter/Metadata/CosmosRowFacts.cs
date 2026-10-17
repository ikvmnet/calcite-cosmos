using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What the rows a relation yields are known to satisfy: the last stratum of every filter they have passed, which is
    /// what a projection, a sort and a grouping over them may lean on, and a key a lookup restricts them by (L1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WHERE runs before SELECT, ORDER BY and GROUP BY</b>, so a clause after a filter reads only rows the filter
    /// kept, and what the filter's pushed conjuncts establish holds of each of them: a sort over <c>t IS NOT NULL</c> has
    /// no null to place, and a projection over <c>kind = 'a'</c> reads a path whose form kind <c>a</c> declares. The facts
    /// are the oracle's last stratum of each filter (<see cref="CosmosFilterStatement.After"/>) — every conjunct a stratum
    /// admitted, which is every one the service applies — and the declaration where there is no filter.
    /// </para>
    /// <para>
    /// <b>Read through the input as the binding is read</b>: a set's original member, then through a filter, a projection
    /// and a sort, each of which keeps the rows it is given or fewer, and the documents behind them. A join, an aggregate,
    /// a traversal or anything else is a relation of other rows, and the walk stops there. Every member of a set yields the
    /// same rows, so a filter found in one holds of whichever the planner chooses, whether or not it is the one that reached
    /// the service. A filter is read as the filter rule pushes it, its condition rewritten (<see cref="CosmosFactRewriter"/>),
    /// which is remembered against the condition, so the statement read here is the one the filter rule already admitted.
    /// </para>
    /// <para>
    /// <b>Asked once, by the rule, and carried.</b> Which filters a walk finds depends on which members it reads, and a
    /// rule decides on the planner's sets while implementation sees the one tree it chose — whose filter may be the same
    /// condition rewritten another way, or reduced. A clause that rendered under one set of facts and implemented under
    /// another is a plan that fails after the planner committed to it, so the node a rule builds carries the facts the rule
    /// decided under, as <see cref="CosmosSort"/> always has, and implementation reads those. A question
    /// another node asks of this one — the binding a <c>DISTINCT</c> gives its keys — is no such clause: it is answered on
    /// both walks, and reads the declaration (<see cref="CosmosAggregate.DistinctKeyBinding"/>).
    /// </para>
    /// <para>
    /// <b>Several filters are read as one statement</b>, of the conjuncts each admitted, read apart and assumed together:
    /// what every one of them keeps (<see cref="OracleLicence.Kept"/>), which is what each filter's last stratum keeps,
    /// together. What is opaque is numbered per filter, each over its own binding, so each filter's opaque terms are
    /// numbered apart from the others': two numbered alike in different filters are different predicates, which the oracle
    /// would otherwise take for one.
    /// </para>
    /// </remarks>
    internal static class CosmosRowFacts
    {

        /// <summary>
        /// What each clause a rule has asked about was decided under, by the clause, and by what it asked: the container,
        /// the licence and which input.
        /// </summary>
        static readonly ConditionalWeakTable<RelNode, Dictionary<(CosmosContainerMetadata? Container, OracleQuestion Question, int Input), JsonFactSet>> Decided = new();

        /// <summary>
        /// Returns what holds of every row a clause reads, the same each time it is asked of the same clause.
        /// </summary>
        /// <remarks>
        /// A converter rule decides whether a clause converts in its operand's predicate and builds the node later, and a
        /// set the walk reads through may have been merged with another in between, changing the member it reads. Asked
        /// of the clause rather than of its input, the answer is the first one, so the node is built under the facts it
        /// was accepted under.
        /// </remarks>
        /// <param name="clause">The projection, sort or grouping, or the join whose key restricts a side.</param>
        /// <param name="container">The container its input reads, or <c>null</c>.</param>
        /// <param name="licence">The clause's licence, as <see cref="Of"/> takes it.</param>
        /// <param name="input">Which of the clause's inputs it reads: its one input, or the side a join restricts.</param>
        /// <returns>The facts.</returns>
        public static JsonFactSet For(RelNode clause, CosmosContainerMetadata? container, OracleLicence licence, int input = 0)
        {
            var decided = Decided.GetValue(clause ?? throw new ArgumentNullException(nameof(clause)), _ => new());
            var key = (container, licence.Question, input);

            lock (decided)
            {
                if (decided.TryGetValue(key, out var facts) == false)
                    decided[key] = facts = Of(clause.getInput(input), container, licence);

                return facts;
            }
        }

        /// <summary>
        /// Returns what holds of every row a relation yields, for a clause after it.
        /// </summary>
        /// <param name="input">The relation the clause reads: its input, which may be an equivalence set.</param>
        /// <param name="container">The container the relation reads, or <c>null</c>, which knows nothing.</param>
        /// <param name="licence">The clause asking: <see cref="OracleLicence.Select"/>, <see cref="OracleLicence.OrderBy"/>,
        /// <see cref="OracleLicence.GroupBy"/> or <see cref="OracleLicence.Key"/>.</param>
        /// <returns>The facts.</returns>
        /// <exception cref="ArgumentException"><paramref name="licence"/> is not a clause after a filter.</exception>
        public static JsonFactSet Of(RelNode? input, CosmosContainerMetadata? container, OracleLicence licence)
        {
            if (licence.ReadsKeptRows == false)
                throw new ArgumentException("Only a projection, a sort, a grouping or a key reads the rows a filter kept.", nameof(licence));

            if (container is null)
                return JsonFactSet.Empty;

            var oracle = CosmosOracle.For(container.Facts);
            var filters = Beneath(input, container);

            return filters.Count switch
            {
                0 => oracle.Declared,
                1 => filters[0].After(licence),
                _ => oracle.Admit(Together(filters)).Facts(OracleLicence.Kept),
            };
        }

        /// <summary>
        /// Returns the filters a relation's rows have passed, nearest first, each as the filter rule pushes it.
        /// </summary>
        static List<CosmosFilterStatement> Beneath(RelNode? input, CosmosContainerMetadata container)
        {
            var filters = new List<CosmosFilterStatement>();

            // Bounded as a plan is deep, not as it is wide: one member per set, so a graph cannot loop it.
            var node = input;
            for (var depth = 0; node is not null && depth < 64; depth++)
            {
                if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                    node = subset.getOriginal() ?? subset.getBest();

                switch (node)
                {
                    case Filter filter:
                    {
                        if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _, out _, out var read) == false
                            || ReferenceEquals(read, container) == false)
                            return filters;

                        var rexBuilder = filter.getCluster().getRexBuilder();
                        var condition = filter is CosmosFilter
                            ? filter.getCondition()
                            : CosmosFactRewriter.Rewrite(filter.getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);

                        filters.Add(CosmosFilterStatement.Of(condition, fields, readings, container, rexBuilder));
                        node = filter.getInput();
                        break;
                    }

                    case Project or Sort:
                        node = node.getInput(0);
                        break;

                    default:
                        return filters;
                }
            }

            return filters;
        }

        /// <summary>
        /// The conjuncts each filter admitted, as one statement, each filter's opaque terms numbered apart.
        /// </summary>
        static PredicateStatement Together(IReadOnlyList<CosmosFilterStatement> filters) =>
            new(filters.SelectMany((filter, k) => filter.Admitted.Select(conjunct => Apart(conjunct, (long)k << 32))).ToArray());

        /// <summary>
        /// A predicate with every opaque term in it numbered from an offset.
        /// </summary>
        static SqlPredicate Apart(SqlPredicate predicate, long offset) => predicate switch
        {
            SqlPredicate.Opaque opaque => new SqlPredicate.Opaque(offset + opaque.Id),
            SqlPredicate.Not not => new SqlPredicate.Not(Apart(not.Operand, offset)),
            SqlPredicate.And and => SqlPredicate.AllOf(and.Arms.Select(arm => Apart(arm, offset))),
            SqlPredicate.Or or => SqlPredicate.AnyOf(or.Arms.Select(arm => Apart(arm, offset))),
            _ => predicate,
        };

    }

}
