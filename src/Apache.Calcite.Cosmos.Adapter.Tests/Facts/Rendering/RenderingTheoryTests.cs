using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
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
        /// atoms a function row covers (F2), what a number Calcite wrote cast denotes (G8), and what the rows of characters make
        /// of each set and leaf: the spelling a set is sent, its parting bands and unsendable classes, the paths it may write,
        /// and what a leaf reads beside its table (G1); what reads each settled key and how it reads a whole number, and what
        /// a class is to the gate (G2); a pair's readings, its table and the rows that license it, and what each reading's
        /// order holds (G3); a failing side's key, a junction holding its connective's unit, and a weakening written as its
        /// parts stand, regrouped and folded by the same laws (G4); each set's table
        /// as the leaves read it; and what a computed call is keyed by, what Calcite answers over each key and the service
        /// alone defines, the images a call's rows license, and the case-insensitive affix a LIKE over a fold is written as
        /// (G6); and what Calcite makes of each reading, <c>LIKE</c>'s units and what each type test is TRUE of, the readings
        /// vocabulary's, which the facts graph runs too (G7). No count bounds them: what the rules cost is judged at the
        /// end-of-plan evaluation.
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
                    "a test reads the text's path as its row says the body reads it",
                    "a test reads a raw value's path as its row says the body reads it",
                    "a test reads the path of a raw value in a column as its row says the body reads it",
                    "a test reads a returning's path as its row says the body reads it",
                    "a definedness test is an atom of what its body reads",
                    "a type test is an atom of what its body reads, TRUE of the kinds its row says",
                    "an affix test with a literal pattern is an atom of what it reads",
                    "a comparison that sees through a box, of a reading and a literal boxed alike, is an atom of the reading",
                    "a comparison that sees through a box, of a raw value and a literal boxed as a value of its kind is, is an atom of the raw value",
                    "a comparison that sees through a box, of a raw value in a column and a literal boxed as a value of its kind is, is an atom of the raw value",
                    "a comparison with SQL's null, by an operator null over a null operand, is an atom of what it reads",
                    "a null test of a box of a type null where what it holds is is an atom of the reading it holds",
                ],
                ["measure"] =
                [
                    "a measured answer is one a set's service may give",
                    "a target a set serves whose rows fold a test's case-insensitive form is the set's, with the bands its row parts at",
                    "a target a set serves whose rows read a unit of LIKE's pattern is the set's, by the row",
                ],
                ["allowed"] =
                [
                    "a class of a value that is no boolean reads as a whole condition over one reads",
                    "a class of a boolean reads as it is",
                    "a class within an allowing box is allowed",
                    "a class set aside is allowed within a box admitting its outcomes",
                    "a class whose every truth of Calcite's the policy sets aside is one Calcite raises on alone",
                    "a class set aside is one Calcite raises on alone",
                    "a class inside that reads as a connective's identity is one",
                    "a class inside whose truths of Calcite's hold UNKNOWN is one Calcite answers unknown on",
                ],
                ["refutes"] =
                [
                    "a class within a refuting box is a definite counterexample",
                ],
                ["tabulate"] =
                [
                    "a class whose key over no cell names a comparator is read by its test's readers",
                    "a key every target its set serves measured is settled",
                    "a comparator whose spelling rewrites a band rewrites",
                    "a fold every target its set serves has rows of is the set's, outside the bands any parts at",
                    "a unit of LIKE's pattern every target its set serves reads so is the set's, by the row",
                ],
                ["sets"] =
                [
                    "a set whose string order compares a spelling that rewrites a band is sent that spelling, the band respelled",
                    "a set whose string order compares no spelling that rewrites is sent as written",
                    "two bands a set's string comparator ranks apart are its parting bands",
                    "a class no parameter is sent holding on a target a set serves is unsendable under it",
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
                    "a function key Calcite answers a value or null over covers each atom its class holds",
                    "a function key the service defines covers each atom its class holds",
                ],
                ["raw"] =
                [
                    "a raw reading's null test over a class is what its column reads the class as means to it",
                    "a raw reading's comparison over a class its column reads as null or raises on is what that read means to it",
                    "a raw reading's comparison of a value with a literal of its type is the comparison as the type, which the cell decides",
                    "a raw reading's comparison of a value with a literal of another type is what its column answers across types",
                    "a cast of a raw value compares a value it reads as itself as itself, which the cell decides",
                    "a cast of a raw value compares the text it renders a value as, which the cell decides",
                    "a raw reading's comparison its column does not compile fails the statement",
                    "a raw reading's answer on a key is the union of its columns', and a column's reading's its own",
                    "a raw column no row measured answers each key as the union of those that were",
                    "a reading reads the classes its rows name",
                    "a raw column raising over a class refines the text over it",
                    "a raw column reading a class as the text reads it refines the text over it",
                    "a function's operand is read through what its rows were measured through",
                ],
                ["readas"] =
                [
                    "a raw column refining the text over every class the text reads is read as it over a function measured through the text",
                ],
                ["reads"] =
                [
                    "a reading reads each class as its rows say",
                    "a reading rests on each row that names it",
                    "a reading's parse of a class is decided over the character classes its witnesses held",
                    "a reading a row reads as another is read as it over each class both read alike",
                    "a reading's test of a class answers what the truth table says its read of the class means",
                    "a reading's comparison of a class read as the value read decides it is decided on the cell",
                    "a reading whose test a row says is its operand's answers over each class what its operand's read of it means, beside its own",
                    "LIKE reads each unit of its pattern as its row says",
                    "a type test is TRUE, by its name, of the JSON kinds of each class its row says",
                    "a reading whose test is its operand's rests on its operand's rows",
                    "a raw column's reading reads each class as its column's rows say, and the reading through every column as each",
                    "a raw column no row measured reads each class as the union of those that were",
                    "a raw column no row measured is null over an absent path where the union's reading is",
                    "a raw column no row measured holds each order the union's reading holds",
                ],
                ["roles"] =
                [
                    "the answers of a key's role the leaf evaluates read it, where it names one and no comparator of another",
                    "a reader of a key reads a whole number as its rows say",
                ],
                ["failing"] =
                [
                    "a failing side's key answers what each key it stands for answers",
                ],
                ["pairs"] =
                [
                    "a pair reads as its left against a literal of its right's value",
                    "a pair reads as its right against a literal of its left's value, the comparator mirrored",
                    "a pair with a side absent reads as the row of one",
                ],
                ["entries"] =
                [
                    "a settled key is an entry of its set's table",
                    "a failing side's key whose every key the set settled is an entry of its table, failing beside them",
                    "a pair key the rows read is an entry of its set's pair table",
                ],
                ["view"] =
                [
                    "a set's settled table is viewed as its leaves read it",
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
                    "a leaf a partition needs reads nothing beside its table until a row says so",
                ],
                ["parted"] =
                [
                    "a leaf rests on the rows that license each test it leans on",
                    "a string literal parts the orders over a path whose strings may hold the other band",
                    "two string orderings of one path in a written conjunction, one of whose literals parts the orders there, need Range",
                    "a band the set's string comparator moves parts every string literal over a path whose strings may hold it",
                    "a string literal holding a band the set's string comparator moves is parted over the path it is compared with",
                    "a string literal holding a band its set respells lies anywhere under a comparator of its string order whose spelling keeps the band",
                    "a string comparison's points are unkept over a class a target the set serves stores as another, where its path's strings may hold it",
                    "a pair departs where its places may hold two bands the set's string comparator ranks apart",
                    "a pair whose places may hold a band the set's string comparator moves lies anywhere where a side holds it",
                    "a pair's points are unkept over a class a target the set serves stores as another, where its places may hold it",
                    "an affix is unread over a band its test reads a string only up to on a target the set serves, its pattern of the row's shape",
                    "an affix whose pattern holds a band its test reads a string only up to reads no string",
                    "an affix whose pattern holds what a target the set serves stores a band as is unread over that band",
                    "an affix whose pattern its matcher's spelling splits is uncut over a band that spelling rewrites",
                    "an affix whose pattern holds a band its set respells reads no string under a matcher of its test whose spelling keeps the band",
                    "an affix whose pattern holds a band its matcher's spelling rewrites reads no string under a set that does not respell it",
                ],
                ["evaluate"] =
                [
                    "a test is evaluated on every cell by the rows",
                    "a compound is evaluated from its parts by its connective's rows",
                ],
                ["classes"] =
                [
                    "Calcite's atom and the form are paired on every cell",
                    "a candidate a cell of which holds no boolean rests on the rows that read a whole condition over one",
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
                    "a place is writable under a set whose spelling rewrites no band its names hold",
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
                    "a box of a type null where what it holds is is strict in what it holds is strict in",
                ],
                ["best"] =
                [
                    "a junction's best is the join of its arms' bests",
                    "a negation's best is its operand's best at the opposite position, negated",
                    "a case whose else absorbs at its position is its condition joined with its then-arm",
                    "a case whose else is the opposite constant, over a two-valued condition, is its condition's form joined with its then-arm",
                    "a junction holding its connective's unit agrees as Kleene's logic folds it",
                    "a junction holding its own connective or a part twice agrees as its connective's laws regroup it",
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
                    "a weakening that negates a constant is Kleene's negation of it",
                    "a weakening holding its connective's unit is what Kleene's logic folds it to",
                    "a weakening holding its own connective or a part twice is what its connective's laws regroup it to",
                    "a case's then-arm weakened beneath its condition holding its connective's unit is what Kleene's logic folds it to",
                    "a case's then-arm weakened beneath its condition holding its own connective or a part twice is what its connective's laws regroup it to",
                    "a node's weakening is the cheapest form that weakens it",
                    "a case's then-arm weakened beneath its condition is the cheapest form that weakens it there",
                    "a case's then-arm weakens beneath its condition to their weakenings joined, where its position reads it so",
                    "a case weakens to its then-arm's weakening beneath its condition joined with its else-arm's, where its position reads it so",
                    "a case weakens to its arms' weakenings joined, where its position joins them",
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
                    "a place's import beneath a stratum is its call's image, a value of its type, once every operand is in and every key agrees and is one Calcite answers a value over",
                    "a place's import beneath a stratum is its type's kinds and absent, once every operand is in and every key agrees and Calcite answers null over one",
                    "a place's import beneath a stratum is its type's kinds and absent, once every operand is in and every key is one the service defines",
                    "a place's import is its import beneath the root",
                    "a computed place's import joins its image beneath each stratum an atom over it is read at",
                    "a place's import beneath a stratum is its type's kinds and absent where the service defines every key any operands may fall under",
                ],
                ["assume"] =
                [
                    "an atom over a computed place is assumed as its place's image beneath the atom's stratum",
                ],
                ["pair"] =
                [
                    "a call of an operator its rows name at a type is keyed by that name at its type",
                    "a call of an operator its rows name alone is keyed by its name",
                    "a call takes a spelling every call of its function takes",
                    "a call takes a spelling its literal at an ordinal lets it, a whole number of the class the spelling asks",
                    "an operand read at an ordinal its function's rows name no key of lets its call be absent",
                    "a literal operand at an ordinal its function's rows name no key of, nor any literal of, lets its call be absent",
                    "a comparison of two readings of one kind is an atom of their pair, with what each reading's order holds",
                ],
                ["fold"] =
                [
                    "a LIKE over a fold S images is the case-insensitive affix its set's rows match alike, over the fold's subject",
                ],
            });
            ProbeTheory.Graph.Rulesets.Sum(r => r.Rules.Count).Should().Be(188);
        }

        /// <summary>
        /// The classes Calcite raises on alone follow the policy: what it sets aside, and a class of a cell set aside, which is
        /// read as a raise; and nothing where it sets none.
        /// </summary>
        [Fact]
        public void TheRaiseOnlyClassesAreTheOnesThePolicySetsAside()
        {
            ProbeTheory.Theory.Get(ProbeTheory.RaiseOnly.Of()).Should().BeEquivalentTo(AgreementRows.Classes.Where(k => k.Calcite == Truths.Raises || k.Outside));
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
        /// Each row the foundation reads in (F3, and G1's of what a spelling rewrites and what is never sent) is a fact of S as
        /// the measured definition carries it, and the definition carries some of each:
        /// what the groups that read them start from. A function's value is the union of its rows.
        /// </summary>
        [Fact]
        public void TheFoundationsRowsAreFactsOfS()
        {
            var (d, s) = (Measured, ProbeTheory);
            new[] { d.Parts.Count, d.Moves.Count, d.StoredAs.Count, d.Truncations.Count, d.Rewrites.Count, d.Unsent.Count, d.PairReadings.Count, d.Boxes.Count,
                d.SeesThrough.Count, d.NullThrough.Count, d.StructureOnly.Count, d.CalciteReadings.Count, d.CalciteBands.Count, d.Casts.Count, d.ReaderOrders.Count, d.TestKinds.Count, d.Comparands.Count,
                d.TestReads.Count }
                .Should().OnlyContain(n => n > 0);

            d.Parts.Should().OnlyContain(r => s.Theory.Check(s.Parts.Of(r.Comparator, r.Band, r.Other)));
            d.Moves.Should().OnlyContain(r => s.Theory.Check(s.Moves.Of(r.Comparator, r.Band)));
            d.StoredAs.Should().OnlyContain(r => s.Theory.Check(s.StoredAs.Of(r.Target, r.Written, r.Stored)));
            d.Truncations.Should().OnlyContain(r => s.Theory.Check(s.Truncates.Of(r.Target, r.Test, r.Shape, r.Band)));
            d.Folds.Should().NotBeEmpty().And.OnlyContain(r => s.Theory.Check(s.FoldsAs.Of(r.Target, r.Test, r.Function, r.Band)));
            d.Rewrites.Should().OnlyContain(r => s.Theory.Check(s.Rewrites.Of(r.Comparator, r.Band)));
            d.Unsent.Should().OnlyContain(r => s.Theory.Check(s.Unsent.Of(r.Target, r.Class)));
            d.PairReadings.Should().OnlyContain(r => s.Theory.Check(s.PairReads.Of(r.Test, r.Swapped, r.Reads)));
            d.Boxes.Should().OnlyContain(r => s.Theory.Check(s.BoxOf.Of(r.Kind, r.Box)));
            d.SeesThrough.Should().OnlyContain(r => s.Theory.Check(s.SeesThrough.Of(r)));
            d.NullThrough.Should().OnlyContain(r => s.Theory.Check(s.BoxNull.Of(r)));
            d.StructureOnly.Should().OnlyContain(r => s.Theory.Check(s.StructureOnly.Of(r)));
            d.CalciteReadings.Should().OnlyContain(r => s.Theory.Check(s.CalciteReads.Of(r.Reader, r.Kind, r.Answer)));
            d.Casts.Should().OnlyContain(r => s.Theory.Check(s.MeasuredCast.Of(r.Target, r.Reader, r.Row)));
            d.Comparands.Should().OnlyContain(r => s.Theory.Check(s.TakesConstant.Of(r.Reading, r.Moved, r.Row)));
            d.TestReads.Should().OnlyContain(r => s.Theory.Check(s.TestReads.Of(r.Reader, r.As)));
            foreach (var g in d.CalciteBands.GroupBy(r => (r.Reader, r.Kind)))
                s.Theory.Get(s.CalciteBands.Of(g.Key.Reader, g.Key.Kind)).Should().BeEquivalentTo(g.Select(r => r.Band));
            foreach (var g in d.ReaderOrders.GroupBy(r => r.Reader))
                s.Theory.Get(s.ReaderOrder.Of(g.Key)).Should().BeEquivalentTo(g.Select(r => r.Claim));
            foreach (var g in d.TestKinds.Where(r => ServiceForm.SpellingOf(r.Test) is not null).GroupBy(r => ServiceForm.SpellingOf(r.Test)!))
                s.Theory.Get(s.TestKinds.Of(g.Key)).Should().BeEquivalentTo(g.SelectMany(r => CosmosValueClasses.JsonKinds.Where(k => k.Class == r.Kind).Select(k => k.Kind)).Distinct());
        }

        /// <summary>
        /// A box is read by its rows (G8): within one, the comparisons <c>Calcite.VariantEqualsOnlyItsOwnBox</c> says see
        /// through it, and its null test, are atoms of the reading it holds, and a raw value compared with a literal boxed as
        /// values of the literal's own kind are is the raw value's comparison, which its cells decide; an order, a literal in
        /// another box and an <c>INTEGER</c>, boxed apart from a stored integer's <c>BIGINT</c>, are no atom. A one-place test
        /// is an atom of what its body reads, by <c>Calcite.TypeTestsAreTheirBodies</c>' rows the raw value of a text, of a
        /// raw value in a column and of a returning alike, and of no cast. A box's null test is what it holds where its type's
        /// row says so (<c>boxnull</c>), each type its own cell; and a comparison with SQL's null by an operator null over a null
        /// operand (<c>strictop</c>) is an atom UNKNOWN wherever it is read.
        /// </summary>
        [Fact]
        public void ABoxAndATestAreReadByTheirRows()
        {
            static IReadOnlyList<PredicateAtom> Atoms(SqlPredicate conjunct) => [.. RenderingTemplatesTests.AtomsOf([conjunct]).Select(a => a.Atom)];
            var t = JsonDocumentPath.Root.Property("t");
            var (text, bikes, yes) = (new SqlValue.Box("VARCHAR", new SqlValue.Text(t)), new SqlValue.Box("CHAR", OracleFixture.Lit("bikes")), new SqlValue.Box("BOOLEAN", OracleFixture.Lit(true)));

            Atoms(new SqlPredicate.Compare("=", text, bikes)).Should().Equal(PredicateAtom.Compare(PredicateAtom.TextReader, "=", JsonValues.Of("bikes")));
            Atoms(new SqlPredicate.Compare("<>", text, bikes)).Should().Equal(PredicateAtom.Compare(PredicateAtom.TextReader, "<>", JsonValues.Of("bikes")));
            Atoms(new SqlPredicate.IsNull(text)).Should().Equal(PredicateAtom.IsNull(PredicateAtom.TextReader));
            Atoms(new SqlPredicate.Compare("<", text, bikes)).Should().BeEmpty("no row says a VARIANT's order sees through its box");
            Atoms(new SqlPredicate.Compare("=", text, yes)).Should().BeEmpty("a boolean's box is not a text's");

            var variant = new SqlValue.Raw(t) { Column = "VARIANT" };
            Atoms(new SqlPredicate.Compare("=", variant, bikes)).Should().Equal(PredicateAtom.Compare(PredicateAtom.RawIn("VARIANT"), "=", JsonValues.Of("bikes")));
            Atoms(new SqlPredicate.Compare("<>", variant, yes)).Should().Equal(PredicateAtom.Compare(PredicateAtom.RawIn("VARIANT"), "<>", JsonValues.Of(true)));
            Atoms(new SqlPredicate.Compare("=", variant, new SqlValue.Box("INTEGER", OracleFixture.Lit(42m)))).Should().BeEmpty("an INTEGER is boxed apart from a stored integer");
            Atoms(new SqlPredicate.Compare("=", new SqlValue.Raw(t), new SqlValue.Box("BIGINT", OracleFixture.Lit(42m)))).Should().Equal(PredicateAtom.Compare(PredicateAtom.RawReader, "=", JsonValues.Of(42m)));

            var kinds = ProbeTheory.Theory.Get(ProbeTheory.TestKinds.Of("IS_STRING"));
            foreach (var read in new SqlValue[] { new SqlValue.Text(t), new SqlValue.Raw(t), variant, new SqlValue.Typed(t, "VARCHAR") })
            {
                Atoms(new SqlPredicate.TypeTest("IS_STRING", read)).Should().Equal([PredicateAtom.TypeTest(PredicateAtom.RawReader, "IS_STRING", kinds)], read.ToString());
                Atoms(new SqlPredicate.IsDefined(read)).Should().Equal([PredicateAtom.IsDefined(PredicateAtom.RawReader)], read.ToString());
            }

            Atoms(new SqlPredicate.IsDefined(new SqlValue.Cast("VARCHAR", new SqlValue.Text(t)))).Should().BeEmpty("a cast is no accessor the body takes apart");

            Atoms(new SqlPredicate.IsNull(new SqlValue.Box("INTEGER", new SqlValue.Typed(t, "INTEGER")))).Should().Equal(PredicateAtom.IsNull(PredicateAtom.TypedReader("INTEGER")));
            Atoms(new SqlPredicate.IsNull(new SqlValue.Box("CHAR", new SqlValue.Text(t)))).Should().BeEmpty("no row says a CHAR's box is null where what it holds is");

            var unknown = PredicateAtom.CompareNull(PredicateAtom.TextReader, "=");
            Atoms(new SqlPredicate.Compare("=", new SqlValue.Text(t), new SqlValue.Null("VARCHAR"))).Should().Equal(unknown);
            unknown.On(new PredicateAtom.State.Scalar(JsonValues.Of("bikes")), ReaderRows).Should().Be(Truths.Unknown);
            unknown.Kept(true, ReaderRows).Should().Be(PathValues.OfKinds(ValueSet<string>.Empty), "it is never TRUE");
        }

        /// <summary>
        /// What S's sets rules derive of each set from its string order and the rows of characters (G1): the emulator's order
        /// compares the spelling it stores, so a set serving it is sent that spelling, U+0000 and U+FDD0 respelled, and Azure's
        /// is sent as written; a set serving either may send no unpaired surrogate, and both orders rank the surrogates and the
        /// high band apart. Calcite is sent nothing, so nothing is unsendable under it.
        /// </summary>
        [Fact]
        public void EachSetIsSentTheSpellingItsStringOrderCompares()
        {
            var s = ProbeTheory;
            s.Theory.Get(s.Rewriting.Of()).AsEnumerable().Should().Equal([CosmosAnswer.StoredCodePoint]);
            (s.SpellingOf(RenderingSet.Production), s.SpellingOf(RenderingSet.Emulator), s.SpellingOf(RenderingSet.Portable))
                .Should().Be((SentSpelling.Written, SentSpelling.Stored, SentSpelling.Stored));
            s.Theory.Get(s.Respelled.Of(RenderingSet.Production)).IsEmpty.Should().BeTrue();
            s.Theory.Get(s.Respelled.Of(RenderingSet.Emulator)).AsEnumerable().Should().BeEquivalentTo([JsonValues.NulClass, JsonValues.EscapeClass]);
            s.Theory.Get(s.Respelled.Of(RenderingSet.Portable)).AsEnumerable().Should().BeEquivalentTo([JsonValues.NulClass, JsonValues.EscapeClass]);
            foreach (var set in new[] { RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable })
            {
                s.Theory.Get(s.Unsendable.Of(set)).AsEnumerable().Should().Equal([JsonValues.UnpairedClass], set.ToString());
                s.Theory.Get(s.PartingBands.Of(set)).AsEnumerable().Should().BeEquivalentTo([JsonValues.SurrogateBand, JsonValues.HighBand], set.ToString());
            }

            s.Theory.Get(s.Unsendable.Of(RenderingSet.Calcite)).IsEmpty.Should().BeTrue();
            Apache.Calcite.Cosmos.Adapter.Metadata.CosmosRendering.StoresSpelled(RenderingSet.Emulator).Should().BeTrue();
            Apache.Calcite.Cosmos.Adapter.Metadata.CosmosRendering.StoresSpelled(RenderingSet.Production).Should().BeFalse();
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
                               from column in PredicateAtom.Columns select PredicateAtom.InColumn(reader, column)]);

            // A numeric cast of the raw value is held back until a Calcite row measures it, through a column or not.
            var covered = built.Where(reader => reader.StartsWith("cast:", StringComparison.Ordinal) == false || reader.EndsWith(":" + PredicateAtom.TextReader, StringComparison.Ordinal)
                || PredicateAtom.Columnless(reader) == PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)).ToList();
            ReaderEdgesTable.Rows.Keys.Should().BeEquivalentTo(covered).And.BeSubsetOf(built);
            covered.Should().OnlyContain(reader => ProbeTheory.Theory.Check(Fact.Exists(ProbeTheory.Edges.Of(reader))));
        }

        /// <summary>
        /// The casts the edges table names are the casts a <c>Calcite</c> row measured (<c>measuredcast</c>), each once: the
        /// table writes their keys by hand until h15 derives them from those rows (<c>TODO.md</c>, the loader's cast
        /// targets), so a cast measured or dropped without the table following fails here rather than drifting.
        /// </summary>
        [Fact]
        public void TheEdgesTablesCastsAreTheMeasuredCasts() =>
            ReaderEdgesTable.Rows.Keys.Where(reader => reader.StartsWith("cast:", StringComparison.Ordinal) && PredicateAtom.ColumnOf(reader) is null)
                .Should().BeEquivalentTo(CosmosAxiomRows.ReadCasts(CosmosAxioms.All).Select(c => PredicateAtom.CastReader(c.Target, c.Reader)).Distinct());

    }

}
