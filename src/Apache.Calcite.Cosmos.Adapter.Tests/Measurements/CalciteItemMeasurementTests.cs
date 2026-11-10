using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.type;
using org.apache.calcite.sql.validate;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's <c>ITEM</c> answers over the value the reader built, and what a reading of an element answers: the
    /// Calcite side of an index segment in a path.
    /// </summary>
    /// <remarks>
    /// In process, over <see cref="MaterialisedTable"/>: an <c>ANY</c> column holds what <c>GetNatural</c> makes of each
    /// value, and the document column what the accessors read.
    /// </remarks>
    public class CalciteItemMeasurementTests
    {

        /// <summary>
        /// The values: an array of one and of two; an empty array; a string, a number, a boolean, an object, a null and an
        /// absent path.
        /// </summary>
        static readonly string?[] Values = ["[2]", "[1,2]", "[]", "\"ab\"", "30", "true", "{\"a\":1}", "null", null];

        /// <summary>
        /// An index over a raw value is its element, counted from one, and null wherever there is no element: past the end,
        /// at zero, over an object, a null and an absent path. Over a scalar a <c>VARIANT</c> is null too, and an
        /// <c>ANY</c> raises, its <c>ITEM</c> reading the value as a struct. An <c>ANY</c>'s element is the value the reader
        /// built, and a <c>VARIANT</c>'s no box: unequal to every boxed value, and a cast of it raises.
        /// </summary>
        [Fact, CosmosPins("Calcite.ItemIsNullWhereNoElementIs")]
        public void AnItemIsNullWhereNoElementIs()
        {
            using var connection = MaterialisedTable.Open();
            string?[] structures = [.. Values.Where(v => v is null || v[0] is '[' or '{' || v == "null")];
            foreach (var type in new[] { SqlTypeName.ANY, SqlTypeName.VARIANT })
                MaterialisedTable.Of(type, structures).Ask(connection, "\"v\"[1] IS NULL", "\"v\"[2] IS NULL", "\"v\"[0] IS NULL").Should().Equal(["FFTTTT", "TFTTTT", "TTTTTT"], type.getName());

            // An ANY's element is the value the reader built; a VARIANT's is no box, unequal to every boxed value and no cast's.
            MaterialisedTable.Of(SqlTypeName.ANY, structures).Ask(connection, "\"v\"[1] = CAST(2 AS BIGINT)").Should().Equal("TFUUUU");
            MaterialisedTable.Of(SqlTypeName.VARIANT, structures).Ask(connection, "\"v\"[1] = CAST(CAST(2 AS BIGINT) AS VARIANT)").Should().Equal("FFUUUU");
            MaterialisedTable.Of(SqlTypeName.VARIANT, ["[2]"]).Failure(connection, "CAST(\"v\"[1] AS INTEGER) = 2").Should().BeOfType<System.InvalidCastException>();

            foreach (var scalar in new[] { "\"ab\"", "30", "true" })
            {
                MaterialisedTable.Of(SqlTypeName.VARIANT, [scalar]).Ask(connection, "\"v\"[1] IS NULL").Should().Equal(["T"], scalar);
                MaterialisedTable.Of(SqlTypeName.ANY, [scalar]).Failure(connection, "\"v\"[1] IS NULL").Should().BeOfType<java.lang.IllegalStateException>(scalar);
            }
        }

        /// <summary>
        /// A reading of an element answers what the same reading of a path holding the element answers: the text accessor of
        /// each character type, a <c>RETURNING</c> of each type the loader reads, <c>VARIANT</c> among them, <c>JSON_QUERY</c>,
        /// <c>JSON_EXISTS</c> and the accessor re-typed <c>ANY</c>, the raw value a weakening's test is written over, alike,
        /// and each type test's body over the text, a <c>RETURNING</c>, <c>JSON_QUERY</c> and the raw value; and where there is
        /// no element, past the end or over a value that is no array, what it answers over an absent path. The cell names each
        /// reading measured so, and no other.
        /// </summary>
        [Fact, CosmosPins("Calcite.ReadingsOfAnIndexAreAsOfAPath")]
        public void AReadingOfAnIndexIsAsOfAPath()
        {
            using var connection = MaterialisedTable.Open();
            var root = (org.apache.calcite.schema.SchemaPlus)((Apache.Calcite.Data.CalciteConnection)connection).RootSchema;
            foreach (var test in TypeTests)
                root.add(test, Declared(test));

            var (_, elements, paths) = Indexes[0];
            string[] readings = ["JSON_VALUE(\"d\", '$.f[0]') = '30'", "JSON_VALUE(\"d\", '$.f[0]') IS NULL", "JSON_QUERY(\"d\", '$.f[0]') IS NULL", "JSON_EXISTS(\"d\", '$.f[0]')"];
            string Path(string reading) => reading.Replace("$.f[0]", "$.g");

            MaterialisedTable.Of(elements).Ask(connection, readings).Should().Equal(MaterialisedTable.Of(paths).Ask(connection, [.. readings.Select(Path)]));

            // A RETURNING raises over a scalar of another type, so it is asked over the rest.
            var typed = "JSON_VALUE(\"d\", '$.f[0]' RETURNING INTEGER) = 30";
            int[] numeric = [0, 2, 3, 5, 6, 7, 8, 9, 10, 11];
            MaterialisedTable.Of(numeric.Select(i => elements[i])).Ask(connection, typed).Should().Equal(MaterialisedTable.Of(numeric.Select(i => paths[i])).Ask(connection, Path(typed)));

            // Each reading by the name the vocabulary keys it by, and each type test's body over it, at every path an
            // element is read at, a raise where it raises: the text of each character type, a RETURNING of each type,
            // and the accessor re-typed ANY, which no statement spells, by Calcite's executor. RETURNING VARIANT's
            // value, which the client reads in no box (it raises a mapping exception of its own over every scalar), is
            // compared by the executor alone.
            var differs = new Dictionary<string, List<string>>
            {
                [PredicateAtom.TextReader] = [.. Differing(connection, path => [.. Characters.Select(t => $"JSON_VALUE(\"d\", '{path}'{t.Returning})")])],
                [PredicateAtom.TypedReaders] = [.. Differing(connection, path => [.. Returnings.Where(t => t != "VARIANT").Select(t => $"JSON_VALUE(\"d\", '{path}' RETURNING {t})")]),
                                                   .. Differing(connection, path => [$"JSON_VALUE(\"d\", '{path}' RETURNING VARIANT)"], value: false),
                                                   .. ExecutedDiffering(SqlTypeName.VARIANT, ("RETURNING VARIANT", (_, value) => value))],
                [PredicateAtom.QueryReader] = [.. Differing(connection, path => [$"JSON_QUERY(\"d\", '{path}')"])],
                [PredicateAtom.RawReader] = [.. ExecutedDiffering(SqlTypeName.ANY, [.. TypeTests.Select(t => (t + "(raw)", Tested(t))).Prepend(("raw", (_, raw) => raw))])],
            };
            foreach (var (reader, wrong) in differs)
                foreach (var w in wrong)
                    TestContext.Current.TestOutputHelper?.WriteLine($"{reader}: {w}");

            // The raw value is the scalar the element holds, null elsewhere, and a test's body reads the element's kind.
            Shown(Executed((_, raw) => raw, "$.f[0]", elements)).Should().Be("30,x,null,null,true,null,null,null,null,null,null,null,3000000000,2.5");
            Shown(Executed(Tested("IS_STRING"), "$.f[0]", elements)).Should().Be("false,true,false,false,false,false,false,false,false,false,false,false,false,false");
            Shown(Executed(Tested("IS_DEFINED"), "$.f[0]", elements)).Should().Be("true,true,true,true,true,true,false,false,false,false,false,false,true,true");

            // Each RETURNING answers a value at some element, so its comparison with the path's compares one: a whole
            // number beyond an int's range for BIGINT, a fraction for FLOAT and DOUBLE. TINYINT, SMALLINT and REAL
            // answer none anywhere: Calcite casts the number it parsed, an int, a long, a double or a decimal, to a
            // byte, a short or a float, and raises over every number, at the path as at the element.
            Returnings.Where(t => t != "VARIANT" && Indexes.All(index => MaterialisedTable.Of(index.Elements)
                .Values(connection, $"JSON_VALUE(\"d\", '{index.Path}' RETURNING {t})").All(v => v is null or Exception)))
                .Should().BeEquivalentTo(["TINYINT", "SMALLINT", "REAL"], "every other RETURNING answers a value somewhere");

            CosmosAxioms.Get("Calcite.ReadingsOfAnIndexAreAsOfAPath").AsOfAPath.Should().BeEquivalentTo(differs.Where(d => d.Value.Count == 0).Select(d => d.Key),
                "the cell names each reading an index reads as of a path, a test's body over it among it, and no other");
        }

        /// <summary>
        /// Each path an element is read at, documents holding something there or nothing, and documents holding the
        /// same at <c>$.g</c>, or nothing: an index over an element of each kind and over none, past the end, over a
        /// number, a string, an object and a null, and no array; a later index; a property of an element; and an
        /// element of an element. Each holds a whole number beyond an int's range and a fraction too, over which
        /// <c>RETURNING BIGINT</c>, <c>FLOAT</c> and <c>DOUBLE</c> answer a value, where they raise over 30.
        /// </summary>
        static readonly (string Path, string[] Elements, string[] Paths)[] Indexes =
        [
            ("$.f[0]",
                ["{\"f\":[30]}", "{\"f\":[\"x\"]}", "{\"f\":[{\"a\":1}]}", "{\"f\":[null]}", "{\"f\":[true]}", "{\"f\":[[1]]}", "{\"f\":[]}", "{\"f\":30}", "{\"f\":\"ab\"}", "{\"f\":{\"a\":1}}", "{\"f\":null}", "{}",
                    "{\"f\":[3000000000]}", "{\"f\":[2.5]}"],
                ["{\"g\":30}", "{\"g\":\"x\"}", "{\"g\":{\"a\":1}}", "{\"g\":null}", "{\"g\":true}", "{\"g\":[1]}", "{}", "{}", "{}", "{}", "{}", "{}",
                    "{\"g\":3000000000}", "{\"g\":2.5}"]),
            ("$.f[1]",
                ["{\"f\":[1,30]}", "{\"f\":[1,\"x\"]}", "{\"f\":[1,false]}", "{\"f\":[1,{\"a\":1}]}", "{\"f\":[1]}", "{\"f\":\"ab\"}", "{\"f\":[1,3000000000]}", "{\"f\":[1,2.5]}"],
                ["{\"g\":30}", "{\"g\":\"x\"}", "{\"g\":false}", "{\"g\":{\"a\":1}}", "{}", "{}", "{\"g\":3000000000}", "{\"g\":2.5}"]),
            ("$.f[0].a",
                ["{\"f\":[{\"a\":30}]}", "{\"f\":[{\"a\":\"x\"}]}", "{\"f\":[{\"a\":null}]}", "{\"f\":[{\"a\":[1]}]}", "{\"f\":[{\"b\":1}]}", "{\"f\":[30]}", "{\"f\":[]}", "{\"f\":{\"a\":30}}",
                    "{\"f\":[{\"a\":3000000000}]}", "{\"f\":[{\"a\":2.5}]}"],
                ["{\"g\":30}", "{\"g\":\"x\"}", "{\"g\":null}", "{\"g\":[1]}", "{}", "{}", "{}", "{}", "{\"g\":3000000000}", "{\"g\":2.5}"]),
            ("$.f[0][0]",
                ["{\"f\":[[30]]}", "{\"f\":[[\"x\"]]}", "{\"f\":[[{\"a\":1}]]}", "{\"f\":[[]]}", "{\"f\":[30]}", "{\"f\":[\"ab\"]}", "{\"f\":[[3000000000]]}", "{\"f\":[[2.5]]}"],
                ["{\"g\":30}", "{\"g\":\"x\"}", "{\"g\":{\"a\":1}}", "{}", "{}", "{}", "{\"g\":3000000000}", "{\"g\":2.5}"]),
        ];

        /// <summary>
        /// The text accessor of each character type the loader reads as the text, by its type: the plain one, and a
        /// <c>RETURNING</c> of each
        /// (<c>CosmosPredicateLoaderTests.EachAccessorTheLoaderReadsIsOneAnIndexIsMeasuredUnder</c>).
        /// </summary>
        internal static readonly (SqlTypeName Type, string Returning)[] Characters =
            [(SqlTypeName.VARCHAR, ""), (SqlTypeName.VARCHAR, " RETURNING VARCHAR(3)"), (SqlTypeName.CHAR, " RETURNING CHAR(8)")];

        /// <summary>
        /// Each type the loader reads a <c>RETURNING</c> of, all keyed by the one name <c>typed</c>
        /// (<c>CosmosPredicateLoaderTests.EachAccessorTheLoaderReadsIsOneAnIndexIsMeasuredUnder</c>).
        /// </summary>
        internal static readonly string[] Returnings = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE", "DECIMAL", "BOOLEAN", "VARIANT"];

        /// <summary>The one-place tests whose bodies are measured over each reading of an element.</summary>
        static readonly string[] TypeTests = ["IS_DEFINED", "IS_NULL", "IS_PRIMITIVE", "IS_STRING", "IS_NUMBER", "IS_BOOL", "IS_OBJECT", "IS_ARRAY"];

        /// <summary>The schema's own declaration of a test, the function whose body Calcite runs in process.</summary>
        static org.apache.calcite.schema.Function Declared(string test) => (org.apache.calcite.schema.Function)CosmosSchemaFunctions.Instance.get(test).iterator().next();

        /// <summary>A test over a node, as Calcite wraps the schema's declaration of it.</summary>
        static Func<RexBuilder, RexNode, RexNode> Tested(string test) => (rex, node) => rex.makeCall(
            new SqlUserDefinedFunction(new SqlIdentifier(test, SqlParserPos.ZERO), SqlKind.OTHER_FUNCTION, ReturnTypes.BOOLEAN, null, null, Declared(test)), node);

        /// <summary>
        /// Each expression a reading writes at a path, its value where the client reads one, and each type test over
        /// it, whose values over the documents holding an element differ from those over the documents holding the same
        /// at <c>$.g</c>.
        /// </summary>
        static IEnumerable<string> Differing(System.Data.Common.DbConnection connection, Func<string, string[]> expressions, bool value = true) =>
            from index in Indexes
            from expression in expressions(index.Path)
            from asked in value ? TypeTests.Select(t => $"{t}({expression})").Prepend(expression) : TypeTests.Select(t => $"{t}({expression})")
            let a = MaterialisedTable.Of(index.Elements).Values(connection, asked)
            let b = MaterialisedTable.Of(index.Paths).Values(connection, asked.Replace(index.Path, "$.g"))
            where Same(a, b) == false
            select $"{asked}: {Shown(a)} against {Shown(b)}";

        /// <summary>
        /// Each node built over the path's accessor typed so, by Calcite's executor, whose values over the documents holding an
        /// element differ from those over the documents holding the same at <c>$.g</c>.
        /// </summary>
        static IEnumerable<string> ExecutedDiffering(SqlTypeName type, params (string Name, Func<RexBuilder, RexNode, RexNode> Build)[] asked) =>
            from index in Indexes
            from node in asked
            let a = Executed(node.Build, index.Path, index.Elements, type)
            let b = Executed(node.Build, "$.g", index.Paths, type)
            where Same(a, b) == false
            select $"{node.Name} at {index.Path}: {Shown(a)} against {Shown(b)}";

        /// <summary>
        /// The path's accessor over a document column typed so, <c>JSON_VALUE(DOC, path)</c>: re-typed <c>ANY</c>, as the loader
        /// writes the raw value a weakening's test is written over, and typed <c>VARIANT</c>, as <c>RETURNING VARIANT</c> is.
        /// </summary>
        static RexNode Accessed(RexBuilder rex, string path, SqlTypeName type)
        {
            var operands = new java.util.ArrayList();
            operands.add(rex.makeInputRef(rex.getTypeFactory().createSqlType(SqlTypeName.VARCHAR), 0));
            operands.add(rex.makeLiteral(path));
            return rex.makeCall(rex.getTypeFactory().createSqlType(type), SqlStdOperatorTable.JSON_VALUE, operands);
        }

        /// <summary>What a node built over the path's accessor typed so is over each document, by Calcite's executor: its value, or the innermost exception it raises.</summary>
        static object?[] Executed(Func<RexBuilder, RexNode, RexNode> build, string path, string[] documents, SqlTypeName? type = null)
        {
            var types = new JavaTypeFactoryImpl();
            var rex = new RexBuilder(types);
            var executable = RexExecutorImpl.getExecutable(rex, java.util.Collections.singletonList(build(rex, Accessed(rex, path, type ?? SqlTypeName.ANY))),
                types.builder().add("d", types.createSqlType(SqlTypeName.VARCHAR)).build());
            var values = new object?[documents.Length];
            for (var i = 0; i < documents.Length; i++)
            {
                try
                {
                    executable.setDataContext(new CalciteInProcess.Record([documents[i]], types));
                    values[i] = executable.execute()?[0];
                }
                catch (Exception e)
                {
                    while (e.InnerException is Exception inner)
                        e = inner;

                    values[i] = e;
                }
            }

            GC.KeepAlive(executable);
            return values;
        }

        /// <summary>
        /// Whether two rows of values are the same: each value equal and of one type, or a raise of one type. A row that raises
        /// on every value measures nothing, so it is never the same.
        /// </summary>
        static bool Same(object?[] a, object?[] b) =>
            a.Length == b.Length && a.Any(v => v is not Exception) && a.Zip(b).All(p => p.First is Exception x ? p.Second is Exception y && x.GetType() == y.GetType() : p.First?.GetType() == p.Second?.GetType() && Equals(p.First, p.Second));

        static string Shown(object?[] values) => string.Join(",", values.Select(v => v is Exception e ? "!" + e.GetType().Name : v?.ToString() ?? "null"));

    }

}
