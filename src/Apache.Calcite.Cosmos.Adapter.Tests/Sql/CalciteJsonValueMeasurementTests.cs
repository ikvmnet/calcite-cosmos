using System;
using System.Collections.Generic;

using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// What Calcite's own <c>JSON_VALUE</c> does, measured end to end and asserted so that a change
    /// upstream is reported here rather than discovered in a pushdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No service and no adapter: a <c>CalciteConnection</c> over a literal JSON string, so what is
    /// pinned is the engine itself. Three questions, and the row model rests on all of them —
    /// <see cref="Rel.Convert.CosmosFilterSplitRule"/> weakens a comparison over the bare accessor
    /// because of the first, the second is why a comparison through <c>RETURNING</c> may exclude a
    /// document rather than reproduce a crash, and the third is why an array-typed column is rendered
    /// at all.
    /// </para>
    /// <para>
    /// <b>Agreement with the engine is the rule and not the axiom.</b> The bare accessor is a
    /// rendering the adapter reproduces exactly; the two <c>RETURNING</c> measurements below are
    /// defects, and there the adapter implements what the construct means instead. Which is which is
    /// the point of measuring: a difference that is not written down here is a bug in this repository.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CalciteJsonValueMeasurementTests
    {

        static (string? Value, string Declared, string? Runtime, string? Threw) Ask(string expression)
        {
            try
            {
                using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT " + expression;

                using var reader = command.ExecuteReader();
                if (reader.Read() == false)
                    return (null, "-", null, "no rows");

                var declared = reader.GetFieldType(0)?.Name ?? "?";

                if (reader.IsDBNull(0))
                    return (null, declared, null, null);

                var value = reader.GetValue(0);
                return (value?.ToString(), declared, value?.GetType().Name, null);
            }
            catch (Exception e)
            {
                var inner = e;
                while (inner.InnerException is Exception next)
                    inner = next;

                return (null, "-", null, inner.GetType().Name);
            }
        }

        static string Document(string json) => "'{\"v\":" + json + "}'";

        /// <summary>
        /// The bare accessor is a rendering operator: declared <c>VARCHAR</c>, and a <c>String</c> at
        /// run time for every scalar.
        /// </summary>
        /// <remarks>
        /// The three layers agree here, which is worth pinning because they need not. A number
        /// arrives as its digits, a boolean as <c>true</c> or <c>false</c>, a double in Java's
        /// rendering. A large integer keeps every digit — the value is not going through a double.
        /// An object, an array, a JSON null and an absent path all answer SQL null: the first two by
        /// the standard's error path, since neither is a scalar.
        /// </remarks>
        [TestMethod]
        public void TheBareAccessorRendersEveryScalarAsText()
        {
            var expected = new (string Json, string? Value)[]
            {
                ("\"bikes\"", "bikes"),
                ("30", "30"),
                ("-7", "-7"),
                ("30.7", "30.7"),
                ("1e30", "1.0E30"),
                ("9007199254740993", "9007199254740993"),
                ("true", "true"),
                ("false", "false"),
                ("null", null),
                ("{\"a\":1}", null),
                ("[1,2]", null),
            };

            foreach (var (json, value) in expected)
            {
                var answer = Ask($"JSON_VALUE({Document(json)}, '$.v')");

                answer.Threw.Should().BeNull($"JSON_VALUE over {json} should not throw");
                answer.Declared.Should().Be("String", $"the accessor is declared VARCHAR, over {json}");
                answer.Value.Should().Be(value, $"over {json}");

                if (value is not null)
                    answer.Runtime.Should().Be("String", $"and is a String at run time, over {json}");
            }

            Ask("JSON_VALUE('{}', '$.v')").Value.Should().BeNull("an absent path answers null");
        }

        /// <summary>
        /// <c>RETURNING</c> asserts the type rather than converting to it, and <c>ON ERROR</c> does
        /// not govern the mismatch. <b>Both are defects.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// SQL:2016 makes a returning-type mismatch an error condition, governed by the <c>ON ERROR</c>
        /// clause whose default is <c>NULL</c>. Calcite instead raises a raw cast failure, and writing
        /// <c>NULL ON ERROR</c> explicitly changes nothing.
        /// </para>
        /// <para>
        /// The object case is what shows it to be an accident rather than a reading of the standard:
        /// an object is not a scalar, so the extraction fails, the error path runs, and null comes
        /// back correctly. The cast happens afterwards and outside that handling, so a scalar of the
        /// wrong type never reaches it.
        /// </para>
        /// <para>
        /// <b>The adapter is entitled to be closer to the standard than the engine here.</b> A
        /// comparison pushed through a <c>RETURNING</c> may restrict to the type it names, which
        /// excludes exactly the documents that would have thrown — and which the standard says should
        /// have been null, and therefore excluded, in the first place. That is a deliberate
        /// divergence, recorded rather than hidden.
        /// </para>
        /// </remarks>
        /// <summary>
        /// <c>JSON_QUERY</c> is the mirror of the scalar accessor, and it re-serialises what it finds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both halves matter to the adapter. That it answers for an object and an array and null for
        /// everything else is the guard the pushed column renders —
        /// <c>IS_OBJECT(p) OR IS_ARRAY(p)</c> is that line at the service, as <c>IS_PRIMITIVE</c> is
        /// <c>JSON_VALUE</c>'s.
        /// </para>
        /// <para>
        /// <b>And that the text is normalised is why the reading cannot hand over the service's own
        /// bytes.</b> The document here is written with spaces between its tokens and comes back
        /// without them, so a column that returned what Cosmos stored would differ from the in-process
        /// answer by exactly the whitespace the document happened to carry.
        /// <see cref="Apache.Calcite.Cosmos.Adapter.CosmosReading.JsonText"/> writes it out compactly
        /// for that reason.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void JsonQueryAnswersStructureOnlyAndWritesItCompactly()
        {
            const string Spaced = "'{\"v\": [ \"a\" ,   \"b\" ] , \"o\": { \"a\" : 1 }, \"s\": \"bikes\", \"n\": null }'";

            Ask($"JSON_QUERY({Spaced}, '$.o')").Value.Should().Be("{\"a\":1}",
                "an object comes back as its JSON, with the spaces the document carried removed");

            Ask($"JSON_QUERY({Spaced}, '$.v')").Value.Should().Be("[\"a\",\"b\"]",
                "and so does an array");

            Ask($"JSON_QUERY({Spaced}, '$.s')").Value.Should().BeNull("a scalar is not this function's business");
            Ask($"JSON_QUERY({Spaced}, '$.n')").Value.Should().BeNull("nor is a JSON null");
            Ask($"JSON_QUERY({Spaced}, '$.missing')").Value.Should().BeNull("nor is an absent path");
        }

        /// <summary>
        /// A wrapper or a behaviour clause substitutes a value the path does not hold.
        /// </summary>
        /// <remarks>
        /// Which is why the adapter renders only the plain form as a path, and declines these — the
        /// path carries the value, and these carry something built around it. Held here because the
        /// refusal rests on what they do rather than on their being unfamiliar.
        /// </remarks>
        [TestMethod]
        public void JsonQueryWrapperAndBehaviourClausesAnswerSomethingElse()
        {
            const string Spaced = "'{\"s\": \"bikes\"}'";

            Ask($"JSON_QUERY({Spaced}, '$.s' WITH UNCONDITIONAL ARRAY WRAPPER)").Value.Should().Be("[\"bikes\"]",
                "the wrapper builds an array the path does not hold");

            Ask($"JSON_QUERY({Spaced}, '$.s' EMPTY OBJECT ON ERROR)").Value.Should().Be("{}",
                "and a behaviour clause substitutes one on the error the scalar causes");
        }

        [TestMethod]
        public void ReturningAssertsTheTypeAndOnErrorDoesNotGovernIt()
        {
            // Where the document holds exactly the type named, it answers.
            var exact = Ask($"JSON_VALUE({Document("30")}, '$.v' RETURNING INTEGER)");
            exact.Threw.Should().BeNull();
            exact.Value.Should().Be("30");
            exact.Declared.Should().Be("Int32", "RETURNING names the declared type");

            // Where it does not, a raw cast failure -- with or without the standard's own default.
            var mismatched = new List<string>
            {
                $"JSON_VALUE({Document("30")}, '$.v' RETURNING DOUBLE)",
                $"JSON_VALUE({Document("30")}, '$.v' RETURNING DOUBLE NULL ON ERROR)",
                $"JSON_VALUE({Document("30.7")}, '$.v' RETURNING INTEGER)",
                $"JSON_VALUE({Document("30.7")}, '$.v' RETURNING INTEGER NULL ON ERROR)",
                $"JSON_VALUE({Document("\"abc\"")}, '$.v' RETURNING INTEGER)",
                $"JSON_VALUE({Document("\"abc\"")}, '$.v' RETURNING INTEGER NULL ON ERROR)",
                $"JSON_VALUE({Document("true")}, '$.v' RETURNING INTEGER)",
            };

            foreach (var expression in mismatched)
                Ask(expression).Threw.Should().NotBeNull(
                    "a returning-type mismatch still throws rather than answering null: " + expression);

            // And the extraction's own error path works, which is what makes the above an accident.
            Ask($"JSON_VALUE({Document("{\"a\":1}")}, '$.v' RETURNING INTEGER)").Threw.Should().BeNull(
                "an object is not a scalar, so the error path runs and answers null");
        }

        /// <summary>
        /// An array <c>RETURNING</c> never answers an array. <b>This too is a defect</b>, and the
        /// adapter implements the construct rather than reproducing it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>RETURNING … ARRAY</c> is the only spelling in SQL that gives an accessor an array type,
        /// and it is therefore the only spelling an <c>UNNEST</c> will take as a source —
        /// <c>JSON_QUERY</c> is <c>VARCHAR</c> even <c>WITH ARRAY WRAPPER</c>. So the clause exists to
        /// name an array, and upstream treats it as a working one:
        /// <see href="https://issues.apache.org/jira/browse/CALCITE-6208">CALCITE-6208</see>, fixed in
        /// 1.37.0, is about the element nullability of exactly
        /// <c>unnest(json_value(col, '$.c' returning bigint array))</c>. It never produces an array
        /// here regardless.
        /// </para>
        /// <para>
        /// <b>Where it happens, isolated.</b> Not the ADO.NET wrapper and not code generation —
        /// <c>JsonFunctions.StatefulFunction.jsonValue</c>, the extraction itself, invoked directly
        /// with <c>NULL ON EMPTY</c> and <c>NULL ON ERROR</c>, answers <c>null</c> over
        /// <c>{"v":["a","b"]}</c> and the string <c>bikes</c> over <c>{"v":"bikes"}</c>. The function
        /// is scalar-only by construction, which is right for SQL/JSON; the <c>RETURNING</c> type is
        /// applied afterwards as a cast, which is why an array answers null and a scalar throws
        /// <c>String → java.util.List</c> outside the <c>ON ERROR</c> handling. The validator admits a
        /// return type the runtime can never produce, and the two halves have never been reconciled.
        /// </para>
        /// <para>
        /// The same answers come back through Calcite's own JDBC driver, over a literal and over a
        /// table column alike, and the <c>UNNEST</c> of one yields no rows — so the null is Calcite's
        /// and not a reader's. Measured against controls that rule the layers out: <c>ARRAY['a','b']</c>
        /// arrives as an <c>ArrayImpl</c>, and a <see cref="java.util.List"/> from a table's own row
        /// arrives as a CLR array — see <see cref="CalciteArrayReadingMeasurementTests"/>.
        /// </para>
        /// <para>
        /// <b>Which is why the adapter answers the array, and why that is not a divergence.</b> The
        /// traversal already reads the elements at that path, and a projection of the same call
        /// answering null made one expression mean two things (#119). The pushed column is
        /// <c>IS_ARRAY(p) ? p : null</c>, and against what the construct means it is right in every
        /// case: the array where the clause exists to name one, null for an object, a JSON null and an
        /// absent path — which is what the engine answers too — and null for a scalar, a type mismatch
        /// under the default <c>NULL ON ERROR</c>, where the engine throws. Two of those five the
        /// engine gets wrong; none of them the adapter does.
        /// </para>
        /// <para>
        /// <b>The authority is Calcite's extension, not SQL:2016</b>, which restricts
        /// <c>RETURNING</c> to predefined scalar types and offers <c>JSON_QUERY</c> for structure —
        /// so the spelling is not standard SQL at all. It is Calcite's, it is intended, and only its
        /// runtime is missing. A release that repairs the extraction therefore converges on what this
        /// already does.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void AnArrayReturningAnswersNullForAnArrayAndThrowsForAScalar()
        {
            // An array — what the clause is for — comes back null.
            foreach (var json in new[] { "[\"a\",\"b\"]", "[]", "[1,2]" })
            {
                var answer = Ask($"JSON_VALUE({Document(json)}, '$.v' RETURNING VARCHAR ARRAY)");

                answer.Threw.Should().BeNull($"over {json}");
                answer.Declared.Should().Be("String[]", $"RETURNING names the declared type, over {json}");
                answer.Value.Should().BeNull($"the engine answers null even over {json}, which is the defect");
            }

            // An object, a JSON null and an absent path answer null as well, and there the adapter agrees.
            Ask($"JSON_VALUE({Document("{\"a\":1}")}, '$.v' RETURNING VARCHAR ARRAY)").Value.Should().BeNull();
            Ask($"JSON_VALUE({Document("null")}, '$.v' RETURNING VARCHAR ARRAY)").Value.Should().BeNull();
            Ask("JSON_VALUE('{}', '$.v' RETURNING VARCHAR ARRAY)").Value.Should().BeNull();

            // A scalar throws: the extraction succeeded, and the cast to a list is outside its handling.
            foreach (var json in new[] { "\"bikes\"", "30", "true" })
                Ask($"JSON_VALUE({Document(json)}, '$.v' RETURNING VARCHAR ARRAY)").Threw.Should().NotBeNull(
                    $"a scalar under an array RETURNING is a raw cast failure, over {json}");
        }

    }

}
