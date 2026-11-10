using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Measurements;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// One service test on each cell (<see cref="ServiceLeaf.On"/>): nothing without a row, never nothing at all, and the
    /// two string orders kept apart where a literal parts them.
    /// </summary>
    public sealed class ServiceLeafTests
    {

        static readonly string S = SqlValue.Spell(JsonDocumentPath.Root.Property("s"));

        static readonly string High = Of(char.ConvertFromUtf32(0xE000));

        static readonly string Emoji = Of("\U0001F600");

        static readonly string[] Literals = [Of("bikes"), Of(""), High, Emoji, Of(30m), Of(2.5m), Of(true), NullValue];

        static readonly ServiceTable Empty = ServiceTable.Of(ValueSet<MeasuredEntry>.Empty, ProbeRows.ReaderClasses);

        static IEnumerable<ServiceForm> Leaves()
        {
            foreach (var test in Enum.GetValues<CosmosServiceTest>().Where(test => test <= CosmosServiceTest.IsArray))
                yield return ServiceForm.TestOf(test, S);
            foreach (var op in new[] { CosmosServiceTest.Equal, CosmosServiceTest.NotEqual, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater, CosmosServiceTest.GreaterOrEqual })
                foreach (var literal in Literals)
                    yield return ServiceForm.CompareOf(op, S, literal);

            yield return ServiceForm.InOf(S, ValueSet.Of(Of("bikes"), Of(30m)));
            yield return ServiceForm.BareOf(S);
            yield return ServiceForm.ConstantOf(true);
            yield return ServiceForm.OpaqueOf("STARTSWITH(c.s, 'b')", ValueSet.Of(S));
        }

        /// <summary>Every kind of cell: an undeclared path, its strings, and integers bounded within a double's exact range.</summary>
        static IEnumerable<CellPartition> Partitions()
        {
            var cuts = ValueSet.From(Literals.Select(l => new CutPoint(l, CutKind.Literal)));
            var integers = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Meet(PathValues.Above(Bound.AtLeast(-10)), PathValues.Below(Bound.AtMost(100))));
            foreach (var classes in new[] { AllBands, NoBand })
                foreach (var import in new[] { PathValues.Anything, PathValues.OfKinds(ValueSet.Of(StringType)), integers })
                    yield return PathCell.Split(S, import, classes, cuts);
        }

        /// <summary>
        /// What S's rules may hand a leaf beside its table (<see cref="LeafReads"/>): nothing; every string literal parted;
        /// and every read at once, every literal unplaced and parted, every class unkept, every band unread, uncut and moved,
        /// every two bands departing, and every cell blind.
        /// </summary>
        static IEnumerable<LeafReads> Reads()
        {
            var strings = ValueSet.From(Literals.Where(IsString));
            var departs = ValueSet.From(AllBands.AsEnumerable().SelectMany(u => AllBands.AsEnumerable().Select(w => (u, w))));
            yield return LeafReads.None;
            yield return LeafReads.None with { Parted = strings };
            yield return LeafReads.Of(LeafReads.Places(strings, strings, AllBands), LeafReads.Matches(AllBands, AllBands, AllBands), LeafReads.Pairs(departs, AllBands));
        }

        static MeasuredOutcome Row(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell, CosmosAnswer answer) =>
            new("Service.Planted", MeasuredKey.Of(test, operand, cell), RenderingTarget.Emulator, answer);

        static ServiceOutcomes At(OutcomeVector vector, string text) => vector.Outcomes[vector.Cells.Cells.ToList().FindIndex(c => c.Text == text)].Outcomes;

        /// <summary>No branch answers anything a row does not say: with no rows, every leaf answers everything on every cell.</summary>
        [Fact]
        public void AnEmptyTableAnswersWidestEverywhere()
        {
            var narrower = from leaf in Leaves()
                           from cells in Partitions()
                           from reads in Reads()
                           let vector = ServiceLeaf.On(leaf, cells, Empty, reads)
                           from i in Enumerable.Range(0, vector.Outcomes.Count)
                           where vector.Outcomes[i].Outcomes != ServiceOutcomes.Widest
                           select $"{leaf} answers {vector.Outcomes[i].Outcomes} on {cells.Cells[i]}";

            narrower.Should().BeEmpty();
        }

        /// <summary>
        /// Whatever the rows, a cell answers something: every set's table S settles from the probe's, and from random tables
        /// cut from them and garbled, a key's rows on a target garbled alike since S holds one answer a key on a target.
        /// </summary>
        [Fact]
        public void NeverEmpty()
        {
            var random = new Random(20_261_020);
            var rows = ProbeRows.Rows;
            var answers = Enum.GetValues<CosmosAnswer>();

            var tables = new List<ServiceTable> { ProbeRows.Table };
            tables.AddRange(ProbeRows.TablesOf(rows));
            for (var trial = 0; trial < 40; trial++)
            {
                var cut = rows.GroupBy(row => (row.Key, row.Target)).Where(_ => random.Next(4) > 0)
                    .Select(key => (Rows: key, Answer: random.Next(3) > 0 ? (CosmosAnswer?)null : answers[random.Next(answers.Length)])).ToList();
                tables.AddRange(ProbeRows.TablesOf(cut.SelectMany(key => key.Answer is CosmosAnswer answer ? key.Rows.Select(row => row with { Answer = answer }) : key.Rows)));
            }

            var empty = from table in tables
                        from leaf in Leaves()
                        from cells in Partitions()
                        from reads in Reads()
                        let vector = ServiceLeaf.On(leaf, cells, table, reads)
                        from i in Enumerable.Range(0, vector.Outcomes.Count)
                        where vector.Outcomes[i].Outcomes == ServiceOutcomes.None
                        select $"{leaf} answers nothing on {cells.Cells[i]}";

            empty.Should().BeEmpty();
        }

        /// <summary>
        /// Every key a leaf reads is a cell its test and operand are read at, as the registry's owed cells and the harness's
        /// witnesses are declared (<see cref="CosmosOutcomeWitnesses"/>), so the three cannot drift apart.
        /// </summary>
        [Fact]
        public void EveryKeyALeafReadsIsACellOfItsKey()
        {
            var orders = ProbeRows.TableOf([Row(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None, CosmosAnswer.CodePoint),
                Row(CosmosServiceTest.Order, CosmosValueClass.Number, CosmosValueClass.None, CosmosAnswer.Numeric)]);
            var strays = from table in new[] { Empty, orders }
                         from leaf in Leaves()
                         from cells in Partitions()
                         from row in ServiceLeaf.On(leaf, cells, table, LeafReads.None).Rows
                         where row.StartsWith("missing:", StringComparison.Ordinal)
                         let key = row["missing:".Length..].Split('/')
                         where CosmosOutcomeWitnesses.Cells(Enum.Parse<CosmosServiceTest>(key[0]), Enum.Parse<CosmosValueClass>(key[1]), RenderingTarget.Emulator)
                             .Contains(Enum.Parse<CosmosValueClass>(key[2])) == false
                         select row;

            strays.Distinct().Should().BeEmpty();
        }

        /// <summary>A key no row settles may fail the statement; one a row settles fails only where the row says so.</summary>
        [Fact]
        public void WidestIncludesFails()
        {
            ServiceOutcomes.Widest.Should().HaveFlag(ServiceOutcomes.Fails);

            var strings = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(Of("bikes"), CutKind.Literal)));
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.LessOrEqual, S, Of(true)), strings, ProbeRows.Table, LeafReads.None).Outcomes
                .Should().OnlyContain(o => o.Outcomes == ServiceOutcomes.Widest, "no row measures <= against a boolean");
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S, Of("bikes")), strings, ProbeRows.Table, LeafReads.None).Outcomes
                .Should().NotContain(o => o.Outcomes.HasFlag(ServiceOutcomes.Fails));
        }

        /// <summary>
        /// U+1F600 is below U+E000 by UTF-16 unit, Calcite's order and the cells', and above it by code point, the service's.
        /// So an interval cut by unit lies to one side of U+E000 for the service only where the literal does not part the
        /// orders over the path; where it does, the interval answers both sides.
        /// </summary>
        [Fact]
        public void PartedLiteralsAnswerBothOrders()
        {
            var table = Less(CosmosAnswer.CodePoint, CosmosAnswer.True);
            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(High, CutKind.Literal), new CutPoint(Emoji, CutKind.Literal)));
            var less = ServiceForm.CompareOf(CosmosServiceTest.Less, S, High);

            var whole = ServiceLeaf.On(less, cells, table, LeafReads.None);
            At(whole, Emoji).Should().Be(ServiceOutcomes.False, "by code point U+1F600 is above U+E000");
            At(whole, new PathCell.Strings(Emoji, High, ValueSet<string>.Empty, AllBands).Text).Should().Be(ServiceOutcomes.True, "unparted, the interval is placed by its bounds");

            var parted = ServiceLeaf.On(less, cells, table, LeafReads.None with { Parted = ValueSet.Of(High) });
            cells.Cells.Select((cell, i) => (cell, parted.Outcomes[i].Outcomes)).Where(c => c.cell is PathCell.Strings)
                .Should().OnlyContain(c => c.Outcomes == (ServiceOutcomes.True | ServiceOutcomes.False));
            At(parted, Emoji).Should().Be(ServiceOutcomes.False);
            At(parted, High).Should().Be(ServiceOutcomes.False);
        }

        /// <summary>
        /// Under the emulator's order a point holding U+0000 lies where U+FDD0 then 0 does, and a range that may hold one,
        /// or any range against a literal holding one, lies either side: a point by the comparator itself, which compares
        /// the literal in the spelling it is sent, and a range by what S's rules hand the leaf of the order's moves row, every
        /// string literal parted over a path that may hold U+0000 and one holding it over any path
        /// (<c>Service.StringOrderIsCodePoint</c>, G1).
        /// </summary>
        [Fact]
        public void TheStoredOrderReadsU0000AsTheEmulatorStoresIt()
        {
            var table = Less(CosmosAnswer.StoredCodePoint, CosmosAnswer.True);
            var nul = Of("\0");
            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(Of("b"), CutKind.Literal), new CutPoint(nul, CutKind.Literal)));
            var less = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, Of("b")), cells, table, LeafReads.None with { Parted = ValueSet.Of(Of("b")) });
            At(less, nul).Should().Be(ServiceOutcomes.False, "stored as U+FDD0 then 0, it lies above b");
            cells.Cells.Select((cell, i) => (cell, less.Outcomes[i].Outcomes)).Where(c => c.cell is PathCell.Strings)
                .Should().OnlyContain(c => c.Outcomes == (ServiceOutcomes.True | ServiceOutcomes.False));
            var held = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, Of("a\0")), cells, table, LeafReads.None with { Parted = ValueSet.Of(Of("a\0")) });
            (At(held, nul), At(held, Of("b"))).Should().Be((ServiceOutcomes.False, ServiceOutcomes.False), "U+FDD0 then 0, and b, lie above a then U+FDD0 then 0");
            cells.Cells.Select((cell, i) => (cell, held.Outcomes[i].Outcomes)).Where(c => c.cell is PathCell.Strings)
                .Should().OnlyContain(c => c.Outcomes == (ServiceOutcomes.True | ServiceOutcomes.False));
        }

        /// <summary>
        /// Under the emulator's order the literal is compared as the statement sends it, in the stored spelling, both
        /// escapes on both sides (h03-kill §2, D14): a stored U+0000 is not the stored U+FDD0 <c>0</c>, which the one escape
        /// alone spelled alike, and a stored U+FDD0 equals the literal U+FDD0 sent as U+FDD0 <c>1</c>.
        /// </summary>
        [Fact]
        public void TheStoredOrderEscapesU0000AndUFDD0Both()
        {
            var (fdd0, aliased) = ("﷐", "﷐" + "0");
            ServiceLeaf.Compare(CosmosAnswer.StoredCodePoint, "\0", aliased).Should().BeLessThan(0, "U+FDD0 0 against U+FDD0 1 0");
            ServiceLeaf.Compare(CosmosAnswer.StoredCodePoint, fdd0, fdd0).Should().Be(0);
            ServiceLeaf.Compare(CosmosAnswer.StoredCodePoint, fdd0, fdd0 + "\u0001").Should().BeLessThan(0, "U+FDD0 1 is a prefix of U+FDD0 1 U+0001");

            var table = ProbeRows.TableOf(new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }
                .Select(side => Row(CosmosServiceTest.Equal, CosmosValueClass.String, side, side == CosmosValueClass.Equal ? CosmosAnswer.True : CosmosAnswer.False))
                .Append(Row(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None, CosmosAnswer.StoredCodePoint)));
            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(Of("\0"), CutKind.Literal), new CutPoint(Of(fdd0), CutKind.Literal)));
            // What S's rules hand the leaf here: each literal parted over a path that may hold U+0000 (Service.StringOrderIsCodePoint's
            // moves, G1); a point is read by the comparator, the literal sent in the spelling it compares.
            LeafReads Stored(string literal) => LeafReads.None with { Parted = ValueSet.Of(literal) };
            var equal = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Equal, S, Of(aliased)), cells, table, Stored(Of(aliased)));
            At(equal, Of("\0")).Should().Be(ServiceOutcomes.False, "the one escape spelled U+0000 as the literal U+FDD0 0");
            At(ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Equal, S, Of(fdd0)), cells, table, Stored(Of(fdd0))), Of(fdd0)).Should().Be(ServiceOutcomes.True);
            // The stored spelling is one to one: a range that may hold U+0000 lies either side of a literal parted over it, and
            // equals it only where it holds the literal itself.
            cells.Cells.Select((cell, i) => (cell, equal.Outcomes[i].Outcomes)).Where(c => c.cell is PathCell.Strings)
                .Should().OnlyContain(c => c.Outcomes == (c.cell.Contains(Of(aliased)) ? ServiceOutcomes.True | ServiceOutcomes.False : ServiceOutcomes.False))
                .And.Contain(c => c.cell.Contains(Of(aliased)));
        }

        /// <summary>
        /// A code point order compares the strings it is given as they are, a lone surrogate by its unit and a pair as the code
        /// point it is: what a target stores of a string written otherwise is S's (<see cref="LeafReads.Unkept"/>), under which
        /// a point holding an unpaired surrogate lies anywhere, Azure storing it as U+FFFD, and a point of pairs alone is
        /// placed as written.
        /// </summary>
        [Fact]
        public void ACodePointOrderComparesALoneSurrogateAsItIsAndAnUnkeptPointLiesAnywhere()
        {
            ServiceLeaf.Compare(CosmosAnswer.CodePoint, "\ud800", "\ufffd").Should().BeLessThan(0, "U+D800 is below U+FFFD");
            ServiceLeaf.Compare(CosmosAnswer.CodePoint, "\ud83d\ude00", "\ufffd").Should().BeGreaterThan(0, "a pair is U+1F600");
            ServiceLeaf.Compare(CosmosAnswer.CodePoint, "a\udc00", "a\ud800").Should().BeGreaterThan(0, "each lone unit by its own value");
            ServiceLeaf.Compare(CosmosAnswer.CodePoint, "a\ud800", "a").Should().BeGreaterThan(0, "a prefix is below");

            var table = Less(CosmosAnswer.CodePoint, CosmosAnswer.True);
            var (lone, pair, b) = (Of("\ud800"), Of("\ud83d\ude00"), Of("b"));
            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(lone, CutKind.Literal), new CutPoint(pair, CutKind.Literal), new CutPoint(b, CutKind.Literal)));
            var form = ServiceForm.CompareOf(CosmosServiceTest.Less, S, b);

            var given = ServiceLeaf.On(form, cells, table, LeafReads.None);
            (At(given, lone), At(given, pair)).Should().Be((ServiceOutcomes.False, ServiceOutcomes.False), "as given, U+D800 and U+1F600 are above b");

            var unkept = ServiceLeaf.On(form, cells, table, LeafReads.Places(ValueSet<string>.Empty, ValueSet<string>.Empty, ValueSet.Of(UnpairedClass)));
            At(unkept, lone).Should().Be(ServiceOutcomes.True | ServiceOutcomes.False, "what is stored of it says where it lies");
            At(unkept, pair).Should().Be(ServiceOutcomes.False, "a pair is stored as written");
            At(unkept, b).Should().Be(ServiceOutcomes.False);
        }

        /// <summary>
        /// A range is placed by its bounds against the literal, which keeps its order in the stored spelling wherever neither
        /// holds U+0000 (h03-kill §2): strings with no U+0000 up to U+FDD0 lie below U+FDD0 U+0001, as each spelled is below
        /// U+FDD0 1 U+0001, the literal as it is sent.
        /// </summary>
        [Fact]
        public void TheStoredOrderPlacesARangeWithoutU0000ByItsBounds()
        {
            var table = Less(CosmosAnswer.StoredCodePoint, CosmosAnswer.True);
            var (fdd0, above) = (Of("﷐"), Of("﷐\u0001"));
            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), ClassSet(Closed(JsonCharacterBand.HighBmp)), ValueSet.Of(new CutPoint(fdd0, CutKind.Literal), new CutPoint(above, CutKind.Literal)));
            var less = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, above), cells, table, LeafReads.None);
            At(less, fdd0).Should().Be(ServiceOutcomes.True);
            At(less, new PathCell.Strings(null, fdd0, ValueSet<string>.Empty, ClassSet(Closed(JsonCharacterBand.HighBmp))).Text).Should().Be(ServiceOutcomes.True);
        }

        /// <summary>
        /// An entry of two answers means both; an order of two comparators places a cell by each. Each key's two answers are
        /// planted a target apiece and read under the set serving both (<see cref="ProbeRows.Portable"/>).
        /// </summary>
        [Fact]
        public void AMultiValuedEntryAnswersTheUnion()
        {
            var table = Less(CosmosAnswer.CodePoint, CosmosAnswer.Utf16, CosmosAnswer.True, CosmosAnswer.NotKept);
            table.TryGet(MeasuredKey.Of(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Below), out var below).Should().BeTrue();
            below!.Outcomes.Should().Be(ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined);

            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(High, CutKind.Literal), new CutPoint(Emoji, CutKind.Literal)));
            var vector = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, High), cells, table, LeafReads.None);
            At(vector, Emoji).Should().Be(ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined, "by code point it is above, by unit below");
            At(vector, new PathCell.Strings(null, Emoji, ValueSet<string>.Empty, AllBands).Text).Should().Be(ServiceOutcomes.True | ServiceOutcomes.False | ServiceOutcomes.Undefined);

            // What an order entry answers beside its comparators stands beside every cell it places.
            var failing = ProbeRows.TableOf(ProbeRows.Portable(new[] { CosmosAnswer.CodePoint, CosmosAnswer.Fails }.Select(a => (MeasuredKey.Of(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None), a))
                .Concat(new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }.Select(side => (MeasuredKey.Of(CosmosServiceTest.Less, CosmosValueClass.String, side), CosmosAnswer.True)))), RenderingSet.Portable);
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, High), cells, failing, LeafReads.None).Outcomes
                .Should().OnlyContain(o => o.Outcomes == (ServiceOutcomes.True | ServiceOutcomes.Fails));
        }

        /// <summary>
        /// A whole number within int64 is placed as its order's entry says each comparator reads one (G2, S's roles rules
        /// over <c>Service.IntegersOrderExactly</c>'s readings, planted here): 2^60 + 1 against 2^60, its double, lies above
        /// by its value and at it by its double; read both ways it lies at either, and read neither way anywhere.
        /// </summary>
        [Fact]
        public void AWholeNumberIsPlacedAsItsComparatorReadsOne()
        {
            var (big, rounded) = (Of(1152921504606846977m), Of(1152921504606846976m));
            var cells = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(IntegerType)), NoBand, ValueSet.Of(new CutPoint(big, CutKind.Literal), new CutPoint(rounded, CutKind.Literal)));
            var rows = new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }
                .Select(side => Row(CosmosServiceTest.Equal, CosmosValueClass.Number, side, side == CosmosValueClass.Equal ? CosmosAnswer.True : CosmosAnswer.False))
                .Append(Row(CosmosServiceTest.Order, CosmosValueClass.Number, CosmosValueClass.None, CosmosAnswer.Numeric));

            ServiceOutcomes Placed(params string[] readings)
            {
                var table = ProbeRows.TableOf(ProbeRows.Planted([.. rows]) with { WholeReadings = [.. readings.Select(r => (CosmosAnswer.Numeric, r))] }, RenderingSet.Emulator);
                return At(ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Equal, S, rounded), cells, table, LeafReads.None), big);
            }

            Placed("value").Should().Be(ServiceOutcomes.False, "by its value it is above 2^60");
            Placed("double").Should().Be(ServiceOutcomes.True, "its double is 2^60");
            Placed("value", "double").Should().Be(ServiceOutcomes.True | ServiceOutcomes.False);
            Placed().Should().Be(ServiceOutcomes.True | ServiceOutcomes.False, "read neither way, it lies anywhere");
        }

        /// <summary>
        /// An order entry naming a comparator of another kind's order has no reader, S's roles rule reading each order's
        /// comparators by its operand's class (<see cref="AnswerMeanings.ReadBy"/>, G2), so its leaf answers anything: a
        /// string under a number's comparator and a number under a string's are placed by neither. The table is S's own,
        /// over the registry's rows with each order's swapped for the other kind's comparator on both targets.
        /// </summary>
        [Fact]
        public void AComparatorOfAnotherKindPlacesNothing()
        {
            var (half, two) = (Of(1.5m), Of(2m));
            var swapped = new[] { (Kind: CosmosValueClass.String, Comparator: CosmosAnswer.Numeric), (Kind: CosmosValueClass.Number, Comparator: CosmosAnswer.CodePoint) };
            var rows = ProbeRows.Rows.Where(row => row.Key.Test != CosmosServiceTest.Order)
                .Concat(from target in new[] { RenderingTarget.Emulator, RenderingTarget.Azure }
                        from order in swapped
                        select new MeasuredOutcome("Service.Planted", MeasuredKey.Of(CosmosServiceTest.Order, order.Kind, CosmosValueClass.None), target, order.Comparator));
            var table = ProbeRows.TableOf(rows, RenderingCheckRun.AssertedSet);
            foreach (var order in swapped)
            {
                table.TryGet(MeasuredKey.Of(CosmosServiceTest.Order, order.Kind, CosmosValueClass.None), out var entry).Should().BeTrue(order.Kind.ToString());
                entry!.Answers.Should().Equal([order.Comparator]);
                entry.Readers.Should().BeEmpty(order.Kind.ToString());
            }

            var strings = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(High, CutKind.Literal), new CutPoint(Emoji, CutKind.Literal)));
            var numbers = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(FractionType, IntegerType)), NoBand, ValueSet.Of(new CutPoint(half, CutKind.Literal), new CutPoint(two, CutKind.Literal)));
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, High), strings, table, LeafReads.None).Outcomes.Should().OnlyContain(o => o.Outcomes == ServiceOutcomes.Widest);
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, two), numbers, table, LeafReads.None).Outcomes.Should().OnlyContain(o => o.Outcomes == ServiceOutcomes.Widest);
        }

        /// <summary>
        /// A comparator the meanings give a role the leaf has no evaluation of reads nothing, S's roles rule reading each key
        /// by the answers the leaf evaluates (<see cref="ServiceLeaf.Evaluates"/>, G2), so its leaf answers anything and never
        /// raises: a number's comparator the meanings give the order of strings, and the UTF-16 order where the leaf is
        /// planted without it. The measured meanings give no answer a role the leaf does not evaluate.
        /// </summary>
        [Fact]
        public void AComparatorTheLeafCannotEvaluateReadsNothing()
        {
            ProbeRows.Measured.Means.ReadBy.AsEnumerable().Should().BeSubsetOf(ServiceLeaf.Evaluates.AsEnumerable());

            var order = MeasuredKey.Of(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None);
            var strings = PathCell.Split(S, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands, ValueSet.Of(new CutPoint(High, CutKind.Literal), new CutPoint(Emoji, CutKind.Literal)));
            IEnumerable<MeasuredOutcome> Ordered(CosmosAnswer comparator) => new[] { CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above }
                .Select(side => Row(CosmosServiceTest.Less, CosmosValueClass.String, side, side == CosmosValueClass.Below ? CosmosAnswer.True : CosmosAnswer.False))
                .Append(Row(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None, comparator));

            var means = ProbeRows.Measured.Means;
            var unevaluated = new[]
            {
                ProbeRows.Planted([.. Ordered(CosmosAnswer.Numeric)]) with { Means = means with { ReadBy = ValueSet.From(means.ReadBy.AsEnumerable().Append((CosmosServiceTest.Order, CosmosValueClass.String, CosmosAnswer.Numeric))) } },
                ProbeRows.Planted([.. Ordered(CosmosAnswer.Utf16)]) with { Evaluates = ValueSet.From(ServiceLeaf.Evaluates.AsEnumerable().Where(e => e != (CosmosServiceTest.Order, CosmosValueClass.String, CosmosAnswer.Utf16))) },
            };
            foreach (var definition in unevaluated)
            {
                var table = ProbeRows.TableOf(definition, RenderingSet.Emulator);
                table.TryGet(order, out var entry).Should().BeTrue();
                entry!.Readers.Should().BeEmpty(entry.Answers.ToString());
                ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.Less, S, High), strings, table, LeafReads.None).Outcomes.Should().OnlyContain(o => o.Outcomes == ServiceOutcomes.Widest);
            }

            ProbeRows.TableOf(Ordered(CosmosAnswer.Utf16)).TryGet(order, out var read).Should().BeTrue();
            read!.Readers.Should().Equal([CosmosAnswer.Utf16], "the leaf evaluates the UTF-16 order");
        }

        /// <summary>A key the table misses is named among the rows, and answers everything where it is read.</summary>
        [Fact]
        public void MissingKeysAreNamed()
        {
            var missing = MeasuredKey.Of(CosmosServiceTest.NotEqual, CosmosValueClass.String, CosmosValueClass.Null);
            var table = ProbeRows.TableOf(ProbeRows.Rows.Where(row => row.Target == RenderingTarget.Emulator && row.Key != missing && row.Key.Test != CosmosServiceTest.LessOrEqual));
            var cells = PathCell.Split(S, PathValues.Anything, AllBands, ValueSet.Of(new CutPoint(Of("bikes"), CutKind.Literal)));

            var vector = ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S, Of("bikes")), cells, table, LeafReads.None);
            vector.Rows.Where(row => row.StartsWith("missing:")).Should().Equal("missing:NotEqual/String/Null");
            At(vector, "null").Should().Be(ServiceOutcomes.Widest);
            At(vector, "absent").Should().Be(ServiceOutcomes.Undefined);

            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.LessOrEqual, S, Of("bikes")), cells, table, LeafReads.None).Rows.Where(row => row.StartsWith("missing:"))
                .Should().BeEquivalentTo(new[] { "Below", "Equal", "Above", "Absent", "Null", "True", "False", "Number", "Object", "Array" }.Select(cell => "missing:LessOrEqual/String/" + cell));
            ServiceLeaf.On(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, S, Of("bikes")), cells, Empty, LeafReads.None).Rows.Should().Contain("missing:Order/String/None");
        }

        /// <summary>
        /// S's table of <c>&lt;</c> against a string, ordered by the comparators, below the literal answering the rest; a key's
        /// two answers are planted a target apiece (<see cref="ProbeRows.Portable"/>).
        /// </summary>
        static ServiceTable Less(params CosmosAnswer[] comparatorsThenBelow)
        {
            var comparators = comparatorsThenBelow.Where(a => a is CosmosAnswer.CodePoint or CosmosAnswer.Utf16 or CosmosAnswer.StoredCodePoint).ToList();
            var (order, below) = (MeasuredKey.Of(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None), MeasuredKey.Of(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Below));
            return ProbeRows.TableOf(ProbeRows.Portable(comparators.Select(c => (order, c))
                .Concat(comparatorsThenBelow.Except(comparators).Select(a => (below, a)))
                .Append((MeasuredKey.Of(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Equal), CosmosAnswer.False))
                .Append((MeasuredKey.Of(CosmosServiceTest.Less, CosmosValueClass.String, CosmosValueClass.Above), CosmosAnswer.False))), RenderingSet.Portable);
        }

    }

}
