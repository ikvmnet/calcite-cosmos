using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The theory's table of what a string function's value draws its characters from (<see cref="StringCalls"/>), held
    /// to Calcite's runtime: the functions it computes in process with, called directly.
    /// </summary>
    /// <remarks>
    /// <c>org.apache.calcite.runtime.SqlFunctions</c> is what a plan computes a string function with, as the measurements
    /// of its answers through a connection show (<c>CalciteStringFunctionMeasurementTests</c>): ß is <c>SS</c>, U+0130 is
    /// <c>i</c> and U+0307. Asked here of every code point, and of each lone surrogate, so that a mapping the table does not
    /// account for is found rather than sampled for.
    /// </remarks>
    public sealed class StringCallsTests
    {

        static IEnumerable<string> EveryCharacter()
        {
            for (var point = 0; point <= 0x10FFFF; point++)
                if (point is < 0xD800 or > 0xDFFF)
                    yield return char.ConvertFromUtf32(point);

            for (var unit = 0xD800; unit <= 0xDFFF; unit++)
                yield return ((char)unit).ToString();
        }

        /// <summary>
        /// Java's full case mapping, which Calcite's <c>UPPER</c> and <c>LOWER</c> are, maps every character into the
        /// classes the table says it does: its own, and for <c>UPPER</c> from U+E000 also those below U+D800.
        /// </summary>
        [Fact]
        public void TheCaseMappingsMapEveryCharacterWhereTheTableSays()
        {
            var escaped = new List<string>();

            foreach (var character in EveryCharacter())
            {
                var classes = JsonValues.MaskOf(character);

                foreach (var (function, mapped) in new (string, string)[] { ("UPPER", org.apache.calcite.runtime.SqlFunctions.upper(character)), ("LOWER", org.apache.calcite.runtime.SqlFunctions.lower(character)) })
                {
                    var held = JsonValues.MaskOf(mapped);
                    if ((held & ~StringCalls.Image(function, classes)) != JsonCharacterBand.None && escaped.Count < 10)
                        escaped.Add($"{function} of U+{char.ConvertToUtf32(character, 0):X4} is {string.Join(" ", mapped.EnumerateRunes().Select(r => $"U+{r.Value:X4}"))}, of {held}");
                }
            }

            escaped.Should().BeEmpty();
        }

        /// <summary>
        /// The table's one class that a case mapping adds is there because a character needs it: U+FB13, from U+E000,
        /// upper-cases to two Armenian letters below U+D800.
        /// </summary>
        [Fact]
        public void UpperMapsALigatureFromTheHighBandBelowTheSurrogates()
        {
            org.apache.calcite.runtime.SqlFunctions.upper("ﬓ").Should().Be("ՄՆ");
            StringCalls.Image("UPPER", JsonCharacterBand.HighBmp).Should().Be(JsonCharacterBand.HighBmp | JsonCharacterBand.NonAscii);
            StringCalls.Image("LOWER", JsonCharacterBand.HighBmp).Should().Be(JsonCharacterBand.HighBmp);
        }

        /// <summary>
        /// A window, a reversal and a trim keep characters of the classes they were given and no others: a window that
        /// splits a surrogate pair keeps a lone one, which is still a surrogate, and a reversal keeps a pair whole.
        /// </summary>
        [Fact]
        public void WhatKeepsCharactersKeepsTheirClasses()
        {
            const string Pair = "a😀b";

            JsonValues.MaskOf(org.apache.calcite.runtime.SqlFunctions.substring(Pair, 2, 1)).Should().Be(JsonCharacterBand.Surrogates);
            org.apache.calcite.runtime.SqlFunctions.reverse(Pair).Should().Be("b😀a");
            JsonValues.MaskOf(org.apache.calcite.runtime.SqlFunctions.trim(true, true, " ", "  a ")).Should().Be(JsonCharacterBand.NonAscii | JsonCharacterBand.Trimmed);

            foreach (var function in new[] { "SUBSTRING", "LEFT", "RIGHT", "REVERSE", "REPEAT", "CAST", "LOWER", "TRIM BOTH", "||", "CASE" })
                StringCalls.Image(function, JsonCharacterBand.Any).Should().Be(JsonCharacterBand.Any, function);
        }

    }

}
