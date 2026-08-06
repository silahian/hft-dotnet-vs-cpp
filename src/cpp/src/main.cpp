#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <map>
#include <new>
#include <string>
#include <vector>

#include "checksum.hpp"
#include "fix_parse.hpp"
#include "formats.hpp"
#include "harness.hpp"
#include "order_book.hpp"
#include "platform.hpp"

#ifndef BENCH_FLAGS
#define BENCH_FLAGS "unknown"
#endif

namespace fs = std::filesystem;
using namespace bench;

// Global allocation counters. The C# side reports GC.GetTotalAllocatedBytes around the
// timed region; without this the C++ side's "allocation-free" claim would rest on
// inspection alone. Replacing global operator new/delete is the standard way to observe
// every heap operation in the process.
namespace {
std::atomic<long long> g_alloc_count{0};
std::atomic<long long> g_alloc_bytes{0};
std::atomic<long long> g_free_count{0};
} // namespace

void* operator new(std::size_t n) {
    g_alloc_count.fetch_add(1, std::memory_order_relaxed);
    g_alloc_bytes.fetch_add(static_cast<long long>(n), std::memory_order_relaxed);
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void* operator new[](std::size_t n) { return operator new(n); }
void operator delete(void* p) noexcept {
    if (p) g_free_count.fetch_add(1, std::memory_order_relaxed);
    std::free(p);
}
void operator delete[](void* p) noexcept { operator delete(p); }
void operator delete(void* p, std::size_t) noexcept { operator delete(p); }
void operator delete[](void* p, std::size_t) noexcept { operator delete(p); }

namespace {

std::map<std::string, std::string> parse_args(int argc, char** argv, int from) {
    std::map<std::string, std::string> o;
    for (int i = from; i < argc; ++i) {
        std::string a = argv[i];
        if (a.rfind("--", 0) != 0) continue;
        std::string key = a.substr(2);
        if (i + 1 < argc && std::string(argv[i + 1]).rfind("--", 0) != 0) {
            o[key] = argv[++i];
        } else {
            o[key] = "true";
        }
    }
    return o;
}

std::string get(const std::map<std::string, std::string>& o, const std::string& k,
                const std::string& dflt) {
    const auto it = o.find(k);
    return it == o.end() ? dflt : it->second;
}

// Walk up from the executable until HARNESS.md is found -- same rule as the C# runner.
fs::path repo_root() {
    fs::path p = fs::current_path();
    std::error_code ec;
#if defined(_WIN32)
    wchar_t buf[MAX_PATH];
    if (GetModuleFileNameW(nullptr, buf, MAX_PATH) != 0) p = fs::path(buf).parent_path();
#else
    const fs::path self = fs::read_symlink("/proc/self/exe", ec);
    if (!ec) p = self.parent_path();
#endif
    for (fs::path d = p; !d.empty(); d = d.parent_path()) {
        if (fs::exists(d / "HARNESS.md", ec)) return d;
        if (d == d.parent_path()) break;
    }
    return fs::current_path();
}

int usage() {
    std::fprintf(stderr,
                 "usage:\n"
                 "  bench-cpp probe [--cores N]\n"
                 "  bench-cpp run   [--data DIR] [--core N] [--warmup W] [--series S]\n"
                 "                  [--reps R] [--out FILE] [--tag NAME] [--probe]\n");
    return 1;
}

int cmd_probe(const std::map<std::string, std::string>& o) {
    const int cores = std::atoi(get(o, "cores", "16").c_str());
    const auto eff = core_efficiency_classes(cores);
    const int max_eff = *std::max_element(eff.begin(), eff.end());
    const bool have_eff = max_eff >= 0;

    std::printf("platform=%s  probing %d logical cores (lower ns/iter = faster core)\n",
                platform_name(), cores);
    if (have_eff)
        std::printf("EfficiencyClass: %d = performance core, 0 = most efficient\n", max_eff);
    else
        std::printf("EfficiencyClass unavailable on this platform; ranking by timing only\n");

    auto ns = probe_cores(cores);

    std::vector<std::pair<int, double>> ranked;
    for (int i = 0; i < cores; ++i) ranked.emplace_back(i, ns[static_cast<size_t>(i)]);
    std::sort(ranked.begin(), ranked.end(),
              [](const auto& a, const auto& b) { return a.second < b.second; });

    for (size_t i = 0; i < ranked.size(); ++i) {
        const int c = ranked[i].first;
        if (have_eff)
            std::printf("  rank %2zu  core %2d  effClass=%d %-4s %8.4f ns/iter\n", i, c,
                        eff[static_cast<size_t>(c)],
                        eff[static_cast<size_t>(c)] == max_eff ? "[P]" : "[E]", ranked[i].second);
        else
            std::printf("  rank %2zu  core %2d  %8.4f ns/iter\n", i, c, ranked[i].second);
    }

    std::printf("fastest core = %d\n", ranked.front().first);

    if (have_eff) {
        std::printf("performance cores: ");
        for (int i = 0; i < cores; ++i)
            if (eff[static_cast<size_t>(i)] == max_eff) std::printf("%d ", i);
        std::printf("\n");
    }
    return 0;
}

// Runs the operation-counting specialization and emits the counter vector, which must
// match the C# runner's element for element. This is what proves the two languages
// perform the same OPERATIONS, not merely produce the same output.
int cmd_verify(const std::map<std::string, std::string>& o) {
    const fs::path root = repo_root();
    const fs::path data_dir = get(o, "data", (root / "data").string());
    const fs::path out_file = get(o, "out", (root / "results" / "trace-cpp.json").string());

    const auto book = load_book_events((data_dir / "book_events.bin").string());
    const auto fix = load_fix_messages((data_dir / "fix_messages.bin").string());
    std::vector<int64_t> bid(static_cast<size_t>(book.tick_count));
    std::vector<int64_t> ask(static_cast<size_t>(book.tick_count));

    CountTrace::reset();

    // Snapshot the allocation counters around the workloads: this is the C++ analogue
    // of the C# GC.GetTotalAllocatedBytes delta, so "allocation-free" is measured on
    // both sides rather than asserted on one.
    const long long alloc_before = g_alloc_count.load();
    const long long bytes_before = g_alloc_bytes.load();
    const long long free_before = g_free_count.load();

    const uint64_t book_sum = run_order_book<CountTrace>(
        book.events.data(), book.events.size(), bid.data(), ask.data(), book.tick_count);
    const uint64_t fix_sum = run_fix_parse<ScalarSum, CountTrace>(
        fix.payload.data(), fix.offsets.data(), fix.count);

    const long long allocs = g_alloc_count.load() - alloc_before;
    const long long abytes = g_alloc_bytes.load() - bytes_before;
    const long long frees = g_free_count.load() - free_before;

    std::string s;
    char buf[256];
    auto line = [&](const char* fmt, auto... args) {
        std::snprintf(buf, sizeof(buf), fmt, args...);
        s += buf;
    };

    s += "{\n  \"lang\": \"cpp\",\n";
    line("  \"orderbookChecksum\": \"0x%016llX\",\n", static_cast<unsigned long long>(book_sum));
    line("  \"fixChecksum\": \"0x%016llX\",\n", static_cast<unsigned long long>(fix_sum));
    line("  \"heapAllocations\": %lld,\n", allocs);
    line("  \"heapAllocatedBytes\": %lld,\n", abytes);
    line("  \"heapFrees\": %lld,\n", frees);
    s += "  \"counters\": {\n";
    const int n_names = static_cast<int>(sizeof(slot::kNames) / sizeof(slot::kNames[0]));
    for (int i = 0; i < n_names; ++i) {
        line("    \"%s\": %lld%s\n", slot::kNames[i].name,
             static_cast<long long>(CountTrace::values[slot::kNames[i].slot]),
             i == n_names - 1 ? "" : ",");
    }
    s += "  }\n}\n";

    fs::create_directories(out_file.parent_path());
    std::FILE* f = open_binary(out_file.string(), "wb");
    if (f == nullptr) {
        std::fprintf(stderr, "[error] cannot write %s\n", out_file.string().c_str());
        return 2;
    }
    std::fwrite(s.data(), 1, s.size(), f);
    std::fclose(f);

    for (int i = 0; i < n_names; ++i)
        std::printf("%-28s %16lld\n", slot::kNames[i].name,
                    static_cast<long long>(CountTrace::values[slot::kNames[i].slot]));
    std::printf("orderbook checksum 0x%016llX\n", static_cast<unsigned long long>(book_sum));
    std::printf("fix checksum       0x%016llX\n", static_cast<unsigned long long>(fix_sum));
    std::printf("heap allocations   %lld (%lld bytes), frees %lld\n", allocs, abytes, frees);
    std::printf("-> %s\n", out_file.string().c_str());
    return 0;
}

int cmd_run(const std::map<std::string, std::string>& o) {
    const fs::path root = repo_root();
    const fs::path data_dir = get(o, "data", (root / "data").string());
    const int core = std::atoi(get(o, "core", "2").c_str());
    const int warmup = std::atoi(get(o, "warmup", "40").c_str());
    const int series = std::atoi(get(o, "series", "25").c_str());
    const int reps = std::atoi(get(o, "reps", "1").c_str());
    const std::string tag = get(o, "tag", "cpp");
    const bool do_probe = o.count("probe") != 0;
    const fs::path out_file = get(o, "out", (root / "results" / ("cpp-" + tag + ".json")).string());

    pin_to_core(core);

    const auto book = load_book_events((data_dir / "book_events.bin").string());
    const auto fix = load_fix_messages((data_dir / "fix_messages.bin").string());

    // Pre-allocated and reused: the timed region allocates nothing, same as C#.
    std::vector<int64_t> bid(static_cast<size_t>(book.tick_count));
    std::vector<int64_t> ask(static_cast<size_t>(book.tick_count));

    const BookEvent* ev = book.events.data();
    const size_t n_events = book.events.size();
    int64_t* bid_p = bid.data();
    int64_t* ask_p = ask.data();
    const int tick_count = book.tick_count;

    const uint8_t* payload = fix.payload.data();
    const uint32_t* offsets = fix.offsets.data();
    const int msg_count = fix.count;

    std::vector<double> probe;
    if (do_probe) {
        probe = probe_cores(16);
        pin_to_core(core);
    }

    // --only NAME runs a single benchmark in this process. The run scripts use it so
    // every process carries the same thermal load; otherwise the C# runner's 8
    // benchmarks would heat the part far more than the C++ runner's 4, and the
    // later benchmarks in each process would be measured deeper into the heat curve.
    const std::string only = get(o, "only", "");
    auto wanted = [&](const char* name) { return only.empty() || only == name; };

    std::vector<SeriesResult> results;

    if (wanted("orderbook_l2"))
        results.push_back(measure("orderbook_l2", static_cast<int64_t>(n_events), warmup, series,
                                  reps, [&] {
                                      return run_order_book(ev, n_events, bid_p, ask_p, tick_count);
                                  }));

    // same-source pairing: identical scalar source to C#'s ScalarSum
    if (wanted("fix_parse"))
        results.push_back(measure("fix_parse", msg_count, warmup, series, reps, [&] {
            return run_fix_parse<ScalarSum>(payload, offsets, msg_count);
        }));

    // equal-effort pairing: same hand-written PSADBW kernel as C#'s SimdSum
    if (wanted("fix_parse_simd"))
        results.push_back(measure("fix_parse_simd", msg_count, warmup, series, reps, [&] {
            return run_fix_parse<HandSimdSum>(payload, offsets, msg_count);
        }));

    if (wanted("fix_parse_nosum"))
        results.push_back(measure("fix_parse_nosum", msg_count, warmup, series, reps, [&] {
            return run_fix_parse<NoSum>(payload, offsets, msg_count);
        }));

    if (results.empty()) {
        std::fprintf(stderr, "[error] --only '%s' matched no benchmark\n", only.c_str());
        return 4;
    }

    const std::string json =
        to_json(tag, compiler_string(), BENCH_FLAGS, core, results, do_probe ? &probe : nullptr);

    fs::create_directories(out_file.parent_path());
    std::FILE* f = open_binary(out_file.string(), "wb");
    if (f == nullptr) {
        std::fprintf(stderr, "[error] cannot write %s\n", out_file.string().c_str());
        return 2;
    }
    std::fwrite(json.data(), 1, json.size(), f);
    std::fclose(f);

    for (const auto& r : results) {
        auto ns = ns_per_op(r);
        std::sort(ns.begin(), ns.end());
        std::printf("%-24s median=%9.3f ns/op  min=%9.3f  checksum=0x%016llX\n", r.name.c_str(),
                    ns[ns.size() / 2], ns.front(), static_cast<unsigned long long>(r.checksum));
    }
    std::printf("-> %s\n", out_file.string().c_str());
    std::printf("sink=%016llX\n", static_cast<unsigned long long>(g_sink));
    return 0;
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) return usage();
    const std::string cmd = argv[1];
    const auto o = parse_args(argc, argv, 2);
    try {
        if (cmd == "probe") return cmd_probe(o);
        if (cmd == "verify") return cmd_verify(o);
        if (cmd == "run") return cmd_run(o);
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[error] %s\n", ex.what());
        return 3;
    }
    return usage();
}
