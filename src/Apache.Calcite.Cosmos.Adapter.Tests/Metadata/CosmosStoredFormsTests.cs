using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// What the dispatcher does before any family reads a pattern, and what it refuses outright.
    /// </summary>
    /// <remarks>
    /// The families are tested beside the classes that hold them, the reader of the pattern beside its own class, and
    /// every spelling the forms were ever recognised under in <c>CosmosStoredFormsSpellingTests</c>; what is left here
    /// is what every form rests on — one language however it is written, and the anchoring without which no form means
    /// anything.
    /// </remarks>
    public class CosmosStoredFormsTests
    {

        /// <summary>
        /// Every stored form writes ASCII, so a path in one holds no character at or above U+D800, as each form states of
        /// itself for the theory to read (<see cref="CosmosRepresentation.Characters"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What the service's order of such a path rests on.</b> The service orders a document's strings by code
        /// point and Calcite by UTF-16 code unit, and the two part only over a surrogate beside a unit from U+E000 (see
        /// <see cref="CosmosServiceOrder"/>), which a path in a stored form never holds — so a stored form is a proof of
        /// the path's characters, and a form registered later that wrote anything else would be a wrong order this
        /// catches.
        /// </para>
        /// <para>
        /// Each form's language is the canonical spelling it renders (<see cref="CosmosStoredForms"/>): a form is
        /// recognised only from a pattern whose every string is canonical. So rendering values across each form's range
        /// is rendering its language's shape.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryStoredFormWritesAscii()
        {
            var instants = new[] { new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 15, 10, 30, 56, DateTimeKind.Utc), new DateTime(2024, 1, 15, 10, 30, 56, 123, DateTimeKind.Utc), new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc) };
            var uuids = new[] { Guid.Empty, Guid.Parse("123e4567-e89b-12d3-a456-426614174000"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") };
            var integers = new long[] { 0, 7, 42, 99999, long.MaxValue };

            var forms = CosmosStoredForms.Registered.ToList();
            var writing = 0;

            foreach (var form in forms)
            {
                // A form of a time of day writes no literal from an instant, its literals coming from elsewhere; what it
                // is held to is that what it writes is ASCII.
                var written = instants.Select(i => CosmosStoredForms.RenderDateTime(form, i))
                    .Concat(uuids.Select(u => CosmosStoredForms.RenderUuid(form, u)))
                    .Concat(integers.Select(n => CosmosStoredForms.RenderInteger(form, n)))
                    .OfType<string>()
                    .ToList();

                writing += written.Count > 0 ? 1 : 0;
                written.Should().OnlyContain(text => text.All(c => c < ''), $"{form.Name} writes ASCII");
                form.Characters.Should().Be(JsonCharacterBand.None, $"{form.Name} states that it is written below U+D800");
            }

            writing.Should().BeGreaterThan(forms.Count / 2, "most forms write a literal from these values, so this asks something of them");
        }

        /// <summary>
        /// Written with <c>\d</c> it is the same language, which ECMA-262 makes <c>[0-9]</c>.
        /// </summary>
        [Fact]
        public void TheDigitShorthandIsTheSamePattern()
        {
            CosmosStoredForms.Recognise(@"^\d{5}$").Should().Be(CosmosStoredForms.Recognise("^[0-9]{5}$"));
        }

        /// <summary>
        /// An unanchored pattern constrains nothing, because JSON Schema searches rather than matches.
        /// </summary>
        /// <remarks>
        /// <c>[0-9]{5}</c> is satisfied by <c>x12345y</c>. Treating it as a fixed width would be a
        /// claim about documents the schema never made.
        /// </remarks>
        [Fact]
        public void AnUnanchoredPatternIsRecognisedAsNothing()
        {
            CosmosStoredForms.Recognise("[0-9]{5}").Should().BeNull();
            CosmosStoredForms.Recognise("^[0-9]{5}").Should().BeNull();
            CosmosStoredForms.Recognise("[0-9]{5}$").Should().BeNull();
        }

    }

}
