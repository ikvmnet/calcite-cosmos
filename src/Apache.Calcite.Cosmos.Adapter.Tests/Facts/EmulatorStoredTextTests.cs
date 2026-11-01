using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// The emulator's spelling of what it stores, as its measurements read it: U+0000 and U+FDD0 escaped, the rest as
    /// written, and code-point order kept everywhere but at U+0000.
    /// </summary>
    public class EmulatorStoredTextTests
    {

        /// <summary>The strings the emulator probe stored, which the measurements read the model from.</summary>
        static readonly string[] Probed =
        [
            "", "a", "b", "z", "0", "1", "abc", "\0", "﷐", "\0\0", "﷐﷐", "\0﷐", "﷐\0",
            "a\0", "\0a", "a\0b", "a﷐", "﷐a", "a﷐b", "a\0﷐b", "﷐0", "﷐1", "a﷐0b",
            "", "￿", "A\0B", "\0b", "﷐b", "\u0001", "﷏", "﷑",
        ];

        [Theory]
        [InlineData("", "")]
        [InlineData("abc", "abc")]
        [InlineData("\0", "﷐0")]
        [InlineData("﷐", "﷐1")]
        [InlineData("a\0b﷐", "a﷐0b﷐1")]
        [InlineData("﷐0", "﷐10")]
        public void EachEscapeIsTwoUnits(string text, string stored)
        {
            EmulatorStoredText.Of(text).Should().Be(stored);
            EmulatorStoredText.Escapes(text).Should().Be(text != stored);
        }

        /// <summary>A string holding neither character is its own spelling, the same instance, so nothing is built.</summary>
        [Fact]
        public void AStringHoldingNeitherIsItself()
        {
            var text = "a\U0001F600﷏﷑";
            EmulatorStoredText.Of(text).Should().BeSameAs(text);
        }

        /// <summary>
        /// The spelling is one to one, so equality is kept both ways; and among strings holding no U+0000 it keeps
        /// code-point order, so U+FDD0 alone is no order defect.
        /// </summary>
        [Fact]
        public void TheSpellingKeepsEqualityAndAnOrderWithoutU0000()
        {
            foreach (var a in Probed)
                foreach (var b in Probed)
                {
                    (EmulatorStoredText.Of(a) == EmulatorStoredText.Of(b)).Should().Be(a == b, $"{Spell(a)} and {Spell(b)}");

                    if (a.Contains('\0') || b.Contains('\0'))
                        continue;

                    Math.Sign(string.CompareOrdinal(EmulatorStoredText.Of(a), EmulatorStoredText.Of(b))).Should().Be(Math.Sign(string.CompareOrdinal(a, b)), $"{Spell(a)} against {Spell(b)}");
                }
        }

        /// <summary>
        /// Where U+0000 meets a unit from U+0001 to U+FDCF at the first difference the spelling reverses the pair, which is
        /// the whole of the emulator's order defect.
        /// </summary>
        [Fact]
        public void U0000ReversesItsPairBelowTheEscape()
        {
            foreach (var other in new[] { "\u0001", "0", "a", "", "﷏" })
                string.CompareOrdinal(EmulatorStoredText.Of("\0"), EmulatorStoredText.Of(other)).Should().BePositive($"U+0000 is stored above {Spell(other)}");

            foreach (var other in new[] { "﷐", "﷑", "￿" })
                string.CompareOrdinal(EmulatorStoredText.Of("\0"), EmulatorStoredText.Of(other)).Should().BeNegative($"and below {Spell(other)}, as Calcite puts it");
        }

        static string Spell(string text) => string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"<U+{(int)c:X4}>"));

    }

}
