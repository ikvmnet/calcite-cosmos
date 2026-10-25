using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where each reader's answer changes, as host data: the <c>edges</c> rows S's <c>partition</c> rule applies. A reader
    /// with no row is cut nowhere, so an atom read by it is never evaluated and stays unproven.
    /// </summary>
    /// <remarks>
    /// Text and the cast of a raw value to text answer at the literals alone, the last as each of its columns does
    /// (<c>Calcite.CastOfARawValueToText</c>); raw at a number's double too, which the box of a number not written as a
    /// long compares (<c>Calcite.RawNullIsSqlNull</c>). An integer type raises past its limits, and a cast to one
    /// truncates within one of a literal and saturates at them. A double compares doubles, and so does the service a
    /// number it holds as one, an integer type's column a number not written as an integer (<c>CosmosJson.GetInteger</c>),
    /// and <c>DECIMAL</c> one it renders; and a parse of the rendering reads the literal's text as the literal. A numeric
    /// cast of the raw value has no row, since no <c>Calcite.*</c> row measures one (<c>TODO.md</c>): over a
    /// <c>VARIANT</c> Calcite raises on a number beyond the type, and over an <c>ANY</c> wraps a long beyond an int.
    /// </remarks>
    internal static class ReaderEdgesTable
    {

        static ReaderEdges Row(params CutKind[] edges) => new(ValueSet.From(edges), ValueSet<string>.Empty);

        static ReaderEdges Limits(string type, params CutKind[] edges) =>
            PredicateAtom.IntegerLimits(type) is var (least, most) ? new(ValueSet.From(edges), ValueSet.Of(JsonValues.Of(least), JsonValues.Of(most))) : Row(edges);

        public static IReadOnlyDictionary<string, ReaderEdges> Rows { get; } = new Dictionary<string, ReaderEdges>
        {
            [PredicateAtom.TextReader] = Row(),
            [PredicateAtom.RawReader] = Row(CutKind.Double),
            [PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)] = Row(),
            [PredicateAtom.TypedReader("BOOLEAN")] = Row(),
            [PredicateAtom.TypedReader("TINYINT")] = Limits("TINYINT", CutKind.Double),
            [PredicateAtom.TypedReader("SMALLINT")] = Limits("SMALLINT", CutKind.Double),
            [PredicateAtom.TypedReader("INTEGER")] = Limits("INTEGER", CutKind.Double),
            [PredicateAtom.TypedReader("BIGINT")] = Limits("BIGINT", CutKind.Double),
            [PredicateAtom.TypedReader("FLOAT")] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("REAL")] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("DOUBLE")] = Row(CutKind.Double),
            [PredicateAtom.TypedReader("DECIMAL")] = Row(CutKind.Double, CutKind.Rendering),
            [PredicateAtom.CastReader("INTEGER", PredicateAtom.TextReader)] = Limits("INTEGER", CutKind.Rendering),
            [PredicateAtom.CastReader("BIGINT", PredicateAtom.TextReader)] = Limits("BIGINT", CutKind.Rendering),
            [PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader)] = Row(CutKind.Double, CutKind.Rendering),
        };

    }

}
