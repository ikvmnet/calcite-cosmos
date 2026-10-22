using System;
using System.Collections.Generic;
using System.Linq;

using Class = Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;
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
    /// string functions and the stored values rest on a run of 2026-10-19, which has no record.
    /// </para>
    /// </remarks>
    internal static class CosmosAxioms
    {

        static readonly DateOnly Today = new(2026, 10, 15);

        static readonly DateOnly Orders = new(2026, 10, 16);

        static readonly DateOnly Stored = new(2026, 10, 19);

        static readonly DateOnly Strings = new(2026, 10, 19);

        static readonly DateOnly Family = new(2026, 10, 20);

        // A connective's operands that are no truth, each measured over its own witnesses; declared before the rows read it.
        static readonly Class[] NoTruth = [Class.Undefined, Class.Absent, Class.Null, Class.Number, Class.String, Class.Object, Class.Array];

        // An outcome's targets, those that ran its own witnesses: one alone where the two answered apart.
        const CosmosAxiomTarget Both = CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Emulator = CosmosAxiomTarget.Emulator, Azure = CosmosAxiomTarget.Azure;

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
                "A cast of the accessor to BOOLEAN parses the rendering: a stored boolean reads as itself, a null and an absent path as null, a string spelling a boolean parses, trimmed and in any case, and a number raises.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 9), "Measurements.CalciteJsonValueMeasurementTests.ACastToBooleanParsesTheRendering"),
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
                "A stored null and an absent path are SQL null in a VARIANT, an ANY and a VARCHAR column: unknown to = and <>, and true to IS NULL. A value of another type is unequal; a VARCHAR column fails the statement over anything but a string, ANY fails an ordering across types, and VARIANT has no ordering and is equal to a number boxed with its own SQL type alone.",
                CosmosAxiomTarget.Calcite, new(2026, 10, 20), "Measurements.CalciteVariantMeasurementTests.ARawNullIsSqlNullAndAnotherTypeIsUnequal"),
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
            // ── What a string function computes (CosmosStringFunctions) ─────────────────────

            new("Service.LengthCountsCodePoints",
                "LENGTH counts code points: a, U+1F600, b is 3, where Calcite's CHAR_LENGTH counts 4 UTF-16 units. Without a surrogate the two counts are one.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.LengthCountsCodePoints"),
            new("Service.SubstringCountsUnits",
                "SUBSTRING, LEFT and RIGHT count UTF-16 units, and a window whose edge splits a surrogate pair returns the half as U+FFFD, where Calcite returns a lone surrogate; without a surrogate they answer as Calcite's. The emulator counts code points.",
                CosmosAxiomTarget.Azure, Strings, "Measurements.CosmosStringFunctionMeasurementTests.SubstringLeftAndRightCountUnitsAndSendASplitPairAsReplacement"),
            new("Service.IndexOfCountsUnits",
                "INDEX_OF counts UTF-16 units, so INDEX_OF + 1 is Calcite's POSITION over every string: b in a, U+1F600, b is at 3. The emulator counts code points and answers 2.",
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

            // ── What a document holds, read back (CosmosJson) ─────────────────────────────

            new("Service.IntegerOutsideLongIsStoredAsADouble",
                "An integer written outside int64 is stored as a double, which every query prints one way, the document read whole and the value projected alike: 9223372036854775808 comes back 9.223372036854776E+18, no BIGINT. The emulator keeps one written in [2^63, 2^64) as written, and prints it projected to 17 digits.",
                CosmosAxiomTarget.Azure, Stored, "Measurements.CosmosStoredValueMeasurementTests.AnIntegerOutsideLongIsStoredAsADouble"),
            new("Service.MalformedTextIsStoredAsWritten",
                "Bytes that are not UTF-8 in a document body, in a value or in a name, are stored as a client wrote them, returned as written by every query and by a point read, and compared as bytes: a WTF-8 surrogate, CESU-8, an overlong form, a stray byte. The emulator refuses the write.",
                CosmosAxiomTarget.Azure, Stored, "Measurements.CosmosStoredValueMeasurementTests.MalformedTextIsStoredAsWritten"),

            // ── The service's order (CosmosServiceOrder, CosmosContainerMetadata.IsSortSupported) ───

            new("Service.StringOrderIsCodePoint",
                "A document's strings are ordered, compared and extremed by code point, which is UTF-8 byte order, with no collation and no normalisation: U+E000 below U+1F600, which UTF-16 order puts above. The emulator stores U+0000 as U+FDD0 followed by 0 and orders and compares what it stores, so it puts U+0000 above U+E000; Azure orders and compares U+0000 by code point, the least, and reads past it.",
                CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.ADocumentsStringsOrderByCodePoint",
                [
                    new(Test.Order, Class.String, Emulator, "none=storedcodepoint"),
                    new(Test.Order, Class.String, Azure, "none=codepoint"),
                    new(Test.Greater, Class.String, Both, "below=false equal=false above=true"),
                ]),
            new("Service.LoneSurrogateIsStoredAsReplacement",
                "A lone surrogate written as a JSON escape is stored as U+FFFD, a unit of the high band, where a surrogate pair is stored as written; the emulator refuses the write. Read as data by the theory's rule of what a string written in some bands holds stored (JsonValues.Stored).",
                CosmosAxiomTarget.Azure, Orders, "Measurements.CosmosServiceOrderMeasurementTests.ALoneSurrogateIsStoredAsTheReplacementCharacter"),
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
