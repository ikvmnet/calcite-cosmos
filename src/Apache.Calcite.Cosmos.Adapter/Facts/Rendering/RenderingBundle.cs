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

        /// <summary>
        /// Writes a conjunct as the shared vocabulary's terms, with the spelling of every path it reads, and its syntax
        /// beside them: each leaf, the values a comparison or a membership is over, and each call's operands.
        /// </summary>
        public Pred Write(SqlPredicate predicate) => Write(predicate, null);

        /// <summary>
        /// Writes a conjunct as <see cref="Write(SqlPredicate)"/> does, and beside each opaque predicate whose reading is
        /// given the value it was read as (<see cref="PredicateStatement.Opaques"/>), with the calls in it.
        /// </summary>
        public Pred Write(SqlPredicate predicate, IReadOnlyDictionary<long, OpaqueReading>? opaques)
        {
            foreach (var atom in predicate.Subterms.Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not)))
                _atoms.TryAdd(atom, null);

            var theory = _theory;
            var facts = new List<Fact>();
            var writer = new PredicateWriter(theory.Ir, path =>
            {
                var term = JsonFactEncoding.Path(path).ToTerm(theory.Paths);
                facts.Add(theory.Spelling.Of(term).Is(SqlValue.Spell(path)));
                return term;
            }, leaf: (term, values) => term is ConstructorTerm<Pred> leaf
                ? values.Select(v => theory.Over.Of(leaf, v)).Prepend(theory.Leaf.Of(leaf))
                : values.Select(v => theory.CallArg.Of((ConstructorTerm<Val>)term, v)), opaques: opaques);

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
            var term = JsonFactEncoding.Path(path).ToTerm(_theory.Paths);

            // A differing second import is the importer's fault, not the engine's, and throws.
            try
            {
                if (Failure is null)
                    Scope.Assume(_theory.Imported.Of(term).Is(values), _theory.ImportedChars.Of(term).Is(classes), _theory.PartedLiterals.Of(term).Is(ValueSet<string>.Empty));
            }
            catch (Exception e) when (e is not FactConflictException && IsEngineFailure(e))
            {
                Decline(e);
            }
        }

        /// <summary>
        /// States what an atom's path may hold where its conjunct was rendered: a later round of one of the statements the
        /// import is the root of, which narrows the import and is read of nothing but that conjunct's atoms. Before the run
        /// only; an atom no call names is read as anything there.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The bundle has run; the stratum is no later round of the import's statements; the atom reads no imported path,
        /// or was stated otherwise already.
        /// </exception>
        /// <exception cref="InvalidOperationException">The value admits what the import does not.</exception>
        public void Assume(ImportSource source, SqlPredicate atom, PathValues values)
        {
            if (_run is not null)
                throw new InvalidOperationException("A stratum stated after the run would be read by nothing the run derived.");

            if (_source is null || source.Round <= _source.Round || _source.Statement.Split('+').Contains(source.Statement) == false)
                throw new InvalidOperationException($"{source} is no later round of the statements {_source} is the root of.");

            if (atom.Paths.ToList() is not [var path] || _imported.TryGetValue(path, out var imported) == false)
                throw new InvalidOperationException($"{atom} reads no one path the bundle imported.");

            if (PathValues.Meet(values, imported) != values)
                throw new InvalidOperationException($"{atom}'s stratum says {values}, which the import {imported} does not admit.");

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
        public RenderingRun Run(int iterationLimit = 64) => Run(_theory.Check, iterationLimit);

        /// <summary>
        /// Runs generate mode as <see cref="Run(int)"/> runs the check, within 80 iterations and 8 more for each unit of
        /// the width its roots ask (<see cref="WidthOf"/>): two strata more than the check's, and a junction peeled an arm
        /// an iteration.
        /// </summary>
        public RenderingRun RunGenerate(int width) => Run(_theory.Generate, 80 + 8 * width);

        /// <summary>A generate run's width: the roots' atoms, every <c>IN</c>'s points, and the arms of each root that is a junction.</summary>
        public static int WidthOf(IEnumerable<SqlPredicate> roots)
        {
            var all = roots.ToList();
            var atoms = all.SelectMany(r => r.Subterms).Where(t => t is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not)).Distinct().ToList();
            return atoms.Count + atoms.OfType<SqlPredicate.In>().Sum(i => i.Points.Count) + all.Sum(r => r switch { SqlPredicate.And a => a.Arms.Count, SqlPredicate.Or o => o.Arms.Count, _ => 0 });
        }

        RenderingRun Run(Schedule schedule, int iterationLimit)
        {
            var assumed = _atoms.ToList().Select(a => _theory.Assumed.Of(Write(a.Key)).Is(a.Value ?? PathValues.Anything)).ToList();
            return _run = Guard(() =>
            {
                Scope.Assume([.. assumed]);
                return Ran(schedule, iterationLimit);
            }) ?? new RenderingRun(null, true, false);
        }

        RenderingRun Ran(Schedule schedule, int iterationLimit)
        {
            var report = Scope.Run(schedule, new RunOptions { IterationLimit = iterationLimit, RowLimit = 50_000 });

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
        /// The weakening generate mode wrote for a root at <c>Pos</c>; <c>null</c> where the run may not be read, where none
        /// was written, or where it is <c>TRUE</c>, the one trivial spelling once every constant is folded.
        /// </summary>
        public ServiceForm? ReadWeakening(Pred root) => Guard(() => Readable(out _, out _) ? Weakened(root) : null);

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

        /// <summary>What a call into the engine declines on: the facts graph's engine failures, and running out (Arch §2.12).</summary>
        static bool IsEngineFailure(Exception e) => JsonFactTheory.IsEngineFailure(e) || e is OverflowException;

        T? Guard<T>(Func<T> call)
        {
            if (Failure is not null)
                return default;

            try
            {
                return call();
            }
            catch (Exception e) when (IsEngineFailure(e))
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
        /// The one discipline every read passes: the bundle declined on no engine failure, and its run completed within its
        /// limits, still held one node per class and derived no contradiction. Where it did not, what a read says instead.
        /// </summary>
        bool Readable(out RenderingVerdictKind kind, out string? reason)
        {
            // A merge would let an agreement pass between two terms; no rule merges, so one is a rule that is wrong.
            (kind, reason) = Failure is not null ? (RenderingVerdictKind.Unproven, Declined)
                : _run is not { StopReason: RunStopReason.Completed } ? (RenderingVerdictKind.Incomplete, _run is null ? "not run" : "stopped: " + _run.StopReason)
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
