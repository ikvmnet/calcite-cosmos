using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// Ternary logic partitioning: every row of a container makes a predicate true, false or unknown,
    /// and exactly one of them, so the rows a pushed <c>WHERE φ</c>, <c>WHERE NOT φ</c> and
    /// <c>WHERE φ IS NULL</c> keep are the container, each row once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Generated rather than written, because the written corpus had stopped finding things.</b> It
    /// recorded no divergence while ordinary statements one step away from it answered wrong rows:
    /// <c>IS NULL</c> over an accessor that reads an object, <c>NOT</c> over a text equality, a
    /// <c>CASE</c> condition under a <c>NOT</c>. Each was a shape nobody had thought to write down, and
    /// each breaks a partition. The predicates here are drawn from the shapes the adapter pushes —
    /// comparisons over accessors, promoted columns and casts, the null tests, the connectives,
    /// <c>CASE</c>, <c>COALESCE</c>, <c>LIKE</c>, the case folds, <c>IN</c>, <c>RETURNING</c> and
    /// arithmetic — over the two containers the corpus already seeds, whose documents were chosen to
    /// make those shapes disagree.
    /// </para>
    /// <para>
    /// <b>The partition needs no oracle, and the oracle is asked anyway.</b> The property is the
    /// pushdown's own and asks nothing of Calcite. But a pushdown can be wrong in a way that still
    /// partitions — an accessor that reads an object as present, by the test and by its negation
    /// alike — so each partition is compared with Calcite's too, read in process as the predicate's
    /// value per row. Both are judged by <see cref="Refines"/>, so a predicate Calcite raises over is
    /// not judged at all: a Calcite error refines to anything, and the pushdown may answer it as it
    /// likes.
    /// </para>
    /// <para>
    /// <b>A failure names the smallest part of the predicate that still fails</b>, found by asking each
    /// operand in turn and descending into the first that fails. Its witness is usually a single
    /// comparison, which is what a fix is written against.
    /// </para>
    /// </remarks>
    public partial class CosmosDifferentialTests
    {

        /// <summary>
        /// How many predicates <see cref="EveryPredicatePartitionsTheRows"/> generates, alternating
        /// between the two containers: <see cref="GatePartitionedPredicates"/> where
        /// <c>COSMOS_PARTITION_GATE=1</c>, and <see cref="DefaultPartitionedPredicates"/> otherwise.
        /// </summary>
        /// <remarks>
        /// Each costs four statements: the three partitions pushed, and Calcite's reading of the
        /// predicate in process. See the test's remarks for what a run of each size costs.
        /// </remarks>
        internal static int PartitionedPredicates => Environment.GetEnvironmentVariable("COSMOS_PARTITION_GATE") == "1"
            ? GatePartitionedPredicates
            : DefaultPartitionedPredicates;

        /// <summary>
        /// The full run, which is Stage 0's gate: ternary logic partitioning holds over 1,000 generated
        /// predicates.
        /// </summary>
        const int GatePartitionedPredicates = 1_000;

        /// <summary>
        /// The run every pass of the class makes, sized for a pull request rather than for the gate.
        /// </summary>
        /// <remarks>
        /// The first of the gate's predicates, not others: the generator draws them in order from one
        /// seed, so this run asks exactly what the full run asks first, and a predicate failing here
        /// fails there too, as the same statement. The size was measured; see <c>DESIGN.md</c> under
        /// <em>Differential testing</em>.
        /// </remarks>
        internal const int DefaultPartitionedPredicates = 200;

        /// <summary>
        /// The generator's seed, fixed so that a failure names a predicate the next run asks again, and
        /// so that the default run is a prefix of the gate's.
        /// </summary>
        const int PartitionSeed = 20_261_015;

        /// <summary>
        /// How many predicates are judged at once.
        /// </summary>
        /// <remarks>
        /// Planning is the cost, and each statement plans on one thread, so the predicates are spread
        /// over the cores. Measured on a workstation that other work already held at full load, four at
        /// a time was no faster than one, and gave the same answers; a runner with idle cores is where
        /// it can help.
        /// </remarks>
        static int PartitionParallelism => Math.Max(1, Environment.ProcessorCount / 2);

        /// <summary>
        /// What a generated predicate may say about one container: the expressions it reads, and the
        /// constants it compares them with.
        /// </summary>
        /// <param name="Table">The table the container is registered as.</param>
        /// <param name="Ids">Every document's id, which is what a partition is checked against.</param>
        /// <param name="Text">Expressions SQL types as text.</param>
        /// <param name="Numbers">Expressions SQL types as a number. Each must be one Calcite evaluates
        /// over every document without raising, or the predicate is not judged at all.</param>
        /// <param name="TextLiterals">Text the containers hold, spelled as they are and as they
        /// render, case-folded and concatenated.</param>
        /// <param name="NumberLiterals">Numbers the containers hold, and numbers between them.</param>
        /// <param name="Patterns">LIKE patterns, prefixes and otherwise.</param>
        sealed record Vocabulary(string Table, string[] Ids, string[] Text, string[] Numbers, string[] TextLiterals, string[] NumberLiterals, string[] Patterns);

        /// <summary>
        /// The container whose <c>label</c> holds a different JSON type in every document, which is
        /// where an accessor's rendering and the raw value part company.
        /// </summary>
        /// <remarks>
        /// Numbers come from <c>price</c> only, and only through a cast: it holds <c>30</c>,
        /// <c>"30"</c> and <c>30.7</c>, which a cast reads and a <c>RETURNING</c> refuses, so a
        /// <c>RETURNING</c> here would make Calcite raise over every predicate that names it. The label
        /// is listed twice to be asked about twice as often.
        /// </remarks>
        static Vocabulary TypedVocabulary() => new(
            "typed",
            Ids(TypedDocuments),
            [
                "JSON_VALUE(c.\"DOC\", '$.label')",
                "JSON_VALUE(c.\"DOC\", '$.label')",
                "JSON_VALUE(c.\"DOC\", '$.price')",
                "JSON_VALUE(c.\"DOC\", '$.name')",
                "JSON_VALUE(c.\"DOC\", '$.big')",
                "JSON_VALUE(c.\"DOC\", '$.category')",
                "CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR)",
                "CAST(c.\"$.category\" AS VARCHAR)",
                "c.\"id\"",
            ],
            [
                "CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE)",
                "CAST(CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) AS INTEGER)",
            ],
            ["bikes", "shoes", "huge", "30", "true", "a", "b", "BIKES", "TRUE", "none", "1", "9", "bikes!", "30!", "Exact", "1.0E30"],
            ["0", "7", "8", "9", "10", "30", "31"],
            ["bi%", "%e%", "3%", "t%", "%s", "_ike_", "B%", "%!"]);

        /// <summary>
        /// The container whose paths are null in one document and absent in another, with an object
        /// and an array among them.
        /// </summary>
        /// <remarks>
        /// Every <c>price</c> here is an integer or nothing, so <c>RETURNING INTEGER</c> answers over
        /// all of them, and its arithmetic is integer arithmetic in Calcite.
        /// </remarks>
        static Vocabulary ProductsVocabulary() => new(
            "products",
            Ids(Documents),
            [
                "JSON_VALUE(c.\"DOC\", '$.name')",
                "JSON_VALUE(c.\"DOC\", '$.category')",
                "JSON_VALUE(c.\"DOC\", '$.price')",
                "JSON_VALUE(c.\"DOC\", '$.metadata')",
                "JSON_VALUE(c.\"DOC\", '$.metadata.sku')",
                "JSON_VALUE(c.\"DOC\", '$.tags')",
                "JSON_VALUE(c.\"DOC\", '$.tags[0]')",
                "CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR)",
                "CAST(c.\"$.category\" AS VARCHAR)",
                "c.\"id\"",
            ],
            [
                "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER)",
                "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER)",
                "CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE)",
                "(JSON_VALUE(c.\"DOC\", '$.price') + 1)",
            ],
            ["bikes", "shoes", "Sprint", "Road Runner", "B-2", "outdoor", "BIKES", "SPRINT", "none", "1", "6", "120", "Sprint!"],
            ["0", "2", "5", "10", "50", "80", "100", "120", "340"],
            ["S%", "%Runner%", "_prin_", "%e%", "b%", "B%", "%a%"]);

        static string[] Ids(string[] documents)
        {
            return documents.Select(json => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!).ToArray();
        }

        /// <summary>
        /// A generated predicate, kept as a tree so that a failure can be narrowed to an operand.
        /// </summary>
        abstract record Predicate
        {

            /// <summary>The predicate as SQL, parenthesised so that it composes as written.</summary>
            public abstract string Sql { get; }

            /// <summary>The predicates this one is built from, the first place to look for a smaller
            /// witness.</summary>
            public virtual IEnumerable<Predicate> Operands => [];

            public sealed override string ToString() => Sql;

        }

        /// <summary>A comparison, a null test, a <c>LIKE</c> or an <c>IN</c> over one expression.</summary>
        sealed record Atom(string Text) : Predicate
        {
            public override string Sql => Text;
        }

        sealed record Negation(Predicate Operand) : Predicate
        {
            public override string Sql => $"NOT ({Operand.Sql})";
            public override IEnumerable<Predicate> Operands => [Operand];
        }

        sealed record Junction(Predicate Left, string Connective, Predicate Right) : Predicate
        {
            public override string Sql => $"({Left.Sql} {Connective} {Right.Sql})";
            public override IEnumerable<Predicate> Operands => [Left, Right];
        }

        sealed record Choice(Predicate When, Predicate Then, Predicate Else) : Predicate
        {
            public override string Sql => $"(CASE WHEN {When.Sql} THEN {Then.Sql} ELSE {Else.Sql} END)";
            public override IEnumerable<Predicate> Operands => [When, Then, Else];
        }

        sealed record Fallback(Predicate Operand, bool Value) : Predicate
        {
            public override string Sql => $"COALESCE({Operand.Sql}, {(Value ? "TRUE" : "FALSE")})";
            public override IEnumerable<Predicate> Operands => [Operand];
        }

        /// <summary>A test of a predicate's truth value: <c>IS NULL</c>, <c>IS NOT TRUE</c> and the rest.</summary>
        sealed record Test(Predicate Operand, string Is) : Predicate
        {
            public override string Sql => $"(({Operand.Sql}) IS {Is})";
            public override IEnumerable<Predicate> Operands => [Operand];
        }

        /// <summary>
        /// Draws predicates over a <see cref="Vocabulary"/>, shallow more often than deep, at most
        /// <paramref name="depth"/> connectives deep.
        /// </summary>
        /// <remarks>
        /// The <c>ORDER BY</c> yardstick draws its filters here too, one connective deep: a sort is
        /// pushed only above a filter the service answers whole, and a deeper predicate rarely is.
        /// </remarks>
        sealed class PredicateGenerator(Random random, int depth = PredicateGenerator.Depth)
        {

            /// <summary>
            /// How many connectives deep a predicate goes. Two reaches a <c>CASE</c> under a
            /// <c>NOT</c> and a null test over a conjunction, which is where the defects so far have
            /// been. At three a predicate took twice as long on average, and its slowest statements
            /// several seconds each to implement.
            /// </summary>
            internal const int Depth = 2;

            static readonly string[] Comparisons = ["=", "<>", "<", ">"];

            static readonly string[] Tests = ["NULL", "NOT NULL", "TRUE", "NOT TRUE", "FALSE", "NOT FALSE"];

            public Predicate Next(Vocabulary vocabulary) => Draw(vocabulary, 0);

            T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];

            Predicate Draw(Vocabulary v, int level)
            {
                if (level < depth && random.NextDouble() < 0.7 - 0.25 * level)
                {
                    return random.Next(14) switch
                    {
                        0 or 1 or 2 => new Negation(Draw(v, level + 1)),
                        3 or 4 or 5 => new Junction(Draw(v, level + 1), "AND", Draw(v, level + 1)),
                        6 or 7 or 8 => new Junction(Draw(v, level + 1), "OR", Draw(v, level + 1)),
                        9 or 10 => new Choice(Draw(v, level + 1), Draw(v, level + 1), Draw(v, level + 1)),
                        11 => new Fallback(Draw(v, level + 1), random.Next(2) == 0),
                        12 => new Test(Draw(v, level + 1), random.Next(2) == 0 ? "NULL" : "NOT NULL"),
                        _ => new Test(Draw(v, level + 1), Pick(Tests)),
                    };
                }

                return Leaf(v);
            }

            Atom Leaf(Vocabulary v)
            {
                var not = random.Next(3) == 0 ? "NOT " : "";

                return new Atom(random.Next(16) switch
                {
                    0 or 1 or 2 or 3 or 4 => $"{Text(v)} {Pick(Comparisons)} {Quote(Pick(v.TextLiterals))}",
                    5 or 6 or 7 or 8 => Number(v, n => $"{n.Sql} {Pick(Comparisons)} {n.Literal(Pick(v.NumberLiterals))}"),
                    9 => $"c.\"$.category\" {(random.Next(2) == 0 ? "=" : "<>")} {Quote(Pick(v.TextLiterals))}",
                    10 or 11 => random.Next(3) switch
                    {
                        0 => $"{Text(v)} IS {not}NULL",
                        1 => Number(v, n => $"{n.Sql} IS {not}NULL"),
                        _ => $"c.\"$.category\" IS {not}NULL",
                    },
                    12 or 13 => $"{Text(v)} {not}LIKE {Quote(Pick(v.Patterns))}",
                    14 => $"{Text(v)} {not}IN ({Quote(Pick(v.TextLiterals))}, {Quote(Pick(v.TextLiterals))})",
                    _ => random.Next(2) == 0
                        ? $"c.\"$.category\" {not}IN ({Quote(Pick(v.TextLiterals))}, {Quote(Pick(v.TextLiterals))})"
                        : Number(v, n => $"{n.Sql} {not}IN ({n.Literal(Pick(v.NumberLiterals))}, {n.Literal(Pick(v.NumberLiterals))})"),
                });
            }

            /// <summary>
            /// A text expression: an accessor, a column or a cast, and sometimes a case fold, a
            /// fallback or a concatenation over it.
            /// </summary>
            string Text(Vocabulary v)
            {
                var text = Pick(v.Text);

                return random.Next(10) switch
                {
                    0 => $"UPPER({text})",
                    1 => $"LOWER({text})",
                    2 => $"COALESCE({text}, 'none')",
                    3 => $"({text} || '!')",
                    _ => text,
                };
            }

            /// <summary>
            /// A numeric expression, and sometimes arithmetic over it, division by an integer among it,
            /// handed to <paramref name="compare"/> with the spelling its constants take.
            /// </summary>
            /// <remarks>
            /// A constant compared with a <c>DOUBLE</c> is written as one, <c>50E0</c> rather than
            /// <c>50</c>. They mean the same, and Calcite reduces the second to the first. But it
            /// reduces a literal's cast by compiling it with Janino, and under IKVM that compile was
            /// most of what a slow statement cost. Over a hundred predicates, a statement reading a
            /// <c>DOUBLE</c> cast took 744 ms on average against 156 ms for one without, and spelling
            /// the constants as doubles halved the run. The integer spelling still occurs wherever the
            /// expression is an integer.
            /// </remarks>
            string Number(Vocabulary v, Func<Numeric, string> compare)
            {
                var number = Pick(v.Numbers);
                var numeric = new Numeric(number, number.Contains("AS DOUBLE)") && number.Contains("AS INTEGER)") == false);
                var by = numeric.Literal(random.Next(1, 4).ToString(System.Globalization.CultureInfo.InvariantCulture));

                return compare(random.Next(8) switch
                {
                    0 => numeric with { Sql = $"({number} + {by})" },
                    1 => numeric with { Sql = $"({number} - {by})" },
                    2 => numeric with { Sql = $"({number} * {by})" },
                    3 => numeric with { Sql = $"({number} / {by})" },
                    _ => numeric,
                });
            }

            /// <summary>
            /// A numeric expression and whether SQL types it <c>DOUBLE</c>.
            /// </summary>
            internal readonly record struct Numeric(string Sql, bool Double)
            {
                public string Literal(string digits) => Double ? digits + "E0" : digits;
            }

            static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

        }

        /// <summary>
        /// The judgement of one predicate over one container.
        /// </summary>
        /// <param name="Vocabulary">The container it was asked of.</param>
        /// <param name="Predicate">The predicate.</param>
        /// <param name="Judged">Whether it was judged: <c>false</c> where Calcite raised, and anything
        /// is a refinement of that.</param>
        /// <param name="Failures">What is wrong, empty where nothing is.</param>
        /// <param name="Detail">The partitions both ways and the statements pushed, for the report.</param>
        sealed record Partitioning(Vocabulary Vocabulary, Predicate Predicate, bool Judged, IReadOnlyList<string> Failures, string Detail)
        {
            public bool Failed => Failures.Count > 0;
        }

        /// <summary>
        /// The first <paramref name="count"/> predicates the generator draws from <see cref="PartitionSeed"/>, alternating
        /// between the two containers.
        /// </summary>
        static List<(Vocabulary Vocabulary, Predicate Predicate)> PartitionedPredicatesOf(int count)
        {
            var typed = TypedVocabulary();
            var products = ProductsVocabulary();

            var generator = new PredicateGenerator(new Random(PartitionSeed));
            return Enumerable.Range(0, count)
                .Select(i => i % 2 == 0 ? typed : products)
                .Select(v => (v, generator.Next(v)))
                .ToList();
        }

        /// <summary>
        /// The three statements that partition a container by a predicate, each with the truth value of the rows it keeps.
        /// </summary>
        static (string Name, string Sql, string Truth)[] PartitionsOf(Vocabulary v, Predicate predicate)
        {
            var where = $"SELECT c.\"id\" FROM {v.Table} AS c WHERE ";
            return
            [
                ("φ", where + predicate.Sql, "true"),
                ("NOT φ", where + $"NOT ({predicate.Sql})", "false"),
                ("φ IS NULL", where + $"({predicate.Sql}) IS NULL", "null"),
            ];
        }

        /// <summary>
        /// Runs and judges predicates, asking each statement of each once however often it comes up —
        /// the generator repeats itself, and narrowing a failure asks again of the parts.
        /// </summary>
        sealed class PartitionJudge
        {

            readonly ConcurrentDictionary<string, Lazy<Task<(Outcome Outcome, IReadOnlyList<string> Sent)>>> _statements = new();
            readonly ConcurrentDictionary<string, Lazy<Task<Partitioning>>> _predicates = new();

            readonly ConcurrentBag<(double Milliseconds, string Sql)> _timings = new();

            public int Statements => _statements.Count;

            /// <summary>Each statement's time, slowest first.</summary>
            public IReadOnlyList<(double Milliseconds, string Sql)> Timings => _timings.OrderByDescending(t => t.Milliseconds).ToList();

            Task<(Outcome Outcome, IReadOnlyList<string> Sent)> Execute(string sql, bool pushdown)
            {
                return _statements.GetOrAdd((pushdown ? "pushed " : "oracle ") + sql, key => new(async () =>
                {
                    var sent = new List<string>();
                    var clock = Stopwatch.StartNew();
                    var outcome = await Attempt(sql, pushdown, sent);
                    _timings.Add((clock.Elapsed.TotalMilliseconds, key));
                    return (outcome, (IReadOnlyList<string>)sent);
                })).Value;
            }

            public Task<Partitioning> Judge(Vocabulary v, Predicate predicate)
            {
                return _predicates.GetOrAdd(v.Table + " " + predicate.Sql, _ => new(() => JudgeCore(v, predicate))).Value;
            }

            async Task<Partitioning> JudgeCore(Vocabulary v, Predicate predicate)
            {
                var partitions = PartitionsOf(v, predicate);

                // Calcite's answer for every row at once: the predicate as a column, in process. One
                // predicate a statement: a projection of many costs more than they do apart, measured at
                // 25 to a statement as minutes against seconds, the reductions Calcite attempts over a
                // projection growing faster than its width.
                var (oracle, _) = await Execute($"SELECT c.\"id\", {predicate.Sql} FROM {v.Table} AS c", pushdown: false);

                var pushed = new (Outcome Outcome, IReadOnlyList<string> Sent)[partitions.Length];
                for (var i = 0; i < partitions.Length; i++)
                    pushed[i] = await Execute(partitions[i].Sql, pushdown: true);

                var detail = new StringBuilder();
                for (var i = 0; i < partitions.Length; i++)
                {
                    detail.Append($"    pushed {partitions[i].Name}: ");
                    detail.AppendLine(pushed[i].Outcome.Raised is Exception e ? "raised " + e.Message.Split('\n')[0] : "[" + string.Join(", ", pushed[i].Outcome.Rows!) + "]");
                    foreach (var sent in pushed[i].Sent)
                        detail.AppendLine("      sent: " + sent);
                }

                if (oracle.Raised is Exception raised)
                {
                    detail.AppendLine("    oracle: raised " + raised.Message.Split('\n')[0]);
                    return new Partitioning(v, predicate, false, [], detail.ToString());
                }

                // Each row is ("id", true|false|null), and the canonical text of the id is what a
                // partition's rows are.
                var truth = oracle.Rows!.Select(row => Regex.Match(row, "^\\((\"[^\"]*\"), (true|false|null)\\)$")).ToList();
                if (truth.Any(match => match.Success == false))
                    throw new InvalidOperationException("The oracle answered a row that is not an id and a truth value: " + string.Join("; ", oracle.Rows!));

                var failures = new List<string>();

                for (var i = 0; i < partitions.Length; i++)
                {
                    var expected = new Outcome(truth.Where(m => m.Groups[2].Value == partitions[i].Truth).Select(m => m.Groups[1].Value).ToList(), null);
                    detail.AppendLine($"    oracle {partitions[i].Name}: [{string.Join(", ", expected.Rows!)}]");

                    if (Refines(partitions[i].Sql, false, expected, pushed[i].Outcome) is string failure)
                        failures.Add($"{partitions[i].Name} differs from the oracle: " + failure.Replace("\n", "\n    "));
                }

                // The partition proper, which asks nothing of Calcite: every id exactly once.
                if (pushed.All(p => p.Outcome.Raised is null))
                {
                    var seen = pushed.SelectMany(p => p.Outcome.Rows!).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                    var missing = v.Ids.Select(id => "\"" + id + "\"").Where(id => seen.ContainsKey(id) == false).ToList();
                    var repeated = seen.Where(e => e.Value > 1).Select(e => e.Key).ToList();

                    if (missing.Count > 0 || repeated.Count > 0)
                        failures.Insert(0, $"the three do not partition the {v.Ids.Length} rows: in none [{string.Join(", ", missing)}], in more than one [{string.Join(", ", repeated)}]");
                }

                return new Partitioning(v, predicate, true, failures, detail.ToString());
            }

            /// <summary>
            /// The smallest part of a failing predicate that fails too: the first operand that does,
            /// narrowed in its turn, or the predicate itself where none does.
            /// </summary>
            public async Task<Partitioning> Narrow(Partitioning failing)
            {
                foreach (var operand in failing.Predicate.Operands)
                    if (await Judge(failing.Vocabulary, operand) is { Failed: true } smaller)
                        return await Narrow(smaller);

                return failing;
            }

        }

        /// <summary>
        /// Every generated predicate's three pushed partitions are the container's rows, each once, and
        /// each is the partition Calcite computes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What a run costs.</b> Measured against the emulator with this seed, at both sizes: see
        /// <c>DESIGN.md</c> under <em>Differential testing</em>. Repeated statements run once, and the
        /// predicates are judged <see cref="PartitionParallelism"/> at a time. Every run of the class
        /// pays for <see cref="DefaultPartitionedPredicates"/>; <c>COSMOS_PARTITION_GATE=1</c> asks
        /// for the gate's <see cref="GatePartitionedPredicates"/>, of which those are the first.
        /// </para>
        /// <para>
        /// <c>COSMOS_MEASUREMENT_LOG</c> names a file to append every failure to in full, with the
        /// statements each partition sent, and the run's counts and time.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task EveryPredicatePartitionsTheRows()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var predicates = PartitionedPredicatesOf(PartitionedPredicates);

            var judge = new PartitionJudge();
            var judged = new Partitioning[predicates.Count];
            var clock = Stopwatch.StartNew();

            var done = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, predicates.Count), new ParallelOptions { MaxDegreeOfParallelism = PartitionParallelism }, async (i, _) =>
            {
                judged[i] = await judge.Judge(predicates[i].Vocabulary, predicates[i].Predicate);

                if (Interlocked.Increment(ref done) % 100 == 0)
                    Log($"judged {done} of {predicates.Count} in {clock.Elapsed.TotalSeconds:F0} s, {judged.Count(p => p is { Failed: true })} failing");
            });

            var judging = clock.Elapsed;

            var failing = judged.Where(p => p.Failed).ToList();
            var narrowed = new Partitioning[failing.Count];

            await Parallel.ForEachAsync(Enumerable.Range(0, failing.Count), new ParallelOptions { MaxDegreeOfParallelism = PartitionParallelism }, async (i, _) =>
            {
                narrowed[i] = await judge.Narrow(failing[i]);
            });

            var size = predicates.Count == GatePartitionedPredicates ? "the gate's" : "the first of the gate's " + GatePartitionedPredicates + "; COSMOS_PARTITION_GATE=1 asks for all of them";
            var summary = $"{predicates.Count} predicates ({size}), {predicates.Select(p => p.Vocabulary.Table + " " + p.Predicate.Sql).Distinct().Count()} distinct, " +
                $"{judged.Count(p => p.Judged == false)} not judged because Calcite raised, {failing.Count} failing; " +
                $"{judge.Statements} statements run; judged in {judging.TotalSeconds:F1} s, narrowed in {(clock.Elapsed - judging).TotalSeconds:F1} s " +
                $"at {PartitionParallelism} at a time";

            // One entry per smallest witness, most predicates first: many failing predicates narrow to
            // the same comparison, and the witness is what a fix is written against.
            var witnesses = Enumerable.Range(0, failing.Count)
                .GroupBy(i => narrowed[i].Vocabulary.Table + ": " + narrowed[i].Predicate.Sql)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"over {g.Key}\n  the smallest failing part of {g.Count()} predicate(s), among them {failing[g.First()].Predicate.Sql}\n  " +
                    string.Join("\n  ", narrowed[g.First()].Failures))
                .ToList();

            Log("## EveryPredicatePartitionsTheRows: " + summary);
            var timings = judge.Timings;
            Log($"statement time: median {timings[timings.Count / 2].Milliseconds:F0} ms, p90 {timings[timings.Count / 10].Milliseconds:F0} ms, slowest:");
            foreach (var (ms, sql) in timings.Take(10))
                Log($"  {ms:F0} ms  {sql}");
            for (var i = 0; i < failing.Count; i++)
            {
                Log($"### over {failing[i].Vocabulary.Table}: {failing[i].Predicate.Sql}");
                Log($"  smallest: {narrowed[i].Predicate.Sql}");
                foreach (var failure in narrowed[i].Failures)
                    Log("  " + failure);
                Log(narrowed[i].Detail);
            }

            witnesses.Should().BeEmpty($"every predicate's pushed partitions must be the rows, each once, as Calcite partitions them ({summary}; {witnesses.Count} distinct smallest failing parts)");
        }

        /// <summary>
        /// Appends a line to the file <c>COSMOS_MEASUREMENT_LOG</c> names, where it names one. The
        /// test platform keeps nothing a passing test writes.
        /// </summary>
        static void Log(string line)
        {
            var log = Environment.GetEnvironmentVariable("COSMOS_MEASUREMENT_LOG");
            if (string.IsNullOrEmpty(log) == false)
                lock (typeof(CosmosDifferentialTests))
                    System.IO.File.AppendAllText(log, line + Environment.NewLine);
        }

    }

}
