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

        const string SecondsPattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$";

        /// <summary>
        /// A container declaring <c>at</c> present, a string, and confined to one shape.
        /// </summary>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c> for none.</param>
        /// <returns>The container.</returns>
        static CosmosContainerMetadata Container(string? pattern)
        {
            var declared = pattern is null ? "" : $""", "pattern": "{pattern.Replace("\\", "\\\\")}" """;

            return new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(
                    $$"""
                    { "type": "object", "required": ["at"],
                      "properties": { "at": { "type": "string"{{declared}} } } }
                    """)));
        }

        static RelNode PlanToCosmos(string sql, CosmosContainerMetadata container)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container);

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("items", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                Adapter.Sql.CosmosOperators.Instance,
                SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
                    java.util.EnumSet.allOf(java.lang.Class.forName("org.apache.calcite.sql.fun.SqlLibrary"))));

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

    }

}
