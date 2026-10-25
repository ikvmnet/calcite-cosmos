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

        /// <summary>One writer at a time: the check and witness tests write these from two collections at once.</summary>
        static readonly object Writing = new();

        /// <summary>What the emulator made of each refutation's witness, once the witness test has run, which every write carries.</summary>
        static IReadOnlyDictionary<int, string>? _witnessed;

        /// <summary>Writes every report, and returns the folder.</summary>
        /// <param name="run">The check run.</param>
        /// <param name="witnessed">What the emulator made of each refutation's witness, by memo entry, where it was run.</param>
        public static string Write(RenderingCheckRun run, IReadOnlyDictionary<int, string>? witnessed = null)
        {
            lock (Writing)
                return Written(run, _witnessed = witnessed ?? _witnessed);
        }

        /// <summary>The reports' folder, made where it is not.</summary>
        static string Folder()
        {
            var folder = Path.GetFullPath(Path.Combine("TestResults", "rendering"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        static string Written(RenderingCheckRun run, IReadOnlyDictionary<int, string>? witnessed)
        {
            var folder = Folder();
            var rows = Ordered(run.Conjuncts).ToList();
            var skipping = Skipping(run);

            Tsv(folder, "verdicts.tsv", ["input", "name", "unit", "statement", "conjunct", "site", "ir", "text", "verdict", RenderingCheckRun.BesideSet.ToString().ToLowerInvariant(), "alone", "reason", "entry", "run", "open", "constrained"],
                rows.Select(c => new object?[] { c.Input, c.Name, c.Unit, c.Statement, c.Conjunct, c.Site, c.Ir, c.Text, c.Skip is null ? Name(c.Entry?.Asserted, c) : "Skipped",
                    Name(c.Entry?.Beside, c), Name(c.Entry?.Alone[c.Position], c),
                    c.Skip ?? c.Entry?.Asserted.Reason ?? (c.Entry?.Asserted.Kind == RenderingVerdictKind.Unproven && skipping.Contains((c.Input, c.Name, c.Unit)) ? "beside a skipped conjunct" : null), c.Entry?.Id,
                    c.Entry?.AssertedRun is { } run ? $"{run.StopReason}{(run.OneNodePerClass ? "" : ", merged")}{(run.Contradicted ? ", contradicted" : "")}" : null,
                    c.Import?.DefaultedOpen, c.Import?.Constrained.Count }));

            Tsv(folder, "refutations.tsv", ["input", "name", "unit", "entry", "level", "cells", "class", "rows", "constrained", "conditional", "witness", "emulator", "triage"],
                Refutations(run).Select(x => new object?[] { x.Check.Input, x.Check.Name, x.Check.Unit, x.Check.Entry!.Id, "check", string.Join("; ", x.Refutation.Cells.AsEnumerable()),
                    x.Refutation.Class, string.Join(" ", x.Refutation.Rows.AsEnumerable()), string.Join(" ", x.Check.Import!.Constrained.AsEnumerable()), x.Check.Conditional,
                    x.Check.Entry.What?.Witness is { } cells ? string.Join("; ", cells.Select(c => c.Key + "=" + (c.Value ?? "absent"))) : null,
                    witnessed?.GetValueOrDefault(x.Check.Entry.Id) ?? "not run", x.Triage })
                .Concat(Alone(run).Select(x => new object?[] { x.Check.Input, x.Check.Name, x.Check.Unit, x.Check.Entry!.Id, "conjunct " + x.Check.Position,
                    string.Join("; ", x.Refutation.Cells.AsEnumerable()), x.Refutation.Class, string.Join(" ", x.Refutation.Rows.AsEnumerable()),
                    string.Join(" ", x.Check.Import!.Constrained.AsEnumerable()), x.Check.Conditional, null, "not run",
                    $"reported: its check is {x.Check.Entry.Asserted.Kind}, and holds what the service is asked beside it" })));

            Tsv(folder, "vacuous-proofs.tsv", ["input", "name", "unit", "entry", "why", "form"],
                Units(rows).Where(RestsOnNothing).Select(c => new object?[] { c.Input, c.Name, c.Unit, c.Entry!.Id,
                    c.Import!.Contradicted ? "the render licence keeps nothing" : "Calcite raises on every row", c.Entry.Key.Form }));

            var resting = Resting(run);
            Tsv(folder, "proof-lines.tsv", ["row", "checks", "which"],
                CosmosAxioms.All.Select(a => a.Id).Concat(resting.Keys).Distinct().OrderBy(id => resting.ContainsKey(id) ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal)
                    .Select(id => resting.TryGetValue(id, out var on) ? new object?[] { id, on.Count, string.Join(" ", on) } : [id, 0, "read by no proof"]));

            Tsv(folder, "skipped.tsv", ["input", "category", "count"],
                rows.Where(c => c.Skip is not null).Select(c => (c.Input, Category: c.Site == 3 ? "weakening " + c.Skip : c.Skip!))
                    .Concat(run.Outside.Select(o => (o.Input, Category: "written outside the translator")))
                    .Concat(run.Failures.Select(f => (f.Input, Category: f.Failure.Split(':')[0])))
                    .GroupBy(s => s).OrderBy(g => Array.IndexOf(Inputs, g.Key.Input)).ThenBy(g => g.Key.Category, StringComparer.Ordinal)
                    .Select(g => new object?[] { g.Key.Input, g.Key.Category, g.Count() }));

            return folder;
        }

        /// <summary>The pairwise check's report, <c>pairwise.tsv</c>: a row a pair.</summary>
        public static void WritePairwise(RenderingPairwiseTests.Compared compared)
        {
            lock (Writing)
                Tsv(Folder(), "pairwise.tsv", ["set", "bundle", "node", "position", "of", "a", "b", "compared", "raised", "parted"], compared.Rows);
        }

        /// <summary>
        /// The generate shadow's reports (spec g05): <c>shadow-admission.tsv</c>, each pending conjunct of each round under
        /// each set, with its class; <c>shadow-weakening.tsv</c>, each residual's weakening beside J's; and
        /// <c>shadow-summary.txt</c>, the classes per set and input, the regressions by reason against O2's caps, the
        /// weakenings' classes, and S's time, first and again, and the bundles opened, by shape: of each statement, and of
        /// each query, every statement its plan asked S of. Returns the summary's lines.
        /// </summary>
        public static List<string> WriteShadow(RenderingGenerateRun shadow, int cap)
        {
            lock (Writing)
            {
                var folder = Folder();
                var classes = shadow.Classes.ToDictionary(c => (c.Input, c.Name, c.Statement, c.Conjunct, c.Set));
                static (int, int, string, int, int) Order(string input, string name, int statement, int conjunct) => (Key(input, name).Input, Key(input, name).Number, name, statement, conjunct);

                Tsv(folder, "shadow-admission.tsv", ["input", "name", "statement", "conjunct", "round", "set", "owner", "translator", "verdict", "s", "form", "reason", "beneath", "bundle", "ms", "class", "why"],
                    shadow.Admissions.OrderBy(a => Order(a.Input, a.Name, a.Statement, a.Conjunct)).ThenBy(a => a.Round).ThenBy(a => a.Set).Select(a =>
                        classes.GetValueOrDefault((a.Input, a.Name, a.Statement, a.Conjunct, a.Set)) is var c ? new object?[] { a.Input, a.Name, a.Statement, a.Conjunct, a.Round, a.Set,
                        a.Owned ? "S" : "translator", a.Text, a.TextVerdict, a.Answer.Kind, a.Answer.Best?.Text, a.Answer.Reason, a.Stratum is { } j ? "round " + j : "root", a.Bundle,
                        a.Milliseconds.ToString("F2", CultureInfo.InvariantCulture), c?.Class, c?.Reason } : null!));

                var weakenings = shadow.Weakenings.OrderBy(w => Order(w.Input, w.Name, w.Statement, w.Conjunct)).ThenBy(w => w.Set).Select(w => (Row: w, Class: RenderingGenerateRun.ClassOf(w))).ToList();
                Tsv(folder, "shadow-weakening.tsv", ["input", "name", "statement", "conjunct", "set", "owner", "ir", "j", "j verdict", "s", "class", "bundle"],
                    weakenings.Select(w => new object?[] { w.Row.Input, w.Row.Name, w.Row.Statement, w.Row.Conjunct, w.Row.Set, w.Row.Owned ? "S" : "translator", w.Row.Ir, w.Row.Text,
                        w.Row.TranslatorVerdict, w.Row.Mine?.Text, w.Class, w.Row.Bundle }));

                var lines = new List<string>();
                foreach (var set in RenderingGenerateRun.Sets)
                {
                    var asserted = RenderingGenerateRun.AssertedSets.Contains(set) ? "asserted" : "beside";
                    var rows = shadow.Classes.Where(c => c.Set == set).ToList();
                    lines.Add($"{set} ({asserted}): {rows.Count} conjuncts in {rows.Select(r => (r.Input, r.Name, r.Statement)).Distinct().Count()} statements");
                    foreach (var g in rows.GroupBy(r => r.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
                        lines.Add($"  {g.Key}: {g.Count()} ({string.Join(", ", Inputs.Select(i => $"{i} {g.Count(r => r.Input == i)}"))})");

                    var regressions = rows.Where(r => r.Class == RenderingGenerateRun.Regression).ToList();
                    lines.Add($"  regressions {regressions.Count} against the cap {cap}: " + string.Join("; ", regressions.GroupBy(r => r.Reason).Select(g => $"{g.Key} {g.Count()}")));
                    lines.AddRange(regressions.Select(r => $"    {r.Input} {r.Name}/{r.Statement}/{r.Conjunct} ({r.Reason}): {r.Ir} sent {r.Text} [{r.TextVerdict}]"));
                    lines.Add("  weakenings: " + string.Join(", ", weakenings.Where(w => w.Row.Set == set).GroupBy(w => w.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));

                    var proposed = shadow.Bundles.Where(b => b.Set == set).Select(b => b.Proposed).ToList();
                    lines.Add($"  {proposed.Count} generate bundles; {proposed.Sum(p => p.Candidates)} candidates over {proposed.Sum(p => p.Atoms)} atoms, "
                        + $"{(double)proposed.Sum(p => p.Candidates) / Math.Max(1, proposed.Sum(p => p.Atoms)):F2} an atom");
                    // Each run's iterations against its limit (R7): the base of 80 is the margin one-atom bundles hold.
                    var runs = shadow.Bundles.Where(b => b.Set == set && b.Failure is null).Select(b => (b.Iterations, Limit: RenderingBundle.GenerateLimit(RenderingBundle.WidthOf(b.Roots)))).ToList();
                    if (runs.Count > 0 && runs.MaxBy(r => (double)r.Iterations / r.Limit) is var (iterations, limit))
                        lines.Add($"  iterations: the tightest run {iterations} of {limit} ({100.0 * iterations / limit:F0} %); the widest limit {runs.Max(r => r.Limit)}");
                    // A query is an input's statement as planned: every filter statement its plan asked S of, its shape read
                    // off the union of their atoms (§6.3), its time and bundles their sums.
                    var costs = shadow.Costs.Where(c => c.Set == set).ToList();
                    var queries = costs.GroupBy(c => (c.Input, c.Name)).Select(g => new RenderingGenerateRun.Cost(g.Key.Input, g.Key.Name, g.Count(), set,
                        RenderingGenerateRun.ShapeOf(g.SelectMany(c => c.Conjuncts)), g.Sum(c => c.Bundles), g.Sum(c => c.First), g.Sum(c => c.Again), [.. g.SelectMany(c => c.Conjuncts)])).ToList();
                    foreach (var (unit, timed) in new[] { ("statements", costs), ("queries", queries) })
                        foreach (var shape in timed.GroupBy(c => c.Shape).OrderBy(g => Shapes.IndexOf(g.Key)))
                        {
                            var (first, again, bundles) = (Sorted(shape.Select(c => c.First)), Sorted(shape.Select(c => c.Again)), Sorted(shape.Select(c => (double)c.Bundles)));
                            lines.Add($"  {shape.Key}: {first.Count} {unit}{(unit == "queries" ? $" of {shape.Sum(q => q.Statement)} statements" : "")}; bundles median {RenderingCostTests.At(bundles, 0.5)}, "
                                + $"max {bundles[^1]}; S first p50 {RenderingCostTests.At(first, 0.5):F2} p90 {RenderingCostTests.At(first, 0.9):F2} max {first[^1]:F1} ms, total {first.Sum() / 1000:F2} s; "
                                + $"again p50 {RenderingCostTests.At(again, 0.5):F3} p90 {RenderingCostTests.At(again, 0.9):F3} ms");
                        }
                }

                File.WriteAllLines(Path.Combine(folder, "shadow-summary.txt"), lines);
                return lines;
            }

            static List<double> Sorted(IEnumerable<double> values) => [.. values.Order()];
        }

        /// <summary>The shapes a query is read as (spec §6.3), from the narrowest.</summary>
        static readonly List<string> Shapes = ["1-3 conditions", "4 conditions", "5-7 conditions", "a list of 5-19", "rare"];

        /// <summary>The order the reports keep: by input, then by a statement's place where its name is one, then by name.</summary>
        static (int Input, int Number) Key(string input, string name) => (Array.IndexOf(Inputs, input), int.TryParse(name, CultureInfo.InvariantCulture, out var n) ? n : -1);

        /// <summary>
        /// The coverage line's count by input (spec §6): each conjunct sent and not skipped, read by its check's verdict, the
        /// claim made, but by its own at its position where its check is Unproven for holding a skipped conjunct, which the
        /// line excludes with what it does to the check.
        /// </summary>
        public static IReadOnlyDictionary<string, (int Unproven, int Counted)> Coverage(RenderingCheckRun run)
        {
            var skipping = Skipping(run);
            return run.Conjuncts.Where(c => c.Site == 2 && c.Entry is not null)
                .Select(c => (c.Input, Kind: c.Entry!.Asserted.Kind == RenderingVerdictKind.Unproven && skipping.Contains((c.Input, c.Name, c.Unit))
                    ? c.Entry.Alone[c.Position]?.Kind ?? RenderingVerdictKind.Unproven : c.Entry.Asserted.Kind))
                .GroupBy(c => c.Input).ToDictionary(g => g.Key, g => (g.Count(c => c.Kind == RenderingVerdictKind.Unproven), g.Count()));
        }

        static HashSet<(string, string, int)> Skipping(RenderingCheckRun run) => [.. run.Conjuncts.Where(c => c.Skip is not null).Select(c => (c.Input, c.Name, c.Unit))];

        /// <summary>
        /// Each registry row a proven check rests on, and the checks resting on it: the rows its proof reads, and the reader's
        /// grammar, through which its claim was read from the text.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resting(RenderingCheckRun run) =>
            Units(Ordered(run.Conjuncts)).Where(c => c.Entry!.Asserted.Proof is not null)
                .SelectMany(c => c.Entry!.Asserted.Proof!.Rows.AsEnumerable().Append(CosmosSqlFormReader.Row).Select(row => (Row: row, Check: $"{c.Input}:{c.Name}#{c.Unit}")))
                .GroupBy(r => r.Row, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Check).Distinct()], StringComparer.Ordinal);

        /// <summary>Each check refuted, and its triage.</summary>
        public static IEnumerable<(RenderingCheckRun.Checked Check, Refutation Refutation, string Triage)> Refutations(RenderingCheckRun run) =>
            Units(Ordered(run.Conjuncts)).Where(c => c.Entry!.Asserted.Refutation is not null).Select(c => (c, c.Entry!.Asserted.Refutation!, Triage(c, c.Entry.Asserted.Refutation!)));

        /// <summary>
        /// Each conjunct sent refuted at its own position in a check not refuted: reported, not asserted, since the service
        /// is asked the check's junction, which its siblings may decide.
        /// </summary>
        public static IEnumerable<(RenderingCheckRun.Checked Check, Refutation Refutation)> Alone(RenderingCheckRun run) =>
            Ordered(run.Conjuncts).Where(c => c.Site == 2 && c.Entry is { Asserted.Refutation: null } e && e.Alone[c.Position]?.Refutation is not null)
                .Select(c => (c, c.Entry!.Alone[c.Position]!.Refutation!));

        /// <summary>
        /// A check that rests on nothing: its render licence keeps no document (#217's shape), or a proof of it or of a
        /// conjunct in it holds only where Calcite raises on every row.
        /// </summary>
        public static bool RestsOnNothing(RenderingCheckRun.Checked c) => c.Import!.Contradicted
            || c.Entry!.Asserted is { Kind: RenderingVerdictKind.Proven, Vacuous: true } || c.Entry.Alone.Any(v => v is { Kind: RenderingVerdictKind.Proven, Vacuous: true });

        /// <summary>
        /// A refutation warns until the emulator reproduces it where the facts it was found on may admit what no document
        /// holds: a refuting cell on a path whose import defaulted constrained, or a cell over two paths under conditional
        /// rules. Where the check holds a conjunct a later round admitted, the triage says its paths were read beside the facts
        /// it was rendered from.
        /// </summary>
        static string Triage(RenderingCheckRun.Checked c, Refutation r) =>
            (r.Cells.Any(cell => c.Import!.Constrained.Any(path => cell.StartsWith(path + "=", StringComparison.Ordinal))) ? "warns: a default the declaration constrains"
            : r.Cells.Count > 1 && c.Conditional ? "warns: a cell over two paths, under conditional rules"
            : "fails") + (c.LaterRound ? "; a later round's conjunct, read beside the facts it was rendered from" : "");

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
            rows.OrderBy(c => Key(c.Input, c.Name)).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Unit).ThenBy(c => c.Position);

        internal static void Tsv(string folder, string name, string[] header, IEnumerable<object?[]> rows) =>
            File.WriteAllLines(Path.Combine(folder, name), [string.Join("\t", header), .. rows.Select(r => string.Join("\t", r.Select(Field)))]);

        static string Field(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\t", " ").Replace("\n", " ").Replace("\r", "") ?? "";

    }

}
