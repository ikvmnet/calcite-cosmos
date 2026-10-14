using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Facts;

using Eggnog;

using EggnogDerive;

// What the adapter does today, against Eggnog, timed. Three engines answer the same derivations:
//
//   horn     JsonFactTheory.Derive, as the planner calls it now.
//   flat     Eggnog with no contexts: a fresh graph per derivation, holding the theory, the container's
//            rules and the query's facts, saturated and asked.
//   layered  Eggnog with contexts: JSON's rules and the service's facts in a frozen root; a container's
//            rules in a frozen child, one Eggnog rule per compiled rule; each query a child of that,
//            saturated, asked and disposed.
//   merged   layered, with rules of identical bodies merged into one rule of several heads, as the
//            plan of record's §2.5 does.
//   data     the same contexts, with a container's rules as rows of the frozen child, read by a fixed
//            set of rules in the root (JsonFactData).
//
// Each derivation is followed by the questions consumers ask of a JsonFactSet. Usage:
//   dotnet run -c Release                 every size
//   dotnet run -c Release -- 3000 30000   only these sizes (target rule counts)

var only = args.Where(a => a.StartsWith("--")).ToHashSet();
var sizes = args.Where(a => a.StartsWith("--") == false).Select(int.Parse).DefaultIfEmpty(-1).ToArray();
if (sizes is [-1])
    sizes = new[] { 0, 300, 3_000, 10_000, 30_000, 60_000 };
var flatLimit = 3_000;
var layeredLimit = only.Contains("--no-layered") ? -1 : 35_000;

Console.WriteLine($"Eggnog {typeof(EGraph).Assembly.GetName().Version}, .NET {Environment.Version}, {Environment.ProcessorCount} cores");
Console.WriteLine();

// The service's guarantees, as CosmosServiceFacts states them: every container has these.
var service = new List<JsonFactRule>();
foreach (var (name, type) in new[] { ("id", JsonType.String), ("_ts", JsonType.Integer), ("_etag", JsonType.String) })
{
    var path = JsonDocumentPath.Root.Property(name);
    service.Add(JsonFactRule.Unconditional(new JsonFact(path, new JsonClaim.OfType(type))));
    service.Add(JsonFactRule.Unconditional(new JsonFact(path, new JsonClaim.Present())));
}

// The root the layered engine shares across every container: built once per process.
var rootWatch = Stopwatch.StartNew();
var graph = new EGraph();
var tables = new JsonFactTables(graph);
JsonFactProgram.Declare(tables, graph.Root, service, local: false);
graph.Root.Run(tables.Saturation);
graph.Root.Freeze();
Console.WriteLine($"layered root (JSON rules, service facts), built and frozen once: {rootWatch.Elapsed.TotalMilliseconds:F1} ms");

var mergedGraph = new EGraph();
var mergedTables = new JsonFactTables(mergedGraph);
JsonFactProgram.Declare(mergedTables, mergedGraph.Root, service, local: false);
mergedGraph.Root.Run(mergedTables.Saturation);
mergedGraph.Root.Freeze();

rootWatch.Restart();
var dataGraph = new EGraph();
var data = new JsonFactData(dataGraph);
data.Declare(dataGraph.Root, service);
dataGraph.Root.Run(data.Tables.Saturation);
dataGraph.Root.Freeze();
Console.WriteLine($"data root (JSON rules, the rule interpreter, service facts), built and frozen once: {rootWatch.Elapsed.TotalMilliseconds:F1} ms");
Console.WriteLine();

foreach (var corpus in sizes.SelectMany(target => target == 0 ? new[] { Corpus.Items(), Corpus.Gap2(), Corpus.Structure() } : new[] { Corpus.Synthetic(target) }))
{

    var compile = Stopwatch.StartNew();
    var schemaRules = JsonSchemaFacts.ReadFrom(JsonNode.Parse(corpus.Schema)!, Corpus.Recognisers);
    var readMs = compile.Elapsed.TotalMilliseconds;
    var rules = service.Concat(schemaRules).ToList();
    var conditional = rules.Count(r => r.Body.Count > 0);

    Console.WriteLine($"## {corpus.Name}: {rules.Count:N0} compiled rules ({conditional:N0} conditional), JSON Schema read in {readMs:F1} ms");

    // Container registration: what each engine does once per container.
    compile.Restart();
    var horn = new JsonFactTheory(rules);
    horn.Derive(null);
    var hornCompileMs = compile.Elapsed.TotalMilliseconds;

    Console.WriteLine($"   register   horn     {hornCompileMs,9:F1} ms");

    Scope? schema = null;
    if (rules.Count <= layeredLimit)
    {
        compile.Restart();
        schema = graph.Root.CreateChild();
        JsonFactProgram.Declare(tables, schema, schemaRules, local: true);
        var schemaReport = schema.Run(tables.Saturation);
        var runMs = compile.Elapsed.TotalMilliseconds;
        schema.Freeze();
        Console.WriteLine($"   register   layered  {compile.Elapsed.TotalMilliseconds,9:F1} ms (saturate {runMs:F1} ms in {schemaReport.Iterations} iterations, then freeze)");
    }

    compile.Restart();
    var merged = mergedGraph.Root.CreateChild();
    JsonFactProgram.Declare(mergedTables, merged, schemaRules, local: true, merge: true);
    var mergedReport = merged.Run(mergedTables.Saturation);
    var mergedRunMs = compile.Elapsed.TotalMilliseconds;
    merged.Freeze();
    Console.WriteLine($"   register   merged   {compile.Elapsed.TotalMilliseconds,9:F1} ms (saturate {mergedRunMs:F1} ms in {mergedReport.Iterations} iterations, then freeze; {merged.LocalRules.Count:N0} Eggnog rules)");

    compile.Restart();
    var dataSchema = dataGraph.Root.CreateChild();
    data.Declare(dataSchema, schemaRules);
    var dataReport = dataSchema.Run(data.Tables.Saturation);
    var dataRunMs = compile.Elapsed.TotalMilliseconds;
    dataSchema.Freeze();
    Console.WriteLine($"   register   data     {compile.Elapsed.TotalMilliseconds,9:F1} ms (saturate {dataRunMs:F1} ms in {dataReport.Iterations} iterations, then freeze)");

    // Parity first, so that a fast answer is known to be the same answer.
    if (schema is not null)
    {
        var (agree, eggnogOnly, hornOnly) = Parity(corpus, rules, horn, tables, schema);
        Console.WriteLine($"   parity     layered: {agree:N0} answers agree; Eggnog alone knows {eggnogOnly:N0}; Horn alone knows {hornOnly:N0}");
    }
    {
        var (agree, eggnogOnly, hornOnly) = Parity(corpus, rules, horn, mergedTables, merged);
        Console.WriteLine($"   parity     merged:  {agree:N0} answers agree; Eggnog alone knows {eggnogOnly:N0}; Horn alone knows {hornOnly:N0}");
    }
    {
        var (agree, eggnogOnly, hornOnly) = Parity(corpus, rules, horn, data.Tables, dataSchema);
        Console.WriteLine($"   parity     data:    {agree:N0} answers agree; Eggnog alone knows {eggnogOnly:N0}; Horn alone knows {hornOnly:N0}");
    }

    foreach (var query in corpus.Queries)
    {
        var line = $"   {query.Name,-22} horn {Bench.Measure(() => AskHorn(horn, query))} | data {Bench.Measure(() => AskLayered(data.Tables, dataSchema, query))} | merged {Bench.Measure(() => AskLayered(mergedTables, merged, query))}";

        if (schema is not null)
            line += $" | layered {Bench.Measure(() => AskLayered(tables, schema, query))}";

        if (rules.Count <= flatLimit)
            line += $" | flat {Bench.Measure(() => AskFlat(rules, query))}";

        Console.WriteLine(line);
    }

    Console.WriteLine();
}

int AskHorn(JsonFactTheory horn, Derivation query)
{
    var set = horn.Derive(query.Established.Count == 0 ? null : query.Established);
    if (set.IsContradictory)
        return -1;

    var known = 0;
    foreach (var probe in query.Probes)
        if (set.Knows(probe))
            known++;

    return known;
}

int AskLayered(JsonFactTables t, Scope schema, Derivation query)
{
    // The outright facts are the frozen schema scope itself: nothing to run.
    if (query.Established.Count == 0)
        return Answer(t, schema, query);

    using var scope = schema.CreateChild();
    JsonFactProgram.Establish(t, scope, query.Established);
    scope.Run(t.Saturation);
    return Answer(t, scope, query);
}

int AskFlat(IReadOnlyList<JsonFactRule> rules, Derivation query)
{
    var g = new EGraph();
    var t = new JsonFactTables(g);
    JsonFactProgram.Declare(t, g.Root, rules, local: false);
    JsonFactProgram.Establish(t, g.Root, query.Established);
    g.Root.Run(t.Saturation);

    if (g.Root.IsContradictory)
        return -1;

    var known = 0;
    foreach (var probe in query.Probes)
        if (JsonFactProgram.Knows(t, g.Root, probe))
            known++;

    return known;
}

int Answer(JsonFactTables t, Scope scope, Derivation query)
{
    if (scope.IsContradictory)
        return -1;

    var known = 0;
    foreach (var probe in query.Probes)
        if (JsonFactProgram.Knows(t, scope, probe))
            known++;

    return known;
}

(int Agree, int EggnogOnly, int HornOnly) Parity(Corpus corpus, List<JsonFactRule> rules, JsonFactTheory horn, JsonFactTables tables, Scope schema)
{
    // Every head the theory can conclude, and every fact a query states, asked of both engines.
    var probes = rules.Select(r => r.Head).Concat(corpus.Queries.SelectMany(q => q.Established.Concat(q.Probes))).Distinct().ToList();
    if (probes.Count > 4_000)
        probes = probes.Where((_, i) => i % (probes.Count / 4_000 + 1) == 0).ToList();

    int agree = 0, eggnogOnly = 0, hornOnly = 0;
    foreach (var query in corpus.Queries)
    {
        var set = horn.Derive(query.Established.Count == 0 ? null : query.Established);
        using var scope = schema.CreateChild();
        JsonFactProgram.Establish(tables, scope, query.Established);
        scope.Run(tables.Saturation);

        if (set.IsContradictory || scope.IsContradictory)
        {
            if (set.IsContradictory == scope.IsContradictory)
                agree++;
            else if (scope.IsContradictory)
            {
                eggnogOnly++;
                Console.WriteLine($"   + Eggnog alone finds {query.Name} contradictory: no document satisfies it");
            }
            else
                hornOnly++;

            continue;
        }

        foreach (var probe in probes)
        {
            var h = set.Knows(probe);
            var e = JsonFactProgram.Knows(tables, scope, probe);
            if (h == e)
                agree++;
            else if (e)
            {
                eggnogOnly++;
                if (eggnogOnly <= 5)
                    Console.WriteLine($"   + Eggnog alone knows {probe.Path} {probe.Claim} under {query.Name}");
            }
            else
            {
                hornOnly++;
                if (hornOnly <= 5)
                    Console.WriteLine($"   ! Horn alone knows {probe.Path} {probe.Claim} under {query.Name}");
            }
        }
    }

    return (agree, eggnogOnly, hornOnly);
}

namespace EggnogDerive
{

    sealed record Derivation(string Name, IReadOnlyList<JsonFact> Established, IReadOnlyList<JsonFact> Probes);

    sealed record Corpus(string Name, string Schema, IReadOnlyList<Derivation> Queries)
    {

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

        sealed record Form(string Name) : IJsonStoredForm;

        static readonly Form UuidLower = new("uuid-lower");

        /// <summary>The adapter's recogniser, reduced to the one form the corpora use.</summary>
        public static readonly JsonSchemaRecognisers Recognisers = new(StringPattern: pattern => pattern == Uuid ? UuidLower : null);

        static JsonDocumentPath P(string name) => JsonDocumentPath.Root.Property(name);

        static JsonFact Fact(string path, JsonClaim claim) => new(P(path), claim);

        /// <summary>The schema of <c>CosmosDeclaredFactPlanningTests</c>: two kinds, one declaring a canonical UUID.</summary>
        public static Corpus Items() => new(
            "items (the planning tests' schema)",
            $$"""
            {
              "oneOf": [
                { "properties": { "kind": { "const": "A" } } },
                { "properties": { "kind": { "const": "B" },
                                  "ref": { "type": "string", "pattern": "{{Uuid}}" } } }
              ]
            }
            """,
            new[]
            {
                new Derivation("outright", Array.Empty<JsonFact>(), Probes()),
                new Derivation("kind = 'B'", new[] { Fact("kind", new JsonClaim.EqualTo("B")) }, Probes()),
                new Derivation("kind = 'C' (none)", new[] { Fact("kind", new JsonClaim.EqualTo("C")) }, Probes()),
            });

        /// <summary>
        /// Gap 2 of the integration plan: kind is required and one of order or invoice, and each kind declares
        /// what ref holds. A query refusing one kind proves the other, which no single fact states.
        /// </summary>
        public static Corpus Gap2() => new(
            "gap 2 (a domain less a refused value)",
            $$"""
            {
              "required": ["kind"],
              "properties": { "kind": { "enum": ["order", "invoice"] } },
              "oneOf": [
                { "properties": { "kind": { "const": "order" },
                                  "ref": { "type": "string", "pattern": "{{Uuid}}" } } },
                { "properties": { "kind": { "const": "invoice" },
                                  "ref": { "type": "integer" } } }
              ]
            }
            """,
            new[]
            {
                new Derivation("outright", Array.Empty<JsonFact>(), Probes()),
                new Derivation("kind = 'order'", new[] { Fact("kind", new JsonClaim.EqualTo("order")) }, Probes()),
                new Derivation("kind <> 'invoice'", new[] { Fact("kind", new JsonClaim.NotEqualTo("invoice")) }, Probes()),
            });

        /// <summary>
        /// The rules of JSON: data is declared a string, and the query asks about a property of it. An
        /// accessor's comparison proves its path is there — what the plan's F4 adds to the extractor — so a
        /// present data.id makes data a present object, which no string is.
        /// </summary>
        public static Corpus Structure() => new(
            "JSON's own rules (data is a string)",
            """
            { "properties": { "data": { "type": "string" }, "meta": { "type": "object" } } }
            """,
            new[]
            {
                new Derivation("outright", Array.Empty<JsonFact>(), StructureProbes()),
                new Derivation("data.id = 'x'", new[] { Fact2("data", "id", new JsonClaim.EqualTo("x")), Fact2("data", "id", new JsonClaim.Present()) }, StructureProbes()),
                new Derivation("meta.v = 'x'", new[] { Fact2("meta", "v", new JsonClaim.EqualTo("x")), Fact2("meta", "v", new JsonClaim.Present()) }, StructureProbes()),
            });

        static JsonFact Fact2(string parent, string child, JsonClaim claim) => new(P(parent).Property(child), claim);

        static JsonFact[] StructureProbes() => new[]
        {
            Fact("meta", new JsonClaim.Present()),
            Fact("meta", new JsonClaim.OfType(JsonType.Object)),
            Fact("data", new JsonClaim.Present()),
        };

        static JsonFact[] Probes() => new[]
        {
            Fact("ref", new JsonClaim.Represents(UuidLower)),
            Fact("ref", new JsonClaim.OfType(JsonType.String, OrNull: true)),
            Fact("id", new JsonClaim.Present()),
            Fact("kind", new JsonClaim.Present()),
        };

        /// <summary>
        /// A container holding many kinds of document, discriminated by <c>kind</c>, each declaring its own
        /// typed properties: the shape a container of many kinds takes when every kind is described. The
        /// property names are shared across kinds with types that differ by kind, so the discriminator guards
        /// nearly every rule.
        /// </summary>
        public static Corpus Synthetic(int targetRules)
        {
            // About 1.4 compiled rules per declared property (a type or form, and a third are required).
            var properties = Math.Max(10, (int)Math.Sqrt(targetRules / 1.4 * 2));
            var kinds = Math.Max(2, (int)(targetRules / 1.4 / properties));

            var branches = new JsonArray();
            for (var k = 0; k < kinds; k++)
            {
                var props = new JsonObject { ["kind"] = new JsonObject { ["const"] = $"k{k}" } };
                var required = new JsonArray();
                for (var j = 0; j < properties; j++)
                {
                    props[$"p{j}"] = ((j + k) % 5) switch
                    {
                        0 => new JsonObject { ["type"] = "string" },
                        1 => new JsonObject { ["type"] = "integer" },
                        2 => new JsonObject { ["type"] = "number" },
                        3 => new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        _ => new JsonObject { ["enum"] = new JsonArray("a", "b", "c") },
                    };
                    if ((j + k) % 3 == 0)
                        required.Add($"p{j}");
                }

                branches.Add(new JsonObject { ["properties"] = props, ["required"] = required });
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("kind"),
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["type"] = "string" } },
                ["oneOf"] = branches,
            };

            var probes = new[]
            {
                Fact("p3", new JsonClaim.Represents(UuidLower)),
                Fact("p0", new JsonClaim.OfType(JsonType.String, OrNull: true)),
                Fact("p0", new JsonClaim.Present()),
                Fact("id", new JsonClaim.Present()),
                Fact("kind", new JsonClaim.Present()),
            };

            return new($"synthetic, {kinds} kinds x {properties} properties", schema.ToJsonString(), new[]
            {
                new Derivation("outright", Array.Empty<JsonFact>(), probes),
                new Derivation("kind = 'k0'", new[] { Fact("kind", new JsonClaim.EqualTo("k0")) }, probes),
                new Derivation("kind = 'k0', p4 = 'a'", new[] { Fact("kind", new JsonClaim.EqualTo("k0")), Fact("p4", new JsonClaim.EqualTo("a")) }, probes),
                new Derivation("kind = 'nope' (none)", new[] { Fact("kind", new JsonClaim.EqualTo("nope")) }, probes),
            });
        }

    }

    static class Bench
    {

        /// <summary>
        /// The median time of one call, over batches sized to take about 20 ms, after a warm-up; and the
        /// bytes one call allocates.
        /// </summary>
        public static string Measure(Func<int> call)
        {
            var sink = 0;
            var warm = Stopwatch.StartNew();
            var calls = 0;
            while (warm.ElapsedMilliseconds < 200 || calls < 3)
            {
                sink += call();
                calls++;
            }

            var perCall = warm.Elapsed.TotalMilliseconds / calls;
            var batch = Math.Max(1, (int)(20 / Math.Max(perCall, 1e-6)));
            var batches = perCall > 500 ? 3 : 15;
            var samples = new List<double>();
            long allocated = 0;

            for (var b = 0; b < batches; b++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < batch; i++)
                    sink += call();

                samples.Add(watch.Elapsed.TotalMilliseconds * 1000 / batch);
                allocated += (GC.GetAllocatedBytesForCurrentThread() - before) / batch;
            }

            GC.KeepAlive(sink);
            samples.Sort();
            return $"{Format(samples[samples.Count / 2]),9} {allocated / batches / 1024.0,8:F1} KB";
        }

        static string Format(double micros) =>
            micros >= 1000 ? $"{micros / 1000:F2} ms" : micros >= 1 ? $"{micros:F1} µs" : $"{micros * 1000:F0} ns";

    }

}
