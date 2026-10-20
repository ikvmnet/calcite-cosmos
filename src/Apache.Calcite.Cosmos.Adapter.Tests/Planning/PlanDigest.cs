using System.Collections.Generic;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// What one statement's plan is, as far as a commit that claims to change no plan could change it.
    /// </summary>
    /// <param name="Plan">The chosen plan's text, its newlines normalised; or why it failed to plan.</param>
    /// <param name="Statements">Each Cosmos statement the plan builds: its text, its parameter values in order, and
    /// where it is sent.</param>
    /// <param name="Conjuncts">Each conjunct of each filter the plan pushes: the filter, the conjunct's index, the round
    /// that admitted it, whether it stayed residual, and its IR digest. A round can move without moving the text a
    /// conjunct is written as, since what it renders from is what moved.</param>
    internal sealed record PlanDigest(string Plan, IReadOnlyList<string> Statements, IReadOnlyList<string> Conjuncts)
    {

        /// <summary>
        /// The digest as three tab-separated fields, each on one line.
        /// </summary>
        public override string ToString() =>
            Escape(Plan) + "\t" + Escape(string.Join("\n", Statements)) + "\t" + Escape(string.Join("\n", Conjuncts));

        /// <summary>
        /// Escapes a field so that it holds no tab and no line break.
        /// </summary>
        internal static string Escape(string text) =>
            text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    }

}
