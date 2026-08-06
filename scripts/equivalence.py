"""
Bounds the true difference between the two languages, instead of merely failing to
reject that it is zero.

WHY THIS EXISTS. `analyze.py` reports a permutation test per canonical pairing and
prints "indistinguishable" when the adjusted p is large. That is a weak statement and
an easy one to over-read: a large p is *not* evidence of equivalence, it can equally
mean the design was too noisy to see anything. "C# ties C++" is a claim about the size
of the difference, so it needs an interval on the difference, not a failed test.

METHOD. Invert the same two-sided permutation test used in `analyze.py`. For a
candidate difference d, shift every C# observation by d and re-test against the C++
observations. The set of d that the test does NOT reject at alpha is a distribution-free
confidence interval for the true difference of medians. Reported as absolute ns and as a
percentage of the C# median.

READING IT. A narrow interval straddling zero is an equivalence result: it says the true
gap cannot be larger than the endpoints in either direction. A wide interval straddling
zero says only that this design could not resolve the question, which is a different and
much weaker statement, and it is labelled as such in the output.

Usage: python scripts/equivalence.py    # prints, and writes results/EQUIVALENCE.md
"""

from __future__ import annotations

import contextlib
import io
import json
import pathlib
import random
import statistics
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
RESULTS = ROOT / "results"

ALPHA = 0.05
SEED = 20260806
P_ITERS = 20_000       # for the headline p-value
CI_ITERS = 6_000       # per grid point when inverting the test

# (label, cpp_config, cpp_bench, cs_config, cs_bench, grid_step_ns)
# The C# side is the pre-declared canonical config from analyze.py (no profile data on
# either side), except the rows explicitly marked best-config, which are post-hoc and
# labelled as such wherever they are quoted.
PAIRINGS = [
    ("Windows  FIX equal-effort   MSVC 14.44 vs C# tc0",
     "cpp-msvc1444", "fix_parse_simd", "jit-tc0", "fix_parse_safe_simd", 0.10),
    ("Windows  FIX equal-effort   MSVC 14.51 vs C# tc0",
     "cpp-msvc1451", "fix_parse_simd", "jit-tc0", "fix_parse_safe_simd", 0.10),
    ("Linux    FIX equal-effort   GCC v3     vs C# tc0",
     "lnx-gcc-v3", "fix_parse_simd", "lnx-jit-tc0", "fix_parse_safe_simd", 0.25),
    ("Linux    FIX equal-effort   GCC native vs C# tc0",
     "lnx-gcc-native", "fix_parse_simd", "lnx-jit-tc0", "fix_parse_safe_simd", 0.50),
    ("Windows  order book         MSVC 14.44 vs C# tc0",
     "cpp-msvc1444", "orderbook_l2", "jit-tc0", "orderbook_l2_safe", 0.02),
    ("Windows  order book         MSVC 14.44 vs C# best-config",
     "cpp-msvc1444", "orderbook_l2", "jit-default", "orderbook_l2_safe", 0.02),
    ("Linux    order book         GCC native vs C# tc0",
     "lnx-gcc-native", "orderbook_l2", "lnx-jit-tc0", "orderbook_l2_safe", 0.05),
    ("Linux    order book         GCC native vs C# best-config",
     "lnx-gcc-native", "orderbook_l2", "lnx-jit-qjfl-nogc", "orderbook_l2_opt2", 0.05),
]


def observations(config: str, bench: str) -> list[float]:
    """One observation per process launch: the median of that process's series."""
    out: list[float] = []
    for path in sorted(RESULTS.glob(f"proc-{config}-{bench}-*.json")):
        doc = json.loads(path.read_text())
        for b in doc["benchmarks"]:
            if b["name"] == bench:
                out.append(statistics.median(b["nsPerOpSeries"]))
    return sorted(out)


def perm_p(a: list[float], b: list[float], iters: int) -> float:
    """Two-sided permutation test on the difference of medians."""
    observed = abs(statistics.median(a) - statistics.median(b))
    pool = list(a) + list(b)
    na = len(a)
    rng = random.Random(SEED)
    hits = 0
    for _ in range(iters):
        rng.shuffle(pool)
        if abs(statistics.median(pool[:na]) - statistics.median(pool[na:])) >= observed - 1e-12:
            hits += 1
    return (hits + 1) / (iters + 1)


def difference_interval(a: list[float], b: list[float], step: float):
    """Every shift d of the C# sample that the test does not reject, i.e. the
    confidence interval for (median(cpp) - median(cs))."""
    centre = statistics.median(a) - statistics.median(b)
    span = max(8.0, abs(centre) * 4)
    keep: list[float] = []
    d = centre - span
    while d <= centre + span:
        if perm_p(a, [x + d for x in b], CI_ITERS) > ALPHA:
            keep.append(round(d, 3))
        d += step
    return (min(keep), max(keep)) if keep else None


def classify(lo_pct: float, hi_pct: float, p: float) -> str:
    straddles = lo_pct <= 0.0 <= hi_pct
    width = hi_pct - lo_pct
    if not straddles:
        return "**separated** -- a real difference, in the direction of the sign"
    if width <= 10.0:
        return "**equivalent within the stated bound** -- the true gap cannot exceed these endpoints"
    return "**unresolved** -- interval too wide to claim equivalence, this design cannot answer it"


def main() -> int:
    print("# Bounding the difference, not just failing to reject it\n")
    print("Confidence intervals on the true difference of medians, obtained by inverting the "
          "same two-sided permutation test `analyze.py` uses. Negative means C++ is faster. "
          "Percentages are of the C# median.\n")
    print(f"alpha = {ALPHA}, seed = {SEED}, {CI_ITERS} permutations per grid point.\n")
    print("_Generated by `scripts/equivalence.py`; do not hand-edit._\n")

    print("| pairing | n | C++ | C# | observed diff | 95% interval on (C++ - C#) | reading |")
    print("|---|---|---|---|---|---|---|")

    rows = []
    for label, ccfg, cbench, scfg, sbench, step in PAIRINGS:
        a = observations(ccfg, cbench)
        b = observations(scfg, sbench)
        if not a or not b:
            print(f"| {label} | - | - | - | - | MISSING DATA | - |")
            continue
        ma, mb = statistics.median(a), statistics.median(b)
        p = perm_p(a, b, P_ITERS)
        ci = difference_interval(a, b, step)
        if ci is None:
            print(f"| {label} | {len(a)}/{len(b)} | {ma:.2f} | {mb:.2f} | "
                  f"{ma - mb:+.2f} | no shift accepted | inconclusive |")
            continue
        lo_pct, hi_pct = ci[0] / mb * 100, ci[1] / mb * 100
        reading = classify(lo_pct, hi_pct, p)
        print(f"| {label} | {len(a)}/{len(b)} | {ma:.2f} | {mb:.2f} | {ma - mb:+.2f} ns | "
              f"[{ci[0]:+.2f}, {ci[1]:+.2f}] ns = [{lo_pct:+.1f}%, {hi_pct:+.1f}%] | {reading} |")
        rows.append((label, p))

    print()
    print("## Raw two-sided p-values (unadjusted)\n")
    print("Reported for completeness. The interval above is the load-bearing number: a large p on "
          "its own would not distinguish 'the same' from 'too noisy to tell'.\n")
    for label, p in rows:
        print(f"- `{label}` -- p = {p:.4f}")

    print()
    print("> The Linux rows inherit WSL2's virtualization jitter (`fix_parse_simd` on "
          "`lnx-gcc-native` spans 101.7% across launches, per `SUMMARY.md`), which is why their "
          "intervals are wide. Width there is a property of the host, not of either language.\n")
    return 0


def _run_and_write() -> int:
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        rc = main()
    text = buf.getvalue()
    sys.stdout.write(text)
    (RESULTS / "EQUIVALENCE.md").write_text(text, encoding="utf-8")
    return rc


if __name__ == "__main__":
    raise SystemExit(_run_and_write())
