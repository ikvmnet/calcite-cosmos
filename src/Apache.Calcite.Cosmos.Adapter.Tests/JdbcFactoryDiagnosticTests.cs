using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.tools;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Pins the class loading behaviour that <see cref="TestInitialization"/> works around.
    /// </summary>
    [TestClass]
    public class JdbcFactoryDiagnosticTests
    {

        const string Factory = "org.apache.calcite.jdbc.CalciteJdbc41Factory";

        /// <remarks>
        /// The class is present and loadable — the original diagnosis that IKVM had failed to
        /// compile it was wrong.
        /// </remarks>
        [TestMethod]
        public void FactoryClassIsPresent()
        {
            java.lang.Class.forName(Factory).getName().Should().Be(Factory);
        }

        /// <remarks>
        /// Each assembly gets its own loader, so a class in <c>calcite.core</c> is not visible to
        /// one belonging to an assembly that does not reference it. This is the mechanism behind
        /// the driver failure.
        /// </remarks>
        [TestMethod]
        public void ClassesAreScopedToTheirAssemblyLoader()
        {
            var calcite = java.lang.Class.forName("org.apache.calcite.jdbc.CalciteFactory");
            var avatica = java.lang.Class.forName("org.apache.calcite.avatica.UnregisteredDriver");

            calcite.getClassLoader().Should().NotBeSameAs(avatica.getClassLoader());

            // Which assembly a class came from is asked of the class rather than read out of its
            // loader's ToString. IKVM 8.16 prints every loader as
            // "ikvm.runtime.AppDomainAssemblyClassLoader@<hash>", so the assembly name is no longer
            // in that string — a change to how a loader formats itself, not to the scoping this
            // pins, which the distinct loaders above still say.
            AssemblyOf(calcite).Should().Be("calcite.core");
            AssemblyOf(avatica).Should().Be("avatica.core");
        }

        /// <remarks>
        /// With <c>calcite.core</c> on the boot class path the driver initializes, so the Calcite
        /// entry points that open an internal connection work normally.
        /// </remarks>
        [TestMethod]
        public void FrameworksEntryPointsWorkOnceTheAssemblyIsOnTheBootClassPath()
        {
            var root = Frameworks.createRootSchema(true);
            var cosmos = root.add("cosmos", new CosmosSchema(new[] { new CosmosContainerMetadata("products", new[] { "/pk" }) }));

            var config = Frameworks.newConfigBuilder().defaultSchema(cosmos).build();
            var scan = RelBuilder.create(config).scan("products").build();

            // Resolved through CosmosTable.toRel, so the scan arrives in the Cosmos convention.
            scan.getRelTypeName().Should().Be("CosmosTableScan");
        }

        /// <summary>
        /// The name of the assembly <paramref name="cls"/> was compiled into.
        /// </summary>
        static string? AssemblyOf(java.lang.Class cls) =>
            ikvm.runtime.Util.getInstanceTypeFromClass(cls).Assembly.GetName().Name;

    }

}
