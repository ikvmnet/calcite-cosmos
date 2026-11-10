using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A <c>CASE</c> in S (spec h08): the constant else that absorbs, which needs no two-valuedness; the opposite constant,
    /// which does, and the soundness critic's counterexample it refuses; the service ternary, as the set licenses it; each
    /// arm's strata, the narrow else read only beneath a two-valued condition; an arm's atom its own key; and the rows a
    /// tagged leaf carries.
    /// </summary>
    public sealed class RenderingCaseTests
    {

        static readonly JsonDocumentPath XPath = P("x"), NPath = P("n"), NamePath = P("name");

        static readonly SqlPredicate True = new SqlPredicate.Constant(true), False = new SqlPredicate.Constant(false), Unknown = new SqlPredicate.Constant(null);

        /// <summary>A comparison UNKNOWN where the label is null or absent: never two-valued over an open import.</summary>
        static readonly SqlPredicate Nullable = Eq(Text(LabelPath), "bikes");

        /// <summary>A type test, TRUE or FALSE on every document (<c>Calcite.TypeTestsAreTheirBodies</c>).</summary>
        static readonly SqlPredicate TwoValued = new SqlPredicate.TypeTest("IS_NUMBER", Raw(XPath));

        static readonly SqlPredicate Kind = Eq(Text(KindPath), "a");

        static SqlPredicate Case(SqlPredicate condition, SqlPredicate then, SqlPredicate otherwise) => new SqlPredicate.Case(condition, then, otherwise);

        /// <summary>What generate mode says of one root, read before its bundle goes: the answer, the run, the strata and atoms, and each tagged leaf's rows.</summary>
        sealed record Generated(RenderingAnswer Answer, RenderingRun Run, IReadOnlyDictionary<long, IReadOnlyList<SqlPredicate>> Strata, IReadOnlyList<SqlPredicate> Atoms,
            IReadOnlyList<(SqlPredicate Atom, bool Leaf, bool Over, bool Assumed)> Tagged)
        {
            public ServiceForm? Best => Answer.Best;
        }

        /// <summary>
        /// Generates a root under a set, every path imported as anything unless given, or through <paramref name="import"/>,
        /// which may state strata too.
        /// </summary>
        static Generated Generate(SqlPredicate root, RenderingSet set, IReadOnlyDictionary<JsonDocumentPath, PathValues>? values = null, Action<RenderingBundle>? beneath = null,
            Action<RenderingBundle>? import = null)
        {
            var theory = ProbeTheory;
            using var bundle = theory.Open(set);
            var term = bundle.Write(root);
            if (import is not null)
                import(bundle);
            else
                foreach (var path in root.Paths)
                    bundle.Import(Source, path, values?.GetValueOrDefault(path) ?? PathValues.Anything, AllBands);

            beneath?.Invoke(bundle);
            var tagged = bundle.Atoms.OfType<SqlPredicate.Beneath>().Select(a => (Atom: (SqlPredicate)a, Term: bundle.Write(a))).ToList();
            bundle.Generate(term);
            var run = bundle.RunGenerate(RenderingBundle.WidthOf([root]));
            var rows = bundle.Failure is null ? bundle.Scope.Rows(theory.Over).ToList() : [];
            return new(bundle.ReadBest(term), run, bundle.Strata.ToDictionary(s => s.Key, s => s.Value), [.. bundle.Atoms],
                [.. tagged.Select(t => (t.Atom, bundle.Scope.Check(theory.Leaf.Of(t.Term)), rows.Any(r => t.Term.Id.Equals(r.Keys[0])), bundle.Scope.TryGet(theory.Assumed.Of(t.Term), out _)))]);
        }

        static readonly RenderingRun Disciplined = new(RunStopReason.Completed, true, false);

        static bool HasTernary(ServiceForm form) => form.Subforms.Any(f => f.Kind == ServiceFormKind.Choose);

        /// <summary>
        /// An absorbing else: <c>CASE WHEN c THEN a ELSE FALSE</c> at <c>Pos</c>, and with no <c>ELSE</c> at <c>Pos</c> and
        /// beneath a <c>NOT</c>, over a nullable condition, are Proven under <c>Production</c>, which licenses no ternary:
        /// <c>c AND a</c> and <c>NOT c OR a</c> leave the rows the else does wherever <c>c</c> is not TRUE.
        /// </summary>
        [Fact]
        public void AnAbsorbingElseIsItsConditionJoinedWithItsThenArm()
        {
            foreach (var root in new[] { Case(Nullable, Kind, False), Case(Nullable, Kind, Unknown), Not(Case(Nullable, Kind, Unknown)) })
            {
                var generated = Generate(root, RenderingSet.Production);
                generated.Run.Should().Be(Disciplined, root.ToString());
                generated.Answer.Kind.Should().Be(RenderingVerdictKind.Proven, root.ToString());
                HasTernary(generated.Best!).Should().BeFalse(root.ToString());
            }

            // UNKNOWN is no constant the rows measured: alone it has no exact form, which the absorbing rule never needs.
            Generate(Unknown, RenderingSet.Emulator).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            Generate(False, RenderingSet.Emulator).Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// The else and junction rules join as the arms stand, and S's regroup rule flattens a junction of the connective's
        /// own and keeps one of each part, under a set licensing the connective (G4); the price keeps the flattened, of as
        /// many nodes in fewer groups, whatever its text sorts as (<see cref="Priced"/>): <c>CASE WHEN c THEN a AND b ELSE
        /// FALSE</c> is <c>c AND a AND b</c>, with parts that sort before a parenthesis or after it, and so is the junction
        /// of the three, its arms paired as they stand; and <c>CASE WHEN a THEN a ELSE FALSE</c> is <c>a</c>'s own best.
        /// </summary>
        [Fact]
        public void AnAbsorbingElseRegroupsWhatItJoins()
        {
            var typed = And(TwoValued, new SqlPredicate.TypeTest("IS_STRING", Raw(NPath)));
            foreach (var root in new[] { Case(Nullable, And(Kind, Eq(Text(NPath), "x")), False), Case(Nullable, typed, False), Case(And(Kind, Nullable), typed, False), And(Nullable, typed) })
            {
                var flat = Generate(root, RenderingSet.Production);
                flat.Answer.Kind.Should().Be(RenderingVerdictKind.Proven, root.ToString());
                flat.Best!.Kind.Should().Be(ServiceFormKind.And, root.ToString());
                flat.Best.Members.Should().HaveCountGreaterThanOrEqualTo(3).And.OnlyContain(m => m.Kind != ServiceFormKind.And, flat.Best.Text);
            }

            var once = Generate(Case(Kind, Kind, False), RenderingSet.Production);
            once.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            once.Best.Should().Be(Generate(Kind, RenderingSet.Production).Best);
        }

        /// <summary>
        /// The opposite constant needs a two-valued condition. <c>NOT ((label = 'bikes') IS TRUE)</c> keeps the rows the
        /// label is null on, which <c>label = 'bikes'</c> written bare would not (the soundness critic's counterexample):
        /// under <c>Production</c>, the probe account having measured the ternary (S1), its form keeps the ternary.
        /// <c>NOT (IS_NUMBER(x) IS TRUE)</c> is Proven there with none.
        /// </summary>
        [Fact]
        public void TheOppositeConstantNeedsATwoValuedCondition()
        {
            var counterexample = Generate(Not(Case(Nullable, True, False)), RenderingSet.Production);
            counterexample.Run.Should().Be(Disciplined);
            counterexample.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            HasTernary(counterexample.Best!).Should().BeTrue("the condition is not two-valued, so its opposite constant needs the ternary");

            var typed = Generate(Not(Case(TwoValued, True, False)), RenderingSet.Production);
            typed.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            typed.Best.Should().Be(ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNumber, SqlValue.Spell(XPath))));
        }

        /// <summary>
        /// <c>x IS NOT TRUE</c> over a nullable comparison at <c>Pos</c> is the opposite constant over a condition that is
        /// not two-valued: Proven as the ternary under every set, each target having measured it (S1, Azure at run 1).
        /// </summary>
        [Fact]
        public void IsNotTrueOverANullableComparisonNeedsTheTernary()
        {
            var root = Case(Nullable, False, True);
            foreach (var set in new[] { RenderingSet.Emulator, RenderingSet.Production })
            {
                var generated = Generate(root, set);
                generated.Answer.Kind.Should().Be(RenderingVerdictKind.Proven, set.ToString());
                generated.Best!.Kind.Should().Be(ServiceFormKind.Choose, set.ToString());
            }
        }

        /// <summary>A general <c>CASE</c> is Proven as the ternary under a set that licenses it, every set since run 1 measured it on Azure.</summary>
        [Fact]
        public void AGeneralCaseIsTheTernaryWhereTheSetLicensesIt()
        {
            var root = Case(Nullable, Kind, Eq(Text(NPath), "x"));
            var emulator = Generate(root, RenderingSet.Emulator);
            emulator.Run.Should().Be(Disciplined);
            emulator.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            HasTernary(emulator.Best!).Should().BeTrue();

            var production = Generate(root, RenderingSet.Production);
            production.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            HasTernary(production.Best!).Should().BeTrue();
        }

        /// <summary>
        /// A three-arm <c>CASE</c>: the second arm's atom is read beneath two strata, the narrow copy's
        /// <c>[NOT c1, c2]</c> and the wide copy's <c>[c2]</c>, the wide copy's inner case rooting its own else narrow
        /// beneath <c>[NOT c2]</c>. A narrowing stated beneath the narrow stratum alone, which
        /// makes the arm's bare form exact, is read where the first condition is two-valued, and nowhere else: over a
        /// nullable first condition the case is read through the wide copy, Proven still, its else restricted.
        /// </summary>
        [Fact]
        public void TheNarrowElseIsReadOnlyBeneathATwoValuedCondition()
        {
            var second = Ne(Text(NamePath), "bikes");
            var bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, SqlValue.Spell(NamePath), Of("bikes"));
            var defined = ServiceForm.TestOf(CosmosServiceTest.IsDefined, SqlValue.Spell(NamePath));
            Generated Run(SqlPredicate first) => Generate(Case(first, Eq(Text(NPath), "x"), Case(Kind, second, False)), RenderingSet.Emulator, beneath: bundle =>
            {
                var narrow = bundle.Strata.Single(s => s.Value.SequenceEqual([new SqlPredicate.Not(first), Kind])).Key;
                bundle.Assume(Source with { Stratum = narrow }, new SqlPredicate.Beneath(narrow, second), PathValues.OfKinds(ValueSet.Of(StringType)));
            });

            var typed = Run(TwoValued);
            typed.Run.Should().Be(Disciplined);
            typed.Strata.Values.Select(s => string.Join(", ", s.Select(c => c.ToString()))).Should().BeEquivalentTo(
                new[] { [TwoValued], [new SqlPredicate.Not(TwoValued)], [new SqlPredicate.Not(TwoValued), Kind], [Kind], [new SqlPredicate.Not(Kind)],
                    new SqlPredicate[] { new SqlPredicate.Not(TwoValued), new SqlPredicate.Not(Kind) } }
                    .Select(s => string.Join(", ", s.Select(c => c.ToString()))));
            typed.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            typed.Best!.Kind.Should().Be(ServiceFormKind.Choose);
            typed.Best.Members[2].Subforms.Should().Contain(bare).And.NotContain(defined, "the narrow copy reads the narrowing beneath a two-valued condition");

            var nullable = Run(Nullable);
            nullable.Run.Should().Be(Disciplined);
            nullable.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
            nullable.Best!.Kind.Should().Be(ServiceFormKind.Choose);
            nullable.Best.Members[2].Subforms.Should().Contain(defined, "where the first condition may be UNKNOWN the narrowing beneath its negation is not the else-arm's");
        }

        /// <summary>
        /// An atom spelled alike in a sibling and in an arm is two keys, each its own term, and the run faults nothing; and
        /// every tagged leaf has its <c>leaf</c>, <c>over</c> and <c>assumed</c> rows (eggnog map risk 6).
        /// </summary>
        [Fact]
        public void AnArmsAtomIsItsOwnKeyAndCarriesALeafsRows()
        {
            var generated = Generate(And(Nullable, Case(Kind, Nullable, False)), RenderingSet.Production);
            generated.Run.Should().Be(Disciplined);
            generated.Atoms.Should().Contain(Nullable).And.Contain(a => a is SqlPredicate.Beneath && ((SqlPredicate.Beneath)a).Leaf.Equals(Nullable));
            generated.Tagged.Should().NotBeEmpty().And.OnlyContain(t => t.Leaf && t.Over && t.Assumed);
            generated.Answer.Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// An arm read beside the facts its condition gives: where kind is "a" the label is a string, so beneath
        /// <c>RenderArm</c>'s stratum the then-arm's bare inequality is exact, cheaper than the restricted form read at the
        /// root. The licence opens only over conditions the facts graph reads exactly (<c>StratumFor</c>).
        /// </summary>
        [Fact]
        public void AnArmIsReadBesideTheFactsItsConditionGives()
        {
            var theory = Theory(When(KindPath, "a", LabelPath, Type(JsonType.String)));
            var root = Case(Kind, Ne(Text(LabelPath), "bikes"), False);
            var scope = Admitted(theory, root);
            var node = RenderingCheckRun.RootOf([scope])!.Value;
            var licence = scope.StratumFor(0, [Kind], null);
            licence.Should().NotBeNull();
            scope.StratumFor(0, [Kind], _ => false).Should().BeNull("a condition the facts graph does not read exactly opens no stratum");
            var arm = scope.ContextFor(licence!.Value);

            Generated Run(bool stated) => Generate(root, RenderingSet.Production, import: bundle =>
            {
                var beneath = stated ? bundle.Atoms.OfType<SqlPredicate.Beneath>().Where(a => bundle.Strata[a.Stratum].SequenceEqual([Kind]))
                    .Select(a => ((SqlPredicate)a, Source with { Stratum = a.Stratum }, arm.Context, arm.Rules)) : [];
                RenderingImporter.Import(bundle, Source, node.Context, node.Rules, theory, root.Paths, beneath);
            });

            var (open, narrowed) = (Run(false), Run(true));
            (open.Answer.Kind, narrowed.Answer.Kind).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Proven));
            Priced.Of(narrowed.Best!).Should().BeLessThan(Priced.Of(open.Best!));
            narrowed.Best!.Subforms.Should().Contain(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, SqlValue.Spell(LabelPath), Of("bikes")));
            GC.KeepAlive(scope);
        }

    }

}
