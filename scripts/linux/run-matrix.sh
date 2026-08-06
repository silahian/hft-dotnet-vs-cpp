#!/usr/bin/env bash
# Linux counterpart of scripts/run-matrix.ps1: one benchmark per process, randomized
# order, many independent launches. See HARNESS.md section 4.
#
# Pinning is external (taskset): .NET's Process.ProcessorAffinity returns "Permission
# denied" on this host while C++'s sched_setaffinity succeeds, so without taskset the
# C++ process would be pinned and the C# process roaming across P/E cores.
set -euo pipefail

CORE="${1:-11}"
WARMUP="${2:-40}"
SERIES="${3:-15}"
LAUNCHES="${4:-11}"
COOLDOWN="${5:-5}"
SEED="${6:-20260806}"

export HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

WORK="$HOME/bench"
WIN_RESULTS="${BENCH_REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}/results"
DATA="$WORK/data"
CS="$WORK/src/csharp/Bench.Runner/bin/Release/net10.0/bench-csharp"
AOT="$WORK/build/aot/bench-csharp"

command -v taskset >/dev/null || { echo "[error] taskset not found"; exit 1; }
mkdir -p "$WORK/results"

# fix_parse_nosum added 2026-08-06: it is the oracle-gated, same-source pairing that
# isolates the parser from the CheckSum byte-sum, and it was Windows-only, which left the
# "strip the checksum and C++ is still ahead" reading with no cross-platform check.
CPP_BENCH="orderbook_l2 fix_parse fix_parse_simd fix_parse_nosum"
CS_BENCH="orderbook_l2_safe orderbook_l2_opt2 fix_parse_safe fix_parse_safe_simd fix_parse_nosum"

# tag|exe|benchlist|mode
#   mode: none | tc0 | qjfl | qjflnogc
CONFIGS=(
    "lnx-gcc-native|$WORK/build/cpp-native/bench-cpp|$CPP_BENCH|none"
    "lnx-gcc-v3|$WORK/build/cpp-v3/bench-cpp|$CPP_BENCH|none"
    "lnx-jit-default|$CS|$CS_BENCH|none"
    "lnx-jit-tc0|$CS|$CS_BENCH|tc0"
    "lnx-jit-qjfl|$CS|$CS_BENCH|qjfl"
    "lnx-jit-qjfl-nogc|$CS|$CS_BENCH|qjflnogc"
    "lnx-aot|$AOT|$CS_BENCH|none"
)

TASKS=()
for cfg in "${CONFIGS[@]}"; do
    IFS='|' read -r tag exe benches mode <<< "$cfg"
    [ -x "$exe" ] || { echo "[skip] missing $tag: $exe"; continue; }
    for b in $benches; do
        for i in $(seq 1 "$LAUNCHES"); do
            TASKS+=("$tag|$exe|$b|$i|$mode")
        done
    done
done

mapfile -t TASKS < <(printf '%s\n' "${TASKS[@]}" | shuf --random-source=<(yes "$SEED"))

echo "core=$CORE warmup=$WARMUP series=$SERIES launches=$LAUNCHES cooldown=${COOLDOWN}s"
echo "tasks=${#TASKS[@]} (randomized, seed=$SEED)  one benchmark per process"

n=0
for t in "${TASKS[@]}"; do
    n=$((n + 1))
    IFS='|' read -r tag exe bench launch mode <<< "$t"
    out="$WORK/results/proc-${tag}-${bench}-${launch}.json"

    unset DOTNET_TieredCompilation DOTNET_TC_QuickJitForLoops || true
    extra=()
    case "$mode" in
        tc0)      export DOTNET_TieredCompilation=0 ;;
        qjfl)     export DOTNET_TC_QuickJitForLoops=0 ;;
        qjflnogc) export DOTNET_TC_QuickJitForLoops=0; extra=(--nogc) ;;
    esac

    line=$(taskset -c "$CORE" "$exe" run --data "$DATA" --core "$CORE" \
            --warmup "$WARMUP" --series "$SERIES" --only "$bench" \
            "${extra[@]}" --tag "$tag" --out "$out" 2>/dev/null | grep median= || true)
    printf '[%4d/%d] %-20s %-24s %s\n' "$n" "${#TASKS[@]}" "$tag" "$bench" "$line"

    cp "$out" "$WIN_RESULTS/" 2>/dev/null || true
    sleep "$COOLDOWN"
done

unset DOTNET_TieredCompilation DOTNET_TC_QuickJitForLoops || true
echo ""
echo "done -> $WIN_RESULTS"
