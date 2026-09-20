using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FluentAssertions;
using System.Threading.Tasks;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus
{

    /// <summary>
    /// Plans the whole corpus once and holds it to two things: that every statement still plans, and
    /// that the ones claimed to push wholly still do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Breadth, where the rest of the suite is depth.</b> Every other planner test asks a pointed
    /// question about one statement and asserts the answer, which catches a rule that answers wrongly
    /// and misses a rule that stops answering at all for a shape nobody wrote a test for. This walks
    /// a wide spread of statements and asks the shallowest questions of each. Neither kind subsumes
    /// the other.
    /// </para>
    /// <para>
    /// <b>Both are reported whole rather than at the first failure.</b> A rule predicate that
    /// tightens usually takes several statements with it, and the set is what identifies the rule —
    /// one name out of forty says much less than "every statement that pins a partition key". So the
    /// failures are collected and the assertion carries all of them.
    /// </para>
    /// <para>
    /// No service and no emulator: this is the planner, the metadata and the renderer, over
    /// <see cref="PlannerSchema"/>'s declared containers.
    /// </para>
    /// </remarks>
    public class PlannerCorpusTests : IClassFixture<PlannerCorpusTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown. xUnit drives these through a fixture the
        /// class asks for rather than through static hooks the framework calls by attribute.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public ValueTask InitializeAsync() { Initialize(); return default; }

            public ValueTask DisposeAsync() => default;

        }

        /// <summary>
        /// What one statement did.
        /// </summary>
        /// <param name="Query">The statement.</param>
        /// <param name="Stage">The stage that threw, or <c>ok</c>.</param>
        /// <param name="Failure">The failure, or <c>null</c>.</param>
        /// <param name="Pushes">Whether the chosen plan puts anything in the Cosmos convention.</param>
        /// <param name="PushesWhole">Whether the statement also plans wholly in the Cosmos convention.</param>
        sealed record Result(PlannerQuery Query, string Stage, Exception? Failure, bool Pushes, bool PushesWhole);

        static PlannerHarness _harness = null!;
        static IReadOnlyList<Result> _results = null!;

        /// <summary>
        /// Plans the corpus once for the whole class.
        /// </summary>
        /// <remarks>
        /// Planning forty-odd statements is seconds, most of it Calcite's type initializers on the
        /// first one, and both tests read the same pass. A harness is reusable across statements —
        /// only the validator and the planner are per statement, which <see cref="PlannerHarness"/>
        /// builds as such.
        /// </remarks>
        static void Initialize()
        {
            _harness = new PlannerHarness();

            // The first statement through pays for every type initializer Calcite has. Doing it here
            // keeps that cost out of whichever corpus entry happened to be first.
            _ = _harness.PlanToAsync("""SELECT c."id" FROM products AS c WHERE c."$.category" = 'x'""");

            _results = PlannerQueries.All.Select(Plan).ToList();
        }

        /// <summary>
        /// Runs one statement through every stage, stopping at the first that throws.
        /// </summary>
        static Result Plan(PlannerQuery query)
        {
            var stage = "parse";

            try
            {
                var parsed = _harness.Parse(query.Sql);

                stage = "convert";
                _ = _harness.ToRel(parsed);

                stage = "plan";
                var planned = _harness.PlanToAsync(query.Sql);

                var pushes = PlannerHarness.Pushes(planned);

                // Whether the statement also plans with the in-process alternative removed. Failure
                // is an answer rather than an error -- most statements do not push whole -- so it is
                // caught here and reported as false.
                var whole = true;

                try
                {
                    _ = _harness.PlanToCosmos(query.Sql);
                }
                catch (Exception)
                {
                    whole = false;
                }

                return new Result(query, "ok", null, pushes, whole);
            }
            catch (Exception e)
            {
                return new Result(query, stage, e, false, false);
            }
        }

        /// <summary>
        /// Every statement in the corpus parses, converts and plans.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cheapest question worth asking of a corpus, and the one it exists for: a statement
        /// that stopped planning is a rule set that can no longer produce a plan for a shape it once
        /// could, and nothing about it is visible from a suite of pointed tests that never wrote that
        /// shape down.
        /// </para>
        /// <para>
        /// It cannot fail for want of a cheaper plan. A host asks for the CLR convention, and reading
        /// the container and doing everything in process is always available — so a failure here is a
        /// rule that threw, a statement that stopped parsing, or a converter that could not rewrite
        /// it, never a costing that came out badly.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryStatementInTheCorpusPlans()
        {
            // Projected to text before asserting, so the report reads as a list of statements
            // rather than as a dump of the records behind them.
            var failures = _results
                .Where(r => r.Failure is not null)
                .Select(r => $"  {r.Query.Name} ({r.Query.Category}) failed at {r.Stage}: {Summarize(r.Failure!)}")
                .ToList();

            failures.Should().BeEmpty(
                "every statement in the corpus should still plan, and these did not:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// The wholly-pushed list matches what the planner does, in both directions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Drift either way is a failure, and the second direction is the one worth explaining.</b>
        /// A statement that stopped planning wholly inside the convention is a rule that regressed,
        /// which is the obvious half. A statement that <em>started</em> is a rule that improved — and
        /// still a failure, because the list is a claim about the adapter that is now wrong, and the
        /// next person to read it will believe it. Improving the adapter includes saying so here.
        /// </para>
        /// <para>
        /// Whether a statement pushes <em>anything</em> is deliberately not asserted. Several corpus
        /// entries are here precisely because they must not push, and their notes say so; a count of
        /// them is reported by <see cref="PlannerHarness.Pushes"/> for a reader, not held to a
        /// number.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheWhollyPushedListAgreesWithThePlanner()
        {
            var drifted = new List<string>();

            foreach (var result in _results.Where(r => r.Failure is null))
            {
                var expected = PlannerQueries.IsWhollyPushed(result.Query);

                if (expected == result.PushesWhole)
                    continue;

                drifted.Add(expected
                    ? $"  {result.Query.Name} is listed but no longer plans wholly in the convention"
                    : $"  {result.Query.Name} now plans wholly in the convention and is not listed");
            }

            drifted.Should().BeEmpty(
                "the wholly-pushed list in PlannerQueries should agree with the planner:" + Environment.NewLine +
                string.Join(Environment.NewLine, drifted));
        }

        /// <summary>
        /// Every name in the wholly-pushed list names a statement that exists.
        /// </summary>
        /// <remarks>
        /// <b>Without this the list can rot silently, and the check it feeds rots with it.</b> Drift
        /// is found by asking each statement whether it is listed, so a name nothing is called is
        /// never consulted — it neither matches nor fails. Rename a corpus entry and its claim stops
        /// being checked, which is the one way the guard above can pass while guarding less than it
        /// says. Caught here rather than by <see cref="PlannerQueries.WhollyPushed"/> throwing out of
        /// a property, so that the report names the offenders.
        /// </remarks>
        [Fact]
        public void EveryWhollyPushedNameNamesAStatement()
        {
            var names = PlannerQueries.All.Select(q => q.Name).ToHashSet(StringComparer.Ordinal);
            var unknown = PlannerQueries.ClaimedWhollyPushed.Where(n => names.Contains(n) == false).ToList();

            unknown.Should().BeEmpty(
                "every name in the wholly-pushed list should name a corpus statement, and these do not:" +
                Environment.NewLine + string.Join(Environment.NewLine, unknown.Select(n => "  " + n)));
        }

        /// <summary>
        /// Reduces an exception to one line.
        /// </summary>
        /// <remarks>
        /// Calcite's failures arrive wrapped several deep and the innermost one is the only
        /// informative layer, so this walks to it.
        /// </remarks>
        static string Summarize(Exception exception)
        {
            var inner = exception;

            while (inner.InnerException is not null)
                inner = inner.InnerException;

            var message = inner is java.lang.Throwable throwable ? throwable.getMessage() ?? inner.Message : inner.Message;

            message = message.Replace("\r", " ").Replace("\n", " ");

            return message.Length > 300 ? message[..300] + "…" : message;
        }

    }

}
