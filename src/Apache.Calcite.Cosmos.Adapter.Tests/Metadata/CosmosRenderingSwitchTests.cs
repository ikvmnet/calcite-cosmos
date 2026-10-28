using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The switch (spec g16): a schema's first plan starts its rendering graph's build, and a question S declines leaves its
    /// conjunct in process with nothing sent, counted on <c>cosmos.rendering.declines</c> by container and reason (O8).
    /// </summary>
    public sealed class CosmosRenderingSwitchTests
    {

        const string Filtered = "SELECT c.\"id\" FROM products AS c WHERE c.\"$.category\" = 'bikes'";

        /// <summary>
        /// A table's first <c>toRel</c> warms its schema's reasoning, once; a statement only validated, which reads the row
        /// type alone, builds nothing, nor does a plan in a schema a test turned S off in.
        /// </summary>
        [Fact]
        public void AFirstPlanStartsItsSchemasBuild()
        {
            var (validated, off, planned) = (new CosmosReasoning(), new CosmosReasoning(new JsonFactGenerations(), renderingOff: true), new CosmosReasoning());

            var harness = new PlannerHarness(validated);
            harness.Validate(harness.Parse(Filtered));
            new PlannerHarness(off).PlanToAsync(Filtered);
            new PlannerHarness(planned).PlanToAsync(Filtered);

            (validated.Rendering.Graph.Started, off.Rendering.Graph.Started, planned.Rendering.Graph.Started).Should().Be((false, false, true));
            planned.Rendering.Ready.Wait();
        }

        /// <summary>
        /// Where S cannot answer — here a definition that throws, so no generation ever answers — an owned conjunct is
        /// declined: kept in process with no weakening, the container read for it, and counted once a question under the
        /// container, reason <c>unavailable</c>.
        /// </summary>
        [Fact]
        public void ADeclineReadsTheContainerAndIsCounted()
        {
            var declining = new CosmosReasoning(new JsonFactGenerations(), new RenderingOracle(new RenderingGraph(() => throw new InvalidOperationException("planted")), wait: TimeSpan.Zero));
            var counted = new List<(long Count, string? Container, string? Reason)>();
            using var listener = new MeterListener
            {
                // By name, never by reference: the meter publishes each instrument as the type's initializer creates it,
                // before the field is assigned, so a run where this plan initializes it would read null.
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == CosmosInstrumentation.Name && instrument.Name == "cosmos.rendering.declines")
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var all = tags.ToArray().ToDictionary(t => t.Key, t => t.Value as string);
                if (all.GetValueOrDefault("cosmos.container") == "products")
                    lock (counted)
                        counted.Add((value, all.GetValueOrDefault("cosmos.container"), all.GetValueOrDefault("cosmos.rendering.reason")));
            });
            listener.Start();

            var planned = new PlannerHarness(declining).PlanToAsync(Filtered);
            var cosmos = Planning.PlanDigests.Subtrees(planned, n => n is Adapter.Rel.CosmosRel).Single();
            var sent = PlannerHarness.Implement(cosmos, PlannerHarness.TableOf(cosmos)!.Container).Sql;

            sent.Should().NotContain("WHERE", "a declined conjunct sends nothing: " + sent);
            PlannerHarness.Explain(planned).Should().Contain("ClrCursorFilter", "and is decided in process");
            lock (counted)
                counted.Should().NotBeEmpty().And.OnlyContain(c => c.Count == 1 && c.Reason == CosmosInstrumentation.RenderingReasons.Unavailable);
        }

    }

}
