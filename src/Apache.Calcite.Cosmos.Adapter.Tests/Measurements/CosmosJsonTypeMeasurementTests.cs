using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service answers about a path, and over it, for each JSON type it can hold and for
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The translator writes the <c>IS_*</c> family as guards, and a guard is only as right as what
    /// it answers over each type.</b> <c>IS_PRIMITIVE</c> is the line <c>JSON_VALUE</c> draws between a
    /// scalar and a structure (<c>CosmosRexTranslator.TryJsonValueProjection</c>); <c>IS_DEFINED</c> and
    /// <c>IS_NULL</c> are SQL's null test over a path; and
    /// <c>CosmosOperators.IsAbsenceObserving</c> claims the whole family answers a real boolean where
    /// a comparison answers undefined. That claim had been measured for <c>IS_DEFINED</c> alone. And
    /// the repository recorded two answers for <c>IS_PRIMITIVE</c> over an absent path — false in the
    /// translator, undefined in <c>DESIGN.md</c> — which is the question that settled it.
    /// </para>
    /// <para>
    /// <b>And the operators the translator writes over a rendering are measured beside it</b>: the
    /// string functions, division, the comparisons across types and <c>NOT</c> over undefined. Each
    /// is a place where the service's answer over a value of the wrong type differs from SQL's over
    /// the value's rendering, and each is a row of <c>CosmosAxioms</c>.
    /// </para>
    /// <para>
    /// <b>A row's outcomes are held by <see cref="CosmosOutcomeHarness"/>,</b> which tells undefined from
    /// false by a <c>WHERE</c> and a <c>WHERE NOT</c>, neither of which keeps it, and by the projection: an
    /// object constructor omits a property whose value is undefined, so <c>SELECT c.id, F(c.v) AS r</c>
    /// answers <c>true</c>, <c>false</c> or nothing. What is no truth is read projected here.
    /// </para>
    /// <para>
    /// Against whatever account the suite resolves — see <see cref="CosmosEmulator"/>. Each outcome
    /// names the targets that ran it.
    /// </para>
    /// </remarks>
    public class CosmosJsonTypeMeasurementTests : IClassFixture<CosmosJsonTypeMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown, driven through a fixture rather than static hooks.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_jsontype_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>
        /// One document per thing the path can hold, named for it.
        /// </summary>
        static readonly string[] Documents =
        {
            """{"id":"string","pk":"a","v":"bikes"}""",
            """{"id":"number","pk":"a","v":30}""",
            """{"id":"boolean","pk":"a","v":true}""",
            """{"id":"null","pk":"a","v":null}""",
            """{"id":"object","pk":"a","v":{"x":1}}""",
            """{"id":"array","pk":"a","v":[1]}""",
            """{"id":"absent","pk":"a"}""",
        };

        static CosmosClient? _client;
        static Container? _container;
        static string? _unavailable;
        static CosmosRequestCharges? _charges;

        static async Task Initialize()
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 5 : 30),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
                CustomHandlers = { (_charges = new(nameof(CosmosJsonTypeMeasurementTests))) },
            };

            if (CosmosEmulator.IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                if (CosmosEmulator.Endpoint is not string endpoint || CosmosEmulator.Key is not string key)
                {
                    _unavailable = "No account is reachable.";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 10 : 120));

                _client = new CosmosClient(endpoint, key, options);
                var database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                try { await database.GetContainer("kinds").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("kinds", "/pk"), cancellationToken: cts.Token)).Container;

                foreach (var json in Documents)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var response = await _container.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: cts.Token);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _container = null;
            }
        }

        static void Cleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            if (_client is not null)
                _charges?.Write();
            _client?.Dispose();
            _client = null;
        }

        /// <summary>Holds a row's outcomes against the service, over witnesses of their own.</summary>
        static Task Holds(string id) => CosmosOutcomeHarness.Holds(_container, id, _unavailable);

        /// <summary>
        /// What an expression over <c>c.v</c> answers for each document, written
        /// <c>id=answer</c> in the order of <see cref="Documents"/>: the value's JSON, or
        /// <c>undefined</c> where the projection omitted it.
        /// </summary>
        static async Task<string> Answers(string expression)
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            var answers = new Dictionary<string, string>(StringComparer.Ordinal);

            using var iterator = _container!.GetItemQueryIterator<JObject>(new QueryDefinition($"SELECT c.id, {expression} AS r FROM c"));
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    answers[(string)row["id"]!] = row.TryGetValue("r", out var r) ? r.ToString(Formatting.None) : "undefined";

            return string.Join(" ", Documents.Select(json => (string)JObject.Parse(json)["id"]!).Select(id => $"{id}={(answers.TryGetValue(id, out var answer) ? answer : "missing")}"));
        }

        /// <summary>
        /// <c>IS_PRIMITIVE</c> is true of a string, a number, a boolean and a null, and false of an
        /// object, an array and an absent path — false, not undefined.
        /// </summary>
        /// <remarks>
        /// This is the row the translator and <c>DESIGN.md</c> disagreed about. The translator's
        /// reading was right, and the guarded accessor it writes,
        /// <c>(IS_PRIMITIVE(p) ? p : null)</c>, is therefore null over an absent path rather than
        /// undefined — which is what <c>JSON_VALUE</c> answers there, and why
        /// <c>IS_DEFINED</c> over the guard is true even where the path is not.
        /// </remarks>
        [Fact, CosmosPins("Service.IsPrimitive")]
        public async Task IsPrimitiveIsFalseOverAnAbsentPath()
        {
            await Holds("Service.IsPrimitive");
            (await Answers("(IS_PRIMITIVE(c.v) ? c.v : null)")).Should().Be("string=\"bikes\" number=30 boolean=true null=null object=null array=null absent=null",
                "so the guarded accessor is null over an absent path, as JSON_VALUE is");
        }

        /// <summary>
        /// <c>IS_DEFINED</c> is false over an absent path and true over everything else, a null
        /// included.
        /// </summary>
        [Fact, CosmosPins("Service.IsDefined")]
        public async Task IsDefinedIsFalseOnlyOverAnAbsentPath()
        {
            await Holds("Service.IsDefined");
        }

        /// <summary>
        /// <c>IS_NULL</c> is true of a null alone, and false over an absent path: absence is not null
        /// to the service, which is why SQL's null test names both.
        /// </summary>
        [Fact, CosmosPins("Service.IsNull")]
        public async Task IsNullIsTrueOnlyOfANull()
        {
            await Holds("Service.IsNull");
        }

        /// <summary>
        /// Each test of a kind is true of that kind and false of everything else, an absent path
        /// included.
        /// </summary>
        [Fact, CosmosPins("Service.KindTests")]
        public async Task EachKindTestIsTrueOfItsKindAlone()
        {
            await Holds("Service.KindTests");
        }

        /// <summary>
        /// So <c>NOT</c> over any of them is a real boolean too, over an absent path as over anything
        /// else: true wherever the test is false.
        /// </summary>
        /// <remarks>
        /// That is what <c>CosmosOperators.IsAbsenceObserving</c> claims of the family: an expression
        /// built from one keeps a document without the path, where a comparison over it would not.
        /// </remarks>
        [Fact, CosmosPins("Service.NotOfAKindTest")]
        public async Task NotOfATypeTestIsTrueOverAnAbsentPath()
        {
            await Holds("Service.NotOfAKindTest");
        }

        /// <summary>
        /// The string functions are undefined over anything that is not a string: a number, a
        /// boolean, a null, a structure and nothing alike.
        /// </summary>
        /// <remarks>
        /// Calcite renders a number or a boolean as text before it folds or joins it, so
        /// <c>UPPER(JSON_VALUE(doc, '$.v'))</c> is <c>'30'</c> over a stored <c>30</c> and
        /// <c>'TRUE'</c> over a stored <c>true</c> (<c>CalciteJsonValueMeasurementTests</c>). The same
        /// function over the guarded accessor is undefined at the service there, so a case fold or a
        /// concatenation over a rendering does not mean at the service what it means in process
        /// unless the path is known to hold a string. <c>WriteLike</c> knows this; the projection
        /// and the comparison sides did not.
        /// </remarks>
        [Fact]
        public async Task TheStringFunctionsAreUndefinedOverAnythingButAString()
        {
            (await Answers("UPPER(c.v)")).Should().Be("string=\"BIKES\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("LOWER(c.v)")).Should().Be("string=\"bikes\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("CONCAT(c.v, \"!\")")).Should().Be("string=\"bikes!\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("UPPER((IS_PRIMITIVE(c.v) ? c.v : null))")).Should().Be("string=\"BIKES\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined",
                "and the guard does not change it: it renders nothing");
        }

        /// <summary>
        /// <c>/</c> divides as floating point, integers included, and is undefined over anything that
        /// is not a number.
        /// </summary>
        /// <remarks>
        /// Calcite's <c>/</c> over two integers truncates (<c>CalciteArithmeticMeasurementTests</c>),
        /// so the operator is the same symbol and a different function wherever both operands are
        /// integers.
        /// </remarks>
        [Fact]
        public async Task DivisionIsFloatingPointAndUndefinedOverAnythingButANumber()
        {
            (await Answers("c.v / 4")).Should().Be("string=undefined number=7.5 boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("7 / 2")).Should().Be("string=3.5 number=3.5 boolean=3.5 null=3.5 object=3.5 array=3.5 absent=3.5");
            (await Answers("-7 / 2")).Should().Be("string=-3.5 number=-3.5 boolean=-3.5 null=-3.5 object=-3.5 array=-3.5 absent=-3.5");
        }

        /// <summary>
        /// An ordering comparison across types is undefined, as is one over an absent path.
        /// </summary>
        [Fact, CosmosPins("Service.OrderingAcrossTypesIsUndefined")]
        public async Task AnOrderingAcrossTypesIsUndefined()
        {
            await Holds("Service.OrderingAcrossTypesIsUndefined");
        }

        /// <summary>
        /// An equality across types is false, not undefined; only over an absent path is it
        /// undefined. So <c>!=</c> is true over a value of another type and over a null — where SQL's
        /// <c>&lt;&gt;</c> over a null is unknown.
        /// </summary>
        /// <remarks>
        /// <c>CosmosBooleanLogicMeasurementTests.AComparisonIsUndefinedOnlyOverAnAbsentPath</c> says
        /// the same of a boolean in a filter; this says it of every type, read as a value. Which is
        /// why a pushed <c>&lt;&gt;</c> over a nullable path is guarded with <c>IS_DEFINED</c> and
        /// <c>NOT IS_NULL</c>, and why <c>NOT (p = x)</c> keeps a null at the service and not in
        /// process.
        /// </remarks>
        [Fact, CosmosPins("Service.EqualityAcrossTypesIsFalse"), CosmosPins("Service.InequalityOverNullIsTrue")]
        public async Task AnEqualityAcrossTypesIsFalseAndAnInequalityOverANullIsTrue()
        {
            await Holds("Service.EqualityAcrossTypesIsFalse");
            await Holds("Service.InequalityOverNullIsTrue");
        }

        /// <summary>
        /// Within a type a comparison is true or false, never undefined, as the two values stand in the type's order: strings
        /// either side of U+E000 by code point, integers either side of 2^53 + 1, a boolean against itself and the other.
        /// </summary>
        [Fact, CosmosPins("Service.ComparisonWithinAType")]
        public async Task AComparisonWithinATypeIsTrueOrFalse()
        {
            await Holds("Service.ComparisonWithinAType");
        }

        /// <summary>
        /// <c>NOT</c> over undefined is undefined: over an absent path, over a comparison across
        /// types, and over a value that is not a boolean.
        /// </summary>
        /// <remarks>
        /// SQL's <c>NOT</c> over unknown is unknown too, so the two agree wherever the service's
        /// undefined stands where SQL's unknown does — and part company wherever the service answers
        /// false where SQL answers unknown, which is the equality across types and over a null above.
        /// </remarks>
        [Fact, CosmosPins("Service.NotOverUndefinedIsUndefined")]
        public async Task NotOverUndefinedIsUndefined()
        {
            await Holds("Service.NotOverUndefinedIsUndefined");
        }

        /// <summary>
        /// The in-process bodies of the <c>IS_*</c> tests answer what the service answers over the catalogue's kinds, as
        /// <c>Sql.CosmosFunctionBodiesTests</c> asks of a real account: each over the document text the query returns, so
        /// neither side is handed a tidied input, and a test the service answers as undefined, absent from the row, false.
        /// </summary>
        [Fact, CosmosPins("Service.TypeTestBodiesMatchTheService")]
        public async Task TheTypeTestBodiesAnswerWhatTheServiceAnswersOverEveryKind()
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            var (pk, cancellation) = (Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
            var witnesses = CosmosOutcomeWitnesses.KindCells.SelectMany(kind => CosmosOutcomeWitnesses.Of(CosmosValueClass.None, kind)).ToList();
            foreach (var (json, i) in witnesses.Select((json, i) => (json, i)))
                using (var response = await _container!.CreateItemStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes($"{{\"id\":\"w{i}\",\"pk\":\"{pk}\"" + (json is null ? "" : ",\"v\":" + json) + "}")), new PartitionKey(pk), cancellationToken: cancellation))
                    response.EnsureSuccessStatusCode();

            var sql = "SELECT c AS doc, " + string.Join(", ", CosmosFunctionBodiesTests.Tests.Select(t => $"{t.Name}(c.v) AS {t.Name}")) + " FROM c";
            var (disagreements, seen) = (new List<string>(), 0);
            using var iterator = _container!.GetItemQueryStreamIterator(new QueryDefinition(sql), requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(pk) });
            while (iterator.HasMoreResults)
            {
                using var page = await iterator.ReadNextAsync(cancellation);
                using var rows = await System.Text.Json.JsonDocument.ParseAsync(page.EnsureSuccessStatusCode().Content, cancellationToken: cancellation);
                foreach (var row in rows.RootElement.GetProperty("Documents").EnumerateArray())
                {
                    seen++;
                    var document = row.GetProperty("doc").GetRawText();
                    disagreements.AddRange(from test in CosmosFunctionBodiesTests.Tests
                                           where (row.TryGetProperty(test.Name, out var answer) && answer.ValueKind == System.Text.Json.JsonValueKind.True) != test.Body(document, "$.v")
                                           select $"{test.Name} over {document}");
                }
            }

            (seen, disagreements.Count).Should().Be((witnesses.Count, 0), string.Join("; ", disagreements));
        }

        /// <summary>The target the service answering is, whose outcomes a row's test holds it to: the emulator wherever the endpoint is this machine's.</summary>
        internal static CosmosAxiomTarget Answering => Facts.Rendering.CosmosEvalServiceTests.Target == RenderingTarget.Emulator ? CosmosAxiomTarget.Emulator : CosmosAxiomTarget.Azure;

        /// <summary>A value of each kind at <c>c.v</c>, and none: true, false, null, absent, two numbers, two strings, an object and an array.</summary>
        static readonly string?[] Kinds = ["true", "false", "null", null, "30", "30.5", "\"bikes\"", "\"true\"", "{\"x\":1}", "[1]"];

        /// <summary>Asks expressions over witnesses for a row, each answer spelled as <see cref="CosmosWitnessProbe"/> spells it, the witnesses' joined by spaces.</summary>
        static async Task<string[]> Probe(string row, IReadOnlyList<string?> witnesses, params string[] expressions) =>
            [.. (await CosmosWitnessProbe.Ask(_container, _unavailable, row, witnesses, expressions)).Select(answers => string.Join(" ", answers))];

        /// <summary>
        /// The ternary and <c>IIF</c> take the then-arm where the condition is true and the else-arm over every other value,
        /// an absent path and an undefined condition included: never undefined for a defined arm. The row's outcomes are
        /// <c>c ? true : false</c> by the condition's class, which S's ternary licence reads.
        /// </summary>
        [Fact, CosmosPins("Service.TernaryTakesThenOnTrueAlone")]
        public async Task TheTernaryTakesThenOnTrueAlone()
        {
            // Every probe is asked before any answer is asserted, so a run on an account records them all.
            using var scope = new FluentAssertions.Execution.AssertionScope();
            var row = CosmosAxioms.Get("Service.TernaryTakesThenOnTrueAlone").Outcomes!.Single(o => o.Test == CosmosServiceTest.Choose).Cells.ToDictionary(c => c.Cell, c => c.Answer);
            CosmosValueClass[] classes = [CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Null, CosmosValueClass.Absent, CosmosValueClass.Number, CosmosValueClass.Number,
                CosmosValueClass.String, CosmosValueClass.String, CosmosValueClass.Object, CosmosValueClass.Array];
            string Kept(CosmosValueClass cell) => row[cell] == CosmosAnswer.True ? "true/T" : "false/F";
            (await Probe("Service.TernaryTakesThenOnTrueAlone", Kinds, "(c.v ? \"t\" : \"e\")", "IIF(c.v, \"t\", \"e\")", "(c.v ? true : false)", "(c.v ? c.v : false)")).Should().Equal(
                "\"t\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U",
                "\"t\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U \"e\"/U",
                string.Join(" ", classes.Select(Kept)),
                "true/T false/F false/F false/F false/F false/F false/F false/F false/F false/F");

            // An undefined condition: an ordering across types, over a number (CosmosOutcomeWitnesses.Side).
            (await Probe("Service.TernaryTakesThenOnTrueAlone", ["1"], "((c.v > \"\") ? true : false)")).Should().Equal(Kept(CosmosValueClass.Undefined));
        }

        /// <summary>
        /// Two paths compare as the left with the right's value written as a literal, and as the right with the left's
        /// with the comparator mirrored: each value at <c>v</c> against each at <c>w</c>, an absent path on either side, a
        /// string holding U+0000, U+FFFF or a surrogate pair among them. Within strings the stored order is the Order row's,
        /// so the literal is asked where the two differ in kind, or over a number, a boolean or a null. A side absent answers
        /// undefined, and a right side null each comparator's answer over the literal null, as the row's outcomes claim.
        /// </summary>
        [Fact, CosmosPins("Service.TwoPathsCompareAsAPathAndALiteral")]
        public async Task TwoPathsCompareAsAPathAndALiteral()
        {
            using var scope = new FluentAssertions.Execution.AssertionScope();
            const string Row = "Service.TwoPathsCompareAsAPathAndALiteral";
            CosmosAxioms.Get(Row).Outcomes!.Should().ContainSingle(o => o.Test == CosmosServiceTest.ComparePaths && o.Operand == CosmosValueClass.None, "the licence");
            (string Op, string Mirror, CosmosServiceTest Test)[] ops = [("=", "=", CosmosServiceTest.Equal), ("!=", "!=", CosmosServiceTest.NotEqual), ("<", ">", CosmosServiceTest.Less),
                ("<=", ">=", CosmosServiceTest.LessOrEqual), (">", "<", CosmosServiceTest.Greater), (">=", "<=", CosmosServiceTest.GreaterOrEqual)];
            var outcomes = CosmosAxioms.Get(Row).Outcomes!;
            string Claimed(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell) => outcomes.Single(o => o.Test == test && o.Operand == operand).Cells.Single(c => c.Cell == cell).Answer switch
            {
                CosmosAnswer.True => "true/T",
                CosmosAnswer.False => "false/F",
                _ => "undefined/U",
            };
            string?[] lefts = [.. Kinds, "2.5", "\"a\"", "\"\\u0000\"", "\"\\uFFFF\"", "\"\\uD83D\\uDE00\""];
            string?[] rights = ["null", null, "30", "\"bikes\"", "\"\\uFFFF\"", "true", "[1]", "{\"x\":1}"];
            static CosmosValueClass Class(string? json) => json switch
            {
                null => CosmosValueClass.Absent,
                "true" => CosmosValueClass.True,
                "false" => CosmosValueClass.False,
                _ => Enum.Parse<CosmosValueClass>(Kind(json), ignoreCase: true),
            };
            static string Kind(string? json) => json switch { null => "absent", "true" or "false" => "bool", _ => json[0] switch { '"' => "string", '{' => "object", '[' => "array", 'n' => "null", _ => "number" } };
            var asked = 0;
            foreach (var right in rights)
            {
                var literal = right is not null && Kind(right) is not ("object" or "array");
                var witnesses = lefts.Select(l => string.Join(",", new[] { l is null ? null : "\"v\":" + l, right is null ? null : "\"w\":" + right }.OfType<string>())).ToList();
                var expressions = ops.SelectMany(o => new[] { $"(c.v {o.Op} c.w)", $"(c.w {o.Mirror} c.v)" }.Concat(literal ? [$"(c.v {o.Op} {right})"] : [])).ToArray();
                var answers = await CosmosWitnessProbe.AskMembers(_container, _unavailable, Row, witnesses, expressions);
                var width = literal ? 3 : 2;
                for (var o = 0; o < ops.Length; o++)
                    for (var i = 0; i < lefts.Length; i++, asked++)
                    {
                        var direct = answers[o * width][i];
                        answers[o * width + 1][i].Should().Be(direct, $"{expressions[o * width + 1]} answers as {expressions[o * width]} over {witnesses[i]}");
                        if (lefts[i] is null || right is null)
                            direct.Should().Be(Claimed(CosmosServiceTest.ComparePaths, CosmosValueClass.Absent, CosmosValueClass.None), $"{expressions[o * width]} has a side absent over {witnesses[i]}");
                        else if (right == "null")
                            direct.Should().Be(Claimed(ops[o].Test, CosmosValueClass.Null, Class(lefts[i])), $"{expressions[o * width]} is the left against null over {witnesses[i]}");
                        if (literal && (Kind(lefts[i]) != Kind(right) || Kind(right) != "string"))
                            answers[o * width + 2][i].Should().Be(direct, $"{expressions[o * width + 2]} answers as {expressions[o * width]} over {witnesses[i]}");
                    }
            }

            asked.Should().Be(rights.Length * ops.Length * lefts.Length);
        }

        /// <summary>An index is an array's element from zero, and undefined over every other value, a string among them, and past the end.</summary>
        [Fact, CosmosPins("Service.IndexOverANonArrayIsUndefined")]
        public async Task AnIndexOverANonArrayIsUndefined()
        {
            (await Probe("Service.IndexOverANonArrayIsUndefined", [.. Kinds, "[1,2]", "[]", "\"ab\""], "c.v[0]", "c.v[1]", "IS_DEFINED(c.v[0])", "c.v[0] = 1")).Should().Equal(
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U 1/U 1/U undefined/U undefined/U",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U 2/U undefined/U undefined/U",
                "false/F false/F false/F false/F false/F false/F false/F false/F false/F true/T true/T false/F false/F",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U true/T true/T undefined/U undefined/U");
        }

        /// <summary><c>IN</c> is membership by the kind-strict equality, false over every value no member equals and undefined over an absent path alone.</summary>
        [Fact, CosmosPins("Service.InIsMembership")]
        public async Task InIsMembership()
        {
            (await Probe("Service.InIsMembership", Kinds, "c.v IN (\"bikes\", 30)", "c.v IN (true)", "c.v IN (null)")).Should().Equal(
                "false/F false/F false/F undefined/U true/T false/F true/T false/F false/F false/F",
                "true/T false/F false/F undefined/U false/F false/F false/F false/F false/F false/F",
                "false/F false/F true/T undefined/U false/F false/F false/F false/F false/F false/F");
        }

        /// <summary>The affixes and <c>LIKE</c> are undefined over every value that is no string, and true or false over a string.</summary>
        [Fact, CosmosPins("Service.StringPredicatesOverNonStringsAreUndefined")]
        public async Task TheStringPredicatesOverNonStringsAreUndefined()
        {
            (await Probe("Service.StringPredicatesOverNonStringsAreUndefined", Kinds, "STARTSWITH(c.v, \"b\")", "ENDSWITH(c.v, \"e\")", "CONTAINS(c.v, \"r\")", "c.v LIKE \"b%\"", "STARTSWITH(c.v, \"3\")", "c.v LIKE \"3%\"")).Should().Equal(
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U true/T false/F undefined/U undefined/U",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U false/F true/T undefined/U undefined/U",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U false/F true/T undefined/U undefined/U",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U true/T false/F undefined/U undefined/U",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U false/F false/F undefined/U undefined/U",
                "undefined/U undefined/U undefined/U undefined/U undefined/U undefined/U false/F false/F undefined/U undefined/U");
        }

        /// <summary>
        /// Each arithmetic operator by a typed name, as Calcite computes it over a value read as that type and as the
        /// service writes it; <c>/</c> twice, by two and by zero.
        /// </summary>
        internal static readonly (string Function, string Calcite, string Service)[] Arithmetic =
        [
            .. from type in new[] { "INTEGER", "BIGINT", "DECIMAL(19, 2)", "DOUBLE" }
               let x = $"CAST(s AS {type})"
               let name = type.Split('(')[0]
               from f in new[] { ("+", $"{x} + CAST(1 AS {type})", "s + 1"), ("-", $"{x} - CAST(1 AS {type})", "s - 1"), ("*", $"{x} * CAST(2 AS {type})", "s * 2"),
                                 ("/", $"{x} / CAST(2 AS {type})", "s / 2"), ("/", $"{x} / CAST(0 AS {type})", "s / 0"), ("%", $"MOD({x}, CAST(2 AS {type}))", "s % 2") }
               select (f.Item1 + ":" + name, f.Item2, f.Item3),
        ];

        /// <summary>The witnesses of the arithmetic keys: numbers at 2^31 - 1, 2^53 and 2^63 - 1, near 1e308, negative and fractional; and a value of each other kind.</summary>
        internal static readonly (CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)[] ArithmeticWitnesses =
        [
            (CosmosValueClass.Number, JsonCharacterBand.None, ["7", "-7", "2147483647", "9007199254740992", "9223372036854775807", "1e308", "0.1", "7.5"]),
            (CosmosValueClass.String, JsonCharacterBand.None, ["\"1\""]),
            (CosmosValueClass.True, JsonCharacterBand.None, ["true"]),
            (CosmosValueClass.Null, JsonCharacterBand.None, ["null"]),
            (CosmosValueClass.Absent, JsonCharacterBand.None, [null]),
            (CosmosValueClass.Object, JsonCharacterBand.None, ["{\"x\":1}"]),
            (CosmosValueClass.Array, JsonCharacterBand.None, ["[1]"]),
        ];

        /// <summary>
        /// The service adds, subtracts and multiplies integers exactly while the result fits int64, past 2^31 and 2^53,
        /// and in doubles past it, where Calcite's INTEGER and BIGINT wrap; it divides in doubles; a result JSON cannot
        /// hold, an infinity, fails the statement; and over anything but a number each is undefined. Then each typed
        /// operator against Calcite's, as the row records or better.
        /// </summary>
        [Fact, CosmosPins("Service.IntegerArithmeticIsExactWithinLong")]
        public async Task IntegerArithmeticIsExactWithinLong()
        {
            using var scope = new FluentAssertions.Execution.AssertionScope();
            const string row = "Service.IntegerArithmeticIsExactWithinLong";
            (await Probe(row, ["2147483647", "9007199254740992", "9223372036854775807", "1e308", "0.1", "\"1\"", "true", "null", null],
                "c.v - 1", "c.v * 2", "c.v + 1 = 2147483648", "c.v + 1 = 9007199254740993", "c.v + 1 > 9223372036854775807", "c.v / 0")).Should().Equal(
                "2147483646/U 9007199254740991/U 9223372036854775806/U 1E+308/U -0.9/U undefined/U undefined/U undefined/U undefined/U",
                "4294967294/U 18014398509481984/U 1.8446744073709552E+19/U !/U 0.2/U undefined/U undefined/U undefined/U undefined/U",
                "true/T false/F false/F false/F false/F undefined/U undefined/U undefined/U undefined/U",
                "false/F true/T false/F false/F false/F undefined/U undefined/U undefined/U undefined/U",
                "false/F false/F true/T true/T false/F undefined/U undefined/U undefined/U undefined/U",
                "!/U !/U !/U !/U !/U undefined/U undefined/U undefined/U undefined/U");

            var measured = await CosmosFunctionComparison.Measure(_container!, _unavailable, row, Arithmetic, ArithmeticWitnesses);
            CosmosFunctionComparison.Unsound(measured, CosmosAxioms.Get(row).FunctionOutcomes!, Answering).Should().BeEmpty();
        }

        /// <summary>A number reads back, compares and tests as the service holds it: exactly as an int64 written without a point, and written with one as a double on the emulator and as an int64 where it is one on Azure.</summary>
        [Fact, CosmosPins("Service.IntegralNumbersReadBackAsHeld")]
        public async Task IntegralNumbersReadBackAsHeld()
        {
            (await Probe("Service.IntegralNumbersReadBackAsHeld", ["30", "30.0", "3e1", "300e-1", "-0", "-0.0", "1e20", "1E21", "9007199254740993", "9007199254740993.0", "1.5e300"], "c.v", "c.v = 30", "IS_INTEGER(c.v)")).Should().Equal(
                "30/U 30/U 30/U 30/U 0/U 0/U 1E+20/U 1E+21/U 9007199254740993/U " + (Answering == CosmosAxiomTarget.Emulator ? "9007199254740992" : "9007199254740993") + "/U 1.5E+300/U",
                "true/T true/T true/T true/T false/F false/F false/F false/F false/F false/F false/F",
                "true/T true/T true/T true/T true/T true/T false/F false/F true/T true/T false/F");
        }

    }

}
