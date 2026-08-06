# C# / .NET 10 vs C++ — two HFT hot paths, measured

A reproducible benchmark comparing modern C# against C++ on two workloads that actually
look like electronic-trading infrastructure:

1. **L2 order book** — direct-indexed price ladder, quantity update plus top-of-book
   read, with rescan-outward when the touch is consumed.
2. **FIX 4.4 parse** — tag scan, integer and fixed-point decode, and full CheckSum
   (tag 10) validation, allocation-free.

Both are single-threaded, allocation-free in steady state, and written the way a
practitioner would write them in each language.

## Headline result

869 process launches in the current matrix, plus a second, fully independent 506-launch
matrix of the same Windows binaries used purely to ask whether the verdicts reproduce.
Two C++ compilers, two operating systems, one checksum oracle that every implementation
has to satisfy before a single timing is printed.

The load-bearing numbers are **intervals on the difference**, not p-values. A large p
would not distinguish "the same" from "too noisy to tell"; an interval does. Full tables:
[EQUIVALENCE.md](results/EQUIVALENCE.md), [SUMMARY.md](results/SUMMARY.md),
[REPRODUCIBILITY.md](results/REPRODUCIBILITY.md).

### What reproduces

| pairing | platform | 95% interval on (C++ − C#) | reading |
| --- | --- | --- | --- |
| FIX **same-source** | Windows | C++ +34% and +39% (p = 0.010) | **C++ wins, decisively** |
| FIX **same-source** | Linux | C++ +32% and +34% (p = 0.010) | **C++ wins, decisively** |
| L2 order book, like-for-like | Windows | [−16.9%, −9.0%] | **C++ wins** |
| L2 order book, like-for-like | Linux | [−46.6%, −29.5%] | **C++ wins** |
| L2 order book vs best C# config | Linux | [−29.3%, −5.4%] | **C++ wins** |
| FIX parser only, no checksum | Windows | [−9.3%, −2.3%] | C++ wins, narrowly |

**Same-source is the headline, and it is the most stable number in the repo**: C++ is
32–39% faster on both platforms, both compilers, and both independent matrices. The
cause is visible in the emitted assembly — MSVC auto-vectorizes the scalar byte-sum loop
(`vpmovzxbd` + `vpaddd`, published in
[results/asm-msvc1444-evidence.txt](results/asm-msvc1444-evidence.txt)) and RyuJIT does
not, because .NET has no auto-vectorizer.

### What does NOT reproduce, and why that is the interesting part

Hand-write the same AVX2 kernel on both sides (`fix_parse_simd` against
`fix_parse_safe_simd`) and the 35% gap collapses — to a size this rig cannot pin down:

| measurement | 95% interval on (C++ − C#) | says |
| --- | --- | --- |
| Windows, matrix A | [−2.9%, +4.0%] | a tie |
| Windows, matrix B | [−9.9%, −7.7%] | C++ ahead ~9% |
| Linux, GCC native | [+2.6%, +18.8%] | **C# ahead ~7%** |

Three measurements of one pairing, three different answers, including a sign flip between
two runs of identical binaries. Anything at this scale is inside the machine's own
run-to-run drift: `between_runs.py` puts the residual spread between two independent
matrices at a median of 2.7pp and a maximum of 11.1pp.

**So the honest claim is not "C# ties C++". It is: writing ~10 lines of `Vector256` turns
a reproducible 35% deficit into a difference too small for a pinned, thermally-controlled
laptop to measure consistently.** Whether the remainder favours C++ or C# depends on which
platform and which afternoon you ask.

Three findings from the matrix that are worth more than the headline:

- **Stock .NET tiering costs ~7× on a loop-shaped hot path, and it fires on most process
  starts.** `fix_parse_safe` runs at 964.13 ns/msg on default settings against 124.85
  with `TieredCompilationQuickJitForLoops` disabled — **7.72×**. Across the two
  independent matrices the slow mode hit **17 of 22 launches**; matrix A was bimodal (5
  fast at 125–148, 6 slow at 966–1011, nothing between) and matrix B was slow on all 11.
  For a trading system that reloads on failover, "usually 7× slower, occasionally not" is
  a worse property than a constant 7×. The tier-0 disassembly is at
  [results/jitdisasm-simdsum.txt](results/jitdisasm-simdsum.txt).
  **Microsoft's own documentation says this knob defaults to off** — *"If you omit this
  setting, quick JIT is not used for methods that contain loops"* — but that page carries
  `ms.date: 2021-10-29`, and the runtime source sets the default to `1` whenever
  `FEATURE_ON_STACK_REPLACEMENT` is defined, which it is. The measurement agrees with the
  source, not the doc.
- **Removing bounds checks with `unsafe` + `fixed` never made C# faster, and sometimes
  made it measurably slower.** Matrix A found no detectable difference (p = 0.819, 1.000,
  1.000). Matrix B found `unsafe` **slower** on two configs of three: 8.42 vs 9.33
  ns/event on `jit-tc0` (p = 0.001) and 7.69 vs 8.63 on `jit-default` (p = 0.018), with
  `aot` indifferent (p = 0.312). Pinning inhibits optimizations that a plain managed `ref`
  does not, and the safe, bounds-checked code was never the loser. (An earlier README also
  cited `fix_parse_unsafe` here — a benchmark with zero process launches. Retracted.)
- **Suppressing the GC changed nothing** once the hot path allocates zero bytes:
  −2.2% on `fix_parse_safe_simd` (p = 0.669) and +0.2% on `orderbook_l2_opt2` (p = 0.730).
  Note that `TryStartNoGCRegion` reserves a budget rather than disabling collection; the
  result files now record `noGcActive` and the latency mode sampled *inside* the timed
  region, so this is no longer taken on trust.

## What makes this comparison trustworthy

- **Both languages load the same binary fixture files.** No per-language data generation.
- **A checksum oracle gates every run.** Every implementation folds all intermediate
  results into a 64-bit checksum; a run is invalid unless every implementation — across
  MSVC 14.44, MSVC 14.51, GCC 13.3, RyuJIT and NativeAOT, on Windows and Linux — produces
  a bit-identical value. `analyze.py` refuses to print timings if the oracle fails.
- **The observation is a process launch, not a series.** Series inside one process share
  a thermal state; treating them as independent samples produced a 1.4% "result" against
  a 6.5% run-to-run noise floor. Verdicts come from a permutation test on the difference
  of medians, Holm-corrected across cells — never from eyeballing interval overlap.
- **One benchmark per process, randomized order.** Otherwise the language that runs more
  benchmarks per process carries more heat, and position confounds with language.
- **Canonical comparisons are declared in source before the data is seen**, so "best C#"
  cannot be quietly selected from six correlated rows against one C++ row.
- **Workload realism is measured, not asserted** (`bench-csharp stats`) — and when the
  measurement disagreed with the claim, the claim lost. HARNESS.md §7 asserted a 130 KB
  order-book footprint until the replay showed 1.4 KB; see §7.1 and limitation 9.
- **35 trace counters prove the two languages do the same work**, not merely produce the
  same answer. They cover every branch, loop iteration and switch arm of the parse and
  ladder loops, and match exactly across both languages and both operating systems. They
  do **not** cover the checksum kernel, the SOH-scan implementation, array zeroing, or
  bounds checks — those were audited by reading the source, and identical counters do not
  by themselves imply identical work.

Full methodology, including two corrections where earlier versions of this harness were
wrong, is in [HARNESS.md](HARNESS.md).

## Layout

```text
data/                 generated binary fixtures, shared by both languages (not committed --
                      run `bench-csharp gen` to rebuild them, ~44 MB)
src/csharp/           Bench.Core (workloads + harness), Bench.Runner (CLI)
src/cpp/              header-only workloads + harness, portable across MSVC and GCC
scripts/              build + run + analyze (Windows)
scripts/linux/        setup + build + run (WSL / Linux)
results/              per-process JSON + SUMMARY.md
```

The C# and C++ workload sources are deliberately line-for-line counterparts —
`OrderBookWorkload.cs` against `order_book.hpp`, `FixParseWorkload.cs` against
`fix_parse.hpp` — so they can be diffed by eye.

## Reproducing

Windows:

```
scripts\build-cpp.cmd 2022          rem MSVC 14.44
scripts\build-cpp.cmd 18            rem MSVC 14.51
scripts\build-csharp.cmd            rem JIT + NativeAOT
src\csharp\Bench.Runner\bin\Release\net10.0\bench-csharp.exe gen
powershell -File scripts\run-matrix.ps1 -Core 11 -Launches 11
python scripts\analyze.py
```

Linux / WSL:

```
bash scripts/linux/setup.sh         # installs .NET into $HOME, no sudo
bash scripts/linux/build.sh         # g++ -O3 -march=native, plus C# JIT + AOT
bash scripts/linux/run-matrix.sh 11 40 15 11 6   # core warmup series launches cooldown
```

Pick the pinned core from `bench-cpp probe --cores 16`, which reports the Windows
`EfficiencyClass` for each core rather than assuming P-cores come first.

## Configurations

| tag | what it is |
| --- | --- |
| `cpp-msvc1444` / `cpp-msvc1451` | MSVC `/O2 /Oi /Ot /Oy /GL /Gw /GS- /arch:AVX2`, link `/LTCG` |
| `lnx-gcc-native` / `lnx-gcc-v3` | GCC 13.3 `-O3 -flto` with `-march=native` / `-march=x86-64-v3` |
| `jit-tc0` | .NET 10 with `DOTNET_TieredCompilation=0` — fully optimized, no dynamic PGO |
| `jit-qjfl` | .NET 10 with `DOTNET_TC_QuickJitForLoops=0` — tiering on, but loop bodies skip tier-0 |
| `jit-qjfl-nogc` | as `jit-qjfl`, plus `GC.TryStartNoGCRegion` held open across the timed region |
| `jit-default` | .NET 10 stock: tiered + dynamic PGO |
| `aot` | .NET 10 NativeAOT |

`jit-tc0` versus an optimizing C++ compiler without PGO is the like-for-like pairing:
neither side has profile data.

## Results

- [results/SUMMARY.md](results/SUMMARY.md) — every cell, every config, plus the
  pre-declared canonical verdicts. Regenerated by `scripts/analyze.py`.
- [results/EQUIVALENCE.md](results/EQUIVALENCE.md) — intervals on the difference, which
  is what you need to tell an equivalence result apart from an underpowered one.
  Regenerated by `scripts/equivalence.py`. **Read this before quoting any "tie".**

Read [HARNESS.md](HARNESS.md) §8 before quoting any number from this repo. In particular:
this runs on a thermally constrained laptop, the Linux runs are inside WSL2, there is no
clang, and nothing here measures GC pauses, tail latency, startup, or memory footprint.

The raw evidence is committed, not just the summary: `results/proc-*.json` is one file per
process launch, 759 of them, each with its own checksum, allocation counters and full
series vector. Re-run `python scripts/analyze.py` against them to reproduce every table
without rebuilding anything.

## Found a flaw?

The most useful thing you can do with this repo is break it. If the two implementations
diverge somewhere the oracle does not catch, if a compiler flag is unfair, or if the
statistics are wrong, open an issue — [HARNESS.md](HARNESS.md) already records two
corrections where earlier versions of this harness were measuring the wrong thing, and a
third is more likely than not.

## License

MIT. See [LICENSE](LICENSE).
