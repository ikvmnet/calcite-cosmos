using System;
using System.Collections.Generic;

using Apache.Calcite.Data;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
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
        [Fact]
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
        [Fact]
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
        [Fact]
        public void JsonQueryWrapperAndBehaviourClausesAnswerSomethingElse()
        {
            const string Spaced = "'{\"s\": \"bikes\"}'";

            Ask($"JSON_QUERY({Spaced}, '$.s' WITH UNCONDITIONAL ARRAY WRAPPER)").Value.Should().Be("[\"bikes\"]",
                "the wrapper builds an array the path does not hold");

            Ask($"JSON_QUERY({Spaced}, '$.s' EMPTY OBJECT ON ERROR)").Value.Should().Be("{}",
                "and a behaviour clause substitutes one on the error the scalar causes");
        }

        /// <summary>
        /// <c>JSON_VALUE</c>'s behaviour clauses substitute a value too, and which documents they
        /// substitute for depends on the path's mode — a path written without one is strict.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The standard's reading of <c>DEFAULT x ON EMPTY</c> is "where the path finds nothing", and
        /// that is the rendering a service could plausibly write — <c>IS_DEFINED(p) ? … : @x</c>. It
        /// is not what the engine does with <c>'$.v'</c>. With no mode written the path behaves as
        /// <c>strict</c>, an absent property is an <em>error</em> rather than an empty result, and the
        /// <c>ON ERROR</c> clause is the one that answers: <c>DEFAULT 'none' ON EMPTY</c> over
        /// <c>{}</c> is null and <c>DEFAULT 'none' ON ERROR</c> is <c>'none'</c>. A JSON null, an
        /// object and an array take the error branch too. Written <c>'lax $.v'</c>, the same
        /// <c>ON EMPTY</c> answers the default.
        /// </para>
        /// <para>
        /// So the clause's meaning is a table over the clause, the mode and the JSON type found, and
        /// <c>ERROR ON ERROR</c> raises, which no pushed column reproduces. That is why the adapter
        /// renders no behaviour clause but the defaults written down —
        /// <c>CosmosRexTranslator.IsPlainJsonValue</c> — and leaves the rest to the engine, which this
        /// pins. The guarded path rendered for the plain accessor answers null wherever a default
        /// answers here, so pushing it was a wrong answer and not a missed pushdown.
        /// </para>
        /// </remarks>
        [Fact]
        public void JsonValueBehaviourClausesFollowThePathModeNotTheStandardsReading()
        {
            Ask("JSON_VALUE('{}', '$.v' DEFAULT 'none' ON EMPTY)").Value.Should().BeNull(
                "an absent property under a path with no mode is an error, so ON EMPTY never runs");
            Ask("JSON_VALUE('{}', '$.v' DEFAULT 'none' ON ERROR)").Value.Should().Be("none",
                "and ON ERROR is the clause that answers it");
            Ask("JSON_VALUE('{}', 'strict $.v' DEFAULT 'none' ON ERROR)").Value.Should().Be("none",
                "which is what strict says");
            Ask("JSON_VALUE('{}', 'lax $.v' DEFAULT 'none' ON EMPTY)").Value.Should().Be("none",
                "while under lax the absence is empty, and ON EMPTY answers");

            foreach (var json in new[] { "null", "{\"a\":1}", "[1]" })
            {
                Ask($"JSON_VALUE({Document(json)}, '$.v' DEFAULT 'a' ON EMPTY DEFAULT 'b' ON ERROR)").Value.Should().Be("b",
                    $"a JSON null, an object and an array take the error branch, over {json}");
            }

            Ask("JSON_VALUE('{}', '$.v' ERROR ON ERROR)").Threw.Should().NotBeNull(
                "ERROR ON ERROR raises over an absent property");
            Ask("JSON_VALUE('{}', '$.v' ERROR ON EMPTY)").Threw.Should().BeNull(
                "and ERROR ON EMPTY does not, the absence not being empty");

            Ask($"JSON_VALUE({Document("30")}, '$.v' NULL ON EMPTY NULL ON ERROR)").Value.Should().Be("30",
                "the defaults written down are the plain accessor");
            Ask("JSON_VALUE('{}', '$.v' NULL ON EMPTY NULL ON ERROR)").Value.Should().BeNull(
                "in every case");
        }

        [Fact]
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
        /// <c>RETURNING DECIMAL</c> is the exception to <see cref="ReturningAssertsTheTypeAndOnErrorDoesNotGovernIt"/>: it
        /// parses the value's rendering as a <c>BigDecimal</c>, trimmed, so a stored string of digits answers its number,
        /// and only what does not parse raises. And it is typed <c>DECIMAL(19, 0)</c>, so a literal it is compared with is
        /// cast to scale zero while the value keeps its fraction: <c>= 2.5</c> is false of a stored 2.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured first where the predicate oracle's checker found it, on <c>feature/predicate-oracle-checker</c> in
        /// 5730e87 ("Read RETURNING DECIMAL as the parse of a rendering it is"): the oracle read the clause as asserting
        /// a number, and established that a row a comparison through it keeps holds one, which a stored <c>"30"</c> is a
        /// counterexample to; and that a row <c>= 2.5</c> keeps holds 2.5, which none does. Brought here unchanged.
        /// </para>
        /// <para>
        /// <b>The adapter read it the same way, twice.</b> The translator wrote the accessor as the path, so <c>= 2</c>
        /// pushed as <c>c.body = 2</c> and lost every document storing <c>"2"</c>; and the fact extractor took the
        /// equality to say the path holds the number 2, which over a path declared a string was a contradiction the
        /// rewriter answered with <c>FALSE</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void ReturningDecimalParsesTheRendering()
        {
            Ask($"JSON_VALUE({Document("\"30\"")}, '$.v' RETURNING DECIMAL)").Value.Should().Be("30", "a string of digits is parsed");
            Ask($"JSON_VALUE({Document("\" 30 \"")}, '$.v' RETURNING DECIMAL)").Value.Should().Be("30", "trimmed");

            foreach (var json in new[] { "30", "30.0", "2.5", "\"2.5\"" })
                Ask($"JSON_VALUE({Document(json)}, '$.v' RETURNING DECIMAL)").Threw.Should().BeNull($"{json} parses");

            foreach (var json in new[] { "true", "\"x\"", "\"\"" })
                Ask($"JSON_VALUE({Document(json)}, '$.v' RETURNING DECIMAL)").Threw.Should().NotBeNull($"{json} does not");

            Ask($"JSON_VALUE({Document("{\"a\":1}")}, '$.v' RETURNING DECIMAL)").Should().Match<(string? Value, string Declared, string? Runtime, string? Threw)>(
                answer => answer.Value == null && answer.Threw == null, "an object is no scalar, and answers null");

            // The value keeps its fraction, and a literal compared with it is cast to the type's scale, which is zero.
            string Compared(string json, string comparison) =>
                Ask($"CASE WHEN JSON_VALUE({Document(json)}, '$.v' RETURNING DECIMAL) {comparison} THEN 'T' ELSE 'F' END").Value!;

            Compared("2.5", "= 2.5").Should().Be("F", "2.5 is compared as the literal cast to DECIMAL(19, 0)");
            Compared("2.4", "< 2.5").Should().Be("F");
            Compared("2.4", "> 2").Should().Be("T", "and the stored value is not cast");
            Compared("30.0", "= 30").Should().Be("T");
        }

        /// <summary>
        /// What each numeric and boolean <c>RETURNING</c> does with a string, and with a number of a type other than its
        /// own: every one but <c>DECIMAL</c> casts the value the reader built to one Java class and raises on anything
        /// else, and <c>DECIMAL</c> parses.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The class is the JSON reader's, not the document's type.</b> A whole number within an <c>int</c> arrives as
        /// an <c>Integer</c>, a larger one as a <c>Long</c>, and one written with a point or an exponent as a
        /// <c>Double</c> — so <c>BIGINT</c> answers only a whole number past an <c>INTEGER</c>'s range and raises on a
        /// stored 30, <c>DOUBLE</c> and <c>FLOAT</c> answer <c>30.0</c> and raise on 30, and <c>SMALLINT</c>,
        /// <c>TINYINT</c> and <c>REAL</c>, whose classes the reader never builds, raise on every scalar there is. A
        /// string raises under all of them, a string spelling the type included, and so does a boolean under every one
        /// but <c>BOOLEAN</c>. <c>NULL ON ERROR</c> catches none of it (#81, ikvmnet/calcite-dotnet#120).
        /// </para>
        /// <para>
        /// <b>So "<c>RETURNING</c> asserts" holds of all of them but one</b>, which is what the adapter rests on where it
        /// writes a typed accessor as the path: a row Calcite answers a value for holds that type at the path, and a
        /// document holding anything else fails the statement, which the pushed type test excludes instead.
        /// <c>DECIMAL</c>, at every precision and scale, parses the trimmed text of a string and converts a number of
        /// either class, raising only where the text is no number; a boolean's rendering is not one.
        /// </para>
        /// </remarks>
        [Fact]
        public void EachNumericOrBooleanReturningAssertsItsClassButDecimal()
        {
            // The JSON values, and the Java class the reader builds for each.
            var integer = "30";             // Integer
            var large = "2147483648";       // Long
            var fraction = "30.0";          // Double
            var digits = "\"30\"";          // String, spelling a number
            var spaced = "\" 30 \"";
            var spelled = "\"true\"";       // String, spelling a boolean
            var boolean = "true";           // Boolean

            string? Answer(string type, string json)
            {
                var answer = Ask($"JSON_VALUE({Document(json)}, '$.v' RETURNING {type})");
                return answer.Threw is null ? answer.Value ?? "null" : null;
            }

            // What answers, by type; everything else raises.
            var answers = new (string Type, string[] Answered)[]
            {
                ("INTEGER", [integer]),
                ("BIGINT", [large]),
                ("SMALLINT", []),
                ("TINYINT", []),
                ("DOUBLE", [fraction]),
                ("FLOAT", [fraction]),
                ("REAL", []),
                ("BOOLEAN", [boolean]),
                ("DECIMAL", [integer, large, fraction, digits, spaced]),
                ("DECIMAL(10, 2)", [integer, large, fraction, digits, spaced]),
                ("DECIMAL(5, 1)", [integer, large, fraction, digits, spaced]),
            };

            foreach (var (type, answered) in answers)
                foreach (var json in new[] { integer, large, fraction, digits, spaced, spelled, boolean })
                {
                    if (Array.IndexOf(answered, json) >= 0)
                        Answer(type, json).Should().NotBeNull($"RETURNING {type} answers {json}");
                    else
                        Answer(type, json).Should().BeNull($"RETURNING {type} raises on {json}");
                }

            Answer("DECIMAL", digits).Should().Be("30", "the string is parsed as the number it spells");
            Answer("DECIMAL", spaced).Should().Be("30", "after trimming");
            Answer("DECIMAL(5, 1)", large).Should().Be("2147483648", "and the declared precision is not applied");

            Ask($"JSON_VALUE({Document("\"x\"")}, '$.v' RETURNING DECIMAL NULL ON ERROR)").Threw.Should().NotBeNull(
                "the parse failing is not the error ON ERROR governs");
            Ask($"JSON_VALUE({Document("\"30\"")}, '$.v' RETURNING INTEGER NULL ON ERROR)").Threw.Should().NotBeNull(
                "and neither is the assertion");
        }

        /// <summary>
        /// <c>RETURNING DECIMAL</c> is not the service's value even where the path holds a number: a stored fraction is
        /// the double's exact binary expansion, and the literal it is compared with is truncated toward zero to the
        /// comparison's scale.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reader holds a fraction as a <c>Double</c>, and the clause converts that double exactly rather than its
        /// rendering: a stored <c>0.1</c> is <c>0.1000000000000000055511151231257827…</c>, so under
        /// <c>RETURNING DECIMAL(10, 1)</c> it is not equal to <c>0.1</c> and is greater than it, where the service's
        /// doubles are equal. A stored <c>"0.1"</c> parses to 0.1 exactly. Neither is cast to the declared scale:
        /// <c>"2.255"</c> under <c>DECIMAL(10, 2)</c> equals <c>2.255</c>.
        /// </para>
        /// <para>
        /// The literal is cast to the comparison's type, and against the default <c>DECIMAL(19, 0)</c> that truncates
        /// it toward zero: <c>= 2.5</c> keeps a stored 2, <c>= -2.5</c> a stored -2 and not -3, and <c>= 0.9</c> a stored
        /// 0. It moves by less than one, which is what lets a comparison through the clause push the window
        /// <c>CosmosNumericWindow</c> states for a cast to a number.
        /// </para>
        /// </remarks>
        [Fact]
        public void ReturningDecimalReadsAStoredFractionExactlyAndTruncatesItsLiteral()
        {
            string Compared(string json, string type, string comparison) =>
                Ask($"CASE WHEN JSON_VALUE({Document(json)}, '$.v' RETURNING {type}) {comparison} THEN 'T' ELSE 'F' END").Value!;

            Compared("0.1", "DECIMAL(10, 1)", "= 0.1").Should().Be("F", "the stored double is not 0.1");
            Compared("0.1", "DECIMAL(10, 1)", "> 0.1").Should().Be("T", "it is a little more");
            Compared("\"0.1\"", "DECIMAL(10, 1)", "= 0.1").Should().Be("T", "where the string parses to 0.1 exactly");
            Compared("0.5", "DECIMAL(10, 1)", "= 0.5").Should().Be("T", "and a double that is exact is exact");
            Compared("\"2.255\"", "DECIMAL(10, 2)", "= 2.255").Should().Be("T", "the value is not cast to the declared scale");

            Compared("2", "DECIMAL", "= 2.5").Should().Be("T", "2.5 is truncated to 2");
            Compared("1", "DECIMAL", "= 1.5").Should().Be("T", "and 1.5 to 1, so it is truncation and not rounding to even");
            Compared("-2", "DECIMAL", "= -2.5").Should().Be("T", "toward zero");
            Compared("-3", "DECIMAL", "= -2.5").Should().Be("F");
            Compared("0", "DECIMAL", "= 0.9").Should().Be("T");
            Compared("-0.9", "DECIMAL", "= 0.9").Should().Be("F", "the value is not truncated");

            Ask($"JSON_VALUE({Document("0.1")}, '$.v' RETURNING DECIMAL)").Value.Should().StartWith("0.1000000000000000055", "projected, the expansion is the value");
        }

        /// <summary>
        /// A comparison through <c>RETURNING DECIMAL</c> casts the wider side: the value up where the literal has more
        /// digits than the clause, and the literal down where the two together would pass nineteen. Either cast checks
        /// the precision, and raises where it does not fit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The clause applies no precision of its own — a stored 12345 under <c>DECIMAL(5, 1)</c> is 12345 — but
        /// <c>= 12345</c> casts it to <c>DECIMAL(11, 1)</c>, which a stored 2^60 does not fit. And
        /// <c>DECIMAL(19, 18) = 50</c> casts 50 to a type with one whole digit, which raises whatever is stored, where
        /// <c>= 5</c> fits and compares. Against plain <c>DECIMAL</c> every literal is cast down, and truncated:
        /// <c>2.9999999999999999</c> is 2.
        /// </para>
        /// <para>
        /// Found by the review of the <c>RETURNING DECIMAL</c> fix. <c>CosmosNumericWindow</c> reads no window through
        /// the value's cast, and none from a literal whose cast raises.
        /// </para>
        /// </remarks>
        [Fact]
        public void AComparisonThroughReturningDecimalCastsTheWiderSide()
        {
            (string? Value, string Declared, string? Runtime, string? Threw) Compared(string json, string type, string comparison) =>
                Ask($"CASE WHEN JSON_VALUE({Document(json)}, '$.v' RETURNING {type}) {comparison} THEN 'T' ELSE 'F' END");

            Compared("12345", "DECIMAL(5, 1)", "= 12345").Value.Should().Be("T", "the clause does not apply its precision");
            Compared("1152921504606846976", "DECIMAL(5, 1)", "= 12345").Threw.Should().NotBeNull("the value cast up to DECIMAL(11, 1) does not fit");

            Compared("5", "DECIMAL(19, 18)", "= 5").Value.Should().Be("T", "5 fits one whole digit");
            Compared("50", "DECIMAL(19, 18)", "= 50").Threw.Should().NotBeNull("the literal cast down to DECIMAL(19, 18) does not");
            Compared("5", "DECIMAL(19, 18)", "= 50").Threw.Should().NotBeNull("whatever is stored");

            Compared("2", "DECIMAL", "= 2.9999999999999999").Value.Should().Be("T", "truncated to the clause's scale, however near the next");
        }

        /// <summary>
        /// A cast of the accessor to a number is a parse of its rendering, not a conversion of the number stored: Java's
        /// integer parse for <c>INTEGER</c> and <c>BIGINT</c>, and its double parse for <c>DOUBLE</c>, each over the text
        /// trimmed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// So the integer casts raise on every rendering with a point or an exponent — a stored <c>30.0</c>, <c>2.5</c>,
        /// <c>1e30</c> — and on a whole number beyond the type, where a cast of the number would have truncated or held it
        /// at the limit (<c>CosmosRexTranslator.TryNumericCastOperand</c>). A double's parse reads every rendering of a
        /// number. A string spelling a number is parsed as one, and anything else raises.
        /// </para>
        /// <para>
        /// Taken from <c>feature/predicate-oracle-checker</c> (0d9f109, "Read an integer cast of the accessor as Calcite
        /// does: a parse that raises"), whose population-D checker found it, with the test unchanged. The window a
        /// comparison through the cast pushes still holds: a whole number lands exactly, and what raises is a document
        /// the statement fails on.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACastOfTheAccessorToANumberParsesTheRendering()
        {
            Ask($"CAST(JSON_VALUE({Document("30")}, '$.v') AS INTEGER)").Value.Should().Be("30");
            Ask($"CAST(JSON_VALUE({Document("\" 30 \"")}, '$.v') AS INTEGER)").Value.Should().Be("30", "a string is trimmed and parsed");
            Ask($"CAST(JSON_VALUE({Document("2147483648")}, '$.v') AS BIGINT)").Value.Should().Be("2147483648");

            foreach (var json in new[] { "30.0", "2.5", "1e30", "2147483648", "true", "\"x\"" })
                Ask($"CAST(JSON_VALUE({Document(json)}, '$.v') AS INTEGER)").Threw.Should().NotBeNull(
                    $"an integer's parse refuses the rendering of {json}, where a cast of the number would not");

            Ask($"CAST(JSON_VALUE({Document("30.0")}, '$.v') AS BIGINT)").Threw.Should().NotBeNull("and so does a long's");

            foreach (var json in new[] { "30", "30.0", "2.5", "1e30", "\"1e30\"" })
                Ask($"CAST(JSON_VALUE({Document(json)}, '$.v') AS DOUBLE)").Threw.Should().BeNull($"a double's parse reads {json}");

            Ask($"CAST(JSON_VALUE({Document("true")}, '$.v') AS DOUBLE)").Threw.Should().NotBeNull("a boolean's rendering is no number");
            Ask($"CAST(JSON_VALUE({Document("null")}, '$.v') AS INTEGER)").Should().Match<(string? Value, string Declared, string? Runtime, string? Threw)>(
                answer => answer.Value == null && answer.Threw == null, "a JSON null renders as SQL null, which casts to null");
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
        [Fact]
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

        /// <summary>
        /// A cast of the accessor to <c>BOOLEAN</c> is a parse of the text: it reads back a stored
        /// boolean as itself, and reads a string spelling one too, which is why it is pushed only where
        /// the container says the path holds a boolean.
        /// </summary>
        /// <remarks>
        /// <c>CosmosRexTranslator.TryStoredBoolean</c> rests on both halves. Over a stored boolean the
        /// cast answers that boolean, and over a null or an absent path it answers null — so where the
        /// path holds nothing else the cast is the stored value. Over a string it parses, trimming and
        /// ignoring case, so <c>"TRUE"</c> is true here and is a string at the service; and a number
        /// raises. Neither of those is the stored value, and only a declaration rules them out.
        /// </remarks>
        [Fact]
        public void ACastToBooleanParsesTheRendering()
        {
            Ask($"CAST(JSON_VALUE({Document("true")}, '$.v') AS BOOLEAN)").Value.Should().Be("True");
            Ask($"CAST(JSON_VALUE({Document("false")}, '$.v') AS BOOLEAN)").Value.Should().Be("False");

            Ask($"CAST(JSON_VALUE({Document("null")}, '$.v') AS BOOLEAN)").Should().Match<(string? Value, string Declared, string? Runtime, string? Threw)>(
                answer => answer.Value == null && answer.Threw == null, "a JSON null is null");
            Ask("CAST(JSON_VALUE('{}', '$.v') AS BOOLEAN)").Should().Match<(string? Value, string Declared, string? Runtime, string? Threw)>(
                answer => answer.Value == null && answer.Threw == null, "and so is an absent path");

            // What a declaration rules out.
            Ask($"CAST(JSON_VALUE({Document("\"TRUE\"")}, '$.v') AS BOOLEAN)").Value.Should().Be("True", "a string spelling a boolean is parsed, in any case");
            Ask($"CAST(JSON_VALUE({Document("\" false \"")}, '$.v') AS BOOLEAN)").Value.Should().Be("False", "and trimmed");
            Ask($"CAST(JSON_VALUE({Document("1")}, '$.v') AS BOOLEAN)").Threw.Should().NotBeNull("a number raises");
        }

    }

}
