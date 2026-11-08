using System;
using System.Collections.Generic;

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
    /// row says, which the predicate vocabulary's type-test rule reads.
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
            ReadsExactly = graph.DefineFunction<string, CosmosValueClass, bool>("readsexactly", ValueLattice.Max<bool>(), analysis);
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
        /// Whether the facts graph reads a reading as Calcite does beside a literal of a class, <c>Number</c> or <c>None</c>
        /// for any other, a row's: what a statement's strata read before resting on an atom (<see cref="RenderingFamily.ReadExactly(SqlPredicate, Func{string, CosmosValueClass, bool})"/>).
        /// </summary>
        public Function<string, CosmosValueClass, bool> ReadsExactly { get; }

        /// <summary>The facts a root holds: the rows, the truth table and the comparisons the cell decides, and the table empty.</summary>
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

            foreach (var (reader, beside) in rows.Exact)
                yield return ReadsExactly.Of(reader, beside).Is(true);

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
        }

    }

}
