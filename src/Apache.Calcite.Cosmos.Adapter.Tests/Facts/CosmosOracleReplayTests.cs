using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// The fact family's defects, replayed against the planner's oracle: each the decision its fix settled, asked of the
    /// general rules through the licence table, with no rule written for the case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is replayed is the decision, not the fix.</b> Each defect was a site deciding from the wrong facts, or a
    /// shape one site read and its neighbour did not. Here the filter is converted from SQL as a planner carries it, read
    /// as the rules read it (<see cref="CosmosFilterStatement"/>), its conjuncts admitted by the translator, and the
    /// question asked under the licence the deciding rule has — whatever the shape, a literal or a parameter, an equality
    /// or a membership.
    /// </para>
    /// <para>
    /// <b>Where the oracle answers only because something outside it does, the test says so</b>: #172 and the
    /// declaration-wide half of #175 are what the schema reader states, and the oracle reads what it states. A
    /// declaration whose union the reader stated as a split would need no reader case; that reader is not built.
    /// </para>
    /// </remarks>
    public sealed class CosmosOracleReplayTests
    {

        const string Canonical = "123e4567-e89b-12d3-a456-426614174000";
        const string Other = "223e4567-e89b-12d3-a456-426614174000";
        const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Ref = P("ref");
        static readonly JsonDocumentPath Label = P("label");
        static readonly JsonDocumentPath N = P("n");
        static readonly JsonDocumentPath At = P("at");

        const string RefText = """JSON_VALUE(c."DOC", '$.ref')""";
        const string RefUuid = $"""CAST({RefText} AS UUID)""";
        const string KindText = """JSON_VALUE(c."DOC", '$.kind')""";
        const string NCast = """CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER)""";
        const string NDouble = """CAST(JSON_VALUE(c."DOC", '$.n') AS DOUBLE)""";

        /// <summary>
        /// A filter over a container, read as the planner's rules read it.
        /// </summary>
        /// <param name="Statement">The filter.</param>
        /// <param name="Container">The container.</param>
        sealed record Loaded(CosmosFilterStatement Statement, CosmosContainerMetadata Container)
        {

            /// <summary>The index of the conjunct whose spelling mentions the text.</summary>
            public int Conjunct(string mentions) =>
                Enumerable.Range(0, Statement.Conjuncts.Count).First(i => Statement.Conjuncts[i].ToString()!.Contains(mentions, StringComparison.Ordinal));

            /// <summary>What the declaration states of every document: the route and residual licence.</summary>
            public JsonFactSet Declared => CosmosOracle.For(Container.Facts).Declared;

        }

        static CosmosContainerMetadata Container(string? schema) =>
            schema is null
                ? new CosmosContainerMetadata("items", new[] { "/pk" })
                : new CosmosContainerMetadata("items", new[] { "/pk" }).WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)));

        /// <summary>
        /// Converts <c>SELECT … WHERE condition</c> over a container and loads its filter.
        /// </summary>
        static Loaded Where(string? schema, string condition)
        {
            var container = Container(schema);
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container);

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("items", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create($"""SELECT c."DOC" FROM items AS c WHERE {condition}""", SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);
            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            var filter = FindFilter(logical) ?? throw new InvalidOperationException("No filter in " + RelOptUtil.toString(logical));
            CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _).Should().BeTrue();

            return new Loaded(CosmosFilterStatement.Of(filter.getCondition(), fields, readings, container, cluster.getRexBuilder()), container);
        }

        static Filter? FindFilter(RelNode node)
        {
            if (node is Filter filter)
                return filter;

            for (var i = 0; i < node.getInputs().size(); i++)
                if (FindFilter((RelNode)node.getInputs().get(i)) is Filter found)
                    return found;

            return null;
        }

        static bool Knows(JsonFactSet facts, JsonDocumentPath path, JsonClaim claim) => facts.Knows(new JsonFact(path, claim));

        static JsonClaim String => Type(Apache.Calcite.Cosmos.Facts.JsonType.String, orNull: true);

        static string Discriminated(params string[] kinds) => $$"""
        {
          "oneOf": [
            {{string.Join(",\n", kinds.Select(k => k.StartsWith('!')
                ? $$"""{ "properties": { "kind": { "const": "{{k[1..]}}" } } }"""
                : $$"""{ "properties": { "kind": { "const": "{{k}}" }, "ref": { "type": "string", "pattern": "{{UuidPattern.Replace("\\", "\\\\")}}" } } }"""))}}
          ]
        }
        """;

        // ── #99: the stored form applies to a membership as to an equality ───────────────────────────────────────

        /// <summary>
        /// #99: the form an equality is lowered by is the form a membership over the same path is lowered by. The licence
        /// chooses facts by the conjunct, never by its shape, so the two ask one question.
        /// </summary>
        [Fact]
        public void TheFormThatLowersAnEqualityLowersAMembership()
        {
            var schema = Discriminated("!A", "B");
            var equality = Where(schema, $"{KindText} = 'B' AND {RefUuid} = UUID'{Canonical}'");
            var membership = Where(schema, $"{KindText} = 'B' AND {RefUuid} IN (UUID'{Canonical}', UUID'{Other}')");

            foreach (var loaded in new[] { equality, membership })
            {
                var compared = loaded.Conjunct("$.ref");
                loaded.Statement.Siblings(compared).RepresentationOf(Ref)
                    .Should().NotBeNull("the sibling kind = 'B' proves the form, whatever the comparison's shape");
            }
        }

        // ── #105, #182, #187, #207: a parameter is a value of its type ────────────────────────────────────────────

        /// <summary>
        /// #105: <c>c."id" = ?</c> is exact because the service declares <c>id</c> a string, which the stratum it is
        /// rendered from holds; nothing about the parameter is needed.
        /// </summary>
        [Fact]
        public void AParameterComparedWithADeclaredStringIsRenderedFromTheDeclaration()
        {
            var loaded = Where(null, """c."id" = ?""");

            Knows(loaded.Statement.FactsFor(0), P("id"), String).Should().BeTrue();
            loaded.Statement.StratumOf(0).Should().Be(1, "it renders from the declaration alone");
        }

        /// <summary>
        /// #182 and #207: an instant and a UUID compared with a parameter are lowered by the form a literal is. The form
        /// is a fact about the path, and the licence hands it to either.
        /// </summary>
        [Theory]
        [InlineData("UUID'" + Canonical + "'")]
        [InlineData("CAST(? AS UUID)")]
        public void AFormIsTheSameForAParameterAsForALiteral(string operand)
        {
            var loaded = Where(Discriminated("!A", "B"), $"{KindText} = 'B' AND {RefUuid} = {operand}");

            loaded.Statement.Siblings(loaded.Conjunct("$.ref")).RepresentationOf(Ref).Should().NotBeNull();
        }

        [Theory]
        [InlineData("TIMESTAMP '2024-01-15 12:30:00'")]
        [InlineData("CAST(? AS TIMESTAMP)")]
        public void AnInstantsFormIsTheSameForAParameterAsForALiteral(string operand)
        {
            const string Instant = """{ "properties": { "at": { "type": "string", "format": "date-time", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$" } } }""";
            var loaded = Where(Instant, $"""CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP) > {operand}""");

            loaded.Statement.Siblings(0).RepresentationOf(At).Should().NotBeNull("the declaration states the form");
        }

        /// <summary>
        /// #187: a distance to a point given as a parameter is measured from a path the declaration says holds a point;
        /// the comparison itself is opaque to the loader either way, and the path's fact is the declaration's.
        /// </summary>
        [Fact]
        public void APointsShapeIsTheDeclarationsWhateverItIsComparedWith()
        {
            const string Point = """
            { "type": "object",
              "required": ["location"],
              "properties": { "location": { "type": "object", "required": ["type", "coordinates"],
                            "properties": {
                              "type": { "const": "Point" },
                              "coordinates": { "type": "array", "minItems": 2, "maxItems": 3,
                                "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                                 { "type": "number", "minimum": -90, "maximum": 90 } ] } } } } }
            """;

            var facts = Container(Point).Facts;
            foreach (var distance in new[] { Opaque(0), Opaque(1) })
                Declared(facts, distance).Facts(OracleLicence.RenderConjunct(0)).IsAlwaysGeography(P("location")).Should().BeTrue();
        }

        // ── #114: a comparison every document satisfies, deleted only on facts from outside it ────────────────────

        [Fact]
        public void AComparisonTheDeclarationGuaranteesIsDeletedAndOneItDoesNotIsNot()
        {
            const string Required = """{ "required": ["kind"], "properties": { "kind": { "const": "A" } } }""";
            const string Optional = """{ "properties": { "kind": { "const": "A" } } }""";

            Where(Required, $"{KindText} = 'A'").Statement.Redundant.Should().Equal(0);
            Where(Optional, $"{KindText} = 'A'").Statement.Redundant.Should().BeEmpty("a document without kind is not kept");
            Where(Required, $"{KindText} = 'Z'").Statement.IsContradictory.Should().BeTrue("and kind = 'Z' keeps nothing");

            // The guard a conjunct proves is never what deletes it.
            var self = Where(Discriminated("!A", "B"), $"{KindText} = 'B'");
            self.Statement.Redundant.Should().BeEmpty();
        }

        // ── #117 and TODO.md:1595-1597: the preimage, and a declared string meeting it ──────────────────────────────

        /// <summary>
        /// #117: an equality with text a number renders as is not made exact by itself. Its rendering is decided from the
        /// stratum before it, which never holds what it establishes, and what it establishes is its preimage.
        /// </summary>
        [Fact]
        public void AnEqualityWithRenderedTextNeverCertifiesItself()
        {
            var loaded = Where(null, """JSON_VALUE(c."DOC", '$.label') = '30'""");

            Knows(loaded.Statement.FactsFor(0), Label, String).Should().BeFalse();
            loaded.Statement.IsAdmitted(0).Should().BeFalse("so the translator does not render it exactly");
            loaded.Statement.Kept.TryGetConstant(Label, out _).Should().BeFalse("a stored 30 renders as '30' too");
            Knows(loaded.Statement.Kept, Label, Enum("30", 30L)).Should().BeTrue("the preimage, and nothing narrower");
        }

        /// <summary>
        /// TODO.md:1595-1597: a declared string deletes the stored-number disjunct. Over it the same equality is rendered
        /// from a stratum that knows the path a string, and what it establishes is the string alone.
        /// </summary>
        [Fact]
        public void ADeclaredStringLeavesThePreimageItsString()
        {
            var loaded = Where("""{ "properties": { "label": { "type": "string" } } }""", """JSON_VALUE(c."DOC", '$.label') = '30'""");

            Knows(loaded.Statement.FactsFor(0), Label, String).Should().BeTrue();
            loaded.Statement.StratumOf(0).Should().Be(1, "it renders exactly from the declaration");
            loaded.Statement.Kept.TryGetConstant(Label, out var only).Should().BeTrue("the row it keeps holds the string there");
            only.Should().Be((JsonScalar)"30");
        }

        // ── #172 and #175: what a union states ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// #172: a nullable identifier declared as a union with null states the identifier's form, made nullable — which
        /// the schema reader states, and the oracle reads. No predicate rule is involved.
        /// </summary>
        [Fact]
        public void ANullableUnionStatesItsFormThroughTheReader()
        {
            var schema = $$"""{ "properties": { "ref": { "anyOf": [ { "type": "null" }, { "type": "string", "pattern": "{{UuidPattern.Replace("\\", "\\\\")}}" } ] } } }""";
            var loaded = Where(schema, $"{RefUuid} = UUID'{Canonical}'");

            loaded.Declared.RepresentationOf(Ref).Should().NotBeNull();
        }

        /// <summary>
        /// #175: what every branch of a discriminated union states holds of a query over every kind. Where the query
        /// names its kinds the oracle's split proves it with no reader case at all; where it names none, it is the meet the
        /// reader states.
        /// </summary>
        [Fact]
        public void WhatEveryBranchStatesHoldsOfEveryKind()
        {
            var schema = Discriminated("B", "C");

            var named = Where(schema, $"{KindText} IN ('B', 'C') AND {RefUuid} = UUID'{Canonical}'");
            named.Statement.Siblings(named.Conjunct("$.ref")).RepresentationOf(Ref).Should().NotBeNull("the split over kinds lifts what both state");

            var unnamed = Where(schema, $"{RefUuid} = UUID'{Canonical}'");
            unnamed.Declared.RepresentationOf(Ref).Should().NotBeNull("the reader's meet");
        }

        // ── #198 and #217: a number is a number ──────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("""{ "properties": { "rate": { "enum": [0.5, 1.5] } } }""", "1.5")]
        [InlineData("""{ "properties": { "rate": { "const": 1.5 } } }""", "1.5")]
        [InlineData("""{ "properties": { "rate": { "enum": [1, 2] } } }""", "1.0")]
        [InlineData("""{ "properties": { "rate": { "type": "number" } } }""", "2")]
        [InlineData("""{ "required": ["rate"], "properties": { "rate": { "type": "number" } } }""", "2")]
        public void ANumberTheDeclarationAdmitsIsNoContradiction(string schema, string number)
        {
            var loaded = Where(schema, $"""CAST(JSON_VALUE(c."DOC", '$.rate') AS DECIMAL(10, 2)) = {number}""");

            loaded.Statement.IsContradictory.Should().BeFalse();
        }

        [Fact]
        public void ANumberTheDeclarationRefusesIsOne()
        {
            Where("""{ "properties": { "rate": { "type": "string" } } }""", """JSON_VALUE(c."DOC", '$.rate' RETURNING INTEGER) = 2""")
                .Statement.IsContradictory.Should().BeTrue("a string is never an integer RETURNING keeps");
        }

        // ── F2, F3, F4 ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// F2: a comparison with the text a stored number renders as matches the number, so it contradicts no declared
        /// integer; text no integer renders as does.
        /// </summary>
        [Fact]
        public void TextARenderedNumberMatchesIsNotAStoredString()
        {
            const string Integer = """{ "properties": { "n": { "type": "integer" } } }""";

            Where(Integer, """JSON_VALUE(c."DOC", '$.n') = '5'""").Statement.IsContradictory.Should().BeFalse();
            Where(Integer, """JSON_VALUE(c."DOC", '$.n') = 'five'""").Statement.IsContradictory.Should().BeTrue();
        }

        /// <summary>
        /// F3: two declared claims no value satisfies say the property is absent, which a document satisfies; F4: a
        /// comparison says the property it reads is there, which then contradicts them.
        /// </summary>
        [Fact]
        public void AbsenceIsNoContradictionUntilAComparisonSaysThePathIsThere()
        {
            const string Impossible = """{ "properties": { "status": { "type": "integer", "enum": ["open", "closed"] } } }""";

            var other = Where(Impossible, $"{KindText} = 'A'");
            other.Statement.IsContradictory.Should().BeFalse();
            Knows(other.Statement.Kept, Kind, Present).Should().BeTrue("F4: the comparison says kind is there");

            Where(Impossible, """JSON_VALUE(c."DOC", '$.status') = 'open'""").Statement.IsContradictory.Should().BeTrue();
        }

        // ── Gaps 1 to 5, and gap 1 spelled per kind (TODO.md:1703-1704) ────────────────────────────────────────────

        [Fact]
        public void Gap1TwoKindsNamedTogetherProveWhatBothDeclare()
        {
            var both = Where(Discriminated("order", "invoice", "!note"), $"{KindText} IN ('order', 'invoice') AND {RefUuid} = UUID'{Canonical}'");
            both.Statement.Siblings(both.Conjunct("$.ref")).RepresentationOf(Ref).Should().NotBeNull();

            var control = Where(Discriminated("order", "invoice", "!note"), $"{KindText} IN ('order', 'note') AND {RefUuid} = UUID'{Canonical}'");
            control.Statement.Siblings(control.Conjunct("$.ref")).RepresentationOf(Ref).Should().BeNull();
        }

        [Fact]
        public void Gap2ADisequalityLeavesTheOneKindThatRemains()
        {
            var order = Where(Discriminated("order", "!invoice"), $"{KindText} <> 'invoice' AND {RefUuid} = UUID'{Canonical}'");
            order.Statement.Siblings(order.Conjunct("$.ref")).RepresentationOf(Ref).Should().NotBeNull();

            var invoice = Where(Discriminated("order", "!invoice"), $"{KindText} <> 'order' AND {RefUuid} = UUID'{Canonical}'");
            invoice.Statement.Siblings(invoice.Conjunct("$.ref")).RepresentationOf(Ref).Should().BeNull();
        }

        [Fact]
        public void Gap3AUnionOfEnumsIsTheirMembers()
        {
            const string Status = """{ "type": "object", "properties": { "status": { "anyOf": [ { "enum": ["open", "closed"] }, { "enum": ["archived"] } ] } } }""";

            Where(Status, """JSON_VALUE(c."DOC", '$.status') = 'purged'""").Statement.IsContradictory.Should().BeTrue();
            Where(Status, """JSON_VALUE(c."DOC", '$.status') = 'archived'""").Statement.IsContradictory.Should().BeFalse();
        }

        [Fact]
        public void Gap4ARangeBeyondADeclaredBoundKeepsNoNumber()
        {
            const string Integer = """{ "type": "object", "properties": { "n": { "type": "integer", "maximum": 100 } } }""";
            const string Untyped = """{ "type": "object", "properties": { "n": { "maximum": 100 } } }""";

            Where(Integer, $"{NCast} > 500").Statement.IsContradictory.Should().BeTrue();
            Where(Integer, $"{NCast} > 50").Statement.IsContradictory.Should().BeFalse();
            Where(Untyped, $"{NCast} > 500").Statement.IsContradictory.Should().BeFalse("a stored \"600\" passes the cast");
        }

        [Fact]
        public void Gap5ARangeOutsideAnEnumKeepsNothing()
        {
            const string Enumerated = """{ "type": "object", "properties": { "n": { "enum": [1, 2, 3] } } }""";

            Where(Enumerated, $"{NCast} > 499 AND {NCast} < 501").Statement.IsContradictory.Should().BeTrue();
            Where(Enumerated, $"{NCast} = 30").Statement.IsContradictory.Should().BeTrue();
            Where(Enumerated, $"{NCast} = 2").Statement.IsContradictory.Should().BeFalse();
            Where(Enumerated, $"{NCast} > 1").Statement.IsContradictory.Should().BeFalse();
            Where("""{ "type": "object", "properties": { "n": { "enum": ["600", 1] } } }""", $"{NCast} > 500").Statement.IsContradictory.Should().BeFalse();
        }

        /// <summary>
        /// TODO.md:1703-1704, gap 1 spelled per kind: each arm names its own kind, and what both kinds declare holds of
        /// every row the disjunction keeps.
        /// </summary>
        [Fact]
        public void Gap1SpelledPerKindProvesWhatEachArmsKindDeclares()
        {
            var schema = Discriminated("order", "invoice", "!note");

            var both = Where(schema, $"""(({KindText} = 'order' AND JSON_VALUE(c."DOC", '$.x') = '1') OR ({KindText} = 'invoice' AND JSON_VALUE(c."DOC", '$.y') = '2')) AND {RefUuid} = UUID'{Canonical}'""");
            both.Statement.Siblings(both.Conjunct("$.ref")).RepresentationOf(Ref).Should().NotBeNull();

            var control = Where(schema, $"""(({KindText} = 'order' AND JSON_VALUE(c."DOC", '$.x') = '1') OR ({KindText} = 'note' AND JSON_VALUE(c."DOC", '$.y') = '2')) AND {RefUuid} = UUID'{Canonical}'""");
            control.Statement.Siblings(control.Conjunct("$.ref")).RepresentationOf(Ref).Should().BeNull();
        }

        // ── Tautology over IN, <>, a range and IS NOT NULL ────────────────────────────────────────────────────────

        /// <summary>
        /// The shapes the old <c>IsAlwaysTrue</c> lacked: each deleted where the declaration guarantees it, and each kept
        /// where a document the declaration admits would fail it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two rows the shadow's replay held wrong, corrected.</b> It said <c>CAST(JSON_VALUE(n) AS INTEGER) &gt; 0</c>
        /// over a declared integer of at least 1, and <c>… BETWEEN 1 AND 9</c> under a maximum of 9, were tautologies,
        /// resting on an integer-cast row the population-D checker then proved wrong: over the accessor the cast is Java's
        /// integer parse of the rendering, which raises on <c>1e30</c> and <c>3000000000</c> (both integers JSON Schema
        /// admits) and on any rendering with a point. Deleting either would answer where the query raises. Under the
        /// maximum only a <c>5.0</c>-style spelling could raise, which the emulator renders as <c>5</c>; it stays
        /// undecided, soundly.
        /// </para>
        /// <para>
        /// The same ranges through a cast to <c>DOUBLE</c>, whose parse raises on no number, are the tautologies; and
        /// <c>RETURNING DOUBLE</c> is not, raising over a stored integer.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("""{ "required": ["kind"], "properties": { "kind": { "enum": ["A", "B"] } } }""", KindText + " IN ('A', 'B')", true)]
        [InlineData("""{ "properties": { "kind": { "enum": ["A", "B"] } } }""", KindText + " IN ('A', 'B')", false)]
        [InlineData("""{ "required": ["kind"], "properties": { "kind": { "enum": ["A", "B"] } } }""", KindText + " IN ('A', 'C')", false)]
        [InlineData("""{ "required": ["kind"], "properties": { "kind": { "enum": ["A", "B"] } } }""", KindText + " <> 'Z'", true)]
        [InlineData("""{ "required": ["kind"], "properties": { "kind": { "enum": ["A", "B"] } } }""", KindText + " <> 'A'", false)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1 } } }""", NCast + " > 0", false)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1 } } }""", NCast + " > 1", false)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1 } } }""", NDouble + " > 0", true)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1 } } }""", NDouble + " > 1", false)]
        // A declared integer may be stored 30.0, which RETURNING INTEGER raises on: deleting the comparison would answer
        // where the query raises, so it is not deleted, though every value satisfies it.
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1 } } }""", """JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER) > 0""", false)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1, "maximum": 9 } } }""", NCast + " BETWEEN 1 AND 9", false)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1, "maximum": 9 } } }""", NDouble + " BETWEEN 1 AND 9", true)]
        [InlineData("""{ "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 1, "maximum": 9 } } }""", """JSON_VALUE(c."DOC", '$.n' RETURNING DOUBLE) BETWEEN 1 AND 9""", false)]
        [InlineData("""{ "required": ["label"], "properties": { "label": { "type": "string" } } }""", """JSON_VALUE(c."DOC", '$.label') IS NOT NULL""", true)]
        [InlineData("""{ "properties": { "label": { "type": "string" } } }""", """JSON_VALUE(c."DOC", '$.label') IS NOT NULL""", false)]
        [InlineData("""{ "required": ["label"], "properties": { "label": { "type": ["string", "null"] } } }""", """JSON_VALUE(c."DOC", '$.label') IS NOT NULL""", false)]
        public void ATautologyIsDecidedWhateverItsShape(string schema, string condition, bool always)
        {
            var loaded = Where(schema, condition);
            var conjuncts = loaded.Statement.Conjuncts.Count;

            if (always)
                loaded.Statement.Redundant.Should().HaveCount(conjuncts, $"the declaration guarantees {condition}: {loaded.Statement.Statement}");
            else
                loaded.Statement.Redundant.Should().HaveCountLessThan(conjuncts, $"a document the declaration admits fails {condition}: {loaded.Statement.Statement}");
        }

    }

}
