using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

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
            .. axioms.SelectMany(axiom => (axiom.Outcomes ?? [])
                .Where(outcome => outcome.Formula is null)
                .SelectMany(outcome => Targets
                    .Where(target => outcome.On.HasFlag(target.Flag))
                    .SelectMany(target => outcome.Cells
                        .Select(cell => new MeasuredOutcome(
                            axiom.Id, MeasuredKey.Of(outcome.Test, outcome.Operand, cell.Cell, outcome.Band),
                            target.Target, cell.Answer))))),
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
            .. outcomes.SelectMany(outcome => Targets
                .Where(target => outcome.On.HasFlag(target.Flag))
                .Select(target => (KeyOf(outcome), target.Target, outcome.Answer))),
        ];

        /// <summary>
        /// One row per key of each function outcome, with what Calcite answers over it, a value or null (G6), S's
        /// <c>functionyield</c>: what a call's image holds where its keys agree, as the row states it. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(FunctionKey Key, CalciteYield Yields)> ReadFunctionYields(IEnumerable<CosmosFunctionOutcome> outcomes) =>
            [.. outcomes.Select(outcome => (KeyOf(outcome), outcome.Yields)).Distinct()];

        /// <summary>
        /// One row per name a function's rows are filed under, with the operator it is of and the SQL type it is keyed by,
        /// none where the rows are the function's alone (G6), S's <c>rowname</c> and <c>rownamedalone</c>: which name a call
        /// is keyed by is S's rule over these, never a list of the operators keyed by type.
        /// </summary>
        public static IReadOnlyList<(string Operator, string? Type, string Name)> ReadFunctionNames(IEnumerable<CosmosFunctionOutcome> outcomes) =>
            [.. outcomes.Select(outcome => (outcome.Function, outcome.Type, outcome.Name)).Distinct()];

        /// <summary>Every function outcome of the axioms given.</summary>
        public static IEnumerable<CosmosFunctionOutcome> OutcomesOf(IEnumerable<CosmosAxiom> axioms) => axioms.SelectMany(a => a.FunctionOutcomes ?? []);

        static FunctionKey KeyOf(CosmosFunctionOutcome outcome) => FunctionKey.Of(outcome.Name, outcome.Subject, outcome.Operand, outcome.Band, outcome.Value);

        /// <summary>
        /// The function rows declared rather than measured (G6), read beside the axioms' wherever theirs are, as
        /// <see cref="Owed"/>'s cells are: S's image and the translator's column gate read them alike.
        /// </summary>
        /// <remarks>
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
            .. new[] { CosmosValueClass.Number, CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Null,
                    CosmosValueClass.Absent, CosmosValueClass.Object, CosmosValueClass.Array }
                .Select(kind => new CosmosFunctionOutcome("ToString", FunctionKey.Every, kind, JsonCharacterBand.None,
                    CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, CosmosFunctionAnswer.Defined, CalciteYield.Null)),
            .. JsonValues.CharacterClasses
                .Where(c => c.Flag == c.Within)
                .Select(c => c.Flag)
                .Prepend(JsonCharacterBand.None)
                .Select(band => new CosmosFunctionOutcome("ToString", FunctionKey.Every, CosmosValueClass.String, band,
                    CosmosAxiomTarget.Emulator | CosmosAxiomTarget.Azure, CosmosFunctionAnswer.Defined, CalciteYield.Null)),
        ];

        /// <summary>
        /// One row per class a <c>Service</c> row says a string written in another is stored holding, as the rules of JSON
        /// read it: they have no target, so a row names none, and the union of the rows is what they hold. It merges nothing.
        /// </summary>
        public static IReadOnlyList<JsonClassMap> ReadStored(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms.SelectMany(axiom => axiom.StoredAs ?? [])
                .Select(stored => new JsonClassMap(stored.Written, stored.Stored))
                .Distinct(),
        ];

        /// <summary>
        /// One row per class a <c>Service</c> row says a string written in another is stored holding, on each target that
        /// stored it so (F3): S's <c>storedas</c>, which names the target the rules of JSON leave out. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, string Written, string Stored)> ReadStoredOn(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms
                .SelectMany(axiom => (axiom.StoredAs ?? [])
                    .SelectMany(stored => Targets
                        .Where(target => axiom.MeasuredOn.HasFlag(target.Flag))
                        .Select(target => (target.Target, stored.Written, stored.Stored))))
                .Distinct(),
        ];

        /// <summary>One row per pair of bands a comparator ranks apart from Calcite's order (F3), S's <c>parts</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Band, string Other)> ReadParts(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Parts ?? [])
                .Select(part => (part.Comparator, part.Band, part.Other))
                .Distinct()];

        /// <summary>One row per band a comparator's spelling rewrites (G1), S's <c>rewrites</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Band)> ReadRewrites(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Rewrites ?? [])
                .Select(rewrite => (rewrite.Comparator, rewrite.Band))
                .Distinct()];

        /// <summary>
        /// One row per class a parameter holding a character of is never sent, on each target the row was measured on (G1),
        /// S's <c>unsent</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, string Class)> ReadUnsent(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms
                .SelectMany(axiom => (axiom.Unsent ?? [])
                    .SelectMany(@class => Targets
                        .Where(target => axiom.MeasuredOn.HasFlag(target.Flag))
                        .Select(target => (target.Target, @class))))
                .Distinct(),
        ];

        /// <summary>One row per way a comparator reads a whole number within int64 (G2), S's <c>wholereads</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Reading)> ReadWholeReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.WholeReadings ?? [])
                .Select(reading => (reading.Comparator, reading.Reading))
                .Distinct()];

        /// <summary>One row per band a comparator moves in order (F3), S's <c>moves</c>. It merges nothing.</summary>
        public static IReadOnlyList<(CosmosAnswer Comparator, string Band)> ReadMoves(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Moves ?? []).Select(move => (move.Comparator, move.Band)).Distinct()];

        /// <summary>
        /// One row per target a test reads a string to a band's first character on, for an argument of a shape (F3), S's
        /// <c>truncates</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, CosmosServiceTest Test, string Shape, string Band)> ReadTruncations(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms.SelectMany(axiom => axiom.Truncations ?? [])
                .SelectMany(truncation => Targets
                    .Where(target => truncation.On.HasFlag(target.Flag))
                    .Select(target => (target.Target, truncation.Test, truncation.Shape, truncation.Band)))
                .Distinct(),
        ];

        /// <summary>
        /// One row per target a test's case-insensitive form matches as the test over a fold on, and each band it says nothing
        /// of, by member (G6), S's <c>foldsas</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, CosmosServiceTest Test, string Function, string Band)> ReadFolds(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms.SelectMany(axiom => axiom.Folds ?? [])
                .SelectMany(fold => Targets
                    .Where(target => fold.On.HasFlag(target.Flag))
                    .SelectMany(target => JsonValues.ClassSet(fold.Band).AsEnumerable()
                        .Select(band => (target.Target, fold.Test, fold.Function, band))))
                .Distinct(),
        ];

        /// <summary>One row per way a comparison of two paths reads against a literal, the mirror law (F3), S's <c>pairreads</c>.</summary>
        public static IReadOnlyList<(CosmosServiceTest Test, bool Swapped, CosmosServiceTest Reads)> ReadPairReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.PairReadings ?? [])
                .Select(reading => (reading.Test, reading.Swapped, reading.Reads))
                .Distinct()];

        /// <summary>One row per reading or kind of value and the box it is held in cast to <c>VARIANT</c> (F3), S's <c>boxof</c>.</summary>
        public static IReadOnlyList<(string Kind, string Box)> ReadBoxes(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Boxes ?? []).Select(box => (box.Kind, box.Box)).Distinct()];

        /// <summary>One row per comparison that reads the values inside one box (F3), S's <c>seesthrough</c>.</summary>
        public static IReadOnlyList<string> ReadSeesThrough(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.SeesThrough ?? []).Distinct()];

        /// <summary>One row per SQL type whose box is null where what it holds is, each measured apart (G8), S's <c>boxnull</c>.</summary>
        public static IReadOnlyList<string> ReadNullThrough(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.NullThrough ?? []).Distinct()];

        /// <summary>
        /// One row per reading and kind of stored value a <c>Calcite</c> row measured in process, with what it answers (F3),
        /// S's <c>calcitereads</c>. It merges nothing.
        /// </summary>
        public static IReadOnlyList<(string Reader, CosmosValueClass Kind, string Answer)> ReadCalciteReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.CalciteReadings ?? [])
                .Select(reading => (reading.Reader, reading.Kind, reading.Answer))
                .Distinct()];

        /// <summary>
        /// One row per character class the witnesses of a reading's kind held (F3), S's <c>calcitebands</c>, whose union is
        /// what was measured: what lies beyond it was not.
        /// </summary>
        public static IReadOnlyList<(string Reader, CosmosValueClass Kind, string Band)> ReadCalciteBands(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.CalciteReadings ?? [])
                .SelectMany(reading => (reading.Bands ?? []).Select(band => (reading.Reader, reading.Kind, band)))
                .Distinct()];

        /// <summary>
        /// One row per reading a <c>Calcite</c> row names, by its readings, the readings it reads as another and the tests it
        /// takes through, beside the row (G7): what a reading's answers rest on, <c>calciterows</c>.
        /// </summary>
        public static IReadOnlyList<(string Reader, string Row)> ReadCalciteRows(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms
                .SelectMany(axiom => (axiom.CalciteReadings ?? []).Select(r => r.Reader)
                    .Concat((axiom.ReadAs ?? []).Select(r => r.Reader))
                    .Concat((axiom.Through ?? []).Select(r => r.Reader))
                    .Select(reader => (reader, axiom.Id)))
                .Distinct(),
        ];

        /// <summary>Every <c>Calcite</c> row of readings, as the readings vocabulary of either graph reads them (G7).</summary>
        public static CalciteRows ReadCalcite(IEnumerable<CosmosAxiom> axioms) =>
            new(ReadCalciteReadings(axioms), ReadCalciteBands(axioms), ReadCalciteRows(axioms), ReadCalciteReadAs(axioms), ReadCalciteThrough(axioms), ReadCalciteEvaluators(axioms),
                ReadLikeUnits(axioms), ReadTestKinds(axioms),
                ReadTestReads(axioms), ReadExactReadings(axioms), ReadBoxes(axioms), ReadSeesThrough(axioms), ReadNullThrough(axioms), ReadComparands(axioms),
                Metadata.CosmosStrictness.Operators, ReadCasts(axioms), ReadAsOfAPath(axioms), ReadYieldKinds(axioms), ReadYieldReads(axioms), ReadColumns(axioms),
                CosmosValueClasses.Types, ReadParametersAs(axioms));

        /// <summary>
        /// One row per reading a <c>Calcite</c> row says an index reads as of a path holding the element, a test's body over it
        /// among it, both graphs' <c>asofapath</c>: which readings of an element the predicate vocabulary reads as a path's.
        /// </summary>
        public static IReadOnlyList<string> ReadAsOfAPath(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.AsOfAPath ?? []).Distinct()];

        /// <summary>
        /// One row per element type a <c>Calcite</c> row measured a traversal of, the traversal's form, and each JSON kind of
        /// each class it yields an element of (<see cref="CosmosValueClasses.JsonKinds"/>), both graphs' <c>yieldkinds</c>: what
        /// a yielded element holds.
        /// </summary>
        public static IReadOnlyList<(string Type, string Form, string Kind)> ReadYieldKinds(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Yields ?? [])
                .SelectMany(y => y.Classes
                    .SelectMany(c => CosmosValueClasses.JsonKinds
                        .Where(k => k.Class == c)
                        .Select(k => (y.Type, y.Form, k.Kind))))
                .Distinct()];

        /// <summary>
        /// One row per element type a <c>Calcite</c> row measured a traversal of, the traversal's form, and the reading its
        /// elements read as, both graphs' <c>yieldreads</c>.
        /// </summary>
        public static IReadOnlyList<(string Type, string Form, string Reader)> ReadYieldReads(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Yields ?? []).Select(y => (y.Type, y.Form, y.Reader)).Distinct()];

        /// <summary>
        /// One row per raw column a <c>Calcite</c> row says compares with a parameter as another column does, by its class, both
        /// graphs' <c>parameteras</c>: what a column no literal's row measured answers beside a parameter.
        /// </summary>
        public static IReadOnlyList<(string Column, string As)> ReadParametersAs(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.ParametersAs ?? []).Distinct()];

        /// <summary>
        /// One row per reading the facts graph reads as Calcite does beside a literal of a class, both graphs'
        /// <c>readsexactly</c>: which strata a statement's later rounds may rest on (<see cref="CosmosExactReading"/>).
        /// </summary>
        public static IReadOnlyList<(string Reader, CosmosValueClass Beside)> ReadExactReadings(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.ExactReadings ?? [])
                .Select(exact => (exact.Reader, exact.Beside))
                .Distinct()];

        /// <summary>
        /// One row per reading a one-place test's in-process body takes apart, with the reading it reads instead (G8), both
        /// graphs' <c>testreads</c>: what the predicate vocabulary's type-test and definedness rules read a test's value by.
        /// </summary>
        public static IReadOnlyList<(string Reader, string As)> ReadTestReads(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.TestReads ?? []).Select(read => (read.Reader, read.As)).Distinct()];

        /// <summary>One row per unit of a pattern a <c>Calcite</c> row says <c>LIKE</c> reads, with what it reads it as, citing the row (G7), <c>likeunits</c>.</summary>
        public static IReadOnlyList<(LikeUnit Unit, string Row)> ReadLikeUnits(IEnumerable<CosmosAxiom> axioms) =>
            [.. ReadLikeUnitsOn(axioms).Where(u => u.Target == RenderingTarget.Calcite).Select(u => (u.Unit, u.Row))];

        /// <summary>
        /// One row per unit of a pattern a <c>Service</c> row says a target's <c>LIKE</c> reads, with what it reads it as,
        /// citing the row (G7), S's <c>likereads</c>.
        /// </summary>
        public static IReadOnlyList<(RenderingTarget Target, LikeUnit Unit, string Row)> ReadServiceLikeUnits(IEnumerable<CosmosAxiom> axioms) =>
            [.. ReadLikeUnitsOn(axioms).Where(u => u.Target != RenderingTarget.Calcite)];

        /// <summary>
        /// Each target's units, as rows, on the targets each names, its row's by default: the unit spelled empty reading every
        /// unit the target's other rows name not, which its row carries as data (<see cref="LikeUnit.Besides"/>), so a unit
        /// is read by exactly one row of a target.
        /// </summary>
        static IEnumerable<(RenderingTarget Target, LikeUnit Unit, string Row)> ReadLikeUnitsOn(IEnumerable<CosmosAxiom> axioms)
        {
            var rows = axioms
                .SelectMany(axiom => (axiom.LikeUnits ?? [])
                    .SelectMany(unit => Targets
                        .Where(target => (unit.On ?? axiom.MeasuredOn).HasFlag(target.Flag)
                            && axiom.MeasuredOn.HasFlag(target.Flag))
                        .Select(target => (target.Target, unit, axiom.Id))))
                .ToList();
            return rows.Select(row =>
            {
                var named = row.Item2.Unit.Length > 0 ? ValueSet<string>.Empty
                    : ValueSet.From(rows.Where(o => o.Item1 == row.Item1 && o.Item2.Unit.Length > 0).Select(o => o.Item2.Unit));
                var over = row.Item2.Over is { } classes ? ValueSet.From(classes) : null;
                return (row.Item1, new LikeUnit(row.Item2.Unit, row.Item2.Reads, row.Item2.Except ?? "", over, named), row.Item3);
            }).Distinct();
        }

        /// <summary>One row per reading a <c>Calcite</c> row says is read as another over each class both read alike (G7), <c>calcitereadas</c>.</summary>
        public static IReadOnlyList<(string Reader, string As)> ReadCalciteReadAs(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.ReadAs ?? []).Select(read => (read.Reader, read.As)).Distinct()];

        /// <summary>
        /// One row per evaluator a <c>Calcite</c> row names for a reading, with the SQL type it evaluates a value of and the kinds
        /// of value Calcite's type system says that type holds (<see cref="Metadata.CosmosRendering.ReadTypeKinds"/>, the one
        /// reader of those families), citing the row (G7), <c>calciteevaluator</c>: which evaluator reads the reading's rows, and
        /// the rows that say so. It merges no rows and chooses nothing.
        /// </summary>
        public static IReadOnlyList<(string Reader, ReadingEvaluator Evaluator, string Row)> ReadCalciteEvaluators(IEnumerable<CosmosAxiom> axioms)
        {
            var kinds = Metadata.CosmosRendering.ReadTypeKinds();
            return [.. axioms.SelectMany(axiom => (axiom.Evaluators ?? [])
                .Select(e => (e.Reader, new ReadingEvaluator(e.Evaluator, e.Type, ValueSet.From(kinds.Where(t => t.Type == e.Type).Select(t => t.Kind))), axiom.Id))).Distinct()];
        }

        /// <summary>One row per test of a reading a <c>Calcite</c> row says is its operand's (G7), <c>calcitethrough</c>.</summary>
        public static IReadOnlyList<(string Reader, CosmosServiceTest Test, string Operand)> ReadCalciteThrough(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.Through ?? [])
                .Select(through => (through.Reader, through.Test, through.Operand))
                .Distinct()];

        /// <summary>
        /// One row per cast a <c>Calcite</c> row measured, by its target and the reading cast, citing the row (F3): both graphs'
        /// <c>measuredcast</c>, which the shared cast rule reads (h15b), and the cast keys of the edges table.
        /// </summary>
        public static IReadOnlyList<(string Target, string Reader, string Row)> ReadCasts(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => (axiom.Casts ?? []).Select(cast => (cast.Target, cast.Reader, axiom.Id)))
                .Distinct()];

        /// <summary>
        /// One row per reading that compares a number Calcite wrote cast with the number it denotes, moved or not (G8), S's
        /// <c>takesconstant</c>, citing its row.
        /// </summary>
        public static IReadOnlyList<(string Reading, bool Moved, string Row)> ReadComparands(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms
                .SelectMany(axiom => (axiom.Comparands ?? [])
                    .Select(comparand => (comparand.Reading, comparand.Moved, axiom.Id)))
                .Distinct()];

        /// <summary>One row per claim a reading's order holds in process (F3), S's <c>readerorder</c>, whose union is a reading's.</summary>
        public static IReadOnlyList<(string Reader, string Claim)> ReadReaderOrders(IEnumerable<CosmosAxiom> axioms) =>
            [.. axioms.SelectMany(axiom => axiom.ReaderOrders ?? [])
                .Select(order => (order.Reader, order.Claim))
                .Distinct()];

        /// <summary>
        /// One row per class a one-place test a <c>Calcite</c> row measured is TRUE of, citing the row (F3): what a type test
        /// is in process, its body's row (<c>Calcite.TypeTestsAreTheirBodies</c>), and a connective keyed by its operand's
        /// outcome, NOT's and CASE's choice, each cell answered as the vector says. The readings vocabulary keys the classes
        /// by the name the service spells a test by, so a test it spells by none keys no <c>testkinds</c> (G7).
        /// </summary>
        public static IReadOnlyList<(CosmosServiceTest Test, CosmosValueClass Kind, string Row)> ReadTestKinds(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms
                .Where(axiom => axiom.MeasuredOn == CosmosAxiomTarget.Calcite)
                .SelectMany(axiom => (axiom.Outcomes ?? [])
                    .Where(outcome => outcome.Formula is null && outcome.Operand == CosmosValueClass.None)
                    .SelectMany(outcome => outcome.Cells
                        .Where(cell => cell.Answer == CosmosAnswer.True)
                        .Select(cell => (outcome.Test, cell.Cell, axiom.Id))))
                .Distinct(),
        ];

        /// <summary>
        /// One row per operand a function outcome is of, each as its row names it (F2): S's union gathers a function's, so
        /// no reader picks one. It merges nothing either.
        /// </summary>
        public static IReadOnlyList<(string Function, long Subject)> ReadFunctionSubjects(IEnumerable<CosmosAxiom> axioms) => ReadFunctionSubjects(OutcomesOf(axioms));

        /// <summary>One row per operand each function outcome given is of, by the name its rows are filed under.</summary>
        public static IReadOnlyList<(string Function, long Subject)> ReadFunctionSubjects(IEnumerable<CosmosFunctionOutcome> outcomes) =>
            [.. outcomes.Select(outcome => (outcome.Name, outcome.Subject)).Distinct()];

        /// <summary>
        /// One row per function an axiom's rows compute: the readers its witnesses were read through, which S reads as
        /// constant raw facts beside the keys and meets (<c>measuredreaders</c>, an intersection), so two axioms of one
        /// function each give theirs and no reader picks one. Which raw column reads as the text over a function's rows is
        /// S's rule over the columns' rows and the text's (G5).
        /// </summary>
        public static IReadOnlyList<(string Function, IReadOnlyList<string> Readers)> ReadFunctionReads(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. axioms.SelectMany(axiom => (axiom.FunctionOutcomes ?? []).Select(o => o.Name).Distinct()
                .Select(function => (function, axiom.Readers ?? []))),
        ];

        /// <summary>
        /// What each raw column a <c>Calcite</c> row states, one fact a field (G5): what its reading reads each class as, the
        /// literals it is beside, the comparisons that compile, whether it compares as the type or as text, and what it
        /// answers across types. Which key each answers, and how, are the readings vocabulary's rules, in both graphs; it merges nothing.
        /// </summary>
        public static RawColumnRows ReadColumns(IEnumerable<CosmosAxiom> axioms)
        {
            var columns = axioms
                .Where(axiom => axiom.MeasuredOn == CosmosAxiomTarget.Calcite)
                .SelectMany(axiom => axiom.Columns ?? [])
                .ToList();
            return new(
                [.. columns.SelectMany(c => c.Reads.Select(read => (c.Reader, c.Name, read.Key, read.Value)))],
                [.. columns.SelectMany(c => c.Beside.Select(operand => (c.Reader, c.Name, operand)))],
                [.. columns.SelectMany(c => c.Compiles.Select(test => (c.Reader, c.Name, test.Key, test.Value)))],
                [.. columns.Select(c => (c.Reader, c.Name, c.AsType))],
                [.. columns.SelectMany(c => c.Across.Select(test => (c.Reader, c.Name, test.Key, test.Value)))]);
        }

        /// <summary>
        /// The cells of a key some outcome answers for a target that no outcome covers for it: unmeasured there, so no
        /// row, and S reads the service as answering anything. Declared, so that a cell is owed on purpose and one
        /// dropped from a vector is caught. The probe account has run every witness the emulator runs (D7), and none is owed
        /// since h15b read the emulator's <c>LIKE</c> by its affixes' matcher, each <c>_</c> one code point of the stored
        /// spelling, as rows2 measured it (<c>Service.LikeMatchesByCodePoint</c>, <c>Service.LikeOneOverEachClass</c>).
        /// </summary>
        public static IReadOnlyList<(MeasuredKey Key, RenderingTarget Target)> Owed { get; } = [];

    }

}
