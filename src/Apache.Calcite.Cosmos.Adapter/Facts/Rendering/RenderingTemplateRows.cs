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
            (KindRestrictedNumber, RenderPosition.Neg), (Window, RenderPosition.Pos), (TextAlternatives, RenderPosition.Pos),
            (ComparePaths, RenderPosition.Pos), (ComparePaths, RenderPosition.Neg),
            .. Affixes.SelectMany(t => new[] { (t, RenderPosition.Pos), (t, RenderPosition.Neg) }), .. AffixesRendered.Select(t => (t, RenderPosition.Neg)),
            (StructureTest, RenderPosition.Pos), (StructureTest, RenderPosition.Neg),
        ];

        /// <summary>The templates of a <c>LIKE</c>, one a test that may say its pattern (B1).</summary>
        static TemplateId[] Affixes => [AffixEquality, AffixStartsWith, AffixEndsWith, AffixContains, AffixLike];

        /// <summary>The templates of a <c>LIKE</c> over a rendering, one a test as <see cref="Affixes"/>.</summary>
        static TemplateId[] AffixesRendered => [AffixRenderedEquality, AffixRenderedStartsWith, AffixRenderedEndsWith, AffixRenderedContains, AffixRenderedLike];

        /// <summary>
        /// The guarded comparison's templates, which production asks only from the switch (spec §4.2 h15): a bridge, since
        /// the Rex they are pushed as is a <c>CASE</c> only S reads, and site W asks the translator to write it until then.
        /// The affix's, since S owns no affix until the switch and the translator writes one till then (h07).
        /// </summary>
        public static IReadOnlySet<TemplateId> FromTheSwitch { get; } = new HashSet<TemplateId>([KindRestrictedNumber, Window, TextAlternatives, .. Affixes, .. AffixesRendered]);

        /// <summary>
        /// Each template and the shapes it fits. A <c>JSON_QUERY</c>'s reading is asked its own template alone, the form its
        /// row says it is (G8): every other template's proposes a value it has none of.
        /// </summary>
        public static IReadOnlyList<(TemplateId, string Shape)> Fits { get; } = [.. Rows().Distinct()];

        static IEnumerable<(TemplateId, string)> Rows()
        {
            string[] text = [PredicateAtom.TextReader], textual = [PredicateAtom.TextReader, PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)];
            string[] readers = [.. ReaderEdgesTable.Rows.Keys.Where(r => PredicateAtom.ColumnOf(r) is null && r != PredicateAtom.QueryReader), .. textual], strings = [StringKind, TruthKind, NumeralKind], literals = [.. strings, NumberKind, BooleanKind];
            string[] parsed = [.. new[] { "INTEGER", "BIGINT", "DOUBLE" }.Select(t => PredicateAtom.CastReader(t, PredicateAtom.TextReader)), PredicateAtom.TypedReader("DECIMAL")];
            string[] orderings = [PredicateAtom.Less, PredicateAtom.LessOrEqual, PredicateAtom.Greater, PredicateAtom.GreaterOrEqual];
            string[] comparisons = [PredicateAtom.Equal, PredicateAtom.NotEqual, .. orderings], equalOrOrdering = [PredicateAtom.Equal, .. orderings];

            IEnumerable<(TemplateId, string)> Fit(TemplateId id, IEnumerable<string> over, IEnumerable<string> tests, IEnumerable<string> kinds) =>
                from reader in over from test in tests from kind in kinds select (id, reader + "/" + test + "/" + kind);

            return Fit(Bare, readers, comparisons, literals).Concat(Fit(Bare, readers, [PredicateAtom.InTest], [PointsKind]))
                .Concat(Fit(Bare, readers, [PredicateAtom.DefinedTest, TypeTestShape], [NoOperand]))
                .Concat(Fit(Restricted, readers, [PredicateAtom.NotEqual], literals))
                .Concat(Fit(Completed, readers, equalOrOrdering, literals)).Concat(Fit(Completed, readers, [PredicateAtom.InTest], [PointsKind]))
                .Concat(Fit(NullTest, readers, [PredicateAtom.NullTest], [NoOperand]))
                .Concat(Fit(BooleanAlternatives, textual, [PredicateAtom.Equal], [TruthKind]))
                .Concat(Fit(ByKindBoolean, text, orderings, strings)).Concat(Fit(ByKindNumber, text, orderings, strings))
                .Concat(Fit(StringBranch, textual, [PredicateAtom.NotEqual, .. orderings], strings))
                .Concat(Fit(KindRestricted, textual, comparisons, strings))
                .Concat(Fit(KindRestrictedNumber, parsed, comparisons, [NumberKind])).Concat(Fit(Window, parsed, comparisons, [NumberKind]))
                .Concat(Fit(TextAlternatives, text, [PredicateAtom.Equal], [NumeralKind]))
                .Concat(Fit(ComparePaths, readers, comparisons, readers.Select(r => ReadingKind + r)))
                .Concat(Affixes.SelectMany(t => Fit(t, [.. textual, PredicateAtom.RawReader, PredicateAtom.TypedReader("VARCHAR")], [PredicateAtom.LikeTest], strings)))
                .Concat(AffixesRendered.SelectMany(t => Fit(t, textual, [PredicateAtom.LikeTest], strings)))
                .Concat(Fit(StructureTest, [PredicateAtom.QueryReader], [PredicateAtom.NullTest], [NoOperand]));
        }

    }

}
