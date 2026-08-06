#!/usr/bin/env bash
# Linux counterpart of scripts/run-all.ps1. Same core, same warmup, same series,
# same rounds, same cooldown.
#
# PINNING: every binary is launched under `taskset -c $CORE`. The in-process pinning
# path is NOT relied on here -- .NET's Process.ProcessorAffinity returns "Permission
# denied" on this box while C++'s sched_setaffinity succeeds, which would have left
# C++ pinned and C# roaming across P/E cores. taskset applies the identical
# constraint to both languages from outside the process.
set -euo pipefail

CORE="${1:-11}"
WARMUP="${2:-40}"
SERIES="${3:-25}"
ROUNDS="${4:-3}"
COOLDOWN="${5:-20}"

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

echo "core=$CORE warmup=$WARMUP series=$SERIES rounds=$ROUNDS cooldown=${COOLDOWN}s"
echo "pinning: taskset -c $CORE (external, identical for both languages)"

run_one () {
    local tag="$1" round="$2" exe="$3"
    local out="$WORK/results/round${round}-${tag}.json"
    echo ""
    echo "--- round $round / $tag ---"
    taskset -c "$CORE" "$exe" run --data "$DATA" --core "$CORE" \
        --warmup "$WARMUP" --series "$SERIES" --tag "$tag" --out "$out"
    cp "$out" "$WIN_RESULTS/"
    sleep "$COOLDOWN"
}

for r in $(seq 1 "$ROUNDS"); do
    run_one "lnx-gcc-native" "$r" "$WORK/build/cpp-native/bench-cpp"
    run_one "lnx-gcc-v3"     "$r" "$WORK/build/cpp-v3/bench-cpp"

    unset DOTNET_TieredCompilation
    run_one "lnx-jit-default" "$r" "$CS"

    export DOTNET_TieredCompilation=0
    run_one "lnx-jit-tc0" "$r" "$CS"
    unset DOTNET_TieredCompilation

    if [ -x "$AOT" ]; then
        run_one "lnx-aot" "$r" "$AOT"
    fi
done

echo ""
echo "done -> $WIN_RESULTS"
