using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The truth tables the predicate rules read: what a row with a truth value says of the path an atom reads, and which
    /// truth values the documents a path's knowledge admits can give it.
    /// </summary>
    /// <remarks>
    /// The two answers are written separately, and each must over-approximate: the first because it is what an assumed
    /// predicate establishes, the second because it is what a verdict is read from. The property test holds them to each
    /// other on every kind of stored value.
    /// </remarks>
    public sealed class PredicateAtomTests
    {

        const string Text = PredicateAtom.TextReader;
        const string Raw = PredicateAtom.RawReader;

        static PathValues Holding(params string[] values) =>
            PathValues.Meet(PathValues.OfKinds(ValueSet.From(values.Select(TypeOf))), PathValues.OneOf(ValueSet.From(values)));

        static readonly PathValues Absent = PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind));

        static readonly PathValues AnObject = PathValues.OfKinds(ValueSet.Of(ObjectType));

        [Fact]
        public void AnEqualityWithTextANumberRendersAsAdmitsTheStringAndTheNumber()
        {
            var kept = PredicateAtom.Compare(Text, "=", Of("30")).Kept(true, FactsReadings.Rows, FactsReadings.Columns);

            kept.IsPresent.Should().BeTrue("a comparison over SQL null keeps no row");
            foreach (var value in new[] { Of("30"), Of(30m), Of(30.000000000000001m) })
                kept.Admits(value).Should().BeTrue($"the stored number 30 renders as '30' too, and one whose double is 30 is returned as 30: {value}");
            // The join of a string and the numbers whose double is 30, which the lattice holds as their kinds and the numbers' window.
            foreach (var value in new[] { Of(true), Of(false), JsonValues.NullValue, Of(31m), Of(29.5m) })
                kept.Admits(value).Should().BeFalse(value);
        }

        [Fact]
        public void AnEqualityWithTextNothingElseRendersAsAdmitsTheStringAlone()
        {
            PredicateAtom.Compare(Text, "=", Of("bikes")).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Values.Should().BeEquivalentTo(new[] { Of("bikes") });
            PredicateAtom.Compare(Text, "=", Of("true")).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Values.Should().BeEquivalentTo(new[] { Of("true"), Of(true) },
                "Calcite renders a boolean in lower case");
            PredicateAtom.Compare(Text, "=", Of("TRUE")).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Values.Should().BeEquivalentTo(new[] { Of("TRUE") });
        }

        [Fact]
        public void TextOfANumberNoDecimalHoldsAdmitsANumberOfEitherKind()
        {
            var kept = PredicateAtom.Compare(Text, "=", Of("1.0E30")).Kept(true, FactsReadings.Rows, FactsReadings.Columns);

            kept.Values.Should().BeNull("no domain names a number a decimal does not hold");
            kept.Types.Should().Contain(new[] { StringType, IntegerType, FractionType });
        }

        [Fact]
        public void ADisequalityExcludesOnlyTheStringItsTextSpells()
        {
            var kept = PredicateAtom.Compare(Text, "<>", Of("30")).Kept(true, FactsReadings.Rows, FactsReadings.Columns);

            kept.Excluded.Should().BeEquivalentTo(new[] { Of("30") },
                "the text reading reads no spelling the service returns, so it keeps the number 30, which comes back as '30', soundly (TODO.md)");
            kept.Types.Should().BeEquivalentTo(new[] { StringType, IntegerType, FractionType, BooleanType });
        }

        [Fact]
        public void ANullTestOverTheAccessorIsTrueOfAnObjectAndFalseOfAScalar()
        {
            var test = PredicateAtom.IsNull(Text);

            test.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Kinds.Should().BeEquivalentTo(new[] { PathValues.AbsentKind, NullType, ObjectType, ArrayType });
            test.Kept(false, FactsReadings.Rows, FactsReadings.Columns).IsPresent.Should().BeTrue();
            test.Over(AnObject, FactsReadings.Rows).Should().Be(Truths.True, "JSON_VALUE answers SQL null for an object");
            test.Over(Holding(Of("x")), FactsReadings.Rows).Should().Be(Truths.False);
        }

        [Fact]
        public void ANumericCastBoundsTheNumberLooselyAndNothingElse()
        {
            var greater = PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Text), ">", Of(500m));

            greater.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Lower.Should().Be(Bound.Above(499m), "a conversion moves a number by less than one");
            greater.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Upper.Should().BeNull();
            greater.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Types.Should().Contain(StringType, "the cast converts a stored string too");
            greater.Kept(false, FactsReadings.Rows, FactsReadings.Columns).Upper.Should().Be(Bound.Below(501m), "FALSE of > is TRUE of <=");
        }

        [Fact]
        public void ALimitTheComparisonHoldsOfBoundsNothingOnThatSide()
        {
            // A stored -1e30 converts to the lower limit, which is greater than -3000000000.
            PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Text), ">", Of(-3000000000m)).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Lower.Should().BeNull();
            PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", Text), ">", Of(-3000000000m)).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Lower.Should().Be(Bound.Above(-3000000001m));
        }

        [Fact]
        public void AComparisonOverAnAbsentPathIsUnknownNotFalse()
        {
            var equal = PredicateAtom.Compare(Text, "=", Of("x"));

            equal.Over(Absent, FactsReadings.Rows).Should().Be(Truths.Unknown);
            equal.NeverTrue(Absent, FactsReadings.Rows).Should().BeTrue();
            equal.AlwaysFalse(Absent, FactsReadings.Rows).Should().BeFalse("NOT of it keeps no row either");
        }

        /// <summary>
        /// The translator holds every text the oracle's grammar reads as a number's to be ambiguous, over generated text of
        /// digits, points, exponents and signs, spaces, words and digits of another script, which <c>BigDecimal</c> alone
        /// reads as a number and no stored number renders in.
        /// </summary>
        [Fact]
        public void TheTranslatorReadsNumberTextByTheOraclesGrammar()
        {
            var random = new Random(20261016);
            var alphabet = new[] { "0", "1", "9", ".", "e", "E", "+", "-", " ", "a", "\u0663", "Infinity", "true" };

            for (var i = 0; i < 20000; i++)
            {
                var text = string.Concat(Enumerable.Range(0, random.Next(0, 6)).Select(_ => alphabet[random.Next(alphabet.Length)]));

                if (Renderings.MayBeNumberText(text))
                    Adapter.Sql.CosmosRexTranslator.IsUnambiguousText(text).Should().BeFalse($"\"{text}\" may be a number's rendering");
            }
        }

        [Fact]
        public void ANumberWhoseValueTheTextSpellsMayRenderOtherwise()
        {
            PredicateAtom.Compare(Text, "=", Of("30")).Over(Holding(Of(30m)), FactsReadings.Rows).Should().Be(Truths.True | Truths.False,
                "the reading says only that a number whose double is the text's may render as it, and reads no spelling the service returns, which is '30' (TODO.md)");
            PredicateAtom.Compare(Text, "=", Of("30")).Over(Holding(Of("30")), FactsReadings.Rows).Should().Be(Truths.True);
            PredicateAtom.Compare(Text, "=", Of("31")).Over(Holding(Of(30m)), FactsReadings.Rows).Should().Be(Truths.False);
        }

        [Fact]
        public void ARangeTheBoundsSatisfyIsAlwaysTrueOfTheConversion()
        {
            var integers = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m)));

            PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", Text), ">=", Of(0m)).AlwaysTrue(integers, FactsReadings.Rows).Should().BeTrue(
                "a double's parse reads 30.0 and 1.0E30 as it reads 30");
            PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", Text), "<", Of(0m)).NeverTrue(integers, FactsReadings.Rows).Should().BeTrue();

            // A raw value's numeric cast is no reading (h15b): over an ANY it wraps a long beyond an int, so nothing of a
            // stored number is decided by it.
            var wrapped = PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Raw), ">=", Of(0m));
            wrapped.AlwaysTrue(integers, FactsReadings.Rows).Should().BeFalse();
            wrapped.NeverTrue(integers, FactsReadings.Rows).Should().BeFalse();
        }

        /// <summary>
        /// A cast of the accessor to an integer type parses the rendering as Java parses an integer, which raises on a
        /// stored <c>2.5</c> or <c>1e30</c> and on a number beyond the type. The service returns a whole number within a
        /// long as its digits however it was written, <c>30.0</c> as <c>30</c> (<c>Service.IntegralNumbersReadBackAsHeld</c>),
        /// which the parse reads, and the cells say so (<c>NumberCast.ParsesWhole</c>). The coarse reading, which the
        /// verdicts read, reads no spelling and puts the parse's raise beside every number, so no number decides it either
        /// way (<c>TODO.md</c>, <em>A conjunct through an integer parse its declared bounds satisfy is still sent</em>).
        /// </summary>
        [Fact]
        public void AnIntegerCastOverTheAccessorMayRaiseOnAnyNumber()
        {
            var integers = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m)));
            var atLeastZero = PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Text), ">=", Of(0m));

            atLeastZero.Over(Holding(Of(30m)), FactsReadings.Rows).Should().Be(Truths.True | Truths.Raises, "the coarse reading puts the parse's raise beside every number");
            atLeastZero.RaisesOver(Holding(Of(30m)), FactsReadings.Columns, FactsReadings.Rows).Should().BeFalse("its cells read 30 as the service returns it, which the parse reads");
            atLeastZero.RaisesOver(PathValues.Meet(integers, PathValues.Below(Bound.AtMost(100m))), FactsReadings.Columns, FactsReadings.Rows).Should().BeFalse(
                "nor does it raise on an integer from 0 to 100, however written");
            atLeastZero.AlwaysTrue(PathValues.Meet(integers, PathValues.Below(Bound.AtMost(100m))), FactsReadings.Rows).Should().BeFalse(
                "yet the verdict reads the coarse reading, and is not reached");
            atLeastZero.Over(Holding(Of(2.5m)), FactsReadings.Rows).Should().HaveFlag(Truths.Raises);
            atLeastZero.AlwaysTrue(integers, FactsReadings.Rows).Should().BeFalse("a stored 3000000000 is no int");
            PredicateAtom.Compare(PredicateAtom.CastReader("BIGINT", Text), "<", Of(0m)).NeverTrue(integers, FactsReadings.Rows).Should().BeFalse();
            PredicateAtom.IsNull(PredicateAtom.CastReader("INTEGER", Text)).Over(Holding(Of(2.5m)), FactsReadings.Rows).Should().Be(Truths.False | Truths.Raises);
            PredicateAtom.IsNull(PredicateAtom.CastReader("INTEGER", Text)).Over(Absent, FactsReadings.Rows).Should().Be(Truths.True);
            atLeastZero.Over(Holding(Of("30")), FactsReadings.Rows).Should().Be(Truths.True | Truths.False | Truths.Raises, "a string is parsed, or raises");
        }

        [Fact]
        public void ARawNumberIsNotDecidedEqualAcrossNumericTypes()
        {
            // Calcite's comparison of two VARIANTs of different numeric types has not been measured.
            PredicateAtom.Compare(Raw, "=", Of(30m)).AlwaysTrue(Holding(Of(30m)), FactsReadings.Rows).Should().BeFalse();
            PredicateAtom.Compare(Raw, "=", Of(31m)).NeverTrue(Holding(Of(30m)), FactsReadings.Rows).Should().BeTrue();
            PredicateAtom.Compare(Raw, "=", Of("a")).AlwaysTrue(Holding(Of("a")), FactsReadings.Rows).Should().BeTrue();
        }

        [Fact]
        public void ATypedReadingDecidesNothingButAbsence()
        {
            var typed = PredicateAtom.Compare(PredicateAtom.TypedReader("INTEGER"), "=", Of(30m));

            typed.Over(Holding(Of(30m)), FactsReadings.Rows).Should().Be(Truths.True | Truths.Raises,
                "the coarse reading puts the raise of a spelling of another class beside every value of the type, though the service returns 30.0 as 30, an Integer");
            typed.RaisesOver(Holding(Of(30m)), FactsReadings.Columns, FactsReadings.Rows).Should().BeFalse("its cells read the spelling the service returns");
            typed.AlwaysTrue(Holding(Of(30m)), FactsReadings.Rows).Should().BeFalse();
            PredicateAtom.Compare(PredicateAtom.TypedReader("INTEGER"), "=", Of(31m)).NeverTrue(Holding(Of(30m)), FactsReadings.Rows).Should().BeFalse(
                "a row the coarse reading says may raise is no row a verdict may decide");
            typed.Over(Holding(Of("30")), FactsReadings.Rows).Should().Be(Truths.Raises);
            typed.NeverTrue(Absent, FactsReadings.Rows).Should().BeTrue();
            typed.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Should().Match<PathValues>(k => k.Values == null && k.Admits(Of(30m)) && k.Admits(Of(30.000000000000001m)) && k.Admits(Of(30.01m)) == false,
                "the column reads a number written with a point as its double, whole there");
            PredicateAtom.Compare(PredicateAtom.TypedReader("INTEGER"), "=", Of(30.5m)).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Kinds.Should().BeEmpty(
                "an integer is never 30.5");
        }

        /// <summary>
        /// <c>RETURNING DECIMAL</c> parses the rendering, so a stored string of digits is a number to it, and a row it keeps
        /// may hold one; a stored number of either spelling converts, and decides it.
        /// </summary>
        [Fact]
        public void ADecimalReturningParsesTheRendering()
        {
            var equal = PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(30m));

            equal.Over(Holding(Of("30")), FactsReadings.Rows).Should().HaveFlag(Truths.True, "a stored \"30\" parses as 30");
            equal.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Types.Should().Contain(StringType);
            equal.AlwaysTrue(Holding(Of(30m)), FactsReadings.Rows).Should().BeTrue("30 and 30.0 both parse as 30");
            equal.Over(Holding(Of(true)), FactsReadings.Rows).Should().Be(Truths.Raises | Truths.True | Truths.False, "a boolean's rendering does not parse");
            equal.Over(AnObject, FactsReadings.Rows).Should().Be(Truths.Unknown);

            var fraction = PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(2.5m));
            fraction.Over(Holding(Of(2.5m)), FactsReadings.Rows).Should().Be(Truths.True | Truths.False, "the literal is compared at scale zero, which is not 2.5");
            fraction.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Lower.Should().Be(Bound.Above(1.5m), "the window holds whatever the literal is cast to");
        }

        /// <summary>
        /// A cast to <c>DOUBLE</c> rounds the stored number and the literal each to a double, which from 2^52 is a whole
        /// unit wide: no window holds around such a bound, and two numbers a unit apart are one double past 2^53.
        /// </summary>
        /// <remarks>
        /// Measured as <c>= 2^60</c> keeping a stored 2^60 + 1.
        /// </remarks>
        [Fact]
        public void ADoubleCastStatesNoWindowWhereADoublesUnitIsWhole()
        {
            const decimal Big = 1152921504606846976m;   // 2^60
            var reader = PredicateAtom.CastReader("DOUBLE", Text);

            var equal = PredicateAtom.Compare(reader, "=", Of(Big));
            equal.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Lower.Should().BeNull("a stored 2^60 + 1 rounds to the literal's double");
            equal.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Upper.Should().BeNull();
            equal.Over(Holding(Of(Big + 1)), FactsReadings.Rows).Should().HaveFlag(Truths.True, "2^60 + 1 is 2^60 as a double");

            var range = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.Above(Big)));
            PredicateAtom.Compare(reader, ">", Of(Big)).Over(range, FactsReadings.Rows).Should().HaveFlag(Truths.False, "a number above 2^60 may be 2^60 as a double");
            PredicateAtom.Compare(reader, "=", Of(Big)).NeverTrue(range, FactsReadings.Rows).Should().BeFalse();

            PredicateAtom.Compare(reader, "=", Of(30m)).Kept(true, FactsReadings.Rows, FactsReadings.Columns).Lower.Should().Be(Bound.Above(29m), "below 2^52 the window holds");
        }

        /// <summary>
        /// <c>RETURNING DECIMAL</c> compares the number the rendering parses as, exactly: below 2^53 a number is its double,
        /// and past it two numbers a unit apart are one to a double but not to the parse, where Azure returns an integer as
        /// itself; the emulator holds one written with a point as its double, and returns that double's digits
        /// (<c>Service.IntegralNumbersReadBackAsHeld</c>, 9007199254740993.0 as 9007199254740992), so there the stored
        /// 2^60 + 1 may be 2^60, and what the comparison keeps holds it.
        /// </summary>
        [Fact]
        public void ADecimalReturningComparesExactlyPastADouble()
        {
            const decimal Big = 1152921504606846976m;   // 2^60
            var parse = PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(Big));

            parse.Over(Holding(Of(Big + 1)), FactsReadings.Rows).Should().Be(Truths.True | Truths.False, "Azure returns 2^60 + 1, the emulator may return 2^60");
            parse.Kept(true, FactsReadings.Rows, FactsReadings.Columns).Admits(Of(Big + 1)).Should().BeTrue("a TRUE row may hold 2^60 + 1 written with a point");
            PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(30m)).Over(Holding(Of(31m)), FactsReadings.Rows).Should().Be(Truths.False, "below 2^53 no double moves an integer");
            PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", Text), "=", Of(Big)).Over(Holding(Of(Big + 1)), FactsReadings.Rows).Should().Be(Truths.True);
        }

        [Fact]
        public void AMembershipIsTheDisjunctionOfItsEqualities()
        {
            var membership = PredicateAtom.In(Text, ValueSet.Of(Of("x"), Of("30")));

            var kept = membership.Kept(true, FactsReadings.Rows, FactsReadings.Columns);
            foreach (var value in new[] { Of("x"), Of("30"), Of(30m), Of(30.000000000000001m) })
                kept.Admits(value).Should().BeTrue(value);
            foreach (var value in new[] { Of(true), Of(31m), Of(29.5m) })
                kept.Admits(value).Should().BeFalse(value);
            membership.Over(Holding(Of("x"), Of("y")), FactsReadings.Rows).Should().Be(Truths.True | Truths.False);
            membership.Over(Holding(Of("x")), FactsReadings.Rows).Should().Be(Truths.True);
        }

        [Fact]
        public void KleenesConnectivesOverSetsOfTruthValues()
        {
            PredicateAtom.Swap(Truths.True | Truths.Unknown).Should().Be(Truths.False | Truths.Unknown);
            PredicateAtom.Or(Truths.False, Truths.Unknown).Should().Be(Truths.Unknown);
            PredicateAtom.Or(Truths.False | Truths.True, Truths.Unknown).Should().Be(Truths.True | Truths.Unknown);
            PredicateAtom.Or(Truths.False, Truths.False).Should().Be(Truths.False);
        }

        [Fact]
        public void PathValuesIsALattice()
        {
            var samples = new[]
            {
                PathValues.Anything,
                PathValues.AnythingThere,
                PathValues.OfTypes(ValueSet.Of(StringType)),
                PathValues.OneOf(ValueSet.Of(Of("a"), Of(1m))),
                PathValues.Not(Of("a")),
                PathValues.Above(Bound.AtLeast(0m)),
                PathValues.Below(Bound.Below(10m)),
                Holding(Of(5m)),
                Absent,
            };

            PathValues.Lattice.Verify(samples);
        }

        /// <summary>The grid's readings: each the loader builds, a cast over each reading it casts.</summary>
        internal static readonly string[] Readers =
        {
            Text, Raw, PredicateAtom.TypedReader("INTEGER"), PredicateAtom.TypedReader("DOUBLE"), PredicateAtom.TypedReader("BOOLEAN"), PredicateAtom.TypedReader("DECIMAL"),
            PredicateAtom.CastReader("INTEGER", Text), PredicateAtom.CastReader("BIGINT", Text), PredicateAtom.CastReader("DOUBLE", Text),
            PredicateAtom.CastReader("VARCHAR", Raw), PredicateAtom.CastReader("BOOLEAN", Text),
        };

        internal static readonly string[] Literals = { Of("30"), Of("x"), Of("true"), Of("[1]"), Of("null"), Of(30m), Of(30.5m), Of(-3000000000m), Of(true), Of(1e30) };

        internal static readonly string[] Stored = { Of("30"), Of("x"), Of("true"), Of("30.5"), Of(30m), Of(30.5m), Of(31m), Of(-1e30), Of(1e30), Of(true), Of(false), NullValue };

        /// <summary>The grid's atoms over readings: a null test, a parameter, and each test and a membership with each literal.</summary>
        internal static IEnumerable<PredicateAtom> Grid(IEnumerable<string> readers)
        {
            foreach (var reader in readers)
            {
                yield return PredicateAtom.IsNull(reader);
                yield return PredicateAtom.CompareParameter(reader, "=");
                foreach (var literal in Literals)
                {
                    foreach (var op in new[] { "=", "<>", "<", "<=", ">", ">=" })
                        yield return PredicateAtom.Compare(reader, op, literal);
                    yield return PredicateAtom.In(reader, ValueSet.Of(literal, Of("y")));
                }
            }
        }

        /// <summary>
        /// Every truth value an atom may take on a stored value is one whose kept documents include that value: what a
        /// verdict is read from and what an assumption establishes cannot disagree, on any reading, test or value. The cast
        /// of a raw value to text keeps by its column's answer (<c>rawunion</c>), which its coarse reading does not read, so
        /// it is asked of its cells over the same rows; beside a parameter its cells add the coarse answer, which says a null
        /// may compare where every column casts it to null, and <c>PredicateAtomCellTests</c>' audit of what every reading
        /// keeps holds it against the evaluator instead.
        /// </summary>
        [Fact]
        public void WhatARowKeepsCoversEveryValueOnWhichItTakesThatTruthValue()
        {
            var atoms = Grid(Readers).Where(a => a.Parameter == false || PredicateAtom.Columnless(a.Reader) != "cast:VARCHAR:raw").ToList();
            var states = Stored.Select(v => (Name: v, Values: v == NullValue ? PathValues.OfKinds(ValueSet.Of(NullType)) : Holding(v), Admits: (Func<PathValues, bool>)(k => k.Admits(v))))
                .Append((Name: "absent", Values: Absent, Admits: k => k.Kinds.Contains(PathValues.AbsentKind)))
                .Append((Name: "object", Values: AnObject, Admits: k => k.Kinds.Contains(ObjectType) && k.Values is null))
                .Append((Name: "array", Values: PathValues.OfKinds(ValueSet.Of(ArrayType)), Admits: k => k.Kinds.Contains(ArrayType) && k.Values is null))
                .ToArray();

            foreach (var atom in atoms)
            {
                foreach (var state in states)
                {
                    var truths = PredicateAtom.Columnless(atom.Reader) == "cast:VARCHAR:raw"
                        ? PredicateAtom.State.Enumerate(state.Values).Aggregate(Truths.None, (t, s) => t | atom.OnCell(PathCell.Of(s), FactsReadings.Columns, FactsReadings.Rows))
                        : atom.Over(state.Values, FactsReadings.Rows);
                    foreach (var truth in new[] { true, false })
                        if ((truths & (truth ? Truths.True : Truths.False)) != 0)
                            state.Admits(atom.Kept(truth, FactsReadings.Rows, FactsReadings.Columns)).Should().BeTrue($"{atom} may be {truth} over {state.Name}, so what it keeps at {truth} must admit it");
                }
            }
        }

    }

}
