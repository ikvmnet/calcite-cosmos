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
    /// <c>Before(k)</c>, each later round's atoms stated beneath <see cref="CosmosStatementScope.StratumFor(StatementTerms.Node, Func{int, bool})"/>;
    /// what it admitted, the composite, and each residual's weakening, read from a bundle a round opened or one closing
    /// bundle. Writing, the scope takes its admissions; beside the translator, they are what S would admit in the
    /// translator's rounds.
    /// </summary>
    /// <remarks>
    /// Legality only: which conjuncts S is asked, beneath which stratum, and which bundle answers; every answer is S's
    /// (<c>DESIGN.md</c>, <i>The shadow in production code</i>). Made and asked under its statement's lock.
    /// </remarks>
    internal sealed class CosmosFilterRendering
    {

        /// <summary>The sites that ask: admission's rounds (A), and a weakening's closing bundle the split rule opens (W).</summary>
        public const string Admission = "A", Weakening = "W";

        readonly CosmosFilterStatement _statement;
        readonly CosmosRenderingShadow.Arming _arm;
        readonly bool[] _owned;
        StatementTerms? _terms;
        string? _opaques;
        readonly Dictionary<int, Admitted> _admitted = [];
        readonly List<Round> _rounds = [];
        List<Round>? _voided;
        (RenderingAnswer? Answer, Question? Closing, RenderingOracle.RenderingBundleAnswer? Answered)? _composite;
        Resolution? _resolved;

        internal CosmosFilterRendering(CosmosFilterStatement statement, RenderingSet set, CosmosRenderingShadow.Arming arm, bool[] owned) =>
            (_statement, Set, _arm, _owned) = (statement, set, arm, owned);

        /// <summary>The set the statement is proven under here.</summary>
        public RenderingSet Set { get; }

        /// <summary>The rounds S was asked, in order: the rounds run again where its composite was not proven, if they ran.</summary>
        public IReadOnlyList<Round> Rounds => _rounds;

        /// <summary>The rounds of the first pass where its composite was voided and the rounds ran again; none otherwise.</summary>
        public IReadOnlyList<Round>? Voided => _voided;

        /// <summary>What S admitted in the rounds, by conjunct, before the composite is read: void where it is not proven.</summary>
        public IReadOnlyDictionary<int, Admitted> Admissions => _admitted;

        IReadOnlyList<SqlPredicate> Conjuncts => _statement.Statement.Conjuncts;

        /// <summary>
        /// One round: S asked of the round's owned pending conjuncts, from the root, and those it proved admitted by it; on
        /// the closing call, all of them where their composite is proven, and none where not; on a round run again, none.
        /// </summary>
        public IReadOnlyList<int> Answer(AdmissionRound round, CosmosStatementScope scope)
        {
            _terms ??= scope.Terms;

            // After the last round, the residuals are S's owned ones it did not prove and the others no round admitted.
            if (round.Closing)
            {
                _composite = Composite(scope, round.Node, [.. Enumerable.Range(0, Conjuncts.Count).Where(i => _owned[i] ? _admitted.ContainsKey(i) == false : round.Pending.Contains(i))]);
                return _composite.Value.Answer is { Kind: RenderingVerdictKind.Proven } ? [.. _admitted.Keys.Order()] : [];
            }

            // The rounds run again from the root: what S admitted before is void, and it admits nothing now.
            if (round is { Again: true, Number: 1 })
            {
                _voided = [.. _rounds];
                _admitted.Clear();
                _rounds.Clear();
            }

            var asked = round.Again ? [] : round.Pending.Where(i => _owned[i] && _admitted.ContainsKey(i) == false).ToArray();
            var planted = _arm.Observer?.RenderedFrom;
            var stratum = StratumOf(scope.StratumFor(round.Node, _admitted.ContainsKey, _arm.Observer?.ReadExactly));
            var strata = round.Pending.Where(i => _admitted.ContainsKey(i) == false).ToDictionary(i => i, i => planted?.Invoke(scope, i) is { } node
                ? new Stratum(round.Number + 1, node.Context, [.. Enumerable.Range(0, Conjuncts.Count).Except(round.Pending)]) : stratum);
            foreach (var (i, a) in _admitted)
                strata[i] = a.Stratum;

            var (through, before) = (_admitted.Keys.Concat(asked).ToList(), round.Number > 1 ? _admitted.Keys.ToList() : null);
            var question = QuestionOf(Admission, scope, strata, [through, before]);
            var answer = Ask(question);
            var admitted = asked.Where(i => answer.Of(Conjuncts[i]) is { Kind: RenderingVerdictKind.Proven, Best: not null }).ToList();
            foreach (var i in admitted)
                _admitted[i] = new(round.Number, answer.Of(Conjuncts[i]).Best!, strata[i]);

            _rounds.Add(new(round.Number, round.Node, round.Pending, asked, Junction(through), before is null ? null : Junction(before), question, answer, round.Again));
            return admitted;
        }

        /// <summary>
        /// What S's admission came to, read once the rounds are done: the composite, whether it voided S's admissions, the
        /// forms that stand, the conjuncts left residual, and each one's weakening, read from the round after S's last
        /// admission that holds them all, or from one closing bundle (and from round 1's, where the composite was voided).
        /// </summary>
        public Resolution Resolve(CosmosStatementScope scope)
        {
            if (_resolved is { } resolved)
                return resolved;

            // Writing, the scope asked the composite after the last round and, where it failed, ran the rounds again, whose own
            // rounds answer the weakenings; beside the translator, the composite is read here, and voids S's admissions alone.
            var again = _voided is not null;
            List<int> Residual(Func<int, bool> s) => [.. Enumerable.Range(0, Conjuncts.Count).Where(i => _owned[i] ? s(i) == false : scope.StratumOf(i) == 0)];
            var (composite, closing, closed) = _composite ?? (again ? default : Composite(scope, scope.Stopped, Residual(_admitted.ContainsKey)));
            var voided = again == false && composite is { Kind: not RenderingVerdictKind.Proven };
            var forms = voided ? [] : _admitted.ToDictionary(a => a.Key, a => a.Value.Form);
            var residual = Residual(forms.ContainsKey);

            // Round 1 asks every conjunct from the root, so where the composite voids S's admissions it answers the weakenings.
            var answering = voided ? _rounds[0] : _rounds.LastOrDefault(r => _admitted.Values.All(a => a.Round < r.Number) && residual.All(r.Pending.Contains));
            var (question, answer) = voided || closed is null || again ? (answering?.Question, answering?.Answer) : (closing, closed);
            if (residual.Count > 0 && answer is null)
                (question, answer) = Close(scope, scope.Stopped, residual, Weakening);

            var weakenings = residual.Where(i => answer is not null && answer.Roots.ContainsKey(Conjuncts[i].Digest) && answer.Of(Conjuncts[i]).Weakening is not null)
                .ToDictionary(i => i, i => answer!.Of(Conjuncts[i]).Weakening!);
            return _resolved = new(composite, voided || again, forms, residual, question, answer, weakenings);
        }

        /// <summary>
        /// The composite (§5.2): <c>Through(k)</c> of the last round S admitted in, where it admitted all it was asked; else
        /// <c>Before</c> of the round after; else <c>Before</c> in one closing bundle, which answers the weakenings too.
        /// </summary>
        (RenderingAnswer?, Question?, RenderingOracle.RenderingBundleAnswer?) Composite(CosmosStatementScope scope, StatementTerms.Node stopped, IReadOnlyList<int> residual)
        {
            if (_admitted.Count == 0)
                return (null, null, null);

            var last = _rounds.Single(r => r.Number == _admitted.Values.Max(a => a.Round));
            if (last.Asked.All(_admitted.ContainsKey))
                return (last.Answer.Of(last.Through!), null, null);

            if (_rounds.FirstOrDefault(r => r.Number == last.Number + 1) is { } after)
                return (after.Answer.Of(after.Before!), null, null);

            var (question, answer) = Close(scope, stopped, residual, Admission);
            return (answer.Of(Junction(_admitted.Keys)!), question, answer);
        }

        /// <summary>One closing bundle: the residuals stated beneath <c>StratumFor</c> of where the rounds stopped, S's admissions beneath their own, and <c>Before</c>.</summary>
        (Question, RenderingOracle.RenderingBundleAnswer) Close(CosmosStatementScope scope, StatementTerms.Node stopped, IReadOnlyList<int> residual, string site)
        {
            var final = StratumOf(scope.StratumFor(stopped, _admitted.ContainsKey, _arm.Observer?.ReadExactly));
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
            var joined = junctions.ToList();
            var roots = conjuncts.Select(i => Conjuncts[i]).Concat(joined.Select(j => j is null ? null : Junction(j)).OfType<SqlPredicate>()).DistinctBy(r => r.Digest).ToList();
            var shared = conjuncts.SelectMany(i => AtomsOf(Conjuncts[i]).Distinct()).GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var digest = _statement.Statement.Digest;
            var (root, rules) = scope.ContextFor(OracleLicence.Declaration);
            var stated = conjuncts.Where(i => strata[i] is not null).SelectMany(i => AtomsOf(Conjuncts[i]).Distinct().Where(a => shared.Contains(a) == false)
                .Select(a => new Stated(i, a, strata[i]!, new ImportSource(digest, "render round " + strata[i]!.Round, strata[i]!.Round)))).ToList();
            _opaques ??= string.Join(";", _statement.Statement.Opaques.OrderBy(o => o.Key).Select(o => o.Key + "=" + o.Value));
            var signature = string.Join(",", conjuncts.Select(i => i + "@" + strata[i]?.Key)) + "|" + string.Join("|", joined.Select(j => j is null ? "-" : string.Join(".", j.Distinct().Order())))
                + "|" + _opaques;
            return new(site, Set, conjuncts, roots, strata, stated, new ImportSource(digest, "declaration", 1), root, rules, signature);
        }

        /// <summary>The atoms of a conjunct that read one path: what a cell of it is cut for, and what a stratum is stated beneath.</summary>
        internal static IEnumerable<SqlPredicate> AtomsOf(SqlPredicate conjunct) =>
            conjunct.Subterms.Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque) && t.Paths.Count() == 1);

        /// <summary>
        /// Asks S a question through the flow's oracle, a test's or the statement's schema's, or hands it the answer a
        /// statement of the same terms was given there (<see cref="RenderingOracle.Answered"/>); a test's plant, which reads a
        /// node no key names, is asked afresh, and a decline is never kept.
        /// </summary>
        RenderingOracle.RenderingBundleAnswer Ask(Question q)
        {
            var (started, oracle) = (Stopwatch.GetTimestamp(), _arm.Observer?.Oracle ?? _statement.Rendering);
            var answered = _arm.Observer is null or { RenderedFrom: null, ReadExactly: null } && _terms is not null ? oracle.Answered.GetValue(_terms, _ => new()) : null;
            if (answered is not null && answered.TryGetValue((Set, oracle.Digest, oracle.Memo.Epoch, q.Signature), out var known))
                return CosmosRendering.Reused(oracle, q.Site, Set, q.Roots, known, Stopwatch.GetElapsedTime(started));

            var answer = CosmosRendering.Ask(oracle, q.Site, Set, q.Roots, _statement.Statement.Opaques, q.Source, q.Root, q.Rules,
                [.. q.Beneath.Select(b => (b.Atom, b.Source, b.Stratum.Node, q.Rules))],
                _arm.Observer is { } observer ? (bundle, terms, import, run, elapsed) => observer.Opened(_statement, q, bundle, terms, import, run, elapsed) : null);
            if (answered is not null && answer.Declined is null && oracle.Digest is not null)
                answered.TryAdd((Set, oracle.Digest, oracle.Memo.Epoch, q.Signature), answer);

            return answer;
        }

        /// <summary>A conjunct S admitted: in which round, its form, and the stratum its atoms were stated beneath, none for the root.</summary>
        internal sealed record Admitted(int Round, ServiceForm Form, Stratum? Stratum);

        /// <summary>A later round's stratum: the round it renders, its node in the facts graph, the conjuncts it assumes, by index, and the node's name within its terms; none for a plant's.</summary>
        internal sealed record Stratum(int Round, Scope? Node, IReadOnlyList<int> Assumes, string? Key = null);

        /// <summary>An atom of a conjunct stated beneath its stratum, and the source it is stated from.</summary>
        internal sealed record Stated(int Conjunct, SqlPredicate Atom, Stratum Stratum, ImportSource Source);

        /// <summary>
        /// One question to S: the site, the set, the conjuncts (by index) and roots, each conjunct's stratum, the atoms
        /// stated beneath, and the root it is imported from: what the shadow claims the translator's texts at, at one key;
        /// and what names it within its statement's terms.
        /// </summary>
        internal sealed record Question(string Site, RenderingSet Set, IReadOnlyList<int> Conjuncts, IReadOnlyList<SqlPredicate> Roots, IReadOnlyDictionary<int, Stratum?> Strata,
            IReadOnlyList<Stated> Beneath, ImportSource Source, Scope? Root, PredicateRules? Rules, string Signature);

        /// <summary>A round asked: its number and node, its pending conjuncts and those S was asked, its junctions, the question and what S answered.</summary>
        internal sealed record Round(int Number, StatementTerms.Node Node, IReadOnlyList<int> Pending, IReadOnlyList<int> Asked, SqlPredicate? Through, SqlPredicate? Before,
            Question Question, RenderingOracle.RenderingBundleAnswer Answer, bool Again);

        /// <summary>
        /// What S's admission came to: the composite, none where S admitted nothing; whether it voided S's admissions; the
        /// forms that stand, by conjunct; the residual conjuncts; the question and answer the weakenings were read from; and
        /// each residual's weakening, none where S gave none.
        /// </summary>
        internal sealed record Resolution(RenderingAnswer? Composite, bool Voided, IReadOnlyDictionary<int, ServiceForm> Forms, IReadOnlyList<int> Residual,
            Question? WeakeningQuestion, RenderingOracle.RenderingBundleAnswer? WeakeningAnswer, IReadOnlyDictionary<int, ServiceForm> Weakenings);

    }

}
