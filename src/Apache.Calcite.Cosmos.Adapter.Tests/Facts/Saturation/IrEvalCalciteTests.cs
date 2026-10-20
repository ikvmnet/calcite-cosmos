using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;
using Apache.Calcite.Cosmos.Adapter.Tests.Measurements;

using Eggnog;

using org.apache.calcite.sql.type;

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
    /// <b>No service</b>: a <c>CalciteConnection</c> over a table of JSON documents, each beside the columns the adapter's
    /// reader fills from it (<c>MaterialisedTable</c>). Each predicate is one statement over every document on which
    /// <see cref="IrEval"/> answers without reaching a raise; and a few documents it says Calcite raises on are asked one at
    /// a time, since a raise fails the statement it is in. A document on which a raise is reached but a connective decides
    /// is skipped: Calcite raises there (<c>Calcite.ConnectivesAreKleene</c>).
    /// </para>
    /// <para>
    /// <b>A predicate's typed and raw readings are read one way each</b>, drawn with it: <c>typed</c> through
    /// <c>RETURNING</c> or a column, <c>raw</c> through a <c>VARIANT</c>, an <c>ANY</c> or a <c>VARCHAR</c> column, against
    /// the literals the loader reads beside each. A cast over a raw value and parameters are deferred: neither was measured.
    /// <see cref="LongPredicates"/> runs on a Calcite or IKVM bump.
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
            var columns = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var clock = Stopwatch.StartNew();

            using var connection = MaterialisedTable.Open();

            for (var n = 0; n < predicates; n++)
            {
                // A VARIANT comparison takes a second or more to compile, so it is drawn a quarter of the time.
                var typed = random.Next(2) == 0 ? IrEval.TypedAs.Returning : IrEval.TypedAs.Column;
                var raw = random.Next(4) switch { 0 => IrEval.RawAs.Variant, 3 => IrEval.RawAs.Varchar, _ => IrEval.RawAs.Any };
                var drawn = new Drawn(typed, raw);
                var predicate = Predicate(random, 0, drawn, random.Next(3));
                foreach (var column in predicate.Subterms.SelectMany(Readings).Select(r => Column(r, drawn)).OfType<(JsonDocumentPath Path, string Type)>())
                    columns[column.Type] = columns.GetValueOrDefault(column.Type) + 1;
                var truths = documents.Select(d => IrEval.Truth(predicate, d, world, typed, raw)).ToArray();
                var raising = documents.Select(d => IrEval.Raises(predicate, d, world, typed, raw)).ToArray();

                var answered = Enumerable.Range(0, documents.Length).Where(i => raising[i] == false).ToArray();
                skipped += documents.Length - answered.Length - truths.Count(t => t == Truths.Raises);

                // Every document the evaluator answers without a raise, in one statement.
                statements++;
                if (Ask(connection, predicate, drawn, answered.Select(i => documents[i]).ToArray()) is { } rows)
                {
                    for (var k = 0; k < answered.Length; k++)
                    {
                        compared++;
                        var expected = Letter(truths[answered[k]]);
                        if (rows[k] != expected)
                            mismatches.Add($"{predicate} ({drawn}) is {rows[k]} of {Documents.Json(documents[answered[k]])} in Calcite, and {expected} to the evaluator");
                    }
                }
                else
                {
                    // Find what raised, a half at a time.
                    foreach (var i in Raising(connection, predicate, drawn, answered, documents, ref statements))
                        mismatches.Add($"{predicate} ({drawn}) raises over {Documents.Json(documents[i])} in Calcite, and is {truths[i]} to the evaluator");
                }

                // And a few the evaluator says raise, alone.
                foreach (var i in Enumerable.Range(0, documents.Length).Where(i => truths[i] == Truths.Raises).Take(RaisesAsked))
                {
                    statements++;
                    raised++;
                    if (Ask(connection, predicate, drawn, new[] { documents[i] }) is { } answer)
                        mismatches.Add($"{predicate} ({drawn}) is {answer[0]} of {Documents.Json(documents[i])} in Calcite, and raises to the evaluator");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{predicates} predicates, {statements} statements in {clock.Elapsed.TotalSeconds:N1} s: {compared:N0} answers compared, {raised} raises confirmed, {skipped:N0} documents skipped where a connective decided past a raise; readings of a column: {string.Join(", ", columns.Select(c => c.Key + " " + c.Value))}");

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
        static string[]? Ask(DbConnection connection, SqlPredicate predicate, Drawn drawn, IReadOnlyList<J.Obj> documents)
        {
            if (documents.Count == 0)
                return Array.Empty<string>();

            var columns = predicate.Subterms.SelectMany(Readings).Select(r => Column(r, drawn)).OfType<(JsonDocumentPath Path, string Type)>().Distinct().ToArray();
            var table = new MaterialisedTable(
                columns.Select(c => (Name(c), SqlTypeName.valueOf(c.Type))).ToArray(),
                documents.Select(d => (Documents.Json(d), columns.Select(c => IrEval.At(d, c.Path) is J value ? Documents.Json(value) : null).ToArray())).ToArray());

            try
            {
                return table.Ask(connection, Sql(predicate, drawn))[0].Select(c => c.ToString()).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The way a predicate's typed and raw readings reach Calcite.</summary>
        sealed record Drawn(IrEval.TypedAs Typed, IrEval.RawAs Raw);

        static IEnumerable<SqlValue> Readings(SqlPredicate predicate) => predicate switch
        {
            SqlPredicate.Compare c => new[] { c.Left },
            SqlPredicate.In i => new[] { i.Value },
            SqlPredicate.IsNull n => new[] { n.Value },
            _ => Array.Empty<SqlValue>(),
        };

        /// <summary>The column a reading is read from, or <c>null</c> where it is read off the document.</summary>
        static (JsonDocumentPath Path, string Type)? Column(SqlValue reading, Drawn drawn) => reading switch
        {
            SqlValue.Typed t when drawn.Typed == IrEval.TypedAs.Column => (t.Path, t.Type),
            SqlValue.Raw r => (r.Path, drawn.Raw.ToString().ToUpperInvariant()),
            _ => null,
        };

        static string Name((JsonDocumentPath Path, string Type) column) => string.Join(".", column.Path.Names) + ":" + column.Type;

        /// <summary>
        /// Finds the documents Calcite raises on among some, by halves.
        /// </summary>
        static IEnumerable<int> Raising(DbConnection connection, SqlPredicate predicate, Drawn drawn, IReadOnlyList<int> among, IReadOnlyList<J.Obj> documents, ref int statements)
        {
            var found = new List<int>();
            var pending = new Stack<IReadOnlyList<int>>();
            pending.Push(among);
            while (pending.Count > 0 && found.Count < 3)
            {
                var part = pending.Pop();
                statements++;
                if (Ask(connection, predicate, drawn, part.Select(i => documents[i]).ToArray()) is not null)
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

        static SqlPredicate Predicate(Random random, int depth, Drawn drawn, int kind)
        {
            var roll = random.Next(10);
            if (depth < 2 && roll < 2)
                return new SqlPredicate.Not(Predicate(random, depth + 1, drawn, kind));
            if (depth < 2 && roll < 4)
            {
                var arms = Enumerable.Range(0, 2 + random.Next(2)).Select(_ => Predicate(random, depth + 1, drawn, kind)).ToArray();
                return random.Next(2) == 0 ? SqlPredicate.AllOf(arms) : SqlPredicate.AnyOf(arms);
            }

            // DECIMAL is no column the reader was measured over.
            var types = drawn.Typed == IrEval.TypedAs.Column ? new[] { "INTEGER", "BIGINT", "DOUBLE", "BOOLEAN" } : new[] { "INTEGER", "BIGINT", "DOUBLE", "DECIMAL", "BOOLEAN" };
            var path = Paths[random.Next(Paths.Length)];
            SqlValue reading = random.Next(12) switch
            {
                < 4 => new SqlValue.Text(path),
                < 7 => new SqlValue.Typed(path, types[random.Next(types.Length)]),
                < 10 => new SqlValue.Cast(new[] { "INTEGER", "BIGINT", "DOUBLE" }[random.Next(3)], new SqlValue.Text(path)),
                _ => new SqlValue.Raw(path),
            };

            // Beside a raw value, the literals the loader reads as raw, of one type a predicate: a list of two types is
            // cast to one, and a column compared with two fails the statement. A VARIANT takes no bare number, and the
            // IR loses a boxed one's SQL type; a VARCHAR column beside anything but a string is cast.
            SqlValue.Literal Literal() => reading switch
            {
                SqlValue.Text => SqlValue.Of(Texts[random.Next(Texts.Length)]),
                SqlValue.Typed { Type: "BOOLEAN" } => SqlValue.Of(random.Next(2) == 0),
                SqlValue.Raw => (drawn.Raw, kind) switch
                {
                    (IrEval.RawAs.Varchar, _) or (_, 0) => SqlValue.Of(Texts[random.Next(Texts.Length)]),
                    (IrEval.RawAs.Variant, _) or (_, 1) => SqlValue.Of(random.Next(2) == 0),
                    _ => SqlValue.Of(Numbers[random.Next(Numbers.Length)]),
                },
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
        /// Writes a predicate as Calcite's SQL over a document column <c>d</c> and the columns the reader fills.
        /// </summary>
        static string Sql(SqlPredicate predicate, Drawn drawn) => predicate switch
        {
            SqlPredicate.Compare c => $"({Sql(c.Left, drawn)} {c.Op} {Sql(c.Right, drawn, c.Left)})",
            SqlPredicate.In i => $"({Sql(i.Value, drawn)} IN ({string.Join(", ", i.Points.Select(p => Sql(new SqlValue.Literal(p), drawn, i.Value)))}))",
            SqlPredicate.IsNull n => $"({Sql(n.Value, drawn)} IS NULL)",
            SqlPredicate.Not n => $"(NOT {Sql(n.Operand, drawn)})",
            SqlPredicate.And a => "(" + string.Join(" AND ", a.Arms.Select(arm => Sql(arm, drawn))) + ")",
            SqlPredicate.Or o => "(" + string.Join(" OR ", o.Arms.Select(arm => Sql(arm, drawn))) + ")",
            _ => throw new NotSupportedException(predicate.ToString()),
        };

        static string Sql(SqlValue value, Drawn drawn, SqlValue? beside = null) => value switch
        {
            _ when Column(value, drawn) is { } column => "\"" + Name(column) + "\"",
            SqlValue.Text t => $"JSON_VALUE(\"d\", '{Path(t.Path)}')",
            SqlValue.Typed t => $"JSON_VALUE(\"d\", '{Path(t.Path)}' RETURNING {t.Type})",
            SqlValue.Cast { Value: SqlValue.Text text } c => $"CAST(JSON_VALUE(\"d\", '{Path(text.Path)}') AS {c.Target})",
            SqlValue.Literal l when IsString(l.Value) => "'" + StringOf(l.Value).Replace("'", "''") + "'",
            SqlValue.Literal l when TypeOf(l.Value) == BooleanType => beside is SqlValue.Raw && drawn.Raw == IrEval.RawAs.Variant
                ? $"CAST({(l.Value == Of(true) ? "TRUE" : "FALSE")} AS VARIANT)"
                : l.Value == Of(true) ? "TRUE" : "FALSE",
            SqlValue.Literal l when TryNumberOf(l.Value, out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(value.ToString()),
        };

        static string Path(JsonDocumentPath path) => "$." + string.Join(".", path.Names);

    }

}
