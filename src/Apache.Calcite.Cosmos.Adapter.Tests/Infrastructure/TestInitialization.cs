using System.Runtime.CompilerServices;
using System.Threading.Tasks;

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
        [ModuleInitializer]
        internal static void AddCalciteToBootClassPath()
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
