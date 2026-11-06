using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// What S's rules hand a leaf beside its table (<see cref="LeafReads"/>, G1), each derived from the registry's rows of
    /// characters through the comparators a set's string order names, the matchers a test's entry names or the targets a set
    /// serves; and the places S may write under the spelling a set is sent. The leaf tests what it is handed on a cell and
    /// names no band, character or comparator itself, so these are the decisions (<c>DESIGN.md</c>, <i>Character bands and
    /// the stored spelling, S's (G1)</i>).
    /// </summary>
    public sealed class RenderingLeafReadsTests
    {

        static readonly JsonDocumentPath NamePath = P("name"), OtherPath = P("other");

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        /// <summary>Every class but U+0000's.</summary>
        static readonly ValueSet<string> WithoutNul = ValueSet.From(AllBands.Where(c => c != NulClass));

        /// <summary>
        /// What S hands a leaf claimed for a conjunct under a set, every place the conjunct reads imported as strings of these
        /// classes: the check needs the leaf and derives its reads, the literals parted over any place among them.
        /// </summary>
        static LeafReads Reads(SqlPredicate conjunct, ServiceForm leaf, RenderingSet set, ValueSet<string> classes)
        {
            using var bundle = ProbeTheory.Open(set);
            var root = bundle.Write(conjunct);
            foreach (var path in conjunct.Places)
                bundle.Import(Source, path, Strings, classes);

            bundle.Claim(root, leaf);
            bundle.Run().StopReason.Should().Be(RunStopReason.Completed);

            T Read<T>(Term<T> term)
            {
                bundle.Scope.TryGet(term, out var read).Should().BeTrue($"S needs {leaf.Text} under {set}");
                return read!;
            }

            var parted = bundle.Scope.Rows(ProbeTheory.PartedLiterals).Aggregate(ValueSet<string>.Empty, (all, row) => all.Union((ValueSet<string>)row.Output!));
            return LeafReads.Of(LeafReads.Places(parted, Read(ProbeTheory.UnplacedLiterals.Of(leaf)), Read(ProbeTheory.LooseLiterals.Of(leaf))),
                LeafReads.Matches(Read(ProbeTheory.Unread.Of(leaf)), Read(ProbeTheory.Uncut.Of(leaf)), Read(ProbeTheory.Blind.Of(leaf))),
                LeafReads.Pairs(Read(ProbeTheory.Departs.Of(leaf)), Read(ProbeTheory.Moved.Of(leaf))));
        }

        /// <summary>
        /// A comparison with a literal. The emulator orders what it stores, which moves U+0000
        /// (<c>Service.StringOrderIsCodePoint</c>'s <c>moves</c>): over strings that may hold U+0000 every literal is parted,
        /// one holding U+0000 lies anywhere, and one holding the high band is loose, a stored U+0000 being U+FDD0 and 0 (its
        /// <c>collides</c>). Either code point order ranks a surrogate pair against the high band the other way from UTF-16's
        /// (its <c>parts</c>), so a literal of the high band is parted over strings that may hold a pair, on Azure too, whose
        /// order moves nothing.
        /// </summary>
        [Fact]
        public void AComparisonReadsWhatItsSetsStringOrderMovesAndParts()
        {
            LeafReads Compared(string literal, RenderingSet set, ValueSet<string> classes) =>
                Reads(Cmp(Text(LabelPath), PredicateAtom.Less, Lit(literal)), ServiceForm.CompareOf(CosmosServiceTest.Less, Label, Of(literal)), set, classes);

            LeafReads Placed(string literal, bool parted = false, bool unplaced = false, bool loose = false)
            {
                ValueSet<string> If(bool holds) => holds ? ValueSet.Of(Of(literal)) : ValueSet<string>.Empty;
                return LeafReads.Places(If(parted), If(unplaced), If(loose));
            }

            Compared("b", RenderingSet.Emulator, AllBands).Should().Be(Placed("b", parted: true));
            Compared("a\0", RenderingSet.Emulator, AllBands).Should().Be(Placed("a\0", parted: true, unplaced: true));
            Compared("", RenderingSet.Emulator, AllBands).Should().Be(Placed("", parted: true, loose: true));
            Compared("b", RenderingSet.Emulator, WithoutNul).Should().Be(Placed("b"));
            Compared("", RenderingSet.Emulator, WithoutNul).Should().Be(Placed("", parted: true));

            Compared("b", RenderingSet.Production, AllBands).Should().Be(Placed("b"));
            Compared("a\0", RenderingSet.Production, AllBands).Should().Be(Placed("a\0"));
            Compared("", RenderingSet.Production, AllBands).Should().Be(Placed("", parted: true));
        }

        /// <summary>
        /// An affix. Azure's <c>CONTAINS</c>, and the <c>LIKE</c> of one run it answers as one, read a string only up to its
        /// first U+0000, and a pattern holding one reads none (<c>Service.NulEndsWhatReplaceAndContainsRead</c>'s
        /// <c>truncates</c>); a pattern holding what Azure stores a lone surrogate as matches a string that held one
        /// (<c>Service.LoneSurrogateIsStoredAsReplacement</c>'s <c>storedas</c>). The emulator matches what it stores against
        /// what it is sent, so a pattern its stored spelling splits leaves a range's cut untold over the characters that
        /// spelling rewrites (<c>Service.EmulatorComparesTheSpellingItIsSent</c>'s <c>rewrites</c>), and one it does not split,
        /// a <c>0</c> its first run begins with among them, leaves it told.
        /// </summary>
        [Fact]
        public void AnAffixReadsWhatItsTestTruncatesAndItsMatcherRewrites()
        {
            LeafReads Matched(CosmosServiceTest test, string argument, RenderingSet set)
            {
                var leaf = ServiceForm.AffixOf(test, Label, argument);
                return Reads(new SqlPredicate.Affix(PredicateAtom.LikeTest, Text(LabelPath), leaf.Pattern!), leaf, set, AllBands);
            }

            var (none, nul) = (ValueSet<string>.Empty, ValueSet.Of(NulClass));
            Matched(CosmosServiceTest.Contains, "a", RenderingSet.Production).Should().Be(LeafReads.Matches(nul, none, none));
            Matched(CosmosServiceTest.Contains, "a\0", RenderingSet.Production).Should().Be(LeafReads.Matches(nul, none, nul));
            Matched(CosmosServiceTest.Like, "%a%", RenderingSet.Production).Should().Be(LeafReads.Matches(nul, none, none));
            Matched(CosmosServiceTest.Like, "a%b", RenderingSet.Production).Should().Be(LeafReads.None);
            Matched(CosmosServiceTest.StartsWith, "a", RenderingSet.Production).Should().Be(LeafReads.None);
            Matched(CosmosServiceTest.EndsWith, "�", RenderingSet.Production).Should().Be(LeafReads.Matches(ValueSet.Of(SurrogateBand), none, none));

            var rewritten = ValueSet.Of(NulClass, EscapeClass);
            Matched(CosmosServiceTest.EndsWith, "\0", RenderingSet.Emulator).Should().Be(LeafReads.Matches(none, rewritten, none));
            Matched(CosmosServiceTest.EndsWith, "0", RenderingSet.Emulator).Should().Be(LeafReads.Matches(none, rewritten, none));
            Matched(CosmosServiceTest.StartsWith, "0", RenderingSet.Emulator).Should().Be(LeafReads.None);
            Matched(CosmosServiceTest.Contains, "a", RenderingSet.Emulator).Should().Be(LeafReads.None);
            Matched(CosmosServiceTest.EndsWith, "�", RenderingSet.Emulator).Should().Be(LeafReads.None);
        }

        /// <summary>
        /// A comparison of two paths: its places' characters meet the bands the set's string comparator ranks apart from
        /// UTF-16's order one a side (<c>parts</c>, on both targets), and the band the emulator's moves where either may hold
        /// it (<c>moves</c>).
        /// </summary>
        [Fact]
        public void APairReadsTheBandsItsSetsStringOrderPartsAndMoves()
        {
            var pair = Cmp(Text(NamePath), PredicateAtom.Less, Text(OtherPath));
            var leaf = ServiceForm.ComparePathsOf(CosmosServiceTest.Less, SqlValue.Spell(NamePath), SqlValue.Spell(OtherPath));
            var apart = ValueSet.Of((SurrogateBand, HighBand), (HighBand, SurrogateBand));

            Reads(pair, leaf, RenderingSet.Production, AllBands).Should().Be(LeafReads.Pairs(apart, ValueSet<string>.Empty));
            Reads(pair, leaf, RenderingSet.Emulator, AllBands).Should().Be(LeafReads.Pairs(apart, ValueSet.Of(NulClass)));
            Reads(pair, leaf, RenderingSet.Emulator, WithoutNul).Should().Be(LeafReads.Pairs(apart, ValueSet<string>.Empty));
            Reads(pair, leaf, RenderingSet.Emulator, NoBand).Should().Be(LeafReads.None);
        }

        /// <summary>
        /// A place is written where the spelling its set is sent rewrites no character its names hold: under a set whose
        /// string order compares the emulator's stored spelling, which rewrites U+0000 and U+FDD0, a name holding either is
        /// no place S proposes a form over; under Azure's, which compares what is sent as written, it is.
        /// </summary>
        [Fact]
        public void APlaceIsWritableWhereItsSetsSpellingRewritesNoCharacterItsNamesHold()
        {
            bool Writable(JsonDocumentPath path, RenderingSet set)
            {
                using var bundle = ProbeTheory.Open(set);
                var conjunct = Eq(Text(path), "x");
                var root = bundle.Write(conjunct);
                bundle.Import(Source, path, Strings, NoBand);
                bundle.Generate(root);
                bundle.RunGenerate(RenderingBundle.WidthOf([conjunct])).StopReason.Should().Be(RunStopReason.Completed);
                return bundle.Scope.Check(ProbeTheory.Writable.Of(JsonFactEncoding.Path(path).ToTerm(ProbeTheory.Paths)));
            }

            foreach (var set in new[] { RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable })
                Writable(LabelPath, set).Should().BeTrue(set.ToString());

            foreach (var name in new[] { "a\0", "﷐" })
                (Writable(P(name), RenderingSet.Production), Writable(P(name), RenderingSet.Emulator), Writable(P(name), RenderingSet.Portable)).Should().Be((true, false, false), name);
        }

    }

}
