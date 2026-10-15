using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

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
using Xunit;
using Xunit.Sdk;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// Drives the Volcano planner over real SQL with the Cosmos rule set registered, asserting that
    /// the planner actually selects the Cosmos nodes.
    /// </summary>
    /// <remarks>
    /// The rule predicates and each node's rendering are covered elsewhere. What is checked here is
    /// the step between them: that the planner reaches a plan wholly in the Cosmos convention, and
    /// that the plan renders to the expected statement.
    /// </remarks>
    public class CosmosPlannerTests
    {

        /// <remarks>
        /// The full text and vector paths are declared because the declaration is what decides the
        /// vector function's legality and the full text functions' price: a container that says
        /// nothing about a path is one whose full text predicate the service answers by scanning,
        /// and whose <c>VECTORDISTANCE</c> it refuses. Every statement here that names one names a
        /// declared path, and <see cref="AFullTextPredicateOverAnUndeclaredPathIsPushedDownAndPricedAsAScan"/>
        /// is the other half.
        /// </remarks>
        static readonly CosmosContainerMetadata Products = new(
            "products",
            new[] { "/category" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/id", false),
                    new CosmosCompositeIndexPath("/_ts", false),
                }),
            },
            fullTextPaths: new[] { "/name", "/tags" },
            vectorPaths: new[] { "/a" });

        CosmosTable _table = null!;

        public CosmosPlannerTests()
        {
            _table = new CosmosTable(Products);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _table);


            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            // Chained so that a query can name the adapter's own functions. Calcite's standard table has
            // nothing to resolve IS_DEFINED or FULLTEXTCONTAINS to, and this is the seam a caller wires
            // the same way.
            //
            // The shared full text table is chained beside it, which is what a host assembling its own
            // planner does -- and this harness is one. A connection takes the other route instead,
            // CosmosSchema declaring the same names where the catalog reader looks; that one is
            // CosmosConnectionFunctionTests. One route or the other and never both, for the reason the
            // package records: two candidates for one name reach a type-precedence pass a single
            // candidate skips, and an ARRAY argument throws there rather than declining.
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance,
                Apache.Calcite.FullText.Sql.FullTextOperatorTable.Instance());

            var validator = SqlValidatorUtil.newValidator(
                operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            // project(), not rel. Ordering by an expression outside the select list makes
            // SqlToRelConverter carry it as an extra column and record in the RelRoot that it is not
            // output; project() is what applies that, and is what a real consumer uses. Taking rel
            // leaves the column at the root, which for a scoring function is the difference between a
            // plan that can be implemented and one that cannot — Cosmos will not project a score.
            // It is a no-op wherever the mapping is trivial, which is every other query here.
            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Plans a statement and asks the planner for the best plan wholly in the Cosmos convention.
        /// </summary>
        RelNode PlanToCosmos(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(_table.Convention).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Renders a planned tree to the statement it would execute.
        /// </summary>
        string Render(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build().Sql;
        }

        static string Plan(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        /// <summary>
        /// Renders a planned tree to the full query, including anything recovered about execution.
        /// </summary>
        CosmosQuery Query(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build();
        }

        // ── Through a view's cast to text ─────────────────────────────────────────

        /// <summary>
        /// The whole point of the exercise, end to end: a view exposing the partition key as text runs
        /// against one partition rather than every one.
        /// </summary>
        /// <remarks>
        /// The predicate is in the statement as well. Routing chooses which partitions are visited and
        /// filters nothing, so the rows are decided by the same comparison either way — which is why
        /// this is a cost change and not a behaviour change.
        /// </remarks>
        [Fact]
        public void ACastToTextOverThePartitionKeyConfinesExecution()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"$.category\" AS VARCHAR) = 'bikes'"));

            query.PartitionKeyValues.Should().Equal("bikes");
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
        }

        [Fact]
        public void ACastToTextOverAnOrdinaryPathPushesAsAComparison()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'"))
                .Should().Contain("WHERE (c.label = @p0)");
        }

        /// <remarks>
        /// Text a stored number renders as, so a document Calcite matches could be in another partition
        /// — and the comparison itself would select differently at the service. Neither the predicate
        /// nor the routing is taken, which is the container read whole, exactly as before.
        /// </remarks>
        [Fact]
        public void ACastAgainstTextANumberRendersAsIsNotTaken()
        {
            // No plan wholly in the convention exists, which is this harness's way of saying the filter
            // declined: it stays above, and Calcite applies it to the whole container.
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"$.category\" AS VARCHAR) = '30'");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        /// <remarks>
        /// A cast to a number converts, and no Cosmos comparison reproduces that — so it is declined
        /// and Calcite answers it over the whole container. Slower, and the rows SQL says.
        /// </remarks>
        [Fact]
        public void ACastToANumberIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) = 30");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        // ── A bound on what a numeric cast reads ──────────────────────────────────

        /// <summary>
        /// A comparison through a cast to a number has no Cosmos form, and still says something the
        /// service can apply.
        /// </summary>
        /// <remarks>
        /// Converting a number to a number moves it by less than one, so a document whose converted
        /// value is 30 has a raw value strictly between 29 and 31. The predicate itself stays above and
        /// decides the rows; this only decides which documents cross the wire.
        /// </remarks>
        [Fact]
        public void AComparisonThroughANumericCastPushesABoundOnTheRawValue()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) = 30")));

            query.Sql.Should().Contain("IS_DEFINED(c.price)");
            query.Sql.Should().Contain("(NOT IS_NUMBER(c.price))");
            query.Sql.Should().Contain("(c.price > @p0)");
            query.Sql.Should().Contain("(c.price < @p1)");

            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("29", "31");
        }

        /// <summary>
        /// The type test lets non-numbers through rather than filtering to numbers.
        /// </summary>
        /// <remarks>
        /// This is the whole soundness of it, and the direction is the opposite of the obvious one.
        /// Calcite's cast converts a stored <em>string</em> too — measured, <c>= 30</c> keeps a document
        /// storing <c>"30"</c> — so a filter that kept only numbers would lose it. Anything that is not
        /// a number passes untouched and is decided above.
        /// </remarks>
        [Fact]
        public void TheTypeTestAdmitsNonNumbersRatherThanExcludingThem()
        {
            var sql = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) = 30"))).Sql;

            sql.Should().Contain("(NOT IS_NUMBER(c.price)) OR");
            sql.Should().NotContain("IS_NUMBER(c.price) AND");
        }

        [Fact]
        public void AnInequalityPushesTheBoundOnOneSideOnly()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) > 10")));

            query.Sql.Should().Contain("(c.price > @p0)");
            query.Sql.Should().NotContain("@p1");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("9");
        }

        /// <remarks>
        /// The bound is on the side the cast is, so a comparison written the other way round is the
        /// mirrored operator over the same bound.
        /// </remarks>
        [Fact]
        public void TheBoundIsTheSameWithTheOperandsTheOtherWayRound()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE 10 < CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER)")));

            query.Sql.Should().Contain("(c.price > @p0)");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("9");
        }

        /// <remarks>
        /// Calcite widens a literal to the type it is compared against, so the bound arrives wrapped in
        /// a cast of its own. A cast of a constant to a number is that constant.
        /// </remarks>
        [Fact]
        public void ABoundWrappedInItsOwnCastIsStillRead()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) <= 30.5")));

            query.Sql.Should().Contain("(c.price < @p0)");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("31.5");
        }

        /// <summary>
        /// At the limit the conversion saturates to, the bound on that side is not stated.
        /// </summary>
        /// <remarks>
        /// A stored value far past what the target can hold converts to the limit — measured,
        /// <c>toInt(1e30)</c> is <c>2147483647</c> — so <c>= 2147483647</c> is true of a document
        /// storing <c>1e30</c>, and a window around the limit would exclude exactly that document. It
        /// did, and the differential corpus caught it as a lost row.
        /// </remarks>
        [Fact]
        public void AComparisonAtTheSaturationLimitDoesNotBoundThatSide()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) = 2147483647")));

            query.Sql.Should().Contain("(c.price > @p0)");
            query.Sql.Should().NotContain("@p1");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("2147483646");
        }

        /// <summary>
        /// An inequality the limit itself satisfies does not bound that side either. F10.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>&gt; -3000000000</c> is true of the lower limit, so of every stored value converting to it, a
        /// stored <c>-1e30</c> among them — and a window above <c>-3000000001</c> would refuse that document.
        /// The equality was the one comparison asked about the limit; the question is the same for each.
        /// </para>
        /// <para>
        /// <b>Pinned rather than reached.</b> A constant beyond the target's range is wider than the target, so
        /// Calcite widens the cast to compare it — <c>CAST(CAST(… AS INTEGER) AS BIGINT) &gt; -3000000000</c>
        /// — and the window does not read through a cast of a cast. Only <c>IS_DEFINED</c> is pushed, which
        /// is sound. <see cref="Adapter.Metadata.CosmosNumericWindow"/> asks the limit of every comparison
        /// all the same, so a spelling that does reach it is answered rightly.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("> -3000000000", "c.price > @")]
        [InlineData(">= -3000000000", "c.price > @")]
        [InlineData("< 3000000000", "c.price < @")]
        [InlineData("<= 3000000000", "c.price < @")]
        public void AnInequalityTheLimitSatisfiesDoesNotBoundThatSide(string comparison, string bound)
        {
            var sql = Query(FindCosmos(PlanToAsync(
                $"SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) {comparison}"))).Sql;

            sql.Should().NotContain(bound, "a stored value beyond the limit converts to it and is kept: " + sql);
        }

        /// <summary>
        /// The targets whose conversion does not stay within one of the stored value state no bound.
        /// </summary>
        /// <remarks>
        /// Each was measured against Calcite's own runtime, and measuring is what ruled them out.
        /// <c>SMALLINT</c> and <c>TINYINT</c> wrap rather than saturate — <c>toShort(1e30)</c> is
        /// <c>-1</c> and <c>toByte(1e30)</c> is <c>255</c>, which bear no relation to the stored value.
        /// <c>FLOAT</c> and <c>REAL</c> round to float precision, and <c>float(1e30)</c> is 1.5e22 away
        /// from <c>1e30</c>. <c>DECIMAL</c> raises where the value does not fit its declared precision,
        /// and excluding the document would turn a failing query into a passing one.
        /// </remarks>
        [Fact]
        public void ATargetThatWrapsOrRoundsOrRaisesStatesNoBound()
        {
            foreach (var type in new[] { "SMALLINT", "TINYINT", "REAL", "FLOAT", "DECIMAL(10, 2)" })
            {
                var sql = Query(FindCosmos(PlanToAsync(
                    $"SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS {type}) = 30"))).Sql;

                sql.Should().Contain("IS_DEFINED(c.price)", "a comparison still implies the path is defined");
                sql.Should().NotContain("IS_NUMBER", "no bound is sound for {0}", type);
            }
        }

        /// <summary>
        /// A comparison through <c>RETURNING DECIMAL</c> pushes the bound a cast to a number pushes, and is
        /// rechecked above.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The clause parses what the path holds — a stored <c>"30"</c> is 30 to it, measured — where every
        /// other numeric <c>RETURNING</c> asserts. It was written as the path, so <c>= 30</c> pushed as
        /// <c>c.price = 30</c> and lost the string. It is the cast written as a clause, and takes the cast's
        /// window: non-numbers pass the type test to the recheck.
        /// </para>
        /// <para>
        /// The literal is truncated to the comparison's scale, <c>= 30.5</c> keeping a stored 30, and the window is
        /// read from the literal as written; the truncation moves it by less than the unit the window allows.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("= 30", "29", "31")]
        [InlineData("> 30", "29", null)]
        [InlineData("= 30.5", "29.5", "31.5")]
        public void AComparisonThroughADecimalReturningPushesTheBoundACastPushes(string comparison, string above, string? below)
        {
            var best = PlanToAsync($"SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) {comparison}");
            var query = Query(FindCosmos(best));

            query.Sql.Should().Contain("IS_DEFINED(c.price)");
            query.Sql.Should().Contain("(NOT IS_NUMBER(c.price)) OR", "a stored string that spells a number is the recheck's to decide: " + query.Sql);
            query.Sql.Should().NotContain("c.price = @", "the comparison itself has no Cosmos form");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal(below is null ? new[] { above } : new[] { above, below });

            Plan(best).Should().Contain("ClrCursorFilter", "and the comparison is Calcite's: " + Plan(best));
        }

        /// <summary>
        /// A bound the service would read as another number is sent as one it reads exactly, or not at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A decimal reaches the service as a double, and beyond 2^53 the unit a window is loosened by is nothing to
        /// one: <c>= 2^60</c> sent 2^60 - 1 and 2^60 + 1, which came back as 2^60 and excluded the document holding it.
        /// A whole bound a double does not hold goes as a long, which the service keeps exact. The lower bound of
        /// <c>= 2.9999999999999999</c>, whose double is 2, has no value that is it, and is dropped — and the upper with
        /// it, so no window is read. Measured against the emulator in the differential corpus, over <c>declared</c>.
        /// </para>
        /// <para>
        /// A cast to <c>DOUBLE</c> is within one only below 2^52. From there a stored integer and the literal each round by
        /// up to half a unit, and <c>= 2^60</c> keeps a stored 2^60 + 1. It states no window that far out, and still does
        /// just inside.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) = 1152921504606846976", "1152921504606846975", "1152921504606846977")]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) = 2.9999999999999999", null, null)]
        [InlineData("CAST(JSON_VALUE(c.\"DOC\", '$.price') AS BIGINT) = 9007199254740993", "9007199254740992", "9007199254740994")]
        [InlineData("CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) = 1152921504606846976", null, null)]
        [InlineData("CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) = 4503599627370495", "4503599627370494", "4503599627370496")]
        public void ABoundTheServiceWouldReadAsAnotherNumberIsSentExactlyOrNotAtAll(string comparison, string? above, string? below)
        {
            var query = Query(FindCosmos(PlanToAsync($"SELECT * FROM products AS c WHERE {comparison}")));

            if (above is null)
            {
                query.Sql.Should().NotContain("c.price >", "no value the service is sent is the bound: " + query.Sql);
                query.Sql.Should().NotContain("c.price <", query.Sql);
                return;
            }

            query.Sql.Should().Contain("(c.price > @p0) AND (c.price < @p1)", query.Sql);
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal(above, below);
            query.Parameters.Should().OnlyContain(p => p.Value is decimal || p.Value is long, "each a value the service reads as itself");
        }

        /// <summary>
        /// An exact literal a double does not hold is sent as the long it is, and one with no such value is not sent.
        /// </summary>
        /// <remarks>
        /// Sent as a decimal, which the service reads as a double, <c>RETURNING BIGINT = 1152921504606846977</c> matched
        /// the document holding 2^60 and not the one holding the literal. One the double does hold stays a decimal: the
        /// service compares it with a stored integer exactly, and <c>&gt; 1152921504606846976</c> keeps 2^60 + 1.
        /// </remarks>
        [Fact]
        public void AnExactLiteralADoubleDoesNotHoldIsSentAsTheLongItIs()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING BIGINT) = 1152921504606846977")));

            query.Sql.Should().Contain("(c.price = @p0)");
            query.Parameters.Single().Value.Should().BeOfType<long>().Which.Should().Be(1152921504606846977L);

            var held = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING BIGINT) > 1152921504606846976")));

            held.Sql.Should().Contain("(c.price > @p0)");
            held.Parameters.Single().Value.Should().BeOfType<decimal>().Which.Should().Be(1152921504606846976m);
        }

        /// <summary>
        /// A literal cast down to a <c>DECIMAL</c> its whole digits do not fit states no window, and no fact.
        /// </summary>
        /// <remarks>
        /// Where the comparison's common type would pass nineteen digits, Calcite casts the literal to the clause's type
        /// rather than both up, and <c>CAST(50):DECIMAL(19, 18)</c> raises on every row. Read as 50, the window kept
        /// nothing between 49 and 51 at the service, and answered where the engine raises. A literal it fits is truncated
        /// and still bounds: <c>= 2.5</c> against plain <c>DECIMAL</c>.
        /// </remarks>
        [Fact]
        public void ALiteralCastDownToADecimalItDoesNotFitStatesNoWindow()
        {
            var raises = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL(19, 18)) = 50"))).Sql;

            raises.Should().NotContain("c.price >", "the cast of the literal raises, and nothing bounds what it does not answer: " + raises);
            raises.Should().NotContain("c.price <", raises);

            var fits = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL(19, 18)) = 5")));

            fits.Sql.Should().Contain("(c.price > @p0) AND (c.price < @p1)", fits.Sql);
            fits.Parameters.Select(p => Convert.ToDecimal(p.Value)).Should().Equal(4m, 6m);
        }

        /// <summary>
        /// Nothing else written over <c>RETURNING DECIMAL</c> reaches the service: a column, a sort key, a grouping key,
        /// an inequality, a null test, arithmetic.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each read the path for the parse. The column, and the grouping and the sort over it, sent back the stored string
        /// where the plan declared a number, and the statement failed to read where Calcite answers the number the
        /// string spells; the null test and the inequality took <c>IS_NUMBER</c> as the clause's null set and lost the
        /// string. Measured against the emulator, in the differential corpus. A parsing <c>RETURNING</c> addresses no
        /// path, so each is Calcite's.
        /// </para>
        /// <para>
        /// The control is <c>RETURNING INTEGER</c>, which asserts its type and is written as the path, through its type's
        /// test — see <see cref="AScalarReturningAccessorIsGuardedByItsTypesTest"/>.
        /// </para>
        /// </remarks>
        [Fact]
        public void NothingElseOverADecimalReturningIsPushed()
        {
            const string Price = "JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL)";

            foreach (var sql in new[]
            {
                $"SELECT c.\"id\", {Price} AS \"p\" FROM products AS c",
                $"SELECT c.\"id\" FROM products AS c ORDER BY {Price}",
                $"SELECT {Price} AS \"p\", COUNT(*) AS \"n\" FROM products AS c GROUP BY {Price}",
                $"SELECT c.\"id\" FROM products AS c WHERE {Price} <> 30",
                $"SELECT c.\"id\" FROM products AS c WHERE {Price} IS NOT NULL",
                $"SELECT c.\"id\", {Price} * 2 AS \"p\" FROM products AS c",
            })
            {
                var best = PlanToAsync(sql);
                Render(FindCosmos(best)).Should().NotContain("c.price", "the parse is Calcite's: " + sql + "\n" + Plan(best));
            }

            Render(FindCosmos(PlanToAsync("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c")))
                .Should().Be("SELECT VALUE { \"id\": c.id, \"p\": (IS_NUMBER(c.price) ? c.price : null) } FROM products c", "an asserted type is the value at the path, where it is of that type");
        }

        /// <remarks>
        /// Nothing is known about where converting to a date lands, so there is no bound to state — and
        /// the definedness the comparison implies is still worth pushing.
        /// </remarks>
        [Fact]
        public void ACastWithNoBoundToStateStillPushesDefinedness()
        {
            var sql = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.when') AS DATE) = DATE '2020-01-01'"))).Sql;

            sql.Should().Contain("IS_DEFINED(c.when)");
            sql.Should().NotContain("IS_NUMBER");
        }

        // ── Partition key recovery ────────────────────────────────────────────────

        /// <remarks>
        /// Naming the partition key confines execution to one physical partition rather than
        /// fanning out across every one. It changes nothing about the statement itself.
        /// </remarks>
        [Fact]
        public void PredicateOnThePartitionKeyIsRecovered()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes'"));

            query.PartitionKeyValues.Should().Equal("bikes");
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
        }

        /// <remarks>
        /// A single trailing wildcard is a prefix match, which the index serves as
        /// <c>STARTSWITH</c> where <c>LIKE</c> is a scan.
        /// </remarks>
        [Fact]
        public void PrefixLikeIsPushedAsStartsWith()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"$.category\" LIKE 'bi%'"));

            query.Sql.Should().Contain("STARTSWITH(c.category, @p0)");
        }

        /// <remarks>
        /// The batch counterpart of the point read: <c>pk = … AND id IN (…)</c> is a set of
        /// documents, which <c>ReadManyItemsAsync</c> answers charged as point reads. The <c>IN</c>
        /// arrives from the planner as a <c>SEARCH</c>, which is why this is asserted from real SQL
        /// rather than a hand-built predicate.
        /// </remarks>
        [Fact]
        public void ASetOfIdsWithThePartitionKeyIsRecoveredAsABatchOfPointReads()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('a', 'b')"));

            query.PointReadIds.Should().Equal("a", "b");
            query.PartitionKeyValues.Should().Equal("bikes");
            query.PartitionKeyIsComplete.Should().BeTrue();
        }

        /// <remarks>
        /// The reads are blind, so anything beyond the pinned predicate withdraws them and the
        /// statement runs as the query it already is.
        /// </remarks>
        [Fact]
        public void AResidualPredicateWithdrawsTheBatchOfPointReads()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('a', 'b') AND c.\"_ts\" > 5"));

            query.PointReadIds.Should().BeNull();
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// <para>
        /// The same residual, written by a second filter rather than beside the pinning equalities —
        /// a view's <c>WHERE</c> queried from outside by partition key and <c>id</c>. The planner
        /// keeps the two filters apart and both reach the service, conjoined into one
        /// <c>WHERE</c>; the extractor read only the outer one, so the read was offered and returned
        /// the document the view excludes (review probe H6c, measured on the emulator).
        /// </para>
        /// <para>
        /// Both orders, and both reads: the view's filter beneath the pinning one, and a filter above
        /// the pinning one, which conjoins after the read was offered (H5b).
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"$.category\" AS \"cat\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) IS NULL) AS t WHERE t.\"cat\" = 'shoes' AND t.\"id\" = '3'")]
        [InlineData("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"$.category\" AS \"cat\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 200) AS t WHERE t.\"cat\" = 'bikes' AND t.\"id\" IN ('1', '2')")]
        [InlineData("SELECT t.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')) AS t WHERE t.\"p\" > 200")]
        [InlineData("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"_ts\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1') AS t WHERE t.\"_ts\" > 5")]
        public void AFilterStackedOnThePinningOneWithdrawsTheReads(string sql)
        {
            var best = PlanToCosmos(sql);
            var query = Query(best);

            Plan(best).Split("CosmosFilter").Length.Should().Be(3, "the shape under test is two filters reaching the service: " + Plan(best));
            query.PointReadId.Should().BeNull("the WHERE carries a conjunct the pinning filter did not write: " + query.Sql);
            query.PointReadIds.Should().BeNull("the WHERE carries a conjunct the pinning filter did not write: " + query.Sql);
            query.PartitionKeyIsComplete.Should().BeTrue("the partition key still routes the query");
        }

        /// <remarks>
        /// <para>
        /// A batch returns a row per document and <c>DISTINCT</c> a row per value, so two documents
        /// sharing a <c>_ts</c> came back as two identical rows — soundness probe P22c, measured on
        /// the emulator. <c>_ts</c> is the case that reached it: the service writes it on every
        /// document, so the distinct binds the path and the converter could read it out of each
        /// document, and nothing else withdrew the read.
        /// </para>
        /// <para>
        /// The statement keeps the partition key it pinned and runs as the query it already is.
        /// </para>
        /// </remarks>
        [Fact]
        public void ADistinctWithdrawsTheBatchOfPointReads()
        {
            var query = Query(PlanToCosmos("SELECT DISTINCT c.\"_ts\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('a', 'b')"));

            query.Sql.Should().StartWith("SELECT DISTINCT VALUE { \"_ts\": c._ts } FROM products c WHERE ");
            query.PointReadIds.Should().BeNull();
            query.PointReadId.Should().BeNull();
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        [Fact]
        public void PredicateOnANonPartitionKeyRecoversNothing()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'")).PartitionKeyValues.Should().BeNull();
        }

        [Fact]
        public void QueryWithoutAPredicateRecoversNothing()
        {
            Query(PlanToCosmos("SELECT * FROM products")).PartitionKeyValues.Should().BeNull();
        }

        // ── The planner selects Cosmos nodes ──────────────────────────────────────

        [Fact]
        public void ScanAlonePlansInTheCosmosConvention()
        {
            var best = PlanToCosmos("SELECT * FROM products");

            Plan(best).Should().Contain("CosmosTableScan");
            best.getConvention().Should().BeSameAs(_table.Convention);
        }

        [Fact]
        public void FilterIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'");

            Plan(best).Should().Contain("CosmosFilter");
            Render(best).Should().Contain("WHERE (c.id = @p0)");
        }

        [Fact]
        public void ProjectIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c");

            Plan(best).Should().Contain("CosmosProject");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c");
        }

        [Fact]
        public void FilterAndProjectPlanTogether()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes'");
            var sql = Render(best);

            sql.Should().StartWith("SELECT VALUE { \"id\": c.id } FROM products c WHERE ");
            sql.Should().Contain("c.category = @p0");
        }

        /// <remarks>
        /// The container declares a composite index over (/id, /_ts), so this multi-key sort is
        /// legal and the rule may fire.
        /// </remarks>
        [Fact]
        public void SortIsSelectedWhenTheCompositeIndexPermitsIt()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c ORDER BY c.\"id\", c.\"_ts\"");

            Plan(best).Should().Contain("CosmosSort");
            Render(best).Should().Contain("ORDER BY c.id ASC, c._ts ASC");
        }

        /// <summary>
        /// The end of the chain: an array traversal planned from SQL, selected by the planner, and
        /// rendered to Cosmos SQL.
        /// </summary>
        [Fact]
        public void UnnestIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// The traversal a host actually plans: the array reached through a projection below the
        /// correlate rather than off the scan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The test above plans the array expression straight off the scan, which is what a bare
        /// Volcano planner produces and not what a host does. Calcite's own rule set hoists the
        /// traversed array into a projection on the correlate's left, and the traversal is then above
        /// a projection — which the statement can express, because Cosmos evaluates <c>SELECT</c>
        /// after <c>JOIN</c>, as long as the element is added to the object being constructed.
        /// </para>
        /// <para>
        /// A sub-select is how that shape is reached here without borrowing the host's rules. It is
        /// the same shape and it failed the same way: while the traversal refused to sit above a
        /// projection, no consumer could unnest an array at all, whatever the SQL said. See
        /// ikvmnet/calcite-cosmos#36.
        /// </para>
        /// </remarks>
        [Fact]
        public void UnnestOverAHoistedArrayCarriesTheElement()
        {
            var best = PlanToAsync("SELECT c.\"id\", CAST(t AS VARCHAR) FROM (SELECT p.\"id\", p.\"DOC\" FROM products AS p) AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t");

            Plan(best).Should().Contain("CosmosUnnest");

            // The element is the last property, and it is the whole point: without it the statement
            // returns the projected object the traversal was written under, one column short.
            Render(FindCosmos(best)).Should().Be("SELECT VALUE { \"id\": c.id, \"DOC\": c, \"EXPR$0\": t0 } FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// A predicate over the traversed element is answered by the service rather than by the plan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos evaluates <c>WHERE</c> after <c>JOIN</c>, so a predicate over the element is an
        /// ordinary <c>WHERE</c> over the traversal alias. Reaching it takes two steps: a query writes
        /// the predicate above the correlate, <c>FILTER_CORRELATE</c> pushes it inside, and
        /// <c>CosmosUnnestRule</c> reads it back out as a <c>CosmosFilter</c> over the traversal.
        /// </para>
        /// <para>
        /// Without them the predicate stayed outside and every element of every document crossed the
        /// wire to be discarded here. See ikvmnet/calcite-cosmos#36.
        /// </para>
        /// </remarks>
        [Fact]
        public void APredicateOverTheTraversedElementIsPushedAsAWhere()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t WHERE CAST(t AS VARCHAR) = 'steel'");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags WHERE (t0 = @p0)");
        }

        // ── Reaching an array through the document column ─────────────────────────────
        //
        // UNNEST takes an ARRAY, a MULTISET, a MAP or an ANY, and refuses a string. JSON_QUERY is typed
        // VARCHAR and is refused outright by the validator. StringToArray is the composition that gets
        // past that, being typed ANY, and neither call survives into the statement.

        /// <summary>
        /// An array reached through <c>DOC</c> traverses at the service.
        /// </summary>
        /// <remarks>
        /// Cosmos's own <c>StringToArray</c> takes a string and is <c>undefined</c> over an array, so
        /// eliding the call is what makes the statement run rather than merely what makes it cheaper.
        /// The path already holds the array; there is nothing at the service left to convert.
        /// </remarks>
        [Fact]
        public void AnArrayReachedThroughTheDocumentColumnIsTraversed()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// And a predicate over the element pushes with it.
        /// </summary>
        [Fact]
        public void APredicateOverAnElementReachedThroughTheDocumentColumnIsPushed()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t WHERE CAST(t AS VARCHAR) = 'steel'");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags WHERE (t0 = @p0)");
        }

        /// <summary>
        /// <c>StringToArray</c> over anything but an accessor is the service's own function and is
        /// rendered, not elided.
        /// </summary>
        /// <remarks>
        /// The restriction that keeps the elision honest. A caller naming a path that genuinely holds
        /// a string means Cosmos's function and means it to run; only the composition with an accessor
        /// names an array that is already an array.
        /// </remarks>
        [Fact]
        public void StringToArrayOverAnythingButAnAccessorDoesNotTraverse()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(CAST(JSON_VALUE(c.\"DOC\", '$.tags') AS VARCHAR))) AS t"));

            plan.Should().NotContain("CosmosUnnest", "the operand names a string, so the call is the service's own function rather than an address: " + plan);
        }

        /// <summary>
        /// An element predicate does not pin the partition key, however much it looks like one.
        /// </summary>
        /// <remarks>
        /// <c>products</c> is partitioned on <c>/category</c> and <c>t0</c> is not <c>c.category</c>,
        /// whatever the value compared to it. Routing on it would visit one partition and miss every
        /// document holding the tag elsewhere — a wrong answer rather than a slow one, and silent. The
        /// extractor refuses a path rooted at a traversal alias; this is that refusal reached from SQL.
        /// </remarks>
        [Fact]
        public void APredicateOverTheElementDoesNotPinThePartitionKey()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t WHERE CAST(t AS VARCHAR) = 'bikes'"));

            query.PartitionKeyValues.Should().BeNull();
            query.Sql.Should().Contain("WHERE (t0 = @p0)");
        }

        /// <summary>
        /// A predicate the service cannot express leaves the traversal pushed and stays above it.
        /// </summary>
        /// <remarks>
        /// The control for the two rules above, and the reason they are safe to register. A
        /// transformation adds an equivalence rather than replacing one, so the plan with the predicate
        /// still above the correlate survives — and where the predicate does not render, that is the
        /// plan the planner is left with. The traversal is not lost with it.
        /// </remarks>
        [Fact]
        public void AnUntranslatablePredicateOverTheElementLeavesTheTraversalPushed()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\" FROM products AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t WHERE INITCAP(CAST(t AS VARCHAR)) = 'Steel'"));

            plan.Should().Contain("CosmosUnnest");
            plan.Should().Contain("INITCAP");
            plan.Should().NotContain("ClrCursorUncollect");
        }

        // ── Aggregation ───────────────────────────────────────────────────────────

        [Fact]
        public void GroupByWithCountIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"$.category\", COUNT(*) FROM products AS c GROUP BY c.\"$.category\"");

            Plan(best).Should().Contain("CosmosAggregate");
            Render(best).Should().Contain("GROUP BY (IS_DEFINED(c.category) ? c.category : null)");
            Render(best).Should().Contain("COUNT(1)");
        }

        /// <remarks>
        /// <c>_ts</c> is service-guaranteed and therefore non-nullable, so Cosmos and SQL agree on
        /// the aggregate's value.
        /// </remarks>
        [Fact]
        public void AggregateOverANonNullableColumnIsSelected()
        {
            var best = PlanToCosmos("SELECT c.\"$.category\", MAX(c.\"_ts\") FROM products AS c GROUP BY c.\"$.category\"");

            Plan(best).Should().Contain("CosmosAggregate");
            Render(best).Should().Contain("MAX(c._ts)");
        }

        /// <remarks>
        /// The key is grouped and projected as the value the property has when it is there and as
        /// <c>null</c> when it is not, because SQL has one null where the service keeps an absent
        /// property apart from a present-and-null one. See <c>CosmosAggregate.GroupingKey</c>. The
        /// <c>WHERE</c> beside it still names the plain path, and so does the partition key it pins:
        /// nothing about a predicate needs the two brought together.
        /// </remarks>
        [Fact]
        public void GroupByRendersTheWholeStatement()
        {
            var sql = Render(PlanToCosmos("SELECT c.\"$.category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"$.category\""));

            // Flat rather than an object constructor: Cosmos rejects an aggregate inside one.
            sql.Should().Be("SELECT (IS_DEFINED(c.category) ? c.category : null) AS \"$.category\", COUNT(1) AS \"n\" FROM products c GROUP BY (IS_DEFINED(c.category) ? c.category : null)");
        }

        /// <remarks>
        /// A <c>HAVING</c> on a grouping key is a filter above the aggregate, which cannot bind —
        /// aggregate output has no document paths. <c>FILTER_AGGREGATE_TRANSPOSE</c> moves it below,
        /// where it is an ordinary <c>WHERE</c> the service applies before grouping, and where a
        /// predicate on the partition key confines execution the way it does anywhere else.
        /// </remarks>
        [Fact]
        public void HavingOnAGroupingKeyIsPushedAsAWhere()
        {
            var query = Query(PlanToCosmos(
                "SELECT c.\"$.category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"$.category\" HAVING c.\"$.category\" = 'bikes'"));

            query.Sql.Should().Be("SELECT (IS_DEFINED(c.category) ? c.category : null) AS \"$.category\", COUNT(1) AS \"n\" FROM products c WHERE (c.category = @p0) GROUP BY (IS_DEFINED(c.category) ? c.category : null)");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// <para>
        /// A call-less aggregate is a <c>DISTINCT</c>, and emitting it as one is what lets the sort
        /// join it: <c>GROUP BY</c> and <c>ORDER BY</c> cannot appear together, <c>DISTINCT</c> and
        /// <c>ORDER BY</c> can — measured against a real account, not only the emulator.
        /// </para>
        /// <para>
        /// Over <c>_ts</c> rather than a user path, and that is the null-placement rule rather than
        /// anything to do with the distinct: Calcite's ascending means nulls last and Cosmos sorts
        /// them first, so a nullable key is refused whatever sits below it. Every promoted user
        /// column is nullable today, so this combination reaches only the service's own columns
        /// until a column can be declared non-nullable.
        /// </para>
        /// </remarks>
        [Fact]
        public void DistinctAndOrderByPushAsOneStatement()
        {
            var sql = Render(PlanToCosmos("SELECT DISTINCT c.\"_ts\" FROM products AS c ORDER BY c.\"_ts\""));

            sql.Should().Be("SELECT DISTINCT VALUE { \"_ts\": c._ts } FROM products c ORDER BY c._ts ASC");
        }

        /// <remarks>
        /// <para>
        /// The other half: a <c>DISTINCT</c> of a path some documents lack keeps its sort in process,
        /// and the distinct itself still pushes. The key is grouped through the accessor's guard,
        /// <c>IS_NUMBER(p) ? p : null</c>, SQL's one null for the service's absent and null and for a
        /// value of another type, so the projected value is not the path and the distinct binds nothing
        /// a sort could name. (It was <c>IS_DEFINED(p) ? p : null</c> while the accessor was written
        /// bare, and grouped an object as itself; see <c>AScalarReturningAccessorIsGuardedByItsTypesTest</c>.)
        /// </para>
        /// <para>
        /// The rule used to decide on a binding that named the path, convert the sort, and leave
        /// implementation to refuse it — soundness probe P16, which failed with "the sort keys do not
        /// resolve to document paths" once the planner had already chosen the plan. <c>NULLS FIRST</c>
        /// is what reaches it: under Calcite's default placement a nullable key is declined anyway.
        /// </para>
        /// </remarks>
        [Fact]
        public void ASortOverADistinctOfAPathSomeDocumentsLackStaysAbove()
        {
            var best = PlanToAsync("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c ORDER BY 1 NULLS FIRST");
            var plan = Plan(best);

            plan.Should().Contain("ClrCursorSort", "the sort has no path to name: " + plan);

            var sql = Render(FindCosmos(best));

            sql.Should().StartWith("SELECT DISTINCT VALUE { \"p\": (IS_NUMBER(c.price) ? c.price : null) } FROM products c");
            sql.Should().NotContain("ORDER BY");
        }

        /// <remarks>
        /// A filter over the same distinct is the binding's other reader, and the node behind it
        /// translates against the distinct's. Not a witness of the drift the test above is: the planner
        /// moves this filter beneath the distinct, and it rendered before the two encodings were one.
        /// Held here so that wherever the filter is left, the plan renders.
        /// </remarks>
        [Fact]
        public void AFilterOverADistinctOfAPathSomeDocumentsLackStillRenders()
        {
            var best = PlanToAsync("SELECT d.\"p\" FROM (SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c) AS d WHERE d.\"p\" > 10");

            var sql = Render(FindCosmos(best));

            sql.Should().StartWith("SELECT DISTINCT VALUE { \"p\": (IS_NUMBER(c.price) ? c.price : null) } FROM products c");
        }

        // ── The planner declines rather than guessing ─────────────────────────────

        /// <summary>
        /// An integer division stays in process, and the accessor beneath it is what goes down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Calcite truncates an integer quotient toward zero and the service answers the fraction.
        /// Measured against the emulator, <c>price / 2 = 2</c> lost the document priced 5, and
        /// <c>price / 3</c> projected failed the read at <c>113.33333333333333</c>, which is not the
        /// integer the plan declared. A quotient Calcite computes in doubles is the service's
        /// quotient, and pushes.
        /// </para>
        /// <para>
        /// The control divides the integer accessor by a double, so that Calcite widens the integer
        /// it reads and the differential corpus can say the two agree. <c>RETURNING DOUBLE</c> would
        /// have been the obvious spelling and cannot be checked: over a stored integer Calcite raises
        /// rather than widening, the assertion ikvmnet/calcite-dotnet#120 records, so there is no
        /// oracle to agree with.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnIntegerDivisionIsNotPushedDown()
        {
            var filtered = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 = 2");
            Render(FindCosmos(filtered)).Should().NotContain(" / ", "the quotient is Calcite's: " + Render(FindCosmos(filtered)));

            var projected = PlanToAsync("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 3 AS \"third\" FROM products AS c");
            Render(FindCosmos(projected)).Should().Be("SELECT VALUE { \"$f0\": (IS_NUMBER(c.price) ? c.price : null) } FROM products c");

            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / CAST(3 AS DOUBLE) AS \"third\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"third\": ((IS_NUMBER(c.price) ? c.price : null) / @p0) } FROM products c");
        }

        /// <summary>
        /// A sum, difference, product or remainder Calcite types a decimal fraction stays in process.
        /// </summary>
        /// <remarks>
        /// Calcite computes <c>price * 1.1</c> exactly, in <c>DECIMAL</c>, and the service in doubles,
        /// where <c>1.1</c> is not <c>1.1</c>. Measured against the emulator, <c>price * 1.1 = 374</c>
        /// lost the document priced 340, whose product the service makes <c>374.00000000000006</c>,
        /// and the product projected came back the same way. An integer product is exact in doubles,
        /// and pushes.
        /// </remarks>
        [Fact]
        public void ArithmeticInDecimalFractionsIsNotPushedDown()
        {
            var filtered = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 1.1 = 374");
            Render(FindCosmos(filtered)).Should().NotContain(" * ", "the product is Calcite's: " + Render(FindCosmos(filtered)));

            var projected = PlanToAsync("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) + 0.1 AS \"p\" FROM products AS c");
            Render(FindCosmos(projected)).Should().Be("SELECT VALUE { \"$f0\": (IS_NUMBER(c.price) ? c.price : null) } FROM products c");

            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) * 2 AS \"p\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"p\": ((IS_NUMBER(c.price) ? c.price : null) * @p0) } FROM products c");
        }

        /// <summary>
        /// A comparison read as a value stays in process, where SQL can answer it unknown.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every form the comparison is written in is false somewhere SQL's is unknown: the service's
        /// equality over a JSON null, and the guard a <c>WHERE</c> puts on an inequality over an
        /// absent path. A <c>WHERE</c> discards the row for either. A column, a grouping key and an
        /// enclosing comparison do not. Measured against the emulator over products, whose category
        /// is null in one document and absent in another, <c>category = 'bikes'</c> projected was
        /// false for the null where Calcite answers null, and <c>(category &lt;&gt; 'bikes') =
        /// FALSE</c> kept both documents, which SQL discards.
        /// </para>
        /// <para>
        /// Typed <c>NOT NULL</c>, SQL cannot answer it unknown, and a comparison of the id pushes as it
        /// did. In a <c>CASE</c> condition the truth alone selects an arm, and that pushes too.
        /// </para>
        /// </remarks>
        [Fact]
        public void AComparisonReadAsAValueIsNotPushedDown()
        {
            var projected = PlanToAsync("SELECT c.\"id\", c.\"$.category\" = 'bikes' AS \"b\" FROM products AS c");
            Render(FindCosmos(projected)).Should().NotContain("(c.category = ", "the comparison's value is Calcite's: " + Render(FindCosmos(projected)));

            var compared = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" <> 'bikes') = FALSE");
            Render(FindCosmos(compared)).Should().Be("SELECT VALUE c FROM products c WHERE IS_DEFINED(c.category)", "the split rule pushes what the predicate implies");

            Render(PlanToCosmos("SELECT c.\"id\" = '3' AS \"b\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"b\": (c.id = @p0) } FROM products c");

            Render(PlanToCosmos("SELECT CASE WHEN c.\"$.category\" <> 'bikes' THEN 'x' ELSE 'y' END AS \"b\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"b\": ((IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != @p0)) ? @p1 : @p2) } FROM products c");
        }

        /// <remarks>
        /// Measured on the emulator, Cosmos <c>COUNT(x)</c> counts a JSON null where SQL excludes
        /// it, so the two disagree on any nullable column.
        /// </remarks>
        [Fact]
        public void CountOfANullableColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT COUNT(c.\"$.category\") FROM products AS c");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// Not by any rule here: Calcite rewrites <c>COUNT(x)</c> over a non-nullable column to
        /// <c>COUNT(*)</c> before conversion, so what arrives is the argumentless form that is
        /// always safe. Pinned because it is why <see cref="CosmosAggregate.CanImplement"/> needs
        /// no non-nullable <c>COUNT(x)</c> case — that branch was probed and found dead.
        /// </remarks>
        [Fact]
        public void CountOfANonNullableColumnIsPushedDown()
        {
            var sql = Render(PlanToCosmos("SELECT COUNT(c.\"_ts\") AS n FROM products AS c"));

            sql.Should().Be("SELECT COUNT(1) AS \"n\" FROM products c");
        }

        /// <remarks>
        /// <c>SUM</c> over a set containing a JSON null returns undefined rather than ignoring it.
        /// </remarks>
        [Fact]
        public void SumOfANullableColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT SUM(c.\"$.category\") FROM products AS c");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// Not pushed <em>whole</em>: the registered expansion rewrites this into an aggregate over
        /// an aggregate whose inner <c>GROUP BY</c> is pushable, but the finishing count has no
        /// Cosmos rendering, and a planner asked for a wholly-Cosmos plan cannot produce one. The
        /// partial form is covered in <see cref="Rel.Convert.CosmosAggregateSplitRuleTests"/>,
        /// where there is somewhere outside the convention for the count to live.
        /// </remarks>
        [Fact]
        public void DistinctAggregateIsNotPushedDownWhole()
        {
            var act = () => PlanToCosmos("SELECT COUNT(DISTINCT c.\"id\") FROM products AS c");

            act.Should().Throw<Exception>();
        }


        /// <remarks>
        /// UPPER has no Cosmos equivalent, so no rule converts the filter. With only Cosmos rules
        /// registered the planner cannot reach a plan at all, which is the correct outcome: in a
        /// real planning context the operator is left to Calcite's own runtime.
        /// </remarks>
        [Fact]
        public void UntranslatableFilterIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT * FROM products AS c WHERE INITCAP(c.\"id\") = 'X'");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// A multi-key sort with no matching composite index is rejected by the service, so the
        /// rule must not fire.
        /// </remarks>
        [Fact]
        public void SortWithoutAMatchingCompositeIndexIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT * FROM products AS c ORDER BY c.\"id\", c.\"$.category\"");

            act.Should().Throw<Exception>();
        }

        // ── A null placement the query itself has settled ─────────────────────────
        //
        // `category` is nullable and its placement conflicts with Cosmos in both directions, so
        // ordering by it is refused. A predicate that removes the nulls settles the conflict: with
        // none left there is nothing to place wrongly, whichever way each side would have placed
        // one. The predicate reaches the node through `RelMdPredicates`, so this is the planner
        // deciding rather than the renderer discovering.
        //
        // Suspended pending #165: `category` is now a VARIANT column, which CosmosSort declines to sort
        // outright — Calcite cannot order a VARIANT in process, so the placement question no longer
        // arises. The unlock tests below are skipped until a variant order lands upstream; the two
        // controls stay green, since a VARIANT sort is refused either way.

        /// <remarks>
        /// The pair that carries the change. Both directions, both of Calcite's default placements,
        /// and both refused without the predicate — see
        /// <see cref="ANullableKeyIsStillRefusedWithoutTheGuarantee"/>.
        /// </remarks>
        [Fact(Skip = "#165: a promoted column is now VARIANT, and CosmosSort declines a sort keyed directly on one — Calcite cannot order a VARIANT in process. This verifies the nullable promoted-scalar sort, to re-enable when upstream defines a variant order.")]
        public void AnIsNotNullPredicateMakesANullableColumnASortKey()
        {
            Render(PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c WHERE c.\"$.category\" IS NOT NULL ORDER BY c.\"$.category\""))
                .Should().Contain("ORDER BY c.category ASC");

            Render(PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c WHERE c.\"$.category\" IS NOT NULL ORDER BY c.\"$.category\" DESC"))
                .Should().Contain("ORDER BY c.category DESC");
        }

        /// <summary>
        /// The predicate and the ordering leave as one statement, which is what makes the guarantee
        /// hold at the service rather than only in the plan.
        /// </summary>
        [Fact(Skip = "#165: a promoted column is now VARIANT, and CosmosSort declines a sort keyed directly on one — Calcite cannot order a VARIANT in process. Re-enable when upstream defines a variant order.")]
        public void ThePredicateAndTheOrderingPushAsOneStatement()
        {
            Render(PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c WHERE c.\"$.category\" IS NOT NULL ORDER BY c.\"$.category\""))
                .Should().Be("SELECT VALUE { \"id\": c.id, \"$.category\": c.category } FROM products c WHERE (IS_DEFINED(c.category) AND NOT IS_NULL(c.category)) ORDER BY c.category ASC");
        }

        /// <summary>
        /// The row limit becomes pushable at the same moment the ordering does, a limit being sound
        /// only once the ordering above it is.
        /// </summary>
        [Fact(Skip = "#165: a promoted column is now VARIANT, and CosmosSort declines a sort keyed directly on one — Calcite cannot order a VARIANT in process. Re-enable when upstream defines a variant order.")]
        public void TheRowLimitRidesAlongWithTheOrdering()
        {
            Render(PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c WHERE c.\"$.category\" IS NOT NULL ORDER BY c.\"$.category\" FETCH NEXT 10 ROWS ONLY"))
                .Should().Contain("ORDER BY c.category ASC OFFSET 0 LIMIT 10");
        }

        /// <remarks>
        /// The control. Without the predicate the same statement is refused, which is what says the
        /// tests above depend on the predicate rather than on anything else that changed.
        /// </remarks>
        [Fact]
        public void ANullableKeyIsStillRefusedWithoutTheGuarantee()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c ORDER BY c.\"$.category\"");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// The guarantee has to be about the sort key. A predicate over a different column removes
        /// no null from the one being ordered by.
        /// </remarks>
        [Fact]
        public void APredicateOverAnotherColumnDoesNotUnlockTheSort()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c WHERE c.\"id\" IS NOT NULL ORDER BY c.\"$.category\"");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// The <c>IS NOT NULL</c> that unlocks a nullable sort key does not reach the key when the key
        /// is an unpromoted document path. The reason is structural: such a path projects as an accessor
        /// call rather than as a reference, and <c>RelMdPredicates</c> carries a predicate through a
        /// projection only where the projection is a reference. So the sort is refused for want of the
        /// guarantee — <em>not</em> because the path cannot be ordered: a declared-shape sort over the
        /// same path (a parse, a cast, a <c>RETURNING</c>) still pushes, binding through the rendering
        /// projection. Recorded as the boundary of what this reaches — see <c>TODO.md</c> section 6,
        /// where the fix is a column.
        /// </remarks>
        [Fact]
        public void AnIsNotNullOverAnUnpromotedPathDoesNotUnlockItsSort()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') IS NOT NULL ORDER BY JSON_VALUE(c.\"DOC\", '$.name')");

            act.Should().Throw<Exception>();
        }

        // ── Binding through a projection ────────────────────────

        /// <remarks>
        /// The control for the two below. The key is <c>id</c> rather than <c>category</c> because a
        /// nullable column is refused on its null placement, whatever the projection does.
        /// </remarks>
        [Fact]
        public void SortPushesPastAnAllPathProjection()
        {
            var best = PlanToCosmos("SELECT c.\"id\" AS \"i\" FROM products AS c ORDER BY c.\"id\"");

            Render(best).Should().Contain("ORDER BY c.id ASC");
        }

        /// <remarks>
        /// A computed column has no document path, and the columns beside it still do. Binding per
        /// ordinal is what lets this sort push: the key names <c>id</c>, a plain path, and never reads
        /// the computed one. Bound all-or-nothing, as it was, the whole sort was declined.
        /// </remarks>
        [Fact]
        public void SortOnAPlainColumnPushesPastAComputedProjection()
        {
            var best = PlanToCosmos("SELECT UPPER(c.\"id\") AS \"u\", c.\"id\" AS \"i\" FROM products AS c ORDER BY c.\"id\"");

            Render(best).Should().Contain("ORDER BY c.id ASC");
        }

        /// <remarks>
        /// The other half of the same rule: a sort that does read the computed column has nothing to
        /// order by, Cosmos being unable to address a projection alias, so it is refused — <b>at the
        /// rule</b>, which is what <c>CosmosConverterRule</c> requires. The rule derives its binding by
        /// walking the input rather than reading alias names off the input row type, so it declines here
        /// for the same reason implementation would, and no plan is produced to render.
        /// </remarks>
        [Fact]
        public void SortOnAComputedColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT UPPER(c.\"id\") AS \"u\", c.\"id\" AS \"i\" FROM products AS c ORDER BY UPPER(c.\"id\")");

            act.Should().Throw<Exception>();
        }


        // ── Ordering by a column the query does not select ────────────────────────
        //
        // Calcite answers this in three nodes. Ordering by a column outside the select list makes it
        // an output first, and `RelRoot.project()` adds a projection above the sort to drop it again,
        // leaving projection over sort over projection. Cosmos answers it in one statement, which
        // holds one SELECT — so neither projection converts while the sort sits between them, and
        // without a rewrite the whole query declines and the container is read to answer it.
        //
        // The shape stayed out of the suite because reaching it needs a sort that pushes, and a
        // nullable key is refused on its null placement before the question is asked. `id` is not
        // nullable, which is what exposes it.

        /// <summary>
        /// A query ordering by a column it does not select leaves as one statement.
        /// </summary>
        /// <remarks>
        /// The transpose lifts the inner projection above the sort and the merge folds the two into
        /// one, which is the shape that converts. Asserting the statement rather than the plan is
        /// the point: planning wholly in the convention and rendering are the two things this used
        /// to fail at, in that order.
        /// </remarks>
        [Fact]
        public void OrderingByAColumnOutsideTheSelectListPushesAsOneStatement()
        {
            var best = PlanToCosmos("SELECT c.\"$.category\" FROM products AS c ORDER BY c.\"id\"");

            Plan(best).Should().Be(
                "CosmosProject($.category=[$4])\n" +
                "  CosmosSort(sort0=[$1], dir0=[ASC])\n" +
                "    CosmosTableScan(table=[[products]])",
                "the two projections fold into the one the statement has room for");

            Render(best).Should().Be("SELECT VALUE { \"$.category\": c.category } FROM products c ORDER BY c.id ASC");
        }


        // ── What the subtree below has already written ────────────────────────────
        //
        // A statement holds one of each clause, and the service applies them in its own order rather
        // than the plan's. An operator is therefore pushable only onto a subtree that has not written
        // the clause it needs — and, where two clauses reorder instead of colliding, only onto one
        // whose rows it would still be reading. Each node refuses the pairing it cannot render; what
        // these ask is that the rule refuse it first, which is the difference between running the
        // operator in process and failing the query after the planner has committed to the plan.
        //
        // The binding walk reports the clauses, so every rule reads the same answer the implementor
        // will. Each case below reached its node and threw, bar one, which rendered.

        /// <remarks>
        /// A statement has one ORDER BY. The inner sort takes it and its page, and the outer one runs
        /// over the rows that come back.
        /// </remarks>
        [Fact]
        public void ASortIsNotPushedOntoASubtreeThatHasAlreadySorted()
        {
            var best = PlanToAsync("SELECT * FROM (SELECT * FROM products AS c ORDER BY c.\"id\" FETCH NEXT 5 ROWS ONLY) AS x ORDER BY x.\"id\"");
            var plan = Plan(best);

            plan.Should().Contain("ClrCursorSort", "the second ordering stays in process: " + plan);
            Render(FindCosmos(best)).Should().Contain("ORDER BY c.id ASC OFFSET 0 LIMIT 5");
        }

        /// <summary>
        /// The one that did not throw: pushed onto a page, a sort renders into a statement that
        /// answers a different question.
        /// </summary>
        /// <remarks>
        /// Cosmos applies OFFSET/LIMIT after ORDER BY, so folding this sort in asks the service to
        /// order the container and then take five, where the plan asked for five rows in no
        /// particular order and an ordering of those. Different rows, and nothing anywhere to say so
        /// — which is why the answer has to be the rule's rather than the node's.
        /// </remarks>
        [Fact]
        public void ASortIsNotPushedOntoAPushedRowLimit()
        {
            var best = PlanToAsync("SELECT * FROM (SELECT * FROM products AS c FETCH NEXT 5 ROWS ONLY) AS x ORDER BY x.\"id\"");
            var sql = Render(FindCosmos(best));

            sql.Should().Contain("OFFSET 0 LIMIT 5");
            sql.Should().NotContain("ORDER BY", "the page is taken first, and ordering the container before it takes other rows: " + sql);
            Plan(best).Should().Contain("ClrCursorSort");
        }

        /// <remarks>
        /// WHERE is evaluated before OFFSET/LIMIT, so a predicate cannot join a statement that has
        /// taken its page: it would filter the container and page what survived.
        /// </remarks>
        [Fact]
        public void AFilterIsNotPushedOntoAPushedRowLimit()
        {
            var best = PlanToAsync("SELECT * FROM (SELECT * FROM products AS c ORDER BY c.\"id\" FETCH NEXT 5 ROWS ONLY) AS x WHERE x.\"$.category\" = 'bikes'");
            var sql = Render(FindCosmos(best));

            sql.Should().NotContain("WHERE", "the predicate reads the page, not the container: " + sql);
            Plan(best).Should().Contain("ClrCursorFilter");
        }

        /// <remarks>
        /// The service joins before it orders, pages or de-duplicates, so a traversal cannot join a
        /// statement that has done any of them — it would multiply the rows first and restrict after,
        /// where the plan asked for the restricted rows to be multiplied. A projection below is the
        /// pairing that is allowed, and is covered by
        /// <see cref="UnnestOverAHoistedArrayCarriesTheElement"/>.
        /// </remarks>
        [Fact]
        public void AnArrayTraversalIsNotPushedOntoAPagedOrDistinctSubtree()
        {
            var paged = PlanToAsync("SELECT c.\"id\" FROM (SELECT * FROM products AS p ORDER BY p.\"id\" FETCH NEXT 5 ROWS ONLY) AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t");

            Plan(paged).Should().NotContain("CosmosUnnest", "the page is taken before the traversal: " + Plan(paged));
            Render(FindCosmos(paged)).Should().Contain("OFFSET 0 LIMIT 5");

            var distinct = PlanToAsync("SELECT c.\"id\" FROM (SELECT DISTINCT p.\"id\", p.\"DOC\" FROM products AS p) AS c, UNNEST(StringToArray(JSON_QUERY(c.\"DOC\", '$.tags'))) AS t");

            Plan(distinct).Should().NotContain("CosmosUnnest", "the de-duplication happens before the traversal: " + Plan(distinct));
            Render(FindCosmos(distinct)).Should().Contain("SELECT DISTINCT");
        }

        /// <remarks>
        /// ORDER BY RANK is the statement's one ordering and pairs with TOP alone, and the node writes
        /// the whole SELECT itself — so a subtree that has ordered, paged or projected leaves it
        /// nowhere to go, and the three nodes it would have collapsed stay as they are.
        /// </remarks>
        [Fact]
        public void AnOrderByRankIsNotPushedOntoASubtreeThatHasWrittenItsClauses()
        {
            var paged = PlanToAsync(
                "SELECT y.\"id\" FROM (SELECT c.\"id\", c.\"DOC\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 20 ROWS ONLY) AS y " +
                "ORDER BY FULLTEXTSCORE(JSON_VALUE(y.\"DOC\", '$.name'), 'steel') FETCH FIRST 10 ROWS ONLY");

            Plan(paged).Should().NotContain("CosmosRank", "the statement has already ordered and paged: " + Plan(paged));

            var distinct = PlanToAsync(
                "SELECT y.\"id\" FROM (SELECT DISTINCT c.\"id\", c.\"DOC\" FROM products AS c) AS y " +
                "ORDER BY FULLTEXTSCORE(JSON_VALUE(y.\"DOC\", '$.name'), 'steel') FETCH FIRST 10 ROWS ONLY");

            Plan(distinct).Should().NotContain("CosmosRank", "the statement has already written its SELECT: " + Plan(distinct));
        }


        // ── A cast to text is sent as the value and rendered on the way back ────

        /// <summary>
        /// A projection whose column is a cast to text pushes, the statement carrying the path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Which is what a view is made of — and through the document column a view no longer casts
        /// at all: <c>JSON_VALUE</c> is already <c>VARCHAR</c>. A cast written over one anyway
        /// converts nothing and is dropped, leaving the accessor to render as itself.
        /// </para>
        /// <para>
        /// As itself means guarded. <c>JSON_VALUE</c> answers a scalar's text and null for an object
        /// or an array, and <c>IS_PRIMITIVE</c> is that distinction at the service. The text half
        /// rests on the same equivalence it always did: Calcite's cast over a document value is
        /// Java's rendering of the box the reader already builds, so the value is sent as it stands
        /// and rendered as it is read — see <c>CosmosJson.GetText</c> and <c>DESIGN.md</c>, which
        /// keeps the measurement. What changes is that the statement stops carrying whole documents.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACastToTextInAProjectionPushes()
        {
            var best = PlanToAsync("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c");
            var plan = Plan(best);

            plan.Should().Contain("CosmosProject", "the projection belongs at the service: " + plan);
            plan.Should().NotContain("ClrCursorProject", "and nothing should be left above it: " + plan);

            Render(FindCosmos(best)).Should().Contain("SELECT VALUE { \"n\": (IS_PRIMITIVE(c.name) ? c.name : null) }");
        }

        /// <summary>
        /// A width is a second conversion the reader does not perform, so a cast carrying one stays in
        /// process.
        /// </summary>
        /// <remarks>
        /// Measured against Calcite's own runtime: <c>VARCHAR(3)</c> truncates <c>'bikes'</c> to
        /// <c>'bik'</c> and <c>CHAR(8)</c> pads it to <c>'bikes&#160;&#160;&#160;'</c>. Rendering either as the bare value
        /// would return a different string, so both are refused.
        /// </remarks>
        [Fact]
        public void ACastToTextWithAWidthIsNotRendered()
        {
            Plan(PlanToAsync("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR(3)) AS \"n\" FROM products AS c"))
                .Should().Contain("ClrCursorProject");

            Plan(PlanToAsync("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.name') AS CHAR(8)) AS \"n\" FROM products AS c"))
                .Should().Contain("ClrCursorProject");
        }

        /// <remarks>
        /// A cast to a number converts rather than renders — <c>CAST(x AS INTEGER)</c> reads the stored
        /// string <c>"30"</c> as 30 and truncates 30.7 — and nothing the service returns reproduces that.
        /// It stays in process, as it did.
        /// </remarks>
        [Fact]
        public void ACastToANumberInAProjectionIsStillDeclined()
        {
            Plan(PlanToAsync("SELECT CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) AS \"p\" FROM products AS c"))
                .Should().Contain("ClrCursorProject");
        }

        /// <summary>
        /// A rendered column addresses no document path, so a sort on it is not pushed.
        /// </summary>
        /// <remarks>
        /// The whole of what makes the rendering sound. The column carries text and the path carries the
        /// raw value, and the two do not order alike — as text <c>10</c> sorts before <c>9</c>, and
        /// Cosmos orders a boolean before either. Binding it to no path is what makes the sort decline,
        /// exactly as a computed column does.
        ///
        /// Stated <c>NULLS FIRST</c> deliberately: under Calcite's default placement the sort would be
        /// refused on its null placement instead, and the test would pass without saying anything.
        /// </remarks>
        [Fact]
        public void ASortOnARenderedCastColumnIsNotPushed()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c ORDER BY 2 NULLS FIRST FETCH NEXT 10 ROWS ONLY"));

            plan.Should().Contain("CosmosProject", "the projection still pushes: " + plan);
            plan.Should().NotContain("CosmosSort", "ordering by the rendering is not ordering by the path: " + plan);
        }

        /// <remarks>
        /// The same for a filter, and for the same reason: <c>= '30'</c> is true of the rendered number
        /// and false at the service.
        /// </remarks>
        [Fact]
        public void AFilterOnARenderedCastColumnIsNotPushed()
        {
            var plan = Plan(PlanToAsync(
                "SELECT * FROM (SELECT CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c) WHERE \"n\" = '30'"));

            // What it implies may reach the service -- a stored '30' or a stored 30 -- but the
            // comparison itself stays above the converter, over the column the service projected.
            plan.Should().MatchRegex(@"ClrCursorFilter\(condition=\[=\(.*, '30'\)\]\)", "the predicate reads a rendering, not a path: " + plan);
            plan.Should().NotContain("CosmosFilter(condition=[=(", "and the rendering's comparison is not the statement's: " + plan);
        }

        // ── The cast to text over a SQL/JSON accessor ─────────────────────────────
        //
        // A view over DOC casts a JSON_VALUE. The cast to text is dropped over it: without a RETURNING
        // clause the accessor reads the value as text, which is the rendering the cast over ANY
        // performs, so the argument on TryTextCastOperand carries over.

        [Fact]
        public void ACastToTextOverAJsonAccessorPushesAsAComparison()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'"))
                .Should().Contain("WHERE (c.label = @p0)");
        }

        [Fact]
        public void ACastToTextOverAJsonAccessorToThePartitionKeyConfinesExecution()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.category') AS VARCHAR) = 'bikes'"));

            query.PartitionKeyValues.Should().Equal("bikes");
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
        }

        /// <remarks>
        /// Text a stored number renders as is refused, because the accessor renders the number too.
        /// </remarks>
        [Fact]
        public void ACastAgainstTextANumberRendersAsIsNotTakenOverAJsonAccessor()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = '30'");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        /// <remarks>
        /// A RETURNING clause that converts is a different value under the cast: the number it
        /// declares renders as digits and never as this text, where the path holds whatever the
        /// document says. The cast is not dropped over one.
        /// </remarks>
        [Fact]
        public void ACastToTextOverAConvertingJsonAccessorIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS VARCHAR) = 'bikes'");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        /// <remarks>
        /// JSON_QUERY returns the JSON text of an object or array and nothing for a scalar, so the
        /// cast over it matches no document that stores this text. Not an accessor the cast drops over.
        /// </remarks>
        [Fact]
        public void ACastToTextOverAJsonQueryIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_QUERY(c.\"DOC\", '$.location') AS VARCHAR) = 'bikes'");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        /// <remarks>
        /// A behaviour clause substitutes a value where the path has none, which is a document the
        /// path itself does not match. Refused, whatever the clause says.
        /// </remarks>
        [Fact]
        public void ACastToTextOverAJsonAccessorWithABehaviourClauseIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label' DEFAULT 'bikes' ON EMPTY) AS VARCHAR) = 'bikes'");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        // ── The bare accessor is that cast with nothing written ──────────────────
        //
        // JSON_VALUE without RETURNING is a rendering: SQL:2016 casts the scalar to the returning
        // type, and measured, Calcite keeps a document storing the number 30 for `= '30'`. The path at
        // the service holds the number and does not. So an equality over the bare accessor is held to
        // the literal test the cast form is held to, and behaves like Calcite either way.

        [Fact]
        public void AnEqualityOverAJsonAccessorAgainstUnambiguousTextPushes()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = 'bikes'"))
                .Should().Contain("WHERE (c.label = @p0)");
        }

        /// <remarks>
        /// Declined rather than pushed, and what it implies is pushed instead — see the section on the
        /// alternatives below.
        /// </remarks>
        [Fact]
        public void AnEqualityOverAJsonAccessorAgainstTextANumberRendersAsIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '30'");

            plan.Should().Throw<java.lang.RuntimeException>();

            var best = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '30'");

            // Over the accessor's column where the projection is pushed beneath it (#210), and over the
            // document where it is not: either way, Calcite's to make.
            Plan(best).Should().MatchRegex(@"ClrCursorFilter\(condition=\[=\((JSON_VALUE\(\$0, '\$\.label'\)|\$\d+), '30'\)\]\)", "the comparison is Calcite's to make: " + Plan(best));
        }

        /// <remarks>
        /// The accessor renders scalars only, so a literal no scalar renders as — a JSON null comes
        /// back as SQL null, and Calcite's boolean is lowercase — is exact and pushes as it stands.
        /// </remarks>
        [Fact]
        public void AnEqualityOverAJsonAccessorAgainstTextNoScalarRendersAsPushes()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = 'null'"))
                .Should().Contain("WHERE (c.label = @p0)");

            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = 'TRUE'"))
                .Should().Contain("WHERE (c.label = @p0)");
        }

        // ── What a refused text equality implies ─────────────────────────────────
        //
        // `= '30'` is declined as a translation because Calcite keeps the document storing the number
        // 30, having rendered it, and the service would not. It still implies that the value is that
        // string or that number, and the disjunction of the two pushes under the comparison Calcite
        // makes. The looseness is one spelling: a stored 30.0 renders as `30.0`, is not matched, and
        // crosses the wire to be discarded above. Better than the IS_DEFINED this used to push.

        [Fact]
        public void ATextEqualityAgainstTextANumberRendersAsPushesTheStringOrTheNumber()
        {
            var query = Query(FindCosmos(PlanToAsync("SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '30'")));

            query.Sql.Should().Contain("WHERE ((c.label = @p0) OR (c.label = @p1))");
            query.Parameters.Select(p => p.Value).Should().Equal("30", 30d);
        }

        [Fact]
        public void TheAlternativesPushUnderTheCastFormToo()
        {
            var query = Query(FindCosmos(PlanToAsync("SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = '1.0E30'")));

            query.Sql.Should().Contain("WHERE ((c.label = @p0) OR (c.label = @p1))");
            query.Parameters.Select(p => p.Value).Should().Equal("1.0E30", 1e30);
        }

        [Fact]
        public void TextABooleanRendersAsPushesTheStringOrTheBoolean()
        {
            var query = Query(FindCosmos(PlanToAsync("SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'true'")));

            query.Sql.Should().Contain("WHERE ((c.label = @p0) OR (c.label = @p1))");
            query.Parameters.Select(p => p.Value).Should().Equal("true", true);
        }

        /// <remarks>
        /// Calcite renders a boolean in lowercase, so this text matches only the string. The cast
        /// form's literal test is case-insensitive and refuses it, and the rule pushes the string
        /// alone, which is exact.
        /// </remarks>
        [Fact]
        public void TextInTheWrongCasePushesTheStringAlone()
        {
            var query = Query(FindCosmos(PlanToAsync("SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'TRUE'")));

            query.Sql.Should().Contain("WHERE (c.label = @p0)");
            query.Sql.Should().NotContain(" OR ");
        }

        /// <remarks>
        /// A bracketed literal needs no array branch. <c>JSON_VALUE</c> answers null for an array, so
        /// no stored array matches however the literal looks, and the string comparison is exact. A
        /// cast over the accessor is dropped rather than treated as a second rendering.
        /// </remarks>
        [Fact]
        public void ABracketedLiteralNeedsNoArrayBranch()
        {
            foreach (var sql in new[]
            {
                "SELECT * FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.tags') AS VARCHAR) = '[steel]'",
                "SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.tags') = '[steel]'",
            })
            {
                var query = Query(FindCosmos(PlanToAsync(sql)));

                query.Sql.Should().Contain("WHERE (c.tags = @p0)");
                query.Sql.Should().NotContain("IS_ARRAY");
            }
        }

        /// <remarks>
        /// A number the double cannot hold has no literal to compare against, so the type test stands
        /// in for it.
        /// </remarks>
        [Fact]
        public void ANumberBeyondTheDoubleRangeTakesTheTypeTest()
        {
            var query = Query(FindCosmos(PlanToAsync("SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '1E+400'")));

            query.Sql.Should().Contain("WHERE ((c.label = @p0) OR IS_NUMBER(c.label))");
        }

        /// <remarks>
        /// The alternatives are not the comparison, so the comparison is still made above them.
        /// </remarks>
        [Fact]
        public void TheComparisonStaysAboveTheAlternatives()
        {
            var plan = Plan(PlanToAsync("SELECT * FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = '30'"));

            plan.Should().Contain("ClrCursorFilter(condition=[=(JSON_VALUE($0, '$.label'), '30')])", plan);
            plan.Should().Contain("CosmosFilter(condition=[OR(=(JSON_VALUE($0, '$.label'), '30':VARCHAR(2000)), =(JSON_VALUE($0, '$.label'), 30.0E0:DOUBLE))])", plan);
        }

        /// <remarks>
        /// Against anything but a literal there is no text to reason from: the other side may hold the
        /// text a number renders as, and Calcite would match the number.
        /// </remarks>
        [Fact]
        public void AnEqualityOverAJsonAccessorAgainstAnotherExpressionIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') = c.\"id\"");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        /// <remarks>
        /// A RETURNING clause is a different cast, with its own argument still to be made; nothing
        /// changes for it here.
        /// </remarks>
        [Fact]
        public void AnEqualityOverAConvertingJsonAccessorStillPushes()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) = 30"))
                .Should().Contain("WHERE (c.price = @p0)");
        }

        /// <summary>
        /// The same cast in a projection is rendered, guarded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This test used to say the opposite, and the reason it did is the reason for the guard. A
        /// projection has no literal to exclude the cases on, and measured against Calcite's own
        /// runtime <c>JSON_VALUE</c> answers null for an object or an array where the reader would
        /// render one as <c>{x=1}</c> or <c>[x, y]</c> — so the bare path would carry text for a
        /// document the in-process plan carries nothing for. Leaving it in process was the safe
        /// answer to that.
        /// </para>
        /// <para>
        /// The service can state the distinction instead. <c>IS_PRIMITIVE</c> is exactly SQL/JSON's
        /// line — true of a string, a number, a boolean and a JSON null, false of an object, an array
        /// and an absent property — so the rendered column carries nothing for precisely the
        /// documents the function carries nothing for, and the projection pushes.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACastToTextOverAJsonAccessorInAProjectionIsRenderedGuarded()
        {
            var best = PlanToAsync(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.label') AS VARCHAR) = 'bikes'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosProject", "the projection belongs at the service: " + plan);
            plan.Should().NotContain("ClrCursorProject", "and nothing should be left above it: " + plan);

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("(IS_PRIMITIVE(c.name) ? c.name : null)");
            sql.Should().Contain("WHERE (c.label = @p0)");
        }

        /// <summary>
        /// A <c>DISTINCT</c> or a grouping over an accessor read as text, of a path nothing declares,
        /// is computed in process over the guarded column the service still sends.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The service groups the stored value and SQL groups the rendering, and the guard keeps JSON's
        /// types apart where the rendering does not: a stored <c>30</c> and a stored <c>"30"</c> are two
        /// groups at the service and one value, <c>'30'</c>, in SQL. Measured on the emulator (review
        /// probes J1, J2 and J13), the distinct returned <c>'30'</c> twice, the grouping two groups of
        /// one where SQL has one of two, and <c>COUNT(DISTINCT …)</c> counted one more than SQL.
        /// </para>
        /// <para>
        /// This test used to pin that the pushed distinct kept the guard, which it must where it pushes
        /// at all: the distinct rebuilt the select list from the binding, which is the path, and
        /// emitting the path returned a number for a column declared <c>VARCHAR</c> — which the reader
        /// refuses rather than coerces, so the statement failed outright, <em>Expected a JSON string,
        /// got Number</em>, found by the differential oracle. <c>CosmosDeclaredFactPlanningTests</c>
        /// holds the guarded key over a path declared a string, the one place it now pushes. Here the
        /// guard is still the projection's, beneath the aggregate.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price') FROM products AS c")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.price'), COUNT(*) FROM products AS c GROUP BY JSON_VALUE(c.\"DOC\", '$.price')")]
        [InlineData("SELECT COUNT(DISTINCT JSON_VALUE(c.\"DOC\", '$.price')) FROM products AS c")]
        public void AGroupingOverATextAccessorOfAnUndeclaredPathStaysAbove(string sql)
        {
            var best = PlanToAsync(sql);
            var plan = Plan(best);

            plan.Should().NotContain("CosmosAggregate", "the service would group what SQL calls one value as two: " + plan);
            Render(FindCosmos(best)).Should().Contain("(IS_PRIMITIVE(c.price) ? c.price : null)", "the column still goes down, guarded");
        }

        /// <summary>
        /// A <c>DISTINCT</c> or a grouping over a typed accessor reaches the service over a path nothing
        /// declares, keyed by the accessor's guard.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The guard is the type's own test, null wherever the column is — an object, an array, an
        /// absent path — so grouping by it puts rows together exactly where SQL does. Before it the key
        /// was <c>IS_DEFINED(p) ? p : null</c>, which grouped an object as itself and sent it back for
        /// the integer the plan declared: measured on the emulator over the differential suite's typed
        /// container, the <c>DISTINCT</c> and the <c>GROUP BY</c> failed the read where SQL answers.
        /// </para>
        /// <para>
        /// A sort over the same column stays above —
        /// <see cref="ASortOverAColumnThatIsNotTheStoredValueStaysAbove"/> — because the service orders
        /// only a path, and the path is not the column; a grouping is given the column's own key.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("SELECT DISTINCT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) FROM products AS c", "SELECT DISTINCT")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER), COUNT(*) FROM products AS c GROUP BY JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER)", "GROUP BY (IS_NUMBER(c.price) ? c.price : null)")]
        public void AGroupingOverATypedAccessorPushesKeyedByItsGuard(string sql, string clause)
        {
            var best = PlanToAsync(sql);
            var plan = Plan(best);

            plan.Should().Contain("CosmosAggregate", "the grouping reaches the service: " + plan);

            var statement = Render(FindCosmos(best));
            statement.Should().Contain(clause, statement);
            statement.Should().Contain("(IS_NUMBER(c.price) ? c.price : null)", "keyed by the guarded accessor: " + statement);
        }

        /// <summary>
        /// A sort keyed by a column that is not the value the service holds at its path — an accessor
        /// read as text over a path nothing declares, a typed accessor over a path nothing declares
        /// its type, a <c>JSON_QUERY</c> fragment, the document, an array — is computed in process,
        /// under the placement the service's own order would honour.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The service orders the path and SQL orders the column. The accessor binds the path beneath
        /// it, and the sort never asked how the column was read, so it pushed <c>ORDER BY c.label</c>
        /// and the service ordered the stored values — by JSON type, numbers by value — where SQL orders
        /// their text. Measured on the emulator over the differential suite's typed container: the
        /// label came back <c>[null, null, 'true', '30', 'bikes', 'huge', 'shoes', null, null]</c>
        /// against SQL's <c>[null ×4, '30', 'bikes', 'huge', 'shoes', 'true']</c>. The fragment, the
        /// document and an array are the same disagreement over objects and arrays, measured too.
        /// </para>
        /// <para>
        /// A typed accessor is the same disagreement over the documents it reads null for: an object
        /// and an array sort after every scalar at the service and read null in the column, so under a
        /// page of three <c>RETURNING INTEGER</c> came back <c>[null, null, 30]</c> where SQL answers
        /// three nulls, and <c>RETURNING BOOLEAN</c> <c>[null, null, true]</c>.
        /// </para>
        /// <para>
        /// An explicit placement, because without one the null placement declines every row here first
        /// and the test would say nothing. <c>CosmosDeclaredFactPlanningTests</c> holds the other side,
        /// a text key over a path declared a string and a typed key over a path declared its type,
        /// which still push.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.name') FROM products AS c ORDER BY 1 NULLS FIRST")]
        [InlineData("SELECT c.\"id\" FROM products AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.name') DESC NULLS LAST")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.name' RETURNING VARCHAR) FROM products AS c ORDER BY 1 NULLS FIRST FETCH NEXT 10 ROWS ONLY")]
        [InlineData("SELECT p.\"n\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"n\" FROM products AS c) AS p ORDER BY p.\"n\" NULLS FIRST")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) FROM products AS c ORDER BY 1 NULLS FIRST")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) FROM products AS c ORDER BY 1 NULLS FIRST FETCH NEXT 3 ROWS ONLY")]
        [InlineData("SELECT p.\"p\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c) AS p ORDER BY p.\"p\" DESC NULLS LAST")]
        [InlineData("SELECT c.\"id\" FROM products AS c ORDER BY JSON_VALUE(c.\"DOC\", '$.price' RETURNING DOUBLE) DESC NULLS LAST OFFSET 2 ROWS")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.flag' RETURNING BOOLEAN) FROM products AS c ORDER BY 1 NULLS FIRST")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL) FROM products AS c ORDER BY 1 NULLS FIRST")]
        [InlineData("SELECT JSON_QUERY(c.\"DOC\", '$.metadata') FROM products AS c ORDER BY 1 NULLS FIRST")]
        [InlineData("SELECT c.\"id\" FROM products AS c ORDER BY c.\"DOC\"")]
        [InlineData("SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) FROM products AS c ORDER BY 1 NULLS FIRST")]
        public void ASortOverAColumnThatIsNotTheStoredValueStaysAbove(string sql)
        {
            var best = PlanToAsync(sql);
            var plan = Plan(best);

            plan.Should().NotContain("CosmosSort", "the service would order the stored values where SQL orders the column: " + plan);
            Render(FindCosmos(best)).Should().NotContain("ORDER BY", plan);
        }

        /// <summary>
        /// A sort keyed by a column that is the stored value whatever the container declares still
        /// reaches the service: the service's own <c>id</c> and <c>_ts</c>.
        /// </summary>
        /// <remarks>
        /// Typed and bound to a path, as an accessor with a <c>RETURNING</c> is, and asked nothing of
        /// the container because neither can be null — so neither is an accessor's reading, which
        /// answers null for a value of another type. A <c>RETURNING</c> key pushes where the container
        /// declares its type; <c>CosmosDeclaredFactPlanningTests</c> holds that.
        /// </remarks>
        [Theory]
        [InlineData("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 5 ROWS ONLY", "ORDER BY c.id ASC OFFSET 0 LIMIT 5")]
        [InlineData("SELECT c.\"id\", c.\"_ts\" FROM products AS c ORDER BY c.\"_ts\" DESC", "ORDER BY c._ts DESC")]
        public void ASortOverAColumnReadAsDeclaredStillPushes(string sql, string clause)
        {
            var best = PlanToAsync(sql);

            Plan(best).Should().Contain("CosmosSort", Plan(best));
            Render(FindCosmos(best)).Should().Contain(clause);
        }

        /// <summary>
        /// An accessor whose <c>RETURNING</c> names an array type is guarded by <c>IS_ARRAY</c>, not
        /// by the text form's <c>IS_PRIMITIVE</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The guard used to be <c>IS_PRIMITIVE</c> for every <c>JSON_VALUE</c> whatever it was typed,
        /// and <c>IS_PRIMITIVE</c> is false of an array: the column answered null for exactly the
        /// documents it was written to read (#119). The same expression as an <c>UNNEST</c> source
        /// resolved to the path and returned the elements, so one spelling meant two things.
        /// </para>
        /// <para>
        /// <c>RETURNING … ARRAY</c> is the only spelling that names an array type — <c>JSON_QUERY</c>
        /// is <c>VARCHAR</c> even <c>WITH ARRAY WRAPPER</c> — so the declared type is what tells the
        /// two apart, and it is the same test the binding records the reading by.
        /// </para>
        /// </remarks>
        /// <summary>
        /// A projection mixing renderable columns with one that is not sends the renderable ones and
        /// keeps only the rest in process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The case #125 was filed for.</b> A constant timestamp cannot be rendered — Cosmos has no
        /// temporal type and nothing declares which encoding a container uses, so
        /// <c>CosmosRexTranslator.GetLiteralValue</c> refuses one rather than guessing. Before the
        /// split that refusal took the whole projection with it, and the statement became
        /// <c>SELECT VALUE c</c>: every document crossed the wire so that the client could compute a
        /// constant.
        /// </para>
        /// <para>
        /// The timestamp reads nothing from the input, so the pushed half is the array alone and the
        /// document does not travel at all.
        /// </para>
        /// </remarks>
        [Fact]
        public void AProjectionSplitsAroundAnExpressionThatCannotRender()
        {
            var best = PlanToAsync(
                "SELECT CAST('2020-01-01 12:00:00' AS TIMESTAMP) AS \"x\", JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c");

            var plan = Plan(best);
            plan.Should().Contain("CosmosProject", "the half that renders belongs at the service: " + plan);

            var sql = Render(FindCosmos(best));
            sql.Should().Contain("IS_ARRAY(c.tags)", "which is the array column, guarded as ever: " + sql);
            sql.Should().NotContain("SELECT VALUE c ", "and the document itself should not travel: " + sql);
        }

        /// <summary>
        /// The pushed half carries whatever the residual reads, so a residual over a document path
        /// still finds its operand.
        /// </summary>
        /// <remarks>
        /// A numeric cast of a path cannot render, and this used to send <c>DOC</c> beside the array so
        /// that the cast could be computed above it. It no longer does: the accessor <em>inside</em>
        /// the cast renders, so the scalar is extracted at the service and the cast runs over that.
        /// The document stays where it is, which is the bytes half this split was written for and did
        /// not originally reach.
        /// </remarks>
        [Fact]
        public void TheSplitCarriesWhatTheResidualReads()
        {
            var best = PlanToAsync(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.n' RETURNING VARCHAR) AS DOUBLE) AS \"x\", JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c");

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("IS_ARRAY(c.tags)", "the array is evaluated at the service: " + sql);
            sql.Should().Contain("IS_PRIMITIVE(c.n)", "and so is the accessor the cast reads: " + sql);
            sql.Should().NotContain("\": c }", "so the document has no reason to travel: " + sql);
        }

        /// <summary>
        /// A residual expression pushes the part of itself that renders.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The case the split was written for and did not originally reach.</b> The cast converts
        /// where the service would not, so the expression is residual whole; the accessor inside it
        /// renders perfectly well. What used to cross the wire for this column was the document, so
        /// that the cast could read one scalar out of it in process.
        /// </para>
        /// <para>
        /// Nothing about the cast changed — it is still computed above, and still reads what comes
        /// back. What changed is what comes back.
        /// </para>
        /// </remarks>
        [Fact]
        public void AResidualExpressionPushesTheAccessorInsideIt()
        {
            var best = PlanToAsync(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.n' RETURNING VARCHAR) AS DOUBLE) AS \"x\" FROM products AS c");

            var plan = Plan(best);

            plan.Should().Contain("ClrCursorProject(x=[CAST($0)", "the cast is still the engine's: " + plan);

            Render(FindCosmos(best))
                .Should().Be("SELECT VALUE { \"$f0\": (IS_PRIMITIVE(c.n) ? c.n : null) } FROM products c");
        }

        /// <summary>
        /// The walk stops at the first node that renders rather than descending past it.
        /// </summary>
        /// <remarks>
        /// <b>Maximal, not merely renderable.</b> Both the accessor and the coalesce around it render,
        /// and it is the coalesce that must go down: splitting deeper would push the accessor and leave
        /// the service's own conditional to be evaluated in process over it. One column, and the whole
        /// of what the service was willing to answer.
        /// <para>
        /// Over a scalar <c>RETURNING</c>, whose value is the value. This was written over the text
        /// accessor, whose coalesce no longer renders over a path nothing says holds a string — see
        /// <see cref="ACoalesceOverAnUndeclaredTextAccessorStaysInProcess"/> — which would have made
        /// the accessor the largest part and said nothing about the walk.
        /// </para>
        /// </remarks>
        [Fact]
        public void AFragmentIsTheLargestRenderablePartRatherThanTheFirst()
        {
            var best = PlanToAsync(
                "SELECT CAST(COALESCE(JSON_VALUE(c.\"DOC\", '$.a' RETURNING INTEGER), 0) AS DOUBLE) AS \"x\" FROM products AS c");

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("IS_NUMBER(c.a)) ? (IS_NUMBER(c.a) ? c.a : null) : @p0)",
                "the coalesce goes down whole, not the accessor out of it: " + sql);

            Plan(best).Should().Contain("CAST($0)", "leaving one column for the cast to read: " + Plan(best));
        }

        /// <summary>
        /// The same sub-expression written twice is one column.
        /// </summary>
        /// <remarks>
        /// Keyed by the node's digest, which is what Calcite compares nodes by. Worth a test because
        /// the alternative is silent: two identical columns cost a little and read the same, so nothing
        /// would ever fail.
        /// </remarks>
        [Fact]
        public void ASubExpressionWrittenTwiceIsOneColumn()
        {
            var best = PlanToAsync(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.n' RETURNING VARCHAR) AS DOUBLE) AS \"x\", "
                + "CAST(JSON_VALUE(c.\"DOC\", '$.n' RETURNING VARCHAR) AS INTEGER) AS \"y\" FROM products AS c");

            var sql = Render(FindCosmos(best));

            sql.Should().Be("SELECT VALUE { \"$f0\": (IS_PRIMITIVE(c.n) ? c.n : null) } FROM products c",
                "one accessor, one column, read twice above: " + sql);
        }

        /// <summary>
        /// An expression with nothing renderable inside it still sends what the residual reads.
        /// </summary>
        /// <remarks>
        /// The floor the walk falls back to, and the behaviour the split had before it looked inside
        /// anything: no fragment is found, so the input the residual names is projected for it. Worth
        /// holding because the walk must not lose that — an expression it can do nothing with has to
        /// come out the same way it went in.
        /// </remarks>
        [Fact]
        public void AResidualWithNoRenderablePartStillCarriesItsInputs()
        {
            var best = PlanToAsync(
                "SELECT CAST('2020-01-01 12:00:00' AS TIMESTAMP) AS \"t\", JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c");

            var plan = Plan(best);

            plan.Should().Contain("CosmosProject", "the half that renders still goes down: " + plan);
            plan.Should().Contain("ClrCursorProject", "and the constant is still computed above: " + plan);
        }

        /// <summary>
        /// A residual that consumes an array reads the array as a pushed column.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>`TODO.md` said this could not work, and it was measured rather than taken on trust.</b>
        /// The entry read: a residual that consumes an array evaluates in process, where Calcite
        /// cannot produce one — the correctness half of #125, left standing where the bytes half was
        /// closed. It was written while the adapter pushed <c>JSON_VALUE(… RETURNING … ARRAY)</c>,
        /// whose in-process answer is null, so lifting a projection into process emptied the column.
        /// </para>
        /// <para>
        /// Two things since have removed it. <c>JSON_QUERY</c>'s array <c>RETURNING</c> does produce an
        /// array in process — measured against Calcite's own runtime — so the fragment goes down as a
        /// real array column and the residual reads a <see cref="java.util.List"/>. And
        /// <c>JSON_VALUE</c>'s is refused in every clause, so the spelling the sentence was about is
        /// not pushed at all, and its null is the engine's own answer rather than a divergence this
        /// introduced.
        /// </para>
        /// <para>
        /// <c>ITEM</c> is the operator here because it has no Cosmos form over an array, which is what
        /// makes the expression residual; <c>CARDINALITY</c> would not do, rendering whole as
        /// <c>ARRAY_LENGTH</c>. What the column is read <em>as</em> is pinned end to end by
        /// <c>CosmosToClrCursorConverterTests.ShouldReadAnArrayReturningJsonQueryAsTheArray</c>;
        /// running the residual over it is not available offline — see
        /// <see href="https://github.com/ikvmnet/calcite-dotnet/issues/155">calcite-dotnet#155</see>.
        /// </para>
        /// </remarks>
        [Fact]
        public void AResidualConsumingAnArrayReadsThePushedColumn()
        {
            var best = PlanToAsync(
                "SELECT (JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY))[1] AS \"n\" FROM products AS c");

            var plan = Plan(best);

            plan.Should().Contain("ClrCursorProject(n=[ITEM($0, 1)])",
                "the operator has no Cosmos form, so it stays: " + plan);

            Render(FindCosmos(best))
                .Should().Be("SELECT VALUE { \"$f0\": (IS_ARRAY(c.tags) ? c.tags : null) } FROM products c");
        }

        /// <summary>
        /// A projection every part of which renders is not split.
        /// </summary>
        /// <remarks>
        /// The rule fires only where both halves are non-empty, so the ordinary case keeps the single
        /// <see cref="CosmosProject"/> the all-or-nothing rule already made. Worth holding because a
        /// split that fired here would add a node and a wrapping projection for nothing.
        /// </remarks>
        [Fact]
        public void AWhollyRenderableProjectionIsNotSplit()
        {
            var best = PlanToAsync(
                "SELECT JSON_VALUE(c.\"DOC\", '$.a' RETURNING VARCHAR) AS \"x\", JSON_QUERY(c.\"DOC\", '$.b' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c");

            var plan = Plan(best);

            plan.Should().Contain("CosmosProject", "the projection pushes whole: " + plan);
            plan.Should().NotContain("ClrCursorProject", "with nothing left above it: " + plan);
        }

        /// <summary>
        /// A plain <c>JSON_QUERY</c> is guarded by the complement of the scalar accessor's guard.
        /// </summary>
        /// <remarks>
        /// <c>JSON_VALUE</c> answers for a string, a number, a boolean and a JSON null;
        /// <c>JSON_QUERY</c> answers for an object and an array. <c>IS_OBJECT(p) OR IS_ARRAY(p)</c> is
        /// that line at the service, so the rendered column carries a value for exactly the documents
        /// the function carries one for.
        /// </remarks>
        [Fact]
        public void APlainJsonQueryIsGuardedByIsObjectOrIsArray()
        {
            var best = PlanToCosmos("SELECT JSON_QUERY(c.\"DOC\", '$.o') AS \"q\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"q\": (IS_OBJECT(c.o) OR IS_ARRAY(c.o) ? c.o : null) } FROM products c");
        }

        /// <summary>
        /// A wrapper or a behaviour clause is not the path, so it does not push.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured against Calcite's own runtime, <c>WITH UNCONDITIONAL ARRAY WRAPPER</c> over the
        /// string <c>bikes</c> answers <c>["bikes"]</c> and <c>EMPTY OBJECT ON ERROR</c> answers
        /// <c>{}</c> — values built around the path rather than held at it. The accessor test refuses
        /// them for that reason, and the projection is left in process where the engine computes what
        /// it means.
        /// </para>
        /// <para>
        /// The clauses are always present as operands, whether written or not, which is why the plain
        /// form is recognised by what the three symbols say rather than by their absence.
        /// </para>
        /// </remarks>
        [Fact]
        public void AJsonQueryWithAWrapperDoesNotPush()
        {
            var plan = Plan(PlanToAsync("SELECT JSON_QUERY(c.\"DOC\", '$.o' WITH UNCONDITIONAL ARRAY WRAPPER) AS \"q\" FROM products AS c"));

            plan.Should().Contain("ClrCursorProject", "the wrapper form stays in process: " + plan);
            plan.Should().NotContain("CosmosProject", "and nothing of it is pushed: " + plan);
        }

        [Fact]
        public void AnArrayReturningAccessorIsGuardedByIsArray()
        {
            var best = PlanToCosmos("SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"t\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"t\": (IS_ARRAY(c.tags) ? c.tags : null) } FROM products c");
        }

        /// <summary>
        /// An array <c>RETURNING</c> on <c>JSON_VALUE</c> is refused rather than rendered, because SQL
        /// does not define it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A reversal of #119, and the reasoning changed rather than the measurement.</b> That
        /// change rendered this spelling as the array at the path, on the ground that the engine
        /// answers null and a traversal already reads the elements. But the clause names a
        /// <em>predefined scalar type</em> in SQL, so there is no construct here to be faithful to:
        /// an array <c>RETURNING</c> on this accessor is a spelling Calcite happens to accept, not one
        /// with a meaning to implement.
        /// </para>
        /// <para>
        /// So the column is left in process, where a caller gets what the same query without this
        /// adapter gets — null under the default <c>NULL ON ERROR</c>, and a raw failure under
        /// <c>ERROR ON ERROR</c>. The second cannot be reproduced at the service, and once the first
        /// is conceded it should not be: a pushed answer a plain Calcite query never produces is the
        /// divergence, whichever way it falls.
        /// </para>
        /// <para>
        /// A caller wanting the array writes <c>JSON_QUERY</c>, which SQL permits and Calcite
        /// implements — measured, it answers the array and unnests correctly in process, so it works
        /// whether or not the projection pushes.
        /// </para>
        /// </remarks>
        /// <summary>
        /// An accessor nested in an expression carries the guard it carries alone (#131).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The guard used to be dropped merely by nesting.</b> Measured before this change,
        /// <c>UPPER(JSON_VALUE(DOC, '$.a'))</c> rendered <c>UPPER(c.a)</c> and the concatenation
        /// rendered <c>CONCAT(c.a, @p0)</c> — the bare path in both. <c>JSON_VALUE</c> extracts
        /// scalars, so for a document holding an object at <c>$.a</c> Calcite answers null and the
        /// service was handing the object to the enclosing function instead.
        /// </para>
        /// <para>
        /// The bare accessor was guarded the whole time, which is what made this hard to see: one
        /// spelling of the same column agreed with the engine and the other did not.
        /// </para>
        /// </remarks>
        /// <summary>
        /// #130's own query, whole: the accessor renders, the coalesce renders, and the constant
        /// fallback is computed rather than addressed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The shape a caller writes for an array column that reads empty.</b> Three separate
        /// refusals stood between this and the service, and each was a different thing: the cast
        /// COALESCE expands to, which converts nothing; the guard an accessor loses by being nested
        /// (#131); and an accessor over a literal document, which resolves to no path because there is
        /// no document column under it.
        /// </para>
        /// <para>
        /// The parameter is the empty array itself, sent as JSON, so what comes back for a document
        /// with nothing at the path is read by the column's own reading as an empty list — which is
        /// what the query asked for and what it answers in process.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheReportedCoalesceToAnEmptyArrayPushesWhole()
        {
            var query = Query(PlanToCosmos(
                "SELECT COALESCE(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY), JSON_QUERY('[]', '$' RETURNING VARCHAR ARRAY)) AS \"a\" FROM products AS c"));

            query.Sql.Should().Be("SELECT VALUE { \"a\": ((IS_DEFINED(c.tags) AND NOT IS_NULL(c.tags) AND IS_ARRAY(c.tags)) ? (IS_ARRAY(c.tags) ? c.tags : null) : @p0) } FROM products c");

            query.Parameters.Should().HaveCount(1);
            query.Parameters[0].Value.Should().BeAssignableTo<System.Collections.Generic.IEnumerable<object?>>()
                .Which.Should().BeEmpty("the fallback is the empty array the constant is");
        }

        /// <summary>
        /// A constant accessor is the value at the path, and the accessor's own shape test decides it.
        /// </summary>
        /// <remarks>
        /// The same line the guard draws at the service, drawn here instead because both operands are
        /// known: an array <c>RETURNING</c> answers only an array, a text accessor only a primitive,
        /// and null for anything else. A wildcard or a descent is refused, as it is over a document
        /// column — one grammar, not two.
        /// </remarks>
        [Fact]
        public void AConstantAccessorIsComputedRatherThanAddressed()
        {
            Query(PlanToCosmos("SELECT JSON_VALUE('{\"v\":3}', '$.v' RETURNING INTEGER) AS \"a\" FROM products AS c"))
                .Parameters.Select(p => p.Value?.ToString()).Should().Equal("3");

            Query(PlanToCosmos("SELECT JSON_QUERY('{\"v\":[1,2]}', '$.v' RETURNING INTEGER ARRAY) AS \"a\" FROM products AS c"))
                .Parameters.Should().HaveCount(1);

            // A scalar is not an array, so the array accessor answers null -- written out rather than
            // parameterised, there being no value to bind.
            Render(PlanToCosmos("SELECT JSON_QUERY('{\"v\":3}', '$.v' RETURNING INTEGER ARRAY) AS \"a\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"a\": null } FROM products c");

            // And an object is not a scalar, so the text accessor answers null.
            Render(PlanToCosmos("SELECT JSON_VALUE('{\"v\":{}}', '$.v' RETURNING VARCHAR) AS \"a\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"a\": null } FROM products c");
        }

        /// <summary>
        /// A <c>COALESCE</c> over a text accessor stays in process where nothing says the path holds a
        /// string, and the accessor beneath it is what goes down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nothing about <c>COALESCE</c> was unrenderable (#130).</b> The validator expands
        /// <c>COALESCE(x, y)</c> to <c>CASE(IS NOT NULL(x), CAST(x):T NOT NULL, y)</c> before a
        /// <c>RexCall</c> exists, every piece of which renders, and the cast converts nothing: Calcite
        /// writes it to assert what the null test just proved. That is still so, and the array
        /// coalesce below and the one over a scalar <c>RETURNING</c> push whole.
        /// </para>
        /// <para>
        /// <b>What the text accessor's coalesce answers is the trouble.</b> Its branch hands the value
        /// on as the column, and the column is text: the accessor renders a stored number or boolean
        /// as text, and the service sends the number or the boolean. Measured against the emulator
        /// over the typed container, the read failed at the stored <c>true</c>, where Calcite answers
        /// <c>'true'</c>. Over a declared string the branch is the value and the coalesce pushes — see
        /// <c>CosmosDeclaredFactPlanningTests</c>, which pins the null test it takes.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACoalesceOverAnUndeclaredTextAccessorStaysInProcess()
        {
            var best = PlanToAsync("SELECT COALESCE(JSON_VALUE(c.\"DOC\", '$.a' RETURNING VARCHAR), 'x') AS \"a\" FROM products AS c");

            Plan(best).Should().Contain("CASE(", "the coalesce is Calcite's: " + Plan(best));
            Render(FindCosmos(best)).Should().Contain("(IS_PRIMITIVE(c.a) ? c.a : null)", "and the accessor is the service's");
        }

        /// <summary>
        /// The same over array columns, which is the shape #130 was reported from.
        /// </summary>
        /// <remarks>
        /// The reporter models an array column that reads as an empty collection where the document
        /// has none. Both operands here address a path, so both render; a fallback that is a
        /// <em>constant</em> — their <c>JSON_QUERY('[]', '$')</c> — addresses none and is a separate
        /// question, which <c>TODO.md</c> carries.
        /// </remarks>
        [Fact]
        public void ACoalesceOverArrayAccessorsPushes()
        {
            Render(PlanToCosmos("SELECT COALESCE(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY), JSON_QUERY(c.\"DOC\", '$.other' RETURNING VARCHAR ARRAY)) AS \"a\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"a\": ((IS_DEFINED(c.tags) AND NOT IS_NULL(c.tags) AND IS_ARRAY(c.tags)) ? (IS_ARRAY(c.tags) ? c.tags : null) : (IS_ARRAY(c.other) ? c.other : null)) } FROM products c");
        }

        /// <summary>
        /// A scalar <c>RETURNING</c> carries its type's guard into the coalesce, as it carries it alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The third guarded form beside the two above — see
        /// <see cref="AScalarReturningAccessorIsGuardedByItsTypesTest"/>. It used to be the bare path, on
        /// the reading that a document of the wrong type fails the read either way, and here that
        /// reading was furthest from true: over an object at <c>$.n</c> Calcite's accessor answers null
        /// and the coalesce answers <c>0</c>, where the bare path handed the object to the reader as
        /// the integer the plan declared. Guarded, the object is null at the service too and the
        /// fallback is taken.
        /// </para>
        /// <para>
        /// The null test decides which branch is taken, and reads the same null set from the same
        /// place as the guard. The accessor is null wherever the value is not of the type it names — an
        /// object, which Calcite answers null for, and another scalar, which SQL:2016 answers null for
        /// and Calcite raises over — so the test is over the path and the type's test, as the array
        /// form's above is over the path and <c>IS_ARRAY</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACoalesceOverAScalarReturningReadsThroughItsGuard()
        {
            Render(PlanToCosmos("SELECT COALESCE(JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER), 0) AS \"a\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"a\": ((IS_DEFINED(c.n) AND NOT IS_NULL(c.n) AND IS_NUMBER(c.n)) ? (IS_NUMBER(c.n) ? c.n : null) : @p0) } FROM products c");
        }

        /// <summary>
        /// A query written against the shared full text vocabulary pushes, and renders as the service's
        /// own spelling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The half the translator tests cannot answer.</b> That <c>CLR_FT_CONTAINS</c> renders as
        /// <c>FULLTEXTCONTAINS</c> is one thing; that a caller can <em>write</em> it in SQL and have it
        /// resolve is another, and it is the whole point of merging the declarations into the schema's
        /// own rather than expecting a host to chain a table.
        /// </para>
        /// <para>
        /// One route, not two. Chaining the package's operator table as well would leave two candidates
        /// for one name and break an <c>ARRAY</c> column, which the package's README records against
        /// <c>SqlUtil.lookupSubjectRoutines</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSharedVocabularyResolvesAndPushes()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CLR_FT_CONTAINS(JSON_VALUE(c.\"DOC\", '$.name'), 'steel')"))
                .Should().Be("SELECT VALUE { \"id\": c.id } FROM products c WHERE FULLTEXTCONTAINS(c.name, @p0)");

            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CLR_FT_CONTAINS_ANY(JSON_VALUE(c.\"DOC\", '$.name'), 'steel', 'frame')"))
                .Should().Contain("FULLTEXTCONTAINSANY(c.name, @p0, @p1)");
        }

        /// <summary>
        /// The shared score reaches the rank clause the same way the service's own does.
        /// </summary>
        /// <remarks>
        /// <c>CosmosRankRule</c> matches on <c>IsScoringFunction</c>, which is a test on the call's
        /// <em>name</em> — so teaching it the shared spellings was one line, and the three-node shape
        /// it collapses is unchanged.
        /// </remarks>
        [Fact]
        public void TheSharedScoreReachesTheRankClause()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY CLR_FT_SCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel')");

            Plan(best).Should().Contain("CosmosRank", "the rule reads the name, not the operator: " + Plan(best));

            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c ORDER BY RANK FULLTEXTSCORE(c.name, @p0)");
        }

        /// <summary>
        /// A shared predicate beside one that cannot render pushes what it can.
        /// </summary>
        /// <remarks>
        /// The filter split treats a full text call as it treats anything else that renders: the
        /// conjunct goes to the service and the rest is rechecked above. Dropping a conjunct only ever
        /// weakens, so the service discards nothing the whole predicate would have kept.
        /// </remarks>
        [Fact]
        public void ASharedPredicateBesideAResidualStillPushes()
        {
            var best = PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE CLR_FT_CONTAINS(JSON_VALUE(c.\"DOC\", '$.name'), 'steel') AND INITCAP(c.\"id\") = 'X'");

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("FULLTEXTCONTAINS(c.name, @p0)", "the half the service can answer goes down: " + sql);
            Plan(best).Should().Contain("INITCAP", "and the half it cannot is rechecked above: " + Plan(best));
        }

        /// <summary>
        /// A prefix term takes the whole predicate out of the statement, rather than approximating one.
        /// </summary>
        /// <remarks>
        /// <c>CLR_FT_PREFIX</c> is the one thing in the shared vocabulary Cosmos has no form for. The
        /// call is declined, and because a full text function has no in-process body the query then
        /// fails with a sentence saying so rather than answering a different question — which is what
        /// rendering <c>STARTSWITH</c> over the same property would have been.
        /// </remarks>
        [Fact]
        public void APrefixTermIsNotPushed()
        {
            var plan = Plan(PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE CLR_FT_CONTAINS(JSON_VALUE(c.\"DOC\", '$.name'), CLR_FT_PREFIX('mount'))"));

            plan.Should().Contain("ClrCursorFilter", "the service has no prefix term: " + plan);
            plan.Should().NotContain("CosmosFilter(condition=[CLR_FT_CONTAINS", "and nothing of it is rendered: " + plan);
        }

        /// <summary>
        /// An accessor nested in an expression carries the guard it carries alone (#131), where what
        /// encloses it reads it as a rendering.
        /// </summary>
        /// <remarks>
        /// A comparison is such a reader: it decides which documents the rendering matches, and over
        /// unambiguous text the guarded value matches the same ones. A string function is not — see
        /// <see cref="AStringFunctionOverAnUndeclaredAccessorStaysInProcess"/>.
        /// </remarks>
        [Fact]
        public void ANestedAccessorCarriesItsGuard()
        {
            Render(PlanToCosmos("SELECT CASE WHEN JSON_VALUE(c.\"DOC\", '$.a') = 'x' THEN 1 ELSE 0 END AS \"a\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"a\": (((IS_PRIMITIVE(c.a) ? c.a : null) = @p0) ? @p1 : @p2) } FROM products c");
        }

        /// <summary>
        /// A string function over an accessor stays in process where nothing says the path holds a
        /// string, and the accessor beneath it is what goes down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This test used to pin <c>UPPER((IS_PRIMITIVE(c.a) ? c.a : null))</c> as the rendering, and
        /// the guard is right about what it is about: an object and an array, which the accessor
        /// answers null for. It says nothing about a number or a boolean, which the accessor renders
        /// as text and the service hands to <c>UPPER</c> as themselves. Measured against the emulator,
        /// <c>UPPER(label)</c> and <c>label || '!'</c> projected null for the stored 30 and the stored
        /// <c>true</c>, where Calcite projects <c>30</c>, <c>TRUE</c>, <c>30!</c> and <c>true!</c>.
        /// </para>
        /// <para>
        /// Over a path declared a string the function pushes, being the same function of the same
        /// text at both ends — see <c>CosmosDeclaredFactPlanningTests</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void AStringFunctionOverAnUndeclaredAccessorStaysInProcess()
        {
            foreach (var sql in new[]
            {
                "SELECT UPPER(JSON_VALUE(c.\"DOC\", '$.a' RETURNING VARCHAR)) AS \"a\" FROM products AS c",
                "SELECT JSON_VALUE(c.\"DOC\", '$.a' RETURNING VARCHAR) || 'x' AS \"a\" FROM products AS c",
            })
            {
                var best = PlanToAsync(sql);
                var rendered = Render(FindCosmos(best));

                rendered.Should().Contain("(IS_PRIMITIVE(c.a) ? c.a : null)", "the accessor goes down: " + rendered);
                rendered.Should().NotContain("UPPER").And.NotContain("CONCAT", "and the function is Calcite's: " + rendered);
            }
        }

        /// <summary>
        /// The array accessor carries its own guard nested, which is the half that would have been a
        /// cast failure rather than a wrong value.
        /// </summary>
        /// <remarks>
        /// Bare, an object at the path reaches a column the plan typed a list and the read throws —
        /// #129 by another route. <c>IS_ARRAY</c> is the same guard
        /// <see cref="AnArrayReturningAccessorIsGuardedByIsArray"/> holds at the top level.
        /// </remarks>
        [Fact]
        public void ANestedArrayAccessorCarriesTheArrayGuard()
        {
            Render(PlanToCosmos("SELECT CASE WHEN c.\"id\" = '1' THEN JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) ELSE JSON_QUERY(c.\"DOC\", '$.other' RETURNING VARCHAR ARRAY) END AS \"a\" FROM products AS c"))
                .Should().Contain("(IS_ARRAY(c.tags) ? c.tags : null)")
                .And.Contain("(IS_ARRAY(c.other) ? c.other : null)");
        }

        /// <summary>
        /// A plain <c>JSON_QUERY</c> nested in an expression is not rendered in place — it is lifted
        /// out into a column of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The refusal stands and the expression pushes anyway, which is the point of a fragment.</b>
        /// Rendered <em>in place</em>, this accessor is wrong at any guard: its column is the fragment
        /// as text, which <see cref="CosmosReading.JsonText"/> produces on the way back and the service
        /// cannot produce at all, so <c>UPPER</c> would run over the object where Calcite runs over the
        /// text.
        /// </para>
        /// <para>
        /// Lifted out, there <em>is</em> a reading: the accessor becomes a column with its own, the text
        /// arrives as Calcite would have computed it, and <c>UPPER</c> runs above over exactly that. So
        /// the split does not weaken the refusal — it supplies the one thing the refusal said was
        /// missing.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANestedJsonQueryIsLiftedOutRatherThanRenderedInPlace()
        {
            var best = PlanToAsync("SELECT UPPER(JSON_QUERY(c.\"DOC\", '$.o')) AS \"a\" FROM products AS c");
            var plan = Plan(best);

            plan.Should().Contain("ClrCursorProject(a=[UPPER(", "the text operator stays with the engine: " + plan);

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("IS_OBJECT(c.o) OR IS_ARRAY(c.o)", "and the accessor goes down as its own column: " + sql);
            sql.Should().NotContain("UPPER", "which is not the same as rendering it in place: " + sql);
        }

        [Fact]
        public void AnArrayReturningOnJsonValueIsNotPushed()
        {
            var plan = Plan(PlanToAsync("SELECT JSON_VALUE(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"t\" FROM products AS c"));

            plan.Should().Contain("ClrCursorProject", "the engine decides this one: " + plan);
            plan.Should().NotContain("CosmosProject", "and nothing of it is rendered: " + plan);
        }

        /// <summary>
        /// The same spelling is refused as a traversal source, and refusing it there is what makes the
        /// refusal mean anything.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The first version of this reversal declined the column and left this pushing</b>, which
        /// moved the divergence rather than removing it. In process the accessor answers null and
        /// <c>UNNEST</c> of a null array yields <em>no rows</em> —
        /// <c>CalciteJsonValueArrayMeasurementTests.NeitherUnnestNorCardinalityFindsAnything</c> pins
        /// that — so a pushed <c>JOIN t0 IN c.tags</c> would answer rows where the same query without
        /// this adapter answers none.
        /// </para>
        /// <para>
        /// So the refusal lives in <c>IsJsonAccessor</c>, the one gate a projection, a filter, a
        /// partition key and a traversal all go through: the spelling addresses no path in any clause.
        /// One expression, one meaning, which is what #119 was filed about.
        /// </para>
        /// <para>
        /// The cost is named rather than hidden: a caller who wrote this and got rows will now get
        /// none, because that is what the statement means. <c>JSON_QUERY</c> is the spelling that
        /// keeps the rows, and <see cref="AProjectedArrayAddressesTheSamePathATraversalDoes"/> holds
        /// it to the same path.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnArrayReturningOnJsonValueIsNotTraversedEither()
        {
            var plan = Plan(PlanToAsync(
                "SELECT c.\"id\" FROM products AS c, UNNEST(JSON_VALUE(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t"));

            plan.Should().NotContain("CosmosUnnest", "the spelling addresses no path, so there is no traversal to render: " + plan);
        }

        /// <summary>
        /// The array a projection reads is the array a traversal traverses.
        /// </summary>
        /// <remarks>
        /// One path, one spelling, and after #119 one meaning. The traversal names the path bare
        /// because <c>JOIN … IN</c> iterates it; the projection guards it because a column has to
        /// answer something for a document whose path holds no array. Both address <c>c.tags</c>.
        /// </remarks>
        [Fact]
        public void AProjectedArrayAddressesTheSamePathATraversalDoes()
        {
            var projected = Render(PlanToCosmos(
                "SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"t\" FROM products AS c"));

            var traversed = Render(PlanToCosmos(
                "SELECT c.\"id\" FROM products AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t"));

            projected.Should().Contain("c.tags");
            traversed.Should().Contain("IN c.tags");
        }

        /// <summary>
        /// A <c>JSON_VALUE</c> carrying a behaviour clause is not projected as the path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It was: the accessor was taken whatever followed the path, and
        /// <c>DEFAULT 'none' ON ERROR</c> became <c>(IS_PRIMITIVE(c.flag) ? c.flag : null)</c> — null
        /// for a document with no <c>flag</c>, where the engine answers <c>'none'</c>. A wrong answer,
        /// not a missed pushdown.
        /// </para>
        /// <para>
        /// Refused rather than rendered with the default, because which documents a clause answers
        /// for is a table over the clause, the path's mode and the JSON type found, and it is not the
        /// standard's — <c>CalciteJsonValueMeasurementTests.JsonValueBehaviourClausesFollowThePathModeNotTheStandardsReading</c>
        /// pins it. <c>ERROR ON ERROR</c> raises, which nothing pushed reproduces.
        /// </para>
        /// </remarks>
        [Fact]
        public void AJsonValueBehaviourClauseIsNotProjectedAsThePath()
        {
            foreach (var clause in new[] { "DEFAULT 'none' ON EMPTY", "DEFAULT 'none' ON ERROR", "ERROR ON ERROR", "NULL ON EMPTY ERROR ON ERROR", "RETURNING INTEGER DEFAULT 0 ON EMPTY" })
            {
                var best = PlanToAsync($"SELECT JSON_VALUE(c.\"DOC\", '$.flag' {clause}) AS \"f\" FROM products AS c");
                var plan = Plan(best);

                plan.Should().Contain("ClrCursorProject", $"the engine decides {clause}: " + plan);
                Render(FindCosmos(best)).Should().NotContain("c.flag", $"and nothing of {clause} is rendered");
            }
        }

        /// <summary>
        /// The defaults written down are the plain accessor, and are rendered as it is.
        /// </summary>
        /// <remarks>
        /// The validator keeps them as symbols — <c>FLAG(NULL), FLAG(ON EMPTY)</c> — so the call has
        /// more operands than the bare one; it is what the clauses say that decides, not how many there
        /// are.
        /// </remarks>
        [Fact]
        public void TheDefaultBehaviourClausesWrittenDownAreThePlainAccessor()
        {
            const string Expected = "SELECT VALUE { \"f\": (IS_PRIMITIVE(c.flag) ? c.flag : null) } FROM products c";

            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.flag') AS \"f\" FROM products AS c")).Should().Be(Expected);
            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.flag' NULL ON EMPTY) AS \"f\" FROM products AS c")).Should().Be(Expected);
            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.flag' NULL ON EMPTY NULL ON ERROR) AS \"f\" FROM products AS c")).Should().Be(Expected);
        }

        /// <summary>
        /// A view's column carrying a behaviour clause binds to no path, so an operator over it is not
        /// pushed against one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The other half of the projection's refusal, and the one the projection's test does not see.
        /// The column used to bind to <c>c.flag</c> read as text, and a filter above it was then held
        /// to the tests a plain accessor's column is: <c>= 'none'</c> is unambiguous text, so it
        /// pushed whole as <c>c.flag = 'none'</c> — losing every document without a <c>flag</c>, which
        /// is precisely the document the clause answers <c>'none'</c> for. The comment on
        /// <c>IsUnambiguousTextEquality</c> had said as much: a field bound to one carried no clause
        /// to inspect.
        /// </para>
        /// <para>
        /// Under a host's rewrites too, where the filter is transposed below the projection with the
        /// accessor inlined, and must decline there in its own spelling.
        /// </para>
        /// </remarks>
        [Fact]
        public void AFilterOverAViewsBehaviourClauseColumnIsNotPushedAgainstThePath()
        {
            const string Defaulted = "(SELECT c.\"id\" AS \"Id\", JSON_VALUE(c.\"DOC\", '$.flag' DEFAULT 'none' ON ERROR) AS \"Flag\" FROM products AS c) AS p";

            foreach (var hostRewrites in new[] { false, true })
            {
                foreach (var predicate in new[] { "p.\"Flag\" = 'none'", "p.\"Flag\" LIKE 'no%'", "p.\"Flag\" IS NULL" })
                {
                    var best = PlanToAsync($"SELECT p.\"Id\" FROM {Defaulted} WHERE {predicate}", hostRewrites);
                    var plan = Plan(best);

                    plan.Should().Contain("ClrCursorFilter", $"{predicate} is the engine's to decide, with host rewrites {hostRewrites}: " + plan);
                    Render(FindCosmos(best)).Should().NotContain("c.flag", $"and {predicate} pushes nothing against the path, with host rewrites {hostRewrites}");
                }
            }
        }

        /// <summary>
        /// A <c>RETURNING</c> that names a scalar type is rendered through its type's test and read as
        /// that type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The text guard applied to these once, and it carried a second reading with it: the column
        /// was read back as text into a plan that had declared a number, which the row builder then
        /// could not hand over — <em>Unable to cast System.String to java.lang.Integer</em>. The same
        /// one-line mistake as the array case, and it went with it.
        /// </para>
        /// <para>
        /// Then bare, on the reading that <c>RETURNING</c> asserts, so that a document contradicting
        /// the clause fails the read as it fails Calcite. It does not, for an object or an array:
        /// Calcite's accessor answers null for both under the default <c>NULL ON ERROR</c>
        /// (<c>CalciteJsonValueMeasurementTests</c>), and the bare path handed them to the reader,
        /// which refused them — measured on the emulator over the differential suite's typed
        /// container, where a projection, a grouping, a <c>DISTINCT</c> and a <c>COUNT</c> over such a
        /// path failed while the query answered. So the guard is the type's own test, the line the
        /// accessor draws; a scalar of another type reads null too, which is SQL:2016's answer where
        /// Calcite raises (ikvmnet/calcite-dotnet#120).
        /// </para>
        /// <para>
        /// <c>DECIMAL</c> is not among them: it parses a stored string spelling a number, so no type
        /// test is where it answers, and it addresses no path at all — see
        /// <see cref="NothingElseOverADecimalReturningIsPushed"/>.
        /// </para>
        /// </remarks>
        [Fact]
        public void AScalarReturningAccessorIsGuardedByItsTypesTest()
        {
            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"n\": (IS_NUMBER(c.n) ? c.n : null) } FROM products c");

            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.n' RETURNING DOUBLE) AS \"n\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"n\": (IS_NUMBER(c.n) ? c.n : null) } FROM products c");

            Render(PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.b' RETURNING BOOLEAN) AS \"b\" FROM products AS c"))
                .Should().Be("SELECT VALUE { \"b\": (IS_BOOL(c.b) ? c.b : null) } FROM products c");
        }

        /// <summary>
        /// A negated conjunction implies nothing about the paths inside it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The split rule pushes a restriction the predicate implies and rechecks the predicate
        /// above, which is sound while the restriction is genuinely implied. Definedness is implied
        /// by a bare comparison, negated or not — an absent property makes the comparison undefined
        /// and <c>NOT undefined</c> is not true either. It is not implied by a negated
        /// <em>conjunction</em>: where the other conjunct is false the conjunction is false whatever
        /// the second says, so the negation is <b>true</b> and a document missing the path belongs in
        /// the answer.
        /// </para>
        /// <para>
        /// Measured as a wrong answer rather than reasoned into. The differential oracle returned
        /// five rows where the pushdown returned three; nothing offline could see it, the plan being
        /// well formed and the rows being what disagreed.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANegatedConjunctionPushesNoDefinednessRestriction()
        {
            var best = PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes' AND CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)");

            var cosmos = FindCosmos(best);
            var sql = cosmos is null ? "" : Render(cosmos);

            sql.Should().NotContain("IS_DEFINED", "a negated conjunction implies no path is defined: " + sql);
        }

        /// <summary>
        /// A negated comparison still does, which is the case the rule measured.
        /// </summary>
        [Fact]
        public void ANegatedComparisonStillPushesItsDefinedness()
        {
            var best = PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE NOT (CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)");

            Render(FindCosmos(best)).Should().Contain("IS_DEFINED(c.price)");
        }

        /// <summary>
        /// A <c>CASE</c> condition under a <c>NOT</c> is written in the positive position.
        /// </summary>
        /// <remarks>
        /// The condition selects an arm, and both SQL and the service take the next arm for a condition
        /// that is not true, so it has to be true exactly where SQL's is, whatever encloses the
        /// <c>CASE</c>. It was written in the position of the <c>NOT</c> above it, the inequality
        /// unguarded and so true over the null category: the then arm was taken where SQL takes the
        /// else, and measured against the emulator the document was kept. The arms stay in the
        /// <c>NOT</c>'s position, which is where their value is negated.
        /// </remarks>
        [Fact]
        public void ACaseConditionUnderANegationIsWrittenPositively()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE NOT (CASE WHEN c.\"$.category\" <> 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" = '6' END)"))
                .Should().EndWith("WHERE (NOT ((IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != @p0)) ? ((c.id = @p1) OR IS_NULL(c.id) OR NOT IS_DEFINED(c.id)) : ((c.id = @p2) OR IS_NULL(c.id) OR NOT IS_DEFINED(c.id))))");
        }

        /// <summary>
        /// A <c>CASE</c> implies nothing about the paths inside it either.
        /// </summary>
        /// <remarks>
        /// Its else arm answers where the condition's path is absent, so the predicate can be true
        /// with every path it reads missing. Measured against the emulator, this was weakened to
        /// <c>IS_DEFINED(c.category) AND IS_DEFINED(c.name)</c>, and the document with no category —
        /// whose <c>CASE</c> is <c>'z'</c> — was lost. A <c>CASE</c> now observes absence, as the
        /// null tests do.
        /// </remarks>
        [Fact]
        public void ACasePushesNoDefinednessRestriction()
        {
            var best = PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE (CASE WHEN c.\"$.category\" = 'bikes' THEN INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) ELSE 'z' END) = 'z'");

            var cosmos = FindCosmos(best);
            var sql = cosmos is null ? "" : Render(cosmos);

            sql.Should().NotContain("IS_DEFINED", "a CASE implies no path is defined: " + sql);
        }

        /// <summary>
        /// A comparison that needs a guard declines over a <c>COALESCE</c>, whose value does not follow
        /// its path into unknown.
        /// </summary>
        /// <remarks>
        /// Measured over the typed container, <c>NOT (COALESCE(label, 'none') = 'x')</c> and
        /// <c>COALESCE(label, 'none') &lt;&gt; 'x'</c> each lost the null label and the absent one: the
        /// guard tested <c>label</c>, and the coalesce is <c>'none'</c> there, which SQL compares.
        /// </remarks>
        [Fact]
        public void AGuardedComparisonOverACoalesceStaysInProcess()
        {
            Plan(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE NOT (COALESCE(JSON_VALUE(c.\"DOC\", '$.name'), 'none') = 'x')"))
                .Should().Contain("ClrCursorFilter");

            Plan(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE COALESCE(JSON_VALUE(c.\"DOC\", '$.name'), 'none') <> 'x'"))
                .Should().Contain("ClrCursorFilter");
        }

        /// <summary>
        /// <c>IS NULL</c> over a predicate stays in process, and so does the <c>COALESCE</c> that
        /// expands to one.
        /// </summary>
        /// <remarks>
        /// <c>COALESCE(p, FALSE)</c> is <c>CASE WHEN p IS NOT NULL THEN p ELSE FALSE END</c>, and the
        /// service has no way to ask where <c>p</c> is unknown: its comparisons are two-valued over a
        /// null where SQL's are not. Measured against the emulator,
        /// <c>NOT COALESCE(category &lt;&gt; 'bikes', FALSE)</c> lost the document whose category is
        /// null. Nothing restricts the read in its place, the predicate observing absence.
        /// </remarks>
        [Fact]
        public void IsNullOverAPredicateStaysInProcess()
        {
            var best = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE NOT COALESCE(c.\"$.category\" <> 'bikes', FALSE)");

            Plan(best).Should().Contain("ClrCursorFilter", "the predicate is Calcite's to answer: " + Plan(best));
            Render(FindCosmos(best)).Should().NotContain("WHERE", "and nothing it implies is pushed in its place: " + Render(FindCosmos(best)));
        }

        // ── The comparisons over a text accessor ──────────────────────────────────────
        //
        // JSON_VALUE read as text renders the value at the path and Calcite compares that rendering;
        // the service compares the raw value. They agree on a stored string, whose rendering is
        // itself, and nowhere else -- the orders differ in kind, a boolean sorting before a number
        // and a number before any string at the service, while as text `true` sorts after `bikes`
        // and `30` before it. Measured over the typed container, one document per JSON type.

        /// <summary>
        /// An ordering comparison pushes where the value is a string, and admits the rest.
        /// </summary>
        [Fact]
        public void AnOrderingComparisonOverATextAccessorIsWeakenedToTheStringCase()
        {
            var sql = Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') > 'steel'")));

            sql.Should().Contain("NOT IS_STRING(c.name)");
            sql.Should().Contain("c.name > @p0");

            // The absent path is the one document the escape hatch need not admit: the accessor
            // answers null there and no comparison keeps a null.
            sql.Should().Contain("IS_DEFINED(c.name)");
        }

        /// <summary>
        /// And the comparison itself stays above, because the pushed form is a superset.
        /// </summary>
        [Fact]
        public void TheOrderingComparisonStaysAboveTheWeakening()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') > 'steel'"));

            plan.Should().Contain("ClrCursorFilter", "the comparison is Calcite's to make: " + plan);
        }

        /// <summary>
        /// The inequality goes with them rather than with the equality it negates.
        /// </summary>
        /// <remarks>
        /// An equality against text no non-string renders as is exact; its negation is not, because
        /// the accessor answers null for an object or an array where the raw value compares unequal
        /// to anything. Measured: <c>label &lt;&gt; 'bikes'</c> gained the array and the object.
        /// </remarks>
        [Fact]
        public void AnInequalityOverATextAccessorIsWeakenedToo()
        {
            Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') <> 'steel'")))
                .Should().Contain("NOT IS_STRING(c.name)");
        }

        /// <summary>
        /// The equality under a <c>NOT</c> pushes, guarded by the accessor's null set.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The inequality's problem without the inequality: under a negation the comparison has to be
        /// true wherever SQL's is unknown, so that the <c>NOT</c> above discards the row, and over the
        /// accessor that is wherever it is null — at an object and an array as well as an absent path
        /// and a JSON null. The guard named the last two only. Measured over the typed container,
        /// <c>NOT (label = 'bikes')</c> kept the array and the object, and so did the same equality
        /// under a negated conjunction and a negated disjunction.
        /// </para>
        /// <para>
        /// A typed <c>RETURNING</c> the same, with the type it names: its <c>!=</c> was restricted
        /// to the type in the positive position and its <c>=</c> was not under a <c>NOT</c>, so an
        /// object at the path was kept there. The raw value is null only where the path is absent or
        /// holds null, and keeps the guard it had.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANegatedEqualityOverAnAccessorIsGuardedByItsNullSet()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.label') = 'bikes')"))
                .Should().EndWith("WHERE (NOT ((c.label = @p0) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label) OR NOT IS_PRIMITIVE(c.label)))");

            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE NOT (JSON_VALUE(c.\"DOC\", '$.metadata' RETURNING INTEGER) = 5)"))
                .Should().EndWith("WHERE (NOT ((c.metadata = @p0) OR IS_NULL(c.metadata) OR NOT IS_DEFINED(c.metadata) OR NOT IS_NUMBER(c.metadata)))");

            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes')"))
                .Should().EndWith("WHERE (NOT ((c.category = @p0) OR IS_NULL(c.category) OR NOT IS_DEFINED(c.category)))");
        }

        /// <summary>
        /// <c>LIKE</c> the same, its subject being the accessor.
        /// </summary>
        /// <remarks>
        /// Measured: <c>label LIKE '3%'</c> matches the stored number 30, which renders as
        /// <c>30</c>, and the service's <c>STARTSWITH</c> over a number is undefined rather than
        /// true.
        /// </remarks>
        [Fact]
        public void LikeOverATextAccessorIsWeakenedToo()
        {
            var sql = Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') LIKE 'st%'")));

            sql.Should().Contain("NOT IS_STRING(c.name)");
            sql.Should().Contain("STARTSWITH(c.name");
        }

        /// <summary>
        /// A comparison against a number is untouched, and that is the distinction the gate rests on.
        /// </summary>
        /// <remarks>
        /// Nothing types such a call from SQL; the one that exists is built by the split rule against
        /// the raw value on purpose, and is exactly the comparison the service should make. Declining
        /// it made the numeric bound unrenderable and stopped it being pushed at all.
        /// </remarks>
        [Fact]
        public void AComparisonAgainstANumberIsNotWeakened()
        {
            var sql = Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) > 10")));

            sql.Should().Contain("NOT IS_NUMBER(c.price)");
            sql.Should().NotContain("IS_STRING");
        }

        // ── The same comparisons over a view's column ─────────────────────────────────
        //
        // A view projects the accessor under an alias, and a caller's predicate arrives over the
        // alias: a field reference the binding resolves to the path, which is right, and which said
        // nothing about the column being the rendering rather than the value. The comparison pushed
        // raw -- the very one the accessor's own spelling is declined for -- and where a host
        // transposed the filter below the projection, the accessor was inlined and implementation
        // refused the statement (#83). The binding now carries how each column is read.

        /// <summary>
        /// A view over the container, in the shape every typed caller writes: an accessor under an
        /// alias, restricted to one kind of document.
        /// </summary>
        const string View = "(SELECT JSON_VALUE(x.\"DOC\", '$.name' RETURNING VARCHAR) AS \"Name\" FROM products AS x WHERE JSON_VALUE(x.\"DOC\", '$.category' RETURNING VARCHAR) = 'bikes') AS p";

        /// <summary>
        /// <c>LIKE</c> over the view's column takes the guard the accessor takes, and stays above it.
        /// </summary>
        [Fact]
        public void LikeOverAViewsTextColumnIsWeakenedToTheStringCase()
        {
            var best = PlanToAsync($"SELECT p.\"Name\" FROM {View} WHERE p.\"Name\" LIKE 'st%'");
            var sql = Render(FindCosmos(best));

            sql.Should().Contain("NOT IS_STRING(c.name)");
            sql.Should().Contain("STARTSWITH(c.name");
            sql.Should().Contain("IS_DEFINED(c.name)");

            Plan(best).Should().Contain("ClrCursorFilter", "the pattern is Calcite's to match: " + Plan(best));
        }

        /// <summary>
        /// And under a host's rewrites, which is where it failed rather than merely erred: the filter
        /// transposed below the projection, the accessor inlined, the statement refused.
        /// </summary>
        /// <remarks>
        /// What makes it survive is that the guard names the raw value with a cast to <c>ANY</c>
        /// rather than by re-typing the field: the transpose replaces the field with the projection's
        /// expression and a re-typed field would lose the type that said it. See
        /// <c>CosmosFilterSplitRule.RawValue</c>.
        /// </remarks>
        [Fact]
        public void LikeOverAViewsTextColumnSurvivesAHostsTransposition()
        {
            var best = PlanToAsync($"SELECT p.\"Name\" FROM {View} WHERE p.\"Name\" LIKE 'st%'", hostRewrites: true);
            var plan = Plan(best);

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("NOT IS_STRING(c.name)");
            sql.Should().Contain("STARTSWITH(c.name");

            plan.Should().Contain("ClrCursorFilter", "the pattern is Calcite's to match: " + plan);
        }

        /// <summary>
        /// The ordering comparisons the same way.
        /// </summary>
        [Fact]
        public void AnOrderingComparisonOverAViewsTextColumnIsWeakenedToo()
        {
            foreach (var hostRewrites in new[] { false, true })
            {
                var best = PlanToAsync($"SELECT p.\"Name\" FROM {View} WHERE p.\"Name\" > 'st'", hostRewrites);
                var sql = Render(FindCosmos(best));

                sql.Should().Contain("NOT IS_STRING(c.name)", "with host rewrites: " + hostRewrites);
                sql.Should().Contain("c.name > @p1", "with host rewrites: " + hostRewrites);

                Plan(best).Should().Contain("ClrCursorFilter", "the comparison is Calcite's to make: " + Plan(best));
            }
        }

        /// <summary>
        /// An equality against unambiguous text is exact over the accessor, so it is exact over the
        /// column too, and nothing is left above.
        /// </summary>
        [Fact]
        public void AnExactEqualityOverAViewsTextColumnStillPushesWhole()
        {
            var best = PlanToAsync($"SELECT p.\"Name\" FROM {View} WHERE p.\"Name\" = 'steel'");

            Render(FindCosmos(best)).Should().Contain("(c.name = @p1)").And.NotContain("IS_STRING");
            Plan(best).Should().NotContain("ClrCursorFilter", "an exact equality has nothing to recheck: " + Plan(best));
        }

        /// <summary>
        /// And an equality against text a number renders as pushes the alternatives the accessor's
        /// spelling pushes: the string, or the number.
        /// </summary>
        [Fact]
        public void AnAmbiguousEqualityOverAViewsTextColumnPushesItsAlternatives()
        {
            var best = PlanToAsync($"SELECT p.\"Name\" FROM {View} WHERE p.\"Name\" = '30'");
            var sql = Render(FindCosmos(best));

            sql.Should().Contain("(c.name = @p1) OR (c.name = @p2)");
            Plan(best).Should().Contain("ClrCursorFilter", "the comparison is Calcite's to make: " + Plan(best));
        }

        // ── A case fold under LIKE ────────────────────────────────────────────────────
        //
        // `UPPER(x) LIKE '%STEEL%'` is what an ORM writes for a case-insensitive contains, and the
        // service has one natively (#84). Over the accessor it has the rendering gap LIKE has, so it
        // is declined and weakened like LIKE; the guard's own comparison is the one rendered as the
        // native function.

        [Fact]
        public void ACaseFoldedContainsIsTheServicesCaseInsensitiveContains()
        {
            var best = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.name')) LIKE '%STEEL%'");
            var sql = Render(FindCosmos(best));

            sql.Should().Contain("CONTAINS(c.name, @p0, true)");
            sql.Should().Contain("NOT IS_STRING(c.name)");

            Plan(best).Should().Contain("ClrCursorFilter", "the fold and the pattern are Calcite's to apply: " + Plan(best));
        }

        [Fact]
        public void ACaseFoldedPrefixOverAViewsColumnIsACaseInsensitiveStartsWith()
        {
            foreach (var hostRewrites in new[] { false, true })
            {
                var sql = Render(FindCosmos(PlanToAsync($"SELECT p.\"Name\" FROM {View} WHERE LOWER(p.\"Name\") LIKE 'st%'", hostRewrites)));

                sql.Should().Contain("STARTSWITH(c.name, @p1, true)", "with host rewrites: " + hostRewrites);
                sql.Should().Contain("NOT IS_STRING(c.name)", "with host rewrites: " + hostRewrites);
            }
        }

        /// <summary>
        /// Over a path the row model types <c>ANY</c> there is no rendering and no guard, and the
        /// fold renders as the native function directly.
        /// </summary>
        [Fact]
        public void ACaseFoldedSuffixOverARawPathNeedsNoGuard()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE UPPER(c.\"$.category\") LIKE '%KES'");

            Render(best).Should().Contain("ENDSWITH(c.category, @p0, true)").And.NotContain("IS_STRING");
        }

        // ── A comparison through RETURNING ────────────────────────────────────────────
        //
        // RETURNING is a typed extraction rather than a rendering: it participates only where the
        // value is of the type it names. Calcite does not enforce that -- it casts what it extracted
        // and throws when the cast fails, outside the ON ERROR handling that SQL:2016 says should
        // answer null (ikvmnet/calcite-dotnet#120). So the adapter restricts, which is closer to the
        // standard than the engine.

        /// <summary>
        /// An inequality through a typed accessor restricts to the type it names.
        /// </summary>
        /// <remarks>
        /// It is the only comparison that has to. Measured against a real account: the service's
        /// ordering comparisons are <em>undefined</em> across JSON types, so a stored string is
        /// already absent from <c>c.v &gt; 10</c> and a type test changes nothing. <c>!=</c> is the
        /// exception and answers true for every value of another type — a string, a boolean, an array
        /// and an object all came back — and those are exactly the documents Calcite throws on and
        /// the standard excludes.
        /// </remarks>
        [Fact]
        public void AnInequalityThroughAReturningRestrictsToItsType()
        {
            Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) <> 30")))
                .Should().Contain("IS_NUMBER(c.price)");
        }

        /// <summary>
        /// And the ordering comparisons do not, because the service already restricts them.
        /// </summary>
        [Fact]
        public void AnOrderingThroughAReturningNeedsNoTypeTest()
        {
            foreach (var op in new[] { ">", "<", ">=", "<=" })
            {
                var sql = Render(FindCosmos(PlanToAsync($"SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) {op} 10")));

                sql.Should().NotContain("IS_NUMBER", $"the service's {op} is undefined across types: " + sql);
                sql.Should().Contain($"c.price {op} @p0");
            }
        }

        /// <summary>
        /// An equality needs none either, the service's <c>=</c> not crossing types.
        /// </summary>
        [Fact]
        public void AnEqualityThroughAReturningNeedsNoTypeTest()
        {
            var sql = Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) = 30")));

            sql.Should().NotContain("IS_NUMBER", sql);
            sql.Should().Contain("c.price = @p0");
        }

        /// <summary>
        /// A boolean <c>RETURNING</c> takes the boolean test.
        /// </summary>
        [Fact]
        public void ABooleanReturningTakesTheBooleanTest()
        {
            Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.flag' RETURNING BOOLEAN) <> TRUE")))
                .Should().Contain("IS_BOOL(c.flag)");
        }

        // ── Past a projection that cannot be pushed ──────────────────────────────
        //
        // A view gives a container a relational shape by casting, the row model typing every path
        // ANY. A cast to text is rendered by the reader and pushes; the rest — a width, a numeric
        // target — stays in process, and a sort and row limit above it used to stay with it, reading
        // the container whole to answer a bounded page. Transposed below the projection they push,
        // and the cast runs over the rows that come back.

        /// <summary>
        /// The sort and its row limit reach the statement even though the projection above them
        /// cannot be rendered.
        /// </summary>
        [Fact]
        public void ASortOnAnUncastColumnPushesPastAnUnrenderableProjection()
        {
            var best = PlanToAsync("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) AS \"p\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 10 ROWS ONLY");
            var plan = Plan(best);

            plan.Should().Contain("CosmosSort", "the sort belongs at the service: " + plan);
            plan.Should().NotContain("ClrCursorSort", "and must not also remain in process: " + plan);
            plan.Should().Contain("ClrCursorProject", "the cast itself still runs in process: " + plan);

            Render(FindCosmos(best)).Should().Contain("ORDER BY c.id ASC OFFSET 0 LIMIT 10");
        }

        /// <remarks>
        /// The other half, and the reason this is sound. Ordering by the cast column is not ordering
        /// by the path underneath, so the sort must stay above the projection. Calcite maps the keys
        /// through and declines where any of them is not a plain reference, which is the whole guard.
        ///
        /// Stated <c>NULLS FIRST</c> deliberately: under Calcite's default placement the sort would
        /// be refused on its null placement instead, and the test would pass without saying anything
        /// about the transpose.
        /// </remarks>
        [Fact]
        public void ASortOnTheCastColumnItselfDoesNotTranspose()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER) AS \"p\" FROM products AS c ORDER BY 2 NULLS FIRST FETCH NEXT 10 ROWS ONLY"));

            plan.Should().NotContain("CosmosSort", "ordering by the cast is not ordering by the path: " + plan);
        }

        /// <summary>
        /// The shape a paged view has, end to end: a predicate, a cast projection, an ordering and a
        /// row limit. All of it belongs at the service.
        /// </summary>
        /// <remarks>
        /// This is the difference the item is about. Answering ten rows used to read every document
        /// the predicate matched; it now reads ten — and, since the cast to text is rendered rather
        /// than declined, nothing is left in process at all.
        /// </remarks>
        [Fact]
        public void APagedViewReadsAPageRatherThanTheMatchingDocuments()
        {
            var best = PlanToAsync(
                "SELECT c.\"id\", CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) AS \"n\" FROM products AS c " +
                "WHERE c.\"$.category\" = 'bikes' ORDER BY c.\"id\" FETCH NEXT 10 ROWS ONLY");

            var plan = Plan(best);
            plan.Should().NotContain("ClrCursorProject", "nothing is left for the plan to do: " + plan);

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("WHERE (c.category = @p0)");
            sql.Should().Contain("ORDER BY c.id ASC OFFSET 0 LIMIT 10");
        }

        /// <remarks>
        /// The control for both: with no cast the projection pushes and the sort goes with it, which
        /// is the plan the transpose is trying to get back to the shape of.
        /// </remarks>
        [Fact]
        public void WithNoCastTheWholeStatementPushesAsBefore()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"n\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 10 ROWS ONLY"));

            plan.Should().Contain("CosmosSort");
            plan.Should().Contain("CosmosProject");
            plan.Should().NotContain("ClrCursorProject", "nothing is left for the plan to do: " + plan);
        }


        // ── Partial filter pushdown ───────────────────────────────

        /// <summary>
        /// Plans a statement, asking for the CLR convention so that a plan may legitimately
        /// keep some work in Calcite rather than having to be Cosmos throughout.
        /// </summary>
        RelNode PlanToAsync(string sql) => PlanToAsync(sql, hostRewrites: false);

        /// <summary>
        /// Plans a statement to the CLR convention, optionally under the rewrites a host
        /// running Calcite's own rule set brings with it.
        /// </summary>
        /// <remarks>
        /// The three registered are the ones that move a filter past a projection and merge what
        /// lands together — <c>FILTER_PROJECT_TRANSPOSE</c>, <c>PROJECT_FILTER_TRANSPOSE</c> and
        /// <c>FILTER_MERGE</c> — and they are the ones that turned a wrong plan into a failing one:
        /// the transpose copies a Cosmos filter below a Cosmos projection with the projection's
        /// expression inlined, and nothing rechecks what it made. A plan has to survive them because
        /// every connection-based host has them.
        /// </remarks>
        RelNode PlanToAsync(string sql, bool hostRewrites)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorRules.Rules())
                planner.addRule(rule);

            if (hostRewrites)
            {
                planner.addRule(org.apache.calcite.rel.rules.CoreRules.FILTER_PROJECT_TRANSPOSE);
                planner.addRule(org.apache.calcite.rel.rules.CoreRules.PROJECT_FILTER_TRANSPOSE);
                planner.addRule(org.apache.calcite.rel.rules.CoreRules.FILTER_MERGE);
            }

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <remarks>
        /// INITCAP has no Cosmos form, so the whole predicate used to be declined and every document
        /// crossed the wire. The renderable conjunct is pushed and the rest rechecked above it, which is
        /// sound because dropping a conjunct only ever weakens: the service discards nothing the full
        /// predicate would have kept.
        /// </remarks>
        [Fact]
        public void RenderablePartOfAPredicateIsPushedAndTheRestRechecked()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter");
            plan.Should().Contain("INITCAP");
        }

        /// <summary>
        /// A disjunction whose branch cannot be rendered is pushed as what that branch implies.
        /// </summary>
        /// <remarks>
        /// Dropping a disjunct strengthens, so an <c>OR</c> with an untranslatable branch used to be
        /// declined whole and every document crossed the wire. Since a branch can only be true where
        /// the paths it reads are defined, <c>a OR b</c> pushes <c>a OR IS_DEFINED(…)</c> — implied by
        /// the original, so it discards nothing the original would have kept — and the original is
        /// rechecked above it.
        /// </remarks>
        [Fact]
        public void ADisjunctionWithAnUntranslatableBranchIsWeakened()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' OR INITCAP(c.\"_etag\") = 'X'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter", "something should reach the service: " + plan);
            plan.Should().Contain("IS_DEFINED", "the untranslatable branch implies its path is defined: " + plan);
            plan.Should().Contain("INITCAP", "and the original is still rechecked: " + plan);
        }

        /// <summary>
        /// A branch that can observe absence implies nothing about definedness, and is not weakened.
        /// </summary>
        /// <remarks>
        /// Measured, and the reason the rule is about absence rather than about polarity:
        /// <c>NOT IS_DEFINED(c.x)</c> is true exactly where the path is missing, so a branch containing
        /// it cannot imply the path is there. Weakening it anyway would strengthen the predicate and
        /// lose rows — the failure this whole design is arranged to make impossible.
        /// </remarks>
        [Fact]
        public void ABranchThatObservesAbsenceIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT * FROM products AS c " +
                "WHERE c.\"$.category\" = 'bikes' OR (NOT IS_DEFINED(c.\"_etag\") AND INITCAP(c.\"_etag\") = 'X')");

            var plan = Plan(best);

            plan.Should().NotContain("CosmosFilter",
                "nothing about this disjunction is safe to push: " + plan);
        }

        /// <summary>
        /// SQL's own null tests observe absence, and a branch using one is not weakened either.
        /// </summary>
        /// <remarks>
        /// <c>x IS NULL</c> renders as <c>(NOT IS_DEFINED(x) OR IS_NULL(x))</c>, so it is true where
        /// the path is missing — but its Rex operator belongs to the standard table rather than to the
        /// Cosmos family, so checking only the latter let it through. A branch containing one can be
        /// true with the path absent, and weakening it to <c>IS_DEFINED</c> would have discarded
        /// exactly those rows.
        /// </remarks>
        [Fact]
        public void ABranchUsingSqlNullTestsIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT * FROM products AS c " +
                "WHERE c.\"id\" = 'x' OR (c.\"$.category\" IS NULL AND INITCAP(c.\"_etag\") = 'X')");

            var plan = Plan(best);

            plan.Should().NotContain("CosmosFilter",
                "a branch that can be true with the path absent implies nothing about definedness: " + plan);
        }

        /// <summary>
        /// A disjunction implies only the paths every branch needs, and a branch that is a negated
        /// conjunction of these needs none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Rel.Convert.CosmosFilterSplitRule"/> weakens a disjunction branch by branch, and
        /// where one branch has no weakening it gave the disjunction whole to the walk that collects the
        /// paths a predicate needs — which descended into every branch and pushed <c>IS_DEFINED</c> of
        /// each path it found. A disjunction is true where one branch is, so a document lacking the
        /// other branches' paths belongs in the result.
        /// </para>
        /// <para>
        /// The first two are the predicates ternary logic partitioning found, written as <c>NOT φ</c>.
        /// Calcite's converter pushes the <c>NOT</c> down through the connectives above an <c>IN</c>, so
        /// each arrives as a disjunction one of whose branches is <c>NOT (… AND …)</c>, which has no
        /// weakening. Measured against the emulator, the first sent <c>WHERE (IS_DEFINED(c.tags) AND
        /// IS_DEFINED(c.metadata.sku))</c> and kept none of the seven documents its negation is true of;
        /// the second sent <c>WHERE (IS_DEFINED(c.tags[0]) AND IS_DEFINED(c.price))</c> and kept one.
        /// The third is the shape written directly: it sent <c>WHERE IS_DEFINED(c.price)</c> and lost the
        /// shoe with no price, for which the negated branch is true.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("NOT (((JSON_VALUE(c.\"DOC\", '$.tags') IN ('Road Runner', 'BIKES') OR JSON_VALUE(c.\"DOC\", '$.metadata.sku') <> 'SPRINT') AND ((CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) - 3E0) = 5E0 AND (CAST(JSON_VALUE(c.\"DOC\", '$.name') AS VARCHAR) || '!') > 'bikes')))")]
        [InlineData("NOT ((((JSON_VALUE(c.\"DOC\", '$.name') || '!') = 'shoes' AND c.\"id\" > 'SPRINT') AND (JSON_VALUE(c.\"DOC\", '$.tags[0]') IN ('bikes', 'bikes') OR (CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) + 1E0) NOT IN (5E0, 340E0))))")]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Sprint')")]
        public void ADisjunctionWithANegatedCompoundBranchPushesNoPresence(string predicate)
        {
            var best = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE " + predicate);
            var plan = Plan(best);
            var sql = Render(FindCosmos(best));

            sql.Should().NotContain("IS_DEFINED", "the negated branch is true where the paths the others read are absent: " + sql);
            plan.Should().Contain("ClrCursorFilter", "the predicate is decided in process: " + plan);
        }

        /// <summary>
        /// A compound read as a value implies only what every one of its operands needs.
        /// </summary>
        /// <remarks>
        /// A comparison needs both of its operands, and a conjunction read as one of them is false where
        /// either conjunct is, whatever the other reads. Measured against the emulator, the first pushed
        /// <c>IS_DEFINED</c> of category, price and id and lost document 5, a shoe with no price whose
        /// conjunction is false and whose id is '5'; the second pushed category and price, and lost
        /// documents 5 and 7. What is left is the path the comparison's other side reads, which every
        /// document has.
        /// </remarks>
        [Fact]
        public void ACompoundReadAsAValueImpliesOnlyWhatEveryOperandNeeds()
        {
            var compared = Render(FindCosmos(PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes' AND JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100) <> (c.\"id\" = '5')")));

            compared.Should().EndWith("WHERE IS_DEFINED(c.id)");

            var negated = Render(FindCosmos(PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE (c.\"$.category\" = 'bikes' AND JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100) = FALSE")));

            negated.Should().NotContain("IS_DEFINED", "the conjunction is false where either conjunct is: " + negated);
        }

        /// <summary>
        /// The paths every branch of a disjunction needs are still implied.
        /// </summary>
        /// <remarks>
        /// The other side of the two above. A disjunction inside a branch implies the path both of its
        /// own branches read, so the conjunction around it pushes that beside its other path.
        /// </remarks>
        [Fact]
        public void APathEveryBranchNeedsIsStillImplied()
        {
            var sql = Render(FindCosmos(PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR " +
                "(INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Road Runner' AND (INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'X'))")));

            sql.Should().EndWith("WHERE ((c.price > @p0) OR (IS_DEFINED(c.name) AND IS_DEFINED(c.metadata.sku)))");
        }

        /// <summary>
        /// A path every branch reads is implied however each branch spells it.
        /// </summary>
        /// <remarks>
        /// The inner disjunction reads the category in both branches, once as the promoted column and
        /// once through the document. Matched by expression the two were different paths, the
        /// disjunction needed neither, and the weakening pushed <c>IS_DEFINED(c.name)</c> alone — sound,
        /// and less than it could. Matched by the path each resolves to, the category is needed too.
        /// </remarks>
        [Fact]
        public void APathEveryBranchNeedsIsImpliedHoweverEachSpellsIt()
        {
            var sql = Render(FindCosmos(PlanToAsync(
                "SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 300 OR " +
                "(INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) <> 'Zzz' AND (c.\"$.category\" = 'bikes' OR JSON_VALUE(c.\"DOC\", '$.category') = 'shoes'))")));

            sql.Should().EndWith("WHERE ((c.price > @p0) OR (IS_DEFINED(c.name) AND IS_DEFINED(c.category)))");
        }

        /// <summary>
        /// A negated compound, or one compared with <c>TRUE</c> or <c>FALSE</c>, is read for its truth or
        /// for its falsity, and implies what that needs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A disjunction is false only where every branch is, so false it needs what any branch does; a
        /// conjunction is true only where every conjunct is. A <c>NOT</c> swaps the two, and so does a
        /// comparison with <c>FALSE</c>. Each of these pushed nothing before: a negated compound needed
        /// nothing, and a compared one only what its operands need as values, which for a compound is
        /// the little every operand needs. Each now pushes both paths, and each agrees with Calcite on
        /// the emulator, as the differential corpus says.
        /// </para>
        /// <para>
        /// The last is the conjunction's falsity, which is what every conjunct needs to be false: both
        /// read the name, so the name is needed and nothing else.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("NOT (c.\"$.category\" = 'shoes' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2')", "WHERE (IS_DEFINED(c.category) AND IS_DEFINED(c.metadata.sku))")]
        [InlineData("NOT (NOT (c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2'))", "WHERE (IS_DEFINED(c.category) AND IS_DEFINED(c.metadata.sku))")]
        [InlineData("(c.\"$.category\" = 'bikes' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') = TRUE", "WHERE (IS_DEFINED(c.category) AND IS_DEFINED(c.metadata.sku))")]
        [InlineData("(c.\"$.category\" = 'shoes' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') = FALSE", "WHERE (IS_DEFINED(c.category) AND IS_DEFINED(c.metadata.sku))")]
        [InlineData("(c.\"$.category\" = 'shoes' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') <> TRUE", "WHERE (IS_DEFINED(c.category) AND IS_DEFINED(c.metadata.sku))")]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT ((c.\"$.category\" = 'shoes' OR INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) = 'B-2') <> FALSE)", "WHERE ((c.price > @p0) OR (IS_DEFINED(c.category) AND IS_DEFINED(c.metadata.sku)))")]
        [InlineData("JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 100 OR NOT (INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) = 'Sprint' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.name')) > 'A')", "WHERE ((c.price > @p0) OR IS_DEFINED(c.name))")]
        public void ACompoundIsReadForItsTruthOrItsFalsity(string predicate, string where)
        {
            var sql = Render(FindCosmos(PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE " + predicate)));

            sql.Should().EndWith(where);
        }

        /// <summary>
        /// A null test Calcite has folded into a <c>SEARCH</c> is still a null test, and is not
        /// weakened.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A host running Calcite's own rules simplifies the filter it moves beneath a projection, and
        /// <c>n IS NULL OR n = 'B-2'</c> comes out as <c>SEARCH(INITCAP(sku), Sarg['B-2'; NULL AS
        /// TRUE])</c>. Written apart, the null test kept the disjunction from being weakened; folded,
        /// the split rule read the <c>SEARCH</c> as a comparison that needs its path, and pushed
        /// <c>IS_DEFINED(c.metadata.sku)</c> under a predicate true of all seven products, where only
        /// one has a sku.
        /// </para>
        /// <para>
        /// Only through the host's rules: this harness without them, and the differential corpus,
        /// hand the split rule the disjunction as written. Declined, the folded form wins nothing, and
        /// the plan the planner keeps is the one over the disjunction as written.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANullTestFoldedIntoASearchIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT p.\"id\" FROM (SELECT c.\"id\", INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) AS \"n\" FROM products AS c) AS p WHERE p.\"n\" IS NULL OR p.\"n\" = 'B-2'",
                hostRewrites: true);

            var sql = Render(FindCosmos(best));

            sql.Should().NotContain("IS_DEFINED", "the folded null test is true where the path is absent: " + sql);
        }

        /// <summary>
        /// A call that answers a value over a null is not read through, and implies nothing about the
        /// paths beneath it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The walk that collects the paths a predicate needs descended into every operator but the
        /// connectives, on the premise that each answers null for a null. A constructor does not: an
        /// array or a map holding a null is not null, so a comparison over its cardinality or over an
        /// element it also holds is true with the path absent. Measured against the emulator, the
        /// first pushed <c>WHERE IS_DEFINED(c.metadata.sku)</c> and kept one of the seven documents it
        /// is true of. <c>CONCAT_WS</c> and <c>ARRAY_CONTAINS</c>, which lost rows the same way, are
        /// library functions this harness does not register; the differential corpus has them.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("CARDINALITY(ARRAY[INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku'))]) = 1")]
        [InlineData("ARRAY[INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')), 'x'][2] = 'x'")]
        [InlineData("CARDINALITY(MAP['k', INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku'))]) = 1")]
        public void ACallThatAnswersAValueOverANullImpliesNoPresence(string predicate)
        {
            var best = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE " + predicate);
            var sql = Render(FindCosmos(best));

            sql.Should().NotContain("IS_DEFINED", "the call is not null where the path beneath it is absent: " + sql);
        }

        /// <summary>
        /// A function that is null for a null is still read through to the path it reads.
        /// </summary>
        /// <remarks>
        /// The other side of the one above. Calcite classifies none of these — each reports
        /// <see cref="Strong.Policy.AS_IS"/> — so each is one the split rule names, measured in process
        /// to answer null over a path no document has. The last is an accessor over a scalar rendering,
        /// which addresses no path of its own and is null where the rendering it parses is.
        /// </remarks>
        [Theory]
        [InlineData("REPLACE(INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')), 'B', 'C') = 'C-2'", "c.metadata.sku")]
        [InlineData("POSITION('-' IN INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku'))) = 2", "c.metadata.sku")]
        [InlineData("CHAR_LENGTH(SUBSTRING(INITCAP(JSON_VALUE(c.\"DOC\", '$.metadata.sku')) FROM 2)) = 2", "c.metadata.sku")]
        [InlineData("INITCAP(JSON_VALUE(JSON_VALUE(c.\"DOC\", '$.name'), '$')) = 'Sprint'", "c.name")]
        public void AFunctionNullForANullStillImpliesThePathItReads(string predicate, string path)
        {
            var best = PlanToAsync("SELECT c.\"id\" FROM products AS c WHERE " + predicate);
            var sql = Render(FindCosmos(best));

            sql.Should().EndWith($"WHERE IS_DEFINED({path})");
        }

        /// <summary>
        /// A sort above a pushed aggregate stays in Calcite, and the aggregate still pushes.
        /// </summary>
        /// <remarks>
        /// Cosmos rejects <c>GROUP BY</c> and <c>ORDER BY</c> in one statement. <c>CosmosSort</c>
        /// refuses the combination when it renders, but refusing only there is too late — the rule
        /// would already have produced a node the planner cannot implement, which fails rather than
        /// planning something slower. Declining in the rule is also the better plan: the sort then runs
        /// over one row per group instead of over the container.
        /// </remarks>
        [Fact]
        public void ASortAboveAPushedAggregateStaysInCalcite()
        {
            var best = PlanToAsync("SELECT c.\"$.category\", COUNT(*) FROM products AS c GROUP BY c.\"$.category\" ORDER BY c.\"$.category\"");
            var plan = Plan(best);

            plan.Should().Contain("CosmosAggregate", "the grouping is still worth pushing: " + plan);
            plan.Should().NotContain("CosmosSort", "Cosmos will not take ORDER BY alongside GROUP BY: " + plan);
        }

        /// <summary>
        /// The same split applies to a filter sitting above a projection.
        /// </summary>
        /// <remarks>
        /// The argument does not depend on what the filter sits on — dropping a conjunct only ever
        /// weakens, so the service discards nothing the full predicate would have kept. The rule used
        /// to match a filter directly over the scan and nothing else, which meant a projection between
        /// the two cost the whole pushdown rather than the untranslatable half of it.
        /// </remarks>
        [Fact]
        public void APredicateAboveAProjectionIsSplitToo()
        {
            var best = PlanToAsync(
                "SELECT * FROM (SELECT c.\"$.category\" AS cat, c.\"id\" AS ident FROM products AS c) AS t " +
                "WHERE t.cat = 'bikes' AND INITCAP(t.ident) = 'X'");

            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter");
            plan.Should().Contain("INITCAP");
        }

        /// <remarks>
        /// The pushed half carries the renderable conjunct and whatever the other one implies, never
        /// the other one itself. <c>INITCAP</c> has no Cosmos form, so it stays above and is rechecked;
        /// that it is a comparison at all says the path it reads is defined, and that much the service
        /// can apply. The partition key is still recovered from the conjunct that pins it.
        /// </remarks>
        [Fact]
        public void ThePushedHalfCarriesTheRenderableConjunctAndWhatTheOtherImplies()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'");
            var cosmos = FindCosmos(best);

            var query = Query(cosmos);
            query.Sql.Should().Contain("(c.category = @p0)");
            query.Sql.Should().Contain("IS_DEFINED(c.id)");
            query.Sql.Should().NotContain("INITCAP");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// A wholly renderable predicate is not split; there is nothing to leave behind.
        /// </remarks>
        [Fact]
        public void AWhollyRenderablePredicateIsNotSplit()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = 'x'");

            Plan(best).Split("CosmosFilter").Length.Should().Be(2);
        }

        /// <summary>
        /// Returns the root of the pushed-down Cosmos subtree — the highest node in the convention,
        /// which is the one that renders the whole statement.
        /// </summary>
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


        // ── Navigating the document ───────────────────────────────────────────────

        /// <remarks>
        /// A document path of any depth resolves, and the translator folds the whole path into one
        /// Cosmos path rather than nesting accessors.
        /// </remarks>
        [Fact]
        public void NestedPropertiesResolveToASinglePath()
        {
            var best = PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.metadata.sku') AS \"sku\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"sku\": (IS_PRIMITIVE(c.metadata.sku) ? c.metadata.sku : null) } FROM products c");
        }

        [Fact]
        public void ThreeLevelsResolveJustAsFar()
        {
            var best = PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.a.b.c') AS \"deep\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"deep\": (IS_PRIMITIVE(c.a.b.c) ? c.a.b.c : null) } FROM products c");
        }

        /// <remarks>
        /// An array index is a path segment like any other.
        /// </remarks>
        [Fact]
        public void ArrayIndexingIsPartOfThePath()
        {
            // Subscripted in the path rather than around it. A JSON path carries the index, and
            // JSON_VALUE is typed VARCHAR, so there is nothing for SQL's own subscript to apply to.
            var best = PlanToCosmos("SELECT JSON_VALUE(c.\"DOC\", '$.tags[0]') AS \"first\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"first\": (IS_PRIMITIVE(c.tags[0]) ? c.tags[0] : null) } FROM products c");
        }

        /// <remarks>
        /// A non-constant key has no path form — the statement addresses a property by name, and the
        /// name is not known until the row is read — so it is declined rather than guessed at.
        /// </remarks>
        [Fact]
        public void ANonConstantKeyIsNotAPath()
        {
            var act = () => PlanToCosmos("SELECT c.\"DOC\"[c.\"id\"] AS \"dynamic\" FROM products AS c");

            act.Should().Throw<Exception>();
        }


        // ── Testing whether a property exists ─────────────────────────────────────

        /// <remarks>
        /// <para>
        /// The SQL spelling. Cosmos distinguishes an absent property from one present and null and SQL
        /// does not, so this renders as both tests — which is what makes it match a document that simply
        /// lacks the property.
        /// </para>
        /// <para>
        /// <b>And as the third test the accessor needs.</b> <c>JSON_VALUE</c> is null for an object or
        /// an array, which is exactly what <c>metadata</c> is in the documents that have it, so the two
        /// tests alone kept every one of them — measured against the emulator over document 2 of the
        /// differential seed, which Calcite does not return. This test pinned that rendering as right
        /// until it was found. <c>IS_PRIMITIVE</c> is the line <c>JSON_VALUE</c> draws, the same one
        /// its projection is guarded by.
        /// </para>
        /// </remarks>
        [Fact]
        public void IsNotNullOnADocumentPropertyTestsBothCosmosStates()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata') IS NOT NULL");

            Render(best).Should().EndWith("WHERE (IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata) AND IS_PRIMITIVE(c.metadata))");
        }

        /// <summary>
        /// <c>IS NULL</c> over an accessor is true wherever the accessor answers null.
        /// </summary>
        /// <remarks>
        /// The complement, written so that the absent case is decided by <c>IS_DEFINED</c> before any
        /// type test is asked: the record disagrees about what <c>IS_PRIMITIVE</c> says of an absent
        /// path, and <c>NOT IS_DEFINED(p) OR …</c> is true there whatever it says.
        /// </remarks>
        [Fact]
        public void IsNullOverAnAccessorIsTrueWhereTheAccessorAnswersNull()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata') IS NULL"))
                .Should().Be("SELECT VALUE { \"id\": c.id } FROM products c WHERE (NOT IS_DEFINED(c.metadata) OR IS_NULL(c.metadata) OR NOT IS_PRIMITIVE(c.metadata))");
        }

        /// <summary>
        /// The null test reads each accessor's own null set: the type a <c>RETURNING</c> names, and
        /// the fragment <c>JSON_QUERY</c> answers.
        /// </summary>
        /// <remarks>
        /// <c>JSON_QUERY</c> is the mirror of <c>JSON_VALUE</c>, null for every scalar, so <c>IS NULL</c>
        /// over it was false of a stored string Calcite answers null for. A typed <c>RETURNING</c> is
        /// null wherever the value is not of its type — an object, which Calcite answers null for, and
        /// another scalar, which the standard answers null for and Calcite raises over. The raw value
        /// is null only where it is absent or null, and keeps the two tests it always had.
        /// </remarks>
        [Fact]
        public void TheNullTestReadsEachAccessorsOwnNullSet()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_QUERY(c.\"DOC\", '$.metadata') IS NULL"))
                .Should().EndWith("WHERE (NOT IS_DEFINED(c.metadata) OR IS_NULL(c.metadata) OR NOT (IS_OBJECT(c.metadata) OR IS_ARRAY(c.metadata)))");

            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.metadata' RETURNING INTEGER) IS NOT NULL"))
                .Should().EndWith("WHERE (IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata) AND IS_NUMBER(c.metadata))");

            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" IS NULL"))
                .Should().EndWith("WHERE (NOT IS_DEFINED(c.category) OR IS_NULL(c.category))");
        }

        /// <summary>
        /// A view's column bound to the accessor is held to the accessor's null set.
        /// </summary>
        /// <remarks>
        /// The reading the binding carries is what says the column is the accessor's text rather than
        /// the value — the same thing that holds a comparison over it to the accessor's tests (#83).
        /// </remarks>
        [Fact]
        public void IsNotNullOverAViewColumnReadsTheAccessorsNullSet()
        {
            Render(PlanToCosmos("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM products AS c) AS p WHERE p.\"Label\" IS NOT NULL"))
                .Should().EndWith("WHERE (IS_DEFINED(c.label) AND NOT IS_NULL(c.label) AND IS_PRIMITIVE(c.label))");
        }

        /// <remarks>
        /// The exact spelling, for a query that needs to tell absent from null — which SQL cannot say and
        /// this adapter's own operator can. It reaches the validator through the chained operator table.
        /// </remarks>
        [Fact]
        public void IsDefinedTestsExistenceAlone()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(JSON_VALUE(c.\"DOC\", '$.metadata'))");

            Render(best).Should().Contain("WHERE IS_DEFINED(c.metadata)");
        }

        /// <remarks>
        /// Existence of a nested property, which is the same question one level down and the same path.
        /// </remarks>
        [Fact]
        public void IsDefinedReachesANestedProperty()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(JSON_VALUE(c.\"DOC\", '$.metadata.sku'))");

            Render(best).Should().Contain("WHERE IS_DEFINED(c.metadata.sku)");
        }


        // ── Ranking by a scoring function ─────────────────────────────────────────

        /// <remarks>
        /// Calcite expresses this as three nodes — project the score, sort on it, project it away — and
        /// the first is a statement Cosmos rejects, a scoring function not being projectable. The whole
        /// shape collapses into one clause, and the score never appears in the select list.
        /// </remarks>
        [Fact]
        public void OrderingByAScoreBecomesOrderByRank()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel') FETCH FIRST 10 ROWS ONLY");
            var sql = Render(best);

            sql.Should().Be("SELECT TOP 10 VALUE { \"id\": c.id } FROM products c ORDER BY RANK FULLTEXTSCORE(c.name, @p0)");
            sql.Should().NotContain("\"$f");
        }

        /// <remarks>
        /// The keyword binds like any other literal, so the statement text does not vary with it.
        /// </remarks>
        [Fact]
        public void TheRankKeywordIsBound()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel') FETCH FIRST 5 ROWS ONLY"));

            query.Parameters.Should().ContainSingle().Which.Value.Should().Be("steel");
        }

        /// <remarks>
        /// RRF fuses two scores, and its arguments are themselves scoring functions rather than paths.
        /// </remarks>
        [Fact]
        public void RrfFusesTwoScores()
        {
            var best = PlanToCosmos(
                "SELECT c.\"id\" FROM products AS c " +
                "ORDER BY RRF(FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel'), FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.tags'), 'frame')) " +
                "FETCH FIRST 10 ROWS ONLY");

            Render(best).Should().Contain("ORDER BY RANK RRF(FULLTEXTSCORE(c.name, @p0), FULLTEXTSCORE(c.tags, @p1))");
        }

        /// <remarks>
        /// A scoring function anywhere but the rank clause is refused: the service will not project one,
        /// and will not filter on one either.
        /// </remarks>
        [Fact]
        public void AProjectedScoreIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel') AS \"s\" FROM products AS c");

            act.Should().Throw<Exception>();
        }

        [Fact]
        public void AScoreInAPredicateIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel') > 1");

            act.Should().Throw<Exception>();
        }


        /// <remarks>
        /// The shape the cast case exists for, reached from SQL rather than built by hand: comparing
        /// against a function that returns a double coerces the literal, so the predicate arrives with a
        /// cast wrapped around the bound. Declining it would decline the predicate.
        /// </remarks>
        [Fact]
        public void AComparisonAgainstAVectorDistancePushes()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE VECTORDISTANCE(JSON_VALUE(c.\"DOC\", '$.a'), JSON_VALUE(c.\"DOC\", '$.b')) < 0.5");

            Render(best).Should().Contain("WHERE (VECTORDISTANCE(c.a, c.b) < @p0)");
        }


        // ── The declaration prices a full text call, and gates a vector one ──────────
        //
        // A full text predicate over a path the container declares nothing about was refused, on a
        // measurement of a bodyless 400 that no longer reproduces: measured against three accounts
        // and four containers, the service answers the predicates and the score over an undeclared
        // path, over a container with no policy, and on an account without the capability (#85). So
        // the declaration decides the price -- an index seek over /name, a scan over /description --
        // and the plan pushes either way. Refusing it had the worse failure: the declined call was
        // left in process, where it has no body.

        /// <summary>
        /// The predicate pushes over an undeclared path, and costs more than over a declared one.
        /// </summary>
        [Fact]
        public void AFullTextPredicateOverAnUndeclaredPathIsPushedDownAndPricedAsAScan()
        {
            var undeclared = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINS(JSON_VALUE(c.\"DOC\", '$.description'), 'steel')");
            var declared = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINS(JSON_VALUE(c.\"DOC\", '$.name'), 'steel')");

            Render(undeclared).Should().Contain("WHERE FULLTEXTCONTAINS(c.description, @p0)");

            SelfCost(Find<CosmosFilter>(declared)).isLt(SelfCost(Find<CosmosFilter>(undeclared)))
                .Should().BeTrue("a scan costs more than an index seek");
        }

        /// <summary>
        /// And the score, which reaches the rank clause through a different rule and is priced by
        /// the node it becomes.
        /// </summary>
        [Fact]
        public void ARankOverAnUndeclaredPathIsPushedDownAndPricedAsAScan()
        {
            var undeclared = PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.description'), 'steel') FETCH FIRST 10 ROWS ONLY");
            var declared = PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(JSON_VALUE(c.\"DOC\", '$.name'), 'steel') FETCH FIRST 10 ROWS ONLY");

            Render(undeclared).Should().Contain("ORDER BY RANK FULLTEXTSCORE(c.description, @p0)");

            SelfCost(Find<CosmosRank>(declared)).isLt(SelfCost(Find<CosmosRank>(undeclared)))
                .Should().BeTrue("a scan costs more than an index seek");
        }

        /// <summary>
        /// What a node says it costs on its own, which is where the declaration is priced.
        /// </summary>
        static RelOptCost SelfCost(RelNode node)
        {
            return node.computeSelfCost(node.getCluster().getPlanner(), node.getCluster().getMetadataQuery())!;
        }

        static T Find<T>(RelNode node) where T : RelNode
        {
            if (node is T found)
                return found;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (Find<T>((RelNode)inputs.get(i)) is T inner)
                    return inner;

            throw new XunitException($"No {typeof(T).Name} in the plan: " + Plan(node));
        }

        /// <remarks>
        /// A vector distance needs one of its two vectors to be a declared path. <c>/a</c> is one and
        /// <c>/b</c> is not, so the test above pushes on the strength of the first argument alone;
        /// with neither declared there is nothing for the service to search.
        /// </remarks>
        [Fact]
        public void AVectorDistanceOverUndeclaredPathsIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE VECTORDISTANCE(JSON_VALUE(c.\"DOC\", '$.b'), JSON_VALUE(c.\"DOC\", '$.d')) < 0.5");

            act.Should().Throw<Exception>();
        }

        // ── Point lookup ──────────────────────────────────────────────────────────

        /// <remarks>
        /// A lookup by id and a complete partition key is a read, not a query: about 1 RU against the
        /// 2.3 a query costs at best, and no query engine.
        /// </remarks>
        [Fact]
        public void IdAndPartitionKeyBecomeAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes'"));

            query.PointReadId.Should().Be("x");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// <b>The predicate must say nothing else.</b> A point read applies no predicate of its own, so
        /// under an extra conjunct it would return a document the query excludes — a wrong answer rather
        /// than a slow one. The statement is still rendered and still executed; it is just executed as a
        /// query.
        /// </remarks>
        [Fact]
        public void AResidualPredicateRulesOutAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes' AND c.\"_ts\" > 100"));

            query.PointReadId.Should().BeNull();
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        [Fact]
        public void AnIdWithoutThePartitionKeyIsNotAPointRead()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'")).PointReadId.Should().BeNull();
        }

        [Fact]
        public void APartitionKeyWithoutAnIdIsNotAPointRead()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes'")).PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// A read returns one document whole. A row limit and an ordering describe a result set rather
        /// than a document, so either rules it out even though the predicate would allow it.
        /// </remarks>
        [Fact]
        public void ARowLimitRulesOutAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes' FETCH FIRST 1 ROWS ONLY"));

            query.PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// Under a disjunction an equality does not constrain the whole predicate, so it pins nothing —
        /// the same reason the partition key is not recovered from one.
        /// </remarks>
        [Fact]
        public void ADisjunctionIsNotAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE (c.\"id\" = 'x' AND c.\"$.category\" = 'bikes') OR c.\"id\" = 'y'"));

            query.PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// A projection of plain paths still reads: the converter walks each path in the returned
        /// document rather than naming a property of an object the statement never constructed.
        /// </remarks>
        [Fact]
        public void AProjectionOfPathsStillPointReads()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\", c.\"$.category\" FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes'"));

            query.PointReadId.Should().Be("x");
        }


        // ── Hierarchical partition keys ───────────────────────────────────────────

        static readonly CosmosContainerMetadata Tenanted = new("products", new[] { "/tenant", "/user" });

        /// <summary>
        /// Plans against a container whose partition key is hierarchical.
        /// </summary>
        CosmosQuery TenantedQuery(string sql)
        {
            _table = new CosmosTable(Tenanted);

            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(_table.Convention).simplify()));
            var best = planner.findBestExp();

            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), Tenanted);
            implementor.Visit(best);
            return implementor.Build();
        }

        /// <remarks>
        /// Cosmos routes on any prefix of a hierarchical key, so pinning the outermost path reaches the
        /// partitions under that tenant rather than every partition in the container. Recovering only a
        /// complete key threw that away.
        /// </remarks>
        [Fact]
        public void APinnedOutermostPathRoutesOnThePrefix()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"$.tenant\" = 'acme'");

            query.PartitionKeyValues.Should().Equal("acme");
            query.PartitionKeyIsComplete.Should().BeFalse();
        }

        [Fact]
        public void PinningEveryPathIsACompleteKey()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"$.tenant\" = 'acme' AND c.\"$.user\" = 'kim'");

            query.PartitionKeyValues.Should().Equal("acme", "kim");
            query.PartitionKeyIsComplete.Should().BeTrue();
        }

        /// <remarks>
        /// Prefix means prefix. Routing is on the leading components, so pinning an inner path without
        /// the one above it narrows nothing and must not be presented as though it did.
        /// </remarks>
        [Fact]
        public void AnInnerPathWithoutTheOuterRoutesNothing()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"$.user\" = 'kim'");

            query.PartitionKeyValues.Should().BeNull();
        }

        /// <remarks>
        /// A prefix routes to a set of partitions and does not identify a document, so it cannot carry
        /// a point read however much of the predicate is an id.
        /// </remarks>
        [Fact]
        public void APrefixCannotCarryAPointRead()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"$.tenant\" = 'acme' AND c.\"id\" = 'x'");

            query.PartitionKeyValues.Should().Equal("acme");
            query.PointReadId.Should().BeNull();
        }


        // ── What the table claims about ordering ──────────────────────────────────

        /// <remarks>
        /// A probe, not a specification. RelOptTableImpl.getCollationList returns the statistic's
        /// collations, and RelMdCollation reports them as the collation <em>of a scan</em> — that is,
        /// the order rows already arrive in. Whether the planner is being told that is what this asks.
        /// </remarks>
        [Fact]
        public void AScanIsNotClaimedToBeSorted()
        {
            var best = PlanToCosmos("SELECT * FROM products");
            var mq = best.getCluster().getMetadataQuery();

            var collations = mq.collations(best);

            // The container declares a composite index over (/id, /_ts). An index permits an ORDER BY;
            // it does not order a query that has none, and Cosmos guarantees no order without one. A
            // scan claiming a collation would licence the planner to drop a Sort that asked for it.
            collations.Should().NotBeNull();
            collations.size().Should().Be(0, "a Cosmos scan returns rows in no guaranteed order");
        }

    }

}