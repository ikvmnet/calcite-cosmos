using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using Op = Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Check mode through <c>AND</c>, <c>OR</c> and <c>NOT</c>, on synthetic bundles: a junction agrees where its sides do
    /// and its form is exactly their join, through any pairing of the written members; it is refuted where one side is and
    /// the other is the connective's identity on a location apart; and each step holds only where the set and Calcite
    /// license its connective.
    /// </summary>
    public sealed class RenderingComposeTests
    {

        static readonly JsonDocumentPath LabelPath = P("label"), KindPath = P("kind"), CategoryPath = P("category"), MetadataPath = P("metadata"), FlagPath = P("flag");

        static readonly string Label = SqlValue.Spell(LabelPath), Kind = SqlValue.Spell(KindPath), Category = SqlValue.Spell(CategoryPath), Metadata = SqlValue.Spell(MetadataPath);

        /// <summary>The bare comparison, refuted for Equal where the service keeps a null, an object or an array (T5).</summary>
        static readonly ServiceForm Bare = Compare(Op.NotEqual, Label, "bikes");

        /// <summary>The guarded comparison the translator writes for text <c>label &lt;&gt; 'bikes'</c>, exact (T1).</summary>
        static readonly ServiceForm Guarded = All(Is(Op.IsPrimitive, Label), Negated(Is(Op.IsNull, Label)), Bare);

        static readonly ServiceForm KindIsA = Compare(Op.Equal, Kind, "a");

        static readonly SqlPredicate LabelAndKind = And(Ne(Text(LabelPath), "bikes"), Eq(Text(KindPath), "a"));

        static readonly RenderingDefinition Measured = RenderingDefinition.Measured(ProbeRows.Rows);

        static readonly Lazy<RenderingTheory> Probe = new(() => RenderingTheory.Define(Measured));

        static ServiceForm Is(Op test, string path) => ServiceForm.TestOf(test, path);

        static ServiceForm Compare(Op op, string path, string literal) => ServiceForm.CompareOf(op, path, Of(literal));

        static ServiceForm All(params ServiceForm[] members) => ServiceForm.JunctionOf(Op.And, members);

        static ServiceForm Any(params ServiceForm[] members) => ServiceForm.JunctionOf(Op.Or, members);

        static ServiceForm Negated(ServiceForm operand) => ServiceForm.NotOf(operand);

        /// <summary>What 4b65754 wrote beneath a NOT for an equality: kept where Calcite's would be unknown too.</summary>
        static ServiceForm EqualOrUnknown(string path, string literal) => Any(Compare(Op.Equal, path, literal), Is(Op.IsNull, path), Negated(Is(Op.IsDefined, path)));

        /// <summary>
        /// Checks a form claimed for a conjunct in a bundle of its own, every path imported as anything unless given, and
        /// reads both claims; the run must complete with one node per class and no contradiction.
        /// </summary>
        static (RenderingVerdict Equal, RenderingVerdict Implies) Check(SqlPredicate conjunct, ServiceForm form, RenderingTheory? theory = null,
            RenderingSet set = RenderingSet.Emulator, JsonDocumentPath? path = null, PathValues? values = null, ValueSet<string>? classes = null)
        {
            using var bundle = (theory ?? Probe.Value).Open(set);
            var root = bundle.Write(conjunct);
            foreach (var each in new[] { LabelPath, KindPath, CategoryPath, MetadataPath, FlagPath })
                bundle.Import("test", each, each == path ? values! : PathValues.Anything, each == path ? classes ?? AllBands : AllBands);

            bundle.Claim(root, form);
            var run = bundle.Run();
            (run.StopReason, run.OneNodePerClass, run.Contradicted).Should().Be((RunStopReason.Completed, true, false), form.Text);
            return (bundle.Read(root, form, Verdict.Equal), bundle.Read(root, form, Verdict.Implies));
        }

        /// <summary>The probe's T3: a conjunction of two exact forms is exact, whichever order the written members take.</summary>
        [Fact]
        public void T3()
        {
            foreach (var form in new[] { All(Guarded, KindIsA), All(KindIsA, Guarded) })
            {
                var (equal, implies) = Check(LabelAndKind, form);
                (equal.Kind, implies.Kind).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Proven), form.Text);
                equal.Proof!.Cells.Should().Be(22, "label's eleven cells and kind's");
            }
        }

        /// <summary>
        /// The readable wrong answers of the first family (harness map §5), as 4b65754 wrote them, over imported anything:
        /// each root is refuted at a structure, which the text reading reads as null. P5 and P6 are refuted at the junction
        /// beneath their NOT, the label side at <c>Neg</c> and the category at the connective's identity.
        /// </summary>
        [Fact]
        public void P1toP6Synthetic()
        {
            var isNotNull = All(Is(Op.IsDefined, Label), Negated(Is(Op.IsNull, Label)));
            var cases = new (string Name, SqlPredicate Conjunct, ServiceForm Form, string Path, bool Implies)[]
            {
                ("P1", IsNotNull(Text(MetadataPath)), All(Is(Op.IsDefined, Metadata), Negated(Is(Op.IsNull, Metadata))), Metadata, false),
                ("P19", IsNotNull(Text(MetadataPath)), All(Is(Op.IsDefined, Metadata), Negated(Is(Op.IsNull, Metadata))), Metadata, false),
                ("P2", IsNotNull(Text(LabelPath)), isNotNull, Label, false),
                ("P25", IsNotNull(Text(LabelPath)), isNotNull, Label, false),
                ("P3", IsNull(Text(LabelPath)), Any(Negated(Is(Op.IsDefined, Label)), Is(Op.IsNull, Label)), Label, true),
                ("P4", Not(Eq(Text(LabelPath), "bikes")), Negated(EqualOrUnknown(Label, "bikes")), Label, false),
                ("P5", Not(And(Eq(Text(LabelPath), "bikes"), Eq(Raw(CategoryPath), "a"))), Negated(All(EqualOrUnknown(Label, "bikes"), EqualOrUnknown(Category, "a"))), Label, false),
                ("P6", Not(Or(Eq(Text(LabelPath), "bikes"), Eq(Raw(CategoryPath), "b"))), Negated(Any(EqualOrUnknown(Label, "bikes"), EqualOrUnknown(Category, "b"))), Label, false),
            };

            foreach (var (name, conjunct, form, path, implies) in cases)
            {
                var (equal, implied) = Check(conjunct, form);
                equal.Kind.Should().Be(RenderingVerdictKind.Refuted, name);
                if (implies)
                    implied.Kind.Should().Be(RenderingVerdictKind.Refuted, name + " keeps less than Calcite");

                var refutation = equal.Refutation!;
                refutation.Path.Should().Be(path, name);
                refutation.Cells.Should().Contain(cell => cell == path + "=" + ArrayType || cell == path + "=" + ObjectType, name);
                if (name is "P5" or "P6")
                {
                    refutation.MultiPath.Should().BeTrue(name);
                    refutation.Cells.Should().HaveCount(2).And.Contain(cell => cell.StartsWith(Category + "="), name);
                }
                else
                {
                    refutation.Cells.Should().ContainSingle(name);
                }
            }

            // P19's second conjunct, raw category = 'bikes', was written exactly.
            Check(Eq(Raw(CategoryPath), "bikes"), Compare(Op.Equal, Category, "bikes")).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// The set's half of the licence: without one AND row the emulator measured, AND is unlicensed there, and a
        /// conjunction of two sides that read no AND row is unproven where each side is proven.
        /// </summary>
        [Fact]
        public void WithoutAConnectiveRowNothingComposes()
        {
            var dropped = MeasuredKey.Of(Op.And, CosmosValueClass.True, CosmosValueClass.False);
            var theory = RenderingTheory.Define(Measured with { Rows = [.. ProbeRows.Rows.Where(row => row.Key != dropped || row.Target != RenderingTarget.Emulator)] });
            Probe.Value.Theory.Get(Probe.Value.Licensed.Of(RenderingSet.Emulator)).Should().BeEquivalentTo(new[] { Op.And, Op.Or, Op.Not });
            theory.Theory.Get(theory.Licensed.Of(RenderingSet.Emulator)).Should().BeEquivalentTo(new[] { Op.Or, Op.Not });

            var conjunct = And(Eq(Text(KindPath), "a"), Eq(Text(CategoryPath), "b"));
            var form = All(KindIsA, Compare(Op.Equal, Category, "b"));
            Check(conjunct, form).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
            Check(conjunct, form, theory).Equal.Kind.Should().Be(RenderingVerdictKind.Unproven);
            Check(Eq(Text(KindPath), "a"), KindIsA, theory).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
            Check(Eq(Text(CategoryPath), "b"), form.Members[1], theory).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// A junction of three members at one atom is evaluated regrouped and rid of copies, which only the licence makes
        /// its value: with one AND row dropped, the flat guarded form is unproven, where its members written two by two,
        /// whose rows are read as written and hold no dropped key, prove.
        /// </summary>
        [Fact]
        public void AJunctionOfThreeIsRegroupedOnlyUnderTheLicence()
        {
            var dropped = MeasuredKey.Of(Op.And, CosmosValueClass.Undefined, CosmosValueClass.Undefined);
            var theory = RenderingTheory.Define(Measured with { Rows = [.. ProbeRows.Rows.Where(row => row.Key != dropped || row.Target != RenderingTarget.Emulator)] });
            var notBikes = Ne(Text(LabelPath), "bikes");
            Check(notBikes, Guarded).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
            Check(notBikes, Guarded, theory).Equal.Kind.Should().Be(RenderingVerdictKind.Unproven);
            Check(notBikes, All(Guarded.Members[0], All(Guarded.Members[1], Bare)), theory).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// A path written bare is read inside AND by the row for the class of what it holds, so AND is licensed only where
        /// the rows for values that are no boolean match Kleene's too: without them a junction holding one is unproven.
        /// </summary>
        [Fact]
        public void WithoutTheNonBooleanRowsABareMemberDoesNotCompose()
        {
            CosmosValueClass[] kinds = [CosmosValueClass.Absent, CosmosValueClass.Null, CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Number,
                CosmosValueClass.String, CosmosValueClass.Object, CosmosValueClass.Array];
            var bare = kinds.SelectMany(kind => new[] { RenderingTarget.Emulator, RenderingTarget.Azure }.Select(target => new MeasuredOutcome("Service.BarePathKeepsOnlyTrue",
                MeasuredKey.Of(Op.Bare, CosmosValueClass.None, kind), target,
                kind == CosmosValueClass.True ? CosmosAnswer.True : kind == CosmosValueClass.False ? CosmosAnswer.False : CosmosAnswer.Undefined)));
            static bool Boolean(CosmosValueClass x) => x is CosmosValueClass.None or CosmosValueClass.True or CosmosValueClass.False or CosmosValueClass.Undefined;
            var booleans = ProbeRows.Rows.Where(row => row.Key.Test is not (Op.And or Op.Or or Op.Not) || Boolean(row.Key.Operand) && Boolean(row.Key.Cell));

            var conjunct = And(Eq(Raw(FlagPath), true), Eq(Text(KindPath), "a"));
            var form = All(ServiceForm.BareOf(SqlValue.Spell(FlagPath)), KindIsA);
            Check(conjunct, form, RenderingTheory.Define(Measured with { Rows = [.. ProbeRows.Rows, .. bare] })).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);

            var theory = RenderingTheory.Define(Measured with { Rows = [.. booleans, .. bare] });
            theory.Theory.Get(theory.Licensed.Of(RenderingSet.Emulator)).Should().BeEmpty("no connective is licensed");
            Check(conjunct, form, theory).Equal.Kind.Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>
        /// Two bounds of one path whose literal parts the orders there need the range licence, which only a set whose every
        /// target answers the range pointwise holds: planted Range rows, kept by the emulator and not by Azure (N8), license
        /// it under the emulator and not under the portable set; bounds that part nothing compose under both, and the
        /// measured definition, holding no Range row, licenses it nowhere.
        /// </summary>
        [Fact]
        public void APartedRangeComposesOnlyWhereRangeIsLicensed()
        {
            var range = MeasuredKey.Of(Op.Range, CosmosValueClass.Parted, CosmosValueClass.Between);
            var orders = new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }.SelectMany(side => new[]
            {
                (MeasuredKey.Of(Op.GreaterOrEqual, CosmosValueClass.String, side), side == CosmosValueClass.Below ? CosmosAnswer.False : CosmosAnswer.True),
                (MeasuredKey.Of(Op.LessOrEqual, CosmosValueClass.String, side), side == CosmosValueClass.Above ? CosmosAnswer.False : CosmosAnswer.True),
            });
            var planted = Measured with
            {
                Rows =
                [
                    .. ProbeRows.Rows,
                    .. orders.SelectMany(o => new[] { RenderingTarget.Emulator, RenderingTarget.Azure }.Select(target => new MeasuredOutcome("Service.StringOrderIsCodePoint", o.Item1, target, o.Item2))),
                    new("Service.RangeOverPartedBoundsIsEmpty", range, RenderingTarget.Emulator, CosmosAnswer.True),
                    new("Service.RangeOverPartedBoundsIsEmpty", range, RenderingTarget.Azure, CosmosAnswer.NotKept),
                ],
                Kleene = [.. Measured.Kleene, (RenderingTarget.Emulator, range, CosmosAnswer.True), (RenderingTarget.Azure, range, CosmosAnswer.True)],
            };

            // Strings the two orders rank alike, beneath a bound that holds the high band: each bound is exact alone.
            var high = "\uE000";
            var conjunct = And(Cmp(Text(LabelPath), ">=", Lit(high)), Cmp(Text(LabelPath), "<=", Lit("z")));
            var form = All(Compare(Op.GreaterOrEqual, Label, high), Compare(Op.LessOrEqual, Label, "z"));
            var strings = new PathValues(ValueSet.Of(StringType), ValueSet.Of(Of("a"), Of("b")), ValueSet<string>.Empty, null, null);
            RenderingVerdictKind Composed(RenderingTheory theory, RenderingSet set, ValueSet<string> classes) =>
                Check(conjunct, form, theory, set, LabelPath, strings, classes).Equal.Kind;

            var theory = RenderingTheory.Define(planted);
            Composed(theory, RenderingSet.Emulator, AllBands).Should().Be(RenderingVerdictKind.Proven);
            Composed(theory, RenderingSet.Portable, AllBands).Should().Be(RenderingVerdictKind.Unproven);
            Composed(theory, RenderingSet.Portable, NoBand).Should().Be(RenderingVerdictKind.Proven);
            Composed(RenderingTheory.Define(planted with { Kleene = Measured.Kleene }), RenderingSet.Emulator, AllBands).Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>
        /// Descent proposes every member to the IR's first arm and the rest to its second, and a junction composes only
        /// where its form is exactly the join: swapped members and a member written twice alike prove. A member written
        /// twice differently is left over: unproven where it falls to the first arm, which is offered members one at a
        /// time, and proven where the second arm evaluates it with its own on its cells. A plant that drops it, as the
        /// aligner did, is refused.
        /// </summary>
        [Fact]
        public void EveryPairingIsTriedAndOnlyAnExactJoinComposes()
        {
            ((SqlPredicate.And)LabelAndKind).Arms[0].Digest.Should().Be(Eq(Text(KindPath), "a").Digest, "the IR orders its arms by digest");
            Check(LabelAndKind, All(KindIsA, Guarded)).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);
            Check(LabelAndKind, All(Guarded, KindIsA, Guarded)).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);

            // A disjunction of the same members, claimed beside the conjunction, offers its sides the same candidates, and
            // is still no join of a conjunction's sides.
            using (var both = Probe.Value.Open(RenderingSet.Emulator))
            {
                var conjunct = both.Write(LabelAndKind);
                both.Import("test", LabelPath, PathValues.Anything, AllBands);
                both.Import("test", KindPath, PathValues.Anything, AllBands);
                both.Claim(conjunct, All(KindIsA, Guarded));
                both.Claim(conjunct, Any(KindIsA, Guarded));
                both.Run().StopReason.Should().Be(RunStopReason.Completed);
                (both.Read(conjunct, All(KindIsA, Guarded), Verdict.Equal).Kind, both.Read(conjunct, Any(KindIsA, Guarded), Verdict.Equal).Kind)
                    .Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven));
            }

            var leftOver = All(Guarded, KindIsA, All(Is(Op.IsString, Kind), KindIsA));
            var (equal, implies) = Check(LabelAndKind, leftOver);
            (equal.Kind, implies.Kind).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven));
            Check(LabelAndKind, All(Guarded, KindIsA, All(Negated(Is(Op.IsNull, Label)), Is(Op.IsPrimitive, Label), Bare))).Equal.Kind
                .Should().Be(RenderingVerdictKind.Proven, "label's two guarded forms are evaluated together on its cells");

            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "descend").DefineRule("planted: the rest without a member that differs in text", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, d) = r.Vars<RenderPosition, JunctionSide>("pos", "d");
                var (f, g, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("f", "g", "h");
                r.When(theory.Candidate.Of(e, pos, f), theory.Junction.Of(e, x, y, d), theory.Member.Of(f, g), theory.Member.Of(f, h), h.IsNot(g));
                r.Where(RenderingPrimitives.SideIsLeft.Of(d));
                r.Where(Primitives.Greater.Of(Compute.Of(f, w => (long)w.Members.Count), 2L));
                r.Then(theory.Candidate.Of(y, pos, Compute.Of(f, g, h, (w, a, b) => ServiceForm.Without(ServiceForm.Without(w, a), b))));
            });

            using var bundle = theory.Open(RenderingSet.Emulator);
            var root = bundle.Write(LabelAndKind);
            bundle.Import("test", LabelPath, PathValues.Anything, AllBands);
            bundle.Import("test", KindPath, PathValues.Anything, AllBands);
            bundle.Claim(root, leftOver);
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
            bundle.Scope.Rows(theory.Agrees).Should().Contain(row => row.Keys[2].Equals(Guarded), "the plant offered the label side its own member alone");
            bundle.Read(root, leftOver, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>
        /// A refutation composes across locations a document sets apart: two properties of one parent. Two conjuncts of one
        /// path are unproven, where one of another is refuted with a witness setting both; and a refutation that already
        /// sets the sibling's path composes no further, since its witness would set that path twice.
        /// </summary>
        [Fact]
        public void ARefutationNeedsDisjointLocations()
        {
            var samePath = Check(And(Ne(Text(LabelPath), "bikes"), Ne(Text(LabelPath), "cars")), All(Bare, Compare(Op.NotEqual, Label, "cars")));
            samePath.Equal.Kind.Should().Be(RenderingVerdictKind.Unproven);

            var apart = Check(LabelAndKind, All(Bare, KindIsA)).Equal;
            apart.Kind.Should().Be(RenderingVerdictKind.Refuted);
            apart.Refutation!.Cells.Should().BeEquivalentTo(new[] { Label + "=" + ArrayType, Kind + "=" + Of("a") });

            // Calcite and the service both keep kind = 'a' alone, so no document refutes it; the OR beneath is refuted with
            // kind set to a cell other than 'a', and the AND's identity needs it to be 'a'.
            var nested = Check(And(Eq(Text(KindPath), "a"), Or(Ne(Text(LabelPath), "bikes"), Eq(Text(KindPath), "a"))), All(KindIsA, Any(Bare, KindIsA)));
            nested.Equal.Kind.Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>
        /// A proof over no cell holds of nothing, and says so through every junction and negation above it: a side imported
        /// as holding nothing makes its conjunction's proof vacuous, and a negation's too.
        /// </summary>
        [Fact]
        public void AVacuousChildMakesItsJunctionVacuous()
        {
            var nothing = PathValues.OfKinds(ValueSet<string>.Empty);
            var junction = Check(LabelAndKind, All(Guarded, KindIsA), path: KindPath, values: nothing).Equal;
            (junction.Kind, junction.Proof!.Vacuous, junction.Proof.Cells).Should().Be((RenderingVerdictKind.Proven, true, 11));

            var negation = Check(Not(Eq(Text(KindPath), "a")), Negated(KindIsA), path: KindPath, values: nothing).Equal;
            (negation.Kind, negation.Proof!.Vacuous).Should().Be((RenderingVerdictKind.Proven, true));

            Check(LabelAndKind, All(Guarded, KindIsA)).Equal.Proof!.Vacuous.Should().BeFalse();
        }

    }

}
