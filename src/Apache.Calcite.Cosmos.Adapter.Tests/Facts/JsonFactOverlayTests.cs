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
    /// What a statement's stratum assumes, read without the engine where the engine could add nothing, held to what the
    /// engine answers.
    /// </summary>
    /// <remarks>
    /// The overlay is a claim that a handful of rules of JSON are all that apply when its gate passes. That is checked the
    /// only way it can be: generated statements go through both, and every question a consumer asks of a fact set is asked
    /// of both, about every path either could know something of.
    /// </remarks>
    public class JsonFactOverlayTests
    {

        static readonly JsonDocumentPath A = JsonDocumentPath.Root.Property("a");
        static readonly JsonDocumentPath B = JsonDocumentPath.Root.Property("b");
        static readonly JsonDocumentPath Ax = A.Property("x");
        static readonly JsonDocumentPath Ay = A.Property("y");
        static readonly JsonDocumentPath Bzw = B.Property("z").Property("w");
        static readonly JsonDocumentPath Id = JsonDocumentPath.Root.Property("id");
        static readonly JsonDocumentPath ETag = JsonDocumentPath.Root.Property("_etag");
        static readonly JsonDocumentPath Ts = JsonDocumentPath.Root.Property("_ts");
        static readonly JsonDocumentPath Meta = JsonDocumentPath.Root.Property("meta");
        static readonly JsonDocumentPath MetaOwner = Meta.Property("owner");
        static readonly JsonDocumentPath MetaX = Meta.Property("x");

        /// <summary>The service's facts alone: the theory of every container that declares no schema.</summary>
        static readonly JsonFactTheory Undeclared = new(CosmosServiceFacts.Rules);

        /// <summary>
        /// The service's facts and unconditional ones beneath a path a query can also name a sibling of: a string, of
        /// characters a pattern would declare.
        /// </summary>
        static readonly JsonFactTheory Stated = new(CosmosServiceFacts.Rules.Concat(new[]
        {
            JsonFactRule.Unconditional(new JsonFact(MetaOwner, new JsonClaim.Present())),
            JsonFactRule.Unconditional(new JsonFact(MetaOwner, new JsonClaim.OfType(JsonType.String))),
            JsonFactRule.Unconditional(new JsonFact(MetaOwner, new JsonClaim.Characters(JsonCharacterBand.HighBmp))),
        }));

        static readonly JsonDocumentPath[] Paths = { A, B, Ax, Ay, Bzw, MetaX, Id, Ts, ETag, Id.Property("q"), MetaOwner };

        /// <summary>
        /// Strings from all three bands a string's characters are known by: a high unit, a supplementary character, both,
        /// and a character below U+D800 that is no ASCII.
        /// </summary>
        static readonly string[] Banded = { "\uE000", "a\uD83D\uDE00", "\uFF21\uD83D\uDE00", "\u4E2D" };

        static readonly JsonScalar[] Values =
        {
            "s", "t", "", 0m, 1m, 5m, -3m, 2.5m, 1e20m, 1e300, true, false, JsonScalar.Null, "\uE000", "a\uD83D\uDE00",
        };

        static readonly decimal[] Edges = { -3m, 0m, 1m, 2.5m, 5m, 100m };

        static JsonFact Fact(JsonDocumentPath path, JsonClaim claim) => new(path, claim);

        /// <summary>
        /// What a statement's conjuncts all keep, read off the overlay or the engine's context for it.
        /// </summary>
        static JsonFactSet Kept(JsonFactTheory theory, params SqlPredicate[] conjuncts) =>
            OracleFixture.Oracle(theory).Admit(new PredicateStatement(conjuncts)).Facts(OracleLicence.Kept);

        static SqlPredicate Equal(JsonDocumentPath path, JsonScalar value) => new SqlPredicate.Compare("=", new SqlValue.Raw(path), SqlValue.Of(value));

        static SqlPredicate Defined(JsonDocumentPath path) => new SqlPredicate.IsDefined(new SqlValue.Raw(path));

        /// <summary>The cast to a number that reads every rendering, which is what bounds a path.</summary>
        static SqlValue Number(JsonDocumentPath path) => new SqlValue.Cast("DOUBLE", new SqlValue.Text(path));

        [Fact]
        public void ACommonFilterOverAnUndeclaredContainerIsAnsweredWithoutTheEngine()
        {
            var set = Kept(Undeclared, Equal(A, "k0"), new SqlPredicate.Compare(">", Number(B), SqlValue.Of(500m)));

            set.Tables.Should().BeOfType<JsonFactOverlay>();
            set.Knows(Fact(A, new JsonClaim.OfType(JsonType.String))).Should().BeTrue();
            set.Knows(Fact(A, new JsonClaim.NotEqualTo("k1"))).Should().BeTrue();
            set.Knows(Fact(B, new JsonClaim.LowerBound(400))).Should().BeTrue();
            set.Knows(Fact(Id, new JsonClaim.OfType(JsonType.String))).Should().BeTrue("what the service states is read from its own context");
            set.Knows(Fact(Ax, new JsonClaim.Present())).Should().BeFalse("a property of a string is absent");
            set.IsContradictory.Should().BeFalse();
        }

        [Theory]
        [InlineData("two bounds may cross")]
        [InlineData("a path beneath another meets it")]
        [InlineData("the service states id")]
        [InlineData("a path beneath one the service states meets it")]
        [InlineData("a claim about the root meets every path")]
        [InlineData("an assumed disjunction is cases")]
        public void WhatTheGateRefusesGoesToTheEngine(string why)
        {
            Kept(Undeclared, WhatTheGateRefuses[why]).Tables.Should().NotBeOfType<JsonFactOverlay>(why);
        }

        static readonly Dictionary<string, SqlPredicate[]> WhatTheGateRefuses = new()
        {
            { "two bounds may cross", new[] { new SqlPredicate.Compare(">=", Number(A), SqlValue.Of(1m)), new SqlPredicate.Compare("<=", Number(A), SqlValue.Of(5m)) } },
            { "a path beneath another meets it", new[] { Equal(A, 5m), Defined(Ax) } },
            { "the service states id", new[] { Equal(Id, "x") } },
            { "a path beneath one the service states meets it", new[] { Defined(Id.Property("q")) } },
            { "a claim about the root meets every path", new[] { Defined(JsonDocumentPath.Root) } },
            { "an assumed disjunction is cases", new[] { new SqlPredicate.Or(new[] { Equal(A, "s"), Equal(B, "t") }) } },
        };

        [Fact]
        public void AConditionalDeclarationAlwaysGoesToTheEngine()
        {
            var kind = JsonDocumentPath.Root.Property("kind");
            var theory = new JsonFactTheory(new[]
            {
                new JsonFactRule(new[] { Fact(kind, new JsonClaim.EqualTo("order")) }, Fact(A, new JsonClaim.Present())),
            });

            Kept(theory, Defined(B)).Tables.Should().NotBeOfType<JsonFactOverlay>();
        }

        [Fact]
        public void APathBeneathAServicePathTheGateRefusesTheEngineContradicts()
        {
            // The case the gate exists for: a property of id is the property of a string.
            Kept(Undeclared, Defined(Id.Property("q"))).IsContradictory.Should().BeTrue();
        }

        /// <summary>
        /// A predicate oracle's stratum read off the overlay, over generated statements, answers as the engine's context for
        /// the same stratum does, on every path the statement reads; and knows no less anywhere else.
        /// </summary>
        /// <remarks>
        /// The overlay reads each conjunct as <c>PredicateAtom.Kept</c> does and meets them path by path; the engine reads
        /// the same atoms through the analysis rules and meets them in the theory's tables. This holds the two readings to
        /// each other, the characters of a path's strings among them: literals from all three bands, and a service fact and
        /// a declaration stating them. Beneath a path that holds no object the overlay says absent where the engine, not
        /// told of the path, knows nothing — so elsewhere it is held only to knowing at least as much.
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
                    // What a consumer asks of a fact set beside a claim, on every path the statement reads.
                    if (read.Contains(path))
                    {
                        fast.HasOneScalarType(path).Should().Be(engine.HasOneScalarType(path), $"HasOneScalarType({path}) under {described}");

                        var fastFound = fast.TryGetConstant(path, out var fastValue);
                        fastFound.Should().Be(engine.TryGetConstant(path, out var engineValue), $"TryGetConstant({path}) under {described}");
                        if (fastFound)
                            fastValue.Should().Be(engineValue, $"TryGetConstant({path}) under {described}");

                        Describe(fast.ClaimsFor(path)).Should().Equal(Describe(engine.ClaimsFor(path)), $"ClaimsFor({path}) under {described}");
                        fast.CharactersOf(path).Should().Be(engine.CharactersOf(path), $"CharactersOf({path}) under {described}");
                    }
                    else if (engine.CharactersOf(path) is var bands && bands != JsonCharacterBand.Any)
                        (fast.CharactersOf(path) & ~bands).Should().Be(JsonCharacterBand.None, $"CharactersOf({path}) under {described}, which the engine knows");

                    foreach (var claim in Questions())
                    {
                        var known = engine.Knows(Fact(path, claim));
                        if (read.Contains(path))
                            fast.Knows(Fact(path, claim)).Should().Be(known, $"Knows({path}, {Describe(claim)}) under {described}");
                        else if (known)
                            fast.Knows(Fact(path, claim)).Should().BeTrue($"Knows({path}, {Describe(claim)}) under {described}, which the engine knows");
                    }
                }

                // The context is the scope's, and is read here past the scope's last use: in Release the JIT lets the
                // scope go at ContextFor, and its lease's finalizer disposes the context, which then answers less.
                GC.KeepAlive(scope);
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
            new[] { SqlValue.Of("s"), SqlValue.Of("30"), SqlValue.Of("true"), SqlValue.Of(30m), SqlValue.Of(2.5m), SqlValue.Of(true) }
                .Concat(Banded.Select(text => SqlValue.Of(text))).ToArray();

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
            yield return new JsonClaim.OneOf(new JsonScalar[] { "s", "\uE000" });

            foreach (var bands in new[] { JsonCharacterBand.None, JsonCharacterBand.Surrogates, JsonCharacterBand.HighBmp, JsonCharacterBand.Any })
                yield return new JsonClaim.Characters(bands);

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
