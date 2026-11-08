using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The registry's one reader into the rendering graph: each atomic outcome of each row, one measured row per cell and
    /// per target it names. It merges nothing; which targets a key is settled under, and what two that differ mean
    /// together, are S's rules (<c>DESIGN.md</c>, <i>The registry, read into S</i>).
    /// </summary>
    internal static class CosmosAxiomRows
    {

        static readonly (CosmosAxiomTarget Flag, RenderingTarget Target)[] Targets =
            [(CosmosAxiomTarget.Emulator, RenderingTarget.Emulator), (CosmosAxiomTarget.Azure, RenderingTarget.Azure), (CosmosAxiomTarget.Calcite, RenderingTarget.Calcite)];

        /// <summary>One row per cell per target of every outcome that is no composite, citing its axiom.</summary>
        public static IReadOnlyList<MeasuredOutcome> Read(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. from axiom in axioms
               from outcome in axiom.Outcomes ?? []
               where outcome.Formula is null
               from target in Targets
               where outcome.On.HasFlag(target.Flag)
               from cell in outcome.Cells
               select new MeasuredOutcome(axiom.Id, MeasuredKey.Of(outcome.Test, outcome.Operand, cell.Cell, outcome.Band), target.Target, cell.Answer),
        ];

        /// <summary>
        /// One row per key per target of every function outcome: what the target computed against Calcite, which S reads as
        /// constant raw facts (<c>DESIGN.md</c>, <i>The rows the next families read, and function rows</i>). It merges nothing either.
        /// </summary>
        public static IReadOnlyList<(FunctionKey Key, RenderingTarget Target, CosmosFunctionAnswer Answer)> ReadFunctions(IEnumerable<CosmosAxiom> axioms) =>
            ReadFunctions(OutcomesOf(axioms));

        /// <summary>One row per key per target of each function outcome given: the axioms' and the declared rows' alike.</summary>
        public static IReadOnlyList<(FunctionKey Key, RenderingTarget Target, CosmosFunctionAnswer Answer)> ReadFunctions(IEnumerable<CosmosFunctionOutcome> outcomes) =>
        [
            .. from outcome in outcomes
               from target in Targets
               where outcome.On.HasFlag(target.Flag)
               select (KeyOf(outcome), target.Target, outcome.Answer),
        ];

        /// <summary>
        /// One row per key of each function outcome, with what Calcite answers over it, a value or null (G6), S's
        /// <c>functionyield</c>: what a call's image holds where its keys agree, as the row states it. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(FunctionKey Key, CalciteYield Yields)> ReadFunctionYields(IEnumerable<CosmosFunctionOutcome> outcomes) =>
            [.. (from outcome in outcomes select (KeyOf(outcome), outcome.Yields)).Distinct()];

        /// <summary>
        /// One row per name a function's rows are filed under, with the operator it is of and the SQL type it is keyed by,
        /// none where the rows are the function's alone (G6), S's <c>rowname</c> and <c>rownamedalone</c>: which name a call
        /// is keyed by is S's rule over these, never a list of the operators keyed by type.
        /// </summary>
        public static IReadOnlyList<(string Operator, string? Type, string Name)> ReadFunctionNames(IEnumerable<CosmosFunctionOutcome> outcomes) =>
            [.. (from outcome in outcomes select (outcome.Function, outcome.Type, outcome.Name)).Distinct()];

        /// <summary>Every function outcome of the axioms given.</summary>
        public static IEnumerable<CosmosFunctionOutcome> OutcomesOf(IEnumerable<CosmosAxiom> axioms) => axioms.SelectMany(a => a.FunctionOutcomes ?? []);

        static FunctionKey KeyOf(CosmosFunctionOutcome outcome) => FunctionKey.Of(outcome.Name, outcome.Subject, outcome.Operand, outcome.Band, outcome.Value);

        /// <summary>
        /// The function rows declared rather than measured (G6), read beside the axioms' wherever theirs are, as
        /// <see cref="Owed"/>'s cells are: S's image and the translator's column gate read them alike.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A function no run measured that a gate holds to its rows</b>, Calcite's library <c>LENGTH</c>: each key
        /// <see cref="CosmosFunctionAnswer.Unmeasured"/> on both targets, so that it is read as unmeasured rather than as
        /// unnamed, and no reader it was measured through, so that no image and no column is written of it. Its spelling is
        /// the service's <c>LENGTH</c>, which counts what <c>CHAR_LENGTH</c>'s rows say it counts; what Calcite's counts in
        /// each library has not been measured beside it.
        /// </para>
        /// <para>
        /// <b>A function the service defines alone</b>, the D class, <c>ToString</c>: one of this adapter's own operators,
        /// which Calcite has no body for, so what the service answers is what it means. Each key is
        /// <see cref="CosmosFunctionAnswer.Defined"/> on both targets over every kind and band, and Calcite's call null over
        /// any, the service's undefined being its answer too: S images it as every kind of its type and absent, whatever
        /// it reads its operand through, and the gate passes it (F3).
        /// </para>
        /// </remarks>
        public static IReadOnlyList<CosmosFunctionOutcome> Declared { get; } =
        [
            .. from kind in new[] { CosmosValueClass.String, CosmosValueClass.Number, CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Null,
                   CosmosValueClass.Absent, CosmosValueClass.Object, CosmosValueClass.Array }
               select new CosmosFunctionOutcome("LENGTH", FunctionKey.Every, kind, JsonCharacterBand.None, CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure,
                   CosmosFunctionAnswer.Unmeasured, CalciteYield.Null),
            .. from kind in new[] { CosmosValueClass.Number, CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Null,
                   CosmosValueClass.Absent, CosmosValueClass.Object, CosmosValueClass.Array }
               select new CosmosFunctionOutcome("ToString", FunctionKey.Every, kind, JsonCharacterBand.None, CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure,
                   CosmosFunctionAnswer.Defined, CalciteYield.Null),
            .. from band in JsonValues.CharacterClasses.Where(c => c.Flag == c.Within).Select(c => c.Flag).Prepend(JsonCharacterBand.None)
               select new CosmosFunctionOutcome("ToString", FunctionKey.Every, CosmosValueClass.String, band, CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure,
                   CosmosFunctionAnswer.Defined, CalciteYield.Null),
        ];

        /// <summary>
        /// One row per class a <c>Service</c> row says a string written in another is stored holding, as the rules of JSON
        /// read it: they have no target, so a row names none, and the union of the rows is what they hold. It merges nothing.
        /// </summary>
        public static IReadOnlyList<JsonClassMap> ReadStored(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                from stored in axiom.StoredAs ?? []
                select new JsonClassMap(stored.Written, stored.Stored)).Distinct(),
        ];

        /// <summary>
        /// One row per class a <c>Service</c> row says a string written in another is stored holding, on each target that
        /// stored it so (F3): S's <c>storedas</c>, which names the target the rules of JSON leave out. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, string Written, string Stored)> ReadStoredOn(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                from stored in axiom.StoredAs ?? []
                from target in Targets
                where axiom.MeasuredOn.HasFlag(target.Flag)
                select (target.Target, stored.Written, stored.Stored)).Distinct(),
        ];

        /// <summary>One row per pair of bands a comparator ranks apart from Calcite's order (F3), S's <c>parts</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Band, string Other)> ReadParts(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from part in axiom.Parts ?? [] select (part.Comparator, part.Band, part.Other)).Distinct()];

        /// <summary>One row per band a comparator's spelling rewrites (G1), S's <c>rewrites</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Band)> ReadRewrites(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from rewrite in axiom.Rewrites ?? [] select (rewrite.Comparator, rewrite.Band)).Distinct()];

        /// <summary>
        /// One row per class a parameter holding a character of is never sent, on each target the row was measured on (G1),
        /// S's <c>unsent</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, string Class)> ReadUnsent(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                from @class in axiom.Unsent ?? []
                from target in Targets
                where axiom.MeasuredOn.HasFlag(target.Flag)
                select (target.Target, @class)).Distinct(),
        ];

        /// <summary>One row per way a comparator reads a whole number within int64 (G2), S's <c>wholereads</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Reading)> ReadWholeReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from reading in axiom.WholeReadings ?? [] select (reading.Comparator, reading.Reading)).Distinct()];

        /// <summary>One row per band a comparator moves in order (F3), S's <c>moves</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Band)> ReadMoves(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from move in axiom.Moves ?? [] select (move.Comparator, move.Band)).Distinct()];

        /// <summary>
        /// One row per target a test reads a string to a band's first character on, for an argument of a shape (F3), S's
        /// <c>truncates</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, CosmosServiceTest Test, string Shape, string Band)> ReadTruncations(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                from truncation in axiom.Truncations ?? []
                from target in Targets
                where truncation.On.HasFlag(target.Flag)
                select (target.Target, truncation.Test, truncation.Shape, truncation.Band)).Distinct(),
        ];

        /// <summary>
        /// One row per target a test's case-insensitive form matches as the test over a fold on, and each band it says nothing
        /// of, by member (G6), S's <c>foldsas</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, CosmosServiceTest Test, string Function, string Band)> ReadFolds(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                from fold in axiom.Folds ?? []
                from target in Targets
                where fold.On.HasFlag(target.Flag)
                from band in JsonValues.ClassSet(fold.Band).AsEnumerable()
                select (target.Target, fold.Test, fold.Function, band)).Distinct(),
        ];

        /// <summary>One row per way a comparison of two paths reads against a literal, the mirror law (F3), S's <c>pairreads</c>.</summary>
        public static IReadOnlyList<(CosmosServiceTest Test, bool Swapped, CosmosServiceTest Reads)> ReadPairReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from reading in axiom.PairReadings ?? [] select (reading.Test, reading.Swapped, reading.Reads)).Distinct()];

        /// <summary>One row per reading or kind of value and the box it is held in cast to <c>VARIANT</c> (F3), S's <c>boxof</c>.</summary>
        public static IReadOnlyList<(string Kind, string Box)> ReadBoxes(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from box in axiom.Boxes ?? [] select (box.Kind, box.Box)).Distinct()];

        /// <summary>One row per comparison that reads the values inside one box (F3), S's <c>seesthrough</c>.</summary>
        public static IReadOnlyList<string> ReadSeesThrough(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from op in axiom.SeesThrough ?? [] select op).Distinct()];

        /// <summary>One row per function that answers a structure alone (F3), S's <c>structureonly</c>.</summary>
        public static IReadOnlyList<string> ReadStructureOnly(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from function in axiom.StructureOnly ?? [] select function).Distinct()];

        /// <summary>
        /// One row per reading and kind of stored value a <c>Calcite</c> row measured in process, with what it answers (F3),
        /// S's <c>calcitereads</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(string Reader, CosmosValueClass Kind, string Answer)> ReadCalciteReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from reading in axiom.CalciteReadings ?? [] select (reading.Reader, reading.Kind, reading.Answer)).Distinct()];

        /// <summary>
        /// One row per character class the witnesses of a reading's kind held (F3), S's <c>calcitebands</c>, whose union is
        /// what was measured: what lies beyond it was not.
        /// </summary>
        public static IReadOnlyList<(string Reader, CosmosValueClass Kind, string Band)> ReadCalciteBands(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from reading in axiom.CalciteReadings ?? [] from band in reading.Bands ?? [] select (reading.Reader, reading.Kind, band)).Distinct()];

        /// <summary>One row per cast a <c>Calcite</c> row measured, by its target and the reading cast, citing the row (F3), S's <c>measuredcast</c>.</summary>
        public static IReadOnlyList<(string Target, string Reader, string Row)> ReadCasts(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from cast in axiom.Casts ?? [] select (cast.Target, cast.Reader, axiom.Id)).Distinct()];

        /// <summary>
        /// One row per reading that compares a number Calcite wrote cast with the number it denotes, moved or not (G8), S's
        /// <c>takesconstant</c>, citing its row.
        /// </summary>
        public static IReadOnlyList<(string Reading, bool Moved, string Row)> ReadComparands(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from comparand in axiom.Comparands ?? [] select (comparand.Reading, comparand.Moved, axiom.Id)).Distinct()];

        /// <summary>One row per claim a reading's order holds in process (F3), S's <c>readerorder</c>, whose union is a reading's.</summary>
        public static IReadOnlyList<(string Reader, string Claim)> ReadReaderOrders(IEnumerable<CosmosAxiom> axioms) =>
            [.. (from axiom in axioms from order in axiom.ReaderOrders ?? [] select (order.Reader, order.Claim)).Distinct()];

        /// <summary>
        /// One row per class a one-place test a <c>Calcite</c> row measured is true of (F3), S's <c>testkinds</c>: what a type
        /// test is in process, its body's row (<c>Calcite.TypeTestsAreTheirBodies</c>), each cell answered as the vector says.
        /// </summary>
        public static IReadOnlyList<(CosmosServiceTest Test, CosmosValueClass Kind)> ReadTestKinds(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                where axiom.MeasuredOn == CosmosAxiomTarget.Calcite
                from outcome in axiom.Outcomes ?? []
                where outcome.Formula is null && outcome.Operand == CosmosValueClass.None
                from cell in outcome.Cells
                where cell.Answer == CosmosAnswer.True
                select (outcome.Test, cell.Cell)).Distinct(),
        ];

        /// <summary>
        /// One row per operand a function outcome is of, each as its row names it (F2): S's union gathers a function's, so
        /// no reader picks one. It merges nothing either.
        /// </summary>
        public static IReadOnlyList<(string Function, long Subject)> ReadFunctionSubjects(IEnumerable<CosmosAxiom> axioms) => ReadFunctionSubjects(OutcomesOf(axioms));

        /// <summary>One row per operand each function outcome given is of, by the name its rows are filed under.</summary>
        public static IReadOnlyList<(string Function, long Subject)> ReadFunctionSubjects(IEnumerable<CosmosFunctionOutcome> outcomes) =>
            [.. (from outcome in outcomes select (outcome.Name, outcome.Subject)).Distinct()];

        /// <summary>
        /// One row per function an axiom's rows compute: the readers its witnesses were read through, which S reads as
        /// constant raw facts beside the keys and meets (<c>measuredreaders</c>, an intersection), so two axioms of one
        /// function each give theirs and no reader picks one.
        /// </summary>
        /// <remarks>
        /// Beside the text, each raw column that reads a string as itself and raises over every other kind that is no null
        /// (<c>Calcite.RawNullIsSqlNull</c>'s <c>VARCHAR</c>), read through that column (rawtyped): over a string it hands the
        /// function what the text hands it (<c>Calcite.JsonValueRendersScalarsAsText</c>), and a null as the text does,
        /// and over anything else Calcite raises, which any answer refines.
        /// </remarks>
        public static IReadOnlyList<(string Function, IReadOnlyList<string> Readers)> ReadFunctionReads(IEnumerable<CosmosAxiom> axioms)
        {
            var all = axioms.ToList();
            var asText = (from axiom in all
                          where axiom.MeasuredOn == CosmosAxiomTarget.Calcite
                          from column in axiom.Columns ?? []
                          where CosmosRawColumn.Values.All(kind => column.Read[kind] == (kind == CosmosRawColumn.String ? CosmosRawColumn.Answer.Itself : CosmosRawColumn.Answer.Raises))
                          select PredicateAtom.RawIn(column.Name)).ToList();
            return
            [
                .. from axiom in all
                   let readers = axiom.Readers ?? []
                   from function in (axiom.FunctionOutcomes ?? []).Select(o => o.Name).Distinct()
                   select (function, (IReadOnlyList<string>)(readers.Contains(PredicateAtom.TextReader) ? [.. readers, .. asText] : readers)),
            ];
        }

        /// <summary>
        /// What each raw column a <c>Calcite</c> row states answers on each key, one row a column a key, whose union S's rule
        /// takes (h12): the raw reading's comparisons beside a literal of each class and its null test, and those of its cast
        /// to text, each its column's answer for the kind. A column comparing the value as its type answers TRUE or FALSE,
        /// which the cell decides; across types an equality is unequal and an ordering raises; a column with no ordering
        /// raises on every one; a null is SQL null (<c>Calcite.RawNullIsSqlNull</c>, <c>Calcite.CastOfARawValueToText</c>).
        /// </summary>
        public static IReadOnlyList<(RawKey Key, string Column, Truths Truths)> ReadColumns(IEnumerable<CosmosAxiom> axioms) =>
            [.. from axiom in axioms where axiom.MeasuredOn == CosmosAxiomTarget.Calcite from column in axiom.Columns ?? [] from row in Rows(column) select (row.Key, column.Name, row.Truths)];

        static readonly CosmosValueClass[] Kinds = [CosmosValueClass.String, CosmosValueClass.Number, CosmosValueClass.True, CosmosValueClass.False,
            CosmosValueClass.Object, CosmosValueClass.Array, CosmosValueClass.Null];

        static readonly CosmosServiceTest[] Comparisons = [CosmosServiceTest.Equal, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater,
            CosmosServiceTest.GreaterOrEqual];

        static IEnumerable<(RawKey Key, Truths Truths)> Rows(CosmosRawColumn column)
        {
            const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;
            var cast = PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader);
            foreach (var kind in Kinds)
            {
                var (read, text) = (column.Read[KindOf(kind)], column.AsText[KindOf(kind)]);
                yield return (RawKey.Of(PredicateAtom.RawReader, kind, CosmosServiceTest.IsNull, CosmosValueClass.None), Answered(read, F, T));
                yield return (RawKey.Of(cast, kind, CosmosServiceTest.IsNull, CosmosValueClass.None), Answered(text, F, T));
                foreach (var test in Comparisons)
                {
                    foreach (var operand in Kinds.Where(o => column.BesideStringsAlone == false || o == CosmosValueClass.String))
                    {
                        var key = RawKey.Of(PredicateAtom.RawReader, kind, test, operand);
                        yield return (key, test != CosmosServiceTest.Equal && column.Orders == false ? R : Answered(read, key.Across ? test == CosmosServiceTest.Equal ? F : R : T | F, U));
                    }

                    yield return (RawKey.Of(cast, kind, test, CosmosValueClass.String), Answered(text, T | F, U));
                }
            }
        }

        static Truths Answered(CosmosRawColumn.Answer answer, Truths itself, Truths nulled) =>
            answer switch { CosmosRawColumn.Answer.Itself => itself, CosmosRawColumn.Answer.Null => nulled, _ => Truths.Raises };

        /// <summary>The column's kind of a class, a stored null's its own entry (F3).</summary>
        static string KindOf(CosmosValueClass kind) => kind switch
        {
            CosmosValueClass.String => CosmosRawColumn.String,
            CosmosValueClass.Number => CosmosRawColumn.Number,
            CosmosValueClass.True or CosmosValueClass.False => CosmosRawColumn.Boolean,
            CosmosValueClass.Object or CosmosValueClass.Array => CosmosRawColumn.Structure,
            CosmosValueClass.Null => CosmosRawColumn.Null,
            CosmosValueClass.Absent => CosmosRawColumn.Absent,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A column answers for a stored value's kind alone."),
        };

        /// <summary>
        /// The cells of a key some outcome answers for a target that no outcome covers for it: unmeasured there, so no
        /// row, and S reads the service as answering anything. Declared, so that a cell is owed on purpose and one
        /// dropped from a vector is caught. The probe account has run every witness the emulator runs (D7); what is owed is
        /// the emulator's matcher for LIKE, whose reading of a stored escape no statement has measured, so S's LIKE form
        /// answers anything over a string there (h07, <c>TODO.md</c>).
        /// </summary>
        public static IReadOnlyList<(MeasuredKey Key, RenderingTarget Target)> Owed { get; } =
            [(MeasuredKey.Of(CosmosServiceTest.Like, CosmosValueClass.String, CosmosValueClass.None), RenderingTarget.Emulator)];

    }

}
