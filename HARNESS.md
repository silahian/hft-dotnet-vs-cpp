# Measurement Protocol

Both language implementations run the **same** measurement policy: same clock, same
warmup rule, same series structure, same aggregation. Where the two runtimes genuinely
differ (JIT tiering, GC), the difference is handled explicitly and reported, not hidden.

This document has been wrong twice. Both corrections are recorded below rather than
edited out, because "what we got wrong" is the most useful part of a benchmark writeup.

## 1. Clock

| Side | API | Underlying source |
| --- | --- | --- |
| C# | `Stopwatch.GetTimestamp()` | `QueryPerformanceCounter` (Win) / `clock_gettime(CLOCK_MONOTONIC)` (Linux) |
| C++ | `bench::now_ns()` | `QueryPerformanceCounter` (Win) / `clock_gettime(CLOCK_MONOTONIC)` (Linux) |

Identical source on each platform. No cross-library calibration gap.

## 2. Input data

Both sides load the **same binary fixture files** from `data/`, produced once by a
deterministic generator (`xorshift64*`, fixed seed). No per-language data generation.
The bytes in memory are identical before the timer starts.

## 3. Correctness oracle

Every workload folds all intermediate results into a 64-bit checksum using wrapping
arithmetic. **A run is invalid unless every implementation's checksum is bit-identical.**
This proves the implementations did the same work, defeats dead-code elimination, and
catches struct-layout mismatches (a padding difference would garble the event stream).

Verified across MSVC 14.44, MSVC 14.51, GCC 13.3, RyuJIT and NativeAOT, on Windows and
Linux. Note the limit of this tool: a checksum proves the same *output*, not the same
*amount of work* — statement-level equivalence was audited separately by hand.

## 4. The observation is one process launch

```
per process:  W warmup passes (discarded), then S measured series
              observation = median of that process's S series
per config:   L independent process launches, in randomized order
              reported  = median of the L process medians, with a distribution-free
                          order-statistic interval (actual coverage is printed, and at
                          L = 11 it is 98.8%, not 95%)
```

Defaults: `W = 40`, `S = 15`, `L = 11`. `analyze.py` refuses to report any cell with
fewer than 11 launches (`MIN_LAUNCHES`), so a run with `-Launches 7` produces an empty
table rather than a weaker one.

### 4.1 CORRECTION 1 — series inside a process are not independent samples

The original protocol treated the 25 series inside a single process as the sample and
quoted their stdev. That manufactured precision the data did not have. Re-running the
**same unmodified binary** three times gave order-book medians of 6.415 / 6.343 / 6.756
ns/op — a **6.5% run-to-run spread** — while the cross-language claim being published
off that data was **1.4%**. The series within one process share a thermal and DVFS
state, so their spread measures jitter, not reproducibility.

Consequence: the process launch is the observation, `L = 11` launches per cell, and
**no directional claim is made unless a permutation test on the difference of medians
survives Holm correction across all reported cells** — everything else is reported as
*indistinguishable*, not as a small win. An earlier revision of this file gated verdicts
on non-overlapping intervals instead; that is a different and weaker test (the implied
per-comparison α is roughly 2.8%, uncorrected), so it was replaced.

### 4.2 CORRECTION 2 — warmup does not reliably reach tier-1

An earlier version claimed `W = 10` was "far past .NET's tiered-JIT promotion
thresholds". That was wrong. Each rep is *one* method call that loops over the whole
dataset, so the runtime sees ~35 invocations, not millions. Promotion depends on call
counting (~30 calls) plus a *background* compile, racing the measurement window.

JIT disassembly confirmed the mechanism directly: `SimdSum:Sum` was emitted as
`; Instrumented Tier0 code` / `; compiling with minopt`, with `CORINFO_HELP_COUNTPROFILE32`
profile-counter calls *inside* the hot loop and every vector spilled to stack. More
warmup did not converge — it oscillated (187.7 / 122.6 / 183.2 / 101.7 ns at W = 10 /
40 / 100 / 250). It is bistable, not slow to settle.

Consequence: C# is reported in three configurations, never one.

| config | meaning |
| --- | --- |
| `jit-tc0` | `DOTNET_TieredCompilation=0`. Fully optimized from the first call, and therefore **also without dynamic PGO** — the like-for-like peer of an optimizing C++ compiler without PGO. |
| `jit-default` | stock tiered + dynamic PGO. What a default deployment gets. Reported *with* its instability. |
| `aot` | NativeAOT. No JIT, no dynamic PGO. |

## 5. Load and order control

- **One benchmark per process** (`--only`). Previously the C# runner executed 8
  benchmarks and the C++ runner 4 in a single process, so C# carried ~5x the sustained
  thermal load and its later benchmarks were measured deeper into the heat curve.
- **Randomized task order** (fixed seed). Previously config order was fixed with C++
  always first, perfectly confounding position with language: the same C++ process
  measured 6.225 ns/op run first and 6.970 ns/op run second — a 12% position effect.
- Affinity pinned to one **P-core**, identical for every configuration. On Windows the
  P-core set is read from `GetLogicalProcessorInformationEx` (`EfficiencyClass`), not
  assumed from enumeration order — cores 0, 1, 10, 11, 12, 13 on this part. On Linux,
  pinning is applied externally via `taskset` because .NET's `Process.ProcessorAffinity`
  returns "Permission denied" on this host while C++'s `sched_setaffinity` succeeds;
  without `taskset` the C++ process would have been pinned and the C# process roaming.

## 6. Declared canonical pairings

Declared in source **before** the data was seen (`Bench.Runner/Program.cs`, `analyze.py`),
because the FIX benchmark exposes 6 C# rows against 1 C++ row and taking "best C#" over
6 correlated draws against 1 is worth several percent of free advantage.

| pairing | C++ | C# |
| --- | --- | --- |
| order book | `orderbook_l2` | `orderbook_l2_safe` |
| FIX, same-source | `fix_parse` (scalar) | `fix_parse_safe` (scalar) |
| FIX, equal-effort | `fix_parse_simd` (hand PSADBW) | `fix_parse_safe_simd` (hand PSADBW) |

**Both FIX pairings are published.** MSVC and GCC auto-vectorize the scalar byte-sum
(verified in emitted assembly: `vpmovzxbd` + `vpaddd`, `build/asm-msvc1444/main.cod`);
RyuJIT never auto-vectorizes. Reporting only same-source ignores that a competent C#
author reaches for intrinsics; reporting only equal-effort hides that C++ got there for
free. A hand-written SIMD policy was added to the C++ side specifically so the
equal-effort row is a real comparison — it turned out to make no difference to C++
(93.4 vs 93.4 ns on MSVC), which confirms the auto-vectorizer was already doing it.

Everything else is a diagnostic and is reported in a separate, non-comparative table.

## 7. Workload realism is measured, not asserted

`bench-csharp stats` replays the generated fixtures and reports what the workload
actually does. This exists because the first two versions of the book generator did
**not** do what their comments claimed, and only replay caught it.

| | v1 (shipped, wrong) | v4 (shipped, current) |
| --- | --- | --- |
| rescans triggered | 0.26% of events | **10.72%** |
| rescan inner iterations | 0.0093 / event | **0.0471** |
| top-of-book updates | 0.28% of events | **25.40%** |
| distinct ticks touched | 250 of 4096 (6%) | **160 of 8,192 (2.0%)** |
| tick range | — | **4011 .. 4173 (163 ticks)** |
| resident in the last 10% | — | **15 cache lines (960 bytes)** |
| 95% of late accesses | — | **8 cache lines (512 bytes)** |

v1's comment said "deletes at the touch force the rescan; that is the point" — but the
rescan fired on 1 event in 382, so the branch the workload was built around was
effectively dead and the benchmark was really measuring branch-mispredict latency,
which no language can differentiate on. The generator now runs the same state machine
the consumer runs, so it always knows where the touch actually is, and the touch
performs a random walk driven by its own levels being consumed and replenished.

### 7.1 CORRECTION 3 — this table previously claimed a footprint the fixture does not have

Until 2026-08-06 the right-hand column read **"8,127 of 8,192 (99.2%)"** distinct ticks and
**"130 KB (exceeds L1)"**. Both were wrong — by 50× and 138× respectively. They were the v4
generator's *design targets*, written into the table and never replaced with the replay
output after v4 actually ran. The three behavioural rows above were correct all along, which
is precisely why the error survived: the table looked verified because most of it was.

**The correct reading is the opposite of what this section used to assert.** The restoring
bias and the hard spread cap that were added to stop the v3 generator stalling at the ladder
edge (`DataGen.cs`) also pin the touch inside a 163-tick band. The whole million-event run
lives in under 2 KB and never leaves L1.

Consequence for every order-book number in this repo: **the order-book benchmark contains no
memory hierarchy.** It measures ALU, branch and store throughput on a working set that fits
in L1 several times over. That is the configuration that maximally exposes per-element
bounds-check overhead and raw codegen quality, and it is the configuration least like a real
book under load. The order-book results are still a valid like-for-like comparison of the two
languages on identical work — they are not evidence about cache-resident behaviour in
production, and this section previously implied that they were. See limitation 9 in §8.

Multi-symbol interleaving is the fix that would put real cache pressure in the workload. It
is not built.

## 8. Known, disclosed limitations

1. **Laptop thermals.** Core Ultra 9 285H in a mobile chassis; turbo residency is not
   fixed. Mitigated by short single-benchmark processes, randomized order, cooldowns,
   many launches, and CI-gated verdicts — reduced, not eliminated.
2. **Hybrid topology.** P/E/LP-E asymmetry, mitigated by measured P-core pinning.
3. **No AVX-512.** Arrow Lake-H does not expose it. All SIMD claims are AVX2-scoped.
4. **Linux runs are inside WSL2**, a virtualized guest. Within-platform comparisons are
   sound; cross-OS deltas are not attributable to language or compiler and are never
   pooled. WSL2 measured *faster* than native Windows on comparable C++ builds, which
   is itself unexplained and is a caveat on the Windows numbers.
5. **No clang.** Installing it needs sudo, which is not available on this host. C++ is
   covered by MSVC 14.44, MSVC 14.51 and GCC 13.3.
6. **No PGO on the C++ side**, and none on the two C# headline configs either
   (`jit-tc0` and `aot` both lack dynamic PGO). Only `jit-default` has it.
7. **Two workloads only.** Nothing here measures GC pause behaviour, tail latency under
   allocation churn, startup, or memory footprint. No claim is made about them.
8. **One machine, one microarchitecture.**
9. **The order-book workload is L1-resident.** 163 distinct ticks, under 2 KB touched
   across a million events, 15 cache lines resident in the last decile. See §7.1. No
   order-book number here says anything about cache behaviour.
10. **The two C++ builds are not equally hardened-off.** MSVC gets `/GS-`; the GCC build
    does not pass `-fno-stack-protector`, and Ubuntu's g++ defaults to
    `-fstack-protector-strong`. GCC also links `memchr`/`memset` through the PLT. The
    GCC side is therefore mildly handicapped relative to MSVC. Both C++ builds still
    beat C# on the order book, so this does not flip a verdict — it means the C++
    order-book margin is a floor, not a ceiling.
11. **ISA targeting is not symmetric.** GCC gets `-march=native`; MSVC gets generic
    `/arch:AVX2`; RyuJIT compiles for the actual CPU, so the JIT configs effectively get
    `-march=native` on both platforms; NativeAOT is pinned to `x86-64-v3`. §8.6 discloses
    the PGO asymmetry; this is the ISA one.
12. **Two C++ compilers, not four.** `lnx-gcc-native` and `lnx-gcc-v3` are the same GCC
    13.3 with different `-march`. Counting them as separate compilers overstates the
    coverage.

## 9. Fairness rules applied to the implementations

- Same algorithm, same data structure, same memory layout, same element types, audited
  statement by statement.
- No standard-library strawmen. `memchr` is paired against `Span<byte>.IndexOf`, both
  handed the same remainder slice. **Corrected 2026-08-06:** this bullet used to add
  "both vectorized", which is asserted, not verified, and the shipped artifacts point the
  other way. `build/asm-msvc1444/main.cod` carries `EXTRN memchr:PROC` and two `call
  memchr` sites — under `/MT` that is an out-of-line static-CRT call, and `/Oi` does not
  intrinsify it — while RyuJIT inlines `IndexOf` into the caller. The trace counters put
  the volume at 3,000,000 scan calls per rep to find a byte a mean 5.61 positions away.
  This asymmetry runs **against C++** in the one pairing where the two languages come out
  level, so the FIX equal-effort result should be read as C++'s floor, not its ceiling.
  Settling it needs either a disassembly of the UCRT and glibc `memchr` bodies or a C++
  variant with a hand-inlined vector scan. Neither is in the repo.
- Both sides allocation-free in steady state; the C# allocation counter is published.
- Optimization flags are recorded verbatim in each result file, including the **actual**
  `DOTNET_TieredCompilation` environment value. (Previously this was read from
  `AppContext`, which reports the baked runtimeconfig and so recorded `TC=True` even for
  the `jit-tc0` runs — the artifact asserted the opposite of its own configuration.)
- The C# `SimdSum` policy uses `MemoryMarshal.GetReference` + `Vector256.LoadUnsafe`,
  which elides bounds checks. It is therefore **not** "safe code", and the earlier
  blanket claim that all primary C# implementations are safe-only is withdrawn. The
  fully bounds-checked `SimdSumCreate` is measured alongside it and is not slower.
