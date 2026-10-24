using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Rendering.RenderingTemplates;
using static Apache.Calcite.Cosmos.Facts.Rendering.TemplateId;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Which forms generate mode proposes, as host data: the <c>template</c> rows, a template and a position it is asked
    /// at, and the <c>fits</c> rows, a template and an atom's shape (<see cref="RenderingTemplates.ShapeOf"/>) it is asked
    /// of. A template only proposes and the gate disposes, so a row asks a question and decides nothing. No row names a
    /// parameter's shape, nor a reader with no <c>edges</c> row but the text cast of a raw value.
    /// </summary>
    internal static class RenderingTemplateRows
    {

        /// <summary>
        /// Each template and the positions it is asked at (spec §4.1). A6 is asked by each kind at both positions, so which
        /// kind a text ordering's literal leaves on the kept side is the gate's.
        /// </summary>
        public static IReadOnlyList<(TemplateId, RenderPosition)> Templates { get; } =
        [
            (Bare, RenderPosition.Pos), (Bare, RenderPosition.Neg), (Restricted, RenderPosition.Pos), (Completed, RenderPosition.Neg),
            (NullTest, RenderPosition.Pos), (NullTest, RenderPosition.Neg), (BooleanAlternatives, RenderPosition.Pos), (BooleanAlternatives, RenderPosition.Neg),
            (ByKindBoolean, RenderPosition.Pos), (ByKindBoolean, RenderPosition.Neg), (ByKindNumber, RenderPosition.Pos), (ByKindNumber, RenderPosition.Neg),
            (StringBranch, RenderPosition.Pos), (KindRestricted, RenderPosition.Neg),
        ];

        /// <summary>Each template and the shapes it fits.</summary>
        public static IReadOnlyList<(TemplateId, string Shape)> Fits { get; } = [.. Rows().Distinct()];

        static IEnumerable<(TemplateId, string)> Rows()
        {
            string[] text = [PredicateAtom.TextReader], textual = [PredicateAtom.TextReader, PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)];
            string[] readers = [.. ReaderEdgesTable.Rows.Keys, .. textual], strings = [StringKind, TruthKind], literals = [.. strings, NumberKind, BooleanKind];
            string[] orderings = [PredicateAtom.Less, PredicateAtom.LessOrEqual, PredicateAtom.Greater, PredicateAtom.GreaterOrEqual];
            string[] comparisons = [PredicateAtom.Equal, PredicateAtom.NotEqual, .. orderings], equalOrOrdering = [PredicateAtom.Equal, .. orderings];

            IEnumerable<(TemplateId, string)> Fit(TemplateId id, IEnumerable<string> over, IEnumerable<string> tests, IEnumerable<string> kinds) =>
                from reader in over from test in tests from kind in kinds select (id, reader + "/" + test + "/" + kind);

            return Fit(Bare, readers, comparisons, literals).Concat(Fit(Bare, readers, [PredicateAtom.InTest], [PointsKind]))
                .Concat(Fit(Bare, readers, [PredicateAtom.DefinedTest], [NoOperand]))
                .Concat(Fit(Restricted, readers, [PredicateAtom.NotEqual], literals))
                .Concat(Fit(Completed, readers, equalOrOrdering, literals)).Concat(Fit(Completed, readers, [PredicateAtom.InTest], [PointsKind]))
                .Concat(Fit(NullTest, readers, [PredicateAtom.NullTest], [NoOperand]))
                .Concat(Fit(BooleanAlternatives, textual, [PredicateAtom.Equal], [TruthKind]))
                .Concat(Fit(ByKindBoolean, text, orderings, strings)).Concat(Fit(ByKindNumber, text, orderings, strings))
                .Concat(Fit(StringBranch, textual, [PredicateAtom.NotEqual, .. orderings], strings))
                .Concat(Fit(KindRestricted, textual, comparisons, strings));
        }

    }

}
