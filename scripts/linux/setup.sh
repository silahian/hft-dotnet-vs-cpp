#!/usr/bin/env bash
# Installs the .NET SDK into $HOME (no sudo required) and reports the toolchain.
# WSL inherits a mangled HOME from the Windows environment, so it is re-derived
# from /etc/passwd here rather than trusted.
set -euo pipefail

export HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

DOTNET_DIR="$HOME/.dotnet"

if [ ! -x "$DOTNET_DIR/dotnet" ]; then
    echo "[setup] installing .NET SDK 10.0 -> $DOTNET_DIR"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR" --no-path
else
    echo "[setup] .NET already present at $DOTNET_DIR"
fi

export PATH="$DOTNET_DIR:$PATH"

echo "[setup] HOME       = $HOME"
echo "[setup] dotnet SDKs:"
dotnet --list-sdks
echo "[setup] dotnet runtimes:"
dotnet --list-runtimes | grep NETCore || true
echo "[setup] g++        : $(g++ --version | head -1)"
echo "[setup] clang++    : $(command -v clang++ >/dev/null && clang++ --version | head -1 || echo 'not installed (needs sudo)')"
echo "[setup] kernel     : $(uname -r)"
echo "[setup] cpu        : $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | sed 's/^ //')"
echo "[setup] nproc      : $(nproc)"
