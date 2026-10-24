using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using Op = Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Generate mode's theory (spec g03), on synthetic bundles: the forms it keeps for an atom, through <c>NOT</c> and a
    /// junction, and for the round's junction; what it never lifts; presence; the weakening at a leaf, a junction and a
    /// negation; the one discipline every read passes; and every run over the check run's inputs completing within its
    /// limits.
    /// </summary>
    public sealed class RenderingGenerateTests
    {

        static readonly JsonDocumentPath NPath = P("n"), PricePath = P("price"), NamePath = P("name");

        static readonly string Kind = SqlValue.Spell(KindPath), N = SqlValue.Spell(NPath), Price = SqlValue.Spell(PricePath), Name = SqlValue.Spell(NamePath);

        static ServiceForm Is(Op test, string path) => ServiceForm.TestOf(test, path);

        static ServiceForm Compare(Op op, string path, string literal) => ServiceForm.CompareOf(op, path, literal);

        /// <summary>A2 over text: present, not null, a scalar, and the comparison.</summary>
        static ServiceForm Restricted(string path, ServiceForm core) => All(Is(Op.IsDefined, path), Negated(Is(Op.IsNull, path)), Is(Op.IsPrimitive, path), core);

        /// <summary>
        /// What generate mode says of each root in a bundle of its own, every path imported as anything unless given, and the
        /// agreements it reports of each; and of every node beneath, at both positions, its best, implied forms and
        /// weakening, and every candidate and agreement, read before the bundle goes.
        /// </summary>
        sealed record Generated(RenderingRun Run, RenderingAnswer[] Answers, IReadOnlyList<(ServiceForm Form, Verdict Claim)>[] Reported,
            Dictionary<(SqlPredicate, RenderPosition), (Priced? Best, ValueSet<ServiceForm>? Implied, ServiceForm? Weakening)> Nodes,
            List<ServiceForm> Candidates, List<(ServiceForm Form, Verdict Claim)> Agreed)
        {
            public Priced? Best(SqlPredicate node, RenderPosition pos = RenderPosition.Pos) => Nodes[(node, pos)].Best;

            public ValueSet<ServiceForm> Implied(SqlPredicate node, RenderPosition pos = RenderPosition.Pos) => Nodes[(node, pos)].Implied ?? ValueSet<ServiceForm>.Empty;

            public ServiceForm? Weakening(SqlPredicate node, RenderPosition pos = RenderPosition.Pos) => Nodes[(node, pos)].Weakening;
        }

        static Generated Generate(IReadOnlyList<SqlPredicate> roots, RenderingTheory? theory = null, RenderingSet set = RenderingCheckRun.AssertedSet,
            IReadOnlyDictionary<JsonDocumentPath, PathValues>? values = null, ValueSet<string>? chars = null, IReadOnlyDictionary<long, SqlValue>? opaques = null)
        {
            theory ??= ProbeTheory;
            using var bundle = theory.Open(set);
            var nodes = roots.SelectMany(r => r.Subterms).Distinct().ToList();
            var terms = nodes.ToDictionary(n => n, n => bundle.Write(n, opaques));
            foreach (var path in roots.SelectMany(r => r.Paths).Distinct())
                bundle.Import(Source, path, values?.GetValueOrDefault(path) ?? PathValues.Anything, chars ?? AllBands);

            foreach (var root in roots)
                bundle.Generate(terms[root]);

            var run = bundle.RunGenerate(RenderingBundle.WidthOf(roots));
            T? Get<T>(Term<T> term) where T : class => bundle.Failure is null && bundle.Scope.TryGet(term, out var value) ? value : null;
            var read = (from node in nodes from pos in System.Enum.GetValues<RenderPosition>()
                        select (node, pos, Read: (Get(theory.BestExact.Of(terms[node], pos)), Get(theory.Implied.Of(terms[node], pos)), Get(theory.Weakening.Of(terms[node], pos)))))
                .ToDictionary(x => (x.node, x.pos), x => x.Read);
            IEnumerable<TableRow> Rows(TableHandle table) => bundle.Failure is null ? bundle.Scope.Rows(table) : [];
            return new(run, [.. roots.Select(r => bundle.ReadBest(terms[r]))], [.. roots.Select(r => bundle.Agreements(terms[r], RenderPosition.Pos))], read,
                [.. Rows(theory.Candidate).Select(row => (ServiceForm)row.Keys[2])],
                [.. Rows(theory.Agrees).Select(row => ((ServiceForm)row.Keys[2], (Verdict)row.Keys[3]))]);
        }

        static Generated Generate(params SqlPredicate[] roots) => Generate(roots, theory: null);

        static readonly RenderingRun Disciplined = new(RunStopReason.Completed, true, false);

        /// <summary>
        /// P1: text <c>label &lt;&gt; 'bikes'</c> over an undeclared label is sent as the translator's restricted form, Exact
        /// under the asserted set and under <c>Production</c>; the bare form agrees as implied, never exact.
        /// </summary>
        [Fact]
        public void P1_AnUndeclaredTextInequalityIsSentRestricted()
        {
            foreach (var set in new[] { RenderingSet.Portable, RenderingSet.Production })
            {
                var root = Ne(Text(LabelPath), "bikes");
                var generated = Generate([root], set: set);
                generated.Run.Should().Be(Disciplined);
                var answer = generated.Answers.Single();
                (answer.Kind, answer.Best, answer.Weakening).Should().Be((RenderingVerdictKind.Proven, Restricted(Label, Bare), Restricted(Label, Bare)), set.ToString());
                answer.Proof!.Cells.Should().BePositive();
                generated.Reported.Single().Should().NotContain((Bare, Verdict.Equal)).And.Contain((Bare, Verdict.Implies)).And.Contain((Restricted(Label, Bare), Verdict.Equal));
            }
        }

        /// <summary>P8: over a label declared a required string the guards are vacuous, and the bare form is the cheapest exact one.</summary>
        [Fact]
        public void P8_ARequiredStringIsSentBare()
        {
            var generated = Generate([Ne(Text(LabelPath), "bikes")], values: new Dictionary<JsonDocumentPath, PathValues> { [LabelPath] = PathValues.OfKinds(ValueSet.Of(StringType)) });
            generated.Answers.Single().Best.Should().Be(Bare);
            generated.Agreed.Should().Contain((Restricted(Label, Bare), Verdict.Equal), "the restricted form is exact too, and dearer");
        }

        /// <summary>
        /// <c>NOT (x IS NULL)</c> is written in <c>IS NOT NULL</c>'s spelling, the null test's form at <c>Neg</c> stripped
        /// of its <c>NOT</c>; and a double <c>NOT</c> collapses to its operand's form.
        /// </summary>
        [Fact]
        public void ANegationStripsItsOperandsBest()
        {
            var answers = Generate(IsNotNull(Text(LabelPath)), Not(Not(Eq(Text(KindPath), "a")))).Answers;
            answers[0].Best.Should().Be(All(Is(Op.IsDefined, Label), Negated(Is(Op.IsNull, Label)), Is(Op.IsPrimitive, Label)));
            answers[1].Best.Should().Be(Compare(Op.Equal, Kind, Of("a")));
        }

        /// <summary>
        /// A junction's best is <c>Joined</c> of its arms' bests, in the IR's order, flattened: a conjunction's and a
        /// disjunction's of one path.
        /// </summary>
        [Fact]
        public void AJunctionsBestIsTheJoinOfItsArmsBests()
        {
            var (and, or) = (And(Ne(Text(LabelPath), "bikes"), Eq(Text(KindPath), "a")), Or(Eq(Text(KindPath), "a"), Eq(Text(KindPath), "b")));
            var generated = Generate(and, or);
            foreach (var (junction, connective, i) in new[] { (and, Op.And, 0), (or, Op.Or, 1) })
            {
                var arms = junction is SqlPredicate.And a ? a.Arms : ((SqlPredicate.Or)junction).Arms;
                var joined = ServiceForm.Joined(connective, generated.Best(arms[0])!.Form!, generated.Best(arms[1])!.Form!);
                generated.Answers[i].Best.Should().Be(joined);
            }

            generated.Answers[0].Best!.Members.Should().HaveCount(5, "the restricted form's members are spliced in");
            generated.Answers[1].Best.Should().Be(Any(Compare(Op.Equal, Kind, Of("a")), Compare(Op.Equal, Kind, Of("b"))));
        }

        /// <summary>
        /// The round's junction is one more root beside its conjuncts, sharing their terms: its best is the join of theirs,
        /// and it has none where a conjunct has none, though the other's stands: text <c>n = '1'</c> over a declared
        /// integer, which no template makes exact.
        /// </summary>
        [Fact]
        public void TheRoundsJunctionIsProvenOnlyWhereEveryConjunctIs()
        {
            var (label, kind, n) = (Ne(Text(LabelPath), "bikes"), Eq(Text(KindPath), "a"), Eq(Text(NPath), "1"));
            var (through, partial) = (SqlPredicate.AllOf([label, kind]), SqlPredicate.AllOf([label, n]));
            var generated = Generate([label, kind, n, through, partial], values: new Dictionary<JsonDocumentPath, PathValues> { [NPath] = PathValues.OfTypes(ValueSet.Of(IntegerType)) });

            var answers = generated.Answers;
            answers.Select(a => a.Kind).Should().Equal(RenderingVerdictKind.Proven, RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven, RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven);
            var arms = ((SqlPredicate.And)through).Arms;
            answers[3].Best.Should().Be(ServiceForm.Joined(Op.And, generated.Best(arms[0])!.Form!, generated.Best(arms[1])!.Form!));
            answers[4].Best.Should().BeNull();
        }

        /// <summary>
        /// Where Calcite raises on every document the import admits — a text cast to an integer over a path that holds one
        /// word — every candidate agrees and every agreement is vacuous: none is lifted, and none is an agreement reported.
        /// </summary>
        [Fact]
        public void AVacuousAgreementIsNeverLifted()
        {
            var root = Cmp(Cast("INTEGER", Text(NPath)), "=", Lit(5));
            var word = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(StringType)), PathValues.OneOf(ValueSet.Of(Of("abc"))));
            var generated = Generate([root], values: new Dictionary<JsonDocumentPath, PathValues> { [NPath] = word });
            generated.Agreed.Should().Contain(a => a.Claim == Verdict.Equal, "Calcite raises on every cell");
            generated.Best(root)!.Form.Should().BeNull();
            generated.Implied(root).Should().OnlyContain(f => f.Kind == ServiceFormKind.Test, "presence alone");
            generated.Answers.Single().Kind.Should().Be(RenderingVerdictKind.Unproven);
            generated.Reported.Single().Should().BeEmpty();
        }

        /// <summary>
        /// The band filter: a string ordering whose literal holds a character of a parting band is never sent, though over
        /// strings of no band the gate proves it exact; one whose literal holds none is.
        /// </summary>
        [Fact]
        public void TheBandFilterRefusesAnOrderingAgainstAPartingLiteral()
        {
            const string High = "\uE000";
            var strings = new PathValues(ValueSet.Of(StringType), ValueSet.Of(Of("a"), Of("b")), ValueSet<string>.Empty, null, null);
            var (parting, plain) = (Cmp(Text(LabelPath), ">=", Lit(High)), Cmp(Text(LabelPath), ">=", Lit("x")));

            Check(parting, Compare(Op.GreaterOrEqual, Label, Of(High)), path: LabelPath, values: strings, classes: NoBand).Equal.Kind.Should().Be(RenderingVerdictKind.Proven, "the gate proves it");
            var generated = Generate([parting, plain], values: new Dictionary<JsonDocumentPath, PathValues> { [LabelPath] = strings }, chars: NoBand);
            generated.Agreed.Should().Contain((Compare(Op.GreaterOrEqual, Label, Of(High)), Verdict.Equal));
            generated.Answers[0].Best.Should().BeNull();
            generated.Implied(parting).Should().NotContain(f => f.Literals.Contains(Of(High)));
            generated.Answers[1].Best.Should().Be(Compare(Op.GreaterOrEqual, Label, Of("x")));
        }

        /// <summary>
        /// <c>c.name = U&amp;'\D800'</c>: a literal the SDK refuses to send on either target is never a best, at either
        /// position, nor implied, though the gate agrees with forms holding it; the weakening is presence alone. Beside
        /// <c>&lt;&gt;</c> the restricted form is exact and refused, so the root is Unproven, and says which form the gate
        /// refuted.
        /// </summary>
        [Fact]
        public void ALoneSurrogateLiteralIsNeverAdmitted()
        {
            var (atom, unequal) = (Eq(Text(NamePath), "\uD800"), Ne(Text(NamePath), "\uD800"));
            var generated = Generate(atom, Not(atom), unequal);
            foreach (var pos in System.Enum.GetValues<RenderPosition>())
            {
                generated.Best(atom, pos)!.Form.Should().BeNull(pos.ToString());
                generated.Implied(atom, pos).Should().NotContain(f => f.Literals.Contains(Of("\uD800")), pos.ToString());
            }

            generated.Agreed.Should().Contain(a => a.Claim == Verdict.Equal && a.Form.Literals.Contains(Of("\uD800")), "the gate agrees with forms holding it");
            (generated.Answers[2].Kind, generated.Answers[2].Reason).Should().Be((RenderingVerdictKind.Unproven,
                "refuted: " + Compare(Op.NotEqual, Name, Of("\uD800")) + " at " + Name + "=" + ArrayType));
            generated.Answers[0].Weakening.Should().Be(Is(Op.IsDefined, Name));
        }

        /// <summary>
        /// Presence: a comparison read through a strict reading implies its path present at <c>Pos</c> and, beneath a
        /// <c>NOT</c>, absent at <c>Neg</c>; and nothing where the definition names no reading strict.
        /// </summary>
        [Fact]
        public void AComparisonOverAStrictReadingImpliesItsPathPresent()
        {
            var atom = Eq(Text(NPath), "1");
            var generated = Generate(atom, Not(atom));
            generated.Implied(atom).Should().Contain(Is(Op.IsDefined, N));
            generated.Implied(atom, RenderPosition.Neg).Should().Contain(Negated(Is(Op.IsDefined, N)));
            generated.Answers[0].Weakening.Should().Be(Is(Op.IsDefined, N));

            var none = RenderingTheory.Define(Measured with { StrictOperators = [], StrictReaders = [] });
            Generate([atom], theory: none).Implied(atom).Should().BeEmpty("a definition strict in nothing");
        }

        /// <summary>
        /// Presence through an opaque conjunct, read by the loader beside its term and written with it (spec g04), under the
        /// registry's strictness: <c>UPPER(c.name) = 'X'</c> implies the name present at <c>Pos</c> and absent at
        /// <c>Neg</c>; <c>CONCAT_WS('-', name, sku) &lt;&gt; 'x'</c> nothing, since no row names <c>CONCAT_WS</c>; and
        /// beside it under a strict <c>||</c>, <c>UPPER(label)</c> its own path, though its sibling gives none.
        /// </summary>
        [Fact]
        public void AnOpaqueConjunctImpliesThePathsItsStrictCallsRead()
        {
            var types = new JavaTypeFactoryImpl();
            var rex = new RexBuilder(types);
            var doc = rex.makeInputRef(types.createSqlType(SqlTypeName.VARCHAR), 0);
            RexNode Read(string path) => rex.makeCall(types.createSqlType(SqlTypeName.VARCHAR), SqlStdOperatorTable.JSON_VALUE, java.util.Arrays.asList(doc, rex.makeLiteral("$." + path)));
            var concat = rex.makeCall(SqlLibraryOperators.CONCAT_WS, rex.makeLiteral("-"), Read("name"), Read("sku"));
            RexNode[] conjuncts =
            [
                rex.makeCall(SqlStdOperatorTable.EQUALS, rex.makeCall(SqlStdOperatorTable.UPPER, Read("name")), rex.makeLiteral("X")),
                rex.makeCall(SqlStdOperatorTable.NOT_EQUALS, concat, rex.makeLiteral("x")),
                rex.makeCall(SqlStdOperatorTable.NOT_EQUALS, rex.makeCall(SqlStdOperatorTable.CONCAT, concat, rex.makeCall(SqlStdOperatorTable.UPPER, Read("label"))), rex.makeLiteral("x")),
            ];
            var statement = new CosmosPredicateLoader.Loading([CosmosPath.Root("c")], [CosmosReading.Typed], "c").Statement(conjuncts);
            statement.Conjuncts.Should().AllBeOfType<SqlPredicate.Opaque>();

            var (upper, unlisted, sibling) = (statement.Conjuncts[0], statement.Conjuncts[1], statement.Conjuncts[2]);
            var generated = Generate([upper, Not(upper), unlisted, sibling], opaques: statement.Opaques);
            generated.Run.Should().Be(Disciplined);
            generated.Implied(upper).Should().Equal(Is(Op.IsDefined, Name));
            generated.Implied(upper, RenderPosition.Neg).Should().Equal(Negated(Is(Op.IsDefined, Name)));
            generated.Implied(unlisted).Should().BeEmpty();
            generated.Answers.Select(a => a.Weakening).Should().Equal(Is(Op.IsDefined, Name), Is(Op.IsDefined, Name), null, Is(Op.IsDefined, SqlValue.Spell(LabelPath)));
        }

        /// <summary>
        /// A leaf whose best the Rex builder writes weakens to it; one whose best holds a boolean beside a text reading,
        /// which the builder does not write, weakens to what its implied forms make together.
        /// </summary>
        [Fact]
        public void ALeafWeakensToItsBestWhereThatIsWritableAndToTheFoldElsewhere()
        {
            var (unequal, truth) = (Ne(Text(LabelPath), "bikes"), Eq(Text(LabelPath), "true"));
            var generated = Generate(unequal, truth);
            generated.Weakening(unequal).Should().Be(generated.Best(unequal)!.Form!);
            generated.Best(truth)!.Form.Should().Be(Any(Compare(Op.Equal, Label, Of("true")), Compare(Op.Equal, Label, Of(true))));
            generated.Implied(truth).Should().NotContain(generated.Best(truth)!.Form!);
            generated.Weakening(truth).Should().Be(Is(Op.IsDefined, Label));
        }

        /// <summary>
        /// The sentinels are inert: rows that ask every template of every shape here propose the unspelled form beside the
        /// rest, and no agreement, best, implied form or weakening ever holds it, nor the unpriced form.
        /// </summary>
        [Fact]
        public void NoSentinelIsEverLifted()
        {
            SqlPredicate[] roots = [Ne(Text(LabelPath), "bikes"), Eq(Text(KindPath), "true"), In(Raw(CategoryPath), "a", "b"), IsNull(Text(NPath)), Cmp(Cast("DOUBLE", Text(PricePath)), ">", Lit(50))];
            var shapes = RenderingTemplateRows.Fits.Select(f => f.Shape).Distinct().ToList();
            var everything = RenderingTheory.Define(Measured with { Fits = [.. from id in System.Enum.GetValues<TemplateId>() from shape in shapes select (id, shape)] });
            var generated = Generate([.. roots, .. roots.Select(Not)], theory: everything);

            generated.Run.Should().Be(Disciplined);
            generated.Candidates.Should().Contain(f => f.Kind == ServiceFormKind.Opaque, "the unspelled form is proposed");
            generated.Agreed.Should().NotContain(a => a.Form.Kind == ServiceFormKind.Opaque);
            generated.Nodes.Values.Where(n => n.Best?.Form is { } f && RenderingCheckRun.Opaque(f)).Should().BeEmpty();
            generated.Nodes.Values.Where(n => n.Implied?.Any(RenderingCheckRun.Opaque) == true || n.Weakening is { } w && RenderingCheckRun.Opaque(w)).Should().BeEmpty();
        }

        /// <summary>
        /// Probe T4, P5 as restated: <c>(kind = 'a' AND label = 'x') OR (kind = 'c' AND n = '1')</c> beside a declared
        /// integer <c>n</c> has no exact form, and its weakening keeps arm 2's <c>c.kind = 'c'</c> beside <c>n</c>'s presence.
        /// </summary>
        [Fact]
        public void T4sWeakeningKeepsTheSecondArmsKind()
        {
            var root = Or(And(Eq(Text(KindPath), "a"), Eq(Text(LabelPath), "x")), And(Eq(Text(KindPath), "c"), Eq(Text(NPath), "1")));
            var answer = Generate([root], values: new Dictionary<JsonDocumentPath, PathValues> { [NPath] = PathValues.OfTypes(ValueSet.Of(IntegerType)) }).Answers.Single();
            answer.Best.Should().BeNull();
            answer.Weakening!.Kind.Should().Be(ServiceFormKind.Or);
            answer.Weakening.Members.Select(m => m.Members.ToHashSet()).Should().BeEquivalentTo(new[]
            {
                new HashSet<ServiceForm> { Compare(Op.Equal, Kind, Of("a")), Compare(Op.Equal, Label, Of("x")) },
                new HashSet<ServiceForm> { Compare(Op.Equal, Kind, Of("c")), Is(Op.IsDefined, N) },
            });
        }

        /// <summary>
        /// P6: <c>NOT (c.category = 'bikes' AND CAST(price AS DOUBLE) &gt; 50)</c> weakens beneath its <c>NOT</c> at
        /// <c>Neg</c>, where the cast's comparison takes C4's <c>IS_NUMBER(c.price) AND c.price &gt; 50</c>.
        /// </summary>
        [Fact]
        public void P6WeakensAtNeg()
        {
            var (category, price) = (Eq(Text(CategoryPath), "bikes"), Cmp(Cast("DOUBLE", Text(PricePath)), ">", Lit(50)));
            var root = Not(And(category, price));
            var generated = Generate(root);
            var kindRestricted = All(Is(Op.IsNumber, Price), Compare(Op.Greater, Price, Of(50m)));
            generated.Implied(price, RenderPosition.Neg).Should().Contain(kindRestricted);
            generated.Weakening(price, RenderPosition.Neg)!.Parts.Prepend(generated.Weakening(price, RenderPosition.Neg)!).Should().Contain(kindRestricted);
            var answer = generated.Answers.Single();
            answer.Best.Should().BeNull();
            var arms = ((SqlPredicate.And)((SqlPredicate.Not)root).Operand).Arms;
            answer.Weakening.Should().Be(ServiceForm.Negated(ServiceForm.Joined(Op.And, generated.Weakening(arms[0], RenderPosition.Neg)!, generated.Weakening(arms[1], RenderPosition.Neg)!)));
        }

        /// <summary>
        /// G10's variant: a bundle whose run derived a contradiction, here through a gate planted to refute every candidate
        /// it agrees with, still writes a weakening, and no read gives it: the best is a Conflict, the weakening none, and no
        /// agreement is reported.
        /// </summary>
        [Fact]
        public void AContradictedBundleReadsNoWeakening()
        {
            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "gate").DefineRule("planted: every candidate is refuted", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var o = r.Var<CellOutcomes>("o");
                r.When(theory.Candidate.Of(e, pos, f), o.Is(theory.Outcomes.Of(e, f)));
                r.Then(theory.Disagrees.Of(e, pos, f, Verdict.Equal).Is(Compute.Of(o, x => Refutation.Of(x, x.Classes))));
            });

            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(Ne(Text(LabelPath), "bikes"));
            bundle.Import(Source, LabelPath, PathValues.Anything, AllBands);
            bundle.Generate(root);
            bundle.RunGenerate(1).Should().Be(new RenderingRun(RunStopReason.Completed, true, true));
            bundle.Scope.Check(Fact.Exists(theory.Weakening.Of(root, RenderPosition.Pos))).Should().BeTrue("the run wrote one");
            var answer = bundle.ReadBest(root);
            (answer.Kind, answer.Best, answer.Weakening).Should().Be((RenderingVerdictKind.Conflict, (ServiceForm?)null, (ServiceForm?)null));
            answer.Reason.Should().StartWith("agreed and refuted");
            bundle.ReadWeakening(root).Should().BeNull();
            bundle.Agreements(root, RenderPosition.Pos).Should().BeEmpty();
        }

        /// <summary>
        /// The tripwire: two terms beside a root, which the bundle's scope is asked to make one, leave a class with two
        /// nodes, and no read gives an answer.
        /// </summary>
        [Fact]
        public void AMergedBundleReadsNothing()
        {
            using var bundle = ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(Ne(Text(LabelPath), "bikes"));
            bundle.Scope.Union(bundle.Write(Eq(Text(KindPath), "a")), bundle.Write(Eq(Text(KindPath), "b")));
            bundle.Import(Source, LabelPath, PathValues.Anything, AllBands);
            bundle.Generate(root);
            bundle.RunGenerate(1).OneNodePerClass.Should().BeFalse();
            var answer = bundle.ReadBest(root);
            (answer.Kind, answer.Best, answer.Weakening, answer.Reason).Should().Be((RenderingVerdictKind.Conflict, (ServiceForm?)null, (ServiceForm?)null, "tripwire: a rule merged two terms"));
            bundle.ReadWeakening(root).Should().BeNull();
        }

        /// <summary>
        /// Every check the pull request's run made, asked again of generate mode under the asserted set and the one beside
        /// it, riding its pass: each conjunct a root, and beside them their junction where there are two or more, imported
        /// and stated as the check was. Every run completes within its limits with one node per class and no
        /// contradiction, declines on nothing, and no best, implied form or weakening is a sentinel. Counts and each
        /// bundle's time, open to dispose, are reported.
        /// </summary>
        [Fact]
        public void EveryGenerateRunCompletes()
        {
            var claims = RenderingCheckRun.PerPullRequest.Value.Memo.Entries.Select(e => e.What).OfType<RenderingMemo.Claimed>().ToList();
            claims.Should().NotBeEmpty();
            var faults = new ConcurrentQueue<string>();
            foreach (var set in new[] { RenderingCheckRun.AssertedSet, RenderingCheckRun.BesideSet })
            {
                var (times, counts) = (new ConcurrentBag<double>(), new int[3]);
                Parallel.ForEach(claims, new ParallelOptions { MaxDegreeOfParallelism = 2 }, claim =>
                {
                    var started = Stopwatch.GetTimestamp();
                    var roots = claim.Conjuncts.Count > 1 ? [.. claim.Conjuncts, SqlPredicate.AllOf(claim.Conjuncts)] : claim.Conjuncts.ToList();
                    using (var bundle = ProbeTheory.Open(set))
                    {
                        var terms = roots.Select(bundle.Write).ToList();
                        RenderingCheckRun.Restate(bundle, claim);
                        terms.ForEach(bundle.Generate);
                        var run = bundle.RunGenerate(RenderingBundle.WidthOf(roots));
                        var answers = terms.Select(bundle.ReadBest).ToList();
                        var lifted = bundle.Failure is null ? bundle.Scope.Rows(ProbeTheory.BestExact).Select(r => ((Priced)r.Output!).Form).OfType<ServiceForm>()
                            .Concat(bundle.Scope.Rows(ProbeTheory.Implied).SelectMany(r => (ValueSet<ServiceForm>)r.Output!))
                            .Concat(bundle.Scope.Rows(ProbeTheory.Weakening).Select(r => (ServiceForm)r.Output!)).ToList() : [];
                        if (run != Disciplined || bundle.Failure is not null || answers.Any(a => a.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict) || lifted.Any(RenderingCheckRun.Opaque))
                            faults.Enqueue($"{set} {claim.Form}: {run}; {bundle.Failure?.Message}; {string.Join(", ", answers.Select(a => a.Kind + " " + a.Reason))}");

                        Interlocked.Add(ref counts[0], roots.Count);
                        Interlocked.Add(ref counts[1], answers.Count(a => a.Kind == RenderingVerdictKind.Proven));
                        Interlocked.Add(ref counts[2], answers.Count(a => a.Weakening is not null));
                    }

                    times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                });

                var ms = times.Order().ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: {ms.Count} bundles, {counts[0]} roots, {counts[1]} proven, {counts[2]} weakened; " +
                    $"a bundle p50 {ms[ms.Count / 2]:N2} ms, p90 {ms[ms.Count * 9 / 10]:N2} ms, max {ms[^1]:N1} ms; total {ms.Sum() / 1000:N2} s");
            }

            faults.Should().BeEmpty();
        }

    }

}
