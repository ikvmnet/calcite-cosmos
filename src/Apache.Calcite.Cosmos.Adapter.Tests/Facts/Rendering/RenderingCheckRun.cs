using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The check run (spec §5.3, as Appendix E amends it): each input statement planned and implemented in a flow armed for
    /// the capture and for <c>D0</c>; then each filter statement a round admitted, read off the texts written, imported
    /// from its licensed node and checked in a bundle of S as one junction of its conjuncts, claimed against their
    /// conjunction as built, so that the statement's <c>AND</c> licence and range need are read across them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A conjunct is skipped for a parameter, for an IR outside the reader's family (<c>Opaque</c> anywhere), or for a
    /// text the reader reads back otherwise or reads as opaque; never for what it means. The rest of a statement's
    /// conjuncts are checked together. Their import is the node of the earliest round among them, which every later
    /// round's node lies beneath: weaker facts, so a later conjunct may be unproven there, never wrongly proven.
    /// </para>
    /// <para>
    /// The run decides only whether a verdict may be read; S decides what it is. Each check is memoised by content,
    /// and read under <see cref="AssertedSet"/> and, beside it, under <see cref="RenderingSet.Portable"/>.
    /// </para>
    /// </remarks>
    internal sealed class RenderingCheckRun
    {

        /// <summary>
        /// The set CI asserts under: the emulator's, until c09b commits the probe account's record and widens to Azure the
        /// outcomes it was run on (<c>TODO.md</c>), when this becomes <see cref="RenderingSet.Portable"/>.
        /// </summary>
        public const RenderingSet AssertedSet = RenderingSet.Emulator;

        /// <summary>One S a process, its generations defined from the registry's rows.</summary>
        static readonly RenderingGraph Graph = new(() => ProbeRows.Measured);

        /// <summary>The pull request's run, once a process, in generations of its own: the partition run's 200 predicates, or 1,000 at the gate.</summary>
        public static readonly Lazy<RenderingCheckRun> PerPullRequest = new(() =>
            Over(RenderingInputs.All(CosmosDifferentialTests.PartitionedPredicates, new JsonFactGenerations())));

        readonly RenderingGraph _graph;

        public RenderingCheckRun(RenderingMemo? memo = null, RenderingGraph? graph = null)
        {
            Memo = memo ?? new RenderingMemo();
            _graph = graph ?? Graph;
        }

        public RenderingMemo Memo { get; }

        /// <summary>Every conjunct a round admitted, one row each, with what its statement's check says.</summary>
        public ConcurrentQueue<Checked> Conjuncts { get; } = new();

        /// <summary>Every text captured at either site, once a text with its values inlined: what the reader is held to.</summary>
        public ConcurrentDictionary<string, (string Text, IReadOnlyList<CosmosParameter> Parameters, bool InFamily)> Captured { get; } = new();

        /// <summary>
        /// What the cross-checks found: a conjunct whose texts at the two sites differ with their values inlined, or a
        /// statement whose texts sent are not the conjunction its <c>WHERE</c> holds.
        /// </summary>
        public ConcurrentQueue<string> Mismatches { get; } = new();

        /// <summary>Text a <c>WHERE</c> holds that no site wrote: written outside the translator.</summary>
        public ConcurrentQueue<(string Input, string Name, string Text)> Outside { get; } = new();

        /// <summary>A statement that failed to plan or to implement.</summary>
        public ConcurrentQueue<(string Input, string Name, string Failure)> Failures { get; } = new();

        /// <summary>Checks every input; the corpus's share one harness and are planned one at a time, the rest two at a time.</summary>
        public static RenderingCheckRun Over(IEnumerable<RenderingInputs.Statement> inputs)
        {
            var run = new RenderingCheckRun();
            var all = inputs.ToList();
            foreach (var input in all.Where(i => i.Input == "corpus"))
                run.Plan(input);

            Parallel.ForEach(all.Where(i => i.Input != "corpus"), new ParallelOptions { MaxDegreeOfParallelism = 2 }, run.Plan);
            return run;
        }

        /// <summary>
        /// Checks a claim of a form for the conjunction of conjuncts, each with its own member of the form, under the asserted
        /// set and under <see cref="RenderingSet.Portable"/>; memoised on the import the callback makes into a bundle.
        /// </summary>
        public RenderingMemo.Entry Check(IReadOnlyList<SqlPredicate> conjuncts, ServiceForm form, IReadOnlyList<ServiceForm> members, Verdict claim,
            Func<RenderingBundle, ImportReport> import)
        {
            var ir = conjuncts.Count == 1 ? conjuncts[0] : new SqlPredicate.And(conjuncts);
            RenderingMemoKey key;
            using (var probe = _graph.Open(AssertedSet))
                key = new RenderingMemoKey(ir.Digest, import(probe).Digest, form.Text, claim, AssertedSet, RenderingMemo.DigestOf(probe.Theory));

            return Memo.GetOrAdd(key, id =>
            {
                var (asserted, run, alone, report) = Bundle(AssertedSet);
                var (portable, portableRun, _, _) = Bundle(RenderingSet.Portable);
                return new RenderingMemo.Entry(id, key, asserted, run, portable, portableRun, alone, report);
            });

            (RenderingVerdict, RenderingRun, IReadOnlyList<RenderingVerdict>, ImportReport) Bundle(RenderingSet set)
            {
                using var bundle = _graph.Open(set);
                var each = conjuncts.Select(bundle.Write).ToList();
                var root = bundle.Write(ir);
                var report = import(bundle);
                bundle.Claim(root, form);

                // A count limit, never time: descent and evaluation peel a junction a member at a time, so it grows with the form.
                var run = bundle.Run(64 + 8 * Leaves(form));
                return (bundle.Read(root, form, claim), run, [.. each.Select((c, i) => bundle.Read(c, members[i], claim))], report);
            }
        }

        void Plan(RenderingInputs.Statement input)
        {
            var recorder = new RenderingRecorder();
            var queries = new List<(string? Where, int From, int To)>();
            using (recorder.Arm())
            using (StatementTerms.ArmDeclaredPossible())
            {
                RelNode planned;
                try
                {
                    planned = input.Plan();
                }
                catch (Exception e)
                {
                    Failures.Enqueue((input.Input, input.Name, "plans: " + Failure(e)));
                    return;
                }

                foreach (var cosmos in Subtrees(planned))
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
                        Failures.Enqueue((input.Input, input.Name, "implements: " + Failure(e)));
                    }
                }
            }

            // Read after the statement, never during its planning: the importer opens nothing planning did not.
            var records = recorder.Records;
            var statements = records.Select(r => r.Statement).Distinct().ToList();
            for (var k = 0; k < statements.Count; k++)
                CheckStatement(input, k, statements[k], [.. records.Where(r => r.Statement == statements[k])]);

            foreach (var (where, from, to) in queries)
                Account(input, where, records.Take(to).Skip(from).Where(r => r.Site == 2).ToList());

            GC.KeepAlive(records);
        }

        void CheckStatement(RenderingInputs.Statement input, int k, CosmosFilterStatement statement, List<CaptureRecord> records)
        {
            var ir = statement.Statement.Conjuncts;
            var texts = new CaptureRecord?[ir.Count];
            foreach (var record in records.OrderByDescending(r => r.Site))
            {
                texts[record.Conjunct] ??= record;
                if (CosmosSqlFormReader.Inline(record.Text, record.Parameters) != CosmosSqlFormReader.Inline(texts[record.Conjunct]!.Text, texts[record.Conjunct]!.Parameters))
                    Mismatches.Enqueue($"{input.Input} {input.Name}: conjunct {record.Conjunct} admitted as {record.Text}, sent as {texts[record.Conjunct]!.Text}");
            }

            var reads = texts.Select(r => r is null ? null : CosmosSqlFormReader.Read(r.Text, r.Parameters)).ToArray();
            var skips = texts.Select((r, i) => r is null ? null : Skip(ir[i], r, reads[i]!)).ToArray();
            var checkable = Enumerable.Range(0, ir.Count).Where(i => texts[i] is not null && skips[i] is null).ToArray();
            foreach (var record in records)
                Captured.TryAdd(CosmosSqlFormReader.Inline(record.Text, record.Parameters), (record.Text, record.Parameters, Parts(ir[record.Conjunct]).All(p => p is not (SqlPredicate.Opaque or SqlValue.Parameter))));

            RenderingMemo.Entry? entry = null;
            string? reason = null;
            if (checkable.Length > 0)
            {
                var scope = statement.ScopeForCheck;
                var first = checkable.MinBy(scope.StratumOf);
                var (node, rules) = scope.ContextFor(OracleLicence.RenderConjunct(first));
                var source = new ImportSource(statement.Statement.Digest, $"render {first}", scope.StratumOf(first));
                var paths = checkable.SelectMany(i => ir[i].Paths).Distinct().ToArray();
                var form = Shape(statement, i => checkable.Contains(i) ? reads[i]!.Form : null, parts => ServiceForm.JunctionOf(CosmosServiceTest.And, parts))!;

                // Sent whole, the claim is the conjunction as built, read back.
                if (checkable.Length == ir.Count && texts.All(t => t!.Site == 2)
                    && CosmosSqlFormReader.Read(Built(statement, i => texts[i]!.Text), [.. texts.SelectMany(t => t!.Parameters)]).Form.Equals(form) == false)
                    Mismatches.Enqueue($"{input.Input} {input.Name}: {Built(statement, i => texts[i]!.Text)} reads otherwise than its conjuncts joined");

                entry = Check([.. checkable.Select(i => ir[i])], form, [.. checkable.Select(i => reads[i]!.Form)], Verdict.Equal,
                    bundle => RenderingImporter.Import(bundle, source, node, rules, statement.DeclarationForCheck, paths));
                GC.KeepAlive(scope);

                // A written junction of another arity than the IR's flattened one leaves an IR child no exact join.
                var arity = form.Kind == ServiceFormKind.And ? form.Members.Count : 1;
                reason = entry.Asserted.Reason ?? (entry.Asserted.Kind == RenderingVerdictKind.Unproven && arity != checkable.Sum(i => Arms(ir[i])) ? "no exact join" : null);
            }

            var conditional = statement.DeclarationForCheck.Rules.Any(r => r.Body.Count > 0);
            for (var i = 0; i < ir.Count; i++)
                if (texts[i] is { } text)
                    Conjuncts.Enqueue(new Checked(input.Input, input.Name, k, i, text.Site, ir[i].Digest, text.Text, skips[i], skips[i] is null ? entry : null,
                        Array.IndexOf(checkable, i), conditional, skips[i] is null ? reason : null));
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

        static string? Skip(SqlPredicate ir, CaptureRecord record, ReadForm read) =>
            Parts(ir).Any(p => p is SqlValue.Parameter) || record.Parameters.Any(p => p.Value is CosmosDynamicValue) || record.Comparisons.Count + record.Rechecks.Count > 0 ? "parameter"
            : Parts(ir).Any(p => p is SqlPredicate.Opaque) ? "outside the IR"
            : read.Written != record.Text || Opaque(read.Form) ? "unreadable"
            : null;

        static int Arms(SqlPredicate p) => p is SqlPredicate.And and ? and.Arms.Sum(Arms) : 1;

        static string Failure(Exception e) => e.GetType().Name + ": " + e.Message.Split('\n')[0].TrimEnd('\r');

        static bool Opaque(ServiceForm form) => form.Kind == ServiceFormKind.Opaque || form.Members.Any(Opaque);

        static int Leaves(ServiceForm form) => form.Members.Count == 0 ? 1 : form.Members.Sum(Leaves);

        /// <summary>Every predicate and value in the IR, depth first.</summary>
        static IEnumerable<object> Parts(SqlPredicate predicate) => predicate switch
        {
            SqlPredicate.And a => a.Arms.SelectMany(Parts),
            SqlPredicate.Or o => o.Arms.SelectMany(Parts),
            SqlPredicate.Not n => Parts(n.Operand),
            SqlPredicate.Compare c => Values(c.Left).Concat(Values(c.Right)),
            SqlPredicate.In i => Values(i.Value),
            SqlPredicate.IsNull n => Values(n.Value),
            SqlPredicate.IsDefined d => Values(d.Value),
            _ => [predicate],
        };

        static IEnumerable<object> Values(SqlValue value) => value switch
        {
            SqlValue.Cast c => Values(c.Value).Prepend(value),
            SqlValue.Call c => c.Operands.SelectMany(Values).Prepend(value),
            _ => [value],
        };

        /// <summary>The conjunction of the texts given, as <c>Translate</c> writes one.</summary>
        static string Built(CosmosFilterStatement statement, Func<int, string?> text) => Shape(statement, text, parts => "(" + string.Join(" AND ", parts) + ")")!;

        /// <summary>
        /// The conjunction of a statement's conjuncts as <c>Translate</c> writes it, each by its leaf and those with none
        /// left out: every <c>AND</c> of the condition a junction of what it holds, and one of a single member that member.
        /// </summary>
        static T? Shape<T>(CosmosFilterStatement statement, Func<int, T?> leaf, Func<IReadOnlyList<T>, T> and) where T : class
        {
            var next = 0;
            T? Walk(RexNode node)
            {
                if (node is not RexCall call || call.getKind() != SqlKind.AND)
                    return leaf(next++);

                var parts = Enumerable.Range(0, call.getOperands().size()).Select(i => Walk((RexNode)call.getOperands().get(i))).OfType<T>().ToList();
                return parts.Count switch { 0 => null, 1 => parts[0], _ => and(parts) };
            }

            return Walk(statement.Condition);
        }

        /// <summary>The Cosmos subtrees of a plan, each rooted where the walk from the top first meets the convention.</summary>
        static IEnumerable<RelNode> Subtrees(RelNode node) => node is CosmosRel
            ? [node]
            : Enumerable.Range(0, node.getInputs().size()).SelectMany(i => Subtrees((RelNode)node.getInputs().get(i)));

        /// <summary>
        /// One conjunct a round admitted: its input statement, its filter statement's number within it, its index, the site
        /// its text was read from (2 where it was sent), its IR and text, why it was skipped, the check of its statement and
        /// its own position in it, whether the declaration has conditional rules, and why the check is unproven.
        /// </summary>
        public sealed record Checked(string Input, string Name, int Statement, int Conjunct, int Site, string Ir, string Text, string? Skip,
            RenderingMemo.Entry? Entry, int Position, bool Conditional, string? Reason);

    }

}
