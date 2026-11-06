using System;
using System.Collections.Generic;
using System.Linq;

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
        [
            .. from axiom in axioms
               from outcome in axiom.FunctionOutcomes ?? []
               from target in Targets
               where outcome.On.HasFlag(target.Flag)
               select (FunctionKey.Of(outcome.Function, outcome.Subject, outcome.Operand, outcome.Band, outcome.Value), target.Target, outcome.Answer),
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
        public static IReadOnlyList<(string Function, long Subject)> ReadFunctionSubjects(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. (from axiom in axioms
                from outcome in axiom.FunctionOutcomes ?? []
                select (outcome.Function, outcome.Subject)).Distinct(),
        ];

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
                   from function in (axiom.FunctionOutcomes ?? []).Select(o => o.Function).Distinct()
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
