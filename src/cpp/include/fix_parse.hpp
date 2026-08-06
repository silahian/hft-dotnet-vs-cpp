#pragma once
#include <cstdint>
#include <cstring>

#if defined(__AVX2__) || (defined(_MSC_VER) && defined(__AVX2__))
#include <immintrin.h>
#endif

#include "checksum.hpp"
#include "trace.hpp"

// FIX 4.4 NewOrderSingle field decode. Line-for-line counterpart of
// Bench.Core/FixParseWorkload.cs.
//
// The SOH scan uses memchr, which is the fair counterpart to the C# side's
// vectorized Span<byte>.IndexOf. Neither side gets a hand-written scalar scan
// the other does not.
namespace bench {

namespace detail {

constexpr uint8_t kSoh = 0x01;
constexpr uint8_t kEq = static_cast<uint8_t>('=');
constexpr uint8_t kDot = static_cast<uint8_t>('.');
constexpr uint8_t kZero = static_cast<uint8_t>('0');

template <class TTrace>
[[nodiscard]] inline int64_t parse_long(const uint8_t* s, int n) noexcept {
    int64_t v = 0;
    for (int i = 0; i < n; ++i) {
        TTrace::count(slot::kFixParseLongDigit);
        v = v * 10 + (s[i] - kZero);
    }
    return v;
}

// Decimal ASCII to fixed-point scaled by 1e6.
template <class TTrace>
[[nodiscard]] inline int64_t parse_price6(const uint8_t* s, int n) noexcept {
    int64_t whole = 0;
    int i = 0;
    for (; i < n && s[i] != kDot; ++i) {
        TTrace::count(slot::kFixPriceIntDigit);
        whole = whole * 10 + (s[i] - kZero);
    }

    int64_t frac = 0;
    int digits = 0;
    if (i < n && s[i] == kDot) {
        ++i;
        for (; i < n && digits < 6; ++i, ++digits) {
            TTrace::count(slot::kFixPriceFracDigit);
            frac = frac * 10 + (s[i] - kZero);
        }
    }
    while (digits < 6) { TTrace::count(slot::kFixPricePadDigit); frac *= 10; ++digits; }

    return whole * 1'000'000LL + frac;
}

template <class TTrace>
[[nodiscard]] inline uint64_t fold_bytes(uint64_t h, const uint8_t* s, int n) noexcept {
    for (int i = 0; i < n; ++i) {
        TTrace::count(slot::kFixFoldBytes);
        h = mix(h, s[i]);
    }
    return h;
}

} // namespace detail

// CheckSum byte-sum policies. Same split as the C# ISumPolicy structs, so the parse
// loop is shared by every variant and the variants cannot drift apart.
//
// There are TWO comparable pairings, and both are published, because they answer
// different questions:
//
//   same-source   : C++ ScalarSum      vs C# ScalarSum   -- identical source in both
//                   languages. MSVC/GCC auto-vectorize this; RyuJIT does not.
//   equal-effort  : C++ HandSimdSum    vs C# SimdSum     -- both hand-written PSADBW.
//
// Reporting only the second would flatter C#, since C++ reaches that performance with
// no effort at all. Reporting only the first would ignore that a competent C# author
// would obviously reach for intrinsics. Publish both.
struct ScalarSum {
    static constexpr bool enabled = true;
    [[nodiscard]] static int sum(const uint8_t* s, int n) noexcept {
        int total = 0;
        for (int i = 0; i < n; ++i) total += s[i];
        return total;
    }
};

// Same _mm256_sad_epu8 kernel the C# SimdSum policy uses, so the equal-effort
// pairing really is the same algorithm on both sides.
struct HandSimdSum {
    static constexpr bool enabled = true;
    [[nodiscard]] static int sum(const uint8_t* s, int n) noexcept {
#if defined(__AVX2__)
        int i = 0;
        __m256i acc = _mm256_setzero_si256();
        const __m256i zero = _mm256_setzero_si256();
        for (; i + 32 <= n; i += 32) {
            const __m256i v = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + i));
            acc = _mm256_add_epi64(acc, _mm256_sad_epu8(v, zero));
        }
        // Register-only horizontal reduction. The previous version stored the 256-bit
        // accumulator to a stack array and read back four 8-byte lanes; a 32-byte store
        // followed by 8-byte loads cannot store-forward, costing ~12-15 cycles per
        // message. That handicapped C++ on the one pairing where the two languages were
        // close, so it is removed rather than left in.
        const __m128i lo128 = _mm256_castsi256_si128(acc);
        const __m128i hi128 = _mm256_extracti128_si256(acc, 1);
        const __m128i sum128 = _mm_add_epi64(lo128, hi128);
        const __m128i swapped = _mm_unpackhi_epi64(sum128, sum128);
        int total = static_cast<int>(_mm_cvtsi128_si64(_mm_add_epi64(sum128, swapped)));
        for (; i < n; ++i) total += s[i];
        return total;
#else
        return ScalarSum::sum(s, n);
#endif
    }
};

struct NoSum {
    static constexpr bool enabled = false;
    [[nodiscard]] static int sum(const uint8_t*, int) noexcept { return 0; }
};

namespace detail {

template <class TSum, class TTrace>
[[nodiscard]] inline uint64_t parse_one(const uint8_t* msg, int len, uint64_t h) noexcept {
    int64_t seq = 0, qty = 0, px = 0;
    int side = 0;
    int declared = -1, computed = -2;

    int pos = 0;
    while (pos < len) {
        TTrace::count(slot::kFixFields);
        const int tag_start = pos;

        int tag = 0;
        while (pos < len && msg[pos] != kEq) {
            TTrace::count(slot::kFixTagDigits);
            tag = tag * 10 + (msg[pos] - kZero);
            ++pos;
        }
        ++pos;  // '='

        TTrace::count(slot::kFixScanCalls);
        const auto* found = static_cast<const uint8_t*>(
            std::memchr(msg + pos, kSoh, static_cast<size_t>(len - pos)));
        if (found == nullptr) { TTrace::count(slot::kFixNoSoh); break; }
        const int vend = static_cast<int>(found - msg);
        TTrace::add(slot::kFixScanDistance, vend - pos);
        const uint8_t* val = msg + pos;
        const int vlen = vend - pos;

        switch (tag) {
            case 34: TTrace::count(slot::kFixCase34); seq = parse_long<TTrace>(val, vlen); break;
            case 11: TTrace::count(slot::kFixCase11); h = fold_bytes<TTrace>(h, val, vlen); break;
            case 55: TTrace::count(slot::kFixCase55); h = fold_bytes<TTrace>(h, val, vlen); break;
            case 54: TTrace::count(slot::kFixCase54); side = val[0] - kZero; break;
            case 38: TTrace::count(slot::kFixCase38); qty = parse_long<TTrace>(val, vlen); break;
            case 44: TTrace::count(slot::kFixCase44); px = parse_price6<TTrace>(val, vlen); break;
            case 10: {
                TTrace::count(slot::kFixCase10);
                declared = static_cast<int>(parse_long<TTrace>(val, vlen));
                if constexpr (TSum::enabled) {
                    TTrace::add(slot::kFixSumBytes, tag_start);
                    computed = TSum::sum(msg, tag_start) & 0xFF;
                }
                break;
            }
            default: TTrace::count(slot::kFixCaseOther); break;
        }

        pos = vend + 1;
    }

    TTrace::add(slot::kFixMix, 5);
    h = mix(h, static_cast<uint64_t>(seq));
    h = mix(h, static_cast<uint64_t>(qty));
    h = mix(h, static_cast<uint64_t>(px));
    h = mix(h, static_cast<uint64_t>(static_cast<int64_t>(side)));
    h = mix(h, declared == computed ? 1ULL : 0ULL);
    return h;
}

} // namespace detail

template <class TSum = ScalarSum, class TTrace = NullTrace>
[[nodiscard]] inline uint64_t run_fix_parse(
    const uint8_t* payload, const uint32_t* offsets, int count) noexcept {

    uint64_t h = kChecksumOffset;
    for (int m = 0; m < count; ++m) {
        TTrace::count(slot::kFixMessages);
        const int start = static_cast<int>(offsets[m]);
        const int len = static_cast<int>(offsets[m + 1]) - start;
        h = detail::parse_one<TSum, TTrace>(payload + start, len, h);
    }
    return h;
}

} // namespace bench
