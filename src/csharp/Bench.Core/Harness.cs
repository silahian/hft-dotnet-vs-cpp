using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Bench.Core;

public sealed class SeriesResult
{
    public required string Name { get; init; }
    public required ulong ChecksumValue { get; init; }
    public required long OpsPerRep { get; init; }
    public required int RepsPerSeries { get; init; }
    public required double[] SeriesNs { get; init; }
    public required long AllocatedBytes { get; init; }
    public required int Gen0Collections { get; init; }

    public double[] NsPerOp
    {
        get
        {
            var r = new double[SeriesNs.Length];
            double denom = (double)OpsPerRep * RepsPerSeries;
            for (int i = 0; i < r.Length; i++) r[i] = SeriesNs[i] / denom;
            return r;
        }
    }
}

public static class Harness
{
    /// <summary>Prevents the JIT from eliding the workload result.</summary>
    private static ulong _sink;
    public static ulong Sink => Volatile.Read(ref _sink);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(ulong v) => Volatile.Write(ref _sink, v);

    public static SeriesResult Measure(
        string name, long opsPerRep, int warmupReps, int seriesCount, int repsPerSeries, Func<ulong> body)
    {
        ulong checksum = 0;

        // Warmup: drives tiered JIT to tier-1 + applies dynamic PGO, warms caches/TLB.
        for (int i = 0; i < warmupReps; i++)
        {
            checksum = body();
            Consume(checksum);
        }

        // Settle the heap so allocation accounting during measurement is meaningful.
        // Skipped inside a NoGCRegion: an explicit collect would terminate the region,
        // which is precisely what the --nogc experiment is trying to hold open.
        if (System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.NoGCRegion)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        }

        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Before = GC.CollectionCount(0);

        var seriesNs = new double[seriesCount];
        double tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;

        for (int s = 0; s < seriesCount; s++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int r = 0; r < repsPerSeries; r++)
            {
                checksum = body();
                Consume(checksum);
            }
            long t1 = Stopwatch.GetTimestamp();
            seriesNs[s] = (t1 - t0) * tickToNs;
        }

        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        int gen0After = GC.CollectionCount(0);

        return new SeriesResult
        {
            Name = name,
            ChecksumValue = checksum,
            OpsPerRep = opsPerRep,
            RepsPerSeries = repsPerSeries,
            SeriesNs = seriesNs,
            AllocatedBytes = allocAfter - allocBefore,
            Gen0Collections = gen0After - gen0Before,
        };
    }

    // ------------------------------------------------------------------ host control

    public static bool PinToCore(int core)
    {
        try
        {
            var p = Process.GetCurrentProcess();
            p.PriorityClass = ProcessPriorityClass.High;
            p.ProcessorAffinity = (IntPtr)(1L << core);
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] could not pin to core {core}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Measures a fixed dependent-integer spin loop on each logical core so the
    /// P-core / E-core split is observed rather than assumed from enumeration order.
    /// </summary>
    public static double[] ProbeCores(int coreCount, int iterations = 40_000_000)
    {
        var result = new double[coreCount];
        var p = Process.GetCurrentProcess();
        IntPtr original = p.ProcessorAffinity;
        double tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;

        try
        {
            for (int c = 0; c < coreCount; c++)
            {
                try { p.ProcessorAffinity = (IntPtr)(1L << c); }
                catch { result[c] = double.NaN; continue; }

                Thread.Sleep(15); // let the scheduler actually migrate
                Spin(iterations / 8);

                long t0 = Stopwatch.GetTimestamp();
                ulong v = Spin(iterations);
                long t1 = Stopwatch.GetTimestamp();
                Consume(v);

                result[c] = (t1 - t0) * tickToNs / iterations;
            }
        }
        finally
        {
            try { p.ProcessorAffinity = original; } catch { /* best effort */ }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Spin(int iterations)
    {
        ulong x = 88172645463325252UL;
        for (int i = 0; i < iterations; i++)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
        }
        return x;
    }

    // ------------------------------------------------------------------ reporting

    public static string DescribeRuntime()
    {
        string mode = RuntimeFeature.IsDynamicCodeCompiled ? "jit" : "nativeaot";
        return $"{mode}";
    }

    public static string ToJson(
        string runtimeTag, string flags, int core, IReadOnlyList<SeriesResult> results, double[]? coreProbeNs)
    {
        var sb = new StringBuilder(8192);
        var ci = CultureInfo.InvariantCulture;

        sb.Append("{\n");
        sb.Append($"  \"lang\": \"csharp\",\n");
        sb.Append($"  \"runtime\": \"{Esc(runtimeTag)}\",\n");
        sb.Append($"  \"codegen\": \"{Esc(Harness.DescribeRuntime())}\",\n");
        sb.Append($"  \"frameworkDescription\": \"{Esc(RuntimeInformation.FrameworkDescription)}\",\n");
        sb.Append($"  \"runtimeIdentifier\": \"{Esc(RuntimeInformation.RuntimeIdentifier)}\",\n");
        sb.Append($"  \"processArchitecture\": \"{Esc(RuntimeInformation.ProcessArchitecture.ToString())}\",\n");
        sb.Append($"  \"osDescription\": \"{Esc(RuntimeInformation.OSDescription)}\",\n");
        sb.Append($"  \"serverGC\": {(System.Runtime.GCSettings.IsServerGC ? "true" : "false")},\n");
        sb.Append($"  \"gcLatencyMode\": \"{Esc(System.Runtime.GCSettings.LatencyMode.ToString())}\",\n");
        sb.Append($"  \"flags\": \"{Esc(flags)}\",\n");
        sb.Append($"  \"pinnedCore\": {core},\n");
        sb.Append($"  \"stopwatchFrequency\": {Stopwatch.Frequency},\n");
        sb.Append($"  \"isHighResolution\": {(Stopwatch.IsHighResolution ? "true" : "false")},\n");

        if (coreProbeNs is not null)
        {
            sb.Append("  \"coreProbeNsPerIter\": [");
            for (int i = 0; i < coreProbeNs.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(coreProbeNs[i].ToString("F6", ci));
            }
            sb.Append("],\n");
        }

        sb.Append("  \"benchmarks\": [\n");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var ns = r.NsPerOp;
            sb.Append("    {\n");
            sb.Append($"      \"name\": \"{Esc(r.Name)}\",\n");
            sb.Append($"      \"checksum\": \"0x{r.ChecksumValue:X16}\",\n");
            sb.Append($"      \"opsPerRep\": {r.OpsPerRep},\n");
            sb.Append($"      \"repsPerSeries\": {r.RepsPerSeries},\n");
            sb.Append($"      \"allocatedBytes\": {r.AllocatedBytes},\n");
            sb.Append($"      \"gen0Collections\": {r.Gen0Collections},\n");
            sb.Append("      \"nsPerOpSeries\": [");
            for (int k = 0; k < ns.Length; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append(ns[k].ToString("F6", ci));
            }
            sb.Append("]\n");
            sb.Append(i == results.Count - 1 ? "    }\n" : "    },\n");
        }
        sb.Append("  ]\n");
        sb.Append("}\n");

        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
