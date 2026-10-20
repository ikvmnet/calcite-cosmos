using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The definition-time order test: every rule S's two schedules run, read off its rendering, against the strata it runs
    /// in, so that no verdict can fire over a set a rule is still growing (critic-validation F1, which reported
    /// <c>exact=True</c> beside a disagreeing cell from a run that completed).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it reads.</b> <c>Rule.ToString()</c> renders <c>(rule (body…) (head…) :ruleset name)</c>; named primitives
    /// render by name and lambdas as <c>&lt;lambda&gt;</c>. A value is traced through the body's lets to the functions
    /// whose outputs bind it; a function is complete in a stratum where it has no lattice, or every rule of the schedule
    /// that writes it runs in an earlier stratum, which takes in the host's tables and the frozen theory's.
    /// </para>
    /// <para>
    /// <b>What it asserts.</b> No head merges classes. No bundle rule writes a table the host or the theory owns, and no
    /// theory ruleset runs in a bundle. Both arguments of a universal (<c>set-subset</c>, <c>truths-within</c>,
    /// <c>outcomes-within</c>) and every input of a lattice-free head are complete. The superset of a <c>set-subset</c>
    /// and the container of a <c>set-contains</c> never narrow in their stratum or after. No filter is a lambda. Only the
    /// conflict rule derives the contradiction.
    /// </para>
    /// </remarks>
    public sealed class RenderingOrderTests
    {

        static readonly string[] HostAndTheory =
        [
            "outcome", "cites", "serves", "means", "answers", "measured", "evidence", "final", "table", "class", "raiseonly", "allowing",
            "refuting", "tolerated", "allowed", "allowedcites", "refutes", "edges", "parting", "kleene", "expected", "matched", "licensed",
            "identity", "opposite", "connectiveof", "version", "spelling", "conjunct", "under", "imported", "importedchars",
        ];

        static readonly string[] Universals = ["set-subset", "truths-within", "outcomes-within"];

        static readonly string[] Narrowing = ["intersect", "min", PathValues.Lattice.Name];

        [Fact]
        public void TheScheduleReadsNoUniversalOverADomainThatMoves()
        {
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(ProbeRows.Rows));
            Violations(theory).Should().BeEmpty();
        }

        /// <summary>
        /// The plant after F1: a stratum grows a set of classes and reads it with <c>set-subset</c>, so the universal may
        /// fire before the set is whole.
        /// </summary>
        [Fact]
        public void AUniversalOverASetItsOwnStratumGrowsIsRefused()
        {
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(ProbeRows.Rows));
            var seen = theory.Graph.DefineFunction<Pred, ValueSet<CellClass>>("seen", ValueLattice.Union<CellClass>());
            theory.Graph.Rulesets.Single(r => r.Name == "classes").DefineRule("planted: a cell's class is seen", r =>
            {
                var (e, f, o) = r.Vars<Pred, ServiceForm, CellOutcomes>("e", "f", "o");
                r.When(o.Is(theory.Outcomes.Of(e, f)));
                r.Then(seen.Of(e).Is(Compute.Of(o, x => x.Classes)));
            });
            theory.Graph.Rulesets.Single(r => r.Name == "gate").DefineRule("planted: every class seen is allowed", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (k, y, o) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, CellOutcomes>("k", "y", "o");
                r.When(theory.Candidate.Of(e, pos, f), k.Is(seen.Of(e)), y.Is(theory.Allowed.Of(pos, v)), o.Is(theory.Outcomes.Of(e, f)));
                r.Where(Primitives.IsSubsetOf<CellClass>().Of(k, y));
                r.Then(theory.Agrees.Of(e, pos, f, v).Is(Compute.Of(o, y, (x, w) => Proof.Of(x, ValueSet<string>.Empty, false))));
            });

            Violations(theory).Should().Equal("gate/planted: every class seen is allowed: set-subset reads seen, which grows in its stratum");
        }

        /// <summary>
        /// A stratum narrows a set of character classes and reads it as the superset of <c>set-subset</c>, so the universal
        /// may hold of a set that later loses a member.
        /// </summary>
        [Fact]
        public void AUniversalOverATableItsOwnStratumNarrowsIsRefused()
        {
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(ProbeRows.Rows));
            var chars = theory.Graph.DefineFunction<JPath, ValueSet<string>>("chars", ValueLattice.Intersect<string>());
            theory.Graph.Rulesets.Single(r => r.Name == "cuts").DefineRule("planted: a path's classes narrow", r =>
            {
                var (p, k) = r.Vars<JPath, ValueSet<string>>("p", "k");
                r.When(k.Is(theory.ImportedChars.Of(p)));
                r.Then(chars.Of(p).Is(k));
            });
            theory.Graph.Rulesets.Single(r => r.Name == "parted").DefineRule("planted: a literal within the classes parts nothing", r =>
            {
                var (p, k, x) = r.Vars<JPath, ValueSet<string>, ValueSet<string>>("p", "k", "x");
                r.When(k.Is(chars.Of(p)), x.Is(theory.PartedLiterals.Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(x, k));
                r.Then(theory.PartedLiterals.Of(p).Is(x));
            });

            Violations(theory).Should().Contain("parted/planted: a literal within the classes parts nothing: set-subset reads chars, which narrows in its stratum");
        }

        /// <summary>A filter written as a lambda over an agreement is one the order test cannot read, and is refused.</summary>
        [Fact]
        public void ALambdaFilterIsRefused()
        {
            var theory = RenderingTheory.Define(RenderingDefinition.Measured(ProbeRows.Rows));
            var proven = theory.Graph.DefineRelation<Pred>("proven");
            theory.Graph.Rulesets.Single(r => r.Name == "compose").DefineRule("planted: an agreement over some cell", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var p = r.Var<Proof>("p");
                r.When(p.Is(theory.Agrees.Of(e, pos, f, v)));
                r.Where(p, x => x.Cells > 0);
                r.Then(proven.Of(e));
            });

            Violations(theory).Should().Equal("compose/planted: an agreement over some cell: a filter is a lambda");
        }

        static List<string> Violations(RenderingTheory theory)
        {
            var tables = theory.Graph.Tables.ToDictionary(t => t.Name);
            var settle = Strata(theory.Settle);
            var check = Strata(theory.Check);
            var violations = settle.Keys.Intersect(check.Keys).Select(name => $"the theory's {name} runs in a bundle").ToList();
            foreach (var (strata, bundle) in new[] { (settle, false), (check, true) })
            {
                var rules = theory.Graph.Rulesets.Where(r => strata.ContainsKey(r.Name)).SelectMany(r => r.Rules)
                    .Select(rule => (Stratum: strata[rule.Ruleset.Name], Name: rule.Ruleset.Name + "/" + rule.Name, Read: new Reading(Parse(rule.ToString(), tables.Keys), tables)))
                    .ToList();
                var writers = rules.SelectMany(r => r.Read.Writes.Select(table => (table, r.Stratum))).ToLookup(w => w.table, w => w.Stratum);

                foreach (var (stratum, name, read) in rules)
                {
                    ValueLattice? Lattice(string table) => (tables[table] as FunctionHandle)?.Lattice;
                    bool Grows(string table) => Lattice(table) is not null && writers[table].Any(w => w >= stratum);
                    bool Narrows(string table) => Narrowing.Contains(Lattice(table)?.Name) && writers[table].Any(w => w >= stratum);
                    void Refuse(string what, Node value, Func<string, bool> moves, string how) =>
                        violations.AddRange(read.Sources(value).Where(moves).Select(table => $"{name}: {what} reads {table}, which {how} in its stratum"));

                    foreach (var (primitive, arguments) in read.Calls)
                    {
                        if (Universals.Contains(primitive))
                            foreach (var argument in arguments)
                                Refuse(primitive, argument, Grows, "grows");

                        if (primitive == "set-subset")
                            Refuse(primitive, arguments[1], Narrows, "narrows");
                        else if (primitive == "set-contains")
                            Refuse(primitive, arguments[0], Narrows, "narrows");
                    }

                    foreach (var (table, value) in read.Sets.Where(s => Lattice(s.Table) is null))
                        Refuse("the lattice-free " + table, value, Grows, "grows");

                    if (read.LambdaFilter)
                        violations.Add($"{name}: a filter is a lambda");
                    if (read.Unions)
                        violations.Add($"{name}: a head merges classes");
                    if (read.Writes.Contains("contradiction") && read.Ruleset != "conflict")
                        violations.Add($"{name}: derives the contradiction");
                    if (bundle)
                        violations.AddRange(read.Writes.Intersect(HostAndTheory).Select(table => $"{name}: writes {table}, which the host or the theory owns"));
                }
            }

            return violations;
        }

        /// <summary>The stratum of each ruleset: the index of the top-level step of the sequence it runs in.</summary>
        static Dictionary<string, int> Strata(Schedule schedule)
        {
            var strata = new Dictionary<string, int>();
            var steps = Parse(schedule.ToString(), []).Items!.Skip(1).ToList();
            for (var i = 0; i < steps.Count; i++)
                foreach (var run in Lists(steps[i]).Where(l => l.Items![0].Token == "run"))
                    foreach (var ruleset in run.Items!.Skip(1))
                        strata[ruleset.Token!] = i;

            return strata;
        }

        static IEnumerable<Node> Lists(Node node) => node.Items is { } items ? items.SelectMany(Lists).Prepend(node) : [];

        /// <summary>Reads an s-expression; after an opening parenthesis a table's name is one token, spaces and all.</summary>
        static Node Parse(string text, IEnumerable<string> names)
        {
            var spaced = names.Where(n => n.Contains(' ')).OrderByDescending(n => n.Length).ToList();
            var i = 0;
            return Read();

            Node Read()
            {
                while (text[i] == ' ')
                    i++;

                if (text[i] == '(')
                {
                    var items = new List<Node>();
                    if (spaced.FirstOrDefault(n => string.CompareOrdinal(text, i + 1, n, 0, n.Length) == 0 && text[i + 1 + n.Length] is ' ' or ')') is { } name)
                    {
                        items.Add(new Node(name, null));
                        i += name.Length;
                    }

                    for (i++; text[i] != ')'; )
                    {
                        items.Add(Read());
                        while (text[i] == ' ')
                            i++;
                    }

                    i++;
                    return new Node(null, items);
                }

                var start = i;
                if (text[i] == '"')
                    for (i++; text[i] != '"'; i++)
                        if (text[i] == '\\')
                            i++;

                while (i < text.Length && text[i] is not (' ' or ')' or '('))
                    i++;

                return new Node(text[start..i], null);
            }
        }

        /// <summary>An atom of an s-expression, or a list of them.</summary>
        sealed record Node(string? Token, List<Node>? Items)
        {

            public string? Head => Items is [{ Token: { } head }, ..] ? head : null;

        }

        /// <summary>
        /// A rule's rendering, read: what each variable's value comes from, the primitives it applies, and what its head
        /// writes.
        /// </summary>
        sealed class Reading
        {

            readonly Dictionary<string, HashSet<string>> _sources = [];

            public Reading(Node rule, IReadOnlyDictionary<string, TableHandle> tables)
            {
                var (body, head) = (rule.Items![1].Items!, rule.Items[2].Items!);
                Ruleset = rule.Items[4].Token!;
                var lets = new List<(string Variable, Node Value)>();
                foreach (var fact in body)
                {
                    if (fact.Head == "=" && fact.Items![2].Head is { } applied)
                    {
                        if (tables.ContainsKey(applied))
                            Bind(fact.Items[1].Token!, [applied]);
                        else
                            lets.Add((fact.Items[1].Token!, fact.Items[2]));
                    }
                    else if (fact.Head is { } filter && filter != "!=" && tables.ContainsKey(filter) == false)
                    {
                        LambdaFilter |= filter == "<lambda>";
                        Calls.AddRange(Applications(fact));
                    }
                }

                // A let's value comes from its inputs' sources, which may be bound by a later let.
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var (variable, value) in lets)
                        changed |= Bind(variable, Sources(value));
                }

                Calls.AddRange(lets.SelectMany(l => Applications(l.Value)));
                foreach (var item in head)
                {
                    if (item.Head == "union")
                    {
                        Unions = true;
                    }
                    else if (item.Head == "set")
                    {
                        Writes.Add(item.Items![1].Head!);
                        Sets.Add((item.Items[1].Head!, item.Items[2]));
                        Calls.AddRange(Applications(item.Items[2]));
                    }
                    else if (item.Head is { } table)
                    {
                        Writes.Add(table);
                    }
                }
            }

            public string Ruleset { get; }

            public List<(string Primitive, List<Node> Arguments)> Calls { get; } = [];

            public List<string> Writes { get; } = [];

            public List<(string Table, Node Value)> Sets { get; } = [];

            public bool LambdaFilter { get; }

            public bool Unions { get; }

            /// <summary>The functions whose outputs a term's value is computed from.</summary>
            public IEnumerable<string> Sources(Node term) =>
                term.Items is { } items ? items.Skip(1).SelectMany(Sources).Distinct() : _sources.TryGetValue(term.Token!, out var tables) ? tables : [];

            bool Bind(string variable, IEnumerable<string> tables)
            {
                var known = _sources.TryGetValue(variable, out var set) ? set : _sources[variable] = [];
                return tables.ToList().Count(known.Add) > 0;
            }

            /// <summary>Every primitive applied in a term, nested ones included, with its arguments.</summary>
            static IEnumerable<(string, List<Node>)> Applications(Node term) => term.Items is { } items
                ? items.Skip(1).SelectMany(Applications).Prepend((term.Head!, items.Skip(1).ToList()))
                : [];

        }

    }

}
