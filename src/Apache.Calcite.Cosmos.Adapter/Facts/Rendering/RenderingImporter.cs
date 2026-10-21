using System;
using System.Collections.Generic;
using System.Linq;

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
        /// Copies the node's <c>possible</c> and <c>chars</c> rows for each path into the bundle, before its run. A path the
        /// node holds no <c>possible</c> row for gets the top (<see cref="PathValues.Anything"/>,
        /// <see cref="JsonValues.AllBands"/>) and is counted: open where the declaration mentions nothing at, within or
        /// above it, constrained otherwise. A node that keeps no document imports nothing.
        /// </summary>
        /// <param name="bundle">The bundle, not yet run.</param>
        /// <param name="source">The node's statement, licence and round.</param>
        /// <param name="node">The node the licence chose (<see cref="CosmosStatementScope.ContextFor"/>), frozen; read with
        /// <c>TryGet</c>, which interns nothing.</param>
        /// <param name="rules">The rules the node ran; <c>null</c> where the statement knows nothing.</param>
        /// <param name="declaration">The container's declaration, which says where a default loses precision.</param>
        /// <param name="paths">The paths the bundle's atoms read.</param>
        /// <exception cref="InvalidOperationException">The bundle has run, or holds another source's imports.</exception>
        /// <exception cref="FactConflictException">A path was imported with another value.</exception>
        public static ImportReport Import(RenderingBundle bundle, ImportSource source, Scope? node, PredicateRules? rules,
            JsonFactTheory? declaration, IEnumerable<JsonDocumentPath> paths)
        {
            // ContextFor hands back no context where the licence keeps nothing; with rules, something was known.
            if (rules is not null && node is null or { IsContradictory: true })
                return new ImportReport(0, 0, 0, "contradicted", true);

            var (open, constrained) = (0, 0);
            var spelled = new List<string>();
            foreach (var path in paths.Distinct().OrderBy(SqlValue.Spell, StringComparer.Ordinal))
            {
                var (values, chars) = (PathValues.Anything, JsonValues.AllBands);
                var term = node is null ? null : JsonFactEncoding.Path(path).ToTerm(rules!.Json);
                if (term is not null && node!.TryGet(rules!.Possible.Of(term), out var possible))
                {
                    values = possible;

                    // No chars row is every class, as the facts graph reads its absence.
                    if (node.TryGet(rules.Json.Chars.Of(term), out var held))
                        chars = held;
                }
                else if (declaration?.MentionedPaths.Any(m => path.IsWithin(m) || m.IsWithin(path)) == true)
                    constrained++;
                else
                    open++;

                bundle.Import(source, path, values, chars);
                spelled.Add($"{SqlValue.Spell(path)}={values} chars {string.Join(",", chars.AsEnumerable())}");
            }

            return new ImportReport(spelled.Count, open, constrained, string.Join("; ", spelled), false);
        }

    }

}
