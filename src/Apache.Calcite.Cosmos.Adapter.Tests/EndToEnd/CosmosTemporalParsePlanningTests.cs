using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using FluentAssertions;
using Xunit;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What a declared shape changes about a plan whose temporal type comes from a <c>PARSE_</c> call
    /// or a cast carrying a format rather than from a plain cast, end to end: nothing it sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A chain rather than a cast, and the chain reads leniently.</b> A cast says only that the
    /// text is to be read as an instant, and the declared shape answers whether comparing the stored
    /// strings answers what comparing the instants answers. A parse says <em>how</em> the text is to
    /// be read, in a format string, and the shape would license the stored strings in its place only
    /// if the format read every string the shape admits as the instant it denotes. Measured, none
    /// does: a shape is a run of digits per field, and every parse rolls a field past its range onto
    /// another instant, <c>2016-12-31T24:00:00Z</c> onto <c>2017-01-01T00:00:00Z</c> and
    /// <c>2016-11-61T00:00:00Z</c> onto 31 December, raising over none
    /// (<c>Calcite.ParsesReadTheirFieldsLeniently</c>, <c>CalciteTemporalLeniencyMeasurementTests</c>).
    /// So a comparison, a sort or a projection through a parse is computed in process over every
    /// shape and format, and this is where the plan is asked whether it left them there.
    /// </para>
    /// <para>
    /// <b>The harness chains the function libraries and the shared one does not</b>, which is why this
    /// is a class of its own: <c>PARSE_DATETIME</c> is a library function and does not validate
    /// against the standard table, and turning the libraries on for every planning test would change
    /// which operator a name resolves to in tests that are not about this.
    /// </para>
    /// </remarks>
    public class CosmosTemporalParsePlanningTests
    {

        /// <summary>
        /// The format that reads a seconds-precision UTC instant, as a query writes it — the SQL
        /// literal doubles every quote.
        /// </summary>
        const string Seconds = "'%Y-%m-%d''T''%H:%M:%S''Z'''";

        /// <summary>
        /// The same shape written the way a caller reaches for first, and the way that is measured to
        /// answer the wrong instant.
        /// </summary>
        const string Java = "'yyyy-MM-dd''T''HH:mm:ss''Z'''";

        /// <summary>
        /// The format for a milliseconds shape, which reads a seconds one not at all.
        /// </summary>
        const string Milliseconds = "'%Y-%m-%d''T''%H:%M:%S.%E3S''Z'''";

        const string At = """JSON_VALUE(c."DOC", '$.at')""";

        /// <summary>
        /// The standard spelling of the seconds parse, over the same accessor.
        /// </summary>
        const string CastSeconds = """CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''')""";

        const string SecondsPattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$";

        const string MillisecondsPattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$";

        /// <summary>
        /// A container declaring <c>at</c> present, a string, and confined to one shape.
        /// </summary>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c> for none.</param>
        /// <returns>The container.</returns>
        static CosmosContainerMetadata Container(string? pattern)
        {
            var declared = pattern is null ? "" : $""", "pattern": "{pattern.Replace("\\", "\\\\")}" """;

            return TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/ref" }))
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(
                    $$"""
                    { "type": "object", "required": ["at"],
                      "properties": { "at": { "type": "string"{{declared}} } } }
                    """)));
        }

        static RelNode PlanToCosmos(string sql, CosmosContainerMetadata container, bool libraries = true)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container);

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("items", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            // Without the libraries is what a model view is analyzed under, whatever the connection
            // enabled -- the configuration the standard cast spelling exists for.
            var operators = libraries
                ? org.apache.calcite.sql.util.SqlOperatorTables.chain(
                    SqlStdOperatorTable.instance(),
                    Adapter.Sql.CosmosOperators.Instance,
                    SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
                        java.util.EnumSet.allOf(java.lang.Class.forName("org.apache.calcite.sql.fun.SqlLibrary"))))
                : org.apache.calcite.sql.util.SqlOperatorTables.chain(
                    SqlStdOperatorTable.instance(),
                    Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            // To the CLR convention, so that a comparison or a sort that does not reach the service is
            // a plannable query rather than a failure -- the case this class exists to tell apart.
            foreach (var rule in Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        static RelNode FindCosmos(RelNode node)
        {
            if (node is CosmosRel)
                return node;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCosmos((RelNode)inputs.get(i)) is RelNode found)
                    return found;

            return null!;
        }

        static CosmosQuery Query(RelNode rel, CosmosContainerMetadata container)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), container);
            implementor.Visit(rel);
            return implementor.Build();
        }

        static string PlanText(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        /// <summary>
        /// A range over a parsed instant stays in process, over a shape the format reads.
        /// </summary>
        /// <remarks>
        /// The format reads the shape's canonical strings as the instants they spell, and the shape is
        /// fixed, so those strings order as their instants. The rest of the shape's strings do not:
        /// <c>2016-11-61T00:00:00Z</c> sorts before <c>2016-12-01T00:00:00Z</c> and is 31 December to
        /// the parse. So no literal is written in the container's spelling, and the
        /// <c>ClrCursorFilter</c> above the converter is the comparison computed where Calcite reads it.
        /// </remarks>
        [Fact]
        public void ARangeOverAParsedInstantStaysInProcess()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().NotContain("c.at > ", "the stored strings do not order as the instants the parse reads: " + query.Sql);

            query.Parameters.Select(p => p.Value?.ToString()).Should().NotContain("2024-02-01T00:00:00Z",
                "and no literal is written in the shape the container stores");

            PlanText(best).Should().Contain("ClrCursorFilter", "the comparison is computed in process: " + PlanText(best));
        }

        /// <summary>
        /// No comparison through a parse lowers, in either orientation, the equality among them.
        /// </summary>
        /// <remarks>
        /// The equality is the measured witness. Over <c>2017-01-01T00:00:00Z</c>,
        /// <c>2016-12-31T24:00:00Z</c> and <c>2016-12-31T23:60:00Z</c>, all three strings of the
        /// declared shape, Calcite keeps all three for <c>= TIMESTAMP '2017-01-01 00:00:00'</c>, where
        /// the stored strings' equality, which this once sent as <c>c.at = @p0</c>, kept the first
        /// alone. The reversed orientation asks the same licence, and stays in process with it.
        /// </remarks>
        [Fact]
        public void NoComparisonThroughAParseLowers()
        {
            var container = Container(SecondsPattern);

            var reversed = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE TIMESTAMP '2024-02-01 00:00:00' < PARSE_DATETIME({Seconds}, {At})""",
                container);

            Query(FindCosmos(reversed), container).Sql.Should().NotContain("c.at > ",
                "reading the operands backwards licenses nothing either");
            PlanText(reversed).Should().Contain("ClrCursorFilter", PlanText(reversed));

            var equal = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) = TIMESTAMP '2017-01-01 00:00:00'""",
                container);

            Query(FindCosmos(equal), container).Sql.Should().NotContain("c.at = ",
                "and the equality keeps what Calcite keeps only where Calcite reads it");
            PlanText(equal).Should().Contain("ClrCursorFilter", PlanText(equal));
        }

        /// <summary>
        /// The format a caller reaches for first is not pushed, because it does not read the shape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>yyyy-MM-dd'T'HH:mm:ss'Z'</c> parses without raising and answers the wrong instant —
        /// April for January, measured in <c>CalciteTemporalParseMeasurementTests</c>. So the query is
        /// already returning the wrong rows before the adapter sees it, and the only thing the adapter
        /// can do that is not worse is leave the answer where it is.
        /// </para>
        /// <para>
        /// The control beside it is a format that <em>is</em> a spelling, of a different shape: the
        /// milliseconds format over a seconds path raises for every document rather than mis-reading
        /// one.
        /// </para>
        /// <para>
        /// Every parse is declined now, a format that reads the shape included
        /// (<c>Calcite.ParsesReadTheirFieldsLeniently</c>); these keep their reasons for any later
        /// reading of a parse.
        /// </para>
        /// </remarks>
        [Fact]
        public void AFormatThatDoesNotReadTheShapeIsNotPushed()
        {
            var container = Container(SecondsPattern);

            foreach (var format in new[] { Java, Milliseconds })
            {
                var best = PlanToCosmos(
                    $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({format}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                    container);

                Query(FindCosmos(best), container).Sql.Should().NotContain("c.at > ",
                    "the format does not denote the stored shape, for " + format);

                PlanText(best).Should().Contain("ClrCursorFilter",
                    "so the comparison stays where it was, for " + format);
            }
        }

        /// <summary>
        /// And the same range over an unconfined path stays in process however the format is written.
        /// </summary>
        /// <remarks>
        /// The format is one this knows, and without a declared shape there is nothing for it to read,
        /// leniently or not.
        /// </remarks>
        [Fact]
        public void ARangeOverAnUnconfinedShapeStaysInProcess()
        {
            var container = Container(null);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            Query(FindCosmos(best), container).Sql.Should().NotContain("c.at > ", "nothing says what the stored strings look like");
            PlanText(best).Should().Contain("ClrCursorFilter");
        }

        /// <summary>
        /// Ordering by a parsed instant stays in process, and so does the parsed column.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The stored strings order as the canonical strings' instants and not as the parse orders
        /// the rest: <c>2016-11-61T00:00:00Z</c> sorts before <c>2016-12-01T00:00:00Z</c> and is
        /// 31 December to the parse. So the service is asked for no order.
        /// </para>
        /// <para>
        /// And the parsed column is not rendered as the guarded path for the reader to convert, which
        /// is what once put a <c>CosmosProject</c> under the sort: the reader would answer its own
        /// reading of a string that rolls, not the parse's. The text goes down, and the parse is
        /// computed over it in process.
        /// </para>
        /// </remarks>
        [Fact]
        public void AParsedInstantLeavesTheSortInProcess()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT PARSE_DATETIME({Seconds}, {At}) AS "at" FROM items AS c ORDER BY 1""",
                container);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().NotContain("ORDER BY", "the stored order is not the order the parse answers: " + query.Sql);
            query.Sql.Should().NotContain("\"at\": (IS_PRIMITIVE(c.at)", "and the parsed column is not the guarded path: " + query.Sql);

            PlanText(best).Should().Contain("ClrCursorSort", "the sort is computed in process: " + PlanText(best));
            PlanText(best).Should().Contain("PARSE_DATETIME", "and so is the column it sorts by, over the text sent down: " + PlanText(best));
        }

        /// <summary>
        /// And the page stays in process with the sort.
        /// </summary>
        /// <remarks>
        /// A page over a sort the service cannot give is a page of the whole container, sorted in
        /// process; the service is asked for neither.
        /// </remarks>
        [Fact]
        public void AParsedInstantLeavesThePageInProcessWithTheSort()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT PARSE_DATETIME({Seconds}, {At}) AS "at" FROM items AS c ORDER BY 1 FETCH NEXT 20 ROWS ONLY""",
                container);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().NotContain("ORDER BY", "the sort is not the service's: " + query.Sql);
            query.Sql.Should().NotContain("LIMIT 20", "and neither is the page: " + query.Sql);

            PlanText(best).Should().Contain("ClrCursorSort", "both are computed in process: " + PlanText(best));
        }

        /// <summary>
        /// Without the declaration the same sort stays in process.
        /// </summary>
        /// <remarks>
        /// It was the control of a sort the declaration pushed; every parse is declined now, and this
        /// keeps its reason for any later reading of a parse.
        /// </remarks>
        [Fact]
        public void TheSortNeedsTheDeclaration()
        {
            var container = Container(null);

            var best = PlanToCosmos(
                $"""SELECT PARSE_DATETIME({Seconds}, {At}) AS "at" FROM items AS c ORDER BY 1""",
                container);

            Query(FindCosmos(best), container).Sql.Should().NotContain("ORDER BY", "nothing licenses ordering by the stored text");
            PlanText(best).Should().Contain("ClrCursorSort");
        }

        /// <summary>
        /// And the sort is refused where the format does not read the shape, although the shape itself
        /// is as sortable as ever.
        /// </summary>
        /// <remarks>
        /// The row that separated the two conditions. The container declares a fixed shape, so
        /// <c>PreservesOrder</c> holds; the format reads the strings as some other instants, whose order
        /// is not theirs. Every parse is declined now, a format that reads the shape included, and this
        /// keeps its reason for any later reading of a parse.
        /// </remarks>
        [Fact]
        public void AFormatThatDoesNotReadTheShapeCarriesNoSort()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT PARSE_DATETIME({Java}, {At}) AS "at" FROM items AS c ORDER BY 1""",
                container);

            Query(FindCosmos(best), container).Sql.Should().NotContain("ORDER BY",
                "the shape is sortable and the format is not a reading of it");
        }

        /// <summary>
        /// A parse into a type holding less than the shape carries is refused at both sites.
        /// </summary>
        /// <remarks>
        /// <c>PARSE_DATE</c> over a path storing a full instant reads the format faithfully and then
        /// throws the clock away, so every instant on a day shares one value. Lowering the comparison
        /// onto the stored strings would ask a finer question than the query did, and ordering by them
        /// would order within a day where the key does not. Every parse is declined now, and this keeps
        /// its reason for any later reading of a parse.
        /// </remarks>
        [Fact]
        public void AParseThatTruncatesIsRefused()
        {
            var container = Container(SecondsPattern);

            var filtered = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATE({Seconds}, {At}) > DATE '2024-02-01'""",
                container);

            Query(FindCosmos(filtered), container).Sql.Should().NotContain("c.at > ", "the DATE cannot hold what the format read");

            var sorted = PlanToCosmos(
                $"""SELECT PARSE_DATE({Seconds}, {At}) AS "on" FROM items AS c ORDER BY 1""",
                container);

            Query(FindCosmos(sorted), container).Sql.Should().NotContain("ORDER BY", "and neither can the sort key");
        }

        /// <summary>
        /// A date shape parsed as a date pushes neither the comparison nor the sort, though the
        /// <c>DATE</c> holds all the shape carries.
        /// </summary>
        /// <remarks>
        /// So the refusal above is no longer about the truncation alone. A calendar date is two digits
        /// a field as well, and <c>PARSE_DATE</c> reads <c>2024-03-00</c> as 29 February and
        /// <c>2024-02-30</c> as 1 March, so the stored strings answer for the parse no more than an
        /// instant's do.
        /// </remarks>
        [Fact]
        public void ADateShapeParsedAsADatePushesNeither()
        {
            var container = Container("^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

            var filtered = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATE('%Y-%m-%d', {At}) > DATE '2024-02-01'""",
                container);

            var query = Query(FindCosmos(filtered), container);
            query.Sql.Should().NotContain("c.at > ", "the format rolls the shape's other strings: " + query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().NotContain("2024-02-01");
            PlanText(filtered).Should().Contain("ClrCursorFilter", PlanText(filtered));

            var sorted = PlanToCosmos(
                $"""SELECT PARSE_DATE('%Y-%m-%d', {At}) AS "on" FROM items AS c ORDER BY 1""",
                container);

            Query(FindCosmos(sorted), container).Sql.Should().NotContain("ORDER BY");
            PlanText(sorted).Should().Contain("ClrCursorSort", PlanText(sorted));
        }

        /// <summary>
        /// Over an instant neither the parse nor the plain cast beside it pushes, over one container
        /// and one shape.
        /// </summary>
        /// <remarks>
        /// Both queries ask the same question of the same path in the same container, and the declared
        /// shape gives both the same two bits; neither has an answer the stored strings give. The cast
        /// raises over every ISO-8601 instant, <c>Invalid DATE value</c>, and the parse reads a field
        /// past its range as another instant. So the cast stays in process where it raises, and the
        /// parse where it rolls.
        /// </remarks>
        [Fact]
        public void NeitherTheParseNorTheCastBesideItPushesAnInstant()
        {
            var container = Container(SecondsPattern);

            const string Cast = """CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP)""";

            var parsed = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            var cast = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {Cast} > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            Query(FindCosmos(parsed), container).Sql.Should().NotContain("c.at > @",
                "the format reads the shape's other strings as other instants, so the stored strings answer for nothing");

            Query(FindCosmos(cast), container).Sql.Should().NotContain("c.at > @",
                "and the cast names a reading it raises on, so there are no rows to ask for");

            PlanText(parsed).Should().Contain("ClrCursorFilter");
            PlanText(cast).Should().Contain("ClrCursorFilter");
        }

        /// <summary>
        /// And the accessor the plan types as temporal does not go down as a column either.
        /// </summary>
        /// <remarks>
        /// The projection is the site with no comparison to rewrite, so nothing stands between the
        /// path and the reader: the service sends the stored string and
        /// <c>CosmosJson</c> parses it, where the engine raises. Refused at the one place every
        /// clause reaches an accessor through — see
        /// <c>CosmosRexTranslator.RequireTheEngineCouldRead</c>.
        /// </remarks>
        [Fact]
        public void AnAccessorTypedTemporalIsNotSentDownAsAColumn()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                """SELECT JSON_VALUE(c."DOC", '$.at' RETURNING TIMESTAMP) AS "at" FROM items AS c""",
                container);

            Query(FindCosmos(best), container).Sql.Should().NotContain("\"at\": c.at",
                "the engine cannot read the string into a TIMESTAMP, so the column stays where it raises");
        }

        /// <summary>
        /// Nor does an accessor typed <c>TIME</c> over a declared time of day go down as a column.
        /// </summary>
        /// <remarks>
        /// <c>RETURNING TIME</c> asserts the extracted type rather than converting to it, and raises over
        /// every string, <c>12:30:00</c> as surely as <c>24:00:00</c>
        /// (<c>Calcite.ReturningReadsNoStoredString</c>). The bare path read back by <c>CosmosJson</c>
        /// answered a time where the query raises, so the accessor is computed in process, where it
        /// raises.
        /// </remarks>
        [Fact]
        public void AnAccessorTypedTimeOverADeclaredClockIsNotSentDownAsAColumn()
        {
            var container = Container("^[0-9]{2}:[0-9]{2}:[0-9]{2}$");

            var best = PlanToCosmos(
                """SELECT JSON_VALUE(c."DOC", '$.at' RETURNING TIME) AS "at" FROM items AS c""",
                container);

            Query(FindCosmos(best), container).Sql.Should().NotContain("\"at\": c.at",
                "the engine raises over every string read into a TIME, so the column stays where it raises");
        }

        /// <summary>
        /// A format that is not a literal is refused, whatever the document happens to hold there.
        /// </summary>
        /// <remarks>
        /// The claim a rewrite needs is about every document in the container at once — that the
        /// parse reads the declared shape — and a format read per row is not a claim about anything.
        /// A container could hold the very spelling this knows in every document and it would still be
        /// nothing the schema said.
        /// </remarks>
        [Fact]
        public void AFormatThatIsNotALiteralIsRefused()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME(JSON_VALUE(c."DOC", '$.fmt'), {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            Query(FindCosmos(best), container).Sql.Should().NotContain("c.at > ", "the format is read per row and says nothing");
            PlanText(best).Should().Contain("ClrCursorFilter");
        }

        /// <summary>
        /// <c>PARSE_TIMESTAMP</c> is refused at both sites, and the reason is its type rather than its
        /// parse.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It is the name a caller reaches for first and it reads the format exactly as
        /// <c>PARSE_DATETIME</c> does — measured. What it answers is <c>TIMESTAMP WITH LOCAL TIME
        /// ZONE</c>, and that is two refusals rather than one. A comparison between one of those and a
        /// zone-less literal is a question about the session's zone, which no declared stored form
        /// answers. And <c>CosmosJson</c> has no reading for the type, so a column of one cannot be
        /// sent down for the reader to convert, and the sort has no projection to stand on.
        /// </para>
        /// <para>
        /// Recorded as a test rather than only in <c>TODO.md</c> because the two spellings look
        /// interchangeable in a query and are not here.
        /// </para>
        /// </remarks>
        [Fact]
        public void ParseTimestampIsRefusedForItsTypeRatherThanItsParse()
        {
            var container = Container(SecondsPattern);

            var filtered = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_TIMESTAMP({Seconds}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            Query(FindCosmos(filtered), container).Sql.Should().NotContain("c.at > ",
                "the comparison is against a zone-less literal and the parse answers a zoned value");

            var sorted = PlanToCosmos(
                $"""SELECT PARSE_TIMESTAMP({Seconds}, {At}) AS "at" FROM items AS c ORDER BY 1""",
                container);

            Query(FindCosmos(sorted), container).Sql.Should().NotContain("ORDER BY",
                "and the reader has no reading for the type, so the projection does not go down either");
        }

        /// <summary>
        /// A cast carrying a format is the same parse, and a range over one stays in process with no
        /// function library enabled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the spelling ikvmnet/calcite-cosmos#170 asked for. A model view is analyzed under
        /// Calcite's default configuration whatever the connection's <c>fun</c> says, so
        /// <c>PARSE_DATETIME</c> fails to validate inside one. <c>CAST … FORMAT</c> is in the core
        /// operator table, which is why the harness runs without the libraries here.
        /// </para>
        /// <para>
        /// It reads its format with the model the <c>PARSE_</c> family does, and rolls a field past
        /// its range as they do (<c>CalciteTemporalLeniencyMeasurementTests</c> measures every
        /// spelling through both), so a range over one is computed in process as a parse's is.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACastWithAFormatStaysInProcessWithoutTheLibraries()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {CastSeconds} > TIMESTAMP '2024-02-01 00:00:00'""",
                container,
                libraries: false);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().NotContain("c.at > ", "the stored strings do not order as the instants the cast reads: " + query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().NotContain("2024-02-01T00:00:00Z");

            PlanText(best).Should().Contain("ClrCursorFilter", "the comparison is computed in process: " + PlanText(best));
        }

        /// <summary>
        /// The millisecond shape the issue was about stays in process through a cast whose type holds
        /// the fraction, as through one whose type does not.
        /// </summary>
        /// <remarks>
        /// A bare <c>TIMESTAMP</c> is <c>TIMESTAMP(0)</c>, so a cast into one promises a value with no
        /// fraction whatever its format read, and every stored instant within a second is promised
        /// the same one. <c>TIMESTAMP(3)</c> holds what <c>FF3</c> reads, and that no longer separates
        /// the two: the cast still rolls a field past its range, so the equality is computed in process
        /// into either type.
        /// </remarks>
        [Fact]
        public void ACastWithAFormatLeavesTheMillisecondShapeInProcessWhateverItsType()
        {
            var container = Container(MillisecondsPattern);

            const string Format = "'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z'''";

            var held = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(3) FORMAT {Format}) = TIMESTAMP '2024-02-01 00:00:00.123'""",
                container,
                libraries: false);

            var query = Query(FindCosmos(held), container);

            query.Sql.Should().NotContain("c.at = ", "TIMESTAMP(3) holds what FF3 reads, and the cast rolls the shape's other strings: " + query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().NotContain("2024-02-01T00:00:00.123Z");
            PlanText(held).Should().Contain("ClrCursorFilter", PlanText(held));

            var truncated = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP FORMAT {Format}) = TIMESTAMP '2024-02-01 00:00:00.123'""",
                container,
                libraries: false);

            Query(FindCosmos(truncated), container).Sql.Should().NotContain("c.at = @",
                "a TIMESTAMP(0) cannot hold what the format read");
            PlanText(truncated).Should().Contain("ClrCursorFilter");
        }

        /// <summary>
        /// And the cast leaves the sort in process, as the parse does.
        /// </summary>
        [Fact]
        public void ACastWithAFormatLeavesTheSortInProcess()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT {CastSeconds} AS "at" FROM items AS c ORDER BY 1""",
                container,
                libraries: false);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().NotContain("ORDER BY", "the stored order is not the order the cast answers: " + query.Sql);
            query.Sql.Should().NotContain("\"at\": (IS_PRIMITIVE(c.at)", "and the cast column is not the guarded path: " + query.Sql);

            PlanText(best).Should().Contain("ClrCursorSort", "the sort is computed in process: " + PlanText(best));
            PlanText(best).Should().Contain("ClrCursorProject", "and so is the column it sorts by, over the text sent down: " + PlanText(best));
        }

        /// <summary>
        /// A cast whose format does not read the shape is refused on the same terms as a parse.
        /// </summary>
        /// <remarks>
        /// The millisecond format over a seconds path, and <c>FF</c> over a millisecond path — which
        /// the model reads as no fraction at all, measured, so every instant within a second reads as
        /// the same one and the parse is not injective.
        /// </remarks>
        [Fact]
        public void ACastWhoseFormatDoesNotReadTheShapeIsNotPushed()
        {
            foreach (var (pattern, format) in new[]
            {
                (SecondsPattern, "YYYY-MM-DD''T''HH24:MI:SS.FF3''Z''"),
                (MillisecondsPattern, "YYYY-MM-DD''T''HH24:MI:SS.FF''Z''"),
            })
            {
                var container = Container(pattern);

                var best = PlanToCosmos(
                    $"""SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP FORMAT '{format}') > TIMESTAMP '2024-02-01 00:00:00'""",
                    container,
                    libraries: false);

                Query(FindCosmos(best), container).Sql.Should().NotContain("c.at > ",
                    "the format does not denote the stored shape, for " + format);

                PlanText(best).Should().Contain("ClrCursorFilter", "so the comparison stays where it was, for " + format);
            }
        }

        /// <summary>
        /// A cast with a format into a <c>DATE</c> over an instant truncates, and is refused like
        /// <c>PARSE_DATE</c>.
        /// </summary>
        [Fact]
        public void ACastWithAFormatThatTruncatesIsRefused()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                """SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.at') AS DATE FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''') > DATE '2024-02-01'""",
                container,
                libraries: false);

            Query(FindCosmos(best), container).Sql.Should().NotContain("c.at > ", "the DATE cannot hold what the format read");
        }

        /// <summary>
        /// The format for the milliseconds shape, in the standard cast's spelling.
        /// </summary>
        const string CastMilliseconds = """CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(3) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z''')""";

        /// <summary>
        /// Plans a comparison against a parameter without the libraries, as a model view is analyzed,
        /// and renders it.
        /// </summary>
        static (CosmosQuery Query, string Plan) PlanParameter(string predicate, string? pattern)
        {
            var container = Container(pattern);
            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE {predicate}""", container, libraries: false);

            return (Query(FindCosmos(best), container), PlanText(best));
        }

        /// <summary>
        /// The slots a statement leaves for a parameter written in a stored form.
        /// </summary>
        static System.Collections.Generic.IEnumerable<Adapter.Sql.CosmosDynamicValue> StoredSlots(CosmosQuery query) =>
            query.Parameters.Select(p => p.Value).OfType<Adapter.Sql.CosmosDynamicValue>().Where(slot => slot.Form is not null);

        /// <summary>
        /// The comparison #182 reported, a stored instant read through the cast the issue names against
        /// <c>CAST(? AS TIMESTAMP)</c>, stays in process, and no slot is left for the value to be written
        /// into the stored spelling.
        /// </summary>
        /// <remarks>
        /// The cast carries a format, so it is a parse, and rolls a field past its range as every parse
        /// does: the stored strings answer for it against a parameter no more than against a literal.
        /// </remarks>
        [Fact]
        public void AComparisonAgainstAParameterStaysInProcess()
        {
            var (query, plan) = PlanParameter($"{CastMilliseconds} > CAST(? AS TIMESTAMP)", MillisecondsPattern);

            query.Sql.Should().NotContain("(c.at > @p0)", "the stored strings do not order as the instants the cast reads: " + query.Sql);
            plan.Should().Contain("ClrCursorFilter", "the comparison is computed in process: " + plan);

            StoredSlots(query).Should().BeEmpty("no value is written in the stored spelling");
        }

        /// <summary>
        /// No comparison against a parameter lowers, whichever side the parameter is read on.
        /// </summary>
        [Theory]
        [InlineData("{0} = ?", "(c.at = @p0)")]
        [InlineData("{0} <> ?", "(c.at != @p0)")]
        [InlineData("{0} >= ?", "(c.at >= @p0)")]
        [InlineData("{0} < CAST(? AS TIMESTAMP(3))", "(c.at < @p0)")]
        [InlineData("? < {0}", "(c.at > @p0)")]
        [InlineData("? >= {0}", "(c.at <= @p0)")]
        public void NoComparisonAgainstAParameterLowers(string shape, string rendered)
        {
            var (query, plan) = PlanParameter(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, CastMilliseconds), MillisecondsPattern);

            query.Sql.Should().NotContain(rendered, query.Sql);
            plan.Should().Contain("ClrCursorFilter", plan);
        }

        /// <summary>
        /// A parameter compared with a parsed seconds shape is written into no spelling, whatever the
        /// comparison, and the comparison is computed in process.
        /// </summary>
        /// <remarks>
        /// These comparisons were where a value between two stored spellings was rounded onto the form
        /// in the direction the comparison does not see. With no comparison through a parse lowered,
        /// no value is rounded or written for one.
        /// </remarks>
        [Theory]
        [InlineData(">")]
        [InlineData("<=")]
        [InlineData(">=")]
        [InlineData("<")]
        [InlineData("=")]
        [InlineData("<>")]
        public void AParameterComparedWithAParseIsWrittenIntoNoSpelling(string op)
        {
            var (query, plan) = PlanParameter($"{CastSeconds} {op} CAST(? AS TIMESTAMP)", SecondsPattern);

            StoredSlots(query).Should().BeEmpty("no value is written in the stored spelling for " + op);
            plan.Should().Contain("ClrCursorFilter", plan);
        }

        /// <summary>
        /// A parameter against a path whose form the cast does not read, or that declares none, stays in
        /// process as it did.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(SecondsPattern)]
        public void AParameterTheFormDoesNotLicenseIsNotPushed(string? pattern)
        {
            var (query, plan) = PlanParameter($"{CastMilliseconds} > CAST(? AS TIMESTAMP)", pattern);

            query.Sql.Should().NotContain("c.at > @", query.Sql);
            plan.Should().Contain("ClrCursorFilter", plan);
        }

        /// <summary>
        /// A <c>DATE</c> parameter against a calendar date parsed with a format stays in process too, and
        /// is written into no spelling.
        /// </summary>
        /// <remarks>
        /// The cast's format reads <c>2024-03-00</c> as 29 February, as every parse of the date shape
        /// does.
        /// </remarks>
        [Fact]
        public void ADateParameterAgainstAParsedDateStaysInProcess()
        {
            var (query, plan) = PlanParameter("""CAST(JSON_VALUE(c."DOC", '$.at') AS DATE FORMAT 'YYYY-MM-DD') = CAST(? AS DATE)""", "^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

            query.Sql.Should().NotContain("(c.at = @p0)", query.Sql);
            plan.Should().Contain("ClrCursorFilter", plan);

            StoredSlots(query).Should().BeEmpty("no value is written in the stored spelling");
        }

        /// <summary>The declared calendar date, which a plain cast reads.</summary>
        const string DatePattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$";

        /// <summary>
        /// The statement as it runs with <c>?0</c> holding the given value: the stored slot's value as written.
        /// </summary>
        static object? Bound(CosmosQuery query, object value) =>
            Adapter.Client.CosmosQueries.Bind(query, new Context(value)).Parameters.Single(p => p.Value is string).Value;

        /// <summary>The epoch milliseconds Calcite's runtime holds a <c>TIMESTAMP</c> parameter in.</summary>
        static java.lang.Long EpochMillis(string utc) =>
            java.lang.Long.valueOf(System.DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds());

        /// <summary>
        /// A calendar date read with a plain cast, compared with a literal on the left, is sent with the operator reversed:
        /// read backwards and kept as written, <c>&lt;</c> would select the complement.
        /// </summary>
        /// <remarks>
        /// The plain cast is what still lowers onto the stored strings, so it carries the pins the parse used to. The 15th
        /// is no month's last day, where the day zero parts the strings from the dates
        /// (<c>Calcite.DateCastReadsDayZeroAsTheMonthBefore</c>).
        /// </remarks>
        [Fact]
        public void ADateCastWithTheLiteralOnTheLeftIsSentWithTheOperatorReversed()
        {
            var container = Container(DatePattern);

            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE DATE '2024-01-15' < CAST({At} AS DATE)""", container);
            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("(c.at > @p0)", "reading the operands backwards reverses the operator: " + query.Sql);
            query.Parameters.Select(p => p.Value).Should().Contain("2024-01-15", "the literal is written as the stored date");
            PlanText(best).Should().NotContain("ClrCursorFilter", "with nothing left to recheck");
        }

        /// <summary>
        /// A <c>TIMESTAMP</c> parameter against a calendar date read with a plain cast is rounded onto the date in the
        /// direction the comparison does not see, and written as the stored date when the statement runs.
        /// </summary>
        /// <remarks>
        /// Noon of the 15th lies between the 15th's spelling and the 16th's: <c>&gt;</c> keeps what <c>&gt; 15th</c>
        /// keeps and <c>&gt;=</c> what <c>&gt;= 16th</c> does. A value that lands is written as it is.
        /// </remarks>
        [Theory]
        [InlineData(">", "(c.at > @p0)", "2024-01-15")]
        [InlineData("<=", "(c.at <= @p0)", "2024-01-15")]
        [InlineData(">=", "(c.at >= @p0)", "2024-01-16")]
        [InlineData("<", "(c.at < @p0)", "2024-01-16")]
        public void AParameterAgainstADateCastIsRoundedTheWayTheComparisonNeeds(string op, string rendered, string written)
        {
            var (query, plan) = PlanParameter($"""CAST({At} AS TIMESTAMP) {op} CAST(? AS TIMESTAMP)""", DatePattern);

            query.Sql.Should().Contain(rendered, query.Sql);
            plan.Should().NotContain("ClrCursorFilter", plan);
            StoredSlots(query).Single().Form.Should().Be(CosmosTemporalForms.Iso8601Date, "the slot carries the spelling to write the value in");

            Bound(query, EpochMillis("2024-01-15T12:00:00Z")).Should().Be(written);
            Bound(query, EpochMillis("2024-01-15T00:00:00Z")).Should().Be("2024-01-15", "and a value that lands is written as it is");
        }

        /// <summary>
        /// A <c>DATE</c> parameter against a calendar date read with a plain cast is written as the stored date, from the
        /// days since the epoch Calcite holds it in.
        /// </summary>
        [Fact]
        public void ADateParameterAgainstADateCastIsWrittenAsTheStoredDate()
        {
            var (query, plan) = PlanParameter($"""CAST({At} AS DATE) < CAST(? AS DATE)""", DatePattern);

            query.Sql.Should().Contain("(c.at < @p0)", query.Sql);
            plan.Should().NotContain("ClrCursorFilter", plan);

            Bound(query, java.lang.Integer.valueOf((int)(new System.DateTime(2026, 8, 2) - System.DateTime.UnixEpoch).TotalDays))
                .Should().Be("2026-08-02");
        }

        /// <summary>
        /// A calendar date, declared.
        /// </summary>
        const string DatePattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$";

        /// <summary>
        /// A time of day to the second, declared.
        /// </summary>
        const string TimePattern = "^[0-9]{2}:[0-9]{2}:[0-9]{2}$";

        /// <summary>
        /// A <c>DATE</c>, <c>TIME</c> or <c>TIMESTAMP</c> parameter compared with a <c>RETURNING</c> accessor over a path
        /// declared to spell one is never sent, as no literal of one is: Calcite holds it as a count of days or
        /// milliseconds, which no stored spelling is. The comparison is computed in process, and a conjunct beside it is
        /// still sent.
        /// </summary>
        /// <remarks>
        /// <c>JSON_VALUE(… RETURNING DATE) = ?</c> was sent as <c>c.at = @p0</c>, bound to the days Calcite holds the
        /// date in, which no stored string equals, and <c>&lt;&gt;</c> kept every document, where Calcite's plan fails
        /// reading the string (<see cref="CosmosConnectionPlanningTests"/>).
        /// </remarks>
        [Theory]
        [InlineData("""JSON_VALUE(c."DOC", '$.at' RETURNING DATE) = ?""", DatePattern)]
        [InlineData("""JSON_VALUE(c."DOC", '$.at' RETURNING DATE) <> ?""", DatePattern)]
        [InlineData("""? = JSON_VALUE(c."DOC", '$.at' RETURNING DATE) AND c."id" = 'a'""", DatePattern)]
        [InlineData("""JSON_VALUE(c."DOC", '$.at' RETURNING TIMESTAMP) IN (?, ?) AND c."id" = 'a'""", DatePattern)]
        [InlineData("""JSON_VALUE(c."DOC", '$.at' RETURNING TIME) = ? OR c."id" = 'a'""", TimePattern)]
        [InlineData("""JSON_VALUE(c."DOC", '$.at' RETURNING TIME) <> ? AND c."id" = 'a'""", TimePattern)]
        public void ADateOrTimeParameterIsNeverSentAsTheCountCalciteHoldsItIn(string predicate, string pattern)
        {
            var (query, _) = PlanParameter(predicate, pattern);

            query.Parameters.Select(p => p.Value).OfType<Adapter.Sql.CosmosDynamicValue>().Should().BeEmpty("no slot holds the count: " + query.Sql);
            if (predicate.Contains(" AND ", System.StringComparison.Ordinal))
                query.Sql.Should().Contain("c.id = @p", "the conjunct beside it is sent");
        }

        /// <summary>
        /// A <c>DATE</c> or <c>TIMESTAMP</c> parameter compared with the cast of a path declared to spell a date is
        /// written in that spelling, through a slot of its own: the parameter is never sent as its count, and the
        /// comparison still is.
        /// </summary>
        [Theory]
        [InlineData("""CAST(JSON_VALUE(c."DOC", '$.at') AS DATE) = ?""", "(c.at = @p0)")]
        [InlineData("""CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP) <> ?""", "(c.at != @p0)")]
        public void ADateParameterComparedWithTheDeclaredSpellingsCastIsWrittenInIt(string predicate, string rendered)
        {
            var (query, plan) = PlanParameter(predicate, DatePattern);

            query.Sql.Should().Contain(rendered, query.Sql);
            plan.Should().NotContain("ClrCursorFilter", plan);
            Slot(query).Form.Should().Be(CosmosTemporalForms.Iso8601Date, "the slot carries the spelling to write the value in");
        }

        /// <summary>
        /// A data context holding the one value a run supplies.
        /// </summary>
        sealed class Context : org.apache.calcite.DataContext
        {

            readonly object _value;

            public Context(object value) => _value = value;

            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            public org.apache.calcite.adapter.java.JavaTypeFactory getTypeFactory() => null!;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => name == "?0" ? _value : null!;

        }

    }

}
