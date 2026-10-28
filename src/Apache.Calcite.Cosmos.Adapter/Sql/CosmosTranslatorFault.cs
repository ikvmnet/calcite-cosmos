using System;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A historical translator fault the rendering check must catch, planted again by <see cref="CosmosTranslatorFaults"/>
    /// at the writer's own site, never in a recogniser the loader or the facts also read (<c>DESIGN.md</c>, <i>The
    /// rendering graph in check mode</i>). Each is named by the commit that fixed it.
    /// </summary>
    [Flags]
    internal enum CosmosTranslatorFault
    {

        None = 0,

        /// <summary><c>WriteIsNull</c> ignores the reading's tests, testing the path's null set (fb4b93e).</summary>
        M1 = 1 << 0,

        /// <summary>A negated comparison drops <c>OR NOT</c> the reading's tests (6904ecc).</summary>
        M2 = 1 << 1,

        /// <summary>The positive guard against a null is dropped, so <c>!=</c> keeps one (d045961).</summary>
        M3 = 1 << 2,

        /// <summary><c>NOT</c> leaves the position unflipped (d045961).</summary>
        M4 = 1 << 3,

        /// <summary>A connective writes its operands as though no <c>NOT</c> enclosed them (d045961, efbfe4d).</summary>
        M5 = 1 << 4,

        /// <summary>An equality over a bare accessor is taken against text another value renders as (ae310eb).</summary>
        M6 = 1 << 5,

        /// <summary>The cast to text is dropped whatever the text (1eebacf).</summary>
        M7 = 1 << 6,

        /// <summary>An inequality over a rendering is written across JSON types (26a10bb).</summary>
        M8 = 1 << 7,

        /// <summary>An ordering the service answers otherwise is written as proved (26a10bb, 2defc9e).</summary>
        M9 = 1 << 8,

        /// <summary><c>RETURNING DECIMAL</c> is written as the path (78e5939).</summary>
        M10 = 1 << 9,

        // M11 (1 << 10), a text equality's weakening that loses its number (8d57df0), was planted in the split
        // rule's own weakening, and went with it: S writes every weakening, and M11 is planted in its lift
        // (RenderingGenerateMutationTests).

        /// <summary>Control: a <c>CASE</c> condition written in the <c>CASE</c>'s position, which the loader does not read.</summary>
        X1 = 1 << 11,

        /// <summary>Control: a typed inequality's type test dropped (0d7bb56), where Calcite raises.</summary>
        X2 = 1 << 12,

    }

}
