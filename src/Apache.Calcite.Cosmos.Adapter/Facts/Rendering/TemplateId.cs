namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A template generate mode proposes forms by: a core, the comparison, and a guard (<c>DESIGN.md</c>, <i>The rendering
    /// graph in generate mode</i>). Which positions each is asked at and which atoms it fits are host rows
    /// (<see cref="RenderingTemplateRows"/>); its form is <see cref="RenderingTemplates.Spell"/>'s; the gate disposes.
    /// </summary>
    /// <remarks>
    /// <c>T(p)</c> is the reading's type test: <c>IS_PRIMITIVE</c> for text, <c>IS_NUMBER</c> or <c>IS_BOOL</c> by
    /// <c>RETURNING</c>'s type, none for a raw value or a cast (<see cref="RenderingRexGrammar.TypeTests"/>).
    /// </remarks>
    internal enum TemplateId
    {

        /// <summary>A1: the comparison as it stands, an <c>IN</c> as its equalities, or <c>IS_DEFINED(p)</c>.</summary>
        Bare,

        /// <summary>A2, at <c>Pos</c>: an inequality where the path holds a value, not null, of the reading's type.</summary>
        Restricted,

        /// <summary>A3, at <c>Neg</c>: an equality, an ordering or each point of an <c>IN</c>, or the path absent, null or of another type.</summary>
        Completed,

        /// <summary>A4: <c>IS NULL</c> as the path absent, null or of another type, and beneath a <c>NOT</c> its negation.</summary>
        NullTest,

        /// <summary>A5: text equal to <c>'true'</c> or <c>'false'</c> as the string or the boolean it renders.</summary>
        BooleanAlternatives,

        /// <summary>A6 by the booleans: a text ordering or the path a boolean at <c>Pos</c>, and not one at <c>Neg</c>.</summary>
        ByKindBoolean,

        /// <summary>A6 by the numbers: a text ordering or the path a number at <c>Pos</c>, and not one at <c>Neg</c>.</summary>
        ByKindNumber,

        /// <summary>C1, at <c>Pos</c>: a text comparison where the path holds a string, anything else admitted.</summary>
        StringBranch,

        /// <summary>C4, at <c>Neg</c>: a text comparison where the path holds a string.</summary>
        KindRestricted,

        /// <summary>C4n, at <c>Neg</c>: a number compared through a cast of the text or <c>RETURNING DECIMAL</c>, where the path holds a number.</summary>
        KindRestrictedNumber,

        /// <summary>C2, at <c>Pos</c>: the same comparison where the path holds a number, anything else present admitted.</summary>
        Window,

        /// <summary>At <c>Pos</c>: text equal to a string spelling a number as the string, or the number it spells.</summary>
        TextAlternatives,

        /// <summary>At both: a comparison of two readings as the comparison of their two paths (h11).</summary>
        ComparePaths,
        /// <summary>
        /// At both: <c>LIKE</c> as the equality of its text, where its pattern holds literal units alone (h07). Each test that
        /// may say a pattern is a template of its own (B1), so the gate holds each and S's best chooses among those it holds
        /// by their price (<see cref="Priced"/>).
        /// </summary>
        AffixEquality,
        /// <summary>At both: <c>LIKE</c> as <c>STARTSWITH</c> of its one run, where the run of literal units is followed by <c>%</c> alone.</summary>
        AffixStartsWith,
        /// <summary>At both: <c>LIKE</c> as <c>ENDSWITH</c> of its one run, where the run of literal units follows <c>%</c> alone.</summary>
        AffixEndsWith,
        /// <summary>At both: <c>LIKE</c> as <c>CONTAINS</c> of its one run, where the run of literal units lies between two <c>%</c>.</summary>
        AffixContains,
        /// <summary>At both: <c>LIKE</c> as itself, of the pattern as written, whatever runs it holds.</summary>
        AffixLike,
        /// <summary>At <c>Neg</c>: <see cref="AffixEquality"/> over a rendering, where the path holds neither a number nor a boolean, which it misses (h07).</summary>
        AffixRenderedEquality,
        /// <summary>At <c>Neg</c>: <see cref="AffixStartsWith"/> over a rendering, as <see cref="AffixRenderedEquality"/>.</summary>
        AffixRenderedStartsWith,
        /// <summary>At <c>Neg</c>: <see cref="AffixEndsWith"/> over a rendering, as <see cref="AffixRenderedEquality"/>.</summary>
        AffixRenderedEndsWith,
        /// <summary>At <c>Neg</c>: <see cref="AffixContains"/> over a rendering, as <see cref="AffixRenderedEquality"/>.</summary>
        AffixRenderedContains,
        /// <summary>At <c>Neg</c>: <see cref="AffixLike"/> over a rendering, as <see cref="AffixRenderedEquality"/>.</summary>
        AffixRenderedLike,
        /// <summary>
        /// At both: a <c>JSON_QUERY</c>'s <c>IS NULL</c> as the path holding no structure, <c>NOT (IS_ARRAY(p) OR IS_OBJECT(p))</c>,
        /// which its row says it answers null over (<c>Calcite.JsonQueryAnswersStructureOnly</c>, G8).
        /// </summary>
        StructureTest,

    }

}
