using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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
using org.apache.calcite.sql.util;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The Cosmos functions reached the way a connection reaches them: resolved against the schema,
    /// with nothing chained into the validator's operator table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A validator resolves a function name against the operator table it was built with, chained
    /// with the catalog reader — and the catalog reader resolves the schema's own functions. That
    /// chaining is the one thing these tests reproduce by hand: <c>CalcitePrepareImpl</c> does it for
    /// every statement a connection prepares, and everything else here is what a host would already
    /// have. <c>CosmosConnectionFunctionTests</c> is the same claim made against a real connection,
    /// and needs a service to make it.
    /// </para>
    /// <para>
    /// What arrives in the plan is not the operator <see cref="CosmosOperators"/> declares: Calcite
    /// builds a <c>SqlUserDefinedFunction</c> around the schema's declaration, carrying its name and
    /// arity. So these also pin the part that makes that work — that the translator and the rules ask
    /// a call for its name rather than for its identity.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosSchemaFunctionTests
    {

        /// <remarks>
        /// <c>/name</c> and <c>/tags</c> are declared full text searchable because a full text
        /// function pushes only over a path the container declares. These tests are about where a
        /// name is <em>resolved</em>, so the declaration is here to keep the gate from being what
        /// they measure — <c>CosmosPlannerTests</c> measures the gate.
        /// </remarks>
        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" }, fullTextPaths: new[] { "/name", "/tags" });

        /// <summary>
        /// Plans a statement against a <see cref="CosmosSchema"/> the connection is rooted in.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="chain">
        /// Whether to also chain <see cref="CosmosOperators.Instance"/>, as the README used to require
        /// and still permits.
        /// </param>
        /// <param name="account">Whether to root the connection at an account rather than a database.</param>
        /// <param name="libraries">
        /// Whether to chain every <c>SqlLibrary</c> table as well, which is what a connection setting
        /// <c>fun</c> does — and which is chained ahead of the catalog reader, so it is where a name
        /// Calcite also uses would shadow the schema's declaration.
        /// </param>
        static RelNode Plan(string sql, bool chain = false, bool account = false, bool libraries = false)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            // The schema builds its own tables, so the convention to plan into is read back out of it
            // rather than built beside it. Two conventions over one container describe their rules
            // identically and a planner refuses the second, which is what building one here caused.
            var schema = account
                ? (org.apache.calcite.schema.Schema)new CosmosAccountSchema(new[] { new KeyValuePair<string, IReadOnlyList<CosmosContainerMetadata>>("inventory", new[] { Products }) })
                : new CosmosSchema(new[] { Products });

            var table = (CosmosTable)(account
                ? ((org.apache.calcite.schema.Schema)schema.subSchemas().get("inventory")).tables().get("products")
                : schema.tables().get("products"));

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("cosmos", schema);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            // The connection's default schema, which is where an unqualified function name is looked
            // for — that and the root, and nothing else.
            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.singletonList("cosmos"),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            // What CalcitePrepareImpl builds: the fun libraries the connection names, chained with the
            // catalog reader. Nothing Cosmos-specific unless the caller asks for it.
            var tables = new List<org.apache.calcite.sql.SqlOperatorTable> { SqlStdOperatorTable.instance() };

            if (libraries)
                tables.Add(org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
                    java.util.EnumSet.allOf(java.lang.Class.forName("org.apache.calcite.sql.fun.SqlLibrary"))));

            if (chain)
                tables.Add(CosmosOperators.Instance);

            tables.Add(catalogReader);

            var operators = SqlOperatorTables.chain(tables.ToArray());

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(table.Convention).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Renders a planned tree to the statement it would execute.
        /// </summary>
        static string Render(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build().Sql;
        }

        /// <summary>
        /// The whole point: no operator table, and the name still resolves.
        /// </summary>
        [TestMethod]
        public void AFunctionResolvesThroughTheSchemaAlone()
        {
            Render(Plan("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['price'])"))
                .Should().Be("SELECT VALUE { \"id\": c.id } FROM products c WHERE IS_DEFINED(c.price)");
        }

        /// <summary>
        /// A host that chains the operator table as well keeps working.
        /// </summary>
        /// <remarks>
        /// Two tables now answer for the same name, which is a duplicate only if something insists on
        /// one answer. Nothing does: overload resolution takes the first candidate whose arity fits,
        /// so the chained operator wins and the schema's declaration is never reached. The README's
        /// instruction is therefore stale rather than wrong, and says so.
        /// </remarks>
        [TestMethod]
        public void ChainingTheOperatorTableAsWellIsNotADuplicate()
        {
            const string Sql = "SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['price'])";

            Render(Plan(Sql, chain: true)).Should().Be(Render(Plan(Sql)));
        }

        /// <summary>
        /// The full text predicates, including the variadic ones.
        /// </summary>
        [TestMethod]
        public void TheFullTextPredicatesResolve()
        {
            Render(Plan("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINS(c.\"_MAP\"['name'], 'steel')"))
                .Should().Contain("FULLTEXTCONTAINS(c.name, @p0)");

            Render(Plan("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINSALL(c.\"_MAP\"['name'], 'steel', 'frame', 'road')"))
                .Should().Contain("FULLTEXTCONTAINSALL(c.name, @p0, @p1, @p2)");

            Render(Plan("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINSANY(c.\"_MAP\"['name'], 'steel', 'frame')"))
                .Should().Contain("FULLTEXTCONTAINSANY(c.name, @p0, @p1)");
        }

        /// <summary>
        /// A variadic function is declared with a fixed number of optional parameters, and this is
        /// where that stops being invisible.
        /// </summary>
        /// <remarks>
        /// Calcite derives a function's operand count range from its parameter list, so a schema
        /// function is exactly as variadic as the number of parameters it declares. The limit is
        /// reachable rather than theoretical, so it is measured on both sides — a call at the limit
        /// resolves, and one past it does not, through the schema alone. Chaining the operator table
        /// answers the same call, its checker being genuinely variadic; that is what the second half
        /// asserts, and it is the workaround for a query that needs more keywords than this.
        /// </remarks>
        [TestMethod]
        public void AVariadicFunctionResolvesUpToTheDeclaredLimit()
        {
            static string Keywords(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"'k{i}'"));

            var atLimit = $"SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINSALL(c.\"_MAP\"['name'], {Keywords(CosmosSchemaFunctions.VariadicOperandLimit - 1)})";
            var past = $"SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINSALL(c.\"_MAP\"['name'], {Keywords(CosmosSchemaFunctions.VariadicOperandLimit)})";

            Render(Plan(atLimit)).Should().Contain("FULLTEXTCONTAINSALL(c.name");

            var beyond = () => Plan(past);
            beyond.Should().Throw<Exception>("a schema function is as variadic as its parameter list, and no more");

            var chained = () => Plan(past, chain: true);
            chained.Should().NotThrow("the operator table's checker is variadic, and remains the way past the limit");
        }

        /// <summary>
        /// Ordering by a score reaches the rank clause through the schema route too.
        /// </summary>
        /// <remarks>
        /// The rule that recognises the shape asks the ordering expression whether it is a scoring
        /// function, and asks by name. This is what that buys.
        /// </remarks>
        [TestMethod]
        public void OrderingByAScoreBecomesARankClause()
        {
            Render(Plan("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') FETCH FIRST 10 ROWS ONLY"))
                .Should().Be("SELECT TOP 10 VALUE { \"id\": c.id } FROM products c ORDER BY RANK FULLTEXTSCORE(c.name, @p0)");
        }

        /// <summary>
        /// And <c>RRF</c>, whose arguments are themselves scores.
        /// </summary>
        [TestMethod]
        public void ScoresFuseThroughTheSchemaRouteToo()
        {
            Render(Plan(
                    "SELECT c.\"id\" FROM products AS c " +
                    "ORDER BY RRF(FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel'), FULLTEXTSCORE(c.\"_MAP\"['tags'], 'frame')) " +
                    "FETCH FIRST 10 ROWS ONLY"))
                .Should().Contain("ORDER BY RANK RRF(FULLTEXTSCORE(c.name, @p0), FULLTEXTSCORE(c.tags, @p1))");
        }

        /// <summary>
        /// The restriction survives the change of route.
        /// </summary>
        /// <remarks>
        /// A score is legal in an <c>ORDER BY RANK</c> clause and nowhere else. The translator refuses
        /// it elsewhere by name, so a call resolved through the schema is refused for the same reason
        /// — which is the half of this that a name-based check had to keep.
        /// </remarks>
        [TestMethod]
        public void AProjectedScoreIsStillRefused()
        {
            var projected = () => Plan("SELECT FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') AS \"s\" FROM products AS c");

            projected.Should()
                .Throw<Exception>("Cosmos will not project a score, and there is no in-process implementation either")
                .Where(e => e.Message.Contains("No match found for function signature") == false,
                    "the refusal has to be the planner declining a resolved call, not the name failing to resolve");
        }

        /// <summary>
        /// And so does the gate on what the container declares.
        /// </summary>
        /// <remarks>
        /// The other half of the same claim. A full text predicate pushes only over a path the
        /// container declares searchable, and that check is reached from the call's name like every
        /// other — so a call Calcite built around a schema declaration is held to it as well.
        /// <c>/name</c> is declared and <c>/description</c> is not.
        /// </remarks>
        [TestMethod]
        public void AnUndeclaredPathIsStillRefused()
        {
            var undeclared = () => Plan("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINS(c.\"_MAP\"['description'], 'steel')");

            undeclared.Should()
                .Throw<Exception>("the container declares nothing about the path, and the service refuses the statement")
                .Where(e => e.Message.Contains("No match found for function signature") == false,
                    "the refusal has to be the planner declining a resolved call, not the name failing to resolve");
        }

        /// <summary>
        /// Where the functions live when a model exposes the account rather than one database.
        /// </summary>
        /// <remarks>
        /// An unqualified name is looked for in the connection's default schema and in the root, never
        /// in a subschema — so a query that names <c>"inventory"."products"</c> resolves the function
        /// against the account it descended from. Declaring them at both levels is what makes that
        /// work, and this is the case that needs the account level.
        /// </remarks>
        [TestMethod]
        public void AnAccountRootedQueryResolvesThemToo()
        {
            Render(Plan("SELECT c.\"id\" FROM \"inventory\".\"products\" AS c WHERE IS_DEFINED(c.\"_MAP\"['price'])", account: true))
                .Should().Be("SELECT VALUE { \"id\": c.id } FROM products c WHERE IS_DEFINED(c.price)");
        }

        /// <summary>
        /// None of the declarations carries a body, and asking for one says so.
        /// </summary>
        /// <remarks>
        /// A schema function Calcite can implement is one bound to a method, and binding one here
        /// would let a call that cannot be pushed down plan anyway and then answer with something
        /// Cosmos never computed. So there is no body — but declining the interface outright left
        /// Calcite to report it, as <c>User defined function FULLTEXTSCORE must implement
        /// ImplementableFunction</c>, which names an interface rather than the reason. The refusal is
        /// the same refusal at the same moment; what this pins is that it arrives in words that name
        /// the function and say why.
        /// </remarks>
        [TestMethod]
        public void NoneOfThemHasABody()
        {
            var schema = new CosmosSchema(new[] { Products });
            var names = ((org.apache.calcite.schema.Schema)schema).getFunctionNames();

            names.size().Should().BeGreaterThan(0);

            var iterator = names.iterator();
            while (iterator.hasNext())
            {
                var name = (string)iterator.next();
                var functions = ((org.apache.calcite.schema.Schema)schema).getFunctions(name);

                functions.size().Should().BeGreaterThan(0);

                var functionIterator = functions.iterator();
                while (functionIterator.hasNext())
                {
                    var function = functionIterator.next();
                    function.Should().BeAssignableTo<org.apache.calcite.schema.ScalarFunction>();

                    var implementable = function.Should()
                        .BeAssignableTo<org.apache.calcite.schema.ImplementableFunction>(
                            "'{0}' has to be the one that refuses, so that the refusal can say why", name)
                        .Which;

                    var act = () => implementable.getImplementor();

                    act.Should().Throw<java.lang.UnsupportedOperationException>(
                            "'{0}' exists to be rendered into a statement, not evaluated here", name)
                        .WithMessage("*" + name + "*");
                }
            }
        }

        /// <summary>
        /// Every operator the table offers is offered by the schema as well.
        /// </summary>
        /// <remarks>
        /// Derived from the table rather than listed again, so this is a guard on the derivation
        /// rather than on a second list: an operator added to <see cref="CosmosOperators"/> and
        /// reachable through a planner a host built but not through a connection is the defect the
        /// schema declarations exist to close.
        /// </remarks>
        [TestMethod]
        public void TheSchemaOffersEverythingTheOperatorTableDoes()
        {
            var declared = new List<string>();
            var names = ((org.apache.calcite.schema.Schema)new CosmosSchema(new[] { Products })).getFunctionNames();

            var iterator = names.iterator();
            while (iterator.hasNext())
                declared.Add((string)iterator.next());

            var operators = CosmosOperators.Instance.getOperatorList();
            for (var i = 0; i < operators.size(); i++)
                declared.Should().Contain(((org.apache.calcite.sql.SqlOperator)operators.get(i)).getName());
        }

        /// <summary>
        /// The same names still resolve to the schema's functions with every <c>fun</c> library
        /// chained ahead of the catalog reader.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The claim below stated the other way round, and this is the half that would actually bite a
        /// host: <c>Fun = "all"</c> is an ordinary connection string, and the library table it builds
        /// is chained before the catalog reader. Rendering the statement is what proves the Cosmos
        /// operator answered — a shadowing library function would type-check and then fail to
        /// translate, or worse, translate as something else.
        /// </para>
        /// <para>
        /// <c>REVERSE</c> first, and not incidentally: Calcite's standard table does not carry it, so
        /// it resolves only where the libraries really were chained. Without it this would pass just as
        /// well against a library table that had failed to load, which is how a test like this goes
        /// quietly wrong.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void TheFunLibrariesDoNotShadowThem()
        {
            Render(Plan("SELECT REVERSE(c.\"id\") AS \"r\" FROM products AS c", libraries: true))
                .Should().Contain("REVERSE(c.id)");

            var withoutLibraries = () => Plan("SELECT REVERSE(c.\"id\") AS \"r\" FROM products AS c");
            withoutLibraries.Should().Throw<Exception>("REVERSE is a library function, so chaining them above is doing something");

            Render(Plan("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['price'])", libraries: true))
                .Should().Be("SELECT VALUE { \"id\": c.id } FROM products c WHERE IS_DEFINED(c.price)");

            Render(Plan("SELECT c.\"id\" FROM products AS c WHERE REGEXMATCH(c.\"id\", '^a')", libraries: true))
                .Should().Contain("REGEXMATCH(c.id, @p0)");

            Render(Plan("SELECT \"StringToArray\"(c.\"id\") AS \"a\" FROM products AS c", libraries: true))
                .Should().Contain("StringToArray(c.id)");
        }

        /// <summary>
        /// Every one of these names is Cosmos's alone: Calcite has no operator by any of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not a tidiness check. A connection chains the operator table its <c>fun</c> property names
        /// <em>before</em> the catalog reader, and overload resolution takes the first candidate whose
        /// arity fits — so the day Calcite gives some library a function called <c>IS_ARRAY</c>, that
        /// operator answers and the schema's declaration stops being reached, silently and only for
        /// hosts that set <c>fun</c>. The failure would be a wrong statement rather than an error, and
        /// nothing else here would notice.
        /// </para>
        /// <para>
        /// Measured against every library Calcite ships rather than the few the suite chains elsewhere,
        /// and case-insensitively, because that is how a name matcher would find one. There are near
        /// misses and they are on purpose: Calcite has <c>IS_INF</c> and <c>IS_NAN</c> beside this
        /// family, <c>REGEXP_LIKE</c> beside <c>REGEXMATCH</c>, and <c>STRING_TO_ARRAY</c> beside
        /// <c>StringToArray</c> — the last two named apart deliberately, being different functions
        /// rather than different spellings. See <see cref="CosmosOperators"/>.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void NoneOfTheNamesIsOneCalciteAlreadyUses()
        {
            var libraries = java.util.EnumSet.allOf(java.lang.Class.forName("org.apache.calcite.sql.fun.SqlLibrary"));

            var calcite = new List<string>();

            foreach (var table in new org.apache.calcite.sql.SqlOperatorTable[]
            {
                SqlStdOperatorTable.instance(),
                org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(libraries),
            })
            {
                var list = table.getOperatorList();
                for (var i = 0; i < list.size(); i++)
                    calcite.Add(((org.apache.calcite.sql.SqlOperator)list.get(i)).getName());
            }

            calcite.Should().HaveCountGreaterThan(500, "both tables have to have actually loaded for this to mean anything");

            var ours = CosmosOperators.Instance.getOperatorList();
            for (var i = 0; i < ours.size(); i++)
            {
                var name = ((org.apache.calcite.sql.SqlOperator)ours.get(i)).getName();

                calcite.Should().NotContain(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase),
                    "'{0}' would be shadowed by Calcite's own operator wherever a connection sets fun", name);
            }
        }

    }

}
