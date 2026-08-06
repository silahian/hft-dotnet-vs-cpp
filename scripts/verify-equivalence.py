"""
Diffs the operation-counter vectors emitted by the C# and C++ runners.

This is the apples-to-apples gate. The checksum oracle proves both languages produce
the same OUTPUT; it cannot prove they perform the same WORK, because a cheaper path
that reaches the same answer would pass it. The counters close that gap: every branch
taken, every loop iteration, and every switch arm is recorded, from the SAME source
that the timed path uses (specialized with NullTrace instead of CountTrace).

Any nonzero difference means the benchmark is invalid.

Usage: python scripts/verify-equivalence.py [results/trace-csharp.json] [results/trace-cpp.json]
"""

from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


def load(p: pathlib.Path) -> dict:
    if not p.exists():
        sys.exit(f"missing {p} -- run `bench-csharp verify` and `bench-cpp verify` first")
    with p.open(encoding="utf-8") as fh:
        return json.load(fh)


def main() -> int:
    cs_path = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "results" / "trace-csharp.json"
    cpp_path = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "results" / "trace-cpp.json"

    cs, cpp = load(cs_path), load(cpp_path)

    print("# Operational equivalence check\n")
    print(f"C#  : {cs_path.name}")
    print(f"C++ : {cpp_path.name}\n")

    failures = 0

    print("## Checksums\n")
    for key in ("orderbookChecksum", "fixChecksum"):
        a, b = cs.get(key), cpp.get(key)
        ok = a == b and a is not None
        failures += 0 if ok else 1
        print(f"- `{key}`: {'**MATCH**' if ok else '**MISMATCH**'} C#={a} C++={b}")
    print()

    names = list(cs["counters"].keys())
    extra = [k for k in cpp["counters"] if k not in cs["counters"]]
    missing = [k for k in names if k not in cpp["counters"]]
    if extra or missing:
        failures += 1
        print(f"**Counter sets differ.** only-C++={extra} only-C#={missing}\n")

    print("## Operation counters\n")
    print("| counter | C# | C++ | delta |")
    print("|---|---|---|---|")
    for name in names:
        a = cs["counters"][name]
        b = cpp["counters"].get(name)
        if b is None:
            continue
        d = a - b
        if d != 0:
            failures += 1
        mark = "" if d == 0 else "  **<-- DIVERGENCE**"
        print(f"| `{name}` | {a:,} | {b:,} | {d:+,}{mark} |")
    print()

    if failures:
        print(f"## RESULT: **FAIL** ({failures} divergence(s))\n")
        print("> The two implementations are NOT performing the same operations.")
        print("> Any timing comparison built on this is invalid. Fix before measuring.\n")
        return 1

    total_ops = sum(cs["counters"].values())
    print("## RESULT: **PASS**\n")
    print(f"> All {len(names)} operation counters and both checksums are identical.")
    print(f"> {total_ops:,} counted operations, zero divergence: the two implementations")
    print("> execute the same branches, the same loop iterations and the same switch arms.\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
