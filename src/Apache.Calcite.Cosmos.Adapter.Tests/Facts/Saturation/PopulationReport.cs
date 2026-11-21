using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// What one trial checked, and what it found.
    /// </summary>
    sealed class PopulationReport
    {

        public PopulationReport(int seed, Declaration declaration, IReadOnlyList<SqlPredicate> conjuncts)
        {
            Seed = seed;
            Declaration = declaration;
            Conjuncts = conjuncts;
        }

        public int Seed { get; }

        public Declaration Declaration { get; }

        public IReadOnlyList<SqlPredicate> Conjuncts { get; }

        /// <summary>The failures, each beside the check that found it.</summary>
        public List<(string Check, string Message)> Failures { get; } = new();

        public int Facts { get; set; }

        /// <summary>Of <see cref="Facts"/>, those about the characters of a path's strings that say something.</summary>
        public int Characters { get; set; }

        /// <summary>The answers about a computed string's characters that said something, each checked on every document kept.</summary>
        public int Computed { get; set; }

        public int Verdicts { get; set; }

        /// <summary>The verdicts that decided something: always or never, or nothing kept.</summary>
        public int Decided { get; set; }

        public int Rows { get; set; }

        public int Atoms { get; set; }

        public int Nodes { get; set; }

        public int Strata { get; set; }

        public bool Complete { get; set; }

        /// <summary>Whether what every conjunct assumed contradicted the declaration.</summary>
        public bool Kept { get; set; }

        /// <summary>Whether the reduce's node was contradictory, the WHERE raising on no document the declaration admits.</summary>
        public bool Reduced { get; set; }

        /// <summary>Whether the arm asked of was never read: its question's node, gated on the WHERE, contradictory.</summary>
        public bool NeverRead { get; set; }

        /// <summary>Whether the raises question's node was contradictory, nothing the statement holds raising.</summary>
        public bool RaisesNothing { get; set; }

        /// <summary>The statement the raises question was asked of beside a traversal (<see cref="Statements.Traversed"/>).</summary>
        public IReadOnlyList<SqlPredicate> Traversed { get; set; } = [];

        /// <summary>Whether the raises question's node beside a traversal was contradictory, nothing that statement holds raising.</summary>
        public bool TraversedRaisesNothing { get; set; }

        public TimeSpan Elapsed { get; set; }

        public void Fail(string check, string message)
        {
            if (Failures.Count < 20 && Failures.Contains((check, message)) == false)
                Failures.Add((check, message));
        }

        /// <summary>The trial, spelled so that it can be reproduced and read.</summary>
        public string Describe() =>
            $"seed {Seed}\ndeclaration:\n  {Declaration.ToString().Replace("\n", "\n  ")}\nconjuncts:\n  {string.Join("\n  ", Conjuncts.Select((c, i) => i + ": " + c))}\n"
            + $"beside a traversal:\n  {string.Join("\n  ", Traversed.Select((c, i) => i + ": " + c))}\nfailures:\n  {string.Join("\n  ", Failures.Select(f => "[" + f.Check + "] " + f.Message))}";

    }

}
