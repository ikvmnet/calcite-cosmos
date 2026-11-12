using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What the manual and <c>TODO.md</c> say a filter beside a part the declaration rules out is planned as: sent where the
    /// service takes it whole, and, where part of it stays in Calcite, the empty relation beneath that part, which reads
    /// only the rows the part sent returns (<c>docs/14</c>, 14.2; <c>DESIGN.md</c>, <em>Not covered: the split</em>).
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
        /// A <c>LIKE</c> over <c>id</c>, which the adapter cannot show never fails, is sent with the contradicted
        /// <c>status</c> as one request that finds nothing.
        /// </summary>
        [Fact]
        public void ALikeOverTheIdBesideAContradictionIsSentWhole()
        {
            var container = Declaring(Statuses);
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND c."id" LIKE 'a%'""", container, out _);
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
            Query(FindCosmos(plan), container).Sql.Should().Contain("c.status", "the whole WHERE is sent");
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
        /// A number pins the key only where the declaration makes it an integer, required or not: through <c>RETURNING
        /// INTEGER</c>, or <c>RETURNING BIGINT</c> past an <c>INTEGER</c>'s range (<c>docs/07</c>, 7.3). <c>RETURNING BIGINT</c>
        /// over a stored 5 and <c>RETURNING DOUBLE</c> pin it too, answering where Calcite fails (<c>docs/23</c>). Undeclared,
        /// Calcite compares the number by its double, and none routes; <c>CAST(5 AS VARIANT)</c>, which Calcite holds equal
        /// to no stored integer, a <c>BIGINT</c> (<c>Calcite.VariantEqualsOnlyItsOwnBox</c>), routes nowhere, declared or not.
        /// </summary>
        [Fact]
        public void ANumberPinsTheKeyOnlyThroughADeclaredInteger()
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
        }

    }

}
