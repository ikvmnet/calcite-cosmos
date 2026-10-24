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

    }

}
