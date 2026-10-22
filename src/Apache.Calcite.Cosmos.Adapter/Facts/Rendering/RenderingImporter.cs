using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Reads what the facts graph's licensed node says each path may hold into a bundle: one value per path per table,
    /// copied, never met with another (<c>DESIGN.md</c>, <i>The rendering graph in check mode</i>).
    /// </summary>
    internal static class RenderingImporter
    {

        /// <summary>
        /// Copies the node's <c>possible</c> and <c>chars</c> rows for each path into the bundle, before its run, each table
        /// read on its own. A path the node holds no <c>possible</c> row for gets the top (<see cref="PathValues.Anything"/>)
        /// and is counted: open where the declaration mentions nothing at, within or above it, constrained otherwise; a
        /// path with no <c>chars</c> row gets every class (<see cref="JsonValues.AllBands"/>), as the facts graph reads its
        /// absence. A node that keeps no document imports nothing. Beside each path, what it may hold at the node
        /// <paramref name="beneath"/> names for it, a stratum beneath this one, where the earliest round reading it was
        /// rendered: no fact of it is met with the import's, and the cells it admits nothing of are set aside.
        /// </summary>
        /// <param name="bundle">The bundle, not yet run; <c>null</c> for the report alone.</param>
        /// <param name="source">The node's statement, licence and round.</param>
        /// <param name="node">The node the licence chose (<see cref="CosmosStatementScope.ContextFor"/>), frozen; read with
        /// <c>TryGet</c>, which interns nothing.</param>
        /// <param name="rules">The rules the node ran; <c>null</c> where the statement knows nothing.</param>
        /// <param name="declaration">The container's declaration, which says where a default loses precision.</param>
        /// <param name="paths">The paths the bundle's atoms read.</param>
        /// <param name="beneath">The node each path was rendered from, where it is not this one.</param>
        /// <exception cref="InvalidOperationException">The bundle has run, or holds another node's imports.</exception>
        /// <exception cref="FactConflictException">A path was imported with another value.</exception>
        public static ImportReport Import(RenderingBundle? bundle, ImportSource source, Scope? node, PredicateRules? rules,
            JsonFactTheory? declaration, IEnumerable<JsonDocumentPath> paths, IReadOnlyDictionary<JsonDocumentPath, Scope?>? beneath = null)
        {
            // ContextFor hands back no context where the licence keeps nothing; with rules, something was known.
            if (rules is not null && node is null or { IsContradictory: true })
                return new ImportReport(0, 0, ValueSet<string>.Empty, "contradicted", true);

            var (open, constrained, spelled) = (0, new List<string>(), new List<string>());
            foreach (var path in paths.Distinct().OrderBy(SqlValue.Spell, StringComparer.Ordinal))
            {
                var (values, chars, assumed) = ((PathValues?)null, (ValueSet<string>?)null, (PathValues?)null);
                if (node is not null && rules is not null)
                {
                    var term = JsonFactEncoding.Path(path).ToTerm(rules.Json);
                    values = node.TryGet(rules.Possible.Of(term), out var possible) ? possible : null;
                    chars = node.TryGet(rules.Json.Chars.Of(term), out var held) ? held : null;
                    if (beneath?.GetValueOrDefault(path) is { IsContradictory: false } stratum && stratum.TryGet(rules.Possible.Of(term), out var narrower))
                        assumed = narrower;
                }

                if (values is null && declaration?.MentionedPaths.Any(m => path.IsWithin(m) || m.IsWithin(path)) == true)
                    constrained.Add(SqlValue.Spell(path));
                else if (values is null)
                    open++;

                bundle?.Import(source with { Node = node }, path, values ?? PathValues.Anything, chars ?? JsonValues.AllBands, assumed);
                spelled.Add(Spell(path, values ?? PathValues.Anything, chars ?? JsonValues.AllBands) + (assumed is null ? "" : " beneath " + Spell(path, assumed, chars ?? JsonValues.AllBands)));
            }

            return new ImportReport(spelled.Count, open, ValueSet.From(constrained), "[" + string.Join(",", spelled) + "]", false);
        }

        /// <summary>One path's import as JSON, every value quoted, so that two imports that differ never spell alike.</summary>
        internal static string Spell(JsonDocumentPath path, PathValues values, ValueSet<string> chars) => JsonSerializer.Serialize<object?[]>(
        [
            SqlValue.Spell(path), values.Kinds.AsEnumerable(), values.Values?.AsEnumerable(), values.Excluded.AsEnumerable(),
            values.Lower?.Value, values.Lower?.Open, values.Upper?.Value, values.Upper?.Open, chars.AsEnumerable(),
        ]);

    }

}
