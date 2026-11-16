using System.Linq;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What the manual and <c>TODO.md</c> say a filter beside a part the declaration rules out is planned as: sent where the
    /// service takes it whole, and, where part of it stays in Calcite, the part sent pushed and sending nothing beneath that
    /// part, which reads only the rows the part sent returns (<c>docs/14</c>, 14.2; <c>DESIGN.md</c>, <em>Not covered: the
    /// split</em>); and a filter over one Calcite keeps, which is not split at all.
    /// </summary>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Statuses = """{ "type": "object", "required": ["status", "body", "name"], "properties": { "status": { "enum": ["active", "archived"] }, "body": { "type": "string" }, "name": { "type": "string" } } }""";

        /// <summary>
        /// A part that stays in Calcite beside a contradicted <c>status</c> reads what the part sent returns, which the
        /// declaration says is nothing, so no request is made and the part kept fails on nothing, where Calcite fails on a
        /// required string read as an integer. A comparison of two properties no declaration names stays in Calcite too.
        /// </summary>
        [Fact]
        public void ThePartKeptInProcessReadsOnlyWhatTheSplitSends()
        {
            var container = Declaring(Statuses);
            foreach (var kept in new[]
            {
                """JSON_VALUE(c."DOC", '$.body' RETURNING INTEGER) = 2""",
                """UPPER(JSON_VALUE(c."DOC", '$.name')) = 'X'""",
                """JSON_VALUE(c."DOC", '$.a') = JSON_VALUE(c."DOC", '$.b')""",
            })
            {
                var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND {kept}""", container, out _);
                PlanText(plan).Should().Contain(KeepsNothing, "the part sent keeps nothing: " + PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter", "and the part kept reads it: " + PlanText(plan));
            }
        }

        /// <summary>
        /// A comparison of two declared strings beside a contradicted <c>status</c> is sent with it on Azure, as one request
        /// that finds nothing; on the emulator, which compares strings by what it stores, it stays in Calcite over the part
        /// sent, which the declaration says is nothing.
        /// </summary>
        [Fact]
        public void TwoDeclaredStringsComparedBesideAContradictionAreSentOnAzure()
        {
            const string Sql = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND JSON_VALUE(c."DOC", '$.body') = JSON_VALUE(c."DOC", '$.name')""";

            var azure = Declaring(Statuses).WithServiceTarget(Adapter.Metadata.CosmosServiceTarget.Azure);
            var sent = PlanToCosmos(Sql, azure, out _);
            PlanText(sent).Should().NotContain(KeepsNothing, PlanText(sent));
            Query(FindCosmos(sent), azure).Sql.Should().Contain("c.status", "the whole WHERE is sent").And.Contain("c.body", "the comparison with it");

            var emulator = Declaring(Statuses).WithServiceTarget(Adapter.Metadata.CosmosServiceTarget.Emulator);
            var kept = PlanToCosmos(Sql, emulator, out _);
            PlanText(kept).Should().Contain(KeepsNothing, "the part sent keeps nothing: " + PlanText(kept));
            PlanText(kept).Should().Contain("ClrCursorFilter", "and the comparison is Calcite's: " + PlanText(kept));
        }

        /// <summary>
        /// A filter over a subquery's filter that stays in Calcite, where no host merges the two, is not split: the filter
        /// split off would be Calcite's, over a filter Calcite keeps or as the copy of one Calcite implements, and a weaker
        /// form in it is the service's, a Cosmos type test Calcite has no body for. The text equality on <c>n</c>, declared
        /// an <c>enum</c> of numbers or an <c>"integer"</c>, failed so with "cannot translate call IS_NUMBER" where Calcite
        /// answers; so did one on <c>z</c>, which nothing declares, on <c>main</c> too. Each is Calcite's above the cast,
        /// declared or not, and the statement beneath sends only what the cast's own filter pushes.
        /// </summary>
        [Theory]
        [InlineData("n", "1", false, false)]
        [InlineData("n", "2", true, false)]
        [InlineData("z", "1", false, false)]
        [InlineData("n", "1", false, true)]
        public void AFilterOverOneCalciteKeepsIsNotSplit(string path, string text, bool integer, bool third)
        {
            var schema = integer ? Tagged.Replace("""{ "enum": [1, 2] }""", """{ "type": "integer" }""") : Tagged;
            var cast = $"""SELECT c."id", c."DOC" FROM items AS c WHERE {JsonInteger("c", "body")} = 1""";
            var sql = $"""SELECT x."id", x."DOC" FROM ({cast}) AS x WHERE {Json("x", path)} = '{text}'""";
            if (third)
                sql = $"""SELECT y."id" FROM ({sql}) AS y WHERE {KindOf("y")} = 'C'""";

            foreach (var (declared, container) in new[] { ("declared", Declaring(schema)), ("undeclared", Declaring("""{ "type": "object" }""")) })
            {
                var plan = PlanText(PlanToCosmos(sql, container, out _, windows: true, subqueries: true));
                foreach (var line in plan.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")))
                    line.Should().NotContainAny(["IS_NUMBER(", "IS_DEFINED(", "IS_STRING("], $"{path} {declared}: Calcite holds no service form: " + plan);
                plan.Should().Contain("CAST(JSON_VALUE(", $"{path} {declared}: the cast is Calcite's: " + plan);
                foreach (var line in plan.Split('\n').Where(l => l.TrimStart().StartsWith("Cosmos")))
                    line.Should().NotContain($"'$.{path}'", $"{path} {declared}: nothing above the cast is sent: " + plan);
            }
        }

        static string LateralKind(string alias) => $"CROSS JOIN LATERAL (SELECT {KindOf(alias)} AS k2 FROM (VALUES (1)) AS v(x0)) AS l";

        static string Unnested(string alias) => $"""UNNEST(JSON_QUERY({alias}."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)""";

        /// <summary>A filter on <c>n</c> or <c>z</c> over a lateral, an <c>UNNEST</c> or a scalar sub-query beside or over a filter Calcite keeps.</summary>
        static string OverCorrelate(string name) => name switch
        {
            "sp1" => $"""SELECT x."id" FROM (SELECT c."id", c."DOC", l.k2 FROM items AS c {LateralKind("c")} WHERE {JsonInteger("c", "body")} = 1) AS x WHERE {Json("x", "n")} = '1'""",
            "sp2" => $"""SELECT x."id", x.t FROM (SELECT c."id", c."DOC", u.t FROM items AS c, {Unnested("c")} WHERE {JsonInteger("c", "body")} = 1) AS x WHERE {Json("x", "n")} = '1'""",
            "sp5" => $"""SELECT x."id" FROM (SELECT c."id", c."DOC", l.k2 FROM items AS c {LateralKind("c")} WHERE {JsonInteger("c", "body")} = 1) AS x WHERE {Json("x", "z")} = '1'""",
            "sp7" => $"""SELECT x."id" FROM (SELECT c."id", c."DOC", (SELECT {KindOf("c")} FROM (VALUES (1)) AS v(x0)) AS k2 FROM items AS c WHERE {JsonInteger("c", "body")} = 1) AS x WHERE {Json("x", "n")} = '1'""",
            "sp10" => $"""SELECT x."id" FROM (SELECT d."id", d."DOC", l.k2 FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {JsonInteger("c", "body")} = 1) AS d {LateralKind("d")}) AS x WHERE {Json("x", "n")} = '1'""",
            "sp11" => $"""SELECT x."id" FROM (SELECT d."id", d."DOC", l.k2 FROM (SELECT c."id", c."DOC" FROM items AS c WHERE UPPER({Json("c", "body")}) = '1') AS d {LateralKind("d")}) AS x WHERE {Json("x", "n")} = '1'""",
            "sp12" => $"""SELECT x."id" FROM (SELECT d."id", d."DOC", l.k2 FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {JsonInteger("c", "body")} = 1) AS d {LateralKind("d")}) AS x WHERE {Json("x", "z")} = '1'""",
            "sp13" => $"""SELECT x."id" FROM (SELECT d."id", d."DOC", u.t FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {JsonInteger("c", "body")} = 1) AS d, {Unnested("d")}) AS x WHERE {Json("x", "n")} = '1'""",
            _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
        };

        /// <summary>
        /// A filter over a lateral, an <c>UNNEST</c> or a scalar sub-query whose left side holds a filter Calcite keeps is not
        /// split: the correlate is no part of a statement, its traversal being the <c>UNNEST</c> rule's only over a left side
        /// that reaches the service, so the filter split off would be Calcite's, and Calcite's calc pass, after the correlate
        /// rule moves it into the left side, evaluated a weaker form in it, a Cosmos type test, and failed with "cannot
        /// translate call IS_NUMBER" where Calcite answers, declared or not on <c>n</c> and on <c>main</c> too on <c>z</c>,
        /// which nothing declares. Each is Calcite's above the filter it keeps, declared and not.
        /// </summary>
        [Theory]
        [InlineData("sp1", "CAST(JSON_VALUE(")]
        [InlineData("sp2", "CAST(JSON_VALUE(")]
        [InlineData("sp5", "CAST(JSON_VALUE(")]
        [InlineData("sp7", "CAST(JSON_VALUE(")]
        [InlineData("sp10", "CAST(JSON_VALUE(")]
        [InlineData("sp11", "UPPER(JSON_VALUE(")]
        [InlineData("sp12", "CAST(JSON_VALUE(")]
        [InlineData("sp13", "CAST(JSON_VALUE(")]
        public void AFilterOverACorrelateOverOneCalciteKeepsIsNotSplit(string name, string kept)
        {
            foreach (var (declared, container) in new[] { ("declared", Declaring(Tagged)), ("undeclared", Declaring("""{ "type": "object" }""")) })
            {
                var plan = PlanText(PlanToCosmos(OverCorrelate(name), container, out _, windows: true, subqueries: true));
                var calcite = plan.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")).ToList();
                foreach (var line in calcite)
                    line.Should().NotContainAny(["IS_NUMBER(", "IS_DEFINED(", "IS_STRING("], $"{name} {declared}: Calcite holds no service form: " + plan);
                calcite.Should().Contain(l => l.Contains(kept), $"{name} {declared}: the filter beneath is Calcite's: " + plan);
            }
        }

        /// <summary>
        /// A part the split keeps in process is simplified without the part it sends, as recorded (<c>TODO.md</c>, <em>A part
        /// the split keeps in process is simplified without the part it sends</em>): a subquery's <c>CAST(body AS INTEGER) =
        /// 1 AND kind IN ('A', 'B')</c> under <c>kind = 'C'</c> sends the <c>IN</c>, and Calcite evaluates the cast beside
        /// <c>kind = 'C'</c> alone, where Calcite in process reads the three together as false and evaluates none. So the
        /// statement fails on a <c>body</c> that is no number, declared or not, where Calcite answers no rows; declared, the
        /// request has no <c>WHERE</c>, the declaration making the <c>IN</c> true. When the split simplifies its residual
        /// under what it sends this changes.
        /// </summary>
        [Fact]
        public void APartKeptInProcessIsSimplifiedWithoutThePartSent()
        {
            var sql = $"""SELECT x."id" FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {JsonInteger("c", "body")} = 1 AND {KindOf("c")} IN ('A', 'B')) AS x WHERE {KindOf("x")} = 'C'""";
            foreach (var (declared, container) in new[] { ("declared", Declaring(Tagged)), ("undeclared", Declaring("""{ "type": "object" }""")) })
            {
                var plan = PlanToCosmos(sql, container, out _, windows: true, subqueries: true);
                var text = PlanText(plan);
                var kept = string.Join("\n", text.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")));
                kept.Should().Contain("CAST(JSON_VALUE(", $"{declared}: the cast is Calcite's: " + text).And.Contain("'C'", text);
                kept.Should().NotContain("'B'", $"{declared}: beside kind = 'C' alone: " + text);
                text.Should().NotContain(KeepsNothing, text);

                var query = Query(FindCosmos(plan), container).Sql;
                if (declared == "declared")
                    query.Should().NotContain("WHERE", "the declaration makes the IN true: " + text);
                else
                    query.Should().Contain("c.kind", "the IN is sent: " + text);
            }
        }

        /// <summary>
        /// The partition key's <c>VARIANT</c> ordered against a parameter, which Calcite does not compile, is sent on Azure
        /// and answered by the service; on the emulator the ordering stays in Calcite, which fails as it does, and only the
        /// key's presence is sent (<c>docs/07</c>, 7.3; <c>docs/23</c>). Beside a contradicted <c>status</c>, Azure sends
        /// both, and the emulator keeps the ordering over the part sent, which the declaration says is nothing.
        /// </summary>
        [Fact]
        public void TheKeyOrderedAgainstAParameterIsSentOnAzureAlone()
        {
            const string Alone = """SELECT c."id" FROM items AS c WHERE c."$.ref" < ?""";
            const string Beside = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND c."$.ref" < ?""";

            var azure = Declaring(Statuses).WithServiceTarget(Adapter.Metadata.CosmosServiceTarget.Azure);
            var sent = PlanToCosmos(Alone, azure, out _);
            PlanText(sent).Should().NotContain("ClrCursorFilter", PlanText(sent));
            Query(FindCosmos(sent), azure).Sql.Should().EndWith("WHERE (c.ref < @p0)", "Azure is sent the ordering");
            var both = PlanToCosmos(Beside, azure, out _);
            Query(FindCosmos(both), azure).Sql.Should().EndWith("WHERE ((c.status = @p0) AND (c.ref < @p1))", "and sent it beside the contradiction");

            var emulator = Declaring(Statuses).WithServiceTarget(Adapter.Metadata.CosmosServiceTarget.Emulator);
            var kept = PlanToCosmos(Alone, emulator, out _);
            PlanText(kept).Should().Contain("ClrCursorFilter(condition=[<($4, ?0)])", "the emulator keeps the ordering in Calcite: " + PlanText(kept));
            Query(FindCosmos(kept), emulator).Sql.Should().EndWith("WHERE IS_DEFINED(c.ref)", "and sends the key's presence");
            var split = PlanToCosmos(Beside, emulator, out _);
            PlanText(split).Should().Contain(KeepsNothing, PlanText(split));
            PlanText(split).Should().Contain("ClrCursorFilter(condition=[<($4, ?0)])", PlanText(split));
        }

        /// <summary>
        /// A <c>LIKE</c> over <c>id</c> is pushed with the contradicted <c>status</c> as one <c>WHERE</c>, and the
        /// statement sends nothing: the facts graph reads the raw <c>id</c> as its column's rows say, a string the service
        /// keeps there LIKE never fails on, so nothing in the <c>WHERE</c> raises. It was sent, one request finding nothing,
        /// while the raw column's rows were the rendering graph's alone.
        /// </summary>
        [Fact]
        public void ALikeOverTheIdBesideAContradictionSendsNothing()
        {
            var container = Declaring(Statuses);
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND c."id" LIKE 'a%'""", container, out _);
            PlanText(plan).Should().Contain("LIKE($1, 'a%')", "the whole WHERE is pushed: " + PlanText(plan));
            PlanText(plan).Should().Contain(KeepsNothing, PlanText(plan));
            Query(FindCosmos(plan), container).SendsNothing.Should().BeTrue();
        }

        /// <summary>
        /// <c>RETURNING DOUBLE</c> over a required integer is sent, though Calcite fails on every whole number a long holds,
        /// which the service returns as its digits however it was written (<c>Service.IntegralNumbersReadBackAsHeld</c>):
        /// the loader reads <c>RETURNING DOUBLE</c> and a column typed <c>DOUBLE</c> alike, the column reads every number,
        /// and the rendering graph lifts the agreement beside the raise (<c>TODO.md</c>, <em>The rendering graph lifts an
        /// agreement where a raise sits beside a truth value</em>). So it is sent where the declaration bounds the integer
        /// within a long, or lists the integers, and Calcite fails on every document too. A fraction no integer equals is sent
        /// as well, since a raise keeps the reduce shut.
        /// </summary>
        [Fact]
        public void AReturningDoubleOverARequiredIntegerIsSent()
        {
            foreach (var declared in new[] { """{ "type": "integer" }""", """{ "type": "integer", "minimum": 0, "maximum": 100 }""", """{ "enum": [1, 2, 3] }""" })
            {
                var container = Declaring($$"""{ "type": "object", "required": ["body"], "properties": { "body": {{declared}} } }""");
                foreach (var value in new[] { "2", "1.5" })
                {
                    var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.body' RETURNING DOUBLE) = {value}""", container, out _);
                    PlanText(plan).Should().NotContain(KeepsNothing, $"Calcite fails over {declared}: " + PlanText(plan));
                    Query(FindCosmos(plan), container).Sql.Should().EndWith("WHERE (c.body = @p0)", "the agreement is lifted");
                }
            }
        }

        /// <summary>
        /// <c>RETURNING BIGINT</c> over a required integer the declaration keeps within an <c>INTEGER</c>, bounded or listed, is
        /// sent too, though Calcite fails on every document: the reader builds an <c>Integer</c> of such a number, which the
        /// clause, asserting a <c>Long</c>, refuses (<c>CalciteJsonValueMeasurementTests.EachNumericOrBooleanReturningAssertsItsClassButDecimal</c>).
        /// It is <see cref="AReturningDoubleOverARequiredIntegerIsSent"/>'s lift: a column typed <c>BIGINT</c> reads those
        /// numbers, and the loader reads the two alike.
        /// </summary>
        [Fact]
        public void AReturningBigintOverIntegersWithinAnIntegerIsSent()
        {
            foreach (var declared in new[] { """{ "type": "integer", "minimum": 0, "maximum": 100 }""", """{ "enum": [1, 2, 3] }""" })
            {
                var container = Declaring($$"""{ "type": "object", "required": ["body"], "properties": { "body": {{declared}} } }""");
                foreach (var value in new[] { "2", "5" })
                {
                    var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.body' RETURNING BIGINT) = {value}""", container, out _);
                    PlanText(plan).Should().NotContain(KeepsNothing, $"Calcite fails over {declared}: " + PlanText(plan));
                    Query(FindCosmos(plan), container).Sql.Should().EndWith("WHERE (c.body = @p0)", "the agreement is lifted");
                }
            }
        }

        /// <summary>
        /// <c>RETURNING DOUBLE</c> against a parameter beside a contradicted <c>status</c> is sent: a typed comparison with a
        /// parameter raises over every scalar (<c>TODO.md</c>, <em>A typed comparison with a parameter raises over every
        /// scalar</em>), so the reduce stays shut.
        /// </summary>
        [Fact]
        public void ATypedComparisonWithAParameterBesideAContradictionIsSent()
        {
            var container = Declaring("""{ "type": "object", "required": ["status"], "properties": { "status": { "enum": ["active", "archived"] }, "n": { "type": "number" } } }""");
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND JSON_VALUE(c."DOC", '$.n' RETURNING DOUBLE) = ?""", container, out _);
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
        }

        /// <summary>
        /// The cast to text of a key declared an integer keeps no document: Calcite casts a <c>VARIANT</c>'s number to null
        /// (<c>Calcite.CastOfARawValueToText</c>), and the declaration rules out a stored string. Undeclared, the same
        /// comparison routes to the stored string <c>"30"</c> (<c>CosmosPlannerTests.ACastAgainstTextANumberRendersAsIsTakenOverAVariant</c>).
        /// </summary>
        [Fact]
        public void ACastToTextOfAKeyDeclaredANumberKeepsNothing()
        {
            var container = Declaring("""{ "type": "object", "properties": { "ref": { "type": "integer" } } }""");
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE CAST(c."$.ref" AS VARCHAR) = '30'""", container, out _);
            PlanText(plan).Should().Contain(KeepsNothing, PlanText(plan));
        }

        /// <summary>
        /// A boolean pins the key, written <c>CAST(TRUE AS VARIANT)</c> since Calcite compares a <c>VARIANT</c> with no bare
        /// boolean (<c>docs/07</c>).
        /// </summary>
        [Fact]
        public void ABooleanKeyPinsThePartition()
        {
            var container = Declaring("""{ "type": "object" }""");
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE c."$.ref" = CAST(TRUE AS VARIANT)""", container, out _);
            Query(FindCosmos(plan), container).PartitionKeyValues.Should().Equal(true);
        }

        /// <summary>
        /// A number pins the key only where the declaration leaves it one value whose double is the literal's
        /// (<c>docs/07</c>, 7.3). A key declared an integer, required or not, is pinned through <c>RETURNING INTEGER</c>, or
        /// <c>RETURNING BIGINT</c> past an <c>INTEGER</c>'s range; <c>RETURNING BIGINT</c> over a stored 5 and
        /// <c>RETURNING DOUBLE</c> pin it too, answering where Calcite fails (<c>docs/23</c>). A key an <c>enum</c> or a
        /// <c>const</c> narrows to fractions is pinned through <c>RETURNING DOUBLE</c> or a cast of the text to
        /// <c>DOUBLE</c>, sent as a double, and Calcite answers there. Undeclared, Calcite compares the number by its
        /// double, and none routes; <c>CAST(5 AS VARIANT)</c>, which Calcite holds equal to no stored integer, a
        /// <c>BIGINT</c> (<c>Calcite.VariantEqualsOnlyItsOwnBox</c>), routes nowhere, declared or not.
        /// </summary>
        [Fact]
        public void ANumberPinsTheKeyOnlyWhereTheDeclarationLeavesItOneValue()
        {
            const string Returning = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.ref' RETURNING {0}) = {1}""";
            const string Boxed = """SELECT c."id" FROM items AS c WHERE c."$.ref" = CAST(5 AS VARIANT)""";
            foreach (var declared in new[] { """{ "type": "object", "properties": { "ref": { "type": "integer" } } }""", """{ "type": "object", "required": ["ref"], "properties": { "ref": { "type": "integer" } } }""" })
            {
                var container = Declaring(declared);
                foreach (var (type, value, pinned) in new[] { ("INTEGER", "5", 5L), ("BIGINT", "3000000000", 3000000000L), ("BIGINT", "5", 5L), ("DOUBLE", "5", 5L) })
                    Query(FindCosmos(PlanToCosmos(string.Format(Returning, type, value), container, out _)), container).PartitionKeyValues
                        .Should().Equal(new object?[] { pinned }, $"RETURNING {type} = {value} over {declared}");
                Query(FindCosmos(PlanToCosmos(Boxed, container, out _)), container).PartitionKeyValues.Should().BeNull($"a boxed 5 over {declared}");
            }

            var undeclared = Declaring("""{ "type": "object" }""");
            foreach (var (type, value) in new[] { ("INTEGER", "5"), ("BIGINT", "3000000000"), ("DOUBLE", "5") })
                Query(FindCosmos(PlanToCosmos(string.Format(Returning, type, value), undeclared, out _)), undeclared).PartitionKeyValues
                    .Should().BeNull($"RETURNING {type} = {value}, undeclared");
            Query(FindCosmos(PlanToCosmos(Boxed, undeclared, out _)), undeclared).PartitionKeyValues.Should().BeNull("a boxed 5, undeclared");

            foreach (var declared in new[] { """{ "enum": [5.5] }""", """{ "enum": [5.5, 7.25] }""", """{ "const": 5.5 }""" })
            {
                var container = Declaring($$"""{ "type": "object", "properties": { "ref": {{declared}} } }""");
                foreach (var read in new[] { """JSON_VALUE(c."DOC", '$.ref' RETURNING DOUBLE)""", """CAST(JSON_VALUE(c."DOC", '$.ref') AS DOUBLE)""" })
                {
                    var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {read} = 5.5""", container, out _);
                    var query = Query(FindCosmos(plan), container);
                    query.PartitionKeyValues.Should().Equal(new object?[] { 5.5d }, $"{read} = 5.5 over {declared}");
                    query.Sql.Should().EndWith("WHERE (c.ref = @p0)", PlanText(plan));
                }
            }
        }

    }

}
