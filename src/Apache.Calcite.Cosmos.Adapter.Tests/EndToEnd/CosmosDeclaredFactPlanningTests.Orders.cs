using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What the service is asked to order, planned: each site that asks <see cref="CosmosServiceOrder"/> — the sort, the
    /// extreme, the comparison — beside what the service accepts, the indexed path and the composite index.
    /// </summary>
    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary>
        /// A container declaring the paths the order tests read: <c>s</c> a string of unknown characters, <c>a</c> one of a
        /// few below U+D800, <c>n</c> an integer, every one required; with the indexing policy given.
        /// </summary>
        static CosmosContainerMetadata Orderable(string[]? included = null, string[]? excluded = null, string[]? computed = null) =>
            new CosmosContainerMetadata("items", new[] { "/pk" }, null, included, excluded, computedProperties: computed)
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse("""
                    { "type": "object", "required": ["s", "a", "n", "x", "cp_lower", "cp_neg"],
                      "properties": { "s": { "type": "string" }, "a": { "enum": ["a", "b", "c"] }, "n": { "type": "integer" },
                                      "x": { "enum": ["p", "q"] }, "cp_lower": { "enum": ["p", "q"] }, "cp_neg": { "type": "integer" } } }
                    """)));

        static string Sent(string sql, CosmosContainerMetadata container) => Query(FindCosmos(PlanToCosmos(sql, container, out _)), container).Sql;

        /// <summary>
        /// A sort over a path the indexing policy excludes stays in process, its order proved or not: measured on the probe
        /// account, the service answers it with a 400.
        /// </summary>
        [Fact]
        public void ASortOverAnExcludedPathStaysInProcess()
        {
            var excluding = Orderable(new[] { "/*" }, new[] { "/x/?" });

            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.x')""", excluding).Should().NotContain("ORDER BY");
            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.a')""", excluding).Should().EndWith("ORDER BY c.a ASC", "a path the policy indexes is the control");
        }

        /// <summary>
        /// So does one over <c>_etag</c>, whose characters the service states and which every policy it hands back
        /// excludes; its <c>MAX</c> pushes, an aggregate scanning where an <c>ORDER BY</c> is refused.
        /// </summary>
        [Fact]
        public void TheEntityTagIsExtremedAndNotSorted()
        {
            var container = Declared(false);

            Sent("""SELECT c."id" FROM items AS c ORDER BY c."_etag" FETCH NEXT 3 ROWS ONLY""", container).Should().NotContain("ORDER BY");
            Sent("""SELECT MAX(c."_etag") FROM items AS c""", container).Should().Contain("MAX(c._etag)");
        }

        /// <summary>
        /// A computed property orders only where an included path names it: measured on the probe account, one the
        /// policy does not name is a 400, as an excluded path is.
        /// </summary>
        [Fact]
        public void AnUnindexedComputedPropertyStaysInProcess()
        {
            var computing = Orderable(new[] { "/*", "/cp_lower/?" }, new string[0], new[] { "cp_lower", "cp_neg" });

            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.cp_lower')""", computing).Should().EndWith("ORDER BY c.cp_lower ASC");
            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.cp_neg' RETURNING INTEGER)""", computing).Should().NotContain("ORDER BY");
        }

        /// <summary>
        /// <c>MAX</c> of <c>id</c> stays in process where nothing is known of its characters, and pushes where a filter
        /// pins them: the service's maximum of U+FFFF and U+1F600 is the second, Calcite's the first.
        /// </summary>
        [Fact]
        public void AnExtremeOfAnIdNeedsItsCharacters()
        {
            var container = Declared(false);

            Sent("""SELECT MAX(c."id") FROM items AS c""", container).Should().NotContain("MAX(");
            Sent("""SELECT MAX(c."id") FROM items AS c WHERE c."id" IN ('a', 'b')""", container).Should().Contain("MAX(c.id)");
        }

        /// <summary>
        /// An extreme of a declared integer pushes, which it did not while the argument's row type was what was asked:
        /// the service skips the absent path as Calcite skips the null.
        /// </summary>
        [Fact]
        public void AnExtremeOfADeclaredIntegerPushes()
        {
            Sent("""SELECT MIN(JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER)) FROM items AS c""", Orderable()).Should().Contain("MIN(c.n)");
        }

        /// <summary>
        /// A keyset seek over <c>id</c> against a parameter stays in process, and against a literal pushes: a parameter
        /// may be bound to any character, and the service compares strings by code point where Calcite compares them by
        /// UTF-16 code unit.
        /// </summary>
        [Fact]
        public void AnIdComparesWithALiteralAndNotWithAParameter()
        {
            var container = Declared(false);

            Sent("""SELECT c."id" FROM items AS c WHERE c."id" > 'a'""", container).Should().Contain("c.id > @");

            var seek = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE c."id" > ?""", container, out _);
            PlanText(seek).Should().Contain("ClrCursorFilter", "the comparison is Calcite's: " + PlanText(seek));
        }

        /// <summary>
        /// A key the filter pins orders nothing and is not written, which also keeps it out of the composite index
        /// question: <c>WHERE category = 'bikes' ORDER BY category, _ts DESC</c> is a sort on <c>_ts</c>, which needs
        /// none.
        /// </summary>
        [Fact]
        public void APinnedKeyIsNotWritten()
        {
            Sent("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.a') = 'a' ORDER BY JSON_VALUE(c."DOC", '$.a'), c."_ts" DESC""", Orderable())
                .Should().EndWith("ORDER BY c._ts DESC");
        }

        /// <summary>
        /// A key repeated is written once: the second ties wherever the first does, and Azure refuses
        /// <c>ORDER BY c.a, c.a</c> without a composite index.
        /// </summary>
        [Fact]
        public void ARepeatedKeyIsWrittenOnce()
        {
            Sent("""SELECT JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER) AS "x", JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER) AS "y" FROM items AS c ORDER BY 1, 2 DESC""", Orderable())
                .Should().EndWith("ORDER BY c.n ASC");
        }

        /// <summary>
        /// A text key whose characters nothing states stays in process however it is otherwise declared; one a domain
        /// keeps below U+D800 pushes.
        /// </summary>
        [Fact]
        public void ATextKeyOrdersByItsCharacters()
        {
            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.s')""", Orderable()).Should().NotContain("ORDER BY");
            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.a')""", Orderable()).Should().EndWith("ORDER BY c.a ASC");
        }

    }

}
