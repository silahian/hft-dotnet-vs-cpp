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

759 process launches, two C++ compilers, two operating systems, one checksum oracle that
every implementation has to satisfy before a single timing is printed.

The load-bearing numbers are **intervals on the difference**, not p-values. A large p
would not distinguish "the same" from "too noisy to tell"; an interval does. Full table
in [results/EQUIVALENCE.md](results/EQUIVALENCE.md).

| workload | platform | pairing | 95% interval on (C++ − C#) | reading |
| --- | --- | --- | --- | --- |
| FIX 4.4 decode | Windows | equal-effort vs MSVC 14.44 | [−2.9%, +4.0%] | **equivalent within ±4%** |
| FIX 4.4 decode | Windows | equal-effort vs MSVC 14.51 | [−7.1%, +1.4%] | equivalent, but C++ may be 7% ahead |
| FIX 4.4 decode | Windows | **same-source** | C++ +36% (p ≤ 0.010) | **C++ wins** |
| FIX 4.4 decode | Linux | equal-effort vs GCC 13.3 | [−6.1%, +8.6%] | **unresolved** — design cannot answer it |
| L2 order book | Windows | like-for-like (no PGO either side) | [−25.4%, −13.1%] | **C++ wins** |
| L2 order book | Linux | C++ vs best C# config | [−33.1%, −21.6%] | **C++ wins** |

**"Equal-effort" means both sides hand-write the same PSADBW kernel.** "Same-source"
means both sides write the obvious scalar loop — and there C++ wins by 36% on Windows,
because MSVC auto-vectorizes it and RyuJIT does not. The parity result is real and it is
narrow: it exists on one workload, on one platform, and only after a human writes
`Vector256` on the C# side.

Strip the checksum out and measure the parser alone (`fix_parse_nosum`, oracle-gated,
same-source) and **C++ is still 5.2–9.5% ahead** (raw p = 0.036 and 0.009).

Three findings from the matrix that are worth more than the headline:

- **Stock .NET tiering is bimodal on a loop-shaped hot path, and that is worse than
  slow.** `fix_parse_safe` on default settings does not land near one number: across 11
  launches, 5 came in at 125–148 ns/msg and 6 at 966–1011 ns/msg, with nothing between.
  The published median of 965.92 is a 6-versus-5 coin flip; one launch the other way and
  it would read 147.5. Disabling `TieredCompilationQuickJitForLoops` removes the slow
  mode entirely (min 123.76, max 143.90). The honest claim is not "7× slower" — it is
  "a ~7× penalty that fires on roughly half of process starts", which for a trading
  system that restarts on failover is the more alarming of the two. The JIT disassembly
  showing the tier-0 body is at [results/jitdisasm-simdsum.txt](results/jitdisasm-simdsum.txt).
- **Removing bounds checks with `unsafe` + `fixed` bought nothing detectable.** Safe
  9.41 vs unsafe 9.56 ns/event on `jit-tc0` (p = 0.819), 8.05 vs 8.14 on `jit-default`
  (p = 1.000), 9.36 vs 9.36 on `aot` (p = 1.000). An earlier version of this README said
  `unsafe` made C# *slower*; that was a directional claim on p = 0.819, which this
  repo's own rule forbids, and it also cited `fix_parse_unsafe` — a benchmark with zero
  process launches. Both are retracted.
- **Suppressing the GC changed nothing** (1–3%, inside noise) once the hot path allocates
  zero bytes. Read this as weaker than it looks: `TryStartNoGCRegion` reserves a budget
  rather than disabling collection, and the result files written before 2026-08-06 do not
  record whether the region was actually live.

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
