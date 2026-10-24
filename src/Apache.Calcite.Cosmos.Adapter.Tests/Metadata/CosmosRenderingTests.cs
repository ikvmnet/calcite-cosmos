using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The set a container's statements are proven under (O1): read from the model's <c>serviceTarget</c> operand, carried
    /// on the container's metadata, <c>Production</c> on Azure and <c>Emulator</c> on the emulator, and in every key.
    /// </summary>
    public sealed class CosmosRenderingTests
    {

        static java.util.HashMap Operand(string? target)
        {
            var operand = new java.util.HashMap();
            if (target is not null)
                operand.put(CosmosSchemaFactory.ServiceTargetOperand, target);

            return operand;
        }

        /// <summary>
        /// Azure unless the model says the emulator; a name that is neither is a model error. The metadata carries it
        /// through every copy, and it chooses the set: an ordering over a label that may hold U+0000, which the emulator
        /// orders as stored, is proven for Azure and not for the emulator, each asked of the process's oracle under its key.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task TheTargetChoosesTheSet()
        {
            new[] { null, "azure", "Emulator", "EMULATOR" }.Select(t => CosmosSchemaFactory.ReadServiceTarget(Operand(t)))
                .Should().Equal(CosmosServiceTarget.Azure, CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator, CosmosServiceTarget.Emulator);
            FluentActions.Invoking(() => CosmosSchemaFactory.ReadServiceTarget(Operand("cloud"))).Should().Throw<ArgumentException>().WithMessage("*'azure' or 'emulator'*");

            var azure = new CosmosContainerMetadata("c");
            var emulated = azure.WithServiceTarget(CosmosServiceTarget.Emulator).WithFacts([Outright(P("label"), Present)]);
            (azure.ServiceTarget, emulated.ServiceTarget).Should().Be((CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator), "a copy carries it");
            (CosmosRendering.SetFor(azure.ServiceTarget), CosmosRendering.SetFor(emulated.ServiceTarget)).Should().Be((RenderingSet.Production, RenderingSet.Emulator));

            CosmosRendering.Warm();
            await CosmosRendering.Oracle.Ready;
            CosmosRendering.Oracle.Available.Should().BeTrue();
            var ordering = Cmp(Text(P("label")), "<", Lit("a"));
            var answers = new[] { azure, emulated }.Select(c => CosmosRendering.Oracle.Ask(CosmosRendering.SetFor(c.ServiceTarget), [ordering], null, new ImportSource("test", "test", 0), null, null)).ToList();
            answers.Select(a => a.Key!.Set).Should().Equal(RenderingSet.Production, RenderingSet.Emulator);
            answers.Select(a => a.Of(ordering).Kind).Should().Equal(RenderingVerdictKind.Proven, RenderingVerdictKind.Unproven);
        }

    }

}
