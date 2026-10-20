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

        static readonly Class[] Kinds = [Class.Absent, Class.Null, Class.True, Class.False, Class.Number, Class.String, Class.Object, Class.Array];

        /// <summary>
        /// The keys whose targets answer apart, where both answered. The parted range's keys join it with its two
        /// outcomes; two constants compared, the other known divergence, are no key S reads.
        /// </summary>
        static readonly MeasuredKey[] Disagreements = [];

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

        [Fact]
        public void EveryRowWithOutcomesNamesAPinningTestThatCarriesItsId()
        {
            foreach (var axiom in WithOutcomes)
                (PinningTest(axiom)?.GetCustomAttributes<CosmosPinsAttribute>().Select(a => a.Id) ?? []).Should().Contain(axiom.Id, axiom.PinnedBy);
        }

        /// <summary>
        /// An atomic outcome is a row per cell and per target it names, citing its axiom; a composite is none, and a
        /// vector that names a class twice, or one no enum holds, is refused as the registry is read.
        /// </summary>
        [Fact]
        public void TheReaderWritesARowForEachCellAndTarget()
        {
            const CosmosAxiomTarget both = CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure;
            var axiom = new CosmosAxiom("Service.Planted", "Planted.", both, new(2026, 10, 20), "Planted",
                [new(Test.IsNull, Class.None, both, "absent=false null=true"), new(Test.Not, Class.None, both, "absent=true", "NOT IS_DEFINED(c.v)")]);

            CosmosAxiomRows.Read([axiom]).Should().BeEquivalentTo(
                from target in new[] { RenderingTarget.Emulator, RenderingTarget.Azure }
                from cell in new[] { (Class.Absent, CosmosAnswer.False), (Class.Null, CosmosAnswer.True) }
                select new MeasuredOutcome("Service.Planted", MeasuredKey.Of(Test.IsNull, Class.None, cell.Item1), target, cell.Item2));

            foreach (var vector in new[] { "absent=false absent=true", "nul=true", "absent=1", "absent=True,False", "absent", "" })
                FluentActions.Invoking(() => new CosmosOutcome(Test.IsNull, Class.None, both, vector)).Should().Throw<FormatException>(vector);
        }

        /// <summary>The registry's rows define S, which a contradiction across rows would refuse, and each reads back as written.</summary>
        [Fact]
        public void TheRowsLoadIntoS()
        {
            var rows = CosmosAxiomRows.Read(CosmosAxioms.All);
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(rows));

            foreach (var row in rows)
                theory.Theory.Get(theory.Outcome.Of(row.Key, row.Target)).Should().Be(row.Answer, $"{row.AxiomId} at {row.Key} on {row.Target}");

            GC.KeepAlive(theory);
        }

        /// <summary>
        /// A new divergence between the emulator and Azure is data, not a conflict, so it is declared, or it would pass unseen:
        /// answers that differ and mean nothing in common. What a <c>WHERE</c> alone measured on one, beside an answer within
        /// it on the other, is no divergence.
        /// </summary>
        [Fact]
        public void TheKeysTheTargetsDisagreeOnAreDeclared()
        {
            var answers = CosmosAxiomRows.Read(CosmosAxioms.All).ToLookup(r => (r.Key, r.Target), r => r.Answer);
            ServiceOutcomes Meaning(IEnumerable<CosmosAnswer> each) => each.Aggregate(ServiceOutcomes.None, (m, a) => m | AnswerMeanings.Measured.Of(a));
            var disagreeing = answers.Select(g => g.Key.Key).Distinct().Where(key =>
                answers[(key, RenderingTarget.Emulator)].Any() && answers[(key, RenderingTarget.Azure)].Any()
                && answers[(key, RenderingTarget.Emulator)].ToHashSet().SetEquals(answers[(key, RenderingTarget.Azure)]) == false
                && (Meaning(answers[(key, RenderingTarget.Emulator)]) & Meaning(answers[(key, RenderingTarget.Azure)])) == ServiceOutcomes.None);

            disagreeing.Select(k => k.ToString()).Should().BeEquivalentTo(Disagreements.Select(k => k.ToString()));
        }

        /// <summary>
        /// Per target, every cell of a key some outcome answers there is answered or declared owed, and nothing owed is
        /// answered; and no outcome answers a cell its key is never read at.
        /// </summary>
        [Fact]
        public void TheOwedCellsAreDeclared()
        {
            var rows = CosmosAxiomRows.Read(CosmosAxioms.All);
            rows.Where(r => CellsOf(r.Key.Test, r.Key.Operand, r.Target).Contains(r.Key.Cell) == false).Should().BeEmpty();

            var uncovered = from key in rows.GroupBy(r => (r.Key.Test, r.Key.Operand, r.Target))
                            from cell in CellsOf(key.Key.Test, key.Key.Operand, key.Key.Target).Except(key.Select(r => r.Key.Cell))
                            select $"{MeasuredKey.Of(key.Key.Test, key.Key.Operand, cell)} on {key.Key.Target}";

            uncovered.Should().BeEquivalentTo(CosmosAxiomRows.Owed.Select(o => $"{o.Key} on {o.Target}"));
        }

        /// <summary>
        /// The cells a key is read at: a connective's, Kleene's expectations for the target; a comparison with a string or a
        /// number, a value of its type by its side; an order, whole; a range, by where a value lies; the rest, by kind.
        /// </summary>
        static IEnumerable<Class> CellsOf(Test test, Class operand, RenderingTarget target) => test switch
        {
            Test.Not or Test.And or Test.Or => KleeneTables.Rows.Where(k => k.Item1 == target && k.Item2.Test == test && k.Item2.Operand == operand).Select(k => k.Item2.Cell),
            Test.Order => [Class.None],
            Test.Range => [Class.Between, Class.Outside],
            _ when operand is Class.String or Class.Number => [.. Kinds.Where(k => k != operand), Class.Below, Class.Equal, Class.Above],
            _ => Kinds,
        };

        /// <summary>
        /// A composite's measured answers are what its atoms compose to: read by a copy of the reader's grammar and evaluated
        /// over each witness by <see cref="ServiceLeaf"/> and <see cref="ServiceCompound"/>, on each target it names the
        /// composition holds the answer, and on the emulator, whose every atom here was measured definite, is it. The rows
        /// are the rendering tests', the probe's Kleene rows standing for the connectives no row measures yet.
        /// </summary>
        [Fact]
        public void EveryCompositeIsWhatItsAtomsCompose()
        {
            var path = SqlValue.Spell(JsonDocumentPath.Root.Property("v"));
            var wrong = new List<string>();
            foreach (var axiom in WithOutcomes)
                foreach (var outcome in axiom.Outcomes!.Where(o => o.Formula is not null))
                    foreach (var (_, target) in new[] { (CosmosAxiomTarget.Emulator, RenderingTarget.Emulator), (CosmosAxiomTarget.Azure, RenderingTarget.Azure) }.Where(t => outcome.On.HasFlag(t.Item1)))
                    {
                        var table = ProbeRows.TableOf(ProbeRows.Rows.Where(row => row.Target == target));
                        foreach (var (cell, answer) in outcome.Cells)
                        {
                            var measured = AnswerMeanings.Measured.Of(answer);
                            var cells = new CellPartition(path, ValueSet.From(CosmosOutcomeWitnesses.Of(Class.None, cell).Select(CellOf)));
                            foreach (var composed in Evaluate(Read(outcome.Formula!, path), cells, table).Outcomes.Select(o => o.Outcomes))
                                if ((composed & measured) != measured || target == RenderingTarget.Emulator && composed != measured)
                                    wrong.Add($"{axiom.Id}: {outcome.Formula} at {cell} on {target} is {measured}, composed {composed}");
                        }
                    }

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

        /// <summary>A form over one partition: a constant by its value, a leaf and a connective by the primitives.</summary>
        static OutcomeVector Evaluate(ServiceForm form, CellPartition cells, ServiceTable table) => form.Kind switch
        {
            ServiceFormKind.Constant => new(cells, [.. cells.Cells.Select(_ => new CellOutcome(form.Literal == JsonValues.Of(true) ? ServiceOutcomes.True : ServiceOutcomes.False, Class.None))], ValueSet<string>.Empty),
            ServiceFormKind.Not or ServiceFormKind.And or ServiceFormKind.Or => ServiceCompound.On(form, Evaluate(ServiceForm.First(form), cells, table), Evaluate(ServiceForm.Rest(form), cells, table), table),
            _ => ServiceLeaf.On(form, cells, table, ValueSet<string>.Empty),
        };

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
        /// Every cell of every outcome that names Azure is in a committed run record with the same answer, so an outcome
        /// cannot claim Azure for a witness only the emulator ran; and the records name rows and keys the registry knows.
        /// </summary>
        [Fact]
        public void EveryAzureClaimHasARecordedRun()
        {
            var records = Records().ToList();
            records.Should().NotBeEmpty();
            records.Select(r => r.Row).Should().BeSubsetOf(CosmosAxioms.All.Select(a => a.Id));
            records.Select(r => (r.Row, r.Key, r.Cell)).Should().OnlyHaveUniqueItems();

            var claims = from axiom in WithOutcomes
                         from outcome in axiom.Outcomes!
                         where outcome.On.HasFlag(CosmosAxiomTarget.Azure)
                         from cell in outcome.Cells
                         select (axiom.Id, outcome.Formula ?? outcome.Test + "/" + outcome.Operand, cell.Cell, cell.Answer.ToString());

            claims.Except(records.Select(r => (r.Row, r.Key, r.Cell, r.Answer))).Should().BeEmpty("an outcome claims Azure only where a run on Azure answered it");
        }

        /// <summary>
        /// The lines of every record under <c>Measurements/Runs</c>: a row, its key spelled <c>test/operand</c> or a
        /// composite's formula, the cell and the answer.
        /// </summary>
        static IEnumerable<(string Row, string Key, Class Cell, string Answer)> Records()
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
                    yield return (f[0], key, Enum.Parse<Class>(f[3]), f[4]);
                }
            }
        }

    }

}
