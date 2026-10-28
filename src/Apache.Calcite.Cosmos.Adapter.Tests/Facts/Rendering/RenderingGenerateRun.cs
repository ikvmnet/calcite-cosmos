using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Generate mode's run (spec g05, g15, g17; §5.6): every bundle production's sites opened as the check run planned in the
    /// run's own schema, told through its probe (<see cref="ICosmosRenderingProbe"/>) — each statement's rounds asked at
    /// admission, one bundle a round, imported from the root with a later round's atoms stated beneath <c>StratumFor</c>,
    /// every pending conjunct, <c>Through(k)</c> and <c>Before(k)</c> its roots, and a closing bundle where one is needed —
    /// and what each statement sent: S's form of each conjunct it admitted, and its weakening of each it left residual. Under
    /// each set a leg plans under, asserted, and <c>Portable</c> beside, each in its own admissions; the statement's own set
    /// writes its plan.
    /// </summary>
    /// <remarks>
    /// The bundles are production's (<see cref="RenderingOracle.Ask"/>), read before they go through the oracle's hook; the run
    /// opens none of its own but the rewrite's pairs. Its schema's oracle is over the run's graph, so that every key's first
    /// question opens the bundle the run reads, and a plant's graph is the one production asks.
    /// </remarks>
    internal sealed class RenderingGenerateRun : ICosmosRenderingProbe
    {

        /// <summary>
        /// The sets the legs plan under (O1): <c>Production</c> on a leg with no emulator, <c>Emulator</c> on one with it. Both
        /// are asserted on every leg.
        /// </summary>
        public static readonly RenderingSet[] AssertedSets = [RenderingSet.Production, RenderingSet.Emulator];

        /// <summary>The set reported beside the asserted ones, never asserted.</summary>
        public const RenderingSet BesideSet = RenderingSet.Portable;

        public static IEnumerable<RenderingSet> Sets => [.. AssertedSets, BesideSet];

        readonly RenderingGraph _graph;
        readonly RenderingSet[] _sets;
        readonly ConcurrentDictionary<RenderingQuestionKey, Bundle> _opened = new();
        int _next;

        /// <param name="graph">The S it asks: the registry's, or a plant's of its own.</param>
        /// <param name="sets">The sets it reads under: those the legs plan under and the one beside, unless a plant asks fewer.</param>
        /// <param name="readExactly">A plant's reading precision, which production's <c>StratumFor</c> reads in its place (G12).</param>
        /// <param name="renderedFrom">A plant's node a conjunct is stated beneath in its own round (G11); none in the registry's run.</param>
        /// <param name="signature">A plant's name for a question within its terms (G9); none for the question's own.</param>
        public RenderingGenerateRun(RenderingGraph? graph = null, IEnumerable<RenderingSet>? sets = null, Func<SqlPredicate, bool>? readExactly = null,
            Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? renderedFrom = null, Func<string, string>? signature = null)
        {
            (_graph, _sets, ReadExactly, RenderedFrom, Signature) = (graph ?? RenderingCheckRun.Measured, [.. sets ?? Sets], readExactly, renderedFrom, signature);
            Oracle = new RenderingOracle(_graph, remembered: 1 << 20, wait: Timeout.InfiniteTimeSpan);
            Reasoning = new CosmosReasoning(new JsonFactGenerations(), Oracle, this);
        }

        /// <summary>The run's schema's oracle, over the run's graph: one bundle code path, production's.</summary>
        public RenderingOracle Oracle { get; }

        /// <summary>The run's schema, whose filters ask <see cref="Oracle"/> and tell the run: what its inputs are bound to.</summary>
        public CosmosReasoning Reasoning { get; }

        IReadOnlyList<RenderingSet> ICosmosRenderingProbe.Sets => _sets;

        /// <inheritdoc/>
        public Func<SqlPredicate, bool>? ReadExactly { get; }

        /// <inheritdoc/>
        public Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? RenderedFrom { get; }

        /// <inheritdoc/>
        public Func<string, string>? Signature { get; }

        /// <summary>Containers bound to the run's schema, so that what they plan is read here; each must be unbound, or it would reason elsewhere.</summary>
        public CosmosContainerMetadata[] Bind(IEnumerable<CosmosContainerMetadata> containers) =>
            [.. containers.Select(c => c.Reasoning is null ? c.WithReasoning(Reasoning) : throw new ArgumentException($"'{c.Name}' is bound to a schema already, which the run would not read."))];

        /// <summary>Every generate bundle production's sites opened, once each.</summary>
        public ConcurrentQueue<Bundle> Bundles { get; } = new();

        /// <summary>One row a conjunct a set: what the statement sent of it, S's form where S admitted it.</summary>
        public ConcurrentQueue<Sent> Sends { get; } = new();

        /// <summary>Each conjunct the declared-fact rewrite changed, beside what it became, under each set: line 33.</summary>
        public ConcurrentQueue<Rewrite> Rewrites { get; } = new();

        /// <summary>One row a residual conjunct a set: S's weakening of it.</summary>
        public ConcurrentQueue<Weakened> Weakenings { get; } = new();

        /// <summary>Each question S declined: an engine failure, a conflict, or no generation.</summary>
        public ConcurrentQueue<string> Declines { get; } = new();

        /// <summary>Each question a site asked, with the statement that asked it and the bundle that answered it: what <c>NoProjectionReachesS</c> reads.</summary>
        public ConcurrentQueue<Asking> Asked { get; } = new();

        /// <summary>
        /// A bundle production opened, read before it goes: each node's agreements, implied forms and weakenings at both
        /// positions, the refutations, the candidates proposed, and the claim source A samples, filed under the key the
        /// oracle files its answer under.
        /// </summary>
        void ICosmosRenderingProbe.Opened(CosmosFilterStatement statement, CosmosFilterRendering.Question question, RenderingBundle bundle, IReadOnlyList<Pred> terms,
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
            var generated = new Bundle(id, question.Set, run, bundle.Failure, question.Roots, answers, distinct, claim, elapsed.TotalMilliseconds, proposed, refuted, bundle.Iterations,
                key, witnesses);
            if (_opened.TryAdd(key, generated))
                Bundles.Enqueue(generated);
        }

        /// <summary>
        /// Reads one statement the check run planned, after its planning: under each set, the questions its rounds asked, what
        /// it sent of each conjunct and each residual's weakening; and the rewrite's pairs.
        /// </summary>
        public void Read(RenderingInputs.Statement input, int index, CosmosFilterStatement statement)
        {
            var (scope, conjuncts) = (statement.ScopeForCheck, statement.Statement.Conjuncts);
            int[] rounds = [.. Enumerable.Range(0, conjuncts.Count).Select(scope.StratumOf)];
            bool[] owned = [.. conjuncts.Select(RenderingFamily.Owns)];
            foreach (var set in _sets)
            {
                if (statement.RenderingFor(set) is not var (rendering, resolution))
                    throw new InvalidOperationException($"{input.Input} {input.Name}: statement {index} asked S nothing under {set}: planned outside the run's schema, or with S off.");

                Record(input, index, statement, rounds, owned, set, rendering, resolution);
            }

            Rewritten(input, index, statement, RenderingCheckRun.RootOf([scope])!.Value);
            GC.KeepAlive(statement);
        }

        /// <summary>
        /// A statement's admission under a set as production asked it: each question and the bundle that answered it, each
        /// conjunct's form where S admitted it, and each residual's weakening, read from one bundle.
        /// </summary>
        void Record(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, int[] rounds, bool[] owned, RenderingSet set, CosmosFilterRendering rendering,
            CosmosFilterRendering.Resolution resolution)
        {
            Bundle Generated(RenderingOracle.RenderingBundleAnswer answer) => answer.Key is { } key && _opened.TryGetValue(key, out var bundle) ? bundle
                : new Bundle(0, set, null, null, [], answer.Roots, [], null, 0);

            var questions = rendering.Rounds.Select(r => (r.Question, r.Answer)).ToList();
            if (resolution.WeakeningQuestion is { } wq && questions.All(q => ReferenceEquals(q.Question, wq) == false))
                questions.Add((wq, resolution.WeakeningAnswer!));

            foreach (var (question, answer) in questions)
            {
                Asked.Enqueue(new(input.Input, input.Name, index, statement, question, Generated(answer).Id));
                if (answer.Declined is { } declined)
                    Declines.Enqueue($"{input.Input} {input.Name}/{index} {set}: {declined} {answer.Roots.Values.FirstOrDefault()?.Reason}");
            }

            var conjuncts = statement.Statement.Conjuncts;
            for (var i = 0; i < conjuncts.Count; i++)
                Sends.Enqueue(new(input.Input, input.Name, index, i, set, conjuncts[i].Digest, owned[i], rounds[i], resolution.Forms.GetValueOrDefault(i), statement));

            // Each conjunct left residual here or in the statement's plan, S's weakening of those left here, read from one bundle.
            var weakening = resolution.WeakeningAnswer is { } wa ? Generated(wa) : null;
            foreach (var i in resolution.Residual.Union(Enumerable.Range(0, conjuncts.Count).Where(i => rounds[i] == 0)).Order())
            {
                var mine = resolution.Weakenings.GetValueOrDefault(i);
                Weakenings.Enqueue(new(input.Input, input.Name, index, i, set, conjuncts[i].Digest, mine, resolution.Residual.Contains(i), weakening?.Id, weakening?.Claim, owned[i],
                    mine is not null && conjuncts[i].Subterms.Any(t => t is SqlPredicate.Opaque) ? (statement.Conjuncts[i], statement.BindingForCheck) : null, mine is not null ? statement : null));
            }
        }

        /// <summary>
        /// The declared-fact rewrite's pairs (spec g05; line 33): where the rewrite changed a statement's conjuncts, each
        /// original beside what it became, the rewritten one asked of S in a bundle imported from the root under each set,
        /// with the original's node and binding, by which Calcite judges S's best for it in process (<c>RenderingRewriteTests</c>).
        /// </summary>
        void Rewritten(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, (Scope? Context, PredicateRules? Rules) root)
        {
            var rewritten = statement.RewrittenForCheck;
            if (ReferenceEquals(rewritten, statement))
                return;

            // A conjunct the rewrite left alone is the same node; the rest are paired in order, a deleted one left unpaired.
            var (kept, before) = (rewritten.Conjuncts.ToHashSet(ReferenceEqualityComparer.Instance), statement.Conjuncts.ToHashSet(ReferenceEqualityComparer.Instance));
            var originals = Enumerable.Range(0, statement.Conjuncts.Count).Where(i => kept.Contains(statement.Conjuncts[i]) == false).ToList();
            var lowered = Enumerable.Range(0, rewritten.Conjuncts.Count).Where(i => before.Contains(rewritten.Conjuncts[i]) == false).ToList();
            for (var k = 0; k < originals.Count; k++)
            {
                var original = statement.Statement.Conjuncts[originals[k]];
                var made = k < lowered.Count && originals.Count == lowered.Count ? rewritten.Statement.Conjuncts[lowered[k]] : null;
                var source = ((org.apache.calcite.rex.RexNode)statement.Statement.Sources[originals[k]]!, statement.BindingForCheck);
                foreach (var set in _sets)
                    Rewrites.Enqueue(made is null ? new(input.Input, input.Name, index, set, original, null, null, null, source)
                        : Pair(input, index, statement, root, set, original, made, rewritten.Statement.Opaques, source));
            }

            GC.KeepAlive(rewritten);
        }

        Rewrite Pair(RenderingInputs.Statement input, int index, CosmosFilterStatement statement, (Scope? Context, PredicateRules? Rules) root, RenderingSet set, SqlPredicate original,
            SqlPredicate made, IReadOnlyDictionary<long, SqlValue> opaques,
            (org.apache.calcite.rex.RexNode, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?>, IReadOnlyList<CosmosReading>)) source)
        {
            using var bundle = _graph.Open(set);
            var m = bundle.Write(made, opaques);
            RenderingImporter.Import(bundle, new ImportSource(statement.Statement.Digest, "declaration", 1), root.Context, root.Rules, statement.DeclarationForCheck, made.Paths.Concat(original.Paths));
            bundle.Generate(m);
            bundle.RunGenerate(RenderingBundle.WidthOf([made]));
            var answer = bundle.ReadBest(m);
            var claim = answer.Best is { } best && bundle.Failure is null ? RenderingCheckRun.Claimed(bundle, [made], best, [null], [], null, statement.DeclarationForCheck.Rules) : null;
            return new(input.Input, input.Name, index, set, original, made, answer, claim, source);
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

        /// <summary>
        /// A form read off a bundle, of a node at a position: an agreement the gate or <c>best</c> derived, an implied form,
        /// or a weakening, with the verdict it holds for.
        /// </summary>
        internal sealed record Judged(SqlPredicate Node, RenderPosition Position, ServiceForm Form, Verdict Claim, string What)
        {
            public const string Agreement = "agreement", Implied = "implied", Weakening = "weakening";
        }

        /// <summary>
        /// One bundle, read before it went: its set, run and engine failure, roots, generate mode's answer of each root, every
        /// form over IR-read atoms at both positions, what it imported and stated (source A samples it), its time from open to
        /// the roots' answers, the candidates its templates proposed beside its atoms, each form the gate refuted, the
        /// iterations its run took, the key the oracle filed it under, and a value of each cell its outcomes cut, by path,
        /// which C6 runs its forms over. Keyed by content, so by digests, never a statement's indices.
        /// </summary>
        internal sealed record Bundle(int Id, RenderingSet Set, RenderingRun? Run, Exception? Failure, IReadOnlyList<SqlPredicate> Roots,
            IReadOnlyDictionary<string, RenderingAnswer> Answers, IReadOnlyList<Judged> Forms, RenderingMemo.Claimed? Claim, double Milliseconds,
            (int Candidates, int Atoms) Proposed = default, IReadOnlyList<Refuted>? Refutations = null, int Iterations = 0, RenderingQuestionKey? Key = null,
            IReadOnlyList<(string Path, string? Witness)>? Cells = null);

        /// <summary>A form of an atom at a position the gate refuted as exact, and the value each refuting cell sets its path to.</summary>
        internal sealed record Refuted(SqlPredicate Node, RenderPosition Position, ServiceForm Form, Refutation Refutation, IReadOnlyDictionary<string, string?> Witness);

        /// <summary>A question a site asked of a statement under a set, and the bundle that answered it, <c>0</c> where none the run read did.</summary>
        internal sealed record Asking(string Input, string Name, int Index, CosmosFilterStatement Statement, CosmosFilterRendering.Question Question, int Bundle);

        /// <summary>
        /// A conjunct under a set: its IR, whether S owns it, the round the statement's own admission took it in, none for a
        /// residual, S's form where S admitted it under the set, and its statement.
        /// </summary>
        internal sealed record Sent(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, string Ir, bool Owned, int Round, ServiceForm? Form,
            CosmosFilterStatement Source);

        /// <summary>
        /// A residual's weakening under a set: S's, whether it is residual under the set, the bundle S's was read from and its
        /// import, whether S owns it, and for an opaque conjunct S weakens, its node and the binding it reads its fields
        /// through, which Calcite evaluates in process; and for every weakening S gives, its statement, whose accessors the
        /// Rex builder writes it over (g14).
        /// </summary>
        internal sealed record Weakened(string Input, string Name, int Statement, int Conjunct, RenderingSet Set, string Ir, ServiceForm? Mine, bool Residual, int? Bundle,
            RenderingMemo.Claimed? Claim, bool Owned,
            (org.apache.calcite.rex.RexNode Conjunct, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings) Binding)? Opaque,
            CosmosFilterStatement? Source);

        /// <summary>
        /// A conjunct the rewrite changed under a set: the original and what it became, <c>null</c> where it was deleted; S's
        /// answer for the rewritten one and what its bundle imported, which source A samples; and the original's node and the
        /// binding it reads its fields through, which Calcite evaluates in process.
        /// </summary>
        internal sealed record Rewrite(string Input, string Name, int Statement, RenderingSet Set, SqlPredicate Original, SqlPredicate? Made, RenderingAnswer? Answer,
            RenderingMemo.Claimed? Claim, (org.apache.calcite.rex.RexNode Node, (IReadOnlyList<Apache.Calcite.Cosmos.Adapter.Sql.CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings) Binding) Source);

    }

}
