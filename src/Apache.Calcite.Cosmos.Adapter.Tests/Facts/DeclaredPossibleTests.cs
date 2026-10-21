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
    /// is built once, and that the switch roots a statement there and remembers it apart.
    /// </summary>
    public sealed class DeclaredPossibleTests
    {

        /// <summary>
        /// Every path a declaration mentions is already a term of its context, so <c>D0</c> makes none, and each is a
        /// <c>declared</c> row of it; a declaration that mentions nothing has no <c>D0</c>.
        /// </summary>
        [Fact]
        public void EveryMentionedPathIsATermOfTheContextAndADeclaredRowOfD0()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            generations.Bind(Theory()).Declaration()!.DeclaredPossible().Should().BeNull();

            foreach (var (name, theory) in Declarations(generations))
            {
                var declaration = theory.Declaration()!;
                var rules = PredicateRules.Of(declaration.Generation.Contexts);
                var d0 = declaration.DeclaredPossible();
                d0.Should().NotBeNull(name);
                d0!.IsFrozen.Should().BeTrue(name);

                foreach (var path in theory.MentionedPaths)
                {
                    var term = JsonFactEncoding.Path(path).ToTerm(rules.Json);
                    declaration.Context.TryGet(term, out _).Should().BeTrue($"{name} mentions {SqlValue.Spell(path)}");
                    d0.Check(rules.Declared.Of(term)).Should().BeTrue($"{name} mentions {SqlValue.Spell(path)}");
                }
            }
        }

        /// <summary>
        /// What <c>D0</c> says a path may hold is what a stratum holding an atom over the path computes from the same
        /// declaration, assuming nothing that speaks of it: the same documents, and met with the stratum's row, the
        /// stratum's row, which is what an armed stratum holds.
        /// </summary>
        /// <remarks>
        /// Not always the same spelling. A domain stated beside narrower types keeps only its values of those types for a
        /// path an atom reads (<see cref="PredicateRules"/>' last rule of what a predicate establishes), and <c>D0</c>
        /// holds no atom, so its row keeps the declared domain; a value of another kind is one no reader of the row
        /// admits (<see cref="PathValues.Admits"/>, <see cref="PathCell.Split"/>).
        /// </remarks>
        [Fact]
        public void TheDeclaredPossibleRowsAreWhatAStratumWithAnAtomComputes()
        {
            static PathValues Allowed(PathValues x) =>
                x.Values is null ? x : x with { Values = ValueSet.From(x.Values.Where(v => x.Kinds.Contains(JsonValues.TypeOf(v)))) };

            var generations = new JsonFactGenerations(long.MaxValue);
            var (compared, respelled) = (0, 0);

            foreach (var (name, theory) in Declarations(generations))
            {
                var declaration = theory.Declaration()!;
                var rules = PredicateRules.Of(declaration.Generation.Contexts);
                var d0 = declaration.DeclaredPossible()!;

                foreach (var path in theory.MentionedPaths)
                {
                    var term = rules.Possible.Of(JsonFactEncoding.Path(path).ToTerm(rules.Json));
                    var nothing = Opaque(0);
                    var terms = new StatementTerms(declaration, new PredicateStatement(new[] { new SqlPredicate.IsDefined(Raw(path)), nothing }), null);
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
            var declaration = generations.Bind(Theory(Outright(P("a"), Present), When(P("kind"), "k", P("b"), Type(JsonType.String)))).Declaration()!;
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
        /// Armed, a statement is rooted at <c>D0</c>, whose view is still the declaration's own fact set; and the oracle
        /// remembers the armed tree apart from the unarmed one, so neither is read for the other.
        /// </summary>
        [Fact]
        public void ArmedAStatementIsRootedAtD0AndRememberedApart()
        {
            var generations = new JsonFactGenerations(long.MaxValue);
            var theory = generations.Bind(Theory(Outright(P("a"), Present), Outright(P("a"), Type(JsonType.String))));
            var oracle = new CosmosOracle(theory, generations);
            var statement = new PredicateStatement(new[] { Eq(Text(P("a")), "x") });

            var unarmed = oracle.Admit(statement);
            CosmosStatementScope armed;
            using (StatementTerms.ArmDeclaredPossible())
                armed = oracle.Admit(statement);

            StatementTerms.DeclaredPossibleArmed.Should().BeFalse("disposing the switch puts it back");
            var declaration = theory.Declaration()!;
            armed.ContextFor(OracleLicence.Declaration).Context.Should().BeSameAs(declaration.DeclaredPossible());
            unarmed.ContextFor(OracleLicence.Declaration).Context.Should().BeSameAs(declaration.Context);
            oracle.Admit(statement).ContextFor(OracleLicence.Declaration).Context.Should().BeSameAs(declaration.Context, "the unarmed tree is remembered unarmed");
            armed.Facts(OracleLicence.Declaration).Should().BeSameAs(unarmed.Facts(OracleLicence.Declaration));
            GC.KeepAlive(armed);
            GC.KeepAlive(unarmed);
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

            return containers.Concat(trials).Where(d => d.Item2.MentionedPaths.Count > 0).Select(d => (d.Item1, generations.Bind(d.Item2)));
        }

    }

}
