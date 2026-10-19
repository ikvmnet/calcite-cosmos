using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A schema's conditional statement: where every fact of <see cref="When"/> is known, every fact of
    /// <see cref="Then"/> holds. A <c>oneOf</c> branch under its discriminator, an <c>if</c>/<c>then</c>.
    /// </summary>
    /// <remarks>
    /// A body fact is known the way the theory knows any fact: a domain within the set, types within the set, the
    /// path present or absent, a disequality stated or proven by a domain. So a body fact is a statement about the
    /// document like any other, and the rule is true of a document where its body is false of it or its head true.
    /// </remarks>
    internal sealed record JsonGuardedRule(IReadOnlyList<JsonFact> When, IReadOnlyList<JsonFact> Then)
    {

        public override string ToString() => $"if {string.Join(" and ", When)} then {string.Join(" and ", Then)}";

    }

}
