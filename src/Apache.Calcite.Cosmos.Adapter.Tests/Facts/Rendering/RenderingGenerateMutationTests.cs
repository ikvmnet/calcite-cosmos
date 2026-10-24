using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using Op = Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The mutation line in S (spec g06; line 16; gates map §4.3): each of Stage 1's historical faults planted where S would
    /// carry it, in a graph of its own, and Stage 1's triggers shadowed through it under each set a leg plans under. M1–M3
    /// replace a template's row with one writing its form wrongly; M4 and M5 are rules beside the right ones; M6–M10 come
    /// through the definition's <c>Pair</c> and <c>Leaf</c>; M11 is a lift rule. A template is untrusted, so the gate
    /// refutes the planted form at the cell that showed the fault, nothing wrong is sent or implied, and the loss shows as a
    /// regression. The rest are trusted: S sends or implies a wrong form, and source A, which shares none of S, finds its
    /// document.
    /// </summary>
    public sealed class RenderingGenerateMutationTests
    {

        const string Typed = "SELECT c.\"id\" FROM typed AS c WHERE ", Label = "JSON_VALUE(c.\"DOC\", '$.label')", Products = "SELECT c.\"id\" FROM products AS c WHERE ";

        static string Recall(string p) => RenderingRecallTests.Cases[p].Sql;

        /// <summary>
        /// Each plant and its triggers, Stage 1's. An untrusted plant's trigger names the refutation of its planted form at the
        /// atom, Stage 1's pinned cell beneath any <c>NOT</c> swapping its class; a trusted one's, what a witness document
        /// holds at a path, any of the values or kinds joined by <c>or</c>. <c>null</c> is reported only.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, Plant> Plants = new Dictionary<string, Plant>
        {
            ["M1"] = Template("M1", TemplateId.NullTest, [RenderPosition.Pos, RenderPosition.Neg], f => f.Kind == ServiceFormKind.Test && IsTypeTest(f.Test) || f.Kind == ServiceFormKind.Not && f.Members[0] is { Kind: ServiceFormKind.Test } t && IsTypeTest(t.Test),
                [(Recall("P1"), "$[\"metadata\"]=array (T,f)"), (Recall("P19"), "$[\"metadata\"]=array (T,f)"), (Recall("P2"), "$[\"label\"]=array (T,f)"), (Recall("P3"), "$[\"label\"]=array (T,f)"),
                    (Recall("P25"), "$[\"label\"]=array (T,f)")]),
            ["M2"] = Template("M2", TemplateId.Completed, [RenderPosition.Neg], f => f.Kind == ServiceFormKind.Not && f.Members[0] is { Kind: ServiceFormKind.Test } t && IsTypeTest(t.Test),
                [(Recall("P4"), "$[\"label\"]=array (U,f)"), (Recall("P5"), "$[\"label\"]=array (U,f)"), (Recall("P6"), "$[\"label\"]=array (U,f)")]),
            ["M3"] = Template("M3", TemplateId.Restricted, [RenderPosition.Pos], f => f.Kind == ServiceFormKind.Not && f.Members[0] is { Kind: ServiceFormKind.Test, Test: Op.IsNull },
                [(Products + "c.\"$.category\" <> 'bikes'", "$[\"category\"]=null (U,t)"),
                    ("SELECT c.\"$.category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"$.category\" HAVING c.\"$.category\" <> 'bikes'", "$[\"category\"]=null (U,t)")]),
            ["M4"] = new("M4", true, d => d, NegationAtItsOwnPosition,
                [(Products + "NOT (c.\"$.category\" <> 'bikes')", "$[\"category\"]=absent or null"), (Recall("P4"), "$[\"label\"]=array")]),
            ["M5"] = new("M5", true, d => d, ArmsAtPos, [(Recall("P5"), "$[\"category\"]=null"), (Recall("P6"), "$[\"category\"]=null")]),
            ["M6"] = new("M6", true, d => d with { Pair = TrueTextIsFalseOfTrue(d.Pair) }, _ => { }, [(Typed + Label + " = 'true'", "$[\"label\"]=boolean|true"), (Typed + Label + " = '30'", null)]),
            ["M7"] = new("M7", true, d => d with { Pair = ReadAsRaw(d.Pair, PredicateAtom.TextReader) }, _ => { },
                [(Typed + $"CAST({Label} AS VARCHAR) = 'true'", "$[\"label\"]=boolean|true"), (Typed + $"CAST({Label} AS VARCHAR) = '30'", null)]),
            ["M8"] = new("M8", true, d => d with { Leaf = ByRendering(d.Leaf, [Op.NotEqual]) }, _ => { },
                [(Typed + Label + " <> 'true'", "$[\"label\"]=boolean|true"), (Typed + Label + " <> '30'", null), (Typed + Label + " <> 'bikes'", null)]),
            ["M9"] = new("M9", true, d => d with { Leaf = ByRendering(d.Leaf, [Op.Less, Op.LessOrEqual, Op.Greater, Op.GreaterOrEqual]) }, _ => { },
                [(Typed + Label + " > 'bikes'", "$[\"label\"]=boolean|false or boolean|true"), (Typed + Label + " < 'bikes'", null), (Typed + Label + " >= '30'", null)]),
            ["M10"] = new("M10", true, d => d with { Pair = ReadAsRaw(d.Pair, PredicateAtom.TypedReader("DECIMAL")) }, _ => { },
                // Source A's strings the clause parses as 2, "2" and "2", stand for Stage 1's "2".
                [("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = 2", "$[\"body\"]=string")]),
            ["M11"] = new("M11", true, d => d, StringFormOfANumbersText,
                [(Typed + Label + " = '30'", "$[\"label\"]=number"), (Typed + $"CAST({Label} AS VARCHAR) = '30'", "$[\"label\"]=number"),
                    ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" = '30'", "$[\"label\"]=number")]),
        };

        public static TheoryData<string> Ten => [.. Enumerable.Range(1, 10).Select(i => "M" + i)];

        static readonly ConcurrentDictionary<string, Lazy<Planted>> Runs = new(StringComparer.Ordinal);

        /// <summary>Every trigger shadowed unplanted, once a process.</summary>
        internal static readonly Lazy<Planted> Tip = new(() => Shadowed(new("tip", true, d => d, _ => { }, [.. Plants.Values.SelectMany(p => p.Triggers).Select(t => (t.Sql, (string?)null)).Distinct()])));

        /// <summary>
        /// A plant is caught. Untrusted, it proposed its forms, each trigger it was found by refutes the planted form at the
        /// cell pinned, no root sends a refuted planted form, and source A finds nothing S sends or implies wrong. Trusted,
        /// each trigger pinned has a witness at the value pinned under a set a leg plans under.
        /// </summary>
        [Theory]
        [MemberData(nameof(Ten))]
        public void IsCaught(string fault) => Caught(Run(fault));

        /// <summary>
        /// M11 (8d57df0), beside the ten: a text equality with a literal a number renders as, weakened to its string form
        /// alone, keeps the string and loses the number 30, which Calcite renders so. Source A's Implies finds it.
        /// </summary>
        [Fact]
        public void M11IsCaughtBySourceAsImplies() => Caught(Run("M11"));

        /// <summary>
        /// Unplanted, S sends nothing wrong over every trigger, and what each trusted plant sends or implies is not what the
        /// tip does somewhere among its triggers: the fault fired.
        /// </summary>
        [Fact]
        public void TheTipIsWrongOnNoTriggerAndEveryTrustedPlantFires()
        {
            var tip = Tip.Value;
            tip.Run.Failures.Should().BeEmpty();
            tip.Triggers.SelectMany(t => t.Judged.Values).SelectMany(j => j.Witnesses).Should().BeEmpty();

            var sent = Sent(tip);
            foreach (var plant in Plants.Values.Where(p => p.Trusted))
            {
                var changed = Sent(Run(plant.Name)).Except(sent).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{plant.Name}: {changed.Count} forms sent or implied the tip does not; {string.Join("; ", changed.Take(3))}");
                changed.Should().NotBeEmpty(plant.Name);
            }
        }

        /// <summary>A plant's run, once a process: its triggers shadowed through it.</summary>
        internal static Planted Run(string fault) => Runs.GetOrAdd(fault, f => new Lazy<Planted>(() => Shadowed(Plants[f]))).Value;

        static void Caught(Planted planted)
        {
            var (plant, output) = (planted.Plant, TestContext.Current.TestOutputHelper);
            planted.Run.Failures.Should().BeEmpty();
            foreach (var t in planted.Triggers)
            {
                output?.WriteLine($"{plant.Name} {t.Sql}");
                foreach (var c in t.Classes)
                    output?.WriteLine($"  {c.Set} {c.Ir}: {c.Class} {c.Reason}; S {c.Form?.Text}");
                foreach (var (set, judged) in t.Judged)
                    output?.WriteLine($"  {set}: {judged}; " + string.Join(" | ", judged.Witnesses.Take(2)));
                foreach (var r in Refuted(planted, t).Select(r => $"{r.Node} at {r.Position}: {r.Form.Text} refuted at {Show(r.Refutation)}").Distinct())
                    output?.WriteLine("  planted: " + r);
            }

            if (plant.Trusted == false)
            {
                plant.Proposed.Should().NotBeEmpty("the plant proposed its forms");
                foreach (var t in planted.Triggers.Where(t => t.Caught is not null))
                    Refuted(planted, t).Select(r => Show(r.Refutation)).Distinct().Should().Equal([t.Caught], t.Sql);

                var refuted = planted.Triggers.SelectMany(t => Refuted(planted, t)).Select(r => r.Form).ToHashSet();
                planted.Triggers.SelectMany(t => t.Bundles).SelectMany(b => b.Answers.Values).Select(a => a.Best).OfType<ServiceForm>()
                    .Where(best => best.Subforms.Any(refuted.Contains)).Select(best => best.Text).Should().BeEmpty("no root sends a form the gate refuted");
                planted.Triggers.SelectMany(t => t.Judged.Values).SelectMany(j => j.Witnesses).Should().BeEmpty("nothing S sends or implies is wrong");
                planted.Triggers.SelectMany(t => t.Classes).Should().Contain(c => c.Class == RenderingGenerateRun.Regression, "the loss shows as a regression");
                return;
            }

            foreach (var t in planted.Triggers.Where(t => t.Caught is not null))
            {
                var caught = t.Caught!;
                var (path, values) = (caught[..caught.IndexOf('=')], caught[(caught.IndexOf('=') + 1)..].Split(" or "));
                t.Judged.Values.SelectMany(j => j.Found).Should().Contain(f => values.Contains(Spelled(CosmosEval.At(f.Document, path))) || values.Contains(KindOf(CosmosEval.At(f.Document, path))),
                    $"{plant.Name} over {t.Sql}");
            }
        }

        /// <summary>Each refutation of a form the plant proposed, among a trigger's bundles.</summary>
        static IEnumerable<RenderingGenerateRun.Refuted> Refuted(Planted planted, Trigger t) =>
            t.Bundles.SelectMany(b => b.Refutations ?? []).Where(r => planted.Plant.Proposed.ContainsKey(r.Form));

        /// <summary>What a run sends or implies of each trigger's conjuncts under each set: the forms admitted, weakenings and implied members.</summary>
        static HashSet<string> Sent(Planted planted) => [.. planted.Triggers.SelectMany(t => t.Classes.Select(c => $"{c.Set} {t.Sql} {c.Ir}: sends {c.Form?.Text}")
            .Concat(t.Bundles.SelectMany(b => b.Forms.Where(f => f.What != RenderingGenerateRun.Judged.Agreement).Select(f => $"{b.Set} {t.Sql} {f.Node}: {f.What} at {f.Position} {f.Form.Text}"))))];

        /// <summary>
        /// Plans a plant's triggers, as the differential plans them, with its graph shadowing each under the sets the legs
        /// plan under, and holds each trigger's generate bundles to source A under each set.
        /// </summary>
        static Planted Shadowed(Plant plant)
        {
            var graph = Graph(plant.Define, plant.Rules);
            var statements = plant.Triggers.Select(t => t.Sql).ToList();
            var run = Shadowed(graph, "mutation " + plant.Name, statements);
            var shadow = run.Shadow!;
            var triggers = new List<Trigger>();
            foreach (var (sql, caught) in plant.Triggers)
            {
                var name = statements.IndexOf(sql).ToString(CultureInfo.InvariantCulture);
                var ids = shadow.Admissions.Where(a => a.Name == name).Select(a => a.Bundle).Concat(shadow.Weakenings.Where(w => w.Name == name).Select(w => w.Bundle ?? 0)).ToHashSet();
                var bundles = shadow.Bundles.Where(b => ids.Contains(b.Id)).ToList();
                var judged = new Dictionary<RenderingSet, RenderingSoundnessTests.Tally>();
                foreach (var set in RenderingGenerateRun.AssertedSets)
                    RenderingSoundnessTests.Generated(judged[set] = new RenderingSoundnessTests.Tally(), bundles.Where(b => b.Set == set), $"{plant.Name} {set}");

                triggers.Add(new(sql, caught, bundles, judged, [.. shadow.Classes.Where(c => c.Name == name).OrderBy(c => c.Set).ThenBy(c => c.Statement).ThenBy(c => c.Conjunct)]));
            }

            GC.KeepAlive(graph);
            return new(plant, run, triggers);
        }

        /// <summary>A graph of a plant's own, never spent, defined from the registry's definition as changed and holding the rules given beside the right ones.</summary>
        internal static RenderingGraph Graph(Func<RenderingDefinition, RenderingDefinition> define, Action<RenderingTheory> rules)
        {
            var graph = new RenderingGraph(() => define(ProbeRows.Measured), _ => false);
            rules(graph.Current());
            return graph;
        }

        /// <summary>Statements planned as the differential plans them, shadowed through a graph under the sets the legs plan under.</summary>
        internal static RenderingCheckRun Shadowed(RenderingGraph graph, string input, IReadOnlyList<string> statements) =>
            RenderingCheckRun.Over(RenderingInputs.Differential(input, statements, RenderingInputs.Containers), shadow: new RenderingGenerateRun(graph, RenderingGenerateRun.AssertedSets));

        /// <summary>A planted best: a negation's operand's best at the position <paramref name="at"/> gives, negated, as the negation's.</summary>
        internal static void NegationBestFrom(RenderingTheory t, string name, Func<Var<RenderPosition>, Term<RenderPosition>> at) =>
            t.Graph.Rulesets.Single(r => r.Name == "best").DefineRule(name, r =>
            {
                var (e, a, pos) = r.Vars<Pred, Pred, RenderPosition>("e", "a", "pos");
                var (b, g, h, pa) = r.Vars<Priced, ServiceForm, ServiceForm, Proof>("b", "g", "h", "pa");
                r.When(t.At.Of(e, pos), e.Is(t.Ir.Not.Of(a)), b.Is(t.BestExact.Of(a, at(pos))));
                r.Where(RenderingPrimitives.PricedIsReal.Of(b));
                r.When(g.Is(Compute.Of(b, Priced.FormOf)), pa.Is(t.Agrees.Of(a, at(pos), g, Verdict.Equal)), h.Is(Compute.Of(g, ServiceForm.Strip)));
                r.Then(t.Agrees.Of(e, pos, h, Verdict.Equal).Is(pa), t.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

        /// <summary>
        /// An untrusted plant: a template's rows at the positions given replaced by a rule that proposes, of every shape the
        /// template fits, its form with the members the predicate names taken out.
        /// </summary>
        static Plant Template(string name, TemplateId replaced, RenderPosition[] at, Func<ServiceForm, bool> dropped, (string, string?)[] triggers)
        {
            Plant? plant = null;
            return plant = new(name, false, d => d with { Templates = [.. d.Templates.Where(row => row.Item1 != replaced || at.Contains(row.Item2) == false)] }, t =>
                t.Graph.Rulesets.Single(r => r.Name == "spell").DefineRule($"planted {name}: {replaced} at {string.Join(" and ", at)}, its form written wrongly", r =>
                {
                    var (e, p, a, pos) = r.Vars<Pred, JPath, PredicateAtom, RenderPosition>("e", "p", "a", "pos");
                    var (n, sh) = r.Vars<string, string>("n", "sh");
                    r.When(t.At.Of(e, pos), t.Ir.Atom.Of(e, p, a), n.Is(t.Spelling.Of(p)), sh.Is(Compute.Of(a, RenderingTemplates.ShapeOf)), t.Fits.Of(replaced, sh));
                    r.Where(pos, x => at.Contains(x));
                    r.Then(t.Candidate.Of(e, pos, Compute.Of(pos, a, n, (q, x, y) => plant!.Propose(Drop(RenderingTemplates.Spell(replaced, q, x, y), dropped)))));
                }), triggers);
        }

        static bool IsTypeTest(Op test) => test is Op.IsPrimitive or Op.IsNumber or Op.IsBool;

        /// <summary>A form with each member of a junction the predicate names taken out, at any depth; a junction left one member is that member.</summary>
        internal static ServiceForm Drop(ServiceForm form, Func<ServiceForm, bool> dropped) => form.Kind switch
        {
            ServiceFormKind.Not => ServiceForm.NotOf(Drop(form.Members[0], dropped)),
            ServiceFormKind.And or ServiceFormKind.Or => form.Members.Where(m => dropped(m) == false).Select(m => Drop(m, dropped)).ToList() switch
            {
                [var one] => one,
                var rest => ServiceForm.JunctionOf(form.Test, rest),
            },
            _ => form,
        };

        /// <summary>M4: a negation's operand also stands at the negation's own position, and its best there, negated, is the negation's.</summary>
        static void NegationAtItsOwnPosition(RenderingTheory t)
        {
            t.Graph.Rulesets.Single(r => r.Name == "at").DefineRule("planted M4: a negation's operand stands at its own position", r =>
            {
                var (e, a, pos) = r.Vars<Pred, Pred, RenderPosition>("e", "a", "pos");
                r.When(t.At.Of(e, pos), e.Is(t.Ir.Not.Of(a)));
                r.Then(t.At.Of(a, pos), t.BestExact.Of(a, pos).Is(Priced.None), t.Implied.Of(a, pos).Is(ValueSet<ServiceForm>.Empty));
            });
            NegationBestFrom(t, "planted M4: a negation's best is its operand's best at its own position, negated", pos => pos);
        }

        /// <summary>M5: a junction's arms also stand at <c>Pos</c>, and their bests there make the junction's, whatever its position.</summary>
        static void ArmsAtPos(RenderingTheory t)
        {
            t.Graph.Rulesets.Single(r => r.Name == "at").DefineRule("planted M5: a junction's arms stand at Pos", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, Op>("pos", "j");
                r.When(t.At.Of(e, pos), t.Junction.Of(e, x, y, j));
                r.Then(t.At.Of(x, RenderPosition.Pos), t.BestExact.Of(x, RenderPosition.Pos).Is(Priced.None), t.Implied.Of(x, RenderPosition.Pos).Is(ValueSet<ServiceForm>.Empty));
            });
            t.Graph.Rulesets.Single(r => r.Name == "best").DefineRule("planted M5: a junction's best is the join of its arms' bests at Pos, whatever its position", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, Op>("pos", "j");
                var (bx, by) = r.Vars<Priced, Priced>("bx", "by");
                var (l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("l", "m", "h");
                var (px, py) = r.Vars<Proof, Proof>("px", "py");
                r.When(t.At.Of(e, pos), t.First.Of(e, x), t.Junction.Of(e, x, y, j), bx.Is(t.BestExact.Of(x, RenderPosition.Pos)), by.Is(t.BestExact.Of(y, RenderPosition.Pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bx));
                r.Where(RenderingPrimitives.PricedIsReal.Of(by));
                r.When(l.Is(Compute.Of(bx, Priced.FormOf)), m.Is(Compute.Of(by, Priced.FormOf)), px.Is(t.Agrees.Of(x, RenderPosition.Pos, l, Verdict.Equal)),
                    py.Is(t.Agrees.Of(y, RenderPosition.Pos, m, Verdict.Equal)), h.Is(Compute.Of(j, l, m, ServiceForm.Joined)));
                r.Then(t.Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(px, py, Proof.Join)), t.BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });
        }

        /// <summary>M6: text equal to <c>'true'</c> read as FALSE of the boolean true, which Calcite renders so.</summary>
        static Func<PredicateAtom, OutcomeVector, AnswerMeanings, Truths, CellOutcomes> TrueTextIsFalseOfTrue(Func<PredicateAtom, OutcomeVector, AnswerMeanings, Truths, CellOutcomes> pair) =>
            (atom, vector, means, tolerated) => pair(atom, vector, means, tolerated) is var outcomes && atom is { Reader: PredicateAtom.TextReader, Test: PredicateAtom.Equal } && atom.Operand == JsonValues.Of("true")
                ? Reclassed(outcomes, cell => cell is PathCell.Point { Value: var v } && v == JsonValues.Of(true), k => k with { Calcite = Truths.False })
                : outcomes;

        /// <summary>M7 and M10: a reading Calcite's own (the text accessor's rendering, a <c>RETURNING DECIMAL</c>) read as the raw value.</summary>
        static Func<PredicateAtom, OutcomeVector, AnswerMeanings, Truths, CellOutcomes> ReadAsRaw(Func<PredicateAtom, OutcomeVector, AnswerMeanings, Truths, CellOutcomes> pair, string reader) =>
            (atom, vector, means, tolerated) => pair(atom.Reader == reader ? atom with { Reader = PredicateAtom.RawReader } : atom, vector, means, tolerated);

        /// <summary>M8 and M9: the service's comparison of a boolean with a string, for the operators given, answered by the boolean's rendering.</summary>
        static Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> ByRendering(Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> leaf, Op[] ops) =>
            (form, cells, table, parted) => leaf(form, cells, table, parted) is var vector && form.Kind == ServiceFormKind.Compare && ops.Contains(form.Test) && JsonValues.IsString(form.Literal!)
                ? vector with
                {
                    Outcomes = [.. vector.Outcomes.Zip(cells.Cells, (o, cell) => cell is PathCell.Point { Value: var v } && JsonValues.TypeOf(v) == JsonValues.BooleanType
                        ? o with { Outcomes = Holds(form.Test, string.CompareOrdinal(v == JsonValues.Of(true) ? "true" : "false", JsonValues.StringOf(form.Literal!))) ? ServiceOutcomes.True : ServiceOutcomes.False }
                        : o)],
                }
                : vector;

        static bool Holds(Op op, int order) => op switch
        {
            Op.NotEqual => order != 0,
            Op.Less => order < 0,
            Op.LessOrEqual => order <= 0,
            Op.Greater => order > 0,
            _ => order >= 0,
        };

        /// <summary>M11: a text equality with a literal a number renders as implies its string form, with no agreement.</summary>
        static void StringFormOfANumbersText(RenderingTheory t) =>
            t.Graph.Rulesets.Single(r => r.Name == "lift").DefineRule("planted M11: a text equality with a number's text implies its string form", r =>
            {
                var (e, p, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "p", "a");
                var n = r.Var<string>("n");
                r.When(t.At.Of(e, RenderPosition.Pos), t.Ir.Atom.Of(e, p, a), n.Is(t.Spelling.Of(p)));
                r.Where(a, x => x is { Reader: PredicateAtom.TextReader, Test: PredicateAtom.Equal, Operand: { } l } && JsonValues.IsString(l)
                    && decimal.TryParse(JsonValues.StringOf(l), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                r.Then(t.Implied.Of(e, RenderPosition.Pos).Is(Primitives.Singleton<ServiceForm>().Of(Compute.Of(a, n, (x, y) => ServiceForm.CompareOf(Op.Equal, y, x.Operand!)))));
            });

        /// <summary>Cell outcomes with the class of each cell named changed.</summary>
        internal static CellOutcomes Reclassed(CellOutcomes outcomes, Func<PathCell, bool> which, Func<CellClass, CellClass> to)
        {
            var cells = outcomes.Cells.Select(at => which(at.Cell) ? at with { Class = to(at.Class) } : at).ToList();
            return outcomes with { Cells = ValueSet.From(cells), Classes = ValueSet.From(cells.Select(c => c.Class)) };
        }

        /// <summary>A verdict as the triggers pin it: a refutation's cells and class, or <c>Exact</c>, or its kind.</summary>
        internal static string Show(RenderingVerdict verdict) => verdict switch
        {
            { Refutation: { } r } => Show(r),
            { Kind: RenderingVerdictKind.Proven } => "Exact",
            _ => verdict.Kind.ToString(),
        };

        /// <summary>A refutation spelled as the triggers pin it: its cells and class.</summary>
        internal static string Show(Refutation refutation) => string.Join("; ", refutation.Cells.AsEnumerable()) + " " + refutation.Class;

        /// <summary>What a document holds at a path, spelled as the triggers pin it: a scalar's value, or its kind.</summary>
        static string Spelled(J? value) => value switch
        {
            null => "absent",
            J.Null => "null",
            J.Bool b => JsonValues.Of(b.Value),
            J.Str s => JsonValues.Of(s.Value),
            J.Num or J.Huge => "number",
            J.Arr => "array",
            _ => "object",
        };

        static string KindOf(J? value) => value switch { J.Str => "string", J.Bool => "boolean", _ => Spelled(value) };

        /// <summary>
        /// A fault planted in S: its definition, the rules beside the right ones in its graph, whether it is trusted, and the
        /// triggers it was found by; an untrusted one keeps each form it proposed.
        /// </summary>
        internal sealed record Plant(string Name, bool Trusted, Func<RenderingDefinition, RenderingDefinition> Define, Action<RenderingTheory> Rules, (string Sql, string? Caught)[] Triggers)
        {

            public ConcurrentDictionary<ServiceForm, byte> Proposed { get; } = new();

            /// <summary>Keeps a form the plant proposed, and returns it.</summary>
            public ServiceForm Propose(ServiceForm form)
            {
                Proposed.TryAdd(form, 0);
                return form;
            }

        }

        /// <summary>A plant's run: its triggers planned and shadowed.</summary>
        internal sealed record Planted(Plant Plant, RenderingCheckRun Run, IReadOnlyList<Trigger> Triggers);

        /// <summary>A trigger shadowed: its generate bundles, source A over them under each set, and each conjunct's class.</summary>
        internal sealed record Trigger(string Sql, string? Caught, IReadOnlyList<RenderingGenerateRun.Bundle> Bundles, IReadOnlyDictionary<RenderingSet, RenderingSoundnessTests.Tally> Judged,
            IReadOnlyList<RenderingGenerateRun.Classed> Classes);

    }

}
