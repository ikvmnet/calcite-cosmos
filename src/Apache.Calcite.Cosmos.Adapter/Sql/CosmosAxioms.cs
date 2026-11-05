using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Class = Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;
using Column = Apache.Calcite.Cosmos.Adapter.Sql.CosmosRawColumn.Answer;
using Test = Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The measured facts about the service and about Calcite's runtime that a pushdown relies on,
    /// one row each, with where and when each was measured and the test that pins it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Much of what decides a pushdown is measurement, not logic.</b> That <c>!=</c> over a null is
    /// true at the service, that <c>JSON_VALUE</c> reads an object as null, that <c>/</c> is
    /// floating point at one end and integer at the other: none of these can be reasoned to, and each
    /// was measured before a rendering was written against it. Until this list they were spread over
    /// remarks in the translator, <c>DESIGN.md</c> and the measurement classes, and two of them
    /// disagreed — <c>IS_PRIMITIVE</c> over an absent path was false in the translator and undefined
    /// in <c>DESIGN.md</c>. A wrong axiom is wrong at every site that consults it, so the sites should
    /// consult one place.
    /// </para>
    /// <para>
    /// <b>The rendering graph reads a row's outcomes; the translator reads none yet.</b> A row may
    /// carry what its measurement answered, key by key (<see cref="CosmosOutcome"/>), which
    /// <see cref="CosmosAxiomRows"/> reads into S's measured rows. Each row names the test that
    /// measures it, and <c>CosmosAxiomsTests</c> requires that test to exist and, where the row carries
    /// outcomes, to say it pins the row, so a row cannot outlive its evidence. Making the translator
    /// read the rows is later work. Until then a rendering that rests on an axiom should name the
    /// row's id beside the measurement it already cites.
    /// </para>
    /// <para>
    /// <b>The targets are a record of what was run; what a statement must hold on is a declared
    /// set.</b> <c>MeasuredOn</c>, and an outcome's <c>On</c>, name the targets that ran the
    /// measurement and passed. S declares the sets a statement may be checked under — Azure alone,
    /// Azure and the emulator, the emulator alone as CI runs — and a key answers in a set once every
    /// target the set serves has answered it, the union of their answers where they differ. So a
    /// capability holds under both only where both accept it, the emulator having differed from the
    /// service before (<c>DESIGN.md</c>, <em>Verified against the emulator</em>), and a key one target
    /// never ran answers anything. A row the emulator does not hold — an <c>ORDER BY</c> over an
    /// excluded path accepted there and refused on Azure — names Azure alone, and its test detects
    /// the emulator's answer and skips.
    /// </para>
    /// <para>
    /// <b>An outcome claims Azure only where a run record holds its answer</b>
    /// (<c>Measurements/Runs</c> in the tests), read by the statements its pinning test runs, over
    /// every witness the test holds the cell to. On 2026-10-21 the probe account ran every row's
    /// pinning test as it stands (D7), so each outcome claims both targets but where they answered
    /// apart; of its run of 2026-10-16, what it projected, wrote inline or only ordered backs no
    /// claim, since its filter has answered apart from its projection. The Azure targets of the
    /// string functions and the stored values rest on a run of 2026-10-19, which has no record. A function row's keys
    /// (<see cref="CosmosFunctionOutcome"/>) claim Azure where the probe account's run 1 (2026-11-04) measured them, its
    /// record's lines read again through Calcite in process; a row measured on the emulator since 2026-11-02 names the
    /// release that answered it in <c>Measurements/Runs/Emulator</c>, and one run 1 measured holds every line of it there.
    /// </para>
    /// </remarks>
    internal static class CosmosAxioms
    {

        static readonly DateOnly Today = new(2026, 10, 15);

        static readonly DateOnly Orders = new(2026, 10, 16);

        static readonly DateOnly Stored = new(2026, 10, 19);

        static readonly DateOnly Strings = new(2026, 10, 19);

        static readonly DateOnly Family = new(2026, 10, 20);

        static readonly DateOnly Spelling = new(2026, 10, 31);

        static readonly DateOnly Cases = new(2026, 11, 2);

        static readonly DateOnly Pairs = new(2026, 11, 3);

        // A connective's operands that are no truth, each measured over its own witnesses; declared before the rows read it.
        static readonly Class[] NoTruth = [Class.Undefined, Class.Absent, Class.Null, Class.Number, Class.String, Class.Object, Class.Array];

        // An outcome's targets, those that ran its own witnesses: one alone where the two answered apart.
        const CosmosAxiomTarget Both = CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Emulator = CosmosAxiomTarget.Emulator, Azure = CosmosAxiomTarget.Azure;

        /// <summary>
        /// A string function's keys on each target: <see cref="CosmosFunctionAnswer.Differs"/> over the bands named for it
        /// and the same over the rest of a string's, one outcome for both where the two agree; undefined over a number and a
        /// boolean, the same over a null, an absent path, an object and an array, on both.
        /// </summary>
        static IEnumerable<CosmosFunctionOutcome> StringFunction(string function, long subject, JsonCharacterBand differs, JsonCharacterBand azure) =>
        [
            .. from band in new[] { JsonCharacterBand.None, JsonCharacterBand.Surrogates, JsonCharacterBand.HighBmp, JsonCharacterBand.NonAscii, JsonCharacterBand.Trimmed, JsonCharacterBand.Nul }
               let emulator = band != JsonCharacterBand.None && differs.HasFlag(band) ? CosmosFunctionAnswer.Differs : CosmosFunctionAnswer.Same
               let onAzure = band != JsonCharacterBand.None && azure.HasFlag(band) ? CosmosFunctionAnswer.Differs : CosmosFunctionAnswer.Same
               from outcome in emulator == onAzure ? [new CosmosFunctionOutcome(function, subject, Class.String, band, Both, emulator)]
                   : new[] { new CosmosFunctionOutcome(function, subject, Class.String, band, Emulator, emulator), new CosmosFunctionOutcome(function, subject, Class.String, band, Azure, onAzure) }
               select outcome,
            .. from kind in new[] { Class.Number, Class.True, Class.False }
               select new CosmosFunctionOutcome(function, subject, kind, JsonCharacterBand.None, Both, CosmosFunctionAnswer.Undefined),
            .. from kind in new[] { Class.Null, Class.Absent, Class.Object, Class.Array }
               select new CosmosFunctionOutcome(function, subject, kind, JsonCharacterBand.None, Both, CosmosFunctionAnswer.Same),
        ];

        /// <summary>
        /// A typed arithmetic operator's keys, the two targets alike: what it answers over a number, undefined over a string
        /// and a boolean, the same over a null, an absent path, an object and an array.
        /// </summary>
        static IEnumerable<CosmosFunctionOutcome> Arithmetic(string function, CosmosFunctionAnswer overNumbers) =>
        [
            new(function, FunctionKey.Every, Class.Number, JsonCharacterBand.None, Both, overNumbers),
            .. from kind in new[] { Class.String, Class.True }
               select new CosmosFunctionOutcome(function, FunctionKey.Every, kind, JsonCharacterBand.None, Both, CosmosFunctionAnswer.Undefined),
            .. from kind in new[] { Class.Null, Class.Absent, Class.Object, Class.Array }
               select new CosmosFunctionOutcome(function, FunctionKey.Every, kind, JsonCharacterBand.None, Both, CosmosFunctionAnswer.Same),
        ];

        /// <summary>The row of an id.</summary>
        public static CosmosAxiom Get(string id) => All.Single(a => a.Id == id);

        /// <summary>
        /// Every row, grouped by what relies on it.
        /// </summary>
        public static IReadOnlyList<CosmosAxiom> All { get; } =
        [
            // ── The IS_* family: the guards the translator writes over a path ─────────────

            new("Service.IsPrimitive",
                "IS_PRIMITIVE is true of a string, a number, a boolean and a null, and false of an object, an array and an absent path: false, not undefined. So (IS_PRIMITIVE(p) ? p : null) is null over an absent path.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.IsPrimitiveIsFalseOverAnAbsentPath",
                [new(Test.IsPrimitive, Class.None, Both, "absent=false null=true true=true false=true number=true string=true object=false array=false")]),
            new("Service.IsDefined",
                "IS_DEFINED is false over an absent path and true over every value, a null, an object and an array included.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.IsDefinedIsFalseOnlyOverAnAbsentPath",
                [new(Test.IsDefined, Class.None, Both, "absent=false null=true true=true false=true number=true string=true object=true array=true")]),
            new("Service.IsNull",
                "IS_NULL is true of a null alone, and false over an absent path, an object and an array.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.IsNullIsTrueOnlyOfANull",
                [new(Test.IsNull, Class.None, Both, "absent=false null=true true=false false=false number=false string=false object=false array=false")]),
            new("Service.KindTests",
                "IS_STRING, IS_NUMBER, IS_BOOL, IS_OBJECT and IS_ARRAY are each true of their kind alone, and false over an absent path.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.EachKindTestIsTrueOfItsKindAlone",
                [
                    new(Test.IsString, Class.None, Both, "absent=false null=false true=false false=false number=false string=true object=false array=false"),
                    new(Test.IsNumber, Class.None, Both, "absent=false null=false true=false false=false number=true string=false object=false array=false"),
                    new(Test.IsBool, Class.None, Both, "absent=false null=false true=true false=true number=false string=false object=false array=false"),
                    new(Test.IsObject, Class.None, Both, "absent=false null=false true=false false=false number=false string=false object=true array=false"),
                    new(Test.IsArray, Class.None, Both, "absent=false null=false true=false false=false number=false string=false object=false array=true"),
                ]),
            new("Service.NotOfAKindTest",
                "NOT over any IS_* test is a real boolean over an absent path, true wherever the test is false; the family never answers undefined.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.NotOfATypeTestIsTrueOverAnAbsentPath",
                [
                    .. new[] { "PRIMITIVE", "STRING", "NUMBER", "BOOL", "NULL", "OBJECT", "ARRAY" }.Select(test => new CosmosOutcome(Test.Not, Class.None, Both, "absent=true", $"NOT IS_{test}(c.v)")),
                    new(Test.Not, Class.None, Both, "absent=true null=false true=false false=false number=false string=false object=false array=false", "NOT IS_DEFINED(c.v)"),
                ]),

            // ── Comparisons and NOT, where undefined and SQL's unknown part company ───────

            new("Service.OrderingAcrossTypesIsUndefined",
                "An ordering comparison between values of different types is undefined, as is one over an absent path: \"bikes\" < \"zzz\" is true, and 30 < \"zzz\" is undefined.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.AnOrderingAcrossTypesIsUndefined",
                [
                    new(Test.Less, Class.String, Both, "below=true absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.Greater, Class.Number, Both, "above=true absent=undefined null=undefined true=undefined false=undefined string=undefined object=undefined array=undefined"),
                ]),
            new("Service.EqualityAcrossTypesIsFalse",
                "An equality between values of different types is false, not undefined, a null against anything but null included; only over an absent path is it undefined.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.AnEqualityAcrossTypesIsFalseAndAnInequalityOverANullIsTrue",
                [new(Test.Equal, Class.String, Both, "equal=true absent=undefined null=false true=false false=false number=false object=false array=false")]),
            new("Service.InequalityOverNullIsTrue",
                "!= is true over a null and over a value of another type, where SQL's <> over a null is unknown; over an absent path it is undefined (#32).",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.AnEqualityAcrossTypesIsFalseAndAnInequalityOverANullIsTrue",
                [
                    new(Test.NotEqual, Class.String, Both, "equal=false absent=undefined null=true true=true false=true number=true object=true array=true"),
                    new(Test.NotEqual, Class.Number, Both, "equal=false absent=undefined null=true true=true false=true string=true object=true array=true"),
                ]),
            new("Service.NotOverUndefinedIsUndefined",
                "NOT over undefined is undefined: over an absent path, over an ordering across types, and over a value that is not a boolean.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.NotOverUndefinedIsUndefined",
                [
                    new(Test.Not, Class.None, Both, "absent=undefined null=undefined true=false false=true number=undefined string=undefined object=undefined array=undefined"),
                    new(Test.Not, Class.None, Both, "absent=undefined null=undefined true=undefined false=undefined number=undefined string=false object=undefined array=undefined", "NOT (c.v < \"zzz\")"),
                    // Its string cell is "bikes" itself, which not every string witness is.
                    new(Test.Not, Class.None, Both, "absent=undefined null=true true=true false=true number=true object=true array=true", "NOT (c.v = \"bikes\")"),
                ]),
            new("Service.BooleanEqualityIsUndefinedOnlyOverAbsent",
                "A comparison with a boolean is false over a null and over a value of another type, and undefined over an absent path, so NOT above it keeps the first two and not the third.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, new(2026, 10, 9), "Measurements.CosmosBooleanLogicMeasurementTests.AComparisonIsUndefinedOnlyOverAnAbsentPath",
                [new(Test.Equal, Class.True, Both, "true=true false=false null=false absent=undefined string=false number=false object=false array=false")]),

            // ── A stored boolean written bare (TryStoredBoolean, WriteTruthTest) ──────────

            new("Service.BarePathKeepsOnlyTrue",
                "A path written as a whole condition keeps a stored true and nothing else: not a string spelling it, not a number.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, new(2026, 10, 9), "Measurements.CosmosBooleanLogicMeasurementTests.ABarePathKeepsOnlyTrue",
                [new(Test.Bare, Class.None, Both, "true=true false=false null=undefined absent=undefined string=undefined number=undefined object=undefined array=undefined")]),
            new("Service.NotOverAPathKeepsOnlyFalse",
                "NOT over a path keeps a stored false alone; over a null, an absent path and a value that is not a boolean it keeps nothing, as SQL's NOT over unknown.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, new(2026, 10, 9), "Measurements.CosmosBooleanLogicMeasurementTests.NotOverAPathKeepsOnlyFalse",
                [new(Test.Not, Class.None, Both, "false=true true=false null=undefined absent=undefined string=undefined number=undefined object=undefined array=undefined")]),
            new("Service.ConnectivesReadNonBooleansAsUnknown",
                "AND and OR treat a value that is not a boolean, a null and an absent path as SQL treats unknown: OR true keeps it, NOT (... AND false) keeps it, NOT (... OR false) does not.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, new(2026, 10, 9), "Measurements.CosmosBooleanLogicMeasurementTests.TheConnectivesTreatEverythingButABooleanAsUnknown",
                [
                    new(Test.Or, Class.None, Both, "true=true false=true null=true absent=true string=true number=true object=true array=true", "(c.v OR true)"),
                    new(Test.Not, Class.None, Both, "true=true false=true null=true absent=true string=true number=true object=true array=true", "NOT (c.v AND false)"),
                    new(Test.Not, Class.None, Both, "false=true true=false null=undefined absent=undefined string=undefined number=undefined object=undefined array=undefined", "NOT (c.v OR false)"),
                ]),

            // ── What the rendering check reads, measured whole on the emulator and the probe account (D7) ──

            new("Service.ComparisonWithinAType",
                "A comparison of a string with a string, or of a number with a number, is true or false as the two stand in the type's order, never undefined; against a value of another type = is false, != true and an ordering undefined, and over an absent path each is undefined. = and != against true or false are true or false over every value but an absent path.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Family, "Measurements.CosmosJsonTypeMeasurementTests.AComparisonWithinATypeIsTrueOrFalse",
                [
                    // Another type's cells, where an earlier row of the family answers them, are its.
                    new(Test.Equal, Class.String, Both, "below=false equal=true above=false"),
                    new(Test.NotEqual, Class.String, Both, "below=true equal=false above=true"),
                    new(Test.Less, Class.String, Both, "below=true equal=false above=false"),
                    new(Test.LessOrEqual, Class.String, Both, "below=true equal=true above=false absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.Greater, Class.String, Both, "below=false equal=false above=true absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.GreaterOrEqual, Class.String, Both, "below=false equal=true above=true absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.Equal, Class.Number, Both, "below=false equal=true above=false absent=undefined null=false true=false false=false string=false object=false array=false"),
                    new(Test.NotEqual, Class.Number, Both, "below=true equal=false above=true"),
                    new(Test.Less, Class.Number, Both, "below=true equal=false above=false absent=undefined null=undefined true=undefined false=undefined string=undefined object=undefined array=undefined"),
                    new(Test.LessOrEqual, Class.Number, Both, "below=true equal=true above=false absent=undefined null=undefined true=undefined false=undefined string=undefined object=undefined array=undefined"),
                    new(Test.Greater, Class.Number, Both, "below=false equal=false above=true"),
                    new(Test.GreaterOrEqual, Class.Number, Both, "below=false equal=true above=true absent=undefined null=undefined true=undefined false=undefined string=undefined object=undefined array=undefined"),
                    new(Test.Equal, Class.True, Both, "true=true false=false absent=undefined null=false number=false string=false object=false array=false"),
                    new(Test.NotEqual, Class.True, Both, "true=false false=true absent=undefined null=true number=true string=true object=true array=true"),
                    new(Test.Equal, Class.False, Both, "true=false false=true absent=undefined null=false number=false string=false object=false array=false"),
                    new(Test.NotEqual, Class.False, Both, "true=true false=false absent=undefined null=true number=true string=true object=true array=true"),
                ]),
            new("Service.ConnectivesAreKleene",
                "AND and OR are Kleene's over true, false and undefined, in either order, a value that is no boolean and an absent path read as undefined, and NOT over undefined is undefined: false AND anything is false and true OR anything true, either way round.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Family, "Measurements.CosmosBooleanLogicMeasurementTests.TheConnectivesAreKleenesInEitherOrder",
                [
                    new(Test.And, Class.True, Both, "true=true false=false undefined=undefined absent=undefined null=undefined number=undefined string=undefined object=undefined array=undefined"),
                    new(Test.And, Class.False, Both, "true=false false=false undefined=false absent=false null=false number=false string=false object=false array=false"),
                    .. NoTruth.Select(x => new CosmosOutcome(Test.And, x, Both, "true=undefined false=false undefined=undefined absent=undefined null=undefined number=undefined string=undefined object=undefined array=undefined")),
                    new(Test.Or, Class.True, Both, "true=true false=true undefined=true absent=true null=true number=true string=true object=true array=true"),
                    new(Test.Or, Class.False, Both, "true=true false=false undefined=undefined absent=undefined null=undefined number=undefined string=undefined object=undefined array=undefined"),
                    .. NoTruth.Select(x => new CosmosOutcome(Test.Or, x, Both, "true=true false=undefined undefined=undefined absent=undefined null=undefined number=undefined string=undefined object=undefined array=undefined")),
                    new(Test.Not, Class.None, Both, "true=false false=true undefined=undefined absent=undefined null=undefined number=undefined string=undefined object=undefined array=undefined"),
                ]),
            new("Service.AConstantIsItsOwnTruth",
                "true written as a condition keeps every document and NOT true none, and false the reverse, whatever a path holds or lacks.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Family, "Measurements.CosmosBooleanLogicMeasurementTests.AConstantIsItsOwnTruth",
                [
                    new(Test.Constant, Class.True, Both, "absent=true null=true true=true false=true number=true string=true object=true array=true"),
                    new(Test.Constant, Class.False, Both, "absent=false null=false true=false false=false number=false string=false object=false array=false"),
                ]),
            new("Service.TypeTestBodiesMatchTheService",
                "The in-process bodies of the IS_* tests (CosmosFunctionBodies) answer what the service answers over a path holding each kind of value, false and a fraction among them, and over an absent one; so IS_DEFINED's Calcite side, which is its body, is the service's.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Family, "Measurements.CosmosJsonTypeMeasurementTests.TheTypeTestBodiesAnswerWhatTheServiceAnswersOverEveryKind"),
            new("Service.NotBindsTightestThenAndThenOr",
                "In a condition NOT binds tighter than a comparison and than AND, and AND tighter than OR, parentheses grouping as written: NOT c.a = @v compares NOT c.a, NOT x AND y negates x alone, and x OR y AND z is x OR (y AND z). The rendering check's reader reads every text it claims by this grammar.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, new(2026, 10, 22), "Measurements.CosmosSqlPrecedenceMeasurementTests.TheServiceKeepsWhatThePinnedTreePredicts"),
            new("Service.NumbersBeyondLongCompareAsDoubles",
                "A number written as a fraction or outside int64 is held as its correctly rounded double and compares as that double, against an int64 by value rather than rounded: 9007199254740992.5, held as 2^53, is below 2^53 + 1, 9999999999999999999999 equals 1e22, and 2^63 - 1 is below 2^63. The emulator keeps an integer written in [2^63, 2^64) as written (Service.IntegerOutsideLongIsStoredAsADouble) and compares it as its double, 2^63 + 1 equal to 2^63; and it holds 9007199254740993.0 as a double, which Azure holds as an int64.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Family, "Measurements.CosmosServiceOrderMeasurementTests.NumbersBeyondLongCompareAsDoubles",
                [
                    new(Test.Equal, Class.Number, Both, "below=false equal=true above=false"),
                    new(Test.NotEqual, Class.Number, Both, "below=true equal=false above=true"),
                    new(Test.Less, Class.Number, Both, "below=true equal=false above=false"),
                    new(Test.LessOrEqual, Class.Number, Both, "below=true equal=true above=false"),
                    new(Test.Greater, Class.Number, Both, "below=false equal=false above=true"),
                    new(Test.GreaterOrEqual, Class.Number, Both, "below=false equal=true above=true"),
                ]),

            // ── Functions and arithmetic over a rendering ─────────────────────────────────

            new("Service.StringFunctionsOverNonStrings",
                "UPPER, LOWER and CONCAT are undefined over a number, a boolean, a null, an object, an array and an absent path; only a string has an answer, and the guarded accessor changes nothing.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.TheStringFunctionsAreUndefinedOverAnythingButAString"),
            new("Service.DivisionIsFloatingPoint",
                "/ divides in floating point, two integers included (7 / 2 is 3.5, -7 / 2 is -3.5), and is undefined over anything that is not a number.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosJsonTypeMeasurementTests.DivisionIsFloatingPointAndUndefinedOverAnythingButANumber"),
            new("Calcite.IntegerDivisionTruncates",
                "Calcite's / over two integers is integer division, truncating toward zero: 7 / 2 is 3, -7 / 2 is -3, and an accessor RETURNING INTEGER divides the same way.",
                CosmosAxiomTarget.Calcite, Today, "Measurements.CalciteArithmeticMeasurementTests.IntegerDivisionTruncatesTowardZero"),
            new("Service.IntegerArithmeticIsExactWithinLong",
                "+, - and * over integers are exact while the result fits int64, past 2^31 and past 2^53, 2^53 + 1 being 9007199254740993, and a double past it, 2^63 - 1 + 1 above 2^63 - 1 and never wrapped; / is a double; a result JSON cannot hold, an infinity, fails the statement projected and is undefined in a WHERE, 1e308 * 2 and any number / 0 alike; and each is undefined over a string, a boolean, a null and an absent path. So against Calcite's INTEGER, BIGINT, DECIMAL and DOUBLE each typed operator parts over a number, Calcite wrapping, truncating, raising over a fraction or rounding past 2^53 where the service does not, and * and / fail where Calcite answers an infinity or raises. Azure answers alike, but for % of 2^63 - 1 by 2, 0 there in doubles where the emulator answers 1.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosJsonTypeMeasurementTests.IntegerArithmeticIsExactWithinLong",
                Readers: [.. from type in new[] { "INTEGER", "BIGINT", "DECIMAL", "DOUBLE" } from reader in new[] { PredicateAtom.TypedReader(type), PredicateAtom.CastReader(type, PredicateAtom.TextReader) } select reader],
                FunctionOutcomes:
                [
                    .. from type in new[] { "INTEGER", "BIGINT", "DECIMAL", "DOUBLE" }
                       from op in new[] { ("+", CosmosFunctionAnswer.Differs), ("-", CosmosFunctionAnswer.Differs), ("*", CosmosFunctionAnswer.Fails), ("/", CosmosFunctionAnswer.Fails), ("%", CosmosFunctionAnswer.Differs) }
                       from outcome in Arithmetic(op.Item1 + ":" + type, op.Item2)
                       select outcome,
                ]),
            new("Service.OutOfDomainArithmeticFailsTheStatement",
                "ASIN(2), ACOS(2), SQRT(-1) and LOG(0) fail the whole statement rather than answering undefined for the row, where Calcite answers NaN.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, new(2026, 8, 8), "Client.CosmosQueryExecutorTests.OutOfDomainArithmeticFailsTheQuery"),

            // ── What a point read answers (CosmosQueryExecutor, CosmosImplementor) ────────

            new("Service.ReadManyIsWholeDocuments",
                "ReadManyItemsAsync answers each document it finds whole, one per id, applying no projection, predicate or DISTINCT: two documents equal in every value come back as two.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosReadManyMeasurementTests.ReadManyAnswersOneWholeDocumentPerId"),
            new("Service.ReadManyReadsAnIdOnce",
                "ReadManyItemsAsync answers an id it is asked for twice once.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Today, "Measurements.CosmosReadManyMeasurementTests.ReadManyAnswersAnIdAskedTwiceOnce"),
            new("Service.PointReadBeatsTheResidualQuery",
                "A point read followed by an in-process residual is charged less than the query the residual forces, whether the residual keeps the document or discards it, for a small document.",
                CosmosAxiomTarget.Azure, new(2026, 9, 14), "Measurements.CosmosPointReadResidualMeasurementTests.AReadThenFilterCostsLessThanTheQueryTheResidualForces"),
            new("Service.ReadManyAndTheSetQueryCrossOver",
                "ReadManyItemsAsync is charged per document and the set query is not, so the two cross as the id set grows; for one id the read is cheaper.",
                CosmosAxiomTarget.Azure, new(2026, 9, 14), "Measurements.CosmosPointReadResidualMeasurementTests.TheBatchReadAndTheSetQueryCrossOverAsTheIdSetGrows"),

            // ── JSON_VALUE and JSON_QUERY in process: what an accessor means ──────────────

            new("Calcite.JsonValueRendersScalarsAsText",
                "JSON_VALUE without RETURNING is declared VARCHAR and answers every scalar as its text: 30 as '30', true as 'true', 1e30 as '1.0E30', a large integer with every digit.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 7), "Measurements.CalciteJsonValueMeasurementTests.TheBareAccessorRendersEveryScalarAsText"),
            new("Calcite.JsonValueReadsStructureAsNull",
                "JSON_VALUE without RETURNING answers SQL null for an object, an array, a JSON null and an absent path.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 7), "Measurements.CalciteJsonValueMeasurementTests.TheBareAccessorRendersEveryScalarAsText"),
            new("Calcite.JsonQueryAnswersStructureOnly",
                "JSON_QUERY answers an object or an array as its JSON, written without the whitespace the document carried, and null for a scalar, a JSON null and an absent path.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 16), "Measurements.CalciteJsonValueMeasurementTests.JsonQueryAnswersStructureOnlyAndWritesItCompactly"),
            new("Calcite.JsonQueryClausesSubstitute",
                "A JSON_QUERY wrapper or behaviour clause answers a value the path does not hold.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 16), "Measurements.CalciteJsonValueMeasurementTests.JsonQueryWrapperAndBehaviourClausesAnswerSomethingElse"),
            new("Calcite.JsonValueClausesFollowThePathMode",
                "A JSON_VALUE behaviour clause answers by the path's mode: a path with none written is strict, so an absent property, a null, an object and an array take ON ERROR, not ON EMPTY.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 9), "Measurements.CalciteJsonValueMeasurementTests.JsonValueBehaviourClausesFollowThePathModeNotTheStandardsReading"),
            new("Calcite.ReturningAsserts",
                "RETURNING asserts the type rather than converting to it: a scalar of another type raises, NULL ON ERROR notwithstanding, while an object answers null.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 7), "Measurements.CalciteJsonValueMeasurementTests.ReturningAssertsTheTypeAndOnErrorDoesNotGovernIt"),
            new("Calcite.ArrayReturningNeverAnswersAnArray",
                "An array RETURNING answers null over an array, an object, a null and an absent path, and raises over a scalar.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 15), "Measurements.CalciteJsonValueMeasurementTests.AnArrayReturningAnswersNullForAnArrayAndThrowsForAScalar"),
            new("Calcite.CastToBooleanParses",
                "A cast of the accessor to BOOLEAN parses the rendering: a stored boolean reads as itself, a null and an absent path as null, a string spelling a boolean parses, trimmed of every character up to U+0020 as String.trim trims and in any ASCII case, and any other string and a number raise.",
                CosmosAxiomTarget.Calcite, new(2026, 11, 3),"Measurements.CalciteJsonValueMeasurementTests.ACastToBooleanParsesTheRendering"),
            new("Calcite.CastOfTheAccessorParsesTheRendering",
                "A cast of the accessor to INTEGER or BIGINT is Java's integer parse of the rendering, trimmed of every character up to U+0020 as String.trim trims, and to DOUBLE a double parse that reads no type suffix: a whole number written without a point lands exactly, a string spelling a number parses, and a point, an exponent, a whole number beyond the type and anything else raise under the integer types. BIGINT reads text that starts 199 and holds a colon as a timestamp instead.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 22), "Measurements.CalciteJsonValueMeasurementTests.ACastOfTheAccessorToANumberParsesTheRendering"),
            new("Calcite.ReturningDecimalParsesTheRendering",
                "RETURNING DECIMAL parses the rendering as a BigDecimal, trimmed of every character up to U+0020, a string of digits among them, and raises only on what does not parse; it is typed DECIMAL(19, 0), so a literal compared with it is cast to scale zero.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 22), "Measurements.CalciteJsonValueMeasurementTests.ReturningDecimalParsesTheRendering"),

            // ── What Calcite compares, in the columns the reader fills: the rendering check's Calcite side ──

            new("Calcite.ColumnReadsWholeNumbersAsItsType",
                "A column typed BIGINT or INTEGER reads a stored number written as an integer in the type's range as the type, and any other number as its double where that is whole and in the type's range: 42.0, a fraction that rounds to a whole double, and past 2^53 one written with a point as that double. It fails the statement over any other number, a string and a boolean. DOUBLE reads every number as its double, DECIMAL a number's text exactly and nothing else, BOOLEAN a boolean alone, and each compares and orders as its type. RETURNING raises over 42.0 under INTEGER and over an int-sized number under BIGINT.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 22), "Measurements.CalciteColumnReadingMeasurementTests.EachColumnReadsAndComparesAsItsType"),
            new("Calcite.RawStringEqualityIsOrdinal",
                "A raw value in a VARIANT, an ANY or a VARCHAR column is equal to a string where it is the string with the same UTF-16 units, and to nothing else: not another case, not a trailing space, not a number, a boolean or a structure.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 20), "Measurements.CalciteVariantMeasurementTests.ARawValueIsEqualToTheStringOfItsUnitsAlone"),
            new("Calcite.RawNullIsSqlNull",
                "A stored null and an absent path are SQL null in a VARIANT, an ANY and a VARCHAR column: unknown to = and <>, and true to IS NULL. A value of another type is unequal; a VARCHAR column fails the statement over anything but a string, ANY fails an ordering across types, and VARIANT has no ordering and is equal to a number boxed with its own SQL type alone. Within a type ANY orders as the type, a string by its UTF-16 units as a VARCHAR column does and false before true, and compares a number in the box the reader built: a long by its value, and any other number by its double's exact value, so 2^53 + 1 written with a point is 2^53 and a stored 0.1 lies above 0.1; two doubles compare by Double.compareTo, so a stored -0.0 lies below a double 0 and is unequal to it, where a long 0 equals both.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 20), "Measurements.CalciteVariantMeasurementTests.ARawNullIsSqlNullAndAnotherTypeIsUnequal",
                Columns:
                [
                    // No ordering, the statement failing to compile; a number equal to one boxed with its own type alone; cast to
                    // text, a number or a boolean is null and a structure raises.
                    new("VARIANT", false, CosmosRawColumn.Kinds(Column.Itself, Column.Itself, Column.Itself, Column.Itself), false, CosmosRawColumn.Kinds(Column.Itself, Column.Null, Column.Null, Column.Raises)),
                    // Ordered as the type, a number in its box; cast to text, each value as Java renders the box.
                    new("ANY", false, CosmosRawColumn.Kinds(Column.Itself, Column.Itself, Column.Itself, Column.Itself), true, CosmosRawColumn.Kinds(Column.Itself, Column.Itself, Column.Itself, Column.Itself)),
                    // A string alone, the statement failing over anything else.
                    new("VARCHAR", true, CosmosRawColumn.Kinds(Column.Itself, Column.Raises, Column.Raises, Column.Raises), true, CosmosRawColumn.Kinds(Column.Itself, Column.Raises, Column.Raises, Column.Raises)),
                ]),
            new("Calcite.CastOfARawValueToText",
                "A cast to VARCHAR of a raw value is its column's own. A string is itself and a null or an absent path null in each; a VARIANT casts a number and a boolean to null and raises over an object or an array; an ANY renders each value as Java renders the reader's box: a boolean as true or false, an integer within a long in its digits and any other number as Java spells its double, 42.0 as 42.0 and 99999999999999999999 as 1.0E20, an object as {a=1} and an array as [1]; and a VARCHAR column reads a string alone.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 24), "Measurements.CalciteVariantMeasurementTests.ACastOfARawValueToTextIsEachColumnsOwn"),
            new("Calcite.ReadingsAreNullOverAnAbsentPath",
                "Every reading the predicate loader builds is null over an absent path: the text accessor; RETURNING of each scalar type, and a column of each; the raw value in a VARIANT, an ANY and a VARCHAR column; a cast of the accessor or of the raw value to INTEGER, BIGINT or DOUBLE; and a cast of the raw value to VARCHAR. Read as data by the rendering graph's strict readings (CosmosStrictness).",
                CosmosAxiomTarget.Calcite, new(2026, 10, 23), "Measurements.CalciteNullStrictnessMeasurementTests.EveryReadingIsNullOverAnAbsentPath",
                Subjects: [PredicateAtom.TextReader, PredicateAtom.RawReader,
                    .. new[] { "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "FLOAT", "REAL", "DOUBLE", "DECIMAL", "BOOLEAN" }.Select(PredicateAtom.TypedReader),
                    .. from target in new[] { "INTEGER", "BIGINT", "DOUBLE" } from inner in new[] { PredicateAtom.TextReader, PredicateAtom.RawReader } select PredicateAtom.CastReader(target, inner),
                    PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)]),
            new("Calcite.ListedFunctionsAreNullOverANullOperand",
                "||, UPPER, LOWER, INITCAP, SUBSTRING, CHAR_LENGTH, CHARACTER_LENGTH, POSITION, REPLACE, LEFT, RIGHT, REVERSE, REPEAT, MOD, ABS, ROUND, POWER, SAFE_CAST, TRY_CAST and IN over literals are null wherever an operand is null, in every form each takes, though Calcite declares no null policy for them. Read as data by the rendering graph's strict operators (CosmosStrictness).",
                CosmosAxiomTarget.Calcite, new(2026, 10, 23), "Measurements.CalciteNullStrictnessMeasurementTests.EachListedFunctionIsNullOverANullOperand",
                Subjects: ["||", "UPPER", "LOWER", "INITCAP", "SUBSTRING", "CHAR_LENGTH", "CHARACTER_LENGTH", "POSITION", "REPLACE", "LEFT", "RIGHT", "REVERSE", "REPEAT",
                    "MOD", "ABS", "ROUND", "POWER", "SAFE_CAST", "TRY_CAST", "IN"]),
            new("Calcite.TextOrderIsByCodeUnit",
                "Text compares by UTF-16 code unit, as String.compareTo does: U+E000 and U+FFFD above U+1F600, which code point order puts above both, and 'true' and 'false' above 'bikes', '30' below.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 20), "Measurements.CalciteTextOrderMeasurementTests.TextComparesByUtf16Unit"),
            new("Calcite.ConnectivesAreKleene",
                "AND, OR and NOT are Kleene's over TRUE, FALSE and UNKNOWN, in either order; an operand that raises raises AND beside FALSE and OR beside TRUE, in either order, both operands of a nullable connective being evaluated.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 20), "Measurements.CalciteConnectiveMeasurementTests.TheConnectivesAreKleenesAndEvaluateBothOperands",
                [
                    new(Test.And, Class.True, CosmosAxiomTarget.Calcite, "true=true false=false undefined=undefined"),
                    new(Test.And, Class.False, CosmosAxiomTarget.Calcite, "true=false false=false undefined=false"),
                    new(Test.And, Class.Undefined, CosmosAxiomTarget.Calcite, "true=undefined false=false undefined=undefined"),
                    new(Test.Or, Class.True, CosmosAxiomTarget.Calcite, "true=true false=true undefined=true"),
                    new(Test.Or, Class.False, CosmosAxiomTarget.Calcite, "true=true false=false undefined=undefined"),
                    new(Test.Or, Class.Undefined, CosmosAxiomTarget.Calcite, "true=true false=undefined undefined=undefined"),
                    new(Test.Not, Class.None, CosmosAxiomTarget.Calcite, "true=false false=true undefined=undefined"),
                ]),

            // ── What Calcite answers of a CASE, a type test, an element and arithmetic ───────

            new("Calcite.CaseTakesTheFirstTrueArm",
                "CASE takes the arm of the first condition that is TRUE, a condition FALSE or UNKNOWN passing to the next, and the ELSE where none is, NULL without one; an arm not taken is not evaluated, so one that raises off its condition raises nowhere.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteCaseMeasurementTests.ACaseTakesTheFirstTrueArmAndEvaluatesNoOther",
                [
                    new(Test.Choose, Class.None, CosmosAxiomTarget.Calcite, "true=true false=false undefined=false"),
                ]),
            new("Calcite.CaseWithAnArmThatRaisesIsKept",
                "A CASE whose arm raises off its condition is kept a CASE through the planner a connection runs, in a WHERE and projected, through a simplifying RelBuilder and through Hep's reduction of a filter: none makes it the eager AND or OR that would raise, so a guard written as one still guards.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteCaseMeasurementTests.ACaseWhoseArmRaisesIsKept"),
            new("Calcite.TruthTestsAreCases",
                "x IS TRUE is TRUE where x is and FALSE elsewhere, IS NOT TRUE its negation, IS FALSE and IS NOT FALSE alike for FALSE, and IS NULL and IS UNKNOWN over a predicate TRUE where it is UNKNOWN, each the CASE of its definition; x = TRUE and x <> FALSE are x, and x = FALSE is NOT x.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteCaseMeasurementTests.TheTruthTestsAreCases"),
            new("Calcite.CoalesceIsACase",
                "COALESCE is the CASE of its first operand that is not null: COALESCE(p, FALSE) is TRUE where p is and FALSE elsewhere, and an operand after one that is not null is not evaluated.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteCaseMeasurementTests.CoalesceIsACase"),
            new("Calcite.TypeTestsAreTheirBodies",
                "In process the IS_* tests are their bodies (CosmosFunctionBodies), which answer over each kind of value at a path, and over an absent one, what the service answers (Service.TypeTestBodiesMatchTheService): a real boolean, never null.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteTypeTestMeasurementTests.EachTypeTestAnswersItsRowInProcess",
                [
                    new(Test.IsDefined, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=true true=true false=true number=true string=true object=true array=true"),
                    new(Test.IsNull, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=true true=false false=false number=false string=false object=false array=false"),
                    new(Test.IsPrimitive, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=true true=true false=true number=true string=true object=false array=false"),
                    new(Test.IsString, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=false true=false false=false number=false string=true object=false array=false"),
                    new(Test.IsNumber, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=false true=false false=false number=true string=false object=false array=false"),
                    new(Test.IsBool, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=false true=true false=true number=false string=false object=false array=false"),
                    new(Test.IsObject, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=false true=false false=false number=false string=false object=true array=false"),
                    new(Test.IsArray, Class.None, CosmosAxiomTarget.Calcite, "absent=false null=false true=false false=false number=false string=false object=false array=true"),
                ]),
            new("Calcite.ItemIsNullWhereNoElementIs",
                "ITEM over a raw value is its element, counted from one, and null wherever there is no element: past the end, at zero, over an object, a null and an absent path. Over a scalar a VARIANT's is null too and an ANY's raises. An ANY's element is the value the reader built, and a VARIANT's is no box: unequal to every boxed value, and a cast of it raises.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteItemMeasurementTests.AnItemIsNullWhereNoElementIs"),
            new("Calcite.ReadingsOfAnIndexAreAsOfAPath",
                "A reading of an element, the text accessor, a RETURNING, JSON_QUERY and JSON_EXISTS alike, answers what the same reading of a path holding the element answers, and where there is no element, past the end or over a value that is no array, what it answers over an absent path.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteItemMeasurementTests.AReadingOfAnIndexIsAsOfAPath"),
            new("Calcite.VariantEqualsOnlyItsOwnBox",
                "A VARIANT equals a value cast to VARIANT only in its own box: a stored integer, boxed BIGINT, the integer cast to BIGINT and nothing else, a fraction, boxed DOUBLE, a DOUBLE, and a string a string; so an IN of a number and its spelling keeps each in its own box alone. A text cast to VARIANT is a string box, equal to a boxed string where the text is that string and null where the text is null.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteVariantMeasurementTests.AVariantEqualsOnlyItsOwnBox"),
            new("Calcite.IntegerArithmeticWraps",
                "+, - and * over two INTEGERs or two BIGINTs wrap at the type's bounds as Java's do, 2147483647 + 1 being -2147483648 and 9223372036854775807 + 1 -9223372036854775808, and are exact within them, past 2^53 too.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteArithmeticMeasurementTests.IntegerArithmeticWraps"),
            new("Calcite.DecimalArithmeticIsExact",
                "+, - and * over DECIMAL are exact: 0.1 + 0.2 is 0.3 and 9007199254740993 + 0 is itself, where a double holds neither.",
                CosmosAxiomTarget.Calcite, Cases, "Measurements.CalciteArithmeticMeasurementTests.DecimalArithmeticIsExact"),

            // ── What the service answers of a ternary, an index, IN, the affixes and LIKE, and a number read back (Azure at h14b) ──

            new("Service.TernaryTakesThenOnTrueAlone",
                "c ? a : b and IIF(c, a, b) take a where c is true and b over every other value, false, null, a number, a string, \"true\" among them, an object, an array and an absent path alike, never undefined for a defined arm: so c ? true : false is true where c is true and false elsewhere, in a SELECT and in a WHERE.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosJsonTypeMeasurementTests.TheTernaryTakesThenOnTrueAlone",
                [
                    new(Test.Choose, Class.None, Both, "true=true false=false undefined=false absent=false null=false number=false string=false object=false array=false"),
                ]),
            new("Service.IndexOverANonArrayIsUndefined",
                "An index is the element of an array, counted from zero, and undefined over every value that is no array, a string included, and past an array's end, an empty array's too: IS_DEFINED of it is false there, and a comparison with it undefined.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosJsonTypeMeasurementTests.AnIndexOverANonArrayIsUndefined"),
            new("Service.InIsMembership",
                "IN is true where the value equals a member, a value of another kind never equal, 30 no \"30\" and true no \"true\", null IN (null) true, and false elsewhere, an object and an array included; over an absent path it is undefined.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosJsonTypeMeasurementTests.InIsMembership"),
            new("Service.StringPredicatesOverNonStringsAreUndefined",
                "STARTSWITH, ENDSWITH, CONTAINS and LIKE are undefined over a number, a boolean, a null, an object, an array and an absent path, a number matched by no pattern of its digits; over a string each is true or false.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosJsonTypeMeasurementTests.TheStringPredicatesOverNonStringsAreUndefined",
                [
                    // S's affix form (h07) reads a value that is no string by its kind.
                    new(Test.StartsWith, Class.None, Both, "absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.EndsWith, Class.None, Both, "absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.Contains, Class.None, Both, "absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.Like, Class.None, Both, "absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                ]),
            new("Service.AffixesMatchByCodePoint",
                "STARTSWITH, ENDSWITH and CONTAINS match a string's code points as written, with no normalisation: U+00E9 matches itself alone and e the decomposed e, U+0301 alone; U+1F600 and U+E000 match whole; a true third operand folds ASCII case and a false one does not; ENDSWITH of the empty string is true of every string, the empty one too. The emulator matches the string as it stores it against the argument as sent, so a statement sends it in that spelling (Service.EmulatorComparesTheSpellingItIsSent); Azure matches the string as written, its CONTAINS reading it only up to its first U+0000 (Service.NulEndsWhatReplaceAndContainsRead).",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosStringFunctionMeasurementTests.TheAffixesMatchByCodePoint",
                [
                    // S's affix form (h07): a string by whether the argument matches it, under the matcher the cell's none names;
                    // Azure's by code point, a CONTAINS either side where the string or the argument may hold U+0000 (ServiceLeaf).
                    new(Test.StartsWith, Class.String, Emulator, "none=storedcodepoint matching=true missing=false"),
                    new(Test.EndsWith, Class.String, Emulator, "none=storedcodepoint matching=true missing=false"),
                    new(Test.Contains, Class.String, Emulator, "none=storedcodepoint matching=true missing=false"),
                    new(Test.StartsWith, Class.String, Azure, "none=codepoint matching=true missing=false"),
                    new(Test.EndsWith, Class.String, Azure, "none=codepoint matching=true missing=false"),
                    new(Test.Contains, Class.String, Azure, "none=codepoint matching=true missing=false"),
                ]),
            new("Service.LikeMatchesByCodePoint",
                "LIKE matches by code point, case sensitive: _ matches one code point, U+1F600, U+00E9 and U+E000 alike, and % any run of them, the empty one too.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosStringFunctionMeasurementTests.LikeMatchesByCodePoint",
                [
                    // No matcher: what the emulator's LIKE makes of a stored escape is unmeasured (CosmosAxiomRows.Owed), so S's LIKE form answers anything.
                    new(Test.Like, Class.String, Emulator, "matching=true missing=false"),
                    // Azure's by code point, one run between two % read as a CONTAINS is (ServiceLeaf).
                    new(Test.Like, Class.String, Azure, "none=codepoint matching=true missing=false"),
                ]),
            new("Service.LikeReadsBracketsAsARange",
                "LIKE reads [a-c] as one character of the range and [^a-c] as one outside it, [[] as a literal [, and a pattern holding a [ it never closes matches nothing, the string it spells included; ESCAPE makes the character after it literal.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosStringFunctionMeasurementTests.LikeReadsBracketsAsARange"),
            new("Service.IntegralNumbersReadBackAsHeld",
                "A number reads back as the service holds it: 30, 30.0, 3e1 and 300e-1 read back 30, equal 30 and are IS_INTEGER, and -0 and -0.0 read back 0; an integer written without a point is held exactly, 9007199254740993 as itself, and one written with a point by the emulator as a double, 9007199254740993.0 as 9007199254740992, and by Azure as an int64 where it is one, 9007199254740993.0 as itself, IS_INTEGER either way; 1e20, 1E21 and 1.5e300 read back with an exponent and are not IS_INTEGER.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosJsonTypeMeasurementTests.IntegralNumbersReadBackAsHeld"),
            new("Service.TwoPathsCompareAsAPathAndALiteral",
                "c.v op c.w answers as c.v op the value at w written as a literal, for each of =, !=, <, <=, > and >=, over every kind of value on either side, an absent path included, and as c.w op' c.v with the comparator mirrored (< and >, <= and >=), so a right side no literal can be, null, an object, an array or an absent path, reads as the left compared with it the other way round. A pair with either side absent is undefined, both absent included; against null = is true of a null alone, != of everything else, <= and >= true of a null and < and > false of one, each ordering undefined over the rest.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Pairs, "Measurements.CosmosJsonTypeMeasurementTests.TwoPathsCompareAsAPathAndALiteral",
                [
                    new(Test.ComparePaths, Class.None, Both, "none=true"),
                    // A side absent, whatever the other holds (ServiceLeaf.Paired).
                    new(Test.ComparePaths, Class.Absent, Both, "none=undefined"),
                    // Each comparator against the literal null, as the right side null reads: two nulls among them.
                    new(Test.Equal, Class.Null, Both, "null=true absent=undefined true=false false=false number=false string=false object=false array=false"),
                    new(Test.NotEqual, Class.Null, Both, "null=false absent=undefined true=true false=true number=true string=true object=true array=true"),
                    .. new[] { (Test.Less, "false"), (Test.LessOrEqual, "true"), (Test.Greater, "false"), (Test.GreaterOrEqual, "true") }.Select(t =>
                        new CosmosOutcome(t.Item1, Class.Null, Both, $"null={t.Item2} absent=undefined true=undefined false=undefined number=undefined string=undefined object=undefined array=undefined")),
                ]),

            // ── Temporal conversions (CosmosTemporalForms, CosmosStoredForms, CosmosQueries.Bind) ──

            new("Calcite.TemporalCastReadsFewShapes",
                "A cast to a temporal type reads exactly the stored shapes CosmosTemporalForms.EngineReads lists, and raises over an ISO-8601 instant at any precision.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 21), "Measurements.CalciteTemporalReadingMeasurementTests.TheTableSaysWhatTheEngineDoes"),
            new("Calcite.TemporalCastCanSucceedWrongly",
                "CAST('20240115' AS TIME) answers a wrong value and CAST('12:30:00.123' AS TIME) drops the fraction: a conversion that succeeds is not therefore usable.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 21), "Measurements.CalciteTemporalReadingMeasurementTests.AConversionThatSucceedsIsNotThereforeUsable"),
            new("Calcite.ReturningTimestampWantsEpochMilliseconds",
                "RETURNING TIMESTAMP raises over every string and reads a JSON number of epoch milliseconds.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 21), "Measurements.CalciteTemporalReadingMeasurementTests.AReturningClauseAssertsTheTypeRatherThanConvertingToIt"),
            new("Calcite.ParseSpellingsReadTheirShapes",
                "Every PARSE_ format string the temporal form table claims reads its stored shape exactly.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 21), "Measurements.CalciteTemporalParseMeasurementTests.EverySpellingTheTableClaimsReadsItsShapeExactly"),
            new("Calcite.ParseReadsMinutesAsMonths",
                "The format model matches its elements without regard to case, so the mm of yyyy-MM-dd'T'HH:mm:ss'Z' is read as a month and the instant comes back wrong, without an error.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 21), "Measurements.CalciteTemporalParseMeasurementTests.TheJavaSpellingOfAnInstantIsReadAsTheWrongInstant"),
            new("Calcite.TimestampParameterIsEpochMilliseconds",
                "A TIMESTAMP parameter reaches the plan as a Long of epoch milliseconds, anything finer dropped.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 9), "Measurements.CalciteTemporalParameterMeasurementTests.ATimestampParameterIsEpochMilliseconds"),
            new("Calcite.DateParameterIsEpochDays",
                "A DATE parameter reaches the plan as an Integer of days since the epoch.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 9), "Measurements.CalciteTemporalParameterMeasurementTests.ADateParameterIsEpochDays"),

            // ── UUIDs and arrays read back (CosmosStoredForms, CosmosJson) ────────────────

            new("Calcite.UuidOrderIsCanonicalOrdinal",
                "ORDER BY over UUIDs is the ordinal order of their canonical spelling, compared unsigned since CALCITE-7716.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 19), "Measurements.CalciteUuidOrderingMeasurementTests.OrderingIsTheOrdinalOrderOfTheCanonicalSpelling"),
            new("Calcite.UuidValueReadsAsAGuid",
                "A UuidValue in a UUID column reaches the reader as a Guid, at a row of one column or more.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 20), "Measurements.CalciteUuidReadingMeasurementTests.AUuidValueReachesTheReaderAsAGuid"),
            new("Calcite.ListIsAnArrayValue",
                "A java.util.List in an ARRAY column reaches the reader as a CLR array of the element type.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 15), "Measurements.CalciteArrayReadingMeasurementTests.AListInAnArrayColumnReachesTheReaderAsAClrArray"),

            // ── Geography (TryStoredGeographyProjection, CosmosSort, the spatial renderings) ──

            new("Calcite.GeographyOverAScalarIsNull",
                "In process a geography over a scalar at the path is null, JSON_QUERY answering null before the constructor is reached; the constructor over the scalar directly raises.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 20), "Measurements.CalciteGeographyReadingMeasurementTests.AScalarAtThePathIsNullInProcessAndUnreadableDirectly"),
            new("Calcite.GeographyOverAnArrayRaises",
                "The GeoJSON constructor raises over an array, in process and directly alike.",
                CosmosAxiomTarget.Calcite, new(2026, 9, 20), "Measurements.CalciteGeographyReadingMeasurementTests.AnArrayAtThePathRaisesBothWays"),
            new("Calcite.GeometryParameterFromWkt",
                "A GEOMETRY parameter given WKT text arrives in the plan as a JTS geometry.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 10), "Measurements.CalciteGeometryParameterMeasurementTests.WktTextArrivesAsAGeometry"),
            new("Calcite.GeometryParameterRefusesTheRest",
                "A GEOMETRY parameter given GeoJSON text or a number fails before the plan executes.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 10), "Measurements.CalciteGeometryParameterMeasurementTests.GeoJsonTextAndANumberFailBeforeExecution"),
            new("Service.DistanceOverANonShapeIsUndefined",
                "ST_DISTANCE over anything that is not a shape is undefined, not an error.",
                CosmosAxiomTarget.Azure, new(2026, 9, 20), "Measurements.CosmosGeographyValidityMeasurementTests.ADistanceOverSomethingThatIsNotAShapeIsUndefined"),
            new("Service.ValidityIsDefinedness",
                "ST_ISVALID is true for exactly the documents ST_DISTANCE is defined over.",
                CosmosAxiomTarget.Azure, new(2026, 9, 20), "Measurements.CosmosGeographyValidityMeasurementTests.ValidityAgreesWithDefinedness"),
            // ── What a string function computes (CosmosFunctionSpellings) ─────────────────────

            new("Service.LengthCountsCodePoints",
                "LENGTH counts code points: a, U+1F600, b is 3, where Calcite's CHAR_LENGTH counts 4 UTF-16 units. On Azure the two counts are one without a surrogate; the emulator counts the code points of what it stores (EmulatorStoredText), U+0000 and U+FDD0 two each, so there they are one only without a surrogate, U+0000 or U+FDD0 (Service.EmulatorCountsTheStringItStores).",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.LengthCountsCodePoints"),
            new("Service.SubstringCountsUnits",
                "SUBSTRING, LEFT and RIGHT count UTF-16 units, and a window whose edge splits a surrogate pair returns the half as U+FFFD, where Calcite returns a lone surrogate; without a surrogate they answer as Calcite's. The emulator counts the code points of what it stores, U+0000 and U+FDD0 two each, and returns half an escape pair as a lone U+FDD0 or digit (Service.EmulatorCountsTheStringItStores).",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.SubstringLeftAndRightCountUnitsAndSendASplitPairAsReplacement"),
            new("Service.IndexOfCountsUnits",
                "INDEX_OF counts UTF-16 units, so INDEX_OF + 1 is Calcite's POSITION over every string: b in a, U+1F600, b is at 3. The emulator counts the code points of what it stores and answers 2, one more for each U+0000 or U+FDD0 before the match (Service.EmulatorCountsTheStringItStores).",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.IndexOfCountsUnits"),
            new("Service.CaseMappingIsSimple",
                "UPPER, LOWER and the case-insensitive CONTAINS, STARTSWITH and ENDSWITH map case by a simple mapping, which agrees with Java's full one over ASCII and leaves ß, U+FB00, U+017F, U+0130 and U+212A as they are. The emulator maps the last three.",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.CaseMappingIsSimpleAndAgreesWithCalcitesOverAscii"),
            new("Service.TrimRemovesWhiteSpace",
                "TRIM, LTRIM and RTRIM remove U+0020 and U+0009 to U+000D, U+0085, U+00A0, U+1680, U+180E, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F and U+3000, and nothing else in the basic plane, plane 1 or U+E0000 to U+E01EF, at either end. The emulator removes neither U+0085, U+00A0, U+180E, U+2007 nor U+202F. Read as data by the theory's trimmed class (JsonValues.IsTrimmed).",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.TrimRemovesWhiteSpace"),
            new("Service.LoneSurrogateParameterIsNeverSent",
                "A parameter holding a lone surrogate fails the statement before it is sent: the SDK's UTF-8 encoder refuses the unit (EncoderFallbackException), so it fails on either target where Calcite answers.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.ALoneSurrogateParameterFailsBeforeItIsSent"),
            new("Service.NulEndsWhatReplaceAndContainsRead",
                "REPLACE and CONTAINS, both modes, read a string only up to its first U+0000, and so does a LIKE whose pattern is one run of text between two %, which the service answers as a CONTAINS: b is not found in a, U+0000, b. A search holding U+0000 matches every string, and REPLACE of one replaces every byte. INDEX_OF, STARTSWITH, ENDSWITH, LENGTH, a LIKE of any other shape and a replacement holding U+0000 read past it. The emulator stores U+0000 as U+FDD0 followed by 0. Read by the theory's class of U+0000 (JsonCharacterBand.Nul).",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.NulEndsWhatReplaceAndContainsRead"),
            new("Service.ReplaceOfAnEmptySearchIsUndefined",
                "REPLACE with an empty search is undefined for every string, where Calcite's is the subject.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.AReplacementOfAnEmptySearchIsUndefined"),
            new("Service.SubstringOfANegativeStartIsEmpty",
                "SUBSTRING with a start below 0 answers the empty string, projected, where Calcite clips the window to the string; SUBSTRING(s, 0, 0) is the empty string, projected and in a WHERE.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.AWindowStartingBeforeTheStringIsEmpty"),
            new("Service.ReplicateStopsAtTenThousandCodePoints",
                "REPLICATE is undefined for a negative count, where Calcite's REPEAT is the empty string, and the empty string for a count of 0; and undefined where the result would pass 10,000 code points, which it reaches inclusive: ab 5,000 times is 10,000 and 5,001 times undefined, and a, U+1F600, b 3,333 times, 13,332 UTF-16 units, is defined. The emulator has no limit.",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.ARepetitionIsUndefinedForANegativeCountAndPastTenThousandCodePoints"),
            new("Service.StringFunctionAnswersByBand",
                "On the emulator each string function answers Calcite's over a string of ASCII alone, and parts from it over the bands its rule refuses there: the counts, SUBSTRING, LEFT, RIGHT and POSITION over a surrogate, U+0000 and the high band, which holds U+FDD0, counting the code points the emulator stores; UPPER over the first band and the high one, LOWER over the first; REPLACE and REVERSE over U+0000 and the high band; the trims over the trimmed class; CONCAT, || and REPEAT over none measured. On Azure each parts over fewer: the counts, SUBSTRING, LEFT and RIGHT over a surrogate, Azure counting code points and sending a split pair as U+FFFD; UPPER over the first band and the high one, LOWER over the first; REPLACE over U+0000; the trims over the trimmed class; RIGHT read as parting over a surrogate though no witness's last two units split a pair. Each is undefined over a number and a boolean, which Calcite's text accessor reads as text, and null over a null, an absent path, an object and an array, as the accessor reads them.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Cases, "Measurements.CosmosStringFunctionMeasurementTests.EachStringFunctionAnswersItsRowByBand",
                Readers: [PredicateAtom.TextReader, PredicateAtom.TypedReader("VARCHAR"), PredicateAtom.CastReader("VARCHAR", PredicateAtom.TextReader)],
                FunctionOutcomes:
                [
                    .. StringFunction("CHAR_LENGTH", 0, JsonCharacterBand.Surrogates | JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.Surrogates),
                    .. StringFunction("CHARACTER_LENGTH", 0, JsonCharacterBand.Surrogates | JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.Surrogates),
                    .. StringFunction("SUBSTRING", 0, JsonCharacterBand.Surrogates | JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.Surrogates),
                    .. StringFunction("LEFT", 0, JsonCharacterBand.Surrogates | JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.Surrogates),
                    .. StringFunction("RIGHT", 0, JsonCharacterBand.Surrogates | JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.Surrogates),
                    // The emulator's INDEX_OF counts code points, so POSITION parts over a surrogate as well (Service.IndexOfCountsUnits).
                    .. StringFunction("POSITION", 1, JsonCharacterBand.Surrogates | JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.None),
                    .. StringFunction("UPPER", 0, JsonCharacterBand.NonAscii | JsonCharacterBand.HighBmp, JsonCharacterBand.NonAscii | JsonCharacterBand.HighBmp),
                    .. StringFunction("LOWER", 0, JsonCharacterBand.NonAscii, JsonCharacterBand.NonAscii),
                    .. StringFunction("REPLACE", 0, JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.Nul),
                    .. StringFunction("REVERSE", 0, JsonCharacterBand.HighBmp | JsonCharacterBand.Nul, JsonCharacterBand.None),
                    .. StringFunction("TRIM BOTH", 1, JsonCharacterBand.Trimmed, JsonCharacterBand.Trimmed),
                    .. StringFunction("TRIM LEADING", 1, JsonCharacterBand.Trimmed, JsonCharacterBand.Trimmed),
                    .. StringFunction("TRIM TRAILING", 1, JsonCharacterBand.Trimmed, JsonCharacterBand.Trimmed),
                    .. StringFunction("||", FunctionKey.Every, JsonCharacterBand.None, JsonCharacterBand.None),
                    .. StringFunction("CONCAT", FunctionKey.Every, JsonCharacterBand.None, JsonCharacterBand.None),
                    .. StringFunction("REPEAT", 0, JsonCharacterBand.None, JsonCharacterBand.None),
                ]),
            new("Calcite.StringCountsAreInUnits",
                "CHAR_LENGTH, SUBSTRING and POSITION count UTF-16 units: a, U+1F600, b is 4 long, and SUBSTRING FROM 2 FOR 1 of it is the lone lead surrogate.",
                CosmosAxiomTarget.Calcite, Strings, "Measurements.CalciteStringFunctionMeasurementTests.TheCountsAreInUnits"),
            new("Calcite.CaseMappingIsJavasFullMapping",
                "UPPER and LOWER are Java's full case mapping: ß is SS, U+FB00 is FF, U+017F is S, U+0130 is i and U+0307, U+212A is k.",
                CosmosAxiomTarget.Calcite, Strings, "Measurements.CalciteStringFunctionMeasurementTests.CaseMappingIsJavasFullMapping"),
            new("Calcite.TrimRemovesSpacesAlone",
                "TRIM with no character named, and LTRIM and RTRIM, remove U+0020 alone: a tab, U+00A0 and U+3000 stay.",
                CosmosAxiomTarget.Calcite, Strings, "Measurements.CalciteStringFunctionMeasurementTests.TrimRemovesSpacesAlone"),
            new("Calcite.SubstringClipsAWindowBeforeTheString",
                "SUBSTRING clips a window that starts before the first character to the string: FROM 0 FOR 2 of abc is a, FROM -5 FOR 2 the empty string.",
                CosmosAxiomTarget.Calcite, Strings, "Measurements.CalciteStringFunctionMeasurementTests.AWindowBeforeTheFirstCharacterIsClipped"),
            new("Calcite.RepeatOfANegativeCountIsEmpty",
                "REPEAT of a count below one is the empty string, and nothing bounds the length of what it repeats.",
                CosmosAxiomTarget.Calcite, Strings, "Measurements.CalciteStringFunctionMeasurementTests.ARepetitionOfANegativeCountIsEmpty"),
            new("Calcite.ReplaceOfAnEmptySearchIsTheSubject",
                "REPLACE of an empty search is its subject.",
                CosmosAxiomTarget.Calcite, Strings, "Measurements.CalciteStringFunctionMeasurementTests.AReplacementOfAnEmptySearchIsTheSubject"),

            // ── The emulator's spelling of what it stores (EmulatorStoredText) ────────────────

            new("Service.EmulatorComparesTheSpellingItIsSent",
                "The emulator compares a parameter, a literal and a constant built from them as sent, unescaped, with a string as it stores it, U+0000 as U+FDD0 followed by 0 and U+FDD0 as U+FDD0 followed by 1: a value sent as written that holds either character equals no stored string, and one sent in the stored spelling is kept exactly where Calcite keeps it under =, IN, != and STARTSWITH, against a path or a function of one, and under ENDSWITH, CONTAINS and NOT CONTAINS unless it begins with 0 or 1, which an escape's second unit matches. CONTAINS of U+FDD0 followed by 0 is true exactly of the strings holding U+0000 (TheStoredSpellingOfU0000IsAnExactTest).",
                CosmosAxiomTarget.Emulator, Spelling, "Measurements.CosmosStringFunctionMeasurementTests.TheEmulatorComparesWhatItIsSentWithWhatItStores"),
            new("Service.EmulatorCountsTheStringItStores",
                "The emulator's LENGTH, SUBSTRING, LEFT, RIGHT, REVERSE and INDEX_OF work on a path's string as it stores it, U+0000 and U+FDD0 two units each, and what they return is decoded wherever a whole escape pair comes back: LENGTH of U+0000 is 2, LEFT of it by 1 a lone U+FDD0, its REVERSE 0 then U+FDD0.",
                CosmosAxiomTarget.Emulator, Spelling, "Measurements.CosmosStringFunctionMeasurementTests.TheEmulatorCountsAndCutsTheStringItStores"),
            new("Service.EmulatorFailsANulMadeFromAPath",
                "On the emulator a function that makes U+0000 from a path fails the statement with a 500: CONCAT of a path and U+0000, a literal or a parameter, and REPLACE whose replacement holds one wherever the search is found. A constant CONCAT of U+0000 loses it.",
                CosmosAxiomTarget.Emulator, Spelling, "Measurements.CosmosStringFunctionMeasurementTests.AFunctionThatMakesU0000FromAPathFails"),
            new("Service.EmulatorDecodesWhatItReturns",
                "The emulator returns a constant decoded as it returns a stored string, U+FDD0 followed by 0 as U+0000, U+FDD0 followed by 1 as U+FDD0, a lone U+FDD0 as itself: a constant sent in the stored spelling comes back as written, alone, in a conditional's arm, in a coalesce, as an object's or an array's member and inside an object sent whole, and a U+0000 sent as written fails the statement with a 500 wherever a value is returned.",
                CosmosAxiomTarget.Emulator, Spelling, "Measurements.CosmosStringFunctionMeasurementTests.AConstantTheEmulatorReturnsIsDecodedAsStored"),
            new("Service.EmulatorPointReadOfAnEscapedId",
                "On the emulator a point read of an id holding U+0000 is a 500 and of one holding U+FDD0 a 404, a point read of U+FDD0 followed by 0 answers the document whose id is U+0000, and ReadMany leaves out an id holding either beside other characters; a query of the id sent in the stored spelling finds its document exactly.",
                CosmosAxiomTarget.Emulator, Spelling, "Measurements.CosmosReadManyMeasurementTests.AnIdHoldingAnEscapeIsNotReadByItsId"),

            // ── What a document holds, read back (CosmosJson) ─────────────────────────────

            new("Service.IntegerOutsideLongIsStoredAsADouble",
                "An integer written outside int64 is stored as a double, which every query prints one way, the document read whole and the value projected alike: 9223372036854775808 comes back 9.223372036854776E+18, no BIGINT. The emulator keeps one written in [2^63, 2^64) as written, and prints it projected to 17 digits.",
                CosmosAxiomTarget.Azure, Stored, "Measurements.CosmosStoredValueMeasurementTests.AnIntegerOutsideLongIsStoredAsADouble"),
            new("Service.MalformedTextIsStoredAsWritten",
                "Bytes that are not UTF-8 in a document body, in a value or in a name, are stored as a client wrote them, returned as written by every query and by a point read, and compared as bytes: a WTF-8 surrogate, CESU-8, an overlong form, a stray byte. The emulator refuses the write.",
                CosmosAxiomTarget.Azure, Stored, "Measurements.CosmosStoredValueMeasurementTests.MalformedTextIsStoredAsWritten"),

            // ── The service's order (CosmosServiceOrder, CosmosContainerMetadata.IsSortSupported) ───

            new("Service.StringOrderIsCodePoint",
                "A document's strings are ordered, compared and extremed by code point, which is UTF-8 byte order, with no collation and no normalisation: U+E000 below U+1F600, which UTF-16 order puts above. The emulator stores U+0000 as U+FDD0 followed by 0 and U+FDD0 as U+FDD0 followed by 1 (EmulatorStoredText) and orders and compares what it stores, so it puts U+0000 above every unit from U+0001 to U+FDCF, U+E000 among them, and U+FDD0 where code point order does (TheEmulatorEscapesUFDD0Too); Azure orders and compares U+0000 by code point, the least, and reads past it.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.ADocumentsStringsOrderByCodePoint",
                [
                    new(Test.Order, Class.String, Emulator, "none=storedcodepoint"),
                    new(Test.Order, Class.String, Azure, "none=codepoint"),
                    new(Test.Greater, Class.String, Both, "below=false equal=false above=true"),
                ]),
            new("Service.LoneSurrogateIsStoredAsReplacement",
                "A lone surrogate written as a JSON escape is stored as U+FFFD, a unit of the high band, where a surrogate pair is stored as written; the emulator refuses the write. Read as data by the rules of JSON (CosmosAxiomRows.ReadStored, the rule of what a string written in some classes holds stored): a string written with a surrogate may be stored holding the high band and U+FFFD.",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.ALoneSurrogateIsStoredAsTheReplacementCharacter",
                StoredAs:
                [
                    new(JsonValues.SurrogateBand, JsonValues.HighBand),
                    new(JsonValues.SurrogateBand, JsonValues.ReplacementClass),
                ]),
            new("Service.ConstantStringComparisonIsByCodeUnit",
                "Two constants are compared by UTF-16 code unit on Azure, @a < @b false for U+E000 and U+1F600, where the emulator compares them by code point.",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.TwoConstantsCompareByUtf16"),
            new("Service.RangeOverPartedBoundsIsEmpty",
                "A filter c.s BETWEEN @lo AND @hi, or c.s >= @lo AND c.s <= @hi, whose bounds the two orders rank apart, U+E000 and U+1F600, keeps nothing on Azure, nor does its NOT, while the same predicate projected is true of the rows between them by code point. The emulator keeps the rows between them by what it stores, which is code point wherever no string holds U+0000, and NOT the rest, as it does between bounds the orders rank alike; over a value of another type, or none, the range is undefined.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.ARangeWhoseBoundsTheOrdersRankApartKeepsNothing",
                [
                    // In the spelling S reads, >= AND <=, which keeps what BETWEEN does on either target.
                    new(Test.Range, Class.Parted, Emulator, "between=true"),
                    new(Test.Range, Class.Parted, Azure, "between=undefined"),
                    new(Test.Range, Class.Parted, Both, "outside=false absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                    new(Test.Range, Class.Agreeing, Both, "between=true outside=false absent=undefined null=undefined true=undefined false=undefined number=undefined object=undefined array=undefined"),
                ]),
            new("Service.OrderByTypeOrder",
                "ORDER BY orders undefined, null, false, true, numbers, strings, arrays, objects, over every path, and DESC is the exact reverse.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.TheTypeOrderAndItsReverse"),
            new("Service.IntegersOrderExactly",
                "An int64 is held, compared and ordered exactly, past 2^53 where a double ties its neighbours, up to 2^63 - 1.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.IntegersOrderExactly",
                [
                    new(Test.Order, Class.Number, Both, "none=numeric"),
                    new(Test.Equal, Class.Number, Both, "below=false equal=true above=false"),
                ]),
            new("Service.OrderByOverAnExcludedPathFails",
                "ORDER BY over a path the indexing policy excludes is a 400, _etag among them under the default policy; a filter and MIN or MAX over it still answer.",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.AnOrderByOverAnExcludedPathFails"),
            new("Service.UnindexedComputedPropertyFailsToOrder",
                "ORDER BY over a computed property orders where an included path names it and is a 400 where none does; the emulator drops computed properties on create.",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.AnUnindexedComputedPropertyFailsToOrder"),
            new("Service.MultiKeyOrderByNeedsCompositeIndex",
                "A multi-key ORDER BY needs a composite index over exactly its paths whose directions match or are the exact reverse; without one it is a 400, a key repeated included.",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.ARepeatedKeyNeedsACompositeIndex"),
            new("Service.RankTakesNoDirection",
                "ORDER BY RANK returns the best match first and refuses ASC and DESC alike (SC2222).",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.RankTakesNoDirection"),
            new("Service.CrossPartitionOrderIsAMergeByCodeUnit",
                "Each physical partition orders strings by code point, and the SDK merges the partitions' streams by String.CompareOrdinal, UTF-16, the lower feed range first on a tie: a stream from two partitions may be in neither order.",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosOrderMergeMeasurementTests.ACrossPartitionOrderIsTheMergeOfCodePointRuns"),

            new("Service.ComputedOrderKeepsTheTypeOrder",
                "ORDER BY over a computed value, ST_DISTANCE undefined over a row, keeps the type order a path's has: undefined first ascending and last descending, DESC the exact reverse of ASC.",
                CosmosAxiomTarget.Azure, new(2026, 9, 20), "Measurements.CosmosGeographyValidityMeasurementTests.UndefinedSortsFirstAscendingAndLastDescending"),
            new("Service.SpatialOrderByTakesTheCallAlone",
                "ORDER BY takes a spatial call and nothing built around one: a conditional over it, or a second key beside it, fails the statement.",
                CosmosAxiomTarget.Azure, new(2026, 9, 20), "Measurements.CosmosGeographyValidityMeasurementTests.NeitherAConditionalNorASecondKeyIsAcceptedInTheClause"),
            new("Service.DistanceOrders",
                "The service orders by ST_DISTANCE, under a filter and a page as well.",
                CosmosAxiomTarget.Azure, new(2026, 9, 7), "Measurements.CosmosGeographyServiceMeasurementTests.ADistanceOrdersAtTheService"),
            new("Service.SpatialFormsExecute",
                "The spatial forms the translator emits execute: ST_DISTANCE projected and compared, ST_WITHIN, ST_INTERSECTS and ST_ISVALID.",
                CosmosAxiomTarget.Azure, new(2026, 9, 7), "Measurements.CosmosGeographyServiceMeasurementTests.TheEmittedFormsExecute"),
            new("Service.GeometryTypeIsSpelledAsJtsSpellsIt",
                "The type member of a stored shape is spelled as JTS spells it.",
                CosmosAxiomTarget.Azure, new(2026, 9, 7), "Measurements.CosmosGeographyServiceMeasurementTests.TheTypeMemberSpellsWhatJtsSpells"),
            new("Service.PlanarContainerAnswersInCoordinateUnits",
                "A container reading its coordinates as a plane answers a distance in coordinate units, and says nothing about having done so.",
                CosmosAxiomTarget.Azure, new(2026, 9, 7), "Measurements.CosmosGeographyServiceMeasurementTests.APlanarContainerAnswersInCoordinateUnits"),
        ];

    }

}
