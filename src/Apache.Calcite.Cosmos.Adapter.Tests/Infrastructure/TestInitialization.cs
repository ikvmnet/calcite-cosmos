using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// MSTest ran this assembly's tests one at a time, and nothing here is safe to run otherwise: the
// boot class path below is process-wide, Calcite's planner registries are static, and the
// service-backed classes share one emulator and one account. Parallelism is therefore a change to
// make deliberately and measure, not one to inherit from a framework default.
[assembly: Parallelization(Mode = ParallelMode.None)]

// Stops the emulator container once the whole assembly is done, which is what MSTest's
// [AssemblyCleanup] did.
[assembly: AssemblyFixture(typeof(CosmosEmulatorLifetime))]

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// Process-wide setup for the test assembly.
    /// </summary>
    public static class TestInitialization
    {

        /// <summary>
        /// Runs the adapter's own module initializer, which has to precede anything that touches
        /// Calcite.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>CosmosModelClasses</c> appends to <c>calcite.model.classes.allowed</c> from a module
        /// initializer of its own, and Calcite reads that property <em>once</em>, when
        /// <c>CalciteSystemProperty</c> initializes, into a filter it holds in a static. The
        /// allowlist is fail-closed — an empty one rejects everything — so losing that race does not
        /// degrade anything gracefully: asking Calcite for its own spatial library then throws
        /// <c>SecurityException: Class 'org.apache.calcite.runtime.SpatialTypeFunctions' rejected by
        /// the allowlist</c>, permanently, for the life of the process.
        /// </para>
        /// <para>
        /// <b>A module initializer runs when its assembly loads, and the adapter's assembly loads
        /// when a test first uses a type in it</b> — which is not necessarily before some other test
        /// has already made Calcite read the property. Under MSTest that never showed, and it would
        /// have been luck rather than order: xUnit v3 randomizes test order per run, so the losing
        /// order is reachable on any leg and on any run. It was reached on <c>net8.0:win-x64</c>
        /// first, taking <c>CosmosSchemaFunctionTests</c>' two library tests with it, while the same
        /// commit passed on <c>linux-arm64</c>.
        /// </para>
        /// <para>
        /// So the order is stated rather than hoped for. This assembly's module initializer runs
        /// before any test in it, and <see cref="RuntimeHelpers.RunModuleConstructor"/> is what makes
        /// the adapter's run now rather than whenever a type of its own is first touched; it is
        /// idempotent, so the adapter reaching it by the ordinary route as well costs nothing. This
        /// is the same thing <c>README.md</c> tells a consumer whose only mention of the adapter is
        /// the factory name in a model.
        /// </para>
        /// </remarks>
        static void RunTheAdapterModuleInitializer()
        {
            RuntimeHelpers.RunModuleConstructor(typeof(CosmosSchemaFactory).Module.ModuleHandle);
        }

        /// <summary>
        /// The one module initializer, so that the two steps below are ordered rather than merely
        /// both present.
        /// </summary>
        /// <remarks>
        /// Roslyn does not promise an order between separate <see cref="ModuleInitializerAttribute"/>
        /// methods, and the order is the whole point here: the boot class path touches
        /// <c>calcite.core</c>, and the allowlist has to be set before anything touches Calcite at
        /// all. Sequencing them inside one method is what says so.
        /// </remarks>
        [ModuleInitializer]
        internal static void Initialize()
        {
            RunTheAdapterModuleInitializer();
            AddCalciteToBootClassPath();
        }

        /// <summary>
        /// Publishes <c>calcite.core</c> into the boot class loader.
        /// </summary>
        /// <remarks>
        /// <para>
        /// IKVM gives each assembly its own class loader, whereas a JVM has one flat classpath.
        /// Avatica's <c>UnregisteredDriver</c> resolves the Calcite JDBC factory with
        /// <c>Class.forName("org.apache.calcite.jdbc.CalciteJdbc41Factory")</c>, which binds
        /// against the calling class's loader — <c>avatica.core</c>. That factory lives in
        /// <c>calcite.core</c>, and avatica does not reference calcite: the dependency runs the
        /// other way. The class is present and loadable, but not by the loader doing the lookup,
        /// so the driver's static initializer fails with <c>ClassNotFoundException</c> and takes
        /// every Calcite entry point that opens an internal connection down with it —
        /// <c>Frameworks.getPlanner</c> and <c>RelBuilder.create</c> among them.
        /// </para>
        /// <para>
        /// Adding the assembly to the boot class path makes its classes visible to
        /// <c>Class.forName</c> from anywhere, which restores the flat-classpath assumption the
        /// Java code was written against.
        /// </para>
        /// <para>
        /// A module initializer rather than an assembly fixture: the addition has to precede the
        /// first load of the driver, and a type initializer runs once with its failure cached for
        /// the life of the process. Test-framework hooks do not reliably run early enough, whichever
        /// framework it is.
        /// </para>
        /// </remarks>
        static void AddCalciteToBootClassPath()
        {
            ikvm.runtime.Startup.addBootClassPathAssembly(typeof(org.apache.calcite.jdbc.CalciteFactory).Assembly);
        }

    }

    /// <summary>
    /// Holds the emulator container open for the run and disposes it at the end.
    /// </summary>
    /// <remarks>
    /// <see cref="CosmosEmulator"/> starts a container only where no account is named and none is
    /// already listening, so on most runs there is nothing to stop and this does nothing.
    /// </remarks>
    public sealed class CosmosEmulatorLifetime : IAsyncLifetime
    {

        public ValueTask InitializeAsync() => default;

        public async ValueTask DisposeAsync() => await CosmosEmulator.StopAsync();

    }

}
