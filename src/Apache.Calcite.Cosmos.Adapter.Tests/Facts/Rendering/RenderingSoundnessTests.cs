using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The soundness line (spec §5.5, §6): no proof the check run reports has a witness. Source A samples documents from
    /// each proven check's import; source B plans population D's conjuncts over a container their declaration describes and
    /// holds every proof to the declaration's own documents. Each is judged by <see cref="IrEval"/> against
    /// <see cref="CosmosEval"/> on every target the asserted set serves; a document <c>IrEval</c> tossed a coin on is
    /// unmeasured, and one every typed mode raises on is skipped (decision 2).
    /// </summary>
    public sealed class RenderingSoundnessTests
    {

        /// <summary>Source B's trials every pull request pays, of §10.3's 20 to 50.</summary>
        public const int Trials = 30;

        public const int LongTrials = 5_000;

        static readonly IrEval.TypedAs[] Modes = [IrEval.TypedAs.Returning, IrEval.TypedAs.Column];

        /// <summary>The container's hierarchical key, the paths a raw value can be read at: only a key path is a column.</summary>
        static readonly string[] Keys = ["kind", "a", "n"];

        /// <summary>Every proof the pull request's run reports, held to source A's documents, and source B's trials.</summary>
        [Fact]
        public void NoReportedProofHasAWitness()
        {
            var (a, b) = (SourceA(RenderingCheckRun.PerPullRequest.Value), SourceB(Trials));
            TestContext.Current.TestOutputHelper?.WriteLine($"source A: {a}\nsource B: {b}");
            (a.Proofs, b.Proofs).Should().Match<(int, int)>(p => p.Item1 > 0 && p.Item2 > 0);
            a.Witnesses.Concat(b.Witnesses).Should().BeEmpty();
        }

        [Fact(Explicit = true)]
        public void NoReportedProofHasAWitnessOverALongRun()
        {
            var b = SourceB(LongTrials);
            TestContext.Current.TestOutputHelper?.WriteLine($"source B: {b}");
            b.Witnesses.Should().BeEmpty();
        }

        /// <summary>
        /// Source A: each memo entry with a proof, at the root or of a conjunct at its own position, judged on documents
        /// drawn from its import. A document the declaration rules out is none (<see cref="RuledOut"/>), and a conjunct
        /// proven beside its stratum's facts is judged exactly where those conjuncts hold (<see cref="Holds"/>).
        /// </summary>
        internal static Tally SourceA(RenderingCheckRun run, Tally? tally = null, IReadOnlyList<J.Obj>? declared = null)
        {
            tally ??= new Tally();
            foreach (var check in run.Conjuncts.Where(c => c.Entry?.What is not null).GroupBy(c => c.Entry!.Id).Select(g => g.First()))
                SourceA(tally, check.Entry!, $"{check.Input} {check.Name}#{check.Unit}, entry {check.Entry!.Id}", declared);

            return tally;
        }

        /// <summary>Source A over one entry; or source B's, over a trial's declared documents, which no stratum's facts rule out.</summary>
        internal static void SourceA(Tally tally, RenderingMemo.Entry entry, string where, IReadOnlyList<J.Obj>? declared = null)
        {
            var (claim, world) = (entry.What!, new World(entry.Id));
            var proofs = Proofs(entry, claim).ToList();
            tally.Proofs += proofs.Count;
            foreach (var document in proofs.Count == 0 ? [] : declared ?? RenderingDocumentSampler.Sample(claim))
            {
                if (declared is null && RuledOut(claim, document))
                    continue;

                tally.Judged++;
                foreach (var (ir, form, verdict, conjunct) in proofs)
                    if (conjunct is not int i || Holds(claim, document, world, claim.Stated.Where(b => b.Conjunct == i).SelectMany(b => b.Assumes)))
                        tally.Judge(ir, form, verdict, document, world, where);
            }
        }

        /// <summary>
        /// Lines 1 and 2 (spec g05): every form generate mode's bundles derived over IR-read atoms — each agreement at
        /// both positions, every composite among them, each implied form and each weakening — held to source A's documents
        /// drawn from its bundle's import, on every target its set serves, under each set a leg plans under and the one
        /// beside; and source B's trials, each statement planned and judged on its declared documents. No witness, and no
        /// form S would send judged on no measured document, but one beneath a stratum the bundle's cells prove no document
        /// meets (<see cref="OnCells"/>).
        /// </summary>
        [Fact]
        public void NoGeneratedFormHasAWitness()
        {
            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            var (a, unmeasured) = (new Dictionary<RenderingSet, Tally>(), new List<string>());
            foreach (var set in RenderingGenerateRun.Sets)
                unmeasured.AddRange(Generated(a[set] = new Tally(), generated.Bundles.Where(b => b.Set == set), "source A"));

            var b = new Tally();
            var unmeasuredB = SourceB(Trials, b, generated: true);
            foreach (var (set, tally) in a)
                TestContext.Current.TestOutputHelper?.WriteLine($"source A, {set}: {tally}");

            unmeasured.AddRange(unmeasuredB);
            TestContext.Current.TestOutputHelper?.WriteLine($"source B, generated: {b}; forms S would send judged on no measured document: {unmeasured.Count}");
            foreach (var line in unmeasured.Take(40))
                TestContext.Current.TestOutputHelper?.WriteLine("  unmeasured: " + line);

            a[RenderingSet.Production].Proofs.Should().BePositive();
            a.Values.SelectMany(t => t.Witnesses).Concat(b.Witnesses).Should().BeEmpty();
            unmeasured.Should().BeEmpty("no form S would send rests on documents nothing measured");
        }

        /// <summary>
        /// Line 1b (from the switch): source B's long run, 5,000 trials, each statement planned with S writing and every
        /// generated form judged on the trial's declared documents or its bundle's cells. No witness. The forms judged on no
        /// measured document are printed, not judged: at this size the evaluator tosses a coin on some readings — a cast of a
        /// raw value or of text, a typed <c>DECIMAL</c> — that the pull request's 30 trials never reach (<c>TODO.md</c>).
        /// </summary>
        [Fact(Explicit = true)]
        public void NoGeneratedFormHasAWitnessOverALongRun()
        {
            var b = new Tally();
            var unmeasured = SourceB(LongTrials, b, generated: true);
            TestContext.Current.TestOutputHelper?.WriteLine($"source B, generated: {b}; forms S would send judged on no measured document: {unmeasured.Count}");
            foreach (var line in unmeasured.Take(40))
                TestContext.Current.TestOutputHelper?.WriteLine("  unmeasured: " + line);

            b.Proofs.Should().BePositive();
            b.Witnesses.Should().BeEmpty();
        }

        /// <summary>
        /// Line 1c (spec g05): every residual opaque conjunct S weakens in the pull request's run, under each set, evaluated
        /// by Calcite in process over source A's documents drawn from its weakening bundle's import widened to the paths the
        /// weakening reads, beside the literals the conjunct compares with; wherever Calcite keeps the row, the weakening
        /// keeps it on every target the set serves. A document no row is, a field typed never null absent from it, is left
        /// out; a conjunct whose binding reads a field otherwise than as its type or as the document is counted and left out.
        /// </summary>
        [Fact(Explicit = true)]
        public void NoOpaqueWeakeningHasAWitnessInProcess()
        {
            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            var (judged, kept, unread, witnesses) = (0, 0, 0, new List<string>());
            var compiled = new Dictionary<object, Func<J.Obj, Truths?>?>(ReferenceEqualityComparer.Instance);
            foreach (var w in generated.Weakenings.Where(w => w.Opaque is not null && w.Mine is not null && w.Claim is not null))
            {
                var (conjunct, (fields, readings)) = w.Opaque!.Value;
                if ((compiled.TryGetValue(conjunct, out var truth) ? truth : compiled[conjunct] = CalciteInProcess.Compile(conjunct, fields, readings)) is not { } calcite)
                {
                    unread++;
                    continue;
                }

                var imports = w.Claim!.Imports.ToDictionary(i => i.Key, i => i.Value);
                foreach (var path in w.Mine!.Paths.Where(p => imports.ContainsKey(p) == false))
                    imports[path] = new RenderingMemo.Imported(PathValues.Anything, AllBands);

                var beside = CalciteInProcess.Literals(conjunct).Distinct().Select(l => ServiceForm.CompareOf(CosmosServiceTest.Equal, w.Mine.Paths.First(), l)).ToList();
                var claim = w.Claim with { Imports = imports, Form = beside.Count == 0 ? w.Mine : ServiceForm.JunctionOf(CosmosServiceTest.And, [w.Mine, .. beside]) };
                foreach (var document in RenderingDocumentSampler.Sample(claim))
                {
                    if (calcite(document) is not { } answer)
                        continue;

                    judged++;
                    if (answer != Truths.True)
                        continue;

                    kept++;
                    foreach (var target in ProbeRows.Measured.Serves[w.Set])
                        if (CosmosEval.On(w.Mine, document, target) is { } service && service != ServiceOutcomes.True)
                            witnesses.Add($"{w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct}: {w.Mine.Text} for {conjunct} on {Documents.Json(document)}: Calcite keeps it, {target} {service}");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{generated.Weakenings.Count(w => w.Opaque is not null)} opaque weakenings; {judged} documents judged, {kept} Calcite keeps; {unread} left out, a field read as text or JSON");
            judged.Should().BePositive();
            witnesses.Should().BeEmpty();
        }

        /// <summary>
        /// Source A over generate bundles: each bundle's forms judged on documents drawn from its import, or a trial's declared
        /// ones. A form of a node whose atoms are stated beneath a stratum is exact where the conjuncts that stratum assumes
        /// hold in any mode, and held to no failure elsewhere. A form S would send that none of those measured is judged on
        /// the bundle's cells (<see cref="OnCells"/>), and is vacuous where no cell meets its stratum. Returns each form S
        /// would send — a root's best, a residual root's weakening — that no document measured and whose stratum some
        /// document meets.
        /// </summary>
        internal static List<string> Generated(Tally tally, IEnumerable<RenderingGenerateRun.Bundle> bundles, string source, IReadOnlyList<J.Obj>? declared = null)
        {
            var unmeasured = new List<string>();
            foreach (var bundle in bundles.Where(b => b.Claim is not null && b.Forms.Count > 0))
            {
                var (claim, world, targets) = (bundle.Claim!, new World(bundle.Id), ProbeRows.Measured.Serves[bundle.Set]);
                var forms = bundle.Forms.Select(f => (Form: f, Assumes: Assumes(claim, f.Node), Sent: Sent(bundle, f))).ToList();
                var (measured, drawn, ruledOut) = (new int[forms.Count], 0, 0);
                tally.Proofs += forms.Count;

                void Judge(J.Obj document, IEnumerable<int> which)
                {
                    tally.Judged++;
                    foreach (var node in which.GroupBy(i => forms[i].Form.Node.Digest))
                    {
                        var first = forms[node.First()];
                        var (calcite, exact) = (Tally.Calcite(first.Form.Node, document, world), Holds(claim, document, world, first.Assumes));
                        foreach (var i in node)
                            if (tally.Judge(calcite, forms[i].Form.Node, forms[i].Form.Form, forms[i].Form.Claim, forms[i].Form.Position, document, targets,
                                $"{source} {bundle.Set} bundle {bundle.Id} ({forms[i].Form.What})", exact))
                                measured[i]++;
                    }
                }

                foreach (var document in declared ?? Draw(claim))
                {
                    drawn++;
                    if (declared is null && RuledOut(claim, document))
                    {
                        ruledOut++;
                        continue;
                    }

                    Judge(document, Enumerable.Range(0, forms.Count));
                }

                // A stratum S's own admissions state need not be met by any document drawn: such a form is judged on the
                // bundle's cells, and where no cell meets its stratum no document does, so there is nothing to measure.
                var vacuous = new HashSet<int>();
                foreach (var stratum in Enumerable.Range(0, forms.Count).Where(i => forms[i].Sent && measured[i] == 0).GroupBy(i => string.Join(",", forms[i].Assumes)).ToList())
                {
                    var (met, cells) = (false, 0);
                    foreach (var (document, holds) in OnCells(bundle, claim, world, forms[stratum.First()].Assumes))
                    {
                        cells++;
                        met |= holds;
                        if (RuledOut(claim, document) == false)
                            Judge(document, stratum);
                    }

                    if (met == false && cells > 0)
                        vacuous.UnionWith(stratum.Where(i => measured[i] == 0));
                }

                tally.Vacuous += vacuous.Count;
                unmeasured.AddRange(Enumerable.Range(0, forms.Count).Where(i => forms[i].Sent && measured[i] == 0 && vacuous.Contains(i) == false)
                    .Select(i => $"{source} {bundle.Set} bundle {bundle.Id}: {forms[i].Form.What} {forms[i].Form.Form.Text} for {forms[i].Form.Node}, "
                        + $"{drawn} documents drawn, {ruledOut} ruled out, stratum assuming {string.Join(", ", forms[i].Assumes.Select(a => claim.Conjuncts[a]))}"));
            }

            return unmeasured;
        }

        /// <summary>
        /// Documents over a bundle's cells: every combination of one witness a cell of each path the conjuncts a stratum
        /// assumes read, each other path the claim imports set to its cells' witnesses in turn; and whether the stratum holds
        /// of each. The cells partition what the import admits, the declaration as the root reads it, cut for every atom the
        /// bundle's roots hold, the stratum's conjuncts among them, and an atom reads one truth across a cell
        /// (<c>PredicateAtomCellTests</c>), so where no combination meets the stratum no document the container holds does:
        /// a stratum over a number that a text ordering needs a string for, say. None where a path the stratum reads has no
        /// cell, or the combinations pass 4,096.
        /// </summary>
        internal static IEnumerable<(J.Obj Document, bool Holds)> OnCells(RenderingGenerateRun.Bundle bundle, RenderingMemo.Claimed claim, World world, IReadOnlyList<int> assumes)
        {
            var witnesses = (bundle.Cells ?? []).GroupBy(c => c.Path)
                .ToDictionary(g => g.Key, g => g.Select(c => PathCellTests.Decode(c.Witness)).DistinctBy(v => v is null ? "absent" : Documents.Json(v)).ToList());
            var stratum = assumes.SelectMany(i => claim.Conjuncts[i].Paths).Select(SqlValue.Spell).Distinct().ToList();
            if (assumes.Count == 0 || stratum.Any(p => witnesses.ContainsKey(p) == false) || stratum.Aggregate(1L, (n, p) => n * witnesses[p].Count) > 4096)
                yield break;

            var others = claim.Imports.Keys.Where(p => stratum.Contains(p) == false && witnesses.ContainsKey(p)).ToList();
            var paths = stratum.Concat(others).ToList();
            var combinations = stratum.Aggregate((IEnumerable<IReadOnlyList<J?>>)[[]], (all, p) => all.SelectMany(c => witnesses[p].Select(v => (IReadOnlyList<J?>)[.. c, v]))).ToList();
            for (var j = 0; j < combinations.Count; j++)
            {
                var document = RenderingDocumentSampler.Document(paths, [.. combinations[j], .. others.Select(p => witnesses[p][j % witnesses[p].Count])]);
                yield return (document, Holds(claim, document, world, assumes));
            }
        }

        /// <summary>How many documents a generate bundle whose later round is stated beneath a stratum draws, so that its stratum's conjuncts hold on some.</summary>
        internal const int Stated = 512;

        /// <summary>Source A's documents for a generate bundle: <see cref="Stated"/> where a later round is stated beneath a stratum, so its conjuncts hold on some.</summary>
        internal static IEnumerable<J.Obj> Draw(RenderingMemo.Claimed claim) => RenderingDocumentSampler.Sample(claim, claim.Stated.Count > 0 ? Stated : RenderingDocumentSampler.Most);

        /// <summary>
        /// Whether a document is none the container holds: a rule of its declaration whose body holds of it and whose head
        /// does not, read by an evaluator of the declaration's own (<c>Declaration.Holds</c>), never by
        /// the facts graph a stratum rests on, whose reading may be coarse. A document sets the imported paths alone, so a rule
        /// that reads another path is one the paths it leaves out could satisfy, and rules nothing out; nor does one holding
        /// a claim that evaluator does not read.
        /// </summary>
        internal static bool RuledOut(RenderingMemo.Claimed claim, J.Obj document) => claim.Rules?.Any(rule => rule.Body.Append(rule.Head).All(f => claim.Imports.ContainsKey(SqlValue.Spell(f.Path))
            && f.Claim is JsonClaim.Present or JsonClaim.OfType or JsonClaim.EqualTo or JsonClaim.OneOf or JsonClaim.NotEqualTo or JsonClaim.LowerBound or JsonClaim.UpperBound or JsonClaim.Characters)
            && rule.Body.All(fact => Declaration.Holds(document, fact)) && Declaration.Holds(document, rule.Head) == false) == true;

        /// <summary>
        /// Whether the conjuncts a stratum assumes hold of a document in a mode Calcite may read them in, <c>RETURNING</c> or a
        /// column, as <see cref="Tally.Calcite"/> reads every mode: a form beneath it is held to Calcite exactly wherever any
        /// mode keeps the document.
        /// </summary>
        internal static bool Holds(RenderingMemo.Claimed claim, J.Obj document, World world, IEnumerable<int> assumed) =>
            assumed.All(i => Tally.Calcite(claim.Conjuncts[i], document, world, raises: true) is { } truths && truths.Contains(Truths.True));

        /// <summary>The conjuncts the strata a node's atoms are stated beneath assume.</summary>
        internal static IReadOnlyList<int> Assumes(RenderingMemo.Claimed claim, SqlPredicate node) =>
            [.. claim.Stated.Where(s => node.Subterms.Contains(s.Atom)).SelectMany(s => s.Assumes).Distinct()];

        /// <summary>Whether a form is one S would send: a root's best at <c>Pos</c>, a composite's among them, or a root's weakening.</summary>
        static bool Sent(RenderingGenerateRun.Bundle bundle, RenderingGenerateRun.Judged f) => f.Position == RenderPosition.Pos && bundle.Answers.TryGetValue(f.Node.Digest, out var answer)
            && (f.What == RenderingGenerateRun.Judged.Agreement && f.Claim == Verdict.Equal && f.Form.Equals(answer.Best) || f.What == RenderingGenerateRun.Judged.Weakening && f.Form.Equals(answer.Weakening));

        /// <summary>
        /// Source B: population D's declarations, each trial's printable conjuncts written as one Calcite <c>WHERE</c> over a
        /// container the declaration describes, planned and checked as every input is, and every proof judged on the
        /// trial's declared documents.
        /// </summary>
        internal static Tally SourceB(int trials)
        {
            var tally = new Tally();
            SourceB(trials, tally, generated: false);
            return tally;
        }

        /// <summary>
        /// Source B into a tally: each trial's check-mode proofs, or with <paramref name="generated"/> each trial's statements
        /// planned in a generate run's schema and its bundles' forms judged; returns the forms S would send that no declared
        /// document measured.
        /// </summary>
        internal static List<string> SourceB(int trials, Tally tally, bool generated)
        {
            var unmeasured = new List<string>();
            for (var trial = 0; trial < trials; trial++)
            {
                var (random, _, _, declaration, documents) = PopulationD.Trial(PredicateSoundnessTests.Seed + trial);
                var printed = Statements.Draw(random, declaration).Select(Sql).OfType<string>().ToList();
                if (printed.Count == 0)
                    continue;

                var generate = generated ? new RenderingGenerateRun() : null;
                var container = new CosmosContainerMetadata("d", Keys.Select(k => "/" + k)).WithFacts(declaration.Rules).WithReasoning(generate?.Reasoning ?? new CosmosReasoning());
                var run = RenderingCheckRun.Over(RenderingInputs.Differential("population", [$"SELECT c.\"id\" FROM d AS c WHERE {string.Join(" AND ", printed)}"], [container]),
                    generated: generate);
                tally.Statements++;
                tally.Unplanned += run.Failures.Count;
                if (generate is not null)
                    unmeasured.AddRange(Generated(tally, generate.Bundles, $"source B trial {trial}", documents));
                else
                    SourceA(run, tally, documents);
            }

            return unmeasured;
        }

        /// <summary>
        /// An entry's proofs: of the claim at its root, over every document; and of each conjunct at its own position, by its
        /// position, over the documents where the conjuncts its stratum assumes hold.
        /// </summary>
        static IEnumerable<(SqlPredicate Ir, ServiceForm Form, Verdict Claim, int? Conjunct)> Proofs(RenderingMemo.Entry entry, RenderingMemo.Claimed claim)
        {
            if (entry.Asserted.Kind == RenderingVerdictKind.Proven)
                yield return (claim.Conjuncts.Count == 1 ? claim.Conjuncts[0] : new SqlPredicate.And(claim.Conjuncts), claim.Form, entry.Key.Claim, null);

            for (var i = 0; i < claim.Conjuncts.Count; i++)
                if (entry.Alone[i] is { Kind: RenderingVerdictKind.Proven } && claim.Members[i] is { } member)
                    yield return (claim.Conjuncts[i], member, entry.Key.Claim, i);
        }

        /// <summary>A population D conjunct as Calcite SQL over the container, or <c>null</c> where it holds what no statement here writes.</summary>
        static string? Sql(SqlPredicate predicate) => predicate switch
        {
            SqlPredicate.Not n => Sql(n.Operand) is { } operand ? $"NOT ({operand})" : null,
            SqlPredicate.And a => Joined(a.Arms, " AND "),
            SqlPredicate.Or o => Joined(o.Arms, " OR "),
            SqlPredicate.Compare { Right: SqlValue.Literal l } c when Value(c.Left) is { } v && Literal(l.Value) is { } x => $"{v} {c.Op} {x}",
            SqlPredicate.In i when Value(i.Value) is { } v && i.Points.All(p => Literal(p) is not null) => $"{v} IN ({string.Join(", ", i.Points.Select(Literal))})",
            SqlPredicate.IsNull n when Value(n.Value) is { } v => $"{v} IS NULL",
            _ => null,
        };

        static string? Joined(IEnumerable<SqlPredicate> arms, string connective) =>
            arms.Select(Sql).ToList() is var each && each.All(a => a is not null) ? "(" + string.Join(connective, each) + ")" : null;

        static string? Value(SqlValue value) => value switch
        {
            SqlValue.Text t => $"JSON_VALUE(c.\"DOC\", '$.{string.Join(".", t.Path.Names)}')",
            SqlValue.Typed t => $"JSON_VALUE(c.\"DOC\", '$.{string.Join(".", t.Path.Names)}' RETURNING {t.Type})",
            SqlValue.Raw r when r.Path.Names.Count() == 1 && Keys.Contains(r.Path.Names.Single()) => $"c.\"$.{r.Path.Names.Single()}\"",
            SqlValue.Cast c => Value(c.Value) is { } operand ? $"CAST({operand} AS {c.Target})" : null,
            _ => null,
        };

        static string? Literal(string value) => TypeOf(value) switch
        {
            StringType when StringOf(value).All(c => c >= ' ' && char.IsSurrogate(c) == false) => "'" + StringOf(value).Replace("'", "''", StringComparison.Ordinal) + "'",
            IntegerType or FractionType when IsExactNumber(value) => StringOf(value),
            BooleanType => value == Of(true) ? "TRUE" : "FALSE",
            _ => null,
        };

        /// <summary>What a source judged: proofs, documents, and each document's verdict.</summary>
        internal sealed class Tally
        {

            public int Statements, Unplanned, Proofs, Judged, Agreed, Unmeasured, Skipped, Vacuous;

            public List<string> Witnesses { get; } = [];

            /// <summary>Each witness as it was found: where, the form and its node and position, the document and the target.</summary>
            public List<(string Where, ServiceForm Form, SqlPredicate Ir, RenderPosition Position, J.Obj Document, RenderingTarget Target)> Found { get; } = [];

            /// <summary>
            /// Judges a proof on a document: every typed mode against every target, a failure never allowed, <c>Equal</c>
            /// keeping alike and <c>Implies</c> keeping at least Calcite's row. A document on which a mode tosses a coin or is
            /// not modelled is unmeasured. Returns whether it was measured.
            /// </summary>
            public bool Judge(SqlPredicate ir, ServiceForm form, Verdict claim, J.Obj document, World world, string where) =>
                Judge(Calcite(ir, document, world), ir, form, claim, RenderPosition.Pos, document, RenderingCheckRun.Targets, where);

            /// <summary>
            /// Calcite's truths of a predicate on a document, every typed mode and, where a raw value is read through its
            /// columns' own answers, every column (<see cref="IrEval.RawModes"/>), its raises left out; <c>null</c> where a mode
            /// tossed a coin or is not modelled, unless <paramref name="raises"/> asks for what the modelled modes say.
            /// </summary>
            public static List<Truths>? Calcite(SqlPredicate ir, J.Obj document, World world, bool raises = false)
            {
                var calcite = new List<Truths>();
                foreach (var (mode, column) in from mode in Modes from column in IrEval.RawModes(ir) select (mode, column))
                {
                    var tossed = world.Tossed;
                    Truths truth;
                    try
                    {
                        truth = IrEval.Truth(ir, document, world, mode, column);
                    }
                    catch (NotSupportedException)
                    {
                        if (raises)
                            continue;

                        return null;
                    }

                    if (world.Tossed != tossed)
                    {
                        if (raises)
                            continue;

                        return null;
                    }

                    if (truth != Truths.Raises)
                        calcite.Add(truth);
                }

                return calcite;
            }

            /// <summary>
            /// Judges a form claimed at a position against Calcite's truths on a document, on each target: at <c>Pos</c> the
            /// service's true held to Calcite's TRUE, at <c>Neg</c> its false to Calcite's FALSE, alike for <c>Equal</c> and
            /// at least Calcite's for <c>Implies</c>, a failure never. Off its stratum (<paramref name="exact"/> false) a form
            /// is held to no failure alone, which measures nothing. Returns whether the document was measured.
            /// </summary>
            public bool Judge(List<Truths>? calcite, SqlPredicate ir, ServiceForm form, Verdict claim, RenderPosition pos, J.Obj document, IEnumerable<RenderingTarget> targets,
                string where, bool exact = true)
            {
                if (calcite is null && exact)
                {
                    Unmeasured++;
                    return false;
                }

                if (exact && calcite!.Count == 0)
                {
                    Skipped++;
                    return false;
                }

                var (kept, keeps) = pos == RenderPosition.Pos ? (Truths.True, ServiceOutcomes.True) : (Truths.False, ServiceOutcomes.False);
                foreach (var target in targets)
                {
                    if (CosmosEval.On(form, document, target) is not { } service)
                    {
                        Unmeasured++;
                        return false;
                    }

                    if (service == ServiceOutcomes.Fails || exact && calcite!.Any(t => claim == Verdict.Equal ? (t == kept) != (service == keeps) : t == kept && service != keeps))
                    {
                        Witnesses.Add($"{where}: {form.Text} for {ir} at {pos}{(exact ? "" : ", off its stratum")}, on {Documents.Json(document)}: Calcite {string.Join("|", calcite ?? [])}, {target} {service}");
                        Found.Add((where, form, ir, pos, document, target));
                        return true;
                    }
                }

                if (exact)
                    Agreed++;

                return exact;
            }

            public override string ToString() => $"{Statements} statements ({Unplanned} not planned), {Proofs} proofs over {Judged} documents: {Agreed} agreed, "
                + $"{Unmeasured} unmeasured, {Skipped} skipped, {Vacuous} beneath a stratum no cell meets, {Witnesses.Count} witnesses";

        }

    }

}
