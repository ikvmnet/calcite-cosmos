using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// S's generations: each defined off the planning thread, a spent one answering until its successor is ready and serving
    /// its bundles after, one a bundle found out of ids or epochs retired at once, a build over where it publishes or fails
    /// rather than where its task finishes, and a bundle the engine failed declining rather than throwing.
    /// </summary>
    public sealed class RenderingGraphTests
    {

        /// <summary>
        /// Past the gauge, a generation's successor is built while the generation goes on answering, and swapped in once
        /// ready: defined alike, so the two share memo entries, where a planted row shares none. A retired generation serves
        /// the bundles open on it. A bundle that found its graph out of ids or epochs declines, and its generation retires
        /// though the gauge is not past, the next bundle waiting for the successor.
        /// </summary>
        [Fact]
        public void ASpentGenerationAnswersUntilItsSuccessorIsReady()
        {
            var (forced, gate) = (false, new ManualResetEventSlim(true));
            var graph = new RenderingGraph(() =>
            {
                gate.Wait();
                return Measured;
            }, _ => forced);
            using var first = graph.Open(RenderingCheckRun.AssertedSet);
            var root = first.Write(NotBikes);
            first.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
            first.Claim(root, Guarded);

            gate.Reset();
            forced = true;
            using var second = graph.Open(RenderingCheckRun.AssertedSet);
            forced = false;
            using var third = graph.Open(RenderingCheckRun.AssertedSet);
            (ReferenceEquals(second.Theory, first.Theory), ReferenceEquals(third.Theory, first.Theory), graph.Generations).Should().Be((true, true, 1),
                "a spent generation answers while its successor is being defined");

            gate.Set();
            SpinWait.SpinUntil(() => graph.Generations == 2, TimeSpan.FromMinutes(1)).Should().BeTrue();
            using var fourth = graph.Open(RenderingCheckRun.AssertedSet);
            ReferenceEquals(fourth.Theory, first.Theory).Should().BeFalse("the successor is swapped in once ready");
            RenderingAnswerMemo.DigestOf(fourth.Theory).Should().Be(RenderingAnswerMemo.DigestOf(first.Theory), "generations defined alike share memo entries");
            RenderingAnswerMemo.DigestOf(RenderingTheory.Define(Measured with { Tolerated = Truths.None })).Should().NotBe(RenderingAnswerMemo.DigestOf(first.Theory), "a planted row shares none");

            first.Run().StopReason.Should().Be(RunStopReason.Completed);
            first.Read(root, Guarded, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Proven, "a retired generation serves the bundles open on it");

            // Out of ids or epochs is the graph's: no test can spend 2^31 ids, so the bundle is told as its catch would tell it.
            fourth.Decline(new OverflowException("planted"));
            fourth.Read(fourth.Write(NotBikes), Guarded, Verdict.Equal).Should().Match<RenderingVerdict>(v =>
                v.Kind == RenderingVerdictKind.Unproven && v.Reason == "declined: OverflowException: planted");
            using var fifth = graph.Open(RenderingCheckRun.AssertedSet);
            (ReferenceEquals(fifth.Theory, fourth.Theory), graph.Generations, fifth.Failure).Should().Be((false, 3, (Exception?)null));
        }

        /// <summary>
        /// Every generation, the first and each successor, is defined on a dedicated thread: never the one that opens a
        /// bundle, nor one of the pool's, which planning threads may be, and under none of the asker's async locals.
        /// </summary>
        [Fact]
        public void ASuccessorIsBuiltOffThePlanningThread()
        {
            var (forced, defined, planning) = (false, new ConcurrentQueue<(int Thread, bool Pooled, string? Local)>(), new AsyncLocal<string>());
            var graph = new RenderingGraph(() =>
            {
                defined.Enqueue((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread, planning.Value));
                return Measured;
            }, _ => forced);
            planning.Value = "planning";
            graph.Open(RenderingCheckRun.AssertedSet).Dispose();
            forced = true;
            graph.Open(RenderingCheckRun.AssertedSet).Dispose();
            forced = false;
            SpinWait.SpinUntil(() => graph.Generations == 2, TimeSpan.FromMinutes(1)).Should().BeTrue();
            defined.Should().HaveCount(2).And.OnlyContain(d => d.Thread != Environment.CurrentManagedThreadId && d.Pooled == false && d.Local == null);
        }

        /// <summary>
        /// A successor's build that throws, the definition having built once, is no row's fault: S stays available and the
        /// current answers; the first question past the retry starts the next, and it is swapped in. The failed build is
        /// held where it recorded its failure, its task unfinished, while that question is asked: it is over there.
        /// </summary>
        [Fact]
        public void ASuccessorsFailureLeavesTheCurrentAnswering()
        {
            var (forced, defined, failing, release) = (false, 0, 0, new ManualResetEventSlim(false));
            var graph = new RenderingGraph(() =>
            {
                if (Interlocked.Increment(ref defined) != 2)
                    return Measured;

                Volatile.Write(ref failing, Environment.CurrentManagedThreadId);
                throw new OutOfMemoryException("planted");
            }, _ => forced, retry: TimeSpan.Zero, recorded: () =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref failing))
                    release.Wait(TimeSpan.FromMinutes(1));
            });

            try
            {
                var first = graph.Current();
                forced = true;
                graph.Current();
                SpinWait.SpinUntil(() => graph.SuccessorFailures == 1, TimeSpan.FromMinutes(1)).Should().BeTrue();
                (graph.Available, ReferenceEquals(graph.Current(), first)).Should().Be((true, true));
                SpinWait.SpinUntil(() => graph.Generations == 2, TimeSpan.FromMinutes(1)).Should().BeTrue("the question past the retry started the next");
                forced = false;
                (ReferenceEquals(graph.Current(), first), graph.SuccessorFailures, graph.Available).Should().Be((false, 1, true));
            }
            finally
            {
                release.Set();
            }
        }

        /// <summary>
        /// A build is over where it publishes its generation, not where its task finishes: a question that finds the
        /// generation ageing starts its successor, and it is swapped in, while the build that published it is held short of
        /// finishing.
        /// </summary>
        [Fact]
        public void AGenerationAgeingAsItIsPublishedStartsItsSuccessor()
        {
            var (forced, defined, holding, release) = (false, 0, 0, new ManualResetEventSlim(false));
            var graph = new RenderingGraph(() =>
            {
                if (Interlocked.Increment(ref defined) == 1)
                    Volatile.Write(ref holding, Environment.CurrentManagedThreadId);

                return Measured;
            }, _ => forced, recorded: () =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref holding))
                    release.Wait(TimeSpan.FromMinutes(1));
            });

            try
            {
                graph.Warm();
                SpinWait.SpinUntil(() => graph.Generations == 1, TimeSpan.FromMinutes(1)).Should().BeTrue();
                forced = true;
                var first = graph.Current();
                forced = false;
                SpinWait.SpinUntil(() => graph.Generations == 2, TimeSpan.FromMinutes(1)).Should().BeTrue("the question started the successor");
                (ReferenceEquals(graph.Current(), first), graph.Ready.IsCompleted).Should().Be((false, false), "the successor answers while the first build's task is unfinished");
            }
            finally
            {
                release.Set();
            }
        }

        /// <summary>
        /// A build is over where it publishes, not where its task finishes: a question whose wait for the build runs out
        /// between the two answers from what it published rather than declining.
        /// </summary>
        [Fact]
        public void AWaitThatRunsOutAfterTheBuildPublishedAnswers()
        {
            // The publish must land well inside the question's wait for the question to tell anything: a machine that stalls
            // the build, or this thread, past that is tried again on a fresh graph, and one that stalls them every time tells
            // nothing.
            for (var attempt = 0; attempt < 3; attempt++)
                if (AskedAsTheBuildPublishes(TimeSpan.FromSeconds(3)) is { } told)
                {
                    told.Should().Be((true, true, 1));
                    return;
                }

            Assert.Skip("The build published too late in the question's wait, three times.");
        }

        /// <summary>
        /// Holds a first build short of publishing until a question waits on it, then lets it publish and holds its task
        /// unfinished past the question's wait: what the question told, or none where the publish landed too late in it.
        /// </summary>
        static (bool Answered, bool Current, int Generations)? AskedAsTheBuildPublishes(TimeSpan wait)
        {
            var (held, publish, finish) = (new ManualResetEventSlim(false), new ManualResetEventSlim(false), new ManualResetEventSlim(false));
            var graph = new RenderingGraph(() => Measured, exercised: (_, _) =>
            {
                held.Set();
                publish.Wait(TimeSpan.FromMinutes(1));
            }, recorded: () => finish.Wait(TimeSpan.FromMinutes(1)));

            try
            {
                graph.Warm();
                held.Wait(TimeSpan.FromMinutes(1)).Should().BeTrue();

                var (answered, theory, since) = (false, (RenderingTheory?)null, Stopwatch.GetTimestamp());
                var asker = new Thread(() => answered = graph.TryCurrent(wait, out theory));
                asker.Start();
                SpinWait.SpinUntil(() => (asker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 || asker.IsAlive == false, TimeSpan.FromMinutes(1)).Should().BeTrue();
                publish.Set();
                SpinWait.SpinUntil(() => graph.Generations == 1, TimeSpan.FromMinutes(1)).Should().BeTrue();
                var early = Stopwatch.GetElapsedTime(since) < wait - TimeSpan.FromSeconds(0.5);
                asker.Join(TimeSpan.FromMinutes(1)).Should().BeTrue();
                return early ? (answered, ReferenceEquals(theory, graph.Current()), graph.Generations) : null;
            }
            finally
            {
                publish.Set();
                finish.Set();
            }
        }

        /// <summary>
        /// A bundle whose run the engine failed declines: every claim reads Unproven and names the failure. Here a leaf
        /// answers one key two ways: two conjuncts cut at one literal share a partition, so the leaf their forms share is
        /// evaluated for each, and <c>svc</c> holds one value a key. The fault is the bundle's own, so its generation
        /// serves the next.
        /// </summary>
        [Fact]
        public void AFaultedBundleMakesItsConjunctsUnproven()
        {
            var calls = 0;
            var graph = new RenderingGraph(() => Measured with
            {
                Leaf = (f, z, t, q) =>
                {
                    var v = Measured.Leaf(f, z, t, q);
                    return v with { Rows = v.Rows.Add("call " + Interlocked.Increment(ref calls)) };
                },
            });

            using var bundle = graph.Open(RenderingCheckRun.AssertedSet);
            var unequal = bundle.Write(NotBikes);
            var equal = bundle.Write(Eq(Text(LabelPath), "bikes"));
            bundle.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
            bundle.Claim(unequal, Bare);
            bundle.Claim(equal, Bare);
            bundle.Run().Should().Be(new RenderingRun(null, true, false));
            bundle.Failure.Should().BeOfType<RuleEvaluationException>().Which.Message.Should().Match("*function svc*");
            bundle.Scope.IsFaulted.Should().BeTrue();

            foreach (var root in new[] { unequal, equal })
                foreach (var claim in new[] { Verdict.Equal, Verdict.Implies })
                    bundle.Read(root, Bare, claim).Should().Match<RenderingVerdict>(v => v.Kind == RenderingVerdictKind.Unproven && v.Reason!.StartsWith("declined: RuleEvaluationException"));

            using var next = graph.Open(RenderingCheckRun.AssertedSet);
            var alone = next.Write(NotBikes);
            next.Import(Source, LabelPath, PathValues.Anything, JsonValues.AllBands);
            next.Claim(alone, Bare);
            next.Run().StopReason.Should().Be(RunStopReason.Completed);
            (ReferenceEquals(next.Theory, bundle.Theory), next.Read(alone, Bare, Verdict.Equal).Kind).Should().Be((true, RenderingVerdictKind.Refuted));
        }

    }

}
