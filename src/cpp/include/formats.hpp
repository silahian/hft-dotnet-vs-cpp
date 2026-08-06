#pragma once
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

// Loaders for the shared binary fixtures. Layout must match Bench.Core/Formats.cs
// byte for byte -- both runtimes consume the identical files.
namespace bench {

#pragma pack(push, 1)
struct BookEvent {
    int32_t tick_index;
    int32_t side;   // 0 = bid, 1 = ask
    int64_t qty;    // 0 = delete this level
};
#pragma pack(pop)
static_assert(sizeof(BookEvent) == 16, "BookEvent must be 16 bytes to match C#");

struct BookEventFile {
    int32_t tick_count = 0;
    std::vector<BookEvent> events;
};

struct FixMessageFile {
    int32_t count = 0;
    std::vector<uint32_t> offsets;  // size count + 1
    std::vector<uint8_t> payload;
};

// Portable file open: fopen_s is MSVC-only, plain fopen trips MSVC's deprecation
// warning-as-error under /permissive-.
inline std::FILE* open_binary(const std::string& path, const char* mode) {
#if defined(_MSC_VER)
    std::FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), mode) != 0) return nullptr;
    return f;
#else
    return std::fopen(path.c_str(), mode);
#endif
}

inline std::vector<uint8_t> read_all_bytes(const std::string& path) {
    std::FILE* f = open_binary(path, "rb");
    if (f == nullptr)
        throw std::runtime_error("cannot open " + path);
    std::fseek(f, 0, SEEK_END);
    const long size = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    std::vector<uint8_t> buf(static_cast<size_t>(size));
    if (size > 0 && std::fread(buf.data(), 1, buf.size(), f) != buf.size()) {
        std::fclose(f);
        throw std::runtime_error("short read on " + path);
    }
    std::fclose(f);
    return buf;
}

inline uint32_t rd_u32(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;  // x86-64 is little-endian, matching the writer
}

inline BookEventFile load_book_events(const std::string& path) {
    constexpr int kHeader = 32;
    constexpr int kRecord = 16;
    constexpr uint32_t kMagic = 0x5645'4B42;  // 'B','K','E','V'

    auto raw = read_all_bytes(path);
    if (raw.size() < kHeader) throw std::runtime_error(path + ": truncated header");
    if (rd_u32(raw.data()) != kMagic) throw std::runtime_error(path + ": bad magic");
    if (rd_u32(raw.data() + 4) != 1u) throw std::runtime_error(path + ": bad version");

    BookEventFile out;
    const auto count = static_cast<size_t>(rd_u32(raw.data() + 8));
    out.tick_count = static_cast<int32_t>(rd_u32(raw.data() + 12));

    const size_t expected = kHeader + count * kRecord;
    if (raw.size() != expected) throw std::runtime_error(path + ": size mismatch");

    out.events.resize(count);
    std::memcpy(out.events.data(), raw.data() + kHeader, count * kRecord);
    return out;
}

inline FixMessageFile load_fix_messages(const std::string& path) {
    constexpr int kHeader = 32;
    constexpr uint32_t kMagic = 0x4D58'4946;  // 'F','I','X','M'

    auto raw = read_all_bytes(path);
    if (raw.size() < kHeader) throw std::runtime_error(path + ": truncated header");
    if (rd_u32(raw.data()) != kMagic) throw std::runtime_error(path + ": bad magic");
    if (rd_u32(raw.data() + 4) != 1u) throw std::runtime_error(path + ": bad version");

    FixMessageFile out;
    out.count = static_cast<int32_t>(rd_u32(raw.data() + 8));
    const auto payload_bytes = static_cast<size_t>(rd_u32(raw.data() + 12));
    const size_t offsets_bytes = (static_cast<size_t>(out.count) + 1) * 4;

    if (raw.size() != kHeader + offsets_bytes + payload_bytes)
        throw std::runtime_error(path + ": size mismatch");

    out.offsets.resize(static_cast<size_t>(out.count) + 1);
    std::memcpy(out.offsets.data(), raw.data() + kHeader, offsets_bytes);

    out.payload.resize(payload_bytes);
    std::memcpy(out.payload.data(), raw.data() + kHeader + offsets_bytes, payload_bytes);
    return out;
}

} // namespace bench
