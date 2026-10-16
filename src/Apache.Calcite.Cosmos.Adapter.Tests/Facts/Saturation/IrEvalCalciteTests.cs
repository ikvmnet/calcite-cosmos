using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;
using Apache.Calcite.Data;

using Eggnog;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// C2, lite: the IR's evaluator held to Calcite's own runtime, on random predicates over the readings whose behaviour
    /// was measured, each compiled once and evaluated over a few hundred documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No service and no adapter</b>: a <c>CalciteConnection</c> over a <c>VALUES</c> of JSON text, the way
    /// <c>CalciteJsonValueMeasurementTests</c> measures one expression at a time. Each predicate is one statement,
    /// <c>CASE WHEN p THEN 'T' WHEN NOT p THEN 'F' ELSE 'U' END</c> over every document on which <see cref="IrEval"/>
    /// answers without reaching a raise; and a few documents it says Calcite raises on are asked one at a time, since a raise
    /// fails the statement it is in. A document on which a raise is reached but a connective decides is skipped: which one
    /// Calcite evaluates first is its own business.
    /// </para>
    /// <para>
    /// <b>What is deferred</b>: the raw readings — a promoted <c>VARIANT</c>, the split rule's <c>ANY</c>, a cast over one —
    /// and parameters. A raw value is what the adapter's reader materialises from the service's JSON, and no expression
    /// Calcite evaluates over a JSON literal produces one; the evaluator draws it from what the oracle's table admits, and
    /// holding those rows to Calcite needs the reader's materialisation, or the emulator. <see cref="LongPredicates"/>
    /// runs on a Calcite or IKVM bump.
    /// </para>
    /// </remarks>
    public sealed class IrEvalCalciteTests
    {

        /// <summary>The predicates a run of the suite compiles.</summary>
        public const int Predicates = 24;

        /// <summary>The predicates of the long run.</summary>
        public const int LongPredicates = 400;

        /// <summary>The documents each predicate is evaluated over.</summary>
        public const int DocumentCount = 200;

        /// <summary>How many documents the evaluator says raise are asked of Calcite alone, per predicate.</summary>
        const int RaisesAsked = 2;

        static readonly JsonDocumentPath[] Paths = { Declaration.A, Declaration.N, Declaration.V };

        static readonly string[] Texts = { "30", "30.0", "true", "x", "", "1e30", "1.0E30", "abc", "7", "2.5", " 30 " };

        static readonly decimal[] Numbers = { 30m, 7m, -1m, 0m, 2.5m, 10m };

        [Fact]
        public void TheEvaluatorAgreesWithCalcite() => Run(Predicates);

        [Fact(Explicit = true)]
        public void TheEvaluatorAgreesWithCalciteOverALongRun() => Run(LongPredicates);

        static void Run(int predicates)
        {
            var random = new Random(20261015);
            var world = new World(0);
            var documents = Enumerable.Range(0, DocumentCount).Select(_ => Document(random)).ToArray();
            var mismatches = new List<string>();
            int compared = 0, raised = 0, skipped = 0, statements = 0;
            var clock = Stopwatch.StartNew();

            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            for (var n = 0; n < predicates; n++)
            {
                var predicate = Predicate(random, 0);
                var truths = documents.Select(d => IrEval.Truth(predicate, d, world)).ToArray();
                var raising = documents.Select(d => IrEval.Raises(predicate, d, world)).ToArray();

                var answered = Enumerable.Range(0, documents.Length).Where(i => raising[i] == false).ToArray();
                skipped += documents.Length - answered.Length - truths.Count(t => t == Truths.Raises);

                // Every document the evaluator answers without a raise, in one statement.
                statements++;
                if (Ask(connection, predicate, answered.Select(i => documents[i]).ToArray()) is { } rows)
                {
                    for (var k = 0; k < answered.Length; k++)
                    {
                        compared++;
                        var expected = Letter(truths[answered[k]]);
                        if (rows[k] != expected)
                            mismatches.Add($"{predicate} is {rows[k]} of {Documents.Json(documents[answered[k]])} in Calcite, and {expected} to the evaluator");
                    }
                }
                else
                {
                    // Find what raised, a half at a time.
                    foreach (var i in Raising(connection, predicate, answered, documents, ref statements))
                        mismatches.Add($"{predicate} raises over {Documents.Json(documents[i])} in Calcite, and is {truths[i]} to the evaluator");
                }

                // And a few the evaluator says raise, alone.
                foreach (var i in Enumerable.Range(0, documents.Length).Where(i => truths[i] == Truths.Raises).Take(RaisesAsked))
                {
                    statements++;
                    raised++;
                    if (Ask(connection, predicate, new[] { documents[i] }) is { } answer)
                        mismatches.Add($"{predicate} is {answer[0]} of {Documents.Json(documents[i])} in Calcite, and raises to the evaluator");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{predicates} predicates, {statements} statements in {clock.Elapsed.TotalSeconds:N1} s: {compared:N0} answers compared, {raised} raises confirmed, {skipped:N0} documents skipped where a connective decided past a raise");

            if (mismatches.Count > 0)
                Assert.Fail($"{mismatches.Count} disagreements with Calcite:\n  " + string.Join("\n  ", mismatches.Take(20)));
        }

        static string Letter(Truths truth) => truth switch
        {
            Truths.True => "T",
            Truths.False => "F",
            Truths.Unknown => "U",
            _ => "R",
        };

        /// <summary>
        /// Evaluates a predicate over documents in one statement, and returns each one's truth value as a letter; or
        /// <c>null</c> where Calcite raised.
        /// </summary>
        static string[]? Ask(DbConnection connection, SqlPredicate predicate, IReadOnlyList<J.Obj> documents)
        {
            if (documents.Count == 0)
                return Array.Empty<string>();

            var p = Sql(predicate);
            var rows = string.Join(", ", documents.Select((d, i) => $"({i}, '{Documents.Json(d).Replace("'", "''")}')"));
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT k, CASE WHEN {p} THEN 'T' WHEN NOT ({p}) THEN 'F' ELSE 'U' END FROM (VALUES {rows}) AS t(k, d)";

            try
            {
                var answers = new string[documents.Count];
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    answers[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!.Trim();

                return answers;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds the documents Calcite raises on among some, by halves.
        /// </summary>
        static IEnumerable<int> Raising(DbConnection connection, SqlPredicate predicate, IReadOnlyList<int> among, IReadOnlyList<J.Obj> documents, ref int statements)
        {
            var found = new List<int>();
            var pending = new Stack<IReadOnlyList<int>>();
            pending.Push(among);
            while (pending.Count > 0 && found.Count < 3)
            {
                var part = pending.Pop();
                statements++;
                if (Ask(connection, predicate, part.Select(i => documents[i]).ToArray()) is not null)
                    continue;

                if (part.Count == 1)
                {
                    found.Add(part[0]);
                    continue;
                }

                pending.Push(part.Take(part.Count / 2).ToArray());
                pending.Push(part.Skip(part.Count / 2).ToArray());
            }

            return found;
        }

        static J.Obj Document(Random random)
        {
            var properties = new SortedDictionary<string, J>(StringComparer.Ordinal);
            J? Pick() => random.Next(8) == 0 ? null : Declaration.Values[random.Next(Declaration.Values.Length)];

            if (Pick() is J a)
                properties["a"] = a;
            if (Pick() is J n)
                properties["n"] = n;

            var v = Pick();
            if (random.Next(4) > 0)
                properties["o"] = v is null ? new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal)) : new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["v"] = v });
            else if (Pick() is J o)
                properties["o"] = o;

            return new J.Obj(properties);
        }

        static SqlPredicate Predicate(Random random, int depth)
        {
            var roll = random.Next(10);
            if (depth < 2 && roll < 2)
                return new SqlPredicate.Not(Predicate(random, depth + 1));
            if (depth < 2 && roll < 4)
            {
                var arms = Enumerable.Range(0, 2 + random.Next(2)).Select(_ => Predicate(random, depth + 1)).ToArray();
                return random.Next(2) == 0 ? SqlPredicate.AllOf(arms) : SqlPredicate.AnyOf(arms);
            }

            var path = Paths[random.Next(Paths.Length)];
            SqlValue reading = random.Next(10) switch
            {
                < 4 => new SqlValue.Text(path),
                < 7 => new SqlValue.Typed(path, new[] { "INTEGER", "BIGINT", "DOUBLE", "DECIMAL", "BOOLEAN" }[random.Next(5)]),
                _ => new SqlValue.Cast(new[] { "INTEGER", "BIGINT", "DOUBLE" }[random.Next(3)], new SqlValue.Text(path)),
            };

            SqlValue.Literal Literal() => reading switch
            {
                SqlValue.Text => SqlValue.Of(Texts[random.Next(Texts.Length)]),
                SqlValue.Typed { Type: "BOOLEAN" } => SqlValue.Of(random.Next(2) == 0),
                _ => SqlValue.Of(Numbers[random.Next(Numbers.Length)]),
            };

            return random.Next(10) switch
            {
                < 2 => new SqlPredicate.IsNull(reading),
                < 4 => new SqlPredicate.In(reading, ValueSet.From(Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Literal().Value))),
                _ => new SqlPredicate.Compare(new[] { "=", "<>", "<", "<=", ">", ">=" }[random.Next(6)], reading, Literal()),
            };
        }

        /// <summary>
        /// Writes a predicate as Calcite's SQL over a document column <c>d</c>.
        /// </summary>
        static string Sql(SqlPredicate predicate) => predicate switch
        {
            SqlPredicate.Compare c => $"({Sql(c.Left)} {c.Op} {Sql(c.Right)})",
            SqlPredicate.In i => $"({Sql(i.Value)} IN ({string.Join(", ", i.Points.Select(p => Sql(new SqlValue.Literal(p))))}))",
            SqlPredicate.IsNull n => $"({Sql(n.Value)} IS NULL)",
            SqlPredicate.Not n => $"(NOT {Sql(n.Operand)})",
            SqlPredicate.And a => "(" + string.Join(" AND ", a.Arms.Select(Sql)) + ")",
            SqlPredicate.Or o => "(" + string.Join(" OR ", o.Arms.Select(Sql)) + ")",
            _ => throw new NotSupportedException(predicate.ToString()),
        };

        static string Sql(SqlValue value) => value switch
        {
            SqlValue.Text t => $"JSON_VALUE(d, '{Path(t.Path)}')",
            SqlValue.Typed t => $"JSON_VALUE(d, '{Path(t.Path)}' RETURNING {t.Type})",
            SqlValue.Cast { Value: SqlValue.Text text } c => $"CAST(JSON_VALUE(d, '{Path(text.Path)}') AS {c.Target})",
            SqlValue.Literal l when IsString(l.Value) => "'" + StringOf(l.Value).Replace("'", "''") + "'",
            SqlValue.Literal l when TypeOf(l.Value) == BooleanType => l.Value == Of(true) ? "TRUE" : "FALSE",
            SqlValue.Literal l when TryNumberOf(l.Value, out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(value.ToString()),
        };

        static string Path(JsonDocumentPath path) => "$." + string.Join(".", path.Names);

    }

}
