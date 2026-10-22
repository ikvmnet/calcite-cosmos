using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Recall (spec §6): the eight first-family wrong answers 4b65754 sent (<c>soundness.md</c> §2), each text as that
    /// commit wrote it, replayed against the IR the loader reads at the tip and refuted at its root with the cell that
    /// shows it; with its fault planted, the tip writes the text again; and on a service the text keeps what it kept then.
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
        /// the values it bound, the fault that writes it again, the refutation, and the ids the service and Calcite kept.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>
        {
            ["P1"] = new(Products + "JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL", [Defined], [], M1, "$[\"metadata\"]=array (T,f)", ["2"], []),
            ["P19"] = new(Products + "JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL AND c.\"$.category\" = 'bikes'", [Defined, "(c.category = @p0)"], [Bikes], M1, "$[\"category\"]=string|bikes; $[\"metadata\"]=array (T,f)", ["2"], []),
            ["P2"] = new(Typed + "JSON_VALUE(c.\"DOC\", '$.label') IS NOT NULL", ["(IS_DEFINED(c.label) AND NOT IS_NULL(c.label))"], [], M1, Array + "(T,f)", ["1", "2", "3", "4", "5", "8", "9"], ["1", "2", "3", "8", "9"]),
            ["P3"] = new(Typed + "JSON_VALUE(c.\"DOC\", '$.label') IS NULL", ["(NOT IS_DEFINED(c.label) OR IS_NULL(c.label))"], [], M1, Array + "(T,f)", ["6", "7"], ["4", "5", "6", "7"]),
            ["P25"] = new("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" IS NOT NULL",
                ["(IS_DEFINED(c.label) AND NOT IS_NULL(c.label))"], [], M1, Array + "(T,f)", ["1", "2", "3", "4", "5", "8", "9"], ["1", "2", "3", "8", "9"]),
            ["P4"] = new(Typed + "NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes')", [$"(NOT {Label})"], [Bikes], M2, Array + "(U,f)", ["2", "3", "4", "5", "8", "9"], ["2", "3", "8", "9"]),
            ["P5"] = new(Typed + "NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' AND c.\"$.category\" = 'a')", [$"(NOT ({Label} AND {Category}))"], [Bikes, new("@p1", "a")], M2, "$[\"category\"]=string|a; " + Array + "(U,f)",
                ["2", "3", "4", "5", "6", "7", "8", "9"], ["2", "3", "6", "7", "8", "9"]),
            ["P6"] = new(Typed + "NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes' OR c.\"$.category\" = 'b')", [$"(NOT ({Label} OR {Category}))"], [Bikes, new("@p1", "b")], M2, "$[\"category\"]=strings < \"b\" in [high,nonascii,nul,surrogate,trimmed]; " + Array + "(U,f)",
                ["2", "3", "4", "5", "8", "9"], ["2", "3", "8", "9"]),
        };

        public static TheoryData<string> Names => [.. Cases.Keys];

        readonly Fixture _service;

        public RenderingRecallTests(Fixture service) => _service = service;

        /// <summary>
        /// The text is refuted against the tip's IR, with the cell S names, the least refuting one by text; under its fault
        /// the tip writes it again, each conjunct's text as 4b65754 wrote it; and unplanted, it does not.
        /// </summary>
        [Theory]
        [MemberData(nameof(Names))]
        public void IsRefutedAndWrittenAgainUnderItsFault(string p)
        {
            var c = Cases[p];
            RenderingMutationTests.Show(Replay(c.Sql, c.Texts, c.Parameters).Asserted).Should().Be(c.Refuted);
            Sent(RenderingMutationTests.Checked(c.Fault, c.Sql)).Should().Equal(c.Texts);
            Sent(RenderingMutationTests.Checked(None, c.Sql)).Should().NotEqual(c.Texts);

            static IEnumerable<string> Sent(RenderingCheckRun run) => run.Conjuncts.Where(r => r.Site == 2).OrderBy(r => r.Unit).ThenBy(r => r.Conjunct).Select(r => r.Text);
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
        /// Claims a text, joined from its conjuncts' as <c>Translate</c> joins them, for the conjuncts a statement's planning
        /// admitted, imported as the check run imports them: from their root, <c>D0</c> armed.
        /// </summary>
        internal static RenderingMemo.Entry Replay(string sql, IReadOnlyList<string> texts, IReadOnlyList<CosmosParameter> parameters, CosmosTranslatorFault faults = None,
            RenderingCheckRun? run = null)
        {
            var recorder = new RenderingRecorder();
            using (CosmosTranslatorFaults.Plant(faults))
            using (recorder.Arm())
            using (StatementTerms.ArmDeclaredPossible())
                RenderingInputs.Differential("recall", [sql], RenderingMutationTests.Containers).Single().Plan();

            var held = recorder.Records.Where(r => r.Site == 1).DistinctBy(r => r.Conjunct).OrderBy(r => r.Conjunct).ToList();
            held.Should().HaveCount(texts.Count, "each text is one admitted conjunct's");
            var root = RenderingCheckRun.RootOf(held.Select(r => r.Statement.ScopeForCheck).Distinct())!.Value;
            var conjuncts = held.Select(r => r.Statement.Statement.Conjuncts[r.Conjunct]).ToList();
            var claimed = CosmosSqlFormReader.Read(Where(texts), parameters);
            claimed.Written.Should().Be(Where(texts), "the text reads back as itself");
            var source = new ImportSource(held[0].Statement.Statement.Digest, "declaration", 1);
            var entry = (run ?? new RenderingCheckRun()).Check(conjuncts, claimed.Form, [.. texts.Select(t => CosmosSqlFormReader.Read(t, parameters).Form)], Verdict.Equal,
                bundle => RenderingImporter.Import(bundle, source, root.Context, root.Rules, held[0].Statement.DeclarationForCheck, conjuncts.SelectMany(c => c.Paths))).Entry;
            GC.KeepAlive(held);
            return entry;
        }

        /// <summary>Conjuncts' texts joined as <c>Translate</c> joins them.</summary>
        internal static string Where(IReadOnlyList<string> texts) => texts.Count == 1 ? texts[0] : "(" + string.Join(" AND ", texts) + ")";

        /// <summary>A recall case (<see cref="Cases"/>).</summary>
        internal sealed record Case(string Sql, string[] Texts, CosmosParameter[] Parameters, CosmosTranslatorFault Fault, string Refuted, string[] Pushed, string[] Calcite);

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
