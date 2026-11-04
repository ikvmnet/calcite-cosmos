using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning.Stage3
{

    /// <summary>
    /// Branch-only, deleted at h16: h14's triage (spec §4.2 h14, §7.2 steps 2-3). Each <c>WHERE</c> push the translator
    /// wrote is asked of S as S reads it from the switch (the loader's reading with places, <see cref="ProbeRows.Switched"/>'s
    /// templates): in check mode, the translator's text claimed for the conjunct at <c>Pos</c> and, beneath a <c>NOT</c>, at
    /// <c>Neg</c>; and in generate mode, whether S finds an exact form of its own, which is what h15 sends. Imported from the
    /// statement's root, as the check run imports a sent <c>WHERE</c>.
    /// </summary>
    internal static class WhereTriage
    {

        /// <summary>One S a process, with every template, as production defines it from h15.</summary>
        static readonly RenderingGraph Graph = new(() => ProbeRows.Switched);

        static readonly ConcurrentDictionary<string, Verdicts> Memo = new();

        /// <summary>
        /// What S says of one push under a set: the IR it reads, the claim's verdict at <c>Pos</c> and <c>Neg</c>, the least
        /// refutation at <c>Pos</c>, generate mode's answer and form, and the form the text was read as.
        /// </summary>
        internal sealed record Verdicts(string Ir, string Pos, string Neg, string Refutation, string Best, string BestForm, string Claimed, string Reason, string[]? Holds = null);

        /// <summary>
        /// A push's triage class and its reason: <i>Proven</i> where generate mode finds an exact form and the text is not
        /// refuted; <i>refuted</i> until reproduced on 8083 and 8088, when it is <i>shown wrong</i> or a <i>false
        /// refutation</i>; otherwise <i>Unproven</i>, closable or declined, by what the IR holds under the set.
        /// </summary>
        internal static (string Class, string Reason) Class(TranslatorCensus.Push push)
        {
            if (push.Position != "where")
                return ("", "");

            if (push.Own is not { } own)
                return ("not triaged", push.Source == "u" ? "a translator test's push: no plan, no statement" : "no statement");

            if (own.Best == "Failed" || own.Pos == "Failed")
                return ("failure", own.Reason);

            if (own.Pos == nameof(RenderingVerdictKind.Refuted))
                return ("refuted", own.Refutation);

            if (own.Best == nameof(RenderingVerdictKind.Proven))
                return ("Proven", own.Pos == nameof(RenderingVerdictKind.Proven) ? "the text and S's own form" : "S's own form; the text " + own.Pos);

            if (own.Pos == nameof(RenderingVerdictKind.Proven))
                return ("Unproven closable", "S proves the text, and generates no form for it");

            return Unproven(push, own);
        }

        /// <summary>The kind of a decline's reason: a correctness fix the owner names, waiting on Azure, or no piece in 3A.</summary>
        internal static string KindOf(string reason) =>
            reason.StartsWith("Q3", StringComparison.Ordinal) || reason.StartsWith("Q4", StringComparison.Ordinal) || reason.StartsWith("N12", StringComparison.Ordinal)
                || reason.StartsWith("#246", StringComparison.Ordinal) || reason.StartsWith("the case mapping", StringComparison.Ordinal) ? "correctness fix, the owner names it"
            : reason.Contains("h14b", StringComparison.Ordinal) ? "waits on probe run 1 (h14b)"
            : reason.Contains("(3B)", StringComparison.Ordinal) ? "another PR's"
            : "no piece in 3A, the owner overrides or keeps the code";

        /// <summary>The 3A rows the triage judges, each at its <c>WHERE</c>.</summary>
        static readonly (string Id, string Name)[] Judged =
            [("type-tests", "WHERE type tests"), ("H", "H CASE, truth tests (WHERE)"), ("FG", "F, G predicates, casts, arithmetic"), ("I", "I ITEM (WHERE)"), ("J1-where", "J1 string functions in a WHERE")];

        /// <summary>
        /// The triage's report: per 3A row and set, real (without the translator tests' (u), which have no statement) and
        /// generated distinct pushes, each class's count, the reasons, Q14's floor, and lines 6 and 7.
        /// </summary>
        internal static string Report(IReadOnlyList<TranslatorCensus.Push> pushes)
        {
            var text = new System.Text.StringBuilder("## Per row and set (generated)\n\n");
            string[] real = ["a", "b", "d"];
            foreach (var (id, name) in Judged)
            {
                text.Append($"### {name}\n\n");
                foreach (var set in new[] { nameof(RenderingSet.Production), nameof(RenderingSet.Emulator) })
                {
                    var row = pushes.Where(p => p.Row == id && p.Position == "where" && p.Set == set).ToList();
                    var distinct = row.Where(p => real.Contains(p.Source)).GroupBy(p => p.Key).Select(g => g.FirstOrDefault(p => p.Own is not null) ?? g.First()).ToList();
                    var generated = row.Where(p => p.Source == "c").GroupBy(p => p.Key).Select(g => g.First()).Where(p => distinct.All(d => d.Key != p.Key)).ToList();
                    var units = row.Where(p => p.Source == "u").Select(p => p.Key).Distinct().Count();
                    var classed = distinct.Select(p => (Push: p, Class: Class(p))).ToList();
                    var count = classed.Count;
                    int Of(string cls) => classed.Count(c => c.Class.Class == cls);
                    var declined = classed.Where(c => c.Class.Class == "Unproven declined").ToList();
                    int Decided(string kind) => classed.Count(c => Decision(c.Push, c.Class) == kind);
                    var (accepted, overridden, ruled) = (Decided(OwnerDecisions.Accepted), Decided(OwnerDecisions.Overridden), Decided(OwnerDecisions.EmulatorRule));
                    var open = count - Of("Proven") - accepted - overridden - ruled;
                    var floor = count >= 30 ? $"{count} real: judged on them" : $"**under 30**: judged on all {count} real; generated {generated.Count} beside";
                    var line6 = (open == 0 ? "passes" : $"**fails**: {open} real pushes neither Proven nor decided by name")
                        + $" (Proven {Of("Proven"):N0}, {OwnerDecisions.Accepted} {accepted:N0}, {OwnerDecisions.Overridden} {overridden:N0}, {OwnerDecisions.EmulatorRule} {ruled:N0})";
                    var line7 = Of("false refutation") == 0 ? "passes: 0 false refutations" : $"{Of("false refutation")} false refutations";
                    var portable = distinct.Count(p => p.Portable?.Best == nameof(RenderingVerdictKind.Proven));
                    text.Append($"**{set}**: real distinct {count:N0} (translator tests apart, untriaged: {units:N0}); generated beyond real {generated.Count:N0}, of which Proven {generated.Count(p => Class(p).Class == "Proven"):N0}.\n\n");
                    text.Append($"| Proven | shown wrong | false refutation | refuted, not yet reproduced | Unproven closable | Unproven declined | failure | Portable Proven (beside) |\n|---|---|---|---|---|---|---|---|\n");
                    text.Append($"| {Of("Proven"):N0} | {Of("shown wrong"):N0} | {Of("false refutation"):N0} | {Of("refuted"):N0} | {Of("Unproven closable"):N0} | {declined.Count:N0} | {Of("failure"):N0} | {portable:N0} |\n\n");
                    text.Append($"- Floor (Q14): {floor}.\n- Line 6: {line6}.\n- Line 7: {line7}.\n");
                    foreach (var reason in classed.Where(c => c.Class.Class is not "Proven").GroupBy(c => (c.Class.Class, c.Class.Reason, Decided: Decision(c.Push, c.Class)))
                        .OrderBy(g => g.Key.Class).ThenByDescending(g => g.Count()))
                        text.Append($"- {reason.Key.Class}{(reason.Key.Class == "Unproven declined" ? " (" + KindOf(reason.Key.Reason) + ")" : "")}{(reason.Key.Decided is { } d ? ", **" + d + "**" : "")}: "
                            + $"{reason.Count():N0}, {reason.Key.Reason}; e.g. `{reason.First().Push.Text.Replace("`", "'")}`\n");
                    text.Append('\n');
                }
            }

            // A name no push answers to is stale: the push is gone, Proven, or read for another reason, and decides nothing.
            var answered = pushes.Where(p => p.Position == "where").Select(p => (p.Set, p.Text, Class(p).Reason)).ToHashSet();
            var stale = OwnerDecisions.Named.Where(d => answered.Contains((d.Set, d.Text, d.Reason)) == false).ToList();
            text.Append($"## The owner's decisions by name (2026-11-03)\n\n{OwnerDecisions.Named.Count:N0} named: "
                + string.Join(", ", OwnerDecisions.Named.GroupBy(d => (d.Kind, d.Set)).Select(g => $"{g.Key.Kind} under {g.Key.Set} {g.Count():N0}")) + $". Answered by no push: {stale.Count:N0}.\n");
            foreach (var d in stale)
                text.Append($"- {d.Kind} under {d.Set}, {d.Reason}: `{d.Text.Replace("`", "'")}`\n");

            return text.ToString();
        }

        /// <summary>The owner's decision of a push, where the triage left it Unproven for the reason the decision names.</summary>
        internal static string? Decision(TranslatorCensus.Push push, (string Class, string Reason) classed) =>
            classed.Class is "Unproven declined" or "Unproven closable" ? OwnerDecisions.Of(push, classed.Reason)?.Kind : null;

        /// <summary>
        /// Why an affix S reads stays Unproven, by its pattern and reading: a <c>_</c>, which no row measures the service's of
        /// over a line terminator; under <c>Emulator</c> runs between and around <c>%</c>, a <c>LIKE</c> the emulator's
        /// matcher reads over a stored escape, owed; under <c>Production</c> one run between two <c>%</c>, which Azure reads
        /// only to a string's first U+0000; a pattern a number's rendering may match, which the service reads as no string; a
        /// raw column cast to text, whose columns' union parts what a negation keeps.
        /// </summary>
        static string Affix(string rex, string ir, bool emulator)
        {
            if (Regex.Match(rex, @"LIKE\(.*, '((?:[^']|'')*)'\)") is not { Success: true } like)
                return "an affix whose pattern the triage reads not";

            var pattern = like.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal);
            var canonical = AffixPattern.Canonical(pattern);
            if (pattern.Contains('_'))
                return "LIKE's _: one code point that is no line terminator, which no row measures the service at";
            if (emulator && canonical is not null && AffixPattern.Written(canonical) is { Test: CosmosServiceTest.Like })
                return "LIKE of runs around and between %: the emulator's matcher over a stored escape is owed";
            if (emulator == false && canonical is not null && AffixPattern.Runs(canonical) is ["", { Length: > 0 }, ""])
                return "one run between two %: Azure's CONTAINS reads a string only to its first U+0000, so S answers either over one that may hold it (sound refusal)";
            if (pattern.All(c => c == '%' || Renderings.MayBeNumberUnit(c)))
                return "a pattern a number's rendering may match, over a path that may hold one: the service reads no number as a string (sound refusal)";
            if (ir.Contains("cast(VARCHAR, raw(", StringComparison.Ordinal))
                return "an affix over a raw column cast to text: the columns' union, ANY rendering a number or a boolean where VARIANT reads null, parts what a negation keeps (the reading names no column)";
            return "an affix: Unproven otherwise";
        }

        static (string Class, string Reason) Unproven(TranslatorCensus.Push push, Verdicts own)
        {
            const string Declined = "Unproven declined", Closable = "Unproven closable";
            var (rex, emulator) = (push.Rex, push.Set == nameof(RenderingSet.Emulator));
            var holds = (own.Holds ?? []).ToHashSet();
            bool Has(string prefix) => holds.Any(h => h.StartsWith(prefix, StringComparison.Ordinal));
            bool Rexes(string text) => rex.Contains(text, StringComparison.Ordinal);
            bool Matches(string pattern) => Regex.IsMatch(rex, pattern);

            // Run 1 gave every row h14 waited on an Azure target (h14b): what stays Unproven is classed alike under both sets.

            if (Has("parameter"))
                return (Declined, "a parameter (3B)");
            if (Has("opaque"))
            {
                if (Rexes("LIKE(") && rex.Contains('['))
                    return (Declined, "N12: LIKE with a bracket");
                if (Matches(@"(SQRT|LN|LOG10|LOG|ASIN|ACOS)\("))
                    return (Declined, "Q3: out-of-domain math");
                if (Matches(@"(POWER|EXP|ROUND|SIN|COS|TAN|ATAN|COT|DEGREES|RADIANS|ATAN2)\("))
                    return (Declined, "Q4: an approximate function without bitwise rows");
                if (Rexes("CASE("))
                    return (Declined, "a value CASE compared: 3D's Yield (no 3A piece)");
                // An unnest element is a column the text names by its alias (t0); a declared column cast to text is not one.
                if (Matches(@"CAST\(\$\d+\):VARCHAR") && Regex.IsMatch(push.Text, @"(?<![\w.])t\d+\b") || Rexes("$cor"))
                    return (Declined, "an unnest element: S's places are rooted at the container (no piece)");
                // The affix step reads LIKE over a computed text place and a VARCHAR column; what stays opaque is over a call no row types.
                if (Matches(@"LIKE\((UPPER|LOWER|\|\||CONCAT|SUBSTRING|TRIM)\("))
                    return (Closable, "an affix over a call the loader types no place of");
                if (Matches(@"LIKE\(\$\d+,"))
                    return (Closable, "LIKE over a column the loader reads no affix of");
                if (Matches(@"(MOD|ABS|CEIL|FLOOR|SIGN)\("))
                    return (Closable, emulator ? "a function no row names (MOD, ABS, CEIL, FLOOR, SIGN): new function rows" : "a function no row names: new function rows, Azure's half not measured");
                if (Rexes("SUBSTRING("))
                    return (Closable, "SUBSTRING from 0: outside the rows' spellings");
                // The text cast to BOOLEAN and a schema's own IS_DEFINED are read (the readers step); a schema's other type
                // tests are each another operator instance the loader does not yet read by reference (TODO.md).
                if (Matches(@"^(NOT\()?IS_(BOOL|NUMBER|STRING|NULL|ARRAY|OBJECT|PRIMITIVE)\("))
                    return (Closable, "a schema's own type test but IS_DEFINED: another operator, not yet read by reference");
                if (Matches(@"^[<>=]+\((\$\d+, JSON_VALUE|JSON_VALUE\([^)]*\), \$\d+\))"))
                    return (Closable, "a column compared with a text reading: two readings of different kinds, no pair");
                return (Closable, "outside the IR: other");
            }

            var arithmetic = holds.Where(h => Regex.IsMatch(h, @"^place:[-+*/%]:")).ToList();
            if (arithmetic.Any(h => h.EndsWith(":INTEGER", StringComparison.Ordinal) || h.EndsWith(":BIGINT", StringComparison.Ordinal)))
                return (Declined, "Q3: integer overflow");
            if (arithmetic.Count > 0)
                return (Declined, "Q3: arithmetic past 2^53 or its rounding (no magnitude class)");
            if (Has("fraction literal"))
                return (Declined, "a fraction literal no double is: the service's double of it is placed against no cell (sound refusal)");
            if (Has("place:UPPER") || Has("place:LOWER"))
                return (Declined, "the case mapping beyond ASCII: legality under every set until cased witnesses are measured");
            // A raw reading under a function: the rows read a call's operand through the text, a typed VARCHAR or its cast alone.
            if (Has("place:") && Regex.IsMatch(own.Ir, @"(\|\||CONCAT|SUBSTRING|TRIM [A-Z]+|REPLACE|REVERSE|POSITION|CHAR_LENGTH)\(raw\("))
                return (Closable, "a function over a raw reading: no row reads a raw value under a function (the rows' Calcite side through the raw reader)");
            if (Has("case") && emulator == false)
                return (Closable, "a ternary: Unproven otherwise, S1 measured on both targets");
            if (Has("place:") && emulator == false)
                return (Closable, "a computed place: Unproven otherwise, the function rows measured on both targets");
            if (Has("affix"))
                return (Closable, Affix(rex, own.Ir, emulator));
            // Two texts ordered and Unproven under the emulator are its stored order over U+0000 (RenderingPairTests): a
            // pair's absent and null cells are read since post1, and two nulls the service keeps S refutes rather than leaves.
            if (Has("pair") && emulator && Regex.IsMatch(own.Ir, @"^\(?text\([^)]*\) (<|<=|>|>=) text\("))
                return (Declined, "the emulator's stored string order over U+0000 or the high band: refused under Emulator");
            if (Has("pair"))
                return (Closable, "a pair: Unproven otherwise, its absent and null cells read");
            if (Has("string order") && emulator)
                return (Declined, "the emulator's stored string order over U+0000 or the high band: refused under Emulator");
            if (Has("element") && emulator == false)
                return (Closable, "an element: Unproven otherwise, M4 measured on both targets");
            if (Has("call:"))
                return (Closable, "a call no row names: " + string.Join(",", holds.Where(h => h.StartsWith("call:", StringComparison.Ordinal))));
            return (Closable, "unclassed: " + string.Join(",", holds));
        }

        /// <summary>What an IR holds that the triage classes by: each kind of term, each call with its type where it is a place.</summary>
        static IEnumerable<string> Holds(SqlPredicate p)
        {
            switch (p)
            {
                case SqlPredicate.And a:
                    return a.Arms.SelectMany(Holds);
                case SqlPredicate.Or o:
                    return o.Arms.SelectMany(Holds);
                case SqlPredicate.Not n:
                    return Holds(n.Operand);
                case SqlPredicate.Beneath b:
                    return Holds(b.Leaf);
                case SqlPredicate.Case c:
                    return Holds(c.Condition).Concat(Holds(c.Then)).Concat(Holds(c.Else)).Prepend("case");
                case SqlPredicate.Compare { Op: not ("=" or "<>"), Right: SqlValue.Literal { Kind: "string" } } c:
                    return Values(c.Left).Prepend("string order");
                case SqlPredicate.Compare c:
                    return Values(c.Left).Concat(Values(c.Right)).Concat(IsReading(c.Left) && IsReading(c.Right) ? ["pair"] : []);
                case SqlPredicate.In i:
                    return Values(i.Value).Prepend("in");
                case SqlPredicate.IsNull n:
                    return Values(n.Value);
                case SqlPredicate.IsDefined d:
                    return Values(d.Value);
                case SqlPredicate.TypeTest t:
                    return Values(t.Value).Prepend("typetest");
                case SqlPredicate.Affix x:
                    return Values(x.Value).Prepend("affix");
                case SqlPredicate.Constant:
                    return ["constant"];
                case SqlPredicate.Opaque:
                    return ["opaque"];
                default:
                    return [p.GetType().Name];
            }

            static bool IsReading(SqlValue v) => v.PathRead is not null || v is SqlValue.Call { Type: not null };
        }

        static IEnumerable<string> Values(SqlValue v) => v switch
        {
            SqlValue.Parameter => ["parameter"],
            SqlValue.Literal { Kind: "fraction" } => ["fraction literal"],
            SqlValue.Call { Type: { } type } call => call.Operands.SelectMany(Values).Prepend("place:" + call.Function + ":" + type),
            SqlValue.Call call => call.Operands.SelectMany(Values).Prepend("call:" + call.Function),
            SqlValue.Cast cast => Values(cast.Value),
            SqlValue.Case c => c.Conditions.SelectMany(Holds).Concat(c.Arms.SelectMany(Values)).Prepend("value case"),
            _ when v.PathRead is { HasElement: true } => ["element"],
            _ => [],
        };

        /// <summary>What generate mode derived of a push's root and atoms: each candidate's agreement or refutation, for reading an Unproven by hand.</summary>
        internal static IEnumerable<string> Explain(CosmosFilterStatement statement, int conjunct, string rootAlias, RenderingSet set)
        {
            var (fields, readings) = statement.BindingForCheck;
            var ir = new CosmosPredicateLoader.Loading(fields, readings, rootAlias, places: true).Statement(statement.Conjuncts).Conjuncts[conjunct];
            var root = RenderingCheckRun.RootOf([statement.ScopeForCheck])!.Value;
            var source = new ImportSource(statement.RenderingStatement.Digest, "triage", 1);
            using var bundle = Graph.Open(set);
            var term = bundle.Write(ir);
            var report = RenderingImporter.Import(bundle, source, root.Context, root.Rules, statement.DeclarationForCheck, ir.Paths.Concat(ir.Places));
            bundle.Generate(term);
            var run = bundle.RunGenerate(RenderingBundle.WidthOf([ir]));
            yield return $"ir {ir}; import {report.Digest}; run {run}; best {bundle.ReadBest(term)}";
            var theory = bundle.Theory;
            foreach (var row in bundle.Scope.Rows(theory.Candidate))
            {
                var (node, pos, form) = (row.Keys[0], (RenderPosition)row.Keys[1], (ServiceForm)row.Keys[2]);
                var agrees = bundle.Scope.Rows(theory.Agrees).Where(a => a.Keys[0].Equals(node) && a.Keys[1].Equals(pos) && a.Keys[2].Equals(form)).Select(a => a.Keys[3].ToString());
                var disagrees = bundle.Scope.Rows(theory.Disagrees).Where(a => a.Keys[0].Equals(node) && a.Keys[1].Equals(pos) && a.Keys[2].Equals(form))
                    .Select(a => a.Keys[3] + " refuted " + RenderingGenerateMutationTests.Show((Refutation)a.Output!));
                yield return $"  node {node} {pos} {form.Text}: agrees [{string.Join(",", agrees)}] {string.Join("; ", disagrees)}";
            }

            foreach (var row in bundle.Scope.Rows(theory.Outcomes))
                yield return "  outcomes " + row.Output;
            GC.KeepAlive(statement);
        }

        /// <summary>Asks S of conjunct <paramref name="conjunct"/> of a statement, its text as the translator wrote it, under a set.</summary>
        public static Verdicts Of(CosmosFilterStatement statement, int conjunct, string rootAlias, string written, CosmosParameterList parameters, RenderingSet set)
        {
            try
            {
                var (fields, readings) = statement.BindingForCheck;
                var ir = new CosmosPredicateLoader.Loading(fields, readings, rootAlias, places: true).Statement(statement.Conjuncts).Conjuncts[conjunct];
                var read = CosmosSqlFormReader.Read(written, [.. parameters.Parameters]);
                var claim = read.Written == written ? read.Form : ServiceForm.OpaqueOf(written, Eggnog.ValueSet<string>.Empty);
                if (RenderingCheckRun.RootOf([statement.ScopeForCheck]) is not { } root)
                    return new(ir.ToString(), "", "", "", "", "", claim.Text, "rooted apart");

                var source = new ImportSource(statement.RenderingStatement.Digest, "triage", 1);
                ImportReport Import(RenderingBundle? bundle, SqlPredicate p) =>
                    RenderingImporter.Import(bundle, source, root.Context, root.Rules, statement.DeclarationForCheck, p.Paths.Concat(p.Places));

                var key = string.Join("\u0001", set, ir, Import(null, ir).Digest, claim.Text);
                return Memo.GetOrAdd(key, _ =>
                {
                    RenderingVerdict Check(SqlPredicate p, ServiceForm f)
                    {
                        using var bundle = Graph.Open(set);
                        var node = bundle.Write(p);
                        Import(bundle, p);
                        bundle.Claim(node, f);
                        bundle.Run(RenderingCheckRun.CheckLimit([f]));
                        return bundle.Read(node, f, Verdict.Equal);
                    }

                    var pos = Check(ir, claim);
                    var neg = Check(new SqlPredicate.Not(ir), ServiceForm.NotOf(claim));

                    using var generate = Graph.Open(set);
                    var term = generate.Write(ir);
                    Import(generate, ir);
                    generate.Generate(term);
                    generate.RunGenerate(RenderingBundle.WidthOf([ir]));
                    var best = generate.ReadBest(term);

                    return new(ir.ToString(), pos.Kind.ToString(), neg.Kind.ToString(), pos.Refutation is { } r ? RenderingGenerateMutationTests.Show(r) : "",
                        best.Kind.ToString(), best.Best?.Text ?? "", claim.Text, pos.Reason ?? best.Reason ?? "", [.. Holds(ir).Distinct().Order()]);
                });
            }
            catch (Exception e)
            {
                return new("", "Failed", "Failed", "", "Failed", "", "", e.GetType().Name + ": " + e.Message.Split('\n')[0]);
            }
            finally
            {
                GC.KeepAlive(statement);
            }
        }

    }

}
