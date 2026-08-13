using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Bench.Core;

/// <summary>
/// Policy for the FIX CheckSum (tag 10) byte-sum. Implemented as a struct generic so
/// RyuJIT specializes each variant and constant-folds <see cref="Enabled"/> — the
/// parse loop itself is shared by every variant, so the variants cannot drift apart.
/// The C++ side uses a template with the same three policies.
/// </summary>
public interface ISumPolicy
{
    static abstract bool Enabled { get; }
    static abstract int Sum(ReadOnlySpan<byte> s);
}

/// <summary>Straightforward scalar byte-sum. What almost everyone writes.</summary>
public struct ScalarSum : ISumPolicy
{
    public static bool Enabled => true;

    public static int Sum(ReadOnlySpan<byte> s)
    {
        int total = 0;
        for (int i = 0; i < s.Length; i++) total += s[i];
        return total;
    }
}

/// <summary>
/// Explicitly vectorized byte-sum via PSADBW, which is what a C++ compiler emits on
/// its own for the scalar loop. RyuJIT does not auto-vectorize, so on the C# side
/// this has to be written by hand — that asymmetry is the finding, not a defect.
///
/// Loads via <c>Vector256.LoadUnsafe</c>, the idiomatic fast path.
/// </summary>
public struct SimdSum : ISumPolicy
{
    public static bool Enabled => true;

    public static int Sum(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        int i = 0;
        int total = 0;

        if (Avx2.IsSupported && n >= 32)
        {
            ref byte origin = ref MemoryMarshal.GetReference(s);
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = n - 32;
            for (; i <= limit; i += 32)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref origin, (nuint)i);
                acc += Avx2.SumAbsoluteDifferences(v, Vector256<byte>.Zero).AsUInt64();
            }
            // Same register-only reduction shape the C++ HandSimdSum policy uses, so the
            // equal-effort pairing compares the same reduction rather than a 4-lane
            // extract against a stack round-trip.
            Vector128<ulong> sum128 = Sse2.Add(acc.GetLower(), acc.GetUpper());
            total = (int)(sum128.GetElement(0) + sum128.GetElement(1));
        }

        for (; i < n; i++) total += s[i];
        return total;
    }
}

/// <summary>
/// Identical vector math to <see cref="SimdSum"/>, but loads with the fully bounds-
/// checked <c>Vector256.Create&lt;byte&gt;(span.Slice(i, 32))</c> instead of
/// <c>LoadUnsafe</c>.
///
/// RETRACTION: originally labelled a "trap". It is not one — under optimized codegen
/// it is the FASTER of the two in 3 of 5 configurations (90.5 vs 92.8 on jit-tc0).
/// The earlier 2x penalty was instrumented tier-0 code, not a property of the API.
///
/// This is also the only genuinely safe vector path: <see cref="SimdSum"/> uses
/// <c>MemoryMarshal.GetReference</c> + <c>LoadUnsafe</c>, which elides bounds checks
/// and therefore does not satisfy the repo's "safe code only" claim.
/// </summary>
public struct SimdSumCreate : ISumPolicy
{
    public static bool Enabled => true;

    public static int Sum(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        int i = 0;
        int total = 0;

        if (Avx2.IsSupported && n >= 32)
        {
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            int limit = n - 32;
            for (; i <= limit; i += 32)
            {
                Vector256<byte> v = Vector256.Create<byte>(s.Slice(i, 32));
                acc += Avx2.SumAbsoluteDifferences(v, Vector256<byte>.Zero).AsUInt64();
            }
            // Same register-only reduction shape the C++ HandSimdSum policy uses, so the
            // equal-effort pairing compares the same reduction rather than a 4-lane
            // extract against a stack round-trip.
            Vector128<ulong> sum128 = Sse2.Add(acc.GetLower(), acc.GetUpper());
            total = (int)(sum128.GetElement(0) + sum128.GetElement(1));
        }

        for (; i < n; i++) total += s[i];
        return total;
    }
}

/// <summary>
/// Diagnostic only: skips CheckSum validation entirely. Produces a different oracle
/// value by design, so it is compared against the C++ no-sum variant, never against
/// the validating ones.
/// </summary>
public struct NoSum : ISumPolicy
{
    public static bool Enabled => false;
    public static int Sum(ReadOnlySpan<byte> s) => 0;
}

/// <summary>
/// FIX 4.4 NewOrderSingle field decode: tag scan, integer decode, fixed-point price
/// decode, and CheckSum (tag 10) validation. Allocation-free.
///
/// The C++ side uses <c>memchr</c> for the SOH scan; the C# side uses
/// <c>Span&lt;byte&gt;.IndexOf</c>, which is the vectorized equivalent. Neither side
/// gets a hand-written scalar scan the other does not.
/// </summary>
public static class FixParseWorkload
{
    private const byte Soh = 0x01;
    private const byte Eq = (byte)'=';
    private const byte Dot = (byte)'.';
    private const byte Zero = (byte)'0';

    /// <summary>Headline C# implementation: safe code, scalar checksum.</summary>
    public static ulong Run(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        => Run<ScalarSum>(payload, offsets, count);

    public static ulong RunSimd(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        => Run<SimdSum>(payload, offsets, count);

    public static ulong RunSimdCreate(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        => Run<SimdSumCreate>(payload, offsets, count);

    public static ulong RunNoSum(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        => Run<NoSum>(payload, offsets, count);

    public static ulong Run<TSum>(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        where TSum : struct, ISumPolicy
        => Run<TSum, NullTrace>(payload, offsets, count);

    public static ulong Run<TSum, TTrace>(
        ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        where TSum : struct, ISumPolicy
        where TTrace : struct, ITrace
    {
        ulong h = Checksum.Offset;
        for (int m = 0; m < count; m++)
        {
            TTrace.Count(TraceCounters.FixMessages);
            int start = (int)offsets[m];
            int len = (int)offsets[m + 1] - start;
            h = ParseOne<TSum, TTrace>(payload.Slice(start, len), h);
        }
        return h;
    }

    private static ulong ParseOne<TSum, TTrace>(ReadOnlySpan<byte> msg, ulong h)
        where TSum : struct, ISumPolicy
        where TTrace : struct, ITrace
    {
        long seq = 0, qty = 0, px = 0;
        int side = 0;
        int declared = -1, computed = -2;

        int pos = 0;
        int len = msg.Length;

        while (pos < len)
        {
            TTrace.Count(TraceCounters.FixFields);
            int tagStart = pos;

            int tag = 0;
            while (pos < len && msg[pos] != Eq)
            {
                TTrace.Count(TraceCounters.FixTagDigits);
                tag = tag * 10 + (msg[pos] - Zero);
                pos++;
            }
            pos++; // '='

            TTrace.Count(TraceCounters.FixScanCalls);
            int rel = msg[pos..].IndexOf(Soh);
            if (rel < 0) { TTrace.Count(TraceCounters.FixNoSoh); break; }
            TTrace.Add(TraceCounters.FixScanDistance, rel);
            int vend = pos + rel;
            ReadOnlySpan<byte> val = msg[pos..vend];

            switch (tag)
            {
                case 34: TTrace.Count(TraceCounters.FixCase34); seq = ParseLong<TTrace>(val); break;
                case 11: TTrace.Count(TraceCounters.FixCase11); h = FoldBytes<TTrace>(h, val); break;
                case 55: TTrace.Count(TraceCounters.FixCase55); h = FoldBytes<TTrace>(h, val); break;
                case 54: TTrace.Count(TraceCounters.FixCase54); side = val[0] - Zero; break;
                case 38: TTrace.Count(TraceCounters.FixCase38); qty = ParseLong<TTrace>(val); break;
                case 44: TTrace.Count(TraceCounters.FixCase44); px = ParsePrice6<TTrace>(val); break;
                case 10:
                    TTrace.Count(TraceCounters.FixCase10);
                    declared = (int)ParseLong<TTrace>(val);
                    if (TSum.Enabled)
                    {
                        TTrace.Add(TraceCounters.FixSumBytes, tagStart);
                        computed = TSum.Sum(msg[..tagStart]) & 0xFF;
                    }
                    break;
                default: TTrace.Count(TraceCounters.FixCaseOther); break;
            }

            pos = vend + 1;
        }

        TTrace.Add(TraceCounters.FixMix, 5);
        h = Checksum.Mix(h, (ulong)seq);
        h = Checksum.Mix(h, (ulong)qty);
        h = Checksum.Mix(h, (ulong)px);
        h = Checksum.Mix(h, (ulong)(long)side);
        h = Checksum.Mix(h, declared == computed ? 1UL : 0UL);
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParseLong<TTrace>(ReadOnlySpan<byte> s) where TTrace : struct, ITrace
    {
        long v = 0;
        for (int i = 0; i < s.Length; i++)
        {
            TTrace.Count(TraceCounters.FixParseLongDigit);
            v = v * 10 + (s[i] - Zero);
        }
        return v;
    }

    /// <summary>Decimal ASCII to fixed-point scaled by 1e6.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParsePrice6<TTrace>(ReadOnlySpan<byte> s) where TTrace : struct, ITrace
    {
        long whole = 0;
        int i = 0;
        for (; i < s.Length && s[i] != Dot; i++)
        {
            TTrace.Count(TraceCounters.FixPriceIntDigit);
            whole = whole * 10 + (s[i] - Zero);
        }

        long frac = 0;
        int digits = 0;
        if (i < s.Length && s[i] == Dot)
        {
            i++;
            for (; i < s.Length && digits < 6; i++, digits++)
            {
                TTrace.Count(TraceCounters.FixPriceFracDigit);
                frac = frac * 10 + (s[i] - Zero);
            }
        }
        while (digits < 6) { TTrace.Count(TraceCounters.FixPricePadDigit); frac *= 10; digits++; }

        return whole * 1_000_000L + frac;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FoldBytes<TTrace>(ulong h, ReadOnlySpan<byte> s) where TTrace : struct, ITrace
    {
        for (int i = 0; i < s.Length; i++)
        {
            TTrace.Count(TraceCounters.FixFoldBytes);
            h = Checksum.Mix(h, s[i]);
        }
        return h;
    }

    // ------------------------------------------------------------------ tuned safe C#
    //
    // Same algorithm, same operations (verified by the trace counters), same output.
    // Only the codegen differs:
    //
    //   * AggressiveOptimization  -- straight to the optimizing JIT, no tiering. Scoped
    //     to the method rather than the whole process.
    //   * ref-based element access via MemoryMarshal.GetReference + Unsafe.Add, which
    //     removes the per-byte bounds check from the four hot inner loops (tag digits
    //     5.6M, ParseLong 2.4M, ParsePrice6 2.0M, FoldBytes 3.5M -- about 13M checks
    //     over the fixture) WITHOUT `fixed`. Pinning is what made the earlier
    //     `RunUnsafe` variant slower than safe code.
    //   * The SOH scan still goes through a managed span, so it keeps the vectorized
    //     IndexOf that pairs fairly against memchr.

    public static ulong RunOpt(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        => RunOpt<ScalarSum, NullTrace>(payload, offsets, count);

    public static ulong RunOptSimd(ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        => RunOpt<SimdSum, NullTrace>(payload, offsets, count);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong RunOpt<TSum, TTrace>(
        ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
        where TSum : struct, ISumPolicy
        where TTrace : struct, ITrace
    {
        ulong h = Checksum.Offset;
        ref byte pay0 = ref MemoryMarshal.GetReference(payload);
        ref uint off0 = ref MemoryMarshal.GetReference(offsets);

        for (int m = 0; m < count; m++)
        {
            TTrace.Count(TraceCounters.FixMessages);
            int start = (int)Unsafe.Add(ref off0, (nint)m);
            int len = (int)Unsafe.Add(ref off0, (nint)(m + 1)) - start;
            h = ParseOneOpt<TSum, TTrace>(ref Unsafe.Add(ref pay0, (nint)start), len, h);
        }
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong ParseOneOpt<TSum, TTrace>(ref byte msg, int len, ulong h)
        where TSum : struct, ISumPolicy
        where TTrace : struct, ITrace
    {
        long seq = 0, qty = 0, px = 0;
        int side = 0;
        int declared = -1, computed = -2;

        int pos = 0;
        while (pos < len)
        {
            TTrace.Count(TraceCounters.FixFields);
            int tagStart = pos;

            int tag = 0;
            while (pos < len && Unsafe.Add(ref msg, (nint)pos) != Eq)
            {
                TTrace.Count(TraceCounters.FixTagDigits);
                tag = tag * 10 + (Unsafe.Add(ref msg, (nint)pos) - Zero);
                pos++;
            }
            pos++; // '='

            TTrace.Count(TraceCounters.FixScanCalls);
            int rel = MemoryMarshal
                .CreateReadOnlySpan(ref Unsafe.Add(ref msg, (nint)pos), len - pos)
                .IndexOf(Soh);
            if (rel < 0) { TTrace.Count(TraceCounters.FixNoSoh); break; }
            TTrace.Add(TraceCounters.FixScanDistance, rel);

            int vend = pos + rel;
            ref byte val = ref Unsafe.Add(ref msg, (nint)pos);
            int vlen = rel;

            switch (tag)
            {
                case 34: TTrace.Count(TraceCounters.FixCase34); seq = ParseLongRef<TTrace>(ref val, vlen); break;
                case 11: TTrace.Count(TraceCounters.FixCase11); h = FoldBytesRef<TTrace>(h, ref val, vlen); break;
                case 55: TTrace.Count(TraceCounters.FixCase55); h = FoldBytesRef<TTrace>(h, ref val, vlen); break;
                case 54: TTrace.Count(TraceCounters.FixCase54); side = val - Zero; break;
                case 38: TTrace.Count(TraceCounters.FixCase38); qty = ParseLongRef<TTrace>(ref val, vlen); break;
                case 44: TTrace.Count(TraceCounters.FixCase44); px = ParsePrice6Ref<TTrace>(ref val, vlen); break;
                case 10:
                    TTrace.Count(TraceCounters.FixCase10);
                    declared = (int)ParseLongRef<TTrace>(ref val, vlen);
                    if (TSum.Enabled)
                    {
                        TTrace.Add(TraceCounters.FixSumBytes, tagStart);
                        computed = TSum.Sum(MemoryMarshal.CreateReadOnlySpan(ref msg, tagStart)) & 0xFF;
                    }
                    break;
                default: TTrace.Count(TraceCounters.FixCaseOther); break;
            }

            pos = vend + 1;
        }

        TTrace.Add(TraceCounters.FixMix, 5);
        h = Checksum.Mix(h, (ulong)seq);
        h = Checksum.Mix(h, (ulong)qty);
        h = Checksum.Mix(h, (ulong)px);
        h = Checksum.Mix(h, (ulong)(long)side);
        h = Checksum.Mix(h, declared == computed ? 1UL : 0UL);
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParseLongRef<TTrace>(ref byte s, int n) where TTrace : struct, ITrace
    {
        long v = 0;
        for (int i = 0; i < n; i++)
        {
            TTrace.Count(TraceCounters.FixParseLongDigit);
            v = v * 10 + (Unsafe.Add(ref s, (nint)i) - Zero);
        }
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParsePrice6Ref<TTrace>(ref byte s, int n) where TTrace : struct, ITrace
    {
        long whole = 0;
        int i = 0;
        for (; i < n && Unsafe.Add(ref s, (nint)i) != Dot; i++)
        {
            TTrace.Count(TraceCounters.FixPriceIntDigit);
            whole = whole * 10 + (Unsafe.Add(ref s, (nint)i) - Zero);
        }

        long frac = 0;
        int digits = 0;
        if (i < n && Unsafe.Add(ref s, (nint)i) == Dot)
        {
            i++;
            for (; i < n && digits < 6; i++, digits++)
            {
                TTrace.Count(TraceCounters.FixPriceFracDigit);
                frac = frac * 10 + (Unsafe.Add(ref s, (nint)i) - Zero);
            }
        }
        while (digits < 6) { TTrace.Count(TraceCounters.FixPricePadDigit); frac *= 10; digits++; }

        return whole * 1_000_000L + frac;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FoldBytesRef<TTrace>(ulong h, ref byte s, int n) where TTrace : struct, ITrace
    {
        for (int i = 0; i < n; i++)
        {
            TTrace.Count(TraceCounters.FixFoldBytes);
            h = Checksum.Mix(h, Unsafe.Add(ref s, (nint)i));
        }
        return h;
    }

    // ------------------------------------------------------------------ unsafe variants
    // Diagnostic only. The headline C# number is always Run().

    /// <summary>
    /// Element bounds checks removed via raw pointers, while the SOH scan stays on a
    /// managed span so it keeps the vectorized <c>IndexOf</c> — the fair counterpart
    /// to the C++ side's <c>memchr</c>.
    /// </summary>
    public static unsafe ulong RunUnsafe(
        ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
    {
        ulong h = Checksum.Offset;
        fixed (byte* pay = payload)
        fixed (uint* off = offsets)
        {
            for (int m = 0; m < count; m++)
            {
                int start = (int)off[m];
                int len = (int)off[m + 1] - start;
                h = ParseOneUnsafe(pay + start, payload.Slice(start, len), h);
            }
        }
        return h;
    }

    /// <summary>
    /// Differs from <see cref="RunUnsafe"/> only in that the SOH scan calls
    /// <c>IndexOf</c> on a span built from a raw pointer
    /// (<c>new ReadOnlySpan&lt;byte&gt;(ptr, len)</c>) rather than on a managed span.
    ///
    /// RETRACTION: this was originally labelled a "trap" and documented as ~5x slower.
    /// That was wrong. The 5x was measured against instrumented tier-0 code; under
    /// optimized codegen it is 132.4 vs 128.8 ns/op, i.e. 1.12x, which is close to the
    /// run-to-run noise floor. Kept only as a diagnostic, and named plainly.
    /// </summary>
    public static unsafe ulong RunUnsafePtrSpanScan(
        ReadOnlySpan<byte> payload, ReadOnlySpan<uint> offsets, int count)
    {
        ulong h = Checksum.Offset;
        fixed (byte* pay = payload)
        fixed (uint* off = offsets)
        {
            for (int m = 0; m < count; m++)
            {
                int start = (int)off[m];
                int len = (int)off[m + 1] - start;
                h = ParseOnePtrSpanScan(pay + start, len, h);
            }
        }
        return h;
    }

    private static unsafe ulong ParseOneUnsafe(byte* msg, ReadOnlySpan<byte> msgSpan, ulong h)
    {
        long seq = 0, qty = 0, px = 0;
        int side = 0;
        int declared = -1, computed = -2;

        int pos = 0;
        int len = msgSpan.Length;
        while (pos < len)
        {
            int tagStart = pos;

            int tag = 0;
            while (pos < len && msg[pos] != Eq)
            {
                tag = tag * 10 + (msg[pos] - Zero);
                pos++;
            }
            pos++;

            int rel = msgSpan[pos..].IndexOf(Soh);   // managed span, keeps vectorized IndexOf
            if (rel < 0) break;
            int vend = pos + rel;
            byte* val = msg + pos;
            int vlen = vend - pos;

            switch (tag)
            {
                case 34: seq = ParseLongU(val, vlen); break;
                case 11: h = FoldBytesU(h, val, vlen); break;
                case 55: h = FoldBytesU(h, val, vlen); break;
                case 54: side = val[0] - Zero; break;
                case 38: qty = ParseLongU(val, vlen); break;
                case 44: px = ParsePrice6U(val, vlen); break;
                case 10:
                    declared = (int)ParseLongU(val, vlen);
                    int sum = 0;
                    for (int k = 0; k < tagStart; k++) sum += msg[k];
                    computed = sum & 0xFF;
                    break;
            }

            pos = vend + 1;
        }

        h = Checksum.Mix(h, (ulong)seq);
        h = Checksum.Mix(h, (ulong)qty);
        h = Checksum.Mix(h, (ulong)px);
        h = Checksum.Mix(h, (ulong)(long)side);
        h = Checksum.Mix(h, declared == computed ? 1UL : 0UL);
        return h;
    }

    private static unsafe ulong ParseOnePtrSpanScan(byte* msg, int len, ulong h)
    {
        long seq = 0, qty = 0, px = 0;
        int side = 0;
        int declared = -1, computed = -2;

        int pos = 0;
        while (pos < len)
        {
            int tagStart = pos;

            int tag = 0;
            while (pos < len && msg[pos] != Eq)
            {
                tag = tag * 10 + (msg[pos] - Zero);
                pos++;
            }
            pos++;

            int rel = new ReadOnlySpan<byte>(msg + pos, len - pos).IndexOf(Soh);
            if (rel < 0) break;
            int vend = pos + rel;
            byte* val = msg + pos;
            int vlen = vend - pos;

            switch (tag)
            {
                case 34: seq = ParseLongU(val, vlen); break;
                case 11: h = FoldBytesU(h, val, vlen); break;
                case 55: h = FoldBytesU(h, val, vlen); break;
                case 54: side = val[0] - Zero; break;
                case 38: qty = ParseLongU(val, vlen); break;
                case 44: px = ParsePrice6U(val, vlen); break;
                case 10:
                    declared = (int)ParseLongU(val, vlen);
                    int sum = 0;
                    for (int k = 0; k < tagStart; k++) sum += msg[k];
                    computed = sum & 0xFF;
                    break;
            }

            pos = vend + 1;
        }

        h = Checksum.Mix(h, (ulong)seq);
        h = Checksum.Mix(h, (ulong)qty);
        h = Checksum.Mix(h, (ulong)px);
        h = Checksum.Mix(h, (ulong)(long)side);
        h = Checksum.Mix(h, declared == computed ? 1UL : 0UL);
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ParseLongU(byte* s, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = v * 10 + (s[i] - Zero);
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ParsePrice6U(byte* s, int n)
    {
        long whole = 0;
        int i = 0;
        for (; i < n && s[i] != Dot; i++) whole = whole * 10 + (s[i] - Zero);

        long frac = 0;
        int digits = 0;
        if (i < n && s[i] == Dot)
        {
            i++;
            for (; i < n && digits < 6; i++, digits++) frac = frac * 10 + (s[i] - Zero);
        }
        while (digits < 6) { frac *= 10; digits++; }

        return whole * 1_000_000L + frac;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong FoldBytesU(ulong h, byte* s, int n)
    {
        for (int i = 0; i < n; i++) h = Checksum.Mix(h, s[i]);
        return h;
    }
}
