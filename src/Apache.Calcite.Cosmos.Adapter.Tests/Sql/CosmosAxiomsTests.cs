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
            // The emulator matches an affix against the string it stores, Azure against the string as written (run 1).
            MeasuredKey.Of(Test.StartsWith, Class.String, Class.None), MeasuredKey.Of(Test.EndsWith, Class.String, Class.None), MeasuredKey.Of(Test.Contains, Class.String, Class.None)];

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
            foreach (var axiom in CosmosAxioms.All.Where(a => a.Outcomes is not null || a.Subjects is not null || a.FunctionOutcomes is not null || a.Columns is not null || a.StoredAs is not null))
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
            CosmosAxiomRows.ReadFunctionSubjects(CosmosAxioms.All).Should().Contain(("POSITION", 1L)).And.Contain(("CONCAT", FunctionKey.Every)).And.OnlyHaveUniqueItems();

            // A row's band and class of value are read into its key (F1, F2).
            var banded = CosmosAxioms.All.First(a => a.Outcomes is not null) with
            {
                Outcomes = [new CosmosOutcome(CosmosServiceTest.Contains, Class.String, CosmosAxiomTarget.Azure, "string=true", Band: JsonCharacterBand.Nul)],
                FunctionOutcomes = [new CosmosFunctionOutcome("REPLACE", 1, Class.String, JsonCharacterBand.None, CosmosAxiomTarget.Azure, CosmosFunctionAnswer.Undefined, FunctionValueClass.Empty)],
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
            var outcome = new CosmosFunctionOutcome("F", 0, Class.String, JsonCharacterBand.None, CosmosAxiomTarget.Emulator, CosmosFunctionAnswer.Same);
            var (one, two) = (row with { FunctionOutcomes = [outcome, outcome with { Band = JsonCharacterBand.Nul }], Readers = ["a", "b"] }, row with { FunctionOutcomes = [outcome], Readers = ["b"] });
            CosmosAxiomRows.ReadFunctionReads([one, two]).Select(r => (r.Function, string.Join(",", r.Readers))).Should().Equal(("F", "a,b"), ("F", "b"));
        }

        /// <summary>
        /// The classes a written string is stored holding are a <c>Service</c> row's cells, read alike from every target the
        /// row names, since the rules of JSON have none: a lone surrogate written, the high band and U+FFFD within it.
        /// </summary>
        [Fact]
        public void TheStoredClassesAreReadOffTheirRow()
        {
            CosmosAxiomRows.ReadStored(CosmosAxioms.All).Should().BeEquivalentTo(new[]
            {
                new JsonClassMap(JsonValues.SurrogateBand, JsonValues.HighBand), new JsonClassMap(JsonValues.SurrogateBand, JsonValues.ReplacementClass),
            });
            foreach (var axiom in CosmosAxioms.All.Where(a => a.StoredAs is not null))
                foreach (var stored in axiom.StoredAs!)
                    (stored.On != 0 && (stored.On & ~axiom.MeasuredOn) == 0).Should().BeTrue($"{axiom.Id}'s {stored.Written}={stored.Stored} names {stored.On}, within {axiom.MeasuredOn}");
            global::Apache.Calcite.Cosmos.Adapter.Metadata.CosmosReasoning.Stored.Should().Equal(CosmosAxiomRows.ReadStored(CosmosAxioms.All));
        }

        /// <summary>
        /// The raw columns S takes the union of are a <c>Calcite</c> row's, read through the registry one row a column a key
        /// (h12): every key the raw reading and its cast ask, a column raw beside strings alone only beside a string, and each
        /// answer as the row states it.
        /// </summary>
        [Fact]
        public void TheRawColumnsAreACalciteRowsReadOneRowAColumnAKey()
        {
            const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;
            var rows = CosmosAxiomRows.ReadColumns(CosmosAxioms.All);
            rows.Select(r => (r.Key, r.Column)).Should().OnlyHaveUniqueItems();
            rows.GroupBy(r => r.Column).ToDictionary(g => g.Key, g => g.Count()).Should().Equal(new Dictionary<string, int> { ["VARIANT"] = 294, ["ANY"] = 294, ["VARCHAR"] = 84 });

            var cast = PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader);
            Truths At(string reader, Class kind, Test test, Class operand, string column) =>
                rows.Single(r => r.Key == RawKey.Of(reader, kind, test, operand) && r.Column == column).Truths;

            (At("raw", Class.Number, Test.Equal, Class.String, "VARIANT"), At("raw", Class.Number, Test.Equal, Class.String, "VARCHAR")).Should().Be((F, R));
            (At("raw", Class.Number, Test.Less, Class.Number, "VARIANT"), At("raw", Class.Number, Test.Less, Class.Number, "ANY")).Should().Be((R, T | F));
            (At("raw", Class.True, Test.Equal, Class.False, "ANY"), At("raw", Class.Object, Test.Greater, Class.String, "ANY")).Should().Be((T | F, R));
            (At("raw", Class.Null, Test.Equal, Class.String, "ANY"), At("raw", Class.Null, Test.IsNull, Class.None, "VARCHAR")).Should().Be((U, T));
            (At(cast, Class.Number, Test.Equal, Class.String, "VARIANT"), At(cast, Class.Number, Test.Equal, Class.String, "ANY"), At(cast, Class.Object, Test.IsNull, Class.None, "VARIANT"))
                .Should().Be((U, T | F, R));
            rows.Should().NotContain(r => r.Column == "VARCHAR" && r.Key.Reader == "raw" && r.Key.Test != Test.IsNull && r.Key.Operand != Class.String);
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
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(rows));

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
                .ToDictionary(t => t.Item1, t => ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(t.Item2))));
            var wrong = from axiom in WithOutcomes
                        from outcome in axiom.Outcomes!
                        where outcome.Formula is not null
                        from target in tables.Keys
                        where outcome.On.HasFlag(target)
                        from cell in outcome.Cells
                        let measured = AnswerMeanings.Measured.Of(cell.Answer)
                        let cells = new CellPartition(path, ValueSet.From(CosmosOutcomeWitnesses.Of(Class.None, cell.Cell).Select(CellOf)))
                        from composed in CellOutcomesTests.Evaluate(Read(outcome.Formula!, path), cells, tables[target], ValueSet<string>.Empty).Outcomes.Select(o => o.Outcomes)
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
            // emulator record holds, each answered alike but where the row says the two part.
            var emulator = EmulatorRecords().Where(l => l.Release == "EN20261019").ToLookup(l => l.Row);
            var azure = AzureWitnessRecords().ToLookup(l => l.Row);
            var claiming = CosmosAxioms.All.Where(a => a.MeasuredOn.HasFlag(CosmosAxiomTarget.Azure) && emulator.Contains(a.Id)).Select(a => a.Id).ToList();
            claiming.Should().BeEquivalentTo(azure.Select(g => g.Key), "a row the probe account ran claims Azure, and one it did not claims nothing");
            foreach (var row in claiming)
            {
                var asked = azure[row].ToDictionary(l => (l.Expression, l.Witness, Kept(l.Answer)), l => l.Answer);
                emulator[row].Select(l => (l.Expression, l.Witness, Kept(l.Answer))).Except(asked.Keys).Should().BeEmpty($"{row}: the probe account ran every line the emulator did");
                emulator[row].Where(l => asked.TryGetValue((l.Expression, l.Witness, Kept(l.Answer)), out var a) && a != l.Answer && Parted(row, l.Expression, l.Witness) == false)
                    .Select(l => $"{l.Expression} over {l.Witness}: {l.Answer} on the emulator, {asked[(l.Expression, l.Witness, Kept(l.Answer))]} on Azure").Should().BeEmpty($"{row} answers alike but where it says the targets part");
            }
        }

        /// <summary>Whether a witness probe's answer is <c>value/kept</c>, not a projection alone.</summary>
        static bool Kept(string answer) => answer.Length >= 2 && answer[^2] == '/' && "TFU!?".Contains(answer[^1]);

        /// <summary>
        /// Where a row measured by the witness probe says the targets part: a string holding U+0000 ordered against another
        /// (<c>Service.StringOrderIsCodePoint</c>), 9007199254740993.0 held, % past 2^53, and the string functions, whose
        /// keys are each target's own (<see cref="EveryAzureFunctionOutcomeIsWhatTheProbeAccountsRecordMeasures"/>).
        /// </summary>
        static bool Parted(string row, string expression, string witness) => row switch
        {
            "Service.TwoPathsCompareAsAPathAndALiteral" => witness.Contains("\\u0000", StringComparison.Ordinal) && witness.Contains("\"w\":\"", StringComparison.Ordinal),
            "Service.IntegralNumbersReadBackAsHeld" => witness == "9007199254740993.0",
            "Service.IntegerArithmeticIsExactWithinLong" => expression.Contains('%', StringComparison.Ordinal),
            "Service.StringFunctionAnswersByBand" => true,
            _ => false,
        };

        /// <summary>
        /// A function outcome claims Azure only where the probe account's record holds its key's lines: those lines read
        /// again through Calcite in process answer each key as the row records it on Azure, or better.
        /// </summary>
        [Fact]
        public void EveryAzureFunctionOutcomeIsWhatTheProbeAccountsRecordMeasures()
        {
            var measured = new (string Row, IReadOnlyList<(string Function, string Calcite, string Service)> Functions, IReadOnlyList<(Class Kind, JsonCharacterBand Band, string?[] Witnesses)> Keys)[]
            {
                ("Service.StringFunctionAnswersByBand", CosmosStringFunctionMeasurementTests.Functions, CosmosStringFunctionMeasurementTests.FunctionWitnesses),
                ("Service.IntegerArithmeticIsExactWithinLong", CosmosJsonTypeMeasurementTests.Arithmetic, CosmosJsonTypeMeasurementTests.ArithmeticWitnesses),
            };

            CosmosAxioms.All.Where(a => a.FunctionOutcomes?.Any(o => o.On.HasFlag(CosmosAxiomTarget.Azure)) == true).Select(a => a.Id)
                .Should().BeSubsetOf(measured.Select(m => m.Row), "a function key claims Azure only where a committed run holds its answer");

            var lines = AzureWitnessRecords().Where(l => Kept(l.Answer) == false).ToDictionary(l => (l.Row, l.Expression, l.Witness), l => l.Answer);
            foreach (var (row, functions, keys) in measured)
            {
                var asked = CosmosFunctionComparison.Spellings(functions)
                    .Select(spelling => keys.SelectMany(k => k.Witnesses).Select(w => lines[(row, spelling, w ?? "absent")]).ToArray()).ToArray();
                CosmosFunctionComparison.Unsound(CosmosFunctionComparison.Judge(row, functions, keys, asked), CosmosAxioms.Get(row).FunctionOutcomes!, CosmosAxiomTarget.Azure)
                    .Should().BeEmpty(row);
            }
        }

        /// <summary>The lines of every probe-account record the witness probe wrote: the row, the expression, the witness and the answer.</summary>
        static IEnumerable<(string Row, string Expression, string Witness, string Answer)> AzureWitnessRecords()
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
                    yield return (f[1], f[2], f[3], f[4]);
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
