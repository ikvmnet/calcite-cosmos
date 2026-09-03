using System;

using BenchmarkDotNet.Running;

namespace Apache.Calcite.Cosmos.Benchmarks
{

    /// <summary>
    /// The entry point.
    /// </summary>
    public static class Program
    {

        /// <summary>
        /// Runs a benchmark selection, or the corpus verification.
        /// </summary>
        /// <remarks>
        /// Everything but <c>verify</c> is handed to BenchmarkDotNet's switcher, so the whole of its
        /// command line — <c>--filter</c>, <c>--job</c>, <c>--list</c>, <c>--runtimes</c> — works
        /// here unchanged. See the README beside this file.
        /// </remarks>
        /// <param name="args">The command line.</param>
        /// <returns>Zero on success.</returns>
        public static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
                return Verification.Run(args[1..]);

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new BenchmarkConfig(args));
            return 0;
        }

    }

}
