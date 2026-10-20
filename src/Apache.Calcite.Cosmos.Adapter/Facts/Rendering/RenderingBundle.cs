using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One statement round in S: a child of the frozen theory holding the round's conjuncts as terms, the import they were
    /// rendered from, and the forms claimed for them; run once, read, and disposed.
    /// </summary>
    /// <remarks>
    /// <b>An answer is read only off a completed run.</b> A run stopped by a limit derived fewer facts, and an agreement
    /// absent for want of an iteration reads as a refutation's absence too, so <see cref="Read"/> answers
    /// <see cref="RenderingVerdictKind.Incomplete"/> for anything but <see cref="RunStopReason.Completed"/>. The limits are
    /// counts, never time, so an answer depends on content alone.
    /// </remarks>
    internal sealed class RenderingBundle : IDisposable
    {

        readonly RenderingTheory _theory;
        string? _source;
        RenderingRun? _run;

        internal RenderingBundle(RenderingTheory theory, RenderingSet set)
        {
            _theory = theory;
            Scope = theory.Theory.CreateChild();
            Scope.Assume(theory.Under.Of(set));
        }

        /// <summary>The bundle's scope, for its tests.</summary>
        public Scope Scope { get; }

        /// <summary>Writes a conjunct as the shared vocabulary's terms, with the spelling of every path it reads.</summary>
        public Pred Write(SqlPredicate predicate)
        {
            var theory = _theory;
            var facts = new List<Fact>();
            var writer = new PredicateWriter(theory.Ir, path =>
            {
                var term = JsonFactEncoding.Path(path).ToTerm(theory.Paths);
                facts.Add(theory.Spelling.Of(term).Is(SqlValue.Spell(path)));
                return term;
            });

            var term = writer.Term(predicate, facts);
            var root = default(Pred);
            Scope.Write(w =>
            {
                root = w.Make(term);
                w.Assume([.. facts]);
            });

            return root;
        }

        /// <summary>
        /// Imports what one path may hold, whole, from the one source the round was rendered from. Before the run only, so
        /// that every rule reads the final value; a second import of the path that differs throws, since S never meets two.
        /// </summary>
        /// <exception cref="InvalidOperationException">The bundle has run, or holds an import from another source.</exception>
        /// <exception cref="FactConflictException">The path was imported with another value.</exception>
        public void Import(string source, JsonDocumentPath path, PathValues values, ValueSet<string> classes)
        {
            if (_run is not null)
                throw new InvalidOperationException("An import after the run would be read by nothing the run derived.");

            if (_source is not null && _source != source)
                throw new InvalidOperationException($"The bundle imports from '{_source}', and one bundle holds one source's facts.");

            _source = source;
            var term = JsonFactEncoding.Path(path).ToTerm(_theory.Paths);
            Scope.Assume(_theory.Imported.Of(term).Is(values), _theory.ImportedChars.Of(term).Is(classes), _theory.PartedLiterals.Of(term).Is(ValueSet<string>.Empty));
        }

        /// <summary>
        /// Claims a written form for a conjunct at the root, and states every member of every junction in it as written,
        /// and of every conjunction its members with nested conjunctions' flattened in, as the service may read a range.
        /// </summary>
        public void Claim(Pred root, ServiceForm form)
        {
            var facts = new List<Fact> { _theory.Candidate.Of(root, RenderPosition.Pos, form) };
            Members(form, facts);
            Scope.Assume([.. facts]);
        }

        /// <summary>Runs the check, within 64 iterations and 50,000 rows, and the tripwire after it.</summary>
        public RenderingRun Run(int iterationLimit = 64)
        {
            var report = Scope.Run(_theory.Check, new RunOptions { IterationLimit = iterationLimit, RowLimit = 50_000 });

            // No rule merges two classes, so every term is its class's one node: a merge would make two terms one.
            var terms = _theory.Graph.Tables.Where(table => table.Kind == TableKind.Constructor).SelectMany(Scope.Rows).ToList();
            return _run = new RenderingRun(report.StopReason, report.Iterations, terms.Select(row => row.Output).Distinct().Count() == terms.Count,
                Scope.Check(_theory.Graph.Contradiction.Of()));
        }

        /// <summary>What the run says of a claim of a form for a conjunct at the root.</summary>
        public RenderingVerdict Read(Pred root, ServiceForm form, Verdict claim)
        {
            if (_run is not { StopReason: RunStopReason.Completed })
                return new(RenderingVerdictKind.Incomplete, null, null, _run is null ? "not run" : "stopped: " + _run.StopReason);

            // A merge would let an agreement pass between two terms; no rule merges, so one is a rule that is wrong.
            if (_run.OneNodePerClass == false)
                return new(RenderingVerdictKind.Conflict, null, null, "tripwire: a rule merged two terms");

            if (_run.Contradicted)
                return new(RenderingVerdictKind.Conflict, null, null, "agreed and refuted: " + string.Join("; ", Scope.Rows(_theory.Conflict)));

            if (Scope.TryGet(_theory.Agrees.Of(root, RenderPosition.Pos, form, claim), out var proof))
                return new(RenderingVerdictKind.Proven, proof, null, null);

            if (Scope.TryGet(_theory.Disagrees.Of(root, RenderPosition.Pos, form, claim), out var refutation))
                return new(RenderingVerdictKind.Refuted, null, refutation, null);

            return new(RenderingVerdictKind.Unproven, null, null, null);
        }

        /// <inheritdoc />
        public void Dispose() => Scope.Dispose();

        void Members(ServiceForm form, List<Fact> facts)
        {
            foreach (var member in form.Members)
            {
                if (form.Kind is ServiceFormKind.And or ServiceFormKind.Or)
                    facts.Add(_theory.Member.Of(form, member));

                Members(member, facts);
            }

            if (form.Kind == ServiceFormKind.And)
                facts.AddRange(Conjuncts(form).Select(member => _theory.Conjunct.Of(form, member)));
        }

        static IEnumerable<ServiceForm> Conjuncts(ServiceForm and) => and.Members.SelectMany(m => m.Kind == ServiceFormKind.And ? Conjuncts(m) : [m]);

    }

}
