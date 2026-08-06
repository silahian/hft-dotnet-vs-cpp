"""
Publishes the assembly that backs the auto-vectorization claim.

WHY THIS EXISTS. HARNESS.md and the companion article both rest on one causal claim:
MSVC auto-vectorizes the scalar CheckSum byte-sum loop and RyuJIT does not. The evidence
lives in `build/asm-msvc1444/main.cod`, and `build/` is gitignored, so no reader of the
public repo could open the artifact behind the claim.

The first version of this extractor was a grep for `vpmovzxbd`, which produced
overlapping, out-of-order context windows and, worse, led to the WRONG LOOP being
quoted. `$LL7@sum` sits inside `HandSimdSum`, where it is the auto-vectorized *scalar
tail* of the hand-written SIMD routine. The loop that actually backs the claim is
`$LL4@sum` inside `ScalarSum`. Both functions contain `vpmovzxbd`, so a grep cannot tell
them apart; only the enclosing PROC can.

So this extractor is symbol-scoped and contiguous: it finds the PROC..ENDP span for a
named mangled symbol and emits every line of it, in order, with nothing removed. A reader
who wants to check the claim can read the whole function.

Usage: python scripts/extract_asm_evidence.py
"""

from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
COD = ROOT / "build" / "asm-msvc1444" / "main.cod"
OUT = ROOT / "results" / "asm-msvc1444-evidence.txt"

# (mangled symbol, human label, what it proves)
SYMBOLS = [
    ("?sum@ScalarSum@bench@@SAHPEBEH@Z",
     "bench::ScalarSum::sum",
     "THE LOAD-BEARING ONE. This is the plain scalar loop\n"
     "      for (int i = 0; i < n; ++i) total += s[i];\n"
     "  which is the same source C# runs. MSVC turns it into a 256-bit AVX2 reduction\n"
     "  ($LL4@sum: vpmovzxbd / vpaddd), then a horizontal reduction (vphaddd,\n"
     "  vextracti128) and a scalar tail ($LL18@sum). RyuJIT leaves this loop scalar,\n"
     "  because .NET has no auto-vectorizer. That difference is the same-source FIX gap.\n"
     "  Note the MSVC source annotation directly above $LL4@sum: '; 89 : for (int i = 0;\n"
     "  i < n; ++i) total += s[i];'. That is the mapping, from the compiler itself."),
    ("?sum@HandSimdSum@bench@@SAHPEBEH@Z",
     "bench::HandSimdSum::sum",
     "The hand-written _mm256_sad_epu8 kernel (vpsadbw), used for the equal-effort\n"
     "  pairing against C#'s Vector256 policy. NOTE: this function ALSO contains\n"
     "  vpmovzxbd/vpaddd, at $LL7@sum, where MSVC auto-vectorized its sub-32-byte scalar\n"
     "  tail. That loop is NOT evidence for the same-source claim, and an earlier\n"
     "  revision of this file quoted it as though it were."),
]


def find_proc(lines: list[str], symbol: str) -> tuple[int, int] | None:
    start = None
    for i, line in enumerate(lines):
        if start is None and re.match(rf"^{re.escape(symbol)}\s+PROC", line):
            start = i
        elif start is not None and re.match(rf"^{re.escape(symbol)}\s+ENDP", line):
            return start, i
    return None


def main() -> int:
    if not COD.exists():
        sys.exit(f"{COD} not found. Run scripts/asm-cpp.cmd first.")
    lines = COD.read_text(encoding="utf-8", errors="replace").splitlines()

    out: list[str] = []
    out.append("MSVC 14.44 assembly evidence for the auto-vectorization claim")
    out.append("=" * 78)
    out.append("")
    out.append("Extracted from build/asm-msvc1444/main.cod by scripts/extract_asm_evidence.py.")
    out.append("build/ is gitignored, so the functions the claim rests on are published here.")
    out.append("")
    out.append("Each function below is the COMPLETE, CONTIGUOUS PROC..ENDP span. Nothing is")
    out.append("elided. Source-line annotations (; NN :) are MSVC's own and map to")
    out.append("src/cpp/include/fix_parse.hpp.")
    out.append("")

    for symbol, label, note in SYMBOLS:
        span = find_proc(lines, symbol)
        out.append("=" * 78)
        out.append(f"## {label}")
        out.append(f"   mangled: {symbol}")
        out.append("")
        for line in note.split("\n"):
            out.append(f"  {line}")
        out.append("")
        if span is None:
            out.append("  !! SYMBOL NOT FOUND in this listing.")
            out.append("")
            continue
        a, b = span
        body = lines[a:b + 1]
        vec = sum(1 for l in body if "vpmovzxbd" in l or "vpaddd" in l)
        sad = sum(1 for l in body if "vpsadbw" in l)
        labels = []
        for l in body:
            m = re.match(r"^(\$LL\d+@\w+):", l)
            if m:
                labels.append(m.group(1))
        out.append(f"  span: main.cod lines {a + 1}-{b + 1} ({len(body)} lines, verbatim)")
        out.append(f"  loop labels: {', '.join(labels) if labels else '(none)'}")
        out.append(f"  vpmovzxbd/vpaddd sites: {vec}    vpsadbw sites: {sad}")
        out.append("")
        out.extend(body)
        out.append("")

    OUT.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}")
    for symbol, label, _ in SYMBOLS:
        span = find_proc(lines, symbol)
        where = f"lines {span[0] + 1}-{span[1] + 1}" if span else "NOT FOUND"
        print(f"  {label}: {where}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
