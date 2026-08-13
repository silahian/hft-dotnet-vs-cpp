using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bench.Core;

/// <summary>
/// Direct-indexed L2 price ladder — the layout an actual low-latency book uses.
/// Two contiguous quantity arrays indexed by tick, best-bid / best-ask cached,
/// rescan-outward on deletion of the touch.
///
/// Generic over <see cref="ITrace"/> so the timed path and the operation-counting
/// path are the same source. With <see cref="NullTrace"/> every trace call is an
/// empty static method on an empty struct and disappears at JIT time.
///
/// <c>order_book.hpp</c> is the line-for-line C++ counterpart, including the trace
/// calls, so the two can be diffed by eye and verified by counter equality.
/// </summary>
public static class OrderBookWorkload
{
    public static ulong Run(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        => Run<NullTrace>(events, bid, ask);

    /// <summary>Safe C#. Spans hoisted so the JIT sees loop-invariant lengths.</summary>
    public static ulong Run<TTrace>(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        where TTrace : struct, ITrace
    {
        bid.Clear();
        ask.Clear();

        int tickCount = bid.Length;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        for (int i = 0; i < events.Length; i++)
        {
            TTrace.Count(TraceCounters.BookEvents);

            BookEvent e = events[i];
            int idx = e.TickIndex;
            long qty = e.Qty;

            if (qty != 0) TTrace.Count(TraceCounters.BookQtyNonZero);
            else TTrace.Count(TraceCounters.BookQtyZero);

            if (e.Side == 0)
            {
                TTrace.Count(TraceCounters.BookSideBid);
                bid[idx] = qty;
                if (qty != 0)
                {
                    if (idx > bestBid) { TTrace.Count(TraceCounters.BookBidImprove); bestBid = idx; }
                }
                else if (idx == bestBid)
                {
                    TTrace.Count(TraceCounters.BookBidRescan);
                    int j = idx - 1;
                    while (j >= 0 && bid[j] == 0) { TTrace.Count(TraceCounters.BookBidRescanIter); j--; }
                    bestBid = j;
                }
            }
            else
            {
                TTrace.Count(TraceCounters.BookSideAsk);
                ask[idx] = qty;
                if (qty != 0)
                {
                    if (idx < bestAsk) { TTrace.Count(TraceCounters.BookAskImprove); bestAsk = idx; }
                }
                else if (idx == bestAsk)
                {
                    TTrace.Count(TraceCounters.BookAskRescan);
                    int j = idx + 1;
                    while (j < tickCount && ask[j] == 0) { TTrace.Count(TraceCounters.BookAskRescanIter); j++; }
                    bestAsk = j;
                }
            }

            long bq;
            if (bestBid >= 0) { TTrace.Count(TraceCounters.BookBestBidValid); bq = bid[bestBid]; }
            else bq = 0;

            long aq;
            if (bestAsk < tickCount) { TTrace.Count(TraceCounters.BookBestAskValid); aq = ask[bestAsk]; }
            else aq = 0;

            TTrace.Add(TraceCounters.BookMix, 4);
            h = Checksum.Mix(h, (ulong)(long)bestBid);
            h = Checksum.Mix(h, (ulong)bq);
            h = Checksum.Mix(h, (ulong)(long)bestAsk);
            h = Checksum.Mix(h, (ulong)aq);
        }

        return h;
    }

    /// <summary>
    /// TUNED SAFE C#. Same algorithm, same operations (verified by the trace counters),
    /// same output. Only the codegen differs. Four changes, each with a specific reason:
    ///
    /// 1. <c>AggressiveOptimization</c> — compiles straight to the optimizing JIT and
    ///    skips tiering entirely. This is the in-code fix for the tier-0 contamination
    ///    that made the earlier SIMD numbers meaningless, and unlike
    ///    DOTNET_TieredCompilation=0 it is scoped to the method rather than the process.
    /// 2. <c>ref</c> bases hoisted out of the loop via <c>MemoryMarshal.GetReference</c>,
    ///    with <c>Unsafe.Add</c> for element access — removes bounds checks WITHOUT
    ///    <c>fixed</c>. That matters: the earlier `RunUnsafe` variant pinned the arrays,
    ///    and pinning is what made it SLOWER than the safe version. A managed ref does
    ///    not pin and does not inhibit the same optimizations.
    /// 3. <c>ref readonly</c> for the 16-byte event struct, so it is read in place
    ///    rather than copied per iteration.
    /// 4. <c>nint</c> index arithmetic.
    ///
    /// CORRECTION: point 4 used to read "avoiding a sign-extend (movsxd) on every
    /// access". That is false, and the emitted assembly says so — <c>(nint)someInt</c>
    /// IS a sign-extend, and RyuJIT emits 7 <c>movsxd</c> in this method, including one
    /// on every rescan iteration:
    ///     mov    r11d, dword ptr [r8]
    ///     movsxd rsi, r11d
    ///     mov    qword ptr [r9+8*rsi], rbx
    /// MSVC, by contrast, folds the widening into the load and reuses the 64-bit
    /// register. Widening by ZERO-extension is what actually removes the instruction;
    /// see <see cref="RunOpt3"/>, which drops it from 7 to 2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong RunOpt(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        => RunOpt<NullTrace>(events, bid, ask);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong RunOpt<TTrace>(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        where TTrace : struct, ITrace
    {
        bid.Clear();
        ask.Clear();

        int tickCount = bid.Length;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        ref BookEvent ev0 = ref MemoryMarshal.GetReference(events);
        ref long bid0 = ref MemoryMarshal.GetReference(bid);
        ref long ask0 = ref MemoryMarshal.GetReference(ask);
        int n = events.Length;

        for (int i = 0; i < n; i++)
        {
            TTrace.Count(TraceCounters.BookEvents);

            ref readonly BookEvent e = ref Unsafe.Add(ref ev0, (nint)i);
            int idx = e.TickIndex;
            long qty = e.Qty;

            if (qty != 0) TTrace.Count(TraceCounters.BookQtyNonZero);
            else TTrace.Count(TraceCounters.BookQtyZero);

            if (e.Side == 0)
            {
                TTrace.Count(TraceCounters.BookSideBid);
                Unsafe.Add(ref bid0, (nint)idx) = qty;
                if (qty != 0)
                {
                    if (idx > bestBid) { TTrace.Count(TraceCounters.BookBidImprove); bestBid = idx; }
                }
                else if (idx == bestBid)
                {
                    TTrace.Count(TraceCounters.BookBidRescan);
                    int j = idx - 1;
                    while (j >= 0 && Unsafe.Add(ref bid0, (nint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookBidRescanIter);
                        j--;
                    }
                    bestBid = j;
                }
            }
            else
            {
                TTrace.Count(TraceCounters.BookSideAsk);
                Unsafe.Add(ref ask0, (nint)idx) = qty;
                if (qty != 0)
                {
                    if (idx < bestAsk) { TTrace.Count(TraceCounters.BookAskImprove); bestAsk = idx; }
                }
                else if (idx == bestAsk)
                {
                    TTrace.Count(TraceCounters.BookAskRescan);
                    int j = idx + 1;
                    while (j < tickCount && Unsafe.Add(ref ask0, (nint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookAskRescanIter);
                        j++;
                    }
                    bestAsk = j;
                }
            }

            long bq;
            if (bestBid >= 0)
            {
                TTrace.Count(TraceCounters.BookBestBidValid);
                bq = Unsafe.Add(ref bid0, (nint)bestBid);
            }
            else bq = 0;

            long aq;
            if (bestAsk < tickCount)
            {
                TTrace.Count(TraceCounters.BookBestAskValid);
                aq = Unsafe.Add(ref ask0, (nint)bestAsk);
            }
            else aq = 0;

            TTrace.Add(TraceCounters.BookMix, 4);
            h = Checksum.Mix(h, (ulong)(long)bestBid);
            h = Checksum.Mix(h, (ulong)bq);
            h = Checksum.Mix(h, (ulong)(long)bestAsk);
            h = Checksum.Mix(h, (ulong)aq);
        }

        return h;
    }

    /// <summary>
    /// SECOND TUNING ATTEMPT. Differences from <see cref="RunOpt"/>:
    ///
    ///  * NO <c>AggressiveOptimization</c> — that attribute forfeits dynamic PGO, which
    ///    measured worth more than everything it bought on this workload. The tier-0
    ///    problem it was working around is better solved by
    ///    <c>TieredCompilationQuickJitForLoops=false</c>, which keeps PGO.
    ///  * Moving <c>ref</c> cursor over the event array instead of indexed access, so
    ///    the 16-byte stride becomes a pointer bump rather than an index scale.
    ///  * <c>[module: SkipLocalsInit]</c> applies to this assembly.
    ///
    /// Operation counts are unchanged (verified), so this is a pure codegen experiment.
    /// </summary>
    public static ulong RunOpt2(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        => RunOpt2<NullTrace>(events, bid, ask);

    public static ulong RunOpt2<TTrace>(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        where TTrace : struct, ITrace
    {
        bid.Clear();
        ask.Clear();

        int tickCount = bid.Length;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        ref BookEvent ev = ref MemoryMarshal.GetReference(events);
        ref BookEvent evEnd = ref Unsafe.Add(ref ev, events.Length);
        ref long bid0 = ref MemoryMarshal.GetReference(bid);
        ref long ask0 = ref MemoryMarshal.GetReference(ask);

        while (Unsafe.IsAddressLessThan(ref ev, ref evEnd))
        {
            TTrace.Count(TraceCounters.BookEvents);

            int idx = ev.TickIndex;
            long qty = ev.Qty;

            if (qty != 0) TTrace.Count(TraceCounters.BookQtyNonZero);
            else TTrace.Count(TraceCounters.BookQtyZero);

            if (ev.Side == 0)
            {
                TTrace.Count(TraceCounters.BookSideBid);
                Unsafe.Add(ref bid0, (nint)idx) = qty;
                if (qty != 0)
                {
                    if (idx > bestBid) { TTrace.Count(TraceCounters.BookBidImprove); bestBid = idx; }
                }
                else if (idx == bestBid)
                {
                    TTrace.Count(TraceCounters.BookBidRescan);
                    int j = idx - 1;
                    while (j >= 0 && Unsafe.Add(ref bid0, (nint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookBidRescanIter);
                        j--;
                    }
                    bestBid = j;
                }
            }
            else
            {
                TTrace.Count(TraceCounters.BookSideAsk);
                Unsafe.Add(ref ask0, (nint)idx) = qty;
                if (qty != 0)
                {
                    if (idx < bestAsk) { TTrace.Count(TraceCounters.BookAskImprove); bestAsk = idx; }
                }
                else if (idx == bestAsk)
                {
                    TTrace.Count(TraceCounters.BookAskRescan);
                    int j = idx + 1;
                    while (j < tickCount && Unsafe.Add(ref ask0, (nint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookAskRescanIter);
                        j++;
                    }
                    bestAsk = j;
                }
            }

            long bq;
            if (bestBid >= 0)
            {
                TTrace.Count(TraceCounters.BookBestBidValid);
                bq = Unsafe.Add(ref bid0, (nint)bestBid);
            }
            else bq = 0;

            long aq;
            if (bestAsk < tickCount)
            {
                TTrace.Count(TraceCounters.BookBestAskValid);
                aq = Unsafe.Add(ref ask0, (nint)bestAsk);
            }
            else aq = 0;

            TTrace.Add(TraceCounters.BookMix, 4);
            h = Checksum.Mix(h, (ulong)(long)bestBid);
            h = Checksum.Mix(h, (ulong)bq);
            h = Checksum.Mix(h, (ulong)(long)bestAsk);
            h = Checksum.Mix(h, (ulong)aq);

            ev = ref Unsafe.Add(ref ev, 1);
        }

        return h;
    }

    /// <summary>
    /// FOURTH TUNING ATTEMPT — removes the two top-of-book validity BRANCHES entirely.
    ///
    /// The counters say <c>bestbid_valid</c> fires 1,000,000/1,000,000 and
    /// <c>bestask_valid</c> 999,999/1,000,000, so both branches are ~perfectly predicted
    /// — but RyuJIT still spends a test + jcc + xor + jmp per event on each:
    ///     test eax, eax / jge L / xor r11d, r11d / jmp L2
    ///
    /// Sentinel slots delete them. Both ladders get ONE extra element, and the bid base
    /// ref is biased forward by one, so:
    ///
    ///   * bid index range becomes [-1, tickCount-1]; index -1 lands on a slot that is
    ///     never written and always zero, which is exactly what the old else-branch
    ///     produced. So <c>bq = bid[bestBid]</c> needs no guard.
    ///   * ask index range becomes [0, tickCount]; <c>bestAsk</c> is already provably
    ///     &lt;= tickCount, and slot tickCount is never written and always zero. So
    ///     <c>aq = ask[bestAsk]</c> needs no guard either.
    ///
    /// Output is bit-identical: both sentinel reads return 0 in precisely the cases the
    /// branches used to return 0. The bid index stays SIGNED (it can be -1), so that one
    /// access keeps a movsxd — one ALU op traded for a branch, a jump and an xor.
    ///
    /// Requires ladders of length tickCount+1; <c>tickCount</c> is derived as
    /// <c>bid.Length - 1</c> rather than <c>bid.Length</c>.
    /// </summary>
    public static ulong RunOpt4(ReadOnlySpan<BookEvent> events, Span<long> bidPlus1, Span<long> askPlus1)
        => RunOpt4<NullTrace>(events, bidPlus1, askPlus1);

    public static ulong RunOpt4<TTrace>(
        ReadOnlySpan<BookEvent> events, Span<long> bidPlus1, Span<long> askPlus1)
        where TTrace : struct, ITrace
    {
        bidPlus1.Clear();
        askPlus1.Clear();

        int tickCount = bidPlus1.Length - 1;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        ref BookEvent ev = ref MemoryMarshal.GetReference(events);
        ref BookEvent evEnd = ref Unsafe.Add(ref ev, events.Length);

        // bid base biased forward one element, so logical index -1 == physical slot 0.
        ref long bid0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(bidPlus1), 1);
        ref long ask0 = ref MemoryMarshal.GetReference(askPlus1);

        while (Unsafe.IsAddressLessThan(ref ev, ref evEnd))
        {
            TTrace.Count(TraceCounters.BookEvents);

            int idx = ev.TickIndex;
            long qty = ev.Qty;

            if (qty != 0) TTrace.Count(TraceCounters.BookQtyNonZero);
            else TTrace.Count(TraceCounters.BookQtyZero);

            if (ev.Side == 0)
            {
                TTrace.Count(TraceCounters.BookSideBid);
                Unsafe.Add(ref bid0, (nuint)(uint)idx) = qty;
                if (qty != 0)
                {
                    if (idx > bestBid) { TTrace.Count(TraceCounters.BookBidImprove); bestBid = idx; }
                }
                else if (idx == bestBid)
                {
                    TTrace.Count(TraceCounters.BookBidRescan);
                    int j = idx - 1;
                    while (j >= 0 && Unsafe.Add(ref bid0, (nuint)(uint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookBidRescanIter);
                        j--;
                    }
                    bestBid = j;
                }
            }
            else
            {
                TTrace.Count(TraceCounters.BookSideAsk);
                Unsafe.Add(ref ask0, (nuint)(uint)idx) = qty;
                if (qty != 0)
                {
                    if (idx < bestAsk) { TTrace.Count(TraceCounters.BookAskImprove); bestAsk = idx; }
                }
                else if (idx == bestAsk)
                {
                    TTrace.Count(TraceCounters.BookAskRescan);
                    int j = idx + 1;
                    while (j < tickCount && Unsafe.Add(ref ask0, (nuint)(uint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookAskRescanIter);
                        j++;
                    }
                    bestAsk = j;
                }
            }

            // No guards: the sentinel slots make both reads unconditional.
            TTrace.Count(TraceCounters.BookBestBidValid);
            long bq = Unsafe.Add(ref bid0, (nint)bestBid);

            TTrace.Count(TraceCounters.BookBestAskValid);
            long aq = Unsafe.Add(ref ask0, (nuint)(uint)bestAsk);

            TTrace.Add(TraceCounters.BookMix, 4);
            h = Checksum.Mix(h, (ulong)(long)bestBid);
            h = Checksum.Mix(h, (ulong)bq);
            h = Checksum.Mix(h, (ulong)(long)bestAsk);
            h = Checksum.Mix(h, (ulong)aq);

            ev = ref Unsafe.Add(ref ev, 1);
        }

        return h;
    }

    /// <summary>
    /// THIRD TUNING ATTEMPT — targets one concrete codegen difference found by diffing
    /// RyuJIT's output for <see cref="RunOpt2"/> against MSVC 14.51's output for the
    /// line-for-line identical <c>run_order_book</c>.
    ///
    /// MSVC folds the int-&gt;64-bit widening into the load itself and then uses the
    /// 64-bit register directly as a scaled index:
    ///     movsxd r8, DWORD PTR [r15+rax*8]     ; widen once, in the load
    ///     mov    QWORD PTR [rsi+r8*8], r9
    ///     cmp    QWORD PTR [rsi+rdx*8], 0      ; rescan: 64-bit reg, no re-widen
    ///
    /// RyuJIT emits a SEPARATE sign-extend per access, including one on every rescan
    /// iteration:
    ///     mov    r11d, dword ptr [r8]
    ///     movsxd rsi, r11d                     ; extra instruction
    ///     mov    qword ptr [r9+8*rsi], rbx
    ///
    /// RunOpt2's comment claims `nint` arithmetic "avoids a sign-extend (movsxd) on every
    /// access". It does not: <c>(nint)someInt</c> IS a sign-extend. The fix is to widen by
    /// ZERO-extension instead — every index here is provably non-negative (tick indices
    /// are in [0, tickCount), and both rescan reads are short-circuit guarded by
    /// <c>j &gt;= 0</c> / <c>j &lt; tickCount</c>, while both touch reads are guarded by
    /// <c>bestBid &gt;= 0</c> / <c>bestAsk &lt; tickCount</c>). On x64 a 32-bit register
    /// write already zeroes the upper half, so <c>(nuint)(uint)i</c> should need no
    /// instruction at all.
    ///
    /// Operation counts are unchanged, so this is a pure codegen experiment.
    /// </summary>
    public static ulong RunOpt3(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        => RunOpt3<NullTrace>(events, bid, ask);

    public static ulong RunOpt3<TTrace>(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
        where TTrace : struct, ITrace
    {
        bid.Clear();
        ask.Clear();

        int tickCount = bid.Length;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        ref BookEvent ev = ref MemoryMarshal.GetReference(events);
        ref BookEvent evEnd = ref Unsafe.Add(ref ev, events.Length);
        ref long bid0 = ref MemoryMarshal.GetReference(bid);
        ref long ask0 = ref MemoryMarshal.GetReference(ask);

        while (Unsafe.IsAddressLessThan(ref ev, ref evEnd))
        {
            TTrace.Count(TraceCounters.BookEvents);

            int idx = ev.TickIndex;
            long qty = ev.Qty;

            if (qty != 0) TTrace.Count(TraceCounters.BookQtyNonZero);
            else TTrace.Count(TraceCounters.BookQtyZero);

            if (ev.Side == 0)
            {
                TTrace.Count(TraceCounters.BookSideBid);
                Unsafe.Add(ref bid0, (nuint)(uint)idx) = qty;
                if (qty != 0)
                {
                    if (idx > bestBid) { TTrace.Count(TraceCounters.BookBidImprove); bestBid = idx; }
                }
                else if (idx == bestBid)
                {
                    TTrace.Count(TraceCounters.BookBidRescan);
                    int j = idx - 1;
                    while (j >= 0 && Unsafe.Add(ref bid0, (nuint)(uint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookBidRescanIter);
                        j--;
                    }
                    bestBid = j;
                }
            }
            else
            {
                TTrace.Count(TraceCounters.BookSideAsk);
                Unsafe.Add(ref ask0, (nuint)(uint)idx) = qty;
                if (qty != 0)
                {
                    if (idx < bestAsk) { TTrace.Count(TraceCounters.BookAskImprove); bestAsk = idx; }
                }
                else if (idx == bestAsk)
                {
                    TTrace.Count(TraceCounters.BookAskRescan);
                    int j = idx + 1;
                    while (j < tickCount && Unsafe.Add(ref ask0, (nuint)(uint)j) == 0)
                    {
                        TTrace.Count(TraceCounters.BookAskRescanIter);
                        j++;
                    }
                    bestAsk = j;
                }
            }

            long bq;
            if (bestBid >= 0)
            {
                TTrace.Count(TraceCounters.BookBestBidValid);
                bq = Unsafe.Add(ref bid0, (nuint)(uint)bestBid);
            }
            else bq = 0;

            long aq;
            if (bestAsk < tickCount)
            {
                TTrace.Count(TraceCounters.BookBestAskValid);
                aq = Unsafe.Add(ref ask0, (nuint)(uint)bestAsk);
            }
            else aq = 0;

            TTrace.Add(TraceCounters.BookMix, 4);
            h = Checksum.Mix(h, (ulong)(long)bestBid);
            h = Checksum.Mix(h, (ulong)bq);
            h = Checksum.Mix(h, (ulong)(long)bestAsk);
            h = Checksum.Mix(h, (ulong)aq);

            ev = ref Unsafe.Add(ref ev, 1);
        }

        return h;
    }

    /// <summary>
    /// PINNED OBJECT HEAP variant. Raw pointers, identical to <see cref="RunUnsafe"/>,
    /// but with NO <c>fixed</c> statement: the arrays are allocated via
    /// <c>GC.AllocateArray&lt;T&gt;(n, pinned: true)</c>, so the objects never move and
    /// the pointers stay valid for their whole lifetime. There is nothing left for
    /// <c>fixed</c> to do.
    ///
    /// Exists to DECOMPOSE the repo's least-reproducible finding ("`unsafe` + `fixed`
    /// never made C# faster, sometimes slower"). Two independent contrasts:
    ///
    ///   RunUnsafe (fixed + ptr) vs RunPoh (no fixed + ptr)  -> isolates THE PIN
    ///   RunPoh    (raw ptr)     vs RunOpt2 (managed ref)    -> isolates PTR vs REF
    ///
    /// If RunPoh lands at RunOpt2's speed, per-call pinning was the cost and the
    /// hypothesis in README is confirmed. If it lands at RunUnsafe's speed, pinning is
    /// not the mechanism and that claim needs rewriting.
    ///
    /// SAFETY: <paramref name="events"/>, <paramref name="bid"/> and <paramref name="ask"/>
    /// MUST be POH allocations. A regular heap array compiles fine here and usually
    /// appears to work, then corrupts the heap under GC pressure — the failure is not a
    /// clean crash. Allocation is centralized in Bench.Runner for exactly this reason.
    /// </summary>
    public static unsafe ulong RunPoh(BookEvent[] events, long[] bid, long[] ask)
        => RunPoh<NullTrace>(events, bid, ask);

    public static unsafe ulong RunPoh<TTrace>(BookEvent[] events, long[] bid, long[] ask)
        where TTrace : struct, ITrace
    {
        Array.Clear(bid);
        Array.Clear(ask);

        int tickCount = bid.Length;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        // Pattern 1A: ref -> pointer inline, no null check, no pin metadata. Valid only
        // because the arrays are on the POH (see SAFETY above).
        BookEvent* evBase = (BookEvent*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(events));
        long* bidBase = (long*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(bid));
        long* askBase = (long*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(ask));

        int n = events.Length;
        for (int i = 0; i < n; i++)
        {
            TTrace.Count(TraceCounters.BookEvents);

            BookEvent e = evBase[i];
            int idx = e.TickIndex;
            long qty = e.Qty;

            if (qty != 0) TTrace.Count(TraceCounters.BookQtyNonZero);
            else TTrace.Count(TraceCounters.BookQtyZero);

            if (e.Side == 0)
            {
                TTrace.Count(TraceCounters.BookSideBid);
                bidBase[idx] = qty;
                if (qty != 0)
                {
                    if (idx > bestBid) { TTrace.Count(TraceCounters.BookBidImprove); bestBid = idx; }
                }
                else if (idx == bestBid)
                {
                    TTrace.Count(TraceCounters.BookBidRescan);
                    int j = idx - 1;
                    while (j >= 0 && bidBase[j] == 0)
                    {
                        TTrace.Count(TraceCounters.BookBidRescanIter);
                        j--;
                    }
                    bestBid = j;
                }
            }
            else
            {
                TTrace.Count(TraceCounters.BookSideAsk);
                askBase[idx] = qty;
                if (qty != 0)
                {
                    if (idx < bestAsk) { TTrace.Count(TraceCounters.BookAskImprove); bestAsk = idx; }
                }
                else if (idx == bestAsk)
                {
                    TTrace.Count(TraceCounters.BookAskRescan);
                    int j = idx + 1;
                    while (j < tickCount && askBase[j] == 0)
                    {
                        TTrace.Count(TraceCounters.BookAskRescanIter);
                        j++;
                    }
                    bestAsk = j;
                }
            }

            long bq;
            if (bestBid >= 0) { TTrace.Count(TraceCounters.BookBestBidValid); bq = bidBase[bestBid]; }
            else bq = 0;

            long aq;
            if (bestAsk < tickCount) { TTrace.Count(TraceCounters.BookBestAskValid); aq = askBase[bestAsk]; }
            else aq = 0;

            TTrace.Add(TraceCounters.BookMix, 4);
            h = Checksum.Mix(h, (ulong)(long)bestBid);
            h = Checksum.Mix(h, (ulong)bq);
            h = Checksum.Mix(h, (ulong)(long)bestAsk);
            h = Checksum.Mix(h, (ulong)aq);
        }

        return h;
    }

    /// <summary>
    /// Identical algorithm with bounds checks removed. Diagnostic only — this is
    /// the number that isolates "how much does array bounds checking actually cost
    /// on this workload", which is the question everyone asks and nobody measures.
    /// </summary>
    public static unsafe ulong RunUnsafe(ReadOnlySpan<BookEvent> events, Span<long> bid, Span<long> ask)
    {
        bid.Clear();
        ask.Clear();

        int tickCount = bid.Length;
        int bestBid = -1;
        int bestAsk = tickCount;
        ulong h = Checksum.Offset;

        fixed (BookEvent* evBase = events)
        fixed (long* bidBase = bid)
        fixed (long* askBase = ask)
        {
            int n = events.Length;
            for (int i = 0; i < n; i++)
            {
                BookEvent e = evBase[i];
                int idx = e.TickIndex;
                long qty = e.Qty;

                if (e.Side == 0)
                {
                    bidBase[idx] = qty;
                    if (qty != 0)
                    {
                        if (idx > bestBid) bestBid = idx;
                    }
                    else if (idx == bestBid)
                    {
                        int j = idx - 1;
                        while (j >= 0 && bidBase[j] == 0) j--;
                        bestBid = j;
                    }
                }
                else
                {
                    askBase[idx] = qty;
                    if (qty != 0)
                    {
                        if (idx < bestAsk) bestAsk = idx;
                    }
                    else if (idx == bestAsk)
                    {
                        int j = idx + 1;
                        while (j < tickCount && askBase[j] == 0) j++;
                        bestAsk = j;
                    }
                }

                long bq = bestBid >= 0 ? bidBase[bestBid] : 0;
                long aq = bestAsk < tickCount ? askBase[bestAsk] : 0;

                h = Checksum.Mix(h, (ulong)(long)bestBid);
                h = Checksum.Mix(h, (ulong)bq);
                h = Checksum.Mix(h, (ulong)(long)bestAsk);
                h = Checksum.Mix(h, (ulong)aq);
            }
        }

        return h;
    }
}
