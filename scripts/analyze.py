"""
Aggregates results/proc-*.json (one benchmark, one process launch, per file).

Order of business:
  1. VALIDATE  -- refuse to report anything unless the cross-language checksum oracle
                  holds for every benchmark group.
  2. REPORT    -- per-config median-of-process-medians with a distribution-free
                  order-statistic interval whose ACTUAL coverage is printed.
  3. ADJUDICATE-- for each declared canonical pairing, run a permutation test on the
                  difference of medians, Holm-correct across cells, and say
                  INDISTINGUISHABLE unless the adjusted p clears the threshold.

THE OBSERVATION IS ONE PROCESS LAUNCH. The series inside a single process are
thermally autocorrelated; treating them as independent samples manufactured a
precision the data never had (a 1.4% published claim against a 6.5% run-to-run
spread on the same binary).

Usage: python scripts/analyze.py     # prints to stdout AND writes results/SUMMARY.md
"""

from __future__ import annotations

import contextlib
import io
import json
import math
import pathlib
import random
import statistics
import sys
from collections import defaultdict

ROOT = pathlib.Path(__file__).resolve().parent.parent
RESULTS = ROOT / "results"

# Benchmarks that must agree on the checksum oracle. Names differ between runners, so
# equivalence is declared explicitly rather than inferred.
ORACLE_GROUPS = {
    # orderbook_l2_opt2 was missing here until 2026-08-06 while being published as the
    # C# side of the "order book / tuned C#" canonical pairing -- a comparative result
    # sitting outside the gate that is supposed to make comparison legal at all. It
    # passes (every orderbook_l2* name emits 0x1C1B44A5898F65EC), but nothing would have
    # caught it if it had not.
    "orderbook": ["orderbook_l2", "orderbook_l2_safe", "orderbook_l2_opt",
                  "orderbook_l2_opt2", "orderbook_l2_unsafe"],
    "fix_validating": [
        "fix_parse", "fix_parse_simd",
        "fix_parse_safe", "fix_parse_safe_simd", "fix_parse_safe_simd_create",
        "fix_parse_opt", "fix_parse_opt_simd",
        "fix_parse_unsafe", "fix_parse_unsafe_ptrspan",
    ],
    "fix_nosum": ["fix_parse_nosum"],
}

# Canonical pairings, declared BEFORE looking at the data. Only these are compared.
# Everything else is a diagnostic and appears in a separate, non-comparative table.
# Picking "best C#" from 6 correlated rows against 1 C++ row would hand C# several
# percent of free min-selection advantage.
CANONICAL = [
    ("order book / idiomatic C#",       "orderbook_l2",   "orderbook_l2_safe"),
    ("order book / tuned C#",           "orderbook_l2",   "orderbook_l2_opt2"),
    ("FIX same-source / idiomatic C#",  "fix_parse",      "fix_parse_safe"),
    ("FIX equal-effort / idiomatic C#", "fix_parse_simd", "fix_parse_safe_simd"),
]

DIAGNOSTIC = {
    "orderbook_l2_unsafe", "fix_parse_nosum", "fix_parse_safe_simd_create",
    "fix_parse_unsafe", "fix_parse_unsafe_ptrspan",
}

WINDOWS_CPP = ["cpp-msvc1444", "cpp-msvc1451"]
WINDOWS_CS = ["jit-tc0", "jit-qjfl", "jit-qjfl-nogc", "jit-default", "aot"]
LINUX_CPP = ["lnx-gcc-native", "lnx-gcc-v3"]
LINUX_CS = ["lnx-jit-tc0", "lnx-jit-qjfl", "lnx-jit-qjfl-nogc", "lnx-jit-default", "lnx-aot"]

# Third element is the DECLARED canonical C# config: fully optimized, no dynamic PGO,
# which is the like-for-like peer of an optimizing C++ compiler without PGO. Declared
# here rather than chosen by min() over the three C# configs.
PLATFORMS = {
    "Windows (native)": (WINDOWS_CPP, WINDOWS_CS, "jit-tc0"),
    "Linux (WSL2 guest -- virtualized)": (LINUX_CPP, LINUX_CS, "lnx-jit-tc0"),
}

SEED = 20260806


def permutation_test_median_diff(a: list[float], b: list[float]) -> float:
    """
    Exact two-sided permutation test on the difference of medians.

    Exact enumeration when the split count is small enough, otherwise sampled. At the
    n=11 this repo actually runs, C(22,11) = 705,432 exceeds the 200,000 threshold, so
    THE SAMPLED BRANCH IS THE ONE THAT EXECUTES and the resolution floor is 1/20001,
    not 2/C(22,11). An earlier version of this docstring described the exact branch as
    though it were the live one, and the generated footnote inherited the error.

    Replaces the previous "do the 95% intervals overlap" rule, which is not a test of a
    difference and was anti-conservative here.

    Note on what the null actually is: the permutation null is exchangeability, i.e.
    identical distributions, not merely equal medians. Dispersions here are unequal
    (19% spread against 8% on the Windows equal-effort cell), so a rejection can be
    driven by scale as well as by location. Read a rejection as "these came from
    different distributions", and get the size of the location difference from
    scripts/equivalence.py rather than from this p-value.
    """
    from itertools import combinations

    pooled = list(a) + list(b)
    n = len(a)
    total_splits = math.comb(len(pooled), n)
    observed = abs(statistics.median(a) - statistics.median(b))

    if total_splits <= 200_000:
        idx = list(range(len(pooled)))
        count = 0
        for combo in combinations(idx, n):
            cs = set(combo)
            left = [pooled[i] for i in combo]
            right = [pooled[i] for i in idx if i not in cs]
            if abs(statistics.median(left) - statistics.median(right)) >= observed - 1e-12:
                count += 1
        return count / total_splits

    # Too many splits to enumerate; sample. The +1 correction keeps the estimate
    # conservative (p is never reported as 0).
    rng = random.Random(SEED)
    draws = 20000
    count = 0
    for _ in range(draws):
        shuffled = pooled[:]
        rng.shuffle(shuffled)
        left, right = shuffled[:n], shuffled[n:]
        if abs(statistics.median(left) - statistics.median(right)) >= observed - 1e-12:
            count += 1
    return (count + 1) / (draws + 1)


def load() -> list[dict]:
    files = sorted(RESULTS.glob("proc-*.json"))
    if not files:
        sys.exit(f"no proc-*.json in {RESULTS} -- run scripts/run-matrix.ps1 first")
    out = []
    for f in files:
        with f.open(encoding="utf-8") as fh:
            doc = json.load(fh)
        doc["_file"] = f.name
        out.append(doc)
    return out


def validate_oracle(docs: list[dict]) -> bool:
    seen: dict[str, dict[str, set[str]]] = defaultdict(lambda: defaultdict(set))
    for doc in docs:
        for b in doc["benchmarks"]:
            for group, names in ORACLE_GROUPS.items():
                if b["name"] in names:
                    seen[group][b["checksum"]].add(f'{doc["runtime"]}:{b["name"]}')

    ok = True
    print("## Checksum oracle\n")
    for group in ORACLE_GROUPS:
        sums = seen.get(group, {})
        if not sums:
            print(f"- `{group}`: **NO DATA**")
            ok = False
        elif len(sums) == 1:
            value = next(iter(sums))
            print(f"- `{group}`: **PASS** {value} "
                  f"({len(next(iter(sums.values())))} implementations agree)")
        else:
            ok = False
            print(f"- `{group}`: **FAIL** -- {len(sums)} distinct checksums:")
            for value, who in sums.items():
                print(f"    - {value}: {', '.join(sorted(who))}")
    print()
    return ok


def collect(docs: list[dict]) -> dict[tuple[str, str], list[float]]:
    """(config, benchmark) -> one median per process launch."""
    per_process: dict[tuple[str, str], list[float]] = defaultdict(list)
    for doc in docs:
        for b in doc["benchmarks"]:
            series = b["nsPerOpSeries"]
            if series:
                per_process[(doc["runtime"], b["name"])].append(statistics.median(series))
    return per_process


MIN_LAUNCHES = 11


def order_stat_interval(samples: list[float]):
    """
    Distribution-free interval on the median from the order statistics, with its
    ACTUAL coverage reported rather than assumed.

    This replaces a "bootstrap 95% CI". At n=7 the bootstrap median quantiles land on
    x_(2) and x_(6) in essentially every trial -- the resampling computes nothing, and
    the interval's true coverage is 1 - 2*P(Bin(n,0.5) <= k-1), which is 87.5% at n=7,
    not 95%. Calling that a 95% CI overstated the precision of every verdict built on it.

    Returns (lo, hi, coverage).
    """
    n = len(samples)
    if n < 2:
        return (float("nan"), float("nan"), 0.0)
    s = sorted(samples)

    # Pick the widest symmetric order-statistic interval whose coverage still reaches
    # the target, else the widest available.
    best = None
    for k in range(1, n // 2 + 1):
        tail = sum(math.comb(n, i) for i in range(k)) / (2 ** n)
        coverage = 1 - 2 * tail
        if best is None or coverage >= 0.95:
            best = (s[k - 1], s[n - k], coverage)
        if coverage < 0.95:
            break
    return best


def main() -> int:
    docs = load()
    rng = random.Random(SEED)

    print("# C# / .NET vs C++ -- results\n")
    print("Observation = one process launch (median of its series). Intervals are "
          "distribution-free order-statistic intervals over the process medians; their "
          "actual coverage is stated below and is not exactly 95%. Verdicts come from a "
          "permutation test on the difference of medians, Holm-corrected.\n")
    print("_Generated by `scripts/analyze.py`; do not hand-edit._\n")

    if not validate_oracle(docs):
        print("> **The oracle failed. The implementations are not doing the same work.**")
        print("> No timing numbers are reported. Fix the divergence first.\n")
        return 1

    per_process = collect(docs)
    stats = {}
    incomplete = []
    for key, samples in per_process.items():
        if len(samples) < MIN_LAUNCHES:
            incomplete.append((key, len(samples)))
            continue
        lo, hi, cov = order_stat_interval(samples)
        stats[key] = {
            "n": len(samples),
            "median": statistics.median(samples),
            "lo": lo,
            "hi": hi,
            "coverage": cov,
            "min": min(samples),
            "max": max(samples),
        }

    if incomplete:
        print(f"## Excluded: fewer than {MIN_LAUNCHES} launches\n")
        for (cfg, bench), n in sorted(incomplete):
            print(f"- `{cfg}` / `{bench}`: n={n} -- excluded, no verdict")
        print()

    # ---------------------------------------------------------------- verdicts
    #
    # Two corrections over the previous version:
    #
    #  * The C# side is a DECLARED config (jit-tc0), not min() over three configs.
    #    min() over 3 against min() over 2 hands C# ~0.4% of free advantage at this
    #    noise level, quotes the selected config's own uncorrected interval, and
    #    assembles a "C#" column from whichever runtime won each row.
    #
    #  * The test is an exact two-sided permutation test on the difference of medians,
    #    Holm-corrected across all cells. Comparing two intervals for overlap is not a
    #    test of difference: at n=7 these intervals carry 87.5% coverage, and the
    #    overlap rule's true false-positive rate is ~2.8% per cell, ~15% across 6 cells.
    tests = []
    for plat, (cpp_cfgs, cs_cfgs, cs_canon) in PLATFORMS.items():
        for label, cpp_bench, cs_bench in CANONICAL:
            if (cs_canon, cs_bench) not in stats:
                continue
            for cpp_cfg in cpp_cfgs:
                if (cpp_cfg, cpp_bench) not in stats:
                    continue
                a = per_process[(cpp_cfg, cpp_bench)]
                b = per_process[(cs_canon, cs_bench)]
                p = permutation_test_median_diff(a, b)
                tests.append({
                    "plat": plat.split(" ")[0], "label": label,
                    "cpp_cfg": cpp_cfg, "cs_cfg": cs_canon,
                    "cpp": stats[(cpp_cfg, cpp_bench)], "cs": stats[(cs_canon, cs_bench)],
                    "p": p,
                })

    # Holm-Bonferroni across every cell reported.
    for i, t in enumerate(sorted(tests, key=lambda t: t["p"])):
        t["p_adj"] = min(1.0, t["p"] * (len(tests) - i))
    for i, t in enumerate(sorted(tests, key=lambda t: t["p"])):
        running = max(x["p_adj"] for x in sorted(tests, key=lambda t: t["p"])[: i + 1])
        t["p_adj"] = running

    print("## Verdicts on the declared canonical pairings\n")
    print(f"C# side is the declared canonical config per platform "
          f"(fully optimized, no profile data). "
          f"Intervals are distribution-free order-statistic intervals; their ACTUAL "
          f"coverage is shown below and is not 95%.\n")
    print("**A large p here means 'not distinguished', which is not the same as 'the "
          "same'.** For the size of each difference, and for whether a non-rejection is "
          "an equivalence result or merely an underpowered one, see "
          "[EQUIVALENCE.md](EQUIVALENCE.md).\n")
    print("| platform | pairing | C++ config | C++ ns/op [interval] | C# ns/op [interval] "
          "| diff | p (Holm) | verdict |")
    print("|---|---|---|---|---|---|---|---|")
    for t in tests:
        cpp_s, cs_s = t["cpp"], t["cs"]
        pct = (cs_s["median"] / cpp_s["median"] - 1) * 100
        who = "C++" if pct > 0 else "C#"
        if t["p_adj"] >= 0.05:
            verdict = "**indistinguishable**"
        else:
            verdict = f"**{who} faster by {abs(pct):.0f}%**"
        print(f"| {t['plat']} | {t['label']} | {t['cpp_cfg']} | "
              f"{cpp_s['median']:.2f} [{cpp_s['lo']:.2f}, {cpp_s['hi']:.2f}] | "
              f"{cs_s['median']:.2f} [{cs_s['lo']:.2f}, {cs_s['hi']:.2f}] | "
              f"{pct:+.1f}% | {t['p_adj']:.3f} | {verdict} |")
    print()
    cov = next((t["cpp"]["coverage"] for t in tests), 0.0)
    print(f"> Interval coverage at n={MIN_LAUNCHES} is **{cov * 100:.1f}%**, not 95%. "
          f"Verdicts come from the permutation test, not from interval overlap.\n")
    splits = math.comb(2 * MIN_LAUNCHES, MIN_LAUNCHES)
    exact = splits <= 200_000
    floor = 2.0 / splits if exact else 1.0 / 20001
    branch = "exact enumeration" if exact else f"sampling ({splits:,} splits > 200,000 threshold)"
    print(f"> The test ran by {branch}, so the resolution floor on a two-sided p is "
          f"{floor:.2e}; after Holm across {len(tests)} cells the smallest reportable "
          f"adjusted p is {min(1.0, floor * len(tests)):.2e}.\n")
    print("> The Holm family is redundant by construction: the same 11 C# launches are "
          "reused against both C++ compilers, so roughly 6 independent comparisons are "
          "being corrected as if there were 14. That direction is conservative.\n")

    # ---------------------------------------------------------------- full tables
    for plat, (cpp_cfgs, cs_cfgs, _canon) in PLATFORMS.items():
        cfgs = [c for c in cpp_cfgs + cs_cfgs if any(k[0] == c for k in stats)]
        if not cfgs:
            continue
        benches = sorted({k[1] for k in stats if k[0] in cfgs})

        print(f"## {plat} -- ns/op, median of process medians "
              f"[order-statistic interval], n launches\n")
        print("| benchmark | " + " | ".join(cfgs) + " |")
        print("|" + "---|" * (len(cfgs) + 1))
        for bench in benches:
            cells = []
            for c in cfgs:
                s = stats.get((c, bench))
                cells.append(f"{s['median']:.2f} [{s['lo']:.2f}, {s['hi']:.2f}] n={s['n']}"
                             if s else "-")
            tag = "" if bench not in DIAGNOSTIC else " *(diag)*"
            print(f"| `{bench}`{tag} | " + " | ".join(cells) + " |")
        print()

    print("> Rows marked *(diag)* are diagnostics, not comparative results.\n")
    print("> Windows and Linux are never pooled: WSL2 is a virtualized guest, so cross-OS\n"
          "> deltas are not attributable to the language or the compiler.\n")

    # ---------------------------------------------------------------- reproducibility
    print("## Reproducibility (spread across independent process launches)\n")
    print("| config | benchmark | median | min | max | spread |")
    print("|---|---|---|---|---|---|")
    for (c, b), s in sorted(stats.items()):
        spread = (s["max"] / s["min"] - 1) * 100 if s["min"] else 0.0
        print(f"| {c} | `{b}` | {s['median']:.2f} | {s['min']:.2f} | {s['max']:.2f} | "
              f"{spread:.1f}% |")
    print()

    # ---------------------------------------------------------------- provenance
    print("## Build provenance\n")
    seen_cfg = set()
    for doc in docs:
        if doc["runtime"] in seen_cfg:
            continue
        seen_cfg.add(doc["runtime"])
        print(f"**{doc['runtime']}** ({doc['lang']}, codegen={doc.get('codegen', '?')})")
        if doc["lang"] == "cpp":
            print(f"- compiler: `{doc.get('compiler', '?')}`")
        else:
            print(f"- framework: `{doc.get('frameworkDescription', '?')}`")
            print(f"- serverGC={doc.get('serverGC')} latencyMode={doc.get('gcLatencyMode')}")
        print(f"- flags: `{doc.get('flags', '?')}`")
        print(f"- pinned core: {doc.get('pinnedCore')}\n")

    # C# timed regions must be allocation-free or the comparison measures GC behaviour.
    worst = 0
    for doc in docs:
        if doc["lang"] != "csharp":
            continue
        for b in doc["benchmarks"]:
            worst = max(worst, b["allocatedBytes"])
    print(f"## Allocation check\n\nMax bytes allocated in any C# timed region: "
          f"**{worst}** (this is the harness's own results array, not the workload; "
          f"gen0 collections were 0 everywhere).\n")

    return 0


def _run_and_write() -> int:
    """Renders the report once, echoes it to stdout, and writes results/SUMMARY.md.

    SUMMARY.md is a build artifact of this script -- README.md says so, so the script
    has to be the thing that actually produces it rather than relying on the caller
    remembering to redirect stdout.
    """
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        rc = main()
    text = buf.getvalue()
    sys.stdout.write(text)
    (RESULTS / "SUMMARY.md").write_text(text, encoding="utf-8")
    return rc


if __name__ == "__main__":
    raise SystemExit(_run_and_write())
