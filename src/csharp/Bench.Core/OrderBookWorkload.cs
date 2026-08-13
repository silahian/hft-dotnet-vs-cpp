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
    /// 4. <c>nint</c> index arithmetic, avoiding a sign-extend (movsxd) on every access.
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
