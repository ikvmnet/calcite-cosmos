using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

using Apache.Calcite.Data;

using FluentAssertions;
using FluentAssertions.Execution;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
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
    /// Three things are held here. The driver carries array values perfectly well, through all seven
    /// routes including the provider's own collection accessors
    /// (<see cref="TheDriverCarriesArrayValues"/>). No spelling of an array <c>RETURNING</c> produces
    /// one, and no reader route rescues it — <c>GetArray</c> and <c>GetArray&lt;T&gt;</c> included,
    /// which is what makes "no route" exhaustive rather than a survey of the general-purpose ones
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
        /// <param name="Collection">What <c>GetArray</c> returned — the provider's own collection accessor.</param>
        /// <param name="CollectionTyped">What <c>GetArray&lt;T&gt;</c> returned, for the column's own element type.</param>
        sealed record Read(
            Type? Declared,
            bool IsDbNull,
            object? Value,
            object? Indexer,
            object? Typed,
            object? Untyped,
            object? ProviderSpecific,
            object? Collection,
            object? CollectionTyped)
        {

            /// <summary>
            /// Every route's answer, so that a claim about "no route" is made over all of them.
            /// </summary>
            public IEnumerable<(string Route, object? Answer)> Routes =>
            [
                ("GetValue", Value),
                ("this[0]", Indexer),
                ("GetFieldValue<string[]>", Typed),
                ("GetFieldValue<object>", Untyped),
                ("GetProviderSpecificValue", ProviderSpecific),
                ("GetArray", Collection),
                ("GetArray<T>", CollectionTyped),
            ];

            /// <summary>
            /// Whether no route produced an array.
            /// </summary>
            /// <remarks>
            /// Asked as "produced no array" rather than "was null everywhere", because the two are
            /// not the same and only the first is the claim. A typed accessor refuses a column of
            /// another type by throwing, and a refusal is not a value — what would falsify this class
            /// is a route handing back a collection, not a route declining differently from its
            /// neighbours.
            /// </remarks>
            public bool FoundNoArray => Routes.All(r => r.Answer is not Array);

        }

        /// <summary>
        /// What a route did instead of answering.
        /// </summary>
        /// <remarks>
        /// Kept apart from a null so the two are never conflated: <c>GetArray</c> over a
        /// <c>VARCHAR</c> throwing is a different fact from a collection column holding nothing.
        /// </remarks>
        sealed record Thrown(string Exception)
        {
            public override string ToString() => "threw " + Exception;
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
                // is an answer too -- recorded as a refusal so a caller can tell it from "it was null".
                while (e.InnerException is Exception inner)
                    e = inner;

                return new Thrown(e.GetType().Name);
            }
        }

        /// <summary>
        /// Runs a statement and reads its first column by every route.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="withTable">Whether to register the document table as <c>docs</c>.</param>
        /// <param name="typedArray">
        /// How to call <c>GetArray&lt;T&gt;</c> for this column. Supplied by the caller rather than
        /// fixed, because the element type is the column's: <c>GetArray&lt;string&gt;</c> over an
        /// <c>INTEGER ARRAY</c> is refused for naming the wrong element type, which is a different
        /// fact from the one being measured and would be recorded as though it were the same.
        /// Defaults to <see cref="string"/>, which is what most of the corpus declares.
        /// </param>
        /// <returns>The read, or <c>null</c> where the statement produced no rows.</returns>
        static Read? Ask(string sql, bool withTable = false, Func<CalciteDataReader, object?>? typedArray = null)
        {
            typedArray ??= r => r.GetArray<string>(0);

            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            if (withTable)
                ((SchemaPlus)connection.RootSchema).add("docs", new DocumentTable());

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            if (reader.Read() == false)
                return null;

            // GetArray is the provider's own, ADO.NET having no accessor for a collection, so it is
            // reached through the concrete reader rather than through DbDataReader.
            var calcite = (CalciteDataReader)reader;
            var isNull = reader.IsDBNull(0);

            // Asked only where the column holds something, which is the calling convention rather
            // than a way around one. The collection accessors are typed getters and refuse a null as
            // every other typed getter does; a caller that wants to distinguish a null asks IsDBNull
            // first, explicitly. So this is what a correct caller writes, and the matrix writes it.
            //
            // Called unguarded, they would record a refusal for every array RETURNING in the corpus
            // -- all of which are null -- which says only that the column was null, a fact IsDBNull
            // already carries. The question here is whether any route reaches an array, and a
            // refusal-for-null is not an answer to it. TheCollectionAccessorsRefuseANullCollection
            // pins the refusal itself.
            object? collection = isNull ? null : Normalize(() => calcite.GetArray(0));
            object? collectionTyped = isNull ? null : Normalize(() => typedArray(calcite));

            return new Read(
                reader.GetFieldType(0),
                isNull,
                Normalize(() => reader.GetValue(0)),
                Normalize(() => reader[0]),
                Normalize(() => reader.GetFieldValue<string[]>(0)),
                Normalize(() => reader.GetFieldValue<object>(0)),
                Normalize(() => reader.GetProviderSpecificValue(0)),
                collection,
                collectionTyped);
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

            // Every route, including the two the provider added for collections. Asserted one by one
            // rather than through Routes, so a failure names the accessor that stopped answering.
            array.Value.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Indexer.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Typed.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Untyped.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.ProviderSpecific.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            array.Collection.Should().BeAssignableTo<Array>("GetArray is the accessor for a collection");
            array.CollectionTyped.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");

            // A MULTISET is the other collection type and arrives the same way -- they differ in
            // whether the order of the elements means anything, not in what holds them.
            var multiset = Ask("SELECT MULTISET['a','b']")!;
            multiset.Value.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
            multiset.CollectionTyped.Should().BeOfType<string[]>().Which.Should().Equal("a", "b");

            // And the element type is the column's rather than everything arriving as strings, which
            // is why the generic accessor is called with the type the column declares.
            var integers = Ask("SELECT ARRAY[1,2]", typedArray: r => r.GetArray<int>(0))!;
            integers.Declared.Should().Be(typeof(int[]));
            integers.Value.Should().BeOfType<int[]>().Which.Should().Equal(1, 2);
            integers.CollectionTyped.Should().BeOfType<int[]>().Which.Should().Equal(1, 2);
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
        /// <b>The reader is asked seven ways per case</b>, because the seven are not one mechanism: a
        /// provider may convert in <c>GetValue</c> and not in <c>GetFieldValue&lt;T&gt;</c>, or keep an
        /// engine representation only behind <c>GetProviderSpecificValue</c>. Two of the seven are the
        /// provider's own <c>GetArray</c> and <c>GetArray&lt;T&gt;</c> — the accessors built for
        /// collections, ADO.NET having none, and therefore the ones with the best claim to reach an
        /// array if anything does. The generic is called with the element type the column declares,
        /// since naming another is a refusal about the type argument rather than about the array.
        /// None of the seven differ here.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void NoArrayReturningFormYieldsAnArray()
        {
            Func<CalciteDataReader, object?> text = r => r.GetArray<string>(0);
            Func<CalciteDataReader, object?> integer = r => r.GetArray<int>(0);
            Func<CalciteDataReader, object?> big = r => r.GetArray<long>(0);

            var forms = new (string Case, string Sql, bool Table, Func<CalciteDataReader, object?> Typed)[]
            {
                ("VARCHAR ARRAY", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY)", false, text),
                ("VARCHAR(20) ARRAY", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR(20) ARRAY)", false, text),
                ("INTEGER ARRAY", $"SELECT JSON_VALUE({Literal}, '$.n' RETURNING INTEGER ARRAY)", false, integer),
                ("BIGINT ARRAY", $"SELECT JSON_VALUE({Literal}, '$.n' RETURNING BIGINT ARRAY)", false, big),
                ("lax path", $"SELECT JSON_VALUE({Literal}, 'lax $.v' RETURNING VARCHAR ARRAY)", false, text),
                ("strict path", $"SELECT JSON_VALUE({Literal}, 'strict $.v' RETURNING VARCHAR ARRAY)", false, text),
                ("wildcard step", $"SELECT JSON_VALUE({Literal}, 'lax $.v[*]' RETURNING VARCHAR ARRAY)", false, text),
                ("the document root", "SELECT JSON_VALUE('[\"a\",\"b\"]', '$' RETURNING VARCHAR ARRAY)", false, text),
                ("NULL ON EMPTY", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY NULL ON EMPTY)", false, text),
                ("NULL ON ERROR", $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY NULL ON ERROR)", false, text),
                ("DEFAULT ON EMPTY", $"SELECT JSON_VALUE({Literal}, '$.missing' RETURNING VARCHAR ARRAY DEFAULT ARRAY['z'] ON EMPTY)", false, text),
                ("a column, VARCHAR ARRAY", "SELECT JSON_VALUE(\"J\", '$.v' RETURNING VARCHAR ARRAY) FROM \"docs\"", true, text),
                ("a column, INTEGER ARRAY", "SELECT JSON_VALUE(\"J\", '$.n' RETURNING INTEGER ARRAY) FROM \"docs\"", true, integer),
            };

            // Every form is reported, rather than the first one to fail: which subset breaks is
            // what would say whether a future fix is partial.
            using var scope = new AssertionScope();

            foreach (var (name, sql, table, typed) in forms)
            {
                var read = Ask(sql, table, typed);

                read.Should().NotBeNull($"the statement should produce a row, over {name}");
                read!.Declared.Should().NotBeNull($"the column should be typed, over {name}");
                read.FoundNoArray.Should().BeTrue(
                    $"no reader route should find an array, over {name} — got " +
                    string.Join(", ", read.Routes.Select(r => $"{r.Route}={Describe(r.Answer)}")));
            }
        }

        /// <summary>
        /// The collection accessors refuse a null rather than answering one, as every other typed
        /// getter does, so a caller that wants to tell a null apart asks <c>IsDBNull</c> first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A contract and not a defect</b>, which is worth saying because the opposite is
        /// arguable: both return types are reference types, so null is expressible, and JDBC's
        /// <c>getArray</c> does answer null for a SQL NULL. The provider's rule is the other one —
        /// a typed getter converts a value, and where there is no value there is nothing to convert,
        /// so it refuses exactly as <c>GetInt32</c> or <c>GetString</c> would. Uniformity across the
        /// accessors wins over JDBC parity on this point, and the cost is that <c>IsDBNull</c> is
        /// explicit rather than implied.
        /// </para>
        /// <para>
        /// Pinned here because the matrix depends on it: every array <c>RETURNING</c> in this class
        /// is null, so this is the path a caller of the corpus actually takes, and a change to it
        /// would change what
        /// <see cref="NoArrayReturningFormYieldsAnArray"/> is measuring without changing its result.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TheCollectionAccessorsRefuseANullCollection()
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY)";

            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();

            var calcite = (CalciteDataReader)reader;

            reader.GetFieldType(0).Should().Be(typeof(string[]), "the column is a collection");
            reader.IsDBNull(0).Should().BeTrue("and it is null, which is the premise");

            // Called without the guard, deliberately: this is the one place that asks what happens.
            calcite.Invoking(r => r.GetArray(0)).Should().Throw<InvalidCastException>(
                "a typed getter refuses a null rather than answering one");

            calcite.Invoking(r => r.GetArray<string>(0)).Should().Throw<InvalidCastException>(
                "and naming the element type does not change that");

            // The general-purpose accessors answer null instead, which is the difference a caller
            // sees and the reason IsDBNull is what tells the two apart before either is called.
            reader.GetValue(0).Should().Be(DBNull.Value);
            reader.GetFieldValue<string[]>(0).Should().BeNull();
        }

        /// <summary>
        /// The collection accessors reach an array on exactly the column shape every failing case
        /// has, so what those cases lack is the value and not the route.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both are asked over a <c>VARCHAR ARRAY</c> produced by <c>DEFAULT … ON ERROR</c> — the same
        /// declared type, the same statement shape, the same reader as every null row above, differing
        /// only in that a value reaches the column. They answer it. That is what licenses the claim
        /// the matrix makes: a route that works here and finds nothing there is reporting an absence,
        /// not a limitation of its own.
        /// </para>
        /// <para>
        /// How they decline a null is pinned separately, by
        /// <see cref="TheCollectionAccessorsRefuseANullCollection"/>. The matrix asks
        /// <c>IsDBNull</c> before calling them, which is the convention a caller owes a typed getter
        /// rather than a way around one.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TheCollectionAccessorsReachAnArrayInSuchAColumn()
        {
            var read = Ask($"SELECT JSON_VALUE({Literal}, '$.v' RETURNING VARCHAR ARRAY DEFAULT ARRAY['z'] ON ERROR)")!;

            read.Declared.Should().Be(typeof(string[]), "the column is typed exactly as the failing ones are");
            read.IsDbNull.Should().BeFalse();
            read.Collection.Should().BeAssignableTo<Array>("GetArray is the accessor for a collection");
            read.CollectionTyped.Should().BeOfType<string[]>().Which.Should().Equal("z");
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
            // The collection accessor reaches it too, so what fails in the null cases is the
            // extraction and not the route. (Equal takes params, so the reason lives here.)
            read.CollectionTyped.Should().BeOfType<string[]>().Which.Should().Equal("z");
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
            Thrown thrown => thrown.ToString(),
            string[] a => "string[" + a.Length + "]",
            System.Array a => value.GetType().Name + "[" + a.Length + "]",
            _ => value.GetType().Name + ":" + value,
        };

    }

}
