#pragma once
#include <cstdint>

// FNV-1a style 64-bit fold. Wrapping unsigned arithmetic only, so this is
// bit-identical to the C# Checksum.Mix. Cross-language correctness oracle
// and dead-code-elimination barrier.
namespace bench {

inline constexpr uint64_t kChecksumOffset = 14695981039346656037ULL;
inline constexpr uint64_t kChecksumPrime  = 1099511628211ULL;

[[nodiscard]] inline uint64_t mix(uint64_t h, uint64_t v) noexcept {
    return (h ^ v) * kChecksumPrime;
}

} // namespace bench
