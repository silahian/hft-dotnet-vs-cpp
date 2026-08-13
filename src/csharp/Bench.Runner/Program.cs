using Bench.Core;

namespace Bench.Runner;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        var opt = ParseArgs(args.AsSpan(1));
        return args[0] switch
        {
            "gen"    => CmdGen(opt),
            "probe"  => CmdProbe(opt),
            "stats"  => CmdStats(opt),
            "verify" => CmdVerify(opt),
            "run"    => CmdRun(opt),
            _        => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              bench-csharp gen   [--data DIR] [--book N] [--fix N] [--seed S]
              bench-csharp probe [--cores N]
              bench-csharp stats [--data DIR]
              bench-csharp run   [--data DIR] [--core N] [--warmup W] [--series S]
                                 [--reps R] [--out FILE] [--tag NAME] [--only NAME] [--probe]
            """);
        return 1;
    }

    /// <summary>
    /// Runs the operation-counting specialization of each workload and emits the
    /// counter vector. The C++ runner emits the same vector from the same source
    /// specialized the same way; `scripts/verify-equivalence.py` diffs them.
    ///
    /// This is what proves the two languages perform the same OPERATIONS, not merely
    /// produce the same output. The checksum oracle cannot do that on its own: a
    /// cheaper path that reaches the same answer would pass it.
    /// </summary>
    private static int CmdVerify(Dictionary<string, string> o)
    {
        string dir = Get(o, "data", DefaultDataDir());
        string outFile = Get(o, "out", Path.Combine(DefaultResultsDir(), "trace-csharp.json"));

        var book = BookEventFile.Load(Path.Combine(dir, "book_events.bin"));
        var fix = FixMessageFile.Load(Path.Combine(dir, "fix_messages.bin"));
        var bid = new long[book.TickCount];
        var ask = new long[book.TickCount];

        TraceCounters.Reset();
        ulong bookSum = OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);
        ulong fixSum = FixParseWorkload.Run<ScalarSum, CountTrace>(
            fix.Payload, fix.Offsets, fix.Count);

        // The TUNED variants must produce byte-identical counters and checksums to the
        // baseline. If tuning changed any branch or loop count it is no longer the same
        // work, and the comparison against C++ would be invalid.
        TraceCounters.Reset();
        ulong bookOptSum = OrderBookWorkload.RunOpt<CountTrace>(book.Events, bid, ask);
        ulong fixOptSum = FixParseWorkload.RunOpt<ScalarSum, CountTrace>(
            fix.Payload, fix.Offsets, fix.Count);
        var optCounters = (long[])TraceCounters.Values.Clone();

        TraceCounters.Reset();
        OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);
        FixParseWorkload.Run<ScalarSum, CountTrace>(fix.Payload, fix.Offsets, fix.Count);

        bool optMatches = bookOptSum == bookSum && fixOptSum == fixSum;
        for (int i = 0; i < TraceCounters.Size; i++)
            if (optCounters[i] != TraceCounters.Values[i]) optMatches = false;

        Console.WriteLine(optMatches
            ? "tuned(opt)-vs-baseline C#:  counters and checksums IDENTICAL"
            : "tuned(opt)-vs-baseline C#:  **DIVERGENCE** -- tuning changed the work done");

        // The second tuning attempt must preserve the work done just as strictly.
        TraceCounters.Reset();
        ulong bookOpt2Sum = OrderBookWorkload.RunOpt2<CountTrace>(book.Events, bid, ask);
        var opt2Counters = (long[])TraceCounters.Values.Clone();

        TraceCounters.Reset();
        OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);

        bool opt2Matches = bookOpt2Sum == bookSum;
        for (int i = 0; i <= TraceCounters.BookMix; i++)
            if (opt2Counters[i] != TraceCounters.Values[i]) opt2Matches = false;

        Console.WriteLine(opt2Matches
            ? "tuned(opt2)-vs-baseline C#: counters and checksums IDENTICAL"
            : "tuned(opt2)-vs-baseline C#: **DIVERGENCE** -- tuning changed the work done");

        // opt3 (zero-extended indices) must be a pure codegen change: counters and
        // checksum both strictly identical.
        TraceCounters.Reset();
        ulong bookOpt3Sum = OrderBookWorkload.RunOpt3<CountTrace>(book.Events, bid, ask);
        var opt3Counters = (long[])TraceCounters.Values.Clone();

        TraceCounters.Reset();
        OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);

        bool opt3Matches = bookOpt3Sum == bookSum;
        for (int i = 0; i <= TraceCounters.BookMix; i++)
            if (opt3Counters[i] != TraceCounters.Values[i]) opt3Matches = false;

        Console.WriteLine(opt3Matches
            ? "tuned(opt3)-vs-baseline C#: counters and checksums IDENTICAL"
            : "tuned(opt3)-vs-baseline C#: **DIVERGENCE** -- tuning changed the work done");

        // opt4 replaces the two top-of-book validity branches with sentinel-slot reads.
        // The CHECKSUM must be identical, but bestask_valid is EXPECTED to differ by
        // exactly +1: the baseline skips that read on the one event where
        // bestAsk == tickCount, whereas opt4 always performs it and reads a zero
        // sentinel. Any other divergence is a real bug.
        var vBidP1 = new long[book.TickCount + 1];
        var vAskP1 = new long[book.TickCount + 1];

        TraceCounters.Reset();
        ulong bookOpt4Sum = OrderBookWorkload.RunOpt4<CountTrace>(book.Events, vBidP1, vAskP1);
        var opt4Counters = (long[])TraceCounters.Values.Clone();

        TraceCounters.Reset();
        OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);

        bool opt4ChecksumOk = bookOpt4Sum == bookSum;
        var opt4Deltas = new List<string>();
        for (int i = 0; i <= TraceCounters.BookMix; i++)
        {
            long d = opt4Counters[i] - TraceCounters.Values[i];
            if (d != 0) opt4Deltas.Add($"slot{i}{(d > 0 ? "+" : "")}{d}");
        }

        bool opt4AsExpected = opt4ChecksumOk
            && opt4Deltas.Count == 1
            && opt4Deltas[0] == $"slot{TraceCounters.BookBestAskValid}+1";

        Console.WriteLine(opt4AsExpected
            ? "tuned(opt4)-vs-baseline C#: checksum IDENTICAL, counters differ by the one "
              + "EXPECTED slot (bestask_valid +1, sentinel read)"
            : $"tuned(opt4)-vs-baseline C#: **UNEXPECTED** checksumOk={opt4ChecksumOk} "
              + $"deltas=[{string.Join(",", opt4Deltas)}]");

        // The POH variants reach the data through raw pointers with no `fixed` and no
        // bounds checks. Nothing in the compiler will catch an off-by-one in that pointer
        // arithmetic, so the counters and the checksum are the only guard: if either
        // diverges, the timing comparison would be measuring a different algorithm.
        var pohEvents = GC.AllocateArray<BookEvent>(book.Events.Length, pinned: true);
        book.Events.CopyTo(pohEvents, 0);
        var pohBid = GC.AllocateArray<long>(book.TickCount, pinned: true);
        var pohAsk = GC.AllocateArray<long>(book.TickCount, pinned: true);
        var pohPayload = GC.AllocateArray<byte>(fix.Payload.Length, pinned: true);
        fix.Payload.CopyTo(pohPayload, 0);
        var pohOffsets = GC.AllocateArray<uint>(fix.Offsets.Length, pinned: true);
        fix.Offsets.CopyTo(pohOffsets, 0);

        TraceCounters.Reset();
        ulong bookPohSum = OrderBookWorkload.RunPoh<CountTrace>(pohEvents, pohBid, pohAsk);
        ulong fixPohSum = FixParseWorkload.RunPoh<ScalarSum, CountTrace>(
            pohPayload, pohOffsets, fix.Count);
        var pohCounters = (long[])TraceCounters.Values.Clone();

        TraceCounters.Reset();
        OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);
        FixParseWorkload.Run<ScalarSum, CountTrace>(fix.Payload, fix.Offsets, fix.Count);

        bool pohMatches = bookPohSum == bookSum && fixPohSum == fixSum;
        for (int i = 0; i < TraceCounters.Size; i++)
            if (pohCounters[i] != TraceCounters.Values[i]) pohMatches = false;

        Console.WriteLine(pohMatches
            ? "poh-vs-baseline C#:         counters and checksums IDENTICAL"
            : "poh-vs-baseline C#:         **DIVERGENCE** -- the POH variant does different work");

        // Restore the full counter vector for the cross-language report below.
        TraceCounters.Reset();
        OrderBookWorkload.Run<CountTrace>(book.Events, bid, ask);
        FixParseWorkload.Run<ScalarSum, CountTrace>(fix.Payload, fix.Offsets, fix.Count);

        var sb = new System.Text.StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"lang\": \"csharp\",\n");
        sb.Append($"  \"orderbookChecksum\": \"0x{bookSum:X16}\",\n");
        sb.Append($"  \"fixChecksum\": \"0x{fixSum:X16}\",\n");
        sb.Append("  \"counters\": {\n");
        for (int i = 0; i < TraceCounters.Names.Length; i++)
        {
            var (slotId, name) = TraceCounters.Names[i];
            string comma = i == TraceCounters.Names.Length - 1 ? "" : ",";
            sb.Append($"    \"{name}\": {TraceCounters.Values[slotId]}{comma}\n");
        }
        sb.Append("  }\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, sb.ToString());

        foreach (var (slotId, name) in TraceCounters.Names)
            Console.WriteLine($"{name,-28} {TraceCounters.Values[slotId],16:N0}");
        Console.WriteLine($"orderbook checksum 0x{bookSum:X16}");
        Console.WriteLine($"fix checksum       0x{fixSum:X16}");
        Console.WriteLine($"-> {outFile}");
        return 0;
    }

    /// <summary>
    /// Replays the generated fixtures and reports what the workload actually does.
    /// Exists because v1 of the book generator did not do what its comments claimed,
    /// and nothing caught it until the fixture was replayed.
    /// </summary>
    private static int CmdStats(Dictionary<string, string> o)
    {
        string dir = Get(o, "data", DefaultDataDir());
        var book = BookEventFile.Load(Path.Combine(dir, "book_events.bin"));
        Console.WriteLine("== order book workload ==");
        Console.WriteLine(DataGen.DescribeBookWorkload(book.Events, book.TickCount));

        var fix = FixMessageFile.Load(Path.Combine(dir, "fix_messages.bin"));
        Console.WriteLine();
        Console.WriteLine("== fix parse workload ==");
        Console.WriteLine($"messages            {fix.Count:N0}");
        Console.WriteLine($"payload bytes       {fix.Payload.Length:N0}");
        Console.WriteLine($"mean message bytes  {(double)fix.Payload.Length / fix.Count:F1}");
        return 0;
    }

    // ------------------------------------------------------------------ commands

    private static int CmdGen(Dictionary<string, string> o)
    {
        string dir = Get(o, "data", DefaultDataDir());
        int bookCount = int.Parse(Get(o, "book", "1000000"));
        int fixCount = int.Parse(Get(o, "fix", "200000"));
        ulong seed = ulong.Parse(Get(o, "seed", "20260805"));

        var (tickCount, events) = DataGen.GenerateBookEvents(bookCount, seed);
        BookEventFile.Save(Path.Combine(dir, "book_events.bin"), tickCount, events);
        Console.WriteLine($"book_events.bin  events={events.Length}  tickCount={tickCount}");

        var (offsets, payload) = DataGen.GenerateFixMessages(fixCount, seed ^ 0xA5A5A5A5UL);
        FixMessageFile.Save(Path.Combine(dir, "fix_messages.bin"), offsets, payload);
        Console.WriteLine($"fix_messages.bin messages={fixCount}  payloadBytes={payload.Length}  " +
                          $"avgMsgBytes={(double)payload.Length / fixCount:F1}");
        return 0;
    }

    private static int CmdProbe(Dictionary<string, string> o)
    {
        int cores = int.Parse(Get(o, "cores", Environment.ProcessorCount.ToString()));
        Console.WriteLine($"probing {cores} logical cores (lower ns/iter = faster core)");
        double[] ns = Harness.ProbeCores(cores);
        var ranked = ns.Select((v, i) => (Core: i, Ns: v)).OrderBy(t => t.Ns).ToArray();
        for (int i = 0; i < ranked.Length; i++)
            Console.WriteLine($"  rank {i,2}  core {ranked[i].Core,2}  {ranked[i].Ns,8:F4} ns/iter");
        Console.WriteLine($"fastest core = {ranked[0].Core}");
        return 0;
    }

    private static int CmdRun(Dictionary<string, string> o)
    {
        string dir = Get(o, "data", DefaultDataDir());
        int core = int.Parse(Get(o, "core", "2"));
        int warmup = int.Parse(Get(o, "warmup", "10"));
        int series = int.Parse(Get(o, "series", "25"));
        int reps = int.Parse(Get(o, "reps", "1"));
        string tag = Get(o, "tag", Harness.DescribeRuntime());
        string outFile = Get(o, "out", Path.Combine(DefaultResultsDir(), $"csharp-{tag}.json"));
        bool doProbe = o.ContainsKey("probe");

        Harness.PinToCore(core);

        var book = BookEventFile.Load(Path.Combine(dir, "book_events.bin"));
        var fix = FixMessageFile.Load(Path.Combine(dir, "fix_messages.bin"));

        // Pre-allocated, reused across every rep: the timed region allocates nothing.
        var bid = new long[book.TickCount];
        var ask = new long[book.TickCount];
        var events = book.Events;
        var payload = fix.Payload;
        var offsets = fix.Offsets;
        int msgCount = fix.Count;

        // POH copies, used ONLY by the *_poh rows. Deliberately not shared with any other
        // benchmark: moving the canonical rows onto different memory would invalidate every
        // number already committed under results/. Allocated below only when a _poh row is
        // actually selected, so an ordinary run pays nothing for them.
        BookEvent[]? pohEvents = null;
        long[]? pohBid = null;
        long[]? pohAsk = null;
        byte[]? pohPayload = null;
        uint[]? pohOffsets = null;

        // orderbook_l2_opt4 needs ladders one element longer (sentinel slot). Separate
        // arrays so the canonical rows keep running on exactly the buffers they always did.
        long[]? bidP1 = null;
        long[]? askP1 = null;

        double[]? probe = doProbe ? Harness.ProbeCores(Environment.ProcessorCount) : null;
        if (doProbe) Harness.PinToCore(core);

        // Canonical vs diagnostic rows are declared HERE, in the source, before any
        // data is seen. Only the canonical rows are compared against C++:
        //
        //   orderbook_l2_safe    <-> cpp orderbook_l2
        //   fix_parse_safe       <-> cpp fix_parse       (same-source pairing)
        //   fix_parse_safe_simd  <-> cpp fix_parse_simd  (equal-effort pairing)
        //
        // Everything else is a diagnostic and is reported in a separate, explicitly
        // non-comparative table. Picking "best C#" from 6 correlated rows against a
        // single C++ row would hand C# several percent of free min-selection advantage.
        var candidates = new (string Name, long Ops, Func<ulong> Body)[]
        {
            ("orderbook_l2_safe",   events.Length, () => OrderBookWorkload.Run(events, bid, ask)),
            ("orderbook_l2_unsafe", events.Length, () => OrderBookWorkload.RunUnsafe(events, bid, ask)),
            ("orderbook_l2_opt",    events.Length, () => OrderBookWorkload.RunOpt(events, bid, ask)),
            ("orderbook_l2_opt2",   events.Length, () => OrderBookWorkload.RunOpt2(events, bid, ask)),
            ("orderbook_l2_opt3",   events.Length, () => OrderBookWorkload.RunOpt3(events, bid, ask)),
            ("orderbook_l2_opt4",   events.Length, () => OrderBookWorkload.RunOpt4(events, bidP1!, askP1!)),
            ("fix_parse_safe",         msgCount, () => FixParseWorkload.Run(payload, offsets, msgCount)),
            ("fix_parse_safe_simd",    msgCount, () => FixParseWorkload.RunSimd(payload, offsets, msgCount)),
            ("fix_parse_opt",          msgCount, () => FixParseWorkload.RunOpt(payload, offsets, msgCount)),
            ("fix_parse_opt_simd",     msgCount, () => FixParseWorkload.RunOptSimd(payload, offsets, msgCount)),
            ("fix_parse_safe_simd_create", msgCount, () => FixParseWorkload.RunSimdCreate(payload, offsets, msgCount)),
            ("fix_parse_safe_simd_tuned", msgCount, () => FixParseWorkload.RunSimdTuned(payload, offsets, msgCount)),
            ("fix_parse_nosum",        msgCount, () => FixParseWorkload.RunNoSum(payload, offsets, msgCount)),
            ("fix_parse_unsafe",       msgCount, () => FixParseWorkload.RunUnsafe(payload, offsets, msgCount)),
            ("fix_parse_unsafe_ptrspan", msgCount, () => FixParseWorkload.RunUnsafePtrSpanScan(payload, offsets, msgCount)),
            // Raw pointers with NO `fixed`, on Pinned Object Heap buffers. Diagnostic.
            // Paired against *_unsafe (isolates the pin) and *_opt/_opt2 (isolates ptr vs ref).
            ("orderbook_l2_poh",   events.Length, () => OrderBookWorkload.RunPoh(pohEvents!, pohBid!, pohAsk!)),
            ("fix_parse_poh",          msgCount, () => FixParseWorkload.RunPoh(pohPayload!, pohOffsets!, msgCount)),
            ("fix_parse_poh_simd",     msgCount, () => FixParseWorkload.RunPohSimd(pohPayload!, pohOffsets!, msgCount)),
        };

        string only = Get(o, "only", "");
        var selected = candidates.Where(c => only.Length == 0 || c.Name == only).ToArray();
        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"[error] --only '{only}' matched no benchmark");
            return 4;
        }

        // Allocate the pinned buffers only if a _poh row is going to run. GC.AllocateArray
        // with pinned:true is the ONLY sanctioned source for these -- the workloads take a
        // raw pointer to the array data and hold it across the whole loop with no `fixed`,
        // which is safe exactly because POH objects never move.
        if (selected.Any(c => c.Name.Contains("_opt4", StringComparison.Ordinal)))
        {
            bidP1 = new long[book.TickCount + 1];
            askP1 = new long[book.TickCount + 1];
        }

        if (selected.Any(c => c.Name.Contains("_poh", StringComparison.Ordinal)))
        {
            pohEvents = GC.AllocateArray<BookEvent>(events.Length, pinned: true);
            events.CopyTo(pohEvents, 0);
            pohBid = GC.AllocateArray<long>(book.TickCount, pinned: true);
            pohAsk = GC.AllocateArray<long>(book.TickCount, pinned: true);
            pohPayload = GC.AllocateArray<byte>(payload.Length, pinned: true);
            payload.CopyTo(pohPayload, 0);
            pohOffsets = GC.AllocateArray<uint>(offsets.Length, pinned: true);
            offsets.CopyTo(pohOffsets, 0);
            Console.WriteLine("POH buffers allocated (pinned: true)");
        }

        // --nogc: suppress garbage collection outright for the measured region.
        // The workloads allocate nothing, so this is expected to change nothing -- but
        // "we measured zero allocations" and "GC costs nothing here" are different
        // claims, and a few runs did report gen0=1 from background JIT activity. This
        // makes the second claim measurable instead of inferred.
        bool noGc = o.ContainsKey("nogc");
        bool noGcActive = false;
        if (noGc)
        {
            try { noGcActive = GC.TryStartNoGCRegion(64L * 1024 * 1024); }
            catch (Exception ex) { Console.Error.WriteLine($"[warn] NoGCRegion failed: {ex.Message}"); }
            Console.WriteLine($"NoGCRegion requested: {(noGcActive ? "ACTIVE" : "FAILED")}");
        }

        var results = selected
            .Select(c => Harness.Measure(c.Name, c.Ops, warmup, series, reps, c.Body))
            .ToList();

        // Sampled HERE, while the region is still open. ToJson runs after EndNoGCRegion,
        // so reading GCSettings.LatencyMode there reported "Batch" for every --nogc run --
        // the artifact could not tell a working NoGC region from one that silently failed
        // to start, which is the same class of self-refuting-provenance bug as the
        // AppContext one below.
        string latencyModeDuringRun = System.Runtime.GCSettings.LatencyMode.ToString();

        if (noGcActive)
        {
            try
            {
                if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
                    GC.EndNoGCRegion();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[warn] EndNoGCRegion: {ex.Message}"); }
        }

        // Provenance must reflect what the process was ACTUALLY configured with.
        // AppContext reads the baked runtimeconfig, which said TieredCompilation=true
        // even when DOTNET_TieredCompilation=0 was set in the environment -- so the
        // jit-tc0 result files previously asserted the opposite of their own config.
        string EnvOr(string name, string dflt) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : dflt;

        // DOTNET_TC_QuickJitForLoops and the NoGC state were both absent from this string
        // until 2026-08-06, which meant jit-default, jit-qjfl and jit-qjfl-nogc emitted
        // byte-identical provenance. Two of this repo's headline findings rest on exactly
        // those two knobs, so their artifacts carried no evidence the knob was applied.
        string flags =
            $"DOTNET_TieredCompilation={EnvOr("DOTNET_TieredCompilation", "(unset->default on)")}; " +
            $"DOTNET_TC_QuickJitForLoops={EnvOr("DOTNET_TC_QuickJitForLoops", "(unset->default on)")}; " +
            $"DOTNET_TieredPGO={EnvOr("DOTNET_TieredPGO", "(unset->default on)")}; " +
            $"DOTNET_ReadyToRun={EnvOr("DOTNET_ReadyToRun", "(unset->default on)")}; " +
            $"noGcRequested={noGc}; noGcActive={noGcActive}; " +
            $"latencyModeDuringRun={latencyModeDuringRun}; " +
            $"codegen={Harness.DescribeRuntime()}";
        string json = Harness.ToJson(tag, flags, core, results, probe);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, json);

        foreach (var r in results)
        {
            var ns = r.NsPerOp.OrderBy(x => x).ToArray();
            Console.WriteLine($"{r.Name,-24} median={ns[ns.Length / 2],9:F3} ns/op  " +
                              $"min={ns[0],9:F3}  checksum=0x{r.ChecksumValue:X16}  " +
                              $"alloc={r.AllocatedBytes}B  gen0={r.Gen0Collections}");
        }
        Console.WriteLine($"-> {outFile}");
        Console.WriteLine($"sink={Harness.Sink:X16}");
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, string> ParseArgs(ReadOnlySpan<string> args)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            string key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                d[key] = args[i + 1];
                i++;
            }
            else d[key] = "true";
        }
        return d;
    }

    private static string Get(Dictionary<string, string> o, string k, string dflt) =>
        o.TryGetValue(k, out var v) ? v : dflt;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HARNESS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string DefaultDataDir() => Path.Combine(RepoRoot(), "data");
    private static string DefaultResultsDir() => Path.Combine(RepoRoot(), "results");
}
