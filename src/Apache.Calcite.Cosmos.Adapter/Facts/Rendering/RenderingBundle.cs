using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One statement round in S: a child of the frozen theory holding the round's conjuncts as terms, the import they were
    /// rendered from, and the forms claimed for them; run once, read off a completed run alone, and disposed.
    /// </summary>
    /// <remarks>
    /// An engine failure in any call declines the bundle rather than throwing: every claim then reads Unproven, and where
    /// the graph ran out of ids or epochs its generation retires (<see cref="RenderingGraph"/>).
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

        /// <summary>Writes a conjunct as the shared vocabulary's terms, with the spelling of every path it reads.</summary>
        public Pred Write(SqlPredicate predicate)
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
            });

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
        /// Runs the check within count limits, never time, and the tripwire after it. A run the engine failed has no stop
        /// reason.
        /// </summary>
        public RenderingRun Run(int iterationLimit = 64)
        {
            var assumed = _atoms.ToList().Select(a => _theory.Assumed.Of(Write(a.Key)).Is(a.Value ?? PathValues.Anything)).ToList();
            return _run = Guard(() =>
            {
                Scope.Assume([.. assumed]);
                return Ran(iterationLimit);
            }) ?? new RenderingRun(null, true, false);
        }

        RenderingRun Ran(int iterationLimit)
        {
            var report = Scope.Run(_theory.Check, new RunOptions { IterationLimit = iterationLimit, RowLimit = 50_000 });

            // No rule merges two classes, so every term is its class's one node: a merge would make two terms one.
            var terms = _theory.Graph.Tables.Where(table => table.Kind == TableKind.Constructor).SelectMany(Scope.Rows).ToList();
            return new RenderingRun(report.StopReason, terms.Select(row => row.Output).Distinct().Count() == terms.Count,
                Scope.Check(_theory.Graph.Contradiction.Of()));
        }

        /// <summary>What the run says of a claim of a form for a conjunct at the root; Unproven where the bundle declined.</summary>
        public RenderingVerdict Read(Pred root, ServiceForm form, Verdict claim) => Guard(() => Answer(root, form, claim))
            ?? new(RenderingVerdictKind.Unproven, null, null, $"declined: {Failure!.GetType().Name}: {Failure.Message}");

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

        RenderingVerdict Answer(Pred root, ServiceForm form, Verdict claim)
        {
            if (_run is not { StopReason: RunStopReason.Completed })
                return new(RenderingVerdictKind.Incomplete, null, null, _run is null ? "not run" : "stopped: " + _run.StopReason);

            // A merge would let an agreement pass between two terms; no rule merges, so one is a rule that is wrong.
            if (_run.OneNodePerClass == false)
                return new(RenderingVerdictKind.Conflict, null, null, "tripwire: a rule merged two terms");

            if (_run.Contradicted)
                return new(RenderingVerdictKind.Conflict, null, null, "agreed and refuted: " + string.Join("; ", Scope.Rows(_theory.Conflict)));

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
