using System;
using System.Collections.Generic;
using System.Data.Common;

using Apache.Calcite.Data;

using FluentAssertions;
using FluentAssertions.Execution;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Whether an array <c>RETURNING</c> can be read as an array through the ADO.NET driver, asked of
    /// every form of the clause and every route the reader offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The answer is no, and the point of the class is what rules out the alternatives.</b>
    /// <see cref="CalciteJsonValueMeasurementTests"/> establishes that the engine answers null; this
    /// establishes that nothing between the engine and the caller is responsible and nothing the
    /// caller can write gets past it. Both halves matter, because "the provider loses the array" and
    /// "the engine never produced one" look identical at <c>IsDBNull</c> and call for entirely
    /// different fixes.
    /// </para>
    /// <para>
    /// Three things are held here. The driver carries array values perfectly well
    /// (<see cref="TheDriverCarriesArrayValues"/>). No spelling of an array <c>RETURNING</c> produces
    /// one, and no reader route rescues it
    /// (<see cref="NoArrayReturningFormYieldsAnArray"/>). And the engine says why, in its own words,
    /// when asked to raise rather than swallow (<see cref="TheExtractionRefusesTheArrayAsNonScalar"/>)
    /// — with <see cref="ADefaultOnErrorProvesEverythingBelowTheExtractionWorks"/> as the control that
    /// closes it off: the very same expression, column type and reader hand back an array the moment
    /// a value reaches them by any route other than the extraction.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CalciteJsonValueArrayMeasurementTests
    {

        /// <summary>
        /// One document, carrying an array of strings, an array of numbers, a scalar and an object.
        /// </summary>
        const string Document = """{"v":["a","b"],"n":[1,2],"s":"bikes","o":{"a":1}}""";

        /// <summary>
        /// A table of one <c>VARCHAR</c> column holding the document, so the same questions can be
        /// asked of a column rather than of a literal.
        /// </summary>
        /// <remarks>
        /// Worth asking separately: a literal can be reduced at plan time, which is a different code
        /// path from the one a column's generated accessor takes. Measured, they agree — but that is
        /// a result rather than an assumption, and it is the shape
        /// <see href="https://issues.apache.org/jira/browse/CALCITE-6208">CALCITE-6208</see> is
        /// written against.
        /// </remarks>
        sealed class DocumentTable : org.apache.calcite.schema.impl.AbstractTable, ScannableTable
        {

            public override RelDataType getRowType(RelDataTypeFactory typeFactory) =>
                typeFactory.builder()
                    .add("J", typeFactory.createTypeWithNullability(typeFactory.createSqlType(SqlTypeName.VARCHAR), true))
                    .build();

            public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
            {
                var rows = new java.util.ArrayList(1);
                rows.add(new object[] { Document });

                return org.apache.calcite.linq4j.Linq4j.asEnumerable(rows);
            }

        }

        /// <summary>
        /// What every route the reader offers answered for one column.
        /// </summary>
        /// <param name="Declared">The CLR type the reader declares for the column.</param>
        /// <param name="IsDbNull">What <c>IsDBNull</c> said.</param>
        /// <param name="Value">What <c>GetValue</c> returned, or <c>null</c> for <c>DBNull</c>.</param>
        /// <param name="Indexer">What the indexer returned.</param>
        /// <param name="Typed">What <c>GetFieldValue&lt;string[]&gt;</c> returned, or the exception's name.</param>
        /// <param name="Untyped">What <c>GetFieldValue&lt;object&gt;</c> returned.</param>
        /// <param name="ProviderSpecific">What <c>GetProviderSpecificValue</c> returned.</param>
        sealed record Read(
            Type? Declared,
            bool IsDbNull,
            object? Value,
            object? Indexer,
            object? Typed,
            object? Untyped,
            object? ProviderSpecific)
        {

            /// <summary>
            /// Whether every route agreed the column held nothing.
            /// </summary>
            public bool IsNullEverywhere =>
                IsDbNull && Value is null && Indexer is null && Typed is null && Untyped is null && ProviderSpecific is null;

        }

        static object? Normalize(Func<object?> read)
        {
            try
            {
                var value = read();
                return value is DBNull ? null : value;
            }
            catch (Exception e)
            {
                // A typed accessor over a column of another type throws rather than answering, which
                // is an answer too -- recorded as the exception's name so a caller can tell the two
                // apart from "it was null".
                while (e.InnerException is Exception inner)
                    e = inner;

                return e.GetType().Name;
            }
        }

        /// <summary>
        /// Runs a statement and reads its first column by every route.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="withTable">Whether to register the document table as <c>docs</c>.</param>
        /// <returns>The read, or <c>null</c> where the statement produced no rows.</returns>
        static Read? Ask(string sql, bool withTable = false)
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            if (withTable)
                ((SchemaPlus)connection.RootSchema).add("docs", new DocumentTable());

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            if (reader.Read() == false)
                return null;

            return new Read(
                reader.GetFieldType(0),
                reader.IsDBNull(0),
                Normalize(() => reader.GetValue(0)),
                Normalize(() => reader[0]),
                Normalize(() => reader.GetFieldValue<string[]>(0)),
                Normalize(() => reader.GetFieldValue<object>(0)),
                Normalize(() => reader.GetProviderSpecificValue(0)));
        }

        /// <summary>
        /// The literal document, quoted for embedding in a statement.
        /// </summary>
        static string Literal => "'" + Document.Replace("'", "''") + "'";

        /// <summary>
        /// The driver hands back array values, by every route, for both collection types.
        /// </summary>
        /// <remarks>
        /// <b>The control the rest of the class rests on.</b> Without it, every null below could be
        /// the provider failing to marshal a collection, and the conclusion would be the opposite one.
        /// An <c>ARRAY</c> value arrives as a CLR array of the declared element type — through
        /// <c>GetValue</c>, the indexer, the strongly typed accessor, the untyped one, and the
        /// provider-specific one alike.
        /// </remarks>
        [TestMethod]
        public void TheDriverCarriesArrayValues()
        {
            var array = Ask("SELECT ARRAY['a','b']")!;

            array.Declared.Should().Be(typeof(string[]));
            array.IsDbNull.Should().BeFalse();
            array.Value.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Indexer.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Typed.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Untyped.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.ProviderSpecific.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");

            // A MULTISET is the other collection type and arrives the same way.
            var multiset = Ask("SELECT MULTISET['a','b']")!;
            multiset.Value.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");

            // And the element type is honoured rather than everything arriving as strings.
            var integers = Ask("SELECT ARRAY[1,2]")!;
            integers.Declared.Should().Be(typeof(int[]));
            integers.Value.Should().BeOfType<int[]>().Which.Should().Equal(1, 2);
        }

        /// <summary>
        /// No form of an array <c>RETURNING</c> yields an array, and no reader route rescues it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every axis that could plausibly matter, varied one at a time: the element type, a width on
        /// it, the path mode, a wildcard step, the document root itself, an explicit <c>ON EMPTY</c>
        /// or <c>ON ERROR</c> that names the standard's own default, and a column in place of a
        /// literal. All of them declare the right CLR type and all of them answer null.
        /// </para>
        /// <para>
        /// The reader is asked five ways per case because the five are not one mechanism: a provider
        /// may convert in <c>GetValue</c> and not in <c>GetFieldValue&lt;T&gt;</c>, or keep an engine
        /// representation only behind <c>GetProviderSpecificValue</c>. None of them differ here.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void NoArrayReturningFormYieldsAnArray()
        {
            var forms = new (string Case, string Sql, bool Table)[]
            {
                ("VARCHAR ARRAY", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY)", false),
                ("VARCHAR(20) ARRAY", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR(20) ARRAY)", false),
                ("INTEGER ARRAY", $"SELECT JSON_VALUE({Literal}, '$.n' RETURNING INTEGER ARRAY)", false),
                ("BIGINT ARRAY", $"SELECT JSON_VALUE({Literal}, '$.n' RETURNING BIGINT ARRAY)", false),
                ("lax path", $"SELECT JSON_VALUE({Literal}, 'lax $.v' RETURNING VARCHAR ARRAY)", false),
                ("strict path", $"SELECT JSON_VALUE({Literal}, 'strict $.v' RETURNING VARCHAR ARRAY)", false),
                ("wildcard step", $"SELECT JSON_VALUE({Literal}, 'lax $.v[*]' RETURNING VARCHAR ARRAY)", false),
                ("the document root", "SELECT JSON_VALUE('[\"a\",\"b\"]', '$' RETURNING VARCHAR ARRAY)", false),
                ("NULL ON EMPTY", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY NULL ON EMPTY)", false),
                ("NULL ON ERROR", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY NULL ON ERROR)", false),
                ("DEFAULT ON EMPTY", $"SELECT JSON_VALUE({Literal}, '$.missing' RETURNING VARCHAR ARRAY DEFAULT ARRAY['z'] ON EMPTY)", false),
                ("a column, VARCHAR ARRAY", "SELECT JSON_VALUE(\"J\", '$.v' RETURNING VARCHAR ARRAY) FROM \"docs\"", true),
                ("a column, INTEGER ARRAY", "SELECT JSON_VALUE(\"J\", '$.n' RETURNING INTEGER ARRAY) FROM \"docs\"", true),
            };

            // Every form is reported, rather than the first one to fail: which subset breaks is
            // what would say whether a future fix is partial.
            using var scope = new AssertionScope();

            foreach (var (name, sql, table) in forms)
            {
                var read = Ask(sql, table);

                read.Should().NotBeNull($"the statement should produce a row, over {name}");
                read!.Declared.Should().NotBeNull($"the column should be typed, over {name}");
                read.IsNullEverywhere.Should().BeTrue(
                    $"no reader route should find an array, over {name} — got " +
                    $"value={Describe(read.Value)}, typed={Describe(read.Typed)}, provider={Describe(read.ProviderSpecific)}");
            }
        }

        /// <summary>
        /// A nested <c>UNNEST</c> of one produces no rows, and <c>CARDINALITY</c> of one is null.
        /// </summary>
        /// <remarks>
        /// The two things a caller does with an array rather than reading it whole, and they fail the
        /// same way for the same reason — which is worth pinning because the <c>UNNEST</c> form is the
        /// one <see href="https://issues.apache.org/jira/browse/CALCITE-6208">CALCITE-6208</see>
        /// treats as supported, and the one this adapter renders to a traversal at the service.
        /// </remarks>
        [TestMethod]
        public void NeitherUnnestNorCardinalityFindsAnything()
        {
            Ask("SELECT u.c FROM \"docs\", UNNEST(JSON_VALUE(\"J\", '$.v' RETURNING VARCHAR ARRAY)) AS u(c)", withTable: true)
                .Should().BeNull("unnesting a null array produces no rows at all");

            var cardinality = Ask("SELECT CARDINALITY(JSON_VALUE(\"J\", '$.v' RETURNING VARCHAR ARRAY)) FROM \"docs\"", withTable: true)!;
            cardinality.IsDbNull.Should().BeTrue("the cardinality of a null array is null");
        }

        /// <summary>
        /// Asked to raise rather than swallow, the engine says exactly what it did — and it had the
        /// array in hand.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the whole diagnosis in one message.</b> <c>ERROR ON ERROR</c> turns the silent
        /// null into <em>"Strict jsonpath mode requires scalar value, and the actual value is:
        /// '[a, b]'"</em>. The path resolved, the array was found, and it was then discarded for not
        /// being a scalar: <c>JSON_VALUE</c> extracts SQL scalars by construction, and the
        /// <c>RETURNING</c> type is applied afterwards, so it is never consulted in the decision that
        /// throws the value away.
        /// </para>
        /// <para>
        /// Note "Strict" though the statement asked for neither mode. The scalar requirement is the
        /// function's, not the path's, so writing <c>lax</c> does not relax it — which is why the lax
        /// and strict rows above agree.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TheExtractionRefusesTheArrayAsNonScalar()
        {
            var act = () => Ask($"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY ERROR ON ERROR)");

            act.Should().Throw<Exception>()
                .WithMessage("*requires scalar value*", "the refusal is about the value's shape")
                .And.Message.Should().Contain("[a, b]", "and the array it refused is the one that was there");
        }

        /// <summary>
        /// <c>DEFAULT … ON ERROR</c> returns its array, through the same column type and the same
        /// reader.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The control that closes the question.</b> Everything about this statement is what the
        /// failing ones are — the same function, the same <c>RETURNING VARCHAR ARRAY</c>, the same
        /// declared <c>string[]</c>, the same reader — save that the value reaches the column from the
        /// default rather than from the extraction. It arrives intact. So the plan, the code
        /// generation, the column type and every layer of the provider handle a collection value
        /// correctly, and the extraction is the only thing that does not.
        /// </para>
        /// <para>
        /// It also confirms the error path is genuinely what runs: a default that is only consulted
        /// <c>ON ERROR</c> would never appear if the extraction had merely found nothing.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void ADefaultOnErrorProvesEverythingBelowTheExtractionWorks()
        {
            var read = Ask($"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY DEFAULT ARRAY['z'] ON ERROR)")!;

            read.Declared.Should().Be(typeof(string[]), "the column is typed exactly as the failing ones are");
            read.IsDbNull.Should().BeFalse();
            read.Value.Should().BeOfType<string[]>().Which.Should().Equal("z");
            read.Typed.Should().BeOfType<string[]>().Which.Should().Equal("z");
            read.ProviderSpecific.Should().BeOfType<string[]>().Which.Should().Equal("z");
        }

        /// <summary>
        /// <c>JSON_QUERY</c> is the other half of SQL/JSON and is text, wrapper or no wrapper.
        /// </summary>
        /// <remarks>
        /// Which is why it cannot stand in for the missing behaviour: it answers the array's JSON
        /// <em>text</em>, not the array, so it is neither an <c>UNNEST</c> source nor a collection
        /// column. <c>WITH UNCONDITIONAL ARRAY WRAPPER</c> wraps the text in another layer of
        /// brackets and is still text.
        /// </remarks>
        [TestMethod]
        public void JsonQueryIsTextWhateverTheWrapper()
        {
            var plain = Ask($"SELECT JSON_QUERY({Literal}, '$.v')")!;
            plain.Declared.Should().Be(typeof(string));
            plain.Value.Should().Be("[\"a\",\"b\"]");

            var wrapped = Ask($"SELECT JSON_QUERY({Literal}, '$.v' WITH UNCONDITIONAL ARRAY WRAPPER)")!;
            wrapped.Declared.Should().Be(typeof(string));
            wrapped.Value.Should().Be("[[\"a\",\"b\"]]");
        }

        static string Describe(object? value) => value switch
        {
            null => "null",
            string[] a => "string[" + a.Length + "]",
            System.Array a => value.GetType().Name + "[" + a.Length + "]",
            _ => value.GetType().Name + ":" + value,
        };

    }

}
