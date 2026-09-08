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
    /// pinned is the engine the adapter has to agree with. Two questions, and the row model rests on
    /// both — <see cref="Rel.Convert.CosmosFilterSplitRule"/> weakens a comparison over the bare
    /// accessor because of the first, and the second is why a comparison through <c>RETURNING</c>
    /// may exclude a document rather than reproduce a crash.
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

    }

}
