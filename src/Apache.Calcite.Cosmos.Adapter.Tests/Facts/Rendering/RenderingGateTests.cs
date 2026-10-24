using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

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
    /// The gate on one atom, as S's rules derive it in a bundle: the probe's checks (T1, T2, T5) as verdicts, and the
    /// disciplines a verdict rests on — one node per class (T7), bundles on several threads (T8), each evaluation once per
    /// key, nothing read off a run that stopped short, a contradiction made loud, and a form over another path left
    /// unevaluated.
    /// </summary>
    public sealed class RenderingGateTests
    {

        static readonly SqlPredicate NotBikes = Ne(Text(LabelPath), "bikes");

        static readonly PathValues DeclaredString = PathValues.OfKinds(ValueSet.Of(StringType));

        /// <summary>
        /// Text <c>label &lt;&gt; 'bikes'</c> over an undeclared path: the guarded form the translator writes is exact; the
        /// bare one is implied, and refuted for Equal where a <c>WHERE</c> keeps a null, an object or an array Calcite keeps
        /// neither way. The refutation names the least of those cells by text, the array.
        /// </summary>
        [Fact]
        public void T1_T5()
        {
            var guarded = Check(NotBikes, Guarded);
            (guarded.Equal.Kind, guarded.Implies.Kind).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Proven));
            (guarded.Equal.Proof!.Cells, guarded.Equal.Vacuous).Should().Be((11, false));
            guarded.Equal.Proof!.Rows.Should().Contain(new[] { "Service.IsNull", "Service.IsPrimitive", "Service.BarePathKeepsOnlyTrue" })
                .And.NotContain(row => row.StartsWith("missing:"));

            var bare = Check(NotBikes, Bare);
            (bare.Equal.Kind, bare.Implies.Kind).Should().Be((RenderingVerdictKind.Refuted, RenderingVerdictKind.Proven));
            bare.Equal.Refutation.Should().Be(bare.Equal.Refutation! with
            {
                Cells = ValueSet.Of(Label + "=" + ArrayType),
                Class = new CellClass(Truths.Unknown, ServiceOutcomes.True),
            });
        }

        /// <summary>Over a declared, required string the bare form is exact.</summary>
        [Fact]
        public void T2() => Check(NotBikes, Bare, path: LabelPath, values: DeclaredString).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);

        /// <summary>
        /// The tripwire: a planted rule that merges two atoms leaves a class with two nodes, which the run reports and the
        /// read answers as a conflict, whatever the run derived.
        /// </summary>
        [Fact]
        public void T7_AMergeOfTwoTermsTripsTheWire()
        {
            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "gate").DefineRule("planted: two atoms of one path are one", r =>
            {
                var (e1, e2, p) = r.Vars<Pred, Pred, JPath>("e1", "e2", "p");
                var (a1, a2) = r.Vars<PredicateAtom, PredicateAtom>("a1", "a2");
                r.When(theory.Ir.Atom.Of(e1, p, a1), theory.Ir.Atom.Of(e2, p, a2), e1.IsNot(e2));
                r.Then(e1.Is(e2));
            });

            // The merged atoms are another path's, unclaimed, so the claim's own rows stand: the read refuses them.
            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(NotBikes);
            bundle.Write(Eq(Text(P("kind")), "a"));
            bundle.Write(Eq(Text(P("kind")), "b"));
            bundle.Import(Source, LabelPath, DeclaredString, AllBands);
            bundle.Claim(root, Bare);
            bundle.Run().OneNodePerClass.Should().BeFalse();
            bundle.Read(root, Bare, Verdict.Equal).Should().Be(new RenderingVerdict(RenderingVerdictKind.Conflict, null, null, "tripwire: a rule merged two terms"));
        }

        /// <summary>Bundles under the frozen theory on four threads answer as they do one at a time.</summary>
        [Fact]
        public void T8_SixteenBundlesOnFourThreadsEqualTheirSerialAnswers()
        {
            (RenderingVerdict, RenderingVerdict) One(int k)
            {
                var literal = "bikes" + k;
                var bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Label, Of(literal));
                var form = k % 2 == 0 ? bare : ServiceForm.JunctionOf(CosmosServiceTest.And, [Guarded.Members[0], Guarded.Members[1], bare]);
                return Check(Ne(Text(LabelPath), literal), form, path: LabelPath, values: k % 4 < 2 ? PathValues.Anything : DeclaredString);
            }

            var serial = Enumerable.Range(0, 16).Select(One).ToArray();
            var parallel = new (RenderingVerdict, RenderingVerdict)[16];
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 4 }, k => parallel[k] = One(k));
            parallel.Should().Equal(serial);
            serial.Select(v => v.Item1.Kind).Distinct().Should().BeEquivalentTo(new[] { RenderingVerdictKind.Proven, RenderingVerdictKind.Refuted });
        }

        /// <summary>
        /// Each table the evaluation fills is computed once a match, and from nothing else: counted through the definition's
        /// primitives, over two conjuncts of two paths, which share no partition, so once a match is once a key there.
        /// </summary>
        [Fact]
        public void EachEvaluationIsComputedOncePerKey()
        {
            var calls = new ConcurrentDictionary<object, int>();
            T Count<T>(object key, T value)
            {
                calls.AddOrUpdate(key, 1, (_, n) => n + 1);
                return value;
            }

            var theory = RenderingTheory.Define(Measured with
            {
                Cut = (a, f, g) => Count(("cuts", a, f), Measured.Cut(a, f, g)),
                Split = (n, x, k, c) => Count(("partition", n, x, k, c), Measured.Split(n, x, k, c)),
                Leaf = (f, z, t, q) => Count(("svc", z, f), Measured.Leaf(f, z, t, q)),
                Compound = (f, v, w, t) => Count(("svc", v.Cells, f), Measured.Compound(f, v, w, t)),
                Pair = (a, v, m, t) => Count(("outcomes", a, v), Measured.Pair(a, v, m, t)),
            });

            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var kind = P("kind");
            var label = bundle.Write(NotBikes);
            var named = bundle.Write(Eq(Text(kind), "a"));
            bundle.Import(Source, LabelPath, PathValues.Anything, AllBands);
            bundle.Import(Source, kind, PathValues.Anything, AllBands);
            bundle.Claim(label, Guarded);
            bundle.Claim(named, ServiceForm.CompareOf(CosmosServiceTest.Equal, SqlValue.Spell(kind), Of("a")));
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);

            calls.Values.Should().OnlyContain(n => n == 1);
            int Keys(string table) => calls.Keys.Count(k => ((ITuple)k)[0]!.Equals(table));
            (Keys("cuts"), Keys("partition"), Keys("svc"), Keys("outcomes")).Should().Be(
                ((int)bundle.Scope.CountRows(theory.Partition), (int)bundle.Scope.CountRows(theory.Partition), (int)bundle.Scope.CountRows(theory.Svc), (int)bundle.Scope.CountRows(theory.Outcomes)));
            Keys("svc").Should().Be(7, "the guarded form's six parts and the comparison of kind");
        }

        /// <summary>A run stopped by its limit answers nothing, though it derived something before it stopped.</summary>
        [Fact]
        public void ReadWaitsForCompleted()
        {
            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(NotBikes);
            bundle.Import(Source, LabelPath, PathValues.Anything, AllBands);
            bundle.Claim(root, Guarded);
            bundle.Run(iterationLimit: 2).StopReason.Should().Be(RunStopReason.IterationLimit);
            bundle.Read(root, Guarded, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Incomplete);
        }

        /// <summary>
        /// A plant that makes the box in which both sides keep every row refute too: one claim is agreed and refuted, the
        /// run says so, and the read answers a conflict, never either verdict.
        /// </summary>
        [Fact]
        public void AConflictIsLoud()
        {
            var theory = RenderingTheory.Define(Measured with
            {
                Refuting = [.. Measured.Refuting, (RenderPosition.Pos, Verdict.Equal, new Box(Truths.True, ServiceOutcomes.True, ValueSet<string>.Empty))],
            });

            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(NotBikes);
            bundle.Import(Source, LabelPath, DeclaredString, AllBands);
            bundle.Claim(root, Bare);
            bundle.Run().Contradicted.Should().BeTrue();
            bundle.Read(root, Bare, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Conflict);
        }

        /// <summary>
        /// The plant "a form over another path": a translator writing <c>lable</c> for <c>label</c> is never evaluated on
        /// <c>label</c>'s cells, so it is neither proven nor refuted.
        /// </summary>
        [Fact]
        public void AFormOverAnotherPathIsNotEvaluated()
        {
            var lable = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, SqlValue.Spell(P("lable")), Of("bikes"));
            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(NotBikes);
            bundle.Import(Source, LabelPath, PathValues.Anything, AllBands);
            bundle.Claim(root, lable);
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
            (bundle.Read(root, lable, Verdict.Equal).Kind, bundle.Read(root, lable, Verdict.Implies).Kind).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven));
            (bundle.Scope.CountRows(ProbeTheory.Partition), bundle.Scope.CountRows(ProbeTheory.Svc)).Should().Be((0, 0));
        }

        /// <summary>
        /// A text cast of the raw value reads its three columns on a cell (<c>Calcite.CastOfARawValueToText</c>): its
        /// equality with a string is the service's, a stored null is null in each, so <c>NOT</c> over the bare equality is
        /// refuted there, and over a boolean a <c>VARIANT</c>'s null and an <c>ANY</c>'s rendering part, so the guarded form
        /// the translator writes is implied and no more.
        /// </summary>
        [Fact]
        public void ACastToTextOverARawValueReadsItsThreeColumns()
        {
            var (cast, bare) = (Cast("VARCHAR", Raw(LabelPath)), ServiceForm.CompareOf(CosmosServiceTest.Equal, Label, Of("b")));
            var guarded = Negated(Any(bare, ServiceForm.TestOf(CosmosServiceTest.IsNull, Label), Negated(ServiceForm.TestOf(CosmosServiceTest.IsDefined, Label))));
            static (RenderingVerdictKind, RenderingVerdictKind) Kinds((RenderingVerdict Equal, RenderingVerdict Implies) v) => (v.Equal.Kind, v.Implies.Kind);

            Kinds(Check(Eq(cast, "b"), bare)).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Proven));
            Kinds(Check(Not(Eq(cast, "b")), Negated(bare))).Should().Be((RenderingVerdictKind.Refuted, RenderingVerdictKind.Proven));
            Kinds(Check(Not(Eq(cast, "b")), guarded)).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Proven));
        }

        /// <summary>
        /// The emulator orders a string holding U+0000 as it stores it, so over strings an ordering is proven only where
        /// none may hold one, and nothing is proven against a literal holding one; an equality with ASCII is, whatever
        /// they hold (T1).
        /// </summary>
        [Fact]
        public void AStringHoldingU0000IsReadAsTheEmulatorStoresIt()
        {
            var strings = PathValues.OfKinds(ValueSet.Of(StringType));
            var withoutNul = ValueSet.From(AllBands.Where(c => c != NulClass));
            RenderingVerdictKind Compared(string op, string literal, ValueSet<string> classes) => Check(Cmp(Text(LabelPath), op, Lit(literal)),
                ServiceForm.CompareOf(op == "<" ? CosmosServiceTest.Less : CosmosServiceTest.Equal, Label, Of(literal)), path: LabelPath, values: strings, classes: classes).Equal.Kind;

            (Compared("<", "b", AllBands), Compared("<", "b", withoutNul), Compared("<", "a\0", withoutNul), Compared("=", "\0", AllBands))
                .Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven));
        }

        /// <summary>
        /// A comparison with U+E000, an ordering or an equality alike, parts the two string orders where the path's strings
        /// may hold a surrogate, and only there: the literal holds the high band, so the import must hold the other. An
        /// interval below it by unit may lie above it by code point, where <c>!=</c> reads the other side's row.
        /// </summary>
        [Fact]
        public void AComparisonPartsTheOrdersWhereTheImportMayHoldTheOtherBand()
        {
            var high = "";
            foreach (var (chars, parted, op) in from c in new[] { (AllBands, true), (ValueSet.Of(HighBand), false), (NoBand, false) } from op in new[] { "<", "<>" } select (c.Item1, c.Item2, op))
            {
                var form = ServiceForm.CompareOf(op == "<" ? CosmosServiceTest.Less : CosmosServiceTest.NotEqual, Label, Of(high));
                using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
                var root = bundle.Write(Cmp(Text(LabelPath), op, Lit(high)));
                bundle.Import(Source, LabelPath, PathValues.Anything, chars);
                bundle.Claim(root, form);
                bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
                bundle.Scope.Rows(ProbeTheory.PartedLiterals).Single().Output.Should().Be(parted ? ValueSet.Of(Of(high)) : ValueSet<string>.Empty, string.Join(",", chars.AsEnumerable()));
            }
        }

    }

}
