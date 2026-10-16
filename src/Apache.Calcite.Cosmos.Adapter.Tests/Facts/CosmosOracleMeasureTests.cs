using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// What a question costs the oracle, warm, at the sizes <c>Saturation.MeasureTests</c> measures the theory at. Prints
    /// its figures; asserts only the answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every question is distinct.</b> Each varies a literal, so no tree of contexts is reused and each figure is what a
    /// statement the oracle has not seen costs: its terms opened beneath the declaration, its conjuncts admitted, and the
    /// question answered. That is the figure Stage 1's gate is written against (p50 within 2 ms and p95 within 11 ms is
    /// the range #221 measured; a p95 beyond 25 ms is a kill). Two more are asked of a tree that exists: a deletion's
    /// leave-one-out context, and a predicate the statement does not hold.
    /// </para>
    /// <para>
    /// The figures are written to <c>measure-oracle.txt</c> beside the test assembly. A Debug build on a workstation runs at
    /// between one and three times a Release build's.
    /// </para>
    /// </remarks>
    public sealed class CosmosOracleMeasureTests
    {

        const int Warm = 5;
        const int Asked = 40;

        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Q = P("q");

        static readonly object Writing = new();

        static void Report(string line)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            lock (Writing)
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "measure-oracle.txt"), line + Environment.NewLine);
        }

        static JsonDocumentPath Property(int j) => P("p" + j);

        /// <summary>
        /// <c>MeasureTests</c>' container, as a declaration: <paramref name="kinds"/> kinds discriminated by <c>kind</c>,
        /// each declaring <paramref name="properties"/> typed properties, a third of them required. Kinds 0 and 1 both
        /// require <c>p0</c>; kind 2 does not.
        /// </summary>
        static (JsonFactTheory Theory, int Heads) Container(int kinds, int properties)
        {
            var rules = new List<JsonFactRule>
            {
                Outright(Kind, Present),
                Outright(Kind, Enum(Enumerable.Range(0, kinds).Select(k => (JsonScalar)("k" + k)).ToArray())),
            };

            var heads = 0;
            for (var k = 0; k < kinds; k++)
            {
                var guard = new[] { new JsonFact(Kind, Value("k" + k)) };
                var then = new List<JsonFact>();
                for (var j = 0; j < properties; j++)
                {
                    var path = Property(j);
                    if ((j + k) % 4 == 2)
                        then.Add(new JsonFact(path, Type(JsonType.Number)));
                    then.Add(new JsonFact(path, ((j + k) % 4) switch
                    {
                        0 => Type(JsonType.String),
                        1 => Type(JsonType.Integer),
                        2 => new JsonClaim.UpperBound(100 + k),
                        _ => Enum("a", "b", "c"),
                    }));
                    if ((j + k) % 3 == 0)
                        then.Add(new JsonFact(path, Present));
                }

                if (k < 2)
                    then.Add(new JsonFact(Property(0), Present));

                heads += then.Count;
                rules.AddRange(then.Select(head => new JsonFactRule(guard, head)));
            }

            return (Theory(rules.ToArray()), heads);
        }

        static string Percentiles(List<double> milliseconds)
        {
            var sorted = milliseconds.OrderBy(x => x).ToArray();
            double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
            return $"p50 {At(0.50),7:F2} ms  p95 {At(0.95),7:F2} ms  max {sorted[^1],7:F2} ms";
        }

        /// <summary>Times a question asked of <see cref="Asked"/> distinct statements, after <see cref="Warm"/> more.</summary>
        static List<double> Time(Func<int, Truth> ask, Truth expected, string because)
        {
            var times = new List<double>();
            for (var n = 0; n < Warm + Asked; n++)
            {
                var watch = Stopwatch.StartNew();
                var answer = ask(n);
                watch.Stop();

                answer.Should().Be(expected, because);
                if (n >= Warm)
                    times.Add(watch.Elapsed.TotalMilliseconds);
            }

            return times;
        }

        [Theory]
        [InlineData(10, 30)]
        [InlineData(32, 100)]
        [InlineData(60, 500)]
        public void WhatAQuestionCosts(int kinds, int properties)
        {
            var watch = Stopwatch.StartNew();
            var (theory, heads) = Container(kinds, properties);
            var oracle = Oracle(theory);

            // The first statement compiles the declaration into the oracle's generation, which is not a question's cost.
            oracle.Admit(new PredicateStatement(new[] { Eq(Text(Kind), "k0") })).Decide(0, OracleLicence.Declaration);
            Report($"{kinds} kinds x {properties} properties: {heads:N0} conditional facts, declaration compiled and first statement answered in {watch.Elapsed.TotalMilliseconds:F0} ms");

            var p0 = IsNotNull(Text(Property(0)));
            CosmosStatementScope Admitted(params SqlPredicate[] conjuncts) => oracle.Admit(new PredicateStatement(conjuncts), (_, _) => true);

            var oneKind = Time(n => Admitted(Eq(Text(Kind), "k0"), Eq(Text(Q), "v" + n)).Decide(p0, OracleLicence.Select), Truth.Always,
                "kind k0 requires p0");
            var twoKinds = Time(n => Admitted(In(Text(Kind), "k0", "k1"), Eq(Text(Q), "v" + n)).Decide(p0, OracleLicence.Select), Truth.Always,
                "both kinds require p0, which takes a split on the discriminator");
            var perKind = Time(n => Admitted(Or(And(Eq(Text(Kind), "k0"), Eq(Text(Q), "v" + n)), And(Eq(Text(Kind), "k1"), Eq(Text(Q), "w" + n)))).Decide(p0, OracleLicence.Select), Truth.Always,
                "each arm's kind requires p0, which takes a split on the arms");
            var control = Time(n => Admitted(In(Text(Kind), "k0", "k2"), Eq(Text(Q), "v" + n)).Decide(p0, OracleLicence.Select), Truth.Unknown,
                "k2 does not require p0");
            var none = Time(n => Admitted(Eq(Text(Kind), "nope" + n)).Decide(p0, OracleLicence.Select), Truth.Never,
                "no document is of that kind");

            // The planner's own Derive of what the first two statements establish, on the engine and remembering nothing:
            // the cost the oracle would replace, measured beside it.
            JsonFact Is(JsonDocumentPath path, JsonClaim claim) => new(path, claim);
            theory.DeriveOnEngine(new[] { Is(Kind, Present) }, new[] { Property(0) });
            var deriveOne = Time(n => theory.DeriveOnEngine(new[] { Is(Kind, Present), Is(Kind, Value("k0")), Is(Q, Present), Is(Q, Value("v" + n)) }, new[] { Property(0) })
                .Knows(Is(Property(0), Present)) ? Truth.Always : Truth.Unknown, Truth.Always, "kind k0 requires p0");
            var deriveTwo = Time(n => theory.DeriveOnEngine(new[] { Is(Kind, Present), Is(Kind, Enum("k0", "k1")), Is(Q, Present), Is(Q, Value("v" + n)) }, new[] { Property(0) })
                .Knows(Is(Property(0), Present)) ? Truth.Always : Truth.Unknown, Truth.Always, "both kinds require p0");

            // Two questions of a tree that exists.
            var trees = Enumerable.Range(0, Warm + Asked).Select(n => Admitted(Eq(Text(Kind), "k0"), Eq(Text(Q), "x" + n))).ToArray();
            var deletion = Time(n => trees[n].Decide(1, OracleLicence.DeleteConjunct(1)), Truth.Unknown,
                "q's value is its own conjunct's, and a deletion never reads that");
            var candidate = Time(n => trees[n].Decide(Ne(Text(Q), "zz"), OracleLicence.Select), Truth.Always,
                "q is x-something wherever the statement keeps a row");

            Report($"   a distinct statement, one kind          {Percentiles(oneKind)}");
            Report($"   a distinct statement, two kinds         {Percentiles(twoKinds)}");
            Report($"   a distinct statement, a kind per arm    {Percentiles(perKind)}");
            Report($"   a distinct statement, a kind without    {Percentiles(control)}");
            Report($"   a distinct statement, no such kind      {Percentiles(none)}");
            Report($"   per distinct question, all of the above {Percentiles(oneKind.Concat(twoKinds).Concat(perKind).Concat(control).Concat(none).ToList())}");
            Report($"   the planner's Derive, one kind          {Percentiles(deriveOne)}");
            Report($"   the planner's Derive, two kinds         {Percentiles(deriveTwo)}");
            Report($"   of a tree that exists, a deletion       {Percentiles(deletion)}");
            Report($"   of a tree that exists, a candidate      {Percentiles(candidate)}");
        }

    }

}
