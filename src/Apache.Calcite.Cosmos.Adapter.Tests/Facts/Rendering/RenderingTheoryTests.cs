using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// S's theory, loaded and frozen: the rule count, the boxes' algebra and their definition, the merge of targets into
    /// a set's table, a contradicting row refused at load (T6, G1), and the readers the cuts are named for.
    /// </summary>
    public sealed class RenderingTheoryTests
    {

        static readonly Lazy<RenderingTheory> Probe = new(() => RenderingTheory.Define(RenderingDefinition.Measured(ProbeRows.Rows)));

        static ValueSet<CellClass> Allowed(RenderPosition pos, Verdict v) => Probe.Value.Theory.Get(Probe.Value.Allowed.Of(pos, v));

        static ValueSet<CellClass> Refutes(RenderPosition pos, Verdict v) => Probe.Value.Theory.Get(Probe.Value.Refutes.Of(pos, v));

        [Fact]
        public void TheTheoryHasAtMostFortyRules() => Probe.Value.Graph.Rulesets.Sum(r => r.Rules.Count).Should().BeLessThanOrEqualTo(40);

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
            var once = MeasuredKey.Of(CosmosServiceTest.IsBool, None, Number);
            var twice = MeasuredKey.Of(CosmosServiceTest.IsBool, None, CosmosValueClass.String);
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
        /// T6, gate fault G1: a second row for one key and target that answers otherwise is refused at load, naming its
        /// axiom, without faulting the theory; a definition from the right rows answers as before.
        /// </summary>
        [Fact]
        public void T6_AContradictingRowIsRefusedAtLoad()
        {
            var key = MeasuredKey.Of(CosmosServiceTest.NotEqual, CosmosValueClass.String, Null);
            var refusal = FluentActions.Invoking(() => RenderingTheory.Define(RenderingDefinition.Measured(
                [.. ProbeRows.Rows, new("Service.Planted", key, RenderingTarget.Azure, CosmosAnswer.False)]))).Should().Throw<FactConflictException>().Which;
            (refusal.Table.Name, refusal.Data["axiom"], refusal.Data["faulted"]).Should().Be(("outcome", "Service.Planted", false));

            Probe.Value.Theory.Get(Probe.Value.Final.Of(key.Test, key.Operand, key.Cell, RenderingSet.Portable)).Should().Equal(ValueSet.Of(CosmosAnswer.True));
        }

        /// <summary>
        /// Text and raw have the row their cuts read, and every row names a reader the loader builds (<c>CosmosPredicateLoader.Value</c>
        /// and <c>Cast</c>). A text cast of the raw value has none: over a <c>VARIANT</c> Calcite casts all but a string to
        /// null, which its cells do not model, so an atom read through it is never evaluated.
        /// </summary>
        [Fact]
        public void EveryReaderTheLoaderBuildsHasAnEdgesRow()
        {
            string[] scalars = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE", "DECIMAL", "BOOLEAN"];
            var built = new[] { PredicateAtom.TextReader, PredicateAtom.RawReader, PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader) }
                .Concat(scalars.Select(PredicateAtom.TypedReader))
                .Concat(from target in new[] { "INTEGER", "BIGINT", "DOUBLE" } from inner in new[] { PredicateAtom.TextReader, PredicateAtom.RawReader } select PredicateAtom.CastReader(target, inner))
                .ToList();

            var covered = built.Take(2).ToList();
            ReaderEdgesTable.Rows.Keys.Should().BeEquivalentTo(covered).And.BeSubsetOf(built);
            covered.Should().OnlyContain(reader => Probe.Value.Theory.Check(Fact.Exists(Probe.Value.Edges.Of(reader))));
        }

    }

}
