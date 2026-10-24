using System;
using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a generate bundle's answers depend on, so that two questions with one key share one bundle: the roots' digests
    /// with the value each opaque term was read as, the import spelled whole with every stratum stated beneath it, the set,
    /// and the frozen theory's rows hashed. No form and no question: in generate mode the form is the answer, and one
    /// bundle answers admission and the weakening alike.
    /// </summary>
    /// <remarks>
    /// A parameter is spelled by its ordinal and type, never its value; a literal is spelled, so a new literal is a new
    /// key. Why each part is in it: <c>DESIGN.md</c>, <i>The production oracle</i>.
    /// </remarks>
    internal sealed record RenderingQuestionKey(string Roots, string Import, RenderingSet Set, string Theory)
    {

        /// <summary>
        /// The key of the roots read beside their opaque terms' readings, the import's report and the set, under a theory's
        /// digest: the roots sorted ordinally, since a junction root's digest already orders its arms, and each opaque term
        /// beside what the bundle writes for it, its number naming it only within one loading.
        /// </summary>
        public static RenderingQuestionKey Of(IEnumerable<SqlPredicate> roots, IReadOnlyDictionary<long, SqlValue>? opaques, ImportReport import, RenderingSet set, string theory)
        {
            var all = roots.ToList();
            var read = all.SelectMany(r => r.Subterms).OfType<SqlPredicate.Opaque>().Select(o => o.Id).Distinct().Order()
                .Select(id => "opaque#" + id + " reads " + (opaques?.GetValueOrDefault(id)?.ToString() ?? "nothing"));
            return new(string.Join("\n", all.Select(r => r.Digest).Distinct().Order(StringComparer.Ordinal).Concat(read)), import.Digest, set, theory);
        }

    }

}
