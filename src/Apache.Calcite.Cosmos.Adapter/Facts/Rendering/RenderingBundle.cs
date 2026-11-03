using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One statement round in S: a child of the frozen theory holding the round's conjuncts as terms, the import they were
    /// rendered from, and the forms claimed for them, or the roots generate mode is asked of; run once, read off a
    /// completed run alone, and disposed.
    /// </summary>
    /// <remarks>
    /// An engine failure in any call declines the bundle rather than throwing: every claim then reads Unproven, and where
    /// the graph ran out of ids or epochs its generation retires (<see cref="RenderingGraph"/>). Every read passes one
    /// discipline (<see cref="Readable"/>).
    /// </remarks>
    internal sealed class RenderingBundle : IDisposable
    {

        readonly RenderingTheory _theory;
        readonly Dictionary<JsonDocumentPath, PathValues> _imported = [];
        readonly Dictionary<SqlPredicate, PathValues?> _atoms = [];
        readonly Dictionary<string, long> _strataByKey = new(StringComparer.Ordinal);
        readonly Dictionary<long, IReadOnlyList<SqlPredicate>> _strata = [];
        readonly Dictionary<SqlValue.Call, ConstructorTerm<Val>> _computed = [];
        readonly Dictionary<(long Stratum, JsonDocumentPath Path), PathValues> _arms = [];
        readonly Dictionary<string, ValueSet<string>> _chars = new(StringComparer.Ordinal);
        ImportSource? _source;
        RenderingRun? _run;

        internal RenderingBundle(RenderingTheory theory, RenderingSet set)
        {
            _theory = theory;
            Scope = theory.Theory.CreateChild();
            Scope.Assume(theory.Under.Of(set));
        }

        /// <summary>The bundle's scope, for its tests.</summary>
        public Scope Scope { get; }

        /// <summary>The theory, of one generation, the bundle is a child of.</summary>
        public RenderingTheory Theory => _theory;

        /// <summary>The engine failure the bundle declined on, if any.</summary>
        public Exception? Failure { get; private set; }

        /// <summary>The iterations its run took, against its limit; none before a run, or where the engine failed it.</summary>
        public int Iterations { get; private set; }

        /// <summary>
        /// The rows its run left in its own scope, what <c>RowLimit</c> bounds, counted when read; none before a run (h11,
        /// kill line 39).
        /// </summary>
        public long Rows => _run?.StopReason is null ? 0 : _theory.Graph.Tables.Sum(table => Scope.CountRows(table) - _theory.Theory.CountRows(table));

        /// <summary>
        /// Every arm stratum a written case opened, by id: the conditions it is read beneath, outermost first, each a
        /// condition or its negation. Zero, the enclosing node, is none of them.
        /// </summary>
        public IReadOnlyDictionary<long, IReadOnlyList<SqlPredicate>> Strata => _strata;

        /// <summary>Every atom written, an arm's tagged with its stratum (<see cref="SqlPredicate.Beneath"/>): what <see cref="Assume"/> names.</summary>
        public IReadOnlyCollection<SqlPredicate> Atoms => _atoms.Keys;

        /// <summary>The narrow else of a case is rooted beneath its condition's negation to this many conditions deep; wide alone below.</summary>
        internal const int NarrowDepth = 3;

        /// <summary>
        /// Writes a conjunct as the shared vocabulary's terms, with the spelling of every path it reads, and its syntax
        /// beside them: each leaf over the value it is, and each call's operands.
        /// </summary>
        public Pred Write(SqlPredicate predicate) => Write(predicate, null);

        /// <summary>
        /// Writes a conjunct as <see cref="Write(SqlPredicate)"/> does, and each opaque predicate whose reading is given
        /// over the value it was read as (<see cref="PredicateStatement.Opaques"/>), with the calls in it.
        /// </summary>
        public Pred Write(SqlPredicate predicate, IReadOnlyDictionary<long, SqlValue>? opaques)
        {
            Collect(predicate);

            var theory = _theory;
            var facts = new List<Fact>();
            PredicateWriter? writer = null;
            writer = new PredicateWriter(theory.Ir, path =>
            {
                var term = JsonFactEncoding.Path(path).ToTerm(theory.Paths);
                facts.Add(theory.Spelling.Of(term).Is(SqlValue.Spell(path)));
                return term;
            }, leaf: (term, values) => term is ConstructorTerm<Pred> leaf
                ? values.Select(v => theory.Over.Of(leaf, v)).Prepend(theory.Leaf.Of(leaf))
                : values.Select(v => theory.CallArg.Of((ConstructorTerm<Val>)term, v)), opaques: opaques,
                beneath: (stratum, leaf) => theory.Beneath.Of(stratum, leaf),
                @case: (stratum, chosen, bookkeeping) => Chosen(writer!, stratum, chosen, bookkeeping), computed: Computed);

            var term = writer.Term(predicate, facts);
            return Guard(() =>
            {
                var root = default(Pred);
                Scope.Write(w =>
                {
                    root = w.Make(term);
                    w.Assume([.. facts]);
                });

                return root;
            });
        }

        /// <summary>
        /// A case beneath a stratum as S's <c>chosen</c>: the condition read where the case is, the then-arm beneath the
        /// condition too, and the else-arm twice, narrow beneath its negation (to <see cref="NarrowDepth"/> conditions) and
        /// wide where the case is. Each arm's atoms are collected as they are tagged.
        /// </summary>
        /// <summary>
        /// What the host writes of a call the loader read as a computed place (h10): its type, arity, operands by ordinal,
        /// its place's spelling, and that it reads that place through its type's reader. The loader types only a call the
        /// function rows name; whether its place has an import is the image rule's. A call with no spelling has no place.
        /// </summary>
        IEnumerable<Fact> Computed(SqlValue.Call call, ConstructorTerm<Val> term, IReadOnlyList<ConstructorTerm<Val>> operands)
        {
            if (call.Spelled is not { } spelled)
                return [];

            _computed.TryAdd(call, term);
            return
            [
                _theory.SqlType.Of(term).Is(call.Type!), _theory.Arity.Of(term).Is((long)operands.Count),
                _theory.Spelling.Of(_theory.Valued.Of(term)).Is(spelled), _theory.Ir.Reads.Of(term, _theory.Valued.Of(term), PredicateAtom.TypedReader(call.Type!)),
                .. operands.Select((operand, i) => _theory.CallArgAt.Of(term, (long)i, operand)),
            ];
        }

        /// <summary>The calls written as computed places.</summary>
        public IReadOnlyCollection<SqlValue.Call> ComputedCalls => _computed.Keys;

        /// <summary>
        /// Imports a computed place's characters: F's for the call at the root (<c>ValueChars</c>), a superset beneath any
        /// stratum. A call the bundle wrote no place for is ignored.
        /// </summary>
        public void ImportComputed(SqlValue.Call call, ValueSet<string> classes)
        {
            if (_run is not null)
                throw new InvalidOperationException("An import after the run would be read by nothing the run derived.");

            if (_computed.TryGetValue(call, out var term) == false)
                return;

            _chars.TryAdd(call.Spelled!, classes);
            var place = _theory.Valued.Of(term);
            try
            {
                if (Failure is null)
                    Scope.Write(w => w.Assume(_theory.ImportedChars.Of(place).Is(classes), _theory.PartedLiterals.Of(place).Is(ValueSet<string>.Empty)));
            }
            catch (Exception e) when (e is not FactConflictException && JsonFactTheory.IsEngineFailure(e))
            {
                Decline(e);
            }
        }

        /// <summary>
        /// Whether an atom compares a computed place, or two readings (h11): its <c>Assumed</c> is the assume rule's, not
        /// the bundle's.
        /// </summary>
        internal static bool IsComputed(SqlPredicate atom) => atom switch
        {
            SqlPredicate.Beneath b => IsComputed(b.Leaf),
            SqlPredicate.Compare c => IsPlace(c.Left) || IsPlace(c.Right) || IsPair(c),
            SqlPredicate.In i => IsPlace(i.Value),
            SqlPredicate.IsNull n => IsPlace(n.Value),
            SqlPredicate.IsDefined d => IsPlace(d.Value),
            SqlPredicate.TypeTest t => IsPlace(t.Value),
            SqlPredicate.Affix a => IsPlace(a.Value),
            _ => false,
        };

        static bool IsPlace(SqlValue value) => value is SqlValue.Call { Type: not null };

        /// <summary>Whether a comparison reads a place on each side, a pair's atom: a path read or a computed place.</summary>
        static bool IsPair(SqlPredicate.Compare compare) => IsReading(compare.Left) && IsReading(compare.Right);

        static bool IsReading(SqlValue value) => value.PathRead is not null || IsPlace(value);

        /// <summary>A reading's place and spelling: a path's, or a computed place's the bundle wrote; none otherwise.</summary>
        (Term<JPath> Term, string Spelling)? PlaceOf(SqlValue value) => value.PathRead is { } path
            ? (JsonFactEncoding.Path(path).ToTerm(_theory.Paths), SqlValue.Spell(path))
            : value is SqlValue.Call { Spelled: { } spelled } call && _computed.TryGetValue(call, out var term) ? (_theory.Valued.Of(term), spelled) : null;

        ConstructorTerm<Pred> Chosen(PredicateWriter writer, long stratum, SqlPredicate.Case chosen, List<Fact> bookkeeping)
        {
            var at = stratum == 0 ? Array.Empty<SqlPredicate>() : _strata[stratum];
            var (condition, then, wide) = (Tagged(chosen.Condition, at), Tagged(chosen.Then, [.. at, chosen.Condition]), Tagged(chosen.Else, at));
            var narrow = at.Count < NarrowDepth ? Tagged(chosen.Else, [.. at, new SqlPredicate.Not(chosen.Condition)]) : wide;
            foreach (var part in new[] { condition, then, narrow, wide })
                Collect(part);

            return _theory.Chosen.Of(writer.Term(condition, bookkeeping), writer.Term(then, bookkeeping), writer.Term(narrow, bookkeeping), writer.Term(wide, bookkeeping));
        }

        /// <summary>
        /// A predicate read beneath conditions: every leaf, and every case to be written there, beneath their stratum, its
        /// junctions and negations kept and a constant untagged; itself beneath none.
        /// </summary>
        SqlPredicate Tagged(SqlPredicate predicate, IReadOnlyList<SqlPredicate> conditions)
        {
            if (conditions.Count == 0)
                return predicate;

            var key = string.Join("; ", conditions.Select(c => c.ToString()));
            if (_strataByKey.TryGetValue(key, out var stratum) == false)
            {
                _strataByKey.Add(key, stratum = _strataByKey.Count + 1);
                _strata.Add(stratum, conditions);
            }

            SqlPredicate Tag(SqlPredicate p) => p switch
            {
                SqlPredicate.And a => new SqlPredicate.And([.. a.Arms.Select(Tag)]),
                SqlPredicate.Or o => new SqlPredicate.Or([.. o.Arms.Select(Tag)]),
                SqlPredicate.Not n => new SqlPredicate.Not(Tag(n.Operand)),
                SqlPredicate.Constant => p,
                SqlPredicate.Beneath => throw new InvalidOperationException($"{p} is tagged already."),
                _ => new SqlPredicate.Beneath(stratum, p),
            };

            return Tag(predicate);
        }

        /// <summary>
        /// Notes every atom a predicate writes: each leaf as it is spelled, tagged or not; a case's are its arms', noted as
        /// it is written.
        /// </summary>
        void Collect(SqlPredicate predicate)
        {
            switch (predicate)
            {
                case SqlPredicate.And a:
                    foreach (var arm in a.Arms)
                        Collect(arm);
                    break;
                case SqlPredicate.Or o:
                    foreach (var arm in o.Arms)
                        Collect(arm);
                    break;
                case SqlPredicate.Not n:
                    Collect(n.Operand);
                    break;
                case SqlPredicate.Case or SqlPredicate.Beneath { Leaf: SqlPredicate.Case }:
                    break;
                default:
                    _atoms.TryAdd(predicate, null);
                    break;
            }
        }

        /// <summary>
        /// Imports what one path may hold, whole, from the one source the round was rendered from. Before the run only, so
        /// that every rule reads the final value; a second import of the path that differs throws, since S never meets two.
        /// </summary>
        /// <exception cref="InvalidOperationException">The bundle has run, or holds an import from another source.</exception>
        /// <exception cref="FactConflictException">The path was imported with another value.</exception>
        public void Import(ImportSource source, JsonDocumentPath path, PathValues values, ValueSet<string> classes)
        {
            if (_run is not null)
                throw new InvalidOperationException("An import after the run would be read by nothing the run derived.");

            if (_source is not null && _source != source)
                throw new InvalidOperationException($"The bundle imports from {_source}, and one bundle holds one source's facts.");

            _source = source;
            _imported.TryAdd(path, values);
            _chars.TryAdd(SqlValue.Spell(path), classes);
            var term = JsonFactEncoding.Path(path).ToTerm(_theory.Paths);

            // A differing second import is the importer's fault, not the engine's, and throws.
            try
            {
                if (Failure is null)
                    Scope.Assume(_theory.Imported.Of(term).Is(values), _theory.ImportedChars.Of(term).Is(classes), _theory.PartedLiterals.Of(term).Is(ValueSet<string>.Empty));
            }
            catch (Exception e) when (e is not FactConflictException && JsonFactTheory.IsEngineFailure(e))
            {
                Decline(e);
            }
        }

        /// <summary>
        /// States what an atom's path may hold where its conjunct was rendered: a later round of one of the statements the
        /// import is the root of, which narrows the import and is read of nothing but that conjunct's atoms; or an arm
        /// stratum the bundle opened, of the root's own statement and round, read of nothing but the atoms tagged with it.
        /// Before the run only; an atom no call names is read as anything there.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The bundle has run; the stratum is no later round of the import's statements, nor an arm stratum the bundle
        /// opened of its own round; the atom reads no imported path, is not tagged with the arm, or was stated otherwise
        /// already.
        /// </exception>
        /// <exception cref="InvalidOperationException">The value admits what the import does not.</exception>
        public void Assume(ImportSource source, SqlPredicate atom, PathValues values)
        {
            if (_run is not null)
                throw new InvalidOperationException("A stratum stated after the run would be read by nothing the run derived.");

            var statement = _source is not null && _source.Statement.Split('+').Contains(source.Statement);
            if (source.Stratum is { } arm)
            {
                if (statement == false || source.Round != _source!.Round || _strata.ContainsKey(arm) == false || atom is not SqlPredicate.Beneath { Stratum: var tagged } || tagged != arm)
                    throw new InvalidOperationException($"{source} is no arm stratum the bundle opened of the round {_source} is the root of, or {atom} is not read beneath it.");
            }
            else if (statement == false || source.Round <= _source!.Round)
                throw new InvalidOperationException($"{source} is no later round of the statements {_source} is the root of.");

            if (atom.Places.ToList() is not [var path] || _imported.TryGetValue(path, out var imported) == false)
                throw new InvalidOperationException($"{atom} reads no one path the bundle imported.");

            if (PathValues.Meet(values, imported) != values)
                throw new InvalidOperationException($"{atom}'s stratum says {values}, which the import {imported} does not admit.");

            // The path's import beneath the arm, which a computed place's operand there reads (h10); an atom over a
            // computed place is assumed by the assume rule, not as its operand's values.
            if (source.Stratum is { } beneath)
                _arms.TryAdd((beneath, path), values);

            if (IsComputed(atom))
                return;

            if (_atoms.TryGetValue(atom, out var stated) && stated is not null && stated != values)
                throw new InvalidOperationException($"{atom} is stated beneath two strata.");

            _atoms[atom] = values;
        }

        /// <summary>
        /// Claims a written form for a conjunct at the root, and states the members of every junction in it as written, and
        /// of every conjunction what it is a join of, as the service may read a range across them.
        /// </summary>
        public void Claim(Pred root, ServiceForm form)
        {
            var facts = new List<Fact> { _theory.Candidate.Of(root, RenderPosition.Pos, form) };
            Members(form, facts);
            Guard(() => Scope.Assume([.. facts]));
        }

        /// <summary>
        /// Asks generate mode of a root: it stands at <c>Pos</c>, with no exact form found and nothing implied yet, as
        /// <see cref="Claim"/> writes a candidate.
        /// </summary>
        public void Generate(Pred root) => Guard(() => Scope.Assume(_theory.At.Of(root, RenderPosition.Pos), _theory.BestExact.Of(root, RenderPosition.Pos).Is(Priced.None),
            _theory.Implied.Of(root, RenderPosition.Pos).Is(ValueSet<ServiceForm>.Empty)));

        /// <summary>
        /// Runs the check within count limits, never time, and the tripwire after it. A run the engine failed has no stop
        /// reason.
        /// </summary>
        public RenderingRun Run(int iterationLimit = 64 + PlaceIterations) => Run(_theory.Check, iterationLimit);

        /// <summary>
        /// Runs generate mode as <see cref="Run(int)"/> runs the check, within 80 iterations and 8 more for each unit of
        /// the width its roots ask (<see cref="WidthOf"/>): two strata more than the check's, and a junction peeled an arm
        /// an iteration.
        /// </summary>
        public RenderingRun RunGenerate(int width) => RunGenerateWithin(GenerateLimit(width));

        /// <summary>Runs generate mode within an iteration limit of the caller's: a test's stops a run short.</summary>
        internal RenderingRun RunGenerateWithin(int iterationLimit) => Run(_theory.Generate, iterationLimit);

        /// <summary>The iterations a generate run of a width is allowed.</summary>
        internal static int GenerateLimit(int width) => 80 + 8 * width + PlaceIterations;

        /// <summary>The most rows a run may leave in its scope before it stops Incomplete.</summary>
        internal const int RowLimit = 50_000;

        /// <summary>What the nested gathering of computed places may take: 16 an outer pass, four levels deep (h10, R7).</summary>
        internal const int PlaceIterations = 16 * 4;

        /// <summary>A generate run's width: the roots' atoms, every <c>IN</c>'s points, and the arms of each root that is a junction.</summary>
        public static int WidthOf(IEnumerable<SqlPredicate> roots)
        {
            var all = roots.ToList();
            var atoms = all.SelectMany(r => r.Subterms).Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not)).Distinct().ToList();
            return atoms.Count + atoms.OfType<SqlPredicate.In>().Sum(i => i.Points.Count) + all.Sum(r => r switch { SqlPredicate.And a => a.Arms.Count, SqlPredicate.Or o => o.Arms.Count, _ => 0 });
        }

        /// <summary>
        /// What the host writes for the computed places at the run (h10): each atom's stratum, each imported path's import
        /// beneath every stratum (the arm's where F read one, else the root's, a superset), and each literal operand as its
        /// own single cell; and of each pair (h11), its spelling and its places' characters, once both are known.
        /// </summary>
        IEnumerable<Fact> Places()
        {
            foreach (var compare in _atoms.Keys.Select(a => a is SqlPredicate.Beneath b ? b.Leaf : a).OfType<SqlPredicate.Compare>().Where(IsPair).Distinct())
                if (PlaceOf(compare.Left) is (var left, var ls) && PlaceOf(compare.Right) is (var right, var rs) && _chars.TryGetValue(ls, out var lc) && _chars.TryGetValue(rs, out var rc))
                {
                    var pair = _theory.Paired.Of(left, right);
                    yield return _theory.Spelling.Of(pair).Is(PlaceSpellings.Of(ls, rs));
                    yield return _theory.ImportedChars.Of(pair).Is(lc.Union(rc));
                    yield return _theory.PartedLiterals.Of(pair).Is(ValueSet<string>.Empty);
                }

            var strata = _strata.Keys.Prepend(0L).ToList();
            foreach (var atom in _atoms.Keys.Where(IsComputed))
                yield return _theory.StratumId.Of(Write(atom)).Is(atom is SqlPredicate.Beneath b ? b.Stratum : 0L);

            foreach (var (path, values) in _imported)
            {
                var term = JsonFactEncoding.Path(path).ToTerm(_theory.Paths);
                foreach (var stratum in strata)
                    yield return _theory.ArmImport.Of(stratum, term).Is(stratum == 0 ? values : _arms.GetValueOrDefault((stratum, path), values));
            }

            foreach (var (call, term) in _computed)
                for (var i = 0; i < call.Operands.Count; i++)
                    if (call.Operands[i] is SqlValue.Literal literal)
                    {
                        var cell = PathImport.Literal(i, literal.Value);
                        foreach (var stratum in strata)
                            yield return _theory.Gathered.Of(stratum, term).Is(ValueSet.Of(cell));
                    }
        }

        RenderingRun Run(Schedule schedule, int iterationLimit)
        {
            var assumed = _atoms.ToList().Where(a => IsComputed(a.Key) == false).Select(a => _theory.Assumed.Of(Write(a.Key)).Is(a.Value ?? PathValues.Anything)).ToList();
            if (_computed.Count > 0 || _atoms.Keys.Any(IsComputed))
                assumed.AddRange(Places());
            return _run = Guard(() =>
            {
                Scope.Assume([.. assumed]);
                return Ran(schedule, iterationLimit);
            }) ?? new RenderingRun(null, true, false);
        }

        RenderingRun Ran(Schedule schedule, int iterationLimit)
        {
            var report = Scope.Run(schedule, new RunOptions { IterationLimit = iterationLimit, RowLimit = RowLimit });
            Iterations = report.Iterations;

            // No rule merges two classes, so every term is its class's one node: a merge would make two terms one.
            var terms = _theory.Graph.Tables.Where(table => table.Kind == TableKind.Constructor).SelectMany(Scope.Rows).ToList();
            return new RenderingRun(report.StopReason, terms.Select(row => row.Output).Distinct().Count() == terms.Count,
                Scope.Check(_theory.Graph.Contradiction.Of()));
        }

        /// <summary>What the run says of a claim of a form for a conjunct at the root; Unproven where the bundle declined.</summary>
        public RenderingVerdict Read(Pred root, ServiceForm form, Verdict claim) =>
            Guard(() => Readable(out var kind, out var reason) ? Answer(root, form, claim) : new RenderingVerdict(kind, null, null, reason))
            ?? new(RenderingVerdictKind.Unproven, null, null, Declined);

        /// <summary>
        /// What generate mode says of a root at <c>Pos</c>: Proven with its cheapest exact form and that form's proof, or
        /// Unproven with the least refutation of a form proposed for it, where one was refuted; and its weakening beside
        /// either. Incomplete or a Conflict where the run may not be read, and Unproven where the bundle declined.
        /// </summary>
        public RenderingAnswer ReadBest(Pred root) => Guard(() => Best(root)) ?? new(RenderingVerdictKind.Unproven, null, null, null, Declined);

        /// <summary>
        /// Every agreement the run derived of a node at a position that rests on something, each form with the verdict it
        /// holds for: what source A and the pairwise check judge. None where the run may not be read.
        /// </summary>
        internal IReadOnlyList<(ServiceForm Form, Verdict Claim)> Agreements(Pred node, RenderPosition pos) => Guard(() => Readable(out _, out _)
            ? Scope.Rows(_theory.Agrees).Where(row => node.Id.Equals(row.Keys[0]) && pos.Equals(row.Keys[1])).Select(row => ((ServiceForm)row.Keys[2], (Verdict)row.Keys[3]))
                .Where(a => Scope.TryGet(_theory.Vacuous.Of(node, pos, a.Item1, a.Item2), out var vacuous) == false || vacuous == false).ToList()
            : []) ?? [];

        /// <summary>
        /// Declines the bundle on an engine failure, so nothing more is asked of its scope. Running out of ids or epochs is
        /// the graph's, not the bundle's, and retires its generation.
        /// </summary>
        internal void Decline(Exception failure)
        {
            Failure ??= failure;
            if (failure is OverflowException)
                _theory.RanOut = true;
        }

        /// <inheritdoc />
        public void Dispose() => Scope.Dispose();

        T? Guard<T>(Func<T> call)
        {
            if (Failure is not null)
                return default;

            try
            {
                return call();
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                Decline(e);
                return default;
            }
        }

        void Guard(Action call) => Guard(() =>
        {
            call();
            return true;
        });

        string Declined => $"declined: {Failure!.GetType().Name}: {Failure.Message}";

        /// <summary>
        /// The one discipline every read passes, inside <see cref="Guard{T}"/>, which answers for a bundle that declined: its
        /// run completed within its limits, still held one node per class and derived no contradiction. Where it did not,
        /// what a read says instead.
        /// </summary>
        bool Readable(out RenderingVerdictKind kind, out string? reason)
        {
            // A merge would let an agreement pass between two terms; no rule merges, so one is a rule that is wrong.
            (kind, reason) = _run is not { StopReason: RunStopReason.Completed } ? (RenderingVerdictKind.Incomplete, _run is null ? "not run" : "stopped: " + _run.StopReason)
                : _run.OneNodePerClass == false ? (RenderingVerdictKind.Conflict, "tripwire: a rule merged two terms")
                : _run.Contradicted ? (RenderingVerdictKind.Conflict, "agreed and refuted: " + string.Join("; ", Scope.Rows(_theory.Conflict)))
                : (RenderingVerdictKind.Proven, null);
            return reason is null;
        }

        RenderingAnswer Best(Pred root)
        {
            if (Readable(out var kind, out var reason) == false)
                return new(kind, null, null, null, reason);

            var weakening = Weakened(root);
            if (Scope.TryGet(_theory.BestExact.Of(root, RenderPosition.Pos), out var best) && best.Form is { } form)
                return new(RenderingVerdictKind.Proven, form, weakening, Scope.TryGet(_theory.Agrees.Of(root, RenderPosition.Pos, form, Verdict.Equal), out var proof) ? proof : null, null);

            // The least cell, and of the forms refuted there the cheapest; a sentinel is no form proposed, and none refutes one.
            var refuted = Scope.Rows(_theory.Disagrees).Where(row => root.Id.Equals(row.Keys[0]) && RenderPosition.Pos.Equals(row.Keys[1]) && Verdict.Equal.Equals(row.Keys[3])
                    && ((ServiceForm)row.Keys[2]).Kind != ServiceFormKind.Opaque)
                .Select(row => (Form: (ServiceForm)row.Keys[2], Count: ((Refutation)row.Output!).Cells.Count, Cells: string.Join(", ", ((Refutation)row.Output!).Cells.AsEnumerable())))
                .OrderBy(r => r.Count).ThenBy(r => r.Cells, StringComparer.Ordinal).ThenBy(r => Priced.Of(r.Form)).FirstOrDefault();
            return new(RenderingVerdictKind.Unproven, null, weakening, null, refuted.Form is null ? null : $"refuted: {refuted.Form} at {refuted.Cells}");
        }

        ServiceForm? Weakened(Pred root) =>
            Scope.TryGet(_theory.Weakening.Of(root, RenderPosition.Pos), out var weakening) && weakening.Equals(ServiceForm.ConstantOf(true)) == false ? weakening : null;

        RenderingVerdict Answer(Pred root, ServiceForm form, Verdict claim)
        {
            if (Scope.TryGet(_theory.Agrees.Of(root, RenderPosition.Pos, form, claim), out var proof))
                return new(RenderingVerdictKind.Proven, proof, null, null, Scope.Get(_theory.Vacuous.Of(root, RenderPosition.Pos, form, claim)));

            if (Scope.TryGet(_theory.Disagrees.Of(root, RenderPosition.Pos, form, claim), out var refutation))
                return new(RenderingVerdictKind.Refuted, null, refutation, null);

            return new(RenderingVerdictKind.Unproven, null, null, null);
        }

        void Members(ServiceForm form, List<Fact> facts)
        {
            foreach (var member in form.Members)
                Members(member, facts);

            if (form.Kind is ServiceFormKind.And or ServiceFormKind.Or)
                facts.AddRange(form.Members.Select(member => _theory.Member.Of(form, member)));

            if (form.Kind == ServiceFormKind.And)
                facts.AddRange(form.Flat.Select(member => _theory.Conjunct.Of(form, member)));
        }

    }

}
