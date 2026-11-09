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
    /// translator writes, each with an in-process body (<c>CosmosTypeTestImplementor</c>), over any reading of a path, the
    /// builder writing it over the path's accessor the body takes apart, the document column's where the conjunct reads the
    /// path through a field alone (h15b), and over no call (<see cref="Value.Raising"/>); no ordering over a reading that has no order (<see cref="Value.Unordered"/>),
    /// and the ternary as a <c>CASE</c>. The builder,
    /// <c>RenderingRexBuilder</c>, writes every row, held by <c>RenderingRexBuilderTests</c> over every shape S can hold.
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

        /// <summary>
        /// The readings no test is written over: those a type test's in-process body raises over whatever they hold, keyed as
        /// the test-reads row keys them (<c>Calcite.TypeTestsAreTheirBodies</c>, which <c>RenderingTemplatesTests</c> holds
        /// these to), that have no accessor the body takes apart, a call's (h15): where the filter beneath declined it, the
        /// body would fail every row in process, which no weakening may. A raw value in a column, a field's, has one, the
        /// path's accessor over the document column re-typed <c>ANY</c>, which the builder writes a test over (h15b), and
        /// pushes none of where the input holds no document column.
        /// </summary>
        public static ValueSet<string> Raising { get; } = ValueSet.Of(PredicateAtom.CallReaders);

        /// <summary>
        /// The readings no ordering is written over: a raw value in a <c>VARIANT</c> column, which has no order, so that an
        /// ordering of one fails to compile whatever the rows hold (the raw column's rows, <c>Calcite.RawNullIsSqlNull</c>,
        /// which <c>RenderingTemplatesTests</c> holds this to). A weakening is kept in process where the filter beneath
        /// declines it, so it may hold nothing that fails where its conjunct does not (h15); an ordering a member was judged
        /// over another reading of the path is written over the raw value of the path's text, or where the conjunct reads
        /// none, through its own cast of the field to a character type (<c>RenderingRexBuilder</c>).
        /// </summary>
        public static ValueSet<string> Unordered { get; } = ValueSet.Of(PredicateAtom.RawIn("VARIANT"));

        /// <summary>The rows as one value, which <c>rex-writable</c> reads as <c>sendable</c> reads the parting bands.</summary>
        public static Value Written { get; } = new(
            ValueSet.Of(AnyReader + "/" + JsonValues.StringType, NumberReader + "/" + JsonValues.IntegerType, NumberReader + "/" + JsonValues.FractionType, BooleanReader + "/" + JsonValues.BooleanType),
            ValueSet.Of(CosmosServiceTest.IsDefined, CosmosServiceTest.IsNull, CosmosServiceTest.IsPrimitive, CosmosServiceTest.IsString, CosmosServiceTest.IsNumber,
                CosmosServiceTest.IsBool, CosmosServiceTest.IsObject, CosmosServiceTest.IsArray), Guarded, Raising, Unordered);

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
        /// The grammar's rows as a value: comparisons spelled <c>reader class/literal type</c>, the tests, the guarded
        /// comparisons spelled <c>reader/literal type</c>, the readings no test is written over (<see cref="RenderingRexGrammar.Raising"/>),
        /// and those no ordering is (<see cref="RenderingRexGrammar.Unordered"/>).
        /// </summary>
        public sealed record Value(ValueSet<string> Comparisons, ValueSet<CosmosServiceTest> Tests, ValueSet<string> GuardedComparisons, ValueSet<string> Raising,
            ValueSet<string> Unordered)
        {

            static readonly IReadOnlySet<string> Unguarded = new HashSet<string>();

            /// <summary>
            /// Whether the builder writes a form over a reader, each member over its own where it carries one: every
            /// comparison, membership and test in it, each guarded pair, and nothing else but constants, connectives and
            /// ternaries. A test is written over no call, which the body raises over and which has no accessor it takes apart.
            /// No ordering is written over a reading that has no order.
            /// </summary>
            public bool Writes(ServiceForm form, string reader) => Writes(form, reader, Unguarded);

            bool Writes(ServiceForm form, string reader, IReadOnlySet<string> guarded) => form.Kind switch
            {
                ServiceFormKind.Test => Tests.Contains(form.Test) && Raising.Contains(PredicateAtom.ColumnOf(form.Reader ?? reader) is { } column ? PredicateAtom.RawIn(column) : form.Reader ?? reader) == false,
                ServiceFormKind.Compare when form.Test is CosmosServiceTest.Less or CosmosServiceTest.LessOrEqual or CosmosServiceTest.Greater or CosmosServiceTest.GreaterOrEqual
                    && Unordered.Contains(form.Reader ?? reader) => false,
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
