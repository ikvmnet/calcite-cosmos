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
    /// rather than from a cast, end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A chain rather than a cast, which is the whole difference.</b> A cast says only that the
    /// text is to be read as an instant, and the declared shape answers whether comparing the stored
    /// strings answers what comparing the instants answers. A parse says <em>how</em> the text is to
    /// be read, in a format string — so the same declared shape licenses nothing until the format is
    /// known to read it. <c>CalciteTemporalParseMeasurementTests</c> is where that is measured; this
    /// is where the plan is asked whether it acted on it.
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

            return new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(
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
        /// A range over a parsed instant reaches the statement, with the literal written in the
        /// container's own spelling.
        /// </summary>
        /// <remarks>
        /// The whole predicate leaves as one statement. A <c>ClrCursorFilter</c> above the
        /// converter would mean the comparison was rechecked in process, which is what happened before
        /// the chain was recognised — and before it, the statement asked only <c>IS_DEFINED</c> and
        /// the container came back whole.
        /// </remarks>
        [Fact]
        public void ARangeOverAParsedInstantReachesTheStatement()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.at > @", "the parse is dropped and the comparison is the stored strings': " + query.Sql);

            query.Parameters.Select(p => p.Value?.ToString()).Should().Contain("2024-02-01T00:00:00Z",
                "and the literal is written in the shape the container stores");

            PlanText(best).Should().NotContain("ClrCursorFilter", "with nothing left to recheck: " + PlanText(best));
        }

        /// <summary>
        /// An equality over a parsed instant lowers on the other bit, and both orientations of the
        /// comparison lower.
        /// </summary>
        /// <remarks>
        /// The reversed orientation is the case that would be silently wrong rather than merely
        /// unpushed: read backwards without reversing the operator, <c>&lt; PARSE_DATETIME(…)</c>
        /// selects the complement. It goes through the same machinery the cast spelling does, so this
        /// pins that the chain did not find a way around it.
        /// </remarks>
        [Fact]
        public void BothOrientationsLower()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE TIMESTAMP '2024-02-01 00:00:00' < PARSE_DATETIME({Seconds}, {At})""",
                container);

            Query(FindCosmos(best), container).Sql.Should().Contain("c.at > @",
                "reading the operands backwards reverses the operator");

            var equal = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) = TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            Query(FindCosmos(equal), container).Sql.Should().Contain("c.at = @", "and the equality lowers on its own bit");
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
        /// one, and is refused by the same test rather than by a second one.
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
        /// The control that makes the pushed case a licence rather than an accident: the format is one
        /// this knows, and without a declared shape there is nothing for it to read.
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
        /// Ordering by a parsed instant reaches the service, which is the larger of the two changes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two things had to happen and the first is easy to miss. The projection has to render, or
        /// there is no <c>CosmosProject</c> under the sort to record the binding on — and Calcite does
        /// not transpose a sort through a projection whose key is a function call, the way it does
        /// through a cast, so the sort cannot get past it either. Measured before: a
        /// <c>ClrCursorSort</c> over a <c>ClrCursorProject</c> over the bare accessor, with
        /// the whole container read to feed it.
        /// </para>
        /// <para>
        /// The second is the binding itself, which is the same two-bit question a cast's sort asks
        /// with the format's question added to it.
        /// </para>
        /// </remarks>
        [Fact]
        public void AParsedInstantCarriesTheSort()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT PARSE_DATETIME({Seconds}, {At}) AS "at" FROM items AS c ORDER BY 1""",
                container);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("ORDER BY c.at", "the stored order is the order the parse answers: " + query.Sql);
            query.Sql.Should().Contain("(IS_PRIMITIVE(c.at) ? c.at : null)", "and the column is the guarded path: " + query.Sql);

            PlanText(best).Should().NotContain("ClrCursorSort", "with nothing left to sort in process: " + PlanText(best));
        }

        /// <summary>
        /// And the page goes with it, which is the number that makes the entry worth having.
        /// </summary>
        /// <remarks>
        /// A sort left in process is a container read whole to produce twenty rows. Pushed, the
        /// service orders and pages and sends twenty documents — the same difference the rendered
        /// <c>UUID</c> column bought for a catalog page, and the shape <c>README.md</c> shows.
        /// </remarks>
        [Fact]
        public void AParsedInstantCarriesThePageWithTheSort()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT PARSE_DATETIME({Seconds}, {At}) AS "at" FROM items AS c ORDER BY 1 FETCH NEXT 20 ROWS ONLY""",
                container);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("ORDER BY c.at", "the sort is the service's: " + query.Sql);
            query.Sql.Should().Contain("LIMIT 20", "and so is the page: " + query.Sql);

            PlanText(best).Should().NotContain("ClrCursorSort", "with nothing left in process: " + PlanText(best));
        }

        /// <summary>
        /// The control: without the declaration the same sort stays in process.
        /// </summary>
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
        /// The row that separates the two conditions. The container declares a fixed shape, so
        /// <c>PreservesOrder</c> holds and a cast's sort over the same path would push; what declines
        /// this one is only that the format reads the strings as some other instants, whose order is
        /// not theirs.
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
        /// would order within a day where the key does not.
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
        /// A date shape parsed as a date pushes both, which is the row that says the refusal above is
        /// about the truncation rather than about <c>PARSE_DATE</c>.
        /// </summary>
        [Fact]
        public void ADateShapeParsedAsADatePushesBoth()
        {
            var container = Container("^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

            var filtered = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATE('%Y-%m-%d', {At}) > DATE '2024-02-01'""",
                container);

            var query = Query(FindCosmos(filtered), container);
            query.Sql.Should().Contain("c.at > @", "the format reads the shape and the DATE holds it: " + query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().Contain("2024-02-01");

            var sorted = PlanToCosmos(
                $"""SELECT PARSE_DATE('%Y-%m-%d', {At}) AS "on" FROM items AS c ORDER BY 1""",
                container);

            Query(FindCosmos(sorted), container).Sql.Should().Contain("ORDER BY c.at");
        }

        /// <summary>
        /// The parse pushes and the cast beside it does not, over one container and one shape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the whole argument for the parse in one row.</b> Both queries ask the same
        /// question of the same path in the same container, and the declared shape gives both the
        /// same two bits. What separates them is whether Calcite can evaluate the expression: it
        /// reads a format it was given, and it raises on <c>CAST(&lt;iso&gt; AS TIMESTAMP)</c> —
        /// measured, <c>Invalid DATE value</c>, for every ISO-8601 instant.
        /// </para>
        /// <para>
        /// A pushdown has to answer what the engine would have answered. Over the cast there is no
        /// such answer, so pushing it would not be an optimisation — it would hand the caller rows
        /// their query cannot produce. The parse has an answer, and the pushed plan gives that one.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheParsePushesWhereTheCastBesideItCannot()
        {
            var container = Container(SecondsPattern);

            const string Cast = """CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP)""";

            var parsed = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE PARSE_DATETIME({Seconds}, {At}) > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            var cast = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {Cast} > TIMESTAMP '2024-02-01 00:00:00'""",
                container);

            Query(FindCosmos(parsed), container).Sql.Should().Contain("c.at > @",
                "the format names a reading the engine performs, so the service can be asked for the same rows");

            Query(FindCosmos(cast), container).Sql.Should().NotContain("c.at > @",
                "and the cast names one it raises on, so there are no rows to ask for");

            PlanText(parsed).Should().NotContain("ClrCursorFilter");
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
        /// A cast carrying a format is the same parse, and a range over one reaches the statement
        /// with no function library enabled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the spelling ikvmnet/calcite-cosmos#170 asked for. A model view is analyzed under
        /// Calcite's default configuration whatever the connection's <c>fun</c> says, so
        /// <c>PARSE_DATETIME</c> fails to validate inside one and a view had no instant that pushed.
        /// <c>CAST … FORMAT</c> is in the core operator table, which is why the harness runs without
        /// the libraries here.
        /// </para>
        /// <para>
        /// The format is read against the declared shape by the same table the <c>PARSE_</c> family
        /// uses — <c>CalciteTemporalParseMeasurementTests</c> measures every spelling through both.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACastWithAFormatReachesTheStatementWithoutTheLibraries()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {CastSeconds} > TIMESTAMP '2024-02-01 00:00:00'""",
                container,
                libraries: false);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.at > @", "the cast is dropped and the comparison is the stored strings': " + query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().Contain("2024-02-01T00:00:00Z");

            PlanText(best).Should().NotContain("ClrCursorFilter", "with nothing left to recheck: " + PlanText(best));
        }

        /// <summary>
        /// The millisecond shape the issue was about pushes through a cast whose type holds the
        /// fraction, and not through one whose type does not.
        /// </summary>
        /// <remarks>
        /// A bare <c>TIMESTAMP</c> is <c>TIMESTAMP(0)</c>, so a cast into one promises a value with no
        /// fraction whatever its format read, and every stored instant within a second is promised
        /// the same one. Calcite's runtime keeps the milliseconds anyway, measured — but the plan
        /// widens the cast back to <c>TIMESTAMP(3)</c> to compare it, and what a pushdown has to agree
        /// with is what the query says rather than what the runtime happens to do.
        /// </remarks>
        [Fact]
        public void ACastWithAFormatReadsTheMillisecondShapeIntoATypeThatHoldsIt()
        {
            var container = Container(MillisecondsPattern);

            const string Format = "'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z'''";

            var held = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(3) FORMAT {Format}) = TIMESTAMP '2024-02-01 00:00:00.123'""",
                container,
                libraries: false);

            var query = Query(FindCosmos(held), container);

            query.Sql.Should().Contain("c.at = @", "FF3 reads a three-digit fraction exactly and TIMESTAMP(3) holds it: " + query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().Contain("2024-02-01T00:00:00.123Z");

            var truncated = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP FORMAT {Format}) = TIMESTAMP '2024-02-01 00:00:00.123'""",
                container,
                libraries: false);

            Query(FindCosmos(truncated), container).Sql.Should().NotContain("c.at = @",
                "a TIMESTAMP(0) cannot hold what the format read");
            PlanText(truncated).Should().Contain("ClrCursorFilter");
        }

        /// <summary>
        /// And the cast carries the sort, as the parse does.
        /// </summary>
        [Fact]
        public void ACastWithAFormatCarriesTheSort()
        {
            var container = Container(SecondsPattern);

            var best = PlanToCosmos(
                $"""SELECT {CastSeconds} AS "at" FROM items AS c ORDER BY 1""",
                container,
                libraries: false);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("ORDER BY c.at", "the stored order is the order the cast answers: " + query.Sql);
            query.Sql.Should().Contain("(IS_PRIMITIVE(c.at) ? c.at : null)", "and the column is the guarded path: " + query.Sql);

            PlanText(best).Should().NotContain("ClrCursorSort", "with nothing left to sort in process: " + PlanText(best));
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
        /// The slot a statement leaves for a parameter written in a stored form.
        /// </summary>
        static Adapter.Sql.CosmosDynamicValue Slot(CosmosQuery query) =>
            query.Parameters.Select(p => p.Value).OfType<Adapter.Sql.CosmosDynamicValue>().Single();

        /// <summary>
        /// The statement as it runs with <c>?0</c> holding the given value.
        /// </summary>
        static object? Bound(CosmosQuery query, object value) =>
            Adapter.Client.CosmosQueries.Bind(query, new Context(value)).Parameters.Single(p => p.Value is string).Value;

        /// <summary>
        /// The epoch milliseconds Calcite's runtime holds a <c>TIMESTAMP</c> parameter in.
        /// </summary>
        static java.lang.Long Millis(string utc) =>
            java.lang.Long.valueOf(System.DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds());

        /// <summary>
        /// The comparison #182 reported: a stored instant read through the cast the issue names, against
        /// <c>CAST(? AS TIMESTAMP)</c>, pushes — and the value is written in the stored spelling when it
        /// arrives.
        /// </summary>
        /// <remarks>
        /// <c>CAST(? AS TIMESTAMP)</c> compared with a <c>TIMESTAMP(3)</c> is
        /// <c>CAST(CAST(?0):TIMESTAMP(0)):TIMESTAMP(3)</c> by the time it is planned, and the casts change
        /// nothing at run time — <c>CalciteTemporalParameterMeasurementTests</c> measures that the
        /// milliseconds survive the <c>TIMESTAMP(0)</c> — so the value written is the one the parameter
        /// carries.
        /// </remarks>
        [Fact]
        public void AComparisonAgainstAParameterReachesTheStatement()
        {
            var (query, plan) = PlanParameter($"{CastMilliseconds} > CAST(? AS TIMESTAMP)", MillisecondsPattern);

            query.Sql.Should().Contain("(c.at > @p0)", "the comparison is the stored strings': " + query.Sql);
            plan.Should().NotContain("ClrCursorFilter", "with nothing left to recheck: " + plan);

            var slot = Slot(query);
            slot.Ordinal.Should().Be(0);
            slot.Form.Should().Be(CosmosTemporalForms.Iso8601UtcMilliseconds, "the slot carries the spelling to write the value in");

            Bound(query, Millis("2026-08-02T12:00:00.123Z")).Should().Be("2026-08-02T12:00:00.123Z",
                "and the value is written in it when the statement runs");
        }

        /// <summary>
        /// Every comparison lowers on its own bit, and reading the parameter on the left reverses the
        /// operator — the case that would select the complement rather than merely not push.
        /// </summary>
        [Theory]
        [InlineData("{0} = ?", "(c.at = @p0)")]
        [InlineData("{0} <> ?", "(c.at != @p0)")]
        [InlineData("{0} >= ?", "(c.at >= @p0)")]
        [InlineData("{0} < CAST(? AS TIMESTAMP(3))", "(c.at < @p0)")]
        [InlineData("? < {0}", "(c.at > @p0)")]
        [InlineData("? >= {0}", "(c.at <= @p0)")]
        public void EveryComparisonAgainstAParameterLowers(string shape, string rendered)
        {
            var (query, plan) = PlanParameter(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, CastMilliseconds), MillisecondsPattern);

            query.Sql.Should().Contain(rendered, query.Sql);
            plan.Should().NotContain("ClrCursorFilter", plan);
        }

        /// <summary>
        /// A value between two stored spellings is rounded onto the form in the direction the comparison
        /// does not see, so the statement answers what the comparison against the value answers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Against a seconds shape, half a second past noon. Nothing is stored between <c>12:00:00Z</c>
        /// and <c>12:00:01Z</c>, so <c>&gt;</c> keeps what <c>&gt; 12:00:00Z</c> keeps and <c>&gt;=</c>
        /// what <c>&gt;= 12:00:01Z</c> does; an equality is true of no stored value and an inequality of
        /// every one, which the value written in full — at a precision no spelling has — gives both.
        /// </para>
        /// <para>
        /// A literal that does not land is refused instead, and its comparison stays in process; a
        /// parameter's statement is already written by the time its value is known.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(">", "2026-08-02T12:00:00Z")]
        [InlineData("<=", "2026-08-02T12:00:00Z")]
        [InlineData(">=", "2026-08-02T12:00:01Z")]
        [InlineData("<", "2026-08-02T12:00:01Z")]
        [InlineData("=", "2026-08-02T12:00:00.5000000Z")]
        [InlineData("<>", "2026-08-02T12:00:00.5000000Z")]
        public void AParameterBetweenTwoSpellingsIsRoundedTheWayTheComparisonNeeds(string op, string written)
        {
            var (query, _) = PlanParameter($"{CastSeconds} {op} CAST(? AS TIMESTAMP)", SecondsPattern);

            Bound(query, Millis("2026-08-02T12:00:00.500Z")).Should().Be(written);
            Bound(query, Millis("2026-08-02T12:00:00.000Z")).Should().Be("2026-08-02T12:00:00Z", "and a value that lands is written as it is");
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
        /// A <c>DATE</c> parameter against a calendar date lowers too, the days Calcite holds it in
        /// written as the date.
        /// </summary>
        [Fact]
        public void ADateParameterIsWrittenAsTheStoredDate()
        {
            var (query, plan) = PlanParameter("""CAST(JSON_VALUE(c."DOC", '$.at') AS DATE FORMAT 'YYYY-MM-DD') = CAST(? AS DATE)""", "^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

            query.Sql.Should().Contain("(c.at = @p0)", query.Sql);
            plan.Should().NotContain("ClrCursorFilter", plan);

            Bound(query, java.lang.Integer.valueOf((int)(new System.DateTime(2026, 8, 2) - System.DateTime.UnixEpoch).TotalDays))
                .Should().Be("2026-08-02");
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
