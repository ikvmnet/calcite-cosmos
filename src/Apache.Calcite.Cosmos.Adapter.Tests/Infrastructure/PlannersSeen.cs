using System;
using System.Collections.Generic;

using org.apache.calcite.plan;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// Remembers the planners a statement was planned by: one a test builds, told of its rules as their listener, or one a
    /// connection builds, handed over through <c>Hook.PLANNER</c>.
    /// </summary>
    sealed class PlannersSeen : RelOptListener, java.util.function.Consumer
    {

        readonly List<RelOptPlanner> _planners = [];

        /// <summary>Gets the planners seen, in the order seen.</summary>
        public IReadOnlyList<RelOptPlanner> Planners => _planners;

        void Saw(RelOptPlanner planner)
        {
            if (_planners.Contains(planner) == false)
                _planners.Add(planner);
        }

        public void accept(object o)
        {
            if (o is RelOptPlanner planner)
                Saw(planner);
        }

        public java.util.function.Consumer andThen(java.util.function.Consumer after) => throw new NotSupportedException();

        public void ruleAttempted(RelOptListener.RuleAttemptedEvent e) => Saw(e.getRuleCall().getPlanner());

        public void ruleProductionSucceeded(RelOptListener.RuleProductionEvent e) { }

        public void relEquivalenceFound(RelOptListener.RelEquivalenceEvent e) { }

        public void relDiscarded(RelOptListener.RelDiscardedEvent e) { }

        public void relChosen(RelOptListener.RelChosenEvent e) { }

    }

}
