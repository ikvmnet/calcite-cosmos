using System;
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
    /// <para>
    /// A string literal over any reader, over the path's raw value (<c>RenderingRexBuilder.RawValue</c>). A number only
    /// over a <c>RETURNING</c> of a number type and a boolean only over <c>RETURNING BOOLEAN</c>, each a literal of its
    /// accessor's own type, which Calcite reads as the value the service compares or raises on: a reader whose type test
    /// (<see cref="TypeTests"/>) is <c>IS_NUMBER</c> or <c>IS_BOOL</c>. Over a cast of the text, <c>RETURNING DECIMAL</c>
    /// and the text, which parse a string Calcite then compares where the service compares no string, a number only as a
    /// guarded pair (<see cref="Guarded"/>): beside <c>IS_NUMBER(p)</c> in an <c>AND</c>, or <c>NOT IS_NUMBER(p)</c> in an
    /// <c>OR</c>, written <c>CASE WHEN IS_NUMBER(raw) THEN … ELSE FALSE|TRUE END</c>, which raises on no string; over
    /// <c>RETURNING DECIMAL</c>, typed <c>DECIMAL(19, 0)</c>, a whole number alone (<c>Calcite.ReturningDecimalParsesTheRendering</c>).
    /// Each member is written through the reader it was judged over (<see cref="ServiceForm.Reader"/>). The type tests the
    /// translator writes, each with an in-process body (<c>CosmosTypeTestImplementor</c>), and the ternary as a
    /// <c>CASE</c>. The builder, <c>RenderingRexBuilder</c>, writes every row, held by <c>RenderingRexBuilderTests</c> over
    /// every shape S can hold.
    /// </para>
    /// </remarks>
    internal static class RenderingRexGrammar
    {

        /// <summary>Reader classes: every reader, one typed as a number, <c>RETURNING BOOLEAN</c>.</summary>
        public const string AnyReader = "any", NumberReader = "number", BooleanReader = "boolean";

        /// <summary>
        /// Each reader's type test, <c>T(p)</c>, as the translator writes it beside a reading (<c>TypeTestOf</c>): the text
        /// accessor's <c>IS_PRIMITIVE</c>, and a scalar <c>RETURNING</c>'s by its type. The templates guard by it, and a
        /// reader typed as a number or a boolean is its class here; a raw value, a cast and <c>RETURNING DECIMAL</c> have none.
        /// </summary>
        public static IReadOnlyDictionary<string, CosmosServiceTest> TypeTests { get; } = new Dictionary<string, CosmosServiceTest>(
            new[] { "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE" }.Select(t => KeyValuePair.Create(PredicateAtom.TypedReader(t), CosmosServiceTest.IsNumber)))
        {
            [PredicateAtom.TypedReader("BOOLEAN")] = CosmosServiceTest.IsBool,
            [PredicateAtom.TextReader] = CosmosServiceTest.IsPrimitive,
        };

        /// <summary>
        /// The readers a number is compared through only as a guarded pair, each with the literal types it holds: a cast of
        /// the text to an integer type or a double, <c>RETURNING DECIMAL</c> a whole number alone, and the text itself,
        /// through its cast to <c>DOUBLE</c> (<c>TemplateId.TextAlternatives</c>).
        /// </summary>
        public static ValueSet<string> Guarded { get; } = ValueSet.From(
            new[] { "INTEGER", "BIGINT", "DOUBLE" }.Select(t => PredicateAtom.CastReader(t, PredicateAtom.TextReader)).Append(PredicateAtom.TextReader)
                .SelectMany(r => new[] { r + "/" + JsonValues.IntegerType, r + "/" + JsonValues.FractionType })
                .Append(PredicateAtom.TypedReader("DECIMAL") + "/" + JsonValues.IntegerType));

        /// <summary>The rows as one value, which <c>rex-writable</c> reads as <c>sendable</c> reads the parting bands.</summary>
        public static Value Written { get; } = new(
            ValueSet.Of(AnyReader + "/" + JsonValues.StringType, NumberReader + "/" + JsonValues.IntegerType, NumberReader + "/" + JsonValues.FractionType, BooleanReader + "/" + JsonValues.BooleanType),
            ValueSet.Of(CosmosServiceTest.IsDefined, CosmosServiceTest.IsNull, CosmosServiceTest.IsPrimitive, CosmosServiceTest.IsString, CosmosServiceTest.IsNumber,
                CosmosServiceTest.IsBool, CosmosServiceTest.IsObject, CosmosServiceTest.IsArray), Guarded);

        /// <summary>
        /// The paths a junction guards: each <c>p</c> with <c>IS_NUMBER(p)</c> a member of an <c>AND</c>, or
        /// <c>NOT IS_NUMBER(p)</c> one of an <c>OR</c>, so that a comparison of <c>p</c> beside it is asked only where
        /// <c>p</c> holds a number: elsewhere the guard decides the junction.
        /// </summary>
        public static IReadOnlySet<string> GuardsOf(ServiceForm junction) => junction.Kind switch
        {
            ServiceFormKind.And => junction.Members.Where(IsNumberTest).Select(m => m.Path!).ToHashSet(StringComparer.Ordinal),
            ServiceFormKind.Or => junction.Members.Where(m => m.Kind == ServiceFormKind.Not && IsNumberTest(m.Members[0])).Select(m => m.Members[0].Path!).ToHashSet(StringComparer.Ordinal),
            _ => new HashSet<string>(),
        };

        static bool IsNumberTest(ServiceForm form) => form is { Kind: ServiceFormKind.Test, Test: CosmosServiceTest.IsNumber };

        /// <summary>A reader's class: one typed as a number, <c>RETURNING BOOLEAN</c>, or its own name.</summary>
        public static string ClassOf(string reader) => TypeTests.GetValueOrDefault(reader) switch
        {
            CosmosServiceTest.IsNumber => NumberReader,
            CosmosServiceTest.IsBool => BooleanReader,
            _ => reader,
        };

        /// <summary>
        /// The grammar's rows as a value: comparisons spelled <c>reader class/literal type</c>, the tests, and the guarded
        /// comparisons spelled <c>reader/literal type</c>.
        /// </summary>
        public sealed record Value(ValueSet<string> Comparisons, ValueSet<CosmosServiceTest> Tests, ValueSet<string> GuardedComparisons)
        {

            static readonly IReadOnlySet<string> Unguarded = new HashSet<string>();

            /// <summary>
            /// Whether the builder writes a form over a reader, each member over its own where it carries one: every
            /// comparison, membership and test in it, each guarded pair, and nothing else but constants, connectives and
            /// ternaries.
            /// </summary>
            public bool Writes(ServiceForm form, string reader) => Writes(form, reader, Unguarded);

            bool Writes(ServiceForm form, string reader, IReadOnlySet<string> guarded) => form.Kind switch
            {
                ServiceFormKind.Test => Tests.Contains(form.Test),
                ServiceFormKind.Compare or ServiceFormKind.In => form.Literals.All(l => Holds(AnyReader, l) || Holds(ClassOf(form.Reader ?? reader), l)
                    || form.Kind == ServiceFormKind.Compare && guarded.Contains(form.Path!) && IsGuarded(form.Reader ?? reader, l)),
                ServiceFormKind.Constant => true,
                ServiceFormKind.Not or ServiceFormKind.Choose => form.Members.All(m => Writes(m, reader, Unguarded)),
                ServiceFormKind.And or ServiceFormKind.Or => GuardsOf(form) is var guards && form.Members.All(m => Writes(m, reader, guards)),
                _ => false,
            };

            /// <summary>Whether a number is compared through a reader only as a guarded pair.</summary>
            public bool IsGuarded(string reader, string literal) => GuardedComparisons.Contains(reader + "/" + JsonValues.TypeOf(literal));

            bool Holds(string readerClass, string literal) => Comparisons.Contains(readerClass + "/" + JsonValues.TypeOf(literal));

        }

    }

}
