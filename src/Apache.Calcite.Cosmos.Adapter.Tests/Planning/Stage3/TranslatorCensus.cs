using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

using Xunit;

using Syntax = Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.CosmosSqlFormReader;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning.Stage3
{

    /// <summary>
    /// Branch-only, deleted at h16: Stage 3's census (spec §4.2 h05). Every push the translator writes today, from four
    /// sources, each classed by the row of spec §3.1 it falls in: (a) the digest inputs, planned here; (b) every test that
    /// implements a statement, captured while the suite runs with <c>COSMOS_CENSUS_CAPTURE</c> set; (c) a generated value
    /// population, counted apart; (d) the order inventory and the yardstick, captured as (b) is and told apart by test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A push</b> is a text the translator wrote into a statement the plan sends: a conjunct of a pushed filter that S did
    /// not print, a projected column that is not a bare reference, and the clauses read off the whole statement (order,
    /// grouping, rank). Each is found by walking the planned tree and, at each node, building the translator as
    /// <c>CosmosImplementor.CreateTranslator</c> builds it, over an implementor that visited the node's input; the
    /// <c>WHERE</c>'s texts are what the capture's <c>Written</c> is told as the filter's statement writes itself.
    /// </para>
    /// <para>
    /// <b>Its class</b> is read off the Rex the translator was handed, as the inventory classed the plan's conditions (a
    /// conjunct may fall in more than one row), and its spelling off the text, read by
    /// <see cref="CosmosSqlFormReader.ReadSyntax"/>. A correctness-fix class is a shape §3.1 names a decline for; the triage
    /// (h14) decides each, so a share here is of candidates.
    /// </para>
    /// </remarks>
    public class TranslatorCensus
    {

        /// <summary>The rows of spec §3.1, in its order: an identifier for the file names, the row's name and its PR.</summary>
        internal static readonly (string Id, string Name, string Pr)[] Rows =
        [
            ("BC", "B, C readers and binding", "3R (readers), 3E (binding)"),
            ("type-tests", "WHERE type tests", "3A"),
            ("H", "H CASE, truth tests", "3A (predicates), 3D (values)"),
            ("FG", "F, G predicates, casts, arithmetic", "3A"),
            ("I", "I ITEM", "3A (WHERE), 3D (values)"),
            ("J1-where", "J1 string functions in a WHERE", "3A"),
            ("C2", "C2 parameters", "3B"),
            ("rewrite", "The declared-fact rewrite", "3C"),
            ("KED", "K, E, D projections, guards, rendering legality", "3D"),
            ("J1J2-column", "J1, J2 functions in a column", "3D"),
            ("grouping", "Grouping and DISTINCT keys", "3D"),
            ("J3", "J3 geography", "3E"),
            ("J4", "J4 full text, vector, rank", "3E"),
            ("A", "A core", "h37, h42"),
            ("order", "Order (CosmosServiceOrder)", "3R fixes only"),
        ];

        /// <summary>
        /// One push in one row: where it came from, the set its container plans under, the position it was written at, its
        /// class in the row and every class it holds, the correctness-fix classes it falls in, the S piece the spec plans
        /// for it, the text (parameters inlined), the Rex, and how the text reads.
        /// </summary>
        internal sealed record Push(string Source, string Set, string Origin, string Position, string Row, string Class, string[] Classes, string[] Fixes,
            string Piece, string Text, string Rex, bool Reads, bool RoundTrips, string Form)
        {
            public string Key => Set + "\t" + Position + "\t" + Text;
        }

        // ── Shapes ──────────────────────────────────────────────────────────────────────────────────────────────────

        static readonly HashSet<string> TypeTests = new(StringComparer.OrdinalIgnoreCase) { "IS_STRING", "IS_NUMBER", "IS_BOOL", "IS_PRIMITIVE", "IS_OBJECT", "IS_ARRAY", "IS_NULL" };

        static readonly HashSet<string> Affixes = new(StringComparer.OrdinalIgnoreCase) { "STARTSWITH", "ENDSWITH", "CONTAINS" };

        static readonly HashSet<string> StringFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "UPPER", "LOWER", "CHAR_LENGTH", "CHARACTER_LENGTH", "LENGTH", "TRIM", "LTRIM", "RTRIM", "BTRIM", "POSITION", "SUBSTRING", "SUBSTR", "LEFT", "RIGHT",
            "REVERSE", "CONCAT", "||", "REPLACE", "REPEAT", "REPLICATE", "INITCAP", "INSTR", "STRPOS", "LPAD", "RPAD", "ASCII", "CHR", "INDEX_OF",
        };

        /// <summary>The string functions §3.1 declines over surrogates on the emulator.</summary>
        static readonly HashSet<string> Surrogates = new(StringComparer.OrdinalIgnoreCase) { "POSITION", "SUBSTRING", "SUBSTR", "LEFT", "RIGHT", "INSTR", "STRPOS", "INDEX_OF" };

        static readonly HashSet<string> MathFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "ABS", "CEIL", "CEILING", "FLOOR", "ROUND", "TRUNCATE", "SIGN", "POWER", "EXP", "LN", "LOG", "LOG10", "LOG2", "SQRT", "SIN", "COS", "TAN", "ASIN",
            "ACOS", "ATAN", "ATAN2", "COT", "DEGREES", "RADIANS", "PI", "RAND", "MOD", "CBRT",
        };

        /// <summary>Q3's out-of-domain functions.</summary>
        static readonly HashSet<string> Domain = new(StringComparer.OrdinalIgnoreCase) { "SQRT", "LN", "LOG", "LOG10", "LOG2", "ASIN", "ACOS" };

        /// <summary>Q4's approximate functions.</summary>
        static readonly HashSet<string> Approximate = new(StringComparer.OrdinalIgnoreCase)
        {
            "POWER", "EXP", "LN", "LOG", "LOG10", "LOG2", "SQRT", "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "ATAN2", "COT", "DEGREES", "RADIANS", "ROUND", "CBRT",
        };

        static readonly HashSet<string> ArrayFunctions = new(StringComparer.OrdinalIgnoreCase) { "ARRAY_LENGTH", "CARDINALITY", "ARRAY_CONTAINS", "ARRAY_SLICE", "MEMBER OF", "ARRAY_CONCAT", "ARRAY_INTERSECT", "ARRAY_UNION" };

        static bool IsGeography(string name) => name.StartsWith("ST_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLR_ST_", StringComparison.OrdinalIgnoreCase);

        static bool IsSearch(string name) => name.StartsWith("FULLTEXT", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLR_FT_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("VECTOR", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("REGEX", StringComparison.OrdinalIgnoreCase) || name.Equals("RRF", StringComparison.OrdinalIgnoreCase);

        static readonly HashSet<string> StoredTypes = new(StringComparer.OrdinalIgnoreCase) { "UUID", "TIMESTAMP", "DATE", "TIME", "TIMESTAMP_WITH_LOCAL_TIME_ZONE", "TIMESTAMP_TZ", "GEOMETRY" };

        static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase) { "TINYINT", "SMALLINT", "INTEGER", "BIGINT" };

        /// <summary>What a Rex holds, as far as a row of §3.1 asks.</summary>
        sealed class Shape
        {
            public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
            public bool Parameter, Correlated, Case, Truth, Like, LikeBracket, NumericCast, VariantCast, StoredCast, DecimalReading, Arithmetic, IntegerArithmetic,
                Item, JsonIndex, PathPair;
        }

        static Shape ShapeOf(RexNode node)
        {
            var shape = new Shape();
            Visit(node);
            return shape;

            void Visit(RexNode n)
            {
                switch (n)
                {
                    case RexDynamicParam:
                        shape.Parameter = true;
                        break;

                    case RexCorrelVariable:
                        shape.Correlated = true;
                        break;

                    case RexFieldAccess access:
                        Visit(access.getReferenceExpr());
                        break;

                    case RexCall call:
                        var kind = call.getKind();
                        var name = call.getOperator().getName();
                        var type = call.getType().getSqlTypeName().getName();
                        shape.Names.Add(name);

                        if (kind == SqlKind.CASE || kind == SqlKind.COALESCE)
                            shape.Case = true;

                        if (kind == SqlKind.IS_TRUE || kind == SqlKind.IS_NOT_TRUE || kind == SqlKind.IS_FALSE || kind == SqlKind.IS_NOT_FALSE
                            || (kind == SqlKind.IS_NULL || kind == SqlKind.IS_NOT_NULL) && Operand(call, 0).getType().getSqlTypeName() == SqlTypeName.BOOLEAN)
                            shape.Truth = true;

                        if (kind == SqlKind.LIKE)
                        {
                            shape.Like = true;
                            if (call.getOperands().size() > 1 && Operand(call, 1) is RexLiteral pattern && pattern.getValue2() is string p && p.IndexOfAny(['[', ']']) >= 0)
                                shape.LikeBracket = true;
                        }

                        if (kind == SqlKind.CAST)
                        {
                            if (type == "VARIANT")
                                shape.VariantCast = true;
                            else if (StoredTypes.Contains(type))
                                shape.StoredCast = true;
                            else if (SqlTypeUtil.isNumeric(call.getType()) && Operand(call, 0) is not RexLiteral)
                                shape.NumericCast = true;
                        }

                        if (name == "JSON_VALUE")
                        {
                            if (type == "DECIMAL")
                                shape.DecimalReading = true;
                            if (call.getOperands().size() > 1 && Operand(call, 1) is RexLiteral path && path.getValue2() is string spelled && spelled.Contains('['))
                                shape.JsonIndex = true;
                        }

                        if (kind == SqlKind.ITEM)
                            shape.Item = true;

                        if ((kind == SqlKind.PLUS || kind == SqlKind.MINUS || kind == SqlKind.TIMES || kind == SqlKind.DIVIDE || kind == SqlKind.MOD || kind == SqlKind.MINUS_PREFIX)
                            && SqlTypeUtil.isNumeric(call.getType()))
                        {
                            shape.Arithmetic = true;
                            if (IntegerTypes.Contains(type))
                                shape.IntegerArithmetic = true;
                        }

                        if ((kind == SqlKind.EQUALS || kind == SqlKind.NOT_EQUALS || kind == SqlKind.LESS_THAN || kind == SqlKind.GREATER_THAN
                            || kind == SqlKind.LESS_THAN_OR_EQUAL || kind == SqlKind.GREATER_THAN_OR_EQUAL) && call.getOperands().size() == 2
                            && ReadsInput(Operand(call, 0)) && ReadsInput(Operand(call, 1)))
                            shape.PathPair = true;

                        for (var i = 0; i < call.getOperands().size(); i++)
                            Visit(Operand(call, i));

                        break;
                }
            }
        }

        static RexNode Operand(RexCall call, int i) => (RexNode)call.getOperands().get(i);

        static bool ReadsInput(RexNode node) => node switch
        {
            RexInputRef or RexFieldAccess or RexCorrelVariable => true,
            RexCall call => Enumerable.Range(0, call.getOperands().size()).Any(i => ReadsInput(Operand(call, i))),
            _ => false,
        };

        /// <summary>The classes a translator conjunct holds, each with its row, the S piece the spec plans and the fixes it may be.</summary>
        static List<(string Row, string Class, string Piece, string[] Fixes)> WhereClasses(Shape s, Syntax.SqlSyntax written, string set)
        {
            var classes = new List<(string, string, string, string[])>();
            var names = s.Names;
            if (s.Parameter)
                classes.Add(("C2", "parameter", "h18 (3B)", []));
            if (names.Any(IsSearch))
                classes.Add(("J4", "full text, vector or regex", "h39-h41 (3E)", []));
            if (names.Any(IsGeography))
                classes.Add(("J3", "geography", "h39-h41 (3E)", names.Contains("ST_DISTANCE") ? ["Q4: ST_DISTANCE without bitwise rows"] : []));
            if (names.Any(TypeTests.Contains))
                classes.Add(("type-tests", "type test", "h07", []));
            if (s.Case || s.Truth)
                classes.Add(("H", "CASE, COALESCE or a truth test", "h07, h08", written.Nodes.Any(n => n is Syntax.TernarySyntax or Syntax.CoalesceSyntax) ? ["Q2: a ternary while S1 is unmeasured"] : []));
            if (s.Like || names.Any(Affixes.Contains))
                classes.Add(("FG", "LIKE", "h07 (affixes)", s.LikeBracket ? ["N12: LIKE with a bracket"] : []));
            if (s.NumericCast || s.DecimalReading)
                classes.Add(("FG", "numeric cast or RETURNING DECIMAL", "h09", []));
            if (s.Arithmetic)
                classes.Add(("FG", "arithmetic", "h10", s.IntegerArithmetic ? ["Q3: integer overflow"] : []));
            if (names.Any(MathFunctions.Contains))
                classes.Add(("FG", "math function", "h10", names.Any(Domain.Contains) ? ["Q3: out-of-domain math"] : []));
            if (s.Item || s.JsonIndex)
                classes.Add(("I", "element or ITEM", "h07", []));
            if (names.Any(StringFunctions.Contains))
                classes.Add(("J1-where", "string function", "h10", StringFixes(names, set)));
            if (names.Contains("JSON_QUERY"))
                classes.Add(("FG", "JSON_QUERY", "none named in 3A", []));
            if (s.VariantCast)
                classes.Add(("FG", "VARIANT box", "h07", []));
            if (s.StoredCast)
                classes.Add(("FG", "stored form (UUID, instant)", "h24 (3C)", []));
            if (s.PathPair)
                classes.Add(("FG", "two readings", "h11", []));
            if (s.Correlated)
                classes.Add(("FG", "unnest element", "h07 (element paths)", []));
            if (names.Any(ArrayFunctions.Contains))
                classes.Add(("FG", "array function", "none named in 3A", []));
            if (classes.Count == 0)
                classes.Add(("FG", names.Contains("IS_DEFINED") ? "presence over a text accessor" : "other", "none named in 3A", []));

            return classes;
        }

        static string[] StringFixes(IEnumerable<string> names, string set) =>
        [
            .. set == nameof(Apache.Calcite.Cosmos.Facts.Rendering.RenderingSet.Emulator) && names.Any(Surrogates.Contains) ? ["J1: POSITION, SUBSTRING, LEFT or RIGHT over surrogates on the emulator"] : Array.Empty<string>(),
            .. names.Contains("REVERSE", StringComparer.OrdinalIgnoreCase) ? ["J1: REVERSE over band S"] : Array.Empty<string>(),
        ];

        /// <summary>The classes a projected column holds.</summary>
        static List<(string Row, string Class, string Piece, string[] Fixes)> ColumnClasses(RexNode node, Shape s, Syntax.SqlSyntax written, string set)
        {
            var classes = new List<(string, string, string, string[])>();
            var names = s.Names;
            var q5 = set == nameof(Apache.Calcite.Cosmos.Facts.Rendering.RenderingSet.Emulator) && SqlTypeUtil.isNumeric(node.getType()) ? new[] { "Q5: a number column under the emulator" } : [];
            string[] With(params string[] fixes) => [.. fixes, .. q5];

            if (OfConstantsAlone(node))
                return [("KED", "literal or parameter", "h31", [])];

            if (names.Any(IsSearch))
                classes.Add(("J4", "full text or vector in a column", "h39-h41 (3E)", With()));
            if (names.Any(IsGeography))
                classes.Add(("J3", "geography in a column", "h39-h41 (3E)", With(names.Contains("ST_DISTANCE") ? ["Q4: ST_DISTANCE without bitwise rows"] : [])));
            if (s.Case || s.Truth)
                classes.Add(("H", "CASE in a column", "h31 (3D)", With(s.NumericCast || s.Arithmetic || names.Any(n => StringFunctions.Contains(n) || MathFunctions.Contains(n)) ? ["the CASE-branch refusal: an arm that may raise"] : [])));
            if (s.Item || s.JsonIndex)
                classes.Add(("I", "element in a column", "h31 (3D)", With()));

            var functions = names.Where(n => StringFunctions.Contains(n) || MathFunctions.Contains(n) || ArrayFunctions.Contains(n)).ToList();
            if (functions.Count > 0)
                classes.Add(("J1J2-column", "function", "h10, h31", With(
                [
                    .. functions.Any(Approximate.Contains) ? ["Q4: an approximate function without bitwise rows"] : Array.Empty<string>(),
                    .. functions.Any(Domain.Contains) ? ["Q3: out-of-domain math"] : Array.Empty<string>(),
                    .. StringFixes(functions, set),
                ])));
            if (s.Arithmetic)
                classes.Add(("KED", "arithmetic", "h10, h31", With(s.IntegerArithmetic ? ["Q3: integer overflow"] : [])));
            if (names.Contains("JSON_QUERY"))
                classes.Add(("KED", "JSON_QUERY", "h33", With("the JSON_QUERY writer")));
            if (s.StoredCast)
                classes.Add(("KED", "stored form (UUID, instant, geography)", "h24, h31", With()));
            if (IsGuarded(written))
                classes.Add(("KED", "guarded accessor", "h31", With()));
            if (classes.Count == 0 && node.getType().getSqlTypeName() == SqlTypeName.BOOLEAN)
                classes.Add(("KED", "predicate in a column", "h31", With()));
            if (classes.Count == 0)
                classes.Add(Strip(written) is Syntax.PathSyntax ? ("bare", "plain path read", "not asked of S", []) : ("KED", "other", "h31", With()));

            return classes;
        }

        static bool OfConstantsAlone(RexNode node) => node switch
        {
            RexLiteral or RexDynamicParam => true,
            RexCall call => Enumerable.Range(0, call.getOperands().size()).All(i => OfConstantsAlone(Operand(call, i))),
            _ => false,
        };

        static Syntax.SqlSyntax Strip(Syntax.SqlSyntax syntax) => syntax is Syntax.GroupSyntax group ? Strip(group.Inner) : syntax;

        /// <summary>A reading guarded by a test: <c>IS_T(p) ? p : null</c>, however parenthesised.</summary>
        static bool IsGuarded(Syntax.SqlSyntax written) =>
            Strip(written) is Syntax.TernarySyntax ternary && ternary.Else is Syntax.LiteralSyntax { Text: "null" or "undefined" }
                && Strip(ternary.Condition).Nodes.Any(n => n is Syntax.CallSyntax call && call.Name.StartsWith("IS_", StringComparison.Ordinal));

        // ── The walk ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Set while the census walks, so that what it implements is not itself recorded.</summary>
        [ThreadStatic]
        static bool _walking;

        static string SetOf(CosmosContainerMetadata container) => CosmosRendering.SetFor(container.ServiceTarget).ToString();

        static Push Make(string source, string set, string origin, string position, (string Row, string Class, string Piece, string[] Fixes) cls,
            IEnumerable<string> classes, string text, string written, RexNode? rex, Func<string, string>? form = null)
        {
            var syntax = Syntax.ReadSyntax(written);
            return new(source, set, origin, position, cls.Row, cls.Class, [.. classes.Distinct()], cls.Fixes, cls.Piece, text, rex?.ToString() ?? "",
                syntax.Opaque == false, Syntax.Squeezed(syntax.Written) == Syntax.Squeezed(written), form?.Invoke(written) ?? "");
        }

        /// <summary>Every push in a Cosmos subtree: its filters' and projections' and its statement's.</summary>
        internal static List<Push> Walk(RelNode root, CosmosContainerMetadata container, string source, string origin, IReadOnlySet<string> lowered)
        {
            var was = _walking;
            _walking = true;
            try
            {
                var pushes = new List<Push>();
                var set = SetOf(container);
                var rexBuilder = root.getCluster().getRexBuilder();
                foreach (var node in PlanDigests.Subtrees(root, n => n is CosmosRel, descend: true))
                {
                    try
                    {
                        if (node is CosmosFilter filter)
                            pushes.AddRange(Filter(filter, container, rexBuilder, source, set, origin, lowered));
                        else if (node is CosmosProject project)
                            pushes.AddRange(Project(project, container, rexBuilder, source, set, origin));
                    }
                    catch (Exception e)
                    {
                        pushes.Add(Failed(source, set, origin, node.getRelTypeName(), e));
                    }
                }

                try
                {
                    pushes.AddRange(Statement(CosmosConverters.GenerateQuery(root, rexBuilder).Query, source, set, origin));
                }
                catch (Exception e)
                {
                    pushes.Add(Failed(source, set, origin, "statement", e));
                }

                return pushes;
            }
            finally
            {
                _walking = was;
            }
        }

        static Push Failed(string source, string set, string origin, string where, Exception e) =>
            new(source, set, origin, where, "failure", e.GetType().Name, [], [], "", PlanDigests.Failure(e), "", false, false, "");

        static IEnumerable<Push> Filter(CosmosFilter filter, CosmosContainerMetadata container, RexBuilder rexBuilder, string source, string set, string origin, IReadOnlySet<string> lowered)
        {
            var implementor = new CosmosImplementor(rexBuilder, container);
            implementor.Visit(filter.getInput());
            var statement = CosmosFilterStatement.Of(filter.getCondition(), implementor.Fields, implementor.Readings, container, rexBuilder, implementor.RootAlias);

            var catcher = new Catcher(statement);
            var parameters = new CosmosParameterList();
            using (CosmosRenderingCapture.Arm(catcher))
                statement.Translate(parameters);

            GC.KeepAlive(statement);
            foreach (var (i, written) in catcher.Written)
            {
                var conjunct = statement.Conjuncts[i];
                var text = CosmosSqlFormReader.Inline(written, [.. parameters.Parameters]);
                string Form(string read) => CosmosSqlFormReader.Read(read, [.. parameters.Parameters]).Form.Kind.ToString();

                if (lowered.Contains(conjunct.ToString()))
                    yield return Make(source, set, origin, "where", ("rewrite", "lowered", "h24, h25 (3C)", []), ["lowered"], text, written, conjunct, Form);

                if (statement.Form(i) is not null)
                    continue;

                var shape = ShapeOf(conjunct);
                var classes = WhereClasses(shape, Syntax.ReadSyntax(written), set);
                foreach (var cls in classes)
                    yield return Make(source, set, origin, "where", cls, classes.Select(c => c.Class), text, written, conjunct, Form);
            }
        }

        static IEnumerable<Push> Project(CosmosProject project, CosmosContainerMetadata container, RexBuilder rexBuilder, string source, string set, string origin)
        {
            var implementor = new CosmosImplementor(rexBuilder, container);
            implementor.Visit(project.getInput());
            var translator = implementor.CreateTranslator(project.Facts);
            var projects = project.getProjects();
            var pushes = new List<Push>();
            for (var i = 0; i < projects.size(); i++)
            {
                var node = (RexNode)projects.get(i);
                if (node is RexInputRef)
                    continue;

                string written;
                try
                {
                    written = translator.TranslateProjection(node, out _);
                }
                catch (CosmosTranslationException e)
                {
                    pushes.Add(Failed(source, set, origin, "column", e));
                    continue;
                }

                var text = CosmosSqlFormReader.Inline(written, [.. implementor.Parameters.Parameters]);
                var classes = ColumnClasses(node, ShapeOf(node), Syntax.ReadSyntax(written), set);
                pushes.AddRange(classes.Select(cls => Make(source, set, origin, "column", cls, classes.Select(c => c.Class), text, written, node)));
            }

            return pushes;
        }

        static readonly Regex Extreme = new(@"^SELECT (VALUE )?(MIN|MAX)\(", RegexOptions.CultureInvariant);

        /// <summary>The rows read off a whole statement: every site, its order, grouping and rank.</summary>
        static IEnumerable<Push> Statement(CosmosQuery query, string source, string set, string origin)
        {
            var sql = CosmosSqlFormReader.Inline(query.Sql, query.Parameters);
            Push Row(string row, string cls, string piece) => new(source, set, origin, "statement", row, cls, [cls], [], piece, sql, "", true, true, "");

            yield return Row("A", "statement", "h37, h42");
            yield return Row("BC", sql.Contains(" JOIN ", StringComparison.Ordinal) ? "statement with a join" : "statement", "moved (h01, h40)");

            if (sql.Contains(" ORDER BY RANK ", StringComparison.Ordinal))
                yield return Row("J4", "rank clause", "h40 (3E)");
            else if (sql.Contains(" ORDER BY ", StringComparison.Ordinal))
                yield return Row("order", sql.Contains(" OFFSET ", StringComparison.Ordinal) || sql.StartsWith("SELECT TOP ", StringComparison.Ordinal) ? "page" : "sort", "none (Q8 A)");

            if (Extreme.IsMatch(sql) && sql.Contains(" GROUP BY ", StringComparison.Ordinal) == false)
                yield return Row("order", "extreme", "none (Q8 A)");

            if (sql.Contains(" GROUP BY ", StringComparison.Ordinal))
                yield return Row("grouping", "GROUP BY", "h32 (Q9 b)");
            else if (sql.Contains("DISTINCT", StringComparison.Ordinal))
                yield return Row("grouping", "DISTINCT", "h32 (Q9 b)");
        }

        /// <summary>The texts one statement writes, by conjunct, as the capture's <c>Written</c> is told them.</summary>
        sealed class Catcher(CosmosFilterStatement statement) : ICosmosRenderingObserver
        {

            public List<(int Conjunct, string Text)> Written { get; } = [];

            public void Admitted(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters) { }

            void ICosmosRenderingObserver.Written(CosmosFilterStatement written, int conjunct, string text, CosmosParameterList parameters)
            {
                if (ReferenceEquals(written, statement))
                    Written.Add((conjunct, text));
            }

            public void Weakened(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters) { }

            public void Read(CosmosFilterStatement statement, Apache.Calcite.Cosmos.Facts.AdmissionRound round) { }

        }

        // ── Source (b) and (d): the suite, captured ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Where <c>COSMOS_CENSUS_CAPTURE</c> names a file, arms the census for every flow in the process: each subtree
        /// implemented is walked, and each push appended to the file as one JSON line, with the test it ran in.
        /// </summary>
        [ModuleInitializer]
        internal static void ArmForTheSuite()
        {
            if (Environment.GetEnvironmentVariable("COSMOS_CENSUS_CAPTURE") is { Length: > 0 } path)
            {
                CosmosRenderingCapture.Census = new Recorder(new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true });
                CosmosRenderingCapture.Armed = true;
            }
        }

        /// <summary>
        /// The census's observer: the conditions the declared-fact rewrite lowered, by test; and, where it has a file, each
        /// subtree's pushes as it is implemented, and each text a translator test asks of the translator alone.
        /// </summary>
        internal sealed class Recorder(TextWriter? sink) : ICosmosRenderingObserver
        {

            readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _lowered = new();

            /// <summary>The digests of the conjuncts the rewrite lowered in a test, or in this recorder's flow.</summary>
            public IReadOnlySet<string> Lowered(string test) => _lowered.TryGetValue(test, out var lowered) ? lowered.Keys.ToHashSet() : new HashSet<string>();

            internal static string Test => TestContext.Current.Test?.TestDisplayName ?? "(no test)";

            static string TestClass => TestContext.Current.TestClass?.TestClassName ?? "";

            public void Admitted(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters) { }

            public void Written(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters) { }

            public void Weakened(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters) { }

            public void Read(CosmosFilterStatement statement, Apache.Calcite.Cosmos.Facts.AdmissionRound round) { }

            public void Rewritten(RexNode condition, RexNode rewritten)
            {
                if (_walking)
                    return;

                var original = CosmosFilterStatement.ConjunctsOf(condition).Select(c => c.ToString()).ToHashSet();
                var lowered = _lowered.GetOrAdd(sink is null ? "" : Test, _ => new());
                foreach (var conjunct in CosmosFilterStatement.ConjunctsOf(rewritten))
                    if (original.Contains(conjunct.ToString()) == false)
                        lowered[conjunct.ToString()] = true;
            }

            public void Planned(RelNode root, CosmosContainerMetadata container)
            {
                if (sink is null || _walking)
                    return;

                var test = Test;
                List<Push> pushes;
                try
                {
                    pushes = Walk(root, container, "suite", test, Lowered(test));
                }
                catch (Exception e)
                {
                    pushes = [Failed("suite", SetOf(container), test, "walk", e)];
                }

                Write(pushes);
            }

            public void Translated(string position, RexNode node, string text)
            {
                // A translator test asks the translator alone, with no plan to walk; everywhere else the walk finds the push.
                if (sink is null || _walking || TestClass.StartsWith("Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosRexTranslatorTests", StringComparison.Ordinal) == false)
                    return;

                var set = nameof(Apache.Calcite.Cosmos.Facts.Rendering.RenderingSet.Production);
                var syntax = Syntax.ReadSyntax(text);
                var where = position is "conjunct" or "predicate";
                var classes = where ? WhereClasses(ShapeOf(node), syntax, set) : ColumnClasses(node, ShapeOf(node), syntax, set);
                Write([.. classes.Select(cls => Make("suite", set, Test, where ? "where" : "column", cls, classes.Select(c => c.Class), text, text, node))]);
            }

            void Write(IEnumerable<Push> pushes)
            {
                var lines = pushes.Select(p => JsonSerializer.Serialize(p)).ToList();
                lock (sink!)
                    foreach (var line in lines)
                        sink.WriteLine(line);
            }

        }

        // ── Source (c): the generated population ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The generated population, counted apart (§3.1): <paramref name="count"/> value expressions over the partitions'
        /// two containers, from their readings plus <c>CASE</c>, <c>COALESCE</c>, an element, calls and arithmetic, each
        /// projected and each again under a <c>CASE</c> condition in a <c>WHERE</c>.
        /// </summary>
        internal static IReadOnlyList<string> Generated(int count)
        {
            var random = new Random(20_261_102);
            var statements = new List<string>(count * 2);
            for (var i = 0; i < count; i++)
            {
                var v = i % 2 == 0 ? Typed : Products;
                var (sql, number) = Value(v, random, 0);
                statements.Add($"SELECT {sql} AS \"v\" FROM {v.Table} AS c");
                var condition = number ? $"{sql} > {Pick(random, v.Numbers)}" : $"{sql} = {Quote(Pick(random, v.Literals))}";
                statements.Add($"SELECT c.\"id\" FROM {v.Table} AS c WHERE CASE WHEN {condition} THEN TRUE ELSE FALSE END");
            }

            return statements;
        }

        sealed record Vocabulary(string Table, string[] Text, string[] Number, string[] Literals, string[] Numbers);

        static readonly Vocabulary Typed = new("typed",
            ["JSON_VALUE(c.\"DOC\", '$.label')", "JSON_VALUE(c.\"DOC\", '$.name')", "JSON_VALUE(c.\"DOC\", '$.category')", "CAST(c.\"$.category\" AS VARCHAR)", "c.\"id\""],
            ["CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE)"],
            ["bikes", "30", "a", "true"], ["0E0", "9E0", "30E0"]);

        static readonly Vocabulary Products = new("products",
            ["JSON_VALUE(c.\"DOC\", '$.name')", "JSON_VALUE(c.\"DOC\", '$.category')", "JSON_VALUE(c.\"DOC\", '$.metadata.sku')", "JSON_VALUE(c.\"DOC\", '$.tags[0]')", "c.\"id\""],
            ["JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER)", "CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE)"],
            ["Sprint", "bikes", "B-2", "outdoor"], ["5", "50", "120"]);

        static T Pick<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

        static (string Sql, bool Number) Value(Vocabulary v, Random random, int depth)
        {
            string Text() => Pick(random, v.Text);
            string Number() => Pick(random, v.Number);
            string Condition() => random.Next(2) == 0 ? $"{Text()} = {Quote(Pick(random, v.Literals))}" : $"{Number()} > {Pick(random, v.Numbers)}";

            return random.Next(depth > 0 ? 6 : 9) switch
            {
                0 => (Text(), false),
                1 => (Number(), true),
                2 => ($"COALESCE({Text()}, 'none')", false),
                3 => (v == Products ? Pick(random, new[] { "JSON_VALUE(c.\"DOC\", '$.tags[1]')", "JSON_VALUE(c.\"DOC\", '$.tags[0]')" }) : "JSON_VALUE(c.\"DOC\", '$.label')", false),
                4 => StringCall(),
                5 => (Pick(random, new[]
                {
                    $"ABS({Number()})", $"FLOOR({Number()})", $"CEIL({Number()})", $"ROUND({Number()})", $"POWER({Number()}, 2)", $"SQRT({Number()})", $"LN({Number()})",
                    $"MOD({Pick(random, v.Number.Where(n => n.Contains("RETURNING", StringComparison.Ordinal)).DefaultIfEmpty("CAST(JSON_VALUE(c.\"DOC\", '$.price') AS INTEGER)").ToArray())}, 3)",
                    $"SIGN({Number()})",
                }), true),
                6 => Arithmetic(),
                7 => Value(v, random, depth + 1) is var (then, number) && Value(v, random, depth + 1) is var (otherwise, other) && number == other
                    ? ($"CASE WHEN {Condition()} THEN {then} ELSE {otherwise} END", number)
                    : ($"CASE WHEN {Condition()} THEN {Text()} ELSE 'none' END", false),
                _ => ($"CASE WHEN {Condition()} THEN {Number()} ELSE {Arithmetic().Sql} END", true),
            };

            (string Sql, bool Number) StringCall()
            {
                var call = Pick(random, new[]
                {
                    $"UPPER({Text()})", $"LOWER({Text()})", $"CHAR_LENGTH({Text()})", $"TRIM({Text()})", $"SUBSTRING({Text()} FROM 2 FOR 3)", $"POSITION('a' IN {Text()})",
                    $"REVERSE({Text()})", $"LEFT({Text()}, 2)", $"RIGHT({Text()}, 2)", $"({Text()} || '!')", $"REPLACE({Text()}, 'a', 'b')", $"CONCAT({Text()}, '!')",
                });
                return (call, call.StartsWith("CHAR_LENGTH", StringComparison.Ordinal) || call.StartsWith("POSITION", StringComparison.Ordinal));
            }

            (string Sql, bool Number) Arithmetic()
            {
                var number = Number();
                var by = number.Contains("DOUBLE", StringComparison.Ordinal) ? random.Next(1, 4) + "E0" : random.Next(1, 4).ToString(CultureInfo.InvariantCulture);
                return ($"({number} {Pick(random, new[] { "+", "-", "*", "/" })} {by})", true);
            }
        }

        // ── The census ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A statement planned and walked: its pushes in each Cosmos subtree, the rewrite's lowerings read as it planned.</summary>
        internal static List<Push> Census(string source, string origin, Func<RelNode> plan)
        {
            var recorder = new Recorder(null);
            RelNode planned;
            try
            {
                using (CosmosRenderingCapture.Arm(recorder))
                    planned = plan();
            }
            catch (Exception e)
            {
                return [new(source, "", origin, "plan", "failure", e.GetType().Name, [], [], "", PlanDigests.Failure(e), "", false, false, "")];
            }

            var pushes = new List<Push>();
            foreach (var cosmos in PlanDigests.Subtrees(planned, n => n is CosmosRel))
                if (PlannerHarness.TableOf(cosmos) is CosmosTable table)
                    pushes.AddRange(Walk(cosmos, table.Container, source, origin, recorder.Lowered("")));

            return pushes;
        }

        /// <summary>
        /// Writes the census: sources (a) and (c) planned here under Production and Emulator, at the gate's 1,000 partitions
        /// where <c>COSMOS_PARTITION_GATE=1</c>; sources (b) and (d) read from the capture files <c>COSMOS_CENSUS_SUITE</c>
        /// names (';' between them); <c>census.md</c> and a <c>coverage-&lt;row&gt;.tsv</c> each row into
        /// <c>COSMOS_CENSUS_OUT</c>.
        /// </summary>
        [Fact(Explicit = true)]
        public void WriteCensus()
        {
            var output = Path.GetFullPath(Environment.GetEnvironmentVariable("COSMOS_CENSUS_OUT") ?? Path.Combine("TestResults", "census"));
            Directory.CreateDirectory(output);
            var pushes = new List<Push>();
            var timings = new List<string>();

            using (var load = new CpuLoad())
            {
                foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
                {
                    var watch = Stopwatch.StartNew();
                    var harness = new HarnessReasonings();
                    var reasoning = harness.Make();
                    var (inputs, generated) = (0, 0);
                    using (harness.Planning())
                    {
                        foreach (var statement in RenderingInputs.All(CosmosDifferentialTests.PartitionedPredicates, reasoning, target))
                        {
                            inputs++;
                            pushes.AddRange(Census("a", statement.Input + "/" + statement.Name, statement.Plan));
                        }

                        var a = watch.Elapsed;
                        var containers = CosmosDifferentialTests.Containers.Select(c => c.WithReasoning(reasoning).WithServiceTarget(target)).ToArray();
                        foreach (var statement in RenderingInputs.Differential("generated", Generated(CosmosDifferentialTests.PartitionedPredicates), containers))
                        {
                            generated++;
                            pushes.AddRange(Census("c", "generated/" + statement.Name, statement.Plan));
                        }

                        timings.Add($"{target}: (a) {inputs:N0} statements in {a.TotalSeconds:F0} s; (c) {generated:N0} statements in {(watch.Elapsed - a).TotalSeconds:F0} s");
                    }
                }

                timings.Add(load.ToString());
            }

            var suites = (Environment.GetEnvironmentVariable("COSMOS_CENSUS_SUITE") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var suite in suites)
                foreach (var line in File.ReadLines(suite))
                    if (JsonSerializer.Deserialize<Push>(line) is { } push)
                        pushes.Add(push with { Source = IsOrder(push.Origin) ? "d" : "b" });

            File.WriteAllText(Path.Combine(output, "census.md"), Report(pushes, timings, suites));
            foreach (var (id, _, _) in Rows.Append(("bare", "", "")).Append(("failure", "", "")))
                File.WriteAllText(Path.Combine(output, $"coverage-{id}.tsv"), Coverage(pushes.Where(p => p.Row == id)));

            TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", timings));
            pushes.Should().Contain(p => p.Source == "a" && p.Row == "type-tests", "the digests' weakenings hand the translator type tests");
        }

        /// <summary>Whether a test is the order inventory's or the yardstick's (source d).</summary>
        internal static bool IsOrder(string test) =>
            test.Contains("CosmosSortInventoryTests", StringComparison.Ordinal)
            || test.Contains("CosmosDifferentialTests", StringComparison.Ordinal) && test.Contains("CalciteAllows", StringComparison.Ordinal);

        static string Coverage(IEnumerable<Push> pushes)
        {
            var text = new StringBuilder("set\tposition\ttext\tsources\toccurrences\tfirst origin\tclass\tclasses\tfixes\tpiece\treads\tround trips\tform\trex\n");
            foreach (var group in pushes.GroupBy(p => p.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                text.Append(string.Join("\t", first.Set, first.Position, PlanDigest.Escape(first.Text), string.Join(",", group.Select(p => p.Source).Distinct().Order()),
                    group.Count(), PlanDigest.Escape(first.Origin), first.Class, string.Join("; ", first.Classes), string.Join("; ", group.SelectMany(p => p.Fixes).Distinct()),
                    first.Piece, first.Reads, first.RoundTrips, first.Form, PlanDigest.Escape(first.Rex))).Append('\n');
            }

            return text.ToString();
        }

        static readonly string[] Real = ["a", "b", "d"];

        static string Report(List<Push> pushes, List<string> timings, string[] suites)
        {
            var text = new StringBuilder();
            const string P = "Production", E = "Emulator";
            int Distinct(IEnumerable<Push> of, string? set = null) => of.Where(p => set is null || p.Set == set).Select(p => p.Key).Distinct().Count();
            string Share(int part, int whole) => whole == 0 ? "—" : $"{part:N0} ({100.0 * part / whole:F0} %)";

            text.Append("# Stage 3 census (h05)\n\n");
            text.Append("Branch-only (`Tests/Planning/Stage3/TranslatorCensus.cs`, deleted at h16). Every push the translator writes today, by row of the spec's §3.1.\n\n");
            text.Append("## Inputs\n\n");
            foreach (var source in new[] { "a", "b", "c", "d" })
            {
                var of = pushes.Where(p => p.Source == source).ToList();
                text.Append($"- ({source}): {of.Select(p => p.Origin).Distinct().Count():N0} origins, {of.Count(p => p.Row is not "failure"):N0} pushes, {Distinct(of.Where(p => p.Row is not "failure")):N0} distinct; {of.Count(p => p.Row == "failure"):N0} failures.\n");
            }

            text.Append($"- Planned here: {string.Join("; ", timings)}.\n");
            text.Append($"- Suite captures read: {(suites.Length == 0 ? "none" : string.Join(", ", suites.Select(Path.GetFileName)))}.\n\n");

            text.Append("## Per row\n\n");
            text.Append("Distinct pushes: one text at one position under one set. Real is sources (a), (b) and (d); generated is (c). A fix is a shape §3.1 names a correctness-fix decline for, a candidate until the triage. Reads: the grown reader reads it whole and prints it back.\n\n");
            text.Append("| Row | PR | Real distinct (Production / Emulator) | (a) / (b) / (d) | (a) statements, Production | Generated distinct (new beyond real) | Real in a fix class (Production / Emulator) | Reads, real | Q14 |\n|---|---|---|---|---|---|---|---|---|\n");
            foreach (var (id, name, pr) in Rows)
            {
                var row = pushes.Where(p => p.Row == id).ToList();
                var real = row.Where(p => Real.Contains(p.Source)).ToList();
                var generated = row.Where(p => p.Source == "c").ToList();
                var realKeys = real.Select(p => p.Key).ToHashSet();
                var (rp, re) = (Distinct(real, P), Distinct(real, E));
                var fixP = Distinct(real.Where(p => p.Fixes.Length > 0), P);
                var fixE = Distinct(real.Where(p => p.Fixes.Length > 0), E);
                var reads = real.Where(p => p.Reads && p.RoundTrips).Select(p => p.Key).Distinct().Count();
                var statements = real.Where(p => p.Source == "a" && p.Set == P).Select(p => p.Origin).Distinct().Count();
                var floor = Math.Max(rp, re) >= 30 ? "judged on real" : $"**under 30**: judged on all {Math.Max(rp, re)} real; owner told";
                text.Append($"| {name} | {pr} | {rp:N0} / {re:N0} | {Distinct(row.Where(p => p.Source == "a")):N0} / {Distinct(row.Where(p => p.Source == "b")):N0} / {Distinct(row.Where(p => p.Source == "d")):N0} | {statements:N0} | {Distinct(generated):N0} ({generated.Select(p => p.Key).Distinct().Count(k => realKeys.Contains(k) == false):N0}) | {Share(fixP, rp)} / {Share(fixE, re)} | {Share(reads, realKeys.Count)} | {floor} |\n");
            }

            text.Append("\n## Per row, by class, fix and piece\n\n");
            foreach (var (id, name, _) in Rows.Append(("bare", "Plain path reads in a column (not asked of S)", "")))
            {
                var row = pushes.Where(p => p.Row == id).ToList();
                if (row.Count == 0)
                {
                    text.Append($"### {name}\n\nNo push.\n\n");
                    continue;
                }

                text.Append($"### {name}\n\n| Class | Real P | Real E | (a) statements P | Generated | S piece |\n|---|---|---|---|---|---|\n");
                foreach (var cls in row.GroupBy(p => p.Class).OrderByDescending(g => g.Count()))
                {
                    var real = cls.Where(p => Real.Contains(p.Source)).ToList();
                    text.Append($"| {cls.Key} | {Distinct(real, P):N0} | {Distinct(real, E):N0} | {real.Where(p => p.Source == "a" && p.Set == P).Select(p => p.Origin).Distinct().Count():N0} | {Distinct(cls.Where(p => p.Source == "c")):N0} | {string.Join(", ", cls.Select(p => p.Piece).Distinct())} |\n");
                }

                var fixes = row.Where(p => Real.Contains(p.Source)).SelectMany(p => p.Fixes.Select(f => (Fix: f, Push: p))).GroupBy(x => x.Fix).ToList();
                if (fixes.Count > 0)
                {
                    text.Append("\n| Fix class (real) | Production | Emulator |\n|---|---|---|\n");
                    foreach (var fix in fixes.OrderBy(f => f.Key, StringComparer.Ordinal))
                        text.Append($"| {fix.Key} | {Distinct(fix.Select(x => x.Push), P):N0} | {Distinct(fix.Select(x => x.Push), E):N0} |\n");
                }

                text.Append('\n');
            }

            text.Append("## Failures\n\n");
            foreach (var failure in pushes.Where(p => p.Row == "failure").GroupBy(p => (p.Source, p.Position, p.Text)).OrderByDescending(g => g.Count()).Take(40))
                text.Append($"- ({failure.Key.Source}) {failure.Key.Position}: {failure.Count():N0} × {failure.Key.Text}\n");

            return text.ToString();
        }

        // ── Checks on the census itself ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The generated population is fixed by its seed, its default a prefix of its gate's, two statements an expression.</summary>
        [Fact]
        public void TheGeneratedPopulationIsFixed()
        {
            var gate = Generated(CosmosDifferentialTests.GatePartitionedPredicates);
            gate.Should().HaveCount(2 * CosmosDifferentialTests.GatePartitionedPredicates);
            Generated(CosmosDifferentialTests.DefaultPartitionedPredicates).Should().Equal(gate.Take(2 * CosmosDifferentialTests.DefaultPartitionedPredicates));
            gate.Where((_, i) => i % 2 == 1).Should().OnlyContain(s => s.Contains(" WHERE CASE WHEN ", StringComparison.Ordinal));
        }

        /// <summary>
        /// The planner corpus's census finds the translator's own pushes, each a row of §3.1, and every statement a site: what
        /// the report's source (a) rests on, over the 78 statements a build runs in seconds.
        /// </summary>
        [Fact]
        public void TheCorpusCensusClassesEveryPush()
        {
            var harness = new HarnessReasonings();
            var reasoning = harness.Make();
            List<Push> pushes;
            using (harness.Planning())
                pushes = [.. RenderingInputs.Corpus(reasoning).SelectMany(s => Census("a", s.Name, s.Plan))];

            pushes.Where(p => p.Row == "failure").Should().BeEmpty();
            pushes.Select(p => p.Row).Distinct().Should().BeSubsetOf(Rows.Select(r => r.Id).Append("bare"));
            pushes.Where(p => p.Row == "A").Select(p => p.Origin).Distinct().Should().HaveCountGreaterThan(60, "nearly every corpus statement sends one");
            pushes.Should().Contain(p => p.Row == "KED" && p.Class == "guarded accessor", "the corpus pushes guarded accessors in its select lists");
            pushes.Where(p => p.Position == "where").Should().OnlyContain(p => p.Rex.Length > 0);
        }

    }

}
