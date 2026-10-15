using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// What a query established, read without the engine where the engine could add nothing, held to what the engine
    /// answers.
    /// </summary>
    /// <remarks>
    /// The overlay is a claim that a handful of rules of JSON are all that apply when its gate passes. That is checked the
    /// only way it can be: generated sets go through both, and every question a consumer asks is asked of both, about
    /// every path either could know something of. The engine is told of the paths beneath what was established, so that
    /// it proves them absent as the overlay does; told of none, it knows less of them, and never more. It is told of no
    /// other path, since what it then knows is what a query naming none of them gets.
    /// </remarks>
    public class JsonFactOverlayTests
    {

        static readonly JsonDocumentPath A = JsonDocumentPath.Root.Property("a");
        static readonly JsonDocumentPath B = JsonDocumentPath.Root.Property("b");
        static readonly JsonDocumentPath Ax = A.Property("x");
        static readonly JsonDocumentPath Ay = A.Property("y");
        static readonly JsonDocumentPath Bzw = B.Property("z").Property("w");
        static readonly JsonDocumentPath Id = JsonDocumentPath.Root.Property("id");
        static readonly JsonDocumentPath Ts = JsonDocumentPath.Root.Property("_ts");
        static readonly JsonDocumentPath Meta = JsonDocumentPath.Root.Property("meta");
        static readonly JsonDocumentPath MetaOwner = Meta.Property("owner");
        static readonly JsonDocumentPath MetaX = Meta.Property("x");

        /// <summary>The service's facts alone: the theory of every container that declares no schema.</summary>
        static readonly JsonFactTheory Undeclared = new(CosmosServiceFacts.Rules);

        /// <summary>The service's facts and an unconditional one beneath a path a query can also name a sibling of.</summary>
        static readonly JsonFactTheory Stated = new(CosmosServiceFacts.Rules.Concat(new[]
        {
            JsonFactRule.Unconditional(new JsonFact(MetaOwner, new JsonClaim.Present())),
            JsonFactRule.Unconditional(new JsonFact(MetaOwner, new JsonClaim.OfType(JsonType.String))),
        }));

        static readonly JsonDocumentPath[] Paths = { A, B, Ax, Ay, Bzw, MetaX, Id, Ts, Id.Property("q"), MetaOwner };

        static readonly JsonScalar[] Values =
        {
            "s", "t", "", 0m, 1m, 5m, -3m, 2.5m, 1e20m, 1e300, true, false, JsonScalar.Null,
        };

        static readonly decimal[] Edges = { -3m, 0m, 1m, 2.5m, 5m, 100m };

        static JsonFact Fact(JsonDocumentPath path, JsonClaim claim) => new(path, claim);

        [Fact]
        public void ACommonFilterOverAnUndeclaredContainerIsAnsweredWithoutTheEngine()
        {
            var set = Undeclared.Derive(new[]
            {
                Fact(A, new JsonClaim.Present()), Fact(A, new JsonClaim.EqualTo("k0")),
                Fact(B, new JsonClaim.Present()), Fact(B, new JsonClaim.LowerBound(500, Exclusive: true)),
            });

            set.Tables.Should().BeOfType<JsonFactOverlay>();
            set.Knows(Fact(A, new JsonClaim.OfType(JsonType.String))).Should().BeTrue();
            set.Knows(Fact(A, new JsonClaim.NotEqualTo("k1"))).Should().BeTrue();
            set.Knows(Fact(B, new JsonClaim.LowerBound(400))).Should().BeTrue();
            set.Knows(Fact(Id, new JsonClaim.OfType(JsonType.String))).Should().BeTrue("what the service states is read from its own context");
            set.Knows(Fact(Ax, new JsonClaim.Present())).Should().BeFalse("a property of a string is absent");
            set.IsContradictory.Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(WhatTheGateRefuses))]
        public void WhatTheGateRefusesGoesToTheEngine(string why, JsonFact[] established)
        {
            Undeclared.Derive(established).Tables.Should().BeOfType<JsonScopeTables>(why);
        }

        public static TheoryData<string, JsonFact[]> WhatTheGateRefuses => new()
        {
            { "two claims beside a presence meet", new[] { Fact(A, new JsonClaim.LowerBound(1)), Fact(A, new JsonClaim.UpperBound(5)) } },
            { "an empty domain is a claim no value satisfies", new[] { Fact(A, new JsonClaim.OneOf(Array.Empty<JsonScalar>())) } },
            { "a path beneath another meets it", new[] { Fact(A, new JsonClaim.EqualTo(5m)), Fact(Ax, new JsonClaim.Present()) } },
            { "the service states id", new[] { Fact(Id, new JsonClaim.EqualTo("x")) } },
            { "a path beneath one the service states meets it", new[] { Fact(Id.Property("q"), new JsonClaim.Present()) } },
            { "a claim about the root meets every path", new[] { Fact(JsonDocumentPath.Root, new JsonClaim.Present()) } },
        };

        [Fact]
        public void AConditionalDeclarationAlwaysGoesToTheEngine()
        {
            var kind = JsonDocumentPath.Root.Property("kind");
            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { Fact(kind, new JsonClaim.EqualTo("order")) }, Fact(A, new JsonClaim.Present())),
            });

            theory.Derive(new[] { Fact(B, new JsonClaim.Present()) }).Tables.Should().BeOfType<JsonScopeTables>();
        }

        [Fact]
        public void APathBeneathAServicePathTheGateRefusesTheEngineContradicts()
        {
            // The case the gate exists for: a property of id is the property of a string.
            Undeclared.Derive(new[] { Fact(Id.Property("q"), new JsonClaim.Present()) }).IsContradictory.Should().BeTrue();
        }

        /// <summary>
        /// Generated sets, every one the gate passes answered exactly as the engine answers it.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheOverlayAnswersAsTheEngineDoes(bool stated)
        {
            var theory = stated ? Stated : Undeclared;
            var random = new Random(20261014);
            var overlaid = 0;

            for (var i = 0; i < 1500; i++)
            {
                var established = Generate(random);
                var fast = theory.Derive(established);
                if (fast.Tables is not JsonFactOverlay)
                    continue;

                overlaid++;

                var beneath = Beneath(established).ToList();
                var engine = theory.DeriveOnEngine(established, beneath);
                var probes = beneath.Concat(Paths).Append(JsonDocumentPath.Root).Distinct().ToList();
                var described = string.Join(" ; ", established);

                fast.IsContradictory.Should().Be(engine.IsContradictory, described);

                foreach (var path in probes)
                {
                    fast.IsAlwaysScalar(path).Should().Be(engine.IsAlwaysScalar(path), $"IsAlwaysScalar({path}) under {described}");
                    fast.HasOneScalarType(path).Should().Be(engine.HasOneScalarType(path), $"HasOneScalarType({path}) under {described}");

                    var fastFound = fast.TryGetValue(path, out var fastValue);
                    var engineFound = engine.TryGetValue(path, out var engineValue);
                    fastFound.Should().Be(engineFound, $"TryGetValue({path}) under {described}");
                    if (fastFound)
                        fastValue.Should().Be(engineValue);

                    Describe(fast.ClaimsFor(path)).Should().Equal(Describe(engine.ClaimsFor(path)), $"ClaimsFor({path}) under {described}");

                    foreach (var claim in Questions())
                        fast.Knows(Fact(path, claim)).Should().Be(engine.Knows(Fact(path, claim)), $"Knows({path}, {Describe(claim)}) under {described}");
                }
            }

            overlaid.Should().BeGreaterThan(300, "or the comparison says little about the overlay");
        }

        /// <summary>
        /// A predicate oracle's stratum read off the overlay, over generated statements, answers as the engine's context for
        /// the same stratum does, on every path the statement reads; and knows no less anywhere else.
        /// </summary>
        /// <remarks>
        /// The overlay reads each conjunct as <c>PredicateAtom.Kept</c> does and meets them path by path; the engine reads
        /// the same atoms through the analysis rules and meets them in the theory's tables. This holds the two readings to
        /// each other. Beneath a path that holds no object the overlay says absent where the engine, not told of the path,
        /// knows nothing — so elsewhere it is held only to knowing at least as much.
        /// </remarks>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AStratumReadOffTheOverlayAnswersAsItsContextDoes(bool stated)
        {
            var theory = stated ? Stated : Undeclared;
            var random = new Random(20261015);
            var overlaid = 0;

            for (var i = 0; i < 600; i++)
            {
                var conjuncts = Enumerable.Range(0, random.Next(1, 4)).Select(_ => Conjunct(random)).ToArray();
                var scope = OracleFixture.Oracle(theory).Admit(new PredicateStatement(conjuncts), (_, _) => true);
                var fast = scope.Facts(OracleLicence.Select);
                if (fast.Tables is not JsonFactOverlay)
                    continue;

                overlaid++;

                var described = string.Join(" ; ", conjuncts.Select(c => c.Digest));
                var (context, rules) = scope.ContextFor(OracleLicence.Select);
                context.Should().NotBeNull($"the overlay is never contradictory, and nor is the engine under {described}");
                var engine = new JsonFactSet(new JsonScopeTables(rules!.Json, context!, owned: false), fast.Vocabulary);

                fast.IsContradictory.Should().BeFalse(described);
                engine.IsContradictory.Should().BeFalse(described);

                var read = conjuncts.SelectMany(c => c.Paths).ToHashSet();
                foreach (var path in Paths.Append(JsonDocumentPath.Root).Concat(read).Distinct())
                {
                    foreach (var claim in Questions())
                    {
                        var known = engine.Knows(Fact(path, claim));
                        if (read.Contains(path))
                            fast.Knows(Fact(path, claim)).Should().Be(known, $"Knows({path}, {Describe(claim)}) under {described}");
                        else if (known)
                            fast.Knows(Fact(path, claim)).Should().BeTrue($"Knows({path}, {Describe(claim)}) under {described}, which the engine knows");
                    }
                }
            }

            overlaid.Should().BeGreaterThan(150, "or the comparison says little about the overlay");
        }

        /// <summary>
        /// A verdict asked of a stratum read off the overlay is read off the engine's context for it, opened then.
        /// </summary>
        [Fact]
        public void AVerdictOfAStratumReadOffTheOverlayIsTheEngines()
        {
            var equal = new SqlPredicate.Compare("=", new SqlValue.Raw(A), SqlValue.Of("s"));
            var within = new SqlPredicate.In(new SqlValue.Raw(A), Eggnog.ValueSet.Of(SqlValue.Of("s").Value, SqlValue.Of("t").Value));
            var scope = OracleFixture.Oracle(Undeclared).Admit(new PredicateStatement(new SqlPredicate[] { equal, within }), (i, _) => i == 0);

            scope.Facts(OracleLicence.RenderConjunct(1)).Tables.Should().BeOfType<JsonFactOverlay>();
            scope.Decide(1, OracleLicence.RenderConjunct(1)).Should().Be(Apache.Calcite.Cosmos.Facts.Saturation.Truth.Always, "a = 's' is what the stratum assumes");
        }

        static readonly JsonDocumentPath[] Read = { A, B, Ax, MetaX, Id };

        static readonly SqlValue.Literal[] Literals =
        {
            SqlValue.Of("s"), SqlValue.Of("30"), SqlValue.Of("true"), SqlValue.Of(30m), SqlValue.Of(2.5m), SqlValue.Of(true),
        };

        /// <summary>
        /// A conjunct of one atom over a path, or its negation: a comparison with a literal or a parameter, a membership, a
        /// null test or a definedness test, through any reading.
        /// </summary>
        static SqlPredicate Conjunct(Random random)
        {
            var path = Read[random.Next(Read.Length)];
            SqlValue reading = random.Next(8) switch
            {
                < 3 => new SqlValue.Text(path),
                < 5 => new SqlValue.Raw(path),
                5 => new SqlValue.Typed(path, new[] { "INTEGER", "DOUBLE", "DECIMAL", "BOOLEAN" }[random.Next(4)]),
                6 => new SqlValue.Cast(new[] { "INTEGER", "DOUBLE" }[random.Next(2)], new SqlValue.Text(path)),
                _ => new SqlValue.Cast("VARCHAR", new SqlValue.Raw(path)),
            };

            SqlPredicate atom = random.Next(10) switch
            {
                0 => new SqlPredicate.IsNull(reading),
                1 => new SqlPredicate.IsDefined(new SqlValue.Raw(path)),
                2 => new SqlPredicate.In(reading, Eggnog.ValueSet.From(Enumerable.Range(0, 2).Select(_ => Literals[random.Next(Literals.Length)].Value))),
                3 => new SqlPredicate.Compare("=", reading, new SqlValue.Parameter(0, "VARCHAR")),
                _ => new SqlPredicate.Compare(new[] { "=", "<>", "<", ">=" }[random.Next(4)], reading, Literals[random.Next(Literals.Length)]),
            };

            return random.Next(4) == 0 ? new SqlPredicate.Not(atom) : atom;
        }

        /// <summary>
        /// One to four paths, most with a presence, most with one claim beside it and some with two, now and then one the
        /// gate refuses.
        /// </summary>
        static JsonFact[] Generate(Random random)
        {
            var facts = new List<JsonFact>();
            var count = random.Next(1, 5);

            foreach (var path in Paths.OrderBy(_ => random.Next()).Take(count))
            {
                if (random.NextDouble() < 0.8)
                    facts.Add(Fact(path, new JsonClaim.Present()));

                var claims = random.NextDouble() switch
                {
                    < 0.15 => 0,
                    < 0.9 => 1,
                    _ => 2,
                };

                for (var c = 0; c < claims; c++)
                    facts.Add(Fact(path, Claim(random)));
            }

            return facts.ToArray();
        }

        static JsonClaim Claim(Random random)
        {
            JsonScalar Value() => Values[random.Next(Values.Length)];
            decimal Edge() => Edges[random.Next(Edges.Length)];

            return random.Next(5) switch
            {
                0 => new JsonClaim.EqualTo(Value()),
                1 => new JsonClaim.OneOf(Enumerable.Range(0, random.Next(1, 4)).Select(_ => Value()).Distinct().ToArray()),
                2 => new JsonClaim.NotEqualTo(Value()),
                3 => new JsonClaim.LowerBound(Edge(), random.Next(2) == 0),
                _ => new JsonClaim.UpperBound(Edge(), random.Next(2) == 0),
            };
        }

        /// <summary>
        /// Every path above an established one, which the query names by naming it; two levels beneath each; and every
        /// path of the pool beneath one.
        /// </summary>
        static IEnumerable<JsonDocumentPath> Beneath(IEnumerable<JsonFact> established)
        {
            var paths = new HashSet<JsonDocumentPath>();

            foreach (var fact in established)
            {
                var path = JsonDocumentPath.Root;
                foreach (var name in fact.Path.Names)
                    paths.Add(path = path.Property(name));

                paths.Add(fact.Path.Property("d"));
                paths.Add(fact.Path.Property("d").Property("e"));

                foreach (var pooled in Paths)
                    if (pooled.Depth > fact.Path.Depth && pooled.Names.Take(fact.Path.Depth).SequenceEqual(fact.Path.Names))
                        paths.Add(pooled);
            }

            return paths;
        }

        static IEnumerable<JsonClaim> Questions()
        {
            yield return new JsonClaim.Present();

            foreach (var type in Enum.GetValues<JsonType>())
            {
                yield return new JsonClaim.OfType(type);
                yield return new JsonClaim.OfType(type, OrNull: true);
            }

            foreach (var value in Values)
            {
                yield return new JsonClaim.EqualTo(value);
                yield return new JsonClaim.NotEqualTo(value);
            }

            yield return new JsonClaim.OneOf(new JsonScalar[] { "s", "t" });
            yield return new JsonClaim.OneOf(new JsonScalar[] { 0m, 1m, 5m });
            yield return new JsonClaim.OneOf(new JsonScalar[] { true, false, JsonScalar.Null });

            foreach (var edge in Edges)
            {
                yield return new JsonClaim.LowerBound(edge);
                yield return new JsonClaim.LowerBound(edge, Exclusive: true);
                yield return new JsonClaim.UpperBound(edge);
                yield return new JsonClaim.UpperBound(edge, Exclusive: true);
            }
        }

        static string Describe(JsonClaim claim) => claim switch
        {
            JsonClaim.OneOf domain => "OneOf[" + string.Join(", ", domain.Values) + "]",
            _ => claim.ToString(),
        };

        static IEnumerable<string> Describe(IEnumerable<JsonClaim> claims) => claims.Select(Describe);

    }

}
