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
        static readonly MeasuredKey[] Disagreements = [MeasuredKey.Of(Test.Range, Class.Parted, Class.Between), MeasuredKey.Of(Test.Order, Class.String, Class.None)];

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

        /// <summary>A row read as data, by its outcomes or its subjects, names a test that says it pins the row.</summary>
        [Fact]
        public void EveryRowReadAsDataNamesAPinningTestThatCarriesItsId()
        {
            foreach (var axiom in CosmosAxioms.All.Where(a => a.Outcomes is not null || a.Subjects is not null))
                (PinningTest(axiom)?.GetCustomAttributes<CosmosPinsAttribute>().Select(a => a.Id) ?? []).Should().Contain(axiom.Id, axiom.PinnedBy);
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
            var claims = (from axiom in WithOutcomes
                          from outcome in axiom.Outcomes!
                          where outcome.On.HasFlag(CosmosAxiomTarget.Azure)
                          from cell in outcome.Cells
                          select (axiom.Id, outcome.Formula ?? outcome.Test + "/" + outcome.Operand, cell.Cell, cell.Answer)).ToList();

            // A line is claimed by its answer, or by a later run's that means nothing more, as a WHERE NOT read beside a NotKept.
            claims.Except(claimable).Should().BeEmpty("an outcome claims Azure only where a run read the cell as its test does");
            claimable.Where(line => claims.Any(c => (c.Item1, c.Item2, c.Cell) == (line.Row, line.Key, line.Cell)
                && (c.Answer == line.Answer || AnswerMeanings.Measured.Of(c.Answer) is var meant and not ServiceOutcomes.None
                    && (meant & ~AnswerMeanings.Measured.Of(line.Answer)) == ServiceOutcomes.None)) == false).Should().BeEmpty("no evidence a run read so is dropped");
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
                foreach (var line in reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#').Skip(1))
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
