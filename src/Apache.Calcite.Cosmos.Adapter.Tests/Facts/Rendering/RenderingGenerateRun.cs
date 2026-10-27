using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The generate shadow (spec g05, g15, §5.6), told by production's four sites as the check run plans: each statement's
    /// rounds asked of S at admission (site A) through its oracle, one bundle a round, imported from the root with a later
    /// round's atoms stated beneath <c>StratumFor</c>, every pending conjunct, <c>Through(k)</c> and <c>Before(k)</c> its
    /// roots, and a closing bundle where one is needed; S's weakening as the split rule would push it (W), and where the
    /// residual projection rule fires (R). Beside each bundle, after planning, a check
    /// bundle of the same roots, import and strata claims the translator's texts, so that no claimed text's parts enter
    /// <c>BestExact</c> and the two share one key. Under each set a leg plans under, asserted, and <c>Portable</c> beside.
    /// </summary>
    /// <remarks>
    /// The bundles are production's (<see cref="RenderingOracle.Ask"/>), read before they go through the oracle's hook; the
    /// shadow writes none of its own. Its oracle is its own, over its graph, so that every key's first question opens the
    /// bundle the shadow reads, and a plant's graph is the one production asks. S's rounds follow the translator's, whose
    /// nodes the facts graph built from the translator's admissions; writing, S's own (the rehearsal).
    /// </remarks>
    internal sealed class RenderingGenerateRun : ICosmosRenderingShadowObserver
    {

        /// <summary>
        /// The sets the legs plan under (O1): <c>Production</c> on a leg with no emulator, <c>Emulator</c> on one with it. Both
        /// are asserted on every leg.
        /// </summary>
        public static readonly RenderingSet[] AssertedSets = [RenderingSet.Production, RenderingSet.Emulator];

        /// <summary>The set reported beside the asserted ones, never asserted.</summary>
        public const RenderingSet BesideSet = RenderingSet.Portable;

        public static IEnumerable<RenderingSet> Sets => [.. AssertedSets, BesideSet];

        /// <summary>
        /// The classes of an owned conjunct's admission: respelled where both forms are exact at the key, and S's alone exact
        /// where the translator's text is not, which no switch listing may read as cosmetic.
        /// </summary>
        public const string Same = "same", Respelled = "respelled", ExactToSAlone = "S exact, the translator's unproven", Gain = "gain", ShownWrong = "translator shown wrong",
            Regression = "regression", Alike = "declined alike", Written = "translator-written", Engine = "engine", Unread = "translator text unread";

        /// <summary>
        /// A regression's reasons, each read off rows the shadow holds: a form the gate agreed exact that the band filter
        /// refused; the translator's text exact at the key, so a form no template proposes; a stratum shallower than the
        /// translator's, which claims no cause; the composite; or the conjunct on its merits.
        /// </summary>
        public const string BandFilter = "band filter", NotProposed = "a form S does not propose", Shallower = "a stratum shallower than the translator's",
            Composite = "the composite unproven", OnItsMerits = "Unproven on its merits";

        /// <summary>A statement over which the residual projection rule fires by S's split and not the translator's, or the other way (spec g15, site R): a class row of its own.</summary>
        public const string Placement = "projection placement";

        readonly RenderingGraph _graph;
        readonly RenderingSet[] _sets;
        readonly ConcurrentDictionary<string, Lazy<Bundle>> _checks = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<RenderingQuestionKey, Bundle> _opened = new();
        readonly ConcurrentDictionary<(CosmosFilterStatement, int, RenderingSet), (ServiceForm Form, string? Text, bool Applied)> _weakened = new();
        readonly ConcurrentDictionary<CosmosFilterStatement, (bool Translator, bool Mine)> _placed = new();
        ValueSet<string>? _bands;
        int _next;

        /// <param name="graph">The S it asks: the registry's, or a plant's of its own.</param>
        /// <param name="sets">The sets it shadows under: those the legs plan under and the one beside, unless a plant asks fewer.</param>
        /// <param name="readExactly">A plant's reading precision, which production's <c>StratumFor</c> reads in its place (G12).</param>
        /// <param name="renderedFrom">A plant's node a conjunct is stated beneath in its own round (G11); none in the registry's run.</param>
        public RenderingGenerateRun(RenderingGraph? graph = null, IEnumerable<RenderingSet>? sets = null, Func<SqlPredicate, bool>? readExactly = null,
            Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? renderedFrom = null)
        {
            (_graph, _sets, ReadExactly, RenderedFrom) = (graph ?? RenderingCheckRun.Measured, [.. sets ?? Sets], readExactly, renderedFrom);
            Oracle = new RenderingOracle(_graph, remembered: 1 << 20, wait: Timeout.InfiniteTimeSpan);
        }

        /// <summary>The oracle production's sites ask in the shadow's flows: one bundle code path, over the shadow's graph.</summary>
        public RenderingOracle Oracle { get; }

        RenderingOracle? ICosmosRenderingShadowObserver.Oracle => Oracle;

        IReadOnlyList<RenderingSet> ICosmosRenderingShadowObserver.Sets => _sets;

        /// <inheritdoc/>
        public Func<SqlPredicate, bool>? ReadExactly { get; }

        /// <inheritdoc/>
        public Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? RenderedFrom { get; }

        /// <summary>The parting bands as S holds them, the graph's own: a plant's where it planted some.</summary>
        ValueSet<string> Bands => _bands ??= _graph.Current() is var theory ? theory.Theory.Get(theory.PartingBands.Of()) : ValueSet<string>.Empty;

        /// <summary>Every generate bundle production's sites opened, once each.</summary>
        public ConcurrentQueue<Bundle> Bundles { get; } = new();

        /// <summary>One row a pending conjunct a round: <c>shadow-admission.tsv</c>.</summary>
        public ConcurrentQueue<Admission> Admissions { get; } = new();

        /// <summary>One class an owned or translated conjunct a set.</summary>
        public ConcurrentQueue<Classed> Classes { get; } = new();

        /// <summary>Each conjunct the declared-fact rewrite changed, beside what it became, under each set: line 33.</summary>
        public ConcurrentQueue<Rewrite> Rewrites { get; } = new();

        /// <summary>One row a residual conjunct a set: <c>shadow-weakening.tsv</c>.</summary>
        public ConcurrentQueue<Weakened> Weakenings { get; } = new();

        /// <summary>
        /// Each conjunct whose translator's text the shadow's check bundle proved beside S's best, at one key: the pairwise
        /// check's second half.
        /// </summary>
        public ConcurrentQueue<(Bundle Bundle, SqlPredicate Conjunct, ServiceForm Translator, ServiceForm Best)> Pairs { get; } = new();

        /// <summary>A statement's cost under a set: generate bundles opened, S's time first and asked again.</summary>
        public ConcurrentQueue<Cost> Costs { get; } = new();

        /// <summary>Site W: each residual's weakening as the split rule would push it under a set, and the text its Rex translates to (line 19).</summary>
        public ConcurrentQueue<Pushed> Pushes { get; } = new();

        /// <summary>Site R: each filter the residual projection rule asked of, where it fires by the translator's split and by S's.</summary>
        public ConcurrentQueue<(string Input, string Name, int Statement, bool Translator, bool Mine)> Placements { get; } = new();

        /// <summary>Each question S declined: an engine failure, a conflict, or no generation.</summary>
        public ConcurrentQueue<string> Declines { get; } = new();

        /// <summary>Each question a site asked, with the statement that asked it: what <c>NoProjectionReachesS</c> reads.</summary>
        public ConcurrentQueue<(CosmosFilterStatement Statement, CosmosFilterRendering.Question Question)> Asked { get; } = new();

        /// <summary>
        /// A bundle production opened, read before it goes: each node's agreements, implied forms and weakenings at both
        /// positions, the refutations, the candidates proposed, and the claim source A samples, filed under the key the
        /// oracle files its answer under.
        /// </summary>
        void ICosmosRenderingShadowObserver.Opened(CosmosFilterStatement statement, CosmosFilterRendering.Question question, RenderingBundle bundle, IReadOnlyList<Pred> terms,
            ImportReport import, RenderingRun run, TimeSpan elapsed)
        {
            var key = RenderingQuestionKey.Of(question.Roots, statement.Statement.Opaques, import, question.Set, RenderingAnswerMemo.DigestOf(bundle.Theory));
            var id = Interlocked.Increment(ref _next);
            var nodes = question.Roots.SelectMany(r => r.Subterms).DistinctBy(n => n.Digest).ToList();

            // A node is its term again: made by get-or-add, the class the roots already hold.
            var written = nodes.ToDictionary(n => n.Digest, n => bundle.Write(n, statement.Statement.Opaques));
            var answers = question.Roots.Select((r, i) => (r.Digest, Answer: bundle.ReadBest(terms[i]))).ToDictionary(a => a.Digest, a => a.Answer);
            var (indices, conjuncts) = (question.Conjuncts.ToList(), question.Conjuncts.Select(i => statement.Statement.Conjuncts[i]).ToList());
            var stated = (import.Stated ?? []).Select(x => question.Beneath.First(b => b.Atom == x.Atom) is var b
                ? new RenderingMemo.Beneath(indices.IndexOf(b.Conjunct), x.Atom, b.Source, x.Assumed, [.. b.Stratum.Assumes.Select(a => indices.IndexOf(a))]) : null!).ToList();
            var proposed = (Candidates: bundle.Failure is null ? (int)bundle.Scope.CountRows(bundle.Theory.Candidate) : 0,
                Atoms: nodes.Count(n => n is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque)));
            var (forms, refuted) = (new List<Judged>(), new List<Refuted>());
            if (bundle.Failure is null && run is { StopReason: RunStopReason.Completed, OneNodePerClass: true, Contradicted: false })
            {
                foreach (var node in nodes.Where(n => n.Subterms.Any(t => t is SqlPredicate.Opaque) == false))
                    foreach (var pos in Enum.GetValues<RenderPosition>())
                    {
                        var term = written[node.Digest];
                        forms.AddRange(bundle.Agreements(term, pos).Select(a => new Judged(node, pos, a.Form, a.Claim, Judged.Agreement)));

                        // The best, as the Equal agreement it must be: one a plant lifted with none is judged too.
                        if (bundle.Scope.TryGet(bundle.Theory.BestExact.Of(term, pos), out var best) && best.Form is { } exact)
                            forms.Add(new Judged(node, pos, exact, Verdict.Equal, Judged.Agreement));
                        if (bundle.Scope.TryGet(bundle.Theory.Implied.Of(term, pos), out var implied))
                            forms.AddRange(implied.Select(f => new Judged(node, pos, f, Verdict.Implies, Judged.Implied)));
                        if (bundle.Scope.TryGet(bundle.Theory.Weakening.Of(term, pos), out var weakening))
                            forms.Add(new Judged(node, pos, weakening, Verdict.Implies, Judged.Weakening));
                    }

                // Each Equal refutation of an atom's form, and the value each refuting cell sets its path to: what a witness is made of.
                var cells = RenderingCheckRun.Cells(bundle);
                foreach (var row in bundle.Scope.Rows(bundle.Theory.Disagrees).Where(r => Verdict.Equal.Equals(r.Keys[3])))
                    if (nodes.FirstOrDefault(n => written[n.Digest].Id.Equals(row.Keys[0])) is { } atom && row.Output is Refutation refutation)
                        refuted.Add(new Refuted(atom, (RenderPosition)row.Keys[1], (ServiceForm)row.Keys[2], refutation,
                            refutation.Cells.Select(c => cells[c]).ToDictionary(c => c.Path, c => c.Cell.Witness())));
            }

            var distinct = forms.Distinct().ToList();
            var literals = distinct.Select(f => f.Form).DistinctBy(f => f.Text).ToList();
            var claim = bundle.Failure is null ? RenderingCheckRun.Claimed(bundle, conjuncts, literals.Count switch
            {
                0 => ServiceForm.ConstantOf(true),
                1 => literals[0],
                _ => ServiceForm.JunctionOf(CosmosServiceTest.And, literals),
            }, [.. conjuncts.Select(_ => (ServiceForm?)null)], stated, null, statement.DeclarationForCheck.Rules) : null;
            var witnesses = bundle.Failure is null ? RenderingCheckRun.Cells(bundle).Values.Select(c => (c.Path, Witness: c.Cell.Witness())).ToList() : [];
            var generated = new Bundle(id, question.Set, run, bundle.Failure, question.Roots, answers, new Dictionary<string, RenderingVerdict>(), distinct, claim,
                elapsed.TotalMilliseconds, proposed, refuted, bundle.Iterations, key, witnesses);
            if (_opened.TryAdd(key, generated))
                Bundles.Enqueue(generated);
        }

        // The split rule asks the same filter many times, and what lies beneath may have changed; the text a report wrote is kept.
        void ICosmosRenderingShadowObserver.Weakened(CosmosFilterStatement statement, int conjunct, RenderingSet set, ServiceForm form, string? text, bool applied) =>
            _weakened.AddOrUpdate((statement, conjunct, set), (form, text, applied), (_, was) => was.Text is null ? (form, text, applied) : was);

        void ICosmosRenderingShadowObserver.Placed(CosmosFilterStatement statement, bool translator, bool mine) =>
            _placed[statement] = (translator, mine);

        /// <summary>
        /// Shadows one statement the check run planned, read after its planning, from what production's sites asked of S and
        /// the records the capture wrote: under each set, its rounds and what each class says of its conjuncts.
        /// </summary>
        public void Shadow(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, IReadOnlyList<CaptureRecord> records)
        {
            var shape = Shape.Of(input, index, statement, records);
            foreach (var set in _sets)
            {
                if (statement.RenderingFor(set) is not var (rendering, resolution))
                    throw new InvalidOperationException($"{input.Input} {input.Name}: statement {index} was admitted where no flow armed the shadow under {set}.");

                Record(shape, set, rendering, resolution);
            }

            foreach (var ((s, conjunct, set), (form, text, applied)) in _weakened.Where(w => w.Key.Item1 == statement))
                Pushes.Enqueue(new(input.Input, input.Name, index, conjunct, set, form, text, applied));
            if (_placed.TryGetValue(statement, out var placed))
            {
                Placements.Enqueue((input.Input, input.Name, index, placed.Translator, placed.Mine));
                if (placed.Translator != placed.Mine)
                    Classes.Enqueue(new(input.Input, input.Name, index, -1, statement.RenderedSets[0], Placement, $"fires by the translator's split {placed.Translator}, by S's {placed.Mine}",
                        "statement " + statement.Statement.Digest, null, null, null, null, 0));
            }

            Rewritten(shape);
            GC.KeepAlive(statement);
        }

        /// <summary>
        /// A statement's admission under a set as production asked it: each round's bundle beside a check of the translator's
        /// texts at its key, the composite where §5.2 reads it, and each residual's weakening against J's; and S's time in
        /// the bundles, first as though the memo held none, and again, the keys and lookups alone.
        /// </summary>
        void Record(Shape s, RenderingSet set, CosmosFilterRendering rendering, CosmosFilterRendering.Resolution resolution)
        {
            var rounds = rendering.Rounds;
            var questions = rounds.Select(r => (r.Question, r.Answer)).ToList();
            if (resolution.WeakeningQuestion is { } wq && questions.All(q => ReferenceEquals(q.Question, wq) == false))
                questions.Add((wq, resolution.WeakeningAnswer!));

            foreach (var (question, answer) in questions)
            {
                Asked.Enqueue((s.Statement, question));
                if (answer.Declined is { } declined)
                    Declines.Enqueue($"{s.Input.Input} {s.Input.Name}/{s.Index} {set}: {declined} {answer.Roots.Values.FirstOrDefault()?.Reason}");
            }

            Bundle Generated(RenderingOracle.RenderingBundleAnswer answer) => answer.Key is { } key && _opened.TryGetValue(key, out var bundle) ? bundle
                : new Bundle(0, set, null, null, [], answer.Roots, new Dictionary<string, RenderingVerdict>(), [], null, 0);

            var (checks, askedUnder) = (new Dictionary<int, RenderingVerdict?>(), new Dictionary<int, int?>());
            foreach (var round in rounds)
            {
                var (k, bundle, strata) = (round.Number, Generated(round.Answer), round.Question.Strata);
                foreach (var i in round.Pending.Where(i => s.Round[i] == k))
                    askedUnder[i] = strata[i]?.Round;

                // The translator's texts of this round, claimed in a check bundle of the same roots, import and strata.
                var claims = round.Pending.Where(i => s.Round[i] == k && s.Owned[i] && s.Texts.ContainsKey(i) && s.Texts[i].Form is not null).ToDictionary(i => i, i => s.Texts[i].Form!);
                if (claims.Count > 0)
                {
                    var check = Check(s, round.Question, claims);
                    foreach (var i in claims.Keys)
                    {
                        checks[i] = check.Verdicts[s.Conjuncts[i].Digest];
                        if (checks[i]!.Kind == RenderingVerdictKind.Proven && bundle.Answers.GetValueOrDefault(s.Conjuncts[i].Digest)?.Best is { } best)
                            Pairs.Enqueue((bundle, s.Conjuncts[i], claims[i], best));
                    }
                }

                foreach (var i in round.Pending)
                    Admissions.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, i, k, set, s.Owned[i], s.Round[i] == k ? s.Texts.GetValueOrDefault(i)?.Written : null,
                        s.Round[i] == k && checks.TryGetValue(i, out var v) ? Show(v) : null, round.Answer.Of(s.Conjuncts[i]), strata[i]?.Round, bundle.Id, bundle.Milliseconds));
            }

            // J's weakening of each residual, claimed implied in a check bundle of the weakening's roots, import and strata.
            var theirs = new Dictionary<int, RenderingVerdict?>();
            var weakening = resolution.WeakeningAnswer is { } wa ? Generated(wa) : null;
            if (resolution.WeakeningQuestion is { } q)
            {
                var claims = resolution.Residual.Where(i => q.Strata.ContainsKey(i) && s.Weakened.GetValueOrDefault(i)?.Form is not null).ToDictionary(i => i, i => s.Weakened[i].Form!);
                if (claims.Count > 0)
                {
                    var check = Check(s, q, claims, Verdict.Implies);
                    foreach (var i in claims.Keys)
                        theirs[i] = check.Verdicts[s.Conjuncts[i].Digest];
                }
            }

            Classify(s, set, resolution.Forms, resolution.Voided, checks, askedUnder, [.. questions.Select(q => Generated(q.Answer))], [.. resolution.Residual], weakening, theirs);

            // First, each bundle's own time, as though S's memo held none; again, each question asked once more, a hit.
            var keys = questions.Select(q => q.Answer.Key).OfType<RenderingQuestionKey>().Distinct().ToList();
            var started = Stopwatch.GetTimestamp();
            foreach (var (question, _) in questions)
                Oracle.Ask(set, question.Roots, s.Opaques, question.Source, question.Root, question.Rules, [.. question.Beneath.Select(b => (b.Atom, b.Source, b.Stratum.Node, question.Rules))]);
            Costs.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, set, ShapeOf(s.Conjuncts), keys.Count, keys.Sum(k => _opened.GetValueOrDefault(k)?.Milliseconds ?? 0),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, s.Conjuncts));
        }

        /// <summary>
        /// The declared-fact rewrite's pairs (spec g05; line 33): where the rewrite changed a statement's conjuncts, each
        /// original beside what it became, the rewritten one asked of S in a bundle imported from the root under each set,
        /// with the original's node and binding, by which Calcite judges S's best for it in process (<c>RenderingRewriteTests</c>).
        /// </summary>
        void Rewritten(Shape s)
        {
            var rewritten = s.Statement.RewrittenForCheck;
            if (ReferenceEquals(rewritten, s.Statement))
                return;

            // A conjunct the rewrite left alone is the same node; the rest are paired in order, a deleted one left unpaired.
            var (kept, before) = (rewritten.Conjuncts.ToHashSet(ReferenceEqualityComparer.Instance), s.Statement.Conjuncts.ToHashSet(ReferenceEqualityComparer.Instance));
            var originals = Enumerable.Range(0, s.Count).Where(i => kept.Contains(s.Statement.Conjuncts[i]) == false).ToList();
            var lowered = Enumerable.Range(0, rewritten.Conjuncts.Count).Where(i => before.Contains(rewritten.Conjuncts[i]) == false).ToList();
            for (var k = 0; k < originals.Count; k++)
            {
                var original = s.Conjuncts[originals[k]];
                var made = k < lowered.Count && originals.Count == lowered.Count ? rewritten.Statement.Conjuncts[lowered[k]] : null;
                var source = ((org.apache.calcite.rex.RexNode)s.Statement.Statement.Sources[originals[k]]!, s.Statement.BindingForCheck);
                foreach (var set in _sets)
                    Rewrites.Enqueue(made is null ? new(s.Input.Input, s.Input.Name, s.Index, set, original, null, null, null, source) : Pair(s, set, original, made, rewritten.Statement.Opaques, source));
            }

            GC.KeepAlive(rewritten);
        }

        Rewrite Pair(Shape s, RenderingSet set, SqlPredicate original, SqlPredicate made, IReadOnlyDictionary<long, SqlValue> opaques,
            (org.apache.calcite.rex.RexNode, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?>, IReadOnlyList<CosmosReading>)) source)
        {
            using var bundle = _graph.Open(set);
            var m = bundle.Write(made, opaques);
            RenderingImporter.Import(bundle, s.Source, s.Root.Context, s.Root.Rules, s.Declaration, made.Paths.Concat(original.Paths));
            bundle.Generate(m);
            bundle.RunGenerate(RenderingBundle.WidthOf([made]));
            var answer = bundle.ReadBest(m);
            var claim = answer.Best is { } best && bundle.Failure is null ? RenderingCheckRun.Claimed(bundle, [made], best, [null], [], null, s.Declaration.Rules) : null;
            return new(s.Input.Input, s.Input.Name, s.Index, set, original, made, answer, claim, source);
        }

        /// <summary>Each conjunct's class under a set, its reason where it is a regression, and each residual's weakening against J's.</summary>
        void Classify(Shape s, RenderingSet set, IReadOnlyDictionary<int, ServiceForm> forms, bool voided,
            Dictionary<int, RenderingVerdict?> checks, Dictionary<int, int?> askedUnder, List<Bundle> bundles, List<int> residual, Bundle? weakening,
            Dictionary<int, RenderingVerdict?> theirs)
        {
            for (var i = 0; i < s.Count; i++)
            {
                var digest = s.Conjuncts[i].Digest;
                var verdict = checks.GetValueOrDefault(i);
                var engine = verdict?.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict || bundles.Any(b => b.Answers.TryGetValue(digest, out var x)
                    && (x.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict || x.Reason?.StartsWith("declined", StringComparison.Ordinal) == true));
                var form = forms.GetValueOrDefault(i);
                var text = s.Texts.GetValueOrDefault(i);
                var (@class, reason) = engine ? (Engine, null)
                    : s.Owned[i] == false ? (Written, null)
                    : s.Round[i] > 0 && verdict?.Kind == RenderingVerdictKind.Refuted && (askedUnder.GetValueOrDefault(i) ?? 1) >= s.Round[i] ? (ShownWrong, null)
                    : s.Round[i] > 0 && form is not null && text?.Form is null ? (Unread, null)
                    : s.Round[i] > 0 && form is not null ? (form.Equals(text!.Form) ? Same : verdict?.Kind == RenderingVerdictKind.Proven ? Respelled : ExactToSAlone, null)
                    : s.Round[i] > 0 ? (Regression, Why(s, i, voided, bundles, askedUnder.GetValueOrDefault(i), verdict))
                    : form is not null ? (Gain, null)
                    : (Alike, (string?)null);
                Classes.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, i, set, @class, reason, digest, text?.Written, text?.Form, Show(verdict), form, s.Round[i], text?.Parameters));
            }

            // Residuals: J weakened what the translator left above; S weakens what it leaves, read from one bundle.
            foreach (var i in residual.Union(Enumerable.Range(0, s.Count).Where(i => s.Round[i] == 0)).Order())
            {
                var mine = residual.Contains(i) && weakening is not null && weakening.Answers.TryGetValue(s.Conjuncts[i].Digest, out var answer) ? answer.Weakening : null;
                var j = s.Weakened.GetValueOrDefault(i);
                // Site W's own record: S's weakening the builder wrote no Rex for, or the translator wrote no text of, is not pushed.
                var refused = mine is not null && _weakened.TryGetValue((s.Statement, i, set), out var pushed) && pushed.Text is null && pushed.Applied == false;
                Weakenings.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, i, set, s.Conjuncts[i].Digest, j?.Written, j?.Form, Show(theirs.GetValueOrDefault(i), Verdict.Implies), mine, residual.Contains(i), s.Round[i] > 0,
                    weakening?.Id, weakening?.Claim, s.Owned[i], mine is not null && s.Conjuncts[i].Subterms.Any(t => t is SqlPredicate.Opaque) ? (s.Statement.Conjuncts[i], s.Statement.BindingForCheck) : null,
                    j?.Form is not { } theirsForm || Writable(theirsForm, s.Conjuncts[i]), mine is not null ? s.Statement : null, refused));
            }
        }

        /// <summary>
        /// Why S declines a conjunct the translator pushes, read off rows the shadow holds: a form the gate agreed exact that
        /// the band filter refused, by S's own bands; the translator's text exact at the key, so a form no template proposed;
        /// a stratum shallower than the translator's; the composite; or the conjunct on its merits.
        /// </summary>
        string Why(Shape s, int i, bool voided, List<Bundle> bundles, int? stratum, RenderingVerdict? translator)
        {
            var digest = s.Conjuncts[i].Digest;
            if (voided)
                return Composite;

            if (bundles.SelectMany(b => b.Forms).Any(f => f.Node.Digest == digest && f.Position == RenderPosition.Pos && f.What == Judged.Agreement && f.Claim == Verdict.Equal
                    && RenderingPrimitives.Sendable.Function(f.Form, Bands) == false))
                return BandFilter;

            return translator?.Kind == RenderingVerdictKind.Proven ? NotProposed : (stratum ?? 1) < s.Round[i] ? Shallower : OnItsMerits;
        }

        /// <summary>
        /// Whether the Rex builder writes a form over a conjunct's own readings: each comparison and membership of it over the
        /// reader of an atom of its path, by the grammar's rows (<see cref="RenderingRexGrammar"/>); none over a path no atom reads.
        /// </summary>
        static bool Writable(ServiceForm form, SqlPredicate conjunct)
        {
            var readers = CosmosFilterRendering.AtomsOf(conjunct).Select(a => (Path: SqlValue.Spell(a.Paths.Single()), Reader: a switch
            {
                SqlPredicate.Compare c => RenderingFamily.ReaderOf(c.Left),
                SqlPredicate.In m => RenderingFamily.ReaderOf(m.Value),
                _ => null,
            })).Where(r => r.Reader is not null).ToLookup(r => r.Path, r => r.Reader!);
            return form.Subforms.Where(f => f.Kind is ServiceFormKind.Compare or ServiceFormKind.In).All(f => readers[f.Path!].Any(r => RenderingRexGrammar.Written.Writes(f, r)));
        }

        /// <summary>
        /// A check of the translator's texts, or J's weakenings, in a bundle of its own with the roots, import and strata of a
        /// question production asked, claimed at its conjuncts: one key by construction. Remembered by content.
        /// </summary>
        Bundle Check(Shape s, CosmosFilterRendering.Question question, IReadOnlyDictionary<int, ServiceForm> claims, Verdict claimed = Verdict.Equal)
        {
            var beneath = question.Beneath.Select(b => (b.Atom, b.Source, b.Stratum.Node, question.Rules)).ToList();
            ImportReport Import(RenderingBundle? bundle) => RenderingImporter.Import(bundle, question.Source, question.Root, question.Rules, s.Declaration, question.Roots.SelectMany(r => r.Paths), beneath);
            var asked = RenderingQuestionKey.Of(question.Roots, s.Opaques, Import(null), question.Set, RenderingAnswerMemo.DigestOf(_graph.Current()));
            var key = string.Join("\n", [asked.ToString(), .. claims.Select(c => s.Conjuncts[c.Key].Digest + " claims " + claimed + " " + c.Value.Text).Order(StringComparer.Ordinal)]);
            return _checks.GetOrAdd(key, _ => new Lazy<Bundle>(() =>
            {
                var started = Stopwatch.GetTimestamp();
                using var bundle = _graph.Open(question.Set);
                var terms = question.Roots.SelectMany(r => r.Subterms).DistinctBy(n => n.Digest).ToDictionary(n => n.Digest, n => bundle.Write(n, s.Opaques));
                Import(bundle);
                foreach (var (i, form) in claims)
                    bundle.Claim(terms[s.Conjuncts[i].Digest], form);

                var run = bundle.Run(RenderingCheckRun.CheckLimit(claims.Values));
                // A conjunct admitted twice is one term, claimed once a text.
                var verdicts = claims.GroupBy(c => s.Conjuncts[c.Key].Digest).ToDictionary(g => g.Key, g => bundle.Read(terms[g.Key], g.First().Value, claimed));
                return new Bundle(Interlocked.Increment(ref _next), question.Set, run, bundle.Failure, question.Roots, new Dictionary<string, RenderingAnswer>(), verdicts, [], null,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            })).Value;
        }

        /// <summary>
        /// A residual's weakening against J's: the same form; S's tighter, keeping no document J's does not and dropping one
        /// it keeps, or looser, or the two parting both ways, on the documents source A draws from the bundle's import, on
        /// every target the set serves; S's alone or J's alone; dropped, a conjunct the translator pushed whole that S leaves
        /// with none; pushed exactly where S admits what J weakened; none. S's that site W refused to push is none of S's.
        /// </summary>
        public static string ClassOf(Weakened w)
        {
            if (w.Residual == false)
                return w.Translator is null ? "none" : "pushed exactly";

            // J's alone where J's is unproven to S, and S proves none, is O7's loss 5 as a looser one is; never where site W refused S's.
            if (w.Mine is null || w.Refused)
                return w.Pushed ? "dropped" : w.Translator is null ? "none" : w.Refused == false && w.TranslatorVerdict == "Unproven" ? "translator-only (O7: J's unproven to S)" : "translator-only";

            if (w.Translator is null)
                return "S-only";

            if (w.Mine.Equals(w.Translator))
                return Same;

            var (fewer, more) = (false, false);
            foreach (var document in w.Claim is null ? [] : RenderingDocumentSampler.Sample(w.Claim))
                foreach (var target in ProbeRows.Measured.Serves[w.Set])
                {
                    var (mine, theirs) = (CosmosEval.On(w.Mine, document, target), CosmosEval.On(w.Translator, document, target));
                    if (mine is null || theirs is null)
                        continue;

                    fewer |= theirs == ServiceOutcomes.True && mine != ServiceOutcomes.True;
                    more |= mine == ServiceOutcomes.True && theirs != ServiceOutcomes.True;
                }

            // O7 lists a looser weakening by J's verdict at the weakening's key and the grammar's rows, never by its shape: J's
            // implied but holding a member the Rex builder cannot write over the conjunct's readings (loss 1, the text-equality
            // alternatives' number branch among them), or J's present and unproven (loss 5). One J's verdict is missing or
            // refuted for is unlisted.
            var listed = w.TranslatorVerdict switch
            {
                "Implied" when w.TranslatorWritable == false => " (O7: J's holds a member the Rex builder cannot write)",
                "Unproven" => " (O7: J's unproven to S)",
                _ => "",
            };
            return (fewer, more) switch { (true, false) => "tighter", (false, true) => "looser" + listed, (true, true) => "parting" + listed, _ => "alike on the documents" };
        }

        /// <summary>
        /// The shape a cost line is read under (§6.3), off the union of the atoms asked: an atom is a predicate other than a
        /// junction, a negation or an opaque term, holding no parameter; a list is an <c>IN</c> of five or more points, one of
        /// two to four counting as one condition.
        /// </summary>
        public static string ShapeOf(IEnumerable<SqlPredicate> asked)
        {
            var atoms = asked.SelectMany(r => r.Subterms).Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque)
                && t is not (SqlPredicate.Compare { Left: SqlValue.Parameter } or SqlPredicate.Compare { Right: SqlValue.Parameter })).DistinctBy(t => t.Digest).ToList();
            var list = atoms.OfType<SqlPredicate.In>().Select(i => i.Points.Count).DefaultIfEmpty(0).Max();
            var conditions = atoms.Count(t => t is not SqlPredicate.In { Points.Count: >= 5 });
            return list >= 20 || conditions >= 8 ? "rare" : list >= 5 ? "a list of 5-19" : conditions switch { <= 3 => "1-3 conditions", 4 => "4 conditions", _ => "5-7 conditions" };
        }

        static string? Show(RenderingVerdict? verdict, Verdict claimed = Verdict.Equal) => verdict is null ? null
            : verdict.Kind == RenderingVerdictKind.Proven ? claimed == Verdict.Equal ? "Exact" : "Implied" : verdict.Kind.ToString();

        /// <summary>
        /// A form read off a bundle, of a node at a position: an agreement the gate or <c>best</c> derived, an implied form,
        /// or a weakening, with the verdict it holds for.
        /// </summary>
        internal sealed record Judged(SqlPredicate Node, RenderPosition Position, ServiceForm Form, Verdict Claim, string What)
        {
            public const string Agreement = "agreement", Implied = "implied", Weakening = "weakening";
        }

        /// <summary>
        /// One bundle, read before it went: its set, run and engine failure, roots, generate mode's answer of each root or a
        /// check's verdict of each claimed conjunct, every form over IR-read atoms at both positions, what it imported and
        /// stated (source A samples it), its time from open to the roots' answers, the candidates its templates proposed
        /// beside its atoms, each form the gate refuted, the iterations its run took, the key the oracle filed it under, and a
        /// value of each cell its outcomes cut, by path, which C6 runs its forms over. Keyed by content, so by digests, never a
        /// statement's indices.
        /// </summary>
        internal sealed record Bundle(int Id, RenderingSet Set, RenderingRun? Run, Exception? Failure, IReadOnlyList<SqlPredicate> Roots,
            IReadOnlyDictionary<string, RenderingAnswer> Answers, IReadOnlyDictionary<string, RenderingVerdict> Verdicts, IReadOnlyList<Judged> Forms,
            RenderingMemo.Claimed? Claim, double Milliseconds, (int Candidates, int Atoms) Proposed = default, IReadOnlyList<Refuted>? Refutations = null, int Iterations = 0,
            RenderingQuestionKey? Key = null, IReadOnlyList<(string Path, string? Witness)>? Cells = null);

        /// <summary>A form of an atom at a position the gate refuted as exact, and the value each refuting cell sets its path to.</summary>
        internal sealed record Refuted(SqlPredicate Node, RenderPosition Position, ServiceForm Form, Refutation Refutation, IReadOnlyDictionary<string, string?> Witness);

        /// <summary>A pending conjunct in a round: whether S owns it, the translator's text of the round and its verdict, S's answer, the stratum round stated beneath, the bundle.</summary>
        internal sealed record Admission(string Input, string Name, int Statement, int Conjunct, int Round, RenderingSet Set, bool Owned, string? Text, string? TextVerdict,
            RenderingAnswer Answer, int? Stratum, int Bundle, double Milliseconds);

        /// <summary>
        /// A conjunct's class under a set: its reason where it is a regression, its IR, the translator's text, form and verdict,
        /// S's form, the translator's round, and the values the translator's text bound, which the printer is held to (g13).
        /// </summary>
        internal sealed record Classed(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, string Class, string? Reason, string Ir, string? Text,
            ServiceForm? TextForm, string? TextVerdict, ServiceForm? Form, int TranslatorRound, IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosParameter>? Bound = null);

        /// <summary>
        /// A residual's weakening under a set: J's text and form where J wrote one, and its verdict claimed implied at the
        /// weakening's key, S's, whether S leaves it residual and
        /// whether the translator pushed it, the bundle S's was read from and its import, whether S owns it, and for an opaque
        /// conjunct S weakens, its node and the binding it reads its fields through, which Calcite evaluates in process; for
        /// every weakening S gives, its statement, whose accessors the Rex builder writes it over (g14); and whether site W
        /// refused to push S's, its Rex unwritten or untranslated.
        /// </summary>
        internal sealed record Weakened(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, string Ir, string? Text, ServiceForm? Translator, string? TranslatorVerdict,
            ServiceForm? Mine, bool Residual, bool Pushed, int? Bundle, RenderingMemo.Claimed? Claim, bool Owned,
            (org.apache.calcite.rex.RexNode Conjunct, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings) Binding)? Opaque = null,
            bool TranslatorWritable = true, CosmosFilterStatement? Source = null, bool Refused = false);

        /// <summary>Site W's report: S's weakening of a residual as the split rule would push it, the text its Rex translates to (none where none), and whether it was applied beneath already.</summary>
        internal sealed record Pushed(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, ServiceForm Form, string? Text, bool Applied);

        /// <summary>
        /// A conjunct the rewrite changed under a set: the original and what it became, <c>null</c> where it was deleted; S's
        /// answer for the rewritten one and what its bundle imported, which source A samples; and the original's node and the
        /// binding it reads its fields through, which Calcite evaluates in process.
        /// </summary>
        internal sealed record Rewrite(string Input, string Name, int Statement, RenderingSet Set, SqlPredicate Original, SqlPredicate? Made, RenderingAnswer? Answer,
            RenderingMemo.Claimed? Claim, (org.apache.calcite.rex.RexNode Node, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings) Binding) Source);

        /// <summary>A statement's cost under a set, by its shape: the generate bundles it opened and S's time in them, first; asked again, from the memo.</summary>
        internal sealed record Cost(string Input, string Name, int Statement, RenderingSet Set, string Shape, int Bundles, double First, double Again, IReadOnlyList<SqlPredicate> Conjuncts);

        /// <summary>A translator's text read: its form, or none where it did not read back or is no claim the check makes; and the values it bound.</summary>
        internal sealed record Translated(string Written, ServiceForm? Form, IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosParameter> Parameters);

        /// <summary>What one statement brings to every set: its conjuncts, ownership, the translator's rounds and texts.</summary>
        sealed record Shape(RenderingInputs.Statement Input, int Index, CosmosFilterStatement Statement, bool[] Owned, int[] Round,
            (Scope? Context, PredicateRules? Rules) Root, IReadOnlyDictionary<int, Translated> Texts, IReadOnlyDictionary<int, Translated> Weakened)
        {
            public IReadOnlyList<SqlPredicate> Conjuncts => Statement.Statement.Conjuncts;
            public int Count => Conjuncts.Count;
            public IReadOnlyDictionary<long, SqlValue> Opaques => Statement.Statement.Opaques;
            public JsonFactTheory Declaration => Statement.DeclarationForCheck;
            public ImportSource Source => new(Statement.Statement.Digest, "declaration", 1);

            /// <summary>
            /// Reads a statement after its planning: its conjuncts' owners and the translator's round of each, zero for a
            /// residual; its root; and the translator's texts and weakenings, read back.
            /// </summary>
            public static Shape Of(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, IReadOnlyList<CaptureRecord> records)
            {
                var (scope, conjuncts) = (statement.ScopeForCheck, statement.Statement.Conjuncts);

                // J's weakening of an opaque conjunct is a form all the same: the conjunct is outside the IR, the weakening not,
                // and line 13b reads it against S's.
                Translated Read(CaptureRecord r) => CosmosSqlFormReader.Read(r.Text, r.Parameters) is var read
                    && RenderingCheckRun.Skip(r, read, r.Comparisons.Count + r.Rechecks.Count > 0) is var skip
                    && (skip is null || r.Site == 3 && skip == "outside the IR" && read.Written == r.Text && RenderingCheckRun.Opaque(read.Form) == false)
                    ? new(r.Text, read.Form, r.Parameters) : new(r.Text, null, r.Parameters);
                Dictionary<int, Translated> Site(int site) => records.Where(r => r.Statement == statement && r.Site == site).GroupBy(r => r.Conjunct).ToDictionary(g => g.Key, g => Read(g.First()));
                return new(input, index, statement, [.. conjuncts.Select(RenderingFamily.Owns)], [.. Enumerable.Range(0, conjuncts.Count).Select(scope.StratumOf)],
                    RenderingCheckRun.RootOf([scope])!.Value, Site(1), Site(3));
            }
        }

    }

}
