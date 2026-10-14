using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Eggnog;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static JsonTheory.JsonValues;

namespace JsonTheory.Tests
{

    /// <summary>
    /// What a split costs on a large schema, and whether queries on many threads under one frozen schema context
    /// answer as one thread does. Prints its figures; asserts only the answers.
    /// </summary>
    [TestClass]
    public sealed class MeasureTests
    {

        static readonly JsonContexts Contexts = new();

        /// <summary>The figures, written beside the test assembly as <c>measure.txt</c>: the runner keeps a passing test's output.</summary>
        static void Report(string line)
        {
            Console.WriteLine(line);
            lock (Contexts)
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "measure.txt"), line + Environment.NewLine);
        }

        static JsonFact.OneOf Is(JsonPathSpec path, params string[] values) => new(path, ValueSet.From(values.Select(Of)));

        /// <summary>
        /// A container of <paramref name="kinds"/> kinds discriminated by <c>kind</c>, each declaring <paramref name="properties"/>
        /// typed properties, a third of them required. Kinds 0 and 1 both require <c>p0</c>; kind 2 does not.
        /// </summary>
        static (Scope Schema, int Heads, double Milliseconds) Container(int kinds, int properties)
        {
            var rules = new List<JsonGuardedRule>();
            var heads = 0;
            for (var k = 0; k < kinds; k++)
            {
                var then = new List<JsonFact>();
                for (var j = 0; j < properties; j++)
                {
                    var path = JsonPathSpec.Root["p" + j];
                    if ((j + k) % 4 == 2)
                        then.Add(new JsonFact.OfType(path, Types("number")));
                    then.Add(((j + k) % 4) switch
                    {
                        0 => new JsonFact.OfType(path, Types("string")),
                        1 => new JsonFact.OfType(path, Types("integer")),
                        2 => new JsonFact.Upper(path, Bound.AtMost(100 + k)),
                        _ => Is(path, "a", "b", "c"),
                    });
                    if ((j + k) % 3 == 0)
                        then.Add(new JsonFact.Present(path));
                }

                // Kinds 0 and 1 both require p0; kind 2 does not. Whatever else each says of p0, it must be satisfiable.
                if (k < 2)
                    then.Add(new JsonFact.Present("$.p0"));

                heads += then.Count;
                rules.Add(new JsonGuardedRule(new JsonFact[] { Is("$.kind", "k" + k) }, then));
            }

            var watch = Stopwatch.StartNew();
            var facts = new JsonFact[]
            {
                new JsonFact.Present("$.kind"), new JsonFact.OneOf("$.kind", ValueSet.From(Enumerable.Range(0, kinds).Select(k => Of("k" + k)))),
                new JsonFact.Discriminates("$.kind"),
            };
            var schema = Contexts.OpenSchema(facts, rules, Enumerable.Range(0, properties).Select(j => JsonPathSpec.Root["p" + j]));
            schema.Freeze();
            return (schema, heads, watch.Elapsed.TotalMilliseconds);
        }

        static Truth Ask(Scope schema, JsonAtom atom, params JsonFact[] facts)
        {
            using var query = Contexts.Open(schema, facts, new[] { atom.Path });
            return Contexts.Decide(query, atom);
        }

        static string Time(Func<Truth> call, out Truth answer)
        {
            answer = call();
            var watch = Stopwatch.StartNew();
            var calls = 0;
            while (watch.ElapsedMilliseconds < 300 || calls < 3)
            {
                call();
                calls++;
            }

            var micros = watch.Elapsed.TotalMilliseconds * 1000 / calls;
            return micros >= 1000 ? $"{micros / 1000:F2} ms" : $"{micros:F0} µs";
        }

        [TestMethod]
        [DataRow(10, 30)]
        [DataRow(32, 100)]
        [DataRow(60, 500)]
        public void WhatASplitCosts(int kinds, int properties)
        {
            var (schema, heads, compile) = Container(kinds, properties);
            Report($"{kinds} kinds x {properties} properties: {heads:N0} conditional facts, schema compiled and frozen in {compile:F0} ms");

            var one = Time(() => Ask(schema, new JsonAtom.Defined("$.p0"), Is("$.kind", "k0")), out var a1);
            var two = Time(() => Ask(schema, new JsonAtom.Defined("$.p0"), Is("$.kind", "k0", "k1")), out var a2);
            var control = Time(() => Ask(schema, new JsonAtom.Defined("$.p0"), Is("$.kind", "k0", "k2")), out var a3);
            var five = Time(() => Ask(schema, new JsonAtom.HasType("$.p1", "boolean"), Is("$.kind", "k0", "k1", "k2", "k3", "k4")), out var a4);
            var none = Time(() => Ask(schema, new JsonAtom.Defined("$.p0"), Is("$.kind", "nope")), out var a5);

            using (Contexts.Open(schema, new JsonFact[] { Is("$.kind", "k0", "k1") }, new JsonPathSpec[] { "$.p0" }))
            {
                var report = JsonContexts.LastReport!;
                Report($"   a 2-case query: {report.Elapsed.TotalMilliseconds:F2} ms in the run (search {report.SearchTime.TotalMilliseconds:F2}, merge {report.MergeTime.TotalMilliseconds:F2}, rebuild {report.RebuildTime.TotalMilliseconds:F2}), {report.Iterations} iterations, {report.CasesOpened} cases, {report.Lifts} lifts, {report.Rules.Count} rules");
                Assert.AreEqual(2, report.CasesOpened, "kind IN two kinds is a split of two cases");
                foreach (var (rule, stats) in report.Rules.OrderByDescending(kv => kv.Value.SearchTime).Take(6))
                    Report($"      {stats.SearchTime.TotalMilliseconds,6:F2} ms  {stats.Matches,7} matches  {rule.Name}");
            }

            Report($"   kind = 'k0' (no split)              {one,10}  {a1}");
            Report($"   kind IN two kinds (2 cases)        {two,10}  {a2}");
            Report($"   kind IN two, one saying otherwise  {control,10}  {a3}");
            Report($"   kind IN five kinds (5 cases)       {five,10}  {a4}");
            Report($"   kind = 'nope' (no document)        {none,10}  {a5}");

            Assert.AreEqual(Truth.Always, a1);
            Assert.AreEqual(Truth.Always, a2, "both kinds require p0, which takes a split");
            Assert.AreEqual(Truth.Unknown, a3, "k2 does not require p0 when (0 + 2) % 3 != 0");
            Assert.AreEqual(Truth.Never, a4, "no kind lets p1 be a boolean");
            Assert.AreEqual(Truth.Never, a5);
        }

        [TestMethod]
        public void QueriesOnManyThreadsAnswerAsOneThreadDoes()
        {
            var (schema, _, _) = Container(32, 100);
            var questions = Enumerable.Range(0, 64).Select(i => (
                Atom: (JsonAtom)new JsonAtom.Defined(JsonPathSpec.Root["p" + (i % 100)]),
                Facts: new JsonFact[] { Is("$.kind", "k" + (i % 32), "k" + ((i * 7 + 1) % 32)) })).ToArray();

            var serial = questions.Select(q => Ask(schema, q.Atom, q.Facts)).ToArray();

            var watch = Stopwatch.StartNew();
            var parallel = new Truth[questions.Length * 8];
            Parallel.For(0, parallel.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var q = questions[i % questions.Length];
                parallel[i] = Ask(schema, q.Atom, q.Facts);
            });

            Report($"{parallel.Length} split queries on {Environment.ProcessorCount} threads under one frozen schema context: {watch.Elapsed.TotalMilliseconds:F0} ms");
            for (var i = 0; i < parallel.Length; i++)
                Assert.AreEqual(serial[i % questions.Length], parallel[i], $"question {i % questions.Length} on another thread");
        }

    }

}
