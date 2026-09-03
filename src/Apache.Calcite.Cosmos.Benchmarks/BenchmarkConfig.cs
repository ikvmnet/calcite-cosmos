using System;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;

namespace Apache.Calcite.Cosmos.Benchmarks
{

    /// <summary>
    /// How every benchmark here is run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two departures from the defaults, both because of what is being measured.
    /// </para>
    /// <para>
    /// <b>Allocation is reported.</b> A Volcano search is a graph of rule matches, equivalence sets
    /// and interned traits, and the number of bytes it allocates to answer one statement is a more
    /// stable signal than the time it took — it does not move with the machine, and a rule that
    /// starts producing an alternative nothing selects shows up here before it shows up in a mean.
    /// </para>
    /// <para>
    /// <b>Results are declared in corpus order.</b> The default orders by mean, which puts the
    /// cheap statements together and the expensive ones together and makes two runs hard to read
    /// against each other. Declaration order keeps a statement in the same row between runs.
    /// </para>
    /// </remarks>
    public sealed class BenchmarkConfig : ManualConfig
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="args">The command line, consulted only for whether it names a job of its
        /// own.</param>
        public BenchmarkConfig(string[] args)
        {
            Add(DefaultConfig.Instance);

            AddDiagnoser(MemoryDiagnoser.Default);

            // JSON beside the markdown the default config already exports, so a run can be diffed
            // against another run by something other than eye.
            AddExporter(JsonExporter.Full);

            WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared));

            // The parameter is a statement name and the default budget of twenty characters elides
            // the half that identifies it — `Compo(...)ation [31]` names nothing. Wide enough for
            // the longest name in the corpus.
            WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(40));

            // The IKVM-compiled Java assemblies — calcite.core and avatica.core — carry a
            // DebuggableAttribute that says they were not optimized, because that is what the
            // compiler that produced them emits regardless of configuration. BenchmarkDotNet's
            // validator reads that attribute and refuses to run rather than publish numbers from a
            // debug build, which is right for a C# project and wrong here: this project and the
            // adapter are built Release, and there is no Release build of those two to switch to.
            WithOptions(ConfigOptions.DisableOptimizationsValidator);

            // Planning one statement is milliseconds rather than nanoseconds, so the default
            // iteration count buys precision nothing here needs at a cost of minutes per statement.
            //
            // Added only where the command line named no job of its own. BenchmarkDotNet merges the
            // two rather than replacing one with the other, so a run asked for `--job short` would
            // otherwise measure everything twice and report two rows per benchmark — which reads as
            // a comparison and is not one.
            if (NamesAJob(args) == false)
                AddJob(Job.Default
                    .WithWarmupCount(3)
                    .WithIterationCount(10)
                    .WithId("Planner"));
        }

        /// <summary>
        /// Determines whether the command line names a job.
        /// </summary>
        /// <param name="args">The command line.</param>
        /// <returns><c>true</c> where it does.</returns>
        static bool NamesAJob(string[] args)
        {
            foreach (var arg in args)
                if (string.Equals(arg, "--job", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("--job=", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-j", StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

    }

}
