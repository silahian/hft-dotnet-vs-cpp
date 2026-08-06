#pragma once
#include <cstdint>

// Operation-counting policy. Counterpart of Bench.Core/Trace.cs.
//
// The checksum oracle proves both languages produce the same OUTPUT. It does not
// prove they perform the same WORK. Both workloads are templated on this policy so
// the timed code and the counted code are the SAME SOURCE specialized twice.
// NullTrace's methods are empty and inline away; CountTrace records every branch,
// loop iteration and switch arm. The resulting counter vectors must match the C#
// ones element for element.
namespace bench {

struct NullTrace {
    static void count(int) noexcept {}
    static void add(int, int64_t) noexcept {}
};

struct CountTrace {
    static constexpr int kSize = 48;
    static inline int64_t values[kSize] = {};

    static void reset() noexcept {
        for (int i = 0; i < kSize; ++i) values[i] = 0;
    }
    static void count(int slot) noexcept { values[slot]++; }
    static void add(int slot, int64_t n) noexcept { values[slot] += n; }
};

// Slot numbering MUST match Bench.Core/TraceCounters.
namespace slot {

// order book
inline constexpr int kBookEvents        = 0;
inline constexpr int kBookSideBid       = 1;
inline constexpr int kBookSideAsk       = 2;
inline constexpr int kBookQtyNonZero    = 3;
inline constexpr int kBookQtyZero       = 4;
inline constexpr int kBookBidImprove    = 5;
inline constexpr int kBookAskImprove    = 6;
inline constexpr int kBookBidRescan     = 7;
inline constexpr int kBookAskRescan     = 8;
inline constexpr int kBookBidRescanIter = 9;
inline constexpr int kBookAskRescanIter = 10;
inline constexpr int kBookBestBidValid  = 11;
inline constexpr int kBookBestAskValid  = 12;
inline constexpr int kBookMix           = 13;

// fix parse
inline constexpr int kFixMessages       = 20;
inline constexpr int kFixFields         = 21;
inline constexpr int kFixTagDigits      = 22;
inline constexpr int kFixScanCalls      = 23;
inline constexpr int kFixScanDistance   = 24;
inline constexpr int kFixCase34         = 25;
inline constexpr int kFixCase11         = 26;
inline constexpr int kFixCase55         = 27;
inline constexpr int kFixCase54         = 28;
inline constexpr int kFixCase38         = 29;
inline constexpr int kFixCase44         = 30;
inline constexpr int kFixCase10         = 31;
inline constexpr int kFixCaseOther      = 32;
inline constexpr int kFixParseLongDigit = 33;
inline constexpr int kFixPriceIntDigit  = 34;
inline constexpr int kFixPriceFracDigit = 35;
inline constexpr int kFixPricePadDigit  = 36;
inline constexpr int kFixFoldBytes      = 37;
inline constexpr int kFixSumBytes       = 38;
inline constexpr int kFixMix            = 39;
inline constexpr int kFixNoSoh          = 40;

struct Named {
    int slot;
    const char* name;
};

inline constexpr Named kNames[] = {
    {kBookEvents, "book.events"},
    {kBookSideBid, "book.side_bid"},
    {kBookSideAsk, "book.side_ask"},
    {kBookQtyNonZero, "book.qty_nonzero"},
    {kBookQtyZero, "book.qty_zero"},
    {kBookBidImprove, "book.bid_improve"},
    {kBookAskImprove, "book.ask_improve"},
    {kBookBidRescan, "book.bid_rescan"},
    {kBookAskRescan, "book.ask_rescan"},
    {kBookBidRescanIter, "book.bid_rescan_iters"},
    {kBookAskRescanIter, "book.ask_rescan_iters"},
    {kBookBestBidValid, "book.bestbid_valid"},
    {kBookBestAskValid, "book.bestask_valid"},
    {kBookMix, "book.mix_calls"},
    {kFixMessages, "fix.messages"},
    {kFixFields, "fix.fields"},
    {kFixTagDigits, "fix.tag_digits"},
    {kFixScanCalls, "fix.scan_calls"},
    {kFixScanDistance, "fix.scan_distance"},
    {kFixCase34, "fix.case_34"},
    {kFixCase11, "fix.case_11"},
    {kFixCase55, "fix.case_55"},
    {kFixCase54, "fix.case_54"},
    {kFixCase38, "fix.case_38"},
    {kFixCase44, "fix.case_44"},
    {kFixCase10, "fix.case_10"},
    {kFixCaseOther, "fix.case_other"},
    {kFixParseLongDigit, "fix.parselong_digits"},
    {kFixPriceIntDigit, "fix.price_int_digits"},
    {kFixPriceFracDigit, "fix.price_frac_digits"},
    {kFixPricePadDigit, "fix.price_pad_digits"},
    {kFixFoldBytes, "fix.fold_bytes"},
    {kFixSumBytes, "fix.sum_bytes"},
    {kFixMix, "fix.mix_calls"},
    {kFixNoSoh, "fix.no_soh_break"},
};

} // namespace slot
} // namespace bench
