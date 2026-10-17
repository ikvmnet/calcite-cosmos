using System.Linq;

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
        /// What the reader of a statement checks as the rows arrive, or <c>null</c> where the plan proved everything.
        /// </summary>
        static CosmosOrderProgram? Program(string sql, CosmosContainerMetadata container) => Query(FindCosmos(PlanToCosmos(sql, container, out _)), container).Order;

        /// <summary>
        /// The statement as the converter renders it, its projection forced, which a statement whose rows the reader checks
        /// needs: the reader reads the values it checks from members of the rows the statement constructs.
        /// </summary>
        static string Converted(string sql, CosmosContainerMetadata container)
        {
            var cosmos = FindCosmos(PlanToCosmos(sql, container, out _));
            return Apache.Calcite.Cosmos.Adapter.Rel.Convert.CosmosConverters.GenerateQuery(cosmos, cosmos.getCluster().getRexBuilder()).Query.Sql;
        }

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
        /// <c>MAX</c> of <c>id</c> pushes where a filter pins its characters, and where nothing does pushes anchored: the
        /// service's maximum of U+FFFF and U+1F600 is the second, Calcite's the first, so an answer at or above U+D800 is
        /// read again, in order, to the first anchor. Grouped, there is no one row to check, and it stays in process.
        /// </summary>
        [Fact]
        public void AnExtremeOfAnIdIsProvedByItsCharactersOrAnchored()
        {
            var container = Declared(false);

            Sent("""SELECT MAX(c."id") FROM items AS c""", container).Should().Contain("MAX(c.id)");
            var extreme = Program("""SELECT MAX(c."id") FROM items AS c""", container)!.Extremes.Should().ContainSingle().Subject;
            extreme.Anchor.Should().Be(CosmosOrderAnchor.Text);
            extreme.Reread.Should().Be("SELECT VALUE c.id FROM items c ORDER BY c.id DESC");
            extreme.Ordered.Should().BeTrue();

            Sent("""SELECT MAX(c."id") FROM items AS c WHERE c."id" IN ('a', 'b')""", container).Should().Contain("MAX(c.id)");
            Program("""SELECT MAX(c."id") FROM items AS c WHERE c."id" IN ('a', 'b')""", container).Should().BeNull();

            Sent("""SELECT JSON_VALUE(c."DOC", '$.category'), MAX(c."id") FROM items AS c GROUP BY JSON_VALUE(c."DOC", '$.category')""", container).Should().NotContain("MAX(");
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
        /// A comparison of <c>id</c> with a literal pushes exactly, and with a parameter anchored: a parameter may be bound
        /// to any character, and the service compares strings by code point where Calcite compares them by UTF-16 code
        /// unit, so the reader sends it as it is where the value lies below U+D800 and weakened and rechecked otherwise.
        /// Only as a whole conjunct: under an <c>OR</c> a recheck of the one comparison would not be the predicate's, and
        /// it stays in process.
        /// </summary>
        [Fact]
        public void AnIdComparesWithALiteralExactlyAndWithAParameterAnchored()
        {
            var container = Declared(false);

            Sent("""SELECT c."id" FROM items AS c WHERE c."id" > 'a'""", container).Should().Contain("c.id > @");
            Program("""SELECT c."id" FROM items AS c WHERE c."id" > 'a'""", container).Should().BeNull();

            var seek = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE c."id" > ?""", container, out _);
            PlanText(seek).Should().NotContain("ClrCursorFilter", "the comparison is the service's, checked by the reader: " + PlanText(seek));

            var comparison = Query(FindCosmos(seek), container).Order!.Comparisons.Should().ContainSingle().Subject;
            comparison.Should().BeEquivalentTo(new { Operator = ">", Expression = "c.id", Exact = "(c.id > @p0)", Weakened = "(c.id >= @p1)", Guarded = "IS_STRING(c.id)", Member = "$c0" });

            var either = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE c."id" > ? OR c."id" = 'x'""", container, out _);
            PlanText(either).Should().Contain("ClrCursorFilter", "a disjunct is not rechecked: " + PlanText(either));
        }

        /// <summary>
        /// A comparison the reader may recheck is the rows the <c>WHERE</c> kept, one each: a grouping or a
        /// <c>DISTINCT</c> over it leaves none to recheck, and stays in process while the filter pushes.
        /// </summary>
        [Fact]
        public void AComparisonTheReaderRechecksIsNotGroupedAtTheService()
        {
            var container = Declared(false);

            Converted("""SELECT COUNT(*) FROM items AS c WHERE c."id" > ?""", container).Should().NotContain("COUNT(").And.Contain("c.id > @");
            Converted("""SELECT DISTINCT JSON_VALUE(c."DOC", '$.category') FROM items AS c WHERE c."id" > ?""", container).Should().NotContain("DISTINCT");
            Sent("""SELECT COUNT(*) FROM items AS c WHERE c."id" > 'a'""", container).Should().Contain("COUNT(1)", "against a literal the comparison is exact");
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
        /// A text key whose characters nothing states pushes anchored, the reader checking each value for a unit at or
        /// above U+D800 and the statement selecting the key for it; one a domain keeps below U+D800 pushes proved, and is
        /// read as it comes.
        /// </summary>
        [Fact]
        public void ATextKeyOrdersByItsCharacters()
        {
            const string ByS = """SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.s')""";

            Sent(ByS, Orderable()).Should().Be("SELECT VALUE { \"id\": c.id, \"EXPR$1\": (IS_PRIMITIVE(c.s) ? c.s : null), \"$k0\": c.s } FROM items c ORDER BY c.s ASC");
            Program(ByS, Orderable())!.Keys.Should().Equal(new CosmosOrderedKey("$k0", CosmosOrderedKeyKind.Text, false, false, CosmosOrderAnchor.Text));

            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.a')""", Orderable()).Should().EndWith("ORDER BY c.a ASC");
            Program("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.a')""", Orderable()).Should().BeNull();
        }

        /// <summary>
        /// A container declaring the characters of <c>id</c> by a <c>pattern</c>, an identifier's; of <c>s</c> and
        /// <c>u</c> every character below U+D800, and of <c>w</c> the basic plane without the surrogates; and of
        /// <c>x</c> a string of any character.
        /// </summary>
        static CosmosContainerMetadata Patterned() =>
            new CosmosContainerMetadata("items", new[] { "/pk" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse("""
                    { "type": "object", "required": ["s", "u", "w", "x"],
                      "properties": { "id": { "type": "string", "pattern": "^[A-Za-z0-9_-]+$" },
                                      "s": { "type": "string", "pattern": "^[\\u0000-\\uD7FF]*$" },
                                      "u": { "type": "string", "pattern": "^[\\u0000-\\uD7FF]*$" },
                                      "w": { "type": "string", "pattern": "^[\\u0000-\\uD7FF\\uE000-\\uFFFF]*$" },
                                      "x": { "type": "string" } } }
                    """)!));

        /// <summary>
        /// An id whose characters a <c>pattern</c> declares orders at the service, and its page with it: the declaration
        /// is the proof, made when the plan is, and nothing is checked when the rows arrive.
        /// </summary>
        [Fact]
        public void AnIdDeclaredByAPatternOrdersAtTheService()
        {
            const string Page = """SELECT c."id" FROM items AS c ORDER BY c."id" OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY""";

            Sent(Page, Patterned()).Should().EndWith("ORDER BY c.id ASC OFFSET 40 LIMIT 20");
            Program(Page, Patterned()).Should().BeNull("the declaration proves it");
            Sent(Page, Declared(false)).Should().EndWith("ORDER BY c.id ASC OFFSET 40 LIMIT 20");
            Program(Page, Declared(false))!.Name.Should().Be("page", "an id nothing declares may hold any character, and its page is checked at its edges");
            Sent("""SELECT MAX(c."id") FROM items AS c""", Patterned()).Should().Contain("MAX(c.id)");
            Program("""SELECT MAX(c."id") FROM items AS c""", Patterned()).Should().BeNull();
        }

        /// <summary>
        /// And so does its keyset seek, the paging chapter 9 recommends: the parameter may hold any character, and against
        /// strings below U+D800 any character compares alike by code point and by code unit. Where nothing declares the
        /// characters the seek still pushes, anchored: the reader checks the parameter and the page's edges.
        /// </summary>
        [Fact]
        public void AnIdDeclaredByAPatternSeeksAtTheService()
        {
            const string Seek = """SELECT c."id" FROM items AS c WHERE c."id" > ? ORDER BY c."id" FETCH NEXT 20 ROWS ONLY""";

            var seek = PlanToCosmos(Seek, Patterned(), out _);
            PlanText(seek).Should().NotContain("ClrCursorFilter").And.NotContain("EnumerableSort").And.Contain("CosmosSort", "the whole statement is the service's: " + PlanText(seek));
            Query(FindCosmos(seek), Patterned()).Sql.Should().Contain("c.id > @").And.EndWith("ORDER BY c.id ASC OFFSET 0 LIMIT 20");
            Query(FindCosmos(seek), Patterned()).Order.Should().BeNull();

            var anchored = PlanToCosmos(Seek, Declared(false), out _);
            PlanText(anchored).Should().NotContain("ClrCursorFilter").And.Contain("order=[anchored]", "the whole statement is the service's, checked as it arrives: " + PlanText(anchored));
            var program = Query(FindCosmos(anchored), Declared(false)).Order!;
            program.Keys.Should().ContainSingle().Which.Anchor.Should().Be(CosmosOrderAnchor.Text);
            program.Comparisons.Should().ContainSingle();
            program.Unpaged.Should().NotContain("OFFSET");
        }

        /// <summary>
        /// Two paths compare at the service unless one may hold a surrogate and the other a high unit: a path below U+D800
        /// compares alike against anything, and one of the basic plane against one of any character does not. A sort over
        /// the basic plane without the surrogates orders alike too, a high unit against a high unit being one comparison
        /// in both orders.
        /// </summary>
        [Fact]
        public void TwoDeclaredPathsCompareAtTheService()
        {
            Sent("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.s') < JSON_VALUE(c."DOC", '$.u')""", Patterned()).Should().Contain("c.s < c.u");
            Sent("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.s') < JSON_VALUE(c."DOC", '$.x')""", Patterned()).Should().Contain("c.s < c.x");
            Sent("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.w') < JSON_VALUE(c."DOC", '$.x')""", Patterned()).Should().NotContain("c.w < c.x");
            Sent("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.w')""", Patterned()).Should().EndWith("ORDER BY c.w ASC");
            Program("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.w')""", Patterned()).Should().BeNull();
            Program("""SELECT c."id" FROM items AS c ORDER BY JSON_VALUE(c."DOC", '$.x')""", Patterned())!.Keys.Single().Anchor.Should().Be(CosmosOrderAnchor.Text, "a string of any character is ordered anchored");
        }

    }

}
