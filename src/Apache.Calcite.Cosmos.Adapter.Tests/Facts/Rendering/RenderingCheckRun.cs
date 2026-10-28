using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The check run (spec §5.3, as Appendix E amends it): each input planned and implemented in a flow armed for the
    /// capture, then what the service is asked together checked as one junction in S: each <c>WHERE</c>
    /// sent, and each statement never sent as the conjunction its rounds admitted (<c>DESIGN.md</c>, <i>A check is what
    /// the service is asked together</i>).
    /// </summary>
    internal sealed class RenderingCheckRun
    {

        /// <summary>
        /// The set CI asserts under: every target the adapter serves, since the probe account's record of 2026-10-21 widened
        /// to Azure the outcomes it was run on.
        /// </summary>
        public const RenderingSet AssertedSet = RenderingSet.Portable;

        /// <summary>The set every check is read under beside the asserted one, and reported, never asserted.</summary>
        public const RenderingSet BesideSet = RenderingSet.Emulator;

        /// <summary>The targets the asserted set serves, each of which a proof must hold on.</summary>
        public static IEnumerable<RenderingTarget> Targets => ProbeRows.Measured.Serves[AssertedSet];

        /// <summary>One S a process, its generations defined from the registry's rows, which the shadow asks too.</summary>
        internal static readonly RenderingGraph Measured = new(() => ProbeRows.Measured);

        readonly RenderingGraph Graph;

        /// <summary>
        /// The pull request's run, once a process, in a reasoning of its own: the partition run's 200 predicates, or 1,000 at
        /// the gate; and generate mode's shadow over every statement it plans, riding its pass.
        /// </summary>
        public static readonly Lazy<RenderingCheckRun> PerPullRequest = new(() =>
        {
            var reasoning = new CosmosReasoning();
            return Over(RenderingInputs.All(CosmosDifferentialTests.PartitionedPredicates, reasoning).Append(RenderingInputs.Parameterised(reasoning)),
                shadow: new RenderingGenerateRun(askOracleEvery: 16));
        });

        readonly Action<RenderingSet, TimeSpan>? Timed;

        /// <param name="memo">The memo, or one of its own.</param>
        /// <param name="graph">The S it checks in: the registry's, or a plant's of its own.</param>
        /// <param name="timed">Told each bundle's time from open to dispose, with its set (the cost line).</param>
        /// <param name="shadow">Generate mode's shadow, asked of each statement once its checks are made; none where <c>null</c>.</param>
        public RenderingCheckRun(RenderingMemo? memo = null, RenderingGraph? graph = null, Action<RenderingSet, TimeSpan>? timed = null, RenderingGenerateRun? shadow = null) =>
            (Memo, Graph, Timed, Shadow) = (memo ?? new RenderingMemo(), graph ?? Measured, timed, shadow);

        public RenderingMemo Memo { get; }

        /// <summary>Generate mode's shadow of every statement planned, where the run carries one.</summary>
        public RenderingGenerateRun? Shadow { get; }

        /// <summary>Every conjunct a check holds, one row each, with what the check says.</summary>
        public ConcurrentQueue<Checked> Conjuncts { get; } = new();

        /// <summary>Every text captured at either site, once a text with its values inlined: what the reader is held to.</summary>
        public ConcurrentDictionary<string, (string Text, IReadOnlyList<CosmosParameter> Parameters, bool InFamily)> Captured { get; } = new();

        /// <summary>Every conjunct's IR a site wrote, by its digest: the atoms the inputs load.</summary>
        public ConcurrentDictionary<string, SqlPredicate> Irs { get; } = new();

        /// <summary>
        /// What the cross-checks found: a conjunct written two ways with its values inlined, a <c>WHERE</c> that is not the
        /// conjunction of the texts its statements sent, or a text claimed that does not read back.
        /// </summary>
        public ConcurrentQueue<string> Mismatches { get; } = new();

        /// <summary>Text a <c>WHERE</c> holds that no site wrote: written outside the translator.</summary>
        public ConcurrentQueue<(string Input, string Name, string Text)> Outside { get; } = new();

        /// <summary>A statement that failed to plan or to implement, or a check whose statements share no root.</summary>
        public ConcurrentQueue<(string Input, string Name, string Failure)> Failures { get; } = new();

        /// <summary>Checks every input; the corpus's share one harness and are planned one at a time, the rest two at a time.</summary>
        public static RenderingCheckRun Over(IEnumerable<RenderingInputs.Statement> inputs, Action<RenderingSet, TimeSpan>? timed = null, RenderingGenerateRun? shadow = null)
        {
            var run = new RenderingCheckRun(timed: timed, shadow: shadow);
            var all = inputs.ToList();
            foreach (var input in all.Where(i => i.Input == "corpus"))
                run.Plan(input);

            Parallel.ForEach(all.Where(i => i.Input != "corpus"), new ParallelOptions { MaxDegreeOfParallelism = 2 }, run.Plan);
            return run;
        }

        /// <summary>
        /// Checks a claim of a form for the conjunction of conjuncts, each with its own member of the form or none, under the
        /// asserted set and under <see cref="BesideSet"/>; memoised on the import the callback makes, which it
        /// is first asked with no bundle, for the report alone, and recording the atoms it stated beneath their strata.
        /// </summary>
        public (RenderingMemo.Entry Entry, ImportReport Import) Check(IReadOnlyList<SqlPredicate> conjuncts, ServiceForm form, IReadOnlyList<ServiceForm?> members,
            Verdict claim, Func<RenderingBundle?, ImportReport> import, IReadOnlyList<RenderingMemo.Beneath>? stated = null, IReadOnlyList<JsonFactRule>? rules = null)
        {
            var ir = conjuncts.Count == 1 ? conjuncts[0] : new SqlPredicate.And(conjuncts);
            var report = import(null);
            var key = new RenderingMemoKey(ir.Digest, report.Digest, form.Text, claim, AssertedSet, RenderingAnswerMemo.DigestOf(Graph.Current()));
            return (Memo.GetOrAdd(key, id =>
            {
                var started = Stopwatch.GetTimestamp();
                var (asserted, run, alone, failure, what) = Bundle(AssertedSet);
                Timed?.Invoke(AssertedSet, Stopwatch.GetElapsedTime(started));
                started = Stopwatch.GetTimestamp();
                var (beside, besideRun, _, declined, _) = Bundle(BesideSet);
                Timed?.Invoke(BesideSet, Stopwatch.GetElapsedTime(started));
                return new RenderingMemo.Entry(id, key, asserted, run, beside, besideRun, alone, failure ?? declined, what);
            }), report);

            (RenderingVerdict, RenderingRun, IReadOnlyList<RenderingVerdict?>, Exception?, RenderingMemo.Claimed?) Bundle(RenderingSet set)
            {
                using var bundle = Graph.Open(set);
                var each = conjuncts.Select(bundle.Write).ToList();
                var root = bundle.Write(ir);
                import(bundle);
                bundle.Claim(root, form);

                // A count limit, never time: descent and evaluation peel a junction a member at a time, so it grows with the form.
                var run = bundle.Run(CheckLimit([form]));
                // No exact join (spec §5.1), read from S's rows: a conjunct its own written member was never offered to.
                var verdict = bundle.Read(root, form, claim);
                if (verdict is { Kind: RenderingVerdictKind.Unproven, Reason: null } && bundle.Failure is null
                    && each.Where((c, i) => members[i] is { } member && bundle.Scope.Check(bundle.Theory.Candidate.Of(c, RenderPosition.Pos, member)) == false).Any())
                    verdict = verdict with { Reason = "no exact join" };

                return (verdict, run, [.. each.Select((c, i) => members[i] is { } member ? bundle.Read(c, member, claim) : null)], bundle.Failure,
                    bundle.Failure is null && set == AssertedSet ? Claimed(bundle, conjuncts, form, members, stated ?? [], verdict.Refutation, rules) : null);
            }
        }

        /// <summary>
        /// What a bundle claimed and imported, read off its rows, the atoms stated beneath their strata, the value at each of a
        /// refutation's cells, and the declaration's rules, by which source A rules a document out.
        /// </summary>
        internal static RenderingMemo.Claimed Claimed(RenderingBundle bundle, IReadOnlyList<SqlPredicate> conjuncts, ServiceForm form, IReadOnlyList<ServiceForm?> members,
            IReadOnlyList<RenderingMemo.Beneath> stated, Refutation? refutation, IReadOnlyList<JsonFactRule>? rules)
        {
            var (scope, theory) = (bundle.Scope, bundle.Theory);
            IReadOnlyDictionary<object, T> Rows<T>(Function<JPath, T> table) => scope.Rows(table).ToDictionary(r => r.Keys[0], r => (T)r.Output!);
            var (values, chars) = (Rows(theory.Imported), Rows(theory.ImportedChars));
            var cells = refutation is null ? [] : Cells(bundle);
            return new(conjuncts, form, members, Rows(theory.Spelling).Where(s => values.ContainsKey(s.Key)).ToDictionary(s => s.Value, s => new RenderingMemo.Imported(values[s.Key], chars[s.Key])),
                stated, refutation?.Cells.Select(c => cells[c]).ToDictionary(c => c.Path, c => c.Cell.Witness()), rules);
        }

        /// <summary>Checks another form, and its members, for what an entry claimed, over what it imported and stated.</summary>
        public RenderingMemo.Entry Recheck(RenderingMemo.Claimed claimed, ServiceForm form, IReadOnlyList<ServiceForm?> members) =>
            Check(claimed.Conjuncts, form, members, Verdict.Equal, bundle => Restate(bundle, claimed), claimed.Stated, claimed.Rules).Entry;

        /// <summary>Imports what a check imported into another bundle, and states what it stated, from a root of the same statements.</summary>
        internal static ImportReport Restate(RenderingBundle? bundle, RenderingMemo.Claimed claimed)
        {
            var root = new ImportSource(string.Join("+", claimed.Stated.Select(b => b.Source.Statement).Distinct().DefaultIfEmpty("recheck")), "recheck", 1);
            foreach (var (path, import) in claimed.Imports)
                bundle?.Import(root, CosmosEval.PathOf(path), import.Values, import.Chars);

            foreach (var beneath in claimed.Stated)
                bundle?.Assume(beneath.Source, beneath.Atom, beneath.Assumed);

            return new ImportReport(claimed.Imports.Count, 0, ValueSet<string>.Empty, string.Join(",", claimed.Imports.Select(i => RenderingImporter.Spell(CosmosEval.PathOf(i.Key), i.Value.Values, i.Value.Chars))
                .Concat(claimed.Stated.Select(b => b.Atom + " beneath " + RenderingImporter.Spell(CosmosEval.PathOf(b.Path), b.Assumed, ValueSet<string>.Empty)))), false);
        }

        /// <summary>
        /// The one node every statement of a check is imported from: their root, which assumes none of their conjuncts, and
        /// so none the check holds; <c>null</c> where two are rooted apart.
        /// </summary>
        internal static (Scope? Context, PredicateRules? Rules)? RootOf(IEnumerable<CosmosStatementScope> scopes)
        {
            var roots = scopes.Select(s => s.ContextFor(OracleLicence.Declaration)).ToList();
            return roots.All(r => ReferenceEquals(r.Context, roots[0].Context)) ? roots[0] : null;
        }

        void Plan(RenderingInputs.Statement input)
        {
            var recorder = new RenderingRecorder();
            var queries = new List<(string? Where, int From, int To)>();
            using (recorder.Arm())
            {
                RelNode planned;
                try
                {
                    planned = input.Plan();
                }
                catch (Exception e)
                {
                    Failures.Enqueue((input.Input, input.Name, "plans: " + PlanDigests.Failure(e)));
                    return;
                }

                foreach (var cosmos in PlanDigests.Subtrees(planned, n => n is CosmosRel))
                {
                    if (PlannerHarness.TableOf(cosmos) is not CosmosTable table)
                        continue;

                    var from = recorder.Records.Count;
                    try
                    {
                        var implementor = new CosmosImplementor(cosmos.getCluster().getRexBuilder(), table.Container);
                        implementor.Visit(cosmos);
                        queries.Add((implementor.Query.Where, from, recorder.Records.Count));
                    }
                    catch (Exception e)
                    {
                        Failures.Enqueue((input.Input, input.Name, "implements: " + PlanDigests.Failure(e)));
                    }
                }
            }

            // Read after the statement, never during its planning: the importer opens nothing planning did not.
            var records = recorder.Records;
            var statements = records.Select(r => r.Statement).Distinct().ToList();
            foreach (var record in records)
            {
                Captured.TryAdd(CosmosSqlFormReader.Inline(record.Text, record.Parameters), (record.Text, record.Parameters,
                    Parts(IrOf(record)).All(p => p is not (SqlPredicate.Opaque or SqlValue.Parameter))));
                Irs.TryAdd(IrOf(record).Digest, IrOf(record));
            }

            foreach (var conjunct in records.GroupBy(r => (r.Statement, r.Conjunct)).Where(g => g.Select(r => CosmosSqlFormReader.Inline(r.Text, r.Parameters)).Distinct().Count() > 1))
                Mismatches.Enqueue($"{input.Input} {input.Name}: conjunct {conjunct.Key.Conjunct} written as {string.Join(" and as ", conjunct.Select(r => r.Text).Distinct())}");

            var unit = 0;
            foreach (var (where, from, to) in queries)
            {
                var sent = records.Take(to).Skip(from).Where(r => r.Site == 2).ToList();
                Account(input, where, sent);
                if (sent.Count > 0)
                    CheckUnit(input, unit++, statements, records, sent, where!, [.. sent.SelectMany(r => r.Parameters).DistinctBy(p => p.Name)]);
            }

            // A statement never sent is claimed as Translate would write what its rounds admitted, each conjunct's names apart.
            foreach (var statement in statements.Where(s => records.Any(r => r.Statement == s && r.Site == 1) && records.All(r => r.Statement != s || r.Site != 2)))
            {
                var admitted = records.Where(r => r.Statement == statement && r.Site == 1).ToList();
                CheckUnit(input, unit++, statements, records, admitted,
                    Built(statement, i => admitted.FirstOrDefault(r => r.Conjunct == i) is { } r ? CosmosSqlFormReader.Rename(r.Text, "_" + i) : null),
                    [.. admitted.SelectMany(r => r.Parameters.Select(p => p with { Name = p.Name + "_" + r.Conjunct }))]);
            }

            // The split rule weakens each time it is asked, and a residual conjunct renders one way from its statement's facts.
            foreach (var weakening in records.Where(r => r.Site == 3).DistinctBy(r => (r.Statement, r.Conjunct, CosmosSqlFormReader.Inline(r.Text, r.Parameters))))
                CheckWeakening(input, unit++, statements, weakening);

            // The shadow reads the same scopes, after planning, each statement's records its own; and the statements none of
            // whose conjuncts rendered, which wrote none.
            var read = statements.Concat(recorder.Read.Except(statements)).ToList();
            if (Shadow is { } shadow)
                foreach (var statement in read)
                {
                    try
                    {
                        shadow.Shadow(input, read.IndexOf(statement), statement, records);
                    }
                    catch (Exception e)
                    {
                        Failures.Enqueue((input.Input, input.Name, "shadows: " + PlanDigests.Failure(e)));
                    }
                }

            GC.KeepAlive(records);
            GC.KeepAlive(read);
        }

        /// <summary>
        /// Checks a residual conjunct's weakening (spec §3.17): claimed as implied by the conjunct, and imported from the
        /// stratum it was rendered from, which assumes no conjunct but those the split rule pushes beside it, every one;
        /// or from the root, where the facts graph reads one of those otherwise than Calcite (<see cref="RenderingFamily.ReadExactly"/>).
        /// </summary>
        void CheckWeakening(RenderingInputs.Statement input, int unit, List<CosmosFilterStatement> statements, CaptureRecord record)
        {
            var (ir, read, statement) = (IrOf(record), CosmosSqlFormReader.Read(record.Text, record.Parameters), record.Statement);
            var skip = Skip(record, read, record.Comparisons.Count > 0);
            var (entry, report, round) = ((RenderingMemo.Entry?)null, (ImportReport?)null, Enumerable.Range(0, statement.Conjuncts.Count).Max(statement.StratumOf) + 1);
            if (skip is null)
            {
                var node = statement.ScopeForCheck.AssumedIn(OracleLicence.RenderConjunct(record.Conjunct)).All(j => RenderingFamily.ReadExactly(statement.Statement.Conjuncts[j]))
                    ? statement.ScopeForCheck.ContextFor(OracleLicence.RenderConjunct(record.Conjunct)) : RootOf([statement.ScopeForCheck])!.Value;
                var source = new ImportSource(statement.Statement.Digest, "weakening", round);
                (entry, report) = Check([ir], read.Form, [read.Form], Verdict.Implies,
                    bundle => RenderingImporter.Import(bundle, source, node.Context, node.Rules, statement.DeclarationForCheck, ir.Paths), rules: statement.DeclarationForCheck.Rules);
            }

            Conjuncts.Enqueue(new Checked(input.Input, input.Name, unit, statements.IndexOf(statement), record.Conjunct, record.Site, ir.Digest, record.Text, skip, entry, 0, report,
                statement.DeclarationForCheck.IsConditional, round > 1, record.Text, record.Parameters));
        }

        /// <summary>
        /// Checks one text the service is asked, or would be, as one junction of every conjunct it holds: claimed as the
        /// reader reads the text, and imported from the statements' root. A conjunct skipped is written as an opaque term,
        /// which no form agrees with, so a junction that holds one is never proven: nothing the service reads beside a
        /// checked conjunct is left out of its check.
        /// </summary>
        void CheckUnit(RenderingInputs.Statement input, int unit, List<CosmosFilterStatement> statements, IReadOnlyList<CaptureRecord> records,
            List<CaptureRecord> held, string text, IReadOnlyList<CosmosParameter> parameters)
        {
            var claimed = CosmosSqlFormReader.Read(text, parameters);
            if (claimed.Written != text)
            {
                Mismatches.Enqueue($"{input.Input} {input.Name}: {text} reads back as {claimed.Written}");
                return;
            }

            var reads = held.Select(r => CosmosSqlFormReader.Read(r.Text, r.Parameters)).ToList();
            var skips = held.Select((r, i) => Skip(r, reads[i], records.Any(x => x.Statement == r.Statement && x.Conjunct == r.Conjunct && x.Comparisons.Count + x.Rechecks.Count > 0))).ToList();
            var (entry, report) = ((RenderingMemo.Entry?)null, (ImportReport?)null);
            if (skips.Any(s => s is null))
            {
                if (RootOf(held.Select(r => r.Statement.ScopeForCheck).Distinct()) is not { } root)
                {
                    Failures.Enqueue((input.Input, input.Name, "checks: two statements rooted apart"));
                    return;
                }

                var conjuncts = held.Select((r, i) => skips[i] is null ? IrOf(r) : new SqlPredicate.Opaque(-1 - i)).ToList();
                var source = new ImportSource(string.Join("+", held.Select(r => r.Statement.Statement.Digest).Distinct()), "declaration", 1);
                var beneath = Beneath(held, skips);
                ImportReport Import(RenderingBundle? bundle) => RenderingImporter.Import(bundle, source, root.Context, root.Rules, held[0].Statement.DeclarationForCheck,
                    conjuncts.SelectMany(c => c.Paths), beneath.Select(b => (b.Atom, b.Source, b.Node, b.Rules)));
                var stated = (Import(null).Stated ?? []).Select(s => beneath.First(b => b.Atom == s.Atom) is var b ? new RenderingMemo.Beneath(b.Conjunct, s.Atom, b.Source, s.Assumed, b.Assumes) : null!).ToList();
                (entry, report) = Check(conjuncts, claimed.Form, [.. reads.Select((r, i) => skips[i] is null ? r.Form : null)], Verdict.Equal, Import, stated, held[0].Statement.DeclarationForCheck.Rules);
            }

            var (conditional, later) = (held[0].Statement.DeclarationForCheck.IsConditional, held.Any(r => r.Statement.StratumOf(r.Conjunct) > 1));
            for (var i = 0; i < held.Count; i++)
                Conjuncts.Enqueue(new Checked(input.Input, input.Name, unit, statements.IndexOf(held[i].Statement), held[i].Conjunct, held[i].Site, IrOf(held[i]).Digest,
                    held[i].Text, skips[i], skips[i] is null ? entry : null, i, report, conditional, later, text, parameters));
        }

        /// <summary>Holds each <c>WHERE</c> built to the conjunction of the texts its statements sent, as <c>Translate</c> writes one.</summary>
        void Account(RenderingInputs.Statement input, string? where, List<CaptureRecord> sent)
        {
            var rest = where ?? "";
            foreach (var group in sent.GroupBy(r => r.Statement))
            {
                var built = Built(group.Key, i => group.FirstOrDefault(r => r.Conjunct == i)?.Text);
                var at = rest.IndexOf(built, StringComparison.Ordinal);
                if (at < 0)
                    Mismatches.Enqueue($"{input.Input} {input.Name}: sent {built}, and the WHERE is {where}");
                else
                    rest = rest.Remove(at, built.Length);
            }

            if (rest.Replace("(", "").Replace(")", "").Replace(" AND ", "").Trim() is { Length: > 0 })
                Outside.Enqueue((input.Input, input.Name, rest));
        }

        static SqlPredicate IrOf(CaptureRecord record) => record.Statement.Statement.Conjuncts[record.Conjunct];

        /// <summary>
        /// Each atom of a later round's conjunct, with the node the licence table rendered that conjunct from, where that
        /// stratum may be read beside its atoms: every conjunct it assumes is judged in this check and read by the facts
        /// graph as Calcite reads it (<see cref="RenderingFamily.ReadExactly"/>). A cell the stratum admits nothing of is then held only by
        /// documents a conjunct of the same junction drops in Calcite, and so, agreeing, at the service. An atom two
        /// conjuncts share is read beneath neither, since one of them may be what the other's stratum assumes.
        /// </summary>
        static List<(int Conjunct, SqlPredicate Atom, ImportSource Source, Scope? Node, PredicateRules? Rules, int[] Assumes)> Beneath(List<CaptureRecord> held, List<string?> skips)
        {
            var judged = Enumerable.Range(0, held.Count).Where(i => skips[i] is null).ToList();
            var shared = Shared(judged.Select(i => IrOf(held[i])));
            var beneath = new List<(int, SqlPredicate, ImportSource, Scope?, PredicateRules?, int[])>();
            foreach (var i in judged)
            {
                var (statement, conjunct) = (held[i].Statement, held[i].Conjunct);
                var assumes = statement.ScopeForCheck.AssumedIn(OracleLicence.RenderConjunct(conjunct))
                    .Select(j => judged.FirstOrDefault(k => held[k].Statement == statement && held[k].Conjunct == j, -1)).ToArray();
                if (statement.StratumOf(conjunct) <= 1 || assumes.Any(k => k < 0 || RenderingFamily.ReadExactly(IrOf(held[k])) == false))
                    continue;

                var node = statement.ScopeForCheck.ContextFor(OracleLicence.RenderConjunct(conjunct));
                var source = new ImportSource(statement.Statement.Digest, "render conjunct " + conjunct, statement.StratumOf(conjunct));
                beneath.AddRange(Atoms(IrOf(held[i])).Distinct().Where(a => shared.Contains(a) == false).Select(a => (i, a, source, node.Context, node.Rules, assumes)));
            }

            return beneath;
        }

        /// <summary>The atoms two conjuncts share, which neither's stratum is stated beneath, since one may be what the other's assumes.</summary>
        internal static HashSet<SqlPredicate> Shared(IEnumerable<SqlPredicate> conjuncts) =>
            [.. conjuncts.SelectMany(c => Atoms(c).Distinct()).GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key)];

        /// <summary>Each cell a bundle's outcomes cut, by its spelling, with its path: what a refutation's cells name.</summary>
        internal static Dictionary<string, (string Path, PathCell Cell)> Cells(RenderingBundle bundle) => bundle.Scope.Rows(bundle.Theory.Outcomes).Select(r => (CellOutcomes)r.Output!)
            .SelectMany(o => o.Cells.Select(at => (Spelled: o.Path + "=" + at.Cell.Text, o.Path, at.Cell))).DistinctBy(c => c.Spelled).ToDictionary(c => c.Spelled, c => (c.Path, c.Cell));

        /// <summary>The atoms of a conjunct that read one path: what a cell of it is cut for.</summary>
        internal static IEnumerable<SqlPredicate> Atoms(SqlPredicate conjunct) =>
            conjunct.Subterms.Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque) && t.Paths.Count() == 1);

        internal static string? Skip(CaptureRecord record, ReadForm read, bool rechecked) =>
            Parts(IrOf(record)).Any(p => p is SqlValue.Parameter) || record.Parameters.Any(p => p.Value is CosmosDynamicValue) || rechecked ? "parameter"
            : Parts(IrOf(record)).Any(p => p is SqlPredicate.Opaque) ? "outside the IR"
            : read.Written != record.Text || Opaque(read.Form) ? "unreadable"
            : null;

        internal static bool Opaque(ServiceForm form) => form.Kind == ServiceFormKind.Opaque || form.Members.Any(Opaque);

        internal static int Leaves(ServiceForm form) => form.Members.Count == 0 ? 1 : form.Members.Sum(Leaves);

        /// <summary>A check's count limit over the forms it claims: 64 iterations and 8 a leaf, since descent and evaluation peel a junction a member at a time.</summary>
        internal static int CheckLimit(IEnumerable<ServiceForm> forms) => 64 + 8 * forms.Sum(Leaves);

        /// <summary>Every predicate and value in the IR, depth first.</summary>
        static IEnumerable<object> Parts(SqlPredicate predicate) => predicate.Subterms.SelectMany(term => term switch
        {
            SqlPredicate.Compare c => Values(c.Left).Concat(Values(c.Right)),
            SqlPredicate.In i => Values(i.Value),
            SqlPredicate.IsNull n => Values(n.Value),
            SqlPredicate.IsDefined d => Values(d.Value),
            _ => [term],
        });

        static IEnumerable<object> Values(SqlValue value) => value switch
        {
            SqlValue.Cast c => Values(c.Value).Prepend(value),
            SqlValue.Call c => c.Operands.SelectMany(Values).Prepend(value),
            _ => [value],
        };

        /// <summary>
        /// The conjunction of a statement's texts as <c>Translate</c> writes it, those with none left out: every <c>AND</c>
        /// of the condition a parenthesised junction of what it holds, and one of a single member that member.
        /// </summary>
        static string Built(CosmosFilterStatement statement, Func<int, string?> text)
        {
            var next = 0;
            string? Walk(RexNode node)
            {
                if (node is not RexCall call || call.getKind() != SqlKind.AND)
                    return text(next++);

                var parts = Enumerable.Range(0, call.getOperands().size()).Select(i => Walk((RexNode)call.getOperands().get(i))).OfType<string>().ToList();
                return parts.Count switch { 0 => null, 1 => parts[0], _ => "(" + string.Join(" AND ", parts) + ")" };
            }

            return Walk(statement.Condition) ?? "";
        }

        /// <summary>
        /// One conjunct a check holds: its input, the check's number within it, its filter statement's number and its own
        /// index, the site its text was read from (2 where it was sent, 3 its weakening), its IR and text, why it was skipped,
        /// the check and its position in it, the check's import, whether the declaration has conditional rules the facts graph
        /// reads, and whether the check holds a conjunct a later round admitted, and the text the check claimed, with the
        /// values its parameters were bound to.
        /// </summary>
        public sealed record Checked(string Input, string Name, int Unit, int Statement, int Conjunct, int Site, string Ir, string Text, string? Skip,
            RenderingMemo.Entry? Entry, int Position, ImportReport? Import, bool Conditional, bool LaterRound,
            string? Claimed = null, IReadOnlyList<CosmosParameter>? Bound = null);

    }

}
