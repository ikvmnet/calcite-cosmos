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
    /// refutation, every proof that holds of nothing, the statements resting on each registry row, and the skips counted.
    /// </summary>
    internal static class RenderingReports
    {

        static readonly string[] Inputs = ["corpus", "differential", "partitions", "probe"];

        /// <summary>Writes every report, and returns the folder.</summary>
        public static string Write(RenderingCheckRun run, string? folder = null)
        {
            folder = Path.GetFullPath(folder ?? Path.Combine("TestResults", "rendering"));
            Directory.CreateDirectory(folder);
            var rows = Ordered(run.Conjuncts).ToList();

            Tsv(folder, "verdicts.tsv", ["input", "name", "statement", "conjunct", "site", "ir", "text", "verdict", "portable", "alone", "reason", "entry", "run", "open", "constrained"],
                rows.Select(c => new object?[] { c.Input, c.Name, c.Statement, c.Conjunct, c.Site, c.Ir, c.Text, c.Skip is null ? Name(c.Entry?.Asserted, c) : "Skipped",
                    Name(c.Entry?.Portable, c), c.Position < 0 ? null : Name(c.Entry!.Alone[c.Position], c), c.Skip ?? c.Reason, c.Entry?.Id,
                    c.Entry?.AssertedRun is { } run ? $"{run.StopReason}{(run.OneNodePerClass ? "" : ", merged")}{(run.Contradicted ? ", contradicted" : "")}" : null,
                    c.Entry?.Import.DefaultedOpen, c.Entry?.Import.DefaultedConstrained }));

            Tsv(folder, "refutations.tsv", ["input", "name", "statement", "entry", "cells", "class", "rows", "constrained", "conditional", "emulator", "triage"],
                Statements(rows).Select(c => (c, r: c.Entry!.Asserted.Refutation)).Where(x => x.r is not null).Select(x => new object?[] { x.c.Input, x.c.Name, x.c.Statement,
                    x.c.Entry!.Id, string.Join("; ", x.r!.Cells.AsEnumerable()), x.r.Class, string.Join(" ", x.r.Rows.AsEnumerable()), x.c.Entry.Import.DefaultedConstrained,
                    x.c.Conditional, "not run", Triage(x.c, x.r) }));

            Tsv(folder, "vacuous-proofs.tsv", ["input", "name", "statement", "entry", "why", "form"],
                Statements(rows).Where(c => c.Entry!.Import.Contradicted || Vacuous(c.Entry!)).Select(c => new object?[] { c.Input, c.Name, c.Statement, c.Entry!.Id,
                    c.Entry.Import.Contradicted ? "the render licence keeps nothing" : "Calcite raises on every row", c.Entry.Key.Form }));

            var resting = Resting(run);
            Tsv(folder, "proof-lines.tsv", ["row", "statements", "which"],
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

        /// <summary>Each registry row a proven statement's proof reads, and the statements resting on it.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resting(RenderingCheckRun run) =>
            Statements(Ordered(run.Conjuncts)).Where(c => c.Entry!.Asserted.Proof is not null)
                .SelectMany(c => c.Entry!.Asserted.Proof!.Rows.AsEnumerable().Select(row => (Row: row, Statement: $"{c.Input}:{c.Name}#{c.Statement}")))
                .GroupBy(r => r.Row, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Statement).Distinct()], StringComparer.Ordinal);

        /// <summary>A refutation warns until the emulator reproduces it where the facts it was found on may admit what no document holds.</summary>
        static string Triage(RenderingCheckRun.Checked c, Refutation r) =>
            c.Entry!.Import.DefaultedConstrained > 0 ? "warns: a default the declaration constrains"
            : r.Cells.Count > 1 && c.Conditional ? "warns: a cell over two paths, under conditional rules"
            : "fails";

        static bool Vacuous(RenderingMemo.Entry entry) =>
            entry.Asserted is { Kind: RenderingVerdictKind.Proven, Vacuous: true } || entry.Alone.Any(v => v is { Kind: RenderingVerdictKind.Proven, Vacuous: true });

        static string Name(RenderingVerdict? verdict, RenderingCheckRun.Checked c) => verdict switch
        {
            null => "",
            { Kind: RenderingVerdictKind.Proven } => c.Entry!.Key.Claim == Verdict.Equal ? "Exact" : "Implied",
            _ => verdict.Kind.ToString(),
        };

        /// <summary>One row a checked statement: its first checked conjunct's.</summary>
        static IEnumerable<RenderingCheckRun.Checked> Statements(IEnumerable<RenderingCheckRun.Checked> rows) =>
            rows.Where(c => c.Entry is not null).GroupBy(c => (c.Input, c.Name, c.Statement)).Select(g => g.First());

        static IEnumerable<RenderingCheckRun.Checked> Ordered(IEnumerable<RenderingCheckRun.Checked> rows) =>
            rows.OrderBy(c => Array.IndexOf(Inputs, c.Input)).ThenBy(c => int.TryParse(c.Name, CultureInfo.InvariantCulture, out var n) ? n : -1)
                .ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Statement).ThenBy(c => c.Conjunct);

        static void Tsv(string folder, string name, string[] header, IEnumerable<object?[]> rows) =>
            File.WriteAllLines(Path.Combine(folder, name), [string.Join("\t", header), .. rows.Select(r => string.Join("\t", r.Select(Field)))]);

        static string Field(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\t", " ").Replace("\n", " ").Replace("\r", "") ?? "";

    }

}
