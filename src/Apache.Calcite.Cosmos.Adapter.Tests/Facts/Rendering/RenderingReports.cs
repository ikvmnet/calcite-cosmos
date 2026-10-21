using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The check run's reports (spec §5.6), tab-separated, in <c>TestResults/rendering/</c>: every conjunct's verdict, each
    /// refutation, every proof that holds of nothing, the checks resting on each registry row, and the skips counted.
    /// </summary>
    internal static class RenderingReports
    {

        static readonly string[] Inputs = ["corpus", "differential", "partitions", "probe"];

        /// <summary>Writes every report, and returns the folder.</summary>
        public static string Write(RenderingCheckRun run)
        {
            var folder = Path.GetFullPath(Path.Combine("TestResults", "rendering"));
            Directory.CreateDirectory(folder);
            var rows = Ordered(run.Conjuncts).ToList();

            Tsv(folder, "verdicts.tsv", ["input", "name", "unit", "statement", "conjunct", "site", "ir", "text", "verdict", "portable", "alone", "reason", "entry", "run", "open", "constrained"],
                rows.Select(c => new object?[] { c.Input, c.Name, c.Unit, c.Statement, c.Conjunct, c.Site, c.Ir, c.Text, c.Skip is null ? Name(c.Entry?.Asserted, c) : "Skipped",
                    Name(c.Entry?.Portable, c), Name(c.Entry?.Alone[c.Position], c), c.Skip ?? c.Entry?.Asserted.Reason, c.Entry?.Id,
                    c.Entry?.AssertedRun is { } run ? $"{run.StopReason}{(run.OneNodePerClass ? "" : ", merged")}{(run.Contradicted ? ", contradicted" : "")}" : null,
                    c.Import?.DefaultedOpen, c.Import?.Constrained.Count }));

            Tsv(folder, "refutations.tsv", ["input", "name", "unit", "entry", "cells", "class", "rows", "constrained", "conditional", "emulator", "triage"],
                Refutations(run).Select(x => new object?[] { x.Check.Input, x.Check.Name, x.Check.Unit, x.Check.Entry!.Id, string.Join("; ", x.Refutation.Cells.AsEnumerable()),
                    x.Refutation.Class, string.Join(" ", x.Refutation.Rows.AsEnumerable()), string.Join(" ", x.Check.Import!.Constrained.AsEnumerable()), x.Check.Conditional, "not run", x.Triage }));

            Tsv(folder, "vacuous-proofs.tsv", ["input", "name", "unit", "entry", "why", "form"],
                Units(rows).Where(RestsOnNothing).Select(c => new object?[] { c.Input, c.Name, c.Unit, c.Entry!.Id,
                    c.Import!.Contradicted ? "the render licence keeps nothing" : "Calcite raises on every row", c.Entry.Key.Form }));

            var resting = Resting(run);
            Tsv(folder, "proof-lines.tsv", ["row", "checks", "which"],
                CosmosAxioms.All.Select(a => a.Id).Concat(resting.Keys).Distinct().OrderBy(id => resting.ContainsKey(id) ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal)
                    .Select(id => resting.TryGetValue(id, out var on) ? new object?[] { id, on.Count, string.Join(" ", on) } : [id, 0, "read by no proof"]));

            Tsv(folder, "skipped.tsv", ["input", "category", "count"],
                rows.Where(c => c.Skip is not null).Select(c => (c.Input, Category: c.Skip!))
                    .Concat(run.Outside.Select(o => (o.Input, Category: "written outside the translator")))
                    .Concat(run.Failures.Select(f => (f.Input, Category: f.Failure.Split(':')[0])))
                    .GroupBy(s => s).OrderBy(g => Array.IndexOf(Inputs, g.Key.Input)).ThenBy(g => g.Key.Category, StringComparer.Ordinal)
                    .Select(g => new object?[] { g.Key.Input, g.Key.Category, g.Count() }));

            return folder;
        }

        /// <summary>Each registry row a proven check's proof reads, and the checks resting on it.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resting(RenderingCheckRun run) =>
            Units(Ordered(run.Conjuncts)).Where(c => c.Entry!.Asserted.Proof is not null)
                .SelectMany(c => c.Entry!.Asserted.Proof!.Rows.AsEnumerable().Select(row => (Row: row, Check: $"{c.Input}:{c.Name}#{c.Unit}")))
                .GroupBy(r => r.Row, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Check).Distinct()], StringComparer.Ordinal);

        /// <summary>Each check refuted, and its triage.</summary>
        public static IEnumerable<(RenderingCheckRun.Checked Check, Refutation Refutation, string Triage)> Refutations(RenderingCheckRun run) =>
            Units(Ordered(run.Conjuncts)).Where(c => c.Entry!.Asserted.Refutation is not null).Select(c => (c, c.Entry!.Asserted.Refutation!, Triage(c, c.Entry.Asserted.Refutation!)));

        /// <summary>
        /// A check that rests on nothing: its render licence keeps no document (#217's shape), or a proof of it or of a
        /// conjunct in it holds only where Calcite raises on every row.
        /// </summary>
        public static bool RestsOnNothing(RenderingCheckRun.Checked c) => c.Import!.Contradicted
            || c.Entry!.Asserted is { Kind: RenderingVerdictKind.Proven, Vacuous: true } || c.Entry.Alone.Any(v => v is { Kind: RenderingVerdictKind.Proven, Vacuous: true });

        /// <summary>
        /// A refutation warns until the emulator reproduces it where the facts it was found on may admit what no document
        /// holds: a refuting cell on a path whose import defaulted constrained, or a cell over two paths under conditional
        /// rules. Where the check holds a conjunct a later round admitted, the triage says it was judged under round 1's facts.
        /// </summary>
        static string Triage(RenderingCheckRun.Checked c, Refutation r) =>
            (r.Cells.Any(cell => c.Import!.Constrained.Any(path => cell.StartsWith(path + "=", StringComparison.Ordinal))) ? "warns: a default the declaration constrains"
            : r.Cells.Count > 1 && c.Conditional ? "warns: a cell over two paths, under conditional rules"
            : "fails") + (c.LaterRound ? "; a later round's conjunct, judged under round 1's facts" : "");

        static string Name(RenderingVerdict? verdict, RenderingCheckRun.Checked c) => verdict switch
        {
            null => "",
            { Kind: RenderingVerdictKind.Proven } => c.Entry!.Key.Claim == Verdict.Equal ? "Exact" : "Implied",
            _ => verdict.Kind.ToString(),
        };

        /// <summary>One row a check: its first checked conjunct's.</summary>
        static IEnumerable<RenderingCheckRun.Checked> Units(IEnumerable<RenderingCheckRun.Checked> rows) =>
            rows.Where(c => c.Entry is not null).GroupBy(c => (c.Input, c.Name, c.Unit)).Select(g => g.First());

        static IEnumerable<RenderingCheckRun.Checked> Ordered(IEnumerable<RenderingCheckRun.Checked> rows) =>
            rows.OrderBy(c => Array.IndexOf(Inputs, c.Input)).ThenBy(c => int.TryParse(c.Name, CultureInfo.InvariantCulture, out var n) ? n : -1)
                .ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Unit).ThenBy(c => c.Position);

        static void Tsv(string folder, string name, string[] header, IEnumerable<object?[]> rows) =>
            File.WriteAllLines(Path.Combine(folder, name), [string.Join("\t", header), .. rows.Select(r => string.Join("\t", r.Select(Field)))]);

        static string Field(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\t", " ").Replace("\n", " ").Replace("\r", "") ?? "";

    }

}
