using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// The account the service-backed tests run against, and where it comes from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four test classes need a service, and until this they each said so the same way: a constant
    /// endpoint, a constant key, and an <c>Assert.Inconclusive</c> when nothing answered. That works
    /// and it hides things. A whole class of defect — a pushdown that plans correctly and returns the
    /// wrong rows — is invisible without a service, so a suite that quietly skips them reports success
    /// for a run that checked none of it. Three such defects reached a pull request that way.
    /// </para>
    /// <para>
    /// <b>The container is started lazily, and that is deliberate.</b> Most runs are planner tests and
    /// have no business waiting on Docker; nothing here touches it until a test actually asks for an
    /// account. A run that asks pays once, for the whole assembly.
    /// </para>
    /// <para>
    /// <b>Resolution order.</b> A named account wins — <c>COSMOS_TEST_ENDPOINT</c> and
    /// <c>COSMOS_TEST_KEY</c> — because the emulator does not implement everything the service does and
    /// naming an account is how a caller says to verify against the real thing. An emulator already
    /// listening comes next, which keeps the documented <c>docker run</c> workflow and CI's own
    /// emulator working untouched. Only then is one started here.
    /// </para>
    /// </remarks>
    [TestClass]
    public static class CosmosEmulator
    {

        /// <summary>
        /// The emulator image, over plain HTTP.
        /// </summary>
        /// <remarks>
        /// <c>vnext-preview</c> rather than the original Linux emulator: it serves HTTP on 8081 and
        /// starts in seconds, where the original wants HTTPS with a certificate the client has to be
        /// told to trust and takes minutes. This is the image the tests already documented.
        /// </remarks>
        const string Image = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

        const int Port = 8081;

        /// <summary>
        /// The well-known public emulator credentials, documented by Microsoft. Not a secret.
        /// </summary>
        public const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        const string EmulatorEndpoint = "http://localhost:8081/";

        static readonly SemaphoreSlim _gate = new(1, 1);
        static bool _resolved;
        static string? _endpoint;
        static string? _key;
        static bool _isEmulator;
        static string? _unavailable;
        static IContainer? _container;

        /// <summary>
        /// Gets the endpoint to run against, or <c>null</c> where none could be reached.
        /// </summary>
        public static string? Endpoint => Resolve().Endpoint;

        /// <summary>
        /// Gets the key for <see cref="Endpoint"/>.
        /// </summary>
        public static string? Key => Resolve().Key;

        /// <summary>
        /// Gets whether the account is an emulator rather than the service.
        /// </summary>
        /// <remarks>
        /// The emulator does not implement everything — full text search is the case that prompted
        /// the distinction — so a test whose subject the emulator lacks reports inconclusive against
        /// one and runs against an account.
        /// </remarks>
        public static bool IsEmulator => Resolve().IsEmulator;

        /// <summary>
        /// Gets why no account is available, or <c>null</c> where one is.
        /// </summary>
        public static string? Unavailable => Resolve().Unavailable;

        /// <summary>
        /// Reports inconclusive where no account could be reached.
        /// </summary>
        public static void RequireAccount()
        {
            if (Unavailable is string reason)
                Assert.Inconclusive(reason);
        }

        static (string? Endpoint, string? Key, bool IsEmulator, string? Unavailable) Resolve()
        {
            if (_resolved == false)
            {
                _gate.Wait();

                try
                {
                    if (_resolved == false)
                    {
                        ResolveCore().GetAwaiter().GetResult();
                        _resolved = true;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            return (_endpoint, _key, _isEmulator, _unavailable);
        }

        static async Task ResolveCore()
        {
            // A named account wins: the emulator does not implement everything, and naming one is how
            // a caller says to verify against the service.
            if (Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is string named && named.Length > 0)
            {
                _endpoint = named;
                _key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");
                _isEmulator = false;

                if (string.IsNullOrEmpty(_key))
                    _unavailable = "COSMOS_TEST_ENDPOINT is set without COSMOS_TEST_KEY.";

                return;
            }

            _isEmulator = true;
            _key = EmulatorKey;

            // Presence is asked of the port and readiness of the account, and they are different
            // questions: an emulator answers 503 for some seconds after the port opens. Asking the
            // account first, with a budget short enough not to stall a run, reported "nothing there"
            // for an emulator that was merely still starting -- and then failed to start a second
            // one, the port being taken.
            if (await Listening(Port).ConfigureAwait(false))
            {
                if (await Answers(EmulatorEndpoint, TimeSpan.FromMinutes(2)).ConfigureAwait(false))
                    _endpoint = EmulatorEndpoint;
                else
                    _unavailable = $"Something is listening on port {Port} but no Cosmos DB account answered there.";

                return;
            }

            try
            {
                // Bound to the same port rather than a mapped one. The emulator reports its own
                // address in the account metadata the SDK reads, and that address is the port inside
                // the container; a mapped port therefore answers the first request and then sends the
                // client somewhere that is not listening.
                var container = new ContainerBuilder()
                    .WithImage(Image)
                    .WithPortBinding(Port, Port)
                    .Build();

                await container.StartAsync().ConfigureAwait(false);
                _container = container;
            }
            catch (Exception e)
            {
                _unavailable = $"No Cosmos DB account reachable at {EmulatorEndpoint} and none could be started: {e.Message}";
                return;
            }

            // Readiness is the account answering rather than the port opening, which is what the
            // poll below decides. No wait strategy is configured for that reason.
            if (await Answers(EmulatorEndpoint, TimeSpan.FromMinutes(2)).ConfigureAwait(false) == false)
            {
                _unavailable = $"An emulator was started but did not answer at {EmulatorEndpoint}.";
                return;
            }

            _endpoint = EmulatorEndpoint;
        }

        /// <summary>
        /// Determines whether anything holds the port.
        /// </summary>
        /// <remarks>
        /// Which is how an emulator someone else started is told from no emulator at all. It says
        /// nothing about readiness — that is <see cref="Answers"/> — and the two are separated
        /// because an emulator answers 503 for some seconds after the port opens, so a readiness
        /// check alone reports the wrong thing about a container that is merely still starting.
        /// </remarks>
        static async Task<bool> Listening(int port)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await client.ConnectAsync("localhost", port, cts.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Polls an account until it answers, or the budget runs out.
        /// </summary>
        static async Task<bool> Answers(string endpoint, TimeSpan budget)
        {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < budget)
            {
                try
                {
                    using var client = new CosmosClient(endpoint, EmulatorKey, new CosmosClientOptions
                    {
                        ConnectionMode = ConnectionMode.Gateway,
                        RequestTimeout = TimeSpan.FromSeconds(5),
                    });

                    await client.ReadAccountAsync().ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }

            return false;
        }

        /// <summary>
        /// Stops the container, where this run started one.
        /// </summary>
        [AssemblyCleanup]
        public static async Task AssemblyCleanup()
        {
            if (_container is IContainer container)
            {
                _container = null;
                await container.DisposeAsync().ConfigureAwait(false);
            }
        }

    }

}
