using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    using NodeReadings = ConditionalWeakTable<Scope, ConcurrentDictionary<(PredicateRules Rules, JsonDocumentPath Path, bool Chars), (PathValues? Values, ValueSet<string>? Chars, string Spelled)>>;

    /// <summary>
    /// Reads what the facts graph's licensed node says each path may hold into a bundle: one value per path per table,
    /// copied, never met with another (<c>DESIGN.md</c>, <i>The rendering graph in check mode</i>).
    /// </summary>
    internal static class RenderingImporter
    {

        /// <summary>
        /// What a frozen node holds of a path, or a stratum of an atom's path, read and spelled; once a node where the
        /// asker keeps <paramref name="readings"/> (spec §6.1's lever), as a schema's oracle does
        /// (<see cref="RenderingOracle.Readings"/>), and afresh where it keeps none. A node never changes, so either
        /// spells alike.
        /// </summary>
        static (PathValues? Values, ValueSet<string>? Chars, string Spelled) ReadOf(Scope node, PredicateRules rules, JsonDocumentPath path, bool chars,
            NodeReadings? readings)
        {
            (PathValues? Values, ValueSet<string>? Chars, string Spelled) Read((PredicateRules Rules, JsonDocumentPath Path, bool Chars) key)
            {
                var term = JsonFactEncoding.Path(key.Path).ToTerm(key.Rules.Json);
                var values = node.TryGet(key.Rules.Possible.Of(term), out var possible) ? possible : null;
                var held = key.Chars && node.TryGet(key.Rules.Json.Chars.Of(term), out var c) ? c : null;
                return (values, held, Spell(key.Path, values ?? PathValues.Anything, key.Chars ? held ?? JsonValues.AllBands : ValueSet<string>.Empty));
            }

            return readings is null ? Read((rules, path, chars)) : readings.GetValue(node, _ => new()).GetOrAdd((rules, path, chars), Read);
        }

        /// <summary>
        /// Copies the node's <c>possible</c> and <c>chars</c> rows for each path into the bundle, before its run, each table
        /// read on its own. A path the node holds no <c>possible</c> row for gets the top (<see cref="PathValues.Anything"/>)
        /// and is counted: open where the declaration mentions nothing at, within or above it, constrained otherwise; a
        /// path with no <c>chars</c> row gets every class (<see cref="JsonValues.AllBands"/>), as the facts graph reads its
        /// absence. A node that keeps no document imports nothing. Beside an atom <paramref name="beneath"/> names, what its
        /// path may hold where its conjunct was rendered, a stratum beneath this node, read by that atom's cells alone: no
        /// fact of it is met with the import's, and the cells it admits nothing of are set aside
        /// (<see cref="RenderingBundle.Assume"/>). A later round's computed atom has every path its call reads stated so, with
        /// its characters, a stratum of the round's own that S gathers the call's operands beneath
        /// (<see cref="RenderingBundle.AssumeComputed"/>, G6). An arm's atom, one its source names the conditions of
        /// (<see cref="ImportSource.Arm"/>), is stated beneath the stratum the bundle opened for them, tagged with it, and
        /// spelled by its conditions, which no bundle's numbering changes (h15).
        /// </summary>
        /// <param name="bundle">The bundle, not yet run; <c>null</c> for the report alone.</param>
        /// <param name="source">The node's statement, licence and round.</param>
        /// <param name="node">The node the licence chose (<see cref="CosmosStatementScope.ContextFor"/>), frozen; read with
        /// <c>TryGet</c>, which interns nothing.</param>
        /// <param name="rules">The rules the node ran; <c>null</c> where the statement knows nothing.</param>
        /// <param name="declaration">The container's declaration, which says where a default loses precision.</param>
        /// <param name="paths">The paths the bundle's atoms read.</param>
        /// <param name="beneath">Each atom read where its conjunct was rendered: that stratum's source, node and rules.</param>
        /// <param name="readings">What each node was read to hold, kept by the asker; <c>null</c> to read afresh.</param>
        /// <exception cref="InvalidOperationException">The bundle has run, or holds another node's imports.</exception>
        /// <exception cref="FactConflictException">A path was imported with another value.</exception>
        public static ImportReport Import(RenderingBundle? bundle, ImportSource source, Scope? node, PredicateRules? rules,
            JsonFactTheory? declaration, IEnumerable<JsonDocumentPath> paths, IEnumerable<(SqlPredicate Atom, ImportSource Source, Scope? Node, PredicateRules? Rules)>? beneath = null,
            NodeReadings? readings = null, IEnumerable<SqlValue.Call>? computed = null)
        {
            // ContextFor hands back no context where the licence keeps nothing; with rules, something was known.
            if (rules is not null && node is null or { IsContradictory: true })
                return new ImportReport(0, 0, ValueSet<string>.Empty, "contradicted", true);

            var (open, constrained, spelled) = (0, new List<string>(), new List<string>());
            foreach (var path in paths.Distinct().OrderBy(SqlValue.Spell, StringComparer.Ordinal))
            {
                var (values, chars, spelling) = node is not null && rules is not null ? ReadOf(node, rules, path, true, readings) : (null, null, Spell(path, PathValues.Anything, JsonValues.AllBands));
                if (values is null && declaration?.MentionedPaths.Any(m => path.IsWithin(m) || m.IsWithin(path)) == true)
                    constrained.Add(SqlValue.Spell(path));
                else if (values is null)
                    open++;

                bundle?.Import(source with { Node = node }, path, values ?? PathValues.Anything, chars ?? JsonValues.AllBands);
                spelled.Add(spelling);
            }

            var (count, stated) = (spelled.Count, new List<(SqlPredicate, PathValues)>());

            // A computed place's characters, F's for the call at the root (h10), spelled into the key with its spelling.
            foreach (var call in (computed ?? bundle?.ComputedCalls ?? []).Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled, StringComparer.Ordinal).OrderBy(c => c.Spelled, StringComparer.Ordinal))
            {
                var chars = (node is not null && rules is not null ? ValueCharsOf(node, rules, call) : null) ?? JsonValues.AllBands;
                bundle?.ImportComputed(call, chars);
                spelled.Add(JsonSerializer.Serialize<object?[]>([call.Spelled, call.Type, chars.AsEnumerable()]));
            }
            foreach (var (atom, origin, stratum, read) in beneath ?? [])
                if (origin.Arm is { } conditions)
                {
                    // An arm's leaf of one path, as its node holds the path; one over a computed place is read where its case is.
                    if (RenderingBundle.IsComputed(atom) || atom.Places.ToList() is not [var armPath] || stratum is not { IsContradictory: false } || read is null
                        || ReadOf(stratum, read, armPath, false, readings) is not { Values: { } armed } armIt)
                        continue;

                    var arm = bundle?.ArmOf(conditions);
                    if (arm is { } opened)
                        bundle!.Assume(origin with { Node = stratum, Stratum = opened, Arm = null }, new SqlPredicate.Beneath(opened, atom), armed);

                    stated.Add((arm is { } tag ? new SqlPredicate.Beneath(tag, atom) : atom, armed));
                    spelled.Add(JsonSerializer.Serialize(atom.ToString()) + " beneath " + JsonSerializer.Serialize(RenderingBundle.KeyOf(conditions)) + " " + armIt.Spelled);
                }
                else if (RenderingBundle.IsComputed(atom) && atom.Computed.Any() && origin.Stratum is null && atom is not SqlPredicate.Beneath)
                {
                    if (stratum is not { IsContradictory: false } || read is null)
                        continue;

                    // Each path the call reads as the round's node holds it, the root's import of a table it holds no row of.
                    var operands = atom.Places.OrderBy(SqlValue.Spell, StringComparer.Ordinal).Select(p => (Path: p, Read: ReadOf(stratum, read, p, true, readings))).ToList();
                    bundle?.AssumeComputed(origin with { Node = stratum }, atom, [.. operands.Select(o => (o.Path, o.Read.Values, o.Read.Chars))]);
                    spelled.Add(JsonSerializer.Serialize(atom.ToString()) + " beneath [" + string.Join(",", operands.Select(o => o.Read.Spelled)) + "]");
                }
                else if (atom.Places.ToList() is [var path] && stratum is { IsContradictory: false } && read is not null && ReadOf(stratum, read, path, false, readings) is { Values: { } narrower } beneathIt)
                {
                    bundle?.Assume(origin with { Node = stratum }, atom, narrower);
                    stated.Add((atom, narrower));
                    spelled.Add(JsonSerializer.Serialize(atom.ToString()) + " beneath " + beneathIt.Spelled);
                }

            return new ImportReport(count, open, ValueSet.From(constrained), "[" + string.Join(",", spelled) + "]", false, stated);
        }

        /// <summary>One path's import as JSON, every value quoted, so that two imports that differ never spell alike.</summary>
        /// <summary>
        /// F's characters for a value at a node, asked as <c>JsonScopeTables</c> asks them: a child mentioning the paths it
        /// reads, the value's term made and the character rules run; <c>null</c> where that does not complete.
        /// </summary>
        static ValueSet<string>? ValueCharsOf(Scope node, PredicateRules rules, SqlValue value)
        {
            if (node.IsContradictory)
                return null;

            using var child = node.CreateChild();
            JsonContexts.Mention(rules.Json, child, value.PathsRead.Distinct().Select(JsonFactEncoding.Path));
            var term = rules.Term(value);
            child.Make(term);
            var report = child.Run(rules.CharacterSaturation, new RunOptions { StopOnContradiction = true });
            if (report.StopReason != RunStopReason.Completed || child.IsContradictory)
                return null;

            return child.TryGet(rules.ValueChars.Of(term), out ValueSet<string> held) ? held : null;
        }

        internal static string Spell(JsonDocumentPath path, PathValues values, ValueSet<string> chars) => JsonSerializer.Serialize<object?[]>(
        [
            SqlValue.Spell(path), values.Kinds.AsEnumerable(), values.Values?.AsEnumerable(), values.Excluded.AsEnumerable(),
            values.Lower?.Value, values.Lower?.Open, values.Upper?.Value, values.Upper?.Open, chars.AsEnumerable(),
        ]);

    }

}
