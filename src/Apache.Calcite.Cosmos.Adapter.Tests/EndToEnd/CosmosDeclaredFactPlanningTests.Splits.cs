using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;

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
        /// The part a subquery's split keeps in process is merged with the filter above it without the part sent, as
        /// recorded (<c>TODO.md</c>, <em>A part a subquery's split keeps in process is merged with a filter above it without
        /// the part it sends</em>): a subquery's <c>CAST(body AS INTEGER) = 1 AND kind IN ('A', 'B')</c> under
        /// <c>kind = 'C'</c> sends the <c>IN</c>, and Calcite evaluates the cast beside <c>kind = 'C'</c> alone, where Calcite
        /// in process reads the three together as false and evaluates none. So the statement fails on a <c>body</c> that is
        /// no number, declared or not, where Calcite answers no rows; declared, the request has no <c>WHERE</c>, the
        /// declaration making the <c>IN</c> true.
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

        /// <summary><c>kind</c> is <c>A</c> or <c>B</c> and <c>body</c> a string, both required; <c>n</c> is <c>"1"</c> or <c>"2"</c>.</summary>
        internal const string Implied = """{ "type": "object", "required": ["kind", "body"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "enum": ["1", "2"] } } }""";

        /// <summary>A <c>WHERE</c> whose cast of <c>body</c> sits where a sibling the service is sent makes it go unread.</summary>
        internal static string ImpliedBySibling(string name, string t)
        {
            var kind = Json("c", "kind");
            var cast = JsonInteger("c", "body");
            return name switch
            {
                "s1" => $"""SELECT c."id" FROM {t} AS c WHERE NOT ({cast} = 1 AND {kind} = 'C') AND {kind} = 'A'""",
                "s2" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} = 'A' AND ({kind} = 'A' OR {cast} = 1)""",
                "s3" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} = 'A' AND NOT ({kind} = 'B' AND {cast} = 1)""",
                "s4" => $"""SELECT c."id" FROM {t} AS c WHERE {Json("c", "n")} = '1' AND ({Json("c", "n")} = '1' OR {cast} = 1)""",
                "s5" => $"""SELECT COUNT(*) AS q FROM {t} AS c WHERE {kind} = 'A' AND ({kind} = 'A' OR {cast} = 1)""",
                "s6" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} = 'A' AND CASE WHEN {kind} = 'A' THEN TRUE ELSE {cast} = 1 END""",
                "s7" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} IN ('A', 'B') AND ({kind} IN ('A', 'B') OR {cast} = 1)""",
                "s8" => $"""SELECT c."id" FROM {t} AS c WHERE c."id" = '1' AND (c."id" = '1' OR {cast} = 1)""",
                "s9" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} = 'A' AND ({kind} <> 'B' OR {cast} = 1)""",
                "s10" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} = 'A' AND ({kind} = 'A' OR JSON_VALUE(c."DOC", '$.body' RETURNING INTEGER) = 1)""",
                "s11" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} = 'A' AND CASE WHEN CAST({Json("c", "body")} AS BOOLEAN) AND {kind} = 'C' THEN FALSE ELSE TRUE END""",
                "s12" => $"""SELECT c."id" FROM {t} AS c WHERE {kind} IN ('A', 'B') AND ({kind} IN ('A', 'B') OR {cast} = 1) AND {JsonInteger("c", "m")} > 0""",
                "s13" => $"""SELECT COUNT(*) AS q FROM {t} AS c WHERE {kind} IN ('A', 'B') AND ({kind} IN ('A', 'B') OR {cast} = 1) AND {JsonInteger("c", "m")} > 0""",
                _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
            };
        }

        /// <summary>
        /// <c>kind</c> is <c>A</c> or <c>B</c> and <c>body</c> a string, both required; <c>w</c> three digits, <c>code</c> a
        /// whole number unpadded, <c>flag</c> a boolean and <c>n</c> <c>"1"</c> or <c>"2"</c>.
        /// </summary>
        internal const string Lowered = """{ "type": "object", "required": ["kind", "body"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "w": { "type": "string", "pattern": "^[0-9]{3}$" }, "code": { "type": "string", "pattern": "^(0|[1-9][0-9]*)$" }, "flag": { "type": "boolean" }, "n": { "enum": ["1", "2"] } } }""";

        /// <summary>
        /// A <c>WHERE</c> whose cast of <c>body</c> Calcite evaluates, beside a conjunct sent that its lowering, its flattened
        /// <c>CASE</c> or a predicate list holding it would read as deciding the part that holds the cast.
        /// </summary>
        internal static string EvaluatedBesideASent(string name, string t)
        {
            var cast = JsonInteger("c", "body");
            var w = Json("c", "w");
            var kind = Json("c", "kind");
            var n = Json("c", "n");
            const string Flag = """JSON_VALUE(c."DOC", '$.flag' RETURNING BOOLEAN)""";
            var where = name switch
            {
                "lw1" => $"{JsonInteger("c", "w")} = 5 AND ({w} = '005' OR {cast} = 1)",
                "lw2" => $"{JsonInteger("c", "w")} = 5 AND NOT ({w} = '005' OR {cast} = 1)",
                "lw4" => $"{w} = '005' AND ({JsonInteger("c", "w")} = 5 OR {cast} = 1)",
                "lw7" => $"{JsonInteger("c", "w")} = 5 AND NOT ({cast} = 1 AND {w} = '007')",
                "l1" => $"{JsonInteger("c", "code")} = 5 AND ({cast} = 1 OR {Json("c", "code")} = '5')",
                "cf3" => $"CASE WHEN {kind} = 'A' THEN {kind} <> 'B' ELSE FALSE END AND ({kind} <> 'B' OR {cast} = 1)",
                "c1" => $"CASE WHEN {kind} = 'A' THEN {n} = '1' ELSE FALSE END AND ({n} = '1' OR {cast} = 1)",
                "x3" => $"{Flag} AND (({Flag} IS NOT NULL OR {cast} = 1) IS NOT TRUE)",
                "x4" => $"{Flag} AND ({Flag} IS NOT NULL OR {cast} = 1)",
                "x14" => $"{Json("c", "body")} LIKE 'a%' AND (({Json("c", "body")} LIKE 'a%') IS NOT NULL OR {cast} = 1)",
                "x17" => $"{Flag} AND ({cast} = 1 OR {Flag} IS NOT NULL) IS NOT TRUE",
                _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
            };

            return $"""SELECT c."id" FROM {t} AS c WHERE {where}""";
        }

        /// <summary>
        /// A part Calcite evaluates beside the part sent is evaluated: what the split keeps in Calcite is read off Calcite's
        /// simplification of the condition as written, whole, which reads neither a lowering (<c>w = '005'</c> for
        /// <c>CAST(w AS INTEGER) = 5</c>) nor a flattened <c>CASE</c>, nor a boolean sent as a predicate of its own null test.
        /// Read through them, the cast went unevaluated, a residual a lowered sibling makes false was a converted filter's
        /// <c>FALSE</c>, and x3 sent nothing, where Calcite fails on a <c>body</c> that is no number, declared or not, and on
        /// <c>main</c> as Calcite does. lw2's half sent keeps nothing, declared, as recorded (<em>A residual beside a
        /// contradicted split loses its raise</em>), and the cast is still Calcite's.
        /// </summary>
        [Theory]
        [InlineData("lw1")]
        [InlineData("lw2")]
        [InlineData("lw4")]
        [InlineData("lw7")]
        [InlineData("l1")]
        [InlineData("cf3")]
        [InlineData("c1")]
        [InlineData("x3")]
        [InlineData("x4")]
        [InlineData("x14")]
        [InlineData("x17")]
        public void APartCalciteEvaluatesBesideThePartSentIsEvaluated(string name)
        {
            foreach (var (declared, container) in new[] { ("declared", Declaring(Lowered)), ("undeclared", Declaring("""{ "type": "object" }""")) })
            {
                var text = PlanText(PlanToCosmos(EvaluatedBesideASent(name, "items"), container, out _));
                text.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")).Should().Contain(l => l.Contains("'$.body'") && l.Contains("CAST("), $"{name} {declared}: the cast is Calcite's: " + text);
                text.Should().NotContain("condition=[false]", $"{name} {declared}: " + text);
                if (name != "lw2")
                    text.Should().NotContain(KeepsNothing, $"{name} {declared}: " + text);
            }
        }

        /// <summary>
        /// Over a projection the split reads the condition through the expressions the columns stand for, as Calcite merges
        /// the two before it simplifies: over the columns Calcite's simplifier reads a bare boolean column as a predicate
        /// of the rest, and the expression it stands for it does not. So <c>x.f AND NOT (x.f IS NOT NULL OR CAST(x.b AS
        /// INTEGER) = 1)</c>, which over the columns reads as <c>FALSE</c>, keeps its cast in Calcite, which Calcite in
        /// process evaluates and fails on, declared or not, and no converted filter holds a <c>FALSE</c>.
        /// </summary>
        [Fact]
        public void OverAProjectionTheConditionIsReadThroughIt()
        {
            const string Projection = """SELECT c."id", JSON_VALUE(c."DOC", '$.kind') AS k, JSON_VALUE(c."DOC", '$.body') AS b, JSON_VALUE(c."DOC", '$.flag' RETURNING BOOLEAN) AS f FROM items AS c""";
            foreach (var (declared, container) in new[] { ("declared", Declaring(Lowered)), ("undeclared", Declaring("""{ "type": "object" }""")) })
            {
                var kept = PlanText(PlanToCosmos($"""SELECT x."id" FROM ({Projection}) AS x WHERE x.f AND NOT (x.f IS NOT NULL OR CAST(x.b AS INTEGER) = 1)""", container, out _));
                kept.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")).Should().Contain(l => l.Contains(":INTEGER"), $"{declared}: the cast is Calcite's: " + kept);
                kept.Should().NotContain("condition=[false]", $"{declared}: " + kept);
            }
        }

        /// <summary>
        /// A conjunct its siblings make go unread, as Calcite simplifies the whole condition, is not evaluated where one of
        /// them is sent: the split keeps in Calcite what that simplification leaves beside the conjuncts it sends, term by
        /// term, and nothing where it leaves none, so <c>kind = 'A' AND (kind = 'A' OR CAST(body AS INTEGER) = 1)</c> is the
        /// <c>kind = 'A'</c> sent, and s12, beside a cast of <c>m</c> the simplification keeps, keeps that cast alone.
        /// Kept as written, the cast was evaluated over the rows the half sent returns and failed on a <c>body</c> that is
        /// no number, where Calcite answers, declared or not. A <c>CASE</c> whose <c>ELSE</c> holds the cast stays, as
        /// Calcite's simplifier keeps it, and its <c>ELSE</c> is read only where <c>kind</c> is not <c>A</c>. Undeclared,
        /// s4's sibling <c>n = '1'</c> is sent only as a weaker form, <c>n</c> holding a number for all the service knows,
        /// so nothing sent says anything of the condition, which stays in Calcite as written, its <c>OR</c> reading the
        /// cast only where <c>n</c> is not <c>'1'</c>. <c>CosmosConnectionPlanningTests.ASiblingSentLeavesNothingInCalcite</c>
        /// pins the same shapes through the connection the README opens, which simplifies the whole <c>WHERE</c> before
        /// anything is pushed.
        /// </summary>
        [Theory]
        [InlineData("s1", "")]
        [InlineData("s2", "")]
        [InlineData("s3", "")]
        [InlineData("s4", "undeclared")]
        [InlineData("s5", "")]
        [InlineData("s6", "case")]
        [InlineData("s7", "")]
        [InlineData("s8", "")]
        [InlineData("s9", "")]
        [InlineData("s10", "")]
        [InlineData("s11", "")]
        [InlineData("s12", "m")]
        [InlineData("s13", "m")]
        public void ASiblingSentLeavesNothingInCalcite(string name, string kept)
        {
            foreach (var (declared, container) in new[] { ("declared", Declaring(Implied)), ("undeclared", Declaring("""{ "type": "object" }""")) })
            {
                var plan = PlanToCosmos(ImpliedBySibling(name, "items"), container, out _);
                var text = PlanText(plan);
                var calcite = text.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")).ToList();
                if (kept == "case")
                    calcite.Should().Contain(l => l.Contains("CASE(=(JSON_VALUE($0, '$.kind'), 'A'), true,"), $"{name} {declared}: the CASE is Calcite's: " + text);
                else if (kept == declared)
                    calcite.Should().Contain(l => l.Contains("OR(=(JSON_VALUE($0, '$.n'), '1'), =(CAST(JSON_VALUE($0, '$.body')):INTEGER, 1))"), $"{name} {declared}: as written: " + text);
                else
                    foreach (var line in calcite)
                        line.Should().NotContain("'$.body'", $"{name} {declared}: Calcite reads nothing of body: " + text);
                if (kept == "m")
                    calcite.Should().Contain(l => l.Contains("'$.m'"), $"{name} {declared}: the cast of m is Calcite's: " + text);
                text.Should().Contain("CosmosToClrCursorConverter", text).And.NotContain(KeepsNothing, $"{name} {declared}: " + text);
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
        /// A number pins the key only where the declaration leaves it one value the comparison may keep (<c>docs/07</c>,
        /// 7.3). A key declared an integer, required or not, is pinned through <c>RETURNING INTEGER</c>, or
        /// <c>RETURNING BIGINT</c> past an <c>INTEGER</c>'s range; <c>RETURNING BIGINT</c> over a stored 5 and
        /// <c>RETURNING DOUBLE</c> pin it too, answering where Calcite fails (<c>docs/23</c>). A key an <c>enum</c> or a
        /// <c>const</c> narrows to fractions, not required, is pinned through <c>RETURNING DOUBLE</c> or a cast of the text to
        /// <c>DOUBLE</c> equal to the fraction, sent as a double, and Calcite answers there; what the cast pins beside a
        /// number it rules out, and where the key is required, is
        /// <see cref="ACastOfTheKeyToDoublePinsAListedFractionBesideANumberAndNoValueTheKeyIsFixedTo"/>'s. Undeclared,
        /// Calcite compares the number by its double, and none routes; <c>CAST(5 AS VARIANT)</c>, which Calcite holds equal
        /// to no stored integer, a <c>BIGINT</c> (<c>Calcite.VariantEqualsOnlyItsOwnBox</c>), pins nothing of its own, and
        /// here nothing, declared or not.
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

        /// <summary>
        /// Two routings recorded as measured (<c>TODO.md</c>, <em>A number pins no partition unless the declaration
        /// leaves the key one value</em>), each as on <c>main</c>: which listed value a comparison of the key through the
        /// cast of the text to <c>DOUBLE</c> pins, beside what <c>RETURNING DOUBLE</c> sends; and, over a key the
        /// declaration fixes to one value, which comparisons are sent with no <c>WHERE</c> and pin nothing, which keep their
        /// <c>WHERE</c> and pin, which stay in Calcite over a request that pins nothing, and that one beside the rest of a
        /// <c>WHERE</c> leaves the request pinned.
        /// </summary>
        [Fact]
        public void ACastOfTheKeyToDoublePinsAListedFractionBesideANumberAndNoValueTheKeyIsFixedTo()
        {
            const string Cast = """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.ref') AS DOUBLE) = {0}""";
            const string Returning = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.ref' RETURNING DOUBLE) = {0}""";
            const string Text = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.ref') = 'bikes'""";

            CosmosQuery Planned(string sql, CosmosContainerMetadata container) => Query(FindCosmos(PlanToCosmos(sql, container, out _)), container);

            var listed = Declaring("""{ "type": "object", "required": ["ref"], "properties": { "ref": { "enum": [5.5, 7.25] } } }""");
            foreach (var number in new[] { "5", "6" })
            {
                var near = Planned(string.Format(Cast, number), listed);
                near.PartitionKeyValues.Should().Equal(new object?[] { 5.5d }, $"the cast's cell holds 5.5 beside {number}");
                near.SendsNothing.Should().BeFalse($"= {number}");
                near.Sql.Should().EndWith("WHERE (c.ref = @p0)");

                Planned(string.Format(Returning, number), listed).SendsNothing.Should().BeTrue($"RETURNING DOUBLE = {number} keeps nothing");
            }

            var integers = Declaring("""{ "type": "object", "required": ["ref"], "properties": { "ref": { "enum": [5, 7] } } }""");
            var beside = Planned(string.Format(Cast, "5.5"), integers);
            beside.PartitionKeyValues.Should().Equal(new object?[] { 5L }, "the cast's window holds 5 beside 5.5");
            beside.Sql.Should().EndWith("WHERE (c.ref = @p0)");

            var five = Declaring("""{ "type": "object", "required": ["ref"], "properties": { "ref": { "const": 5 } } }""");
            foreach (var kept in new[]
            {
                """JSON_VALUE(c."DOC", '$.ref' RETURNING INTEGER) = 5""", """JSON_VALUE(c."DOC", '$.ref' RETURNING BIGINT) = 5""",
                """JSON_VALUE(c."DOC", '$.ref' RETURNING DOUBLE) = 5""", """CAST(JSON_VALUE(c."DOC", '$.ref') AS INTEGER) = 5""",
                """JSON_VALUE(c."DOC", '$.ref') = '5'""",
            })
            {
                var query = Planned($"""SELECT c."id" FROM items AS c WHERE {kept}""", five);
                query.PartitionKeyValues.Should().Equal(new object?[] { 5L }, $"{kept} is not proved over a key fixed to 5, so it stays and pins");
                query.Sql.Should().Contain("WHERE", kept);
            }

            var decimalParse = Planned("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.ref' RETURNING DECIMAL) = 5""", five);
            decimalParse.PartitionKeyValues.Should().BeNull("RETURNING DECIMAL raises over no number, so the equality is proved over a key fixed to 5, and dropped");
            decimalParse.Sql.Should().NotContain("WHERE");

            foreach (var (fixedTo, inCalcite, literal) in new[]
            {
                ("""{ "const": 0.1 }""", """JSON_VALUE(c."DOC", '$.ref') = '0.1'""", "'0.1'"),
                ("""{ "const": 5 }""", """c."$.ref" = CAST(5 AS VARIANT)""", "VARIANT"),
            })
            {
                var container = Declaring($$"""{ "type": "object", "required": ["ref"], "properties": { "ref": {{fixedTo}} } }""");
                var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {inCalcite}""", container, out _);
                var text = PlanText(plan);
                text[..text.IndexOf("CosmosToClrCursorConverter", System.StringComparison.Ordinal)].Should().Contain(literal, $"{inCalcite} is Calcite's: " + text);
                var query = Query(FindCosmos(plan), container);
                query.PartitionKeyValues.Should().BeNull($"{inCalcite} kept in Calcite over {fixedTo} leaves the request pinning nothing");
                query.Sql.Should().NotContain("WHERE", text);
            }

            foreach (var fixedTo in new[] { """{ "const": 5.5 }""", """{ "enum": [5.5] }""" })
            {
                var container = Declaring($$"""{ "type": "object", "required": ["ref"], "properties": { "ref": {{fixedTo}} } }""");
                var implied = Planned(string.Format(Cast, "5.5"), container);
                implied.PartitionKeyValues.Should().BeNull($"the equality is implied by {fixedTo}, and dropped");
                implied.Sql.Should().NotContain("WHERE", fixedTo);

                Planned(string.Format(Returning, "5.5"), container).PartitionKeyValues.Should().Equal(new object?[] { 5.5d }, $"RETURNING DOUBLE over {fixedTo}");
            }

            var besideRest = Planned("""SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.ref') AS DOUBLE) = 5 AND JSON_VALUE(c."DOC", '$.k') = 'x'""", five);
            besideRest.PartitionKeyValues.Should().Equal(new object?[] { 5L }, "beside the rest of a WHERE the request is still pinned");
            besideRest.Sql.Should().EndWith("WHERE (c.k = @p0)");

            var required = Declaring("""{ "type": "object", "required": ["ref"], "properties": { "ref": { "const": "bikes" } } }""");
            Planned(Text, required).PartitionKeyValues.Should().BeNull("the equality is implied, and dropped");
            Planned(Text, required).Sql.Should().NotContain("WHERE");

            var optional = Declaring("""{ "type": "object", "properties": { "ref": { "const": "bikes" } } }""");
            Planned(Text, optional).PartitionKeyValues.Should().Equal("bikes");
        }

    }

}
