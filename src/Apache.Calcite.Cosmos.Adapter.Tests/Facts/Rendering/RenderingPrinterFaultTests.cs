using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The printer's faults (spec g13; line 17), each planted in the flow alone (O4): an <c>OR</c> in an <c>AND</c>
    /// unparenthesised (M12), a member dropped (M13), a <c>NOT</c> over a bare comparison (M14), parameters bound out of
    /// order (M15), a property misspelled (M16). Every one is caught by the round trip over the forms S chose in the pull
    /// request's run, and none reaches a flow that did not plant it.
    /// </summary>
    public sealed class RenderingPrinterFaultTests
    {

        static readonly RenderingPrinterFault[] Faults = [RenderingPrinterFault.M12, RenderingPrinterFault.M13, RenderingPrinterFault.M14, RenderingPrinterFault.M15, RenderingPrinterFault.M16];

        /// <summary>Whether a fault can show in a form: the shape its writing site meets.</summary>
        static bool Meets(RenderingPrinterFault fault, ServiceForm form) => fault switch
        {
            RenderingPrinterFault.M12 => form.Subforms.Any(f => f.Kind == ServiceFormKind.And && f.Members.Any(m => m.Kind == ServiceFormKind.Or)),
            RenderingPrinterFault.M13 => form.Subforms.Any(f => f.Kind is ServiceFormKind.And or ServiceFormKind.Or),
            RenderingPrinterFault.M14 => form.Subforms.Any(f => f.Kind == ServiceFormKind.Not && f.Members[0].Kind == ServiceFormKind.Compare),
            RenderingPrinterFault.M15 => form.Subforms.Any(f => f.Kind == ServiceFormKind.In && f.Literals.Count > 1
                || f.Kind is ServiceFormKind.And or ServiceFormKind.Or && f.Members.Where(m => m.Kind == ServiceFormKind.Compare).Select(m => m.Literal).Distinct().Count() > 1),
            _ => form.Paths.Count > 0,
        };

        /// <summary>
        /// Each fault, planted, makes the round trip part on some form S chose, and on every form whose shape it meets but
        /// those it cannot change; read before any plant, so the run's own planning sees none.
        /// </summary>
        [Fact]
        public void EachFaultIsCaughtByTheRoundTrip()
        {
            var chosen = RenderingPrinterTests.Chosen();
            var caught = new Dictionary<RenderingPrinterFault, (int Meets, int Caught)>();
            foreach (var fault in Faults)
                using (RenderingPrinterFaults.Plant(fault))
                    caught[fault] = (chosen.Count(c => Meets(fault, c.Form)), chosen.Count(c => RenderingPrinterTests.RoundTrip(c.Form, c.Paths) is not null));

            foreach (var (fault, (meets, found)) in caught)
                TestContext.Current.TestOutputHelper?.WriteLine($"{fault}: caught on {found} of the {chosen.Count} forms chosen, {meets} of which hold its shape");

            caught.Where(c => c.Value.Caught == 0).Select(c => c.Key).Should().BeEmpty("every fault is caught (5 of 5)");
            chosen.Select(c => RenderingPrinterTests.RoundTrip(c.Form, c.Paths)).OfType<string>().Should().BeEmpty("unplanted, nothing parts");
        }

        /// <summary>A plant reaches only its own flow: one started without it prints as unplanted, and the flow prints so again once it is disposed.</summary>
        [Fact]
        public void APlantReachesNoOtherFlow()
        {
            var category = SqlValue.Spell(JsonDocumentPath.Root.Property("category"));
            var paths = new Dictionary<string, JsonDocumentPath> { [category] = JsonDocumentPath.Root.Property("category") };
            var form = ServiceForm.NotOf(ServiceForm.CompareOf(CosmosServiceTest.Equal, category, JsonValues.Of("bikes")));
            string Print() => RenderingPrinter.Print(form, paths, CosmosImplementor.DefaultRootAlias, new CosmosParameterList());

            var unplanted = Print();
            string planted, other = "";
            using (RenderingPrinterFaults.Plant(RenderingPrinterFault.M14 | RenderingPrinterFault.M16))
            {
                planted = Print();

                // A thread started without the flow's context, as another test's flow is: never inlined into this one.
                var elsewhere = new Thread(() => other = Print());
                elsewhere.UnsafeStart();
                elsewhere.Join();
            }

            (unplanted, planted, other, Print()).Should().Be(("(NOT (c.category = @p0))", "(NOT c.Category = @p0)", "(NOT (c.category = @p0))", "(NOT (c.category = @p0))"));
        }

    }

}
