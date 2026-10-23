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
    /// The generate shadow (spec g05, §4.4, §5.6): for each statement the check run plans, riding its pass, the bundles
    /// production will open, one a round of the translator's strata, each imported from the statement's root with a later
    /// round's atoms stated beneath <see cref="StratumFor"/>, every pending conjunct, <c>Through(k)</c> and <c>Before(k)</c>
    /// its roots, and a closing bundle where one is needed; beside each, a check bundle of the same roots, import and
    /// strata claiming the translator's texts, so that no claimed text's parts enter <c>BestExact</c> and the two share one
    /// key. Under each set a leg plans under, asserted, and <c>Portable</c> beside. Each conjunct is classed against the
    /// translator, every agreement, implied form and weakening kept for source A and the pairwise check.
    /// </summary>
    /// <remarks>
    /// It follows the translator's rounds, whose nodes the facts graph built from the translator's admissions; S's own
    /// admissions would change a later round's node, which only production's sites (g15) see. A bundle is remembered by its
    /// content (the set, the roots and each opaque's reading, the import spelled, the claims), so a statement asked again
    /// opens none.
    /// </remarks>
    internal sealed class RenderingGenerateRun
    {

        /// <summary>
        /// The sets the legs plan under (O1): <c>Production</c> on a leg with no emulator, <c>Emulator</c> on one with it. Both
        /// are asserted on every leg.
        /// </summary>
        public static readonly RenderingSet[] AssertedSets = [RenderingSet.Production, RenderingSet.Emulator];

        /// <summary>The set reported beside the asserted ones, never asserted.</summary>
        public const RenderingSet BesideSet = RenderingSet.Portable;

        public static IEnumerable<RenderingSet> Sets => [.. AssertedSets, BesideSet];

        /// <summary>The classes of an owned conjunct's admission, and of a residual's weakening (path map §3.3; gates map §3.5).</summary>
        public const string Same = "same", Respelled = "respelled", Gain = "gain", ShownWrong = "translator shown wrong", Regression = "regression",
            Alike = "declined alike", Written = "translator-written", Engine = "engine", Unread = "translator text unread";

        public const string BandFilter = "band filter", AtTheRoot = "a stratum read at the root", Composite = "the composite unproven", OnItsMerits = "Unproven on its merits";

        static readonly RenderingGraph Measured = new(() => ProbeRows.Measured);

        static readonly ValueSet<string> Bands = ValueSet.From(ProbeRows.Measured.Parting.SelectMany(p => new[] { p.Item1, p.Item2 }));

        readonly RenderingGraph _graph;
        readonly ConcurrentDictionary<string, Lazy<Bundle>> _memo = new(StringComparer.Ordinal);
        int _next;

        public RenderingGenerateRun(RenderingGraph? graph = null) => _graph = graph ?? Measured;

        /// <summary>Every generate bundle opened, once each.</summary>
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

        /// <summary>
        /// Shadows one statement the check run planned, read after its planning, from the records its planning wrote: under
        /// each set, its rounds and what each class says of its conjuncts.
        /// </summary>
        public void Shadow(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, IReadOnlyList<CaptureRecord> records)
        {
            var shape = new Shape(input, index, statement, records);
            foreach (var set in Sets)
            {
                // First, each bundle's own time, as though S's memo held none; again, the keys and lookups alone.
                var opened = Reconstruct(shape, set, true);
                var first = opened.Sum(b => b.Milliseconds);
                var started = Stopwatch.GetTimestamp();
                Reconstruct(shape, set, false);
                Costs.Enqueue(new(input.Input, input.Name, index, set, ShapeOf(shape.Conjuncts), opened.Count, first, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }

            Rewritten(shape);
            GC.KeepAlive(statement);
        }

        /// <summary>
        /// The declared-fact rewrite's pairs (spec g05; line 33): where the rewrite changed a statement's conjuncts, each
        /// original beside what it became, in one bundle imported from the original's root under each set; the rewritten
        /// conjunct's best, where S proves one, claimed for the original in a check bundle of the same roots and import.
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
                foreach (var set in Sets)
                    Rewrites.Enqueue(made is null || original.Subterms.OfType<SqlPredicate.Opaque>().Any() && made.Subterms.OfType<SqlPredicate.Opaque>().Any()
                        ? new(s.Input.Input, s.Input.Name, s.Index, set, original, made, null, null)
                        : Pair(s, set, original, made, rewritten.Statement.Opaques));
            }

            GC.KeepAlive(rewritten);
        }

        Rewrite Pair(Shape s, RenderingSet set, SqlPredicate original, SqlPredicate made, IReadOnlyDictionary<long, OpaqueReading> opaques)
        {
            SqlPredicate[] roots = [original, made];
            ImportReport Import(RenderingBundle bundle) => RenderingImporter.Import(bundle, s.Source, s.Root.Context, s.Root.Rules, s.Declaration, roots.SelectMany(r => r.Paths));
            RenderingAnswer answer;
            using (var bundle = _graph.Open(set))
            {
                var (o, m) = (bundle.Write(original, s.Opaques), bundle.Write(made, opaques));
                Import(bundle);
                bundle.Generate(o);
                bundle.Generate(m);
                bundle.RunGenerate(RenderingBundle.WidthOf(roots));
                answer = bundle.ReadBest(m);
            }

            if (answer.Best is not { } best)
                return new(s.Input.Input, s.Input.Name, s.Index, set, original, made, answer, null);

            using var check = _graph.Open(set);
            var (to, tm) = (check.Write(original, s.Opaques), check.Write(made, opaques));
            Import(check);
            check.Claim(to, best);
            check.Run(64 + 8 * RenderingCheckRun.Leaves(best));
            return new(s.Input.Input, s.Input.Name, s.Index, set, original, made, answer, check.Read(to, best, Verdict.Equal));
        }

        /// <summary>
        /// The statement's bundles under a set, in the translator's rounds: each round asks S of its owned pending conjuncts
        /// and admits each proven, the composite is read where §5.2 reads it, and each residual's weakening from the round
        /// that admitted nothing it holds, or from a closing bundle. Returns the generate bundles opened.
        /// </summary>
        List<Bundle> Reconstruct(Shape s, RenderingSet set, bool record)
        {
            var (admitted, opened) = (new Dictionary<int, (int Round, ServiceForm Form, Stratum? Stratum)>(), new List<Bundle>());
            var rounds = new List<(int Round, Bundle Bundle, int[] Pending, int[] Asked, SqlPredicate? Through, SqlPredicate? Before, Dictionary<int, Stratum?> Strata)>();
            var (checks, askedUnder) = (new Dictionary<int, RenderingVerdict?>(), new Dictionary<int, int?>());
            for (var k = 1; k <= s.Rounds; k++)
            {
                var pending = Enumerable.Range(0, s.Count).Where(i => s.Round[i] == 0 || s.Round[i] >= k).ToArray();
                var asked = pending.Where(i => s.Owned[i] && admitted.ContainsKey(i) == false).ToArray();
                var (through, before) = (Junction(s, admitted.Keys.Concat(asked)), k > 1 ? Junction(s, admitted.Keys) : null);
                var stratum = StratumFor(s, k, admitted.Keys);
                var strata = pending.Where(i => admitted.ContainsKey(i) == false).ToDictionary(i => i, _ => stratum);
                foreach (var (i, a) in admitted)
                    strata[i] = a.Stratum;
                foreach (var i in pending.Where(i => s.Round[i] == k))
                    askedUnder[i] = stratum?.Round;

                var bundle = Ask(s, set, [.. strata.Keys.Order()], strata, [through, before]);
                opened.Add(bundle);
                rounds.Add((k, bundle, pending, asked, through, before, strata));
                foreach (var i in asked)
                    if (bundle.Answers[s.Conjuncts[i].Digest] is { Kind: RenderingVerdictKind.Proven, Best: { } form })
                        admitted[i] = (k, form, stratum);

                // The translator's texts of this round, claimed in a check bundle of the same roots, import and strata.
                var claims = pending.Where(i => s.Round[i] == k && s.Owned[i] && s.Texts.ContainsKey(i) && s.Texts[i].Form is not null).ToDictionary(i => i, i => s.Texts[i].Form!);
                if (claims.Count > 0)
                {
                    var check = Ask(s, set, [.. strata.Keys.Order()], strata, [through, before], claims);
                    foreach (var i in claims.Keys)
                    {
                        checks[i] = check.Verdicts[s.Conjuncts[i].Digest];
                        if (record && checks[i]!.Kind == RenderingVerdictKind.Proven && bundle.Answers[s.Conjuncts[i].Digest].Best is { } best)
                            Pairs.Enqueue((bundle, s.Conjuncts[i], claims[i], best));
                    }
                }

                if (record)
                    foreach (var i in pending)
                        Admissions.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, i, k, set, s.Owned[i], s.Round[i] == k ? s.Texts.GetValueOrDefault(i)?.Written : null,
                            s.Round[i] == k && checks.TryGetValue(i, out var v) ? Show(v) : null, bundle.Answers[s.Conjuncts[i].Digest], strata[i]?.Round, bundle.Id, bundle.Milliseconds));
            }

            // The composite (§5.2): Through(k) of the last round S admitted in, where it admitted all it was asked; else
            // Before of the round after; else Before in a closing bundle. Not proven, S admits nothing.
            var (composite, needsClosing) = ((RenderingAnswer?)null, false);
            if (admitted.Count > 0)
            {
                var last = rounds.Single(r => r.Round == admitted.Values.Max(a => a.Round));
                var after = rounds.FirstOrDefault(r => r.Round == last.Round + 1);
                composite = last.Asked.All(admitted.ContainsKey) ? last.Bundle.Answers[last.Through!.Digest]
                    : after.Bundle is not null ? after.Bundle.Answers[after.Before!.Digest] : null;
                needsClosing = composite is null;
            }

            var residual = Enumerable.Range(0, s.Count).Where(i => s.Owned[i] ? admitted.ContainsKey(i) == false : s.Round[i] == 0).ToList();
            var (closing, voided) = ((Bundle?)null, false);
            var answering = rounds.LastOrDefault(r => r.Bundle is not null && admitted.Values.All(a => a.Round < r.Round) && residual.All(r.Pending.Contains));
            var weakening = (answering.Bundle, answering.Strata, Junctions: new[] { answering.Through, answering.Before });
            if (needsClosing || residual.Count > 0 && answering.Bundle is null)
            {
                var final = StratumFor(s, s.Rounds + (s.Residual ? 1 : 0), admitted.Keys);
                var strata = residual.ToDictionary(i => i, _ => final);
                foreach (var (i, a) in admitted)
                    strata[i] = a.Stratum;

                var before = Junction(s, admitted.Keys);
                closing = Ask(s, set, [.. strata.Keys.Order()], strata, [before]);
                opened.Add(closing);
                weakening = (closing, strata, [before]);
                if (needsClosing)
                    composite = closing.Answers[before!.Digest];
            }

            // Round 1 asks every conjunct from the root, so where the composite voids S's admissions it answers the weakenings.
            if (composite is { Kind: not RenderingVerdictKind.Proven })
                (voided, residual, weakening) = (true, [.. Enumerable.Range(0, s.Count).Where(i => s.Owned[i] || s.Round[i] == 0)], (rounds[0].Bundle, rounds[0].Strata, [rounds[0].Through]));

            // J's weakening of each residual, claimed implied in a check bundle of the weakening's roots, import and strata.
            var theirs = new Dictionary<int, RenderingVerdict?>();
            if (record && weakening.Bundle is not null)
            {
                var claims = residual.Where(i => weakening.Strata.ContainsKey(i) && s.Weakened.GetValueOrDefault(i)?.Form is not null).ToDictionary(i => i, i => s.Weakened[i].Form!);
                if (claims.Count > 0)
                {
                    var check = Ask(s, set, [.. weakening.Strata.Keys.Order()], weakening.Strata, weakening.Junctions, claims, Verdict.Implies);
                    foreach (var i in claims.Keys)
                        theirs[i] = check.Verdicts[s.Conjuncts[i].Digest];
                }
            }

            if (record)
                Classify(s, set, admitted, voided, checks, askedUnder, [.. rounds.Select(r => r.Bundle).Append(closing).OfType<Bundle>()], residual, weakening.Bundle, theirs);

            return opened;
        }

        /// <summary>Each conjunct's class under a set, its reason where it is a regression, and each residual's weakening against J's.</summary>
        void Classify(Shape s, RenderingSet set, Dictionary<int, (int Round, ServiceForm Form, Stratum? Stratum)> admitted, bool voided,
            Dictionary<int, RenderingVerdict?> checks, Dictionary<int, int?> askedUnder, List<Bundle> bundles, List<int> residual, Bundle? weakening,
            Dictionary<int, RenderingVerdict?> theirs)
        {
            for (var i = 0; i < s.Count; i++)
            {
                var digest = s.Conjuncts[i].Digest;
                var verdict = checks.GetValueOrDefault(i);
                var engine = verdict?.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict || bundles.Any(b => b.Answers.TryGetValue(digest, out var x)
                    && (x.Kind is RenderingVerdictKind.Incomplete or RenderingVerdictKind.Conflict || x.Reason?.StartsWith("declined", StringComparison.Ordinal) == true));
                var form = admitted.TryGetValue(i, out var a) && voided == false ? a.Form : null;
                var text = s.Texts.GetValueOrDefault(i);
                var (@class, reason) = engine ? (Engine, null)
                    : s.Owned[i] == false ? (Written, null)
                    : s.Round[i] > 0 && verdict?.Kind == RenderingVerdictKind.Refuted ? (ShownWrong, null)
                    : s.Round[i] > 0 && form is not null && text?.Form is null ? (Unread, null)
                    : s.Round[i] > 0 && form is not null ? (form.Equals(text!.Form) ? Same : Respelled, null)
                    : s.Round[i] > 0 ? (Regression, Why(s, i, voided, bundles, askedUnder.GetValueOrDefault(i)))
                    : form is not null ? (Gain, null)
                    : (Alike, (string?)null);
                Classes.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, i, set, @class, reason, digest, text?.Written, text?.Form, Show(verdict), form, a.Round, s.Round[i]));
            }

            // Residuals: J weakened what the translator left above; S weakens what it leaves, read from one bundle.
            foreach (var i in residual.Union(Enumerable.Range(0, s.Count).Where(i => s.Round[i] == 0)).Order())
            {
                var mine = residual.Contains(i) && weakening is not null && weakening.Answers.TryGetValue(s.Conjuncts[i].Digest, out var answer) ? answer.Weakening : null;
                var j = s.Weakened.GetValueOrDefault(i);
                Weakenings.Enqueue(new(s.Input.Input, s.Input.Name, s.Index, i, set, s.Conjuncts[i].Digest, j?.Written, j?.Form, Show(theirs.GetValueOrDefault(i), Verdict.Implies), mine, residual.Contains(i), s.Round[i] > 0,
                    weakening?.Id, weakening?.Claim, s.Owned[i], mine is not null && s.Conjuncts[i].Subterms.Any(t => t is SqlPredicate.Opaque) ? (s.Statement.Conjuncts[i], s.Statement.BindingForCheck) : null));
            }
        }

        /// <summary>
        /// Why S declines a conjunct the translator pushes: a form the gate agreed exact that the band filter refused; the
        /// translator's stratum beneath which S could not state the round; the composite; or the conjunct on its merits.
        /// </summary>
        static string Why(Shape s, int i, bool voided, List<Bundle> bundles, int? stratum)
        {
            var digest = s.Conjuncts[i].Digest;
            if (voided)
                return Composite;

            if (bundles.SelectMany(b => b.Forms).Any(f => f.Node.Digest == digest && f.Position == RenderPosition.Pos && f.What == Judged.Agreement && f.Claim == Verdict.Equal
                    && RenderingPrimitives.Sendable.Function(f.Form, Bands) == false))
                return BandFilter;

            return (stratum ?? 1) < s.Round[i] ? AtTheRoot : OnItsMerits;
        }

        /// <summary>
        /// The deepest node of the translator's chain at or above round <paramref name="round"/>'s every conjunct of which
        /// it assumes S admitted and the facts graph reads exactly (<see cref="RenderingFamily.ReadExactly"/>); <c>null</c>
        /// for the root, beneath which nothing is stated.
        /// </summary>
        static Stratum? StratumFor(Shape s, int round, IEnumerable<int> admitted)
        {
            var held = admitted.ToHashSet();
            for (var j = Math.Min(round, s.Nodes.Count); j >= 2; j--)
                if (s.Nodes[j - 1] is var (node, assumes) && assumes.All(a => held.Contains(a) && RenderingFamily.ReadExactly(s.Conjuncts[a])))
                    return new(j, node, assumes);

            return null;
        }

        static SqlPredicate? Junction(Shape s, IEnumerable<int> conjuncts) => conjuncts.Distinct().ToList() is { Count: > 0 } all ? SqlPredicate.AllOf(all.Order().Select(i => s.Conjuncts[i])) : null;

        /// <summary>
        /// A bundle of the statement's conjuncts given and the junctions beside them, imported from the root, each
        /// conjunct's atoms stated beneath its stratum but those two conjuncts share; generate mode, or, with claims, a check
        /// of the translator's texts. Remembered by content.
        /// </summary>
        Bundle Ask(Shape s, RenderingSet set, List<int> conjuncts, IReadOnlyDictionary<int, Stratum?> strata, IEnumerable<SqlPredicate?> junctions,
            IReadOnlyDictionary<int, ServiceForm>? claims = null, Verdict claimed = Verdict.Equal)
        {
            var conjunctTerms = conjuncts.Select(i => s.Conjuncts[i]).ToList();
            var roots = conjunctTerms.Concat(junctions.OfType<SqlPredicate>()).DistinctBy(r => r.Digest).ToList();
            var shared = conjunctTerms.SelectMany(c => RenderingCheckRun.Atoms(c).Distinct()).GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var beneath = conjuncts.Where(i => strata[i] is not null)
                .SelectMany(i => RenderingCheckRun.Atoms(s.Conjuncts[i]).Distinct().Where(a => shared.Contains(a) == false).Select(a => (Conjunct: i, Atom: a, Stratum: strata[i]!))).ToList();
            ImportReport Import(RenderingBundle? bundle) => RenderingImporter.Import(bundle, s.Source, s.Root.Context, s.Root.Rules, s.Declaration, roots.SelectMany(r => r.Paths),
                beneath.Select(b => (b.Atom, new ImportSource(s.Statement.Statement.Digest, "render round " + b.Stratum.Round, b.Stratum.Round), b.Stratum.Node, s.Root.Rules)));

            var report = Import(null);
            var opaques = roots.SelectMany(r => r.Subterms).OfType<SqlPredicate.Opaque>().Select(o => o.Id).Distinct().Order().Select(id => id + "=" + s.Opaques.GetValueOrDefault(id)?.Top);
            var key = string.Join("\n", new[] { set.ToString(), report.Digest, string.Join(",", opaques), RenderingMemo.DigestOf(_graph.Current()) }
                .Concat(roots.Select(r => r.Digest).Order(StringComparer.Ordinal))
                .Concat(claims is null ? [] : claims.Select(c => s.Conjuncts[c.Key].Digest + " claims " + claimed + " " + c.Value.Text).Order(StringComparer.Ordinal)));
            return _memo.GetOrAdd(key, _ => new Lazy<Bundle>(() =>
            {
                var id = Interlocked.Increment(ref _next);
                var started = Stopwatch.GetTimestamp();
                using var bundle = _graph.Open(set);
                var nodes = roots.SelectMany(r => r.Subterms).DistinctBy(n => n.Digest).ToList();
                var terms = nodes.ToDictionary(n => n.Digest, n => bundle.Write(n, s.Opaques));
                var made = Import(bundle);
                var stated = (made.Stated ?? []).Select(x => beneath.First(b => b.Atom == x.Atom) is var b
                    ? new RenderingMemo.Beneath(conjuncts.IndexOf(b.Conjunct), x.Atom, new ImportSource(s.Statement.Statement.Digest, "render round " + b.Stratum.Round, b.Stratum.Round),
                        x.Assumed, [.. b.Stratum.Assumes.Select(a => conjuncts.IndexOf(a))]) : null!).ToList();

                if (claims is not null)
                {
                    foreach (var (i, form) in claims)
                        bundle.Claim(terms[s.Conjuncts[i].Digest], form);

                    var checkRun = bundle.Run(64 + 8 * claims.Values.Sum(RenderingCheckRun.Leaves));
                    // A conjunct admitted twice is one term, claimed once a text.
                    var verdicts = claims.GroupBy(c => s.Conjuncts[c.Key].Digest).ToDictionary(g => g.Key, g => bundle.Read(terms[g.Key], g.First().Value, claimed));
                    return new Bundle(id, set, checkRun, bundle.Failure, roots, new Dictionary<string, RenderingAnswer>(), verdicts, [], null,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                foreach (var root in roots)
                    bundle.Generate(terms[root.Digest]);

                var run = bundle.RunGenerate(RenderingBundle.WidthOf(roots));
                var answers = roots.ToDictionary(r => r.Digest, r => bundle.ReadBest(terms[r.Digest]));

                // S's time is production's: to the roots' answers, before what the tests read beside them.
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var proposed = (Candidates: bundle.Failure is null ? bundle.Scope.Rows(bundle.Theory.Candidate).Count() : 0,
                    Atoms: nodes.Count(n => n is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque)));
                var forms = new List<Judged>();
                if (bundle.Failure is null && run is { StopReason: RunStopReason.Completed, OneNodePerClass: true, Contradicted: false })
                    foreach (var node in nodes.Where(n => n.Subterms.Any(t => t is SqlPredicate.Opaque) == false))
                        foreach (var pos in Enum.GetValues<RenderPosition>())
                        {
                            var term = terms[node.Digest];
                            forms.AddRange(bundle.Agreements(term, pos).Select(a => new Judged(node, pos, a.Form, a.Claim, Judged.Agreement)));
                            if (bundle.Scope.TryGet(bundle.Theory.Implied.Of(term, pos), out var implied))
                                forms.AddRange(implied.Select(f => new Judged(node, pos, f, Verdict.Implies, Judged.Implied)));
                            if (bundle.Scope.TryGet(bundle.Theory.Weakening.Of(term, pos), out var weakening))
                                forms.Add(new Judged(node, pos, weakening, Verdict.Implies, Judged.Weakening));
                        }

                var distinct = forms.Distinct().ToList();
                var literals = distinct.Select(f => f.Form).DistinctBy(f => f.Text).ToList();
                var claim = bundle.Failure is null ? RenderingCheckRun.Claimed(bundle, conjunctTerms, literals.Count switch
                {
                    0 => ServiceForm.ConstantOf(true),
                    1 => literals[0],
                    _ => ServiceForm.JunctionOf(CosmosServiceTest.And, literals),
                }, [.. conjunctTerms.Select(_ => (ServiceForm?)null)], stated, null) : null;
                var generated = new Bundle(id, set, run, bundle.Failure, roots, answers, new Dictionary<string, RenderingVerdict>(), distinct, claim, elapsed, proposed);
                Bundles.Enqueue(generated);
                return generated;
            })).Value;
        }

        /// <summary>
        /// A residual's weakening against J's: the same form; S's tighter, keeping no document J's does not and dropping one
        /// it keeps, or looser, or the two parting both ways, on the documents source A draws from the bundle's import, on
        /// every target the set serves; S's alone or J's alone; dropped, a conjunct the translator pushed whole that S leaves
        /// with none; pushed exactly where S admits what J weakened; none.
        /// </summary>
        public static string ClassOf(Weakened w)
        {
            if (w.Residual == false)
                return w.Translator is null ? "none" : "pushed exactly";

            if (w.Mine is null)
                return w.Pushed ? "dropped" : w.Translator is null ? "none" : "translator-only";

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

            // A looser weakening is one O7 lists where J's is the text-equality alternatives (C3, not built) or unproven to S.
            var listed = Alternatives(w.Translator) ? " (O7: the text-equality alternatives)" : w.TranslatorVerdict != "Implied" ? " (O7: J's unproven to S)" : "";
            return (fewer, more) switch { (true, false) => "tighter", (false, true) => "looser" + listed, (true, true) => "parting" + listed, _ => "alike on the documents" };

            static bool Alternatives(ServiceForm f) => f.Kind == ServiceFormKind.Or && f.Members.All(m => m.Kind == ServiceFormKind.Compare && m.Test == CosmosServiceTest.Equal)
                && f.Members.Select(m => m.Path).Distinct().Count() == 1 && f.Members.Any(m => JsonValues.IsString(m.Literal!)) && f.Members.Any(m => JsonValues.IsString(m.Literal!) == false);
        }

        /// <summary>The shape of a statement's filter (§6.3): its conditions, a list of two to four counting as one, and a list of five or more apart.</summary>
        public static string ShapeOf(IReadOnlyList<SqlPredicate> conjuncts)
        {
            var atoms = conjuncts.SelectMany(c => c.Subterms).Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque)
                && t is not SqlPredicate.Compare { Right: SqlValue.Parameter }).ToList();
            var list = atoms.OfType<SqlPredicate.In>().Select(i => i.Points.Count).DefaultIfEmpty(0).Max();
            var conditions = atoms.Count(t => t is not SqlPredicate.In { Points.Count: >= 5 });
            return list >= 20 || conditions >= 8 ? "rare" : list >= 5 ? "a list of 5-19" : conditions switch { <= 3 => "1-3 conditions", 4 => "4 conditions", _ => "5-7 conditions" };
        }

        static string? Show(RenderingVerdict? verdict, Verdict claimed = Verdict.Equal) => verdict is null ? null
            : verdict.Kind == RenderingVerdictKind.Proven ? claimed == Verdict.Equal ? "Exact" : "Implied" : verdict.Kind.ToString();

        /// <summary>A later round's stratum: the round it renders, its node in the facts graph, and the conjuncts it assumes, by index.</summary>
        internal sealed record Stratum(int Round, Scope? Node, IReadOnlyList<int> Assumes);

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
        /// stated (source A samples it), its time from open to the roots' answers, and the candidates its templates proposed
        /// beside its atoms. Keyed by content, so by digests, never a statement's indices.
        /// </summary>
        internal sealed record Bundle(int Id, RenderingSet Set, RenderingRun? Run, Exception? Failure, IReadOnlyList<SqlPredicate> Roots,
            IReadOnlyDictionary<string, RenderingAnswer> Answers, IReadOnlyDictionary<string, RenderingVerdict> Verdicts, IReadOnlyList<Judged> Forms,
            RenderingMemo.Claimed? Claim, double Milliseconds, (int Candidates, int Atoms) Proposed = default);

        /// <summary>A pending conjunct in a round: whether S owns it, the translator's text of the round and its verdict, S's answer, the stratum round stated beneath, the bundle.</summary>
        internal sealed record Admission(string Input, string Name, int Statement, int Conjunct, int Round, RenderingSet Set, bool Owned, string? Text, string? TextVerdict,
            RenderingAnswer Answer, int? Stratum, int Bundle, double Milliseconds);

        /// <summary>
        /// A conjunct's class under a set: its reason where it is a regression, its IR, the translator's text, form and verdict,
        /// S's form and round, and the translator's round.
        /// </summary>
        internal sealed record Classed(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, string Class, string? Reason, string Ir, string? Text,
            ServiceForm? TextForm, string? TextVerdict, ServiceForm? Form, int Round, int TranslatorRound);

        /// <summary>
        /// A residual's weakening under a set: J's text and form where J wrote one, and its verdict claimed implied at the
        /// weakening's key, S's, whether S leaves it residual and
        /// whether the translator pushed it, the bundle S's was read from and its import, whether S owns it, and for an opaque
        /// conjunct S weakens, its node and the binding it reads its fields through, which Calcite evaluates in process.
        /// </summary>
        internal sealed record Weakened(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, string Ir, string? Text, ServiceForm? Translator, string? TranslatorVerdict,
            ServiceForm? Mine, bool Residual, bool Pushed, int? Bundle, RenderingMemo.Claimed? Claim, bool Owned,
            (org.apache.calcite.rex.RexNode Conjunct, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings) Binding)? Opaque = null);

        /// <summary>
        /// A conjunct the rewrite changed under a set: the original and what it became, <c>null</c> where it was deleted or
        /// the two hold opaque terms numbered apart; S's answer for the rewritten one, and its best claimed for the original.
        /// </summary>
        internal sealed record Rewrite(string Input, string Name, int Statement, RenderingSet Set, SqlPredicate Original, SqlPredicate? Made, RenderingAnswer? Answer, RenderingVerdict? Claimed);

        /// <summary>A statement's cost under a set, by its shape: the generate bundles it opened and S's time in them, first; asked again, from the memo.</summary>
        internal sealed record Cost(string Input, string Name, int Statement, RenderingSet Set, string Shape, int Bundles, double First, double Again);

        /// <summary>A translator's text read: its form, or none where it did not read back or is no claim the check makes.</summary>
        internal sealed record Translated(string Written, ServiceForm? Form);

        /// <summary>What one statement brings to every set: its conjuncts, ownership, the translator's rounds, nodes and texts.</summary>
        sealed class Shape
        {

            public Shape(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, IReadOnlyList<CaptureRecord> records)
            {
                (Input, Index, Statement) = (input, index, statement);
                var scope = statement.ScopeForCheck;
                Conjuncts = statement.Statement.Conjuncts;
                Count = Conjuncts.Count;
                Opaques = statement.Statement.Opaques;
                Owned = [.. Conjuncts.Select(RenderingFamily.Owns)];
                Round = [.. Enumerable.Range(0, Count).Select(scope.StratumOf)];
                Residual = scope.Residual.Count > 0;
                Root = RenderingCheckRun.RootOf([scope])!.Value;
                Declaration = statement.DeclarationForCheck;
                Source = new ImportSource(statement.Statement.Digest, "declaration", 1);

                // The chain: round j's node and what it assumes, read through a conjunct it rendered; the last, a residual's.
                var strata = scope.Strata.Count;
                Nodes = [.. Enumerable.Range(1, strata + (Residual ? 1 : 0)).Select(j => j <= strata ? scope.Strata[j - 1][0] : scope.Residual[0])
                    .Select(i => (scope.ContextFor(OracleLicence.RenderConjunct(i)).Context, scope.AssumedIn(OracleLicence.RenderConjunct(i))))];

                // The translator asked a round that admitted nothing where its chain ended complete and keeping something;
                // otherwise production closes in a bundle of its own.
                Rounds = strata + (Residual && scope.Complete && scope.IsContradictory(OracleLicence.RenderConjunct(scope.Residual[0])) == false ? 1 : 0);

                Translated Read(CaptureRecord r) => CosmosSqlFormReader.Read(r.Text, r.Parameters) is var read
                    && RenderingCheckRun.Skip(r, read, r.Comparisons.Count + r.Rechecks.Count > 0) is null ? new(r.Text, read.Form) : new(r.Text, null);
                Texts = records.Where(r => r.Statement == statement && r.Site == 1).GroupBy(r => r.Conjunct).ToDictionary(g => g.Key, g => Read(g.First()));
                Weakened = records.Where(r => r.Statement == statement && r.Site == 3).GroupBy(r => r.Conjunct).ToDictionary(g => g.Key, g => Read(g.First()));
            }

            public RenderingInputs.Statement Input { get; }

            public int Index { get; }

            public CosmosFilterStatement Statement { get; }

            public IReadOnlyList<SqlPredicate> Conjuncts { get; }

            public int Count { get; }

            public IReadOnlyDictionary<long, OpaqueReading> Opaques { get; }

            public bool[] Owned { get; }

            /// <summary>The translator's round of each conjunct, zero for a residual.</summary>
            public int[] Round { get; }

            public bool Residual { get; }

            /// <summary>The rounds a bundle is opened for: the translator's strata, and the round that admitted nothing where it asked one.</summary>
            public int Rounds { get; }

            public (Scope? Context, PredicateRules? Rules) Root { get; }

            public JsonFactTheory Declaration { get; }

            public ImportSource Source { get; }

            /// <summary>Round j's node at j − 1, and the conjuncts it assumes.</summary>
            public IReadOnlyList<(Scope? Node, IReadOnlyList<int> Assumes)> Nodes { get; }

            public IReadOnlyDictionary<int, Translated> Texts { get; }

            public IReadOnlyDictionary<int, Translated> Weakened { get; }

        }

    }

}
