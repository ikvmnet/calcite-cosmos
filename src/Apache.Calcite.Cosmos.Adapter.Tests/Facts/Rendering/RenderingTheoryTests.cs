using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// S's theory, loaded and frozen: the rule count, the boxes' algebra and their definition, the merge of targets into
    /// a set's table, and the readers the cuts are named for. A contradicting row refused at load (T6) is gate fault G1
    /// (<see cref="RenderingGateFaultTests"/>).
    /// </summary>
    public sealed class RenderingTheoryTests
    {

        static ValueSet<CellClass> Allowed(RenderPosition pos, Verdict v) => ProbeTheory.Theory.Get(ProbeTheory.Allowed.Of(pos, v));

        static ValueSet<CellClass> Refutes(RenderPosition pos, Verdict v) => ProbeTheory.Theory.Get(ProbeTheory.Refutes.Of(pos, v));

        /// <summary>
        /// Stage 3's line: check mode's 42 (design 2) and generate mode's 14, h07's two atoms, the type test's and the
        /// affix's, and h08's case, eight by the spec's count and ten as built: its ternary a template written twice, and a
        /// truth value's constant beside it; h10's computed places, six; h11's pair, two; h12's raw columns' union, one, 77 in
        /// all. Each family's count is held, so a rule that moves is seen.
        /// </summary>
        [Fact]
        public void TheTheoryHasAtMostEightyRules()
        {
            ProbeTheory.Graph.Rulesets.Sum(r => r.Rules.Count).Should().BeLessThanOrEqualTo(80);
            ProbeTheory.Graph.Rulesets.Where(r => r.Rules.Count > 0).ToDictionary(r => r.Name, r => r.Rules.Count).Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["measure"] = 1, ["allowed"] = 2, ["refutes"] = 1, ["tabulate"] = 1, ["expected"] = 1, ["matched"] = 1, ["licence"] = 1,
                ["analyse"] = 11, ["tag"] = 1, ["structure"] = 2, ["descend"] = 6, ["disjoint"] = 1,
                ["partition"] = 1, ["need"] = 2, ["parted"] = 2, ["evaluate"] = 2, ["classes"] = 1, ["gate"] = 3, ["compose"] = 4, ["conflict"] = 1,
                ["at"] = 4, ["spell"] = 1, ["lift"] = 2, ["strict"] = 3, ["twovalued"] = 1, ["best"] = 7, ["weaken"] = 5,
                ["valued"] = 1, ["disagree"] = 1, ["place"] = 2, ["image"] = 2, ["assume"] = 1, ["pair"] = 1, ["raw"] = 1,
            });
            ProbeTheory.Graph.Rulesets.Sum(r => r.Rules.Count).Should().Be(77);
        }

        /// <summary>The classes Calcite raises on alone follow the policy: what it sets aside, and nothing where it sets none.</summary>
        [Fact]
        public void TheRaiseOnlyClassesAreTheOnesThePolicySetsAside()
        {
            ProbeTheory.Theory.Get(ProbeTheory.RaiseOnly.Of()).Should().BeEquivalentTo(AgreementRows.Classes.Where(k => k.Calcite == Truths.Raises));
            var strict = RenderingTheory.Define(RenderingDefinition.Measured(ProbeRows.Rows) with { Tolerated = Truths.None });
            strict.Theory.Get(strict.RaiseOnly.Of()).Should().BeEmpty();
        }

        /// <summary>
        /// An exact form is implied, an implied form's refutation refutes it as exact, and no class is both allowed and a
        /// refutation: what composition and the conflict rule rely on.
        /// </summary>
        [Fact]
        public void TheBoxesAreNestedAndDisjoint()
        {
            foreach (var pos in Enum.GetValues<RenderPosition>())
            {
                Allowed(pos, Verdict.Equal).IsSubsetOf(Allowed(pos, Verdict.Implies)).Should().BeTrue();
                Refutes(pos, Verdict.Implies).IsSubsetOf(Refutes(pos, Verdict.Equal)).Should().BeTrue();
                foreach (var v in Enum.GetValues<Verdict>())
                    Allowed(pos, v).Overlaps(Refutes(pos, v)).Should().BeFalse();
            }
        }

        /// <summary>
        /// Each of the 225 classes is allowed, and refutes, exactly where F2 says under decision 2, read pair by pair: any
        /// truth Calcite may give on the cell may meet any outcome the service may give there.
        /// </summary>
        [Fact]
        public void EveryClassIsAllowedAndRefutesAsTheDefinitionSays()
        {
            foreach (var pos in Enum.GetValues<RenderPosition>())
            {
                foreach (var v in Enum.GetValues<Verdict>())
                {
                    var (allowed, refutes) = (Allowed(pos, v), Refutes(pos, v));
                    for (var c = 1; c < 16; c++)
                    {
                        for (var s = 1; s < 16; s++)
                        {
                            var k = new CellClass((Truths)c, (ServiceOutcomes)s);
                            allowed.Contains(k).Should().Be(Holds(k, pos, v), $"{k} allowed for {v} at {pos}");
                            refutes.Contains(k).Should().Be(Parts(k, pos, v), $"{k} refutes {v} at {pos}");
                        }
                    }
                }
            }
        }

        // A position keeps T or t at Pos, and F or f at Neg. Equal keeps alike, and Implies keeps on the service what
        // Calcite keeps. A raise is refined by anything; a failure of the service is never tolerated.
        static bool Holds(CellClass k, RenderPosition pos, Verdict v) =>
            (k.Service & ServiceOutcomes.Fails) == 0 && Pairs(k).All(p => p.Calcite == Truths.Raises
                || (v == Verdict.Equal ? Keeps(p.Calcite, pos) == Keeps(p.Service, pos) : Keeps(p.Calcite, pos) == false || Keeps(p.Service, pos)));

        // Every pair on the cell breaks the verdict, and Calcite answers on every row of it.
        static bool Parts(CellClass k, RenderPosition pos, Verdict v) =>
            (k.Calcite & Truths.Raises) == 0 && Pairs(k).All(p => p.Service == ServiceOutcomes.Fails
                || (v == Verdict.Equal ? Keeps(p.Calcite, pos) != Keeps(p.Service, pos) : Keeps(p.Calcite, pos) && Keeps(p.Service, pos) == false));

        static bool Keeps(Truths x, RenderPosition pos) => x == (pos == RenderPosition.Pos ? Truths.True : Truths.False);

        static bool Keeps(ServiceOutcomes y, RenderPosition pos) => y == (pos == RenderPosition.Pos ? ServiceOutcomes.True : ServiceOutcomes.False);

        static IEnumerable<(Truths Calcite, ServiceOutcomes Service)> Pairs(CellClass k) =>
            from x in Enumerable.Range(0, 4) where ((int)k.Calcite & 1 << x) != 0
            from y in Enumerable.Range(0, 4) where ((int)k.Service & 1 << y) != 0
            select ((Truths)(1 << x), (ServiceOutcomes)(1 << y));

        /// <summary>
        /// A key is settled in a set once every target the set serves measured it: one the emulator alone measured is in
        /// the emulator's table and not the portable one, and one the two targets answered differently is one entry holding
        /// both answers and their outcomes.
        /// </summary>
        [Fact]
        public void TheTargetsMergeIntoOneEntryForEachSettledKey()
        {
            // An ordering against a boolean, which no row measures.
            var once = MeasuredKey.Of(CosmosServiceTest.LessOrEqual, True, True);
            var twice = MeasuredKey.Of(CosmosServiceTest.LessOrEqual, True, False);
            var theory = RenderingTheory.Define(RenderingDefinition.Measured([.. ProbeRows.Rows,
                new("Service.Planted", once, RenderingTarget.Emulator, CosmosAnswer.False),
                new("Service.Planted", twice, RenderingTarget.Emulator, CosmosAnswer.False),
                new("Service.Planted", twice, RenderingTarget.Azure, CosmosAnswer.Undefined)]));

            var portable = theory.Theory.Get(theory.Table.Of(RenderingSet.Portable));
            portable.Should().NotContain(e => e.Key == once);
            theory.Theory.Get(theory.Table.Of(RenderingSet.Emulator)).Should().ContainSingle(e => e.Key == once);
            portable.Single(e => e.Key == twice).Should().Be(new MeasuredEntry(twice, ValueSet.Of(CosmosAnswer.False, CosmosAnswer.Undefined),
                ServiceOutcomes.False | ServiceOutcomes.Undefined, ValueSet.Of("Service.Planted")));
        }

        /// <summary>
        /// Every reader the loader builds has the row its cuts read (<c>CosmosPredicateLoader.Value</c> and <c>Cast</c>), and
        /// every row names one. A numeric cast of the raw value has none: each column casts its own way, which no row
        /// measures, so an atom read through it is never evaluated.
        /// </summary>
        [Fact]
        public void EveryReaderTheLoaderBuildsHasAnEdgesRow()
        {
            string[] scalars = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE", "DECIMAL", "BOOLEAN"];
            var built = new[] { PredicateAtom.TextReader, PredicateAtom.RawReader, PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader) }
                .Concat(scalars.Select(PredicateAtom.TypedReader))
                // A computed text place's reader (h10): the loader types a call VARCHAR or CHAR.
                .Concat(new[] { "VARCHAR", "CHAR" }.Select(PredicateAtom.TypedReader))
                .Concat(from target in new[] { "INTEGER", "BIGINT", "DOUBLE" } from inner in new[] { PredicateAtom.TextReader, PredicateAtom.RawReader } select PredicateAtom.CastReader(target, inner))
                .ToList();

            // A numeric cast of the raw value is held back until a Calcite row measures it.
            var covered = built.Where(reader => reader.StartsWith("cast:", StringComparison.Ordinal) == false || reader.EndsWith(":" + PredicateAtom.TextReader, StringComparison.Ordinal)
                || reader == PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)).ToList();
            ReaderEdgesTable.Rows.Keys.Should().BeEquivalentTo(covered).And.BeSubsetOf(built);
            covered.Should().OnlyContain(reader => ProbeTheory.Theory.Check(Fact.Exists(ProbeTheory.Edges.Of(reader))));
        }

    }

}
