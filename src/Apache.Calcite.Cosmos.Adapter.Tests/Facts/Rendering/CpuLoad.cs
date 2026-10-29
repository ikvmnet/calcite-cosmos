using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The machine's load, sampled every few seconds while a timing runs, so that a figure is read beside the load it was
    /// taken at (the owner's rule: never wait for quiet; record the load). The share of every processor's time not idle
    /// over each interval, from the system's own counters: <c>GetSystemTimes</c> on Windows, <c>/proc/stat</c> on Linux.
    /// </summary>
    internal sealed class CpuLoad : IDisposable
    {

        readonly List<double> _samples = [];
        readonly Timer _timer;
        (ulong Idle, ulong Total)? _last;

        /// <summary>Starts sampling, every <paramref name="seconds"/>.</summary>
        public CpuLoad(int seconds = 5)
        {
            _last = Times();
            _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
        }

        /// <summary>The load over each interval so far, in percent.</summary>
        public IReadOnlyList<double> Samples
        {
            get
            {
                lock (_samples)
                    return [.. _samples];
            }
        }

        /// <summary>The samples' median and 90th centile, and how many there were; with the load since the last sample where there are none.</summary>
        public override string ToString()
        {
            Sample();
            var sorted = Samples.Order().ToList();
            return sorted.Count == 0 ? "load not read" : $"load median {sorted[sorted.Count / 2]:F0} %, p90 {sorted[Math.Min(sorted.Count - 1, sorted.Count * 9 / 10)]:F0} % over {sorted.Count} samples";
        }

        void Sample()
        {
            lock (_samples)
            {
                if (Times() is not { } now || _last is not { } last || now.Total <= last.Total)
                    return;

                _samples.Add(100.0 * (1 - (double)(now.Idle - last.Idle) / (now.Total - last.Total)));
                _last = now;
            }
        }

        public void Dispose() => _timer.Dispose();

        /// <summary>Every processor's idle and total time so far, in the system's own units; none where neither counter is read.</summary>
        static (ulong Idle, ulong Total)? Times()
        {
            if (OperatingSystem.IsWindows())
                return GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : null;

            if (OperatingSystem.IsLinux() && File.ReadLines("/proc/stat").FirstOrDefault() is { } line && line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToList();
                return (fields[3] + (fields.Count > 4 ? fields[4] : 0), fields.Aggregate(0UL, (a, b) => a + b));
            }

            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    }

}
