using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the <c>IS_*</c> tests answer in process, where a plan evaluates one: their bodies (<c>CosmosFunctionBodies</c>),
    /// read over a document holding each kind of value at the path, and none.
    /// </summary>
    /// <remarks>
    /// The Calcite side of a type test. That the bodies answer what the service answers is
    /// <c>Service.TypeTestBodiesMatchTheService</c>; this holds the row S reads to each body over the outcome catalogue's
    /// witnesses (<see cref="CosmosOutcomeWitnesses"/>), a test the body answers false absent from the row as false.
    /// </remarks>
    public class CalciteTypeTestMeasurementTests
    {

        [Fact, CosmosPins("Calcite.TypeTestsAreTheirBodies")]
        public void EachTypeTestAnswersItsRowInProcess()
        {
            var tests = CosmosFunctionBodiesTests.Tests.ToDictionary(t => t.Name.Replace("IS_", "").Replace("_", ""), t => t.Body, System.StringComparer.OrdinalIgnoreCase);
            var wrong = from outcome in CosmosAxioms.Get("Calcite.TypeTestsAreTheirBodies").Outcomes!
                        from cell in outcome.Cells
                        from json in CosmosOutcomeWitnesses.Of(CosmosValueClass.None, cell.Cell)
                        let document = json is null ? "{}" : $"{{\"v\":{json}}}"
                        let answer = tests[outcome.Test.ToString()[2..]](document, "$.v") ? CosmosAnswer.True : CosmosAnswer.False
                        where answer != cell.Answer
                        select $"{outcome.Test} over {json ?? "absent"} is {answer}, the row {cell.Answer}";

            wrong.Should().BeEmpty();
            CosmosAxioms.Get("Calcite.TypeTestsAreTheirBodies").Outcomes!.Select(o => o.Test).Should().BeEquivalentTo(
                CosmosAxioms.All.Where(a => a.Id is "Service.IsPrimitive" or "Service.IsDefined" or "Service.IsNull" or "Service.KindTests").SelectMany(a => a.Outcomes!).Select(o => o.Test),
                "every test the service's rows answer has its Calcite side");

            // What the body reads over each reading, measured (testreads): the reading a row names, and where none does, a raise.
            var rows = CosmosAxioms.Get("Calcite.TypeTestsAreTheirBodies").TestReads!;
            var read = TestReadings();
            read.Where(r => r.Value != (rows.Where(t => t.Reader == r.Key.Reader || t.Reader == PredicateAtom.TypedReaders && r.Key.Reader.StartsWith(PredicateAtom.TypedReaders + ":", StringComparison.Ordinal))
                .Select(t => t.As).SingleOrDefault() ?? "raises")).Select(r => $"{r.Key.Test} over {r.Key.Reader} reads {r.Value}").Should().BeEmpty();
            read.Where(r => r.Value == PredicateAtom.RawReader).Select(r => r.Key.Reader).Distinct().Should().HaveCount(12, "the text, a RETURNING of each of ten types and a JSON_QUERY read the raw value");
            rows.Where(t => PredicateAtom.ColumnOf(t.Reader) != null || t.Reader == PredicateAtom.CallReaders).Should()
                .HaveCount(PredicateAtom.Columns.Count() + 1).And.OnlyContain(t => t.As == PredicateAtom.RaisesReader, "the body raises over a field of every column and a call, each a row of its own");

            // The forms the loader writes apart (h15b), each keyed by a row of its own that says the body raises over it, as
            // measured over each writing: a field read as text (a VARCHAR column) or as a scalar type, an item, a cast to ANY and a
            // cast of the text to VARCHAR, which the loader reads as the text but for a test's operand.
            var forms = new Dictionary<string, string[]>
            {
                [PredicateAtom.FieldForm] = [PredicateAtom.RawIn("VARCHAR"), ScalarField],
                [PredicateAtom.ItemForm] = ["item:VARIANT"],
                [PredicateAtom.AnyCastForm] = [PredicateAtom.CastReader("ANY", PredicateAtom.TextReader)],
                [PredicateAtom.TextCastForm] = [PredicateAtom.CastReader("VARCHAR", PredicateAtom.TextReader)],
            };
            rows.Where(t => forms.ContainsKey(t.Reader)).Select(t => (t.Reader, t.As)).Should().BeEquivalentTo(forms.Keys.Select(f => (f, PredicateAtom.RaisesReader)));
            forms.SelectMany(f => f.Value.SelectMany(reader => ReadTests.Select(t => (f.Key, reader, t.Test, Read: read[(reader, t.Test)]))))
                .Where(m => m.Read != "raises").Select(m => $"{m.Test} over {m.reader}, written as {m.Key}, reads {m.Read}").Should().BeEmpty();
        }

        /// <summary>A field of a scalar type, as a <c>BIGINT</c> column holds <c>_ts</c>.</summary>
        const string ScalarField = "field:BIGINT";

        /// <summary>
        /// Each reading of a path a one-place test is asked over, in process: its SQL over a document whose <c>v</c> holds the
        /// witness, or over a column of the document's value, and what the loader writes it as. The text and a
        /// <c>RETURNING</c> of each type and a <c>JSON_QUERY</c> are an accessor the body takes apart; a raw value in each column, a field of a
        /// scalar type, an item of one, a cast to <c>ANY</c>, a cast of the text to <c>VARCHAR</c> and a call are not.
        /// </summary>
        static readonly (string Reader, string Sql, SqlTypeName? Column)[] Readings =
        [
            (PredicateAtom.TextReader, "JSON_VALUE(\"d\", '$.v')", null),
            // A RETURNING of each type the loader reads one of, VARIANT's among them, which holds no box.
            .. new[] { "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE", "DECIMAL", "BOOLEAN", "VARIANT" }
                .Select(t => (PredicateAtom.TypedReader(t), $"JSON_VALUE(\"d\", '$.v' RETURNING {t})", (SqlTypeName?)null)),
            (PredicateAtom.CastReader("ANY", PredicateAtom.TextReader), "CAST(JSON_VALUE(\"d\", '$.v') AS ANY)", null),
            (PredicateAtom.CastReader("VARCHAR", PredicateAtom.TextReader), "CAST(JSON_VALUE(\"d\", '$.v') AS VARCHAR)", null),
            (PredicateAtom.QueryReader, "JSON_QUERY(\"d\", '$.v')", null),
            (PredicateAtom.RawIn("VARIANT"), "\"v\"", SqlTypeName.VARIANT),
            (PredicateAtom.RawIn("ANY"), "\"v\"", SqlTypeName.ANY),
            (PredicateAtom.RawIn("VARCHAR"), "\"v\"", SqlTypeName.VARCHAR),
            (PredicateAtom.RawIn("CHAR"), "\"v\"", SqlTypeName.CHAR),
            (ScalarField, "\"v\"", SqlTypeName.BIGINT),
            ("item:VARIANT", "\"v\"['x']", SqlTypeName.VARIANT),
            (PredicateAtom.CallReaders, "UPPER(JSON_VALUE(\"d\", '$.v'))", null),
        ];

        /// <summary>The one-place tests whose readings are measured: the definedness test and two type tests.</summary>
        static readonly (CosmosServiceTest Test, string Name)[] ReadTests =
            [(CosmosServiceTest.IsDefined, "IS_DEFINED"), (CosmosServiceTest.IsString, "IS_STRING"), (CosmosServiceTest.IsNumber, "IS_NUMBER")];

        /// <summary>
        /// What each test over each reading reads, measured: <c>raw</c> where it answers each witness as the row says of the
        /// value the document holds at the path, <c>raises</c> where it raises over every witness, <c>other</c> otherwise.
        /// </summary>
        internal static IReadOnlyDictionary<(string Reader, CosmosServiceTest Test), string> TestReadings()
        {
            var row = CosmosAxioms.Get("Calcite.TypeTestsAreTheirBodies");
            var classes = new[] { CosmosValueClass.Absent, CosmosValueClass.Null, CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Number, CosmosValueClass.String, CosmosValueClass.Object, CosmosValueClass.Array };
            // The catalogue's witnesses, and a string every RETURNING of a number reads as one.
            var witnesses = (from cell in classes from json in CosmosOutcomeWitnesses.Of(CosmosValueClass.None, cell) select (Cell: cell, Json: json))
                .Append((Cell: CosmosValueClass.String, Json: (string?)"\"30\"")).ToList();

            using var connection = MaterialisedTable.Open("all");
            var root = (org.apache.calcite.schema.SchemaPlus)((Apache.Calcite.Data.CalciteConnection)connection).RootSchema;
            foreach (var (_, name) in ReadTests)
                for (var functions = CosmosSchemaFunctions.Instance.get(name).iterator(); functions.hasNext();)
                    root.add(name, (org.apache.calcite.schema.Function)functions.next());

            var read = new Dictionary<(string, CosmosServiceTest), string>();
            foreach (var (reader, sql, column) in Readings)
            {
                // The document holds the witness at v, and the column the document's value, under x for an item.
                var item = reader.StartsWith("item:", StringComparison.Ordinal);
                var table = new MaterialisedTable(column is { } type ? [("v", type)] : [],
                    [.. witnesses.Select(w => (w.Json is null ? "{}" : $"{{\"v\":{w.Json}}}", column is null ? Array.Empty<string?>() : [item ? (w.Json is null ? "{}" : $"{{\"x\":{w.Json}}}") : w.Json]))]);
                // Where the reading raises, as Calcite evaluates it: its null test, which a client reading no box of a VARIANT
                // does not touch.
                var reading = table.Values(connection, $"({sql}) IS NULL");
                foreach (var (test, name) in ReadTests)
                {
                    // The test reads the raw value where it answers each witness the reading answers as the row says of the
                    // value at the path, raising where the reading raises.
                    var cells = row.Outcomes!.Single(o => o.Test == test).Cells.ToDictionary(c => c.Cell, c => c.Answer);
                    var values = table.Values(connection, $"{name}({sql})");
                    read[(reader, test)] = values.All(v => v is Exception) ? "raises"
                        : values.Select((v, i) => reading[i] is Exception ? v is Exception : v is bool b && (b ? CosmosAnswer.True : CosmosAnswer.False) == cells[witnesses[i].Cell]).All(x => x) ? PredicateAtom.RawReader
                        : "other:" + string.Join(",", values.Select(v => v is Exception e ? "!" + e.GetType().Name : v?.ToString() ?? "null"));
                }
            }

            GC.KeepAlive(connection);
            return read;
        }

    }

}
