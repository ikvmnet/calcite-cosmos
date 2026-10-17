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
    /// The predicate oracle checked on population D: a random declaration, a random filter over its paths, and documents
    /// drawn from the declaration, not from the filter. Everything the oracle says is checked on the documents the scope it
    /// was said in admits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why D and not the query's documents.</b> <c>SoundnessTests</c> states facts true of one document and checks what
    /// follows on that document, which is the right shape for a theory of facts and blind to the one mistake a theory of
    /// predicates makes most: a conjunct licensed by its own facts, which is right on every document that satisfies it and
    /// wrong on the rest. A scope's documents here are the declared documents its licence's conjuncts keep, read off
    /// <see cref="IrEval"/>, so a fact that came from somewhere it should not have is false of one of them.
    /// </para>
    /// <para>
    /// <b>What is checked, in every scope the licence table names</b> — the declaration, the last stratum, the stratum each
    /// conjunct was rendered from, every leave-one-out child, a point read's pins:
    /// </para>
    /// <list type="bullet">
    /// <item>a contradiction admits no document;</item>
    /// <item>every fact of JSON the context holds, row by row, and every claim its fact set answers, is true of every
    /// document it admits;</item>
    /// <item>every verdict — of each conjunct, each term within one, and predicates the statement does not hold — agrees
    /// with the evaluator on every document it admits, as does every <c>holds</c>, <c>always true</c>,
    /// <c>always false</c>, <c>never true</c> and <c>possible</c> row;</item>
    /// <item>every <c>atom</c> row's meaning agrees with the evaluator on every declared document: the truth value is
    /// among those <c>Over</c> names, and the document is among those <c>Kept</c> names for it;</item>
    /// <item>C5: every e-node of the <c>Pred</c>, <c>Val</c> and <c>JPath</c> sorts, evaluated over its children's
    /// representatives, agrees with its class's representative on every declared document, the only check that sees a
    /// union;</item>
    /// <item>the facts each conjunct was handed to render from are true of every document the conjuncts admitted before
    /// it keep, and a conjunct the oracle calls redundant drops no row.</item>
    /// </list>
    /// <para>
    /// <b>A verdict may stand over a row Calcite raises on.</b> The connectives do not model a raise, and whether an
    /// answer may stand where Calcite fails is the refinement order still to settle; a raise is held to the table that
    /// says whether one happens, by the atom check, which is where it is decided.
    /// </para>
    /// </remarks>
    sealed class PopulationD
    {

        /// <summary>How many documents each trial draws from its declaration.</summary>
        public const int DocumentsPerTrial = 64;

        /// <summary>
        /// How many more it draws holding a string of either band where the declaration lets one stand, from a stream of
        /// their own: what the characters a string is said to hold are checked on (<see cref="Declaration"/>).
        /// </summary>
        public const int BandedPerTrial = 16;

        readonly Func<JsonFactTheory, CosmosOracle> _oracle;

        /// <param name="oracle">How an oracle is made over a declaration: by default, as the planner would, in its own graphs.</param>
        public PopulationD(Func<JsonFactTheory, CosmosOracle>? oracle = null)
        {
            _oracle = oracle ?? (theory => new CosmosOracle(theory, null));
        }

        /// <summary>
        /// Runs one trial.
        /// </summary>
        /// <param name="seed">The trial's seed, which fixes the declaration, the documents, the filter and the world.</param>
        /// <returns>What was checked, and what failed.</returns>
        public PopulationReport Run(int seed)
        {
            var clock = Stopwatch.StartNew();
            var random = new Random(seed);
            var bands = new Random(unchecked(seed * 7919 + 17));
            var world = new World(seed);
            var declaration = Declaration.Random(random, bands);
            var documents = Enumerable.Range(0, DocumentsPerTrial).Select(_ => declaration.Generate(random))
                .Concat(Enumerable.Range(0, BandedPerTrial).Select(_ => declaration.GenerateBanded(bands)))
                .ToArray();
            var conjuncts = Statements.Draw(random, declaration);
            var report = new PopulationReport(seed, declaration, conjuncts);

            foreach (var document in documents)
                if (declaration.Admits(document) == false)
                    report.Fail("generator", $"drew {Documents.Json(document)}, which the declaration does not admit");

            var statement = new PredicateStatement(conjuncts);
            var oracle = _oracle(new JsonFactTheory(declaration.Rules));

            // A renderer that reads what it is handed: a conjunct renders unaided, or where a path it needs is known there —
            // most often one a sibling reads, so that a sibling admitted first may be what admits it.
            var needs = conjuncts.Select((c, i) =>
            {
                var read = conjuncts.Where((_, j) => j != i).SelectMany(s => s.Paths).Distinct().ToArray();
                return read.Length > 0 && random.Next(3) > 0 ? read[random.Next(read.Length)] : Declaration.Paths[random.Next(Declaration.Paths.Length)];
            }).ToArray();
            var unaided = conjuncts.Select(_ => random.Next(3) == 0).ToArray();
            var handed = new Dictionary<int, JsonFactSet>();
            var scope = oracle.Admit(statement, (i, facts) =>
            {
                var renders = unaided[i] || facts.Knows(new JsonFact(needs[i], new JsonClaim.Present()));
                if (renders)
                    handed[i] = facts;
                return renders;
            });

            var truths = conjuncts.Select(c => documents.Select(d => IrEval.Truth(c, d, world)).ToArray()).ToArray();
            IReadOnlyList<J.Obj> Keeping(IEnumerable<int> assumed)
            {
                var set = assumed.ToArray();
                return documents.Where((_, d) => set.All(c => truths[c][d] == Truths.True)).ToArray();
            }

            // The questions: every term of every conjunct, and two predicates the statement does not hold.
            var questions = conjuncts.SelectMany(c => c.Subterms).Concat(Enumerable.Range(0, 2).Select(_ => Statements.Predicate(random, declaration, 0, new List<SqlPredicate>())))
                .GroupBy(p => p.ToString(), StringComparer.Ordinal).Select(g => g.First()).ToArray();
            var check = new Check(report, world, documents, questions);

            // The licence table, read from the strata the oracle admitted in, and never from what it says it assumed.
            var stratum = Enumerable.Range(0, conjuncts.Count).Select(scope.StratumOf).ToArray();
            var admitted = Enumerable.Range(0, conjuncts.Count).Where(i => stratum[i] > 0).ToArray();
            var licences = new List<(string Name, OracleLicence Licence, int[] Assumed)>
            {
                ("the declaration", OracleLicence.Declaration, Array.Empty<int>()),
                ("the last stratum", OracleLicence.Select, admitted),
            };

            for (var i = 0; i < conjuncts.Count; i++)
            {
                var before = stratum[i] > 0 ? admitted.Where(j => stratum[j] < stratum[i]).ToArray() : admitted;
                licences.Add(($"rendering conjunct {i}", OracleLicence.RenderConjunct(i), before));
                var others = Enumerable.Range(0, conjuncts.Count).Where(j => j != i).ToArray();
                licences.Add(($"deleting conjunct {i}", OracleLicence.DeleteConjunct(i), stratum[i] > 0 ? others.Where(j => stratum[j] > 0).ToArray() : others));
                licences.Add(($"rewriting inside conjunct {i}", OracleLicence.Siblings(i), others));
            }

            // Every conjunct, for a strengthening; and an arm of a CASE inside one, read under a condition the trial
            // draws, which holds of the documents its siblings keep and the condition does.
            licences.Add(("every conjunct", OracleLicence.Kept, Enumerable.Range(0, conjuncts.Count).ToArray()));

            var arm = random.Next(conjuncts.Count);
            var condition = questions[random.Next(questions.Length)];
            var siblings = Enumerable.Range(0, conjuncts.Count).Where(j => j != arm).ToArray();
            check.Scope($"an arm in conjunct {arm} under {condition}", scope, OracleLicence.Arm(arm, new[] { condition }),
                Keeping(siblings).Where(d => IrEval.Truth(condition, d, world) == Truths.True).ToArray(), siblings);

            // And a weakening beside the statement, read beside what a filter beneath applies, drawn the same way.
            var beneath = questions[random.Next(questions.Length)];
            check.Scope($"a weakening beside what is applied, {beneath} beneath", scope, OracleLicence.Applied(new[] { beneath }),
                Keeping(admitted).Where(d => IrEval.Truth(beneath, d, world) == Truths.True).ToArray(), admitted);

            var pins = conjuncts.Select(Pin).OfType<JsonDocumentPath>().Distinct().ToArray();
            if (pins.Length > 0)
                licences.Add(("a point read", OracleLicence.PointRead(pins), Enumerable.Range(0, conjuncts.Count).Where(j => Pin(conjuncts[j]) is not null).ToArray()));

            foreach (var (name, licence, assumed) in licences)
            {
                var structural = scope.AssumedIn(licence);
                if (structural.Except(assumed).Any())
                    report.Fail("licence", $"{name} assumes conjuncts {string.Join(", ", structural)}, beyond {string.Join(", ", assumed)}");

                check.Scope(name, scope, licence, Keeping(assumed), assumed);
            }

            // The strata: what each conjunct was rendered from holds of what the conjuncts before it keep.
            foreach (var (i, facts) in handed.OrderBy(kv => kv.Key))
            {
                if (stratum[i] == 0)
                    continue;

                var before = admitted.Where(j => stratum[j] < stratum[i]).ToArray();
                check.FactSet($"the facts conjunct {i} was rendered from, in stratum {stratum[i]}", facts, Keeping(before), before, "strata", "admitted before it");
            }

            // Redundancy: what is deleted one at a time drops no row the rest keep.
            var removed = scope.RedundantConjuncts();
            var kept = Enumerable.Range(0, conjuncts.Count).Except(removed).ToArray();
            for (var d = 0; d < documents.Length; d++)
                if (kept.All(k => truths[k][d] == Truths.True))
                    foreach (var r in removed)
                        if (truths[r][d] is not (Truths.True or Truths.Raises))
                            report.Fail("redundant", $"conjunct {r} was deleted as redundant beside {string.Join(", ", kept)}, and is {truths[r][d]} of {Documents.Json(documents[d])}, which they keep");

            // Implication: what a predicate is given others.
            for (var n = 0; n < 2; n++)
            {
                var y = questions[random.Next(questions.Length)];
                var given = Enumerable.Range(0, 1 + random.Next(2)).Select(_ => questions[random.Next(questions.Length)]).ToArray();
                var truth = oracle.Admit(new PredicateStatement(given)).Decide(y, OracleLicence.Kept);
                var keeping = documents.Where(d => given.All(x => IrEval.Truth(x, d, world) == Truths.True)).ToArray();
                check.Decided($"{y} given {string.Join(" and ", given.AsEnumerable())}", truth, y, keeping);
            }

            report.Elapsed = clock.Elapsed;
            report.Strata = scope.Strata.Count;
            report.Complete = scope.Complete;
            return report;
        }

        /// <summary>
        /// The key a conjunct pins for a point read: an equality of what a path holds or reads as with one literal, as
        /// the oracle's own test reads one.
        /// </summary>
        static JsonDocumentPath? Pin(SqlPredicate conjunct) => conjunct switch
        {
            SqlPredicate.Compare { Op: PredicateAtom.Equal, Left: var value, Right: SqlValue.Literal } when value is not SqlValue.Cast => value.PathRead,
            SqlPredicate.In { Value: var value, Points.Count: 1 } when value is not SqlValue.Cast => value.PathRead,
            _ => null,
        };

        /// <summary>
        /// The checks of one trial, over its documents and its questions.
        /// </summary>
        sealed class Check
        {

            readonly PopulationReport _report;
            readonly World _world;
            readonly IReadOnlyList<J.Obj> _documents;
            readonly IReadOnlyList<SqlPredicate> _questions;
            readonly HashSet<(Scope, string)> _read = new();
            readonly HashSet<Scope> _validated = new();

            public Check(PopulationReport report, World world, IReadOnlyList<J.Obj> documents, IReadOnlyList<SqlPredicate> questions)
            {
                _report = report;
                _world = world;
                _documents = documents;
                _questions = questions;
            }

            Truths Truth(SqlPredicate predicate, J.Obj document) => IrEval.Truth(predicate, document, _world);

            /// <summary>
            /// Checks everything a scope says on the documents its licence admits.
            /// </summary>
            public void Scope(string name, CosmosStatementScope scope, OracleLicence licence, IReadOnlyList<J.Obj> keeping, IReadOnlyList<int> assumed)
            {
                FactSet(name, scope.Facts(licence), keeping, assumed, "fact", "it assumes");

                if (scope.IsContradictory(licence) && keeping.Count > 0)
                    _report.Fail("contradiction", $"{name} is contradictory, and the conjuncts it assumes ({string.Join(", ", assumed)}) keep {Documents.Json(keeping[0])}");

                foreach (var question in _questions)
                {
                    var verdict = scope.Judge(question, licence);
                    _report.Verdicts++;
                    Verdict($"{question} in {name}", verdict, question, keeping);
                }

                var (context, rules) = scope.ContextFor(licence);
                if (context is null || rules is null)
                    return;

                if (_read.Add((context, string.Join(",", assumed))))
                    Rows(name, context, rules, keeping);

                if (_validated.Add(context))
                    Validate(name, context, rules);

                // The context lives as long as the scope's lease; see ContextFor.
                GC.KeepAlive(scope);
            }

            /// <summary>
            /// Checks a fact set as a consumer reads one: every claim it answers of a path, every disequality it knows of
            /// a scalar a path might hold, and whether it is contradictory.
            /// </summary>
            public void FactSet(string name, JsonFactSet facts, IReadOnlyList<J.Obj> keeping, IReadOnlyList<int> assumed, string kind, string which)
            {
                if (facts.IsContradictory && keeping.Count > 0)
                    _report.Fail(kind, $"{name} is contradictory, and the conjuncts {which} ({string.Join(", ", assumed)}) keep {Documents.Json(keeping[0])}");

                foreach (var path in Declaration.Paths)
                {
                    foreach (var claim in facts.ClaimsFor(path))
                    {
                        _report.Facts++;
                        foreach (var document in keeping)
                            if (Declaration.Holds(IrEval.At(document, path), claim) == false)
                            {
                                _report.Fail(kind, $"{name} claims {SqlValue.Spell(path)} {claim}, false of {Documents.Json(document)}, which the conjuncts {which} ({string.Join(", ", assumed)}) keep");
                                break;
                            }
                    }

                    foreach (var value in Declaration.Values.Where(v => v.IsScalar))
                    {
                        if (JsonFactEncoding.TryScalar(value.Encoded, out var scalar) == false || facts.Knows(new JsonFact(path, new JsonClaim.NotEqualTo(scalar))) == false)
                            continue;

                        _report.Facts++;
                        foreach (var document in keeping)
                            if (Declaration.Holds(IrEval.At(document, path), new JsonClaim.NotEqualTo(scalar)) == false)
                            {
                                _report.Fail(kind, $"{name} knows {SqlValue.Spell(path)} is not {value}, false of {Documents.Json(document)}");
                                break;
                            }
                    }
                }
            }

            /// <summary>
            /// Checks a verdict on the documents its scope admits. A raise satisfies any verdict (see the class remarks).
            /// </summary>
            public void Verdict(string what, OracleVerdict verdict, SqlPredicate predicate, IReadOnlyList<J.Obj> keeping)
            {
                if (verdict.Contradictory || verdict.AlwaysTrue || verdict.AlwaysFalse || verdict.NeverTrue)
                    _report.Decided++;

                if (verdict.Contradictory && keeping.Count > 0)
                {
                    _report.Fail("verdict", $"{what} is answered from a contradiction, and {Documents.Json(keeping[0])} is kept");
                    return;
                }

                foreach (var document in keeping)
                {
                    var truth = Truth(predicate, document);
                    if (truth == Truths.Raises)
                        continue;

                    if (verdict.AlwaysTrue && truth != Truths.True || verdict.AlwaysFalse && truth != Truths.False || verdict.NeverTrue && truth == Truths.True)
                    {
                        _report.Fail("verdict", $"{what} is {Spell(verdict)}, and {truth} of {Documents.Json(document)}");
                        return;
                    }
                }
            }

            /// <summary>
            /// Checks a planner's reading of a verdict, always or never, on the documents it was decided over.
            /// </summary>
            public void Decided(string what, Truth truth, SqlPredicate predicate, IReadOnlyList<J.Obj> keeping)
            {
                _report.Verdicts++;
                foreach (var document in keeping)
                {
                    var actual = Truth(predicate, document);
                    if (actual == Truths.Raises)
                        continue;

                    if (truth == Apache.Calcite.Cosmos.Facts.Saturation.Truth.Always && actual != Truths.True || truth == Apache.Calcite.Cosmos.Facts.Saturation.Truth.Never && actual == Truths.True)
                    {
                        _report.Fail("verdict", $"{what} is decided {truth}, and is {actual} of {Documents.Json(document)}");
                        return;
                    }
                }
            }

            static string Spell(OracleVerdict verdict) =>
                string.Join(" and ", new[] { verdict.AlwaysTrue ? "always true" : null, verdict.AlwaysFalse ? "always false" : null, verdict.NeverTrue ? "never true" : null }.OfType<string>());

            /// <summary>
            /// Reads a context's every row of the theory's facts and the predicates' verdicts, and checks each on the
            /// documents the context admits; and every atom's meaning on every declared document.
            /// </summary>
            void Rows(string name, Scope context, PredicateRules rules, IReadOnlyList<J.Obj> keeping)
            {
                var graph = new ClassIndex(context, rules);
                var json = rules.Json;

                // The theory of JSON's own facts.
                foreach (var fact in JsonFacts(context, json, graph))
                {
                    _report.Facts++;
                    foreach (var document in keeping)
                        if (Documents.IsTrue(document, fact) == false)
                        {
                            _report.Fail("fact", $"{name} holds {fact}, false of {Documents.Json(document)}");
                            break;
                        }
                }

                // What a predicate takes on every row, and the verdicts, read as rows.
                foreach (var row in context.Rows(rules.Holds))
                {
                    if (graph.Predicate((ClassId)row.Keys[0]) is not SqlPredicate held)
                        continue;

                    var expected = (bool)row.Keys[1] ? Truths.True : Truths.False;
                    _report.Rows++;
                    foreach (var document in keeping)
                    {
                        var truth = Truth(held, document);
                        if (truth != expected && truth != Truths.Raises)
                        {
                            _report.Fail("holds", $"{name} holds {held} {expected}, and it is {truth} of {Documents.Json(document)}");
                            break;
                        }
                    }
                }

                foreach (var (table, verdict) in new[] { (rules.AlwaysTrue, new OracleVerdict(false, true, false, false)), (rules.AlwaysFalse, new OracleVerdict(false, false, true, false)), (rules.NeverTrue, new OracleVerdict(false, false, false, true)) })
                    foreach (var row in context.Rows(table))
                        if (graph.Predicate((ClassId)row.Keys[0]) is SqlPredicate decided)
                        {
                            _report.Rows++;
                            Verdict($"{decided}, a row of {name}", verdict, decided, keeping);
                        }

                foreach (var row in context.Rows(rules.Possible))
                {
                    if (graph.DocumentPath((ClassId)row.Keys[0]) is not JsonDocumentPath path)
                        continue;

                    var possible = (PathValues)row.Output!;
                    _report.Rows++;
                    foreach (var document in keeping)
                        if (Admits(possible, IrEval.At(document, path)) == false)
                        {
                            _report.Fail("possible", $"{name} says {SqlValue.Spell(path)} may hold {possible}, and {Documents.Json(document)} holds {Show(IrEval.At(document, path))}");
                            break;
                        }
                }

                // What an atom means is a property of the term, so it holds of every declared document.
                foreach (var row in context.Rows(rules.Atom))
                {
                    if (graph.Predicate((ClassId)row.Keys[0]) is not SqlPredicate term || graph.DocumentPath((ClassId)row.Keys[1]) is not JsonDocumentPath path)
                        continue;

                    var atom = (PredicateAtom)row.Keys[2];
                    _report.Atoms++;
                    foreach (var document in _documents)
                    {
                        var value = IrEval.At(document, path);
                        var truth = Truth(term, document);
                        var over = atom.On(State(value));
                        if ((over & truth) == 0)
                        {
                            _report.Fail("atom", $"{term} is {truth} of {Show(value)} at {SqlValue.Spell(path)}, which its atom {atom} answers only {over}");
                            break;
                        }

                        if (truth is Truths.True or Truths.False && Admits(atom.Kept(truth == Truths.True), value) == false)
                        {
                            _report.Fail("atom", $"{term} is {truth} of {Show(value)} at {SqlValue.Spell(path)}, which its atom {atom} keeps out of {atom.Kept(truth == Truths.True)}");
                            break;
                        }
                    }
                }
            }

            /// <summary>
            /// C5: every e-node of the predicate, value and path sorts agrees, over its children's representatives, with
            /// its class's representative, on every declared document.
            /// </summary>
            void Validate(string name, Scope context, PredicateRules rules)
            {
                var graph = new ClassIndex(context, rules);
                foreach (var (id, node) in graph.Nodes)
                {
                    _report.Nodes++;
                    if (graph.IsRepresentative(id, node))
                        continue;

                    foreach (var document in _documents)
                    {
                        var (member, representative) = graph.Denote(id, node, document, _world);
                        if (member is null || representative is null || member == representative)
                            continue;

                        _report.Fail("c5", $"in {name}, {graph.Spell(node)} is {member} of {Documents.Json(document)}, and its class's {graph.Spell(graph.Representative(id))} is {representative}");
                        break;
                    }
                }
            }

            /// <summary>
            /// The theory of JSON's facts a context holds, as the facts a document is checked against.
            /// </summary>
            static IEnumerable<Apache.Calcite.Cosmos.Facts.Saturation.JsonFact> JsonFacts(Scope context, JsonRules r, ClassIndex graph)
            {
                IEnumerable<(JsonPathSpec Path, TableRow Row)> Of(TableHandle table) =>
                    context.Rows(table).Select(row => (graph.Path((ClassId)row.Keys[0]), row)).Where(x => x.Item1 is not null && x.Item1.Hidden is null)!;

                foreach (var (p, _) in Of(r.Present))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Present(p);
                foreach (var (p, _) in Of(r.Absent))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Absent(p);
                foreach (var (p, row) in Of(r.Types))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.OfType(p, (ValueSet<string>)row.Output!);
                foreach (var (p, row) in Of(r.Dom))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.OneOf(p, (ValueSet<string>)row.Output!);
                foreach (var (p, row) in Of(r.Ne))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.NotEqual(p, (string)row.Keys[1]);
                foreach (var (p, row) in Of(r.Lo))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Lower(p, (Bound)row.Output!);
                foreach (var (p, row) in Of(r.Hi))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Upper(p, (Bound)row.Output!);
                foreach (var (p, row) in Of(r.MinLength))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.MinLength(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.MaxLength))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.MaxLength(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.MinItems))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.MinItems(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.MaxItems))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.MaxItems(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.Closed))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.ClosedTo(p, (ValueSet<string>)row.Output!);
                foreach (var (p, row) in Of(r.Required))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Requires(p, (string)row.Keys[1]);
                foreach (var (p, row) in Of(r.Contains))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Holds(p, (string)row.Keys[1]);
                foreach (var (p, _) in Of(r.Unique))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.UniqueItems(p);
                foreach (var (p, row) in Of(r.MinProperties))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.MinProperties(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.MaxProperties))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.MaxProperties(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.PresentNames))
                    foreach (var named in (ValueSet<string>)row.Output!)
                        yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Present(p[named]);
                foreach (var (p, row) in Of(r.RequiredNames))
                    foreach (var named in (ValueSet<string>)row.Output!)
                        yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.Requires(p, named);
                foreach (var (p, row) in Of(r.ContainsAtLeast))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.ContainsAtLeast(p, (string)row.Keys[1], (long)row.Output!);
                foreach (var (p, row) in Of(r.ContainsAtMost))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.ContainsAtMost(p, (string)row.Keys[1], (long)row.Output!);
                foreach (var (p, row) in Of(r.NameMinLength))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.NameMinLength(p, (long)row.Output!);
                foreach (var (p, row) in Of(r.NameMaxLength))
                    yield return new Apache.Calcite.Cosmos.Facts.Saturation.JsonFact.NameMaxLength(p, (long)row.Output!);
            }

        }

        /// <summary>
        /// The kind of document a value is, as an atom's table is read by: absent, null, a scalar, or a structure.
        /// </summary>
        public static PredicateAtom.State State(J? value) => value switch
        {
            null => PredicateAtom.State.AbsentState,
            J.Null => PredicateAtom.State.NullState,
            J.Obj or J.Arr => new PredicateAtom.State.Structure(value.Type),
            _ => new PredicateAtom.State.Scalar(value.Encoded),
        };

        /// <summary>
        /// Whether what a path may hold admits a value: its kind, its domain and disequalities for a scalar, and its bounds
        /// for a number, whether a decimal holds it or not.
        /// </summary>
        public static bool Admits(PathValues possible, J? value)
        {
            switch (value)
            {
                case null:
                    return possible.Kinds.Contains(PathValues.AbsentKind);

                case J.Obj or J.Arr:
                    return possible.Kinds.Contains(value.Type) && possible.Values is null;

                case J.Huge h:
                    return possible.Kinds.Contains(value.Type)
                        && (possible.Values is null || possible.Values.Contains(value.Encoded))
                        && possible.Excluded.Contains(value.Encoded) == false
                        && (possible.Lower is not Bound l || (l.Open ? h.Value > (double)l.Value : h.Value >= (double)l.Value))
                        && (possible.Upper is not Bound u || (u.Open ? h.Value < (double)u.Value : h.Value <= (double)u.Value));

                default:
                    return possible.Admits(value.Encoded);
            }
        }

        static string Show(J? value) => value is null ? "nothing" : Documents.Json(value);

    }

}
