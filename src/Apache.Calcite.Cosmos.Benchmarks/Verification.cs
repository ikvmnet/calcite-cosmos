using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

using Apache.Calcite.Cosmos.Benchmarks.Model;

namespace Apache.Calcite.Cosmos.Benchmarks
{

    /// <summary>
    /// Plans the whole corpus once and reports what happened to each statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A benchmark corpus rots quietly. A statement that stops parsing, or one that stops pushing
    /// because a rule's predicate tightened, still produces a number — and a suite whose numbers
    /// improved because half of it stopped reaching the rules is worse than no suite. This is the
    /// cheap check that says which is which, and it is a command rather than a benchmark because it
    /// needs one pass, not five hundred.
    /// </para>
    /// <para>
    /// The timings it prints are a single unwarmed pass and are not measurements. They are here to
    /// say which statements are expensive enough to matter and which are large enough to be worth
    /// splitting out, so that a run can be sized before it is started.
    /// </para>
    /// </remarks>
    public static class Verification
    {

        /// <summary>
        /// What one statement did.
        /// </summary>
        /// <param name="Query">The statement.</param>
        /// <param name="Stage">The last stage that succeeded, or the one that threw.</param>
        /// <param name="Failure">The failure, or <c>null</c>.</param>
        /// <param name="PlanMilliseconds">What planning to the asynchronous convention took, unwarmed.</param>
        /// <param name="Pushes">Whether the chosen plan puts anything in the Cosmos convention.</param>
        /// <param name="PushesWhole">Whether the statement also plans wholly in the Cosmos convention.</param>
        /// <param name="Sql">The Cosmos statement a wholly pushed plan renders to, or <c>null</c>.</param>
        sealed record Result(
            PlannerQuery Query,
            string Stage,
            Exception? Failure,
            double PlanMilliseconds,
            bool Pushes,
            bool PushesWhole,
            string? Sql);

        /// <summary>
        /// Runs the verification.
        /// </summary>
        /// <param name="args">Command arguments. <c>--sql</c> also prints the rendered statement of
        /// every wholly pushed plan.</param>
        /// <returns>Zero where every statement planned, one otherwise.</returns>
        public static int Run(string[] args)
        {
            var showSql = args.Contains("--sql", StringComparer.OrdinalIgnoreCase);
            var showScale = args.Contains("--scale", StringComparer.OrdinalIgnoreCase);
            var harness = new PlannerHarness();

            // The first statement through pays for every type initializer Calcite has, which is
            // seconds and would otherwise be attributed to whichever statement came first.
            _ = harness.PlanToAsync("""SELECT c."id" FROM products AS c WHERE c."category" = 'x'""");

            var results = new List<Result>();

            foreach (var query in PlannerQueries.All)
                results.Add(Verify(harness, query));

            Report(results, showSql);

            var failed = results.Count(r => r.Failure is not null);
            var inert = results.Count(r => r.Failure is null && r.Pushes == false);
            var drifted = ReportPushdownDrift(results);

            if (showScale)
                ReportScale(harness);

            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{results.Count} statements, {failed} failed, {inert} planned without reaching the Cosmos convention, {drifted} disagreed with the wholly-pushed list."));

            return failed == 0 && drifted == 0 ? 0 : 1;
        }

        /// <summary>
        /// Reports statements whose pushdown no longer matches what the corpus claims.
        /// </summary>
        /// <remarks>
        /// Both directions are reported and both fail the check. A statement that stopped pushing is
        /// a rule that regressed, and one that started is a rule that improved — but the second is
        /// still a corpus that is now wrong, and the benchmarks that read the list would silently
        /// skip it.
        /// </remarks>
        /// <param name="results">The results.</param>
        /// <returns>How many disagreed.</returns>
        static int ReportPushdownDrift(IReadOnlyList<Result> results)
        {
            var drifted = 0;

            foreach (var result in results)
            {
                if (result.Failure is not null)
                    continue;

                var expected = PlannerQueries.IsWhollyPushed(result.Query);

                if (expected == result.PushesWhole)
                    continue;

                if (drifted++ == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("The wholly-pushed list in PlannerQueries disagrees with the planner:");
                }

                Console.WriteLine(expected
                    ? $"  {result.Query.Name} is listed but no longer plans wholly in the convention"
                    : $"  {result.Query.Name} now plans wholly in the convention and is not listed");
            }

            return drifted;
        }

        /// <summary>
        /// Times the generated statements at a range of sizes.
        /// </summary>
        /// <remarks>
        /// Not a measurement either — one unwarmed pass each — but the one that says where a
        /// scaling benchmark's parameters should stop. A size whose single pass is already seconds
        /// is a size that turns a benchmark run into an afternoon.
        /// </remarks>
        /// <param name="harness">The harness.</param>
        static void ReportScale(PlannerHarness harness)
        {
            var dimensions = new (string Name, int Minimum, Func<int, string> Build)[]
            {
                ("conjuncts", 1, PlannerQueryGenerator.Conjuncts),
                ("disjuncts", 1, PlannerQueryGenerator.Disjuncts),
                ("point reads", 1, PlannerQueryGenerator.PointReadSet),
                ("projections", 1, PlannerQueryGenerator.Projections),
                ("nesting", 1, PlannerQueryGenerator.Nesting),
                ("unnests", 1, PlannerQueryGenerator.Unnests),
                ("joins", 2, PlannerQueryGenerator.Joins),
                ("union branches", 2, PlannerQueryGenerator.UnionBranches),
            };

            var sizes = new[] { 1, 2, 4, 8, 16, 32, 64 };
            var joinSizes = new[] { 2, 3, 4, 5, 6, 7, 8 };

            Console.WriteLine();
            Console.WriteLine("scaling, one unwarmed pass each, milliseconds");
            Console.WriteLine("dimension        " + string.Concat(sizes.Select(n => n.ToString(CultureInfo.InvariantCulture).PadLeft(10))));

            foreach (var (name, minimum, build) in dimensions)
            {
                var row = new StringBuilder(name.PadRight(17));

                foreach (var size in sizes)
                {
                    if (size < minimum)
                    {
                        row.Append("         -");
                        continue;
                    }

                    try
                    {
                        var watch = Stopwatch.StartNew();
                        _ = harness.PlanToAsync(build(size));
                        watch.Stop();

                        row.Append(watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture).PadLeft(10));
                    }
                    catch (Exception)
                    {
                        row.Append("     failed");
                    }
                }

                Console.WriteLine(row.ToString());
            }

            // The one dimension whose growth is combinatorial rather than linear, and the reason
            // the join scaling benchmark stops where it does. Its own row because its sizes are not
            // the others': a seven-way join with reordering is not comparable to seven conjuncts.
            Console.WriteLine();
            Console.WriteLine("join chain with reordering, one unwarmed pass each, milliseconds");
            Console.WriteLine("joins            " + string.Concat(joinSizes.Select(n => n.ToString(CultureInfo.InvariantCulture).PadLeft(10))));

            var reordered = new StringBuilder("reordered".PadRight(17));
            var reason = (string?)null;

            foreach (var size in joinSizes)
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    _ = harness.PlanToAsync(PlannerQueryGenerator.Joins(size), reorderJoins: true);
                    watch.Stop();

                    reordered.Append(watch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture).PadLeft(10));
                }
                catch (Exception e)
                {
                    reordered.Append("     failed");
                    reason ??= Summarize(e);
                }
            }

            Console.WriteLine(reordered.ToString());

            if (reason is not null)
                Console.WriteLine($"  first failure: {reason}");
        }

        /// <summary>
        /// Runs one statement through every stage, stopping at the first that throws.
        /// </summary>
        /// <param name="harness">The harness.</param>
        /// <param name="query">The statement.</param>
        /// <returns>What happened.</returns>
        static Result Verify(PlannerHarness harness, PlannerQuery query)
        {
            var stage = "parse";

            try
            {
                var parsed = harness.Parse(query.Sql);

                stage = "convert";
                _ = harness.ToRel(parsed);

                stage = "plan";
                var watch = Stopwatch.StartNew();
                var planned = harness.PlanToAsync(query.Sql);
                watch.Stop();

                var pushes = PlannerHarness.Pushes(planned);

                // Whether the statement also plans with the alternative removed, which is what makes
                // it usable by the pushdown and rendering benchmarks. Failure here is an answer
                // rather than an error: most statements do not push whole.
                var whole = false;
                string? sql = null;

                try
                {
                    var pushed = harness.PlanToCosmos(query.Sql);
                    whole = true;

                    var table = PlannerHarness.TableOf(pushed);
                    if (table is not null)
                        sql = PlannerHarness.Implement(pushed, table.Container).Sql;
                }
                catch (Exception)
                {
                    whole = false;
                }

                return new Result(query, "ok", null, watch.Elapsed.TotalMilliseconds, pushes, whole, sql);
            }
            catch (Exception e)
            {
                return new Result(query, stage, e, 0, false, false, null);
            }
        }

        /// <summary>
        /// Prints the results as a table.
        /// </summary>
        /// <param name="results">The results.</param>
        /// <param name="showSql">Whether to print the rendered statement of each wholly pushed plan.</param>
        static void Report(IReadOnlyList<Result> results, bool showSql)
        {
            var width = results.Max(r => r.Query.Name.Length);

            Console.WriteLine($"{"statement".PadRight(width)}  {"plan",10}  push  whole  note");
            Console.WriteLine(new string('-', width + 32));

            foreach (var result in results)
            {
                if (result.Failure is not null)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{result.Query.Name.PadRight(width)}  {"FAILED",10}  ----  -----  {result.Stage}: {Summarize(result.Failure)}"));
                    continue;
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{result.Query.Name.PadRight(width)}  {result.PlanMilliseconds,9:F1}ms  {(result.Pushes ? " yes" : "  no")}  {(result.PushesWhole ? "  yes" : "   no")}"));

                if (showSql && result.Sql is not null)
                    Console.WriteLine($"{new string(' ', width + 2)}  {result.Sql}");
            }
        }

        /// <summary>
        /// Reduces an exception to one line.
        /// </summary>
        /// <remarks>
        /// Calcite's failures arrive wrapped several deep and the innermost one is the only
        /// informative layer, so this walks to it.
        /// </remarks>
        /// <param name="exception">The exception.</param>
        /// <returns>The message.</returns>
        static string Summarize(Exception exception)
        {
            var inner = exception;

            while (inner.InnerException is not null)
                inner = inner.InnerException;

            var message = inner is java.lang.Throwable throwable ? throwable.getMessage() ?? inner.Message : inner.Message;

            message = message.Replace("\r", " ").Replace("\n", " ");

            return message.Length > 160 ? message[..160] + "…" : message;
        }

    }

}
