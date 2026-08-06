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

Defaults: `W = 40`, `S = 15`, `L = 7`.

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

| | v1 (shipped, wrong) | current |
| --- | --- | --- |
| rescans triggered | 0.26% of events | **10.39%** |
| rescan inner iterations | 0.0093 / event | **0.0475** |
| distinct ticks touched | 250 of 4096 (6%) | **8,127 of 8,192 (99.2%)** |
| top-of-book updates | 0.28% of events | **24.72%** |
| touched footprint | ~4 KB (L1-resident) | **130 KB (exceeds L1)** |

v1's comment said "deletes at the touch force the rescan; that is the point" — but the
rescan fired on 1 event in 382, so the branch the workload was built around was
effectively dead and the benchmark was really measuring branch-mispredict latency,
which no language can differentiate on. The generator now runs the same state machine
the consumer runs, so it always knows where the touch actually is, and the touch
performs an unbiased random walk driven by its own levels being consumed and replenished.

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

## 9. Fairness rules applied to the implementations

- Same algorithm, same data structure, same memory layout, same element types, audited
  statement by statement.
- No standard-library strawmen. `memchr` is paired against `Span<byte>.IndexOf` — both
  vectorized, both handed the same remainder slice.
- Both sides allocation-free in steady state; the C# allocation counter is published.
- Optimization flags are recorded verbatim in each result file, including the **actual**
  `DOTNET_TieredCompilation` environment value. (Previously this was read from
  `AppContext`, which reports the baked runtimeconfig and so recorded `TC=True` even for
  the `jit-tc0` runs — the artifact asserted the opposite of its own configuration.)
- The C# `SimdSum` policy uses `MemoryMarshal.GetReference` + `Vector256.LoadUnsafe`,
  which elides bounds checks. It is therefore **not** "safe code", and the earlier
  blanket claim that all primary C# implementations are safe-only is withdrawn. The
  fully bounds-checked `SimdSumCreate` is measured alongside it and is not slower.
