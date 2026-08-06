using System.Runtime.CompilerServices;

namespace Bench.Core;

/// <summary>
/// Operation-counting policy.
///
/// The checksum oracle proves both languages produce the same OUTPUT. It does not
/// prove they perform the same WORK -- a cheaper path on one side that reaches the
/// same answer would pass it. This closes that gap.
///
/// Both workloads are generic over this policy, so the timed code and the counted
/// code are the SAME SOURCE specialized two ways. <see cref="NullTrace"/> has empty
/// static methods on an empty struct, so RyuJIT specializes it away and the timed
/// path is unchanged. <see cref="CountTrace"/> records every branch taken, every
/// loop iteration, and every switch arm.
///
/// The C++ side implements the identical policy as a template. Counter vectors from
/// the two languages must match element for element.
/// </summary>
public interface ITrace
{
    static abstract void Count(int slot);
    static abstract void Add(int slot, long n);
}

/// <summary>Zero-cost. Used for every timed run.</summary>
public struct NullTrace : ITrace
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Count(int slot) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(int slot, long n) { }
}

/// <summary>Counting. Used only by the `verify` command, never inside a timed region.</summary>
public struct CountTrace : ITrace
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Count(int slot) => TraceCounters.Values[slot]++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(int slot, long n) => TraceCounters.Values[slot] += n;
}

public static class TraceCounters
{
    public const int Size = 48;
    public static readonly long[] Values = new long[Size];

    public static void Reset() => Array.Clear(Values);

    // ---- order book slots -------------------------------------------------
    public const int BookEvents        = 0;
    public const int BookSideBid       = 1;
    public const int BookSideAsk       = 2;
    public const int BookQtyNonZero    = 3;
    public const int BookQtyZero       = 4;
    public const int BookBidImprove    = 5;
    public const int BookAskImprove    = 6;
    public const int BookBidRescan     = 7;
    public const int BookAskRescan     = 8;
    public const int BookBidRescanIter = 9;
    public const int BookAskRescanIter = 10;
    public const int BookBestBidValid  = 11;
    public const int BookBestAskValid  = 12;
    public const int BookMix           = 13;

    // ---- fix parse slots --------------------------------------------------
    public const int FixMessages       = 20;
    public const int FixFields         = 21;
    public const int FixTagDigits      = 22;
    public const int FixScanCalls      = 23;
    public const int FixScanDistance   = 24;
    public const int FixCase34         = 25;
    public const int FixCase11         = 26;
    public const int FixCase55         = 27;
    public const int FixCase54         = 28;
    public const int FixCase38         = 29;
    public const int FixCase44         = 30;
    public const int FixCase10         = 31;
    public const int FixCaseOther      = 32;
    public const int FixParseLongDigit = 33;
    public const int FixPriceIntDigit  = 34;
    public const int FixPriceFracDigit = 35;
    public const int FixPricePadDigit  = 36;
    public const int FixFoldBytes      = 37;
    public const int FixSumBytes       = 38;
    public const int FixMix            = 39;
    public const int FixNoSoh          = 40;

    public static readonly (int Slot, string Name)[] Names =
    [
        (BookEvents, "book.events"),
        (BookSideBid, "book.side_bid"),
        (BookSideAsk, "book.side_ask"),
        (BookQtyNonZero, "book.qty_nonzero"),
        (BookQtyZero, "book.qty_zero"),
        (BookBidImprove, "book.bid_improve"),
        (BookAskImprove, "book.ask_improve"),
        (BookBidRescan, "book.bid_rescan"),
        (BookAskRescan, "book.ask_rescan"),
        (BookBidRescanIter, "book.bid_rescan_iters"),
        (BookAskRescanIter, "book.ask_rescan_iters"),
        (BookBestBidValid, "book.bestbid_valid"),
        (BookBestAskValid, "book.bestask_valid"),
        (BookMix, "book.mix_calls"),
        (FixMessages, "fix.messages"),
        (FixFields, "fix.fields"),
        (FixTagDigits, "fix.tag_digits"),
        (FixScanCalls, "fix.scan_calls"),
        (FixScanDistance, "fix.scan_distance"),
        (FixCase34, "fix.case_34"),
        (FixCase11, "fix.case_11"),
        (FixCase55, "fix.case_55"),
        (FixCase54, "fix.case_54"),
        (FixCase38, "fix.case_38"),
        (FixCase44, "fix.case_44"),
        (FixCase10, "fix.case_10"),
        (FixCaseOther, "fix.case_other"),
        (FixParseLongDigit, "fix.parselong_digits"),
        (FixPriceIntDigit, "fix.price_int_digits"),
        (FixPriceFracDigit, "fix.price_frac_digits"),
        (FixPricePadDigit, "fix.price_pad_digits"),
        (FixFoldBytes, "fix.fold_bytes"),
        (FixSumBytes, "fix.sum_bytes"),
        (FixMix, "fix.mix_calls"),
        (FixNoSoh, "fix.no_soh_break"),
    ];
}
