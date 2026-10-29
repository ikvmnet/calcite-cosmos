using System;
using System.Runtime.InteropServices;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The calling thread's own processor time, the clock the cost tests hand <c>CosmosRenderingCost</c> so that S's time
    /// is read beside what a host's contention leaves of it: a thread waiting to run gains none. On Windows, the thread's
    /// cycles (<c>QueryThreadCycleTime</c>) at the rate a busy loop reads them against the thread's accounted time, since
    /// that time moves only at the clock's tick; on Linux, <c>CLOCK_THREAD_CPUTIME_ID</c>; elsewhere none.
    /// </summary>
    internal static class ThreadCpu
    {

        static readonly Lazy<double> CyclesPerTick = new(Calibrate);

        /// <summary>The clock, or none where the system offers no thread's time; on Windows, calibrated on first read.</summary>
        public static Func<TimeSpan>? Clock =>
            OperatingSystem.IsWindows() ? (CyclesPerTick.Value > 0 ? Cycles : null) : OperatingSystem.IsLinux() ? Linux : null;

        /// <summary>What the clock reads by, for the report.</summary>
        public static string Describe() =>
            OperatingSystem.IsWindows() ? $"thread time from QueryThreadCycleTime at {CyclesPerTick.Value * 10:F0} MHz, as the thread's accounted time reads it" :
            OperatingSystem.IsLinux() ? "thread time from CLOCK_THREAD_CPUTIME_ID" : "no thread clock";

        static TimeSpan Cycles() => QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? TimeSpan.FromTicks((long)(cycles / CyclesPerTick.Value)) : TimeSpan.Zero;

        static TimeSpan Linux() => clock_gettime(3, out var t) == 0 ? TimeSpan.FromTicks(t.Seconds * TimeSpan.TicksPerSecond + t.Nanoseconds / 100) : TimeSpan.Zero;

        /// <summary>
        /// The thread's cycles per 100 ns of its accounted time, over a busy loop of at least a second of it, each end read
        /// just as the accounted time moves; none where either is not read.
        /// </summary>
        static double Calibrate()
        {
            if (OperatingSystem.IsWindows() == false)
                return 0;

            var thread = GetCurrentThread();
            long Accounted() => GetThreadTimes(thread, out _, out _, out var kernel, out var user) ? kernel + user : -1;
            (long Time, ulong Cycles) Edge(long after)
            {
                long now;
                while ((now = Accounted()) <= after && now >= 0)
                    ;
                return QueryThreadCycleTime(thread, out var cycles) ? (now, cycles) : (-1, 0);
            }

            var start = Edge(Accounted());
            var end = Edge(start.Time + TimeSpan.TicksPerSecond - 1);
            return start.Time < 0 || end.Time <= start.Time ? 0 : (end.Cycles - start.Cycles) / (double)(end.Time - start.Time);
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        [DllImport("libc", EntryPoint = "clock_gettime")]
        static extern int clock_gettime(int clock, out TimeSpec time);

        [StructLayout(LayoutKind.Sequential)]
        struct TimeSpec
        {

            public long Seconds;
            public long Nanoseconds;

        }

    }

}
