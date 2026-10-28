using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// S's side of one filter statement's admission under one set (spec §4.4, §5.2): asked once a round in one bundle,
    /// imported from the statement's root, every pending conjunct a root beside the round's junctions <c>Through(k)</c> and
    /// <c>Before(k)</c>, each later round's atoms stated beneath <see cref="CosmosStatementScope.StratumFor"/>; what it
    /// admitted, the composite, and each residual's weakening, read from a bundle a round opened or one closing bundle.
    /// The scope takes the admissions of the statement's own set; a set a test's probe asks beside it follows its rounds.
    /// </summary>
    /// <remarks>
    /// Legality only: which conjuncts S is asked, beneath which stratum, which bundle answers, and whether the printer
    /// writes what it answered; every answer is S's (<c>DESIGN.md</c>, <i>The production path</i>). Made and asked under
    /// its statement's lock.
    /// </remarks>
    internal sealed class CosmosFilterRendering
    {

        /// <summary>The sites that ask: admission's rounds (A), and a weakening's closing bundle the split rule opens (W).</summary>
        public const string Admission = "A", Weakening = "W";

        readonly CosmosFilterStatement _statement;
        readonly ICosmosRenderingProbe? _probe;
        readonly bool[] _owned;
        readonly string? _countAs;
        StatementTerms? _terms;
        string? _opaques;
        readonly Dictionary<int, Admitted> _admitted = [];
        readonly List<Round> _rounds = [];
        bool _again;
        (RenderingOracle.RenderingBundleAnswer? Bundle, Question? Closing)? _composite;
        Resolution? _resolved;

        /// <param name="statement">The statement admitted.</param>
        /// <param name="set">The set it is proven under here.</param>
        /// <param name="probe">A test's probe of the admission (<see cref="CosmosReasoning.Probe"/>); none in production.</param>
        /// <param name="owned">Whether S owns each conjunct (<see cref="RenderingFamily.Owns"/>).</param>
        /// <param name="countAs">The container a decline is counted under (<see cref="Client.CosmosInstrumentation.RenderingDeclines"/>): where S writes the statement under this set; none for a set a probe asks beside it.</param>
        internal CosmosFilterRendering(CosmosFilterStatement statement, RenderingSet set, ICosmosRenderingProbe? probe, bool[] owned, string? countAs) =>
            (_statement, Set, _probe, _owned, _countAs) = (statement, set, probe, owned, countAs);

        /// <summary>The set the statement is proven under here.</summary>
        public RenderingSet Set { get; }

        /// <summary>The rounds S was asked, in order: the rounds run again where its composite was not proven, if they ran.</summary>
        public IReadOnlyList<Round> Rounds => _rounds;

        /// <summary>What S admitted in the rounds, by conjunct, before the composite is read: void where it is not proven.</summary>
        public IReadOnlyDictionary<int, Admitted> Admissions => _admitted;

        IReadOnlyList<SqlPredicate> Conjuncts => _statement.Statement.Conjuncts;

        /// <summary>
        /// One round: S asked of the round's owned pending conjuncts, from the root, and those it proved admitted by it; on
        /// the closing call, all of them where their composite is proven and printed, and none where not; on a round run
        /// again, none.
        /// </summary>
        public IReadOnlyList<int> Answer(AdmissionRound round, CosmosStatementScope scope)
        {
            _terms ??= scope.Terms;

            // After the last round, the residuals are S's owned ones it did not prove and the others no round admitted.
            if (round.Closing)
            {
                _composite = Composite(scope, round.Node, [.. Enumerable.Range(0, Conjuncts.Count).Where(i => _owned[i] ? _admitted.ContainsKey(i) == false : round.Pending.Contains(i))]);
                return _composite.Value.Bundle is { } bundle && Standing(bundle) is not null ? [.. _admitted.Keys.Order()] : [];
            }

            // The rounds run again from the root: what S admitted before is void, and it admits nothing now.
            if (round is { Again: true, Number: 1 })
            {
                Count(Client.CosmosInstrumentation.RenderingReasons.Composite);
                _again = true;
                _admitted.Clear();
                _rounds.Clear();
            }

            var asked = round.Again ? [] : round.Pending.Where(i => _owned[i] && _admitted.ContainsKey(i) == false).ToArray();
            var planted = _probe?.RenderedFrom;
            var stratum = StratumOf(scope.StratumFor(round.Node, _admitted.ContainsKey, _probe?.ReadExactly));
            var strata = round.Pending.Where(i => _admitted.ContainsKey(i) == false).ToDictionary(i => i, i => planted?.Invoke(scope, i) is { } node
                ? new Stratum(round.Number + 1, node.Context, [.. Enumerable.Range(0, Conjuncts.Count).Except(round.Pending)]) : stratum);
            foreach (var (i, a) in _admitted)
                strata[i] = a.Stratum;

            var question = QuestionOf(Admission, scope, strata, [[.. _admitted.Keys.Concat(asked)], round.Number > 1 ? [.. _admitted.Keys] : null]);
            var answer = Ask(question);
            var admitted = asked.Where(i => answer.Of(Conjuncts[i]) is { Kind: RenderingVerdictKind.Proven, Best: not null }).ToList();
            foreach (var i in admitted)
                _admitted[i] = new(round.Number, answer.Of(Conjuncts[i]).Best!, strata[i]);

            _rounds.Add(new(round.Number, round.Pending, asked, question, answer));
            return admitted;
        }

        /// <summary>
        /// What S's admission came to, read once the rounds are done: whether it voided S's admissions, the forms that stand,
        /// the conjuncts left residual, and each one's weakening, read from the round after S's last admission that holds
        /// them all, or from one closing bundle (and from round 1's, where the composite was voided).
        /// </summary>
        public Resolution Resolve(CosmosStatementScope scope)
        {
            if (_resolved is { } resolved)
                return resolved;

            // The scope asked the composite after the last round and, where it failed, ran the rounds again, whose own rounds
            // answer the weakenings; under a set asked beside, the composite is read here, and voids its admissions alone.
            List<int> Residual(Func<int, bool> s) => [.. Enumerable.Range(0, Conjuncts.Count).Where(i => _owned[i] ? s(i) == false : scope.StratumOf(i) == 0)];
            var (bundle, closing) = _composite ?? (_again ? default : Composite(scope, scope.Stopped, Residual(_admitted.ContainsKey)));
            var standing = _admitted.Count > 0 && bundle is not null ? Standing(bundle) : null;
            var voided = _again == false && _admitted.Count > 0 && standing is null;
            var forms = standing ?? new Dictionary<int, ServiceForm>();
            var residual = Residual(forms.ContainsKey);

            // Round 1 asks every conjunct from the root, so where the composite voids S's admissions it answers the weakenings.
            var answering = voided ? _rounds[0] : _rounds.LastOrDefault(r => _admitted.Values.All(a => a.Round < r.Number) && residual.All(r.Pending.Contains));
            var (question, answer) = voided || closing is null || _again ? (answering?.Question, answering?.Answer) : (closing, bundle);
            if (residual.Count > 0 && answer is null)
                (question, answer) = Close(scope, scope.Stopped, residual, Weakening);

            var weakenings = residual.Where(i => answer is not null && answer.Roots.ContainsKey(Conjuncts[i].Digest) && answer.Of(Conjuncts[i]).Weakening is not null)
                .ToDictionary(i => i, i => answer!.Of(Conjuncts[i]).Weakening!);
            return _resolved = new(voided || _again, forms, residual, question, answer, weakenings);
        }

        /// <summary>
        /// The bundle that holds the composite (§5.2): the round's of <c>Through(k)</c>, the last round S admitted in, where it
        /// admitted all it was asked; else the round's after, of <c>Before</c>; else one closing bundle, which answers the
        /// weakenings too, and its question.
        /// </summary>
        (RenderingOracle.RenderingBundleAnswer?, Question?) Composite(CosmosStatementScope scope, StatementTerms.Node stopped, IReadOnlyList<int> residual)
        {
            if (_admitted.Count == 0)
                return (null, null);

            var last = _rounds.Single(r => r.Number == _admitted.Values.Max(a => a.Round));
            if (last.Asked.All(_admitted.ContainsKey))
                return (last.Answer, null);

            if (_rounds.FirstOrDefault(r => r.Number == last.Number + 1) is { } after)
                return (after.Answer, null);

            var (question, answer) = Close(scope, stopped, residual, Admission);
            return (answer, question);
        }

        /// <summary>
        /// The forms S's admissions are sent as, where they stand: their junction proven in the bundle that holds it, each
        /// conjunct's best there — the arms the <c>best</c> rule joined into the composite's, so that what is sent is the one
        /// form S proved for the whole <c>WHERE</c> (spec §2) — and each written by the printer; none otherwise.
        /// </summary>
        IReadOnlyDictionary<int, ServiceForm>? Standing(RenderingOracle.RenderingBundleAnswer bundle)
        {
            if (bundle.Of(Junction(_admitted.Keys)!) is not { Kind: RenderingVerdictKind.Proven })
                return null;

            var forms = _admitted.Keys.ToDictionary(i => i, i => bundle.Of(Conjuncts[i]).Best);
            return forms.All(f => f.Value is { } form && _statement.Prints(f.Key, form)) ? forms.ToDictionary(f => f.Key, f => f.Value!) : null;
        }

        /// <summary>One closing bundle: the residuals stated beneath <c>StratumFor</c> of where the rounds stopped, S's admissions beneath their own, and <c>Before</c>.</summary>
        (Question, RenderingOracle.RenderingBundleAnswer) Close(CosmosStatementScope scope, StatementTerms.Node stopped, IReadOnlyList<int> residual, string site)
        {
            var final = StratumOf(scope.StratumFor(stopped, _admitted.ContainsKey, _probe?.ReadExactly));
            var strata = residual.ToDictionary(i => i, _ => final);
            foreach (var (i, a) in _admitted)
                strata[i] = a.Stratum;

            var question = QuestionOf(site, scope, strata, [[.. _admitted.Keys]]);
            return (question, Ask(question));
        }

        Stratum? StratumOf((StatementTerms.Node Node, int Depth, IReadOnlyList<int> Assumes) stratum) =>
            stratum.Depth == 0 ? null : new(stratum.Depth + 1, stratum.Node.Scope, stratum.Assumes, stratum.Node.Key);

        SqlPredicate? Junction(IEnumerable<int> conjuncts) => conjuncts.Distinct().Order().ToList() is { Count: > 0 } all ? SqlPredicate.AllOf(all.Select(i => Conjuncts[i])) : null;

        /// <summary>
        /// A question: its conjuncts and the junctions of those given beside them as roots, imported from the root, each
        /// conjunct's atoms stated beneath its stratum but an atom two conjuncts share, since one may be what the other's
        /// stratum assumes.
        /// </summary>
        Question QuestionOf(string site, CosmosStatementScope scope, IReadOnlyDictionary<int, Stratum?> strata, IEnumerable<IReadOnlyCollection<int>?> junctions)
        {
            List<int> conjuncts = [.. strata.Keys.Order()];
            var joined = junctions.Select(j => j is null ? null : Junction(j)).ToList();
            var roots = conjuncts.Select(i => Conjuncts[i]).Concat(joined.OfType<SqlPredicate>()).DistinctBy(r => r.Digest).ToList();
            var shared = SharedAtoms(conjuncts.Select(i => Conjuncts[i]));
            var digest = _statement.Statement.Digest;
            var (root, rules) = scope.ContextFor(OracleLicence.Declaration);
            var stated = conjuncts.Where(i => strata[i] is not null).SelectMany(i => AtomsOf(Conjuncts[i]).Distinct().Where(a => shared.Contains(a) == false)
                .Select(a => new Stated(i, a, strata[i]!, new ImportSource(digest, "render round " + strata[i]!.Round, strata[i]!.Round)))).ToList();
            _opaques ??= string.Join(";", _statement.Statement.Opaques.OrderBy(o => o.Key).Select(o => o.Key + "=" + o.Value));
            var signature = string.Join(",", conjuncts.Select(i => Conjuncts[i].Digest + "@" + strata[i]?.Key)) + "\n" + string.Join(",", joined.Select(j => j?.Digest ?? "-")) + "\n" + _opaques;
            return new(site, Set, conjuncts, roots, strata, stated, new ImportSource(digest, "declaration", 1), root, rules, signature);
        }

        /// <summary>The atoms of a conjunct that read one path: what a cell of it is cut for, and what a stratum is stated beneath.</summary>
        internal static IEnumerable<SqlPredicate> AtomsOf(SqlPredicate conjunct) =>
            conjunct.Subterms.Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque) && t.Paths.Count() == 1);

        /// <summary>The atoms two conjuncts share, which neither's stratum is stated beneath, since one may be what the other's assumes: production's rule and check mode's.</summary>
        internal static HashSet<SqlPredicate> SharedAtoms(IEnumerable<SqlPredicate> conjuncts) =>
            [.. conjuncts.SelectMany(c => AtomsOf(c).Distinct()).GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key)];

        /// <summary>
        /// Asks S a question through the statement's schema's oracle, or hands it the answer a statement of the same terms was
        /// given there under the theory now current (<see cref="RenderingOracle.Answered"/>); a test's plant, which reads a
        /// node no key names, is asked afresh, and a decline is never kept.
        /// </summary>
        RenderingOracle.RenderingBundleAnswer Ask(Question q)
        {
            var (started, oracle, signature) = (Stopwatch.GetTimestamp(), _statement.Rendering, _probe?.Signature?.Invoke(q.Signature) ?? q.Signature);
            var answered = _probe is null or { RenderedFrom: null, ReadExactly: null } && _terms is not null ? oracle.Answered.GetValue(_terms, _ => new()) : null;
            if (answered is not null && oracle.Digest is { } current && answered.TryGetValue((Set, current, signature), out var known))
                return Counted(q, CosmosRendering.Reused(oracle, q.Roots, known, Stopwatch.GetElapsedTime(started)));

            var answer = CosmosRendering.Ask(oracle, Set, q.Roots, _statement.Statement.Opaques, q.Source, q.Root, q.Rules,
                [.. q.Beneath.Select(b => (b.Atom, b.Source, b.Stratum.Node, q.Rules))],
                _probe is { } probe ? (bundle, terms, import, run, elapsed) => probe.Opened(_statement, q, bundle, terms, import, run, elapsed) : null);

            // Filed under the theory the answer was computed under, which a generation swapped in meanwhile does not change.
            if (answered is not null && answer.Declined is null && answer.Key is { } key)
                answered.TryAdd((Set, key.Theory, signature), answer);

            return Counted(q, answer);
        }

        /// <summary>
        /// An answer, counted where it declines a question S writes the statement by: a bundle that declined, or a run that
        /// stopped before a conjunct asked was read; each leaves those conjuncts in process with no weakening (O8).
        /// </summary>
        RenderingOracle.RenderingBundleAnswer Counted(Question q, RenderingOracle.RenderingBundleAnswer answer)
        {
            if (_countAs is not null && (answer.Declined ?? (q.Conjuncts.Any(i => answer.Of(Conjuncts[i]).Kind == RenderingVerdictKind.Incomplete) ? Client.CosmosInstrumentation.RenderingReasons.Incomplete : null)) is { } reason)
                Count(reason);

            return answer;
        }

        /// <summary>Counts one decline of the statement's, under its container, where S writes it.</summary>
        void Count(string reason)
        {
            if (_countAs is not null)
                Client.CosmosInstrumentation.RenderingDeclines.Add(1, new KeyValuePair<string, object?>("cosmos.container", _countAs), new KeyValuePair<string, object?>("cosmos.rendering.reason", reason));
        }

        /// <summary>A conjunct S admitted: in which round, its form, and the stratum its atoms were stated beneath, none for the root.</summary>
        internal sealed record Admitted(int Round, ServiceForm Form, Stratum? Stratum);

        /// <summary>A later round's stratum: the round it renders, its node in the facts graph, the conjuncts it assumes, by index, and the node's name within its terms; none for a plant's.</summary>
        internal sealed record Stratum(int Round, Scope? Node, IReadOnlyList<int> Assumes, string? Key = null);

        /// <summary>An atom of a conjunct stated beneath its stratum, and the source it is stated from.</summary>
        internal sealed record Stated(int Conjunct, SqlPredicate Atom, Stratum Stratum, ImportSource Source);

        /// <summary>
        /// One question to S: the site, the set, the conjuncts (by index) and roots, each conjunct's stratum, the atoms
        /// stated beneath, and the root it is imported from; and what names it within its statement's terms.
        /// </summary>
        internal sealed record Question(string Site, RenderingSet Set, IReadOnlyList<int> Conjuncts, IReadOnlyList<SqlPredicate> Roots, IReadOnlyDictionary<int, Stratum?> Strata,
            IReadOnlyList<Stated> Beneath, ImportSource Source, Scope? Root, PredicateRules? Rules, string Signature);

        /// <summary>A round asked: its number, its pending conjuncts and those S was asked, the question and what S answered.</summary>
        internal sealed record Round(int Number, IReadOnlyList<int> Pending, IReadOnlyList<int> Asked, Question Question, RenderingOracle.RenderingBundleAnswer Answer);

        /// <summary>
        /// What S's admission came to: whether it voided S's admissions; the forms that stand, by conjunct, the composite's
        /// arms; the residual conjuncts; the question and answer the weakenings were read from; and each residual's
        /// weakening, none where S gave none.
        /// </summary>
        internal sealed record Resolution(bool Voided, IReadOnlyDictionary<int, ServiceForm> Forms, IReadOnlyList<int> Residual,
            Question? WeakeningQuestion, RenderingOracle.RenderingBundleAnswer? WeakeningAnswer, IReadOnlyDictionary<int, ServiceForm> Weakenings);

    }

}
