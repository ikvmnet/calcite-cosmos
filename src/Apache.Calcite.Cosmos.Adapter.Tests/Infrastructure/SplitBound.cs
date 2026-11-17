using System;

using Apache.Calcite.Cosmos.Adapter.Rel.Convert;

using org.apache.calcite.plan;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// Bounds how often a planner may split a filter: past the bound it throws, so a statement whose split would fire on its
    /// own outer without end fails its test, where it overflowed the stack and took the test process with it.
    /// </summary>
    /// <remarks>
    /// Added to a planner a test builds, or to the one a connection builds through <c>Hook.PLANNER</c>, which hands it to
    /// <see cref="accept"/>.
    /// </remarks>
    sealed class SplitBound(int bound) : RelOptListener, java.util.function.Consumer
    {

        int _splits;

        /// <summary>Gets how many splits the planner made.</summary>
        public int Splits => _splits;

        public void accept(object o)
        {
            if (o is RelOptPlanner planner)
                planner.addListener(this);
        }

        public java.util.function.Consumer andThen(java.util.function.Consumer after) => throw new NotSupportedException();

        public void ruleProductionSucceeded(RelOptListener.RuleProductionEvent e)
        {
            if (e.isBefore() || e.getRuleCall().getRule() is not CosmosFilterSplitRule)
                return;

            if (++_splits > bound)
                throw new InvalidOperationException($"The planner split a filter more than {bound} times.");
        }

        public void relEquivalenceFound(RelOptListener.RelEquivalenceEvent e) { }

        public void ruleAttempted(RelOptListener.RuleAttemptedEvent e) { }

        public void relDiscarded(RelOptListener.RelDiscardedEvent e) { }

        public void relChosen(RelOptListener.RelChosenEvent e) { }

    }

}
