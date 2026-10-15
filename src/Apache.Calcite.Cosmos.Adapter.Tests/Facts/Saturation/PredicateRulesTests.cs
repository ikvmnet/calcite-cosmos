using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The predicate rules, family by family, through the engine: what a predicate that holds establishes, the split over
    /// a junction's arms, and the verdicts with their connectives.
    /// </summary>
    /// <remarks>
    /// Each test asks through the oracle's facade, with every conjunct admitted where it is about what conjuncts establish
    /// and nothing admitted where it is about what the declaration decides, so that what is pinned is what a planner would
    /// read.
    /// </remarks>
    public sealed class PredicateRulesTests
    {

        static readonly JsonDocumentPath A = P("a");
        static readonly JsonDocumentPath B = P("b");
        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Ref = P("ref");
        static readonly JsonDocumentPath Note = P("note");
        static readonly JsonDocumentPath Label = P("label");
        static readonly JsonDocumentPath N = P("n");
        static readonly JsonDocumentPath Status = P("status");

        static bool Knows(JsonFactSet facts, JsonDocumentPath path, JsonClaim claim) => facts.Knows(new JsonFact(path, claim));

        /// <summary>
        /// Kinds discriminated by <c>kind</c>: an order and an invoice each declare <c>ref</c> a present integer, and a note
        /// says nothing of it. The integration plan's gap 1.
        /// </summary>
        static JsonFactTheory Kinds() => Theory(
            Outright(Kind, Present),
            Outright(Kind, Enum("order", "invoice", "note")),
            When(Kind, "order", Ref, Present),
            When(Kind, "order", Ref, Type(JsonType.Integer)),
            When(Kind, "invoice", Ref, Present),
            When(Kind, "invoice", Ref, Type(JsonType.Integer)));

        /// <summary>
        /// The invariants a reader can check without running anything: no term the oracle makes can be extracted for
        /// execution, and the rules stay within the plan's budget.
        /// </summary>
        [Fact]
        public void NothingIsExtractableAndTheRulesStayWithinTheirBudget()
        {
            var rules = PredicateRules.Of(new JsonContexts());

            new Eggnog.ConstructorHandle[] { rules.Text, rules.Raw, rules.Typed, rules.Cast, rules.Literal, rules.Parameter, rules.Compare, rules.In, rules.IsNull, rules.And, rules.Or, rules.Not, rules.Opaque }
                .Should().OnlyContain(constructor => constructor.Extractable == false);

            (rules.Analyse.Rules.Count + rules.Establish.Rules.Count + rules.Decide.Rules.Count).Should().Be(45);
            rules.Decide.Rules.Count(rule => rule.Name.Contains("conjunction") || rule.Name.Contains("disjunction") || rule.Name.StartsWith("not of") || rule.Name.StartsWith("what is always")).Should().Be(12);
        }

        // What a predicate that holds establishes.

        [Fact]
        public void AConjunctionThatHoldsEstablishesBothSides()
        {
            var facts = Established(Theory(), And(Eq(Text(A), "x"), Eq(Text(B), "y")));

            Knows(facts, A, Value("x")).Should().BeTrue();
            Knows(facts, B, Value("y")).Should().BeTrue();
        }

        [Fact]
        public void ADisjunctionThatFailsEstablishesThatBothSidesFail()
        {
            var facts = Established(Theory(), Not(Or(Eq(Text(A), "x"), Eq(Text(B), "y"))));

            Knows(facts, A, Present).Should().BeTrue("FALSE is not UNKNOWN, so the accessor answered");
            Knows(facts, A, new JsonClaim.NotEqualTo("x")).Should().BeTrue();
            Knows(facts, B, new JsonClaim.NotEqualTo("y")).Should().BeTrue();
        }

        [Fact]
        public void ANegationThatHoldsEstablishesWhatItsOperandFailingSays()
        {
            var facts = Established(Theory(), Not(Eq(Text(A), "x")));

            Knows(facts, A, Present).Should().BeTrue();
            Knows(facts, A, new JsonClaim.NotEqualTo("x")).Should().BeTrue();
            Knows(facts, A, new JsonClaim.NotEqualTo(JsonScalar.Null)).Should().BeTrue("the accessor answers SQL null for a JSON null, which compares unknown");
        }

        /// <summary>
        /// The rendering preimage: a stored number renders as its digits, so an equality with them keeps a document storing
        /// the number as well as one storing the string, and establishes both and nothing narrower. #117's fact, which a
        /// conjunct used to certify itself exact with, is not among them.
        /// </summary>
        [Fact]
        public void AnEqualityWithTextANumberRendersAsEstablishesTheStringOrTheNumber()
        {
            var facts = Established(Theory(), Eq(Text(Label), "30"));

            Knows(facts, Label, Present).Should().BeTrue();
            Knows(facts, Label, Enum("30", 30m)).Should().BeTrue();
            Knows(facts, Label, Value("30")).Should().BeFalse();
            Knows(facts, Label, Type(JsonType.String, orNull: true)).Should().BeFalse();
        }

        /// <summary>
        /// A declared string meets the preimage and leaves the string alone, which deletes the stored-number disjunct
        /// (<c>TODO.md</c>, under <em>The numeric forms stop at whole numbers</em>).
        /// </summary>
        [Fact]
        public void ADeclaredStringLeavesTheStringMemberOfThePreimage()
        {
            var facts = Established(Theory(Outright(Label, Type(JsonType.String))), Eq(Text(Label), "30"));

            Knows(facts, Label, Value("30")).Should().BeTrue();
        }

        [Fact]
        public void TextOnlyAStringRendersAsEstablishesTheString()
        {
            Knows(Established(Theory(), Eq(Text(A), "bikes")), A, Value("bikes")).Should().BeTrue();
            Knows(Established(Theory(), Eq(Text(A), "true")), A, Enum("true", true)).Should().BeTrue();
        }

        [Fact]
        public void AMembershipEstablishesTheUnionOfItsPreimages()
        {
            var facts = Established(Theory(), In(Text(A), "x", "30"));

            Knows(facts, A, Enum("x", "30", 30m)).Should().BeTrue();
            Knows(facts, A, Present).Should().BeTrue();
        }

        [Fact]
        public void ANumericCastPutsTheNumberInAWindow()
        {
            var facts = Established(Theory(), Cmp(Cast("INTEGER", Text(N)), ">", Lit(500m)));

            Knows(facts, N, new JsonClaim.LowerBound(499m, Exclusive: true)).Should().BeTrue();
            Knows(facts, N, Type(JsonType.Number)).Should().BeFalse("the cast converts a stored string too");
        }

        /// <summary>
        /// Gap 4, through the window: a range beyond a declared maximum leaves an integer nothing.
        /// </summary>
        [Fact]
        public void ARangeBeyondADeclaredMaximumKeepsNothing()
        {
            var theory = Theory(Outright(N, Type(JsonType.Integer)), Outright(N, new JsonClaim.UpperBound(100m)));

            Established(theory, Cmp(Cast("INTEGER", Text(N)), ">", Lit(500m))).IsContradictory.Should().BeTrue();
            Established(theory, Cmp(Cast("INTEGER", Text(N)), ">", Lit(50m))).IsContradictory.Should().BeFalse();
        }

        /// <summary>
        /// F10: a stored -1e30 converts to the lower limit, which satisfies the comparison, so nothing bounds that side.
        /// </summary>
        [Fact]
        public void ALimitTheComparisonHoldsOfBoundsNothingOnThatSide()
        {
            var facts = Established(Theory(), Cmp(Cast("INTEGER", Text(N)), ">", Lit(-3000000000m)));

            Knows(facts, N, new JsonClaim.LowerBound(-3000000001m, Exclusive: true)).Should().BeFalse();
            Knows(facts, N, Present).Should().BeTrue();
        }

        [Fact]
        public void ANullTestSaysWhatTheAccessorReadsAsNull()
        {
            var notNull = Established(Theory(), IsNotNull(Text(A)));
            Knows(notNull, A, Present).Should().BeTrue();
            Knows(notNull, A, new JsonClaim.NotEqualTo(JsonScalar.Null)).Should().BeTrue();

            // Where the accessor is null the path holds no scalar, so no equality over it keeps a row.
            Admitted(Theory(), IsNull(Text(A)), Eq(Text(A), "x")).IsContradictory(OracleLicence.Select).Should().BeTrue();
        }

        [Fact]
        public void ARawEqualityEstablishesTheValue()
        {
            Knows(Established(Theory(), Eq(Raw(A), "bikes")), A, Value("bikes")).Should().BeTrue();
        }

        [Fact]
        public void ATypedEqualityEstablishesAValueOfItsType()
        {
            Knows(Established(Theory(), Eq(Typed(N, "INTEGER"), 30m)), N, Value(30m)).Should().BeTrue();
            Established(Theory(), Eq(Typed(N, "INTEGER"), 30.5m)).IsContradictory.Should().BeTrue("an INTEGER is never 30.5");
        }

        /// <summary>
        /// <c>RETURNING DECIMAL</c> parses the rendering, so a row an equality through it keeps may store the string
        /// <c>"30"</c>: it is not established to hold a number, only one near the literal if it holds one.
        /// </summary>
        [Fact]
        public void ADecimalReturningEstablishesNoNumber()
        {
            var facts = Established(Theory(), Eq(Typed(N, "DECIMAL"), 30m));

            Knows(facts, N, Present).Should().BeTrue();
            Knows(facts, N, Type(JsonType.Number)).Should().BeFalse("a stored \"30\" parses as 30");
            Knows(facts, N, new JsonClaim.LowerBound(29m, Exclusive: true)).Should().BeTrue();
        }

        [Fact]
        public void AnOrderingOverATypedPathStatesNoBound()
        {
            var facts = Established(Theory(), Cmp(Typed(N, "INTEGER"), ">", Lit(500m)));

            Knows(facts, N, Present).Should().BeTrue();
            Knows(facts, N, Type(JsonType.Integer)).Should().BeTrue("RETURNING asserts the type");
            Knows(facts, N, new JsonClaim.LowerBound(499m, Exclusive: true)).Should().BeFalse("what RETURNING does at the comparison is unmeasured");
        }

        [Fact]
        public void AComparisonWithAParameterSaysThePathIsThere()
        {
            var facts = Established(Theory(), Cmp(Text(A), "=", Param(0)));

            Knows(facts, A, Present).Should().BeTrue();
            Knows(facts, A, new JsonClaim.NotEqualTo(JsonScalar.Null)).Should().BeTrue();
        }

        [Fact]
        public void APredicateIsNotBothTrueAndFalseOfARow()
        {
            Established(Theory(), And(Opaque(0), Not(Opaque(0)))).IsContradictory.Should().BeTrue();
            Established(Theory(), And(Opaque(0), Not(Opaque(1)))).IsContradictory.Should().BeFalse();
        }

        [Fact]
        public void AnOpaquePredicateProvesNothing()
        {
            var facts = Established(Theory(), Opaque(0));

            facts.IsContradictory.Should().BeFalse();
            Knows(facts, A, Present).Should().BeFalse();
        }

        // The split over a junction's arms.

        /// <summary>
        /// Gap 1 spelled per kind: each arm names its own kind, and what both kinds declare holds of every row the
        /// disjunction keeps. No single arm proves it, and nothing finer than the lift does.
        /// </summary>
        [Fact]
        public void ADisjunctionSpelledPerKindProvesWhatEveryArmProves()
        {
            var scope = Admitted(Kinds(), Or(And(Eq(Text(Kind), "order"), Eq(Text(Note), "a")), And(Eq(Text(Kind), "invoice"), Eq(Text(Note), "b"))));
            var facts = scope.Facts(OracleLicence.Select);

            Knows(facts, Ref, Present).Should().BeTrue();
            Knows(facts, Ref, Type(JsonType.Integer)).Should().BeTrue();
            Knows(facts, Note, Enum("a", "b")).Should().BeTrue();
            scope.Reports.Sum(r => r.CasesOpened).Should().BeGreaterThan(0);
        }

        [Fact]
        public void ADisjunctionWithAnArmThatDeclaresNothingProvesNothingOfIt()
        {
            var facts = Established(Kinds(), Or(And(Eq(Text(Kind), "order"), Eq(Text(Note), "a")), And(Eq(Text(Kind), "note"), Eq(Text(Note), "b"))));

            Knows(facts, Ref, Present).Should().BeFalse("a note says nothing of ref");
        }

        [Fact]
        public void AnArmTheFactsContradictDropsOutOfTheLift()
        {
            var theory = Theory(Outright(Status, Present), Outright(Status, Enum("active", "archived")));

            Knows(Established(theory, Or(Eq(Text(Status), "purged"), Eq(Text(A), "x"))), A, Value("x")).Should().BeTrue(
                "no document is purged, so every row the disjunction keeps is kept by its other arm");
        }

        [Fact]
        public void ADisjunctionEveryArmOfWhichIsContradictedKeepsNothing()
        {
            var theory = Theory(Outright(Status, Present), Outright(Status, Enum("active", "archived")));

            Established(theory, Or(Eq(Text(Status), "purged"), And(Eq(Text(Status), "active"), Eq(Text(Status), "archived")))).IsContradictory.Should().BeTrue();
        }

        [Fact]
        public void AConjunctionThatFailsSplitsOverItsArms()
        {
            // NOT (a = 'x' AND a = 'y'): a is not x, or not y, and either way it is there and a scalar.
            var facts = Established(Theory(), Not(And(Eq(Text(A), "x"), Eq(Text(A), "y"))));

            Knows(facts, A, Present).Should().BeTrue();
        }

        // The verdicts.

        [Fact]
        public void AConstantThatIsAlwaysThereMakesItsEqualityAlwaysTrue()
        {
            var always = Declared(Theory(Outright(Kind, Present), Outright(Kind, Value("A"))));
            var sometimes = Declared(Theory(Outright(Kind, Value("A"))));

            always.Decide(Eq(Text(Kind), "A"), OracleLicence.Residual).Should().Be(Truth.Always);
            sometimes.Decide(Eq(Text(Kind), "A"), OracleLicence.Residual).Should().Be(Truth.Unknown, "a document without kind keeps no row for it");
        }

        [Fact]
        public void AMembershipCoveringTheEnumIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));

            scope.Decide(In(Text(Kind), "a", "b", "c"), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(In(Text(Kind), "a"), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(In(Text(Kind), "c", "d"), OracleLicence.Residual).Should().Be(Truth.Never);
        }

        [Fact]
        public void ADisequalityWithAValueOutsideTheEnumIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));

            scope.Decide(Ne(Text(Kind), "c"), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(Ne(Text(Kind), "a"), OracleLicence.Residual).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void IsNotNullOfARequiredStringIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(A, Present), Outright(A, Type(JsonType.String))));

            scope.Decide(IsNotNull(Text(A)), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(IsNull(Text(A)), OracleLicence.Residual).Should().Be(Truth.Never);
        }

        /// <summary>
        /// A range the declared bounds satisfy is a tautology of the number, converted: through a cast of the raw value, or a
        /// double's parse of the rendering. An integer's parse of the rendering raises on a stored <c>30.0</c> or
        /// <c>3000000000</c>, which the declaration admits, so there it is decided neither way.
        /// </summary>
        [Fact]
        public void ARangeTheDeclaredBoundsSatisfyIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(N, Present), Outright(N, Type(JsonType.Integer)), Outright(N, new JsonClaim.LowerBound(0m))));

            scope.Decide(Cmp(Cast("INTEGER", Raw(N)), ">=", Lit(0m)), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(Cmp(Cast("INTEGER", Raw(N)), ">", Lit(0m)), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(Cmp(Cast("INTEGER", Raw(N)), "<", Lit(0m)), OracleLicence.Residual).Should().Be(Truth.Never);
            scope.Decide(Cmp(Cast("DOUBLE", Text(N)), ">=", Lit(0m)), OracleLicence.Residual).Should().Be(Truth.Always);

            scope.Decide(Cmp(Cast("INTEGER", Text(N)), ">=", Lit(0m)), OracleLicence.Residual).Should().Be(Truth.Unknown, "Calcite raises on a stored 30.0");
            scope.Decide(Cmp(Cast("INTEGER", Text(N)), "<", Lit(0m)), OracleLicence.Residual).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AValueOutsideTheEnumIsAlwaysFalseAndItsNegationAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));

            scope.Judge(Eq(Text(Kind), "z"), OracleLicence.Residual).AlwaysFalse.Should().BeTrue();
            scope.Decide(Not(Eq(Text(Kind), "z")), OracleLicence.Residual).Should().Be(Truth.Always);
        }

        /// <summary>
        /// UNKNOWN is not FALSE under NOT. A comparison over an absent path keeps no row, and neither does its negation; a
        /// rule reading NeverTrue beneath a NOT would delete the negation as a tautology and keep every row.
        /// </summary>
        [Fact]
        public void AComparisonOverAnAbsentPathKeepsNoRowAndNeitherDoesItsNegation()
        {
            // a is a string wherever it is there, so a.b is never there.
            var scope = Declared(Theory(Outright(A, Type(JsonType.String))));
            var beneath = Eq(Text(P("a", "b")), "x");

            var verdict = scope.Judge(beneath, OracleLicence.Residual);
            verdict.NeverTrue.Should().BeTrue();
            verdict.AlwaysFalse.Should().BeFalse();
            scope.Decide(Not(beneath), OracleLicence.Residual).Should().NotBe(Truth.Always);
        }

        /// <summary>
        /// The accessor answers SQL null for an object: a comparison over one is UNKNOWN, and <c>IS NOT NULL</c> is FALSE —
        /// the soundness review's root cause A, decided rightly.
        /// </summary>
        [Fact]
        public void TheAccessorOverAnObjectIsNull()
        {
            var scope = Declared(Theory(Outright(A, Present), Outright(A, Type(JsonType.Object))));

            scope.Decide(IsNotNull(Text(A)), OracleLicence.Residual).Should().Be(Truth.Never);
            scope.Decide(IsNull(Text(A)), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Judge(Eq(Text(A), "x"), OracleLicence.Residual).AlwaysFalse.Should().BeFalse();
            scope.Decide(Not(Eq(Text(A), "x")), OracleLicence.Residual).Should().NotBe(Truth.Always);
        }

        [Fact]
        public void TheConnectives()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));
            var always = In(Text(Kind), "a", "b");
            var never = Eq(Text(Kind), "z");
            var unknown = Eq(Text(Kind), "a");

            scope.Decide(And(always, always), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(And(always, unknown), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(And(unknown, never), OracleLicence.Residual).Should().Be(Truth.Never);
            scope.Decide(Or(unknown, always), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(Or(unknown, never), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(Or(never, Not(always)), OracleLicence.Residual).Should().Be(Truth.Never);
            scope.Decide(Not(Or(never, Not(always))), OracleLicence.Residual).Should().Be(Truth.Always, "both sides are FALSE, not UNKNOWN");
            scope.Decide(Not(And(always, never)), OracleLicence.Residual).Should().Be(Truth.Always);
        }

        [Fact]
        public void AnOpaqueSideDecidesNothingOnItsOwn()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));
            var always = In(Text(Kind), "a", "b");
            var never = Eq(Text(Kind), "z");

            scope.Decide(Opaque(0), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(Or(Opaque(0), always), OracleLicence.Residual).Should().Be(Truth.Always);
            scope.Decide(And(Opaque(0), always), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(And(Opaque(0), never), OracleLicence.Residual).Should().Be(Truth.Never);
            scope.Decide(Not(Opaque(0)), OracleLicence.Residual).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void ARawNumberIsNotDecidedAlwaysEqualAcrossNumericTypes()
        {
            var scope = Declared(Theory(Outright(N, Present), Outright(N, Value(30m)), Outright(A, Present), Outright(A, Value("x"))));

            scope.Decide(Eq(Raw(N), 30m), OracleLicence.Residual).Should().Be(Truth.Unknown);
            scope.Decide(Eq(Raw(N), 31m), OracleLicence.Residual).Should().Be(Truth.Never);
            scope.Decide(Eq(Raw(A), "x"), OracleLicence.Residual).Should().Be(Truth.Always);
        }

        [Fact]
        public void AContradictionKeepsNothingWhateverIsAsked()
        {
            var scope = Admitted(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))), Eq(Text(Kind), "z"));

            scope.IsContradictory(OracleLicence.Select).Should().BeTrue();
            scope.Decide(Eq(Text(A), "anything"), OracleLicence.Select).Should().Be(Truth.Never);
            scope.Decide(Not(Eq(Text(A), "anything")), OracleLicence.Select).Should().Be(Truth.Never);
        }

        [Fact]
        public void AVerdictReachedBeneathTheFactsThatDecideItHoldsThere()
        {
            // kind is not declared; the conjunct kind = 'a' decides kind IN ('a', 'b') in the rows it keeps.
            var scope = Admitted(Theory(), Eq(Text(Kind), "a"));

            scope.Decide(In(Text(Kind), "a", "b"), OracleLicence.Select).Should().Be(Truth.Always);
            scope.Decide(In(Text(Kind), "a", "b"), OracleLicence.Residual).Should().Be(Truth.Unknown);
        }

    }

}
