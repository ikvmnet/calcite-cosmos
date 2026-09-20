using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// What the dispatcher does before any family reads a pattern, and what it refuses outright.
    /// </summary>
    /// <remarks>
    /// The families are tested beside the classes that hold them; what is left here is the pattern
    /// language every one of them rests on — the rewrites that make one language written several ways
    /// into one key, and the anchoring without which no form means anything.
    /// </remarks>
    public class CosmosStoredFormsTests
    {

        /// <summary>
        /// Written with <c>\d</c> it is the same pattern, which normalisation already knew.
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
