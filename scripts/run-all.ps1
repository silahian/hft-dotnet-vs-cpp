# Runs every configuration, multiple rounds, same core, with cooldowns between
# configs so thermal drift is spread across configs instead of loading onto whichever
# one happens to run last.
#
#   powershell -File scripts\run-all.ps1 [-Core 11] [-Warmup 40] [-Series 25] [-Rounds 3]

param(
    [int]$Core     = 11,
    [int]$Warmup   = 40,
    [int]$Series   = 25,
    [int]$Rounds   = 3,
    [int]$Cooldown = 20
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null

# Warmup is identical across every configuration. It clears .NET's ~30-call tier-1
# threshold; see HARNESS.md 4.1 for why more warmup does not converge for the
# default-tiered configuration and why jit-tc0 is reported alongside it.
$configs = @(
    @{ Tag = 'cpp-msvc1444'; Exe = "$root\build\cpp-msvc1444\bench-cpp.exe";                          Env = $null },
    @{ Tag = 'cpp-msvc1451'; Exe = "$root\build\cpp-msvc1451\bench-cpp.exe";                          Env = $null },
    @{ Tag = 'jit-default';  Exe = "$root\src\csharp\Bench.Runner\bin\Release\net10.0\bench-csharp.exe"; Env = $null },
    @{ Tag = 'jit-tc0';      Exe = "$root\src\csharp\Bench.Runner\bin\Release\net10.0\bench-csharp.exe"; Env = @{ DOTNET_TieredCompilation = '0' } },
    @{ Tag = 'aot';          Exe = "$root\build\aot\bench-csharp.exe";                                Env = $null }
)

foreach ($c in $configs) {
    if (-not (Test-Path $c.Exe)) { throw "missing binary for $($c.Tag): $($c.Exe)" }
}

Write-Host "core=$Core warmup=$Warmup series=$Series rounds=$Rounds cooldown=${Cooldown}s"

for ($r = 1; $r -le $Rounds; $r++) {
    foreach ($c in $configs) {
        $out = Join-Path $results ("round{0}-{1}.json" -f $r, $c.Tag)
        Write-Host ""
        Write-Host "--- round $r / $($c.Tag) ---"

        if ($c.Env) { foreach ($k in $c.Env.Keys) { Set-Item -Path "Env:$k" -Value $c.Env[$k] } }
        try {
            & $c.Exe run --core $Core --warmup $Warmup --series $Series --tag $c.Tag --out $out
            if ($LASTEXITCODE -ne 0) { throw "$($c.Tag) exited $LASTEXITCODE" }
        }
        finally {
            if ($c.Env) { foreach ($k in $c.Env.Keys) { Remove-Item -Path "Env:$k" -ErrorAction SilentlyContinue } }
        }

        Start-Sleep -Seconds $Cooldown
    }
}

Write-Host ""
Write-Host "done -> $results"
