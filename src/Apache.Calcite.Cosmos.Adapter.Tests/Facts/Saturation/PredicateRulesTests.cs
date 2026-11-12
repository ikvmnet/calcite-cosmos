using System.Linq;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Adapter.Metadata;
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
            var rules = PredicateRules.Of(new JsonContexts(CosmosReasoning.Rows));

            new Eggnog.ConstructorHandle[] { rules.Ir.Text, rules.Ir.Raw, rules.Ir.Typed, rules.Ir.Cast, rules.Ir.Box, rules.Ir.Query, rules.Ir.Apart, rules.Ir.Null, rules.Ir.Literal, rules.Ir.Parameter, rules.Ir.Compare, rules.Ir.In, rules.Ir.IsNull, rules.Ir.And, rules.Ir.Or, rules.Ir.Not, rules.Ir.Opaque, rules.Ir.TypeTest, rules.Ir.Case, rules.Ir.Affix, rules.Ir.Truth }
                .Should().OnlyContain(constructor => constructor.Extractable == false);

            (rules.Analyse.Rules.Count + rules.Establish.Rules.Count + rules.Decide.Rules.Count).Should().Be(80,
                "the vocabulary's thirteen of G8, which both graphs read since h15, h15b's ten of the element and the readings written apart among them, "
                + "each accessor's reading, of an element as a path's where an index reads it so and as the element reading where not, "
                + "the gate that assumes what a gated node gates where the WHERE raises on no row, "
                + "and the domain an atom's preimage keeps among the types its path may hold, and among its strings their characters");
            rules.Establish.Rules.Count(rule => rule.Name == "an assumption the WHERE gates holds where the WHERE raises on no row").Should().Be(1);
            rules.Decide.Rules.Count(rule => rule.Name.Contains("conjunction") || rule.Name.Contains("disjunction") || rule.Name.StartsWith("not of") || rule.Name.StartsWith("what is always")).Should().Be(12);
            rules.Exactness.Rules.Count.Should().Be(9, "an atom read otherwise than Calcite, and each side of each connective holding one (h15)");
            rules.RaiseRules.Rules.Select(rule => rule.Name).Should().BeEquivalentTo(new[]
            {
                "an atom that raises on nothing its path may hold never raises",
                "a truth value never raises",
                "a negation raises nowhere its operand raises nowhere",
                "a conjunction raises nowhere both its sides raise nowhere",
                "a disjunction raises nowhere both its sides raise nowhere",
                "a CASE raises nowhere its condition and both its arms raise nowhere",
            }, "an atom, a truth value and each connective; an opaque term or any other constructor may raise");

            // What a gated node assumes, and what it judged before assuming anything, are that node's alone, as what a
            // case chose is: none is lifted.
            new Eggnog.TableHandle[] { rules.Gated, rules.NeverRaises, rules.Holds }.Should().OnlyContain(table => table.Lift == false);
        }

        // The reduce's rules, in a node gated by hand as the reduce's is: what the WHERE assumes holds only where the
        // stage before anything is assumed found that the WHERE raises on no row.

        /// <summary>
        /// Opens a child of the declaration's <c>D0</c> holding the WHERE's term, mentioning the paths it reads, asserts
        /// <c>gated(W, W)</c> where <paramref name="gated"/> is set and no <c>holds</c> row, runs
        /// <see cref="PredicateRules.Reduction"/>, and returns whether it is contradictory and whether the WHERE never raises.
        /// </summary>
        static (bool Contradictory, bool NeverRaises) Reduced(JsonFactTheory theory, SqlPredicate where, bool gated = true)
        {
            var terms = Declared(theory, where).Terms;
            var rules = terms.Rules!;
            var json = rules.Json;
            var writer = new PredicateWriter(rules.Ir, path => JsonFactEncoding.Path(path).ToTerm(json), (junction, truth, arms) =>
                new Eggnog.Fact[] { rules.Arms.Of(junction, truth).Is(Eggnog.ValueSet.From(Enumerable.Range(0, arms.Count).Select(m => (long)m))) }
                    .Concat(arms.Select((arm, m) => (Eggnog.Fact)rules.Arm.Of(junction, truth, (long)m, arm))));

            using var scope = terms.Root.Scope!.CreateChild();
            var specs = where.Paths.Select(JsonFactEncoding.Path).ToArray();
            JsonContexts.Mention(json, scope, specs);

            var facts = specs.Select(spec => (Eggnog.Fact)json.Mentioned.Of(spec.ToTerm(json))).ToList();
            var w = writer.Term(where, facts);
            scope.Make(w);
            if (gated)
                facts.Add(rules.Gated.Of(w, w));

            scope.Assume(facts.ToArray());
            var report = scope.Run(rules.Reduction, new Eggnog.RunOptions { StopOnContradiction = true });
            var contradictory = report.StopReason == Eggnog.RunStopReason.Contradiction || scope.IsContradictory;
            var never = contradictory == false && scope.Check(rules.NeverRaises.Of(w));
            System.GC.KeepAlive(terms);
            return (contradictory, never);
        }

        /// <summary>
        /// The gate fires only where the WHERE raises on no document the declaration admits: a text comparison, its
        /// negation and a CASE of such raise nowhere, so a contradiction stands; the first design's counterexample, a cast
        /// to <c>DOUBLE</c> of a path that may hold a string, may raise, and so may an opaque term, so the node assumes
        /// nothing and is not contradictory, while the node that assumes every conjunct still is. Ungated, a node assumes
        /// nothing whatever the WHERE says.
        /// </summary>
        [Fact]
        public void AGatedNodeAssumesTheWhereOnlyWhereItRaisesOnNoRow()
        {
            var kindA = Theory(Outright(Kind, Present), Outright(Kind, Enum("a")), Outright(N, Present), Outright(N, Enum(30m, "x")));
            var text = Eq(Text(Kind), "z");

            Reduced(kindA, text, gated: false).Should().Be((false, true), "an ungated node assumes nothing, and a text comparison raises on no row");
            Reduced(kindA, text).Contradictory.Should().BeTrue("the gate assumes the WHERE, which no document the declaration admits makes TRUE");
            Reduced(kindA, Not(Eq(Text(Kind), "a"))).Contradictory.Should().BeTrue("a negation raises nowhere its operand raises nowhere");
            Reduced(kindA, Or(text, Eq(Text(Kind), "y"))).Contradictory.Should().BeTrue("a disjunction raises nowhere both its sides raise nowhere");

            var guarded = new SqlPredicate.Case(Eq(Text(Kind), "a"), text, new SqlPredicate.Constant(false));
            Reduced(kindA, guarded, gated: false).NeverRaises.Should().BeTrue("a CASE of text comparisons and a truth value raises nowhere");

            var cast = And(Cmp(Cast("DOUBLE", Text(N)), ">", Lit(0m)), text);
            Declared(kindA, cast).IsContradictory(OracleLicence.Kept).Should().BeTrue("assuming every conjunct contradicts the declaration");
            Reduced(kindA, cast).Should().Be((false, false), "the cast raises on the string n may hold, so the gate does not fire");

            var opaque = And(Opaque(1), text);
            Declared(kindA, opaque).IsContradictory(OracleLicence.Kept).Should().BeTrue();
            Reduced(kindA, opaque).Should().Be((false, false), "no rule says an opaque term never raises");
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
        /// The rendering preimage: the service holds a number as its double (<c>Service.NumbersBeyondLongCompareAsDoubles</c>)
        /// and returns a whole one within a long as its digits (<c>Service.IntegralNumbersReadBackAsHeld</c>), so an equality
        /// with them keeps a document storing any number whose double is theirs as well as one storing the string, and
        /// establishes the numbers' window and nothing narrower. #117's fact, which a conjunct used to certify itself exact with, is not among them.
        /// </summary>
        [Fact]
        public void AnEqualityWithTextANumberRendersAsEstablishesTheStringOrTheNumber()
        {
            var facts = Established(Theory(), Eq(Text(Label), "30"));

            Knows(facts, Label, Present).Should().BeTrue();
            Knows(facts, Label, new JsonClaim.LowerBound(29m, Exclusive: true)).Should().BeTrue();
            Knows(facts, Label, new JsonClaim.UpperBound(31m, Exclusive: true)).Should().BeTrue();
            Knows(facts, Label, new JsonClaim.LowerBound(30m)).Should().BeFalse("29.9999999999999999's double is 30");
            Knows(facts, Label, Enum("30", 30m)).Should().BeFalse("30.000000000000001's double is 30, which the service returns as 30");
            Knows(facts, Label, Value("30")).Should().BeFalse();
            Knows(facts, Label, Type(JsonType.String, orNull: true)).Should().BeFalse();
        }

        /// <summary>
        /// A declared string meets the preimage and leaves the string alone, which deletes the stored-number disjunct
        /// (<c>TODO.md</c>, under <em>The numeric forms stop at whole numbers</em>): the preimage's cases are met with the
        /// types before they are joined, so the numbers' window takes nothing from the string's value.
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

        /// <summary>
        /// A membership keeps the union of its points' preimages: a window of numbers beside the strings, which names no
        /// domain, and the strings themselves where the path holds no number.
        /// </summary>
        [Fact]
        public void AMembershipEstablishesTheUnionOfItsPreimages()
        {
            var facts = Established(Theory(), In(Text(A), "x", "30"));

            Knows(facts, A, Present).Should().BeTrue();
            Knows(facts, A, new JsonClaim.LowerBound(29m, Exclusive: true)).Should().BeTrue();
            Knows(facts, A, new JsonClaim.UpperBound(31m, Exclusive: true)).Should().BeTrue();
            Knows(facts, A, Enum("x", "30", 30m)).Should().BeFalse("30.000000000000001 is returned as 30");

            var declared = Established(Theory(Outright(A, Type(JsonType.String))), In(Text(A), "x", "30"));
            Knows(declared, A, Enum("x", "30")).Should().BeTrue();
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

        /// <summary>
        /// The column a typed <c>INTEGER</c> stands for reads a number written with a point as its double where that is
        /// whole, so <c>30.000000000000001</c> is equal to 30 there: an equality establishes a number within the literal's
        /// rounding, not the literal itself; and beside a fraction, which no integer equals, it keeps nothing.
        /// </summary>
        [Fact]
        public void ATypedEqualityEstablishesANumberRoundingToItsLiteral()
        {
            var facts = Established(Theory(), Eq(Typed(N, "INTEGER"), 30m));

            Knows(facts, N, Type(JsonType.Number)).Should().BeTrue();
            Knows(facts, N, new JsonClaim.LowerBound(29m, Exclusive: true)).Should().BeTrue();
            Knows(facts, N, new JsonClaim.UpperBound(31m, Exclusive: true)).Should().BeTrue();
            Knows(facts, N, Value(30m)).Should().BeFalse("a stored 30.000000000000001 is read as 30");
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
            Knows(facts, N, Type(JsonType.Number)).Should().BeTrue("RETURNING and the column read a number alone");
            Knows(facts, N, Type(JsonType.Integer)).Should().BeFalse("the column reads a number written with a point whose double is whole");
            Knows(facts, N, new JsonClaim.LowerBound(499m, Exclusive: true)).Should().BeFalse("what RETURNING does at the comparison is unmeasured");
        }

        /// <summary>
        /// What the statement keeps (<see cref="OracleLicence.Kept"/>) holds every row its readings may keep, so a declaration
        /// that admits such a row is no contradiction: <c>RETURNING DOUBLE</c> compares doubles, and 0.1's is
        /// 0.10000000000000001's; a <c>VARIANT</c> casts a number to text as null; an <c>ANY</c> renders
        /// 0.10000000000000001 as its double's spelling, <c>0.1</c>; and an <c>INTEGER</c> column reads 6.0000000000000001
        /// as 6, which is more than 5. Beside each, a statement whose readings keep no row the declaration admits still
        /// contradicts it.
        /// </summary>
        /// <remarks>
        /// The reduce (<see cref="OracleLicence.Reduce"/>) narrows by the same <c>Kept</c> once its gate assumes the WHERE,
        /// so none of the four is reduced either, and each control that raises nowhere is. Over the <c>INTEGER</c> column,
        /// <c>= 7</c> is not, since its number cells may raise: the gate does not fire, though what the statement keeps
        /// contradicts.
        /// </remarks>
        [Fact]
        public void WhatAStatementKeepsHoldsEveryRowItsReadingsMayKeep()
        {
            static bool Contradicts(JsonFactTheory theory, SqlPredicate conjunct, OracleLicence licence) => Declared(theory, conjunct).IsContradictory(licence);

            foreach (var licence in new[] { OracleLicence.Kept, OracleLicence.Reduce })
            {
                var tenth = Theory(Outright(A, Enum(0.1m, 0.25m)));
                Contradicts(tenth, Eq(Typed(A, "DOUBLE"), 0.10000000000000001m), licence).Should().BeFalse("0.1 and 0.10000000000000001 are one double, under {0}", licence.Question);
                Contradicts(tenth, Eq(Typed(A, "DOUBLE"), 0.3m), licence).Should().BeTrue("no double of the enum is 0.3's, under {0}", licence.Question);

                var category = Theory(Outright(Kind, Present), Outright(Kind, Type(JsonType.Integer)));
                Contradicts(category, IsNull(Cast("VARCHAR", new SqlValue.Raw(Kind) { Column = "VARIANT" })), licence).Should().BeFalse("a VARIANT casts a number to text as null, under {0}", licence.Question);
                Contradicts(category, IsNull(Cast("VARCHAR", new SqlValue.Raw(Kind) { Column = "ANY" })), licence).Should().BeTrue("an ANY casts a number to its text, under {0}", licence.Question);

                var near = Theory(Outright(A, Enum(0.10000000000000001m, 0.25m)));
                Contradicts(near, Eq(Cast("VARCHAR", new SqlValue.Raw(A) { Column = "ANY" }), "0.1"), licence).Should().BeFalse("an ANY renders 0.10000000000000001 as 0.1, under {0}", licence.Question);
                Contradicts(near, Eq(Cast("VARCHAR", new SqlValue.Raw(A) { Column = "ANY" }), "0.2"), licence).Should().BeTrue("an ANY renders no number of the enum as 0.2, under {0}", licence.Question);

                var six = Theory(Outright(A, Enum(6.0000000000000001m)));
                Contradicts(six, Cmp(Typed(A, "INTEGER"), ">", Lit(5m)), licence).Should().BeFalse("an INTEGER column reads 6.0000000000000001 as 6, under {0}", licence.Question);
            }

            var sixth = Theory(Outright(A, Enum(6.0000000000000001m)));
            Contradicts(sixth, Eq(Typed(A, "INTEGER"), 7m), OracleLicence.Kept).Should().BeTrue("the column reads 6.0000000000000001 as 6, not 7");
            Contradicts(sixth, Eq(Typed(A, "INTEGER"), 7m), OracleLicence.Reduce).Should().BeFalse("an INTEGER column's number cells may raise, so the gate does not fire");
        }

        /// <summary>
        /// An arm of a <c>CASE</c> is written <c>FALSE</c> (<see cref="OracleLicence.NeverRead"/>) where its question's
        /// facts contradict, as <see cref="OracleLicence.Arm"/>'s do, and the WHERE raises on no document: here
        /// <c>kind = 'b'</c> beneath a sibling <c>kind = 'a'</c>. Beside a conjunct that may raise, an opaque one, it is
        /// not, though the arm's own node still contradicts: the <c>FALSE</c> would stop Calcite evaluating it.
        /// </summary>
        [Fact]
        public void AnArmIsNeverReadWhereItsFactsContradictAndTheWhereRaisesNowhere()
        {
            var condition = Eq(Text(Kind), "b");
            var @case = Or(new SqlPredicate.Case(condition, Eq(Text(N), "y"), new SqlPredicate.Constant(false)), Eq(Text(A), "x"));
            var conditions = new[] { condition };

            var safe = Declared(Theory(), Eq(Text(Kind), "a"), @case);
            safe.IsContradictory(OracleLicence.Arm(1, conditions)).Should().BeTrue("kind cannot be both a and b");
            safe.IsContradictory(OracleLicence.NeverRead(1, conditions)).Should().BeTrue("and nothing in the WHERE raises");

            var raising = Declared(Theory(), Eq(Text(Kind), "a"), @case, Opaque(9));
            raising.IsContradictory(OracleLicence.Arm(1, conditions)).Should().BeTrue("kind cannot be both a and b");
            raising.IsContradictory(OracleLicence.NeverRead(1, conditions)).Should().BeFalse("no rule says an opaque conjunct never raises");
        }

        /// <summary>
        /// A container's declaration beside the service's facts, which make <c>id</c> a present string, as a planner reads one.
        /// </summary>
        static JsonFactTheory Container(string schema) =>
            Theory([.. CosmosServiceFacts.Rules, .. JsonSchemaFacts.ReadFrom(JsonNode.Parse(schema)!)]);

        /// <summary>
        /// The reduce (<see cref="OracleLicence.Reduce"/>) asks every row FALSE: its node is contradictory only where the
        /// WHERE keeps no row the declaration admits and raises on none. A text contradiction is reduced, alone or beside an
        /// equality that raises nowhere: <c>c."id" = 'x'</c>, a raw value in a <c>VARCHAR</c> over the string the service
        /// makes it; an equality over the <c>VARIANT</c> column a partition key is promoted to, whatever the declaration says
        /// of the key; the same key read through a field typed <c>VARCHAR</c>, where the declaration makes it a string; and
        /// <c>c."id" = ?</c> and the partition key's <c>= ?</c>. It is not reduced where a conjunct may raise, though what
        /// every conjunct keeps still contradicts: <c>RETURNING INTEGER</c> over a declared string, the first design's
        /// counterexample (the cast of the text to <c>DOUBLE</c> over a path that may hold a string), the partition key's
        /// <c>&lt; ?</c> and <c>&lt; 'x'</c>, a <c>VARIANT</c> ordering Calcite does not compile against a parameter or a
        /// literal, and an <c>ANY</c> field's <c>&gt; ?</c> over a path declared an integer or a string, which raises across
        /// types.
        /// </summary>
        [Fact]
        public void TheReduceAsksEveryRowFalse()
        {
            var (id, body, category) = (P("id"), P("body"), P("category"));
            const string Declaration = """
                { "type": "object", "required": ["kind", "body"], "properties": {
                    "kind": { "enum": ["a"] }, "body": { "type": "string" }, "n": { "type": ["integer", "string"] } } }
                """;
            var theory = Container(Declaration);
            var contradiction = Eq(Text(Kind), "z");
            var variant = new SqlValue.Raw(category) { Column = "VARIANT" };

            void Reduced(JsonFactTheory declared, string because, params SqlPredicate[] conjuncts)
            {
                var scope = Declared(declared, conjuncts);
                scope.IsContradictory(OracleLicence.Kept).Should().BeTrue("every conjunct together keeps no row, {0}", because);
                scope.IsContradictory(OracleLicence.Reduce).Should().BeTrue("nothing raises, {0}", because);
            }

            void NotReduced(JsonFactTheory declared, string because, params SqlPredicate[] conjuncts)
            {
                var scope = Declared(declared, conjuncts);
                scope.IsContradictory(OracleLicence.Kept).Should().BeTrue("every conjunct together keeps no row, {0}", because);
                scope.IsContradictory(OracleLicence.Reduce).Should().BeFalse("a conjunct may raise, {0}", because);
            }

            Reduced(theory, "for a text comparison", contradiction);
            Reduced(theory, "beside c.\"id\" = 'x'", contradiction, Eq(new SqlValue.Raw(id) { Column = "VARCHAR" }, "x"));

            var keys = new[]
            {
                ("nothing", "{}"), ("a string", """{ "type": "string" }"""), ("an integer", """{ "type": "integer" }"""),
                ("a structure", """{ "type": ["object", "array"] }"""),
            };
            foreach (var (name, key) in keys)
            {
                var declared = Container(Declaration.Replace("\"n\":", $"\"category\": {key}, \"n\":"));
                Reduced(declared, $"beside the partition key's VARIANT equality, the key declared {name}", contradiction, Eq(variant, "x"));
                Reduced(declared, $"beside the partition key's = ?, the key declared {name}", contradiction, Cmp(variant, "=", Param(0, "VARIANT")));
            }

            var aString = Container(Declaration.Replace("\"n\":", "\"category\": { \"type\": \"string\" }, \"n\":"));
            Reduced(aString, "beside the key read through a field typed VARCHAR, the key declared a string", contradiction,
                Eq(new SqlValue.Raw(category) { Column = "VARCHAR" }, "x"));
            Reduced(theory, "beside c.\"id\" = ?", contradiction, Cmp(new SqlValue.Raw(id) { Column = "VARCHAR" }, "=", Param(0, "VARCHAR")));

            NotReduced(theory, "for RETURNING INTEGER over a declared string", Eq(Typed(body, "INTEGER"), 2m));
            NotReduced(theory, "for the first design's counterexample", Cmp(Cast("DOUBLE", Text(N)), ">", Lit(0m)), contradiction);
            NotReduced(theory, "beside the partition key's < ?", contradiction, Cmp(variant, "<", Param(0, "VARIANT")));
            NotReduced(theory, "beside the partition key's < 'x'", contradiction, Cmp(variant, "<", Lit("x")));
            NotReduced(theory, "beside an ANY field's > ? over an integer or a string", contradiction, Cmp(new SqlValue.Raw(N) { Column = "ANY" }, ">", Param(0, "ANY")));
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

            always.Decide(Eq(Text(Kind), "A"), OracleLicence.Declaration).Should().Be(Truth.Always);
            sometimes.Decide(Eq(Text(Kind), "A"), OracleLicence.Declaration).Should().Be(Truth.Unknown, "a document without kind keeps no row for it");
        }

        [Fact]
        public void AMembershipCoveringTheEnumIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));

            scope.Decide(In(Text(Kind), "a", "b", "c"), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Decide(In(Text(Kind), "a"), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(In(Text(Kind), "c", "d"), OracleLicence.Declaration).Should().Be(Truth.Never);
        }

        [Fact]
        public void ADisequalityWithAValueOutsideTheEnumIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));

            scope.Decide(Ne(Text(Kind), "c"), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Decide(Ne(Text(Kind), "a"), OracleLicence.Declaration).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void IsNotNullOfARequiredStringIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(A, Present), Outright(A, Type(JsonType.String))));

            scope.Decide(IsNotNull(Text(A)), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Decide(IsNull(Text(A)), OracleLicence.Declaration).Should().Be(Truth.Never);
        }

        /// <summary>
        /// A range the declared bounds satisfy is a tautology of the number, converted by a double's parse of the rendering.
        /// An integer's parse of the rendering raises on a stored <c>3000000000</c>, which the declaration admits, so there it
        /// is decided neither way; it reads <c>30.0</c>, which the service returns as <c>30</c>, though a verdict reads the
        /// coarse reading, which puts the parse's raise beside every number (<c>TODO.md</c>). A cast of the raw value is
        /// decided neither way since h15b, being
        /// each column's own, which no row measured (<c>measuredcast</c>): an <c>ANY</c> wraps a long beyond an int, so a
        /// stored 2147483648 the bound admits casts to -2147483648
        /// (<c>CalciteVariantMeasurementTests.ACastOfARawNumberIsEachColumnsOwn</c>).
        /// </summary>
        [Fact]
        public void ARangeTheDeclaredBoundsSatisfyIsAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(N, Present), Outright(N, Type(JsonType.Integer)), Outright(N, new JsonClaim.LowerBound(0m))));

            scope.Decide(Cmp(Cast("INTEGER", Raw(N)), ">=", Lit(0m)), OracleLicence.Declaration).Should().Be(Truth.Unknown, "an ANY wraps a stored 2147483648");
            scope.Decide(Cmp(Cast("INTEGER", Raw(N)), ">", Lit(0m)), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(Cmp(Cast("INTEGER", Raw(N)), "<", Lit(0m)), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(Cmp(Cast("DOUBLE", Text(N)), ">=", Lit(0m)), OracleLicence.Declaration).Should().Be(Truth.Always);

            scope.Decide(Cmp(Cast("INTEGER", Text(N)), ">=", Lit(0m)), OracleLicence.Declaration).Should().Be(Truth.Unknown, "Calcite raises on a stored 3000000000");
            scope.Decide(Cmp(Cast("INTEGER", Text(N)), "<", Lit(0m)), OracleLicence.Declaration).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void AValueOutsideTheEnumIsAlwaysFalseAndItsNegationAlwaysTrue()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));

            scope.Judge(Eq(Text(Kind), "z"), OracleLicence.Declaration).AlwaysFalse.Should().BeTrue();
            scope.Decide(Not(Eq(Text(Kind), "z")), OracleLicence.Declaration).Should().Be(Truth.Always);
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

            var verdict = scope.Judge(beneath, OracleLicence.Declaration);
            verdict.NeverTrue.Should().BeTrue();
            verdict.AlwaysFalse.Should().BeFalse();
            scope.Decide(Not(beneath), OracleLicence.Declaration).Should().NotBe(Truth.Always);
        }

        /// <summary>
        /// The accessor answers SQL null for an object: a comparison over one is UNKNOWN, and <c>IS NOT NULL</c> is FALSE —
        /// the soundness review's root cause A, decided rightly.
        /// </summary>
        [Fact]
        public void TheAccessorOverAnObjectIsNull()
        {
            var scope = Declared(Theory(Outright(A, Present), Outright(A, Type(JsonType.Object))));

            scope.Decide(IsNotNull(Text(A)), OracleLicence.Declaration).Should().Be(Truth.Never);
            scope.Decide(IsNull(Text(A)), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Judge(Eq(Text(A), "x"), OracleLicence.Declaration).AlwaysFalse.Should().BeFalse();
            scope.Decide(Not(Eq(Text(A), "x")), OracleLicence.Declaration).Should().NotBe(Truth.Always);
        }

        [Fact]
        public void TheConnectives()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));
            var always = In(Text(Kind), "a", "b");
            var never = Eq(Text(Kind), "z");
            var unknown = Eq(Text(Kind), "a");

            scope.Decide(And(always, always), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Decide(And(always, unknown), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(And(unknown, never), OracleLicence.Declaration).Should().Be(Truth.Never);
            scope.Decide(Or(unknown, always), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Decide(Or(unknown, never), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(Or(never, Not(always)), OracleLicence.Declaration).Should().Be(Truth.Never);
            scope.Decide(Not(Or(never, Not(always))), OracleLicence.Declaration).Should().Be(Truth.Always, "both sides are FALSE, not UNKNOWN");
            scope.Decide(Not(And(always, never)), OracleLicence.Declaration).Should().Be(Truth.Always);
        }

        [Fact]
        public void AnOpaqueSideDecidesNothingOnItsOwn()
        {
            var scope = Declared(Theory(Outright(Kind, Present), Outright(Kind, Enum("a", "b"))));
            var always = In(Text(Kind), "a", "b");
            var never = Eq(Text(Kind), "z");

            scope.Decide(Opaque(0), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(Or(Opaque(0), always), OracleLicence.Declaration).Should().Be(Truth.Always);
            scope.Decide(And(Opaque(0), always), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(And(Opaque(0), never), OracleLicence.Declaration).Should().Be(Truth.Never);
            scope.Decide(Not(Opaque(0)), OracleLicence.Declaration).Should().Be(Truth.Unknown);
        }

        [Fact]
        public void ARawNumberIsNotDecidedAlwaysEqualAcrossNumericTypes()
        {
            var scope = Declared(Theory(Outright(N, Present), Outright(N, Value(30m)), Outright(A, Present), Outright(A, Value("x"))));

            scope.Decide(Eq(Raw(N), 30m), OracleLicence.Declaration).Should().Be(Truth.Unknown);
            scope.Decide(Eq(Raw(N), 31m), OracleLicence.Declaration).Should().Be(Truth.Never);
            scope.Decide(Eq(Raw(A), "x"), OracleLicence.Declaration).Should().Be(Truth.Always);
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
            scope.Decide(In(Text(Kind), "a", "b"), OracleLicence.Declaration).Should().Be(Truth.Unknown);
        }

    }

}
