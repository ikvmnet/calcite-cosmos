using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// <c>D0</c>, a declaration's context with a <c>possible</c> row for every path it mentions: what its rows are, that it
    /// is built once, and that a statement is rooted there.
    /// </summary>
    public sealed class DeclaredPossibleTests
    {

        /// <summary>
        /// Every path a declaration mentions is already a term of its context, so <c>D0</c>, frozen, makes none, and is a
        /// <c>declared</c> row of it; and what <c>D0</c> says it may hold is what a stratum holding an atom over the path
        /// computes from the same declaration's own context, assuming nothing that speaks of it: the same documents, and met
        /// with the stratum's row, the stratum's row, which is what a stratum beneath <c>D0</c> holds. A declaration that
        /// mentions nothing has no <c>D0</c>.
        /// </summary>
        /// <remarks>Not always the same spelling: <c>DESIGN.md</c>, <i><c>D0</c> keeps a declared domain as stated</i>.</remarks>
        [Fact]
        public void TheDeclaredPossibleRowsAreWhatAStratumWithAnAtomComputes()
        {
            static PathValues Allowed(PathValues x) =>
                x.Values is null ? x : x with { Values = ValueSet.From(x.Values.Where(v => x.Kinds.Contains(JsonValues.TypeOf(v)))) };

            var generations = new JsonFactGenerations(long.MaxValue);
            Theory().In(generations).Declaration()!.DeclaredPossible().Should().BeNull();
            var (compared, respelled) = (0, 0);

            foreach (var (name, theory) in Declarations(generations))
            {
                var declaration = theory.Declaration()!;
                var rules = PredicateRules.Of(declaration.Generation.Contexts);
                var d0 = declaration.DeclaredPossible()!;
                d0.IsFrozen.Should().BeTrue(name);

                foreach (var path in theory.MentionedPaths)
                {
                    var at = JsonFactEncoding.Path(path).ToTerm(rules.Json);
                    (declaration.Context.TryGet(at, out _), d0.Check(rules.Declared.Of(at))).Should().Be((true, true), $"{name} mentions {SqlValue.Spell(path)}");
                    var term = rules.Possible.Of(at);
                    var nothing = Opaque(0);
                    // Rooted at the context, as a declaration with no D0 is: what a stratum knew before D0 was its root.
                    var terms = new StatementTerms(declaration with { DeclaredPossible = () => null }, new PredicateStatement(new[] { new SqlPredicate.IsDefined(Raw(path)), nothing }), null);
                    var stratum = terms.Child(terms.Root, new[] { nothing });
                    stratum.Complete.Should().BeTrue(name);

                    stratum.Scope!.TryGet(term, out var computed).Should().BeTrue($"{name}: the stratum reads {SqlValue.Spell(path)}");
                    d0.TryGet(term, out var declared).Should().BeTrue($"{name}: D0 declares {SqlValue.Spell(path)}");
                    Allowed(declared!).Should().Be(Allowed(computed!), $"{name} at {SqlValue.Spell(path)}");
                    PathValues.Meet(declared!, computed!).Should().Be(computed, $"{name} at {SqlValue.Spell(path)}");
                    compared++;
                    respelled += declared == computed ? 0 : 1;
                    GC.KeepAlive(terms);
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{compared} paths compared, {respelled} spelled apart");
        }

        /// <summary>
        /// Sixteen threads asking for one declaration's <c>D0</c> at once get one context, built once: the declaration's
        /// context has one child.
        /// </summary>
        [Fact]
        public void SixteenThreadsBuildOneD0()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var declaration = Theory(Outright(P("a"), Present), When(P("kind"), "k", P("b"), Type(JsonType.String))).In(generations).Declaration()!;
            var seen = new Scope?[16];

            using (var start = new Barrier(seen.Length))
            {
                var threads = Enumerable.Range(0, seen.Length).Select(i => new Thread(() =>
                {
                    start.SignalAndWait();
                    seen[i] = declaration.DeclaredPossible();
                })).ToArray();

                foreach (var thread in threads)
                    thread.Start();
                foreach (var thread in threads)
                    thread.Join();
            }

            seen.Distinct().Should().ContainSingle().Which.Should().NotBeNull();
            declaration.Context.Children.Should().ContainSingle("one D0 was built");
        }

        /// <summary>
        /// A statement is rooted at <c>D0</c>, whose view is still the declaration's own fact set; and at the declaration's
        /// context where it has no <c>D0</c>, a declaration that mentions nothing.
        /// </summary>
        [Fact]
        public void AStatementIsRootedAtD0()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = Theory(Outright(P("a"), Present), Outright(P("a"), Type(JsonType.String))).In(generations);
            var statement = new PredicateStatement(new[] { Eq(Text(P("a")), "x") });
            var scope = new CosmosOracle(theory, generations).Admit(statement);

            var declaration = theory.Declaration()!;
            scope.ContextFor(OracleLicence.Declaration).Context.Should().BeSameAs(declaration.DeclaredPossible()).And.NotBeNull();
            scope.Facts(OracleLicence.Declaration).Should().BeSameAs(declaration.Declared);

            var nothing = Theory().In(generations);
            var unrooted = new CosmosOracle(nothing, generations).Admit(statement);
            unrooted.ContextFor(OracleLicence.Declaration).Context.Should().BeSameAs(nothing.Declaration()!.Context);
            GC.KeepAlive(scope);
            GC.KeepAlive(unrooted);
        }

        /// <summary>
        /// The declarations checked, each that mentions a path: every container the plan digests plan over, and the
        /// declarations of population D's first 64 trials, bound to the generations given.
        /// </summary>
        static IEnumerable<(string Name, JsonFactTheory Theory)> Declarations(JsonFactGenerations generations)
        {
            var containers = CosmosDifferentialTests.Containers.Concat(PlannerSchema.All).Select(c => (c.Name, c.Facts));
            var trials = Enumerable.Range(0, 64).Select(trial =>
            {
                var seed = PredicateSoundnessTests.Seed + trial;
                var declaration = Declaration.Random(new Random(seed), new Random(unchecked(seed * 7919 + 17)), PopulationD.DeclaresNothingConditional(seed));
                return ($"trial {trial}", new JsonFactTheory(declaration.Rules));
            });

            return containers.Concat(trials).Where(d => d.Item2.MentionedPaths.Count > 0).Select(d => (d.Item1, d.Item2.In(generations)));
        }

    }

}
