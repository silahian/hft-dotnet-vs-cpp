using System.Text;

namespace Bench.Core;

/// <summary>
/// Produces the binary fixtures both runtimes consume. Runs once, offline, untimed.
/// Deterministic: same seed produces byte-identical files.
/// </summary>
public static class DataGen
{
    public const char Soh = '\x01';

    private static readonly string[] Symbols =
        ["ESH6", "NQH6", "CLG6", "GCG6", "6EH6", "ZNH6", "BTCUSD", "ETHUSD"];

    // ---------------------------------------------------------------- book events

    /// <summary>
    /// Generates an L2 ladder event stream.
    ///
    /// VERSION 2. Version 1 was measured against its own shipped fixture and did not
    /// do what its comments claimed: the mid walked +/-1 only once every 64 events, so
    /// over 1M events it covered ~250 of 4096 ticks, the whole working set stayed
    /// L1-resident, and the best-level rescan fired on 1 event in 382 (0.0093 inner
    /// iterations per event). The branch the workload was built around was effectively
    /// dead, which made the benchmark a branch-mispredict-latency test that no language
    /// can differentiate on.
    ///
    /// V2 changes, all aimed at making the workload actually exercise the mechanism:
    ///   - the mid random-walks on EVERY event, so over 1M events it diffuses across
    ///     most of the ladder (sigma ~ sqrt(N) ticks) instead of sitting in one spot
    ///   - deletes are biased toward the touch, so the rescan path is live
    ///   - the ladder is sized so the touched footprint exceeds L1
    ///
    /// The realized statistics are not asserted: <c>bench-csharp stats</c> replays the
    /// generated file and reports rescan rate, distinct ticks touched, and footprint,
    /// and those numbers are published alongside the timings.
    /// </summary>
    public static (int TickCount, BookEvent[] Events) GenerateBookEvents(int count, ulong seed)
    {
        const int tickCount = 8192;
        const int window = 48;   // active levels maintained per side

        var rng = new Xorshift64(seed);
        var events = new BookEvent[count];

        // The generator runs the SAME state machine the benchmark runs, so it always
        // knows where the touch actually is. v1 and v2 both failed because they guessed
        // the touch from a notional "mid" that had drifted away from the real best.
        var bidQty = new long[tickCount];
        var askQty = new long[tickCount];
        int bestBid = -1;
        int bestAsk = tickCount;
        int n = 0;

        long RandQty() => (long)(rng.NextBounded(1000) + 1) * 100;

        void Emit(int side, int tick, long qty)
        {
            if (n >= count || tick < 1 || tick >= tickCount - 1) return;
            events[n++] = new BookEvent(tick, side, qty);

            // Mirror the consumer's update exactly, rescan included.
            if (side == 0)
            {
                bidQty[tick] = qty;
                if (qty != 0) { if (tick > bestBid) bestBid = tick; }
                else if (tick == bestBid)
                {
                    int j = tick - 1;
                    while (j >= 0 && bidQty[j] == 0) j--;
                    bestBid = j;
                }
            }
            else
            {
                askQty[tick] = qty;
                if (qty != 0) { if (tick < bestAsk) bestAsk = tick; }
                else if (tick == bestAsk)
                {
                    int j = tick + 1;
                    while (j < tickCount && askQty[j] == 0) j++;
                    bestAsk = j;
                }
            }
        }

        // Seed a two-sided book around the middle of the ladder.
        int seedMid = tickCount / 2;
        for (int d = 0; d < window && n < count; d++)
        {
            Emit(0, seedMid - 1 - d, RandQty());
            Emit(1, seedMid + d, RandQty());
        }

        // v4. v3 balanced consume/improve by PROBABILITY and still drifted 8.7 sigma to
        // the ladder edges, because the two actions have different STEP SIZES: improve
        // moves the touch exactly 1 tick, while consume moves it 1 tick plus the run of
        // holes punched by the cancel branch (measured mean ~1.46). Equal probabilities
        // with unequal steps is a biased walk. Compounding it, replenishing at
        // `newBest +/- window` ratchets the far edge outward on every consume, so the
        // book spread grew without bound and the last half of the run touched only
        // ~25 cache lines -- the exact L1-resident failure v1 had.
        //
        // v4 fixes both: replenishment is anchored to the CURRENT MID rather than to the
        // moving best, and a restoring bias plus a hard spread cap keep the touch near
        // the centre of the ladder. The walk is now mean-reverting rather than free.
        const int targetMid = tickCount / 2;
        const int maxSpread = 3 * window;
        const int edgeGuard = window + 16;

        while (n < count)
        {
            int mid = (bestBid + bestAsk) / 2;
            int spread = bestAsk - bestBid;

            // Hard spread cap: if the book has pulled apart, tighten the wider side.
            if (spread > maxSpread)
            {
                int wideSide = (mid - bestBid) >= (bestAsk - mid) ? 0 : 1;
                Emit(wideSide, wideSide == 0 ? bestBid + 1 : bestAsk - 1, RandQty());
                continue;
            }

            int side = (int)(rng.Next() & 1);
            int best = side == 0 ? bestBid : bestAsk;
            if (best < edgeGuard || best >= tickCount - edgeGuard)
            {
                int toward = best < edgeGuard ? best + 1 : best - 1;
                if (toward < 1) toward = 1;
                if (toward > tickCount - 2) toward = tickCount - 2;
                Emit(side, toward, RandQty());
                continue;
            }

            // Restoring bias, in percentage points, proportional to how far the touch
            // has drifted from the centre. Consuming pushes the price away from the
            // centre, improving pulls it back, so the bias is applied to that split.
            int drift = mid - targetMid;
            int bias = drift / 8;
            if (bias > 15) bias = 15;
            if (bias < -15) bias = -15;
            // A bid consume moves price DOWN, an ask consume moves it UP.
            int consumeWeight = 12 + (side == 0 ? bias : -bias);
            if (consumeWeight < 2) consumeWeight = 2;
            if (consumeWeight > 22) consumeWeight = 22;

            // Geometric depth from the touch: the touch is hot, the tail is cold.
            int depth = 0;
            ulong r = rng.Next();
            while (depth < window - 1 && (r & 1) == 1) { depth++; r >>= 1; }

            int Tick(int d) => side == 0 ? best - d : best + d;

            uint roll = rng.NextBounded(100);
            if (roll < 55)
            {
                // Quantity update at an existing level near the touch.
                Emit(side, Tick(depth), RandQty());
            }
            else if (roll < 75)
            {
                // Cancel away from the touch: a plain store, no rescan.
                Emit(side, Tick(depth == 0 ? 1 : depth), 0);
            }
            else if (roll < 75 + consumeWeight)
            {
                // Consume the touch. THIS is the rescan path, and it is what moves the
                // price -- a book walks because its best levels get eaten.
                Emit(side, best, 0);
                // Replenish anchored to the MID, not to the moved best. Anchoring to the
                // best is what ratcheted the book apart in v3.
                int anchor = (bestBid + bestAsk) / 2;
                Emit(side, side == 0 ? anchor - window : anchor + window, RandQty());
            }
            else
            {
                // New level improving the touch by one tick: pushes price the other way.
                Emit(side, side == 0 ? best + 1 : best - 1, RandQty());
            }
        }

        if (n != count)
            throw new InvalidOperationException(
                $"generator produced {n} of {count} events; fixture would be padded with " +
                "default-initialized entries and silently wrong");

        return (tickCount, events);
    }

    /// <summary>
    /// Replays an event stream through the same state machine the benchmark uses and
    /// reports what the workload actually does. Published with the results so the
    /// workload's realism is a measured claim rather than a comment.
    /// </summary>
    public static string DescribeBookWorkload(BookEvent[] events, int tickCount)
    {
        var bid = new long[tickCount];
        var ask = new long[tickCount];
        int bestBid = -1, bestAsk = tickCount;

        long deletes = 0, rescans = 0, rescanIters = 0, bestUpdates = 0;
        var touched = new bool[tickCount];
        int distinct = 0, minTick = int.MaxValue, maxTick = int.MinValue;

        foreach (var e in events)
        {
            int idx = e.TickIndex;
            if (!touched[idx]) { touched[idx] = true; distinct++; }
            if (idx < minTick) minTick = idx;
            if (idx > maxTick) maxTick = idx;
            if (e.Qty == 0) deletes++;

            int prevBid = bestBid, prevAsk = bestAsk;

            if (e.Side == 0)
            {
                bid[idx] = e.Qty;
                if (e.Qty != 0) { if (idx > bestBid) bestBid = idx; }
                else if (idx == bestBid)
                {
                    rescans++;
                    int j = idx - 1;
                    while (j >= 0 && bid[j] == 0) { j--; rescanIters++; }
                    bestBid = j;
                }
            }
            else
            {
                ask[idx] = e.Qty;
                if (e.Qty != 0) { if (idx < bestAsk) bestAsk = idx; }
                else if (idx == bestAsk)
                {
                    rescans++;
                    int j = idx + 1;
                    while (j < tickCount && ask[j] == 0) { j++; rescanIters++; }
                    bestAsk = j;
                }
            }

            if (bestBid != prevBid || bestAsk != prevAsk) bestUpdates++;
        }

        long n = events.Length;

        // Lifetime-touch is NOT a working set, and reporting it hid a real failure: a
        // fixture can touch 99% of the ladder over its life while the last half of the
        // run lives in 25 cache lines. What matters is the RESIDENT set in a window, so
        // that is what is reported. (The old figure also double-counted, because the
        // touched[] array was not per-side.)
        int decileStart = (int)(events.Length * 0.9);
        var linesLate = new HashSet<long>();
        var lineHits = new Dictionary<long, int>();
        for (int i = decileStart; i < events.Length; i++)
        {
            var e = events[i];
            long line = ((long)e.Side << 40) | (uint)((e.TickIndex * 8) / 64);
            linesLate.Add(line);
            lineHits[line] = lineHits.GetValueOrDefault(line) + 1;
        }
        int lateAccesses = events.Length - decileStart;
        int lines95 = 0, acc = 0;
        foreach (var kv in lineHits.OrderByDescending(kv => kv.Value))
        {
            acc += kv.Value;
            lines95++;
            if (acc >= lateAccesses * 0.95) break;
        }

        return $"""
            events              {n:N0}
            ladder ticks        {tickCount:N0}
            deletes             {deletes:N0} ({100.0 * deletes / n:F2}%)
            distinct ticks      {distinct:N0} of {tickCount:N0} ({100.0 * distinct / tickCount:F1}%)
            tick range          {minTick} .. {maxTick}
            rescans triggered   {rescans:N0} ({100.0 * rescans / n:F2}% of events)
            rescan inner iters  {rescanIters:N0} ({(double)rescanIters / n:F4} per event)
            best-level updates  {bestUpdates:N0} ({100.0 * bestUpdates / n:F2}% of events)
            final bestBid/Ask   {bestBid} / {bestAsk}  (spread {bestAsk - bestBid})
            mid drift from mid  {(bestBid + bestAsk) / 2 - tickCount / 2:+#;-#;0} ticks
            RESIDENT last 10%   {linesLate.Count:N0} cache lines ({linesLate.Count * 64:N0} bytes)
            95% of late access  {lines95:N0} cache lines ({lines95 * 64:N0} bytes)
            """;
    }

    // ---------------------------------------------------------------- FIX messages

    /// <summary>
    /// Generates well-formed FIX 4.4 NewOrderSingle messages with correct
    /// BodyLength (tag 9) and CheckSum (tag 10), so the parser's validation path
    /// is real work rather than a no-op.
    /// </summary>
    public static (uint[] Offsets, byte[] Payload) GenerateFixMessages(int count, ulong seed)
    {
        var rng = new Xorshift64(seed);
        var offsets = new uint[count + 1];
        var payload = new List<byte>(count * 160);
        var sb = new StringBuilder(256);

        for (int i = 0; i < count; i++)
        {
            offsets[i] = (uint)payload.Count;

            long seq = 1000 + i;
            string sym = Symbols[rng.NextBounded((uint)Symbols.Length)];
            int sideDigit = (int)(rng.Next() & 1) + 1;          // 1 = buy, 2 = sell
            long qty = rng.NextBounded(5000) + 1;
            long pxCents = 100_000 + rng.NextBounded(900_000);   // 1000.00 .. 9999.99
            long clOrdId = 1_000_000_000L + i;

            // Deterministic wall-clock-looking timestamp, fixed width.
            long totalMs = i * 37L;
            int ms = (int)(totalMs % 1000);
            long totalSec = totalMs / 1000;
            int ss = (int)(totalSec % 60);
            int mm = (int)((totalSec / 60) % 60);
            int hh = (int)((totalSec / 3600) % 24);

            sb.Clear();
            sb.Append("35=D").Append(Soh);
            sb.Append("34=").Append(seq).Append(Soh);
            sb.Append("49=CLIENT").Append(i % 16).Append(Soh);
            sb.Append("56=VENUE").Append(Soh);
            sb.Append("52=20260805-")
              .Append(hh.ToString("D2")).Append(':')
              .Append(mm.ToString("D2")).Append(':')
              .Append(ss.ToString("D2")).Append('.')
              .Append(ms.ToString("D3")).Append(Soh);
            sb.Append("11=ORD").Append(clOrdId.ToString("D10")).Append(Soh);
            sb.Append("55=").Append(sym).Append(Soh);
            sb.Append("54=").Append(sideDigit).Append(Soh);
            sb.Append("38=").Append(qty).Append(Soh);
            sb.Append("40=2").Append(Soh);
            sb.Append("44=").Append(pxCents / 100).Append('.').Append((pxCents % 100).ToString("D2")).Append(Soh);
            sb.Append("59=0").Append(Soh);
            string bodyStr = sb.ToString();

            byte[] body = Encoding.ASCII.GetBytes(bodyStr);
            byte[] head = Encoding.ASCII.GetBytes($"8=FIX.4.4{Soh}9={body.Length}{Soh}");

            int sum = 0;
            foreach (byte b in head) sum += b;
            foreach (byte b in body) sum += b;
            byte[] trailer = Encoding.ASCII.GetBytes($"10={(sum % 256):D3}{Soh}");

            payload.AddRange(head);
            payload.AddRange(body);
            payload.AddRange(trailer);
        }

        offsets[count] = (uint)payload.Count;
        return (offsets, payload.ToArray());
    }
}
