using System.Collections.Generic;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The S a translator test's translations ask of each call the function rows name, where no schema's reasoning is bound
    /// (G6): one over the registry's definition, built once a test process and waited for, as a schema's is once a schema.
    /// </summary>
    static class TranslatorRendering
    {

        /// <summary>Gets the S a test's translator without a schema asks.</summary>
        public static RenderingOracle Oracle { get; } = new(new RenderingGraph(() => CosmosRendering.Definition), wait: Timeout.InfiniteTimeSpan);

        /// <summary>A translator as <see cref="CosmosRexTranslator"/>'s public constructor makes one, asking <see cref="Oracle"/> where its container has no reasoning.</summary>
        public static CosmosRexTranslator Translator(RexBuilder rexBuilder, IReadOnlyList<CosmosPath?> fields, CosmosParameterList parameters, JsonFactSet facts,
            CorrelationId? ownRow = null, CosmosContainerMetadata? container = null, IReadOnlyList<CosmosReading>? readings = null) =>
            new(rexBuilder, fields, parameters, facts, ownRow, container, readings, Oracle);

    }

}
