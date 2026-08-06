#pragma once
#include <cstdint>
#include <cstdio>
#include <vector>

// Thin platform layer so the identical workload sources build under MSVC/Windows
// and GCC/Linux. Everything here is off the timed path except now_ns().

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#define BENCH_NOINLINE __declspec(noinline)
#else
#include <sched.h>
#include <sys/resource.h>
#include <time.h>
#include <unistd.h>
#define BENCH_NOINLINE __attribute__((noinline))
#endif

namespace bench {

// Monotonic nanoseconds.
//   Windows: QueryPerformanceCounter
//   Linux:   clock_gettime(CLOCK_MONOTONIC)
// These are exactly the sources .NET's Stopwatch uses on each platform, so the two
// languages read the same clock on whichever OS the run happens on.
[[nodiscard]] inline int64_t now_ns() noexcept {
#if defined(_WIN32)
    static const double tick_ns = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return 1'000'000'000.0 / static_cast<double>(f.QuadPart);
    }();
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return static_cast<int64_t>(static_cast<double>(t.QuadPart) * tick_ns);
#else
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<int64_t>(ts.tv_sec) * 1'000'000'000LL + ts.tv_nsec;
#endif
}

[[nodiscard]] inline const char* platform_name() noexcept {
#if defined(_WIN32)
    return "windows";
#else
    return "linux";
#endif
}

inline void sleep_ms(int ms) noexcept {
#if defined(_WIN32)
    Sleep(static_cast<DWORD>(ms));
#else
    timespec ts{ms / 1000, (ms % 1000) * 1'000'000L};
    nanosleep(&ts, nullptr);
#endif
}

inline bool pin_current_process_to_core(int core) {
#if defined(_WIN32)
    const HANDLE proc = GetCurrentProcess();
    if (!SetPriorityClass(proc, HIGH_PRIORITY_CLASS))
        std::fprintf(stderr, "[warn] SetPriorityClass failed (%lu)\n", GetLastError());
    const DWORD_PTR mask = static_cast<DWORD_PTR>(1ULL) << core;
    if (!SetProcessAffinityMask(proc, mask)) {
        std::fprintf(stderr, "[warn] could not pin to core %d (%lu)\n", core, GetLastError());
        return false;
    }
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    return true;
#else
    cpu_set_t set;
    CPU_ZERO(&set);
    CPU_SET(core, &set);
    if (sched_setaffinity(0, sizeof(set), &set) != 0) {
        std::fprintf(stderr, "[warn] sched_setaffinity(core=%d) failed\n", core);
        return false;
    }
    // Best effort; without CAP_SYS_NICE this silently stays at the default.
    setpriority(PRIO_PROCESS, 0, -10);
    return true;
#endif
}

// Windows reports hybrid P/E classes directly. Linux exposes nothing equivalent
// under WSL2, so the caller falls back to the timing probe there.
[[nodiscard]] inline std::vector<int> core_efficiency_classes(int core_count) {
    std::vector<int> out(static_cast<size_t>(core_count), -1);
#if defined(_WIN32)
    DWORD len = 0;
    GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &len);
    if (len == 0) return out;

    std::vector<uint8_t> buf(len);
    if (!GetLogicalProcessorInformationEx(
            RelationProcessorCore,
            reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*>(buf.data()), &len))
        return out;

    DWORD off = 0;
    while (off + sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX) <= len) {
        auto* e = reinterpret_cast<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*>(buf.data() + off);
        if (e->Size == 0) break;
        if (e->Relationship == RelationProcessorCore) {
            const int eff = static_cast<int>(e->Processor.EfficiencyClass);
            for (WORD g = 0; g < e->Processor.GroupCount; ++g) {
                const KAFFINITY m = e->Processor.GroupMask[g].Mask;
                for (int b = 0; b < core_count && b < 64; ++b)
                    if (m & (static_cast<KAFFINITY>(1) << b)) out[static_cast<size_t>(b)] = eff;
            }
        }
        off += e->Size;
    }
#else
    (void)core_count;
#endif
    return out;
}

#define BENCH_STR2(x) #x
#define BENCH_STR(x) BENCH_STR2(x)

[[nodiscard]] inline const char* compiler_string() noexcept {
#if defined(__clang__)
    return "clang " __clang_version__;
#elif defined(_MSC_FULL_VER)
    return "MSVC " BENCH_STR(_MSC_FULL_VER);
#elif defined(__GNUC__)
    return "gcc " __VERSION__;
#else
    return "unknown";
#endif
}

} // namespace bench
