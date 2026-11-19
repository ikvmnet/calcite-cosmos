using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What Calcite makes of each reading, as rules over its rows (G7, <c>DESIGN.md</c> "Calcite's readings as rows"): one
    /// vocabulary both graphs define, S at its root and the facts graph at its, so a cell and the facts graph read an atom's
    /// readings in one table (<see cref="ReaderRows"/>), class by class. A reading reads each class as its rows say; a parse
    /// of a class is decided over the character classes its witnesses held; a reading a row says is read as another is read
    /// as it over each class both read alike, and as itself over any other; a test answers over a class what the truth
    /// table says the class's read means, beside its operand's where a row says the test is the operand's; and a comparison
    /// of a class read as itself, rendered or parsed is the cell's to decide; <c>LIKE</c> reads each unit of its pattern as
    /// its row says; and a type test is TRUE, by the name the service spells it by, of the JSON kinds of each class its
    /// row says, which the predicate vocabulary's type-test rule reads. Beside them, what each raw column answers on a key,
    /// from its rows alone, and the columns' union (h12, G5), so both graphs read a raw reading's keys alike; a comparison
    /// with a parameter reads its column's answer beside every class it is compared beside. And, by element type, the kinds
    /// of the elements a traversal yields and the reading they read as.
    /// </summary>
    internal sealed class ReadingVocabulary
    {

        /// <summary>
        /// Defines the vocabulary's tables on a graph, beside the predicate vocabulary's test kinds, which its type-test rule
        /// writes, and the readings a test's body takes apart, which its facts seed; <see cref="Define"/> defines its rules.
        /// </summary>
        public ReadingVocabulary(EGraph graph, Function<string, ValueSet<string>> testKinds, Relation<string, string> testReads)
        {
            TestKinds = testKinds;
            TestReads = testReads;
            var analysis = new TableOptions { Role = TableRole.Analysis };
            TestClasses = graph.DefineRelation<CosmosServiceTest, CosmosValueClass, string>("testclasses", analysis);
            TestNames = graph.DefineRelation<string, CosmosServiceTest>("testnames", analysis);
            ClassKinds = graph.DefineRelation<CosmosValueClass, string>("classkinds", analysis);
            Reads = graph.DefineRelation<string, CosmosValueClass, string>("calcitereads");
            Bands = graph.DefineFunction<string, CosmosValueClass, ValueSet<string>>("calcitebands", ValueLattice.Union<string>());
            Rows = graph.DefineRelation<string, string>("calciterows");
            ReadAs = graph.DefineRelation<string, string>("calcitereadas");
            Through = graph.DefineRelation<string, CosmosServiceTest, string>("calcitethrough");
            ReadTruths = graph.DefineFunction<CosmosServiceTest, string, Truths>("readtruths");
            ReadCompares = graph.DefineRelation<CosmosServiceTest, string>("readcompares");
            LikeUnits = graph.DefineRelation<LikeUnit, string>("likeunits");
            ReaderRows = graph.DefineFunction<ReadingTable>("readerrows", ReadingTable.Union);
            graph.Values.Register<(string, CosmosValueClass)>("reading and class");
            graph.Values.RegisterSet<(string, CosmosValueClass)>();
            // Read beside the terms by the predicate vocabulary's G8 rules and the facts graph's exactness, which may read
            // only terms and analyses.
            ReadsExactly = graph.DefineFunction<ValueSet<(string, CosmosValueClass)>>("readsexactly", ValueLattice.Union<(string, CosmosValueClass)>(), options: analysis);
            BoxOf = graph.DefineRelation<string, string>("boxof", analysis);
            SeesThrough = graph.DefineRelation<string>("seesthrough", analysis);
            BoxNull = graph.DefineRelation<string>("boxnull", analysis);
            TakesConstant = graph.DefineRelation<string, bool, string>("takesconstant", analysis);
            StrictOp = graph.DefineFunction<string, bool>("strictop", ValueLattice.Min<bool>(), options: analysis);
            // Read beside the terms by the shared cast rule (h15b).
            MeasuredCast = graph.DefineRelation<string, string, string>("measuredcast", analysis);
            // Read beside the terms by the predicate vocabulary's element rules: one set, so a rule reads a reading it lacks.
            AsOfAPath = graph.DefineFunction<ValueSet<string>>("asofapath", ValueLattice.Union<string>(), options: analysis);
            // Read beside the terms by the rules of an element a traversal yields, by its element type.
            YieldKinds = graph.DefineFunction<string, ValueSet<string>>("yieldkinds", ValueLattice.Union<string>(), options: analysis);
            YieldReads = graph.DefineRelation<string, string>("yieldreads", analysis);
            // The raw columns as their rows state them, one fact a field; each class's JSON type; each column's answer on a
            // key, the rules', and their union, which Eggnog's join takes (h12, G5). Their values registered here, once a
            // graph, since each graph has a store of its own.
            graph.Values.Register<RawKey>("raw key");
            graph.Values.Register<RawAnswer>("raw answer");
            graph.Values.Register<RawTruths>("raw truths");
            RawReads = graph.DefineFunction<string, string, CosmosValueClass, string>("rawreads");
            RawBeside = graph.DefineRelation<string, string, CosmosValueClass>("rawbeside");
            RawCompiles = graph.DefineFunction<string, string, CosmosServiceTest, bool>("rawcompiles");
            RawAsType = graph.DefineFunction<string, string, bool>("rawastype");
            RawAcross = graph.DefineFunction<string, string, CosmosServiceTest, Truths>("rawacross");
            ClassType = graph.DefineFunction<CosmosValueClass, string>("classtype");
            RawColumn = graph.DefineFunction<RawKey, string, RawAnswer>("rawcolumn");
            RawUnion = graph.DefineFunction<RawTruths>("rawunion", RawTruths.Union);
            // Read beside the terms by S's rule that gives such a column's reading the union's order, so an analysis.
            UnionColumn = graph.DefineRelation<string>("unioncolumn", analysis);
            ParameterAs = graph.DefineRelation<string, string>("parameteras");
        }

        /// <summary>The JSON kinds each type test is TRUE of, by its name: the predicate vocabulary's, which this one's rule writes.</summary>
        public Function<string, ValueSet<string>> TestKinds { get; }

        /// <summary>The readings a one-place test's body takes apart, with the reading it reads instead: the predicate vocabulary's, which this one's facts seed (G8).</summary>
        public Relation<string, string> TestReads { get; }

        /// <summary>The classes each one-place test is TRUE of in process, citing the row.</summary>
        public Relation<CosmosServiceTest, CosmosValueClass, string> TestClasses { get; }

        /// <summary>Each test the service spells by a name, by that name (<c>ServiceForm.SpellingOf</c>).</summary>
        public Relation<string, CosmosServiceTest> TestNames { get; }

        /// <summary>The JSON kinds each class of value is held as (<see cref="CosmosValueClasses.JsonKinds"/>).</summary>
        public Relation<CosmosValueClass, string> ClassKinds { get; }

        /// <summary>What each reading reads each class of value as, a <c>Calcite</c> row's.</summary>
        public Relation<string, CosmosValueClass, string> Reads { get; }

        /// <summary>The character classes a reading's witnesses of a class held.</summary>
        public Function<string, CosmosValueClass, ValueSet<string>> Bands { get; }

        /// <summary>Each row naming a reading, beside the reading.</summary>
        public Relation<string, string> Rows { get; }

        /// <summary>A reading a row says is read as another over each class both read alike.</summary>
        public Relation<string, string> ReadAs { get; }

        /// <summary>A test of a reading a row says is its operand's, by the operand's reading.</summary>
        public Relation<string, CosmosServiceTest, string> Through { get; }

        /// <summary>What a test answers of a value read so, where the read decides it (<see cref="KleeneTables.Reads"/>).</summary>
        public Function<CosmosServiceTest, string, Truths> ReadTruths { get; }

        /// <summary>The tests the value read decides, by what it is read as (<see cref="KleeneTables.Compares"/>).</summary>
        public Relation<CosmosServiceTest, string> ReadCompares { get; }

        /// <summary>What <c>LIKE</c> reads each unit of a pattern as, citing its row: one row a unit (<see cref="LikeUnit.Of"/>).</summary>
        public Relation<LikeUnit, string> LikeUnits { get; }

        /// <summary>Every reading's rows in one value (<see cref="ReadingTable"/>), which a cell and the facts graph's atom rules bind.</summary>
        public Function<ReadingTable> ReaderRows { get; }

        /// <summary>
        /// The readings the facts graph reads as Calcite does beside a literal of a class, <c>Number</c> or <c>None</c> for any
        /// other, in one value, the rows': what a statement's strata read before resting on an atom (<see cref="PredicateAtom.ReadExactly"/>,
        /// <c>PredicateRules.Inexact</c>). One table, so that a rule reads an atom no row names as one read otherwise.
        /// </summary>
        public Function<ValueSet<(string, CosmosValueClass)>> ReadsExactly { get; }

        /// <summary>The box each reading, kind of stored value or value cast is held in, cast to <c>VARIANT</c> (G8).</summary>
        public Relation<string, string> BoxOf { get; }

        /// <summary>The comparisons and tests that read the values inside one box (G8).</summary>
        public Relation<string> SeesThrough { get; }

        /// <summary>The SQL types whose box is null where what it holds is (G8).</summary>
        public Relation<string> BoxNull { get; }

        /// <summary>
        /// The readers that compare with what a number Calcite wrote cast denotes (G8), citing the row: with the number itself
        /// (<c>false</c>), and with one a cast that drops scale moved by less than one (<c>true</c>), which only a reader whose
        /// reading rescales the literal reads (<c>PredicateAtom</c>'s decimal parse). A reader no row names takes neither.
        /// </summary>
        public Relation<string, bool, string> TakesConstant { get; }

        /// <summary>Whether Calcite answers null for an operator, by its name in the IR, over a null operand (<c>strictop</c>).</summary>
        public Function<string, bool> StrictOp { get; }

        /// <summary>
        /// The casts a <c>Calcite</c> row measured, by target and the reading cast, its column apart, citing the row: a cast is
        /// a reading where one names it (the shared cast rule, h15b), and a cast no row names reads nothing.
        /// </summary>
        public Relation<string, string, string> MeasuredCast { get; }

        /// <summary>
        /// The readings a <c>Calcite</c> row says an index reads as of a path holding the element, a test's body over each among
        /// it, in one value (<c>Calcite.ReadingsOfAnIndexAreAsOfAPath</c>): the predicate vocabulary's element rules read each
        /// accessor of an element as a path's where it names the reading, and as the element reading where it does not.
        /// </summary>
        public Function<ValueSet<string>> AsOfAPath { get; }

        /// <summary>
        /// The JSON kinds a traversal of an array of an element type yields an element of, by the type's name, a <c>Calcite</c>
        /// row's (<c>Calcite.AVarcharArrayYieldsItsStringsAndNullsAsTheText</c>). A type no row names has no value, so nothing
        /// narrows what its elements hold.
        /// </summary>
        public Function<string, ValueSet<string>> YieldKinds { get; }

        /// <summary>
        /// The reading the elements a traversal of an array of an element type yields read as, by the type's name, a
        /// <c>Calcite</c> row's. A type no row names has no row, so its elements read as nothing.
        /// </summary>
        public Relation<string, string> YieldReads { get; }

        /// <summary>What a raw column's reading reads a stored value of each class as (<c>CosmosAxiomRows.ReadColumns</c>, G5), raw.</summary>
        public Function<string, string, CosmosValueClass, string> RawReads { get; }

        /// <summary>The literal classes a raw column's reading is compared beside, raw.</summary>
        public Relation<string, string, CosmosValueClass> RawBeside { get; }

        /// <summary>Whether each comparison compiles over a raw column's reading, raw.</summary>
        public Function<string, string, CosmosServiceTest, bool> RawCompiles { get; }

        /// <summary>Whether a raw column's reading compares a value as its type, or as the text it renders, raw.</summary>
        public Function<string, string, bool> RawAsType { get; }

        /// <summary>What a comparison a raw column compiles answers of a value it reads beside a literal of another type, raw.</summary>
        public Function<string, string, CosmosServiceTest, Truths> RawAcross { get; }

        /// <summary>The JSON type of each class of value, constant data (G5).</summary>
        public Function<CosmosValueClass, string> ClassType { get; }

        /// <summary>What one raw column answers on a key, as the raw rules derive it from the column's rows; no lattice, so two disagreeing conflict.</summary>
        public Function<RawKey, string, RawAnswer> RawColumn { get; }

        /// <summary>
        /// The raw columns' answers, joined key by key from every column's: what a raw reading's cells read, and, under a
        /// comparison's key with no literal's class, what it answers beside a parameter.
        /// </summary>
        public Function<RawTruths> RawUnion { get; }

        /// <summary>A column a raw value is read through that no row measured, read as the measured columns' union (<see cref="PredicateAtom.UnionColumns"/>).</summary>
        public Relation<string> UnionColumn { get; }

        /// <summary>
        /// A column whose comparison with a parameter answers as another column's, by its class (<c>Calcite.AParameterComparesAsItsComparand</c>):
        /// a <c>CHAR</c>'s as a <c>VARCHAR</c>'s, where no literal's row measured it.
        /// </summary>
        public Relation<string, string> ParameterAs { get; }

        /// <summary>
        /// The facts a root holds: the rows, the truth table and the comparisons the cell decides, and the table empty; the raw
        /// columns' rows, each class's type, the columns no row measured, and the union empty.
        /// </summary>
        public IEnumerable<Fact> Facts(CalciteRows rows, IEnumerable<(CosmosServiceTest Test, string Read, Truths Truths)> truths, IEnumerable<(CosmosServiceTest Test, string Read)> compares)
        {
            foreach (var (reader, kind, answer) in rows.Reads)
                yield return Reads.Of(reader, kind, answer);

            foreach (var (reader, kind, band) in rows.Bands)
                yield return Bands.Of(reader, kind).Is(ValueSet.Of(band));

            foreach (var (reader, row) in rows.Rows)
                yield return Rows.Of(reader, row);

            foreach (var (reader, @as) in rows.ReadAs)
                yield return ReadAs.Of(reader, @as);

            foreach (var (reader, test, operand) in rows.Through)
                yield return Through.Of(reader, test, operand);

            foreach (var (unit, row) in rows.LikeUnits)
                yield return LikeUnits.Of(unit, row);

            foreach (var (test, kind, row) in rows.TestKinds)
                yield return TestClasses.Of(test, kind, row);

            foreach (var (reader, @as) in rows.TestReads)
                yield return TestReads.Of(reader, @as);

            yield return ReadsExactly.Of().Is(ValueSet.From(rows.Exact.Select(e => (e.Reader, e.Beside))));

            foreach (var (kind, box) in rows.Boxes)
                yield return BoxOf.Of(kind, box);

            foreach (var op in rows.SeesThrough)
                yield return SeesThrough.Of(op);

            foreach (var type in rows.NullThrough)
                yield return BoxNull.Of(type);

            foreach (var (reading, moved, row) in rows.Comparands)
                yield return TakesConstant.Of(reading, moved, row);

            foreach (var (name, strict) in rows.StrictOperators)
                yield return StrictOp.Of(name).Is(strict);

            foreach (var (target, reader, row) in rows.Casts)
                yield return MeasuredCast.Of(target, reader, row);

            yield return AsOfAPath.Of().Is(ValueSet.From(rows.AsOfAPath));

            foreach (var kinds in rows.YieldKinds.GroupBy(y => y.Type))
                yield return YieldKinds.Of(kinds.Key).Is(ValueSet.From(kinds.Select(y => y.Kind)));

            foreach (var (type, reader) in rows.YieldReads)
                yield return YieldReads.Of(type, reader);

            foreach (var test in Enum.GetValues<CosmosServiceTest>())
                if (ServiceForm.SpellingOf(test) is { } name)
                    yield return TestNames.Of(name, test);

            foreach (var (kind, json) in CosmosValueClasses.JsonKinds)
                yield return ClassKinds.Of(kind, json);

            foreach (var (test, read, t) in truths)
                yield return ReadTruths.Of(test, read).Is(t);

            foreach (var (test, read) in compares)
                yield return ReadCompares.Of(test, read);

            yield return ReaderRows.Of().Is(ReadingTable.Empty);

            // The columns' rows, one fact a field; the union starts empty, so a raw reading's cells read anything and a
            // raise where no column is named.
            foreach (var (reading, column, kind, answer) in rows.Columns.Reads)
                yield return RawReads.Of(reading, column, kind).Is(answer);

            foreach (var (reading, column, operand) in rows.Columns.Beside)
                yield return RawBeside.Of(reading, column, operand);

            foreach (var (reading, column, test, compiles) in rows.Columns.Compiles)
                yield return RawCompiles.Of(reading, column, test).Is(compiles);

            foreach (var (reading, column, asType) in rows.Columns.AsType)
                yield return RawAsType.Of(reading, column).Is(asType);

            foreach (var (reading, column, test, across) in rows.Columns.Across)
                yield return RawAcross.Of(reading, column, test).Is(across);

            foreach (var (@class, type) in rows.ClassTypes)
                yield return ClassType.Of(@class).Is(type);

            foreach (var column in PredicateAtom.UnionColumns)
                yield return UnionColumn.Of(column);

            foreach (var (column, @as) in rows.ParametersAs)
                yield return ParameterAs.Of(column, @as);

            yield return RawUnion.Of().Is(RawTruths.Empty);
        }

        /// <summary>Defines the vocabulary's rules in a ruleset, every one monotone over the rows: one stage settles them.</summary>
        public void Define(Ruleset reads)
        {
            reads.DefineRule("a reading reads each class as its rows say", r =>
            {
                var (rd, a) = r.Vars<string, string>("rd", "a");
                var k = r.Var<CosmosValueClass>("k");
                r.When(Reads.Of(rd, k, a));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, k, a, ReadingTable.Read)));
            });

            reads.DefineRule("a reading rests on each row that names it", r =>
            {
                var (rd, row) = r.Vars<string, string>("rd", "row");
                r.When(Rows.Of(rd, row));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, row, ReadingTable.Cited)));
            });

            reads.DefineRule("a reading's parse of a class is decided over the character classes its witnesses held", r =>
            {
                var rd = r.Var<string>("rd");
                var k = r.Var<CosmosValueClass>("k");
                var b = r.Var<ValueSet<string>>("b");
                r.When(Reads.Of(rd, k, CalciteRead.Parses), b.Is(Bands.Of(rd, k)));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, k, b, ReadingTable.Measured)));
            });

            reads.DefineRule("a reading a row reads as another is read as it over each class both read alike", r =>
            {
                var (rd, al, a) = r.Vars<string, string, string>("rd", "al", "a");
                var k = r.Var<CosmosValueClass>("k");
                r.When(ReadAs.Of(rd, al), Reads.Of(rd, k, a), Reads.Of(al, k, a));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, k, al, ReadingTable.Evaluated)));
            });

            reads.DefineRule("a reading's test of a class answers what the truth table says its read of the class means", r =>
            {
                var (rd, a) = r.Vars<string, string>("rd", "a");
                var k = r.Var<CosmosValueClass>("k");
                var test = r.Var<CosmosServiceTest>("test");
                var t = r.Var<Truths>("t");
                r.When(Reads.Of(rd, k, a), t.Is(ReadTruths.Of(test, a)));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, k, test, t, ReadingTable.Answering)));
            });

            reads.DefineRule("a reading's comparison of a class read as the value read decides it is decided on the cell", r =>
            {
                var (rd, a) = r.Vars<string, string>("rd", "a");
                var k = r.Var<CosmosValueClass>("k");
                var test = r.Var<CosmosServiceTest>("test");
                r.When(Reads.Of(rd, k, a), ReadCompares.Of(test, a));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, k, test, a, ReadingTable.Comparing)));
            });

            reads.DefineRule("a reading whose test a row says is its operand's answers over each class what its operand's read of it means, beside its own", r =>
            {
                var (rd, op, a, b) = r.Vars<string, string, string, string>("rd", "op", "a", "b");
                var k = r.Var<CosmosValueClass>("k");
                var test = r.Var<CosmosServiceTest>("test");
                var t = r.Var<Truths>("t");
                r.When(Through.Of(rd, test, op), Reads.Of(rd, k, a), Reads.Of(op, k, b), t.Is(ReadTruths.Of(test, b)));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, k, test, t, ReadingTable.Answering)));
            });

            reads.DefineRule("LIKE reads each unit of its pattern as its row says", r =>
            {
                var u = r.Var<LikeUnit>("u");
                var row = r.Var<string>("row");
                r.When(LikeUnits.Of(u, row));
                r.Then(ReaderRows.Of().Is(Compute.Of(u, row, ReadingTable.Unit)));
            });

            reads.DefineRule("a type test is TRUE, by its name, of the JSON kinds of each class its row says", r =>
            {
                var (name, row, kind) = r.Vars<string, string, string>("name", "row", "kind");
                var test = r.Var<CosmosServiceTest>("test");
                var k = r.Var<CosmosValueClass>("k");
                r.When(TestClasses.Of(test, k, row), TestNames.Of(name, test), ClassKinds.Of(k, kind));
                r.Then(TestKinds.Of(name).Is(Primitives.Singleton<string>().Of(kind)), ReaderRows.Of().Is(Compute.Of(name, row, ReadingTable.Tested)));
            });

            reads.DefineRule("a reading whose test is its operand's rests on its operand's rows", r =>
            {
                var (rd, op, row) = r.Vars<string, string, string>("rd", "op", "row");
                var test = r.Var<CosmosServiceTest>("test");
                r.When(Through.Of(rd, test, op), Rows.Of(op, row));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, row, ReadingTable.Cited)));
            });

            DefineRaw(reads);
        }

        // The raw columns (h12, G5): what each column answers on a key, from its rows alone. A null test is what the column
        // reads the class as means to it; a comparison it compiles, over a class it reads as null or raises on, what that
        // read means; over a value of the literal's type, the comparison as the type, and over its cast's text, the text
        // compared, both of which the cell decides; across types, what the column says; one it does not compile fails the
        // statement. The union joins the columns, each column's reading keeping its own (rawtyped); a comparison with a
        // parameter, under its key with no literal's class, joins what the column answers beside every class.
        void DefineRaw(Ruleset reads)
        {
            reads.DefineRule("a raw reading's null test over a class is what its column reads the class as means to it", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var k = r.Var<CosmosValueClass>("k");
                var t = r.Var<Truths>("t");
                r.When(a.Is(RawReads.Of(rd, c, k)), t.Is(ReadTruths.Of(CosmosServiceTest.IsNull, a)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, (x, y) => RawKey.Of(x, y, CosmosServiceTest.IsNull, CosmosValueClass.None)), c).Is(Compute.Of(t, RawAnswer.Stated)));
            });

            reads.DefineRule("a raw reading's comparison over a class its column reads as null or raises on is what that read means to it", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var (t, tr) = r.Vars<CosmosServiceTest, Truths>("t", "tr");
                r.When(a.Is(RawReads.Of(rd, c, k)), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o), tr.Is(ReadTruths.Of(t, a)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(Compute.Of(tr, RawAnswer.Stated)));
            });

            reads.DefineRule("a raw reading's comparison of a value with a literal of its type is the comparison as the type, which the cell decides", r =>
            {
                var (rd, c, ty) = r.Vars<string, string, string>("rd", "c", "ty");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Itself), RawAsType.Of(rd, c).Is(true), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o),
                    ty.Is(ClassType.Of(k)), ty.Is(ClassType.Of(o)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.OnTheCell));
            });

            reads.DefineRule("a raw reading's comparison of a value with a literal of another type is what its column answers across types", r =>
            {
                var (rd, c) = r.Vars<string, string>("rd", "c");
                var (tk, to) = r.Vars<string, string>("tk", "to");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var (t, tr) = r.Vars<CosmosServiceTest, Truths>("t", "tr");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Itself), RawAsType.Of(rd, c).Is(true), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o),
                    tk.Is(ClassType.Of(k)), to.Is(ClassType.Of(o)), tk.IsNot(to), tr.Is(RawAcross.Of(rd, c, t)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(Compute.Of(tr, RawAnswer.Stated)));
            });

            reads.DefineRule("a cast of a raw value compares a value it reads as itself as itself, which the cell decides", r =>
            {
                var (rd, c) = r.Vars<string, string>("rd", "c");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Itself), RawAsType.Of(rd, c).Is(false), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.OnTheCell));
            });

            reads.DefineRule("a cast of a raw value compares the text it renders a value as, which the cell decides", r =>
            {
                var (rd, c) = r.Vars<string, string>("rd", "c");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Renders), RawAsType.Of(rd, c).Is(false), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.OnTheCell));
            });

            reads.DefineRule("a raw reading's comparison its column does not compile fails the statement", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(a.Is(RawReads.Of(rd, c, k)), RawCompiles.Of(rd, c, t).Is(false), RawBeside.Of(rd, c, o));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.Fails));
            });

            reads.DefineRule("a raw reading's answer on a key is the union of its columns', and a column's reading's its own", r =>
            {
                var (k, c, t) = r.Vars<RawKey, string, RawAnswer>("k", "c", "t");
                r.When(t.Is(RawColumn.Of(k, c)));
                r.Then(RawUnion.Of().Is(Compute.Of(k, c, t, RawTruths.Of)));
            });

            // A column no row measured, a CHAR's, answers each key as the union does until a row measures it: every measured
            // column's answer joined under its key, as under the union's.
            reads.DefineRule("a raw column no row measured answers each key as the union of those that were", r =>
            {
                var (k, c, u) = r.Vars<RawKey, string, string>("k", "c", "u");
                var t = r.Var<RawAnswer>("t");
                r.When(t.Is(RawColumn.Of(k, c)), UnionColumn.Of(u));
                r.Then(RawUnion.Of().Is(Compute.Of(k, u, t, RawTruths.Of)));
            });

            // A parameter is typed as what it is compared with, so the comparison answers what its column answers beside each
            // class the column is compared beside: every key's answer joined under the key with no literal's class, by the
            // union's lattice, as rawcolumn has none. A null test's key is its own, and joins what it holds.
            reads.DefineRule("a raw reading's comparison with a parameter over a class answers what its column answers beside any class", r =>
            {
                var (k, c, t) = r.Vars<RawKey, string, RawAnswer>("k", "c", "t");
                r.When(t.Is(RawColumn.Of(k, c)));
                r.Then(RawUnion.Of().Is(Compute.Of(k, c, t, RawTruths.OfParameter)));
            });

            // A column whose parameter a row measured answering as another's, a CHAR's as a VARCHAR's, answers each key beside
            // a parameter as that column does; one no row names, none, so a raw reading's cells read anything and a raise there.
            reads.DefineRule("a raw column answers a parameter as the column a row says it answers one as", r =>
            {
                var (k, c, u) = r.Vars<RawKey, string, string>("k", "c", "u");
                var t = r.Var<RawAnswer>("t");
                r.When(t.Is(RawColumn.Of(k, c)), ParameterAs.Of(u, c));
                r.Then(RawUnion.Of().Is(Compute.Of(k, u, t, RawTruths.OfParameter)));
            });

            // A raw column's reading as the other readings are read, from its rows: each class as its column reads it, and
            // the reading through every column as each. So the facts graph reads a raw value's comparison, test and LIKE
            // over the cells as the rendering graph does, a raise where its column's row says one, and where none, none.
            reads.DefineRule("a raw column's reading reads each class as its column's rows say, and the reading through every column as each", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var k = r.Var<CosmosValueClass>("k");
                r.When(a.Is(RawReads.Of(rd, c, k)));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, c, k, a, ReadingTable.Raw)));
            });

            // A column no row measured reads as the union until a row measures it (PredicateAtom.UnionColumns): each class
            // as every measured column reads it.
            reads.DefineRule("a raw column no row measured reads each class as the union of those that were", r =>
            {
                var (rd, c, a, u) = r.Vars<string, string, string, string>("rd", "c", "a", "u");
                var k = r.Var<CosmosValueClass>("k");
                r.When(a.Is(RawReads.Of(rd, c, k)), UnionColumn.Of(u));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, u, k, a, ReadingTable.Raw)));
            });
        }

    }

}
