#!/usr/bin/env bash
# Cross-language operational equivalence check on Linux, mirroring the Windows run.
set -euo pipefail

export HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

W="$HOME/bench"
D="$W/data"
OUT="${BENCH_REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}/results"

"$W/src/csharp/Bench.Runner/bin/Release/net10.0/bench-csharp" verify \
    --data "$D" --out "$OUT/trace-csharp-linux.json" | grep -E "tuned|checksum"

"$W/build/cpp-native/bench-cpp" verify \
    --data "$D" --out "$OUT/trace-cpp-linux.json" | grep -E "checksum|heap"
