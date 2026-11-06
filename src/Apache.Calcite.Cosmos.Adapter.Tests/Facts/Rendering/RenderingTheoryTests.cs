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
    /// S's theory, loaded and frozen: its rules by family, the boxes' algebra and their definition, the merge of targets
    /// into a set's table, and the readers the cuts are named for. A contradicting row refused at load (T6) is gate
    /// fault G1 (<see cref="RenderingGateFaultTests"/>).
    /// </summary>
    public sealed class RenderingTheoryTests
    {

        static ValueSet<CellClass> Allowed(RenderPosition pos, Verdict v) => ProbeTheory.Theory.Get(ProbeTheory.Allowed.Of(pos, v));

        static ValueSet<CellClass> Refutes(RenderPosition pos, Verdict v) => ProbeTheory.Theory.Get(ProbeTheory.Refutes.Of(pos, v));

        /// <summary>
        /// S's rules, held by family and each by name, so a rule that moves, goes or is swapped is seen: check mode's
        /// (design 2) and generate mode's, h07's two atoms, the type test's and the affix's, h08's case (its ternary a
        /// template written twice, and a truth value's constant beside it), h10's computed places (a call's reading of its
        /// place is the bundle's), h11's pair, h12's raw columns' union, a raw value read through its column (rawtyped), the
        /// atoms a function row covers (F2), and what a number Calcite wrote cast denotes (G8). No count bounds them: what the
        /// rules cost is judged at the end-of-plan evaluation.
        /// </summary>
        [Fact]
        public void TheTheorysRulesAreHeldByFamily()
        {
            ProbeTheory.Graph.Rulesets.Where(r => r.Rules.Count > 0).ToDictionary(r => r.Name, r => r.Rules.Select(x => x.Name).ToList())
                .Should().BeEquivalentTo(new Dictionary<string, List<string>>
            {
                ["analyse"] =
                [
                    "text reads its path",
                    "a raw value reads its path",
                    "a raw value in a column reads its path through the column",
                    "a literal a cast to DOUBLE converts denotes the number it spells",
                    "a literal a cast to a DECIMAL converts denotes the number it spells",
                    "a cast to DOUBLE denotes what it converts denotes",
                    "a cast to a DECIMAL that holds the number's digits denotes what it converts denotes",
                    "a cast to a DECIMAL that drops the number's places denotes it moved",
                    "a comparison with what a cast denotes is an atom of what it reads where its reading takes it",
                    "a returning reads its path as its type",
                    "a cast reads what it converts",
                    "a comparison with a literal is an atom of what it reads",
                    "a comparison with a parameter is an atom of what it reads",
                    "a membership is an atom of what it reads",
                    "a null test is an atom of what it reads",
                    "a definedness test is an atom of what it reads",
                    "a type test is an atom of what it reads",
                    "an affix test with a literal pattern is an atom of what it reads",
                ],
                ["measure"] =
                [
                    "a measured answer is one a set's service may give",
                ],
                ["allowed"] =
                [
                    "a class within an allowing box is allowed",
                    "a class whose every truth of Calcite's the policy sets aside is one Calcite raises on alone",
                ],
                ["refutes"] =
                [
                    "a class within a refuting box is a definite counterexample",
                ],
                ["tabulate"] =
                [
                    "a class some row orders is placed by its comparators",
                    "a settled key is an entry of its set's table",
                ],
                ["expected"] =
                [
                    "every Kleene cell of a connective is expected of a set that serves a target it is Kleene's for",
                ],
                ["matched"] =
                [
                    "a settled answer that is Kleene's matches",
                ],
                ["licence"] =
                [
                    "a connective every expected cell of which matched composes in the set",
                ],
                ["disagree"] =
                [
                    "a function key some target measured covers each atom its class holds",
                    "a function key a target of the set answers otherwise than Same disagrees there",
                ],
                ["raw"] =
                [
                    "a raw reading's truths on a key are the union of its columns', and a column's reading's its own",
                ],
                ["tag"] =
                [
                    "a predicate beneath a stratum is an atom as its predicate is",
                ],
                ["structure"] =
                [
                    "a conjunction's arms are its junction's sides",
                    "a disjunction's arms are its junction's sides",
                ],
                ["descend"] =
                [
                    "a junction lies within each form of its own connective it is a candidate for",
                    "an arm of a junction that is a junction of its connective lies within what the junction lies within",
                    "each arm of a junction takes each part of a form it lies within",
                    "an atom of a junction takes the part of a form it lies within that reads its path alone",
                    "a negation's operand is a candidate for the form's operand at the opposite position",
                    "a member of a member of a junction's own connective is a member of the junction",
                ],
                ["disjoint"] =
                [
                    "two properties of one parent with different names are disjoint locations",
                ],
                ["partition"] =
                [
                    "an atom's import is cut into cells, at its literals, the form's and the edges its reader names, where the form reads that path alone",
                ],
                ["need"] =
                [
                    "a candidate of an atom is evaluated on the atom's cells",
                    "a compound that is evaluated is evaluated through its parts",
                ],
                ["parted"] =
                [
                    "a string literal parts the orders over a path whose strings may hold the other band",
                    "two string orderings of one path in a written conjunction, one of whose literals parts the orders there, need Range",
                ],
                ["evaluate"] =
                [
                    "a test is evaluated on every cell by the rows",
                    "a compound is evaluated from its parts by its connective's rows",
                ],
                ["classes"] =
                [
                    "Calcite's atom and the form are paired on every cell",
                ],
                ["gate"] =
                [
                    "a candidate agrees where every class of its cells is allowed at its position",
                    "a candidate is refuted where a class of its cells is a definite counterexample",
                    "an atom is a connective's identity on some cell",
                ],
                ["compose"] =
                [
                    "a junction agrees where both sides agree on one verdict, as the part of a form it lies within that they make",
                    "a negation agrees where its operand agrees at the opposite position",
                    "a junction is refuted where one side is refuted and the other is its identity on another location, as the part of a form it lies within that they make",
                    "a negation is refuted where its operand is refuted at the opposite position",
                ],
                ["conflict"] =
                [
                    "an agreement and a refutation of one candidate are a contradiction",
                ],
                ["at"] =
                [
                    "a junction's arms stand where it stands",
                    "a negation's operand stands at the opposite position",
                    "a case's condition stands at Pos and at Neg, wherever the case stands",
                    "a case's arms stand where the case stands",
                ],
                ["spell"] =
                [
                    "a template that fits an atom proposes its form at the atom's position",
                ],
                ["lift"] =
                [
                    "an exact agreement that rests on something and may be sent is a best candidate",
                    "an implied agreement that rests on something, may be sent and can be written as a Rex is implied",
                ],
                ["strict"] =
                [
                    "a reading Calcite answers null over an absent path is strict in its path",
                    "a call whose operator answers null for a null operand is strict in what an operand is strict in",
                    "a predicate over a strict value implies its path present",
                ],
                ["best"] =
                [
                    "a junction's best is the join of its arms' bests",
                    "a negation's best is its operand's best at the opposite position, negated",
                    "a case whose else absorbs at its position is its condition joined with its then-arm",
                    "a case whose else is the opposite constant, over a two-valued condition, is its condition's form joined with its then-arm",
                    "a case's best is the service ternary of its condition's Pos best and its arms' bests, the narrow else over a two-valued condition",
                    "a case's best is the service ternary of its condition's Pos best and its arms' bests, the wide else",
                    "a truth value is exact as the constant its position reads it as",
                ],
                ["weaken"] =
                [
                    "a leaf whose exact form can be written as a Rex weakens to it",
                    "any other leaf weakens to what its implied forms make together",
                    "a junction's weakening joins its arms' weakenings",
                    "a negation's weakening is its operand's at the opposite position, negated",
                    "a case weakens to its arms' weakenings under its condition's",
                ],
                ["twovalued"] =
                [
                    "an atom none of whose classes Calcite answers unknown on is two-valued",
                ],
                ["place"] =
                [
                    "a call's operands' imports are gathered beneath each stratum, one an ordinal",
                    "a pair's import beneath a stratum is the product of its places' imports there",
                ],
                ["image"] =
                [
                    "a place's import beneath a stratum is its call's image, once every operand is in and every key agrees",
                    "a place's import is its import beneath the root",
                ],
                ["assume"] =
                [
                    "an atom over a computed place is assumed as its place's image beneath the atom's stratum",
                ],
                ["pair"] =
                [
                    "a comparison of two readings is an atom of their pair",
                ],
            });
            ProbeTheory.Graph.Rulesets.Sum(r => r.Rules.Count).Should().Be(85);
        }

        /// <summary>The classes Calcite raises on alone follow the policy: what it sets aside, and nothing where it sets none.</summary>
        [Fact]
        public void TheRaiseOnlyClassesAreTheOnesThePolicySetsAside()
        {
            ProbeTheory.Theory.Get(ProbeTheory.RaiseOnly.Of()).Should().BeEquivalentTo(AgreementRows.Classes.Where(k => k.Calcite == Truths.Raises));
            var strict = RenderingTheory.Define(ProbeRows.Planted(ProbeRows.Rows) with { Tolerated = Truths.None });
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
            var theory = RenderingTheory.Define(ProbeRows.Planted([.. ProbeRows.Rows,
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
        /// Each row the foundation reads in (F3) is a fact of S as the measured definition carries it, and the definition
        /// carries some of each: what the groups that read them start from. A function's value is the union of its rows.
        /// </summary>
        [Fact]
        public void TheFoundationsRowsAreFactsOfS()
        {
            var (d, s) = (Measured, ProbeTheory);
            new[] { d.Parts.Count, d.Moves.Count, d.StoredAs.Count, d.Truncations.Count, d.PairReadings.Count, d.Boxes.Count, d.SeesThrough.Count, d.StructureOnly.Count,
                d.CalciteReadings.Count, d.CalciteBands.Count, d.Casts.Count, d.ReaderOrders.Count, d.TestKinds.Count, d.Comparands.Count }.Should().OnlyContain(n => n > 0);

            d.Parts.Should().OnlyContain(r => s.Theory.Check(s.Parts.Of(r.Comparator, r.Band, r.Other)));
            d.Moves.Should().OnlyContain(r => s.Theory.Check(s.Moves.Of(r.Comparator, r.Band)));
            d.StoredAs.Should().OnlyContain(r => s.Theory.Check(s.StoredAs.Of(r.Target, r.Written, r.Stored)));
            d.Truncations.Should().OnlyContain(r => s.Theory.Check(s.Truncates.Of(r.Target, r.Test, r.Shape, r.Band)));
            d.PairReadings.Should().OnlyContain(r => s.Theory.Check(s.PairReads.Of(r.Test, r.Swapped, r.Reads)));
            d.Boxes.Should().OnlyContain(r => s.Theory.Check(s.BoxOf.Of(r.Kind, r.Box)));
            d.SeesThrough.Should().OnlyContain(r => s.Theory.Check(s.SeesThrough.Of(r)));
            d.StructureOnly.Should().OnlyContain(r => s.Theory.Check(s.StructureOnly.Of(r)));
            d.CalciteReadings.Should().OnlyContain(r => s.Theory.Check(s.CalciteReads.Of(r.Reader, r.Kind, r.Answer)));
            d.Casts.Should().OnlyContain(r => s.Theory.Check(s.MeasuredCast.Of(r.Target, r.Reader, r.Row)));
            d.Comparands.Should().OnlyContain(r => s.Theory.Check(s.TakesConstant.Of(r.Reading, r.Moved, r.Row)));
            foreach (var g in d.CalciteBands.GroupBy(r => (r.Reader, r.Kind)))
                s.Theory.Get(s.CalciteBands.Of(g.Key.Reader, g.Key.Kind)).Should().BeEquivalentTo(g.Select(r => r.Band));
            foreach (var g in d.ReaderOrders.GroupBy(r => r.Reader))
                s.Theory.Get(s.ReaderOrder.Of(g.Key)).Should().BeEquivalentTo(g.Select(r => r.Claim));
            foreach (var g in d.TestKinds.GroupBy(r => r.Test))
                s.Theory.Get(s.TestKinds.Of(g.Key)).Should().BeEquivalentTo(g.Select(r => r.Kind));
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
                // The text's parse as a boolean, which S's reading builds (h14's triage).
                .Append(PredicateAtom.CastReader("BOOLEAN", PredicateAtom.TextReader))
                .ToList();

            // Each raw reading again through each declared column (rawtyped).
            built.AddRange([.. from reader in built where PredicateAtom.IsRaw(reader) || reader.EndsWith(":" + PredicateAtom.RawReader, StringComparison.Ordinal)
                               from column in PredicateAtom.RawColumns select PredicateAtom.InColumn(reader, column)]);

            // A numeric cast of the raw value is held back until a Calcite row measures it, through a column or not.
            var covered = built.Where(reader => reader.StartsWith("cast:", StringComparison.Ordinal) == false || reader.EndsWith(":" + PredicateAtom.TextReader, StringComparison.Ordinal)
                || PredicateAtom.Columnless(reader) == PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)).ToList();
            ReaderEdgesTable.Rows.Keys.Should().BeEquivalentTo(covered).And.BeSubsetOf(built);
            covered.Should().OnlyContain(reader => ProbeTheory.Theory.Check(Fact.Exists(ProbeTheory.Edges.Of(reader))));
        }

    }

}
