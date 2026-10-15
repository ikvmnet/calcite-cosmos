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
            var kept = PredicateAtom.Compare(Text, "=", Of("30")).Kept(true);

            kept.IsPresent.Should().BeTrue("a comparison over SQL null keeps no row");
            kept.Values.Should().BeEquivalentTo(new[] { Of("30"), Of(30m) }, "the stored number 30 renders as '30' too");
        }

        [Fact]
        public void AnEqualityWithTextNothingElseRendersAsAdmitsTheStringAlone()
        {
            PredicateAtom.Compare(Text, "=", Of("bikes")).Kept(true).Values.Should().BeEquivalentTo(new[] { Of("bikes") });
            PredicateAtom.Compare(Text, "=", Of("true")).Kept(true).Values.Should().BeEquivalentTo(new[] { Of("true"), Of(true) },
                "Calcite renders a boolean in lower case");
            PredicateAtom.Compare(Text, "=", Of("TRUE")).Kept(true).Values.Should().BeEquivalentTo(new[] { Of("TRUE") });
        }

        [Fact]
        public void TextOfANumberNoDecimalHoldsAdmitsANumberOfEitherKind()
        {
            var kept = PredicateAtom.Compare(Text, "=", Of("1.0E30")).Kept(true);

            kept.Values.Should().BeNull("no domain names a number a decimal does not hold");
            kept.Types.Should().Contain(new[] { StringType, IntegerType, FractionType });
        }

        [Fact]
        public void ADisequalityExcludesOnlyTheStringItsTextSpells()
        {
            var kept = PredicateAtom.Compare(Text, "<>", Of("30")).Kept(true);

            kept.Excluded.Should().BeEquivalentTo(new[] { Of("30") }, "a stored 30.0 renders as 30.0, which is not '30'");
            kept.Types.Should().BeEquivalentTo(new[] { StringType, IntegerType, FractionType, BooleanType });
        }

        [Fact]
        public void ANullTestOverTheAccessorIsTrueOfAnObjectAndFalseOfAScalar()
        {
            var test = PredicateAtom.IsNull(Text);

            test.Kept(true).Kinds.Should().BeEquivalentTo(new[] { PathValues.AbsentKind, NullType, ObjectType, ArrayType });
            test.Kept(false).IsPresent.Should().BeTrue();
            test.Over(AnObject).Should().Be(Truths.True, "JSON_VALUE answers SQL null for an object");
            test.Over(Holding(Of("x"))).Should().Be(Truths.False);
        }

        [Fact]
        public void ANumericCastBoundsTheNumberLooselyAndNothingElse()
        {
            var greater = PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Text), ">", Of(500m));

            greater.Kept(true).Lower.Should().Be(Bound.Above(499m), "a conversion moves a number by less than one");
            greater.Kept(true).Upper.Should().BeNull();
            greater.Kept(true).Types.Should().Contain(StringType, "the cast converts a stored string too");
            greater.Kept(false).Upper.Should().Be(Bound.Below(501m), "FALSE of > is TRUE of <=");
        }

        [Fact]
        public void ALimitTheComparisonHoldsOfBoundsNothingOnThatSide()
        {
            // A stored -1e30 converts to the lower limit, which is greater than -3000000000.
            PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Text), ">", Of(-3000000000m)).Kept(true).Lower.Should().BeNull();
            PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", Text), ">", Of(-3000000000m)).Kept(true).Lower.Should().Be(Bound.Above(-3000000001m));
        }

        [Fact]
        public void AComparisonOverAnAbsentPathIsUnknownNotFalse()
        {
            var equal = PredicateAtom.Compare(Text, "=", Of("x"));

            equal.Over(Absent).Should().Be(Truths.Unknown);
            equal.NeverTrue(Absent).Should().BeTrue();
            equal.AlwaysFalse(Absent).Should().BeFalse("NOT of it keeps no row either");
        }

        [Fact]
        public void ANumberWhoseValueTheTextSpellsMayRenderOtherwise()
        {
            PredicateAtom.Compare(Text, "=", Of("30")).Over(Holding(Of(30m))).Should().Be(Truths.True | Truths.False, "a stored 30.0 renders as 30.0");
            PredicateAtom.Compare(Text, "=", Of("30")).Over(Holding(Of("30"))).Should().Be(Truths.True);
            PredicateAtom.Compare(Text, "=", Of("31")).Over(Holding(Of(30m))).Should().Be(Truths.False);
        }

        [Fact]
        public void ARangeTheBoundsSatisfyIsAlwaysTrueOfTheConversion()
        {
            var atLeastZero = PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Raw), ">=", Of(0m));
            var integers = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m)));

            atLeastZero.AlwaysTrue(integers).Should().BeTrue();
            PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Raw), ">", Of(0m)).AlwaysTrue(integers).Should().BeFalse();
            PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Raw), "<", Of(0m)).NeverTrue(integers).Should().BeTrue();
            PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", Text), ">=", Of(0m)).AlwaysTrue(integers).Should().BeTrue(
                "a double's parse reads 30.0 and 1.0E30 as it reads 30");
        }

        /// <summary>
        /// A cast of the accessor to an integer type parses the rendering as Java parses an integer, which raises on a
        /// stored <c>30.0</c>, <c>2.5</c> or <c>1e30</c> and on a number beyond the type. The theory spells <c>30</c> and
        /// <c>30.0</c> alike, so no number decides it either way: the rows that raise are rows no verdict may decide.
        /// </summary>
        [Fact]
        public void AnIntegerCastOverTheAccessorMayRaiseOnAnyNumber()
        {
            var integers = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m)));
            var atLeastZero = PredicateAtom.Compare(PredicateAtom.CastReader("INTEGER", Text), ">=", Of(0m));

            atLeastZero.Over(Holding(Of(30m))).Should().Be(Truths.True | Truths.Raises, "a stored 30.0 renders as 30.0, which Java's integer parse refuses");
            atLeastZero.Over(Holding(Of(2.5m))).Should().HaveFlag(Truths.Raises);
            atLeastZero.AlwaysTrue(integers).Should().BeFalse("a stored 3000000000 is no int");
            PredicateAtom.Compare(PredicateAtom.CastReader("BIGINT", Text), "<", Of(0m)).NeverTrue(integers).Should().BeFalse();
            PredicateAtom.IsNull(PredicateAtom.CastReader("INTEGER", Text)).Over(Holding(Of(2.5m))).Should().Be(Truths.False | Truths.Raises);
            PredicateAtom.IsNull(PredicateAtom.CastReader("INTEGER", Text)).Over(Absent).Should().Be(Truths.True);
            atLeastZero.Over(Holding(Of("30"))).Should().Be(Truths.True | Truths.False | Truths.Raises, "a string is parsed, or raises");
        }

        [Fact]
        public void ARawNumberIsNotDecidedEqualAcrossNumericTypes()
        {
            // Calcite's comparison of two VARIANTs of different numeric types has not been measured.
            PredicateAtom.Compare(Raw, "=", Of(30m)).AlwaysTrue(Holding(Of(30m))).Should().BeFalse();
            PredicateAtom.Compare(Raw, "=", Of(31m)).NeverTrue(Holding(Of(30m))).Should().BeTrue();
            PredicateAtom.Compare(Raw, "=", Of("a")).AlwaysTrue(Holding(Of("a"))).Should().BeTrue();
        }

        [Fact]
        public void ATypedReadingDecidesNothingButAbsence()
        {
            var typed = PredicateAtom.Compare(PredicateAtom.TypedReader("INTEGER"), "=", Of(30m));

            typed.Over(Holding(Of(30m))).Should().Be(Truths.True | Truths.Raises, "a stored 30.0 is no Integer, and Calcite raises on it");
            typed.AlwaysTrue(Holding(Of(30m))).Should().BeFalse();
            PredicateAtom.Compare(PredicateAtom.TypedReader("INTEGER"), "=", Of(31m)).NeverTrue(Holding(Of(30m))).Should().BeFalse(
                "a row Calcite raises on is no row a verdict may decide");
            typed.Over(Holding(Of("30"))).Should().Be(Truths.Raises);
            typed.NeverTrue(Absent).Should().BeTrue();
            typed.Kept(true).Values.Should().BeEquivalentTo(new[] { Of(30m) });
            PredicateAtom.Compare(PredicateAtom.TypedReader("INTEGER"), "=", Of(30.5m)).Kept(true).Kinds.Should().BeEmpty(
                "an integer is never 30.5");
        }

        [Fact]
        public void AMembershipIsTheDisjunctionOfItsEqualities()
        {
            var membership = PredicateAtom.In(Text, ValueSet.Of(Of("x"), Of("30")));

            membership.Kept(true).Values.Should().BeEquivalentTo(new[] { Of("x"), Of("30"), Of(30m) });
            membership.Over(Holding(Of("x"), Of("y"))).Should().Be(Truths.True | Truths.False);
            membership.Over(Holding(Of("x"))).Should().Be(Truths.True);
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

        /// <summary>
        /// Every truth value an atom may take on a stored value is one whose kept documents include that value: what a
        /// verdict is read from and what an assumption establishes cannot disagree, on any reading, test or value.
        /// </summary>
        [Fact]
        public void WhatARowKeepsCoversEveryValueOnWhichItTakesThatTruthValue()
        {
            var readers = new[]
            {
                Text, Raw, PredicateAtom.TypedReader("INTEGER"), PredicateAtom.TypedReader("DOUBLE"), PredicateAtom.TypedReader("BOOLEAN"),
                PredicateAtom.CastReader("INTEGER", Text), PredicateAtom.CastReader("BIGINT", Raw), PredicateAtom.CastReader("DOUBLE", Text),
                PredicateAtom.CastReader("VARCHAR", Raw),
            };
            var literals = new[] { Of("30"), Of("x"), Of("true"), Of("[1]"), Of("null"), Of(30m), Of(30.5m), Of(-3000000000m), Of(true), Of(1e30) };
            var stored = new[] { Of("30"), Of("x"), Of("true"), Of("30.5"), Of(30m), Of(30.5m), Of(31m), Of(-1e30), Of(1e30), Of(true), Of(false), NullValue };

            var atoms = new List<PredicateAtom>();
            foreach (var reader in readers)
            {
                atoms.Add(PredicateAtom.IsNull(reader));
                atoms.Add(PredicateAtom.CompareParameter(reader, "="));
                foreach (var literal in literals)
                {
                    foreach (var op in new[] { "=", "<>", "<", "<=", ">", ">=" })
                        atoms.Add(PredicateAtom.Compare(reader, op, literal));
                    atoms.Add(PredicateAtom.In(reader, ValueSet.Of(literal, Of("y"))));
                }
            }

            var states = stored.Select(v => (Name: v, Values: v == NullValue ? PathValues.OfKinds(ValueSet.Of(NullType)) : Holding(v), Admits: (Func<PathValues, bool>)(k => k.Admits(v))))
                .Append((Name: "absent", Values: Absent, Admits: k => k.Kinds.Contains(PathValues.AbsentKind)))
                .Append((Name: "object", Values: AnObject, Admits: k => k.Kinds.Contains(ObjectType) && k.Values is null))
                .Append((Name: "array", Values: PathValues.OfKinds(ValueSet.Of(ArrayType)), Admits: k => k.Kinds.Contains(ArrayType) && k.Values is null))
                .ToArray();

            foreach (var atom in atoms)
            {
                foreach (var state in states)
                {
                    var truths = atom.Over(state.Values);
                    foreach (var truth in new[] { true, false })
                        if ((truths & (truth ? Truths.True : Truths.False)) != 0)
                            state.Admits(atom.Kept(truth)).Should().BeTrue($"{atom} may be {truth} over {state.Name}, so what it keeps at {truth} must admit it");
                }
            }
        }

    }

}
