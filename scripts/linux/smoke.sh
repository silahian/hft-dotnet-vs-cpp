#!/usr/bin/env bash
# Quick cross-platform oracle check: every Linux binary must reproduce the exact
# checksums the Windows binaries produced, or the two platforms are not running
# the same computation and nothing can be compared.
set -euo pipefail

export HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

W="$HOME/bench"
D="$W/data"

echo "=== g++ -march=native ==="
"$W/build/cpp-native/bench-cpp" run --data "$D" --core 11 --warmup 5 --series 5 \
    --tag smoke --out /tmp/s-gcc.json

echo "=== C# JIT ==="
"$W/src/csharp/Bench.Runner/bin/Release/net10.0/bench-csharp" run --data "$D" --core 11 \
    --warmup 5 --series 5 --tag smoke --out /tmp/s-jit.json

echo "=== C# NativeAOT ==="
"$W/build/aot/bench-csharp" run --data "$D" --core 11 --warmup 5 --series 5 \
    --tag smoke --out /tmp/s-aot.json

echo ""
echo "expected from Windows runs:"
echo "  orderbook       0xD5066EF3C4DFE61A"
echo "  fix (validating) 0x5D30C4CE35A0F200"
echo "  fix_nosum       0xFDD391470268D3D8"
