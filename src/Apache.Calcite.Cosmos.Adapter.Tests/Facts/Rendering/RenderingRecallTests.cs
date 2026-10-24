using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Recall (spec §6, g06): the eight first-family wrong answers 4b65754 sent (<c>soundness.md</c> §2), each text as
    /// that commit wrote it, replayed against the IR the loader reads at the tip and refuted at its root with the cell that
    /// shows it; offered to S as a planted template for its statement, never what S sends; and on a service the text keeps
    /// what it kept then.
    /// </summary>
    public sealed class RenderingRecallTests : IClassFixture<RenderingRecallTests.Fixture>
    {

        const string Products = "SELECT c.\"id\" FROM products AS c WHERE ", Typed = "SELECT c.\"id\" FROM typed AS c WHERE ";

        const string Defined = "(IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata))", Label = "((c.label = @p0) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label))",
            Category = "((c.category = @p1) OR IS_NULL(c.category) OR NOT IS_DEFINED(c.category))";

        const string Array = "$[\"label\"]=array ";

        internal static readonly CosmosParameter Bikes = new("@p0", "bikes");

        /// <summary>
        /// Each case: the differential's statement, the text 4b65754 sent for each conjunct (its probe's output, verbatim),
        /// the values it bound, the refutation, and the ids the service and Calcite kept.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>
        {
            ["P1"] = new(Products + "JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL", [Defined], [], M1, "$[\"metadata\"]=array (F,t)", ["2"], []),
            ["P19"] = new(Products + "JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL AND c.\"$.category\" = 'bikes'", [Defined, "(c.category = @p0)"], [Bikes], M1, "$[\"category\"]=string|bikes; $[\"metadata\"]=array (F,t)", ["2"], []),
            ["P2"] = new(Typed + "JSON_VALUE(c.\"DOC\", '$.label') IS NOT NULL", ["(IS_DEFINED(c.label) AND NOT IS_NULL(c.label))"], [], M1, Array + "(F,t)", ["1", "2", "3", "4", "5", "8", "9"], ["1", "2", "3", "8", "9"]),
            ["P3"] = new(Typed + "JSON_VALUE(c.\"DOC\", '$.label') IS NULL", ["(NOT IS_DEFINED(c.label) OR IS_NULL(c.label))"], [], M1, Array + "(T,f)", ["6", "7"], ["4", "5", "6", "7"]),
            ["P25"] = new("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" IS NOT NULL",
                ["(IS_DEFINED(c.label) AND NOT IS_NULL(c.label))"], [], M1, Array + "(F,t)", ["1", "2", "3", "4", "5", "8", "9"], ["1", "2", "3", "8", "9"]),
            ["P4"] = new(Typed + "NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes')", [$"(NOT {Label})"], [Bikes], M2, Array + "(U,t)", ["2", "3", "4", "5", "8", "9"], ["2", "3", "8", "9"]),
            ["P5"] = new(Typed + "NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' AND c.\"$.category\" = 'a')", [$"(NOT ({Label} AND {Category}))"], [Bikes, new("@p1", "a")], M2, "$[\"category\"]=string|a; " + Array + "(U,t)",
                ["2", "3", "4", "5", "6", "7", "8", "9"], ["2", "3", "6", "7", "8", "9"]),
            ["P6"] = new(Typed + "NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' OR c.\"$.category\" = 'b')", [$"(NOT ({Label} OR {Category}))"], [Bikes, new("@p1", "b")], M2, "$[\"category\"]=strings < \"b\" in [high,nonascii,nul,surrogate,trimmed]; " + Array + "(U,t)",
                ["2", "3", "4", "5", "8", "9"], ["2", "3", "8", "9"]),
        };

        public static TheoryData<string> Names => [.. Cases.Keys];

        readonly Fixture _service;

        public RenderingRecallTests(Fixture service) => _service = service;

        /// <summary>
        /// The text is refuted against the tip's IR, with the cell S names, the least refuting one by text. Offered to S as a
        /// planted template for its statement, each conjunct's text at its atoms, it is never S's best. The part a structure
        /// shows wrong is refuted as exact at the pinned cell, read at its atom. It is implied only where that cell keeps a
        /// superset of Calcite's rows: (F,t) or (U,t), (T,f) or (U,f) beneath a <c>NOT</c>, inside the Implies box; never
        /// P3's (T,f). S sends its own form, under each set a leg plans under. Until the switch the translator writes the
        /// family (g16), so under its fault it writes the text again, and unplanted it does not.
        /// </summary>
        [Theory]
        [MemberData(nameof(Names))]
        public void IsRefutedAndNeverSentInItsPlace(string p)
        {
            var c = Cases[p];
            RenderingGenerateMutationTests.Show(Replay(c.Sql, c.Texts, c.Parameters).Asserted).Should().Be(c.Refuted);
            Written(RenderingMutationTests.Checked(c.Fault, c.Sql)).Should().Equal(c.Texts);
            Written(RenderingMutationTests.Checked(None, c.Sql)).Should().NotEqual(c.Texts);
            static IEnumerable<string> Written(RenderingCheckRun run) => run.Conjuncts.Where(r => r.Site == 2).OrderBy(r => r.Unit).ThenBy(r => r.Conjunct).Select(r => r.Text);

            var (run, pieces) = Offered.Value;
            run.Failures.Should().BeEmpty();
            var (shadow, name) = (run.Shadow!, Cases.Keys.ToList().IndexOf(p).ToString(CultureInfo.InvariantCulture));
            var ids = shadow.Admissions.Where(a => a.Name == name).Select(a => a.Bundle).Concat(shadow.Weakenings.Where(w => w.Name == name).Select(w => w.Bundle ?? 0)).ToHashSet();
            var bundles = shadow.Bundles.Where(b => ids.Contains(b.Id)).ToList();
            var refuted = pieces[p].SelectMany(piece => bundles.SelectMany(b => b.Refutations ?? [])
                .Where(r => r.Node.Digest == piece.Atom.Digest && r.Position == piece.Position && r.Form.Equals(piece.Form)).Select(r => (Piece: piece, Spelled: RenderingGenerateMutationTests.Show(r.Refutation))))
                .Distinct().ToList();
            var implied = refuted.Select(r => r.Piece).Where(piece => bundles.SelectMany(b => b.Forms).Any(f => f.What == RenderingGenerateRun.Judged.Implied && f.Node.Digest == piece.Atom.Digest
                && f.Position == piece.Position && f.Form.Equals(piece.Form))).ToList();
            foreach (var piece in pieces[p])
                TestContext.Current.TestOutputHelper?.WriteLine($"{piece.Atom} at {piece.Position}: {piece.Form.Text}");
            TestContext.Current.TestOutputHelper?.WriteLine($"refuted: {string.Join("; ", refuted.Select(r => r.Spelled))}; implied: {implied.Count}");

            refuted.Should().ContainSingle("one part is wrong");
            refuted[0].Spelled.Should().Be(AtAtom(c.Refuted, refuted[0].Piece.Path, refuted[0].Piece.Position), "it is refuted at the pinned cell");
            implied.Should().OnlyContain(piece => Keeps(refuted[0].Spelled, piece.Position), "a part is implied only where its cell keeps more than Calcite does");
            if (p == "P3")
                implied.Should().BeEmpty("P3's cell drops a row Calcite keeps");

            var tip = RenderingGenerateMutationTests.Tip.Value.Triggers.First(t => t.Sql == c.Sql).Classes;
            var sent = shadow.Classes.Where(k => k.Name == name).ToList();
            sent.Select(k => (k.Set, k.Ir, k.Form?.Text)).Should().BeEquivalentTo(tip.Select(k => (k.Set, k.Ir, k.Form?.Text)), "S sends its own form");
            sent.Should().NotContain(k => k.Form != null && k.Form.Subforms.Any(f => f.Equals(refuted[0].Piece.Form) || f.Equals(ServiceForm.Strip(refuted[0].Piece.Form))),
                "nothing S sends holds the wrong part");
        }

        /// <summary>
        /// Every case's statement shadowed in a graph of its own, each conjunct's text taken apart at its atoms and offered
        /// there as a planted template: a rule proposing each part at every atom of its path at its position.
        /// </summary>
        static readonly Lazy<(RenderingCheckRun Run, IReadOnlyDictionary<string, IReadOnlyList<Piece>> Pieces)> Offered = new(() =>
        {
            var pieces = Cases.ToDictionary(c => c.Key, c => (IReadOnlyList<Piece>)[.. Pieces(c.Value)]);
            var graph = new RenderingGraph(() => ProbeRows.Measured, _ => false);
            var theory = graph.Current();
            foreach (var (path, pos, form) in pieces.Values.SelectMany(x => x).Select(x => (x.Path, x.Position, x.Form)).Distinct())
                theory.Graph.Rulesets.Single(r => r.Name == "spell").DefineRule($"planted: 4b65754's {form.Text} at {path}, {pos}", r =>
                {
                    var (e, q, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "q", "a");
                    var n = r.Var<string>("n");
                    r.When(theory.At.Of(e, pos), theory.Ir.Atom.Of(e, q, a), n.Is(theory.Spelling.Of(q)));
                    r.Where(n, x => x == path);
                    r.Then(theory.Candidate.Of(e, pos, form));
                });

            var run = RenderingCheckRun.Over(RenderingInputs.Differential("recall", [.. Cases.Values.Select(c => c.Sql)], RenderingInputs.Containers),
                shadow: new RenderingGenerateRun(graph, RenderingGenerateRun.AssertedSets));
            GC.KeepAlive(graph);
            return (run, pieces);
        });

        /// <summary>A case's texts taken apart at its conjuncts' atoms, through a <c>NOT</c> stripped and through a junction by the paths each arm reads.</summary>
        static IEnumerable<Piece> Pieces(Case c)
        {
            var statement = Statement(c.Sql, c.Texts.Length);
            var pieces = statement.Statement.Conjuncts.Zip(c.Texts, (ir, text) => Apart(ir, CosmosSqlFormReader.Read(text, c.Parameters).Form, RenderPosition.Pos)).SelectMany(x => x).ToList();
            GC.KeepAlive(statement);
            return pieces;
        }

        static IEnumerable<Piece> Apart(SqlPredicate ir, ServiceForm form, RenderPosition pos) => ir switch
        {
            SqlPredicate.Not n => Apart(n.Operand, ServiceForm.Strip(form), pos == RenderPosition.Pos ? RenderPosition.Neg : RenderPosition.Pos),
            SqlPredicate.And a => a.Arms.SelectMany(arm => Apart(arm, Part(form, arm), pos)),
            SqlPredicate.Or o => o.Arms.SelectMany(arm => Apart(arm, Part(form, arm), pos)),
            _ => [new Piece(ir, SqlValue.Spell(ir.Paths.Single()), pos, form)],
        };

        /// <summary>The members of a junction's form that read the paths an arm reads, joined as the form joins them.</summary>
        static ServiceForm Part(ServiceForm form, SqlPredicate arm)
        {
            var paths = arm.Paths.Select(SqlValue.Spell).ToHashSet();
            var members = form.Members.Where(m => m.Paths.All(paths.Contains)).ToList();
            return members.Count == 1 ? members[0] : ServiceForm.JunctionOf(form.Test, members);
        }

        /// <summary>Whether a refuting class keeps more rows than Calcite at a position: kept by the service where Calcite does not keep it.</summary>
        static bool Keeps(string refuted, RenderPosition pos) => refuted.EndsWith(pos == RenderPosition.Pos ? "(F,t)" : "(T,f)", StringComparison.Ordinal)
            || refuted.EndsWith(pos == RenderPosition.Pos ? "(U,t)" : "(U,f)", StringComparison.Ordinal);

        /// <summary>A pinned refutation read at an atom: the cell of its path, and the class swapped beneath a <c>NOT</c>.</summary>
        static string AtAtom(string pinned, string path, RenderPosition pos)
        {
            var at = pinned.LastIndexOf(" (", StringComparison.Ordinal);
            var cell = pinned[..at].Split("; ").Single(x => x.StartsWith(path + "=", StringComparison.Ordinal));
            var @class = pinned[(at + 1)..];
            return cell + " " + (pos == RenderPosition.Neg ? new string([.. @class.Select(ch => ch switch { 'T' => 'F', 'F' => 'T', 't' => 'f', 'f' => 't', _ => ch })]) : @class);
        }

        /// <summary>
        /// On a service, each text sent verbatim keeps the ids 4b65754's pushdown answered, and Calcite in process the ids it
        /// answered: the service reproduces the wrong answer the check refutes.
        /// </summary>
        [Theory]
        [MemberData(nameof(Names))]
        public async Task OnTheService(string p)
        {
            if (_service.Typed is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            var c = Cases[p];
            (await _service.Service(c.Sql.Contains("FROM products", StringComparison.Ordinal) ? _service.Products! : _service.Typed, Where(c.Texts), c.Parameters)).Should().Equal(c.Pushed);
            (await _service.Calcite(c.Sql)).Should().Equal(c.Calcite);
        }

        /// <summary>
        /// Claims a text, joined from its conjuncts' as <c>Translate</c> joins them, for the conjuncts of the statement
        /// planning read, imported as the check run imports them: from their root, <c>D0</c>. The statement is read
        /// whether the translator admits its conjuncts or not, so no fault need make it.
        /// </summary>
        internal static RenderingMemo.Entry Replay(string sql, IReadOnlyList<string> texts, IReadOnlyList<CosmosParameter> parameters, RenderingCheckRun? run = null)
        {
            var statement = Statement(sql, texts.Count);
            var root = RenderingCheckRun.RootOf([statement.ScopeForCheck])!.Value;
            var conjuncts = statement.Statement.Conjuncts;
            var claimed = CosmosSqlFormReader.Read(Where(texts), parameters);
            claimed.Written.Should().Be(Where(texts), "the text reads back as itself");
            var source = new ImportSource(statement.Statement.Digest, "declaration", 1);
            var entry = (run ?? new RenderingCheckRun()).Check(conjuncts, claimed.Form, [.. texts.Select(t => CosmosSqlFormReader.Read(t, parameters).Form)], Verdict.Equal,
                bundle => RenderingImporter.Import(bundle, source, root.Context, root.Rules, statement.DeclarationForCheck, conjuncts.SelectMany(c => c.Paths)),
                rules: statement.DeclarationForCheck.Rules).Entry;
            GC.KeepAlive(statement);
            return entry;
        }

        /// <summary>The filter statement planning a statement read whose conjuncts are as many as given.</summary>
        static CosmosFilterStatement Statement(string sql, int conjuncts)
        {
            var recorder = new RenderingRecorder();
            using (recorder.Arm())
                RenderingInputs.Differential("recall", [sql], RenderingInputs.Containers).Single().Plan();

            return recorder.Read.First(s => s.Statement.Conjuncts.Count == conjuncts);
        }

        /// <summary>Conjuncts' texts joined as <c>Translate</c> joins them.</summary>
        internal static string Where(IReadOnlyList<string> texts) => texts.Count == 1 ? texts[0] : "(" + string.Join(" AND ", texts) + ")";

        /// <summary>A recall case (<see cref="Cases"/>).</summary>
        internal sealed record Case(string Sql, string[] Texts, CosmosParameter[] Parameters, CosmosTranslatorFault Fault, string Refuted, string[] Pushed, string[] Calcite);

        /// <summary>A part of a case's text, offered at an atom of its path at a position.</summary>
        internal sealed record Piece(SqlPredicate Atom, string Path, RenderPosition Position, ServiceForm Form);

        /// <summary>A database of its own, named by the framework, holding the differential's products and typed documents.</summary>
        public class Fixture : CosmosEvalServiceTests.Fixture
        {

            public Fixture() : base("recall") { }

            protected Fixture(string name) : base(name) { }

            public Container? Products { get; private set; }

            public Container? Typed { get; private set; }

            protected override async Task Seed(Database database, CancellationToken token) =>
                (Products, Typed) = (await CosmosDifferentialTests.Seed(database, "products", CosmosDifferentialTests.Documents, token),
                    await CosmosDifferentialTests.Seed(database, "typed", CosmosDifferentialTests.TypedDocuments, token));

            /// <summary>The ids Calcite keeps for a statement over these containers, in process, nothing pushed.</summary>
            public Task<string[]> Calcite(string sql) => Calcite(sql, (CosmosDifferentialTests.Products, Products!), (CosmosDifferentialTests.Typed, Typed!));

            /// <summary>The ids Calcite keeps for a statement over containers, in process, nothing pushed.</summary>
            internal static async Task<string[]> Calcite(string sql, params (Adapter.Metadata.CosmosContainerMetadata, Container)[] containers) =>
                [.. (await CosmosDifferentialTests.Run(sql, false, null, containers, null))
                    .Select(row => Convert.ToString(row is object[] r ? r[0] : row, System.Globalization.CultureInfo.InvariantCulture)!).Order(StringComparer.Ordinal)];

            /// <summary>The ids the service keeps for a text sent verbatim as a container's <c>WHERE</c>, its values bound.</summary>
            public async Task<string[]> Service(Container container, string where, IReadOnlyList<CosmosParameter> parameters)
            {
                var ids = new List<string>();
                using var feed = container.GetItemQueryIterator<string>(parameters.Aggregate(new QueryDefinition("SELECT VALUE c.id FROM c WHERE " + where), (q, p) => q.WithParameter(p.Name, p.Value)));
                while (feed.HasMoreResults)
                    ids.AddRange(await feed.ReadNextAsync());

                return [.. ids.Order(StringComparer.Ordinal)];
            }

        }

    }

}
