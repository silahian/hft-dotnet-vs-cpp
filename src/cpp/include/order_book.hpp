#pragma once
#include <cstdint>
#include <cstring>

#include "checksum.hpp"
#include "formats.hpp"
#include "trace.hpp"

// Direct-indexed L2 price ladder. Line-for-line counterpart of
// Bench.Core/OrderBookWorkload.cs: same arrays, same element type, same best-level
// caching, same rescan-outward on deletion of the touch, and the same trace calls.
//
// Templated on the trace policy so the timed path and the operation-counting path
// are the same source. NullTrace's methods are empty and inline away.
namespace bench {

template <class TTrace = NullTrace>
[[nodiscard]] inline uint64_t run_order_book(
    const BookEvent* events, size_t n, int64_t* bid, int64_t* ask, int tick_count) noexcept {

    std::memset(bid, 0, sizeof(int64_t) * static_cast<size_t>(tick_count));
    std::memset(ask, 0, sizeof(int64_t) * static_cast<size_t>(tick_count));

    int best_bid = -1;
    int best_ask = tick_count;
    uint64_t h = kChecksumOffset;

    for (size_t i = 0; i < n; ++i) {
        TTrace::count(slot::kBookEvents);

        const BookEvent e = events[i];
        const int idx = e.tick_index;
        const int64_t qty = e.qty;

        if (qty != 0) TTrace::count(slot::kBookQtyNonZero);
        else TTrace::count(slot::kBookQtyZero);

        if (e.side == 0) {
            TTrace::count(slot::kBookSideBid);
            bid[idx] = qty;
            if (qty != 0) {
                if (idx > best_bid) { TTrace::count(slot::kBookBidImprove); best_bid = idx; }
            } else if (idx == best_bid) {
                TTrace::count(slot::kBookBidRescan);
                int j = idx - 1;
                while (j >= 0 && bid[j] == 0) { TTrace::count(slot::kBookBidRescanIter); --j; }
                best_bid = j;
            }
        } else {
            TTrace::count(slot::kBookSideAsk);
            ask[idx] = qty;
            if (qty != 0) {
                if (idx < best_ask) { TTrace::count(slot::kBookAskImprove); best_ask = idx; }
            } else if (idx == best_ask) {
                TTrace::count(slot::kBookAskRescan);
                int j = idx + 1;
                while (j < tick_count && ask[j] == 0) { TTrace::count(slot::kBookAskRescanIter); ++j; }
                best_ask = j;
            }
        }

        int64_t bq;
        if (best_bid >= 0) { TTrace::count(slot::kBookBestBidValid); bq = bid[best_bid]; }
        else bq = 0;

        int64_t aq;
        if (best_ask < tick_count) { TTrace::count(slot::kBookBestAskValid); aq = ask[best_ask]; }
        else aq = 0;

        TTrace::add(slot::kBookMix, 4);
        h = mix(h, static_cast<uint64_t>(static_cast<int64_t>(best_bid)));
        h = mix(h, static_cast<uint64_t>(bq));
        h = mix(h, static_cast<uint64_t>(static_cast<int64_t>(best_ask)));
        h = mix(h, static_cast<uint64_t>(aq));
    }

    return h;
}

} // namespace bench
