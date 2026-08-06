#pragma once
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "platform.hpp"

// Mirror of Bench.Core/Harness.cs. Same clock, same warmup rule, same series
// structure, same aggregation, same JSON shape.
namespace bench {

struct SeriesResult {
    std::string name;
    uint64_t checksum = 0;
    int64_t ops_per_rep = 0;
    int reps_per_series = 0;
    std::vector<double> series_ns;
};

// Volatile sink: the compiler cannot prove the stores are dead, so the workload
// survives /O2 /GL and -O3 -flto. Same role as Harness.Consume on the C# side.
inline volatile uint64_t g_sink = 0;

inline void consume(uint64_t v) noexcept { g_sink = v; }

template <class F>
[[nodiscard]] SeriesResult measure(const std::string& name, int64_t ops_per_rep,
                                   int warmup_reps, int series_count, int reps_per_series,
                                   F&& body) {
    uint64_t checksum = 0;

    for (int i = 0; i < warmup_reps; ++i) {
        checksum = body();
        consume(checksum);
    }

    SeriesResult r;
    r.name = name;
    r.ops_per_rep = ops_per_rep;
    r.reps_per_series = reps_per_series;
    r.series_ns.resize(static_cast<size_t>(series_count));

    for (int s = 0; s < series_count; ++s) {
        const int64_t t0 = now_ns();
        for (int k = 0; k < reps_per_series; ++k) {
            checksum = body();
            consume(checksum);
        }
        const int64_t t1 = now_ns();
        r.series_ns[static_cast<size_t>(s)] = static_cast<double>(t1 - t0);
    }

    r.checksum = checksum;
    return r;
}

// ---------------------------------------------------------------- host control

inline bool pin_to_core(int core) { return pin_current_process_to_core(core); }

// Seed is read through volatile so whole-program optimization cannot constant-fold
// the loop. MSVC /GL did exactly that on the first attempt -- the probe reported
// 0.0000 ns/iter because the entire spin was evaluated away at compile time.
inline volatile uint64_t g_spin_seed = 88172645463325252ULL;

// Identical spin loop and iteration count to Harness.Spin on the C# side.
BENCH_NOINLINE inline uint64_t spin(int iterations) noexcept {
    uint64_t x = g_spin_seed;
    for (int i = 0; i < iterations; ++i) {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
    }
    return x;
}

[[nodiscard]] inline std::vector<double> probe_cores(int core_count, int iterations = 40'000'000) {
    std::vector<double> result(static_cast<size_t>(core_count), -1.0);

    for (int c = 0; c < core_count; ++c) {
        if (!pin_current_process_to_core(c)) continue;
        sleep_ms(15);
        consume(spin(iterations / 8));

        const int64_t t0 = now_ns();
        const uint64_t v = spin(iterations);
        const int64_t t1 = now_ns();
        consume(v);

        result[static_cast<size_t>(c)] = static_cast<double>(t1 - t0) / iterations;
    }
    return result;
}

// ---------------------------------------------------------------- reporting

[[nodiscard]] inline std::vector<double> ns_per_op(const SeriesResult& r) {
    std::vector<double> out(r.series_ns.size());
    const double denom = static_cast<double>(r.ops_per_rep) * r.reps_per_series;
    for (size_t i = 0; i < out.size(); ++i) out[i] = r.series_ns[i] / denom;
    return out;
}

[[nodiscard]] inline std::string to_json(const std::string& runtime_tag,
                                         const std::string& compiler,
                                         const std::string& flags, int core,
                                         const std::vector<SeriesResult>& results,
                                         const std::vector<double>* core_probe) {
    std::string s;
    s.reserve(8192);
    char buf[512];

    auto line = [&](const char* fmt, auto... args) {
        std::snprintf(buf, sizeof(buf), fmt, args...);
        s += buf;
    };

    s += "{\n";
    s += "  \"lang\": \"cpp\",\n";
    line("  \"runtime\": \"%s\",\n", runtime_tag.c_str());
    s += "  \"codegen\": \"native\",\n";
    line("  \"compiler\": \"%s\",\n", compiler.c_str());
    line("  \"platform\": \"%s\",\n", platform_name());
    line("  \"flags\": \"%s\",\n", flags.c_str());
    line("  \"pinnedCore\": %d,\n", core);

    if (core_probe != nullptr) {
        s += "  \"coreProbeNsPerIter\": [";
        for (size_t i = 0; i < core_probe->size(); ++i) {
            if (i > 0) s += ", ";
            line("%.6f", (*core_probe)[i]);
        }
        s += "],\n";
    }

    s += "  \"benchmarks\": [\n";
    for (size_t i = 0; i < results.size(); ++i) {
        const auto& r = results[i];
        const auto ns = ns_per_op(r);
        s += "    {\n";
        line("      \"name\": \"%s\",\n", r.name.c_str());
        line("      \"checksum\": \"0x%016llX\",\n", static_cast<unsigned long long>(r.checksum));
        line("      \"opsPerRep\": %lld,\n", static_cast<long long>(r.ops_per_rep));
        line("      \"repsPerSeries\": %d,\n", r.reps_per_series);
        s += "      \"allocatedBytes\": 0,\n";
        s += "      \"gen0Collections\": 0,\n";
        s += "      \"nsPerOpSeries\": [";
        for (size_t k = 0; k < ns.size(); ++k) {
            if (k > 0) s += ", ";
            line("%.6f", ns[k]);
        }
        s += "]\n";
        s += (i + 1 == results.size()) ? "    }\n" : "    },\n";
    }
    s += "  ]\n}\n";
    return s;
}

} // namespace bench
