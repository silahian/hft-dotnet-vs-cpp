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

759 process launches, 3 compilers, 2 operating systems, one checksum oracle that every
implementation has to satisfy before a single timing is printed.

| workload | platform | best C++ | best C# | outcome |
| --- | --- | --- | --- | --- |
| FIX 4.4 decode | Windows | 89.05 ns/msg (MSVC 14.51) | 91.27 ns/msg | **statistical tie** (p = 0.579) |
| FIX 4.4 decode | Linux | 107.18 ns/msg (GCC 13.3) | 96.37 ns/msg | **statistical tie** (p = 1.000) |
| L2 order book | Windows | 7.61 ns/event (MSVC 14.44) | 8.05 ns/event | C++ ahead ~6%, intervals overlap |
| L2 order book | Linux | 5.69 ns/event (GCC 13.3) | 7.85 ns/event | **C++ ahead 38%**, clean separation |

The C# columns above are best-config, which is a post-hoc selection — the pre-declared
canonical pairings and their Holm-corrected p-values are in
[results/SUMMARY.md](results/SUMMARY.md) and are the numbers to quote.

Three things that came out of the matrix and are worth more than the headline:

- **Stock .NET tiering costs 7× on a loop-shaped hot path.** `fix_parse_safe` is 965.92
  ns/msg on default settings and 132.99 ns/msg with `TieredCompilationQuickJitForLoops`
  disabled. The JIT disassembly showing the tier-0 body is checked in at
  [results/jitdisasm-simdsum.txt](results/jitdisasm-simdsum.txt).
- **`unsafe` and pinning made C# slower, not faster,** in every variant tried
  (`orderbook_l2_unsafe`, `fix_parse_unsafe`). The safe, bounds-checked code wins.
- **Suppressing the GC entirely changes nothing** (1–3%, inside noise) once the hot path
  allocates zero bytes — which the harness proves rather than assumes.

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
- **Workload realism is measured, not asserted** (`bench-csharp stats`).

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
powershell -File scripts\run-matrix.ps1 -Core 11 -Launches 7
python scripts\analyze.py
```

Linux / WSL:

```
bash scripts/linux/setup.sh         # installs .NET into $HOME, no sudo
bash scripts/linux/build.sh         # g++ -O3 -march=native, plus C# JIT + AOT
bash scripts/linux/run-matrix.sh 11 40 15 7 6
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

See [results/SUMMARY.md](results/SUMMARY.md), regenerated by `scripts/analyze.py`.

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
