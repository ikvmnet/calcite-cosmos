using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the Rex builder writes over a conjunct's own accessor, as rows: which literals a comparison or an <c>IN</c> may
    /// hold over which class of reader, and which tests; and constants, <c>NOT</c>, <c>AND</c> and <c>OR</c> over what it
    /// writes. S admits into <c>implied</c> only what the rows write (<c>rex-writable</c>), so the builder never meets a
    /// member it would have to leave out, which beneath an <c>OR</c> or a <c>NOT</c> would strengthen a weakening.
    /// </summary>
    /// <remarks>
    /// A string literal over any reader, the accessor re-typed <c>ANY</c> as the split rule's <c>RawValue</c> does; a number
    /// only over a reader typed as one, a <c>RETURNING</c> or a cast, and a boolean only over <c>RETURNING BOOLEAN</c>, each
    /// a literal of its accessor's own type; the type tests the translator writes, each with an in-process body
    /// (<c>CosmosTypeTestImplementor</c>). Stated before its builder (spec g14), which is held to every row.
    /// </remarks>
    internal static class RenderingRexGrammar
    {

        /// <summary>Reader classes: every reader, one typed as a number, <c>RETURNING BOOLEAN</c>.</summary>
        public const string AnyReader = "any", NumberReader = "number", BooleanReader = "boolean";

        static readonly string[] Numbers = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE", "DECIMAL"];

        /// <summary>Which literals a comparison or an <c>IN</c> may hold over which class of reader, by the literal's JSON type.</summary>
        public static IReadOnlyList<(string ReaderClass, string LiteralClass)> Comparisons { get; } =
        [
            (AnyReader, JsonValues.StringType),
            (NumberReader, JsonValues.IntegerType),
            (NumberReader, JsonValues.FractionType),
            (BooleanReader, JsonValues.BooleanType),
        ];

        /// <summary>The tests the builder writes.</summary>
        public static IReadOnlyList<CosmosServiceTest> Tests { get; } =
        [
            CosmosServiceTest.IsDefined, CosmosServiceTest.IsNull, CosmosServiceTest.IsPrimitive, CosmosServiceTest.IsString,
            CosmosServiceTest.IsNumber, CosmosServiceTest.IsBool, CosmosServiceTest.IsObject, CosmosServiceTest.IsArray,
        ];

        /// <summary>The rows as one value, which <c>rex-writable</c> reads as <c>sendable</c> reads the parting bands.</summary>
        public static Value Written { get; } = new(ValueSet.From(Comparisons.Select(c => c.ReaderClass + "/" + c.LiteralClass)), ValueSet.From(Tests));

        /// <summary>A reader's class: one typed as a number, <c>RETURNING BOOLEAN</c>, or its own name.</summary>
        public static string ClassOf(string reader) => reader.Split(':') switch
        {
            ["typed" or "cast", var type, ..] when Numbers.Contains(type) => NumberReader,
            ["typed", "BOOLEAN"] => BooleanReader,
            _ => reader,
        };

        /// <summary>The grammar's rows as a value: comparisons spelled <c>reader class/literal type</c>, and the tests.</summary>
        public sealed record Value(ValueSet<string> Comparisons, ValueSet<CosmosServiceTest> Tests)
        {

            /// <summary>Whether the builder writes a form over a reader: every comparison, membership and test in it, and nothing else but constants and connectives.</summary>
            public bool Writes(ServiceForm form, string reader) => form.Kind switch
            {
                ServiceFormKind.Test => Tests.Contains(form.Test),
                ServiceFormKind.Compare or ServiceFormKind.In => form.Literals.All(l => Holds(AnyReader, l) || Holds(ClassOf(reader), l)),
                ServiceFormKind.Constant => true,
                ServiceFormKind.Not or ServiceFormKind.And or ServiceFormKind.Or => form.Members.All(m => Writes(m, reader)),
                _ => false,
            };

            bool Holds(string readerClass, string literal) => Comparisons.Contains(readerClass + "/" + JsonValues.TypeOf(literal));

        }

    }

}
