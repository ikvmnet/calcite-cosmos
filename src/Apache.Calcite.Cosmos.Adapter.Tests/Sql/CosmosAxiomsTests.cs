using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Adapter.Tests.Measurements;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Newtonsoft.Json.Linq;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

using Class = Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;
using Test = Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The registry holds only what has a measurement behind it, and its outcomes say no more than was measured.
    /// </summary>
    /// <remarks>
    /// The measurement is the axiom's evidence, and a row whose test has been renamed or deleted is a
    /// claim nobody is checking any more. So every row's test is looked up here, by name, and has to
    /// be a test; a row's outcomes must name that test back, stay within the row's targets, and, where
    /// they name Azure, be what a recorded run on Azure answered.
    /// </remarks>
    public class CosmosAxiomsTests
    {

        const string Root = "Apache.Calcite.Cosmos.Adapter.Tests.";

        /// <summary>
        /// The keys whose targets answer apart, where both answered: a range between bounds the orders rank apart, which the
        /// emulator keeps and Azure does not; and the string order, which Azure reads past U+0000 by code point and the
        /// emulator as it stores it. Two constants compared, the other known divergence, are no key S reads.
        /// </summary>
        static readonly MeasuredKey[] Disagreements = [MeasuredKey.Of(Test.Range, Class.Parted, Class.Between), MeasuredKey.Of(Test.Order, Class.String, Class.None),
            // The emulator matches an affix and a LIKE against the string it stores, Azure against the string as written (run 1, rows2).
            MeasuredKey.Of(Test.StartsWith, Class.String, Class.None), MeasuredKey.Of(Test.EndsWith, Class.String, Class.None), MeasuredKey.Of(Test.Contains, Class.String, Class.None),
            MeasuredKey.Of(Test.Like, Class.String, Class.None),
            // The emulator holds a whole number written with a point as its double, Azure as the int64 it is (N11).
            MeasuredKey.Of(Test.Order, Class.Number, Class.None)];

        static IEnumerable<CosmosAxiom> WithOutcomes => CosmosAxioms.All.Where(a => a.Outcomes is not null);

        static MethodInfo? PinningTest(CosmosAxiom axiom)
        {
            var dot = axiom.PinnedBy.LastIndexOf('.');
            var type = dot < 0 ? null : typeof(CosmosAxiomsTests).Assembly.GetType(Root + axiom.PinnedBy[..dot]);
            return type?.GetMethod(axiom.PinnedBy[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance);
        }

        [Fact]
        public void EveryAxiomNamesAMeasurementThatExists()
        {
            CosmosAxioms.All.Should().NotBeEmpty();

            // A theory is a fact to xUnit, so this asks for either.
            var missing = CosmosAxioms.All.Where(a => PinningTest(a)?.IsDefined(typeof(FactAttribute), inherit: true) != true).Select(a => $"{a.Id}: {a.PinnedBy}");
            missing.Should().BeEmpty("every axiom names the test that measures it, and the test must exist");
        }

        [Fact]
        public void EveryAxiomHasItsOwnName()
        {
            CosmosAxioms.All.Select(a => a.Id).Should().OnlyHaveUniqueItems();

            foreach (var axiom in CosmosAxioms.All)
                axiom.Id.Should().MatchRegex(@"^(Service|Calcite)\.[A-Z][A-Za-z]+$", "an id says what was measured and then what it says");
        }

        /// <summary>
        /// A <c>Service.</c> row was measured against an emulator or an account, and a <c>Calcite.</c>
        /// row against neither; each says when.
        /// </summary>
        [Fact]
        public void EveryAxiomSaysWhereAndWhen()
        {
            foreach (var axiom in CosmosAxioms.All)
            {
                if (axiom.Id.StartsWith("Calcite.", StringComparison.Ordinal))
                    axiom.MeasuredOn.Should().Be(CosmosAxiomTarget.Calcite, axiom.Id);
                else
                    (axiom.MeasuredOn & ~(CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure)).Should().Be((CosmosAxiomTarget)0, axiom.Id);

                axiom.MeasuredOn.Should().NotBe((CosmosAxiomTarget)0, axiom.Id);
                axiom.Measured.Should().BeOnOrAfter(new DateOnly(2026, 1, 1), axiom.Id);
                axiom.Measured.Should().BeOnOrBefore(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), axiom.Id);
                axiom.Statement.Should().EndWith(".", axiom.Id);
            }
        }

        [Fact]
        public void EveryOutcomeIsWithinItsRowsTargets()
        {
            foreach (var axiom in WithOutcomes)
                foreach (var outcome in axiom.Outcomes!)
                    (outcome.On != 0 && (outcome.On & ~axiom.MeasuredOn) == 0).Should().BeTrue($"{axiom.Id}'s {outcome.Test}/{outcome.Operand} names {outcome.On}, within {axiom.MeasuredOn}");
        }

        /// <summary>A key may carry an outcome per target where they answered apart, over cells that do not meet.</summary>
        [Fact]
        public void NoTwoOutcomesNameOneTargetForOneCell()
        {
            var claims = from axiom in WithOutcomes
                         from outcome in axiom.Outcomes!
                         from target in Enum.GetValues<CosmosAxiomTarget>()
                         where outcome.On.HasFlag(target)
                         from cell in outcome.Cells
                         select $"{axiom.Id} {outcome.Formula ?? outcome.Test + "/" + outcome.Operand} {cell.Cell} on {target}";

            claims.Should().OnlyHaveUniqueItems();
        }

        /// <summary>A row read as data, by its outcomes, its subjects, its function outcomes, its columns or its stored classes, names a test that says it pins the row.</summary>
        [Fact]
        public void EveryRowReadAsDataNamesAPinningTestThatCarriesItsId()
        {
            foreach (var axiom in CosmosAxioms.All.Where(a => a.Outcomes is not null || a.Subjects is not null || a.FunctionOutcomes is not null || a.Columns is not null || a.StoredAs is not null
                || a.Parts is not null || a.Moves is not null || a.Truncations is not null || a.PairReadings is not null || a.Boxes is not null || a.SeesThrough is not null
                || a.NullThrough is not null || a.CalciteReadings is not null || a.Casts is not null || a.ReaderOrders is not null || a.Comparands is not null
                || a.Rewrites is not null || a.Unsent is not null || a.WholeReadings is not null || a.Folds is not null))
                (PinningTest(axiom)?.GetCustomAttributes<CosmosPinsAttribute>().Select(a => a.Id) ?? []).Should().Contain(axiom.Id, axiom.PinnedBy);
        }

        /// <summary>
        /// A function row names each key once per target, within its row's targets, and reads back one row per key per
        /// target, each key's function, kind and band as written and in the order the key compares by.
        /// </summary>
        [Fact]
        public void TheFunctionRowsReadBackKeyByKey()
        {
            var outcomes = (from axiom in CosmosAxioms.All from o in axiom.FunctionOutcomes ?? [] select (axiom, o)).ToList();
            outcomes.Should().NotBeEmpty();
            foreach (var (axiom, o) in outcomes)
                (o.On != 0 && (o.On & ~axiom.MeasuredOn) == 0).Should().BeTrue($"{axiom.Id}'s {o.Function}/{o.Operand}/{o.Band} names {o.On}, within {axiom.MeasuredOn}");

            var rows = CosmosAxiomRows.ReadFunctions(CosmosAxioms.All);
            rows.Should().HaveCount(outcomes.Sum(x => System.Numerics.BitOperations.PopCount((uint)x.o.On)));
            rows.Select(r => (r.Key, r.Target)).Should().OnlyHaveUniqueItems();
            rows.Should().Contain((FunctionKey.Of("POSITION", 1, Class.String, JsonCharacterBand.Surrogates), RenderingTarget.Emulator, CosmosFunctionAnswer.Differs));
            CosmosAxiomRows.ReadFunctionSubjects(CosmosAxioms.All).Should().Contain(("POSITION", 1L)).And.Contain(("POSITION", 0L)).And.Contain(("CONCAT", FunctionKey.Every))
                .And.Contain(("CONCAT", FunctionKey.Literal)).And.Contain(("REPEAT", FunctionKey.Literal)).And.OnlyHaveUniqueItems();

            // A row's band and class of value are read into its key (F1, F2).
            var banded = CosmosAxioms.All.First(a => a.Outcomes is not null) with
            {
                Outcomes = [new CosmosOutcome(CosmosServiceTest.Contains, Class.String, CosmosAxiomTarget.Azure, "string=true", Band: JsonCharacterBand.Nul)],
                FunctionOutcomes = [new CosmosFunctionOutcome("REPLACE", 1, Class.String, JsonCharacterBand.None, CosmosAxiomTarget.Azure, CosmosFunctionAnswer.Undefined, CalciteYield.Value, FunctionValueClass.Empty)],
            };
            CosmosAxiomRows.Read([banded]).Select(r => r.Key).Should().Equal(MeasuredKey.Of(CosmosServiceTest.Contains, Class.String, Class.String, JsonCharacterBand.Nul));
            CosmosAxiomRows.ReadFunctions([banded]).Select(r => r.Key).Should().Equal(FunctionKey.Of("REPLACE", 1, Class.String, JsonCharacterBand.None, FunctionValueClass.Empty));
            rows.Select(r => r.Key).Order().Select(FunctionKey.FunctionOf).Should().BeInAscendingOrder(StringComparer.Ordinal);
        }

        /// <summary>
        /// Each axiom's readers are read for each function its rows compute, one row an axiom, and none is picked: two axioms
        /// of one function give two rows, which S meets (<c>measuredreaders</c>, an intersection).
        /// </summary>
        [Fact]
        public void EachAxiomsReadersAreReadForEachFunctionItsRowsCompute()
        {
            var row = CosmosAxioms.All.First(a => a.FunctionOutcomes is not null);
            CosmosAxiomRows.ReadFunctionReads(CosmosAxioms.All).Select(r => r.Function).Should().OnlyHaveUniqueItems("no two axioms of the registry compute one function");
            var outcome = new CosmosFunctionOutcome("F", 0, Class.String, JsonCharacterBand.None, CosmosAxiomTarget.Emulator, CosmosFunctionAnswer.Same, CalciteYield.Value);
            var (one, two) = (row with { FunctionOutcomes = [outcome, outcome with { Band = JsonCharacterBand.Nul }], Readers = ["a", "b"] }, row with { FunctionOutcomes = [outcome], Readers = ["b"] });
            CosmosAxiomRows.ReadFunctionReads([one, two]).Select(r => (r.Function, string.Join(",", r.Readers))).Should().Equal(("F", "a,b"), ("F", "b"));
        }

        /// <summary>
        /// The classes a written string is stored holding are a <c>Service</c> row's cells, which name no target, since the
        /// rules of JSON have none: an unpaired surrogate written, the high band and U+FFFD within it; a pair is stored as
        /// written.
        /// </summary>
        [Fact]
        public void TheStoredClassesAreReadOffTheirRow()
        {
            CosmosAxiomRows.ReadStored(CosmosAxioms.All).Should().BeEquivalentTo(new[]
            {
                new JsonClassMap(JsonValues.UnpairedClass, JsonValues.HighBand), new JsonClassMap(JsonValues.UnpairedClass, JsonValues.ReplacementClass),
            });
            global::Apache.Calcite.Cosmos.Adapter.Metadata.CosmosReasoning.Stored.Should().Equal(CosmosAxiomRows.ReadStored(CosmosAxioms.All));
        }

        /// <summary>
        /// The structured cells that were a prose row's (F3) read back as written, each under its row: the bands each code
        /// point order parts and the one the emulator's moves, what a lone surrogate is stored as on the target that stores
        /// it so, what Azure's <c>CONTAINS</c> and a <c>LIKE</c> of one run read a string to, the mirror law, the boxes and
        /// the comparisons that see into one, the function that answers a structure alone, the boolean parse with the
        /// classes its witnesses held, the casts each citing its row, the text reader's order, and the classes each type
        /// test is true of in process; and G1's, the two characters the emulator's spelling rewrites and the unpaired
        /// surrogate no parameter is sent holding on either target.
        /// </summary>
        [Fact]
        public void TheFoundationsRowsReadBackAsWritten()
        {
            var all = CosmosAxioms.All;
            CosmosAxiomRows.ReadParts(all).Should().BeEquivalentTo(new[]
            {
                (CosmosAnswer.CodePoint, JsonValues.SurrogateBand, JsonValues.HighBand), (CosmosAnswer.CodePoint, JsonValues.HighBand, JsonValues.SurrogateBand),
                (CosmosAnswer.StoredCodePoint, JsonValues.SurrogateBand, JsonValues.HighBand), (CosmosAnswer.StoredCodePoint, JsonValues.HighBand, JsonValues.SurrogateBand),
            });
            CosmosAxiomRows.ReadMoves(all).Should().Equal((CosmosAnswer.StoredCodePoint, JsonValues.NulClass));
            CosmosAxiomRows.ReadStoredOn(all).Should().BeEquivalentTo(new[]
            {
                (RenderingTarget.Azure, JsonValues.UnpairedClass, JsonValues.HighBand), (RenderingTarget.Azure, JsonValues.UnpairedClass, JsonValues.ReplacementClass),
            });
            CosmosAxiomRows.ReadTruncations(all).Should().BeEquivalentTo(new[]
            {
                (RenderingTarget.Azure, Test.Contains, CosmosTruncation.OneRun, JsonValues.NulClass), (RenderingTarget.Azure, Test.Like, CosmosTruncation.OneRun, JsonValues.NulClass),
            });
            CosmosAxiomRows.ReadRewrites(all).Should().BeEquivalentTo(new[] { (CosmosAnswer.StoredCodePoint, JsonValues.NulClass), (CosmosAnswer.StoredCodePoint, JsonValues.EscapeClass) });
            CosmosAxiomRows.ReadFolds(all).Should().BeEquivalentTo(
                from target in new[] { RenderingTarget.Azure, RenderingTarget.Emulator }
                from test in new[] { Test.StartsWith, Test.EndsWith, Test.Contains }
                from function in new[] { "UPPER", "LOWER" }
                from band in new[] { JsonValues.SurrogateBand, JsonValues.HighBand, JsonValues.NonAsciiBand }
                select (target, test, function, band));
            CosmosAxiomRows.ReadUnsent(all).Should().BeEquivalentTo(new[] { (RenderingTarget.Azure, JsonValues.UnpairedClass), (RenderingTarget.Emulator, JsonValues.UnpairedClass) });

            var mirror = CosmosAxiomRows.ReadPairReadings(all);
            mirror.Should().HaveCount(12).And.Contain((Test.Less, true, Test.Greater)).And.Contain((Test.GreaterOrEqual, false, Test.GreaterOrEqual));
            mirror.Select(r => (r.Test, r.Swapped)).Should().OnlyHaveUniqueItems();

            CosmosAxiomRows.ReadBoxes(all).Should().HaveCount(9).And.Contain((PredicateAtom.TextReader, "VARCHAR")).And.Contain((JsonValues.IntegerType, "BIGINT"))
                .And.Contain(("CHAR", "VARCHAR")).And.Contain((JsonValues.BooleanType, "BOOLEAN")).And.NotContain(b => b.Kind == "INTEGER", "an INTEGER is boxed apart from a stored integer");
            CosmosAxiomRows.ReadSeesThrough(all).Should().BeEquivalentTo(PredicateAtom.Equal, PredicateAtom.NotEqual);
            CosmosAxiomRows.ReadNullThrough(all).Should().BeEquivalentTo("VARCHAR", "BOOLEAN", "INTEGER", "BIGINT", "DOUBLE", "DECIMAL");
            CosmosAxiomRows.ReadTestReads(all).Should().BeEquivalentTo(new[] { (PredicateAtom.TextReader, PredicateAtom.RawReader), (PredicateAtom.RawReader, PredicateAtom.RawReader),
                (PredicateAtom.TypedReaders, PredicateAtom.RawReader), (PredicateAtom.QueryReader, PredicateAtom.RawReader), (PredicateAtom.RawIn("VARIANT"), PredicateAtom.RaisesReader), (PredicateAtom.RawIn("ANY"), PredicateAtom.RaisesReader),
                (PredicateAtom.RawIn("VARCHAR"), PredicateAtom.RaisesReader), (PredicateAtom.RawIn("CHAR"), PredicateAtom.RaisesReader), (PredicateAtom.CallReaders, PredicateAtom.RaisesReader),
                (PredicateAtom.FieldForm, PredicateAtom.RaisesReader), (PredicateAtom.ItemForm, PredicateAtom.RaisesReader), (PredicateAtom.AnyCastForm, PredicateAtom.RaisesReader),
                (PredicateAtom.TextCastForm, PredicateAtom.RaisesReader) });

            var parse = PredicateAtom.CastReader("BOOLEAN", PredicateAtom.TextReader);
            CosmosAxiomRows.ReadCalciteReadings(all).Should().HaveCount(30).And.Contain((parse, Class.String, CosmosCalciteReading.Parses))
                .And.Contain((PredicateAtom.TextReader, Class.String, CosmosCalciteReading.Itself)).And.Contain((PredicateAtom.TextReader, Class.Number, CosmosCalciteReading.Renders))
                .And.Contain((PredicateAtom.TextReader, Class.Absent, CosmosCalciteReading.Null))
                .And.Contain((parse, Class.Absent, CosmosCalciteReading.Null)).And.Contain((parse, Class.Number, CosmosCalciteReading.Raises))
                .And.Contain((parse, Class.Object, CosmosCalciteReading.Null)).And.Contain((PredicateAtom.TypedReader("VARCHAR"), Class.String, CosmosCalciteReading.Itself))
                .And.Contain((PredicateAtom.TypedReader("CHAR"), Class.Absent, CosmosCalciteReading.Null));
            CosmosAxiomRows.ReadCalciteBands(all).Should().BeEquivalentTo(new[] { (parse, Class.String, JsonValues.TrimmedClass), (parse, Class.String, JsonValues.NulClass) });
            CosmosAxiomRows.ReadCasts(all).Should().HaveCount(5).And.Contain(("BOOLEAN", PredicateAtom.TextReader, "Calcite.CastToBooleanParses"))
                .And.Contain(("DOUBLE", PredicateAtom.TextReader, "Calcite.CastOfTheAccessorParsesTheRendering")).And.Contain(("VARCHAR", PredicateAtom.RawReader, "Calcite.CastOfARawValueToText"));
            var orders = CosmosAxiomRows.ReadReaderOrders(all);
            orders.Where(o => o.Claim == CosmosReaderOrder.Itself).Select(o => o.Reader).Should().BeEquivalentTo(PredicateAtom.TextReader, PredicateAtom.TypedReader("VARCHAR"), PredicateAtom.TypedReader("CHAR"));
            orders.Where(o => o.Claim == CosmosReaderOrder.Reflexive).Should().HaveCount(16).And.Contain((PredicateAtom.InColumn(PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader), "ANY"), CosmosReaderOrder.Reflexive))
                .And.NotContain(o => o.Reader == PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader) || o.Reader == PredicateAtom.TypedReader("DOUBLE") || o.Reader == PredicateAtom.TypedReader("FLOAT")
                    || o.Reader == PredicateAtom.TypedReader("REAL"), "a double may hold a NaN, which compares as nothing alike");

            var kinds = CosmosAxiomRows.ReadTestKinds(all);
            kinds.Where(k => k.Test == Test.IsString).Select(k => k.Kind).Should().Equal(Class.String);
            kinds.Where(k => k.Test == Test.IsDefined).Select(k => k.Kind).Should().BeEquivalentTo([Class.Null, Class.True, Class.False, Class.Number, Class.String, Class.Object, Class.Array]);
            kinds.Where(k => k.Test == Test.IsPrimitive).Select(k => k.Kind).Should().BeEquivalentTo([Class.Null, Class.True, Class.False, Class.Number, Class.String]);
        }

        /// <summary>
        /// The raw columns S answers keys from are two <c>Calcite</c> rows', read through the registry one fact a field (h12,
        /// G5): what each reading reads each class as, the literals it is beside, the comparisons that compile, whether it
        /// compares as the type or as text, and what it answers across types. What each key answers is S's rules' over them:
        /// a VARIANT's number beside a string unequal and a VARCHAR column's raising; a VARIANT's ordering failing and an
        /// ANY's left to the cell; an ordering across types failing; a null and an absent path SQL null; the cast's number
        /// null through a VARIANT and its rendering through an ANY; a VARCHAR column beside nothing but a string.
        /// </summary>
        [Fact]
        public void TheRawColumnsAreTwoCalciteRowsReadOneFactAField()
        {
            const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;
            var rows = CosmosAxiomRows.ReadColumns(CosmosAxioms.All);
            var cast = PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader);
            (rows.Reads.Count, rows.Beside.Count, rows.Compiles.Count, rows.AsType.Count, rows.Across.Count).Should().Be((48, 18, 30, 6, 6));
            rows.Reads.Select(r => (r.Reading, r.Column, r.Kind)).Should().OnlyHaveUniqueItems();
            rows.Columns.Should().BeEquivalentTo("VARIANT", "ANY", "VARCHAR");
            rows.Reads.Where(r => r.Kind is Class.Null or Class.Absent).Should().HaveCount(12).And.OnlyContain(r => r.Answer == CosmosCalciteReading.Null,
                "a stored null and an absent path are SQL null in each column, read and cast alike (F3)");

            var union = ProbeRows.Columns;
            RawAnswer At(string reader, Class kind, Test test, Class operand, string column) => union[RawKey.Of(reader, kind, test, operand).In(column)];

            (At("raw", Class.Number, Test.Equal, Class.String, "VARIANT"), At("raw", Class.Number, Test.Equal, Class.String, "VARCHAR")).Should().Be((RawAnswer.Stated(F), RawAnswer.Stated(R)));
            (At("raw", Class.Number, Test.Less, Class.Number, "VARIANT"), At("raw", Class.Number, Test.Less, Class.Number, "ANY")).Should().Be((RawAnswer.Fails, RawAnswer.OnTheCell));
            (At("raw", Class.True, Test.Equal, Class.False, "ANY"), At("raw", Class.Object, Test.Greater, Class.String, "ANY")).Should().Be((RawAnswer.OnTheCell, RawAnswer.Stated(R)));
            (At("raw", Class.Null, Test.Equal, Class.String, "ANY"), At("raw", Class.Null, Test.IsNull, Class.None, "VARCHAR")).Should().Be((RawAnswer.Stated(U), RawAnswer.Stated(T)));
            (At("raw", Class.Absent, Test.Equal, Class.String, "ANY"), At("raw", Class.Absent, Test.IsNull, Class.None, "VARIANT")).Should().Be((RawAnswer.Stated(U), RawAnswer.Stated(T)));
            (At(cast, Class.Number, Test.Equal, Class.String, "VARIANT"), At(cast, Class.Number, Test.Equal, Class.String, "ANY"), At(cast, Class.Object, Test.IsNull, Class.None, "VARIANT"))
                .Should().Be((RawAnswer.Stated(U), RawAnswer.OnTheCell, RawAnswer.Stated(R)));
            At("raw", Class.Number, Test.Equal, Class.Number, "VARCHAR").Should().Be(RawAnswer.Unanswered, "a VARCHAR column is raw beside a string alone");
            System.GC.KeepAlive(ProbeRows.ProbeTheory);
        }

        /// <summary>
        /// A vector that names a class twice, one no enum holds, or none, is refused as the registry is read, and a copy with
        /// other answers reads its own.
        /// </summary>
        [Fact]
        public void AMalformedVectorIsRefused()
        {
            foreach (var vector in new[] { "absent=false absent=true", "nul=true", "absent=1", "absent=True,False", "absent", "" })
                FluentActions.Invoking(() => new CosmosOutcome(Test.IsNull, Class.None, CosmosAxiomTarget.Emulator, vector)).Should().Throw<FormatException>(vector);

            (new CosmosOutcome(Test.IsNull, Class.None, CosmosAxiomTarget.Emulator, "absent=false") with { Answers = "null=true" }).Cells.Should().Equal((Class.Null, CosmosAnswer.True));
        }

        /// <summary>
        /// The registry's rows define S, which a contradiction across rows would refuse, and each reads back as written; and
        /// alone they license the connectives under every set, the probe account's among them, so S composes from the
        /// registry.
        /// </summary>
        [Fact]
        public void TheRowsLoadIntoS()
        {
            var rows = CosmosAxiomRows.Read(CosmosAxioms.All);
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(rows, CosmosAxiomRows.ReadCasts(CosmosAxioms.All)));

            foreach (var row in rows)
                theory.Theory.Get(theory.Outcome.Of(row.Key, row.Target)).Should().Be(row.Answer, $"{row.AxiomId} at {row.Key} on {row.Target}");

            foreach (var set in Enum.GetValues<RenderingSet>())
                theory.Theory.Get(theory.Licensed.Of(set)).Should().Contain([Test.And, Test.Or, Test.Not], set.ToString());

            GC.KeepAlive(theory);
        }

        /// <summary>
        /// A new divergence between the emulator and Azure is data, not a conflict, so it is declared, or it would pass unseen:
        /// a key S settles for each, whose answers differ and mean nothing in common. What a <c>WHERE</c> alone measured on
        /// one, beside an answer within it on the other, is no divergence.
        /// </summary>
        [Fact]
        public void TheKeysTheTargetsDisagreeOnAreDeclared()
        {
            var means = ProbeTheory.Theory.Get(ProbeTheory.Means.Of());
            ValueSet<CosmosAnswer>? Final(MeasuredKey key, RenderingSet set) => ProbeTheory.Theory.TryGet(ProbeTheory.Final.Of(key, set), out var answers) ? answers : null;
            ServiceOutcomes Meaning(ValueSet<CosmosAnswer> answers) => answers.Aggregate(ServiceOutcomes.None, (m, a) => m | means.Of(a));
            var disagreeing = from key in ProbeRows.Rows.Select(r => r.Key).Distinct()
                              let azure = Final(key, RenderingSet.Production)
                              let emulator = Final(key, RenderingSet.Emulator)
                              where azure is not null && emulator is not null && azure.Equals(emulator) == false && (Meaning(azure) & Meaning(emulator)) == ServiceOutcomes.None
                              select key.ToString();

            disagreeing.Should().BeEquivalentTo(Disagreements.Select(k => k.ToString()));
        }

        /// <summary>
        /// Per target, every cell of a key some outcome answers there is answered or declared owed, and nothing owed is
        /// answered; and no outcome answers a cell its key is never read at.
        /// </summary>
        [Fact]
        public void TheOwedCellsAreDeclared()
        {
            var rows = CosmosAxiomRows.Read(CosmosAxioms.All);
            rows.Where(r => CosmosOutcomeWitnesses.Cells(r.Key.Test, r.Key.Operand, r.Target).Contains(r.Key.Cell) == false).Should().BeEmpty();

            var uncovered = from key in rows.GroupBy(r => (r.Key.Test, r.Key.Operand, r.Target))
                            from cell in CosmosOutcomeWitnesses.Cells(key.Key.Test, key.Key.Operand, key.Key.Target).Except(key.Select(r => r.Key.Cell))
                            select $"{MeasuredKey.Of(key.Key.Test, key.Key.Operand, cell)} on {key.Key.Target}";

            uncovered.Should().BeEquivalentTo(CosmosAxiomRows.Owed.Select(o => $"{o.Key} on {o.Target}"));
        }

        /// <summary>
        /// A composite's measured answers are what its atoms compose to: read by a copy of the reader's grammar and evaluated
        /// over each witness through S's primitives on the table S settles for the target, on each target it names the
        /// composition is the answer, every atom here having been measured definite on both.
        /// </summary>
        [Fact]
        public void EveryCompositeIsWhatItsAtomsCompose()
        {
            var path = SqlValue.Spell(JsonDocumentPath.Root.Property("v"));
            var tables = new[] { (CosmosAxiomTarget.Emulator, RenderingSet.Emulator), (CosmosAxiomTarget.Azure, RenderingSet.Production) }
                .ToDictionary(t => t.Item1, t => ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(t.Item2)), ProbeTheory.Theory.Get(ProbeTheory.ReaderClasses.Of())));
            var wrong = from axiom in WithOutcomes
                        from outcome in axiom.Outcomes!
                        where outcome.Formula is not null
                        from target in tables.Keys
                        where outcome.On.HasFlag(target)
                        from cell in outcome.Cells
                        let measured = AnswerMeanings.Measured.Of(cell.Answer)
                        let cells = new CellPartition(path, ValueSet.From(CosmosOutcomeWitnesses.Of(Class.None, cell.Cell).Select(CellOf)))
                        from composed in CellOutcomesTests.Evaluate(Read(outcome.Formula!, path), cells, tables[target], LeafReads.None).Outcomes.Select(o => o.Outcomes)
                        where composed != measured
                        select $"{axiom.Id}: {outcome.Formula} at {cell.Cell} on {target} is {measured}, composed {composed}";

            wrong.Should().BeEmpty();
        }

        static readonly Dictionary<string, Test> Operators = new() { ["="] = Test.Equal, ["!="] = Test.NotEqual, ["<"] = Test.Less, ["<="] = Test.LessOrEqual, [">"] = Test.Greater, [">="] = Test.GreaterOrEqual };

        /// <summary>The reader's grammar, as far as the composites go: <c>NOT</c>, a parenthesised junction, a test, a comparison or <c>c.v</c> bare, and a constant.</summary>
        static ServiceForm Read(string formula, string path)
        {
            var tokens = Regex.Matches(formula, @"""[^""]*""|-?\d+(\.\d+)?(?![\w.])|<=|>=|!=|[()=<>]|[\w.]+").Select(m => m.Value).ToList();
            var at = 0;
            ServiceForm Term()
            {
                var token = tokens[at++];
                if (token == "NOT")
                    return ServiceForm.NotOf(Term());
                if (token is "true" or "false")
                    return ServiceForm.ConstantOf(token == "true");
                if (token.StartsWith("IS_", StringComparison.Ordinal))
                {
                    at += 3;
                    return ServiceForm.TestOf(Enum.Parse<Test>("Is" + token[3] + token[4..].ToLowerInvariant()), path);
                }

                if (token == "(")
                {
                    var members = new List<ServiceForm> { Term() };
                    var connective = Test.And;
                    while (tokens[at] != ")")
                    {
                        connective = tokens[at++] == "AND" ? Test.And : Test.Or;
                        members.Add(Term());
                    }

                    at++;
                    return members.Count == 1 ? members[0] : ServiceForm.JunctionOf(connective, members);
                }

                if (at + 1 < tokens.Count && Operators.TryGetValue(tokens[at], out var op))
                {
                    var literal = tokens[at + 1];
                    at += 2;
                    return ServiceForm.CompareOf(op, path, literal[0] == '"' ? JsonValues.Of(literal[1..^1]) : JsonValues.Of(decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture)));
                }

                return ServiceForm.BareOf(path);
            }

            var form = Term();
            at.Should().Be(tokens.Count, formula);
            return form;
        }

        static PathCell CellOf(string? json) => json is null ? new PathCell.Absent() : JToken.Parse(json) switch
        {
            { Type: JTokenType.Null } => new PathCell.Null(),
            { Type: JTokenType.Object } => new PathCell.Structure(JsonValues.ObjectType),
            { Type: JTokenType.Array } => new PathCell.Structure(JsonValues.ArrayType),
            { Type: JTokenType.String } s => new PathCell.Point(JsonValues.Of((string)s!)),
            { Type: JTokenType.Boolean } b => new PathCell.Point(JsonValues.Of((bool)b)),
            var n => new PathCell.Point(JsonValues.Of(n.Value<decimal>())),
        };

        /// <summary>
        /// Azure is claimed exactly where a committed run record holds the cell's answer, read by the statements the row's
        /// pinning test runs over every witness it holds the cell to: so no outcome claims a witness, a literal or a
        /// statement only the emulator ran, and no such line at a cell its row declares goes unclaimed. A line read
        /// otherwise — projected, a literal written inline, an order alone — is recorded as what it is, and backs nothing;
        /// so does a cell a run answered two ways, and one its row declares for no target, measured beside its key's.
        /// </summary>
        [Fact]
        public void EveryAzureClaimHasARecordedRun()
        {
            var records = Records().ToList();
            records.Should().NotBeEmpty();
            records.Select(r => r.Row).Should().BeSubsetOf(CosmosAxioms.All.Select(a => a.Id));
            records.Select(r => (r.Record, r.Row, r.Key, r.Cell, r.Answer)).Should().OnlyHaveUniqueItems();

            // The cells each row's keys declare, and the class whose witnesses stand there: a connective's and a composite's
            // are the kinds at c.v, as the harness writes them.
            var declared = (from axiom in WithOutcomes
                            from o in axiom.Outcomes!
                            from cell in o.Cells
                            select (Key: (axiom.Id, o.Formula ?? o.Test + "/" + o.Operand, cell.Cell), Operand: o.Formula is null && o.Test is not (Test.And or Test.Or) ? o.Operand : Class.None))
                .DistinctBy(d => d.Key).ToDictionary(d => d.Key, d => d.Operand);
            var split = records.GroupBy(r => (r.Record, r.Row, r.Key, r.Cell)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var claimable = (from r in records
                             where Enum.TryParse<CosmosAnswer>(r.Answer, out _) && declared.TryGetValue((r.Row, r.Key, r.Cell), out var operand)
                                 && split.Contains((r.Record, r.Row, r.Key, r.Cell)) == false && Witnessed(operand, r.Cell, r.Witnesses)
                             select (r.Row, r.Key, r.Cell, Answer: Enum.Parse<CosmosAnswer>(r.Answer))).ToList();
            // A row the probe account's witness record holds is held to it below, line by line, not by the harness's cells.
            var witnessed = AzureWitnessRecords().Select(l => l.Row).ToHashSet();
            var claims = (from axiom in WithOutcomes
                          where witnessed.Contains(axiom.Id) == false
                          from outcome in axiom.Outcomes!
                          where outcome.On.HasFlag(CosmosAxiomTarget.Azure)
                          from cell in outcome.Cells
                          select (axiom.Id, outcome.Formula ?? outcome.Test + "/" + outcome.Operand, cell.Cell, cell.Answer)).ToList();

            // A line is claimed by its answer, or by a later run's that means nothing more, as a WHERE NOT read beside a NotKept.
            claims.Except(claimable).Should().BeEmpty("an outcome claims Azure only where a run read the cell as its test does");
            claimable.Where(line => claims.Any(c => (c.Item1, c.Item2, c.Cell) == (line.Row, line.Key, line.Cell)
                && (c.Answer == line.Answer || AnswerMeanings.Measured.Of(c.Answer) is var meant and not ServiceOutcomes.None
                    && (meant & ~AnswerMeanings.Measured.Of(line.Answer)) == ServiceOutcomes.None)) == false).Should().BeEmpty("no evidence a run read so is dropped");

            // A row measured by the witness probe claims Azure only where the probe account's record holds every line its
            // emulator record holds of each expression it ran, each answered alike but where the row says the two part; an
            // expression it has not run claims nothing there (EveryAzureFunctionOutcomeIsWhatTheProbeAccountsRecordMeasures).
            var emulator = EmulatorRecords().Where(l => l.Release == "EN20261019").ToLookup(l => l.Row);
            var azure = AzureWitnessRecords().ToLookup(l => l.Row);
            var claiming = CosmosAxioms.All.Where(a => a.MeasuredOn.HasFlag(CosmosAxiomTarget.Azure) && emulator.Contains(a.Id)).Select(a => a.Id).ToList();
            claiming.Should().BeEquivalentTo(azure.Select(g => g.Key), "a row the probe account ran claims Azure, and one it did not claims nothing");
            foreach (var row in claiming)
            {
                var asked = azure[row].ToDictionary(l => (l.Expression, l.Witness, Kept(l.Answer)), l => l.Answer);
                var ran = azure[row].Select(l => l.Expression).ToHashSet(StringComparer.Ordinal);
                emulator[row].Where(l => ran.Contains(l.Expression)).Select(l => (l.Expression, l.Witness, Kept(l.Answer))).Except(asked.Keys)
                    .Should().BeEmpty($"{row}: the probe account ran every line the emulator did of each expression it ran");
                emulator[row].Where(l => asked.TryGetValue((l.Expression, l.Witness, Kept(l.Answer)), out var a) && a != l.Answer && Parted(row, l.Expression, l.Witness) == false)
                    .Select(l => $"{l.Expression} over {l.Witness}: {l.Answer} on the emulator, {asked[(l.Expression, l.Witness, Kept(l.Answer))]} on Azure").Should().BeEmpty($"{row} answers alike but where it says the targets part");
            }
        }

        /// <summary>Whether a witness probe's answer is <c>value/kept</c>, not a projection alone.</summary>
        static bool Kept(string answer) => answer.Length >= 2 && answer[^2] == '/' && "TFU!?".Contains(answer[^1]);

        /// <summary>
        /// Where a row measured by the witness probe says the targets part: a string holding U+0000 ordered against another
        /// (<c>Service.StringOrderIsCodePoint</c>), 9007199254740993.0 held, % past 2^53, the string functions, whose keys
        /// are each target's own (<see cref="EveryAzureFunctionOutcomeIsWhatTheProbeAccountsRecordMeasures"/>), the case
        /// mapping's and <c>RIGHT</c>'s own witnesses among them, and <c>_</c> over U+0000 and U+FDD0, which the emulator
        /// stores as two.
        /// </summary>
        static bool Parted(string row, string expression, string witness) => row switch
        {
            "Service.TwoPathsCompareAsAPathAndALiteral" => witness.Contains("\\u0000", StringComparison.Ordinal) && witness.Contains("\"w\":\"", StringComparison.Ordinal),
            "Service.IntegralNumbersReadBackAsHeld" => witness == "9007199254740993.0",
            "Service.IntegerArithmeticIsExactWithinLong" => expression.Contains('%', StringComparison.Ordinal),
            "Service.StringFunctionAnswersByBand" or "Service.CaseMappingOverCasedLetters" or "Service.RightOverASplitPair" => true,
            "Service.LikeOneOverEachClass" => witness.Contains("\\u0000", StringComparison.Ordinal) || witness.Contains('﷐', StringComparison.Ordinal),
            _ => false,
        };

        /// <summary>
        /// A function outcome claims Azure only where the probe account's record holds its key's lines: those lines read
        /// again through Calcite in process answer each key as the row records it on Azure, or better.
        /// </summary>
        [Fact]
        public void EveryAzureFunctionOutcomeIsWhatTheProbeAccountsRecordMeasures()
        {
            var measured = new (string Row, IReadOnlyList<CosmosFunctionSpelling> Functions, IReadOnlyList<(Class Kind, JsonCharacterBand Band, string?[] Witnesses)> Keys)[]
            {
                ("Service.StringFunctionAnswersByBand", CosmosStringFunctionMeasurementTests.Functions, CosmosStringFunctionMeasurementTests.FunctionWitnesses),
                ("Service.IntegerArithmeticIsExactWithinLong", CosmosJsonTypeMeasurementTests.Arithmetic, CosmosJsonTypeMeasurementTests.ArithmeticWitnesses),
            };

            CosmosAxioms.All.Where(a => a.FunctionOutcomes?.Any(o => o.On.HasFlag(CosmosAxiomTarget.Azure)) == true).Select(a => a.Id)
                .Should().BeSubsetOf(measured.Select(m => m.Row), "a function key claims Azure only where a committed run holds its answer");

            var lines = AzureWitnessRecords().Where(l => Kept(l.Answer) == false).ToDictionary(l => (l.Row, l.Expression, l.Witness), l => l.Answer);
            foreach (var (row, functions, keys) in measured)
            {
                // A spelling the probe account has not run measures nothing there, nor one it left unanswered over a witness
                // (CosmosFunctionComparison.Judge), and its keys claim nothing on Azure.
                var ran = functions.Where(f => keys.SelectMany(k => k.Witnesses).All(w => lines.ContainsKey((row, CosmosFunctionComparison.Spelled(f), w ?? "absent")))).ToList();
                var asked = CosmosFunctionComparison.Spellings(ran)
                    .Select(spelling => keys.SelectMany(k => k.Witnesses).Select(w => lines[(row, spelling, w ?? "absent")]).ToArray()).ToArray();
                // A key another row's witnesses measured is held to these lines too, where they measured it.
                var judgedHere = CosmosFunctionComparison.Judge(row, ran, keys, asked);
                CosmosFunctionComparison.Unsound(judgedHere, CosmosAxioms.Get(row).FunctionOutcomes!.Where(o => o.Evidence is null || judgedHere.Keys.Any(k => Covers(o, k))), CosmosAxiomTarget.Azure)
                    .Should().BeEmpty(row);
            }

            // A row whose own witnesses measure another's keys (run 2): its lines read again answer each key it measured as
            // the keyed row records it on Azure, or better.
            var edges = new (string Row, IReadOnlyList<CosmosFunctionSpelling> Functions, IReadOnlyList<(Class Kind, JsonCharacterBand Band, string?[] Witnesses)> Keys)[]
            {
                ("Service.CaseMappingOverCasedLetters", CosmosStringFunctionMeasurementTests.CaseMappings, CosmosStringFunctionMeasurementTests.CasedLetters),
                ("Service.RightOverASplitPair", CosmosStringFunctionMeasurementTests.SplitRights, CosmosStringFunctionMeasurementTests.SplitPairs),
            };
            foreach (var (row, functions, keys) in edges)
            {
                var asked = CosmosFunctionComparison.Spellings(functions)
                    .Select(spelling => keys.SelectMany(k => k.Witnesses).Select(w => lines[(row, spelling, w ?? "absent")]).ToArray()).ToArray();
                var judged = CosmosFunctionComparison.Judge(row, functions, keys, asked);
                var keyed = CosmosAxioms.Get("Service.StringFunctionAnswersByBand").FunctionOutcomes!
                    .Where(o => o.Type is null && judged.Keys.Any(k => (k.Function, k.Ordinal, k.Kind, k.Band) == (o.Function, o.Subject, o.Operand, o.Band)));
                CosmosFunctionComparison.Unsound(judged, keyed, CosmosAxiomTarget.Azure).Should().BeEmpty(row);
            }
        }

        /// <summary>Whether a recorded outcome's key covers a measured atom's, as <see cref="CosmosFunctionComparison.Unsound"/> reads it.</summary>
        static bool Covers(CosmosFunctionOutcome outcome, FunctionKey atom) =>
            FunctionKey.Of(outcome.Name, outcome.Subject, outcome.Operand, outcome.Band, atom.Value) == atom
            && FunctionValueClasses.Holding.Contains((atom.Kind, outcome.Value, atom.Value));

        /// <summary>
        /// A function key another row's measurement ran names a row measured on every target the key claims, whose pinning
        /// test carries its id, and claims no target its own row's run measured: <c>REPEAT</c>'s count of 0 and its result past
        /// 10,000 code points on Azure are <c>Service.ReplicateStopsAtTenThousandCodePoints</c>'s, which no spelling of the
        /// string function row sends. <c>SUBSTRING</c>'s length of 0 was <c>Service.SubstringOfANegativeStartIsEmpty</c>'s
        /// window from 0 of length 0 until run 3 sent the row's own.
        /// </summary>
        [Fact]
        public void AFunctionKeyAnotherRowRanNamesARowMeasuredWhereItClaims()
        {
            var backed = (from axiom in CosmosAxioms.All from o in axiom.FunctionOutcomes ?? [] where o.Evidence is not null select (axiom, o)).ToList();
            backed.Should().NotBeEmpty();
            foreach (var (axiom, o) in backed)
            {
                var evidence = CosmosAxioms.Get(o.Evidence!);
                (evidence.MeasuredOn & o.On).Should().Be(o.On, $"{o} claims only where {o.Evidence} was measured");
                (PinningTest(evidence)?.GetCustomAttributes<CosmosPinsAttribute>().Select(a => a.Id) ?? []).Should().Contain(evidence.Id, evidence.PinnedBy);
                axiom.FunctionOutcomes!.Where(other => other.Evidence is null && (other.On & o.On) != 0
                    && FunctionKey.Of(other.Name, other.Subject, other.Operand, other.Band, other.Value) == FunctionKey.Of(o.Name, o.Subject, o.Operand, o.Band, o.Value))
                    .Should().BeEmpty($"{o} is the key's one row on its targets");
            }
        }

        /// <summary>
        /// A line two runs of the probe account both answered is answered alike by both, so the records read as one: run 3
        /// sent run 1's string functions and run 2's cased letters again beside what it added. A line a run left unanswered
        /// measures nothing (<see cref="CosmosWitnessProbe.Unanswered"/>): run 3's <c>LENGTH(c.v)</c> over <c>"a0b1c"</c>,
        /// which runs 1 and 4 answered 5, and its six searches read from a path, which run 4 answered, the one over U+0000
        /// then 1 failing (<see cref="CosmosWitnessProbe.Failed"/>).
        /// </summary>
        [Fact]
        public void EveryLineTwoProbeRunsSentIsAnsweredAlike()
        {
            var lines = AzureRecordLines().ToList();
            lines.GroupBy(l => (l.Row, l.Expression, l.Witness)).Where(g => g.Select(l => l.Record).Distinct().Count() > 1).Should().NotBeEmpty();
            lines.Where(l => l.Answer != CosmosWitnessProbe.Unanswered).GroupBy(l => (l.Row, l.Expression, l.Witness, Kept(l.Answer))).Where(g => g.Select(l => l.Answer).Distinct().Count() > 1)
                .Select(g => $"{g.Key.Row}: {g.Key.Expression} over {g.Key.Witness}: " + string.Join(", ", g.Select(l => $"{l.Answer} in {l.Record}")))
                .Should().BeEmpty("the probe account answers a line as it did before");
        }

        /// <summary>
        /// The lines of every probe-account record the witness probe wrote, each once however many runs sent it: the row,
        /// the expression, the witness and the answer. A line one run left unanswered and another answered, or failed, is
        /// that answer alone, and one no run answered is <see cref="CosmosWitnessProbe.Unanswered"/>, which measures nothing.
        /// </summary>
        static IEnumerable<(string Row, string Expression, string Witness, string Answer)> AzureWitnessRecords()
        {
            var lines = AzureRecordLines().Select(l => (l.Row, l.Expression, l.Witness, l.Answer)).Distinct().ToList();
            var answered = lines.Where(l => l.Answer != CosmosWitnessProbe.Unanswered).Select(l => (l.Row, l.Expression, l.Witness)).ToHashSet();
            return lines.Where(l => l.Answer != CosmosWitnessProbe.Unanswered || answered.Contains((l.Row, l.Expression, l.Witness)) == false);
        }

        /// <summary>The lines of every probe-account record the witness probe wrote, by record.</summary>
        static IEnumerable<(string Record, string Row, string Expression, string Witness, string Answer)> AzureRecordLines()
        {
            var assembly = typeof(CosmosAxiomsTests).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Runs/", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#').ToList();
                if (lines[0] != WitnessHeader)
                    continue;

                foreach (var f in lines.Skip(1).Select(l => l.Split('\t')))
                {
                    f.Should().HaveCount(5, name);
                    f[0].Should().Be("Azure", name);
                    yield return (name, f[1], f[2], f[3], f[4]);
                }
            }
        }

        /// <summary>The header of a record the witness probe wrote, which <see cref="Records"/> leaves to <see cref="AzureWitnessRecords"/>.</summary>
        const string WitnessHeader = "release\trow\texpression\twitness\tanswer";

        /// <summary>
        /// A row measured on the emulator since the run records began (2026-11-02) names its release in a committed
        /// emulator record (<c>Measurements/Runs/Emulator</c>): every line its pinning test logged, with the release that
        /// answered, so a later release that answers otherwise is read against what this one did.
        /// </summary>
        [Fact]
        public void EveryEmulatorRowSinceTheRecordsBeganHasARecordedRelease()
        {
            var lines = EmulatorRecords().ToList();
            lines.Should().NotBeEmpty();
            lines.Select(l => l.Row).Should().BeSubsetOf(CosmosAxioms.All.Select(a => a.Id));
            lines.Should().OnlyContain(l => Regex.IsMatch(l.Release, "^EN[0-9]{8}$"), "each line names the emulator release that answered it");

            var recorded = lines.Select(l => l.Row).ToHashSet();
            CosmosAxioms.All.Where(a => a.MeasuredOn.HasFlag(CosmosAxiomTarget.Emulator) && a.Measured >= new DateOnly(2026, 11, 2) && a.Id.StartsWith("Service.", StringComparison.Ordinal))
                .Select(a => a.Id).Where(id => recorded.Contains(id) == false).Should().BeEmpty("an emulator row measured since 2026-11-02 has its release's record");
        }

        /// <summary>The lines of every emulator record: the release, the row, the expression, the witness and the answer.</summary>
        static IEnumerable<(string Record, string Release, string Row, string Expression, string Witness, string Answer)> EmulatorRecords()
        {
            var assembly = typeof(CosmosAxiomsTests).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("EmulatorRuns/", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                foreach (var line in reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#').Skip(1))
                {
                    var f = line.Split('\t');
                    f.Should().HaveCount(5, $"{name}: {line}");
                    yield return (name, f[0], f[1], f[2], f[3], f[4]);
                }
            }
        }

        /// <summary>
        /// Whether a line's witnesses hold every one the catalogue has for its cell: its operand's literal's, or, for a row
        /// held over the rounding edges in the literal's place, every edge's (N14).
        /// </summary>
        static bool Witnessed(Class operand, Class cell, string[] witnesses) =>
            new IReadOnlyList<string?>[] { CosmosOutcomeWitnesses.Of(operand, cell), [.. CosmosOutcomeWitnesses.RoundingEdges.SelectMany(edge => CosmosOutcomeWitnesses.Of(operand, cell, edge))] }
                .Any(of => of.Count > 0 && of.All(w => witnesses.Contains(w ?? "absent")));

        /// <summary>
        /// The lines of every record under <c>Measurements/Runs</c>: the record, a row, its key spelled <c>test/operand</c>
        /// or a composite's formula, the cell, the answer and the witnesses.
        /// </summary>
        static IEnumerable<(string Record, string Row, string Key, Class Cell, string Answer, string[] Witnesses)> Records()
        {
            var assembly = typeof(CosmosAxiomsTests).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Runs/", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#').ToList();
                if (lines[0] == WitnessHeader)
                    continue;

                foreach (var line in lines.Skip(1))
                {
                    var f = line.Split('\t');
                    f.Should().HaveCount(7, $"{name}: {line}");
                    var key = f[5].Length > 0 ? f[5] : Enum.Parse<Test>(f[1]) + "/" + Enum.Parse<Class>(f[2]);
                    yield return (name, f[0], key, Enum.Parse<Class>(f[3]), f[4], f[6].Split(' '));
                }
            }
        }

    }

}
