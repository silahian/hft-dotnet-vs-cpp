#!/usr/bin/env bash
# Copies the source tree onto the Linux filesystem (building on /mnt/c is slow and
# has locking quirks) and builds every Linux configuration.
#
# GCC gets -march=native deliberately: the point of this cross-check is whether the
# BEST available C++ beats C#, so the C++ side is steelmanned rather than matched
# flag-for-flag to MSVC's /arch:AVX2.
set -euo pipefail

export HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Derived from this script's own location so the repo works from any checkout path;
# override with BENCH_REPO when staging from somewhere else.
WIN_ROOT="${BENCH_REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
WORK="$HOME/bench"

echo "[build] staging $WIN_ROOT -> $WORK"
rm -rf "$WORK"
mkdir -p "$WORK/build" "$WORK/results"
cp -r "$WIN_ROOT/src" "$WORK/"
cp -r "$WIN_ROOT/data" "$WORK/"
cp "$WIN_ROOT/HARNESS.md" "$WORK/"
cp "$WIN_ROOT/global.json" "$WORK/"
# Windows-only build intermediates must not leak into the Linux build.
rm -rf "$WORK"/src/csharp/*/bin "$WORK"/src/csharp/*/obj

# -fno-stack-protector matches MSVC's /GS-. Ubuntu's g++ defaults to
# -fstack-protector-strong, so without this the GCC build carries a per-function guard
# the MSVC build does not, and the two C++ configs are not equally tuned. Added
# 2026-08-06; the results committed before that date were produced without it, which
# handicapped GCC slightly.
CXXBASE="-std=c++20 -O3 -DNDEBUG -fomit-frame-pointer -flto=auto -fno-stack-protector -U_FORTIFY_SOURCE"

build_cpp () {
    local tag="$1" arch="$2"
    local flags="$CXXBASE $arch"
    local out="$WORK/build/cpp-$tag"
    mkdir -p "$out"
    echo "[build] g++ $flags"
    g++ $flags -DBENCH_FLAGS="\"g++ $flags\"" \
        -I"$WORK/src/cpp/include" "$WORK/src/cpp/src/main.cpp" -o "$out/bench-cpp"
    echo "[ok] $out/bench-cpp"
}

build_cpp native "-march=native"
build_cpp v3     "-march=x86-64-v3"

cd "$WORK"

echo "[build] C# JIT"
dotnet build src/csharp/Bench.Runner/Bench.Runner.csproj -c Release -v q --nologo

echo "[build] C# NativeAOT"
if dotnet publish src/csharp/Bench.Runner/Bench.Runner.csproj -c Release -r linux-x64 \
        -p:PublishAot=true -o build/aot -v q --nologo 2>&1 | tail -5; then
    echo "[ok] NativeAOT built"
else
    echo "[warn] NativeAOT unavailable (needs clang + zlib1g-dev, which need sudo); continuing"
fi

echo "[build] done"
ls -la "$WORK/build/cpp-native/bench-cpp" "$WORK/build/cpp-v3/bench-cpp"
