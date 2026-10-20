using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
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

        static readonly JsonDocumentPath LabelPath = P("label");

        static readonly string Label = SqlValue.Spell(LabelPath);

        static readonly SqlPredicate NotBikes = Ne(Text(LabelPath), "bikes");

        static readonly ServiceForm Bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Label, Of("bikes"));

        static readonly ServiceForm Guarded = ServiceForm.JunctionOf(CosmosServiceTest.And,
            [ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, Label), ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, Label)), Bare]);

        static readonly PathValues DeclaredString = PathValues.OfKinds(ValueSet.Of(StringType));

        static readonly RenderingDefinition Measured = RenderingDefinition.Measured(ProbeRows.Rows);

        static readonly Lazy<RenderingTheory> Probe = new(() => RenderingTheory.Define(Measured));

        /// <summary>
        /// Checks a form claimed for a conjunct over <c>label</c>, in a bundle of its own under the emulator, and reads both
        /// claims; the run must complete with one node per class.
        /// </summary>
        static (RenderingVerdict Equal, RenderingVerdict Implies) Check(ServiceForm form, PathValues import, SqlPredicate? conjunct = null, RenderingTheory? theory = null)
        {
            using var bundle = (theory ?? Probe.Value).Open(RenderingSet.Emulator);
            var root = bundle.Write(conjunct ?? NotBikes);
            bundle.Import("test", LabelPath, import, AllBands);
            bundle.Claim(root, form);
            var run = bundle.Run();
            (run.StopReason, run.OneNodePerClass, run.Contradicted).Should().Be((RunStopReason.Completed, true, false));
            return (bundle.Read(root, form, Verdict.Equal), bundle.Read(root, form, Verdict.Implies));
        }

        /// <summary>
        /// Text <c>label &lt;&gt; 'bikes'</c> over an undeclared path: the guarded form the translator writes is exact; the
        /// bare one is implied, and refuted for Equal where a <c>WHERE</c> keeps a null, an object or an array Calcite keeps
        /// neither way. The refutation names the least of those cells by text, the array.
        /// </summary>
        [Fact]
        public void T1_T5()
        {
            var guarded = Check(Guarded, PathValues.Anything);
            (guarded.Equal.Kind, guarded.Implies.Kind).Should().Be((RenderingVerdictKind.Proven, RenderingVerdictKind.Proven));
            (guarded.Equal.Proof!.Cells, guarded.Equal.Vacuous).Should().Be((11, false));
            guarded.Equal.Proof!.Rows.Should().Contain(new[] { "Service.IsNull", "Service.IsPrimitive", "Service.BarePathKeepsOnlyTrue" })
                .And.NotContain(row => row.StartsWith("missing:"));

            var bare = Check(Bare, PathValues.Anything);
            (bare.Equal.Kind, bare.Implies.Kind).Should().Be((RenderingVerdictKind.Refuted, RenderingVerdictKind.Proven));
            bare.Equal.Refutation.Should().Be(bare.Equal.Refutation! with
            {
                Cells = ValueSet.Of(Label + "=" + ArrayType),
                Class = new CellClass(Truths.Unknown, ServiceOutcomes.True),
            });
        }

        /// <summary>Over a declared, required string the bare form is exact.</summary>
        [Fact]
        public void T2() => Check(Bare, DeclaredString).Equal.Kind.Should().Be(RenderingVerdictKind.Proven);

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
            using var bundle = theory.Open(RenderingSet.Emulator);
            var root = bundle.Write(NotBikes);
            bundle.Write(Eq(Text(P("kind")), "a"));
            bundle.Write(Eq(Text(P("kind")), "b"));
            bundle.Import("test", LabelPath, DeclaredString, AllBands);
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
                return Check(form, k % 4 < 2 ? PathValues.Anything : DeclaredString, Ne(Text(LabelPath), literal));
            }

            var serial = Enumerable.Range(0, 16).Select(One).ToArray();
            var parallel = new (RenderingVerdict, RenderingVerdict)[16];
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 4 }, k => parallel[k] = One(k));
            parallel.Should().Equal(serial);
            serial.Select(v => v.Item1.Kind).Distinct().Should().BeEquivalentTo(new[] { RenderingVerdictKind.Proven, RenderingVerdictKind.Refuted });
        }

        /// <summary>
        /// Each table the evaluation fills is computed once for each of its keys, and from nothing else: counted through
        /// the definition's primitives, over two conjuncts of two paths.
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
                Pair = (a, v, m) => Count(("outcomes", a, v), Measured.Pair(a, v, m)),
            });

            using var bundle = theory.Open(RenderingSet.Emulator);
            var kind = P("kind");
            var label = bundle.Write(NotBikes);
            var named = bundle.Write(Eq(Text(kind), "a"));
            bundle.Import("test", LabelPath, PathValues.Anything, AllBands);
            bundle.Import("test", kind, PathValues.Anything, AllBands);
            bundle.Claim(label, Guarded);
            bundle.Claim(named, ServiceForm.CompareOf(CosmosServiceTest.Equal, SqlValue.Spell(kind), Of("a")));
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);

            calls.Values.Should().OnlyContain(n => n == 1);
            int Keys(string table) => calls.Keys.Count(k => ((ITuple)k)[0]!.Equals(table));
            (Keys("cuts"), Keys("partition"), Keys("svc"), Keys("outcomes")).Should().Be(
                ((int)bundle.Scope.CountRows(theory.Partition), (int)bundle.Scope.CountRows(theory.Partition), (int)bundle.Scope.CountRows(theory.Svc), (int)bundle.Scope.CountRows(theory.Outcomes)));
            Keys("svc").Should().Be(7, "the guarded form's six parts and the comparison of kind");
        }

        /// <summary>
        /// A leaf that answers one key two ways faults the bundle: two conjuncts cut at one literal share a partition, so
        /// the leaf their forms share is evaluated for each, and <c>svc</c> holds one value a key.
        /// </summary>
        [Fact]
        public void ANondeterministicLeafFaultsTheBundle()
        {
            var calls = 0;
            var theory = RenderingTheory.Define(Measured with
            {
                Leaf = (f, z, t, q) =>
                {
                    var v = Measured.Leaf(f, z, t, q);
                    return v with { Rows = v.Rows.Add("call " + Interlocked.Increment(ref calls)) };
                },
            });

            using var bundle = theory.Open(RenderingSet.Emulator);
            var unequal = bundle.Write(NotBikes);
            var equal = bundle.Write(Eq(Text(LabelPath), "bikes"));
            bundle.Import("test", LabelPath, PathValues.Anything, AllBands);
            bundle.Claim(unequal, Bare);
            bundle.Claim(equal, Bare);
            FluentActions.Invoking(() => bundle.Run()).Should().Throw<RuleEvaluationException>().WithMessage("*function svc*");
            bundle.Scope.IsFaulted.Should().BeTrue();
        }

        /// <summary>A run stopped by its limit answers nothing, though it derived something before it stopped.</summary>
        [Fact]
        public void ReadWaitsForCompleted()
        {
            using var bundle = Probe.Value.Open(RenderingSet.Emulator);
            var root = bundle.Write(NotBikes);
            bundle.Import("test", LabelPath, PathValues.Anything, AllBands);
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

            using var bundle = theory.Open(RenderingSet.Emulator);
            var root = bundle.Write(NotBikes);
            bundle.Import("test", LabelPath, DeclaredString, AllBands);
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
            using var bundle = Probe.Value.Open(RenderingSet.Emulator);
            var root = bundle.Write(NotBikes);
            bundle.Import("test", LabelPath, PathValues.Anything, AllBands);
            bundle.Claim(root, lable);
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
            (bundle.Read(root, lable, Verdict.Equal).Kind, bundle.Read(root, lable, Verdict.Implies).Kind).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven));
            (bundle.Scope.CountRows(Probe.Value.Partition), bundle.Scope.CountRows(Probe.Value.Svc)).Should().Be((0, 0));
        }

        /// <summary>
        /// A text cast of the raw value has no edges row, so the written form is never evaluated: over a <c>VARIANT</c>
        /// Calcite casts a boolean to null, where the service compares it.
        /// </summary>
        [Fact]
        public void ACastToTextOverARawValueIsNeverEvaluated()
        {
            var form = ServiceForm.NotOf(ServiceForm.CompareOf(CosmosServiceTest.Equal, Label, Of("b")));
            var (equal, implies) = Check(form, PathValues.Anything, Not(Eq(Cast("VARCHAR", Raw(LabelPath)), "b")));
            (equal.Kind, implies.Kind).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven));
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
                using var bundle = Probe.Value.Open(RenderingSet.Emulator);
                var root = bundle.Write(Cmp(Text(LabelPath), op, Lit(high)));
                bundle.Import("test", LabelPath, PathValues.Anything, chars);
                bundle.Claim(root, form);
                bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
                bundle.Scope.Rows(Probe.Value.PartedLiterals).Single().Output.Should().Be(parted ? ValueSet.Of(Of(high)) : ValueSet<string>.Empty, string.Join(",", chars.AsEnumerable()));
            }
        }

    }

}
