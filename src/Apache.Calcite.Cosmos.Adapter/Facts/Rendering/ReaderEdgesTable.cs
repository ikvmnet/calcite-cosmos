using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where each reader's answer changes, as host data: the <c>edges</c> rows S's <c>partition</c> rule applies. A reader
    /// with no row is cut nowhere, so an atom read by it is never evaluated and stays unproven. A cast's keys are the casts
    /// a row measured (<c>measuredcast</c>, h15b), its edges its target type's.
    /// </summary>
    /// <remarks>
    /// Text and the cast of a raw value to text answer at the literals alone, the last as each of its columns does
    /// (<c>Calcite.CastOfARawValueToText</c>); raw at a number's double too, which the box of a number not written as a
    /// long compares (<c>Calcite.RawNullIsSqlNull</c>). An integer type raises past its limits, and a cast to one
    /// truncates within one of a literal and saturates at them. A double compares doubles, and so does the service a
    /// number it holds as one, an integer type's column a number not written as an integer (<c>CosmosJson.GetInteger</c>),
    /// and <c>DECIMAL</c> one it renders; and a parse of the rendering reads the literal's text as the literal. A numeric
    /// cast of the raw value has no key, since no <c>Calcite.*</c> row measures one: over a <c>VARIANT</c> Calcite raises
    /// on a number beyond the type, and over an <c>ANY</c> wraps a long beyond an int.
    /// </remarks>
    internal static class ReaderEdgesTable
    {

        static ReaderEdges Row(params CutKind[] edges) => new(ValueSet.From(edges), ValueSet<string>.Empty);

        static ReaderEdges Limits(string type, params CutKind[] edges) =>
            PredicateAtom.IntegerLimits(type) is var (least, most) ? new(ValueSet.From(edges), ValueSet.Of(JsonValues.Of(least), JsonValues.Of(most))) : Row(edges);

        /// <summary>Each reading's edges but a cast's, in no column.</summary>
        public static IReadOnlyDictionary<string, ReaderEdges> Readings { get; } = new Dictionary<string, ReaderEdges>
        {
            [PredicateAtom.TextReader] = Row(),
            [PredicateAtom.RawReader] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("BOOLEAN")] = Row(),
            // A computed text place's (h10), read as text.
            [PredicateAtom.TypedReader("VARCHAR")] = Row(),
            [PredicateAtom.TypedReader("CHAR")] = Row(),
            // A JSON_QUERY's (G8): its answer changes with the class alone (Calcite.JsonQueryAnswersStructureOnly).
            [PredicateAtom.QueryReader] = Row(),
            [PredicateAtom.TypedReader("TINYINT")] = Limits("TINYINT", CutKind.Double),
            [PredicateAtom.TypedReader("SMALLINT")] = Limits("SMALLINT", CutKind.Double),
            [PredicateAtom.TypedReader("INTEGER")] = Limits("INTEGER", CutKind.Double),
            [PredicateAtom.TypedReader("BIGINT")] = Limits("BIGINT", CutKind.Double),
            [PredicateAtom.TypedReader("FLOAT")] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("REAL")] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("DOUBLE")] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("DECIMAL")] = Row(CutKind.Double, CutKind.Rendering),
        };

        /// <summary>
        /// A cast's edges by its target type, data per type: the cast to text's, the text's parse as a boolean, a boolean read
        /// as itself (<c>Calcite.CastToBooleanParses</c>), and a number's parse, an integer type's within its limits.
        /// </summary>
        public static IReadOnlyDictionary<string, ReaderEdges> CastTargets { get; } = new Dictionary<string, ReaderEdges>
        {
            ["VARCHAR"] = Row(),
            ["BOOLEAN"] = Row(),
            ["INTEGER"] = Limits("INTEGER", CutKind.Rendering),
            ["BIGINT"] = Limits("BIGINT", CutKind.Rendering),
            ["DOUBLE"] = Row(CutKind.Double, CutKind.Rendering),
        };

        /// <summary>
        /// Every reading's edges, a cast's for each cast a row measured (<c>measuredcast</c>) by its target type, and each in
        /// every column a raw reading is read through.
        /// </summary>
        public static IReadOnlyDictionary<string, ReaderEdges> Of(IEnumerable<(string Target, string Reader, string Row)> casts)
        {
            var rows = new Dictionary<string, ReaderEdges>(Readings);
            foreach (var (target, reader, _) in casts)
                if (CastTargets.TryGetValue(target, out var edges))
                    rows[PredicateAtom.CastReader(target, reader)] = edges;

            return InColumns(rows);
        }

        /// <summary>
        /// A raw reading read through one column (rawtyped) is cut where the union is: each column's answer changes there or
        /// nowhere, a column no row measured reading as the union (<see cref="PredicateAtom.UnionColumns"/>).
        /// </summary>
        static Dictionary<string, ReaderEdges> InColumns(Dictionary<string, ReaderEdges> rows)
        {
            foreach (var (reader, edges) in rows.Where(r => PredicateAtom.IsRaw(r.Key) || r.Key.EndsWith(":" + PredicateAtom.RawReader, System.StringComparison.Ordinal)).ToList())
                foreach (var column in PredicateAtom.Columns)
                    rows[PredicateAtom.InColumn(reader, column)] = edges;

            return rows;
        }

    }

}
