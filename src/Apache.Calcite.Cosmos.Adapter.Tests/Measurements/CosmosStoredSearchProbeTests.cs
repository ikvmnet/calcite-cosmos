using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// <c>REPLACE</c>'s search read from a path, <c>REPLACE("a0b1c", c.v, "x")</c>, over the six witnesses probe run 3 left
    /// unanswered twice (503) on the probe account, <c>"a0b1c"</c>, <c>"b"</c>, the empty string, U+0000 before a
    /// <c>1</c>, 30 and 0.5, each asked alone in a statement of its own; and <c>LENGTH(c.v)</c> over <c>"a0b1c"</c>,
    /// which run 3 left unanswered too and run 1 answered 5, asked before each of them and after the last. Where the
    /// length is answered beside a search that is not, the search fails every time; where neither is, the service was
    /// busy. Each is a line of <c>Service.StringFunctionAnswersByBand</c>, spelled and logged as its own test's are
    /// (<see cref="CosmosWitnessProbe.ProjectAlone"/>), so a record of them reads with run 3's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each line is written beside Calcite's answer in process and the verdict of each key its witness alone measures
    /// (<see cref="CosmosFunctionComparison.Judge"/>), unmeasured where the service left it unanswered, to
    /// <c>TestResults/measurements/alone-answers.tsv</c>; the keys over every witness together to
    /// <c>function-answers.tsv</c>, as the row's own test writes them; every try to <c>tries.tsv</c>. A search left
    /// unanswered every time between two lengths answered is <see cref="CosmosWitnessProbe.Failed"/>, which fails its key.
    /// </para>
    /// <para>
    /// Explicit, as a probe of one account's open question is: CI's ordinary run never asks it, and a run names it with
    /// <c>-explicit only</c>. One container in a database of 400 RU/s, holding the six witnesses alone, made when the test
    /// first asks for it, so a run that does not ask, or reaches no service, makes nothing, and deleted when the class
    /// ends: none of the row's own class's setup, which was 660.82 of run 3's 1,576.17 RU. <c>COSMOS_RU_CAP</c>, where
    /// set, refuses every request but a delete once the class's charges pass it (<see cref="CosmosRequestCharges"/>).
    /// Written for probe run 4 (2026-11-09).
    /// </para>
    /// </remarks>
    public class CosmosStoredSearchProbeTests : IClassFixture<CosmosStoredSearchProbeTests.Fixture>
    {

        /// <summary>
        /// The class's teardown; its setup is the test's first asking (<see cref="Searched"/>), since xUnit makes a class's
        /// fixture whether or not an explicit test runs.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public ValueTask InitializeAsync() => default;

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_search_" + Regex.Replace(RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>The row whose lines these are.</summary>
        const string Row = "Service.StringFunctionAnswersByBand";

        /// <summary>The search read from a path, as the row's spelling sends it.</summary>
        const string Search = "REPLACE(\"a0b1c\", c.v, \"x\")";

        /// <summary>The length beside it, as the row's three counts send it.</summary>
        const string Length = "LENGTH(c.v)";

        /// <summary>How many times a statement the service leaves unanswered is asked in all.</summary>
        const int Tries = 5;

        /// <summary>The first pause before a statement left unanswered is asked again; each after it is twice the one before.</summary>
        static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

        /// <summary>The witnesses run 3 left the search unanswered over, spelled as the row's own are.</summary>
        static readonly string[] Asked = [Json("a0b1c"), Json("b"), Json(""), Json("\u00001"), "30", "0.5"];

        /// <summary>The row's keys of the asked witnesses, each with the ones it holds, in the row's order.</summary>
        static readonly (CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)[] Keys =
            [.. from key in CosmosStringFunctionMeasurementTests.FunctionWitnesses
                let held = key.Witnesses.Where(w => Asked.Contains(w)).ToArray()
                where held.Length > 0
                select (key.Kind, key.Band, held)];

        static Task? _asked;
        static CosmosClient? _client;
        static Database? _made;
        static Container? _container;
        static string? _unavailable;
        static CosmosRequestCharges? _charges;

        /// <summary>The container, its database made at the first asking; <c>null</c> where no service made it.</summary>
        static async Task<Container?> Searched()
        {
            await (_asked ??= Initialize());
            return _container;
        }

        static async Task Initialize()
        {
            var loopback = CosmosEmulator.Endpoint is not null && CosmosEmulator.ServiceTarget == Apache.Calcite.Cosmos.Adapter.Metadata.CosmosServiceTarget.Emulator;
            var cap = double.TryParse(Environment.GetEnvironmentVariable("COSMOS_RU_CAP"), NumberStyles.Float, CultureInfo.InvariantCulture, out var capped) ? capped : double.PositiveInfinity;
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(loopback ? 10 : 60),
                LimitToEndpoint = loopback,
                CustomHandlers = { (_charges = new(nameof(CosmosStoredSearchProbeTests), cap)) },
            };

            if (CosmosEmulator.IsEmulator)
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;

            try
            {
                if (CosmosEmulator.Endpoint is not string endpoint || CosmosEmulator.Key is not string key)
                {
                    _unavailable = "No account is reachable.";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(loopback ? 10 : 120));
                _client = new CosmosClient(endpoint, key, options);

                try
                {
                    _made = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, 400, cancellationToken: cts.Token)).Database;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.BadRequest)
                {
                    // A serverless account takes no throughput.
                    _made = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;
                }

                _container = (await _made.CreateContainerIfNotExistsAsync(new ContainerProperties("search", "/pk"), cancellationToken: cts.Token)).Container;
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _container = null;
            }
        }

        /// <summary>
        /// Deletes the database the class made, made or half made, and writes what the class was charged: a run that never
        /// asked for one, or reached no service, made none and deletes nothing.
        /// </summary>
        static void Cleanup()
        {
            try { _made?.DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }
            if (_client is not null)
                _charges?.Write();

            _client?.Dispose();
            (_asked, _client, _made, _container) = (null, null, null, null);
        }

        /// <summary>
        /// The search over each witness alone and the length over <c>"a0b1c"</c> beside each, every line written beside
        /// Calcite's answer and its verdict; the release a committed emulator record names answers each line as recorded.
        /// </summary>
        [Fact(Explicit = true), Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins(Row)]
        public async Task ASearchReadFromAPathIsAskedOfEachWitnessAlone()
        {
            string?[] witnesses = [.. Keys.SelectMany(k => k.Witnesses)];
            witnesses.Should().Equal(Asked, "each witness is the row's own, so its line reads with run 3's");

            // The length over "a0b1c" before each search and after the last, the search over one witness at a time.
            var statements = Enumerable.Range(0, witnesses.Length).SelectMany(i => new[] { (0, 0), (1, i) }).Append((0, 0)).ToList();
            var answers = await CosmosWitnessProbe.ProjectAlone(await Searched(), _unavailable, Row, witnesses, [Length, Search], statements, Tries, Pause);
            answers[0][0].Should().NotBeNull();
            answers[1].Should().NotContainNulls();

            var lines = Lines(Spelled(Length), [(CosmosValueClass.String, JsonCharacterBand.None, [witnesses[0]])], [answers[0][0]])
                .Concat(Lines(Spelled(Search), Keys, answers[1])).ToList();

            // Each key over every witness together, as the row's own test judges it: none of a spelling left unanswered.
            var measured = CosmosFunctionComparison.Judge(Row, Spelled(Length), [(CosmosValueClass.String, JsonCharacterBand.None, [witnesses[0]])], [[answers[0][0]]])
                .Concat(CosmosFunctionComparison.Judge(Row, Spelled(Search), Keys, [answers[1]])).ToList();

            lock (Written)
            {
                var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "alone-answers.tsv"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, lines);
                File.AppendAllLines(Path.Combine(Path.GetDirectoryName(path)!, "function-answers.tsv"), measured.Select(m => string.Join('\t', CosmosWitnessProbe.Release, Row, m.Key, m.Value)));
            }
        }

        static readonly object Written = new();

        /// <summary>The row's spellings the service writes as one, which the probe asks once.</summary>
        static CosmosFunctionSpelling[] Spelled(string service) =>
            [.. CosmosStringFunctionMeasurementTests.Functions.Where(f => CosmosFunctionComparison.Spelled(f) == service)];

        /// <summary>
        /// Each witness's line, as the probe logged it, beside Calcite's answer under each spelling and reader, written once
        /// where all agree, and the verdict of each key the witness alone measures.
        /// </summary>
        static IEnumerable<string> Lines(CosmosFunctionSpelling[] functions, IReadOnlyList<(CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)> keys, string[] service)
        {
            var w = 0;
            foreach (var (kind, band, witnesses) in keys)
            {
                foreach (var witness in witnesses)
                {
                    var answer = service[w++];
                    var calcite = (from f in functions from c in CosmosFunctionComparison.CalciteAnswers(Row, f, [witness]) select (f.Calcite, c.Reader, Answer: c.Answers[0])).ToList();
                    var verdicts = CosmosFunctionComparison.Judge(Row, functions, [(kind, band, [witness])], [[answer]]);
                    yield return string.Join('\t', CosmosWitnessProbe.Release, Row, CosmosFunctionComparison.Spelled(functions[0]), witness, answer,
                        calcite.Select(c => c.Answer).Distinct().Count() == 1 ? calcite[0].Answer : string.Join("; ", calcite.Select(c => $"{c.Calcite} via {c.Reader}: {c.Answer}")),
                        verdicts.Count == 0 ? "unmeasured" : string.Join("; ", verdicts.Select(v => $"{v.Key}={v.Value}")));
                }
            }
        }

        static string Json(string text) => Newtonsoft.Json.JsonConvert.ToString(text);

    }

}
